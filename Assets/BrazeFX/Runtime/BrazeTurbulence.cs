using UnityEngine;

namespace BrazeFX
{
    /// <summary>Turbulence — a pure curl-flow conformance field adapted from the publisher-owned sparse-flip force library.
    /// Unlike random jitter or wind, it bends existing motion toward a smooth, evolving curl field without adding
    /// speed or any pull of its own: particles in motion follow the turbulent path and conform to it, nothing else.
    /// Great for swirling smoke, embers, dust drift.</summary>
    [AddComponentMenu("BrazeFX/Particles/Forces/Braze Turbulence", 35)]
    public sealed class BrazeTurbulence : BrazeParticleForce
    {
        [Header("TURBULENCE")]
        [Tooltip("Wave size — larger = broad billows, smaller = fine wisps.")]
        [Min(0.05f)] [BrazeAnimatable("TURBULENCE/Scale", 0.05f, 50f)] public float scale = 10f;
        [Tooltip("Adds a finer octave — fine wisps layered on top of the broad swirls.")]
        [Range(0f, 1f)] [BrazeAnimatable("TURBULENCE/Detail", 0f, 1f)] public float detail = 1f;
        [Tooltip("Per-particle wave-size variation, so the swirl sizes aren't uniform.")]
        [Range(0f, 1f)] [BrazeAnimatable("TURBULENCE/Scale Random", 0f, 1f)] public float scaleRandom = 0.3f;

        public enum PreviewStyle { Streamlines, Arrows, Both }

        [Header("PREVIEW (editor — select to see the flow)")]
        [Tooltip("Draw the turbulence flow field in the Scene view while this object is selected — so you can see " +
                 "the swirl shape and wave size. Arrows sample the nominal GPU curl field and fade/shorten with " +
                 "Strength; streamlines trace representative test particles through the same speed-preserving curl " +
                 "conformance. The preview is a static snapshot (t = 0); at play time the field slowly evolves so " +
                 "particles never pool. Editor-only, no runtime cost.")]
        public bool previewField = true;
        [Tooltip("Streamlines = representative test-particle paths (best for reading the swirl, includes Scale " +
                 "Random and Strength). Arrows = nominal local curl-field direction grid, scaled by Strength. Both = overlay.")]
        public PreviewStyle previewStyle = PreviewStyle.Both;
        [Tooltip("Preview resolution — more = denser/clearer but more lines. Editor-only.")]
        [Range(3, 12)] public int previewDensity = 7;
        [Tooltip("Half-size of the sampled preview region when the Region Shape is Infinite (world units).")]
        [Min(0.1f)] public float previewExtent = 2f;

        const int PreviewLineSteps = 40;
        const float PreviewDt = 1f / 60f;

        protected override float TypeId => 4f;
        protected override Vector4 Extra => new(detail, scale, scaleRandom, 0f);   // turbulence packs detail/scale/scaleRandom only
        protected override Color GizmoColor => new(0.4f, 0.8f, 0.9f, 0.7f);

        // ── Flow-field preview (editor, on selection) ───────────────────────────────────────────────
        // CPU replica of the GPU curl noise plus the same speed-preserving conformance used by the shader. The
        // preview is a static (t = 0) snapshot — at play time the runtime field evolves; this just shows its shape.
        void OnDrawGizmosSelected()
        {
            if (!previewField) return;
            Gizmos.matrix = Matrix4x4.identity;
            Vector3 center = transform.position, H = PreviewHalfExtent();
            bool forceLocalSamples = UsesForceLocalPreviewSpace();
            if (previewStyle != PreviewStyle.Streamlines) DrawArrowField(center, H, forceLocalSamples);
            if (previewStyle != PreviewStyle.Arrows) DrawStreamlines(center, H, forceLocalSamples);
        }

        Vector3 PreviewHalfExtent()
        {
            if (shape == ForceShape.Sphere) return Vector3.one * Mathf.Max(radius, 0.1f);
            if (shape == ForceShape.Box) return SanitizeHalfExtent(Abs(boxSize) * 0.5f);
            return Vector3.one * Mathf.Max(previewExtent, 0.1f);
        }

        bool UsesForceLocalPreviewSpace() => shape == ForceShape.Box;

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static Vector3 SanitizeHalfExtent(Vector3 h) =>
            new(Mathf.Max(Mathf.Abs(h.x), 0.1f), Mathf.Max(Mathf.Abs(h.y), 0.1f), Mathf.Max(Mathf.Abs(h.z), 0.1f));

        Vector3 ToWorldOffset(Vector3 sampleOffset, bool forceLocalSamples) =>
            forceLocalSamples ? transform.rotation * sampleOffset : sampleOffset;

        // Same region test as force_falloff() in BrazeParticles.compute, including the 256-sample LUT used on GPU.
        float ForceFalloffGpuLike(Vector3 pos)
        {
            if (shape == ForceShape.Infinite) return 1f;

            Vector3 d = pos - transform.position;
            float t;
            if (shape == ForceShape.Sphere)
            {
                t = d.magnitude / Mathf.Max(radius, 1e-4f);
            }
            else
            {
                Vector3 ld = Quaternion.Inverse(transform.rotation) * d;
                Vector3 h = boxSize * 0.5f;
                t = Mathf.Max(Mathf.Abs(ld.x) / Mathf.Max(h.x, 1e-4f),
                    Mathf.Max(Mathf.Abs(ld.y) / Mathf.Max(h.y, 1e-4f), Mathf.Abs(ld.z) / Mathf.Max(h.z, 1e-4f)));
            }

            if (t > 1f) return 0f;
            if (falloff == null) return 0f;
            float x = Mathf.Clamp01(t) * 255f;
            int i0 = Mathf.FloorToInt(x);
            int i1 = Mathf.Min(i0 + 1, 255);
            return Mathf.Lerp(falloff.Evaluate(i0 / 255f), falloff.Evaluate(i1 / 255f), x - i0);
        }

        // Arrows = the nominal raw field direction at a grid of points (Scale + Detail set the shape). Length/alpha
        // use the same strength*falloff*dt conformance response as the shader, accumulated over a short preview horizon.
        // Scale Random is per-particle, so representative streamlines show that variation instead.
        void DrawArrowField(Vector3 center, Vector3 H, bool forceLocalSamples)
        {
            int n = Mathf.Clamp(previewDensity, 3, 12);
            Vector3 step = new(2f * H.x / (n - 1), 2f * H.y / (n - 1), 2f * H.z / (n - 1));
            float len = PreviewVectorLength(step) * 0.52f;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++)
                    for (int z = 0; z < n; z++)
                    {
                        Vector3 off = new(-H.x + x * step.x, -H.y + y * step.y, -H.z + z * step.z);
                        Vector3 worldOff = ToWorldOffset(off, forceLocalSamples);
                        Vector3 p = center + worldOff;
                        float fo = ForceFalloffGpuLike(p);
                        if (fo <= 0.001f) continue;                       // outside region → no force, no arrow
                        Vector3 d = SafeNormalize(FlowVector(p, 1f), Vector3.up);
                        float influence = PreviewStrengthInfluence(fo, 12);
                        float visual = Mathf.Sqrt(influence);
                        DrawArrow(p, d, len * Mathf.Lerp(0.18f, 1f, visual),
                            Mathf.Clamp01(fo) * Mathf.Lerp(0.06f, 1f, visual));
                    }
        }

        // Streamlines trace the noise field itself. They start on the local curl-flow direction, then use the
        // shader's strength/falloff curl-flow conformance as they move through changing field samples.
        void DrawStreamlines(Vector3 center, Vector3 H, bool forceLocalSamples)
        {
            int m = Mathf.Clamp(previewDensity / 2, 3, 5);
            Vector3 step = new(2f * H.x / Mathf.Max(m - 1, 1), 2f * H.y / Mathf.Max(m - 1, 1), 2f * H.z / Mathf.Max(m - 1, 1));
            float dl = Mathf.Clamp(Mathf.Max(scale, 0.05f) * 0.05f, 0.03f, PreviewVectorLength(step) * 0.45f);
            float speed = dl / PreviewDt;
            float sr = Mathf.Clamp01(scaleRandom);
            for (int x = 0; x < m; x++)
                for (int y = 0; y < m; y++)
                    for (int z = 0; z < m; z++)
                    {
                        Vector3 off = new(-H.x + x * step.x, -H.y + y * step.y, -H.z + z * step.z);
                        Vector3 worldOff = ToWorldOffset(off, forceLocalSamples);
                        Vector3 seed = center + worldOff;
                        if (ForceFalloffGpuLike(seed) <= 0.001f) continue; // seed must be inside the region
                        // per-particle wave size (Scale Random), stable per seed — exp2 like the shader
                        uint pseed = PreviewSeed(x, y, z);
                        float freqMul = ScaleRandomMultiplier(pseed, sr);
                        Vector3 flow = SafeNormalize(FlowVector(seed, freqMul), Vector3.up);
                        Vector3 p = seed, prev = p, vel = flow * speed;
                        Vector3 lastDir = flow;
                        bool drew = false;
                        for (int s = 1; s <= PreviewLineSteps; s++)
                        {
                            if (OutsidePreviewEnvelope(p, center, H, forceLocalSamples, 1.25f)) break;

                            float fo = ForceFalloffGpuLike(p);
                            if (fo <= 0.001f) break;
                            ApplyTurbulenceVelocity(ref vel, p, freqMul, fo, PreviewDt);
                            if (vel.sqrMagnitude < 1e-9f) break;

                            p += vel * PreviewDt;
                            lastDir = SafeNormalize(vel, lastDir);
                            Gizmos.color = StreamlineColor(s, PreviewLineSteps, fo, PreviewStrengthInfluence(fo, s));
                            Gizmos.DrawLine(prev, p);
                            prev = p;
                            drew = true;
                        }
                        if (drew) DrawArrowHead(prev, lastDir, dl * 0.35f, 0.8f);
                    }
        }

        static float PreviewVectorLength(Vector3 step)
        {
            float x = step.x > 0.12f ? step.x : float.PositiveInfinity;
            float y = step.y > 0.12f ? step.y : float.PositiveInfinity;
            float z = step.z > 0.12f ? step.z : float.PositiveInfinity;
            float useful = Mathf.Min(x, Mathf.Min(y, z));
            if (float.IsInfinity(useful)) useful = Mathf.Max(step.x, Mathf.Max(step.y, step.z));
            return Mathf.Max(useful, 0.08f);
        }

        bool OutsidePreviewEnvelope(Vector3 p, Vector3 center, Vector3 H, bool forceLocalSamples, float pad)
        {
            Vector3 off = p - center;
            if (forceLocalSamples) off = Quaternion.Inverse(transform.rotation) * off;
            return Mathf.Abs(off.x) > H.x * pad || Mathf.Abs(off.y) > H.y * pad || Mathf.Abs(off.z) > H.z * pad;
        }

        float PreviewStrengthInfluence(float falloff, int steps)
        {
            float response = Mathf.Min(1f - Mathf.Exp(-Mathf.Abs(strength) * Mathf.Clamp01(falloff) * PreviewDt), 0.35f);
            return 1f - Mathf.Pow(1f - response, Mathf.Max(1, steps));
        }

        // Mirrors the shader's speed-preserving curl-flow conformance — bends direction toward flow, keeps speed.
        void ApplyTurbulenceVelocity(ref Vector3 vel, Vector3 pos, float freqMul, float fo, float dt)
        {
            Vector3 flow = SafeNormalize(FlowVector(pos, freqMul), Vector3.up);
            float signedStrength = strength * fo;
            if (signedStrength < 0f) flow = -flow;
            float spd = vel.magnitude;
            if (spd <= 1e-5f) return;
            Vector3 cur = vel / spd;
            float response = Mathf.Min(1f - Mathf.Exp(-Mathf.Abs(signedStrength) * dt), 0.35f);
            vel = SafeNormalize(Vector3.Lerp(cur, flow, response), cur) * spd;
        }

        static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) =>
            v.sqrMagnitude > 1e-10f ? v.normalized : fallback;

        static uint PreviewSeed(int x, int y, int z)
        {
            unchecked
            {
                uint u = (uint)(x + 1) * 73856093u ^ (uint)(y + 1) * 19349663u ^ (uint)(z + 1) * 83492791u;
                return HashU32(u) & 0x00ffffffu;
            }
        }

        static uint HashU32(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }

        static float Hash01(uint x)
        {
            unchecked
            {
                x = ((x >> 16) ^ x) * 73244475u;
                x = ((x >> 16) ^ x) * 73244475u;
                x = (x >> 16) ^ x;
                return (x & 65535u) / 65535f;
            }
        }

        static float ScaleRandomMultiplier(uint pseed, float sr)
        {
            unchecked
            {
                return Mathf.Pow(2f, (Hash01(pseed * 374761393u + 13u) * 2f - 1f) * sr);
            }
        }

        // Curl flow at a world point — combined base + Detail octaves, matching the shader (freqMul = Scale Random).
        Vector3 FlowVector(Vector3 pos, float freqMul)
        {
            float freq = (2f / Mathf.Max(scale, 0.05f)) * freqMul;
            float tDetail = Mathf.Clamp01(detail);
            Vector3 nn = Curl(pos, freq) + Curl(pos, freq * 3.5f) * tDetail;
            return nn / (1f + tDetail);
        }

        static Color StreamlineColor(int step, int steps, float falloff, float strengthInfluence)
        {
            float t = step / (float)steps;
            float f = Mathf.Clamp01(falloff);
            float v = Mathf.Sqrt(Mathf.Clamp01(strengthInfluence));
            return new Color(Mathf.Lerp(0.28f, 0.72f, t),
                Mathf.Lerp(0.72f, 1f, t),
                Mathf.Lerp(0.85f, 1f, t),
                Mathf.Lerp(0.10f, 0.85f, t) * Mathf.Lerp(0.25f, 1f, f) * Mathf.Lerp(0.08f, 1f, v));
        }

        static void DrawArrow(Vector3 p, Vector3 d, float len, float falloff)
        {
            Vector3 mid = p + d * (len * 0.62f);
            Vector3 tip = p + d * len;
            Gizmos.color = new Color(0.32f, 0.78f, 0.9f, 0.20f * falloff);
            Gizmos.DrawLine(p, mid);
            Gizmos.color = new Color(0.68f, 1f, 1f, 0.9f * falloff);
            Gizmos.DrawLine(mid, tip);
            DrawArrowHead(tip, d, len * 0.22f, falloff);
        }

        static void DrawArrowHead(Vector3 tip, Vector3 d, float size, float alpha)
        {
            Vector3 side = Vector3.Cross(d, Vector3.up);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.Cross(d, Vector3.right);
            side.Normalize();
            Vector3 back = -d * size;
            Vector3 wing = side * (size * 0.45f);
            Gizmos.color = new Color(0.68f, 1f, 1f, 0.75f * alpha);
            Gizmos.DrawLine(tip, tip + back + wing);
            Gizmos.DrawLine(tip, tip + back - wing);
        }

        static Vector3 Curl(Vector3 pos, float freq)
        {
            const float e = 0.05f;
            Vector3 dx = TurbField(pos + new Vector3(e, 0, 0), freq) - TurbField(pos - new Vector3(e, 0, 0), freq);
            Vector3 dy = TurbField(pos + new Vector3(0, e, 0), freq) - TurbField(pos - new Vector3(0, e, 0), freq);
            Vector3 dz = TurbField(pos + new Vector3(0, 0, e), freq) - TurbField(pos - new Vector3(0, 0, e), freq);
            float inv = 1f / (2f * e);
            return new Vector3(dy.z - dz.y, dz.x - dx.z, dx.y - dy.x) * inv / Mathf.Max(freq, 0.05f);
        }

        static Vector3 TurbField(Vector3 pos, float freq)
        {
            Vector3 p = pos * freq;
            Vector3 s1 = new(
                Mathf.Sin(p.y * 2.1f) + Mathf.Cos(p.z * 1.7f),
                Mathf.Sin(p.z * 2.3f) + Mathf.Cos(p.x * 1.9f),
                Mathf.Sin(p.x * 2.7f) + Mathf.Cos(p.y * 1.3f));
            Vector3 s2 = new(
                Mathf.Sin(p.z * 5.3f) * 0.5f,
                Mathf.Sin(p.x * 4.9f) * 0.5f,
                Mathf.Sin(p.y * 5.7f) * 0.5f);
            return s1 + s2;
        }

        // Curated defaults applied when the component is added (or Reset) — a turbulence that reads
        // well out of the box: a strong, broad, detailed swirl.
        void Reset()
        {
            shape = ForceShape.Infinite;
            strength = 15f;
            scale = 10f;
            detail = 1f;
            scaleRandom = 0.3f;
            previewField = true;
            previewStyle = PreviewStyle.Both;
            previewDensity = 7;
            previewExtent = 2f;
        }
    }
}
