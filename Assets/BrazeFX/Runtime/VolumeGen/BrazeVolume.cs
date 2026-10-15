using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A standalone procedural volume. The GENERATOR makes only a base primitive shape (box, sphere, slab, …) — no
    /// noise, no definition. The look is then art-directed by independent scene <see cref="BrazeVolumeField"/>
    /// objects (transform-driven falloff fields, like lights) — Noise, Density Add/Remove, Deform, Remap, Smooth — each
    /// a cheap bake-time GPU pass applied in Order to any volume it overlaps. It owns a dedicated
    /// <see cref="BrazeGeneratedVolumeRenderer"/>, so its look development never touches the live fire/smoke renderer.
    /// </summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Generator", 5)]
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class BrazeVolume : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum Shape { Box = 0, Sphere = 1, Cylinder = 2, Cone = 3, Torus = 4, Capsule = 5, Slab = 6 }

        // ── DOMAIN ───────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("The volume box in world units (this transform's local size). The generated field fills it.")]
        public Vector3 worldSize = new Vector3(12f, 6f, 12f);
        [Tooltip("Voxels along the LONGEST axis; the other axes match the world voxel pitch (cubic cells). A static " +
                 "volume needs no velocity/pressure buffers, so it can run higher-res than a live sim for the same VRAM.")]
        [Range(8, 512)] public int resolution = 128;

        // ── BASE SHAPE (generator — no noise) ──────────────────────────────────────────────────────────────
        [Tooltip("The base primitive the volume starts as. All structure/erosion comes from the FILTER stack below.")]
        public Shape shape = Shape.Box;
        [Tooltip("Half-size / radii of the base shape, as a fraction of the box (1 = fills the box).")]
        public Vector3 shapeSize = new Vector3(0.85f, 0.85f, 0.85f);
        [Tooltip("Extra corner/edge rounding of the base shape (fraction of the box).")]
        [Range(0f, 0.5f)] [BrazeAnimatable("SHAPE/Round", 0f, 0.5f)] public float shapeRound = 0.05f;
        [Tooltip("Tube/shell thickness for Torus and Slab (fraction of the box).")]
        [Range(0.01f, 1f)] [BrazeAnimatable("SHAPE/Thickness", 0.01f, 1f)] public float shapeThickness = 0.25f;
        [Tooltip("Soft edge width of the base shape (fraction of the box). 0 = hard edge.")]
        [Range(0f, 0.5f)] [BrazeAnimatable("SHAPE/Falloff", 0f, 0.5f)] public float falloff = 0.12f;
        [Tooltip("Density the base shape starts with (filters sculpt from here).")]
        [Min(0f)] [BrazeAnimatable("SHAPE/Base Density", 0f, 8f)] public float baseDensity = 1f;

        // ── LOOK ─────────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("Key light for the volume's self-shadowing / scatter. Empty = a default warm top-down light.")]
        public Light keyLight;
        [Tooltip("Appearance for the generator's dedicated volume renderer (not the shared fire/smoke raymarcher).")]
        public BrazeRenderSettings look = BrazeRenderSettings.Default;

        // ── CLOUD SHADING (render-side) ─────────────────────────────────────────────────────────────────
        public enum TintMode { Uniform = 0, ByDensity = 1, ByHeight = 2 }

        [ColorUsage(false, true)] public Color cloudAlbedo = Color.white;
        public TintMode tintMode = TintMode.Uniform;
        [GradientUsage(true)] public Gradient tintRamp = DefaultTintRamp();
        [Range(-0.95f, 0.95f)] [BrazeAnimatable("CLOUD SHADING/Anisotropy", -0.95f, 0.95f)] public float anisotropy = 0.35f;
        [Range(0f, 2f)] [BrazeAnimatable("CLOUD SHADING/Silver Lining", 0f, 2f)] public float silverLining = 0.6f;
        [Range(0f, 1f)] [BrazeAnimatable("CLOUD SHADING/Powder", 0f, 1f)] public float powder = 0.5f;
        [ColorUsage(false, true)] public Color skyColor = new Color(0.45f, 0.6f, 0.85f, 1f);
        [ColorUsage(false, true)] public Color groundColor = new Color(0.28f, 0.24f, 0.2f, 1f);

        // ── EMISSION (render-side glow from density) ─────────────────────────────────────────────────────
        [Range(0f, 4f)] [BrazeAnimatable("EMISSION/Emission", 0f, 4f)] public float emission = 0f;
        [ColorUsage(false, true)] public Color emissionColor = new Color(0.5f, 0.85f, 1.4f, 1f);
        [Range(0f, 1f)] [BrazeAnimatable("EMISSION/Base", 0f, 1f)] public float emissionBase = 0f;

        // ── FIELDS (explicit list) ─────────────────────────────────────────────────────────────────────────
        [Tooltip("Fields that shape this volume — drag field objects in. ONLY listed fields affect the volume; a field " +
                 "not in this list does nothing to it.")]
        public List<BrazeVolumeField> fields = new List<BrazeVolumeField>();

        static Gradient DefaultTintRamp()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(0.35f, 0.55f, 0.95f), 0f),
                        new GradientColorKey(new Color(0.95f, 0.45f, 0.75f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        // ── internals ────────────────────────────────────────────────────────────────────────────────────
        BrazeGeneratedVolumeRenderer rend;
        ComputeShader filtersCS;
        RenderTexture densityA, densityB; // RHalf bake-time ping-pong; final packed RGBA lives in rend.Field
        RenderTexture bakedDensity;       // latest ping-pong side, retained for light-only rebakes
        RenderTexture occupancy;        // coarse max-pooled density for the renderer's empty-space skipping
        Vector3Int occRes;
        RenderTexture lightTex;         // coarse baked lighting (.x self-shadow, .y sky occlusion, .z cast shadow)
        Vector3Int lightRes;
        RenderTexture detailNoise;      // static seamless tiling noise for the render-time sharpness layer
        const int TileDim = 64;
        Texture2D tintRampTex;
        const int TintRampSize = 128;
        Vector3Int builtRes;
        bool fieldDirty = true;
        bool lookDirty = true;
        int bakedStackSig;
        readonly List<BrazeVolumeField> affecting = new List<BrazeVolumeField>();

        public bool Ready => rend != null && rend.Ready;

        // Heal older serialized looks and keep generator-only Render Detail values inside their renderer-space ranges.
        // The inspector displays normalized controls, but these fields intentionally stay in renderer units so old scenes
        // and presets retain their look.
        bool MigrateLook()
        {
            bool changed = false;

            if (look.crispenSharp < 1f)
            {
                SetChanged(ref look.crispenSharp, 4f, ref changed);
                SetChanged(ref look.crispenPivot, 0.35f, ref changed);
                if (!look.emptySpaceSkip) { look.emptySpaceSkip = true; changed = true; }
            }
            if (look.lightingDownsample < 1) { look.lightingDownsample = 2; changed = true; }   // coarse light grid divisor

            if (look.detailSizeMax < 0.02f && look.detailSizeMin < 0.02f)
            {
                SetChanged(ref look.detailSizeMin, 0.15f, ref changed);
                SetChanged(ref look.detailSizeMax, 0.6f, ref changed);
            }
            else
            {
                float detailMin = BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMin);
                float detailMax = BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMax);
                if (detailMax < detailMin) detailMax = detailMin;
                SetChanged(ref look.detailSizeMin, detailMin, ref changed);
                SetChanged(ref look.detailSizeMax, detailMax, ref changed);
            }

            SetChanged(ref look.detailBoost, Mathf.Clamp01(look.detailBoost), ref changed);
            SetChanged(ref look.detailRoughness,
                Mathf.Clamp(look.detailRoughness, BrazeRenderSettings.GeneratedDetailMinRoughness, BrazeRenderSettings.GeneratedDetailMaxRoughness),
                ref changed);
            SetChanged(ref look.detailContrast,
                Mathf.Clamp(look.detailContrast, BrazeRenderSettings.GeneratedDetailMinSharpness, BrazeRenderSettings.GeneratedDetailMaxSharpness),
                ref changed);
            SetChanged(ref look.detailCurl, Mathf.Clamp01(look.detailCurl), ref changed);
            int seed = Mathf.Clamp(look.detailSeed, 0, BrazeRenderSettings.GeneratedDetailMaxSeed);
            if (seed != look.detailSeed) { look.detailSeed = seed; changed = true; }

            return changed;
        }

        static void SetChanged(ref float field, float value, ref bool changed)
        {
            if (Mathf.Approximately(field, value)) return;
            field = value;
            changed = true;
        }

#if UNITY_EDITOR
        void MarkEditorDirty()
        {
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        void OnEnable()
        {
            bool migrated = MigrateLook();
#if UNITY_EDITOR
            if (migrated) MarkEditorDirty();
#endif
            Build(); fieldDirty = lookDirty = true;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            Release();
        }

        void Reset()
        {
            look = BrazeRenderSettings.Default;
            look.renderFire = false;
            look.renderDetail = false;
        }

        void OnValidate()
        {
            worldSize = new Vector3(Mathf.Max(worldSize.x, 0.01f), Mathf.Max(worldSize.y, 0.01f), Mathf.Max(worldSize.z, 0.01f));
            shapeSize = new Vector3(Mathf.Clamp01(shapeSize.x), Mathf.Clamp01(shapeSize.y), Mathf.Clamp01(shapeSize.z));
            bool migrated = MigrateLook();
#if UNITY_EDITOR
            if (migrated) MarkEditorDirty();
#endif
            fieldDirty = lookDirty = true;
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (this == null || Application.isPlaying) return;
            if (fieldDirty || StackSignature() != bakedStackSig || AnyAnimating() || KeyDirOS() != lastKeyDir || !detailBaked)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
        }
#endif

        void Build()
        {
            if (rend != null) return;
            if (filtersCS == null)
            {
                filtersCS = Resources.Load<ComputeShader>("BrazeVolumeFilters");
                if (filtersCS == null) Debug.LogError("[BrazeFX] Compute shader 'BrazeVolumeFilters' not found.");
            }
            rend = new BrazeGeneratedVolumeRenderer(transform);
            rend.SetWorldSize(worldSize);
            rend.SetKeyLight(keyLight);
            rend.SetLook(look);
            ApplyCloudShading();
            builtRes = ComputeRes();
            rend.Begin(builtRes);
            densityA = MakeDensityWork(builtRes, "BrazeFX_VolumeDensityA");
            densityB = MakeDensityWork(builtRes, "BrazeFX_VolumeDensityB");
            occRes = ComputeOccRes(builtRes);
            occupancy = MakeOccupancy(occRes);
            rend.SetOccupancy(occupancy);
            lightRes = ComputeLightRes(builtRes);
            lightTex = MakeLight(lightRes);
            rend.SetLighting(lightTex);
            detailNoise = MakeTileNoise(TileDim);
            BakeTileNoise();
            rend.SetDetailNoise(detailNoise, TileDim);
        }

        void Release()
        {
            if (rend != null) { rend.End(); rend = null; }
            if (densityA != null) { densityA.Release(); DestroyImmediate(densityA); densityA = null; }
            if (densityB != null) { densityB.Release(); DestroyImmediate(densityB); densityB = null; }
            bakedDensity = null;
            if (occupancy != null) { occupancy.Release(); DestroyImmediate(occupancy); occupancy = null; }
            if (lightTex != null) { lightTex.Release(); DestroyImmediate(lightTex); lightTex = null; }
            if (detailNoise != null) { detailNoise.Release(); DestroyImmediate(detailNoise); detailNoise = null; }
            detailBaked = false;
            if (tintRampTex != null)
            {
                if (Application.isPlaying) Destroy(tintRampTex); else DestroyImmediate(tintRampTex);
                tintRampTex = null;
            }
        }

        internal static RenderTextureFormat DensityWorkFormat => RenderTextureFormat.RHalf;
        internal static RenderTextureFormat OccupancyFormat => RenderTextureFormat.RHalf;
        internal static FilterMode OccupancyFilterMode => FilterMode.Bilinear;
        internal static RenderTextureFormat LightGridFormat => RenderTextureFormat.ARGBHalf;
        internal static RenderTextureFormat DetailNoiseFormat =>
            SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGB32)
                ? RenderTextureFormat.ARGB32
                : RenderTextureFormat.ARGBHalf;

        static RenderTexture MakeDensityWork(Vector3Int r, string textureName)
        {
            var rt = new RenderTexture(r.x, r.y, 0, DensityWorkFormat, RenderTextureReadWrite.Linear)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = r.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                name = textureName,
            };
            rt.Create();
            return rt;
        }

        // Coarse occupancy res (¼ per axis) — a few hundred KB at most; used only to skip empty space while marching.
        internal static Vector3Int ComputeOccRes(Vector3Int r) => new Vector3Int(
            Mathf.Max(1, Mathf.CeilToInt(r.x / 4f)),
            Mathf.Max(1, Mathf.CeilToInt(r.y / 4f)),
            Mathf.Max(1, Mathf.CeilToInt(r.z / 4f)));

        static RenderTexture MakeOccupancy(Vector3Int r, string textureName = "BrazeFX_VolumeOccupancy")
        {
            var rt = new RenderTexture(r.x, r.y, 0, OccupancyFormat, RenderTextureReadWrite.Linear)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = r.z,
                enableRandomWrite = true,
                filterMode = OccupancyFilterMode,  // adjacent max cells blend in conservatively at their boundaries
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                name = textureName,
            };
            rt.Create();
            return rt;
        }

        Vector3Int ComputeRes()
        {
            float m = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z));
            return new Vector3Int(
                Mathf.Clamp(Mathf.RoundToInt(resolution * worldSize.x / m), 8, 512),
                Mathf.Clamp(Mathf.RoundToInt(resolution * worldSize.y / m), 8, 512),
                Mathf.Clamp(Mathf.RoundToInt(resolution * worldSize.z / m), 8, 512));
        }

        void BakeTintRamp()
        {
            if (tintRampTex == null)
            {
                tintRampTex = new Texture2D(TintRampSize, 1, TextureFormat.RGBAHalf, false, true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                    name = "BrazeFX_TintRamp",
                };
            }
            var g = tintRamp ?? DefaultTintRamp();
            for (int i = 0; i < TintRampSize; i++)
                tintRampTex.SetPixel(i, 0, g.Evaluate(i / (float)(TintRampSize - 1)));
            tintRampTex.Apply(false, false);
        }

        void ApplyCloudShading()
        {
            if (rend == null) return;
            BakeTintRamp();
            rend.SetCloudShading(cloudAlbedo, (int)tintMode, tintRampTex, anisotropy, silverLining, powder,
                                 skyColor, groundColor, emissionColor, emission, emissionBase);
        }

        void LateUpdate()
        {
            if (rend == null) { Build(); if (rend == null) return; }

            if (ComputeRes() != builtRes) { Release(); Build(); fieldDirty = lookDirty = true; }
            if (!rend.Ready) return;

            rend.SetWorldSize(worldSize);
            rend.SetKeyLight(keyLight);
            if (lookDirty) { rend.SetLook(look); ApplyCloudShading(); lookDirty = false; }

            int sig = StackSignature();
            if (sig != bakedStackSig) fieldDirty = true;
            bool animating = AnyAnimating();
            if (animating) fieldDirty = true;   // time-driven motion → re-bake every frame
            rend.SetTemporalDetailUnstable(animating || fieldDirty);

            if (fieldDirty) { Generate(); bakedStackSig = sig; fieldDirty = false; }
            else if (KeyDirOS() != lastKeyDir && bakedDensity != null) BakeLightingPass(rend.Res, bakedDensity); // light-only rebake
            if (!detailBaked || (look.renderDetail && DetailBakeSig() != lastDetailBakeSig)) BakeTileNoise();   // self-heal + re-bake on knob change
            rend.Render(Vector3.zero);
        }

        /// <summary>Force a one-off field regeneration + look reapply (raised on any edit, by fields, or preset apply).</summary>
        public void MarkDirty() { fieldDirty = true; lookDirty = true; }

        // Timeline drove one of our own knobs (baseDensity / falloff / shading / emission …) via reflection, which
        // skips OnValidate — so re-bake + reapply look on the next tick.
        public void OnBrazeAnimated() => MarkDirty();

        /// <summary>The active fields shaping this volume, in bake order (for the inspector). Recomputed on call.</summary>
        public List<BrazeVolumeField> GetAffectingFields() { GatherFields(); return affecting; }

        // Fields that affect THIS volume = ONLY the ones it explicitly lists, baked TOP-TO-BOTTOM in FIELDS-list
        // order (reorder the list to change the stack). A field not in the list does nothing.
        void GatherFields()
        {
            affecting.Clear();
            if (fields != null)
                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i];
                    if (f != null && f.Active && !affecting.Contains(f)) affecting.Add(f);
                }
        }

        // True if any affecting field has time-driven motion (flow) → the volume must re-bake every frame.
        bool AnyAnimating()
        {
            GatherFields();
            for (int i = 0; i < affecting.Count; i++) if (affecting[i].Animating) return true;
            return false;
        }

        // Lightweight hash over the affecting fields so add / remove / reorder / move / param-edit triggers a re-bake.
        int StackSignature()
        {
            GatherFields();
            unchecked
            {
                int h = 17;
                for (int i = 0; i < affecting.Count; i++)
                {
                    var f = affecting[i];
                    h = h * 31 + f.BrazeInstanceId();           // sequence encodes list order (reorder → rebake)
                    h = h * 31 + f.version;
                    h = h * 31 + f.transform.localToWorldMatrix.GetHashCode();   // catch gizmo moves
                    h = h * 31 + f.ContentHash();             // catch referenced-asset changes (e.g. spline edits)
                }
                h = h * 31 + transform.localToWorldMatrix.GetHashCode();          // volume moved/scaled
                return h;
            }
        }

        void Generate()
        {
            if (filtersCS == null || rend == null || rend.Field == null || densityA == null || densityB == null) return;
            Vector3Int r = rend.Res;

            // 1) Base shape → RHalf density work texture.
            int kBase = filtersCS.FindKernel("GenerateBase");
            filtersCS.SetTexture(kBase, "_Dst", densityA);
            filtersCS.SetInts("_Res", r.x, r.y, r.z);
            filtersCS.SetVector("_WorldSize", worldSize);
            filtersCS.SetInt("_ShapeType", (int)shape);
            filtersCS.SetVector("_ShapeSize", shapeSize);
            filtersCS.SetFloat("_ShapeRound", shapeRound);
            filtersCS.SetFloat("_ShapeThick", shapeThickness);
            filtersCS.SetFloat("_Falloff", Mathf.Max(falloff, 1e-3f));
            filtersCS.SetFloat("_BaseDensity", Mathf.Max(baseDensity, 0f));
            Dispatch(kBase, r);

            // 2) Apply each affecting field in order, ping-ponging two narrow RHalf textures.
            GatherFields();
            RenderTexture cur = densityA, other = densityB;
            for (int i = 0; i < affecting.Count; i++)
            {
                var f = affecting[i];
                int k = filtersCS.FindKernel(f.KernelName);
                if (k < 0) continue;
                filtersCS.SetTexture(k, "_Src", cur);
                filtersCS.SetTexture(k, "_Dst", other);
                filtersCS.SetInts("_Res", r.x, r.y, r.z);
                filtersCS.SetVector("_WorldSize", worldSize);
                f.BindFalloff(filtersCS, k, this);
                f.SetParams(filtersCS, k);
                Dispatch(k, r);
                (cur, other) = (other, cur);
            }

            bakedDensity = cur;

            // 3) Bake coarse lighting, then pack density + lighting into the render-ready RGBA field.
            BakeLightingPass(r, bakedDensity);

            // 4) Rebuild coarse occupancy from the scalar density source.
            if (occupancy != null)
            {
                int kOcc = filtersCS.FindKernel("BuildOccupancy");
                filtersCS.SetTexture(kOcc, "_Src", bakedDensity);
                filtersCS.SetTexture(kOcc, "_OccDst", occupancy);
                filtersCS.SetTexture(kOcc, "_OccDetailNoise", detailNoise);
                filtersCS.SetInts("_Res", r.x, r.y, r.z);
                filtersCS.SetVector("_WorldSize", worldSize);
                filtersCS.SetInts("_OccRes", occRes.x, occRes.y, occRes.z);
                filtersCS.SetInts("_OccBlock", Mathf.CeilToInt(r.x / (float)occRes.x),
                    Mathf.CeilToInt(r.y / (float)occRes.y), Mathf.CeilToInt(r.z / (float)occRes.z));
                bool warpOccupancy = look.renderDetail && look.detailBoost > 1e-4f &&
                                     look.detailCurl > 1e-4f && detailNoise != null;
                filtersCS.SetInt("_OccWarpEnabled", warpOccupancy ? 1 : 0);
                filtersCS.SetFloat("_OccDetailFreq", 1f /
                    (BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMin) * TileDim));
                filtersCS.SetFloat("_OccDetailWarp", Mathf.Clamp01(look.detailCurl) * 0.08f);
                filtersCS.Dispatch(kOcc, Mathf.CeilToInt(occRes.x / 4f),
                    Mathf.CeilToInt(occRes.y / 4f), Mathf.CeilToInt(occRes.z / 4f));
            }

            rend.MarkFieldChanged();
        }

        void Dispatch(int kernel, Vector3Int r)
            => filtersCS.Dispatch(kernel, Mathf.CeilToInt(r.x / 8f), Mathf.CeilToInt(r.y / 8f), Mathf.CeilToInt(r.z / 8f));

        // Unit-box direction toward the key light. Divide by world extents before normalizing: a physical world ray
        // through a non-uniform 40×6×40 cloud is not the same direction in its normalized texture coordinates.
        Vector3 KeyDirOS()
        {
            Vector3 worldDir = keyLight != null ? -keyLight.transform.forward : new Vector3(0.3f, 1f, 0.2f);
            Vector3 local = Quaternion.Inverse(transform.rotation) * worldDir.normalized;
            Vector3 d = new Vector3(local.x / Mathf.Max(worldSize.x, 1e-4f),
                                    local.y / Mathf.Max(worldSize.y, 1e-4f),
                                    local.z / Mathf.Max(worldSize.z, 1e-4f));
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
        }

        Vector3 lastKeyDir = Vector3.positiveInfinity;

        // Coarse light grid (lighting is low-frequency): ¼..½ of field res by the Light Grid divisor. Smaller grid →
        // far cheaper bake (8–64×), no slider-drag stutter on big volumes. The renderer samples it trilinearly.
        Vector3Int ComputeLightRes(Vector3Int r)
        {
            int div = Mathf.Clamp(look.lightingDownsample, 1, 4);
            return new Vector3Int(
                Mathf.Max(8, Mathf.CeilToInt(r.x / (float)div)),
                Mathf.Max(8, Mathf.CeilToInt(r.y / (float)div)),
                Mathf.Max(8, Mathf.CeilToInt(r.z / (float)div)));
        }

        static RenderTexture MakeLight(Vector3Int r)
        {
            var rt = new RenderTexture(r.x, r.y, 0, LightGridFormat, RenderTextureReadWrite.Linear)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = r.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,   // trilinear read — lighting is smooth, coarse grid is invisible
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                name = "BrazeFX_VolumeLight",
            };
            rt.Create();
            return rt;
        }

        // Seamless tiling noise volume for the render-time sharpness layer. Repeat wrap = no visible tiling; baked
        // ONCE (static, field-independent) so the raymarch only does a cheap single fetch.
        static RenderTexture MakeTileNoise(int dim)
        {
            var rt = new RenderTexture(dim, dim, 0, DetailNoiseFormat, RenderTextureReadWrite.Linear)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = dim,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,   // seamless tiling under world-space sampling
                useMipMap = false,
                name = "BrazeFX_DetailNoise",
            };
            rt.Create();
            return rt;
        }

        bool detailBaked;
        int lastDetailBakeSig;

        // Octave count derived from the Feature Size MIN↔MAX ratio (lacunarity 2): finest octave = min, coarsest = max.
        int DetailLayers()
        {
            float lo = BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMin);
            float hi = Mathf.Max(lo, BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMax));
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(hi / lo, 2f)) + 1, 1, 5);
        }
        float DetailRoughness()
            => Mathf.Clamp(look.detailRoughness, BrazeRenderSettings.GeneratedDetailMinRoughness, BrazeRenderSettings.GeneratedDetailMaxRoughness);
        int DetailBakeSig() { unchecked { return (DetailLayers() * 397 + Mathf.RoundToInt(DetailRoughness() * 100f)) * 397 + look.detailSeed; } }

        void BakeTileNoise()
        {
            // HasKernel guard: FindKernel THROWS if the kernel is missing (e.g. a stale/failed compute recompile),
            // which would otherwise crash OnEnable. If it's not ready yet, leave detailBaked=false so LateUpdate retries
            // once the compute finishes (re)compiling — otherwise the texture stays empty and there's no grain.
            if (filtersCS == null || detailNoise == null || !filtersCS.HasKernel("GenTileNoise")) return;
            int k = filtersCS.FindKernel("GenTileNoise");
            filtersCS.SetTexture(k, "_TileNoiseOut", detailNoise);
            filtersCS.SetInt("_TileDim", TileDim);
            filtersCS.SetInt("_TileOctaves", DetailLayers());
            filtersCS.SetFloat("_TileRoughness", DetailRoughness());
            filtersCS.SetFloat("_TileSeed", look.detailSeed);
            int g = Mathf.CeilToInt(TileDim / 4f);
            filtersCS.Dispatch(k, g, g, g);
            detailBaked = true;
            lastDetailBakeSig = DetailBakeSig();
        }

        // Precompute self-shadow (.x), sky occlusion (.y), and cast-shadow transmittance (.z) into the COARSE light
        // grid. Pack G/B/A into the final field afterward so the view raymarch reads density + lighting in one tap.
        void BakeLightingPass(Vector3Int r, RenderTexture densitySource)
        {
            if (filtersCS == null || rend == null || rend.Field == null || densitySource == null) return;
            int k = filtersCS.FindKernel("LightBake");
            if (k < 0) return;

            Vector3Int lr = ComputeLightRes(r);
            if (lightTex == null || lightRes != lr)
            {
                if (lightTex != null) { lightTex.Release(); DestroyImmediate(lightTex); }
                lightRes = lr; lightTex = MakeLight(lightRes); rend.SetLighting(lightTex);
            }

            int steps = Mathf.Clamp(look.gridLightSteps * 2, 16, 96);   // bake affords more than a per-frame march
            Vector3 dir = KeyDirOS();
            filtersCS.SetTexture(k, "_Src", densitySource);
            filtersCS.SetTexture(k, "_LightDst", lightTex);
            filtersCS.SetInts("_LightRes", lightRes.x, lightRes.y, lightRes.z);
            filtersCS.SetVector("_LbWorldSize", worldSize);
            filtersCS.SetVector("_LbLightDir", dir);
            filtersCS.SetInt("_LbLightSteps", steps);
            filtersCS.SetInt("_LbAmbSteps", Mathf.Max(8, steps / 2));
            filtersCS.SetFloat("_LbStep", 1.2f / steps);
            filtersCS.SetFloat("_LbDensityScale", Mathf.Max(0f, look.densityScale));
            filtersCS.SetFloat("_LbShadow", Mathf.Max(0f, look.shadowStrength));
            filtersCS.SetFloat("_LbCastDensity", Mathf.Max(0f, look.castShadowDensity));
            filtersCS.Dispatch(k, Mathf.CeilToInt(lightRes.x / 4f), Mathf.CeilToInt(lightRes.y / 4f), Mathf.CeilToInt(lightRes.z / 4f));

            int kPack = filtersCS.FindKernel("PackGeneratedLighting");
            filtersCS.SetTexture(kPack, "_PackedLight", lightTex);
            filtersCS.SetTexture(kPack, "_PackedDensity", densitySource);
            filtersCS.SetTexture(kPack, "_PackedField", rend.Field);
            filtersCS.SetInts("_PackedRes", r.x, r.y, r.z);
            filtersCS.Dispatch(kPack, Mathf.CeilToInt(r.x / 8f), Mathf.CeilToInt(r.y / 8f), Mathf.CeilToInt(r.z / 8f));
            lastKeyDir = dir;
        }

        void OnDrawGizmos()
        {
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, worldSize);
            Gizmos.color = new Color(0.5f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}
