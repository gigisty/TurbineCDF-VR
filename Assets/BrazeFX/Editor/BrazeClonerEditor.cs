using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeCloner))]
    [CanEditMultipleObjects]
    public sealed class BrazeClonerEditor : Editor
    {
        const string K = "BrazeFX.Cloner.";
        const float CompactGap = 6f;
        static readonly Color CompactDivider = new(1f, 1f, 1f, 0.055f);
        static GUIStyle compactToggleStyle;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("GPU CLONER", "cloner");
            SerializedProperty P(string name) => serializedObject.FindProperty(name);

            if (BrazeEditorGUI.Section(K + "sources", "SOURCES"))
            {
                DrawSourceDropList(P("sources"));
                BrazeEditorGUI.PillsEnum("Order", P("sourceOrder"));
                BrazeEditorGUI.PropertyField(P("seed"));
                BrazeEditorGUI.Hint("Drop prefab or scene-object roots here. MeshRenderer hierarchies, submeshes, and up to four levels from the first LODGroup are converted into GPU indirect batches. Lit properties and transparent blend state are preserved; transparent clones share one indirect batch and are not depth-sorted per clone. A source containing another Braze Cloner clones the complete composition: the nested cloner keeps evaluating once, its effectors stay live, and every copy shows the same animation. Nested cloners must be active scene objects; their source templates inside the composition are not drawn twice.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "distribution", "DISTRIBUTION"))
            {
                BrazeEditorGUI.PillsEnum("Mode", P("distribution"));
                var mode = (BrazeCloner.Distribution)P("distribution").enumValueIndex;
                if (mode == BrazeCloner.Distribution.Grid)
                {
                    BrazeEditorGUI.PropertyField(P("gridCount"), new GUIContent("Count XYZ"));
                    BrazeEditorGUI.PropertyField(P("gridSpacing"), new GUIContent("Spacing"));
                    BrazeEditorGUI.ToggleButton("Centered", P("center"));
                }
                else if (mode == BrazeCloner.Distribution.Linear)
                {
                    BrazeEditorGUI.PropertyField(P("count"));
                    BrazeEditorGUI.PropertyField(P("linearOffset"), new GUIContent("Step Position"));
                    BrazeEditorGUI.PropertyField(P("linearRotation"), new GUIContent("Step Rotation"));
                    BrazeEditorGUI.PropertyField(P("linearScale"), new GUIContent("End Scale"));
                }
                else if (mode == BrazeCloner.Distribution.Spline)
                {
                    BrazeEditorGUI.PropertyField(P("splineInput"), new GUIContent("Spline"));
                    BrazeEditorGUI.PropertyField(P("count"), new GUIContent("Clone Count"));
                    FullField(P("splineRange"), "Range");
                    BrazeEditorGUI.PropertyField(P("splineOffset"), new GUIContent("Offset"));
                    if (targets.Length == 1 && target is BrazeCloner splineCloner && splineCloner.splineInput == null)
                        EditorGUILayout.HelpBox("Assign a Braze Spline as the distribution path.", MessageType.Info);
                    BrazeEditorGUI.Hint("Clones are spaced evenly by arc length between Range start and end. Offset " +
                        "slides every clone along the path and wraps on closed splines — animate it to flow clones " +
                        "around the loop at zero CPU cost. Default alignment follows the path tangent; the spline's " +
                        "Transform, Points Distance, and Wobble are followed live.");
                }
                else if (mode == BrazeCloner.Distribution.Mesh)
                {
                    BrazeEditorGUI.PropertyField(P("meshInput"), new GUIContent("Mesh Input"));
                    BrazeEditorGUI.PropertyField(P("count"), new GUIContent("Clone Count"));
                    BrazeEditorGUI.PillsEnum("Place On", P("meshSampling"));
                    if (targets.Length == 1 && target is BrazeCloner meshCloner)
                    {
                        if (meshCloner.meshInput == null || meshCloner.DistributionMesh == null)
                            EditorGUILayout.HelpBox("Assign a MeshRenderer or SkinnedMeshRenderer as the distribution input.", MessageType.Info);
                        else if (!meshCloner.DistributionMesh.isReadable)
                            EditorGUILayout.HelpBox("Enable Read/Write on the input mesh's import settings so its distribution points can be sampled.", MessageType.Warning);
                        else if (meshCloner.meshInput is SkinnedMeshRenderer)
                            BrazeEditorGUI.Hint("Skinned inputs use their shared bind-pose mesh and follow the renderer Transform; bone deformation is not sampled.");
                    }
                    BrazeEditorGUI.Hint("Surface distributes clones evenly by triangle area. Vertices walks the input topology. The Seed keeps the result deterministic.");
                }
                else
                {
                    BrazeEditorGUI.SubHeader("RING TOPOLOGY");
                    CompactPair(P("count"), "Count Per Ring", P("radialRings"), "Rings");
                    CompactSingle(P("radialCountStep"), "Count Growth");

                    BrazeEditorGUI.SubHeader("POLAR SPACE");
                    CompactPair(P("radialRadius"), "Radius", P("radialRadiusStep"), "Radius Step");
                    FullField(P("radialArc"), "Arc Degrees");
                    CompactPair(P("radialRingTwist"), "Twist / Ring", P("radialAxisStep"), "Axis Step");
                    BrazeEditorGUI.PillsEnum("Plane", P("radialPlane"));
                    CompactSingle(P("radialCenterAxis"), "Center Axis");

                    BrazeEditorGUI.SubHeader("SPIRAL");
                    CompactPair(P("radialSpiralRadius"), "Radial Growth", P("radialSpiralAxis"), "Axis Rise");

                    BrazeEditorGUI.SubHeader("WAVE");
                    CompactPair(P("radialWaveCycles"), "Cycles", P("radialWavePhase"), "Phase");
                    CompactPair(P("radialWaveRadius"), "Radial Amplitude", P("radialWaveAxis"), "Axis Amplitude");
                    CompactPair(P("radialWaveAxialPhase"), "Axial Offset", P("radialWaveRingPhase"), "Phase / Ring");

                    BrazeEditorGUI.SubHeader("LOCAL ROTATION");
                    FullField(P("radialRotationOffset"), "Rotation Offset");
                    FullField(P("radialRotationStep"), "Rotation / Ring");

                    BrazeEditorGUI.SubHeader("SCALE");
                    CompactPair(P("radialScaleStep"), "Scale / Ring", P("radialScaleWave"), "Scale Wave");
                    BrazeEditorGUI.Hint("Layered polar generator: radius and axis steps build rings; twist, spiral, and phase-shifted waves can form ripples, helices, flowers, tunnels, and shells.");
                }

                if (targets.Length == 1 && target is BrazeCloner cloner)
                    BrazeEditorGUI.SubLabel($"{cloner.InstanceCount:n0} INSTANCES");
                BrazeEditorGUI.Hint("Position, rotation, and scale are generated directly into structured GPU buffers. Counts are not expanded into a Transform hierarchy.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "transform", "TRANSFORM"))
            {
                BrazeEditorGUI.SubHeader("ALIGNMENT");
                BrazeEditorGUI.PillsEnum("Mode", P("alignment"));
                if (P("alignment").enumValueIndex != (int)BrazeCloner.CloneAlignment.None)
                {
                    BrazeEditorGUI.SubHeader("AIM AXIS");
                    BrazeEditorGUI.SignedAxis(P("alignmentAxis"));
                    BrazeEditorGUI.PillsEnum("Up Reference", P("alignmentUp"));
                }

                BrazeEditorGUI.SubHeader("SCALE");
                BrazeEditorGUI.Slider(P("uniformScale"), -1f, 1f, new GUIContent("Uniform Scale"));
                BrazeEditorGUI.Hint("Default preserves source orientation in Grid and Linear, follows the generated path in Radial, and follows surface normals in Mesh mode. Path follows the layout direction; Outward and Inward use the distribution direction. Negative scale mirrors every clone.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "effectors", "EFFECTORS"))
            {
                BrazeEditorGUI.ObjectDropList(P("effectors"),
                    "＋   Drop an effector here", "＋   Drop another effector",
                    ResolveEffector, reorderable: true);
                BrazeEditorGUI.Hint("Ordered top to bottom. Random, Step, Target, Spline, Shader, and topology-aware Push Apart use the same GPU data contract and transform semantics as Voronoi Fracture.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "appearance", "APPEARANCE"))
            {
                BrazeEditorGUI.ToggleButton("Enable", P("appearanceEnabled"));
                if (P("appearanceEnabled").boolValue)
                {
                    BrazeEditorGUI.SubHeader("UNAFFECTED");
                    CompactPair(P("unaffectedColor"), "Colour", P("unaffectedGlow"), "Glow");

                    BrazeEditorGUI.SubHeader("AFFECTED");
                    BrazeEditorGUI.PillsEnum("Colour Mode", P("affectedColorMode"));
                    var colorMode = (BrazeCloner.AffectedColorMode)P("affectedColorMode").enumValueIndex;
                    if (colorMode == BrazeCloner.AffectedColorMode.Solid)
                    {
                        CompactPair(P("affectedColor"), "Colour", P("affectedGlow"), "Glow");
                    }
                    else
                    {
                        BrazeEditorGUI.PropertyField(P("affectedGradient"), new GUIContent("Gradient"));
                        BrazeEditorGUI.PropertyField(P("affectedGlow"), new GUIContent("Glow"));
                        if (colorMode == BrazeCloner.AffectedColorMode.Speed)
                        {
                            FullField(P("affectedSpeedRange"), "Speed Range");
                            BrazeEditorGUI.Slider(P("affectedSpeedSmoothing"), 0f, 1f,
                                new GUIContent("Smoothing", P("affectedSpeedSmoothing").tooltip));
                        }
                        CompactPair(P("affectedGradientScale"), "Ramp Scale",
                            P("affectedGradientOffset"), "Ramp Offset");
                    }

                    BrazeEditorGUI.SubHeader("REGION");
                    CompactPair(P("affectedThreshold"), "Threshold", P("affectedSoftness"), "Softness");
                    BrazeEditorGUI.ToggleButton("Invert Region", P("invertAffected"));

                    BrazeEditorGUI.Hint("Unaffected and affected colours remain independent; Region Softness is the " +
                        "only transition between them. Each Glow emits exclusively from its own region colour. " +
                        "Gradient spans the full clone order; Speed measures final motion after the complete effector stack.");
                }
                else
                {
                    BrazeEditorGUI.Hint("Enable to style unaffected and effector-driven clone regions independently.");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "rendering", "RENDERING"))
            {
                BrazeEditorGUI.Slider(P("lodBias"), 0.1f, 4f, new GUIContent("LOD Bias"));
                BrazeEditorGUI.PropertyField(P("maxDrawDistance"));
                BrazeEditorGUI.PropertyField(P("boundsPadding"));
                BrazeEditorGUI.PropertyField(P("shadowCasting"));
                BrazeEditorGUI.ToggleButton("Receive Shadows", P("receiveShadows"));
                BrazeEditorGUI.ToggleButton("Scene View", P("renderInSceneView"));
                BrazeEditorGUI.ToggleButton("Render Clones", P("renderClones"));
                BrazeEditorGUI.Hint("Every camera runs one GPU frustum/LOD bucketing pass. Only visible clone IDs are submitted; indirect instance counts never return to the CPU. Turn Render Clones off to hide this cloner's own output while another Braze Cloner clones it as part of a composition.");
            }
            BrazeEditorGUI.EndSection();

            if (targets.Length == 1 && target is BrazeCloner active)
            {
                if (BrazeEditorGUI.Section(K + "status", "STATUS"))
                {
                    EditorGUILayout.HelpBox(active.Status, active.ActiveSourceCount > 0 ? MessageType.Info : MessageType.Warning);
                    if (!SystemInfo.supportsComputeShaders)
                        EditorGUILayout.HelpBox("This platform does not report compute-shader support.", MessageType.Error);
                }
                BrazeEditorGUI.EndSection();
            }

            serializedObject.ApplyModifiedProperties();
        }

        static void CompactPair(SerializedProperty first, string firstLabel, SerializedProperty second, string secondLabel)
        {
            EnsureCompactStyles();
            float firstHeight = BrazeEditorGUI.PropertyHeight(first, GUIContent.none, true);
            float secondHeight = BrazeEditorGUI.PropertyHeight(second, GUIContent.none, true);
            float fieldHeight = Mathf.Max(firstHeight, secondHeight);
            GUILayout.Space(3f);
            Rect row = EditorGUILayout.GetControlRect(false, fieldHeight);
            float width = (row.width - CompactGap) * 0.5f;
            DrawInlineField(new Rect(row.x, row.y, width, firstHeight), first, firstLabel);
            EditorGUI.DrawRect(new Rect(row.x + width + CompactGap * 0.5f, row.y, 1f, fieldHeight), CompactDivider);
            DrawInlineField(new Rect(row.x + width + CompactGap, row.y, width, secondHeight), second, secondLabel);
            GUILayout.Space(3f);
        }

        static void CompactSingle(SerializedProperty property, string label)
        {
            EnsureCompactStyles();
            float fieldHeight = BrazeEditorGUI.PropertyHeight(property, GUIContent.none, true);
            GUILayout.Space(3f);
            Rect row = EditorGUILayout.GetControlRect(false, fieldHeight);
            DrawInlineField(new Rect(row.x, row.y, (row.width - CompactGap) * 0.5f, fieldHeight), property, label);
            GUILayout.Space(3f);
        }

        static void FullField(SerializedProperty property, string label)
        {
            GUILayout.Space(3f);
            BrazeEditorGUI.PropertyField(property, new GUIContent(label, property.tooltip), true);
            GUILayout.Space(3f);
        }

        static void DrawInlineField(Rect fieldRect, SerializedProperty property, string label)
        {
            var content = new GUIContent(label, property.tooltip);
            if (property.propertyType != SerializedPropertyType.Boolean)
            {
                float previousLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = Mathf.Clamp(fieldRect.width * 0.44f, 72f, 108f);
                BrazeEditorGUI.PropertyField(fieldRect, property, content, true);
                EditorGUIUtility.labelWidth = previousLabelWidth;
                return;
            }

            EditorGUI.BeginProperty(fieldRect, GUIContent.none, property);
            float labelWidth = Mathf.Clamp(fieldRect.width * 0.44f, 72f, 108f);
            EditorGUI.LabelField(new Rect(fieldRect.x, fieldRect.y, labelWidth - 4f, fieldRect.height), content);
            var toggleRect = new Rect(fieldRect.x + labelWidth, fieldRect.y, fieldRect.width - labelWidth, fieldRect.height);
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            bool current = property.boolValue;
            Color previousBackground = GUI.backgroundColor;
            if (current && !property.hasMultipleDifferentValues) GUI.backgroundColor = new Color(1f, 0.45f, 0.14f);
            EditorGUI.BeginChangeCheck();
            bool next = GUI.Toggle(toggleRect, current, property.hasMultipleDifferentValues ? "—" : current ? "On" : "Off", compactToggleStyle);
            if (EditorGUI.EndChangeCheck()) property.boolValue = next;
            GUI.backgroundColor = previousBackground;
            EditorGUI.showMixedValue = previousMixed;
            EditorGUI.EndProperty();
        }

        static void EnsureCompactStyles()
        {
            compactToggleStyle ??= BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniButton);
            compactToggleStyle.fontSize = 10;
            compactToggleStyle.alignment = TextAnchor.MiddleCenter;
        }

        static Object ResolveEffector(Object value)
        {
            if (value is BrazeFractureEffector effector) return effector;
            if (value is GameObject gameObject) return gameObject.GetComponent<BrazeFractureEffector>();
            return null;
        }

        static GUIStyle sourceNameStyle, sourceDropStyle;

        static void EnsureSourceStyles()
        {
            sourceNameStyle ??= BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            sourceDropStyle ??= BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            sourceNameStyle.fontSize = 11;
            sourceNameStyle.fontStyle = FontStyle.Bold;
            sourceNameStyle.alignment = TextAnchor.MiddleLeft;
            sourceNameStyle.normal.textColor = new Color(0.86f, 0.88f, 0.92f);
            sourceDropStyle.fontSize = 11;
            sourceDropStyle.fontStyle = FontStyle.Bold;
            sourceDropStyle.alignment = TextAnchor.MiddleCenter;
            sourceDropStyle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
        }

        static void DrawSourceDropList(SerializedProperty sources)
        {
            EnsureSourceStyles();
            if (!BrazeEditorGUI.CanEditObjectList(sources)) return;
            BrazeEditorGUI.BeginReorderableRows();

            for (int i = 0; i < sources.arraySize; i++)
            {
                SerializedProperty source = sources.GetArrayElementAtIndex(i);
                SerializedProperty gameObject = source.FindPropertyRelative("gameObject");
                SerializedProperty enabled = source.FindPropertyRelative("enabled");
                var row = EditorGUILayout.GetControlRect(false, 24f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);

                var toggleRect = new Rect(row.x + 8f, row.y + 3f, 18f, 18f);
                enabled.boolValue = EditorGUI.Toggle(toggleRect, enabled.boolValue);
                var handleRect = new Rect(row.x + 29f, row.y + 1f, 14f, 22f);
                var nameRect = new Rect(row.x + 47f, row.y + 1f, row.width - 75f, 22f);
                Object sourceObject = gameObject.objectReferenceValue;
                GUI.Label(nameRect, BrazeEditorGUI.HierarchyName(sourceObject), sourceNameStyle);
                BrazeEditorGUI.ReorderableRow(sources, i, row, handleRect, nameRect, sourceObject);
                if (GUI.Button(new Rect(row.xMax - 24f, row.y + 2f, 20f, 18f), "✕"))
                {
                    sources.DeleteArrayElementAtIndex(i);
                    break;
                }

                GUILayout.Space(2f);
            }
            BrazeEditorGUI.EndReorderableRows(sources);

            GUILayout.Space(2f);
            Event current = Event.current;
            Rect drop = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            bool hovering = drop.Contains(current.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            RoundRect(drop, hovering ? new Color(1f, 0.45f, 0.14f, 0.16f) : new Color(1f, 1f, 1f, 0.035f),
                new Vector4(6, 6, 6, 6));
            RoundRect(new Rect(drop.x, drop.y, drop.width, 1.5f),
                new Color(1f, 0.45f, 0.14f, hovering ? 0.85f : 0.3f), new Vector4(6, 6, 0, 0));
            GUI.Label(drop, sources.arraySize == 0 ? "＋   Drop a source object here" : "＋   Drop another source object",
                sourceDropStyle);

            if ((current.type == EventType.DragUpdated || current.type == EventType.DragPerform) &&
                drop.Contains(current.mousePosition))
            {
                bool any = false;
                foreach (Object dragged in DragAndDrop.objectReferences)
                    if (SourceObjectFrom(dragged) != null) { any = true; break; }
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (current.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (Object dragged in DragAndDrop.objectReferences)
                    {
                        GameObject resolved = SourceObjectFrom(dragged);
                        if (resolved != null) AddSourceUnique(sources, resolved);
                    }
                }
                current.Use();
            }
        }

        static GameObject SourceObjectFrom(Object value)
        {
            if (value is GameObject gameObject) return gameObject;
            return value is Component component ? component.gameObject : null;
        }

        static void AddSourceUnique(SerializedProperty sources, GameObject gameObject)
        {
            for (int i = 0; i < sources.arraySize; i++)
                if (sources.GetArrayElementAtIndex(i).FindPropertyRelative("gameObject").objectReferenceValue == gameObject)
                    return;

            int index = sources.arraySize;
            sources.arraySize++;
            SerializedProperty source = sources.GetArrayElementAtIndex(index);
            source.FindPropertyRelative("gameObject").objectReferenceValue = gameObject;
            source.FindPropertyRelative("materialOverride").objectReferenceValue = null;
            source.FindPropertyRelative("enabled").boolValue = true;
        }

        static void RoundRect(Rect rect, Color color, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, Vector4.zero, radii);
        }
    }
}
