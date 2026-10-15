using UnityEngine;

namespace BrazeFX
{
    /// <summary>Field that remaps density (opacity / levels) inside its falloff — input black/white points, gamma,
    /// contrast, output range, invert. Defaults to Infinite (whole-volume).</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Remap (Opacity)")]
    [ExecuteAlways]
    public sealed class BrazeRemapField : BrazeVolumeField
    {
        [Range(0f, 2f)] [BrazeAnimatable("REMAP/In Low", 0f, 2f)] public float inLow = 0f;
        [Range(0f, 2f)] [BrazeAnimatable("REMAP/In High", 0f, 2f)] public float inHigh = 1f;
        [Range(0.05f, 5f)] [BrazeAnimatable("REMAP/Gamma", 0.05f, 5f)] public float gamma = 1f;
        [Range(0.1f, 4f)] [BrazeAnimatable("REMAP/Contrast", 0.1f, 4f)] public float contrast = 1f;
        [Range(0f, 2f)] [BrazeAnimatable("REMAP/Out Low", 0f, 2f)] public float outLow = 0f;
        [Range(0f, 4f)] [BrazeAnimatable("REMAP/Out High", 0f, 4f)] public float outHigh = 1f;
        public bool invertValues = false;

        public override string KernelName => "FilterRemap";
        public override string DisplayName => "Remap (Opacity)";
        protected override Color GizmoColor => new Color(0.9f, 0.8f, 0.5f);

        protected override void Reset() { base.Reset(); shape = FieldShape.Infinite; }

        public override void SetParams(ComputeShader cs, int k)
        {
            cs.SetFloat("_RemapInLow", inLow);
            cs.SetFloat("_RemapInHigh", Mathf.Max(inHigh, inLow + 1e-3f));
            cs.SetFloat("_RemapGamma", Mathf.Max(0.05f, gamma));
            cs.SetFloat("_RemapContrast", Mathf.Max(0.1f, contrast));
            cs.SetFloat("_RemapOutLow", outLow);
            cs.SetFloat("_RemapOutHigh", outHigh);
            cs.SetInt("_RemapInvert", invertValues ? 1 : 0);
        }
    }
}
