// BrazeFX — shared noise. Value noise + divergence-free curl noise.
// Adapted from the publisher-owned three-js-volume prototype's tsl-noise.ts curlNoise (curl of a vector
// potential built from noise => guaranteed divergence-free turbulence).
#ifndef BRAZE_NOISE_INCLUDED
#define BRAZE_NOISE_INCLUDED

// 3-octave summed-sine value field (three-js-volume `tsl-noise.ts` / `wave`). Smooth, bounded and
// APERIODIC — unlike an integer-lattice value-noise hash it has no repeating cells,
// so the curl turbulence built from it never tiles into a visible grid pattern.
// `x,y,z` are pre-scaled positions; `ph` is the time phase (animates the field in place).
float BrazeWave(float x, float y, float z, float ph)
{
    float value = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    [unroll]
    for (int o = 0; o < 3; o++)
    {
        float term = (sin(x * freq + ph)
                    + sin((y * 1.31 + z * 0.73) * freq + ph * 1.37)
                    + sin((z * 1.17 + x * 0.61) * freq - ph * 0.83)) / 3.0;
        value += term * amp;
        amp  *= 0.5;
        freq *= 2.03;
    }
    return value;
}

// Vector potential — three decorrelated wave channels (three-js-volume `tsl-noise.ts` / `potential`).
float3 BrazePotential(float3 p, float ph)
{
    return float3(
        BrazeWave(p.x + 11.1, p.y,        p.z,        ph),
        BrazeWave(p.x,        p.y + 23.7, p.z,        ph + 2.1),
        BrazeWave(p.x,        p.y,        p.z + 37.3, ph + 4.2));
}

// Divergence-free turbulence = curl of the potential (finite differences, step `e`).
// `p` is the pre-scaled sample position; `ph` is the time phase. Matches the internal prototype's `curlNoise`.
float3 BrazeCurlNoise(float3 p, float e, float ph)
{
    float3 dx = float3(e, 0, 0), dy = float3(0, e, 0), dz = float3(0, 0, e);

    float3 px1 = BrazePotential(p + dx, ph), px0 = BrazePotential(p - dx, ph);
    float3 py1 = BrazePotential(p + dy, ph), py0 = BrazePotential(p - dy, ph);
    float3 pz1 = BrazePotential(p + dz, ph), pz0 = BrazePotential(p - dz, ph);

    float x = (py1.z - py0.z) - (pz1.y - pz0.y);
    float y = (pz1.x - pz0.x) - (px1.z - px0.z);
    float z = (px1.y - px0.y) - (py1.x - py0.x);
    return float3(x, y, z) / (2.0 * e);
}

// --- 2-octave lite curl noise — for the fine-grid advect, where the finest octave is barely
//     visible in the rendered density (the small-scale structure comes from advection, not the
//     velocity noise). Drops ~1/3 of the sin() work in the heaviest WT kernel. ---
float BrazeWave2(float x, float y, float z, float ph)
{
    float value = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    [unroll]
    for (int o = 0; o < 2; o++)
    {
        float term = (sin(x * freq + ph)
                    + sin((y * 1.31 + z * 0.73) * freq + ph * 1.37)
                    + sin((z * 1.17 + x * 0.61) * freq - ph * 0.83)) / 3.0;
        value += term * amp;
        amp  *= 0.5;
        freq *= 2.03;
    }
    return value;
}

float3 BrazePotential2(float3 p, float ph)
{
    return float3(
        BrazeWave2(p.x + 11.1, p.y,        p.z,        ph),
        BrazeWave2(p.x,        p.y + 23.7, p.z,        ph + 2.1),
        BrazeWave2(p.x,        p.y,        p.z + 37.3, ph + 4.2));
}

float3 BrazeCurlNoise2(float3 p, float e, float ph)
{
    float3 dx = float3(e, 0, 0), dy = float3(0, e, 0), dz = float3(0, 0, e);
    float3 px1 = BrazePotential2(p + dx, ph), px0 = BrazePotential2(p - dx, ph);
    float3 py1 = BrazePotential2(p + dy, ph), py0 = BrazePotential2(p - dy, ph);
    float3 pz1 = BrazePotential2(p + dz, ph), pz0 = BrazePotential2(p - dz, ph);
    float x = (py1.z - py0.z) - (pz1.y - pz0.y);
    float y = (pz1.x - pz0.x) - (px1.z - px0.z);
    float z = (px1.y - px0.y) - (py1.x - py0.x);
    return float3(x, y, z) / (2.0 * e);
}

// --- Cellular / FBM helpers — for procedural CLOUD generation (static volume generator). ---

// 3D hash → a random point per integer cell.
float3 BrazeHash33(float3 p)
{
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453123);
}

// Worley/cellular F1 distance, bounded ~[0,1]: ~0 at a cell's feature point, →1 toward the borders.
// `1 - BrazeWorley` gives the classic puffy "billow" lobes; raw BrazeWorley erodes edges (cauliflower bite).
float BrazeWorley(float3 p)
{
    float3 ip = floor(p), fp = frac(p);
    float d = 1.0;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float3 g = float3(x, y, z);
        float3 o = BrazeHash33(ip + g);
        float3 r = g + o - fp;
        d = min(d, dot(r, r));
    }
    return saturate(sqrt(d));
}

// Fractal value noise built on the aperiodic BrazeWave (4 octaves), normalized to ~[0,1].
float BrazeFbm(float3 p, float ph)
{
    float sum = 0.0, amp = 0.5, norm = 0.0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        sum  += amp * (BrazeWave(p.x, p.y, p.z, ph) / 0.875 * 0.5 + 0.5);   // BrazeWave ∈ ~[-0.875,0.875] → [0,1]
        norm += amp;
        p = p * 2.02 + 19.3; amp *= 0.5;
    }
    return saturate(sum / max(norm, 1e-4));
}

// Fractal Worley (billow), normalized ~[0,1] — soft cauliflower lobes for cloud erosion detail.
float BrazeFbmWorley(float3 p)
{
    float sum = 0.0, amp = 0.5, norm = 0.0;
    [unroll] for (int i = 0; i < 3; i++)
    {
        sum  += amp * (1.0 - BrazeWorley(p));   // billow lobes
        norm += amp;
        p = p * 2.03 + 7.7; amp *= 0.5;
    }
    return saturate(sum / max(norm, 1e-4));
}

#endif
