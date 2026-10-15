using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// C4D-style temporal modifier for the ordered Braze effector stack. Delay consumes the transform accumulated by
    /// effectors above it; the owning Cloner or Voronoi Fracture keeps its per-clone history entirely on the GPU.
    /// </summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Delay Effector", 50)]
    public sealed class BrazeDelayEffector : BrazeFractureEffector
    {
        public enum DelayMode { Even, Blend, Spring }

        [Header("DELAY")]
        [Tooltip("Even follows at a uniform rate. Blend responds quickly then eases. Spring adds damped overshoot.")]
        public DelayMode mode = DelayMode.Blend;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Delay;
        public override bool Animating => true;
        public override bool UsesSharedTransformChannels => false;

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "mode" => "EFFECTOR/DELAY/Mode",
            _ => fallbackPath,
        };

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            var flags = (FractureEffectorFlags)gpu.flags;
            flags &= ~(FractureEffectorFlags.Position | FractureEffectorFlags.Rotation |
                       FractureEffectorFlags.Scale | FractureEffectorFlags.UniformScale |
                       FractureEffectorFlags.EffectorSpace);
            gpu.flags = (int)flags;
            gpu.mode = (int)mode;
        }

        public override int ContentHash()
        {
            unchecked
            {
                return base.ContentHash() * 31 + (int)mode;
            }
        }

        protected override Color GizmoColor => new(1f, 0.72f, 0.16f, 0.8f);

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            strength = 0.5f;
            affectPosition = affectRotation = affectScale = false;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            strength = Mathf.Clamp01(strength);
        }
    }

    internal readonly struct BrazeDelayGpuSample
    {
        internal readonly bool hasHistory;
        internal readonly float deltaTime;

        internal BrazeDelayGpuSample(bool hasHistory, float deltaTime)
        {
            this.hasHistory = hasHistory;
            this.deltaTime = deltaTime;
        }
    }

    /// <summary>One owner-local history stream for one Delay effector.</summary>
    internal sealed class BrazeDelayGpuState
    {
        internal GraphicsBuffer position;
        internal GraphicsBuffer rotation;
        internal GraphicsBuffer scale;
        internal GraphicsBuffer velocity;
        internal int lastUsedGeneration;

        int count;
        int stackIndex = -1;
        BrazeDelayEffector.DelayMode mode = (BrazeDelayEffector.DelayMode)(-1);
        bool hasHistory;
        double lastTime = -1.0;

        internal long GpuBytes => Bytes(position) + Bytes(rotation) + Bytes(scale) + Bytes(velocity);

        internal void Prepare(int instanceCount, int currentStackIndex, BrazeDelayEffector.DelayMode currentMode)
        {
            instanceCount = Mathf.Max(1, instanceCount);
            bool layoutChanged = count != instanceCount;
            bool semanticsChanged = stackIndex != currentStackIndex || mode != currentMode;
            if (layoutChanged)
            {
                ReleaseBuffers();
                count = instanceCount;
                position = NewBuffer(count);
                rotation = NewBuffer(count);
                scale = NewBuffer(count);
            }

            bool spring = currentMode == BrazeDelayEffector.DelayMode.Spring;
            if (spring && (velocity == null || velocity.count != count))
            {
                ReleaseVelocities();
                velocity = NewVelocityBuffer(count);
                semanticsChanged = true;
            }
            else if (!spring && velocity != null)
            {
                ReleaseVelocities();
                semanticsChanged = true;
            }

            stackIndex = currentStackIndex;
            mode = currentMode;
            if (layoutChanged || semanticsChanged) ResetHistory();
        }

        internal BrazeDelayGpuSample BeginSample()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = now - lastTime;
            bool valid = hasHistory && elapsed >= 0.0 && elapsed <= 0.5;
            float delta = 1f / 60f;
            if (valid)
            {
                delta = elapsed <= 1e-6 ? 0f : Mathf.Clamp((float)elapsed, 1f / 240f, 1f / 30f);
            }
            else
            {
                hasHistory = false;
            }
            lastTime = now;
            return new BrazeDelayGpuSample(hasHistory, delta);
        }

        internal void CommitSample() => hasHistory = true;

        internal void Release()
        {
            ReleaseBuffers();
            count = 0;
            stackIndex = -1;
            mode = (BrazeDelayEffector.DelayMode)(-1);
            ResetHistory();
        }

        void ResetHistory()
        {
            hasHistory = false;
            lastTime = -1.0;
        }

        void ReleaseBuffers()
        {
            Release(ref position);
            Release(ref rotation);
            Release(ref scale);
            ReleaseVelocities();
        }

        void ReleaseVelocities()
        {
            Release(ref velocity);
        }

        static GraphicsBuffer NewBuffer(int bufferCount)
            => new(GraphicsBuffer.Target.Structured, Mathf.Max(1, bufferCount), sizeof(float) * 4);
        static GraphicsBuffer NewVelocityBuffer(int bufferCount)
            => new(GraphicsBuffer.Target.Structured, Mathf.Max(1, bufferCount), sizeof(float) * 12);

        static long Bytes(GraphicsBuffer buffer) => buffer != null ? (long)buffer.count * buffer.stride : 0L;
        static void Release(ref GraphicsBuffer buffer) { buffer?.Release(); buffer = null; }
    }

    /// <summary>Reclaims history immediately when a Delay effector leaves an owner's ordered stack.</summary>
    internal sealed class BrazeDelayGpuStateCollection
    {
        readonly Dictionary<int, BrazeDelayGpuState> states = new();
        readonly List<int> stale = new();
        int generation;

        internal long GpuBytes
        {
            get
            {
                long bytes = 0L;
                foreach (BrazeDelayGpuState state in states.Values) bytes += state.GpuBytes;
                return bytes;
            }
        }

        internal void BeginEvaluation()
        {
            if (generation == int.MaxValue)
            {
                generation = 1;
                foreach (BrazeDelayGpuState state in states.Values) state.lastUsedGeneration = 0;
            }
            else generation++;
        }

        internal BrazeDelayGpuState Use(BrazeDelayEffector effector, int count, int stackIndex)
        {
            int key = effector.BrazeInstanceId();
            if (!states.TryGetValue(key, out BrazeDelayGpuState state))
            {
                state = new BrazeDelayGpuState();
                states.Add(key, state);
            }
            state.lastUsedGeneration = generation;
            state.Prepare(count, stackIndex, effector.mode);
            return state;
        }

        internal void EndEvaluation()
        {
            stale.Clear();
            foreach (KeyValuePair<int, BrazeDelayGpuState> pair in states)
                if (pair.Value.lastUsedGeneration != generation) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                states[stale[i]].Release();
                states.Remove(stale[i]);
            }
            stale.Clear();
        }

        internal void Release()
        {
            foreach (BrazeDelayGpuState state in states.Values) state.Release();
            states.Clear();
            stale.Clear();
        }
    }
}
