using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// GPU trail renderer for Braze particles and Cloner instances. It records live source positions into GPU history
    /// rings and draws one indirect ribbon per tracked source element without copying positions back to the CPU.
    /// </summary>
    [AddComponentMenu("BrazeFX/Particles/Braze Trail Renderer", 22)]
    [DisallowMultipleComponent]
    public sealed class BrazeTrailRenderer : MonoBehaviour, IBrazeAnimatedTarget
    {
        public static readonly List<BrazeTrailRenderer> Active = new();

        public enum SourceType { ParticleSystem, Cloner }
        public enum BlendMode { Additive, Alpha, Multiply }
        public enum ColorMode { FireSmoke, Gradient, ByAge, Constant }

        const float MinTrailTime = 0.01f;
        const int MaxGroupsPerDim = 65535;
        const int MinRenderSegments = 8;
        const int MaxRenderSegments = 64;
        const int MinHistorySamples = 16;
        const int MaxHistorySamples = 160;
        const int TrailSettingsVersion = 2;

        [Header("Source")]
        [Tooltip("GPU source whose elements are recorded into trail ribbons.")]
        public SourceType sourceType = SourceType.ParticleSystem;
        [Tooltip("Particle system whose particle positions are recorded into trail ribbons.")]
        public BrazeParticleSystem particles;
        [Tooltip("Cloner whose GPU instance positions are recorded into trail ribbons.")]
        public BrazeCloner cloner;

        [Header("Trail")]
        [Tooltip("How many seconds of recorded source motion each ribbon shows.")]
        [BrazeAnimatable("SOURCE/Trail Time", 0.01f, 8f)]
        [Min(MinTrailTime)] public float trailTime = 1.0f;
        [Tooltip("Single trail quality control. Higher values increase both recorded path density and ribbon subdivisions.")]
        [Range(0f, 1f)] public float trailDetail = 0.35f;
        [Tooltip("Normalized percentage of source elements that get trail ribbons. 1 = all, 0 = none.")]
        [Range(0f, 1f)] public float trackedParticleRatio = 1f;
        [Tooltip("Cull complete trail bounds against each rendering camera on the GPU before the indirect draw.")]
        public bool frustumCull = true;
        [HideInInspector, Tooltip("Driven by Trail Detail. Number of ribbon quads drawn along each trail.")]
        [Range(1, 128)] public int segments = 24;
        [HideInInspector, Tooltip("Driven by Trail Detail. History samples stored per particle.")]
        [Range(2, 256)] public int historySamples = 64;
        [SerializeField, HideInInspector] int trailSettingsVersion;

        [Header("Shape")]
        [Tooltip("Minimum ribbon width in world units. Each tracked element gets a stable random width between Min and Max.")]
        [BrazeAnimatable("SHAPE/Width Min", 0f, 2f)]
        [Min(0f)] public float width = 0.12f;
        [Tooltip("Maximum ribbon width in world units. Set equal to Width Min for uniform trails.")]
        [BrazeAnimatable("SHAPE/Width Max", 0f, 2f)]
        [Min(0f)] public float widthMax = 0.12f;
        [Tooltip("Width multiplier along each trail: left = head, right = tail.")]
        public AnimationCurve widthCurve = DefaultWidthCurve();
        [Tooltip("Smooths the recorded particle path without adding extrapolated motion.")]
        [BrazeAnimatable("SHAPE/Trail Smoothing", 0f, 1f)]
        [Range(0f, 1f)] public float pathSmoothing = 0.35f;

        [Header("Shading")]
        public ColorMode colorMode = ColorMode.FireSmoke;
        [Tooltip("Colour ramp used by Gradient and ByAge modes.")]
        [GradientUsage(true)] public Gradient ramp = DefaultRamp();
        [ColorUsage(true, true)] public Color constantColor = new(1f, 0.6f, 0.2f, 1f);
        [ColorUsage(true, true)] public Color fireColor = new(1f, 0.5f, 0.15f, 1f);
        [ColorUsage(true, true)] public Color smokeColor = new(0.2f, 0.2f, 0.22f, 1f);
        [BrazeAnimatable("SHADING/Color Mid", 0f, 1f)]
        [Range(0f, 1f)] public float colorMid = 0.4f;
        [BrazeAnimatable("SHADING/Emissive", 0f, 8f)]
        [Min(0f)] public float emissive = 2f;
        [BrazeAnimatable("SHADING/Opacity", 0f, 1f)]
        [Range(0f, 1f)] public float opacity = 1f;
        [Tooltip("Opacity along each trail: left = head, right = tail.")]
        public AnimationCurve lengthAlpha = DefaultLengthAlphaCurve();
        [Tooltip("Multiply opacity by particle normalized lifetime.")]
        public bool opacityOverLife = false;
        [Tooltip("Opacity multiplier over particle lifetime: left = birth, right = death.")]
        public AnimationCurve opacityLifeCurve = DefaultOpacityLifeCurve();
        [BrazeAnimatable("SHADING/Soft Edge", 0f, 0.5f)]
        [Range(0f, 0.5f)] public float softEdge = 0.3f;
        [Tooltip("Shader-only internal shadowing for the trail. Darkens dense core/tail areas without adding compute work or extra passes.")]
        [BrazeAnimatable("SHADING/Self Shadow", 0f, 1f)]
        [Range(0f, 1f)] public float selfShadow = 0f;
        [HideInInspector] public bool inheritColor = false;
        public BlendMode blendMode = BlendMode.Additive;

        [Header("Flame")]
        [BrazeAnimatable("FLAME/Amount", 0f, 1f)]
        [Range(0f, 1f)] public float fireAmount = 0f;
        [BrazeAnimatable("FLAME/Scale", 0.25f, 16f)]
        [Range(0.25f, 16f)] public float fireScale = 4f;
        [BrazeAnimatable("FLAME/Speed", 0f, 8f)]
        [Range(0f, 8f)] public float fireSpeed = 1.5f;
        [Range(1, 5)] public int fireDetail = 4;
        [BrazeAnimatable("FLAME/Sharpness", 0.2f, 4f)]
        [Range(0.2f, 4f)] public float fireSharpness = 1.2f;
        [BrazeAnimatable("FLAME/Edge", 0f, 1f)]
        [Range(0f, 1f)] public float fireEdge = 0.3f;
        [BrazeAnimatable("FLAME/Heat", 0f, 4f)]
        [Range(0f, 4f)] public float fireHeat = 1.5f;

        [Header("Smoke")]
        [BrazeAnimatable("SMOKE/Amount", 0f, 1f)]
        [Range(0f, 1f)] public float smokeAmount = 0f;
        [ColorUsage(true, true)] public Color smokeLayerColor = new(0.18f, 0.17f, 0.15f, 1f);
        [BrazeAnimatable("SMOKE/Scale", 0.25f, 16f)]
        [Range(0.25f, 16f)] public float smokeScale = 4f;
        [BrazeAnimatable("SMOKE/Speed", 0f, 4f)]
        [Range(0f, 4f)] public float smokeSpeed = 0.6f;
        [Range(1, 5)] public int smokeDetail = 4;
        [BrazeAnimatable("SMOKE/Depth", 0f, 1f)]
        [Range(0f, 1f)] public float smokeDepth = 0.65f;
        [BrazeAnimatable("SMOKE/Edge", 0f, 1f)]
        [Range(0f, 1f)] public float smokeEdge = 0.35f;

        ComputeShader cs;
        int kClear, kClearSourceMap, kBuildSourceMap, kPrepareParticle, kPrepareCloner, kAppend, kAppendCloner, kWriteNodeDispatch,
            kBuildNodes, kBuildClonerNodes, kBuildBounds, kPrepareCull, kCull;
        GraphicsBuffer trailBuf, lineNodeBuf, lineArgsBuf, trackedArgsBuf, stripIndexBuf;
        GraphicsBuffer trackedTrailBuf, drawVisibleBuf, trailBoundsBuf;
        GraphicsBuffer trailSourceBuf, sourceToTrailBuf;
        GraphicsBuffer particleDispatchArgsBuf, nodeDispatchArgsBuf, cullDispatchArgsBuf;
        Material mat;
        MaterialPropertyBlock mpb;
        Texture2D rampLut, opacityLifeLut, lengthAlphaLut, widthCurveLut;
        bool rampDirty = true, opacityLifeDirty = true, lengthAlphaDirty = true, widthCurveDirty = true;
        int allocSourceCapacity, allocCapacity, allocPoints, allocNodeCapacity, allocNodeCount,
            allocVisibleCapacity, allocMappedCapacity, allocStripSegs = -1;
        int ringHead;
        float sampleAccum;
        float widthCurvePeak = 1f;
        BrazeParticleSystem prevParticles;
        BrazeCloner prevCloner;
        SourceType prevSourceType;
        bool sourcePrepared;
        bool historyNeedsSeed = true;

        static readonly Plane[] CullPlaneScratch = new Plane[6];
        static readonly Vector4[] CullPlaneVectors = new Vector4[6];

        static readonly int IdVisible = Shader.PropertyToID("_VisibleIndices");
        static readonly int IdLineNodes = Shader.PropertyToID("_LineNodes");
        static readonly int IdRamp = Shader.PropertyToID("_Ramp");
        static readonly int IdOpacityLife = Shader.PropertyToID("_OpacityLifeTex");
        static readonly int IdLengthAlpha = Shader.PropertyToID("_LengthAlphaTex");
        static readonly int IdWidthCurve = Shader.PropertyToID("_WidthCurveTex");

        public Material RenderMaterial => mat;
        public GraphicsBuffer ArgsBuffer => lineArgsBuf;
        public GraphicsBuffer IndexBuffer => stripIndexBuf;
        public MaterialPropertyBlock Properties => mpb;
        internal GraphicsBuffer LineNodeBuffer => lineNodeBuf;
        internal GraphicsBuffer TrackedTrailBuffer => trackedTrailBuf;
        internal GraphicsBuffer TrackedArgsBuffer => trackedArgsBuf;
        internal GraphicsBuffer DrawVisibleBuffer => drawVisibleBuf;
        internal GraphicsBuffer TrailBoundsBuffer => trailBoundsBuf;
        internal GraphicsBuffer NodeDispatchArgsBuffer => nodeDispatchArgsBuf;
        internal GraphicsBuffer CullDispatchArgsBuffer => cullDispatchArgsBuf;
        internal GraphicsBuffer TrailHistoryBuffer => trailBuf;
        public bool Ready => mat != null && lineArgsBuf != null && sourcePrepared &&
            (sourceType == SourceType.Cloner
                ? cloner != null && cloner.isActiveAndEnabled
                : particles != null && particles.Ready);

        public static int ResolveRenderSegments(float detail)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MinRenderSegments, MaxRenderSegments, Mathf.Clamp01(detail))), 1, 128);
        }

        public static int ResolveHistorySamples(float detail)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MinHistorySamples, MaxHistorySamples, Mathf.Clamp01(detail))), 2, 256);
        }

        public static int ResolveTrackedCount(int sourceCount, float ratio)
        {
            sourceCount = Mathf.Max(0, sourceCount);
            ratio = Mathf.Clamp01(ratio);
            if (sourceCount == 0 || ratio <= 0f) return 0;
            if (ratio >= 1f) return sourceCount;
            int proportionalCount = (int)System.Math.Round(sourceCount * (double)ratio);
            return Mathf.Clamp(proportionalCount, 1, sourceCount);
        }

        /// <summary>Maps a dense tracked trail slot to its stable source slot. The evenly spaced mapping selects
        /// exactly <paramref name="trackedCount"/> slots without a GPU-side allocation overflow.</summary>
        public static int ResolveSourceIndexForTrail(int trailIndex, int sourceCount, int trackedCount)
        {
            sourceCount = Mathf.Max(0, sourceCount);
            trackedCount = Mathf.Clamp(trackedCount, 0, sourceCount);
            if (sourceCount == 0 || trackedCount == 0) return -1;
            trailIndex = Mathf.Clamp(trailIndex, 0, trackedCount - 1);
            if (trackedCount >= sourceCount) return trailIndex;
            long numerator = (2L * trailIndex + 1L) * sourceCount;
            return Mathf.Min((int)(numerator / (2L * trackedCount)), sourceCount - 1);
        }

        /// <summary>Inverse of <see cref="ResolveSourceIndexForTrail"/>; -1 means that source slot is not selected.</summary>
        public static int ResolveTrailIndexForSource(int sourceIndex, int sourceCount, int trackedCount)
        {
            sourceCount = Mathf.Max(0, sourceCount);
            trackedCount = Mathf.Clamp(trackedCount, 0, sourceCount);
            if (sourceIndex < 0 || sourceIndex >= sourceCount || trackedCount == 0) return -1;
            if (trackedCount >= sourceCount) return sourceIndex;

            int low = 0;
            int high = trackedCount - 1;
            while (low <= high)
            {
                int candidate = low + ((high - low) >> 1);
                int mapped = ResolveSourceIndexForTrail(candidate, sourceCount, trackedCount);
                if (mapped == sourceIndex) return candidate;
                if (mapped < sourceIndex) low = candidate + 1;
                else high = candidate - 1;
            }
            return -1;
        }

        public static void ResetFor(BrazeParticleSystem system)
        {
            if (system == null) return;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var trail = Active[i];
                if (trail == null)
                {
                    Active.RemoveAt(i);
                    continue;
                }
                if (trail.particles == system)
                    trail.ResetTrailHistory();
            }
        }

        void Reset()
        {
            particles = GetComponent<BrazeParticleSystem>();
            cloner = GetComponent<BrazeCloner>();
            sourceType = particles != null ? SourceType.ParticleSystem
                : cloner != null ? SourceType.Cloner
                : SourceType.ParticleSystem;
        }

        void OnEnable()
        {
            BrazeNoiseTex.Bind();   // globals reset on domain reload; the material may survive — rebind always
            MigrateTrailSettings();
            ApplyTrailDetail();

            if (sourceType == SourceType.Cloner)
            {
                if (cloner == null) cloner = GetComponent<BrazeCloner>();
            }
            else if (particles == null)
            {
                particles = GetComponent<BrazeParticleSystem>();
                if (particles == null && TryGetComponent(out BrazeCloner localCloner))
                {
                    cloner = localCloner;
                    sourceType = SourceType.Cloner;
                }
            }

            var csAsset = Resources.Load<ComputeShader>("BrazeTrail");
            var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Trail");
            if (csAsset == null || shader == null)
            {
                Debug.LogError("[BrazeFX] Braze Trail Renderer needs the BrazeTrail compute shader and BrazeFX/Trail shader.");
                enabled = false;
                return;
            }

            cs = csAsset;
            kClear = cs.FindKernel("ClearTrail");
            kClearSourceMap = cs.FindKernel("ClearSourceMap");
            kBuildSourceMap = cs.FindKernel("BuildSourceMapDirect");
            kPrepareParticle = cs.FindKernel("PrepareParticleFrame");
            kPrepareCloner = cs.FindKernel("PrepareClonerFrame");
            kAppend = cs.FindKernel("TrailAppend");
            kAppendCloner = cs.FindKernel("TrailAppendCloner");
            kWriteNodeDispatch = cs.FindKernel("WriteNodeDispatchArgs");
            kBuildNodes = cs.FindKernel("BuildLineNodes");
            kBuildClonerNodes = cs.FindKernel("BuildClonerLineNodes");
            kBuildBounds = cs.FindKernel("BuildTrailBounds");
            kPrepareCull = cs.FindKernel("PrepareCull");
            kCull = cs.FindKernel("CullTrails");

            mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            BrazeNoiseTex.Bind();   // baked base noise for the fire/smoke fbm (global textures)
            mpb = new MaterialPropertyBlock();
            rampLut = BrazeTransferFunction.CreateRampLut("BrazeTrailRamp");
            opacityLifeLut = BrazeTransferFunction.CreateCurveLut("BrazeTrailOpacityLife");
            lengthAlphaLut = BrazeTransferFunction.CreateCurveLut("BrazeTrailLengthAlpha");
            widthCurveLut = BrazeTransferFunction.CreateCurveLut("BrazeTrailWidthCurve");
            lineArgsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, sizeof(uint));
            trackedArgsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, sizeof(uint));
            particleDispatchArgsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            nodeDispatchArgsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            cullDispatchArgsBuf = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            // Zero the indirect args NOW: Ready flips true the moment this method returns, and the render
            // feature may draw this very frame (e.g. enabled from a script after our LateUpdate slot). Fresh
            // GPU memory is garbage — a garbage instance count draws millions of junk ribbons from unbound
            // node buffers until the first LateUpdate writes real args. Zero instances = draw nothing.
            lineArgsBuf.SetData(new uint[5]);
            trackedArgsBuf.SetData(new uint[5]);

            // Authoritative state reset — don't rely on the realloc path alone to rewind the history ring.
            ringHead = 0;
            sampleAccum = 0f;
            prevParticles = null;
            prevCloner = null;
            sourcePrepared = false;

            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            trailBuf?.Release(); trailBuf = null;
            lineNodeBuf?.Release(); lineNodeBuf = null;
            trackedTrailBuf?.Release(); trackedTrailBuf = null;
            drawVisibleBuf?.Release(); drawVisibleBuf = null;
            trailBoundsBuf?.Release(); trailBoundsBuf = null;
            trailSourceBuf?.Release(); trailSourceBuf = null;
            sourceToTrailBuf?.Release(); sourceToTrailBuf = null;
            stripIndexBuf?.Release(); stripIndexBuf = null;
            lineArgsBuf?.Release(); lineArgsBuf = null;
            trackedArgsBuf?.Release(); trackedArgsBuf = null;
            particleDispatchArgsBuf?.Release(); particleDispatchArgsBuf = null;
            nodeDispatchArgsBuf?.Release(); nodeDispatchArgsBuf = null;
            cullDispatchArgsBuf?.Release(); cullDispatchArgsBuf = null;
            if (mat != null) { DestroyOwned(mat); mat = null; }
            cs = null;
            if (rampLut != null) { DestroyOwned(rampLut); rampLut = null; }
            if (opacityLifeLut != null) { DestroyOwned(opacityLifeLut); opacityLifeLut = null; }
            if (lengthAlphaLut != null) { DestroyOwned(lengthAlphaLut); lengthAlphaLut = null; }
            if (widthCurveLut != null) { DestroyOwned(widthCurveLut); widthCurveLut = null; }
            allocCapacity = 0;
            allocSourceCapacity = 0;
            allocPoints = 0;
            allocNodeCapacity = 0;
            allocNodeCount = 0;
            allocVisibleCapacity = 0;
            allocMappedCapacity = 0;
            allocStripSegs = -1;
            prevParticles = null;
            prevCloner = null;
            sourcePrepared = false;
            historyNeedsSeed = true;
        }

        static void DestroyOwned(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        void OnValidate()
        {
            ValidateSettings(true);
        }

        void ValidateSettings(bool dirtyLuts)
        {
            MigrateTrailSettings();
            ApplyTrailDetail();
            trackedParticleRatio = Mathf.Clamp01(trackedParticleRatio);
            trailTime = Mathf.Max(MinTrailTime, trailTime);
            width = Mathf.Max(0f, width);
            widthMax = Mathf.Max(width, widthMax);
            pathSmoothing = Mathf.Clamp01(pathSmoothing);
            widthCurve ??= DefaultWidthCurve();
            lengthAlpha ??= DefaultLengthAlphaCurve();
            opacityLifeCurve ??= DefaultOpacityLifeCurve();
            fireDetail = Mathf.Clamp(fireDetail, 1, 5);
            selfShadow = Mathf.Clamp01(selfShadow);
            smokeAmount = Mathf.Clamp01(smokeAmount);
            smokeScale = Mathf.Clamp(smokeScale, 0.25f, 16f);
            smokeSpeed = Mathf.Clamp(smokeSpeed, 0f, 4f);
            smokeDetail = Mathf.Clamp(smokeDetail, 1, 5);
            smokeDepth = Mathf.Clamp01(smokeDepth);
            smokeEdge = Mathf.Clamp01(smokeEdge);
            if (dirtyLuts)
            {
                rampDirty = true;
                opacityLifeDirty = true;
                lengthAlphaDirty = true;
                widthCurveDirty = true;
            }
            sourcePrepared = false;
        }

        // Timeline animates only scalar [BrazeAnimatable] fields on this component. Avoid rebaking and uploading all
        // four curve/gradient textures for scalar animation; inspector curve edits still arrive through OnValidate.
        public void OnBrazeAnimated() => ValidateSettings(false);

        void LateUpdate()
        {
            sourcePrepared = false;
            if (mat == null || lineArgsBuf == null || cs == null) return;

            BakeLutsIfNeeded();
            ApplyTrailDetail();

            int points = Mathf.Max(2, historySamples);
            if (sourceType == SourceType.Cloner) UpdateClonerSource(points);
            else UpdateParticleSource(points);
        }

        void UpdateParticleSource(int points)
        {
            if (particles == null || !particles.Ready || particles.ParticleBuffer == null ||
                particles.VisibleBuffer == null || particles.ArgsBuffer == null) return;

            int sourceCapacity = particles.ParticleCapacity;
            int capacity = ResolveTrackedCount(sourceCapacity, trackedParticleRatio);
            EnsureResources(sourceCapacity, capacity, points, segments + 1);

            if (prevSourceType != sourceType || prevParticles != particles)
            {
                prevSourceType = sourceType;
                prevParticles = particles;
                prevCloner = null;
                ResetTrailHistory();
            }

            int commits = AdvanceHistory(points);
            int head = ((ringHead % points) + points) % points;
            Matrix4x4 model = particles.ModelMatrix;
            int fillCount = ResolveHistoryFill(commits);

            cs.SetInt("_Capacity", capacity);
            cs.SetInt("_SourceCapacity", sourceCapacity);
            cs.SetInt("_Points", points);
            cs.SetInt("_RingHead", head);
            cs.SetInt("_FillCount", fillCount);
            cs.SetInt("_NodeCount", segments + 1);
            cs.SetInt("_DrawSegments", segments);
            cs.SetInt("_SegVerts", segments * 6);
            cs.SetMatrix("_Model", model);

            cs.SetBuffer(kPrepareParticle, "_SourceArgs", particles.ArgsBuffer);
            cs.SetBuffer(kPrepareParticle, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kPrepareParticle, "_ParticleDispatchArgs", particleDispatchArgsBuf);
            cs.Dispatch(kPrepareParticle, 1, 1, 1);

            cs.SetBuffer(kAppend, "_Particles", particles.ParticleBuffer);
            cs.SetBuffer(kAppend, "_SourceVisible", particles.VisibleBuffer);
            cs.SetBuffer(kAppend, "_SourceArgs", particles.ArgsBuffer);
            cs.SetBuffer(kAppend, "_SourceToTrail", sourceToTrailBuf);
            cs.SetBuffer(kAppend, "_TrailSource", trailSourceBuf);
            cs.SetBuffer(kAppend, "_Trail", trailBuf);
            cs.SetBuffer(kAppend, "_TrackedTrail", trackedTrailBuf);
            cs.SetBuffer(kAppend, "_DrawVisible", drawVisibleBuf);
            cs.SetBuffer(kAppend, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kAppend, "_ParticleDispatchArgs", particleDispatchArgsBuf);
            cs.DispatchIndirect(kAppend, particleDispatchArgsBuf);

            EnsureStripIndices(segments);
            WriteNodeDispatchArgs();
            BuildLineNodes(capacity, sourceCapacity, points, segments, head, model);
            if (frustumCull) BuildTrailBounds(model);
            BindMaterial(model);
            sourcePrepared = true;
        }

        void UpdateClonerSource(int points)
        {
            if (cloner == null ||
                !cloner.TryGetGpuPositions(out GraphicsBuffer basePositions,
                    out GraphicsBuffer positionOffsets, out int sourceCapacity))
                return;

            int capacity = ResolveTrackedCount(sourceCapacity, trackedParticleRatio);
            EnsureResources(sourceCapacity, capacity, points, segments + 1);

            if (prevSourceType != sourceType || prevCloner != cloner)
            {
                prevSourceType = sourceType;
                prevCloner = cloner;
                prevParticles = null;
                ResetTrailHistory();
            }

            int commits = AdvanceHistory(points);
            int head = ((ringHead % points) + points) % points;
            Matrix4x4 model = Matrix4x4.identity;
            int fillCount = ResolveHistoryFill(commits);

            cs.SetInt("_Capacity", capacity);
            cs.SetInt("_SourceCapacity", sourceCapacity);
            cs.SetInt("_SourceSeed", cloner.seed);
            cs.SetInt("_Points", points);
            cs.SetInt("_RingHead", head);
            cs.SetInt("_FillCount", fillCount);
            cs.SetInt("_NodeCount", segments + 1);
            cs.SetInt("_DrawSegments", segments);
            cs.SetInt("_SegVerts", segments * 6);

            cs.SetBuffer(kPrepareCloner, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kPrepareCloner, "_ParticleDispatchArgs", particleDispatchArgsBuf);
            cs.Dispatch(kPrepareCloner, 1, 1, 1);

            cs.SetBuffer(kAppendCloner, "_CloneBasePosition", basePositions);
            cs.SetBuffer(kAppendCloner, "_ClonePositionOffset", positionOffsets);
            cs.SetBuffer(kAppendCloner, "_TrailSource", trailSourceBuf);
            cs.SetBuffer(kAppendCloner, "_Trail", trailBuf);
            cs.SetBuffer(kAppendCloner, "_TrackedTrail", trackedTrailBuf);
            cs.SetBuffer(kAppendCloner, "_DrawVisible", drawVisibleBuf);
            cs.SetBuffer(kAppendCloner, "_ParticleDispatchArgs", particleDispatchArgsBuf);
            cs.DispatchIndirect(kAppendCloner, particleDispatchArgsBuf);

            EnsureStripIndices(segments);
            WriteNodeDispatchArgs();
            BuildClonerLineNodes(capacity, sourceCapacity, points, segments, head, basePositions, positionOffsets);
            if (frustumCull) BuildTrailBounds(model);
            BindMaterial(model);
            sourcePrepared = true;
        }

        int ResolveHistoryFill(int commits)
        {
            if (!historyNeedsSeed) return commits;
            historyNeedsSeed = false;
            return Mathf.Max(1, commits);
        }

        int AdvanceHistory(int points)
        {
            float interval = Mathf.Max(MinTrailTime, trailTime) / Mathf.Max(1, points - 1);
            sampleAccum += Time.deltaTime;
            if (sampleAccum < interval) return 0;

            int commits = Mathf.Min(Mathf.FloorToInt(sampleAccum / interval), points);
            ringHead += commits;
            sampleAccum -= commits * interval;
            if (sampleAccum > interval) sampleAccum = 0f;
            return commits;
        }

        void EnsureResources(int sourceCapacity, int capacity, int points, int nodeCount)
        {
            sourceCapacity = Mathf.Max(0, sourceCapacity);
            int storageCapacity = Mathf.Max(1, capacity);
            int sourceStorageCapacity = Mathf.Max(1, sourceCapacity);
            points = Mathf.Max(2, points);
            nodeCount = Mathf.Max(2, nodeCount);
            bool mappingChanged = allocSourceCapacity != sourceCapacity || allocMappedCapacity != capacity;
            bool resetHistory = mappingChanged;
            bool rebuildSourceMap = mappingChanged;

            if (sourceToTrailBuf == null || sourceToTrailBuf.count != sourceStorageCapacity)
            {
                sourceToTrailBuf?.Release();
                sourceToTrailBuf = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, sourceStorageCapacity, sizeof(uint));
                rebuildSourceMap = true;
            }

            if (trailBuf == null || allocCapacity != storageCapacity || allocPoints != points)
            {
                trailBuf?.Release();
                trailBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity * points, 16);
                allocCapacity = storageCapacity;
                allocPoints = points;
                resetHistory = true;
            }

            if (lineNodeBuf == null || allocNodeCapacity != storageCapacity || allocNodeCount != nodeCount)
            {
                lineNodeBuf?.Release();
                lineNodeBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity * nodeCount, 48);
                allocNodeCapacity = storageCapacity;
                allocNodeCount = nodeCount;
            }

            if (trackedTrailBuf == null || drawVisibleBuf == null || trailBoundsBuf == null || trailSourceBuf == null ||
                allocVisibleCapacity != storageCapacity)
            {
                trackedTrailBuf?.Release();
                drawVisibleBuf?.Release();
                trailBoundsBuf?.Release();
                trailSourceBuf?.Release();
                trackedTrailBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity, sizeof(uint));
                drawVisibleBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity, sizeof(uint));
                trailBoundsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity, 16);
                trailSourceBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storageCapacity, sizeof(uint));
                allocVisibleCapacity = storageCapacity;
                rebuildSourceMap = true;
            }

            allocSourceCapacity = sourceCapacity;
            allocMappedCapacity = capacity;
            if (rebuildSourceMap) BuildSourceMap(sourceCapacity, capacity);
            if (resetHistory) ResetTrailHistory();
        }

        void BuildSourceMap(int sourceCapacity, int capacity)
        {
            cs.SetInt("_SourceCapacity", sourceCapacity);
            cs.SetInt("_Capacity", capacity);
            cs.SetBuffer(kClearSourceMap, "_SourceToTrail", sourceToTrailBuf);
            Dispatch1D(kClearSourceMap, sourceCapacity);
            if (capacity <= 0) return;

            cs.SetBuffer(kBuildSourceMap, "_TrailSource", trailSourceBuf);
            cs.SetBuffer(kBuildSourceMap, "_SourceToTrail", sourceToTrailBuf);
            Dispatch1D(kBuildSourceMap, capacity);
        }

        void EnsureStripIndices(int segs)
        {
            if (stripIndexBuf != null && allocStripSegs == segs) return;

            stripIndexBuf?.Release();
            int idxCount = segs * 6;
            stripIndexBuf = new GraphicsBuffer(GraphicsBuffer.Target.Index, idxCount, sizeof(uint));
            var idx = new uint[idxCount];
            for (int s = 0; s < segs; s++)
            {
                uint a0 = (uint)(2 * s);
                uint a1 = a0 + 1u;
                uint b0 = a0 + 2u;
                uint b1 = a0 + 3u;
                int o = s * 6;
                idx[o + 0] = a0; idx[o + 1] = b0; idx[o + 2] = a1;
                idx[o + 3] = a1; idx[o + 4] = b0; idx[o + 5] = b1;
            }
            stripIndexBuf.SetData(idx);
            allocStripSegs = segs;
        }

        void WriteNodeDispatchArgs()
        {
            cs.SetBuffer(kWriteNodeDispatch, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kWriteNodeDispatch, "_NodeDispatchArgs", nodeDispatchArgsBuf);
            cs.SetBuffer(kWriteNodeDispatch, "_CullDispatchArgs", cullDispatchArgsBuf);
            cs.SetBuffer(kWriteNodeDispatch, "_DrawArgs", lineArgsBuf);
            cs.Dispatch(kWriteNodeDispatch, 1, 1, 1);
        }

        void BuildLineNodes(int capacity, int sourceCapacity, int points, int drawnSegments, int head,
            Matrix4x4 model)
        {
            cs.SetInt("_Capacity", capacity);
            cs.SetInt("_SourceCapacity", sourceCapacity);
            cs.SetInt("_Points", points);
            cs.SetInt("_RingHead", head);
            cs.SetInt("_NodeCount", drawnSegments + 1);
            cs.SetInt("_DrawSegments", drawnSegments);
            cs.SetMatrix("_Model", model);
            cs.SetMatrix("_WorldToParticle", model.inverse);
            cs.SetFloat("_PathSmoothing", pathSmoothing);
            cs.SetFloat("_TrailPhase", sampleAccum);
            cs.SetFloat("_SampleInterval", Mathf.Max(MinTrailTime, trailTime) / Mathf.Max(1, points - 1));
            cs.SetBuffer(kBuildNodes, "_Particles", particles.ParticleBuffer);
            cs.SetBuffer(kBuildNodes, "_TrailSource", trailSourceBuf);
            cs.SetBuffer(kBuildNodes, "_Trail", trailBuf);
            cs.SetBuffer(kBuildNodes, "_LineNodes", lineNodeBuf);
            cs.SetBuffer(kBuildNodes, "_TrackedTrail", trackedTrailBuf);
            cs.SetBuffer(kBuildNodes, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kBuildNodes, "_NodeDispatchArgs", nodeDispatchArgsBuf);
            cs.DispatchIndirect(kBuildNodes, nodeDispatchArgsBuf);
        }

        void BuildClonerLineNodes(int capacity, int sourceCapacity, int points, int drawnSegments, int head,
            GraphicsBuffer basePositions, GraphicsBuffer positionOffsets)
        {
            cs.SetInt("_Capacity", capacity);
            cs.SetInt("_SourceCapacity", sourceCapacity);
            cs.SetInt("_SourceSeed", cloner.seed);
            cs.SetInt("_Points", points);
            cs.SetInt("_RingHead", head);
            cs.SetInt("_NodeCount", drawnSegments + 1);
            cs.SetInt("_DrawSegments", drawnSegments);
            cs.SetFloat("_PathSmoothing", pathSmoothing);
            cs.SetFloat("_TrailPhase", sampleAccum);
            cs.SetFloat("_SampleInterval", Mathf.Max(MinTrailTime, trailTime) / Mathf.Max(1, points - 1));
            cs.SetBuffer(kBuildClonerNodes, "_CloneBasePosition", basePositions);
            cs.SetBuffer(kBuildClonerNodes, "_ClonePositionOffset", positionOffsets);
            cs.SetBuffer(kBuildClonerNodes, "_TrailSource", trailSourceBuf);
            cs.SetBuffer(kBuildClonerNodes, "_Trail", trailBuf);
            cs.SetBuffer(kBuildClonerNodes, "_LineNodes", lineNodeBuf);
            cs.SetBuffer(kBuildClonerNodes, "_TrackedTrail", trackedTrailBuf);
            cs.SetBuffer(kBuildClonerNodes, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kBuildClonerNodes, "_NodeDispatchArgs", nodeDispatchArgsBuf);
            cs.DispatchIndirect(kBuildClonerNodes, nodeDispatchArgsBuf);
        }

        void BuildTrailBounds(Matrix4x4 model)
        {
            cs.SetInt("_NodeCount", segments + 1);
            cs.SetInt("_DrawSegments", segments);
            cs.SetFloat("_CullRadius",
                0.5f * Mathf.Max(width, widthMax) * Mathf.Max(widthCurvePeak, 0f));
            cs.SetMatrix("_Model", model);
            cs.SetBuffer(kBuildBounds, "_TrackedArgs", trackedArgsBuf);
            cs.SetBuffer(kBuildBounds, "_TrackedTrail", trackedTrailBuf);
            cs.SetBuffer(kBuildBounds, "_LineNodes", lineNodeBuf);
            cs.SetBuffer(kBuildBounds, "_TrailBounds", trailBoundsBuf);
            cs.SetBuffer(kBuildBounds, "_CullDispatchArgs", cullDispatchArgsBuf);
            cs.DispatchIndirect(kBuildBounds, cullDispatchArgsBuf);
        }

        void ResetTrailHistory()
        {
            ringHead = 0;
            sampleAccum = 0f;
            historyNeedsSeed = true;
            if (trailBuf == null || cs == null || allocCapacity <= 0 || allocPoints <= 0) return;

            cs.SetInt("_Capacity", allocCapacity);
            cs.SetInt("_Points", allocPoints);
            cs.SetBuffer(kClear, "_Trail", trailBuf);
            Dispatch1D(kClear, allocCapacity * allocPoints);
        }

        /// <summary>Build the camera-visible trail list and indirect draw count entirely on the GPU. The CPU only
        /// supplies the six camera planes; no particle/trail count or position ever leaves GPU memory.</summary>
        internal void DispatchCameraCull(CommandBuffer cmd, Camera camera)
        {
            if (cmd == null || cs == null || !sourcePrepared || trackedArgsBuf == null || trackedTrailBuf == null ||
                trailBoundsBuf == null || drawVisibleBuf == null || lineArgsBuf == null || cullDispatchArgsBuf == null)
                return;
            if (!frustumCull || camera == null) return; // LateUpdate already published the unculled GPU list/args.

            GeometryUtility.CalculateFrustumPlanes(camera, CullPlaneScratch);
            for (int i = 0; i < CullPlaneScratch.Length; i++)
            {
                Plane plane = CullPlaneScratch[i];
                CullPlaneVectors[i] = new Vector4(
                    plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            }
            cmd.SetComputeVectorArrayParam(cs, Shader.PropertyToID("_CullPlanes"), CullPlaneVectors);

            cmd.SetComputeIntParam(cs, "_CullOn", 1);
            cmd.SetComputeIntParam(cs, "_SegVerts", segments * 6);

            cmd.SetComputeBufferParam(cs, kPrepareCull, "_TrackedArgs", trackedArgsBuf);
            cmd.SetComputeBufferParam(cs, kPrepareCull, "_DrawArgs", lineArgsBuf);
            cmd.SetComputeBufferParam(cs, kPrepareCull, "_CullDispatchArgs", cullDispatchArgsBuf);
            cmd.DispatchCompute(cs, kPrepareCull, 1, 1, 1);

            cmd.SetComputeBufferParam(cs, kCull, "_TrackedArgs", trackedArgsBuf);
            cmd.SetComputeBufferParam(cs, kCull, "_TrackedTrail", trackedTrailBuf);
            cmd.SetComputeBufferParam(cs, kCull, "_TrailBounds", trailBoundsBuf);
            cmd.SetComputeBufferParam(cs, kCull, "_DrawVisible", drawVisibleBuf);
            cmd.SetComputeBufferParam(cs, kCull, "_DrawArgs", lineArgsBuf);
            cmd.SetComputeBufferParam(cs, kCull, "_CullDispatchArgs", cullDispatchArgsBuf);
            cmd.DispatchCompute(cs, kCull, cullDispatchArgsBuf, 0);
        }

        void BindMaterial(Matrix4x4 model)
        {
            mpb.SetBuffer(IdVisible, drawVisibleBuf);
            mpb.SetBuffer(IdLineNodes, lineNodeBuf);
            mpb.SetMatrix("_Model", model);
            mpb.SetInt("_Segments", segments);
            mpb.SetInt("_NodeCount", segments + 1);
            float widthMin = Mathf.Max(0f, Mathf.Min(width, widthMax));
            float widthHi = Mathf.Max(0f, Mathf.Max(width, widthMax));
            mpb.SetFloat("_WidthMin", widthMin);
            mpb.SetFloat("_WidthMax", widthHi);
            if (widthCurveLut != null) mpb.SetTexture(IdWidthCurve, widthCurveLut);

            mpb.SetFloat("_ColorMode", (float)colorMode);
            if (rampLut != null) mpb.SetTexture(IdRamp, rampLut);
            mpb.SetColor("_ConstColor", constantColor);
            mpb.SetColor("_FireColor", fireColor);
            mpb.SetColor("_SmokeColor", smokeColor);
            mpb.SetFloat("_ColorMid", colorMid);
            mpb.SetFloat("_Emissive", Mathf.Max(0f, emissive));
            mpb.SetFloat("_Opacity", Mathf.Clamp01(opacity));
            if (lengthAlphaLut != null) mpb.SetTexture(IdLengthAlpha, lengthAlphaLut);
            if (opacityLifeLut != null) mpb.SetTexture(IdOpacityLife, opacityLifeLut);
            mpb.SetFloat("_UseOpacityLife", opacityOverLife ? 1f : 0f);
            mpb.SetFloat("_SoftEdge", softEdge);
            mpb.SetFloat("_SelfShadow", Mathf.Clamp01(selfShadow));
            mpb.SetFloat("_FireAmount", Mathf.Clamp01(fireAmount));
            mpb.SetFloat("_FireScale", Mathf.Max(0.05f, fireScale));
            mpb.SetFloat("_FireSpeed", Mathf.Max(0f, fireSpeed));
            mpb.SetFloat("_FireDetail", Mathf.Clamp(fireDetail, 1, 5));
            mpb.SetFloat("_FireSharp", Mathf.Max(0.05f, fireSharpness));
            mpb.SetFloat("_FireEdge", Mathf.Max(0f, fireEdge));
            mpb.SetFloat("_FireHeat", Mathf.Max(0f, fireHeat));
            mpb.SetFloat("_FireTime", Time.time);
            mpb.SetFloat("_SmokeAmount", Mathf.Clamp01(smokeAmount));
            mpb.SetColor("_SmokeLayerColor", smokeLayerColor);
            mpb.SetFloat("_SmokeScale", Mathf.Max(0.05f, smokeScale));
            mpb.SetFloat("_SmokeSpeed", Mathf.Max(0f, smokeSpeed));
            mpb.SetFloat("_SmokeDetail", Mathf.Clamp(smokeDetail, 1, 5));
            mpb.SetFloat("_SmokeDepth", Mathf.Clamp01(smokeDepth));
            mpb.SetFloat("_SmokeEdge", Mathf.Clamp01(smokeEdge));

            int src;
            int dst;
            int mul;
            switch (blendMode)
            {
                case BlendMode.Alpha:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    mul = 0;
                    break;
                case BlendMode.Multiply:
                    src = (int)UnityEngine.Rendering.BlendMode.DstColor;
                    dst = (int)UnityEngine.Rendering.BlendMode.Zero;
                    mul = 1;
                    break;
                default:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.One;
                    mul = 0;
                    break;
            }
            mat.SetFloat("_BlendSrc", src);
            mat.SetFloat("_BlendDst", dst);
            mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
            mpb.SetFloat("_Multiply", mul);
        }

        void ApplyTrailDetail()
        {
            trailDetail = Mathf.Clamp01(trailDetail);
            segments = ResolveRenderSegments(trailDetail);
            historySamples = ResolveHistorySamples(trailDetail);
        }

        void MigrateTrailSettings()
        {
            if (trailSettingsVersion >= TrailSettingsVersion) return;

            if (trailSettingsVersion < 1)
            {
                float segU = Mathf.InverseLerp(
                    MinRenderSegments,
                    MaxRenderSegments,
                    Mathf.Clamp(segments, MinRenderSegments, MaxRenderSegments));
                float sampleU = Mathf.InverseLerp(
                    MinHistorySamples,
                    MaxHistorySamples,
                    Mathf.Clamp(historySamples, MinHistorySamples, MaxHistorySamples));
                trailDetail = Mathf.Clamp01(Mathf.Max(segU, sampleU));
            }

            if (trailSettingsVersion < 2)
            {
                width = Mathf.Max(0f, width);
                widthMax = width;
            }

            trailSettingsVersion = TrailSettingsVersion;
        }

        void BakeLutsIfNeeded()
        {
            if (rampDirty && rampLut != null)
            {
                BrazeTransferFunction.BakeRamp(rampLut, ramp);
                rampDirty = false;
            }
            if (opacityLifeCurve == null)
            {
                opacityLifeCurve = DefaultOpacityLifeCurve();
                opacityLifeDirty = true;
            }
            if (opacityLifeDirty && opacityLifeLut != null)
            {
                BrazeTransferFunction.BakeCurve1DSmooth(opacityLifeLut, opacityLifeCurve);
                opacityLifeDirty = false;
            }
            if (lengthAlpha == null)
            {
                lengthAlpha = DefaultLengthAlphaCurve();
                lengthAlphaDirty = true;
            }
            if (lengthAlphaDirty && lengthAlphaLut != null)
            {
                BrazeTransferFunction.BakeCurve1DSmooth(lengthAlphaLut, lengthAlpha);
                lengthAlphaDirty = false;
            }
            if (widthCurve == null)
            {
                widthCurve = DefaultWidthCurve();
                widthCurveDirty = true;
            }
            if (widthCurveDirty && widthCurveLut != null)
            {
                widthCurvePeak = BrazeTransferFunction.BakeCurve1DUnclampedSmooth(widthCurveLut, widthCurve);
                widthCurveDirty = false;
            }
        }

        void Dispatch1D(int kernel, int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            cs.SetInt("_RowThreads", gx * 64);
            cs.Dispatch(kernel, gx, gy, 1);
        }

        static Gradient DefaultRamp()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.0f, 0.85f, 0.45f), 0.0f),
                    new GradientColorKey(new Color(1.0f, 0.35f, 0.10f), 0.45f),
                    new GradientColorKey(new Color(0.25f, 0.25f, 0.28f), 1.0f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        static AnimationCurve DefaultOpacityLifeCurve() =>
            BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 1f),
                new Vector2(0.75f, 1f),
                new Vector2(1f, 0f));

        static AnimationCurve DefaultLengthAlphaCurve() =>
            BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 1f),
                new Vector2(0.5f, 0.65f),
                new Vector2(0.75f, 0.35f),
                new Vector2(1f, 0f));

        static AnimationCurve DefaultWidthCurve() =>
            BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 1f),
                new Vector2(1f, 0f));
    }
}
