// BrazeFX - composite the half-res volume buffer over a copied camera color.
// The volume buffer is premultiplied/transmittance-weighted; the shader performs
// the over operation explicitly so HDR camera targets do not rely on attachment load/blend state.
Shader "BrazeFX/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BrazeComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Set by the volume render feature.
            TEXTURE2D_X(_BlitTexture);
            TEXTURE2D_X(_BrazeSceneColor);
            SAMPLER(sampler_linear_clamp);
            float4 _BlitScaleBias;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 sceneUV : TEXCOORD0;
                float2 volumeUV : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float4 pos = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv  = GetFullScreenTriangleTexCoord(input.vertexID);
                o.positionCS = pos;
                o.sceneUV = uv;
                o.volumeUV = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 scene = SAMPLE_TEXTURE2D_X_LOD(_BrazeSceneColor, sampler_linear_clamp, input.sceneUV, 0);
                float4 volume = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_linear_clamp, input.volumeUV, 0);
                float volumeAlpha = saturate(volume.a);
                float transmittance = 1.0 - volumeAlpha;
                return float4(volume.rgb + scene.rgb * transmittance,
                              volumeAlpha + scene.a * transmittance);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BrazeTemporalResolve"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_BrazeVolumeCurrent);
            TEXTURE2D_X(_BrazeVolumeHistory);
            TEXTURE2D_X(_BrazeVolumeMotion);   // per-pixel screen-space motion vector (cur→prev), from the raymarch
            SAMPLER(sampler_linear_clamp);
            float4 _BlitScaleBias;
            float4 _BrazeVolumeTexelSize;
            float _BrazeTemporalBlend;
            float _BrazeReproject;             // 1 = follow motion vectors; 0 = sample history at the same pixel
            int _BrazeInterleaveMode;
            int _BrazeInterleavePhase;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                o.uv = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }

            float4 SampleCurrent(float2 uv)
            {
                float4 c = SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeCurrent, sampler_linear_clamp, uv, 0);
                c.a = saturate(c.a);
                return c;
            }

            bool InterleavePixelValid(int2 pixel)
            {
                if (_BrazeInterleaveMode == 1) return ((pixel.x + _BrazeInterleavePhase) & 1) == 0;
                if (_BrazeInterleaveMode == 2)
                    return ((pixel.x & 1) + ((pixel.y & 1) << 1)) == _BrazeInterleavePhase;
                return true;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float blend = saturate(_BrazeTemporalBlend);
                float4 current = SampleCurrent(input.uv);
                if (blend <= 0.001) return current;

                // Follow the volume's screen-space motion so history is fetched from where this material WAS last
                // frame — under moving fire / camera orbit the reprojected history matches `current`, so the
                // stability test below keeps (not rejects) it and the raymarch grain averages away. Off → same-pixel.
                float2 mv = _BrazeReproject > 0.5
                    ? SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeMotion, sampler_linear_clamp, input.uv, 0).xy
                    : 0.0;
                float2 huv = input.uv + mv;
                // Reprojected off-screen → active pixels reject history below. Inactive interleaved pixels must
                // retain same-pixel history because their current buffer is deliberately empty.
                float reprojValid = (huv.x >= 0.0 && huv.x <= 1.0 && huv.y >= 0.0 && huv.y <= 1.0) ? 1.0 : 0.0;

                float2 historyUV = reprojValid > 0.5 ? huv : input.uv;
                float4 history = SAMPLE_TEXTURE2D_X_LOD(_BrazeVolumeHistory, sampler_linear_clamp, historyUV, 0);
                history.a = saturate(history.a);

                // Initial frames and cuts disable interleaving in C#, so deliberately missing pixels always have
                // initialized history. Preserve it if motion rejection would otherwise expose the sampling pattern.
                if (!InterleavePixelValid(int2(input.positionCS.xy)))
                    return history;

                float currentMax = max(max(current.r, current.g), current.b);
                float historyMax = max(max(history.r, history.g), history.b);
                float alphaDelta = abs(current.a - history.a);
                float rgbScale = max(max(currentMax, historyMax), 1.0);
                float rgbDelta = length(current.rgb - history.rgb) / rgbScale;
                float hot = saturate(currentMax * 0.12);

                // Static generated clouds converge over most of the screen. Do not pay an eight-neighbour clip on
                // pixels whose reprojected history already matches; changed edges/disocclusions take the guarded path.
                [branch] if (reprojValid > 0.5 && alphaDelta < 0.0125 && rgbDelta < 0.02)
                {
                    float stableW = blend * (1.0 - hot * 0.05);
                    float4 stableResolved = lerp(current, history, stableW);
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
                    float4 s = SampleCurrent(input.uv + crossOffsets[i] * px);
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
                // The bright-region penalty kept fast flames crisp, but it also starved the brightest pixels
                // of temporal averaging — which is exactly where raymarch grain (the dotted/screen-door jitter)
                // is most visible, ON the fire. With motion reprojection the reprojected history now MATCHES the
                // moving fire (so `stable` no longer collapses), letting the glowing core denoise — so the penalty
                // is softened further still (0.12 -> 0.05). Raise if hot flames ghost. reprojValid drops history
                // that reprojected off-screen.
                float w = blend * stable * (1.0 - hot * 0.05) * reprojValid;

                float4 resolved = lerp(current, history, w);
                resolved.a = saturate(resolved.a);
                return resolved;
            }
            ENDHLSL
        }

        // Pass 2 — single-pass composite: the volume buffer is premultiplied (rgb = transmittance-weighted
        // colour, a = coverage = 1-transmittance), so hardware "Blend One OneMinusSrcAlpha" reproduces the
        // explicit over (col + dst*transmittance) directly onto the camera color — no scene copy needed.
        Pass
        {
            Name "BrazeCompositeBlend"
            Blend One OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D_X(_BlitTexture);
            SAMPLER(sampler_linear_clamp);
            float4 _BlitScaleBias;
            float4 _BrazeVolumeTexelSize;   // (1/lowW, 1/lowH, lowW, lowH) of the volume buffer
            float  _BrazeBilateral;         // <0.5 = plain bilinear upsample; ≥0.5 = depth-guided
            float  _BrazeReconstructionSharpness;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 volumeUV : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                o.volumeUV = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }

            float4 SampleVol(float2 uv)
            {
                float4 v = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_linear_clamp, uv, 0);
                v.a = saturate(v.a);
                return v;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Plain bilinear (half-res, or bilateral disabled) — the hardware does the upsample.
                if (_BrazeBilateral < 0.5)
                    return SampleVol(input.volumeUV);   // Blend One OneMinusSrcAlpha → vol.rgb + dst*(1-coverage)

                // Depth-guided upsample: weight the 4 low-res neighbours by how closely the scene depth at
                // each matches this pixel, so the volume's occlusion edge against geometry stays crisp at
                // quarter res instead of bleeding across the silhouette. Degrades to bilinear on flat depth.
                float2 px = _BrazeVolumeTexelSize.xy;
                float2 uv = input.volumeUV;
                float dC = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

                const float2 offs[4] = { float2(-0.5,-0.5), float2(0.5,-0.5), float2(-0.5,0.5), float2(0.5,0.5) };
                float4 sum = 0.0;
                float  wsum = 0.0;
                float4 lo = 1e20;
                float4 hi = -1e20;
                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float2 tuv = uv + offs[i] * px;
                    float dT = LinearEyeDepth(SampleSceneDepth(tuv), _ZBufferParams);
                    float w = 1.0 / (1.0 + abs(dT - dC) * 8.0);   // depth-similarity weight
                    float4 sampleV = SampleVol(tuv);
                    sum  += sampleV * w;
                    wsum += w;
                    lo = min(lo, sampleV);
                    hi = max(hi, sampleV);
                }
                // wsum is a sum of strictly-positive weights, so the fallback is effectively dead — but a
                // ternary holding a texture sample can compile as evaluate-both on some targets. Keep the
                // guard as a real branch so the extra tap never issues.
                [branch] if (wsum > 1e-5)
                {
                    float4 result = sum / wsum;
                    // Contrast-adaptive reconstruction from the same four taps: strengthen only where the low-res
                    // neighbourhood contains structure, and clamp to its extrema to prevent bright/alpha halos.
                    float4 mid = (lo + hi) * 0.5;
                    float range = max(hi.a - lo.a, max(max(hi.r - lo.r, hi.g - lo.g), hi.b - lo.b));
                    float amount = saturate(_BrazeReconstructionSharpness) * saturate(range * 4.0);
                    result = clamp(mid + (result - mid) * (1.0 + amount), lo, hi);
                    result.a = saturate(result.a);
                    return result;
                }
                return SampleVol(uv);
            }
            ENDHLSL
        }

        // Full-resolution fast path: resolve temporal history and blend the same resolved value onto camera color
        // in one draw. Indexed blend keeps history as a replacement target while camera color uses premultiplied over.
        Pass
        {
            Name "BrazeTemporalResolveCompositeBlend"
            Blend 0 Off
            Blend 1 One OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_BrazeVolumeCurrent);
            TEXTURE2D_X(_BrazeVolumeHistory);
            TEXTURE2D_X(_BrazeVolumeMotion);
            SAMPLER(sampler_linear_clamp);
            float4 _BlitScaleBias;
            float4 _BrazeVolumeTexelSize;
            float _BrazeTemporalBlend;
            float _BrazeReproject;
            int _BrazeInterleaveMode;
            int _BrazeInterleavePhase;
            #include "Shared/BrazeTemporalResolve.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                o.uv = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }
            struct Outputs { float4 history : SV_Target0; float4 camera : SV_Target1; };
            Outputs Frag(Varyings input)
            {
                Outputs o;
                float4 resolved = BrazeResolveTemporal(input.uv, input.positionCS.xy);
                o.history = resolved;
                o.camera = resolved;
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
