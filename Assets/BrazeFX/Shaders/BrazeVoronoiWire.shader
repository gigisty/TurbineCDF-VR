// BrazeFX Voronoi Fracture — editor/play wire overlay.
// Draws MeshTopology.Lines from the baked fragment mesh, but resolves each vertex from the same GPU rigid-fragment
// buffers as BrazeFX/Chunk so the wire follows fragments after release.

Shader "BrazeFX/VoronoiWire"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            Name "VoronoiWireCpu"
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4x4 _L2W;

            struct Attr
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
            };

            struct Vary
            {
                float4 hcs   : SV_POSITION;
                half4  color : COLOR;
            };

            Vary vert(Attr IN)
            {
                Vary o;
                float3 wp = mul(_L2W, float4(IN.positionOS.xyz, 1.0)).xyz;
                o.hcs = TransformWorldToHClip(wp);
                o.color = IN.color;
                return o;
            }

            half4 frag(Vary IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "VoronoiWireGpu"
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _ClumpPosState;  // xyz = free world position, w = state (0 anchored, 1 free)
            StructuredBuffer<float4> _ClumpRot;
            StructuredBuffer<float4> _ClumpLife;      // x = shrink scale
            StructuredBuffer<float3> _DeformedPos;    // rest/deformed local vertex position
            StructuredBuffer<float3> _FreeOffset;     // free vertex offset captured at release
            StructuredBuffer<float4> _ClumpCentroid;
            StructuredBuffer<float4> _EffectorPosition;
            StructuredBuffer<float4> _EffectorRotation;
            StructuredBuffer<float4> _EffectorScale;
            float4x4 _L2W;
            int _EffectorCount;

            float3 qrot(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

            struct Attr
            {
                float4 positionOS : POSITION;  // fallback/rest only; live position is resolved from buffers
                float4 color      : COLOR;
                float2 ids        : TEXCOORD1; // x = clump id, y = vertex id
                float3 push       : TEXCOORD2; // optional preview explode offset, source-local
            };

            struct Vary
            {
                float4 hcs   : SV_POSITION;
                half4  color : COLOR;
            };

            Vary vert(Attr IN)
            {
                Vary o;
                uint clumpId = (uint)(IN.ids.x + 0.5);
                uint vertId = (uint)(IN.ids.y + 0.5);
                float4 ps = _ClumpPosState[clumpId];

                float3 wp;
                if (ps.w < 0.5)
                {
                    wp = mul(_L2W, float4(_DeformedPos[vertId] + IN.push, 1.0)).xyz;
                }
                else
                {
                    float4 q = _ClumpRot[clumpId];
                    float scale = _ClumpLife[clumpId].x;
                    float3 worldPush = mul((float3x3)_L2W, IN.push);
                    wp = ps.xyz + qrot(q, _FreeOffset[vertId] * scale) + worldPush;
                }
                if (_EffectorCount > 0)
                {
                    float3 pivot = ps.w < 0.5 ? _ClumpCentroid[clumpId].xyz : ps.xyz;
                    float3 effScale = max(_EffectorScale[clumpId].xyz, float3(0.001, 0.001, 0.001));
                    wp = pivot + qrot(_EffectorRotation[clumpId], (wp - pivot) * effScale)
                               + _EffectorPosition[clumpId].xyz;
                }

                o.hcs = TransformWorldToHClip(wp);
                o.color = IN.color;
                return o;
            }

            half4 frag(Vary IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
