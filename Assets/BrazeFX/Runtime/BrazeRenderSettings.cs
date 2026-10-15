using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// The full APPEARANCE payload for the volume renderer — every smoke/fire/lighting/detail knob, decoupled from
    /// who drives the fields (live sim or cache). The look "travels with the clip": a <see cref="BrazeCacheBaker"/>
    /// captures the container's settings into the baked <see cref="BrazeFluidCacheAsset"/>; a
    /// <see cref="BrazeCachePlayer"/> then applies this look to the hidden container it drives, which renders through
    /// the same slice renderer live containers use. Players may also override the baked default per-instance.
    ///
    /// Field names/defaults mirror the matching BrazeContainer fields 1:1 (the container's CaptureRenderSettings
    /// copies into this; the renderer's bind reads from it). Scene-object references (the key Light) are NOT baked —
    /// the renderer resolves a key light at play time.
    /// </summary>
    [System.Serializable]
    public struct BrazeRenderSettings
    {
        // Legacy scheduling metadata remains serialized so existing cache manifests and scene assets round-trip
        // byte-for-byte. Braze Render Feature now owns final camera-space resolution and sampling.
        public int rendererStateVersion;
        public BrazeContainer.QualityMode qualityMode;
        public int customRenderStepBudget;
        public int customMemoryBudgetMB;

        // ── Channel toggles ──
        public bool renderSmoke;
        public bool renderFire;

        // ── Smoke ──
        public float densityScale;
        public float smokeDetail;
        public float smokeBrightness;
        public float smokeContrast;
        public float opacity;
        public int raymarchSteps;       // legacy render-quality field; retained for portable-format compatibility
        public bool adaptiveSteps;      // legacy render-quality field; retained for portable-format compatibility
        public int maxRaymarchHeight;   // legacy render-quality field; retained for portable-format compatibility
        // LUT sources (pre-integrated into the smoke transfer LUT at build time).
        public Gradient smokeAlbedoGradient;
        public AnimationCurve smokeOpacityCurve;

        // ── Fire ──
        public float fireScale;
        public float fireDensityScale;
        public float fireTempScale;
        public float fireDetail;
        public AnimationCurve fireEmissionCurve;
        public BrazeContainer.FireColorMode fireColorMode;
        public Gradient fireGradient;
        [ColorUsage(false, true)] public Color fireLightColor;

        // ── Lighting ──
        public bool bakeLighting;
        public float illumination;
        public float scattering;
        public float shadowStrength;
        public Color ambientColor;
        public float ambientIntensity;
        public float definition;
        // ── Generated-volume crispness (render-time, conforming, ~free; generated renderer only) ──
        public float crispen;        // 0 = off; blend toward a contrast-sharpened density (sharper edges, no new noise)
        public float crispenSharp;   // edge slope — higher steepens the soft falloff into a crisp surface
        public float crispenPivot;   // density level that becomes the crisp boundary
        public float smoothVoxels;   // solver-side de-voxel bake strength (approximating B-spline in BuildRenderField)
        public bool  emptySpaceSkip; // leap empty coarse cells while marching (sharper at equal cost / faster)
        public float gridLightStepVox;
        public int gridLightSteps;
        public int lightingDownsample;
        public int lightingInterval;    // rebake the light volumes every N sim steps (1 = every step)
        public float depthFeather;
        public float edgeFade;          // container-wall falloff (fraction of the box faded in from each face; 0 = hard box cut)

        // ── Fire Light ──
        public float fireLightIntensity;
        [ColorUsage(false, true)] public Color fireLightTint;
        public bool fireLightSmoke;
        public bool fireLight;
        public float fireLightRange;
        public float fireLightSoftness;
        public float fireLightShadow;
        public int fireLightSteps;

        // ── Cast Shadows ──
        public bool castShadows;
        public float castShadowStrength;
        public float castShadowDensity;
        public int castShadowSteps;

        // ── Receive Shadows (generated volume) — scene casters darken the cloud's key light ──
        public bool receiveShadows;

        // ── Render Detail ──
        // Container renderer/live sim: Render Detail is the restored flow-carried warp volume. Several older
        // fine-field knobs stay serialized for generated-volume detail and older saved looks.
        public bool renderDetail;
        public float detailBoost;      // Amount
        public float detailScale;      // Feature Size (container)
        public float detailSharpness;  // Sharpness (virtual residual amplitude)
        public float detailQuality;    // Detail Quality (virtual 2x-4x render sampling + extra noise octaves)
        public float detailCurl;       // Flow Coherence
        public float detailMotion;     // Motion
        public float detailSpeed;      // Speed
        public float detailResponse;   // Response
        public float detailPersistence; // Persistence
        public float detailCrisp;      // Crisp: flow-anchored hard-edged grain erosion strength (container)
        public float detailGrainMin;   // Grain Size Min — finest grain feature (voxels); high frequency
        public float detailGrainMax;   // Grain Size Max — coarsest grain feature (voxels); low frequency
        // Generated-volume render-time detail enhancer (BrazeVolume / BrazeGeneratedVolumeRaymarch): a static
        // world-space tiling-noise sharpness+warp layer, resolution-independent. The generator inspector exposes
        // these as normalized 0..1 controls, while the serialized fields remain in renderer units for compatibility.
        // Reads detailBoost=Amount, detailContrast=Sharpness, detailCurl=Warp, detailRoughness=grain,
        // detailSizeMin/Max=feature range.
        public float detailContrast;   // Sharpness (generated renderer)
        public float detailRoughness;  // grain octave (generated renderer)
        public float detailSizeMin;    // FINE grain size (world units) — anchors the finest baked octave
        public float detailSizeMax;    // BIGGEST feature size (world units) — derives the octave count
        public int   detailSeed;       // reroll the grain pattern
        public bool detailDebugCells;   // container: show created render detail as a debug color overlay
        public bool detailDebugCellsOnly; // container: hide original fume and show only created render detail
        public float detailDebugCutoff; // container: threshold for isolating the debug band
        public float detailStretch, detailFine, detailGrainCurl, detailEdge, detailBillow;
        public int detailRenderScale, detailMemoryBudgetMB, detailBakeInterval;

        // Current live/cache slice renderer payload. Version 0 means an older cache asset: the player keeps the
        // hidden container's proven slice defaults and applies the legacy fields above. New bakes capture version 1.
        public BrazeSliceRenderSettings slice;

        public const float GeneratedDetailMinFeatureSize = 0.03f;
        public const float GeneratedDetailMaxFeatureSize = 8f;
        public const float GeneratedDetailMinRoughness = 0.2f;
        public const float GeneratedDetailMaxRoughness = 0.9f;
        public const float GeneratedDetailMinSharpness = 1f;
        public const float GeneratedDetailMaxSharpness = 8f;
        public const int GeneratedDetailMaxSeed = 31;

        public static float GeneratedDetailClampFeatureSize(float worldUnits)
            => Mathf.Clamp(worldUnits, GeneratedDetailMinFeatureSize, GeneratedDetailMaxFeatureSize);

        public static float GeneratedDetailFeatureSizeFrom01(float t)
        {
            float a = Mathf.Log(GeneratedDetailMinFeatureSize);
            float b = Mathf.Log(GeneratedDetailMaxFeatureSize);
            return Mathf.Exp(Mathf.Lerp(a, b, Mathf.Clamp01(t)));
        }

        public static float GeneratedDetailFeatureSizeTo01(float worldUnits)
        {
            float a = Mathf.Log(GeneratedDetailMinFeatureSize);
            float b = Mathf.Log(GeneratedDetailMaxFeatureSize);
            return Mathf.InverseLerp(a, b, Mathf.Log(GeneratedDetailClampFeatureSize(worldUnits)));
        }

        public static float GeneratedDetailRoughnessFrom01(float t)
            => Mathf.Lerp(GeneratedDetailMinRoughness, GeneratedDetailMaxRoughness, Mathf.Clamp01(t));

        public static float GeneratedDetailRoughnessTo01(float roughness)
            => Mathf.InverseLerp(GeneratedDetailMinRoughness, GeneratedDetailMaxRoughness,
                Mathf.Clamp(roughness, GeneratedDetailMinRoughness, GeneratedDetailMaxRoughness));

        public static float GeneratedDetailSharpnessFrom01(float t)
            => Mathf.Lerp(GeneratedDetailMinSharpness, GeneratedDetailMaxSharpness, Mathf.Clamp01(t));

        public static float GeneratedDetailSharpnessTo01(float sharpness)
            => Mathf.InverseLerp(GeneratedDetailMinSharpness, GeneratedDetailMaxSharpness,
                Mathf.Clamp(sharpness, GeneratedDetailMinSharpness, GeneratedDetailMaxSharpness));

        public static int GeneratedDetailSeedFrom01(float t)
            => Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(t) * GeneratedDetailMaxSeed), 0, GeneratedDetailMaxSeed);

        public static float GeneratedDetailSeedTo01(int seed)
            => Mathf.Clamp(seed, 0, GeneratedDetailMaxSeed) / (float)GeneratedDetailMaxSeed;

        /// <summary>Sensible defaults matching the BrazeContainer field initializers — used when a clip has no baked
        /// look (older caches) so playback still renders reasonably.</summary>
        public static BrazeRenderSettings Default => new BrazeRenderSettings
        {
            rendererStateVersion = 1,
            qualityMode = BrazeContainer.QualityMode.Realtime,
            customRenderStepBudget = 256,
            customMemoryBudgetMB = 2048,

            renderSmoke = true,
            renderFire = true,

            densityScale = 5f,
            smokeDetail = 0.7f,
            smokeBrightness = 1f,
            smokeContrast = 1.5f,
            opacity = 0.95f,
            raymarchSteps = 96,
            adaptiveSteps = true,
            maxRaymarchHeight = 0,
            smokeAlbedoGradient = null,
            smokeOpacityCurve = null,

            fireScale = 3f,
            fireDensityScale = 1.4f,
            fireTempScale = 0.5f,
            fireDetail = 0.7f,
            fireEmissionCurve = null,
            fireColorMode = BrazeContainer.FireColorMode.Blackbody,
            fireGradient = null,
            fireLightColor = new Color(1f, 0.45f, 0.12f, 1f),

            bakeLighting = true,
            illumination = 1.2f,
            scattering = 0.5f,
            shadowStrength = 4.5f,
            ambientColor = new Color(0.16f, 0.20f, 0.27f, 1f),
            ambientIntensity = 0.6f,
            definition = 0.35f,
            crispen = 0f,
            crispenSharp = 4f,
            crispenPivot = 0.35f,
            smoothVoxels = 1f,
            emptySpaceSkip = true,
            gridLightStepVox = 1.4f,
            gridLightSteps = 28,
            lightingDownsample = 2,
            lightingInterval = 2,
            depthFeather = 0.1f,
            edgeFade = 0.06f,

            fireLightIntensity = 1.5f,
            fireLightTint = Color.white,
            fireLightSmoke = true,
            fireLight = false,
            fireLightRange = 8f,
            fireLightSoftness = 0.25f,
            fireLightShadow = 2f,
            fireLightSteps = 12,

            castShadows = false,
            castShadowStrength = 0.85f,
            castShadowDensity = 3f,
            castShadowSteps = 12,
            receiveShadows = false,


            renderDetail = false,
            detailBoost = 0.6f,        // Amount
            detailScale = 10f,         // Feature Size
            detailSharpness = 0.5f,    // Sharpness
            detailQuality = 0.55f,     // Detail Quality
            detailCurl = 0.4f,         // Flow Coherence
            detailMotion = 1f,         // Motion
            detailSpeed = 1f,          // Speed
            detailResponse = 1f,       // Response
            detailPersistence = 0.6f,  // Persistence
            detailCrisp = 0.4f,        // Crisp grain erosion (container)
            detailGrainMin = 2.5f,     // finest grain (voxels)
            detailGrainMax = 9f,       // coarsest grain (voxels)
            detailContrast = 1.5f,     // Sharpness (generated)
            detailRoughness = 0.5f,    // grain (generated)
            detailSizeMin = 0.15f,     // finest octave (lattice 64) at ~0.15 world units
            detailSizeMax = 0.6f,      // → 3 octaves (ratio 4), i.e. 16/32/64
            detailSeed = 0,
            detailDebugCells = false,
            detailDebugCellsOnly = false,
            detailDebugCutoff = 0.55f,
            detailStretch = 1.5f,
            detailFine = 1f,
            detailGrainCurl = 0f,
            detailEdge = 1f,
            detailBillow = 0.7f,
            detailRenderScale = 1,
            detailMemoryBudgetMB = 512,
            detailBakeInterval = 2,
            slice = BrazeSliceRenderSettings.Default,
        };
    }

    /// <summary>Versioned appearance owned by the current slice renderer. Kept separate from the legacy/shared look
    /// fields so existing cache assets deserialize safely and generated-volume settings stay independent.
    /// 2026-07-10: the orphaned pyro-froxel look fields (smokeExtinction, scatterAlbedo, fireIntensity/Power/
    /// Rolloff/Opacity, the payload's own fire colour mode/gradient/temp range, ambientOcclusion, powderStrength,
    /// sootiness, bright/dark smoke colours, fireSelfShadowStrength) were removed — the "restored old smoke/fire
    /// response" rework left their uniforms declared-but-never-sampled in BrazeSlicesCore.hlsl. Version stays 1:
    /// older serialized payloads simply drop the extinct values, the surviving fields deserialize by name.</summary>
    [System.Serializable]
    public struct BrazeSliceRenderSettings
    {
        public int version;
        public bool emptySpaceSkip, virtualFill;
        public float virtualFillStrength, virtualFillSharpness;
        public BrazeContainer.SliceVirtualFillView virtualFillView;
        public float phaseG, multiScatter;
        public bool selfShadows;
        public BrazeContainer.SliceShadowResolution shadowResolution;
        public int shadowFrameInterval, shadowSteps;
        public float shadowDensity, selfShadowStrength, shadowAmbientAmount, selfShadowDepthScale;
        public bool castShadows;
        public float castShadowStrength, castShadowFade, castShadowSoftness;
        public Color smokeFillColor;
        public float smokeFillStrength;
        public float sourceFilterStrength, sourceFilterRadius, densityContrast, depthSoftness, edgeFade;
        public float jitterStrength, jitterScale, jitterSceneBlend, jitterDenoise;
        public float densityGain, fireGain;

        public static BrazeSliceRenderSettings Default => new BrazeSliceRenderSettings
        {
            version = 1,
            emptySpaceSkip = true,
            virtualFill = true,
            virtualFillStrength = 0.75f,
            virtualFillSharpness = 0.35f,
            virtualFillView = BrazeContainer.SliceVirtualFillView.Both,
            phaseG = 0.45f,
            multiScatter = 0.5f,
            selfShadows = true,
            shadowResolution = BrazeContainer.SliceShadowResolution.Half,
            shadowFrameInterval = 2,
            shadowSteps = 16,
            shadowDensity = 1.5f,
            selfShadowStrength = 0.7f,
            shadowAmbientAmount = 0.6f,
            selfShadowDepthScale = 1f,
            castShadows = false,
            castShadowStrength = 0.5f,
            castShadowFade = 2f,
            castShadowSoftness = 1f,
            smokeFillColor = new Color(0.5f, 0.55f, 0.62f),
            smokeFillStrength = 0.5f,
            sourceFilterStrength = 0.45f,
            sourceFilterRadius = 1.25f,
            densityContrast = 1.15f,
            depthSoftness = 0.75f,
            edgeFade = 0.07f,
            jitterStrength = 0.5f,
            jitterScale = 1f,
            jitterSceneBlend = 0.65f,
            jitterDenoise = 0.55f,
            densityGain = 10f,
            fireGain = 0.5f,
        };
    }
}
