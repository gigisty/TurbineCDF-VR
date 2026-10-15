using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdBoundaryPatchMode
    {
        VelocityInlet = 0,
        PressureOutlet = 1,
    }

    public enum BrazeCfdDomainFace
    {
        NegativeX = 0,
        PositiveX = 1,
        NegativeY = 2,
        PositiveY = 3,
        NegativeZ = 4,
        PositiveZ = 5,
    }

    public enum BrazeCfdSmokePattern
    {
        Full = 0,
        Sheet = 1,
        Rake = 2,
        TurbulentVolume = 3,
        // No inlet dye at all: the solver releases dye in the fractional surface shell where moving air meets
        // the body (see CfdSeedContactDye), driven by the active CFD presentation facade's frame policy.
        BodyContact = 4,
    }

    /// <summary>Cross-section of a boundary patch on its domain face. Rectangle preserves the original
    /// behaviour; Circle/Ellipse model round ducts and fan openings; Ring is a fan annulus with a hub cutout;
    /// Slot is a stadium (rounded-cap) vent. Values are serialized — append only.</summary>
    public enum BrazeCfdPatchShape
    {
        Rectangle = 0,
        Ellipse = 1,
        Ring = 2,
        Circle = 3,
        Slot = 4,
    }

    /// <summary>
    /// A rectangular physical boundary condition on one outer face of a CFD domain. The Transform position sets
    /// the patch centre along the face; its normal coordinate is snapped to the selected domain face by the solver.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Boundary Patch", 4)]
    [ExecuteAlways]
    public sealed class BrazeCfdBoundaryPatch : MonoBehaviour
    {
        internal static readonly List<BrazeCfdBoundaryPatch> Active = new();

        [Tooltip("The CFD domain affected by this patch. When empty, the closest parent domain is used.")]
        public BrazeCfdDomain domain;
        public BrazeCfdBoundaryPatchMode mode = BrazeCfdBoundaryPatchMode.VelocityInlet;
        [Tooltip("The outer domain face carrying this boundary condition.")]
        public BrazeCfdDomainFace face = BrazeCfdDomainFace.NegativeZ;
        [Tooltip("Patch width and height in world units along the two tangential domain axes.")]
        public Vector2 size = new(2f, 2f);
        [Tooltip("Cross-section on the face. Rectangle/Ellipse/Slot use Size (width × height); Circle and Ring " +
                 "use Radius; Ring adds a hub cutout.")]
        public BrazeCfdPatchShape shape = BrazeCfdPatchShape.Rectangle;
        [Min(0.005f), Tooltip("Circle and Ring: outer radius in world units.")]
        public float radius = 1f;
        [Range(0f, 0.95f), Tooltip("Ring only: hub cutout radius as a fraction of the outer radius.")]
        public float ringInnerFraction = 0.35f;
        [Tooltip("Offset of the patch centre across the face, in world units along the width/height axes. " +
                 "Applied on top of the Transform-derived (or domain-centred) position.")]
        public Vector2 faceOffset;
        [Range(-180f, 180f), Tooltip("Rotation of the cross-section on its face, in degrees. The wind-tunnel " +
                 "facade derives this automatically from its Transform's roll about the wind axis.")]
        public float faceRotation;

        [Header("Velocity Inlet")]
        [Min(0f), Tooltip("Normal air speed directed into the domain, in world units per second.")]
        public float normalSpeed = 5f;
        [Tooltip("Optional velocity along the patch's width and height axes, in world units per second.")]
        public Vector2 tangentialVelocity;
        [Tooltip("Set the incoming air temperature instead of preserving transported temperature.")]
        public bool setInletTemperature = true;
        [Tooltip("Incoming air temperature in degrees Celsius.")]
        public float inletTemperatureC = 20f;
        [Tooltip("Inject passive diagnostic dye into the incoming air. It is transported by the existing smoke channel.")]
        public bool setInletSmoke;
        [Tooltip("Turbulent Volume emits a continuous evolving dye field. Full fills the inlet, Sheet emits a soft " +
                 "center plane, and Tracer Rake emits fifteen diagnostic tubes. Body Contact injects nothing at " +
                 "the inlet; dye is released at the test body's surface instead (Wind Tunnel or Visualizer required).")]
        public BrazeCfdSmokePattern inletSmokePattern = BrazeCfdSmokePattern.Full;
        [Range(0f, 1f), Tooltip("Passive dye concentration written at the inlet.")]
        public float inletSmokeDensity = 0.12f;

        [Header("Pressure Outlet")]
        [Tooltip("Dimensionless solver gauge pressure. Zero is the stable ambient/reference outlet for this phase.")]
        public float gaugePressure;
        [Tooltip("Reject velocity returning from the outlet into the domain.")]
        public bool preventBackflow = true;

        [Header("Authoring")]
        public bool showGizmo = true;

        [SerializeField, HideInInspector] internal BrazeCfdWindTunnel managedByWindTunnel;
        [SerializeField, HideInInspector] internal bool managedCenterOnDomain;

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;

        /// <summary>Bounding width/height on the face: radius-driven shapes derive it, the rest author it.</summary>
        internal Vector2 EffectiveSize => shape switch
        {
            BrazeCfdPatchShape.Circle or BrazeCfdPatchShape.Ring => Vector2.one * (radius * 2f),
            _ => size,
        };

        /// <summary>GPU shape code: 0 rectangle, 1 ellipse (Circle is an equal-axis ellipse), 2 ring, 3 slot.</summary>
        internal float GpuShape => shape switch
        {
            BrazeCfdPatchShape.Ellipse or BrazeCfdPatchShape.Circle => 1f,
            BrazeCfdPatchShape.Ring => 2f,
            BrazeCfdPatchShape.Slot => 3f,
            _ => 0f,
        };

        void Reset() => ResolveDomain();

        void OnEnable()
        {
            ResolveDomain();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        void OnValidate()
        {
            size.x = Mathf.Max(0.001f, size.x);
            size.y = Mathf.Max(0.001f, size.y);
            radius = Mathf.Max(0.005f, radius);
            ringInnerFraction = Mathf.Clamp(ringInnerFraction, 0f, 0.95f);
            normalSpeed = Mathf.Max(0f, normalSpeed);
            inletSmokeDensity = Mathf.Clamp01(inletSmokeDensity);
            ResolveDomain();
            if (isActiveAndEnabled) BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }

        internal static Vector3 LocalOutwardNormal(BrazeCfdDomainFace selectedFace) => selectedFace switch
        {
            BrazeCfdDomainFace.NegativeX => Vector3.left,
            BrazeCfdDomainFace.PositiveX => Vector3.right,
            BrazeCfdDomainFace.NegativeY => Vector3.down,
            BrazeCfdDomainFace.PositiveY => Vector3.up,
            BrazeCfdDomainFace.NegativeZ => Vector3.back,
            _ => Vector3.forward,
        };

        internal static void LocalTangentAxes(BrazeCfdDomainFace selectedFace, out Vector3 widthAxis,
                                               out Vector3 heightAxis)
        {
            switch (selectedFace)
            {
                case BrazeCfdDomainFace.NegativeX:
                case BrazeCfdDomainFace.PositiveX:
                    widthAxis = Vector3.up;
                    heightAxis = Vector3.forward;
                    break;
                case BrazeCfdDomainFace.NegativeY:
                case BrazeCfdDomainFace.PositiveY:
                    widthAxis = Vector3.right;
                    heightAxis = Vector3.forward;
                    break;
                default:
                    widthAxis = Vector3.right;
                    heightAxis = Vector3.up;
                    break;
            }
        }

        static readonly Color InletColor = new(0.08f, 0.82f, 1f, 0.9f);
        static readonly Color OutletColor = new(1f, 0.58f, 0.12f, 0.9f);

        void OnDrawGizmos()
        {
            if (!showGizmo || domain == null || domain.Container == null) return;
            BrazeContainer container = domain.Container;
            Vector3 localCenter = Quaternion.Inverse(container.transform.rotation) *
                                  (transform.position - container.transform.position);
            if (managedCenterOnDomain) localCenter = Vector3.zero;
            Vector3 worldSize = container.WorldSize();
            Vector3 outward = LocalOutwardNormal(face);
            if (Mathf.Abs(outward.x) > 0.5f) localCenter.x = outward.x * worldSize.x * 0.5f;
            if (Mathf.Abs(outward.y) > 0.5f) localCenter.y = outward.y * worldSize.y * 0.5f;
            if (Mathf.Abs(outward.z) > 0.5f) localCenter.z = outward.z * worldSize.z * 0.5f;
            LocalTangentAxes(face, out Vector3 axisU, out Vector3 axisV);

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = Matrix4x4.TRS(container.transform.position, container.transform.rotation, Vector3.one);
            Gizmos.color = mode == BrazeCfdBoundaryPatchMode.VelocityInlet ? InletColor : OutletColor;
            localCenter += axisU * faceOffset.x + axisV * faceOffset.y;
            // Spin the drawing axes by the face rotation so the gizmo matches the GPU containment frame.
            float rotation = faceRotation * Mathf.Deg2Rad;
            Vector3 rotatedU = axisU * Mathf.Cos(rotation) + axisV * Mathf.Sin(rotation);
            Vector3 rotatedV = axisV * Mathf.Cos(rotation) - axisU * Mathf.Sin(rotation);
            Vector2 effectiveSize = EffectiveSize;
            Vector3 halfU = rotatedU * effectiveSize.x * 0.5f;
            Vector3 halfV = rotatedV * effectiveSize.y * 0.5f;
            switch (shape)
            {
                case BrazeCfdPatchShape.Rectangle:
                    Gizmos.DrawLine(localCenter - halfU - halfV, localCenter + halfU - halfV);
                    Gizmos.DrawLine(localCenter + halfU - halfV, localCenter + halfU + halfV);
                    Gizmos.DrawLine(localCenter + halfU + halfV, localCenter - halfU + halfV);
                    Gizmos.DrawLine(localCenter - halfU + halfV, localCenter - halfU - halfV);
                    break;
                case BrazeCfdPatchShape.Slot:
                    DrawGizmoStadium(localCenter, rotatedU, rotatedV, effectiveSize);
                    break;
                default:
                    DrawGizmoEllipse(localCenter, halfU, halfV, 1f);
                    if (shape == BrazeCfdPatchShape.Ring)
                        DrawGizmoEllipse(localCenter, halfU, halfV, Mathf.Clamp(ringInnerFraction, 0f, 0.95f));
                    break;
            }
            Vector3 direction = mode == BrazeCfdBoundaryPatchMode.VelocityInlet ? -outward : outward;
            float arrowLength = Mathf.Max(0.25f, Mathf.Min(normalSpeed, 4f) * 0.25f);
            Vector3 tip = localCenter + direction * arrowLength;
            Gizmos.DrawLine(localCenter, tip);
            Gizmos.DrawLine(tip, tip - direction * 0.12f + axisU * 0.08f);
            Gizmos.DrawLine(tip, tip - direction * 0.12f - axisU * 0.08f);
            Gizmos.matrix = oldMatrix;
            Gizmos.color = oldColor;
        }

        static void DrawGizmoEllipse(Vector3 center, Vector3 halfU, Vector3 halfV, float radiusScale)
        {
            const int Segments = 32;
            Vector3 previous = center + halfU * radiusScale;
            for (int i = 1; i <= Segments; i++)
            {
                float angle = i / (float)Segments * Mathf.PI * 2f;
                Vector3 point = center + (halfU * Mathf.Cos(angle) + halfV * Mathf.Sin(angle)) * radiusScale;
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
        }

        // Stadium (slot) outline: two cap arcs joined by tangent lines, along whichever axis is longer.
        static void DrawGizmoStadium(Vector3 center, Vector3 axisU, Vector3 axisV, Vector2 sizeUV)
        {
            bool alongU = sizeUV.x >= sizeUV.y;
            Vector3 longAxis = alongU ? axisU : axisV;
            Vector3 shortAxis = alongU ? axisV : axisU;
            float capRadius = Mathf.Min(sizeUV.x, sizeUV.y) * 0.5f;
            float halfSegment = Mathf.Max(Mathf.Max(sizeUV.x, sizeUV.y) * 0.5f - capRadius, 0f);
            Vector3 capA = center + longAxis * halfSegment;
            Vector3 capB = center - longAxis * halfSegment;
            const int Segments = 16;
            Vector3 previousTop = capB + shortAxis * capRadius;
            Vector3 previousBottom = capB - shortAxis * capRadius;
            Gizmos.DrawLine(previousTop, capA + shortAxis * capRadius);
            Gizmos.DrawLine(previousBottom, capA - shortAxis * capRadius);
            for (int i = 0; i <= Segments; i++)
            {
                float angle = i / (float)Segments * Mathf.PI;
                Vector3 pointA = capA + shortAxis * Mathf.Cos(angle) * capRadius + longAxis * Mathf.Sin(angle) * capRadius;
                if (i > 0) Gizmos.DrawLine(previousTop, pointA);
                previousTop = pointA;
                Vector3 pointB = capB + shortAxis * Mathf.Cos(angle) * capRadius - longAxis * Mathf.Sin(angle) * capRadius;
                if (i > 0) Gizmos.DrawLine(previousBottom, pointB);
                previousBottom = pointB;
            }
        }
    }
}
