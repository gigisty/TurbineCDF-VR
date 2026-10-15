using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>How a mesh contributes to the domain signed-distance union.</summary>
    public enum BrazeCfdSdfBoundaryMode
    {
        /// <summary>The enclosed side of the GPU-voxelized surface is solid. Triangle winding is ignored.</summary>
        SolidInterior = 0,
        /// <summary>A finite-thickness wall is built around the mesh surface; open meshes are supported.</summary>
        SurfaceShell = 1,
    }

    /// <summary>
    /// Static mesh boundary for one CFD domain. Mesh triangles are uploaded and converted to a GPU SDF only when
    /// the mesh, transform, settings, domain transform, or bake resolution changes.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/SDF Boundary", 3)]
    [ExecuteAlways]
    public sealed class BrazeCfdSdfBoundary : MonoBehaviour
    {
        internal static readonly List<BrazeCfdSdfBoundary> Active = new();

        [Tooltip("The CFD domain affected by this boundary. When empty, the closest parent domain is used.")]
        public BrazeCfdDomain domain;
        [Tooltip("Static triangle mesh to bake. When empty, the Mesh Filter on this GameObject is used.")]
        public MeshFilter meshFilter;
        [Tooltip("Solid Interior classifies exact GPU polygon crossings and is insensitive to mixed/reversed " +
                 "triangle winding. Surface Shell represents intentionally open architectural surfaces.")]
        public BrazeCfdSdfBoundaryMode mode = BrazeCfdSdfBoundaryMode.SolidInterior;
        [Tooltip("World-space shell thickness. Used only by Surface Shell mode.")]
        [Min(0.001f)] public float wallThickness = 0.05f;
        [Tooltip("Inflates a Solid Interior boundary outward in world units. Useful for closing sub-voxel gaps.")]
        [Min(0f)] public float surfaceOffset;
        [Tooltip("Rigid-motion animation (propellers, fans, doors): the SDF is baked once in mesh-local space and " +
                 "re-stamped through the Mesh Filter's current transform every solver tick, and the surface's rigid " +
                 "velocity is injected into the flow. Rotation, translation, animated parents, and changed scale " +
                 "are supported; deforming or skinned meshes are not.")]
        public bool animated;
        [Tooltip("Draw the source mesh in the Scene view.")]
        public bool showGizmo = true;

        [SerializeField, HideInInspector] internal BrazeCfdTestBody managedByTestBody;

        [System.NonSerialized] int revision;
        [System.NonSerialized] string bakeStatus;
        [System.NonSerialized] Vector3 previousPosition;
        [System.NonSerialized] Quaternion previousRotation;
        [System.NonSerialized] bool motionValid;

        /// <summary>The last bake validation message. Empty means this boundary was accepted.</summary>
        public string BakeStatus => bakeStatus;
        internal int Revision => revision;
        internal Mesh SourceMesh => meshFilter != null ? meshFilter.sharedMesh : null;

        void Reset()
        {
            ResolveReferences();
            MarkDirty();
        }

        void OnEnable()
        {
            ResolveReferences();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            MarkDirty();
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            motionValid = false;   // re-enabling must not read a stale transform as a teleport velocity
            MarkDirty();
        }

        void OnValidate()
        {
            wallThickness = Mathf.Max(0.001f, wallThickness);
            surfaceOffset = Mathf.Max(0f, surfaceOffset);
            ResolveReferences();
            if (isActiveAndEnabled) BrazeRuntimeRegistry.AddUnique(Active, this);
            MarkDirty();
        }

        /// <summary>Call after modifying a procedural mesh in-place so its cached SDF is rebuilt.</summary>
        public void MarkDirty()
        {
            unchecked { revision++; }
            domain?.MarkSdfDirty();
        }

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;
        internal void SetBakeStatus(string value) => bakeStatus = value;

        /// <summary>Finite-difference rigid motion of the mesh transform since the previous call. The first call
        /// after enabling (or a teleport-sized dt) reports zero so a body never "launches" the air it spawns in.</summary>
        internal void ComputeRigidMotion(float dt, out Vector3 linearVelocity, out Vector3 angularVelocity)
        {
            Transform meshTransform = meshFilter != null ? meshFilter.transform : transform;
            Vector3 position = meshTransform.position;
            Quaternion rotation = meshTransform.rotation;
            linearVelocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            if (motionValid && dt > 1e-6f)
            {
                linearVelocity = (position - previousPosition) / dt;
                Quaternion delta = rotation * Quaternion.Inverse(previousRotation);
                delta.ToAngleAxis(out float angleDegrees, out Vector3 axis);
                if (angleDegrees > 180f) angleDegrees -= 360f;
                if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x) && axis.sqrMagnitude > 1e-8f)
                    angularVelocity = axis.normalized * (angleDegrees * Mathf.Deg2Rad / dt);
            }
            previousPosition = position;
            previousRotation = rotation;
            motionValid = true;
        }

        internal void ResolveReferences()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        }

        static readonly Color BoundaryColor = new(0.18f, 0.9f, 0.72f, 0.8f);

        void OnDrawGizmosSelected()
        {
            if (!showGizmo || meshFilter == null || meshFilter.sharedMesh == null) return;
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = meshFilter.transform.localToWorldMatrix;
            Gizmos.color = BoundaryColor;
            Gizmos.DrawWireMesh(meshFilter.sharedMesh);
            Gizmos.matrix = oldMatrix;
            Gizmos.color = oldColor;
        }
    }
}
