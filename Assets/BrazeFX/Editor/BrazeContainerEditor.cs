using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// BoxCollider-style face-drag resize handles for a <see cref="BrazeContainer"/> in the scene
    /// (the Unity-native equivalent of the publisher-owned three-js-volume prototype's ContainerResizeGizmo).
    /// Dragging a face edits the
    /// container's `size` field (NOT the Transform scale, so child emitters/forces aren't distorted);
    /// an off-center drag also recenters via the Transform position. Also draws the premium IMGUI
    /// inspector (one editor per type, so it lives in the same class as the scene handles).
    /// </summary>
    [CustomEditor(typeof(BrazeContainer))]
    [CanEditMultipleObjects]
    public sealed class BrazeContainerEditor : Editor
    {
        const string K = "BrazeFX.Container.";
        static double sliceBudgetRefreshAt;
        static bool sliceBudgetCached;
        static int cachedSliceCount, cachedSliceDownsample;
        static float cachedSlicesPerVoxel;
        static readonly GUIContent[] FpsLimitLabels =
        {
            new GUIContent("Project Settings"),
            new GUIContent("VSync"),
            new GUIContent("Uncapped"),
            new GUIContent("30 FPS"),
            new GUIContent("45 FPS"),
            new GUIContent("60 FPS"),
            new GUIContent("90 FPS"),
            new GUIContent("120 FPS"),
            new GUIContent("144 FPS"),
            new GUIContent("240 FPS"),
            new GUIContent("Custom…"),
        };

        readonly BoxBoundsHandle box = new BoxBoundsHandle();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CONTAINER", "container");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            // ── GRID: domain ──
            bool gGrid = BrazeEditorGUI.GroupBegin(K + "g_grid", "GRID", 1);
            if (gGrid)
            {
                if (BrazeEditorGUI.Section(K + "grid", "Grid"))
                {
                    SerializedProperty quality = P("qualityMode");
                    EditorGUI.BeginChangeCheck();
                    BrazeEditorGUI.PropertyField(quality, new GUIContent("Simulation Preset"));
                    bool qualityChanged = EditorGUI.EndChangeCheck();
                    if (qualityChanged && !quality.hasMultipleDifferentValues)
                    {
                        ApplyQualityPresetValues(serializedObject,
                            (BrazeContainer.QualityMode)quality.enumValueIndex);
                        // Apply immediately so every field drawn below reflects the selected preset this repaint.
                        serializedObject.ApplyModifiedProperties();
                        serializedObject.Update();
                    }

                    BrazeContainer.QualityMode selectedMode =
                        (BrazeContainer.QualityMode)P("qualityMode").enumValueIndex;
                    if (selectedMode != BrazeContainer.QualityMode.Custom)
                        BrazeEditorGUI.Hint("Selecting a preset immediately applies its visible grid, solver, Render Detail, " +
                                            "and shadow defaults. Final screen resolution and raymarch sampling stay under " +
                                            "Braze Render Feature. Physics Reference Res is preserved so world motion does " +
                                            "not change.");
                    BrazeEditorGUI.IntSlider(P("resolution"), 16, 512, new GUIContent("Resolution"));
                    BrazeEditorGUI.PropertyField(P("size"));
                    BrazeEditorGUI.PropertyField(P("physicsReferenceResolution"), new GUIContent("Physics Reference Res"));
                    BrazeEditorGUI.PropertyField(P("autoSubsteps"), new GUIContent("Resolution-Aware Substeps"));
                    if ((BrazeContainer.QualityMode)P("qualityMode").enumValueIndex == BrazeContainer.QualityMode.Custom)
                    {
                        BrazeEditorGUI.IntSlider(P("customMaxSubsteps"), 1, 32, new GUIContent("Max Auto Substeps"));
                        BrazeEditorGUI.Slider(P("cflTargetVoxels"), 0.5f, 8f, new GUIContent("CFL Target (voxels)"));
                        BrazeEditorGUI.PropertyField(P("customStrictCfl"), new GUIContent("Strict CFL"));
                        BrazeEditorGUI.IntSlider(P("customMemoryBudgetMB"), 64, 24576, new GUIContent("Memory Budget (MB)"));
                    }
                    // Live VRAM estimate so the cost of cranking resolution is visible while dragging.
                    var c = target as BrazeContainer;
                    if (c != null)
                    {
                        BrazeEditorGUI.Hint($"≈ {BrazeContainer.FormatBytes(c.EstimateVramBytes())} VRAM at this res " +
                                            "(dense grids scale ~res³). 64–192 = realtime; 256–512 = production / big-GPU " +
                                            "territory — watch this and the Stats HUD. Cost is memory, not compute (sparse solver).");
                        if (c.EstimateVramBytes() > 1024L * 1024L * 1024L)
                            EditorGUILayout.HelpBox("This container's estimated GPU allocation exceeds 1 GB. Treat it as a production/offline preset or lower Grid/Detail/Shadow resolution.", MessageType.Warning);
                        BrazeEditorGUI.Hint($"{c.qualityMode} simulation budget: {c.EffectiveSubstepBudget} max substeps, " +
                                            $"{c.EffectivePressureIterationBudget} max pressure iterations, " +
                                            $"{BrazeContainer.FormatBytes(c.EffectiveMemoryBudgetBytes)} memory, " +
                                            "sparse RBGS pressure. Camera-space quality comes from the active URP Renderer.");
                        if (!c.ValidateResourceBudget(c.AuthoredGridRes, out string rejection))
                            EditorGUILayout.HelpBox($"Simulation will not start: {rejection}", MessageType.Warning);
                    }
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(gGrid);

            // ── DYNAMICS: container-wide transport/stability. Source motion lives on each BrazePyroEmitter. ──
            bool gDyn = BrazeEditorGUI.GroupBegin(K + "g_dyn", "DYNAMICS", 3);
            if (gDyn)
            {
                if (BrazeEditorGUI.Section(K + "dynLife", "Life"))
                {
                    BrazeEditorGUI.Slider(P("smokeLife"), 0.1f, 60f, new GUIContent("Smoke Life (sec)"));
                    BrazeEditorGUI.Slider(P("fireLife"), 0.1f, 60f, new GUIContent("Fire Life (sec)"));
                    BrazeEditorGUI.Slider(P("wispFade"), 0f, 1f, new GUIContent("Wisp Fade"));
                    BrazeEditorGUI.Slider(P("ambientLife"), 0f, 1f, new GUIContent("Ambient Life"));
                    BrazeEditorGUI.Hint("Lifetimes in SECONDS (e-fold time). Smoke Life = how long the smoke takes to fade " +
                                        "(~63% gone after this long, mostly cleared after ~3–4×). Fire Life = how long the visible FLAME lasts; " +
                                        "heat persists ~1.5× and leftover fuel ~3× that. Use high Smoke Life for lingering nuke-style caps; keep " +
                                        "Fire Life lower so the fire core dies before the smoke. Wisp Fade erodes the thin diffused haze faster than " +
                                        "dense cores, so smoke breaks into wisps instead of fading as a soft 'wool' blob (Smoke Life dims it evenly; " +
                                        "Wisp Fade breaks it up). 0 = off.");
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "dynForces", "Forces"))
                {
                    BrazeEditorGUI.Slider(P("swirl"), 0f, 12f, new UnityEngine.GUIContent("Swirl (vorticity)"));
                    BrazeEditorGUI.Slider(P("turbulence"), 0f, 60f, new UnityEngine.GUIContent("Turbulence"));
                    BrazeEditorGUI.Slider(P("turbulenceScale"), 0.02f, 0.5f, new UnityEngine.GUIContent("Turbulence Scale"));
                    BrazeEditorGUI.Slider(P("billowStrength"), 0f, 60f, new UnityEngine.GUIContent("Billows"));
                    BrazeEditorGUI.Slider(P("billowScale"), 0.01f, 0.2f, new UnityEngine.GUIContent("Billow Scale"));
                    BrazeEditorGUI.Hint("GLOBAL fluid churn over the whole plume (the proven look layer). Swirl = vorticity confinement (rolls/curls — also feeds Render Detail). Turbulence/Billows = curl-noise break-up everywhere there's smoke. Per-emitter Motion adds source-local motion ON TOP of this.");

                    EditorGUILayout.Space(4);
                    BrazeEditorGUI.Slider(P("instability"), 0f, 1f, new UnityEngine.GUIContent("Instability"));
                    BrazeEditorGUI.Slider(P("instabilityScale"), 0.02f, 0.5f, new UnityEngine.GUIContent("Instability Scale"));
                    BrazeEditorGUI.Slider(P("interfaceTurbulence"), 0f, 60f, new UnityEngine.GUIContent("Interface Turbulence"));
                    BrazeEditorGUI.Slider(P("interfaceTurbScale"), 0.05f, 1f, new UnityEngine.GUIContent("Interface Turb Scale"));
                    BrazeEditorGUI.Hint("DEFINITION (base solver). Instability perturbs the buoyant lift so a clean blast " +
                                        "ROLLS UP into mushroom caps instead of one smooth dome (Rayleigh–Taylor seed). Interface Turbulence " +
                                        "concentrates fine churn on the smoke/air edge so it curdles into cauliflower florets. Both default to 0; " +
                                        "dial Instability ~0.3–0.8 first, then add Interface Turbulence. Floret fineness is still capped by grid resolution.");
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "dynStab", "Stability / Perf"))
                {
                    BrazeEditorGUI.Slider(P("maxSpeed"), 0f, 600f, new GUIContent("Max Speed"));
                    BrazeEditorGUI.Slider(P("velocityDissipation"), 0.9f, 1f);
                    BrazeEditorGUI.IntSlider(P("pressureIterations"), 1, 40);
                    BrazeEditorGUI.IntSlider(P("pressureCadence"), 1, 8, new GUIContent("Pressure Cadence"));
                    using (new EditorGUI.DisabledScope(P("pressureCadence").intValue <= 1))
                        BrazeEditorGUI.IntSlider(P("pressureTrickle"), 0, 6, new GUIContent("Cadence Trickle"));
                    BrazeEditorGUI.IntSlider(P("substeps"), 1, 8, new GUIContent("Substeps"));
                    if (Application.isPlaying && target is BrazeContainer live && live.CflBudgetLimited)
                        EditorGUILayout.HelpBox($"CFL quality limited: this flow asks for {live.RequiredCflSubsteps} substeps, " +
                                                $"but {live.qualityMode} schedules {live.ScheduledSubsteps}. Use Production, " +
                                                "raise the Custom budget, increase Sim Rate, or reduce source speed.", MessageType.Warning);
                    if ((BrazeContainer.QualityMode)P("qualityMode").enumValueIndex != BrazeContainer.QualityMode.Production &&
                        (P("pressureIterations").intValue > 6 || P("pressureCadence").intValue < 2 || P("substeps").intValue > 2))
                        EditorGUILayout.HelpBox("Above the realtime solve budget (Pressure 6, Cadence 2, Substeps 1–2). Keep this only for a measured visual requirement.", MessageType.Warning);
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.ToggleButton("Closed Bottom", P("closedBottom"));
                    BrazeEditorGUI.ToggleButton("Closed Top", P("closedTop"));
                    BrazeEditorGUI.ToggleButton("Closed Sides", P("closedSides"));
                    BrazeEditorGUI.Hint("Container-wide transport limits. Max Speed is the hard ceiling on how fast the fume " +
                                        "moves/rises (auto-raised to fit an emitter's Jet). Substeps splits advection into N steps/frame " +
                                        "so a fast jet/blast stays CRISP instead of smearing (2–4 for high Jet; cost ≈ +1 advection pass each). " +
                                        "Pressure Cadence runs the full solve every Nth step (cheaper sim; transport carries the gaps) — " +
                                        "Iterations = how good each solve is, Cadence = how often you solve. " +
                                        "Closed Bottom/Top/Sides set each face as an open outflow or a closed wall for pressure projection. " +
                                        "The solver's CLOCK (how often all of this runs) is Sim Rate in SYSTEM > Performance. " +
                                        "Emitter Motion controls source-local buoyancy, swirl, turbulence and billows.");
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(gDyn);

            // ── RENDERER: live drawing is owned by BrazeContainer.
            bool gRnd = BrazeEditorGUI.GroupBegin(K + "g_rnd", "RENDERER", P("renderVolume").boolValue ? 5 : 1);
            if (gRnd)
            {
                BrazeEditorGUI.ToggleButton("Render Volume", P("renderVolume"));
                if (P("renderVolume").boolValue)
                {
                    // Collapsed renderer: only the controls that make a real, orthogonal visual difference.
                    // Field adaptation (Density/Fire Gain), de-voxel (Smooth Voxels), empty-space skip, the whole
                    // shadow-bake quality cluster, source filter, edge fade, rolloff, etc. are now derived/fixed at
                    // smart defaults — see the render feature. One knob per visual axis.
                    // The curated block itself lives in BrazeRenderInspector.DrawVolumeLookControls and is drawn
                    // IDENTICALLY by the cache player's Override Look — edit it there so the two can't drift.
                    BrazeRenderInspector.DrawVolumeLookControls(K + "rnd", P,
                        n => P("slice" + char.ToUpperInvariant(n[0]) + n.Substring(1)),
                        drawSun: true, sliceActive: true, liveContainer: target as BrazeContainer);

                    DrawGlobalSliceBudget(target as BrazeContainer);
                }
            }
            BrazeEditorGUI.GroupEnd(gRnd);

            // ── SYSTEM: performance + stats ──
            bool gSys = BrazeEditorGUI.GroupBegin(K + "g_sys", "SYSTEM", 3);
            if (gSys)
            {
                if (BrazeEditorGUI.Section(K + "perf", "Performance"))
                {
                    BrazeEditorGUI.Slider(P("cullThreshold"), 0.001f, 0.1f);
                    BrazeEditorGUI.Hint("Cull Threshold trims wasted solve on invisible trace smoke. If 'Active Bricks %' " +
                                        "stays high (e.g. 85%) for a small effect, raise this — it's the main fix for the " +
                                        "FPS sliding as the sim runs. Fire is never culled.");

                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.IntSlider(P("simRate"), 10, 240, new GUIContent("Sim Rate (Hz)"));
                    BrazeEditorGUI.PropertyField(P("adaptiveRate"), new GUIContent("Adaptive Rate"));
                    using (new EditorGUI.DisabledScope(!P("adaptiveRate").boolValue))
                    {
                        BrazeEditorGUI.IntSlider(P("fastSimRate"), 24, 120, new GUIContent("Fast Sim Rate"));
                        BrazeEditorGUI.Slider(P("adaptiveFastSpeed"), 2f, 80f, new GUIContent("Fast Speed Threshold"));
                    }
                    if ((BrazeContainer.QualityMode)P("qualityMode").enumValueIndex != BrazeContainer.QualityMode.Production &&
                        (P("simRate").intValue > 60 || P("cullThreshold").floatValue < 0.01f))
                        EditorGUILayout.HelpBox("Above the realtime dispatch budget. Prefer Sim Rate ≤ 60 Hz and Cull Threshold ≥ 0.01 before increasing quality elsewhere.", MessageType.Warning);
                    BrazeEditorGUI.Hint("THIS container's solver clock — independent of the display and the rest of Unity. " +
                                        "The solver ticks at this rate; the slice renderer draws the latest solved field " +
                                        "between ticks. Adaptive Rate auto-raises the clock toward Fast Sim Rate while " +
                                        "emitters inject fast flow, then settles back on calm smoke. Cache baking captures " +
                                        "at Sim Rate, so clips stay speed-correct at any display rate.");

                    BrazeEditorGUI.Separator();
                    DrawFpsLimit(P("fpsLimit"), P("customFps"));
                    BrazeEditorGUI.Hint("FPS Limit is the optional APPLICATION display governor (targetFrameRate / vSync — " +
                                        "affects all of Unity, not just this container). 'Project Settings' leaves Unity " +
                                        "untouched; use it if your game owns its own frame cap. The solver no longer follows " +
                                        "this — Sim Rate above is its clock.");
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "stats", "Stats"))
                {
                    BrazeEditorGUI.ToggleButton("Show Stats", P("showStats"));
                    if (P("showStats").boolValue)
                        EditorGUILayout.HelpBox("Stats enables periodic GPU readback. Keep it off in shipping builds and FPS captures.", MessageType.Info);
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "debug", "Debug"))
                {
                    // Scene-view display gizmos (cell grid + dimension ruler).
                    BrazeEditorGUI.PropertyField(P("showCellGrid"));
                    using (new EditorGUI.DisabledScope(!P("showCellGrid").boolValue))
                        BrazeEditorGUI.Slider(P("cellGridOpacity"), 0f, 1f);
                    BrazeEditorGUI.PropertyField(P("showRuler"), new GUIContent("Show Ruler (ft)"));
                    using (new EditorGUI.DisabledScope(!P("showRuler").boolValue))
                        BrazeEditorGUI.Slider(P("rulerOpacity"), 0f, 1f, new GUIContent("Ruler Opacity"));

                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.ToggleButton("Show Bricks", P("showBricks"));
                    using (new EditorGUI.DisabledScope(!P("showBricks").boolValue))
                        BrazeEditorGUI.Slider(P("brickOpacity"), 0.02f, 0.6f);
                    BrazeEditorGUI.Hint("Scene-view occupancy bricks (Play mode): orange = raw content, blue = halo " +
                                        "padding. Shows exactly what the sparse solver pays for — the proof tool for the " +
                                        "indirect-dispatch + selective-detail work.");
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(gSys);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(targets.Length != 1))
                if (GUILayout.Button("Open Export Studio", GUILayout.Height(30f)))
                {
                    var container = (BrazeContainer)target;
                    BrazeExportWindow.OpenFor(container);
                }
        }

        /// <summary>Apply the visible authored defaults associated with a device/workflow preset. Budget enforcement
        /// remains runtime-owned by BrazeContainer; this method makes the inspector values honestly match the choice.</summary>
        internal static bool ApplyQualityPresetValues(SerializedObject so, BrazeContainer.QualityMode mode)
        {
            if (so == null || mode == BrazeContainer.QualityMode.Custom) return false;

            int resolution, pressure, cadence, trickle, substeps, simRate, fastSimRate;
            int detailScale, detailMemory, detailInterval, shadowSteps, shadowInterval;
            float cullThreshold;
            BrazeContainer.SliceShadowResolution shadowResolution;

            switch (mode)
            {
                case BrazeContainer.QualityMode.Mobile:
                    resolution = 64;
                    pressure = 4; cadence = 3; trickle = 1; substeps = 1;
                    simRate = 30; fastSimRate = 45; cullThreshold = 0.02f;
                    detailScale = 1; detailMemory = 64; detailInterval = 4;
                    shadowResolution = BrazeContainer.SliceShadowResolution.Half;
                    shadowSteps = 8; shadowInterval = 4;
                    break;
                case BrazeContainer.QualityMode.Realtime:
                    resolution = 128;
                    pressure = 6; cadence = 2; trickle = 2; substeps = 2;
                    simRate = 60; fastSimRate = 60; cullThreshold = 0.01f;
                    detailScale = 1; detailMemory = 512; detailInterval = 2;
                    shadowResolution = BrazeContainer.SliceShadowResolution.Half;
                    shadowSteps = 16; shadowInterval = 2;
                    break;
                default: // Production: safe 256³ starting point; the mode still permits a manual 512³ grid.
                    resolution = 256;
                    pressure = 12; cadence = 1; trickle = 0; substeps = 2;
                    simRate = 120; fastSimRate = 120; cullThreshold = 0.004f;
                    detailScale = 2; detailMemory = 2048; detailInterval = 1;
                    shadowResolution = BrazeContainer.SliceShadowResolution.Full;
                    shadowSteps = 32; shadowInterval = 1;
                    break;
            }

            so.FindProperty("qualityMode").enumValueIndex = (int)mode;
            so.FindProperty("resolution").intValue = resolution;
            so.FindProperty("autoSubsteps").boolValue = true;
            so.FindProperty("pressureIterations").intValue = pressure;
            so.FindProperty("pressureCadence").intValue = cadence;
            so.FindProperty("pressureTrickle").intValue = trickle;
            so.FindProperty("substeps").intValue = substeps;
            so.FindProperty("simRate").intValue = simRate;
            so.FindProperty("fastSimRate").intValue = fastSimRate;
            so.FindProperty("cullThreshold").floatValue = cullThreshold;
            so.FindProperty("detailRenderScale").intValue = detailScale;
            so.FindProperty("detailMemoryBudgetMB").intValue = detailMemory;
            so.FindProperty("detailBakeInterval").intValue = detailInterval;
            so.FindProperty("sliceShadowResolution").enumValueIndex = (int)shadowResolution;
            so.FindProperty("sliceShadowSteps").intValue = shadowSteps;
            so.FindProperty("sliceShadowFrameInterval").intValue = shadowInterval;
            return true;
        }

        static void DrawGlobalSliceBudget(BrazeContainer container)
        {
            if (EditorApplication.timeSinceStartup >= sliceBudgetRefreshAt)
            {
                sliceBudgetCached = TryGetGlobalSliceBudget(
                    out cachedSliceCount, out cachedSlicesPerVoxel, out cachedSliceDownsample);
                sliceBudgetRefreshAt = EditorApplication.timeSinceStartup + 1.0;
            }
            if (!sliceBudgetCached)
            {
                EditorGUILayout.HelpBox("No loaded BrazeRenderFeature has Live Slices enabled. Configure BrazeFX on the project's URP Renderer assets.", MessageType.Warning);
                if (GUILayout.Button("Configure URP Rendering")) BrazeUrpSetup.ConfigureFromMenu();
                return;
            }

            BrazeEditorGUI.Hint($"Authoritative slice quality (URP Renderer): {cachedSliceCount} max steps, " +
                                $"{cachedSlicesPerVoxel:0.##} samples/voxel, 1/{cachedSliceDownsample} render resolution. " +
                                "Every live container and cache player uses this camera-space policy.");
        }

        // BrazeFX.Editor deliberately has no direct URP assembly dependency. Read renderer-feature sub-assets
        // through SerializedObject so the Container inspector can still expose the real project-wide budget.
        static bool TryGetGlobalSliceBudget(out int sliceCount, out float slicesPerVoxel, out int sliceDownsample)
        {
            sliceCount = 0;
            slicesPerVoxel = 0f;
            sliceDownsample = 1;
            string[] rendererGuids = AssetDatabase.FindAssets("t:ScriptableRendererData");
            for (int g = 0; g < rendererGuids.Length; g++)
            {
                string path = AssetDatabase.GUIDToAssetPath(rendererGuids[g]);
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                for (int a = 0; a < assets.Length; a++)
                {
                    UnityEngine.Object asset = assets[a];
                    if (asset == null || asset.GetType().Name != "BrazeRenderFeature") continue;
                    var feature = new SerializedObject(asset);
                    SerializedProperty enabled = feature.FindProperty("liveSlices");
                    SerializedProperty count = feature.FindProperty("sliceCount");
                    SerializedProperty perVoxel = feature.FindProperty("slicesPerVoxel");
                    SerializedProperty downsample = feature.FindProperty("sliceDownsample");
                    if (enabled == null || !enabled.boolValue || count == null || perVoxel == null || downsample == null)
                        continue;
                    sliceCount = count.intValue;
                    slicesPerVoxel = perVoxel.floatValue;
                    sliceDownsample = downsample.intValue;
                    return true;
                }
            }
            return false;
        }

        static void DrawFpsLimit(SerializedProperty prop, SerializedProperty customProp)
        {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.Popup(new GUIContent("FPS Limit"), prop.enumValueIndex, FpsLimitLabels);
            if (EditorGUI.EndChangeCheck()) prop.enumValueIndex = next;
            EditorGUI.showMixedValue = false;

            // Custom = last option → let the user type a rate. Concrete rates (presets + Custom) are enforced
            // system-wide so every system shares one cadence (needed for a correct, in-sync bake + playback).
            if (prop.enumValueIndex == FpsLimitLabels.Length - 1)
            {
                EditorGUI.indentLevel++;
                BrazeEditorGUI.PropertyField(customProp, new GUIContent("Custom FPS"));
                EditorGUI.indentLevel--;
            }
        }

        void OnSceneGUI()
        {
            var c = (BrazeContainer)target;
            var t = c.transform;

            // Drive the handle in the container's OWN local space — scale INCLUDED in the matrix. This is the
            // idiomatic setup: Unity's slider projects the drag through the TRS once, so the handle tracks the
            // cursor 1:1 even when the box's dimensions come from a large Transform scale (face drag + shift/alt
            // all behave). `size` is the local box extent; the matrix scale (lossyScale) turns it into the world box.
            Vector3 ls = AbsScale(t.lossyScale);
            Matrix4x4 m = Matrix4x4.TRS(t.position, t.rotation, ls);
            using (new Handles.DrawingScope(m))
            {
                box.center = Vector3.zero;
                box.size = AbsScale(c.size);   // LOCAL size (pre-scale); the matrix applies lossyScale
                box.wireframeColor = new Color(1f, 0.6f, 0.2f, 1f);
                box.handleColor = new Color(1f, 0.85f, 0.35f, 1f);

                Vector3 oldSize = AbsScale(c.size);
                EditorGUI.BeginChangeCheck();
                box.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(c, "Resize Braze Container");
                    // Resize SYMMETRICALLY about the box centre so the Transform position never moves (pure size
                    // edit, no sliding). A face drag this frame moves one face by δ → box.size changes by δ (SIGNED:
                    // +δ growing, −δ shrinking). For a fixed centre the grabbed face must move δ and the opposite
                    // mirror, so the size changes by 2δ: newSize = oldSize + 2(box.size − oldSize) = 2·box.size − oldSize.
                    // (Using the signed size delta — not |center| — is what lets it BOTH grow and shrink.)
                    c.size = Vector3.Max(2f * box.size - oldSize, Vector3.one * 1e-3f);
                }
            }
        }

        static Vector3 AbsScale(Vector3 s)
            => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }
}
