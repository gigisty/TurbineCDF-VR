using UnityEngine;

namespace BrazeFX
{
    /// <summary>Scene-owned entry point for opening Export Studio with a specific source Container.</summary>
    [AddComponentMenu("BrazeFX/Baking/Braze Export Studio", 31)]
    [DisallowMultipleComponent]
    public sealed class BrazeExport : MonoBehaviour
    {
        [Tooltip("Container whose simulation timeline is baked and delivered by Export Studio.")]
        public BrazeContainer container;
    }
}
