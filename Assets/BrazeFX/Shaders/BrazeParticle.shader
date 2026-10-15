// BrazeFX GPU particle renderer.
//
// Procedural, instanced draw fed by the COMPACT-built visible-index buffer + indirect args.
// Adapted from the publisher-owned three-js-particles `render.wgsl`: billboard orientation (camera/velocity/world/local +
// stretched / horizontal / vertical), sprite-sheet atlas UVs, pivot/flip, soft particles,
// and alpha/additive/multiply blending (blend state driven by material ints set in C#).

Shader "BrazeFX/Particle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HideInInspector] _FlipbookOpacityLut ("Flipbook Opacity", 2D) = "white" {}
        [HideInInspector] _FlipbookOpacityEnable ("Flipbook Opacity Enable", Float) = 0
        _BlendSrc ("BlendSrc", Float) = 1
        _BlendDst ("BlendDst", Float) = 1
        // Depth test: LEqual (4) = particles are occluded by opaque geometry in front of them (correct for in-world
        // VFX). Set to Always (8) for overlay particles that should ignore depth.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeParticle"
            Blend [_BlendSrc] [_BlendDst]
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile _ _SOFT_PARTICLES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #if defined(_SOFT_PARTICLES)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #endif

            // Packed 48-byte particle record + accessors (shared with the sim + mesh/line paths).
            #include "BrazeParticleStruct.hlsl"

            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<uint>     _VisibleIndices;
            float4x4 _Model;

            TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
            TEXTURE2D(_FlipbookOpacityLut); SAMPLER(sampler_FlipbookOpacityLut);
            float4 _MainTex_TexelSize;   // (1/w, 1/h, w, h) — auto-filled by Unity; for the flipbook edge inset
            float4 _FlipbookOpacityLut_TexelSize;

            float4 _Tint;
            float2 _Pivot;
            float2 _Flip;            // per-particle flip probabilities (x, y)
            float  _RenderMode;      // 0 sprite, 1 stretched, 2 flat (horizontal), 3 upright (vertical)
            float  _Facing;          // 0 camera, 1 velocity, 2 world, 3 local
            float  _VelocityIsLocal; // 1 when particle velocity must be transformed by _Model before rendering
            // xy stretch, z stretch variation, w brightness minimum. Packing these together consumes exactly the
            // same constant-buffer slot as the former float2 _Stretch + float _StretchVar + padding.
            float4 _RenderParams;
            float4 _Flipbook;        // x cols, y rows, z enabled, w lifetime cycles
            float4 _FlipbookPlayback;// x time mode, y Once/Loop/PingPong/HoldLast, z fps, w start frame
            float4 _FlipbookLayout;  // x WholeSheet/SingleRow, y Custom/Random row, z row index, w random start
            float4 _FlipbookSpeedRange; // xy speed mapped to the first/last sequence frames
            float  _FlipbookOpacityEnable;
            float  _SoftFade;        // soft-particle fade distance (world units); 0 = off
            float  _AlphaCutoff;
            float  _Multiply;        // 1 = multiply blend (different premultiply)
            float  _EmissiveGain;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : TEXCOORD1;
                float  viewDepth  : TEXCOORD2;   // eye-space depth for soft particles
            };

            static const float2 kCorners[6] =
            {
                float2(-0.5, -0.5), float2( 0.5, -0.5), float2(-0.5,  0.5),
                float2(-0.5,  0.5), float2( 0.5, -0.5), float2( 0.5,  0.5),
            };

            float3 safeNrm(float3 v, float3 fb) { float l = length(v); return l > 1e-5 ? v / l : fb; }

            float hash01(uint s, uint salt)
            {
                uint x = s ^ salt;
                x = x * 747796405u + 2891336453u;
                uint w = ((x >> ((x >> 28u) + 4u)) ^ x) * 277803737u;
                return float((w >> 8u) & 0x00ffffffu) / 16777215.0;
            }

            float sampleFlipbookOpacity(float t, uint seed)
            {
                if (_FlipbookOpacityEnable < 0.5) return 1.0;

                // Curve LUTs use point filtering so compute and render sampling agree. Interpolate the two
                // neighbouring entries explicitly to keep opacity smooth between atlas-frame boundaries.
                float width = max(_FlipbookOpacityLut_TexelSize.z, 1.0);
                float samplePosition = saturate(t) * max(width - 1.0, 0.0);
                float x0 = floor(samplePosition);
                float x1 = min(x0 + 1.0, width - 1.0);
                float2 lo = SAMPLE_TEXTURE2D_LOD(_FlipbookOpacityLut, sampler_FlipbookOpacityLut,
                                                  float2((x0 + 0.5) / width, 0.5), 0).rg;
                float2 hi = SAMPLE_TEXTURE2D_LOD(_FlipbookOpacityLut, sampler_FlipbookOpacityLut,
                                                  float2((x1 + 0.5) / width, 0.5), 0).rg;
                float2 range = lerp(lo, hi, frac(samplePosition));
                return max(lerp(range.x, range.y, hash01(seed, 0x94d049bbu)), 0.0);
            }

            float flipbookFrame(Particle p, uint seed, float frameCount, out float visible, out float curveT)
            {
                visible = 1.0;
                curveT = 0.0;
                frameCount = max(frameCount, 1.0);
                float lastFrame = frameCount - 1.0;
                float startFrame = clamp(floor(_FlipbookPlayback.w), 0.0, lastFrame);
                if (_FlipbookLayout.w > 0.5)
                    startFrame = min(floor(hash01(seed, 0xc2b2ae35u) * frameCount), lastFrame);

                float advance;
                if (_FlipbookPlayback.x < 0.5) // FPS: seconds alive × frames/sec, independent of particle lifetime
                {
                    advance = p.age * max(_FlipbookPlayback.z, 0.0);
                }
                else if (_FlipbookPlayback.x < 1.5) // Lifetime: retained as an optional Unity-style time mode
                {
                    float normalizedAge = saturate(p.age / max(ParticleLifetime(p), 1e-5));
                    bool repeats = _FlipbookPlayback.y > 0.5 && _FlipbookPlayback.y < 2.5;
                    float cycles = repeats ? max(_Flipbook.w, 1.0) : 1.0;
                    float cycleSpan = _FlipbookPlayback.y > 1.5 && _FlipbookPlayback.y < 2.5
                        ? max(lastFrame * 2.0, 1.0)
                        : frameCount;
                    advance = normalizedAge * cycleSpan * cycles;
                }
                else // Speed: select a frame by the particle's current speed
                {
                    float speedSpan = max(_FlipbookSpeedRange.y - _FlipbookSpeedRange.x, 1e-5);
                    float speedT = saturate((length(p.velocity) - _FlipbookSpeedRange.x) / speedSpan);
                    advance = speedT * lastFrame;
                    curveT = speedT;
                }

                float cursor = startFrame + floor(max(advance, 0.0));
                if (_FlipbookPlayback.x > 1.5) // Speed is a clamped frame selection, not a playback clock
                    return min(cursor, lastFrame);
                if (_FlipbookPlayback.y < 0.5) // Once: show every remaining frame, then disappear
                {
                    float durationFrames = max(frameCount - startFrame, 1.0);
                    curveT = saturate(advance / durationFrames);
                    visible = advance < durationFrames ? 1.0 : 0.0;
                    return min(cursor, lastFrame);
                }
                if (_FlipbookPlayback.y < 1.5) // Loop
                {
                    curveT = frac(max(advance, 0.0) / frameCount);
                    return fmod(cursor, frameCount);
                }
                if (_FlipbookPlayback.y < 2.5) // Ping Pong: 0..last..0 without duplicating endpoints
                {
                    float period = max(lastFrame * 2.0, 1.0);
                    float phase = fmod(cursor, period);
                    float curvePhase = fmod(max(advance, 0.0), period);
                    curveT = lastFrame > 0.0
                        ? (curvePhase <= lastFrame ? curvePhase : period - curvePhase) / lastFrame
                        : frac(max(advance, 0.0));
                    return phase <= lastFrame ? phase : period - phase;
                }
                curveT = saturate(advance / max(frameCount - startFrame, 1.0));
                return min(cursor, lastFrame); // Hold Last
            }

            void orient(float3 vel, out float3 right, out float3 up)
            {
                float3 camR = UNITY_MATRIX_V[0].xyz;
                float3 camU = UNITY_MATRIX_V[1].xyz;
                float3 camF = -UNITY_MATRIX_V[2].xyz;

                if (_RenderMode > 1.5 && _RenderMode < 2.5)      { right = float3(1,0,0); up = float3(0,0,1); return; }  // flat
                if (_RenderMode > 2.5)                           { right = camR; up = float3(0,1,0); return; }          // upright
                // sprite — facing
                if (_Facing > 0.5 && _Facing < 1.5) { up = safeNrm(vel, camU); right = safeNrm(cross(up, camF), camR); return; } // velocity
                if (_Facing > 1.5 && _Facing < 2.5) { right = float3(1,0,0); up = float3(0,1,0); return; }                   // world
                if (_Facing > 2.5) {
                    right = safeNrm(mul(_Model, float4(1,0,0,0)).xyz, camR);
                    up    = safeNrm(mul(_Model, float4(0,1,0,0)).xyz, camU); return;                                          // local
                }
                right = camR; up = camU;                                                                                    // camera
            }

            Varyings vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                Varyings o = (Varyings)0;
                Particle p = _Particles[_VisibleIndices[iid]];

                uint seed = ParticleRandomSeed(p);
                float2 c = kCorners[vid % 6u];
                if (hash01(seed, 0x9e3779b9u) < _Flip.x) c.x = -c.x;
                if (hash01(seed, 0x85ebca6bu) < _Flip.y) c.y = -c.y;
                // Texture coord comes from the (flipped) corner BEFORE the pivot offset — pivot must shift the
                // QUAD, not slide the texture (a non-zero pivot used to drag the sampling off-frame).
                float2 uv = c + 0.5;
                c -= _Pivot;

                // Flipbook atlas UV.
                float flipbookVisible = 1.0;
                float flipbookCurveT = 0.0;
                if (_Flipbook.z > 0.5)
                {
                    float cols = max(_Flipbook.x, 1.0), rows = max(_Flipbook.y, 1.0);
                    float total = cols * rows;
                    bool singleRow = _FlipbookLayout.x > 0.5;
                    float sequenceFrames = singleRow ? cols : total;
                    float frame = flipbookFrame(p, seed, sequenceFrames, flipbookVisible, flipbookCurveT);
                    float col = singleRow ? frame : fmod(frame, cols);
                    float authoredRow = singleRow
                        ? (_FlipbookLayout.y > 0.5
                            ? min(floor(hash01(seed, 0x165667b1u) * rows), rows - 1.0)
                            : clamp(floor(_FlipbookLayout.z), 0.0, rows - 1.0))
                        : floor(frame / cols);
                    // Sheets are authored top-to-bottom, but UV origin is bottom-left, so flip the row index —
                    // otherwise the animation plays its rows in reverse vertical order (looks scrambled).
                    float row = (rows - 1.0) - authoredRow;
                    // Half-texel inset: keep each frame's bilinear taps INSIDE its own tile so neighbouring
                    // frames don't bleed across the boundary (faint ghosting), regardless of wrap mode.
                    float2 inset = 0.5 * _MainTex_TexelSize.xy * float2(cols, rows);
                    uv = inset + uv * (1.0 - 2.0 * inset);
                    uv = (float2(col, row) + uv) / float2(cols, rows);
                }

                float3 vel = (_VelocityIsLocal > 0.5) ? mul((float3x3)_Model, p.velocity) : p.velocity;
                float3 right, up;
                orient(vel, right, up);

                // A completed Once animation becomes a degenerate quad, avoiding lingering last-frame fragments.
                float size = ParticleSize(p) * flipbookVisible;
                float w = size, h = size;
                if (_RenderMode > 0.5 && _RenderMode < 1.5)      // stretched
                {
                    float speed = length(vel);
                    float stretchLen = size * max(_RenderParams.y, 0.0) + speed * max(_RenderParams.x, 0.0);
                    // per-particle ± variation so streaks aren't all the same length
                    float varf = 1.0 + _RenderParams.z * (hash01(seed, 0x27d4eb2fu) * 2.0 - 1.0);
                    h = max(size, stretchLen * max(varf, 0.0));
                    up = safeNrm(vel, up);                       // streak always elongates along velocity
                    // Alignment chooses the facing plane (the streak's flat side). Camera/Velocity = billboard
                    // (faces you); World/Local = the streak lies in a fixed plane (cross with that space's up).
                    float3 facingRef;
                    if (_Facing > 1.5 && _Facing < 2.5)      facingRef = float3(0, 1, 0);                                  // world up
                    else if (_Facing > 2.5)                  facingRef = safeNrm(mul(_Model, float4(0,1,0,0)).xyz, float3(0,1,0)); // local up
                    else                                     facingRef = -UNITY_MATRIX_V[2].xyz;                           // camera fwd (billboard)
                    right = safeNrm(cross(up, facingRef), right);
                }

                // sprite-space rotation (skip for stretched/flat/upright which use world axes)
                float rot = ParticleRotation(p);
                float sr, cr; sincos(rot, sr, cr);
                float2 rc = (_RenderMode < 0.5) ? float2(c.x * cr - c.y * sr, c.x * sr + c.y * cr) : c;

                float3 center = mul(_Model, float4(p.position, 1.0)).xyz;
                float3 worldPos = center + right * rc.x * w + up * rc.y * h;

                o.positionCS = TransformWorldToHClip(worldPos);
                o.uv = uv;
                o.color = ParticleColor(p) * _Tint;
                // The spawn seed is already a uniformly hashed 24-bit stable identity. Reusing it adds no particle
                // storage and keeps each particle's brightness steady for its entire life. Do this per vertex instead
                // of per fragment; fixed brightness skips the lerp and both modes remove the old per-pixel gain multiply.
                if (_Multiply < 0.5)
                {
                    float brightness = max(_EmissiveGain, 0.0);
                    float brightnessMin = max(_RenderParams.w, 0.0);
                    if (brightnessMin < brightness)
                        brightness = lerp(brightnessMin, brightness,
                                          float(seed) * (1.0 / 16777215.0));
                    o.color.rgb *= brightness;
                }
                o.color.a *= flipbookVisible * sampleFlipbookOpacity(flipbookCurveT, seed);
                o.viewDepth = -TransformWorldToView(worldPos).z;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half4 col = tex * i.color;

                float a = col.a;
            #if defined(_SOFT_PARTICLES)
                if (_SoftFade > 0.0)
                {
                    float2 suv = i.positionCS.xy / _ScreenParams.xy;        // SV_POSITION is pixel coords in frag
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                    a *= saturate((sceneEye - i.viewDepth) / _SoftFade);   // fade as it nears / goes behind geometry
                }
            #endif

                if (a < _AlphaCutoff) discard;

                if (_Multiply > 0.5)
                    return half4(lerp((half3)1.0, col.rgb, a), a);          // multiply: Blend DstColor Zero
                return half4(col.rgb * a, a);                               // premultiplied (additive / alpha)
            }
            ENDHLSL
        }
    }
    Fallback Off
}
