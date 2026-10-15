// Pipeline-neutral BrazeFX volume integration. The URP wrapper provides its pipeline includes
// first, then includes this file. Keep all smoke/fire/raymarch logic here.
#ifndef BRAZEFX_SLICES_CORE_INCLUDED
#define BRAZEFX_SLICES_CORE_INCLUDED

#include "BrazeInterleave.hlsl"

            TEXTURE3D(_State);        SAMPLER(sampler_State);
            TEXTURE3D(_FineVolume);   SAMPLER(sampler_FineVolume);   // Render Detail 2×/4× field: R=density, G=burn (final)
            float3 _FineInvRes;       // 1 / fine-field resolution (base×2 or ×4) — for the fine tap's own quintic de-facet
            TEXTURE2D(_SmokePreint);  SAMPLER(sampler_SmokePreint);  // old smoke TF: rgb albedo, a extinction
            TEXTURE2D(_FirePreint);   SAMPLER(sampler_FirePreint);   // old fire TF: rgb colour×gain, a amount
            TEXTURE2D(_FireCurve);    SAMPLER(sampler_FireCurve);    // old fire amount response curve
            TEXTURE3D(_WarpVolume);   SAMPLER(sampler_WarpVolume);   // restored Render Detail displacement field
            float  _WarpSamplePhase;  // procedural UV advance since the last sparse warp bake
            TEXTURE3D(_VelocityTex);   SAMPLER(sampler_VelocityTex);  // solver velocity in voxels/second, for fluid-following jitter
            float  _UseVelocityJitter;
            float  _VelocityJitterScroll; // reconstructed VDB motion reprojects history but must not scroll stochastic ray noise
            float3 _VelocityInvRes;
            TEXTURE2D_X(_PyroDepth);  SAMPLER(sampler_PyroDepth);
            TEXTURE3D(_ShadowTex);    SAMPLER(sampler_ShadowTex);
            TEXTURE3D(_SculptTex);    SAMPLER(sampler_SculptTex);   // baked Definition sculpt: r = ndl, g = gradient weight
            TEXTURE3D(_OccTex);       SAMPLER(sampler_OccTex);   // max visibility/fire, mean visibility, combined variation
            float  _UseOccupancy;     // 1 = skip empty cells, 0 = march every step
            float3 _OccInvRes;        // 1 / occupancy resolution
            float3 _OccCoordScale;    // base-grid resolution / 4: exact normalized UVW -> sparse-brick coordinate
            float  _AdaptiveMarching;
            float  _AdaptiveMaxStep;
            float  _AdaptiveFireMaxStep;
            float  _AdaptiveVarianceThreshold;
            float _ShadowAmbientAmount; // how much the shadow also darkens ambient + fill
            float3 _SmokeFill;        // artist fill light for smoke (linear, pre-scaled)

            // BrazeFX field adaptation — the ONLY departure from the froxel original.
            float _StateDensityGain;  // Braze density (~0..0.3) -> froxel smoke domain (0..3). Default 10.
            float _StateFireGain;     // rate of the saturating emission/burn -> fire-amount response. Default 0.5.
#if defined(_BRAZE_SMOKE)
            #define BRAZE_SMOKE_ON 1
#else
            #define BRAZE_SMOKE_ON 0
#endif
#if defined(_BRAZE_FIRE)
            #define BRAZE_FIRE_ON 1
#else
            #define BRAZE_FIRE_ON 0
#endif
#if defined(_BRAZE_SELF_SHADOW)
            #define BRAZE_SELF_SHADOW_ON 1
#else
            #define BRAZE_SELF_SHADOW_ON 0
#endif
#if defined(_BRAZE_FILL)
            #define BRAZE_FILL_ON 1
#else
            #define BRAZE_FILL_ON 0
#endif
            float _SmokeDensityScale;
            float _SmokeBright;
            float _SmokeContrast;
            float _Opacity;
            float _FireScaleOld;
            float _FireDensityScale;
            float _FireTempScaleOld;
            float _Illum;
            float _ScatterAmt;
            float _Definition;
            float _FireGlow;
            float3 _FireLightColor;

            #define DENSITY_REMAP 3.0
            #define HEAT_GAIN 0.5

            int    _SliceCount;
            int    _SliceLayerMode;  // 0 = both, 1 = real slices only, 2 = gap fill only
            float  _SliceStep;       // view-depth step in world units, derived from voxel pitch; stable across camera angle
            float4 _VolumeRect;
            float2 _VolumeZ;
            // Pixel size of the CURRENT render target. _ScaledScreenParams tracks the CAMERA target, which is
            // wrong when the march renders into the downsampled offscreen buffer — SV_Position is in target
            // pixels, so screen-space math must divide by the target's own size.
            float2 _SliceTargetSize;
            // Forward resolutions alongside the inverses: SmoothVoxelUVWRes used to reconstruct res as
            // 1.0/invRes — avoiding reciprocals in the remaining fine/velocity helper taps.
            float3 _FineResF;
            float3 _VelocityResF;

            float3 _PyroCamPos;
            float3 _PyroCamRight;
            float3 _PyroCamUp;
            float3 _PyroCamFwd;
            float2 _PyroCamTanHalfFov;   // perspective: tan(halfFov); ortho: world half-extents
            float  _PyroOrtho;           // 1 = orthographic camera, 0 = perspective
            float4x4 _WorldToSim;
            float4x4 _SimToWorld;

            float3 _SunColor;
            float3 _SunDirWorld;
            float3 _AmbientColor;
            float  _PhaseG;
            float  _EdgeFade;
            float  _MultiScatter;
            float  _SelfShadowStrength;
            float  _SelfShadowDepthScale;
            float  _PyroTime;
            float3 _SimSize;
            float  _SourceFilterStrength;
            float  _SourceFilterRadius;
            float  _DensityContrast;
            float  _DepthSoftness;    // metres over which the volume fades into geometry
            float  _JitterStrength;   // 0..1 fraction of a march step to randomize the ray start (anti-staircase)
            float  _JitterScale;      // spatial frequency of the fixed ray-start dither
            float  _JitterPyroFill;   // repairs partial-opacity jitter holes with smoke/fire colour carried by this ray
            float  _JitterDenoise;    // post-march coverage repair for alpha/transmittance speckles; does not reduce jitter
            float  _VirtualFill;      // budget-driven gap fill strength: 0 at native step, >0 when slice cap stretches it
            float  _VirtualFillSharpness; // max-density bias for sparse smoke gap fill
            float  _DebugView;        // 0 normal, 1 raw smoke, 2 shaped smoke, 3 temperature, 4 occupancy, 5 gap fill, 6 self-shadow, 7 CFD heat map
            float  _CfdHeatMin;       // ambient-relative solver heat at the transparent threshold
            float  _CfdHeatInvRange;  // inverse solver-heat span from cold to hot
            float  _CfdHeatOpacity;
            float3 _CfdHeatCold;
            float3 _CfdHeatMiddle;
            float3 _CfdHeatHot;
            float _CfdSmokeTemperature; // 1 = visualize the solved fluid-temperature field directly
            float _CfdSmokeFlowSpeed;   // 1 = colour transported CFD dye by normalized solved velocity magnitude
            float _CfdSmokeColorContrast;
            float _CfdSmokeFlowEmphasis; // 0..1: thins nominal-freestream dye so wake/acceleration keep optical depth
            float _CfdSmokeDiagnostic;  // 1 = bright presentation dye, independent of scene-light darkness

            // Native temporal interleaving. Motion is current-screen -> previous-screen UV. Live simulation adds
            // a one-tap fluid backtrace at the ray's visible-opacity centroid; cache playback is camera-only.
            float4x4 _BrazePrevVP;
            float _BrazeReproject;
            float _BrazeFluidReprojection;
            float _BrazeSliceDeltaTime;
            int _BrazeInterleaveMode;
            int _BrazeInterleavePhase;
            int _BrazeJitterSampleIndex;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 rayUV : TEXCOORD2;       // true (bottom-left) screen UV, _VolumeRect space
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;

                // Draw a quad (2 triangles, 6 verts) that covers ONLY the volume's
                // screen-space AABB (_VolumeRect), not the whole framebuffer. The
                // container usually fills a fraction of the screen, so a full-screen
                // triangle would shade (and discard) millions of pixels the fire
                // never touches. Drawn as a SINGLE instance now -- the march happens
                // inside the fragment, not across slice instances.
                const float2 kQuad[6] =
                {
                    float2(0, 0), float2(0, 1), float2(1, 1),
                    float2(0, 0), float2(1, 1), float2(1, 0)
                };
                float2 corner = kQuad[input.vertexID];
                float2 screenPos = _VolumeRect.xy + corner * _VolumeRect.zw; // true bottom-left-origin UV

                // A full-screen triangle can output raw clip Y unflipped because a
                // vertical mirror still covers every pixel. This fitted quad cannot:
                // on UNITY_UV_STARTS_AT_TOP platforms (e.g. Metal) the NDC->framebuffer
                // Y flip would mirror the covered rect and feed rayUV in upside-down,
                // rendering the volume vertically inverted. Cancel that flip here so
                // screenPos maps to the true physical pixel and rayUV stays upright.
                float4 pos = float4(screenPos * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    pos.y = -pos.y;
                #endif
                o.positionCS = pos;
                o.rayUV = screenPos;   // true bottom-left UV: reconstruction + cull
                return o;
            }

            float2 BrazeWorldToScreenUV(float3 posWS, float4x4 vp)
            {
                float4 cs = mul(vp, float4(posWS, 1.0));
                float w = (abs(cs.w) < 1e-6) ? 1e-6 : cs.w;
                return (float2(cs.x, cs.y * _ProjectionParams.x) + w) * 0.5 / w;
            }

            bool BrazeInterleavePixelValid(int2 pixel)
            {
                // Coherent work tiles let inactive fragment waves skip the entire latency-bound march.
                return BrazeCoherentInterleavePixelValid(
                    pixel, _BrazeInterleaveMode, _BrazeInterleavePhase);
            }

            // BrazeFX field -> slice renderer convention. Keep temperature and burn as distinct signals:
            // temperature chooses the fire colour, while burn/emission chooses its amount. Density is the only
            // channel that needs a domain conversion before entering the smoke transfer function.
            float4 MapState(float4 s)
            {
                return float4(max(s.x, 0.0) * _StateDensityGain,
                              max(s.y, 0.0), max(s.z, 0.0), max(s.w, 0.0));
            }

            float CfdColorCoordinate(float value)
            {
                // Sigmoid contrast, not a linear stretch: the linear form clips to flat saturated bands past
                // |value-0.5| > 0.5/contrast, so raising Color Contrast destroyed exactly the turbulent detail it
                // was meant to reveal. tanh expands the same central slope while compressing the tails smoothly.
                float k = max(_CfdSmokeColorContrast, 0.5);
                float s = saturate(value) * 2.0 - 1.0;
                if (k <= 1.001) return saturate(s * 0.5 * k + 0.5);
                float c = sqrt(3.0 * (k - 1.0)); // centre slope ~= k for the whole 1..6 slider range
                return 0.5 + 0.5 * tanh(c * s) / tanh(c);
            }

            // Vivid engineering false colour in linear HDR space. It intentionally shares the Analysis Slice's
            // blue->cyan->green->yellow->red semantics so 2D and 3D views communicate the same field ordering.
            float3 CfdSpectrum(float value)
            {
                float u = CfdColorCoordinate(value);
                float3 c0 = float3(0.005, 0.015, 1.00);
                float3 c1 = float3(0.00, 0.92, 1.00);
                float3 c2 = float3(0.01, 1.00, 0.08);
                float3 c3 = float3(1.00, 0.88, 0.00);
                float3 c4 = float3(1.00, 0.005, 0.00);
                if (u < 0.25) return lerp(c0, c1, u * 4.0);
                if (u < 0.50) return lerp(c1, c2, (u - 0.25) * 4.0);
                if (u < 0.75) return lerp(c2, c3, (u - 0.50) * 4.0);
                return lerp(c3, c4, (u - 0.75) * 4.0);
            }

            float4 SmokeTF(float dFront, float dBack)
            {
                return SAMPLE_TEXTURE2D_LOD(_SmokePreint, sampler_SmokePreint, float2(dFront, dBack), 0);
            }

            float4 FireTF(float tFront, float tBack)
            {
                return SAMPLE_TEXTURE2D_LOD(_FirePreint, sampler_FirePreint, float2(tFront, tBack), 0);
            }

            float SmokeCurve(float rawSmoke)
            {
                return pow(saturate(rawSmoke / 3.0), _DensityContrast) * 3.0;
            }

            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * 3.14159265 * pow(abs(1.0 + g2 - 2.0 * g * cosTheta), 1.5));
            }

            // Textureless pixel hash in [0,1). The march starts every ray at the SAME
            // phase (tEnter + 0.5·step), so adjacent pixels sample the SAME depth planes;
            // where a shared plane crosses a density/fire iso-surface it reads as a hard
            // step -- the "staircase" / wood-grain banding, on both thick plumes AND the
            // fine detail's iso-shells. Offsetting each ray's start by a per-pixel fraction
            // of one step decorrelates those planes. Screen-space interleaved-gradient noise
            // made that grain line up into diagonal CRT bands; this integer-cell hash avoids
            // the long lattice and can be shifted by fluid velocity only where fume exists. Use an integer
            // avalanche instead of the former float-frac hash: high Jitter Scale values sampled that hash on a
            // correlated stride and bright emission exposed its broad mottling.
            uint BrazeHash32(uint value)
            {
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return value;
            }

            float BrazePixelHash(float2 p)
            {
                uint2 cell = asuint(int2(floor(p)));
                uint seed = cell.x * 0x8da6b343u ^ cell.y * 0xd8163841u;
                return (float)(BrazeHash32(seed) >> 8) * (1.0 / 16777216.0);
            }

            float3 BrazeHash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac(float3((p.x + p.y) * p.z,
                                   (p.x + p.z) * p.y,
                                   (p.y + p.z) * p.x));
            }

            float3 VoxelBreakupUVW(float3 uvw, float3 invRes, float gate, float salt)
            {
                float strength = saturate(_SourceFilterStrength) * saturate(gate);
                float3 cell = floor(saturate(uvw) / max(invRes, float3(1e-6, 1e-6, 1e-6)));
                float3 h = BrazeHash33(cell + salt) - 0.5;
                return saturate(uvw + h * invRes * strength * max(_SourceFilterRadius, 0.5));
            }

            // Quintic coordinate reconstruction is retained only for vector/helper fields where a zero gradient
            // at texel centres is useful. Do not use it for density, transmittance, or sculpt: its broad flat
            // shoulders turn voxel centres into visible cardboard terraces in thin/wide smoke. Native trilinear
            // keeps those scalar transitions monotonic and costs less while retaining the same one texture tap.
            float3 SmoothVoxelUVWRes(float3 uvw, float3 invRes, float3 res)
            {
                float3 p = uvw * res - 0.5;
                float3 i = floor(p);
                float3 f = p - i;
                f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
                return (i + f + 0.5) * invRes;
            }
            #define WARP_DECODE 0.35
            float3 DecodeWarp(float3 e) { return (e - 0.5) * (2.0 * WARP_DECODE); }

            float3 SampleWarp(float3 uvw)
            {
                // Advancing the lookup by detail-phase / detail-frequency approximates the procedural field that
                // would have been baked this render frame. At the next N-tick bake the offset resets as the new
                // field catches up, avoiding the visible hold/jump cycle. Native trilinear sampling removes the
                // old quintic remap, so this remains one tap and is cheaper despite the phase add.
                float3 wuv = saturate(uvw + _WarpSamplePhase.xxx);
                return DecodeWarp(SAMPLE_TEXTURE3D_LOD(_WarpVolume, sampler_WarpVolume, wuv, 0).xyz);
            }

            float3 WarpUvw(float3 uvw)
            {
            #if defined(_BRAZE_USE_WARP)
                return saturate(uvw + SampleWarp(uvw));
            #else
                return uvw;
            #endif
            }

            float2 SampleFineRaw(float3 uvw)
            {
                return SAMPLE_TEXTURE3D_LOD(_FineVolume, sampler_FineVolume, SmoothVoxelUVWRes(saturate(uvw), _FineInvRes, _FineResF), 0).rg;
            }

            float2 SampleFineBreakup(float3 uvw, float gate)
            {
                float strength = saturate(_SourceFilterStrength) * saturate(gate);
                float2 sample = SampleFineRaw(uvw);
                [branch] if (strength > 1e-4)
                    sample = SampleFineRaw(VoxelBreakupUVW(uvw, _FineInvRes, gate, 17.31));
                return sample;
            }

            float SampleShadowRaw(float3 uvw)
            {
                // Native trilinear reconstruction is intentionally used for transmittance. The quintic texel
                // remap used by scalar fields creates broad flat plateaus at shadow-voxel centres; on a half-res
                // shadow volume those plateaus become the visible blocks this lookup is meant to hide.
                return SAMPLE_TEXTURE3D_LOD(_ShadowTex, sampler_ShadowTex, saturate(uvw), 0).r;
            }

            float3 OccupancyPosition(float3 uvw)
            {
                // The occupancy grid is ceil(baseRes / 4). Sampling it at raw normalized UVW shifts brick
                // boundaries whenever a grid axis is not divisible by four (175 and 250 both are not), allowing
                // a neighbouring empty brick to reject visible state. Scaling by the ACTUAL base/brick span
                // aligns the point-filtered texel boundaries exactly; no floor or extra texture tap is needed.
                return saturate(uvw) * _OccCoordScale;
            }

            float SampleOccupancyPosition(float3 occPosition)
            {
                return SAMPLE_TEXTURE3D_LOD(_OccTex, sampler_OccTex,
                                            saturate(occPosition * _OccInvRes), 0).r;
            }

            float4 SampleOccupancyStatsPosition(float3 occPosition)
            {
                return SAMPLE_TEXTURE3D_LOD(_OccTex, sampler_OccTex,
                                            saturate(occPosition * _OccInvRes), 0);
            }

            float SampleOccupancy(float3 uvw)
            {
                return SampleOccupancyPosition(OccupancyPosition(uvw));
            }

            float4 SampleStateRaw(float3 uvw)
            {
            #if defined(_BRAZE_USE_FINE)
                // Legacy fine field path. The live Render Detail module now warps uvw before this call, so the
                // usual path below samples the base render field through the restored displacement volume.
                // BuildRenderField has already de-voxeled this scalar field. A second quintic coordinate remap
                // flattened every voxel centre and produced the large terraces visible in shallow plumes.
                float4 baseS = SAMPLE_TEXTURE3D_LOD(_State, sampler_State, saturate(uvw), 0);
                float smokeBody = saturate(baseS.x * _StateDensityGain * 0.6);   // 0 at wisps -> 1 in dense smoke
                float fireBody = saturate((1.0 - exp(-max(baseS.w, 0.0) * _StateFireGain)) * 1.35);
                float body = max(smokeBody, fireBody);
                float2 fine = SampleFineBreakup(uvw, body * body);

                // Bright cores should read as continuous glowing gas, not a high-frequency scalar texture.
                // Blend the core toward the de-voxeled base; leave low-density fringes mostly fine.
                float toBase = body * body * 0.55;
                float density = lerp(fine.x, baseS.x, toBase);
                float burn    = lerp(fine.y, baseS.w, toBase);
                return float4(density, baseS.y, baseS.z, burn);
            #else
                return SAMPLE_TEXTURE3D_LOD(_State, sampler_State, saturate(uvw), 0);
            #endif
            }

            // warpOffset = the CURRENT slice's already-computed warp displacement (lookupUVW - suvw). The warp
            // field is smooth at its own resolution, so over the half-step to the fill sample it is locally
            // constant to within a texel — reusing it saves the fill's own warp tap per filled sample.
            float SliceGapFillD(float3 suvw, float3 warpOffset)
            {
                float rawSmoke = max(SampleStateRaw(suvw + warpOffset).x, 0.0) * _StateDensityGain;
                float smokeD = saturate(rawSmoke * DENSITY_REMAP);
                float softD = smokeD * smokeD;
                return lerp(softD, smokeD, saturate(_VirtualFillSharpness));
            }

            float4 SampleStateFiltered(float3 uvw)
            {
                return MapState(SampleStateRaw(uvw));
            }

            void BrazeFrag(Varyings input, out float4 outColor, out float2 outMotion)
            {
                outColor = 0.0;
                outMotion = 0.0;
                // Depth of THIS physical pixel: SV_Position maps straight to the
                // depth texel because the depth texture shares the color target's
                // orientation. (A rayUV-derived UV double-flips on top-origin
                // targets and mirrors the depth lookup vertically.)
                float2 screenUV = input.positionCS.xy / _SliceTargetSize.xy;

                // Reconstruct the line of sight from the CPU-provided camera basis
                // (guaranteed consistent with _PyroCamPos) instead of a global
                // inverse-VP matrix, and handle both projections explicitly.
                // Orthographic cameras (common in the Scene view) have no single eye
                // point: reconstructing through _PyroCamPos fanned the [0,1]^3 container
                // out to fill the screen -- the "massive" look. Now each ortho pixel
                // gets a parallel ray with its own origin on the view plane.
                float2 ndc = input.rayUV * 2.0 - 1.0;
                float3 ray;        // world meters per unit view-Z along this pixel's sight line
                float3 originWS;   // ray origin in world space
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

                // rayUV is the pixel's true screen UV -- the same space _VolumeRect
                // lives in -- so this is the volume's screen-rect cull test.
                float2 fuv = (input.rayUV - _VolumeRect.xy) / max(_VolumeRect.zw, 1e-5);
                if (any(fuv < 0.0) || any(fuv > 1.0))
                    return;   // no coverage (alpha = opacity)

                float3 ro = mul(_WorldToSim, float4(originWS, 1.0)).xyz;
                float3 rd = mul((float3x3)_WorldToSim, ray);   // uvw per unit view-Z
                rd = (abs(rd) < 1e-6) ? 1e-6 : rd;             // avoid slab div-by-zero

                // Reject an inactive WORK TILE before scene depth, slab intersection, occupancy, or any march setup.
                // The fitted volume depth midpoint is sufficient for a cheap camera/fluid history estimate.
                bool fastForcedMarch = false;
                if (!BrazeInterleavePixelValid(int2(input.positionCS.xy)))
                {
                    [branch] if (_BrazeReproject > 0.5)
                    {
                        float midT = _VolumeZ.x + _VolumeZ.y * 0.5;
                        float3 midUVW = saturate(ro + rd * midT);
                        float3 previousWS = originWS + ray * midT;
                        [branch] if (_BrazeFluidReprojection > 0.5 && _UseVelocityJitter > 0.5)
                        {
                            float3 vuv = SmoothVoxelUVWRes(midUVW, _VelocityInvRes, _VelocityResF);
                            float3 velVoxPerSec = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, vuv, 0).xyz;
                            float3 velUvwPerSec = velVoxPerSec / max(_VelocityResF, float3(1.0, 1.0, 1.0));
                            previousWS -= mul((float3x3)_SimToWorld, velUvwPerSec) * _BrazeSliceDeltaTime;
                        }
                        outMotion = BrazeWorldToScreenUV(previousWS, _BrazePrevVP) - input.rayUV;
                        // Past the trust radius, reprojected history is guaranteed misaligned (single-depth motion
                        // estimate vs fast depth-varying flow): forfeit the schedule discount and march fresh. The
                        // estimate motion is kept (not overwritten by the centroid version below) so the resolve,
                        // reading this exact value, re-derives the same forfeit decision and consumes the current.
                        float2 motionPx = outMotion * _SliceTargetSize.xy;
                        if (dot(motionPx, motionPx) <=
                            BRAZE_INTERLEAVE_FAST_REPROJECT_PX * BRAZE_INTERLEAVE_FAST_REPROJECT_PX)
                            return;
                        fastForcedMarch = true;
                    }
                    else
                    {
                        return;
                    }
                }

                float rawDepth = SAMPLE_TEXTURE2D_X_LOD(_PyroDepth, sampler_PyroDepth, screenUV, 0).r;
                #if UNITY_REVERSED_Z
                    bool isSky = rawDepth <= 1e-7;
                #else
                    bool isSky = rawDepth >= 1.0 - 1e-7;
                #endif
                float sceneZ;
                if (isSky)
                {
                    sceneZ = 1e8;
                }
                else if (_PyroOrtho > 0.5)
                {
                    // Orthographic device depth is linear in [near, far]; LinearEyeDepth
                    // assumes a perspective projection and would mis-occlude here.
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

                // --- ray / box intersection in sim uvw space (the unit cube) ----
                // Reconstruct the pixel's sight line directly in uvw, then slab-clip
                // it to [0,1]^3 so we march only the span that actually passes through
                // the container (empty pixels do ~no work -- the fitted-quad win, now
                // per-pixel).
                float3 tA = (0.0 - ro) / rd, tB = (1.0 - ro) / rd;
                float3 tLo = min(tA, tB), tHi = max(tA, tB);
                float tEnter = max(max(tLo.x, tLo.y), tLo.z);
                float tExit  = min(min(tHi.x, tHi.y), tHi.z);

                // Clamp to the camera-near side of the box and to scene depth (+ a
                // softness margin so the volume dissolves into geometry, not a hard cut).
                tEnter = max(tEnter, _VolumeZ.x);
                tExit  = min(tExit, min(_VolumeZ.x + _VolumeZ.y, sceneZ + _DepthSoftness));
                if (tExit <= tEnter)
                    return;   // no coverage (alpha = opacity)

                // _SliceStep is a fixed voxel-derived view-depth step. Do NOT divide
                // the current ray span by _SliceCount: that stretches/rephases the
                // sample lattice whenever the camera angle changes, making a static
                // field look like it slides or speeds up. _SliceCount is only the
                // safety budget for the longest fitted ray.
                int maxSteps = max(_SliceCount, 1);
                float raySpan = max(tExit - tEnter, 1e-5);
                float baseStep = min(max(_SliceStep, 1e-5), raySpan); // fine step inside smoke
                float invSpan = 1.0 / max(_VolumeZ.y, 1e-4);

                // View-depth distance across one exact 4-voxel brick on each axis. The old fixed 0.9-cell leap
                // ignored where the ray currently sat inside the brick; from near a far face it jumped several
                // voxels into the next occupied brick, producing cube-shaped holes. The empty branch below uses
                // this with the current in-cell phase to stop just across the NEXT boundary instead.
                float3 occDir = rd * _OccCoordScale;
                float3 occCellT = 1.0 / max(abs(occDir), 1e-6);

                float3 accum = 0.0;   // front-to-back accumulated radiance (premultiplied)
                float T = 1.0;        // remaining transmittance toward the camera
                // Start-phase dither so neighbouring rays don't sample the same depth planes (the staircase).
                // Keep the phase on a world-space ray lattice instead of tEnter-relative: tying the first
                // sample to the moving box entry face makes the dither pattern jump as the visible face changes
                // during an orbit. Keep the non-directional static pixel hash as the base everywhere.
                // Denoise is a post-march coverage repair; it must not reduce this phase offset or the
                // staircase/wood-grain banding comes back. Velocity scrolling still only applies where real fume exists.
                float rayWorldLen = max(length(ray), 1e-5);
                float3 rayDir = ray / rayWorldLen;
                float jitterScale = max(_JitterScale, 1e-3);
                float3 jitterUVW = saturate(ro + rd * (tEnter + raySpan * 0.5));
                float jitterDenoise = saturate(_JitterDenoise);
                float jitterStrength = saturate(_JitterStrength);
                float rndPix = BrazePixelHash(input.positionCS.xy * jitterScale);
                float fumeMask = 0.0;
                [branch] if (_UseVelocityJitter > 0.5 && _VelocityJitterScroll > 0.5 && jitterStrength > 1e-4)
                {
                    float occMask = 1.0;
                    [branch] if (_UseOccupancy > 0.5)
                        occMask = SampleOccupancy(jitterUVW);

                    [branch] if (occMask > 1e-3)
                    {
                        float4 jitterState = SampleStateRaw(WarpUvw(jitterUVW));
                        float smokePresence = BRAZE_SMOKE_ON
                            ? max(jitterState.x, 0.0) * _StateDensityGain * DENSITY_REMAP : 0.0;
                        float firePresence = BRAZE_FIRE_ON
                            ? max(max(jitterState.y, 0.0) * HEAT_GAIN,
                                  max(jitterState.w, 0.0) * max(_StateFireGain, 0.0))
                            : 0.0;
                        fumeMask = max(smokePresence, firePresence) * saturate(occMask);
                    }
                    fumeMask = smoothstep(0.01, 0.18, fumeMask);

                    [branch] if (fumeMask > 1e-4)
                    {
                        [branch] if (_UseVelocityJitter > 0.5)
                        {
                            float3 vuv = SmoothVoxelUVWRes(jitterUVW, _VelocityInvRes, _VelocityResF);
                            float3 velVoxPerSec = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, vuv, 0).xyz;
                            float3 velUvwPerSec = velVoxPerSec / max(_VelocityResF, float3(1.0, 1.0, 1.0));
                            float3 velWorldPerSec = mul((float3x3)_SimToWorld, velUvwPerSec);
                            float2 velCamPerSec = float2(dot(velWorldPerSec, _PyroCamRight),
                                                         dot(velWorldPerSec, _PyroCamUp));
                            float2 halfSpan = max(abs(_PyroCamTanHalfFov), float2(1e-4, 1e-4));
                            float midViewZ = max(tEnter + raySpan * 0.5, 1e-3);
                            float2 uvPerSec = (_PyroOrtho > 0.5)
                                ? velCamPerSec / (2.0 * halfSpan)
                                : velCamPerSec / (2.0 * halfSpan * midViewZ);
                            float2 flowPixels = uvPerSec * _SliceTargetSize.xy * _PyroTime;
                            float animatedRnd = BrazePixelHash((input.positionCS.xy - flowPixels) * jitterScale);
                            rndPix = lerp(rndPix, animatedRnd, fumeMask);
                        }
                    }
                }
                // A tile receives one new raymarch every two/four frames. A golden-ratio rotation makes those
                // revisits sample different depth phases, so the history already being paid for averages jitter
                // instead of preserving a stationary bright-fire stipple. Index 0 in spatial/full-refresh modes.
                rndPix = frac(rndPix + (float)_BrazeJitterSampleIndex * 0.61803398875);
                float jitter = (rndPix - 0.5) * jitterStrength;
                float worldStep = max(baseStep * rayWorldLen, 1e-5);
                float startPhase = (0.5 + jitter) * worldStep;
                float rayOriginPhase = dot(originWS, rayDir);
                float enterPhase = rayOriginPhase + tEnter * rayWorldLen;
                float samplePhase = ceil((enterPhase - startPhase) / worldStep) * worldStep + startPhase;
                float t = (samplePhase - rayOriginPhase) / rayWorldLen;
                if (t < tEnter)
                    t += baseStep;
                float debugMax = 0.0;

                // Phase / sun geometry is constant along this pixel's sight line (ray
                // and _SunDirWorld don't change with t), so evaluate it ONCE here
                // instead of per march step. Bit-identical, but removes a normalize
                // + two pow(...,1.5) (HenyeyGreenstein) + a lerp from every shaded
                // sample -- the hottest ALU in the loop.
                float cosTheta = dot(rayDir, -_SunDirWorld);
                float phase = lerp(HenyeyGreenstein(cosTheta, -0.25),
                                   HenyeyGreenstein(cosTheta, _PhaseG), 0.7);

                // Trapezoidal integration: carry the previous shaded step's extinction + source density so each
                // step integrates a LINEAR ramp between the two samples instead of a piecewise-constant block. That
                // is what softens the thick-plume "staircase" -- both the opacity bands (avg sigma) and the fire
                // rings (avg radiance) -- with zero extra taps and zero noise. Reset to 0 whenever the ray leaps
                // empty air or hits a thin sample, so a fresh smoke entry ramps from zero (also softens the edge).
                float prevSigma = 0.0;
                float3 prevSource = 0.0;
                float prevDBack = 0.0;   // last sample's saturate(density × remap) — the trapezoid front (was re-derived per sample)
                float prevTBack = 0.0;   // last sample's fire-temperature response — ditto (was re-derived through an exp)
                float prevBurn = 0.0;
                float previousStep = baseStep; // actual distance from the previous shaded sample to this one
                float3 carriedPyroColor = 0.0;
                float3 jitterPyroColorSum = 0.0;
                float jitterPyroWeight = 0.0;
                float motionWeight = 0.0;
                float motionT = 0.0;

                // Loop-invariant lighting algebra: uniform-only (or uniform × per-pixel-constant) products
                // hoisted out of the shaded-sample path (~10–20 scalar ALU per sample).
                float3 sunPhase = _SunColor * (phase * 1.1 + 0.20);
                float3 sunScatterCol = _SunColor * _ScatterAmt;
                float3 ambientFillCol = (_AmbientColor + _SmokeFill);
                float3 fireGlowCol = _FireLightColor * _FireGlow;
                float smokeGain = _SmokeBright * _Illum;
                float contrastExp = max(_SmokeContrast, 0.01);

                [loop]
                for (int stepi = 0; stepi < maxSteps; stepi++)
                {
                    if (t >= tExit)
                        break;
                    float3 suvw = ro + rd * t;
                    float stepNow = baseStep;

                #if defined(_BRAZE_DEBUG_VIEW)
                    // Debug visualization lives in its own variant so the shipping loop never carries its
                    // registers/branches.
                    if (_DebugView > 0.5)
                    {
                        float4 rawState = MapState(SAMPLE_TEXTURE3D_LOD(_State, sampler_State, suvw, 0));
                        float rawSmoke = rawState.r;
                        float debugValue = rawSmoke;
                        if (_DebugView > 1.5 && _DebugView < 2.5)
                        {
                            float4 ds = SampleStateFiltered(WarpUvw(suvw));
                            debugValue = SmokeCurve(ds.r);
                        }
                        else if (_DebugView > 2.5 && _DebugView < 3.5)
                        {
                            debugValue = max(rawState.g, 0.0);
                        }
                        else if (_DebugView > 3.5 && _DebugView < 4.5)
                        {
                            debugValue = 0.0;
                            if (_UseOccupancy > 0.5)
                                debugValue = SampleOccupancy(suvw);
                        }
                        else if (_DebugView > 4.5 && _DebugView < 5.5)
                        {
                            debugValue = 0.0;
                            if (BRAZE_FILL_ON)
                            {
                                float fillT = max(tEnter, t - baseStep * 0.5);
                                float3 fillP = ro + rd * fillT;
                                debugValue = SliceGapFillD(fillP, WarpUvw(fillP) - fillP) * saturate(_VirtualFill);
                            }
                        }
                        else if (_DebugView > 5.5)
                        {
                            debugValue = 0.0;
                            if (_DebugView > 6.5)
                            {
                                debugValue = saturate((rawState.g - _CfdHeatMin) * _CfdHeatInvRange);
                            }
                            else if (BRAZE_SELF_SHADOW_ON)
                            {
                                float debugMidT = max(tEnter, t - baseStep * 0.5);
                                float shadowA = SampleShadowRaw(WarpUvw(suvw));
                                float shadowB = SampleShadowRaw(WarpUvw(ro + rd * debugMidT));
                                debugValue = 1.0 - (shadowA + shadowB) * 0.5;
                            }
                        }
                        debugMax = max(debugMax, saturate(debugValue));
                        t += baseStep;
                        continue;
                    }
                #endif

                    // Leap through empty air: where the coarse occupancy cell holds no
                    // smoke or heat, skip ahead without shading. THIS is the win --
                    // render cost now tracks smoke volume, not container/screen size.
                    float occValue = 0.0;
                    if (_UseOccupancy > 0.5)
                    {
                        float3 occPosition = OccupancyPosition(suvw);
                        float4 occStats = SampleOccupancyStatsPosition(occPosition);
                        occValue = occStats.x;
                        bool cfdFlowExactOccupancy = _CfdSmokeFlowSpeed > 0.5;
                        bool emptyOccupancy = cfdFlowExactOccupancy
                            ? occValue <= 0.0
                            : occValue < 1e-3;
                        if (emptyOccupancy)
                        {
                            float3 occPhase = frac(occPosition);
                            float3 towardBoundary = lerp(occPhase, 1.0 - occPhase, step(0.0, occDir));
                            float3 boundaryT3 = towardBoundary * occCellT;
                            float boundaryT = min(boundaryT3.x, min(boundaryT3.y, boundaryT3.z));
                            float boundaryAdvance = boundaryT + 1e-5;

                            // Flow Speed's high contrast exposes any loss of the ray-start phase. Jumping directly
                            // to `boundaryT + epsilon` rephases every ray on the 4^3 brick grid and overrides jitter,
                            // producing coherent zigzag seams. Keep long zero-space skips, but advance only by whole
                            // base steps so the original world-space lattice survives. If the next brick is occupied,
                            // retain the last lattice sample before its face for trilinear support and a continuous
                            // zero-to-dye ramp. Ordinary smoke/fire keeps its established boundary leap unchanged.
                            bool skipEmpty = true;
                            float emptyAdvance = max(baseStep, boundaryAdvance);
                            [branch] if (cfdFlowExactOccupancy)
                            {
                                float nextOcc = SampleOccupancyPosition(
                                    occPosition + occDir * boundaryAdvance);
                                [branch] if (nextOcc > 0.0)
                                {
                                    if (boundaryT <= baseStep)
                                        skipEmpty = false;
                                    else
                                        emptyAdvance =
                                            max(1.0, ceil(boundaryT / baseStep) - 1.0) * baseStep;
                                }
                                else
                                {
                                    emptyAdvance =
                                        max(1.0, ceil(boundaryAdvance / baseStep)) * baseStep;
                                }
                            }

                            [branch] if (skipEmpty)
                            {
                                prevSigma = 0.0;   // gap → next real sample starts a fresh ramp, not an average across empty air
                                prevSource = 0.0;
                                prevDBack = 0.0;
                                prevTBack = 0.0;
                                prevBurn = 0.0;
                                carriedPyroColor = 0.0;
                                t += emptyAdvance;
                                previousStep = baseStep; // empty distance is not part of the next smoke trapezoid
                                continue;
                            }
                        }

                        // Smooth smoke/fire is cheap to integrate over a longer interval. Alpha contains normalized
                        // variation from both fields; visibility peak-vs-mean separately protects smoke silhouettes.
                        // Fire caps at a conservative 2x even when perfectly smooth, while a flame front/turbulent
                        // brick drives combined variation high and restores the exact voxel step.
                        [branch] if (_AdaptiveMarching > 0.5)
                        {
                            float meanVis = max(occStats.z, 0.0);
                            float threshold = max(_AdaptiveVarianceThreshold, 0.05);
                            float variationDetail = saturate(occStats.w / max(threshold * threshold, 1e-5));
                            float peakDetail = saturate((occStats.x - meanVis) / max(occStats.x, 0.05) * 2.0);
                            float detail = max(variationDetail, peakDetail);
                            float firePresence = saturate(occStats.y * 8.0);
                            float smoothStep = lerp(max(_AdaptiveMaxStep, 1.0),
                                clamp(_AdaptiveFireMaxStep, 1.0, max(_AdaptiveMaxStep, 1.0)), firePresence);
                            float stepScale = lerp(smoothStep, 1.0, detail);

                            // Never stride across a statistics-brick boundary: the next brick may contain fire/detail.
                            float3 occPhase = frac(occPosition);
                            float3 towardBoundary = lerp(occPhase, 1.0 - occPhase, step(0.0, occDir));
                            float3 boundaryT3 = towardBoundary * occCellT;
                            float boundaryT = min(boundaryT3.x, min(boundaryT3.y, boundaryT3.z));
                            stepNow = min(baseStep * stepScale, max(baseStep, boundaryT + 1e-5));
                        }
                    }

                    // `stepNow` is the outgoing stride chosen from this brick. The trapezoid below blends the
                    // previous and current samples, so it must integrate over the incoming stride instead. Using
                    // the outgoing stride at a fine/coarse transition pumps opacity and emission at brick edges.
                    float segmentStep = previousStep;
                    float sampleUnits = max(segmentStep * length(rd), 1e-5);

                    float depthFade = saturate((sceneZ - t) / max(_DepthSoftness, 1e-4));

                    float3 lookupUVW = WarpUvw(suvw);

                    bool fillOnly = _SliceLayerMode == 2;
                    bool slicesOnly = _SliceLayerMode == 1;
                    float density = 0.0;
                    float fireTemp = 0.0;
                    float cfdFlowSpeed = 0.0;
                    float fireBurn = 0.0;
                    float dBack = 0.0;
                    float dFront = prevDBack;
                    float midT = max(tEnter, t - segmentStep * 0.5);
                    float3 midSuvw = ro + rd * midT;
                    [branch] if (!fillOnly)
                    {
                        float4 raw = MapState(SampleStateRaw(lookupUVW));
                        density = raw.x;
                        fireTemp = raw.y;
                        cfdFlowSpeed = raw.z;
                        fireBurn = raw.w;
                        dBack = saturate(density * DENSITY_REMAP);
                    }

                    // Gap fill: when the slice budget stretches the physical step, sample the
                    // missing half-step as a cheap slice and lift only under-sampled valleys.
                    // The fill sample goes through WarpUvw/SampleStateRaw, then the same smoke
                    // transfer, lighting, shadow and definition path as the real slice.
                    [branch] if (!slicesOnly && BRAZE_FILL_ON && BRAZE_SMOKE_ON && dBack < 0.999)
                    {
                        float fillWeight = saturate(_VirtualFill);
                        [branch] if (fillWeight > 1e-4)
                        {
                            float fillD = SliceGapFillD(midSuvw, lookupUVW - suvw);
                            float fillLift = saturate(fillD * fillWeight);
                            dFront = fillOnly ? fillLift : saturate(dFront + max(fillD - dFront, 0.0) * fillWeight);
                            dBack = fillOnly ? fillLift : saturate(dBack + max(fillD - dBack, 0.0) * fillWeight);
                            density = max(density, dBack / DENSITY_REMAP);
                        }
                    }

                    // All-zero sample meeting an all-zero trapezoid front: the pre-integrated tables map the zero
                    // corner to zero extinction/response, so this is exactly the sigma early-out below — taken
                    // BEFORE paying its LUT taps + exps. Common at silhouettes inside occupied bricks.
                    if (dBack <= 0.0 && fireTemp <= 0.0 && fireBurn <= 0.0 &&
                        prevTBack <= 0.0 && dFront <= 0.0 && prevBurn <= 0.0)
                    {
                        prevSigma = 0.0;
                        prevSource = 0.0;
                        carriedPyroColor = 0.0;
                        previousStep = stepNow;
                        t += stepNow;
                        continue;
                    }

                float3 borderDist = min(suvw, 1.0 - suvw);
                float fade = smoothstep(0.0, _EdgeFade, min(borderDist.x, min(borderDist.y, borderDist.z)));

                // Fire path is a local shader variant: its two LUT taps + three exps are absent from
                // sample of smoke-only volumes and were multiplied by zero afterwards. The trapezoid front
                // (prevTBack) IS last iteration's back value, carried instead of re-derived through an exp.
                float tBack = 0.0;
                float4 fireTF = 0.0;
                float fireAmt = 0.0;
                if (BRAZE_FIRE_ON)
                {
                    tBack = 1.0 - exp(-fireTemp * _FireTempScaleOld * HEAT_GAIN);
                    fireTF = FireTF(prevTBack, tBack);
                    float burnAvg = (prevBurn + fireBurn) * 0.5;
                    float fireResp = 1.0 - exp(-max(burnAvg, 0.0) * max(_StateFireGain, 0.0));
                    fireResp = SAMPLE_TEXTURE2D_LOD(_FireCurve, sampler_FireCurve, float2(saturate(fireResp), 0.5), 0).x;
                    fireAmt = fireResp * fade;
                }

                float4 smokeTF = 0.0;
                float sigmaSmoke = 0.0;
                if (BRAZE_SMOKE_ON)
                {
                    smokeTF = SmokeTF(dFront, dBack);
                    [branch] if (_CfdSmokeFlowSpeed > 0.5)
                    {
                        smokeTF.xyz = CfdSpectrum(cfdFlowSpeed);
                        // Flow Emphasis: the freestream dye carries the same optical depth as the wake, so the
                        // march averages turbulent reds/blues into the surrounding green slab. Thin the dye where
                        // flow sits at nominal speed and keep it dense where it deviates. Deviation is measured
                        // PRE-contrast so this knob and Color Contrast stay orthogonal.
                        float dev = saturate(abs(cfdFlowSpeed - 0.5) * 2.0);
                        smokeTF.w *= lerp(1.0, smoothstep(0.0, 0.5, dev), saturate(_CfdSmokeFlowEmphasis));
                    }
                    else if (_CfdSmokeTemperature > 0.5)
                    {
                        float heat = saturate((fireTemp - _CfdHeatMin) * _CfdHeatInvRange);
                        smokeTF.xyz = CfdSpectrum(heat);

                        // Temperature is a solved fluid field, not a passive-dye quantity. Using dye as its only
                        // opacity source makes a full inlet fill the whole domain with cold blue/green fog and
                        // can completely occlude the comparatively small electronics plume. Suppress ambient
                        // dye and add extinction from above-ambient temperature itself. Opacity must rise with
                        // temperature: making the cool outer plume opaque hides the hotter source-side core in
                        // front-to-back compositing and leaves an incorrectly all-blue image. Colour remains
                        // linearly calibrated to the displayed Celsius range; only visibility is transferred.
                        float thermalVisibility = smoothstep(0.03, 0.65, heat);
                        float temperatureDye = smokeTF.w * thermalVisibility;
                        float temperatureVolume = thermalVisibility * 0.40;
                        smokeTF.w = max(temperatureDye, temperatureVolume);
                    }
                    sigmaSmoke = smokeTF.w * max(_SmokeDensityScale, 0.0) * saturate(_Opacity) * fade;
                }
                float sigmaFire = fireAmt * max(_FireDensityScale, 0.0);
                float sigmaT = sigmaSmoke + sigmaFire;
                if (sigmaT < 1e-5 && fireAmt < 1e-5)
                {
                    prevSigma = 0.0;
                    prevSource = 0.0;
                    prevDBack = 0.0;
                    prevTBack = 0.0;
                    prevBurn = 0.0;
                    carriedPyroColor = 0.0;
                    previousStep = stepNow;
                    t += stepNow;
                    continue;
                }

                float shadow;
                if (BRAZE_SELF_SHADOW_ON)
                {
                    // ONE shadow tap at the slice's already-warped point. The old endpoint+midpoint average
                    // cost a second shadow tap AND a second WarpUvw per lit sample for a half-step smoothing
                    // the shadow volume's own trilinear filtering already provides (it lives at state res and
                    // the march steps ~1 voxel) — 3 taps → 1 with a sub-voxel phase shift as the only delta.
                    float shadowFilterGate = saturate(max(max(dFront, dBack), fireAmt));
                    float shadowT = SampleShadowRaw(lookupUVW);
                    // Apply depth through the body, not across transparent fringe voxels. This prevents a coarse
                    // transmittance cell from stamping its shape onto otherwise smooth wisps, while dense smoke
                    // retains the full authored Mix Strength. Same one texture tap; only local values are reused.
                    float localShadowBody = smoothstep(0.02, 0.45, shadowFilterGate);
                    float shadowStrength = saturate(_SelfShadowStrength) * localShadowBody;
                    shadow = lerp(1.0, saturate(shadowT), shadowStrength);
                }
                else
                {
                    // slice01: 0 at the near side of the box, 1 at the far side — matches the old per-slice
                    // value this fallback expects. Only this branch consumes it, so it lives here.
                    float slice01 = saturate((t - _VolumeZ.x) * invSpan);
                    float depthShadow = saturate(1.0 - (1.0 - slice01) * _SelfShadowDepthScale);
                    shadow = lerp(1.0, depthShadow, _SelfShadowStrength);
                }
                // Preserve the Smoke Contrast control without the expensive power curve that crushed low-res
                // transmittance into near-binary patches. At maximum contrast this reaches shadow²; at the usual
                // 1–1.5 range it remains a subtle depth adjustment.
                float shadowContrast = saturate((contrastExp - 1.0) / 3.0);
                shadow = lerp(shadow, shadow * shadow, shadowContrast);

                float3 sculptCol = 1.0;
            #if defined(_BRAZE_DEFINITION)
                {
                    // Baked per-voxel sculpt (KSculpt in BrazeSliceBake.compute): ONE tap replaces the old
                    // 6-tap per-sample central difference (up to 12 fetches + a normalize(mul) of two
                    // uniforms PER SAMPLE — the largest tap cost in this loop). Same band-limited,
                    // voxel-resolution signal; the Definition knob stays live as the lerp weight.
                    // Sculpt is a scalar field too: native trilinear avoids reintroducing flat voxel-centre
                    // shelves when Definition is enabled.
                    float2 sv = SAMPLE_TEXTURE3D_LOD(_SculptTex, sampler_SculptTex, saturate(lookupUVW), 0).rg;
                    float sculptV = sv.x * 1.7 + 0.1;
                    sculptCol = lerp(float3(1.0, 1.0, 1.0), float3(sculptV, sculptV, sculptV), saturate(sv.y * _Definition));
                }
            #endif

                // Fill is broad, indirect light: preserve most of it even where direct transmittance reaches zero.
                // Feeding the raw shadow into ambient at Fill Shadowing=1 reproduced the half-res shadow cells.
                // Bounding the fill occlusion to 35% keeps the control useful for depth without letting it stamp
                // the bake lattice into the smoke. The affine remap replaces the old AO remap below, so ALU is flat.
                float softFillShadow = 0.65 + 0.35 * shadow;
                float shadowedFill = lerp(1.0, softFillShadow, saturate(_ShadowAmbientAmount));
                float3 direct = sunPhase * shadow;
                float3 inscatter = sunScatterCol * ((1.0 - shadow) * (shadow * 0.6 + 0.4))
                                 * shadowedFill * saturate(_MultiScatter);
                float3 ambient = ambientFillCol * shadowedFill;
                float3 fireGlow = fireGlowCol * fireAmt;
                float3 scatter = smokeTF.xyz * (direct + inscatter + ambient + fireGlow)
                               * sculptCol * (smokeGain * sigmaSmoke);
                [branch] if (_CfdSmokeDiagnostic > 0.5)
                {
                    // CFD dye is a false-colour field view rather than soot. Scene-light mixing washed saturated
                    // bands toward grey/cyan, so keep this presentation branch fully self-readable and unlit.
                    float3 diagnostic = smokeTF.xyz * (smokeGain * sigmaSmoke);
                    scatter = diagnostic;
                }

                float3 emission = 0.0;
                if (BRAZE_FIRE_ON)
                {
                    float fireLuma = max(dot(fireTF.xyz, float3(0.2126, 0.7152, 0.0722)), 1e-4);
                    float3 fireHue = fireTF.xyz / fireLuma;
                    emission = fireHue * (_FireScaleOld * fireAmt) * sigmaFire;
                }

                // In-scatter/emission integrated as a LINEAR ramp between the previous and current sample. Source
                // density is split from extinction so flame can be emissive even when Fire Opacity is near zero.
                float3 sourceNow = scatter + emission;
                bool hasPrev = prevSigma > 0.0 || dot(prevSource, prevSource) > 1e-12;
                float sigmaAvg = hasPrev ? 0.5 * (prevSigma + sigmaT) : 0.5 * sigmaT;
                float3 sourceAvg = hasPrev ? 0.5 * (prevSource + sourceNow) : 0.5 * sourceNow;
                float tr = exp(-sigmaAvg * sampleUnits);
                // Metal's static analyzer evaluates both sides of a ternary for division safety.
                float sourceIntegral = (1.0 - tr) / max(sigmaAvg, 1e-5);
                if (sigmaAvg <= 1e-5) sourceIntegral = sampleUnits;
                float3 contribution = sourceAvg * sourceIntegral;
                float segmentOpacity = saturate(1.0 - tr);
                float3 segmentColor = contribution / max(segmentOpacity, 1e-4);
                float segmentLuma = dot(segmentColor, float3(0.2126, 0.7152, 0.0722));
                // Repair weights must see the segment's VISIBLE opacity (× depthFade), matching the
                // compositor below. Un-gated weights let smoke sitting behind geometry feed the
                // post-march coverage repair, which then paints speckles on top of the meshes.
                float segmentVisibleOpacity = segmentOpacity * depthFade;
                float segmentWeight = segmentVisibleOpacity * saturate(max(max(dFront, dBack), fireAmt) + sigmaT * sampleUnits);
                if (segmentWeight > 1e-5 && segmentLuma > 1e-5)
                {
                    carriedPyroColor = segmentColor;
                    jitterPyroColorSum += segmentColor * segmentWeight;
                    jitterPyroWeight += segmentWeight;
                }
                else if (segmentVisibleOpacity > 1e-5 && dot(carriedPyroColor, carriedPyroColor) > 1e-10)
                {
                    float carryWeight = segmentVisibleOpacity * 0.35;
                    jitterPyroColorSum += carriedPyroColor * carryWeight;
                    jitterPyroWeight += carryWeight;
                }

                // Front-to-back compositing, with the soft-depth fade applied to
                // both radiance and opacity so the volume melts into geometry.
                [branch] if (_BrazeReproject > 0.5)
                {
                    float visibleContribution = T * segmentVisibleOpacity;
                    if (visibleContribution > 1e-5)
                    {
                        motionWeight += visibleContribution;
                        motionT += t * visibleContribution;
                    }
                }
                accum += T * contribution * depthFade;
                T *= lerp(1.0, tr, depthFade);
                prevSigma = sigmaT;
                prevSource = sourceNow;
                prevDBack = dBack;
                prevTBack = tBack;
                prevBurn = fireBurn;
                previousStep = stepNow;
                t += stepNow;   // adaptive inside smooth bricks; fine at fire/silhouettes

                // Early ray termination: once only 2% transmittance remains, farther material is strongly attenuated
                // by the already-opaque foreground. Dense, long-life columns reach this point early, so stop paying
                // the expensive state/lighting loop for material with at most a small remaining influence.
                if (T < 0.02)
                    break;
                }

            #if defined(_BRAZE_DEBUG_VIEW)
                if (_DebugView > 0.5)
                {
                    if (_DebugView > 6.5)
                    {
                        float heat = saturate(debugMax);
                        float3 heatColor = heat < 0.5
                            ? lerp(_CfdHeatCold, _CfdHeatMiddle, heat * 2.0)
                            : lerp(_CfdHeatMiddle, _CfdHeatHot, (heat - 0.5) * 2.0);
                        float alpha = saturate(heat * _CfdHeatOpacity);
                        outColor = float4(heatColor * alpha, alpha); // premultiplied for Blend One/OneMinusSrcAlpha
                        return;
                    }
                    float3 debugColor = debugMax.xxx;
                    if (_DebugView > 2.5 && _DebugView < 3.5)
                        debugColor = float3(debugMax, debugMax * 0.45, 0.0);
                    else if (_DebugView > 3.5 && _DebugView < 4.5)
                        debugColor = float3(debugMax * 0.15, debugMax, debugMax * 0.25);
                    else if (_DebugView > 4.5 && _DebugView < 5.5)
                        debugColor = float3(debugMax * 0.15, debugMax * 0.55, debugMax);
                    else if (_DebugView > 5.5)
                        debugColor = float3(debugMax, debugMax * 0.72, debugMax * 0.18);
                    outColor = float4(debugColor, 1.0);   // opaque: replace the destination (alpha = opacity)
                    return;
                }
            #endif

                // Blend One OneMinusSrcAlpha composites this as: destinationColor * T + accum (alpha = opacity).
                float opacity = saturate(1.0 - T);
                bool hasPyroColor = jitterPyroWeight > 1e-5;
                float3 pyroCol = jitterPyroColorSum / max(jitterPyroWeight, 1e-5);
                if (!hasPyroColor) pyroCol = float3(0.0, 0.0, 0.0);
                float pyroSeen = smoothstep(0.005, 0.18, jitterPyroWeight);
                float partialOpacityMask = smoothstep(0.002, 0.08, max(opacity, jitterPyroWeight))
                                         * (1.0 - smoothstep(0.62, 0.94, opacity));

                // Jitter denoise fills the alpha/transmittance byproduct after the raymarch. It leaves the
                // jittered sample phase untouched, then adds only a small amount of missing pyro-coloured
                // premultiplied coverage on partial-opacity fume pixels.
                float coverageRepairMask = saturate(_JitterStrength * jitterDenoise) * partialOpacityMask * pyroSeen;
                [branch] if (coverageRepairMask > 1e-4 && hasPyroColor)
                {
                    float opacityLift = (1.0 - opacity) * (0.08 + 0.42 * pyroSeen) * coverageRepairMask;
                    accum += pyroCol * opacityLift;
                    opacity = saturate(opacity + opacityLift);
                    T = min(T, 1.0 - opacity);
                }

                float jitterPyroMask = saturate(_JitterStrength * _JitterPyroFill)
                                     * smoothstep(0.015, 0.18, opacity)
                                     * (1.0 - smoothstep(0.55, 0.9, opacity));
                [branch] if (jitterPyroMask > 1e-4 && hasPyroColor)
                {
                    float3 pyroPremul = pyroCol * opacity;
                    float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
                    float accumLuma = dot(accum, lumaWeights);
                    float pyroLuma = dot(pyroPremul, lumaWeights);
                    float needsLift = saturate((pyroLuma - accumLuma) / max(pyroLuma, 1e-4));
                    accum = lerp(accum, pyroPremul, jitterPyroMask * needsLift);
                }
                [branch] if (_BrazeReproject > 0.5 && motionWeight > 1e-5 && !fastForcedMarch)
                {
                    float centroidT = motionT / max(motionWeight, 1e-5);
                    float3 centroidUVW = saturate(ro + rd * centroidT);
                    float3 previousWS = originWS + ray * centroidT;
                    [branch] if (_BrazeFluidReprojection > 0.5 && _UseVelocityJitter > 0.5)
                    {
                        float3 vuv = SmoothVoxelUVWRes(centroidUVW, _VelocityInvRes, _VelocityResF);
                        float3 velVoxPerSec = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, vuv, 0).xyz;
                        float3 velUvwPerSec = velVoxPerSec / max(_VelocityResF, float3(1.0, 1.0, 1.0));
                        previousWS -= mul((float3x3)_SimToWorld, velUvwPerSec) * _BrazeSliceDeltaTime;
                    }
                    outMotion = BrazeWorldToScreenUV(previousWS, _BrazePrevVP) - input.rayUV;
                }
                outColor = float4(accum, saturate(1.0 - T));
            }

#endif // BRAZEFX_SLICES_CORE_INCLUDED
