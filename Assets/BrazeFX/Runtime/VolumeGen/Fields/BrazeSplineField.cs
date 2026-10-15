using UnityEngine;

namespace BrazeFX
{
    /// <summary>Shapes the volume along a <see cref="BrazeSpline"/> path — carve a winding tunnel (Remove) or deposit
    /// a rope of density (Add) inside a tube around the curve. The spline is a separate, reusable object (assign it
    /// here, or put this component on the same GameObject as the spline).</summary>
    [AddComponentMenu("BrazeFX/Volume/Volume Deformers/Spline (Add or Remove)")]
    [ExecuteAlways]
    [RequireComponent(typeof(BrazeSpline))]
    public sealed class BrazeSplineField : BrazeVolumeField
    {
        public enum SOp { Add = 0, Remove = 1 }

        [Tooltip("The path to follow. Leave empty to use a BrazeSpline on this same GameObject.")]
        public BrazeSpline spline;
        public SOp op = SOp.Remove;
        [Tooltip("Tube radius around the path, in world units.")]
        [Min(0.01f)] [BrazeAnimatable("SPLINE/Radius", 0.01f, 5f)] public float radius = 1.5f;
        [Tooltip("Soft edge of the tube, as a fraction of the radius.")]
        [Range(0f, 1f)] [BrazeAnimatable("SPLINE/Edge Softness", 0f, 1f)] public float edgeSoftness = 0.4f;
        [Tooltip("Strength. Remove: 1 = fully carve to zero in the tube core; push past 1 for harder/wider cuts. " +
                 "Add: density deposited.")]
        [Range(0f, 4f)] [BrazeAnimatable("SPLINE/Amount", 0f, 4f)] public float amount = 1f;
        [Tooltip("Tube thickness along the path (radius multiplier vs length 0→1). Flat = uniform rope; ramp for taper.")]
        public AnimationCurve radiusProfile = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("Fade the tube to nothing over this fraction of the path at both ends. 0 = rounded hard caps. " +
                 "(Ignored on a closed spline.)")]
        [Range(0f, 0.5f)] [BrazeAnimatable("SPLINE/End Caps", 0f, 0.5f)] public float endCaps = 0f;
        [Tooltip("Break up the tube edge with fractal noise — bites/wobbles along the path instead of a clean rope/pipe.")]
        [Range(0f, 1f)] [BrazeAnimatable("SPLINE/Edge Noise", 0f, 1f)] public float edgeNoise = 0f;
        [Tooltip("Feature size of the edge noise, in world units.")]
        [Min(0.01f)] [BrazeAnimatable("SPLINE/Noise Size", 0.01f, 5f)] public float noiseSize = 1.5f;
        [Tooltip("Scroll the edge noise along the path over time (units/sec) — motion. 0 = static.")]
        [BrazeAnimatable("SPLINE/Flow Speed", -4f, 4f)] public float flowSpeed = 0f;
        [Tooltip("Cut Through: extrude the tube into a full-depth WALL along the spline's up-axis. 0 = a round tube at " +
                 "the path's height (shallow dent); 1 = a curtain that trenches straight through the volume.")]
        [Range(0f, 1f)] [BrazeAnimatable("SPLINE/Wall", 0f, 1f)] public float wall = 0f;

        public override string KernelName => "FilterSplineDensity";
        public override string DisplayName => $"Spline ({op})";
        protected override Color GizmoColor =>
            op == SOp.Remove ? new Color(1f, 0.4f, 0.3f) : new Color(0.4f, 1f, 0.6f);

        // A spline IS the region, so this field needs no falloff shape.
        protected override void Reset() { base.Reset(); shape = FieldShape.Infinite; }

        BrazeSpline Spline => spline != null ? spline : GetComponent<BrazeSpline>();

        public override int ContentHash() { var s = Spline; return s != null ? SplineHash(s) : 0; }
        public override bool Animating => flowSpeed != 0f && edgeNoise > 0f;

        public override void SetParams(ComputeShader cs, int k)
        {
            var s = Spline;
            cs.SetInt("_SplineOp", (int)op);
            cs.SetFloat("_SplineAmount", Mathf.Max(0f, amount));
            cs.SetFloat("_SplineRadius", Mathf.Max(0.01f, radius));
            cs.SetFloat("_SplineSoft", Mathf.Max(1e-3f, radius * edgeSoftness));
            cs.SetFloat("_SplineEndCap", (s != null && s.closed) ? 0f : endCaps);
            cs.SetFloat("_SplineNoiseAmt", Mathf.Clamp01(edgeNoise));
            cs.SetFloat("_SplineNoiseScale", 1f / Mathf.Max(noiseSize, 1e-3f));
            cs.SetFloat("_SplineFlow", flowSpeed * FieldTime);
            cs.SetFloat("_SplineFlatten", Mathf.Clamp01(wall));
            cs.SetVector("_SplineFlattenAxis", SplineUpAxis(s));
            BindSplinePoints(cs, k, s);
            BindSplineProfile(cs, k, radiusProfile);
        }
    }
}
