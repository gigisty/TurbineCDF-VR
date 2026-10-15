using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Shared transfer-LUT baking for volume looks. This used to also bind the full appearance to the legacy
    /// BrazeFX/Raymarch material (Apply/Frame/ResolveSteps) — that binder was removed with the legacy renderer;
    /// the new volume renderer binds its own material. The LUTs remain shared because the fire-light/shadow
    /// injection (<see cref="BrazeFireLightBind"/>) and any volume renderer consume the same baked ramps, so a
    /// container, a cache playback and a generated volume all grade identically.
    /// </summary>
    public static class BrazeVolumeBind
    {
        /// <summary>Bake the smoke/fire transfer LUTs from a settings' gradients/curves (creating the textures if null).
        /// Mirrors BrazeContainer.RebuildTransferLuts; falls back to the built-in defaults for any unset gradient/curve.</summary>
        public static void BuildLuts(in BrazeRenderSettings s, ref Texture2D smokePreint, ref Texture2D firePreint, ref Texture2D fireCurveLut)
        {
            var smokeAlbedo = s.smokeAlbedoGradient ?? BrazeTransferFunction.DefaultSmokeAlbedoGradient();
            var smokeOpacity = (s.smokeOpacityCurve != null && s.smokeOpacityCurve.length > 0) ? s.smokeOpacityCurve : BrazeTransferFunction.DefaultSmokeOpacityCurve();
            var fireEmission = (s.fireEmissionCurve != null && s.fireEmissionCurve.length > 0) ? s.fireEmissionCurve : BrazeTransferFunction.DefaultFireEmissionCurve();
            var fireGrad = s.fireGradient ?? BrazeBlackbody.DefaultFireGradient();

            if (smokePreint == null) smokePreint = BrazeTransferFunction.CreateLut("BrazeFX_SmokePreint");
            if (firePreint == null) firePreint = BrazeTransferFunction.CreateLut("BrazeFX_FirePreint");
            if (fireCurveLut == null) fireCurveLut = BrazeTransferFunction.CreateCurveLut("BrazeFX_FireCurve");

            System.Func<float, Color> fireColorAt = s.fireColorMode == BrazeContainer.FireColorMode.Blackbody
                ? (System.Func<float, Color>)BrazeBlackbody.Planck01
                : v => fireGrad.Evaluate(v);
            BrazeTransferFunction.Bake(smokePreint, firePreint, smokeAlbedo, smokeOpacity, fireColorAt, fireEmission);
            BrazeTransferFunction.BakeCurve1D(fireCurveLut, fireEmission);
        }
    }
}
