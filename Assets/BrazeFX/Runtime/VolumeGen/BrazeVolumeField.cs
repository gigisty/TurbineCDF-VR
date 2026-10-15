using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Base class for a volume FIELD — a transform-driven falloff region (like a force field). A field is its OWN
    /// GameObject: move/rotate/scale its transform to place and size the falloff; the operation (noise, add/remove,
    /// deform, …) applies inside that falloff. A field affects ONLY the volumes that explicitly list it (drag it into
    /// a volume's FIELDS list) — it does nothing on its own. Listed fields bake top-to-bottom in that list's order.
    /// </summary>
    [ExecuteAlways]
    public abstract class BrazeVolumeField : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum FieldShape { Sphere = 0, Box = 1, Capsule = 2, Cylinder = 3, Infinite = 4, Spline = 5 }

        [Tooltip("Falloff shape. The transform's position/rotation/scale place and size it. Infinite = affect the " +
                 "whole volume (for global ops like Deform or Opacity). Spline = a tube around a BrazeSpline path.")]
        public FieldShape shape = FieldShape.Sphere;
        [Tooltip("Soft falloff band at the field surface (field-local units). 0 = hard edge.")]
        [Range(0.001f, 1f)] [BrazeAnimatable("FALLOFF/Edge", 0.001f, 1f)] public float falloff = 0.3f;
        [Tooltip("Affect OUTSIDE the field instead of inside.")]
        public bool invert = false;

        // ── Spline region (only when shape == Spline) ──────────────────────────────────────────────────────
        // A spline turns ANY field into a path-confined op: the field's operation (noise, density, deform, …) is
        // gated by a tube around this spline instead of an analytic Sphere/Box/etc.
        [Tooltip("Path for a Spline-shaped region — the field's operation applies inside a tube around it.")]
        public BrazeSpline regionSpline;
        [Tooltip("Tube radius around the spline, in world units.")]
        [Min(0.01f)] [BrazeAnimatable("REGION/Radius", 0.01f, 5f)] public float regionRadius = 1.5f;
        [Tooltip("Soft edge of the tube, as a fraction of the radius.")]
        [Range(0f, 1f)] [BrazeAnimatable("REGION/Edge", 0f, 1f)] public float regionEdge = 0.4f;
        [Tooltip("Tube thickness along the path (radius multiplier vs length 0→1). Flat = uniform pipe; ramp for taper.")]
        public AnimationCurve regionRadiusProfile = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("Fade the tube to nothing over this fraction of the path at both ends. 0 = rounded hard caps. " +
                 "(Ignored on a closed spline.)")]
        [Range(0f, 0.5f)] [BrazeAnimatable("REGION/End Caps", 0f, 0.5f)] public float regionEndCaps = 0f;
        [Tooltip("Break up the tube edge with fractal noise — bites and wobbles along the path instead of a clean pipe.")]
        [Range(0f, 1f)] [BrazeAnimatable("REGION/Edge Noise", 0f, 1f)] public float regionEdgeNoise = 0f;
        [Tooltip("Feature size of the edge noise, in world units.")]
        [Min(0.01f)] [BrazeAnimatable("REGION/Noise Size", 0.01f, 5f)] public float regionNoiseSize = 1.5f;
        [Tooltip("Scroll the edge noise along the path over time (units/sec) — motion. 0 = static. Animates in play and " +
                 "in the editor; the volume re-bakes each frame while this is non-zero.")]
        [BrazeAnimatable("REGION/Flow Speed", -4f, 4f)] public float regionFlowSpeed = 0f;
        [Tooltip("Cut Through: extrude the tube into a full-depth WALL along the spline's up-axis. 0 = a round tube at " +
                 "the path's height (reads as a shallow dent); 1 = a curtain that trenches straight through the volume.")]
        [Range(0f, 1f)] [BrazeAnimatable("REGION/Wall", 0f, 1f)] public float regionWall = 0f;

        /// <summary>Bumped whenever a parameter changes so volumes can detect it and re-bake.</summary>
        [System.NonSerialized] public int version;

        public abstract string KernelName { get; }
        public virtual string DisplayName => GetType().Name.Replace("Braze", "").Replace("Field", "");

        /// <summary>Set this field's operation uniforms (host has bound _Src/_Dst/_Res/_WorldSize + falloff + kernel).</summary>
        public abstract void SetParams(ComputeShader cs, int kernel);

        /// <summary>Extra state (beyond this field's own transform/params) that should trigger a re-bake when it
        /// changes — e.g. a referenced spline's points. A Spline-shaped region hashes its spline by default.</summary>
        public virtual int ContentHash() => (shape == FieldShape.Spline && regionSpline != null) ? SplineHash(regionSpline) : 0;

        /// <summary>The spline's up-axis (projection-plane normal) in world space — the direction a Wall cut extrudes
        /// through. Falls back to world up for a Free (planeless) spline.</summary>
        protected static Vector3 SplineUpAxis(BrazeSpline s)
        {
            Vector3 nLocal = s != null ? s.PlaneNormal() : Vector3.up;
            Vector3 w = (s != null && nLocal != Vector3.zero) ? s.transform.TransformDirection(nLocal) : Vector3.up;
            return w.sqrMagnitude > 1e-8f ? w.normalized : Vector3.up;
        }

        /// <summary>Hash a spline's identity + edit-state so a referenced-spline change triggers a re-bake.</summary>
        protected static int SplineHash(BrazeSpline s)
        {
            unchecked
            {
                int h = s.BrazeInstanceId();
                h = h * 31 + s.version;
                h = h * 31 + s.SampleCount;
                h = h * 31 + s.transform.localToWorldMatrix.GetHashCode();
                return h;
            }
        }

        public bool Active => isActiveAndEnabled;

        /// <summary>Wall-clock used to drive motion in the bake (advances in both play and edit mode).</summary>
        protected static float FieldTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        /// <summary>True when this field has time-driven motion — the host then re-bakes the volume every frame.</summary>
        public virtual bool Animating => shape == FieldShape.Spline && regionFlowSpeed != 0f && regionEdgeNoise > 0f;

        protected virtual void OnEnable() { version++; }
        protected virtual void OnValidate() { version++; }

        // Timeline drove an animatable knob via reflection (no OnValidate) — bump version so the owning BrazeVolume's
        // stack signature changes and it re-bakes. Covers every field subclass (Noise / Density / Remap / …).
        public void OnBrazeAnimated() => version++;
        protected virtual void OnDisable() { ReleaseResources(); }

        /// <summary>Release any GPU resources this field owns (e.g. compute buffers). Subclasses should call base.</summary>
        public virtual void ReleaseResources()
        {
            splinePtBuf?.Release(); splinePtBuf = null; splinePtData = null;
            splineProfBuf?.Release(); splineProfBuf = null; splineProfData = null;
            dummyPt?.Release(); dummyPt = null;
            dummyProf?.Release(); dummyProf = null;
        }

        // ── Spline tube buffers (shared by a Spline region AND by BrazeSplineField's own op) ─────────────────
        ComputeBuffer splinePtBuf; Vector4[] splinePtData;
        ComputeBuffer splineProfBuf; float[] splineProfData;
        ComputeBuffer dummyPt, dummyProf;
        const int ProfileSamples = 32;
        readonly List<Vector3> splineScratch = new List<Vector3>();

        /// <summary>Sample a spline to world points and bind them to the kernel. Returns the sample count.</summary>
        protected int BindSplinePoints(ComputeShader cs, int k, BrazeSpline s)
        {
            int count = 0;
            if (s != null) { s.GetWorldSamples(splineScratch); count = splineScratch.Count; }
            int n = Mathf.Max(count, 1);
            if (splinePtBuf == null || splinePtBuf.count != n)
            {
                splinePtBuf?.Release();
                splinePtBuf = new ComputeBuffer(n, sizeof(float) * 4);
                splinePtData = new Vector4[n];
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 p = (i < count) ? splineScratch[i] : Vector3.zero;
                splinePtData[i] = new Vector4(p.x, p.y, p.z, 0f);
            }
            splinePtBuf.SetData(splinePtData, 0, 0, n);
            cs.SetBuffer(k, "_SplinePoints", splinePtBuf);
            cs.SetInt("_SplineCount", count);
            return count;
        }

        /// <summary>Bake an AnimationCurve radius profile (along length 0→1) and bind it to the kernel.</summary>
        protected void BindSplineProfile(ComputeShader cs, int k, AnimationCurve curve)
        {
            if (splineProfBuf == null) { splineProfBuf = new ComputeBuffer(ProfileSamples, sizeof(float)); splineProfData = new float[ProfileSamples]; }
            for (int i = 0; i < ProfileSamples; i++)
            {
                float t = i / (float)(ProfileSamples - 1);
                splineProfData[i] = Mathf.Max(curve != null ? curve.Evaluate(t) : 1f, 0f);
            }
            splineProfBuf.SetData(splineProfData);
            cs.SetBuffer(k, "_SplineProfile", splineProfBuf);
            cs.SetInt("_SplineProfileN", ProfileSamples);
        }

        // Bind 1-element placeholders so kernels that reference the spline buffers stay bound even when this field
        // is NOT a spline (required on Metal — referenced-but-unbound StructuredBuffers are invalid there).
        void BindDummySpline(ComputeShader cs, int k)
        {
            if (dummyPt == null) dummyPt = new ComputeBuffer(1, sizeof(float) * 4);
            if (dummyProf == null) dummyProf = new ComputeBuffer(1, sizeof(float));
            cs.SetBuffer(k, "_SplinePoints", dummyPt);
            cs.SetBuffer(k, "_SplineProfile", dummyProf);
            cs.SetInt("_SplineCount", 0);
            cs.SetInt("_SplineProfileN", 0);
        }

        // Default a freshly-added field to a visible size (a tiny 1-unit region in a large box is easy to miss).
        protected virtual void Reset() { transform.localScale = Vector3.one * 3f; }

        /// <summary>Bind the shared falloff uniforms for this field, mapped into the target volume's box space.</summary>
        public void BindFalloff(ComputeShader cs, int kernel, BrazeVolume vol)
        {
            cs.SetInt("_RegionType", (int)shape);
            cs.SetFloat("_RegionSoft", Mathf.Max(falloff, 1e-3f));
            cs.SetInt("_RegionInvert", invert ? 1 : 0);
            // c (-1..1 box space) → world → field-local unit shape (the falloff region; uses the field scale).
            Matrix4x4 boxToWorld = Matrix4x4.TRS(vol.transform.position, vol.transform.rotation, vol.worldSize)
                                 * Matrix4x4.Scale(Vector3.one * 0.5f);
            cs.SetMatrix("_RegionInv", transform.worldToLocalMatrix * boxToWorld);

            // Deform frame: field ROTATION + POSITION only, normalised by the volume half-extent (NOT the field
            // scale). So a deformer warps the whole volume coherently, its strength is independent of gizmo size
            // (scale only sets the falloff region), and the gizmo's rotation/position orient and pivot it.
            float refSize = Mathf.Max(vol.worldSize.x, Mathf.Max(vol.worldSize.y, vol.worldSize.z)) * 0.5f;
            Matrix4x4 worldToDeform = Matrix4x4.Scale(Vector3.one / Mathf.Max(refSize, 1e-3f))
                                    * Matrix4x4.Rotate(Quaternion.Inverse(transform.rotation))
                                    * Matrix4x4.Translate(-transform.position);
            Matrix4x4 cToDeform = worldToDeform * boxToWorld;
            cs.SetMatrix("_DeformToLocal", cToDeform);
            cs.SetMatrix("_DeformToBox", cToDeform.inverse);

            // Box-normalized → world (for fields that measure world-space distance, e.g. spline tubes).
            cs.SetMatrix("_BoxToWorld", boxToWorld);

            // Spline region: bind the tube buffers; else bind placeholders so the buffers stay bound (Metal).
            if (shape == FieldShape.Spline && regionSpline != null)
            {
                BindSplinePoints(cs, kernel, regionSpline);
                BindSplineProfile(cs, kernel, regionRadiusProfile);
                cs.SetFloat("_SplineRadius", Mathf.Max(0.01f, regionRadius));
                cs.SetFloat("_SplineSoft", Mathf.Max(1e-3f, regionRadius * regionEdge));
                cs.SetFloat("_SplineEndCap", regionSpline.closed ? 0f : regionEndCaps);
                cs.SetFloat("_SplineNoiseAmt", Mathf.Clamp01(regionEdgeNoise));
                cs.SetFloat("_SplineNoiseScale", 1f / Mathf.Max(regionNoiseSize, 1e-3f));
                cs.SetFloat("_SplineFlow", regionFlowSpeed * FieldTime);
                cs.SetFloat("_SplineFlatten", Mathf.Clamp01(regionWall));
                cs.SetVector("_SplineFlattenAxis", SplineUpAxis(regionSpline));
            }
            else BindDummySpline(cs, kernel);
        }

        protected virtual Color GizmoColor => new Color(0.45f, 0.8f, 1f);

        void OnDrawGizmos() => DrawGizmo(0.35f);
        void OnDrawGizmosSelected() => DrawGizmo(0.9f);

        void DrawGizmo(float alpha)
        {
            if (shape == FieldShape.Spline) { DrawSplineGizmo(alpha); return; }
            if (shape == FieldShape.Infinite) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Color c = GizmoColor; c.a = alpha;
            Gizmos.color = c;
            switch (shape)
            {
                case FieldShape.Box:      Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2f); break;
                case FieldShape.Cylinder: Gizmos.DrawWireCube(Vector3.zero, new Vector3(2f, 2f, 2f)); break;
                case FieldShape.Capsule:  Gizmos.DrawWireSphere(Vector3.up, 1f); Gizmos.DrawWireSphere(-Vector3.up, 1f); break;
                default:                  Gizmos.DrawWireSphere(Vector3.zero, 1f); break;
            }
        }

        // Spline region: draw the world path + a few profile-scaled radius rings (no field-local matrix).
        void DrawSplineGizmo(float alpha)
        {
            if (regionSpline == null) return;
            Gizmos.matrix = Matrix4x4.identity;
            Color c = GizmoColor; c.a = alpha; Gizmos.color = c;
            // Cap the sampled line count — a high-control-point spline (e.g. a surface wrap) has SampleCount in the
            // thousands; drawing one line each every frame tanks the editor (same trap as BrazeSpline.OnDrawGizmos).
            int n = regionSpline.GizmoSampleCount;
            if (n >= 2)
            {
                Vector3 prev = regionSpline.EvaluateWorld(0f);
                for (int i = 1; i < n; i++)
                {
                    Vector3 cur = regionSpline.EvaluateWorld(i / (float)(n - 1));
                    Gizmos.DrawLine(prev, cur);
                    prev = cur;
                }
            }
            for (float t = 0f; t <= 1.001f; t += 0.25f)
            {
                float prof = regionRadiusProfile != null ? regionRadiusProfile.Evaluate(t) : 1f;
                Gizmos.DrawWireSphere(regionSpline.EvaluateWorld(t), regionRadius * Mathf.Max(prof, 0f));
            }
        }
    }
}
