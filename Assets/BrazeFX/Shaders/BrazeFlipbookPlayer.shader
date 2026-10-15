// Braze Flipbook Player renderer. One lightweight mesh per player.
// There is deliberately no particle pool, indirect-args buffer, or compute dispatch.
Shader "BrazeFX/FlipbookPlayer"
{
    Properties
    {
        _FlipbookTexture ("Flipbook Atlas", 2D) = "white" {}
        [HideInInspector] _BlendSrc ("Blend Src", Float) = 1
        [HideInInspector] _BlendDst ("Blend Dst", Float) = 10
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeFlipbookPlayer"
            Blend [_BlendSrc] [_BlendDst]
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_FlipbookTexture);   SAMPLER(sampler_FlipbookTexture);
            TEXTURE2D(_FlipbookSizeLut);   SAMPLER(sampler_FlipbookSizeLut);
            TEXTURE2D(_FlipbookOpacityLut); SAMPLER(sampler_FlipbookOpacityLut);

            float4 _FlipbookTexture_TexelSize;
            float4 _FlipbookPositionDelay;
            float4 _FlipbookRotation;
            float4 _FlipbookScaleSpeed; // xyz transform scale, w playback speed
            float _FlipbookStartFrame;
            float _FlipbookFrameCount;
            float _FlipbookPremultiplied;
            float4 _FlipbookSheet;       // columns, rows, fps, Once/Loop/PingPong/HoldLast
            float4 _FlipbookTint;
            float _FlipbookTime;
            float _FlipbookFacing;       // Camera/Local/World
            float _FlipbookBrightness;
            float _FlipbookAlphaCutoff;
            float _FlipbookSoftFade;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float viewDepth : TEXCOORD2;
            };

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            float3 rotateByQuaternion(float3 v, float4 q)
            {
                return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
            }

            void selectFrame(out float frame, out float curveT, out float visible)
            {
                float columns = max(floor(_FlipbookSheet.x), 1.0);
                float rows = max(floor(_FlipbookSheet.y), 1.0);
                float frameCount = clamp(floor(_FlipbookFrameCount), 1.0, columns * rows);
                float start = clamp(floor(_FlipbookStartFrame), 0.0, frameCount - 1.0);
                float sequenceLength = max(frameCount - start, 1.0);
                float age = _FlipbookTime - _FlipbookPositionDelay.w;
                float advance = max(age, 0.0) * max(_FlipbookSheet.z, 0.0) * max(_FlipbookScaleSpeed.w, 0.0);

                visible = age >= 0.0 ? 1.0 : 0.0;
                float localFrame = 0.0;

                if (_FlipbookSheet.w < 0.5) // Once: disappear after the last frame interval.
                {
                    visible *= advance < sequenceLength ? 1.0 : 0.0;
                    localFrame = min(floor(advance), sequenceLength - 1.0);
                    curveT = sequenceLength > 1.0 ? localFrame / (sequenceLength - 1.0) : 1.0;
                }
                else if (_FlipbookSheet.w < 1.5) // Loop
                {
                    float phase = fmod(advance, sequenceLength);
                    localFrame = floor(phase);
                    curveT = phase / sequenceLength;
                }
                else if (_FlipbookSheet.w < 2.5) // Ping Pong, without duplicated end frames.
                {
                    float last = sequenceLength - 1.0;
                    float period = max(last * 2.0, 1.0);
                    float phase = fmod(advance, period);
                    localFrame = phase <= last ? floor(phase) : ceil(period - phase);
                    curveT = last > 0.0 ? saturate(localFrame / last) : 1.0;
                }
                else // Hold Last
                {
                    localFrame = min(floor(advance), sequenceLength - 1.0);
                    curveT = sequenceLength > 1.0 ? localFrame / (sequenceLength - 1.0) : 1.0;
                }

                frame = start + localFrame;
            }

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                float frame, curveT, visible;
                selectFrame(frame, curveT, visible);

                float2 uv = input.uv;
                float columns = max(floor(_FlipbookSheet.x), 1.0);
                float rows = max(floor(_FlipbookSheet.y), 1.0);
                float column = fmod(frame, columns);
                float authoredRow = floor(frame / columns);
                float row = (rows - 1.0) - authoredRow;
                float2 inset = 0.5 * _FlipbookTexture_TexelSize.xy * float2(columns, rows);
                uv = inset + uv * (1.0 - 2.0 * inset);
                o.uv = (float2(column, row) + uv) / float2(columns, rows);

                float sizeCurve = SAMPLE_TEXTURE2D_LOD(_FlipbookSizeLut, sampler_FlipbookSizeLut,
                                                        float2(curveT, 0.5), 0).r;
                float opacityCurve = SAMPLE_TEXTURE2D_LOD(_FlipbookOpacityLut, sampler_FlipbookOpacityLut,
                                                           float2(curveT, 0.5), 0).r;
                float3 extent = max(_FlipbookScaleSpeed.xyz, 0.0) * max(sizeCurve, 0.0) * visible;
                float3 localOffset = rotateByQuaternion(input.positionOS * extent, _FlipbookRotation);
                float3 center = _FlipbookPositionDelay.xyz;
                float3 worldOffset;

                if (_FlipbookFacing < 0.5) // Camera-relative orientation, including authored tilt.
                {
                    float3 cameraRight = UNITY_MATRIX_V[0].xyz;
                    float3 cameraUp = UNITY_MATRIX_V[1].xyz;
                    float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
                    worldOffset = cameraRight * localOffset.x + cameraUp * localOffset.y + cameraForward * localOffset.z;
                }
                else // Child Transform orientation in world space.
                {
                    worldOffset = localOffset;
                }

                float3 worldPosition = center + worldOffset;
                o.positionCS = TransformWorldToHClip(worldPosition);
                o.viewDepth = -TransformWorldToView(worldPosition).z;
                o.color = _FlipbookTint;
                o.color.rgb *= max(_FlipbookBrightness, 0.0);
                o.color.a *= max(opacityCurve, 0.0) * visible;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 source = SAMPLE_TEXTURE2D(_FlipbookTexture, sampler_FlipbookTexture, i.uv);
                float modifierAlpha = i.color.a;
                float alpha = source.a * modifierAlpha;

                if (_FlipbookSoftFade > 0.0)
                {
                    float2 screenUV = i.positionCS.xy / _ScreenParams.xy;
                    float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                    float soft = saturate((sceneDepth - i.viewDepth) / _FlipbookSoftFade);
                    modifierAlpha *= soft;
                    alpha *= soft;
                }

                if (alpha < _FlipbookAlphaCutoff) discard;
                float3 tinted = source.rgb * i.color.rgb;
                // Export Studio stores the volume buffer exactly as rendered: premultiplied radiance + coverage.
                // Apply only Player opacity/soft-fade to that radiance. Straight-alpha atlases still take the
                // conventional RGB × final-alpha path.
                float3 premultiplied = _FlipbookPremultiplied > 0.5
                    ? tinted * modifierAlpha
                    : tinted * alpha;
                return half4(premultiplied, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
