using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Iteratively separates neighboring fracture fragments through direct positional offsets.</summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Push Apart Effector", 49)]
    public sealed class BrazePushApartEffector : BrazeFractureEffector
    {
        public enum DirectionMode { All, AlongX, AlongY, AlongZ }

        [Header("PUSH APART")]
        [Tooltip("All uses the shortest 3D separation direction. Axis modes constrain movement to one world axis.")]
        public DirectionMode direction = DirectionMode.All;
        [Tooltip("For Cloners, derive each contact radius from its source mesh bounds and current scale. Fractures continue to use the authored Radius.")]
        public bool useCloneBounds = true;
        [Min(0f)] [Tooltip("Extra clearance around scale-aware Cloner bounds. Without Clone Bounds, this remains the virtual radius used by every item.")]
        [BrazeAnimatable("PUSH APART/Radius", 0f, 10f)] public float separationRadius = 0.1f;
        [Range(1, 32)] [Tooltip("Repeated relaxation passes. More passes resolve dense overlaps more precisely but cost more.")]
        public int iterations = 4;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.PushApart;

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "direction" => "EFFECTOR/SEPARATION/Direction",
            "separationRadius" => "EFFECTOR/SEPARATION/Radius",
            "iterations" => "EFFECTOR/SEPARATION/Iterations",
            _ => fallbackPath,
        };

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            var flags = (FractureEffectorFlags)gpu.flags;
            flags &= ~(FractureEffectorFlags.Position | FractureEffectorFlags.Rotation |
                       FractureEffectorFlags.Scale | FractureEffectorFlags.UniformScale |
                       FractureEffectorFlags.EffectorSpace);
            flags |= FractureEffectorFlags.Position;
            gpu.flags = (int)flags;
            gpu.mode = (int)direction;
            gpu.parameters = new Vector4(Mathf.Max(0f, separationRadius), Mathf.Clamp(iterations, 1, 32),
                useCloneBounds ? 1f : 0f, 0f);
        }

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + (int)direction;
                h = h * 31 + (useCloneBounds ? 1 : 0);
                h = h * 31 + separationRadius.GetHashCode();
                h = h * 31 + iterations;
                return h;
            }
        }

        protected override Color GizmoColor => new(0.35f, 0.8f, 1f, 0.8f);

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            affectPosition = true;
            affectRotation = false;
            affectScale = false;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            separationRadius = Mathf.Max(0f, separationRadius);
            iterations = Mathf.Clamp(iterations, 1, 32);
        }
    }

    /// <summary>Owner-local baked correction for one Push Apart effector.</summary>
    internal sealed class BrazePushApartGpuState
    {
        internal GraphicsBuffer bakedCorrection;
        internal int lastUsedGeneration;

        int count;
        int stackIndex = -1;
        int bakeHash;
        bool hasBake;

        internal bool HasBake => hasBake;
        internal long GpuBytes => bakedCorrection != null
            ? (long)bakedCorrection.count * bakedCorrection.stride
            : 0L;

        internal void Prepare(int instanceCount, int currentStackIndex, int currentBakeHash)
        {
            instanceCount = Mathf.Max(1, instanceCount);
            bool layoutChanged = count != instanceCount;
            bool semanticsChanged = stackIndex != currentStackIndex || bakeHash != currentBakeHash;
            if (layoutChanged)
            {
                ReleaseBuffer();
                bakedCorrection = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, instanceCount, sizeof(float) * 4);
                count = instanceCount;
            }
            stackIndex = currentStackIndex;
            bakeHash = currentBakeHash;
            if (layoutChanged || semanticsChanged) hasBake = false;
        }

        internal void CommitBake() => hasBake = true;

        internal void Release()
        {
            ReleaseBuffer();
            count = 0;
            stackIndex = -1;
            bakeHash = 0;
            hasBake = false;
        }

        void ReleaseBuffer()
        {
            bakedCorrection?.Release();
            bakedCorrection = null;
        }
    }

    internal sealed class BrazePushApartGpuStateCollection
    {
        readonly Dictionary<int, BrazePushApartGpuState> states = new();
        readonly List<int> stale = new();
        int generation;

        internal long GpuBytes
        {
            get
            {
                long bytes = 0L;
                foreach (BrazePushApartGpuState state in states.Values) bytes += state.GpuBytes;
                return bytes;
            }
        }

        internal void BeginEvaluation()
        {
            if (generation == int.MaxValue)
            {
                generation = 1;
                foreach (BrazePushApartGpuState state in states.Values) state.lastUsedGeneration = 0;
            }
            else generation++;
        }

        internal BrazePushApartGpuState Use(
            BrazePushApartEffector effector, int count, int stackIndex, int bakeHash)
        {
            int key = effector.BrazeInstanceId();
            if (!states.TryGetValue(key, out BrazePushApartGpuState state))
            {
                state = new BrazePushApartGpuState();
                states.Add(key, state);
            }
            state.lastUsedGeneration = generation;
            state.Prepare(count, stackIndex, bakeHash);
            return state;
        }

        internal void EndEvaluation()
        {
            stale.Clear();
            foreach (KeyValuePair<int, BrazePushApartGpuState> pair in states)
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
            foreach (BrazePushApartGpuState state in states.Values) state.Release();
            states.Clear();
            stale.Clear();
        }
    }
}
