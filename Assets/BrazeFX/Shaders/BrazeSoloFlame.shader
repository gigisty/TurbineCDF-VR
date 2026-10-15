// BrazeFX standalone single-card procedural flame shader.

Shader "BrazeFX/Solo Flame"
{
    Properties
    {
        _BlendSrc ("BlendSrc", Float) = 1
        _BlendDst ("BlendDst", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _FlameType ("Flame Type", Float) = 0
        [HideInInspector] _Preset ("Preset", Float) = 2
        [HideInInspector] _Tint ("Tint", Color) = (1, 0.42, 0.12, 1)
        [HideInInspector] _Intensity ("Intensity", Float) = 3.5
        [HideInInspector] _BillowHeat ("Billow Heat", Float) = 0.6
        [HideInInspector] _Opacity ("Opacity", Float) = 1
        [HideInInspector] _Scale ("Scale", Float) = 1
        [HideInInspector] _Speed ("Speed", Float) = 1.4
        [HideInInspector] _Detail ("Detail", Float) = 4
        [HideInInspector] _Quality ("Quality", Float) = 2
        [HideInInspector] _Sharpness ("Sharpness", Float) = 1.2
        [HideInInspector] _Edge ("Edge", Float) = 0
        [HideInInspector] _Stretch ("Stretch", Float) = 2
        [HideInInspector] _Height ("Flame Height", Float) = 2
        [HideInInspector] _Smoke ("Smoke", Float) = 0.4
        [HideInInspector] _SmokeTint ("Smoke Tint", Color) = (0.14, 0.11, 0.10, 0.7)
        [HideInInspector] _SoftEdge ("Soft Edge", Float) = 0.03
        [HideInInspector] _Seed ("Seed", Float) = 0
        [HideInInspector] _TimeParam ("Time", Float) = 0
        [HideInInspector] _Multiply ("Multiply", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeSoloFlame"
            Blend [_BlendSrc] [_BlendDst]
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float  _FlameType;   // 0 red, 1 blue, 2 green, 3 custom tint
                float  _Preset;      // 0..4 = original shader flame columns, rendered one at a time
                float4 _Tint;
                float  _Intensity;
                float  _BillowHeat;
                float  _Opacity;
                float  _Scale;
                float  _Speed;
                float  _Detail;
                float  _Quality;
                float  _Sharpness;
                float  _Edge;
                float  _Stretch;
                float  _Height;
                float  _Smoke;
                float4 _SmokeTint;
                float  _SoftEdge;
                float  _Seed;
                float  _TimeParam;
                float  _Multiply;
            CBUFFER_END

            float2 flameHash(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
            }

            float flameNoise(float2 p)
            {
                const float K1 = 0.366025404;
                const float K2 = 0.211324865;
                float2 i = floor(p + (p.x + p.y) * K1);
                float2 a = p - i + (i.x + i.y) * K2;
                float2 o = (a.x > a.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
                float2 b = a - o + K2;
                float2 c = a - 1.0 + 2.0 * K2;
                float3 h = max(0.5 - float3(dot(a, a), dot(b, b), dot(c, c)), 0.0);
                float3 n = h * h * h * h * float3(dot(a, flameHash(i + 0.0)), dot(b, flameHash(i + o)), dot(c, flameHash(i + 1.0)));
                return dot(n, float3(70.0, 70.0, 70.0));
            }

            float flameFbm(float2 uv, int octaves)
            {
                float f = 0.0;
                float amp = 0.5;
                float2x2 m = float2x2(1.6, 1.2, -1.2, 1.6);
                octaves = min(max(octaves, 1), 8);
                [loop] for (int k = 0; k < 8; k++)
                {
                    if (k >= octaves) break;
                    f += amp * flameNoise(uv);
                    uv = mul(m, uv);
                    amp *= 0.5;
                }
                return 0.5 + 0.5 * f;
            }

            void soloFlame(float2 uv, out float alpha, out float3 color)
            {
                uv = saturate(uv);

                float preset = floor(clamp(_Preset, 0.0, 4.0));

                float2 q;
                q.x = (uv.x - 0.5) * max(_Scale, 0.25);
                q.y = uv.y * max(_Stretch, 0.25);

                float strength = preset + 1.0;
                float t3 = max(3.0, 1.25 * strength) * _TimeParam * max(_Speed, 0.0);
                q.y -= 0.25;

                float2 seedDomain = float2(_Seed * 0.173, _Seed * -0.119);
                float2 noiseDomain = strength * q + seedDomain - float2(0.0, t3);
                float n = flameFbm(noiseDomain, (int)round(clamp(_Detail, 1.0, 8.0)));
                float edge = saturate(_Edge);
                float2 shapeScale = float2(1.8 + q.y * 1.5, 0.75);
                float shape = max(0.0, length(q * shapeScale) - n * max(0.0, q.y + 0.25));
                float c = 1.0 - lerp(16.0, 30.0, edge) * pow(shape, max(_Sharpness, 0.05));

                // Heat: hot at the base, cooling with height. The original shader shipped two variants —
                // a short flame (coefficient 2.5, cools at uv.y ~ 0.44) and a tall one (1.25, ~0.89).
                // _Height selects between them: coefficient = 2.5 / _Height (higher = taller flame).
                float heightFade = 2.5 / max(_Height, 0.25);
                float deathY = min(0.92, 1.107 / heightFade);   // uv.y where the fire fully cools (capped → always leaves headroom)
                float heat = 1.5 - pow(heightFade * uv.y, 4.0);

                // Large-scale billow mask: the big rising masses (low-frequency, slow). Boosting the hot core
                // where the billows are dense makes them burn hotter/whiter than the thin fire around them —
                // the depth cue in the reference (bright billows reading in front of the redder body).
                float billow = 0.5 + 0.5 * flameNoise(q * 2.0 + seedDomain - float2(0.0, t3 * 0.6));
                float hot = 1.0 + max(_BillowHeat, 0.0) * saturate(billow * 1.7 - 0.7);

                float c1 = saturate(n * c * heat * hot);        // fire intensity — billows run hotter
                float c3 = c1 * c1 * c1;
                color = float3(1.5 * c1, 1.5 * c3, c3 * c3);    // fire colour

                if (_FlameType > 0.5 && _FlameType < 1.5) color = color.zyx;
                else if (_FlameType > 1.5 && _FlameType < 2.5) color = 0.85 * color.yxz;
                else if (_FlameType > 2.5) color *= max(_Tint.rgb, 0.0);

                // Smoke: the plume body that has risen past the flame and cooled. Driven by the SAME noise so
                // it reads as the top of the same plume — dim, desaturated, appearing only where fire has faded.
                float body = saturate(n * c);                   // plume density (heat-independent)
                float cooled = saturate(1.0 - saturate(heat));  // 0 inside the fire, 1 up in the cooled smoke
                float smoke = body * cooled * max(_Smoke, 0.0);
                color += smoke * max(_SmokeTint.rgb, 0.0);

                // Alpha tapers to zero by the cool line so no bright noise tongue reaches (and hard-clips at)
                // the top edge of the card. Fire body + smoke share the envelope.
                float topTaper = 1.0 - smoothstep(deathY * 0.6, deathY, uv.y);
                float fireA = c * (1.0 - pow(uv.y, 3.0));
                alpha = saturate(max(fireA, smoke * _SmokeTint.a) * topTaper);
            }

            float4 sampleSoloFlame(float2 uv)
            {
                float flameAlpha;
                float3 flameColor;
                soloFlame(uv, flameAlpha, flameColor);

                float e = max(_SoftEdge, 1e-4);
                float edgeFade = smoothstep(0.0, e, uv.x) * smoothstep(0.0, e, 1.0 - uv.x);
                edgeFade *= smoothstep(0.0, e, uv.y) * smoothstep(0.0, e, 1.0 - uv.y);

                float a = saturate(flameAlpha * edgeFade);
                return float4(flameColor * a, a);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 dx = ddx(i.uv) * 0.45;
                float2 dy = ddy(i.uv) * 0.45;

                float4 flame = sampleSoloFlame(i.uv);
                float sampleCount = 1.0;
                if (_Quality > 0.5)
                {
                    flame += sampleSoloFlame(i.uv + dx + dy);
                    flame += sampleSoloFlame(i.uv - dx - dy);
                    sampleCount += 2.0;
                }
                if (_Quality > 1.5)
                {
                    flame += sampleSoloFlame(i.uv + dx - dy);
                    flame += sampleSoloFlame(i.uv - dx + dy);
                    sampleCount += 2.0;
                }

                flame /= sampleCount;

                float a = saturate(flame.a * _Opacity);
                float3 col = flame.a > 1e-5 ? flame.rgb / max(flame.a, 1e-5) : float3(0.0, 0.0, 0.0);
                col *= max(_Intensity, 0.0);

                if (_Multiply > 0.5) return half4(lerp(half3(1, 1, 1), col, a), a);
                return half4(col * a, a);
            }
            ENDHLSL
        }
    }
}
