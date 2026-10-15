using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace BrazeFX
{
    public sealed partial class BrazeContainer
    {
        [Header("Live Slice Renderer")]
        [Tooltip("Empty-space skipping: bakes a coarse occupancy volume so the renderer leaps through air where there is no visible smoke/fire.")]
        public bool sliceEmptySpaceSkip = true;
        [Tooltip("Cheap half-step smoke slice that fills gaps only when the slice budget stretches the march step.")]
        public bool sliceVirtualFill = true;
        [Range(0f, 1f), Tooltip("How strongly the half-step slice lifts missing smoke between real slice samples.")]
        public float sliceVirtualFillStrength = 0.75f;
        [Range(0f, 1f), Tooltip("0 = softer half-step support, 1 = stronger max-density support for sparse wisps. Higher values fill more aggressively but can look softer.")]
        public float sliceVirtualFillSharpness = 0.35f;
        public enum SliceVirtualFillView { Both, Slices, GapFill }
        [Tooltip("Choose whether the renderer shows normal slices, the cheap gap fill, or both.")]
        public SliceVirtualFillView sliceVirtualFillView = SliceVirtualFillView.Both;

        // (The pyro-froxel native look block that lived here — extinction, scatter albedo, fire
        // intensity/power/rolloff/opacity, a slice-local fire colour mode/gradient/temperature range,
        // ambient occlusion, powder, sooty smoke colours — was removed 2026-07-10. The "restored old
        // smoke/fire response" rework replaced that look math with the legacy transfer functions
        // (_SmokePreint/_FirePreint + the shared BrazeRenderSettings fields), leaving those uniforms
        // declared-but-never-sampled in BrazeSlicesCore.hlsl. Do not reintroduce knobs here without
        // wiring them into the shader first.)

        [Header("Live Slice Scattering")]
        [Range(-0.9f, 0.9f), Tooltip("Scattering anisotropy. Positive = stronger silver lining toward the sun.")]
        public float slicePhaseG = 0.45f;
        [Range(0f, 1f), Tooltip("Cheap multiple-scattering fill for dense smoke cores.")]
        public float sliceMultiScatter = 0.5f;

        [Header("Live Slice Self Shadowing")]
        [Tooltip("Bake light-direction transmittance into a 3D texture and sample it during slice rendering.")]
        public bool sliceSelfShadows = true;
        public enum SliceShadowResolution { Half, Full }
        public SliceShadowResolution sliceShadowResolution = SliceShadowResolution.Half;
        [Range(1, 8), Tooltip("Rebake the shadow volume every N frames.")]
        public int sliceShadowFrameInterval = 2;
        [Range(4, 64), Tooltip("Light-march taps per voxel in the shadow bake. Higher values smooth the internal shadow layers that otherwise read as paper/cardboard sheets.")]
        public int sliceShadowSteps = 16;
        [Range(0.25f, 4f), Tooltip("Multiplier on smoke extinction for the shadow bake.")]
        public float sliceShadowDensity = 1.5f;
        [Range(0f, 1f), Tooltip("Strength of the self-shadow contribution.")]
        public float sliceSelfShadowStrength = 0.7f;
        [Range(0f, 1f), Tooltip("How much directional self-shadow also darkens ambient/fill light.")]
        public float sliceShadowAmbientAmount = 0.6f;
        [Range(0f, 2f), Tooltip("Fallback slice-depth darkening when self shadows are off.")]
        public float sliceSelfShadowDepthScale = 1f;

        [Header("Live Slice Cast Shadows")]
        [Tooltip("Project the smoke shadow onto opaque scene geometry using the baked shadow volume.")]
        public bool sliceCastShadows = false;
        [Range(0f, 1f), Tooltip("Strength of the shadow cast onto scene geometry.")]
        public float sliceCastShadowStrength = 0.5f;
        [Range(0.5f, 8f), Tooltip("Distance in container sizes over which the cast shadow fades out.")]
        public float sliceCastShadowFade = 2f;
        [Range(0f, 3f), Tooltip("Penumbra filter radius in shadow voxels.")]
        public float sliceCastShadowSoftness = 1f;

        [Header("Live Slice Smoke Fill")]
        [ColorUsage(false, true), Tooltip("Extra HDR fill light for smoke.")]
        public Color sliceSmokeFillColor = new Color(0.5f, 0.55f, 0.62f);
        [Range(0f, 4f), Tooltip("Strength of the smoke fill light.")]
        public float sliceSmokeFillStrength = 0.5f;

        [Header("Live Slice Crispness")]
        [Range(0f, 1f), Tooltip("Fine-field anti-voxel reconstruction. Rotates the body/shadow filter per object-space voxel so close-up fire stops showing one unified grid while wispy edges stay sharp.")]
        public float sliceSourceFilterStrength = 0.45f;
        [Range(0.5f, 2.5f), Tooltip("Fine-field anti-voxel filter radius in fine render voxels.")]
        public float sliceSourceFilterRadius = 1.25f;
        [Range(0.5f, 3f), Tooltip(">1 steepens smoke density edges for crisper silhouettes.")]
        public float sliceDensityContrast = 1.15f;
        [Range(0.05f, 4f), Tooltip("Metres over which the volume fades into intersecting scene geometry.")]
        public float sliceDepthSoftness = 0.75f;
        [Range(0.01f, 0.25f), Tooltip("Soft fade against container walls.")]
        public float sliceEdgeFade = 0.07f;

        [Header("Live Slice Anti-Banding")]
        [Range(0f, 1f), Tooltip("Randomizes each ray's start phase by this fraction of a march step so neighbouring pixels don't sample the same view-depth planes. It breaks up raymarch banding, but does not filter the underlying voxel field (scalar reconstruction handles that automatically). 0 = off; ~0.5 balances clean coverage against visible grain.")]
        public float sliceJitterStrength = 0.5f;
        [Range(0.1f, 4f), Tooltip("Feature size of the ray-start dither. Low = coarser breakup; 1 = one value per working-resolution pixel. Legacy values above 1 are safely capped at the pixel Nyquist limit to avoid bright-fire alias patterns.")]
        public float sliceJitterScale = 1f;
        [Range(0f, 1f), Tooltip("Fills thin jitter holes with the volume's own carried smoke/fire colour. 0 = physical volume only, 1 = strongest pyro-colour repair on partial-opacity jitter pixels.")]
        public float sliceJitterSceneBlend = 0.65f;
        [Range(0f, 1f), Tooltip("Fills alpha speckles left by jitter using the volume's own carried smoke/fire colour. Does not reduce jitter; higher values add more coverage repair.")]
        public float sliceJitterDenoise = 0.55f;

        [Header("Live Slice Field Adaptation")]
        [Tooltip("Maps BrazeFX raw density into the slice renderer's smoke domain.")]
        public float sliceDensityGain = 10f;
        [Tooltip("Rate of the saturating burn to fire-temperature map.")]
        public float sliceFireGain = 0.5f;

        public enum SliceRenderDebugView { Off, RawSmoke, ShapedSmoke, Temperature, Occupancy, GapFill, SelfShadow }
        [Header("Live Slice Diagnostics")]
        public SliceRenderDebugView sliceRenderDebugView = SliceRenderDebugView.Off;

        ComputeShader sliceBakeShader;
        int sliceShadowKernel = -1, sliceSculptKernel = -1, sliceClearSculptKernel = -1, sliceClearRetiredSculptKernel = -1;
        RenderTexture sliceShadowTex;
        RenderTexture sliceSculptTex;   // baked Definition sculpt (r = ndl, g = gradient weight), state res
        Texture2D sliceSmokePreint;
        Texture2D sliceFirePreint;
        Texture2D sliceFireCurveLut;
        int sliceTransferHash;
        Vector3Int allocatedSliceShadowRes = Vector3Int.zero;
        Vector3Int allocatedSliceSculptRes = Vector3Int.zero;

        // ── Bake dirty-gating ── the three bakes are pure functions of (render-field content, light dir,
        // a few knobs). The sim clock is decoupled from render, so on high-Hz displays MOST rendered frames
        // run zero solver steps — re-dispatching the bakes on those frames recomputes byte-identical texels
        // (the shadow march alone is ~0.5–1.5 ms GPU). sliceFieldVersion is bumped by StepSimulation and
        // UploadCacheFrame; every other input is folded into a per-bake key, so any change (animated sun,
        // knob edit, resize) still rebakes that frame.
        internal int sliceFieldVersion = 1;
        int sliceShadowBakedKey, sliceSculptBakedKey;   // 0 = never baked → forces a bake
        bool sliceShadowDirtyLatch = true;   // carries dirtiness across sliceShadowFrameInterval frames
        static readonly ProfilerMarker MarkSliceShadowBake = new("BrazeFX.SliceShadowBake");
        static readonly ProfilerMarker MarkSliceSculptBake = new("BrazeFX.SliceSculptBake");

        static int SliceKeyCombine(int h, int v) => unchecked(h * 397 ^ v);
        static int SliceKeyF(int h, float v) => SliceKeyCombine(h, v.GetHashCode());
        static int SliceKeyV(int h, Vector3 v)
        {
            h = SliceKeyF(h, v.x); h = SliceKeyF(h, v.y); return SliceKeyF(h, v.z);
        }
        Vector3Int sliceShadowRes = Vector3Int.zero;
        int sliceShadowFrameCount;

        public static BrazeContainer ActiveSliceRenderer
        {
            get
            {
                for (int i = 0; i < Active.Count; i++)
                {
                    var c = Active[i];
                    if (c != null && c.SliceRenderReady) return c;
                }
                return null;
            }
        }

        /// <summary>Collect ALL containers currently ready to draw via the slice renderer (live sims + cache-playback
        /// hosts). The URP feature records one pass per entry, so multiple volumes — e.g. duplicated cache players or a
        /// live container plus a playing clip — all composite, instead of only the first.</summary>
        public static void CollectSliceRenderers(List<BrazeContainer> into, Camera camera = null)
        {
            into.Clear();
            if (BrazeCameraRenderIsolation.TryGetContainer(camera, out BrazeContainer exclusive))
            {
                if (exclusive != null && exclusive.SliceRenderReady) into.Add(exclusive);
                return;
            }
            for (int i = 0; i < Active.Count; i++)
            {
                var c = Active[i];
                if (c != null && c.SliceRenderReady) into.Add(c);
            }
        }

        public RenderTexture SliceRenderStateTexture => FluidRenderField;
        // Live and cache Render Detail both use a render-only warp volume. Live bakes it from flow; production cache
        // playback scatters the complete already-baked native-resolution texture. Compatibility DetailFlow clips may rebuild it.
        // No path needs the old transported fine density/burn field.
        public RenderTexture SliceFineTexture => null;
        public RenderTexture SliceWarpTexture => renderVolume && solver != null &&
            (cachePlaybackActive ? solver.WarpField != null : renderDetail) ? solver.WarpField : null;
        /// <summary>Live simulation velocity can coherently carry the stochastic ray-start pattern. Reconstructed
        /// VDB motion changes independently for every stored frame pair, so multiplying it by absolute render time
        /// makes the pattern jump even when the scalar frames are smooth. VDB motion remains available to detail and
        /// temporal reprojection; only this high-frequency sample-pattern animation is disabled.</summary>
        public bool SliceVelocityJitterScroll => !cachePlaybackActive || !cachePlaybackVdbReconstruction.enabled;
        /// <summary>Analytic procedural-phase advance since the last live warp bake. Sampling the existing warp at
        /// this small UV offset turns an N-tick hold into continuous motion without a history texture or second tap.</summary>
        public float SliceWarpSamplePhase => !cachePlaybackActive && renderVolume && renderDetail &&
            solver != null && solver.WarpField != null
                ? CalculateWarpSamplePhase(SliceAnimationTime * 0.15f, detailLastBakeTime, detailScale)
                : 0f;
        internal static float CalculateWarpSamplePhase(float currentDetailTime, float bakedDetailTime, float frequency)
            => Mathf.Max(0f, currentDetailTime - bakedDetailTime) / Mathf.Max(2f, frequency);
        public RenderTexture SliceShadowTexture => (renderVolume && !CfdSmokePreview &&
            (sliceSelfShadows || sliceCastShadows)) ? sliceShadowTex : null;
        public RenderTexture SliceOccupancyTexture => (renderVolume && sliceEmptySpaceSkip) ? FluidRenderOccupancy : null;
        /// <summary>Baked Definition sculpt volume (KSculpt). Null when lighting/Definition are off — the render
        /// feature forces _Definition to 0 in that case so the shader never samples an unbound texture.</summary>
        public RenderTexture SliceSculptTexture => renderVolume && !CfdSmokePreview ? sliceSculptTex : null;
        internal bool CfdSmokePreview => CfdVisualization != null && CfdVisualization.SmokeEnabled;
        public Texture2D SliceSmokePreint { get { EnsureSliceTransferLuts(); return sliceSmokePreint; } }
        public Texture2D SliceFirePreint { get { EnsureSliceTransferLuts(); return sliceFirePreint; } }
        public Texture2D SliceFireCurveLut { get { EnsureSliceTransferLuts(); return sliceFireCurveLut; } }
#if UNITY_EDITOR
        [System.NonSerialized] internal bool editorRenderSuppressed;
#endif
        // A CFD presentation facade with its dye preview OFF (analysis-slice mode) must not submit the volume:
        // residual dye would otherwise render through the FIRE grey-smoke LUT and haunt the instrument view.
        // A domain-scoped Heat Map keeps the volume alive — it is its own volumetric view.
        bool CfdVolumeSuppressed => CfdVisualization != null && !CfdVisualization.SmokeEnabled && CfdHeatMap == null;

        public bool SliceRenderReady => isActiveAndEnabled && renderVolume && !CfdVolumeSuppressed &&
#if UNITY_EDITOR
                                        !editorRenderSuppressed &&
#endif
                                        SliceRenderStateTexture != null;
        public Matrix4x4 SliceWorldToSimUVW => WorldToFluidUVW();

        internal BrazeSliceRenderSettings CaptureSliceRenderSettings() => new BrazeSliceRenderSettings
        {
            version = 1,
            emptySpaceSkip = sliceEmptySpaceSkip,
            virtualFill = sliceVirtualFill,
            virtualFillStrength = sliceVirtualFillStrength,
            virtualFillSharpness = sliceVirtualFillSharpness,
            virtualFillView = sliceVirtualFillView,
            phaseG = slicePhaseG,
            multiScatter = sliceMultiScatter,
            selfShadows = sliceSelfShadows,
            shadowResolution = sliceShadowResolution,
            shadowFrameInterval = sliceShadowFrameInterval,
            shadowSteps = sliceShadowSteps,
            shadowDensity = sliceShadowDensity,
            selfShadowStrength = sliceSelfShadowStrength,
            shadowAmbientAmount = sliceShadowAmbientAmount,
            selfShadowDepthScale = sliceSelfShadowDepthScale,
            castShadows = sliceCastShadows,
            castShadowStrength = sliceCastShadowStrength,
            castShadowFade = sliceCastShadowFade,
            castShadowSoftness = sliceCastShadowSoftness,
            smokeFillColor = sliceSmokeFillColor,
            smokeFillStrength = sliceSmokeFillStrength,
            sourceFilterStrength = sliceSourceFilterStrength,
            sourceFilterRadius = sliceSourceFilterRadius,
            densityContrast = sliceDensityContrast,
            depthSoftness = sliceDepthSoftness,
            edgeFade = sliceEdgeFade,
            jitterStrength = sliceJitterStrength,
            jitterScale = sliceJitterScale,
            jitterSceneBlend = sliceJitterSceneBlend,
            jitterDenoise = sliceJitterDenoise,
            densityGain = sliceDensityGain,
            fireGain = sliceFireGain,
        };

        void ApplySliceRenderSettings(in BrazeSliceRenderSettings s)
        {
            if (s.version <= 0) return;
            sliceEmptySpaceSkip = s.emptySpaceSkip;
            sliceVirtualFill = s.virtualFill;
            sliceVirtualFillStrength = s.virtualFillStrength;
            sliceVirtualFillSharpness = s.virtualFillSharpness;
            sliceVirtualFillView = s.virtualFillView;
            slicePhaseG = s.phaseG;
            sliceMultiScatter = s.multiScatter;
            sliceSelfShadows = s.selfShadows;
            sliceShadowResolution = s.shadowResolution;
            sliceShadowFrameInterval = s.shadowFrameInterval;
            sliceShadowSteps = s.shadowSteps;
            sliceShadowDensity = s.shadowDensity;
            sliceSelfShadowStrength = s.selfShadowStrength;
            sliceShadowAmbientAmount = s.shadowAmbientAmount;
            sliceSelfShadowDepthScale = s.selfShadowDepthScale;
            sliceCastShadows = s.castShadows;
            sliceCastShadowStrength = s.castShadowStrength;
            sliceCastShadowFade = s.castShadowFade;
            sliceCastShadowSoftness = s.castShadowSoftness;
            sliceSmokeFillColor = s.smokeFillColor;
            sliceSmokeFillStrength = s.smokeFillStrength;
            sliceSourceFilterStrength = s.sourceFilterStrength;
            sliceSourceFilterRadius = s.sourceFilterRadius;
            sliceDensityContrast = s.densityContrast;
            sliceDepthSoftness = s.depthSoftness;
            sliceEdgeFade = s.edgeFade;
            sliceJitterStrength = s.jitterStrength;
            sliceJitterScale = s.jitterScale;
            sliceJitterSceneBlend = s.jitterSceneBlend;
            sliceJitterDenoise = s.jitterDenoise;
            sliceDensityGain = s.densityGain;
            sliceFireGain = s.fireGain;
            ValidateSliceRendererSettings();
        }

        public void GetSliceWorldCorners(Vector3[] corners8)
        {
            Transform t = transform;
            Vector3 h = WorldSize() * 0.5f;
            int i = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        corners8[i++] = t.position + t.rotation * Vector3.Scale(h, new Vector3(x, y, z));
        }

        Light sliceCachedDirLight;   // caches the scene directional-light fallback so the shadow bake skips a per-frame FindObjectsByType scan

        public Vector3 SliceMainLightDirection()
        {
            if (TryGetCacheKeyLight(out Vector3 cacheDirection, out _))
                return cacheDirection;
            Light key = sun;
            if (key == null || key.type != LightType.Directional)
                key = RenderSettings.sun;
            if (key == null || key.type != LightType.Directional)
                key = ResolveSceneDirectionalLight();
            return key != null ? -key.transform.forward : new Vector3(0.3f, 0.9f, 0.2f).normalized;
        }

        public Color SliceMainLightColor()
        {
            if (TryGetCacheKeyLight(out _, out Color cacheColor))
                return cacheColor;
            Light key = sun;
            if (key == null || key.type != LightType.Directional) key = RenderSettings.sun;
            if (key == null || key.type != LightType.Directional) key = ResolveSceneDirectionalLight();
            return key != null
                ? key.color.linear * Mathf.Max(0f, key.intensity)
                : new Color(1f, 0.96f, 0.9f, 1f);
        }

        // FindObjectsByType allocates and scans every Light in the scene; only re-run it when the cached
        // fallback light is gone/disabled/no-longer-directional, so the steady state is a single ref check.
        Light ResolveSceneDirectionalLight()
        {
            if (sliceCachedDirLight != null && sliceCachedDirLight.isActiveAndEnabled &&
                sliceCachedDirLight.type == LightType.Directional)
                return sliceCachedDirLight;
            sliceCachedDirLight = null;
#if UNITY_6000_5_OR_NEWER
            foreach (var l in FindObjectsByType<Light>())
#else
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
#endif
                if (l.type == LightType.Directional && l.isActiveAndEnabled) { sliceCachedDirLight = l; break; }
            return sliceCachedDirLight;
        }

        void ValidateSliceRendererSettings()
        {
            sliceShadowFrameInterval = Mathf.Clamp(sliceShadowFrameInterval, 1, 8);
            sliceShadowSteps = Mathf.Clamp(sliceShadowSteps, 1, 64);
            sliceShadowDensity = Mathf.Clamp(sliceShadowDensity, 0.25f, 4f);
            sliceSelfShadowStrength = Mathf.Clamp01(sliceSelfShadowStrength);
            sliceShadowAmbientAmount = Mathf.Clamp01(sliceShadowAmbientAmount);
            sliceCastShadowFade = Mathf.Max(0.001f, sliceCastShadowFade);
            sliceVirtualFillStrength = Mathf.Clamp01(sliceVirtualFillStrength);
            sliceVirtualFillSharpness = Mathf.Clamp01(sliceVirtualFillSharpness);
            sliceJitterSceneBlend = Mathf.Clamp01(sliceJitterSceneBlend);
            sliceJitterDenoise = Mathf.Clamp01(sliceJitterDenoise);
        }

        void InitializeSliceRenderer()
        {
            ValidateSliceRendererSettings();
            if (sliceBakeShader == null)
                sliceBakeShader = Resources.Load<ComputeShader>("BrazeSliceBake");
            sliceShadowKernel = SliceKernelOrMinusOne(sliceBakeShader, "KShadow");
            sliceSculptKernel = SliceKernelOrMinusOne(sliceBakeShader, "KSculpt");
            sliceClearSculptKernel = SliceKernelOrMinusOne(sliceBakeShader, "KClearSculpt");
            sliceClearRetiredSculptKernel = SliceKernelOrMinusOne(sliceBakeShader, "KClearRetiredSculpt");
        }

        void ReleaseSliceRenderer()
        {
            ReleaseSliceBakes();
            if (sliceSmokePreint != null) { DestroyObj(sliceSmokePreint); sliceSmokePreint = null; }
            if (sliceFirePreint != null) { DestroyObj(sliceFirePreint); sliceFirePreint = null; }
            if (sliceFireCurveLut != null) { DestroyObj(sliceFireCurveLut); sliceFireCurveLut = null; }
            sliceTransferHash = 0;
        }

        void ReleaseSliceBakes()
        {
            ReleaseSliceTexture(ref sliceShadowTex);
            ReleaseSliceTexture(ref sliceSculptTex);
            allocatedSliceShadowRes = Vector3Int.zero;
            allocatedSliceSculptRes = Vector3Int.zero;
            sliceShadowRes = Vector3Int.zero;
            sliceShadowBakedKey = 0;
            sliceSculptBakedKey = 0;
            sliceShadowDirtyLatch = true;
        }

        void UpdateSliceRenderer()
        {
            if (!renderVolume)
            {
                ReleaseSliceBakes();
                return;
            }

            // Passive CFD dye deliberately reuses the mature volume raymarch, but it does not need Fire lighting,
            // definition, or a full-resolution shadow volume. Releasing those bakes avoids hidden CFD GPU work.
            if (CfdSmokePreview)
            {
                ReleaseSliceBakes();
                return;
            }

            RenderTexture state = SliceRenderStateTexture;
            if (state == null)
                return;

            if (sliceBakeShader == null)
                InitializeSliceRenderer();
            if (sliceBakeShader == null)
                return;

            if (sliceShadowKernel < 0 && sliceBakeShader.HasKernel("KShadow"))
                sliceShadowKernel = sliceBakeShader.FindKernel("KShadow");
            if (sliceSculptKernel < 0 && sliceBakeShader.HasKernel("KSculpt"))
                sliceSculptKernel = sliceBakeShader.FindKernel("KSculpt");
            if (sliceClearSculptKernel < 0 && sliceBakeShader.HasKernel("KClearSculpt"))
                sliceClearSculptKernel = sliceBakeShader.FindKernel("KClearSculpt");
            if (sliceClearRetiredSculptKernel < 0 && sliceBakeShader.HasKernel("KClearRetiredSculpt"))
                sliceClearRetiredSculptKernel = sliceBakeShader.FindKernel("KClearRetiredSculpt");

            EnsureSliceShadowTexture(state);
            if (((bakeLighting && sliceSelfShadows) || sliceCastShadows) && sliceShadowKernel >= 0 && sliceShadowTex != null)
            {
                Vector3 ws = WorldSize();
                Vector3 lightLocal = (Quaternion.Inverse(transform.rotation) * SliceMainLightDirection()).normalized;
                bool occLive = SliceOccupancyTexture != null;
                // Everything the march reads: field content, light dir, container size, step knobs, extinction,
                // and the occupancy skip volume (whose content is keyed by the gains).
                int key = SliceKeyCombine(29, sliceFieldVersion);
                key = SliceKeyV(key, lightLocal);
                key = SliceKeyV(key, ws);
                key = SliceKeyCombine(key, Mathf.Clamp(sliceShadowSteps, 1, 64));
                float shadowExtinction = Mathf.Max(0f, shadowStrength) *
                                         Mathf.Max(0f, sliceShadowDensity);
                key = SliceKeyF(key, shadowExtinction);
                key = SliceKeyF(key, Mathf.Max(0f, sliceDensityGain));
                key = SliceKeyCombine(key, occLive ? 1 : 0);
                key = SliceKeyCombine(key, sliceCastShadows ? 1 : 0);
                if (key != sliceShadowBakedKey) sliceShadowDirtyLatch = true;
                bool freshShadowTexture = sliceShadowBakedKey == 0;
                if (sliceShadowDirtyLatch && (freshShadowTexture ||
                    sliceShadowFrameCount % Mathf.Max(1, sliceShadowFrameInterval) == 0))
                {
                    using (MarkSliceShadowBake.Auto())
                        DispatchSliceShadow(state, ws, lightLocal, occLive);
                    sliceShadowBakedKey = key;
                    sliceShadowDirtyLatch = false;
                }
                sliceShadowFrameCount++;
            }

            // Definition sculpt bake: per-voxel replacement for what used to be a 6-tap per-SAMPLE gradient in
            // the raymarch loop. Only lives while the lit path actually consumes it.
            if (bakeLighting && definition > 1e-4f && sliceSculptKernel >= 0)
            {
                EnsureSliceSculptTexture(state);
                if (sliceSculptTex != null)
                {
                    Vector3 dirSim = SliceWorldToSimUVW.MultiplyVector(SliceMainLightDirection());
                    dirSim = dirSim.sqrMagnitude > 1e-12f ? dirSim.normalized : Vector3.up;
                    int key = SliceKeyV(SliceKeyCombine(31, sliceFieldVersion), dirSim);
                    if (key != sliceSculptBakedKey)
                    {
                        using (MarkSliceSculptBake.Auto())
                            DispatchSliceSculpt(state, dirSim);
                        sliceSculptBakedKey = key;
                    }
                }
            }
            else if (sliceSculptTex != null)
            {
                ReleaseSliceTexture(ref sliceSculptTex);
                allocatedSliceSculptRes = Vector3Int.zero;
                sliceSculptBakedKey = 0;
            }
        }

        void EnsureSliceSculptTexture(RenderTexture state)
        {
            Vector3Int desired = new Vector3Int(state.width, state.height, state.volumeDepth);
            if (sliceSculptTex != null && allocatedSliceSculptRes == desired)
                return;
            ReleaseSliceTexture(ref sliceSculptTex);
            allocatedSliceSculptRes = desired;
            sliceSculptBakedKey = 0;            // fresh texture → force a bake
            sliceSculptTex = CreateSliceVolume(desired, RenderTextureFormat.RGHalf, "BrazeSliceSculpt");
            if (sliceClearSculptKernel >= 0)
            {
                SliceSetInts3("_Res", desired.x, desired.y, desired.z);
                sliceBakeShader.SetTexture(sliceClearSculptKernel, "_SculptWrite", sliceSculptTex);
                sliceBakeShader.Dispatch(sliceClearSculptKernel,
                    Mathf.CeilToInt(desired.x / 4f), Mathf.CeilToInt(desired.y / 4f), Mathf.CeilToInt(desired.z / 4f));
            }
        }

        // dirSim = sun dir mapped through the SAME world→sim matrix the shader used per sample (its 3x3
        // includes non-uniform container scale, unlike the shadow bake's pure-rotation local dir), normalized.
        void DispatchSliceSculpt(RenderTexture state, Vector3 dirSim)
        {
            SliceSetInts3("_Res", state.width, state.height, state.volumeDepth);
            sliceBakeShader.SetVector("_SculptLightDirSim", dirSim);
            sliceBakeShader.SetTexture(sliceSculptKernel, "_StateRead", state);
            sliceBakeShader.SetTexture(sliceSculptKernel, "_SculptWrite", sliceSculptTex);
            if (solver != null && solver.ActiveBricksBuffer != null && solver.SparseIndirectArgs != null)
            {
                Vector3Int or = solver.OccRes;
                SliceSetInts3("_SculptOccRes", or.x, or.y, or.z);
                sliceBakeShader.SetBuffer(sliceSculptKernel, "_SculptBricks", solver.ActiveBricksBuffer);
                sliceBakeShader.DispatchIndirect(sliceSculptKernel, solver.SparseIndirectArgs, 0);

                if (sliceClearRetiredSculptKernel >= 0 && solver.RetiredBricksBuffer != null)
                {
                    sliceBakeShader.SetBuffer(sliceClearRetiredSculptKernel, "_SculptRetiredBricks", solver.RetiredBricksBuffer);
                    sliceBakeShader.SetTexture(sliceClearRetiredSculptKernel, "_SculptWrite", sliceSculptTex);
                    sliceBakeShader.DispatchIndirect(sliceClearRetiredSculptKernel, solver.SparseIndirectArgs, 12);
                }
            }
        }

        void DispatchSliceShadow(RenderTexture state, Vector3 ws, Vector3 lightLocal, bool occLive)
        {
            float maxDim = Mathf.Max(ws.x, Mathf.Max(ws.y, ws.z));

            sliceBakeShader.SetVector("_SimSize", ws);
            SliceSetInts3("_ShadowRes", sliceShadowRes.x, sliceShadowRes.y, sliceShadowRes.z);
            sliceBakeShader.SetVector("_ShadowLightDirLocal", lightLocal);
            sliceBakeShader.SetInt("_ShadowSteps", Mathf.Clamp(sliceShadowSteps, 1, 64));
            sliceBakeShader.SetFloat("_ShadowStepMeters", maxDim / Mathf.Max(1, sliceShadowSteps));
            float shadowExtinction = Mathf.Max(0f, shadowStrength) *
                                     Mathf.Max(0f, sliceShadowDensity);
            sliceBakeShader.SetFloat("_ShadowExtinction", shadowExtinction);
            // Match the production raymarch exactly: authored field adaptation, then its fixed transfer-domain remap.
            sliceBakeShader.SetFloat("_StateDensityGain", 3f * Mathf.Max(0f, sliceDensityGain));
            sliceBakeShader.SetTexture(sliceShadowKernel, "_StateRead", state);
            sliceBakeShader.SetTexture(sliceShadowKernel, "_ShadowWrite", sliceShadowTex);
            sliceBakeShader.SetInt("_ShadowUseOcc", occLive ? 1 : 0);
            sliceBakeShader.SetInt("_ShadowCastProjection", sliceCastShadows ? 1 : 0);
            sliceBakeShader.SetTexture(sliceShadowKernel, "_OccRead", occLive ? SliceOccupancyTexture : (Texture)state);
            sliceBakeShader.Dispatch(sliceShadowKernel,
                Mathf.CeilToInt(sliceShadowRes.x / 8f),   // KShadow is 8×4×4 (128 threads) for latency hiding
                Mathf.CeilToInt(sliceShadowRes.y / 4f),
                Mathf.CeilToInt(sliceShadowRes.z / 4f));
        }

        void EnsureSliceShadowTexture(RenderTexture state)
        {
            Vector3Int stateRes = new Vector3Int(state.width, state.height, state.volumeDepth);
            // Mirror the dispatch gate exactly (UpdateSliceRenderer): self-shadows only consume this texture when
            // bakeLighting is on, so allocating on sliceSelfShadows alone strands a full-res RHalf volume
            // (14 MB @192³) that is never written or sampled while lighting is A/B'd off.
            Vector3Int desired = ((bakeLighting && sliceSelfShadows) || sliceCastShadows) && sliceShadowKernel >= 0
                ? (sliceShadowResolution == SliceShadowResolution.Full
                    ? stateRes
                    : Vector3Int.Max(new Vector3Int(8, 8, 8), Vector3Int.CeilToInt((Vector3)stateRes * 0.5f)))
                : Vector3Int.zero;

            bool haveCorrect = desired == Vector3Int.zero
                ? sliceShadowTex == null
                : sliceShadowTex != null && allocatedSliceShadowRes == desired;
            if (haveCorrect)
                return;

            ReleaseSliceTexture(ref sliceShadowTex);
            allocatedSliceShadowRes = desired;
            sliceShadowRes = desired;
            sliceShadowBakedKey = 0;            // fresh (uninitialized) texture → force a bake
            sliceShadowDirtyLatch = true;
            if (desired != Vector3Int.zero)
                sliceShadowTex = CreateSliceVolume(desired, RenderTextureFormat.RHalf, "BrazeSliceShadow");
        }

        static RenderTexture CreateSliceVolume(Vector3Int res, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(res.x, res.y, 0, format)
            {
                name = name,
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = res.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        static void ReleaseSliceTexture(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroyObj(rt);
            rt = null;
        }

        // Zero-alloc SetInts for the slice bakes (params int[] allocates per call).
        static readonly int[] sliceIntsScratch = new int[3];
        void SliceSetInts3(string name, int x, int y, int z)
        {
            sliceIntsScratch[0] = x; sliceIntsScratch[1] = y; sliceIntsScratch[2] = z;
            sliceBakeShader.SetInts(name, sliceIntsScratch);
        }

        static int SliceKernelOrMinusOne(ComputeShader cs, string name)
        {
            if (cs == null || !cs.HasKernel(name))
            {
                if (cs != null)
                    Debug.LogWarning($"[BrazeFX] BrazeSliceBake.compute kernel '{name}' unavailable. Reimport the compute asset if this persists.");
                return -1;
            }
            return cs.FindKernel(name);
        }

        static int ComputeSliceGradientHash(Gradient g)
        {
            unchecked
            {
                int h = 17;
                if (g == null) return h;
                foreach (var k in g.colorKeys)
                    h = h * 31 + k.color.GetHashCode() * 31 + k.time.GetHashCode();
                foreach (var k in g.alphaKeys)
                    h = h * 31 + k.alpha.GetHashCode() * 31 + k.time.GetHashCode();
                h = h * 31 + (int)g.mode;
                return h;
            }
        }

        static int ComputeSliceCurveHash(AnimationCurve c)
        {
            unchecked
            {
                int h = 23;
                if (c == null) return h;
                for (int i = 0; i < c.length; i++)
                {
                    Keyframe k = c[i];
                    h = h * 31 + k.time.GetHashCode();
                    h = h * 31 + k.value.GetHashCode();
                    h = h * 31 + k.inTangent.GetHashCode();
                    h = h * 31 + k.outTangent.GetHashCode();
                }
                return h;
            }
        }

        int ComputeSliceTransferHash()
        {
            unchecked
            {
                int h = 29;
                h = h * 31 + ComputeSliceGradientHash(smokeAlbedoGradient);
                h = h * 31 + ComputeSliceCurveHash(smokeOpacityCurve);
                h = h * 31 + ComputeSliceCurveHash(fireEmissionCurve);
                h = h * 31 + ComputeSliceGradientHash(fireGradient);
                h = h * 31 + (int)fireColorMode;
                return h;
            }
        }

        int sliceLutCheckFrame = -1;   // hash-check once per frame: Gradient.colorKeys/alphaKeys ALLOCATE per access,
                                       // and the render feature hits the three LUT getters per volume per camera.

        void EnsureSliceTransferLuts()
        {
            if (sliceLutCheckFrame == Time.frameCount &&
                sliceSmokePreint != null && sliceFirePreint != null && sliceFireCurveLut != null)
                return;
            sliceLutCheckFrame = Time.frameCount;

            int hash = ComputeSliceTransferHash();
            if (sliceSmokePreint != null && sliceFirePreint != null && sliceFireCurveLut != null && hash == sliceTransferHash)
                return;

            BrazeRenderSettings look = CaptureRenderSettings();
            BrazeVolumeBind.BuildLuts(look, ref sliceSmokePreint, ref sliceFirePreint, ref sliceFireCurveLut);
            sliceTransferHash = hash;
        }
    }
}
