using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector + scene handle for <see cref="BrazeRuler"/>.</summary>
    [CustomEditor(typeof(BrazeRuler))]
    [CanEditMultipleObjects]
    public sealed class BrazeRulerEditor : Editor
    {
        const string K = "BrazeFX.Ruler.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("RULER", "ruler");
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            var ruler = (BrazeRuler)target;
            BrazeEditorGUI.Hint($"Reads {ruler.Readout()}. A pure visual reference — no simulation. Place it beside an " +
                                "asset to gauge real-world scale; drag the arrow handle in the scene to resize.");

            if (BrazeEditorGUI.Section(K + "measure", "MEASURE"))
            {
                BrazeEditorGUI.PillsEnum("Axis", P("axis"));
                BrazeEditorGUI.PropertyField(P("length"), new GUIContent("Length (units)"));
                BrazeEditorGUI.PillsEnum("Units", P("units"));
                BrazeEditorGUI.PropertyField(P("unitsPerMeter"), new GUIContent("Units / Metre"));
                BrazeEditorGUI.Hint("Length is the ruler's physical size in world units; the readout converts it to feet. " +
                                    "Units / Metre tells the ruler your scene scale — 1 for metres, 100 for centimetres.");

                BrazeEditorGUI.SubLabel("QUICK SIZE (FEET)");
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Human 6'")) SetFeet(6f);
                    if (GUILayout.Button("Door 7'")) SetFeet(7f);
                    if (GUILayout.Button("Story 10'")) SetFeet(10f);
                    if (GUILayout.Button("100'")) SetFeet(100f);
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "look", "APPEARANCE"))
            {
                BrazeEditorGUI.PropertyField(P("color"), new GUIContent("Color"));
                BrazeEditorGUI.ToggleButton("Graduation Ticks", P("ticks"));
                BrazeEditorGUI.PropertyField(P("tickSize"), new GUIContent("Tick Size"));
                BrazeEditorGUI.ToggleButton("Tick Labels", P("tickLabels"));
                BrazeEditorGUI.IntSlider(P("fontSize"), 8, 40, new GUIContent("Font Size"));
                BrazeEditorGUI.ToggleButton("Always Show", P("alwaysShow"));
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        void SetFeet(float feet)
        {
            foreach (Object o in targets)
            {
                var r = (BrazeRuler)o;
                Undo.RecordObject(r, "Set Ruler Size");
                r.SetFeet(feet);
                EditorUtility.SetDirty(r);
            }
            SceneView.RepaintAll();
        }

        void OnSceneGUI()
        {
            var r = (BrazeRuler)target;
            Vector3 tip = r.Tip;
            float size = HandleUtility.GetHandleSize(tip) * 0.9f;

            EditorGUI.BeginChangeCheck();
            Handles.color = r.color;
            Vector3 moved = Handles.Slider(tip, r.AxisDir, size, Handles.ArrowHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(r, "Resize Ruler");
                r.length = Mathf.Max(0.0001f, Vector3.Dot(moved - r.Base, r.AxisDir));
                EditorUtility.SetDirty(r);
            }
        }
    }
}
