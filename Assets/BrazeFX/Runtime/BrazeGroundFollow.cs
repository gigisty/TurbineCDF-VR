using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Keeps this object a fixed height above the ground by raycasting straight down, no matter how its
    /// parent (or its own animation) moves it vertically. Drop it on a Braze Pyro Emitter parented under a
    /// hovering/tumbling object (a helicopter, a dragon) and the fume source stays pinned just above the
    /// terrain while still inheriting the parent's horizontal travel. Optionally tilts the object to match
    /// the ground's slope using the ray's surface normal.
    ///
    /// Only the VERTICAL world position is overridden — X/Z (and therefore the parent's horizontal motion)
    /// pass through untouched. Runs in LateUpdate after the parent has moved, so it always wins.
    /// </summary>
    [AddComponentMenu("BrazeFX/Helpers/Braze Ground Follow", 60)]
    [ExecuteAlways]                 // also pin during the timeline's edit-mode preview
    [DefaultExecutionOrder(10000)]  // after animators / BrazeMotion have placed the parent this frame
    public sealed class BrazeGroundFollow : MonoBehaviour
    {
        public enum Align { None = 0, Upright = 1, Surface = 2 }

        // ── Ground follow ────────────────────────────────────────────────────────────────────────────
        [Tooltip("Height to hold above the ground hit, in world units (measured straight up from the surface).")]
        [BrazeAnimatable("GROUND/Offset", 0f, 20f)] public float offset = 1f;

        [Tooltip("Which layers count as 'ground'. The ray ignores everything else.")]
        public LayerMask groundMask = ~0;

        [Tooltip("Ignore every collider on this object's WHOLE rig — not just its own — so the ray passes straight " +
                 "through the vehicle it's parented under (e.g. the helicopter body/landing gear) and reaches the " +
                 "ground below. Turn off only if the thing you want to land on shares the same root object.")]
        public bool ignoreOwnHierarchy = true;

        [Tooltip("Start the downward ray this far ABOVE the object's current position, so it still finds the " +
                 "ground even if the object dips to or below it. Make it larger than the highest the parent rises.")]
        [Min(0f)] public float castHeight = 50f;

        [Tooltip("How far the ray travels downward from its start point. No ground within this range = no change.")]
        [Min(0f)] public float maxDistance = 500f;

        [Tooltip("Smooths the vertical follow so a bobbing/tumbling parent doesn't make the object jitter. " +
                 "0 = snap exactly to the target each frame; higher = softer, laggier follow (seconds). " +
                 "Edit mode always snaps.")]
        [Range(0f, 1f)] public float positionSmoothing = 0.05f;

        // ── Orientation ──────────────────────────────────────────────────────────────────────────────
        [Tooltip("None = leave rotation alone (inherit the parent's tumble). Upright = force the object's up " +
                 "to world up (cancel the parent's tilt). Surface = tilt the up axis toward the ground's normal.")]
        public Align align = Align.Upright;

        [Tooltip("Surface mode: how strongly to match the ground slope. 0 = stay upright, 1 = fully lie along the normal.")]
        [Range(0f, 1f)] public float alignAmount = 1f;

        [Tooltip("Surface mode: clamp how far the object can tilt from upright, so a steep slope or a wall " +
                 "doesn't flip it over.")]
        [Range(0f, 90f)] public float maxTilt = 45f;

        [Tooltip("Smooths the orientation change (seconds). 0 = snap each frame; higher = softer. Edit mode always snaps.")]
        [Range(0f, 1f)] public float rotationSmoothing = 0.05f;

        // ── Gizmo ────────────────────────────────────────────────────────────────────────────────────
        [Tooltip("Draw the cast ray, the ground hit and the held-offset target in the Scene view.")]
        public bool showGizmo = true;

        float velY;                 // SmoothDamp state for the vertical follow
        bool hasGround;             // last frame found ground (for the gizmo)
        Vector3 lastHit, lastNormal;

        // Buffer for a self-ignoring raycast (avoids GC each frame).
        static readonly RaycastHit[] s_Hits = new RaycastHit[16];

        void LateUpdate()
        {
            bool play = Application.isPlaying;
            Vector3 pos = transform.position;
            Vector3 origin = new Vector3(pos.x, pos.y + castHeight, pos.z);

            if (!TryGround(origin, out RaycastHit hit))
            {
                hasGround = false;
                return;
            }
            hasGround = true; lastHit = hit.point; lastNormal = hit.normal;

            // ── Vertical pin: override only world Y, leave X/Z (parent's horizontal travel) intact ──
            float targetY = hit.point.y + offset;
            float newY = (!play || positionSmoothing <= 0f)
                ? targetY
                : Mathf.SmoothDamp(pos.y, targetY, ref velY, positionSmoothing, Mathf.Infinity, Time.deltaTime);
            transform.position = new Vector3(pos.x, newY, pos.z);

            // ── Orientation ──
            if (align == Align.None) return;

            Vector3 up = align == Align.Surface
                ? ClampTilt(Vector3.Slerp(Vector3.up, hit.normal.normalized, alignAmount), maxTilt)
                : Vector3.up;

            // Preserve the object's current heading (yaw) while re-seating its up axis.
            Vector3 fwd = transform.forward;
            Vector3 right = Vector3.Cross(up, fwd);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(up, Vector3.right);
            fwd = Vector3.Cross(right.normalized, up).normalized;
            Quaternion target = Quaternion.LookRotation(fwd, up);

            transform.rotation = (!play || rotationSmoothing <= 0f)
                ? target
                : Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(rotationSmoothing, 1e-4f)));
        }

        // Cast straight down and return the nearest hit that isn't part of this object (or its whole rig).
        bool TryGround(Vector3 origin, out RaycastHit best)
        {
            best = default;
            Transform self = transform, root = transform.root;
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, s_Hits, maxDistance, groundMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = s_Hits[i];
                if (h.collider == null) continue;
                var ht = h.collider.transform;
                bool isSelf = ignoreOwnHierarchy ? ht.root == root : (ht == self || ht.IsChildOf(self));
                if (isSelf) continue;   // skip the vehicle this emitter rides on, so the ray reaches the ground
                if (h.distance < nearest) { nearest = h.distance; best = h; found = true; }
            }
            return found;
        }

        // Limit how far 'up' leans from world up, so steep ground can't flip the object.
        static Vector3 ClampTilt(Vector3 up, float maxDeg)
        {
            up = up.normalized;
            float ang = Vector3.Angle(Vector3.up, up);
            if (ang <= maxDeg || ang < 1e-3f) return up;
            return Vector3.Slerp(Vector3.up, up, maxDeg / ang).normalized;
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmo) return;
            Vector3 pos = transform.position;
            Vector3 origin = new Vector3(pos.x, pos.y + castHeight, pos.z);

            // The cast ray.
            Gizmos.color = new Color(0.25f, 0.7f, 1f, 0.5f);
            Gizmos.DrawLine(origin, origin + Vector3.down * maxDistance);

            if (!hasGround) return;

            // Ground hit + the held offset target above it.
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.9f);
            Gizmos.DrawWireCube(lastHit, Vector3.one * 0.12f);
            Vector3 target = lastHit + Vector3.up * offset;
            Gizmos.DrawLine(lastHit, target);
            Gizmos.DrawWireSphere(target, 0.12f);

            // Surface normal (what Align ▸ Surface tilts toward).
            if (align == Align.Surface)
            {
                Gizmos.color = new Color(0.4f, 1f, 0.5f, 0.9f);
                Gizmos.DrawLine(lastHit, lastHit + lastNormal.normalized * 0.6f);
            }
        }
    }
}
