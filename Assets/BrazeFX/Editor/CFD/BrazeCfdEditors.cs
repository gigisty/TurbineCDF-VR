using UnityEditor;
using UnityEngine;
using BrazeFX.CFD;

namespace BrazeFX.EditorTools
{
    // A mesh with Read/Write disabled aborts the SDF bake before any triangle reaches the GPU, so the body is
    // silently invisible to the solver. Every inspector that owns a bake source draws this loud error + fix.
    static class BrazeCfdMeshReadability
    {
        public static void Draw(Mesh mesh)
        {
            if (mesh == null || mesh.isReadable) return;
            EditorGUILayout.HelpBox("Read/Write is disabled on this mesh's importer, so its collision SDF cannot " +
                                    "bake — the solver sees NO solid here and airflow passes straight through.",
                MessageType.Error);
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) is ModelImporter importer &&
                GUILayout.Button("Enable Read/Write On Importer"))
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }
    }

    [CustomEditor(typeof(BrazeCfdDomain))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdDomainEditor : Editor
    {
        const string K = "BrazeFX.CFD.Domain.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD DOMAIN", "cfd");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "authoring", "AUTHORING"))
            {
                BrazeEditorGUI.ToggleButton("Show Advanced Container", P("showAdvancedContainer"));
                BrazeEditorGUI.Hint("The simple workflow needs only Domain, Wind Tunnel, and Test Body. Reveal the " +
                                    "underlying Container only for resolution/rendering diagnostics and expert tuning.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "environment", "ENVIRONMENT"))
            {
                BrazeEditorGUI.PropertyField(P("ambientTemperatureC"), new GUIContent("Ambient Temperature (°C)"));
                BrazeEditorGUI.PropertyField(P("temperatureRangeC"), new GUIContent("Temperature Range (°C)"));
                BrazeEditorGUI.PropertyField(P("thermalPersistenceSeconds"), new GUIContent("Thermal Persistence"));
                BrazeEditorGUI.Hint("Temperatures are ambient-relative. This release targets believable realtime " +
                                    "airflow rather than calibrated material heat transfer.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "airflow", "AIRFLOW"))
            {
                BrazeEditorGUI.PropertyField(P("thermalBuoyancy"), new GUIContent("Thermal Buoyancy"));
                BrazeEditorGUI.PropertyField(P("airDrag"), new GUIContent("Air Drag"));
                BrazeEditorGUI.PropertyField(P("maxAirSpeed"), new GUIContent("Max Air Speed"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "boundaries", "DOMAIN BOUNDARIES"))
            {
                BrazeEditorGUI.ToggleButton("Closed Sides", P("closedSides"));
                BrazeEditorGUI.ToggleButton("Closed Bottom", P("closedBottom"));
                BrazeEditorGUI.ToggleButton("Closed Top", P("closedTop"));
                BrazeEditorGUI.Hint("These settings define the unpatched area of the six outer faces. Velocity " +
                                    "inlet and pressure outlet patches override closure only inside their rectangles.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "quality", "QUALITY"))
            {
                BrazeEditorGUI.PropertyField(P("simulationRate"), new GUIContent("Simulation Rate (Hz)"));
                BrazeEditorGUI.ToggleButton("One Solver Step Per Frame", P("limitToOneSolverStepPerFrame"));
                BrazeEditorGUI.ToggleButton("Pressure Every Step", P("pressureEveryStep"));
                BrazeEditorGUI.ToggleButton("Thermal Occupancy", P("thermalOccupancy"));
                BrazeEditorGUI.PropertyField(P("sdfMaxResolution"), new GUIContent("SDF Resolution"));
                BrazeEditorGUI.ToggleButton("Full Domain With Patches", P("fullDomainSolveWithBoundaryPatches"));
                BrazeEditorGUI.ToggleButton("Full Domain Without Patches", P("fullDomainSolve"));
                BrazeEditorGUI.PropertyField(P("referenceAirDensityKgPerM3"),
                    new GUIContent("Reference Air Density (kg/m³)"));
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("The solver clock is independent of rendering. The one-step guard prevents a " +
                                    "low-FPS frame from immediately scheduling two full-domain solves. SDF " +
                                    "Resolution sizes the collision bake (largest domain axis) independently of " +
                                    "the sim grid — raise it for crisper collision walls without sim cost.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1) ((BrazeCfdDomain)target).ApplyContainerVisibility();

            if (targets.Length == 1)
            {
                var domain = (BrazeCfdDomain)target;
                if (domain.InletVolumeFlowM3PerSecond > 0f || domain.OutletVolumeFlowM3PerSecond > 0f)
                {
                    EditorGUILayout.Space(5f);
                    EditorGUILayout.LabelField("Boundary Flow Diagnostics", EditorStyles.boldLabel);
                    BrazeEditorGUI.LabelField("Inlet", $"{domain.InletVolumeFlowM3PerSecond:0.000} m³/s  ·  " +
                                                       $"{domain.InletMassFlowKgPerSecond:0.000} kg/s");
                    BrazeEditorGUI.LabelField("Outlet", $"{domain.OutletVolumeFlowM3PerSecond:0.000} m³/s  ·  " +
                                                        $"{domain.OutletMassFlowKgPerSecond:0.000} kg/s");
                    MessageType type = domain.MassImbalancePercent <= 5f ? MessageType.Info : MessageType.Warning;
                    EditorGUILayout.HelpBox($"Mass-flow imbalance: {domain.MassImbalancePercent:0.0}%", type);
                }
            }
        }
    }

    [CustomEditor(typeof(BrazeCfdWindTunnel))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdWindTunnelEditor : Editor
    {
        const string K = "BrazeFX.CFD.WindTunnel.";
        static readonly GUIContent[] AnalysisDisplays =
        {
            new("Scalar"), new("Vectors"), new("Scalar And Vectors")
        };
        static readonly GUIContent[] SmokeColors =
        {
            new("Solid Blue"), new("Solid Green"), new("Solid Red"), new("Custom"),
            new("Temperature"), new("Flow Speed")
        };
        static readonly GUIContent[] SmokeShapes =
        {
            new("Full Volume"), new("Sheet"), new("Tracer Rake"), new("Turbulent Volume"),
            new("Body Contact")
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD WIND TUNNEL", "wind_tunnel");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "study", "STUDY"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.Hint("Local +Z is the wind direction. Rotate this object to aim the wind; inlet and " +
                                    "outlet faces are selected and sized automatically.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "air", "AIR CONDITIONS"))
            {
                BrazeEditorGUI.PropertyField(P("airSpeed"), new GUIContent("Air Speed"));
                SerializedProperty inletShape = P("inletShape");
                BrazeEditorGUI.PropertyField(inletShape, new GUIContent("Inlet Shape"));
                if (!inletShape.hasMultipleDifferentValues &&
                    inletShape.enumValueIndex != (int)BrazeCfdInletShape.FullFace)
                {
                    // Emitter-style per-shape parameters: radius-driven shapes never show a width/height pair.
                    switch ((BrazeCfdInletShape)inletShape.enumValueIndex)
                    {
                        case BrazeCfdInletShape.Circle:
                            BrazeEditorGUI.PropertyField(P("inletRadius"), new GUIContent("Radius"));
                            break;
                        case BrazeCfdInletShape.Ring:
                            BrazeEditorGUI.PropertyField(P("inletRadius"), new GUIContent("Radius"));
                            BrazeEditorGUI.PropertyField(P("inletHubFraction"), new GUIContent("Hub Fraction"));
                            break;
                        default:   // Rectangle, Ellipse, Slot
                            BrazeEditorGUI.PropertyField(P("inletSize.x"), new GUIContent("Width"));
                            BrazeEditorGUI.PropertyField(P("inletSize.y"), new GUIContent("Height"));
                            break;
                    }
                    BrazeEditorGUI.PropertyField(P("inletOffset"), new GUIContent("Inlet Offset"));
                    BrazeEditorGUI.Hint("This Transform is the jet's on-wall handle: it snaps onto the inlet " +
                                        "face, slides across it to position the jet, and its roll spins the " +
                                        "shape. The outlet always spans its full face. For a free-floating " +
                                        "emitter INSIDE the domain, use a CFD Flow Source instead.");
                }
                BrazeEditorGUI.PropertyField(P("airTemperatureC"), new GUIContent("Air Temperature (°C)"));
                BrazeEditorGUI.PropertyField(P("flowRegime"), new GUIContent("Flow Regime"));
                if (P("flowRegime").hasMultipleDifferentValues ||
                    P("flowRegime").enumValueIndex == (int)BrazeCfdFlowRegime.Turbulent)
                {
                    BrazeEditorGUI.PropertyField(P("turbulenceIntensityPercent"),
                        new GUIContent("Turbulence Intensity (%)"));
                    BrazeEditorGUI.PropertyField(P("turbulenceEddyScale"),
                        new GUIContent("Eddy Size"));
                    BrazeEditorGUI.PropertyField(P("vorticityRetention"),
                        new GUIContent("Vorticity Retention"));
                    BrazeEditorGUI.Hint("The inlet fluctuation is divergence-free and preserves mean flow. " +
                                        "Vorticity Retention amplifies only vortices resolved by the CFD velocity field.");
                }
                BrazeEditorGUI.PropertyField(P("outletGaugePressure"), new GUIContent("Outlet Gauge Pressure"));
                BrazeEditorGUI.ToggleButton("Prevent Outlet Backflow", P("preventOutletBackflow"));
                BrazeEditorGUI.ToggleButton("Seal Tunnel Walls", P("sealTunnelWalls"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "preview", "PREVIEW"))
            {
                BrazeEditorGUI.ExclusiveToggleButtons("Analysis Slice", P("showAnalysisSlice"),
                                                      "3D Volume", P("showSmoke"));
                if (P("showAnalysisSlice").hasMultipleDifferentValues || P("showAnalysisSlice").boolValue)
                {
                    RestrictedEnumPopup(P("preview"), "Display",
                        AnalysisDisplays, 1);
                    BrazeEditorGUI.PropertyField(P("slicePlane"), new GUIContent("Plane"));
                    BrazeEditorGUI.PropertyField(P("sliceOffset"), new GUIContent("Position"));
                    BrazeEditorGUI.PropertyField(P("sliceQuantity"), new GUIContent("Quantity"));
                    BrazeEditorGUI.PropertyField(P("sliceColorMap"), new GUIContent("Color Map"));
                    BrazeEditorGUI.ToggleButton("Automatic Range", P("automaticSliceRange"));
                    if (P("automaticSliceRange").hasMultipleDifferentValues ||
                        !P("automaticSliceRange").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("sliceMinimum"), new GUIContent("Range Minimum"));
                        BrazeEditorGUI.PropertyField(P("sliceMaximum"), new GUIContent("Range Maximum"));
                    }
                    bool vectors = P("preview").hasMultipleDifferentValues ||
                                   P("preview").enumValueIndex == (int)BrazeCfdWindPreview.VectorField ||
                                   P("preview").enumValueIndex == (int)BrazeCfdWindPreview.ScalarAndVectors;
                    if (vectors)
                        BrazeEditorGUI.PropertyField(P("vectorColumns"), new GUIContent("Vector Density"));
                    BrazeEditorGUI.PropertyField(P("sliceOpacity"), new GUIContent("Slice Opacity"));
                }
                if (P("showSmoke").hasMultipleDifferentValues || P("showSmoke").boolValue)
                {
                    RestrictedEnumPopup(P("smokePreset"), "Color By",
                        SmokeColors, 1);
                    if (!P("smokePreset").hasMultipleDifferentValues &&
                        P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Custom)
                        BrazeEditorGUI.PropertyField(P("customSmokeColor"), new GUIContent("Custom Color"));
                    bool scalarColor = P("smokePreset").hasMultipleDifferentValues ||
                                       P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Temperature ||
                                       P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.FlowSpeed;
                    if (scalarColor)
                        BrazeEditorGUI.PropertyField(P("smokeColorContrast"),
                            new GUIContent("Color Contrast"));
                    bool flowSpeedColor = P("smokePreset").hasMultipleDifferentValues ||
                                          P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.FlowSpeed;
                    if (flowSpeedColor)
                        BrazeEditorGUI.PropertyField(P("smokeFlowEmphasis"),
                            new GUIContent("Flow Emphasis"));
                    RestrictedEnumPopup(P("smokePattern"), "Smoke Shape", SmokeShapes, 0);
                    BrazeEditorGUI.PropertyField(P("smokeDensity"), new GUIContent("Dye Density"));
                    BrazeEditorGUI.PropertyField(P("smokeOpacity"), new GUIContent("Dye Opacity"));
                    BrazeEditorGUI.PropertyField(P("smokeBrightness"), new GUIContent("Dye Brightness"));
                    // Surface the computed lifetime: "keeps filling" reports came from the auto value being
                    // invisible (slow tunnels legitimately stretch it toward the 30 s cap).
                    string autoPersistenceStatus = targets.Length == 1
                        ? $"≈ {((BrazeCfdWindTunnel)target).EffectiveSmokePersistenceSeconds:0.0} s (transit-matched)"
                        : "matched to tunnel transit time";
                    BrazeEditorGUI.ToggleButton("Auto Dye Persistence", P("automaticDyePersistence"),
                        autoPersistenceStatus);
                    if (P("automaticDyePersistence").hasMultipleDifferentValues ||
                        !P("automaticDyePersistence").boolValue)
                        BrazeEditorGUI.PropertyField(P("smokePersistenceSeconds"), new GUIContent("Dye Persistence"));
                }
                BrazeEditorGUI.ToggleButton("Show Scene Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Flow Speed gives the clearest 3D study view: blue/cyan marks slow wake flow, " +
                                    "green marks nominal flow, and yellow/red marks local acceleration. " +
                                    "Raise Flow Emphasis to fade the nominal green freestream so wake structure " +
                                    "pops; Temperature is useful only when the study contains a heat difference.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var tunnel = (BrazeCfdWindTunnel)target;
                tunnel.SyncManagedComponents(true);
                EditorGUILayout.HelpBox(tunnel.Status,
                    tunnel.CoolingRequested ? MessageType.Warning : MessageType.Info);
                if (tunnel.domain != null)
                {
                    BrazeEditorGUI.LabelField("Automatic Boundaries",
                        $"{tunnel.InletFace}  →  {tunnel.OutletFace}");
                    BrazeEditorGUI.LabelField("Volume Flow",
                        $"{tunnel.domain.InletVolumeFlowM3PerSecond:0.000} in / " +
                        $"{tunnel.domain.OutletVolumeFlowM3PerSecond:0.000} out m³/s");
                    BrazeEditorGUI.LabelField("Mass Imbalance", $"{tunnel.domain.MassImbalancePercent:0.0}%");
                }
                if (tunnel.showAnalysisSlice && tunnel.ManagedAnalysisSlice != null)
                    BrazeEditorGUI.LabelField("Analysis Slice", tunnel.ManagedAnalysisSlice.Status);
            }
        }

        static void RestrictedEnumPopup(SerializedProperty property, string label, GUIContent[] choices,
                                        int firstEnumValue)
        {
            Rect row = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var content = new GUIContent(label, property.tooltip);
            EditorGUI.BeginProperty(row, content, property);
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            int selected = Mathf.Clamp(property.enumValueIndex - firstEnumValue, 0, choices.Length - 1);
            EditorGUI.BeginChangeCheck();
            selected = EditorGUI.Popup(row, content, selected, choices);
            if (EditorGUI.EndChangeCheck()) property.enumValueIndex = selected + firstEnumValue;
            EditorGUI.showMixedValue = previousMixed;
            EditorGUI.EndProperty();
        }
    }

    [CustomEditor(typeof(BrazeCfdVisualizer))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdVisualizerEditor : Editor
    {
        const string K = "BrazeFX.CFD.Visualizer.";
        static readonly GUIContent[] AnalysisDisplays =
        {
            new("Scalar"), new("Vectors"), new("Scalar And Vectors")
        };
        static readonly GUIContent[] SmokeColors =
        {
            new("Solid Blue"), new("Solid Green"), new("Solid Red"), new("Custom"),
            new("Temperature"), new("Flow Speed")
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD VISUALIZER", "cfd");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "study", "ADVANCED STUDY"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.ToggleButton("Automatic Reference Speed", P("automaticReferenceSpeed"));
                if (P("automaticReferenceSpeed").hasMultipleDifferentValues ||
                    !P("automaticReferenceSpeed").boolValue)
                    BrazeEditorGUI.PropertyField(P("referenceSpeed"), new GUIContent("Reference Speed"));
                BrazeEditorGUI.Hint("Presentation only: existing Velocity Inlet and Pressure Outlet patches remain " +
                                    "the sole boundary conditions. Internal Flow Sources drive local circulation " +
                                    "without becoming boundaries. Automatic mode uses the strongest inlet or " +
                                    "Flow Source for flow colour, Cp normalization, and dye lifetime.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "preview", "PREVIEW"))
            {
                BrazeEditorGUI.ExclusiveToggleButtons("Analysis Slice", P("showAnalysisSlice"),
                                                      "3D Volume", P("showSmoke"));
                if (P("showAnalysisSlice").hasMultipleDifferentValues || P("showAnalysisSlice").boolValue)
                {
                    RestrictedEnumPopup(P("preview"), "Display", AnalysisDisplays, 1);
                    BrazeEditorGUI.PropertyField(P("slicePlane"), new GUIContent("Plane"));
                    BrazeEditorGUI.PropertyField(P("sliceOffset"), new GUIContent("Position"));
                    BrazeEditorGUI.PropertyField(P("sliceQuantity"), new GUIContent("Quantity"));
                    BrazeEditorGUI.PropertyField(P("sliceColorMap"), new GUIContent("Color Map"));
                    BrazeEditorGUI.ToggleButton("Automatic Range", P("automaticSliceRange"));
                    if (P("automaticSliceRange").hasMultipleDifferentValues ||
                        !P("automaticSliceRange").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("sliceMinimum"), new GUIContent("Range Minimum"));
                        BrazeEditorGUI.PropertyField(P("sliceMaximum"), new GUIContent("Range Maximum"));
                    }
                    bool vectors = P("preview").hasMultipleDifferentValues ||
                                   P("preview").enumValueIndex == (int)BrazeCfdWindPreview.VectorField ||
                                   P("preview").enumValueIndex == (int)BrazeCfdWindPreview.ScalarAndVectors;
                    if (vectors)
                        BrazeEditorGUI.PropertyField(P("vectorColumns"), new GUIContent("Vector Density"));
                    BrazeEditorGUI.PropertyField(P("sliceOpacity"), new GUIContent("Slice Opacity"));
                }
                if (P("showSmoke").hasMultipleDifferentValues || P("showSmoke").boolValue)
                {
                    RestrictedEnumPopup(P("smokePreset"), "Color By", SmokeColors, 1);
                    bool temperatureVolume = !P("smokePreset").hasMultipleDifferentValues &&
                        P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Temperature;
                    if (!P("smokePreset").hasMultipleDifferentValues &&
                        P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Custom)
                        BrazeEditorGUI.PropertyField(P("customSmokeColor"), new GUIContent("Custom Color"));
                    bool scalarColor = P("smokePreset").hasMultipleDifferentValues ||
                                       P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Temperature ||
                                       P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.FlowSpeed;
                    if (scalarColor)
                        BrazeEditorGUI.PropertyField(P("smokeColorContrast"), new GUIContent("Color Contrast"));
                    bool flowSpeed = P("smokePreset").hasMultipleDifferentValues ||
                                     P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.FlowSpeed;
                    if (flowSpeed)
                        BrazeEditorGUI.PropertyField(P("smokeFlowEmphasis"), new GUIContent("Flow Emphasis"));
                    BrazeEditorGUI.PropertyField(P("smokeOpacity"),
                        new GUIContent(temperatureVolume ? "Volume Opacity" : "Dye Opacity"));
                    BrazeEditorGUI.PropertyField(P("smokeBrightness"),
                        new GUIContent(temperatureVolume ? "Volume Brightness" : "Dye Brightness"));
                    if (!temperatureVolume)
                    {
                        string lifetime = targets.Length == 1
                            ? $"≈ {((BrazeCfdVisualizer)target).EffectiveSmokePersistenceSeconds:0.0} s (transit-matched)"
                            : "matched to flow transit time";
                        BrazeEditorGUI.ToggleButton("Auto Dye Persistence", P("automaticDyePersistence"), lifetime);
                        if (P("automaticDyePersistence").hasMultipleDifferentValues ||
                            !P("automaticDyePersistence").boolValue)
                            BrazeEditorGUI.PropertyField(P("smokePersistenceSeconds"),
                                new GUIContent("Dye Persistence"));
                    }
                    BrazeEditorGUI.Hint(temperatureVolume
                        ? "Temperature renders the solved fluid-temperature volume directly. Ambient air is " +
                          "suppressed so heated plumes remain visible; passive dye is optional in this mode."
                        : "Dye is injected by Velocity Inlet patches or internal Flow Sources with Inject Passive " +
                          "Dye enabled. The Visualizer changes presentation, never the boundary physics.");
                }
                bool temperatureSmoke = P("showSmoke").hasMultipleDifferentValues ||
                    (P("showSmoke").boolValue && !P("smokePreset").hasMultipleDifferentValues &&
                     P("smokePreset").enumValueIndex == (int)BrazeCfdSmokePreset.Temperature);
                bool temperatureSlice = P("showAnalysisSlice").hasMultipleDifferentValues ||
                    (P("showAnalysisSlice").boolValue && !P("sliceQuantity").hasMultipleDifferentValues &&
                     P("sliceQuantity").enumValueIndex == (int)BrazeCfdSliceQuantity.Temperature);
                if (temperatureSmoke || temperatureSlice)
                {
                    BrazeEditorGUI.ToggleButton("Auto From Solved Air", P("automaticTemperatureRange"));
                    if (P("automaticTemperatureRange").hasMultipleDifferentValues ||
                        P("automaticTemperatureRange").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("minimumAutomaticTemperatureSpanC"),
                            new GUIContent("Minimum Auto Span (°C)"));
                        BrazeEditorGUI.PropertyField(P("temperatureRangeResponseSeconds"),
                            new GUIContent("Range Response"));
                        BrazeEditorGUI.Hint("Auto range measures the transported fluid temperature after source " +
                                            "injection, SDF clipping, advection, and cooling. Source Heat Rate is " +
                                            "°C/s and is never used as a colour range.");
                    }
                    else
                        BrazeEditorGUI.Hint("Fixed mode reuses the Heat Map range and is recommended when comparing " +
                                            "separate runs with one common colour scale.");
                }
                BrazeEditorGUI.ToggleButton("Show Scene Gizmo", P("showGizmo"));
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var visualizer = (BrazeCfdVisualizer)target;
                visualizer.SyncManagedComponents(true);
                bool warning = visualizer.Status.Contains("Enable Inject") ||
                               visualizer.Status.Contains("No moving") ||
                               visualizer.Status.Contains("Remove one");
                EditorGUILayout.HelpBox(visualizer.Status, warning ? MessageType.Warning : MessageType.Info);
                BrazeEditorGUI.LabelField("Reference Speed", $"{visualizer.ResolvedReferenceSpeed:0.###} units/s");
                BrazeEditorGUI.LabelField("Dye Sources", visualizer.DyeSourceCount.ToString());
                if (visualizer.TemperatureAutoRangeEnabled)
                {
                    BrazeEditorGUI.LabelField("Measured Air Peak",
                        visualizer.TemperatureMeasurementValid
                            ? $"{visualizer.MeasuredPeakTemperatureC:0.0} °C"
                            : "Waiting for solved GPU field…");
                    BrazeEditorGUI.LabelField("Displayed Temperature Range",
                        $"{visualizer.DisplayedTemperatureMinimumC:0.0}–" +
                        $"{visualizer.DisplayedTemperatureMaximumC:0.0} °C (Auto)");
                }
                if (visualizer.showSmoke && visualizer.domain != null && visualizer.domain.HeatMap != null)
                    EditorGUILayout.HelpBox(visualizer.smokePreset == BrazeCfdSmokePreset.Temperature
                            ? (visualizer.automaticTemperatureRange
                                ? "Temperature volume takes render priority and uses the measured solved-air range."
                                : "Temperature volume takes render priority and reuses the Heat Map's fixed range.")
                            : "Smoke / Dye presentation takes render priority over Heat Map. Switch to the " +
                              "Analysis Slice or disable Smoke to view the Heat Map.", MessageType.Info);
                if (visualizer.showAnalysisSlice && visualizer.domain != null && visualizer.domain.HeatMap != null)
                    EditorGUILayout.HelpBox("Heat Map remains volumetric while the Analysis Slice is active. " +
                                            "Disable Heat Map for an unobstructed slice.", MessageType.Info);
                if (visualizer.showAnalysisSlice && visualizer.ManagedAnalysisSlice != null)
                    BrazeEditorGUI.LabelField("Analysis Slice", visualizer.ManagedAnalysisSlice.Status);
            }
        }

        static void RestrictedEnumPopup(SerializedProperty property, string label, GUIContent[] choices,
                                        int firstEnumValue)
        {
            Rect row = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var content = new GUIContent(label, property.tooltip);
            EditorGUI.BeginProperty(row, content, property);
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            int selected = Mathf.Clamp(property.enumValueIndex - firstEnumValue, 0, choices.Length - 1);
            EditorGUI.BeginChangeCheck();
            selected = EditorGUI.Popup(row, content, selected, choices);
            if (EditorGUI.EndChangeCheck()) property.enumValueIndex = selected + firstEnumValue;
            EditorGUI.showMixedValue = previousMixed;
            EditorGUI.EndProperty();
        }
    }

    [CustomEditor(typeof(BrazeCfdTestBody))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdTestBodyEditor : Editor
    {
        const string K = "BrazeFX.CFD.TestBody.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD TEST BODY", "test_body");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "OBJECT UNDER TEST"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.PropertyField(P("meshSource"), new GUIContent("Mesh Source"));
                if (!P("meshSource").hasMultipleDifferentValues &&
                    P("meshSource").objectReferenceValue is MeshFilter sourceFilter)
                    BrazeCfdMeshReadability.Draw(sourceFilter.sharedMesh);
                BrazeEditorGUI.Hint("One readable mesh drives both collision and the surface result overlay. " +
                                    "Collision is sampled from the mesh triangles; Solid Interior derives " +
                                    "winding-independent enclosure from exact polygon crossings.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "collision", "COLLISION"))
            {
                BrazeEditorGUI.PropertyField(P("boundaryMode"), new GUIContent("Representation"));
                if (!P("boundaryMode").hasMultipleDifferentValues &&
                    P("boundaryMode").enumValueIndex == (int)BrazeCfdSdfBoundaryMode.SurfaceShell)
                    BrazeEditorGUI.PropertyField(P("shellThickness"), new GUIContent("Shell Thickness"));
                else
                    BrazeEditorGUI.PropertyField(P("collisionOffset"), new GUIContent("Collision Offset"));
                BrazeEditorGUI.ToggleButton("Show Scene Gizmo", P("showGizmo"));
                BrazeEditorGUI.ToggleButton("Animated (Rigid Motion)", P("animatedBody"));
                if (P("animatedBody").boolValue)
                    BrazeEditorGUI.Hint("The collision follows the Mesh Source transform (including animated " +
                                        "parents) every solver tick and its surface velocity pushes and swirls " +
                                        "the air — spin it for a propeller. Deforming or skinned meshes are not " +
                                        "supported.");
                BrazeEditorGUI.ToggleButton("Show Collision SDF", P("showCollisionSdf"));
                if (P("showCollisionSdf").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("collisionSdfColor"), new GUIContent("Preview Color"));
                    BrazeEditorGUI.PropertyField(P("collisionSdfOpacity"), new GUIContent("Preview Opacity"));
                    BrazeEditorGUI.Hint("The translucent shell is the baked wall the SOLVER collides with — " +
                                        "triangle-sampled, with Collision Offset / Shell Thickness applied. Compare it " +
                                        "against the mesh to judge bake fidelity; raise the domain's SDF " +
                                        "Resolution if it looks too coarse. This is a GPU raymarch debug view; turn " +
                                        "it off for FPS captures and shipping demos.");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "material", "MATERIAL"))
            {
                BrazeEditorGUI.PropertyField(P("material"), new GUIContent("Material Preset"));
                BrazeEditorGUI.PropertyField(P("initialTemperatureC"), new GUIContent("Initial Temperature (°C)"));
                BrazeEditorGUI.PropertyField(P("materialThickness"), new GUIContent("Material Thickness"));
                BrazeEditorGUI.Hint("Material intent is stored now, but conductivity, heat capacity, and the mesh's " +
                                    "own surface temperature require the next solid heat-transfer phase.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "results", "SURFACE RESULTS"))
            {
                BrazeEditorGUI.ToggleButton("Show Surface Pressure", P("showSurfacePressure"));
                BrazeEditorGUI.PropertyField(P("suctionAt"), new GUIContent("Blue / Suction At"));
                BrazeEditorGUI.PropertyField(P("pressureAt"), new GUIContent("Red / Pressure At"));
                BrazeEditorGUI.PropertyField(P("suctionColor"), new GUIContent("Suction Color"));
                BrazeEditorGUI.PropertyField(P("neutralColor"), new GUIContent("Neutral Color"));
                BrazeEditorGUI.PropertyField(P("pressureColor"), new GUIContent("Pressure Color"));
                BrazeEditorGUI.PropertyField(P("overlayOpacity"), new GUIContent("Opacity"));
                BrazeEditorGUI.PropertyField(P("overlayOffset"), new GUIContent("Surface Offset"));
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var body = (BrazeCfdTestBody)target;
                body.SyncManagedComponents(true);
                if (body.ManagedSdfBoundary != null && !string.IsNullOrEmpty(body.ManagedSdfBoundary.BakeStatus))
                    EditorGUILayout.HelpBox("Collision SDF bake failed: " + body.ManagedSdfBoundary.BakeStatus,
                        MessageType.Error);
                EditorGUILayout.HelpBox(body.Status,
                    body.meshSource != null && body.meshSource.sharedMesh != null && !body.meshSource.sharedMesh.isReadable
                        ? MessageType.Warning : MessageType.Info);
                Vector3 load = body.RelativePressureLoadWorld;
                BrazeEditorGUI.LabelField("Relative Pressure Load",
                    $"({load.x:0.000}, {load.y:0.000}, {load.z:0.000})");
                BrazeEditorGUI.LabelField("Relative Drag", $"{body.RelativeDragLoad:0.000}");
                BrazeEditorGUI.LabelField("Relative Lift", $"{body.RelativeLiftLoad:0.000}");
                int bodyCount = 0;
                for (int i = 0; i < BrazeCfdTestBody.Active.Count; i++)
                    if (BrazeCfdTestBody.Active[i] != null && BrazeCfdTestBody.Active[i].isActiveAndEnabled &&
                        BrazeCfdTestBody.Active[i].domain == body.domain) bodyCount++;
                if (bodyCount > 1)
                    EditorGUILayout.HelpBox("Relative load is currently integrated across all Test Bodies in this " +
                                            "Domain. Per-body force isolation is a later diagnostics phase.",
                        MessageType.Warning);
                EditorGUILayout.HelpBox("Pressure and drag values remain comparative until dimensional and benchmark " +
                                        "validation gates pass.", MessageType.Warning);
                if (body.ManagedSdfBoundary != null && GUILayout.Button("Rebuild Collision SDF"))
                    body.ManagedSdfBoundary.MarkDirty();
            }
        }
    }

    [CustomEditor(typeof(BrazeCfdBoundaryPatch))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdBoundaryPatchEditor : Editor
    {
        const string K = "BrazeFX.CFD.BoundaryPatch.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty mode = serializedObject.FindProperty("mode");
            bool outlet = !mode.hasMultipleDifferentValues &&
                          mode.enumValueIndex == (int)BrazeCfdBoundaryPatchMode.PressureOutlet;
            BrazeEditorGUI.Header(outlet ? "CFD PRESSURE OUTLET" : "CFD VELOCITY INLET",
                                  outlet ? "outlet" : "inlet");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "placement", "DOMAIN FACE"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.PropertyField(mode, new GUIContent("Condition"));
                BrazeEditorGUI.PropertyField(P("face"), new GUIContent("Domain Face"));
                SerializedProperty patchShape = P("shape");
                BrazeEditorGUI.PropertyField(patchShape, new GUIContent("Patch Shape"));
                if (!patchShape.hasMultipleDifferentValues)
                {
                    switch ((BrazeCfdPatchShape)patchShape.enumValueIndex)
                    {
                        case BrazeCfdPatchShape.Circle:
                            BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                            break;
                        case BrazeCfdPatchShape.Ring:
                            BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                            BrazeEditorGUI.PropertyField(P("ringInnerFraction"), new GUIContent("Hub Fraction"));
                            break;
                        default:   // Rectangle, Ellipse, Slot
                            BrazeEditorGUI.PropertyField(P("size.x"), new GUIContent("Width"));
                            BrazeEditorGUI.PropertyField(P("size.y"), new GUIContent("Height"));
                            break;
                    }
                }
                else
                    BrazeEditorGUI.PropertyField(P("size"), new GUIContent("Patch Size"));
                BrazeEditorGUI.PropertyField(P("faceOffset"), new GUIContent("Face Offset"));
                BrazeEditorGUI.PropertyField(P("faceRotation"), new GUIContent("Face Rotation"));
                BrazeEditorGUI.Hint("Move the Transform to set the centre along the selected face. The solver " +
                                    "snaps its normal coordinate exactly onto that outer domain face.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "condition", outlet ? "PRESSURE OUTLET" : "VELOCITY INLET"))
            {
                if (mode.hasMultipleDifferentValues || !outlet)
                {
                    BrazeEditorGUI.PropertyField(P("normalSpeed"), new GUIContent("Normal Speed"));
                    BrazeEditorGUI.PropertyField(P("tangentialVelocity"), new GUIContent("Tangential Velocity"));
                    BrazeEditorGUI.ToggleButton("Set Inlet Temperature", P("setInletTemperature"));
                    if (P("setInletTemperature").hasMultipleDifferentValues || P("setInletTemperature").boolValue)
                        BrazeEditorGUI.PropertyField(P("inletTemperatureC"), new GUIContent("Temperature (°C)"));
                    BrazeEditorGUI.ToggleButton("Inject Passive Dye", P("setInletSmoke"));
                    if (P("setInletSmoke").hasMultipleDifferentValues || P("setInletSmoke").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("inletSmokePattern"), new GUIContent("Dye Pattern"));
                        BrazeEditorGUI.PropertyField(P("inletSmokeDensity"), new GUIContent("Dye Density"));
                    }
                    BrazeEditorGUI.Hint("Velocity is a face-locked Dirichlet condition, reapplied after pressure " +
                                        "projection. Positive normal speed always points into the domain.");
                }
                if (mode.hasMultipleDifferentValues || outlet)
                {
                    BrazeEditorGUI.PropertyField(P("gaugePressure"), new GUIContent("Gauge Pressure"));
                    BrazeEditorGUI.ToggleButton("Prevent Backflow", P("preventBackflow"));
                    BrazeEditorGUI.Hint("The outlet supplies pressure to the projection boundary. Keep gauge " +
                                        "pressure at zero until dimensional pressure calibration is enabled.");
                }
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
            }
            BrazeEditorGUI.EndSection();
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(BrazeCfdSdfBoundary))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdSdfBoundaryEditor : Editor
    {
        const string K = "BrazeFX.CFD.SdfBoundary.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD SDF BOUNDARY", "sdf");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "DOMAIN AND MESH"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.PropertyField(P("meshFilter"), new GUIContent("Mesh Filter"));
                BrazeEditorGUI.Hint("The mesh is uploaded only for a static rebake. Enable Read/Write in its importer; " +
                                    "no triangle work or CPU readback occurs during steady-state simulation.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "boundary", "BOUNDARY"))
            {
                SerializedProperty mode = P("mode");
                BrazeEditorGUI.PropertyField(mode, new GUIContent("Mode"));
                if (!mode.hasMultipleDifferentValues &&
                    mode.enumValueIndex == (int)BrazeCfdSdfBoundaryMode.SurfaceShell)
                    BrazeEditorGUI.PropertyField(P("wallThickness"), new GUIContent("Wall Thickness"));
                else
                    BrazeEditorGUI.PropertyField(P("surfaceOffset"), new GUIContent("Surface Offset"));
                BrazeEditorGUI.ToggleButton("Animated (Rigid Motion)", P("animated"));
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint(mode.hasMultipleDifferentValues ||
                    mode.enumValueIndex == (int)BrazeCfdSdfBoundaryMode.SolidInterior
                    ? "Solid Interior derives enclosure from exact GPU polygon crossings, so mixed/reversed winding " +
                      "cannot project detached collision islands. Cut cells use fractional " +
                      "face coverage and an SDF-gradient free-slip wall normal."
                    : "Surface Shell supports open meshes and creates a wall centered on the source surface. " +
                      "Cut cells use fractional face coverage and an SDF-gradient free-slip wall normal.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var boundary = (BrazeCfdSdfBoundary)target;
                BrazeCfdMeshReadability.Draw(boundary.SourceMesh);
                if (!string.IsNullOrEmpty(boundary.BakeStatus))
                    EditorGUILayout.HelpBox("SDF bake failed: " + boundary.BakeStatus, MessageType.Error);
                if (GUILayout.Button("Rebuild SDF")) boundary.MarkDirty();
            }
        }
    }

    [CustomEditor(typeof(BrazeCfdFlowSource))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdFlowSourceEditor : Editor
    {
        const string K = "BrazeFX.CFD.FlowSource.";
        static readonly string[] ShapeNames = { "Sphere", "Box", "Cylinder" };
        static readonly int[] ShapeValues = { 0, 1, 2 };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD FLOW SOURCE", "inlet");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "DOMAIN"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.Hint("Only the selected domain sees this source. It never joins the Fire emitter registry.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "source", "FLOW"))
            {
                SerializedProperty shape = P("shape");
                BrazeEditorGUI.PillsIntGrid("Shape", shape, ShapeNames, ShapeValues, 3);
                if (!shape.hasMultipleDifferentValues && shape.intValue != (int)BrazeCfdSourceShape.Box)
                    BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                if (!shape.hasMultipleDifferentValues && shape.intValue == (int)BrazeCfdSourceShape.Cylinder)
                    BrazeEditorGUI.PropertyField(P("height"), new GUIContent("Height"));
                BrazeEditorGUI.PropertyField(P("speed"), new GUIContent("Speed"));
                BrazeEditorGUI.PropertyField(P("velocityResponse"), new GUIContent("Response"));
                SerializedProperty swirlMode = P("swirlMode");
                BrazeEditorGUI.PropertyField(swirlMode, new GUIContent("Swirl Mode"));
                bool swirlEnabled = swirlMode.hasMultipleDifferentValues ||
                                    swirlMode.enumValueIndex != (int)BrazeCfdFlowSwirlMode.Off;
                if (swirlEnabled)
                {
                    bool followsTransform = swirlMode.hasMultipleDifferentValues ||
                        swirlMode.enumValueIndex == (int)BrazeCfdFlowSwirlMode.FollowTransform;
                    if (followsTransform)
                    {
                        BrazeEditorGUI.PropertyField(P("rotationSource"), new GUIContent("Rotation Source"));
                        if (targets.Length == 1 && ((BrazeCfdFlowSource)target).rotationSource == null)
                            EditorGUILayout.HelpBox("Assign the Transform that actually rotates the propeller or fan.",
                                                    MessageType.Warning);
                    }
                    BrazeEditorGUI.PropertyField(P("swirlRatio"), new GUIContent("Swirl Ratio"));
                    if (targets.Length == 1 && Application.isPlaying && followsTransform)
                        BrazeEditorGUI.LabelField("Detected Rotation",
                            $"{((BrazeCfdFlowSource)target).DetectedRotorRpm:0.#} RPM");
                }
                BrazeEditorGUI.PropertyField(P("heatRateCPerSecond"), new GUIContent("Heat Rate (°C/s)"));
                BrazeEditorGUI.ToggleButton("Inject Passive Dye", P("injectPassiveDye"));
                if (P("injectPassiveDye").hasMultipleDifferentValues || P("injectPassiveDye").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("passiveDyeStrength"), new GUIContent("Dye Strength"));
                    SerializedProperty dyePattern = P("passiveDyePattern");
                    BrazeEditorGUI.PropertyField(dyePattern, new GUIContent("Dye Pattern"));
                    if (dyePattern.hasMultipleDifferentValues ||
                        dyePattern.enumValueIndex == (int)BrazeCfdFlowDyePattern.RotorStreaks)
                    {
                        BrazeEditorGUI.PropertyField(P("dyeStreakCount"), new GUIContent("Streak Count"));
                        BrazeEditorGUI.PropertyField(P("dyeStreakWidth"), new GUIContent("Streak Width"));
                    }
                }
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Flow points along local +Z. Box size comes from Transform scale. Passive dye " +
                                    "is a transported visualization tracer. Follow Transform reads rotation " +
                                    "direction only; Swirl Ratio controls the resolved wake strength.");
            }
            BrazeEditorGUI.EndSection();
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(BrazeCfdThermalSource))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdThermalSourceEditor : Editor
    {
        const string K = "BrazeFX.CFD.ThermalSource.";
        static readonly string[] ShapeNames = { "Sphere", "Box", "Cylinder" };
        static readonly int[] ShapeValues = { 0, 1, 2 };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD THERMAL SOURCE", "heater");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "DOMAIN"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "source", "HEATER"))
            {
                SerializedProperty shape = P("shape");
                BrazeEditorGUI.PillsIntGrid("Shape", shape, ShapeNames, ShapeValues, 3);
                if (!shape.hasMultipleDifferentValues && shape.intValue != (int)BrazeCfdSourceShape.Box)
                    BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                if (!shape.hasMultipleDifferentValues && shape.intValue == (int)BrazeCfdSourceShape.Cylinder)
                    BrazeEditorGUI.PropertyField(P("height"), new GUIContent("Height"));
                BrazeEditorGUI.PropertyField(P("heatRateCPerSecond"), new GUIContent("Heat Rate (°C/s)"));
                BrazeEditorGUI.PropertyField(P("coupling"), new GUIContent("Heat Coupling"));
                SerializedProperty coupling = P("coupling");
                if (coupling.hasMultipleDifferentValues ||
                    coupling.enumValueIndex == (int)BrazeCfdThermalCoupling.SurfaceToFluid)
                    BrazeEditorGUI.PropertyField(P("fluidShellThickness"),
                        new GUIContent("Fluid Shell Thickness"));
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Surface To Fluid transfers heat into the yellow fluid-side shell even when " +
                                    "the orange source overlaps a solid SDF. Heat Rate is a local °C/s source " +
                                    "term, not the component's resulting temperature.");
            }
            BrazeEditorGUI.EndSection();
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(BrazeCfdHeatMap))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdHeatMapEditor : Editor
    {
        const string K = "BrazeFX.CFD.HeatMap.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD HEAT MAP", "heatmap");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "DOMAIN"))
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "range", "TEMPERATURE RANGE"))
            {
                BrazeEditorGUI.PropertyField(P("minimumTemperatureC"), new GUIContent("Transparent Below (°C)"));
                BrazeEditorGUI.PropertyField(P("maximumTemperatureC"), new GUIContent("Hot At (°C)"));
                BrazeEditorGUI.PropertyField(P("coldColor"), new GUIContent("Cold"));
                BrazeEditorGUI.PropertyField(P("middleColor"), new GUIContent("Middle"));
                BrazeEditorGUI.PropertyField(P("hotColor"), new GUIContent("Hot"));
                BrazeEditorGUI.PropertyField(P("opacity"), new GUIContent("Opacity"));
                BrazeEditorGUI.Hint("Displays the maximum temperature encountered along each camera ray. It " +
                                    "reuses the GPU volume renderer and performs no CPU voxel sampling.");
            }
            BrazeEditorGUI.EndSection();
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(BrazeCfdSurfacePressure))]
    [CanEditMultipleObjects]
    public sealed class BrazeCfdSurfacePressureEditor : Editor
    {
        const string K = "BrazeFX.CFD.SurfacePressure.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("CFD SURFACE PRESSURE", "pressure");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "DOMAIN AND SURFACE"))
            {
                BrazeEditorGUI.PropertyField(P("domain"), new GUIContent("CFD Domain"));
                BrazeEditorGUI.PropertyField(P("meshFilter"), new GUIContent("Target Mesh"));
                BrazeEditorGUI.Hint("Use the same outward-normal mesh represented by the domain's SDF boundary. " +
                                    "Pressure is sampled from the existing GPU field; mesh vertices never read back.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "range", "CP DISPLAY RANGE"))
            {
                BrazeEditorGUI.PropertyField(P("minimumPressure"), new GUIContent("Suction At"));
                BrazeEditorGUI.PropertyField(P("maximumPressure"), new GUIContent("Pressure At"));
                BrazeEditorGUI.PropertyField(P("suctionColor"), new GUIContent("Suction"));
                BrazeEditorGUI.PropertyField(P("neutralColor"), new GUIContent("Neutral"));
                BrazeEditorGUI.PropertyField(P("pressureColor"), new GUIContent("Pressure"));
                BrazeEditorGUI.Hint("Cp is referenced to the domain-wide fluid mean and calibrated so the " +
                                    "resolved stagnation peak is +1. The range changes colors only; it does not " +
                                    "change the solver or the measured field.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "rendering", "RENDERING"))
            {
                BrazeEditorGUI.PropertyField(P("opacity"), new GUIContent("Opacity"));
                BrazeEditorGUI.PropertyField(P("surfaceOffset"), new GUIContent("Surface Offset"));
                BrazeEditorGUI.Hint("Surface Offset samples just outside the solid and also prevents z-fighting. " +
                                    "Increase it slightly if a coarse SDF places samples inside the wall.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "loads", "COMPARATIVE LOAD AXES"))
            {
                BrazeEditorGUI.PropertyField(P("dragAxis"), new GUIContent("Local Drag Axis"));
                BrazeEditorGUI.PropertyField(P("liftAxis"), new GUIContent("Local Lift Axis"));
                BrazeEditorGUI.Hint("Loads integrate pressure over GPU cut cells. They are stable A/B indicators, " +
                                    "not Newtons or certified drag/lift coefficients in this phase.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1)
            {
                var view = (BrazeCfdSurfacePressure)target;
                EditorGUILayout.HelpBox(view.Status, MessageType.Info);
                Vector3 load = view.RelativePressureLoadWorld;
                BrazeEditorGUI.LabelField("Relative Pressure Load (World)",
                    $"({load.x:0.000}, {load.y:0.000}, {load.z:0.000})");
                BrazeEditorGUI.LabelField("Relative Drag", $"{view.RelativeDragLoad:0.000}");
                BrazeEditorGUI.LabelField("Relative Lift", $"{view.RelativeLiftLoad:0.000}");
                EditorGUILayout.HelpBox("COMPARATIVE ONLY — do not report these values as engineering forces or " +
                                        "coefficients until dimensional and benchmark validation gates pass.",
                    MessageType.Warning);
            }
        }
    }

}
