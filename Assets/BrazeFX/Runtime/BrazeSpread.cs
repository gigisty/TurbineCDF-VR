using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Mesh Effect — SPREAD / COVERAGE. A front crawls outward across a mesh surface from impact points
    /// (particle collisions via <see cref="Seed"/>), like a stain or ants fanning out and clinging to the body.
    /// Coverage is a GEODESIC DISTANCE field over the welded surface graph: each point is "covered" once the
    /// growing front radius reaches its distance-from-seed. It spreads evenly OUTWARD by construction (a front
    /// can't collapse into a blob), is orientation-independent, and multiple impacts merge their fronts.
    ///
    /// The shared engine (surface graph, geodesic coverage, deform-follow, Look overlay, fluid emission) lives in
    /// <see cref="BrazeSurfaceEffect"/>; this subclass only seeds the coverage from impacts.
    /// </summary>
    [AddComponentMenu("BrazeFX/Destruction/Braze Spread", 42)]
    [DisallowMultipleComponent]
    public sealed class BrazeSpread : BrazeSurfaceEffect
    {
        [Header("Spread")]
        [Tooltip("On: each hit's coverage EXPANDS outward over time (grows like a stain / growth). " +
                 "Off: each hit only stains a fixed spot — keep hitting from all angles to fill the mesh.")]
        public bool expandSpread = true;
        [Tooltip("World units the covered front advances per second (Expand Spread on).")]
        [BrazeAnimatable("SPREAD/Spread Speed", 0f, 4f)]
        [Min(0f)] public float spreadSpeed = 0.5f;
        [Tooltip("Coverage radius of each hit when Expand Spread is OFF (world units).")]
        [BrazeAnimatable("SPREAD/Dab Radius", 0f, 1f)]
        [Min(0f)] public float dabRadius = 0.08f;

        readonly List<int> seeds = new();      // welded seed ids
        readonly List<float> seedFront = new();// front value captured when each seed landed (so it expands from then)
        float frontRadius;                     // the global front (grows at spreadSpeed when expanding)
        bool dijkstraDirty;

        protected override bool HasCoverage => seeds.Count > 0;

        protected override void OnEffectEnable()
        {
            frontRadius = 0f;
            seeds.Clear();
            seedFront.Clear();
        }

        protected override void OnEffectValidate()
        {
            dijkstraDirty = true;   // Expand-Spread toggle changes how the coverage field is seeded → recompute
        }

        protected override void OnBuilt()
        {
            seeds.Clear();
            seedFront.Clear();
            frontRadius = 0f;
            dijkstraDirty = false;
        }

        /// <summary>Queue a new impact point — the front (also) starts from the surface nearest here.</summary>
        public void Seed(Vector3 worldPos)
        {
            if (!EnsureSetup()) return;
            BakeWorld();
            int w = NearestWelded(worldPos);
            if (w >= 0) AddSeed(w);
        }

        /// <summary>Wipe coverage and seeds (the front restarts from scratch).</summary>
        public void ResetSpread()
        {
            seeds.Clear();
            seedFront.Clear();
            frontRadius = 0f;
            ClearReach();
        }

        void AddSeed(int welded)
        {
            if ((uint)welded >= (uint)weldedCount || seeds.Contains(welded)) return;
            seeds.Add(welded);
            seedFront.Add(frontRadius);        // expand from the CURRENT front → late hits don't pop a huge patch
            dijkstraDirty = true;
        }

        protected override void UpdateCoverage()
        {
            if (seeds.Count == 0) { coverFront = 0f; return; }
            if (dijkstraDirty)
            {
                RunDijkstra(seeds, expandSpread ? seedFront : null);   // expand: front-at-landing; dab: raw geodesic
                dijkstraDirty = false;
            }
            if (expandSpread) frontRadius += spreadSpeed * Time.deltaTime;   // off = fixed-size dabs, no growth
            coverFront = expandSpread ? frontRadius : Mathf.Max(1e-4f, dabRadius);
        }
    }
}
