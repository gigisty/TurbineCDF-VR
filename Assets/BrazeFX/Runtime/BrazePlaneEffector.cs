using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Applies one authored transform sample to every clone or fragment selected by the shared effector field.
    /// Field evaluation, falloff, signed strength, transform space, and ordered stack composition all remain in
    /// the GPU effector core; this component only selects its constant sample generator.
    /// </summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Plane Effector", 43)]
    public sealed class BrazePlaneEffector : BrazeFractureEffector
    {
        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Plane;

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            // Plane has no per-clone CPU data and no auxiliary buffer. The compute shader emits a constant
            // one-vector, then the shared core applies all enabled transform channels and field weighting.
            if (uniformScale) gpu.scaleAmount = Vector3.one * scaleAmount.x;
        }

        protected override Color GizmoColor => new(1f, 0.72f, 0.18f, 0.8f);

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
            // A constant effector needs signed scale deviation so artists can shrink as well as enlarge clones.
            // The shared GPU core clamps the final scale away from zero.
            Vector3 signedScale = scaleAmount;
            base.OnValidate();
            scaleAmount = uniformScale ? Vector3.one * signedScale.x : signedScale;
        }
    }
}
