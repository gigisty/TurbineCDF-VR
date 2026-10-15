// BrazeFX slice cast-shadow (Hidden/BrazeFX/SliceCastShadow).
// Casts the volume's shadow onto opaque scene geometry.
// Reuses the baked self-shadow transmittance volume (_ShadowTex): for each scene
// pixel we find where its ray toward the light enters the container and read the
// single pre-integrated transmittance value there. No per-pixel light marching,
// no extra textures -- one multiply over the projected shadow footprint.
Shader "Hidden/BrazeFX/SliceCastShadow"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BrazeSliceCastShadow"
            ZTest Always ZWrite Off Cull Off
            Blend DstColor Zero          // multiplicative: scene *= shadow

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_ShadowTex);    SAMPLER(sampler_ShadowTex);
            TEXTURE2D_X(_PyroDepth);  SAMPLER(sampler_PyroDepth);

            float4x4 _WorldToSim;
            float3   _SunDirWorld;        // normalized, pointing TOWARD the light
            // Frame-constant light geometry, CPU-computed with the exact math this shader used per pixel:
            float3   _CastSunDirSim;      // normalize(mul((float3x3)_WorldToSim, _SunDirWorld))
            float3   _CastInvD;           // 1 / (sign(d)·max(|d|, 1e-6)) — unit-box slab reciprocals
            float3   _CastBasis1;         // filter-tap basis ⟂ to the light
            float3   _CastBasis2;
            float    _CastShadowStrength;
            float    _CastShadowFade;     // uvw-space distance over which the shadow fades out
            float    _CastShadowSoftness; // filter radius in shadow-volume texels
            float3   _ShadowInvRes;       // 1 / shadow volume resolution
            float4   _ShadowRect;         // true bottom-left screen UV: xy + zw
            float3   _PyroCamPos;
            float3   _PyroCamRight;
            float3   _PyroCamUp;
            float3   _PyroCamFwd;
            float2   _PyroCamTanHalfFov;  // perspective: tan(halfFov); ortho: world half-extents
            float    _PyroOrtho;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 rayUV : TEXCOORD0;      // true bottom-left UV, for reconstruction
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                const float2 kQuad[6] =
                {
                    float2(0, 0), float2(0, 1), float2(1, 1),
                    float2(0, 0), float2(1, 1), float2(1, 0)
                };
                float2 corner = kQuad[input.vertexID];
                float2 sp = _ShadowRect.xy + corner * _ShadowRect.zw; // true bottom-left UV

                float4 pos = float4(sp * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    pos.y = -pos.y;
                #endif
                o.positionCS = pos;
                o.rayUV = sp;
                return o;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Depth of THIS physical pixel. The camera depth texture shares the
                // color target's orientation (same camera, same projection flip), so
                // SV_Position maps straight to the depth texel. Deriving the UV from
                // rayUV instead double-flips on top-origin targets and mirrors the
                // lookup: ground pixels read sky, hit the isSky early-out, and the
                // whole pass silently outputs 1 (no shadow).
                float2 depthUV = input.positionCS.xy / _ScaledScreenParams.xy;
                float rawDepth = SAMPLE_TEXTURE2D_X_LOD(_PyroDepth, sampler_PyroDepth, depthUV, 0).r;
                #if UNITY_REVERSED_Z
                    bool isSky = rawDepth <= 1e-7;
                #else
                    bool isSky = rawDepth >= 1.0 - 1e-7;
                #endif
                if (isSky)
                    return 1.0;   // no geometry here -> nothing to shadow

                float sceneZ;
                if (_PyroOrtho > 0.5)
                {
                    float d01 = rawDepth;
                    #if UNITY_REVERSED_Z
                        d01 = 1.0 - d01;
                    #endif
                    sceneZ = lerp(_ProjectionParams.y, _ProjectionParams.z, d01);
                }
                else
                {
                    sceneZ = LinearEyeDepth(rawDepth, _ZBufferParams);
                }

                float2 ndc = input.rayUV * 2.0 - 1.0;
                float3 ray;
                float3 originWS;
                if (_PyroOrtho > 0.5)
                {
                    ray = _PyroCamFwd;
                    originWS = _PyroCamPos
                             + ndc.x * _PyroCamTanHalfFov.x * _PyroCamRight
                             + ndc.y * _PyroCamTanHalfFov.y * _PyroCamUp;
                }
                else
                {
                    ray = _PyroCamFwd
                        + ndc.x * _PyroCamTanHalfFov.x * _PyroCamRight
                        + ndc.y * _PyroCamTanHalfFov.y * _PyroCamUp;
                    originWS = _PyroCamPos;
                }
                float3 worldPos = originWS + ray * sceneZ;

                // Scene point in container uvw, and the toward-light direction there.
                // The light direction, its slab reciprocals, and the tap basis are frame constants —
                // computed once on the CPU (identical math) instead of per shadow pixel.
                float3 o = mul(_WorldToSim, float4(worldPos, 1.0)).xyz;
                float3 d = _CastSunDirSim;

                // Intersect (o + t*d) toward the light with the unit box.
                float3 invd = _CastInvD;
                float3 t0 = (0.0 - o) * invd;
                float3 t1 = (1.0 - o) * invd;
                float3 tmin = min(t0, t1), tmax = max(t0, t1);
                float tNear = max(max(tmin.x, tmin.y), tmin.z);
                float tFar  = min(min(tmax.x, tmax.y), tmax.z);

                bool inside = all(o >= 0.0) && all(o <= 1.0);
                if (tFar < max(tNear, 0.0))
                    return 1.0;   // path to the light never crosses the volume

                // Inside: sample here. Outside/below: sample the entry point -- the
                // segment before it is empty air, so entry transmittance == the full
                // occlusion toward the light.
                float tSample = inside ? 0.0 : max(tNear, 0.0);
                float3 uvw = clamp(o + d * tSample, 0.0, 1.0);

                // 4-tap rotated-grid filter in the plane perpendicular to the
                // light. A single tap projects the bake's raw voxel columns onto
                // flat receivers as a hard mosaic; a small fixed light-space
                // pattern reads as a stable penumbra instead. Radius grows with
                // receiver distance like a real sun shadow. Offsets are constant
                // -- no screen or temporal noise.
                float3 b1 = _CastBasis1;
                float3 b2 = _CastBasis2;
                float3 r = _ShadowInvRes * (_CastShadowSoftness * (1.0 + 2.0 * tSample));
                const float2 kTaps[4] =
                {
                    float2(-0.71, 0.0), float2(0.71, 0.0),
                    float2(0.0, -0.71), float2(0.0, 0.71)
                };
                float shadowT = 0.0;
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float3 tapUVW = clamp(uvw + (kTaps[i].x * b1 + kTaps[i].y * b2) * r, 0.0, 1.0);
                    shadowT += SAMPLE_TEXTURE3D_LOD(_ShadowTex, sampler_ShadowTex, tapUVW, 0).r;
                }
                shadowT *= 0.25;

                float s = lerp(1.0, shadowT, _CastShadowStrength);

                // Distance fade: smoke shadows soften and disappear away from the
                // volume rather than projecting infinitely at full strength like a
                // solid caster. tSample is the uvw distance from receiver to volume.
                float distFade = saturate(1.0 - tSample / max(_CastShadowFade, 1e-3));
                s = lerp(1.0, s, distFade);
                return float4(s, s, s, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
