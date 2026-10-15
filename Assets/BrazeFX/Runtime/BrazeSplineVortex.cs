using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A spline-lathed force for Braze fire and smoke. The spline is the vortex centreline; the tail, body and eye
    /// radii form a continuously-varying capture volume around it. Its calm conformation guide is evaluated at the
    /// end of the existing sparse global-dynamics pass, so adding a spline vortex does not add another dispatch.
    /// </summary>
    [AddComponentMenu("BrazeFX/Fire/Forces/Spline Vortex", 12)]
    [ExecuteAlways]
    [BrazeTimelineCurated]
    public sealed class BrazeSplineVortex : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum ConformMode
        {
            Volume = 0,   // fill and contain the whole lathed silhouette
            Wall = 1,     // pull fluid toward a shell at the profile radius
        }

        /// <summary>All enabled spline vortices in the scene (registry — no hierarchy constraint).</summary>
        public static readonly List<BrazeSplineVortex> Active = new();

        [Tooltip("Spline used as the centreline. If empty, a Braze Spline on this GameObject is used.")]
        public BrazeSpline spline;
        [Tooltip("GPU centreline samples. 24 is smooth for most tornadoes; raise only for tightly curved splines.")]
        [Range(8, 64)] public int pathSamples = 24;
        [Tooltip("Flip which end is the tail and which is the eye. Positive flow always travels tail → eye.")]
        public bool reversePath;

        [Header("Lathe Profile")]
        [Tooltip("Profile radius at the tail end, in world units.")]
        [Min(0f), BrazeAnimatable("PROFILE/Tail Radius", 0f, 30f)] public float tailRadius = 0.15f;
        [Tooltip("Profile radius through the main funnel body, in world units.")]
        [Min(0f), BrazeAnimatable("PROFILE/Body Radius", 0f, 30f)] public float bodyRadius = 0.8f;
        [Tooltip("Profile radius at the eye end, in world units.")]
        [Min(0f), BrazeAnimatable("PROFILE/Eye Radius", 0f, 30f)] public float eyeRadius = 2.5f;
        [Tooltip("Normalized portion of the path used to blend from the narrow tail into the body.")]
        [Range(0.01f, 1f), BrazeAnimatable("PROFILE/Tail Length", 0.01f, 1f)] public float tailLength = 0.35f;
        [Tooltip("Normalized portion of the path used to blend from the body into the broad eye.")]
        [Range(0.01f, 1f), BrazeAnimatable("PROFILE/Eye Length", 0.01f, 1f)] public float eyeLength = 0.2f;
        [Tooltip("Extra multiplier over the tail/body/eye shape. Use this to add waists, bulges or stepped funnels.")]
        public AnimationCurve radiusProfile = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("Soft capture distance outside the visible profile, in world units. Larger values gather smoke from farther away.")]
        [Min(0.01f), BrazeAnimatable("PROFILE/Capture Margin", 0.01f, 30f)] public float captureMargin = 0.75f;
        [Tooltip("Volume fills the funnel; Wall creates a hollow lathed shell around the profile radius.")]
        public ConformMode conformMode = ConformMode.Volume;
        [Tooltip("Wall mode only: full width of the shell band, in world units.")]
        [Min(0.01f), BrazeAnimatable("PROFILE/Wall Thickness", 0.01f, 30f)] public float wallThickness = 0.4f;

        [Header("Motion")]
        [Tooltip("Signed rotation speed around the spline, in turns per second.")]
        [Range(-10f, 10f), BrazeAnimatable("MOTION/Spin Speed", -10f, 10f)] public float spinSpeed = 0.35f;
        [Tooltip("How tightly smoke/fire is driven toward the target rotational speed.")]
        [Range(0f, 1f), BrazeAnimatable("MOTION/Spin Grip")] public float spinGrip = 0.5f;
        [Tooltip("Requested speed along the spline in world units/sec. Positive travels tail → eye. Tight bends " +
                 "automatically use a safe local speed so the fluid follows the curve instead of overshooting it.")]
        [Range(-30f, 30f), BrazeAnimatable("MOTION/Flow Speed", -30f, 30f)] public float flowSpeed = 1f;
        [Tooltip("Smooth response toward Flow Speed. This is a monotonic response—not a force balance—so increasing " +
                 "it cannot overshoot the requested speed.")]
        [Range(0f, 1f), BrazeAnimatable("MOTION/Flow Grip")] public float flowGrip = 0.45f;
        [Tooltip("Primary shape authority. Calms inherited radial/curl motion throughout the captured volume and " +
                 "guides smoke/fire back to the lathed shape. Detail remains secondary.")]
        [Range(0f, 1f), BrazeAnimatable("MOTION/Conform")] public float conform = 0.85f;
        [Tooltip("Final one-way boundary rail. Damps outward escape near the profile edge without damping the " +
                 "authored axial flow or spin.")]
        [Range(0f, 1f), BrazeAnimatable("MOTION/Containment")] public float containment = 0.9f;

        [Header("Eye & Tail")]
        [Tooltip("Fraction of Flow Speed retained at the end of the eye. 0 makes a calm terminal eye; 1 carries " +
                 "the full body flow through the endpoint. The blend uses Eye Length.")]
        [Range(0f, 1f), BrazeAnimatable("EYE & TAIL/Eye Flow")] public float eyeFlow = 0.2f;
        [Tooltip("Opens the core near the eye as a fraction of the local eye radius.")]
        [Range(0f, 1f), BrazeAnimatable("EYE & TAIL/Eye Opening")] public float eyeOpening = 0.35f;
        [Tooltip("Maximum outward target speed through the eye opening, in world units/sec. This is a smooth " +
                 "speed ceiling, not an accumulating push; 0 keeps the eye radially calm.")]
        [Min(0f), BrazeAnimatable("EYE & TAIL/Eye Outflow", 0f, 30f)] public float eyeOutflow = 0.75f;
        [Tooltip("Additional inward draw at the tail, in world units/sec.")]
        [Min(0f), BrazeAnimatable("EYE & TAIL/Tail Suction", 0f, 30f)] public float tailSuction = 0.4f;

        [Header("Detail")]
        [Tooltip("Rotating spiral lobes that break up an axisymmetric funnel. 0 keeps the vortex smooth.")]
        [Range(0, 8)] public int spiralArms = 3;
        [Tooltip("Strength of the rotating spiral lobes.")]
        [Range(0f, 1f), BrazeAnimatable("DETAIL/Arm Strength")] public float armStrength = 0.08f;
        [Tooltip("Signed number of turns the arm pattern twists from tail to eye.")]
        [Range(-8f, 8f), BrazeAnimatable("DETAIL/Arm Twist", -8f, 8f)] public float armTwist = 1.5f;
        [Tooltip("Curl turbulence layered into the captured funnel.")]
        [Range(0f, 1f), BrazeAnimatable("DETAIL/Turbulence")] public float turbulence = 0.03f;
        [Tooltip("World-space size of turbulence features. Larger values make broader motion.")]
        [Min(0.01f), BrazeAnimatable("DETAIL/Turbulence Scale", 0.01f, 30f)] public float turbulenceScale = 1f;

        [Tooltip("Show the spline-lathed profile in the Scene view.")]
        public bool showGizmo = true;

        [System.NonSerialized] BrazeSpline localSpline;

        /// <summary>The assigned spline, or a spline colocated with this component.</summary>
        public BrazeSpline ResolvedSpline
        {
            get
            {
                if (spline != null) return spline;
                if (localSpline == null) localSpline = GetComponent<BrazeSpline>();
                return localSpline;
            }
        }

        internal int ResolvedSampleCount
        {
            get
            {
                BrazeSpline s = ResolvedSpline;
                return s != null && s.SegmentCount > 0 ? Mathf.Clamp(pathSamples, 8, 64) : 0;
            }
        }

        void Reset()
        {
            spline = GetComponent<BrazeSpline>();
        }

        void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
        }

        void OnDisable() => Active.Remove(this);

        void OnValidate()
        {
            pathSamples = Mathf.Clamp(pathSamples, 8, 64);
            tailRadius = Mathf.Max(0f, tailRadius);
            bodyRadius = Mathf.Max(0f, bodyRadius);
            eyeRadius = Mathf.Max(0f, eyeRadius);
            tailLength = Mathf.Clamp(tailLength, 0.01f, 1f);
            eyeLength = Mathf.Clamp(eyeLength, 0.01f, 1f);
            captureMargin = Mathf.Max(0.01f, captureMargin);
            wallThickness = Mathf.Max(0.01f, wallThickness);
            spiralArms = Mathf.Clamp(spiralArms, 0, 8);
            eyeFlow = Mathf.Clamp01(eyeFlow);
            eyeOutflow = Mathf.Max(0f, eyeOutflow);
            tailSuction = Mathf.Max(0f, tailSuction);
            turbulenceScale = Mathf.Max(0.01f, turbulenceScale);
        }

        public void OnBrazeAnimated() => OnValidate();

        /// <summary>World-space profile radius at normalized tail-to-eye position.</summary>
        internal float EvaluateProfileRadius(float tailToEyeT)
        {
            float t = Mathf.Clamp01(tailToEyeT);
            float tailW = 1f - Mathf.SmoothStep(0f, 1f, t / Mathf.Max(tailLength, 0.01f));
            float eyeW = Mathf.SmoothStep(0f, 1f,
                (t - (1f - Mathf.Max(eyeLength, 0.01f))) / Mathf.Max(eyeLength, 0.01f));
            float bodyW = Mathf.Max(0f, 1f - tailW - eyeW);
            float weight = Mathf.Max(tailW + bodyW + eyeW, 1e-5f);
            float radius = (Mathf.Max(tailRadius, 0f) * tailW
                          + Mathf.Max(bodyRadius, 0f) * bodyW
                          + Mathf.Max(eyeRadius, 0f) * eyeW) / weight;
            float custom = radiusProfile != null ? Mathf.Max(0f, radiusProfile.Evaluate(t)) : 1f;
            return radius * custom;
        }

        /// <summary>
        /// Writes world-space centreline samples as xyz + profile radius. Returns the number written and allocates
        /// nothing, allowing containers to reuse one CPU staging array every simulation step.
        /// </summary>
        internal int WriteWorldSamples(Vector4[] target, int offset)
        {
            int count = ResolvedSampleCount;
            BrazeSpline s = ResolvedSpline;
            if (count <= 0 || s == null || target == null || offset < 0 || offset + count > target.Length) return 0;
            float denom = count - 1f;
            for (int i = 0; i < count; i++)
            {
                float u = i / denom;
                float profileT = reversePath ? 1f - u : u;
                Vector3 p = s.EvaluateWorld(u);
                target[offset + i] = new Vector4(p.x, p.y, p.z, EvaluateProfileRadius(profileT));
            }
            return count;
        }

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            DrawPreview(false);
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmo) return;
            DrawPreview(true);
        }

        void DrawPreview(bool selected)
        {
            BrazeSpline s = ResolvedSpline;
            int count = ResolvedSampleCount;
            if (s == null || count < 2) return;

            int previewCount = selected ? Mathf.Min(count, 32) : Mathf.Min(count, 12);
            Vector3 previous = s.EvaluateWorld(0f);
            Vector3 previousNormal = Vector3.zero;
            Color tail = new Color(1f, 0.36f, 0.08f, selected ? 0.9f : 0.35f);
            Color eye = new Color(0.16f, 0.75f, 1f, selected ? 0.9f : 0.35f);

            for (int i = 0; i < previewCount; i++)
            {
                float u = i / (float)(previewCount - 1);
                float profileT = reversePath ? 1f - u : u;
                Vector3 p = s.EvaluateWorld(u);
                Vector3 tangent = s.TangentWorld(u);
                if (reversePath) tangent = -tangent;

                Vector3 normal = previousNormal - tangent * Vector3.Dot(previousNormal, tangent);
                if (normal.sqrMagnitude < 1e-6f)
                {
                    normal = Vector3.Cross(tangent, Vector3.up);
                    if (normal.sqrMagnitude < 1e-6f) normal = Vector3.Cross(tangent, Vector3.right);
                }
                normal.Normalize();
                Vector3 binormal = Vector3.Cross(tangent, normal).normalized;
                previousNormal = normal;

                Gizmos.color = Color.Lerp(tail, eye, profileT);
                if (i > 0) Gizmos.DrawLine(previous, p);
                previous = p;

                bool ring = selected || i == 0 || i == previewCount - 1 || i == previewCount / 2;
                if (!ring) continue;
                DrawRing(p, normal, binormal, EvaluateProfileRadius(profileT), selected ? 16 : 10);
                if (selected)
                {
                    Color outer = Gizmos.color;
                    outer.a *= 0.28f;
                    Gizmos.color = outer;
                    DrawRing(p, normal, binormal, EvaluateProfileRadius(profileT) + captureMargin, 16);
                }
            }
        }

        static void DrawRing(Vector3 center, Vector3 normal, Vector3 binormal, float radius, int segments)
        {
            if (radius <= 1e-5f) return;
            Vector3 previous = center + normal * radius;
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 current = center + (normal * Mathf.Cos(a) + binormal * Mathf.Sin(a)) * radius;
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }
    }
}
