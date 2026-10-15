using UnityEngine;

namespace BrazeFX
{
    /// <summary>Radial force — pushes particles away from the origin (positive strength) or pulls
    /// them in (negative strength). Good for blasts and implosions.</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Radial", 33)]
    public sealed class BrazeRadial : BrazeParticleForce
    {
        protected override float TypeId => 1f;
        protected override Color GizmoColor => new(1f, 0.6f, 0.3f, 0.7f);
    }
}
