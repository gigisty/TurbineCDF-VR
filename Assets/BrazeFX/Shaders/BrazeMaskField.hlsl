#ifndef BRAZE_MASK_FIELD_INCLUDED
#define BRAZE_MASK_FIELD_INCLUDED

// BrazeFX surface-mask FIELD — the single source of truth for the 0..1 membership, shared by the preview shader
// (evaluated per pixel), the particle gate compute, and the pyro mesh gate compute (evaluated per rest sample).
// Keep this as the canonical implementation so preview, particles and pyro agree without CPU mask sampling.
// Set these uniforms from C# via BrazeSurfaceMask.BindField(material) / BindField(computeShader, kernel).

#define BRAZE_MAX_SEEDS 16
float4 _Seeds[BRAZE_MAX_SEEDS];   // xyz = seed centre in EVAL space (world or Braze mask space); w unused
int   _SeedCount;
int   _ProjectionMode;  // 0 = planar 2D projector, 1 = volume 3D
int   _ProjectionPlane; // 0 = XY, 1 = XZ, 2 = YZ
float _Front, _Soft, _Invert;                    // region radius / edge feather / invert membership
float _Organic, _OrganicScale, _OrganicTime;     // organic edge (fbm); _OrganicTime = organicSpeed * time (boil)
float _AnisoAmount; float3 _AnisoAxis;           // anisotropy / stretch
float _RingAmount, _RingSpacing, _RingThickness; // rings / ripple
float _ErodeAmount, _ErodeScale;                 // erosion / dissolve holes
float _Contrast, _Bias;                          // contrast / bias remap (identity at 1, 0)

float BrazeMaskHash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float BrazeMaskVNoise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = BrazeMaskHash13(i + float3(0,0,0)), n100 = BrazeMaskHash13(i + float3(1,0,0));
    float n010 = BrazeMaskHash13(i + float3(0,1,0)), n110 = BrazeMaskHash13(i + float3(1,1,0));
    float n001 = BrazeMaskHash13(i + float3(0,0,1)), n101 = BrazeMaskHash13(i + float3(1,0,1));
    float n011 = BrazeMaskHash13(i + float3(0,1,1)), n111 = BrazeMaskHash13(i + float3(1,1,1));
    float nx00 = lerp(n000, n100, f.x), nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x), nx11 = lerp(n011, n111, f.x);
    return lerp(lerp(nx00, nx10, f.y), lerp(nx01, nx11, f.y), f.z);
}
float BrazeMaskFbm(float3 p)
{
    float a = 0.5, s = 0.0, norm = 0.0;
    [unroll] for (int o = 0; o < 3; o++) { s += a * BrazeMaskVNoise(p); norm += a; p *= 2.0; a *= 0.5; }
    return s / norm;
}

float3 BrazeMaskProjectDelta(float3 v)
{
    float3 result = v;
    if (_ProjectionMode == 0)
    {
        if (_ProjectionPlane == 0) result = float3(v.x, v.y, 0.0);
        else if (_ProjectionPlane == 2) result = float3(0.0, v.y, v.z);
        else result = float3(v.x, 0.0, v.z);
    }
    return result;
}

float3 BrazeMaskProjectPoint(float3 p)
{
    float3 result = p;
    if (_ProjectionMode == 0)
    {
        if (_ProjectionPlane == 0) result = float3(p.x, p.y, 0.0);
        else if (_ProjectionPlane == 2) result = float3(0.0, p.y, p.z);
        else result = float3(p.x, 0.0, p.z);
    }
    return result;
}

float3 BrazeMaskProjectAxis(float3 axis)
{
    float3 a = BrazeMaskProjectDelta(axis);
    float len2 = dot(a, a);
    float3 result = float3(1.0, 0.0, 0.0);
    if (len2 > 1e-12) result = a * rsqrt(len2);
    else if (_ProjectionPlane == 2) result = float3(0.0, 1.0, 0.0);
    return result;
}

// 0..1 membership of a point in the field's EVAL space. The fbm is the expensive part, so clear outside/inside
// cases skip the noise when contrast is identity and no interior filter can carve the result.
float MaskFieldWeight(float3 epos)
{
    float d = 1e9;
    float3 anisoAxis = BrazeMaskProjectAxis(_AnisoAxis);
    [loop] for (int s = 0; s < _SeedCount; s++)
    {
        float3 dl = BrazeMaskProjectDelta(epos - _Seeds[s].xyz);
        if (_AnisoAmount > 1e-4)
        {
            float al = dot(dl, anisoAxis);
            dl = (dl - al * anisoAxis) + anisoAxis * (al / (1.0 + _AnisoAmount));
        }
        d = min(d, length(dl));
    }

    float soft = max(_Soft, 1e-3);
    float wobble = (_Organic > 1e-4) ? abs(_Organic * _Front) : 0.0;
    bool contrastId = (abs(_Contrast - 1.0) < 1e-4) && (abs(_Bias) < 1e-6);
    float result = 0.0;
    if (contrastId && d - wobble > _Front + soft)
    {
        result = (_Invert > 0.5) ? 1.0 : 0.0;
    }
    else if (contrastId && _RingAmount < 1e-4 && _ErodeAmount < 1e-4 && d + wobble < _Front - soft)
    {
        result = (_Invert > 0.5) ? 0.0 : 1.0;
    }
    else
    {
        float3 noisePos = BrazeMaskProjectPoint(epos);
        if (_Organic > 1e-4)
            d += (BrazeMaskFbm(noisePos * _OrganicScale + _OrganicTime) * 2.0 - 1.0) * _Organic * _Front;
        d = max(d, 0.0);

        float coverage = 1.0 - smoothstep(_Front - soft, _Front + soft, d);
        if (_RingAmount > 1e-4)
        {
            float rv = 0.5 + 0.5 * cos((d / max(_RingSpacing, 1e-3)) * 6.2831853);
            float ringBand = smoothstep(1.0 - _RingThickness, 1.0, rv);
            coverage *= lerp(1.0, ringBand, _RingAmount);
        }

        float weight = coverage;
        if (_ErodeAmount > 1e-4)
            weight *= smoothstep(_ErodeAmount, _ErodeAmount + 0.15, BrazeMaskFbm(noisePos * _ErodeScale));

        weight = saturate((weight + _Bias - 0.5) * _Contrast + 0.5);
        result = (_Invert > 0.5) ? (1.0 - weight) : weight;
    }
    return result;
}

#endif // BRAZE_MASK_FIELD_INCLUDED
