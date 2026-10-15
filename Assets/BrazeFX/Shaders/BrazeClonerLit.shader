Shader "BrazeFX/Cloner/Lit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _WorkflowMode ("Workflow Mode", Float) = 1
        _Metallic ("Metallic", Range(0,1)) = 0
        _MetallicGlossMap ("Metallic Map", 2D) = "white" {}
        _SpecColor ("Specular", Color) = (0.2,0.2,0.2,1)
        _SpecGlossMap ("Specular Map", 2D) = "white" {}
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _SmoothnessTextureChannel ("Smoothness Source", Float) = 0
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0,1)) = 1
        _EmissionMap ("Emission", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,0)
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clipping", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _Surface ("Surface", Float) = 0
        _Blend ("Blend", Float) = 0
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 1
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 0
        [HideInInspector] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1
        [HideInInspector] _DstBlendAlpha ("Destination Blend Alpha", Float) = 0
        [HideInInspector] _ZWrite ("Z Write", Float) = 1
        [HideInInspector] _BlendModePreserveSpecular ("Preserve Specular", Float) = 1
        [HideInInspector] _AlphaToMask ("Alpha To Coverage", Float) = 0
        [ToggleOff] _SpecularHighlights ("Specular Highlights", Float) = 1
        [ToggleOff] _EnvironmentReflections ("Environment Reflections", Float) = 1
        [ToggleUI] _ReceiveShadows ("Receive Shadows", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct CloneTransformData
        {
            float4 value;
        };

        StructuredBuffer<float4> _BrazeClonerBasePosition;
        StructuredBuffer<float4> _BrazeClonerBaseRotation;
        StructuredBuffer<float4> _BrazeClonerBaseScale;
        StructuredBuffer<float4> _BrazeClonerEffectorPosition;
        StructuredBuffer<float4> _BrazeClonerEffectorRotation;
        StructuredBuffer<float4> _BrazeClonerEffectorScale;
        StructuredBuffer<float> _BrazeClonerAppearanceSpeed;
        StructuredBuffer<uint> _BrazeClonerVisibleIndices;
        uint _BrazeClonerBucketOffset;
        float4x4 _BrazeClonerPartMatrix;
        float4x4 _BrazeClonerPartNormalMatrix;

        // Nested-cloner composition. Zero child count keeps the regular single-stage path; otherwise the
        // visible index decodes as parent*childCount+child and geometry chains through the child cloner's
        // clone transform, the source-root inverse, then the parent clone transform.
        uint _BrazeClonerNestedChildCount;
        float4 _BrazeClonerNestedRootInvRotation;
        float4 _BrazeClonerNestedRootPosition;
        float4 _BrazeClonerNestedRootInvScale;
        StructuredBuffer<float4> _BrazeClonerChildBasePosition;
        StructuredBuffer<float4> _BrazeClonerChildBaseRotation;
        StructuredBuffer<float4> _BrazeClonerChildBaseScale;
        StructuredBuffer<float4> _BrazeClonerChildEffectorPosition;
        StructuredBuffer<float4> _BrazeClonerChildEffectorRotation;
        StructuredBuffer<float4> _BrazeClonerChildEffectorScale;

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_SpecGlossMap); SAMPLER(sampler_SpecGlossMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
        TEXTURE2D(_BrazeClonerColorRamp); SAMPLER(sampler_BrazeClonerColorRamp);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _SpecColor;
            half4 _EmissionColor;
            half _Metallic;
            half _Smoothness;
            half _BumpScale;
            half _OcclusionStrength;
            half _AlphaClip;
            half _Cutoff;
            half _Surface;
            uint _BrazeClonerAppearanceEnabled;
            uint _BrazeClonerAppearanceColorMode;
            half4 _BrazeClonerUnaffectedColor;
            half4 _BrazeClonerAffectedColor;
            float4 _BrazeClonerAppearanceMask;
            float4 _BrazeClonerAppearanceMap;
            float4 _BrazeClonerAppearanceGlow;
        CBUFFER_END

        float3 qrot(float4 q, float3 v)
        {
            return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
        }

        float4 CloneRotation(float4 baseRotation, float4 effectRotation)
        {
            return normalize(float4(
                effectRotation.w * baseRotation.xyz + baseRotation.w * effectRotation.xyz + cross(effectRotation.xyz, baseRotation.xyz),
                effectRotation.w * baseRotation.w - dot(effectRotation.xyz, baseRotation.xyz)));
        }

        float3 SafeCloneScale(float3 scale)
        {
            float3 scaleSign = float3(scale.x < 0.0 ? -1.0 : 1.0,
                                      scale.y < 0.0 ? -1.0 : 1.0,
                                      scale.z < 0.0 ? -1.0 : 1.0);
            return scaleSign * max(abs(scale), 0.001);
        }

        // Applies one clone transform stage (scale, rotate, translate) to a position/normal/tangent triple.
        void ApplyCloneStage(float3 stagePosition, float4 stageRotation, float3 stageScale,
                             inout float3 p, inout float3 n, inout float3 t, inout float handedness)
        {
            float3 safeScale = SafeCloneScale(stageScale);
            p = stagePosition + qrot(stageRotation, p * stageScale);
            n = qrot(stageRotation, n / safeScale);
            t = qrot(stageRotation, t * safeScale);
            handedness *= stageScale.x * stageScale.y * stageScale.z < 0.0 ? -1.0 : 1.0;
        }

        void ResolveClone(uint instanceID, float3 positionOS, float3 normalOS, float4 tangentOS,
                          out float3 positionWS, out float3 normalWS, out float4 tangentWS,
                          out float2 appearance)
        {
            uint clone = _BrazeClonerVisibleIndices[_BrazeClonerBucketOffset + instanceID];
            uint parentClone = clone;
            uint appearanceClone = clone;
            float appearanceInfluence = 0.0;

            float3 p = mul(_BrazeClonerPartMatrix, float4(positionOS, 1.0)).xyz;
            float3 n = mul((float3x3)_BrazeClonerPartNormalMatrix, normalOS);
            float3 t = mul((float3x3)_BrazeClonerPartMatrix, tangentOS.xyz);
            float handedness = 1.0;

            if (_BrazeClonerNestedChildCount > 0u)
            {
                parentClone = clone / _BrazeClonerNestedChildCount;
                uint childClone = clone - parentClone * _BrazeClonerNestedChildCount;
                appearanceClone = childClone;

                float4 childEffectorPosition = _BrazeClonerChildEffectorPosition[childClone];
                float3 childPosition = _BrazeClonerChildBasePosition[childClone].xyz + childEffectorPosition.xyz;
                float4 childRotation = CloneRotation(_BrazeClonerChildBaseRotation[childClone],
                                                     _BrazeClonerChildEffectorRotation[childClone]);
                float3 childScale = _BrazeClonerChildBaseScale[childClone].xyz
                                  * _BrazeClonerChildEffectorScale[childClone].xyz;
                ApplyCloneStage(childPosition, childRotation, childScale, p, n, t, handedness);

                // Source-root inverse (rotate then scale): brings the child's world-space clone back into
                // the composition-local frame the parent treats as its clone content.
                float3 invScale = _BrazeClonerNestedRootInvScale.xyz;
                float3 safeInvScale = SafeCloneScale(invScale);
                p = qrot(_BrazeClonerNestedRootInvRotation, p - _BrazeClonerNestedRootPosition.xyz) * invScale;
                n = qrot(_BrazeClonerNestedRootInvRotation, n) / safeInvScale;
                t = qrot(_BrazeClonerNestedRootInvRotation, t) * safeInvScale;
                handedness *= invScale.x * invScale.y * invScale.z < 0.0 ? -1.0 : 1.0;

                appearanceInfluence = childEffectorPosition.w;
            }

            float4 effectorPosition = _BrazeClonerEffectorPosition[parentClone];
            float3 position = _BrazeClonerBasePosition[parentClone].xyz + effectorPosition.xyz;
            float4 rotation = CloneRotation(_BrazeClonerBaseRotation[parentClone],
                                            _BrazeClonerEffectorRotation[parentClone]);
            float3 scale = _BrazeClonerBaseScale[parentClone].xyz * _BrazeClonerEffectorScale[parentClone].xyz;
            ApplyCloneStage(position, rotation, scale, p, n, t, handedness);

            if (_BrazeClonerNestedChildCount == 0u) appearanceInfluence = effectorPosition.w;
            positionWS = p;
            normalWS = normalize(n);
            tangentWS = float4(normalize(t), tangentOS.w * handedness);
            float appearanceCoordinate = appearanceClone * _BrazeClonerAppearanceMask.w;
            if (_BrazeClonerAppearanceEnabled != 0u && _BrazeClonerAppearanceColorMode == 2u)
                appearanceCoordinate = _BrazeClonerAppearanceSpeed[appearanceClone];
            appearance = float2(appearanceInfluence, appearanceCoordinate);
        }

        void AlphaClip(half alpha)
        {
        #if defined(_ALPHATEST_ON)
            clip(alpha - _Cutoff);
        #else
            if (_AlphaClip > 0.5) clip(alpha - _Cutoff);
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
            Cull [_Cull] ZWrite [_ZWrite] ZTest LEqual
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Unity 6.0/URP 17.0 uses _FORWARD_PLUS; Unity 6.3/URP 17.3 renamed the
            // same clustered-light path to _CLUSTER_LIGHT_LOOP. Both variants are
            // required so clones receive punctual lights in fresh Unity 6 projects.
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                float2 appearance : TEXCOORD6;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                ResolveClone(input.instanceID, input.positionOS, input.normalOS, input.tangentOS,
                             output.positionWS, output.normalWS, output.tangentWS, output.appearance);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.shadowCoord = TransformWorldToShadowCoord(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 sourceAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half4 albedoSample = sourceAlbedo;
                half appearanceAmount = 0.0h;
                half4 appearanceTone = half4(1, 1, 1, 1);
                half4 affectedAppearanceTone = _BrazeClonerAffectedColor;

                if (_BrazeClonerAppearanceEnabled != 0u)
                {
                    float influence = saturate(abs(input.appearance.x));
                    float threshold = saturate(_BrazeClonerAppearanceMask.x);
                    float softness = saturate(_BrazeClonerAppearanceMask.y);
                    float low = max(0.0, threshold - softness * 0.5);
                    float high = min(1.0, threshold + softness * 0.5);
                    appearanceAmount = high - low > 1e-5
                        ? smoothstep(low, high, influence)
                        : (influence > threshold ? 1.0h : 0.0h);
                    if (_BrazeClonerAppearanceMask.z > 0.5) appearanceAmount = 1.0h - appearanceAmount;

                    float coordinate = input.appearance.y;
                    if (_BrazeClonerAppearanceColorMode == 2u)
                        coordinate = saturate((input.appearance.y - _BrazeClonerAppearanceMap.z)
                                              * _BrazeClonerAppearanceMap.w);
                    coordinate = saturate(coordinate * _BrazeClonerAppearanceMap.x
                                          + _BrazeClonerAppearanceMap.y);
                    affectedAppearanceTone = _BrazeClonerAppearanceColorMode == 0u
                        ? _BrazeClonerAffectedColor
                        : SAMPLE_TEXTURE2D(_BrazeClonerColorRamp,
                            sampler_BrazeClonerColorRamp, float2(coordinate, 0.5));
                    appearanceTone = lerp(_BrazeClonerUnaffectedColor, affectedAppearanceTone, appearanceAmount);
                    albedoSample = half4(appearanceTone.rgb, sourceAlbedo.a * appearanceTone.a);
                }

                AlphaClip(albedoSample.a);
                half3 normalWS = normalize(input.normalWS);
            #if defined(_NORMALMAP)
                half3 tangentWS = normalize(input.tangentWS.xyz);
                half3 bitangentWS = input.tangentWS.w * GetOddNegativeScale() * cross(normalWS, tangentWS);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                normalWS = TransformTangentToWorld(normalTS, half3x3(tangentWS, bitangentWS, normalWS));
            #endif

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(input.positionWS, inputData.normalWS);
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                half4 metallicSpecGloss;
            #if defined(_METALLICSPECGLOSSMAP)
                #if defined(_SPECULAR_SETUP)
                    metallicSpecGloss = SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, input.uv);
                #else
                    metallicSpecGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
                #endif
                #if defined(_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A)
                    metallicSpecGloss.a = sourceAlbedo.a * _Smoothness;
                #else
                    metallicSpecGloss.a *= _Smoothness;
                #endif
            #else
                #if defined(_SPECULAR_SETUP)
                    metallicSpecGloss = half4(_SpecColor.rgb, _Smoothness);
                #else
                    metallicSpecGloss = half4(_Metallic.rrr, _Smoothness);
                #endif
                #if defined(_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A)
                    metallicSpecGloss.a = sourceAlbedo.a * _Smoothness;
                #endif
            #endif
                surface.albedo = AlphaModulate(albedoSample.rgb, albedoSample.a);
                surface.alpha = albedoSample.a;
            #if defined(_SPECULAR_SETUP)
                surface.metallic = 0;
                surface.specular = metallicSpecGloss.rgb;
            #else
                surface.metallic = metallicSpecGloss.r;
                surface.specular = half3(0, 0, 0);
            #endif
                surface.smoothness = metallicSpecGloss.a;
                surface.normalTS = half3(0, 0, 1);
                half occlusion = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).g;
                surface.occlusion = lerp(1.0h, occlusion, _OcclusionStrength);
                half3 sourceEmission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb
                    * _EmissionColor.rgb;
                half appearanceEnabled = _BrazeClonerAppearanceEnabled != 0u ? 1.0h : 0.0h;
                half3 regionGlow = _BrazeClonerUnaffectedColor.rgb * _BrazeClonerAppearanceGlow.x
                        * (1.0h - appearanceAmount)
                    + affectedAppearanceTone.rgb * _BrazeClonerAppearanceGlow.y * appearanceAmount;
                surface.emission = sourceEmission + regionGlow * appearanceEnabled;
                surface.clearCoatMask = 0;
                surface.clearCoatSmoothness = 0;
                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull [_Cull] ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex VertShadow
            #pragma fragment FragShadow
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; uint instanceID:SV_InstanceID; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };

            Varyings VertShadow(Attributes input)
            {
                Varyings output;
                float3 positionWS, normalWS; float4 tangentWS; float2 appearance;
                ResolveClone(input.instanceID, input.positionOS, input.normalOS, input.tangentOS,
                             positionWS, normalWS, tangentWS, appearance);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }
            half4 FragShadow(Varyings input) : SV_Target
            {
                AlphaClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            Cull [_Cull] ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex VertDepth
            #pragma fragment FragDepth
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; uint instanceID:SV_InstanceID; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings VertDepth(Attributes input)
            {
                Varyings output;
                float3 positionWS, normalWS; float4 tangentWS; float2 appearance;
                ResolveClone(input.instanceID, input.positionOS, input.normalOS, input.tangentOS,
                             positionWS, normalWS, tangentWS, appearance);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }
            half4 FragDepth(Varyings input) : SV_Target
            {
                AlphaClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            Cull [_Cull] ZWrite On

            HLSLPROGRAM
            #pragma vertex VertNormals
            #pragma fragment FragNormals
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; uint instanceID:SV_InstanceID; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 normalWS:TEXCOORD0; float2 uv:TEXCOORD1; };
            Varyings VertNormals(Attributes input)
            {
                Varyings output;
                float3 positionWS; float4 tangentWS; float2 appearance;
                ResolveClone(input.instanceID, input.positionOS, input.normalOS, input.tangentOS,
                             positionWS, output.normalWS, tangentWS, appearance);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }
            half4 FragNormals(Varyings input) : SV_Target
            {
                AlphaClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a);
                return half4(normalize(input.normalWS) * 0.5h + 0.5h, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
