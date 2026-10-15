using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// GPU fire/smoke solver. Owns the 3D RenderTextures and emitter/force buffers. Realtime model:
    /// allocate on construct, dispose on tear-down — no frame caching.
    /// </summary>
    public sealed class BrazeSolver : System.IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuEmitter
        {
            public Vector3 posVox;        public float radiusVox;
            public Vector3 channels;      public float shape;
            public Vector3 halfExtentVox; public float motionBuoyancy;  // per-emitter buoyancy (signed: + rise, − sink)
            public Vector3 motionDir;     public float motionForward;   // Push direction + magnitude
            public float motionRadial;    public float gasPressureBurst; public float gasSourceSpread; public float gasSourceSharpness;
            public Vector4 rot;                                          // emitter orientation in grid space (quaternion)
            public Vector3 motionJet;     public float sourceTurbulence; // inflow JET velocity (grid voxels/sec) + source-local jitter magnitude
            public Vector3 sourceVelocity; public float sourceVelocityBlend; // Pyro-style local-axis source velocity (grid voxels/sec) + blend
            public Vector3 sourceSwirlAxis; public float sourceSwirlSpeed; // tangential target axis + signed rim speed (grid voxels/sec)
            public float sourceDyePattern; public float sourceDyeStreakCount; public float sourceDyeStreakWidth; public float sourceDyePad;
        }
        public const int EmitterStride = 160;

        // Point-set emission (a deduped set of grid cells, e.g. a surface-coverage mask). Padded to 16-byte
        // member alignment (float3 + float) — HLSL won't let a struct member straddle a 16-byte boundary, so a
        // bare float3+float3 (24 B) would mis-map `channels`. Mirrors the GpuEmitter convention.
        [StructLayout(LayoutKind.Sequential)]
        public struct PointSource
        {
            public Vector3 posVox;     public float buoyBlend;  // voxel-space cell centre; authority to set _Vel.w (0 = leave untouched)
            public Vector3 channels;   public float buoyancy;   // density, temperature, fuel (already × strength); per-source buoyancy coeff
            public Vector3 vel;        public float velBlend;   // grid-space source velocity + how strongly to inherit it
        }
        public const int PointStride = 48;

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuForce
        {
            public Vector3 posVox;  public float type;
            public Vector3 dir;     public float strength;     // disc Spread; vortex/spinner angular rate where applicable
            public float radiusVox; public float thickness; public float scale; public float swirl;   // disc: half-height/swirl; spinner: swirl = Flatten rate (1/s)
            public float flatten;   public float distort;   public float roll;  public float hold;     // disc: flatten/distort/roll/hold
            public float dome;      public float power;     public float seal;  public float layers;   // disc: dome/power/seal/stack count
            public float layerSpacing; public float taper;  public Vector2 forcePad;                    // disc: x core/y suction; cauli: x smoke/y fire deposit; spinner: x Grip/y Containment rate (1/s)
            public float falloffInner; public float falloffPower; public float billowLines; public float billowMode;  // falloff core/curve (generic field); cauli: striation amount + motion mode (+1 up / 0 in-place / -1 down)
            public float shape; public float hole; public float fieldHeight; public float shapePad;  // field shape (0 sphere/1 cylinder/2 torus/3 disc) + central hole frac + cylinder/disc half-height (vox)
            public float billowStrength; public float billowOrigin; public float billowScale; public float billowSpeed;  // Cauli Billows: raw strength / center↔rim emphasis / lobe size in voxels / turnover rate
            public float vortexMode; public float vortexHold; public float vortexOrbitR; public float vortexLift;        // Vortex: 0 swirl/1 inward/2 orbit + hold response + orbit radius (vox) + axial lift (vox/s)
            public float vortexAxialDamp; public float vortexLiftVar; public float vortexGrip; public float vortexBlades; // Vortex: axial damp/lift variation (0..1) + grip rate (1/s) + blade count
        }
        public const int ForceStride = 176;   // 44 floats × 4 (incl. the Vortex motion/axial row + pad)

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuObstacle
        {
            public Vector3 posVox;        public float radiusVox;
            public Vector3 halfExtentVox; public float shape;   // 0 sphere, 1 box, 2 cylinder, 3 cone, 4 capsule, 5 torus
            public Vector4 rot;                                 // primitive orientation (local +Y = axial direction)
        }
        public const int ObstacleStride = 48;

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuCfdBoundary
        {
            public Vector3 centerVox; public float face;
            public Vector3 velocityVox; public float mode;
            public Vector2 halfSizeVox; public float gaugePressure; public float inletTemperature;
            public float cellVolumeWorld; public float preventBackflow; public float inletSmokeDensity; public float inletSmokePattern;
            public float shape; public float ringInner; public float rotationRadians; public float _pad;   // shape: 0 rect, 1 ellipse, 2 ring, 3 slot
        }
        public const int CfdBoundaryStride = 80;

        public struct FrameParams
        {
            public float dt, time, maxSpeed, velDis;
            public float resolutionScale;                    // current longest-axis res / authored physics reference res
            public float smokeDecayMul, fireDecayMul;       // 1/life — scales field decay (lower = longer-lived)
            public float smokeErode;                         // subtractive density erosion/sec → wisp-fade instead of woolly blob
            public float ambientLife;                        // gentle large-scale curl drift so smoke never freezes static
            public float drag;                              // emitter-relayed whole-plume velocity drag (buoyancy is per-emitter, in _Vel.w)
            public float confineScale, turbulence, turbScale, billow, billowScale;   // container-wide global dynamics
            public float instability, instabScale;          // baroclinic buoyancy perturbation (Rayleigh–Taylor seed)
            public float interfaceTurb, interfaceTurbScale; // gradient-gated edge turbulence (cauliflower)
            public float cfdInletTurbulence;                // RMS fraction of mean inlet speed (0 for legacy/Fire)
            public float cfdTurbulenceLengthVox;            // dominant synthetic-inlet eddy size in voxels
            public float cfdTurbulenceFrequency;            // temporal turnover frequency in cycles/second
            public float cfdRenderSpeedInvReference;        // CFD render-field B = normalized solved speed (0 disables)
            public float cfdContactDyeDensity;              // Body Contact dye release concentration (0 disables)
            public float cfdContactDyeSpeedInv;             // 1 / inlet speed (vox) — normalizes the release weighting
            public float cfdInletSpeedVox;                  // freestream speed for the Cp dynamic head (0 = neutral; Cp undefined)
            public float cfdThermalBuoyancy;                // CFD domain-wide buoyancy per heat unit (grid units; 0 = Fire)
            public float cfdLinearDrag;                     // CFD linear air damping in literal 1/s (0 = Fire quadratic path)
            // Render Detail transport — normalized 0..1 knobs; only Amount/Persistence plus Speed drive the live
            // transported fine field. The extra fields stay serialized for render-setting/cache compatibility.
            public float fineAmount;   // Amount: >1e-4 enables the fine field (visibility applied render-side)
            public float fineFeature;  // Legacy detail-noise control; unused by transported fine field
            public float fineSharp;    // Legacy detail-noise control; unused by transported fine field
            public float fineFlow;     // Legacy detail-noise control; unused by transported fine field
            public float fineQuality;  // Legacy detail-noise control; unused by transported fine field
            public int fineUpres;      // fine-field up-res factor: 2 or 4 (× base res); auto-clamped down by the VRAM cap
            public float fineMotion;   // Legacy detail-noise control; unused by transported fine field
            public float fineResponse; // Legacy detail-noise control; unused by transported fine field
            public float finePersist;  // Persistence 0..1: residual survives longer before coarse anchoring wins
            public float fineSpeed;    // Speed 0..3: multiplier on sim velocity for fine-field transport
            public float fineCrisp;    // Legacy detail-noise control; unused by transported fine field
            public float fineGrainMin; // Legacy detail-noise control; unused by transported fine field
            public float fineGrainMax; // Legacy detail-noise control; unused by transported fine field
            public float occThreshold;                       // brick cull: field-sum below this is skipped by the solve
            public int jacobiIters;
            public int substeps;                             // advection sub-steps/frame (CFL — keeps fast flow crisp)
            public int pressureCadence;                      // FULL pressure solve every Nth step (1 = every step)
            public int pressureTrickle;                      // warm-started iters on the between-steps (amortizes the solve so motion isn't lost in bursts; 0 = pure skip)
            public int closedFaces;                          // bitmask: 1/-X, 2/+X, 4/-Y, 8/+Y, 16/-Z, 32/+Z
        }

        /// <summary>The Render Detail knobs the CACHE-PLAYBACK path bakes into the fine field (mirrors the live
        /// container's per-step bake). The caller maps the render settings into this with the SAME formulas the
        /// container uses (see BrazeContainer's FrameParams fill), so live and cached detail render identically.</summary>
        public struct DetailBakeParams
        {
            public float amount, feature, flow, sharp, quality, motion, persist, speed, crisp, grainMin, grainMax, time;
            public int upres;   // fine-field up-res factor: 2 or 4 (× base res); auto-clamped down by the VRAM cap
        }

        readonly ComputeShader cs;
        readonly Vector3Int res;
        readonly Vector3Int groups;
        readonly bool playbackOnly;
        readonly bool playbackWarp;
        readonly bool playbackFlow;   // DetailFlow clip: warp channels scatter RAW into a flow anchor; the warp is re-baked at full scale
        readonly bool playbackVelocity;       // cache BASE vectors are authoritative velocity (voxels/sec)
        readonly bool playbackReconstruction; // scalar-only VDB fallback: infer motion from adjacent frames
        readonly bool playbackMotion;         // either authoritative or reconstructed renderer/fluid motion is resident
        readonly Vector3Int playbackWarpRes;
        readonly ComputeShader vdbPlaybackCs;
        readonly int kVdbPackSignal = -1, kVdbDownsampleSignal = -1;
        readonly int kVdbBuildMotionTerms = -1, kVdbRefineMotion = -1, kVdbTemporalBlend = -1, kVdbFinalizeMotion = -1;
        readonly int kEmit, kEmitPoints, kAdvectFields, kMac, kCurl, kConf, kCurlNoise, kForce, kMarkSolids, kApplyCfdSdf, kApplyCfdBoundariesFace, kClearCfdFlux, kMeasureCfdFluxFace, kClearCfdSurfaceLoad, kMeasureCfdSurfaceLoadWindow, kClearCfdTemperaturePeak, kMeasureCfdTemperaturePeak, kClearCfdRefPressure, kMeasureCfdRefPressure, kMeasureCfdStagnationPressure, kFinalizeCfdPressureCalibration, kCfdSeedContactDye, kCfdBuildCoarseSolid, kCfdRestrictResidual, kCfdCoarseRBGS, kCfdProlongAdd, kCfdSmoothPressureVis, kRestoreCfdStaticSolid, kApplyCfdBodySdf, kApplyCfdBodyMotion, kDiv, kDivCfd, kRBGS, kRBGSCfd, kProj, kProjCfd, kDecay, kFineSeed, kDetailBake, kAdvectFlow, kClearRetiredFlow, kFlowCurl, kBakeWarp, kClearRetiredWarpBricks, kClearWarpNeutral;
        readonly int[] sparseBaseKernels;   // base kernels that skip empty bricks (need _OccD bound per frame)
        readonly int kIllum, kScatterSeed, kScatterBlur, kFireLightSeed, kPackLight, kClearScalar, kClearScalar2, kClearLightScalar;
        readonly int kClearOcc, kMarkOcc, kMarkOccDense, kMarkEmitterOcc, kMarkPointOcc, kDilateOcc, kWriteArgs, kClearField, kClearInactiveFields, kClearRetiredBricks, kBuildRenderField, kBuildCacheRenderOcc, kBrickDebug;
        readonly int kSurfSplat, kSurfEmitFold, kClearSurfAccum;
        readonly int kMeshSplat, kFragmentSplat, kMeshEmitFold, kClearMeshAccum, kClearMeshMaskPass, kMeshMaskGate, kWriteMeshMaskDispatchArgs;
        readonly int kFireMoments;
        readonly int kCachePackBase, kCacheCountSparse, kCachePackSparse, kCacheScatterBase, kCacheVelocityMotion,
                     kCacheMotionBlend, kClearBricksFV,
                     kClearCacheWarpBricks, kMarkOccFromScatter;
        public const int OccDownscale = 4;   // base voxels per occupancy brick (occRes = ceil(res / OccDownscale))
        const int Brick = OccDownscale;       // base voxels per occupancy brick
        internal const int MaxDispatchGroupsPerAxis = 65535;
        internal const long MaxLinearDispatchGroups = uint.MaxValue / 64L;
        readonly Vector3Int occRes, occGroups;

        RenderTexture fieldsCur, fieldsNext, fieldsTmp, velCur, velNext, curl, div, pressure;
        RenderTexture solid;        // obstacle mask — LAZY, allocated on the first step with obstacles
        RenderTexture solidDummy;   // zeroed 4³ stand-in bound while solid == null (never read: _NoSolids gates SolidAt)
        RenderTexture SolidTex => solid != null ? solid : solidDummy;
        RenderTexture fineCur, fineNext;   // Render Detail transported field (RGHalf: R density, G burn)
        Vector3Int fineRes;
        int fineScale = 1;
        bool fineInitialized;
        RenderTexture flowField;           // accumulated render-warp displacement (flow map — DetailBake carrier)
        RenderTexture warpField;           // baked render-detail domain warp sampled by the slice renderer
        RenderTexture playbackNeighbourField, vdbMotionHistory;
        // Frame-skip motion smoothing (velocity clips): the key frame scatters here, the neighbour into
        // playbackNeighbourField, and CacheMotionBlend writes the interpolated result into fieldsCur. Lazy —
        // allocated only when a player actually blends, freed with the solver. cacheBlendReady goes false whenever
        // a classic scatter writes fieldsCur directly, so a stale key field can never leak into a blend.
        RenderTexture playbackKeyField;
        bool cacheBlendReady;
        // Coarse-to-fine optical-flow pyramid: [0] = full res, each further level halved per axis.
        const int VdbPyramidLevels = 3;
        readonly RenderTexture[] vdbSignal = new RenderTexture[VdbPyramidLevels];
        readonly RenderTexture[] vdbMotionTerms = new RenderTexture[VdbPyramidLevels];
        readonly RenderTexture[] vdbMotionA = new RenderTexture[VdbPyramidLevels];
        readonly RenderTexture[] vdbMotionB = new RenderTexture[VdbPyramidLevels];
        readonly Vector3Int[] vdbLevelRes = new Vector3Int[VdbPyramidLevels];
        int vdbLevelCount;
        int warpScale;
        Vector3Int warpGroups;
        RenderTexture occ, occD;
        // Render-only visual field (PING-PONG — writing renderCur in place stalls against the previous frame's
        // draws still sampling it; measured −50 FPS 2026-07-05) + max-pooled skip mask.
        RenderTexture renderCur, renderNext, renderOcc;
        // Grid-space lighting (ported): single-scatter transmittance + multiple-scattering radiance (+ ping)
        // + fire-as-a-light radiance (fire emission diffused through the smoke). Working buffers are scalar;
        // they're packed into one RGBA `lightPacked` volume so the renderer samples all three in one tap.
        RenderTexture illumField, scatterField, scatterTmp, fireLightField, lightPacked;
        // Lighting is diffuse / low-frequency → baked at a coarser grid (lightRes = res / lightDiv) for big VRAM +
        // compute savings. The light march keeps base-res world-step geometry; only the stored field is coarser.
        int lightDiv = 1;
        Vector3Int lightRes, lightGroups;
        /// <summary>Resolution divisor for the light bakes: 1 = base res, 2 = half (≈8× less VRAM + lighting
        /// compute, slightly softer shadows). Reallocates the light fields on change.</summary>
        public int LightDownsample { get => lightDiv; set => lightDiv = Mathf.Clamp(value, 1, 4); }
        /// <summary>Master switch for the grid-space lighting bake. False = skip DispatchLighting entirely (cheaper);
        /// the renderer also bypasses the (stale) light volume via its own toggle, so the smoke shades flat/ambient.</summary>
        public bool LightingEnabled { get; set; } = true;
        /// <summary>When false, the grid fire-light channel is cleared instead of seeded + blurred. Static
        /// non-emissive generated clouds use this to skip five dense light-grid passes with identical visual output.</summary>
        public bool GridFireLightEnabled { get; set; } = true;
        bool cfdThermalOccupancy;
        /// <summary>CFD-only occupancy policy. When enabled, temperature by itself keeps a sparse brick alive.</summary>
        public bool CfdThermalOccupancy
        {
            get => cfdThermalOccupancy;
            set
            {
                cfdThermalOccupancy = value;
                // ComputeShader parameters belong to the shared shader asset, not to this solver instance. Always
                // publish the local policy before this solver dispatches: a CFD container may have set the same
                // asset to 1 immediately before an ordinary Fire container whose local value is already false.
                cs.SetInt("_CfdThermalOccupancy", value ? 1 : 0);
            }
        }
        bool cfdFullDomainOccupancy;
        int cfdBoundaryCount;
        Vector2Int cfdFaceGroups = Vector2Int.one;   // thread groups covering the largest patch's in-plane window
        int cfdOutletFacesMask;                      // faces carrying a pressure outlet → coarse-level Dirichlet
        ComputeBuffer cfdBoundaryBuf, cfdFluxBuf;
        // CFD full-domain multigrid: one coarse correction level (res/2). Lazy — ordinary Fire solvers and
        // sparse CFD domains never allocate these.
        RenderTexture cfdCoarsePressure, cfdCoarseDiv, cfdCoarseSolid;
        Vector3Int cfdCoarseRes;
        bool cfdCoarseSolidValid;
        Vector3Int[] cfdCoarseDirtyWindowMin, cfdCoarseDirtyWindowSize;
        int cfdCoarseDirtyWindowCount;
        internal RenderTexture CfdCoarseSolidMask => cfdCoarseSolid;
        const int CfdCoarseIterations = 24;
        Vector3Int cfdLoadMin, cfdLoadSize;          // body voxel AABB (+margin) for the surface-load reduction
        /// <summary>False skips the per-tick render-field build (the volume is not being drawn — CFD slice mode).</summary>
        internal bool RenderFieldEnabled = true;
        // Visualization-only Cp sampled by the surface overlay. Raw solver projection potential flickers on the
        // overlay: its free gauge level, per-tick transients, collocated checkerboard mode, and interior zeros all
        // bleed into surface taps. The reference/filter/calibration pass never feeds the simulation.
        RenderTexture cfdPressureVis;
        bool cfdPressureVisSeeded;
        const float CfdPressureVisTimeConstant = 0.25f;   // seconds to ~63% response — calm but not laggy
        internal RenderTexture CfdPressureVisualization => cfdPressureVis;

        internal static float CfdProjectionDynamicHead(float inletSpeedVox, float maxSpeed, float dt,
                                                       int pressureCadence, int pressureTrickle)
        {
            float speed = Mathf.Min(Mathf.Abs(inletSpeedVox), Mathf.Max(maxSpeed, 0f));
            float projectionInterval = Mathf.Max(dt, 1e-4f) *
                                       (pressureCadence > 1 && pressureTrickle <= 0
                                           ? Mathf.Max(1, pressureCadence) : 1);
            return 0.5f * speed * speed * projectionInterval;
        }

        /// <summary>One rigid-motion animated CFD body for the current tick. The caller folds every transform
        /// into <see cref="voxelToUvw"/> and expresses velocities in grid units (isotropic voxels assumed).</summary>
        internal struct CfdAnimatedBody
        {
            public RenderTexture sdf;          // mesh-local SDF, world-unit distances
            public Matrix4x4 voxelToUvw;       // domain voxel centre → body bake uvw
            public Vector3 linearVelVox;       // pivot velocity, grid voxels/sec
            public Vector3 angularVelGrid;     // rad/s about grid axes
            public Vector3 pivotVox;
            public Vector3Int windowMin;       // current footprint in domain voxels (+margin, clamped)
            public Vector3Int windowSize;
        }
        CfdAnimatedBody[] cfdAnimatedBodies;
        int cfdAnimatedBodyCount;
        Vector3Int[] cfdBodyPrevWindowMin, cfdBodyPrevWindowSize;
        int cfdBodyPrevWindowCount;
        Vector3Int[] cfdRestoreWindowMin, cfdRestoreWindowSize;
        int cfdRestoreWindowCount;

        /// <summary>Publishes this tick's animated bodies. Pass count 0 to clear; the array is borrowed, not copied.</summary>
        internal void SetCfdAnimatedBodies(CfdAnimatedBody[] bodies, int count)
        {
            cfdAnimatedBodies = bodies;
            cfdAnimatedBodyCount = bodies != null ? Mathf.Clamp(count, 0, bodies.Length) : 0;
            if (cfdAnimatedBodyCount > 0 &&
                (cfdBodyPrevWindowMin == null || cfdBodyPrevWindowMin.Length < cfdAnimatedBodyCount))
            {
                System.Array.Resize(ref cfdBodyPrevWindowMin, cfdAnimatedBodyCount);
                System.Array.Resize(ref cfdBodyPrevWindowSize, cfdAnimatedBodyCount);
            }
        }
        bool cfdFluxReadbackPending;
        System.Action<AsyncGPUReadbackRequest> onCfdFluxCb;
        int cfdFluxReadbackFrame = -1000;
        const int CfdFluxReadbackFrameInterval = 8;
        const float CfdFluxFixedPoint = 4096f;
        bool cfdSurfaceDiagnostics;
        Vector3 cfdCellFaceAreaWorld;
        ComputeBuffer cfdSurfaceLoadBuf;
        ComputeBuffer cfdRefPressureBuf;   // ambient reference + stagnation calibration; lives with cfdPressureVis
        bool cfdSurfaceLoadReadbackPending;
        System.Action<AsyncGPUReadbackRequest> onCfdSurfaceLoadCb;
        int cfdSurfaceLoadReadbackFrame = -1000;
        bool cfdTemperatureDiagnosticsEnabled;
        ComputeBuffer cfdTemperaturePeakBuf;
        bool cfdTemperaturePeakReadbackPending;
        System.Action<AsyncGPUReadbackRequest> onCfdTemperaturePeakCb;
        int cfdTemperaturePeakReadbackFrame = -1000;

        internal bool CfdTemperatureDiagnosticsEnabled
        {
            get => cfdTemperatureDiagnosticsEnabled;
            set
            {
                cfdTemperatureDiagnosticsEnabled = value;
                if (!value) CfdTemperaturePeakValid = false;
            }
        }
        internal bool CfdTemperaturePeakValid { get; private set; }
        internal float CfdTemperaturePeakHeatUnits { get; private set; }
        internal ComputeBuffer CfdTemperaturePeakDiagnosticsBuffer => cfdTemperaturePeakBuf;

        public float CfdInletVolumeFlowM3PerSecond { get; private set; }
        public float CfdOutletVolumeFlowM3PerSecond { get; private set; }
        internal ComputeBuffer CfdFluxDiagnosticsBuffer => cfdFluxBuf;
        public Vector3 CfdRelativePressureLoadLocal { get; private set; }
        internal ComputeBuffer CfdSurfaceLoadDiagnosticsBuffer => cfdSurfaceLoadBuf;

        internal void SetCfdSurfaceDiagnostics(bool enabled, Vector3 cellFaceAreaWorld)
            => SetCfdSurfaceDiagnostics(enabled, cellFaceAreaWorld, Vector3Int.zero, Vector3Int.zero);

        internal void SetCfdSurfaceDiagnostics(bool enabled, Vector3 cellFaceAreaWorld,
                                               Vector3Int loadMin, Vector3Int loadSize)
        {
            cfdSurfaceDiagnostics = enabled;
            // Reduction window: the body's voxel AABB from the SDF bake. Zero size means "whole grid" (legacy
            // callers / bounds unavailable) — clamped to the domain either way.
            cfdLoadMin = Vector3Int.Max(Vector3Int.zero, Vector3Int.Min(loadMin, res - Vector3Int.one));
            cfdLoadSize = loadSize.x > 0 && loadSize.y > 0 && loadSize.z > 0
                ? Vector3Int.Min(loadSize, res - cfdLoadMin)
                : res;
            cfdCellFaceAreaWorld = new Vector3(Mathf.Max(0f, cellFaceAreaWorld.x),
                                               Mathf.Max(0f, cellFaceAreaWorld.y),
                                               Mathf.Max(0f, cellFaceAreaWorld.z));
            if (!enabled)
            {
                CfdRelativePressureLoadLocal = Vector3.zero;
                Rel(ref cfdPressureVis);   // vis smoothing is overlay-only; keep the lazy-allocation contract
                cfdPressureVisSeeded = false;
                cfdRefPressureBuf?.Release();
                cfdRefPressureBuf = null;  // a different body/study must not inherit the old stagnation gain
                return;
            }
            if (cfdSurfaceLoadBuf == null)
            {
                cfdSurfaceLoadBuf = new ComputeBuffer(4, sizeof(int));
                cfdSurfaceLoadBuf.SetData(new int[4]);
            }
        }

        /// <summary>Uploads CFD face conditions. The buffer is lazy and remains absent from ordinary Fire solvers.
        /// Full-domain occupancy is the caller's policy and no longer requires boundary patches: airflow with no
        /// dye is invisible to content-driven occupancy, so a patch-free CFD study still needs the global solve.</summary>
        internal void SetCfdBoundaries(GpuCfdBoundary[] boundaries, int count, bool fullDomainOccupancy)
        {
            cfdBoundaryCount = boundaries != null ? Mathf.Clamp(count, 0, boundaries.Length) : 0;
            cfdFullDomainOccupancy = fullDomainOccupancy;
            // Face-layer dispatch extent + the outlet-face mask the coarse pressure level pins to zero.
            cfdOutletFacesMask = 0;
            int maxCellsU = 1, maxCellsV = 1;
            for (int i = 0; i < cfdBoundaryCount; i++)
            {
                GpuCfdBoundary b = boundaries[i];
                // Face-layer dispatch must cover the ROTATED cross-section's axis-aligned bounding box.
                float absCos = Mathf.Abs(Mathf.Cos(b.rotationRadians));
                float absSin = Mathf.Abs(Mathf.Sin(b.rotationRadians));
                float boundU = absCos * b.halfSizeVox.x + absSin * b.halfSizeVox.y;
                float boundV = absSin * b.halfSizeVox.x + absCos * b.halfSizeVox.y;
                maxCellsU = Mathf.Max(maxCellsU, Mathf.CeilToInt(boundU * 2f) + 1);
                maxCellsV = Mathf.Max(maxCellsV, Mathf.CeilToInt(boundV * 2f) + 1);
                if (b.mode > 0.5f)
                    cfdOutletFacesMask |= 1 << Mathf.Clamp(Mathf.RoundToInt(b.face), 0, 5);
            }
            cfdFaceGroups = new Vector2Int(Mathf.CeilToInt(maxCellsU / 8f), Mathf.CeilToInt(maxCellsV / 8f));
            if (cfdBoundaryCount <= 0)
            {
                CfdInletVolumeFlowM3PerSecond = 0f;
                CfdOutletVolumeFlowM3PerSecond = 0f;
                return;
            }

            int capacity = Mathf.NextPowerOfTwo(cfdBoundaryCount);
            if (cfdBoundaryBuf == null || cfdBoundaryBuf.count < cfdBoundaryCount)
            {
                cfdBoundaryBuf?.Release();
                cfdBoundaryBuf = new ComputeBuffer(capacity, CfdBoundaryStride);
            }
            cfdBoundaryBuf.SetData(boundaries, 0, 0, cfdBoundaryCount);
            if (cfdFluxBuf == null)
            {
                cfdFluxBuf = new ComputeBuffer(2, sizeof(int));
                cfdFluxBuf.SetData(new int[2]);
            }
        }
        /// <summary>
        /// Supplies the optional CFD mesh SDF. The version is part of the contract because a static bake updates
        /// texture contents in place; the full-resolution solid mask is refreshed only when either value changes.
        /// </summary>
        internal void SetCfdSignedDistance(RenderTexture texture, int version, float cellWorldSize)
        {
            requestedCfdSdf = texture;
            requestedCfdSdfVersion = version;
            requestedCfdCellWorldSize = Mathf.Max(0f, cellWorldSize);
        }
        internal RenderTexture SolidMask => solid;
        ComputeBuffer emitterBuf, forceBuf, splineVortexBuf, obstacleBuf, activeCountBuf, activeBricksBuf, retiredBricksBuf, indirectArgs;
        bool pendingDenseInactiveClear;   // one-shot dense residue sweep after an external dense field write
        ComputeBuffer fireMomentsBuf;   // fire-light source: burn-weighted centroid (normalized) + total power
        ComputeBuffer cachePackBuf;   // legacy dense cache bake readback buffer
        ComputeBuffer cacheSparseCountBuf, cacheSparseIdxBuf, cacheSparseBlocksBuf;
        ComputeBuffer cachePlaybackWarpBricksBuf;
        int previousCacheScatterBrickCount;
        static readonly uint[] CacheSparseCountClear = { 0u };
        static readonly uint[] FireMomentsClear = new uint[4];
        /// <summary>Fire-light source buffer: [0]=Σweight, [1..3]=Σ(weight·uvw), fixed-point ×1024. The injection
        /// material reads this directly (no readback) → centroid = [1..3]/[0] in [0,1] grid space, power = [0]/1024.</summary>
        public ComputeBuffer FireMoments => fireMomentsBuf;
        ComputeBuffer pointBuf;
        int pointCount;
        bool readbackPending;
        /// <summary>True while async readbacks still reference this solver's GPU buffers.</summary>
        public bool HasPendingReadbacks => readbackPending || brickDbgPending || cfdFluxReadbackPending ||
                                           cfdSurfaceLoadReadbackPending || cfdTemperaturePeakReadbackPending;
        int prevObstacleCount = -1;   // MarkSolids skip: -1 forces a clear on the first Step
        RenderTexture requestedCfdSdf, appliedCfdSdf;
        int requestedCfdSdfVersion, appliedCfdSdfVersion;
        float requestedCfdCellWorldSize, appliedCfdCellWorldSize;
        int uploadedObstacleHash;
        bool obstacleUploadInitialized;
        bool previousObstacleBoundsValid;
        Vector3Int previousObstacleMin, previousObstacleMax;   // inclusive voxel bounds
        Vector3Int obstacleDirtyMin, obstacleDirtySize;
        int simStep;                  // global step counter → pressure-cadence phase (step 0 always corrects)

        /// <summary>Diagnostics: occupied bricks after dilation (updated via async readback).</summary>
        public int ActiveBricks { get; private set; }
        /// <summary>Diagnostics: bricks containing significant raw field content before safety dilation.</summary>
        public int RawBricks { get; private set; }
        public int TotalBricks => occRes.x * occRes.y * occRes.z;

        public RenderTexture Fields => fieldsCur;
        /// <summary>Render Detail up-res field (RGHalf: R = fine density, G = fine burn). Seeded from the coarse
        /// render field, advected by sim velocity, and anchored back to the current coarse low band. Render-only:
        /// never feeds density/pressure/occupancy.</summary>
        public RenderTexture Fine => fineCur;
        /// <summary>Legacy accumulated render-warp displacement field. The live transported fine path no longer
        /// allocates or samples this unless an external caller explicitly steps it.</summary>
        public RenderTexture FlowField => flowField;
        /// <summary>Baked render-detail domain warp. This is the old Render Detail module: a visual-only
        /// displacement field that bends the render field instead of adding a second density/burn volume.</summary>
        public RenderTexture WarpField => warpField;
        /// <summary>Allocated warp resolution multiplier, or 0 while Render Detail is not resident.</summary>
        public int WarpScale => warpScale;
        public RenderTexture Velocity => playbackOnly && !playbackMotion ? null : velCur;
        public RenderTexture Pressure => pressure;
        public bool PlaybackMotionReconstruction => playbackReconstruction;
        public bool PlaybackAuthoritativeVelocity => playbackVelocity;
        /// <summary>Per-voxel vorticity (∇×v) from this frame's Curl pass.</summary>
        public RenderTexture Curl => curl;
        /// <summary>The texture the renderer should sample: a render-only smoothed copy built before sparse clears.</summary>
        public RenderTexture RenderField => renderCur != null ? renderCur : fieldsCur;
        /// <summary>Solver occupancy (raw + halo), occRes³ — used by sim-side consumers such as particles.</summary>
        public RenderTexture Occupancy => occD;
        /// <summary>Render-only occupancy max-pooled from <see cref="RenderField"/> for empty-space leaping.</summary>
        public RenderTexture RenderOccupancy => renderOcc != null ? renderOcc : occD;
        /// <summary>GPU sparse-dispatch state shared with container-owned support bakes.</summary>
        internal ComputeBuffer ActiveBricksBuffer => activeBricksBuf;
        internal ComputeBuffer RetiredBricksBuffer => retiredBricksBuf;
        internal ComputeBuffer SparseIndirectArgs => indirectArgs;
        /// <summary>Packed light volume (low-res): r = single-scatter transmittance, g = multi-scatter radiance,
        /// b = fire-as-a-light. One texture so the renderer reads all lighting in a single tap. Null until SetLighting.</summary>
        public RenderTexture LightField => lightPacked;

        /// <summary>Inputs for the grid-space light bakes — pushed by the container each frame before Step. Only the
        /// self-shadow march needs them now; smoke colour / ambient / fire-glow are applied live in the renderer.</summary>
        public struct LightingParams
        {
            public Vector3 lightDirOS;   // key light direction in object space (normalized)
            public float shadowDensity;  // = shadowStrength (deeper self-shadows)
            public float lightStepVox;   // shadow-march step length (base voxels)
            public int lightSteps;       // shadow-march steps
        }
        LightingParams lp;

        /// <summary>Configure the grid-space light bakes. Allocates the illumination + scatter fields lazily, at
        /// lightRes = res / LightDownsample. (Re)allocates when the divisor changes.</summary>
        public void SetLighting(in LightingParams p)
        {
            lp = p;
            Vector3Int want = new Vector3Int(
                Mathf.Max(res.x / lightDiv, 4),
                Mathf.Max(res.y / lightDiv, 4),
                Mathf.Max(res.z / lightDiv, 4));
            if (illumField == null || lightRes != want)
            {
                Rel(ref illumField); Rel(ref scatterField); Rel(ref scatterTmp);
                Rel(ref fireLightField); Rel(ref lightPacked);
                lightRes = want;
                lightGroups = Groups(lightRes, 4);
                illumField = Make(lightRes, RenderTextureFormat.RHalf);
                scatterField = Make(lightRes, RenderTextureFormat.RHalf);
                scatterTmp = Make(lightRes, RenderTextureFormat.RHalf);
                fireLightField = Make(lightRes, RenderTextureFormat.RHalf);
                lightPacked = Make(lightRes, RenderTextureFormat.ARGBHalf);
                // No pre-clear: the bakes dispatch DENSE over the whole light grid, writing every voxel each tick.
                lightBakeDirty = true;   // fresh (uninitialized) textures — force a bake before the next reuse window
            }
        }

        void ReleaseLighting()
        {
            Rel(ref illumField); Rel(ref scatterField); Rel(ref scatterTmp);
            Rel(ref fireLightField); Rel(ref lightPacked);
            lightRes = default;
            lightGroups = default;
            lightBakeDirty = true;
        }

        /// <summary>Rebake the light volumes every Nth sim step (1 = every step). Lighting is diffuse and changes
        /// at smoke speed, not sim rate — reusing the packed volume between bakes is visually free at 2 and cuts
        /// the bake cost proportionally. (Re)allocation always forces an immediate bake.</summary>
        public int LightBakeInterval { get; set; } = 1;
        bool lightBakeDirty = true;
        public Vector3Int Res => res;

        public BrazeSolver(ComputeShader shader, Vector3Int resolution, bool playbackOnly = false, bool playbackWarp = false,
                           bool playbackFlow = false, Vector3Int playbackWarpResolution = default,
                           bool playbackMotion = false, bool playbackVelocity = false)
        {
            cs = shader;
            this.playbackOnly = playbackOnly;
            this.playbackWarp = playbackOnly && playbackWarp;
            this.playbackFlow = playbackOnly && playbackWarp && playbackFlow;
            this.playbackVelocity = playbackOnly && playbackVelocity;
            this.playbackReconstruction = playbackOnly && playbackMotion && !this.playbackVelocity;
            this.playbackMotion = this.playbackVelocity || this.playbackReconstruction;
            if (this.playbackReconstruction)
            {
                vdbPlaybackCs = Resources.Load<ComputeShader>("BrazeVdbPlayback");
                if (vdbPlaybackCs == null)
                    throw new System.InvalidOperationException("BrazeVdbPlayback.compute is missing from Resources.");
                kVdbPackSignal = vdbPlaybackCs.FindKernel("PackSignalPair");
                kVdbDownsampleSignal = vdbPlaybackCs.FindKernel("DownsampleSignal");
                kVdbBuildMotionTerms = vdbPlaybackCs.FindKernel("BuildMotionTerms");
                kVdbRefineMotion = vdbPlaybackCs.FindKernel("RefineMotion");
                kVdbTemporalBlend = vdbPlaybackCs.FindKernel("TemporalBlendMotion");
                kVdbFinalizeMotion = vdbPlaybackCs.FindKernel("FinalizeMotion");
            }
            // Per-axis base resolution. 512 ceiling = the practical high-res cap for this dense-allocated grid
            // (512³ ≈ 16 GB of fields → a 24 GB GPU); the dispatch + occupancy math scales fine, VRAM is the wall.
            res = new Vector3Int(
                Mathf.Clamp(resolution.x, 8, 512),
                Mathf.Clamp(resolution.y, 8, 512),
                Mathf.Clamp(resolution.z, 8, 512));
            int maxWarpAxis = Mathf.Max(8, SystemInfo.maxTexture3DSize);
            playbackWarpRes = this.playbackWarp
                ? new Vector3Int(
                    Mathf.Clamp(playbackWarpResolution.x > 0 ? playbackWarpResolution.x : res.x, 8, maxWarpAxis),
                    Mathf.Clamp(playbackWarpResolution.y > 0 ? playbackWarpResolution.y : res.y, 8, maxWarpAxis),
                    Mathf.Clamp(playbackWarpResolution.z > 0 ? playbackWarpResolution.z : res.z, 8, maxWarpAxis))
                : Vector3Int.zero;
            groups = Groups(res, 4);
            occRes = new Vector3Int(
                Mathf.CeilToInt((float)res.x / Brick),
                Mathf.CeilToInt((float)res.y / Brick),
                Mathf.CeilToInt((float)res.z / Brick));
            occGroups = Groups(occRes, 4);

            kEmit = cs.FindKernel("Emit");
            kEmitPoints = cs.FindKernel("EmitPoints");
            kAdvectFields = cs.FindKernel("AdvectFields");   // fused: fields hat + velocity self-advection
            kMac = cs.FindKernel("MacCormackFields");
            kCurl = cs.FindKernel("Curl");
            kConf = cs.FindKernel("Confinement");
            kCurlNoise = cs.FindKernel("CurlNoise");
            kForce = cs.FindKernel("Force");
            kMarkSolids = cs.FindKernel("MarkSolids");
            kApplyCfdSdf = cs.FindKernel("ApplyCfdSdf");
            kApplyCfdBoundariesFace = cs.FindKernel("ApplyCfdBoundariesFace");
            kClearCfdFlux = cs.FindKernel("ClearCfdFlux");
            kMeasureCfdFluxFace = cs.FindKernel("MeasureCfdFluxFace");
            kClearCfdSurfaceLoad = cs.FindKernel("ClearCfdSurfaceLoad");
            kMeasureCfdSurfaceLoadWindow = cs.FindKernel("MeasureCfdSurfaceLoadWindow");
            kClearCfdTemperaturePeak = cs.FindKernel("ClearCfdTemperaturePeak");
            kMeasureCfdTemperaturePeak = cs.FindKernel("MeasureCfdTemperaturePeak");
            kCfdSeedContactDye = cs.FindKernel("CfdSeedContactDye");
            kClearCfdRefPressure = cs.FindKernel("ClearCfdRefPressure");
            kMeasureCfdRefPressure = cs.FindKernel("MeasureCfdRefPressure");
            kMeasureCfdStagnationPressure = cs.FindKernel("MeasureCfdStagnationPressure");
            kFinalizeCfdPressureCalibration = cs.FindKernel("FinalizeCfdPressureCalibration");
            kCfdBuildCoarseSolid = cs.FindKernel("CfdBuildCoarseSolid");
            kCfdRestrictResidual = cs.FindKernel("CfdRestrictResidual");
            kCfdCoarseRBGS = cs.FindKernel("CfdCoarseRBGS");
            kCfdProlongAdd = cs.FindKernel("CfdProlongAdd");
            kCfdSmoothPressureVis = cs.FindKernel("CfdSmoothPressureVis");
            kRestoreCfdStaticSolid = cs.FindKernel("RestoreCfdStaticSolid");
            kApplyCfdBodySdf = cs.FindKernel("ApplyCfdBodySdf");
            kApplyCfdBodyMotion = cs.FindKernel("ApplyCfdBodyMotion");
            kDiv = cs.FindKernel("Divergence");
            kDivCfd = cs.FindKernel("DivergenceCfd");
            kRBGS = cs.FindKernel("RBGS");
            kRBGSCfd = cs.FindKernel("RBGSCfd");
            kProj = cs.FindKernel("Project");
            kProjCfd = cs.FindKernel("ProjectCfd");
            kDecay = cs.FindKernel("Decay");
            kFineSeed = cs.FindKernel("FineSeed");
            kDetailBake = cs.FindKernel("DetailBake");
            kAdvectFlow = cs.FindKernel("AdvectFlow");                     // brick-indirect
            kClearRetiredFlow = cs.FindKernel("ClearRetiredFlow");
            kFlowCurl = cs.FindKernel("FlowCurl");                         // brick-indirect (DetailFlow playback)
            kBakeWarp = cs.FindKernel("BakeWarp");                         // brick-indirect
            kClearRetiredWarpBricks = cs.FindKernel("ClearRetiredWarpBricks");
            kClearWarpNeutral = cs.FindKernel("ClearWarpNeutral");
            kClearOcc = cs.FindKernel("ClearOcc");
            kMarkOcc = cs.FindKernel("MarkOcc");                 // brick-indirect content scan (live path)
            kMarkOccDense = cs.FindKernel("MarkOccDense");       // dense content scan (static-bake path)
            kMarkEmitterOcc = cs.FindKernel("MarkEmitterOcc");   // per-emitter AABB footprint marks
            kMarkPointOcc = cs.FindKernel("MarkPointOcc");
            kDilateOcc = cs.FindKernel("DilateOcc");
            kSurfSplat = cs.FindKernel("SurfSplat");
            kSurfEmitFold = cs.FindKernel("SurfEmitFold");
            kClearSurfAccum = cs.FindKernel("ClearSurfAccum");
            kMeshSplat = cs.FindKernel("MeshSplat");
            kFragmentSplat = cs.FindKernel("FragmentSplat");
            kClearMeshMaskPass = cs.FindKernel("ClearMeshMaskPass");
            kMeshMaskGate = cs.FindKernel("MeshMaskGate");
            kWriteMeshMaskDispatchArgs = cs.FindKernel("WriteMeshMaskDispatchArgs");
            kMeshEmitFold = cs.FindKernel("MeshEmitFold");
            kClearMeshAccum = cs.FindKernel("ClearMeshAccum");
            kWriteArgs = cs.FindKernel("WriteArgs");
            kClearField = cs.FindKernel("ClearField");
            kClearInactiveFields = cs.FindKernel("ClearInactiveFields");   // dense fallback (transitions only)
            kClearRetiredBricks = cs.FindKernel("ClearRetiredBricks");     // per-step plume-proportional clear
            kBuildRenderField = cs.FindKernel("BuildRenderField");   // also writes the fused render occupancy
            kBuildCacheRenderOcc = cs.FindKernel("BuildCacheRenderOcc");
            kBrickDebug = cs.FindKernel("BrickDebug");
            kIllum = cs.FindKernel("Illumination");
            kScatterSeed = cs.FindKernel("ScatterSeed");
            kScatterBlur = cs.FindKernel("ScatterBlur");
            kFireLightSeed = cs.FindKernel("FireLightSeed");
            kPackLight = cs.FindKernel("PackLight");
            kClearScalar = cs.FindKernel("ClearScalar");
            kClearScalar2 = cs.FindKernel("ClearScalar2");
            kClearLightScalar = cs.FindKernel("ClearLightScalar");
            kFireMoments = cs.FindKernel("FireMoments");
            kCachePackBase = cs.FindKernel("CachePackBase");
            kCacheCountSparse = cs.FindKernel("CacheCountSparse");
            kCachePackSparse = cs.FindKernel("CachePackSparse");
            kCacheScatterBase = cs.FindKernel("CacheScatterBase");
            kCacheVelocityMotion = cs.FindKernel("CacheVelocityMotion");
            kCacheMotionBlend = cs.FindKernel("CacheMotionBlend");
            kClearBricksFV = cs.FindKernel("ClearBricksFV");
            kClearCacheWarpBricks = cs.FindKernel("ClearCacheWarpBricks");
            kMarkOccFromScatter = cs.FindKernel("MarkOccFromScatter");

            // Heavy base kernels that early-out on empty bricks (BASE_EMPTY → needs _OccD bound each frame).
            // NB: MarkSolids (obstacles are smoke-independent) is deliberately NOT here. Emit is brick-indirect
            // too but binds _ActiveBricks at its own dispatch site (like the splat folds) — it must NOT skip
            // empty bricks, since emission is what fills them.
            // The CFD boundary/diagnostic kernels left this list when they became face-layer / windowed
            // dispatches — they no longer walk the active-brick set at all.
            sparseBaseKernels = new[]
            {
                kAdvectFields, kMac, kCurl, kConf, kCurlNoise, kForce,
                kDiv, kDivCfd, kProj, kProjCfd, kDecay,
                kDetailBake,
            };

            try
            {
                fieldsCur = Make(res, RenderTextureFormat.ARGBHalf);
                if (!playbackOnly)
                {
                    renderCur = Make(res, RenderTextureFormat.ARGBHalf);
                    renderNext = Make(res, RenderTextureFormat.ARGBHalf);
                    velCur = Make(res, RenderTextureFormat.ARGBHalf);
                    fieldsNext = Make(res, RenderTextureFormat.ARGBHalf);
                    fieldsTmp = Make(res, RenderTextureFormat.ARGBHalf);
                    velNext = Make(res, RenderTextureFormat.ARGBHalf);
                    curl = Make(res, RenderTextureFormat.ARGBHalf);
                    div = Make(res, RenderTextureFormat.RFloat);
                    pressure = Make(res, RenderTextureFormat.RFloat);
                    // Obstacle mask is LAZY: allocated on the first step that actually has obstacles (33 MB @256³,
                    // 268 MB @512³ otherwise dead weight). Until then every _Solid slot binds this zeroed 4³ dummy —
                    // never READ, because _NoSolids short-circuits SolidAt while solid == null.
                    solidDummy = Make(new Vector3Int(4, 4, 4), RenderTextureFormat.RHalf);
                    ClearScalarField(solidDummy, new Vector3Int(4, 4, 4));
                }
                else
                {
                    // Normal cache playback needs only a tiny placeholder. VDB playback allocates full-resolution
                    // renderer-only motion targets; they never enter the simulation solve.
                    velCur = Make(this.playbackMotion ? res : new Vector3Int(4, 4, 4), RenderTextureFormat.ARGBHalf);
                    if (this.playbackMotion)
                    {
                        flowField = Make(res, RenderTextureFormat.ARGBHalf);
                        curl = Make(res, RenderTextureFormat.ARGBHalf);
                        ClearBaseField(flowField);
                        ClearBaseField(curl);

                        if (this.playbackReconstruction)
                        {
                            playbackNeighbourField = Make(res, RenderTextureFormat.ARGBHalf);
                            vdbMotionHistory = Make(res, RenderTextureFormat.ARGBHalf);
                            ClearBaseField(playbackNeighbourField);
                            ClearBaseField(vdbMotionHistory);

                            // Optical-flow pyramid levels. Coarser levels exist only while every axis stays ≥ 8;
                            // authoritative velocity skips this allocation and the solve entirely.
                            vdbLevelRes[0] = res;
                            vdbLevelCount = 1;
                            for (int k = 1; k < VdbPyramidLevels; k++)
                            {
                                Vector3Int half = new Vector3Int((vdbLevelRes[k - 1].x + 1) / 2,
                                                                 (vdbLevelRes[k - 1].y + 1) / 2,
                                                                 (vdbLevelRes[k - 1].z + 1) / 2);
                                if (Mathf.Min(half.x, Mathf.Min(half.y, half.z)) < 8) break;
                                vdbLevelRes[k] = half;
                                vdbLevelCount = k + 1;
                            }
                        }
                    }
                    if (this.playbackFlow)
                    {
                        // DetailFlow clip: the channels scatter RAW into the base-res flow anchor; the full-scale
                        // warp texture is allocated by the first BakeWarp (exactly like live), not here.
                        EnsureFlowField();
                    }
                    else if (this.playbackWarp)
                    {
                        // Final-look clip: allocate the detail texture at the exact resolution used by the live
                        // renderer. It is scattered separately from the base render field; no downsample/rebuild.
                        warpField = Make(playbackWarpRes, RenderTextureFormat.ARGBHalf);
                        warpScale = Mathf.Max(1, playbackWarpRes.x / Mathf.Max(1, res.x));
                        warpGroups = Groups(playbackWarpRes, 4);
                        SetInts3("_WarpRes", playbackWarpRes.x, playbackWarpRes.y, playbackWarpRes.z);
                        cs.SetTexture(kClearWarpNeutral, "_WarpField", warpField);
                        cs.Dispatch(kClearWarpNeutral, warpGroups.x, warpGroups.y, warpGroups.z);
                    }
                }

                // Indirect base kernels never visit empty bricks, so unvisited cells must start clean (same
            // reasoning as the fine field below) — else undefined memory would advect/render at the edges.
            ClearBaseField(fieldsCur);
            if (fieldsNext != null) ClearBaseField(fieldsNext);
            if (fieldsTmp != null) ClearBaseField(fieldsTmp);
            if (renderCur != null) ClearBaseField(renderCur);
            if (renderNext != null) ClearBaseField(renderNext);
            ClearBaseField(velCur);
            if (velNext != null) ClearBaseField(velNext);
            if (curl != null) ClearBaseField(curl);
            // Pressure is now genuinely warm-started. Keep the invariant pressure==0 outside the active set:
            // initialize once here, then ClearRetiredBricks/ClearInactiveFields maintain it as occupancy changes.
            if (div != null) ClearScalarField(div);
            if (pressure != null) ClearScalarField(pressure);

            if (!playbackOnly)
            {
                emitterBuf = new ComputeBuffer(1, EmitterStride);
                forceBuf = new ComputeBuffer(1, ForceStride);
                // Always keep one float4 bound: Metal validates declared StructuredBuffers even when the frame has
                // no Spline Vortex. Active components grow this tiny buffer geometrically to their exact sample need.
                splineVortexBuf = new ComputeBuffer(1, sizeof(float) * 4);
                obstacleBuf = new ComputeBuffer(1, ObstacleStride);
            }

            // R8 brick masks: raw occupancy briefly uses 0 / 0.5 motion-only / 1 scalar-content during the
            // mark+dilate pre-pass; the published dilated mask remains binary 0/1. UNorm R8 preserves those classes
            // without changing the allocation (and remains one quarter the VRAM of RFloat).
            occ = Make(occRes, RenderTextureFormat.R8);
            occD = Make(occRes, RenderTextureFormat.R8);
            // RGBA importance brick: max visibility, max fire, mean visibility, normalized smoke/fire variation.
            // Live writes it while conditioning the field; cache playback rebuilds the same reduction sparsely from
            // its already-final field so adaptive marching and empty-space skipping keep the live sampling lattice.
            renderOcc = Make(occRes, RenderTextureFormat.ARGBHalf);
            // Occupancy is a per-brick skip oracle. Keep it point-filtered: the renderer maps normalized state UVW
            // to the exact containing 4³ brick (including non-divisible grid axes) and advances to exact brick
            // boundaries, so linear neighbour bleed would only shade extra empty space.
            occ.filterMode = FilterMode.Point;
            occD.filterMode = FilterMode.Point;
            if (renderOcc != null)
            {
                renderOcc.filterMode = FilterMode.Point;
                ClearVectorField(renderOcc, occRes);
            }
            ClearScalarField(occD, occRes);   // DilateOcc reads last step's halo for retired-brick detection — start clean
            activeCountBuf = new ComputeBuffer(3, sizeof(uint));
            activeCountBuf.SetData(new uint[] { 0, 0, 0 });   // first cache-frame sparse clear reads this before any ClearOcc
            if (!playbackOnly) fireMomentsBuf = new ComputeBuffer(4, sizeof(uint));
            activeBricksBuf = new ComputeBuffer(occRes.x * occRes.y * occRes.z, sizeof(uint));
            retiredBricksBuf = new ComputeBuffer(occRes.x * occRes.y * occRes.z, sizeof(uint));
            // Two DispatchIndirect triplets: [0..2] active bricks (offset 0), [3..5] retired bricks (offset 12).
            // Initialized to zero groups: MarkOcc scans LAST step's bricks via these args before WriteArgs
            // refreshes them, and on the very first step there is no prior content to scan.
                indirectArgs = new ComputeBuffer(6, sizeof(uint), ComputeBufferType.IndirectArguments);
                indirectArgs.SetData(new uint[] { 0, 1, 1, 0, 1, 1 });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        static Vector3Int Groups(Vector3Int r, int threads) => new Vector3Int(
            Mathf.CeilToInt((float)r.x / threads),
            Mathf.CeilToInt((float)r.y / threads),
            Mathf.CeilToInt((float)r.z / threads));

        RenderTexture Make(Vector3Int r, RenderTextureFormat fmt)
        {
            var rt = new RenderTexture(r.x, r.y, 0, fmt, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = r.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "BrazeFX_3D",
            };
            bool created = rt.Create();
            if (!created || !rt.IsCreated())
            {
                rt.Release();
#if UNITY_EDITOR
                if (!Application.isPlaying) Object.DestroyImmediate(rt); else Object.Destroy(rt);
#else
                Object.Destroy(rt);
#endif
                long bytes = (long)r.x * r.y * r.z * (fmt == RenderTextureFormat.ARGBHalf ? 8L :
                    fmt == RenderTextureFormat.RGHalf || fmt == RenderTextureFormat.RFloat ? 4L :
                    fmt == RenderTextureFormat.RHalf ? 2L : 1L);
                throw new System.InvalidOperationException(
                    $"Could not create {fmt} 3D texture {r.x}×{r.y}×{r.z} ({bytes / (1024 * 1024)} MB)." );
            }
            return rt;
        }

        static long TextureBytes(RenderTexture rt)
        {
            if (rt == null) return 0;
            int bytesPerVoxel = rt.format switch
            {
                RenderTextureFormat.ARGBHalf => 8,
                RenderTextureFormat.RGHalf => 4,
                RenderTextureFormat.RFloat => 4,
                RenderTextureFormat.RHalf => 2,
                RenderTextureFormat.R8 => 1,
                _ => 4,
            };
            return (long)rt.width * rt.height * Mathf.Max(1, rt.volumeDepth) * bytesPerVoxel;
        }

        static long BufferBytes(ComputeBuffer b) => b != null ? (long)b.count * b.stride : 0L;
        static long BufferBytes(GraphicsBuffer b) => b != null ? (long)b.count * b.stride : 0L;

        /// <summary>Current solver-owned GPU allocation estimate. Includes optional detail/light/history textures
        /// and source accumulators, unlike the old authored-grid-only HUD estimate.</summary>
        public long EstimateAllocatedBytes()
        {
            long bytes = 0;
            bytes += TextureBytes(fieldsCur) + TextureBytes(fieldsNext) + TextureBytes(fieldsTmp);
            bytes += TextureBytes(renderCur) + TextureBytes(renderNext) + TextureBytes(renderOcc);
            bytes += TextureBytes(velCur) + TextureBytes(velNext) + TextureBytes(curl) + TextureBytes(div) + TextureBytes(pressure);
            bytes += TextureBytes(solid) + TextureBytes(solidDummy) + TextureBytes(occ) + TextureBytes(occD);
            bytes += TextureBytes(fineCur) + TextureBytes(fineNext);
            bytes += TextureBytes(flowField) + TextureBytes(warpField);
            bytes += TextureBytes(playbackNeighbourField) + TextureBytes(vdbMotionHistory);
            bytes += TextureBytes(playbackKeyField);
            for (int k = 0; k < VdbPyramidLevels; k++)
                bytes += TextureBytes(vdbSignal[k]) + TextureBytes(vdbMotionTerms[k])
                      + TextureBytes(vdbMotionA[k]) + TextureBytes(vdbMotionB[k]);
            bytes += TextureBytes(illumField) + TextureBytes(scatterField) + TextureBytes(scatterTmp)
                  + TextureBytes(fireLightField) + TextureBytes(lightPacked);
            bytes += BufferBytes(emitterBuf) + BufferBytes(forceBuf) + BufferBytes(splineVortexBuf)
                  + BufferBytes(obstacleBuf) + BufferBytes(pointBuf);
            bytes += BufferBytes(activeCountBuf) + BufferBytes(activeBricksBuf) + BufferBytes(retiredBricksBuf)
                  + BufferBytes(indirectArgs) + BufferBytes(fireMomentsBuf) + BufferBytes(cachePackBuf);
            bytes += BufferBytes(surfAccumBuf) + BufferBytes(meshAccumBuf) + BufferBytes(meshMaskDummyU) + BufferBytes(brickDbgBuf);
            bytes += BufferBytes(cfdBoundaryBuf) + BufferBytes(cfdFluxBuf);
            bytes += BufferBytes(cfdSurfaceLoadBuf) + BufferBytes(cfdTemperaturePeakBuf);
            bytes += TextureBytes(cfdCoarsePressure) + TextureBytes(cfdCoarseDiv) + TextureBytes(cfdCoarseSolid);
            bytes += TextureBytes(cfdPressureVis);
            return bytes;
        }

        public void Step(in FrameParams p,
                         GpuEmitter[] emitters, int emitterCount,
                         GpuForce[] forces, int forceCount,
                         GpuObstacle[] obstacles, int obstacleCount,
                         Vector4[] splineVortexSamples = null, int splineVortexSampleCount = 0)
        {
            UploadEmitters(emitters, emitterCount);
            UploadForces(forces, forceCount);
            int uploadedSplineVortexSamples = UploadSplineVortexSamples(splineVortexSamples, splineVortexSampleCount);
            bool obstaclesChanged = UploadObstacles(obstacles, obstacleCount);
            bool cfdSdfChanged = !ReferenceEquals(requestedCfdSdf, appliedCfdSdf) ||
                                 requestedCfdSdfVersion != appliedCfdSdfVersion ||
                                 requestedCfdCellWorldSize != appliedCfdCellWorldSize;
            bool cfdSdfBodyActive = requestedCfdSdf != null || cfdAnimatedBodyCount > 0;
            bool fractionalCfd = cfdSdfBodyActive || cfdBoundaryCount > 0;
            if (fractionalCfd && cfdBoundaryBuf == null)
                cfdBoundaryBuf = new ComputeBuffer(1, CfdBoundaryStride);

            // ComputeShader uniforms are asset-shared. Publish every local CFD switch for every solver step so a
            // Fire container cannot inherit a preceding CFD container's full-domain/thermal/fractional policy.
            cs.SetInt("_CfdThermalOccupancy", cfdThermalOccupancy ? 1 : 0);
            cs.SetInt("_CfdFullDomainOccupancy", cfdFullDomainOccupancy ? 1 : 0);
            cs.SetInt("_CfdBoundaryCount", cfdBoundaryCount);
            cs.SetInt("_CfdFractionalSolids", fractionalCfd ? 1 : 0);
            cs.SetVector("_CfdCellFaceArea", cfdCellFaceAreaWorld);

            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetFloat("_Dt", p.dt);
            cs.SetFloat("_Time", p.time);
            cs.SetFloat("_MaxSpeed", p.maxSpeed);
            cs.SetFloat("_ResolutionScale", Mathf.Max(p.resolutionScale, 1e-4f));
            cs.SetFloat("_VelDissipation", p.velDis);
            cs.SetFloat("_SmokeDecayMul", p.smokeDecayMul);
            cs.SetFloat("_FireDecayMul", p.fireDecayMul);
            cs.SetFloat("_SmokeErode", p.smokeErode);
            cs.SetFloat("_AmbientLife", p.ambientLife);
            cs.SetFloat("_Drag", p.drag);                     // emitter drag → whole-plume velocity settle (Force kernel)
            cs.SetFloat("_ConfineScale", p.confineScale);     // container global dynamics ↓
            cs.SetFloat("_Turbulence", p.turbulence);
            cs.SetFloat("_TurbScale", p.turbScale);
            cs.SetFloat("_BillowStrength", p.billow);
            cs.SetFloat("_BillowScale", p.billowScale);
            cs.SetFloat("_Instability", p.instability);             // baroclinic buoyancy perturbation (Force kernel)
            cs.SetFloat("_InstabScale", p.instabScale);
            cs.SetFloat("_InterfaceTurb", p.interfaceTurb);         // gradient-gated edge turbulence (CurlNoise kernel)
            cs.SetFloat("_InterfaceTurbScale", p.interfaceTurbScale);
            cs.SetFloat("_CfdInletTurbulence", Mathf.Clamp(p.cfdInletTurbulence, 0f, 0.3f));
            cs.SetFloat("_CfdTurbulenceLengthVox", Mathf.Max(p.cfdTurbulenceLengthVox, 1f));
            cs.SetFloat("_CfdTurbulenceFrequency", Mathf.Max(p.cfdTurbulenceFrequency, 0f));
            cs.SetFloat("_CfdRenderSpeedInvReference", Mathf.Max(p.cfdRenderSpeedInvReference, 0f));
            cs.SetVector("_CfdContactDye", new Vector4(Mathf.Max(p.cfdContactDyeDensity, 0f),
                                                       Mathf.Max(p.cfdContactDyeSpeedInv, 0f), 0f, 0f));
            cs.SetFloat("_CfdThermalBuoyancy", Mathf.Max(p.cfdThermalBuoyancy, 0f));
            cs.SetFloat("_CfdLinearDrag", Mathf.Max(p.cfdLinearDrag, 0f));
            cs.SetFloat("_OccThreshold", Mathf.Max(1e-5f, p.occThreshold));
            // The existing dilated occupancy halo covers one four-voxel brick. Avoid even sampling velocity in
            // MarkOcc unless the configured speed could cross that support during this complete simulation tick.
            cs.SetInt("_AdvectionSweep", p.maxSpeed * Mathf.Max(p.dt, 0f) > Brick ? 1 : 0);
            cs.SetInt("_ClosedFaces", p.closedFaces);
            cs.SetInt("_EmitterCount", emitterCount);
            cs.SetInt("_ForceCount", forceCount);
            cs.SetInt("_SplineVortexSampleCount", uploadedSplineVortexSamples);
            int splineVortexForceStart = forceCount;
            int splineVortexForceEnd = -1;
            if (forces != null)
            {
                int safeForceCount = Mathf.Min(forceCount, forces.Length);
                for (int i = 0; i < safeForceCount; i++)
                {
                    if (Mathf.RoundToInt(forces[i].type) != 11) continue;
                    if (splineVortexForceEnd < 0) splineVortexForceStart = i;
                    splineVortexForceEnd = i;
                }
            }
            // Container packing keeps them contiguous. Span first→last anyway so direct Step callers with an
            // interleaved force array remain correct; the kernel cheaply skips non-type-11 entries in that span.
            int splineVortexForceCount = splineVortexForceEnd >= splineVortexForceStart
                ? splineVortexForceEnd - splineVortexForceStart + 1 : 0;
            cs.SetInt("_SplineVortexForceStart", splineVortexForceStart);
            cs.SetInt("_SplineVortexForceCount", splineVortexForceCount);
            cs.SetInt("_ObstacleCount", obstacleCount);
            // Obstacle-free fast path: SolidAt skips the _Solid load entirely (6 loads/cell/RBGS iteration).
            // Gated on last step too so the transition frame still reads the stale mask exactly like before
            // (MarkSolids clears it later in this step; pre-clear consumers like advection see old behaviour).
            if (solid == null && (obstacleCount > 0 || requestedCfdSdf != null || cfdAnimatedBodyCount > 0))
            {
                // First obstacle → real mask. Cleared immediately: pre-MarkSolids consumers this step
                // (advection) must read zeros exactly like the old always-alive mask did mid-run.
                solid = Make(res, RenderTextureFormat.RHalf);
                ClearScalarField(solid);
            }
            bool solidActiveOrTransition = obstacleCount > 0 || prevObstacleCount > 0 ||
                                           requestedCfdSdf != null || appliedCfdSdf != null ||
                                           cfdAnimatedBodyCount > 0;
            cs.SetInt("_NoSolids", solid == null || !solidActiveOrTransition ? 1 : 0);

            // 1. Occupancy: mark existing field content plus current source footprints, then dilate for halo.
            // Sources are emitted after advection; this pre-marks their bricks so the sparse solve visits them.
            // Raw occupancy distinguishes scalar-emitting/content (1) from motion-only support (0.5); DilateOcc
            // keeps occD binary and tags the already-packed active-brick index, adding no allocation or dispatch.
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetTexture(kClearOcc, "_Occ", occ);
            cs.SetBuffer(kClearOcc, "_ActiveCount", activeCountBuf);
            DispatchOcc(kClearOcc);
            // Content scan: indirect over LAST step's bricks (fields are provably 0 everywhere else — writers
            // only touch marked bricks and retired bricks are zeroed each step). Replaces the dense full-grid
            // field read (~134 MB @256³ per step).
            if (!cfdFullDomainOccupancy)
            {
                cs.SetTexture(kMarkOcc, "_Occ", occ);
                cs.SetTexture(kMarkOcc, "_Fields", fieldsCur);
                cs.SetTexture(kMarkOcc, "_Vel", velCur);   // velocity-swept support for fast full-tick advection
                cs.SetBuffer(kMarkOcc, "_ActiveBricks", activeBricksBuf);
                cs.DispatchIndirect(kMarkOcc, indirectArgs, 0);
            }

            // Emitter footprints: one small dispatch per driving emitter over its conservative voxel AABB,
            // evaluating the exact per-voxel contribution — identical marks to the old dense per-voxel ×
            // per-emitter loop without running shape math over the whole grid. The drive gate (skip emitters
            // that add no channels/motion/gas) moved here from the kernel.
            if (!cfdFullDomainOccupancy && emitterCount > 0 && emitters != null)
            {
                cs.SetTexture(kMarkEmitterOcc, "_Occ", occ);
                cs.SetBuffer(kMarkEmitterOcc, "_Emitters", emitterBuf);
                for (int i = 0; i < emitterCount; i++)
                {
                    GpuEmitter e = emitters[i];
                    // Includes the pure air-mover terms (jet / source velocity / turbulence with zero channels):
                    // Emit now dispatches brick-indirect over the active set, so an emitter that only STIRS must
                    // still mark its AABB or its velocity injection would be skipped in empty space.
                    float drive = e.channels.x + e.channels.y + e.channels.z
                                + Mathf.Abs(e.motionForward) + Mathf.Abs(e.motionRadial) + e.gasPressureBurst
                                + e.motionJet.magnitude + e.sourceVelocity.magnitude + e.sourceTurbulence;
                    if (drive <= 0f) continue;
                    // Superset of every shape's falloff support at any rotation (worst case is the torus:
                    // major + 1.35 × tube), plus one voxel so voxel-centre sampling never clips the edge.
                    float bound = 1.35f * (Mathf.Max(e.radiusVox, 0f) + e.halfExtentVox.magnitude) + 1f;
                    Vector3Int lo = Vector3Int.Max(Vector3Int.zero, Vector3Int.FloorToInt(e.posVox - Vector3.one * bound));
                    Vector3Int hi = Vector3Int.Min(res - Vector3Int.one, Vector3Int.CeilToInt(e.posVox + Vector3.one * bound));
                    Vector3Int size = hi - lo + Vector3Int.one;
                    if (size.x <= 0 || size.y <= 0 || size.z <= 0) continue;   // fully outside the grid
                    cs.SetInt("_MarkEmitterIndex", i);
                    SetInts3("_MarkAabbMin", lo.x, lo.y, lo.z);
                    SetInts3("_MarkAabbSize", size.x, size.y, size.z);
                    cs.Dispatch(kMarkEmitterOcc,
                        Mathf.CeilToInt(size.x / 4f), Mathf.CeilToInt(size.y / 4f), Mathf.CeilToInt(size.z / 4f));
                }
            }
            if (pointCount > 0 && pointBuf != null)
            {
                cs.SetInt("_PointCount", pointCount);
                cs.SetTexture(kMarkPointOcc, "_Occ", occ);
                cs.SetBuffer(kMarkPointOcc, "_PointSources", pointBuf);
                cs.Dispatch(kMarkPointOcc, Mathf.CeilToInt(pointCount / 64f), 1, 1);
            }

            // GPU surface-coverage splat: coverage test + live-skinned vertex fetch + atomic channel
            // splat + brick mark, all on the GPU (no CPU vertex walk). Before DilateOcc so splatted
            // bricks get the halo and land in the indirect args; SurfEmitFold consumes after Emit.
            if (surfSources.Count > 0 && surfAccumBuf != null)
            {
                cs.SetMatrix("_WorldToVoxelM", worldToVoxelM);
                for (int s = 0; s < surfSources.Count; s++)
                {
                    var src = surfSources[s];
                    if (src.verts == null || !src.verts.IsValid() || src.weldRep == null || src.reach == null || src.weldedCount <= 0)
                        continue;
                    cs.SetInt("_SurfWeldedCount", src.weldedCount);
                    cs.SetInt("_SurfStride", src.stride);
                    cs.SetInt("_SurfPosOffset", src.posOffset);
                    cs.SetFloat("_SurfFront", src.front);
                    cs.SetFloat("_SurfBandMin", src.bandMin);
                    cs.SetVector("_SurfChannels", src.channels);
                    cs.SetMatrix("_SurfLocalToWorld", src.localToWorld);
                    cs.SetBuffer(kSurfSplat, "_SurfVerts", src.verts);
                    cs.SetBuffer(kSurfSplat, "_SurfWeldRep", src.weldRep);
                    cs.SetBuffer(kSurfSplat, "_SurfReach", src.reach);
                    cs.SetBuffer(kSurfSplat, "_SurfAccum", surfAccumBuf);
                    cs.SetTexture(kSurfSplat, "_Occ", occ);
                    cs.Dispatch(kSurfSplat, Mathf.CeilToInt(src.weldedCount / 64f), 1, 1);
                }
            }

            // GPU mesh-emitter splat (skinned/static Mesh-shape emitters): live world verts → sparse per-cell
            // channel/velocity/buoyancy hash + brick mark. Before DilateOcc so splatted bricks get the
            // halo + land in the indirect args; MeshEmitFold consumes after Emit. Replaces the CPU vertex walk.
            if (meshSources.Count > 0 && meshAccumBuf != null)
            {
                cs.SetMatrix("_WorldToVoxelM", worldToVoxelM);
                for (int s = 0; s < meshSources.Count; s++)
                {
                    var src = meshSources[s];
                    if (src.positions == null || !src.positions.IsValid() || src.prevPos == null || src.vertCount <= 0)
                        continue;
                    cs.SetInt("_MeshVertCount", src.vertCount);
                    cs.SetInt("_MeshHasPrev", src.hasPrev ? 1 : 0);
                    cs.SetFloat("_MeshInvDt", src.invDt);   // per-frame pose interval (not sim dt)
                    cs.SetVector("_MeshChannels", src.channels);
                    cs.SetFloat("_MeshBuoyancy", src.buoyancy);
                    cs.SetFloat("_MeshNormalSpeed", src.normalSpeed);
                    cs.SetVector("_MeshJetVelocity", src.jetVelocity);
                    cs.SetVector("_MeshSourceVelocity", src.sourceVelocity);
                    cs.SetFloat("_MeshSourceVelocityBlend", src.sourceVelocityBlend);
                    cs.SetFloat("_MeshSourceTurbulence", src.sourceTurbulence);
                    cs.SetVector("_MeshPushAccel", src.pushAccel);
                    cs.SetInt("_MeshInputLocal", src.positionsAreLocal ? 1 : 0);
                    if (src.positionsAreLocal)
                    {
                        cs.SetMatrix("_MeshLocalToWorld", src.localToWorld);
                        cs.SetMatrix("_MeshNormalLocalToWorld", src.normalLocalToWorld);
                    }
                    int maskOn = src.mask != null && src.maskRestLocal != null && src.maskRestLocal.IsValid() ? 1 : 0;
                    bool passSetOn = maskOn != 0
                        && src.maskUsePassSet
                        && src.maskPassIdx != null && src.maskPassIdx.IsValid()
                        && src.maskPassCount != null && src.maskPassCount.IsValid();
                    if (passSetOn && src.maskPassDirty)
                    {
                        cs.SetBuffer(kClearMeshMaskPass, "_MeshMaskPassCountRW", src.maskPassCount);
                        cs.Dispatch(kClearMeshMaskPass, 1, 1, 1);
                        cs.SetInt("_MeshVertCount", src.vertCount);
                        src.mask.BindField(cs, kMeshMaskGate);
                        cs.SetMatrix("_MeshMaskRestLocalToField", src.maskRestLocalToField);
                        cs.SetBuffer(kMeshMaskGate, "_MeshMaskRestLocal", src.maskRestLocal);
                        cs.SetBuffer(kMeshMaskGate, "_MeshMaskPassIdxRW", src.maskPassIdx);
                        cs.SetBuffer(kMeshMaskGate, "_MeshMaskPassCountRW", src.maskPassCount);
                        cs.Dispatch(kMeshMaskGate, Mathf.CeilToInt(src.vertCount / 64f), 1, 1);
                    }
                    cs.SetInt("_MeshMaskUsePassIdx", passSetOn ? 1 : 0);
                    cs.SetInt("_MeshMaskEnable", maskOn);
                    if (maskOn != 0)
                    {
                        src.mask.BindField(cs, kMeshSplat);
                        cs.SetMatrix("_MeshMaskRestLocalToField", src.maskRestLocalToField);
                    }
                    cs.SetBuffer(kMeshSplat, "_MeshVerts", src.positions);
                    cs.SetBuffer(kMeshSplat, "_MeshNormals", src.normals != null ? src.normals : src.positions);
                    cs.SetBuffer(kMeshSplat, "_MeshPrevPos", src.prevPos);
                    cs.SetBuffer(kMeshSplat, "_MeshMaskRestLocal", maskOn != 0 ? src.maskRestLocal : src.positions);
                    cs.SetBuffer(kMeshSplat, "_MeshMaskPassIdx", passSetOn ? src.maskPassIdx : MeshMaskDummyU());
                    cs.SetBuffer(kMeshSplat, "_MeshMaskPassCount", passSetOn ? src.maskPassCount : MeshMaskDummyU());
                    cs.SetBuffer(kMeshSplat, "_MeshAccum", meshAccumBuf);
                    cs.SetTexture(kMeshSplat, "_Occ", occ);
                    if (passSetOn)
                    {
                        cs.SetBuffer(kWriteMeshMaskDispatchArgs, "_MeshMaskPassCount", src.maskPassCount);
                        cs.SetBuffer(kWriteMeshMaskDispatchArgs, "_IndirectArgs", indirectArgs);
                        cs.Dispatch(kWriteMeshMaskDispatchArgs, 1, 1, 1);
                        cs.DispatchIndirect(kMeshSplat, indirectArgs, 0);
                    }
                    else
                    {
                        cs.Dispatch(kMeshSplat, Mathf.CeilToInt(src.vertCount / 64f), 1, 1);
                    }
                }
            }

            // GPU Voronoi-fragment splat: read the fracture simulation's live centroid/state, velocity and life
            // buffers directly. One thread per fragment; anchored/dissolved pieces exit before touching the hash.
            if (fragmentSources.Count > 0 && meshAccumBuf != null)
            {
                cs.SetMatrix("_WorldToVoxelM", worldToVoxelM);
                for (int s = 0; s < fragmentSources.Count; s++)
                {
                    var src = fragmentSources[s];
                    if (src.posState == null || !src.posState.IsValid()
                        || src.velocity == null || !src.velocity.IsValid()
                        || src.life == null || !src.life.IsValid() || src.count <= 0)
                        continue;
                    if (src.impactFiltered && (src.impactFluidState == null || !src.impactFluidState.IsValid()
                        || src.impactFluidMotion == null || !src.impactFluidMotion.IsValid()
                        || src.rotation == null || !src.rotation.IsValid()
                        || src.angularVelocity == null || !src.angularVelocity.IsValid()
                        || src.centroidRadius == null || !src.centroidRadius.IsValid()
                        || src.impactFluidGeometry == null || !src.impactFluidGeometry.IsValid()
                        || src.impactFluidVelocity == null || !src.impactFluidVelocity.IsValid()))
                        continue;
                    cs.SetInt("_FragmentCount", src.count);
                    cs.SetInt("_FragmentEmitCount", src.emitCount);
                    cs.SetInt("_FragmentImpactFilter", src.impactFiltered ? 1 : 0);
                    cs.SetFloat("_FragmentImpactBuoyancyScale", src.impactFiltered ? src.impactBuoyancyScale : 1f);
                    cs.SetVector("_MeshChannels", src.channels);
                    cs.SetFloat("_MeshBuoyancy", src.buoyancy);
                    cs.SetFloat("_FragmentVelocityInherit", src.velocityInherit);
                    cs.SetVector("_MeshJetVelocity", src.jetVelocity);
                    cs.SetVector("_MeshSourceVelocity", src.sourceVelocity);
                    cs.SetFloat("_MeshSourceVelocityBlend", src.sourceVelocityBlend);
                    cs.SetFloat("_MeshSourceTurbulence", src.sourceTurbulence);
                    cs.SetVector("_MeshPushAccel", src.pushAccel);
                    cs.SetBuffer(kFragmentSplat, "_FragmentPosState", src.posState);
                    cs.SetBuffer(kFragmentSplat, "_FragmentRotation",
                        src.rotation != null && src.rotation.IsValid() ? src.rotation : src.life);
                    cs.SetBuffer(kFragmentSplat, "_FragmentVelocity", src.velocity);
                    cs.SetBuffer(kFragmentSplat, "_FragmentAngularVelocity",
                        src.angularVelocity != null && src.angularVelocity.IsValid() ? src.angularVelocity : src.velocity);
                    cs.SetBuffer(kFragmentSplat, "_FragmentLife", src.life);
                    cs.SetBuffer(kFragmentSplat, "_FragmentCentroidRadius",
                        src.centroidRadius != null && src.centroidRadius.IsValid() ? src.centroidRadius : src.posState);
                    cs.SetBuffer(kFragmentSplat, "_FragmentImpactFluidState",
                        src.impactFluidState != null && src.impactFluidState.IsValid() ? src.impactFluidState : src.life);
                    cs.SetBuffer(kFragmentSplat, "_FragmentImpactFluidMotion",
                        src.impactFluidMotion != null && src.impactFluidMotion.IsValid() ? src.impactFluidMotion : MeshMaskDummyU());
                    cs.SetBuffer(kFragmentSplat, "_FragmentImpactFluidGeometry",
                        src.impactFluidGeometry != null && src.impactFluidGeometry.IsValid() ? src.impactFluidGeometry : src.life);
                    cs.SetBuffer(kFragmentSplat, "_FragmentImpactFluidVelocity",
                        src.impactFluidVelocity != null && src.impactFluidVelocity.IsValid() ? src.impactFluidVelocity : src.velocity);
                    cs.SetBuffer(kFragmentSplat, "_MeshAccum", meshAccumBuf);
                    cs.SetTexture(kFragmentSplat, "_Occ", occ);
                    cs.Dispatch(kFragmentSplat, Mathf.CeilToInt(src.emitCount / 64f), 1, 1);
                }
            }
            cs.SetTexture(kDilateOcc, "_Occ", occ);
            cs.SetTexture(kDilateOcc, "_OccD", occD);
            cs.SetBuffer(kDilateOcc, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kDilateOcc, "_ActiveBricks", activeBricksBuf);
            cs.SetBuffer(kDilateOcc, "_RetiredBricks", retiredBricksBuf);
            DispatchOcc(kDilateOcc);

            // INDIRECT base dispatch: every heavy base kernel runs one thread-group per active brick
            // (via _ActiveBricks + BaseVoxel), so empty space costs literally nothing — not even a thread
            // launch. advection/curl/force/pressure/decay/emit now scale with the active set, not the
            // box. MarkSolids is dirty-driven over the union of the old/new obstacle bounds instead.
            foreach (int k in sparseBaseKernels) cs.SetBuffer(k, "_ActiveBricks", activeBricksBuf);

            // Publish active-brick count as indirect dispatch args.
            cs.SetBuffer(kWriteArgs, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kWriteArgs, "_IndirectArgs", indirectArgs);
            cs.Dispatch(kWriteArgs, 1, 1, 1);

            if (DiagnosticsEnabled) RequestActiveCount();

            // 2. Advect (fields = MacCormack, velocity = SL). Sub-stepped: with a fast jet a single dt advection traces
            // back many voxels and smears, so split it into N steps of dt/N (CFL) — crisp fast flow. Emit/Force/Project
            // run once per frame after, so the extra cost is just the advection kernels × N. _Dt restored below.
            int substeps = Mathf.Max(1, p.substeps);
            // Velocity Dissipation is authored per SOLVER tick. Applying the same multiplier in every substep made
            // an automatic quality increase also over-damp the plume (d^N). Use the Nth root so all substep counts
            // produce the same total tick damping.
            cs.SetFloat("_VelDissipation", Mathf.Pow(Mathf.Clamp(p.velDis, 0f, 1f), 1f / substeps));
            if (substeps > 1) cs.SetFloat("_Dt", p.dt / substeps);
            for (int sub = 0; sub < substeps; sub++)
            {
                // Fused advect: one kernel back-traces once and writes BOTH the fields hat (fieldsTmp) and the
                // self-advected velocity (velNext). MacCormack still reads the pre-swap velCur, so ordering and
                // results are identical to the old three-dispatch sequence.
                // _SolidRO is the CFD through-wall backtrace guard (read-only alias of the coverage mask); it is
                // only sampled when _CfdFractionalSolids is set and _NoSolids is clear, so the Fire path never
                // pays the load and the dummy binding is never read.
                cs.SetTexture(kAdvectFields, "_VelIn", velCur);
                cs.SetTexture(kAdvectFields, "_FieldsIn", fieldsCur);
                cs.SetTexture(kAdvectFields, "_FieldsOut", fieldsTmp);
                cs.SetTexture(kAdvectFields, "_VelOut", velNext);
                cs.SetTexture(kAdvectFields, "_SolidRO", SolidTex);
                DispatchBricks(kAdvectFields);

                cs.SetTexture(kMac, "_VelIn", velCur);
                cs.SetTexture(kMac, "_FieldsN", fieldsCur);
                cs.SetTexture(kMac, "_FieldsHat", fieldsTmp);
                cs.SetTexture(kMac, "_FieldsOut", fieldsNext);
                cs.SetTexture(kMac, "_SolidRO", SolidTex);
                DispatchBricks(kMac);

                Swap(ref fieldsCur, ref fieldsNext);
                Swap(ref velCur, ref velNext);
            }
            if (substeps > 1) cs.SetFloat("_Dt", p.dt);   // restore full-frame dt for Emit / Force / Project / Decay

            // 3. Emit current-frame sources AFTER advection, so combustion/fire is anchored to the emitter volume
            // instead of being born at an advected offset. Channels into _Fields; per-emitter buoyancy into _Vel.w.
            // Brick-indirect: MarkEmitterOcc AABB-marked every driving emitter's falloff support before DilateOcc,
            // so the active set covers every voxel any emitter can reach — identical output to the old dense pass
            // without the full-grid launch + per-voxel emitter SDF sweep. Zero emitters → skip entirely.
            if (emitterCount > 0)
            {
                cs.SetTexture(kEmit, "_Fields", fieldsCur);
                cs.SetTexture(kEmit, "_Vel", velCur);   // .w ← per-emitter buoyancy coefficient (mass-weighted blend)
                cs.SetTexture(kEmit, "_Solid", SolidTex);   // CFD fractional injection mask (Fire: gated off, never read)
                cs.SetBuffer(kEmit, "_Emitters", emitterBuf);
                cs.SetBuffer(kEmit, "_ActiveBricks", activeBricksBuf);
                DispatchBricks(kEmit);
            }

            // 3a. Point-set emission (surface-coverage mask) — additive into the same fields, deduped cells.
            if (pointCount > 0 && pointBuf != null)
            {
                cs.SetInt("_PointCount", pointCount);
                cs.SetTexture(kEmitPoints, "_Fields", fieldsCur);
                cs.SetTexture(kEmitPoints, "_Vel", velCur);   // moving sources blend their velocity into the field
                cs.SetBuffer(kEmitPoints, "_PointSources", pointBuf);
                cs.Dispatch(kEmitPoints, Mathf.CeilToInt(pointCount / 64f), 1, 1);
            }

            // 3b. Fold the GPU surface-coverage splat into the fields (indirect over active bricks —
            // SurfSplat brick-marked every splatted cell before DilateOcc, so nothing is missed). The
            // fold zeroes what it consumes, keeping the accumulator clean for the next step.
            if (surfSources.Count > 0 && surfAccumBuf != null)
            {
                cs.SetTexture(kSurfEmitFold, "_Fields", fieldsCur);
                cs.SetBuffer(kSurfEmitFold, "_SurfAccum", surfAccumBuf);
                cs.SetBuffer(kSurfEmitFold, "_ActiveBricks", activeBricksBuf);
                DispatchBricks(kSurfEmitFold);
            }

            // 3c. Fold occupied mesh/fragment hash slots into the fields + velocity. Every occupied slot owns one
            // unique voxel, so writes are race-free without a resolution³ accumulator or an indirect grid walk.
            if ((meshSources.Count > 0 || fragmentSources.Count > 0) && meshAccumBuf != null)
            {
                cs.SetTexture(kMeshEmitFold, "_Fields", fieldsCur);
                cs.SetTexture(kMeshEmitFold, "_Vel", velCur);
                cs.SetBuffer(kMeshEmitFold, "_MeshAccum", meshAccumBuf);
                DispatchLinear(kMeshEmitFold, meshHashCapacity, "_MeshHashRow");
            }

            // 4. Force fields + emitter buoyancy + drag + per-emitter turbulence/billows (post-drag) + clamp.
            cs.SetTexture(kForce, "_Vel", velCur);
            cs.SetTexture(kForce, "_Fields", fieldsCur);   // temperature → emitter buoyancy body force
            cs.SetTexture(kForce, "_Curl", curl);          // last frame's vorticity → Cauli Billows confinement
            cs.SetBuffer(kForce, "_Forces", forceBuf);
            DispatchBricks(kForce);

            // 4b. Container global DYNAMICS — the proven "magic": vorticity confinement (Swirl) + global curl-noise
            // turbulence/billows, applied over the whole plume. Curl is computed here (pre-projection) and reused by
            // Confinement AND by Render Detail downstream. Confinement amplifies the fluid's own vortices.
            // CFD-only dispatch skips: a laminar CFD study zeroes every strength these kernels apply, yet each is
            // still a full-domain pass under full-domain occupancy. Skipping them there is output-identical (all
            // contributions multiply to zero); Fire keeps its dispatch sequence untouched.
            bool cfdSolverMode = fractionalCfd || cfdThermalOccupancy;
            bool skipVorticity = cfdSolverMode && p.confineScale <= 0f;
            bool skipCurlNoise = cfdSolverMode && p.turbulence <= 0f && p.billow <= 0f &&
                                 p.interfaceTurb <= 0f && p.ambientLife <= 0f && splineVortexForceCount == 0;
            if (!skipVorticity)
            {
                // _Solid feeds the CFD one-sided curl and the confinement wall mask (fractional-only reads).
                cs.SetTexture(kCurl, "_Vel", velCur);
                cs.SetTexture(kCurl, "_Curl", curl);
                cs.SetTexture(kCurl, "_Solid", SolidTex);
                DispatchBricks(kCurl);
                cs.SetTexture(kConf, "_Curl", curl);
                cs.SetTexture(kConf, "_Vel", velCur);
                cs.SetTexture(kConf, "_Solid", SolidTex);
                DispatchBricks(kConf);
            }
            if (!skipCurlNoise)
            {
                cs.SetTexture(kCurlNoise, "_Fields", fieldsCur);
                cs.SetTexture(kCurlNoise, "_Vel", velCur);
                // Spline Vortex deliberately runs at the END of this existing global-dynamics pass, after confinement,
                // billows and turbulence. It therefore remains the final calm/conforming velocity guide without adding
                // another dispatch or buffer.
                cs.SetBuffer(kCurlNoise, "_Forces", forceBuf);
                cs.SetBuffer(kCurlNoise, "_SplineVortexSamples", splineVortexBuf);
                DispatchBricks(kCurlNoise);
            }

            // 5. Obstacles → solid mask (voxelize primitives; static walls for the pressure solve).
            // Obstacle descriptors are hashed during upload, so a static field is voxelized ONCE and then reused.
            // A changed primitive only dispatches over the union of its previous/current conservative bounds;
            // the transition to zero similarly visits just the old bounds to clear them.
            if (solid != null && (cfdSdfChanged || obstaclesChanged || prevObstacleCount < 0))
            {
                Vector3Int dispatchMin = cfdSdfChanged ? Vector3Int.zero : obstacleDirtyMin;
                Vector3Int dispatchSize = cfdSdfChanged ? res : obstacleDirtySize;
                if (dispatchSize.x > 0 && dispatchSize.y > 0 && dispatchSize.z > 0)
                {
                    SetInts3("_SolidDispatchMin", dispatchMin.x, dispatchMin.y, dispatchMin.z);
                    SetInts3("_SolidDispatchSize", dispatchSize.x, dispatchSize.y, dispatchSize.z);
                    cs.SetTexture(kMarkSolids, "_Solid", SolidTex);
                    cs.SetBuffer(kMarkSolids, "_Obstacles", obstacleBuf);
                    cs.Dispatch(kMarkSolids,
                        Mathf.CeilToInt(dispatchSize.x / 4f),
                        Mathf.CeilToInt(dispatchSize.y / 4f),
                        Mathf.CeilToInt(dispatchSize.z / 4f));
                }
            }
            bool solidMaskChanged = cfdSdfChanged || obstaclesChanged || prevObstacleCount < 0;
            if (solid != null && requestedCfdSdf != null && solidMaskChanged)
            {
                cs.SetTexture(kApplyCfdSdf, "_Solid", SolidTex);
                cs.SetTexture(kApplyCfdSdf, "_CfdSignedDistance", requestedCfdSdf);
                cs.SetFloat("_CfdCellWorldSize", Mathf.Max(requestedCfdCellWorldSize, 1e-6f));
                cs.Dispatch(kApplyCfdSdf, groups.x, groups.y, groups.z);
            }
            if (solidMaskChanged) InvalidateCfdCoarseSolid();   // multigrid coarse coverage mirrors _Solid
            appliedCfdSdf = requestedCfdSdf;
            appliedCfdSdfVersion = requestedCfdSdfVersion;
            appliedCfdCellWorldSize = requestedCfdCellWorldSize;
            prevObstacleCount = obstacleCount;

            // Animated (rigid-motion) bodies: first restore EVERY previous and current footprint to the static
            // state, then merge every current body. Keeping all restores ahead of all merges handles overlap,
            // registry reordering, removal, and the transition to zero bodies without leaving ghost coverage.
            if ((cfdBodyPrevWindowCount > 0 || cfdAnimatedBodyCount > 0) && solid != null)
            {
                cs.SetInt("_CfdHasStaticSdf", requestedCfdSdf != null ? 1 : 0);
                cs.SetFloat("_CfdCellWorldSize", Mathf.Max(requestedCfdCellWorldSize, 1e-6f));
                cs.SetTexture(kRestoreCfdStaticSolid, "_Solid", SolidTex);
                cs.SetTexture(kRestoreCfdStaticSolid, "_CfdSignedDistance",
                    requestedCfdSdf != null ? (Texture)requestedCfdSdf : solidDummy);

                BuildCfdRestoreWindows();
                for (int i = 0; i < cfdRestoreWindowCount; i++)
                {
                    Vector3Int restoreMin = cfdRestoreWindowMin[i];
                    Vector3Int restoreSize = cfdRestoreWindowSize[i];
                    MarkCfdCoarseSolidDirty(restoreMin, restoreSize);
                    SetInts3("_SolidDispatchMin", restoreMin.x, restoreMin.y, restoreMin.z);
                    SetInts3("_SolidDispatchSize", restoreSize.x, restoreSize.y, restoreSize.z);
                    cs.Dispatch(kRestoreCfdStaticSolid,
                        Mathf.CeilToInt(restoreSize.x / 4f), Mathf.CeilToInt(restoreSize.y / 4f),
                        Mathf.CeilToInt(restoreSize.z / 4f));
                }

                for (int i = 0; i < cfdAnimatedBodyCount; i++)
                {
                    CfdAnimatedBody body = cfdAnimatedBodies[i];
                    if (body.sdf == null ||
                        body.windowSize.x <= 0 || body.windowSize.y <= 0 || body.windowSize.z <= 0) continue;
                    SetInts3("_SolidDispatchMin", body.windowMin.x, body.windowMin.y, body.windowMin.z);
                    SetInts3("_SolidDispatchSize", body.windowSize.x, body.windowSize.y, body.windowSize.z);
                    cs.SetMatrix("_CfdBodyVoxelToUvw", body.voxelToUvw);
                    cs.SetTexture(kApplyCfdBodySdf, "_Solid", SolidTex);
                    cs.SetTexture(kApplyCfdBodySdf, "_CfdBodySdf", body.sdf);
                    cs.Dispatch(kApplyCfdBodySdf,
                        Mathf.CeilToInt(body.windowSize.x / 4f), Mathf.CeilToInt(body.windowSize.y / 4f),
                        Mathf.CeilToInt(body.windowSize.z / 4f));
                }
            }

            for (int i = 0; i < cfdAnimatedBodyCount; i++)
            {
                cfdBodyPrevWindowMin[i] = cfdAnimatedBodies[i].windowMin;
                cfdBodyPrevWindowSize[i] = cfdAnimatedBodies[i].windowSize;
            }
            cfdBodyPrevWindowCount = cfdAnimatedBodyCount;

            if (cfdBoundaryCount > 0) DispatchCfdBoundaryFaces();

            // 6. Pressure projection (solid-aware: no flux into obstacles, Neumann pressure BC).
            // CADENCE: the divergence-free correction is the solver's heaviest cost. Run the FULL solve only every Nth
            // step (step 0 always corrects); transport + forces carry the between-steps. Velocity isn't divergence-free
            // until the next correction — the deliberate trade. A higher-quality solve run less often beats a weak solve
            // run constantly. This is the live-sim twin of cache keyframing (same "Transport often, Correct rarely").
            // AMORTIZED solve: a FULL solve (jacobiIters) every Nth step, a cheap warm-started TRICKLE on the between-steps.
            // The pressure field persists frame-to-frame, so a couple of trickle iters keep velocity continuously near-
            // divergence-free — buoyancy couples into bulk flow incrementally instead of piling up as divergence and
            // getting over-cancelled in one burst (which reads as "smoke moves less"). trickle=0 → pure skip (max FPS).
            int cadence = Mathf.Max(1, p.pressureCadence);
            bool full = (simStep % cadence) == 0;
            int iters = full ? Mathf.Max(1, p.jacobiIters) : Mathf.Max(0, p.pressureTrickle);
            if (iters > 0)
            {
                int divergenceKernel = fractionalCfd ? kDivCfd : kDiv;
                int pressureKernel = fractionalCfd ? kRBGSCfd : kRBGS;
                int projectKernel = fractionalCfd ? kProjCfd : kProj;

                cs.SetTexture(divergenceKernel, "_Vel", velCur);
                cs.SetTexture(divergenceKernel, "_Div", div);
                cs.SetTexture(divergenceKernel, "_Pressure", pressure);
                cs.SetTexture(divergenceKernel, "_Solid", SolidTex);
                cs.SetTexture(divergenceKernel, "_Fields", fieldsCur);   // gas-expansion source (ignition × fuel)
                cs.SetBuffer(divergenceKernel, "_Emitters", emitterBuf);
                if (fractionalCfd) cs.SetBuffer(divergenceKernel, "_CfdBoundaries", cfdBoundaryBuf);
                DispatchBricks(divergenceKernel);

                cs.SetTexture(pressureKernel, "_Pressure", pressure);   // warm start: pressure persists across frames
                cs.SetTexture(pressureKernel, "_Div", div);
                cs.SetTexture(pressureKernel, "_Solid", SolidTex);
                cs.SetBuffer(pressureKernel, "_ActiveBricks", activeBricksBuf);
                if (fractionalCfd) cs.SetBuffer(pressureKernel, "_CfdBoundaries", cfdBoundaryBuf);
                // Full-domain CFD runs a V-cycle: at realtime iteration budgets plain RBGS cannot carry the inlet
                // condition across the domain (information travels one to two cells per sweep), so air behaved
                // compressibly — piling up in front of a body instead of deflecting around it. Sparse domains keep
                // the plain smoother: their inactive-brick divergence is stale, which a dense coarse level would
                // integrate as truth.
                if (fractionalCfd && cfdFullDomainOccupancy && full)
                    SolveCfdPressureVCycle(iters, p.closedFaces);
                else
                    SmoothFinePressure(iters, pressureKernel);

                cs.SetTexture(projectKernel, "_Pressure", pressure);
                cs.SetTexture(projectKernel, "_Vel", velCur);
                cs.SetTexture(projectKernel, "_Solid", SolidTex);
                cs.SetBuffer(projectKernel, "_Emitters", emitterBuf);   // legacy Project directional Push binding
                if (fractionalCfd) cs.SetBuffer(projectKernel, "_CfdBoundaries", cfdBoundaryBuf);
                DispatchBricks(projectKernel);

                // Projection is allowed to adjust the interior field, but a Dirichlet inlet is authoritative at
                // its face. Reapply it after pressure; outlets only perform their optional backflow clamp.
                if (cfdBoundaryCount > 0) DispatchCfdBoundaryFaces();
            }

            // Animated-body wall velocity — inlet-style post-projection authority: covered cells carry the rigid
            // surface velocity (v = v_pivot + ω × r) into the next tick's advection, which is what makes a
            // spinning propeller push and swirl air rather than acting as a static blurred obstacle.
            if (cfdAnimatedBodyCount > 0 && solid != null)
            {
                for (int i = 0; i < cfdAnimatedBodyCount; i++)
                {
                    CfdAnimatedBody body = cfdAnimatedBodies[i];
                    if (body.sdf == null ||
                        body.windowSize.x <= 0 || body.windowSize.y <= 0 || body.windowSize.z <= 0) continue;
                    SetInts3("_SolidDispatchMin", body.windowMin.x, body.windowMin.y, body.windowMin.z);
                    SetInts3("_SolidDispatchSize", body.windowSize.x, body.windowSize.y, body.windowSize.z);
                    cs.SetMatrix("_CfdBodyVoxelToUvw", body.voxelToUvw);
                    cs.SetVector("_CfdBodyLinVelVox", body.linearVelVox);
                    cs.SetVector("_CfdBodyAngVelGrid", body.angularVelGrid);
                    cs.SetVector("_CfdBodyPivotVox", body.pivotVox);
                    cs.SetTexture(kApplyCfdBodyMotion, "_Vel", velCur);
                    cs.SetTexture(kApplyCfdBodyMotion, "_CfdBodySdf", body.sdf);
                    cs.Dispatch(kApplyCfdBodyMotion,
                        Mathf.CeilToInt(body.windowSize.x / 4f), Mathf.CeilToInt(body.windowSize.y / 4f),
                        Mathf.CeilToInt(body.windowSize.z / 4f));
                }
            }

            // Overlay pressure conditioning — every tick (unlike the reductions below, this feeds a continuously
            // rendered surface, so it must track the solve), but only over the body window, so the cost is a few
            // thousand threads. Seeded with a full-weight first write so the overlay never fades in from zero.
            if (cfdSurfaceDiagnostics && cfdSdfBodyActive)
            {
                if (cfdPressureVis == null)
                {
                    cfdPressureVis = Make(res, RenderTextureFormat.RHalf);
                    cfdPressureVisSeeded = false;
                }
                if (cfdRefPressureBuf == null)
                {
                    cfdRefPressureBuf = new ComputeBuffer(4, sizeof(int));
                    cfdRefPressureBuf.SetData(new[] { 0, 0, 0, 1024 }); // theoretical attenuation=1 until measured
                }
                // Cp overlay chain: domain-ambient reference (kills the solve's drifting global level) +
                // theoretical dynamic-head pre-scale + measured-stagnation gain (see the smooth kernel).
                // The projection applies v -= ∇φ with NO dt factor, so the stored field is the splitting
                // potential φ = (p/ρ)·dt_projection / h². When a trickle solve projects every tick the interval
                // is dt; only a true cadence skip (zero trickle iterations) accumulates a cadence-wide impulse.
                // The reference speed is capped by the solver clamp: past it, requested speed cannot reach the field.
                float dynamicHead = CfdProjectionDynamicHead(p.cfdInletSpeedVox, p.maxSpeed, p.dt,
                                                             p.pressureCadence, p.pressureTrickle);
                // Cp is undefined at zero freestream speed. Resolve that state to neutral instead of amplifying
                // residual projection noise with either a tiny denominator or a raw-pressure fallback.
                cs.SetFloat("_CfdPressureVisScale", dynamicHead > 1e-6f ? 1f / dynamicHead : 0f);
                float temporalBlend = 1f - Mathf.Exp(-Mathf.Max(p.dt, 1e-4f) / CfdPressureVisTimeConstant);
                cs.SetFloat("_CfdPressureCalibrationBlend", temporalBlend);
                // The pressure texture itself receives a full-weight first write so it never fades in from zero;
                // calibration does not, which prevents startup noise from being promoted immediately to Cp=+1.
                cs.SetFloat("_PressureVisBlend", cfdPressureVisSeeded ? temporalBlend : 1f);
                cs.SetBuffer(kClearCfdRefPressure, "_CfdRefPressure", cfdRefPressureBuf);
                cs.Dispatch(kClearCfdRefPressure, 1, 1, 1);
                cs.SetTexture(kMeasureCfdRefPressure, "_Pressure", pressure);
                cs.SetTexture(kMeasureCfdRefPressure, "_Solid", SolidTex);
                cs.SetBuffer(kMeasureCfdRefPressure, "_CfdRefPressure", cfdRefPressureBuf);
                cs.Dispatch(kMeasureCfdRefPressure, Mathf.CeilToInt(res.x / 32f),
                    Mathf.CeilToInt(res.y / 32f), Mathf.CeilToInt(res.z / 32f));
                SetInts3("_CfdLoadMin", cfdLoadMin.x, cfdLoadMin.y, cfdLoadMin.z);
                SetInts3("_CfdLoadSize", cfdLoadSize.x, cfdLoadSize.y, cfdLoadSize.z);
                cs.SetTexture(kMeasureCfdStagnationPressure, "_Pressure", pressure);
                cs.SetTexture(kMeasureCfdStagnationPressure, "_Solid", SolidTex);
                cs.SetBuffer(kMeasureCfdStagnationPressure, "_CfdBoundaries", cfdBoundaryBuf);
                cs.SetBuffer(kMeasureCfdStagnationPressure, "_CfdRefPressure", cfdRefPressureBuf);
                cs.Dispatch(kMeasureCfdStagnationPressure,
                    Mathf.CeilToInt(cfdLoadSize.x / 4f),
                    Mathf.CeilToInt(cfdLoadSize.y / 4f),
                    Mathf.CeilToInt(cfdLoadSize.z / 4f));
                cs.SetBuffer(kFinalizeCfdPressureCalibration, "_CfdRefPressure", cfdRefPressureBuf);
                cs.Dispatch(kFinalizeCfdPressureCalibration, 1, 1, 1);
                cs.SetTexture(kCfdSmoothPressureVis, "_Pressure", pressure);
                cs.SetTexture(kCfdSmoothPressureVis, "_Solid", SolidTex);
                cs.SetBuffer(kCfdSmoothPressureVis, "_CfdBoundaries", cfdBoundaryBuf);
                cs.SetBuffer(kCfdSmoothPressureVis, "_CfdRefPressure", cfdRefPressureBuf);
                cs.SetTexture(kCfdSmoothPressureVis, "_PressureVis", cfdPressureVis);
                cs.Dispatch(kCfdSmoothPressureVis,
                    Mathf.CeilToInt(cfdLoadSize.x / 4f),
                    Mathf.CeilToInt(cfdLoadSize.y / 4f),
                    Mathf.CeilToInt(cfdLoadSize.z / 4f));
                cfdPressureVisSeeded = true;
            }

            // Diagnostics run on the READBACK cadence, not every solver tick: the totals are consumed at most
            // once per readback interval, so measuring in between was pure discarded GPU work (the surface-load
            // reduction alone was a full-domain pass per tick before it was windowed to the body AABB).
            if (cfdBoundaryCount > 0 && !cfdFluxReadbackPending &&
                Time.frameCount - cfdFluxReadbackFrame >= CfdFluxReadbackFrameInterval)
            {
                cs.SetBuffer(kClearCfdFlux, "_CfdFluxTotals", cfdFluxBuf);
                cs.Dispatch(kClearCfdFlux, 1, 1, 1);
                cs.SetTexture(kMeasureCfdFluxFace, "_Vel", velCur);
                cs.SetTexture(kMeasureCfdFluxFace, "_Solid", SolidTex);
                cs.SetBuffer(kMeasureCfdFluxFace, "_CfdBoundaries", cfdBoundaryBuf);
                cs.SetBuffer(kMeasureCfdFluxFace, "_CfdFluxTotals", cfdFluxBuf);
                cs.Dispatch(kMeasureCfdFluxFace, cfdFaceGroups.x, cfdFaceGroups.y, cfdBoundaryCount);
                RequestCfdFlux();
            }
            if (cfdSurfaceDiagnostics && cfdSdfBodyActive && cfdSurfaceLoadBuf != null &&
                !cfdSurfaceLoadReadbackPending &&
                Time.frameCount - cfdSurfaceLoadReadbackFrame >= CfdFluxReadbackFrameInterval)
            {
                cs.SetBuffer(kClearCfdSurfaceLoad, "_CfdSurfaceLoad", cfdSurfaceLoadBuf);
                cs.Dispatch(kClearCfdSurfaceLoad, 1, 1, 1);
                SetInts3("_CfdLoadMin", cfdLoadMin.x, cfdLoadMin.y, cfdLoadMin.z);
                SetInts3("_CfdLoadSize", cfdLoadSize.x, cfdLoadSize.y, cfdLoadSize.z);
                cs.SetTexture(kMeasureCfdSurfaceLoadWindow, "_Pressure", pressure);
                cs.SetTexture(kMeasureCfdSurfaceLoadWindow, "_Solid", SolidTex);
                cs.SetBuffer(kMeasureCfdSurfaceLoadWindow, "_CfdBoundaries", cfdBoundaryBuf);
                cs.SetBuffer(kMeasureCfdSurfaceLoadWindow, "_CfdSurfaceLoad", cfdSurfaceLoadBuf);
                cs.Dispatch(kMeasureCfdSurfaceLoadWindow,
                    Mathf.CeilToInt(cfdLoadSize.x / 4f),
                    Mathf.CeilToInt(cfdLoadSize.y / 4f),
                    Mathf.CeilToInt(cfdLoadSize.z / 4f));
                RequestCfdSurfaceLoad();
            }
            simStep++;

            // 7. Combustion + decay (clears smoke inside obstacles). Vorticity (curl) was computed at 4b above —
            // pre-projection, with the confinement-amplified swirls — and is what Render Detail reads.

            cs.SetTexture(kDecay, "_Fields", fieldsCur);
            cs.SetTexture(kDecay, "_Solid", SolidTex);
            DispatchBricks(kDecay);

            // Body Contact dye: surface-shell release windowed to the body AABB. After Decay so this tick's seed
            // reaches the render-field build undecayed; the kernel's max() write keeps the release rate stable.
            if (p.cfdContactDyeDensity > 0f && cfdSdfBodyActive)
            {
                SetInts3("_CfdLoadMin", cfdLoadMin.x, cfdLoadMin.y, cfdLoadMin.z);
                SetInts3("_CfdLoadSize", cfdLoadSize.x, cfdLoadSize.y, cfdLoadSize.z);
                cs.SetTexture(kCfdSeedContactDye, "_Fields", fieldsCur);
                cs.SetTexture(kCfdSeedContactDye, "_Vel", velCur);
                cs.SetTexture(kCfdSeedContactDye, "_Solid", SolidTex);
                cs.Dispatch(kCfdSeedContactDye,
                    Mathf.CeilToInt(cfdLoadSize.x / 4f),
                    Mathf.CeilToInt(cfdLoadSize.y / 4f),
                    Mathf.CeilToInt(cfdLoadSize.z / 4f));
            }

            // Build the renderable visual field BEFORE the solver clears inactive bricks. This decouples visual
            // quality from sparse-solve housekeeping: the sim can hard-zero stale brick residue, while the renderer
            // samples a cheap smoothed copy with a very short decayed tail at cull boundaries.
            // RenderFieldEnabled: a CFD analysis-slice view samples the SIM textures directly and never draws the
            // volume — skipping this full-grid pass per tick is free performance there.
            if (RenderFieldEnabled) BuildRenderResources(p.occThreshold, p.dt, true);

            // Auto temperature colour uses the actual field consumed by the selected presentation, never source
            // settings. A thin SDF-side thermal shell is deliberately smoothed by BuildRenderField; measuring the
            // unsmoothed solver spike made that shell set a high range it could not visibly reach, turning the
            // hottest rendered air blue. Volume mode therefore reduces renderCur after conditioning, while an
            // Analysis Slice (which samples the simulation texture directly) retains the raw solved-air range.
            if (cfdTemperatureDiagnosticsEnabled && !cfdTemperaturePeakReadbackPending &&
                Time.frameCount - cfdTemperaturePeakReadbackFrame >= CfdFluxReadbackFrameInterval)
            {
                if (cfdTemperaturePeakBuf == null)
                {
                    cfdTemperaturePeakBuf = new ComputeBuffer(1, sizeof(uint));
                    cfdTemperaturePeakBuf.SetData(new uint[1]);
                }
                cs.SetBuffer(kClearCfdTemperaturePeak, "_CfdTemperaturePeak", cfdTemperaturePeakBuf);
                cs.Dispatch(kClearCfdTemperaturePeak, 1, 1, 1);
                cs.SetTexture(kMeasureCfdTemperaturePeak, "_Fields",
                    RenderFieldEnabled && renderCur != null ? renderCur : fieldsCur);
                cs.SetTexture(kMeasureCfdTemperaturePeak, "_Solid", SolidTex);
                cs.SetBuffer(kMeasureCfdTemperaturePeak, "_ActiveBricks", activeBricksBuf);
                cs.SetBuffer(kMeasureCfdTemperaturePeak, "_CfdTemperaturePeak", cfdTemperaturePeakBuf);
                DispatchBricks(kMeasureCfdTemperaturePeak);
                RequestCfdTemperaturePeak();
            }

            // Zero every ping-pong copy of bricks that JUST left the active set. Clearing only `fieldsCur` let
            // the uncleared `fieldsNext` become current on a later advection swap, resurrecting retired 4³ cubes
            // in the render field; stale velocity returned the same way. The kernel now clears both field and
            // velocity pairs in this SAME sparse dispatch. A dense sweep remains only for external dense writes,
            // where content can legitimately exist outside the tracked halo.
            if (pendingDenseInactiveClear)
            {
                pendingDenseInactiveClear = false;
                cs.SetTexture(kClearInactiveFields, "_Fields", fieldsCur);
                cs.SetTexture(kClearInactiveFields, "_FieldsClearAux", fieldsNext);
                cs.SetTexture(kClearInactiveFields, "_Vel", velCur);
                cs.SetTexture(kClearInactiveFields, "_VelClearAux", velNext);
                cs.SetTexture(kClearInactiveFields, "_Pressure", pressure);
                cs.SetTexture(kClearInactiveFields, "_OccD", occD);
                Dispatch(kClearInactiveFields);
            }
            else
            {
                cs.SetBuffer(kClearRetiredBricks, "_RetiredBricks", retiredBricksBuf);
                cs.SetTexture(kClearRetiredBricks, "_Fields", fieldsCur);
                cs.SetTexture(kClearRetiredBricks, "_FieldsClearAux", fieldsNext);
                cs.SetTexture(kClearRetiredBricks, "_Vel", velCur);
                cs.SetTexture(kClearRetiredBricks, "_VelClearAux", velNext);
                cs.SetTexture(kClearRetiredBricks, "_Pressure", pressure);
                cs.DispatchIndirect(kClearRetiredBricks, indirectArgs, 12);   // triplet [3..5] = retired count
            }

            // Render Detail is a transported fine density/burn field. It seeds from the current de-voxeled render
            // field, advects with the sim velocity, and re-anchors its low band to the current coarse plume. The
            // raymarch samples it in one tap. Cap the fine field so its VRAM (RGHalf = 4 B/voxel) stays bounded.
            // 512 allows up to a 256³ base → 512³ fine ≈ 512 MB; above that the field is dropped (no detail).
            int maxAxis = Mathf.Max(res.x, Mathf.Max(res.y, res.z));
            int fineScale = FitFineScale(p.fineUpres, maxAxis, 512);   // 4× needs base ≤128, 2× needs base ≤256
            bool canBakeDetail = p.fineAmount > 1e-4f && fineScale >= 2;
            if (canBakeDetail)
                BakeRenderDetail(p.fineAmount, p.fineFeature, p.fineFlow, p.fineSharp, p.fineQuality,
                                 p.fineMotion, p.finePersist, p.fineSpeed, p.time,
                                 p.fineCrisp, p.fineGrainMin, p.fineGrainMax, fineScale, p.dt);
            else if (fineCur != null)
            {
                ReleaseFine();   // grid too big / Amount 0 -> drop the fine field; renderer falls back to the base field
            }

            // 7b. Grid-space light bakes (ported): single-scatter transmittance (Illumination) + multiple-
            // scattering radiance (ScatterSeed + 2× ScatterBlur). Reads the final post-decay base density; the
            // renderer shades live from the packed volume. CADENCE: light is diffuse and moves at smoke speed,
            // so rebaking every Nth step (LightBakeInterval) and reusing the pack between bakes is visually free
            // at 2 — the whole bake stack (up to ~9 light-grid dispatches) drops out of the skipped steps.
            if (illumField != null && LightingEnabled &&
                (lightBakeDirty || (simStep % Mathf.Max(1, LightBakeInterval)) == 0))
            {
                DispatchLighting();
                lightBakeDirty = false;
            }
        }

        /// <summary>Set this frame's point-set emission (deduped covered cells). Call before Step().</summary>
        public void SetPointSources(PointSource[] data, int count)
        {
            pointCount = Mathf.Max(0, count);
            if (pointCount == 0) return;
            if (pointBuf == null || pointCount > pointBuf.count)
            {
                pointBuf?.Release();
                pointBuf = new ComputeBuffer(Mathf.NextPowerOfTwo(pointCount), PointStride);
            }
            pointBuf.SetData(data, 0, 0, pointCount);
        }

        // ── GPU surface-coverage emission (no CPU round-trip) ────────────────────────────────────
        /// <summary>One surface effect's GPU emission inputs. The vertex buffer is the LIVE one (GPU-
        /// skinned for a SkinnedMeshRenderer), so splats track animation at full rate; the descriptor
        /// itself (front, transform, buffers) is refreshed by the container at its gather cadence.</summary>
        public struct SurfaceSourceGpu
        {
            public GraphicsBuffer verts;       // raw vertex buffer (owner: the surface effect)
            public int stride, posOffset;      // vertex layout
            public int weldedCount;
            public ComputeBuffer weldRep;      // welded id -> raw vertex index (owner: the effect)
            public ComputeBuffer reach;        // welded id -> coverage reach (owner: the effect)
            public float front;                // covered when reach <= front
            public float bandMin;              // emit-front-only lower bound; < 0 = whole covered area
            public Vector3 channels;           // density/temperature/fuel per covered vertex
            public Matrix4x4 localToWorld;
        }

        readonly List<SurfaceSourceGpu> surfSources = new List<SurfaceSourceGpu>();
        Matrix4x4 worldToVoxelM = Matrix4x4.identity;
        ComputeBuffer surfAccumBuf;   // cells*3 uints, fixed-point channel sums

        /// <summary>One Mesh-shape BrazePyroEmitter's GPU fluid-emission inputs. Positions is the LIVE
        /// world-space vertex buffer (BrazeDeformSource — GPU-skinned, no readback); prevPos is the emitter-
        /// owned per-vertex previous positions used for the surface-velocity diff.</summary>
        public struct MeshSourceGpu
        {
            public GraphicsBuffer positions;   // world-space verts (owner: the emitter's BrazeDeformSource)
            public GraphicsBuffer normals;     // world-space unit normals (owner: BrazeDeformSource) — normal-speed push
            public GraphicsBuffer prevPos;     // per-vertex previous world pos (owner: the emitter)
            public int vertCount;
            public bool hasPrev;               // false on the emitter's first splat / a vertex-count change
            public Vector3 channels;           // density/temperature/fuel × emissionScale
            public float buoyancy;             // per-emitter lift coefficient
            public float normalSpeed;          // world m/s pushed out along the surface normal (0 = none)
            public Vector3 jetVelocity;        // grid-space forward Jet target (voxels/sec)
            public Vector3 sourceVelocity;     // grid-space authored source-velocity target (voxels/sec)
            public float sourceVelocityBlend;  // exponential target blend rate (1/sec)
            public float sourceTurbulence;     // grid-space source jitter speed (voxels/sec)
            public Vector3 pushAccel;          // grid-space Push acceleration (voxels/sec^2)
            public float invDt;                // 1 / real seconds between pose refreshes (the pose updates per FRAME,
                                               // not per sim step, so velocity divides by the frame interval — not dt)
            public BrazeSurfaceMask mask;       // optional GPU mask gate; null = emit all verts
            public GraphicsBuffer maskRestLocal;// mesh-local rest positions, index-aligned with positions
            public Matrix4x4 maskRestLocalToField;
            public GraphicsBuffer maskPassIdx;  // optional compacted pass set, owner: emitter
            public GraphicsBuffer maskPassCount;// 1 uint, owner: emitter
            public bool maskUsePassSet;         // true = MeshSplat samples maskPassIdx instead of evaluating the field
            public bool maskPassDirty;          // true = solver must rebuild maskPassIdx this step
            public bool positionsAreLocal;       // static sampled mesh points; compute transforms them to world
            public Matrix4x4 localToWorld;
            public Matrix4x4 normalLocalToWorld;
        }

        /// <summary>One Voronoi fracture's live GPU simulation inputs. All buffers are owned by the fracture;
        /// the solver reads them in FragmentSplat and never requests a CPU snapshot.</summary>
        public struct FragmentSourceGpu
        {
            public GraphicsBuffer posState;    // world centroid xyz + anchored/free state in w
            public GraphicsBuffer rotation;    // current fragment quaternion
            public GraphicsBuffer velocity;    // world velocity xyz
            public GraphicsBuffer angularVelocity; // world angular velocity, radians/sec
            public GraphicsBuffer life;        // current fragment scale/dissolve in x
            public GraphicsBuffer centroidRadius; // anchored centroid xyz + authored world radius in w
            public GraphicsBuffer impactFluidState;  // xyz impact-emission rates + remaining seconds
            public GraphicsBuffer impactFluidMotion; // packed buoyancy + fragment-velocity inheritance
            public GraphicsBuffer impactFluidGeometry; // local surface direction/bias + authored duration
            public GraphicsBuffer impactFluidVelocity; // local ejecta velocity + breakup speed
            public bool impactFiltered;        // emit only fragments whose impact timer remains active
            public float impactBuoyancyScale;  // container resolution scaling for packed authored buoyancy
            public int count;
            public int emitCount;               // deterministic selected subset dispatched this step
            public int hashEntryCount;           // upper bound for sparse-hash sizing; 0 falls back to emitCount
            public Vector3 channels;           // density/temperature/fuel × emissionScale
            public float buoyancy;
            public float velocityInherit;      // 0 = no fragment motion, 1 = full fragment velocity
            public Vector3 jetVelocity;
            public Vector3 sourceVelocity;
            public float sourceVelocityBlend;
            public float sourceTurbulence;
            public Vector3 pushAccel;           // grid-space Push acceleration (voxels/sec^2)
        }

        readonly List<MeshSourceGpu> meshSources = new List<MeshSourceGpu>();
        readonly List<FragmentSourceGpu> fragmentSources = new List<FragmentSourceGpu>();
        const int MeshHashIntsPerSlot = 13;
        ComputeBuffer meshAccumBuf;   // sparse hash: key, channels, surface velocity, birth target, buoyancy/blend/count
        int meshHashCapacity;
        GraphicsBuffer meshMaskDummyU;
        bool meshAccumLimitWarned, surfAccumLimitWarned;

        /// <summary>Replace the GPU mesh/fragment source sets. The sparse accumulator is sized from the maximum
        /// submitted sample count, so its memory and clear/fold cost do not grow with grid resolution.</summary>
        public void SetMeshSources(List<MeshSourceGpu> list, List<FragmentSourceGpu> fragments, Matrix4x4 worldToVoxel)
        {
            meshSources.Clear();
            if (list != null) meshSources.AddRange(list);
            fragmentSources.Clear();
            if (fragments != null) fragmentSources.AddRange(fragments);
            worldToVoxelM = worldToVoxel;   // shared with the surface path (same container gather)

            long entries64 = 0;
            for (int i = 0; i < meshSources.Count; i++) entries64 += Mathf.Max(0, meshSources[i].vertCount);
            for (int i = 0; i < fragmentSources.Count; i++)
            {
                var source = fragmentSources[i];
                entries64 += Mathf.Max(0, source.hashEntryCount > 0 ? source.hashEntryCount : source.emitCount);
            }
            int entries = (int)System.Math.Min(entries64, int.MaxValue);

            // Keep the allocation through short burst gaps to avoid churn; release after sustained disuse.
            if (entries > 0) { meshAccumIdle = 0; EnsureMeshAccum(entries); }
            else if (meshAccumBuf != null && ++meshAccumIdle >= AccumIdleReleaseCalls)
            {
                meshAccumBuf.Release(); meshAccumBuf = null; meshHashCapacity = 0; meshAccumIdle = 0;
            }
        }

        void EnsureMeshAccum(int sourceEntries)
        {
            // A 0.5 maximum load factor keeps linear probes short. Unique occupied cells can never exceed the
            // number of submitted samples, so this also guarantees room without a GPU counter/readback.
            long neededSlots = System.Math.Max(256L, (long)sourceEntries * 2L);
            int capacity = 256;
            const int MaxPowerOfTwoCapacity = 1 << 27;   // capacity*13 must still fit ComputeBuffer.count (int)
            while (capacity < neededSlots && capacity < MaxPowerOfTwoCapacity) capacity <<= 1;
            long count64 = (long)capacity * MeshHashIntsPerSlot;
            long bytes = count64 * sizeof(int);
            if (capacity < neededSlots || count64 > int.MaxValue || bytes > SystemInfo.maxGraphicsBufferSize)
            {
                if (!meshAccumLimitWarned)
                {
                    meshAccumLimitWarned = true;
                    Debug.LogError($"[BrazeFX] Mesh fluid emission disabled: {sourceEntries:N0} GPU source points " +
                                   $"need a {bytes / (1024 * 1024)} MB sparse hash, above this device's buffer limit.");
                }
                meshAccumBuf?.Release(); meshAccumBuf = null; meshHashCapacity = 0;
                return;
            }
            int count = (int)count64;
            meshHashCapacity = capacity;
            cs.SetInt("_MeshHashCapacity", capacity);
            cs.SetInt("_MeshHashMask", capacity - 1);
            int allocatedCapacity = meshAccumBuf != null ? meshAccumBuf.count / MeshHashIntsPerSlot : 0;
            // Grow immediately; shrink only after a substantial drop (e.g. intact mesh → 5% fragment subset)
            // so percentage changes reclaim memory without making ordinary burst/source-count jitter reallocate.
            bool shouldShrink = allocatedCapacity >= capacity * 4;
            if (meshAccumBuf != null && meshAccumBuf.count >= count && !shouldShrink) return;
            meshAccumBuf?.Release();
            meshAccumBuf = new ComputeBuffer(count, sizeof(int));
            cs.SetBuffer(kClearMeshAccum, "_MeshAccum", meshAccumBuf);
            DispatchLinear(kClearMeshAccum, count, "_MeshClearRow");
        }

        GraphicsBuffer MeshMaskDummyU()
        {
            if (meshMaskDummyU == null)
            {
                meshMaskDummyU = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                meshMaskDummyU.SetData(new uint[] { 0 });
            }
            return meshMaskDummyU;
        }

        // Zero a large linear buffer via a 2-D group grid (numthreads 64) so the group count never exceeds the
        // 65535 per-dimension cap — the kernel flattens with i = tid.x + tid.y * rowThreads (`rowUniform`).
        void DispatchLinear(int kernel, int elements, string rowUniform)
        {
            int groups1D = Mathf.CeilToInt(elements / 64f);
            int gx = Mathf.Max(1, Mathf.Min(groups1D, 32768));
            int gy = Mathf.CeilToInt(groups1D / (float)gx);
            cs.SetInt(rowUniform, gx * 64);
            cs.Dispatch(kernel, gx, Mathf.Max(1, gy), 1);
        }

        /// <summary>Replace the GPU surface-source set (persists until the next call — the container
        /// refreshes it at its gather cadence; splat/fold run every step from the live buffers).</summary>
        // Accumulators keep the anti-thrash policy (bursting effects cycle in ms — the buffer stays), but with
        // a bound: a scene that STOPS using surface/mesh emission shouldn't pay res³-scaled VRAM forever
        // (~201 MB surf + ~537 MB mesh @256³). Re-creation on next use is identical to first use (fresh + zeroed).
        const int AccumIdleReleaseCalls = 600;   // gather calls ≈ sim steps → ~10 s at 60 Hz
        int surfAccumIdle, meshAccumIdle;

        public void SetSurfaceSources(List<SurfaceSourceGpu> list, Matrix4x4 worldToVoxel)
        {
            surfSources.Clear();
            if (list != null) surfSources.AddRange(list);
            worldToVoxelM = worldToVoxel;
            if (surfSources.Count > 0) { surfAccumIdle = 0; EnsureSurfAccum(); }
            else if (surfAccumBuf != null && ++surfAccumIdle >= AccumIdleReleaseCalls)
            {
                surfAccumBuf.Release(); surfAccumBuf = null; surfAccumIdle = 0;
            }
        }

        void EnsureSurfAccum()
        {
            long cells64 = (long)res.x * res.y * res.z;
            long count64 = cells64 * 3L;
            long bytes = count64 * sizeof(uint);
            if (count64 > int.MaxValue || bytes > SystemInfo.maxGraphicsBufferSize)
            {
                if (!surfAccumLimitWarned)
                {
                    surfAccumLimitWarned = true;
                    Debug.LogError($"[BrazeFX] Surface fluid emission disabled at {res.x}×{res.y}×{res.z}: its " +
                                   $"{bytes / (1024 * 1024)} MB accumulator exceeds this device's single-buffer limit.");
                }
                surfAccumBuf?.Release(); surfAccumBuf = null;
                return;
            }
            int cells = (int)cells64;
            int count = (int)count64;
            if (surfAccumBuf != null && surfAccumBuf.count == count) return;
            surfAccumBuf?.Release();
            surfAccumBuf = new ComputeBuffer(count, sizeof(uint));
            // Contents are undefined on create → one-time zero (steady state: SurfEmitFold zeroes what it consumes).
            cs.SetInt("_SurfCellCount", cells);
            cs.SetBuffer(kClearSurfAccum, "_SurfAccum", surfAccumBuf);
            DispatchLinear(kClearSurfAccum, count, "_SurfClearRow");
        }

        void UploadEmitters(GpuEmitter[] data, int count)
        {
            if (count > emitterBuf.count)
            {
                emitterBuf.Release();
                emitterBuf = new ComputeBuffer(Mathf.NextPowerOfTwo(count), EmitterStride);
            }
            if (count > 0) emitterBuf.SetData(data, 0, 0, count);
        }

        void UploadForces(GpuForce[] data, int count)
        {
            if (count > forceBuf.count)
            {
                forceBuf.Release();
                forceBuf = new ComputeBuffer(Mathf.NextPowerOfTwo(count), ForceStride);
            }
            if (count > 0) forceBuf.SetData(data, 0, 0, count);
        }

        int UploadSplineVortexSamples(Vector4[] data, int count)
        {
            count = data == null ? 0 : Mathf.Clamp(count, 0, data.Length);
            if (count > splineVortexBuf.count)
            {
                splineVortexBuf.Release();
                splineVortexBuf = new ComputeBuffer(Mathf.NextPowerOfTwo(count), sizeof(float) * 4);
            }
            if (count > 0) splineVortexBuf.SetData(data, 0, 0, count);
            return count;
        }

        static int ObstacleHash(GpuObstacle[] data, int count)
        {
            unchecked
            {
                int h = 17 * 31 + count;
                for (int i = 0; i < count; i++)
                {
                    ref GpuObstacle o = ref data[i];
                    h = h * 31 + o.posVox.GetHashCode();
                    h = h * 31 + o.radiusVox.GetHashCode();
                    h = h * 31 + o.halfExtentVox.GetHashCode();
                    h = h * 31 + o.shape.GetHashCode();
                    h = h * 31 + o.rot.GetHashCode();
                }
                return h;
            }
        }

        bool ComputeObstacleBounds(GpuObstacle[] data, int count, out Vector3Int lo, out Vector3Int hi)
        {
            lo = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            hi = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                GpuObstacle o = data[i];
                int shape = Mathf.RoundToInt(o.shape);
                float radius = Mathf.Max(0f, o.radiusVox);
                Vector3 localExt;
                switch (shape)
                {
                    case 0:
                        localExt = Vector3.one * radius;
                        break;
                    case 1:
                        localExt = o.halfExtentVox;
                        break;
                    case 2:
                    case 3:
                        localExt = new Vector3(radius, Mathf.Max(0f, o.halfExtentVox.y), radius);
                        break;
                    case 4:
                        localExt = new Vector3(radius, Mathf.Max(o.halfExtentVox.y, radius), radius);
                        break;
                    default: // torus
                    {
                        float minor = Mathf.Max(0f, o.halfExtentVox.x);
                        float outer = radius + minor;
                        localExt = new Vector3(outer, minor, outer);
                        break;
                    }
                }
                Vector3 ext = localExt;
                if (shape != 0)
                {
                    Quaternion q = new(o.rot.x, o.rot.y, o.rot.z, o.rot.w);
                    Matrix4x4 m = Matrix4x4.Rotate(q);
                    ext = new Vector3(
                        Mathf.Abs(m.m00) * localExt.x + Mathf.Abs(m.m01) * localExt.y + Mathf.Abs(m.m02) * localExt.z,
                        Mathf.Abs(m.m10) * localExt.x + Mathf.Abs(m.m11) * localExt.y + Mathf.Abs(m.m12) * localExt.z,
                        Mathf.Abs(m.m20) * localExt.x + Mathf.Abs(m.m21) * localExt.y + Mathf.Abs(m.m22) * localExt.z);
                }
                Vector3Int a = Vector3Int.FloorToInt(o.posVox - ext - Vector3.one);
                Vector3Int b = Vector3Int.CeilToInt(o.posVox + ext + Vector3.one);
                a = Vector3Int.Max(Vector3Int.zero, a);
                b = Vector3Int.Min(res - Vector3Int.one, b);
                if (a.x > b.x || a.y > b.y || a.z > b.z) continue;
                lo = Vector3Int.Min(lo, a);
                hi = Vector3Int.Max(hi, b);
                any = true;
            }
            return any;
        }

        bool UploadObstacles(GpuObstacle[] data, int count)
        {
            int hash = ObstacleHash(data, count);
            bool changed = !obstacleUploadInitialized || hash != uploadedObstacleHash;
            if (count > obstacleBuf.count)
            {
                obstacleBuf.Release();
                obstacleBuf = new ComputeBuffer(Mathf.NextPowerOfTwo(count), ObstacleStride);
                changed = true;
            }
            if (changed)
            {
                if (count > 0) obstacleBuf.SetData(data, 0, 0, count);
                bool currentValid = ComputeObstacleBounds(data, count, out Vector3Int currentMin, out Vector3Int currentMax);
                bool dirtyValid = currentValid || previousObstacleBoundsValid;
                if (dirtyValid)
                {
                    Vector3Int dirtyMax;
                    if (currentValid && previousObstacleBoundsValid)
                    {
                        obstacleDirtyMin = Vector3Int.Min(currentMin, previousObstacleMin);
                        dirtyMax = Vector3Int.Max(currentMax, previousObstacleMax);
                    }
                    else if (currentValid)
                    {
                        obstacleDirtyMin = currentMin;
                        dirtyMax = currentMax;
                    }
                    else
                    {
                        obstacleDirtyMin = previousObstacleMin;
                        dirtyMax = previousObstacleMax;
                    }
                    obstacleDirtySize = dirtyMax - obstacleDirtyMin + Vector3Int.one;
                }
                else
                {
                    obstacleDirtyMin = Vector3Int.zero;
                    obstacleDirtySize = Vector3Int.zero;
                }
                previousObstacleBoundsValid = currentValid;
                previousObstacleMin = currentMin;
                previousObstacleMax = currentMax;
            }
            uploadedObstacleHash = hash;
            obstacleUploadInitialized = true;
            return changed;
        }

        void DispatchLighting()
        {
            // Lighting bakes run DENSE over the coarse lightRes grid (no active-brick indirect dispatch): the fields
            // are diffuse/low-frequency and the whole grid is small, so a dense pass over lightRes is both simpler
            // and cheaper than the sparse base-res pass — and writing every voxel means no clear is needed.
            RenderTexture lightFields = RenderField;
            RenderTexture lightOcc = RenderOccupancy;
            SetInts3("_Res", res.x, res.y, res.z);                  // base res → light march world-step geometry
            SetInts3("_LightRes", lightRes.x, lightRes.y, lightRes.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);      // dilated-halo skip: bake only near smoke
            void DL(int kernel) => cs.Dispatch(kernel, lightGroups.x, lightGroups.y, lightGroups.z);

            // 1) Single-scatter transmittance toward the key light (reads post-decay base density via SRV). Skips
            // light voxels outside the dilated smoke halo (_OccD) — never rendered, so the shadow march is wasted.
            cs.SetVector("_LbLightDir", lp.lightDirOS);
            cs.SetFloat("_LbShadowDensity", lp.shadowDensity);
            cs.SetFloat("_LbLightStepVox", Mathf.Max(0.25f, lp.lightStepVox));
            cs.SetInt("_LbLightSteps", Mathf.Clamp(lp.lightSteps, 1, 48));
            cs.SetTexture(kIllum, "_FieldsIn", lightFields);
            cs.SetTexture(kIllum, "_IllumField", illumField);
            cs.SetTexture(kIllum, "_OccD", lightOcc);
            DL(kIllum);

            // 2) Multiple scattering: seed radiance = illumination, then ping-pong blur twice → scatterField.
            cs.SetTexture(kScatterSeed, "_IllumField", illumField);
            cs.SetTexture(kScatterSeed, "_ScatterField", scatterField);
            cs.SetTexture(kScatterSeed, "_OccD", lightOcc);
            DL(kScatterSeed);

            cs.SetTexture(kScatterBlur, "_OccD", lightOcc);
            cs.SetTexture(kScatterBlur, "_FieldsIn", lightFields);
            cs.SetTexture(kScatterBlur, "_ScatterSrc", scatterField);   // pass A: scatterField → scatterTmp
            cs.SetTexture(kScatterBlur, "_ScatterField", scatterTmp);
            DL(kScatterBlur);
            cs.SetTexture(kScatterBlur, "_ScatterSrc", scatterTmp);     // pass B: scatterTmp → scatterField
            cs.SetTexture(kScatterBlur, "_ScatterField", scatterField);
            DL(kScatterBlur);

            // 3) Fire as a light: seed radiance from fire emission, then diffuse it through the smoke. Static
            // generated smoke often has no burn channel, so clear b instead of running zero-valued diffusion.
            if (GridFireLightEnabled)
            {
                cs.SetTexture(kFireLightSeed, "_FieldsIn", lightFields);
                cs.SetTexture(kFireLightSeed, "_ScatterField", fireLightField);
                DL(kFireLightSeed);
                for (int k = 0; k < 2; k++)   // 2 iterations × 2 passes = 4 diffusion passes
                {
                    cs.SetTexture(kScatterBlur, "_ScatterSrc", fireLightField);
                    cs.SetTexture(kScatterBlur, "_ScatterField", scatterTmp);
                    DL(kScatterBlur);
                    cs.SetTexture(kScatterBlur, "_ScatterSrc", scatterTmp);
                    cs.SetTexture(kScatterBlur, "_ScatterField", fireLightField);
                    DL(kScatterBlur);
                }
            }
            else
            {
                cs.SetTexture(kClearLightScalar, "_ClearLightScalar", fireLightField);
                DL(kClearLightScalar);
            }

            // 4) Pack the three scalar light fields into one RGBA volume → the renderer samples lighting once
            // (1 tap) per step instead of three.
            cs.SetTexture(kPackLight, "_PackIllum", illumField);
            cs.SetTexture(kPackLight, "_PackScatter", scatterField);
            cs.SetTexture(kPackLight, "_PackFireLight", fireLightField);
            cs.SetTexture(kPackLight, "_LightPacked", lightPacked);
            DL(kPackLight);
        }

        // Zero-alloc SetInts: ComputeShader.SetInts(string, params int[]) allocates the params array
        // on EVERY call (~10 calls per step). One reused scratch kills that steady GC trickle.
        readonly int[] intsScratch = new int[3];
        void SetInts3(string name, int x, int y, int z)
        {
            intsScratch[0] = x; intsScratch[1] = y; intsScratch[2] = z;
            cs.SetInts(name, intsScratch);
        }

        void Dispatch(int kernel) => cs.Dispatch(kernel, groups.x, groups.y, groups.z);
        // Indirect: one BRICK^3 thread-group per active brick (args published by WriteArgs). Empty bricks
        // are never launched, so cost scales with the active set rather than the whole grid.
        void DispatchBricks(int kernel) => cs.DispatchIndirect(kernel, indirectArgs);

        void SmoothFinePressure(int iterations, int kernel)
        {
            for (int i = 0; i < iterations; i++)
            {
                cs.SetInt("_RedBlack", 0); cs.DispatchIndirect(kernel, indirectArgs);
                cs.SetInt("_RedBlack", 1); cs.DispatchIndirect(kernel, indirectArgs);
            }
        }

        // Face-layer boundary application: one thread per patch-covered cell (8×8 groups per patch window,
        // z = boundary index) instead of the former full-domain brick sweep.
        void DispatchCfdBoundaryFaces()
        {
            cs.SetTexture(kApplyCfdBoundariesFace, "_Vel", velCur);
            cs.SetTexture(kApplyCfdBoundariesFace, "_Fields", fieldsCur);
            cs.SetTexture(kApplyCfdBoundariesFace, "_Solid", SolidTex);
            cs.SetBuffer(kApplyCfdBoundariesFace, "_CfdBoundaries", cfdBoundaryBuf);
            cs.Dispatch(kApplyCfdBoundariesFace, cfdFaceGroups.x, cfdFaceGroups.y, cfdBoundaryCount);
        }

        void EnsureCfdCoarseResources()
        {
            Vector3Int wanted = new(Mathf.Max(1, (res.x + 1) / 2), Mathf.Max(1, (res.y + 1) / 2),
                                    Mathf.Max(1, (res.z + 1) / 2));
            if (cfdCoarsePressure != null && cfdCoarseRes == wanted) return;
            ReleaseCfdCoarseResources();
            cfdCoarseRes = wanted;
            cfdCoarsePressure = Make(wanted, RenderTextureFormat.RFloat);
            cfdCoarseDiv = Make(wanted, RenderTextureFormat.RFloat);
            cfdCoarseSolid = Make(wanted, RenderTextureFormat.RHalf);
            InvalidateCfdCoarseSolid();
        }

        void ReleaseCfdCoarseResources()
        {
            Rel(ref cfdCoarsePressure);
            Rel(ref cfdCoarseDiv);
            Rel(ref cfdCoarseSolid);
            cfdCoarseRes = default;
            InvalidateCfdCoarseSolid();
        }

        void InvalidateCfdCoarseSolid()
        {
            cfdCoarseSolidValid = false;
            cfdCoarseDirtyWindowCount = 0;
        }

        // The animated mask is restored over both old and new body footprints before any current body is merged.
        // Coalesce overlapping/touching boxes so a rotating body whose conservative AABB barely changes costs one
        // restore dispatch instead of two identical dispatches. Disjoint teleports stay disjoint, avoiding a huge
        // empty swept-volume dispatch. All arrays are persistent solver scratch: steady-state motion allocates zero.
        void BuildCfdRestoreWindows()
        {
            cfdRestoreWindowCount = 0;
            int wanted = cfdBodyPrevWindowCount + cfdAnimatedBodyCount;
            EnsureWindowCapacity(ref cfdRestoreWindowMin, ref cfdRestoreWindowSize, wanted);
            for (int i = 0; i < cfdBodyPrevWindowCount; i++)
                AddCoalescedWindow(ref cfdRestoreWindowMin, ref cfdRestoreWindowSize,
                    ref cfdRestoreWindowCount, cfdBodyPrevWindowMin[i], cfdBodyPrevWindowSize[i], res);
            for (int i = 0; i < cfdAnimatedBodyCount; i++)
                AddCoalescedWindow(ref cfdRestoreWindowMin, ref cfdRestoreWindowSize,
                    ref cfdRestoreWindowCount, cfdAnimatedBodies[i].windowMin,
                    cfdAnimatedBodies[i].windowSize, res);
        }

        void MarkCfdCoarseSolidDirty(Vector3Int fineMin, Vector3Int fineSize)
        {
            // An invalid texture will receive one full rebuild at the next V-cycle. Window tracking matters only
            // after that baseline exists; dirty windows accumulate if pressure cadence skips the current tick.
            if (!cfdCoarseSolidValid || fineSize.x <= 0 || fineSize.y <= 0 || fineSize.z <= 0) return;
            Vector3Int fineMax = Vector3Int.Min(fineMin + fineSize, res);
            Vector3Int coarseMin = new(fineMin.x / 2, fineMin.y / 2, fineMin.z / 2);
            Vector3Int coarseMax = new((fineMax.x + 1) / 2, (fineMax.y + 1) / 2, (fineMax.z + 1) / 2);
            Vector3Int coarseSize = Vector3Int.Max(coarseMax - coarseMin, Vector3Int.zero);
            EnsureWindowCapacity(ref cfdCoarseDirtyWindowMin, ref cfdCoarseDirtyWindowSize,
                cfdCoarseDirtyWindowCount + 1);
            AddCoalescedWindow(ref cfdCoarseDirtyWindowMin, ref cfdCoarseDirtyWindowSize,
                ref cfdCoarseDirtyWindowCount, coarseMin, coarseSize, cfdCoarseRes);
        }

        static void EnsureWindowCapacity(ref Vector3Int[] mins, ref Vector3Int[] sizes, int wanted)
        {
            if (wanted <= 0 || (mins != null && mins.Length >= wanted)) return;
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(4, wanted));
            System.Array.Resize(ref mins, capacity);
            System.Array.Resize(ref sizes, capacity);
        }

        static void AddCoalescedWindow(ref Vector3Int[] mins, ref Vector3Int[] sizes, ref int count,
                                       Vector3Int rawMin, Vector3Int rawSize, Vector3Int limit)
        {
            Vector3Int min = Vector3Int.Max(rawMin, Vector3Int.zero);
            Vector3Int max = Vector3Int.Min(rawMin + rawSize, limit);
            if (max.x <= min.x || max.y <= min.y || max.z <= min.z) return;

            // Merge transitively: joining two boxes may make the union touch a third one.
            for (int i = 0; i < count;)
            {
                Vector3Int otherMin = mins[i];
                Vector3Int otherMax = otherMin + sizes[i];
                bool separate = max.x < otherMin.x || otherMax.x < min.x ||
                                max.y < otherMin.y || otherMax.y < min.y ||
                                max.z < otherMin.z || otherMax.z < min.z;
                if (separate)
                {
                    i++;
                    continue;
                }
                min = Vector3Int.Min(min, otherMin);
                max = Vector3Int.Max(max, otherMax);
                count--;
                mins[i] = mins[count];
                sizes[i] = sizes[count];
            }
            mins[count] = min;
            sizes[count] = max - min;
            count++;
        }

        // Full-domain CFD pressure V-cycle: pre-smooth → restrict residual to res/2 → dense coarse Gauss-Seidel
        // (each coarse sweep moves information twice as far over 1/8th the cells) → trilinear prolong → post-smooth.
        // The caller has already bound kRBGSCfd's textures/buffers; coarse boundary conditions are the homogeneous
        // fine ones (open faces + pressure outlets pin the error to zero, walls and velocity inlets mirror).
        void SolveCfdPressureVCycle(int fineIterations, int closedFaces)
        {
            EnsureCfdCoarseResources();
            int preSmooth = Mathf.Min(2, fineIterations);
            int postSmooth = Mathf.Max(2, fineIterations - preSmooth);
            SmoothFinePressure(preSmooth, kRBGSCfd);

            SetInts3("_CoarseRes", cfdCoarseRes.x, cfdCoarseRes.y, cfdCoarseRes.z);
            cs.SetInt("_CfdCoarseDirichletFaces", (~closedFaces & 63) | cfdOutletFacesMask);
            Vector3Int coarseGroups = new(Mathf.CeilToInt(cfdCoarseRes.x / 4f),
                                          Mathf.CeilToInt(cfdCoarseRes.y / 4f),
                                          Mathf.CeilToInt(cfdCoarseRes.z / 4f));

            if (!cfdCoarseSolidValid)
            {
                SetInts3("_CoarseDispatchMin", 0, 0, 0);
                SetInts3("_CoarseDispatchSize", cfdCoarseRes.x, cfdCoarseRes.y, cfdCoarseRes.z);
                cs.SetTexture(kCfdBuildCoarseSolid, "_Solid", SolidTex);
                cs.SetTexture(kCfdBuildCoarseSolid, "_CoarseSolid", cfdCoarseSolid);
                cs.Dispatch(kCfdBuildCoarseSolid, coarseGroups.x, coarseGroups.y, coarseGroups.z);
                cfdCoarseSolidValid = true;
                cfdCoarseDirtyWindowCount = 0;
            }
            else if (cfdCoarseDirtyWindowCount > 0)
            {
                cs.SetTexture(kCfdBuildCoarseSolid, "_Solid", SolidTex);
                cs.SetTexture(kCfdBuildCoarseSolid, "_CoarseSolid", cfdCoarseSolid);
                for (int i = 0; i < cfdCoarseDirtyWindowCount; i++)
                {
                    Vector3Int dirtyMin = cfdCoarseDirtyWindowMin[i];
                    Vector3Int dirtySize = cfdCoarseDirtyWindowSize[i];
                    SetInts3("_CoarseDispatchMin", dirtyMin.x, dirtyMin.y, dirtyMin.z);
                    SetInts3("_CoarseDispatchSize", dirtySize.x, dirtySize.y, dirtySize.z);
                    cs.Dispatch(kCfdBuildCoarseSolid,
                        Mathf.CeilToInt(dirtySize.x / 4f), Mathf.CeilToInt(dirtySize.y / 4f),
                        Mathf.CeilToInt(dirtySize.z / 4f));
                }
                cfdCoarseDirtyWindowCount = 0;
            }

            cs.SetTexture(kCfdRestrictResidual, "_Pressure", pressure);
            cs.SetTexture(kCfdRestrictResidual, "_Div", div);
            cs.SetTexture(kCfdRestrictResidual, "_Solid", SolidTex);
            cs.SetBuffer(kCfdRestrictResidual, "_CfdBoundaries", cfdBoundaryBuf);
            cs.SetTexture(kCfdRestrictResidual, "_CoarseDiv", cfdCoarseDiv);
            cs.SetTexture(kCfdRestrictResidual, "_CoarsePressure", cfdCoarsePressure);
            cs.Dispatch(kCfdRestrictResidual, coarseGroups.x, coarseGroups.y, coarseGroups.z);

            cs.SetTexture(kCfdCoarseRBGS, "_CoarsePressure", cfdCoarsePressure);
            cs.SetTexture(kCfdCoarseRBGS, "_CoarseDiv", cfdCoarseDiv);
            cs.SetTexture(kCfdCoarseRBGS, "_CoarseSolid", cfdCoarseSolid);
            for (int i = 0; i < CfdCoarseIterations; i++)
            {
                cs.SetInt("_RedBlack", 0);
                cs.Dispatch(kCfdCoarseRBGS, coarseGroups.x, coarseGroups.y, coarseGroups.z);
                cs.SetInt("_RedBlack", 1);
                cs.Dispatch(kCfdCoarseRBGS, coarseGroups.x, coarseGroups.y, coarseGroups.z);
            }

            cs.SetTexture(kCfdProlongAdd, "_Pressure", pressure);
            cs.SetTexture(kCfdProlongAdd, "_Solid", SolidTex);
            cs.SetBuffer(kCfdProlongAdd, "_CfdBoundaries", cfdBoundaryBuf);
            cs.SetTexture(kCfdProlongAdd, "_CoarsePressureRO", cfdCoarsePressure);
            cs.Dispatch(kCfdProlongAdd, groups.x, groups.y, groups.z);

            SmoothFinePressure(postSmooth, kRBGSCfd);
        }

        void ClearBaseField(RenderTexture rt)
        {
            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetTexture(kClearField, "_ClearF4", rt);
            cs.Dispatch(kClearField, groups.x, groups.y, groups.z);
        }
        void ClearVectorField(RenderTexture rt, Vector3Int clearRes)
        {
            Vector3Int clearGroups = Groups(clearRes, 4);
            SetInts3("_Res", clearRes.x, clearRes.y, clearRes.z);
            cs.SetTexture(kClearField, "_ClearF4", rt);
            cs.Dispatch(kClearField, clearGroups.x, clearGroups.y, clearGroups.z);
            SetInts3("_Res", res.x, res.y, res.z);
        }
        void ClearScalarField(RenderTexture rt)
        {
            ClearScalarField(rt, res);
        }
        void ClearScalarField(RenderTexture rt, Vector3Int clearRes)
        {
            Vector3Int clearGroups = Groups(clearRes, 4);
            SetInts3("_Res", clearRes.x, clearRes.y, clearRes.z);
            cs.SetTexture(kClearScalar, "_ClearScalar", rt);
            cs.Dispatch(kClearScalar, clearGroups.x, clearGroups.y, clearGroups.z);
            SetInts3("_Res", res.x, res.y, res.z);
        }

        /// <summary>Strength of the render-field de-voxel filter: the approximating cubic B-spline baked into the
        /// visual copy once per voxel (BuildRenderField). 1 = full de-voxel (removes coarse-grid voxel tiles that
        /// interpolating reconstruction preserves), 0 = raw voxels. The raymarch stays one tap either way.</summary>
        public float RenderDeVoxel { get; set; } = 1f;
        /// <summary>Renderer-visible extinction mapping used by the fused continuous occupancy output.</summary>
        public float RenderOccDensityGain { get; set; } = 10f;
        public float RenderOccFireGain { get; set; } = 0.5f;

        void BuildRenderResources(float occThreshold, float dt, bool keepVisualHistory)
        {
            if (renderCur == null || renderNext == null || renderOcc == null) return;

            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_RenderVisualFilter", Mathf.Clamp01(RenderDeVoxel));
            cs.SetFloat("_RenderVisualDecay", keepVisualHistory ? Mathf.Exp(-Mathf.Max(0f, dt) * 12f) : 0f);
            cs.SetFloat("_RenderHistoryDt", Mathf.Max(0f, dt));
            cs.SetFloat("_RenderOccDensityGain", Mathf.Max(0f, RenderOccDensityGain));
            cs.SetFloat("_RenderOccFireGain", Mathf.Max(0f, RenderOccFireGain));

            // Ping-pong (NOT in place — see field comment) + fused render occupancy: the max-pool happens in
            // groupshared inside this same dispatch (one 4³ group = one occupancy brick), which deleted the old
            // BuildRenderOcc full-field read pass.
            cs.SetTexture(kBuildRenderField, "_FieldsIn", fieldsCur);
            cs.SetTexture(kBuildRenderField, "_VelIn", velCur);
            cs.SetTexture(kBuildRenderField, "_RenderPrev", renderCur);
            cs.SetTexture(kBuildRenderField, "_RenderOut", renderNext);
            cs.SetTexture(kBuildRenderField, "_RenderOcc", renderOcc);
            cs.SetTexture(kBuildRenderField, "_OccD", occD);   // sparse gate: B-spline only inside the halo
            Dispatch(kBuildRenderField);
            Swap(ref renderCur, ref renderNext);
        }

        // Zero an RG (float2) fine up-res field. Dispatched over its own resolution (Oob tests _Res).
        void ClearFineField(RenderTexture rt, Vector3Int clearRes)
        {
            Vector3Int clearGroups = Groups(clearRes, 4);
            SetInts3("_Res", clearRes.x, clearRes.y, clearRes.z);
            cs.SetTexture(kClearScalar2, "_ClearScalar2", rt);
            cs.Dispatch(kClearScalar2, clearGroups.x, clearGroups.y, clearGroups.z);
            SetInts3("_Res", res.x, res.y, res.z);
        }

        void ReleaseFine()
        {
            Rel(ref fineCur);
            Rel(ref fineNext);
            fineInitialized = false;
            fineScale = 1;
            fineRes = default;
        }

        void EnsureFine(int scale)
        {
            scale = Mathf.Clamp(scale, 1, 4);
            Vector3Int targetRes = new Vector3Int(res.x * scale, res.y * scale, res.z * scale);
            if (fineCur != null && fineNext != null && fineScale == scale && fineRes == targetRes) return;
            Rel(ref fineCur);
            Rel(ref fineNext);
            fineInitialized = false;
            fineScale = scale;
            fineRes = targetRes;
            fineCur = Make(fineRes, RenderTextureFormat.RGHalf);
            fineNext = Make(fineRes, RenderTextureFormat.RGHalf);
            ClearFineField(fineCur, fineRes);
            ClearFineField(fineNext, fineRes);
        }


        /// <summary>Flow-displacement allocation used by the old Render Detail warp module.</summary>
        public void EnsureFlowField()
        {
            if (flowField == null) { flowField = Make(res, RenderTextureFormat.ARGBHalf); ClearBaseField(flowField); }
        }

        /// <summary>Advance the Render Detail flow map so the warp is carried by the simulated motion.
        /// Brick-indirect: the flow is only consumed (by BakeWarp) where visible content exists, i.e. inside
        /// active bricks — the old dense res³ RMW churned stale velocity into flow nothing reads. Retired
        /// bricks are zeroed so the "flow == 0 outside the active set" invariant holds from the alloc clear.</summary>
        public void StepFlowMaps(float flowDt, float decay)
        {
            EnsureFlowField();
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_FlowDt", flowDt);
            cs.SetFloat("_FlowDecay", Mathf.Clamp01(decay));
            cs.SetBuffer(kAdvectFlow, "_ActiveBricks", activeBricksBuf);
            cs.SetTexture(kAdvectFlow, "_Vel", velCur);
            cs.SetTexture(kAdvectFlow, "_FlowField", flowField);
            cs.DispatchIndirect(kAdvectFlow, indirectArgs, 0);

            cs.SetBuffer(kClearRetiredFlow, "_RetiredBricks", retiredBricksBuf);
            cs.SetTexture(kClearRetiredFlow, "_FlowField", flowField);
            cs.DispatchIndirect(kClearRetiredFlow, indirectArgs, 12);   // triplet [3..5] = retired count
        }

        /// <summary>Free the live Render Detail warp resources. Used when the container toggles Render Detail off.</summary>
        public void ReleaseRenderWarp()
        {
            if (!playbackMotion) Rel(ref flowField);
            Rel(ref warpField);
            warpScale = 0;
            warpGroups = default;
        }

        /// <summary>Bake the old Render Detail module: a visual-only domain-warp displacement at scale× base
        /// resolution. The slice renderer then samples the existing render field through this warp.</summary>
        public void BakeWarp(int scaleMul, float boost, float scale, float time, float stretch,
                             float rough, float contrast, float edge, float curlAmt, float billow,
                             float fine, float grainCurl, float temporalBlend = 1f)
        {
            if (flowField == null) return;

            int mul = Mathf.Clamp(scaleMul, 1, 4);
            Vector3Int wr = res * mul;
            if (warpField == null || warpScale != mul)
            {
                Rel(ref warpField);
                warpField = Make(wr, RenderTextureFormat.ARGBHalf);
                warpGroups = Groups(wr, 4);
                warpScale = mul;
                // The bake is brick-indirect and never visits empty space — a fresh texture must start at
                // the neutral encode everywhere (dense one-shot; the old dense bake rewrote it every frame).
                SetInts3("_WarpRes", wr.x, wr.y, wr.z);
                cs.SetTexture(kClearWarpNeutral, "_WarpField", warpField);
                cs.Dispatch(kClearWarpNeutral, warpGroups.x, warpGroups.y, warpGroups.z);
            }

            RenderTexture detailSource = RenderField;
            SetInts3("_WarpRes", wr.x, wr.y, wr.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_DetailBoost", Mathf.Clamp(boost, 0f, 2f));
            cs.SetFloat("_DetailScale", Mathf.Clamp(scale, 2f, 64f));
            cs.SetFloat("_DetailTime", time);
            cs.SetFloat("_DetailStretch", Mathf.Clamp(stretch, 0f, 4f));
            cs.SetFloat("_DetailRough", Mathf.Clamp01(rough));
            cs.SetFloat("_DetailContrast", Mathf.Clamp(contrast, 1f, 3f));
            cs.SetFloat("_DetailEdge", Mathf.Clamp(edge, 1f, 4f));
            cs.SetFloat("_DetailCurl", Mathf.Clamp01(curlAmt));
            cs.SetFloat("_DetailBillow", Mathf.Clamp(billow, 0f, 1.5f));
            cs.SetFloat("_DetailFine", Mathf.Clamp01(fine));
            cs.SetFloat("_DetailGrainCurl", Mathf.Clamp01(grainCurl));
            cs.SetFloat("_WarpTemporalBlend", Mathf.Clamp01(temporalBlend));
            cs.SetInt("_WarpMul", mul);
            cs.SetBuffer(kBakeWarp, "_ActiveBricks", activeBricksBuf);
            cs.SetTexture(kBakeWarp, "_FieldsLow", detailSource);
            cs.SetTexture(kBakeWarp, "_FlowSrc", flowField);
            // Playback bakes run BakePlaybackDetailCurl first; the dummy fallback (zero curl → neutral
            // vorticity modulation) only guards a caller that skipped it.
            cs.SetTexture(kBakeWarp, "_CurlSrc", curl != null ? curl : velCur);
            cs.SetTexture(kBakeWarp, "_WarpField", warpField);
            // One group per ACTIVE brick (was dense over the whole warp grid, ~90% of it re-writing neutral
            // into empty space). Retired bricks get the neutral fill below so the renderer's occupancy tails
            // never sample stale warp.
            cs.DispatchIndirect(kBakeWarp, indirectArgs, 0);

            cs.SetBuffer(kClearRetiredWarpBricks, "_RetiredBricks", retiredBricksBuf);
            cs.SetTexture(kClearRetiredWarpBricks, "_WarpField", warpField);
            cs.DispatchIndirect(kClearRetiredWarpBricks, indirectArgs, 12);   // triplet [3..5] = retired count

        }

        /// <summary>Neutralize Render Detail blocks retired since the previous solve even when cadence skips this
        /// frame's warp bake. Uses the existing retired list and texture; no allocation or dense work.</summary>
        internal void ClearRetiredWarp()
        {
            if (warpField == null || warpScale <= 0) return;
            SetInts3("_WarpRes", warpField.width, warpField.height, warpField.volumeDepth);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetInt("_WarpMul", warpScale);
            cs.SetBuffer(kClearRetiredWarpBricks, "_RetiredBricks", retiredBricksBuf);
            cs.SetTexture(kClearRetiredWarpBricks, "_WarpField", warpField);
            cs.DispatchIndirect(kClearRetiredWarpBricks, indirectArgs, 12);
        }

        /// <summary>Cache playback (DetailFlow clips): rebuild the vorticity proxy from the scattered flow anchor
        /// so the subsequent <see cref="BakeWarp"/> keeps its curl-driven modulation (Edge / Flow Coherence /
        /// grain curl) without simulation velocity in the file. <paramref name="bakeFps"/> is the clip's bake
        /// rate — it sets the gain that maps the time-integrated flow back to instantaneous curl (the
        /// steady-state inversion of the live StepFlowMaps(dt·1.5, 0.96) accumulation; see FlowCurl). Call after
        /// <see cref="UploadCacheFrameSparse"/> (it consumes that frame's active-brick list), before BakeWarp.</summary>
        public void BakePlaybackDetailCurl(float bakeFps)
        {
            if (!playbackFlow || flowField == null) return;
            if (curl == null)
            {
                curl = Make(res, RenderTextureFormat.ARGBHalf);
                // The proxy only writes active bricks; BakeWarp's linear taps can straddle into neighbours,
                // which must read 0 (neutral modulation), not uninitialized memory.
                ClearBaseField(curl);
            }
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_FlowCurlGain", Mathf.Max(1f, bakeFps) * (0.04f / 1.5f));   // (1−decay)/flowDt per bake step
            cs.SetBuffer(kFlowCurl, "_ActiveBricks", activeBricksBuf);
            cs.SetTexture(kFlowCurl, "_FlowField", flowField);
            cs.SetTexture(kFlowCurl, "_Curl", curl);
            cs.DispatchIndirect(kFlowCurl, indirectArgs, 0);
        }

        /// <summary>Release ONLY the full-scale warp texture. Playback (DetailFlow) uses this instead of
        /// <see cref="ReleaseRenderWarp"/>, which would also free the flow anchor — the live scatter target.
        /// Used when playback Render Detail resolves to scale 0 (VRAM budget / axis cap) or the look override
        /// turns the module off; the slice renderer then draws the unwarped base field.</summary>
        public void ReleasePlaybackWarp()
        {
            Rel(ref warpField);
            warpScale = 0;
            warpGroups = default;
        }

        // Render Detail transport: seed/advect a render-only density+burn field at 2×/4× base resolution over active
        // bricks. Amount blends base->fine; Persistence controls residual decay/anchoring; Speed scales advection.
        // Largest supported up-res factor (4 → 2) whose fine field fits the VRAM/axis cap; 0 if even 2× overflows
        // (renderer then falls back to the base field). Only even factors keep BRICK*scale a multiple of the 8³ bake
        // tile, so the ladder is {4, 2}. The fine field is render-only RGHalf (density+burn, no solve) — this cost is
        // nothing like raising the sim `resolution`, which rebuilds the whole cubic pressure/advection solve.
        static int FitFineScale(int requested, int maxAxis, int cap)
        {
            if (requested >= 4 && maxAxis * 4 <= cap) return 4;
            if (maxAxis * 2 <= cap) return 2;
            return 0;
        }

        void BakeRenderDetail(float amount, float feature, float flow, float sharp, float quality,
                              float motion, float persist, float speed, float time,
                              float crisp, float grainMin, float grainMax, int fineScale, float dt)
        {
            fineScale = Mathf.Clamp(fineScale, 2, 4);
            EnsureFine(fineScale);   // 2×/4× render-only field; no pressure/advection solve is allocated

            float clampedPersist = Mathf.Clamp01(persist);
            cs.SetFloat("_Dt", Mathf.Max(0f, dt));
            cs.SetFloat("_DBAmount", Mathf.Clamp01(amount));
            cs.SetFloat("_DBSpeed", Mathf.Max(0f, speed));
            cs.SetFloat("_DBPersist", clampedPersist);
            cs.SetFloat("_DBAnchor", Mathf.Lerp(10f, 3f, clampedPersist));
            cs.SetInt("_FineScale", fineScale);
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_FineRes", fineRes.x, fineRes.y, fineRes.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);

            RenderTexture detailSource = renderCur != null ? renderCur : fieldsCur;

            if (!fineInitialized)
            {
                cs.SetTexture(kFineSeed, "_FieldsIn", detailSource);
                cs.SetTexture(kFineSeed, "_Solid", SolidTex);
                cs.SetTexture(kFineSeed, "_FineOut", fineCur);
                cs.SetBuffer(kFineSeed, "_ActiveBricks", activeBricksBuf);
                DispatchBricks(kFineSeed);
                fineInitialized = true;
            }

            // No per-frame full clear: the transport writes every voxel of every DILATED active brick. A stale
            // inactive brick is hidden by render occupancy, and residual carry is gated by current coarse content.
            cs.SetTexture(kDetailBake, "_FieldsIn", detailSource);
            cs.SetTexture(kDetailBake, "_VelIn", velCur);
            cs.SetTexture(kDetailBake, "_Solid", SolidTex);
            cs.SetTexture(kDetailBake, "_FineIn", fineCur);
            cs.SetTexture(kDetailBake, "_FineOut", fineNext);
            // The indirect dispatch maps each group→brick via _ActiveBricks (see the DetailBake kernel). The live Step binds it to
            // every sparse base kernel in its occupancy pre-pass, but the CACHE path (UploadCacheFrameSparse) doesn't —
            // so bind it here to make the bake self-sufficient for both callers (redundant, harmless, for Step).
            cs.SetBuffer(kDetailBake, "_ActiveBricks", activeBricksBuf);
            DispatchBricks(kDetailBake);
            Swap(ref fineCur, ref fineNext);
        }

        void DispatchOcc(int kernel) => cs.Dispatch(kernel, occGroups.x, occGroups.y, occGroups.z);

        /// <summary>Reduce the live fire field into the moments buffer (burn-weighted centroid + total power).
        /// The buffer is bound straight to the fire-light injection material, so the cast light tracks the flame
        /// with zero CPU readback. Clears then runs one dense pass over the grid — cheap; call once per sim step
        /// when a container has Fire Light enabled.</summary>
        public void UpdateFireMoments()
        {
            RenderTexture fireField = RenderField;
            if (fireMomentsBuf == null || fireField == null) return;
            fireMomentsBuf.SetData(FireMomentsClear);
            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetTexture(kFireMoments, "_FieldsIn", fireField);
            cs.SetBuffer(kFireMoments, "_FireMoments", fireMomentsBuf);
            Dispatch(kFireMoments);
        }

        // ── Cache bake readback ──────────────────────────────────────────────────────────────────────
        /// <summary>
        /// GPU-cull and pack the visible cache bricks, returning compact GPU buffers ready for a bounded readback.
        /// The first pass reads one 4³ group per brick and returns only the active count; the second pass writes the
        /// exact sparse payload. This replaces the old dense res³ readback + duplicate CPU scan/copy.
        /// </summary>
        public int PackCacheSparse(float cull, out ComputeBuffer brickIndices, out ComputeBuffer packedBlocks,
                                   bool packBaseVelocity = false, bool packRenderDetail = true,
                                   bool velocityBounds = true)
        {
            brickIndices = null;
            packedBlocks = null;
            if (fieldsCur == null) return 0;
            RenderTexture renderSource = RenderField;
            RenderTexture cacheWarp = packRenderDetail ? warpField : null;
            // Velocity bake: base bricks carry raw simulation velocity (voxels/sec) in their vector halfs. With
            // velocityBounds the sparse test also retains velocity-only boundary bricks so particle advection and
            // VDB motion do not terminate at the visible scalar support; lean playback bakes drop that halo.
            bool baseVelocity = packBaseVelocity && velCur != null;
            RenderTexture baseVector = baseVelocity ? velCur : renderSource;
            cs.SetInt("_CachePackBaseVelocity", baseVelocity ? 1 : 0);
            cs.SetInt("_CacheVelocityBounds", baseVelocity && velocityBounds ? 1 : 0);
            Vector3Int warpRes = cacheWarp != null
                ? new Vector3Int(cacheWarp.width, cacheWarp.height, cacheWarp.volumeDepth)
                : Vector3Int.zero;
            Vector3Int warpOcc = cacheWarp != null
                ? new Vector3Int(Mathf.CeilToInt(warpRes.x / (float)Brick),
                                 Mathf.CeilToInt(warpRes.y / (float)Brick),
                                 Mathf.CeilToInt(warpRes.z / (float)Brick))
                : Vector3Int.zero;

            cacheSparseCountBuf ??= new ComputeBuffer(1, sizeof(uint));
            cs.SetFloat("_CacheCull", Mathf.Max(0f, cull));

            int Count(Vector3Int textureRes, Vector3Int brickGrid, bool detail)
            {
                cacheSparseCountBuf.SetData(CacheSparseCountClear);
                SetInts3("_Res", textureRes.x, textureRes.y, textureRes.z);
                SetInts3("_OccRes", brickGrid.x, brickGrid.y, brickGrid.z);
                cs.SetInt("_CachePackWarpBricks", detail ? 1 : 0);
                cs.SetTexture(kCacheCountSparse, "_FieldsIn", renderSource);
                cs.SetTexture(kCacheCountSparse, "_CacheWarpIn", detail ? cacheWarp : baseVector);
                cs.SetBuffer(kCacheCountSparse, "_CacheSparseCount", cacheSparseCountBuf);
                cs.Dispatch(kCacheCountSparse, brickGrid.x, brickGrid.y, brickGrid.z);
                var request = AsyncGPUReadback.Request(cacheSparseCountBuf);
                request.WaitForCompletion();
                if (request.hasError)
                    throw new System.InvalidOperationException("[BrazeFX] GPU readback failed while counting final-look cache bricks.");
                int value = (int)request.GetData<uint>()[0];
                int capacity = brickGrid.x * brickGrid.y * brickGrid.z;
                if (value < 0 || value > capacity)
                    throw new System.InvalidOperationException($"[BrazeFX] Invalid sparse cache brick count {value} / {capacity}.");
                return value;
            }

            int baseCount = Count(res, occRes, false);
            int warpCount = cacheWarp != null ? Count(warpRes, warpOcc, true) : 0;
            int count = checked(baseCount + warpCount);

            const int packedUintsPerVoxel = 3;
            int uintsPerBrick = Brick * Brick * Brick * packedUintsPerVoxel;
            long requiredBlockUints = (long)count * uintsPerBrick;
            if (requiredBlockUints > int.MaxValue)
                throw new System.InvalidOperationException("[BrazeFX] Sparse cache frame exceeds the GPU buffer size limit.");

            int idxCapacity = Mathf.Max(1, count);
            int blockCapacity = Mathf.Max(1, (int)requiredBlockUints);
            if (cacheSparseIdxBuf == null || cacheSparseIdxBuf.count < idxCapacity)
            {
                cacheSparseIdxBuf?.Release();
                cacheSparseIdxBuf = new ComputeBuffer(idxCapacity, sizeof(uint));
            }
            if (cacheSparseBlocksBuf == null || cacheSparseBlocksBuf.count < blockCapacity)
            {
                cacheSparseBlocksBuf?.Release();
                cacheSparseBlocksBuf = new ComputeBuffer(blockCapacity, sizeof(uint));
            }

            if (count > 0)
            {
                void Pack(Vector3Int textureRes, Vector3Int brickGrid, bool detail, int outputOffset)
                {
                    cacheSparseCountBuf.SetData(CacheSparseCountClear);
                    SetInts3("_Res", textureRes.x, textureRes.y, textureRes.z);
                    SetInts3("_OccRes", brickGrid.x, brickGrid.y, brickGrid.z);
                    cs.SetInt("_CachePackWarpBricks", detail ? 1 : 0);
                    cs.SetInt("_CacheOutputOffset", outputOffset);
                    cs.SetTexture(kCachePackSparse, "_FieldsIn", renderSource);
                    cs.SetTexture(kCachePackSparse, "_CacheWarpIn", detail ? cacheWarp : baseVector);
                    cs.SetBuffer(kCachePackSparse, "_CacheSparseCount", cacheSparseCountBuf);
                    cs.SetBuffer(kCachePackSparse, "_CacheSparseIdx", cacheSparseIdxBuf);
                    cs.SetBuffer(kCachePackSparse, "_CacheSparseBlocks", cacheSparseBlocksBuf);
                    cs.Dispatch(kCachePackSparse, brickGrid.x, brickGrid.y, brickGrid.z);
                }

                if (baseCount > 0) Pack(res, occRes, false, 0);
                if (warpCount > 0) Pack(warpRes, warpOcc, true, baseCount);
            }

            // Other solver entry points bind these before dispatch, but restore the canonical base values so a
            // diagnostic or editor callback immediately following a bake cannot inherit the detail grid.
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);

            brickIndices = cacheSparseIdxBuf;
            packedBlocks = cacheSparseBlocksBuf;
            return count;
        }

        // Pack the base field (density,temp,fuel,burn) into a flat uint buffer — 2 uints/voxel (4 halfs), linear
        // index x + y·res.x + z·res.x·res.y. The caller async-reads this BUFFER (reliable) instead of the 3D
        // texture (whose readback returns only slice 0 on some platforms). Buffer is reused across frames.
        public ComputeBuffer PackBaseField() => PackField(fieldsCur);

        /// <summary>Diagnostic/cache-format pack of the final renderer-visible field.</summary>
        internal ComputeBuffer PackRenderField() => PackField(RenderField);

        ComputeBuffer PackField(RenderTexture scalarField)
        {
            if (scalarField == null) return null;
            int n = res.x * res.y * res.z * 3;   // 6 halfs/voxel (density,temp,burn,velX,velY,velZ) → 3 uints
            if (cachePackBuf == null || cachePackBuf.count != n)
            { cachePackBuf?.Release(); cachePackBuf = new ComputeBuffer(n, sizeof(uint)); }
            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetTexture(kCachePackBase, "_FieldsIn", scalarField);
            cs.SetTexture(kCachePackBase, "_CacheVelIn", velCur);
            cs.SetBuffer(kCachePackBase, "_CachePack", cachePackBuf);
            Dispatch(kCachePackBase);
            return cachePackBuf;
        }

        /// <summary>Drive the ActiveBricks/RawBricks diagnostic readback. Their only consumer is the stats
        /// HUD, so the container gates this on showStats — no per-step readback when nothing displays it.</summary>
        public bool DiagnosticsEnabled { get; set; } = true;

        System.Action<UnityEngine.Rendering.AsyncGPUReadbackRequest> onActiveCountCb;   // cached: a lambda here allocated a closure per Step
        int activeCountReadbackFrame = -1000;
        const int ActiveCountReadbackFrameInterval = 6;

        void RequestActiveCount()
        {
            if (readbackPending) return;
            int frame = Time.frameCount;
            if (frame - activeCountReadbackFrame < ActiveCountReadbackFrameInterval) return;
            activeCountReadbackFrame = frame;
            readbackPending = true;
            onActiveCountCb ??= OnActiveCountReadback;
            AsyncGPUReadback.Request(activeCountBuf, onActiveCountCb);
        }

        void OnActiveCountReadback(UnityEngine.Rendering.AsyncGPUReadbackRequest req)
        {
            readbackPending = false;
            if (!req.hasError)
            {
                var data = req.GetData<uint>();
                ActiveBricks = (int)data[0];
                RawBricks = data.Length > 1 ? (int)data[1] : 0;
            }
        }

        void RequestCfdFlux()
        {
            if (cfdFluxReadbackPending || cfdFluxBuf == null) return;
            int frame = Time.frameCount;
            if (frame - cfdFluxReadbackFrame < CfdFluxReadbackFrameInterval) return;
            cfdFluxReadbackFrame = frame;
            cfdFluxReadbackPending = true;
            onCfdFluxCb ??= OnCfdFluxReadback;
            AsyncGPUReadback.Request(cfdFluxBuf, onCfdFluxCb);
        }

        void OnCfdFluxReadback(AsyncGPUReadbackRequest request)
        {
            cfdFluxReadbackPending = false;
            if (request.hasError || cfdBoundaryCount <= 0) return;
            var values = request.GetData<int>();
            CfdInletVolumeFlowM3PerSecond = values.Length > 0
                ? Mathf.Max(0f, values[0] / CfdFluxFixedPoint) : 0f;
            CfdOutletVolumeFlowM3PerSecond = values.Length > 1
                ? Mathf.Max(0f, values[1] / CfdFluxFixedPoint) : 0f;
        }

        void RequestCfdSurfaceLoad()
        {
            if (cfdSurfaceLoadReadbackPending || cfdSurfaceLoadBuf == null) return;
            int frame = Time.frameCount;
            if (frame - cfdSurfaceLoadReadbackFrame < CfdFluxReadbackFrameInterval) return;
            cfdSurfaceLoadReadbackFrame = frame;
            cfdSurfaceLoadReadbackPending = true;
            onCfdSurfaceLoadCb ??= OnCfdSurfaceLoadReadback;
            AsyncGPUReadback.Request(cfdSurfaceLoadBuf, onCfdSurfaceLoadCb);
        }

        void OnCfdSurfaceLoadReadback(AsyncGPUReadbackRequest request)
        {
            cfdSurfaceLoadReadbackPending = false;
            if (request.hasError || !cfdSurfaceDiagnostics) return;
            var values = request.GetData<int>();
            CfdRelativePressureLoadLocal = values.Length >= 3
                ? new Vector3(values[0], values[1], values[2]) / CfdFluxFixedPoint
                : Vector3.zero;
        }

        void RequestCfdTemperaturePeak()
        {
            if (cfdTemperaturePeakReadbackPending || cfdTemperaturePeakBuf == null) return;
            cfdTemperaturePeakReadbackFrame = Time.frameCount;
            cfdTemperaturePeakReadbackPending = true;
            onCfdTemperaturePeakCb ??= OnCfdTemperaturePeakReadback;
            AsyncGPUReadback.Request(cfdTemperaturePeakBuf, onCfdTemperaturePeakCb);
        }

        void OnCfdTemperaturePeakReadback(AsyncGPUReadbackRequest request)
        {
            cfdTemperaturePeakReadbackPending = false;
            if (request.hasError || !cfdTemperatureDiagnosticsEnabled) return;
            var values = request.GetData<uint>();
            if (values.Length <= 0) return;
            CfdTemperaturePeakHeatUnits = values[0] / 4096f;
            CfdTemperaturePeakValid = true;
        }

        // ── Brick visualizer (DEBUG ONLY) ───────────────────────────────────────────────────────────
        // Per-brick occupancy snapshot for the scene-view debug draw. A compute kernel flattens occ/occD
        // into a flat ComputeBuffer, which is read back async — buffer readback avoids the platform-specific
        // row/slice padding (and per-layer GetData) of 3D-texture readback that breaks a straight CopyTo.
        // Never on the shipping path (driven only when the container's debug toggle is on).
        // Flags per brick: bit0 = raw content, bit1 = active (raw or halo). idx = x + y*OccRes.x + z*OccRes.x*OccRes.y.
        public Vector3Int OccRes => occRes;
        public int BrickSize => Brick;
        public bool BrickDebugValid { get; private set; }
        ComputeBuffer brickDbgBuf;
        uint[] brickFlagsCpu;
        bool brickDbgPending;
        int brickDbgReadbackFrame = -1000;
        const int BrickDebugReadbackFrameInterval = 10;
        /// <summary>Per-brick flags (bit0 = raw, bit1 = active/halo), valid when <see cref="BrickDebugValid"/>.</summary>
        public uint[] BrickFlags => brickFlagsCpu;

        public void RequestBrickDebug()
        {
            if (brickDbgPending || occ == null || occD == null) return;
            int frame = Time.frameCount;
            if (frame - brickDbgReadbackFrame < BrickDebugReadbackFrameInterval) return;
            brickDbgReadbackFrame = frame;
            int n = occRes.x * occRes.y * occRes.z;
            if (brickDbgBuf == null || brickDbgBuf.count != n)
            {
                brickDbgBuf?.Release();
                brickDbgBuf = new ComputeBuffer(n, sizeof(uint));
                brickFlagsCpu = new uint[n];
            }
            cs.SetTexture(kBrickDebug, "_Occ", occ);
            cs.SetTexture(kBrickDebug, "_OccD", occD);
            cs.SetBuffer(kBrickDebug, "_BrickDbg", brickDbgBuf);
            DispatchOcc(kBrickDebug);

            brickDbgPending = true;
            AsyncGPUReadback.Request(brickDbgBuf, req =>
            {
                brickDbgPending = false;
                if (!req.hasError) { req.GetData<uint>().CopyTo(brickFlagsCpu); BrickDebugValid = true; }
            });
        }

        // ── Cache playback ─────────────────────────────────────────────────────────────────────────
        // Load a cached frame instead of simulating: scatter its render-ready density/temp/burn into fieldsCur and
        // rebuild the sparse occupancy mask. The slice renderer consumes both directly; no conditioning or light
        // volume is rebuilt during playback. Production clips scatter their final baked Render Detail displacement;
        // DetailFlow reconstruction remains only for explicitly-authored compatibility data.
        /// <summary>Cache playback (fast path): scatter the sparse active-brick blocks straight into the 3D fields on
        /// the GPU (no per-frame full-volume CPU rebuild/upload), then rebuild only sparse occupancy. Buffers hold one
        /// half-exact scalar payload: base = brickSize³×3 (density,temp,burn), plus optional compact displacement.</summary>
        public void UploadCacheFrameSparse(ComputeBuffer brickIdx, ComputeBuffer baseBlocks,
                                           int brickOffset, int brickCount, bool warpQuant, bool hasWarp,
                                           bool hasVelocity, ComputeBuffer blockId, bool dedup, bool scalarQuant,
                                           bool splitWarp, RenderTexture fieldsTarget = null,
                                           bool skipOccupancy = false)
        {
            if (fieldsCur == null) return;
            // Motion smoothing scatters the key frame into its own field; a classic scatter into fieldsCur
            // invalidates the blend targets (their content no longer matches the active-brick clears).
            RenderTexture scatterFields = fieldsTarget != null ? fieldsTarget : fieldsCur;
            if (fieldsTarget == null) cacheBlendReady = false;
            bool hasVectors = hasWarp || hasVelocity;

            Vector3Int scatterWarpRes = hasWarp && warpField != null
                ? new Vector3Int(warpField.width, warpField.height, warpField.volumeDepth)
                : res;
            Vector3Int warpBrickGrid = new Vector3Int(
                Mathf.CeilToInt(scatterWarpRes.x / (float)Brick),
                Mathf.CeilToInt(scatterWarpRes.y / (float)Brick),
                Mathf.CeilToInt(scatterWarpRes.z / (float)Brick));

            // Sparse clear: zero ONLY the cells touched LAST frame (the prior active brick set) instead of the whole
            // 3.28M-voxel grid — the cache replaces the field wholesale, so cells outside last frame's active set are
            // already 0. Publish last frame's count to the indirect args, then clear those bricks. (First frame: count
            // is 0 → no-op; the field starts clean from construction.) This replaces the two dense ClearField passes.
            // Production clips scatter high-bit-tagged native-resolution detail bricks into the playback texture.
            // Compatibility DetailFlow data scatters raw channels into a flow anchor and requests reconstruction.
            RenderTexture scatterDetail = playbackFlow ? flowField : warpField;
            // Velocity-only caches have no Render Detail target. Keep the generic warp UAV bound to a distinct valid
            // texture while CacheScatterBase writes BASE vectors through _ScatterVelocity instead.
            RenderTexture scatterWarpTarget = hasWarp && scatterDetail != null
                ? scatterDetail : hasVelocity && flowField != null ? flowField : velCur;
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            SetInts3("_ScatterWarpRes", scatterWarpRes.x, scatterWarpRes.y, scatterWarpRes.z);
            SetInts3("_WarpBrickGrid", warpBrickGrid.x, warpBrickGrid.y, warpBrickGrid.z);
            cs.SetInt("_ScatterHasWarp", hasVectors ? 1 : 0);
            cs.SetInt("_ScatterHasVelocity", hasVelocity ? 1 : 0);
            cs.SetInt("_ScatterDiscardVelocity", 0);
            cs.SetInt("_ScatterWarpRaw", playbackFlow ? 1 : 0);
            cs.SetInt("_ScatterSplitWarp", splitWarp ? 1 : 0);
            cs.SetBuffer(kWriteArgs, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kWriteArgs, "_IndirectArgs", indirectArgs);
            cs.Dispatch(kWriteArgs, 1, 1, 1);
            cs.SetBuffer(kClearBricksFV, "_ActiveBricks", activeBricksBuf);
            cs.SetTexture(kClearBricksFV, "_Fields", scatterFields);
            cs.SetTexture(kClearBricksFV, "_ScatterWarp", scatterWarpTarget);
            cs.SetTexture(kClearBricksFV, "_ScatterVelocity", velCur);
            cs.DispatchIndirect(kClearBricksFV, indirectArgs);

            // The native detail grid can be 2×/4× the base field. Clear exactly the tagged detail bricks touched by
            // the previous frame before replacing them; a dense high-resolution clear here is what would freeze
            // timeline playback on production caches.
            if (splitWarp && scatterDetail != null && cachePlaybackWarpBricksBuf != null &&
                previousCacheScatterBrickCount > 0)
            {
                cs.SetBuffer(kClearCacheWarpBricks, "_CacheWarpActiveBricks", cachePlaybackWarpBricksBuf);
                cs.SetTexture(kClearCacheWarpBricks, "_ScatterWarp", scatterDetail);
                DispatchLinearGroups(kClearCacheWarpBricks, previousCacheScatterBrickCount);
            }

            if (splitWarp && (cachePlaybackWarpBricksBuf == null || cachePlaybackWarpBricksBuf.count < Mathf.Max(1, brickCount)))
            {
                cachePlaybackWarpBricksBuf?.Release();
                cachePlaybackWarpBricksBuf = new ComputeBuffer(Mathf.Max(1, brickCount), sizeof(uint));
            }

            if (brickCount > 0 && brickIdx != null && baseBlocks != null)
            {
                cs.SetInt("_ScatterBrickCount", brickCount);
                cs.SetInt("_ScatterBrickOffset", brickOffset);
                SetInts3("_BrickGrid", occRes.x, occRes.y, occRes.z);
                cs.SetInt("_ScatterBrickSize", Brick);
                cs.SetInt("_ScatterWarpQuant", warpQuant ? 1 : 0);
                cs.SetInt("_ScatterScalarQuant", scalarQuant ? 1 : 0);
                cs.SetInt("_ScatterHasWarp", hasVectors ? 1 : 0);
                cs.SetInt("_ScatterHasVelocity", hasVelocity ? 1 : 0);
                cs.SetInt("_ScatterDiscardVelocity", 0);
                cs.SetInt("_ScatterSplitWarp", splitWarp ? 1 : 0);
                cs.SetInt("_ScatterDedup", dedup ? 1 : 0);
                cs.SetBuffer(kCacheScatterBase, "_ScatterBlockId", dedup ? blockId : brickIdx);   // placeholder bind when off
                cs.SetBuffer(kCacheScatterBase, "_ScatterBrickIdx", brickIdx);
                cs.SetBuffer(kCacheScatterBase, "_ScatterBase", baseBlocks);
                cs.SetBuffer(kCacheScatterBase, "_CacheWarpActiveBricks",
                    splitWarp ? cachePlaybackWarpBricksBuf : activeBricksBuf);
                cs.SetTexture(kCacheScatterBase, "_Fields", scatterFields);
                cs.SetTexture(kCacheScatterBase, "_ScatterWarp", scatterWarpTarget);
                cs.SetTexture(kCacheScatterBase, "_ScatterVelocity", velCur);
                long vpb = (long)Brick * Brick * Brick;
                DispatchLinearGroups(kCacheScatterBase, DivideRoundUp((long)brickCount * vpb, 64));
            }

            previousCacheScatterBrickCount = splitWarp ? brickCount : 0;

            if (!skipOccupancy)
            {
                MarkOccupancyFromScatter(brickIdx, brickOffset, brickCount);
                BuildCacheRenderOccupancy();
            }
        }

        /// <summary>Publish renderer motion auxiliaries from authoritative cache velocity. Velocity itself was
        /// scattered byte-for-byte into <see cref="Velocity"/> and remains untouched; this pass derives only curl
        /// and the deterministic UVW flow anchor used by Render Detail.</summary>
        public void FinalizeCacheVelocity(float fps, float flowMemoryFrames)
        {
            if (!playbackVelocity || velCur == null || curl == null || flowField == null) return;
            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetFloat("_CacheVelocityFlowSeconds", Mathf.Max(0f, flowMemoryFrames) / Mathf.Max(1f, fps));
            cs.SetTexture(kCacheVelocityMotion, "_Vel", velCur);
            cs.SetTexture(kCacheVelocityMotion, "_Curl", curl);
            cs.SetTexture(kCacheVelocityMotion, "_FlowField", flowField);
            cs.Dispatch(kCacheVelocityMotion, groups.x, groups.y, groups.z);
        }

        /// <summary>Scatter one neighbouring cache frame into the reconstruction/blend volume. This does not disturb
        /// the visible field, occupancy, the active-brick list, or the key frame's velocity. The layout flags must
        /// match the clip (velocity clips carry vector data in their blocks, changing the stride); the neighbour's
        /// own velocity is decoded for correct block addressing but deliberately not written — the key frame's
        /// velocity drives all motion.</summary>
        public void UploadPlaybackNeighbourSparse(ComputeBuffer brickIdx, ComputeBuffer baseBlocks,
                                                  int brickOffset, int brickCount, ComputeBuffer blockId,
                                                  bool dedup, bool scalarQuant, bool hasVel = false,
                                                  bool warpQuant = false)
        {
            if (playbackNeighbourField == null) return;
            ClearBaseField(playbackNeighbourField);
            if (brickCount <= 0 || brickIdx == null || baseBlocks == null) return;

            cs.SetInt("_ScatterBrickCount", brickCount);
            cs.SetInt("_ScatterBrickOffset", brickOffset);
            SetInts3("_BrickGrid", occRes.x, occRes.y, occRes.z);
            SetInts3("_ScatterWarpRes", res.x, res.y, res.z);
            SetInts3("_WarpBrickGrid", occRes.x, occRes.y, occRes.z);
            cs.SetInt("_ScatterBrickSize", Brick);
            cs.SetInt("_ScatterWarpQuant", hasVel && warpQuant ? 1 : 0);
            cs.SetInt("_ScatterScalarQuant", scalarQuant ? 1 : 0);
            cs.SetInt("_ScatterHasWarp", hasVel ? 1 : 0);        // stride selector for vector-carrying blocks
            cs.SetInt("_ScatterHasVelocity", hasVel ? 1 : 0);
            cs.SetInt("_ScatterDiscardVelocity", hasVel ? 1 : 0);
            cs.SetInt("_ScatterWarpRaw", 0);
            cs.SetInt("_ScatterSplitWarp", 0);
            cs.SetInt("_ScatterDedup", dedup ? 1 : 0);
            cs.SetBuffer(kCacheScatterBase, "_ScatterBlockId", dedup ? blockId : brickIdx);
            cs.SetBuffer(kCacheScatterBase, "_ScatterBrickIdx", brickIdx);
            cs.SetBuffer(kCacheScatterBase, "_ScatterBase", baseBlocks);
            cs.SetBuffer(kCacheScatterBase, "_CacheWarpActiveBricks", activeBricksBuf);
            cs.SetTexture(kCacheScatterBase, "_Fields", playbackNeighbourField);
            cs.SetTexture(kCacheScatterBase, "_ScatterWarp", velCur);
            cs.SetTexture(kCacheScatterBase, "_ScatterVelocity", velCur);
            long vpb = (long)Brick * Brick * Brick;
            DispatchLinearGroups(kCacheScatterBase, DivideRoundUp((long)brickCount * vpb, 64));
        }

        // ── Frame-skip motion smoothing (velocity clips) ─────────────────────────────────────────────
        // Key frame → playbackKeyField, neighbour → playbackNeighbourField, CacheMotionBlend → fieldsCur.

        /// <summary>True when this playback solver can motion-blend (velocity clip). Allocates the two blend
        /// fields lazily (2 × base-res ARGBHalf) and dense-clears them when they are new or a classic scatter
        /// has since written the display field directly.</summary>
        public bool EnsureCacheBlendTargets()
        {
            if (!playbackOnly || !playbackVelocity || fieldsCur == null) return false;
            if (playbackKeyField == null)
            {
                playbackKeyField = Make(res, RenderTextureFormat.ARGBHalf);
                cacheBlendReady = false;
            }
            if (playbackNeighbourField == null)
            {
                playbackNeighbourField = Make(res, RenderTextureFormat.ARGBHalf);
                cacheBlendReady = false;
            }
            if (!cacheBlendReady)
            {
                ClearBaseField(playbackKeyField);
                ClearBaseField(playbackNeighbourField);
                cacheBlendReady = true;
            }
            return true;
        }

        /// <summary>The key-frame scatter target while motion smoothing (null until <see cref="EnsureCacheBlendTargets"/>).</summary>
        public RenderTexture CacheBlendKeyField => playbackKeyField;

        /// <summary>Dense-clear the neighbour blend field (stream miss / clip end: blend degenerates to the key).</summary>
        public void ClearCacheBlendNeighbour()
        {
            if (playbackNeighbourField != null) ClearBaseField(playbackNeighbourField);
        }

        /// <summary>Rebuild binary occupancy from the UNION of the key and neighbour brick slices, so empty-space
        /// support and the detail bake cover smoke that only exists in one of the two frames. Render-importance
        /// statistics are fused into <see cref="CacheMotionBlend"/> after the blended display value is known.</summary>
        public void RebuildCacheOccupancyPair(ComputeBuffer brickIdx, int keyOffset, int keyCount,
                                              int neighbourOffset, int neighbourCount)
        {
            MarkOccupancyFromScatter(brickIdx, keyOffset, keyCount, brickIdx, neighbourOffset, neighbourCount);
        }

        /// <summary>Motion-compensated interpolation of the two scattered blend fields into the display field.
        /// <paramref name="t"/> = playhead position between the stored frames; <paramref name="storedDt"/> = the
        /// stored-frame interval in seconds. Call after both blend fields hold their frames.</summary>
        public void CacheMotionBlend(float t, float storedDt, bool neighbourValid)
        {
            if (playbackKeyField == null || playbackNeighbourField == null || fieldsCur == null || velCur == null)
                return;
            SetInts3("_Res", res.x, res.y, res.z);
            cs.SetFloat("_CacheBlendT", neighbourValid ? Mathf.Clamp01(t) : 0f);
            cs.SetFloat("_CacheBlendSeconds", Mathf.Max(1e-4f, storedDt));
            cs.SetTexture(kCacheMotionBlend, "_CacheBlendKey", playbackKeyField);
            cs.SetTexture(kCacheMotionBlend, "_CacheBlendNext", neighbourValid ? playbackNeighbourField : playbackKeyField);
            cs.SetTexture(kCacheMotionBlend, "_CacheBlendVel", velCur);
            cs.SetTexture(kCacheMotionBlend, "_Fields", fieldsCur);
            cs.SetTexture(kCacheMotionBlend, "_RenderOcc", renderOcc);
            cs.Dispatch(kCacheMotionBlend, groups.x, groups.y, groups.z);
        }

        static long DivideRoundUp(long value, long divisor) => (value + divisor - 1L) / divisor;

        /// <summary>Shape a logically one-dimensional dispatch across X/Y without exceeding Unity's per-axis
        /// 65,535 group limit. Matching kernels flatten Y using the fixed maximum-X stride.</summary>
        internal static Vector2Int BoundedLinearDispatchShape(long groupCount)
        {
            if (groupCount <= 0L)
                throw new System.ArgumentOutOfRangeException(nameof(groupCount), "Dispatch group count must be positive.");
            if (groupCount > MaxLinearDispatchGroups)
                throw new System.ArgumentOutOfRangeException(nameof(groupCount),
                    $"Dispatch requires {groupCount} groups; the bounded cache limit is {MaxLinearDispatchGroups}.");
            int x = (int)System.Math.Min(groupCount, MaxDispatchGroupsPerAxis);
            int y = (int)DivideRoundUp(groupCount, MaxDispatchGroupsPerAxis);
            return new Vector2Int(x, y);
        }

        void DispatchLinearGroups(int kernel, long groupCount)
        {
            Vector2Int shape = BoundedLinearDispatchShape(groupCount);
            cs.Dispatch(kernel, shape.x, shape.y, 1);
        }

        // Lazily create one pyramid-level working texture. No clear is needed: every texel of every level texture
        // is fully written before it is read within each solve (pack/downsample fill the signals, BuildMotionTerms
        // fills terms and the first motion buffer, refine writes before the ping-pong partner is read).
        RenderTexture EnsureVdbLevelTexture(RenderTexture[] set, int level)
        {
            if (set[level] == null) set[level] = Make(vdbLevelRes[level], RenderTextureFormat.ARGBHalf);
            return set[level];
        }

        /// <summary>Run the dedicated VDB optical-flow solve and publish renderer velocity, curl, and flow anchor.
        /// The solve is a coarse-to-fine pyramid: displacement is measured at the coarsest level (where fast motion
        /// is sub-voxel), then each finer level re-linearizes around the upsampled estimate and solves only the
        /// residual — that is what keeps reconstructed magnitude close to the true motion.
        /// <paramref name="temporalHistoryValid"/> means the previous solve produced exactly the pair preceding this
        /// one (sequential playback): the previous motion then seeds the coarsest level and is blended into the
        /// full-res result, which keeps independently solved frame pairs coherent at frame boundaries.</summary>
        public void ReconstructVdbPlaybackMotion(float fps, int temporalDirection,
                                                 BrazeVdbReconstructionSettings settings,
                                                 bool temporalHistoryValid = false)
        {
            if (!playbackReconstruction || vdbPlaybackCs == null || playbackNeighbourField == null || vdbLevelCount <= 0)
                return;
            settings = settings.Validated();
            float historyBlend = temporalHistoryValid ? settings.temporalSmoothing : 0f;
            vdbPlaybackCs.SetInts("_FullRes", res.x, res.y, res.z);
            vdbPlaybackCs.SetFloat("_TemporalDirection", temporalDirection < 0 ? -1f : 1f);
            vdbPlaybackCs.SetFloat("_MotionFps", Mathf.Max(1f, fps));
            vdbPlaybackCs.SetFloat("_FlowMemoryFrames", settings.flowMemoryFrames);
            vdbPlaybackCs.SetFloat("_HistoryBlend", historyBlend);

            // Signal pyramid: pack the scalar pair once at full res, then box-downsample level by level.
            Vector3Int fullGroups = Groups(res, 4);
            vdbPlaybackCs.SetInts("_VdbRes", res.x, res.y, res.z);
            vdbPlaybackCs.SetTexture(kVdbPackSignal, "_CurrentState", fieldsCur);
            vdbPlaybackCs.SetTexture(kVdbPackSignal, "_NeighbourState", playbackNeighbourField);
            vdbPlaybackCs.SetTexture(kVdbPackSignal, "_SignalWrite", EnsureVdbLevelTexture(vdbSignal, 0));
            vdbPlaybackCs.Dispatch(kVdbPackSignal, fullGroups.x, fullGroups.y, fullGroups.z);
            for (int k = 1; k < vdbLevelCount; k++)
            {
                Vector3Int dg = Groups(vdbLevelRes[k], 4);
                vdbPlaybackCs.SetInts("_VdbRes", vdbLevelRes[k].x, vdbLevelRes[k].y, vdbLevelRes[k].z);
                vdbPlaybackCs.SetInts("_SrcRes", vdbLevelRes[k - 1].x, vdbLevelRes[k - 1].y, vdbLevelRes[k - 1].z);
                vdbPlaybackCs.SetTexture(kVdbDownsampleSignal, "_SignalPair", vdbSignal[k - 1]);
                vdbPlaybackCs.SetTexture(kVdbDownsampleSignal, "_SignalWrite", EnsureVdbLevelTexture(vdbSignal, k));
                vdbPlaybackCs.Dispatch(kVdbDownsampleSignal, dg.x, dg.y, dg.z);
            }

            // Coarse-to-fine solve, stopping at the finest SOLVED level: full res only when the user asks for it —
            // motion fields are smooth, so a half-res solve sampled up in FinalizeMotion is visually near-identical
            // and several times cheaper (playback is expected to be near-free). Each level warps the neighbour by
            // its seed (temporal history at the coarsest, the coarser level's result below that) and refines the
            // total motion at that scale. Smoothness scales DOWN with the level: the user's Motion Coherence governs
            // the coarse large-scale flow while fine levels solve their small residuals under lighter regularization
            // — otherwise a high coherence setting averages small billow/curl motion into the broad plume flow.
            int finest = settings.fullResolutionMotion ? 0 : Mathf.Min(1, vdbLevelCount - 1);
            float motionScale = 1 << finest;
            Vector3Int motionRes = vdbLevelRes[finest];
            Vector3Int motionGroups = Groups(motionRes, 4);
            int coarsestScale = 1 << (vdbLevelCount - 1);
            RenderTexture read = null;
            for (int k = vdbLevelCount - 1; k >= finest; k--)
            {
                Vector3Int lr = vdbLevelRes[k];
                Vector3Int vg = Groups(lr, 4);
                float levelScale = 1 << k;
                int seedMode = k == vdbLevelCount - 1 ? (historyBlend > 0f ? 2 : 0) : 1;
                vdbPlaybackCs.SetInts("_VdbRes", lr.x, lr.y, lr.z);
                vdbPlaybackCs.SetFloat("_MotionMaxVoxels", settings.maxMotionVoxelsPerFrame / levelScale);
                vdbPlaybackCs.SetFloat("_MotionSmoothness", settings.smoothness * levelScale / coarsestScale);
                vdbPlaybackCs.SetFloat("_LevelScale", levelScale);
                vdbPlaybackCs.SetInt("_SeedMode", seedMode);
                vdbPlaybackCs.SetFloat("_SeedScale", 2f);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_SignalPair", vdbSignal[k]);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionSeed",
                                         seedMode == 1 ? read : vdbMotionHistory);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionHistory", vdbMotionHistory);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionTermsWrite",
                                         EnsureVdbLevelTexture(vdbMotionTerms, k));
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionWrite",
                                         EnsureVdbLevelTexture(vdbMotionA, k));
                vdbPlaybackCs.Dispatch(kVdbBuildMotionTerms, vg.x, vg.y, vg.z);

                read = vdbMotionA[k];
                RenderTexture write = EnsureVdbLevelTexture(vdbMotionB, k);
                // The coarse seed already carries the converged large-scale flow, so a full-res finest level only
                // needs a short residual polish — spending the whole budget there is what made playback expensive.
                int levelIterations = k == 0 ? Mathf.Max(3, settings.iterations / 3) : settings.iterations;
                for (int i = 0; i < levelIterations; i++)
                {
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_SignalPair", vdbSignal[k]);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionTerms", vdbMotionTerms[k]);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionRead", read);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionWrite", write);
                    vdbPlaybackCs.Dispatch(kVdbRefineMotion, vg.x, vg.y, vg.z);
                    Swap(ref read, ref write);
                }
            }

            // Second warped outer iteration at the finest solved level. Small billows and curl edges move
            // differently from the coarse seed by more than the ~1-voxel linear range of a single linearization;
            // without this pass they keep the smoothed large-scale flow until the discrepancy grows big enough to
            // snap — the "curls eaten away, then a small curl suddenly becomes a big one" failure. Re-linearizing
            // around the refined field measures them directly. Finest-level uniforms still bound from the loop.
            {
                vdbPlaybackCs.SetInt("_SeedMode", 1);
                vdbPlaybackCs.SetFloat("_SeedScale", 1f);
                RenderTexture rewarpDst = read == vdbMotionA[finest] ? vdbMotionB[finest] : vdbMotionA[finest];
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_SignalPair", vdbSignal[finest]);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionSeed", read);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionHistory", vdbMotionHistory);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionTermsWrite", vdbMotionTerms[finest]);
                vdbPlaybackCs.SetTexture(kVdbBuildMotionTerms, "_MotionWrite", rewarpDst);
                vdbPlaybackCs.Dispatch(kVdbBuildMotionTerms, motionGroups.x, motionGroups.y, motionGroups.z);
                read = rewarpDst;
                RenderTexture write = read == vdbMotionA[finest] ? vdbMotionB[finest] : vdbMotionA[finest];
                int polish = Mathf.Max(2, settings.iterations / (finest == 0 ? 4 : 2));
                for (int i = 0; i < polish; i++)
                {
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_SignalPair", vdbSignal[finest]);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionTerms", vdbMotionTerms[finest]);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionRead", read);
                    vdbPlaybackCs.SetTexture(kVdbRefineMotion, "_MotionWrite", write);
                    vdbPlaybackCs.Dispatch(kVdbRefineMotion, motionGroups.x, motionGroups.y, motionGroups.z);
                    Swap(ref read, ref write);
                }
            }

            // Temporal blend runs at the finest solved level (uniforms still bound). Finalize then runs at FULL
            // res, sampling the solved motion trilinearly with _MotionScale converting to full-res voxel units.
            if (historyBlend > 0f)
            {
                RenderTexture spare = read == vdbMotionA[finest] ? vdbMotionB[finest] : vdbMotionA[finest];
                vdbPlaybackCs.SetTexture(kVdbTemporalBlend, "_MotionRead", read);
                vdbPlaybackCs.SetTexture(kVdbTemporalBlend, "_MotionHistory", vdbMotionHistory);
                vdbPlaybackCs.SetTexture(kVdbTemporalBlend, "_MotionWrite", spare);
                vdbPlaybackCs.Dispatch(kVdbTemporalBlend, motionGroups.x, motionGroups.y, motionGroups.z);
                read = spare;
            }

            vdbPlaybackCs.SetInts("_VdbRes", res.x, res.y, res.z);
            vdbPlaybackCs.SetInts("_MotionRes", motionRes.x, motionRes.y, motionRes.z);
            vdbPlaybackCs.SetFloat("_MotionScale", motionScale);
            vdbPlaybackCs.SetFloat("_MotionMaxVoxels", settings.maxMotionVoxelsPerFrame);
            // FinalizeMotion also rewrites vdbMotionHistory; the passes above already consumed the old contents.
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_SignalPair", vdbSignal[0]);
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_MotionRead", read);
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_MotionHistoryWrite", vdbMotionHistory);
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_VelocityWrite", velCur);
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_FlowWrite", flowField);
            vdbPlaybackCs.SetTexture(kVdbFinalizeMotion, "_CurlWrite", curl);
            vdbPlaybackCs.Dispatch(kVdbFinalizeMotion, fullGroups.x, fullGroups.y, fullGroups.z);
        }

        /// <summary>Neutral renderer motion for a still VDB or while a streamed neighbour is pending.</summary>
        public void ClearVdbPlaybackMotion()
        {
            if (!playbackReconstruction) return;
            if (velCur != null) ClearBaseField(velCur);
            if (flowField != null) ClearBaseField(flowField);
            if (curl != null) ClearBaseField(curl);
            if (vdbMotionHistory != null) ClearBaseField(vdbMotionHistory);
        }

        void BuildCacheRenderOccupancy()
        {
            if (renderOcc == null || activeBricksBuf == null || indirectArgs == null) return;
            ClearVectorField(renderOcc, occRes);
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_RenderOccDensityGain", Mathf.Max(0f, RenderOccDensityGain));
            cs.SetFloat("_RenderOccFireGain", Mathf.Max(0f, RenderOccFireGain));
            cs.SetTexture(kBuildCacheRenderOcc, "_Fields", fieldsCur);
            cs.SetTexture(kBuildCacheRenderOcc, "_RenderOcc", renderOcc);
            cs.SetBuffer(kBuildCacheRenderOcc, "_ActiveBricks", activeBricksBuf);
            cs.DispatchIndirect(kBuildCacheRenderOcc, indirectArgs, 0);
        }

        /// <summary>Static-volume path (no sim, no cache): the caller has just written a DENSE field into
        /// <see cref="Fields"/> (e.g. a procedural cloud generator compute). Rebuild occupancy FROM THAT FIELD
        /// (dense MarkOcc, like the live sim's pre-pass — empty sky bricks get culled) + bake lighting, leaving the
        /// field ready to render. Unlike render-ready cache playback, this path must scan and condition the dense
        /// generated field. No emitters/points → field-only occupancy.</summary>
        public void BakeStaticVolume(float occThreshold, in LightingParams lp, bool bakeLight)
        {
            if (fieldsCur == null) return;

            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetFloat("_OccThreshold", Mathf.Max(1e-5f, occThreshold));
            cs.SetInt("_EmitterCount", 0);   // field-only occupancy (a generator has no emitters)
            cs.SetInt("_CfdFullDomainOccupancy", 0); // static generated volumes never inherit a CFD solver's policy

            // Dense occupancy: clear → mark from the field → dilate one brick (mirrors Step's occupancy pre-pass).
            cs.SetTexture(kClearOcc, "_Occ", occ);
            cs.SetBuffer(kClearOcc, "_ActiveCount", activeCountBuf);
            DispatchOcc(kClearOcc);
            cs.SetTexture(kMarkOccDense, "_Occ", occ);
            cs.SetTexture(kMarkOccDense, "_Fields", fieldsCur);
            Dispatch(kMarkOccDense);   // dense: a generator writes content anywhere, prior halos are meaningless
            cs.SetTexture(kDilateOcc, "_Occ", occ);
            cs.SetTexture(kDilateOcc, "_OccD", occD);
            cs.SetBuffer(kDilateOcc, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kDilateOcc, "_ActiveBricks", activeBricksBuf);
            cs.SetBuffer(kDilateOcc, "_RetiredBricks", retiredBricksBuf);   // written (unused here) — kernel needs the binding
            DispatchOcc(kDilateOcc);

            // Publish active-brick count → indirect args (valid state for any indirect consumers).
            cs.SetBuffer(kWriteArgs, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kWriteArgs, "_IndirectArgs", indirectArgs);
            cs.Dispatch(kWriteArgs, 1, 1, 1);

            // The generator just wrote a DENSE field: residue can exist far outside any halo, which the
            // per-step retired-brick clear can never reach. Arm the one-shot dense sweep for the next Step.
            pendingDenseInactiveClear = true;

            LightingEnabled = bakeLight;
            if (bakeLight) SetLighting(lp); else ReleaseLighting();
            BuildRenderResources(occThreshold, 0f, false);
            if (bakeLight && illumField != null) DispatchLighting();
        }

        // Occupancy from THIS frame's scattered bricks: clear → mark each cached brick occupied (one thread per
        // brick, no dense field read) → dilate one brick. The cached bricks already ARE the content set (the baker
        // culled empties), so we skip the dense MarkOcc full-grid pass. Dilate fills activeBricksBuf/activeCountBuf
        // for indirect dispatch (and for next frame's sparse clear).
        void MarkOccupancyFromScatter(ComputeBuffer brickIdx, int brickOffset, int brickCount,
                                      ComputeBuffer brickIdx2 = null, int brickOffset2 = 0, int brickCount2 = 0)
        {
            SetInts3("_Res", res.x, res.y, res.z);
            SetInts3("_OccRes", occRes.x, occRes.y, occRes.z);
            cs.SetInt("_CfdFullDomainOccupancy", 0); // cache playback occupancy is defined by the cache itself
            cs.SetTexture(kClearOcc, "_Occ", occ);
            cs.SetBuffer(kClearOcc, "_ActiveCount", activeCountBuf);
            DispatchOcc(kClearOcc);

            if (brickCount > 0 && brickIdx != null)
            {
                cs.SetInt("_ScatterBrickCount", brickCount);
                cs.SetInt("_ScatterBrickOffset", brickOffset);
                SetInts3("_BrickGrid", occRes.x, occRes.y, occRes.z);
                cs.SetTexture(kMarkOccFromScatter, "_Occ", occ);
                cs.SetBuffer(kMarkOccFromScatter, "_ScatterBrickIdx", brickIdx);
                cs.Dispatch(kMarkOccFromScatter, Mathf.CeilToInt(brickCount / 64f), 1, 1);
            }

            // Motion smoothing: the neighbour frame's bricks join the active set (union), so the blend, the
            // empty-space skip, and the detail bake all cover smoke that exists in only one of the two frames.
            if (brickCount2 > 0 && brickIdx2 != null)
            {
                cs.SetInt("_ScatterBrickCount", brickCount2);
                cs.SetInt("_ScatterBrickOffset", brickOffset2);
                SetInts3("_BrickGrid", occRes.x, occRes.y, occRes.z);
                cs.SetTexture(kMarkOccFromScatter, "_Occ", occ);
                cs.SetBuffer(kMarkOccFromScatter, "_ScatterBrickIdx", brickIdx2);
                cs.Dispatch(kMarkOccFromScatter, Mathf.CeilToInt(brickCount2 / 64f), 1, 1);
            }

            cs.SetTexture(kDilateOcc, "_Occ", occ);
            cs.SetTexture(kDilateOcc, "_OccD", occD);
            cs.SetBuffer(kDilateOcc, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kDilateOcc, "_ActiveBricks", activeBricksBuf);
            cs.SetBuffer(kDilateOcc, "_RetiredBricks", retiredBricksBuf);   // written (unused here) — kernel needs the binding
            DispatchOcc(kDilateOcc);

            // Container-owned sparse support bakes consume the same active/retired lists through indirect args.
            cs.SetBuffer(kWriteArgs, "_ActiveCount", activeCountBuf);
            cs.SetBuffer(kWriteArgs, "_IndirectArgs", indirectArgs);
            cs.Dispatch(kWriteArgs, 1, 1, 1);
        }

        static void Swap(ref RenderTexture a, ref RenderTexture b) { var t = a; a = b; b = t; }

        public void Dispose()
        {
            requestedCfdSdf = null;
            appliedCfdSdf = null;
            Rel(ref fieldsCur); Rel(ref fieldsNext); Rel(ref fieldsTmp);
            Rel(ref renderCur); Rel(ref renderNext); Rel(ref renderOcc);
            Rel(ref velCur); Rel(ref velNext);
            Rel(ref fineCur); Rel(ref fineNext); Rel(ref flowField); Rel(ref warpField);
            Rel(ref playbackNeighbourField); Rel(ref vdbMotionHistory);
            Rel(ref playbackKeyField);
            for (int k = 0; k < VdbPyramidLevels; k++)
            {
                Rel(ref vdbSignal[k]); Rel(ref vdbMotionTerms[k]); Rel(ref vdbMotionA[k]); Rel(ref vdbMotionB[k]);
            }
            Rel(ref curl); Rel(ref div); Rel(ref pressure); Rel(ref solid); Rel(ref solidDummy);
            ReleaseCfdCoarseResources();
            Rel(ref cfdPressureVis);
            Rel(ref occ); Rel(ref occD);
            Rel(ref illumField); Rel(ref scatterField); Rel(ref scatterTmp); Rel(ref fireLightField); Rel(ref lightPacked);
            emitterBuf?.Release(); emitterBuf = null;
            pointBuf?.Release(); pointBuf = null;
            surfAccumBuf?.Release(); surfAccumBuf = null;
            surfSources.Clear();   // buffers inside are owned by the surface effects, not the solver
            meshAccumBuf?.Release(); meshAccumBuf = null; meshHashCapacity = 0;
            meshMaskDummyU?.Release(); meshMaskDummyU = null;
            meshSources.Clear();   // positions/prevPos buffers are owned by the emitters, not the solver
            fragmentSources.Clear(); // fracture simulation buffers are owned by BrazeVoronoiFracture
            forceBuf?.Release(); forceBuf = null;
            splineVortexBuf?.Release(); splineVortexBuf = null;
            obstacleBuf?.Release(); obstacleBuf = null;
            cfdBoundaryBuf?.Release(); cfdBoundaryBuf = null;
            cfdFluxBuf?.Release(); cfdFluxBuf = null;
            cfdSurfaceLoadBuf?.Release(); cfdSurfaceLoadBuf = null;
            cfdTemperaturePeakBuf?.Release(); cfdTemperaturePeakBuf = null;
            cfdRefPressureBuf?.Release(); cfdRefPressureBuf = null;
            activeCountBuf?.Release(); activeCountBuf = null;
            fireMomentsBuf?.Release(); fireMomentsBuf = null;
            cachePackBuf?.Release(); cachePackBuf = null;
            cacheSparseCountBuf?.Release(); cacheSparseCountBuf = null;
            cacheSparseIdxBuf?.Release(); cacheSparseIdxBuf = null;
            cacheSparseBlocksBuf?.Release(); cacheSparseBlocksBuf = null;
            cachePlaybackWarpBricksBuf?.Release(); cachePlaybackWarpBricksBuf = null;
            previousCacheScatterBrickCount = 0;
            activeBricksBuf?.Release(); activeBricksBuf = null;
            retiredBricksBuf?.Release(); retiredBricksBuf = null;
            indirectArgs?.Release(); indirectArgs = null;
            brickDbgBuf?.Release(); brickDbgBuf = null;
        }

        static void Rel(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            // Destroy() warns / no-ops in edit mode (the timeline's edit-mode sim disposes there).
            if (Application.isPlaying) Object.Destroy(rt); else Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
