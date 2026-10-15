// BrazeFX GPU particle trail renderer.
//
// Camera-facing trail ribbon shader. Geometry/path nodes are built by BrazeTrail.compute; this shader only expands
// those nodes to camera-facing width and applies the fire/color/opacity look.

Shader "BrazeFX/Trail"
{
    Properties
    {
        _BlendSrc ("BlendSrc", Float) = 1
        _BlendDst ("BlendDst", Float) = 1
        _SelfShadow ("Self Shadow", Float) = 0
        _SmokeAmount ("Smoke Amount", Float) = 0
        _SmokeLayerColor ("Smoke Color", Color) = (0.18, 0.17, 0.15, 1)
        // LEqual (4) = trails are occluded by opaque geometry in front of them. Always (8) for overlay trails.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeTrail"
            Blend [_BlendSrc] [_BlendDst]
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct LineNode
            {
                float4 posT;  // xyz = particle-space spine position, w = normalized trail u (head 0 -> tail 1)
                float4 color; // xyz = particle-space tangent, precomputed once by BrazeTrail.compute
                float4 misc;  // x = ramp u, y = age u, z = valid, w = seed phase
            };

            StructuredBuffer<uint> _VisibleIndices;
            StructuredBuffer<LineNode> _LineNodes;
            float4x4 _Model;

            uint  _Segments;     // quads per ribbon
            uint  _NodeCount;    // _Segments + 1

            float _WidthMin;     // per-particle width range, in world units
            float _WidthMax;
            TEXTURE2D(_WidthCurveTex); SAMPLER(sampler_WidthCurveTex);   // width multiplier along length (head→tail)

            // Colour model. _ColorMode: 0 FireSmoke, 1 Gradient(along trail), 2 ByAge, 3 Constant.
            float  _ColorMode;
            TEXTURE2D(_Ramp);    SAMPLER(sampler_Ramp);   // 256x1 HDR ramp for Gradient
            float4 _ConstColor;  // Constant mode flat colour

            float4 _FireColor;   // HDR head colour
            float4 _SmokeColor;  // HDR tail colour
            float  _ColorMid;    // where fire→smoke crosses over (0..1 along length)
            float  _Emissive;    // HDR multiplier near the head (bloom)
            float  _Opacity;
            TEXTURE2D(_LengthAlphaTex); SAMPLER(sampler_LengthAlphaTex);   // 256×1 alpha along the ribbon (head→tail)
            TEXTURE2D(_OpacityLifeTex); SAMPLER(sampler_OpacityLifeTex);    // 256×1 alpha over particle lifetime
            float  _UseOpacityLife;
            float  _SoftEdge;    // soft width edge (0..0.5)
            float  _SelfShadow;  // shader-only internal attenuation, 0 = off
            float  _Multiply;    // 1 = multiply blend premultiply

            // Procedural FIRE inside the strand (fragment-side). Animated BrazeFX fBm carves fire/smoke licks into
            // the ribbon's own surface; it distorts shading only and never moves geometry.
            float  _FireAmount;   // master: 0 = off (branch skips it) → 1 = full fire carved into the strand
            float  _FireScale;    // noise frequency in ribbon UV (flame size)
            float  _FireSpeed;    // flow speed along the length (fire streams)
            float  _FireDetail;   // fbm octaves (1..5)
            float  _FireSharp;    // flame-lick contrast (higher = crisper tongues, lower = soft glow)
            float  _FireEdge;     // erosion toward the ribbon edges (0 = fills, higher = wispy sides)
            float  _FireHeat;     // HDR heat-glow brightness added on hot noise spots (bloom); 0 = pure texture
            float  _FireTime;     // animation clock (Time.time)

            // Procedural smoke layer: same ribbon surface, no compute/buffers. It tints and thickens the trail with
            // noisy density so additive trails get smoky depth instead of a flat black multiplier.
            float  _SmokeAmount;
            float4 _SmokeLayerColor;
            float  _SmokeScale;
            float  _SmokeSpeed;
            float  _SmokeDetail;
            float  _SmokeDepth;
            float  _SmokeEdge;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;   // x = t along length, y = across width
                float  rampU      : TEXCOORD1;
                float  ageU       : TEXCOORD2;
                float  valid      : TEXCOORD3;   // 1 = live ribbon node, 0 = dead/unfilled (hard-hides recycled slots)
                float  seedPhase  : TEXCOORD4;   // stable per-particle 0..1 phase — decorrelates each strand's fire
            };

            float3 safeNrm(float3 v, float3 fb) { float l = length(v); return l > 1e-5 ? v / l : fb; }

            bool readNode(uint particleSlot, uint node, out LineNode n)
            {
                n = _LineNodes[particleSlot * _NodeCount + min(node, _NodeCount - 1u)];
                return n.misc.z > 0.5;
            }

            // ---- Procedural fire: BrazeFX baked-noise fBm ------------------------------------------
            // The base octave is a repeating baked gradient-noise texture (BrazeNoiseTex, bound globally) instead
            // of per-pixel simplex + sin-hash — that ALU chain, times fbm octaves, times trail overdraw, was the
            // flame path's FPS tank. The rotated-octave fbm structure (the look) is unchanged. LOD 0: callers sit
            // in branchy flow with no safe derivatives, and the 256² tile lives in cache anyway.
            TEXTURE2D(_BrazeNoise2D); SAMPLER(sampler_BrazeNoise2D);
            float fireNoise(float2 p)
            {
                return SAMPLE_TEXTURE2D_LOD(_BrazeNoise2D, sampler_BrazeNoise2D, p * (1.0 / 16.0), 0).r * 2.0 - 1.0;
            }
            float fireFbm(float2 uv, int octaves)
            {
                float f = 0.0, amp = 0.5;
                // Package-specific rotated octave basis and offset keep repetition out of long trails without
                // carrying anonymous external path/constant data into the distributable shader.
                float2x2 m = float2x2(1.67, 1.11, -1.11, 1.67);
                [loop] for (int k = 0; k < octaves; k++)
                {
                    f += amp * fireNoise(uv);
                    uv = mul(m, uv) + float2(13.17, 7.31);
                    amp *= 0.5;
                }
                return 0.5 + 0.5 * f;   // → ~0..1
            }

            // Indexed strip layout: 2 vertices per ribbon node (left/right). vid = node*2 + side, so each node's
            // spine point is transformed ~once (shared by its two adjacent quads via the post-transform cache).
            Varyings vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                Varyings o = (Varyings)0;

                uint node = vid >> 1u;             // 0.._Segments
                float ky  = (vid & 1u) ? 0.5 : -0.5;   // ribbon side
                uint particleSlot = _VisibleIndices[iid];

                // Compute already stores a smoothed tangent and collapses the first invalid cap node. Reading one
                // node instead of current+both neighbours removes two 48-byte structured-buffer loads per vertex.
                LineNode nN = (LineNode)0;
                bool vN = readNode(particleSlot, node, nN);
                float3 mN = nN.posT.xyz;

                float3 wN = mul(_Model, float4(mN, 1.0)).xyz;
                float3 fwd = mul((float3x3)_Model, nN.color.xyz);

                float t = saturate(nN.posT.w);

                float fwdLenSq = dot(fwd, fwd);
                bool hasNode = vN;
                bool hasTangent = fwdLenSq > 1e-8;
                float3 dir = hasTangent ? fwd * rsqrt(fwdLenSq) : float3(0, 1, 0);
                float3 view = safeNrm(_WorldSpaceCameraPos - wN, float3(0, 0, 1));
                float3 sideRaw = cross(dir, view);
                float sideLenSq = dot(sideRaw, sideRaw);
                if (sideLenSq < 1e-8)
                {
                    float3 axis = (abs(dir.y) < 0.9) ? float3(0, 1, 0) : float3(1, 0, 0);
                    sideRaw = cross(dir, axis);
                    sideLenSq = dot(sideRaw, sideRaw);
                }
                float3 side = sideLenSq > 1e-8 ? sideRaw * rsqrt(sideLenSq) : float3(1, 0, 0);
                // Width profile ALONG the ribbon (head t=0 → tail t=1) from the editable curve LUT. Vertex stage has no
                // derivatives, so sample an explicit LOD 0. The 256×1 half texture is tiny and stays cache-hot.
                float widthRand = frac(nN.misc.w * 37.17 + 0.193);
                float widthBase = lerp(min(_WidthMin, _WidthMax), max(_WidthMin, _WidthMax), widthRand);
                float width = widthBase * SAMPLE_TEXTURE2D_LOD(_WidthCurveTex, sampler_WidthCurveTex, float2(t, 0.5), 0.0).r;

                float3 worldPos;
                if (hasNode)       worldPos = wN + side * (ky * width);
                else               worldPos = wN;       // compute collapses the first dead cap; alpha hides the rest

                o.rampU = (_ColorMode > 1.5 && _ColorMode < 2.5) ? max(nN.misc.y, 0.0) : nN.misc.x;
                o.ageU = nN.misc.y;

                o.positionCS = TransformWorldToHClip(worldPos);
                o.uv = float2(t, ky + 0.5);
                o.valid = hasNode ? 1.0 : 0.0; // hide unfilled caps; tiny tangents use a fallback frame instead
                o.seedPhase = nN.misc.w;   // stable per-particle phase for the fire noise
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = i.uv.x;
                float across = i.uv.y;

                float3 col;
                float colorAlpha = 1.0;
                if (_ColorMode < 0.5)                       // 0 — Fire→Smoke length gradient
                {
                    float cm = max(_ColorMid, 1e-3);
                    float mixT = smoothstep(cm * 0.5, min(cm * 1.5, 1.0), t);
                    col = lerp(_FireColor.rgb, _SmokeColor.rgb, mixT);
                    colorAlpha = lerp(_FireColor.a, _SmokeColor.a, mixT);
                }
                else if (_ColorMode > 2.5)                  // 3 Constant
                {
                    col = _ConstColor.rgb;
                    colorAlpha = _ConstColor.a;
                }
                else                                        // 1 Gradient or 2 ByAge
                {
                    float u = (_ColorMode < 1.5) ? t : i.rampU;
                    float4 ramp = SAMPLE_TEXTURE2D(_Ramp, sampler_Ramp, float2(saturate(u), 0.5));
                    col = ramp.rgb;
                    colorAlpha = ramp.a;
                }
                col *= lerp(_Emissive, 1.0, saturate(t));          // hot/bloomy at the head, neutral at the tail

                // Base alpha is the trail's own opacity, gated by node validity.
                float a = _Opacity * saturate(colorAlpha) * i.valid;
                // Opacity along the ribbon LENGTH: head (t=0, on the particle) → tail (t=1) via the editable curve LUT.
                float lengthFade = SAMPLE_TEXTURE2D(_LengthAlphaTex, sampler_LengthAlphaTex, float2(saturate(t), 0.5)).r;
                a *= lengthFade;
                if (_UseOpacityLife > 0.5 && i.ageU >= 0.0)
                    a *= SAMPLE_TEXTURE2D(_OpacityLifeTex, sampler_OpacityLifeTex, float2(saturate(i.ageU), 0.5)).r;
                float e = max(_SoftEdge, 1e-3);
                float profileMask = smoothstep(0.0, e, across) * smoothstep(0.0, e, 1.0 - across);
                a *= profileMask;

                float selfShadowFactor = 1.0;
                [branch] if (_SelfShadow > 1e-4)
                {
                    float shadowCore = saturate(1.0 - abs(across - 0.5) * 2.0);
                    float localDensity = saturate(_Opacity * saturate(colorAlpha) * lengthFade * profileMask);
                    float optical = localDensity * (0.35 + 0.65 * shadowCore) * lerp(0.65, 1.25, saturate(t));
                    selfShadowFactor = lerp(1.0, 0.35 + 0.65 * exp2(-8.0 * optical), saturate(_SelfShadow));
                }

                [branch] if (_SmokeAmount > 1e-4)
                {
                    float smokeTail = smoothstep(0.02, 0.95, t);
                    float smokeCore = saturate(1.0 - abs(across - 0.5) * 2.0);
                    float smokeEdgeMask = smoothstep(0.0, 0.02 + _SmokeEdge * 0.28, across) *
                                          smoothstep(0.0, 0.02 + _SmokeEdge * 0.28, 1.0 - across);

                    float2 suv = float2((across - 0.5) * 1.65, t * 2.35) * max(_SmokeScale, 0.05);
                    suv += float2(i.seedPhase * 41.0, i.seedPhase * 73.0 - _FireTime * _SmokeSpeed);
                    int smokeOctaves = (int)clamp(_SmokeDetail, 1.0, 5.0);
                    float n0 = fireFbm(suv, smokeOctaves);
                    float n1 = fireFbm(suv * 0.57 + float2(n0 * 2.4, -n0 * 1.7) + 19.37, smokeOctaves);
                    float smoke = smoothstep(0.24, 0.92, lerp(n0, n1, 0.7));
                    smoke *= smokeEdgeMask * (0.25 + 0.75 * smokeCore) * (0.20 + 0.80 * smokeTail) * i.valid;
                    smoke = saturate(smoke * _SmokeAmount);

                    float depth = saturate(_SmokeDepth);
                    float3 smokeRgb = _SmokeLayerColor.rgb * lerp(0.65, 1.25, n1);
                    col = lerp(col, smokeRgb, smoke * depth);
                    a = saturate(a + smoke * saturate(_SmokeLayerColor.a) * 0.35);
                    selfShadowFactor = min(selfShadowFactor, lerp(1.0, 0.55 + 0.45 * (1.0 - smoke), depth * smoke));
                }
                col *= selfShadowFactor;
                clip(a - 1e-4);

                // ---- Procedural FIRE inside the strand (fragment) ----------------------------------
                [branch] if (_FireAmount > 1e-4)
                {
                    // Domain: x = across the width, y = along the length (stretched, so flames streak lengthwise).
                    // Scroll along the length over time so the fire streams; per-particle phase decorrelates strands.
                    float2 fuv = float2(across, t * 2.5) * _FireScale + i.seedPhase * 37.0;
                    fuv.y -= _FireTime * _FireSpeed;
                    float n = fireFbm(fuv, (int)clamp(_FireDetail, 1.0, 5.0));

                    // Hotter along the centreline, cooler toward the edges → reads as a flame core, not a flat wash.
                    float core  = saturate(1.0 - abs(across - 0.5) * 2.0);
                    float flame = saturate(n * (0.55 + 0.75 * core) - (1.0 - core) * _FireEdge);
                    flame = pow(flame, max(_FireSharp, 0.05));

                    // Heat glow: the classic fire ramp (white-hot → red), added on the hottest noise (HDR → bloom).
                    float3 heat = float3(flame, flame * flame * flame, flame * flame * flame * flame * flame * flame);
                    col += heat * (_FireHeat * flame * _FireAmount);

                    // Fire carves the strand's alpha into licking wisps.
                    a *= lerp(1.0, flame, _FireAmount);
                    clip(a - 1e-4);
                }

                a = saturate(a);

                if (_Multiply > 0.5) return half4(lerp(half3(1, 1, 1), col, a), a);
                return half4(col * a, a);          // premultiplied (additive / alpha)
            }
            ENDHLSL
        }
    }
}
