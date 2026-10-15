using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Dedicated renderer for the procedural volume system (<see cref="BrazeVolume"/> + its filter stack). It is
    /// intentionally separate from the live/cache fire-smoke renderer so procedural nebula/cloud rendering cannot
    /// change it. The generator packs density and baked lighting into <see cref="Field"/> (R/G/B/A = density,
    /// self-shadow, ambient occlusion, cast-shadow transmittance); this class owns the proxy cube and materials.
    /// </summary>
    public sealed class BrazeGeneratedVolumeRenderer : IBrazeVolumeSource, IDisposable
    {
        static readonly int VolumeID = Shader.PropertyToID("_Volume");
        static readonly int WorldSizeID = Shader.PropertyToID("_WorldSize");
        static readonly int StepCountID = Shader.PropertyToID("_StepCount");
        static readonly int DensityScaleID = Shader.PropertyToID("_DensityScale");
        static readonly int BrightnessID = Shader.PropertyToID("_Brightness");
        static readonly int ContrastID = Shader.PropertyToID("_Contrast");
        static readonly int OpacityID = Shader.PropertyToID("_Opacity");
        static readonly int IllumID = Shader.PropertyToID("_Illum");
        static readonly int ScatterID = Shader.PropertyToID("_Scatter");
        static readonly int DefinitionID = Shader.PropertyToID("_Definition");
        static readonly int KeyDirWSID = Shader.PropertyToID("_KeyDirWS");
        static readonly int KeyColorID = Shader.PropertyToID("_KeyColor");
        static readonly int KeyIntensityID = Shader.PropertyToID("_KeyIntensity");
        static readonly int AmbientIntensityID = Shader.PropertyToID("_AmbientIntensity");
        static readonly int DepthFeatherID = Shader.PropertyToID("_DepthFeather");
        static readonly int SampleOffsetID = Shader.PropertyToID("_SampleOffsetUvw");
        static readonly int CloudAlbedoID = Shader.PropertyToID("_CloudAlbedo");
        static readonly int TintModeID = Shader.PropertyToID("_TintMode");
        static readonly int TintRampID = Shader.PropertyToID("_TintRamp");
        static readonly int AnisotropyID = Shader.PropertyToID("_Anisotropy");
        static readonly int SilverLiningID = Shader.PropertyToID("_SilverLining");
        static readonly int PowderID = Shader.PropertyToID("_Powder");
        static readonly int SkyColorID = Shader.PropertyToID("_SkyColor");
        static readonly int GroundColorID = Shader.PropertyToID("_GroundColor");
        static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
        static readonly int EmissionAmountID = Shader.PropertyToID("_EmissionAmount");
        static readonly int EmissionBaseID = Shader.PropertyToID("_EmissionBase");
        static readonly int CrispenID = Shader.PropertyToID("_Crispen");
        static readonly int CrispSharpID = Shader.PropertyToID("_CrispSharp");
        static readonly int CrispPivotID = Shader.PropertyToID("_CrispPivot");
        static readonly int OccupancyID = Shader.PropertyToID("_Occupancy");
        static readonly int OccStepID = Shader.PropertyToID("_OccStep");
        static readonly int OccEnabledID = Shader.PropertyToID("_OccEnabled");
        static readonly int DetailNoiseID = Shader.PropertyToID("_DetailNoise");
        static readonly int DetailAmtID = Shader.PropertyToID("_DetailAmt");
        static readonly int DetailFreqID = Shader.PropertyToID("_DetailFreq");
        static readonly int DetailSharpID = Shader.PropertyToID("_DetailSharp");
        static readonly int DetailWarpID = Shader.PropertyToID("_DetailWarp");
        // Cast-shadow pass (BrazeFX/FireLight pass 1) — same uniform names BrazeFireLightBind uses.
        static readonly int ShadowFieldID = Shader.PropertyToID("_BrazeFireField");
        static readonly int ShadowWorldToGridID = Shader.PropertyToID("_BrazeWorldToGridUVW");
        static readonly int ShadowLightDirUVWID = Shader.PropertyToID("_BrazeShadowLightDirUVW");
        static readonly int ShadowCastStrengthID = Shader.PropertyToID("_BrazeShadowStrength");
        static readonly int ShadowCastDensityID = Shader.PropertyToID("_BrazeShadowDensity");
        static readonly int ShadowCastStepsID = Shader.PropertyToID("_BrazeShadowSteps");
        static readonly int ShadowBakedTexID = Shader.PropertyToID("_BrazeShadowBakedTex");
        static readonly int ShadowUseBakedID = Shader.PropertyToID("_BrazeShadowUseBaked");

        readonly Transform host;
        BrazeRenderSettings look = BrazeRenderSettings.Default;
        Vector3 worldSize = Vector3.one;
        Vector3Int res;
        bool active;
        bool renderVolume = true;
        bool temporalDetailUnstable;
        Light keyLight;

        RenderTexture field;
        RenderTexture occupancyTex;     // coarse occupancy owned by BrazeVolume; bound for empty-space skipping
        RenderTexture lightTex;         // coarse baked light grid owned by BrazeVolume (.x shadow, .y sky occ)
        RenderTexture detailNoiseTex;   // static tiling noise for the render-time sharpness layer
        float detailTileDim = 64f;      // its finest octave lattice (= TileDim) — anchors Feature Size Min
        GameObject proxy;
        MeshRenderer proxyRenderer;
        Material mat;
        Material shadowMat;   // BrazeFX/FireLight (pass 1) — casts the volume's shadow onto scene geometry

        // Cloud-shading look (dedicated to the generator — not part of the shared BrazeRenderSettings).
        Color cloudAlbedo = Color.white;
        int tintMode;
        Texture tintRamp;
        float anisotropy = 0.35f;
        float silverLining = 0.6f;
        float powder = 0.5f;
        Color skyColor = new Color(0.28f, 0.42f, 0.62f, 1f);
        Color groundColor = new Color(0.20f, 0.16f, 0.12f, 1f);
        Color emissionColor = new Color(1f, 0.45f, 0.12f, 1f);
        float emissionAmount;
        float emissionBase;

        public BrazeGeneratedVolumeRenderer(Transform host) { this.host = host; }

        public bool VolumeRendering => active && renderVolume && look.renderSmoke && field != null && mat != null;
        public bool TemporalDetailUnstable => VolumeRendering && temporalDetailUnstable;
        public int MaxRaymarchHeightCap => Mathf.Max(0, look.maxRaymarchHeight); // legacy interface compatibility
        public bool FireLightActive => false;   // no fire emission in a static volume → only the shadow pass is used
        public bool ShadowCastActive => VolumeRendering && look.castShadows && shadowMat != null;
        public Material FireLightMaterial => shadowMat;

        public bool Ready => active && field != null && mat != null;
        public RenderTexture Field => field;
        public Vector3Int Res => res;

        public void SetWorldSize(Vector3 ws)
        {
            worldSize = new Vector3(Mathf.Max(ws.x, 0.01f), Mathf.Max(ws.y, 0.01f), Mathf.Max(ws.z, 0.01f));
            if (proxy != null) proxy.transform.localScale = worldSize;
        }

        public void SetLook(in BrazeRenderSettings s)
        {
            // NOTE: deliberately does NOT Touch() the registry. Touch resets the URP feature's temporal history; doing
            // it on every look/bake change made continuous slider drags reset history ~every frame → visible flicker.
            // The volume is static and the temporal blend converges on its own, so a reset per edit isn't needed.
            look = s;
        }

        public void SetKeyLight(Light light) => keyLight = light;
        public void SetTemporalDetailUnstable(bool unstable) => temporalDetailUnstable = unstable;

        /// <summary>Coarse max-pooled occupancy. The shader maps base coordinates to exact 4-voxel cells.</summary>
        public void SetOccupancy(RenderTexture occ) => occupancyTex = occ;

        /// <summary>Coarse baked light grid (.x self-shadow, .y sky occlusion, .z cast transmittance).</summary>
        public void SetLighting(RenderTexture light) => lightTex = light;

        /// <summary>Static tiling noise volume for the render-time sharpness layer + its finest octave lattice (TileDim).</summary>
        public void SetDetailNoise(RenderTexture tex, float tileDim) { detailNoiseTex = tex; detailTileDim = tileDim; }

        /// <summary>Dedicated cloud/nebula shading look (albedo, tint ramp, scattering, two-tone ambient, glow).</summary>
        public void SetCloudShading(Color albedo, int mode, Texture ramp, float aniso, float silver, float pow,
                                    Color sky, Color ground, Color emission, float emissionAmt, float emissionBaseBias)
        {
            cloudAlbedo = albedo;
            tintMode = mode;
            tintRamp = ramp;
            anisotropy = Mathf.Clamp(aniso, -0.95f, 0.95f);
            silverLining = Mathf.Max(0f, silver);
            powder = Mathf.Clamp01(pow);
            skyColor = sky;
            groundColor = ground;
            emissionColor = emission;
            emissionAmount = Mathf.Max(0f, emissionAmt);
            emissionBase = Mathf.Clamp01(emissionBaseBias);
            // No Touch() — see SetLook (avoids per-frame temporal-history resets while editing).
        }

        public void Begin(Vector3Int baseRes)
        {
            if (active) return;
            res = new Vector3Int(Mathf.Clamp(baseRes.x, 8, 512),
                                 Mathf.Clamp(baseRes.y, 8, 512),
                                 Mathf.Clamp(baseRes.z, 8, 512));
            field = MakeField(res);
            BuildProxy();
            active = field != null && mat != null;
            if (active) BrazeVolumeRegistry.Register(this);
        }

        // The field content changed (re-baked). No Touch() — the temporal blend converges to the new content without a
        // history reset; resetting every bake during a slider drag caused the renderer to flicker. Register/Unregister
        // (volume appear/disappear) still reset history, which is when it actually matters.
        public void MarkFieldChanged() { }

        public void Render(Vector3 sampleOffsetUvw)
        {
            if (!Ready) return;

            ResolveKeyLight(out Vector3 dirWS, out Color keyColor, out float keyIntensity);
            mat.SetTexture(VolumeID, field);
            mat.SetVector(WorldSizeID, worldSize);
            // Compatibility fallback for a direct/custom draw path. The standard URP feature sets the authoritative
            // _BrazeGeneratedStepBudget immediately before its renderer list is drawn.
            mat.SetInt(StepCountID, Mathf.Clamp(look.raymarchSteps, 8, 256));
            mat.SetFloat(DensityScaleID, Mathf.Max(0f, look.densityScale));
            mat.SetFloat(BrightnessID, Mathf.Max(0f, look.smokeBrightness));
            mat.SetFloat(ContrastID, Mathf.Clamp(look.smokeContrast, 0.25f, 6f));
            mat.SetFloat(OpacityID, Mathf.Clamp01(look.opacity));
            mat.SetFloat(IllumID, Mathf.Max(0f, look.illumination));
            mat.SetFloat(ScatterID, Mathf.Max(0f, look.scattering));
            mat.SetFloat(DefinitionID, Mathf.Max(0f, look.definition));
            mat.SetVector(KeyDirWSID, dirWS);
            mat.SetColor(KeyColorID, keyColor);
            mat.SetFloat(KeyIntensityID, keyIntensity);
            mat.SetFloat(AmbientIntensityID, Mathf.Max(0f, look.ambientIntensity));
            mat.SetFloat(DepthFeatherID, Mathf.Max(0f, look.depthFeather));
            mat.SetVector(SampleOffsetID, sampleOffsetUvw);

            mat.SetColor(CloudAlbedoID, cloudAlbedo);
            mat.SetInt(TintModeID, tintMode);
            if (tintRamp != null) mat.SetTexture(TintRampID, tintRamp);
            mat.SetFloat(AnisotropyID, anisotropy);
            mat.SetFloat(SilverLiningID, silverLining);
            mat.SetFloat(PowderID, powder);
            mat.SetColor(SkyColorID, skyColor);
            mat.SetColor(GroundColorID, groundColor);
            mat.SetColor(EmissionColorID, emissionColor);
            mat.SetFloat(EmissionAmountID, emissionAmount);
            mat.SetFloat(EmissionBaseID, emissionBase);

            // Render-time crispness (conforming, ~free). 0 = off → the shader's Crispen branch is a no-op.
            mat.SetFloat(CrispenID, Mathf.Clamp01(look.crispen));
            mat.SetFloat(CrispSharpID, Mathf.Max(1f, look.crispenSharp));
            mat.SetFloat(CrispPivotID, Mathf.Clamp01(look.crispenPivot));

            // Empty-space skipping: leap whole empty coarse cells while marching (sharper at equal cost, or faster).
            if (occupancyTex != null) mat.SetTexture(OccupancyID, occupancyTex);
            if (occupancyTex != null)
                mat.SetFloat(OccStepID, 0.9f / Mathf.Max(occupancyTex.width,
                    Mathf.Max(occupancyTex.height, occupancyTex.volumeDepth)));
            mat.SetFloat(OccEnabledID, (look.emptySpaceSkip && occupancyTex != null) ? 1f : 0f);

            // Render-time sharpness layer (baked tiling noise, cheap single fetch). Amt = 0 when off → free branch.
            if (detailNoiseTex != null) mat.SetTexture(DetailNoiseID, detailNoiseTex);
            mat.SetFloat(DetailAmtID, look.renderDetail ? Mathf.Clamp01(look.detailBoost) : 0f);
            // Finest octave (lattice = tileDim) lands at Feature Size Min; coarser octaves (baked) reach toward Max.
            mat.SetFloat(DetailFreqID, 1f / (BrazeRenderSettings.GeneratedDetailClampFeatureSize(look.detailSizeMin) * Mathf.Max(1f, detailTileDim)));
            mat.SetFloat(DetailSharpID, Mathf.Clamp(look.detailContrast,
                BrazeRenderSettings.GeneratedDetailMinSharpness,
                BrazeRenderSettings.GeneratedDetailMaxSharpness));
            mat.SetFloat(DetailWarpID, Mathf.Clamp01(look.detailCurl) * 0.08f);

            CoreUtils.SetKeyword(mat, "_BRAZE_GEN_DETAIL", look.renderDetail && look.detailBoost > 1e-4f && detailNoiseTex != null);
            CoreUtils.SetKeyword(mat, "_BRAZE_GEN_TINT", tintMode != 0 && tintRamp != null);
            CoreUtils.SetKeyword(mat, "_BRAZE_GEN_EMISSION", emissionAmount > 1e-4f);
            CoreUtils.SetKeyword(mat, "_BRAZE_GEN_RECEIVE_SHADOWS", look.receiveShadows);
            CoreUtils.SetKeyword(mat, "_BRAZE_GEN_BAKED_LIGHT", look.bakeLighting);

            if (proxyRenderer != null) proxyRenderer.enabled = VolumeRendering;

            if (look.castShadows) ConfigureShadow();   // refresh the cast-shadow uniforms (transform/light/density may move)
        }

        public void End()
        {
            if (!active && field == null && proxy == null && mat == null) return;
            if (active) BrazeVolumeRegistry.Unregister(this);
            active = false;
            ReleaseField();
            DestroyObj(proxy); proxy = null; proxyRenderer = null;
            DestroyObj(mat); mat = null;
            DestroyObj(shadowMat); shadowMat = null;
        }

        public void Dispose() => End();

        static RenderTexture MakeField(Vector3Int r)
        {
            var rt = new RenderTexture(r.x, r.y, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = r.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = "BrazeFX_GeneratedVolume",
            };
            rt.Create();
            return rt;
        }

        void BuildProxy()
        {
            var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/GeneratedVolumeRaymarch");
            if (shader == null)
            {
                Debug.LogError("[BrazeFX] Shader 'BrazeFX/GeneratedVolumeRaymarch' not found.");
                return;
            }

            proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.name = "BrazeFX Generated Volume Proxy";
            proxy.hideFlags = HideFlags.DontSave;
            var col = proxy.GetComponent<Collider>();
            if (col != null) DestroyObj(col);

            if (host != null) proxy.transform.SetParent(host, false);
            proxy.transform.localPosition = Vector3.zero;
            proxy.transform.localRotation = Quaternion.identity;
            proxy.transform.localScale = worldSize;

            mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            proxyRenderer = proxy.GetComponent<MeshRenderer>();
            proxyRenderer.sharedMaterial = mat;
            proxyRenderer.enabled = renderVolume;

            // Cast-shadow material: the volume blocks the key light onto opaque geometry. Drawn fullscreen (pass 1) by
            // BrazeRenderFeature's "BrazeFX Volume Shadow" pass — the same path containers use, just density-only.
            var shadowShader = BrazeRuntimeShaderLibrary.Find("BrazeFX/FireLight");
            if (shadowShader != null) shadowMat = new Material(shadowShader) { hideFlags = HideFlags.DontSave };
        }

        // World → grid-uvw for the unit-cube proxy (local ∈ [-0.5,0.5], scaled by worldSize): uvw = localToCube + 0.5.
        Matrix4x4 WorldToGridUVW() =>
            Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * proxy.transform.worldToLocalMatrix;

        // Configure the cast-shadow material each frame (transform / light / density may have changed). Only the
        // shadow pass (1) is ever drawn for the generator, so we set just its uniforms — no fire-emission inputs.
        void ConfigureShadow()
        {
            if (shadowMat == null || field == null) return;
            Matrix4x4 w2g = WorldToGridUVW();
            Vector3 worldDir = (keyLight != null ? -keyLight.transform.forward : new Vector3(0.3f, 1f, 0.2f)).normalized;
            Vector3 uvwDir = w2g.MultiplyVector(worldDir);
            if (uvwDir.sqrMagnitude < 1e-10f) uvwDir = Vector3.up;

            shadowMat.SetTexture(ShadowFieldID, field);
            if (lightTex != null) shadowMat.SetTexture(ShadowBakedTexID, lightTex);
            shadowMat.SetFloat(ShadowUseBakedID, lightTex != null ? 1f : 0f);
            shadowMat.SetMatrix(ShadowWorldToGridID, w2g);
            shadowMat.SetVector(ShadowLightDirUVWID, uvwDir.normalized);
            shadowMat.SetFloat(ShadowCastStrengthID, Mathf.Clamp01(look.castShadowStrength));
            shadowMat.SetFloat(ShadowCastDensityID, Mathf.Max(0f, look.castShadowDensity));
            shadowMat.SetInt(ShadowCastStepsID, Mathf.Clamp(look.castShadowSteps, 4, 32));
        }

        void ResolveKeyLight(out Vector3 dirWS, out Color col, out float intensity)
        {
            Vector3 worldDir = keyLight != null ? -keyLight.transform.forward : new Vector3(0.3f, 1f, 0.2f);
            dirWS = worldDir.normalized;
            col = keyLight != null ? keyLight.color : new Color(1f, 0.95f, 0.85f);
            intensity = keyLight != null ? Mathf.Min(2.5f, Mathf.Max(0f, keyLight.intensity) * 0.4f) : 1f;
        }

        void ReleaseField()
        {
            if (field == null) return;
            field.Release();
            DestroyObj(field);
            field = null;
        }

        static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
