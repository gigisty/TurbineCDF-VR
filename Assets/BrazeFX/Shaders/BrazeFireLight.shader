// BrazeFX — Fire as a real scene light.
// A fullscreen ADDITIVE pass that injects the volume's fire emission onto opaque scene geometry, so a flame
// actually lights the room/characters around it (a dragon in a dark room lights up when it breathes fire).
// Per opaque pixel: reconstruct world position from depth, derive a geometric normal, then VOLUMETRICALLY
// MARCH from the surface toward the live fire centroid through the fluid field — accumulating fire emission
// weighted by inverse-square distance, attenuated by smoke extinction along the path (the volume self-occludes
// its own cast light). The fire centroid + power come from the solver's FireMoments buffer (zero CPU readback),
// so the light tracks the real flame frame-accurate. Emission colour/amount mirror BrazeRaymarch exactly, so
// the cast light matches the rendered flame.
Shader "BrazeFX/FireLight"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BrazeFireLight"
            // Additive onto the (HDR) scene colour: the fire only ever ADDS light.
            Blend One One
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // ── Fluid field + fire transfer (bound by BrazeContainer.ApplyFireLight, same data the raymarch uses) ──
            TEXTURE3D(_BrazeFireField);   SAMPLER(sampler_BrazeFireField);   // base field: .x density .y temp .w burn
            TEXTURE2D(_FirePreint);       SAMPLER(sampler_FirePreint);       // fire colour×gain LUT (front,back temp)
            TEXTURE2D(_FireCurve);        SAMPLER(sampler_FireCurve);        // 1-D art curve on the normalised amount
            StructuredBuffer<uint> _BrazeFireMoments;   // [0]=Σw, [1..3]=Σ(w·uvw), fixed-point ×FIRE_MOMENT_SCALE

            float4x4 _BrazeWorldToGridUVW;   // world → [0,1] grid uvw (sample the field)
            float4x4 _BrazeFireGridToWorld;  // [0,1] grid uvw → world (place the centroid)

            float  _FireScale;        // fire emission intensity (matches raymarch _FireScale)
            float  _FireTempScale;    // "Heat" → colour hue (matches raymarch _FireTempScale)
            float3 _BrazeFireColor;   // user tint on the cast light (1 = pure fire colour)
            float  _BrazeFireIntensity;
            float  _BrazeFireRange;   // max march distance (world units)
            float  _BrazeFireSoft2;   // inverse-square softening radius² (kills the singularity near the flame)
            float  _BrazeFireShadow;  // smoke extinction strength along the march (volume self-occlusion)
            int    _BrazeFireSteps;   // march samples

            #define FIRE_MOMENT_SCALE 1024.0   // must match BrazeSolver.compute
            #define HEAT_GAIN 0.5              // must match BrazeRaymarch.shader
            #define BURN_GAIN 0.4

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return o;
            }

            // Fire emission radiance at a field sample — replicates BrazeRaymarch: hue from temperature (unit-
            // luminance so Heat is pure colour), amount from burn through the Fire Curve, scaled by Fire Intensity.
            float3 FireEmission(float temp, float burn)
            {
                float amt = 1.0 - exp(-max(burn, 0.0) * BURN_GAIN);
                amt = SAMPLE_TEXTURE2D_LOD(_FireCurve, sampler_FireCurve, float2(saturate(amt), 0.5), 0).x;
                if (amt <= 1e-4) return 0.0;
                float t = 1.0 - exp(-max(temp, 0.0) * _FireTempScale * HEAT_GAIN);
                float3 col = SAMPLE_TEXTURE2D_LOD(_FirePreint, sampler_FirePreint, float2(t, t), 0).xyz;   // colour×gain
                float luma = max(dot(col, float3(0.2126, 0.7152, 0.0722)), 1e-4);
                return (col / luma) * (_FireScale * amt);   // unit-luminance hue × intensity
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Total fire power gates the whole effect — no fire burning → no light (dark room).
                float power = _BrazeFireMoments[0] / FIRE_MOMENT_SCALE;
                if (power < 0.05) return 0.0;

                float2 uv = input.uv;
                float rawDepth = SampleSceneDepth(uv);
                // Skip the skybox / far plane (nothing to light).
            #if UNITY_REVERSED_Z
                if (rawDepth <= 1e-7) return 0.0;
            #else
                if (rawDepth >= 1.0 - 1e-7) return 0.0;
            #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                // Geometric normal from world-position derivatives; flip to face the camera so winding is irrelevant.
                float3 N = normalize(cross(ddy(worldPos), ddx(worldPos)));
                float3 V = _WorldSpaceCameraPos - worldPos;
                if (dot(N, V) < 0.0) N = -N;

                // Fire centroid (normalised grid space → world). Σw>0 already implied by power>0.
                float invW = 1.0 / max((float)_BrazeFireMoments[0], 1.0);
                float3 cuvw = float3(_BrazeFireMoments[1], _BrazeFireMoments[2], _BrazeFireMoments[3]) * invW;
                float3 fireWorld = mul(_BrazeFireGridToWorld, float4(cuvw, 1.0)).xyz;

                float3 toFire = fireWorld - worldPos;
                float dist = length(toFire);
                float3 L = toFire / max(dist, 1e-4);
                float nl = saturate(dot(N, L) * 0.5 + 0.5);   // half-lambert wrap → soft falloff toward the terminator
                if (nl <= 1e-3) return 0.0;

                // March from the surface toward the flame, integrating emission / r² with smoke extinction.
                int steps = max(_BrazeFireSteps, 1);
                float marchLen = min(dist + 1e-3, _BrazeFireRange);
                float stepLen = marchLen / steps;
                float3 accum = 0.0;
                float T = 1.0;
                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float t = (i + 0.5) * stepLen;
                    float3 x = worldPos + L * t;
                    float3 suvw = mul(_BrazeWorldToGridUVW, float4(x, 1.0)).xyz;
                    if (any(suvw < 0.0) || any(suvw > 1.0)) continue;

                    float4 f = SAMPLE_TEXTURE3D_LOD(_BrazeFireField, sampler_BrazeFireField, suvw, 0);
                    float3 emit = FireEmission(f.y, f.w);
                    float d2 = max(t * t, _BrazeFireSoft2);
                    accum += T * emit / d2;
                    // Smoke (density) between the surface and the flame occludes the cast light.
                    T *= exp(-max(f.x, 0.0) * _BrazeFireShadow * stepLen);
                    if (T < 0.02) break;
                }

                float3 outRgb = accum * stepLen * nl * _BrazeFireIntensity * _BrazeFireColor;
                return float4(max(outRgb, 0.0), 0.0);   // Blend One One → adds light to scene colour
            }
            ENDHLSL
        }

        // Pass 1 — Volume → scene SHADOW. The opposite of Pass 0: instead of the fire adding light, the
        // smoke/fire DENSITY blocks the key light reaching opaque geometry, so the ground/objects under the
        // plume fall into a soft shadow. Per opaque pixel, march from the surface TOWARD the light through the
        // density field, accumulate extinction → transmittance, and MULTIPLY the scene colour by it. The march
        // is slab-clipped to the grid box so pixels not under the volume cost almost nothing (and the shadow
        // tracks the live plume). Multiplies total scene colour (a v1 approximation — it also dims ambient a
        // touch under thick smoke, which reads fine).
        Pass
        {
            Name "BrazeVolumeShadow"
            Blend DstColor Zero        // result.rgb = scene.rgb × shadowFactor
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_BrazeFireField);   SAMPLER(sampler_BrazeFireField);   // base field: .x density
            TEXTURE3D(_BrazeShadowBakedTex); SAMPLER(sampler_BrazeShadowBakedTex); // generated: .z cast T
            float4x4 _BrazeWorldToGridUVW;   // world → [0,1] grid uvw

            float3 _BrazeShadowLightDirUVW;  // direction TOWARD the key light, in grid-uvw space (normalized)
            float  _BrazeShadowStrength;     // 0 = no shadow, 1 = full extinction shadow
            float  _BrazeShadowDensity;      // smoke extinction strength along the march
            int    _BrazeShadowSteps;        // march samples across the grid span
            float  _BrazeShadowUseBaked;     // generated static volume: one baked transmittance tap

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return o;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float rawDepth = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                if (rawDepth <= 1e-7) return 1.0;   // skybox → no shadow
            #else
                if (rawDepth >= 1.0 - 1e-7) return 1.0;
            #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 p0 = mul(_BrazeWorldToGridUVW, float4(worldPos, 1.0)).xyz;   // surface in grid uvw

                // Slab-clip the ray p0 + t·dir (t≥0) to the [0,1]³ grid box: only march the segment that
                // actually passes through the volume above the surface. Misses → no shadow, near-free.
                float3 dir = _BrazeShadowLightDirUVW;
                // Sign-preserving, zero-safe reciprocal (a near-axis-aligned light → a slab dir component ~0;
                // a large finite inv reproduces the ±inf slab behaviour without a divide-by-zero).
                float3 ad = max(abs(dir), 1e-5);
                float3 inv = 1.0 / float3(dir.x < 0 ? -ad.x : ad.x,
                                          dir.y < 0 ? -ad.y : ad.y,
                                          dir.z < 0 ? -ad.z : ad.z);
                float3 ta = (0.0 - p0) * inv;
                float3 tb = (1.0 - p0) * inv;
                float3 tmin = min(ta, tb), tmax = max(ta, tb);
                float enter = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                float exit  = min(min(tmax.x, tmax.y), tmax.z);
                if (exit <= enter) return 1.0;   // ray toward the light never crosses the volume

                // Generated volumes integrated this light ray during their static high-quality bake. Sample just
                // inside the entry point; .z is transmittance through the remaining segment toward the key light.
                // Dynamic live containers keep the density-march fallback below.
                [branch] if (_BrazeShadowUseBaked > 0.5)
                {
                    float3 entryUVW = saturate(p0 + dir * (enter + 1e-4));
                    float T = saturate(SAMPLE_TEXTURE3D_LOD(
                        _BrazeShadowBakedTex, sampler_BrazeShadowBakedTex, entryUVW, 0).z);
                    float bakedShadow = 1.0 - saturate(_BrazeShadowStrength) * (1.0 - T);
                    return float4(bakedShadow, bakedShadow, bakedShadow, 1.0);
                }

                int steps = max(_BrazeShadowSteps, 1);
                float stepLen = (exit - enter) / steps;
                float T = 1.0;
                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float t = enter + (i + 0.5) * stepLen;
                    float3 suvw = p0 + dir * t;
                    float dens = max(SAMPLE_TEXTURE3D_LOD(_BrazeFireField, sampler_BrazeFireField, suvw, 0).x, 0.0);
                    T *= exp(-dens * _BrazeShadowDensity * stepLen);
                    if (T < 0.02) break;
                }

                float shadow = 1.0 - saturate(_BrazeShadowStrength) * (1.0 - T);
                return float4(shadow, shadow, shadow, 1.0);   // Blend DstColor Zero → scene × shadow
            }
            ENDHLSL
        }
    }
    Fallback Off
}
