using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Procedural transform animation: local position, rotation and scale motion layered on top of the
    /// incoming transform without accumulating drift. Built for loops, 360-degree spins, one-shot moves,
    /// and organic noise motion. Play-mode only — in edit mode the object stays at its authored pose.
    /// </summary>
    [AddComponentMenu("BrazeFX/Cinematics/Braze Motion", 52)]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class BrazeMotion : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum CycleMode { FreeRun, Loop, PingPong, Once }
        public enum MotionMode { Oscillate, Noise }
        public enum RotationMode { Oscillate, Spin, Noise }
        public enum ScaleApplyMode { Multiplier, Additive }

        [Tooltip("Offsets are layered on top of the incoming transform. Off writes generated values from zero/identity/one.")]
        public bool relative = true;
        [Tooltip("Master seed for procedural noise and per-axis offsets.")]
        public int seed = 0;
        [Tooltip("Master amplitude multiplier for all enabled motion.")]
        [BrazeAnimatable("MOTION/Strength", 0f, 4f)]
        [Min(0f)] public float strength = 1f;
        [Tooltip("Restore the captured rest transform when this component is disabled.")]
        public bool restoreOnDisable = false;
        public bool playing = true;

        [Tooltip("How cycle time is evaluated.")]
        public CycleMode cycleMode = CycleMode.Loop;
        [Tooltip("Cycle length in seconds. Spin mode uses this as seconds per Amount.")]
        [BrazeAnimatable("CYCLE/Duration", 0.001f, 20f)]
        [Min(0.001f)] public float duration = 2f;
        [Tooltip("Local playback speed multiplier.")]
        [BrazeAnimatable("CYCLE/Time Scale", 0f, 4f)]
        [Min(0f)] public float timeScale = 1f;
        [Tooltip("For Loop/PingPong/Once, rounds oscillation/noise frequencies to whole cycles per Duration.")]
        public bool seamlessLoop = true;
        [Tooltip("Remaps normalized cycle time before sampling motion.")]
        public AnimationCurve timeEase = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Envelope multiplied into Strength over each cycle.")]
        public AnimationCurve strengthCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        public bool enablePosition = false;
        public MotionMode positionMode = MotionMode.Oscillate;
        [Tooltip("Position travel in local units. Oscillate/Noise use +/- this value.")]
        public Vector3 positionAmount = new(0.1f, 0f, 0f);
        [Tooltip("Position cycles per second.")]
        public Vector3 positionSpeed = Vector3.one * 2f;
        [Tooltip("Per-axis timing offset, 0-1 of a cycle.")]
        public Vector3 positionOffset;

        public bool enableRotation = false;
        public RotationMode rotationMode = RotationMode.Spin;
        [Tooltip("Rotation amount in degrees. Spin uses degrees per cycle; Oscillate/Noise use +/- this value.")]
        public Vector3 rotationAmount = new(0f, 360f, 0f);
        [Tooltip("Rotation cycles per second. In Spin mode, Amount / Cycle is multiplied by this speed.")]
        public Vector3 rotationSpeed = Vector3.one;
        [Tooltip("Per-axis timing offset, 0-1 of a cycle.")]
        public Vector3 rotationOffset;

        public bool enableScale = false;
        public MotionMode scaleMode = MotionMode.Oscillate;
        public bool uniformScale = true;
        public ScaleApplyMode scaleApply = ScaleApplyMode.Multiplier;
        [Tooltip("Scale travel. Multiplier mode means 0.2 = +/-20%; Additive mode means local-scale units.")]
        public Vector3 scaleAmount = Vector3.one * 0.1f;
        [Tooltip("Scale cycles per second.")]
        public Vector3 scaleSpeed = Vector3.one * 2f;
        [Tooltip("Per-axis timing offset, 0-1 of a cycle.")]
        public Vector3 scaleOffset;

        [SerializeField, HideInInspector] Vector3 restLocalPosition;
        [SerializeField, HideInInspector] Quaternion restLocalRotation = Quaternion.identity;
        [SerializeField, HideInInspector] Vector3 restLocalScale = Vector3.one;
        [SerializeField, HideInInspector] bool restCaptured;

        struct TransformState
        {
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
        }

        TransformState lastInput;
        TransformState lastOutput;
        bool hasApplied;
        double lastRealtime;
        float clock;

        const float TwoPi = Mathf.PI * 2f;

        void OnEnable()
        {
            if (!restCaptured) CaptureRestPose();
            lastRealtime = Now();
            hasApplied = false;
        }

        void OnDisable()
        {
            if (restoreOnDisable && restCaptured) RestoreRestPose();
            hasApplied = false;
        }

        void OnValidate()
        {
            duration = Mathf.Max(0.001f, duration);
            timeScale = Mathf.Max(0f, timeScale);
            strength = Mathf.Max(0f, strength);
            positionSpeed = NonNegative(positionSpeed);
            rotationSpeed = NonNegative(rotationSpeed);
            scaleSpeed = NonNegative(scaleSpeed);
            positionOffset = Wrap01(positionOffset);
            rotationOffset = Wrap01(rotationOffset);
            scaleOffset = Wrap01(scaleOffset);
            timeEase ??= AnimationCurve.Linear(0f, 0f, 1f, 1f);
            strengthCurve ??= AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }

        public void OnBrazeAnimated() => OnValidate();

        void LateUpdate()
        {
            if (!Application.isPlaying) return;   // play-mode only — never animate in edit mode
            if (!gameObject.scene.IsValid()) return;
            if (!restCaptured) CaptureRestPose();

            float dt = DeltaTime();
            if (playing && timeScale > 0f) clock += dt * timeScale;
            Apply(clock);
        }

        [BrazeTimelineEvent("PLAYBACK/Restart")]
        public void Restart()
        {
            clock = 0f;
            lastRealtime = Now();
            hasApplied = false;
            Apply(clock);
        }

        [ContextMenu("Capture Rest Pose")]
        public void CaptureRestPose()
        {
            TransformState s = ReadInputState();
            restLocalPosition = s.localPosition;
            restLocalRotation = s.localRotation;
            restLocalScale = s.localScale;
            restCaptured = true;
            hasApplied = false;
        }

        [ContextMenu("Restore Rest Pose")]
        public void RestoreRestPose()
        {
            if (!restCaptured) return;
            transform.localPosition = restLocalPosition;
            transform.localRotation = restLocalRotation;
            transform.localScale = restLocalScale;
            hasApplied = false;
        }

        void Apply(float t)
        {
            TransformState input = relative ? ReadInputState() : AbsoluteInput();
            float cycle = Cycle01(t);
            float eased = Mathf.Clamp01((timeEase ?? AnimationCurve.Linear(0f, 0f, 1f, 1f)).Evaluate(cycle));
            float envelope = Mathf.Max(0f, (strengthCurve ?? AnimationCurve.Linear(0f, 1f, 1f, 1f)).Evaluate(cycle));
            float gain = strength * envelope;

            TransformState output = input;
            if (enablePosition)
                output.localPosition = input.localPosition +
                    SampleVector(positionMode, positionAmount, positionSpeed, positionOffset, t, eased, 11, false) * gain;

            if (enableRotation)
            {
                Vector3 euler = rotationMode == RotationMode.Spin
                    ? SpinVector(rotationAmount, rotationSpeed, t, eased)
                    : SampleVector((MotionMode)rotationMode, rotationAmount, rotationSpeed, rotationOffset, t, eased, 31, false);
                output.localRotation = input.localRotation * Quaternion.Euler(euler * gain);
            }

            if (enableScale)
            {
                Vector3 s = SampleVector(scaleMode, scaleAmount, scaleSpeed, scaleOffset, t, eased, 53, uniformScale) * gain;
                output.localScale = scaleApply == ScaleApplyMode.Multiplier
                    ? Vector3.Scale(input.localScale, Vector3.one + s)
                    : input.localScale + s;
                output.localScale = Max(output.localScale, 0.0001f);
            }

            transform.localPosition = output.localPosition;
            transform.localRotation = output.localRotation;
            transform.localScale = output.localScale;
            lastInput = input;
            lastOutput = output;
            hasApplied = true;
        }

        TransformState ReadInputState()
        {
            TransformState current = CurrentState();
            return hasApplied && Approximately(current, lastOutput) ? lastInput : current;
        }

        TransformState CurrentState() => new TransformState
        {
            localPosition = transform.localPosition,
            localRotation = transform.localRotation,
            localScale = transform.localScale,
        };

        TransformState AbsoluteInput() => new TransformState
        {
            localPosition = Vector3.zero,
            localRotation = Quaternion.identity,
            localScale = Vector3.one,
        };

        Vector3 SampleVector(MotionMode mode, Vector3 amount, Vector3 speed, Vector3 offset, float t, float eased, int salt, bool uniform)
        {
            if (uniform)
            {
                float v = Signal(mode, speed.x, offset.x, t, eased, salt);
                return Vector3.one * (amount.x * v);
            }

            return new Vector3(
                amount.x * Signal(mode, speed.x, offset.x, t, eased, salt + 0),
                amount.y * Signal(mode, speed.y, offset.y, t, eased, salt + 1),
                amount.z * Signal(mode, speed.z, offset.z, t, eased, salt + 2));
        }

        Vector3 SpinVector(Vector3 amount, Vector3 speed, float t, float eased)
        {
            return new Vector3(
                amount.x * SpinProgress(speed.x, t, eased),
                amount.y * SpinProgress(speed.y, t, eased),
                amount.z * SpinProgress(speed.z, t, eased));
        }

        float SpinProgress(float speed, float t, float eased)
        {
            if (speed <= 0f) return 0f;
            if (cycleMode == CycleMode.FreeRun) return t * speed;

            float cycles = seamlessLoop ? LoopCycles(speed) : speed * Mathf.Max(0.001f, duration);
            return eased * cycles;
        }

        float Signal(MotionMode mode, float speed, float offset, float t, float eased, int salt)
        {
            if (speed <= 0f) return 0f;

            bool locked = cycleMode != CycleMode.FreeRun && seamlessLoop;
            float p = locked
                ? eased * LoopCycles(speed) + offset
                : t * speed + offset + Hash01((uint)(seed * 73856093 ^ salt * 19349663));

            if (mode == MotionMode.Oscillate) return Mathf.Sin(p * TwoPi);
            return locked ? LoopedNoise(p, salt) : FreeNoise(t * speed + offset, salt);
        }

        float FreeNoise(float p, int salt)
        {
            float x = Hash01((uint)(seed * 83492791 ^ salt * 265443576)) * 1000f;
            float y = p + Hash01((uint)(seed * 374761393 ^ salt * 668265263)) * 1000f;
            return Mathf.PerlinNoise(x, y) * 2f - 1f;
        }

        float LoopedNoise(float p, int salt)
        {
            float a = p * TwoPi;
            float r = 0.73f;
            float ox = Hash01((uint)(seed * 1597334677 ^ salt * 3812015801u)) * 1000f;
            float oy = Hash01((uint)(seed * 3812015801u ^ salt * 1597334677)) * 1000f;
            float n0 = Mathf.PerlinNoise(ox + Mathf.Cos(a) * r, oy + Mathf.Sin(a) * r);
            float n1 = Mathf.PerlinNoise(ox + Mathf.Cos(a * 2f + 1.7f) * r * 1.9f,
                                         oy + Mathf.Sin(a * 2f + 1.7f) * r * 1.9f);
            return (n0 * 0.72f + n1 * 0.28f) * 2f - 1f;
        }

        float LoopCycles(float speed)
        {
            if (duration <= 0.001f) return 1f;
            return Mathf.Max(1f, Mathf.Round(speed * duration));
        }

        float Cycle01(float t)
        {
            float n = t / Mathf.Max(0.001f, duration);
            return cycleMode switch
            {
                CycleMode.Loop => Mathf.Repeat(n, 1f),
                CycleMode.PingPong => Mathf.PingPong(n, 1f),
                CycleMode.Once => Mathf.Clamp01(n),
                _ => Mathf.Repeat(n, 1f),
            };
        }

        float DeltaTime()
        {
            double now = Now();
            float dt = Application.isPlaying ? Time.deltaTime : (float)(now - lastRealtime);
            lastRealtime = now;
            return Mathf.Clamp(dt, 0f, 0.1f);
        }

        static double Now() => Time.realtimeSinceStartupAsDouble;

        static bool Approximately(TransformState a, TransformState b)
        {
            return (a.localPosition - b.localPosition).sqrMagnitude < 1e-10f
                && (a.localScale - b.localScale).sqrMagnitude < 1e-10f
                && Quaternion.Angle(a.localRotation, b.localRotation) < 0.001f;
        }

        static Vector3 NonNegative(Vector3 v) =>
            new Vector3(Mathf.Max(0f, v.x), Mathf.Max(0f, v.y), Mathf.Max(0f, v.z));

        static Vector3 Wrap01(Vector3 v) =>
            new Vector3(Mathf.Repeat(v.x, 1f), Mathf.Repeat(v.y, 1f), Mathf.Repeat(v.z, 1f));

        static Vector3 Max(Vector3 v, float min) =>
            new Vector3(Mathf.Max(min, v.x), Mathf.Max(min, v.y), Mathf.Max(min, v.z));

        static float Hash01(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777216f;
        }
    }
}
