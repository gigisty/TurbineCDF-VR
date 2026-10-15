// BrazeFX Slice Composite — places the offscreen slice-raymarch result onto the camera color.
// The source holds standard premultiplied coverage (rgb = premultiplied in-scatter, a = opacity), written by
// BrazeSlices.shader into a target cleared to (0,0,0,0), so this is one bilinear tap + hardware blend per
// pixel. Spatial-only rendering uses it when Slice Downsample > 1; temporal rendering also uses it to composite
// resolved history (including Downsample 1, where it is a native one-to-one composite).
Shader "BrazeFX/SliceComposite"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BrazeFX Slice Composite"
            ZTest Always ZWrite Off Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_BrazeSliceSource);
            SAMPLER(sampler_BrazeSliceSource);
            #define BRAZE_ENABLE_COMPACT_HISTORY 1
            #include "Shared/BrazeTemporalHistory.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            // Full-screen triangle. UVs are NOT interpolated from the vertices — the fragment derives its
            // normalized position from SV_Position, which shares orientation with the low-res buffer (both
            // are render targets written through the same rasterizer convention), so no flip bookkeeping.
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                return o;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                return BrazeDecodeTemporalHistory(
                    SAMPLE_TEXTURE2D_X_LOD(_BrazeSliceSource, sampler_BrazeSliceSource, uv, 0));
            }
            ENDHLSL
        }

        Pass
        {
            Name "BrazeFX Slice Flow Temporal Resolve"
            ZTest Always ZWrite Off Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_BrazeVolumeCurrent);
            TEXTURE2D_X(_BrazeVolumeHistory);
            TEXTURE2D_X(_BrazeVolumeMotion);
            SAMPLER(sampler_linear_clamp);
            float4 _BrazeVolumeTexelSize;
            float _BrazeTemporalBlend;
            float _BrazeReproject;
            int _BrazeInterleaveMode;
            int _BrazeInterleavePhase;
            #define BRAZE_COHERENT_INTERLEAVE_TILES 1
            #define BRAZE_ENABLE_COMPACT_HISTORY 1
            #define BRAZE_ALPHA_STABLE_REACTIVE_RESOLVE 1
            // Tile validity is recomputed from the same mode/phase values assigned to the march in this camera record.
            #include "Shared/BrazeTemporalResolve.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                return o;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // This pass renders INTO the slice-history target, whose dimensions may be camera/downsample.
                // _ScaledScreenParams is the CAMERA size and therefore sampled only the lower-left fraction when
                // temporal interleaving was combined with Downsample 2/4. Use the history target's own texel size.
                float2 uv = input.positionCS.xy * _BrazeVolumeTexelSize.xy;
                return BrazeEncodeTemporalHistory(BrazeResolveTemporal(uv, input.positionCS.xy), input.positionCS.xy);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
