Shader "BrazeFX/CFD/SurfacePressure"
{
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "SurfacePressure"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            // The overlay reuses the exact source triangles. A raster depth bias keeps it stable at aircraft-scale
            // camera distances even when the authored world-space separation is intentionally tiny.
            Offset -1, -1
            // Mirrored transforms (negative scale) flip triangle winding, which Cull Back would reject wholesale.
            // Shell backfaces z-fail against the opaque body's depth, so double-sided rendering costs nothing.
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_PressureTex);
            SAMPLER(sampler_PressureTex);
            float4x4 _WorldToPressureUVW;
            float _PressureMinimum;
            float _PressureInvRange;
            float4 _SuctionColor;
            float4 _NeutralColor;
            float4 _PressureColor;
            float _Opacity;
            float _SurfaceOffset;
            float _PressureSampleOffset;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 samplePositionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                // Explicit normalize: the inverse-transpose normal transform handles non-uniform scale, but its
                // output length varies with the stretch — the world-space offset below must not scale with it.
                float3 worldNormal = normalize(TransformObjectToWorldNormal(input.normalOS));
                float3 surfaceWorld = TransformObjectToWorld(input.positionOS);
                float3 renderWorld = surfaceWorld + worldNormal * _SurfaceOffset;
                output.samplePositionWS = surfaceWorld + worldNormal * _PressureSampleOffset;
                output.positionCS = TransformWorldToHClip(renderWorld);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Per-PIXEL field sample. The former vertex-stage sample interpolated one scalar across each
                // triangle, so a 24-vertex box showed corner blends instead of the field — overlay resolution
                // followed mesh tessellation rather than the pressure grid.
                float3 uvw = mul(_WorldToPressureUVW, float4(input.samplePositionWS, 1.0)).xyz;
                float inside = all(uvw >= 0.0) && all(uvw <= 1.0) ? 1.0 : 0.0;
                float pressure = inside > 0.5
                    ? SAMPLE_TEXTURE3D_LOD(_PressureTex, sampler_PressureTex, saturate(uvw), 0.0).r
                    : 0.0;
                float pressureU = saturate((pressure - _PressureMinimum) * _PressureInvRange);
                float low = saturate(pressureU * 2.0);
                float high = saturate((pressureU - 0.5) * 2.0);
                float3 color = pressureU < 0.5
                    ? lerp(_SuctionColor.rgb, _NeutralColor.rgb, low)
                    : lerp(_NeutralColor.rgb, _PressureColor.rgb, high);
                return half4(color, saturate(_Opacity));
            }
            ENDHLSL
        }
    }
}
