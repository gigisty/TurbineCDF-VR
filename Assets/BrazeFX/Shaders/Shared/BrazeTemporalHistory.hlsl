#ifndef BRAZE_TEMPORAL_HISTORY_INCLUDED
#define BRAZE_TEMPORAL_HISTORY_INCLUDED

#if defined(BRAZE_ENABLE_COMPACT_HISTORY)

    // 0 = linear RGBAHalf history, 1 = log2-companded RGBA8 history. The log curve holds a constant ~4% relative
    // step across the whole premultiplied HDR range (2^-8 .. 2^7). The previous sqrt-Reinhard curve concentrated
    // its codes on dark smoke and widened to 10-30% steps in bright long-life fire; re-quantized fresh tiles then
    // landed on different codes than their stale interleave neighbours — a tile-age checkerboard.
    float _BrazeCompactHistory;
    float _BrazeHistoryDitherFrame;

#define BRAZE_HISTORY_LOG2_MIN   -8.0   // 2^-8: below one LDR code of premultiplied radiance -> exact black
#define BRAZE_HISTORY_LOG2_RANGE 15.0   // ceiling 2^7 = 128, matching the old curve's ~126 top

// Interleaved gradient noise walked by the golden ratio per frame: white-enough per pixel AND per frame for
// stochastic rounding. The interleave phase cannot serve here — a tile is always re-encoded at the same phase,
// which would freeze the dither pattern it sees.
float BrazeHistoryDitherNoise(float2 pixel)
{
    pixel += _BrazeHistoryDitherFrame * 5.588238;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float4 BrazeEncodeTemporalHistory(float4 linearValue, float2 pixel)
{
    linearValue.a = saturate(linearValue.a);
    if (_BrazeCompactHistory < 0.5)
        return linearValue;

    float3 radiance = max(linearValue.rgb, 0.0);
    float3 e = (log2(max(radiance, exp2(BRAZE_HISTORY_LOG2_MIN))) - BRAZE_HISTORY_LOG2_MIN)
             / BRAZE_HISTORY_LOG2_RANGE;
    // Code 0 is reserved for exact black so empty pixels cannot acquire a floor glow on decode.
    float3 encoded = lerp(1.0 / 255.0, 254.0 / 255.0, saturate(e));
    encoded = radiance < exp2(BRAZE_HISTORY_LOG2_MIN) ? 0.0 : encoded;

    // Stochastic rounding onto the UNorm8 grid. Slow fades move by fractions of a code per resolve; plain
    // round-to-nearest cancels that motion and the fade stalls, then drops a whole code at once — per tile-age
    // class, i.e. blocks in transparent/dissipating regions. A +/- half-code dither makes the stored expectation
    // exact so sub-code changes propagate, while values already ON the grid (stale pass-through tiles) cannot be
    // displaced by less than half a code and stay bit-stable.
    float dither = (BrazeHistoryDitherNoise(pixel) - 0.5) / 255.0;
    return saturate(float4(encoded, linearValue.a) + dither);
}

float4 BrazeDecodeTemporalHistory(float4 storedValue)
{
    storedValue.a = saturate(storedValue.a);
    if (_BrazeCompactHistory < 0.5)
        return storedValue;

    float3 e = saturate((storedValue.rgb - 1.0 / 255.0) * (255.0 / 253.0));
    float3 radiance = exp2(e * BRAZE_HISTORY_LOG2_RANGE + BRAZE_HISTORY_LOG2_MIN);
    radiance = storedValue.rgb < 0.5 / 255.0 ? 0.0 : radiance;
    return float4(radiance, storedValue.a);
}

#else

    float4 BrazeEncodeTemporalHistory(float4 linearValue, float2 pixel) { return linearValue; }
    float4 BrazeDecodeTemporalHistory(float4 storedValue) { return storedValue; }

#endif

#endif
