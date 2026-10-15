// BrazeFX Surface Disintegrator — CHUNK shader. Draws the "shattered" mesh once; a per-vertex lookup positions
// each vertex by its CLUMP's state (from a StructuredBuffer the component fills each frame):
//   ANCHORED → the vertex's current SKINNED/deformed position (still locked to the body, conforms to animation).
//   FREE     → a per-clump rigid transform (position + rotation) of the shape captured at release.
// So before release the mesh looks IDENTICAL to the body; released clumps fly off as rigid pieces, leaving holes.
//
// Shading faithfully reproduces URP/Lit (UniversalFragmentPBR + shadows + fog) so the disintegrator looks exactly
// like the original material until it breaks. The component copies the source material's PBR properties across.
Shader "BrazeFX/Chunk"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _MetallicGlossMap ("Metallic Map", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
        _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        _OcclusionStrength ("Occlusion", Range(0,1)) = 1.0
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _Ambient ("Extra Ambient Fill", Range(0,1)) = 0.0
        [HideInInspector] _Cull ("Cull", Float) = 0
        _VxTriplanar ("Voronoi Triplanar", Float) = 0
        _VxTriScale ("Voronoi Tri Scale", Float) = 1
        _VxInterior ("Voronoi Interior", Float) = 0
        _InteriorTex ("Interior Albedo", 2D) = "white" {}
        _InteriorColor ("Interior Color", Color) = (0.28,0.25,0.22,1)
        _InteriorMetallic ("Interior Metallic", Range(0,1)) = 0
        _InteriorSmoothness ("Interior Smoothness", Range(0,1)) = 0.22
        _VxDetail ("Voronoi Detail FX", Float) = 0
        _VxDetailAmp ("Voronoi Detail Amount", Float) = 0
        _VxDetailScale ("Voronoi Detail Scale", Float) = 5
        _VxDetailOct ("Voronoi Detail Octaves", Float) = 3
        _VxDetailContrast ("Voronoi Detail Contrast", Range(0,1)) = 0.25
        _VxEdge ("Voronoi Edge Detail", Float) = 0
        _VxEdgeColor ("Voronoi Edge Color", Color) = (0.07,0.055,0.045,1)
        _VxEdgeWidth ("Voronoi Edge Width", Float) = 1.6
        _VxEdgeStrength ("Voronoi Edge Strength", Range(0,1)) = 0.7
        _VxEdgeNoise ("Voronoi Edge Noise", Range(0,1)) = 0.35
        _VxEdgeScale ("Voronoi Edge Noise Scale", Float) = 12
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }

        // ── shared: clump buffers + per-vertex displacement (anchored vs free) ───────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);          SAMPLER(sampler_MainTex);
        TEXTURE2D(_MetallicGlossMap);
        TEXTURE2D(_BumpMap);
        TEXTURE2D(_OcclusionMap);
        TEXTURE2D(_EmissionMap);
        TEXTURE2D(_InteriorTex);      SAMPLER(sampler_InteriorTex);
        float4 _MainTex_ST;
        float4 _InteriorTex_ST;
        float4 _Color;
        float4 _InteriorColor;
        float  _Metallic, _Smoothness, _BumpScale, _OcclusionStrength, _Ambient;
        float  _InteriorMetallic, _InteriorSmoothness;
        float4 _EmissionColor;
        float  _HasMetallicMap, _HasBump, _HasOcclusionMap, _HasEmissionMap;

        StructuredBuffer<float4> _ClumpPosState;  // xyz = free-clump world position, w = state (0 anchored, 1 free)
        StructuredBuffer<float4> _ClumpRot;       // free-clump rotation quaternion
        StructuredBuffer<float3> _DeformedPos;    // per ORIGINAL vertex: current deformed position
        StructuredBuffer<float3> _DeformedNrm;    // per ORIGINAL vertex: current deformed normal
        StructuredBuffer<float3> _FreeOffset;     // per SHATTERED vertex: offset from clump centroid, captured at release
        StructuredBuffer<float3> _FreeNrm;        // per SHATTERED vertex: normal captured at release
        StructuredBuffer<float4> _ClumpLife;      // per clump: x = scale, y = dissolve amount, z = speed
        StructuredBuffer<float4> _ClumpCentroid;  // per clump: anchored world-space pivot
        StructuredBuffer<float4> _EffectorPosition;
        StructuredBuffer<float4> _EffectorRotation;
        StructuredBuffer<float4> _EffectorScale;
        int _EffectorCount;
        float4x4 _L2W;                            // deformed buffer → world (identity for GPU-extracted world buffers)
        float4x4 _N2W;                            // normal transform matching _L2W
        float  _SoftAmount;                       // soft-body wobble for free chunks (0 = off)
        float  _DissolveScale;                    // dissolve grain frequency
        float4 _DissolveEdge;                     // glow colour at the dissolve edge (rgb × a)
        float  _DrawMode;                         // 0 = all chunks, 1 = free chunks only
        float  _PreviewExplode;                   // editor-only world distance; zero in play / disintegrator paths
        float3 _PreviewCenterWS;
        float  _PreviewColorize;
        float  _PreviewSlice;
        float4 _PreviewSlicePlane;                // world plane: dot(n,p)+d; positive side is removed
        float  _PreviewStale;

        // Voronoi render-time detailing. All switches default OFF so the shared Surface Disintegrator path remains
        // unchanged. Cut faces use a dedicated single-pass interior layer; relief and crease wear are shader-only.
        float _VxInterior, _VxTriplanar, _VxTriScale;
        float _VxDetail, _VxDetailAmp, _VxDetailScale, _VxDetailOct, _VxDetailContrast;
        float _VxEdge, _VxEdgeWidth, _VxEdgeStrength, _VxEdgeNoise, _VxEdgeScale;
        float4 _VxEdgeColor;

        float3 qrot(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
        float h31(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
        float noise3(float3 p)
        {
            float3 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(lerp(h31(i + float3(0,0,0)), h31(i + float3(1,0,0)), f.x),
                             lerp(h31(i + float3(0,1,0)), h31(i + float3(1,1,0)), f.x), f.y),
                        lerp(lerp(h31(i + float3(0,0,1)), h31(i + float3(1,0,1)), f.x),
                             lerp(h31(i + float3(0,1,1)), h31(i + float3(1,1,1)), f.x), f.y), f.z);
        }

        float vfbm(float3 p)
        {
            float s = 0.0, a = 0.5, f = 1.0;
            int oct = (int)clamp(_VxDetailOct, 1.0, 6.0);
            [loop] for (int i = 0; i < oct; i++) { s += a * noise3(p * f); f *= 2.0; a *= 0.5; }
            return s;
        }

        // Dedicated interior triplanar projection. It uses rest space, so the texture remains attached while chunks
        // tumble, and never falls back to the outer mesh UV (which is undefined for synthetic cap vertices).
        half4 SampleInteriorTriplanar(float3 p, float3 n)
        {
            float3 bw = pow(abs(n), 4.0); bw /= (bw.x + bw.y + bw.z + 1e-4);
            float2 uvx = p.zy * (_VxTriScale * _InteriorTex_ST.xy) + _InteriorTex_ST.zw;
            float2 uvy = p.xz * (_VxTriScale * _InteriorTex_ST.xy) + _InteriorTex_ST.zw;
            float2 uvz = p.xy * (_VxTriScale * _InteriorTex_ST.xy) + _InteriorTex_ST.zw;
            half4 cx = SAMPLE_TEXTURE2D(_InteriorTex, sampler_InteriorTex, uvx);
            half4 cy = SAMPLE_TEXTURE2D(_InteriorTex, sampler_InteriorTex, uvy);
            half4 cz = SAMPLE_TEXTURE2D(_InteriorTex, sampler_InteriorTex, uvz);
            return cx * bw.x + cy * bw.y + cz * bw.z;
        }

        // Fake surface relief: perturb the normal by the gradient of a procedural height (Mikkelsen derivative
        // bump). height is sampled in REST space (sticks to the chunk); the surface frame uses posWS so the bump
        // orients correctly as the chunk tumbles.
        float3 ApplyDetailBump(float3 n, float3 posWS, float height)
        {
            float3 dpx = ddx(posWS), dpy = ddy(posWS);
            float dhx = ddx(height), dhy = ddy(height);
            float3 r1 = cross(dpy, n), r2 = cross(n, dpx);
            float det = dot(dpx, r1);
            float3 grad = abs(det) > 1e-8 ? (dhx * r1 + dhy * r2) / det : float3(0, 0, 0);
            return normalize(n - _VxDetailAmp * grad);
        }

        // Screen-pixel distance to CPU-selected real creases. bary.xyz is generated from the existing triangle-soup
        // vertex id; edgeMask stores the three selected opposite-edge bits in UV0.w. No extra mesh channel is needed.
        float FractureEdgeFactor(float3 bary, float edgeMask, float3 restPos, float clumpId)
        {
            int mask = (int)(edgeMask + 0.5);
            float3 selected = float3(mask & 1, (mask >> 1) & 1, (mask >> 2) & 1);
            float3 pixelDistance = bary / max(fwidth(bary), float3(1e-4, 1e-4, 1e-4));
            float3 distanceOrFar = lerp(float3(1e4, 1e4, 1e4), pixelDistance, selected);
            float distanceToEdge = min(distanceOrFar.x, min(distanceOrFar.y, distanceOrFar.z));
            float width = _VxEdgeWidth;
            if (_VxEdgeNoise > 0.001)
            {
                float chip = noise3(restPos * _VxEdgeScale + clumpId * 0.173);
                width *= lerp(1.0, lerp(0.45, 1.45, chip), _VxEdgeNoise);
            }
            return (1.0 - smoothstep(width, width + 1.0, distanceToEdge)) * _VxEdgeStrength;
        }

        // Resolve a shattered vertex to its current world position + normal, plus per-clump dissolve / state.
        void ComputeChunk(uint vid, float2 ids, out float3 wp, out float3 wn, out float diss, out float state)
        {
            uint clumpId = (uint)(ids.x + 0.5);
            uint origIdx = (uint)(ids.y + 0.5);
            float4 ps = _ClumpPosState[clumpId];
            float4 life = _ClumpLife[clumpId];      // x scale, y dissolve, z speed
            state = ps.w;
            diss = 0.0;
            if (ps.w < 0.5)   // anchored — follow the skinned/deformed body
            {
                wp = mul(_L2W, float4(_DeformedPos[origIdx], 1.0)).xyz;
                wn = normalize(mul((float3x3)_N2W, _DeformedNrm[origIdx]));
            }
            else              // free — rigid body from the captured shape (shrunk + maybe wobbling)
            {
                float4 q = _ClumpRot[clumpId];
                wp = ps.xyz + qrot(q, _FreeOffset[vid] * life.x);   // life.x = shrink scale
                wn = qrot(q, _FreeNrm[vid]);
                diss = life.y;
                if (_SoftAmount > 0.0)   // cheap speed-scaled wobble — pure vertex math, no sim
                {
                    float t = _Time.y, s = _SoftAmount * 0.03 * life.z;
                    wp += float3(sin(wp.y * 9.0 + t * 4.0), sin(wp.z * 9.0 + t * 4.3), sin(wp.x * 9.0 + t * 3.7)) * s;
                }
            }

            // Voronoi-only direct-transform layer. Surface Disintegrator materials leave _EffectorCount at zero,
            // so their shared Chunk shader path remains unchanged and does not touch these buffers.
            if (_EffectorCount > 0)
            {
                float3 pivot = ps.w < 0.5 ? _ClumpCentroid[clumpId].xyz : ps.xyz;
                float3 effScale = max(_EffectorScale[clumpId].xyz, float3(0.001, 0.001, 0.001));
                float4 effRot = _EffectorRotation[clumpId];
                wp = pivot + qrot(effRot, (wp - pivot) * effScale) + _EffectorPosition[clumpId].xyz;
                wn = normalize(qrot(effRot, wn / effScale));
            }

            if (_PreviewExplode > 0.0)
            {
                float3 pivot = ps.w < 0.5 ? _ClumpCentroid[clumpId].xyz : ps.xyz;
                float3 away = pivot - _PreviewCenterWS;
                float lenSq = dot(away, away);
                if (lenSq > 1e-10) wp += away * rsqrt(lenSq) * _PreviewExplode;
            }
        }

        void PreviewClip(float3 posWS)
        {
            if (_PreviewSlice > 0.5)
                clip(-(dot(_PreviewSlicePlane.xyz, posWS) + _PreviewSlicePlane.w));
        }

        half3 PreviewFragmentColor(float id)
        {
            float3 h = frac(sin(float3(id * 12.9898 + 1.7, id * 78.233 + 9.2, id * 39.425 + 4.1)) * 43758.5453);
            return lerp(half3(0.18, 0.34, 0.62), saturate(h * 0.72 + 0.28), 0.78);
        }

        // Noise-dissolve cutout (free chunks crumble away). Returns the noise sample for edge-glow reuse.
        float DissolveClip(float diss, float3 posWS)
        {
            float nz = diss > 0.0 ? noise3(posWS * _DissolveScale) : 1.0;
            clip(nz - diss);
            return nz;
        }
        ENDHLSL

        // ── forward lit: faithful URP/Lit shading ──────────────────────────────────────
        Pass
        {
            Name "ChunkForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]      // Surface Disintegrator stays two-sided; Voronoi closed cells force back-face culling.
            ZWrite On ZTest LEqual   // opaque cells: write depth so they occlude + integrate with the depth texture

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Unity 6.0/URP 17.0 uses _FORWARD_PLUS; Unity 6.3/URP 17.3 renamed the
            // same clustered-light path to _CLUSTER_LIGHT_LOOP. Keep both so fracture
            // chunks receive additional lights in fresh Forward+ projects across the
            // full supported Unity range.
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            // Braze creates chunk materials at runtime, so both branches must survive Player shader stripping.
            #pragma multi_compile_local _ _VX_INTERIOR_RELIEF
            #pragma multi_compile_local _ _VX_EDGE_DETAIL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attr
            {
                float4 positionOS : POSITION;     // unused (kept so DrawMesh has a position stream)
                float4 uv  : TEXCOORD0;           // xy = original albedo UV, z = 1 for render-skin verts (real surface)
                float2 ids : TEXCOORD1;           // x = clumpId, y = original vertex index
                uint   vid : SV_VertexID;
            };
            struct Vary
            {
                float4 hcs : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 nrmWS : TEXCOORD1;
                float3 posWS : TEXCOORD2;
                float  dissolve : TEXCOORD3;
                float  state : TEXCOORD4;
                float  fogCoord : TEXCOORD5;
                float3 restPos : TEXCOORD6;   // rest-space position for triplanar/detail (sticks to the chunk)
                float2 skinClump : TEXCOORD7; // x: skin flag; y: stable clump id for authoring colour
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                half3 vertexLighting : TEXCOORD9;
            #endif
            #if defined(_VX_EDGE_DETAIL)
                float4 baryEdge : TEXCOORD8;  // xyz: per-triangle barycentric; w: selected real-crease bit mask
            #endif
            };

            Vary vert(Attr IN)
            {
                Vary o;
                float3 wp, wn;
                ComputeChunk(IN.vid, IN.ids, wp, wn, o.dissolve, o.state);
                o.hcs = TransformWorldToHClip(wp);
                o.uv = TRANSFORM_TEX(IN.uv.xy, _MainTex);
                o.nrmWS = wn;
                o.posWS = wp;
                o.fogCoord = ComputeFogFactor(o.hcs.z);
                o.restPos = _DeformedPos[(uint)(IN.ids.y + 0.5)];
                o.skinClump = float2(IN.uv.z, IN.ids.x);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                o.vertexLighting = VertexLighting(wp, normalize(wn));
            #endif
            #if defined(_VX_EDGE_DETAIL)
                uint corner = IN.vid % 3;
                o.baryEdge.xyz = corner == 0 ? float3(1, 0, 0)
                                  : corner == 1 ? float3(0, 1, 0) : float3(0, 0, 1);
                o.baryEdge.w = IN.uv.w;
            #endif
                return o;
            }

            half4 frag(Vary IN, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                if (_DrawMode > 0.5) clip(IN.state - 0.5);
                PreviewClip(IN.posWS);
                float nz = DissolveClip(IN.dissolve, IN.posWS);

                bool interior = _VxInterior > 0.5 && IN.skinClump.x < 0.5;
                half4 albedo;
                if (interior)
                {
                    albedo = _InteriorColor;
                    if (_VxTriplanar > 0.5)
                        albedo *= SampleInteriorTriplanar(IN.restPos, normalize(IN.nrmWS));
                }
                else
                {
                    albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Color;
                }
                if (_PreviewColorize > 0.5)
                    albedo.rgb = lerp(albedo.rgb, PreviewFragmentColor(IN.skinClump.y), 0.62);

                // two-sided: flip the normal for back faces so tumbling chunks light correctly
                float3 n = normalize(IN.nrmWS);
                n *= IS_FRONT_VFACE(facing, 1.0, -1.0);

            #if defined(_VX_INTERIOR_RELIEF)
                float detailHeight = 0.5;
                if (interior)
                {
                    detailHeight = vfbm(IN.restPos * _VxDetailScale);
                    if (_VxDetailAmp > 0.0) n = ApplyDetailBump(n, IN.posWS, detailHeight);
                    float tone = lerp(0.68, 1.22, detailHeight);
                    albedo.rgb *= lerp(1.0, tone, _VxDetailContrast);
                }
            #endif

                if (_HasBump > 0.5 && !interior)   // source normal map belongs to the original outer skin only
                {
                    float3 dpx = ddx(IN.posWS), dpy = ddy(IN.posWS);
                    float2 dux = ddx(IN.uv),   duy = ddy(IN.uv);
                    float3 t = normalize(dpx * duy.y - dpy * dux.y);
                    float3 b = -normalize(cross(n, t));
                    float3 nts = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_MainTex, IN.uv), _BumpScale);
                    n = normalize(nts.x * t + nts.y * b + nts.z * n);
                }

                float metallic = interior ? _InteriorMetallic : _Metallic;
                float smoothness = interior ? _InteriorSmoothness : _Smoothness;
                if (!interior && _HasMetallicMap > 0.5)
                {
                    half4 mg = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MainTex, IN.uv);
                    metallic = mg.r;
                    smoothness = mg.a * _Smoothness;
                }
            #if defined(_VX_INTERIOR_RELIEF)
                if (interior)
                    smoothness *= lerp(1.0, lerp(0.55, 0.9, detailHeight), _VxDetailContrast);
            #endif

            #if defined(_VX_EDGE_DETAIL)
                float edgeFactor = 0.0;
                if (IN.baryEdge.w > 0.5)
                {
                    edgeFactor = FractureEdgeFactor(IN.baryEdge.xyz, IN.baryEdge.w, IN.restPos, IN.skinClump.y);
                    albedo.rgb = lerp(albedo.rgb, _VxEdgeColor.rgb, edgeFactor);
                    smoothness = lerp(smoothness, min(smoothness, 0.12), edgeFactor);
                }
            #endif

                half occlusion = !interior && _HasOcclusionMap > 0.5
                    ? lerp(1.0, SAMPLE_TEXTURE2D(_OcclusionMap, sampler_MainTex, IN.uv).g, _OcclusionStrength)
                    : 1.0;
                half3 emission = interior ? half3(0, 0, 0) : _EmissionColor.rgb;
                if (!interior && _HasEmissionMap > 0.5)
                    emission *= SAMPLE_TEXTURE2D(_EmissionMap, sampler_MainTex, IN.uv).rgb;

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.posWS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = normalize(GetWorldSpaceViewDir(IN.posWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.posWS);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.hcs);
                inputData.shadowMask = half4(1, 1, 1, 1);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                inputData.vertexLighting = IN.vertexLighting;
            #endif

                SurfaceData surf = (SurfaceData)0;
                surf.albedo = albedo.rgb;
                surf.metallic = saturate(metallic);
                surf.smoothness = saturate(smoothness);
                surf.occlusion = occlusion;
                surf.emission = emission;
                surf.alpha = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surf);
                // Thousands of detached, rotating cells can mutually shadow until direct light disappears. Apply a
                // lower bound only to FREE chunks and only to pixels below it. Unlike additive ambient this cannot
                // lift highlights or flatten normally lit surfaces, and it adds no light/shadow/texture sample.
                color.rgb = max(color.rgb, albedo.rgb * (_Ambient * saturate(IN.state)));

                if (IN.dissolve > 0.0)                            // glow at the crumbling edge
                {
                    float edge = 1.0 - saturate((nz - IN.dissolve) / 0.12);
                    color.rgb += _DissolveEdge.rgb * (edge * _DissolveEdge.a);
                }
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                if (_PreviewStale > 0.5)
                    color.rgb = lerp(color.rgb, color.rgb * 0.58 + half3(0.48, 0.19, 0.035), 0.42);
                return half4(color.rgb, 1.0);
            }
            ENDHLSL
        }

        // ── shadow caster: chunks cast shadows just like the original body ───────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual Cull [_Cull] ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attr { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 ids : TEXCOORD1; uint vid : SV_VertexID; };
            struct Vary { float4 hcs : SV_POSITION; float2 uv : TEXCOORD0; float3 posWS : TEXCOORD1; float dissolve : TEXCOORD2; float state : TEXCOORD3; };

            Vary vert(Attr IN)
            {
                Vary o;
                float3 wp, wn;
                ComputeChunk(IN.vid, IN.ids, wp, wn, o.dissolve, o.state);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 ld = normalize(_LightPosition - wp);
            #else
                float3 ld = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(wp, wn, ld));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.hcs = cs;
                o.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                o.posWS = wp;
                return o;
            }

            half4 frag(Vary IN) : SV_Target
            {
                if (_DrawMode > 0.5) clip(IN.state - 0.5);
                PreviewClip(IN.posWS);
                DissolveClip(IN.dissolve, IN.posWS);
                return 0;
            }
            ENDHLSL
        }

        // ── depth only (depth prepass / soft-depth volume compositing) ───────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attr { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 ids : TEXCOORD1; uint vid : SV_VertexID; };
            struct Vary { float4 hcs : SV_POSITION; float2 uv : TEXCOORD0; float3 posWS : TEXCOORD1; float dissolve : TEXCOORD2; float state : TEXCOORD3; };

            Vary vert(Attr IN)
            {
                Vary o;
                float3 wp, wn;
                ComputeChunk(IN.vid, IN.ids, wp, wn, o.dissolve, o.state);
                o.hcs = TransformWorldToHClip(wp);
                o.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                o.posWS = wp;
                return o;
            }

            half4 frag(Vary IN) : SV_Target
            {
                if (_DrawMode > 0.5) clip(IN.state - 0.5);
                PreviewClip(IN.posWS);
                DissolveClip(IN.dissolve, IN.posWS);
                return 0;
            }
            ENDHLSL
        }

        // ── depth + normals (SSAO / depth-normals prepass) ──────────────────────────────
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attr { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 ids : TEXCOORD1; uint vid : SV_VertexID; };
            struct Vary { float4 hcs : SV_POSITION; float2 uv : TEXCOORD0; float3 nrmWS : TEXCOORD1; float3 posWS : TEXCOORD2; float dissolve : TEXCOORD3; float state : TEXCOORD4; };

            Vary vert(Attr IN)
            {
                Vary o;
                float3 wp, wn;
                ComputeChunk(IN.vid, IN.ids, wp, wn, o.dissolve, o.state);
                o.hcs = TransformWorldToHClip(wp);
                o.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                o.nrmWS = wn;
                o.posWS = wp;
                return o;
            }

            half4 frag(Vary IN, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                if (_DrawMode > 0.5) clip(IN.state - 0.5);
                PreviewClip(IN.posWS);
                DissolveClip(IN.dissolve, IN.posWS);
                float3 n = normalize(IN.nrmWS) * IS_FRONT_VFACE(facing, 1.0, -1.0);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalize(n));
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                return half4(PackFloat2To888(remappedOctNormalWS), 0.0);
            #else
                return half4(NormalizeNormalPerPixel(n), 0.0);
            #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
