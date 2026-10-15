using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Spline Flow — captures particles into a <see cref="BrazeSpline"/> path and streams them ALONG it: the
    /// particles flow through the curve like fluid through a pipe / energy along a wire. Per particle it finds
    /// the nearest point on the (GPU-sampled) path, pulls toward it (Capture), pushes along the local tangent
    /// (Strength = flow speed), optionally swirls around the curve, and confines escaping motion (Stick).
    /// Force type 6. Pairs naturally with a surface-wrapped spline (BrazeSplineSurface) for energy that flows
    /// over a mesh following the wrap.
    ///
    /// Like Surface Flow it is bound to one system's auxiliary buffer (the sampled path), so it ONLY affects a
    /// particle system that explicitly lists it (Motion → Forces) — never global.
    /// </summary>
    [AddComponentMenu("BrazeFX/Spline/Braze Spline Flow", 37)]
    public sealed class BrazeSplineFlow : BrazeParticleForce
    {
        [Header("PATH")]
        [Tooltip("The spline particles flow along. Leave empty to use a BrazeSpline on this object.")]
        public BrazeSpline spline;
        [Tooltip("How far from the curve a particle can be and still get captured (world units). Particles outside " +
                 "this tube are unaffected.")]
        [Min(0.01f)] [BrazeAnimatable("PATH/Capture Radius", 0.01f, 30f)] public float captureRadius = 2.5f;
        [Tooltip("Reverse the direction of flow along the path.")]
        public bool reverse = false;

        [Header("FLOW")]
        [Tooltip("How strongly particles are pulled onto the curve. High = a tight wire; low = a loose, drifting stream.")]
        [Min(0f)] [BrazeAnimatable("FLOW/Capture", 0f, 60f)] public float capture = 14f;
        [Tooltip("How firmly particles are kept from escaping the tube (1 = sealed pipe, 0 = free to leak out).")]
        [Range(0f, 1f)] [BrazeAnimatable("FLOW/Stick", 0f, 1f)] public float stick = 0.6f;
        [Tooltip("Swirl AROUND the curve axis (rad/s feel) — gives the stream a corkscrew / helical twist. 0 = straight flow.")]
        [BrazeAnimatable("FLOW/Swirl", -40f, 40f)] public float swirl = 0f;

        protected override float TypeId => 6f;
        // Bound to one system's sampled path buffer → only affects a system that explicitly lists it (never global).
        public override bool ScopedOnly => true;
        protected override float HoldResponse => Mathf.Clamp01(stick);                          // misc.z — tube confinement
        protected override Vector4 Extra => new(capture, captureRadius, swirl, reverse ? 1f : 0f); // extra.xyzw

        /// <summary>The spline particles flow along (explicit field, else a BrazeSpline on this object).</summary>
        public BrazeSpline ResolvedSpline => spline != null ? spline : GetComponent<BrazeSpline>();

        // Strength reads as the FLOW SPEED along the path; the whole curve is the region by default.
        void Reset()
        {
            shape = ForceShape.Infinite;
            strength = 4f;
            spline = GetComponent<BrazeSpline>();
        }

        protected override Color GizmoColor => new(0.72f, 0.5f, 1f, 0.7f);
    }
}
