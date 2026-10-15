using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdThermalCoupling
    {
        /// <summary>Expand the heater into a thin fluid-side shell. Solid-covered cells remain masked.</summary>
        SurfaceToFluid = 0,
        /// <summary>Inject only inside the authored primitive; use when the source already sits in open fluid.</summary>
        FluidVolume = 1,
    }

    /// <summary>A temperature-only GPU source for believable heated-air plumes.</summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Thermal Source", 2)]
    public sealed class BrazeCfdThermalSource : BrazeCfdSource
    {
        public BrazeCfdSourceShape shape = BrazeCfdSourceShape.Sphere;
        [Tooltip("Sphere/Cylinder radius in world units before Transform scale.")]
        [Min(0.01f)] public float radius = 0.5f;
        [Tooltip("Full Cylinder height in world units before Transform scale. The cylinder axis is local Y.")]
        [Min(0.01f)] public float height = 0.5f;
        [Tooltip("Heat injection in degrees Celsius per second. The believable release currently models heaters; " +
                 "cooling sources arrive with signed thermal transport.")]
        [Min(0f)] public float heatRateCPerSecond = 40f;
        [Tooltip("Surface To Fluid is the physical default for electronics: the authored primitive may overlap a " +
                 "solid SDF, while heat is deposited into a thin surrounding layer of fluid. Fluid Volume injects " +
                 "only inside the primitive and is appropriate for already-open air regions.")]
        public BrazeCfdThermalCoupling coupling = BrazeCfdThermalCoupling.SurfaceToFluid;
        [Min(0.001f), Tooltip("Minimum world-space thickness of the heated fluid layer outside the authored " +
                              "source. The runtime guarantees at least one simulation cell.")]
        public float fluidShellThickness = 0.015f;

        protected override void OnValidate()
        {
            radius = Mathf.Max(0.01f, radius);
            height = Mathf.Max(0.01f, height);
            heatRateCPerSecond = Mathf.Max(0f, heatRateCPerSecond);
            fluidShellThickness = Mathf.Max(0.001f, fluidShellThickness);
            base.OnValidate();
        }

        static readonly Color HeatColor = new(1f, 0.28f, 0.08f, 0.9f);

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Matrix4x4 old = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = HeatColor;
            switch (shape)
            {
                case BrazeCfdSourceShape.Box:
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                    break;
                case BrazeCfdSourceShape.Cylinder:
                    Gizmos.DrawWireCube(Vector3.zero, new Vector3(radius * 2f, height, radius * 2f));
                    break;
                default:
                    Gizmos.DrawWireSphere(Vector3.zero, radius);
                    break;
            }
            if (coupling == BrazeCfdThermalCoupling.SurfaceToFluid && fluidShellThickness > 0f)
            {
                Vector3 scale = transform.lossyScale;
                Vector3 absScale = new(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                Gizmos.color = new Color(1f, 0.75f, 0.08f, 0.65f);
                switch (shape)
                {
                    case BrazeCfdSourceShape.Box:
                        Gizmos.DrawWireCube(Vector3.zero, absScale + Vector3.one * (fluidShellThickness * 2f));
                        break;
                    case BrazeCfdSourceShape.Cylinder:
                    {
                        float radialScale = Mathf.Max(absScale.x, absScale.z);
                        float diameter = (radius * radialScale + fluidShellThickness) * 2f;
                        Gizmos.DrawWireCube(Vector3.zero,
                            new Vector3(diameter, height * absScale.y + fluidShellThickness * 2f, diameter));
                        break;
                    }
                    default:
                        Gizmos.DrawWireSphere(Vector3.zero,
                            radius * Mathf.Max(absScale.x, Mathf.Max(absScale.y, absScale.z)) + fluidShellThickness);
                        break;
                }
            }
            Gizmos.matrix = old;
            Gizmos.color = oldColor;
        }
    }
}
