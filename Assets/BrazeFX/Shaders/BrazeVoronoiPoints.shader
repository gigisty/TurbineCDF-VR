// BrazeFX Voronoi Fracture — PARTICLE DEBUG POINTS. Draws one camera-facing point at every fragment vertex,
// resolved from the SAME rigid-fragment buffers the Chunk shader uses (anchored → rest position, free → the
// fragment's rigid transform). Coloured per fragment so you can see which vertex-particles belong to which chunk.
// Drawn via Graphics.DrawProcedural (no mesh, no readback) — 6 verts (a quad) per particle.
Shader "BrazeFX/VoronoiPoints"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 0.02
        _PointColor ("Tint", Color) = (1,1,1,1)
        _ColorByChunk ("Colour By Chunk", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "VoronoiPoints"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _ClumpPosState;  // xyz pos, w state (0 anchored, 1 free)
            StructuredBuffer<float4> _ClumpRot;       // quaternion
            StructuredBuffer<float4> _ClumpLife;      // x = shrink scale
            StructuredBuffer<float3> _DeformedPos;    // per orig vertex: rest position
            StructuredBuffer<float3> _FreeOffset;     // per vertex: offset from centroid (set at release)
            StructuredBuffer<uint2>  _PointIds;       // per vertex: (clumpId, origIdx)
            StructuredBuffer<float4> _ClumpCentroid;
            StructuredBuffer<float4> _EffectorPosition;
            StructuredBuffer<float4> _EffectorRotation;
            StructuredBuffer<float4> _EffectorScale;
            float4x4 _L2W;
            int _EffectorCount;
            float _PointSize, _ColorByChunk;
            float4 _PointColor;

            float3 qrot(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

            struct Vary { float4 hcs : SV_POSITION; float2 uv : TEXCOORD0; float3 col : TEXCOORD1; };

            static const float2 CORNER[6] =
            {
                float2(-1,-1), float2(1,-1), float2(1,1),
                float2(-1,-1), float2(1,1), float2(-1,1)
            };

            Vary vert(uint id : SV_VertexID)
            {
                Vary o;
                uint pt = id / 6u;
                uint2 ids = _PointIds[pt];
                uint clump = ids.x, oi = ids.y;

                float4 ps = _ClumpPosState[clump];
                float3 wp;
                if (ps.w < 0.5) wp = mul(_L2W, float4(_DeformedPos[oi], 1.0)).xyz;
                else            wp = ps.xyz + qrot(_ClumpRot[clump], _FreeOffset[pt] * _ClumpLife[clump].x);
                if (_EffectorCount > 0)
                {
                    float3 pivot = ps.w < 0.5 ? _ClumpCentroid[clump].xyz : ps.xyz;
                    wp = pivot + qrot(_EffectorRotation[clump], (wp - pivot) *
                               max(_EffectorScale[clump].xyz, float3(0.001, 0.001, 0.001)))
                               + _EffectorPosition[clump].xyz;
                }

                float2 c = CORNER[id % 6u];
                float3 right = float3(UNITY_MATRIX_V._m00, UNITY_MATRIX_V._m01, UNITY_MATRIX_V._m02);
                float3 up    = float3(UNITY_MATRIX_V._m10, UNITY_MATRIX_V._m11, UNITY_MATRIX_V._m12);
                float3 billboard = wp + (right * c.x + up * c.y) * (_PointSize * 0.5);

                o.hcs = TransformWorldToHClip(billboard);
                o.uv = c;
                float h = frac(clump * 0.61803398);
                float3 hue = saturate(abs(frac(h + float3(0, 0.6667, 0.3333)) * 6.0 - 3.0) - 1.0);
                o.col = lerp(_PointColor.rgb, hue, _ColorByChunk);
                return o;
            }

            half4 frag(Vary IN) : SV_Target
            {
                float r = dot(IN.uv, IN.uv);
                if (r > 1.0) discard;                       // round point
                float a = saturate((1.0 - r) * 2.0) * _PointColor.a;
                return half4(IN.col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
