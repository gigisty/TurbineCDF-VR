using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Baked output of a <see cref="BrazeVoronoiFracture"/>: the closed clip-and-cap fragments stored in a binary
    /// payload, plus a <see cref="signature"/> of every input that produced them. Created/written by the
    /// inspector's Bake button and loaded at runtime so the (slow) CPU clipper NEVER runs on the hot path or on
    /// OnValidate. If the signature stops matching the live fracture inputs the inspector turns red and prompts a
    /// re-bake — that is the single source of truth for "is this fracture up to date".
    ///
    /// Heavy payload lives in <see cref="blob"/> so the .asset remains tiny and safe to inspect.
    /// </summary>
    public sealed class BrazeFractureBake : ScriptableObject
    {
        public const int CurrentBlobVersion = 1;

        [Tooltip("Hash of every fracture-affecting input at bake time. Inspector compares this against the live inputs " +
                 "to decide green (current) vs red (re-bake needed). Do not edit by hand.")]
        public string signature;

        [Tooltip("Same input hash but with the mesh vertex sampling left out. Mesh.isReadable legitimately differs " +
                 "between edit mode, play mode and builds, so when the mesh can't be sampled the validity check falls " +
                 "back to this instead of reporting a false 'Bake required'. Do not edit by hand.")]
        public string signatureNoMeshSample;

        [Tooltip("Raw values of every fracture input at bake time, so the inspector can NAME what changed instead of " +
                 "only flipping the light red. Do not edit by hand.")]
        public BakedInputs inputs;
        public bool inputsRecorded;

        /// <summary>Snapshot of the geometry-affecting inputs at bake time (diagnostics only — the hash in
        /// <see cref="signature"/> stays the single source of truth for validity).</summary>
        [System.Serializable]
        public struct BakedInputs
        {
            public int geometryVersion;
            public string meshFingerprint;          // sampled (counts + bounds + strided vertices), hex
            public string meshFingerprintNoSample;  // counts + bounds only, hex
            public int pointCount;
            public int seed;
            public int fracturePattern;
            public Vector3 patternDirection;
            public float fibreSlenderness;
            public float secondaryFraction;
            public Vector3 impactPoint;
            public float impactRadius;
            public int radialRings;
            public int radialRays;
            public float radialJitter;
            public float radialExtent;
            public int distribution;
            public float variation;
            public Vector3 clusterCenter;
            public float clusterRadius;
            public float clusterStrength;
            public float clusterFalloff;
            public bool shaderSource;
            public float noiseScale;
            public int noiseSeed;
            public float densityContrast;
            public int densityTries;
            public int neighborK;
            public Vector3 scaleCells;
            public float offsetFragments;
        }

        public int fragmentCount;
        [Tooltip("Binary payload containing seeds, fragment triangle soup, centroids, and shared-face structural adjacency.")]
        public TextAsset blob;
        public int blobVersion;
        public int seedCount;
        public int vertexCount;
        public int triangleCount;
        public int neighborLinkCount;
        public long payloadBytes;

        public bool HasBlob =>
            blob != null &&
            blobVersion == CurrentBlobVersion &&
            fragmentCount > 0 &&
            seedCount > 0 &&
            vertexCount > 0;

        /// <summary>True when this asset holds a consistent, loadable fracture.</summary>
        public bool HasData => HasBlob;

        public int SeedCount => seedCount;
        public int VertexCount => vertexCount;
        public int TriangleCount => triangleCount;
        public int NeighborLinkCount => neighborLinkCount;
        public long PayloadBytes => payloadBytes;
    }
}
