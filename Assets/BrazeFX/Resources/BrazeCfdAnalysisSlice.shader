Shader "BrazeFX/CFD/AnalysisSlice"
{
    SubShader
    {
        Tags { "Queue"="Transparent+15" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "AnalysisSlice"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_StateTex); SAMPLER(sampler_StateTex);
            TEXTURE3D(_VelocityTex); SAMPLER(sampler_VelocityTex);
            TEXTURE3D(_PressureTex); SAMPLER(sampler_PressureTex);
            TEXTURE3D(_SdfTex); SAMPLER(sampler_SdfTex);
            float4x4 _WorldToFluidUVW;
            float4x4 _VoxelToWorldVelocity;
            float3 _VelocityInvRes;
            float3 _PlaneAxisU;
            float3 _PlaneAxisV;
            float2 _PlaneAspect;
            float _Quantity;
            float _ScalarMinimum;
            float _ScalarInvRange;
            float _ColorMap;
            float _ShowScalar;
            float _ShowVectors;
            float _VectorColumns;
            float _Opacity;
            float4 _SolidColor;
            float _UseSdf;

            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 world : TEXCOORD0; float2 uv : TEXCOORD1; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.world = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.world);
                output.uv = input.uv;
                return output;
            }

            float3 CfdRainbow(float u)
            {
                u = saturate(u);
                float3 c0 = float3(0.02, 0.12, 0.95);
                float3 c1 = float3(0.00, 0.90, 1.00);
                float3 c2 = float3(0.03, 0.92, 0.28);
                float3 c3 = float3(1.00, 0.90, 0.02);
                float3 c4 = float3(1.00, 0.04, 0.01);
                if (u < 0.25) return lerp(c0, c1, u * 4.0);
                if (u < 0.50) return lerp(c1, c2, (u - 0.25) * 4.0);
                if (u < 0.75) return lerp(c2, c3, (u - 0.50) * 4.0);
                return lerp(c3, c4, (u - 0.75) * 4.0);
            }

            float3 MapColor(float u)
            {
                float3 color = CfdRainbow(u);
                if (_ColorMap < 1.5)
                {
                    if (_ColorMap >= 0.5)
                        color = u < 0.5
                            ? lerp(float3(0.03, 0.20, 1.0), float3(0.92, 0.94, 0.98), u * 2.0)
                            : lerp(float3(0.92, 0.94, 0.98), float3(1.0, 0.04, 0.01), (u - 0.5) * 2.0);
                }
                else
                {
                    color = u < 0.5
                        ? lerp(float3(0.02, 0.05, 0.25), float3(1.0, 0.10, 0.01), u * 2.0)
                        : lerp(float3(1.0, 0.10, 0.01), float3(1.0, 0.95, 0.18), (u - 0.5) * 2.0);
                }
                return color;
            }

            float SegmentDistance(float2 p, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-5));
                return length(p - (a + ab * t));
            }

            float ArrowMask(float2 uv, float2 flow, float speedGate)
            {
                float columns = max(_VectorColumns, 1.0);
                float rows = max(1.0, round(columns * _PlaneAspect.y / max(_PlaneAspect.x, 1e-4)));
                float2 cell = frac(uv * float2(columns, rows)) - 0.5;
                float2 dir = normalize(flow + float2(1e-7, 0.0));
                float2 side = float2(-dir.y, dir.x);
                float2 p = float2(dot(cell, dir), dot(cell, side));
                float d = SegmentDistance(p, float2(-0.31, 0.0), float2(0.27, 0.0));
                d = min(d, SegmentDistance(p, float2(0.27, 0.0), float2(0.12, 0.12)));
                d = min(d, SegmentDistance(p, float2(0.27, 0.0), float2(0.12, -0.12)));
                float aa = max(fwidth(d), 0.006);
                return (1.0 - smoothstep(0.022, 0.022 + aa, d)) * speedGate;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 uvw = mul(_WorldToFluidUVW, float4(input.world, 1.0)).xyz;
                if (any(uvw < 0.0) || any(uvw > 1.0)) discard;
                float4 state = SAMPLE_TEXTURE3D_LOD(_StateTex, sampler_StateTex, uvw, 0.0);
                float3 velocityVox = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, uvw, 0.0).xyz;
                float3 velocityWorld = mul((float3x3)_VoxelToWorldVelocity, velocityVox);
                float pressure = SAMPLE_TEXTURE3D_LOD(_PressureTex, sampler_PressureTex, uvw, 0.0).r;

                float value = length(velocityWorld);
                if (_Quantity > 0.5 && _Quantity < 1.5) value = pressure;
                else if (_Quantity > 1.5 && _Quantity < 2.5) value = state.y;
                else if (_Quantity > 2.5)
                {
                    float3 dx = float3(_VelocityInvRes.x, 0.0, 0.0);
                    float3 dy = float3(0.0, _VelocityInvRes.y, 0.0);
                    float3 dz = float3(0.0, 0.0, _VelocityInvRes.z);
                    float3 vx0 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw - dx), 0.0).xyz;
                    float3 vx1 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw + dx), 0.0).xyz;
                    float3 vy0 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw - dy), 0.0).xyz;
                    float3 vy1 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw + dy), 0.0).xyz;
                    float3 vz0 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw - dz), 0.0).xyz;
                    float3 vz1 = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, saturate(uvw + dz), 0.0).xyz;
                    float3 curlVox = float3((vy1.z - vy0.z) - (vz1.y - vz0.y),
                                            (vz1.x - vz0.x) - (vx1.z - vx0.z),
                                            (vx1.y - vx0.y) - (vy1.x - vy0.x)) * 0.5;
                    value = length(mul((float3x3)_VoxelToWorldVelocity, curlVox));
                }

                float scalarU = saturate((value - _ScalarMinimum) * _ScalarInvRange);
                float3 color = _ShowScalar > 0.5 ? MapColor(scalarU) : float3(0.12, 0.14, 0.17);
                float alpha = _ShowScalar > 0.5 ? _Opacity : _Opacity * 0.28;

                if (_UseSdf > 0.5)
                {
                    float sdf = SAMPLE_TEXTURE3D_LOD(_SdfTex, sampler_SdfTex, uvw, 0.0).r;
                    if (sdf <= 0.0) { color = _SolidColor.rgb; alpha = max(alpha, _SolidColor.a); }
                }

                if (_ShowVectors > 0.5)
                {
                    float2 flow = float2(dot(velocityWorld, _PlaneAxisU), dot(velocityWorld, _PlaneAxisV));
                    float arrows = ArrowMask(input.uv, flow, smoothstep(1e-4, 0.02, length(flow)));
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float3 arrowColor = luma > 0.58 ? float3(0.02, 0.025, 0.03) : float3(0.96, 0.98, 1.0);
                    color = lerp(color, arrowColor, arrows * 0.9);
                    alpha = max(alpha, arrows * 0.95);
                }
                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
