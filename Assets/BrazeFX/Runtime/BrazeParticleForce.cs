using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Base class for particle "forces" (the publisher-owned three-js-particles prototype's effectors): world-space, per-particle
    /// pushes — Wind / Radial / Vortex / Drag (one concrete component per type). Drop one on any
    /// GameObject, then add it to a particle system's Forces list to make it affect that system, within
    /// its region + C4D-style falloff.
    ///
    /// Each particle system gathers only the force references in its own list and uploads them to its Simulate pass.
    /// </summary>
    public enum ForceShape { Infinite, Sphere, Box }

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuParticleForce
    {
        public Vector3 position;  public float type;
        public Vector3 direction; public float strength;
        public Vector3 boxHalf;   public float radius;
        public Vector4 rot;
        public Vector4 misc;      // shape, falloffRow, holdResponse (vortex) / gravityTag (directional), vortexMode
        public Vector4 jitter;    // centreJitter, directionJitter, strengthJitter, orbitRadius (vortex)
        public Vector4 extra;     // turbulence: detail, scale, scaleRandom, _
        public const int Stride = 112;
    }

    [DisallowMultipleComponent]
    public abstract class BrazeParticleForce : MonoBehaviour
    {
        public static readonly List<BrazeParticleForce> Active = new();

        // Cheap content signature of a slot-ordered set of falloff curves, for dirty-guarding
        // the per-force falloff atlas (256 evaluations + a full CPU→GPU upload each). Reads
        // keyframes via the alloc-free indexer, so it is safe to call every frame; any curve
        // edit or force reordering changes the result. Consumers cache it and skip the rebake
        // when it matches the last upload. `count` is the real force count (may be 0).
        public static int FalloffAtlasHash(List<BrazeParticleForce> forces, int count)
        {
            unchecked
            {
                int h = 17;
                for (int r = 0; r < count; r++)
                {
                    var c = forces[r].falloff;
                    if (c == null) { h = h * 31; continue; }
                    h = h * 31 + c.length;
                    for (int k = 0; k < c.length; k++)
                    {
                        var kf = c[k];
                        h = h * 31 + kf.time.GetHashCode();
                        h = h * 31 + kf.value.GetHashCode();
                        h = h * 31 + kf.inTangent.GetHashCode();
                        h = h * 31 + kf.outTangent.GetHashCode();
                    }
                }
                return h;
            }
        }

        // Which systems a force affects is decided by the SYSTEM (C4D-style): each particle system owns a Forces list.
        // Loose hierarchy forces are inert until explicitly added to a system.

        [Header("REGION")]
        [Tooltip("Infinite = everywhere; Sphere / Box = localized to this region (follows the Transform).")]
        public ForceShape shape = ForceShape.Sphere;
        [Min(0f)] [BrazeAnimatable("REGION/Radius", 0f, 50f)] public float radius = 3f;
        public Vector3 boxSize = new(4f, 4f, 4f);
        [Tooltip("Strength remap from region centre (0) to edge (1) — C4D-style falloff spline.")]
        public AnimationCurve falloff = new(new Keyframe(0f, 1f), new Keyframe(1f, 0f));

        [Header("STRENGTH")]
        [BrazeAnimatable("STRENGTH/Strength", -50f, 50f)] public float strength = 5f;

        [Header("VARIATION")]
        [Tooltip("Per-particle random offset of the force centre (world units).")]
        [Min(0f)] [BrazeAnimatable("VARIATION/Center Jitter", 0f, 10f)] public float centerJitter = 0f;
        [Tooltip("Per-particle random spread of the force direction / vortex axis.")]
        [Range(0f, 1f)] [BrazeAnimatable("VARIATION/Direction Jitter", 0f, 1f)] public float directionJitter = 0f;
        [Tooltip("Per-particle random scaling of strength.")]
        [Range(0f, 1f)] [BrazeAnimatable("VARIATION/Strength Jitter", 0f, 1f)] public float strengthJitter = 0f;

        public bool showGizmo = true;

        /// <summary>0 wind, 1 radial, 2 vortex, 3 drag, 4 turbulence.</summary>
        protected abstract float TypeId { get; }
        /// <summary>True for force types that also require per-system resources. All particle forces are explicit-list
        /// scoped at runtime; this remains available for inspectors and specialized consumers.</summary>
        public virtual bool ScopedOnly => false;
        /// <summary>Wind direction / vortex axis (world). Default = the object's forward.</summary>
        protected virtual Vector3 Direction => transform.forward;
        /// <summary>Vortex hold response (Inward/Orbit modes).</summary>
        protected virtual float HoldResponse => 0f;
        /// <summary>0 swirl, 1 inward, 2 orbit (vortex only).</summary>
        protected virtual float VortexModeId => 0f;
        /// <summary>Orbit target radius (vortex orbit mode) — jitter.w slot.</summary>
        protected virtual float OrbitRadius => 0f;
        /// <summary>Type-specific extras packed into the `extra` slot (turbulence detail/scale/scaleRandom).</summary>
        protected virtual Vector4 Extra => Vector4.zero;

        void OnEnable() => BrazeRuntimeRegistry.AddUnique(Active, this);
        void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        public virtual GpuParticleForce ToGpu(int falloffRow) => new()
        {
            position = transform.position,
            type = TypeId,
            direction = Direction.sqrMagnitude > 1e-8f ? Direction.normalized : Vector3.forward,
            strength = strength,
            boxHalf = boxSize * 0.5f,
            radius = radius,
            rot = new Vector4(transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w),
            misc = new Vector4((float)shape, falloffRow, HoldResponse, VortexModeId),
            jitter = new Vector4(centerJitter, directionJitter, strengthJitter, OrbitRadius),
            extra = Extra,
        };

        protected virtual Color GizmoColor => new(0.3f, 0.7f, 1f, 0.7f);

        protected virtual void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            if (shape == ForceShape.Box) Gizmos.DrawWireCube(Vector3.zero, boxSize);
            else if (shape == ForceShape.Sphere) Gizmos.DrawWireSphere(Vector3.zero, radius);
        }
    }
}
