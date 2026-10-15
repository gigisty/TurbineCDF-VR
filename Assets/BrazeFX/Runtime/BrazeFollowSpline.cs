using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// BRAZE FOLLOW SPLINE — drives this object along a <see cref="BrazeSpline"/> like a train on a rail. The
    /// spline is the track, this component is the carriage: Position / Speed move it, Wrap decides what happens
    /// at the ends, Constant Speed maps travel to arc-length so it rides evenly. Align to Spline turns the object
    /// into the direction of travel — with Look Ahead (aim at a point further down the track for smooth, damped
    /// heading), an up-axis choice, banking into curves, and a rotation offset for meshes not authored facing +Z.
    /// A Look Target overrides the aim (ride the rail, keep framing a subject). Same path engine as
    /// <see cref="BrazeCamera"/>'s spline dolly; every motion knob is timeline-animatable. Runs in edit mode so
    /// the ride can be authored live.
    /// </summary>
    [AddComponentMenu("BrazeFX/Spline/Follow Spline", 38)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BrazeFollowSpline : MonoBehaviour
    {
        // ── Path ────────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("The spline to ride — the rail. Empty = component does nothing.")]
        public BrazeSpline spline;
        [Tooltip("Position along the spline: 0 = start, 1 = end. Animate this for a keyed ride.")]
        [Range(0f, 1f)] [BrazeAnimatable("Path/Position", 0f, 1f)] public float position = 0f;
        [Tooltip("Continuous hands-off travel along the path (added on top of Position). Units per the Speed Unit " +
                 "below. Negative = travel backwards. 0 = Position only.")]
        [BrazeAnimatable("Path/Speed", -10f, 10f)] public float speed = 0.1f;
        public enum SpeedUnit { PathPerSecond = 0, UnitsPerSecond = 1 }
        [Tooltip("Path/s = fractions of the whole path per second (0.1 = full path in 10s, length-independent). " +
                 "Units/s = world units (metres) per second — a real vehicle speed, needs Constant Speed to be exact.")]
        public SpeedUnit speedUnit = SpeedUnit.PathPerSecond;
        public enum PathWrap { Clamp = 0, Loop = 1, PingPong = 2 }
        [Tooltip("What continuous travel does at the ends: Clamp stops, Loop jumps to the start, PingPong reverses.")]
        public PathWrap wrap = PathWrap.Loop;
        [Tooltip("Constant speed: map Position to DISTANCE along the curve (arc-length), so it travels evenly and " +
                 "fully end-to-end. Off = raw spline parameter, which eases at the ends — very visible with a " +
                 "2-point spline (crawls near the ends, rushes the middle).")]
        public bool constantSpeed = true;

        // ── Alignment ───────────────────────────────────────────────────────────────────────────────────
        [Tooltip("Rotate the object to face along the track (C4D 'Tangential'). Off = only the position is driven; " +
                 "the object keeps whatever rotation it has.")]
        public bool alignToSpline = true;
        [Tooltip("Aim at a point this far AHEAD on the track (path fraction) instead of the instantaneous tangent — " +
                 "smooths the heading through sampled corners and telegraphs upcoming turns. 0 = exact tangent.")]
        [Range(0f, 0.25f)] [BrazeAnimatable("Align/Look Ahead", 0f, 0.25f)] public float lookAhead = 0.02f;
        public enum UpMode { WorldUp = 0, SplineUp = 1 }
        [Tooltip("Which way is 'up' for the aligned object: World Up (stays level, like a train), or the spline " +
                 "transform's up axis (tilts with a rotated/banked track object).")]
        public UpMode upMode = UpMode.WorldUp;
        [Tooltip("Face the direction of TRAVEL: flip the heading on a PingPong return leg or when Speed is negative, " +
                 "so the nose always points where it's going. Off = always face the spline's forward direction " +
                 "(a train backing up doesn't turn around).")]
        public bool faceTravelDirection = true;
        [Tooltip("Roll into curves (like a motorbike / roller-coaster). 0 = stay level. Strength is track-dependent " +
                 "— tune by eye; Max Bank caps the lean.")]
        [Range(0f, 5f)] public float banking = 0f;
        [Tooltip("Hard cap on the banking lean, degrees.")]
        [Range(0f, 89f)] public float maxBank = 45f;
        [Tooltip("Ease the roll in/out over this many seconds (independent of Damping) — the jet feel: lean builds " +
                 "as the turn starts and settles back to level after it, instead of snapping with the curvature. " +
                 "0 = roll tracks the curve exactly.")]
        [Range(0f, 2f)] public float bankSmoothing = 0.35f;
        [Tooltip("Extra local rotation applied after alignment (Euler degrees) — for meshes not authored facing +Z " +
                 "(e.g. a model that points down +X needs Y = -90).")]
        public Vector3 rotationOffset;

        // ── Aim override ────────────────────────────────────────────────────────────────────────────────
        [Tooltip("Optional aim target. The object still RIDES the spline, but faces this transform instead of the " +
                 "travel direction — e.g. a turret on a rail tracking a subject. Empty = face along the track.")]
        public Transform lookTarget;
        [Tooltip("World-space offset added to the Look Target (e.g. aim a little above its base).")]
        public Vector3 lookTargetOffset;

        // ── Offset & feel ───────────────────────────────────────────────────────────────────────────────
        [Tooltip("Offset from the curve. When aligned: in the track frame (X = right of travel, Y = up, Z = ahead) — " +
                 "e.g. Y lifts a train onto its wheels. When not aligned: plain world units.")]
        public Vector3 offset;
        [Tooltip("Smoothing time in seconds for position + rotation. 0 = locked hard to the rail. Higher = heavier, " +
                 "laggier follow that soaks up sharp corners.")]
        [Range(0f, 2f)] public float damping = 0f;

        // ── Internal state ──────────────────────────────────────────────────────────────────────────────
        float travel;                  // accumulated continuous travel (path-fractions)
        float uPrev;                   // last resolved u — measures actual on-curve motion
        float travelSign = 1f;         // latched direction of travel (+1/-1); survives standstill
        Vector3 curPos, velPos;        // damped follow state
        Quaternion curRot = Quaternion.identity;
        float curBank, velBank;        // eased banking roll (degrees)
        bool initialized;

        void OnEnable() => initialized = false;   // re-snap to current knobs on enable / domain reload

        void LateUpdate()
        {
            if (spline == null) return;
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);

            // Continuous travel, converted to path-fractions/second (Units/s divides by the track's world length).
            float fracSpeed = speed;
            if (speedUnit == SpeedUnit.UnitsPerSecond)
            {
                float len = TotalWorldLength();
                fracSpeed = len > 1e-4f ? speed / len : 0f;
            }
            travel += fracSpeed * dt;

            float s = WrapS(position + travel);
            float u = constantSpeed ? ArcLengthToU(s) : s;

            // Direction of travel from the ACTUAL on-curve motion this frame — handles keyed Position, PingPong
            // return legs and negative speed alike. A Loop seam jump (|du| ≈ 1) isn't a reversal; the sign latches
            // through standstill so a stopped train keeps facing where it was going.
            float du = u - uPrev;
            if (wrap == PathWrap.Loop && Mathf.Abs(du) > 0.5f) du -= Mathf.Sign(du);
            if (Mathf.Abs(du) > 1e-5f) travelSign = Mathf.Sign(du);
            uPrev = u;
            float aimSign = faceTravelDirection ? travelSign : 1f;

            Vector3 onCurve = spline.EvaluateWorld(u);

            // Heading: aim at a point Look Ahead down the track (in distance space so it's arc-uniform), falling
            // back to the instantaneous tangent when the look-ahead degenerates (clamped at an end).
            Vector3 fwd = Vector3.zero;
            if (lookAhead > 1e-4f)
            {
                float ua = AheadU(s, lookAhead * aimSign);
                fwd = spline.EvaluateWorld(ua) - onCurve;
            }
            if (fwd.sqrMagnitude < 1e-10f) fwd = spline.TangentWorld(u) * aimSign;
            if (fwd.sqrMagnitude < 1e-10f) fwd = transform.forward;

            Vector3 up = upMode == UpMode.SplineUp ? spline.transform.up : Vector3.up;
            if (Vector3.Cross(fwd, up).sqrMagnitude < 1e-10f)                    // vertical track segment —
                up = Mathf.Abs(fwd.normalized.y) > 0.99f ? Vector3.forward : Vector3.up;   // pick a stable up

            Quaternion frame = Quaternion.LookRotation(fwd.normalized, up);

            // Banking: roll into the curve by how hard the heading is turning about the up axis just ahead.
            float bankTarget = 0f;
            if (alignToSpline && banking > 1e-3f && lookTarget == null)
            {
                float ub = AheadU(s, Mathf.Max(lookAhead, 0.02f) * aimSign);
                Vector3 t0 = Vector3.ProjectOnPlane(fwd, up);
                Vector3 t1 = Vector3.ProjectOnPlane(spline.TangentWorld(ub) * aimSign, up);
                if (t0.sqrMagnitude > 1e-8f && t1.sqrMagnitude > 1e-8f)
                {
                    float turn = Vector3.SignedAngle(t0, t1, up);
                    bankTarget = Mathf.Clamp(-turn * banking, -maxBank, maxBank);
                }
            }
            // Jet-like lean: the roll eases toward the curve's demand on its own clock — builds into a turn,
            // settles back to level after — instead of snapping with the instantaneous curvature.
            if (!initialized || bankSmoothing <= 1e-4f || dt <= 0f) { curBank = bankTarget; velBank = 0f; }
            else curBank = Mathf.SmoothDamp(curBank, bankTarget, ref velBank, bankSmoothing, Mathf.Infinity, dt);
            if (Mathf.Abs(curBank) > 1e-3f) frame *= Quaternion.AngleAxis(curBank, Vector3.forward);

            // Offset rides in the track frame when aligned (Y lifts off the rail even through loops), world otherwise.
            Vector3 tgtPos = onCurve + (alignToSpline ? frame * offset : offset);

            bool driveRotation = alignToSpline || lookTarget != null;
            Quaternion tgtRot = frame;
            if (lookTarget != null)
            {
                Vector3 aim = lookTarget.position + lookTargetOffset - tgtPos;
                if (aim.sqrMagnitude > 1e-8f) tgtRot = Quaternion.LookRotation(aim.normalized, up);
            }
            tgtRot *= Quaternion.Euler(rotationOffset);

            // Damped follow toward the rail pose. damping≈0 → locked to the rail.
            if (!initialized || damping <= 1e-4f || dt <= 0f)
            {
                curPos = tgtPos; curRot = tgtRot; velPos = Vector3.zero; initialized = true;
            }
            else
            {
                curPos = Vector3.SmoothDamp(curPos, tgtPos, ref velPos, damping, Mathf.Infinity, dt);
                curRot = Quaternion.Slerp(curRot, tgtRot, 1f - Mathf.Exp(-dt / Mathf.Max(damping, 1e-4f)));
            }

            transform.position = curPos;
            if (driveRotation) transform.rotation = curRot;
        }

        // Wrapped 0..1 path fraction (distance space when Constant Speed is on).
        float WrapS(float s)
        {
            switch (wrap)
            {
                case PathWrap.Loop:     return Mathf.Repeat(s, 1f);
                case PathWrap.PingPong: return Mathf.PingPong(s, 1f);
                default:                return Mathf.Clamp01(s);
            }
        }

        // u a signed step AHEAD of s (distance space): Loop wraps across the seam, Clamp/PingPong stop at the ends
        // (reflecting a look-ahead would aim backwards).
        float AheadU(float s, float step)
        {
            float sa = s + step;
            sa = wrap == PathWrap.Loop ? Mathf.Repeat(sa, 1f) : Mathf.Clamp01(sa);
            return constantSpeed ? ArcLengthToU(sa) : sa;
        }

        /// <summary>The resolved 0..1 spline parameter this component is currently at (for gizmos / tools).</summary>
        public float CurrentU()
        {
            if (spline == null) return 0f;
            float s = WrapS(position + travel);
            return constantSpeed ? ArcLengthToU(s) : s;
        }

        // ── Arc-length reparameterization (same scheme as BrazeCamera's spline dolly) ──────────────────
        // Cumulative chord lengths sampled in LOCAL space, rebuilt only when the spline identity or its
        // edit-version changes. Maps a 0..1 distance fraction to the spline u.
        float[] arcU, arcLen;
        BrazeSpline arcSpline;
        int arcVersion = int.MinValue;

        void EnsureArcTable()
        {
            if (spline == null) { arcSpline = null; return; }
            if (arcSpline == spline && arcVersion == spline.version && arcLen != null) return;
            const int N = 256;
            if (arcU == null || arcU.Length != N + 1) { arcU = new float[N + 1]; arcLen = new float[N + 1]; }
            Vector3 prev = spline.EvaluateLocal(0f);
            float acc = 0f;
            for (int i = 0; i <= N; i++)
            {
                float u = i / (float)N;
                Vector3 p = spline.EvaluateLocal(u);
                acc += (p - prev).magnitude;
                prev = p;
                arcU[i] = u; arcLen[i] = acc;
            }
            arcSpline = spline; arcVersion = spline.version;
        }

        float ArcLengthToU(float s01)
        {
            EnsureArcTable();
            if (arcLen == null) return s01;
            float total = arcLen[arcLen.Length - 1];
            if (total < 1e-6f) return s01;                       // degenerate / single point
            float target = Mathf.Clamp01(s01) * total;
            int lo = 0, hi = arcLen.Length - 1;                  // first index whose cumulative length ≥ target
            while (lo < hi) { int mid = (lo + hi) >> 1; if (arcLen[mid] < target) lo = mid + 1; else hi = mid; }
            int i0 = Mathf.Max(0, lo - 1), i1 = lo;
            float seg = arcLen[i1] - arcLen[i0];
            float t = seg > 1e-6f ? (target - arcLen[i0]) / seg : 0f;
            return Mathf.Lerp(arcU[i0], arcU[i1], t);
        }

        // Track length in world units (local arc length × the spline transform's average scale) — the divisor
        // that turns a Units/s speed into path-fractions/s.
        float TotalWorldLength()
        {
            EnsureArcTable();
            if (arcLen == null || arcSpline != spline) return 0f;
            Vector3 ls = spline.transform.lossyScale;
            return arcLen[arcLen.Length - 1] * (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
        }

        // ── Gizmo: carriage marker on the rail + heading arrow + offset strut ───────────────────────────
        void OnDrawGizmosSelected()
        {
            if (spline == null) return;
            float u = CurrentU();
            Vector3 p = spline.EvaluateWorld(u);
            float size = Mathf.Max(0.05f, (spline.EvaluateWorld(0f) - spline.EvaluateWorld(0.5f)).magnitude * 0.03f);

            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.95f);              // accent orange — the carriage
            Gizmos.DrawSphere(p, size);

            Vector3 t = spline.TangentWorld(u) * (faceTravelDirection ? travelSign : 1f);
            Gizmos.color = new Color(0.3f, 0.85f, 0.85f, 0.95f);            // teal — heading
            Gizmos.DrawLine(p, p + t * size * 6f);

            if (offset.sqrMagnitude > 1e-8f)                                // strut from rail to the offset object
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
                Gizmos.DrawLine(p, transform.position);
            }

            if (lookTarget != null)                                          // green aim line
            {
                Gizmos.color = new Color(0.35f, 0.9f, 0.4f, 0.9f);
                Gizmos.DrawLine(transform.position, lookTarget.position + lookTargetOffset);
            }
        }
    }
}
