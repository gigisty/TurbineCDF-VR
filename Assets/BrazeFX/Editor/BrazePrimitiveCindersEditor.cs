using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazePrimitiveCinders))]
    [CanEditMultipleObjects]
    public sealed class BrazePrimitiveCindersEditor : Editor
    {
        const string K = "BrazeFX.PrimitiveCinders.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("PRIMITIVE CINDERS", "flame");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                var primitive = P("primitive");
                EditorGUI.BeginChangeCheck();
                BrazeEditorGUI.PillsEnum("Primitive", primitive);
                if (EditorGUI.EndChangeCheck())
                    ApplyPrimitiveDefaults(primitive, P("size"), P("torusTube"));

                BrazeEditorGUI.PropertyField(P("size"), new GUIContent("Size"));

                bool showResolution = true;
                bool showTorusTube = primitive.hasMultipleDifferentValues;
                if (!primitive.hasMultipleDifferentValues)
                {
                    var shape = (BrazePrimitiveCinders.PrimitiveShape)primitive.enumValueIndex;
                    showResolution = shape == BrazePrimitiveCinders.PrimitiveShape.Sphere ||
                                     shape == BrazePrimitiveCinders.PrimitiveShape.Torus ||
                                     shape == BrazePrimitiveCinders.PrimitiveShape.Capsule;
                    showTorusTube = shape == BrazePrimitiveCinders.PrimitiveShape.Torus;
                }

                using (new EditorGUI.DisabledScope(!showResolution))
                    BrazeEditorGUI.IntSlider(P("resolution"), 8, 96, new GUIContent("Resolution"));
                if (showTorusTube)
                    BrazeEditorGUI.Slider(P("torusTube"), 0.05f, 0.45f, new GUIContent("Torus Tube"));

                BrazeEditorGUI.Hint("Primitive Cinders is a cheap procedural surface. Use a cube or plane for sword/prop fire, and curved primitives for static fire/smoke volumes.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "mapping", "MAPPING"))
            {
                BrazeEditorGUI.PillsEnum("Flow Axis", P("flowAxis"));
                BrazeEditorGUI.Slider(P("deStretch"), 0f, 1f, new GUIContent("De-Stretch"));
                BrazeEditorGUI.PropertyField(P("domainStretch"), new GUIContent("Domain Stretch"));
                BrazeEditorGUI.Slider(P("scale"), 0.05f, 16f, new GUIContent("Scale"));
                BrazeEditorGUI.Hint("De-Stretch compensates transform scale before sampling the fire field. Domain Stretch is the manual per-axis art control when the primitive is intentionally long or thin.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "look", "LOOK"))
            {
                BrazeEditorGUI.PropertyField(P("fireColor"), new GUIContent("Fire Color"));
                BrazeEditorGUI.PropertyField(P("smokeColor"), new GUIContent("Smoke Color"));
                BrazeEditorGUI.Slider(P("intensity"), 0f, 8f, new GUIContent("Intensity"));
                BrazeEditorGUI.Slider(P("opacity"), 0f, 1f, new GUIContent("Opacity"));
                BrazeEditorGUI.PillsEnum("Blend", P("blendMode"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "motion", "MOTION"))
            {
                var reactive = P("motionReactive");
                BrazeEditorGUI.ToggleButton("Reactive", reactive);
                using (new EditorGUI.DisabledScope(!reactive.hasMultipleDifferentValues && !reactive.boolValue))
                {
                    BrazeEditorGUI.Slider(P("motionInfluence"), 0f, 2f, new GUIContent("Influence"));
                    BrazeEditorGUI.Slider(P("motionResponse"), 0f, 30f, new GUIContent("Response"));
                    BrazeEditorGUI.Slider(P("motionMaxSpeed"), 0f, 50f, new GUIContent("Max Speed"));
                    BrazeEditorGUI.Slider(P("motionStretch"), 0f, 2f, new GUIContent("Stretch"));
                }
                BrazeEditorGUI.Hint("Reactive uses this transform's world velocity to shear the fire opposite movement. It is visual only: no particles, no solver, no extra passes.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "cinders", "CINDERS"))
            {
                BrazeEditorGUI.Slider(P("flameHeight"), 0.1f, 2.5f, new GUIContent("Height"));
                BrazeEditorGUI.Slider(P("coverage"), 0.05f, 1f, new GUIContent("Coverage"));
                BrazeEditorGUI.Slider(P("speed"), 0f, 8f, new GUIContent("Speed"));
                BrazeEditorGUI.IntSlider(P("detail"), 1, 6, new GUIContent("Detail"));
                BrazeEditorGUI.Slider(P("sharpness"), 0.2f, 4f, new GUIContent("Sharpness"));
                BrazeEditorGUI.Slider(P("edgeErosion"), 0f, 1f, new GUIContent("Edge Erosion"));
                BrazeEditorGUI.Slider(P("heat"), 0f, 4f, new GUIContent("Heat"));
                BrazeEditorGUI.Slider(P("smoke"), 0f, 1f, new GUIContent("Smoke"));
                BrazeEditorGUI.Slider(P("rimFade"), 0f, 1f, new GUIContent("Rim Fade"));
                BrazeEditorGUI.Slider(P("seed"), 0f, 1000f, new GUIContent("Seed"));

                if (GUILayout.Button("Randomize Seed"))
                {
                    serializedObject.ApplyModifiedProperties();
                    foreach (Object o in targets)
                    {
                        Undo.RecordObject(o, "Randomize Primitive Cinders Seed");
                        ((BrazePrimitiveCinders)o).seed = Random.value * 1000f;
                        EditorUtility.SetDirty(o);
                    }
                    serializedObject.Update();
                }
                BrazeEditorGUI.Hint("Height controls visible flame length inside the primitive. Size along Flow Axis sets the outer bounds.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static void ApplyPrimitiveDefaults(SerializedProperty primitive, SerializedProperty size, SerializedProperty torusTube)
        {
            if (primitive.hasMultipleDifferentValues || size.hasMultipleDifferentValues) return;

            switch ((BrazePrimitiveCinders.PrimitiveShape)primitive.enumValueIndex)
            {
                case BrazePrimitiveCinders.PrimitiveShape.Plane:
                    size.vector3Value = new Vector3(2f, 0.001f, 2f);
                    break;
                case BrazePrimitiveCinders.PrimitiveShape.Capsule:
                    size.vector3Value = new Vector3(1f, 2f, 1f);
                    break;
                case BrazePrimitiveCinders.PrimitiveShape.Torus:
                    size.vector3Value = new Vector3(1.5f, 0.35f, 1.5f);
                    torusTube.floatValue = 0.22f;
                    break;
                default:
                    size.vector3Value = Vector3.one;
                    break;
            }
        }
    }
}
