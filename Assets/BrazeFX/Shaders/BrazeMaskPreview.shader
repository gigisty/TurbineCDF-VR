// BrazeFX surface-mask preview — unlit, transparent. Drawn over the object's own mesh as a VIEW of the spatial mask
// field. Per pixel it computes the distance to the nearest seed (uniform array) in the field's EVAL space (world,
// scale-independent mask space, or skinned REST pose transformed into mask space via _EvalMode), runs the same filter
// pipeline as the GPU gates, and renders the resulting 0..1 membership as grayscale, tinted color, or a texture mapped
// in Braze mask space. In Local mode the field rides with the object and follows skinned deformation; in World mode
// it's pure world proximity.
Shader "BrazeFX/MaskPreview"
{
    Properties
    {
        _Front   ("Front (m)", Float) = 1.5
        _Soft    ("Softness (m)", Float) = 0.4
        _Invert  ("Invert", Float) = 0
        _DebugSolid ("Debug Solid", Float) = 0
        _PreviewColor ("Preview Color", Color) = (1,1,1,1)
        _MaskTex ("Mask Texture", 2D) = "white" {}
        _TextureStrength ("Texture Strength", Range(0,1)) = 1
    }
    SubShader
    {
        // Drawn in the OPAQUE range (Geometry+1), right after the surface, so the fluid/fire (BrazeRenderFeature, at
        // BeforeRenderingTransparents) and any transparent FX composite OVER it instead of being hidden behind it. It
        // still ZTests against solid geometry, so anything physically in front occludes the mask correctly.
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry+1" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BrazeMaskPreview"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1   // nudge toward the camera so it doesn't z-fight the source surface

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "BrazeMaskField.hlsl"   // _Seeds/_Front/_Soft/_Invert + filters + MaskFieldWeight() — shared with the GPU gate

            int _EvalMode;              // 0 = world (positionWS), 1 = world→mask, 2 = rest pose (UV3)→mask
            float4x4 _WorldToField;
            float4x4 _LocalToField;
            float _Inflate;             // lift the shell this many world metres along the normal → no z-fight with the source mesh
            float _DebugSolid, _PreviewOpacity;
            int _PreviewStyle;          // 0 = grayscale, 1 = color, 2 = texture
            float4 _PreviewColor;
            float4 _MaskTex_ST;
            float _TextureStrength;
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;      // for the anti-z-fight shell offset
                float3 restOS     : TEXCOORD3;   // mesh-local REST position (skinned Local preview carries it here)
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 epos : TEXCOORD0;         // the field's eval-space position (world / mask-space / rest→mask)
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                // Push the rasterized shell a few mm OUT along the world normal so it sits just above the source surface
                // and never intersects / z-fights it. The FIELD is still sampled at the true (un-inflated) surface point.
                float3 nWS = TransformObjectToWorldNormal(i.normalOS);
                o.positionCS = TransformWorldToHClip(p.positionWS + nWS * _Inflate);
                o.epos = (_EvalMode == 2) ? mul(_LocalToField, float4(i.restOS, 1.0)).xyz
                       : (_EvalMode == 1) ? mul(_WorldToField, float4(p.positionWS, 1.0)).xyz
                                          : p.positionWS;
                return o;
            }

            float2 VisualUv(float3 epos)
            {
                float2 uv = (_ProjectionPlane == 0) ? epos.xy
                          : (_ProjectionPlane == 2) ? epos.yz
                                                    : epos.xz;
                return uv * _MaskTex_ST.xy + _MaskTex_ST.zw;
            }

            half4 frag(Varyings i) : SV_Target
            {
                if (_DebugSolid > 0.5)
                    return half4(1.0, 0.0, 1.0, 0.82 * _PreviewOpacity);

                // The whole field (distance → early-outs → organic → coverage → rings → erosion → contrast → invert) is
                // shared with the GPU gate in BrazeMaskField.hlsl, so the preview and the spawn set are guaranteed equal.
                float weight = MaskFieldWeight(i.epos);

                if (_PreviewStyle == 0)
                {
                    // Grayscale preview = the membership itself: black where nothing happens, white where it does.
                    float a = (0.3 + 0.65 * weight) * _PreviewOpacity;
                    return half4(weight, weight, weight, a);
                }

                float3 color = _PreviewColor.rgb;
                float alpha = weight * _PreviewColor.a * _PreviewOpacity;
                if (_PreviewStyle == 2)
                {
                    float4 tex = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, VisualUv(i.epos));
                    color = lerp(color, color * tex.rgb, saturate(_TextureStrength));
                    alpha *= tex.a;
                }
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
