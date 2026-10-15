using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Attract - Repel", 6)]
    public sealed class BrazePyroAttractForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.Attract;
    }
}
