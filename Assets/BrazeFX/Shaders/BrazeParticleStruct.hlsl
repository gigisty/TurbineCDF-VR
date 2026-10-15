#ifndef BRAZE_PARTICLE_STRUCT_INCLUDED
#define BRAZE_PARTICLE_STRUCT_INCLUDED

// Single source of truth for the GPU particle record, shared by the sim (BrazeParticles.compute), the
// sprite/mesh/line render shaders, and the trail build (BrazeLine.compute). It used to be a 4×float4 (64 B)
// struct duplicated in five files; this packs it to 48 B (3×16, a clean structured-buffer stride) — a 25%
// smaller particle buffer and ~25% less per-particle sim bandwidth, with no visible change.
//
// What stays FP32: position, velocity, age (accumulated every frame → half would drift), and the RNG seed
// (already a 24-bit value, needs a full-width slot). What becomes a 16-bit half: lifetime and base size
// (set once at spawn), current size and RGBA colour (HDR half is
// indistinguishable for a particle tint). ALWAYS go through the accessors below so the packing lives here.
struct Particle
{
    float3 position;   // world position
    float  age;        // seconds alive (FP32 — accumulated per frame)
    float3 velocity;   // world velocity
    uint   seed;       // low 24 bits = stable RNG/identity; high 8 bits = collision sleep contact counter
    uint   lifeSize;   // half2: lo = lifetime (s), hi = base (start) size
    uint   curRot;     // lo16 = current size (half), hi16 = wrapped rotation (UNORM 0..2π)
    uint   colorRG;    // half2: lo = colour.r, hi = colour.g
    uint   colorBA;    // half2: lo = colour.b, hi = colour.a
};

// Pack two floats into a 16-bit-half pair; unpack the low / high half of a packed slot.
uint  BrazePackHalf2(float lo, float hi) { return (f32tof16(hi) << 16) | (f32tof16(lo) & 0xFFFFu); }
float BrazeUnpackLo(uint v) { return f16tof32(v & 0xFFFFu); }
float BrazeUnpackHi(uint v) { return f16tof32(v >> 16); }

float  ParticleLifetime(Particle p) { return BrazeUnpackLo(p.lifeSize); }
float  ParticleBaseSize(Particle p) { return BrazeUnpackHi(p.lifeSize); }
float  ParticleSize(Particle p)     { return BrazeUnpackLo(p.curRot); }   // current, over-life size
float  ParticleRotation(Particle p) { return (float)(p.curRot >> 16) * (6.28318530718 / 65535.0); }
float4 ParticleColor(Particle p)
{
    return float4(BrazeUnpackLo(p.colorRG), BrazeUnpackHi(p.colorRG),
                  BrazeUnpackLo(p.colorBA), BrazeUnpackHi(p.colorBA));
}
bool ParticleAlive(Particle p) { return ParticleLifetime(p) > 0.0; }

// Spawn seeds are intentionally 24-bit, leaving the high byte available for persistent simulation state without
// growing the 48-byte particle record (or adding a ninth UAV to Simulate). Always use ParticleRandomSeed for
// rendering, random traits and trail identity so changing sleep state cannot change a particle's appearance/history.
uint ParticleRandomSeed(Particle p) { return p.seed & 0x00FFFFFFu; }
uint ParticleSleepFrames(Particle p) { return p.seed >> 24; }
void SetParticleSleepFrames(inout Particle p, uint frames)
{
    p.seed = ParticleRandomSeed(p) | (min(frames, 255u) << 24);
}

void SetParticleLifeSize(inout Particle p, float life, float baseSize) { p.lifeSize = BrazePackHalf2(life, baseSize); }
void SetParticleCurRot(inout Particle p, float cur, float rot)
{
    // Rotation is integrated every frame in mesh mode. Half-float precision near 2π is too coarse for slow spin,
    // so store a wrapped UNORM angle instead (~0.000096 rad resolution) while keeping current size as a half.
    uint encodedRot = (uint)round(frac(rot / 6.28318530718) * 65535.0);
    p.curRot = (encodedRot << 16) | (f32tof16(cur) & 0xFFFFu);
}
void SetParticleColor   (inout Particle p, float4 c)
{
    p.colorRG = BrazePackHalf2(c.r, c.g);
    p.colorBA = BrazePackHalf2(c.b, c.a);
}

#endif // BRAZE_PARTICLE_STRUCT_INCLUDED
