using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Surface Flow — moves particles in swirling, curling currents ACROSS a mesh's surface, contained to it.
    /// POSITION-BASED: unlike the push forces, this is a constraint that runs after integration and collisions —
    /// every frame each particle is projected back into a thin shell around the mesh, so at Adhesion 1 particles
    /// cannot leave the surface no matter what other forces do. The motion itself is composed IN the surface's
    /// tangent plane: stream-function eddies (true on-surface vortices that trace filament lines), a broad
    /// laminar current, and an optional world-direction Drift (energy licking upward). The classic "energy
    /// flowing over a Patronus / hologram" look. Force type 5.
    ///
    /// The nearest-surface query reuses the system's shared mesh grid: if the particle system has a Mesh
    /// Collider, the grid is built from that; otherwise this force's own Mesh is used. (One grid per system —
    /// for a separate flow mesh, leave the collider off and set the Mesh here.)
    /// </summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Surface Flow", 36)]
    public sealed class BrazeSurfaceFlow : BrazeParticleForce
    {
        [Header("SURFACE")]
        [Tooltip("The renderer whose surface particles flow across. Its nearest point + normal are queried per " +
                 "particle. Skinned / deforming meshes are followed.")]
        public Renderer surfaceMesh;
        [Tooltip("Containment stiffness: 1 = hard shell — particles are projected back onto the surface every " +
                 "frame and CANNOT leave, whatever else pushes them. Lower = softer skin that lets motion leak.")]
        [Range(0f, 1f)] [BrazeAnimatable("SURFACE/Adhesion", 0f, 1f)] public float adhesion = 1f;
        [Tooltip("Height the skin of particles hovers above the surface (world units). 0 = right on the mesh.")]
        [BrazeAnimatable("SURFACE/Offset", -1f, 1f)] public float surfaceOffset = 0f;
        [Tooltip("Thickness of the energy skin (world units): particles keep their height freely within " +
                 "Offset ± half this, and are clamped back at the edges. Small = a tight film, larger = a " +
                 "volumetric aura.")]
        [Min(0.01f)] public float bandThickness = 0.35f;

        [Header("FLOW / TURBULENCE")]
        [Tooltip("Wave size of the flow — larger = broad sweeping currents, smaller = fine filaments.")]
        [Min(0.05f)] [BrazeAnimatable("FLOW/Scale", 0.05f, 50f)] public float turbulenceScale = 1.4f;
        [Tooltip("Adds a finer octave layered on the broad flow.")]
        [Range(0f, 1f)] [BrazeAnimatable("FLOW/Detail", 0f, 1f)] public float turbulenceDetail = 0.85f;
        [Tooltip("Eddies vs current: 1 = pure on-surface vortices circulating around noise cells (divergence-" +
                 "free stream-function flow — particles trace its filament lines), 0 = broad laminar current. " +
                 "Blend for swirling rivers.")]
        [Range(0f, 1f)] [BrazeAnimatable("FLOW/Swirl", 0f, 1f)] public float swirl = 0.65f;
        [Tooltip("How tightly particles follow the flow streamlines. High = clean, coherent filaments tracing the " +
                 "field; low = looser, driftier motion.")]
        [Range(0f, 1f)] [BrazeAnimatable("FLOW/Align", 0f, 1f)] public float flowAlign = 0.9f;

        [Header("DRIFT")]
        [Tooltip("Constant current in a world direction, pressed onto the skin — e.g. (0,1,0) makes the energy " +
                 "lick upward across the whole form. Speed in world units/sec; 0 = off.")]
        [BrazeAnimatable("DRIFT/Speed", -10f, 10f)] public float driftSpeed = 0f;
        [Tooltip("World direction of the drift current (projected onto the surface per particle).")]
        public Vector3 driftDirection = Vector3.up;

        protected override float TypeId => 5f;
        // Bound to one system's mesh grid → only affects a system that explicitly adds it (never global).
        public override bool ScopedOnly => true;
        protected override float HoldResponse => Mathf.Clamp01(swirl);        // misc.z — eddies vs current blend
        protected override float VortexModeId => Mathf.Max(0.01f, bandThickness);   // misc.w — shell thickness
        protected override float OrbitRadius => Mathf.Clamp01(flowAlign);     // jitter.w — flow alignment / coherence
        protected override Vector3 Direction =>
            driftDirection.sqrMagnitude > 1e-8f ? driftDirection : Vector3.up; // direction.xyz — drift direction
        protected override Vector4 Extra =>
            new(turbulenceDetail, turbulenceScale, surfaceOffset, adhesion);  // extra.xyzw

        // Direction Jitter is meaningless for a surface-bound flow — its jitter.y slot carries the drift speed.
        public override GpuParticleForce ToGpu(int falloffRow)
        {
            var g = base.ToGpu(falloffRow);
            g.jitter.y = driftSpeed;
            return g;
        }

        // Grid reach: the shell plus headroom for one frame of travel at flow + drift speed (the GPU constraint
        // re-projects every frame, so a captured particle is never further out than that).
        internal float QueryRange =>
            Mathf.Max(0.01f, Mathf.Abs(surfaceOffset) + Mathf.Max(0.01f, bandThickness)
                           + (Mathf.Abs(strength) + Mathf.Abs(driftSpeed)) * 0.12f);

        /// <summary>The mesh whose surface particles flow along (explicit field, else a Renderer on this object).</summary>
        public Renderer ResolvedSurface => surfaceMesh != null ? surfaceMesh : GetComponent<Renderer>();

        // Flow reads as a whole-surface effect by default; Strength is the flow SPEED.
        void Reset()
        {
            shape = ForceShape.Infinite;
            strength = 2.4f;
            var r = GetComponent<Renderer>();
            if (r != null) surfaceMesh = r;
        }

        protected override Color GizmoColor => new(0.4f, 0.9f, 1f, 0.7f);
    }
}
