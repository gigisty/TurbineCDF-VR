using UnityEngine;

namespace BrazeFX
{
    /// <summary>Field that defines structure: erode the density by fractal noise, add billow lobes, or domain-warp it
    /// — all within the field's falloff.</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Noise")]
    [ExecuteAlways]
    public sealed class BrazeNoiseField : BrazeVolumeField
    {
        public enum NMode { Erode = 0, Billow = 1, Warp = 2, Caves = 3 }
        // Basis character: Value=smooth, Billow=rounded beads, Ridged=veins/streaks, Cells=cracked cell walls,
        // Turbulence=billowy puffs, Nebula=thin sharp branching filaments (ridged multifractal — dust lanes / veins).
        // (Int values are baked into _NoiseKind — keep them stable.)
        public enum NKind { Value = 0, Billow = 1, Ridged = 2, Cells = 3, Turbulence = 4, Nebula = 5 }

        [Tooltip("Erode = carve where the noise is low (cauliflower bite); Billow = add lobes; Warp = domain-displace " +
                 "for swirl; Caves = carve connected winding tunnels through the body (inner cloud carvings — Coverage " +
                 "moves them, Edge Hardness sets width, add Warp to wind them).")]
        public NMode mode = NMode.Erode;
        public NKind kind = NKind.Ridged;
        [Tooltip("Strength. Erode: 1 = fully carve voids where the noise mask is 0; Billow: density added in lobes.")]
        [Range(0f, 2f)] [BrazeAnimatable("NOISE/Amount", 0f, 2f)] public float amount = 1f;
        [Tooltip("Edge Hardness: 1 = soft modulation, high = hard binary cuts through the volume.")]
        [Range(0.25f, 8f)] [BrazeAnimatable("NOISE/Edge Hardness", 0.25f, 8f)] public float edgeHardness = 3f;
        [Tooltip("Coverage: the iso-level the cut happens at. Lower keeps more; higher carves more.")]
        [Range(0f, 1f)] [BrazeAnimatable("NOISE/Threshold", 0f, 1f)] public float threshold = 0.5f;
        [Tooltip("Feature size in world units.")]
        [Min(0.01f)] [BrazeAnimatable("NOISE/Scale", 0.01f, 10f)] public float scale = 2.5f;
        [Tooltip("Detail layers. Lacunarity/Persistence only act when this is > 1.")]
        [Range(1, 10)] [BrazeAnimatable("NOISE/Octaves", 1, 10)] public int octaves = 4;
        [Tooltip("Frequency jump per octave — higher = finer added detail.")]
        [Range(1.25f, 4f)] [BrazeAnimatable("NOISE/Lacunarity", 1.25f, 4f)] public float lacunarity = 2.02f;
        [Tooltip("Roughness — how strongly each finer octave crinkles the shape.")]
        [Range(0.2f, 1f)] [BrazeAnimatable("NOISE/Persistence", 0.2f, 1f)] public float persistence = 0.55f;
        [Range(0f, 4f)] [BrazeAnimatable("NOISE/Warp", 0f, 4f)] public float warp = 0f;
        public Vector3 offset = Vector3.zero;
        [Tooltip("Animate the noise over time (units/sec). Mask it to a Spline region to flow erosion along a path. " +
                 "0 = static. The volume re-bakes each frame while this is non-zero.")]
        public Vector3 flow = Vector3.zero;
        [Range(0, 64)] public int seed = 0;

        public override string KernelName => "FilterNoise";
        public override string DisplayName => $"Noise ({mode})";
        protected override Color GizmoColor => new Color(0.5f, 0.85f, 1f);
        public override bool Animating => flow != Vector3.zero;

        public override void SetParams(ComputeShader cs, int k)
        {
            cs.SetInt("_NoiseMode", (int)mode);
            cs.SetInt("_NoiseKind", (int)kind);
            cs.SetFloat("_NoiseAmount", Mathf.Max(0f, amount));
            cs.SetFloat("_NoiseContrast", Mathf.Clamp(edgeHardness, 0.25f, 8f));
            cs.SetFloat("_NoiseThreshold", Mathf.Clamp01(threshold));
            cs.SetFloat("_NoiseScale", Mathf.Max(0.01f, scale));
            cs.SetInt("_NoiseOctaves", Mathf.Clamp(octaves, 1, 10));
            cs.SetFloat("_NoiseLacunarity", Mathf.Clamp(lacunarity, 1.25f, 4f));
            cs.SetFloat("_NoisePersistence", Mathf.Clamp(persistence, 0.2f, 1f));
            cs.SetFloat("_NoiseWarp", Mathf.Clamp(warp, 0f, 4f));
            cs.SetVector("_NoiseOffset", offset + flow * FieldTime);
            cs.SetFloat("_NoiseSeed", seed);
        }
    }
}
