// BrazeFX slice renderer (Hidden/BrazeFX/Slices).
// Single-pass volumetric raymarch over a screen-fitted quad. It can draw directly,
// into a spatially-downsampled target, or into a working-resolution temporally
// interleaved color+motion target. MapState() maps the
// solver's packed fields (x=density ~0..0.3, y=temperature, z=fuel,
// w=burn ~0..15) into the renderer's smoke/fire terms (smoke 0..3,
// fire-temperature 0..~1.5) via two gain uniforms.
Shader "Hidden/BrazeFX/Slices"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BrazeSlices"
            ZTest Always ZWrite Off Cull Off
            // Standard premultiplied over: rgb = accumulated in-scatter (premultiplied), a = OPACITY (1 - T).
            // dst × (1-srcA) = dst × transmittance — identical on-screen result to the old (accum, T) +
            // One SrcAlpha form, but this convention also composites correctly into an offscreen buffer
            // cleared to (0,0,0,0), which is what the downsampled slice path renders into.
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragColor
            #pragma target 4.5
            // Feature keywords instead of uniform-float branches in the march loop: a uniform branch is
            // coherent but the compiler must allocate registers for BOTH sides, dropping wave occupancy for
            // every pixel even when the feature is off — and this ~19-tap latency-bound loop is occupancy-
            // limited. multi_compile (not shader_feature): the keywords are toggled at runtime on a material
            // instance, so build-time stripping of "unused" variants would break them.
            #pragma multi_compile_local_fragment _ _BRAZE_DEBUG_VIEW
            #pragma multi_compile_local_fragment _ _BRAZE_USE_FINE
            #pragma multi_compile_local_fragment _ _BRAZE_USE_WARP
            #pragma multi_compile_local_fragment _ _BRAZE_DEFINITION
            #pragma multi_compile_local_fragment _ _BRAZE_SMOKE
            #pragma multi_compile_local_fragment _ _BRAZE_FIRE
            #pragma multi_compile_local_fragment _ _BRAZE_SELF_SHADOW
            #pragma multi_compile_local_fragment _ _BRAZE_FILL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Shared/BrazeSlicesCore.hlsl"

            float4 FragColor(Varyings input) : SV_Target
            {
                float4 color;
                float2 ignoredMotion;
                BrazeFrag(input, color, ignoredMotion);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "BrazeSlicesTemporal"
            ZTest Always ZWrite Off Cull Off
            Blend 0 One OneMinusSrcAlpha
            Blend 1 Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTemporal
            #pragma target 4.5
            #pragma multi_compile_local_fragment _ _BRAZE_DEBUG_VIEW
            #pragma multi_compile_local_fragment _ _BRAZE_USE_FINE
            #pragma multi_compile_local_fragment _ _BRAZE_USE_WARP
            #pragma multi_compile_local_fragment _ _BRAZE_DEFINITION
            #pragma multi_compile_local_fragment _ _BRAZE_SMOKE
            #pragma multi_compile_local_fragment _ _BRAZE_FIRE
            #pragma multi_compile_local_fragment _ _BRAZE_SELF_SHADOW
            #pragma multi_compile_local_fragment _ _BRAZE_FILL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Shared/BrazeSlicesCore.hlsl"

            void FragTemporal(Varyings input, out float4 color : SV_Target0, out float2 motion : SV_Target1)
            {
                BrazeFrag(input, color, motion);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
