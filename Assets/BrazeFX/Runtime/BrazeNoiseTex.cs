using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Shared baked noise textures for the fire/smoke fragment paths (trail + ribbon flame shading).
    /// The shaders' procedural base noise (sin-hash lattices — up to ~1000 sin() per pixel on the ribbon's
    /// domain-warped fire, at full ribbon overdraw) tanked FPS the moment Flame Amount went above 0. The BASE
    /// octave is baked once into small tileable textures and the fbm/warp structure stays in the shader, so the
    /// look survives while each noise evaluation becomes one cache-hot LOD-0 fetch. Bound as shader globals
    /// (_BrazeNoise2D / _BrazeNoise3D); textures repeat, so any scrolled/warped coordinate works like the
    /// procedural version did.
    /// </summary>
    public static class BrazeNoiseTex
    {
        const int Size2D = 256, Cells2D = 16;   // 16 px per lattice cell — smooth under bilinear
        const int Size3D = 64, Cells3D = 8;     // 8 px per cell (matches the shaders' 1/8 sample scale)

        static Texture2D noise2D;
        static Texture3D noise3D;

        /// <summary>Bake (once) and bind the global noise textures. Idempotent — call before rendering.</summary>
        public static void Bind()
        {
            if (noise2D == null)
            {
                noise2D = Bake2D();
                Shader.SetGlobalTexture("_BrazeNoise2D", noise2D);
            }
            if (noise3D == null)
            {
                noise3D = Bake3D();
                Shader.SetGlobalTexture("_BrazeNoise3D", noise3D);
            }
        }

        // Deterministic integer hash → [0,1). Fixed seed: the bake must be identical across sessions/machines.
        static float Hash01(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) ^ (uint)(y * 668265263) ^ (uint)(z * 974634041) ^ 0x9E3779B9u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return (h & 0xFFFFFFu) / 16777216f;
            }
        }

        static float Fade(float t) => t * t * (3f - 2f * t);   // matches the shader's smoothstep lattice fade

        // Tileable 2D GRADIENT noise (period Cells2D), remapped to use the full [0,1] range — the trail shader
        // restores the signed range (*2-1). Gradient (not value) noise to keep the simplex-like character the
        // trail's fire fbm was tuned around.
        static Texture2D Bake2D()
        {
            const int S = Size2D, P = Cells2D;
            var gx = new float[P * P];
            var gy = new float[P * P];
            for (int y = 0; y < P; y++)
                for (int x = 0; x < P; x++)
                {
                    float ang = Hash01(x, y, 17) * Mathf.PI * 2f;
                    gx[y * P + x] = Mathf.Cos(ang);
                    gy[y * P + x] = Mathf.Sin(ang);
                }

            var vals = new float[S * S];
            float mn = float.MaxValue, mx = float.MinValue;
            float cellsPerTexel = P / (float)S;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float fx = x * cellsPerTexel, fy = y * cellsPerTexel;
                    int ix = (int)fx, iy = (int)fy;
                    float tx = fx - ix, ty = fy - iy;
                    int ix1 = (ix + 1) % P, iy1 = (iy + 1) % P;

                    float d00 = gx[iy * P + ix] * tx + gy[iy * P + ix] * ty;
                    float d10 = gx[iy * P + ix1] * (tx - 1f) + gy[iy * P + ix1] * ty;
                    float d01 = gx[iy1 * P + ix] * tx + gy[iy1 * P + ix] * (ty - 1f);
                    float d11 = gx[iy1 * P + ix1] * (tx - 1f) + gy[iy1 * P + ix1] * (ty - 1f);

                    float u = Fade(tx), v = Fade(ty);
                    float n = Mathf.Lerp(Mathf.Lerp(d00, d10, u), Mathf.Lerp(d01, d11, u), v);
                    vals[y * S + x] = n;
                    if (n < mn) mn = n;
                    if (n > mx) mx = n;
                }

            var px = new byte[S * S];
            float inv = 1f / Mathf.Max(mx - mn, 1e-5f);
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(Mathf.Clamp01((vals[i] - mn) * inv) * 255f);

            var tex = new Texture2D(S, S, TextureFormat.R8, false, true)
            {
                name = "BrazeNoise2D",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixelData(px, 0);
            tex.Apply(false, true);
            return tex;
        }

        // Tileable 3D VALUE noise (period Cells3D) with the smoothstep fade — texel-for-texel the same field the
        // ribbon's n3() computed procedurally, minus the per-pixel 48-sin lattice hash.
        static Texture3D Bake3D()
        {
            const int S = Size3D, P = Cells3D;
            var lattice = new float[P * P * P];
            for (int z = 0; z < P; z++)
                for (int y = 0; y < P; y++)
                    for (int x = 0; x < P; x++)
                        lattice[(z * P + y) * P + x] = Hash01(x, y, z + 101);

            float L(int x, int y, int z) => lattice[((z % P) * P + (y % P)) * P + (x % P)];

            var px = new byte[S * S * S];
            float cellsPerTexel = P / (float)S;
            for (int z = 0; z < S; z++)
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float fx = x * cellsPerTexel, fy = y * cellsPerTexel, fz = z * cellsPerTexel;
                        int ix = (int)fx, iy = (int)fy, iz = (int)fz;
                        float u = Fade(fx - ix), v = Fade(fy - iy), w = Fade(fz - iz);

                        float c00 = Mathf.Lerp(L(ix, iy, iz), L(ix + 1, iy, iz), u);
                        float c10 = Mathf.Lerp(L(ix, iy + 1, iz), L(ix + 1, iy + 1, iz), u);
                        float c01 = Mathf.Lerp(L(ix, iy, iz + 1), L(ix + 1, iy, iz + 1), u);
                        float c11 = Mathf.Lerp(L(ix, iy + 1, iz + 1), L(ix + 1, iy + 1, iz + 1), u);
                        float n = Mathf.Lerp(Mathf.Lerp(c00, c10, v), Mathf.Lerp(c01, c11, v), w);
                        px[(z * S + y) * S + x] = (byte)(Mathf.Clamp01(n) * 255f);
                    }

            var tex = new Texture3D(S, S, S, TextureFormat.R8, false)
            {
                name = "BrazeNoise3D",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixelData(px, 0);
            tex.Apply(false, true);
            return tex;
        }
    }
}
