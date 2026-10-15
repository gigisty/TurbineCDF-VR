using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Shape used to limit a fracture effector in world space.</summary>
    public enum FractureEffectorShape
    {
        // Keep the original values stable so existing scenes and prefabs retain their fields.
        Infinite = 0,
        Sphere = 1,
        Box = 2,
        Capsule = 3,
        Cylinder = 4,
        Cone = 5,
        Torus = 6,
        Plane = 7,
    }
    public enum FractureEffectorTransformSpace { World, Effector }
    public enum FractureEffectorFunction
    {
        // Append-only: these values are serialized into scenes and mirrored by BrazeVoronoi.compute.
        Random = 0,
        Step = 1,
        Target = 2,
        Spline = 3,
        Shader = 4,
        PushApart = 5,
        Delay = 6,
        Plane = 7,
    }
    public enum FractureEffectorAimAxis { PositiveZ, NegativeZ, PositiveY, NegativeY, PositiveX, NegativeX }

    [System.Flags]
    public enum FractureEffectorFlags
    {
        Position = 1 << 0,
        Rotation = 1 << 1,
        Scale = 1 << 2,
        UniformScale = 1 << 3,
        Synchronized = 1 << 4,
        Indexed = 1 << 5,
        ObjectSpace = 1 << 6,
        EffectorSpace = 1 << 7,
        StepReverse = 1 << 8,
        StepLoop = 1 << 9,
        TargetUsePitch = 1 << 10,
        TargetRepel = 1 << 11,
        SplineLoop = 1 << 12,
        ShaderInvert = 1 << 13,
        ShaderAbsolute = 1 << 14,
    }

    /// <summary>GPU representation shared by the fracture effector stack.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuFractureEffector
    {
        public Vector3 position;       public float shape;
        public Vector3 boxHalf;        public float radius;
        public Vector4 rotation;
        public Vector3 positionAmount; public float strength;
        public Vector3 rotationAmount; public float falloffRow;
        public Vector3 scaleAmount;    public int auxOffset;
        public int functionType;       public int mode;
        public int seed;               public int flags;
        public Vector4 parameters;
        public Vector4 special0;
        public Vector4 special1;

        public const int Stride = 160;
    }

    /// <summary>
    /// Base component for direct-transform Voronoi effectors. Effectors do not add velocity or force: the owning
    /// fracture evaluates its ordered effector list and applies the resulting position, rotation, and scale to each
    /// fragment at render time.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public abstract class BrazeFractureEffector : MonoBehaviour, IBrazeAnimatedTarget,
        IBrazeAnimatableFilter, IBrazeAnimatablePresentation
    {
        public static readonly List<BrazeFractureEffector> Active = new();

        public static int FalloffAtlasHash(List<BrazeFractureEffector> effectors, int count)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < count; i++)
                    h = h * 31 + CurveHash(effectors[i].falloff);
                return h;
            }
        }

        public static int FunctionAtlasHash(List<BrazeFractureEffector> effectors, int count)
        {
            unchecked
            {
                int h = 23;
                for (int i = 0; i < count; i++)
                    h = h * 31 + CurveHash(effectors[i].FunctionCurve);
                return h;
            }
        }

        protected internal static int CurveHash(AnimationCurve curve)
        {
            if (curve == null) return 0;
            unchecked
            {
                int h = curve.length;
                h = h * 31 + (int)curve.preWrapMode;
                h = h * 31 + (int)curve.postWrapMode;
                for (int i = 0; i < curve.length; i++)
                {
                    Keyframe key = curve[i];
                    h = h * 31 + key.time.GetHashCode();
                    h = h * 31 + key.value.GetHashCode();
                    h = h * 31 + key.inTangent.GetHashCode();
                    h = h * 31 + key.outTangent.GetHashCode();
                    h = h * 31 + key.inWeight.GetHashCode();
                    h = h * 31 + key.outWeight.GetHashCode();
                    h = h * 31 + (int)key.weightedMode;
                }
                return h;
            }
        }

        [Header("FIELD")]
        [Tooltip("Infinite affects every fragment. Primitive fields affect only fragments inside their region.")]
        public FractureEffectorShape shape = FractureEffectorShape.Sphere;
        [Tooltip("Sphere radius. Also used by Capsule and Cylinder, and as the Cone base radius.")]
        [Min(0f)] public float radius = 3f;
        public Vector3 boxSize = new(4f, 4f, 4f);
        [Tooltip("Total Y-axis height for Capsule, Cylinder, and Cone fields.")]
        [Min(0f)] public float height = 6f;
        [Tooltip("Distance from the Torus centre to the centre of its tube.")]
        [Min(0f)] public float torusMajorRadius = 2f;
        [Tooltip("Radius of the Torus tube.")]
        [Min(0f)] public float torusMinorRadius = 0.5f;
        [Tooltip("Width and depth of the Plane field on its local XZ axes.")]
        public Vector2 planeSize = new(4f, 4f);
        [Tooltip("Y-axis thickness of the Plane field.")]
        [Min(0f)] public float planeThickness = 0.25f;
        [Tooltip("Effect strength from the field centre (left) to its edge (right).")]
        public AnimationCurve falloff = new(new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        [Range(-2f, 2f)] [BrazeAnimatable("EFFECTOR/Strength", -2f, 2f)] public float strength = 1f;
        public bool showGizmo = true;

        [Header("TRANSFORM")]
        public FractureEffectorTransformSpace transformSpace = FractureEffectorTransformSpace.Effector;
        public bool affectPosition = true;
        [Tooltip("Maximum position change on each axis.")]
        public Vector3 positionAmount = Vector3.one;
        public bool affectRotation = true;
        [Tooltip("Maximum Euler rotation in degrees on each axis.")]
        public Vector3 rotationAmount = new(30f, 30f, 30f);
        public bool affectScale = false;
        [Tooltip("Maximum scale deviation from 1 on each axis.")]
        public Vector3 scaleAmount = Vector3.one * 0.25f;
        public bool uniformScale = true;

        protected abstract FractureEffectorFunction FunctionType { get; }
        protected abstract void ConfigureGpu(ref GpuFractureEffector gpu);
        internal virtual void AppendGpuData(ref GpuFractureEffector gpu, List<Vector4> data) { }
        public virtual AnimationCurve FunctionCurve => null;
        public virtual bool Animating => false;
        public virtual bool UsesSharedTransformChannels => true;

        public bool IsAnimatableMemberRelevant(string member)
        {
            string root = MemberRoot(member);
            switch (root)
            {
                case "shape": return true;
                case "radius": return shape == FractureEffectorShape.Sphere || shape == FractureEffectorShape.Capsule ||
                                      shape == FractureEffectorShape.Cylinder || shape == FractureEffectorShape.Cone;
                case "boxSize": return shape == FractureEffectorShape.Box;
                case "height": return shape == FractureEffectorShape.Capsule || shape == FractureEffectorShape.Cylinder ||
                                      shape == FractureEffectorShape.Cone;
                case "torusMajorRadius":
                case "torusMinorRadius": return shape == FractureEffectorShape.Torus;
                case "planeSize":
                case "planeThickness": return shape == FractureEffectorShape.Plane;
                case "strength":
                    return true;
                case "transformSpace":
                case "affectPosition":
                case "affectRotation":
                case "affectScale": return UsesSharedTransformChannels;
                case "positionAmount": return UsesSharedTransformChannels && affectPosition;
                case "rotationAmount": return UsesSharedTransformChannels && affectRotation;
                case "scaleAmount":
                case "uniformScale": return UsesSharedTransformChannels && affectScale;
                default: return IsFunctionAnimatableMemberRelevant(root);
            }
        }

        public string GetAnimatableMemberPath(string member, string fallbackPath)
        {
            string root = MemberRoot(member);
            string path = root switch
            {
                "shape" => "FIELD/Shape",
                "radius" => shape == FractureEffectorShape.Cone ? "FIELD/Base Radius" : "FIELD/Radius",
                "boxSize" => "FIELD/Box Size",
                "height" => "FIELD/Height",
                "torusMajorRadius" => "FIELD/Major Radius",
                "torusMinorRadius" => "FIELD/Tube Radius",
                "planeSize" => "FIELD/Plane Size",
                "planeThickness" => "FIELD/Thickness",
                "strength" => "FIELD/Strength",
                "transformSpace" => "TRANSFORM/Space",
                "affectPosition" => "TRANSFORM/POSITION/Enabled",
                "positionAmount" => "TRANSFORM/POSITION/Amount",
                "affectRotation" => "TRANSFORM/ROTATION/Enabled",
                "rotationAmount" => "TRANSFORM/ROTATION/Amount (degrees)",
                "affectScale" => "TRANSFORM/SCALE/Enabled",
                "scaleAmount" => "TRANSFORM/SCALE/Deviation",
                "uniformScale" => "TRANSFORM/SCALE/Uniform",
                _ => GetFunctionAnimatableMemberPath(root, fallbackPath),
            };
            return AppendChannel(path, member);
        }

        protected virtual bool IsFunctionAnimatableMemberRelevant(string member) => true;
        protected virtual string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => fallbackPath;

        static string MemberRoot(string member)
        {
            int dot = member.IndexOf('.');
            return dot >= 0 ? member.Substring(0, dot) : member;
        }

        static string AppendChannel(string path, string member)
        {
            int dot = member.LastIndexOf('.');
            if (dot < 0 || dot + 2 != member.Length) return path;
            char channel = char.ToUpperInvariant(member[dot + 1]);
            if ("XYZWRGBA".IndexOf(channel) < 0) return path;
            string suffix = "/" + channel;
            return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path : path + suffix;
        }

        public GpuFractureEffector ToGpu(int falloffRow)
        {
            Quaternion q = transform.rotation;
            Vector3 gpuHalf = boxSize * 0.5f;
            float gpuRadius = radius;
            switch (shape)
            {
                case FractureEffectorShape.Capsule:
                case FractureEffectorShape.Cylinder:
                case FractureEffectorShape.Cone:
                    gpuHalf = new Vector3(radius, height * 0.5f, radius);
                    break;
                case FractureEffectorShape.Torus:
                    gpuHalf = new Vector3(torusMinorRadius, 0f, 0f);
                    gpuRadius = torusMajorRadius;
                    break;
                case FractureEffectorShape.Plane:
                    gpuHalf = new Vector3(planeSize.x * 0.5f, planeThickness * 0.5f, planeSize.y * 0.5f);
                    break;
            }

            FractureEffectorFlags flags = 0;
            if (affectPosition) flags |= FractureEffectorFlags.Position;
            if (affectRotation) flags |= FractureEffectorFlags.Rotation;
            if (affectScale) flags |= FractureEffectorFlags.Scale;
            if (uniformScale) flags |= FractureEffectorFlags.UniformScale;
            if (transformSpace == FractureEffectorTransformSpace.Effector) flags |= FractureEffectorFlags.EffectorSpace;

            var gpu = new GpuFractureEffector
            {
                position = transform.position,
                shape = (float)shape,
                boxHalf = gpuHalf,
                radius = gpuRadius,
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                positionAmount = positionAmount,
                strength = strength,
                rotationAmount = rotationAmount * Mathf.Deg2Rad,
                falloffRow = falloffRow,
                scaleAmount = scaleAmount,
                functionType = (int)FunctionType,
                flags = (int)flags,
            };
            ConfigureGpu(ref gpu);
            return gpu;
        }

        /// <summary>Cheap editor-preview signature. New effector types should extend this with their own knobs.</summary>
        public virtual int ContentHash()
        {
            unchecked
            {
                int h = transform.localToWorldMatrix.GetHashCode();
                h = h * 31 + (int)shape;
                h = h * 31 + radius.GetHashCode();
                h = h * 31 + boxSize.GetHashCode();
                h = h * 31 + height.GetHashCode();
                h = h * 31 + torusMajorRadius.GetHashCode();
                h = h * 31 + torusMinorRadius.GetHashCode();
                h = h * 31 + planeSize.GetHashCode();
                h = h * 31 + planeThickness.GetHashCode();
                h = h * 31 + strength.GetHashCode();
                h = h * 31 + (int)transformSpace;
                h = h * 31 + (affectPosition ? 1 : 0);
                h = h * 31 + positionAmount.GetHashCode();
                h = h * 31 + (affectRotation ? 1 : 0);
                h = h * 31 + rotationAmount.GetHashCode();
                h = h * 31 + (affectScale ? 1 : 0);
                h = h * 31 + scaleAmount.GetHashCode();
                h = h * 31 + (uniformScale ? 1 : 0);
                h = h * 31 + (isActiveAndEnabled ? 1 : 0);
                h = h * 31 + CurveHash(falloff);
                return h;
            }
        }

        protected static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352d;
            x ^= x >> 15; x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        protected virtual Color GizmoColor => new(1f, 0.45f, 0.14f, 0.75f);

        protected virtual void OnEnable() => BrazeRuntimeRegistry.AddUnique(Active, this);
        protected virtual void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        protected virtual void OnValidate()
        {
            radius = Mathf.Max(0f, radius);
            boxSize = new Vector3(Mathf.Max(0f, boxSize.x), Mathf.Max(0f, boxSize.y), Mathf.Max(0f, boxSize.z));
            height = Mathf.Max(0f, height);
            torusMajorRadius = Mathf.Max(0f, torusMajorRadius);
            torusMinorRadius = Mathf.Max(0f, torusMinorRadius);
            planeSize = new Vector2(Mathf.Max(0f, planeSize.x), Mathf.Max(0f, planeSize.y));
            planeThickness = Mathf.Max(0f, planeThickness);
            scaleAmount = new Vector3(Mathf.Max(0f, scaleAmount.x), Mathf.Max(0f, scaleAmount.y), Mathf.Max(0f, scaleAmount.z));
        }

        public void OnBrazeAnimated() => OnValidate();

        protected virtual void Reset()
        {
            radius = 3f;
            boxSize = Vector3.one * 4f;
            height = 6f;
            torusMajorRadius = 2f;
            torusMinorRadius = 0.5f;
            planeSize = Vector2.one * 4f;
            planeThickness = 0.25f;
        }

        protected virtual void OnDrawGizmos()
        {
            if (!showGizmo || shape == FractureEffectorShape.Infinite) return;
            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            switch (shape)
            {
                case FractureEffectorShape.Sphere:
                    Gizmos.DrawWireSphere(Vector3.zero, radius);
                    break;
                case FractureEffectorShape.Box:
                    Gizmos.DrawWireCube(Vector3.zero, boxSize);
                    break;
                case FractureEffectorShape.Capsule:
                    DrawWireCapsule(radius, height);
                    break;
                case FractureEffectorShape.Cylinder:
                    DrawWireCylinder(radius, height);
                    break;
                case FractureEffectorShape.Cone:
                    DrawWireCone(radius, height);
                    break;
                case FractureEffectorShape.Torus:
                    DrawWireTorus(torusMajorRadius, torusMinorRadius);
                    break;
                case FractureEffectorShape.Plane:
                    Gizmos.DrawWireCube(Vector3.zero, new Vector3(planeSize.x, planeThickness, planeSize.y));
                    break;
            }
            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        static void DrawWireCircle(Vector3 centre, Vector3 axisA, Vector3 axisB, float circleRadius, int segments = 32)
        {
            if (circleRadius <= 0f) return;
            Vector3 previous = centre + axisA * circleRadius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector3 current = centre + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * circleRadius;
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        static void DrawWireCapsule(float capsuleRadius, float capsuleHeight)
        {
            float halfHeight = Mathf.Max(capsuleHeight * 0.5f, capsuleRadius);
            float segment = Mathf.Max(halfHeight - capsuleRadius, 0f);
            Vector3 top = Vector3.up * segment;
            Vector3 bottom = Vector3.down * segment;
            Gizmos.DrawWireSphere(top, capsuleRadius);
            if (segment > 0f) Gizmos.DrawWireSphere(bottom, capsuleRadius);
            Gizmos.DrawLine(top + Vector3.right * capsuleRadius, bottom + Vector3.right * capsuleRadius);
            Gizmos.DrawLine(top + Vector3.left * capsuleRadius, bottom + Vector3.left * capsuleRadius);
            Gizmos.DrawLine(top + Vector3.forward * capsuleRadius, bottom + Vector3.forward * capsuleRadius);
            Gizmos.DrawLine(top + Vector3.back * capsuleRadius, bottom + Vector3.back * capsuleRadius);
        }

        static void DrawWireCylinder(float cylinderRadius, float cylinderHeight)
        {
            float halfHeight = cylinderHeight * 0.5f;
            Vector3 top = Vector3.up * halfHeight;
            Vector3 bottom = Vector3.down * halfHeight;
            DrawWireCircle(top, Vector3.right, Vector3.forward, cylinderRadius);
            DrawWireCircle(bottom, Vector3.right, Vector3.forward, cylinderRadius);
            Gizmos.DrawLine(top + Vector3.right * cylinderRadius, bottom + Vector3.right * cylinderRadius);
            Gizmos.DrawLine(top + Vector3.left * cylinderRadius, bottom + Vector3.left * cylinderRadius);
            Gizmos.DrawLine(top + Vector3.forward * cylinderRadius, bottom + Vector3.forward * cylinderRadius);
            Gizmos.DrawLine(top + Vector3.back * cylinderRadius, bottom + Vector3.back * cylinderRadius);
        }

        static void DrawWireCone(float coneRadius, float coneHeight)
        {
            float halfHeight = coneHeight * 0.5f;
            Vector3 apex = Vector3.up * halfHeight;
            Vector3 baseCentre = Vector3.down * halfHeight;
            DrawWireCircle(baseCentre, Vector3.right, Vector3.forward, coneRadius);
            Gizmos.DrawLine(apex, baseCentre + Vector3.right * coneRadius);
            Gizmos.DrawLine(apex, baseCentre + Vector3.left * coneRadius);
            Gizmos.DrawLine(apex, baseCentre + Vector3.forward * coneRadius);
            Gizmos.DrawLine(apex, baseCentre + Vector3.back * coneRadius);
        }

        static void DrawWireTorus(float majorRadius, float minorRadius)
        {
            DrawWireCircle(Vector3.zero, Vector3.right, Vector3.forward, majorRadius + minorRadius);
            DrawWireCircle(Vector3.zero, Vector3.right, Vector3.forward, Mathf.Max(majorRadius - minorRadius, 0f));
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI * 0.25f;
                Vector3 radial = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                DrawWireCircle(radial * majorRadius, radial, Vector3.up, minorRadius, 20);
            }
        }

    }

}
