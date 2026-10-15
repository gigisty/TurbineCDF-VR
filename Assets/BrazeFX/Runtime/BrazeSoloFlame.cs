using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Standalone single-card procedural flame. This is intentionally separate from ribbon trails: it renders one
    /// editable local XY card with selectable procedural-shader flame looks and presets.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("BrazeFX/Fire/Braze Solo Flame", 5)]
    public sealed class BrazeSoloFlame : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum FlameType { Red, Blue, Green, Custom }
        public enum FlamePreset { Flame1, Flame2, Flame3, Flame4, Flame5 }
        public enum BlendMode { Additive, Alpha, Multiply }
        public enum RenderQuality { Fast, Smooth, Ultra }

        [Header("Preset")]
        [Tooltip("Selects one of the original shader flame looks. The card renders one flame at a time.")]
        public FlamePreset preset = FlamePreset.Flame3;

        [Header("Shape")]
        [Tooltip("Local card size. X = flame strip width, Y = flame height.")]
        public Vector2 size = new Vector2(1.6f, 0.65f);
        [Tooltip("Fits the card outline to the original flame cell. When enabled, Size Y controls height and Size X is derived.")]
        public bool autoFitCard = true;
        [Tooltip("Extra border around the fitted flame cell.")]
        [Range(0.8f, 1.5f)] public float fitPadding = 1.08f;
        [Tooltip("Normalized local pivot. (0.5, 0.5) = centred, (0.5, 0) = bottom-centred.")]
        public Vector2 pivot = new Vector2(0.5f, 0.5f);

        [Header("Look")]
        public FlameType flameType = FlameType.Red;
        [ColorUsage(true, true)] public Color customTint = new Color(1f, 0.42f, 0.12f, 1f);
        [Range(0f, 8f)] public float intensity = 3.5f;
        [Tooltip("Makes the large billow masses burn hotter/whiter than the thin fire around them, adding depth. 0 = uniform temperature.")]
        [Range(0f, 2f)] public float billowHeat = 0.6f;
        [Range(0f, 1f)] public float opacity = 1f;
        public BlendMode blendMode = BlendMode.Additive;

        [Header("Flame")]
        [Tooltip("Horizontal squeeze of the selected single flame. 1 = original shader cell width.")]
        [Range(0.25f, 4f)] public float scale = 1f;
        [Range(0f, 8f)] public float speed = 1.4f;
        [Range(1, 8)] public int detail = 4;
        public RenderQuality renderQuality = RenderQuality.Ultra;
        [Range(0.2f, 4f)] public float sharpness = 1.2f;
        [Range(0f, 1f)] public float edgeErosion = 0f;
        [Tooltip("How far the flame reaches up the card. 2 = tall licking tongues (original tall variant); 1 = short candle sitting in the bottom of the card.")]
        [Range(0.5f, 3f)] public float flameHeight = 2f;
        [Range(0.25f, 5f)] public float verticalStretch = 2f;
        [Tooltip("Cooled plume rising above the flame. 0 = pure fire (original look); higher adds the dim smoke plume.")]
        [Range(0f, 1f)] public float smoke = 0.4f;
        [Tooltip("Colour/opacity of the smoke plume. Alpha controls how much it occludes.")]
        [ColorUsage(true, false)] public Color smokeColor = new Color(0.14f, 0.11f, 0.10f, 0.7f);
        [Range(0f, 0.45f)] public float softEdge = 0.03f;
        [Range(0f, 1000f)] public float seed = 0f;

        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Mesh mesh;
        Material mat;
        Vector2 lastSize;
        Vector2 lastPivot;

        [SerializeField, HideInInspector] int dataVersion;

        const int CurrentDataVersion = 2;
        const float OriginalCellAspect = 0.712f;
        const HideFlags InternalComponentFlags = HideFlags.HideInInspector;
        const HideFlags RuntimeObjectFlags = HideFlags.HideAndDontSave;

        void OnEnable()
        {
            MigrateSerializedDefaults();
            EnsureRuntimeObjects();
            BuildMesh();
            ApplyMaterial();
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            if (meshFilter != null && meshFilter.sharedMesh == mesh) meshFilter.sharedMesh = null;
            DestroyObj(mesh); mesh = null;
            DestroyObj(mat); mat = null;
        }

#if UNITY_EDITOR
        // Drive the flame animation in edit mode from EditorApplication.update (NOT from the inspector's
        // OnInspectorGUI — a self-Repaint() there re-enters InspectorWindow.RedrawFromNative before the GUI
        // skin context is ready, which throws "Unable to use a named GUIStyle without a current skin").
        void EditorTick()
        {
            if (this == null || Application.isPlaying) return;
            if (speed <= 0f) return;   // static flame needs no per-frame repaint
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();   // ticks LateUpdate → refreshes _TimeParam
            UnityEditor.SceneView.RepaintAll();
        }
#endif

        void OnValidate()
        {
            MigrateSerializedDefaults();
            if (preset < FlamePreset.Flame1 || preset > FlamePreset.Flame5) preset = FlamePreset.Flame5;
            size.x = Mathf.Max(0.001f, size.x);
            size.y = Mathf.Max(0.001f, size.y);
            fitPadding = Mathf.Clamp(fitPadding, 0.8f, 1.5f);
            pivot.x = Mathf.Clamp01(pivot.x);
            pivot.y = Mathf.Clamp01(pivot.y);
            intensity = Mathf.Max(0f, intensity);
            billowHeat = Mathf.Clamp(billowHeat, 0f, 2f);
            opacity = Mathf.Clamp01(opacity);
            scale = Mathf.Max(0.25f, scale);
            speed = Mathf.Max(0f, speed);
            detail = Mathf.Clamp(detail, 1, 8);
            sharpness = Mathf.Max(0.05f, sharpness);
            edgeErosion = Mathf.Clamp01(edgeErosion);
            flameHeight = Mathf.Clamp(flameHeight, 0.5f, 3f);
            verticalStretch = Mathf.Max(0.25f, verticalStretch);
            smoke = Mathf.Clamp01(smoke);
            softEdge = Mathf.Clamp(softEdge, 0f, 0.45f);
            seed = Mathf.Max(0f, seed);

            if (isActiveAndEnabled)
            {
                EnsureRuntimeObjects();
                BuildMesh();
                ApplyMaterial();
            }
        }

        public void OnBrazeAnimated() => OnValidate();

        void MigrateSerializedDefaults()
        {
            if (dataVersion >= CurrentDataVersion) return;

            if (dataVersion < 2)
            {
                if (detail == 6) detail = 4;
                if (renderQuality == RenderQuality.Smooth) renderQuality = RenderQuality.Ultra;
                if (Mathf.Abs(edgeErosion - 0.18f) < 0.0001f) edgeErosion = 0f;
            }

            dataVersion = CurrentDataVersion;
        }

        void LateUpdate()
        {
            EnsureRuntimeObjects();
            if (EffectiveSize() != lastSize || pivot != lastPivot) BuildMesh();
            ApplyMaterial();
        }

        void EnsureRuntimeObjects()
        {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

            meshFilter.hideFlags = InternalComponentFlags;
            meshRenderer.hideFlags = InternalComponentFlags;

            if (mesh == null)
            {
                mesh = new Mesh { name = "BrazeSoloFlame", hideFlags = RuntimeObjectFlags };
                mesh.MarkDynamic();
                meshFilter.sharedMesh = mesh;
            }
            else mesh.hideFlags = RuntimeObjectFlags;

            if (mat == null)
            {
                var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Solo Flame");
                if (shader == null)
                {
                    Debug.LogError("[BrazeFX] Shader 'BrazeFX/Solo Flame' not found.");
                    enabled = false;
                    return;
                }
                mat = new Material(shader) { hideFlags = RuntimeObjectFlags };
                meshRenderer.sharedMaterial = mat;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
            }
            else mat.hideFlags = RuntimeObjectFlags;
        }

        void BuildMesh()
        {
            if (mesh == null) return;

            Vector2 effectiveSize = EffectiveSize();
            float w = effectiveSize.x;
            float h = effectiveSize.y;
            float x0 = -pivot.x * w;
            float x1 = (1f - pivot.x) * w;
            float y0 = -pivot.y * h;
            float y1 = (1f - pivot.y) * h;

            mesh.Clear(false);
            mesh.SetVertices(new[]
            {
                new Vector3(x0, y0, 0f),
                new Vector3(x1, y0, 0f),
                new Vector3(x0, y1, 0f),
                new Vector3(x1, y1, 0f),
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0, true);
            mesh.RecalculateBounds();

            lastSize = effectiveSize;
            lastPivot = pivot;
        }

        Vector2 EffectiveSize()
        {
            float h = Mathf.Max(0.001f, size.y);
            float w = autoFitCard ? h * OriginalCellAspect * Mathf.Max(0.001f, fitPadding) : Mathf.Max(0.001f, size.x);
            return new Vector2(w, h);
        }

        void ApplyMaterial()
        {
            if (mat == null) return;

            mat.SetFloat("_Preset", (float)preset);
            mat.SetFloat("_FlameType", (float)flameType);
            mat.SetColor("_Tint", customTint);
            mat.SetFloat("_Intensity", Mathf.Max(0f, intensity));
            mat.SetFloat("_BillowHeat", Mathf.Clamp(billowHeat, 0f, 2f));
            mat.SetFloat("_Opacity", Mathf.Clamp01(opacity));
            mat.SetFloat("_Scale", Mathf.Max(0.25f, scale));
            mat.SetFloat("_Speed", Mathf.Max(0f, speed));
            mat.SetFloat("_Detail", Mathf.Clamp(detail, 1, 8));
            mat.SetFloat("_Quality", (float)renderQuality);
            mat.SetFloat("_Sharpness", Mathf.Max(0.05f, sharpness));
            mat.SetFloat("_Edge", Mathf.Clamp01(edgeErosion));
            mat.SetFloat("_Height", Mathf.Clamp(flameHeight, 0.5f, 3f));
            mat.SetFloat("_Stretch", Mathf.Max(0.25f, verticalStretch));
            mat.SetFloat("_Smoke", Mathf.Clamp01(smoke));
            mat.SetColor("_SmokeTint", smokeColor);
            mat.SetFloat("_SoftEdge", Mathf.Clamp(softEdge, 0f, 0.45f));
            mat.SetFloat("_Seed", Mathf.Max(0f, seed));
            mat.SetFloat("_TimeParam", Application.isPlaying ? Time.time : Time.realtimeSinceStartup);

            int src, dst, mul;
            switch (blendMode)
            {
                case BlendMode.Alpha:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    mul = 0;
                    break;
                case BlendMode.Multiply:
                    src = (int)UnityEngine.Rendering.BlendMode.DstColor;
                    dst = (int)UnityEngine.Rendering.BlendMode.Zero;
                    mul = 1;
                    break;
                default:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.One;
                    mul = 0;
                    break;
            }

            mat.SetFloat("_BlendSrc", src);
            mat.SetFloat("_BlendDst", dst);
            mat.SetFloat("_Multiply", mul);
            mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
        }

        static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDrawGizmosSelected()
        {
            Vector2 effectiveSize = EffectiveSize();
            Vector3 a = transform.TransformPoint(new Vector3(-pivot.x * effectiveSize.x, -pivot.y * effectiveSize.y, 0f));
            Vector3 b = transform.TransformPoint(new Vector3((1f - pivot.x) * effectiveSize.x, -pivot.y * effectiveSize.y, 0f));
            Vector3 c = transform.TransformPoint(new Vector3((1f - pivot.x) * effectiveSize.x, (1f - pivot.y) * effectiveSize.y, 0f));
            Vector3 d = transform.TransformPoint(new Vector3(-pivot.x * effectiveSize.x, (1f - pivot.y) * effectiveSize.y, 0f));
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.85f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }
    }
}
