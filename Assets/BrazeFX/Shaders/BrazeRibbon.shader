// BrazeFX dedicated weapon ribbon shader.
//
// Mesh UVs: x = history/current-to-tail, y = across the configured local control points.

Shader "BrazeFX/Ribbon"
{
    Properties
    {
        _BlendSrc ("BlendSrc", Float) = 1
        _BlendDst ("BlendDst", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeRibbon"
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

            float  _ColorMode;   // 0 FireSmoke, 1 Gradient, 2 Constant
            TEXTURE2D(_Ramp); SAMPLER(sampler_Ramp);
            float4 _ConstColor;
            float4 _FireColor;
            float4 _SmokeColor;
            float  _ColorMid;
            float  _Emissive;
            float  _Opacity;
            TEXTURE2D(_LengthAlphaTex); SAMPLER(sampler_LengthAlphaTex);
            float  _SoftEdge;
            float  _Multiply;

            float  _FireAmount;
            float  _FireScale;
            float  _FireSpeed;
            float  _FireDetail;
            float  _FireSharp;
            float  _FireEdge;
            float  _FireHeat;
            float  _FireTime;
            float  _FireSeed;

            // Base 3D value noise — a repeating baked texture (BrazeNoiseTex, bound globally) instead of the
            // procedural 8-corner sin-hash lattice: that was ~48 sin() per n3, × up to 22 n3 per fragment in the
            // domain-warped fire sheet, × ribbon overdraw — the flame path's FPS tank. The baked field is the
            // same smoothstep-faded value noise, so fbm4/fbm6 and the warped fire look are unchanged. LOD 0:
            // callers are in branchy flow with no safe derivatives, and the 64³ tile is cache-resident.
            TEXTURE3D(_BrazeNoise3D); SAMPLER(sampler_BrazeNoise3D);
            float n3(float3 p)
            {
                return SAMPLE_TEXTURE3D_LOD(_BrazeNoise3D, sampler_BrazeNoise3D, p * (1.0 / 8.0), 0).r;
            }

            float3 fireWarp(float3 p)
            {
                const float3x3 m3 = float3x3(
                    0.856, 0.2688, 0.4416,
                   -0.192, 0.9584, -0.2112,
                   -0.480, 0.0960, 0.8720);
                return mul(m3, p);
            }

            float fbm4(float3 p)
            {
                float f = 0.0;
                p = fireWarp(p);
                f += 0.5000 * n3(p); p = fireWarp(p) * 2.02;
                f += 0.2500 * n3(p); p = fireWarp(p) * 2.03;
                f += 0.1250 * n3(p); p = fireWarp(p) * 2.01;
                f += 0.0625 * n3(p);
                return f / 0.9375;
            }

            float fbm6(float3 p, int octaves)
            {
                float f = 0.0;
                float norm = 0.0;
                octaves = clamp(octaves, 1, 6);
                p = fireWarp(p);
                if (octaves > 0) { f += 0.500000 * n3(p); norm += 0.500000; p = fireWarp(p) * 2.02; }
                if (octaves > 1) { f += 0.250000 * n3(p); norm += 0.250000; p = fireWarp(p) * 2.03; }
                if (octaves > 2) { f += 0.125000 * n3(p); norm += 0.125000; p = fireWarp(p) * 2.01; }
                if (octaves > 3) { f += 0.062500 * n3(p); norm += 0.062500; p = fireWarp(p) * 2.04; }
                if (octaves > 4) { f += 0.031250 * n3(p); norm += 0.031250; p = fireWarp(p) * 2.01; }
                if (octaves > 5) { f += 0.015625 * n3(p); norm += 0.015625; }
                return f / max(norm, 1e-5);
            }

            float ribbonFireSheet(float t, float profile, out float3 fireColor)
            {
                float time = _FireTime * max(_FireSpeed, 0.0) * 0.7;
                float2 q = float2(profile, t);
                float2 p = -1.0 + 2.0 * q;
                p.x *= 1.65;
                p.y *= 0.3;
                p.y -= time * 1.5;

                float seed = _FireSeed * 97.0;
                float tc = time * 1.2 + seed;
                float tw1 = time * 2.5 + seed;
                float tw2 = time * 0.6 - seed * 0.37;

                float3 vw1 = float3(p, tw1);
                vw1.y *= 2.8;
                float2 ofs1 = float2(fbm4(vw1), fbm4(vw1 + float3(10.0, 20.0, 50.0)));
                ofs1 *= 2.0;
                ofs1 -= 1.0;
                ofs1.y *= 0.3;
                ofs1.x *= 1.3;

                float3 vw2 = float3(p, tw2);
                vw2.y *= 0.8;
                float2 ofs2 = float2(fbm4(vw2), fbm4(vw2 + float3(10.0, 20.0, 50.0)));
                ofs2 *= 2.0;
                ofs2 -= 1.0;
                ofs2.y *= 0.3;
                ofs2.x *= 1.3;

                float domainScale = max(_FireScale, 0.25);
                float2 vs = (p + ofs1 * 0.5 + ofs2 * 0.9) * domainScale;
                int octaves = (int)clamp(_FireDetail + 1.0, 1.0, 6.0);
                float l = fbm6(float3(vs, tc), octaves);
                l = smoothstep(0.0, 1.0, l);

                float tailLift = pow(saturate(t) * 0.8, 0.6);
                float edge = saturate(_FireEdge);
                float widthFade = smoothstep(0.0, 0.02 + edge * 0.28, profile) *
                    smoothstep(0.0, 0.02 + edge * 0.28, 1.0 - profile);
                l = max(0.0, (l - tailLift - edge * 0.18) * lerp(1.8, 2.5, edge));
                l *= lerp(1.0, widthFade, edge);
                l = pow(saturate(l), max(_FireSharp, 0.05));

                float r = pow(l, 1.5);
                float g = pow(l, 3.0);
                float b = pow(l, 6.0);
                fireColor = float3(r, g, b);
                return saturate(l);
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
                float t = saturate(i.uv.x);
                float across = saturate(i.uv.y);

                float3 col;
                float colorAlpha = 1.0;
                if (_ColorMode < 0.5)
                {
                    float cm = max(_ColorMid, 1e-3);
                    float mixT = smoothstep(cm * 0.5, min(cm * 1.5, 1.0), t);
                    col = lerp(_FireColor.rgb, _SmokeColor.rgb, mixT);
                    colorAlpha = lerp(_FireColor.a, _SmokeColor.a, mixT);
                }
                else if (_ColorMode < 1.5)
                {
                    float4 ramp = SAMPLE_TEXTURE2D(_Ramp, sampler_Ramp, float2(t, 0.5));
                    col = ramp.rgb;
                    colorAlpha = ramp.a;
                }
                else
                {
                    col = _ConstColor.rgb;
                    colorAlpha = _ConstColor.a;
                }

                col *= lerp(_Emissive, 1.0, t);

                float lengthFade = SAMPLE_TEXTURE2D(_LengthAlphaTex, sampler_LengthAlphaTex, float2(t, 0.5)).r;
                float a = _Opacity * saturate(colorAlpha);
                a *= lengthFade;

                float e = max(_SoftEdge, 1e-3);
                float profileMask = smoothstep(0.0, e, across) * smoothstep(0.0, e, 1.0 - across);
                a *= profileMask;

                // Kill already-invisible fragments BEFORE the fire sheet — the fire only darkens/colours what is
                // there (output is premultiplied by a), so faded edge/tail pixels must not pay for it.
                clip(a - 1e-4);

                // Continuous domain-warped fire field across the swept ribbon surface. No tiled strand lanes.
                [branch] if (_FireAmount > 1e-4)
                {
                    float3 heat;
                    float flame = ribbonFireSheet(t, across, heat);
                    col += heat * (_FireHeat * _FireAmount);
                    a *= lerp(1.0, flame, _FireAmount);
                    clip(a - 1e-4);
                }

                a = saturate(a);

                if (_Multiply > 0.5) return half4(lerp(half3(1, 1, 1), col, a), a);
                return half4(col * a, a);
            }
            ENDHLSL
        }
    }
}
