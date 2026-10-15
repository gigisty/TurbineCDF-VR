using UnityEngine;

namespace BrazeFX
{
    [AddComponentMenu("BrazeFX/Fire/Forces/Jet", 11)]
    public sealed class BrazePyroJetForce : BrazePyroForce, IBrazeFixedPyroForce
    {
        public ForceType FixedForceType => ForceType.Jet;
    }
}
