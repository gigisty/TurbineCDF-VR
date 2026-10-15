using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Unity-side adaptation of the publisher-owned three-js-particles `core/curve.ts` MinMaxCurve.
    /// An over-lifetime value that can be a constant, a random band between two constants, or a single
    /// authored curve. Baked to a 256×1 RGFloat LUT (R = min, G = max) and sampled on the GPU with a
    /// per-particle random to pick within the band.
    ///
    /// Authoring uses Unity's native AnimationCurve for now; the bespoke IMGUI curve editor is M7.
    /// </summary>
    public enum BrazeCurveMode { Constant, RandomBetweenConstants, Curve }

    [System.Serializable]
    public sealed class BrazeMinMaxCurve
    {
        public BrazeCurveMode mode = BrazeCurveMode.Constant;
        public float scalar = 1f;
        public float constantMin = 1f;
        public float constantMax = 1f;
        public AnimationCurve curveMin = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        public BrazeMinMaxCurve() { }

        public static BrazeMinMaxCurve Constant(float v) =>
            new() { mode = BrazeCurveMode.Constant, constantMin = v, constantMax = v };

        public static BrazeMinMaxCurve Curve(AnimationCurve c) =>
            new() { mode = BrazeCurveMode.Curve, curveMin = c };

        public static BrazeMinMaxCurve Range(float min, float max) =>
            new() { mode = BrazeCurveMode.RandomBetweenConstants, constantMin = min, constantMax = max };

        /// <summary>The (min, max) band at time t — used to drive the GPU with a single float2.</summary>
        public Vector2 SampleRange(float t) => new(Sample(t, 0f), Sample(t, 1f));

        /// <summary>Sample at normalized time t (0..1); r (0..1) picks within a random band.</summary>
        public float Sample(float t, float r)
        {
            switch (mode)
            {
                case BrazeCurveMode.Constant:
                    return constantMin * scalar;
                case BrazeCurveMode.RandomBetweenConstants:
                    return Mathf.Lerp(constantMin, constantMax, r) * scalar;
                default: // Curve
                    return Eval(curveMin, t) * scalar;
            }
        }

        static float Eval(AnimationCurve c, float t) => (c == null || c.length == 0) ? 0f : c.Evaluate(t);
    }

    /// <summary>
    /// An integer value that's either a constant or a random range between two constants — the int
    /// analogue of the Const/Range modes (used by bursts: count / start frame / loop delay).
    /// </summary>
    [System.Serializable]
    public struct BrazeIntRange
    {
        public bool range;   // false = Const, true = Range (random between min..max)
        public int min;
        public int max;

        public BrazeIntRange(int v) { range = false; min = v; max = v; }
        public BrazeIntRange(int lo, int hi) { range = true; min = lo; max = hi; }

        /// <summary>A fresh sample: the constant, or an inclusive random between min..max.</summary>
        public int Sample() => range ? UnityEngine.Random.Range(Mathf.Min(min, max), Mathf.Max(min, max) + 1) : min;
    }

    /// <summary>Bakes BrazeMinMaxCurve into GPU curve LUTs (256×1 RGFloat, R=min G=max, point/clamp/linear).</summary>
    public static class BrazeLut
    {
        public const int Res = 256;

        public static Texture2D CreateCurveTex(string name)
        {
            var t = new Texture2D(Res, 1, TextureFormat.RGFloat, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            return t;
        }

        static readonly Color[] Scratch = new Color[Res];

        public static void BakeCurve(Texture2D tex, BrazeMinMaxCurve c)
        {
            for (int i = 0; i < Res; i++)
            {
                float t = i / (Res - 1f);
                Scratch[i] = new Color(c.Sample(t, 0f), c.Sample(t, 1f), 0f, 0f);
            }
            tex.SetPixels(Scratch);
            tex.Apply(false, false);
        }

        // Color-over-lifetime gradient LUT — 256×1 RGBA32, LINEAR so the authored colour samples back
        // unchanged (no sRGB shift). Point/clamp; the GPU does an explicit two-tap lerp like the curves.
        public static Texture2D CreateGradientTex(string name)
        {
            var t = new Texture2D(Res, 1, TextureFormat.RGBA32, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            return t;
        }

        public static void BakeGradient(Texture2D tex, Gradient g)
        {
            for (int i = 0; i < Res; i++)
            {
                float t = i / (Res - 1f);
                Scratch[i] = g != null ? g.Evaluate(t) : Color.white;
            }
            tex.SetPixels(Scratch);
            tex.Apply(false, false);
        }
    }
}
