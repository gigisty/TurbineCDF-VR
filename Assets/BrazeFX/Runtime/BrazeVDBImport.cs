using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace BrazeFX
{
    /// <summary>Quality controls for the VDB-only GPU reconstruction pass. Each stored frame pair solves
    /// deterministically; seeking and reverse editor scrubbing never depend on playback history. During sequential
    /// forward playback only, <see cref="temporalSmoothing"/> additionally carries the previous frame's solved
    /// motion forward (warm start + advected exponential blend) so independently estimated pairs stop disagreeing
    /// at frame boundaries. Any non-sequential step resets that history.</summary>
    [System.Serializable]
    public struct BrazeVdbReconstructionSettings
    {
        public bool enabled;
        [Range(4, 24)] public int iterations;
        [Range(0.02f, 1f)] public float smoothness;
        [Range(1f, 16f)] public float maxMotionVoxelsPerFrame;
        [Range(0.25f, 4f)] public float flowMemoryFrames;
        [Range(0f, 0.95f)] public float temporalSmoothing;
        public bool fullResolutionMotion;   // solve the finest pyramid level at full res (slow); off = half res

        public static BrazeVdbReconstructionSettings Cinematic => new()
        {
            enabled = true,
            iterations = 12,
            smoothness = 0.18f,
            maxMotionVoxelsPerFrame = 4f,
            flowMemoryFrames = 1.5f,
            temporalSmoothing = 0.6f,
            fullResolutionMotion = false,
        };

        internal BrazeVdbReconstructionSettings Validated()
        {
            iterations = Mathf.Clamp(iterations <= 0 ? 12 : iterations, 4, 24);
            smoothness = Mathf.Clamp(smoothness <= 0f ? 0.18f : smoothness, 0.02f, 1f);
            // Floor of 1: this is a measurement ceiling, and any value below a voxel/frame clamps ALL motion —
            // "minimum quality" intuition turns it into a freeze-everything knob.
            maxMotionVoxelsPerFrame = Mathf.Clamp(maxMotionVoxelsPerFrame <= 0f ? 4f : maxMotionVoxelsPerFrame, 1f, 16f);
            flowMemoryFrames = Mathf.Clamp(flowMemoryFrames <= 0f ? 1.5f : flowMemoryFrames, 0.25f, 4f);
            temporalSmoothing = Mathf.Clamp(temporalSmoothing, 0f, 0.95f);   // 0 = per-pair solve only, a valid choice
            return this;
        }
    }

    /// <summary>
    /// Scene-owned OpenVDB importer and player. Editor import creates the runtime-ready sparse data automatically;
    /// playback is supplied by the neutral volume playback engine shared with baked caches.
    /// </summary>
    [MovedFrom(true, "BrazeFX", null, "BrazeImport")]
    [AddComponentMenu("BrazeFX/Baking/Braze VDB Import", 30)]
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class BrazeVDBImport : BrazeVolumePlayer
    {
        /// <summary>Enabled VDB players. Kept explicit instead of searching the scene every particle frame.</summary>
        public static readonly List<BrazeVDBImport> Active = new();

        [Tooltip("A standalone OpenVDB .vdb still, any numbered .vdb with a sibling Braze manifest, or the " +
                 "_Volume.manifest.json sequence. May live outside the Unity project.")]
        public string sourceFile = "";

        [Header("Particle Fluid Follow")]
        [Tooltip("Particle systems carried by this VDB volume's velocity field. This is an advection-only connection: " +
                 "each particle system keeps its own duration, looping, rate, bursts, and spawn shape.")]
        public List<BrazeParticleSystem> connectedParticleEmitters = new();
        [Tooltip("How tightly connected particles chase the VDB velocity over their lifetime. 0 disables VDB " +
                 "advection; 1 makes them track the field tightly.")]
        [Range(0f, 1f)] public float particleFluidFollow = 1f;
        [Tooltip("Multiplier on the VDB velocity connected particles chase. 1 follows the imported motion as-is; " +
                 "higher values exaggerate travel without changing its direction.")]
        [Range(0f, 8f)] public float particleFluidFollowGain = 1f;
        [Tooltip("Per-particle random variation in follow tightness. 0 is uniform; 1 gives a wide mix of lagging and " +
                 "tightly carried particles.")]
        [Range(0f, 1f)] public float particleFluidFollowRand = 0f;
        [Tooltip("Per-particle random plus/minus variation on Follow Boost.")]
        [Range(0f, 1f)] public float particleFluidFollowGainRand = 0f;

        [Header("VDB GPU Reconstruction")]
        [Tooltip("Build motion-aware renderer detail without running a fluid simulation. When the VDB contains `vel`, " +
                 "that imported field is authoritative; otherwise adjacent scalar frames use the GPU optical-flow " +
                 "fallback. Imported velocity remains available to particles even when this renderer option is off.")]
        public bool reconstructRendererData = true;
        [Tooltip("3D optical-flow refinement passes per pyramid level. The coarse-to-fine solve captures fast motion " +
                 "cheaply; twelve is the high-quality default, raise only for unusually noisy sources.")]
        [Range(4, 24)] public int motionIterations = 12;
        [Tooltip("Spatial coherence of the reconstructed motion. Lower follows small flame motion; higher produces broader plume flow.")]
        [Range(0.02f, 1f)] public float motionSmoothness = 0.18f;
        [Tooltip("Ceiling on measurable motion, in voxels per stored frame. Set at or above the fastest real motion " +
                 "in the clip — values below it visibly freeze fast regions. It also guards topology changes from " +
                 "producing explosive reprojection vectors.")]
        [Range(1f, 16f)] public float maxMotionVoxelsPerFrame = 4f;
        [Tooltip("How many frames of imported or reconstructed travel anchor the procedural detail field.")]
        [Range(0.25f, 4f)] public float detailFlowMemory = 1.5f;
        [Tooltip("Carries each frame's reconstructed motion into the next solve during sequential playback, removing " +
                 "frame-boundary jitter in the inferred flow. Seeking and scrubbing stay deterministic (history " +
                 "resets on any non-sequential step). 0 disables and restores fully independent per-frame solves.")]
        [Range(0f, 0.95f)] public float motionTemporalSmoothing = 0.6f;
        [Tooltip("Solve the motion field at the volume's full resolution instead of half. Motion is smooth, so half " +
                 "resolution is visually near-identical and several times cheaper; enable only when the smallest " +
                 "curls visibly need their own motion.")]
        public bool motionFullResolution = false;

        protected override BrazeVdbReconstructionSettings VdbReconstruction => new()
        {
            enabled = reconstructRendererData,
            iterations = motionIterations,
            smoothness = motionSmoothness,
            maxMotionVoxelsPerFrame = maxMotionVoxelsPerFrame,
            flowMemoryFrames = detailFlowMemory,
            temporalSmoothing = motionTemporalSmoothing,
            fullResolutionMotion = motionFullResolution,
        };

        /// <summary>Re-scatter the visible frame and rebuild its renderer inputs after runtime setting changes.</summary>
        public void ReconstructCurrentFrame() => RefreshLook();

        /// <summary>True when this VDB explicitly supplies lifetime fluid motion to <paramref name="system"/>.
        /// Unlike a <see cref="BrazePyroEmitter"/> connection, this never changes how the system spawns.</summary>
        public bool DrivesParticleFluidFollow(BrazeParticleSystem system)
        {
            if (system == null || connectedParticleEmitters == null) return false;
            for (int i = 0; i < connectedParticleEmitters.Count; i++)
                if (connectedParticleEmitters[i] == system) return true;
            return false;
        }

        /// <summary>Resolve an explicit VDB motion source for a particle system. A ready source wins when the same
        /// system is listed on several VDB players; otherwise the first enabled connection is retained so an
        /// intentional VDB connection cannot silently fall through to an unrelated live/Pyro container while loading.</summary>
        public static bool TryGetParticleFluidSource(BrazeParticleSystem system, out BrazeVDBImport source)
        {
            source = null;
            if (system == null) return false;

            BrazeRuntimeRegistry.PruneNulls(Active);
            for (int i = 0; i < Active.Count; i++)
            {
                BrazeVDBImport candidate = Active[i];
                if (candidate == null || !candidate.isActiveAndEnabled ||
                    !candidate.DrivesParticleFluidFollow(system)) continue;

                source ??= candidate;
                if (candidate.particleFluidFollow > 0f &&
                    candidate.RenderHost != null && candidate.RenderHost.FluidReady)
                {
                    source = candidate;
                    return true;
                }
            }

            return source != null;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        protected override void OnDisable()
        {
            // Stop advertising the field before the base class destroys its hidden playback host.
            BrazeRuntimeRegistry.Remove(Active, this);
            base.OnDisable();
        }

        void Reset()
        {
            autoLoad = true;
            playOnLoad = true;
            streaming = true;
            reconstructRendererData = true;
            motionIterations = 12;
            motionSmoothness = 0.18f;
            maxMotionVoxelsPerFrame = 4f;
            detailFlowMemory = 1.5f;
            motionTemporalSmoothing = 0.6f;
            motionFullResolution = false;
            particleFluidFollow = 1f;
            particleFluidFollowGain = 1f;
            particleFluidFollowRand = 0f;
            particleFluidFollowGainRand = 0f;
        }

        protected override void OnValidate()
        {
            // Reset supplies safe VDB defaults, but imported volumes remain full players: users may intentionally
            // choose manual loading or whole-clip residency for a short effect.
            particleFluidFollow = Mathf.Clamp01(particleFluidFollow);
            particleFluidFollowGain = Mathf.Clamp(particleFluidFollowGain, 0f, 8f);
            particleFluidFollowRand = Mathf.Clamp01(particleFluidFollowRand);
            particleFluidFollowGainRand = Mathf.Clamp01(particleFluidFollowGainRand);
            base.OnValidate();
        }
    }
}
