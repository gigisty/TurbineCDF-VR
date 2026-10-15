using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>Shapes supported by the first GPU CFD source packet.</summary>
    public enum BrazeCfdSourceShape
    {
        Sphere = 0,
        Box = 1,
        Cylinder = 2,
    }

    /// <summary>Shared domain targeting and lifecycle for CFD-only sources.</summary>
    [ExecuteAlways]
    public abstract class BrazeCfdSource : MonoBehaviour
    {
        internal static readonly List<BrazeCfdSource> Active = new();

        [Tooltip("The CFD domain affected by this source. When empty, the closest parent domain is used.")]
        public BrazeCfdDomain domain;
        [Tooltip("Draw the source volume and direction in the Scene view.")]
        public bool showGizmo = true;

        protected virtual void OnEnable()
        {
            ResolveDomain();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        protected virtual void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        protected virtual void OnValidate()
        {
            ResolveDomain();
            if (isActiveAndEnabled) BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        internal bool Targets(BrazeCfdDomain candidate) => candidate != null && domain == candidate;

        internal void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }
    }
}
