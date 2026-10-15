using UnityEngine;

namespace BrazeFX
{
    /// <summary>Vortex force — swirls particles around the object's up axis. Three motions:
    /// Swirl (speed-preserving rotation of the velocity about the axis — particles circle stably without being flung
    /// out; Strength = rad/sec), Inward (swirl + suck toward the axis), Orbit (swirl + hold at a target radius and
    /// damp axial drift). Good for tornados, black holes, magic, smoke curls.</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Vortex", 34)]
    public sealed class BrazeVortex : BrazeParticleForce
    {
        public enum VortexMotion { Swirl, Inward, Orbit }

        [Header("VORTEX")]
        [Tooltip("Swirl = rotate velocity around the axis (stable circling, no fling, Strength = rad/s); " +
                 "Inward = swirl + pull toward the axis; Orbit = swirl + hold at a radius and damp axial drift.")]
        public VortexMotion motion = VortexMotion.Swirl;
        [Tooltip("How firmly Inward/Orbit pull particles to their target radius.")]
        [Min(0f)] [BrazeAnimatable("VORTEX/Hold Strength", 0f, 20f)] public float holdStrength = 4f;
        [Tooltip("Target orbit radius (Orbit mode).")]
        [Min(0f)] [BrazeAnimatable("VORTEX/Orbit Radius", 0f, 20f)] public float orbitRadius = 1f;

        [Header("AXIAL (column height)")]
        [Tooltip("Rise/sink speed along the swirl axis (units/sec, + = along the arrow, − = opposite). Drives the " +
                 "column on its own: height ≈ Lift × Start Lifetime. 0 = no driven flow (free column).")]
        [BrazeAnimatable("AXIAL/Lift", -20f, 20f)] public float lift = 0f;
        [Tooltip("How firmly the vortex holds particles' along-axis speed to Lift, and how strongly it settles a " +
                 "free column's drift. 0 = gentle/loose (Lift still works); higher = snappier. With Lift = 0 and " +
                 "Damp > 0 the column flattens to a disc.")]
        [Range(0f, 1f)] [BrazeAnimatable("AXIAL/Axial Damp", 0f, 1f)] public float axialDamp = 0f;
        [Tooltip("Per-particle ± randomness on Lift (fraction), so particles rise/sink at different rates and reach " +
                 "different heights — a softer, more organic column instead of a flat-topped lockstep one.")]
        [Range(0f, 1f)] [BrazeAnimatable("AXIAL/Lift Variation", 0f, 1f)] public float liftVariation = 0f;

        protected override float TypeId => 2f;
        protected override Vector3 Direction => transform.up;     // swirl axis
        protected override float HoldResponse => holdStrength;
        protected override float VortexModeId => (float)motion;
        protected override float OrbitRadius => orbitRadius;
        protected override Vector4 Extra => new(lift, axialDamp, liftVariation, 0f);   // axial flow + damp + lift jitter (extra is vortex-free)
        protected override Color GizmoColor => new(0.7f, 0.5f, 1f, 0.7f);

        protected override void OnDrawGizmos()
        {
            base.OnDrawGizmos();
            if (!showGizmo) return;
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = GizmoColor;
            Vector3 p = transform.position;
            Gizmos.DrawLine(p - transform.up * 0.9f, p + transform.up * 0.9f);   // swirl axis
            if (motion == VortexMotion.Orbit)
            {
                Gizmos.matrix = Matrix4x4.TRS(p, transform.rotation, Vector3.one);
                DrawCircleXZ(orbitRadius);
            }
        }

        static void DrawCircleXZ(float r)
        {
            const int seg = 32;
            Vector3 prev = new(r, 0, 0);
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = new(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
