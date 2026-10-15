using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// BRAZE CAMERA — a circle-spline orbit rig for shooting BrazeFX explosions. The rig GameObject sits at the
    /// orbit CENTRE (its position is the look-at pivot); it drives a child / assigned <see cref="Camera"/> around
    /// that pivot via spherical coordinates: <see cref="azimuth"/> (360° horizontal) + <see cref="elevation"/>
    /// (full vertical, over the top with no roll flip) + <see cref="radius"/>. On top of the orbit it layers
    /// auto-spin, a damped (smoothed) follow, FOV / dolly-zoom, and two Perlin noise layers (slow Sway + fast
    /// Shake). Every cinematic knob is <see cref="BrazeAnimatableAttribute"/>-tagged, so the Braze Timeline drives
    /// it with zero extra wiring. Runs in edit mode (<c>[ExecuteAlways]</c>) so the shot can be authored live.
    /// </summary>
    [AddComponentMenu("BrazeFX/Cinematics/Braze Camera", 50)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BrazeCamera : MonoBehaviour
    {
        // ── Rig refs ────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("The Camera this rig drives. Leave empty to auto-use a Camera on this object or a child.")]
        public Camera cam;
        [Tooltip("Optional target the pivot tracks (e.g. the explosion). Empty = pivot is this object's position.")]
        public Transform follow;
        [Tooltip("Offset added to the pivot, in world units.")]
        public Vector3 pivotOffset;

        // ── Orbit (animatable) ──────────────────────────────────────────────────────────────────────────
        [Tooltip("Horizontal angle around the vertical axis, degrees. 0–360 sweeps a full turntable.")]
        [Range(0f, 360f)] [BrazeAnimatable("Orbit/Azimuth", 0f, 360f)] public float azimuth = 35f;
        [Tooltip("Vertical angle, degrees. 0 = eye level, +90 = top-down, -90 = up from below, ±180 = inverted. " +
                 "Sweeps a full vertical orbit over the top with no roll flip.")]
        [Range(-180f, 180f)] [BrazeAnimatable("Orbit/Elevation", -180f, 180f)] public float elevation = 12f;
        [Tooltip("Distance from the pivot (orbit radius), world units.")]
        [Min(0.01f)] [BrazeAnimatable("Orbit/Radius", 0.1f, 50f)] public float radius = 8f;

        // ── Spin (animatable speeds — continuous, on top of the angle) ──────────────────────────────────
        [Tooltip("Continuous horizontal orbit speed in degrees/second (hands-off turntable).")]
        [Range(-180f, 180f)] [BrazeAnimatable("Spin/Azimuth Speed", -180f, 180f)] public float autoSpinAzimuth = 0f;
        [Tooltip("Continuous vertical orbit speed in degrees/second.")]
        [Range(-180f, 180f)] [BrazeAnimatable("Spin/Elevation Speed", -180f, 180f)] public float autoSpinElevation = 0f;

        // ── Path (spline dolly — C4D "Align to Spline") ─────────────────────────────────────────────────
        [Tooltip("Optional spline the rig rides. The orbit CENTRE travels along it; orbit / spin / noise / FOV all " +
                 "layer on top exactly as before. Radius→0 puts the camera on the curve; keep Radius to orbit a " +
                 "point that's gliding along the path. Empty = fixed-pivot orbit (overrides Follow Target).")]
        public BrazeSpline path;
        [Tooltip("Position along the spline: 0 = start, 1 = end. Animate this for a keyed dolly.")]
        [Range(0f, 1f)] [BrazeAnimatable("Path/Position", 0f, 1f)] public float pathPosition = 0f;
        [Tooltip("Continuous hands-off travel along the path, in path-fractions/second (added on top of Position). " +
                 "0 = Position only.")]
        [Range(-2f, 2f)] [BrazeAnimatable("Path/Speed", -2f, 2f)] public float pathSpeed = 0f;
        public enum PathWrap { Clamp = 0, Loop = 1, PingPong = 2 }
        [Tooltip("What continuous travel does at the ends: Clamp stops, Loop jumps to the start, PingPong reverses.")]
        public PathWrap pathWrap = PathWrap.Loop;
        [Tooltip("Align to Tangent (C4D 'Tangential'): rotate the azimuth basis to follow the path's heading, so a " +
                 "fixed orbit angle stays consistent relative to the travel direction as the path curves.")]
        public bool alignToTangent = false;
        [Tooltip("Constant speed: map Position to DISTANCE along the curve (arc-length), so it travels evenly and " +
                 "fully end-to-end. Off = raw spline parameter, which eases at the ends — very visible with a " +
                 "2-point spline (crawls near the ends, rushes the middle).")]
        public bool constantSpeed = true;

        // ── Look At (aim override) ──────────────────────────────────────────────────────────────────────
        [Tooltip("Optional aim target. The rig still POSITIONS the camera via the orbit / path, but AIMS it at this " +
                 "transform instead of the orbit centre — so a camera riding a spline can keep framing a fixed " +
                 "subject. Empty = look at the orbit centre (default). NOTE: this is the AIM; Follow Target is the " +
                 "orbit CENTRE (and a Spline overrides Follow Target).")]
        public Transform lookTarget;
        [Tooltip("World-space offset added to the Look Target (e.g. aim a little above the base).")]
        public Vector3 lookTargetOffset;

        // ── Lens (animatable) ───────────────────────────────────────────────────────────────────────────
        [Tooltip("Camera vertical field of view, degrees.")]
        [Range(5f, 150f)] [BrazeAnimatable("Lens/Field of View", 10f, 120f)] public float fieldOfView = 45f;
        [Tooltip("Dolly-zoom (vertigo): hold the subject's apparent size constant as Radius changes, by " +
                 "compensating the FOV. Captures the framing the moment it's switched on.")]
        public bool dollyZoom = false;

        // ── Noise / shake ───────────────────────────────────────────────────────────────────────────────
        /// <summary>One Perlin noise layer: smooth positional + rotational wobble at a frequency.</summary>
        [System.Serializable]
        public struct NoiseLayer
        {
            [Tooltip("Positional wobble amplitude, world units.")] public float positionAmp;
            [Tooltip("Rotational wobble amplitude, degrees.")]     public float rotationAmp;
            [Tooltip("Wobble speed, Hz (cycles/second).")]         public float frequency;
            [Tooltip("Seed — change for a different motion.")]     public int seed;

            public static NoiseLayer Default(float posAmp, float rotAmp, float freq, int seed) =>
                new NoiseLayer { positionAmp = posAmp, rotationAmp = rotAmp, frequency = freq, seed = seed };
        }

        [Tooltip("Slow, broad drift — a floating 'handheld' sway.")]
        public NoiseLayer sway = NoiseLayer.Default(0.15f, 1.5f, 0.25f, 1);
        [Tooltip("Fast, small jitter — impact shake.")]
        public NoiseLayer shake = NoiseLayer.Default(0.05f, 0.6f, 8f, 7);
        [Tooltip("Master multiplier over BOTH noise layers. Ramp this during the blast. 0 = still.")]
        [Range(0f, 2f)] [BrazeAnimatable("Shake/Intensity", 0f, 2f)] public float shakeIntensity = 1f;

        // ── Feel ────────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("Smoothing time in seconds for orbit / lens / pivot. 0 = snap instantly to the target. " +
                 "Higher = heavier, more cinematic ease toward animated values.")]
        [Range(0f, 2f)] public float damping = 0.12f;

        // ── Internal (smoothed) state ───────────────────────────────────────────────────────────────────
        float curAz, curEl, curRadius, curFov;
        Vector3 curPivot, curLook;
        float velAz, velEl, velRadius, velFov;
        Vector3 velPivot, velLook;
        float spinAz, spinEl;          // accumulated continuous-spin offsets
        float pathTravel;              // accumulated continuous path travel (path-fractions)
        float clock;                   // own time base → consistent in edit + play
        bool initialized;
        // dolly-zoom reference captured when the toggle flips on
        bool dollyArmed;
        float dollyRadius0, dollyTanHalf0;

        // The orbit centre / look-at pivot. A spline (if assigned) overrides Follow Target / transform as the
        // source, so the whole rig glides along the path while orbit / spin / noise keep layering on top.
        public Vector3 Pivot
        {
            get
            {
                Vector3 center = path != null ? path.EvaluateWorld(PathU())
                               : follow      ? follow.position
                               :               transform.position;
                return center + pivotOffset;
            }
        }

        // Resolved 0..1 spline parameter. Position + accumulated travel is wrapped in DISTANCE space, then (when
        // Constant Speed is on) remapped through the arc-length table so even Position → even travel. Without the
        // remap, a Catmull-Rom path eases at the segment ends — glaring on a 2-point spline (one eased segment).
        public float PathU()
        {
            float s = pathPosition + pathTravel;
            switch (pathWrap)
            {
                case PathWrap.Loop:     s = Mathf.Repeat(s, 1f); break;
                case PathWrap.PingPong: s = Mathf.PingPong(s, 1f); break;
                default:                s = Mathf.Clamp01(s); break;
            }
            return constantSpeed ? ArcLengthToU(s) : s;
        }

        // ── Arc-length reparameterization ───────────────────────────────────────────────────────────────
        // Cumulative chord lengths sampled in LOCAL space (transform-invariant under uniform scale), rebuilt only
        // when the spline identity or its edit-version changes. Maps a 0..1 distance fraction to the spline u.
        float[] arcU, arcLen;
        BrazeSpline arcSpline;
        int arcVersion = int.MinValue;

        void EnsureArcTable()
        {
            if (path == null) { arcSpline = null; return; }
            if (arcSpline == path && arcVersion == path.version && arcLen != null) return;
            const int N = 256;
            if (arcU == null || arcU.Length != N + 1) { arcU = new float[N + 1]; arcLen = new float[N + 1]; }
            Vector3 prev = path.EvaluateLocal(0f);
            float acc = 0f;
            for (int i = 0; i <= N; i++)
            {
                float u = i / (float)N;
                Vector3 p = path.EvaluateLocal(u);
                acc += (p - prev).magnitude;
                prev = p;
                arcU[i] = u; arcLen[i] = acc;
            }
            arcSpline = path; arcVersion = path.version;
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

        // Azimuth offset (deg) that aligns the orbit basis to the path's horizontal heading. 0 when not on a path
        // or Align to Tangent is off. Keeps the pole-stable up vector — heading follow only, no banking.
        public float TangentYaw()
        {
            if (path == null || !alignToTangent) return 0f;
            Vector3 t = path.TangentWorld(PathU());
            return t.sqrMagnitude > 1e-8f ? Mathf.Atan2(t.x, t.z) * Mathf.Rad2Deg : 0f;
        }

        void OnEnable()
        {
            if (cam == null) cam = GetComponentInChildren<Camera>();
            initialized = false;       // re-snap state to current knobs on enable / domain reload
        }

        void LateUpdate()
        {
            if (cam == null) { cam = GetComponentInChildren<Camera>(); if (cam == null) return; }

            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);
            clock += dt;

            // Continuous spin accumulates over our own clock (frame-rate independent, edit/play consistent).
            spinAz += autoSpinAzimuth * dt;
            spinEl += autoSpinElevation * dt;
            pathTravel += pathSpeed * dt;   // continuous path dolly, same clock

            Vector3 pivot = Pivot;
            Vector3 lookTgt = lookTarget ? lookTarget.position + lookTargetOffset : pivot;
            float tgtAz = azimuth + spinAz + TangentYaw();
            float tgtEl = elevation + spinEl;
            float tgtRadius = Mathf.Max(0.01f, radius);
            float tgtFov = ResolveFov(tgtRadius);

            if (!initialized)
            {
                curAz = tgtAz; curEl = tgtEl; curRadius = tgtRadius; curFov = tgtFov; curPivot = pivot; curLook = lookTgt;
                velAz = velEl = velRadius = velFov = 0f; velPivot = velLook = Vector3.zero;
                initialized = true;
            }

            // Critically-damped follow toward the (possibly animated) targets. damping≈0 → snap.
            if (damping > 1e-4f && dt > 0f)
            {
                curAz     = Mathf.SmoothDampAngle(curAz, tgtAz, ref velAz, damping, Mathf.Infinity, dt);
                curEl     = Mathf.SmoothDampAngle(curEl, tgtEl, ref velEl, damping, Mathf.Infinity, dt);
                curRadius = Mathf.SmoothDamp(curRadius, tgtRadius, ref velRadius, damping, Mathf.Infinity, dt);
                curFov    = Mathf.SmoothDamp(curFov, tgtFov, ref velFov, damping, Mathf.Infinity, dt);
                curPivot  = Vector3.SmoothDamp(curPivot, pivot, ref velPivot, damping, Mathf.Infinity, dt);
                curLook   = Vector3.SmoothDamp(curLook, lookTgt, ref velLook, damping, Mathf.Infinity, dt);
            }
            else
            {
                curAz = tgtAz; curEl = tgtEl; curRadius = tgtRadius; curFov = tgtFov; curPivot = pivot; curLook = lookTgt;
            }

            OrbitFrame(curAz, curEl, curRadius, curPivot, out Vector3 pos, out Quaternion rot);

            // Look At: re-aim at the (smoothed) look target, keeping the orbit's pole-stable up. Position is still the
            // orbit/path solve above — only the AIM changes — so a spline-riding camera can hold a fixed subject.
            if (lookTarget != null)
            {
                Vector3 up = rot * Vector3.up;
                Vector3 fwd = curLook - pos;
                if (fwd.sqrMagnitude > 1e-8f) rot = Quaternion.LookRotation(fwd, up);
            }

            // Layered noise, scaled by the master intensity. Position is in camera-local space; rotation is a
            // local euler offset — both ride on top of the solved orbit pose so they never disturb the framing.
            float gain = Mathf.Max(0f, shakeIntensity);
            if (gain > 1e-4f)
            {
                Vector3 nPos = (NoisePos(sway, 100) + NoisePos(shake, 400)) * gain;
                Vector3 nRot = (NoiseRot(sway, 700) + NoiseRot(shake, 950)) * gain;
                pos += rot * nPos;
                rot *= Quaternion.Euler(nRot);
            }

            cam.transform.SetPositionAndRotation(pos, rot);
            cam.fieldOfView = Mathf.Clamp(curFov, 1f, 179f);
        }

        // FOV target, including the optional dolly-zoom compensation (hold subject size as radius changes).
        float ResolveFov(float r)
        {
            if (!dollyZoom) { dollyArmed = false; return fieldOfView; }
            if (!dollyArmed)
            {
                dollyArmed = true;
                dollyRadius0 = r;
                dollyTanHalf0 = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
            float tanHalf = dollyTanHalf0 * dollyRadius0 / Mathf.Max(r, 1e-3f);
            return 2f * Mathf.Atan(tanHalf) * Mathf.Rad2Deg;
        }

        // Spherical orbit pose around the pivot, with a stable up vector (the elevation tangent) so the camera
        // rolls smoothly OVER the poles instead of snapping when world-up would flip. Public so the editor's
        // scene handles can place the camera marker on the exact same orbit the runtime solves.
        public static void OrbitFrame(float azDeg, float elDeg, float r, Vector3 pivot, out Vector3 pos, out Quaternion rot)
        {
            float az = azDeg * Mathf.Deg2Rad, el = elDeg * Mathf.Deg2Rad;
            float ca = Mathf.Cos(az), sa = Mathf.Sin(az), ce = Mathf.Cos(el), se = Mathf.Sin(el);
            Vector3 dir = new Vector3(ce * sa, se, ce * ca);                 // pivot → camera direction
            Vector3 up  = new Vector3(-se * sa, ce, -se * ca);              // d(dir)/d(elevation): continuous through poles
            pos = pivot + dir * r;
            Vector3 fwd = pivot - pos;
            rot = (fwd.sqrMagnitude > 1e-8f) ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
        }

        // Smooth signed (-1..1) Perlin sample per axis; seed + axis-offset decorrelate the three channels.
        Vector3 NoiseAxes(in NoiseLayer n, int baseOff)
        {
            float t = clock * Mathf.Max(0f, n.frequency);
            float s = n.seed * 13.13f;
            return new Vector3(
                Mathf.PerlinNoise(t + s + baseOff + 0.0f, 0.37f) * 2f - 1f,
                Mathf.PerlinNoise(t + s + baseOff + 31.7f, 0.61f) * 2f - 1f,
                Mathf.PerlinNoise(t + s + baseOff + 67.3f, 0.91f) * 2f - 1f);
        }

        Vector3 NoisePos(in NoiseLayer n, int baseOff) => NoiseAxes(n, baseOff) * n.positionAmp;
        Vector3 NoiseRot(in NoiseLayer n, int baseOff) => NoiseAxes(n, baseOff + 5) * n.rotationAmp;

        // ── Gizmos: orbit ring + elevation arc + camera marker + frustum (faint always, bright on select) ──
        void OnDrawGizmos()        => DrawRig(0.3f);
        void OnDrawGizmosSelected() => DrawRig(0.95f);

        void DrawRig(float a)
        {
            Vector3 pivot = Pivot;
            float r = Mathf.Max(0.05f, radius);
            float az = azimuth * Mathf.Deg2Rad, el = elevation * Mathf.Deg2Rad;

            // Horizontal orbit ring (azimuth path) in the world XZ plane through the pivot.
            Gizmos.color = new Color(1f, 0.45f, 0.14f, a);                  // accent orange
            DrawCircle(pivot, Vector3.right * r, Vector3.forward * r, 64);

            // Elevation arc — the great-circle slice at the current azimuth (over-the-top path).
            Vector3 radial = new Vector3(Mathf.Sin(az), 0f, Mathf.Cos(az));
            Gizmos.color = new Color(0.3f, 0.85f, 0.85f, a);               // teal
            DrawCircle(pivot, radial * r, Vector3.up * r, 64);

            // Camera marker + spoke from the pivot (tangent-aligned azimuth if riding a path).
            OrbitFrame(azimuth + TangentYaw(), elevation, r, pivot, out Vector3 pos, out Quaternion rot);
            Gizmos.color = new Color(1f, 0.45f, 0.14f, a);
            Gizmos.DrawLine(pivot, pos);
            Gizmos.DrawSphere(pos, r * 0.045f);
            Gizmos.color = new Color(0.85f, 0.2f, 0.45f, a);               // pink pivot dot
            Gizmos.DrawSphere(pivot, r * 0.03f);

            // Look Target — green aim line from the camera to the framed subject.
            if (lookTarget != null)
            {
                Vector3 look = lookTarget.position + lookTargetOffset;
                Gizmos.color = new Color(0.35f, 0.9f, 0.4f, a);
                Gizmos.DrawLine(pos, look);
                Gizmos.DrawSphere(look, r * 0.03f);
            }

            // Tiny frustum preview at the camera.
            Gizmos.color = new Color(1f, 1f, 1f, a * 0.5f);
            DrawFrustum(pos, rot, r);
        }

        static void DrawCircle(Vector3 c, Vector3 axisU, Vector3 axisV, int seg)
        {
            Vector3 prev = c + axisU;
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = c + axisU * Mathf.Cos(t) + axisV * Mathf.Sin(t);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }

        void DrawFrustum(Vector3 pos, Quaternion rot, float r)
        {
            float near = r * 0.12f;
            float fov = (cam != null ? cam.fieldOfView : fieldOfView);
            float aspect = (cam != null ? cam.aspect : 1.6f);
            float h = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * near;
            float w = h * aspect;
            Vector3 f = rot * Vector3.forward, u = rot * Vector3.up, rg = rot * Vector3.right;
            Vector3 ctr = pos + f * near;
            Vector3 tl = ctr + u * h - rg * w, tr = ctr + u * h + rg * w;
            Vector3 bl = ctr - u * h - rg * w, br = ctr - u * h + rg * w;
            Gizmos.DrawLine(pos, tl); Gizmos.DrawLine(pos, tr);
            Gizmos.DrawLine(pos, bl); Gizmos.DrawLine(pos, br);
            Gizmos.DrawLine(tl, tr); Gizmos.DrawLine(tr, br);
            Gizmos.DrawLine(br, bl); Gizmos.DrawLine(bl, tl);
        }
    }
}
