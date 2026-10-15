using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Header for a streamed BrazeFX fluid cache. The heavy per-frame voxel data lives in a sibling
    /// <c>.brazecache</c> binary (streamed at playback so multi-GB shots don't bloat Unity serialization);
    /// this lightweight asset holds the metadata + the relative path to that binary.
    ///
    /// The cache stores only final, camera-independent renderer textures — sparse conditioned density + temperature
    /// + burn plus the complete already-baked Render Detail texture at its native resolution. Playback scatters them
    /// directly, without advancing a simulation, downsampling detail, or regenerating noise from velocity/flow data.
    /// </summary>
    public sealed class BrazeFluidCacheAsset : ScriptableObject
    {
        [Tooltip("Format version of the sibling .brazecache binary.")]
        public int version = BrazeFluidCacheFormat.Version;

        [Header("Grid")]
        [Tooltip("Base sim resolution captured at bake time (the longest axis = the container's Resolution).")]
        public Vector3Int baseRes = new Vector3Int(64, 64, 64);
        [Tooltip("Native final-look Render Detail texture resolution. Zero when Render Detail was disabled.")]
        public Vector3Int warpRes = Vector3Int.zero;
        [Tooltip("The cache carries an authoritative base-resolution velocity field in grid voxels per second.")]
        public bool hasVelocity;
        [HideInInspector, Tooltip("The cache uses the lossy per-brick Realtime scalar representation and must not be used as an authoritative interchange source.")]
        public bool scalarQuantized;
        [Tooltip("Sparse tile edge in voxels (matches the solver's occupancy brick).")]
        public int brickSize = BrazeFluidCacheFormat.DefaultBrickSize;

        [Header("Timing")]
        public int frameCount;
        [Tooltip("Bake rate the frames were captured at (playback resamples to display time).")]
        public float fps = 30f;

        [Header("Placement / size")]
        [Tooltip("Container world size × scale when baked — informational; the live container Transform places the cache.")]
        public Vector3 worldSizeAtBake = Vector3.one;

        [Header("Binary")]
        [Tooltip("Path to the .brazecache binary, relative to this asset's folder.")]
        public string binaryFile = "";
        [HideInInspector, Tooltip("Collision-free path used when the editor injects this binary into Player StreamingAssets.")]
        public string buildBinaryFile = "";
        [HideInInspector, Tooltip("Source identity used to invalidate URI-extracted Player copies after a re-bake.")]
        public string buildFingerprint = "";
        [Tooltip("Size of the .brazecache binary in bytes (for the inspector readout).")]
        public long binaryBytes;

        [Header("Look")]
        [Tooltip("The appearance the clip was baked with (smoke/fire/lighting/detail). Lets a container-free " +
                 "BrazeCachePlayer render the baked look standalone; a player may override it per-instance.")]
        public bool hasBakedLook;
        public BrazeRenderSettings bakedLook = BrazeRenderSettings.Default;
        [Tooltip("Snapshot of the live container's resolved directional light, used when the player has no Key Light.")]
        public bool hasBakedKeyLight;
        public Vector3 bakedKeyLightDirection = new Vector3(0.3f, 0.9f, 0.2f).normalized;
        [ColorUsage(false, true)] public Color bakedKeyLightColor = new Color(1f, 0.96f, 0.9f, 1f);

        /// <summary>Clip length in seconds at the baked rate.</summary>
        public float Duration => frameCount > 0 ? frameCount / Mathf.Max(fps, 1e-3f) : 0f;

        /// <summary>True when the header looks like a usable, non-empty cache.</summary>
        public bool IsValid => version == BrazeFluidCacheFormat.Version && frameCount > 0 &&
                               baseRes.x >= 8 && baseRes.y >= 8 && baseRes.z >= 8
                               && !string.IsNullOrEmpty(binaryFile);

        /// <summary>Occupancy (brick) grid resolution = ceil(baseRes / brickSize), per axis.</summary>
        public Vector3Int BrickGrid => new Vector3Int(
            Mathf.CeilToInt((float)baseRes.x / Mathf.Max(1, brickSize)),
            Mathf.CeilToInt((float)baseRes.y / Mathf.Max(1, brickSize)),
            Mathf.CeilToInt((float)baseRes.z / Mathf.Max(1, brickSize)));
    }
}
