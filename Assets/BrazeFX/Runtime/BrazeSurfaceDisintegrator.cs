using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Mesh Effect — SURFACE DISINTEGRATOR. Splits a mesh into CLUMPS (clusters of triangles) drawn as one
    /// "shattered" mesh by a custom vertex shader (<c>BrazeFX/Chunk</c>): while a clump is ANCHORED it follows the
    /// skinned/deformed body (stays locked, conforms to animation); once RELEASED it freezes its current shape and
    /// flies off as a rigid piece — falling/tumbling under the existing Braze force list, then ageing (shrink +
    /// dissolve) until it's gone. Holes appear where chunks leave. Like an eggshell cracking / cement crumbling.
    ///
    /// Works on a single renderer OR a whole hierarchy: leave <see cref="target"/> empty and the component
    /// disintegrates EVERY SkinnedMeshRenderer + MeshRenderer under it (mixed skinned/static is fine). Each child
    /// renderer becomes an independent PART with its own mesh/buffers/materials; the compute instances, force /
    /// collider buffers, hit grid, and eroder are shared. Release calls fan out to every part.
    ///
    /// GPU-friendly: released clumps are simulated by a compute shader and positioned by the chunk vertex shader.
    /// In EDIT mode it just previews the clumps (coloured gizmos); in PLAY it takes over rendering (hides the
    /// source renderers) and simulates.
    /// </summary>
    [AddComponentMenu("BrazeFX/Destruction/Braze Surface Disintegrator", 41)]
    [DisallowMultipleComponent]
    public sealed class BrazeSurfaceDisintegrator : MonoBehaviour, IBrazeAnimatedTarget
    {
        [Header("Target")]
        [Tooltip("Mesh / skinned renderer to disintegrate. EMPTY = disintegrate every SkinnedMeshRenderer + " +
                 "MeshRenderer under this object (a parent with many children).")]
        public Renderer target;

        [Header("Clumps")]
        [Tooltip("Approximate clump diameter in world units — the size of each broken-off chunk.")]
        [BrazeAnimatable("CLUMPS/Clump Size", 0.001f, 1f)]
        [Min(0.001f)] public float clumpSize = 0.12f;
        [Tooltip("0 = boxy grid clumps; 1 = warped, organic clump boundaries.")]
        [BrazeAnimatable("CLUMPS/Irregularity", 0f, 1f)]
        [Range(0f, 1f)] public float irregularity = 0.4f;
        [Tooltip("Layout seed — change for a different clump arrangement at the same size.")]
        public int seed = 12345;

        [Header("Forces (drive free chunks — your existing Braze forces)")]
        [Tooltip("Braze force components that push free chunks (Gravity / Wind / Drag / Radial / Vortex / " +
                 "Turbulence) — drop them here. Empty = ALL active Braze forces in the scene affect the chunks.")]
        public List<BrazeParticleForce> forces = new();
        [Tooltip("Outward speed kicked into a chunk when it breaks off.")]
        [BrazeAnimatable("FORCES/Release Impulse", 0f, 10f)]
        [Min(0f)] public float releaseImpulse = 1.5f;
        [Tooltip("Random tumble (rad/s) given to a chunk on release.")]
        [BrazeAnimatable("FORCES/Release Spin", 0f, 30f)]
        [Min(0f)] public float releaseSpin = 6f;
        [Tooltip("How much of the body's motion a chunk inherits when it breaks off.")]
        [BrazeAnimatable("FORCES/Inherit Body Velocity", 0f, 2f)]
        [Range(0f, 2f)] public float inheritBodyVelocity = 1f;

        [Header("Age (free chunks shrink + dissolve away)")]
        [Tooltip("Lifetime of a free chunk in seconds (0 = never dies). Over it the chunk shrinks and dissolves.")]
        [BrazeAnimatable("AGE/Clump Lifetime", 0f, 10f)]
        [Min(0f)] public float clumpLifetime = 0f;
        [Tooltip("How much a chunk shrinks toward a point over its life (0 = none, 1 = to nothing).")]
        [BrazeAnimatable("AGE/Shrink", 0f, 1f)]
        [Range(0f, 1f)] public float shrink = 0.4f;
        [Tooltip("How much a chunk is eaten away by a noise dissolve over its life (0 = none, 1 = fully consumed) " +
                 "— looks like it crumbles into nothing.")]
        [BrazeAnimatable("AGE/Dissolve", 0f, 1f)]
        [Range(0f, 1f)] public float dissolve = 0.8f;
        [Tooltip("Dissolve grain frequency.")]
        [BrazeAnimatable("AGE/Dissolve Scale", 0.1f, 100f)]
        [Min(0.1f)] public float dissolveScale = 30f;
        [Tooltip("Glow colour at the crumbling dissolve edge (HDR).")]
        [ColorUsage(true, true)] public Color dissolveEdge = new(1f, 0.4f, 0.1f, 2f);

        [Header("Soft body (cheap shader wobble — no sim cost)")]
        public bool softBody = false;
        [Tooltip("Jiggle amount for free chunks (scaled by their speed). Pure vertex-shader wobble, no physics.")]
        [BrazeAnimatable("SOFT BODY/Softness", 0f, 1f)]
        [Range(0f, 1f)] public float softness = 0.3f;

        [Header("Secondary Particles (GPU)")]
        [Tooltip("Braze Particle System that receives GPU spawn requests from clump surface points. " +
                 "The emitter owns particle look, lifetime, size, colour, forces, and rendering.")]
        public BrazeParticleSystem debrisEmitter;
        [Tooltip("Off = one GPU burst when a clump separates. On = released clumps keep shedding particles from their shrinking surface.")]
        public bool debrisFromShrinkingClumps = false;
        [Tooltip("Particles requested per clump at the moment it separates. 0 = off.")]
        [Min(0)] public int debrisPointsPerChunk = 6;
        [Tooltip("When Spawn While Shrinking is on: average particles per second emitted by each released clump.")]
        [BrazeAnimatable("SECONDARY/Debris Rate Per Chunk", 0f, 60f)]
        [Min(0f)] public float debrisRatePerChunk = 12f;
        [Tooltip("How much of the moving clump surface velocity each secondary particle inherits.")]
        [BrazeAnimatable("SECONDARY/Debris Inherit Velocity", 0f, 3f)]
        [Range(0f, 3f)] public float debrisInheritVelocity = 1f;
        [Tooltip("Extra velocity along the sampled surface normal added to each debris request.")]
        [BrazeAnimatable("SECONDARY/Debris Scatter", 0f, 2f)]
        [Min(0f)] public float debrisScatter = 0.3f;
        [Tooltip("Hard cap on debris spawn requests appended by this disintegrator in one frame.")]
        [Min(0)] public int maxDebrisPoints = 8000;

        [Header("Release Controls")]
        [Tooltip("Seconds used by Release In Chunks. Clumps receive stable random release times across this window.")]
        [BrazeAnimatable("RELEASE/Release In Chunks Duration", 0f, 10f)]
        [Min(0f)] public float releaseInChunksDuration = 2f;

        [Header("Fluid Emission (smoke / fire into Braze Containers)")]
        [Tooltip("Master switch: released clumps inject smoke/fire into ANY Braze Container whose box they pass " +
                 "through (no parenting needed). Off = no fluid emission. Uses an async readback of clump positions.")]
        public bool emitToFluid = false;
        [Tooltip("Continuous TRAIL: every flying clump keeps emitting as it tumbles, leaving a smoke/fire wake.")]
        public bool emitTrail = true;
        [Tooltip("BURST at break-off: an extra puff the moment a clump separates, fading over Burst Time.")]
        public bool emitBurst = true;
        [Tooltip("Smoke (soot) injected per clump per second.")]
        [BrazeAnimatable("FLUID/Density", 0f, 10f)]
        [Min(0f)] public float emitDensity = 1f;
        [Tooltip("Heat injected per clump per second — drives the fire look + buoyant rise.")]
        [BrazeAnimatable("FLUID/Temperature", 0f, 10f)]
        [Min(0f)] public float emitTemperature = 3f;
        [Tooltip("Fuel injected per clump per second — sustains combustion (flames).")]
        [BrazeAnimatable("FLUID/Fuel", 0f, 10f)]
        [Min(0f)] public float emitFuel = 2f;
        [Tooltip("Break-off puff strength as a multiple of the trail rates, at the instant of separation; fades to 1× over Burst Time.")]
        [BrazeAnimatable("FLUID/Burst Scale", 1f, 20f)]
        [Range(1f, 20f)] public float emitBurstScale = 6f;
        [Tooltip("Seconds the break-off puff lasts after a clump separates. With Trail off, clumps emit ONLY during this window.")]
        [BrazeAnimatable("FLUID/Burst Time", 0.01f, 5f)]
        [Min(0.01f)] public float emitBurstTime = 0.25f;
        [Tooltip("0 = constant heat. 1 = clumps emit fire+fuel when fresh and COOL to pure smoke as they age (heat/fuel " +
                 "fade to 0 over Cool Time; smoke keeps going). Models burning debris that chars and just smokes.")]
        [BrazeAnimatable("FLUID/Cool With Age", 0f, 1f)]
        [Range(0f, 1f)] public float emitCoolWithAge = 0.7f;
        [Tooltip("Seconds over which Cool With Age fades a clump's heat/fuel toward zero.")]
        [BrazeAnimatable("FLUID/Cool Time", 0.05f, 5f)]
        [Min(0.05f)] public float emitCoolTime = 1.5f;
        [Tooltip("How strongly the emitted smoke/fire inherits the flying clump's VELOCITY, so the fume streaks " +
                 "along with the debris instead of being born motionless. 0 = dead on emission; 1 = matches the " +
                 "clump's speed/direction. (Capped by the container's Max Speed.)")]
        [BrazeAnimatable("FLUID/Velocity Inherit", 0f, 1f)]
        [Range(0f, 1f)] public float emitVelocityInherit = 0.85f;

        [Header("Eroder (particles knock chunks loose on contact)")]
        [Tooltip("Braze Particle Systems whose particles release chunks when they touch the surface. " +
                 "Empty = manual release only (Release All / ReleaseSphere).")]
        public List<BrazeParticleSystem> eroders = new();
        [SerializeField, HideInInspector] BrazeParticleSystem eroder;   // legacy single field — migrated into eroders
        [Tooltip("How close an eroder particle must get to a chunk's surface to release it (world units).")]
        [BrazeAnimatable("ERODER/Hit Radius", 0.001f, 0.5f)]
        [Min(0.001f)] public float hitRadius = 0.06f;

        [Header("Events")]
        [Tooltip("Called once when a full disintegration starts.")]
        public UnityEvent OnStartedDisintegration = new();
        [Tooltip("Called once when a full disintegration has completed.")]
        public UnityEvent OnFinishedDisintegration = new();

        [Header("Preview (edit mode)")]
        [Tooltip("Draw the clumps in the Scene view (each clump a distinct colour) so you can design size + shape.")]
        public bool showClumps = true;
        [Min(1)] public int maxDots = 6000;
        [Min(0f)] public float dotSize = 0.01f;

        public static readonly List<BrazeSurfaceDisintegrator> Active = new();
        public int ClumpCount { get { int n = 0; for (int i = 0; i < parts.Count; i++) n += parts[i].clumpCount; return n; } }
        public int PartCount => parts.Count;

        [StructLayout(LayoutKind.Sequential)]
        struct GpuUInt2
        {
            public uint x, y;
            public GpuUInt2(int x, int y) { this.x = (uint)x; this.y = (uint)y; }
        }

        /// <summary>
        /// Per-part runtime state. A SKINNED child = one Part (its own bones, deformed every frame). All STATIC
        /// children are MERGED into a single combined Part (one submesh per material, baked to world space once) —
        /// so a 300-child structure costs one part's worth of dispatches/draws, not 300.
        /// </summary>
        sealed class Part
        {
            public Renderer target;             // primary source renderer (null for the combined-static part)
            public SkinnedMeshRenderer smr;
            public MeshFilter mf;
            public int clumpCount;
            public bool isStatic;               // combined-static part: deform buffers filled once, never refreshed
            public Material[] overrideMaterials; // combined-static: one material per submesh (else use target's)
            public Mesh combinedMesh;           // combined-static: runtime mesh we own + must destroy
            public Bounds staticWorldBounds;    // combined-static: world bounds for the hit grid / release center

            // source renderers we disabled while we draw chunks (to restore on deactivate)
            public Renderer[] managed;
            public bool[] mgPrevEnabled, mgPrevForceOff;
            public ShadowCastingMode[] mgPrevShadow;
            public ShadowCastingMode castShadowMode;   // shadow mode for the chunk DrawMesh
            public int layer;

            // shattered render
            public bool prevUpdateWhenOffscreen;
            public bool sourceHiddenForDisintegration;
            public GameObject deformProviderGo;
            public SkinnedMeshRenderer deformSmr;
            public Animator targetAnimator;
            public AnimatorCullingMode prevAnimatorCullingMode;
            public bool restoreAnimatorCulling;
            public Mesh shattered;
            public Material[] chunkMats;
            public int origVertCount, shatteredVertCount;
            public int[] clumpVertStart, clumpVertCount;
            public Vector4[] clumpPosStateArr, clumpRotArr, clumpLifeArr, clumpVelArr, clumpAngVelArr;
            public GpuUInt2[] clumpRangeData, shatteredIdsData;
            public GraphicsBuffer clumpPosStateBuf, clumpRotBuf, clumpLifeBuf, clumpVelBuf, clumpAngVelBuf;
            public GraphicsBuffer deformedPosBuf, deformedNrmBuf, freeOffsetBuf, freeNrmBuf, clumpRangeBuf;
            public GraphicsBuffer clumpHitBuf, shatteredIdsBuf;
            // Fluid emission: latest async-read clump pos/state (xyz world pos, w released-flag) + life (x scale,
            // w age) + velocity (xyz world units/sec) so the emitted fume inherits the clump's motion.
            public Vector4[] emitPosReadback, emitLifeReadback, emitVelReadback;
            public bool emitReadbackValid, emitReadbackPending;
            public int emitReadbackRemaining;   // outstanding async requests this round (pos/vel/life)
            public Vector3 prevBodyPos, bodyVel;
            public bool bodyPosInit;

            // deformed extraction layout
            public int srcStride, posOff, nrmOff;
            public int deformMissFrames;
            public bool layoutOK, hasNrm, extractWarned;
            public GraphicsBuffer pendingDeformSrc;

            public Mesh TopologyMesh() => smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
        }

        readonly List<Part> parts = new();

        // ── runtime: shared across all parts ───────────────────────────────────────────
        bool active;
        Material sourceKeepAliveMat;
        readonly List<BrazeParticleForce> forceScratch = new();
        readonly List<BrazeParticleCollider> colliderScratch = new();
        GraphicsBuffer forceBuf, colliderBuf;
        GraphicsBuffer debrisReqDummy, debrisReqCountDummy;
        GraphicsBuffer forceFalloffBuf;       // forceCount × 256 floats (flat) — each force's centre→edge falloff curve
        GpuParticleForce[] forceData = new GpuParticleForce[8];
        GpuParticleCollider[] colliderData = new GpuParticleCollider[8];
        float[] falloffData = new float[256];
        int falloffHash;                      // last-uploaded falloff-atlas signature; skips the rebake when unchanged
        int frameForceCount, frameColliderCount;
        bool rebuildRequested;
        bool startedInvoked;
        bool finishPending, finishInvoked;
        bool finishTracksDebris, finishTracksShrinkingDebris;
        float finishClumpAge, finishDebrisAge, finishDebrisTailAge, finishDebrisLifetime;
        int finishDebrisStartFrame;
        bool chunkedReleaseActive;
        float chunkedReleaseDurationRuntime, chunkedReleaseAge, chunkedReleaseProgress;
        int chunkedReleaseSeed;

        // ── deformed target extraction (GPU) ──────────────────────────────────────────
        ComputeShader extractCS;
        int kExtract;
        const int MaxGroupsPerDim = 65535;

        // ── released-clump simulation (GPU) ───────────────────────────────────────────
        ComputeShader simCS;
        int kSimClumps, kMarkChunkedHits, kReleaseClumps, kDebrisFromReleased, kDebrisFromFreeClumps;
        const int SpawnRequestStride = 48;

        // ── hit detection (eroder particles → release) ─────────────────────────────────
        const int HitMaxDim = 32, HitBucket = 8;
        ComputeShader hitCS;
        int kClearGrid, kClearHits, kScatter, kMarkHits;
        GraphicsBuffer cellNumBuf, cellBucketBuf;   // shared hit grid (transient, cleared per part each use)

        static readonly int IdClumpPosState = Shader.PropertyToID("_ClumpPosState");
        static readonly int IdClumpRot = Shader.PropertyToID("_ClumpRot");
        static readonly int IdClumpLife = Shader.PropertyToID("_ClumpLife");
        static readonly int IdClumpCentroid = Shader.PropertyToID("_ClumpCentroid");
        static readonly int IdEffectorPosition = Shader.PropertyToID("_EffectorPosition");
        static readonly int IdEffectorRotation = Shader.PropertyToID("_EffectorRotation");
        static readonly int IdEffectorScale = Shader.PropertyToID("_EffectorScale");
        static readonly int IdEffectorCount = Shader.PropertyToID("_EffectorCount");
        static readonly int IdSoftAmount = Shader.PropertyToID("_SoftAmount");
        static readonly int IdDissolveScale = Shader.PropertyToID("_DissolveScale");
        static readonly int IdDissolveEdge = Shader.PropertyToID("_DissolveEdge");
        static readonly int IdDeformedPos = Shader.PropertyToID("_DeformedPos");
        static readonly int IdDeformedNrm = Shader.PropertyToID("_DeformedNrm");
        static readonly int IdFreeOffset = Shader.PropertyToID("_FreeOffset");
        static readonly int IdFreeNrm = Shader.PropertyToID("_FreeNrm");
        static readonly int IdL2W = Shader.PropertyToID("_L2W");
        static readonly int IdN2W = Shader.PropertyToID("_N2W");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        static readonly int IdColor = Shader.PropertyToID("_Color");
        static readonly int IdBaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int IdDrawMode = Shader.PropertyToID("_DrawMode");
        static readonly int IdAmbient = Shader.PropertyToID("_Ambient");
        static readonly int IdMetallic = Shader.PropertyToID("_Metallic");
        static readonly int IdSmoothness = Shader.PropertyToID("_Smoothness");
        static readonly int IdGlossiness = Shader.PropertyToID("_Glossiness");
        static readonly int IdMetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap");
        static readonly int IdBumpMap = Shader.PropertyToID("_BumpMap");
        static readonly int IdBumpScale = Shader.PropertyToID("_BumpScale");
        static readonly int IdOcclusionMap = Shader.PropertyToID("_OcclusionMap");
        static readonly int IdOcclusionStrength = Shader.PropertyToID("_OcclusionStrength");
        static readonly int IdEmissionColor = Shader.PropertyToID("_EmissionColor");
        static readonly int IdEmissionMap = Shader.PropertyToID("_EmissionMap");
        static readonly int IdHasMetallicMap = Shader.PropertyToID("_HasMetallicMap");
        static readonly int IdHasBump = Shader.PropertyToID("_HasBump");
        static readonly int IdHasOcclusionMap = Shader.PropertyToID("_HasOcclusionMap");
        static readonly int IdHasEmissionMap = Shader.PropertyToID("_HasEmissionMap");

        void OnEnable() { MigrateLegacyEroder(); BrazeRuntimeRegistry.AddUnique(Active, this); }
        void OnDisable() { BrazeRuntimeRegistry.Remove(Active, this); Deactivate(); DestroyTemp(ref previewBake); }
        void OnValidate() { MigrateLegacyEroder(); builtPreviewKey = null; rebuildRequested = true; }   // defer rebuild to Update (no Destroy in OnValidate)

        // older scenes stored a single `eroder`; fold it into the `eroders` list once.
        public void OnBrazeAnimated() => OnValidate();

        void MigrateLegacyEroder()
        {
            if (eroder == null) return;
            eroders ??= new List<BrazeParticleSystem>();
            if (!eroders.Contains(eroder)) eroders.Add(eroder);
            eroder = null;
        }

        // ── target discovery ────────────────────────────────────────────────────────────
        static bool IsUsableRenderer(Renderer r)
        {
            if (r == null || (r.hideFlags & HideFlags.DontSave) != 0) return false;
            if (r is SkinnedMeshRenderer s) return s.sharedMesh != null;
            if (r is MeshRenderer)
            {
                var f = r.GetComponent<MeshFilter>();
                return f != null && f.sharedMesh != null;
            }
            return false;
        }

        void GatherRenderers(List<Renderer> outList)
        {
            outList.Clear();
            if (target != null)
            {
                if (IsUsableRenderer(target)) outList.Add(target);
                return;
            }
            // whole hierarchy: every skinned + static renderer under us (mixed is fine).
            GetComponentsInChildren(true, rendererScratch);
            for (int i = 0; i < rendererScratch.Count; i++)
                if (IsUsableRenderer(rendererScratch[i])) outList.Add(rendererScratch[i]);
        }
        readonly List<Renderer> rendererScratch = new();
        readonly List<Renderer> gatherScratch = new();

        static Part NewPart(Renderer r)
        {
            var p = new Part { target = r };
            p.smr = r as SkinnedMeshRenderer;
            p.mf = p.smr == null ? r.GetComponent<MeshFilter>() : null;
            return p;
        }

        // ── warped-grid clump cell (rest pose) ─────────────────────────────────────────
        Vector3Int ClumpCell(Vector3 restPos)
        {
            float inv = 1f / Mathf.Max(1e-4f, clumpSize);
            Vector3 p = restPos * inv;
            if (irregularity > 0f)
            {
                float so = (seed % 997) * 0.123f;
                p += new Vector3(Mathf.PerlinNoise(p.y + so, p.z) - 0.5f,
                                 Mathf.PerlinNoise(p.z + so, p.x) - 0.5f,
                                 Mathf.PerlinNoise(p.x + so, p.y) - 0.5f) * (irregularity * 0.5f);
            }
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }

        // ── PLAY: build + simulate + draw ──────────────────────────────────────────────
        void LateUpdate()
        {
            if (!Application.isPlaying) return;
            if (rebuildRequested) { rebuildRequested = false; if (active) Deactivate(); }   // param changed → rebuild
            if (!active && !Activate()) return;
            SimAndDraw();
        }

        bool Activate()
        {
            GatherRenderers(gatherScratch);
            if (gatherScratch.Count == 0) return false;

            EnsureSharedCompute();

            cellNumBuf?.Release();
            cellBucketBuf?.Release();
            int maxCells = HitMaxDim * HitMaxDim * HitMaxDim;
            cellNumBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxCells, sizeof(uint));
            cellBucketBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxCells * HitBucket, sizeof(float) * 3);

            parts.Clear();
            // skinned children deform independently → one Part each. static children are MERGED into one Part.
            staticScratch.Clear();
            int skinnedCount = 0;
            for (int i = 0; i < gatherScratch.Count; i++)
            {
                var r = gatherScratch[i];
                if (r is SkinnedMeshRenderer)
                {
                    var p = NewPart(r);
                    if (ActivatePartSingle(p)) { parts.Add(p); skinnedCount++; }
                    else DeactivatePart(p);
                }
                else
                {
                    staticScratch.Add(r);
                }
            }
            if (staticScratch.Count == 1)
            {
                // a lone static renderer: keep the simple per-renderer path (follows its transform).
                var p = NewPart(staticScratch[0]);
                if (ActivatePartSingle(p)) parts.Add(p); else DeactivatePart(p);
            }
            else if (staticScratch.Count > 1)
            {
                var p = BuildCombinedStaticPart(staticScratch);
                if (p != null) parts.Add(p);
            }

            if (parts.Count == 0) { Deactivate(); return false; }

            if (skinnedCount > 16)
                Debug.LogWarning($"[BrazeFX] Surface Disintegrator is driving {skinnedCount} SKINNED renderers — each is " +
                                 "skinned + simulated every frame and cannot be merged. Expect a frame-rate hit; consider " +
                                 "fewer skinned children or baking them to static meshes.");

            active = true;
            startedInvoked = false;
            finishPending = false;
            finishInvoked = false;
            chunkedReleaseActive = false;
            return true;
        }
        readonly List<Renderer> staticScratch = new();

        void EnsureSharedCompute()
        {
            if (extractCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeMeshExtract");
                if (asset != null) { extractCS = Instantiate(asset); kExtract = extractCS.FindKernel("Extract"); }
                else Debug.LogError("[BrazeFX] Compute shader 'BrazeMeshExtract' not found.");
            }
            if (hitCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeDisintegrateHit");
                if (asset != null)
                {
                    hitCS = Instantiate(asset);
                    kClearGrid = hitCS.FindKernel("ClearGrid"); kClearHits = hitCS.FindKernel("ClearHits");
                    kScatter = hitCS.FindKernel("Scatter"); kMarkHits = hitCS.FindKernel("MarkHits");
                }
            }
            if (simCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeDisintegrateSim");
                if (asset != null)
                {
                    simCS = Instantiate(asset);
                    // Surface clumps intentionally use unit mass response and have no Voronoi-only mass/centroid/
                    // impact-fluid buffers. The dedicated compile-time variant keeps the dispatch lean and prevents
                    // strict backends from rejecting unbound fracture resources.
                    kSimClumps = simCS.FindKernel("SimSurfaceClumps");
                    kMarkChunkedHits = simCS.FindKernel("MarkChunkedHits");
                    kReleaseClumps = simCS.FindKernel("ReleaseClumps");
                    kDebrisFromReleased = simCS.FindKernel("DebrisFromReleased");
                    kDebrisFromFreeClumps = simCS.FindKernel("DebrisFromFreeClumps");
                }
                else Debug.LogError("[BrazeFX] Compute shader 'BrazeDisintegrateSim' not found.");
            }
        }

        bool ActivatePartSingle(Part p)
        {
            Mesh topo = p.TopologyMesh();
            if (topo == null) return false;
            if (p.smr != null) p.smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            else if (p.mf != null && p.mf.sharedMesh != null) p.mf.sharedMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

            BuildShattered(p, topo);
            if (p.shattered == null) return false;

            if (p.target != null)
            {
                p.layer = p.target.gameObject.layer;
                p.castShadowMode = p.target.shadowCastingMode == ShadowCastingMode.Off
                    ? ShadowCastingMode.Off : ShadowCastingMode.TwoSided;
                p.targetAnimator = p.target.GetComponentInParent<Animator>();
                if (p.targetAnimator != null)
                {
                    p.prevAnimatorCullingMode = p.targetAnimator.cullingMode;
                    p.restoreAnimatorCulling = true;
                    p.targetAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
            if (p.smr != null)
            {
                p.prevUpdateWhenOffscreen = p.smr.updateWhenOffscreen;
                if (!EnsureHiddenDeformProvider(p)) return false;
            }
            CaptureManaged(p, p.target);
            HideSourceVisualForChunkRendering(p);
            p.sourceHiddenForDisintegration = false;
            p.bodyPosInit = false;
            p.deformMissFrames = 0;
            p.extractWarned = false;
            return true;
        }

        /// <summary>Merge many static renderers into ONE part (submesh-per-material, baked to world space).</summary>
        Part BuildCombinedStaticPart(List<Renderer> statics)
        {
            var p = new Part { isStatic = true };
            Mesh combined = BuildCombinedStaticMesh(statics, out Material[] mats);
            if (combined == null) return null;
            p.combinedMesh = combined;
            p.overrideMaterials = mats;

            BuildShattered(p, combined);
            if (p.shattered == null) { DestroyTemp(ref p.combinedMesh); return null; }

            // static = no deformation: fill the deformed buffers ONCE (combined mesh is already world space).
            p.deformedPosBuf?.SetData(combined.vertices);
            p.deformedNrmBuf?.SetData(combined.normals);
            p.layoutOK = false;   // never run the GPU extract for static parts
            p.staticWorldBounds = combined.bounds;   // verts are world space → these are world bounds

            p.layer = statics[0] != null ? statics[0].gameObject.layer : gameObject.layer;
            p.castShadowMode = ShadowCastingMode.Off;
            for (int i = 0; i < statics.Count; i++)
                if (statics[i] != null && statics[i].shadowCastingMode != ShadowCastingMode.Off)
                { p.castShadowMode = ShadowCastingMode.TwoSided; break; }

            CaptureManaged(p, statics.ToArray());
            HideSourceVisualForChunkRendering(p);
            p.sourceHiddenForDisintegration = false;
            return p;
        }

        void CaptureManaged(Part p, params Renderer[] renderers)
        {
            int n = renderers != null ? renderers.Length : 0;
            p.managed = new Renderer[n];
            p.mgPrevEnabled = new bool[n];
            p.mgPrevForceOff = new bool[n];
            p.mgPrevShadow = new ShadowCastingMode[n];
            for (int i = 0; i < n; i++)
            {
                var r = renderers[i];
                p.managed[i] = r;
                if (r == null) continue;
                p.mgPrevEnabled[i] = r.enabled;
                p.mgPrevForceOff[i] = r.forceRenderingOff;
                p.mgPrevShadow[i] = r.shadowCastingMode;
            }
        }

        void Deactivate()
        {
            for (int i = 0; i < parts.Count; i++) DeactivatePart(parts[i]);
            parts.Clear();

            ReleaseSharedBuffers();
            if (extractCS != null) { DestroyImmediateSafe(extractCS); extractCS = null; }
            if (hitCS != null) { DestroyImmediateSafe(hitCS); hitCS = null; }
            if (simCS != null) { DestroyImmediateSafe(simCS); simCS = null; }
            forceFalloffBuf?.Release(); forceFalloffBuf = null;
            if (sourceKeepAliveMat != null) { DestroyImmediateSafe(sourceKeepAliveMat); sourceKeepAliveMat = null; }
            chunkedReleaseActive = false;
            active = false;
        }

        void DeactivatePart(Part p)
        {
            if (p.managed != null)
            {
                for (int i = 0; i < p.managed.Length; i++)
                {
                    var r = p.managed[i];
                    if (r == null) continue;
                    r.shadowCastingMode = p.mgPrevShadow[i];
                    r.forceRenderingOff = p.mgPrevForceOff[i];
                    r.enabled = p.mgPrevEnabled[i];
                }
            }
            if (p.restoreAnimatorCulling && p.targetAnimator != null)
            {
                p.targetAnimator.cullingMode = p.prevAnimatorCullingMode;
                p.restoreAnimatorCulling = false;
                p.targetAnimator = null;
            }
            if (p.smr != null) p.smr.updateWhenOffscreen = p.prevUpdateWhenOffscreen;
            ReleasePartBuffers(p);
            DestroyChunkMaterials(p);
            DestroyHiddenDeformProvider(p);
            DestroyTemp(ref p.shattered);
            DestroyTemp(ref p.combinedMesh);
            p.sourceHiddenForDisintegration = false;
        }

        bool EnsureHiddenDeformProvider(Part p)
        {
            if (p.smr == null) return true;
            if (p.deformSmr != null) return true;

            var mat = HiddenSourceMaterial();
            if (mat == null) return false;

            p.deformProviderGo = new GameObject($"{p.target.name} BrazeFX Deform Provider")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            CopyTransform(p.smr.transform, p.deformProviderGo.transform);

            p.deformSmr = p.deformProviderGo.AddComponent<SkinnedMeshRenderer>();
            p.deformSmr.hideFlags = HideFlags.HideAndDontSave;
            p.deformSmr.sharedMesh = p.smr.sharedMesh;
            p.deformSmr.rootBone = p.smr.rootBone;
            p.deformSmr.bones = p.smr.bones;
            p.deformSmr.localBounds = p.smr.localBounds;
            p.deformSmr.quality = p.smr.quality;
            p.deformSmr.skinnedMotionVectors = p.smr.skinnedMotionVectors;
            p.deformSmr.updateWhenOffscreen = true;
            p.deformSmr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            p.deformSmr.shadowCastingMode = ShadowCastingMode.Off;
            p.deformSmr.receiveShadows = false;
            p.deformSmr.forceRenderingOff = false;
            p.deformSmr.enabled = true;

            int matCount = Mathf.Max(1, p.smr.sharedMaterials != null ? p.smr.sharedMaterials.Length : 0);
            var mats = new Material[matCount];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            p.deformSmr.sharedMaterials = mats;

            p.smr.updateWhenOffscreen = true;
            return true;
        }

        Material HiddenSourceMaterial()
        {
            if (sourceKeepAliveMat != null) return sourceKeepAliveMat;
            var sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/Hidden Source KeepAlive");
            if (sh == null)
            {
                Debug.LogError("[BrazeFX] Shader 'BrazeFX/Hidden Source KeepAlive' not found. Surface Disintegrator needs it to keep skinned GPU buffers alive while the original renderer is hidden.");
                return null;
            }

            sourceKeepAliveMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return sourceKeepAliveMat;
        }

        static void CopyTransform(Transform src, Transform dst)
        {
            if (src == null || dst == null) return;
            dst.SetPositionAndRotation(src.position, src.rotation);
            dst.localScale = src.lossyScale;
        }

        void SyncHiddenDeformProvider(Part p)
        {
            if (p.deformSmr == null || p.smr == null) return;
            CopyTransform(p.smr.transform, p.deformSmr.transform);
            p.deformSmr.localBounds = p.smr.localBounds;
            p.deformSmr.enabled = true;
            p.deformSmr.forceRenderingOff = false;

            Mesh mesh = p.smr.sharedMesh;
            if (mesh == null || mesh.blendShapeCount <= 0) return;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                p.deformSmr.SetBlendShapeWeight(i, p.smr.GetBlendShapeWeight(i));
        }

        void HideSourceVisualForChunkRendering(Part p)
        {
            if (p.managed == null) return;
            for (int i = 0; i < p.managed.Length; i++)
            {
                var r = p.managed[i];
                if (r == null) continue;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.forceRenderingOff = false;
                r.enabled = false;
            }
        }

        void DestroyHiddenDeformProvider(Part p)
        {
            if (p.deformProviderGo != null)
            {
                DestroyImmediateSafe(p.deformProviderGo);
                p.deformProviderGo = null;
                p.deformSmr = null;
            }
        }

        void HideSourceRendererForTakeover(Part p)
        {
            if (p.sourceHiddenForDisintegration) return;
            HideSourceVisualForChunkRendering(p);
            DestroyHiddenDeformProvider(p);
            p.sourceHiddenForDisintegration = true;
        }

        void BeginFinishCountdown()
        {
            if (clumpLifetime <= 0f)
            {
                finishPending = false;
                finishInvoked = false;
                return;
            }

            finishPending = true;
            finishInvoked = false;
            finishClumpAge = 0f;
            finishDebrisAge = 0f;
            finishDebrisTailAge = 0f;
            finishTracksDebris = FullReleaseSpawnsDebris();
            finishTracksShrinkingDebris = debrisFromShrinkingClumps;
            finishDebrisLifetime = finishTracksDebris ? GetDebrisGpuLifetime() : 0f;
            finishDebrisStartFrame = Time.frameCount + 1;
        }

        void InvokeStartedDisintegration()
        {
            if (startedInvoked) return;
            startedInvoked = true;
            OnStartedDisintegration?.Invoke();
        }

        bool FullReleaseSpawnsDebris()
        {
            if (!DebrisEmitterReady || debrisEmitter.IncomingRequestCapacity <= 0) return false;
            return debrisFromShrinkingClumps ? debrisRatePerChunk > 0f : debrisPointsPerChunk > 0;
        }

        float GetDebrisGpuLifetime()
        {
            if (debrisEmitter == null) return 0f;

            Vector2 lifetime = debrisEmitter.startLifetime != null ? debrisEmitter.startLifetime.SampleRange(0f) : Vector2.zero;
            return Mathf.Max(0.01f, lifetime.x, lifetime.y);
        }

        float DebrisFinishAgeDelta()
        {
            if (!finishTracksDebris || debrisEmitter == null || !debrisEmitter.isActiveAndEnabled) return 0f;
            return Mathf.Min(Time.deltaTime, 1f / 20f) * Mathf.Max(0f, debrisEmitter.timeScale);
        }

        void CheckFinishedDisintegration(float dt)
        {
            if (!finishPending || finishInvoked) return;
            if (clumpLifetime <= 0f) return; // persistent chunks never reach a finished state.

            finishClumpAge += Mathf.Max(0f, dt);
            bool clumpsDone = finishClumpAge >= clumpLifetime;
            bool debrisDone = true;

            if (finishTracksDebris)
            {
                bool debrisCanBeAlive = Time.frameCount >= finishDebrisStartFrame;
                float debrisDt = debrisCanBeAlive ? DebrisFinishAgeDelta() : 0f;

                if (finishTracksShrinkingDebris)
                {
                    if (clumpsDone) finishDebrisTailAge += debrisDt;
                    debrisDone = finishDebrisTailAge >= finishDebrisLifetime;
                }
                else
                {
                    finishDebrisAge += debrisDt;
                    debrisDone = finishDebrisAge >= finishDebrisLifetime;
                }
            }

            if (!clumpsDone || !debrisDone) return;
            finishPending = false;
            finishInvoked = true;
            OnFinishedDisintegration?.Invoke();
        }

        void SimAndDraw()
        {
            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);

            // shared this frame: gather forces + colliders once for every part.
            frameForceCount = GatherGpuForces();
            frameColliderCount = GatherGpuColliders();

            bool advanceChunked = chunkedReleaseActive;
            if (advanceChunked)
            {
                chunkedReleaseAge = Mathf.Min(chunkedReleaseDurationRuntime, chunkedReleaseAge + Mathf.Max(0f, dt));
                chunkedReleaseProgress = chunkedReleaseDurationRuntime <= 0f
                    ? 1f : Mathf.Clamp01(chunkedReleaseAge / chunkedReleaseDurationRuntime);
            }

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.shattered == null) continue;

                // 1. deformed verts -> buffers while the hidden provider/original mesh still feeds animation.
                //    STATIC parts are baked once (no per-frame deform), so we never re-extract them.
                if (p.isStatic)
                {
                    p.bodyVel = Vector3.zero;
                    if (!p.sourceHiddenForDisintegration) HitDetect(p);
                }
                else if (!p.sourceHiddenForDisintegration)
                {
                    if (RefreshDeformedGpu(p))
                    {
                        Vector3 bp = p.target != null ? p.target.transform.position : transform.position;
                        p.bodyVel = (p.bodyPosInit && dt > 0f) ? (bp - p.prevBodyPos) / dt : Vector3.zero;
                        p.prevBodyPos = bp; p.bodyPosInit = true;
                        HitDetect(p);
                    }
                    else
                    {
                        continue; // deformation not ready this frame for this part
                    }
                }
                else
                {
                    p.bodyVel = Vector3.zero;
                }

                if (advanceChunked) RunGpuChunkedRelease(p, i);
                RunGpuClumpSim(p, dt);
                RunGpuDebrisFromFreeClumps(p, dt);
                ApplyChunkMaterialState(p);
                DrawPart(p);
            }

            if (advanceChunked && chunkedReleaseProgress >= 1f)
            {
                chunkedReleaseActive = false;
                BeginFinishCountdown();
            }

            CheckFinishedDisintegration(dt);
        }

        void DrawPart(Part p)
        {
            // chunks cast shadows like the original body (two-sided, since they render Cull Off).
            int drawCount = Mathf.Min(p.shattered.subMeshCount, p.chunkMats != null ? p.chunkMats.Length : 0);
            for (int i = 0; i < drawCount; i++)
            {
                var mat = p.chunkMats[i];
                if (mat == null) continue;
                Graphics.DrawMesh(p.shattered, Vector3.zero, Quaternion.identity, mat, p.layer, null, i, null, p.castShadowMode, true);
            }
        }

        bool RefreshDeformedGpu(Part p)
        {
            if (extractCS == null || !p.layoutOK || p.deformedPosBuf == null || p.deformedNrmBuf == null) return false;

            SyncHiddenDeformProvider(p);
            p.pendingDeformSrc?.Dispose();
            p.pendingDeformSrc = AcquireDeformedSource(p);
            if (p.pendingDeformSrc == null)
            {
                p.deformMissFrames++;
                if (!p.extractWarned && p.deformMissFrames > 3)
                {
                    p.extractWarned = true;
                    Debug.LogError($"[BrazeFX] Surface Disintegrator could not access '{(p.target != null ? p.target.name : "?")}' GPU vertex buffer; the GPU deformation path cannot run.");
                }
                return false;
            }
            p.deformMissFrames = 0;

            Matrix4x4 m = DeformSourceToWorld(p);
            extractCS.SetBuffer(kExtract, "_Src", p.pendingDeformSrc);
            extractCS.SetBuffer(kExtract, "_OutPos", p.deformedPosBuf);
            extractCS.SetBuffer(kExtract, "_OutNrm", p.deformedNrmBuf);
            extractCS.SetInt("_VertCount", p.origVertCount);
            extractCS.SetInt("_DstBase", 0);
            extractCS.SetInt("_Stride", p.srcStride);
            extractCS.SetInt("_PosOffset", p.posOff);
            extractCS.SetInt("_NrmOffset", p.nrmOff);
            extractCS.SetInt("_HasNrm", p.hasNrm ? 1 : 0);
            extractCS.SetMatrix("_LocalToWorld", m);
            extractCS.SetMatrix("_NormalMatrix", m.inverse.transpose);
            DispatchExtract(p.origVertCount);
            return true;
        }

        GraphicsBuffer AcquireDeformedSource(Part p)
        {
            if (p.deformSmr != null) return p.deformSmr.GetVertexBuffer();
            if (p.smr != null) return p.smr.GetVertexBuffer();
            if (p.mf == null || p.mf.sharedMesh == null) return null;
            p.mf.sharedMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            return p.mf.sharedMesh.GetVertexBuffer(0);
        }

        Matrix4x4 DeformSourceToWorld(Part p)
        {
            var src = p.deformSmr != null ? p.deformSmr : p.smr;
            if (src != null && src.rootBone != null) return src.rootBone.localToWorldMatrix;
            return p.target != null ? p.target.transform.localToWorldMatrix : transform.localToWorldMatrix;
        }

        Bounds ActiveBounds(Part p)
        {
            if (p.isStatic) return p.staticWorldBounds;
            if (p.deformSmr != null) return p.deformSmr.bounds;
            if (p.target != null) return p.target.bounds;
            return new Bounds(transform.position, Vector3.one);
        }

        void DispatchExtract(int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            extractCS.SetInt("_RowThreads", gx * 64);
            extractCS.Dispatch(kExtract, gx, gy, 1);
        }

        void ParseDeformLayout(Part p, Mesh mesh)
        {
            p.layoutOK = false;
            if (mesh == null) return;
            int posStream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            if (posStream < 0) return;
            p.layoutOK = true;
            p.srcStride = mesh.GetVertexBufferStride(posStream);
            p.posOff = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            p.hasNrm = mesh.HasVertexAttribute(VertexAttribute.Normal)
                     && mesh.GetVertexAttributeStream(VertexAttribute.Normal) == posStream;
            p.nrmOff = p.hasNrm ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : 0;
            if (mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                Debug.LogError($"[BrazeFX] '{mesh.name}' position stream is not Float32. The GPU deformation path requires Float32 positions.");
        }

        // ── merge many static renderers into one world-space mesh (submesh per material) ──
        Mesh BuildCombinedStaticMesh(List<Renderer> statics, out Material[] materials)
        {
            materials = null;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var matToSub = new Dictionary<Material, int>();
            var matOrder = new List<Material>();
            var subTris = new List<List<int>>();
            int nullSub = -1;

            for (int ri = 0; ri < statics.Count; ri++)
            {
                var r = statics[ri];
                var filt = r != null ? r.GetComponent<MeshFilter>() : null;
                var mesh = filt != null ? filt.sharedMesh : null;
                if (mesh == null) continue;

                Matrix4x4 l2w = r.transform.localToWorldMatrix;
                Matrix4x4 n2w = l2w.inverse.transpose;
                var mv = mesh.vertices; var mn = mesh.normals; var mu = mesh.uv;
                bool hasN = mn != null && mn.Length == mv.Length;
                bool hasUV = mu != null && mu.Length == mv.Length;
                int baseV = verts.Count;
                for (int v = 0; v < mv.Length; v++)
                {
                    verts.Add(l2w.MultiplyPoint3x4(mv[v]));
                    norms.Add(hasN ? n2w.MultiplyVector(mn[v]).normalized : Vector3.up);
                    uvs.Add(hasUV ? mu[v] : Vector2.zero);
                }

                var rmats = r.sharedMaterials;
                int sub = Mathf.Max(1, mesh.subMeshCount);
                for (int s = 0; s < sub; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                    Material mat = rmats != null && rmats.Length > 0 ? rmats[Mathf.Min(s, rmats.Length - 1)] : null;
                    int si;
                    if (mat == null)
                    {
                        if (nullSub < 0) { nullSub = matOrder.Count; matOrder.Add(null); subTris.Add(new List<int>()); }
                        si = nullSub;
                    }
                    else if (!matToSub.TryGetValue(mat, out si))
                    {
                        si = matOrder.Count; matToSub[mat] = si; matOrder.Add(mat); subTris.Add(new List<int>());
                    }
                    var tris = mesh.GetTriangles(s);
                    var dst = subTris[si];
                    for (int t = 0; t < tris.Length; t++) dst.Add(tris[t] + baseV);
                }
            }

            if (verts.Count == 0 || matOrder.Count == 0) return null;

            var combined = new Mesh
            {
                name = "BrazeDisintegratorCombinedStatic",
                hideFlags = HideFlags.DontSave,
                indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            combined.SetVertices(verts);
            combined.SetNormals(norms);
            combined.SetUVs(0, uvs);
            combined.subMeshCount = matOrder.Count;
            for (int s = 0; s < subTris.Count; s++) combined.SetTriangles(subTris[s], s);
            combined.RecalculateBounds();
            materials = matOrder.ToArray();
            return combined;
        }

        // ── build the shattered mesh (split at clump boundaries, welded within clump) ───
        void BuildShattered(Part p, Mesh mesh)
        {
            Vector3[] rest = mesh.vertices, nrm = mesh.normals;
            Vector2[] uv = mesh.uv;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            var submeshTris = new int[subMeshCount][];
            int triCount = 0;
            for (int s = 0; s < subMeshCount; s++)
            {
                submeshTris[s] = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : System.Array.Empty<int>();
                triCount += submeshTris[s].Length / 3;
            }
            p.origVertCount = rest.Length;
            bool hasN = nrm != null && nrm.Length == p.origVertCount;
            bool hasUV = uv != null && uv.Length == p.origVertCount;

            // per-triangle clump id (centroid cell)
            var cellToClump = new Dictionary<Vector3Int, int>(triCount);
            var triSubmesh = new List<int>(triCount);
            var triStart = new List<int>(triCount);
            var triClump = new List<int>(triCount);
            for (int s = 0; s < subMeshCount; s++)
            {
                int[] tris = submeshTris[s];
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 cen = (rest[tris[t]] + rest[tris[t + 1]] + rest[tris[t + 2]]) / 3f;
                    var cell = ClumpCell(cen);
                    if (!cellToClump.TryGetValue(cell, out int id)) { id = cellToClump.Count; cellToClump[cell] = id; }
                    triSubmesh.Add(s);
                    triStart.Add(t);
                    triClump.Add(id);
                }
            }
            p.clumpCount = Mathf.Max(1, cellToClump.Count);

            var triByClump = new List<int>[p.clumpCount];
            for (int c = 0; c < p.clumpCount; c++) triByClump[c] = new List<int>();
            for (int t = 0; t < triClump.Count; t++) triByClump[triClump[t]].Add(t);

            var sPos = new List<Vector3>(); var sNrm = new List<Vector3>();
            var sUV0 = new List<Vector2>(); var sUV1 = new List<Vector2>(); var sOrig = new List<int>();
            var sTris = new List<int>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++) sTris[s] = new List<int>();
            p.clumpVertStart = new int[p.clumpCount]; p.clumpVertCount = new int[p.clumpCount];
            var weld = new Dictionary<int, int>();
            for (int c = 0; c < p.clumpCount; c++)
            {
                weld.Clear();
                p.clumpVertStart[c] = sPos.Count;
                foreach (int triIndex in triByClump[c])
                {
                    int submesh = triSubmesh[triIndex];
                    int[] tris = submeshTris[submesh];
                    int t = triStart[triIndex];
                    for (int k = 0; k < 3; k++)
                    {
                        int oi = tris[t + k];
                        if (!weld.TryGetValue(oi, out int si))
                        {
                            si = sPos.Count; weld[oi] = si;
                            sPos.Add(rest[oi]); sNrm.Add(hasN ? nrm[oi] : Vector3.up);
                            sUV0.Add(hasUV ? uv[oi] : Vector2.zero);
                            sUV1.Add(new Vector2(c, oi)); sOrig.Add(oi);
                        }
                        sTris[submesh].Add(si);
                    }
                }
                p.clumpVertCount[c] = sPos.Count - p.clumpVertStart[c];
            }

            p.shatteredVertCount = sPos.Count;
            p.shattered = new Mesh
            {
                hideFlags = HideFlags.DontSave,
                indexFormat = p.shatteredVertCount > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32
                                                           : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            p.shattered.SetVertices(sPos);
            p.shattered.SetNormals(sNrm);
            p.shattered.SetUVs(0, sUV0);
            p.shattered.SetUVs(1, sUV1);
            p.shattered.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++) p.shattered.SetTriangles(sTris[s], s);
            p.shattered.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);   // never frustum-cull (shader places verts)
            p.shatteredIdsData = new GpuUInt2[p.shatteredVertCount];               // (clumpId, origIndex) per vert — for release/hit compute
            for (int i = 0; i < p.shatteredVertCount; i++)
                p.shatteredIdsData[i] = new GpuUInt2((int)sUV1[i].x, (int)sUV1[i].y);

            p.clumpRangeData = new GpuUInt2[p.clumpCount];
            p.clumpPosStateArr = new Vector4[p.clumpCount]; p.clumpRotArr = new Vector4[p.clumpCount];
            p.clumpLifeArr = new Vector4[p.clumpCount]; p.clumpVelArr = new Vector4[p.clumpCount]; p.clumpAngVelArr = new Vector4[p.clumpCount];
            for (int c = 0; c < p.clumpCount; c++)
            {
                p.clumpRangeData[c] = new GpuUInt2(p.clumpVertStart[c], p.clumpVertCount[c]);
                p.clumpRotArr[c] = new Vector4(0f, 0f, 0f, 1f);
                p.clumpLifeArr[c] = new Vector4(1f, 0f, 0f, 0f);
            }
            ParseDeformLayout(p, mesh);

            AllocBuffers(p);
            UploadClumpStateBuffers(p);
            BuildMaterials(p);
        }

        void AllocBuffers(Part p)
        {
            ReleasePartBuffers(p);
            p.clumpPosStateBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(float) * 4);
            p.clumpRotBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(float) * 4);
            p.clumpLifeBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(float) * 4);
            p.clumpVelBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(float) * 4);
            p.clumpAngVelBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(float) * 4);
            p.deformedPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.origVertCount, sizeof(float) * 3);
            p.deformedNrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.origVertCount, sizeof(float) * 3);
            p.freeOffsetBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.shatteredVertCount, sizeof(float) * 3);
            p.freeNrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.shatteredVertCount, sizeof(float) * 3);
            p.clumpRangeBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(int) * 2);
            p.clumpRangeBuf.SetData(p.clumpRangeData);

            p.clumpHitBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.clumpCount, sizeof(uint));
            p.shatteredIdsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.shatteredVertCount, sizeof(uint) * 2);
            p.shatteredIdsBuf.SetData(p.shatteredIdsData);
        }

        void BuildMaterials(Part p)  // (kept name distinct to avoid clashing with Unity's reserved names)
        {
            var sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/Chunk");
            if (sh == null) { Debug.LogError("[BrazeFX] Shader 'BrazeFX/Chunk' not found."); return; }
            DestroyChunkMaterials(p);

            Material[] srcMats = p.overrideMaterials != null ? p.overrideMaterials
                               : (p.target != null ? p.target.sharedMaterials : null);
            int count = Mathf.Max(1, p.shattered != null ? p.shattered.subMeshCount : 1);
            p.chunkMats = new Material[count];
            for (int i = 0; i < count; i++)
            {
                Material srcMat = null;
                if (srcMats != null && srcMats.Length > 0) srcMat = srcMats[Mathf.Min(i, srcMats.Length - 1)];
                var mat = new Material(sh) { hideFlags = HideFlags.DontSave };
                CopySourceSurfaceProperties(srcMat, mat);
                mat.SetFloat(IdAmbient, 0f);   // faithful PBR — no artificial light floor
                BindChunkBuffers(p, mat);
                p.chunkMats[i] = mat;
            }
        }

        void CopySourceSurfaceProperties(Material src, Material dst)
        {
            if (dst == null) return;

            // defaults (unlit-ish neutral if there's no source to read from)
            dst.SetColor(IdColor, Color.white);
            dst.SetFloat(IdMetallic, 0f);
            dst.SetFloat(IdSmoothness, 0.5f);
            dst.SetFloat(IdBumpScale, 1f);
            dst.SetFloat(IdOcclusionStrength, 1f);
            dst.SetColor(IdEmissionColor, Color.black);
            dst.SetFloat(IdHasMetallicMap, 0f);
            dst.SetFloat(IdHasBump, 0f);
            dst.SetFloat(IdHasOcclusionMap, 0f);
            dst.SetFloat(IdHasEmissionMap, 0f);
            if (src == null) return;

            // albedo map (URP _BaseMap / Standard _MainTex)
            if (src.HasProperty(IdBaseMap) && src.GetTexture(IdBaseMap) != null)
                CopyTex(src, IdBaseMap, dst, IdMainTex);
            else if (src.HasProperty(IdMainTex) && src.GetTexture(IdMainTex) != null)
                CopyTex(src, IdMainTex, dst, IdMainTex);
            else if (src.mainTexture != null)
                dst.SetTexture(IdMainTex, src.mainTexture);

            // base colour
            if (src.HasProperty(IdBaseColor)) dst.SetColor(IdColor, src.GetColor(IdBaseColor));
            else if (src.HasProperty(IdColor)) dst.SetColor(IdColor, src.GetColor(IdColor));

            // metallic / smoothness (URP stores smoothness in _Smoothness; Standard uses _Glossiness)
            if (src.HasProperty(IdMetallic)) dst.SetFloat(IdMetallic, src.GetFloat(IdMetallic));
            if (src.HasProperty(IdSmoothness)) dst.SetFloat(IdSmoothness, src.GetFloat(IdSmoothness));
            else if (src.HasProperty(IdGlossiness)) dst.SetFloat(IdSmoothness, src.GetFloat(IdGlossiness));
            if (src.HasProperty(IdMetallicGlossMap) && src.GetTexture(IdMetallicGlossMap) != null)
            {
                CopyTex(src, IdMetallicGlossMap, dst, IdMetallicGlossMap);
                dst.SetFloat(IdHasMetallicMap, 1f);
            }

            // normal map
            if (src.HasProperty(IdBumpMap) && src.GetTexture(IdBumpMap) != null)
            {
                CopyTex(src, IdBumpMap, dst, IdBumpMap);
                if (src.HasProperty(IdBumpScale)) dst.SetFloat(IdBumpScale, src.GetFloat(IdBumpScale));
                dst.SetFloat(IdHasBump, 1f);
            }

            // occlusion
            if (src.HasProperty(IdOcclusionMap) && src.GetTexture(IdOcclusionMap) != null)
            {
                CopyTex(src, IdOcclusionMap, dst, IdOcclusionMap);
                if (src.HasProperty(IdOcclusionStrength)) dst.SetFloat(IdOcclusionStrength, src.GetFloat(IdOcclusionStrength));
                dst.SetFloat(IdHasOcclusionMap, 1f);
            }

            // emission
            if (src.HasProperty(IdEmissionColor)) dst.SetColor(IdEmissionColor, src.GetColor(IdEmissionColor));
            if (src.HasProperty(IdEmissionMap) && src.GetTexture(IdEmissionMap) != null)
            {
                CopyTex(src, IdEmissionMap, dst, IdEmissionMap);
                dst.SetFloat(IdHasEmissionMap, 1f);
            }
        }

        static void CopyTex(Material src, int srcId, Material dst, int dstId)
        {
            dst.SetTexture(dstId, src.GetTexture(srcId));
            dst.SetTextureScale(dstId, src.GetTextureScale(srcId));
            dst.SetTextureOffset(dstId, src.GetTextureOffset(srcId));
        }

        void BindChunkBuffers(Part p, Material mat)
        {
            if (mat == null) return;
            mat.SetBuffer(IdClumpPosState, p.clumpPosStateBuf);
            mat.SetBuffer(IdClumpRot, p.clumpRotBuf);
            mat.SetBuffer(IdClumpLife, p.clumpLifeBuf);
            mat.SetBuffer(IdDeformedPos, p.deformedPosBuf);
            mat.SetBuffer(IdDeformedNrm, p.deformedNrmBuf);
            mat.SetBuffer(IdFreeOffset, p.freeOffsetBuf);
            mat.SetBuffer(IdFreeNrm, p.freeNrmBuf);
            // Chunk.shader is shared with Voronoi. Bind valid float4 buffers even though this component leaves the
            // effector branch disabled, which keeps strict graphics backends (notably Metal) free of unbound SRVs.
            mat.SetBuffer(IdClumpCentroid, p.clumpPosStateBuf);
            mat.SetBuffer(IdEffectorPosition, p.clumpPosStateBuf);
            mat.SetBuffer(IdEffectorRotation, p.clumpRotBuf);
            mat.SetBuffer(IdEffectorScale, p.clumpLifeBuf);
            mat.SetInt(IdEffectorCount, 0);
            mat.SetMatrix(IdL2W, Matrix4x4.identity);
            mat.SetMatrix(IdN2W, Matrix4x4.identity);
        }

        void ApplyChunkMaterialState(Part p)
        {
            if (p.chunkMats == null) return;
            for (int i = 0; i < p.chunkMats.Length; i++)
            {
                var mat = p.chunkMats[i];
                if (mat == null) continue;
                mat.SetMatrix(IdL2W, Matrix4x4.identity);
                mat.SetMatrix(IdN2W, Matrix4x4.identity);
                mat.SetFloat(IdSoftAmount, softBody ? softness : 0f);
                mat.SetFloat(IdDissolveScale, dissolveScale);
                mat.SetColor(IdDissolveEdge, dissolveEdge);
                mat.SetFloat(IdDrawMode, 0f);
            }
        }

        void DestroyChunkMaterials(Part p)
        {
            if (p.chunkMats == null) return;
            for (int i = 0; i < p.chunkMats.Length; i++)
            {
                if (p.chunkMats[i] != null) DestroyImmediateSafe(p.chunkMats[i]);
            }
            p.chunkMats = null;
        }

        void UploadClumpStateBuffers(Part p)
        {
            p.clumpPosStateBuf?.SetData(p.clumpPosStateArr);
            p.clumpRotBuf?.SetData(p.clumpRotArr);
            p.clumpLifeBuf?.SetData(p.clumpLifeArr);
            p.clumpVelBuf?.SetData(p.clumpVelArr);
            p.clumpAngVelBuf?.SetData(p.clumpAngVelArr);
        }

        // Returns true if the buffer was (re)allocated, so the caller re-uploads even when the content hash matches.
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

        int GatherGpuForces()
        {
            forceScratch.Clear();
            if (forces != null && forces.Count > 0)
            {
                for (int i = 0; i < forces.Count; i++)
                {
                    var f = forces[i];
                    if (f != null && f.isActiveAndEnabled && !forceScratch.Contains(f)) forceScratch.Add(f);
                }
            }
            else
            {
                var all = BrazeParticleForce.Active;
                for (int i = 0; i < all.Count; i++)
                {
                    var f = all[i];
                    if (f != null && f.isActiveAndEnabled && !forceScratch.Contains(f)) forceScratch.Add(f);
                }
            }

            int count = forceScratch.Count;
            int uploadCount = Mathf.Max(1, count);
            if (forceData.Length < uploadCount) forceData = new GpuParticleForce[Mathf.NextPowerOfTwo(uploadCount)];
            if (count == 0) forceData[0] = default;
            else
                for (int i = 0; i < count; i++) forceData[i] = forceScratch[i].ToGpu(i);

            if (forceBuf == null || forceBuf.count < uploadCount)
            {
                forceBuf?.Release();
                forceBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(uploadCount), GpuParticleForce.Stride);
            }
            forceBuf.SetData(forceData, 0, 0, uploadCount);

            // Skip the 256×count curve rebake + full upload when the curves and buffer are unchanged (see FalloffAtlasHash).
            bool realloc = EnsureFalloffBuf(uploadCount);
            int hash = BrazeParticleForce.FalloffAtlasHash(forceScratch, count);
            if (realloc || hash != falloffHash)
            {
                for (int r = 0; r < uploadCount; r++)
                {
                    var c = r < count ? forceScratch[r].falloff : null;
                    int baseI = r * 256;
                    for (int x = 0; x < 256; x++)
                        falloffData[baseI + x] = c != null ? c.Evaluate(x / 255f) : 0f;
                }
                forceFalloffBuf.SetData(falloffData, 0, 0, uploadCount * 256);
                falloffHash = hash;
            }
            return count;
        }

        int GatherGpuColliders()
        {
            colliderScratch.Clear();
            var all = BrazeParticleCollider.Active;
            BrazeRuntimeRegistry.PruneNulls(all);
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || !c.isActiveAndEnabled || c.shape == BrazeParticleCollider.Shape.Mesh) continue;
                colliderScratch.Add(c);
            }

            int count = colliderScratch.Count;
            int uploadCount = Mathf.Max(1, count);
            if (colliderData.Length < uploadCount) colliderData = new GpuParticleCollider[Mathf.NextPowerOfTwo(uploadCount)];
            if (count == 0) colliderData[0] = default;
            else
                for (int i = 0; i < count; i++) colliderData[i] = colliderScratch[i].ToGpu();

            if (colliderBuf == null || colliderBuf.count < uploadCount)
            {
                colliderBuf?.Release();
                colliderBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(uploadCount), GpuParticleCollider.Stride);
            }
            colliderBuf.SetData(colliderData, 0, 0, uploadCount);
            return count;
        }

        void EnsureDebrisDummies()
        {
            debrisReqDummy ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, SpawnRequestStride);
            if (debrisReqCountDummy == null)
            {
                debrisReqCountDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                debrisReqCountDummy.SetData(new uint[] { 0 });
            }
        }

        bool DebrisEmitterReady =>
            debrisEmitter != null && debrisEmitter.isActiveAndEnabled && debrisEmitter.Ready && maxDebrisPoints > 0;

        void BindDebrisRequests(int kernel, int requestCount, float requestRate)
        {
            requestCount = Mathf.Max(0, requestCount);
            requestRate = Mathf.Max(0f, requestRate);
            bool emitDebris = DebrisEmitterReady && (requestCount > 0 || requestRate > 0f);
            if (emitDebris)
            {
                int parity = Time.frameCount & 1;
                int requestCap = Mathf.Max(0, Mathf.Min(maxDebrisPoints, debrisEmitter.IncomingRequestCapacity));
                simCS.SetBuffer(kernel, "_DebrisRequests", debrisEmitter.IncomingReqBuf(parity));
                simCS.SetBuffer(kernel, "_DebrisReqCount", debrisEmitter.IncomingReqCount(parity));
                simCS.SetInt("_DebrisReqCapacity", debrisEmitter.IncomingRequestCapacity);
                simCS.SetInt("_DebrisMaxRequests", requestCap);
                simCS.SetInt("_DebrisCount", requestCount);
                simCS.SetFloat("_DebrisRate", requestRate);
                simCS.SetFloat("_DebrisSpawnProbability", 1f); // Voronoi supports per-emitter thinning; this path is full-rate.
                simCS.SetInt("_DebrisSpawnSalt", 0);           // preserve the legacy single-emitter random sequence
                simCS.SetFloat("_DebrisVelocityInherit", Mathf.Max(0f, debrisInheritVelocity));
                simCS.SetFloat("_DebrisScatter", Mathf.Max(0f, debrisScatter));
                return;
            }

            EnsureDebrisDummies();
            simCS.SetBuffer(kernel, "_DebrisRequests", debrisReqDummy);
            simCS.SetBuffer(kernel, "_DebrisReqCount", debrisReqCountDummy);
            simCS.SetInt("_DebrisReqCapacity", 0);
            simCS.SetInt("_DebrisMaxRequests", 0);
            simCS.SetInt("_DebrisCount", 0);
            simCS.SetFloat("_DebrisRate", 0f);
            simCS.SetFloat("_DebrisSpawnProbability", 0f);
            simCS.SetInt("_DebrisSpawnSalt", 0);
            simCS.SetFloat("_DebrisVelocityInherit", 0f);
            simCS.SetFloat("_DebrisScatter", 0f);
        }

        void RunGpuRelease(Part p, int mode, Vector3 center, float radius)
        {
            if (simCS == null || p.clumpCount <= 0 || p.clumpPosStateBuf == null || p.clumpRangeBuf == null || p.clumpHitBuf == null) return;

            simCS.SetInt("_ClumpCount", p.clumpCount);
            simCS.SetInt("_ReleaseMode", mode);
            simCS.SetVector("_ReleaseCenterRadius", new Vector4(center.x, center.y, center.z, Mathf.Max(0f, radius)));
            simCS.SetVector("_BodyVel", p.bodyVel);
            simCS.SetFloat("_ReleaseImpulseMin", releaseImpulse);
            simCS.SetFloat("_ReleaseImpulse", releaseImpulse);
            simCS.SetFloat("_ReleaseSpinMin", 0f);
            simCS.SetFloat("_ReleaseSpin", releaseSpin);
            simCS.SetFloat("_InheritBodyVelocity", inheritBodyVelocity);
            simCS.SetMatrix("_L2W", Matrix4x4.identity);
            simCS.SetMatrix("_N2W", Matrix4x4.identity);

            simCS.SetBuffer(kReleaseClumps, "_ClumpPosState", p.clumpPosStateBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpRot", p.clumpRotBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpLife", p.clumpLifeBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpVel", p.clumpVelBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpAngVel", p.clumpAngVelBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpRange", p.clumpRangeBuf);
            simCS.SetBuffer(kReleaseClumps, "_ShatteredIds", p.shatteredIdsBuf);
            simCS.SetBuffer(kReleaseClumps, "_DeformedPos", p.deformedPosBuf);
            simCS.SetBuffer(kReleaseClumps, "_DeformedNrm", p.deformedNrmBuf);
            simCS.SetBuffer(kReleaseClumps, "_FreeOffset", p.freeOffsetBuf);
            simCS.SetBuffer(kReleaseClumps, "_FreeNrm", p.freeNrmBuf);
            simCS.SetBuffer(kReleaseClumps, "_ClumpHit", p.clumpHitBuf);

            simCS.Dispatch(kReleaseClumps, Mathf.CeilToInt(p.clumpCount / 64f), 1, 1);
            RunGpuDebrisFromReleased(p);
        }

        void RunGpuChunkedRelease(Part p, int partIndex)
        {
            if (simCS == null || p.clumpCount <= 0 || p.clumpPosStateBuf == null || p.clumpHitBuf == null) return;

            simCS.SetInt("_ClumpCount", p.clumpCount);
            simCS.SetFloat("_ChunkedReleaseProgress", chunkedReleaseProgress);
            int partSeed = unchecked(chunkedReleaseSeed ^ (int)((uint)partIndex * 0x9E3779B1u));
            simCS.SetInt("_ChunkedReleaseSeed", partSeed);
            simCS.SetBuffer(kMarkChunkedHits, "_ClumpPosState", p.clumpPosStateBuf);
            simCS.SetBuffer(kMarkChunkedHits, "_ClumpHit", p.clumpHitBuf);
            simCS.Dispatch(kMarkChunkedHits, Mathf.CeilToInt(p.clumpCount / 64f), 1, 1);

            RunGpuRelease(p, 0, ClumpsCenterWorld(p), 0f);
        }

        void RunGpuDebrisFromReleased(Part p)
        {
            simCS.SetInt("_ClumpCount", p.clumpCount);
            simCS.SetBuffer(kDebrisFromReleased, "_ClumpHit", p.clumpHitBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_ClumpRange", p.clumpRangeBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_ClumpPosStateRead", p.clumpPosStateBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_ClumpVelRead", p.clumpVelBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_ClumpAngVelRead", p.clumpAngVelBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_FreeOffsetRead", p.freeOffsetBuf);
            simCS.SetBuffer(kDebrisFromReleased, "_FreeNrmRead", p.freeNrmBuf);
            BindDebrisRequests(kDebrisFromReleased, debrisFromShrinkingClumps ? 0 : debrisPointsPerChunk, 0f);

            simCS.Dispatch(kDebrisFromReleased, Mathf.CeilToInt(p.clumpCount / 64f), 1, 1);
        }

        void RunGpuDebrisFromFreeClumps(Part p, float dt)
        {
            if (!debrisFromShrinkingClumps || !DebrisEmitterReady || debrisRatePerChunk <= 0f || dt <= 0f) return;
            if (simCS == null || p.clumpCount <= 0 || p.clumpPosStateBuf == null || p.clumpRotBuf == null || p.clumpLifeBuf == null ||
                p.clumpVelBuf == null || p.clumpAngVelBuf == null || p.clumpRangeBuf == null || p.freeOffsetBuf == null || p.freeNrmBuf == null) return;

            simCS.SetInt("_ClumpCount", p.clumpCount);
            simCS.SetFloat("_Dt", dt);
            simCS.SetFloat("_ClumpLifetime", clumpLifetime);
            simCS.SetFloat("_Shrink", shrink);
            simCS.SetInt("_FrameSeed", unchecked((int)((uint)Time.frameCount * 2654435761u)));
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpRange", p.clumpRangeBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpPosStateRead", p.clumpPosStateBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpRotRead", p.clumpRotBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpLifeRead", p.clumpLifeBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpVelRead", p.clumpVelBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_ClumpAngVelRead", p.clumpAngVelBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_FreeOffsetRead", p.freeOffsetBuf);
            simCS.SetBuffer(kDebrisFromFreeClumps, "_FreeNrmRead", p.freeNrmBuf);
            BindDebrisRequests(kDebrisFromFreeClumps, 0, debrisRatePerChunk);

            simCS.Dispatch(kDebrisFromFreeClumps, Mathf.CeilToInt(p.clumpCount / 64f), 1, 1);
        }

        void RunGpuClumpSim(Part p, float dt)
        {
            if (simCS == null || p.clumpCount <= 0 || p.clumpPosStateBuf == null || p.clumpVelBuf == null) return;

            simCS.SetInt("_ClumpCount", p.clumpCount);
            simCS.SetFloat("_Dt", Mathf.Max(0f, dt));
            simCS.SetFloat("_TurbTime", Time.time);   // drives turbulence curl field so it evolves (no static sinks)
            simCS.SetFloat("_ChunkRadius", Mathf.Max(0.001f, clumpSize * 0.5f));
            simCS.SetFloat("_ClumpLifetime", clumpLifetime);
            simCS.SetFloat("_Shrink", shrink);
            simCS.SetFloat("_Dissolve", dissolve);
            simCS.SetFloat("_SleepDelay", 0f); // Surface Disintegrator has no separate contact/sleep pass.
            simCS.SetInt("_ForceCount", frameForceCount);
            simCS.SetInt("_ColliderCount", frameColliderCount);

            simCS.SetBuffer(kSimClumps, "_ClumpPosState", p.clumpPosStateBuf);
            simCS.SetBuffer(kSimClumps, "_ClumpRot", p.clumpRotBuf);
            simCS.SetBuffer(kSimClumps, "_ClumpLife", p.clumpLifeBuf);
            simCS.SetBuffer(kSimClumps, "_ClumpVel", p.clumpVelBuf);
            simCS.SetBuffer(kSimClumps, "_ClumpAngVel", p.clumpAngVelBuf);
            simCS.SetBuffer(kSimClumps, "_Forces", forceBuf);
            simCS.SetBuffer(kSimClumps, "_ForceFalloff", forceFalloffBuf);
            simCS.SetBuffer(kSimClumps, "_Colliders", colliderBuf);

            simCS.Dispatch(kSimClumps, Mathf.CeilToInt(p.clumpCount / 64f), 1, 1);

            if (emitToFluid) RequestEmitReadback(p);
        }

        // Async-read this part's clump position/life so the (CPU) fluid point path can emit from flying clumps.
        // One request in flight per part; ~1-frame latency, invisible for smoke. Managed arrays are safe even if
        // the GPU buffers are later freed (the callback writes to the readback's own copy).
        void RequestEmitReadback(Part p)
        {
            if (p.emitReadbackPending || p.clumpPosStateBuf == null || p.clumpLifeBuf == null || p.clumpVelBuf == null || p.clumpCount <= 0) return;
            if (p.emitPosReadback == null || p.emitPosReadback.Length != p.clumpCount)
            {
                p.emitPosReadback = new Vector4[p.clumpCount];
                p.emitLifeReadback = new Vector4[p.clumpCount];
                p.emitVelReadback = new Vector4[p.clumpCount];
            }
            p.emitReadbackPending = true;
            var pp = p;
            // Issue all three NOW (buffers are validated above). Do NOT nest — a chained request fires a frame later
            // when the next buffer may already be freed (rebuilt part) → null-buffer exception. A counter marks the
            // round complete when the last one lands.
            pp.emitReadbackRemaining = 3;
            AsyncGPUReadback.Request(pp.clumpPosStateBuf, req =>
            {
                if (!req.hasError && pp.emitPosReadback != null && pp.emitPosReadback.Length == pp.clumpCount)
                    req.GetData<Vector4>().CopyTo(pp.emitPosReadback);
                EmitReadbackDone(pp);
            });
            AsyncGPUReadback.Request(pp.clumpVelBuf, req =>
            {
                if (!req.hasError && pp.emitVelReadback != null && pp.emitVelReadback.Length == pp.clumpCount)
                    req.GetData<Vector4>().CopyTo(pp.emitVelReadback);
                EmitReadbackDone(pp);
            });
            AsyncGPUReadback.Request(pp.clumpLifeBuf, req =>
            {
                if (!req.hasError && pp.emitLifeReadback != null && pp.emitLifeReadback.Length == pp.clumpCount)
                    req.GetData<Vector4>().CopyTo(pp.emitLifeReadback);
                EmitReadbackDone(pp);
            });
        }

        static void EmitReadbackDone(Part pp)
        {
            if (--pp.emitReadbackRemaining > 0) return;
            pp.emitReadbackPending = false;
            pp.emitReadbackValid = true;
        }

        /// <summary>True when this disintegrator should feed fluid containers (a Container polls this each frame).</summary>
        public bool EmitToFluid => emitToFluid && isActiveAndEnabled;
        /// <summary>How strongly the emitted fume inherits each clump's velocity (0..1). Read by the Container.</summary>
        public float EmitVelocityInherit => Mathf.Clamp01(emitVelocityInherit);

        /// <summary>Append each released clump as a world-space fluid emission point + its (density,temp,fuel) channels
        /// + its world velocity (so the fume inherits the clump's motion). Channels fade with clump age per Cool With
        /// Age and get a Burst boost just after break-off. Called by <see cref="BrazeContainer"/>.</summary>
        public void CollectFluidPoints(List<Vector3> worldOut, List<Vector3> channelsOut, List<Vector3> velWorldOut)
        {
            if (!emitToFluid || (!emitTrail && !emitBurst)) return;
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var p = parts[pi];
                if (p == null || !p.emitReadbackValid || p.emitPosReadback == null || p.emitLifeReadback == null || p.emitVelReadback == null) continue;
                int n = Mathf.Min(p.clumpCount, Mathf.Min(p.emitPosReadback.Length, Mathf.Min(p.emitLifeReadback.Length, p.emitVelReadback.Length)));
                for (int c = 0; c < n; c++)
                {
                    Vector4 ps = p.emitPosReadback[c];
                    if (ps.w < 0.5f) continue;                 // not released
                    Vector4 life = p.emitLifeReadback[c];      // x = scale, w = age
                    if (life.x <= 0.001f) continue;            // shrunk / dissolved away
                    float age = life.w;

                    float burst = emitBurst ? 1f + (emitBurstScale - 1f) * Mathf.Clamp01(1f - age / emitBurstTime) : 1f;
                    if (!emitTrail && age > emitBurstTime) continue;   // burst-only mode: skip once the puff has elapsed

                    float heat = emitCoolWithAge > 0f
                        ? Mathf.Lerp(1f, 1f - emitCoolWithAge, Mathf.Clamp01(age / emitCoolTime))
                        : 1f;

                    float dens = emitDensity * burst;
                    float temp = emitTemperature * heat * burst;
                    float fuel = emitFuel * heat * burst;
                    if (dens <= 0f && temp <= 0f && fuel <= 0f) continue;

                    Vector4 vel = p.emitVelReadback[c];        // xyz = world units/sec
                    worldOut.Add(new Vector3(ps.x, ps.y, ps.z));
                    channelsOut.Add(new Vector3(dens, temp, fuel));
                    velWorldOut.Add(new Vector3(vel.x, vel.y, vel.z));
                }
            }
        }

        // ── hit detection (eroder particles → flag clumps → GPU release) ─────────────
        void HitDetect(Part p)
        {
            if (hitCS == null) return;

            // gather every live eroder this frame (skip null/disabled/empty).
            eroderScratch.Clear();
            if (eroders != null)
            {
                for (int i = 0; i < eroders.Count; i++)
                {
                    var e = eroders[i];
                    if (e != null && e.isActiveAndEnabled && e.ParticleBuffer != null && e.ParticleCapacity > 0
                        && !eroderScratch.Contains(e)) eroderScratch.Add(e);
                }
            }
            if (eroderScratch.Count == 0) return;

            // uniform grid over the body bounds; cell ≈ hit radius (capped so the grid fits HitMaxDim^3).
            Bounds b = ActiveBounds(p);
            float margin = hitRadius * 2f;
            Vector3 size = b.size + Vector3.one * (margin * 2f);
            float maxExt = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            float cell = Mathf.Max(hitRadius, maxExt / HitMaxDim);
            Vector3Int dim = new(
                Mathf.Clamp(Mathf.CeilToInt(size.x / cell), 1, HitMaxDim),
                Mathf.Clamp(Mathf.CeilToInt(size.y / cell), 1, HitMaxDim),
                Mathf.Clamp(Mathf.CeilToInt(size.z / cell), 1, HitMaxDim));
            Vector3 origin = b.center - size * 0.5f;
            int cellTotal = dim.x * dim.y * dim.z;

            hitCS.SetVector("_GridOrigin", origin);
            hitCS.SetFloat("_CellSize", cell);
            hitCS.SetInts("_GridDim", dim.x, dim.y, dim.z);
            hitCS.SetInt("_CellTotal", cellTotal);
            hitCS.SetInt("_ShatteredVertCount", p.shatteredVertCount);
            hitCS.SetInt("_ClumpCount", p.clumpCount);
            hitCS.SetFloat("_HitRadius", hitRadius);
            hitCS.SetMatrix("_L2W", Matrix4x4.identity);

            hitCS.SetBuffer(kClearGrid, "_CellNum", cellNumBuf); Dispatch(kClearGrid, cellTotal);
            hitCS.SetBuffer(kClearHits, "_ClumpHit", p.clumpHitBuf); Dispatch(kClearHits, p.clumpCount);

            // scatter EVERY eroder's particles into the shared grid (counts accumulate atomically).
            hitCS.SetBuffer(kScatter, "_CellNum", cellNumBuf);
            hitCS.SetBuffer(kScatter, "_CellBucket", cellBucketBuf);
            for (int i = 0; i < eroderScratch.Count; i++)
            {
                var e = eroderScratch[i];
                int cap = e.ParticleCapacity;
                hitCS.SetInt("_ParticleCapacity", cap);
                hitCS.SetBuffer(kScatter, "_Particles", e.ParticleBuffer);
                Dispatch(kScatter, cap);
            }

            hitCS.SetBuffer(kMarkHits, "_CellNum", cellNumBuf);
            hitCS.SetBuffer(kMarkHits, "_CellBucket", cellBucketBuf);
            hitCS.SetBuffer(kMarkHits, "_DeformedPos", p.deformedPosBuf);
            hitCS.SetBuffer(kMarkHits, "_ShatteredIds", p.shatteredIdsBuf);
            hitCS.SetBuffer(kMarkHits, "_ClumpHit", p.clumpHitBuf);
            Dispatch(kMarkHits, p.shatteredVertCount);

            RunGpuRelease(p, 0, ClumpsCenterWorld(p), 0f);
        }
        readonly List<BrazeParticleSystem> eroderScratch = new();

        void Dispatch(int kernel, int threads)
        {
            if (threads <= 0) return;
            hitCS.Dispatch(kernel, Mathf.CeilToInt(threads / 64f), 1, 1);
        }

        // ── release ────────────────────────────────────────────────────────────────────
        bool EnsureActiveForRelease()
        {
            if (!Application.isPlaying) return false;
            if (rebuildRequested)
            {
                rebuildRequested = false;
                if (active) Deactivate();
            }
            return active || Activate();
        }

        [ContextMenu("Test ▶ Release All")]
        [BrazeTimelineEvent("DESTRUCTION/Release All")]
        public void ReleaseAll()
        {
            if (!EnsureActiveForRelease()) return;
            chunkedReleaseActive = false;
            chunkedReleaseProgress = 1f;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (!p.sourceHiddenForDisintegration && !p.isStatic && !RefreshDeformedGpu(p)) continue;
                RunGpuRelease(p, 1, ClumpsCenterWorld(p), 0f);
                HideSourceRendererForTakeover(p);
            }
            InvokeStartedDisintegration();
            BeginFinishCountdown();
        }

        /// <summary>Release all still-anchored clumps over <see cref="releaseInChunksDuration"/> seconds in a stable random order.</summary>
        [ContextMenu("Test ▶ Release In Chunks")]
        [BrazeTimelineEvent("DESTRUCTION/Release In Chunks")]
        public void ReleaseInChunks() => ReleaseInChunks(releaseInChunksDuration);

        /// <summary>Release all still-anchored clumps over <paramref name="duration"/> seconds in a stable random order.</summary>
        public void ReleaseInChunks(float duration)
        {
            if (!EnsureActiveForRelease()) return;
            if (duration <= 0f)
            {
                ReleaseAll();
                return;
            }

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (!p.sourceHiddenForDisintegration && !p.isStatic) RefreshDeformedGpu(p);
            }

            chunkedReleaseActive = true;
            chunkedReleaseDurationRuntime = Mathf.Max(0.001f, duration);
            chunkedReleaseAge = 0f;
            chunkedReleaseProgress = 0f;
            chunkedReleaseSeed = unchecked(seed * 73856093 ^ Time.frameCount * 19349663);
            finishPending = false;
            finishInvoked = false;

            InvokeStartedDisintegration();
        }

        /// <summary>Release every still-anchored clump whose centre is within <paramref name="radius"/> of the point.</summary>
        public void ReleaseSphere(Vector3 centerWorld, float radius)
        {
            if (!EnsureActiveForRelease()) return;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (!p.sourceHiddenForDisintegration && !p.isStatic && !RefreshDeformedGpu(p)) continue;
                RunGpuRelease(p, 2, centerWorld, radius);
            }
        }

        Vector3 ClumpsCenterWorld(Part p) => ActiveBounds(p).center;

        // ── edit-mode preview (clump colours) ──────────────────────────────────────────
        Mesh previewBake;
        readonly List<Vector3> previewScratch = new();
        // per-renderer preview data, parallel to previewRenderers
        readonly List<Renderer> previewRenderers = new();
        int[][] previewVertClump;
        Vector3[][] previewWorld;
        Color[][] previewClumpColor;
        int previewBakeFrame = -1;
        object builtPreviewKey;
        int builtSeedP; float builtSizeP, builtIrregP;

        void OnDrawGizmos()
        {
            if (!showClumps || Application.isPlaying || !EnsurePreview()) return;
            BakePreview();
            Vector3 size = Vector3.one * dotSize;
            int budget = Mathf.Max(1, maxDots);
            int total = 0;
            for (int r = 0; r < previewWorld.Length; r++) total += previewWorld[r] != null ? previewWorld[r].Length : 0;
            int stride = Mathf.Max(1, Mathf.CeilToInt(total / (float)budget));
            for (int r = 0; r < previewWorld.Length; r++)
            {
                var pw = previewWorld[r]; var vc = previewVertClump[r]; var cols = previewClumpColor[r];
                if (pw == null) continue;
                for (int i = 0; i < pw.Length; i += stride)
                {
                    int cc = vc[i];
                    Gizmos.color = (uint)cc < (uint)cols.Length ? cols[cc] : Color.gray;
                    Gizmos.DrawCube(pw[i], size);
                }
            }
        }

        bool EnsurePreview()
        {
            GatherRenderers(previewRenderers);
            if (previewRenderers.Count == 0) return false;

            // cheap identity key: renderer count + first mesh + layout params
            Mesh firstTopo = TopoOf(previewRenderers[0]);
            object key = firstTopo;
            if (key == builtPreviewKey && builtSeedP == seed && builtSizeP == clumpSize && builtIrregP == irregularity
                && previewVertClump != null && previewVertClump.Length == previewRenderers.Count) return true;

            int n = previewRenderers.Count;
            previewVertClump = new int[n][];
            previewWorld = new Vector3[n][];
            previewClumpColor = new Color[n][];
            for (int r = 0; r < n; r++)
            {
                Mesh topo = TopoOf(previewRenderers[r]);
                if (topo == null) { previewVertClump[r] = System.Array.Empty<int>(); previewWorld[r] = System.Array.Empty<Vector3>(); previewClumpColor[r] = System.Array.Empty<Color>(); continue; }
                Vector3[] rest = topo.vertices;
                int vn = rest.Length;
                var vClump = new int[vn];
                var cellToClump = new Dictionary<Vector3Int, int>(vn);
                for (int i = 0; i < vn; i++)
                {
                    var cell = ClumpCell(rest[i]);
                    if (!cellToClump.TryGetValue(cell, out int id)) { id = cellToClump.Count; cellToClump[cell] = id; }
                    vClump[i] = id;
                }
                int cnt = cellToClump.Count;
                var cols = new Color[cnt];
                for (int c = 0; c < cnt; c++) cols[c] = Color.HSVToRGB((c * 0.61803399f) % 1f, 0.65f, 1f);
                previewVertClump[r] = vClump;
                previewWorld[r] = new Vector3[vn];
                previewClumpColor[r] = cols;
            }
            builtPreviewKey = key; builtSeedP = seed; builtSizeP = clumpSize; builtIrregP = irregularity;
            previewBakeFrame = -1;
            return true;
        }

        static Mesh TopoOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer s) return s.sharedMesh;
            var f = r != null ? r.GetComponent<MeshFilter>() : null;
            return f != null ? f.sharedMesh : null;
        }

        void BakePreview()
        {
            if (previewWorld == null) return;
            if (previewBakeFrame == Time.frameCount) return;
            previewBakeFrame = Time.frameCount;
            for (int r = 0; r < previewRenderers.Count; r++)
            {
                var rend = previewRenderers[r];
                var pw = previewWorld[r];
                if (pw == null) continue;
                Matrix4x4 l2w;
                if (rend is SkinnedMeshRenderer s)
                {
                    if (previewBake == null) previewBake = new Mesh { hideFlags = HideFlags.DontSave };
                    s.BakeMesh(previewBake, true); l2w = s.transform.localToWorldMatrix;
                    previewBake.GetVertices(previewScratch);
                }
                else
                {
                    var f = rend != null ? rend.GetComponent<MeshFilter>() : null;
                    if (f == null || f.sharedMesh == null) continue;
                    f.sharedMesh.GetVertices(previewScratch); l2w = rend.transform.localToWorldMatrix;
                }
                int vn = Mathf.Min(previewScratch.Count, pw.Length);
                for (int i = 0; i < vn; i++) pw[i] = l2w.MultiplyPoint3x4(previewScratch[i]);
            }
        }

        void ReleasePartBuffers(Part p)
        {
            p.pendingDeformSrc?.Dispose(); p.pendingDeformSrc = null;
            p.clumpPosStateBuf?.Release(); p.clumpPosStateBuf = null;
            p.clumpRotBuf?.Release(); p.clumpRotBuf = null;
            p.clumpLifeBuf?.Release(); p.clumpLifeBuf = null;
            p.clumpVelBuf?.Release(); p.clumpVelBuf = null;
            p.clumpAngVelBuf?.Release(); p.clumpAngVelBuf = null;
            p.deformedPosBuf?.Release(); p.deformedPosBuf = null;
            p.deformedNrmBuf?.Release(); p.deformedNrmBuf = null;
            p.freeOffsetBuf?.Release(); p.freeOffsetBuf = null;
            p.freeNrmBuf?.Release(); p.freeNrmBuf = null;
            p.clumpRangeBuf?.Release(); p.clumpRangeBuf = null;
            p.clumpHitBuf?.Release(); p.clumpHitBuf = null;
            p.shatteredIdsBuf?.Release(); p.shatteredIdsBuf = null;
        }

        void ReleaseSharedBuffers()
        {
            forceBuf?.Release(); forceBuf = null;
            colliderBuf?.Release(); colliderBuf = null;
            debrisReqDummy?.Release(); debrisReqDummy = null;
            debrisReqCountDummy?.Release(); debrisReqCountDummy = null;
            cellNumBuf?.Release(); cellNumBuf = null;
            cellBucketBuf?.Release(); cellBucketBuf = null;
        }

        static void DestroyTemp(ref Mesh m) { if (m == null) return; DestroyImmediateSafe(m); m = null; }
        static void DestroyImmediateSafe(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
    }
}
