using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeRenderFeature))]
    public sealed class BrazeRenderFeatureEditor : Editor
    {
        const string K = "BrazeFX.RenderFeature.";
        static readonly string[] PresetNames = { "PC", "Console", "Mobile", "Custom" };
        static readonly int[] PresetValues =
        {
            (int)BrazeRenderFeature.RenderQualityPreset.PC,
            (int)BrazeRenderFeature.RenderQualityPreset.Console,
            (int)BrazeRenderFeature.RenderQualityPreset.Mobile,
            (int)BrazeRenderFeature.RenderQualityPreset.Custom,
        };

        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            if (target is not BrazeRenderFeature feature) return;

            BrazeEditorGUI.BrandHeader("URP RENDER FEATURE");
            DrawQualityProfile(feature);
            DrawSystems();

            bool qualityChanged = false;
            qualityChanged |= DrawLiveVolumes();
            qualityChanged |= DrawGeneratedVolumes();
            if (qualityChanged) P("qualityPreset").intValue = (int)BrazeRenderFeature.RenderQualityPreset.Custom;

            serializedObject.ApplyModifiedProperties();
        }

        void DrawQualityProfile(BrazeRenderFeature feature)
        {
            bool group = BrazeEditorGUI.GroupBegin(K + "quality", "QUALITY PROFILE", 1);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "qualityPreset", "Renderer Preset"))
                {
                    SerializedProperty preset = P("qualityPreset");
                    EditorGUI.BeginChangeCheck();
                    BrazeEditorGUI.PillsInt("Preset", preset, PresetNames, PresetValues);
                    if (EditorGUI.EndChangeCheck())
                    {
                        var selected = (BrazeRenderFeature.RenderQualityPreset)preset.intValue;
                        Undo.RecordObject(feature, "Apply BrazeFX Renderer Preset");
                        serializedObject.ApplyModifiedProperties();
                        feature.ApplyQualityPreset(selected);
                        EditorUtility.SetDirty(feature);
                        serializedObject.Update();
                    }

                    switch ((BrazeRenderFeature.RenderQualityPreset)P("qualityPreset").intValue)
                    {
                        case BrazeRenderFeature.RenderQualityPreset.PC:
                            BrazeEditorGUI.Hint(
                                "Quality-first desktop profile: native-resolution live and generated volumes, full-pixel " +
                                "rendering, and no temporal interleaving. Start here for desktop authoring and capture.");
                            break;
                        case BrazeRenderFeature.RenderQualityPreset.Console:
                            BrazeEditorGUI.Hint(
                                "Stable console baseline: native-resolution output without temporal interleaving, " +
                                "with a lower march budget and stronger importance stepping than PC.");
                            break;
                        case BrazeRenderFeature.RenderQualityPreset.Mobile:
                            BrazeEditorGUI.Hint(
                                "Performance-first default: half-resolution volume rendering, reduced march budgets, " +
                                "and reconstruction. Select PC when native-resolution desktop output is required.");
                            break;
                        default:
                            BrazeEditorGUI.Hint(
                                "Custom preserves individually authored values. Select PC, Console, or Mobile to " +
                                "reapply that complete profile.");
                            break;
                    }
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(group);
        }

        void DrawSystems()
        {
            bool group = BrazeEditorGUI.GroupBegin(K + "systems", "SYSTEMS", 1);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "systemPasses", "Renderer Passes"))
                {
                    BrazeEditorGUI.ToggleButton("Particles", P("particles"));
                    BrazeEditorGUI.ToggleButton("Flipbook Players", P("flipbookPlayers"));
                    BrazeEditorGUI.ToggleButton("Trails", P("trails"));
                    BrazeEditorGUI.ToggleButton("CFD Analysis Slices", P("cfdAnalysisSlices"));
                    BrazeEditorGUI.ToggleButton("CFD Surface Pressure", P("cfdSurfacePressure"));
                    BrazeEditorGUI.ToggleButton("Live / Cache Volumes", P("liveSlices"));
                    BrazeEditorGUI.ToggleButton("Generated Volumes", P("generatedVolumes"));
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(group);
        }

        bool DrawLiveVolumes()
        {
            bool changed = false;
            bool group = BrazeEditorGUI.GroupBegin(K + "live", "LIVE / CACHE VOLUMES", 2);
            if (group)
            {
                using (new EditorGUI.DisabledScope(!P("liveSlices").boolValue))
                {
                    if (BrazeEditorGUI.Section(K + "liveQuality", "Quality"))
                    {
                        EditorGUI.BeginChangeCheck();
                        BrazeEditorGUI.PropertyField(P("sliceCount"), new GUIContent("Maximum Steps"));
                        BrazeEditorGUI.PropertyField(P("slicesPerVoxel"), new GUIContent("Samples Per Voxel"));
                        BrazeEditorGUI.PropertyField(P("sliceJitterStrength"), new GUIContent("Ray Jitter"));
                        BrazeEditorGUI.PropertyField(P("sliceDownsample"), new GUIContent("Resolution Divisor"));
                        BrazeEditorGUI.PropertyField(P("sliceHeightCap"), new GUIContent("Height Cap"));
                        changed |= EditorGUI.EndChangeCheck();

                        int downsample = P("sliceDownsample").intValue;
                        int heightCap = P("sliceHeightCap").intValue;
                        BrazeEditorGUI.Hint(downsample == 1 && heightCap == 0
                            ? "Native-resolution raymarching. This is the PC and Console preset path. These values " +
                              "are authoritative for every live container and cache player. Ray Jitter 0.75 is the " +
                              "validated balance between contour-band breakup and static grain."
                            : $"1/{downsample} resolution per axis. This is a deliberate performance trade and can " +
                              $"look soft at close range; height cap {(heightCap > 0 ? heightCap + " px" : "off")}. " +
                              "Switch to PC for uncapped native-resolution output.");
                    }
                    BrazeEditorGUI.EndSection();

                    if (BrazeEditorGUI.Section(K + "liveAdvanced", "Advanced / Experimental", false))
                    {
                        EditorGUI.BeginChangeCheck();
                        BrazeEditorGUI.PropertyField(P("sliceTemporalMode"), new GUIContent("Temporal Interleaving"));
                        var temporal = (BrazeRenderFeature.LiveSliceTemporalMode)P("sliceTemporalMode").intValue;
                        if (temporal != BrazeRenderFeature.LiveSliceTemporalMode.Off)
                        {
                            BrazeEditorGUI.PropertyField(P("sliceTemporalBlend"), new GUIContent("History Blend"));
                            BrazeEditorGUI.ToggleButton("Motion Reprojection", P("sliceMotionReproject"));
                            BrazeEditorGUI.ToggleButton("Compact History", P("sliceCompactHistory"));
                            EditorGUILayout.HelpBox(
                                "Temporal interleaving is experimental and is not used by any BrazeFX quality " +
                                "preset. Some Unity/URP versions can expose the rotating pixel pattern as visible " +
                                "checkerboarding. Use Off for release-quality output.", MessageType.Warning);
                        }

                        BrazeEditorGUI.SubHeader("IMPORTANCE MARCHING");
                        BrazeEditorGUI.ToggleButton("Adaptive Marching", P("sliceAdaptiveMarching"));
                        using (new EditorGUI.DisabledScope(!P("sliceAdaptiveMarching").boolValue))
                        {
                            BrazeEditorGUI.PropertyField(P("sliceAdaptiveMaxStep"), new GUIContent("Smoke Maximum Step"));
                            BrazeEditorGUI.PropertyField(P("sliceAdaptiveVariance"), new GUIContent("Detail Threshold"));
                            BrazeEditorGUI.ToggleButton("Adaptive Smooth Fire", P("sliceAdaptiveSmoothFire"));
                            using (new EditorGUI.DisabledScope(!P("sliceAdaptiveSmoothFire").boolValue))
                                BrazeEditorGUI.PropertyField(P("sliceAdaptiveFireMaxStep"),
                                    new GUIContent("Fire Maximum Step"));
                        }
                        changed |= EditorGUI.EndChangeCheck();
                    }
                    BrazeEditorGUI.EndSection();
                }
            }
            BrazeEditorGUI.GroupEnd(group);
            return changed;
        }

        bool DrawGeneratedVolumes()
        {
            bool changed = false;
            bool group = BrazeEditorGUI.GroupBegin(K + "generated", "GENERATED VOLUMES", 2);
            if (group)
            {
                using (new EditorGUI.DisabledScope(!P("generatedVolumes").boolValue))
                {
                    if (BrazeEditorGUI.Section(K + "generatedQuality", "Quality"))
                    {
                        EditorGUI.BeginChangeCheck();
                        BrazeEditorGUI.PropertyField(P("volumeRaymarchSteps"), new GUIContent("Maximum Steps"));
                        BrazeEditorGUI.PropertyField(P("volumeDownsample"), new GUIContent("Resolution Divisor"));
                        BrazeEditorGUI.PropertyField(P("volumeHeightCap"), new GUIContent("Height Cap"));
                        BrazeEditorGUI.PropertyField(P("volumeHighResolutionMode"),
                            new GUIContent("High Resolution Mode"));
                        BrazeEditorGUI.ToggleButton("Direct Composite", P("directComposite"));
                        BrazeEditorGUI.ToggleButton("Bilateral Upsample", P("bilateralUpsample"));
                        using (new EditorGUI.DisabledScope(!P("bilateralUpsample").boolValue))
                            BrazeEditorGUI.PropertyField(P("volumeReconstructionSharpness"),
                                new GUIContent("Reconstruction Sharpness"));
                        changed |= EditorGUI.EndChangeCheck();

                        int downsample = P("volumeDownsample").intValue;
                        int heightCap = P("volumeHeightCap").intValue;
                        BrazeEditorGUI.Hint(downsample == 1 && heightCap == 0
                            ? "Native-resolution generated-volume raymarching. Maximum Steps is authoritative for " +
                              "every generated volume."
                            : $"1/{downsample} resolution per axis with optional depth-guided reconstruction; " +
                              $"height cap {(heightCap > 0 ? heightCap + " px" : "off")}.");
                    }
                    BrazeEditorGUI.EndSection();

                    if (BrazeEditorGUI.Section(K + "generatedAdvanced", "Temporal / Cameras", false))
                    {
                        EditorGUI.BeginChangeCheck();
                        BrazeEditorGUI.PropertyField(P("temporalNoiseReduction"),
                            new GUIContent("Temporal Noise Reduction"));
                        BrazeEditorGUI.ToggleButton("Animated Ray Jitter", P("animatedRayJitter"));
                        BrazeEditorGUI.ToggleButton("Motion Reprojection", P("motionReproject"));
                        BrazeEditorGUI.SubHeader("EDITOR AND AUXILIARY CAMERAS");
                        BrazeEditorGUI.PropertyField(P("generatedSceneViewDownsample"),
                            new GUIContent("Scene View Divisor"));
                        BrazeEditorGUI.PropertyField(P("generatedSceneViewHeightCap"),
                            new GUIContent("Scene View Height Cap"));
                        BrazeEditorGUI.ToggleButton("Reflection Cameras", P("generatedReflectionCameras"));
                        changed |= EditorGUI.EndChangeCheck();

                        var mode = (BrazeRenderFeature.GeneratedHighResolutionMode)P("volumeHighResolutionMode").intValue;
                        if (mode != BrazeRenderFeature.GeneratedHighResolutionMode.FullEveryPixel)
                            EditorGUILayout.HelpBox(
                                "Generated-volume interleaving requires temporal noise reduction and motion " +
                                "reprojection. Use Full Every Pixel for the clean reference path.", MessageType.Info);
                        if (P("generatedReflectionCameras").boolValue)
                            EditorGUILayout.HelpBox(
                                "Reflection cameras can add a generated-volume raymarch for every rendered probe face.",
                                MessageType.Warning);
                    }
                    BrazeEditorGUI.EndSection();
                }
            }
            BrazeEditorGUI.GroupEnd(group);
            return changed;
        }
    }
}
