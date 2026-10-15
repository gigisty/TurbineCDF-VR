using UnityEngine;

namespace BrazeFX
{
    /// <summary>Drag force — dampens particle velocity inside its region. Good for slow zones,
    /// sticky air, settling. Strength = drag amount (per second).</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Drag", 30)]
    public sealed class BrazeDrag : BrazeParticleForce
    {
        protected override float TypeId => 3f;   // strength is the drag coefficient (per second)
        protected override Color GizmoColor => new(0.5f, 0.9f, 0.7f, 0.7f);
    }
}
