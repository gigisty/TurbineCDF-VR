using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Generates shared transform values from an authored procedural 3D noise field.</summary>
    [AddComponentMenu("BrazeFX/Motion Design/Effectors/Braze Shader Effector", 48)]
    public sealed class BrazeShaderEffector : BrazeFractureEffector
    {
        public enum NoiseType { Soft, Fractal, Turbulence, Ridged }
        public enum NoiseSpace { Effector, Object, World }

        [Header("NOISE")]
        public NoiseType noiseType = NoiseType.Fractal;
        public int seed = 12345;

        [Header("COORDINATES")]
        [Tooltip("Effector attaches the noise to this component. Object attaches it to the fracture. World fixes it globally.")]
        public NoiseSpace noiseSpace = NoiseSpace.Effector;
        [Min(0.001f)] [Tooltip("Overall feature size. Larger values produce broader regions.")]
        public float globalScale = 1f;
        [Tooltip("Per-axis multiplier for the global feature size.")]
        public Vector3 relativeScale = Vector3.one;
        [Tooltip("Moves the noise through its coordinate space without moving the effector field.")]
        public Vector3 coordinateOffset;

        [Header("DETAIL")]
        [Range(1, 8)] [Tooltip("Number of layered frequencies. Higher values add finer structure.")]
        public int octaves = 4;
        [Range(0f, 1f)] [Tooltip("Strength retained by each finer octave.")]
        public float roughness = 0.5f;
        [Range(1.01f, 4f)] [Tooltip("Frequency multiplier between consecutive octaves.")]
        public float lacunarity = 2f;

        [Header("ANIMATION")]
        [Tooltip("Makes the noise billow internally. Zero keeps its structure static.")]
        public float evolutionSpeed;
        [Tooltip("Direction in which the complete noise pattern travels.")]
        public Vector3 movement = Vector3.up;
        [Tooltip("Travel speed in coordinate-space units per second.")]
        public float movementSpeed;

        [Header("OUTPUT")]
        [Range(0f, 1f)] [Tooltip("Values at or below this level become zero.")]
        public float lowClip;
        [Range(0f, 1f)] [Tooltip("Values at or above this level become one. It may be lower than Low Clip to reverse the range.")]
        public float highClip = 1f;
        [Range(-1f, 1f)] public float brightness;
        [Range(-1f, 1f)] public float contrast;
        [Tooltip("Folds values around the midpoint, creating mirrored peaks.")]
        public bool absolute;
        public bool invert;
        public AnimationCurve remap = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        protected override FractureEffectorFunction FunctionType => FractureEffectorFunction.Shader;
        public override AnimationCurve FunctionCurve => remap;
        public override bool Animating => Mathf.Abs(evolutionSpeed) > 1e-6f || Mathf.Abs(movementSpeed) > 1e-6f;

        protected override bool IsFunctionAnimatableMemberRelevant(string member) => member switch
        {
            "octaves" or "roughness" or "lacunarity" => noiseType != NoiseType.Soft,
            _ => true,
        };

        protected override string GetFunctionAnimatableMemberPath(string member, string fallbackPath) => member switch
        {
            "noiseType" => "EFFECTOR/NOISE/Type",
            "seed" => "EFFECTOR/NOISE/Seed",
            "noiseSpace" => "EFFECTOR/NOISE/COORDINATES/Space",
            "globalScale" => "EFFECTOR/NOISE/COORDINATES/Global Scale",
            "relativeScale" => "EFFECTOR/NOISE/COORDINATES/Relative Scale",
            "coordinateOffset" => "EFFECTOR/NOISE/COORDINATES/Offset",
            "octaves" => "EFFECTOR/NOISE/DETAIL/Octaves",
            "roughness" => "EFFECTOR/NOISE/DETAIL/Roughness",
            "lacunarity" => "EFFECTOR/NOISE/DETAIL/Lacunarity",
            "evolutionSpeed" => "EFFECTOR/NOISE/ANIMATION/Evolution Speed",
            "movement" => "EFFECTOR/NOISE/ANIMATION/Movement",
            "movementSpeed" => "EFFECTOR/NOISE/ANIMATION/Speed",
            "lowClip" => "EFFECTOR/NOISE/OUTPUT/Low Clip",
            "highClip" => "EFFECTOR/NOISE/OUTPUT/High Clip",
            "brightness" => "EFFECTOR/NOISE/OUTPUT/Brightness",
            "contrast" => "EFFECTOR/NOISE/OUTPUT/Contrast",
            "absolute" => "EFFECTOR/NOISE/OUTPUT/Absolute",
            "invert" => "EFFECTOR/NOISE/OUTPUT/Invert",
            "remap" => "EFFECTOR/NOISE/OUTPUT/Curve",
            _ => fallbackPath,
        };

        protected override void ConfigureGpu(ref GpuFractureEffector gpu)
        {
            var flags = (FractureEffectorFlags)gpu.flags;
            if (invert) flags |= FractureEffectorFlags.ShaderInvert;
            if (absolute) flags |= FractureEffectorFlags.ShaderAbsolute;
            gpu.flags = (int)flags;
            gpu.mode = (int)noiseType | ((int)noiseSpace << 3);
            gpu.seed = seed;
            gpu.parameters = new Vector4(Mathf.Max(globalScale, 0.001f), Mathf.Clamp(octaves, 1, 8),
                                         Mathf.Clamp01(roughness), Mathf.Max(lacunarity, 1.01f));
            gpu.special0 = new Vector4(SafeScale(relativeScale.x), SafeScale(relativeScale.y),
                                      SafeScale(relativeScale.z), evolutionSpeed);
            gpu.special1 = new Vector4(movement.x, movement.y, movement.z, movementSpeed);
        }

        internal override void AppendGpuData(ref GpuFractureEffector gpu, List<Vector4> data)
        {
            gpu.auxOffset = data.Count;
            data.Add(new Vector4(lowClip, highClip, brightness, contrast));
            data.Add(new Vector4(coordinateOffset.x, coordinateOffset.y, coordinateOffset.z, 0f));
        }

        /// <summary>Final shaped value used by the editor's live XY slice. Kept on the runtime source so preview and
        /// fragment evaluation cannot drift into two different noise implementations.</summary>
        internal float SamplePreview(Vector2 uv, float time)
        {
            Vector3 coordinate = new((uv.x - 0.5f) * 4f, (uv.y - 0.5f) * 4f, 0f);
            return EvaluateNoise(coordinate, time);
        }

        float EvaluateNoise(Vector3 p, float time)
        {
            Vector3 featureSize = new(Mathf.Max(globalScale * SafeScale(relativeScale.x), 0.001f),
                                      Mathf.Max(globalScale * SafeScale(relativeScale.y), 0.001f),
                                      Mathf.Max(globalScale * SafeScale(relativeScale.z), 0.001f));
            Vector3 direction = movement.sqrMagnitude > 1e-8f ? movement.normalized : Vector3.zero;
            p += coordinateOffset + direction * (movementSpeed * time);
            p = new Vector3(p.x / featureSize.x, p.y / featureSize.y, p.z / featureSize.z);

            float value = FractalNoise(p, time * evolutionSpeed);
            if (absolute) value = Mathf.Abs(value * 2f - 1f);
            value = ApplyClip(value, lowClip, highClip);
            value = Mathf.Clamp01((value - 0.5f) * (1f + contrast) + 0.5f + brightness);
            if (invert) value = 1f - value;
            return remap != null ? remap.Evaluate(value) : value;
        }

        float FractalNoise(Vector3 p, float evolution)
        {
            int layerCount = noiseType == NoiseType.Soft ? 1 : Mathf.Clamp(octaves, 1, 8);
            float sum = 0f, normalization = 0f, amplitude = 1f, frequency = 1f;
            for (int octave = 0; octave < layerCount; octave++)
            {
                float n = ValueNoise(p * frequency, evolution, seed + octave * 1013);
                n = noiseType switch
                {
                    NoiseType.Turbulence => Mathf.Abs(n * 2f - 1f),
                    NoiseType.Ridged => Mathf.Pow(1f - Mathf.Abs(n * 2f - 1f), 2f),
                    _ => n,
                };
                sum += n * amplitude;
                normalization += amplitude;
                frequency *= Mathf.Max(lacunarity, 1.01f);
                amplitude *= Mathf.Clamp01(roughness);
            }
            return normalization > 1e-6f ? sum / normalization : 0.5f;
        }

        static float ValueNoise(Vector3 p, float evolution, int noiseSeed)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            int t = Mathf.FloorToInt(evolution);
            Vector3 f = new(p.x - x, p.y - y, p.z - z);
            f = new Vector3(Smooth(f.x), Smooth(f.y), Smooth(f.z));
            float ft = Smooth(evolution - t);
            return Mathf.Lerp(ValueNoiseSlice(x, y, z, t, noiseSeed, f),
                              ValueNoiseSlice(x, y, z, t + 1, noiseSeed, f), ft);
        }

        static float ValueNoiseSlice(int x, int y, int z, int t, int noiseSeed, Vector3 f)
        {
            float c000 = Lattice(x, y, z, t, noiseSeed), c100 = Lattice(x + 1, y, z, t, noiseSeed);
            float c010 = Lattice(x, y + 1, z, t, noiseSeed), c110 = Lattice(x + 1, y + 1, z, t, noiseSeed);
            float c001 = Lattice(x, y, z + 1, t, noiseSeed), c101 = Lattice(x + 1, y, z + 1, t, noiseSeed);
            float c011 = Lattice(x, y + 1, z + 1, t, noiseSeed), c111 = Lattice(x + 1, y + 1, z + 1, t, noiseSeed);
            float a = Mathf.Lerp(Mathf.Lerp(c000, c100, f.x), Mathf.Lerp(c010, c110, f.x), f.y);
            float b = Mathf.Lerp(Mathf.Lerp(c001, c101, f.x), Mathf.Lerp(c011, c111, f.x), f.y);
            return Mathf.Lerp(a, b, f.z);
        }

        static float Lattice(int x, int y, int z, int t, int noiseSeed)
        {
            uint h = Hash(unchecked((uint)x) * 0x8da6b343u ^ unchecked((uint)y) * 0xd8163841u
                          ^ unchecked((uint)z) * 0xcb1ab31fu ^ unchecked((uint)t) * 0x9e3779b9u
                          ^ unchecked((uint)noiseSeed) * 0x85ebca6bu);
            return (h & 0x00ffffffu) / 16777216f;
        }

        static float ApplyClip(float value, float low, float high)
        {
            float range = high - low;
            if (Mathf.Abs(range) <= 1e-6f) return value >= high ? 1f : 0f;
            return Mathf.Clamp01((value - low) / range);
        }

        static float SafeScale(float value) => Mathf.Max(Mathf.Abs(value), 0.001f);
        static float Smooth(float x) => x * x * (3f - 2f * x);

        public override int ContentHash()
        {
            unchecked
            {
                int h = base.ContentHash();
                h = h * 31 + (int)noiseType;
                h = h * 31 + seed;
                h = h * 31 + (int)noiseSpace;
                h = h * 31 + globalScale.GetHashCode();
                h = h * 31 + relativeScale.GetHashCode();
                h = h * 31 + coordinateOffset.GetHashCode();
                h = h * 31 + octaves;
                h = h * 31 + roughness.GetHashCode();
                h = h * 31 + lacunarity.GetHashCode();
                h = h * 31 + evolutionSpeed.GetHashCode();
                h = h * 31 + movement.GetHashCode();
                h = h * 31 + movementSpeed.GetHashCode();
                h = h * 31 + lowClip.GetHashCode();
                h = h * 31 + highClip.GetHashCode();
                h = h * 31 + brightness.GetHashCode();
                h = h * 31 + contrast.GetHashCode();
                h = h * 31 + (absolute ? 1 : 0);
                h = h * 31 + (invert ? 1 : 0);
                h = h * 31 + CurveHash(remap);
                return h;
            }
        }

        protected override Color GizmoColor => new(0.3f, 0.9f, 0.65f, 0.8f);

        protected override void Reset()
        {
            base.Reset();
            shape = FractureEffectorShape.Infinite;
            affectPosition = true;
            positionAmount = Vector3.up;
            affectRotation = false;
            affectScale = false;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            globalScale = Mathf.Max(globalScale, 0.001f);
            relativeScale = new Vector3(SafeScale(relativeScale.x), SafeScale(relativeScale.y), SafeScale(relativeScale.z));
            octaves = Mathf.Clamp(octaves, 1, 8);
            roughness = Mathf.Clamp01(roughness);
            lacunarity = Mathf.Clamp(lacunarity, 1.01f, 4f);
            lowClip = Mathf.Clamp01(lowClip);
            highClip = Mathf.Clamp01(highClip);
            brightness = Mathf.Clamp(brightness, -1f, 1f);
            contrast = Mathf.Clamp(contrast, -1f, 1f);
        }
    }
}
