using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdSlicePlane
    {
        Vertical = 0,
        Horizontal = 1,
        CrossSection = 2,
    }

    public enum BrazeCfdSliceQuantity
    {
        VelocityMagnitude = 0,
        Pressure = 1,
        Temperature = 2,
        Vorticity = 3,
    }

    public enum BrazeCfdSliceColorMap
    {
        CfdRainbow = 0,
        CoolWarm = 1,
        Thermal = 2,
    }

    /// <summary>A single GPU-sampled analysis plane through a CFD domain. It renders directly from the existing
    /// 3D state, velocity, pressure, and SDF textures: no duplicate simulation, 2D staging texture, or readback.</summary>
    [AddComponentMenu("")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(101)]
    public sealed class BrazeCfdAnalysisSlice : MonoBehaviour
    {
        public static readonly List<BrazeCfdAnalysisSlice> Active = new();

        public BrazeCfdDomain domain;
        public BrazeCfdSlicePlane plane = BrazeCfdSlicePlane.Vertical;
        public BrazeCfdSliceQuantity quantity = BrazeCfdSliceQuantity.VelocityMagnitude;
        public BrazeCfdSliceColorMap colorMap = BrazeCfdSliceColorMap.CfdRainbow;
        [Range(-1f, 1f)] public float offset;
        public float scalarMinimum;
        public float scalarMaximum = 5f;
        public bool showScalar = true;
        public bool showVectors = true;
        [Range(4, 80)] public int vectorColumns = 18;
        [Range(0f, 1f)] public float opacity = 0.92f;
        public Color solidColor = new(0.22f, 0.24f, 0.27f, 1f);
        public bool showGizmo = true;

        [SerializeField, HideInInspector] internal BrazeCfdWindTunnel managedByWindTunnel;
        [SerializeField, HideInInspector] internal BrazeCfdVisualizer managedByVisualizer;

        Material material;
        MaterialPropertyBlock properties;
        Mesh quad;
        Matrix4x4 localToWorld = Matrix4x4.identity;
        bool ready;
        string status = "Waiting for CFD fields.";

        public bool Ready => ready && material != null && quad != null;
        public Material RenderMaterial => material;
        public MaterialPropertyBlock Properties => properties;
        public Mesh Mesh => quad;
        public Matrix4x4 LocalToWorld => localToWorld;
        public string Status => status;

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;

        void OnEnable()
        {
            ResolveDomain();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            ReleaseRuntime();
        }

        void OnDestroy() => ReleaseRuntime();

        void OnValidate()
        {
            offset = Mathf.Clamp(offset, -1f, 1f);
            scalarMaximum = Mathf.Max(scalarMinimum + 0.0001f, scalarMaximum);
            vectorColumns = Mathf.Clamp(vectorColumns, 4, 80);
            opacity = Mathf.Clamp01(opacity);
            ResolveDomain();
            if (isActiveAndEnabled) BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }

        void LateUpdate()
        {
            ready = false;
            ResolveDomain();
            BrazeContainer container = domain != null && domain.isActiveAndEnabled ? domain.Container : null;
            RenderTexture state = container != null ? container.SliceRenderStateTexture : null;
            RenderTexture velocity = container != null ? container.FluidVelocity : null;
            // Prefer the visualization-conditioned pressure (spatial filter + EMA) when the surface-pressure
            // diagnostic maintains it — the raw field carries the undamped checkerboard mode and per-tick noise.
            RenderTexture pressure = container != null
                ? (container.CfdPressureCoefficient != null
                    ? container.CfdPressureCoefficient : container.FluidPressure)
                : null;
            if (container == null || state == null || velocity == null || pressure == null)
            {
                status = domain == null ? "Assign a CFD Domain." : "Waiting for CFD state, velocity, and pressure.";
                return;
            }
            if (!EnsureRuntime()) return;

            domain.EnsureSignedDistance(container.SolverRes, out RenderTexture sdf, out _);
            localToWorld = BuildPlaneMatrix(container);
            Vector3 axisU = localToWorld.GetColumn(0).normalized;
            Vector3 axisV = localToWorld.GetColumn(1).normalized;
            float width = localToWorld.GetColumn(0).magnitude;
            float height = localToWorld.GetColumn(1).magnitude;
            Vector3Int resolution = container.SolverRes;

            properties.SetTexture("_StateTex", state);
            properties.SetTexture("_VelocityTex", velocity);
            properties.SetTexture("_PressureTex", pressure);
            properties.SetTexture("_SdfTex", sdf != null ? sdf : state);
            properties.SetFloat("_UseSdf", sdf != null ? 1f : 0f);
            properties.SetMatrix("_WorldToFluidUVW", container.WorldToFluidUVW());
            properties.SetMatrix("_VoxelToWorldVelocity", container.FluidVoxelToWorldVelocity());
            properties.SetVector("_VelocityInvRes", new Vector3(
                1f / Mathf.Max(1, resolution.x), 1f / Mathf.Max(1, resolution.y),
                1f / Mathf.Max(1, resolution.z)));
            properties.SetVector("_PlaneAxisU", axisU);
            properties.SetVector("_PlaneAxisV", axisV);
            properties.SetVector("_PlaneAspect", new Vector2(width, height));
            properties.SetFloat("_Quantity", (float)quantity);
            properties.SetFloat("_ScalarMinimum", scalarMinimum);
            properties.SetFloat("_ScalarInvRange", 1f / Mathf.Max(scalarMaximum - scalarMinimum, 0.0001f));
            properties.SetFloat("_ColorMap", (float)colorMap);
            properties.SetFloat("_ShowScalar", showScalar ? 1f : 0f);
            properties.SetFloat("_ShowVectors", showVectors ? 1f : 0f);
            properties.SetFloat("_VectorColumns", vectorColumns);
            properties.SetFloat("_Opacity", opacity);
            properties.SetColor("_SolidColor", solidColor);
            ready = true;
            status = $"GPU {quantity} slice active; {plane} plane at {offset:+0.00;-0.00;0.00}.";
        }

        Matrix4x4 BuildPlaneMatrix(BrazeContainer container)
        {
            Vector3 flow = managedByWindTunnel != null
                ? managedByWindTunnel.WindDirectionWorld : container.transform.forward;
            flow = flow.sqrMagnitude > 1e-8f ? flow.normalized : container.transform.forward;
            Vector3 up = container.transform.up;
            if (Mathf.Abs(Vector3.Dot(up, flow)) > 0.95f) up = container.transform.forward;
            Vector3 cross = Vector3.Cross(up, flow).normalized;
            up = Vector3.Cross(flow, cross).normalized;

            Vector3 axisU, axisV, normal;
            switch (plane)
            {
                case BrazeCfdSlicePlane.Horizontal:
                    axisU = flow; axisV = cross; normal = up;
                    break;
                case BrazeCfdSlicePlane.CrossSection:
                    axisU = cross; axisV = up; normal = flow;
                    break;
                default:
                    axisU = flow; axisV = up; normal = cross;
                    break;
            }

            Vector3 size = container.WorldSize();
            Quaternion worldToContainer = Quaternion.Inverse(container.transform.rotation);
            float width = ProjectedLength(worldToContainer * axisU, size);
            float height = ProjectedLength(worldToContainer * axisV, size);
            float depth = ProjectedLength(worldToContainer * normal, size);
            Vector3 center = container.transform.position + normal * (offset * depth * 0.5f);

            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.SetColumn(0, new Vector4(axisU.x * width, axisU.y * width, axisU.z * width, 0f));
            matrix.SetColumn(1, new Vector4(axisV.x * height, axisV.y * height, axisV.z * height, 0f));
            matrix.SetColumn(2, new Vector4(normal.x, normal.y, normal.z, 0f));
            matrix.SetColumn(3, new Vector4(center.x, center.y, center.z, 1f));
            return matrix;
        }

        static float ProjectedLength(Vector3 localAxis, Vector3 size) =>
            Mathf.Abs(localAxis.x) * size.x + Mathf.Abs(localAxis.y) * size.y + Mathf.Abs(localAxis.z) * size.z;

        bool EnsureRuntime()
        {
            if (material != null && properties != null && quad != null) return true;
            Shader shader = Resources.Load<Shader>("BrazeCfdAnalysisSlice");
            if (shader == null) shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/CFD/AnalysisSlice");
            if (shader == null)
            {
                status = "Missing BrazeFX/CFD/AnalysisSlice shader.";
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            properties = new MaterialPropertyBlock();
            quad = new Mesh { name = "Braze CFD Analysis Slice", hideFlags = HideFlags.HideAndDontSave };
            quad.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            });
            quad.SetUVs(0, new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right });
            quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0, true);
            quad.UploadMeshData(true);
            return true;
        }

        void ReleaseRuntime()
        {
            DestroyOwned(material); material = null;
            DestroyOwned(quad); quad = null;
            properties = null;
            ready = false;
        }

        static void DestroyOwned(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        void OnDrawGizmosSelected()
        {
            // Unity draws gizmos even for DISABLED components — without the enabled guard the slice plane kept
            // rendering while another preview mode owned the view.
            if (!isActiveAndEnabled || !showGizmo || domain == null || domain.Container == null) return;
            Matrix4x4 old = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = BuildPlaneMatrix(domain.Container);
            Gizmos.color = new Color(0.05f, 0.9f, 1f, 0.9f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 1f, 0.002f));
            Gizmos.matrix = old;
            Gizmos.color = oldColor;
        }
    }
}
