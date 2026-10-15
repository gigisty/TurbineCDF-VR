using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A particle collider — Plane / Sphere / Box / Mesh surfaces that particles bounce off, slide along, or
    /// stick to. Standalone like the forces and emitters: drop it on any GameObject, it self-registers and
    /// affects ALL particle systems. Each system gathers the active colliders and uploads them to its Simulate
    /// pass; collision is resolved per-particle on the GPU (SDF penetration push-out + velocity reflection).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuParticleCollider
    {
        public Vector3 center;   public float shape;   // 0 plane, 1 sphere, 2 box
        public Vector3 normal;   public float radius;  // plane normal (xyz) / sphere radius (w)
        public Vector3 extent;   public float _pad0;   // box half-extents (xyz)
        public Vector4 rot;                            // box orientation (world quaternion)
        public Vector4 response;                       // bounce, friction, strength, _
        public Vector4 jitter;                         // per-particle ± fraction: bounce, friction, strength, _
        public const int Stride = 96;                  // 6 × 16 B
    }

    [AddComponentMenu("BrazeFX/Particles/Braze Particle Collider", 21)]
    [DisallowMultipleComponent]
    public sealed class BrazeParticleCollider : MonoBehaviour
    {
        public static readonly List<BrazeParticleCollider> Active = new();

        public enum Shape { Plane, Sphere, Box, Mesh }

        [Header("SHAPE")]
        [Tooltip("Size / rotate / move the collider with the Transform tools. Plane = infinite surface " +
                 "(normal = Transform up); Sphere = unit sphere × scale; Box = unit cube × scale, oriented; " +
                 "Mesh = the actual (deforming) surface of the assigned renderer.")]
        public Shape shape = Shape.Plane;
        [Tooltip("Mesh shape only: the renderer whose evaluated surface particles collide with. Skinned / " +
                 "deformed meshes are followed (one mesh collider per system; the first one is used).")]
        public Renderer meshRenderer;

        [Header("RESPONSE")]
        [Tooltip("Bounciness: 0 = slide along the surface, 1 = full elastic bounce.")]
        [BrazeAnimatable("RESPONSE/Bounce", 0f, 1f)]
        [Range(0f, 1f)] public float bounce = 0.3f;
        [Tooltip("Tangential friction on contact: 0 = frictionless, 1 = stops sliding.")]
        [BrazeAnimatable("RESPONSE/Friction", 0f, 1f)]
        [Range(0f, 1f)] public float friction = 0.1f;
        [Tooltip("How firmly the surface acts on a particle's velocity. 1 = full bounce + friction response; " +
                 "0 = the particle keeps its velocity and just slides along (slippery). Particles are ALWAYS " +
                 "pushed out of the surface (never embedded) regardless of this value.")]
        [BrazeAnimatable("RESPONSE/Strength", 0f, 1f)]
        [Range(0f, 1f)] public float strength = 1f;

        [Header("VARIATION")]
        [Tooltip("Per-particle random ± variation of each response value (0 = uniform, 1 = ±100%). " +
                 "Each particle gets a stable random trait — some bounce more, grip more, etc.")]
        [BrazeAnimatable("VARIATION/Bounce Jitter", 0f, 1f)]
        [Range(0f, 1f)] public float bounceJitter = 0f;
        [BrazeAnimatable("VARIATION/Friction Jitter", 0f, 1f)]
        [Range(0f, 1f)] public float frictionJitter = 0f;
        [BrazeAnimatable("VARIATION/Strength Jitter", 0f, 1f)]
        [Range(0f, 1f)] public float strengthJitter = 0f;

        public bool showGizmo = true;

        void OnEnable() => BrazeRuntimeRegistry.AddUnique(Active, this);
        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            if (gizmoBake != null)
            {
                if (Application.isPlaying) Destroy(gizmoBake); else DestroyImmediate(gizmoBake);
                gizmoBake = null;
            }
        }

        Mesh gizmoBake;   // reused baked mesh for the skinned-mesh gizmo

        // Auto-detect: dropping this collider on a mesh/skinned object picks it up as a Mesh collider.
        void Reset()
        {
            var r = GetComponent<Renderer>();
            if (r != null) { meshRenderer = r; shape = Shape.Mesh; }
        }

        // The mesh-collider surface: the explicit field, else a Renderer on this GameObject (auto-detect).
        public Renderer ResolvedMeshRenderer => meshRenderer != null ? meshRenderer : GetComponent<Renderer>();

        // Sphere radius from the largest scale axis (matches Unity's SphereCollider).
        float SphereRadius { get { var s = transform.lossyScale; return 0.5f * Mathf.Max(s.x, Mathf.Max(s.y, s.z)); } }

        public GpuParticleCollider ToGpu() => new()
        {
            center = transform.position,
            shape = (float)shape,
            normal = transform.up,                       // plane normal = the object's up axis
            radius = SphereRadius,                       // sphere radius ← Transform scale
            extent = transform.lossyScale * 0.5f,        // box half-extents ← Transform scale (oriented by rot)
            rot = new Vector4(transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w),
            response = new Vector4(bounce, friction, strength, 0f),
            jitter = new Vector4(bounceJitter, frictionJitter, strengthJitter, 0f),
        };

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Gizmos.color = new Color(0.30f, 1f, 0.55f, 0.7f);
            switch (shape)
            {
                case Shape.Plane:
                    // Unit quad (±0.5) × scale → matches the finite collision bounds (extent = scale × 0.5).
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Vector3 a = new(-0.5f, 0f, -0.5f), b = new(0.5f, 0f, -0.5f), c = new(0.5f, 0f, 0.5f), d = new(-0.5f, 0f, 0.5f);
                    Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
                    Gizmos.DrawLine(a, c); Gizmos.DrawLine(b, d);          // diagonals → reads as a surface
                    Gizmos.matrix = Matrix4x4.identity;
                    Gizmos.DrawLine(transform.position, transform.position + transform.up);  // normal
                    break;
                case Shape.Sphere:
                    Gizmos.DrawWireSphere(transform.position, SphereRadius);
                    break;
                case Shape.Box:
                    Gizmos.matrix = transform.localToWorldMatrix;          // unit cube × scale, oriented
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                    Gizmos.matrix = Matrix4x4.identity;
                    break;
                case Shape.Mesh:
                    var mr = ResolvedMeshRenderer;
                    if (mr == null) break;
                    Mesh gm = GizmoMesh(mr);
                    if (gm != null)
                        // wireframe of the ACTUAL (deforming) surface particles collide with — like Unity's Mesh Collider.
                        Gizmos.DrawWireMesh(gm, mr.transform.position, mr.transform.rotation, mr.transform.lossyScale);
                    else { var bn = mr.bounds; Gizmos.DrawWireCube(bn.center, bn.size); }   // fallback: bounds
                    break;
            }
        }

        // The mesh to draw for the gizmo: the static mesh, or a baked snapshot of the skinned/deformed mesh.
        Mesh GizmoMesh(Renderer mr)
        {
            if (mr is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) return null;
                if (gizmoBake == null) gizmoBake = new Mesh { hideFlags = HideFlags.DontSave };
                smr.BakeMesh(gizmoBake, false);   // local space (no scale) → DrawWireMesh applies the transform TRS
                return gizmoBake;
            }
            var mf = mr.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
    }
}
