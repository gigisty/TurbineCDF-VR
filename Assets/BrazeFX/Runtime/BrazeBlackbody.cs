using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Fire colour ramp baker. Adapted from the publisher-owned three-js-volume `TransferFunction.ts`: a real blackbody locus
    /// (Planck spectral radiance integrated against the CIE-1931 colour-matching functions → XYZ →
    /// linear sRGB, normalized to unit peak) for physical fire colour, or an artist <see cref="Gradient"/>.
    /// Brightness comes from the emission amount × gain in the shader; this is purely chromaticity.
    /// </summary>
    public static class BrazeBlackbody
    {
        public const float TMin = 1000f;   // K — dull red embers
        public const float TMax = 6500f;   // K — white-hot (≤6500 keeps the tip white, not blue)

        /// <summary>Fill a width×1 ramp texture: blackbody locus, or the supplied gradient.</summary>
        public static void Bake(Texture2D tex, bool blackbody, Gradient grad)
        {
            int n = tex.width;
            var px = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float v = n == 1 ? 0f : i / (float)(n - 1);
                px[i] = blackbody ? PlanckRGB(Mathf.Lerp(TMin, TMax, v))
                                  : (grad != null ? grad.Evaluate(v) : Color.white);
            }
            tex.SetPixels(px);
            tex.Apply(false);
        }

        /// <summary>Blackbody hue at normalized temperature v in [0,1] (mapped TMin..TMax), linear sRGB,
        /// unit-peak. Used by the pre-integrated fire transfer LUT (see <see cref="BrazeTransferFunction"/>).</summary>
        public static Color Planck01(float v) => PlanckRGB(Mathf.Lerp(TMin, TMax, v));

        // Planck spectral radiance at T (K) → normalized linear-sRGB chromaticity.
        static Color PlanckRGB(float T)
        {
            const double c2 = 1.4388e7; // hc/k in nm·K
            double X = 0, Y = 0, Z = 0;
            for (int l = 380; l <= 780; l += 5)
            {
                double radiance = 1.0 / (System.Math.Pow(l, 5) * (System.Math.Exp(c2 / (l * T)) - 1.0));
                X += radiance * CieX(l);
                Y += radiance * CieY(l);
                Z += radiance * CieZ(l);
            }
            // CIE XYZ → linear sRGB (D65)
            double r = 3.2406 * X - 1.5372 * Y - 0.4986 * Z;
            double g = -0.9689 * X + 1.8758 * Y + 0.0415 * Z;
            double b = 0.0557 * X - 0.2040 * Y + 1.0570 * Z;
            r = System.Math.Max(0, r); g = System.Math.Max(0, g); b = System.Math.Max(0, b);
            double peak = System.Math.Max(r, System.Math.Max(g, System.Math.Max(b, 1e-30)));
            return new Color((float)(r / peak), (float)(g / peak), (float)(b / peak), 1f);
        }

        // Wyman, Sloan & Shirley, "Simple Analytic Approximations to the CIE XYZ Color Matching Functions",
        // JCGT 2(2), 2013. Gaussian fits to the CIE-1931 colour-matching functions (λ in nm).
        static double CieX(double l)
        {
            double a = (l - 442.0) * (l < 442.0 ? 0.0624 : 0.0374);
            double b = (l - 599.8) * (l < 599.8 ? 0.0264 : 0.0323);
            double c = (l - 501.1) * (l < 501.1 ? 0.0490 : 0.0382);
            return 0.362 * System.Math.Exp(-0.5 * a * a) + 1.056 * System.Math.Exp(-0.5 * b * b) - 0.065 * System.Math.Exp(-0.5 * c * c);
        }
        static double CieY(double l)
        {
            double a = (l - 568.8) * (l < 568.8 ? 0.0213 : 0.0247);
            double b = (l - 530.9) * (l < 530.9 ? 0.0613 : 0.0322);
            return 0.821 * System.Math.Exp(-0.5 * a * a) + 0.286 * System.Math.Exp(-0.5 * b * b);
        }
        static double CieZ(double l)
        {
            double a = (l - 437.0) * (l < 437.0 ? 0.0845 : 0.0278);
            double b = (l - 459.0) * (l < 459.0 ? 0.0385 : 0.0725);
            return 1.217 * System.Math.Exp(-0.5 * a * a) + 0.681 * System.Math.Exp(-0.5 * b * b);
        }

        /// <summary>Publisher-owned prototype's default fire gradient (deep red → orange → yellow → white-hot).</summary>
        public static Gradient DefaultFireGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.0f, 0.0f, 0.0f), 0.0f),
                    new GradientColorKey(new Color(0.55f, 0.05f, 0.0f), 0.2f),
                    new GradientColorKey(new Color(1.0f, 0.35f, 0.04f), 0.45f),
                    new GradientColorKey(new Color(1.0f, 0.72f, 0.2f), 0.7f),
                    new GradientColorKey(new Color(1.0f, 0.95f, 0.82f), 1.0f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }
    }
}
