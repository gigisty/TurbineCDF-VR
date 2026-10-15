using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// C4D-inspired random transform source for the shared fracture-effector core. The base class owns fields,
    /// transform channels and stack composition; this class configures the GPU sample generator.
    /// </summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Random Effector", 44)]
    public sealed class BrazeRandomEffector : BrazeFractureEffector
    {
        public enum RandomMode { Random, Gaussian, Noise, Turbulence }
        public enum NoiseSpace { Global, Object }

        [Header("RANDOM")]
        public RandomMode randomMode = RandomMode.Random;
        public int seed = 12345;
        [Tooltip("Remap generated values into this range before applying the transform amounts.")]
        public Vector2 outputRange = new(-1f, 1f);
        [Tooltip("Use the same generated vector for Position, Rotation, and Scale.")]
        public bool synchronized = false;
        [Tooltip("Generate independent X/Y/Z values. Off produces one value on all axes.")]
        public bool indexed = true;

        [Header("NOISE")]
        [Tooltip("Global fixes noise in world space. Object keeps the pattern attached to the fractured object.")]
        public NoiseSpace noiseSpace = NoiseSpace.Object;
        [Tooltip("World/object-space size of noise features. Larger values create broader coherent regions.")]
        [Min(0.001f)] public float noiseSize = 1f;
        [Tooltip("How quickly Noise/Turbulence evolves. Zero produces a static pattern.")]
        public float animationSpeed = 0f;

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Random;
        public override bool Animating => (randomMode == RandomMode.Noise || randomMode == RandomMode.Turbulence)
                                          && Mathf.Abs(animationSpeed) > 1e-6f;

        protected override bool IsFunctionAnimatableMemberRelevant(string member)
        {
            bool noise = randomMode == RandomMode.Noise || randomMode == RandomMode.Turbulence;
            return member switch
            {
                "noiseSpace" or "noiseSize" or "animationSpeed" => noise,
                _ => true,
            };
        }

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "randomMode" => "EFFECTOR/RANDOMIZATION/Mode",
            "seed" => "EFFECTOR/RANDOMIZATION/Seed",
            "outputRange" => "EFFECTOR/RANDOMIZATION/Range",
            "synchronized" => "EFFECTOR/RANDOMIZATION/Synchronized",
            "indexed" => "EFFECTOR/RANDOMIZATION/Indexed XYZ",
            "noiseSpace" => "EFFECTOR/NOISE/Space",
            "noiseSize" => "EFFECTOR/NOISE/Feature Size",
            "animationSpeed" => "EFFECTOR/NOISE/Animation Speed",
            _ => fallbackPath,
        };

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            gpu.mode = (int)randomMode;
            gpu.seed = seed;
            var flags = (FractureEffectorFlags)gpu.flags;
            if (synchronized) flags |= FractureEffectorFlags.Synchronized;
            if (indexed) flags |= FractureEffectorFlags.Indexed;
            if (noiseSpace == NoiseSpace.Object) flags |= FractureEffectorFlags.ObjectSpace;
            gpu.flags = (int)flags;
            gpu.parameters = new Vector4(outputRange.x, outputRange.y, Mathf.Max(noiseSize, 0.001f), animationSpeed);
        }

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + (int)randomMode;
                h = h * 31 + seed;
                h = h * 31 + outputRange.GetHashCode();
                h = h * 31 + (synchronized ? 1 : 0);
                h = h * 31 + (indexed ? 1 : 0);
                h = h * 31 + (int)noiseSpace;
                h = h * 31 + noiseSize.GetHashCode();
                h = h * 31 + animationSpeed.GetHashCode();
                return h;
            }
        }

        protected override Color GizmoColor => new(0.95f, 0.3f, 0.75f, 0.8f);

        protected override void OnValidate()
        {
            base.OnValidate();
            noiseSize = Mathf.Max(noiseSize, 0.001f);
        }
    }
}
