using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Mushroom Disc", 7)]
    public sealed class BrazePyroDiscForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.Disc;
    }
}
