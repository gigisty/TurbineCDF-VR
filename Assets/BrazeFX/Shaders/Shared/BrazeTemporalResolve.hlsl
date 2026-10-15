#ifndef BRAZE_TEMPORAL_RESOLVE_INCLUDED
#define BRAZE_TEMPORAL_RESOLVE_INCLUDED

#include "BrazeInterleave.hlsl"
#include "BrazeTemporalHistory.hlsl"

float4 BrazeTemporalSampleCurrent(float2 uv)
{
    float4 c = SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeCurrent, sampler_linear_clamp, uv, 0);
    c.a = saturate(c.a);
    return c;
}

bool BrazeTemporalInterleaveValid(int2 pixel)
{
    #if defined(BRAZE_COHERENT_INTERLEAVE_TILES)
        return BrazeCoherentInterleavePixelValid(
            pixel, _BrazeInterleaveMode, _BrazeInterleavePhase);
    #else
        if (_BrazeInterleaveMode == 1) return ((pixel.x + _BrazeInterleavePhase) & 1) == 0;
        if (_BrazeInterleaveMode == 2)
            return ((pixel.x & 1) + ((pixel.y & 1) << 1)) == _BrazeInterleavePhase;
        return true;
    #endif
}

float4 BrazeResolveTemporal(float2 uv, float2 pixel)
{
    float blend = saturate(_BrazeTemporalBlend);
    float4 current = BrazeTemporalSampleCurrent(uv);
    if (blend <= 0.001) return current;

    float4 motionSample = SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeMotion, sampler_linear_clamp, uv, 0);
    float2 mv = _BrazeReproject > 0.5 ? motionSample.xy : 0.0;
    float2 huv = uv + mv;
    float reprojValid = (huv.x >= 0.0 && huv.x <= 1.0 && huv.y >= 0.0 && huv.y <= 1.0) ? 1.0 : 0.0;
    // An inactive coherent tile has no current color by design. Returning `current` when its motion briefly
    // reprojects off-screen (or becomes non-finite) exposes the 8x8 work schedule as transparent holes. Preserve
    // continuity with same-pixel history in that case; active tiles still use reprojValid below to reject stale
    // history and keep their freshly marched current value.
    float2 historyUV = reprojValid > 0.5 ? huv : uv;
    float4 history = BrazeDecodeTemporalHistory(
        SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeHistory, sampler_linear_clamp, historyUV, 0));

    // Recompute the schedule analytically from mode/phase — identical inputs to the march's own tile early-out,
    // with no extra validity channel or transient bandwidth.
    bool currentScheduled = BrazeTemporalInterleaveValid(int2(pixel));
    // Mirror of the march's fast-motion forfeit: a skipped pixel past the trust radius marched fresh anyway and
    // kept its estimate motion, so this test — same value (uv is the texel centre, the sample is exact), same
    // constant — reproduces its decision. Consuming stale history here would discard the march it already paid for.
    float2 motionPx = mv * _BrazeVolumeTexelSize.zw;
    if (dot(motionPx, motionPx) >
        BRAZE_INTERLEAVE_FAST_REPROJECT_PX * BRAZE_INTERLEAVE_FAST_REPROJECT_PX)
        currentScheduled = true;
    if (!currentScheduled)
        return history;

    float currentMax = max(max(current.r, current.g), current.b);
    float historyMax = max(max(history.r, history.g), history.b);
    float alphaDelta = abs(current.a - history.a);
    float rgbScale = max(max(currentMax, historyMax), 1.0);
    float rgbDelta = length(current.rgb - history.rgb) / rgbScale;
    float hot = saturate(currentMax * 0.12);

    #if defined(BRAZE_ALPHA_STABLE_REACTIVE_RESOLVE)
        // Static-region fast path: history already agrees with current, skip the clamp taps.
        [branch] if (reprojValid > 0.5 && alphaDelta < 0.0125 && rgbDelta < 0.02)
        {
            float4 stableResolved = lerp(current, history, blend * (1.0 - hot * 0.05));
            stableResolved.a = saturate(stableResolved.a);
            return stableResolved;
        }

        // Neighbourhood-clamp history before weighing it — the clamp is what lets a HIGH blend stay honest where
        // the volume changes monotonically (dissipating smoke): unclamped, the resolve carries a history lag
        // amplified by 1/(1-w), staggered per tile age — a checkerboard whose amplitude scales with the blend.
        // Taps are clamped INSIDE this pixel's own work tile, so every sample shares this pixel's march schedule
        // and always holds live data. (The original cross clamp was retired because its taps crossed into
        // intentionally-empty neighbour tiles and dragged premultiplied alpha down — checkerboard transparency.)
        float2 px = _BrazeVolumeTexelSize.xy;
        float2 tileBase = floor(pixel / BRAZE_INTERLEAVE_TILE_SIZE) * (float)BRAZE_INTERLEAVE_TILE_SIZE;
        float2 tileMinUV = (tileBase + 0.5) * px;
        float2 tileMaxUV = (tileBase + ((float)BRAZE_INTERLEAVE_TILE_SIZE - 0.5)) * px;
        float4 minC = current;
        float4 maxC = current;
        const float2 crossOffsets[4] =
        {
            float2(-1, 0), float2(1, 0), float2(0, -1), float2(0, 1)
        };
        [unroll] for (int i = 0; i < 4; i++)
        {
            float2 tapUV = uv + crossOffsets[i] * px;
            [flatten] if (_BrazeInterleaveMode != 0)
                tapUV = clamp(tapUV, tileMinUV, tileMaxUV);
            float4 s = BrazeTemporalSampleCurrent(tapUV);
            minC = min(minC, s);
            maxC = max(maxC, s);
        }
        float4 span = maxC - minC;
        float4 pad = span * 0.25 + float4(0.01, 0.01, 0.01, 0.002);
        history = clamp(history, minC - pad, maxC + pad);

        // Reactive gate on the POST-clamp deltas: ordinary integration/flow variation remains history-stable,
        // while a material appearance/disappearance the clamp let through still rejects history before it ghosts.
        alphaDelta = abs(current.a - history.a);
        historyMax = max(max(history.r, history.g), history.b);
        rgbScale = max(max(currentMax, historyMax), 1.0);
        rgbDelta = length(current.rgb - history.rgb) / rgbScale;
        float alphaReactive = smoothstep(0.06, 0.35, alphaDelta);
        float rgbReactive = smoothstep(0.08, 0.45, rgbDelta);
        float historyConfidence = 1.0 - max(alphaReactive, rgbReactive);
        float w = blend * historyConfidence * (1.0 - hot * 0.05) * reprojValid;
    #else
        // Generated volumes retain their established neighbourhood-clamped resolve. Their current buffer is not
        // made of coherent live-slice work tiles, so neighbour samples have different validity/cost tradeoffs.
        [branch] if (reprojValid > 0.5 && alphaDelta < 0.0125 && rgbDelta < 0.02)
        {
            float4 stableResolved = lerp(current, history, blend * (1.0 - hot * 0.05));
            stableResolved.a = saturate(stableResolved.a);
            return stableResolved;
        }

        float2 px = _BrazeVolumeTexelSize.xy;
        float4 minC = current;
        float4 maxC = current;
        const float2 crossOffsets[4] =
        {
            float2(-1, 0), float2(1, 0), float2(0, -1), float2(0, 1)
        };
        [unroll] for (int i = 0; i < 4; i++)
        {
            float4 s = BrazeTemporalSampleCurrent(uv + crossOffsets[i] * px);
            minC = min(minC, s);
            maxC = max(maxC, s);
        }

        float4 span = maxC - minC;
        float4 pad = span * 0.25 + float4(0.01, 0.01, 0.01, 0.002);
        history = clamp(history, minC - pad, maxC + pad);

        alphaDelta = abs(current.a - history.a);
        historyMax = max(max(history.r, history.g), history.b);
        rgbScale = max(max(currentMax, historyMax), 1.0);
        rgbDelta = length(current.rgb - history.rgb) / rgbScale;
        float stable = saturate(1.0 - max(alphaDelta * 4.0, rgbDelta));
        float w = blend * stable * (1.0 - hot * 0.05) * reprojValid;
    #endif
    float4 resolved = lerp(current, history, w);
    resolved.a = saturate(resolved.a);
    return resolved;
}

#endif
