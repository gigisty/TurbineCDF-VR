using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazePyroEmitter"/> (DCC theme).</summary>
    [CustomEditor(typeof(BrazePyroEmitter))]
    [CanEditMultipleObjects]
    public sealed class BrazePyroEmitterEditor : Editor
    {
        const string K = "BrazeFX.Emitter.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("EMITTER", "emitter");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                var shapeProp = P("shape");
                BrazeEditorGUI.PillsEnum("Shape", shapeProp);
                var shape = (BrazePyroEmitter.Shape)shapeProp.enumValueIndex;
                if (shape == BrazePyroEmitter.Shape.Mesh)
                {
                    BrazeEditorGUI.PropertyField(P("sourceMesh"), new GUIContent("Source Mesh"));
                    BrazeEditorGUI.PropertyField(P("sourceSkinnedMesh"), new GUIContent("Source Skinned Mesh"));
                    var smr = P("sourceSkinnedMesh").objectReferenceValue as SkinnedMeshRenderer;   // takes priority
                    var mf = P("sourceMesh").objectReferenceValue as MeshFilter;
                    if (smr == null)
                        BrazeEditorGUI.Slider(P("meshSpacing"), 0.01f, 1f,
                            new GUIContent("Intact Sample Spacing", "World spacing for the intact static-mesh surface. Flying fragments and skinned live vertices do not use this value."));
                    BrazeEditorGUI.Slider(P("meshNormalSpeed"), 0f, 20f, new GUIContent("Normal Speed"));

                    Component srcComp = smr != null ? smr : (Component)mf;
                    var sm = smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
                    if (srcComp == null)
                        EditorGUILayout.HelpBox("Assign a Source Mesh (MeshFilter) for a static prop, or a Source Skinned Mesh for a deforming character.", MessageType.Info);
                    else if (sm == null)
                        EditorGUILayout.HelpBox("The assigned object has no mesh.", MessageType.Warning);
                    else if (!sm.isReadable)
                        EditorGUILayout.HelpBox($"'{sm.name}' is not Read/Write enabled — enable it in the mesh's import settings, or it can't be sampled.", MessageType.Warning);
                    else if (smr != null)
                        EditorGUILayout.HelpBox($"Skinned source: '{sm.name}' is read off the GPU each frame (no CPU skinning); fire/smoke tracks the deforming body.", MessageType.None);

                    // Auto-detect a Voronoi Fracture on the same object → expose fragment-emission controls inline.
                    var frac = mf != null && smr == null ? mf.GetComponent<BrazeVoronoiFracture>() : null;
                    if (frac != null)
                    {
                        GUILayout.Space(4);
                        EditorGUILayout.HelpBox("Voronoi Fracture detected — this mesh can emit from its flying fragments when it shatters.", MessageType.None);
                        BrazeEditorGUI.ToggleButton("Emit From Fragments", P("emitFromFragments"));
                        using (new EditorGUI.DisabledScope(!P("emitFromFragments").boolValue))
                        {
                            BrazeEditorGUI.Slider(P("fragmentEmissionPercent"), 0f, 100f,
                                new GUIContent("Emitting Fragments (%)", "Persistent percentage of fragments selected to emit fire/smoke. Lower values dispatch fewer GPU threads and use a smaller sparse hash."));
                            BrazeEditorGUI.Slider(P("fragmentVelocity"), 0f, 1f, new GUIContent("Fragment Velocity"));
                            if (!serializedObject.isEditingMultipleObjects)
                            {
                                int selected = P("fragmentEmissionPercent").floatValue <= 0f ? 0
                                    : Mathf.Clamp(Mathf.CeilToInt(frac.SeedCount * P("fragmentEmissionPercent").floatValue * 0.01f), 1, frac.SeedCount);
                                BrazeEditorGUI.Hint($"{selected:N0} of {frac.SeedCount:N0} fragments are selected as persistent emitters.");
                            }
                        }
                    }

                    BrazeEditorGUI.Hint(frac != null
                        ? "While intact, Sample Spacing and Normal Speed drive surface emission. After release, a deterministic percentage of flying fragments emits; Fragment Velocity controls motion inheritance. Fragment emission fades with piece dissolve and does not use normals or the Surface Mask."
                        : (smr != null
                            ? "Skinned emission uses every live GPU vertex; Sample Spacing is intentionally unavailable. Normal Speed pushes fume along the animated surface normals."
                            : "Emits from the intact static-mesh surface. Sample Spacing controls coverage density; Normal Speed pushes fume along surface normals. Transform motion is inherited."));

                    // MASK — explicit slot wins; empty slot falls back to a mask on the source mesh object.
                    var autoMask = srcComp != null ? srcComp.GetComponent<BrazeSurfaceMask>() : null;
                    var explicitMask = P("mask").objectReferenceValue as BrazeSurfaceMask;
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.SubHeader("SURFACE MASK");
                    BrazeEditorGUI.ToggleButton("Use Surface Mask", P("useMask"));
                    using (new EditorGUI.DisabledScope(!P("useMask").boolValue))
                    {
                        BrazeEditorGUI.PropertyField(P("mask"), new GUIContent("Mask", "Optional explicit Braze Surface Mask. Leave empty to auto-use a mask on the Source Mesh object."));
                        if (explicitMask == null)
                        {
                            using (new EditorGUI.DisabledScope(true))
                                EditorGUILayout.ObjectField(new GUIContent("Auto Mask"), autoMask, typeof(BrazeSurfaceMask), true);
                        }
                        if (explicitMask == null && autoMask == null)
                            EditorGUILayout.HelpBox("Assign a Braze Surface Mask here, or add one to the Source Mesh object for auto-detection.", MessageType.Info);
                        else
                            BrazeEditorGUI.Hint("Emits only from the masked region. The mask's Projection, Surface Spacing and Max Points define the surface sample set, independent of mesh vertex density. Each point's channels scale by the mask weight, so fire matches the preview and inherits growth + filters. (Flying fragments emit unmasked.)");
                    }
                }
                else
                {
                    if (shape != BrazePyroEmitter.Shape.Box)
                        BrazeEditorGUI.PropertyField(P("radius"));
                    if (shape is BrazePyroEmitter.Shape.Cylinder or BrazePyroEmitter.Shape.Cone or BrazePyroEmitter.Shape.Capsule)
                        BrazeEditorGUI.PropertyField(P("height"));
                    if (shape == BrazePyroEmitter.Shape.Torus)
                        BrazeEditorGUI.PropertyField(P("tubeRadius"), new UnityEngine.GUIContent("Tube Radius"));
                    if (shape is BrazePyroEmitter.Shape.Cylinder or BrazePyroEmitter.Shape.Cone or BrazePyroEmitter.Shape.Capsule or BrazePyroEmitter.Shape.Torus)
                        BrazeEditorGUI.PillsEnum("Axis", P("shapeAxis"));
                    BrazeEditorGUI.Hint("Sphere, Cylinder, Cone, Capsule and Torus use explicit dimensions. Box uses the GameObject Transform scale. " +
                                        "Axis orients the long shapes (and the torus ring); Push/Jet still fire along the forward arrow.");
                }

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(shape == BrazePyroEmitter.Shape.Mesh ? "Show Mesh Gizmo" : "Show Gizmo", P("showGizmo"));
                if (shape == BrazePyroEmitter.Shape.Mesh)
                    BrazeEditorGUI.Hint("Controls the persistent orange/yellow source-mesh wireframe in the Scene view. Off hides it even when the emitter is selected.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "channels", "CHANNELS"))
            {
                BrazeEditorGUI.PropertyField(P("density"));
                BrazeEditorGUI.PropertyField(P("temperature"));
                BrazeEditorGUI.PropertyField(P("fuel"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "output", "OUTPUT"))
            {
                BrazeEditorGUI.PropertyField(P("strength"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "motion", "MOTION"))
            {
                bool meshShape = (BrazePyroEmitter.Shape)P("shape").enumValueIndex == BrazePyroEmitter.Shape.Mesh;
                // Sub-card 1 — per-emitter lift + how its motion settles. (Global Swirl / Turbulence / Billows
                // live on the Container → Dynamics → Forces, applied across the whole plume.)
                if (BrazeEditorGUI.Section(K + "motion.flow", "LIFT & SETTLE"))
                {
                    BrazeEditorGUI.Slider(P("motionBuoyancy"), -300f, 300f, new UnityEngine.GUIContent("Buoyancy"));
                    BrazeEditorGUI.Slider(P("motionDrag"), 0f, 3f, new UnityEngine.GUIContent("Drag (settle)"));
                    BrazeEditorGUI.Hint("Buoyancy lifts the fume by temperature (follows the heat). Drag is speed-proportional (∝ v²): it tames fast/wild motion (a blast spike) while leaving gentle drift alive — animate it UP to calm a blast without going inert. Swirl / Turbulence / Billows are now global on the Container (Dynamics → Forces).");
                }
                BrazeEditorGUI.EndSection();

                // Sub-card 2 — directed forces + gas expansion radiating from the source.
                if (BrazeEditorGUI.Section(K + "motion.push", "PUSH & EXPANSION"))
                {
                    BrazeEditorGUI.Slider(P("jetSpeed"), 0f, 300f, new UnityEngine.GUIContent("Jet (m/s)"));
                    BrazeEditorGUI.PropertyField(P("sourceVelocity"), new UnityEngine.GUIContent("Source Velocity"));
                    BrazeEditorGUI.Slider(P("sourceVelocityBlend"), 0f, 30f, new UnityEngine.GUIContent("Source Blend"));
                    BrazeEditorGUI.Slider(P("sourceTurbulence"), 0f, 3f, new UnityEngine.GUIContent("Source Turbulence"));
                    BrazeEditorGUI.Slider(P("motionForward"), -600f, 600f, new UnityEngine.GUIContent("Push"));
                    if (!meshShape)
                    {
                        BrazeEditorGUI.Slider(P("motionRadial"), -600f, 600f, new UnityEngine.GUIContent("Radial Force"));
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.Slider(P("gasPressureBurst"), 0f, 60f, new UnityEngine.GUIContent("Pressure Burst"));
                        using (new EditorGUI.DisabledScope(P("gasPressureBurst").floatValue <= 0f))
                        {
                            BrazeEditorGUI.Slider(P("gasSourceSpread"), 0f, 1f, new UnityEngine.GUIContent("Source Spread"));
                            BrazeEditorGUI.Slider(P("gasSourceSharpness"), 0.25f, 4f, new UnityEngine.GUIContent("Source Sharpness"));
                        }
                        BrazeEditorGUI.Hint("Source Velocity and Source Turbulence are Pyro-style birth motion. Jet = forward-axis inflow velocity; Push = a continuous axial force. Radial = outward (+) / inward (−). Pressure Burst expands combusting gas; Spread/Sharpness only shape that burst.");
                    }
                    else
                    {
                        BrazeEditorGUI.Hint("Mesh sources support Jet, Source Velocity/Blend, Source Turbulence and axial Push on both intact surfaces and flying fragments. Radial Force and Pressure Burst require an analytic primitive volume, so they are intentionally hidden for Mesh shape.");
                    }
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "particles", "CONNECTED PARTICLE EMITTERS"))
            {
                bool meshParticleMaster = (BrazePyroEmitter.Shape)P("shape").enumValueIndex == BrazePyroEmitter.Shape.Mesh;
                if (meshParticleMaster)
                {
                    BrazeEditorGUI.SubLabel("MESH SPAWN");
                    BrazeEditorGUI.IntSlider(P("meshParticleSpawnMaxPoints"), 8, 4096,
                        new GUIContent("Intact Surface Point Cap", "Maximum intact mesh-surface candidates used by connected particle emitters. A Surface Mask may reduce this count but cannot exceed it."));
                    BrazeEditorGUI.Separator();
                }
                BrazeEditorGUI.SubLabel("START VELOCITY");
                BrazeEditorGUI.Slider(P("particleMotionScale"), 0f, 0.2f, new GUIContent("Motion Scale"));

                BrazeEditorGUI.SubHeader("FLUID FOLLOW");
                BrazeEditorGUI.Slider(P("particleFluidFollow"), 0f, 1f, new GUIContent("Fluid Follow"));
                using (new EditorGUI.DisabledScope(P("particleFluidFollow").floatValue <= 0f))
                {
                    BrazeEditorGUI.Slider(P("particleFluidFollowRand"), 0f, 1f, new GUIContent("Follow Jitter"));
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.Slider(P("particleFluidFollowGain"), 0f, 8f, new GUIContent("Follow Boost"));
                    BrazeEditorGUI.Slider(P("particleFluidFollowGainRand"), 0f, 1f, new GUIContent("Boost Jitter"));
                }

                BrazeEditorGUI.SubHeader("TARGETS");
                DrawParticleEmittersList(P("connectedParticleEmitters"));
                BrazeEditorGUI.Hint("Connected emitters become SLAVES: they spawn ONLY while this emitter emits " +
                                    "(Burst windows and animated Strength gate them too) and spawn FROM this emitter's " +
                                    "shape — their own Shape and duration window are ignored. They keep their own rate, " +
                                    "lifetime, motion and rendering. Motion Scale = how much of this emitter's Push / " +
                                    "Radial / Buoyancy / Pressure they inherit as START velocity. Fluid Follow = how " +
                                    "strongly the LIVE fluid motion carries them (0 = start nudge only, 1 = track the " +
                                    "fluid); Follow Boost exaggerates it (>1 = small fire, far-flying embers). Jitter " +
                                    "sliders add per-particle random spread so the motion looks natural." +
                                    (meshParticleMaster ? " For a Voronoi Mesh master, connected particles use the intact surface and pause after the fluid handoff to flying fragments; Emitting Fragments (%) controls fluid emission only." : ""));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "burst", "BURST"))
            {
                BrazeEditorGUI.ToggleButton("Burst Mode", P("burstMode"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("startFrame"));
                BrazeEditorGUI.Hairline();
                BrazeEditorGUI.MinMaxRow(new UnityEngine.GUIContent("Burst Frames"),
                    P("burstFramesMin"), P("burstFramesMax"));
                BrazeEditorGUI.Hairline();
                BrazeEditorGUI.ToggleButton("Loop", P("loop"));
                using (new EditorGUI.DisabledScope(!P("loop").boolValue))
                {
                    BrazeEditorGUI.Hairline();
                    BrazeEditorGUI.PropertyField(P("loopCount"));
                    BrazeEditorGUI.Hairline();
                    BrazeEditorGUI.MinMaxRow(new UnityEngine.GUIContent("Loop Delay"),
                        P("loopDelayMin"), P("loopDelayMax"));
                }
                if (P("loop").boolValue && P("loopCount").intValue == 0)
                    BrazeEditorGUI.Hint("Loop Count 0 = loop forever. Set a count to stop after that many bursts. " +
                                        "Burst length and loop delay each roll between Min and Max (equal = fixed).");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static GUIStyle dropStyle, particleRowStyle;

        static void EnsureParticleStyles()
        {
            if (dropStyle != null) return;
            dropStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            dropStyle.alignment = TextAnchor.MiddleCenter;
            dropStyle.fontSize = 11;
            dropStyle.fontStyle = FontStyle.Bold;
            dropStyle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
            particleRowStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            particleRowStyle.fontSize = 11;
            particleRowStyle.alignment = TextAnchor.MiddleLeft;
            particleRowStyle.normal.textColor = new Color(0.86f, 0.88f, 0.92f);
        }

        static void RoundRect(Rect r, Color c, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
        }

        static BrazeParticleSystem ParticleEmitterFrom(UnityEngine.Object o)
        {
            var p = o as BrazeParticleSystem;
            if (p == null && o is GameObject go) p = go.GetComponent<BrazeParticleSystem>();
            return p;
        }

        static void AddParticleEmitterUnique(SerializedProperty arr, BrazeParticleSystem p)
        {
            if (p == null) return;
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == p) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = p;
        }

        void DrawParticleEmittersList(SerializedProperty arr)
        {
            EnsureParticleStyles();
            if (!BrazeEditorGUI.CanEditObjectList(arr)) return;

            for (int i = 0; i < arr.arraySize; i++)
            {
                var p = arr.GetArrayElementAtIndex(i).objectReferenceValue as BrazeParticleSystem;
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);

                var nameR = new Rect(row.x + 10f, row.y, row.width - 36f, row.height);
                GUI.Label(nameR, BrazeEditorGUI.HierarchyName(p), particleRowStyle);
                if (p != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.PingObject(p);
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
            GUI.Label(drop, arr.arraySize == 0 ? "＋   Drop a particle emitter here" : "＋   Drop another particle emitter", dropStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences)
                    if (ParticleEmitterFrom(o) != null) { any = true; break; }

                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        var p = ParticleEmitterFrom(o);
                        if (p != null) AddParticleEmitterUnique(arr, p);
                    }
                }
                e.Use();
            }
        }
    }
}
