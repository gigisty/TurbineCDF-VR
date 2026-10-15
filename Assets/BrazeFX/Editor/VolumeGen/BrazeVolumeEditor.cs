using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for <see cref="BrazeVolume"/> — the base-shape generator. Shaping is done by independent
    /// scene FIELD objects (like lights); drag them into the FIELDS list to scope them to this volume.</summary>
    [CustomEditor(typeof(BrazeVolume))]
    [CanEditMultipleObjects]
    public sealed class BrazeVolumeEditor : Editor
    {
        const string K = "BrazeFX.Volume.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("VOLUME GENERATOR", "renderer");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "domain", "DOMAIN"))
            {
                BrazeEditorGUI.PropertyField(P("worldSize"), new GUIContent("World Size"));
                BrazeEditorGUI.PropertyField(P("resolution"), new GUIContent("Resolution"));
                BrazeEditorGUI.Hint("The generated density fills this box. A static volume needs no velocity/pressure " +
                                    "buffers, so Resolution can go higher than a live container for the same VRAM.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shape", "BASE SHAPE"))
            {
                BrazeEditorGUI.PillsEnum("Shape", P("shape"));
                BrazeEditorGUI.PropertyField(P("shapeSize"), new GUIContent("Size"));
                BrazeEditorGUI.Slider(P("shapeRound"), 0f, 0.5f, new GUIContent("Rounding"));
                int s = P("shape").enumValueIndex;
                using (new EditorGUI.DisabledScope(s != (int)BrazeVolume.Shape.Torus && s != (int)BrazeVolume.Shape.Slab))
                    BrazeEditorGUI.Slider(P("shapeThickness"), 0.01f, 1f, new GUIContent("Thickness"));
                BrazeEditorGUI.Slider(P("falloff"), 0f, 0.5f, new GUIContent("Edge Softness"));
                BrazeEditorGUI.PropertyField(P("baseDensity"), new GUIContent("Base Density"));
                BrazeEditorGUI.Hint("The generator makes ONLY this primitive — no noise. Add structure with the filter " +
                                    "stack below.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "fields", "FIELDS"))
            {
                DrawFieldsInfo();
                BrazeEditorGUI.Hint("Drag field objects here to shape this volume — ONLY listed fields affect it. They " +
                                    "bake top-to-bottom; drag rows to reorder (e.g. put a Deform BELOW a Noise so it warps " +
                                    "the noisy result). Move/scale a field's transform to place its falloff. Density " +
                                    "(Remove) carves pathways noise can't.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "cloud", "CLOUD SHADING"))
            {
                BrazeEditorGUI.SubLabel("Colour");
                BrazeEditorGUI.PropertyField(P("cloudAlbedo"), new GUIContent("Cloud Colour"));
                BrazeEditorGUI.PillsEnum("Tint Mode", P("tintMode"));
                using (new EditorGUI.DisabledScope(P("tintMode").enumValueIndex == (int)BrazeVolume.TintMode.Uniform))
                    BrazeEditorGUI.PropertyField(P("tintRamp"), new GUIContent("Tint Ramp"));
                BrazeEditorGUI.Separator();

                BrazeEditorGUI.SubLabel("Scatter");
                BrazeEditorGUI.Slider(P("anisotropy"), -0.95f, 0.95f, new GUIContent("Anisotropy (g)"));
                BrazeEditorGUI.Slider(P("silverLining"), 0f, 2f, new GUIContent("Silver Lining"));
                BrazeEditorGUI.Slider(P("powder"), 0f, 1f, new GUIContent("Powder"));
                BrazeEditorGUI.Separator();

                BrazeEditorGUI.SubLabel("Ambient (two-tone)");
                BrazeEditorGUI.PropertyField(P("skyColor"), new GUIContent("Sky"));
                BrazeEditorGUI.PropertyField(P("groundColor"), new GUIContent("Ground"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "emission", "EMISSION (glow)"))
            {
                BrazeEditorGUI.Slider(P("emission"), 0f, 4f, new GUIContent("Glow"));
                using (new EditorGUI.DisabledScope(P("emission").floatValue <= 1e-4f))
                {
                    BrazeEditorGUI.PropertyField(P("emissionColor"), new GUIContent("Colour"));
                    BrazeEditorGUI.Slider(P("emissionBase"), 0f, 1f, new GUIContent("Base Bias"));
                }
                BrazeEditorGUI.Hint("Glow is derived from the final density and tinted by Colour (decoupled from fire). " +
                                    "It does not touch the shared fire renderer or scene-light passes.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "look", "LOOK"))
            {
                BrazeEditorGUI.PropertyField(P("keyLight"), new GUIContent("Key Light"));
                BrazeEditorGUI.Separator();
                DrawGeneratorLook(P("look"));
            }
            BrazeEditorGUI.EndSection();

            if (serializedObject.ApplyModifiedProperties())
            {
                foreach (var t in targets)
                    if (t is BrazeVolume v) v.MarkDirty();
                if (!Application.isPlaying) { EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); }
            }
        }

        void DrawFieldsInfo()
        {
            // Drag-and-drop assignment + row reorder. ONLY listed fields shape the volume; they bake top-to-bottom.
            BrazeEditorGUI.ObjectDropList(serializedObject.FindProperty("fields"),
                "＋   Drop a field here", "＋   Drop another field", ResolveField, reorderable: true);
        }

        static UnityEngine.Object ResolveField(UnityEngine.Object o)
        {
            if (o is BrazeVolumeField f) return f;
            if (o is GameObject go) return go.GetComponent<BrazeVolumeField>();
            return null;
        }

        static void DrawGeneratorLook(SerializedProperty look)
        {
            SerializedProperty L(string n) => look.FindPropertyRelative(n);

            BrazeEditorGUI.SubLabel("Render");
            BrazeEditorGUI.PropertyField(L("renderSmoke"), new GUIContent("Render"));
            BrazeEditorGUI.Slider(L("depthFeather"), 0f, 0.4f, new GUIContent("Soft Depth"));
            BrazeEditorGUI.PropertyField(L("emptySpaceSkip"), new GUIContent("Empty-Space Skip",
                "Leap whole empty cells while marching (coarse occupancy). Sharper at the same Ray Steps, or faster. " +
                "Leave on unless debugging."));
            BrazeEditorGUI.Hint("Final ray steps, render resolution, and height cap are controlled by the active " +
                                "Braze Render Feature. This volume owns its density, shading, lighting, and shadows.");
            BrazeEditorGUI.Separator();

            BrazeEditorGUI.SubLabel("Density");
            BrazeEditorGUI.PropertyField(L("densityScale"), new GUIContent("Density Scale"));
            BrazeEditorGUI.Slider(L("opacity"), 0f, 1f, new GUIContent("Opacity"));
            BrazeEditorGUI.Slider(L("smokeBrightness"), 0f, 4f, new GUIContent("Brightness"));
            BrazeEditorGUI.Slider(L("smokeContrast"), 0.25f, 6f, new GUIContent("Contrast"));
            BrazeEditorGUI.Separator();

            BrazeEditorGUI.SubLabel("Lighting (baked)");
            BrazeEditorGUI.PropertyField(L("bakeLighting"), new GUIContent("Self Shadow"));
            BrazeEditorGUI.IntSlider(L("gridLightSteps"), 4, 64, new GUIContent("Bake Quality",
                "Light-march steps used when baking shadows (once per change, not per frame). Higher = smoother."));
            BrazeEditorGUI.PropertyField(L("shadowStrength"), new GUIContent("Shadow Strength"));
            BrazeEditorGUI.IntSlider(L("lightingDownsample"), 1, 4, new GUIContent("Light Grid",
                "Resolution divisor of the baked light grid (lighting is low-frequency). 2 = half-res (8× cheaper bake), " +
                "4 = quarter (64×) — raise it on big (512³) volumes to keep edits snappy."));
            BrazeEditorGUI.PropertyField(L("illumination"), new GUIContent("Illumination"));
            BrazeEditorGUI.PropertyField(L("scattering"), new GUIContent("Scattering"));
            BrazeEditorGUI.Slider(L("definition"), 0f, 2f, new GUIContent("Shadow Contrast",
                "Deepen the baked self-shadow for more 3D form. 0 = flat, higher = punchier shadows."));
            BrazeEditorGUI.PropertyField(L("ambientIntensity"), new GUIContent("Ambient Intensity"));
            BrazeEditorGUI.Separator();

            BrazeEditorGUI.SubLabel("Scene Shadows");
            BrazeEditorGUI.PropertyField(L("receiveShadows"), new GUIContent("Receive Shadows",
                "Scene casters (a character / object) darken the cloud where their shadow falls. The generated " +
                "volume samples the shadow map once at its opacity centroid, not once per ray step."));
            BrazeEditorGUI.PropertyField(L("castShadows"), new GUIContent("Cast Shadows",
                "The volume's density blocks the key light onto the ground / objects behind it."));
            using (new EditorGUI.DisabledScope(!L("castShadows").boolValue))
            {
                EditorGUI.indentLevel++;
                BrazeEditorGUI.Slider(L("castShadowStrength"), 0f, 1f, new GUIContent("Strength"));
                BrazeEditorGUI.Slider(L("castShadowDensity"), 0f, 8f, new GUIContent("Density"));
                EditorGUILayout.HelpBox(
                    "Generated scene shadows reuse the baked light grid and cost one 3D sample per screen pixel. " +
                    "Bake Quality and Light Grid above control their quality; the live-volume March Steps setting " +
                    "does not apply here.", MessageType.Info);
                EditorGUI.indentLevel--;
            }
            BrazeEditorGUI.Separator();

            // Render-time crispness: contrast-sharpen the EXISTING density into crisp edges. No new noise, ~free
            // (no extra texture taps), and entirely in this generator's renderer (never touches fire/smoke).
            BrazeEditorGUI.SubLabel("Crispness (render-time)");
            BrazeEditorGUI.Slider(L("crispen"), 0f, 1f, new GUIContent("Crispen",
                "Sharpen the soft cloud falloff into crisp edges by contrast-expanding the sampled density. " +
                "Conforms to existing structure (adds no noise) and costs no extra samples. 0 = off."));
            using (new EditorGUI.DisabledScope(L("crispen").floatValue <= 1e-4f))
            {
                BrazeEditorGUI.Slider(L("crispenSharp"), 1f, 12f, new GUIContent("Edge Sharpness"));
                BrazeEditorGUI.Slider(L("crispenPivot"), 0f, 1f, new GUIContent("Pivot"));
            }
            BrazeEditorGUI.Separator();

            // Render-time detail: a world-space noise layer (baked tiling texture, ONE cheap fetch) that carves crisp,
            // resolution-independent grain into the rendered surface — the sharpness the smooth baked field can't reach.
            BrazeEditorGUI.SubLabel("Render Detail (sharpness)");
            BrazeEditorGUI.PropertyField(L("renderDetail"), new GUIContent("Render Detail",
                "Add crunchy, high-frequency surface grain at render time from a baked tiling noise (world-space → " +
                "sharper than any grid). Carves the rendered surface like a noise texture on a soft mesh. Free when off."));
            using (new EditorGUI.DisabledScope(!L("renderDetail").boolValue))
            {
                Draw01Slider(L("detailBoost"), new GUIContent("Amount",
                    "Strength of the render-time surface detail. 0 = off, 1 = strongest carving."));

                var sizeMin = L("detailSizeMin");
                var sizeMax = L("detailSizeMax");
                DrawMapped01Slider(sizeMin,
                    BrazeRenderSettings.GeneratedDetailFeatureSizeTo01,
                    BrazeRenderSettings.GeneratedDetailFeatureSizeFrom01,
                    new GUIContent("Feature Size - Min",
                        "Normalized fine-grain size. 0 = finest crisp grain, 1 = broadest fine grain."));
                if (!sizeMin.hasMultipleDifferentValues && !sizeMax.hasMultipleDifferentValues && sizeMax.floatValue < sizeMin.floatValue)
                    sizeMax.floatValue = sizeMin.floatValue;
                DrawMapped01Slider(sizeMax,
                    BrazeRenderSettings.GeneratedDetailFeatureSizeTo01,
                    BrazeRenderSettings.GeneratedDetailFeatureSizeFrom01,
                    new GUIContent("Feature Size - Max",
                        "Normalized broad-feature size. Values below Min are clamped to Min; the span controls octave count."));
                if (!sizeMin.hasMultipleDifferentValues && !sizeMax.hasMultipleDifferentValues && sizeMax.floatValue < sizeMin.floatValue)
                    sizeMax.floatValue = sizeMin.floatValue;

                DrawMapped01Slider(L("detailRoughness"),
                    BrazeRenderSettings.GeneratedDetailRoughnessTo01,
                    BrazeRenderSettings.GeneratedDetailRoughnessFrom01,
                    new GUIContent("Roughness",
                        "Balance between broad and fine octaves. 0 = smoother/broader, 1 = fine crunch."));
                DrawSeed01Slider(L("detailSeed"), new GUIContent("Randomness",
                    "Reroll the grain pattern. Displayed as a normalized control; stored as a stable seed."));
                DrawMapped01Slider(L("detailContrast"),
                    BrazeRenderSettings.GeneratedDetailSharpnessTo01,
                    BrazeRenderSettings.GeneratedDetailSharpnessFrom01,
                    new GUIContent("Sharpness",
                        "Hardness of the detail erosion. 0 = soft, 1 = crisp."));
                Draw01Slider(L("detailCurl"), new GUIContent("Wisp Warp",
                    "Domain-warp amount for wispy breakup. 0 = none, 1 = strongest."));
            }
        }

        static void Draw01Slider(SerializedProperty prop, GUIContent label)
        {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            float value = Mathf.Clamp01(prop.floatValue);
            EditorGUI.BeginChangeCheck();
            value = BrazeEditorGUI.Slider(label, value, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                prop.floatValue = value;
            EditorGUI.showMixedValue = false;
        }

        static void DrawMapped01Slider(
            SerializedProperty prop,
            System.Func<float, float> to01,
            System.Func<float, float> from01,
            GUIContent label)
        {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            float value = Mathf.Clamp01(to01(prop.floatValue));
            EditorGUI.BeginChangeCheck();
            value = BrazeEditorGUI.Slider(label, value, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                prop.floatValue = from01(value);
            EditorGUI.showMixedValue = false;
        }

        static void DrawSeed01Slider(SerializedProperty prop, GUIContent label)
        {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            float value = BrazeRenderSettings.GeneratedDetailSeedTo01(prop.intValue);
            EditorGUI.BeginChangeCheck();
            value = BrazeEditorGUI.Slider(label, value, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                prop.intValue = BrazeRenderSettings.GeneratedDetailSeedFrom01(value);
            EditorGUI.showMixedValue = false;
        }
    }
}
