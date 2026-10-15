// BrazeFX cheap procedural fire/smoke for generated primitive surfaces.

Shader "BrazeFX/Primitive Cinders"
{
    Properties
    {
        _BlendSrc ("BlendSrc", Float) = 1
        _BlendDst ("BlendDst", Float) = 10
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _BoundsSize ("Bounds Size", Vector) = (1, 1, 1, 0)
        [HideInInspector] _ObjectScaleAbs ("Object Scale Abs", Vector) = (1, 1, 1, 0)
        [HideInInspector] _FlowAxis ("Flow Axis", Float) = 1
        [HideInInspector] _DeStretch ("De-Stretch", Float) = 1
        [HideInInspector] _DomainStretch ("Domain Stretch", Vector) = (1, 1, 1, 0)
        [HideInInspector] _Scale ("Scale", Float) = 3
        [HideInInspector] _FireColor ("Fire Color", Color) = (1, 0.42, 0.12, 1)
        [HideInInspector] _SmokeColor ("Smoke Color", Color) = (0.14, 0.12, 0.10, 0.65)
        [HideInInspector] _Intensity ("Intensity", Float) = 2.5
        [HideInInspector] _Opacity ("Opacity", Float) = 0.85
        [HideInInspector] _FlameHeight ("Flame Height", Float) = 1
        [HideInInspector] _Coverage ("Coverage", Float) = 1
        [HideInInspector] _Speed ("Speed", Float) = 1.2
        [HideInInspector] _Detail ("Detail", Float) = 4
        [HideInInspector] _Sharpness ("Sharpness", Float) = 1.35
        [HideInInspector] _Edge ("Edge", Float) = 0.25
        [HideInInspector] _Heat ("Heat", Float) = 1.25
        [HideInInspector] _Smoke ("Smoke", Float) = 0.35
        [HideInInspector] _RimFade ("Rim Fade", Float) = 0.15
        [HideInInspector] _Seed ("Seed", Float) = 0
        [HideInInspector] _TimeParam ("Time", Float) = 0
        [HideInInspector] _MotionVector ("Motion Vector", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MotionAmount ("Motion Amount", Float) = 0
        [HideInInspector] _MotionStretch ("Motion Stretch", Float) = 0
        [HideInInspector] _Multiply ("Multiply", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazePrimitiveCinders"
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
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float2 uv         : TEXCOORD3;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BoundsSize;
                float4 _ObjectScaleAbs;
                float  _FlowAxis;
                float  _DeStretch;
                float4 _DomainStretch;
                float  _Scale;
                float4 _FireColor;
                float4 _SmokeColor;
                float  _Intensity;
                float  _Opacity;
                float  _FlameHeight;
                float  _Coverage;
                float  _Speed;
                float  _Detail;
                float  _Sharpness;
                float  _Edge;
                float  _Heat;
                float  _Smoke;
                float  _RimFade;
                float  _Seed;
                float  _TimeParam;
                float4 _MotionVector;
                float  _MotionAmount;
                float  _MotionStretch;
                float  _Multiply;
            CBUFFER_END

            float n1f0(float p) { return frac(sin(p * 1.7227636) * 803.0); }
            float n1f1(float p) { return frac(sin(p * 1.42736 + 1.12) * 510.0); }
            float n1f2(float p) { return frac(sin(p * 1.22712 + 12.161) * 520.0); }

            float n3f(float3 p)
            {
                return frac(
                    n1f0(p.x) + n1f1(p.y) + n1f2(p.z) +
                    n1f0(p.x * 1.613) + n1f1(p.y * 3.112) + n1f2(p.z * 4.112));
            }

            float n3(float3 p)
            {
                float3 b = floor(p);
                float3 e = b + 1.0;
                float3 f = smoothstep(float3(0.0, 0.0, 0.0), float3(1.0, 1.0, 1.0), frac(p));

                float c000 = n3f(b);
                float c001 = n3f(float3(b.x, b.y, e.z));
                float c010 = n3f(float3(b.x, e.y, b.z));
                float c011 = n3f(float3(b.x, e.y, e.z));
                float c100 = n3f(float3(e.x, b.y, b.z));
                float c101 = n3f(float3(e.x, b.y, e.z));
                float c110 = n3f(float3(e.x, e.y, b.z));
                float c111 = n3f(e);

                float4 z = lerp(float4(c000, c100, c010, c110), float4(c001, c101, c011, c111), f.z);
                float2 yz = lerp(z.xy, z.zw, f.y);
                return lerp(yz.x, yz.y, f.x);
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

            float3 flowSpace(float3 p)
            {
                if (_FlowAxis < 0.5) return float3(p.y, p.x, p.z);
                if (_FlowAxis < 1.5) return p;
                return float3(p.x, p.z, p.y);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS);
                o.positionOS = v.positionOS;
                o.positionWS = TransformObjectToWorld(v.positionOS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 objectScale = lerp(float3(1.0, 1.0, 1.0), max(_ObjectScaleAbs.xyz, 1e-4), saturate(_DeStretch));
                float3 domainStretch = max(_DomainStretch.xyz, 1e-4);
                float domainScale = max(_Scale, 0.05);

                float3 domain = i.positionOS * objectScale * domainStretch * domainScale;
                float3 bounds = max(_BoundsSize.xyz * objectScale * domainStretch * domainScale, 1e-4);
                float3 fp = flowSpace(domain);
                float3 fb = flowSpace(bounds);
                float h01 = saturate(fp.y / max(fb.y, 1e-4) + 0.5);

                float flameHeight = clamp(_FlameHeight, 0.1, 2.5);
                float height01 = saturate(h01 / flameHeight);
                float heightMask = flameHeight >= 0.995 ? 1.0 : 1.0 - smoothstep(flameHeight, min(1.0, flameHeight + 0.18), h01);
                float coverage = saturate(_Coverage);
                float coverageMask = coverage > 0.995 ? 1.0 : 1.0 - smoothstep(coverage, min(1.0, coverage + 0.2), h01);
                float shapeMask = heightMask * coverageMask;

                float motionAmount = clamp(_MotionAmount, 0.0, 2.0);
                float motionStretch = clamp(_MotionStretch, 0.0, 2.0);
                float3 motionF = flowSpace(_MotionVector.xyz) * motionAmount;
                float motionEnergy = saturate(length(motionF) * 0.18);
                motionF *= 0.10 + smoothstep(0.05, 1.0, h01) * 0.42;
                motionF.y *= 0.22;

                float time = _TimeParam * max(_Speed, 0.0) * 0.7;
                float seed = _Seed * 97.0;
                float3 q = fp + float3(seed * 0.173, seed * -0.119, seed * 0.071);
                q += motionF;
                q.y *= 0.34;
                q.y -= time * (1.5 + motionEnergy * motionStretch);

                float3 vw1 = q;
                vw1.y *= 2.8;
                float3 ofs1 = float3(
                    fbm4(vw1),
                    fbm4(vw1 + float3(10.0, 20.0, 50.0)),
                    fbm4(vw1 + float3(31.0, 7.0, 17.0)));
                ofs1 = ofs1 * 2.0 - 1.0;
                ofs1.y *= 0.3;
                ofs1.xz *= 1.3 + motionEnergy * motionStretch * 0.45;

                float3 vw2 = q;
                vw2.y *= 0.8;
                float3 ofs2 = float3(
                    fbm4(vw2 + float3(3.0, 29.0, 11.0)),
                    fbm4(vw2 + float3(10.0, 20.0, 50.0)),
                    fbm4(vw2 + float3(23.0, 5.0, 41.0)));
                ofs2 = ofs2 * 2.0 - 1.0;
                ofs2.y *= 0.3;
                ofs2.xz *= 1.3 + motionEnergy * motionStretch * 0.35;

                int octaves = (int)round(clamp(_Detail + 1.0, 1.0, 6.0));
                float n = fbm6(q + ofs1 * 0.5 + ofs2 * 0.9 + float3(0.0, time * 1.2, 0.0), octaves);
                n = smoothstep(0.0, 1.0, n);

                float edge = saturate(_Edge);
                float cooling = pow(saturate(height01 * 0.82), 0.58);
                float flame = max(0.0, (n - cooling - edge * 0.16) * lerp(1.85, 2.55, edge));
                flame = pow(saturate(flame * shapeMask), max(_Sharpness, 0.05));

                float lowerHeat = saturate(1.2 - height01);
                float3 fireCore = float3(pow(flame, 1.5), pow(flame, 3.0), pow(flame, 6.0));
                float smokeBody = smoothstep(0.25, 0.92, n) * (1.0 - flame * 0.75) * saturate(_Smoke) * shapeMask;
                smokeBody *= lerp(0.45, 1.0, height01);

                float3 col = _FireColor.rgb * flame;
                col += fireCore * max(_Heat, 0.0) * lerp(0.8, 1.25, lowerHeat);
                col += _SmokeColor.rgb * smokeBody;

                float alpha = max(flame * _FireColor.a, smokeBody * _SmokeColor.a) * saturate(_Opacity);

                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - i.positionWS);
                float rim = abs(dot(normalize(i.normalWS), viewDir));
                alpha *= lerp(1.0, smoothstep(0.0, 0.35, rim), saturate(_RimFade));

                alpha = saturate(alpha);
                clip(alpha - 1e-4);

                col *= max(_Intensity, 0.0);

                if (_Multiply > 0.5) return half4(lerp(half3(1.0, 1.0, 1.0), col, alpha), alpha);
                return half4(col * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
