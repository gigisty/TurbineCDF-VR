using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Gravity", 5)]
    public sealed class BrazePyroGravityForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.Gravity;
    }
}
