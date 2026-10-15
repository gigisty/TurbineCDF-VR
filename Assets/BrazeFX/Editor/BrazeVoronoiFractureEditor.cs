using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazeVoronoiFracture"/> — DCC/Cinema4D theme, organized into
    /// collapsible cards (Sources / Cells / Object / Effectors / Detailing / Destruction / Forces / Age) with sub-headers,
    /// segmented pills, on/off toggle buttons, and inline hints.</summary>
    [InitializeOnLoad]
    [CustomEditor(typeof(BrazeVoronoiFracture))]
    [CanEditMultipleObjects]
    public sealed class BrazeVoronoiFractureEditor : Editor
    {
        const string K = "BrazeFX.Voronoi.";
        const int AutoPreviewFragmentLimit = 512;
        static readonly string[] DestructionModeNames = { "Manual", "Field" };
        static readonly int[] DestructionModeValues =
        {
            (int)BrazeVoronoiFracture.BreakMode.Manual,
            (int)BrazeVoronoiFracture.BreakMode.Field
        };

        static BrazeVoronoiFractureEditor()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelPendingDrafts;
            AssemblyReloadEvents.beforeAssemblyReload += CancelPendingDrafts;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                CancelPendingDrafts();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("VORONOI FRACTURE", "voronoi");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);
            var vf = target as BrazeVoronoiFracture;

            bool requestDraft = false;

            // ── SETUP ────────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "target", "SETUP"))
            {
                BrazeEditorGUI.ToggleButton("Hide Original", P("hideOriginal"));
                SerializedProperty bakeProperty = P("bake");
                SerializedProperty bakePathProperty = P("bakeAssetPath");
                EditorGUI.BeginChangeCheck();
                BrazeEditorGUI.PropertyField(bakeProperty, new GUIContent("Bake Asset"));
                if (EditorGUI.EndChangeCheck() && targets.Length == 1)
                {
                    var selectedBake = bakeProperty.objectReferenceValue as BrazeFractureBake;
                    bakePathProperty.stringValue = selectedBake != null
                        ? AssetDatabase.GetAssetPath(selectedBake) : string.Empty;
                }
                if (targets.Length == 1 && vf != null)
                {
                    string assignedPath = vf.bake != null ? AssetDatabase.GetAssetPath(vf.bake) : null;
                    string displayedPath = !string.IsNullOrWhiteSpace(bakePathProperty.stringValue)
                        ? bakePathProperty.stringValue : assignedPath ?? string.Empty;
                    EditorGUI.BeginChangeCheck();
                    displayedPath = EditorGUILayout.TextField(new GUIContent("Bake Path",
                        "Type or paste an Assets/ path, an absolute path inside this project, or a file:// URL."),
                        displayedPath);
                    if (EditorGUI.EndChangeCheck()) bakePathProperty.stringValue = displayedPath;

                    using (new EditorGUI.DisabledScope(Application.isPlaying || string.IsNullOrWhiteSpace(displayedPath)))
                    {
                        if (GUILayout.Button("Use Bake Path"))
                        {
                            bakePathProperty.stringValue = displayedPath;
                            serializedObject.ApplyModifiedProperties();
                            BrazeVoronoiFracture selected = vf;
                            string requestedPath = displayedPath;
                            EditorApplication.delayCall += () =>
                            {
                                BrazeFractureBake asset = CreateBakeAssetAtPath(selected, requestedPath);
                                if (asset != null) EditorGUIUtility.PingObject(asset);
                                else EditorUtility.DisplayDialog("Invalid Bake Path",
                                    "Enter a .asset path under this project's Assets folder. Absolute paths and " +
                                    "file:// URLs are accepted when they point inside this project.", "OK");
                            };
                            GUIUtility.ExitGUI();
                        }
                    }

                    BrazeEditorGUI.SubLabel(vf.TopologyChunkCount > 0 && vf.TopologyChunkCount != vf.SeedCount
                        ? $"{vf.SeedCount:n0} authored cells · {vf.TopologyChunkCount:n0} structural chunks"
                        : $"{vf.SeedCount:n0} fragments");
                }
                BrazeEditorGUI.Hint("Fractures the MeshFilter or SkinnedMeshRenderer on this object. The bake asset is " +
                                    "only the committed runtime result; transient drafts never write to it. Paste an " +
                                    "Assets/.../.asset path (or an absolute/file:// path inside the project). Bake writes " +
                                    "the matching .bytes payload beside it. Leave empty for automatic placement.");
            }
            BrazeEditorGUI.EndSection();

            // ── FRACTURE AUTHORING ──────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "authoring", "FRACTURE AUTHORING"))
            {
                SerializedProperty style = P("fractureStyle");
                EditorGUI.BeginChangeCheck();
                BrazeEditorGUI.PropertyField(style, new GUIContent("Fracture Style"));
                bool styleChanged = EditorGUI.EndChangeCheck();
                if (styleChanged && !style.hasMultipleDifferentValues)
                {
                    ApplyFractureStylePresetValues(serializedObject,
                        (BrazeVoronoiFracture.FractureStyle)style.enumValueIndex, vf);
                    serializedObject.ApplyModifiedProperties();
                    serializedObject.Update();
                    requestDraft = true;
                }

                EditorGUI.BeginChangeCheck();
                BrazeEditorGUI.PillsEnum("Pattern", P("fracturePattern"));
                var pattern = (BrazeVoronoiFracture.FracturePattern)P("fracturePattern").enumValueIndex;
                if (pattern == BrazeVoronoiFracture.FracturePattern.RadialSheet)
                {
                    int radialFragments = P("radialRings").intValue * P("radialRays").intValue + 1;
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.IntField(new GUIContent("Fragments", "Derived from Rings × Rays, plus the impact cell."),
                                                 radialFragments);
                }
                else
                {
                    BrazeEditorGUI.PropertyField(P("pointCount"), new GUIContent("Fragments"));
                }
                EditorGUILayout.BeginHorizontal();
                BrazeEditorGUI.PropertyField(P("seed"));
                if (GUILayout.Button("Randomize", GUILayout.Width(88f)))
                    P("seed").intValue = unchecked(P("seed").intValue * 1664525 + 1013904223);
                EditorGUILayout.EndHorizontal();

                if (pattern == BrazeVoronoiFracture.FracturePattern.Volume)
                {
                    BrazeEditorGUI.PillsEnum("Distribution", P("distribution"));
                    if (P("distribution").enumValueIndex == (int)BrazeVoronoiFracture.Distribution.Even)
                        BrazeEditorGUI.Slider(P("variation"), 0f, 1f, new GUIContent("Cell Variation"));

                    if (P("distribution").enumValueIndex == (int)BrazeVoronoiFracture.Distribution.Clustered)
                    {
                        BrazeEditorGUI.SubHeader("LEGACY CLUSTER");
                        BrazeEditorGUI.PropertyField(P("clusterCenter"));
                        BrazeEditorGUI.PropertyField(P("clusterRadius"));
                        BrazeEditorGUI.Slider(P("clusterStrength"), 0f, 1f);
                        BrazeEditorGUI.Slider(P("clusterFalloff"), 1f, 8f);
                    }

                    BrazeEditorGUI.ToggleButton("Noise Density", P("shaderSource"));
                    if (P("shaderSource").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("noiseScale"));
                        BrazeEditorGUI.PropertyField(P("noiseSeed"));
                        BrazeEditorGUI.Slider(P("densityContrast"), 0.05f, 8f);
                        BrazeEditorGUI.IntSlider(P("densityTries"), 1, 8);
                    }
                }
                else if (pattern == BrazeVoronoiFracture.FracturePattern.MultiScale)
                {
                    BrazeEditorGUI.Slider(P("variation"), 0f, 1f, new GUIContent("Macro Variation"));
                    BrazeEditorGUI.Slider(P("secondaryFraction"), 0f, 0.8f, new GUIContent("Micro Fragments"));
                    BrazeEditorGUI.PropertyField(P("impactPoint"));
                    BrazeEditorGUI.Slider(P("impactRadius"), 0.02f, 1f, new GUIContent("Impact Radius"));
                    BrazeEditorGUI.Hint("Orange Scene handles place the micro-shatter centre and normalized radius.");
                }
                else if (pattern == BrazeVoronoiFracture.FracturePattern.Fibres)
                {
                    BrazeEditorGUI.PropertyField(P("patternDirection"), new GUIContent("Grain Direction"));
                    BrazeEditorGUI.Slider(P("fibreSlenderness"), 1f, 12f, new GUIContent("Slenderness"));
                    BrazeEditorGUI.Slider(P("variation"), 0f, 1f, new GUIContent("Cross-section Variation"));
                    BrazeEditorGUI.Hint("The amber Scene handle rotates the grain independently of the object's local axes.");
                }
                else
                {
                    BrazeEditorGUI.PropertyField(P("impactPoint"));
                    BrazeEditorGUI.PropertyField(P("patternDirection"), new GUIContent("Sheet Normal"));
                    BrazeEditorGUI.IntSlider(P("radialRings"), 1, 16, new GUIContent("Rings"));
                    BrazeEditorGUI.IntSlider(P("radialRays"), 3, 48, new GUIContent("Rays"));
                    BrazeEditorGUI.Slider(P("radialJitter"), 0f, 1f, new GUIContent("Irregularity"));
                    BrazeEditorGUI.Slider(P("radialExtent"), 0.1f, 1f, new GUIContent("Sheet Coverage"));
                    BrazeEditorGUI.Hint("Blue Scene handles place the impact and orient the 2.5D fracture plane.");
                }

                if (pattern != BrazeVoronoiFracture.FracturePattern.Fibres)
                    BrazeEditorGUI.PropertyField(P("scaleCells"), new GUIContent("Cell Anisotropy"));

                bool geometryChanged = EditorGUI.EndChangeCheck();
                if (geometryChanged)
                {
                    if (pattern == BrazeVoronoiFracture.FracturePattern.RadialSheet &&
                        !P("radialRings").hasMultipleDifferentValues && !P("radialRays").hasMultipleDifferentValues)
                        P("pointCount").intValue = P("radialRings").intValue * P("radialRays").intValue + 1;
                    P("fractureStyle").enumValueIndex = (int)BrazeVoronoiFracture.FractureStyle.Custom;
                    requestDraft = true;
                }

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubHeader("PREVIEW");
                BrazeEditorGUI.PillsEnum("View", P("previewMode"));
                BrazeEditorGUI.Slider(P("previewExplode"), 0f, 3f, new GUIContent("Explode"));
                BrazeEditorGUI.ToggleButton("Wireframe", P("showWire"));
                BrazeEditorGUI.ToggleButton("Seeds", P("showSeeds"));
                BrazeEditorGUI.ToggleButton("Bounds", P("showBounds"));
                if (P("previewMode").enumValueIndex == (int)BrazeVoronoiFracture.AuthoringPreviewMode.Slice)
                {
                    BrazeEditorGUI.PropertyField(P("previewSliceNormal"), new GUIContent("Slice Normal"));
                    BrazeEditorGUI.Slider(P("previewSliceOffset"), -0.75f, 0.75f, new GUIContent("Slice Offset"));
                }
                BrazeEditorGUI.ToggleButton("Auto Preview", P("autoPreview"));
                if (P("autoPreview").boolValue && P("pointCount").intValue > AutoPreviewFragmentLimit)
                    BrazeEditorGUI.Hint($"Auto Preview pauses above {AutoPreviewFragmentLimit:n0} fragments to avoid " +
                                        "blocking the editor. Update Draft still builds the exact full result on demand.");

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Update Draft"))
                {
                    serializedObject.ApplyModifiedProperties();
                    QueueDraftForTargets(immediate: true, force: true);
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Commit Bake"))
                {
                    serializedObject.ApplyModifiedProperties();
                    QueueBakeForTargets();
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();

                if (vf != null && vf.IsAuthoringResultCurrent)
                {
                    string countLabel = vf.TopologyChunkCount > 0 && vf.TopologyChunkCount != vf.SeedCount
                        ? $"{vf.SeedCount:n0} cells → {vf.TopologyChunkCount:n0} topology chunks"
                        : $"{vf.SeedCount:n0} fragments";
                    BrazeEditorGUI.SubLabel(vf.IsAuthoringDraft
                        ? $"Draft current · {countLabel}"
                        : $"Committed bake current · {countLabel}");
                }
                else if (vf != null && vf.HasAuthoringResult)
                    EditorGUILayout.HelpBox("Previous result shown in amber. The authored pattern changed; Update Draft " +
                                            "to compare the new exact fracture, then Commit Bake when satisfied.", MessageType.Info);
                else
                    BrazeEditorGUI.SubLabel("No fracture result yet — Update Draft to inspect it without writing an asset.");

                BrazeEditorGUI.Separator();
                DrawBakeControl();
            }
            BrazeEditorGUI.EndSection();

            // ── EFFECTORS ────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "effectors", "EFFECTORS"))
            {
                BrazeEditorGUI.ObjectDropList(P("effectors"),
                    "＋   Drop an effector here", "＋   Drop another effector", ResolveEffector, reorderable: true);
                BrazeEditorGUI.Hint("Direct position / rotation / scale transforms, evaluated top to bottom. " +
                                    "Only fragments inside each effector field are changed; no force or velocity is added.");
            }
            BrazeEditorGUI.EndSection();

            // ── DETAILING (render-time) ──────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "detail", "DETAILING"))
            {
                BrazeEditorGUI.SubLabel("OUTER SURFACE");
                BrazeEditorGUI.PropertyField(P("materialSource"), new GUIContent("Material Override"));
                BrazeEditorGUI.Slider(P("movingFragmentLightFloor"), 0f, 0.5f,
                    new GUIContent("Moving Light Floor"));
                BrazeEditorGUI.Hint("Empty keeps the source renderer's full PBR material look on the original outer " +
                                    "skin. Moving Light Floor only raises detached pixels that would otherwise fall " +
                                    "below the selected albedo visibility; highlights and normally lit faces are unchanged.");

                BrazeEditorGUI.SubHeader("INTERIOR FACES");
                BrazeEditorGUI.PropertyField(P("interiorMaterial"), new GUIContent("Interior Material"));
                BrazeEditorGUI.PropertyField(P("interiorColor"), new GUIContent("Fallback Color"));
                BrazeEditorGUI.Slider(P("interiorMetallic"), 0f, 1f, new GUIContent("Fallback Metallic"));
                BrazeEditorGUI.Slider(P("interiorSmoothness"), 0f, 1f, new GUIContent("Fallback Smoothness"));
                BrazeEditorGUI.ToggleButton("Use Material Texture", P("triplanarUV"));
                if (P("triplanarUV").boolValue)
                    BrazeEditorGUI.PropertyField(P("triplanarScale"), new GUIContent("Texture Tiling"));
                BrazeEditorGUI.Hint("The material's base colour/map, metallic, and smoothness are copied into the " +
                                    "existing chunk shader. No second material pass is created.");

                BrazeEditorGUI.SubHeader("INTERIOR SURFACE RELIEF");
                BrazeEditorGUI.ToggleButton("Surface Relief", P("enableDetailing"));
                if (P("enableDetailing").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("detailAmount"), new GUIContent("Strength"));
                    BrazeEditorGUI.PropertyField(P("detailScale"), new GUIContent("Frequency"));
                    BrazeEditorGUI.IntSlider(P("detailOctaves"), 1, 6, new GUIContent("Quality"));
                    BrazeEditorGUI.Slider(P("detailContrast"), 0f, 1f, new GUIContent("Tone Breakup"));
                }

                BrazeEditorGUI.SubHeader("FRACTURE EDGES");
                BrazeEditorGUI.ToggleButton("Fake Edge Wear", P("enableEdgeDetail"));
                if (P("enableEdgeDetail").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("edgeDetailColor"), new GUIContent("Edge Color"));
                    BrazeEditorGUI.Slider(P("edgeDetailWidth"), 0.25f, 8f, new GUIContent("Width (pixels)"));
                    BrazeEditorGUI.Slider(P("edgeDetailStrength"), 0f, 1f, new GUIContent("Strength"));
                    BrazeEditorGUI.Slider(P("edgeDetailNoise"), 0f, 1f, new GUIContent("Chipping"));
                    if (P("edgeDetailNoise").floatValue > 0.001f)
                        BrazeEditorGUI.PropertyField(P("edgeDetailScale"), new GUIContent("Chip Frequency"));
                }
                BrazeEditorGUI.Hint("Relief and edge wear are live shader effects: no displaced vertices, extra mesh, " +
                                    "draw calls, or fracture rebake. Quality controls only pixel-shader work.");
            }
            BrazeEditorGUI.EndSection();

            // ── DESTRUCTION (selection + inherent structural support) ──────────────────
            if (BrazeEditorGUI.Section(K + "destruction", "DESTRUCTION"))
            {
                BrazeEditorGUI.PillsInt("Mode", P("breakMode"), DestructionModeNames, DestructionModeValues);
                int mode = P("breakMode").intValue;

                if (mode == (int)BrazeVoronoiFracture.BreakMode.Field)
                {
                    BrazeEditorGUI.SubHeader("BREAK FIELD");
                    BrazeEditorGUI.PillsEnum("Shape", P("breakFieldShape"));
                    BrazeEditorGUI.PropertyField(P("breakField"), new GUIContent("Field Transform"));
                    BrazeEditorGUI.PropertyField(P("breakFieldSize"), new GUIContent("Size"));
                    BrazeEditorGUI.Slider(P("breakFalloff"), 0f, 1f, new GUIContent("Front Irregularity"));
                    BrazeEditorGUI.Slider(P("breakProgress"), 0f, 1f, new GUIContent("Progress"));
                    BrazeEditorGUI.PropertyField(P("breakSpeed"), new GUIContent("Auto Speed"));
                    BrazeEditorGUI.ToggleButton("Show Field", P("showField"));
                    BrazeEditorGUI.Hint("The field releases fragments instantly as it reaches them. Ramp Progress " +
                                        "(or move the field) to crumble progressively. Animatable from the timeline.");
                }

                BrazeEditorGUI.SubHeader("STRUCTURAL SUPPORT");
                BrazeEditorGUI.ToggleButton("Structural Support", P("structuralSupport"));
                if (P("structuralSupport").boolValue)
                {
                    BrazeEditorGUI.ToggleButton("Follow Braze Gravity", P("supportFollowsGravity"));
                    if (!P("supportFollowsGravity").boolValue)
                        BrazeEditorGUI.PropertyField(P("supportAnchorDirection"), new GUIContent("Anchor Direction"));
                    BrazeEditorGUI.Slider(P("supportAnchorThickness"), 0f, 2f, new GUIContent("Anchor Thickness"));
                    BrazeEditorGUI.Slider(P("lateralSupport"), 0f, 1f, new GUIContent("Lateral Support"));
                    BrazeEditorGUI.ToggleButton("Rigid Clusters", P("rigidClusters"));
                    if (P("rigidClusters").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("collapseMaterial"), new GUIContent("Impact Material"));
                        BrazeEditorGUI.IntSlider(P("clusterMinCells"), 1, 512, new GUIContent("Min Cluster Cells"));
                        if (P("collapseMaterial").enumValueIndex == (int)BrazeVoronoiFracture.CollapseMaterial.Custom)
                            BrazeEditorGUI.PropertyField(P("clusterBreakImpulse"), new GUIContent("Break Impulse"));
                        BrazeEditorGUI.Hint("RayFire-style: a section cut from the anchor detaches as ONE rigid piece " +
                                            "and falls, topples, and lands coherently. It shatters into cells only when a " +
                                            "landing or impact exceeds the material's Break Impulse. Falling tips count, " +
                                            "and long/heavy spans receive a modest stress boost. Islands below Min Cluster " +
                                            "Cells release as loose debris immediately.");
                    }
                    else
                    {
                        BrazeEditorGUI.IntSlider(P("collapseSpeed"), 1, 8, new GUIContent("Collapse Speed"));
                    }
                    BrazeEditorGUI.Hint("Shared-face connections are rooted at the boundary toward which Braze Gravity pulls (or the custom Anchor Direction). " +
                                        "When destruction removes that route, unsupported islands release into the Braze force stack. " +
                                        "The GPU graph runs only after topology changes, then sleeps completely.");

                    BrazeEditorGUI.SubHeader("COHESIVE COLLAPSE");
                    BrazeEditorGUI.ToggleButton("Cohesive Collapse", P("cohesiveCollapse"));
                    if (P("cohesiveCollapse").boolValue)
                    {
                        if (!P("rigidClusters").boolValue)
                            BrazeEditorGUI.PropertyField(P("collapseMaterial"), new GUIContent("Material Preset"));
                        else
                            BrazeEditorGUI.SubLabel("Uses the Impact Material selected above");
                        int material = P("collapseMaterial").enumValueIndex;
                        if (material == (int)BrazeVoronoiFracture.CollapseMaterial.Custom)
                        {
                            BrazeEditorGUI.IntSlider(P("cohesionChunkSize"), 1, 64, new GUIContent("Chunk Size"));
                            BrazeEditorGUI.Slider(P("cohesion"), 0f, 1f, new GUIContent("Cohesion"));
                            BrazeEditorGUI.Slider(P("cohesionDamping"), 0f, 1f, new GUIContent("Motion Matching"));
                            BrazeEditorGUI.Slider(P("cohesionBreakStrain"), 0.01f, 1f, new GUIContent("Break Strain"));
                            BrazeEditorGUI.PropertyField(P("cohesionDuration"), new GUIContent("Bond Duration"));
                            BrazeEditorGUI.Slider(P("cohesionTopple"), 0f, 3f, new GUIContent("Topple Response"));
                            BrazeEditorGUI.Slider(P("cohesionDebrisFraction"), 0f, 1f, new GUIContent("Immediate Rubble"));
                            BrazeEditorGUI.IntSlider(P("cohesionIterations"), 1, 4, new GUIContent("Solver Iterations"));
                        }
                        BrazeEditorGUI.Hint(CollapsePresetHint((BrazeVoronoiFracture.CollapseMaterial)material) +
                            " Continuous shared-face groups activate together; anchor cells remain unyielding and direct impact cells remain granular.");
                    }
                }

                BrazeEditorGUI.SubHeader("IMPACT TRIGGERS (GPU)");
                BrazeEditorGUI.ToggleButton("Receive Impacts", P("acceptImpacts"));
                if (P("acceptImpacts").boolValue)
                {
                    BrazeEditorGUI.IntSlider(P("maxImpactsPerFrame"), 1, 4096, new GUIContent("Commands / Frame"));
                    BrazeEditorGUI.PropertyField(P("maxImpactSpeed"), new GUIContent("Maximum Speed"));
                    if (vf != null && vf.DroppedImpactCount > 0)
                        EditorGUILayout.HelpBox($"{vf.DroppedImpactCount:n0} impact commands exceeded the queue capacity since this component was enabled.", MessageType.Warning);
                    BrazeEditorGUI.Hint("Impact Sources and QueueImpact feed this Destruction receiver. Commands batch into one GPU upload and dispatch; released cells then use the Forces & Release stack below.");
                }

                BrazeEditorGUI.Separator();
                using (new EditorGUI.DisabledScope(!Application.isPlaying))
                {
                    if (GUILayout.Button("Release All"))
                        foreach (var t in targets) (t as BrazeVoronoiFracture)?.ReleaseAll();
                }
                if (!Application.isPlaying)
                    BrazeEditorGUI.Hint("Enter Play to release. Manual mode still accepts impact triggers, Release Sphere, and Release All.");
            }
            BrazeEditorGUI.EndSection();

            // ── FORCES & RELEASE ─────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "forces", "FORCES & RELEASE"))
            {
                DrawForcesList(P("forces"));
                BrazeEditorGUI.Hint("ONLY the forces dropped here drive the fragments — a stray Gravity/Wind elsewhere " +
                                    "in the scene won't affect this fracture. Add a Braze Gravity to make them fall.");
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("releaseImpulseMin"), new GUIContent("Impulse Speed Min"));
                BrazeEditorGUI.PropertyField(P("releaseImpulse"), new GUIContent("Impulse Speed Max"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("releaseSpinMin"), new GUIContent("Spin Speed Min"));
                BrazeEditorGUI.PropertyField(P("releaseSpin"), new GUIContent("Spin Speed Max"));
                BrazeEditorGUI.PropertyField(P("thrustFrames"), new GUIContent("Spin Frames (min,max)"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("spinDamping"), new GUIContent("Spin Damping"));
                BrazeEditorGUI.PropertyField(P("linearDrag"), new GUIContent("Linear Drag"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton("Release On Play", P("releaseOnPlay"));
                BrazeEditorGUI.Hint("Every Destruction release samples signed Impulse and Spin speeds between their " +
                                    "Min/Max values. Negative impulse reverses the primary release direction. Impacts " +
                                    "add their source response; structural collapse intentionally suppresses impulse " +
                                    "but keeps randomized spin.");
            }
            BrazeEditorGUI.EndSection();

            // ── SECONDARY PARTICLES ──────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "debris", "SECONDARY PARTICLES"))
            {
                var debrisEmitters = P("debrisEmitters");
                var debrisEmitterPercentages = P("debrisEmitterSpawnPercentages");
                var debrisEmitterOwners = P("debrisEmitterSpawnOwners");
                DrawEmitterList(debrisEmitters, debrisEmitterPercentages, debrisEmitterOwners);
                if (HasEmitter(debrisEmitters))
                {
                    bool shrink = P("debrisFromShrinkingClumps").boolValue;
                    bool stick = P("stickToFragment").boolValue;
                    bool grain = P("spawnAtStart").boolValue;

                    // Burst on break-off — independent of the continuous sheds below. 0 = no burst.
                    BrazeEditorGUI.PropertyField(P("debrisPointsPerChunk"), new GUIContent("Burst / Fragment"));

                    // Continuous channels. Spawn While Shrinking is the master switch; the other toggles select streams.
                    BrazeEditorGUI.ToggleButton("Spawn While Shrinking", P("debrisFromShrinkingClumps"));
                    BrazeEditorGUI.ToggleButton("Stick To Fragment", P("stickToFragment"));
                    BrazeEditorGUI.ToggleButton("Spawn At Start", P("spawnAtStart"));

                    if (shrink && (stick || grain))
                        BrazeEditorGUI.PropertyField(P("debrisRatePerChunk"), new GUIContent("Rate / Fragment"));

                    if (stick)
                        BrazeEditorGUI.Hint("Stick To Fragment: particles ride each fragment rigidly (forces & inherit-velocity " +
                                            "ignored) and fade after the emitter's Start Lifetime. Needs Spawn While Shrinking on.");
                    if (grain)
                        BrazeEditorGUI.Hint("Spawn At Start: FREE grains shed from moving fragments (no attachment) that fall away — " +
                                            "runs alongside Stick. Needs Spawn While Shrinking on. Stops once a fragment settles.");
                    if ((shrink && (stick || grain)) && P("fragmentLifetime").floatValue <= 0f)
                        EditorGUILayout.HelpBox("With Lifetime at 0, free fragments persist and shed until they settle. Set " +
                            "Age → Lifetime above zero for them to shrink + dissolve as they shed.", MessageType.Info);

                    // Free-particle motion knobs — apply to burst (when not sticking) + the free shed stream.
                    if (!stick || (shrink && grain))
                    {
                        BrazeEditorGUI.Slider(P("debrisInheritVelocity"), 0f, 3f, new GUIContent("Inherit Velocity"));
                        BrazeEditorGUI.PropertyField(P("debrisScatter"), new GUIContent("Scatter"));
                    }
                    BrazeEditorGUI.PropertyField(P("maxDebrisPoints"), new GUIContent("Max / Frame"));
                }
                BrazeEditorGUI.Hint("Burst / Fragment = one puff when a fragment breaks off. Spawn While Shrinking enables " +
                                    "continuous secondary particles; Stick To Fragment adds attached riders, and Spawn At Start " +
                                    "adds free grains. Each emitter's percentage scales all of its streams independently; " +
                                    "new emitters start at 100%.");
            }
            BrazeEditorGUI.EndSection();

            // ── AGE ──────────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "age", "AGE"))
            {
                BrazeEditorGUI.PropertyField(P("fragmentLifetime"), new GUIContent("Lifetime"));
                if (P("fragmentLifetime").floatValue > 0f)
                {
                    BrazeEditorGUI.Slider(P("shrink"), 0f, 1f);
                    BrazeEditorGUI.Slider(P("dissolve"), 0f, 1f);
                    BrazeEditorGUI.PropertyField(P("dissolveScale"));
                    BrazeEditorGUI.PropertyField(P("dissolveEdge"));
                }
                BrazeEditorGUI.Hint("After release a fragment lives this many seconds, shrinking + dissolving away " +
                                    "(0 = persist forever).");
            }
            BrazeEditorGUI.EndSection();

            // ── COLLISION ────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "collision", "COLLISION"))
            {
                BrazeEditorGUI.ToggleButton("Shape-Aware", P("shapeAwareCollision"));
                if (P("shapeAwareCollision").boolValue)
                {
                    BrazeEditorGUI.IntSlider(P("collisionSamples"), 4, 64);
                    BrazeEditorGUI.Slider(P("collisionStrength"), 0.1f, 1f);
                    BrazeEditorGUI.Slider(P("angularResponse"), 0f, 1f);
                    BrazeEditorGUI.Slider(P("toppleStrength"), 0f, 4f, new GUIContent("Topple"));
                    BrazeEditorGUI.Slider(P("contactTipResponse"), 0f, 2f, new GUIContent("Tip Response"));
                    BrazeEditorGUI.PropertyField(P("contactRestSlop"), new GUIContent("Rest Slop"));
                    BrazeEditorGUI.PropertyField(P("contactAngularDamping"), new GUIContent("Contact Spin Damping"));
                    BrazeEditorGUI.PropertyField(P("sleepLinearSpeed"), new GUIContent("Sleep Linear"));
                    BrazeEditorGUI.PropertyField(P("sleepAngularSpeed"), new GUIContent("Sleep Angular"));
                    BrazeEditorGUI.PropertyField(P("sleepDelay"), new GUIContent("Sleep Delay"));
                }
                BrazeEditorGUI.Hint("Shape-Aware = each fragment's vertices are collision points rigidly attached to " +
                                    "its body, so chunks rest on faces/edges + tumble against Braze Particle Colliders " +
                                    "(no centroid-sphere sinking). Mass is derived automatically from each closed " +
                                    "fragment's volume; rigid clusters use mass-weighted COM, inertia, contacts, drag, " +
                                    "and restitution. Off = the cheap single sphere. Needs a Braze Particle Collider in the scene.");
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton("Self Collision", P("selfCollision"));
                if (P("selfCollision").boolValue)
                {
                    BrazeEditorGUI.Slider(P("selfCollisionStrength"), 0f, 1f, new GUIContent("Separation Strength"));
                    if (Application.isPlaying && vf != null)
                    {
                        string live = vf.SelfCollisionBodies > 0
                            ? $"live: {vf.SelfCollisionBodies:n0} responding bodies · {vf.SelfCollisionContacts:n0} unique contacts"
                            : "live: no fragment contacts this frame (fire at the fracture or Release All to see it react)";
                        if (vf.SelfCollisionSuppressedContacts > 0)
                            live += $" · {vf.SelfCollisionSuppressedContacts:n0} assembled responses muted";
                        if (vf.SelfCollisionWakeRequests > 0)
                            live += $" · {vf.SelfCollisionWakeRequests:n0} wake requests";
                        if (vf.SelfCollisionGpuBytes > 0)
                            live += $" · {vf.SelfCollisionGpuBytes / (1024f * 1024f):0.00} MB GPU";
                        BrazeEditorGUI.SubLabel(live);
                    }
                    BrazeEditorGUI.Hint("No-drop linked spatial grid + bounded sphere proxies. Pairs overlapping in " +
                                        "the assembled pose stay muted until they separate once, then collide normally. " +
                                        "Sleeping bodies wake on fragment contact. Four small dispatches; working buffers " +
                                        "allocate only after the feature is first used.");
                }
            }
            BrazeEditorGUI.EndSection();

            // ── SOFT BODY ────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "soft", "SOFT BODY"))
            {
                BrazeEditorGUI.ToggleButton("Soft Body", P("softBody"));
                if (P("softBody").boolValue) BrazeEditorGUI.Slider(P("softness"), 0f, 1f);
                BrazeEditorGUI.Hint("Cheap vertex-shader wobble on free fragments (scaled by speed) — no sim cost.");
            }
            BrazeEditorGUI.EndSection();

            // ── PARTICLE DEBUG ───────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "particles", "PARTICLE DEBUG"))
            {
                BrazeEditorGUI.ToggleButton("Show Particles", P("showParticles"));
                if (P("showParticles").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("particleSize"), new GUIContent("Size"));
                    BrazeEditorGUI.ToggleButton("Colour By Chunk", P("particleColorByChunk"));
                    if (!P("particleColorByChunk").boolValue)
                        BrazeEditorGUI.PropertyField(P("particleColor"), new GUIContent("Colour"));
                }
                BrazeEditorGUI.Hint("Play only. One point per fragment vertex, resolved live from the rigid-fragment " +
                                    "buffers — each vertex is currently bolted to its chunk's single particle (centroid + rotation).");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
            if (requestDraft) QueueDraftForTargets(immediate: false, force: false);
        }

        sealed class PendingDraft
        {
            public BrazeVoronoiFracture fracture;
            public double due;
        }

        static readonly System.Collections.Generic.Dictionary<int, PendingDraft> PendingDrafts = new();

        static void CancelPendingDrafts()
        {
            PendingDrafts.Clear();
            EditorApplication.update -= ServicePendingDrafts;
        }

        void QueueDraftForTargets(bool immediate, bool force)
        {
            foreach (var item in targets)
                if (item is BrazeVoronoiFracture fracture && fracture != null &&
                    (force || (fracture.autoPreview && fracture.pointCount <= AutoPreviewFragmentLimit)))
                    QueueDraft(fracture, immediate);
        }

        static void QueueDraft(BrazeVoronoiFracture fracture, bool immediate)
        {
            if (fracture == null || Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;
            PendingDrafts[fracture.BrazeInstanceId()] = new PendingDraft
            {
                fracture = fracture,
                due = EditorApplication.timeSinceStartup + (immediate ? 0.01 : 0.28),
            };
            EditorApplication.update -= ServicePendingDrafts;
            EditorApplication.update += ServicePendingDrafts;
        }

        static void ServicePendingDrafts()
        {
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                CancelPendingDrafts();
                return;
            }
            if (PendingDrafts.Count == 0)
            {
                EditorApplication.update -= ServicePendingDrafts;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            var ready = new System.Collections.Generic.List<int>();
            foreach (var pair in PendingDrafts)
                if (pair.Value.fracture == null || pair.Value.due <= now) ready.Add(pair.Key);

            for (int i = 0; i < ready.Count; i++)
            {
                int id = ready[i];
                if (!PendingDrafts.TryGetValue(id, out PendingDraft pending)) continue;
                PendingDrafts.Remove(id);
                if (pending.fracture != null) pending.fracture.BuildAuthoringPreview();
            }
        }

        void QueueBakeForTargets()
        {
            var fractures = new System.Collections.Generic.List<BrazeVoronoiFracture>();
            foreach (var item in targets)
                if (item is BrazeVoronoiFracture fracture && fracture != null) fractures.Add(fracture);
            EditorApplication.delayCall += () =>
            {
                for (int i = 0; i < fractures.Count; i++)
                    if (fractures[i] != null) fractures[i].Bake();
            };
        }

        /// <summary>Create (or reuse) the exact project asset selected by the artist and persist its component link.
        /// The binary writer derives the sidecar path from this .asset, so both files always stay together.</summary>
        internal static BrazeFractureBake CreateBakeAssetAtPath(BrazeVoronoiFracture fracture, string path)
        {
            if (fracture == null) return null;
            BrazeFractureBake asset = fracture.CreateBakeAssetAtPathForEditor(path);
            if (asset == null) return null;
            fracture.AssignBakeAssetForEditor(asset);
            return asset;
        }

        /// <summary>Copy visible geometry values for a built-in style. Values remain editable and independent after
        /// application; the first manual edit changes the label to Custom.</summary>
        public static bool ApplyFractureStylePresetValues(SerializedObject so,
            BrazeVoronoiFracture.FractureStyle style, BrazeVoronoiFracture fracture)
        {
            if (so == null || style == BrazeVoronoiFracture.FractureStyle.Custom) return false;

            Bounds bounds = fracture != null ? fracture.AuthoringBounds : new Bounds(Vector3.zero, Vector3.one);
            Vector3 size = bounds.size;
            Vector3 LongestAxis() => size.x >= size.y && size.x >= size.z ? Vector3.right
                : size.y >= size.z ? Vector3.up : Vector3.forward;
            Vector3 ThinnestAxis() => size.x <= size.y && size.x <= size.z ? Vector3.right
                : size.y <= size.z ? Vector3.up : Vector3.forward;

            void Int(string name, int value) => so.FindProperty(name).intValue = value;
            void Float(string name, float value) => so.FindProperty(name).floatValue = value;
            void Bool(string name, bool value) => so.FindProperty(name).boolValue = value;
            void Vec(string name, Vector3 value) => so.FindProperty(name).vector3Value = value;
            void Col(string name, Color value) => so.FindProperty(name).colorValue = value;

            Int("fractureStyle", (int)style);
            Vec("impactPoint", bounds.center);
            Vec("scaleCells", Vector3.one);
            Float("offsetFragments", 0f);
            Bool("showWire", true);
            Int("previewMode", (int)BrazeVoronoiFracture.AuthoringPreviewMode.Fragments);
            Bool("triplanarUV", true);
            Bool("shaderSource", false);
            so.FindProperty("interiorMaterial").objectReferenceValue = null;
            Float("interiorMetallic", 0f); Float("interiorSmoothness", 0.22f);
            Float("detailContrast", 0.28f);
            Bool("enableEdgeDetail", true); Float("edgeDetailWidth", 1.6f);
            Float("edgeDetailStrength", 0.7f); Float("edgeDetailNoise", 0.35f); Float("edgeDetailScale", 12f);
            Col("edgeDetailColor", new Color(0.07f, 0.055f, 0.045f, 1f));

            switch (style)
            {
                case BrazeVoronoiFracture.FractureStyle.Concrete:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.MultiScale);
                    Int("pointCount", 128); Float("variation", 0.82f);
                    Float("secondaryFraction", 0.38f); Float("impactRadius", 0.24f);
                    Bool("enableDetailing", true); Float("detailAmount", 0.42f);
                    Float("detailScale", 7f); Int("detailOctaves", 4);
                    Col("interiorColor", new Color(0.36f, 0.34f, 0.31f, 1f));
                    break;
                case BrazeVoronoiFracture.FractureStyle.Stone:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.Volume);
                    Int("pointCount", 56); Int("distribution", (int)BrazeVoronoiFracture.Distribution.Uniform);
                    Float("variation", 1f); Bool("shaderSource", true); Float("noiseScale", 1.35f);
                    Float("densityContrast", 1.4f); Int("densityTries", 3);
                    Bool("enableDetailing", true); Float("detailAmount", 0.58f);
                    Float("detailScale", 4.5f); Int("detailOctaves", 4);
                    Col("interiorColor", new Color(0.3f, 0.285f, 0.26f, 1f));
                    break;
                case BrazeVoronoiFracture.FractureStyle.Wood:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.Fibres);
                    Int("pointCount", 96); Vec("patternDirection", LongestAxis());
                    Float("fibreSlenderness", 7f); Float("variation", 0.72f);
                    Bool("enableDetailing", true); Float("detailAmount", 0.32f);
                    Float("detailScale", 8f); Int("detailOctaves", 3);
                    Col("interiorColor", new Color(0.52f, 0.31f, 0.14f, 1f));
                    Col("edgeDetailColor", new Color(0.12f, 0.055f, 0.018f, 1f));
                    break;
                case BrazeVoronoiFracture.FractureStyle.Glass:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.RadialSheet);
                    Int("radialRings", 5); Int("radialRays", 14); Int("pointCount", 71);
                    Vec("patternDirection", ThinnestAxis()); Float("radialJitter", 0.16f); Float("radialExtent", 0.94f);
                    Bool("enableDetailing", false); Float("detailAmount", 0f);
                    Col("interiorColor", new Color(0.52f, 0.68f, 0.76f, 1f));
                    Float("interiorSmoothness", 0.38f); Col("edgeDetailColor", new Color(0.1f, 0.28f, 0.36f, 1f));
                    Float("edgeDetailWidth", 1.15f); Float("edgeDetailNoise", 0.18f);
                    break;
                case BrazeVoronoiFracture.FractureStyle.Ceramic:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.RadialSheet);
                    Int("radialRings", 4); Int("radialRays", 12); Int("pointCount", 49);
                    Vec("patternDirection", ThinnestAxis()); Float("radialJitter", 0.38f); Float("radialExtent", 0.9f);
                    Bool("enableDetailing", true); Float("detailAmount", 0.12f);
                    Float("detailScale", 10f); Int("detailOctaves", 2);
                    Col("interiorColor", new Color(0.58f, 0.49f, 0.4f, 1f));
                    Float("edgeDetailWidth", 1.25f); Float("edgeDetailNoise", 0.25f);
                    break;
                default:
                    Int("fracturePattern", (int)BrazeVoronoiFracture.FracturePattern.Volume);
                    Int("pointCount", 64); Int("distribution", (int)BrazeVoronoiFracture.Distribution.Even);
                    Float("variation", 0.7f); Bool("enableDetailing", false); Float("detailAmount", 0.4f);
                    Col("interiorColor", new Color(0.28f, 0.25f, 0.22f, 1f)); Bool("enableEdgeDetail", false);
                    break;
            }
            return true;
        }

        void OnSceneGUI()
        {
            // Unity invokes scene callbacks for one Editor target at a time and forbids querying Editor.targets here.
            if (target is not BrazeVoronoiFracture fracture || fracture == null) return;
            Transform space = fracture.AuthoringSpace;
            if (space == null) return;
            Bounds bounds = fracture.AuthoringBounds;
            float localRef = Mathf.Max(bounds.size.magnitude, 1e-4f);
            float worldScale = Mathf.Max(Mathf.Abs(space.lossyScale.x),
                               Mathf.Max(Mathf.Abs(space.lossyScale.y), Mathf.Abs(space.lossyScale.z)));
            float worldRef = localRef * Mathf.Max(worldScale, 1e-4f);
            bool geometryChanged = false;
            bool previewChanged = false;

            if (fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.MultiScale ||
                fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.RadialSheet)
            {
                Handles.color = fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.MultiScale
                    ? new Color(1f, 0.42f, 0.12f) : new Color(0.25f, 0.72f, 1f);
                Vector3 worldImpact = space.TransformPoint(fracture.impactPoint);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(worldImpact, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(fracture, "Move Fracture Impact");
                    fracture.impactPoint = space.InverseTransformPoint(moved);
                    geometryChanged = true;
                }

                if (fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.MultiScale)
                {
                    EditorGUI.BeginChangeCheck();
                    float radius = Handles.RadiusHandle(Quaternion.identity, moved,
                                   fracture.impactRadius * worldRef);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(fracture, "Resize Fracture Impact");
                        fracture.impactRadius = Mathf.Clamp(radius / worldRef, 0.02f, 1f);
                        geometryChanged = true;
                    }
                }
            }

            if (fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.Fibres ||
                fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.RadialSheet)
            {
                Handles.color = fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.Fibres
                    ? new Color(0.84f, 0.56f, 0.18f) : new Color(0.25f, 0.72f, 1f);
                Vector3 localOrigin = fracture.fracturePattern == BrazeVoronoiFracture.FracturePattern.RadialSheet
                    ? fracture.impactPoint : bounds.center;
                Vector3 worldOrigin = space.TransformPoint(localOrigin);
                Vector3 localDirection = fracture.patternDirection.sqrMagnitude > 1e-8f
                    ? fracture.patternDirection.normalized : Vector3.up;
                Vector3 worldDirection = space.TransformDirection(localDirection).normalized;
                float length = HandleUtility.GetHandleSize(worldOrigin) * 0.75f;
                Vector3 endpoint = worldOrigin + worldDirection * length;
                Handles.DrawLine(worldOrigin, endpoint, 3f);
                EditorGUI.BeginChangeCheck();
                Vector3 movedEnd = Handles.PositionHandle(endpoint, Quaternion.identity);
                if (EditorGUI.EndChangeCheck() && (movedEnd - worldOrigin).sqrMagnitude > 1e-8f)
                {
                    Undo.RecordObject(fracture, "Rotate Fracture Direction");
                    fracture.patternDirection = space.InverseTransformDirection((movedEnd - worldOrigin).normalized).normalized;
                    geometryChanged = true;
                }
            }

            if (fracture.previewMode == BrazeVoronoiFracture.AuthoringPreviewMode.Slice)
            {
                Vector3 localNormal = fracture.previewSliceNormal.sqrMagnitude > 1e-8f
                    ? fracture.previewSliceNormal.normalized : Vector3.right;
                Vector3 worldNormal = space.TransformDirection(localNormal).normalized;
                Vector3 localPoint = bounds.center + localNormal * (fracture.previewSliceOffset * localRef);
                Vector3 worldPoint = space.TransformPoint(localPoint);
                Handles.color = new Color(0.3f, 0.9f, 1f);
                Handles.DrawWireDisc(worldPoint, worldNormal, HandleUtility.GetHandleSize(worldPoint) * 0.55f);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.Slider(worldPoint, worldNormal);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(fracture, "Move Fracture Slice");
                    Vector3 movedLocal = space.InverseTransformPoint(moved);
                    fracture.previewSliceOffset = Mathf.Clamp(Vector3.Dot(movedLocal - bounds.center, localNormal) / localRef,
                                                               -0.75f, 0.75f);
                    previewChanged = true;
                }
            }

            if (!geometryChanged && !previewChanged) return;
            if (geometryChanged) fracture.fractureStyle = BrazeVoronoiFracture.FractureStyle.Custom;
            EditorUtility.SetDirty(fracture);
            fracture.MarkAuthoringChanged();
            if (geometryChanged && fracture.autoPreview && fracture.pointCount <= AutoPreviewFragmentLimit)
                QueueDraft(fracture, immediate: false);
        }

        static string CollapsePresetHint(BrazeVoronoiFracture.CollapseMaterial material)
        {
            switch (material)
            {
                case BrazeVoronoiFracture.CollapseMaterial.Wood:
                    return "Wood — large fibrous sections, strong lateral motion matching, gradual tearing and very little immediate rubble.";
                case BrazeVoronoiFracture.CollapseMaterial.Metal:
                    return "Metal — very large coherent sheets, high ductility and the longest bond window. Highest temporary solver cost.";
                case BrazeVoronoiFracture.CollapseMaterial.Plastic:
                    return "Plastic — flexible medium chunks that bend substantially before separating.";
                case BrazeVoronoiFracture.CollapseMaterial.Glass:
                    return "Glass — small brittle groups, almost immediate bond failure and a high fraction of granular shards.";
                case BrazeVoronoiFracture.CollapseMaterial.Custom:
                    return "Custom — authored values above. Constraint work runs only during active Bond Duration windows.";
                default:
                    return "Cement — medium slabs topple coherently, crack at modest strain, then become mixed chunks and rubble.";
            }
        }

        // ── Bake button + red/green status light ─────────────────────────────────────
        static GUIStyle bakeStateStyle;

        void DrawBakeControl()
        {
            // green only when EVERY selected fracture is baked and current
            bool allValid = true;
            bool allDraftCurrent = true;
            bool canClear = false;
            foreach (var t in targets)
            {
                if (t is not BrazeVoronoiFracture f) continue;
                if (!f.IsBakeValid()) allValid = false;
                if (!f.IsAuthoringResultCurrent || !f.IsAuthoringDraft) allDraftCurrent = false;
                if (f.bake != null) canClear = true;
            }

            if (bakeStateStyle == null)
            {
                bakeStateStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel);
                bakeStateStyle.fontSize = 11;
                bakeStateStyle.alignment = TextAnchor.MiddleLeft;
            }

            var green = new Color(0.32f, 0.86f, 0.42f);
            var amber = new Color(1f, 0.62f, 0.2f);
            var red = new Color(0.96f, 0.32f, 0.24f);
            var c = allValid ? green : allDraftCurrent ? amber : red;

            var row = EditorGUILayout.GetControlRect(false, 26f);
            const float dot = 12f;
            var dotR = new Rect(row.x + 2f, row.y + (row.height - dot) * 0.5f, dot, dot);
            var lblR = new Rect(dotR.xMax + 7f, row.y, 130f, row.height);
            const float gap = 4f;
            const float clearW = 72f;
            float buttonsX = lblR.xMax + gap;
            float buttonsW = row.xMax - buttonsX;
            var bakeR = new Rect(buttonsX, row.y + 2f, Mathf.Max(0f, buttonsW - clearW - gap), row.height - 4f);
            var clearR = new Rect(bakeR.xMax + gap, row.y + 2f, clearW, row.height - 4f);

            RoundRect(dotR, c, new Vector4(dot, dot, dot, dot) * 0.5f);
            bakeStateStyle.normal.textColor = c;
            GUI.Label(lblR, allValid ? "Baked" : allDraftCurrent ? "Draft ready" : "Bake required", bakeStateStyle);

            if (GUI.Button(bakeR, allValid ? "Re-Commit" : allDraftCurrent ? "Commit Draft" : "Bake"))
            {
                serializedObject.ApplyModifiedProperties();
                // defer the (heavy) bake out of this GUI event — running it inline mutates state mid-layout and
                // desyncs the IMGUI group stack ("EndLayoutGroup must be called first").
                var bakers = new System.Collections.Generic.List<BrazeVoronoiFracture>();
                foreach (var t in targets) if (t is BrazeVoronoiFracture f && f != null) bakers.Add(f);
                EditorApplication.delayCall += () =>
                {
                    foreach (var f in bakers) if (f != null) f.Bake();
                };
                GUIUtility.ExitGUI();
            }

            using (new EditorGUI.DisabledScope(!canClear))
            {
                if (GUI.Button(clearR, "Clear"))
                {
                    serializedObject.ApplyModifiedProperties();
                    var clearers = new System.Collections.Generic.List<BrazeVoronoiFracture>();
                    foreach (var t in targets)
                        if (t is BrazeVoronoiFracture f && f != null && f.bake != null)
                            clearers.Add(f);

                    EditorApplication.delayCall += () =>
                    {
                        foreach (var f in clearers) if (f != null) f.ClearBakeCache();
                    };
                    GUIUtility.ExitGUI();
                }
            }

            if (!allValid && !allDraftCurrent)
            {
                // Name exactly which input drifted when we can (single selection with bake data), otherwise generic.
                string why = targets.Length == 1 && target is BrazeVoronoiFracture single
                    ? single.DescribeBakeMismatch() : null;
                EditorGUILayout.HelpBox(why ?? "Not baked, or a fracture input changed since the last bake. Press Bake " +
                                        "to generate the fragments — nothing fractures until you do.", MessageType.Warning);
            }
            else if (allDraftCurrent && !allValid)
            {
                EditorGUILayout.HelpBox("The exact transient draft is current. Commit it to update the runtime bake asset.",
                                        MessageType.Info);
            }

            // bake stats readout (live after a bake; cached counts from the asset otherwise)
            var vf = target as BrazeVoronoiFracture;
            if (vf != null)
            {
                if (!string.IsNullOrEmpty(vf.bakeReport))
                    BrazeEditorGUI.SubLabel(vf.bakeReport);
                else if (vf.bake != null && vf.bake.HasData)
                {
                    int authoredCells = vf.bake.inputsRecorded ? vf.bake.inputs.pointCount : vf.bake.seedCount;
                    int topologyChunks = vf.TopologyChunkCount;
                    string source = topologyChunks != vf.bake.fragmentCount ? "legacy bake repaired in memory" : "cached";
                    BrazeEditorGUI.SubLabel($"{authoredCells:n0} authored cells · {topologyChunks:n0} topology chunks · " +
                                            $"{vf.bake.VertexCount:n0} verts ({source})");
                }
            }
        }

        // ── Force drop zone (matches Braze emitter / disintegrator) ──────────────────
        static GUIStyle dropStyle, forceRowStyle;

        static void EnsureForceStyles()
        {
            if (dropStyle != null) return;
            dropStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            dropStyle.alignment = TextAnchor.MiddleCenter;
            dropStyle.fontSize = 11;
            dropStyle.fontStyle = FontStyle.Bold;
            dropStyle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
            forceRowStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            forceRowStyle.fontSize = 11;
            forceRowStyle.alignment = TextAnchor.MiddleLeft;
            forceRowStyle.normal.textColor = new Color(0.86f, 0.88f, 0.92f);
        }

        static void RoundRect(Rect r, Color c, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
        }

        static BrazeParticleForce ForceFrom(UnityEngine.Object o)
        {
            var f = o as BrazeParticleForce;
            if (f == null && o is GameObject go) f = go.GetComponent<BrazeParticleForce>();
            return f;
        }

        static UnityEngine.Object ResolveEffector(UnityEngine.Object o)
        {
            if (o is BrazeFractureEffector effector) return effector;
            if (o is GameObject go) return go.GetComponent<BrazeFractureEffector>();
            return null;
        }

        static void AddForceUnique(SerializedProperty arr, BrazeParticleForce f)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == f) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = f;
        }

        static BrazeParticleSystem EmitterFrom(UnityEngine.Object o)
        {
            var e = o as BrazeParticleSystem;
            if (e == null && o is GameObject go) e = go.GetComponent<BrazeParticleSystem>();
            return e;
        }

        static void EnsureEmitterPercentages(SerializedProperty arr, SerializedProperty percentages)
        {
            if (percentages == null) return;
            while (percentages.arraySize < arr.arraySize)
            {
                int index = percentages.arraySize;
                percentages.arraySize++;
                percentages.GetArrayElementAtIndex(index).floatValue = 100f;
            }
            if (percentages.arraySize > arr.arraySize) percentages.arraySize = arr.arraySize;
        }

        static void AddEmitterUnique(SerializedProperty arr, SerializedProperty percentages,
            SerializedProperty owners, BrazeParticleSystem e)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == e) return;
            int index = arr.arraySize;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(index).objectReferenceValue = e;
            if (percentages != null)
            {
                percentages.arraySize = arr.arraySize;
                percentages.GetArrayElementAtIndex(index).floatValue = 100f;
            }
            if (owners != null)
            {
                owners.arraySize = arr.arraySize;
                owners.GetArrayElementAtIndex(index).objectReferenceValue = e;
            }
        }

        static bool HasEmitter(SerializedProperty arr)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue != null) return true;
            return false;
        }

        void DrawForcesList(SerializedProperty arr)
        {
            EnsureForceStyles();
            if (!BrazeEditorGUI.CanEditObjectList(arr)) return;

            for (int i = 0; i < arr.arraySize; i++)
            {
                var f = arr.GetArrayElementAtIndex(i).objectReferenceValue as BrazeParticleForce;
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);

                var nameR = new Rect(row.x + 10f, row.y, row.width - 36f, row.height);
                GUI.Label(nameR, BrazeEditorGUI.HierarchyName(f), forceRowStyle);
                if (f != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.PingObject(f);
                    Event.current.Use();
                }

                if (GUI.Button(new Rect(row.xMax - 24f, row.y + 2f, 20f, 18f), "✕"))
                {
                    arr.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    arr.DeleteArrayElementAtIndex(i);
                    break;
                }
            }

            GUILayout.Space(2);
            var e = Event.current;
            var drop = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            bool hovering = drop.Contains(e.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            RoundRect(drop, hovering ? new Color(1f, 0.45f, 0.14f, 0.16f) : new Color(1f, 1f, 1f, 0.035f), new Vector4(6, 6, 6, 6));
            RoundRect(new Rect(drop.x, drop.y, drop.width, 1.5f), new Color(1f, 0.45f, 0.14f, hovering ? 0.85f : 0.3f), new Vector4(6, 6, 0, 0));
            GUI.Label(drop, arr.arraySize == 0 ? "＋   Drop a force here" : "＋   Drop another force", dropStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences)
                    if (ForceFrom(o) != null) { any = true; break; }

                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        var f = ForceFrom(o);
                        if (f != null) AddForceUnique(arr, f);
                    }
                }
                e.Use();
            }
        }

        void DrawEmitterList(SerializedProperty arr, SerializedProperty percentages, SerializedProperty owners)
        {
            EnsureForceStyles();
            if (!BrazeEditorGUI.CanEditObjectList(arr)) return;
            EnsureEmitterPercentages(arr, percentages);
            const float sliderWidth = 155f;

            if (arr.arraySize > 0)
            {
                var header = EditorGUILayout.GetControlRect(false, 16f);
                float controlsX = header.xMax - 24f - 16f - sliderWidth - 12f;
                GUI.Label(new Rect(header.x + 10f, header.y, Mathf.Max(24f, controlsX - header.x - 16f), header.height),
                    "PARTICLE SYSTEM", EditorStyles.miniBoldLabel);
                GUI.Label(new Rect(controlsX + 10f, header.y, header.xMax - controlsX - 38f, header.height),
                    "SPAWN %", EditorStyles.miniBoldLabel);
            }

            for (int i = 0; i < arr.arraySize; i++)
            {
                var emitter = arr.GetArrayElementAtIndex(i).objectReferenceValue as BrazeParticleSystem;
                var percentage = percentages?.GetArrayElementAtIndex(i);
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);

                var removeR = new Rect(row.xMax - 24f, row.y + 2f, 20f, 18f);
                var percentMarkR = new Rect(removeR.x - 16f, row.y, 14f, row.height);
                var sliderR = new Rect(percentMarkR.x - sliderWidth, row.y + 1f, sliderWidth, row.height - 2f);
                var controlsR = new Rect(sliderR.x - 12f, row.y, row.xMax - sliderR.x - 16f, row.height);
                RoundRect(controlsR, new Color(0f, 0f, 0f, 0.24f), new Vector4(0, 4, 4, 0));
                var dividerR = new Rect(controlsR.x, row.y + 1f, 2f, row.height - 2f);
                var nameR = new Rect(row.x + 10f, row.y, Mathf.Max(24f, dividerR.x - row.x - 8f), row.height);
                GUI.Label(nameR, BrazeEditorGUI.HierarchyName(emitter), forceRowStyle);
                EditorGUI.DrawRect(dividerR, new Color(1f, 0.45f, 0.14f, 0.72f));
                if (emitter != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.PingObject(emitter);
                    Event.current.Use();
                }

                if (percentage != null)
                {
                    var sliderLabel = new GUIContent(string.Empty,
                        "Percentage of Burst / Fragment and Rate / Fragment sent to this particle system.");
                    percentage.floatValue = Mathf.Round(BrazeEditorGUI.Slider(
                        sliderR, sliderLabel, percentage.floatValue, 0f, 100f));
                    GUI.Label(percentMarkR, "%", EditorStyles.miniLabel);
                }

                if (GUI.Button(removeR, "✕"))
                {
                    arr.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    arr.DeleteArrayElementAtIndex(i);
                    if (percentages != null && i < percentages.arraySize)
                        percentages.DeleteArrayElementAtIndex(i);
                    if (owners != null && i < owners.arraySize)
                    {
                        owners.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        owners.DeleteArrayElementAtIndex(i);
                    }
                    break;
                }
            }

            GUILayout.Space(2);
            var e = Event.current;
            var drop = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            bool hovering = drop.Contains(e.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            RoundRect(drop, hovering ? new Color(1f, 0.45f, 0.14f, 0.16f) : new Color(1f, 1f, 1f, 0.035f), new Vector4(6, 6, 6, 6));
            RoundRect(new Rect(drop.x, drop.y, drop.width, 1.5f), new Color(1f, 0.45f, 0.14f, hovering ? 0.85f : 0.3f), new Vector4(6, 6, 0, 0));
            GUI.Label(drop, arr.arraySize == 0 ? "＋   Drop an emitter here" : "＋   Drop another emitter", dropStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences)
                    if (EmitterFrom(o) != null) { any = true; break; }

                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        var emitter = EmitterFrom(o);
                        if (emitter != null) AddEmitterUnique(arr, percentages, owners, emitter);
                    }
                }
                e.Use();
            }
        }
    }

    [CustomEditor(typeof(BrazeFractureImpactSource))]
    [CanEditMultipleObjects]
    public sealed class BrazeFractureImpactSourceEditor : Editor
    {
        const string K = "BrazeFX.FractureImpact.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("FRACTURE IMPACT", "radial");
            SerializedProperty P(string name) => serializedObject.FindProperty(name);

            if (BrazeEditorGUI.Section(K + "volume", "IMPACT VOLUME"))
            {
                BrazeEditorGUI.PropertyField(P("radius"));
                BrazeEditorGUI.PropertyField(P("penetrationDepth"), new GUIContent("Penetration"));
                BrazeEditorGUI.Hint("The bullet gizmo's width represents Radius and its tip represents Penetration. Penetration 0 displays a spherical impact.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "response", "RESPONSE"))
            {
                BrazeEditorGUI.PropertyField(P("impulse"));
                BrazeEditorGUI.PropertyField(P("spin"));
                BrazeEditorGUI.Slider(P("radialBlend"), 0f, 1f, new GUIContent("Radial Blend"));
                BrazeEditorGUI.PropertyField(P("upwardBias"), new GUIContent("Upward Bias"));
                BrazeEditorGUI.Slider(P("velocityInheritance"), 0f, 2f, new GUIContent("Inherit Velocity"));
                BrazeEditorGUI.PropertyField(P("falloff"));
                BrazeEditorGUI.ToggleButton("Affect Released", P("affectReleased"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "fluid", "CONTAINER DEPOSIT"))
            {
                BrazeEditorGUI.ToggleButton("Deposit Smoke / Fire", P("depositToFluid"));
                if (P("depositToFluid").boolValue)
                {
                    // Container is deliberately not drawn: this component's home is a projectile PREFAB, which cannot
                    // hold a scene reference, and an unset container already means "whichever container is listening".
                    // Scripts can still pin one via the public field for the rare multi-container case.
                    BrazeEditorGUI.Slider(P("fluidDuration"), 0.05f, 10f, new GUIContent("Duration"));
                    BrazeEditorGUI.Slider(P("fluidCellPercentage"), 1f, 100f, new GUIContent("Depositing Cells"));
                    BrazeEditorGUI.SubHeader("RATE / FRAGMENT");
                    BrazeEditorGUI.PropertyField(P("fluidSmoke"), new GUIContent("Smoke / Sec"));
                    BrazeEditorGUI.PropertyField(P("fluidHeat"), new GUIContent("Heat / Sec"));
                    BrazeEditorGUI.PropertyField(P("fluidFuel"), new GUIContent("Fuel / Sec"));
                    BrazeEditorGUI.SubHeader("IMPACT PLUME");
                    BrazeEditorGUI.Slider(P("fluidBurstSpeed"), 0f, 30f, new GUIContent("Ejecta Speed"));
                    BrazeEditorGUI.Slider(P("fluidBlastDirection"), -1f, 1f, new GUIContent("Blast Direction"));
                    BrazeEditorGUI.Slider(P("fluidRadialSpread"), 0f, 1f, new GUIContent("Radial Spread"));
                    BrazeEditorGUI.Slider(P("fluidTurbulence"), 0f, 5f, new GUIContent("Breakup"));
                    BrazeEditorGUI.Slider(P("fluidSurfaceBias"), 0f, 1f, new GUIContent("Surface Bias"));
                    BrazeEditorGUI.SubHeader("GAS MOTION");
                    BrazeEditorGUI.Slider(P("fluidVelocityBlend"), 0f, 1f, new GUIContent("Fragment Velocity"));
                    BrazeEditorGUI.Slider(P("fluidBuoyancy"), -300f, 300f, new GUIContent("Buoyancy"));
                    BrazeEditorGUI.Hint("Depositing Cells keeps a stable GPU-selected percentage of the impacted fragments. Ejecta Speed launches the initial plume away from the hit; Blast Direction aims its axial half — negative blows back along the incoming projectile, positive drives it onward through the surface, zero leaves only the spread; Radial Spread blends that axis toward the struck fragment surface; Breakup prevents rigid trails; Surface Bias moves emission off the centroid. Fire bursts first and smoke fades as a softer tail. No fragment positions are read back to the CPU.");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "behavior", "BEHAVIOUR"))
            {
                BrazeEditorGUI.PillsEnum("Activation", P("activation"));
                BrazeEditorGUI.ToggleButton("Trigger Once", P("triggerOnce"));
                var source = target as BrazeFractureImpactSource;
                bool canFire = Application.isPlaying && targets.Length == 1 && source != null;
                using (new EditorGUI.DisabledScope(!canFire))
                {
                    if (GUILayout.Button("Fire Now")) source.FireFromTransform();
                }
                BrazeEditorGUI.Hint("Transform Sweep is prefab-ready: it automatically checks active Voronoi fractures without a scene reference, Rigidbody, Collider, raycast, or allocation. Manual is for Fire Now, hitscan weapons, and custom projectile controllers.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
