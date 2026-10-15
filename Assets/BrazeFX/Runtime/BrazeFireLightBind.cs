using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Shared configuration of the fire→scene-light + cast-shadow injection material (BrazeFX/FireLight), used by
    /// BOTH the live container and the container-free cache renderer. Lifted from BrazeContainer.ApplyFireLight so the
    /// cast light + volume shadow match the rendered flame exactly, in one place. The owner supplies the world↔grid
    /// matrices (from its transform) and the key-light world direction; everything else comes from the look settings.
    /// </summary>
    public static class BrazeFireLightBind
    {
        static readonly int FireFieldID = Shader.PropertyToID("_BrazeFireField");
        static readonly int FireMomentsID = Shader.PropertyToID("_BrazeFireMoments");
        static readonly int FirePreintID = Shader.PropertyToID("_FirePreint");
        static readonly int FireCurveID = Shader.PropertyToID("_FireCurve");
        static readonly int FireWorldToGridID = Shader.PropertyToID("_BrazeWorldToGridUVW");
        static readonly int FireGridToWorldID = Shader.PropertyToID("_BrazeFireGridToWorld");
        static readonly int FireScaleID = Shader.PropertyToID("_FireScale");
        static readonly int FireTempID = Shader.PropertyToID("_FireTempScale");
        static readonly int FireLightTintID = Shader.PropertyToID("_BrazeFireColor");
        static readonly int FireLightIntensityID = Shader.PropertyToID("_BrazeFireIntensity");
        static readonly int FireLightRangeID = Shader.PropertyToID("_BrazeFireRange");
        static readonly int FireLightSoft2ID = Shader.PropertyToID("_BrazeFireSoft2");
        static readonly int FireLightShadowID = Shader.PropertyToID("_BrazeFireShadow");
        static readonly int FireLightStepsID = Shader.PropertyToID("_BrazeFireSteps");
        static readonly int ShadowLightDirUVWID = Shader.PropertyToID("_BrazeShadowLightDirUVW");
        static readonly int ShadowStrengthID = Shader.PropertyToID("_BrazeShadowStrength");
        static readonly int ShadowDensityID = Shader.PropertyToID("_BrazeShadowDensity");
        static readonly int ShadowStepsID = Shader.PropertyToID("_BrazeShadowSteps");

        /// <summary>Configure the fire-light/shadow injection material. <paramref name="keyLightWorldDir"/> is the
        /// direction TOWARD the key light (e.g. -sun.forward).</summary>
        public static void Apply(Material mat, BrazeSolver solver, in BrazeRenderSettings s,
                                 Texture2D firePreint, Texture2D fireCurveLut,
                                 Matrix4x4 worldToGridUVW, Matrix4x4 gridToWorld, Vector3 keyLightWorldDir)
        {
            if (mat == null || solver == null) return;

            mat.SetTexture(FireFieldID, solver.Fields);
            if (solver.FireMoments != null) mat.SetBuffer(FireMomentsID, solver.FireMoments);
            mat.SetTexture(FirePreintID, firePreint);
            mat.SetTexture(FireCurveID, fireCurveLut);
            mat.SetMatrix(FireWorldToGridID, worldToGridUVW);
            mat.SetMatrix(FireGridToWorldID, gridToWorld);

            // Fire colour/amount inputs — identical to the raymarch so the room light reads as the same flame.
            mat.SetFloat(FireScaleID, s.fireScale);
            mat.SetFloat(FireTempID, s.fireTempScale);
            // Cast-light shaping.
            mat.SetColor(FireLightTintID, s.fireLightTint);
            mat.SetFloat(FireLightIntensityID, s.fireLightIntensity);
            mat.SetFloat(FireLightRangeID, s.fireLightRange);
            float soft = Mathf.Max(1e-3f, s.fireLightRange * Mathf.Clamp01(s.fireLightSoftness));
            mat.SetFloat(FireLightSoft2ID, soft * soft);
            mat.SetFloat(FireLightShadowID, Mathf.Max(0f, s.fireLightShadow));
            mat.SetInt(FireLightStepsID, Mathf.Clamp(s.fireLightSteps, 4, 32));

            // Cast-shadow (pass 1): the volume blocks the key light onto the scene. March direction = TOWARD the key
            // light, in grid-uvw space (so the shader can slab-clip against the unit-cube grid).
            Vector3 wd = keyLightWorldDir.sqrMagnitude > 1e-10f ? keyLightWorldDir.normalized : new Vector3(0.3f, 1f, 0.2f).normalized;
            Vector3 uvwDir = worldToGridUVW.MultiplyVector(wd);
            if (uvwDir.sqrMagnitude < 1e-10f) uvwDir = Vector3.up;
            mat.SetVector(ShadowLightDirUVWID, uvwDir.normalized);
            mat.SetFloat(ShadowStrengthID, Mathf.Clamp01(s.castShadowStrength));
            mat.SetFloat(ShadowDensityID, Mathf.Max(0f, s.castShadowDensity));
            mat.SetInt(ShadowStepsID, Mathf.Clamp(s.castShadowSteps, 4, 32));
        }
    }
}
