Shader "BrazeFX/CFD/SdfPreview"
{
    SubShader
    {
        Tags { "Queue"="Transparent+30" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "SdfPreview"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // Debug shell: always visible (including through the body mesh and with the camera inside the
            // preview box). Front faces are culled so the box's BACK faces seed rays — that keeps the march
            // alive when the near plane clips the box front.
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_SdfTex);
            SAMPLER(sampler_SdfTex);
            float4x4 _WorldToSdfUVW;
            float3 _BoxMinUVW;
            float3 _BoxMaxUVW;
            float3 _SdfInvRes;      // one texel in uvw, per axis (bake grid may be anisotropic)
            float4 _ShellColor;
            float _ShellOpacity;
            float _MinStepWorld;    // sphere-trace floor: half a bake cell keeps the march advancing near the shell
            float _SurfaceEps;      // hit threshold: quarter cell reads as the effective solver wall

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            float SampleSdf(float3 uvw)
            {
                return SAMPLE_TEXTURE3D_LOD(_SdfTex, sampler_SdfTex, uvw, 0.0).r;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // The baked SDF stores TRUE world-unit distances (per-axis metric), so classic sphere tracing
                // applies: positions are transformed to uvw only for sampling, while the ray parameter stays in
                // world units. Handles both perspective and orthographic (scene-view iso) cameras.
                float3 originWS = _WorldSpaceCameraPos;
                float3 directionWS = input.positionWS - originWS;
                if (unity_OrthoParams.w > 0.5)
                {
                    float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
                    directionWS = cameraForward;
                    originWS = input.positionWS - cameraForward * 10000.0;
                }
                directionWS = normalize(directionWS);
                float3 o = mul(_WorldToSdfUVW, float4(originWS, 1.0)).xyz;
                float3 d = mul((float3x3)_WorldToSdfUVW, directionWS);

                // Slab-clip the ray to the preview window (uvw space, world-unit parameter).
                float3 safeD = lerp(max(d, 1e-8), min(d, -1e-8), step(d, 0.0));
                float3 tA = (_BoxMinUVW - o) / safeD;
                float3 tB = (_BoxMaxUVW - o) / safeD;
                float3 tLo = min(tA, tB);
                float3 tHi = max(tA, tB);
                float tEnter = max(max(tLo.x, tLo.y), max(tLo.z, 0.0));
                float tExit = min(tHi.x, min(tHi.y, tHi.z));
                if (tExit <= tEnter) discard;

                // The hit threshold EQUALS the step floor: any smaller threshold leaves a gap the forced step can
                // hop across on grazing rays, which rendered as alternating hit/miss bands along near-tangent
                // faces. Near-hits fade out by approach distance below, so the grazing zone reads as a soft edge
                // instead of stripes.
                float t = tEnter;
                float hitDistance = 0.0;
                bool hit = false;
                [loop] for (int i = 0; i < 64; i++)
                {
                    if (t > tExit) break;
                    float s = SampleSdf(o + d * t);
                    if (s <= _SurfaceEps) { hit = true; hitDistance = s; break; }
                    t += max(s, _MinStepWorld);
                }
                if (!hit) discard;

                // SDF-gradient normal (∇_world f = Mᵀ ∇_uvw f) → simple facing/rim shading so the voxelized
                // shell's shape reads clearly against the authored mesh underneath.
                float3 p = o + d * t;
                float3 grad = float3(
                    SampleSdf(p + float3(_SdfInvRes.x, 0.0, 0.0)) - SampleSdf(p - float3(_SdfInvRes.x, 0.0, 0.0)),
                    SampleSdf(p + float3(0.0, _SdfInvRes.y, 0.0)) - SampleSdf(p - float3(0.0, _SdfInvRes.y, 0.0)),
                    SampleSdf(p + float3(0.0, 0.0, _SdfInvRes.z)) - SampleSdf(p - float3(0.0, 0.0, _SdfInvRes.z)));
                float3 normal = normalize(mul(grad, (float3x3)_WorldToSdfUVW) + 1e-6);
                float facing = saturate(dot(normal, -directionWS));
                float rim = 1.0 - facing;
                // 1 at/inside the true surface, 0 at the outer edge of the near-hit band → grazing silhouettes
                // fade smoothly instead of banding.
                float edgeFade = saturate((_SurfaceEps - hitDistance) / max(_SurfaceEps, 1e-5));
                float3 color = _ShellColor.rgb * (0.3 + 0.7 * facing);
                float alpha = saturate(_ShellOpacity * (0.45 + 0.55 * rim)) * edgeFade;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
