using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazeSurfaceDisintegrator"/> (DCC theme).</summary>
    [CustomEditor(typeof(BrazeSurfaceDisintegrator))]
    [CanEditMultipleObjects]
    public sealed class BrazeSurfaceDisintegratorEditor : Editor
    {
        const string K = "BrazeFX.Disintegrator.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("SURFACE DISINTEGRATOR", "disintegrate");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "TARGET"))
            {
                BrazeEditorGUI.PropertyField(P("target"));
                if (targets.Length == 1 && target is BrazeSurfaceDisintegrator dt && dt != null && dt.PartCount > 1)
                    BrazeEditorGUI.SubLabel($"{dt.PartCount} renderers");
                BrazeEditorGUI.Hint("Mesh / skinned renderer to break into clumps. EMPTY = disintegrate EVERY " +
                                    "SkinnedMeshRenderer + MeshRenderer under this object (a parent with many children; " +
                                    "mixed skinned/static is fine). In Play the source renderers are hidden and the " +
                                    "disintegrator draws them (as anchored chunks) instead.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "clumps", "CLUMPS"))
            {
                BrazeEditorGUI.PropertyField(P("clumpSize"));
                BrazeEditorGUI.Slider(P("irregularity"), 0f, 1f);
                BrazeEditorGUI.PropertyField(P("seed"));
                if (targets.Length == 1 && target is BrazeSurfaceDisintegrator d && d != null && d.ClumpCount > 0)
                    BrazeEditorGUI.SubLabel($"{d.ClumpCount} clumps");
                BrazeEditorGUI.Hint("Clump Size = chunk diameter (world). Irregularity: boxy (0) → organic (1). " +
                                    "Clumps are assigned in the REST pose, so they stay stable as the body animates.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "forces", "FORCES"))
            {
                DrawForcesList(P("forces"));
                BrazeEditorGUI.Hint("Leave empty to use ALL active Braze forces in the scene. Drop forces here to scope free chunks to only those forces.");
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("releaseImpulse"));
                BrazeEditorGUI.PropertyField(P("releaseSpin"));
                BrazeEditorGUI.Slider(P("inheritBodyVelocity"), 0f, 2f);
                BrazeEditorGUI.Hint("Free chunks are driven on the GPU by Braze forces (Gravity / Wind / Drag / " +
                                    "Radial / Vortex / Turbulence). Turbulence conforms chunk motion to the noise field. " +
                                    "Add a Braze Gravity to make them fall.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "age", "AGE"))
            {
                BrazeEditorGUI.PropertyField(P("clumpLifetime"));
                if (P("clumpLifetime").floatValue > 0f)
                {
                    BrazeEditorGUI.Slider(P("shrink"), 0f, 1f);
                    BrazeEditorGUI.Slider(P("dissolve"), 0f, 1f);
                    BrazeEditorGUI.PropertyField(P("dissolveScale"));
                    BrazeEditorGUI.PropertyField(P("dissolveEdge"));
                }
                BrazeEditorGUI.Hint("After release a chunk lives for Lifetime seconds — shrinking and eaten away by a " +
                                    "noise dissolve until it's gone (the 'crumbling into nothing' look). 0 = persist forever.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "soft", "SOFT BODY"))
            {
                BrazeEditorGUI.ToggleButton("Soft Body", P("softBody"));
                if (P("softBody").boolValue) BrazeEditorGUI.Slider(P("softness"), 0f, 1f);
                BrazeEditorGUI.Hint("A cheap vertex-shader wobble on free chunks (scaled by their speed) — no sim cost.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "debris", "SECONDARY PARTICLES"))
            {
                BrazeEditorGUI.PropertyField(P("debrisEmitter"));
                if (P("debrisEmitter").objectReferenceValue != null)
                {
                    BrazeEditorGUI.ToggleButton("Spawn While Shrinking", P("debrisFromShrinkingClumps"));
                    if (P("debrisFromShrinkingClumps").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("debrisRatePerChunk"));
                        if (P("clumpLifetime").floatValue <= 0f)
                            EditorGUILayout.HelpBox("Set Age > Lifetime above zero for shrinking chunks. With Lifetime at 0, free chunks persist and keep shedding.", MessageType.Info);
                    }
                    else
                    {
                        BrazeEditorGUI.PropertyField(P("debrisPointsPerChunk"));
                    }
                    BrazeEditorGUI.Slider(P("debrisInheritVelocity"), 0f, 3f);
                    BrazeEditorGUI.PropertyField(P("debrisScatter"));
                    BrazeEditorGUI.PropertyField(P("maxDebrisPoints"));
                }
                BrazeEditorGUI.Hint("Off: clumps append one GPU burst from sampled surface points when they separate. " +
                                    "On: free chunks keep appending GPU requests from their current shrinking surface. " +
                                    "The emitter owns particle look, lifetime, size, colour, and forces.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "fluid", "FLUID EMISSION (smoke / fire)"))
            {
                BrazeEditorGUI.PropertyField(P("emitToFluid"), new GUIContent("Emit To Fluid"));
                if (P("emitToFluid").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("emitTrail"), new GUIContent("Trail (flying clumps)"));
                    BrazeEditorGUI.PropertyField(P("emitBurst"), new GUIContent("Burst (at break-off)"));
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PropertyField(P("emitDensity"), new GUIContent("Smoke (density)"));
                    BrazeEditorGUI.PropertyField(P("emitTemperature"), new GUIContent("Heat (temperature)"));
                    BrazeEditorGUI.PropertyField(P("emitFuel"), new GUIContent("Fuel (fire)"));
                    BrazeEditorGUI.Separator();
                    if (P("emitBurst").boolValue)
                    {
                        BrazeEditorGUI.Slider(P("emitBurstScale"), 1f, 20f, new GUIContent("Burst Scale"));
                        BrazeEditorGUI.PropertyField(P("emitBurstTime"), new GUIContent("Burst Time (s)"));
                    }
                    BrazeEditorGUI.Slider(P("emitCoolWithAge"), 0f, 1f, new GUIContent("Cool With Age"));
                    if (P("emitCoolWithAge").floatValue > 0f)
                        BrazeEditorGUI.PropertyField(P("emitCoolTime"), new GUIContent("Cool Time (s)"));
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.Slider(P("emitVelocityInherit"), 0f, 1f, new GUIContent("Velocity Inherit"));
                }
                BrazeEditorGUI.Hint("Released clumps inject smoke/fire into any Braze Container they fly through (no parenting). " +
                                    "Trail = continuous wake from each flying clump; Burst = an extra puff at break-off. " +
                                    "Cool With Age fades heat/fuel as clumps age so burning debris chars into pure smoke. " +
                                    "Costs a small async readback of clump positions (play mode).");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "hit", "ERODER"))
            {
                DrawEroderList(P("eroders"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("hitRadius"));
                BrazeEditorGUI.Hint("Drop one or more Braze Particle Systems — their particles knock chunks loose " +
                                    "where they touch the surface (GPU proximity). Hit Radius = contact distance. " +
                                    "Empty = manual release only (the button below).");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "events", "EVENTS"))
            {
                BrazeEditorGUI.PropertyField(P("OnStartedDisintegration"));
                BrazeEditorGUI.PropertyField(P("OnFinishedDisintegration"));
                BrazeEditorGUI.Hint("Start is called once after Release All or Release In Chunks successfully begins. " +
                                    "Finish is called after all clumps have reached the end of Clump Lifetime and " +
                                    "any secondary debris spawned by this disintegrator has aged out. " +
                                    "With Clump Lifetime at 0, released clumps persist, so the event does not fire.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "preview", "PREVIEW"))
            {
                BrazeEditorGUI.ToggleButton("Show Clumps", P("showClumps"));
                if (P("showClumps").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("maxDots"));
                    BrazeEditorGUI.PropertyField(P("dotSize"));
                }
                BrazeEditorGUI.Hint("Colours each surface vertex by its clump in the Scene view (edit mode).");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "release", "RELEASE"))
            {
                BrazeEditorGUI.PropertyField(P("releaseInChunksDuration"));
                BrazeEditorGUI.Hint("Release All breaks every clump on the same frame. Release In Chunks marks random " +
                                    "GPU clump hits over this duration, then uses the same release path as eroding particles.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            // Test triggers (Play only) — release everything so you can watch it fall.
            using (new EditorGUI.DisabledScope(!Application.isPlaying || targets.Length != 1))
            {
                GUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("▶  Release All", GUILayout.Height(24)) && target is BrazeSurfaceDisintegrator dd)
                        dd.ReleaseAll();
                    if (GUILayout.Button("▶  Release In Chunks", GUILayout.Height(24)) && target is BrazeSurfaceDisintegrator chunked)
                        chunked.ReleaseInChunks();
                }
            }
            if (!Application.isPlaying) BrazeEditorGUI.Hint("Enter Play, then use the release buttons to test the break-up.");
        }

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

        static void AddForceUnique(SerializedProperty arr, BrazeParticleForce f)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == f) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = f;
        }

        static BrazeParticleSystem EroderFrom(UnityEngine.Object o)
        {
            var ps = o as BrazeParticleSystem;
            if (ps == null && o is GameObject go) ps = go.GetComponent<BrazeParticleSystem>();
            return ps;
        }

        static void AddObjectUnique(SerializedProperty arr, UnityEngine.Object obj)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == obj) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = obj;
        }

        // Drop-in list for eroder particle systems — mirrors the FORCES list UI.
        void DrawEroderList(SerializedProperty arr)
        {
            EnsureForceStyles();
            if (!BrazeEditorGUI.CanEditObjectList(arr)) return;

            for (int i = 0; i < arr.arraySize; i++)
            {
                var ps = arr.GetArrayElementAtIndex(i).objectReferenceValue as BrazeParticleSystem;
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);

                var nameR = new Rect(row.x + 10f, row.y, row.width - 36f, row.height);
                GUI.Label(nameR, BrazeEditorGUI.HierarchyName(ps), forceRowStyle);
                if (ps != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.PingObject(ps);
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
            GUI.Label(drop, arr.arraySize == 0 ? "＋   Drop an eroder here" : "＋   Drop another eroder", dropStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences)
                    if (EroderFrom(o) != null) { any = true; break; }

                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        var ps = EroderFrom(o);
                        if (ps != null) AddObjectUnique(arr, ps);
                    }
                }
                e.Use();
            }
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
    }
}
