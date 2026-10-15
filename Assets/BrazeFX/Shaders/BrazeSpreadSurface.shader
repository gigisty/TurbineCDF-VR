// BrazeFX Mesh Effects — SPREAD surface overlay. Draws the (deformed) target mesh a second time, tinted by
// per-vertex COVERAGE written into vertex colours by BrazeSpread (rgb = look colour, a = coverage alpha).
// Unlit + transparent so the covered region reads as a material layer (frost / paint / corruption / …) sitting
// on the body; uncovered verts have alpha 0 and vanish. Depth-tested + polygon-offset so it hugs the visible
// surface and is occluded correctly by the rest of the body.
Shader "BrazeFX/SpreadSurface"
{
    Properties
    {
        _NoiseTex ("Noise", 2D) = "white" {}
        _NoiseTiling ("Noise Tiling", Float) = 4
        _NoiseBlend ("Triplanar Blend", Float) = 4
        _NoiseStrength ("Noise Strength", Range(0,1)) = 0
        _NoiseLow ("Levels Low", Range(0,1)) = 0
        _NoiseHigh ("Levels High", Range(0,1)) = 1
        _NoiseGamma ("Levels Gamma", Float) = 1
        _NoiseContrast ("Contrast", Float) = 1
    }
    SubShader
    {
        // Opaque-range queue (after the body's Geometry pass, before the volume's BeforeRenderingTransparents
        // composite) so the depth-aware fire/smoke volume draws OVER this overlay — i.e. smoke in front of the
        // body occludes the camo fill/edge/noise too, instead of the overlay painting on top of the smoke.
        // Still alpha-blends onto the body (Blend + ZWrite Off below); only the draw ORDER moves earlier.
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+450" "RenderType" = "Opaque" }

        Pass
        {
            Name "SpreadOverlay"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset -1, -1     // pull toward camera so it wins over the identical body geometry (no z-fight)
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_NoiseTex); SAMPLER(sampler_NoiseTex);
            float _NoiseTiling;
            float _NoiseBlend;
            float _NoiseStrength;
            float _NoiseLow;
            float _NoiseHigh;
            float _NoiseGamma;
            float _NoiseContrast;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR;
                                float3 restOS : TEXCOORD1; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float4 color : COLOR;
                                float3 restPos : TEXCOORD0; float3 nrmOS : TEXCOORD1; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                OUT.restPos = IN.restOS;         // REST-pose coord → cubic projection stays glued as the body deforms
                OUT.nrmOS = IN.normalOS;
                return OUT;
            }

            // Cubic (object-space triplanar) sample — three planar lookups blended by the surface normal,
            // so the grain never stretches regardless of the mesh UVs.
            float SampleTriplanar(float3 p, float3 n)
            {
                float3 bw = pow(abs(normalize(n)), _NoiseBlend);
                bw /= max(bw.x + bw.y + bw.z, 1e-4);
                float nx = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, p.yz).r;
                float ny = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, p.zx).r;
                float nz = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, p.xy).r;
                return nx * bw.x + ny * bw.y + nz * bw.z;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float n = SampleTriplanar(IN.restPos * _NoiseTiling, IN.nrmOS);
                // Levels: input black/white remap → gamma (mid) → contrast around the midpoint.
                n = saturate((n - _NoiseLow) / max(1e-4, _NoiseHigh - _NoiseLow));
                n = pow(n, _NoiseGamma);
                n = saturate((n - 0.5) * _NoiseContrast + 0.5);
                // Mix into the fill VALUE (±strength around 1) → breaks up flat patches, adds depth.
                float f = 1.0 + (n - 0.5) * 2.0 * _NoiseStrength;
                half3 rgb = IN.color.rgb * max(0.0, f);
                return half4(rgb, IN.color.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
