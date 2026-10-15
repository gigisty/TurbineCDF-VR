using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace BrazeFX
{
    // Opt-in, allocation-free capture for standalone QA builds. The production renderer pays one disabled branch
    // unless an external diagnostic harness enables this type through reflection; no customer-facing setting or
    // render behavior depends on it.
    internal static class BrazeSliceRuntimeDiagnostics
    {
        internal static bool Enabled;
        internal static bool HasSnapshot;
        internal static int Frame;
        internal static string CameraName;
        internal static int TargetWidth;
        internal static int TargetHeight;
        internal static int WorkingWidth;
        internal static int WorkingHeight;
        internal static int ConfiguredDownsample;
        internal static int EffectiveDownsample;
        internal static int HeightCap;
        internal static int ReadyContainers;
        internal static int FeatureStepCeiling;
        internal static float FeatureSamplesPerVoxel;
        internal static int ConfiguredTemporalMode;
        internal static int EligibleTemporalMode;
        internal static int ActiveTemporalMode;
        internal static int InterleavePhase;
        internal static bool TemporalResolve;
        internal static bool OffscreenComposite;
        internal static bool AdaptiveMarching;
        internal static bool CompactHistory;
        internal static int ContainerSamples;
        internal static int MinActualSteps;
        internal static int MaxActualSteps;
        internal static float MinActualSamplesPerVoxel;
        internal static float MaxActualSamplesPerVoxel;
        internal static float MaxBaseStepScale;
        internal static float MaxJitterStrength;
        internal static float MaxJitterScale;
        internal static float MinSourceFilterStrength;
        internal static int MinGridWidth;
        internal static int MinGridHeight;
        internal static int MinGridDepth;

        static bool s_Recording;

        internal static void Begin(
            Camera camera,
            RenderTextureDescriptor target,
            Vector2Int working,
            int configuredDownsample,
            int effectiveDownsample,
            int heightCap,
            int readyContainers,
            int featureStepCeiling,
            float featureSamplesPerVoxel,
            int configuredTemporalMode,
            int eligibleTemporalMode,
            bool temporalResolve,
            bool offscreenComposite,
            bool adaptiveMarching,
            bool compactHistory)
        {
            s_Recording = Enabled && camera != null && camera.cameraType == CameraType.Game;
            if (!s_Recording)
                return;

            HasSnapshot = true;
            Frame = Time.frameCount;
            CameraName = camera.name;
            TargetWidth = target.width;
            TargetHeight = target.height;
            WorkingWidth = working.x;
            WorkingHeight = working.y;
            ConfiguredDownsample = configuredDownsample;
            EffectiveDownsample = effectiveDownsample;
            HeightCap = heightCap;
            ReadyContainers = readyContainers;
            FeatureStepCeiling = featureStepCeiling;
            FeatureSamplesPerVoxel = featureSamplesPerVoxel;
            ConfiguredTemporalMode = configuredTemporalMode;
            EligibleTemporalMode = eligibleTemporalMode;
            ActiveTemporalMode = 0;
            InterleavePhase = 0;
            TemporalResolve = temporalResolve;
            OffscreenComposite = offscreenComposite;
            AdaptiveMarching = adaptiveMarching;
            CompactHistory = compactHistory;
            ContainerSamples = 0;
            MinActualSteps = int.MaxValue;
            MaxActualSteps = 0;
            MinActualSamplesPerVoxel = float.PositiveInfinity;
            MaxActualSamplesPerVoxel = 0f;
            MaxBaseStepScale = 0f;
            MaxJitterStrength = 0f;
            MaxJitterScale = 0f;
            MinSourceFilterStrength = float.PositiveInfinity;
            MinGridWidth = int.MaxValue;
            MinGridHeight = int.MaxValue;
            MinGridDepth = int.MaxValue;
        }

        internal static void SetTemporal(int activeMode, int phase)
        {
            if (!s_Recording)
                return;
            ActiveTemporalMode = activeMode;
            InterleavePhase = phase;
        }

        internal static void RecordContainer(
            int actualSteps,
            float actualSamplesPerVoxel,
            float baseStepScale,
            float jitterStrength,
            float jitterScale,
            float sourceFilterStrength,
            int gridWidth,
            int gridHeight,
            int gridDepth)
        {
            if (!s_Recording)
                return;

            ContainerSamples++;
            MinActualSteps = Mathf.Min(MinActualSteps, actualSteps);
            MaxActualSteps = Mathf.Max(MaxActualSteps, actualSteps);
            MinActualSamplesPerVoxel = Mathf.Min(MinActualSamplesPerVoxel, actualSamplesPerVoxel);
            MaxActualSamplesPerVoxel = Mathf.Max(MaxActualSamplesPerVoxel, actualSamplesPerVoxel);
            MaxBaseStepScale = Mathf.Max(MaxBaseStepScale, baseStepScale);
            MaxJitterStrength = Mathf.Max(MaxJitterStrength, jitterStrength);
            MaxJitterScale = Mathf.Max(MaxJitterScale, jitterScale);
            MinSourceFilterStrength = Mathf.Min(MinSourceFilterStrength, sourceFilterStrength);
            MinGridWidth = Mathf.Min(MinGridWidth, gridWidth);
            MinGridHeight = Mathf.Min(MinGridHeight, gridHeight);
            MinGridDepth = Mathf.Min(MinGridDepth, gridDepth);
        }
    }

    /// <summary>
    /// Internal URP renderer pass bundle that renders a BrazeContainer's built-in slice renderer:
    /// a screen-fitted quad whose fragment marches the
    /// container into a native or downsampled camera-space target and composites with a hardware blend.
    /// Temporal interleaving uses flow-reprojected history. Owned by <see cref="BrazeRenderFeature"/>.
    /// </summary>
    internal sealed class BrazeSliceRendererPasses
    {
        // A per-pixel checkerboard diverges inside every 2x2 fragment quad: the GPU still issues the heavy march
        // for every wave with mostly-idle lanes. Coherent tiles let whole waves take the cheap early-out.
        internal const int InterleaveTileSize = 8;

        BrazeVolumeSlicePass _pass;
        BrazeCastShadowPass _castPass;
        readonly List<BrazeContainer> _ready = new();

        internal void Create()
        {
            _pass = new BrazeVolumeSlicePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            // Darken opaque geometry just before the volume slices composite
            // over it.
            _castPass = new BrazeCastShadowPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            // Both passes sample scene depth BEFORE transparents. Declaring the
            // input makes URP schedule _CameraDepthTexture production early
            // enough; without it, a renderer set to copy depth After
            // Transparents hands these passes an unwritten texture (reads as
            // sky -> cast shadow silently outputs 1, slices lose occlusion).
            // With copy mode After Opaques this is the same single copy per
            // frame, so it adds no cost.
            _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            _castPass.ConfigureInput(ScriptableRenderPassInput.Depth);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

#if UNITY_EDITOR
        void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
        {
            // Timestamp heuristics cannot see this boundary reliably: edit-mode Game-view repaints keep
            // re-stamping prevTime/prevVP with the reset clocks, so by the next play entry the stale history
            // looks fresh. Tear the histories down at the transition itself — every session then starts from
            // the same state as the very first one (fresh alloc -> resolveValid=false -> one-frame full-march
            // bootstrap before interleaving resumes).
            if (change == UnityEditor.PlayModeStateChange.ExitingEditMode ||
                change == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                _pass?.ReleaseHistories();
        }
#endif

        internal void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData,
            int sliceCount,
            float slicesPerVoxel,
            float jitterStrength,
            int downsample = 1,
            int heightCap = 0,
            int temporalMode = 0,
            float temporalBlend = 0f,
            bool motionReproject = false,
            bool compactHistory = false,
            bool adaptiveMarching = false,
            float adaptiveMaxStep = 4f,
            float adaptiveFireMaxStep = 1f,
            float adaptiveVariance = 0.35f)
        {
            var camType = renderingData.cameraData.cameraType;
            if (camType != CameraType.Game && camType != CameraType.SceneView)
                return;

            Camera camera = renderingData.cameraData.camera;
            BrazeContainer.CollectSliceRenderers(_ready, camera);
            if (_ready.Count == 0)
            {
                _pass.ResetHistory();
                return;
            }

            // Cast shadows need the baked self-shadow volume — enqueue the pass if ANY ready volume casts.
            bool anyCast = false;
            for (int i = 0; i < _ready.Count; i++)
                if (_ready[i].sliceCastShadows && _ready[i].SliceShadowTexture != null) { anyCast = true; break; }
            if (anyCast)
                renderer.EnqueuePass(_castPass);

            // Scene and Game cameras consume the same feature-owned sampling policy. A container's simulation preset
            // must never change camera-space output quality.
            bool sceneView = camType == CameraType.SceneView;
            // The Scene view normally keeps a plain full march (diagnostic invariant: "Game view blocky, Scene
            // view pristine" must keep meaning "the defect is in the temporal chain"). While the timeline's
            // edit-mode Simulate drives these containers, the Scene view is the user's ONLY live view — full-
            // marching every repaint made Simulate 2-4x more render-bound than play mode — so it gets the same
            // interleave machinery the Game view gets. Play mode is untouched by construction: containers never
            // report EditorSimulating while playing.
            bool editSimSceneView = false;
#if UNITY_EDITOR
            if (sceneView)
                for (int i = 0; i < _ready.Count; i++)
                    if (_ready[i] != null && _ready[i].EditorSimulating) { editSimSceneView = true; break; }
#endif
            _pass.maxSliceCount = Mathf.Clamp(sliceCount, 8, 512);
            _pass.slicesPerVoxel = Mathf.Clamp(slicesPerVoxel, 0.25f, 2f);
            _pass.jitterStrength = Mathf.Clamp01(jitterStrength);
            _pass.sceneView = sceneView;
            _pass.downsample = Mathf.Clamp(downsample, 1, 4);
            _pass.heightCap = Mathf.Clamp(heightCap, 0, 2160);
            int configuredTemporalMode = Mathf.Clamp(temporalMode, 0, 2);
            int cameraTemporalMode = ResolveInterleaveMode(
                configuredTemporalMode, temporalBlend, motionReproject, sceneView && !editSimSceneView);
            _pass.SetupTemporal(configuredTemporalMode, cameraTemporalMode, temporalBlend, motionReproject);
            _pass.compactHistory = compactHistory;
            _pass.adaptiveMarching = adaptiveMarching;
            _pass.adaptiveMaxStep = Mathf.Clamp(adaptiveMaxStep, 1f, 4f);
            _pass.adaptiveFireMaxStep = Mathf.Clamp(adaptiveFireMaxStep, 1f, 2f);
            _pass.adaptiveVariance = Mathf.Clamp(adaptiveVariance, 0.1f, 1f);
            renderer.EnqueuePass(_pass);
        }

        internal static int ResolveInterleaveMode(int requested, float temporal, bool reproject, bool sceneView)
            => temporal > 0.001f && reproject && !sceneView ? Mathf.Clamp(requested, 0, 2) : 0;

        internal static int InterleavePhaseForFrame(int mode, int frame)
            => mode == 1 ? (frame & 1) : mode == 2 ? (frame & 3) : 0;

        // A working-resolution pixel cannot represent dither finer than one sample per pixel. Values above one
        // only stride through the hash lattice and make flow move the noise unnecessarily fast.
        internal static float ResolveJitterScale(float requested)
            => Mathf.Clamp(requested, 0.1f, 1f);

        // Ray-plane decorrelation is a renderer-quality invariant, not a material-type discount. CFD previously
        // capped the feature-owned 0.75 at 0.3; after removing its coarse brick strides that weak phase offset
        // exposed the remaining one-voxel sample terraces. Every volume and every shipped preset now consumes the
        // same feature value. Presets may change step count/resolution, but must not reintroduce coherent planes.
        internal static float ResolveVolumeJitterStrength(float featureStrength, bool cfdDiagnostic)
            => Mathf.Clamp01(featureStrength);

        // The occupancy oracle describes the unwarped 4^3 scalar bricks. A nonlinear Render Detail lookup can
        // leave and re-enter support between the endpoints of a brick leap, so no endpoint-only occupancy test can
        // make that leap safe. Warped volumes therefore retain the exact fine march; ordinary volumes keep the
        // acceleration. Flow Speed preserves its jittered sample lattice across leaps inside the shader.
        internal static bool CanUseOccupancyForVolume(bool hasOccupancy, bool useWarp)
            => hasOccupancy && !useWarp;

        // CFD diagnostic dye is visible through velocity/temperature colour, while the occupancy importance
        // statistics describe density/fire only. A filled tunnel therefore looks "smooth" to those statistics
        // even when its false-colour field changes sharply, and an adaptive 4-voxel stride exposes the underlying
        // brick lattice. Keep the occupancy texture for genuinely empty-space leaps, but march occupied CFD dye at
        // the exact voxel stride. Ordinary smoke/fire retains adaptive acceleration.
        internal static bool CanUseAdaptiveMarchingForVolume(
            bool featureEnabled, bool useOccupancy, bool cfdDiagnostic)
            => featureEnabled && useOccupancy && !cfdDiagnostic;

        // Each tile is revisited every two/four frames. Advance a low-discrepancy ray phase only when that tile
        // can receive a new march; spatial/off modes remain deterministic and do not shimmer. Keep the index small
        // so its shader float conversion retains exact integer precision during very long editor sessions.
        internal static int JitterSequenceForFrame(int mode, int frame)
            => mode == 1 ? ((frame >> 1) & 1023) : mode == 2 ? ((frame >> 2) & 1023) : 0;

        internal static Vector2Int ResolveWorkingResolution(
            int width, int height, int downsample, int heightCap = 0)
        {
            int divisor = Mathf.Clamp(downsample, 1, 4);
            int workingWidth = Mathf.Max(1, width / divisor);
            int workingHeight = Mathf.Max(1, height / divisor);
            if (heightCap > 0 && workingHeight > heightCap)
            {
                // The cap is authored in pixels, not in powers-of-two. Converting it to an integer divisor can
                // undershoot badly at common Player sizes (1286 px / ceil(1286/420) = 321 px), making a fullscreen
                // build visibly softer than a 1080p Game view. Preserve the camera aspect while landing on the exact
                // requested ceiling; the spatial divisor remains the minimum quality/performance floor.
                workingHeight = Mathf.Max(1, heightCap);
                workingWidth = Mathf.Max(1,
                    Mathf.RoundToInt(width * (workingHeight / (float)Mathf.Max(1, height))));
            }
            return new Vector2Int(workingWidth, workingHeight);
        }

        // Generic fire effects author this distance in world metres and may intentionally use a broad dissolve.
        // A CFD volume instead needs a grid-scale intersection: the default 0.75 m softness on a one-metre domain
        // removes smoke far in front of an obstacle and reads as a mesh-shaped cutout. Roughly one cell preserves
        // anti-aliasing at the depth surface without weakening correct opaque occlusion behind the mesh.
        internal static float ResolveDepthSoftness(bool cfdVolume, float authoredWorld, float maximumCellWorld)
            => cfdVolume
                ? Mathf.Max(0.001f, Mathf.Max(maximumCellWorld, 0f) * 1.25f)
                : Mathf.Max(0.001f, authoredWorld);

        internal static GraphicsFormat ResolveHistoryFormat(bool compact)
            => compact ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R16G16B16A16_SFloat;

        internal static int HistoryBytesPerPixel(bool compact) => compact ? 4 : 8;

        internal static bool InterleavePixelValid(int mode, int phase, int x, int y)
        {
            int tileX = x / InterleaveTileSize;
            int tileY = y / InterleaveTileSize;
            return mode == 1 ? ((tileX + tileY + phase) & 1) == 0
             : mode == 2 ? ((tileX & 1) + ((tileY & 1) << 1)) == phase
             : true;
        }

        internal static bool TemporalSettingsRequireHistoryReset(
            int previousMode, float previousBlend, bool previousReproject,
            int mode, float blend, bool reproject)
        {
            bool wasEnabled = Mathf.Clamp(previousMode, 0, 2) != 0 && previousBlend > 0.001f && previousReproject;
            bool isEnabled = Mathf.Clamp(mode, 0, 2) != 0 && blend > 0.001f && reproject;
            return Mathf.Clamp(previousMode, 0, 2) != Mathf.Clamp(mode, 0, 2) ||
                   wasEnabled != isEnabled || previousReproject != reproject;
        }

        internal static float ResolveAdaptiveMaxStep(float requestedMaxStep, float baseStepScale)
            => Mathf.Clamp(requestedMaxStep / Mathf.Max(baseStepScale, 1f), 1f, 4f);

        // One authoritative budget: live containers and cache hosts consume the renderer feature value unchanged.
        internal static int ResolveSliceStepBudget(int featureBudget)
            => Mathf.Clamp(featureBudget, 8, 512);

        // CPU mirror of the shader classifier, kept testable so threshold edits cannot silently turn fire coarse.
        internal static float AdaptiveStepScale(
            float maxVisibility, float maxFire, float meanVisibility, float combinedVariation,
            float varianceThreshold, float maxStep, float fireMaxStep)
        {
            float mean = Mathf.Max(meanVisibility, 0f);
            float threshold = Mathf.Max(varianceThreshold, 0.05f);
            float variationDetail = Mathf.Clamp01(combinedVariation / Mathf.Max(threshold * threshold, 1e-5f));
            float peakDetail = Mathf.Clamp01((maxVisibility - mean) / Mathf.Max(maxVisibility, 0.05f) * 2f);
            float detail = Mathf.Max(variationDetail, peakDetail);
            float firePresence = Mathf.Clamp01(maxFire * 8f);
            float smoothStep = Mathf.Lerp(Mathf.Max(maxStep, 1f),
                Mathf.Clamp(fireMaxStep, 1f, Mathf.Max(maxStep, 1f)), firePresence);
            return Mathf.Lerp(smoothStep, 1f, detail);
        }

        internal void Dispose()
        {
            _pass?.Dispose();
            _pass = null;
            _castPass?.Dispose();
            _castPass = null;
        }

        // Cached shader property IDs: every string-keyed Material.Set* marshals + hashes the name per
        // call — at ~90 setters per volume per camera per frame that is pure main-thread waste.
        static class SliceShaderIds
        {
            public static readonly int _AmbientColor = Shader.PropertyToID("_AmbientColor");
            public static readonly int _AdaptiveMarching = Shader.PropertyToID("_AdaptiveMarching");
            public static readonly int _AdaptiveFireMaxStep = Shader.PropertyToID("_AdaptiveFireMaxStep");
            public static readonly int _AdaptiveMaxStep = Shader.PropertyToID("_AdaptiveMaxStep");
            public static readonly int _AdaptiveVarianceThreshold = Shader.PropertyToID("_AdaptiveVarianceThreshold");
            public static readonly int _BrazeFluidReprojection = Shader.PropertyToID("_BrazeFluidReprojection");
            public static readonly int _BrazeCompactHistory = Shader.PropertyToID("_BrazeCompactHistory");
            public static readonly int _BrazeHistoryDitherFrame = Shader.PropertyToID("_BrazeHistoryDitherFrame");
            public static readonly int _BrazeInterleaveMode = Shader.PropertyToID("_BrazeInterleaveMode");
            public static readonly int _BrazeInterleavePhase = Shader.PropertyToID("_BrazeInterleavePhase");
            public static readonly int _BrazeJitterSampleIndex = Shader.PropertyToID("_BrazeJitterSampleIndex");
            public static readonly int _BrazePrevVP = Shader.PropertyToID("_BrazePrevVP");
            public static readonly int _BrazeReproject = Shader.PropertyToID("_BrazeReproject");
            public static readonly int _BrazeSliceDeltaTime = Shader.PropertyToID("_BrazeSliceDeltaTime");
            public static readonly int _BrazeTemporalBlend = Shader.PropertyToID("_BrazeTemporalBlend");
            public static readonly int _BrazeVolumeCurrent = Shader.PropertyToID("_BrazeVolumeCurrent");
            public static readonly int _BrazeVolumeHistory = Shader.PropertyToID("_BrazeVolumeHistory");
            public static readonly int _BrazeVolumeMotion = Shader.PropertyToID("_BrazeVolumeMotion");
            public static readonly int _BrazeVolumeTexelSize = Shader.PropertyToID("_BrazeVolumeTexelSize");
            public static readonly int _CastBasis1 = Shader.PropertyToID("_CastBasis1");
            public static readonly int _CastBasis2 = Shader.PropertyToID("_CastBasis2");
            public static readonly int _CastInvD = Shader.PropertyToID("_CastInvD");
            public static readonly int _CastShadowFade = Shader.PropertyToID("_CastShadowFade");
            public static readonly int _CastShadowSoftness = Shader.PropertyToID("_CastShadowSoftness");
            public static readonly int _CastShadowStrength = Shader.PropertyToID("_CastShadowStrength");
            public static readonly int _CastSunDirSim = Shader.PropertyToID("_CastSunDirSim");
            public static readonly int _DebugView = Shader.PropertyToID("_DebugView");
            public static readonly int _CfdHeatMin = Shader.PropertyToID("_CfdHeatMin");
            public static readonly int _CfdHeatInvRange = Shader.PropertyToID("_CfdHeatInvRange");
            public static readonly int _CfdHeatOpacity = Shader.PropertyToID("_CfdHeatOpacity");
            public static readonly int _CfdHeatCold = Shader.PropertyToID("_CfdHeatCold");
            public static readonly int _CfdHeatMiddle = Shader.PropertyToID("_CfdHeatMiddle");
            public static readonly int _CfdHeatHot = Shader.PropertyToID("_CfdHeatHot");
            public static readonly int _CfdSmokeTemperature = Shader.PropertyToID("_CfdSmokeTemperature");
            public static readonly int _CfdSmokeFlowSpeed = Shader.PropertyToID("_CfdSmokeFlowSpeed");
            public static readonly int _CfdSmokeColorContrast = Shader.PropertyToID("_CfdSmokeColorContrast");
            public static readonly int _CfdSmokeFlowEmphasis = Shader.PropertyToID("_CfdSmokeFlowEmphasis");
            public static readonly int _CfdSmokeDiagnostic = Shader.PropertyToID("_CfdSmokeDiagnostic");
            public static readonly int _Definition = Shader.PropertyToID("_Definition");
            public static readonly int _DensityContrast = Shader.PropertyToID("_DensityContrast");
            public static readonly int _DepthSoftness = Shader.PropertyToID("_DepthSoftness");
            public static readonly int _EdgeFade = Shader.PropertyToID("_EdgeFade");
            public static readonly int _FineInvRes = Shader.PropertyToID("_FineInvRes");
            public static readonly int _FineResF = Shader.PropertyToID("_FineResF");
            public static readonly int _FineVolume = Shader.PropertyToID("_FineVolume");
            public static readonly int _FireCurve = Shader.PropertyToID("_FireCurve");
            public static readonly int _FireDensityScale = Shader.PropertyToID("_FireDensityScale");
            public static readonly int _FireGlow = Shader.PropertyToID("_FireGlow");
            public static readonly int _FireLightColor = Shader.PropertyToID("_FireLightColor");
            public static readonly int _FirePreint = Shader.PropertyToID("_FirePreint");
            public static readonly int _FireScaleOld = Shader.PropertyToID("_FireScaleOld");
            public static readonly int _FireTempScaleOld = Shader.PropertyToID("_FireTempScaleOld");
            public static readonly int _Illum = Shader.PropertyToID("_Illum");
            public static readonly int _JitterDenoise = Shader.PropertyToID("_JitterDenoise");
            public static readonly int _JitterPyroFill = Shader.PropertyToID("_JitterPyroFill");
            public static readonly int _JitterScale = Shader.PropertyToID("_JitterScale");
            public static readonly int _JitterStrength = Shader.PropertyToID("_JitterStrength");
            public static readonly int _MultiScatter = Shader.PropertyToID("_MultiScatter");
            public static readonly int _OccCoordScale = Shader.PropertyToID("_OccCoordScale");
            public static readonly int _OccInvRes = Shader.PropertyToID("_OccInvRes");
            public static readonly int _OccTex = Shader.PropertyToID("_OccTex");
            public static readonly int _Opacity = Shader.PropertyToID("_Opacity");
            public static readonly int _PhaseG = Shader.PropertyToID("_PhaseG");
            public static readonly int _PyroCamFwd = Shader.PropertyToID("_PyroCamFwd");
            public static readonly int _PyroCamPos = Shader.PropertyToID("_PyroCamPos");
            public static readonly int _PyroCamRight = Shader.PropertyToID("_PyroCamRight");
            public static readonly int _PyroCamTanHalfFov = Shader.PropertyToID("_PyroCamTanHalfFov");
            public static readonly int _PyroCamUp = Shader.PropertyToID("_PyroCamUp");
            public static readonly int _PyroDepth = Shader.PropertyToID("_PyroDepth");
            public static readonly int _PyroOrtho = Shader.PropertyToID("_PyroOrtho");
            public static readonly int _PyroTime = Shader.PropertyToID("_PyroTime");
            public static readonly int _ScatterAmt = Shader.PropertyToID("_ScatterAmt");
            public static readonly int _SculptTex = Shader.PropertyToID("_SculptTex");
            public static readonly int _SelfShadowDepthScale = Shader.PropertyToID("_SelfShadowDepthScale");
            public static readonly int _SelfShadowStrength = Shader.PropertyToID("_SelfShadowStrength");
            public static readonly int _ShadowAmbientAmount = Shader.PropertyToID("_ShadowAmbientAmount");
            public static readonly int _ShadowInvRes = Shader.PropertyToID("_ShadowInvRes");
            public static readonly int _ShadowRect = Shader.PropertyToID("_ShadowRect");
            public static readonly int _ShadowTex = Shader.PropertyToID("_ShadowTex");
            public static readonly int _SimSize = Shader.PropertyToID("_SimSize");
            public static readonly int _SimToWorld = Shader.PropertyToID("_SimToWorld");
            public static readonly int _SliceCount = Shader.PropertyToID("_SliceCount");
            public static readonly int _SliceLayerMode = Shader.PropertyToID("_SliceLayerMode");
            public static readonly int _SliceStep = Shader.PropertyToID("_SliceStep");
            public static readonly int _SmokeBright = Shader.PropertyToID("_SmokeBright");
            public static readonly int _SmokeContrast = Shader.PropertyToID("_SmokeContrast");
            public static readonly int _SmokeDensityScale = Shader.PropertyToID("_SmokeDensityScale");
            public static readonly int _SmokeFill = Shader.PropertyToID("_SmokeFill");
            public static readonly int _SmokePreint = Shader.PropertyToID("_SmokePreint");
            public static readonly int _SourceFilterRadius = Shader.PropertyToID("_SourceFilterRadius");
            public static readonly int _SourceFilterStrength = Shader.PropertyToID("_SourceFilterStrength");
            public static readonly int _State = Shader.PropertyToID("_State");
            public static readonly int _StateDensityGain = Shader.PropertyToID("_StateDensityGain");
            public static readonly int _StateFireGain = Shader.PropertyToID("_StateFireGain");
            public static readonly int _SunColor = Shader.PropertyToID("_SunColor");
            public static readonly int _SunDirWorld = Shader.PropertyToID("_SunDirWorld");
            public static readonly int _UseOccupancy = Shader.PropertyToID("_UseOccupancy");
            public static readonly int _UseVelocityJitter = Shader.PropertyToID("_UseVelocityJitter");
            public static readonly int _VelocityJitterScroll = Shader.PropertyToID("_VelocityJitterScroll");
            public static readonly int _VelocityInvRes = Shader.PropertyToID("_VelocityInvRes");
            public static readonly int _VelocityResF = Shader.PropertyToID("_VelocityResF");
            public static readonly int _VelocityTex = Shader.PropertyToID("_VelocityTex");
            public static readonly int _VirtualFill = Shader.PropertyToID("_VirtualFill");
            public static readonly int _VirtualFillSharpness = Shader.PropertyToID("_VirtualFillSharpness");
            public static readonly int _VolumeRect = Shader.PropertyToID("_VolumeRect");
            public static readonly int _VolumeZ = Shader.PropertyToID("_VolumeZ");
            public static readonly int _SliceTargetSize = Shader.PropertyToID("_SliceTargetSize");
            public static readonly int _BrazeSliceSource = Shader.PropertyToID("_BrazeSliceSource");
            public static readonly int _WarpSamplePhase = Shader.PropertyToID("_WarpSamplePhase");
            public static readonly int _WarpVolume = Shader.PropertyToID("_WarpVolume");
            public static readonly int _WorldToSim = Shader.PropertyToID("_WorldToSim");
        }

        class BrazeVolumeSlicePass : ScriptableRenderPass
        {
            public int maxSliceCount;
            public float slicesPerVoxel;
            public float jitterStrength = 0.75f;
            public bool sceneView;
            public int downsample = 1;   // spatial working-resolution divisor; temporal mode still resolves offscreen
            public int heightCap;        // feature-owned target-height cap; 0 = uncapped
            public bool adaptiveMarching;
            public float adaptiveMaxStep = 4f;
            public float adaptiveFireMaxStep = 1f;
            public float adaptiveVariance = 0.35f;
            public bool compactHistory;

            Material _sliceMaterial;
            Material _compositeMaterial;
            int _configuredTemporalMode;
            int _cameraTemporalMode;
            float _temporalBlend;
            bool _configuredMotionReproject;
            readonly Dictionary<int, HistoryState> _histories = new();
            static readonly List<int> _staleHistoryKeys = new();
            int _lastHistorySweepFrame;

            readonly Vector3[] _corners = new Vector3[8];
            readonly List<BrazeContainer> _ready = new();

            public void SetupTemporal(
                int configuredTemporalMode, int cameraTemporalMode, float temporalBlend, bool motionReproject)
            {
                int configuredMode = Mathf.Clamp(configuredTemporalMode, 0, 2);
                if (TemporalSettingsRequireHistoryReset(
                        _configuredTemporalMode, _temporalBlend, _configuredMotionReproject,
                        configuredMode, temporalBlend, motionReproject))
                    ResetHistory();
                _configuredTemporalMode = configuredMode;
                // This may be Off for Scene View while the authored setting remains interleaved. Keeping the two
                // separate prevents Scene View from invalidating the Game camera's independent history every frame.
                _cameraTemporalMode = Mathf.Clamp(cameraTemporalMode, 0, 2);
                _temporalBlend = Mathf.Clamp(temporalBlend, 0f, 0.95f);
                _configuredMotionReproject = motionReproject;
            }

            sealed class HistoryState
            {
                public RTHandle resolveA;
                public RTHandle resolveB;
                public bool resolveFlip;
                public bool resolveValid;
                public Matrix4x4 prevVP;
                public float prevTime;
                public bool vpValid;
                public Vector3 cameraPosition;
                public Quaternion cameraRotation;
                public bool cameraPoseValid;
                public int contentSignature;
                public bool contentSignatureValid;
                public int lastUsedFrame;
                public int editFrameIndex;   // edit-mode phase clock: Time.frameCount is frozen without a player loop
            }

            void SweepStaleHistories()
            {
                int now = Time.frameCount;
                // Time.frameCount also restarts on play-mode entry; without the clamp the negative delta would
                // postpone sweeping for hundreds of frames, and entries stamped by the previous session would
                // never look old. A negative age likewise marks a cross-session entry: sweep it — a still-active
                // camera is simply re-created fresh on its next GetHistory, which matches the session-cut bootstrap.
                if (now < _lastHistorySweepFrame) _lastHistorySweepFrame = now;
                if (now - _lastHistorySweepFrame < 300) return;
                _lastHistorySweepFrame = now;
                _staleHistoryKeys.Clear();
                foreach (var kv in _histories)
                {
                    int age = now - kv.Value.lastUsedFrame;
                    if (age > 600 || age < 0) _staleHistoryKeys.Add(kv.Key);
                }
                for (int i = 0; i < _staleHistoryKeys.Count; i++)
                {
                    HistoryState state = _histories[_staleHistoryKeys[i]];
                    state.resolveA?.Release();
                    state.resolveB?.Release();
                    _histories.Remove(_staleHistoryKeys[i]);
                }
            }

            HistoryState GetHistory(Camera camera, RenderTextureDescriptor desc)
            {
                SweepStaleHistories();
                int id = camera != null ? camera.BrazeInstanceId() : 0;
                if (!_histories.TryGetValue(id, out HistoryState state))
                {
                    state = new HistoryState();
                    _histories.Add(id, state);
                }
                state.lastUsedFrame = Time.frameCount;
                bool changedA = RenderingUtils.ReAllocateHandleIfNeeded(
                    ref state.resolveA, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BrazeSliceResolveA");
                bool changedB = RenderingUtils.ReAllocateHandleIfNeeded(
                    ref state.resolveB, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BrazeSliceResolveB");
                if (changedA || changedB) state.resolveValid = false;
                return state;
            }

            public void ResetHistory()
            {
                foreach (HistoryState state in _histories.Values)
                    state.resolveValid = false;
            }

            class PassData
            {
                public Material sliceMaterial;
                public RenderTexture state;
                public RenderTexture fineTex;   // legacy fine field (null = fall back to base state)
                public bool useFine;
                public Vector3 fineInvRes;      // 1 / fine-field res, for the fine tap's own quintic de-facet
                public RenderTexture warpTex;   // restored Render Detail domain warp
                public bool useWarp;
                public float warpSamplePhase;
                public RenderTexture velocityTex;
                public bool useVelocityJitter;
                public bool velocityJitterScroll;
                public Vector3 velocityInvRes, velocityRes3;
                public Texture smokePreint, firePreint, fireCurve;
                public RenderTexture shadowTex;
                public RenderTexture sculptTex;   // baked Definition sculpt (null → _Definition forced 0)
                // Forward resolution matching the remaining fine-field inverse vector.
                public Vector3 fineRes3;
                public bool selfShadows;
                public RenderTexture occTex;
                public bool useOccupancy;
                public Vector3 occInvRes, occCoordScale;
                public bool adaptiveMarching;
                public float adaptiveMaxStep, adaptiveFireMaxStep, adaptiveVariance;
                public bool useFill;
                public int sliceCount;
                public float sliceStep;
                public Vector4 volumeRect;
                public Vector2 volumeZ;
                public Vector3 camPos, camRight, camUp, camFwd;
                public Vector2 tanHalfFov;
                public bool ortho;
                public Matrix4x4 worldToSim;
                public Matrix4x4 simToWorld;
                public Vector3 simSize;
                public Vector4 sunColor;
                public Vector3 sunDir;
                public Vector4 ambient;
                public float phaseG, edgeFade;
                public bool renderSmoke, renderFire;
                public float legacySmokeDensity, legacySmokeBright, legacySmokeContrast, legacyOpacity;
                public float legacyFireScale, legacyFireDensity, legacyFireHeat;
                public float legacyIllum, legacyScatter, legacyDefinition, legacyFireGlow;
                public Vector4 legacyFireLightColor;
                public float multiScatter;
                public float selfShadowStrength, selfShadowDepthScale;
                public float shadowAmbientAmount;
                public Vector4 smokeFill;
                public float time;
                public float sourceFilterStrength, sourceFilterRadius, densityContrast, depthSoftness;
                public float jitterStrength, jitterScale, jitterPyroFill, jitterDenoise;
                public float virtualFill, virtualFillSharpness;
                public float densityGain, fireGain;
                public int sliceLayerMode;
                public int debugView;
                public float cfdHeatMin, cfdHeatInvRange, cfdHeatOpacity;
                public Vector4 cfdHeatCold, cfdHeatMiddle, cfdHeatHot;
                public bool cfdSmokeTemperature, cfdSmokeFlowSpeed, cfdSmokeDiagnostic;
                public float cfdSmokeColorContrast;
                public float cfdSmokeFlowEmphasis;
                public bool temporal;
                public bool clearTargets;
                public bool fluidReproject;
                public Matrix4x4 prevVP;
                public float deltaTime;
                public int interleaveMode, interleavePhase, jitterSampleIndex;
                public Vector2 targetSize;   // pixel size of the attachment the march renders into
                public TextureHandle color, motion, depth;
            }

            // One-tap premultiplied composite for either a spatially downsampled march or resolved temporal history.
            class CompositeData
            {
                public Material material;
                public TextureHandle src;
                public bool compactHistory;
            }

            class ResolveData
            {
                public Material material;
                public TextureHandle current, motion;
                // Previous-frame history is bound as a plain texture, NOT routed through ImportTexture: an
                // imported handle that no pass writes within the current graph came back black when sampled
                // (verified with a beacon probe — same-frame reads worked, cross-frame reads were empty), so
                // the persistent RTHandle is read directly like a classic TAA history buffer. Only this frame's
                // WRITE target needs to be a graph resource (it is the pass's render attachment).
                public Texture history;
                public Vector4 texelSize;
                public float blend;
                public int interleaveMode, interleavePhase;
                public bool compactHistory;
                // Drives the encode's stochastic-rounding noise. Wrapped small so its float conversion stays
                // integer-exact over long editor sessions; the interleave phase cannot substitute (a tile is
                // always re-encoded at the same phase, freezing the dither it sees).
                public int ditherFrame;
            }

            bool EnsureResources()
            {
                if (_sliceMaterial != null)
                    return true;

                var shader = BrazeRuntimeShaderLibrary.Find("Hidden/BrazeFX/Slices");
                if (shader == null)
                    return false;

                _sliceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return true;
            }

            bool EnsureCompositeMaterial()
            {
                if (_compositeMaterial != null)
                    return true;
                var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/SliceComposite");
                if (shader == null)
                    return false;
                _compositeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                BrazeContainer.CollectSliceRenderers(_ready, cameraData.camera);
                if (_ready.Count == 0 || !EnsureResources())
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var lightData = frameData.Get<UniversalLightData>();
                Camera cam = cameraData.camera;

                TextureHandle color = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                if (!depth.IsValid())
                    return;

                // Temporal interleaving composes with the selected spatial working resolution: shade a rotating
                // subset there, flow-reconstruct history there, then use the existing camera composite/upscale.
                var camDesc = cameraData.cameraTargetDescriptor;
                // Every live/cache volume shares one premultiplied target and one explicit renderer-feature policy.
                // Container simulation presets and cache playback modes cannot silently resize it.
                int ds = Mathf.Clamp(downsample, 1, 4);
                int resolvedHeightCap = Mathf.Clamp(heightCap, 0, 2160);
                Vector2Int workingSize = ResolveWorkingResolution(
                    camDesc.width, camDesc.height, ds, resolvedHeightCap);

                bool temporal = _cameraTemporalMode != 0 && _temporalBlend > 0.001f &&
                                EnsureCompositeMaterial();
                bool reducedResolution =
                    workingSize.x != camDesc.width || workingSize.y != camDesc.height;
                bool offscreen = temporal || (reducedResolution && EnsureCompositeMaterial());
                BrazeSliceRuntimeDiagnostics.Begin(
                    cam,
                    camDesc,
                    workingSize,
                    downsample,
                    ds,
                    resolvedHeightCap,
                    _ready.Count,
                    maxSliceCount,
                    slicesPerVoxel,
                    _configuredTemporalMode,
                    _cameraTemporalMode,
                    temporal,
                    offscreen,
                    adaptiveMarching,
                    compactHistory);
                TextureHandle marchTarget = color;
                TextureHandle motionTarget = default;
                Vector2 targetSize = new Vector2(Mathf.Max(1, camDesc.width), Mathf.Max(1, camDesc.height));
                HistoryState historyState = null;
                Matrix4x4 prevVP = Matrix4x4.identity;
                int activeInterleave = 0;
                int interleavePhase = 0;
                int temporalFrameIndex = 0;
                float sliceDeltaTime = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);

                if (temporal)
                {
                    var temporalDesc = camDesc;
                    temporalDesc.width = workingSize.x;
                    temporalDesc.height = workingSize.y;
                    temporalDesc.depthBufferBits = 0;
                    temporalDesc.depthStencilFormat = GraphicsFormat.None;
                    temporalDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                    temporalDesc.msaaSamples = 1;
                    var historyDesc = temporalDesc;
                    historyDesc.graphicsFormat = ResolveHistoryFormat(compactHistory);
                    historyState = GetHistory(cam, historyDesc);

                    // Fluid velocity is local to each simulation grid. If a container is teleported/reparented or
                    // the active set changes, its old screen history has no valid object-motion transform: refresh.
                    int contentSignature = 17;
                    for (int i = 0; i < _ready.Count; i++)
                    {
                        BrazeContainer ready = _ready[i];
                        if (ready == null || !ready.SliceRenderReady) continue;
                        unchecked
                        {
                            contentSignature = contentSignature * 31 + ready.BrazeInstanceId();
                            contentSignature = contentSignature * 31 + ready.transform.localToWorldMatrix.GetHashCode();
                        }
                    }
                    if (historyState.contentSignatureValid && historyState.contentSignature != contentSignature)
                        historyState.resolveValid = false;
                    historyState.contentSignature = contentSignature;
                    historyState.contentSignatureValid = true;

                    Matrix4x4 curVP = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) *
                                      cameraData.GetViewMatrix();
                    prevVP = curVP;
                    float cameraTravel = 0f;
                    float cameraTurn = 0f;
                    // Edit mode freezes Time.time (no player loop) — the staleness gap would read 0 forever.
                    // realtimeSinceStartup advances in both modes; play mode keeps Time.time so the negative-gap
                    // session-boundary detection below behaves exactly as documented. A source switch across the
                    // edit/play boundary yields a negative or huge gap → the same clean bootstrap, by design.
                    float clockNow = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
                    float gap = historyState.vpValid ? clockNow - historyState.prevTime : float.PositiveInfinity;
                    // Time.time restarts at zero on every play-mode entry, so a state object that outlived the
                    // transition (camera instance IDs persist under Enter Play Mode Options) reports a NEGATIVE
                    // gap — which sailed past the > 0.25 staleness check and let the new session resolve against
                    // the dead session's history. Time running backwards is a session boundary: force the same
                    // clean bootstrap (interleave off + blend 0 for one frame) that a fresh state gets.
                    bool cameraCut = !historyState.cameraPoseValid || gap > 0.25f || gap < 0f;
                    if (cam != null && historyState.cameraPoseValid)
                    {
                        cameraTravel = Vector3.Distance(cam.transform.position, historyState.cameraPosition);
                        cameraTurn = Quaternion.Angle(cam.transform.rotation, historyState.cameraRotation);
                        cameraCut |= cameraTravel > 5f || cameraTurn > 45f;
                    }
                    if (cameraCut) historyState.resolveValid = false;
                    if (historyState.vpValid && !cameraCut) prevVP = historyState.prevVP;
                    historyState.prevVP = curVP;
                    historyState.prevTime = clockNow;
                    historyState.vpValid = true;
                    // Edit mode also freezes Time.deltaTime, which would zero the fluid-flow reprojection scale.
                    // This camera's own inter-render gap is the honest frame delta there; play mode keeps the
                    // render-frame delta captured above.
                    if (!Application.isPlaying)
                        sliceDeltaTime = float.IsFinite(gap) ? Mathf.Clamp(gap, 0f, 0.1f) : 0f;
                    if (cam != null)
                    {
                        historyState.cameraPosition = cam.transform.position;
                        historyState.cameraRotation = cam.transform.rotation;
                        historyState.cameraPoseValid = true;
                    }

                    activeInterleave = historyState.resolveValid ? _cameraTemporalMode : 0;
                    // Spend more current samples during motion so disocclusions never wait four frames.
                    if (activeInterleave == 2 && (cameraTravel > 0.25f || cameraTurn > 2f)) activeInterleave = 1;
                    if (activeInterleave != 0 && (cameraTravel > 1f || cameraTurn > 8f)) activeInterleave = 0;
                    // Edit mode never advances Time.frameCount, which would freeze the phase so only ONE tile
                    // subset ever re-marches (permanently stale neighbours). Count this camera's own temporal
                    // renders instead — also immune to several editor views rendering in one editor frame.
                    // Play mode keeps the global counter (unchanged behavior).
                    historyState.editFrameIndex++;
                    temporalFrameIndex = Application.isPlaying ? Time.frameCount : historyState.editFrameIndex;
                    interleavePhase = InterleavePhaseForFrame(activeInterleave, temporalFrameIndex);

                    marchTarget = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph, temporalDesc, "_BrazeSliceCurrent", false, FilterMode.Bilinear);
                    var motionDesc = temporalDesc;
                    motionDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                    motionTarget = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph, motionDesc, "_BrazeSliceMotion", false, FilterMode.Bilinear);
                    targetSize = new Vector2(temporalDesc.width, temporalDesc.height);
                }
                else if (offscreen)
                {
                    // Built directly (the URP CreateRenderGraphTexture overload taking a clear COLOR is
                    // internal): cleared to ZERO coverage (premultiplied rgb 0, opacity 0) — mandatory, the
                    // buffer accumulates volumes with the same One OneMinusSrcAlpha blend the direct path
                    // uses against the screen, and a nonzero clear alpha would darken everything under it.
                    int loW = workingSize.x;
                    int loH = workingSize.y;
                    var lowDesc = new TextureDesc(loW, loH)
                    {
                        format = GraphicsFormat.R16G16B16A16_SFloat,
                        clearBuffer = true,
                        clearColor = Color.clear,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        name = "BrazeFX Slice LowRes",
                        msaaSamples = MSAASamples.None,
                    };
                    marchTarget = renderGraph.CreateTexture(lowDesc);
                    targetSize = new Vector2(loW, loH);
                }
                BrazeSliceRuntimeDiagnostics.SetTemporal(activeInterleave, interleavePhase);
                bool anyDrawn = false;

                // One raster pass per ready volume — they composite in enqueue order through the shader's hardware
                // blend, so multiple live containers and/or duplicated cache players all draw. (Per-fragment depth
                // sorting BETWEEN volumes is not resolved — same one-layer limitation as the old registry path.)
                for (int si = 0; si < _ready.Count; si++)
                {
                    var sim = _ready[si];
                    if (!sim.SliceRenderReady)
                        continue;
                    if (!ComputeFit(cam, sim, out Vector4 volumeRect, out Vector2 volumeZ))
                        continue;

                    using var builder = renderGraph.AddRasterRenderPass<PassData>("BrazeFX Volume Slices", out PassData d);

                    var cfdVisualization = sim.CfdVisualization;
                    bool cfdSmoke = cfdVisualization != null && cfdVisualization.SmokeEnabled;
                    d.cfdSmokeTemperature = cfdSmoke && cfdVisualization.SmokeTemperatureColor;
                    d.cfdSmokeFlowSpeed = cfdSmoke && cfdVisualization.SmokeFlowSpeedColor;
                    d.cfdSmokeColorContrast = cfdSmoke ? cfdVisualization.SmokeColorContrast : 1f;
                    d.cfdSmokeFlowEmphasis = d.cfdSmokeFlowSpeed ? cfdVisualization.SmokeFlowEmphasis : 0f;
                    d.cfdSmokeDiagnostic = cfdSmoke;
                    d.sliceMaterial = _sliceMaterial;
                    d.state = sim.SliceRenderStateTexture;
                    d.fineTex = sim.SliceFineTexture;
                    d.useFine = !cfdSmoke && d.fineTex != null;
                    d.fineInvRes = d.useFine
                        ? new Vector3(1f / Mathf.Max(1, d.fineTex.width),
                                      1f / Mathf.Max(1, d.fineTex.height),
                                      1f / Mathf.Max(1, d.fineTex.volumeDepth))
                        : Vector3.one;
                    d.warpTex = sim.SliceWarpTexture;
                    d.useWarp = !cfdSmoke && d.warpTex != null;
                    d.warpSamplePhase = d.useWarp ? sim.SliceWarpSamplePhase : 0f;
                    d.velocityTex = sim.FluidVelocity;
                    d.useVelocityJitter = !cfdSmoke && d.velocityTex != null;
                    d.velocityJitterScroll = d.useVelocityJitter && sim.SliceVelocityJitterScroll;
                    d.velocityInvRes = d.useVelocityJitter
                        ? new Vector3(1f / Mathf.Max(1, d.velocityTex.width),
                                      1f / Mathf.Max(1, d.velocityTex.height),
                                      1f / Mathf.Max(1, d.velocityTex.volumeDepth))
                        : Vector3.one;
                    d.smokePreint = cfdSmoke ? cfdVisualization.SmokePreint : sim.SliceSmokePreint;
                    d.firePreint = cfdSmoke ? d.smokePreint : sim.SliceFirePreint;
                    d.fireCurve = cfdSmoke ? Texture2D.whiteTexture : sim.SliceFireCurveLut;
                    d.shadowTex = sim.SliceShadowTexture;
                    d.sculptTex = sim.SliceSculptTexture;
                    d.selfShadows = !cfdSmoke && sim.bakeLighting && sim.sliceSelfShadows && d.shadowTex != null;

                    d.occTex = sim.SliceOccupancyTexture;
                    d.useOccupancy = CanUseOccupancyForVolume(d.occTex != null, d.useWarp);
                    d.occInvRes = d.useOccupancy
                        ? new Vector3(1f / Mathf.Max(1, d.occTex.width),
                                      1f / Mathf.Max(1, d.occTex.height),
                                      1f / Mathf.Max(1, d.occTex.volumeDepth))
                        : Vector3.one;
                    float brickSize = Mathf.Max(1, sim.SolverBrickSize);
                    d.occCoordScale = new Vector3(d.state.width / brickSize,
                                                  d.state.height / brickSize,
                                                  d.state.volumeDepth / brickSize);
                    // Both live and cache hosts expose the same density/fire importance payload. CFD diagnostic
                    // colour also depends on velocity/temperature, which that payload cannot safely classify as
                    // smooth, so CFD retains empty-space skipping but uses the exact stride inside occupied bricks.
                    d.adaptiveMarching = CanUseAdaptiveMarchingForVolume(
                        adaptiveMarching, d.useOccupancy, cfdSmoke);
                    d.adaptiveVariance = adaptiveVariance;
                    BrazeContainer.SliceVirtualFillView fillView = sim.sliceVirtualFill
                        ? sim.sliceVirtualFillView
                        : BrazeContainer.SliceVirtualFillView.Slices;
                    d.sliceLayerMode = (int)fillView;
                    d.useFill = !cfdSmoke && sim.renderSmoke && sim.sliceVirtualFill &&
                                fillView != BrazeContainer.SliceVirtualFillView.Slices;

                    // Stable slice stride: hold sampling density to the sampled render-grid voxel pitch, but pass
                    // it as a fixed step. The shader uses sliceCount only as a cap; normalizing every ray to the
                    // camera-facing depth span rephases the volume during orbit and reads as swimming smoke/fire.
                    // The restored Render Detail path is a coordinate warp over this field, not a finer scalar grid.
                    Vector3 ws = sim.WorldSize();
                    RenderTexture sampledGrid = d.useFine ? d.fineTex : d.state;
                    float minVoxel = Mathf.Min(
                        ws.x / Mathf.Max(1, sampledGrid.width),
                        Mathf.Min(ws.y / Mathf.Max(1, sampledGrid.height),
                                  ws.z / Mathf.Max(1, sampledGrid.volumeDepth)));
                    float maxVoxel = Mathf.Max(
                        ws.x / Mathf.Max(1, sampledGrid.width),
                        Mathf.Max(ws.y / Mathf.Max(1, sampledGrid.height),
                                  ws.z / Mathf.Max(1, sampledGrid.volumeDepth)));
                    float localSamplesPerVoxel = Mathf.Clamp(slicesPerVoxel, 0.25f, 2f);
                    int localMaxSteps = ResolveSliceStepBudget(maxSliceCount);
                    float desiredStep = Mathf.Max(minVoxel / Mathf.Max(localSamplesPerVoxel, 0.1f), 1e-4f);
                    float fitSpan = Mathf.Max(volumeZ.y, desiredStep);
                    int adaptive = Mathf.CeilToInt(fitSpan / desiredStep) + 1;
                    if (adaptive > localMaxSteps)
                    {
                        d.sliceCount = localMaxSteps;
                        d.sliceStep = fitSpan / Mathf.Max(1, localMaxSteps - 1);
                    }
                    else
                    {
                        d.sliceCount = Mathf.Max(8, adaptive);
                        d.sliceStep = desiredStep;
                    }
                    float stepScale = d.sliceStep / Mathf.Max(desiredStep, 1e-5f);
                    float localJitterStrength = ResolveVolumeJitterStrength(jitterStrength, cfdSmoke);
                    BrazeSliceRuntimeDiagnostics.RecordContainer(
                        d.sliceCount,
                        localSamplesPerVoxel,
                        stepScale,
                        localJitterStrength,
                        sim.sliceJitterScale,
                        sim.sliceSourceFilterStrength,
                        sampledGrid.width,
                        sampledGrid.height,
                        sampledGrid.volumeDepth);
                    // A hard step-budget cap may already stretch the base stride. Do not multiply that stretch by
                    // another full adaptive leap, which can skip flame boundaries when the safety budget is tight.
                    d.adaptiveMaxStep = ResolveAdaptiveMaxStep(adaptiveMaxStep, stepScale);
                    d.adaptiveFireMaxStep = ResolveAdaptiveMaxStep(adaptiveFireMaxStep, stepScale);
                    d.virtualFill = d.useFill && fillView == BrazeContainer.SliceVirtualFillView.GapFill
                        ? Mathf.Clamp01(sim.sliceVirtualFillStrength)
                        : d.useFill
                        ? Mathf.Clamp01((stepScale - 1f) * 3f) * Mathf.Clamp01(sim.sliceVirtualFillStrength)
                        : 0f;
                    d.virtualFillSharpness = Mathf.Clamp01(sim.sliceVirtualFillSharpness);
                    // Forward resolutions, mirroring each InvRes fallback (Vector3.one ↔ invRes one).
                    d.fineRes3 = d.useFine
                        ? new Vector3(Mathf.Max(1, d.fineTex.width), Mathf.Max(1, d.fineTex.height), Mathf.Max(1, d.fineTex.volumeDepth))
                        : Vector3.one;
                    d.velocityRes3 = d.useVelocityJitter
                        ? new Vector3(Mathf.Max(1, d.velocityTex.width), Mathf.Max(1, d.velocityTex.height), Mathf.Max(1, d.velocityTex.volumeDepth))
                        : Vector3.one;
                    d.volumeRect = volumeRect;
                    d.volumeZ = volumeZ;

                    Transform ct = cam.transform;
                    d.camPos = ct.position;
                    d.camRight = ct.right;
                    d.camUp = ct.up;
                    d.camFwd = ct.forward;
                    var proj = cam.projectionMatrix;
                    // Perspective: 1/m00 = tan(halfFovX). Orthographic: 1/m00 = world
                    // half-width (and 1/m11 = half-height) -- the same slots carry the
                    // right numbers for both; the shader branches on _PyroOrtho.
                    d.tanHalfFov = new Vector2(1f / proj.m00, 1f / proj.m11);
                    d.ortho = cam.orthographic;

                    d.worldToSim = sim.SliceWorldToSimUVW;
                    d.simToWorld = d.worldToSim.inverse;
                    d.simSize = ws;

                    int mainIdx = lightData.mainLightIndex;
                    if (sim.TryGetCacheKeyLight(out Vector3 cacheLightDir, out Color cacheLightColor))
                    {
                        d.sunColor = cacheLightColor;
                        d.sunDir = cacheLightDir;
                    }
                    else if (mainIdx >= 0 && mainIdx < lightData.visibleLights.Length)
                    {
                        var vl = lightData.visibleLights[mainIdx];
                        d.sunColor = vl.finalColor;
                        d.sunDir = -(Vector3)vl.localToWorldMatrix.GetColumn(2);
                    }
                    else
                    {
                        d.sunColor = new Color(1f, 0.96f, 0.9f, 1f);
                        d.sunDir = sim.SliceMainLightDirection();
                    }

                    // (A per-frame RenderSettings.ambientProbe.Evaluate used to run here; its result was never
                    // read — the volume's ambient comes solely from the container's own colour × intensity.)
                    var amb = sim.ambientColor.linear * Mathf.Max(0f, sim.ambientIntensity);
                    d.ambient = new Vector4(Mathf.Max(amb.r, 0f), Mathf.Max(amb.g, 0f), Mathf.Max(amb.b, 0f), 0f);

                    d.phaseG = sim.slicePhaseG;
                    d.renderSmoke = cfdSmoke || sim.renderSmoke;
                    d.renderFire = !cfdSmoke && sim.renderFire;
                    d.legacySmokeDensity = cfdSmoke ? 4f : sim.densityScale;
                    d.legacySmokeBright = cfdSmoke ? cfdVisualization.SmokeBrightness : sim.smokeBrightness;
                    d.legacySmokeContrast = sim.smokeContrast;
                    d.legacyOpacity = cfdSmoke ? cfdVisualization.SmokeOpacity : sim.opacity;
                    d.legacyFireScale = sim.fireScale;
                    d.legacyFireDensity = sim.fireDensityScale;
                    d.legacyFireHeat = sim.fireTempScale;
                    d.legacyIllum = cfdSmoke ? 1f : 0.6f + sim.illumination * 0.6f;
                    d.legacyScatter = !cfdSmoke && sim.bakeLighting ? sim.scattering : 0f;
                    // Definition needs the baked sculpt volume — force 0 when it is absent so the shader's
                    // sculpt branch (and its _SculptTex sample) never runs against an unbound texture.
                    d.legacyDefinition = (!cfdSmoke && sim.bakeLighting && d.sculptTex != null) ? sim.definition : 0f;
                    d.legacyFireGlow = !cfdSmoke && sim.fireLightSmoke ? Mathf.Max(0f, sim.fireLightIntensity) : 0f;
                    Color fireLightCol = sim.fireLightColor.linear * sim.fireLightTint.linear;
                    d.legacyFireLightColor = new Vector4(Mathf.Max(0f, fireLightCol.r), Mathf.Max(0f, fireLightCol.g), Mathf.Max(0f, fireLightCol.b), 0f);
                    d.edgeFade = sim.sliceEdgeFade;
                    d.multiScatter = cfdSmoke ? 0f : sim.sliceMultiScatter;
                    d.selfShadowStrength = sim.sliceSelfShadowStrength;
                    d.shadowAmbientAmount = sim.sliceShadowAmbientAmount;
                    d.selfShadowDepthScale = sim.sliceSelfShadowDepthScale;
                    Color fill = sim.sliceSmokeFillColor.linear * Mathf.Max(0f, sim.sliceSmokeFillStrength);
                    d.smokeFill = cfdSmoke ? Vector4.zero : new Vector4(fill.r, fill.g, fill.b, 0f);
                    // Live edit-mode simulation advances outside the player loop, where Time.time is frozen.
                    // The container supplies its Timeline-driven clock there and ordinary Unity time elsewhere.
                    d.time = sim.SliceAnimationTime;
                    d.sourceFilterStrength = cfdSmoke ? 0f : sim.sliceSourceFilterStrength;
                    d.sourceFilterRadius = sim.sliceSourceFilterRadius;
                    d.densityContrast = cfdSmoke ? 0.78f : sim.sliceDensityContrast;
                    d.depthSoftness = ResolveDepthSoftness(
                        cfdSmoke, sim.sliceDepthSoftness, maxVoxel);
                    d.jitterStrength = localJitterStrength;
                    d.jitterScale = sim.sliceJitterScale;
                    d.jitterPyroFill = sim.sliceJitterSceneBlend;
                    d.jitterDenoise = sim.sliceJitterDenoise;
                    d.densityGain = sim.sliceDensityGain;
                    d.fireGain = sim.sliceFireGain;
                    var heatMap = cfdSmoke ? null : sim.CfdHeatMap;
                    d.debugView = heatMap != null ? 7 : (int)sim.sliceRenderDebugView;
                    if (d.cfdSmokeTemperature)
                    {
                        d.cfdHeatMin = cfdVisualization.SmokeHeatMinimum;
                        d.cfdHeatInvRange = cfdVisualization.SmokeHeatInverseRange;
                        d.cfdHeatOpacity = 1f;
                        d.cfdHeatCold = new Color(0.02f, 0.52f, 1f, 1f).linear;
                        d.cfdHeatMiddle = new Color(0.03f, 1f, 0.28f, 1f).linear;
                        d.cfdHeatHot = new Color(1f, 0.16f, 0.008f, 1f).linear;
                    }
                    else if (heatMap != null)
                    {
                        var domain = sim.CfdDomain;
                        float unitsPerDegree = domain != null ? domain.HeatUnitsPerDegree : 1f;
                        float ambient = domain != null ? domain.ambientTemperatureC : 0f;
                        float minHeat = (heatMap.minimumTemperatureC - ambient) * unitsPerDegree;
                        float maxHeat = (heatMap.maximumTemperatureC - ambient) * unitsPerDegree;
                        d.cfdHeatMin = minHeat;
                        d.cfdHeatInvRange = 1f / Mathf.Max(maxHeat - minHeat, 1e-4f);
                        d.cfdHeatOpacity = heatMap.opacity;
                        d.cfdHeatCold = heatMap.coldColor.linear;
                        d.cfdHeatMiddle = heatMap.middleColor.linear;
                        d.cfdHeatHot = heatMap.hotColor.linear;
                    }
                    d.temporal = temporal;
                    d.clearTargets = temporal && !anyDrawn;
                    d.fluidReproject = temporal && d.useVelocityJitter;
                    d.prevVP = prevVP;
                    d.deltaTime = sliceDeltaTime;
                    d.interleaveMode = activeInterleave;
                    d.interleavePhase = interleavePhase;
                    d.jitterSampleIndex = JitterSequenceForFrame(activeInterleave, temporalFrameIndex);

                    d.color = marchTarget;
                    d.motion = motionTarget;
                    d.depth = depth;
                    d.targetSize = targetSize;

                    builder.UseTexture(d.depth, AccessFlags.Read);
                    builder.SetRenderAttachment(d.color, 0, AccessFlags.ReadWrite);
                    if (d.temporal)
                        builder.SetRenderAttachment(d.motion, 1, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<PassData>(static (data, ctx) => Execute(data, ctx));
                    anyDrawn = true;
                }

                if (temporal && anyDrawn)
                {
                    RTHandle previous = historyState.resolveFlip ? historyState.resolveB : historyState.resolveA;
                    RTHandle next = historyState.resolveFlip ? historyState.resolveA : historyState.resolveB;
                    // No import (and no clear) for `previous`: it is sampled directly as a plain texture below.
                    // When history is invalid the resolve runs with blend 0 and returns before ever sampling it,
                    // so first-frame garbage in a fresh RTHandle is unreachable.
                    TextureHandle nextHistory = renderGraph.ImportTexture(next);

                    using (var builder = renderGraph.AddRasterRenderPass<ResolveData>(
                               "BrazeFX Slice Flow Temporal Resolve", out ResolveData rd))
                    {
                        rd.material = _compositeMaterial;
                        rd.current = marchTarget;
                        rd.history = previous.rt;
                        rd.motion = motionTarget;
                        rd.texelSize = new Vector4(1f / Mathf.Max(targetSize.x, 1f),
                                                   1f / Mathf.Max(targetSize.y, 1f),
                                                   targetSize.x, targetSize.y);
                        rd.blend = historyState.resolveValid ? _temporalBlend : 0f;
                        rd.interleaveMode = activeInterleave;
                        rd.interleavePhase = interleavePhase;
                        rd.compactHistory = compactHistory;
                        // renderedFrameCount, not frameCount: the dither must keep animating for edit-mode Game
                        // view repaints, or stochastic rounding degenerates back into the fade-stall it prevents.
                        rd.ditherFrame = Time.renderedFrameCount & 255;
                        builder.UseTexture(rd.current, AccessFlags.Read);
                        builder.UseTexture(rd.motion, AccessFlags.Read);
                        builder.SetRenderAttachment(nextHistory, 0, AccessFlags.Write);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc<ResolveData>(static (data, ctx) =>
                        {
                            var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                            props.SetTexture(SliceShaderIds._BrazeVolumeCurrent, data.current);
                            props.SetTexture(SliceShaderIds._BrazeVolumeHistory, data.history);
                            props.SetTexture(SliceShaderIds._BrazeVolumeMotion, data.motion);
                            props.SetVector(SliceShaderIds._BrazeVolumeTexelSize, data.texelSize);
                            props.SetFloat(SliceShaderIds._BrazeTemporalBlend, data.blend);
                            props.SetFloat(SliceShaderIds._BrazeReproject, 1f);
                            props.SetInt(SliceShaderIds._BrazeInterleaveMode, data.interleaveMode);
                            props.SetInt(SliceShaderIds._BrazeInterleavePhase, data.interleavePhase);
                            props.SetFloat(SliceShaderIds._BrazeCompactHistory, data.compactHistory ? 1f : 0f);
                            props.SetFloat(SliceShaderIds._BrazeHistoryDitherFrame, data.ditherFrame);
                            ctx.cmd.DrawProcedural(
                                Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 1, props);
                        });
                    }

                    marchTarget = nextHistory;
                    historyState.resolveFlip = !historyState.resolveFlip;
                    historyState.resolveValid = true;
                }
                else if (temporal)
                {
                    historyState.resolveValid = false;
                }

                // Composite the spatially upsampled or temporally reconstructed premultiplied volume.
                if (offscreen && anyDrawn)
                {
                    using var builder = renderGraph.AddRasterRenderPass<CompositeData>(
                        "BrazeFX Slice Composite", out CompositeData cd);
                    cd.material = _compositeMaterial;
                    cd.src = marchTarget;
                    cd.compactHistory = temporal && compactHistory;
                    builder.UseTexture(cd.src, AccessFlags.Read);
                    builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<CompositeData>(static (data, ctx) =>
                    {
                        var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                        props.SetTexture(SliceShaderIds._BrazeSliceSource, data.src);
                        props.SetFloat(SliceShaderIds._BrazeCompactHistory, data.compactHistory ? 1f : 0f);
                        ctx.cmd.DrawProcedural(
                            Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, props);
                    });
                }
            }

            // Feature keywords (variant selection — see the multi_compile block in BrazeSlices.shader): the
            // debug/fine/warp/Definition paths no longer cost register pressure in variants that don't use them.
            static void SetKeyword(Material m, string kw, bool on)
            {
                if (on) m.EnableKeyword(kw);
                else if (m.IsKeywordEnabled(kw)) m.DisableKeyword(kw);
            }

            static void Execute(PassData d, RasterGraphContext ctx)
            {
                RasterCommandBuffer cmd = ctx.cmd;
                var mat = d.sliceMaterial;

                if (d.clearTargets)
                    cmd.ClearRenderTarget(false, true, Color.clear);

                SetKeyword(mat, "_BRAZE_USE_FINE", d.useFine);
                SetKeyword(mat, "_BRAZE_USE_WARP", d.useWarp);
                SetKeyword(mat, "_BRAZE_DEFINITION", d.legacyDefinition > 1e-4f && d.sculptTex != null);
                SetKeyword(mat, "_BRAZE_DEBUG_VIEW", d.debugView > 0);
                SetKeyword(mat, "_BRAZE_SMOKE", d.renderSmoke);
                SetKeyword(mat, "_BRAZE_FIRE", d.renderFire && d.sliceLayerMode != (int)BrazeContainer.SliceVirtualFillView.GapFill);
                SetKeyword(mat, "_BRAZE_SELF_SHADOW", d.selfShadows);
                SetKeyword(mat, "_BRAZE_FILL", d.useFill);

                mat.SetTexture(SliceShaderIds._State, d.state);
                // Fine/warp path selection lives in the _BRAZE_* keywords now; keep the samplers valid in
                // every variant (fallback-bind the state) so no variant ever reads an unbound texture.
                mat.SetTexture(SliceShaderIds._FineVolume, d.useFine ? d.fineTex : d.state);
                mat.SetVector(SliceShaderIds._FineInvRes, d.fineInvRes);
                mat.SetTexture(SliceShaderIds._WarpVolume, d.useWarp ? d.warpTex : d.state);
                mat.SetFloat(SliceShaderIds._WarpSamplePhase, d.warpSamplePhase);
                if (d.useVelocityJitter)
                    mat.SetTexture(SliceShaderIds._VelocityTex, d.velocityTex);
                mat.SetFloat(SliceShaderIds._UseVelocityJitter, d.useVelocityJitter ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._VelocityJitterScroll, d.velocityJitterScroll ? 1f : 0f);
                mat.SetVector(SliceShaderIds._VelocityInvRes, d.velocityInvRes);
                if (d.smokePreint != null)
                    mat.SetTexture(SliceShaderIds._SmokePreint, d.smokePreint);
                if (d.firePreint != null)
                    mat.SetTexture(SliceShaderIds._FirePreint, d.firePreint);
                if (d.fireCurve != null)
                    mat.SetTexture(SliceShaderIds._FireCurve, d.fireCurve);
                mat.SetFloat(SliceShaderIds._SmokeDensityScale, Mathf.Max(0f, d.legacySmokeDensity));
                mat.SetFloat(SliceShaderIds._SmokeBright, Mathf.Max(0f, d.legacySmokeBright));
                mat.SetFloat(SliceShaderIds._SmokeContrast, Mathf.Max(0.01f, d.legacySmokeContrast));
                mat.SetFloat(SliceShaderIds._Opacity, Mathf.Clamp01(d.legacyOpacity));
                mat.SetFloat(SliceShaderIds._FireScaleOld, Mathf.Max(0f, d.legacyFireScale));
                mat.SetFloat(SliceShaderIds._FireDensityScale, Mathf.Max(0f, d.legacyFireDensity));
                mat.SetFloat(SliceShaderIds._FireTempScaleOld, Mathf.Clamp01(d.legacyFireHeat));
                mat.SetFloat(SliceShaderIds._Illum, Mathf.Max(0f, d.legacyIllum));
                mat.SetFloat(SliceShaderIds._ScatterAmt, Mathf.Max(0f, d.legacyScatter));
                mat.SetFloat(SliceShaderIds._Definition, Mathf.Max(0f, d.legacyDefinition));
                if (d.sculptTex != null)
                    mat.SetTexture(SliceShaderIds._SculptTex, d.sculptTex);
                mat.SetFloat(SliceShaderIds._FireGlow, Mathf.Max(0f, d.legacyFireGlow));
                mat.SetVector(SliceShaderIds._FireLightColor, d.legacyFireLightColor);
                mat.SetTexture(SliceShaderIds._PyroDepth, d.depth);
                if (d.selfShadows)
                    mat.SetTexture(SliceShaderIds._ShadowTex, d.shadowTex);
                mat.SetFloat(SliceShaderIds._UseOccupancy, d.useOccupancy ? 1f : 0f);
                if (d.useOccupancy)
                    mat.SetTexture(SliceShaderIds._OccTex, d.occTex);
                mat.SetVector(SliceShaderIds._OccInvRes, d.occInvRes);
                mat.SetVector(SliceShaderIds._OccCoordScale, d.occCoordScale);
                mat.SetFloat(SliceShaderIds._AdaptiveMarching, d.adaptiveMarching ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._AdaptiveMaxStep, Mathf.Clamp(d.adaptiveMaxStep, 1f, 4f));
                mat.SetFloat(SliceShaderIds._AdaptiveFireMaxStep, Mathf.Clamp(d.adaptiveFireMaxStep, 1f, 2f));
                mat.SetFloat(SliceShaderIds._AdaptiveVarianceThreshold, Mathf.Clamp(d.adaptiveVariance, 0.1f, 1f));
                mat.SetFloat(SliceShaderIds._VirtualFill, Mathf.Clamp01(d.virtualFill));
                mat.SetFloat(SliceShaderIds._VirtualFillSharpness, Mathf.Clamp01(d.virtualFillSharpness));
                mat.SetInt(SliceShaderIds._SliceCount, d.sliceCount);
                mat.SetInt(SliceShaderIds._SliceLayerMode, d.sliceLayerMode);
                mat.SetFloat(SliceShaderIds._SliceStep, Mathf.Max(1e-5f, d.sliceStep));
                mat.SetVector(SliceShaderIds._VolumeRect, d.volumeRect);
                mat.SetVector(SliceShaderIds._VolumeZ, d.volumeZ);
                mat.SetVector(SliceShaderIds._SliceTargetSize, new Vector4(d.targetSize.x, d.targetSize.y, 0f, 0f));
                mat.SetVector(SliceShaderIds._FineResF, d.fineRes3);
                mat.SetVector(SliceShaderIds._VelocityResF, d.velocityRes3);
                mat.SetVector(SliceShaderIds._PyroCamPos, d.camPos);
                mat.SetVector(SliceShaderIds._PyroCamRight, d.camRight);
                mat.SetVector(SliceShaderIds._PyroCamUp, d.camUp);
                mat.SetVector(SliceShaderIds._PyroCamFwd, d.camFwd);
                mat.SetVector(SliceShaderIds._PyroCamTanHalfFov, d.tanHalfFov);
                mat.SetFloat(SliceShaderIds._PyroOrtho, d.ortho ? 1f : 0f);
                mat.SetMatrix(SliceShaderIds._WorldToSim, d.worldToSim);
                mat.SetMatrix(SliceShaderIds._SimToWorld, d.simToWorld);
                mat.SetVector(SliceShaderIds._SunColor, d.sunColor);
                mat.SetVector(SliceShaderIds._SunDirWorld, d.sunDir);
                mat.SetVector(SliceShaderIds._AmbientColor, d.ambient);
                mat.SetFloat(SliceShaderIds._PhaseG, d.phaseG);
                mat.SetFloat(SliceShaderIds._EdgeFade, d.edgeFade);
                mat.SetFloat(SliceShaderIds._MultiScatter, d.multiScatter);
                mat.SetFloat(SliceShaderIds._SelfShadowStrength, d.selfShadowStrength);
                mat.SetFloat(SliceShaderIds._ShadowAmbientAmount, d.shadowAmbientAmount);
                mat.SetFloat(SliceShaderIds._SelfShadowDepthScale, d.selfShadowDepthScale);
                mat.SetVector(SliceShaderIds._SmokeFill, d.smokeFill);
                mat.SetFloat(SliceShaderIds._PyroTime, d.time);
                mat.SetVector(SliceShaderIds._SimSize, d.simSize);
                // Fine-field voxel breakup is gated in the shader to hot/dense bodies only. It stays one-tap by
                // jittering the reconstruction point in object space instead of filtering neighbour samples.
                mat.SetFloat(SliceShaderIds._SourceFilterStrength, Mathf.Clamp01(d.sourceFilterStrength));
                mat.SetFloat(SliceShaderIds._SourceFilterRadius, Mathf.Clamp(d.sourceFilterRadius, 0.5f, 2.5f));
                mat.SetFloat(SliceShaderIds._DensityContrast, Mathf.Max(0.5f, d.densityContrast));
                mat.SetFloat(SliceShaderIds._DepthSoftness, Mathf.Max(1e-3f, d.depthSoftness));
                mat.SetFloat(SliceShaderIds._JitterStrength, Mathf.Clamp01(d.jitterStrength));
                mat.SetFloat(SliceShaderIds._JitterScale, ResolveJitterScale(d.jitterScale));
                mat.SetFloat(SliceShaderIds._JitterPyroFill, Mathf.Clamp01(d.jitterPyroFill));
                mat.SetFloat(SliceShaderIds._JitterDenoise, Mathf.Clamp01(d.jitterDenoise));
                mat.SetFloat(SliceShaderIds._StateDensityGain, Mathf.Max(0f, d.densityGain));
                mat.SetFloat(SliceShaderIds._StateFireGain, Mathf.Max(0f, d.fireGain));
                mat.SetFloat(SliceShaderIds._DebugView, d.debugView);
                mat.SetFloat(SliceShaderIds._CfdSmokeTemperature, d.cfdSmokeTemperature ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._CfdSmokeFlowSpeed, d.cfdSmokeFlowSpeed ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._CfdSmokeColorContrast,
                    Mathf.Clamp(d.cfdSmokeColorContrast, 0.5f, 6f));
                mat.SetFloat(SliceShaderIds._CfdSmokeFlowEmphasis, Mathf.Clamp01(d.cfdSmokeFlowEmphasis));
                mat.SetFloat(SliceShaderIds._CfdSmokeDiagnostic, d.cfdSmokeDiagnostic ? 1f : 0f);
                if (d.debugView == 7 || d.cfdSmokeTemperature)
                {
                    mat.SetFloat(SliceShaderIds._CfdHeatMin, d.cfdHeatMin);
                    mat.SetFloat(SliceShaderIds._CfdHeatInvRange, d.cfdHeatInvRange);
                    mat.SetFloat(SliceShaderIds._CfdHeatOpacity, Mathf.Clamp01(d.cfdHeatOpacity));
                    mat.SetVector(SliceShaderIds._CfdHeatCold, d.cfdHeatCold);
                    mat.SetVector(SliceShaderIds._CfdHeatMiddle, d.cfdHeatMiddle);
                    mat.SetVector(SliceShaderIds._CfdHeatHot, d.cfdHeatHot);
                }
                mat.SetMatrix(SliceShaderIds._BrazePrevVP, d.prevVP);
                mat.SetFloat(SliceShaderIds._BrazeReproject, d.temporal ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._BrazeFluidReprojection, d.fluidReproject ? 1f : 0f);
                mat.SetFloat(SliceShaderIds._BrazeSliceDeltaTime, Mathf.Clamp(d.deltaTime, 0f, 0.1f));
                mat.SetInt(SliceShaderIds._BrazeInterleaveMode, d.interleaveMode);
                mat.SetInt(SliceShaderIds._BrazeInterleavePhase, d.interleavePhase);
                mat.SetInt(SliceShaderIds._BrazeJitterSampleIndex, d.jitterSampleIndex);

                // 6 verts = a quad fitted to the volume's screen rect (see Vert),
                // drawn ONCE. The fragment raymarches the volume internally with
                // early ray termination; _SliceCount is the per-pixel step budget.
                cmd.DrawProcedural(Matrix4x4.identity, mat, d.temporal ? 1 : 0, MeshTopology.Triangles, 6, 1);
            }

            /// <summary>
            /// Fits the projected container to a screen rectangle and camera-depth range.
            /// Returns false when the container is not visible.
            /// </summary>
            bool ComputeFit(Camera cam, BrazeContainer sim, out Vector4 rect, out Vector2 zRange)
            {
                rect = default;
                zRange = default;
                sim.GetSliceWorldCorners(_corners);

                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 vp = cam.projectionMatrix * view;

                float zMin = float.MaxValue;
                float zMax = float.MinValue;
                Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
                bool anyBehind = false;

                for (int i = 0; i < 8; i++)
                {
                    Vector3 vpos = view.MultiplyPoint3x4(_corners[i]);
                    float viewZ = -vpos.z;
                    zMin = Mathf.Min(zMin, viewZ);
                    zMax = Mathf.Max(zMax, viewZ);

                    Vector4 clip = vp * new Vector4(_corners[i].x, _corners[i].y, _corners[i].z, 1f);
                    if (clip.w <= 0.001f)
                    {
                        anyBehind = true;
                        continue;
                    }

                    Vector2 uv = new Vector2(clip.x / clip.w, clip.y / clip.w) * 0.5f + Vector2.one * 0.5f;
                    uvMin = Vector2.Min(uvMin, uv);
                    uvMax = Vector2.Max(uvMax, uv);
                }

                if (zMax <= cam.nearClipPlane)
                    return false;

                if (anyBehind)
                {
                    uvMin = Vector2.zero;
                    uvMax = Vector2.one;
                }

                uvMin = Vector2.Max(uvMin, Vector2.zero);
                uvMax = Vector2.Min(uvMax, Vector2.one);
                if (uvMax.x - uvMin.x < 1e-4f || uvMax.y - uvMin.y < 1e-4f)
                    return false;

                float z0 = Mathf.Max(zMin, cam.nearClipPlane);
                float z1 = Mathf.Min(zMax, cam.farClipPlane);
                if (z1 - z0 < 1e-3f)
                    return false;

                rect = new Vector4(uvMin.x, uvMin.y, uvMax.x - uvMin.x, uvMax.y - uvMin.y);
                zRange = new Vector2(z0, z1 - z0);
                return true;
            }

            public void ReleaseHistories()
            {
                foreach (HistoryState state in _histories.Values)
                {
                    state.resolveA?.Release();
                    state.resolveB?.Release();
                }
                _histories.Clear();
            }

            public void Dispose()
            {
                ReleaseHistories();
                if (_sliceMaterial != null)
                {
                    Object.DestroyImmediate(_sliceMaterial);
                    _sliceMaterial = null;
                }
                if (_compositeMaterial != null)
                {
                    Object.DestroyImmediate(_compositeMaterial);
                    _compositeMaterial = null;
                }
            }
        }

        /// <summary>
        /// Multiplies opaque scene geometry by the volume's shadow, read from the
        /// baked self-shadow volume with a single tap per pixel. No marching.
        /// </summary>
        class BrazeCastShadowPass : ScriptableRenderPass
        {
            Material _material;
            readonly Vector3[] _corners = new Vector3[8];
            readonly List<BrazeContainer> _ready = new();

            class CastData
            {
                public Material material;
                public RenderTexture shadowTex;
                public Matrix4x4 worldToSim;
                public Vector3 sunDir;
                public float strength;
                public float fade;
                public float softness;
                public Vector3 shadowInvRes;
                public Vector4 shadowRect;
                public Vector3 camPos, camRight, camUp, camFwd;
                public Vector2 tanHalfFov;
                public bool ortho;
                public TextureHandle color, depth;
            }

            bool EnsureResources()
            {
                if (_material != null)
                    return true;
                var shader = BrazeRuntimeShaderLibrary.Find("Hidden/BrazeFX/SliceCastShadow");
                if (shader == null)
                    return false;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                BrazeContainer.CollectSliceRenderers(_ready, cameraData.camera);
                if (_ready.Count == 0 || !EnsureResources())
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                Camera cam = cameraData.camera;

                TextureHandle color = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                if (!depth.IsValid())
                    return;

                // One cast-shadow pass per ready volume that casts.
                for (int si = 0; si < _ready.Count; si++)
                {
                    var sim = _ready[si];
                    if (!sim.SliceRenderReady || !sim.sliceCastShadows)
                        continue;
                    var shadowTex = sim.SliceShadowTexture;
                    if (shadowTex == null)
                        continue;

                    // The shadow volume is baked with BrazeContainer.SliceMainLightDirection();
                    // use that exact direction for projection and sampling.
                    Vector3 sunDir = sim.SliceMainLightDirection();
                    if (!ComputeShadowRect(cam, sim, sunDir, out Vector4 shadowRect))
                        continue;

                    using var builder = renderGraph.AddRasterRenderPass<CastData>("BrazeFX Slice Cast Shadows", out CastData d);

                    d.material = _material;
                    d.shadowTex = shadowTex;
                    d.worldToSim = sim.SliceWorldToSimUVW;
                    d.sunDir = sunDir;
                    d.strength = sim.sliceCastShadowStrength;
                    d.fade = sim.sliceCastShadowFade;
                    d.softness = sim.sliceCastShadowSoftness;
                    d.shadowInvRes = new Vector3(
                        1f / Mathf.Max(1, shadowTex.width),
                        1f / Mathf.Max(1, shadowTex.height),
                        1f / Mathf.Max(1, shadowTex.volumeDepth));
                    d.shadowRect = shadowRect;

                    Transform ct = cam.transform;
                    d.camPos = ct.position;
                    d.camRight = ct.right;
                    d.camUp = ct.up;
                    d.camFwd = ct.forward;
                    var proj = cam.projectionMatrix;
                    d.tanHalfFov = new Vector2(1f / proj.m00, 1f / proj.m11);
                    d.ortho = cam.orthographic;

                    d.color = color;
                    d.depth = depth;

                    builder.UseTexture(d.depth, AccessFlags.Read);
                    builder.SetRenderAttachment(d.color, 0, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<CastData>(static (data, ctx) => ExecuteCast(data, ctx));
                }
            }

            // sign(v + 1e-20) · max(|v|, 1e-6) — the shader's per-pixel safe-reciprocal denominator, replicated
            // exactly so the CPU-computed slab reciprocals match what the pixel math produced.
            static float CastSafeComp(float v)
            {
                float s = (v + 1e-20f) < 0f ? -1f : 1f;
                return s * Mathf.Max(Mathf.Abs(v), 1e-6f);
            }

            static void ExecuteCast(CastData d, RasterGraphContext ctx)
            {
                var mat = d.material;
                mat.SetTexture(SliceShaderIds._ShadowTex, d.shadowTex);
                mat.SetTexture(SliceShaderIds._PyroDepth, d.depth);
                mat.SetMatrix(SliceShaderIds._WorldToSim, d.worldToSim);

                // Frame-constant light geometry (was recomputed per shadow pixel: a mat3 mul + normalize +
                // sign/max/rcp + two crosses + a normalize). Same operations, computed once.
                Vector3 dRaw = d.worldToSim.MultiplyVector(d.sunDir);
                Vector3 dir = dRaw / Mathf.Max(dRaw.magnitude, 1e-6f);
                Vector3 invd = new Vector3(1f / CastSafeComp(dir.x), 1f / CastSafeComp(dir.y), 1f / CastSafeComp(dir.z));
                Vector3 b1 = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
                Vector3 b2 = Vector3.Cross(dir, b1);
                mat.SetVector(SliceShaderIds._CastSunDirSim, dir);
                mat.SetVector(SliceShaderIds._CastInvD, invd);
                mat.SetVector(SliceShaderIds._CastBasis1, b1);
                mat.SetVector(SliceShaderIds._CastBasis2, b2);
                mat.SetVector(SliceShaderIds._SunDirWorld, d.sunDir);
                mat.SetFloat(SliceShaderIds._CastShadowStrength, d.strength);
                mat.SetFloat(SliceShaderIds._CastShadowFade, Mathf.Max(1e-3f, d.fade));
                mat.SetFloat(SliceShaderIds._CastShadowSoftness, d.softness);
                mat.SetVector(SliceShaderIds._ShadowInvRes, d.shadowInvRes);
                mat.SetVector(SliceShaderIds._ShadowRect, d.shadowRect);
                mat.SetVector(SliceShaderIds._PyroCamPos, d.camPos);
                mat.SetVector(SliceShaderIds._PyroCamRight, d.camRight);
                mat.SetVector(SliceShaderIds._PyroCamUp, d.camUp);
                mat.SetVector(SliceShaderIds._PyroCamFwd, d.camFwd);
                mat.SetVector(SliceShaderIds._PyroCamTanHalfFov, d.tanHalfFov);
                mat.SetFloat(SliceShaderIds._PyroOrtho, d.ortho ? 1f : 0f);

                ctx.cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 6, 1);
            }

            bool ComputeShadowRect(Camera cam, BrazeContainer sim, Vector3 sunDir, out Vector4 rect)
            {
                rect = default;
                sim.GetSliceWorldCorners(_corners);

                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 vp = cam.projectionMatrix * view;

                Quaternion invRot = Quaternion.Inverse(sim.transform.rotation);
                Vector3 localSun = invRot * sunDir.normalized;
                Vector3 ws = sim.WorldSize();   // scaled: extrusion fade is in world meters
                Vector3 uvwSun = new Vector3(
                    localSun.x / Mathf.Max(ws.x, 1e-4f),
                    localSun.y / Mathf.Max(ws.y, 1e-4f),
                    localSun.z / Mathf.Max(ws.z, 1e-4f));
                float worldFade = Mathf.Max(0f, sim.sliceCastShadowFade) / Mathf.Max(uvwSun.magnitude, 1e-4f);
                Vector3 extrusion = -sunDir.normalized * worldFade;

                Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
                float zMax = float.MinValue;
                bool anyBehind = false;

                for (int i = 0; i < 16; i++)
                {
                    Vector3 p = _corners[i & 7] + (i >= 8 ? extrusion : Vector3.zero);
                    Vector3 vpos = view.MultiplyPoint3x4(p);
                    zMax = Mathf.Max(zMax, -vpos.z);

                    Vector4 clip = vp * new Vector4(p.x, p.y, p.z, 1f);
                    if (clip.w <= 0.001f)
                    {
                        anyBehind = true;
                        continue;
                    }

                    Vector2 uv = new Vector2(clip.x / clip.w, clip.y / clip.w) * 0.5f + Vector2.one * 0.5f;
                    uvMin = Vector2.Min(uvMin, uv);
                    uvMax = Vector2.Max(uvMax, uv);
                }

                if (zMax <= cam.nearClipPlane)
                    return false;

                if (anyBehind)
                {
                    uvMin = Vector2.zero;
                    uvMax = Vector2.one;
                }

                uvMin = Vector2.Max(uvMin, Vector2.zero);
                uvMax = Vector2.Min(uvMax, Vector2.one);
                if (uvMax.x - uvMin.x < 1e-4f || uvMax.y - uvMin.y < 1e-4f)
                    return false;

                rect = new Vector4(uvMin.x, uvMin.y, uvMax.x - uvMin.x, uvMax.y - uvMin.y);
                return true;
            }

            public void Dispose()
            {
                if (_material != null)
                {
                    Object.DestroyImmediate(_material);
                    _material = null;
                }
            }
        }
    }
}
