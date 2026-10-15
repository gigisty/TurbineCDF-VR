// BrazeFX mesh-particle renderer — real opaque geometry drawn via Graphics.RenderMeshIndirect.
//
// Each instance (SV_InstanceID) IS a particle slot: the vertex shader fetches that particle, applies its
// per-particle transform (position + uniform size + random tumble / velocity align), and emits the mesh vertex.
// Dead slots and particles not assigned to this draw's mesh collapse to a degenerate point. Because this is a
// standard Opaque shader with ForwardLit + ShadowCaster + DepthOnly passes, URP renders it through the normal
// opaque + shadow pipeline — so the meshes depth-sort, RECEIVE shadows, and CAST shadows automatically.
Shader "BrazeFX/ParticleMesh"
{
    Properties
    {
        _AlphaCutoff ("Alpha Cutoff", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Packed 48-byte particle record + accessors (shared with the sim + sprite/line paths).
        #include "BrazeParticleStruct.hlsl"
        StructuredBuffer<Particle> _Particles;
        StructuredBuffer<uint>     _VisibleIndices;   // compacted alive slots; instance id indexes this (count = alive)
        uint     _DrawMesh;          // which mesh (= texture slice) this RenderMeshIndirect call draws
        uint     _MeshCount;         // total meshes (per-particle random pick is modulo this)
        float4x4 _Model;             // local→world for Local sim space (identity for World/request-driven)
        float    _VelocityIsLocal;   // 1 when velocity must be transformed by _Model before alignment
        float    _Facing;            // 1 = Velocity (align +Y to motion); otherwise random tumble
        // x base mesh scale, yz brightness min/max. This fills the same float4 constant slot that previously held
        // _MeshScale plus three padding floats, so random brightness does not grow the material constant buffer.
        float4   _MeshRenderParams;

        uint hashU(uint x) { x ^= x >> 16u; x *= 0x7feb352du; x ^= x >> 15u; x *= 0x846ca68bu; x ^= x >> 16u; return x; }
        float hashF(uint s, uint salt) { return float(hashU(s ^ salt) & 0x00ffffffu) / 16777215.0; }
        float3 randAxis(uint s)
        {
            float u = hashF(s, 0x1u) * 2.0 - 1.0;
            float t = hashF(s, 0x2u) * 6.2831853;
            float r = sqrt(max(0.0, 1.0 - u * u));
            return float3(r * cos(t), r * sin(t), u);
        }

        // Rows = the particle's rotated local x/y/z axes (offset maps to world via mul(local, basis)). `rot` is the
        // simulation-integrated angle, so speed changes cannot retroactively jump the phase. Velocity alignment
        // blends into a stable per-particle fallback near rest instead of switching bases at an epsilon threshold.
        float3x3 particleBasis(uint seed, float3 vel, float rot)
        {
            float angle = rot + hashF(seed, 0xA53u) * 6.2831853;
            float s, c; sincos(angle, s, c);
            float spd = length(vel);
            float3x3 basis = float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1);
            if (_Facing > 0.5 && _Facing < 1.5)
            {
                float3 fallbackUp = randAxis(seed ^ 0x4f1bbcdcu);
                float3 motionUp = spd > 1e-6 ? vel / spd : fallbackUp;
                float align = smoothstep(0.05, 0.25, spd);
                float3 up = normalize(lerp(fallbackUp, motionUp, align));
                float3 rf = abs(up.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 right = normalize(cross(rf, up));
                float3 fwd = cross(up, right);
                basis = float3x3(right * c + fwd * s, up, fwd * c - right * s);
            }
            else
            {
                float3 a = randAxis(seed);
                float3 x = float3(1,0,0) * c + cross(a, float3(1,0,0)) * s + a * (a.x * (1.0 - c));
                float3 y = float3(0,1,0) * c + cross(a, float3(0,1,0)) * s + a * (a.y * (1.0 - c));
                float3 z = float3(0,0,1) * c + cross(a, float3(0,0,1)) * s + a * (a.z * (1.0 - c));
                basis = float3x3(x, y, z);
            }
            return basis;
        }

        // Resolve the per-instance particle into a world-space vertex. Returns false (caller emits a degenerate
        // vertex) when the slot is dead or the particle isn't assigned to this draw's mesh.
        bool resolveVertex(uint iid, float3 posOS, float3 nrmOS, out float3 positionWS, out float3 normalWS, out float4 vcolor)
        {
            positionWS = float3(0, 0, 0); normalWS = float3(0, 1, 0); vcolor = float4(1, 1, 1, 1);
            Particle p = _Particles[_VisibleIndices[iid]];              // iid in [0, alive); resolve the actual slot
            if (!ParticleAlive(p)) return false;                        // defensive (stale visible entry)
            uint seed = ParticleRandomSeed(p);
            uint m = (_MeshCount > 1u) ? (hashU(seed ^ 0x6d2b79f5u) % _MeshCount) : 0u;
            if (m != _DrawMesh) return false;                           // belongs to another mesh's draw
            float3 center = mul(_Model, float4(p.position, 1.0)).xyz;
            // Current size = Size-over-Life applied; rotation is integrated by Simulate and freezes when asleep.
            float3 velocity = _VelocityIsLocal > 0.5 ? mul((float3x3)_Model, p.velocity) : p.velocity;
            float3x3 basis = particleBasis(seed, velocity, ParticleRotation(p));
            positionWS = center + mul(posOS * (ParticleSize(p) * _MeshRenderParams.x), basis);
            normalWS = normalize(mul(nrmOS, basis));
            vcolor = ParticleColor(p);
            float brightness = max(_MeshRenderParams.z, 0.0);
            float brightnessMin = max(_MeshRenderParams.y, 0.0);
            if (brightnessMin < brightness)
                brightness = lerp(brightnessMin, brightness, float(seed) * (1.0 / 16777215.0));
            vcolor.rgb *= brightness;
            return true;
        }
        ENDHLSL

        // ── Forward lit (+ receives main-light shadows) ───────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On  ZTest LEqual  Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_ARRAY(_MeshTex);  SAMPLER(sampler_MeshTex);
            float4 _Tint;
            float  _MeshLight;          // 0 = ambient only, 1 = full Lambert from the main light
            float  _AlphaCutoff;
            float  _MeshReceiveShadows; // 1 = sample the shadow map (can self-shadow), 0 = full light (no self-shadow)
            float  _MeshShadowStrength; // 0..1: caps how dark receiving can drive a particle (volumetric self-shadow)

            struct Attr { float3 posOS : POSITION; float3 nrmOS : NORMAL; float2 uv : TEXCOORD0; uint iid : SV_InstanceID; };
            struct V2F
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 color       : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                nointerpolation uint slice : TEXCOORD4;
            };

            V2F vert(Attr a)
            {
                V2F o = (V2F)0;
                float3 pWS, nWS; float4 vc;
                if (!resolveVertex(a.iid, a.posOS, a.nrmOS, pWS, nWS, vc)) { o.positionCS = float4(2, 2, 2, 1); return o; }
                o.positionCS = TransformWorldToHClip(pWS);
                o.normalWS = nWS;
                o.uv = a.uv;
                o.color = vc * _Tint;
                o.shadowCoord = TransformWorldToShadowCoord(pWS);
                o.slice = _DrawMesh;
                return o;
            }

            half4 frag(V2F i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D_ARRAY(_MeshTex, sampler_MeshTex, i.uv, i.slice);
                half4 col = tex * i.color;
                if (col.a < _AlphaCutoff) discard;
                float3 N = normalize(i.normalWS);
                // Shadow receiving is opt-in. The particles CAST into the main-light shadow map AND would sample it,
                // so a dense/moving cloud self-shadows — a particle darkens as it passes behind its neighbours (or
                // its own bias acne) and recovers as it moves out. Off → full directional light, no self-shadow
                // flicker, while the ShadowCaster pass still drops shadows onto the scene.
                Light mainLight;   // NB: ternary on a struct fails to compile on Metal — branch instead
                if (_MeshReceiveShadows > 0.5) mainLight = GetMainLight(i.shadowCoord);
                else                           mainLight = GetMainLight();
                float ndl = saturate(dot(N, mainLight.direction));
                float3 ambient = SampleSH(N);
                // Cap how dark received shadows go. A dense particle cloud samples a shadow map full of OTHER
                // particles, so without this the inter-particle shadowing accumulates with density and the cloud
                // crushes to black over time. Shadow Strength keeps the self-shadow shaping but floors the darkness.
                float shadow = lerp(1.0, mainLight.shadowAttenuation, saturate(_MeshShadowStrength));
                float3 lit = ambient + mainLight.color * (ndl * shadow * _MeshLight);
                return half4(col.rgb * lit, 1.0);
            }
            ENDHLSL
        }

        // ── Shadow caster (so the meshes cast into the light's shadow map) ────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On  ZTest LEqual  Cull Back  ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attr { float3 posOS : POSITION; float3 nrmOS : NORMAL; uint iid : SV_InstanceID; };
            struct V2F  { float4 positionCS : SV_POSITION; };

            V2F vertShadow(Attr a)
            {
                V2F o = (V2F)0;
                float3 pWS, nWS; float4 vc;
                if (!resolveVertex(a.iid, a.posOS, a.nrmOS, pWS, nWS, vc)) { o.positionCS = float4(2, 2, 2, 1); return o; }
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - pWS);
            #else
                float3 lightDir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(pWS, nWS, lightDir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = cs;
                return o;
            }

            half4 fragShadow(V2F i) : SV_Target { return 0; }
            ENDHLSL
        }

        // ── Depth only (depth prepass / soft-particle depth) ──────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On  ColorMask R  Cull Back

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attr { float3 posOS : POSITION; float3 nrmOS : NORMAL; uint iid : SV_InstanceID; };
            struct V2F  { float4 positionCS : SV_POSITION; };

            V2F vertDepth(Attr a)
            {
                V2F o = (V2F)0;
                float3 pWS, nWS; float4 vc;
                if (!resolveVertex(a.iid, a.posOS, a.nrmOS, pWS, nWS, vc)) { o.positionCS = float4(2, 2, 2, 1); return o; }
                o.positionCS = TransformWorldToHClip(pWS);
                return o;
            }

            half4 fragDepth(V2F i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
