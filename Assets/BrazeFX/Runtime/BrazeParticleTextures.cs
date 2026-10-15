using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    public enum BrazeBuiltinTex { Circle, Soft, Flame, Smoke, Star, Square }

    /// <summary>Procedurally generated, cached particle sprites — so the package ships no image
    /// assets. Circle/Soft for embers, Flame/Smoke for fire, Star/Square for sparkles/debris.</summary>
    public static class BrazeParticleTextures
    {
        static readonly Dictionary<BrazeBuiltinTex, Texture2D> cache = new();
        const int Size = 128;

        public static Texture2D Get(BrazeBuiltinTex kind)
        {
            if (cache.TryGetValue(kind, out var t) && t != null) return t;
            t = Generate(kind);
            cache[kind] = t;
            return t;
        }

        static float Hash(float x, float y)
        {
            float n = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return n - Mathf.Floor(n);
        }

        static float ValueNoise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
            float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        static float Fbm(float x, float y)
        {
            float s = 0f, amp = 0.5f, f = 1f;
            for (int o = 0; o < 4; o++) { s += ValueNoise(x * f, y * f) * amp; f *= 2f; amp *= 0.5f; }
            return s;
        }

        static Texture2D Generate(BrazeBuiltinTex kind)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false)
            {
                name = "BrazeParticle_" + kind,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var px = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float u = (x + 0.5f) / Size * 2f - 1f;
                    float v = (y + 0.5f) / Size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    px[y * Size + x] = Sample(kind, u, v, r, x, y);
                }
            }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        static Color Sample(BrazeBuiltinTex kind, float u, float v, float r, int x, int y)
        {
            switch (kind)
            {
                case BrazeBuiltinTex.Circle:
                {
                    float a = Mathf.Clamp01(1f - Mathf.SmoothStep(0.7f, 1f, r));
                    return new Color(1, 1, 1, a);
                }
                case BrazeBuiltinTex.Soft:
                {
                    float a = Mathf.Clamp01(1f - r); a *= a;            // very soft blob
                    return new Color(1, 1, 1, a);
                }
                case BrazeBuiltinTex.Flame:
                {
                    float a = Mathf.Clamp01(1f - r); a *= a;
                    Color hot = new(1f, 0.95f, 0.7f), edge = new(1f, 0.4f, 0.12f);
                    Color rgb = Color.Lerp(hot, edge, Mathf.Clamp01(r * 1.25f));
                    return new Color(rgb.r, rgb.g, rgb.b, a);
                }
                case BrazeBuiltinTex.Smoke:
                {
                    float n = Fbm(u * 2.4f + 13.7f, v * 2.4f - 5.3f);   // puffy internal variation
                    float a = Mathf.Clamp01(1f - r) * (0.55f + 0.7f * n);
                    a = Mathf.Clamp01(a) * Mathf.Clamp01(1f - Mathf.SmoothStep(0.85f, 1f, r));
                    return new Color(1, 1, 1, a);
                }
                case BrazeBuiltinTex.Star:
                {
                    float ang = Mathf.Atan2(v, u);
                    float spikes = Mathf.Abs(Mathf.Cos(ang * 2f));      // 4-point star
                    float edge = Mathf.Lerp(0.25f, 1f, spikes);
                    float a = Mathf.Clamp01(1f - Mathf.SmoothStep(edge * 0.6f, edge, r));
                    return new Color(1, 1, 1, a);
                }
                default: // Square
                {
                    float m = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v));
                    float a = Mathf.Clamp01(1f - Mathf.SmoothStep(0.9f, 1f, m));
                    return new Color(1, 1, 1, a);
                }
            }
        }
    }
}
