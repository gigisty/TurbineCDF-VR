using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Allocation-free transform-sweep source for custom GPU/kinematic projectiles. No Unity Rigidbody, Collider,
    /// raycast, or trigger is required. Hitscan weapons may call QueueImpact directly instead.
    /// </summary>
    [AddComponentMenu("BrazeFX/Destruction/Braze Fracture Impact Source", 43)]
    [DisallowMultipleComponent]
    public sealed class BrazeFractureImpactSource : MonoBehaviour
    {
        internal static readonly List<BrazeFractureImpactSource> Active = new();

        public enum ActivationMode { TransformSweep, Manual }

        [Header("ACTIVATION")]
        [Tooltip("Transform Sweep tests the motion from the previous to current position. Manual fires only through code or the inspector button.")]
        public ActivationMode activation = ActivationMode.TransformSweep;
        [Tooltip("Optional explicit receiver. Empty checks the bounds of active Voronoi fractures without using Unity Physics.")]
        [HideInInspector]
        public BrazeVoronoiFracture target;

        [Header("IMPACT")]
        [Min(0.001f)] public float radius = 0.35f;
        [Min(0f)] public float impulse = 12f;
        [Min(0f)] public float spin = 6f;
        [Range(0f, 1f)] public float radialBlend = 0.35f;
        [Min(0f)] public float upwardBias = 0.1f;
        [Min(0f)] [Tooltip("Additional distance projected beyond this frame's swept motion.")]
        public float penetrationDepth = 0.5f;
        [Range(0f, 2f)] public float velocityInheritance = 0.5f;
        [Min(0.01f)] public float falloff = 1f;
        public bool affectReleased = true;

        [Header("CONTAINER DEPOSIT")]
        [Tooltip("Emit smoke/fire from the GPU-simulated fragments selected by this impact.")]
        public bool depositToFluid = false;
        /// <summary>
        /// Optional container override, hidden from the inspector: a projectile prefab cannot serialize a scene
        /// reference, and leaving this empty already lets any live container the fragments enter pick up the
        /// emission. Set it from script only to pin one container when several are listening.
        /// </summary>
        [HideInInspector] public BrazeContainer fluidContainer;
        [Tooltip("Seconds the impacted fragments emit after the hit.")]
        [Range(0.05f, 10f)] public float fluidDuration = 0.75f;
        [Tooltip("Stable percentage of cells in the impact area allowed to deposit. Lower values reduce fluid hash contention and active simulation bricks.")]
        [Range(1f, 100f)] public float fluidCellPercentage = 25f;
        [Tooltip("Soot/smoke emitted per impacted fragment per second.")]
        [Min(0f)] public float fluidSmoke = 0.15f;
        [Tooltip("Heat emitted per impacted fragment per second. Heat above ignition combines with Fuel to make flame.")]
        [Min(0f)] public float fluidHeat = 1.5f;
        [Tooltip("Combustible fuel emitted per impacted fragment per second.")]
        [Min(0f)] public float fluidFuel = 1f;
        [Tooltip("How much of each fragment's live GPU velocity the emitted gas inherits.")]
        [Range(0f, 1f)] public float fluidVelocityBlend = 1f;
        [Tooltip("Buoyant lift carried by the deposited hot gas. Signed: positive rises, negative sinks.")]
        [Range(-300f, 300f)] public float fluidBuoyancy = 12f;
        [Tooltip("Initial ejecta speed away from the struck surface, in world units per second.")]
        [Range(0f, 30f)] public float fluidBurstSpeed = 4f;
        [Tooltip("Which way the plume's axial half fires. -1 blows back along the incoming projectile, +1 drives it " +
                 "onward through the surface, 0 removes the axial push and leaves only the surface spread.")]
        [Range(-1f, 1f)] public float fluidBlastDirection = -1f;
        [Tooltip("Blend from the Blast Direction axis to the local fragment-surface direction.")]
        [Range(0f, 1f)] public float fluidRadialSpread = 0.65f;
        [Tooltip("Random source velocity that breaks the impact plume into organic curls, in world units per second.")]
        [Range(0f, 5f)] public float fluidTurbulence = 1f;
        [Tooltip("Moves emission from fragment centroids toward their struck surface. 1 reaches the fragment radius.")]
        [Range(0f, 1f)] public float fluidSurfaceBias = 0.8f;

        [Tooltip("Stop this source after its first impact command is accepted by a fracture.")]
        public bool triggerOnce = true;

        bool triggered;
        bool hasPreviousPosition;
        Vector3 previousPosition;
        uint acceptedImpactVersion;

        /// <summary>Changes whenever this source successfully submits an impact. Pooling helpers use this without events or allocations.</summary>
        public uint AcceptedImpactVersion => acceptedImpactVersion;

        void OnEnable()
        {
            BrazeRuntimeRegistry.AddUnique(Active, this);
            BrazeFractureImpactDriver.Ensure();
            triggered = false;
            previousPosition = transform.position;
            hasPreviousPosition = true;
        }

        void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        void Start() => BrazeFractureImpactDriver.Ensure();

        internal void ProcessSweep()
        {
            Vector3 currentPosition = transform.position;
            if (!hasPreviousPosition)
            {
                previousPosition = currentPosition;
                hasPreviousPosition = true;
            }

            if (Application.isPlaying && !triggered && activation == ActivationMode.TransformSweep)
                FireSweep(previousPosition, currentPosition);

            previousPosition = currentPosition;
        }

        bool FireSweep(Vector3 start, Vector3 end)
        {
            Vector3 displacement = end - start;
            float distance = displacement.magnitude;
            Vector3 direction = distance > 1e-6f ? displacement / distance : transform.forward;
            Vector3 velocity = Time.deltaTime > 1e-6f ? displacement / Time.deltaTime : Vector3.zero;

            // Detection sweeps ONLY the distance actually travelled this frame; Penetration Depth is how far the
            // damage capsule drives PAST the contact, not extra reach for the trigger. Extending the detection ray
            // by it fired the impact while the projectile was still a full penetrationDepth short of the surface,
            // and — because the contact then landed on the far end of that same ray — collapsed the capsule handed
            // to the GPU to ~zero length, leaving a sphere floating in front of the wall that only caught cells
            // whose bounding radius happened to span the gap. That is the "hits the wall, breaks nothing" bug.
            float depth = Mathf.Max(0f, penetrationDepth);

            if (target != null)
            {
                if (!target.TryGetImpactContact(start, end, radius, out Vector3 contact)) return false;
                return Queue(target, contact, direction, depth, velocity);
            }

            var fractures = BrazeVoronoiFracture.Active;
            bool queuedAny = false;
            for (int i = 0; i < fractures.Count; i++)
            {
                BrazeVoronoiFracture receiver = fractures[i];
                if (receiver == null || !receiver.TryGetImpactContact(start, end, radius, out Vector3 contact)) continue;
                if (!Queue(receiver, contact, direction, depth, velocity)) continue;
                queuedAny = true;
                if (triggerOnce) break;
            }
            return queuedAny;
        }

        bool Queue(BrazeVoronoiFracture receiver, Vector3 position, Vector3 direction,
                   float depth, Vector3 sourceVelocity)
        {
            var impact = BuildImpact(position, direction, sourceVelocity);
            impact.penetrationDepth = Mathf.Max(0f, depth);
            bool queued = receiver.QueueImpact(impact, BuildFluidImpact());
            if (queued)
            {
                acceptedImpactVersion++;
                if (triggerOnce) triggered = true;
            }
            return queued;
        }

        BrazeFractureImpact BuildImpact(Vector3 position, Vector3 direction, Vector3 sourceVelocity)
            => new(position, direction, radius, impulse)
            {
                spin = spin,
                radialBlend = radialBlend,
                upwardBias = upwardBias,
                penetrationDepth = penetrationDepth,
                velocityInheritance = velocityInheritance,
                sourceVelocity = sourceVelocity,
                falloff = falloff,
                affectReleased = affectReleased,
            };

        /// <summary>Queue this source's authored impact at an explicit world-space point.</summary>
        public bool Fire(BrazeVoronoiFracture receiver, Vector3 point, Vector3 travelDirection,
                         Vector3 sourceVelocity = default)
        {
            if (triggered || receiver == null) return false;
            BrazeFractureImpact impact = BuildImpact(point, travelDirection, sourceVelocity);
            bool queued = receiver.QueueImpact(impact, BuildFluidImpact());
            if (queued)
            {
                acceptedImpactVersion++;
                if (triggerOnce) triggered = true;
            }
            return queued;
        }

        BrazeFractureFluidImpact BuildFluidImpact() => new()
        {
            enabled = depositToFluid,
            channels = new Vector3(
                Mathf.Max(0f, fluidSmoke),
                Mathf.Max(0f, fluidHeat),
                Mathf.Max(0f, fluidFuel)),
            duration = fluidDuration,
            cellFraction = Mathf.Clamp(fluidCellPercentage, 1f, 100f) * 0.01f,
            buoyancy = fluidBuoyancy,
            velocityInherit = fluidVelocityBlend,
            burstSpeed = fluidBurstSpeed,
            blastDirection = Mathf.Clamp(fluidBlastDirection, -1f, 1f),
            radialSpread = fluidRadialSpread,
            turbulence = fluidTurbulence,
            surfaceBias = fluidSurfaceBias,
            container = fluidContainer,
        };

        /// <summary>Fire at this Transform. An empty receiver override checks active fractures automatically.</summary>
        public bool FireFromTransform()
        {
            Vector3 start = transform.position;
            Vector3 direction = transform.forward.sqrMagnitude > 1e-8f ? transform.forward.normalized : Vector3.forward;
            Vector3 end = start + direction * Mathf.Max(0f, penetrationDepth);
            if (target != null)
            {
                if (!target.TryGetImpactContact(start, end, radius, out Vector3 contact)) contact = start;
                return Fire(target, contact, direction, Vector3.zero);
            }

            var fractures = BrazeVoronoiFracture.Active;
            for (int i = 0; i < fractures.Count; i++)
            {
                BrazeVoronoiFracture receiver = fractures[i];
                if (receiver == null || !receiver.TryGetImpactContact(start, end, radius, out Vector3 contact)) continue;
                if (Fire(receiver, contact, direction, Vector3.zero)) return true;
            }
            return false;
        }

        /// <summary>Re-arm a Trigger Once source without disabling its GameObject.</summary>
        [BrazeTimelineEvent("IMPACT/Rearm")]
        public void Rearm()
        {
            triggered = false;
            previousPosition = transform.position;
            hasPreviousPosition = true;
        }

        void OnValidate()
        {
            radius = Mathf.Max(0.001f, radius);
            impulse = Mathf.Max(0f, impulse);
            spin = Mathf.Max(0f, spin);
            radialBlend = Mathf.Clamp01(radialBlend);
            upwardBias = Mathf.Max(0f, upwardBias);
            penetrationDepth = Mathf.Max(0f, penetrationDepth);
            velocityInheritance = Mathf.Clamp(velocityInheritance, 0f, 2f);
            falloff = Mathf.Max(0.01f, falloff);
            fluidDuration = Mathf.Clamp(fluidDuration, 0.05f, 10f);
            fluidCellPercentage = Mathf.Clamp(fluidCellPercentage, 1f, 100f);
            fluidSmoke = Mathf.Max(0f, fluidSmoke);
            fluidHeat = Mathf.Max(0f, fluidHeat);
            fluidFuel = Mathf.Max(0f, fluidFuel);
            fluidVelocityBlend = Mathf.Clamp01(fluidVelocityBlend);
            fluidBuoyancy = Mathf.Clamp(fluidBuoyancy, -300f, 300f);
            fluidBurstSpeed = Mathf.Clamp(fluidBurstSpeed, 0f, 30f);
            fluidBlastDirection = Mathf.Clamp(fluidBlastDirection, -1f, 1f);
            fluidRadialSpread = Mathf.Clamp01(fluidRadialSpread);
            fluidTurbulence = Mathf.Clamp(fluidTurbulence, 0f, 5f);
            fluidSurfaceBias = Mathf.Clamp01(fluidSurfaceBias);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.3f, 0.08f, 0.8f);
            Vector3 tail = transform.position;
            Vector3 direction = transform.forward.normalized;
            float safeRadius = Mathf.Max(0.001f, radius);
            float depth = Mathf.Max(0f, penetrationDepth);

            if (depth <= 1e-5f)
            {
                Gizmos.DrawWireSphere(tail, safeRadius);
                DrawArrow(tail, direction, safeRadius * 1.5f);
                return;
            }

            Vector3 nose = tail + direction * depth;
            Vector3 shoulder = tail + direction * (depth * 0.68f);
            Vector3 right = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            Vector3 up = Vector3.Cross(right, direction).normalized;
            DrawRing(tail, right, up, safeRadius);
            DrawRing(shoulder, right, up, safeRadius);

            // Four body rails keep the volume readable from every Scene-view angle.
            Gizmos.DrawLine(tail + right * safeRadius, shoulder + right * safeRadius);
            Gizmos.DrawLine(tail - right * safeRadius, shoulder - right * safeRadius);
            Gizmos.DrawLine(tail + up * safeRadius, shoulder + up * safeRadius);
            Gizmos.DrawLine(tail - up * safeRadius, shoulder - up * safeRadius);

            // Ogive silhouette: eight shoulder points converge at the travel/penetration tip.
            const int NoseRails = 8;
            for (int rail = 0; rail < NoseRails; rail++)
            {
                float angle = rail * (Mathf.PI * 2f / NoseRails);
                Vector3 rim = shoulder + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * safeRadius;
                Gizmos.DrawLine(rim, nose);
            }

            Gizmos.DrawLine(tail, nose);
        }

        static void DrawRing(Vector3 center, Vector3 right, Vector3 up, float ringRadius)
        {
            const int Segments = 24;
            Vector3 previous = center + right * ringRadius;
            for (int i = 1; i <= Segments; i++)
            {
                float angle = i * (Mathf.PI * 2f / Segments);
                Vector3 next = center + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * ringRadius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }

        static void DrawArrow(Vector3 start, Vector3 direction, float length)
        {
            float safeLength = Mathf.Max(0.05f, length);
            Vector3 end = start + direction * safeLength;
            Vector3 side = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            float head = Mathf.Min(safeLength * 0.3f, 0.2f);
            Gizmos.DrawLine(start, end);
            Gizmos.DrawLine(end, end - direction * head + side * head * 0.5f);
            Gizmos.DrawLine(end, end - direction * head - side * head * 0.5f);
        }
    }

    /// <summary>One update callback for every active custom projectile source.</summary>
    [DefaultExecutionOrder(100)]
    sealed class BrazeFractureImpactDriver : MonoBehaviour
    {
        static BrazeFractureImpactDriver instance;

        internal static void Ensure()
        {
            if (!Application.isPlaying || instance != null) return;
            var go = new GameObject("Braze Fracture Impact Driver") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<BrazeFractureImpactDriver>();
        }

        void Update()
        {
            var sources = BrazeFractureImpactSource.Active;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                BrazeFractureImpactSource source = sources[i];
                if (source == null) { sources.RemoveAt(i); continue; }
                if (source.isActiveAndEnabled) source.ProcessSweep();
            }
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
