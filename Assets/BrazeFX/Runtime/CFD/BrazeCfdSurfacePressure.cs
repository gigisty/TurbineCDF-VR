using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>
    /// GPU surface overlay for the solver's ambient-referenced, measured-stagnation-calibrated pressure
    /// coefficient field. This is a Cp-like comparative diagnostic until solver validation gates are implemented.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Surface Pressure", 6)]
    [ExecuteAlways]
    [DefaultExecutionOrder(101)]
    public sealed class BrazeCfdSurfacePressure : MonoBehaviour
    {
        public static readonly List<BrazeCfdSurfacePressure> Active = new();

        public BrazeCfdDomain domain;
        [Tooltip("Mesh receiving the pressure overlay. When empty, the Mesh Filter on this GameObject is used.")]
        public MeshFilter meshFilter;
        [Tooltip("Pressure coefficient mapped to the suction (blue) end of the ramp. The overlay shows Cp " +
                 "relative to the domain ambient mean: strong suction on a fast car can reach -2.")]
        public float minimumPressure = -1f;
        [Tooltip("Pressure coefficient mapped to the pressure (red) end of the ramp. Stagnation air reads ~+1 " +
                 "by definition, so +1 is the natural maximum.")]
        public float maximumPressure = 1f;
        [ColorUsage(false, true)] public Color suctionColor = new(0.03f, 0.22f, 1f, 1f);
        [ColorUsage(false, true)] public Color neutralColor = new(0.12f, 0.85f, 0.45f, 1f);
        [ColorUsage(false, true)] public Color pressureColor = new(1f, 0.08f, 0.02f, 1f);
        [Range(0f, 1f)] public float opacity = 0.85f;
        [Min(0f), Tooltip("World-space visual separation from the source mesh. Pressure sampling automatically " +
                          "uses at least three quarters of a CFD cell so it remains on the fluid side.")]
        public float surfaceOffset = 0.01f;

        [Header("Comparative Load Axes")]
        [Tooltip("Local axis transformed by this component and used to report the relative drag load.")]
        public Vector3 dragAxis = Vector3.forward;
        [Tooltip("Local axis transformed by this component and used to report the relative lift load.")]
        public Vector3 liftAxis = Vector3.up;

        [SerializeField, HideInInspector] internal BrazeCfdTestBody managedByTestBody;

        Material material;
        MaterialPropertyBlock properties;
        BrazeCfdDomain attachedDomain;
        bool ready;
        string status = "Waiting for the CFD pressure field.";

        public bool Ready => ready && material != null && meshFilter != null && meshFilter.sharedMesh != null;
        public Mesh Mesh => meshFilter != null ? meshFilter.sharedMesh : null;
        public Matrix4x4 LocalToWorld => meshFilter != null ? meshFilter.transform.localToWorldMatrix : Matrix4x4.identity;
        public Material RenderMaterial => material;
        public MaterialPropertyBlock Properties => properties;
        public string Status => status;
        public Vector3 RelativePressureLoadWorld => domain != null ? domain.RelativePressureLoadWorld : Vector3.zero;
        public float RelativeDragLoad => Vector3.Dot(RelativePressureLoadWorld,
            transform.TransformDirection(dragAxis.normalized));
        public float RelativeLiftLoad => Vector3.Dot(RelativePressureLoadWorld,
            transform.TransformDirection(liftAxis.normalized));

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;

        void Reset() => ResolveReferences();

        void OnEnable()
        {
            ResolveReferences();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            AttachToDomain();
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            attachedDomain?.DetachSurfacePressure(this);
            attachedDomain = null;
            ReleaseRuntime();
        }

        void OnDestroy() => ReleaseRuntime();

        void OnValidate()
        {
            maximumPressure = Mathf.Max(minimumPressure + 0.001f, maximumPressure);
            opacity = Mathf.Clamp01(opacity);
            surfaceOffset = Mathf.Max(0f, surfaceOffset);
            if (dragAxis.sqrMagnitude < 1e-6f) dragAxis = Vector3.forward;
            if (liftAxis.sqrMagnitude < 1e-6f) liftAxis = Vector3.up;
            ResolveReferences();
            if (isActiveAndEnabled)
            {
                BrazeRuntimeRegistry.AddUnique(Active, this);
                AttachToDomain();
            }
        }

        void ResolveReferences()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        }

        void AttachToDomain()
        {
            if (attachedDomain == domain) return;
            attachedDomain?.DetachSurfacePressure(this);
            attachedDomain = domain;
            attachedDomain?.AttachSurfacePressure(this);
        }

        void LateUpdate()
        {
            ready = false;
            ResolveReferences();
            BrazeContainer container = domain != null && domain.isActiveAndEnabled ? domain.Container : null;
            // The conditioned texture already stores Cp. Never fall back to raw projection potential here: it has
            // a free gauge level and different units, so even one pre-conditioning frame can flash the whole body.
            RenderTexture pressure = container != null ? container.CfdPressureCoefficient : null;
            if (container == null || pressure == null || meshFilter == null || meshFilter.sharedMesh == null)
            {
                status = domain == null ? "Assign a CFD Domain and Mesh Filter."
                    : "Waiting for the calibrated CFD pressure-coefficient field and target mesh.";
                return;
            }
            if (!EnsureRuntime()) return;

            properties.SetTexture("_PressureTex", pressure);
            properties.SetMatrix("_WorldToPressureUVW", container.WorldToFluidUVW());
            properties.SetFloat("_PressureMinimum", minimumPressure);
            properties.SetFloat("_PressureInvRange", 1f / Mathf.Max(maximumPressure - minimumPressure, 0.001f));
            properties.SetColor("_SuctionColor", suctionColor);
            properties.SetColor("_NeutralColor", neutralColor);
            properties.SetColor("_PressureColor", pressureColor);
            properties.SetFloat("_Opacity", opacity);
            properties.SetFloat("_SurfaceOffset", surfaceOffset);
            properties.SetFloat("_PressureSampleOffset",
                ResolveSampleOffset(surfaceOffset, container.WorldSize(), container.SolverRes));
            ready = true;
            status = "GPU Cp overlay active. Domain-ambient reference; measured stagnation Cp = +1.";
        }

        internal static float ResolveSampleOffset(float authoredOffset, Vector3 worldSize, Vector3Int resolution)
        {
            Vector3 cell = new(
                Mathf.Abs(worldSize.x) / Mathf.Max(1, resolution.x),
                Mathf.Abs(worldSize.y) / Mathf.Max(1, resolution.y),
                Mathf.Abs(worldSize.z) / Mathf.Max(1, resolution.z));
            float largestCell = Mathf.Max(cell.x, Mathf.Max(cell.y, cell.z));
            return Mathf.Max(Mathf.Max(0f, authoredOffset), largestCell * 0.75f);
        }

        bool EnsureRuntime()
        {
            if (material != null && properties != null) return true;
            // Resources keeps this dynamically-created diagnostic material available in player builds where an
            // otherwise unreferenced Shader.Find-only asset could be stripped.
            Shader shader = Resources.Load<Shader>("BrazeCfdSurfacePressure");
            if (shader == null) shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/CFD/SurfacePressure");
            if (shader == null)
            {
                status = "Missing BrazeFX/CFD/SurfacePressure shader.";
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            properties = new MaterialPropertyBlock();
            return true;
        }

        void ReleaseRuntime()
        {
            if (material != null)
            {
                if (Application.isPlaying) Destroy(material); else DestroyImmediate(material);
            }
            material = null;
            properties = null;
            ready = false;
        }
    }
}
