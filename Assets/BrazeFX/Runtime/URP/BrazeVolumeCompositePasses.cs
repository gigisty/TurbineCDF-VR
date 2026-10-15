using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace BrazeFX
{
    /// <summary>
    /// Renders BrazeFX volumes (shader pass LightMode "BrazeVolume") into a downsampled
    /// offscreen buffer, then composites over a copied camera color. The raymarch is the
    /// fill-rate bottleneck; running it at 1/2 or 1/4 resolution is the big win.
    ///
    /// Owned by the unified <see cref="BrazeRenderFeature"/>. The composite shader auto-resolves from
    /// "BrazeFX/Composite" when left empty.
    /// </summary>
    internal sealed class BrazeVolumeCompositePasses
    {
        [Tooltip("Resolution divisor for the volume pass. 2 = half-res (4x fewer rays), 4 = quarter.")]
        [Range(1, 4)] public int downsample = 2;
        [Range(8, 256)] public int raymarchSteps = 192;
        [Range(0, 2160)] public int heightCap;
        public int highResolutionMode;
        public int sceneViewDownsample = 2;
        public int sceneViewHeightCap = 720;
        public bool reflectionCameras;
        // (The temporal TRAIL — a stylized screen-space afterglow that blended a decayed copy of last frame's
        // volume back in — was removed: a full-res history ping-pong pass every frame is exactly the fixed
        // per-frame cost this realtime system exists to avoid, and it contributed nothing to correctness.)
        [Tooltip("Temporal averaging for raymarch jitter. Keeps stochastic ray phases to avoid banding, then clamps history to the current frame to reduce grain.")]
        [Range(0f, 0.95f)] public float temporalNoiseReduction = 0.72f;
        [Tooltip("Animate the raymarch jitter phase every frame. Off keeps static generated volumes locked in place; on can average grain over time but may look like crawling noise.")]
        public bool animatedRayJitter = false;
        [Tooltip("Composite the volume straight onto the camera color with a hardware blend, skipping the full-res scene-copy pass (cheaper). Turn off to fall back to the explicit copy+composite path if the blend misbehaves on your target.")]
        public bool directComposite = true;
        [Tooltip("Depth-guided (bilateral) upsample of the low-res volume at composite time. Lets you push Downsample to 4 (quarter-res, 4x fewer rays) while keeping the volume's edge against geometry crisp. Only active when Direct Composite is on and Downsample > 1.")]
        public bool bilateralUpsample = false;
        public float reconstructionSharpness = 0.25f;
        [Tooltip("Motion-reproject the temporal denoiser: the raymarch emits per-pixel screen-space motion vectors so history is fetched from where the smoke/fire WAS last frame. Lets the temporal averaging clean the raymarch grain (the 'dots') on MOVING fire and under camera orbit, instead of rejecting history whenever a pixel changes. Only active when Temporal Noise Reduction > 0.")]
        public bool motionReproject = true;
        [Tooltip("Optional override. When empty, the feature auto-finds BrazeFX/Composite.")]
        public Shader compositeShader;

        Material compositeMat;
        VolumePass pass;

        internal static bool SupportsGeneratedCamera(CameraType type, bool allowReflections)
            => type == CameraType.Game || type == CameraType.SceneView ||
               (type == CameraType.Reflection && allowReflections);

        internal static void ResolveCameraBudget(
            CameraType type, int authoredDownsample, int authoredHeightCap,
            int authoringDownsample, int authoringHeightCap,
            out int resolvedDownsample, out int resolvedHeightCap)
        {
            resolvedDownsample = Mathf.Clamp(authoredDownsample, 1, 4);
            resolvedHeightCap = Mathf.Max(0, authoredHeightCap);
            if (type != CameraType.SceneView) return;
            resolvedDownsample = Mathf.Max(resolvedDownsample, Mathf.Clamp(authoringDownsample, 1, 4));
            int sceneCap = Mathf.Max(0, authoringHeightCap);
            if (sceneCap > 0)
                resolvedHeightCap = resolvedHeightCap > 0 ? Mathf.Min(resolvedHeightCap, sceneCap) : sceneCap;
        }

        internal static int ResolveInterleaveMode(int requested, int downsample, float temporal, bool reproject, bool sceneView)
            => downsample == 1 && temporal > 0.001f && reproject && !sceneView ? Mathf.Clamp(requested, 0, 2) : 0;

        internal static int InterleavePhaseForFrame(int mode, int frame)
            => mode == 1 ? (frame & 1) : mode == 2 ? (frame & 3) : 0;

        internal static bool InterleavePixelValid(int mode, int phase, int x, int y)
            => mode == 1 ? ((x + phase) & 1) == 0
             : mode == 2 ? ((x & 1) + ((y & 1) << 1)) == phase
             : true;

        internal void Create()
        {
            pass = new VolumePass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        internal void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Shader resolvedComposite = ResolveCompositeShader(compositeShader);
            if (resolvedComposite == null) return;
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (!SupportsGeneratedCamera(cameraType, reflectionCameras)) return;

            // Skip the whole feature when nothing wants ANY of its passes — volume draw, fire→scene light,
            // or volume→scene shadow (the injection passes must run even with no volume compositing: since
            // the legacy container raymarch was removed, containers report AnyVolumeRendering()=false while
            // still casting fire light + shadows). The raymarch/composite passes are gated separately inside.
            bool anyVolume = BrazeContainer.AnyVolumeRendering() || BrazeVolumeRegistry.AnyRendering();
            bool anyInjection = BrazeContainer.AnyFireLighting() || BrazeVolumeRegistry.AnyFireLighting()
                             || BrazeContainer.AnyShadowCasting() || BrazeVolumeRegistry.AnyShadowCasting();
            if (!anyVolume && !anyInjection)
            {
                pass.ResetHistory();
                return;
            }
            if (!anyVolume)
                pass.ResetHistory();   // injection only: no volume history to keep coherent
            if (compositeMat == null || compositeMat.shader != resolvedComposite)
            {
                CoreUtils.Destroy(compositeMat);
                compositeMat = CoreUtils.CreateEngineMaterial(resolvedComposite);
            }
            bool unstableDetail = BrazeContainer.AnyRenderDetailRendering() || BrazeVolumeRegistry.AnyTemporallyUnstableDetail();
            bool sceneView = cameraType == CameraType.SceneView;
            ResolveCameraBudget(cameraType, downsample, heightCap, sceneViewDownsample, sceneViewHeightCap,
                                out int effectiveDownsample, out int effectiveHeightCap);
            // With Render Detail active, clamp the temporal averaging: full-strength history blends mismatched
            // reprojected frames onto the boiling detail (reprojection can't follow procedural fume animation)
            // → shimmer / "TV noise". The clamp is the tuned compromise that denoises the steady smoke body
            // without shimmering the live detail.
            float effectiveNoiseReduction = unstableDetail ? Mathf.Min(temporalNoiseReduction, 0.35f) : temporalNoiseReduction;
            int effectiveInterleave = ResolveInterleaveMode(
                highResolutionMode, effectiveDownsample, effectiveNoiseReduction, motionReproject, sceneView);
            pass.Setup(effectiveDownsample, effectiveHeightCap, raymarchSteps, compositeMat,
                       effectiveNoiseReduction, animatedRayJitter,
                       directComposite, bilateralUpsample, reconstructionSharpness, motionReproject, effectiveInterleave);
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);   // bind _CameraDepthTexture so the raymarch can occlude against scene geometry
            renderer.EnqueuePass(pass);
        }

        internal static Shader ResolveCompositeShader(Shader overrideShader = null)
        {
            return overrideShader != null ? overrideShader : BrazeRuntimeShaderLibrary.Find("BrazeFX/Composite");
        }

        internal void Dispose()
        {
            CoreUtils.Destroy(compositeMat);
            compositeMat = null;
            pass?.Dispose();
            pass = null;
        }

        sealed class VolumePass : ScriptableRenderPass
        {
            static readonly ShaderTagId Tag = new ShaderTagId("BrazeVolume");
            static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
            static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
            static readonly int SceneColorId = Shader.PropertyToID("_BrazeSceneColor");
            static readonly int VolumeCurrentId = Shader.PropertyToID("_BrazeVolumeCurrent");
            static readonly int VolumeHistoryId = Shader.PropertyToID("_BrazeVolumeHistory");
            static readonly int VolumeTexelSizeId = Shader.PropertyToID("_BrazeVolumeTexelSize");
            static readonly int TemporalBlendId = Shader.PropertyToID("_BrazeTemporalBlend");
            static readonly int RayJitterFrameId = Shader.PropertyToID("_BrazeRayJitterFrame");
            static readonly int MotionTextureId = Shader.PropertyToID("_BrazeVolumeMotion");
            static readonly int PrevVPId = Shader.PropertyToID("_BrazePrevVP");
            static readonly int ReprojectId = Shader.PropertyToID("_BrazeReproject");
            static readonly int InterleaveModeId = Shader.PropertyToID("_BrazeInterleaveMode");
            static readonly int InterleavePhaseId = Shader.PropertyToID("_BrazeInterleavePhase");
            static readonly int GeneratedStepBudgetId = Shader.PropertyToID("_BrazeGeneratedStepBudget");
            static readonly Vector4 FullscreenScaleBias = new(1, 1, 0, 0);
            const int CompositePass = 0;
            const int TemporalResolvePass = 1;
            const int CompositeBlendPass = 2;
            const int TemporalResolveCompositeBlendPass = 3;
            static readonly int BilateralId = Shader.PropertyToID("_BrazeBilateral");
            static readonly int ReconstructionSharpnessId = Shader.PropertyToID("_BrazeReconstructionSharpness");
            int down;
            int maxRayHeight;
            int raymarchSteps;
            float temporalNoiseReduction;
            bool animatedRayJitter;
            bool directComposite;
            bool bilateralUpsample;
            float reconstructionSharpness;
            bool motionReproject;
            int interleaveMode;
            Material composite;
            int seenRenderSettingsVersion = -1;
            readonly Dictionary<int, HistoryState> histories = new();
            // Reused per-record scratch (was a fresh List per camera per frame while injection is active).
            // Safe: each camera's graph executes before the next camera records, and each pass type keeps its own list.
            readonly List<Material> _shadowMats = new();
            readonly List<Material> _fireMats = new();

            public void Setup(int d, int maxRayH, int steps, Material m, float noiseReduction, bool animateJitter, bool direct,
                              bool bilateral, float sharpen, bool reproject, int interleave)
            {
                int sanitizedInterleave = Mathf.Clamp(interleave, 0, 2);
                if (interleaveMode != sanitizedInterleave) ResetHistory();
                down = d;
                maxRayHeight = maxRayH;
                raymarchSteps = Mathf.Clamp(steps, 8, 256);
                composite = m;
                temporalNoiseReduction = Mathf.Clamp01(noiseReduction);
                animatedRayJitter = animateJitter;
                directComposite = direct;
                bilateralUpsample = bilateral;
                reconstructionSharpness = Mathf.Clamp01(sharpen);
                motionReproject = reproject;
                interleaveMode = sanitizedInterleave;
            }

            sealed class HistoryState
            {
                public RTHandle resolveA;
                public RTHandle resolveB;
                public bool resolveFlip;
                public bool resolveValid;
                // Previous-frame view-projection + timestamp per camera → screen-space motion reprojection.
                public Matrix4x4 prevVP;
                public float prevTime;
                public bool vpValid;
                public Vector3 cameraPosition;
                public Quaternion cameraRotation;
                public bool cameraPoseValid;
                public int lastUsedFrame;   // prune key — see SweepStaleHistories
            }

            // Entries for destroyed/transient cameras (runtime spawns, cutscene cams) otherwise hold their two
            // full-res history RTHandles for the app lifetime (~7 MB per abandoned 1080p half-res pair).
            int lastHistorySweepFrame;
            static readonly List<int> _staleHistoryKeys = new();

            void SweepStaleHistories()
            {
                int now = Time.frameCount;
                if (now - lastHistorySweepFrame < 300) return;
                lastHistorySweepFrame = now;
                _staleHistoryKeys.Clear();
                foreach (var kv in histories)
                    if (now - kv.Value.lastUsedFrame > 600) _staleHistoryKeys.Add(kv.Key);
                for (int i = 0; i < _staleHistoryKeys.Count; i++)
                {
                    var s = histories[_staleHistoryKeys[i]];
                    s.resolveA?.Release();
                    s.resolveB?.Release();
                    histories.Remove(_staleHistoryKeys[i]);
                }
            }

            HistoryState GetHistory(Camera camera, RenderTextureDescriptor desc)
            {
                SweepStaleHistories();
                int id = camera != null ? camera.BrazeInstanceId() : 0;
                if (!histories.TryGetValue(id, out var state))
                {
                    state = new HistoryState();
                    histories.Add(id, state);
                }
                state.lastUsedFrame = Time.frameCount;

                bool changedResolveA = RenderingUtils.ReAllocateHandleIfNeeded(
                    ref state.resolveA, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BrazeVolumeResolveA");
                bool changedResolveB = RenderingUtils.ReAllocateHandleIfNeeded(
                    ref state.resolveB, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BrazeVolumeResolveB");
                if (changedResolveA || changedResolveB) state.resolveValid = false;

                return state;
            }

            public void ResetHistory()
            {
                foreach (var state in histories.Values)
                {
                    state.resolveValid = false;
                }
            }

            public void Dispose()
            {
                foreach (var state in histories.Values)
                {
                    state.resolveA?.Release();
                    state.resolveB?.Release();
                }
                histories.Clear();
            }

            class VolumeData
            {
                public RendererListHandle list;
                public float jitterFrame;
                public Matrix4x4 prevVP;
                public float reproject;
                public int interleaveMode;
                public int interleavePhase;
                public int raymarchSteps;
            }

            class CopyData
            {
                public TextureHandle source;
            }

            class FireLightData
            {
                public List<Material> materials;       // container/cache injection materials (BrazeFX/FireLight, pass 1)
            }

            class CompositeData
            {
                public TextureHandle volume;
                public TextureHandle scene;
                public Material material;
                public bool bilateral;
                public float sharpen;
                public Vector4 texelSize;
            }

            class ResolveData
            {
                public TextureHandle current;
                public TextureHandle motion;
                // Cross-frame persistent history is sampled directly. Import only the current frame's write target;
                // Unity 6/Metal can return black for an imported texture with no producer in this frame's graph.
                public Texture history;
                public Material material;
                public Vector4 volumeTexelSize;
                public float blend;
                public float reproject;
                public int interleaveMode;
                public int interleavePhase;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var renderingData = frameData.Get<UniversalRenderingData>();

                // Combined dirty-version across containers + container-free cache renderers → drop stale temporal history.
                int settingsVersion = BrazeContainer.RenderSettingsVersion + BrazeVolumeRegistry.Version;
                if (seenRenderSettingsVersion != settingsVersion)
                {
                    ResetHistory();
                    seenRenderSettingsVersion = settingsVersion;
                }

                // --- Volume shadow: each container's smoke/fire density blocks the key light onto opaque geometry
                // (ground + 3D objects under the plume). Multiply-darkens the scene colour. Runs BEFORE the fire
                // light (shadow first, then the fire adds its glow on top) and before the volume composites. Same
                // injection material as the fire light, shader pass 1. ---
                if ((BrazeContainer.AnyShadowCasting() || BrazeVolumeRegistry.AnyShadowCasting())
                    && resources.cameraDepthTexture.IsValid())
                {
                    var shadowMats = _shadowMats;
                    shadowMats.Clear();
                    var containers = BrazeContainer.Active;
                    for (int i = 0; i < containers.Count; i++)
                    {
                        var c = containers[i];
                        if (c != null && c.ShadowCastActive && c.FireLightMaterial != null)
                            shadowMats.Add(c.FireLightMaterial);
                    }
                    BrazeVolumeRegistry.CollectShadowMaterials(shadowMats);   // + container-free cache renderers
                    if (shadowMats.Count > 0)
                    {
                        using (var builder = renderGraph.AddRasterRenderPass<FireLightData>("BrazeFX Volume Shadow", out var data))
                        {
                            data.materials = shadowMats;
                            builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                            builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                            builder.AllowPassCulling(false);
                            builder.AllowGlobalStateModification(true);
                            builder.SetRenderFunc((FireLightData d, RasterGraphContext ctx) =>
                            {
                                for (int i = 0; i < d.materials.Count; i++)
                                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.materials[i], 1, MeshTopology.Triangles, 3, 1, null);
                            });
                        }
                    }
                }

                // --- Fire scene light: inject each container's fire emission onto opaque geometry. Runs BEFORE the
                // volume composite (additive onto scene colour), so the smoke/fire then draws over the freshly-lit
                // scene. Reads scene depth to reconstruct world position; each container's material carries its own
                // fire field + moments buffer (configured in BrazeContainer.ApplyFireLight). ---
                if ((BrazeContainer.AnyFireLighting() || BrazeVolumeRegistry.AnyFireLighting()) && resources.cameraDepthTexture.IsValid())
                {
                    var fireMats = _fireMats;
                    fireMats.Clear();
                    var containers = BrazeContainer.Active;
                    for (int i = 0; i < containers.Count; i++)
                    {
                        var c = containers[i];
                        if (c != null && c.FireLightActive && c.FireLightMaterial != null)
                            fireMats.Add(c.FireLightMaterial);
                    }
                    BrazeVolumeRegistry.CollectFireLightMaterials(fireMats);   // + container-free cache renderers
                    if (fireMats.Count > 0)
                    {
                        using (var builder = renderGraph.AddRasterRenderPass<FireLightData>("BrazeFX Fire Light", out var data))
                        {
                            data.materials = fireMats;
                            builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                            builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                            builder.AllowPassCulling(false);
                            builder.AllowGlobalStateModification(true);
                            builder.SetRenderFunc((FireLightData d, RasterGraphContext ctx) =>
                            {
                                for (int i = 0; i < d.materials.Count; i++)
                                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.materials[i], 0, MeshTopology.Triangles, 3, 1, null);
                            });
                        }
                    }
                }

                // Skip the raymarch + composite when nothing renders THROUGH this feature (the raymarch draws only
                // "BrazeVolume"-tagged sources: containers and container-free cache renderers).
                if (!BrazeContainer.AnyVolumeRendering() && !BrazeVolumeRegistry.AnyRendering())
                    return;

                // Low-res color buffer for the raymarch. Downsample is the floor; the absolute height cap then
                // makes the (quadratic) raymarch RESOLUTION-INDEPENDENT — a 4K display marches the same ray count
                // as maxRayHeight, only the cheap fullscreen composite scales with the display.
                var camDesc = cameraData.cameraTargetDescriptor;
                var desc = camDesc;
                int rw = Mathf.Max(1, camDesc.width / down);
                int rh = Mathf.Max(1, camDesc.height / down);
                if (maxRayHeight > 0 && rh > maxRayHeight)
                {
                    rw = Mathf.Max(1, Mathf.RoundToInt(rw * (float)maxRayHeight / rh));
                    rh = maxRayHeight;
                }
                desc.width = rw;
                desc.height = rh;
                desc.depthBufferBits = 0;
                desc.depthStencilFormat = GraphicsFormat.None;
                // The volume buffer stores premultiplied RGB plus coverage in alpha. URP's 32-bit HDR camera
                // target can be an RGB-only packed float format, so do not inherit the camera graphicsFormat here:
                // sampling alpha from an RGB volume target reads as full coverage and composites dark volume over
                // opaque meshes. Keep the camera target free to use 32-bit HDR; only this half/quarter-res buffer
                // needs an alpha-capable HDR format.
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.msaaSamples = 1;
                TextureHandle half = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_BrazeVolumeHalf", false, FilterMode.Bilinear);

                bool temporal = temporalNoiseReduction > 0.001f;
                HistoryState historyState = temporal ? GetHistory(cameraData.camera, desc) : null;
                bool reproject = motionReproject && temporal;
                Matrix4x4 curVP = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix();
                Matrix4x4 prevVP = curVP;
                float cameraTravel = 0f;
                float cameraTurn = 0f;
                if (historyState != null)
                {
                    Camera cam = cameraData.camera;
                    float gap = historyState.vpValid ? Time.time - historyState.prevTime : float.PositiveInfinity;
                    bool cameraCut = !historyState.cameraPoseValid || gap > 0.25f;
                    if (cam != null && historyState.cameraPoseValid)
                    {
                        cameraTravel = Vector3.Distance(cam.transform.position, historyState.cameraPosition);
                        cameraTurn = Quaternion.Angle(cam.transform.rotation, historyState.cameraRotation);
                        cameraCut |= cameraTravel > 5f;
                        cameraCut |= cameraTurn > 45f;
                    }
                    if (cameraCut) historyState.resolveValid = false;

                    if (reproject && historyState.vpValid && !cameraCut)
                    {
                        prevVP = historyState.prevVP;
                    }
                    historyState.prevVP = curVP;
                    historyState.prevTime = Time.time;
                    historyState.vpValid = true;
                    if (cam != null)
                    {
                        historyState.cameraPosition = cam.transform.position;
                        historyState.cameraRotation = cam.transform.rotation;
                        historyState.cameraPoseValid = true;
                    }
                }
                int activeInterleave = reproject && historyState != null && historyState.resolveValid ? interleaveMode : 0;
                // More current samples during fast motion keep disocclusions from waiting four frames to refresh.
                if (activeInterleave == 2 && (cameraTravel > 0.25f || cameraTurn > 2f)) activeInterleave = 1;
                if (activeInterleave != 0 && (cameraTravel > 1f || cameraTurn > 8f)) activeInterleave = 0;
                int interleavePhase = InterleavePhaseForFrame(activeInterleave, Time.frameCount);

                // Motion-vector target (half-res RG). Always allocated + bound so the MRT raymarch shaders always
                // have a slot for SV_Target1; it stays 0 (and unread) when reprojection is off.
                var motionDesc = desc;
                motionDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                TextureHandle motion = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, motionDesc, "_BrazeVolumeMotion", false, FilterMode.Bilinear);

                // --- Pass 1: raymarch the volumes into the low-res buffer ---
                using (var builder = renderGraph.AddRasterRenderPass<VolumeData>("BrazeFX Volume", out var data))
                {
                    var sorting = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonTransparent };
                    var drawing = new DrawingSettings(Tag, sorting);
                    var filtering = new FilteringSettings(RenderQueueRange.transparent);
                    data.list = renderGraph.CreateRendererList(
                        new RendererListParams(renderingData.cullResults, drawing, filtering));
                    data.jitterFrame = temporalNoiseReduction > 0.001f && animatedRayJitter ? Time.frameCount & 1023 : 0f;
                    data.prevVP = prevVP;
                    data.reproject = reproject ? 1f : 0f;
                    data.interleaveMode = activeInterleave;
                    data.interleavePhase = interleavePhase;
                    data.raymarchSteps = raymarchSteps;
                    builder.UseRendererList(data.list);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(half, 0, AccessFlags.Write);
                    builder.SetRenderAttachment(motion, 1, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((VolumeData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                        ctx.cmd.SetGlobalFloat(RayJitterFrameId, d.jitterFrame);
                        ctx.cmd.SetGlobalMatrix(PrevVPId, d.prevVP);
                        ctx.cmd.SetGlobalFloat(ReprojectId, d.reproject);
                        ctx.cmd.SetGlobalInt(InterleaveModeId, d.interleaveMode);
                        ctx.cmd.SetGlobalInt(InterleavePhaseId, d.interleavePhase);
                        ctx.cmd.SetGlobalInt(GeneratedStepBudgetId, d.raymarchSteps);
                        ctx.cmd.DrawRendererList(d.list);
                    });
                }

                TextureHandle volumeForComposite = half;
                if (temporal)
                {
                    var history = historyState;
                    RTHandle previous = history.resolveFlip ? history.resolveB : history.resolveA;
                    RTHandle next = history.resolveFlip ? history.resolveA : history.resolveB;
                    // `previous` has no same-frame producer, so bind its persistent RenderTexture directly in the
                    // resolve. Invalid frames use blend 0 and return before the shader samples history.
                    TextureHandle nextHistory = renderGraph.ImportTexture(next);

                    // At native resolution the history and camera attachments are compatible. Resolve once, write
                    // history with replacement blending, and premultiplied-blend the same value onto camera color.
                    bool fuseResolveComposite = directComposite && desc.width == camDesc.width &&
                                                desc.height == camDesc.height && camDesc.msaaSamples == 1;
                    if (fuseResolveComposite)
                    {
                        using (var builder = renderGraph.AddRasterRenderPass<ResolveData>(
                                   "BrazeFX Volume Temporal Resolve + Composite", out var data))
                        {
                            data.current = half;
                            data.history = previous.rt;
                            data.motion = motion;
                            data.material = composite;
                            data.volumeTexelSize = new Vector4(1f / Mathf.Max(desc.width, 1),
                                                               1f / Mathf.Max(desc.height, 1), desc.width, desc.height);
                            data.blend = history.resolveValid ? temporalNoiseReduction : 0f;
                            data.reproject = reproject ? 1f : 0f;
                            data.interleaveMode = activeInterleave;
                            data.interleavePhase = interleavePhase;
                            builder.UseTexture(half, AccessFlags.Read);
                            builder.UseTexture(motion, AccessFlags.Read);
                            builder.SetRenderAttachment(nextHistory, 0, AccessFlags.Write);
                            builder.SetRenderAttachment(resources.activeColorTexture, 1, AccessFlags.ReadWrite);
                            builder.AllowPassCulling(false);
                            builder.SetRenderFunc((ResolveData d, RasterGraphContext ctx) =>
                            {
                                var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                                props.SetTexture(VolumeCurrentId, d.current);
                                props.SetTexture(VolumeHistoryId, d.history);
                                props.SetTexture(MotionTextureId, d.motion);
                                props.SetVector(BlitScaleBiasId, FullscreenScaleBias);
                                props.SetVector(VolumeTexelSizeId, d.volumeTexelSize);
                                props.SetFloat(TemporalBlendId, d.blend);
                                props.SetFloat(ReprojectId, d.reproject);
                                props.SetInt(InterleaveModeId, d.interleaveMode);
                                props.SetInt(InterleavePhaseId, d.interleavePhase);
                                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material,
                                    TemporalResolveCompositeBlendPass, MeshTopology.Triangles, 3, 1, props);
                            });
                        }
                        history.resolveFlip = !history.resolveFlip;
                        history.resolveValid = true;
                        return;
                    }

                    using (var builder = renderGraph.AddRasterRenderPass<ResolveData>("BrazeFX Volume Temporal Resolve", out var data))
                    {
                        data.current = half;
                        data.history = previous.rt;
                        data.motion = motion;
                        data.material = composite;
                        data.volumeTexelSize = new Vector4(1f / Mathf.Max(desc.width, 1),
                                                           1f / Mathf.Max(desc.height, 1),
                                                           desc.width, desc.height);
                        data.blend = history.resolveValid ? temporalNoiseReduction : 0f;
                        data.reproject = reproject ? 1f : 0f;
                        data.interleaveMode = activeInterleave;
                        data.interleavePhase = interleavePhase;

                        builder.UseTexture(half, AccessFlags.Read);
                        builder.UseTexture(motion, AccessFlags.Read);
                        builder.SetRenderAttachment(nextHistory, 0, AccessFlags.Write);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((ResolveData d, RasterGraphContext ctx) =>
                        {
                            var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                            props.SetTexture(VolumeCurrentId, d.current);
                            props.SetTexture(VolumeHistoryId, d.history);
                            props.SetTexture(MotionTextureId, d.motion);
                            props.SetVector(BlitScaleBiasId, FullscreenScaleBias);
                            props.SetVector(VolumeTexelSizeId, d.volumeTexelSize);
                            props.SetFloat(TemporalBlendId, d.blend);
                            props.SetFloat(ReprojectId, d.reproject);
                            props.SetInt(InterleaveModeId, d.interleaveMode);
                            props.SetInt(InterleavePhaseId, d.interleavePhase);
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, TemporalResolvePass, MeshTopology.Triangles, 3, 1, props);
                        });
                    }

                    volumeForComposite = nextHistory;
                    history.resolveFlip = !history.resolveFlip;
                    history.resolveValid = true;
                }

                if (directComposite)
                {
                    // Single blend pass: composite the premultiplied half-res volume straight onto the camera
                    // color. ReadWrite loads the existing framebuffer so the hardware "Blend One OneMinusSrcAlpha"
                    // has a destination to blend against — no full-res scene copy needed.
                    bool useBilateral = bilateralUpsample && desc.width < camDesc.width;
                    using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("BrazeFX Composite (Blend)", out var data))
                    {
                        data.volume = volumeForComposite;
                        data.material = composite;
                        data.bilateral = useBilateral;
                        data.sharpen = reconstructionSharpness;
                        data.texelSize = new Vector4(1f / Mathf.Max(desc.width, 1), 1f / Mathf.Max(desc.height, 1),
                                                     desc.width, desc.height);
                        builder.UseTexture(volumeForComposite, AccessFlags.Read);
                        // Depth-guided upsample reads scene depth to keep the volume's occlusion edge crisp.
                        if (useBilateral && resources.cameraDepthTexture.IsValid())
                            builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                        builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                        {
                            var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                            props.SetTexture(BlitTextureId, d.volume);
                            props.SetVector(BlitScaleBiasId, FullscreenScaleBias);
                            props.SetVector(VolumeTexelSizeId, d.texelSize);
                            props.SetFloat(BilateralId, d.bilateral ? 1f : 0f);
                            props.SetFloat(ReconstructionSharpnessId, d.sharpen);
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, CompositeBlendPass, MeshTopology.Triangles, 3, 1, props);
                        });
                    }
                    return;
                }

                var sceneDesc = renderGraph.GetTextureDesc(resources.activeColorTexture);
                sceneDesc.name = "_BrazeVolumeSceneCopy";
                sceneDesc.clearBuffer = false;
                TextureHandle sceneCopy = renderGraph.CreateTexture(sceneDesc);

                // --- Scene copy: the composite never depends on target load/blend state ---
                using (var builder = renderGraph.AddRasterRenderPass<CopyData>("BrazeFX Scene Copy", out var data))
                {
                    data.source = resources.activeColorTexture;
                    builder.UseTexture(data.source, AccessFlags.Read);
                    builder.SetRenderAttachment(sceneCopy, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((CopyData d, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, d.source, FullscreenScaleBias, 0.0f, false);
                    });
                }

                // --- Composite the low-res buffer over the copied camera color ---
                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("BrazeFX Composite", out var data))
                {
                    data.volume = volumeForComposite;
                    data.scene = sceneCopy;
                    data.material = composite;
                    builder.UseTexture(volumeForComposite, AccessFlags.Read);
                    builder.UseTexture(sceneCopy, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                    {
                        var props = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                        props.SetTexture(BlitTextureId, d.volume);
                        props.SetTexture(SceneColorId, d.scene);
                        props.SetVector(BlitScaleBiasId, FullscreenScaleBias);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, CompositePass, MeshTopology.Triangles, 3, 1, props);
                    });
                }
            }
        }
    }
}
