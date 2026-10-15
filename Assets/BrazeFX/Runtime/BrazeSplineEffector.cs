using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Arranges fracture fragments along a BrazeSpline as a non-destructive transform layer.</summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Spline Effector", 47)]
    public sealed class BrazeSplineEffector : BrazeFractureEffector
    {
        public enum Ordering { Index, ObjectX, ObjectY, ObjectZ, Radial }
        public enum UpMode { World, Spline, Custom }

        [Header("SPLINE")]
        [Tooltip("Braze spline used as the fragment path. Empty = no effect.")]
        public BrazeSpline spline;
        [Tooltip("Determines how fragments are ordered from the start to the end of the path.")]
        public Ordering ordering = Ordering.ObjectX;
        [Range(0f, 1f)] public float start = 0f;
        [Range(0f, 1f)] public float end = 1f;
        [Tooltip("Moves all fragments along the path. Values are fractions of the full spline.")]
        [BrazeAnimatable("SPLINE/Offset", -2f, 2f)] public float offset = 0f;
        [Tooltip("Wrap fragments crossing either end to the opposite side instead of clamping them.")]
        public bool loop = false;
        [Tooltip("Distribute by world distance rather than raw spline parameter.")]
        public bool constantSpacing = true;
        [Range(8, 256)] [Tooltip("Resolution of the compact path uploaded to the fracture GPU.")]
        public int pathSamples = 96;

        [Header("TRANSFORM")]
        [Tooltip("Move fragment pivots onto the spline.")]
        public bool positionOnSpline = true;
        [Tooltip("Additional position in the spline frame: X = right, Y = up, Z = along the path.")]
        public Vector3 positionOffset;
        [Tooltip("Orient fragments along the spline tangent.")]
        public bool alignRotation = true;
        public FractureEffectorAimAxis aimAxis = FractureEffectorAimAxis.PositiveZ;
        public UpMode upMode = UpMode.World;
        [Tooltip("World-space up direction used when Up Mode is Custom.")]
        public Vector3 customUp = Vector3.up;
        [Tooltip("Additional local Euler rotation after tangent alignment.")]
        public Vector3 rotationOffset;

        readonly List<Vector3> cachedSamples = new();
        Vector3[] denseSamples;
        float[] denseLengths;
        int cachedPathHash = int.MinValue;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Spline;

        protected override bool IsFunctionAnimatableMemberRelevant(string member) => member switch
        {
            "positionOffset" => positionOnSpline,
            "rotationOffset" or "aimAxis" or "upMode" => alignRotation,
            "customUp" => alignRotation && upMode == UpMode.Custom,
            _ => true,
        };

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "spline" => "EFFECTOR/PATH/Spline",
            "ordering" => "EFFECTOR/PATH/Order",
            "start" => "EFFECTOR/PATH/Start",
            "end" => "EFFECTOR/PATH/End",
            "offset" => "EFFECTOR/PATH/Offset",
            "loop" => "EFFECTOR/PATH/Loop",
            "constantSpacing" => "EFFECTOR/PATH/Constant Spacing",
            "pathSamples" => "EFFECTOR/PATH/Path Detail",
            "positionOnSpline" => "EFFECTOR/TRANSFORM/Position on Spline",
            "positionOffset" => "EFFECTOR/TRANSFORM/Position Offset",
            "alignRotation" => "EFFECTOR/TRANSFORM/Align Rotation",
            "rotationOffset" => "EFFECTOR/TRANSFORM/Rotation Offset",
            "aimAxis" => "EFFECTOR/AIM/Aim Axis",
            "upMode" => "EFFECTOR/AIM/Up Vector",
            "customUp" => "EFFECTOR/AIM/Custom Up",
            _ => fallbackPath,
        };

        Vector3 UpVector
        {
            get
            {
                Vector3 up = upMode switch
                {
                    UpMode.Spline => spline != null ? spline.transform.up : transform.up,
                    UpMode.Custom => customUp,
                    _ => Vector3.up,
                };
                return up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            }
        }

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            var flags = (FractureEffectorFlags)gpu.flags;
            flags &= ~(FractureEffectorFlags.Position | FractureEffectorFlags.Rotation |
                       FractureEffectorFlags.Scale | FractureEffectorFlags.UniformScale |
                       FractureEffectorFlags.EffectorSpace);
            if (positionOnSpline) flags |= FractureEffectorFlags.Position;
            if (alignRotation) flags |= FractureEffectorFlags.Rotation;
            if (loop) flags |= FractureEffectorFlags.SplineLoop;
            gpu.flags = (int)flags;
            gpu.mode = (int)ordering;
            gpu.parameters = new Vector4(start, end, offset, 0f);
            gpu.positionAmount = positionOffset;

            Vector3 up = UpVector;
            Vector3 radians = rotationOffset * Mathf.Deg2Rad;
            gpu.special0 = new Vector4(up.x, up.y, up.z, (float)aimAxis);
            gpu.special1 = new Vector4(radians.x, radians.y, radians.z, 0f);
        }

        internal override void AppendGpuData(ref GpuFractureEffector gpu, List<Vector4> data)
        {
            BuildPathCache();
            gpu.seed = data.Count;
            gpu.parameters.w = cachedSamples.Count;
            for (int i = 0; i < cachedSamples.Count; i++)
            {
                Vector3 p = cachedSamples[i];
                data.Add(new Vector4(p.x, p.y, p.z, 0f));
            }
        }

        void BuildPathCache()
        {
            int hash = PathHash();
            if (hash == cachedPathHash) return;
            cachedPathHash = hash;
            cachedSamples.Clear();
            if (spline == null || spline.points == null || spline.points.Count < 2) return;

            int count = Mathf.Clamp(pathSamples, 8, 256);
            if (cachedSamples.Capacity < count) cachedSamples.Capacity = count;
            if (!constantSpacing)
            {
                for (int i = 0; i < count; i++)
                    cachedSamples.Add(spline.EvaluateWorld(i / (float)(count - 1)));
                return;
            }

            int denseCount = Mathf.Clamp(Mathf.Max(count * 3, spline.SampleCount * 2), 64, 1024);
            if (denseSamples == null || denseSamples.Length < denseCount)
            {
                denseSamples = new Vector3[Mathf.NextPowerOfTwo(denseCount)];
                denseLengths = new float[denseSamples.Length];
            }
            denseSamples[0] = spline.EvaluateWorld(0f);
            denseLengths[0] = 0f;
            for (int i = 1; i < denseCount; i++)
            {
                denseSamples[i] = spline.EvaluateWorld(i / (float)(denseCount - 1));
                denseLengths[i] = denseLengths[i - 1] + Vector3.Distance(denseSamples[i - 1], denseSamples[i]);
            }

            float total = denseLengths[denseCount - 1];
            if (total <= 1e-6f)
            {
                for (int i = 0; i < count; i++) cachedSamples.Add(denseSamples[0]);
                return;
            }

            int cursor = 1;
            for (int i = 0; i < count; i++)
            {
                float targetDistance = total * i / (count - 1f);
                while (cursor < denseCount - 1 && denseLengths[cursor] < targetDistance) cursor++;
                int previous = Mathf.Max(0, cursor - 1);
                float segment = denseLengths[cursor] - denseLengths[previous];
                float t = segment > 1e-6f ? (targetDistance - denseLengths[previous]) / segment : 0f;
                cachedSamples.Add(Vector3.LerpUnclamped(denseSamples[previous], denseSamples[cursor], t));
            }
        }

        int PathHash()
        {
            unchecked
            {
                int h = spline != null ? spline.BrazeInstanceId() : 0;
                h = h * 31 + (spline != null ? spline.version : 0);
                h = h * 31 + (spline != null ? spline.transform.localToWorldMatrix.GetHashCode() : 0);
                h = h * 31 + pathSamples;
                h = h * 31 + (constantSpacing ? 1 : 0);
                return h;
            }
        }

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + PathHash();
                h = h * 31 + (int)ordering;
                h = h * 31 + start.GetHashCode();
                h = h * 31 + end.GetHashCode();
                h = h * 31 + offset.GetHashCode();
                h = h * 31 + (loop ? 1 : 0);
                h = h * 31 + (positionOnSpline ? 1 : 0);
                h = h * 31 + positionOffset.GetHashCode();
                h = h * 31 + (alignRotation ? 1 : 0);
                h = h * 31 + (int)aimAxis;
                h = h * 31 + (int)upMode;
                h = h * 31 + customUp.GetHashCode();
                h = h * 31 + rotationOffset.GetHashCode();
                return h;
            }
        }

        protected override Color GizmoColor => new(0.75f, 0.4f, 1f, 0.8f);

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            affectPosition = true;
            affectRotation = true;
            affectScale = false;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            start = Mathf.Clamp01(start);
            end = Mathf.Clamp01(end);
            pathSamples = Mathf.Clamp(pathSamples, 8, 256);
            if (customUp.sqrMagnitude <= 1e-8f) customUp = Vector3.up;
            cachedPathHash = int.MinValue;
        }
    }
}
