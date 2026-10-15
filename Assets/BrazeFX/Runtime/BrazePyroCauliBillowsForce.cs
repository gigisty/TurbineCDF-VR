using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Cauli Billows", 9)]
    public sealed class BrazePyroCauliBillowsForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.CauliBillows;
    }
}
