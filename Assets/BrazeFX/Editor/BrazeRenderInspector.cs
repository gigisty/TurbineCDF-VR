using System;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// The ONE curated volume-look UI, drawn identically by the BrazeContainer inspector (RENDERER
    /// group) and the BrazeCachePlayer's Override Look card. Both feed the same slice renderer, so the
    /// controls live here and cannot drift. Two accessors bind it to either host:
    /// P resolves the shared/portable BrazeRenderSettings-shaped fields (grade multipliers, Render
    /// Detail, the light bake) — container fields or lookOverride relatives; S resolves the slice
    /// renderer's NATIVE fields — the container's slice* mirrors live, or the override's embedded
    /// BrazeSliceRenderSettings payload on the cache player.
    /// Legacy knobs with no consumer on the slice path (Crispen, Soft Depth, the legacy Edge Falloff,
    /// grid-light step/resolution/interval, the old scene fire-light and cast-shadow marchers) are
    /// intentionally NOT drawn; their serialized fields survive for the generated-volume renderer and
    /// for old clips.
    /// </summary>
    public static class BrazeRenderInspector
    {
        /// <summary>Draw the curated renderer look (everything under the container's "Render Volume" toggle).</summary>
        /// <param name="keyPrefix">Foldout-state key prefix (keep distinct per inspector).</param>
        /// <param name="P">Shared-look accessor — container: serializedObject.FindProperty; player: lookOverride.FindPropertyRelative.</param>
        /// <param name="S">Slice-native accessor — container: FindProperty("slice"+Pascal(name)); player: lookOverride.slice.FindPropertyRelative.</param>
        /// <param name="drawSun">Container draws its Sun field here; the player sets its Key Light in Playback.</param>
        /// <param name="sliceActive">False for a legacy (version-0) clip payload: slice-native rows draw greyed because the runtime ignores them until the payload is enabled.</param>
        /// <param name="liveContainer">The live container when editing one — enables the instance-dependent extras (Render Detail budget warnings, Debug View). Null for the cache player.</param>
        public static void DrawVolumeLookControls(string keyPrefix,
                                                  Func<string, SerializedProperty> P,
                                                  Func<string, SerializedProperty> S,
                                                  bool drawSun, bool sliceActive,
                                                  BrazeContainer liveContainer)
        {
            if (BrazeEditorGUI.Section(keyPrefix + "Smoke", "Smoke"))
            {
                BrazeEditorGUI.ToggleButton("Render Smoke", P("renderSmoke"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("densityScale"), new GUIContent("Density Scale"));
                BrazeEditorGUI.PropertyField(P("smokeBrightness"), new GUIContent("Brightness"));
                BrazeEditorGUI.Slider(P("smokeContrast"), 1f, 4f, new GUIContent("Contrast"));
                BrazeEditorGUI.Slider(P("opacity"), 0f, 1f);
                BrazeEditorGUI.Separator();
                using (new EditorGUI.DisabledScope(!sliceActive))
                {
                    BrazeEditorGUI.PropertyField(S("virtualFill"), new GUIContent("Gap Fill"));
                    using (new EditorGUI.DisabledScope(!S("virtualFill").boolValue))
                    {
                        BrazeEditorGUI.PillsEnum("View", S("virtualFillView"));
                        BrazeEditorGUI.Slider(S("virtualFillStrength"), 0f, 1f, new GUIContent("Gap Strength"));
                        BrazeEditorGUI.Slider(S("virtualFillSharpness"), 0f, 1f, new GUIContent("Max Bias"));
                    }
                }
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("smokeAlbedoGradient"), new GUIContent("Albedo"));
                BrazeEditorGUI.PropertyField(P("smokeOpacityCurve"), new GUIContent("Opacity Curve"));
                BrazeEditorGUI.Hint("Restored old smoke response: density goes through the pre-integrated " +
                                    "opacity curve and albedo gradient, then key/ambient/scatter/definition " +
                                    "shape it in the raymarch. Gap Fill adds a cheap half-step smoke slice " +
                                    "when the budget stretches the real march step. Gap Fill view shows only " +
                                    "that support; Gap Strength scales it, and Max Bias controls how hard it " +
                                    "can lift sparse wisps.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(keyPrefix + "Fire", "Fire"))
            {
                BrazeEditorGUI.ToggleButton("Render Fire", P("renderFire"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("fireScale"), new GUIContent("Intensity"));
                BrazeEditorGUI.PropertyField(P("fireDensityScale"), new GUIContent("Opacity"));
                BrazeEditorGUI.PropertyField(P("fireTempScale"), new GUIContent("Heat"));
                BrazeEditorGUI.PropertyField(P("fireEmissionCurve"), new GUIContent("Fire Curve"));
                var fcMode = P("fireColorMode");
                BrazeEditorGUI.PillsEnum("Fire Color", fcMode);
                if (fcMode.enumValueIndex != 0)
                    BrazeEditorGUI.PropertyField(P("fireGradient"), new GUIContent("Gradient"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.Slider(P("fireLightIntensity"), 0f, 8f, new GUIContent("Smoke Glow"));
                BrazeEditorGUI.PropertyField(P("fireLightTint"), new GUIContent("Glow Color"));
                BrazeEditorGUI.Hint("Restored old fire response: temperature drives hue, burn drives the " +
                                    "Fire Curve amount, Intensity drives brightness, and Opacity controls " +
                                    "only fire's own extinction.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(keyPrefix + "Detail", "Detail"))
            {
                BrazeEditorGUI.PropertyField(P("renderDetail"), new GUIContent("Render Detail"));
                using (new EditorGUI.DisabledScope(!P("renderDetail").boolValue))
                {
                    BrazeEditorGUI.Slider(P("detailBoost"), 0f, 2f, new GUIContent("Boost"));
                    BrazeEditorGUI.Slider(P("detailScale"), 2f, 32f, new GUIContent("Scale"));
                    BrazeEditorGUI.Slider(P("detailStretch"), 0f, 4f, new GUIContent("Stretch"));
                    BrazeEditorGUI.Slider(P("detailRoughness"), 0f, 1f, new GUIContent("Roughness"));
                    BrazeEditorGUI.Slider(P("detailFine"), 0f, 1f, new GUIContent("Fine Detail"));
                    BrazeEditorGUI.Slider(P("detailGrainCurl"), 0f, 1f, new GUIContent("Grain Curl"));
                    BrazeEditorGUI.Slider(P("detailContrast"), 1f, 3f, new GUIContent("Contrast"));
                    BrazeEditorGUI.Slider(P("detailEdge"), 1f, 4f, new GUIContent("Edge"));
                    BrazeEditorGUI.Slider(P("detailCurl"), 0f, 1f, new GUIContent("Curl"));
                    BrazeEditorGUI.Slider(P("detailBillow"), 0f, 1.5f, new GUIContent("Billow"));
                    BrazeEditorGUI.IntSlider(P("detailRenderScale"), 1, 4, new GUIContent("Detail Resolution"));
                    BrazeEditorGUI.IntSlider(P("detailMemoryBudgetMB"), 64, 2048, new GUIContent("Memory Budget (MB)"));
                    BrazeEditorGUI.IntSlider(P("detailBakeInterval"), 1, 8,
                        new GUIContent("Bake Every N Ticks",
                            "Higher values save warp-bake time. Motion is phase-smoothed between bakes; 1 gives the fastest response."));
                    if (liveContainer != null && P("renderDetail").boolValue)
                    {
                        int requested = P("detailRenderScale").intValue;
                        int effective = liveContainer.EffectiveDetailRenderScale(liveContainer.SolverRes);
                        if (effective <= 0)
                            EditorGUILayout.HelpBox("Render Detail is disabled at this grid size: even 1× exceeds the memory/device budget.", MessageType.Warning);
                        else if (effective < requested)
                            EditorGUILayout.HelpBox($"The requested {requested}× warp exceeds the budget; runtime will use {effective}×.", MessageType.Warning);
                    }
                }
                BrazeEditorGUI.Hint("Restored warp-volume Render Detail. It bends the existing smoke/fire " +
                                    "through a flow-carried displacement field instead of drawing extra " +
                                    "density/burn voxels. Detail Resolution controls the warp texture only; " +
                                    "Bake Every N Ticks is temporally smoothed without a history texture.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(keyPrefix + "Banding", "Anti-Banding"))
            {
                using (new EditorGUI.DisabledScope(!sliceActive))
                {
                    BrazeEditorGUI.Slider(S("jitterScale"), 0.1f, 4f, new GUIContent("Jitter Scale"));
                    BrazeEditorGUI.Slider(S("jitterSceneBlend"), 0f, 1f, new GUIContent("Pyro Fill"));
                    BrazeEditorGUI.Slider(S("jitterDenoise"), 0f, 1f, new GUIContent("Denoise"));
                }
                BrazeEditorGUI.Hint("Ray Jitter strength is owned globally by the Braze Render Feature so containers " +
                                    "cannot fight final renderer quality. Jitter Scale above 1 is treated as 1 because " +
                                    "the working-pixel grid cannot resolve a finer pattern. Denoise fills alpha/" +
                                    "transmittance holes, while Pyro Fill repairs partial-opacity colour holes.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(keyPrefix + "Light", "Lighting"))
            {
                // "Bake Lighting" so it reads unambiguously as the baked-lighting master switch.
                // Settings stay VISIBLE (greyed) when off — hiding them made the section look empty
                // and left users unsure which controls belong to the baked path.
                BrazeEditorGUI.ToggleButton("Bake Lighting", P("bakeLighting"), "Using Baked Lighting");
                using (new EditorGUI.DisabledScope(!P("bakeLighting").boolValue))
                {
                    if (drawSun) BrazeEditorGUI.PropertyField(P("sun"));
                    BrazeEditorGUI.Slider(P("illumination"), 0f, 2f);
                    BrazeEditorGUI.Slider(P("scattering"), 0f, 2f);
                    BrazeEditorGUI.Slider(P("shadowStrength"), 0f, 8f);
                    BrazeEditorGUI.PropertyField(P("ambientColor"));
                    BrazeEditorGUI.Slider(P("ambientIntensity"), 0f, 4f);
                    BrazeEditorGUI.Slider(P("definition"), 0f, 2f);
                }
                BrazeEditorGUI.Separator();
                using (new EditorGUI.DisabledScope(!sliceActive))
                {
                    BrazeEditorGUI.PropertyField(S("selfShadows"), new GUIContent("Self Shadow"));
                    using (new EditorGUI.DisabledScope(!S("selfShadows").boolValue))
                    {
                        BrazeEditorGUI.Slider(S("selfShadowStrength"), 0f, 1f, new GUIContent("Mix Strength"));
                        BrazeEditorGUI.Slider(S("shadowDensity"), 0.25f, 4f, new GUIContent("Bake Density"));
                        BrazeEditorGUI.Slider(S("shadowAmbientAmount"), 0f, 1f, new GUIContent("Fill Shadowing"));
                    }
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PropertyField(S("castShadows"), new GUIContent("Cast Shadows"));
                    using (new EditorGUI.DisabledScope(!S("castShadows").boolValue))
                        BrazeEditorGUI.Slider(S("castShadowStrength"), 0f, 1f, new GUIContent("Cast Strength"));
                    bool shadowsOn = S("selfShadows").boolValue || S("castShadows").boolValue;
                    using (new EditorGUI.DisabledScope(!shadowsOn))
                    {
                        BrazeEditorGUI.PropertyField(S("shadowResolution"), new GUIContent("Shadow Res"));
                        BrazeEditorGUI.IntSlider(S("shadowSteps"), 4, 64, new GUIContent("Shadow Steps"));
                        BrazeEditorGUI.IntSlider(S("shadowFrameInterval"), 1, 8, new GUIContent("Bake Every N Frames"));
                    }
                    if (sliceActive && shadowsOn &&
                        (liveContainer == null || liveContainer.qualityMode != BrazeContainer.QualityMode.Production) &&
                        (S("shadowResolution").enumValueIndex == (int)BrazeContainer.SliceShadowResolution.Full ||
                         S("shadowSteps").intValue > 16 || S("shadowFrameInterval").intValue < 2))
                        EditorGUILayout.HelpBox("Above the realtime shadow budget (Half, 12–16 taps, every 2 frames). Full/every-frame shadows are intentionally expensive.", MessageType.Warning);
                }
                BrazeEditorGUI.Hint("Lighting now uses the old key/ambient/scatter/definition model. Shadow " +
                                    "Strength above and Bake Density drive the baked transmittance volume; " +
                                    "Mix Strength controls how much of that shadow reaches the final slice.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(keyPrefix + "Advanced", "Advanced"))
            {
                using (new EditorGUI.DisabledScope(!sliceActive))
                    BrazeEditorGUI.Slider(S("depthSoftness"), 0.05f, 4f, new GUIContent("Depth Softness"));
                if (liveContainer != null)
                    BrazeEditorGUI.PropertyField(S("renderDebugView"), new GUIContent("Debug View"));
            }
            BrazeEditorGUI.EndSection();
        }
    }
}
