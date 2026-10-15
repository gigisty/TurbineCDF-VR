using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using BrazeFX.CFD;

namespace BrazeFX
{
    /// <summary>
    /// Unified URP renderer feature for BrazeFX. Add this one feature to the active Renderer asset; it owns the
    /// particle, trail, live slice-volume, and generated-volume/composite paths.
    /// </summary>
    public sealed class BrazeRenderFeature : ScriptableRendererFeature
    {
        public enum RenderQualityPreset
        {
            PC = 1,
            Console = 2,
            Mobile = 3,
            // Zero is deliberate: renderer features saved before presets existed deserialize as Custom and retain
            // their authored values instead of being silently relabelled or retuned as the current Mobile default.
            Custom = 0,
        }

        public enum GeneratedHighResolutionMode
        {
            FullEveryPixel = 0,
            Interleaved2x1 = 1,
            Interleaved2x2 = 2,
        }

        public enum LiveSliceTemporalMode
        {
            Off = 0,
            Interleaved2x1 = 1,
            Interleaved2x2 = 2,
        }

        [Header("Quality")]
        [Tooltip("Coordinated renderer-quality profile. Editing an individual quality field in the custom Inspector " +
                 "switches this to Custom. Mobile is the performance-first default for newly created renderer features.")]
        public RenderQualityPreset qualityPreset = RenderQualityPreset.Mobile;

        [Header("Systems")]
        [Tooltip("Draw Braze billboard particles that are configured to render through the feature.")]
        public bool particles = true;
        [Tooltip("Draw lightweight atlas animations authored with Braze Flipbook Player.")]
        public bool flipbookPlayers = true;
        [Tooltip("Draw Braze Trail Renderer and Tracer ribbons.")]
        [FormerlySerializedAs("lines")] public bool trails = true;
        [Tooltip("Draw movable CFD scalar/vector analysis slices. This switch is independent of volume smoke.")]
        public bool cfdAnalysisSlices = true;
        [Tooltip("Draw comparative CFD pressure directly on target SDF meshes. This switch is independent of volumes.")]
        public bool cfdSurfacePressure = true;
        [Tooltip("Draw live/cache container volumes through the slice renderer.")]
        public bool liveSlices = true;
        [Tooltip("Draw generated/procedural volumes through the BrazeVolume composite path.")]
        public bool generatedVolumes = true;

        [Header("Live Slice Volumes")]
        [Range(8, 512), Tooltip("Authoritative maximum raymarch-step budget for every live and cache volume. " +
                                "Containers own simulation and appearance; they cannot lower this renderer budget.")]
        public int sliceCount = 96;
        [Range(0.25f, 2f), Tooltip("Authoritative sampling density for every live and cache volume. 1.0 = about one " +
                                   "sample per voxel; above 1 oversamples the same data at linear extra cost.")]
        public float slicesPerVoxel = 0.5f;
        [Range(0f, 1f), Tooltip("Authoritative ray-start jitter for every live and cache volume. Jitter breaks up " +
                                "view-depth contour bands; 0.75 preserves that coverage without the frozen grain " +
                                "produced by the former per-container value of 1.")]
        public float sliceJitterStrength = 0.75f;
        [Range(1, 4), Tooltip("Authoritative camera-resolution divisor for every live and cache volume. 1 = native " +
                              "pixels; 2 = half resolution per axis; 4 = quarter resolution per axis.")]
        public int sliceDownsample = 2;
        [Range(0, 2160), Tooltip("Authoritative live/cache raymarch target-height cap in pixels, with camera aspect " +
                                 "preserved. 0 = uncapped. This composes with Resolution Divisor and is never " +
                                 "overridden by a container or cache player.")]
        public int sliceHeightCap = 0;
        [Tooltip("Temporal live-slice strategy. Interleaved modes rotate true working-resolution pixels over two or " +
                 "four flow-reprojected frames at the selected Slice Downsample working resolution. Set Slice " +
                 "Downsample to 1 for native temporal output, or combine both levers for maximum speed. Scene view " +
                 "keeps the spatial fallback; first frames, camera cuts, and fast camera motion automatically " +
                 "refresh more Game-camera pixels.")]
        [FormerlySerializedAs("sliceHighResolutionMode")]
        public LiveSliceTemporalMode sliceTemporalMode = LiveSliceTemporalMode.Off;
        [Range(0f, 0.95f), Tooltip("History weight used to reconstruct interleaved live slices. Lower values react " +
                                   "faster; higher values are steadier. Has no cost when Temporal Mode is Off.")]
        public float sliceTemporalBlend = 0f;
        [Tooltip("Reproject live-slice history with the opacity centroid and one solver-velocity lookup per ray. " +
                 "Required by interleaved modes; cache playback falls back to camera-only reprojection.")]
        public bool sliceMotionReproject = false;
        [Tooltip("Experimental A/B: store temporal RGB through an HDR companding curve in RGBA8 instead of " +
                 "RGBAHalf. Halves persistent live-slice history memory/bandwidth; disable if bloom or dark smoke " +
                 "shows unacceptable quantization.")]
        public bool sliceCompactHistory = false;
        [Tooltip("Use the solver's per-4x4x4-brick smoke/fire variation statistics to take longer steps through " +
                 "smooth interiors while retaining voxel-sized steps at silhouettes and flame fronts.")]
        public bool sliceAdaptiveMarching = true;
        [Range(1f, 4f), Tooltip("Largest step multiplier allowed inside a smooth, non-burning occupancy brick.")]
        public float sliceAdaptiveMaxStep = 4f;
        [Tooltip("Experimental: permit conservative longer steps through spatially smooth flame interiors. Flame " +
                 "fronts and high-variation fire still use the voxel step.")]
        public bool sliceAdaptiveSmoothFire = true;
        [Range(1f, 2f), Tooltip("Largest step multiplier allowed in a smooth flame brick. Keep at 2 for the first A/B.")]
        public float sliceAdaptiveFireMaxStep = 2f;
        [Range(0.1f, 1f), Tooltip("Relative within-brick variation treated as detail. Lower values are more conservative " +
                                  "and preserve more samples; higher values accelerate more smooth smoke/fire.")]
        public float sliceAdaptiveVariance = 0.5f;

        [Header("Generated Volumes")]
        [Range(8, 256), Tooltip("Authoritative per-ray step budget for every generated volume. Generated-volume " +
                                "components own density, lighting, shading, and shadows; this feature owns final " +
                                "sampling quality.")]
        public int volumeRaymarchSteps = 64;
        [Range(1, 4), Tooltip("Resolution divisor for generated-volume raymarch/composite. 2 = half-res.")]
        public int volumeDownsample = 2;
        [Range(0, 2160), Tooltip("Authoritative generated-volume raymarch target-height cap in pixels, with camera " +
                                 "aspect preserved. 0 = uncapped.")]
        public int volumeHeightCap = 0;
        [Tooltip("Native-resolution strategy. Interleaved modes shade true output pixels over 2 or 4 reprojected " +
                 "frames instead of permanently blurring a smaller buffer. They require Temporal Noise Reduction " +
                 "and Motion Reprojection; the first frame and camera cuts refresh every pixel.")]
        public GeneratedHighResolutionMode volumeHighResolutionMode = GeneratedHighResolutionMode.FullEveryPixel;
        [Range(0f, 0.95f), Tooltip("Temporal averaging for generated-volume raymarch jitter.")]
        public float temporalNoiseReduction = 0f;
        [Tooltip("Animate generated-volume ray jitter. Off keeps static generated volumes locked in place.")]
        public bool animatedRayJitter = false;
        [Tooltip("Composite generated volumes directly onto camera color with hardware blend.")]
        public bool directComposite = true;
        [Tooltip("Depth-guided upsample for generated volumes when downsampled.")]
        public bool bilateralUpsample = true;
        [Range(0f, 1f), Tooltip("Contrast-adaptive reconstruction for downsampled generated volumes. Uses the " +
                                "bilateral pass's existing four samples; adds no texture reads.")]
        public float volumeReconstructionSharpness = 0.5f;
        [Tooltip("Motion-reproject generated-volume temporal resolve.")]
        public bool motionReproject = false;
        [Range(1, 4), Tooltip("Minimum generated-volume downsample used by Scene view. Editor-only authoring budget.")]
        public int generatedSceneViewDownsample = 4;
        [Range(0, 2160), Tooltip("Scene-view generated-volume height cap. 0 inherits Generated Height Cap.")]
        public int generatedSceneViewHeightCap = 540;
        [Tooltip("Render generated volumes in reflection cameras. Off avoids an implicit extra raymarch per probe.")]
        public bool generatedReflectionCameras = false;
        [SerializeField, HideInInspector] Shader compositeShader; // legacy/override hook; normal setup auto-finds BrazeFX/Composite.

        /// <summary>Apply a coordinated renderer-quality profile without changing which BrazeFX systems are enabled.</summary>
        public void ApplyQualityPreset(RenderQualityPreset preset)
        {
            qualityPreset = preset;
            switch (preset)
            {
                case RenderQualityPreset.PC:
                    sliceCount = 512;
                    slicesPerVoxel = 1f;
                    sliceJitterStrength = 0.75f;
                    sliceDownsample = 1;
                    sliceHeightCap = 0;
                    sliceTemporalMode = LiveSliceTemporalMode.Off;
                    sliceTemporalBlend = 0f;
                    sliceMotionReproject = false;
                    sliceCompactHistory = false;
                    sliceAdaptiveMarching = true;
                    sliceAdaptiveMaxStep = 2f;
                    sliceAdaptiveSmoothFire = false;
                    sliceAdaptiveFireMaxStep = 1f;
                    sliceAdaptiveVariance = 0.25f;
                    volumeRaymarchSteps = 192;
                    volumeDownsample = 1;
                    volumeHeightCap = 0;
                    volumeHighResolutionMode = GeneratedHighResolutionMode.FullEveryPixel;
                    temporalNoiseReduction = 0f;
                    animatedRayJitter = false;
                    directComposite = true;
                    bilateralUpsample = false;
                    volumeReconstructionSharpness = 0.25f;
                    motionReproject = false;
                    generatedSceneViewDownsample = 1;
                    generatedSceneViewHeightCap = 0;
                    generatedReflectionCameras = false;
                    break;

                case RenderQualityPreset.Console:
                    sliceCount = 256;
                    slicesPerVoxel = 0.75f;
                    sliceJitterStrength = 0.75f;
                    sliceDownsample = 1;
                    sliceHeightCap = 0;
                    sliceTemporalMode = LiveSliceTemporalMode.Off;
                    sliceTemporalBlend = 0f;
                    sliceMotionReproject = false;
                    sliceCompactHistory = false;
                    sliceAdaptiveMarching = true;
                    sliceAdaptiveMaxStep = 3f;
                    sliceAdaptiveSmoothFire = true;
                    sliceAdaptiveFireMaxStep = 1.5f;
                    sliceAdaptiveVariance = 0.35f;
                    volumeRaymarchSteps = 128;
                    volumeDownsample = 1;
                    volumeHeightCap = 0;
                    volumeHighResolutionMode = GeneratedHighResolutionMode.FullEveryPixel;
                    temporalNoiseReduction = 0f;
                    animatedRayJitter = false;
                    directComposite = true;
                    bilateralUpsample = false;
                    volumeReconstructionSharpness = 0.25f;
                    motionReproject = false;
                    generatedSceneViewDownsample = 2;
                    generatedSceneViewHeightCap = 1080;
                    generatedReflectionCameras = false;
                    break;

                case RenderQualityPreset.Mobile:
                    sliceCount = 96;
                    slicesPerVoxel = 0.5f;
                    sliceJitterStrength = 0.75f;
                    sliceDownsample = 2;
                    sliceHeightCap = 0;
                    sliceTemporalMode = LiveSliceTemporalMode.Off;
                    sliceTemporalBlend = 0f;
                    sliceMotionReproject = false;
                    sliceCompactHistory = false;
                    sliceAdaptiveMarching = true;
                    sliceAdaptiveMaxStep = 4f;
                    sliceAdaptiveSmoothFire = true;
                    sliceAdaptiveFireMaxStep = 2f;
                    sliceAdaptiveVariance = 0.5f;
                    volumeRaymarchSteps = 64;
                    volumeDownsample = 2;
                    volumeHeightCap = 0;
                    volumeHighResolutionMode = GeneratedHighResolutionMode.FullEveryPixel;
                    temporalNoiseReduction = 0f;
                    animatedRayJitter = false;
                    directComposite = true;
                    bilateralUpsample = true;
                    volumeReconstructionSharpness = 0.5f;
                    motionReproject = false;
                    generatedSceneViewDownsample = 4;
                    generatedSceneViewHeightCap = 540;
                    generatedReflectionCameras = false;
                    break;

                case RenderQualityPreset.Custom:
                    break;
            }
        }

        ParticlePass particlePass;
        TrailPass trailPass;
        AnalysisSlicePass analysisSlicePass;
        SurfacePressurePass surfacePressurePass;
        BrazeSliceRendererPasses slicePasses;
        BrazeVolumeCompositePasses volumePasses;

        public override void Create()
        {
            EnsurePasses();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            EnsurePasses();

            Camera camera = renderingData.cameraData.camera;
            bool effectOnly = BrazeCameraRenderIsolation.IsEffectOnly(camera);
            bool drawParticles = !effectOnly && particles && AnyParticlesReady();
            bool drawFlipbooks = !effectOnly && flipbookPlayers && AnyFlipbooksReady();
            if (drawParticles || drawFlipbooks)
            {
                particlePass.drawParticles = drawParticles;
                particlePass.drawFlipbooks = drawFlipbooks;
                particlePass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(particlePass);
            }
            bool drawTrails = !effectOnly && trails && AnyTrailsReady();
            if (drawTrails)
            {
                trailPass.drawTrails = drawTrails;
                renderer.EnqueuePass(trailPass);
            }
            if (!effectOnly && cfdAnalysisSlices && AnyCfdAnalysisSlicesReady())
            {
                analysisSlicePass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(analysisSlicePass);
            }
            if (!effectOnly && cfdSurfacePressure && AnyCfdSurfacePressureReady())
            {
                surfacePressurePass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(surfacePressurePass);
            }
            if (liveSlices)
                slicePasses.AddRenderPasses(
                    renderer,
                    ref renderingData,
                    Mathf.Clamp(sliceCount, 8, 512),
                    Mathf.Clamp(slicesPerVoxel, 0.25f, 2f),
                    Mathf.Clamp01(sliceJitterStrength),
                    Mathf.Clamp(sliceDownsample, 1, 4),
                    Mathf.Clamp(sliceHeightCap, 0, 2160),
                    (int)sliceTemporalMode,
                    Mathf.Clamp(sliceTemporalBlend, 0f, 0.95f),
                    sliceMotionReproject,
                    sliceCompactHistory,
                    sliceAdaptiveMarching,
                    Mathf.Clamp(sliceAdaptiveMaxStep, 1f, 4f),
                    sliceAdaptiveSmoothFire ? Mathf.Clamp(sliceAdaptiveFireMaxStep, 1f, 2f) : 1f,
                    Mathf.Clamp(sliceAdaptiveVariance, 0.1f, 1f));
            if (!effectOnly && generatedVolumes)
            {
                volumePasses.raymarchSteps = Mathf.Clamp(volumeRaymarchSteps, 8, 256);
                volumePasses.downsample = Mathf.Clamp(volumeDownsample, 1, 4);
                volumePasses.heightCap = Mathf.Clamp(volumeHeightCap, 0, 2160);
                volumePasses.highResolutionMode = (int)volumeHighResolutionMode;
                volumePasses.temporalNoiseReduction = Mathf.Clamp01(temporalNoiseReduction);
                volumePasses.animatedRayJitter = animatedRayJitter;
                volumePasses.directComposite = directComposite;
                volumePasses.bilateralUpsample = bilateralUpsample;
                volumePasses.reconstructionSharpness = Mathf.Clamp01(volumeReconstructionSharpness);
                volumePasses.motionReproject = motionReproject;
                volumePasses.sceneViewDownsample = Mathf.Clamp(generatedSceneViewDownsample, 1, 4);
                volumePasses.sceneViewHeightCap = Mathf.Clamp(generatedSceneViewHeightCap, 0, 2160);
                volumePasses.reflectionCameras = generatedReflectionCameras;
                volumePasses.compositeShader = BrazeVolumeCompositePasses.ResolveCompositeShader(compositeShader);
                volumePasses.AddRenderPasses(renderer, ref renderingData);
            }
        }

        protected override void Dispose(bool disposing)
        {
            ReleasePasses();
        }

        void EnsurePasses()
        {
            particlePass ??= new ParticlePass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
            trailPass ??= new TrailPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
            analysisSlicePass ??= new AnalysisSlicePass
                { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
            surfacePressurePass ??= new SurfacePressurePass
                { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
            if (slicePasses == null)
            {
                slicePasses = new BrazeSliceRendererPasses();
                slicePasses.Create();
            }
            if (volumePasses == null)
            {
                volumePasses = new BrazeVolumeCompositePasses();
                volumePasses.Create();
            }
        }

        static bool AnyParticlesReady()
        {
            bool any = false;
            for (int i = BrazeParticleSystem.Active.Count - 1; i >= 0; i--)
            {
                var system = BrazeParticleSystem.Active[i];
                if (system == null || !system.isActiveAndEnabled)
                {
                    BrazeParticleSystem.Active.RemoveAt(i);
                    continue;
                }
                if (system.Ready && system.DrawViaFeature) any = true;
            }
            return any;
        }

        static bool AnyTrailsReady()
        {
            bool any = false;
            for (int i = BrazeTrailRenderer.Active.Count - 1; i >= 0; i--)
            {
                var trail = BrazeTrailRenderer.Active[i];
                if (trail == null || !trail.isActiveAndEnabled)
                {
                    BrazeTrailRenderer.Active.RemoveAt(i);
                    continue;
                }
                if (trail.Ready) any = true;
            }
            for (int i = BrazeTracer.Active.Count - 1; i >= 0; i--)
            {
                var tracer = BrazeTracer.Active[i];
                if (tracer == null || !tracer.isActiveAndEnabled)
                {
                    BrazeTracer.Active.RemoveAt(i);
                    continue;
                }
                if (tracer.Ready) any = true;
            }
            return any;
        }

        static bool AnyCfdAnalysisSlicesReady()
        {
            bool any = false;
            for (int i = BrazeCfdAnalysisSlice.Active.Count - 1; i >= 0; i--)
            {
                var view = BrazeCfdAnalysisSlice.Active[i];
                if (view == null || !view.isActiveAndEnabled)
                {
                    BrazeCfdAnalysisSlice.Active.RemoveAt(i);
                    continue;
                }
                if (view.Ready) any = true;
            }
            return any;
        }

        static bool AnyCfdSurfacePressureReady()
        {
            bool any = false;
            for (int i = BrazeCfdSurfacePressure.Active.Count - 1; i >= 0; i--)
            {
                var view = BrazeCfdSurfacePressure.Active[i];
                if (view == null || !view.isActiveAndEnabled)
                {
                    BrazeCfdSurfacePressure.Active.RemoveAt(i);
                    continue;
                }
                if (view.Ready) any = true;
            }
            return any;
        }

        static bool AnyFlipbooksReady()
        {
            // Prune stopped/finished players here instead of giving every component an Update method. This scan
            // runs only while the registry contains work and leaves no persistent per-frame cost after Once ends.
            for (int i = BrazeFlipbookPlayer.Active.Count - 1; i >= 0; i--)
            {
                var player = BrazeFlipbookPlayer.Active[i];
                if (player != null && player.isActiveAndEnabled && player.WantsRender)
                    return true;
                BrazeFlipbookPlayer.Active.RemoveAt(i);
            }
            return false;
        }

        void ReleasePasses()
        {
            slicePasses?.Dispose();
            slicePasses = null;
            volumePasses?.Dispose();
            volumePasses = null;
            particlePass = null;
            trailPass = null;
            surfacePressurePass = null;
        }

        sealed class ParticlePass : ScriptableRenderPass
        {
            internal bool drawParticles;
            internal bool drawFlipbooks;

            class PassData
            {
                public readonly List<BrazeParticleSystem> systems = new();
                public readonly List<BrazeFlipbookPlayer> flipbooks = new();
                public TextureHandle color;
                public TextureHandle depth;
                public Camera camera;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                using var builder = renderGraph.AddUnsafePass<PassData>("BrazeFX Particles + Flipbooks", out var data);

                data.color = resources.activeColorTexture;
                data.depth = resources.activeDepthTexture;
                data.camera = cameraData.camera;
                data.systems.Clear();
                data.flipbooks.Clear();
                if (drawParticles)
                    foreach (var s in BrazeParticleSystem.Active)
                        if (s != null && s.isActiveAndEnabled && s.Ready && s.DrawViaFeature)
                        {
                            if (s.NeedsAlphaDepthSort) s.PrepareDepthSort();
                            data.systems.Add(s);
                            builder.UseBuffer(renderGraph.ImportBuffer(s.ParticleBuffer), AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(s.VisibleBuffer),
                                s.NeedsAlphaDepthSort ? AccessFlags.ReadWrite : AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(s.ArgsBuffer), AccessFlags.Read);
                            if (s.SortPairsBuffer != null)
                                builder.UseBuffer(renderGraph.ImportBuffer(s.SortPairsBuffer), AccessFlags.ReadWrite);
                        }
                if (drawFlipbooks)
                    foreach (var player in BrazeFlipbookPlayer.Active)
                        if (player != null && player.isActiveAndEnabled && player.PrepareForRender())
                        {
                            data.flipbooks.Add(player);
                        }

                builder.UseTexture(data.color, AccessFlags.ReadWrite);
                if (data.depth.IsValid())
                    builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, UnsafeGraphContext ctx) =>
                {
                    if (d.depth.IsValid()) ctx.cmd.SetRenderTarget(d.color, d.depth);
                    else ctx.cmd.SetRenderTarget(d.color);

                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    foreach (var s in d.systems)
                    {
                        if (s == null || !s.isActiveAndEnabled || !s.Ready || !s.DrawViaFeature)
                            continue;
                        if (s.NeedsAlphaDepthSort)
                            s.DispatchDepthSort(cmd, d.camera);
                        cmd.DrawProceduralIndirect(
                            Matrix4x4.identity,
                            s.RenderMaterial,
                            0,
                            MeshTopology.Triangles,
                            s.ArgsBuffer,
                            0,
                            s.Properties);
                    }
                    foreach (var player in d.flipbooks)
                    {
                        if (player == null || !player.isActiveAndEnabled || !player.Ready || !player.WantsRender)
                            continue;
                        cmd.DrawMesh(
                            player.RenderMesh,
                            Matrix4x4.identity,
                            player.RenderMaterial,
                            0,
                            0,
                            player.RenderProperties);
                    }
                });
            }
        }

        sealed class TrailPass : ScriptableRenderPass
        {
            public bool drawTrails;

            class PassData
            {
                public readonly List<BrazeTrailRenderer> trails = new();
                public readonly List<BrazeTracer> tracers = new();
                public TextureHandle color;
                public TextureHandle depth;
                public Camera camera;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                using var builder = renderGraph.AddUnsafePass<PassData>("BrazeFX Trails", out var data);

                // Pooled PassData — clear or the lists grow by one entry per frame (see AnalysisSlicePass).
                data.color = resources.activeColorTexture;
                data.depth = resources.activeDepthTexture;
                data.camera = cameraData.camera;
                data.trails.Clear();
                data.tracers.Clear();
                if (drawTrails)
                {
                    foreach (var l in BrazeTrailRenderer.Active)
                        if (l != null && l.isActiveAndEnabled && l.Ready)
                        {
                            data.trails.Add(l);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.LineNodeBuffer), AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.TrailBoundsBuffer), AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.TrackedTrailBuffer), AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.TrackedArgsBuffer), AccessFlags.Read);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.DrawVisibleBuffer), AccessFlags.ReadWrite);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.CullDispatchArgsBuffer), AccessFlags.ReadWrite);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.ArgsBuffer), AccessFlags.ReadWrite);
                            builder.UseBuffer(renderGraph.ImportBuffer(l.IndexBuffer), AccessFlags.Read);
                        }
                    foreach (var tracer in BrazeTracer.Active)
                        if (tracer != null && tracer.isActiveAndEnabled && tracer.Ready)
                            data.tracers.Add(tracer);
                }
                builder.UseTexture(data.color, AccessFlags.ReadWrite);
                if (data.depth.IsValid())
                    builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, UnsafeGraphContext ctx) =>
                {
                    if (d.depth.IsValid()) ctx.cmd.SetRenderTarget(d.color, d.depth);
                    else ctx.cmd.SetRenderTarget(d.color);

                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    foreach (var l in d.trails)
                    {
                        if (l == null || !l.isActiveAndEnabled || !l.Ready || l.IndexBuffer == null)
                            continue;
                        l.DispatchCameraCull(cmd, d.camera);
                        cmd.DrawProceduralIndirect(
                            l.IndexBuffer,
                            Matrix4x4.identity,
                            l.RenderMaterial,
                            0,
                            MeshTopology.Triangles,
                            l.ArgsBuffer,
                            0,
                            l.Properties);
                    }
                    foreach (var tracer in d.tracers)
                    {
                        if (tracer == null || !tracer.isActiveAndEnabled || !tracer.Ready || tracer.IndexBuffer == null)
                            continue;
                        cmd.DrawProceduralIndirect(
                            tracer.IndexBuffer,
                            Matrix4x4.identity,
                            tracer.RenderMaterial,
                            0,
                            MeshTopology.Triangles,
                            tracer.ArgsBuffer,
                            0,
                            tracer.Properties);
                    }
                });
            }
        }

        sealed class AnalysisSlicePass : ScriptableRenderPass
        {
            class PassData
            {
                public readonly List<BrazeCfdAnalysisSlice> views = new();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                using var builder = renderGraph.AddRasterRenderPass<PassData>(
                    "BrazeFX CFD Analysis Slice", out var data);

                // RenderGraph POOLS PassData across frames: without this clear the list grows by one entry per
                // frame and the slice is alpha-blended over itself thousands of times ("FPS decays to single
                // digits the longer a slice stays visible").
                data.views.Clear();
                foreach (var view in BrazeCfdAnalysisSlice.Active)
                    if (view != null && view.isActiveAndEnabled && view.Ready)
                        data.views.Add(view);

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                if (resources.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    foreach (var view in d.views)
                    {
                        if (view == null || !view.isActiveAndEnabled || !view.Ready || view.Mesh == null)
                            continue;
                        ctx.cmd.DrawMesh(view.Mesh, view.LocalToWorld, view.RenderMaterial, 0, 0, view.Properties);
                    }
                });
            }
        }

        sealed class SurfacePressurePass : ScriptableRenderPass
        {
            class PassData
            {
                public readonly List<BrazeCfdSurfacePressure> views = new();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                using var builder = renderGraph.AddRasterRenderPass<PassData>(
                    "BrazeFX CFD Surface Pressure", out var data);

                data.views.Clear();   // pooled PassData — see AnalysisSlicePass
                foreach (var view in BrazeCfdSurfacePressure.Active)
                    if (view != null && view.isActiveAndEnabled && view.Ready)
                        data.views.Add(view);

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                if (resources.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    foreach (var view in d.views)
                    {
                        if (view == null || !view.isActiveAndEnabled || !view.Ready || view.Mesh == null)
                            continue;
                        for (int submesh = 0; submesh < view.Mesh.subMeshCount; submesh++)
                            ctx.cmd.DrawMesh(view.Mesh, view.LocalToWorld, view.RenderMaterial,
                                submesh, 0, view.Properties);
                    }
                });
            }
        }
    }
}
