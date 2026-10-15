using UnityEngine;

namespace BrazeFX
{
    /// <summary>Directional force — a constant wind blowing along the object's forward axis.
    /// Rotate the object to aim; point it down for gravity.</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Wind", 31)]
    public sealed class BrazeWind : BrazeParticleForce
    {
        protected override float TypeId => 0f;
        protected override Vector3 Direction => transform.forward;
        protected override Color GizmoColor => new(0.5f, 0.85f, 1f, 0.7f);

        protected override void OnDrawGizmos()
        {
            base.OnDrawGizmos();
            if (!showGizmo) return;
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = GizmoColor;
            Vector3 p = transform.position;
            Gizmos.DrawLine(p, p + transform.forward * 1.2f);   // wind direction arrow
        }
    }
}
