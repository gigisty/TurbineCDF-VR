Shader "BrazeFX/GeneratedVolumeRaymarch"
{
    Properties
    {
        _Volume ("Generated Volume", 3D) = "" {}
        _StepCount ("Ray Steps", Int) = 96
        _DensityScale ("Density Scale", Float) = 4
        _Brightness ("Brightness", Float) = 1
        _Contrast ("Contrast", Float) = 1.5
        _Opacity ("Opacity", Range(0, 1)) = 0.95
        _Illum ("Illumination", Float) = 1
        _Scatter ("Scatter", Float) = 0.7
        _AmbientIntensity ("Ambient Intensity", Float) = 0.5
        _Definition ("Edge Definition", Float) = 0.4
        _DepthFeather ("Soft Depth", Float) = 0.05
        [HDR] _CloudAlbedo ("Cloud Albedo", Color) = (1, 1, 1, 1)
        _Anisotropy ("Anisotropy g", Range(-0.95, 0.95)) = 0.35
        _SilverLining ("Silver Lining", Float) = 0.6
        _Powder ("Powder", Range(0, 1)) = 0.5
        [HDR] _SkyColor ("Sky Ambient", Color) = (0.28, 0.42, 0.62, 1)
        [HDR] _GroundColor ("Ground Ambient", Color) = (0.20, 0.16, 0.12, 1)
        [HDR] _EmissionColor ("Emission Colour", Color) = (1, 0.45, 0.12, 1)
        _EmissionAmount ("Emission Amount", Float) = 0
        _EmissionBase ("Emission Base Bias", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "BrazeGeneratedVolume"
            Tags { "LightMode" = "BrazeVolume" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            // These switches are set on runtime-created materials. shader_feature variants are stripped when
            // no serialized material owns the keyword combination, so Player builds must retain both branches.
            #pragma multi_compile_local_fragment _ _BRAZE_GEN_DETAIL
            #pragma multi_compile_local_fragment _ _BRAZE_GEN_TINT
            #pragma multi_compile_local_fragment _ _BRAZE_GEN_EMISSION
            #pragma multi_compile_local_fragment _ _BRAZE_GEN_RECEIVE_SHADOWS
            #pragma multi_compile_local_fragment _ _BRAZE_GEN_BAKED_LIGHT

            // Receive scene shadows: compile shadow variants so the in-march key light can be attenuated by URP's
            // main-light shadow map. The non-shadow variant stays free (the sampling is keyword-guarded below).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE3D(_Volume);
            SAMPLER(sampler_Volume);
            TEXTURE3D(_Occupancy);          // coarse max-pooled density (¼ res) for empty-space skipping
            SAMPLER(sampler_Occupancy);
            float _OccEnabled;
            float _OccStep;                 // conservative fixed leap: just under the smallest coarse-cell width
            TEXTURE3D(_DetailNoise);        // static seamless tiling noise (Repeat) for the render-time sharpness layer
            SAMPLER(sampler_DetailNoise);
            float _DetailAmt;               // 0 = off → free branch
            float _DetailFreq;              // 1 / world feature size
            float _DetailSharp;             // erosion hardness (higher = crisper grain)
            float _DetailWarp;              // domain-warp magnitude (wisps)

            float3 _WorldSize;
            int _StepCount;
            int _BrazeGeneratedStepBudget;
            float _DensityScale;
            float _Brightness;
            float _Contrast;
            float _Opacity;
            float _Illum;
            float _Scatter;
            float _Definition;
            float3 _KeyDirWS;
            float3 _KeyColor;
            float _KeyIntensity;
            float _AmbientIntensity;
            float _DepthFeather;
            float3 _SampleOffsetUvw;
            float _BrazeRayJitterFrame;

            // Temporal reprojection: emit a per-pixel screen-space motion vector so the shared temporal-resolve
            // denoiser can follow the volume under CAMERA motion (the static cloud has no fluid velocity) instead of
            // rejecting history on every orbit frame. cur→prev delta from the two view-proj matrices.
            float4x4 _BrazePrevVP;   // previous-frame view-projection
            float    _BrazeReproject; // 1 = accumulate + output motion vectors; 0 = write 0 (free)
            int      _BrazeInterleaveMode;  // 0 full, 1 rotating 2x1, 2 rotating 2x2
            int      _BrazeInterleavePhase;

            // Render-time CRISPNESS (conforming, ~free): a contrast expansion of the already-sampled density around a
            // pivot. It only reshapes existing structure into sharper edges — it adds no new noise and costs no taps.
            float _Crispen;     // 0 = off; blend toward the sharpened density
            float _CrispSharp;  // edge slope — higher steepens the soft falloff into a crisp surface
            float _CrispPivot;  // the density level that becomes the crisp boundary

            // Cloud/nebula shading (dedicated generator look).
            float3 _CloudAlbedo;
            int _TintMode;            // 0 Uniform, 1 ByDensity, 2 ByHeight
            float _Anisotropy;        // HG g
            float _SilverLining;      // extra forward-scatter spike
            float _Powder;            // Beer-Powder dark-edge term
            float3 _SkyColor;         // zenith ambient
            float3 _GroundColor;      // ground ambient
            float3 _EmissionColor;    // inner glow colour (decoupled from fire heat)
            float _EmissionAmount;    // glow strength from density
            float _EmissionBase;      // bias glow toward the underside
            TEXTURE2D(_TintRamp);
            SAMPLER(sampler_TintRamp);

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionOS = IN.positionOS.xyz;
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            bool IntersectBox(float3 ro, float3 rd, out float t0, out float t1)
            {
                float3 inv = 1.0 / rd;
                float3 a = (-0.5 - ro) * inv;
                float3 b = ( 0.5 - ro) * inv;
                float3 tmin = min(a, b);
                float3 tmax = max(a, b);
                t0 = max(max(tmin.x, tmin.y), tmin.z);
                t1 = min(min(tmax.x, tmax.y), tmax.z);
                return t1 > max(t0, 0.0);
            }

            float RayPhaseJitter(float2 pixel)
            {
                float2 off = frac(_BrazeRayJitterFrame * float2(0.754877666, 0.569840296)) * 64.0;
                return frac(52.9829189 * frac(dot(pixel + off, float2(0.06711056, 0.00583715))));
            }

            // World point → screen UV (ComputeScreenPos convention) for the temporal motion vector; same formula for
            // both endpoints so any platform y-flip cancels in the difference.
            float2 WorldToScreenUV(float3 posWS, float4x4 vp)
            {
                float4 cs = mul(vp, float4(posWS, 1.0));
                float w = (abs(cs.w) < 1e-6) ? 1e-6 : cs.w;
                return (float2(cs.x, cs.y * _ProjectionParams.x) + w) * 0.5 / w;
            }

            bool InterleavePixelValid(int2 pixel)
            {
                if (_BrazeInterleaveMode == 1) return ((pixel.x + _BrazeInterleavePhase) & 1) == 0;
                if (_BrazeInterleaveMode == 2)
                    return ((pixel.x & 1) + ((pixel.y & 1) << 1)) == _BrazeInterleavePhase;
                return true;
            }

            float4 SampleGenerated(float3 uvw)
            {
                // Clamp the scroll-shifted sample to the box (no transparent seam at the trailing edge).
                float3 q = clamp(uvw + _SampleOffsetUvw, 0.0, 1.0);
                return SAMPLE_TEXTURE3D_LOD(_Volume, sampler_Volume, q, 0);
            }

            float DensityAt(float3 uvw)
            {
                return max(SampleGenerated(uvw).x, 0.0);
            }

            float DensityResponse(float d)
            {
                float crisp = pow(saturate(d), max(_Contrast, 0.01));
                float w = saturate(abs(_Contrast - 1.0) / 3.0);
                return max(lerp(d, crisp, w), 0.0);
            }

            // Conforming crispness: contrast-expand density around the pivot so the soft edge band collapses into a
            // sharp boundary. Pure function of d — no taps, no new pattern. Off (Crispen=0) compiles to a no-op branch.
            float Crispen(float d)
            {
                if (_Crispen <= 1e-4) return d;
                float s = (d - _CrispPivot) * _CrispSharp + _CrispPivot;
                return max(lerp(d, s, _Crispen), 0.0);
            }

            float3 DensityNormal(float3 uvw, float3 texel)
            {
                float dx = DensityAt(uvw + float3(texel.x, 0, 0)) - DensityAt(uvw - float3(texel.x, 0, 0));
                float dy = DensityAt(uvw + float3(0, texel.y, 0)) - DensityAt(uvw - float3(0, texel.y, 0));
                float dz = DensityAt(uvw + float3(0, 0, texel.z)) - DensityAt(uvw - float3(0, 0, texel.z));
                float3 g = float3(dx, dy, dz);
                return -g / max(length(g), 1e-4);
            }

            // Henyey-Greenstein phase, scaled so g=0 → 1 (isotropic baseline). Positive g = forward scatter.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-3), 1.5);
            }

            // Scattering albedo: flat Cloud Albedo, or the Tint Ramp driven by density / height.
            float3 TintAlbedo(float d, float height)
            {
            #if defined(_BRAZE_GEN_TINT)
                float t = (_TintMode == 2) ? saturate(height) : saturate(d);
                float3 ramp = SAMPLE_TEXTURE2D_LOD(_TintRamp, sampler_TintRamp, float2(t, 0.5), 0).rgb;
                return ramp * _CloudAlbedo.rgb;
            #else
                return _CloudAlbedo.rgb;
            #endif
            }

            // Phase is ray-invariant: compute its three octave weights ONCE, not at every occupied sample.
            float3 ScatterPhaseWeights(float cosT)
            {
                float carry = lerp(0.5, 0.85, saturate(_Scatter));
                float silver = _SilverLining * pow(saturate(cosT), 8.0);
                float g0 = _Anisotropy;
                return float3(HenyeyGreenstein(cosT, g0) + silver,
                              carry * HenyeyGreenstein(cosT, g0 * 0.6),
                              carry * carry * HenyeyGreenstein(cosT, g0 * 0.36));
            }

            float MultiScatter(float Tl, float3 phaseWeights)
            {
                // Original exponents are 1, 1/2, 1/4. Exact square roots avoid three generic per-sample pow calls.
                float t0 = saturate(Tl);
                float t1 = sqrt(t0);
                float t2 = sqrt(t1);
                return dot(phaseWeights, float3(t0, t1, t2));
            }

            // Realtime main-light shadow at an object-space sample position (1 = lit, 0 = in shadow). Keyword-guarded
            // so the no-shadow variant compiles to a constant 1 and costs nothing.
            float SceneShadow(float3 posOS)
            {
            #if defined(_BRAZE_GEN_RECEIVE_SHADOWS) && (defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE))
                float4 sc = TransformWorldToShadowCoord(TransformObjectToWorld(posOS));
                return MainLightRealtimeShadow(sc);
            #endif
                return 1.0;
            }

            void frag(Varyings IN, out half4 outColor : SV_Target0, out half2 outMotion : SV_Target1)
            {
                bool perspective = IsPerspectiveProjection();
                float3 camOS = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 rd = perspective
                    ? normalize(IN.positionOS - camOS)
                    : normalize(TransformWorldToObjectDir(GetViewForwardDir()));
                float3 ro = perspective ? camOS : IN.positionOS - rd * 2.0;

                float t0, t1;
                if (!IntersectBox(ro, rd, t0, t1)) discard;
                t0 = max(t0, 0.0);

                float2 suv = IN.screenPos.xy / IN.screenPos.w;
                float rawDepth = SampleSceneDepth(suv);
                float3 sceneWS = ComputeWorldSpacePosition(suv, rawDepth, UNITY_MATRIX_I_VP);
                float3 sceneOS = TransformWorldToObject(sceneWS);
                float sceneT = dot(sceneOS - ro, rd);
                t1 = min(t1, sceneT);
                if (t1 <= t0) discard;

                // Missing interleaved pixels still emit a cheap camera-motion estimate from the box midpoint.
                if (!InterleavePixelValid(int2(IN.positionHCS.xy)))
                {
                    outColor = 0.0;
                    half2 mv = 0.0;
                    [branch] if (_BrazeReproject > 0.5)
                    {
                        float3 midpointWS = TransformObjectToWorld(ro + rd * ((t0 + t1) * 0.5));
                        mv = (half2)(WorldToScreenUV(midpointWS, _BrazePrevVP) - suv);
                    }
                    outMotion = mv;
                    return;
                }

                // Braze Render Feature is authoritative when this shader is drawn through its volume pass. Retain
                // the material value only as a compatibility fallback for a direct/custom draw path.
                int baseSteps = max(_BrazeGeneratedStepBudget > 0 ? _BrazeGeneratedStepBudget : _StepCount, 1);
                float dt = 1.0 / baseSteps;
                int maxIter = baseSteps * 2;
                float worldStep = length(rd * _WorldSize) * dt;
                float3 lightDirWS = normalize(_KeyDirWS);
                float3 rayDirWS = normalize(mul((float3x3)unity_ObjectToWorld, rd));
                float3 keyCol = _KeyColor * _KeyIntensity;

                float trans = 1.0;
                float3 color = 0.0;
            #if defined(_BRAZE_GEN_RECEIVE_SHADOWS)
                float3 keyColorAccum = 0.0;
                float3 fillColorAccum = 0.0;
            #endif
                float jbase = RayPhaseJitter(IN.positionHCS.xy);
                float t = t0;
                float cosT = dot(rayDirWS, lightDirWS);
                float3 phaseWeights = ScatterPhaseWeights(cosT);

                // Motion-vector centroid (camera-only: static field, no fluid velocity), opacity-weighted.
                float mvW = 0.0;
                float mvT = 0.0;

                [loop]
                for (int i = 0; i < 512; i++)
                {
                    if (i >= maxIter || t >= t1 || trans < 0.01) break;

                    float jitter = frac(jbase + i * 0.61803398875);
                    float ts = min(t + jitter * dt, t1);
                    float3 pOS = ro + rd * ts;
                    float3 uvw = pOS + 0.5;

                    // The occupancy was max-pooled and (when detail warps samples) conservatively dilated at bake
                    // time. A fixed sub-cell stride is deliberately cheaper than per-empty-cell DDA vector math.
                    float3 occQ = saturate(uvw + _SampleOffsetUvw);
                    [branch] if (_OccEnabled > 0.5 &&
                        SAMPLE_TEXTURE3D_LOD(_Occupancy, sampler_Occupancy, occQ, 0).x < 1e-4)
                    {
                        t += max(dt, _OccStep);
                        continue;
                    }

                    // Render-time sharpness: one fetch of the tiling noise in WORLD space (Repeat → resolution-
                    // independent grain). Warp the density sample for wisps + carry the scalar for a crisp erosion.
                    float dn = 1.0;
                #if defined(_BRAZE_GEN_DETAIL)
                        float4 n4 = SAMPLE_TEXTURE3D_LOD(_DetailNoise, sampler_DetailNoise, pOS * _WorldSize * _DetailFreq, 0);
                        uvw += (n4.yzw - 0.5) * _DetailWarp;
                        dn = n4.x;
                #endif

                    // Static bake packs density + self-shadow + sky occlusion into one full-res field sample.
                    float4 generated = SampleGenerated(uvw);
                    float dens = DensityResponse(Crispen(max(generated.x, 0.0)));

                    // Crisp grain: hard-edged noise mask, biased to the rendered surface so the body/haze stay clean.
                #if defined(_BRAZE_GEN_DETAIL)
                    [branch] if (dens > 1e-4)
                    {
                        float mask = saturate((dn - 0.5) * _DetailSharp + 0.5);
                        float edge = saturate(dens * (1.0 - dens) * 4.0);
                        dens *= saturate(1.0 - _DetailAmt * (1.0 - mask) * edge);
                    }
                #endif

                    if (dens > 1e-5)
                    {
                    #if defined(_BRAZE_GEN_BAKED_LIGHT)
                        float Tl = saturate(generated.y);
                        float occ = saturate(generated.z);
                    #else
                        float Tl = 1.0;
                        float occ = 1.0;
                    #endif
                        if (_Definition > 1e-3) Tl = pow(Tl, 1.0 + _Definition);        // deepen shadow for more form

                        float powderTerm = 1.0 - exp(-dens * _DensityScale * 2.0);
                        float powderFac = lerp(1.0, 2.0 * powderTerm, _Powder);

                        float3 albedo  = TintAlbedo(dens, uvw.y);
                        float3 keyLit  = keyCol * _Illum * MultiScatter(Tl, phaseWeights) * powderFac;
                        float3 ambient = lerp(_GroundColor, _SkyColor, uvw.y) * _AmbientIntensity * occ;

                    #if defined(_BRAZE_GEN_EMISSION)
                        // Emission derived from density (render-side), biased toward the underside if requested.
                        float baseGate = lerp(1.0, smoothstep(0.0, 0.5, 1.0 - uvw.y), _EmissionBase);
                        float glow = saturate(dens) * _EmissionAmount * baseGate;
                        float3 emitted = _EmissionColor * glow;
                    #else
                        float3 emitted = 0.0;
                    #endif
                        float3 stepKey = albedo * keyLit * _Brightness;
                        float3 stepFill = (albedo * ambient + emitted) * _Brightness;

                        float a = 1.0 - exp(-dens * _DensityScale * _Opacity * worldStep);
                        float depthFade = (_DepthFeather > 1e-5) ? saturate((sceneT - ts) / _DepthFeather) : 1.0;
                        a *= depthFade;

                    #if defined(_BRAZE_GEN_RECEIVE_SHADOWS)
                        keyColorAccum += trans * stepKey * a;
                        fillColorAccum += trans * stepFill * a;
                        float shadowContrib = trans * a;
                        mvW += shadowContrib;
                        mvT += ts * shadowContrib;
                    #else
                        color += trans * (stepKey + stepFill) * a;
                    #endif

                    #if !defined(_BRAZE_GEN_RECEIVE_SHADOWS)
                        [branch] if (_BrazeReproject > 0.5)
                        {
                            float contrib = trans * a;
                            [branch] if (contrib > 1e-4)
                            {
                                mvW  += contrib;
                                mvT  += ts * contrib;
                            }
                        }
                    #endif

                        trans *= 1.0 - a;
                    }

                    t += dt;
                }

            #if defined(_BRAZE_GEN_RECEIVE_SHADOWS)
                if (mvW > 1e-5)
                {
                    float3 shadowCentroidOS = ro + rd * (mvT / mvW);
                    color = fillColorAccum + keyColorAccum * SceneShadow(shadowCentroidOS);
                }
                else color = fillColorAccum + keyColorAccum;
            #endif
                outColor = half4(color, 1.0 - trans);

                // Camera-motion screen-space vector: reproject the same world centroid through cur vs prev view-proj.
                half2 mv = 0.0;
                [branch] if (_BrazeReproject > 0.5 && mvW > 1e-5)
                {
                    float3 cOS = ro + rd * (mvT / mvW);
                    float3 cWS = TransformObjectToWorld(cOS);
                    // The centroid lies on this pixel's current view ray, so its current projection is exactly suv.
                    mv = (half2)(WorldToScreenUV(cWS, _BrazePrevVP) - suv);
                }
                outMotion = mv;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
