using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A standalone, reusable Catmull-Rom spline through a set of control points. It owns no behaviour of its own —
    /// it's a path other systems consume: carve/deform a volume along it (<see cref="BrazeSplineField"/>), drive a
    /// transform along it, scatter objects, etc. Control points are in local space; the transform places/orients it.
    /// </summary>
    [AddComponentMenu("BrazeFX/Spline/Spline")]
    [ExecuteAlways]
    public sealed class BrazeSpline : MonoBehaviour, IBrazeAnimatedTarget
    {
        [Tooltip("Control points in local space. The transform places/orients/scales the whole path.")]
        public List<Vector3> points = new List<Vector3>
        {
            new Vector3(-1f, 0f, 0f), new Vector3(-0.33f, 0f, 0.4f), new Vector3(0.33f, 0f, -0.4f), new Vector3(1f, 0f, 0f)
        };
        [Tooltip("Join the last point back to the first (loop).")]
        public bool closed = false;
        [Tooltip("Curve samples per segment — higher = smoother sampled path (for gizmos and GPU consumers).")]
        [Range(2, 32)] public int samplesPerSegment = 12;

        [Tooltip("Uniform scale of the point spread about the spline origin — size the whole spline up/down without " +
                 "moving points individually. 1 = authored size.")]
        [BrazeAnimatable("SPLINE/Points Distance", 0f, 100f)]
        [Range(0f, 100f)] public float pointsDistance = 1f;

        [Tooltip("Small animated wobble that displaces the whole curve over time — a living, breathing motion " +
                 "applied AFTER the points are placed. 0 = off. (Animates at play time; static in edit.)")]
        [BrazeAnimatable("WOBBLE/Amount", 0f, 2f)]
        [Range(0f, 2f)] public float wobble = 0f;
        [Tooltip("Spatial frequency of the wobble along the curve — higher = more, tighter undulations.")]
        [Min(0.01f)] public float wobbleScale = 2f;
        [Tooltip("How fast the wobble travels/animates (0 = frozen pose).")]
        [Min(0f)] public float wobbleSpeed = 1f;
        [System.NonSerialized] float wobblePhase;

        public enum ProjectionPlane { Free = 0, XY = 1, XZ = 2, YZ = 3 }
        [Tooltip("Authoring plane — point handles drag within this plane and presets are built on it. Free = full 3D.")]
        public ProjectionPlane projection = ProjectionPlane.XZ;

        public enum Interp { Linear = 0, CatmullRom = 1, Bezier = 2 }
        [Tooltip("Linear = straight segments; Catmull-Rom = auto-smooth through points; Bezier = per-point tangent handles.")]
        public Interp interpolation = Interp.CatmullRom;

        // Per-point tangent behaviour (à la Dreamteck's SplinePoint.Type): how a Bezier point's two handles relate
        // when you drag one. Mirrored = opposite + equal length; Free = opposite direction, independent length;
        // Broken = fully independent (a corner).
        public enum PointType { Mirrored = 0, Free = 1, Broken = 2 }

        // Per-knot Bezier tangent handles, as LOCAL offsets from the point (zero = "auto" Catmull-Rom tangent), and
        // the per-point type. All parallel to `points`.
        public List<Vector3> inHandles = new List<Vector3>();
        public List<Vector3> outHandles = new List<Vector3>();
        public List<int> pointTypes = new List<int>();

        public enum SplinePreset { Line, Arc, SCurve, Wave, Circle, Spiral, Helix }
        [HideInInspector] public int activePreset = -1;   // last-applied SplinePreset, or -1 = Custom (edited)

        /// <summary>Bumped on any edit so consumers can detect changes and rebuild.</summary>
        [System.NonSerialized] public int version;

        public int SegmentCount => points == null ? 0 : (closed ? points.Count : Mathf.Max(points.Count - 1, 0));
        public int SampleCount => SegmentCount <= 0 ? Mathf.Min(points?.Count ?? 0, 1) : SegmentCount * samplesPerSegment + 1;

        void OnValidate() { samplesPerSegment = Mathf.Clamp(samplesPerSegment, 2, 32); EnsureHandles(); version++; }

        // Advance the wobble and signal consumers to rebuild while it's animating. Cheap; no-ops when wobble is off.
        void Update()
        {
            if (wobble > 1e-4f && wobbleSpeed > 0f)
            {
                wobblePhase = Time.time * wobbleSpeed;
                version++;
            }
        }

        // Small time-varying displacement layered on the placed curve (local units, independent of pointsDistance
        // so it stays subtle). u is the global curve parameter so the wobble travels coherently along the path.
        Vector3 WobbleOffset(float u)
        {
            float a = u * wobbleScale * (Mathf.PI * 2f);
            float ph = wobblePhase;
            return new Vector3(
                Mathf.Sin(a + ph),
                Mathf.Sin(a * 1.31f + ph * 1.2f + 1.7f),
                Mathf.Cos(a * 0.87f + ph * 0.9f + 3.1f)) * (wobble * 0.1f);
        }

        // Timeline drove an animatable knob (Points Distance) via reflection (no OnValidate) — bump version so
        // consumers that cache the path (volume spline regions, scatterers) rebuild.
        public void OnBrazeAnimated() => version++;

        Vector3 P(int i)
        {
            int n = points.Count;
            if (closed) return points[((i % n) + n) % n];
            return points[Mathf.Clamp(i, 0, n - 1)];
        }

        // Keep the parallel lists sized to the points (called on edit). A zero handle = "auto" (Catmull-Rom tangent).
        public void EnsureHandles()
        {
            if (points == null) return;
            while (inHandles.Count < points.Count) inHandles.Add(Vector3.zero);
            while (outHandles.Count < points.Count) outHandles.Add(Vector3.zero);
            while (pointTypes.Count < points.Count) pointTypes.Add(0);
            if (inHandles.Count > points.Count) inHandles.RemoveRange(points.Count, inHandles.Count - points.Count);
            if (outHandles.Count > points.Count) outHandles.RemoveRange(points.Count, outHandles.Count - points.Count);
            if (pointTypes.Count > points.Count) pointTypes.RemoveRange(points.Count, pointTypes.Count - points.Count);
        }

        public PointType GetPointType(int i) { EnsureHandles(); return (PointType)Mathf.Clamp(pointTypes[i], 0, 2); }
        public void SetPointType(int i, PointType t) { EnsureHandles(); pointTypes[i] = (int)t; version++; }

        /// <summary>Closest point on the curve to a WORLD position; returns the percent and the world point.</summary>
        public float Project(Vector3 worldPos, out Vector3 onCurve, int samples = 0)
        {
            samples = samples > 0 ? samples : Mathf.Max(SampleCount, 8);
            float best = 0f; float bestD = float.MaxValue; onCurve = EvaluateWorld(0f);
            for (int i = 0; i <= samples; i++)
            {
                float u = i / (float)samples;
                Vector3 w = EvaluateWorld(u);
                float d = (w - worldPos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; onCurve = w; }
            }
            return best;
        }

        Vector3 AutoOut(int i) { int n = points.Count; return n < 2 ? Vector3.zero : (P(i + 1) - P(i - 1)) * (1f / 6f); }
        public Vector3 OutHandle(int i) { EnsureHandles(); var h = outHandles[i]; return h.sqrMagnitude < 1e-8f ? AutoOut(i) : h; }
        public Vector3 InHandle(int i)  { EnsureHandles(); var h = inHandles[i];  return h.sqrMagnitude < 1e-8f ? -AutoOut(i) : h; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t
                         + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                         + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float mt = 1f - t;
            return mt * mt * mt * p0 + 3f * mt * mt * t * p1 + 3f * mt * t * t * p2 + t * t * t * p3;
        }

        /// <summary>Point on the curve in LOCAL space at u ∈ [0,1] across the whole path.</summary>
        public Vector3 EvaluateLocal(float u)
        {
            if (points == null || points.Count == 0) return Vector3.zero;
            if (points.Count == 1) return points[0] * pointsDistance;
            int segs = SegmentCount;
            float f = Mathf.Clamp01(u) * segs;
            int i = Mathf.Min((int)f, segs - 1);
            float lt = f - i;

            Vector3 raw;
            if (interpolation == Interp.Linear) raw = Vector3.Lerp(P(i), P(i + 1), lt);
            else if (interpolation == Interp.Bezier)
            {
                Vector3 a = P(i), d = P(i + 1);
                raw = Bezier(a, a + OutHandle(Wrap(i)), d + InHandle(Wrap(i + 1)), d, lt);
            }
            else raw = CatmullRom(P(i - 1), P(i), P(i + 1), P(i + 2), lt);
            Vector3 p = raw * pointsDistance;   // Points Distance: uniform scale about the origin (non-destructive)
            if (wobble > 1e-4f) p += WobbleOffset(Mathf.Clamp01(u));   // post-placement living wobble
            return p;
        }

        int Wrap(int i) { int n = points.Count; return closed ? ((i % n) + n) % n : Mathf.Clamp(i, 0, n - 1); }

        // ── Scaled point helpers (Points Distance) — used by the editor so handles match the scaled curve. ──
        public Vector3 PointWorld(int i) => transform.TransformPoint(points[i] * pointsDistance);
        public Vector3 ScaledLocalToWorld(Vector3 local) => transform.TransformPoint(local * pointsDistance);
        public Vector3 WorldToUnscaledLocal(Vector3 world) => transform.InverseTransformPoint(world) / Mathf.Max(pointsDistance, 1e-4f);

        public Vector3 EvaluateWorld(float u) => transform.TransformPoint(EvaluateLocal(u));

        /// <summary>Unit tangent (direction of travel) in WORLD space at u ∈ [0,1].</summary>
        public Vector3 TangentWorld(float u)
        {
            float e = 1e-3f;
            Vector3 a = EvaluateWorld(Mathf.Clamp01(u - e));
            Vector3 b = EvaluateWorld(Mathf.Clamp01(u + e));
            Vector3 d = b - a;
            return d.sqrMagnitude > 1e-12f ? d.normalized : transform.forward;
        }

        /// <summary>Fill <paramref name="into"/> with <see cref="SampleCount"/> world-space samples along the path.</summary>
        public void GetWorldSamples(List<Vector3> into)
        {
            into.Clear();
            int count = SampleCount;
            if (count <= 1) { if (points != null && points.Count > 0) into.Add(transform.TransformPoint(points[0])); return; }
            for (int i = 0; i < count; i++) into.Add(EvaluateWorld(i / (float)(count - 1)));
        }

        // Map a 2D plane coordinate (u,v) + off-plane w into local space, per the projection plane.
        public Vector3 PlanePoint(float u, float v, float w = 0f) => ToPlane(projection, u, v, w);

        /// <summary>Build a local point from plane coords (u,v in-plane, w off-plane) for a given plane.</summary>
        public static Vector3 ToPlane(ProjectionPlane plane, float u, float v, float w)
        {
            switch (plane)
            {
                case ProjectionPlane.XY: return new Vector3(u, v, w);
                case ProjectionPlane.YZ: return new Vector3(w, u, v);
                default:                 return new Vector3(u, w, v);   // XZ / Free → ground plane, w = up
            }
        }

        /// <summary>Inverse of <see cref="ToPlane"/>: extract (u, v, w) from a local point for a given plane.</summary>
        public static Vector3 FromPlane(ProjectionPlane plane, Vector3 p)
        {
            switch (plane)
            {
                case ProjectionPlane.XY: return new Vector3(p.x, p.y, p.z);
                case ProjectionPlane.YZ: return new Vector3(p.y, p.z, p.x);
                default:                 return new Vector3(p.x, p.z, p.y);   // XZ / Free
            }
        }

        /// <summary>Re-orient a local point/offset from one plane's basis into another (used to rotate the whole spline).</summary>
        public static Vector3 Reproject(Vector3 local, ProjectionPlane from, ProjectionPlane to)
        {
            Vector3 c = FromPlane(from, local);
            return ToPlane(to, c.x, c.y, c.z);
        }

        /// <summary>The local axis perpendicular to the projection plane (the "depth" axis). Free → none.</summary>
        public Vector3 PlaneNormal()
        {
            switch (projection)
            {
                case ProjectionPlane.XY: return Vector3.forward;
                case ProjectionPlane.YZ: return Vector3.right;
                case ProjectionPlane.XZ: return Vector3.up;
                default:                 return Vector3.zero;
            }
        }

        /// <summary>Replace the points with a premade shape (built on the projection plane, ~unit-sized).</summary>
        public void ApplyPreset(SplinePreset preset, int count)
        {
            count = Mathf.Clamp(count, 2, 64);
            var p = new List<Vector3>(count);
            switch (preset)
            {
                case SplinePreset.Line:
                    for (int i = 0; i < count; i++) p.Add(PlanePoint(Mathf.Lerp(-1f, 1f, i / (float)(count - 1)), 0f));
                    closed = false; break;
                case SplinePreset.Arc:
                    for (int i = 0; i < count; i++) { float a = Mathf.Lerp(Mathf.PI, 0f, i / (float)(count - 1)); p.Add(PlanePoint(Mathf.Cos(a), Mathf.Sin(a))); }
                    closed = false; break;
                case SplinePreset.SCurve:
                    for (int i = 0; i < count; i++) { float u = Mathf.Lerp(-1f, 1f, i / (float)(count - 1)); p.Add(PlanePoint(u, Mathf.Sin(u * Mathf.PI) * 0.6f)); }
                    closed = false; break;
                case SplinePreset.Wave:
                    for (int i = 0; i < count; i++) { float u = Mathf.Lerp(-1f, 1f, i / (float)(count - 1)); p.Add(PlanePoint(u, Mathf.Sin(u * Mathf.PI * 2f) * 0.5f)); }
                    closed = false; break;
                case SplinePreset.Circle:
                    for (int i = 0; i < count; i++) { float a = i / (float)count * Mathf.PI * 2f; p.Add(PlanePoint(Mathf.Cos(a), Mathf.Sin(a))); }
                    closed = true; break;
                case SplinePreset.Spiral:
                    for (int i = 0; i < count; i++) { float t = i / (float)(count - 1); float a = t * Mathf.PI * 3f; float r = Mathf.Lerp(0.15f, 1f, t); p.Add(PlanePoint(Mathf.Cos(a) * r, Mathf.Sin(a) * r)); }
                    closed = false; break;
                case SplinePreset.Helix:
                    for (int i = 0; i < count; i++) { float t = i / (float)(count - 1); float a = t * Mathf.PI * 3f; p.Add(PlanePoint(Mathf.Cos(a), Mathf.Sin(a), Mathf.Lerp(-1f, 1f, t))); }
                    closed = false; break;
            }
            points = p;
            inHandles.Clear(); outHandles.Clear(); pointTypes.Clear(); EnsureHandles();   // auto tangents, default type
            activePreset = (int)preset;
            version++;
        }

        /// <summary>Sample budget for visualization (gizmo / editor curve). Capped so a high-control-point spline
        /// (e.g. a surface wrap with hundreds of points → SampleCount in the thousands) doesn't drown the
        /// immediate-mode line draw at ~10k DrawLine/frame. The curve stays visually smooth well under this.</summary>
        public const int GizmoMaxSamples = 480;
        public int GizmoSampleCount => Mathf.Min(SampleCount, GizmoMaxSamples);

        void OnDrawGizmos()
        {
            if (points == null || points.Count < 2) return;
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            int count = GizmoSampleCount;
            Vector3 prev = EvaluateWorld(0f);
            for (int i = 1; i < count; i++)
            {
                Vector3 cur = EvaluateWorld(i / (float)(count - 1));
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
