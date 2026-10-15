using UnityEngine;

namespace BrazeFX
{
    /// <summary>Field that adds or removes density inside its falloff — the directed shaping noise can't do. Remove
    /// carves caverns and pathways; Add deposits bulges and tendrils; Set clamps toward a value.</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Density (Add or Remove)")]
    [ExecuteAlways]
    public sealed class BrazeDensityField : BrazeVolumeField
    {
        public enum DOp { Add = 0, Remove = 1, Set = 2 }

        public DOp op = DOp.Remove;
        [Tooltip("Remove: 1 = fully carve to zero in the core; push past 1 for harder/wider cuts. Add: density " +
                 "deposited. Set: target density to blend toward.")]
        [Range(0f, 4f)] [BrazeAnimatable("DENSITY/Amount", 0f, 4f)] public float amount = 1f;

        [Header("Edge Noise — break up the cut")]
        [Tooltip("Organic break-up of the falloff EDGE only (the solid core and the outside are untouched): 0 = a " +
                 "clean knife cut, higher = the boundary erodes into wisps and stray pockets. The noisy band sits in " +
                 "the region's soft edge — widen Region Edge for a fatter break-up zone.")]
        [Range(0f, 1f)] [BrazeAnimatable("DENSITY/Edge Noise", 0f, 1f)] public float edgeNoise = 0f;
        [Tooltip("Feature size of the break-up, world units. Small = fine crumble; large = broad organic lobes.")]
        [Min(0.01f)] [BrazeAnimatable("DENSITY/Noise Scale", 0.01f, 10f)] public float noiseScale = 1f;
        [Tooltip("Soft ↔ chunky: 1 = smooth wobble of the edge; higher pushes the noise toward hard chunks / holes.")]
        [Range(0.25f, 8f)] [BrazeAnimatable("DENSITY/Noise Contrast", 0.25f, 8f)] public float noiseContrast = 1.5f;
        [Tooltip("Static position offset of the noise pattern (slide the break-up around).")]
        public Vector3 noiseOffset = Vector3.zero;
        [Tooltip("Animate the break-up over time (units/sec). 0 = static. Non-zero re-bakes the volume each frame.")]
        public Vector3 noiseFlow = Vector3.zero;
        [Tooltip("Seed — change for a different break-up pattern at the same settings.")]
        [Range(0, 64)] public int noiseSeed = 0;

        public override string KernelName => "FilterDensity";
        public override string DisplayName => $"Density ({op})";
        public override bool Animating => base.Animating || (edgeNoise > 0f && noiseFlow != Vector3.zero);
        protected override Color GizmoColor =>
            op == DOp.Remove ? new Color(1f, 0.4f, 0.3f) : op == DOp.Add ? new Color(0.4f, 1f, 0.6f) : new Color(1f, 0.85f, 0.3f);

        public override void SetParams(ComputeShader cs, int k)
        {
            cs.SetInt("_Op", (int)op);
            cs.SetFloat("_Amount", Mathf.Max(0f, amount));
            cs.SetFloat("_DenNoiseAmt", Mathf.Clamp01(edgeNoise));
            cs.SetFloat("_DenNoiseScale", Mathf.Max(0.01f, noiseScale));
            cs.SetFloat("_DenNoiseContrast", Mathf.Max(0.1f, noiseContrast));
            cs.SetVector("_DenNoiseOffset", noiseOffset + noiseFlow * FieldTime);
            cs.SetFloat("_DenNoiseSeed", noiseSeed);
        }
    }
}
