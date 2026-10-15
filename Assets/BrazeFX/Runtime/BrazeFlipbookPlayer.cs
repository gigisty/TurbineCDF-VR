using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>Standalone, lightweight atlas animation player rendered through the BrazeFX render feature.</summary>
    [AddComponentMenu("BrazeFX/Baking/Braze Flipbook Player", 21)]
    [DisallowMultipleComponent]
    public sealed class BrazeFlipbookPlayer : MonoBehaviour, IBrazeAnimatedTarget
    {
        public static readonly List<BrazeFlipbookPlayer> Active = new();

        public enum Blend { Alpha, Additive }
        public enum Facing { Camera, Local, World }
        public enum Playback { Once, Loop, PingPong, HoldLast }
        public enum RenderShape { Quad, Plane, Box, Sphere, Torus, Capsule }

        [System.Serializable]
        public sealed class AtlasSettings
        {
            [Tooltip("Flipbook texture atlas. Frames are read left-to-right, then top-to-bottom.")]
            public Texture2D texture;
            [Min(1)] public int columns = 5;
            [Min(1)] public int rows = 5;
            [Tooltip("Number of populated cells in the atlas. May be smaller than Columns × Rows.")]
            [Min(1)] public int frameCount;
            [Tooltip("Enable when RGB already contains alpha coverage, as produced by Export Studio.")]
            public bool premultipliedAlpha = true;
        }

        [System.Serializable]
        public sealed class PlaybackSettings
        {
            [Tooltip("Delay before the first frame, in seconds.")]
            [Min(0f)] public float delay;
            [Min(0.01f)] public float framesPerSecond = 30f;
            [Tooltip("Animation-speed multiplier.")]
            [Min(0.01f)] public float speed = 1f;
            public Playback mode = Playback.Once;
            [Tooltip("First atlas frame, zero based.")]
            [Min(0)] public int startFrame;
        }

        [System.Serializable]
        public sealed class LookSettings
        {
            [Tooltip("Mesh that receives the flipbook. Quad is an upright card; Plane lies on local XZ.")]
            public RenderShape renderShape = RenderShape.Quad;
            public Blend blend = Blend.Alpha;
            public Facing facing = Facing.Camera;
            [ColorUsage(true, true)] public Color tint = Color.white;
            [Min(0f)] public float brightness = 1f;
            [Range(0f, 1f)] public float alphaCutoff;
            public bool softParticles;
            [Min(0f)] public float softFade = 0.5f;
            // A flipbook already contains the authored growth, translation, and fade. These modifiers remain
            // serialized for backwards compatibility with deliberately stylized players, but are hidden from the
            // fidelity-first inspector and neutral by default so they cannot re-anchor baked motion around the card.
            [HideInInspector] public AnimationCurve sizeOverTime = AnimationCurve.Linear(0f, 1f, 1f, 1f);
            [HideInInspector] public AnimationCurve opacityOverTime = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }

        [Header("Player")]
        public bool playOnEnable = true;
        [Min(0f)] public float timeScale = 1f;
        // Legacy scene/prefab migration source. Export Studio originally assigned its metadata wrapper here and the
        // inspector made it look mandatory even though runtime only needs a texture, grid, frame count, and FPS.
        // Keep the serialized fields hidden so existing components migrate losslessly to the direct-atlas model.
        [SerializeField, HideInInspector] public BrazeFlipbookAsset flipbookAsset;
        [SerializeField, HideInInspector, Min(0)] public int viewIndex;

        public PlaybackSettings playback = new();
        public AtlasSettings atlas = new();
        public LookSettings look = new();

        const int LutResolution = 256;
        const int CurrentSerializedVersion = 2;

        [SerializeField, HideInInspector] int serializedVersion;

        Material material;
        Blend materialBlend;
        Texture2D sizeLut, opacityLut;
        Color[] lutScratch;
        MaterialPropertyBlock properties;
        bool lutsDirty = true;

        bool playing;
        bool visible;
        double startedAt;
        float heldTime;

#if UNITY_EDITOR
        bool editorPreview;
        float editorPreviewTime;
#endif

        public bool Ready => atlas?.texture != null && material != null && sizeLut != null && opacityLut != null;
        public int GpuBytes => LutResolution * sizeof(float) * 2;
        public bool Repeats => playback != null &&
            (playback.mode == Playback.Loop || playback.mode == Playback.PingPong);
        public bool KeepsVisibleAtEnd => playback != null && playback.mode == Playback.HoldLast;
        internal bool WantsRender => visible && Ready && HasScheduledOrVisible(PlaybackTime);

        public float Duration
        {
            get
            {
                EnsureSettings();
                ValidateSettings(playback, atlas, look);
                int frameCount = ResolvedFrameCount(atlas);
                int remaining = Mathf.Max(1, frameCount - Mathf.Clamp(playback.startFrame, 0, frameCount - 1));
                float frameSpan = playback.mode == Playback.PingPong
                    ? Mathf.Max(1f, (remaining - 1) * 2f)
                    : remaining;
                return playback.delay + frameSpan /
                    (Mathf.Max(0.01f, playback.framesPerSecond) * Mathf.Max(0.01f, playback.speed));
            }
        }

        public float PlaybackTime
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying && editorPreview) return editorPreviewTime;
#endif
                if (!playing) return heldTime;
                return heldTime + (float)(Time.timeAsDouble - startedAt) * Mathf.Max(0f, timeScale);
            }
        }

        void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) return;
#endif
            if (playOnEnable) Restart();
        }

        void OnDisable() => ReleaseResources();

        void OnValidate()
        {
            EnsureSettings();
            timeScale = Mathf.Max(0f, timeScale);
            ValidateSettings(playback, atlas, look);
            lutsDirty = true;
        }

        public void OnBrazeAnimated() => OnValidate();

        internal static void ValidateSettings(PlaybackSettings playback, AtlasSettings atlas, LookSettings look)
        {
            if (playback == null || atlas == null || look == null) return;
            atlas.columns = Mathf.Clamp(atlas.columns, 1, 1024);
            atlas.rows = Mathf.Clamp(atlas.rows, 1, 1024);
            int capacity = AtlasCapacity(atlas);
            // Zero is the migration sentinel for atlas players serialized before exact frame counts existed.
            atlas.frameCount = atlas.frameCount <= 0 ? capacity : Mathf.Clamp(atlas.frameCount, 1, capacity);
            playback.delay = Mathf.Max(0f, playback.delay);
            playback.framesPerSecond = Mathf.Max(0.01f, playback.framesPerSecond);
            playback.speed = Mathf.Max(0.01f, playback.speed);
            int frameCount = ResolvedFrameCount(atlas);
            playback.startFrame = Mathf.Clamp(playback.startFrame, 0, frameCount - 1);
            look.brightness = Mathf.Max(0f, look.brightness);
            look.softFade = Mathf.Max(0f, look.softFade);
            look.sizeOverTime ??= AnimationCurve.Linear(0f, 1f, 1f, 1f);
            look.opacityOverTime ??= AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }

        void EnsureSettings()
        {
            playback ??= new PlaybackSettings();
            atlas ??= new AtlasSettings();
            look ??= new LookSettings();
            if (serializedVersion < 1)
            {
                MigrateLegacyMotionCurves(look);
            }
            if (serializedVersion < 2) atlas.premultipliedAlpha = true;
            serializedVersion = CurrentSerializedVersion;
            if (flipbookAsset != null && flipbookAsset.IsValid)
            {
                viewIndex = Mathf.Clamp(viewIndex, 0, flipbookAsset.views.Count - 1);
                BrazeFlipbookAsset.View view = flipbookAsset.GetView(viewIndex);
                atlas.texture = view != null ? view.texture : null;
                atlas.columns = Mathf.Max(1, flipbookAsset.columns);
                atlas.rows = Mathf.Max(1, flipbookAsset.rows);
                atlas.frameCount = Mathf.Clamp(flipbookAsset.frameCount, 1, AtlasCapacity(atlas));
                atlas.premultipliedAlpha = flipbookAsset.premultipliedAlpha;
                playback.framesPerSecond = Mathf.Max(0.01f, flipbookAsset.fps);
                // Complete the migration instead of leaving a hidden reference that would keep overriding direct
                // edits. Existing serialized players become ordinary atlas players on their first validation/use.
                flipbookAsset = null;
                viewIndex = 0;
            }
        }

        /// <summary>The original Player imposed a centre-origin scale-up and a second fade on every exported atlas.
        /// Remove only those exact built-in curves; distinct authored curves are intentionally preserved.</summary>
        internal static bool MigrateLegacyMotionCurves(LookSettings value)
        {
            if (value == null) return false;
            bool changed = false;
            if (HasKeyLayout(value.sizeOverTime, new[] { 0f, 0.3f, 1f }, new[] { 0.25f, 1f, 1f }))
            {
                value.sizeOverTime = AnimationCurve.Linear(0f, 1f, 1f, 1f);
                changed = true;
            }
            if (HasKeyLayout(value.opacityOverTime, new[] { 0f, 0.8f, 1f }, new[] { 1f, 1f, 0f }))
            {
                value.opacityOverTime = AnimationCurve.Linear(0f, 1f, 1f, 1f);
                changed = true;
            }
            return changed;
        }

        static bool HasKeyLayout(AnimationCurve curve, float[] times, float[] values)
        {
            if (curve == null || curve.length != times.Length || times.Length != values.Length) return false;
            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
                if (!Mathf.Approximately(keys[i].time, times[i]) ||
                    !Mathf.Approximately(keys[i].value, values[i])) return false;
            return true;
        }

        static int AtlasCapacity(AtlasSettings value)
            => value == null ? 1 : Mathf.Max(1, value.columns) * Mathf.Max(1, value.rows);

        internal static int ResolvedFrameCount(AtlasSettings value)
            => value == null ? 1 : Mathf.Clamp(value.frameCount, 1, AtlasCapacity(value));

        [BrazeTimelineEvent("PLAYBACK/Play")]
        public void Play()
        {
            if (!Application.isPlaying || !EnsureResources()) return;
            if (!HasScheduledOrVisible(PlaybackTime))
            {
                heldTime = 0f;
                startedAt = Time.timeAsDouble;
            }
            visible = true;
            if (playing) return;
            playing = true;
            startedAt = Time.timeAsDouble;
        }

        [BrazeTimelineEvent("PLAYBACK/Pause")]
        public void Pause()
        {
            if (!Application.isPlaying || !playing) return;
            heldTime = PlaybackTime;
            playing = false;
        }

        [BrazeTimelineEvent("PLAYBACK/Restart")]
        public void Restart()
        {
            if (!Application.isPlaying || !EnsureResources()) return;
            heldTime = 0f;
            startedAt = Time.timeAsDouble;
            playing = true;
            visible = true;
        }

        [BrazeTimelineEvent("PLAYBACK/Stop")]
        public void Stop()
        {
            playing = false;
            visible = false;
            heldTime = 0f;
            BrazeRuntimeRegistry.Remove(Active, this);
        }

        bool EnsureResources(bool register = true)
        {
            EnsureSettings();
            ValidateSettings(playback, atlas, look);
            if (atlas.texture == null)
            {
                BrazeRuntimeRegistry.Remove(Active, this);
                return false;
            }
            properties ??= new MaterialPropertyBlock();

            if (material == null || materialBlend != look.blend)
            {
                DestroyOwned(material);
                Shader shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/FlipbookPlayer");
                if (shader == null)
                {
                    Debug.LogError("[BrazeFX] Shader 'BrazeFX/FlipbookPlayer' was not found.", this);
                    return false;
                }
                materialBlend = look.blend;
                material = new Material(shader)
                {
                    name = "Braze Flipbook Player",
                    hideFlags = HideFlags.DontSave,
                };
                material.SetFloat("_BlendSrc", (int)BlendMode.One);
                material.SetFloat("_BlendDst", look.blend == Blend.Alpha
                    ? (int)BlendMode.OneMinusSrcAlpha
                    : (int)BlendMode.One);
            }

            if (lutsDirty || sizeLut == null || opacityLut == null) BakeLuts();
            if (!Ready) return false;
            if (register) BrazeRuntimeRegistry.AddUnique(Active, this);
            return true;
        }

        void BakeLuts()
        {
            lutsDirty = false;
            if (sizeLut == null) sizeLut = CreateLut("BrazeFlipbook_Size");
            if (opacityLut == null) opacityLut = CreateLut("BrazeFlipbook_Opacity");
            if (lutScratch == null || lutScratch.Length != LutResolution)
                lutScratch = new Color[LutResolution];

            BakeCurve(look.sizeOverTime);
            sizeLut.SetPixels(lutScratch);
            sizeLut.Apply(false, false);
            BakeCurve(look.opacityOverTime);
            opacityLut.SetPixels(lutScratch);
            opacityLut.Apply(false, false);
        }

        void BakeCurve(AnimationCurve curve)
        {
            for (int x = 0; x < LutResolution; x++)
            {
                float value = curve == null || curve.length == 0 ? 1f : curve.Evaluate(x / (LutResolution - 1f));
                lutScratch[x] = new Color(value, 0f, 0f, 0f);
            }
        }

        static Texture2D CreateLut(string textureName) => new(LutResolution, 1, TextureFormat.RFloat, false, true)
        {
            name = textureName,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };

        bool HasScheduledOrVisible(float time)
        {
            float age = time - playback.delay;
            if (age < 0f || playback.mode != Playback.Once) return true;
            int frames = ResolvedFrameCount(atlas);
            int remaining = Mathf.Max(1, frames - Mathf.Clamp(playback.startFrame, 0, frames - 1));
            return age * playback.framesPerSecond * playback.speed < remaining;
        }

        internal bool PrepareForRender()
        {
            if (!visible || !EnsureResources(false) || !HasScheduledOrVisible(PlaybackTime)) return false;

            float time = PlaybackTime;
            Transform t = transform;
            Quaternion rotation = look.facing == Facing.World ? Quaternion.identity : t.rotation;
            Vector3 scale = t.lossyScale;
            properties.Clear();
            properties.SetFloat("_FlipbookTime", time);
            properties.SetVector("_FlipbookPositionDelay", new Vector4(
                t.position.x, t.position.y, t.position.z, playback.delay));
            properties.SetVector("_FlipbookRotation", new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
            properties.SetVector("_FlipbookScaleSpeed", new Vector4(
                Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z), playback.speed));
            properties.SetFloat("_FlipbookStartFrame", playback.startFrame);
            properties.SetFloat("_FlipbookFrameCount", ResolvedFrameCount(atlas));
            properties.SetFloat("_FlipbookPremultiplied", atlas.premultipliedAlpha ? 1f : 0f);
            properties.SetVector("_FlipbookSheet", new Vector4(
                atlas.columns, atlas.rows, playback.framesPerSecond, (float)playback.mode));
            properties.SetFloat("_FlipbookFacing", (float)look.facing);
            properties.SetColor("_FlipbookTint", look.tint);
            properties.SetFloat("_FlipbookBrightness", look.brightness);
            properties.SetFloat("_FlipbookAlphaCutoff", look.alphaCutoff);
            properties.SetFloat("_FlipbookSoftFade", look.softParticles ? look.softFade : 0f);
            properties.SetTexture("_FlipbookTexture", atlas.texture != null ? atlas.texture : Texture2D.whiteTexture);
            properties.SetTexture("_FlipbookSizeLut", sizeLut);
            properties.SetTexture("_FlipbookOpacityLut", opacityLut);
            BrazeFlipbookMeshLibrary.Get(look.renderShape);
            return true;
        }

        internal Mesh RenderMesh => BrazeFlipbookMeshLibrary.Get(look.renderShape);
        internal Material RenderMaterial => material;
        internal MaterialPropertyBlock RenderProperties => properties;

        void ReleaseResources()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            playing = false;
            visible = false;
            heldTime = 0f;
            DestroyOwned(material); material = null;
            DestroyOwned(sizeLut); sizeLut = null;
            DestroyOwned(opacityLut); opacityLut = null;
            lutsDirty = true;
#if UNITY_EDITOR
            editorPreview = false;
#endif
        }

        static void DestroyOwned(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

#if UNITY_EDITOR
        internal void EditorPropertiesChanged() => lutsDirty = true;

        internal void EditorPreviewAt(float time)
        {
            if (Application.isPlaying || !isActiveAndEnabled) return;
            editorPreview = true;
            editorPreviewTime = Mathf.Max(0f, time);
            visible = true;
            EnsureResources();
        }

        internal void EditorPreviewStop()
        {
            if (Application.isPlaying) return;
            visible = false;
            editorPreview = false;
            editorPreviewTime = 0f;
            ReleaseResources();
        }
#endif
    }
}
