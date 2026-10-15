using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>A one-shot command that releases and pushes Voronoi fragments on the GPU.</summary>
    [Serializable]
    public struct BrazeFractureImpact
    {
        [Tooltip("World-space hit point or the start of the penetration capsule.")]
        public Vector3 position;
        [Tooltip("World-space projectile/blast direction. A capsule extends from Position along this direction.")]
        public Vector3 direction;
        [Min(0.001f)] [Tooltip("Impact radius in world units.")]
        public float radius;
        [Min(0f)] [Tooltip("Velocity added to affected fragments.")]
        public float impulse;
        [Min(0f)] [Tooltip("Random angular velocity added to affected fragments, in radians per second.")]
        public float spin;
        [Range(0f, 1f)] [Tooltip("0 follows Direction; 1 pushes radially away from Position.")]
        public float radialBlend;
        [Min(0f)] [Tooltip("Additional upward bias mixed into the impulse direction.")]
        public float upwardBias;
        [Min(0f)] [Tooltip("Zero selects a sphere. Positive values select a capsule extending along Direction.")]
        public float penetrationDepth;
        [Range(0f, 2f)] [Tooltip("How much Source Velocity is inherited by affected fragments.")]
        public float velocityInheritance;
        public Vector3 sourceVelocity;
        [Min(0.01f)] [Tooltip("Radial falloff exponent. 1 is linear; larger values concentrate force near the centre.")]
        public float falloff;
        [Tooltip("When enabled, an impact also pushes fragments that were already released.")]
        public bool affectReleased;
        [Tooltip("Stable random seed. Zero asks the fracture to generate one when queued.")]
        public int seed;

        public BrazeFractureImpact(Vector3 position, Vector3 direction, float radius, float impulse)
        {
            this.position = position;
            this.direction = direction;
            this.radius = radius;
            this.impulse = impulse;
            spin = 5f;
            radialBlend = 0.5f;
            upwardBias = 0.1f;
            penetrationDepth = 0f;
            velocityInheritance = 0f;
            sourceVelocity = Vector3.zero;
            falloff = 1f;
            affectReleased = true;
            seed = 0;
        }

        /// <summary>Create a spherical blast command.</summary>
        public static BrazeFractureImpact Sphere(Vector3 position, float radius, float impulse)
        {
            var impact = new BrazeFractureImpact(position, Vector3.up, radius, impulse);
            impact.radialBlend = 1f;
            return impact;
        }

        /// <summary>Create a capsule-shaped penetrating hit.</summary>
        public static BrazeFractureImpact Penetrating(Vector3 position, Vector3 direction,
                                                       float radius, float depth, float impulse)
        {
            var impact = new BrazeFractureImpact(position, direction, radius, impulse);
            impact.penetrationDepth = depth;
            impact.radialBlend = 0f;
            return impact;
        }

        internal GpuFractureImpact ToGpu(int resolvedSeed, in BrazeFractureFluidImpact fluid)
        {
            Vector3 safeDirection = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
            return new GpuFractureImpact
            {
                position = position,
                radius = Mathf.Max(0.001f, radius),
                direction = safeDirection,
                impulse = Mathf.Max(0f, impulse),
                sourceVelocity = sourceVelocity,
                spin = Mathf.Max(0f, spin),
                radialBlend = Mathf.Clamp01(radialBlend),
                upwardBias = Mathf.Max(0f, upwardBias),
                penetrationDepth = Mathf.Max(0f, penetrationDepth),
                velocityInheritance = Mathf.Clamp(velocityInheritance, 0f, 2f),
                falloff = Mathf.Max(0.01f, falloff),
                seed = resolvedSeed,
                flags = (affectReleased ? GpuFractureImpact.AffectReleased : 0) |
                        (fluid.enabled ? GpuFractureImpact.EmitFluid : 0),
                fluidChannels = fluid.enabled ? fluid.channels : Vector3.zero,
                fluidDuration = fluid.enabled ? Mathf.Max(0f, fluid.duration) : 0f,
                fluidBuoyancy = fluid.buoyancy,
                fluidVelocityInherit = Mathf.Clamp01(fluid.velocityInherit),
                fluidCellFraction = Mathf.Clamp01(fluid.cellFraction),
                fluidBlastDirection = Mathf.Clamp(fluid.blastDirection, -1f, 1f),
                fluidBurstSpeed = Mathf.Max(0f, fluid.burstSpeed),
                fluidRadialSpread = Mathf.Clamp01(fluid.radialSpread),
                fluidTurbulence = Mathf.Max(0f, fluid.turbulence),
                fluidSurfaceBias = Mathf.Clamp01(fluid.surfaceBias),
            };
        }
    }

    internal struct BrazeFractureFluidImpact
    {
        public bool enabled;
        public Vector3 channels;
        public float duration;
        public float cellFraction;
        public float buoyancy;
        public float velocityInherit;
        public float burstSpeed;
        public float blastDirection;
        public float radialSpread;
        public float turbulence;
        public float surfaceBias;
        public BrazeContainer container;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuFractureImpact
    {
        public Vector3 position; public float radius;
        public Vector3 direction; public float impulse;
        public Vector3 sourceVelocity; public float spin;
        public float radialBlend; public float upwardBias;
        public float penetrationDepth; public float velocityInheritance;
        public float falloff; public int seed; public int flags; public float padding;
        public Vector3 fluidChannels; public float fluidDuration;
        public float fluidBuoyancy; public float fluidVelocityInherit; public float fluidCellFraction; public float fluidBlastDirection;
        public float fluidBurstSpeed; public float fluidRadialSpread; public float fluidTurbulence; public float fluidSurfaceBias;

        public const int AffectReleased = 1 << 0;
        public const int EmitFluid = 1 << 1;
        public const int Stride = 128;
    }

}
