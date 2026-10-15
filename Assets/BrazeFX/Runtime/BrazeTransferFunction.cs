using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Pre-integrated transfer-function LUT baker adapted from the publisher-owned three-js-volume
    /// `TransferFunction.ts` (PyroTransferFunctions). Maps the raw scalar field (smoke density / fire temperature) to
    /// emitted colour + opacity via editable CURVES + colour gradients, and bakes 2D PRE-INTEGRATION
    /// LUTs so the raymarch can look up the SEGMENT AVERAGE of the curve over the scalar interval
    /// [front, back] of two consecutive ray samples (thickness-independent; the march applies the
    /// real dt afterwards). This removes the along-ray banding that point-sampling a nonlinear ramp
    /// produces — especially for fire.
    ///
    ///   smokePreint[front,back] : rgb = smoke albedo (gradient) average, a = smoke opacity curve average
    ///   firePreint[front,back]  : rgb = fire colour × FIRE_EMISSIVE_GAIN average, a = fire emission curve average
    ///
    /// Texture layout matches that prototype: pixel (x=front, y=back), sampled as tex2D(uv = (front, back)).
    /// </summary>
    public static class BrazeTransferFunction
    {
        public const int PreintN = 128;   // LUT resolution per axis
        public const int PreintM = 24;    // substeps integrated per segment
        // 0..1 picker colour -> HDR emission headroom so the hot tip blooms (from the internal TransferFunction.ts).
        public const float FireEmissiveGain = 4.6f;

        /// <summary>256×1 LUT for a 1-D art curve (e.g. the fire amount response). Sampled by the normalised
        /// amount in [0,1].</summary>
        public static Texture2D CreateCurveLut(string name)
        {
            var t = new Texture2D(256, 1, TextureFormat.RHalf, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return t;
        }

        public static void BakeCurve1D(Texture2D tex, AnimationCurve curve)
        {
            int n = tex.width;
            var px = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float v = i / (float)(n - 1);
                float y = Mathf.Clamp01(EvaluateLinear(curve, v));
                px[i] = new Color(y, y, y, y);
            }
            tex.SetPixels(px);
            tex.Apply(false);
        }

        /// <summary>Bake a 1-D curve using Unity's actual <see cref="AnimationCurve"/> interpolation. This is for
        /// artist-facing renderer profiles where weighted tangents in the curve editor should be visible.</summary>
        public static void BakeCurve1DSmooth(Texture2D tex, AnimationCurve curve)
        {
            int n = tex.width;
            var px = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float v = i / (float)(n - 1);
                float y = Mathf.Clamp01(EvaluateSmooth(curve, v));
                px[i] = new Color(y, y, y, y);
            }
            tex.SetPixels(px);
            tex.Apply(false);
        }

        /// <summary>Bake a 1-D MULTIPLIER curve (values floored at 0 but NOT capped at 1 — RHalf holds >1 fine), for
        /// things like ribbon width where the profile may bulge past the base. Returns the PEAK sampled value so the
        /// caller can size conservative bounds (e.g. a cull radius that still covers the widest point of the curve).</summary>
        public static float BakeCurve1DUnclamped(Texture2D tex, AnimationCurve curve)
        {
            int n = tex.width;
            var px = new Color[n];
            float peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = i / (float)(n - 1);
                float y = Mathf.Max(0f, EvaluateLinear(curve, v));
                peak = Mathf.Max(peak, y);
                px[i] = new Color(y, y, y, y);
            }
            tex.SetPixels(px);
            tex.Apply(false);
            return peak;
        }

        /// <summary>Bake a non-negative multiplier curve using Unity's actual curve interpolation. Values may exceed
        /// 1 for bulges; negative overshoot is clamped away. Returns the sampled peak.</summary>
        public static float BakeCurve1DUnclampedSmooth(Texture2D tex, AnimationCurve curve)
        {
            int n = tex.width;
            var px = new Color[n];
            float peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = i / (float)(n - 1);
                float y = Mathf.Max(0f, EvaluateSmooth(curve, v));
                peak = Mathf.Max(peak, y);
                px[i] = new Color(y, y, y, y);
            }
            tex.SetPixels(px);
            tex.Apply(false);
            return peak;
        }

        /// <summary>256×1 HDR colour ramp LUT baked from a Unity <see cref="Gradient"/> (rgb = colour, a = alpha).
        /// Sampled by a normalised 0..1 coordinate. Reused by the line / trail renderer for its gradient,
        /// colour-by-speed and colour-by-age shading modes.</summary>
        public static Texture2D CreateRampLut(string name)
        {
            var t = new Texture2D(256, 1, TextureFormat.RGBAHalf, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return t;
        }

        public static void BakeRamp(Texture2D tex, Gradient grad)
        {
            int n = tex.width;
            var px = new Color[n];
            if (grad == null)
            {
                for (int i = 0; i < n; i++) px[i] = Color.white;
            }
            else
            {
                for (int i = 0; i < n; i++)
                    px[i] = grad.Evaluate(i / (float)(n - 1));
            }
            tex.SetPixels(px);
            tex.Apply(false);
        }

        public static Texture2D CreateLut(string name)
        {
            var t = new Texture2D(PreintN, PreintN, TextureFormat.RGBAFloat, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return t;
        }

        /// <summary>
        /// Bake both LUTs in place from the editable curves/gradients. fireColorAt(v) returns the fire
        /// hue at temperature v in [0,1] (blackbody Planck locus or an artist gradient — the caller picks).
        /// </summary>
        const int SampleN = 256;   // 1-D pre-sample resolution for the curves/gradients

        /// <summary>Bake only the smoke half of the pre-integrated transfer function. CFD dye uses this path so
        /// changing a diagnostic colour never pays for the unrelated blackbody/fire LUT.</summary>
        public static void BakeSmoke(Texture2D smokePreint, Gradient smokeAlbedo, AnimationCurve smokeOpacity)
        {
            int n = PreintN;
            var smokeRGB = new Color[SampleN];
            var smokeOp = new float[SampleN];
            for (int i = 0; i < SampleN; i++)
            {
                float v = i / (float)(SampleN - 1);
                smokeRGB[i] = smokeAlbedo.Evaluate(v);
                smokeOp[i] = Mathf.Max(0f, EvaluateLinear(smokeOpacity, v));
            }

            var pixels = new Color[n * n];
            float invM = 1f / PreintM;
            for (int bi = 0; bi < n; bi++)
            {
                float back = bi / (float)(n - 1);
                for (int fi = 0; fi < n; fi++)
                {
                    float front = fi / (float)(n - 1);
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    for (int m = 0; m < PreintM; m++)
                    {
                        float t = (m + 0.5f) / PreintM;
                        float v = Mathf.Lerp(front, back, t);
                        Color tint = LerpRGB(smokeRGB, v);
                        r += tint.r; g += tint.g; b += tint.b;
                        a += LerpF(smokeOp, v);
                    }
                    pixels[bi * n + fi] = new Color(r * invM, g * invM, b * invM, a * invM);
                }
            }
            smokePreint.SetPixels(pixels);
            smokePreint.Apply(false);
        }

        public static void Bake(
            Texture2D smokePreint, Texture2D firePreint,
            Gradient smokeAlbedo, AnimationCurve smokeOpacity,
            System.Func<float, Color> fireColorAt, AnimationCurve fireEmission)
        {
            int n = PreintN;

            // Pre-sample the editable curves/gradients into 256-entry lookups ONCE, then the pre-integration
            // loop does cheap lerps (matches the internal prototype's sampleGradient/sampleCurve). Curves are sampled
            // as piecewise-linear keys so Unity's default smooth tangents do not add a hidden opacity knee.
            // Critical for fire:
            // evaluating the blackbody Planck integral per LUT texel × substep was a multi-second CPU stall.
            var smokeRGB = new Color[SampleN];
            var smokeOp  = new float[SampleN];
            var fireRGB  = new Color[SampleN];
            var fireEm   = new float[SampleN];
            for (int i = 0; i < SampleN; i++)
            {
                float v = i / (float)(SampleN - 1);
                smokeRGB[i] = smokeAlbedo.Evaluate(v);
                smokeOp[i]  = Mathf.Max(0f, EvaluateLinear(smokeOpacity, v));
                Color fc = fireColorAt(v);
                fireRGB[i]  = new Color(fc.r * FireEmissiveGain, fc.g * FireEmissiveGain, fc.b * FireEmissiveGain);
                fireEm[i]   = Mathf.Max(0f, EvaluateLinear(fireEmission, v));
            }

            var sPix = new Color[n * n];
            var fPix = new Color[n * n];
            float invM = 1f / PreintM;

            for (int bi = 0; bi < n; bi++)
            {
                float back = bi / (float)(n - 1);
                for (int fi = 0; fi < n; fi++)
                {
                    float front = fi / (float)(n - 1);
                    float sr = 0, sg = 0, sb = 0, sa = 0;   // smoke: tint.rgb + extinction.a
                    float fr = 0, fg = 0, fb = 0, fa = 0;   // fire: colour.rgb + amount.a
                    for (int m = 0; m < PreintM; m++)
                    {
                        float t = (m + 0.5f) / PreintM;
                        float v = Mathf.Lerp(front, back, t);
                        Color tint = LerpRGB(smokeRGB, v);
                        sr += tint.r; sg += tint.g; sb += tint.b;
                        sa += LerpF(smokeOp, v);
                        Color col = LerpRGB(fireRGB, v);
                        fr += col.r; fg += col.g; fb += col.b;
                        fa += LerpF(fireEm, v);
                    }
                    int o = bi * n + fi;
                    sPix[o] = new Color(sr * invM, sg * invM, sb * invM, sa * invM);
                    fPix[o] = new Color(fr * invM, fg * invM, fb * invM, fa * invM);
                }
            }

            smokePreint.SetPixels(sPix); smokePreint.Apply(false);
            firePreint.SetPixels(fPix); firePreint.Apply(false);
        }

        static Color LerpRGB(Color[] a, float v)
        {
            float fi = Mathf.Clamp01(v) * (a.Length - 1);
            int i0 = (int)fi; int i1 = Mathf.Min(a.Length - 1, i0 + 1);
            return Color.Lerp(a[i0], a[i1], fi - i0);
        }
        static float LerpF(float[] a, float v)
        {
            float fi = Mathf.Clamp01(v) * (a.Length - 1);
            int i0 = (int)fi; int i1 = Mathf.Min(a.Length - 1, i0 + 1);
            return Mathf.Lerp(a[i0], a[i1], fi - i0);
        }

        static float EvaluateLinear(AnimationCurve curve, float v)
        {
            if (curve == null || curve.length == 0) return 0f;

            v = Mathf.Clamp01(v);
            int last = curve.length - 1;
            Keyframe first = curve[0];
            if (v <= first.time) return first.value;

            for (int i = 1; i <= last; i++)
            {
                Keyframe b = curve[i];
                if (v > b.time) continue;

                Keyframe a = curve[i - 1];
                float span = b.time - a.time;
                if (span <= 1e-5f) return b.value;
                return Mathf.LerpUnclamped(a.value, b.value, Mathf.Clamp01((v - a.time) / span));
            }

            return curve[last].value;
        }

        static float EvaluateSmooth(AnimationCurve curve, float v)
        {
            if (curve == null || curve.length == 0) return 0f;
            return curve.Evaluate(Mathf.Clamp01(v));
        }

        // ── Default editable curves / gradients ───────────────────────────────────────────────────────

        /// <summary>DEFAULT_SMOKE_POINTS: opacity curve over density (thin -> dense).</summary>
        public static AnimationCurve DefaultSmokeOpacityCurve() =>
            LinearCurve(new Vector2(0f, 0f), new Vector2(0.08f, 0.08f), new Vector2(0.5f, 0.42f), new Vector2(1f, 1f));

        /// <summary>DEFAULT_FIRE_POINTS: emission curve over temperature (ignition -> full).</summary>
        public static AnimationCurve DefaultFireEmissionCurve() =>
            LinearCurve(new Vector2(0f, 0f), new Vector2(0.22f, 0f), new Vector2(0.6f, 0.5f), new Vector2(1f, 1f));

        /// <summary>DEFAULT_SMOKE_GRADIENT: thin light cool grey -> dense darker warm (sootier) grey.</summary>
        public static Gradient DefaultSmokeAlbedoGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.86f, 0.88f, 0.93f), 0.0f),
                    new GradientColorKey(new Color(0.66f, 0.65f, 0.66f), 0.55f),
                    new GradientColorKey(new Color(0.50f, 0.48f, 0.46f), 1.0f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        // Build a piecewise-LINEAR AnimationCurve through the given points (matches the internal prototype's
        // sampleCurve linear lookup — no smoothing overshoot on the transfer curves).
        public static AnimationCurve LinearCurve(params Vector2[] pts)
        {
            var keys = new Keyframe[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                float inT = 0f, outT = 0f;
                if (i > 0) inT = (pts[i].y - pts[i - 1].y) / Mathf.Max(1e-5f, pts[i].x - pts[i - 1].x);
                if (i < pts.Length - 1) outT = (pts[i + 1].y - pts[i].y) / Mathf.Max(1e-5f, pts[i + 1].x - pts[i].x);
                keys[i] = new Keyframe(pts[i].x, pts[i].y, inT, outT);
            }
            return new AnimationCurve(keys);
        }
    }
}
