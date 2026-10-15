using UnityEngine;

namespace BrazeFX
{
    /// <summary>Field that warps the baked density — bend it into an arc, twist it, or taper it. Defaults to Infinite
    /// (whole-volume) but can be localized by a falloff shape.</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Deform")]
    [ExecuteAlways]
    public sealed class BrazeDeformField : BrazeVolumeField
    {
        // Order matters — these map to _DeformMode ints in the compute.
        public enum DMode { Twist = 0, Bend = 1, Taper = 2, Stretch = 3, Inflate = 4, RadialBlur = 5, DirectionalBlur = 6 }
        public enum DAxis { X = 0, Y = 1, Z = 2 }

        [Tooltip("Twist / Bend = rotate slices along the axis; Taper = pinch the cross-section; Stretch = squash & " +
                 "stretch along the axis; Inflate = push density outwards; Radial/Directional Blur = smear (motion blur).")]
        public DMode mode = DMode.Twist;
        [Tooltip("The deformer's local axis (the field's transform orients it). Twist/Bend/Taper/Stretch run along it; " +
                 "Directional Blur smears along it.")]
        public DAxis axis = DAxis.Y;
        [Tooltip("Strength. 1 ≈ a full π twist / strong stretch across the volume. Independent of the field size — " +
                 "the field's rotation/position orient and pivot the deformer; its scale only sets the falloff region.")]
        [Range(-3f, 3f)] [BrazeAnimatable("DEFORM/Amount", -3f, 3f)] public float amount = 1f;

        public override string KernelName => "FilterDeform";
        public override string DisplayName => $"Deform ({mode})";

        // Whole-volume by default: the deform warps the entire volume coherently about the field's axis/pivot. Give it
        // a Sphere/Box/etc. shape to localise the deformation to a falloff region.
        protected override void Reset() { base.Reset(); shape = FieldShape.Infinite; }
        protected override Color GizmoColor => new Color(0.8f, 0.6f, 1f);

        public override void SetParams(ComputeShader cs, int k)
        {
            cs.SetInt("_DeformMode", (int)mode);
            cs.SetInt("_DeformAxis", (int)axis);
            cs.SetFloat("_DeformAmount", amount);
        }
    }
}
