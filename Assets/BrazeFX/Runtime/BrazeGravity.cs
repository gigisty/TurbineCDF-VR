using UnityEngine;

namespace BrazeFX
{
    /// <summary>Gravity — a constant vertical (world-Y) acceleration driven by the signed Strength:
    /// <b>negative pulls down, positive pushes up</b> (Unity's <c>Physics.gravity.y = -9.81</c> convention).
    /// Add one to make particles/chunks fall. Defaults to an Infinite region (affects everywhere); switch Region
    /// to Sphere/Box for a localized gravity zone. Always vertical — for lateral pulls use Wind / Radial.</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Gravity", 32)]
    public sealed class BrazeGravity : BrazeParticleForce
    {
        protected override float TypeId => 0f;                // directional
        protected override Vector3 Direction => Vector3.up;   // world-Y axis; the SIGN of Strength picks down/up
        protected override float HoldResponse => 1f;          // directional misc.z tag: mass-independent gravity, not wind
        protected override Color GizmoColor => new(0.65f, 0.65f, 0.72f, 0.8f);

        void Reset()
        {
            shape = ForceShape.Infinite;
            strength = -9.81f;   // negative = down (matches Physics.gravity)
        }

        protected override void OnDrawGizmos()
        {
            base.OnDrawGizmos();
            if (!showGizmo) return;
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = GizmoColor;
            Vector3 p = transform.position;
            float s = strength >= 0f ? 1f : -1f;       // arrow points the way the force pushes
            Vector3 tip = p + Vector3.up * (1.1f * s);
            Gizmos.DrawLine(p, tip);
            Gizmos.DrawLine(tip, tip + new Vector3(-0.22f, -0.32f * s, 0f));
            Gizmos.DrawLine(tip, tip + new Vector3(0.22f, -0.32f * s, 0f));
        }
    }
}
