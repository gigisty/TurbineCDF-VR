using UnityEngine;

namespace BrazeFX
{
    /// <summary>Field that blurs the density inside its falloff — melt hard edges, soften carve seams.</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Smooth")]
    [ExecuteAlways]
    public sealed class BrazeSmoothField : BrazeVolumeField
    {
        [Range(0f, 1f)] [BrazeAnimatable("SMOOTH/Amount", 0f, 1f)] public float amount = 0.6f;
        [Range(1, 3)] public int radius = 1;

        public override string KernelName => "FilterSmooth";
        public override string DisplayName => "Smooth";
        protected override Color GizmoColor => new Color(0.7f, 0.9f, 0.9f);

        public override void SetParams(ComputeShader cs, int k)
        {
            cs.SetFloat("_SmoothAmount", Mathf.Clamp01(amount));
            cs.SetInt("_SmoothRadius", Mathf.Clamp(radius, 1, 3));
        }
    }
}
