using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Lift", 10)]
    public sealed class BrazePyroLiftForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.Lift;
    }
}
