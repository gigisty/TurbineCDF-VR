using UnityEngine;

namespace BrazeFX
{
    /// <summary>Directly orients fracture fragments toward a target without adding force or angular velocity.</summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Target Effector", 46)]
    public sealed class BrazeTargetEffector : BrazeFractureEffector
    {
        public enum UpMode { World, Effector, Target, Custom }

        [Header("TARGET")]
        [Tooltip("Transform the fragments aim toward. When empty, they aim toward this effector's position.")]
        public Transform targetObject;
        [Tooltip("The fragment-local axis that points at the target.")]
        public FractureEffectorAimAxis aimAxis = FractureEffectorAimAxis.PositiveZ;
        [Tooltip("Allow the aim direction to tilt toward targets above or below the fragment.")]
        public bool usePitch = true;
        [Tooltip("Reference direction used to keep fragment roll stable.")]
        public UpMode upMode = UpMode.World;
        [Tooltip("World-space up direction used when Up Mode is Custom.")]
        public Vector3 customUp = Vector3.up;

        [Header("REPEL")]
        [Tooltip("Push fragments out of a minimum radius around the target. This is a direct position offset, not a force.")]
        public bool repel = false;
        [Min(0f)] public float repelDistance = 1f;
        [Min(0f)] [BrazeAnimatable("TARGET/Repel Strength", 0f, 4f)] public float repelStrength = 1f;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Target;

        protected override bool IsFunctionAnimatableMemberRelevant(string member) => member switch
        {
            "customUp" => upMode == UpMode.Custom,
            "repelDistance" or "repelStrength" => repel,
            _ => true,
        };

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "targetObject" => "EFFECTOR/AIM/Target",
            "aimAxis" => "EFFECTOR/AIM/Aim Axis",
            "usePitch" => "EFFECTOR/AIM/Use Pitch",
            "upMode" => "EFFECTOR/AIM/Up Vector",
            "customUp" => "EFFECTOR/AIM/Custom Up",
            "repel" => "EFFECTOR/REPEL/Enabled",
            "repelDistance" => "EFFECTOR/REPEL/Minimum Distance",
            "repelStrength" => "EFFECTOR/REPEL/Strength",
            _ => fallbackPath,
        };

        Vector3 TargetPosition => targetObject != null ? targetObject.position : transform.position;

        Vector3 UpVector
        {
            get
            {
                Vector3 up = upMode switch
                {
                    UpMode.Effector => transform.up,
                    UpMode.Target => targetObject != null ? targetObject.up : transform.up,
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
            flags |= FractureEffectorFlags.Rotation;
            if (usePitch) flags |= FractureEffectorFlags.TargetUsePitch;
            if (repel) flags |= FractureEffectorFlags.TargetRepel;
            gpu.flags = (int)flags;
            gpu.mode = (int)aimAxis;

            Vector3 target = TargetPosition;
            Vector3 up = UpVector;
            gpu.special0 = new Vector4(target.x, target.y, target.z, Mathf.Max(0f, repelDistance));
            gpu.special1 = new Vector4(up.x, up.y, up.z, Mathf.Max(0f, repelStrength));
        }

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + (targetObject != null ? targetObject.BrazeInstanceId() : 0);
                h = h * 31 + (targetObject != null ? targetObject.localToWorldMatrix.GetHashCode() : 0);
                h = h * 31 + (int)aimAxis;
                h = h * 31 + (usePitch ? 1 : 0);
                h = h * 31 + (int)upMode;
                h = h * 31 + customUp.GetHashCode();
                h = h * 31 + (repel ? 1 : 0);
                h = h * 31 + repelDistance.GetHashCode();
                h = h * 31 + repelStrength.GetHashCode();
                return h;
            }
        }

        protected override Color GizmoColor => new(1f, 0.35f, 0.3f, 0.8f);

        protected override void OnDrawGizmos()
        {
            base.OnDrawGizmos();
            if (!showGizmo) return;
            Gizmos.color = GizmoColor;
            Vector3 target = TargetPosition;
            Gizmos.DrawLine(transform.position, target);
            Gizmos.DrawWireSphere(target, Mathf.Max(0.04f, repel ? repelDistance : 0.08f));
        }

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            affectPosition = false;
            affectRotation = true;
            affectScale = false;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            repelDistance = Mathf.Max(0f, repelDistance);
            repelStrength = Mathf.Max(0f, repelStrength);
            if (customUp.sqrMagnitude <= 1e-8f) customUp = Vector3.up;
        }
    }
}
