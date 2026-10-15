using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// GPU particle system — the Unity adaptation of the publisher-owned WebGPU prototype three-js-particles.
    /// Owns the particle storage buffer, the COMPACT-built visible-index list, the indirect draw
    /// args, and the over-lifetime curve LUTs. Dispatches its compute each frame; rendering is done
    /// by <see cref="BrazeRenderFeature"/>, which pulls from the static <see cref="Active"/> registry.
    ///
    /// STANDALONE by design — no container. A <see cref="BrazePyroEmitter"/> can optionally provide the spawn
    /// shape and spawn-time motion for connected particle systems. Fluid Follow can use that emitter's resolved live
    /// container, or an explicitly connected <see cref="BrazeVDBImport"/> can provide playback motion without taking
    /// ownership of particle spawning — either path is a single cheap field sample per particle.
    /// Inspector mirrors that prototype's three groups: SYSTEM (duration/looping/time-scale/space),
    /// PARTICLE (start values + renderer + over-life + collision), SPAWN (emission + shape + events).
    /// </summary>
    [AddComponentMenu("BrazeFX/Particles/Braze Particle System", 20)]
    public sealed class BrazeParticleSystem : MonoBehaviour, IBrazeAnimatedTarget
    {
        public static readonly List<BrazeParticleSystem> Active = new();

        public enum SimSpace { World, Local }
        // Append-only: serialized by integer. Cloner deliberately follows Mesh so every existing shape keeps its value.
        public enum EmitShape { Sphere, Hemisphere, Cone, Box, Circle, Plane, Terrain, Torus, Mesh, Cloner }
        // Where on the Mesh shape particles spawn. Surface = area-weighted scatter across faces (uniform density);
        // Vertices = one of the mesh's verts (clumps at dense topology); Edges = along the triangle edges (wireframe).
        public enum MeshEmitSource { Surface, Vertices, Edges }
        // NOTE: append-only — values are serialized by int. Mesh is last so existing assets (None = 4) don't shift.
        public enum RenderMode { Sprite, Stretched, Flat, Upright, None, Mesh }
        public enum Facing { Camera, Velocity, World, Local }
        public enum BlendMode { Additive, Alpha, Multiply }
        public enum FlipbookAnimation { WholeSheet, SingleRow }
        public enum FlipbookTimeMode { FPS, Lifetime, Speed }
        // Append-only: serialized by integer. Once hides after its final frame; HoldLast preserves a clamped result.
        public enum FlipbookPlayback { Once, Loop, PingPong, HoldLast }
        public enum FlipbookRowMode { Custom, Random }

        // Sub-emitter: this system is driven entirely by a parent's events (position/count/timing come
        // from the event). It has no continuous emission, SYSTEM, or SPAWN settings — only Motion/Events/
        // Renderer + Capacity. Set when created via "Create Sub-Emitter". Its Transform is unused.
        [HideInInspector] public bool isSubEmitter = false;
        public bool IsSurfaceDisintegratorDebrisTarget => IsAssignedAsSurfaceDisintegratorDebris(this);
        public bool IsVoronoiFractureDebrisTarget => IsAssignedAsVoronoiFractureDebris(this);
        public bool IsBrazePyroEmitterParticleTarget => BrazePyroEmitter.TryGetParticleSource(this, out _);
        public bool IsBrazeVdbFluidFollowTarget => BrazeVDBImport.TryGetParticleFluidSource(this, out _);
        public bool IsRequestDrivenEmitter => isSubEmitter || IsSurfaceDisintegratorDebrisTarget || IsVoronoiFractureDebrisTarget;

        // ---------------- SYSTEM ----------------
        [Tooltip("System duration in seconds (emission window; loops if Looping).")]
        [BrazeAnimatable("SYSTEM/Duration", 0.01f, 30f)]
        [Min(0.01f)] public float duration = 5f;
        public bool looping = true;
        [Tooltip("Playback speed multiplier.")]
        [BrazeAnimatable("SYSTEM/Time Scale", 0f, 4f)]
        [Min(0f)] public float timeScale = 1f;
        [Tooltip("World = particles detach from the system at birth. Local = particles follow the Transform.")]
        public SimSpace simSpace = SimSpace.World;

        // ---------------- SPAWN ----------------
        [Tooltip("Hard cap. Buffers are sized to this many particles (up to 5,000,000).")]
        public int capacity = 100_000;
        [Tooltip("Particles spawned per second.")]
        [BrazeAnimatable("SPAWN/Rate", 0f, 10000f)]
        [Min(0f)] public float rate = 1000f;

        // ---------------- SPAWN: bursts (additive instant spawns) ----------------
        // Bursts spawn a (randomized) count instantly at scheduled frames, ON TOP of continuous Rate
        // (set Rate = 0 for burst-only). Count / Start Frame / Loop Delay are random between min..max.
        public bool burst = false;
        public BrazeIntRange burstCount = new(100);            // particles per burst (Const / Range)
        public BrazeIntRange burstStart = new(0);              // first-burst frame (Const / Range)
        public bool burstLoop = false;
        [Tooltip("How many bursts when looping (0 = infinite).")]
        [Min(0)] public int burstLoops = 3;
        public BrazeIntRange burstLoopDelay = new(30);         // frames between bursts (Const / Range)

        // ---------------- PARTICLE: start values ----------------
        public BrazeMinMaxCurve startLifetime = BrazeMinMaxCurve.Range(1.0f, 1.8f);
        [Tooltip("Particles never age out — they live until you stop/clear or the pool fills. Over-lifetime modules " +
                 "(Velocity/Size/Opacity/Color over Lifetime) are bypassed, so particles keep their START size, colour " +
                 "and full opacity (an opacity fade-in won't leave them invisible). Use constant Size/Color for the look.")]
        public bool infiniteLifetime = false;
        public BrazeMinMaxCurve startSpeed = BrazeMinMaxCurve.Range(1.5f, 3.0f);
        public BrazeMinMaxCurve startSize = BrazeMinMaxCurve.Range(0.06f, 0.12f);
        public BrazeMinMaxCurve startRotation = BrazeMinMaxCurve.Constant(0f);
        [ColorUsage(true, true)] public Color startColor = new(1f, 0.55f, 0.2f, 1f);

        // ---------------- SPAWN: shape ----------------
        public EmitShape shape = EmitShape.Cone;
        [BrazeAnimatable("SHAPE/Radius", 0f, 5f)]
        [Min(0f)] public float shapeRadius = 0.25f;
        [BrazeAnimatable("SHAPE/Cone Angle", 0f, 90f)]
        [Range(0f, 90f)] public float coneAngle = 18f;
        public Vector3 boxSize = Vector3.one;
        public Vector2 planeSize = new(2f, 2f);
        [BrazeAnimatable("SHAPE/Terrain Height", 0f, 2f)]
        [Min(0f)] public float terrainHeight = 0.3f;
        [BrazeAnimatable("SHAPE/Terrain Freq", 0f, 8f)]
        [Min(0f)] public float terrainFreq = 1.5f;
        [Tooltip("Torus tube (minor) radius. The Radius field is the ring (major) radius.")]
        [BrazeAnimatable("SHAPE/Torus Thickness", 0f, 2f)]
        [Min(0f)] public float torusThickness = 0.1f;
        public bool emitFromShell = false;
        [Tooltip("Mesh shape: the renderer to spawn particles across (world space).")]
        public Renderer shapeMesh;
        [Tooltip("Cloner shape: particles spawn at the final GPU clone positions after distribution, alignment, and effectors.")]
        public BrazeCloner shapeCloner;
        [Tooltip("Where on the mesh particles spawn:\n" +
                 "• Surface — even, area-weighted scatter across the faces (the usual choice).\n" +
                 "• Vertices — at the mesh's vertices (clumps where the mesh is denser).\n" +
                 "• Edges — along the triangle edges (a wireframe look).")]
        public MeshEmitSource meshEmitSource = MeshEmitSource.Surface;
        [Tooltip("Surface / Edges: size of the spawn-point pool scattered over the mesh. Higher = finer, more " +
                 "uniform coverage, but more per-emit-frame cost (the pool is re-evaluated as the mesh moves). " +
                 "A few thousand is plenty for most meshes. Ignored for Vertices (uses the mesh's own verts).")]
        [Min(16)] public int meshScatterCount = 8192;
        [Tooltip("Mesh shape: gate spawning by a Braze Surface Mask so particles only emit from the masked region of " +
                 "the surface. Off = spawn across the whole surface.")]
        public bool useMask = false;
        [Tooltip("The Surface Mask to gate by. Leave empty to auto-use a Braze Surface Mask on the Mesh's object.")]
        public BrazeSurfaceMask mask;
        [Tooltip("Mesh / Cloner shape: source-motion response at birth. Negative values push particles against " +
                 "the source motion for a stronger trail, 0 detaches them in world space, and positive values " +
                 "make them follow the moving source. -1 = full counter-motion, 1 = full source velocity.")]
        // Keep the established animation path so existing Braze animation tracks continue to resolve this field.
        [BrazeAnimatable("SHAPE/Inherit Velocity", -2f, 2f)]
        [Range(-2f, 2f)] public float shapeInheritVelocity = 0f;

        // ---------------- PARTICLE: over-lifetime ----------------
        public bool velocityOverLifetime = false;
        public BrazeMinMaxCurve velocityX = BrazeMinMaxCurve.Constant(0f);
        public BrazeMinMaxCurve velocityY = BrazeMinMaxCurve.Constant(0f);
        public BrazeMinMaxCurve velocityZ = BrazeMinMaxCurve.Constant(0f);

        public bool sizeOverLifetime = false;
        public BrazeMinMaxCurve sizeCurve = BrazeMinMaxCurve.Constant(1f);

        public bool opacityOverLifetime = false;
        public BrazeMinMaxCurve opacityCurve = BrazeMinMaxCurve.Curve(
            new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.8f, 1f), new Keyframe(1f, 0f)));

        // Multiplies the start colour over age (author a hot→cool ramp). LDR [0,1] modulation — HDR
        // punch comes from Start Color (HDR) × Emissive, so the gradient stays a tint × alpha curve.
        public bool colorOverLifetime = false;
        public Gradient colorGradient = DefaultColorGradient();

        // Colour by speed — remaps the particle's speed (range → 0..1) into a gradient, then multiplies
        // it into the colour. Stacks on top of Color over Lifetime (both multiply).
        public bool colorBySpeed = false;
        public Gradient colorBySpeedGradient = DefaultColorGradient();
        [Tooltip("Speed (units/sec) mapped to the gradient: x → gradient start, y → gradient end.")]
        public Vector2 colorBySpeedRange = new(0f, 5f);

        // ---------------- FORCES (C4D-style effectors list) ----------------
        [Tooltip("Forces scoped to this system. Only forces dropped here affect this particle system; loose hierarchy forces do nothing.")]
        public List<BrazeParticleForce> forces = new();

        // ---------------- PARTICLE: collision ----------------
        public bool collisionEnabled = false;
        [Tooltip("Collision contact radius as a fraction of the particle's render size. Higher = particles " +
                 "rest further off colliders; 0 = treat them as points. Collide via Braze Particle Collider components.")]
        [BrazeAnimatable("COLLISION/Radius Scale", 0f, 2f)]
        [Min(0f)] public float collisionRadiusScale = 0.5f;
        [Tooltip("Let particles at a resting contact stop bouncing/jittering under steady gravity. A meaningful " +
                 "tangential or outward force wakes them automatically.")]
        public bool collisionSleep = true;
        [Tooltip("Impacts slower than this are treated as resting contact instead of bouncing (units/second).")]
        [Min(0f)] public float collisionSleepSpeed = 1f;
        [Tooltip("How long low-speed contact must remain stable before residual tangential motion is set to zero.")]
        [Min(0f)] public float collisionSleepDelay = 0.1f;
        [Tooltip("Draw the representative collision contact sphere in the Scene view.")]
        public bool showCollisionGizmo = true;

        [Tooltip("Push overlapping particles apart so a pile/stack reads as solid instead of all merging into the " +
                 "same spot. GPU spatial-hash neighbour separation; cost scales with how densely particles clump.")]
        public bool separationEnabled = false;
        [Tooltip("How hard overlapping particles shove apart each frame. 1 = resolve the full overlap immediately " +
                 "(can jitter when very dense); ~0.5 relaxes the pile over a few frames (smoother).")]
        [BrazeAnimatable("COLLISION/Separation Stiffness", 0f, 1f)]
        [Range(0f, 1f)] public float separationStiffness = 0.5f;
        [Tooltip("Separation spacing as a multiple of the collision contact radius. 1 = particles just touch; " +
                 ">1 spreads them out, <1 lets them overlap more.")]
        [BrazeAnimatable("COLLISION/Separation Spacing", 0f, 2f)]
        [Range(0.1f, 2f)] public float separationScale = 1f;
        [Tooltip("Relaxation passes per frame. More = tighter, less interpenetration in dense piles, at linear cost.")]
        [Range(1, 4)] public int separationIterations = 1;

        // ---------------- PARTICLE: renderer ----------------
        public RenderMode renderMode = RenderMode.Sprite;
        // One mesh + optional albedo per entry. The albedo is sampled through the mesh's UV0; entries are baked into
        // a Texture2DArray and the per-particle mesh pick selects both the geometry and its matching texture slice.
        [System.Serializable]
        public struct MeshEntry
        {
            public Mesh mesh;
            [Tooltip("Optional per-mesh albedo (sampled via the mesh's UV0). Empty = the Default Texture, else white.")]
            public Texture2D texture;
        }
        [Tooltip("Mesh render mode: one or more meshes drawn per particle (random pick per particle). Each is " +
                 "positioned + uniformly scaled by particle size, with a stable random tumble (Alignment = Velocity " +
                 "aligns the mesh's +Y to its motion). Lit by the main directional light + ambient.")]
        public List<MeshEntry> meshes = new();
        [Tooltip("Mesh mode: how strongly the main directional light shades the meshes (0 = flat ambient only, " +
                 "1 = full Lambert). Ambient (sky) always applies.")]
        [BrazeAnimatable("RENDERER/Mesh Light Strength", 0f, 1f)]
        [Range(0f, 1f)] public float meshLightStrength = 1f;
        [Tooltip("Mesh mode: base scale multiplier on top of the particle's size (which itself follows Size over " +
                 "Lifetime). Use to match the mesh's authored units to your particle sizes.")]
        [BrazeAnimatable("RENDERER/Mesh Scale", 0f, 5f)]
        [Min(0f)] public float meshScale = 1f;
        [Tooltip("Mesh mode: spin rate (radians/sec), integrated continuously from spawn on top of the stable random " +
                 "orientation. Rotation pauses when collision sleep puts the particle at rest.")]
        [BrazeAnimatable("RENDERER/Mesh Spin", 0f, 20f)]
        public float meshSpin = 0f;
        [Tooltip("Mesh mode: extra spin rate per unit of speed (radians/sec per unit/sec). Only the current frame's " +
                 "speed contributes, so impacts and sleep cannot retroactively jump the rotation angle.")]
        [BrazeAnimatable("RENDERER/Mesh Spin By Speed", 0f, 20f)]
        public float meshSpinBySpeed = 0f;
        public Facing facing = Facing.Camera;
        public BlendMode blendMode = BlendMode.Additive;
        public BrazeBuiltinTex texture = BrazeBuiltinTex.Flame;
        public Texture2D customTexture;
        [Tooltip("Mesh mode only: fallback albedo for mesh entries that do not assign their own texture. " +
                 "Empty renders white. Kept separate from the sprite texture so changing Render Shape cannot " +
                 "silently carry a sprite atlas or flipbook into mesh rendering.")]
        public Texture2D meshDefaultTexture;
        [ColorUsage(true, true)] public Color tint = Color.white;
        [Tooltip("HDR brightness multiplier on the particle's own colour (additive/alpha). Push >1 to drive Bloom " +
                 "(glowing embers). It does NOT emit light into the scene — it only scales how bright this particle draws.")]
        [BrazeAnimatable("RENDERER/Brightness", 0f, 8f)]
        [Min(0f)] public float emissiveGain = 1f;
        // Negative is the backward-compatible fixed-brightness sentinel. Existing scenes only serialize
        // emissiveGain, so they continue to resolve to (gain, gain) until an artist edits the range control.
        [SerializeField, HideInInspector] float emissiveGainMin = -1f;
        public Vector2 pivot = Vector2.zero;
        [Tooltip("Per-particle random flip probability (x, y).")]
        public Vector2 flip = Vector2.zero;
        [Tooltip("Stretch along velocity (world units per unit/sec).")]
        [BrazeAnimatable("RENDERER/Stretch Velocity", 0f, 2f)]
        public float stretchVelocity = 0.1f;
        [BrazeAnimatable("RENDERER/Stretch Length", 0f, 5f)]
        public float stretchLength = 1f;
        [Tooltip("Per-particle random ± variation of the stretch amount (0 = every particle stretches the same, " +
                 "1 = ±100%). Breaks up the uniform look so streaks feel natural.")]
        [BrazeAnimatable("RENDERER/Stretch Variation", 0f, 1f)]
        [Range(0f, 1f)] public float stretchVariation = 0f;

        [Tooltip("Fade particles as they approach scene geometry (needs URP Depth Texture).")]
        public bool softParticles = false;
        [BrazeAnimatable("RENDERER/Soft Fade", 0f, 4f)]
        [Min(0f)] public float softFade = 0.5f;
        [BrazeAnimatable("RENDERER/Alpha Cutoff", 0f, 1f)]
        [Range(0f, 1f)] public float alphaCutoff = 0f;
        public bool flipbook = false;
        [Min(1)] public int flipbookColumns = 2;
        [Min(1)] public int flipbookRows = 2;
        [Tooltip("Whole Sheet plays every tile. Single Row treats each row as a separate animation.")]
        public FlipbookAnimation flipbookAnimation = FlipbookAnimation.WholeSheet;
        [Tooltip("FPS is independent of particle lifetime. Lifetime maps the sheet across normalized age. Speed selects a frame from particle speed.")]
        public FlipbookTimeMode flipbookTimeMode = FlipbookTimeMode.FPS;
        [Tooltip("Once hides after the final frame; Loop repeats; Ping Pong alternates; Hold Last stays on the final frame.")]
        public FlipbookPlayback flipbookPlayback = FlipbookPlayback.Loop;
        [Tooltip("Frames per second when Time Mode is FPS.")]
        [BrazeAnimatable("RENDERER/Flipbook FPS", 0f, 60f)]
        [Min(0.01f)] public float flipbookFps = 24f;
        [Tooltip("Particle speed mapped to the first and last animation frames when Time Mode is Speed.")]
        public Vector2 flipbookSpeedRange = new(0f, 5f);
        [BrazeAnimatable("RENDERER/Flipbook Cycles", 0f, 8f)]
        [Min(1f)] public float flipbookCycles = 1f;
        [Tooltip("First frame within the selected sequence (zero based).")]
        [Min(0)] public int flipbookStartFrame = 0;
        [Tooltip("Choose a stable random start frame for each particle instead of Start Frame.")]
        public bool flipbookRandomStart = false;
        [Tooltip("How Single Row chooses a row for each particle.")]
        public FlipbookRowMode flipbookRowMode = FlipbookRowMode.Custom;
        [Tooltip("Row used by Single Row + Custom (zero based, top to bottom).")]
        [Min(0)] public int flipbookRow = 0;
        [Tooltip("Multiply opacity over the flipbook's own playback cycle, independently of particle lifetime. " +
                 "Use this to fade a Once animation to zero before its final frame disappears.")]
        public bool flipbookOpacityOverTime = false;
        public BrazeMinMaxCurve flipbookOpacityCurve = BrazeMinMaxCurve.Curve(
            new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.8f, 1f), new Keyframe(1f, 0f)));

        public bool showGizmo = true;

        // ---------------- EVENTS ----------------
        // When a particle event fires, the user picks an operation:
        //   • Spawn Particles — append GPU spawn requests into a target SUB-EMITTER (a child system the
        //     user fully art-directs; enables chains/fireworks). Entirely GPU-side, no readback.
        //   • Fire Unity Event — record a CAPPED sample (≤ EventCapacity/frame) of world positions, read
        //     back async and dispatched to a UnityEvent(Vector3). The only CPU touch, and hard-bounded.
        [System.Serializable] public sealed class Vector3Event : UnityEvent<Vector3> { }

        [System.Serializable]
        public sealed class SpawnEvent
        {
            public bool enabled = false;
            // Independent — both can run at the same event (e.g. splash sub-particles AND seed a spread).
            [Tooltip("Spawn particles into a sub-emitter at the event point.")]
            public bool spawnParticles = false;
            [Tooltip("Fire a Unity Event with the event's world position (e.g. seed a surface spread).")]
            public bool fireEvent = false;
            [Tooltip("Probability the event fires for a given particle (0..1).")]
            [Range(0f, 1f)] public float chance = 1f;

            // Spawn Particles
            [Tooltip("Sub-emitter to spawn into. Empty = spawn into this same system.")]
            public BrazeParticleSystem target;
            [Tooltip("Particles spawned per triggering particle.")]
            [Min(0)] public int count = 8;
            [Tooltip("Random spawn-position scatter radius (world units) so children don't stack on the point.")]
            [Min(0f)] public float positionJitter = 0f;
            public bool inheritColor = true;

            // Fire Unity Event — receives the particle's world position at event time.
            public Vector3Event onFire = new();

            // GPU op bitmask: bit0 = spawn particles, bit1 = fire event. 0 when disabled / nothing selected.
            public int OpCode => !enabled ? 0 : ((spawnParticles ? 1 : 0) | (fireEvent ? 2 : 0));
        }
        public SpawnEvent onDeath = new();
        [Tooltip("Fires when a particle hits a collider — wire On Fire to e.g. BrazeSpread.Seed to spread from each impact.")]
        public SpawnEvent onCollision = new();

        // ---------------- STATS / DEBUG ----------------
        [Tooltip("Draw the Braze Stats overlay (GPU memory, alive count, dispatch cost, FPS) in play mode.")]
        public bool showStats = false;

        public bool RendererVisible => renderMode != RenderMode.None;

        // GPU resources.
        ComputeShader cs;
        int kClear, kResetArgs, kEmit, kEmitRequests, kClearReqCount, kSimulate, kFlowConstrain, kCompact,
            kWriteMeshArgs, kExtractSkinnedEmit, kExtractSkinnedSamples, kCaptureClonerEmit, kMaskGate;
        int kSortInit, kSortStep, kSortScatter;           // Alpha-blend back-to-front depth sort of the visible list
        int kSepClearGrid, kSepWriteArgs, kSepBuildGrid, kSepSolve, kSepApply;
        int kMeshCullReset, kMeshCull;
        GraphicsBuffer particleBuf, visibleBuf, argsBuf;
        GraphicsBuffer sortPairsBuf;    // Alpha depth sort: (key, index) pairs; lazily sized to the pow2 sort window
        GraphicsBuffer cellHeadBuf, partNextBuf, sepDispBuf, sepDispatchArgsBuf;   // inter-particle separation grid (lazy, capacity-tied)
        int sepTableSize;
        GraphicsBuffer headBuf;
        readonly GraphicsBuffer[] reqBuf = new GraphicsBuffer[2];        // ping-pong: write [p], drain [1-p]
        readonly GraphicsBuffer[] reqCountBuf = new GraphicsBuffer[2];
        GraphicsBuffer eventPosBuf, eventCountBuf;
        GraphicsBuffer collisionEventPosBuf, collisionEventCountBuf;
        const int ReqStride = 48;                  // SpawnRequest = 3 × float4
        const int ReqCapacity = 1 << 16;           // max sub-spawns drained per frame (overflow dropped)
        const int EventCapacity = 10;              // hard cap on Unity-event samples per frame
        public const int SubEmitterMaxCapacity = 10_000;   // sub-emitters are capped (prevents chain blow-up)
        public const int MaxEventCount = 100;              // max particles spawned per dying particle
        static readonly uint[] ZeroU = { 0 };
        static readonly Vector4[] EventClear = new Vector4[EventCapacity];
        bool eventReadbackPending;
        bool collisionEventReadbackPending;

        public GraphicsBuffer IncomingReqBuf(int parity) => reqBuf[parity & 1];
        public GraphicsBuffer IncomingReqCount(int parity) => reqCountBuf[parity & 1];
        public int IncomingRequestCapacity => ReqCapacity;
        MaterialPropertyBlock mpb;
        Material mat;

        // Mesh render mode: each assigned mesh is drawn as REAL opaque geometry via Graphics.RenderMeshIndirect
        // (own material "BrazeFX/ParticleMesh" with ForwardLit + ShadowCaster + DepthOnly passes), so URP renders
        // them through the normal opaque + shadow pipeline — they depth-sort, receive AND cast shadows. One indirect
        // draw per triangle submesh; each instance is a particle slot, resolved in the vertex shader
        // (dead/other-mesh slots collapse). Built lazily and rebuilt only when mesh topology/albedo assignments change.
        Material meshMat;
        GraphicsBuffer meshArgsBuf;                       // one IndirectDrawIndexedArgs per drawable submesh
        GraphicsBuffer meshCullVisibleBuf, meshCullCountBuf;   // mesh-mode on-screen instance list + count (capacity-tied)
        GraphicsBuffer meshCullDispatchArgsBuf;                // indirect dispatch args (ceil(alive/64)) for MeshCull
        static readonly Vector4[] meshCullPlanes = new Vector4[6];
        static readonly Plane[] meshCullPlaneScratch = new Plane[6];
        MaterialPropertyBlock[] meshMpb;                  // per draw: carries the owning mesh/texture slice
        Texture2DArray meshTexArray;                      // one albedo slice per baked mesh (slice = the shader's pick)
        readonly List<Mesh> meshDrawMeshes = new();       // one entry per drawable submesh (may repeat a Mesh)
        int meshBakedCount;
        int meshDrawCount;
        bool meshDirty = true;
        int meshBuildSignature = int.MinValue;
        double meshBuildRetryAt;
        const double MeshBuildRetryDelay = 1.0;
        const float MeshWorldBoundsSize = 100000f;        // generous bounds so RenderMeshIndirect is never frustum-culled (per-instance culling is GPU-side, below)
        [Tooltip("Mesh mode: GPU-cull instances outside the camera frustum so cost scales with what's ON-SCREEN, " +
                 "not the whole pool. Big win when meshes spread past the view. Culls against the main camera, so " +
                 "in Play mode some Scene-view meshes may pop — turn off while authoring if that bothers you.")]
        public bool meshFrustumCull = true;
        [Tooltip("Per-instance cull bounding radius as a multiple of the particle's mesh size. Raise if meshes pop " +
                 "at screen edges (their geometry extends past the cull sphere).")]
        [Min(0.1f)] public float meshCullRadiusScale = 1.2f;
        [Tooltip("Camera used for mesh-instance culling. Defaults to Camera.main.")]
        public Camera meshCullCamera;
        Camera _mainCamCache;   // caches Camera.main so mesh culling skips the per-frame FindGameObjectWithTag lookup

        // Camera.main does a tagged-object lookup every call; cache it and only re-query when the cached
        // camera is gone or disabled, preserving the "defaults to Camera.main" behaviour.
        Camera ResolvedMainCamera()
        {
            if (_mainCamCache == null || !_mainCamCache.isActiveAndEnabled)
                _mainCamCache = Camera.main;
            return _mainCamCache;
        }
        [Tooltip("Mesh mode: cast shadows into the scene's light shadow maps (uses a ShadowCaster pass). " +
                 "EXPENSIVE — the geometry is re-rendered once per shadow cascade (up to ~5× the vertex cost on top " +
                 "of the camera pass), so leave OFF for high counts. Turn on only for a few large hero meshes.")]
        public bool meshCastShadows = false;
        [Tooltip("Mesh mode: RECEIVE shadows (self-shadow + scene shadows). Gives the meshes real volumetric form. " +
                 "Use Shadow Strength to stop a dense cloud crushing to black as it fills in.")]
        public bool meshReceiveShadows = true;
        [Tooltip("Mesh mode: caps how dark received shadows can drive a particle (1 = full/can go black, " +
                 "0 = shadows ignored). Lower it if a dense/over-time cloud darkens too much from inter-particle " +
                 "self-shadowing, while keeping the self-shadow shaping. The Lambert + ambient 3D look is unaffected.")]
        [BrazeAnimatable("RENDERER/Mesh Shadow Strength", 0f, 1f)]
        [Range(0f, 1f)] public float meshShadowStrength = 0.5f;

        // Billboard renderer draws through BrazeRenderFeature; mesh mode self-draws via RenderMeshIndirect.
        public bool DrawViaFeature => RendererVisible && renderMode != RenderMode.Mesh;
        bool MeshDrawReady => renderMode == RenderMode.Mesh && meshMat != null && meshArgsBuf != null
                           && meshBakedCount > 0 && meshDrawCount > 0;

        Texture2D lutVelX, lutVelY, lutVelZ, lutSize, lutOpacity, lutFlipbookOpacity, lutColor, lutColorSpeed;
        GraphicsBuffer forceBuf;
        GraphicsBuffer forceFalloffBuf;       // forceCount × 256 floats (flat) — each force's centre→edge falloff curve
        float[] falloffData = new float[256];
        int falloffHash;                      // last-uploaded falloff-atlas signature; skips the 256×count rebake when unchanged
        GpuParticleForce[] forceData = new GpuParticleForce[8];
        readonly List<BrazeParticleForce> forceScratch = new();
        BrazeSurfaceFlow flowForce;   // a Surface Flow force in this frame's set → drives the shared mesh grid

        // Spline Flow (force type 6): a per-system sampled path (positions + tangents) the Simulate kernel streams
        // particles along. Like flowForce, the first listed Spline Flow with a spline owns the buffer.
        BrazeSplineFlow splineFlowForce;
        GraphicsBuffer splinePosBuf, splineTanBuf, dSplinePos, dSplineTan;
        Vector3[] splinePosArr, splineTanArr;
        const int MaxSplineSamples = 256;
        GraphicsBuffer colliderBuf;
        GpuParticleCollider[] colliderData = new GpuParticleCollider[8];

        // Mesh-surface collision (shared deforming triangle grid; one mesh collider per system).
        ComputeShader gridCS;
        BrazeMeshGrid meshGrid;
        BrazeDeformSource meshSource;
        Renderer meshSourceRenderer;
        // Static-surface grid cache: a plain MeshRenderer that hasn't moved reuses last frame's grid as-is —
        // the per-frame extract + rebuild only costs anything when the surface can actually change.
        Mesh gridStaticMesh;
        Matrix4x4 gridStaticMatrix;
        float gridStaticRange;
        BrazeParticleCollider meshCollider;

        // Mesh-SHAPE emission: spawn from a renderer's surface verts (world space), optionally filtered to a Braze
        // Surface Mask's region, along the surface normal. Positions + normals run as parallel buffers.
        GraphicsBuffer emitPointBuf, emitNrmBuf, emitVelBuf, emitPointDummy;
        // Cloner-shape emission reads the Cloner's final transform buffers directly. Only the previous-position
        // history belongs to the particle system; it enables Inherit Velocity without any GPU-to-CPU readback.
        GraphicsBuffer emitClonerPrevPosition, emitClonerDummy;
        int emitClonerSourceId = int.MinValue, emitClonerCount = -1;
        bool emitClonerHasPrevious;
        Vector3[] emitPointArray, emitNrmArray, emitVelArray;
        // GPU skinned-mesh emit path: read the renderer's already-skinned GPU vertex buffer straight into the emit
        // buffers (no CPU BakeMesh / readback / upload). _PrevPos persists for per-vertex velocity.
        GraphicsBuffer emitPrevPosBuf;
        int gpuEmitMeshId = int.MinValue, gpuSrcStride, gpuSrcPosOff, gpuSrcNrmOff;
        bool gpuLayoutOK, gpuHasNrm, gpuHasPrev, gpuRawTargetSet;
        // GPU mask gating: the MaskGate compute transforms REST verts/samples into Braze mask space, evaluates the field,
        // and compacts passing indices into emitPassIdxBuf. Emit samples spawns only from those.
        GraphicsBuffer emitPassIdxBuf, emitPassIdxDummy, emitRestPosBuf, emitPassCountBuf;
        GraphicsBuffer emitSampleIdxBuf, emitSampleWeightBuf, emitSamplePrevPosBuf;
        BrazeSurfaceMask emitGateCachedMask;
        GraphicsBuffer emitGateCachedRest;
        int emitGateCachedFieldVersion, emitGateCachedSpaceVersion, emitGateCachedSampleVersion;
        int emitGateCachedCount = -1, emitGateCachedSourceId = int.MinValue;
        int emitPassIdxCapacity = -1;
        bool emitUsePassIdx;
        bool emitInputLocal, emitHasPrevLocal;
        Matrix4x4 emitLocalToWorld = Matrix4x4.identity;
        Matrix4x4 emitPrevLocalToWorld = Matrix4x4.identity;
        Matrix4x4 emitNormalLocalToWorld = Matrix4x4.identity;
        float emitLocalInvDt = 1f;
        float emitExtraNormalVelocity;
        readonly List<Vector3> emitPointScratch = new();
        readonly List<Vector3> emitNrmScratch = new();
        readonly List<Vector3> emitVelScratch = new();
        readonly List<Vector3> emitRestVerts = new();     // transient scratch while a mesh spawn pool is rebuilt
        readonly List<Vector3> emitBakeNrm = new();

        // Surface / Edges scatter: each sample is 3 vertex indices + 3 weights (barycentric for faces; a 2-vertex
        // lerp for edges with the 3rd weight 0). Rebuilt only when the mesh / source / count changes; per emit frame
        // we just re-evaluate the weighted positions from the current (possibly deformed) verts — cheap.
        // The compact sample pool is serialized so an ordinary imported mesh can keep Read/Write disabled. This is
        // the same data the runtime already needs (three indices/weights plus one rest position/normal per sample),
        // not a duplicate CPU copy of the source mesh. OnValidate refreshes it only when its mesh/source/count changes.
        [SerializeField, HideInInspector] Mesh meshSampleMeshCached;
        [SerializeField, HideInInspector] int meshSampleVertexCountCached = -1;
        [SerializeField, HideInInspector] int[] meshSampleIdx;            // 3 per sample
        [SerializeField, HideInInspector] float[] meshSampleW;            // 3 per sample
        [SerializeField, HideInInspector] Vector3[] meshSampleRestPos;
        [SerializeField, HideInInspector] Vector3[] meshSampleRestNrm;
        [SerializeField, HideInInspector] int meshSampleCountCached = -1;
        [SerializeField, HideInInspector] MeshEmitSource meshSampleSourceCached = (MeshEmitSource)(-1);
        int meshSampleVersion;
        int emitGpuSampleVersion = -1, emitGpuSampleCount = -1, emitGpuSampleMeshId = int.MinValue;
        bool emitGpuSampleLocalUploaded, emitGpuSampleTableUploaded;
        int emitLocalMotionKey = int.MinValue;
        Matrix4x4 emitLastLocalToWorld = Matrix4x4.identity;
        float emitLastLocalTime = -1f;
        bool emitLocalHasPrev;
        bool emitTrackedHasPrev;
        int emitLocalMotionFrame = int.MinValue;
        int emitSkinnedSamplePrevKey = int.MinValue, emitSkinnedSamplePrevCount = -1;
        bool emitSkinnedSampleHasPrev;
        readonly List<int> meshTriScratch = new();
        // External emission source: another component (e.g. Surface Disintegrator debris) pushes world-space spawn
        // points here each frame. Takes priority over the mesh source and emits via the world-space path.
        readonly List<Vector3> extEmitPts = new(), extEmitNrm = new(), extEmitVel = new();
        int extEmitFrame = -10;
        bool HasExternalEmit => extEmitFrame >= TickFrame - 1 && extEmitPts.Count > 0;
        GraphicsBuffer dGridPos, dGridNrm, dGridIdx, dGridCellCount, dGridCellItems;   // dummies bound when off
        readonly List<BrazeParticleCollider> colliderScratch = new();

        // Fluid-follow: 1³ dummies bound to the Simulate kernel's fluid-field slots when follow is off / no container.
        Texture3D dummyFluidVel, dummyFluidOcc;

        // Attachment: an external master (Voronoi Fracture) can have spawned particles rigidly RIDE a source body.
        // It pushes its per-body pos/rot buffers here each frame; the sim resolves attached particles from them.
        GraphicsBuffer attachBuf;                       // per particle slot: xyz local offset, w = bodyIndex+1
        GraphicsBuffer attachDummy, attachPosDummy, attachRotDummy;   // 1-elem dummies bound when attach is off
        GraphicsBuffer flowHintBuf;      // Surface Flow warm-start: last contact triangle per slot (capacity-sized)
        GraphicsBuffer attachSrcPos, attachSrcRot;      // master per-body buffers (not owned — do not release)
        int attachSrcCount;
        int attachSrcFrame = -10;
        bool AttachActive => attachSrcFrame >= TickFrame - 1 && attachSrcPos != null && attachSrcRot != null && attachSrcCount > 0;

        /// <summary>Master effects (e.g. Voronoi Fracture) call this each frame so particles they spawned with the
        /// attach flag ride the given per-body transform buffers. Stops attaching automatically if calls cease.</summary>
        public void SetAttachmentSource(GraphicsBuffer posState, GraphicsBuffer rot, int bodyCount)
        {
            attachSrcPos = posState;
            attachSrcRot = rot;
            attachSrcCount = Mathf.Max(0, bodyCount);
            attachSrcFrame = TickFrame;
        }

        int allocCapacity;
        float emitAccum;
        float systemAge;
        int systemFrame;          // frames since allocation — drives frame-based bursts
        int nextBurstFrame;       // scheduled frame of the next burst
        int burstsFired;
        bool burstDone;
        bool lutsDirty = true;
        bool wasRequestDriven;
        Matrix4x4 lastModel = Matrix4x4.identity;   // mirror of the _Model bound to the sprite shader (for trails)
        bool lastModelUsesLocalVelocity;
        static readonly uint[] ArgsReset = { 6, 0, 0, 0 };
        static readonly int IdParticles = Shader.PropertyToID("_Particles");
        static readonly int IdVisibleIndices = Shader.PropertyToID("_VisibleIndices");
        static readonly int IdIndirect = Shader.PropertyToID("_Indirect");
        static readonly int IdSortPairs = Shader.PropertyToID("_SortPairs");
        static readonly int IdSortCamPos = Shader.PropertyToID("_SortCamPos");
        static readonly int IdSortCamForward = Shader.PropertyToID("_SortCamForward");
        static readonly int IdSortModel = Shader.PropertyToID("_SortModel");
        static readonly int IdSortCount = Shader.PropertyToID("_SortCount");
        static readonly int IdSortJ = Shader.PropertyToID("_SortJ");
        static readonly int IdSortK = Shader.PropertyToID("_SortK");
        static readonly int IdRowThreads = Shader.PropertyToID("_RowThreads");

        // Stats state — alive count via non-blocking readback of the indirect args; FPS smoothed.
        int aliveCount;
        bool statsReadbackPending;
        uint resourceGeneration;
        float statsFps;
        int lastForceCount, lastColliderCount;

        public Material RenderMaterial => mat;
        public GraphicsBuffer ArgsBuffer => argsBuf;
        public MaterialPropertyBlock Properties => mpb;
        public bool Ready => mat != null && argsBuf != null;

        // The editor preview supplies its own clock. Keeping that clock inside the explicit preview path means
        // the normal component remains completely idle in edit mode (no ExecuteAlways / hidden Update cost).
#if UNITY_EDITOR
        float tickDeltaTime;
        float tickTime;
        int tickFrame;
        bool editorPreviewActive;

        float TickDeltaTime => editorPreviewActive ? tickDeltaTime : Time.deltaTime;
        float TickTime => editorPreviewActive ? tickTime : Time.time;
        int TickFrame => editorPreviewActive ? tickFrame : Time.frameCount;
#else
        float TickDeltaTime => Time.deltaTime;
        float TickTime => Time.time;
        int TickFrame => Time.frameCount;
#endif

        static bool IsAssignedAsSurfaceDisintegratorDebris(BrazeParticleSystem system)
        {
            if (system == null) return false;

            var active = BrazeSurfaceDisintegrator.Active;
            BrazeRuntimeRegistry.PruneNulls(active);
            for (int i = 0; i < active.Count; i++)
            {
                var d = active[i];
                if (d != null && d.isActiveAndEnabled && d.debrisEmitter == system) return true;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var all = Resources.FindObjectsOfTypeAll<BrazeSurfaceDisintegrator>();
                for (int i = 0; i < all.Length; i++)
                {
                    var d = all[i];
                    if (d == null || !d.isActiveAndEnabled || d.debrisEmitter != system) continue;
                    if (!d.gameObject.scene.IsValid()) continue;
                    return true;
                }
            }
#endif
            return false;
        }

        // A particle system used as a Voronoi Fracture's secondary-particle emitter is request-driven too: it must
        // NOT spawn from its own shape, only from the GPU debris requests the fracture appends. Mirrors the
        // disintegrator detection above.
        static bool VoronoiUsesDebrisEmitter(BrazeVoronoiFracture fracture, BrazeParticleSystem system)
        {
            if (fracture == null || system == null || fracture.debrisEmitters == null) return false;
            for (int i = 0; i < fracture.debrisEmitters.Count; i++)
                if (fracture.debrisEmitters[i] == system) return true;
            return false;
        }

        static bool IsAssignedAsVoronoiFractureDebris(BrazeParticleSystem system)
        {
            if (system == null) return false;

            var active = BrazeVoronoiFracture.Active;
            BrazeRuntimeRegistry.PruneNulls(active);
            for (int i = 0; i < active.Count; i++)
            {
                var v = active[i];
                if (v != null && v.isActiveAndEnabled && VoronoiUsesDebrisEmitter(v, system)) return true;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var all = Resources.FindObjectsOfTypeAll<BrazeVoronoiFracture>();
                for (int i = 0; i < all.Length; i++)
                {
                    var v = all[i];
                    if (v == null || !v.isActiveAndEnabled || !VoronoiUsesDebrisEmitter(v, system)) continue;
                    if (!v.gameObject.scene.IsValid()) continue;
                    return true;
                }
            }
#endif
            return false;
        }

        void OnEnable()
        {
#if UNITY_EDITOR
            // Edit-mode resources are opt-in and are allocated only when the Scene preview Play button is used.
            if (!Application.isPlaying) return;
#endif
            EnsureResources();
        }

        bool EnsureResources()
        {
            if (Ready)
            {
                BrazeRuntimeRegistry.AddUnique(Active, this);
                return true;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[BrazeFX] Compute shaders are not supported on this device.");
                if (Application.isPlaying) enabled = false;
                return false;
            }

            var csAsset = Resources.Load<ComputeShader>("BrazeParticles");
            if (csAsset == null)
            {
                Debug.LogError("[BrazeFX] Could not load 'BrazeParticles' compute shader (needs a Resources/ folder).");
                if (Application.isPlaying) enabled = false;
                return false;
            }
            // Own INSTANCE of the compute, not the shared asset — so each system's uniforms (start ranges,
            // capacity, event params, buffers) are fully isolated. Sharing one asset across systems let
            // parent/sub-emitter uniform writes bleed across each other (e.g. sub-emitter Motion ignored).
            cs = Instantiate(csAsset);
            kClear = cs.FindKernel("Clear");
            kResetArgs = cs.FindKernel("ResetArgs");
            kEmit = cs.FindKernel("Emit");
            kEmitRequests = cs.FindKernel("EmitRequests");
            kClearReqCount = cs.FindKernel("ClearReqCount");
            kSimulate = cs.FindKernel("Simulate");
            kFlowConstrain = cs.FindKernel("FlowConstrain");
            kCompact = cs.FindKernel("Compact");
            kSortInit = cs.FindKernel("SortInit");
            kSortStep = cs.FindKernel("SortStep");
            kSortScatter = cs.FindKernel("SortScatter");
            kWriteMeshArgs = cs.FindKernel("WriteMeshArgs");
            kExtractSkinnedEmit = cs.FindKernel("ExtractSkinnedEmit");
            kExtractSkinnedSamples = cs.FindKernel("ExtractSkinnedSamples");
            kCaptureClonerEmit = cs.FindKernel("CaptureClonerEmit");
            kMaskGate = cs.FindKernel("MaskGate");
            kSepClearGrid = cs.FindKernel("SepClearGrid");
            kSepWriteArgs = cs.FindKernel("SepWriteArgs");
            kSepBuildGrid = cs.FindKernel("SepBuildGrid");
            kSepSolve = cs.FindKernel("SepSolve");
            kSepApply = cs.FindKernel("SepApply");
            kMeshCullReset = cs.FindKernel("MeshCullReset");
            kMeshCull = cs.FindKernel("MeshCull");

            var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Particle");
            if (shader == null)
            {
                Debug.LogError("[BrazeFX] Shader 'BrazeFX/Particle' not found.");
                if (Application.isPlaying) enabled = false;
                return false;
            }
            mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            mpb = new MaterialPropertyBlock();

            EnsureLuts();
            Allocate();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            return true;
        }

        void OnDisable()
        {
            ShutdownResources();
        }

        void ShutdownResources()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            Release();
            forceBuf?.Release(); forceBuf = null;
            colliderBuf?.Release(); colliderBuf = null;
            meshGrid?.Dispose(); meshGrid = null;
            meshSource?.Dispose(); meshSource = null;
            meshSourceRenderer = null; meshCollider = null;
            emitPointBuf?.Release(); emitPointBuf = null;
            emitNrmBuf?.Release(); emitNrmBuf = null;
            emitVelBuf?.Release(); emitVelBuf = null;
            emitPrevPosBuf?.Release(); emitPrevPosBuf = null;
            emitPassIdxBuf?.Release(); emitPassIdxBuf = null;
            emitPassIdxDummy?.Release(); emitPassIdxDummy = null;
            emitRestPosBuf?.Release(); emitRestPosBuf = null;
            emitPassCountBuf?.Release(); emitPassCountBuf = null;
            emitSampleIdxBuf?.Release(); emitSampleIdxBuf = null;
            emitSampleWeightBuf?.Release(); emitSampleWeightBuf = null;
            emitSamplePrevPosBuf?.Release(); emitSamplePrevPosBuf = null;
            emitGateCachedMask = null; emitGateCachedRest = null; emitGateCachedCount = -1; emitPassIdxCapacity = -1;
            emitGateCachedSourceId = int.MinValue; emitGateCachedSampleVersion = -1;
            gpuPendingVB?.Dispose(); gpuPendingVB = null;
            gpuEmitMeshId = int.MinValue; gpuLayoutOK = false; gpuHasPrev = false; gpuRawTargetSet = false;
            emitGpuSampleVersion = -1; emitGpuSampleCount = -1; emitGpuSampleMeshId = int.MinValue;
            emitGpuSampleLocalUploaded = false; emitGpuSampleTableUploaded = false;
            emitLocalMotionKey = int.MinValue; emitLocalHasPrev = false; emitTrackedHasPrev = false;
            emitLocalMotionFrame = int.MinValue; emitLastLocalTime = -1f;
            emitSkinnedSamplePrevKey = int.MinValue; emitSkinnedSamplePrevCount = -1; emitSkinnedSampleHasPrev = false;
            emitPointDummy?.Release(); emitPointDummy = null;
            emitClonerPrevPosition?.Release(); emitClonerPrevPosition = null;
            emitClonerDummy?.Release(); emitClonerDummy = null;
            emitClonerSourceId = int.MinValue; emitClonerCount = -1; emitClonerHasPrevious = false;
            if (gridCS != null) { DestroyOwned(gridCS); gridCS = null; }
            dGridPos?.Release(); dGridPos = null;
            dGridNrm?.Release(); dGridNrm = null;
            dGridIdx?.Release(); dGridIdx = null;
            dGridCellCount?.Release(); dGridCellCount = null;
            dGridCellItems?.Release(); dGridCellItems = null;
            splinePosBuf?.Release(); splinePosBuf = null;
            splineTanBuf?.Release(); splineTanBuf = null;
            dSplinePos?.Release(); dSplinePos = null;
            dSplineTan?.Release(); dSplineTan = null;
            headBuf?.Release(); headBuf = null;
            for (int b = 0; b < 2; b++)
            {
                reqBuf[b]?.Release(); reqBuf[b] = null;
                reqCountBuf[b]?.Release(); reqCountBuf[b] = null;
            }
            eventPosBuf?.Release(); eventPosBuf = null;
            eventCountBuf?.Release(); eventCountBuf = null;
            collisionEventPosBuf?.Release(); collisionEventPosBuf = null;
            collisionEventCountBuf?.Release(); collisionEventCountBuf = null;
            attachDummy?.Release(); attachDummy = null;
            attachPosDummy?.Release(); attachPosDummy = null;
            attachRotDummy?.Release(); attachRotDummy = null;
            attachSrcPos = null; attachSrcRot = null; attachSrcFrame = -10;
            forceFalloffBuf?.Release(); forceFalloffBuf = null;
            meshArgsBuf?.Release(); meshArgsBuf = null;
            if (meshTexArray != null) { DestroyOwned(meshTexArray); meshTexArray = null; }
            if (meshMat != null) { DestroyOwned(meshMat); meshMat = null; }
            meshMpb = null;
            meshDrawMeshes.Clear();
            meshBakedCount = 0; meshDrawCount = 0; meshDirty = true; meshBuildSignature = int.MinValue;
            meshBuildRetryAt = 0.0;
            if (dummyFluidVel != null) { DestroyOwned(dummyFluidVel); dummyFluidVel = null; }
            if (dummyFluidOcc != null) { DestroyOwned(dummyFluidOcc); dummyFluidOcc = null; }
            if (mat != null) { DestroyOwned(mat); mat = null; }
            if (cs != null) { DestroyOwned(cs); cs = null; }   // our own compute instance
            DestroyLut(ref lutVelX); DestroyLut(ref lutVelY); DestroyLut(ref lutVelZ);
            DestroyLut(ref lutSize); DestroyLut(ref lutOpacity); DestroyLut(ref lutFlipbookOpacity);
            DestroyLut(ref lutColor); DestroyLut(ref lutColorSpeed);
#if UNITY_EDITOR
            editorPreviewActive = false;
#endif
        }

        static void DestroyOwned(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        void OnValidate()
        {
            EnsureEventSettings();
            capacity = isSubEmitter ? Mathf.Clamp(capacity, 256, SubEmitterMaxCapacity)
                                    : Mathf.Clamp(capacity, 1024, 5_000_000);
            onDeath.count = Mathf.Clamp(onDeath.count, 0, MaxEventCount);
            flipbookColumns = Mathf.Clamp(flipbookColumns, 1, 1024);
            flipbookRows = Mathf.Clamp(flipbookRows, 1, 1024);
            flipbookFps = Mathf.Max(0.01f, flipbookFps);
            flipbookCycles = Mathf.Max(1f, flipbookCycles);
            emissiveGain = Mathf.Max(0f, emissiveGain);
            if (emissiveGainMin >= 0f) emissiveGainMin = Mathf.Max(0f, emissiveGainMin);
            flipbookSpeedRange = new Vector2(
                Mathf.Min(flipbookSpeedRange.x, flipbookSpeedRange.y),
                Mathf.Max(flipbookSpeedRange.x, flipbookSpeedRange.y));
            collisionSleepSpeed = Mathf.Max(0f, collisionSleepSpeed);
            collisionSleepDelay = Mathf.Max(0f, collisionSleepDelay);
            int sequenceFrames = flipbookAnimation == FlipbookAnimation.SingleRow
                ? flipbookColumns
                : flipbookColumns * flipbookRows;
            flipbookStartFrame = Mathf.Clamp(flipbookStartFrame, 0, Mathf.Max(0, sequenceFrames - 1));
            flipbookRow = Mathf.Clamp(flipbookRow, 0, Mathf.Max(0, flipbookRows - 1));
            lutsDirty = true;

#if UNITY_EDITOR
            // Imported meshes normally have Read/Write disabled. Build the small spawn pool while the Editor can read
            // the import data, then use that pool in Play Mode and player builds without retaining the whole CPU mesh.
            if (!Application.isPlaying && shape == EmitShape.Mesh && TryResolveShapeMesh(shapeMesh, out Mesh emitMesh))
            {
                BrazeSurfaceMask gate = null;
                if (useMask)
                    gate = mask != null ? mask : shapeMesh.GetComponent<BrazeSurfaceMask>();
                EnsureMeshSamples(emitMesh, shapeMesh.transform, gate);
            }
#endif
        }

        public void OnBrazeAnimated() => OnValidate();

        internal Vector2 ResolvedEmissiveGainRange
        {
            get
            {
                float maximum = Mathf.Max(0f, emissiveGain);
                if (emissiveGainMin < 0f) return new Vector2(maximum, maximum);

                float minimum = Mathf.Max(0f, emissiveGainMin);
                return minimum <= maximum
                    ? new Vector2(minimum, maximum)
                    : new Vector2(maximum, minimum);
            }
        }

        internal void EnsureEventSettings()
        {
            // Managed serializable settings do not have Unity fake-null semantics, so ??= is correct here. Scripts
            // can still assign either object to null at runtime; simulation should restore defaults, not crash.
            onDeath ??= new SpawnEvent();
            onCollision ??= new SpawnEvent();
        }

        void EnsureLuts()
        {
            // Use Unity's overloaded == null, not C# ??=. With domain reload disabled a destroyed
            // Texture2D can leave a non-CLR-null managed wrapper; ??= then preserves that fake-null
            // object and BakeCurve fails in Texture2D.SetPixels on the next Play session.
            bool recreated = false;
            if (lutVelX == null) { lutVelX = BrazeLut.CreateCurveTex("BrazeLUT_VelX"); recreated = true; }
            if (lutVelY == null) { lutVelY = BrazeLut.CreateCurveTex("BrazeLUT_VelY"); recreated = true; }
            if (lutVelZ == null) { lutVelZ = BrazeLut.CreateCurveTex("BrazeLUT_VelZ"); recreated = true; }
            if (lutSize == null) { lutSize = BrazeLut.CreateCurveTex("BrazeLUT_Size"); recreated = true; }
            if (lutOpacity == null) { lutOpacity = BrazeLut.CreateCurveTex("BrazeLUT_Opacity"); recreated = true; }
            if (lutFlipbookOpacity == null) { lutFlipbookOpacity = BrazeLut.CreateCurveTex("BrazeLUT_FlipbookOpacity"); recreated = true; }
            if (lutColor == null) { lutColor = BrazeLut.CreateGradientTex("BrazeLUT_Color"); recreated = true; }
            if (lutColorSpeed == null) { lutColorSpeed = BrazeLut.CreateGradientTex("BrazeLUT_ColorSpeed"); recreated = true; }
            if (recreated) lutsDirty = true;   // new textures contain no authored samples until BakeLuts runs
        }

        void BakeLuts()
        {
            EnsureLuts();
            BrazeLut.BakeCurve(lutVelX, velocityX);
            BrazeLut.BakeCurve(lutVelY, velocityY);
            BrazeLut.BakeCurve(lutVelZ, velocityZ);
            BrazeLut.BakeCurve(lutSize, sizeCurve);
            BrazeLut.BakeCurve(lutOpacity, opacityCurve);
            BrazeLut.BakeCurve(lutFlipbookOpacity, flipbookOpacityCurve);
            BrazeLut.BakeGradient(lutColor, colorGradient);
            BrazeLut.BakeGradient(lutColorSpeed, colorBySpeedGradient);
            lutsDirty = false;
        }

        static void DestroyLut(ref Texture2D t)
        {
            if (t != null) DestroyOwned(t);
            // Clear even a Unity fake-null wrapper so the next enable cannot retain it.
            t = null;
        }

        // Default colour-over-life ramp: white opaque at both ends (a no-op until authored, matching
        // Unity's Color over Lifetime default — the start colour passes through unchanged).
        static Gradient DefaultColorGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        void Allocate()
        {
            allocCapacity = isSubEmitter ? Mathf.Clamp(capacity, 256, SubEmitterMaxCapacity)
                                         : Mathf.Clamp(capacity, 1024, 5_000_000);
            capacity = allocCapacity;

            // 48-byte packed Particle (see BrazeParticleStruct.hlsl): position+age FP32, velocity+seed FP32,
            // and lifetime/size/rotation/colour as 16-bit halves. Must match the HLSL struct byte layout exactly.
            particleBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocCapacity, 48);
            visibleBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocCapacity, sizeof(uint));
            // meshCullVisibleBuf/CountBuf are allocated lazily in EnsureMeshCullBuffers() — only mesh-render
            // mode uses them, so Sprite/stretched systems don't pay the capacity×4 B (e.g. 20 MB at 5M).
            argsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 4, sizeof(uint));
            argsBuf.SetData(ArgsReset);

            mpb.SetBuffer("_Particles", particleBuf);
            mpb.SetBuffer("_VisibleIndices", visibleBuf);

            // Force / collider / event resources are independent of capacity — created once, kept across reallocs.
            forceBuf ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, GpuParticleForce.Stride);
            colliderBuf ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, GpuParticleCollider.Stride);
            if (headBuf == null)
            {
                headBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                headBuf.SetData(ZeroU);
                for (int b = 0; b < 2; b++)
                {
                    reqBuf[b] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ReqCapacity, ReqStride);
                    reqCountBuf[b] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                    reqCountBuf[b].SetData(ZeroU);
                }
                eventPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, EventCapacity, 16);
                eventCountBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                collisionEventPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, EventCapacity, 16);
                collisionEventCountBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                attachDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                attachPosDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                attachRotDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            }
            EnsureFalloffBuf(1);

            emitAccum = 0f;
            systemAge = 0f;
            systemFrame = 0;
            burstsFired = 0;
            burstDone = false;
            nextBurstFrame = burstStart.Sample();        // (possibly random) first-burst frame

            cs.SetInt("_Capacity", allocCapacity);
            cs.SetBuffer(kClear, "_Particles", particleBuf);
            Dispatch1D(kClear, allocCapacity);
        }

        void Release()
        {
            // Readbacks complete asynchronously after their source buffer can be released/replaced. Advance a
            // generation and clear the gates so a re-enabled/resized system can issue fresh requests while stale
            // callbacks harmlessly self-reject.
            resourceGeneration++;
            statsReadbackPending = false;
            eventReadbackPending = false;
            collisionEventReadbackPending = false;
            particleBuf?.Release(); particleBuf = null;
            visibleBuf?.Release(); visibleBuf = null;
            sortPairsBuf?.Release(); sortPairsBuf = null;
            meshCullVisibleBuf?.Release(); meshCullVisibleBuf = null;
            meshCullCountBuf?.Release(); meshCullCountBuf = null;
            meshCullDispatchArgsBuf?.Release(); meshCullDispatchArgsBuf = null;
            argsBuf?.Release(); argsBuf = null;
            attachBuf?.Release(); attachBuf = null;   // capacity-sized → tied to the particle pool
            flowHintBuf?.Release(); flowHintBuf = null;   // capacity-sized → tied to the particle pool
            cellHeadBuf?.Release(); cellHeadBuf = null;
            partNextBuf?.Release(); partNextBuf = null;
            sepDispBuf?.Release(); sepDispBuf = null;
            sepDispatchArgsBuf?.Release(); sepDispatchArgsBuf = null;
            sepTableSize = 0;
        }

        // Lazily (re)allocate the separation grid, sized to the pool. The hash table is the next power of two ≥ 2×
        // capacity so buckets stay sparse. Capacity-tied, so reallocated whenever the pool is.
        void EnsureSeparationBuffers()
        {
            if (partNextBuf != null && partNextBuf.count == allocCapacity) return;
            cellHeadBuf?.Release(); partNextBuf?.Release(); sepDispBuf?.Release();
            sepTableSize = Mathf.NextPowerOfTwo(Mathf.Max(64, allocCapacity * 2));
            cellHeadBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sepTableSize, sizeof(int));
            partNextBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocCapacity, sizeof(int));
            sepDispBuf  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocCapacity, 16);
            sepDispatchArgsBuf ??= new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, sizeof(uint));
        }

        // Lazily (re)allocate the per-slot attach buffer, sized to the pool. Cleared on creation so freshly-toggled
        // attachment never misreads an existing free particle's slot as attached.
        void EnsureAttachBuf(bool needStorage)
        {
            if (!needStorage) return;
            if (attachBuf != null && attachBuf.count == allocCapacity) return;
            attachBuf?.Release();
            attachBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, allocCapacity), 16);
            attachBuf.SetData(new Vector4[Mathf.Max(1, allocCapacity)]);   // one-time zero-fill (storage enabled)
        }

        // Lazily (re)allocate the Surface Flow warm-start buffer (last contact triangle per slot), sized to the
        // pool. Zero-filled on creation (0 = no hint); stale entries self-validate on the GPU by distance.
        void EnsureFlowHintBuf()
        {
            if (flowHintBuf != null && flowHintBuf.count == allocCapacity) return;
            flowHintBuf?.Release();
            flowHintBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, allocCapacity), sizeof(uint));
            flowHintBuf.SetData(new uint[Mathf.Max(1, allocCapacity)]);
        }

        void BindAttach(int kernel, bool attachOn, bool attachStorageOn = false)
        {
            cs.SetBuffer(kernel, "_Attach", attachStorageOn ? attachBuf : attachDummy);
            cs.SetBuffer(kernel, "_AttachPosState", attachOn ? attachSrcPos : attachPosDummy);
            cs.SetBuffer(kernel, "_AttachRot", attachOn ? attachSrcRot : attachRotDummy);
        }

        const int MaxGroupsPerDim = 65535;

        // Dispatch `threads` invocations across a 2D group grid so we can exceed the 65535
        // groups-per-dimension cap (a 1D dispatch tops out at ~4.19M particles; we allow 5M).
        // The kernel rebuilds the linear index via FlatIndex(gid) using _RowThreads.
        void Dispatch1D(int kernel, int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            cs.SetInt("_RowThreads", gx * 64);
            cs.Dispatch(kernel, gx, gy, 1);
        }

        void SetRange(string id, BrazeMinMaxCurve c)
        {
            Vector2 r = c.SampleRange(0f);
            cs.SetVector(id, new Vector4(r.x, r.y, 0f, 0f));
        }

        // Returns true if the buffer was (re)allocated, so the caller knows the GPU-side
        // atlas is stale and must be re-uploaded regardless of the content hash.
        bool EnsureFalloffBuf(int rows)
        {
            rows = Mathf.Max(1, rows);
            int floats = rows * 256;
            if (forceFalloffBuf != null && forceFalloffBuf.count == floats) return false;
            forceFalloffBuf?.Release();
            forceFalloffBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, floats, sizeof(float));
            if (falloffData.Length < floats) falloffData = new float[floats];
            return true;
        }

        void AddActiveForceIfMissing(BrazeParticleForce f)
        {
            if (f != null && f.isActiveAndEnabled && !forceScratch.Contains(f)) forceScratch.Add(f);
        }

        // Collect ONLY the forces explicitly dropped into this system. A force sitting elsewhere in the hierarchy
        // must not affect any particle system until the user adds it to that system's Forces list.
        int GatherForces()
        {
            forceScratch.Clear();
            flowForce = null;
            splineFlowForce = null;
            if (forces != null)
                for (int i = 0; i < forces.Count; i++)
                    AddActiveForceIfMissing(forces[i]);

            // A Surface Flow force needs the mesh grid built for ITS mesh — remember the first one that has a target.
            for (int i = 0; i < forceScratch.Count; i++)
                if (forceScratch[i] is BrazeSurfaceFlow sf && sf.ResolvedSurface != null) { flowForce = sf; break; }

            // A Spline Flow force needs its path sampled to the per-system buffer — remember the first with a spline.
            for (int i = 0; i < forceScratch.Count; i++)
                if (forceScratch[i] is BrazeSplineFlow spf && spf.ResolvedSpline != null) { splineFlowForce = spf; break; }

            int count = forceScratch.Count;
            if (count == 0) return 0;

            if (forceData.Length < count) forceData = new GpuParticleForce[Mathf.NextPowerOfTwo(count)];
            for (int i = 0; i < count; i++) forceData[i] = forceScratch[i].ToGpu(i);

            if (forceBuf == null || forceBuf.count < count)
            {
                forceBuf?.Release();
                forceBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(count), GpuParticleForce.Stride);
            }
            forceBuf.SetData(forceData, 0, 0, count);

            // The falloff atlas is 256 curve evaluations per force + a full CPU→GPU
            // upload. The curves only change on inspector edit, so skip the rebake +
            // upload when neither the curves nor the buffer changed since last frame.
            bool realloc = EnsureFalloffBuf(count);
            int hash = BrazeParticleForce.FalloffAtlasHash(forceScratch, count);
            if (realloc || hash != falloffHash)
            {
                for (int r = 0; r < count; r++)
                {
                    var c = forceScratch[r].falloff;
                    int baseI = r * 256;
                    for (int x = 0; x < 256; x++)
                        falloffData[baseI + x] = c != null ? c.Evaluate(x / 255f) : 0f;
                }
                forceFalloffBuf.SetData(falloffData, 0, 0, count * 256);
                falloffHash = hash;
            }
            return count;
        }

        // Collect the active colliders that target this system and upload them. Mirrors GatherForces
        // (no falloff atlas — collision response is uniform per collider).
        int GatherColliders()
        {
            colliderScratch.Clear();
            meshCollider = null;
            var all = BrazeParticleCollider.Active;
            BrazeRuntimeRegistry.PruneNulls(all);
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || !c.isActiveAndEnabled) continue;
                if (c.shape == BrazeParticleCollider.Shape.Mesh)
                {
                    if (meshCollider == null && c.ResolvedMeshRenderer != null) meshCollider = c;   // first wins
                    continue;                                                                        // not a primitive struct
                }
                colliderScratch.Add(c);
            }
            int count = colliderScratch.Count;
            if (count == 0) return 0;

            if (colliderData.Length < count) colliderData = new GpuParticleCollider[Mathf.NextPowerOfTwo(count)];
            for (int i = 0; i < count; i++) colliderData[i] = colliderScratch[i].ToGpu();

            if (colliderBuf == null || colliderBuf.count < count)
            {
                colliderBuf?.Release();
                colliderBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(count), GpuParticleCollider.Stride);
            }
            colliderBuf.SetData(colliderData, 0, 0, count);
            return count;
        }

        /// <summary>Push world-space spawn points (position / normal / velocity) for THIS frame — e.g. debris
        /// surface points from a Surface Disintegrator's crumbling chunks. Copied internally; takes priority over
        /// the Mesh / Surface-Mapper source and emits through the world-space points path (the emitter still owns
        /// rate, size, colour and lifetime — the caller only supplies where to spawn). Call every frame to keep it
        /// active; emission stops a frame after the pushes stop.</summary>
        public void EmitFromExternalPoints(List<Vector3> pos, List<Vector3> nrm, List<Vector3> vel)
        {
            extEmitPts.Clear(); extEmitNrm.Clear(); extEmitVel.Clear();
            if (pos != null) extEmitPts.AddRange(pos);
            if (nrm != null) extEmitNrm.AddRange(nrm);
            if (vel != null) extEmitVel.AddRange(vel);
            extEmitFrame = TickFrame;
        }

        // ── Mesh-shape emission points ──────────────────────────────────────────────────
        // Fill the mesh spawn source. Mesh renderers use GPU-only extraction/sample buffers; unsupported meshes emit
        // nothing rather than falling back to CPU BakeMesh / mask sampling work.
        int GatherEmitPoints()
        {
            emitUsePassIdx = false;
            emitInputLocal = false;
            emitHasPrevLocal = false;
            emitExtraNormalVelocity = 0f;
            var gate = ActiveMask();

            emitPointScratch.Clear();
            emitNrmScratch.Clear();
            emitVelScratch.Clear();
            if (HasExternalEmit)                                  // pushed debris points win (no rest stream → world gating)
            {
                emitPointScratch.AddRange(extEmitPts);
                emitNrmScratch.AddRange(extEmitNrm);
                emitVelScratch.AddRange(extEmitVel);
                return UploadEmitPoints(emitPointScratch, emitNrmScratch, emitVelScratch);
            }

            return shapeMesh != null && TryGatherGpuMeshEmitPoints(shapeMesh, gate, out int gpuCount) ? gpuCount : 0;
        }

        bool TryGatherGpuMeshEmitPoints(Renderer r, BrazeSurfaceMask gate, out int count,
                                        bool forceSurface = false, int sampleCountOverride = -1)
        {
            count = 0;
            if (r == null) return false;

            if (r is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) return false;
                bool sampleDriven = forceSurface || gate != null || meshEmitSource != MeshEmitSource.Vertices;
                if (sampleDriven)
                    return TryDispatchSkinnedSampleExtract(smr, gate, out count, forceSurface, sampleCountOverride);

                int vcount = DispatchSkinnedExtract(smr);
                if (vcount < 0) return false;
                count = vcount;
                return true;
            }

            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            return TryUseStaticGpuMeshSamples(mf.sharedMesh, r.transform, gate, out count, forceSurface, sampleCountOverride);
        }

        static bool TryResolveShapeMesh(Renderer renderer, out Mesh mesh)
        {
            mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
                return mesh != null;
            }

            if (renderer == null) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            mesh = filter != null ? filter.sharedMesh : null;
            return mesh != null;
        }

        bool TryUseStaticGpuMeshSamples(Mesh mesh, Transform sourceTransform, BrazeSurfaceMask gate, out int count,
                                        bool forceSurface = false, int sampleCountOverride = -1)
        {
            count = 0;
            if (mesh == null || sourceTransform == null) return false;
            if (!EnsureMeshSamples(mesh, sourceTransform, gate, forceSurface, sampleCountOverride)) return false;

            int sampleCount = meshSampleIdx != null ? meshSampleIdx.Length / 3 : 0;
            if (sampleCount <= 0) return false;
            if (!EnsureGpuMeshSampleBuffers(mesh, sampleCount, uploadLocalEmit: true, uploadSampleTable: false))
                return false;

            ConfigureLocalMeshInput(sourceTransform,
                sourceTransform.BrazeInstanceId());

            if (gate != null)
            {
                emitUsePassIdx = true;
                DispatchGpuMaskGate(gate, sourceTransform, emitRestPosBuf, sampleCount);
            }

            count = sampleCount;
            return true;
        }

        bool TryDispatchSkinnedSampleExtract(SkinnedMeshRenderer smr, BrazeSurfaceMask gate, out int count,
                                             bool forceSurface = false, int sampleCountOverride = -1)
        {
            count = 0;
            Mesh mesh = smr != null ? smr.sharedMesh : null;
            if (mesh == null) return false;
            if (!EnsureMeshSamples(mesh, smr.transform, gate, forceSurface, sampleCountOverride)) return false;

            int sampleCount = meshSampleIdx != null ? meshSampleIdx.Length / 3 : 0;
            if (sampleCount <= 0) return false;
            if (!EnsureGpuMeshSampleBuffers(mesh, sampleCount, uploadLocalEmit: false, uploadSampleTable: true))
                return false;

            int meshId = mesh.BrazeInstanceId();
            if (meshId != gpuEmitMeshId)
            {
                gpuEmitMeshId = meshId;
                gpuLayoutOK = ResolveGpuLayout(mesh);
                gpuHasPrev = false;
                gpuRawTargetSet = false;
                emitSkinnedSampleHasPrev = false;
            }
            if (!gpuLayoutOK) return false;

            if (!gpuRawTargetSet)
            {
                smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                smr.updateWhenOffscreen = true;
                gpuRawTargetSet = true;
            }

            EnsureGpuEmitBuffers(sampleCount);
            EnsureSkinnedSamplePrevBuffer(smr.BrazeInstanceId() ^ (meshSampleVersion * 397), sampleCount);

            gpuPendingVB?.Dispose();
            gpuPendingVB = smr.GetVertexBuffer();
            if (gpuPendingVB == null) return false;

            float dt = Mathf.Max(TickDeltaTime, 1e-4f);
            Matrix4x4 l2w = smr.rootBone != null ? smr.rootBone.localToWorldMatrix : smr.transform.localToWorldMatrix;
            cs.SetBuffer(kExtractSkinnedSamples, "_SrcVB", gpuPendingVB);
            cs.SetInt("_SrcStride", gpuSrcStride);
            cs.SetInt("_SrcPosOff", gpuSrcPosOff);
            cs.SetInt("_SrcNrmOff", gpuSrcNrmOff);
            cs.SetInt("_SrcHasNrm", gpuHasNrm ? 1 : 0);
            cs.SetInt("_SrcVertCount", mesh.vertexCount);
            cs.SetMatrix("_SrcL2W", l2w);
            cs.SetMatrix("_SrcN2W", l2w.inverse.transpose);
            cs.SetFloat("_InvDt", 1f / dt);
            cs.SetInt("_SampleCount", sampleCount);
            cs.SetInt("_SampleHasPrev", emitSkinnedSampleHasPrev ? 1 : 0);
            cs.SetBuffer(kExtractSkinnedSamples, "_SampleIdx", emitSampleIdxBuf);
            cs.SetBuffer(kExtractSkinnedSamples, "_SampleWeight", emitSampleWeightBuf);
            cs.SetBuffer(kExtractSkinnedSamples, "_OutPos", emitPointBuf);
            cs.SetBuffer(kExtractSkinnedSamples, "_OutNrm", emitNrmBuf);
            cs.SetBuffer(kExtractSkinnedSamples, "_OutVel", emitVelBuf);
            cs.SetBuffer(kExtractSkinnedSamples, "_PrevSamplePos", emitSamplePrevPosBuf);
            Dispatch1D(kExtractSkinnedSamples, sampleCount);
            emitSkinnedSampleHasPrev = true;

            if (gate != null)
            {
                emitUsePassIdx = true;
                DispatchGpuMaskGate(gate, smr.transform, emitRestPosBuf, sampleCount);
            }

            count = sampleCount;
            return true;
        }

        void ConfigureLocalMeshInput(Transform sourceTransform, int motionKey)
        {
            UpdateLocalMeshMotion(sourceTransform, motionKey);
            emitInputLocal = true;
            emitHasPrevLocal = emitTrackedHasPrev;
            emitNormalLocalToWorld = emitLocalToWorld.inverse.transpose;
        }

        // Record the rigid mesh-source transform every simulation frame, not just on frames that happen to emit.
        // Otherwise the first particle / a one-shot burst has no previous transform and inherits zero velocity.
        // The frame guard lets Gather call this again without collapsing the previous/current pair to the same matrix.
        void UpdateLocalMeshMotion(Transform sourceTransform, int motionKey)
        {
            if (sourceTransform == null) return;
            int frame = TickFrame;
            if (motionKey == emitLocalMotionKey && frame == emitLocalMotionFrame) return;

            Matrix4x4 l2w = sourceTransform.localToWorldMatrix;
            float now = TickTime;
            bool sameSource = motionKey == emitLocalMotionKey;
            bool hasPrevious = sameSource && emitLocalHasPrev;

            emitLocalToWorld = l2w;
            emitPrevLocalToWorld = hasPrevious ? emitLastLocalToWorld : l2w;
            float dt = hasPrevious
                ? Mathf.Max(now - emitLastLocalTime, 1e-4f)
                : Mathf.Max(TickDeltaTime, 1e-4f);
            emitLocalInvDt = 1f / dt;
            emitTrackedHasPrev = hasPrevious;

            emitLastLocalToWorld = l2w;
            emitLastLocalTime = now;
            emitLocalHasPrev = true;
            emitLocalMotionKey = motionKey;
            emitLocalMotionFrame = frame;
        }

        void TrackMeshMotionWhileIdle(BrazePyroEmitter emitterSource, bool requestDriven, bool emitterDriven)
        {
            if (requestDriven) return;

            Transform sourceTransform = null;
            if (emitterDriven && emitterSource != null && emitterSource.shape == BrazePyroEmitter.Shape.Mesh)
            {
                if (emitterSource.sourceSkinnedMesh == null && emitterSource.sourceMesh != null)
                    sourceTransform = emitterSource.sourceMesh.transform;
            }
            else if (!emitterDriven && shape == EmitShape.Mesh)
            {
                if (shapeMesh != null && shapeMesh is not SkinnedMeshRenderer)
                    sourceTransform = shapeMesh.transform;
            }

            // Skinned deformation uses its GPU vertex history. This tracker is only the zero-cost rigid/static path.
            if (sourceTransform != null)
                UpdateLocalMeshMotion(sourceTransform, sourceTransform.BrazeInstanceId());
        }

        bool EnsureGpuMeshSampleBuffers(Mesh mesh, int count, bool uploadLocalEmit, bool uploadSampleTable)
        {
            if (mesh == null || meshSampleIdx == null || meshSampleW == null
                || meshSampleRestPos == null || meshSampleRestNrm == null
                || meshSampleIdx.Length < count * 3 || meshSampleW.Length < count * 3
                || meshSampleRestPos.Length < count || meshSampleRestNrm.Length < count)
                return false;
            int meshId = mesh.BrazeInstanceId();
            bool same = emitGpuSampleMeshId == meshId && emitGpuSampleVersion == meshSampleVersion && emitGpuSampleCount == count;
            bool haveRest = emitRestPosBuf != null && emitRestPosBuf.count >= count;
            bool haveLocal = !uploadLocalEmit || (emitGpuSampleLocalUploaded
                && emitPointBuf != null && emitPointBuf.count >= count && emitNrmBuf != null && emitNrmBuf.count >= count);
            bool haveSamples = !uploadSampleTable || (emitGpuSampleTableUploaded
                && emitSampleIdxBuf != null && emitSampleIdxBuf.count >= count * 3
                && emitSampleWeightBuf != null && emitSampleWeightBuf.count >= count * 3);
            if (same && haveRest && haveLocal && haveSamples) return true;
            if (!same)
            {
                emitGpuSampleLocalUploaded = false;
                emitGpuSampleTableUploaded = false;
            }

            int cap = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
            int tripleCap = Mathf.NextPowerOfTwo(Mathf.Max(3, count * 3));

            if (emitRestPosBuf == null || emitRestPosBuf.count < count)
            {
                emitRestPosBuf?.Release();
                emitRestPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            }
            emitRestPosBuf.SetData(meshSampleRestPos, 0, 0, count);

            if (uploadLocalEmit)
            {
                if (emitPointBuf == null || emitPointBuf.count < count)
                {
                    emitPointBuf?.Release();
                    emitPointBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
                }
                if (emitNrmBuf == null || emitNrmBuf.count < count)
                {
                    emitNrmBuf?.Release();
                    emitNrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
                }
                emitPointBuf.SetData(meshSampleRestPos, 0, 0, count);
                emitNrmBuf.SetData(meshSampleRestNrm, 0, 0, count);
                emitGpuSampleLocalUploaded = true;
            }

            if (uploadSampleTable)
            {
                if (emitSampleIdxBuf == null || emitSampleIdxBuf.count < count * 3)
                {
                    emitSampleIdxBuf?.Release();
                    emitSampleIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tripleCap, sizeof(int));
                }
                if (emitSampleWeightBuf == null || emitSampleWeightBuf.count < count * 3)
                {
                    emitSampleWeightBuf?.Release();
                    emitSampleWeightBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tripleCap, sizeof(float));
                }
                emitSampleIdxBuf.SetData(meshSampleIdx, 0, 0, count * 3);
                emitSampleWeightBuf.SetData(meshSampleW, 0, 0, count * 3);
                emitGpuSampleTableUploaded = true;
            }

            emitGpuSampleMeshId = meshId;
            emitGpuSampleVersion = meshSampleVersion;
            emitGpuSampleCount = count;
            return true;
        }

        void EnsureSkinnedSamplePrevBuffer(int key, int count)
        {
            if (emitSamplePrevPosBuf != null && emitSamplePrevPosBuf.count >= count
                && emitSkinnedSamplePrevKey == key && emitSkinnedSamplePrevCount == count)
                return;
            emitSamplePrevPosBuf?.Release();
            emitSamplePrevPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                Mathf.NextPowerOfTwo(Mathf.Max(1, count)), sizeof(float) * 3);
            emitSkinnedSamplePrevKey = key;
            emitSkinnedSamplePrevCount = count;
            emitSkinnedSampleHasPrev = false;
        }

        GraphicsBuffer gpuPendingVB;   // last frame's GetVertexBuffer() handle — disposed at the start of the next use

        // Read the skinned renderer's already-deformed GPU vertex buffer straight into the emit point/normal/velocity
        // buffers via the ExtractSkinnedEmit kernel — no CPU bake, no readback, no upload. Returns the vertex count,
        // or -1 if the GPU path is unavailable (unsupported vertex layout / no buffer) so the caller falls back to CPU.
        int DispatchSkinnedExtract(SkinnedMeshRenderer smr)
        {
            Mesh mesh = smr.sharedMesh;
            if (mesh == null) return -1;

            int meshId = mesh.BrazeInstanceId();
            if (meshId != gpuEmitMeshId)
            {
                gpuEmitMeshId = meshId;
                gpuLayoutOK = ResolveGpuLayout(mesh);
                gpuHasPrev = false;
                gpuRawTargetSet = false;
            }
            if (!gpuLayoutOK) return -1;

            if (!gpuRawTargetSet)                                  // expose the skinned buffer as a raw GPU buffer (once)
            {
                smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                smr.updateWhenOffscreen = true;                   // keep skinning when culled, else the buffer goes stale
                gpuRawTargetSet = true;
            }

            int vcount = mesh.vertexCount;
            EnsureGpuEmitBuffers(vcount);

            gpuPendingVB?.Dispose();                              // release last frame's handle, then grab this frame's
            gpuPendingVB = smr.GetVertexBuffer();
            if (gpuPendingVB == null) return -1;

            float dt = Mathf.Max(TickDeltaTime, 1e-4f);
            // Unity's GPU-skinned vertex buffer is in ROOT-BONE space (not the renderer's transform space) — using the
            // renderer transform here causes a positional offset. Match BrazeSurfaceDisintegrator.DeformSourceToWorld.
            Matrix4x4 l2w = smr.rootBone != null ? smr.rootBone.localToWorldMatrix : smr.transform.localToWorldMatrix;
            cs.SetBuffer(kExtractSkinnedEmit, "_SrcVB", gpuPendingVB);
            cs.SetInt("_SrcStride", gpuSrcStride);
            cs.SetInt("_SrcPosOff", gpuSrcPosOff);
            cs.SetInt("_SrcNrmOff", gpuSrcNrmOff);
            cs.SetInt("_SrcHasNrm", gpuHasNrm ? 1 : 0);
            cs.SetInt("_SrcVertCount", vcount);
            cs.SetMatrix("_SrcL2W", l2w);
            cs.SetMatrix("_SrcN2W", l2w.inverse.transpose);
            cs.SetFloat("_InvDt", 1f / dt);
            cs.SetInt("_SrcHasPrev", gpuHasPrev ? 1 : 0);
            cs.SetBuffer(kExtractSkinnedEmit, "_OutPos", emitPointBuf);
            cs.SetBuffer(kExtractSkinnedEmit, "_OutNrm", emitNrmBuf);
            cs.SetBuffer(kExtractSkinnedEmit, "_OutVel", emitVelBuf);
            cs.SetBuffer(kExtractSkinnedEmit, "_PrevPos", emitPrevPosBuf);
            Dispatch1D(kExtractSkinnedEmit, vcount);
            gpuHasPrev = true;
            return vcount;
        }

        // Resolve the source vertex layout (stride / position+normal byte offsets). False if positions aren't Float32x3
        // (the only format the extract kernel reads) → caller uses the CPU path. Mirrors BrazeSurfaceDisintegrator.
        bool ResolveGpuLayout(Mesh mesh)
        {
            int posStream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            if (posStream < 0 || mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                return false;
            gpuSrcStride = mesh.GetVertexBufferStride(posStream);
            gpuSrcPosOff = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            gpuHasNrm = mesh.HasVertexAttribute(VertexAttribute.Normal)
                     && mesh.GetVertexAttributeStream(VertexAttribute.Normal) == posStream
                     && mesh.GetVertexAttributeFormat(VertexAttribute.Normal) == VertexAttributeFormat.Float32;
            gpuSrcNrmOff = gpuHasNrm ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : 0;
            return true;
        }

        // (Re)allocate the four GPU emit buffers to hold vcount verts. Reset the prev-pos history on any realloc so the
        // first frame after a resize emits zero velocity (no garbage diff against an uninitialized buffer).
        void EnsureGpuEmitBuffers(int vcount)
        {
            if (emitPointBuf != null && emitPointBuf.count >= vcount
                && emitNrmBuf != null && emitNrmBuf.count >= vcount
                && emitVelBuf != null && emitVelBuf.count >= vcount
                && emitPrevPosBuf != null && emitPrevPosBuf.count >= vcount)
            {
                emitGpuSampleLocalUploaded = false;
                return;
            }
            int cap = Mathf.NextPowerOfTwo(Mathf.Max(1, vcount));
            emitPointBuf?.Release(); emitNrmBuf?.Release(); emitVelBuf?.Release(); emitPrevPosBuf?.Release();
            emitPointBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            emitNrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            emitVelBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            emitPrevPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            emitGpuSampleLocalUploaded = false;
            gpuHasPrev = false;
        }

        // Evaluate the mask on the GPU: the MaskGate kernel transforms each REST point into Braze mask space, runs the
        // shared field, and compacts passing indices into emitPassIdxBuf. Emit then samples spawns only from those.
        void DispatchGpuMaskGate(BrazeSurfaceMask gate, Transform sourceTransform, GraphicsBuffer restPos, int count)
        {
            if (gate == null || sourceTransform == null || restPos == null || count <= 0) return;
            int fieldVersion = gate.FieldVersion;
            int spaceVersion = gate.SourceLocalToFieldVersion(sourceTransform);
            int sourceId = sourceTransform.BrazeInstanceId();
            if (emitPassCountBuf != null
                && gate == emitGateCachedMask
                && restPos == emitGateCachedRest
                && count == emitGateCachedCount
                && sourceId == emitGateCachedSourceId
                && fieldVersion == emitGateCachedFieldVersion
                && spaceVersion == emitGateCachedSpaceVersion
                && meshSampleVersion == emitGateCachedSampleVersion)
                return;

            EnsurePassBuffers(count);
            cs.SetBuffer(kClearReqCount, "_ReqCount", emitPassCountBuf); // GPU-clear the atomic counter before appending
            cs.Dispatch(kClearReqCount, 1, 1, 1);
            gate.BindField(cs, kMaskGate);                              // the shared field uniforms (seeds + filters)
            cs.SetMatrix("_RestLocalToField", gate.SourceLocalToFieldMatrix(sourceTransform));
            cs.SetInt("_RestCount", count);
            cs.SetBuffer(kMaskGate, "_RestPos", restPos);
            cs.SetBuffer(kMaskGate, "_GatePassIdx", emitPassIdxBuf);
            cs.SetBuffer(kMaskGate, "_PassCount", emitPassCountBuf);
            Dispatch1D(kMaskGate, count);

            emitGateCachedMask = gate;
            emitGateCachedRest = restPos;
            emitGateCachedCount = count;
            emitGateCachedSourceId = sourceId;
            emitGateCachedFieldVersion = fieldVersion;
            emitGateCachedSpaceVersion = spaceVersion;
            emitGateCachedSampleVersion = meshSampleVersion;
        }

        // (Re)allocate the pass-index buffer (vertex-sized) + the 1-uint pass-count buffer.
        void EnsurePassBuffers(int vcount)
        {
            if (emitPassCountBuf == null) emitPassCountBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            if (emitPassIdxBuf == null || emitPassIdxCapacity < vcount)
            {
                emitPassIdxBuf?.Release();
                emitPassIdxCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, vcount));
                emitPassIdxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, emitPassIdxCapacity, sizeof(uint));
            }
        }

        GraphicsBuffer EmitPassIdxDummy() =>
            emitPassIdxDummy ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

        // The mask gating spawns, or null. Explicit reference wins; else auto-find one on the shape mesh's object
        // (the mask typically lives on the surface we spawn from). Cached by mesh id.
        BrazeSurfaceMask resolvedAutoMask;
        int maskSourceId = int.MinValue;
        BrazeSurfaceMask ActiveMask()
        {
            if (!useMask) return null;
            if (mask != null) return mask;
            int id = shapeMesh != null ? shapeMesh.BrazeInstanceId() : 0;
            if (id != maskSourceId) { maskSourceId = id; resolvedAutoMask = shapeMesh != null ? shapeMesh.GetComponent<BrazeSurfaceMask>() : null; }
            return resolvedAutoMask;
        }

        // (Re)build the cached face/edge samples for the current mesh + source + count. Imported meshes are read via
        // MeshUtility in the Editor even when Read/Write is disabled; only the compact sample pool is serialized for
        // player use. Area-weighted for Surface and length-weighted for Edges, so density remains uniform.
        bool EnsureMeshSamples(Mesh m, Transform sourceTransform, BrazeSurfaceMask gate,
                               bool forceSurface = false, int countOverride = -1)
        {
            MeshEmitSource sampleSource = (gate != null || forceSurface) ? MeshEmitSource.Surface : meshEmitSource;
            int count = countOverride > 0
                ? Mathf.Max(1, countOverride)
                : (gate != null ? gate.ResolveSurfaceSampleCount(m, sourceTransform)
                    : (sampleSource == MeshEmitSource.Vertices ? m.vertexCount : Mathf.Max(16, meshScatterCount)));
            if (meshSampleMeshCached == m
                && m.vertexCount == meshSampleVertexCountCached
                && count == meshSampleCountCached
                && sampleSource == meshSampleSourceCached
                && meshSampleIdx != null && meshSampleIdx.Length == count * 3
                && meshSampleW != null && meshSampleW.Length == count * 3
                && meshSampleRestPos != null && meshSampleRestPos.Length == count
                && meshSampleRestNrm != null && meshSampleRestNrm.Length == count)
                return true;

            if (!TryReadMeshSpawnSource(m, emitRestVerts, emitBakeNrm, meshTriScratch)) return false;
            int triCount = meshTriScratch.Count / 3;
            if (sampleSource != MeshEmitSource.Vertices && triCount == 0) return false;

            // Deterministic, stable across frames so the scatter pattern doesn't shimmer when re-evaluated.
            var rng = new System.Random(m.BrazeInstanceId() ^ (count * 73856093) ^ ((int)sampleSource * 19349663));
            meshSampleIdx = new int[3 * count];
            meshSampleW = new float[3 * count];

            if (sampleSource == MeshEmitSource.Vertices)
            {
                int vcount = emitRestVerts.Count;
                if (vcount <= 0) return false;
                for (int k = 0; k < count; k++)
                {
                    int v = k % vcount;
                    meshSampleIdx[3 * k] = v; meshSampleIdx[3 * k + 1] = v; meshSampleIdx[3 * k + 2] = v;
                    meshSampleW[3 * k] = 1f; meshSampleW[3 * k + 1] = 0f; meshSampleW[3 * k + 2] = 0f;
                }
            }
            else if (sampleSource == MeshEmitSource.Edges)
            {
                // Unique edges, length-weighted (so the scatter is even along the wireframe, not biased to short edges).
                var seen = new HashSet<long>();
                var ea = new List<int>(); var eb = new List<int>();
                var cum = new List<float>(); float total = 0f;
                for (int t = 0; t < triCount; t++)
                {
                    int a = meshTriScratch[3 * t], b = meshTriScratch[3 * t + 1], c = meshTriScratch[3 * t + 2];
                    AddEdge(a, b, seen, ea, eb, cum, ref total, emitRestVerts);
                    AddEdge(b, c, seen, ea, eb, cum, ref total, emitRestVerts);
                    AddEdge(c, a, seen, ea, eb, cum, ref total, emitRestVerts);
                }
                if (ea.Count == 0 || total <= 0f) return false;
                for (int k = 0; k < count; k++)
                {
                    int e = UpperBound(cum, (float)rng.NextDouble() * total);
                    float t = (float)rng.NextDouble();
                    meshSampleIdx[3 * k] = ea[e]; meshSampleIdx[3 * k + 1] = eb[e]; meshSampleIdx[3 * k + 2] = ea[e];
                    meshSampleW[3 * k] = 1f - t; meshSampleW[3 * k + 1] = t; meshSampleW[3 * k + 2] = 0f;
                }
            }
            else   // Surface — area-weighted barycentric
            {
                var cum = new List<float>(triCount); float total = 0f;
                for (int t = 0; t < triCount; t++)
                {
                    Vector3 v0 = emitRestVerts[meshTriScratch[3 * t]];
                    Vector3 v1 = emitRestVerts[meshTriScratch[3 * t + 1]];
                    Vector3 v2 = emitRestVerts[meshTriScratch[3 * t + 2]];
                    total += 0.5f * Vector3.Cross(v1 - v0, v2 - v0).magnitude;
                    cum.Add(total);
                }
                if (total <= 0f) return false;
                for (int k = 0; k < count; k++)
                {
                    int t = UpperBound(cum, (float)rng.NextDouble() * total);
                    // Uniform point in a triangle (Osada's sqrt method).
                    float su = Mathf.Sqrt((float)rng.NextDouble()), r2 = (float)rng.NextDouble();
                    float w0 = 1f - su, w1 = su * (1f - r2), w2 = su * r2;
                    meshSampleIdx[3 * k] = meshTriScratch[3 * t];
                    meshSampleIdx[3 * k + 1] = meshTriScratch[3 * t + 1];
                    meshSampleIdx[3 * k + 2] = meshTriScratch[3 * t + 2];
                    meshSampleW[3 * k] = w0; meshSampleW[3 * k + 1] = w1; meshSampleW[3 * k + 2] = w2;
                }
            }

            // Cache the evaluated rest stream too. Static meshes upload it directly; skinned meshes use it only for
            // mask gating while the current deformed positions continue to come from the renderer's GPU buffer.
            meshSampleRestPos = new Vector3[count];
            meshSampleRestNrm = new Vector3[count];
            bool hasNormals = emitBakeNrm.Count == emitRestVerts.Count;
            int vertexCount = emitRestVerts.Count;
            for (int k = 0; k < count; k++)
            {
                int b = k * 3;
                int i0 = meshSampleIdx[b], i1 = meshSampleIdx[b + 1], i2 = meshSampleIdx[b + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                    return false;

                float w0 = meshSampleW[b], w1 = meshSampleW[b + 1], w2 = meshSampleW[b + 2];
                meshSampleRestPos[k] = emitRestVerts[i0] * w0 + emitRestVerts[i1] * w1 + emitRestVerts[i2] * w2;
                Vector3 n = hasNormals
                    ? (emitBakeNrm[i0] * w0 + emitBakeNrm[i1] * w1 + emitBakeNrm[i2] * w2).normalized
                    : Vector3.Cross(emitRestVerts[i1] - emitRestVerts[i0], emitRestVerts[i2] - emitRestVerts[i0]).normalized;
                meshSampleRestNrm[k] = n.sqrMagnitude > 1e-12f ? n : Vector3.up;
            }

            meshSampleMeshCached = m;
            meshSampleVertexCountCached = m.vertexCount;
            meshSampleCountCached = count;
            meshSampleSourceCached = sampleSource;
            meshSampleVersion++;

            // These lists can temporarily mirror a large source mesh. The compact arrays above are the only data
            // needed after baking, so release the full-mesh scratch capacity instead of carrying it during playback.
            emitRestVerts.Clear(); emitRestVerts.TrimExcess();
            emitBakeNrm.Clear(); emitBakeNrm.TrimExcess();
            meshTriScratch.Clear(); meshTriScratch.TrimExcess();
            return true;
        }

        static bool TryReadMeshSpawnSource(Mesh mesh, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            vertices.Clear();
            normals.Clear();
            triangles.Clear();
            if (mesh == null) return false;

            if (mesh.isReadable)
            {
                mesh.GetVertices(vertices);
                mesh.GetNormals(normals);
                var submeshIndices = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                    mesh.GetTriangles(submeshIndices, s);
                    triangles.AddRange(submeshIndices);
                }
                return vertices.Count == mesh.vertexCount;
            }

#if UNITY_EDITOR
            // MeshUtility is deliberately Editor-only: unlike Mesh.AcquireReadOnlyMeshData it skips the isReadable
            // check. The resulting data is immediately reduced to the compact serialized sample pool above.
            using Mesh.MeshDataArray dataArray = UnityEditor.MeshUtility.AcquireReadOnlyMeshData(mesh);
            if (dataArray.Length == 0) return false;
            Mesh.MeshData data = dataArray[0];
            using (var vertexData = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp,
                       NativeArrayOptions.UninitializedMemory))
            {
                data.GetVertices(vertexData);
                for (int i = 0; i < vertexData.Length; i++) vertices.Add(vertexData[i]);
            }

            if (mesh.HasVertexAttribute(VertexAttribute.Normal))
            {
                using var normalData = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);
                data.GetNormals(normalData);
                for (int i = 0; i < normalData.Length; i++) normals.Add(normalData[i]);
            }

            if (data.indexFormat == IndexFormat.UInt16)
            {
                NativeArray<ushort> indices = data.GetIndexData<ushort>();
                for (int s = 0; s < data.subMeshCount; s++)
                {
                    SubMeshDescriptor sub = data.GetSubMesh(s);
                    if (sub.topology != MeshTopology.Triangles) continue;
                    int end = sub.indexStart + sub.indexCount;
                    for (int i = sub.indexStart; i < end; i++) triangles.Add(indices[i] + sub.baseVertex);
                }
            }
            else
            {
                NativeArray<uint> indices = data.GetIndexData<uint>();
                for (int s = 0; s < data.subMeshCount; s++)
                {
                    SubMeshDescriptor sub = data.GetSubMesh(s);
                    if (sub.topology != MeshTopology.Triangles) continue;
                    int end = sub.indexStart + sub.indexCount;
                    for (int i = sub.indexStart; i < end; i++) triangles.Add((int)indices[i] + sub.baseVertex);
                }
            }
            return vertices.Count == data.vertexCount;
#else
            return false;
#endif
        }

        static void AddEdge(int a, int b, HashSet<long> seen, List<int> ea, List<int> eb, List<float> cum, ref float total, List<Vector3> rest)
        {
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            long key = ((long)lo << 32) | (uint)hi;
            if (!seen.Add(key)) return;
            ea.Add(a); eb.Add(b);
            total += Vector3.Distance(rest[a], rest[b]);   // length-weighted CDF
            cum.Add(total);
        }

        // Upper-bound index into a cumulative (monotonic) list: smallest i with cum[i] >= v.
        static int UpperBound(List<float> cum, float v)
        {
            int lo = 0, hi = cum.Count - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < v) lo = mid + 1; else hi = mid; }
            return lo;
        }

        int UploadEmitPoints(List<Vector3> pts, List<Vector3> nrm, List<Vector3> vel)
        {
            int n = pts.Count;
            if (n == 0) return 0;
            int cap = Mathf.NextPowerOfTwo(n);
            if (emitPointBuf == null || emitPointBuf.count < n)
            {
                emitPointBuf?.Release(); emitNrmBuf?.Release(); emitVelBuf?.Release();
                emitPointBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
                emitNrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
                emitVelBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            }
            if (emitPointArray == null || emitPointArray.Length < n)
            {
                emitPointArray = new Vector3[cap];
                emitNrmArray = new Vector3[cap];
                emitVelArray = new Vector3[cap];
            }
            pts.CopyTo(emitPointArray);
            nrm.CopyTo(emitNrmArray);
            vel.CopyTo(emitVelArray);
            emitPointBuf.SetData(emitPointArray, 0, 0, n);
            emitNrmBuf.SetData(emitNrmArray, 0, 0, n);
            emitVelBuf.SetData(emitVelArray, 0, 0, n);
            emitGpuSampleLocalUploaded = false;
            return n;
        }

        GraphicsBuffer EmitPointDummy() =>
            emitPointDummy ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);

        const int ClonerComputeShapeKind = 12; // 9..11 are reserved for BrazePyroEmitter-only shapes.

        internal static int ComputeShapeKind(EmitShape emitShape) =>
            emitShape == EmitShape.Cloner ? ClonerComputeShapeKind : (int)emitShape;

        GraphicsBuffer EmitClonerDummy() =>
            emitClonerDummy ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 4);

        bool TryPrepareClonerEmit(out GraphicsBuffer basePosition, out GraphicsBuffer positionOffset,
            out GraphicsBuffer baseRotation, out GraphicsBuffer rotationOffset, out int instanceCount)
        {
            basePosition = null;
            positionOffset = null;
            baseRotation = null;
            rotationOffset = null;
            instanceCount = 0;

            if (shapeCloner == null || !shapeCloner.TryGetGpuTransforms(
                    out basePosition, out positionOffset, out baseRotation, out rotationOffset, out instanceCount) ||
                instanceCount <= 0)
                return false;

            int sourceId = shapeCloner.BrazeInstanceId();
            if (emitClonerPrevPosition == null || emitClonerCount != instanceCount ||
                emitClonerSourceId != sourceId)
            {
                emitClonerPrevPosition?.Release();
                emitClonerPrevPosition = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, instanceCount, sizeof(float) * 4);
                emitClonerSourceId = sourceId;
                emitClonerCount = instanceCount;
                emitClonerHasPrevious = false;
            }

            return true;
        }

        void BindClonerEmit(int kernel, bool ready, GraphicsBuffer basePosition, GraphicsBuffer positionOffset,
            GraphicsBuffer baseRotation, GraphicsBuffer rotationOffset, int instanceCount, float deltaTime)
        {
            var dummy = EmitClonerDummy();
            cs.SetInt("_EmitClonerCount", ready ? instanceCount : 0);
            cs.SetInt("_EmitClonerHasPrevious", ready && emitClonerHasPrevious ? 1 : 0);
            cs.SetFloat("_EmitClonerInvDt", 1f / Mathf.Max(deltaTime, 1e-4f));
            cs.SetBuffer(kernel, "_EmitClonerBasePosition", ready ? basePosition : dummy);
            cs.SetBuffer(kernel, "_EmitClonerPositionOffset", ready ? positionOffset : dummy);
            cs.SetBuffer(kernel, "_EmitClonerBaseRotation", ready ? baseRotation : dummy);
            cs.SetBuffer(kernel, "_EmitClonerRotationOffset", ready ? rotationOffset : dummy);
            cs.SetBuffer(kernel, "_EmitClonerPreviousPosition",
                ready && emitClonerPrevPosition != null ? emitClonerPrevPosition : dummy);
        }

        void CaptureClonerEmit(GraphicsBuffer basePosition, GraphicsBuffer positionOffset, int instanceCount)
        {
            if (instanceCount <= 0 || emitClonerPrevPosition == null) return;
            cs.SetInt("_EmitClonerCount", instanceCount);
            cs.SetBuffer(kCaptureClonerEmit, "_EmitClonerBasePosition", basePosition);
            cs.SetBuffer(kCaptureClonerEmit, "_EmitClonerPositionOffset", positionOffset);
            cs.SetBuffer(kCaptureClonerEmit, "_EmitClonerPreviousPosition", emitClonerPrevPosition);
            Dispatch1D(kCaptureClonerEmit, instanceCount);
            emitClonerHasPrevious = true;
        }

        // Build the deforming triangle grid from the mesh collider's renderer (extracted via BrazeDeformSource),
        // ready for the Simulate kernel's per-particle nearest-surface query. Returns false until it's valid.
        // Build the shared mesh grid for renderer `r` with query reach `range` (deformed verts → hashed triangle
        // grid, rebuilt each frame). Used by mesh collision AND the Surface Flow force (one grid per system).
        bool PrepareMeshGrid(Renderer r, float range)
        {
            if (r == null) return false;
            if (gridCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeMeshGrid");
                if (asset == null) { Debug.LogError("[BrazeFX] Mesh grid needs 'BrazeMeshGrid' compute in Resources."); return false; }
                gridCS = Instantiate(asset);
            }
            if (meshSource == null || meshSourceRenderer != r)
            {
                meshSource?.Dispose();
                meshSource = new BrazeDeformSource(r);
                meshSourceRenderer = r;
                meshGrid ??= new BrazeMeshGrid(gridCS, 32);
                gridStaticMesh = null;
            }
            if (!meshSource.Valid) return false;

            // Static fast path: same non-skinned mesh, same transform, same reach → the grid is already correct.
            bool skinned = r is SkinnedMeshRenderer;
            Mesh staticMesh = null;
            if (!skinned) { var mf = r.GetComponent<MeshFilter>(); staticMesh = mf != null ? mf.sharedMesh : null; }
            Matrix4x4 l2w = r.transform.localToWorldMatrix;
            if (staticMesh != null && gridStaticMesh == staticMesh && meshGrid.Ready
                && gridStaticMatrix == l2w && Mathf.Approximately(gridStaticRange, range))
                return true;

            meshSource.Refresh();
            if (!meshSource.ExtractedOnce || meshSource.TriangleCount <= 0) return false;
            float cell = BrazeMeshGrid.SuggestCellSize(r.bounds, meshSource.TriangleCount);
            meshGrid.Build(meshSource.Positions, meshSource.Normals, meshSource.Indices, meshSource.TriangleCount, cell, range);
            if (meshGrid.Ready && staticMesh != null)
            {
                gridStaticMesh = staticMesh; gridStaticMatrix = l2w; gridStaticRange = range;
            }
            else gridStaticMesh = null;
            return meshGrid.Ready;
        }

        // Grid query reach: cover the largest particle contact radius, with headroom for fast / behind hits.
        float MeshContactRange()
        {
            float maxPr = Mathf.Max(0f, startSize.SampleRange(0f).y) * collisionRadiusScale;
            return Mathf.Max(maxPr * 2f, 1e-3f);
        }

        // Bind 1-element dummies to the grid slots when mesh collision is off (the kernel references them).
        void BindDummyGrid(int kernel)
        {
            if (dGridPos == null)
            {
                dGridPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                dGridNrm = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                dGridIdx = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
                dGridCellCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                dGridCellItems = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                dGridPos.SetData(new[] { Vector3.zero });
                dGridNrm.SetData(new[] { Vector3.up });
                dGridIdx.SetData(new uint[] { 0, 0, 0 });
                dGridCellCount.SetData(ZeroU);
                dGridCellItems.SetData(ZeroU);
            }
            cs.SetBuffer(kernel, "_GridPos", dGridPos);
            cs.SetBuffer(kernel, "_GridNrm", dGridNrm);
            cs.SetBuffer(kernel, "_GridIdx", dGridIdx);
            cs.SetBuffer(kernel, "_GridCellCount", dGridCellCount);
            cs.SetBuffer(kernel, "_GridCellItems", dGridCellItems);
            cs.SetInt("_GridTriCount", 0);
            cs.SetInt("_GridTableSize", 1);
            cs.SetInt("_GridBucketCap", 1);
            cs.SetFloat("_GridCellSize", 1f);
            cs.SetInt("_GridSearchCells", 0);
            cs.SetInt("_GridRowThreads", 64);
        }

        // Sample the Spline Flow path (world positions + unit tangents) to the per-system buffers and bind them,
        // or bind 1-element dummies when no Spline Flow force is present (the Simulate kernel references the slots).
        void BindSplinePath(int kernel)
        {
            BrazeSpline sp = splineFlowForce != null ? splineFlowForce.ResolvedSpline : null;
            int n = sp != null ? SampleSplinePath(sp) : 0;
            if (n >= 2)
            {
                cs.SetBuffer(kernel, "_SplinePos", splinePosBuf);
                cs.SetBuffer(kernel, "_SplineTan", splineTanBuf);
                cs.SetInt("_SplineCount", n);
                return;
            }
            BindDummySpline(kernel);
        }

        // Resample the spline to a bounded set of world-space points + finite-difference tangents. Returns the
        // sample count (0 if unusable). Cheap: a few hundred Evaluate calls only when a Spline Flow force is live.
        int SampleSplinePath(BrazeSpline sp)
        {
            if (sp.points == null || sp.points.Count < 2) return 0;
            int n = Mathf.Clamp(sp.SampleCount, 2, MaxSplineSamples);
            if (splinePosArr == null || splinePosArr.Length < n)
            {
                splinePosArr = new Vector3[n];
                splineTanArr = new Vector3[n];
            }
            for (int i = 0; i < n; i++) splinePosArr[i] = sp.EvaluateWorld(i / (float)(n - 1));
            for (int i = 0; i < n; i++)
            {
                Vector3 a = splinePosArr[Mathf.Max(i - 1, 0)];
                Vector3 b = splinePosArr[Mathf.Min(i + 1, n - 1)];
                Vector3 t = b - a;
                splineTanArr[i] = t.sqrMagnitude > 1e-10f ? t.normalized : Vector3.up;
            }
            if (splinePosBuf == null || splinePosBuf.count < n)
            {
                splinePosBuf?.Release(); splineTanBuf?.Release();
                int cap = Mathf.NextPowerOfTwo(n);
                splinePosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
                splineTanBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 3);
            }
            splinePosBuf.SetData(splinePosArr, 0, 0, n);
            splineTanBuf.SetData(splineTanArr, 0, 0, n);
            return n;
        }

        void BindDummySpline(int kernel)
        {
            if (dSplinePos == null)
            {
                dSplinePos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                dSplineTan = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                dSplinePos.SetData(new[] { Vector3.zero });
                dSplineTan.SetData(new[] { Vector3.up });
            }
            cs.SetBuffer(kernel, "_SplinePos", dSplinePos);
            cs.SetBuffer(kernel, "_SplineTan", dSplineTan);
            cs.SetInt("_SplineCount", 0);
        }

        // Bind a velocity field so the Simulate kernel can advect particles along the fluid. An explicitly connected
        // VDB wins ONLY for this lifetime-follow binding; the Pyro master (when present) still owns its existing
        // spawn gate/shape/motion path, and an otherwise standalone particle system keeps all of its own emission.
        // Free: the particle kernel samples fields the playback/live solver already produced.
        void BindFluidFollow(BrazePyroEmitter pyroSource, bool local, float dt)
        {
            BrazeContainer container;
            float follow;
            float gain;
            float followRand;
            float gainRand;

            if (BrazeVDBImport.TryGetParticleFluidSource(this, out BrazeVDBImport vdbSource))
            {
                // Directly use THIS player's hidden cache host. Do not call ResolveFor: that could pick a nearby live
                // Pyro container and make an explicit VDB target silently follow the wrong volume while loading.
                container = vdbSource.RenderHost;
                follow = Mathf.Clamp01(vdbSource.particleFluidFollow);
                gain = Mathf.Max(0f, vdbSource.particleFluidFollowGain);
                followRand = Mathf.Clamp01(vdbSource.particleFluidFollowRand);
                gainRand = Mathf.Clamp01(vdbSource.particleFluidFollowGainRand);
            }
            else
            {
                follow = pyroSource != null ? Mathf.Clamp01(pyroSource.particleFluidFollow) : 0f;
                container = follow > 0f ? BrazeContainer.ResolveFor(pyroSource.transform.position) : null;
                gain = pyroSource != null ? Mathf.Max(0f, pyroSource.particleFluidFollowGain) : 1f;
                followRand = pyroSource != null ? Mathf.Clamp01(pyroSource.particleFluidFollowRand) : 0f;
                gainRand = pyroSource != null ? Mathf.Clamp01(pyroSource.particleFluidFollowGainRand) : 0f;
            }

            if (follow <= 0f || container == null || !container.FluidReady)
            {
                cs.SetInt("_FluidFollowEnable", 0);
                cs.SetTexture(kSimulate, "_FluidVel", DummyFluidVel());
                cs.SetTexture(kSimulate, "_FluidOcc", DummyFluidOcc());
                return;
            }

            // dt-corrected blend: at 60 fps, k == follow (so follow = 1 is a perfect tracer); frame-rate independent.
            float k = 1f - Mathf.Pow(1f - follow, Mathf.Max(dt, 0f) * 60f);
            cs.SetInt("_FluidFollowEnable", 1);
            cs.SetFloat("_FluidFollowK", k);
            cs.SetFloat("_FluidFollowGain", gain);
            cs.SetFloat("_FluidFollowRand", followRand);
            cs.SetFloat("_FluidFollowGainRand", gainRand);

            // Simulate stores Local-space particles in local coordinates and velocities. Compose the field transforms
            // rather than forcing World space, so connecting a VDB never changes the particle system's authored mode.
            Matrix4x4 simToWorld = local ? transform.localToWorldMatrix : Matrix4x4.identity;
            Matrix4x4 worldToSim = local ? transform.worldToLocalMatrix : Matrix4x4.identity;
            cs.SetMatrix("_FluidWorldToUVW", container.WorldToFluidUVW() * simToWorld);
            cs.SetMatrix("_FluidVoxelToWorldVel", worldToSim * container.FluidVoxelToWorldVelocity());
            cs.SetTexture(kSimulate, "_FluidVel", container.FluidVelocity);
            cs.SetTexture(kSimulate, "_FluidOcc", container.FluidOccupancy);
        }

        Texture3D DummyFluidVel()
        {
            if (dummyFluidVel == null)
            {
                dummyFluidVel = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false) { hideFlags = HideFlags.DontSave };
                dummyFluidVel.SetPixels(new[] { Color.clear }); dummyFluidVel.Apply(false, false);
            }
            return dummyFluidVel;
        }

        Texture3D DummyFluidOcc()
        {
            if (dummyFluidOcc == null)
            {
                dummyFluidOcc = new Texture3D(1, 1, 1, TextureFormat.RFloat, false) { hideFlags = HideFlags.DontSave };
                dummyFluidOcc.SetPixels(new[] { Color.clear }); dummyFluidOcc.Apply(false, false);
            }
            return dummyFluidOcc;
        }

        void BindEmitterMotion(BrazePyroEmitter source, bool local)
        {
            if (source == null || source.particleMotionScale <= 0f)
            {
                cs.SetInt("_EmitterMotionEnable", 0);
                cs.SetVector("_EmitterMotionVelocity", Vector3.zero);
                cs.SetVector("_EmitterMotionCenter", Vector3.zero);
                cs.SetFloat("_EmitterMotionRadial", 0f);
                return;
            }

            float scale = source.particleMotionScale;
            Vector3 worldVelocity =
                source.transform.forward * source.motionForward +
                Vector3.up * source.motionBuoyancy;
            // Mesh fluid sources have no analytic volume for Radial/Pressure (the Pyro Emitter inspector hides
            // those controls in Mesh mode), so connected particles must not inherit stale/default values either.
            float radialVelocity = source.shape == BrazePyroEmitter.Shape.Mesh
                ? 0f : (source.motionRadial + source.gasPressureBurst) * scale;

            Vector3 velocity = worldVelocity * scale;
            Vector3 center = source.transform.position;
            if (local)
            {
                velocity = transform.InverseTransformVector(velocity);
                center = transform.InverseTransformPoint(center);
            }

            cs.SetInt("_EmitterMotionEnable", 1);
            cs.SetVector("_EmitterMotionVelocity", velocity);
            cs.SetVector("_EmitterMotionCenter", center);
            cs.SetFloat("_EmitterMotionRadial", radialVelocity);
        }

        void BindEmitterShape(BrazePyroEmitter source, int meshPointCount)
        {
            // Mesh master: spawn from its surface samples via the world-space points path (kind 8). Use the target
            // particle system's signed source-motion response, just like its standalone Mesh / Cloner path.
            bool meshMaster = source.shape == BrazePyroEmitter.Shape.Mesh && meshPointCount > 0;

            // Match the fluid footprint: the shape follows the emitter's rotation, and the long shapes / torus are
            // reoriented by the Shape Axis (Box bakes its Transform scale in too — its size IS the scale).
            Matrix4x4 emitToWorld = source.shape == BrazePyroEmitter.Shape.Box
                ? source.transform.localToWorldMatrix
                : Matrix4x4.TRS(source.transform.position, source.transform.rotation * source.AxisRotation(), Vector3.one);

            cs.SetMatrix("_EmitToWorld", emitToWorld);
            cs.SetInt("_ShapeKind", meshMaster ? 8 : ParticleShapeFromEmitter(source.shape));
            cs.SetFloat("_ShapeRadius", Mathf.Max(0f, source.radius));
            cs.SetFloat("_ShapeAngle", 0f);
            cs.SetVector("_ShapeBox", Vector3.one * 0.5f);
            cs.SetVector("_ShapePlane", Vector2.zero);
            cs.SetFloat("_ShapeTerrainH", 0f);
            cs.SetFloat("_ShapeTerrainF", 0f);
            cs.SetFloat("_ShapeTorusThickness", Mathf.Max(0f, source.tubeRadius));
            cs.SetFloat("_ShapeHeight", Mathf.Max(0f, source.height));
            cs.SetInt("_ShapeShell", 0);
            cs.SetInt("_EmitPointCount", meshMaster ? meshPointCount : 0);
            cs.SetFloat("_EmitInherit", meshMaster ? shapeInheritVelocity : 0f);
        }

        static int ParticleShapeFromEmitter(BrazePyroEmitter.Shape shape) => shape switch
        {
            BrazePyroEmitter.Shape.Box => 3,
            BrazePyroEmitter.Shape.Cylinder => 9,
            BrazePyroEmitter.Shape.Capsule => 10,
            BrazePyroEmitter.Shape.Cone => 11,
            BrazePyroEmitter.Shape.Torus => 7,
            _ => 0,
        };

        // Slave spawn points from a Mesh-shaped BrazePyroEmitter master. Uses the master's renderer + Surface Mask
        // descriptor and this system's GPU mesh path; unsupported/fragment sources emit nothing instead of falling
        // back to CPU point collection.
        int GatherMasterEmitPoints(BrazePyroEmitter source)
        {
            emitUsePassIdx = false;
            emitInputLocal = false;
            emitHasPrevLocal = false;
            emitExtraNormalVelocity = 0f;
            if (source.TryGetParticleSpawnMeshSource(out var meshRenderer, out var gate, out int maxPoints, out float normalSpeed)
                && TryGatherGpuMeshEmitPoints(meshRenderer, gate, out int gpuCount,
                                              forceSurface: true, sampleCountOverride: Mathf.Max(1, maxPoints)))
            {
                emitExtraNormalVelocity = normalSpeed;
                return gpuCount;
            }

            return 0;
        }

        void Update()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) return;
            editorPreviewActive = false;
#endif
            SimulateStep();
        }

#if UNITY_EDITOR
        /// <summary>Advance this system through one explicit edit-mode preview frame.</summary>
        internal void EditorPreviewStep(float deltaTime, float previewTime, int previewFrame)
        {
            if (Application.isPlaying || !isActiveAndEnabled || !gameObject.scene.IsValid()) return;

            editorPreviewActive = true;
            tickDeltaTime = Mathf.Clamp(deltaTime, 0f, 1f / 20f);
            tickTime = Mathf.Max(0f, previewTime);
            tickFrame = Mathf.Max(0, previewFrame);
            SimulateStep();
        }

        /// <summary>Force authoring LUTs to refresh after nested curve/gradient SerializedProperty edits.</summary>
        internal void EditorPropertiesChanged()
        {
            lutsDirty = true;
        }

        /// <summary>Freeze preview state and discard surface-motion history so resume cannot create a velocity spike.</summary>
        internal void EditorPreviewPause()
        {
            if (Application.isPlaying) return;
            gpuHasPrev = false;
            emitLocalHasPrev = false;
            emitLastLocalTime = -1f;
            emitSkinnedSampleHasPrev = false;
            emitClonerHasPrevious = false;
        }

        /// <summary>End edit-mode preview and release every preview-only GPU allocation.</summary>
        internal void EditorPreviewStop()
        {
            if (Application.isPlaying) return;
            ShutdownResources();
        }

        /// <summary>Submit the latest mesh-particle state for this Scene-view camera without advancing simulation.</summary>
        internal void EditorPreviewRender(Camera camera)
        {
            if (Application.isPlaying || !editorPreviewActive || camera == null || camera.cameraType != CameraType.SceneView)
                return;
            if (renderMode == RenderMode.Mesh) RenderMeshParticles(camera);
        }
#endif

        void SimulateStep()
        {
            EnsureEventSettings();
            if (!Ready && !EnsureResources()) return;

            if (capacity != allocCapacity) { Release(); Allocate(); }
            if (lutsDirty) BakeLuts();
            if (renderMode == RenderMode.Mesh)
            {
                int signature = ComputeMeshBuildSignature();
                bool changed = signature != meshBuildSignature;
                if (changed || (meshDirty && Time.realtimeSinceStartupAsDouble >= meshBuildRetryAt))
                    BuildMesh(signature);
            }

            float unscaledDt = Mathf.Min(TickDeltaTime, 1f / 20f);
            float dt = unscaledDt * Mathf.Max(0f, timeScale);
            bool requestDriven = IsRequestDrivenEmitter;
            BrazePyroEmitter emitterSource = null;
            bool emitterDriven = !requestDriven && BrazePyroEmitter.TryGetParticleSource(this, out emitterSource);
            TrackMeshMotionWhileIdle(emitterSource, requestDriven, emitterDriven);
            if (requestDriven && !wasRequestDriven)
            {
                emitAccum = 0f;
                for (int b = 0; b < 2; b++)
                    reqCountBuf[b]?.SetData(ZeroU);
                cs.SetInt("_Capacity", allocCapacity);
                cs.SetBuffer(kClear, "_Particles", particleBuf);
                Dispatch1D(kClear, allocCapacity);
                BrazeTrailRenderer.ResetFor(this);
            }
            wasRequestDriven = requestDriven;

            // Continuous emission window (duration / looping). Bursts add on top, below.
            systemAge += dt;
            bool emitting = looping || systemAge <= duration;
            if (looping && duration > 0f) systemAge = Mathf.Repeat(systemAge, duration);
            // SLAVE sync: a system connected to a BrazePyroEmitter simply COPIES the master's emission — it spawns
            // at its Rate exactly while the master emits, and stops when it stops (burst windows, animated Strength
            // → 0, a disabled emitter all close the gate). Intervals/pulsing belong to the master's Burst, not the
            // slave's — the slave has no burst loop of its own (see the emit gate below). Free: one flag read.
            if (emitterDriven) emitting = emitterSource.EmissionScale > 0f;

            // Sim space: World bakes the Transform into the spawn (particles detach); Local keeps
            // particles in local space and applies the Transform at render via _Model.
            // Request-driven and BrazePyroEmitter-driven systems render in world space: particles are spawned at
            // world positions provided by the controller/source, so the Transform is irrelevant.
            // Mesh samples and Cloner transforms are already final world-space GPU data. Keep both detached in world
            // space even when Local was selected: applying this system's model matrix again rotates/translates their
            // positions and inherited velocities a second time (and can visibly reverse motion on a rotated system).
            bool worldSpaceShape = shape is EmitShape.Mesh or EmitShape.Cloner;
            bool local = simSpace == SimSpace.Local && !requestDriven && !emitterDriven && !worldSpaceShape;
            Matrix4x4 l2w = transform.localToWorldMatrix;
            lastModel = local ? l2w : Matrix4x4.identity;
            lastModelUsesLocalVelocity = local;
            mpb.SetMatrix("_Model", lastModel);

            cs.SetInt("_Capacity", allocCapacity);
            cs.SetInt("_VertexCount", 6);   // billboard quad; mesh mode draws separately via RenderMeshIndirect
            cs.SetFloat("_Dt", dt);
            cs.SetFloat("_TurbTime", TickTime);   // drives the turbulence curl field so it evolves (no static sinks)
            cs.SetVector("_StartColor", startColor);

            // Frame seed + start-value bands — needed by BOTH continuous Emit and request-driven EmitRequests.
            cs.SetInt("_FrameSeed", (int)((uint)TickFrame * 2654435761u));
            // Infinite life: the Simulate kernel skips death and BYPASSES the over-life modules (so particles keep
            // their start size/colour/full opacity — they don't get frozen invisible by an opacity-fade-in curve).
            // The stored lifetime just has to be positive (alive); large keeps the render-side flipbook frozen too.
            cs.SetInt("_InfiniteLife", infiniteLifetime ? 1 : 0);
            if (infiniteLifetime) cs.SetVector("_StartLifetimeR", new Vector4(1e6f, 1e6f, 0f, 0f));
            else SetRange("_StartLifetimeR", startLifetime);
            SetRange("_StartSpeedR", startSpeed);
            SetRange("_StartSizeR", startSize);
            SetRange("_StartRotR", startRotation);
            cs.SetInt("_ReqCapacity", ReqCapacity);

            // Ping-pong parity (shared across all systems this frame): write to [p], drain [1-p]. This
            // makes cross-system spawning order-independent — sources write [p] while targets drain [1-p].
            int parity = TickFrame & 1;
            int drain = 1 - parity;

            // Attachment: bind the master's per-body transforms so attached particles (flagged at spawn) ride them.
            bool attachOn = AttachActive;
            bool attachStorageOn = attachOn || attachBuf != null;
            EnsureAttachBuf(attachStorageOn);
            attachStorageOn = attachBuf != null;
            cs.SetInt("_AttachEnable", attachOn ? 1 : 0);
            cs.SetInt("_AttachWriteEnable", attachStorageOn ? 1 : 0);
            cs.SetInt("_AttachCount", attachOn ? attachSrcCount : 0);
            BindAttach(kEmitRequests, attachOn, attachStorageOn);
            BindAttach(kSimulate, attachOn, attachStorageOn);

            // --- Drain our incoming requests (written last frame) into fresh particles, then clear them ---
            cs.SetBuffer(kEmitRequests, "_Particles", particleBuf);
            cs.SetBuffer(kEmitRequests, "_Head", headBuf);
            cs.SetBuffer(kEmitRequests, "_Requests", reqBuf[drain]);
            cs.SetBuffer(kEmitRequests, "_ReqCount", reqCountBuf[drain]);
            Dispatch1D(kEmitRequests, ReqCapacity);
            cs.SetBuffer(kClearReqCount, "_ReqCount", reqCountBuf[drain]);
            cs.Dispatch(kClearReqCount, 1, 1, 1);

            // --- Emit (continuous + bursts, shape-based) — request-driven emitters only drain requests ---
            emitAccum = requestDriven ? 0f : emitAccum + (emitting ? rate * dt : 0f);
            int emitCount = (int)emitAccum;
            emitAccum -= emitCount;
            // Bursts are a self-emission feature — a slave has none (the master's Burst provides the intervals).
            if (burst && !requestDriven && !emitterDriven && !burstDone && systemFrame >= nextBurstFrame)
            {
                emitCount += Mathf.Max(0, burstCount.Sample());             // (random) count, additive
                burstsFired++;
                if (burstLoop && (burstLoops <= 0 || burstsFired < burstLoops))
                    nextBurstFrame = systemFrame + Mathf.Max(1, burstLoopDelay.Sample());   // (random) gap
                else
                    burstDone = true;
            }
            emitCount = Mathf.Min(emitCount, allocCapacity);
            systemFrame++;
            // Mesh shape OR pushed external (debris) points both emit via the world-space points path; gather them
            // up front — with none, there's nothing to spawn this frame. BrazePyroEmitter-driven systems ignore their
            // own particle shape and spawn directly from the connected fluid emitter source — a Mesh-shaped master
            // hands its surface samples over through the same world-space points path.
            bool masterMeshEmit = emitterDriven && emitterSource.shape == BrazePyroEmitter.Shape.Mesh;
            bool pointEmit = !requestDriven && (masterMeshEmit || (!emitterDriven && (shape == EmitShape.Mesh || HasExternalEmit)));
            int emitPointCount = (pointEmit && emitCount > 0)
                ? (masterMeshEmit ? GatherMasterEmitPoints(emitterSource) : GatherEmitPoints())
                : 0;
            if (pointEmit && emitPointCount == 0) emitCount = 0;

            bool clonerEmit = !requestDriven && !emitterDriven && shape == EmitShape.Cloner;
            GraphicsBuffer clonerBasePosition = null, clonerPositionOffset = null;
            GraphicsBuffer clonerBaseRotation = null, clonerRotationOffset = null;
            int clonerInstanceCount = 0;
            bool clonerReady = clonerEmit && TryPrepareClonerEmit(
                out clonerBasePosition, out clonerPositionOffset,
                out clonerBaseRotation, out clonerRotationOffset, out clonerInstanceCount);
            if (!clonerReady)
            {
                emitClonerHasPrevious = false;
                if (clonerEmit) emitCount = 0;
            }

            if (emitCount > 0)
            {
                cs.SetInt("_EmitCount", emitCount);
                if (emitterDriven)
                {
                    BindEmitterShape(emitterSource, emitPointCount);
                }
                else
                {
                    cs.SetMatrix("_EmitToWorld", local ? Matrix4x4.identity : l2w);
                    cs.SetInt("_ShapeKind", pointEmit ? (int)EmitShape.Mesh : ComputeShapeKind(shape));
                    cs.SetFloat("_ShapeRadius", shapeRadius);
                    cs.SetFloat("_ShapeAngle", coneAngle * Mathf.Deg2Rad);
                    cs.SetVector("_ShapeBox", boxSize * 0.5f);
                    cs.SetVector("_ShapePlane", planeSize);
                    cs.SetFloat("_ShapeTerrainH", terrainHeight);
                    cs.SetFloat("_ShapeTerrainF", terrainFreq);
                    cs.SetFloat("_ShapeTorusThickness", torusThickness);
                    cs.SetFloat("_ShapeHeight", 0f);
                    cs.SetInt("_ShapeShell", emitFromShell ? 1 : 0);
                    cs.SetInt("_EmitPointCount", emitPointCount);
                    cs.SetFloat("_EmitInherit", pointEmit || clonerEmit ? shapeInheritVelocity : 0f);
                }
                BindEmitterMotion(emitterSource, false);
                cs.SetInt("_EmitInputLocal", emitInputLocal ? 1 : 0);
                cs.SetInt("_EmitHasPrevLocal", emitHasPrevLocal ? 1 : 0);
                cs.SetMatrix("_EmitLocalToWorld", emitLocalToWorld);
                cs.SetMatrix("_EmitPrevLocalToWorld", emitPrevLocalToWorld);
                cs.SetMatrix("_EmitNormalLocalToWorld", emitNormalLocalToWorld);
                cs.SetFloat("_EmitLocalInvDt", emitLocalInvDt);
                cs.SetFloat("_EmitExtraNormalVelocity", emitExtraNormalVelocity);
                cs.SetBuffer(kEmit, "_EmitPoints", emitPointCount > 0 && emitPointBuf != null ? emitPointBuf : EmitPointDummy());
                cs.SetBuffer(kEmit, "_EmitNormals", emitPointCount > 0 && emitNrmBuf != null ? emitNrmBuf : EmitPointDummy());
                cs.SetBuffer(kEmit, "_EmitVelocities", emitPointCount > 0 && emitVelBuf != null ? emitVelBuf : EmitPointDummy());
                BindClonerEmit(kEmit, clonerReady, clonerBasePosition, clonerPositionOffset,
                    clonerBaseRotation, clonerRotationOffset, clonerInstanceCount, dt);
                cs.SetInt("_EmitUsePassIdx", emitUsePassIdx ? 1 : 0);   // masked GPU emission: sample the GPU pass-set
                cs.SetBuffer(kEmit, "_EmitPassIdx", emitUsePassIdx && emitPassIdxBuf != null ? emitPassIdxBuf : EmitPassIdxDummy());
                cs.SetBuffer(kEmit, "_PassCountRO", emitUsePassIdx && emitPassCountBuf != null ? emitPassCountBuf : EmitPassIdxDummy());
                cs.SetBuffer(kEmit, "_Particles", particleBuf);
                cs.SetBuffer(kEmit, "_Head", headBuf);
                Dispatch1D(kEmit, emitCount);
            }

            // Snapshot after Emit so this dispatch's inherited velocity compares against the prior preview/game
            // frame. Capture even on frames with no births so a later burst receives the true one-frame velocity.
            if (clonerReady)
                CaptureClonerEmit(clonerBasePosition, clonerPositionOffset, clonerInstanceCount);

            // --- Simulate ---
            cs.SetInt("_VolEnable", velocityOverLifetime ? 1 : 0);
            cs.SetInt("_SolEnable", sizeOverLifetime ? 1 : 0);
            cs.SetInt("_OolEnable", opacityOverLifetime ? 1 : 0);
            cs.SetInt("_ColEnable", colorOverLifetime ? 1 : 0);
            cs.SetInt("_CbsEnable", colorBySpeed ? 1 : 0);
            cs.SetVector("_CbsRange", colorBySpeedRange);
            cs.SetTexture(kSimulate, "_VolXLut", lutVelX);
            cs.SetTexture(kSimulate, "_VolYLut", lutVelY);
            cs.SetTexture(kSimulate, "_VolZLut", lutVelZ);
            cs.SetTexture(kSimulate, "_SolLut", lutSize);
            cs.SetTexture(kSimulate, "_OolLut", lutOpacity);
            cs.SetTexture(kSimulate, "_ColLut", lutColor);
            cs.SetTexture(kSimulate, "_CbsLut", lutColorSpeed);
            int forceCount = GatherForces();
            cs.SetInt("_ForceCount", forceCount);
            cs.SetBuffer(kSimulate, "_Forces", forceBuf);
            cs.SetBuffer(kSimulate, "_ForceFalloff", forceFalloffBuf);
            int colliderCount = collisionEnabled ? GatherColliders() : 0;
            cs.SetInt("_ColliderCount", colliderCount);
            lastForceCount = forceCount; lastColliderCount = colliderCount;
            cs.SetFloat("_CollisionRadius", collisionRadiusScale);
            cs.SetInt("_CollisionSleepOn", collisionSleep ? 1 : 0);
            cs.SetFloat("_CollisionSleepSpeed", Mathf.Max(0f, collisionSleepSpeed));
            cs.SetFloat("_CollisionWakeSpeed", Mathf.Max(0.01f, collisionSleepSpeed * 0.1f));
            cs.SetInt("_CollisionSleepFrames", Mathf.Clamp(
                Mathf.CeilToInt(Mathf.Max(0f, collisionSleepDelay) / Mathf.Max(dt, 1e-4f)), 1, 255));
            cs.SetInt("_MeshRotationEnable", renderMode == RenderMode.Mesh ? 1 : 0);
            cs.SetFloat("_MeshSpin", meshSpin);
            cs.SetFloat("_MeshSpinBySpeed", meshSpinBySpeed);
            cs.SetBuffer(kSimulate, "_Colliders", colliderBuf);

            // Mesh-surface collision — build the deforming triangle grid from the mesh collider's renderer and
            // bind it (or 1-element dummies when off, since the Simulate kernel references the grid buffers).
            // The single shared grid serves mesh collision AND the Surface Flow force. Collision (if on) owns the
            // grid mesh; otherwise the Flow force's mesh is used — so flow works standalone with no collider.
            bool collActive = collisionEnabled && meshCollider != null;
            Renderer gridR = collActive ? meshCollider.ResolvedMeshRenderer : (flowForce != null ? flowForce.ResolvedSurface : null);
            float collRange = collActive ? MeshContactRange() : 0f;
            float flowRange = flowForce != null ? flowForce.QueryRange : 0f;
            float gridRange = Mathf.Max(collRange, flowRange);
            bool gridReady = gridR != null && PrepareMeshGrid(gridR, Mathf.Max(0.01f, gridRange));
            bool meshCollide = collActive && gridReady;
            cs.SetInt("_MeshCollide", meshCollide ? 1 : 0);
            if (gridReady)
            {
                meshGrid.BindQuery(cs, kSimulate);
                cs.SetFloat("_MeshContactRange", gridRange);
                if (meshCollide)
                    cs.SetVector("_MeshResponse", new Vector4(meshCollider.bounce, meshCollider.friction, meshCollider.strength, 0f));
            }
            else BindDummyGrid(kSimulate);

            // Spline Flow — sample the path (positions + tangents) to the per-system buffers, or bind dummies.
            BindSplinePath(kSimulate);

            // Events — On Death. Resolve the effective op + spawn target (a sub-emitter, or self).
            // Op bitmask per event (bit0 = spawn particles, bit1 = fire event — independent, both may run).
            // Edit preview is visual-only: never invoke user-authored UnityEvents or mutate other systems/scenes.
            int deathOp = Application.isPlaying ? onDeath.OpCode : 0;
            int collOp = Application.isPlaying && collisionEnabled && (colliderCount > 0 || meshCollide)
                ? onCollision.OpCode : 0;

            // ONE shared request buffer per dispatch → resolve a single spawn target. Death takes precedence;
            // if BOTH events spawn to DIFFERENT targets, the collision spawns also go to the death target.
            BrazeParticleSystem spawnTarget = this;
            if ((deathOp & 1) != 0 && onDeath.target != null)
            {
                if (onDeath.target.Ready) spawnTarget = onDeath.target; else deathOp &= ~1;   // not ready → drop spawn
            }
            else if ((collOp & 1) != 0 && onCollision.target != null)
            {
                if (onCollision.target.Ready) spawnTarget = onCollision.target; else collOp &= ~1;
            }
            cs.SetBuffer(kSimulate, "_Requests", spawnTarget.IncomingReqBuf(parity));
            cs.SetBuffer(kSimulate, "_ReqCount", spawnTarget.IncomingReqCount(parity));

            // On Death uniforms (inherit bit1 = colour; velocity inheritance retired).
            cs.SetInt("_OnDeathOp", deathOp);
            cs.SetInt("_OnDeathCount", Mathf.Clamp(onDeath.count, 0, MaxEventCount));
            cs.SetFloat("_OnDeathChance", onDeath.chance);
            cs.SetFloat("_OnDeathPosJitter", onDeath.positionJitter);
            cs.SetInt("_OnDeathInherit", onDeath.inheritColor ? 2 : 0);
            bool deathFire = (deathOp & 2) != 0;
            if (deathFire) { eventCountBuf.SetData(ZeroU); eventPosBuf.SetData(EventClear); }
            cs.SetInt("_EventCapacity", EventCapacity);
            cs.SetBuffer(kSimulate, "_EventPos", eventPosBuf);
            cs.SetBuffer(kSimulate, "_EventCount", eventCountBuf);

            // On Collision uniforms (spawn + fire, both at the hit point). Buffers always bound (referenced in
            // the collision path); fired once per particle per frame on first contact.
            cs.SetInt("_OnCollisionOp", collOp);
            cs.SetFloat("_OnCollisionChance", onCollision.chance);
            cs.SetInt("_OnCollisionCount", Mathf.Clamp(onCollision.count, 0, MaxEventCount));
            cs.SetFloat("_OnCollisionPosJitter", onCollision.positionJitter);
            cs.SetInt("_OnCollisionInherit", onCollision.inheritColor ? 2 : 0);
            bool collFire = (collOp & 2) != 0;
            if (collFire) { collisionEventCountBuf.SetData(ZeroU); collisionEventPosBuf.SetData(EventClear); }
            cs.SetBuffer(kSimulate, "_CollisionEventPos", collisionEventPosBuf);
            cs.SetBuffer(kSimulate, "_CollisionEventCount", collisionEventCountBuf);

            // Fluid-follow — an explicit VDB connection overrides the Pyro/generic field for advection only.
            BindFluidFollow(emitterSource, local, dt);

            cs.SetBuffer(kSimulate, "_Particles", particleBuf);
            Dispatch1D(kSimulate, allocCapacity);

            // Surface Flow constraint pass — its own kernel (Simulate sits at the 8-UAV limit; this one needs
            // only particles + warm-start hints). Runs immediately after Simulate so the surface projection is
            // the final word on this frame's positions.
            if (flowForce != null && gridReady)
            {
                EnsureFlowHintBuf();
                cs.SetBuffer(kFlowConstrain, "_Particles", particleBuf);
                cs.SetBuffer(kFlowConstrain, "_FlowHint", flowHintBuf);
                cs.SetBuffer(kFlowConstrain, "_Forces", forceBuf);
                cs.SetBuffer(kFlowConstrain, "_ForceFalloff", forceFalloffBuf);
                cs.SetBuffer(kFlowConstrain, "_Attach", attachBuf != null ? attachBuf : attachDummy);
                meshGrid.BindQuery(cs, kFlowConstrain);
                Dispatch1D(kFlowConstrain, allocCapacity);
            }

            // After Simulate's event writes are queued, read them back (async) → fire UnityEvents.
            if (deathFire) RequestEventReadback();
            if (collFire) RequestCollisionEventReadback();

            // --- Reset indirect args on the GPU (no CPU SetData / sync), then Compact ---
            cs.SetBuffer(kResetArgs, "_Indirect", argsBuf);
            cs.Dispatch(kResetArgs, 1, 1, 1);
            cs.SetBuffer(kCompact, "_Particles", particleBuf);
            cs.SetBuffer(kCompact, "_VisibleIndices", visibleBuf);
            cs.SetBuffer(kCompact, "_Indirect", argsBuf);
            Dispatch1D(kCompact, allocCapacity);

            // Inter-particle separation: push overlapping particles apart so piles read as solid. Runs AFTER Compact
            // so it can iterate the compacted alive list (indirect dispatch over the alive count, not the full pool).
            // Only positions change here — the alive flag and visible list stay valid, so render still sees them.
            if (separationEnabled) DispatchSeparation(meshCollide);

            ApplyRenderer();

            // Mesh mode renders itself as real opaque geometry (RenderMeshIndirect) — must resubmit every camera
            // frame. Runtime Update can submit here; edit preview submits from beginCameraRendering because the
            // Scene-view render cadence is independent from the capped simulation tick.
            if (renderMode == RenderMode.Mesh)
            {
#if UNITY_EDITOR
                if (!editorPreviewActive || Application.isPlaying)
#endif
                    RenderMeshParticles();
            }

            // Stats are strictly opt-in: when hidden we do ZERO extra work (no readback, no timing).
            if (showStats)
            {
                statsFps = Mathf.Lerp(statsFps, 1f / Mathf.Max(unscaledDt, 1e-5f), 0.1f);
                RequestAliveReadback();
            }
        }

        // GPU inter-particle separation. A uniform spatial hash (linked-list-per-bucket) finds neighbours; each
        // overlapping pair is pushed apart by half the overlap. Read (SepSolve) and write (SepApply) are split so
        // neighbour positions are sampled consistently. _CollisionRadius / _ColliderCount / _MeshCollide were set
        // for Simulate just above, so the depenetration in SepApply matches the static collider contact.
        void DispatchSeparation(bool meshCollide)
        {
            EnsureSeparationBuffers();

            // Cell size = 2 × the largest contact radius so the 3×3×3 query is guaranteed to find every overlap.
            float maxR = Mathf.Max(1e-4f, startSize.SampleRange(0f).y) * collisionRadiusScale * Mathf.Max(0.1f, separationScale);
            cs.SetFloat("_SepCellSize", 2f * maxR);
            cs.SetInt("_SepTableMask", sepTableSize - 1);
            cs.SetFloat("_SepScale", Mathf.Max(0.1f, separationScale));
            cs.SetFloat("_SepStiffness", Mathf.Clamp01(separationStiffness));

            // Turn the live alive-count (in argsBuf[1], written by Compact) into a 1-D indirect dispatch size, so
            // Build/Solve/Apply launch exactly ceil(alive/64) groups — no dead threads, full wave coherence.
            cs.SetBuffer(kSepWriteArgs, "_SepArgs", argsBuf);
            cs.SetBuffer(kSepWriteArgs, "_SepDispatchArgs", sepDispatchArgsBuf);
            cs.Dispatch(kSepWriteArgs, 1, 1, 1);

            int iters = Mathf.Clamp(separationIterations, 1, 4);
            for (int it = 0; it < iters; it++)
            {
                cs.SetBuffer(kSepClearGrid, "_CellHead", cellHeadBuf);
                Dispatch1D(kSepClearGrid, sepTableSize);

                cs.SetBuffer(kSepBuildGrid, "_Particles", particleBuf);
                cs.SetBuffer(kSepBuildGrid, "_SepVisible", visibleBuf);
                cs.SetBuffer(kSepBuildGrid, "_SepArgs", argsBuf);
                cs.SetBuffer(kSepBuildGrid, "_CellHead", cellHeadBuf);
                cs.SetBuffer(kSepBuildGrid, "_PartNext", partNextBuf);
                cs.DispatchIndirect(kSepBuildGrid, sepDispatchArgsBuf);

                cs.SetBuffer(kSepSolve, "_Particles", particleBuf);
                cs.SetBuffer(kSepSolve, "_SepVisible", visibleBuf);
                cs.SetBuffer(kSepSolve, "_SepArgs", argsBuf);
                cs.SetBuffer(kSepSolve, "_CellHead", cellHeadBuf);
                cs.SetBuffer(kSepSolve, "_PartNext", partNextBuf);
                cs.SetBuffer(kSepSolve, "_SepDisp", sepDispBuf);
                cs.DispatchIndirect(kSepSolve, sepDispatchArgsBuf);

                cs.SetBuffer(kSepApply, "_Particles", particleBuf);
                cs.SetBuffer(kSepApply, "_SepVisible", visibleBuf);
                cs.SetBuffer(kSepApply, "_SepArgs", argsBuf);
                cs.SetBuffer(kSepApply, "_SepDisp", sepDispBuf);
                cs.SetBuffer(kSepApply, "_Colliders", colliderBuf);
                if (meshCollide) meshGrid.BindQuery(cs, kSepApply); else BindDummyGrid(kSepApply);
                cs.DispatchIndirect(kSepApply, sepDispatchArgsBuf);
            }
        }

        internal bool NeedsAlphaDepthSort =>
            blendMode == BlendMode.Alpha && DrawViaFeature &&
            cs != null && particleBuf != null && visibleBuf != null && argsBuf != null;

        internal GraphicsBuffer SortPairsBuffer => sortPairsBuf;

        int SortWindowCapacity()
        {
            return Mathf.NextPowerOfTwo(Mathf.Max(1, allocCapacity));
        }

        internal bool PrepareDepthSort()
        {
            if (!NeedsAlphaDepthSort) return false;

            int n = SortWindowCapacity();
            if (sortPairsBuf == null || sortPairsBuf.count != n)
            {
                sortPairsBuf?.Release();
                sortPairsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 8);
            }
            return sortPairsBuf != null;
        }

        // Back-to-front bitonic sort of the compacted visible list. This runs from the URP render graph using the
        // camera currently being rendered, so untagged cameras and Scene/Game view camera differences do not skip or
        // mis-key the alpha order. The full capacity-sized power-of-two window avoids the old stale-readback prefix,
        // which left newly-burst particles unsorted and still flickering.
        internal void DispatchDepthSort(CommandBuffer cmd, Camera cam)
        {
            if (!NeedsAlphaDepthSort || sortPairsBuf == null || cam == null) return;

            int n = sortPairsBuf.count;
            Transform ct = cam.transform;
            Vector3 camPos = ct.position;
            Vector3 camForward = ct.forward;
            cmd.SetComputeVectorParam(cs, IdSortCamPos, new Vector4(camPos.x, camPos.y, camPos.z, 0f));
            cmd.SetComputeVectorParam(cs, IdSortCamForward, new Vector4(camForward.x, camForward.y, camForward.z, 0f));
            cmd.SetComputeMatrixParam(cs, IdSortModel, lastModel);
            cmd.SetComputeIntParam(cs, IdSortCount, n);

            cmd.SetComputeBufferParam(cs, kSortInit, IdParticles, particleBuf);
            cmd.SetComputeBufferParam(cs, kSortInit, IdVisibleIndices, visibleBuf);
            cmd.SetComputeBufferParam(cs, kSortInit, IdIndirect, argsBuf);
            cmd.SetComputeBufferParam(cs, kSortInit, IdSortPairs, sortPairsBuf);
            Dispatch1D(cmd, cs, kSortInit, n);

            cmd.SetComputeBufferParam(cs, kSortStep, IdSortPairs, sortPairsBuf);
            for (int k = 2; k <= n; k <<= 1)
                for (int j = k >> 1; j > 0; j >>= 1)
                {
                    cmd.SetComputeIntParam(cs, IdSortK, k);
                    cmd.SetComputeIntParam(cs, IdSortJ, j);
                    Dispatch1D(cmd, cs, kSortStep, n);
                }

            cmd.SetComputeBufferParam(cs, kSortScatter, IdVisibleIndices, visibleBuf);
            cmd.SetComputeBufferParam(cs, kSortScatter, IdIndirect, argsBuf);
            cmd.SetComputeBufferParam(cs, kSortScatter, IdSortPairs, sortPairsBuf);
            Dispatch1D(cmd, cs, kSortScatter, n);
        }

        static void Dispatch1D(CommandBuffer cmd, ComputeShader shader, int kernel, int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            cmd.SetComputeIntParam(shader, IdRowThreads, gx * 64);
            cmd.DispatchCompute(shader, kernel, gx, gy, 1);
        }

        // Non-blocking readback of the indirect instanceCount (alive particles) — one in flight at a time.
        void RequestAliveReadback()
        {
            if (statsReadbackPending || argsBuf == null) return;
            statsReadbackPending = true;
            uint generation = resourceGeneration;
            AsyncGPUReadback.Request(argsBuf, req =>
            {
                if (this == null || generation != resourceGeneration) return;
                statsReadbackPending = false;
                if (!isActiveAndEnabled) return;
                if (!req.hasError) aliveCount = (int)req.GetData<uint>()[1];
            });
        }

        // Capped CPU event channel — read back this frame's ≤EventCapacity sampled positions and fire
        // the UnityEvent for each. Single readback, one in flight at a time. Hard-bounded so it can't flood.
        void RequestEventReadback()
        {
            if (eventReadbackPending || eventPosBuf == null) return;
            eventReadbackPending = true;
            uint generation = resourceGeneration;
            AsyncGPUReadback.Request(eventPosBuf, req =>
            {
                if (this == null || generation != resourceGeneration) return;
                eventReadbackPending = false;
                if (!isActiveAndEnabled) return;
                if (req.hasError || onDeath == null || onDeath.onFire == null) return;
                var data = req.GetData<Vector4>();
                int n = Mathf.Min(data.Length, EventCapacity);
                for (int i = 0; i < n; i++)
                    if (data[i].w > 0.5f) onDeath.onFire.Invoke(new Vector3(data[i].x, data[i].y, data[i].z));
            });
        }

        // On-Collision twin of the above: read back this frame's capped sample of hit points and fire the
        // UnityEvent for each (e.g. BrazeSpread.Seed). One readback in flight; hard-bounded so it can't flood.
        void RequestCollisionEventReadback()
        {
            if (collisionEventReadbackPending || collisionEventPosBuf == null) return;
            collisionEventReadbackPending = true;
            uint generation = resourceGeneration;
            AsyncGPUReadback.Request(collisionEventPosBuf, req =>
            {
                if (this == null || generation != resourceGeneration) return;
                collisionEventReadbackPending = false;
                if (!isActiveAndEnabled) return;
                if (req.hasError || onCollision == null || onCollision.onFire == null) return;
                var data = req.GetData<Vector4>();
                int n = Mathf.Min(data.Length, EventCapacity);
                for (int i = 0; i < n; i++)
                    if (data[i].w > 0.5f) onCollision.onFire.Invoke(new Vector3(data[i].x, data[i].y, data[i].z));
            });
        }

        // Total GPU bytes held by this system (buffers + LUT/texture atlases) — for the stats panel.
        public long GpuBytes()
        {
            long b = 0;
            b += BufBytes(particleBuf) + BufBytes(visibleBuf) + BufBytes(argsBuf) + BufBytes(sortPairsBuf);
            b += BufBytes(forceBuf) + BufBytes(colliderBuf);
            b += BufBytes(headBuf) + BufBytes(reqBuf[0]) + BufBytes(reqBuf[1])
               + BufBytes(reqCountBuf[0]) + BufBytes(reqCountBuf[1])
               + BufBytes(eventPosBuf) + BufBytes(eventCountBuf)
               + BufBytes(collisionEventPosBuf) + BufBytes(collisionEventCountBuf);
            // Curve LUTs (256×1 RGFloat = 8 B/px) ×5, colour LUTs (256×1 RGBA32 = 4 B/px) ×2, falloff atlas.
            b += 5L * 256 * 8 + 2L * 256 * 4;
            if (forceFalloffBuf != null) b += (long)forceFalloffBuf.count * 4;
            return b;
        }
        static long BufBytes(GraphicsBuffer g) => g == null ? 0 : (long)g.count * g.stride;

        public int AliveCount => aliveCount;
        public int Capacity => allocCapacity;
        public float StatsFps => statsFps;
        public int ActiveForces => lastForceCount;
        public int ActiveColliders => lastColliderCount;
        /// <summary>The GPU particle buffer (48-byte packed Particle; use BrazeParticleStruct.hlsl accessors rather
        /// than assuming a float4 lifetime field).
        /// For other systems (e.g. the Surface Disintegrator) to read particle positions on the GPU.</summary>
        public GraphicsBuffer ParticleBuffer => particleBuf;
        public int ParticleCapacity => allocCapacity;
        /// <summary>Compacted list of alive particle indices (filled by Compact each frame). The matching alive
        /// COUNT is in <see cref="ArgsBuffer"/>[1]. Used by <see cref="BrazeTrailRenderer"/> to draw per-particle trails.</summary>
        public GraphicsBuffer VisibleBuffer => visibleBuf;
        /// <summary>Render-space model matrix (identity for World / emitter-driven systems; the Transform for Local) —
        /// the same _Model the particle shader uses, so trails line up with the sprites.</summary>
        public Matrix4x4 ModelMatrix => lastModel;

        static readonly int IdDrawMesh = Shader.PropertyToID("_DrawMesh");

        // Only mesh topology and mesh albedo assignments belong in this signature. Dynamic renderer values such as
        // Spin (simulation uniform), Scale, Tint and lighting (material values) must never trigger a texture-array
        // readback/rebuild while an inspector control is being dragged.
        int ComputeMeshBuildSignature()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + MeshTextureSignature(meshDefaultTexture);
                int count = meshes != null ? meshes.Count : 0;
                h = h * 31 + count;
                for (int i = 0; i < count; i++)
                {
                    Mesh mesh = meshes[i].mesh;
                    h = h * 31 + (mesh != null ? mesh.BrazeInstanceId() : 0);
                    h = h * 31 + MeshTextureSignature(meshes[i].texture);
                    if (mesh == null) continue;
                    h = h * 31 + mesh.subMeshCount;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        h = h * 31 + (int)mesh.GetTopology(s);
                        h = h * 31 + (int)mesh.GetIndexCount(s);
                        h = h * 31 + (int)mesh.GetIndexStart(s);
                        h = h * 31 + (int)mesh.GetBaseVertex(s);
                    }
                }
                return h;
            }
        }

        static int MeshTextureSignature(Texture2D value)
        {
            if (value == null) return 0;
            unchecked
            {
                int h = value.BrazeInstanceId();
                h = h * 31 + value.width;
                h = h * 31 + value.height;
#if UNITY_EDITOR
                // imageContentsHash follows imported-asset reimports, but Unity removes it from the Player API.
                h = h * 31 + value.imageContentsHash.GetHashCode();
#endif
                // updateCount is Player-safe and also follows runtime Texture.Apply/copy updates in the Editor.
                h = h * 31 + value.updateCount.GetHashCode();
                return h;
            }
        }

        // Validate the assigned meshes and build immutable draw resources. Each triangle submesh gets its own
        // indirect command so meshes with submeshes/base-vertex offsets are not accidentally interpreted as one
        // contiguous draw. All submeshes in a MeshEntry share that entry's albedo slice and particle selection.
        // Meshes do not need Read/Write: rendering consumes Unity's GPU-side vertex/index buffers directly.
        void BuildMesh(int signature)
        {
            meshDirty = false;
            meshBuildSignature = signature;
            meshBuildRetryAt = 0.0;

            if (meshes == null || meshes.Count == 0)
            {
                ClearMeshBuild();
                return;
            }

            if (meshMat == null)
            {
                var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/ParticleMesh");
                if (shader == null)
                {
                    Debug.LogError("[BrazeFX] Shader 'BrazeFX/ParticleMesh' not found.");
                    ClearMeshBuild();
                    ScheduleMeshBuildRetry();
                    return;
                }
                meshMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            }

            var bakedTextures = new List<Texture2D>();
            var drawMeshes = new List<Mesh>();
            var drawSlices = new List<int>();
            var drawArgs = new List<GraphicsBuffer.IndirectDrawIndexedArgs>();

            for (int m = 0; m < meshes.Count; m++)
            {
                Mesh mesh = meshes[m].mesh;
                if (mesh == null) continue;

                int slice = bakedTextures.Count;
                bool hasDraw = false;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    uint indexCount = mesh.GetIndexCount(s);
                    if (mesh.GetTopology(s) != MeshTopology.Triangles || indexCount == 0) continue;

                    drawMeshes.Add(mesh);
                    drawSlices.Add(slice);
                    drawArgs.Add(new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = indexCount,
                        instanceCount = 0,
                        startIndex = mesh.GetIndexStart(s),
                        baseVertexIndex = (uint)mesh.GetBaseVertex(s),
                        startInstance = 0,
                    });
                    hasDraw = true;
                }

                if (hasDraw) bakedTextures.Add(meshes[m].texture);
            }

            if (bakedTextures.Count == 0)
            {
                ClearMeshBuild();
                return;
            }

            GraphicsBuffer newArgs = null;
            Texture2DArray newTextureArray = null;
            try
            {
                newArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    drawArgs.Count, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                newArgs.SetData(drawArgs);
                newTextureArray = CreateMeshTextureArray(bakedTextures);
            }
            catch (System.Exception ex)
            {
                newArgs?.Release();
                if (newTextureArray != null) DestroyOwned(newTextureArray);
                Debug.LogException(ex, this);
                ScheduleMeshBuildRetry();
                return; // preserve the last valid build instead of flickering to an empty renderer
            }

            meshArgsBuf?.Release();
            if (meshTexArray != null) DestroyOwned(meshTexArray);
            meshArgsBuf = newArgs;
            meshTexArray = newTextureArray;
            meshBakedCount = bakedTextures.Count;
            meshDrawCount = drawArgs.Count;
            meshDrawMeshes.Clear();
            meshDrawMeshes.AddRange(drawMeshes);

            meshMpb = new MaterialPropertyBlock[meshDrawCount];
            for (int i = 0; i < meshDrawCount; i++)
            {
                meshMpb[i] = new MaterialPropertyBlock();
                meshMpb[i].SetInt(IdDrawMesh, drawSlices[i]);
            }
        }

        void ScheduleMeshBuildRetry()
        {
            meshDirty = true;
            meshBuildRetryAt = Time.realtimeSinceStartupAsDouble + MeshBuildRetryDelay;
        }

        void ClearMeshBuild()
        {
            meshArgsBuf?.Release(); meshArgsBuf = null;
            if (meshTexArray != null) { DestroyOwned(meshTexArray); meshTexArray = null; }
            meshMpb = null;
            meshDrawMeshes.Clear();
            meshBakedCount = 0;
            meshDrawCount = 0;
        }

        // Compact the alive particles to the camera-visible set (or pass all through when culling is off), so the
        // mesh draws instance only what's on-screen. Runs after Compact, before WriteMeshArgs.
        void DispatchMeshCull(Camera renderCamera)
        {
            // A camera supplied by edit preview is the Scene camera currently being rendered. Runtime preserves the
            // authored override/Main-camera behavior used by the global immediate draw submission.
            Camera cam = meshFrustumCull
                ? (renderCamera != null ? renderCamera
                   : meshCullCamera != null ? meshCullCamera
                   : ResolvedMainCamera())
                : null;
            bool on = cam != null;
            if (on)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, meshCullPlaneScratch);
                for (int i = 0; i < 6; i++)
                {
                    Plane pl = meshCullPlaneScratch[i];
                    meshCullPlanes[i] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
                }
                cs.SetVectorArray("_MeshCullPlanes", meshCullPlanes);
                cs.SetMatrix("_MeshCullModel", lastModel);
                // Per-particle radius in the shader is params.y (current size) × this, so this is just the mesh
                // footprint multiplier (mesh scale × a safety margin) — NOT the size again (params.y supplies it).
                cs.SetFloat("_MeshCullScale", Mathf.Max(1e-4f, Mathf.Max(0f, meshScale) * Mathf.Max(0.1f, meshCullRadiusScale)));
            }
            cs.SetInt("_MeshCullOn", on ? 1 : 0);

            EnsureMeshCullBuffers();
            cs.SetBuffer(kMeshCullReset, "_MeshCullCount", meshCullCountBuf);
            cs.Dispatch(kMeshCullReset, 1, 1, 1);

            // Compute the indirect dispatch args from Compact's alive count (argsBuf[1]) so MeshCull runs one
            // thread per ALIVE particle, not one per capacity slot. Reuses SepWriteArgs (a generic "ceil(count/64)
            // → dispatch args" kernel that reads _SepArgs[1]); argsBuf holds the alive count at [1] post-Compact.
            cs.SetBuffer(kSepWriteArgs, "_SepArgs", argsBuf);
            cs.SetBuffer(kSepWriteArgs, "_SepDispatchArgs", meshCullDispatchArgsBuf);
            cs.Dispatch(kSepWriteArgs, 1, 1, 1);

            cs.SetBuffer(kMeshCull, "_Particles", particleBuf);
            cs.SetBuffer(kMeshCull, "_VisibleIndices", visibleBuf);   // Compact's alive-particle index list (input)
            cs.SetBuffer(kMeshCull, "_Indirect", argsBuf);            // alive count at [1]
            cs.SetBuffer(kMeshCull, "_MeshCullVisible", meshCullVisibleBuf);
            cs.SetBuffer(kMeshCull, "_MeshCullCount", meshCullCountBuf);
            cs.DispatchIndirect(kMeshCull, meshCullDispatchArgsBuf);
        }

        // Lazily allocate the mesh-mode cull buffers (capacity-tied) the first time a mesh draw needs them,
        // so Sprite/stretched systems never pay for them. Re-created if a resize changed allocCapacity.
        void EnsureMeshCullBuffers()
        {
            if (meshCullDispatchArgsBuf == null)
                meshCullDispatchArgsBuf = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            if (meshCullVisibleBuf != null && meshCullVisibleBuf.count == allocCapacity) return;
            meshCullVisibleBuf?.Release();
            meshCullVisibleBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocCapacity, sizeof(uint));
            if (meshCullCountBuf == null)
                meshCullCountBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        }

        // Submit the mesh-mode draws for this frame: one Graphics.RenderMeshIndirect per mesh, drawn as real opaque
        // geometry so URP handles depth-sorting, shadow receiving, and shadow casting through its normal passes.
        void RenderMeshParticles(Camera renderCamera = null)
        {
            if (!MeshDrawReady) return;

            // Cull to the on-screen instances, then push that VISIBLE count into every mesh draw command so
            // RenderMeshIndirect instances only what the camera sees. Runs after Compact (same GPU timeline).
            DispatchMeshCull(renderCamera);
            cs.SetInt("_MeshArgCount", meshDrawCount);
            cs.SetBuffer(kWriteMeshArgs, "_MeshCullCount", meshCullCountBuf);
            cs.SetBuffer(kWriteMeshArgs, "_MeshArgsOut", meshArgsBuf);
            cs.Dispatch(kWriteMeshArgs, 1, 1, 1);

            // Shared state lives on the material; only _DrawMesh differs per mesh (on each block).
            meshMat.SetBuffer("_Particles", particleBuf);
            meshMat.SetBuffer("_VisibleIndices", meshCullVisibleBuf);   // the culled on-screen list (count = _MeshCullCount)
            meshMat.SetInt("_MeshCount", meshBakedCount);
            meshMat.SetTexture("_MeshTex", meshTexArray);
            meshMat.SetMatrix("_Model", lastModel);
            meshMat.SetFloat("_VelocityIsLocal", lastModelUsesLocalVelocity ? 1f : 0f);
            meshMat.SetFloat("_Facing", (float)facing);
            meshMat.SetFloat("_MeshLight", Mathf.Clamp01(meshLightStrength));
            Vector2 brightnessRange = ResolvedEmissiveGainRange;
            // Scale + brightness range occupy the same float4 slot previously reserved for mesh scale. The shader
            // derives stable per-particle brightness from Particle.seed, so no particle-buffer field/allocation is added.
            meshMat.SetVector("_MeshRenderParams", new Vector4(
                Mathf.Max(0f, meshScale), brightnessRange.x, brightnessRange.y, 0f));
            meshMat.SetColor("_Tint", tint);
            meshMat.SetFloat("_AlphaCutoff", alphaCutoff);
            meshMat.SetFloat("_MeshReceiveShadows", meshReceiveShadows ? 1f : 0f);
            meshMat.SetFloat("_MeshShadowStrength", Mathf.Clamp01(meshShadowStrength));

            var rp = new RenderParams(meshMat)
            {
                worldBounds = new Bounds(transform.position, Vector3.one * MeshWorldBoundsSize),
                shadowCastingMode = meshCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = meshReceiveShadows,
                // RenderParams defaults this to Off. Unity 6000.0 then leaves indirect draws without the ambient
                // probe data used by SampleSH, while later Unity 6 revisions can appear to supply a fallback.
                // Request the normal dynamic-renderer path explicitly so mesh particles shade consistently.
                lightProbeUsage = LightProbeUsage.BlendProbes,
                layer = gameObject.layer,
                camera = renderCamera,
            };
            for (int i = 0; i < meshDrawCount; i++)
            {
                rp.matProps = meshMpb[i];
                Graphics.RenderMeshIndirect(rp, meshDrawMeshes[i], meshArgsBuf, 1, i);
            }
        }

        // Pack one albedo per baked mesh into a Texture2DArray (slice i = baked mesh i, the same index the shader
        // picks per particle). Per-mesh texture wins; else the mesh-only Default Texture; else white. Sizes/formats
        // differ freely — each source is blitted to a common RGBA32 size. One-time cost, rebuilt with the mesh list.
        Texture2DArray CreateMeshTextureArray(IReadOnlyList<Texture2D> textures)
        {
            int n = textures.Count;
            if (n == 0) return null;

            int w = 4, h = 4;
            for (int i = 0; i < n; i++)
            {
                var t = textures[i] != null ? textures[i] : meshDefaultTexture;
                if (t != null) { w = Mathf.Max(w, t.width); h = Mathf.Max(h, t.height); }
            }
            w = Mathf.Clamp(Mathf.NextPowerOfTwo(w), 64, 1024);
            h = Mathf.Clamp(Mathf.NextPowerOfTwo(h), 64, 1024);

            Texture2DArray result = null;
            RenderTexture rt = null;
            Texture2D tmp = null;
            var prevActive = RenderTexture.active;
            try
            {
                result = new Texture2DArray(w, h, n, TextureFormat.RGBA32, true)
                { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
                for (int i = 0; i < n; i++)
                {
                    Texture src = textures[i] != null ? textures[i]
                                : (meshDefaultTexture != null ? (Texture)meshDefaultTexture : Texture2D.whiteTexture);
                    Graphics.Blit(src, rt);                  // resize/convert any source into the common slice size
                    RenderTexture.active = rt;
                    tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    tmp.Apply(false);
                    result.SetPixels(tmp.GetPixels(), i, 0);
                }
                result.Apply(true, true);                    // generate mips, then make non-readable (free CPU copy)
                return result;
            }
            catch
            {
                DestroyOwned(result);
                throw;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) DestroyOwned(tmp);
            }
        }

        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        static readonly int IdZTest = Shader.PropertyToID("_ZTest");
        void ApplyRenderer()
        {
            if (mat == null) return;
            Vector2 brightnessRange = ResolvedEmissiveGainRange;
            // Billboards sample _MainTex; mesh mode samples its own per-mesh Texture2DArray (_MeshTex) instead.
            mat.SetTexture(IdMainTex, customTexture != null ? customTexture : BrazeParticleTextures.Get(texture));
            mat.SetFloat(IdZTest, (float)CompareFunction.LessEqual);
            mat.SetColor("_Tint", tint);
            mat.SetFloat("_EmissiveGain", brightnessRange.y);
            mat.SetVector("_Pivot", pivot);
            mat.SetVector("_Flip", flip);
            mat.SetFloat("_RenderMode", (float)renderMode);
            mat.SetFloat("_Facing", (float)facing);
            mat.SetFloat("_VelocityIsLocal", lastModelUsesLocalVelocity ? 1f : 0f);
            // xy = stretch, z = stretch variation, w = brightness minimum. This reuses the old stretch constants'
            // padding, keeping the shader constant footprint unchanged while also removing one material property call.
            mat.SetVector("_RenderParams", new Vector4(
                stretchVelocity, stretchLength, Mathf.Clamp01(stretchVariation), brightnessRange.x));
            mat.SetVector("_Flipbook", new Vector4(flipbookColumns, flipbookRows, flipbook ? 1f : 0f, flipbookCycles));
            mat.SetFloat("_FlipbookOpacityEnable", flipbook && flipbookOpacityOverTime ? 1f : 0f);
            mat.SetTexture("_FlipbookOpacityLut", lutFlipbookOpacity);
            if (flipbook)
            {
                mat.SetVector("_FlipbookPlayback", new Vector4(
                    (float)flipbookTimeMode, (float)flipbookPlayback, flipbookFps, flipbookStartFrame));
                mat.SetVector("_FlipbookLayout", new Vector4(
                    (float)flipbookAnimation, (float)flipbookRowMode, flipbookRow, flipbookRandomStart ? 1f : 0f));
                mat.SetVector("_FlipbookSpeedRange", new Vector4(flipbookSpeedRange.x, flipbookSpeedRange.y, 0f, 0f));
            }
            mat.SetFloat("_SoftFade", softParticles ? softFade : 0f);
            mat.SetFloat("_AlphaCutoff", alphaCutoff);

            int src, dst, mul;
            switch (blendMode)
            {
                case BlendMode.Alpha:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha; mul = 0; break;
                case BlendMode.Multiply:
                    src = (int)UnityEngine.Rendering.BlendMode.DstColor;
                    dst = (int)UnityEngine.Rendering.BlendMode.Zero; mul = 1; break;
                default: // Additive
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.One; mul = 0; break;
            }
            mat.SetFloat("_BlendSrc", src);
            mat.SetFloat("_BlendDst", dst);
            mat.SetFloat("_Multiply", mul);
            if (softParticles) mat.EnableKeyword("_SOFT_PARTICLES"); else mat.DisableKeyword("_SOFT_PARTICLES");
        }

        void OnDrawGizmos()
        {
            // Collision contact sphere — representative size (largest Start Size × Radius Scale), matching
            // the kernel's per-particle contact radius. Drawn a little along the emit direction (in open
            // space, not buried in the emitter gizmo) so the size is readable; own toggle.
            if (collisionEnabled && showCollisionGizmo)
            {
                float pr = Mathf.Max(startSize.SampleRange(0f).y, 0f) * collisionRadiusScale;
                if (pr > 0f)
                {
                    Vector3 at = transform.position + transform.up * (0.6f + pr);
                    Gizmos.color = new Color(0.30f, 1f, 0.55f, 0.95f);   // matches the collider gizmo green
                    Gizmos.DrawWireSphere(at, pr);
                }
            }

            // Request-driven emitters spawn from controller-supplied positions, and slaves of a BrazePyroEmitter
            // spawn from the MASTER's shape — either way their own shape gizmo is misleading.
            if (IsRequestDrivenEmitter || !showGizmo || IsBrazePyroEmitterParticleTarget) return;
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            switch (shape)
            {
                case EmitShape.Sphere:     Gizmos.DrawWireSphere(Vector3.zero, shapeRadius); break;
                case EmitShape.Hemisphere: GizmoHemisphere(shapeRadius); break;
                case EmitShape.Cone:       GizmoCone(shapeRadius, coneAngle * Mathf.Deg2Rad); break;
                case EmitShape.Circle:     CircleXZ(Vector3.zero, shapeRadius); break;
                case EmitShape.Box:        Gizmos.DrawWireCube(Vector3.zero, boxSize); break;
                case EmitShape.Plane:
                case EmitShape.Terrain:    Gizmos.DrawWireCube(Vector3.zero, new Vector3(planeSize.x, 0f, planeSize.y)); break;
                case EmitShape.Torus:      GizmoTorus(shapeRadius, torusThickness); break;
                case EmitShape.Mesh:       Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 0.3f); break;
                case EmitShape.Cloner:
                    Gizmos.matrix = Matrix4x4.identity;
                    if (shapeCloner != null) Gizmos.DrawLine(transform.position, shapeCloner.transform.position);
                    return; // the Cloner already draws the source distribution itself
            }
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawLine(transform.position, transform.position + transform.up * 0.6f);
        }

        // Circle in the local XZ plane.
        static void CircleXZ(Vector3 c, float r, int seg = 40)
        {
            Vector3 prev = c + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }

        // Circle in the plane spanned by axes u, v.
        static void Circle(Vector3 c, Vector3 u, Vector3 v, float r, int seg = 24)
        {
            Vector3 prev = c + u * r;
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }

        static void GizmoCone(float r, float angle)
        {
            CircleXZ(Vector3.zero, r);                            // base
            float h = Mathf.Max(r, 0.05f) * 2f;
            float topR = r + h * Mathf.Tan(Mathf.Clamp(angle, 0f, 1.45f));
            CircleXZ(new Vector3(0f, h, 0f), topR);              // top
            for (int i = 0; i < 4; i++)                          // flare lines
            {
                float a = i / 4f * Mathf.PI * 2f;
                Vector3 d = new(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Gizmos.DrawLine(d * r, d * topR + Vector3.up * h);
            }
        }

        static void GizmoHemisphere(float r)
        {
            CircleXZ(Vector3.zero, r);                            // equator
            Circle(Vector3.zero, Vector3.right, Vector3.up, r, 40);   // upper arcs (full circles read fine)
            Circle(Vector3.zero, Vector3.forward, Vector3.up, r, 40);
        }

        static void GizmoTorus(float major, float thick)
        {
            CircleXZ(Vector3.zero, major + thick);               // outer
            CircleXZ(Vector3.zero, Mathf.Max(0f, major - thick)); // inner
            for (int i = 0; i < 8; i++)                          // tube cross-sections
            {
                float a = i / 8f * Mathf.PI * 2f;
                Vector3 outDir = new(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Circle(outDir * major, outDir, Vector3.up, thick, 16);
            }
        }
    }
}
