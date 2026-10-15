using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>
    /// Opt-in max-temperature volume view for one CFD domain. It reuses the existing GPU slice renderer and
    /// adds no simulation texture or GPU readback.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Heat Map", 4)]
    [ExecuteAlways]
    public sealed class BrazeCfdHeatMap : MonoBehaviour
    {
        internal static readonly List<BrazeCfdHeatMap> Active = new();

        [Tooltip("The CFD domain visualized by this heat map. When empty, the closest parent domain is used.")]
        public BrazeCfdDomain domain;
        [Tooltip("Temperatures at or below this value are transparent.")]
        public float minimumTemperatureC = 21f;
        [Tooltip("Temperature mapped to the hot color.")]
        public float maximumTemperatureC = 80f;
        [ColorUsage(false, true)] public Color coldColor = new(0.03f, 0.2f, 1f, 1f);
        [ColorUsage(false, true)] public Color middleColor = new(1f, 0.85f, 0.05f, 1f);
        [ColorUsage(false, true)] public Color hotColor = new(1f, 0.04f, 0.01f, 1f);
        [Range(0f, 1f), Tooltip("Maximum heat-map opacity. Rendering stays premultiplied and depth-aware.")]
        public float opacity = 0.75f;


        void OnEnable()
        {
            ResolveDomain();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            domain?.AttachHeatMap(this);
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            domain?.DetachHeatMap(this);
        }

        void OnValidate()
        {
            maximumTemperatureC = Mathf.Max(minimumTemperatureC + 0.1f, maximumTemperatureC);
            opacity = Mathf.Clamp01(opacity);
            ResolveDomain();
            if (isActiveAndEnabled)
            {
                BrazeRuntimeRegistry.AddUnique(Active, this);
                domain?.AttachHeatMap(this);
            }
        }

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;

        internal void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }
    }
}
