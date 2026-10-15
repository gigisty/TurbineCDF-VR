using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdFlowSwirlMode
    {
        Off = 0,
        Manual = 1,
        FollowTransform = 2,
    }

    public enum BrazeCfdFlowDyePattern
    {
        Full = 0,
        RotorStreaks = 1,
    }

    /// <summary>A local GPU airflow source that drives directed velocity inside one CFD domain.</summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Flow Source", 1)]
    public sealed class BrazeCfdFlowSource : BrazeCfdSource
    {
        public BrazeCfdSourceShape shape = BrazeCfdSourceShape.Box;
        [Tooltip("Sphere/Cylinder radius in world units before Transform scale.")]
        [Min(0.01f)] public float radius = 0.5f;
        [Tooltip("Full Cylinder height in world units before Transform scale. The cylinder axis is local Y.")]
        [Min(0.01f)] public float height = 0.25f;
        [Tooltip("Air speed along local +Z, in world units per second.")]
        [Min(0f)] public float speed = 2f;
        [Tooltip("Velocity response in 1/seconds. Higher values make the source reach its authored axial and " +
                 "tangential speeds faster.")]
        [Min(0f)] public float velocityResponse = 8f;
        [Tooltip("Optional tangential velocity around local +Z. Manual uses the signed Swirl Ratio directly; " +
                 "Follow Transform detects its sign from a rotating propeller/fan Transform.")]
        public BrazeCfdFlowSwirlMode swirlMode;
        [Tooltip("Follow Transform only: the rotating propeller or fan pivot. Its world angular velocity is " +
                 "projected onto this source's local +Z flow axis.")]
        public Transform rotationSource;
        [Range(-1f, 1f), Tooltip("Tangential rim speed divided by axial Speed. In Follow Transform mode the " +
                                 "detected rotation sign is multiplied by this value; use a negative ratio only " +
                                 "when the modeled blade pitch requires reversed wake swirl.")]
        public float swirlRatio = 0.2f;
        [Tooltip("Optional heat added by the source in degrees Celsius per second. Zero adds no heat.")]
        [Min(0f)] public float heatRateCPerSecond;
        [Tooltip("Inject passive diagnostic dye from this internal flow source. Dye affects presentation only; " +
                 "it does not change velocity, pressure, or temperature.")]
        public bool injectPassiveDye;
        [Tooltip("Passive dye added per second inside the source volume. One is a useful visible default for " +
                 "compact fans and actuator disks.")]
        [Min(0f)] public float passiveDyeStrength = 1f;
        [Tooltip("Full fills the source volume. Rotor Streaks seeds angular tracer bands so tangential motion " +
                 "becomes visible instead of rotating a symmetric dye cloud invisibly.")]
        public BrazeCfdFlowDyePattern passiveDyePattern = BrazeCfdFlowDyePattern.Full;
        [Range(1, 12), Tooltip("Number of angular tracer bands emitted by Rotor Streaks.")]
        public int dyeStreakCount = 4;
        [Range(0.05f, 0.9f), Tooltip("Angular width of each tracer band.")]
        public float dyeStreakWidth = 0.22f;

        [System.NonSerialized] Transform trackedRotationSource;
        [System.NonSerialized] Quaternion previousRotorRotation;
        [System.NonSerialized] bool rotorMotionValid;
        [System.NonSerialized] float detectedSignedAngularSpeed;

        /// <summary>Whether this source currently contributes visible passive dye to its target domain.</summary>
        public bool InjectsPassiveDye => injectPassiveDye && passiveDyeStrength > 0.0001f;
        internal float PassiveDyeRate => InjectsPassiveDye ? passiveDyeStrength : 0f;
        public float DetectedRotorRpm => detectedSignedAngularSpeed * (30f / Mathf.PI);

        protected override void OnEnable()
        {
            ResetRotorMotion();
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ResetRotorMotion();
        }

        protected override void OnValidate()
        {
            radius = Mathf.Max(0.01f, radius);
            height = Mathf.Max(0.01f, height);
            speed = Mathf.Max(0f, speed);
            velocityResponse = Mathf.Max(0f, velocityResponse);
            swirlRatio = Mathf.Clamp(swirlRatio, -1f, 1f);
            heatRateCPerSecond = Mathf.Max(0f, heatRateCPerSecond);
            passiveDyeStrength = Mathf.Max(0f, passiveDyeStrength);
            dyeStreakCount = Mathf.Clamp(dyeStreakCount, 1, 12);
            dyeStreakWidth = Mathf.Clamp(dyeStreakWidth, 0.05f, 0.9f);
            base.OnValidate();
        }

        /// <summary>Signed tangential speed at the source rim, in world units/second. Transform-following uses
        /// angular velocity only for direction: raw blade-tip speed is deliberately not imposed on a coarse grid.</summary>
        internal float ResolveSignedSwirlSpeed(float dt)
        {
            if (swirlMode == BrazeCfdFlowSwirlMode.Off || Mathf.Abs(swirlRatio) <= 0.0001f || speed <= 0f)
            {
                detectedSignedAngularSpeed = 0f;
                return 0f;
            }
            if (swirlMode == BrazeCfdFlowSwirlMode.Manual)
            {
                ResetRotorMotion();
                return speed * swirlRatio;
            }
            if (rotationSource == null)
            {
                ResetRotorMotion();
                return 0f;
            }

            if (trackedRotationSource != rotationSource)
            {
                trackedRotationSource = rotationSource;
                previousRotorRotation = rotationSource.rotation;
                rotorMotionValid = true;
                detectedSignedAngularSpeed = 0f;
                return 0f;
            }

            Quaternion current = rotationSource.rotation;
            detectedSignedAngularSpeed = 0f;
            if (rotorMotionValid && dt > 1e-6f)
            {
                Quaternion delta = current * Quaternion.Inverse(previousRotorRotation);
                delta.ToAngleAxis(out float angleDegrees, out Vector3 axis);
                if (angleDegrees > 180f) angleDegrees -= 360f;
                if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x) && axis.sqrMagnitude > 1e-8f)
                {
                    Vector3 angularVelocity = axis.normalized * (angleDegrees * Mathf.Deg2Rad / dt);
                    detectedSignedAngularSpeed = Vector3.Dot(angularVelocity, transform.forward);
                }
            }
            previousRotorRotation = current;
            rotorMotionValid = true;
            if (Mathf.Abs(detectedSignedAngularSpeed) <= 0.01f) return 0f;
            return Mathf.Sign(detectedSignedAngularSpeed) * speed * swirlRatio;
        }

        void ResetRotorMotion()
        {
            trackedRotationSource = null;
            rotorMotionValid = false;
            detectedSignedAngularSpeed = 0f;
        }

        static readonly Color FlowColor = new(0.12f, 0.82f, 1f, 0.9f);

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Matrix4x4 old = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = FlowColor;
            switch (shape)
            {
                case BrazeCfdSourceShape.Sphere:
                    Gizmos.DrawWireSphere(Vector3.zero, radius);
                    break;
                case BrazeCfdSourceShape.Cylinder:
                    Gizmos.DrawWireCube(Vector3.zero, new Vector3(radius * 2f, height, radius * 2f));
                    break;
                default:
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                    break;
            }
            float arrow = Mathf.Max(0.4f, Mathf.Min(speed, 4f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * arrow);
            Gizmos.DrawLine(Vector3.forward * arrow, new Vector3(0.14f, 0f, arrow - 0.25f));
            Gizmos.DrawLine(Vector3.forward * arrow, new Vector3(-0.14f, 0f, arrow - 0.25f));
            Gizmos.matrix = old;
            Gizmos.color = oldColor;
        }
    }
}
