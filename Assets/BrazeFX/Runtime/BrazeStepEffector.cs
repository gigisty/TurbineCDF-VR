using UnityEngine;

namespace BrazeFX
{
    /// <summary>Progressively applies the shared transform channels across fragment index or object-space position.</summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Step Effector", 45)]
    public sealed class BrazeStepEffector : BrazeFractureEffector
    {
        public enum Ordering { Index, ObjectX, ObjectY, ObjectZ, Radial }

        [Header("STEP")]
        public Ordering ordering = Ordering.ObjectY;
        [Tooltip("Select the portion of the normalized ordering that maps across the Curve.")]
        public Vector2 inputRange = new(0f, 1f);
        [Tooltip("Remaps the ordered 0..1 value before it drives the shared transform channels.")]
        public AnimationCurve stepCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [BrazeAnimatable("STEP/Offset", -2f, 2f)] public float offset = 0f;
        [Tooltip("Wrap Offset instead of clamping at the ends.")]
        public bool loop = false;
        public bool reverse = false;
        [Tooltip("0 or 1 = continuous. Higher values turn the curve into discrete bands.")]
        [Range(0, 64)] public int quantize = 0;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Step;
        public override AnimationCurve FunctionCurve => stepCurve;

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "ordering" => "EFFECTOR/DISTRIBUTION/Order",
            "inputRange" => "EFFECTOR/DISTRIBUTION/Input Range",
            "stepCurve" => "EFFECTOR/REMAP/Curve",
            "offset" => "EFFECTOR/REMAP/Offset",
            "loop" => "EFFECTOR/REMAP/Loop",
            "reverse" => "EFFECTOR/REMAP/Reverse",
            "quantize" => "EFFECTOR/REMAP/Bands",
            _ => fallbackPath,
        };

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            gpu.mode = (int)ordering;
            if (uniformScale) gpu.scaleAmount = Vector3.one * scaleAmount.x;
            var flags = (FractureEffectorFlags)gpu.flags;
            if (reverse) flags |= FractureEffectorFlags.StepReverse;
            if (loop) flags |= FractureEffectorFlags.StepLoop;
            gpu.flags = (int)flags;
            gpu.parameters = new Vector4(inputRange.x, inputRange.y, offset, quantize);
        }

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + (int)ordering;
                h = h * 31 + inputRange.GetHashCode();
                h = h * 31 + offset.GetHashCode();
                h = h * 31 + (loop ? 1 : 0);
                h = h * 31 + (reverse ? 1 : 0);
                h = h * 31 + quantize;
                h = h * 31 + CurveHash(stepCurve);
                return h;
            }
        }

        protected override Color GizmoColor => new(0.35f, 0.85f, 1f, 0.8f);

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            affectPosition = true;
            positionAmount = Vector3.up;
            affectRotation = false;
            affectScale = false;
        }

        protected override void OnValidate()
        {
            float signedUniformScale = scaleAmount.x;
            base.OnValidate();
            if (uniformScale)
                scaleAmount = Vector3.one * Mathf.Clamp(signedUniformScale, -2f, 2f);
            quantize = Mathf.Clamp(quantize, 0, 64);
        }
    }
}
