using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeSoloFlame))]
    [CanEditMultipleObjects]
    public sealed class BrazeSoloFlameEditor : Editor
    {
        const string K = "BrazeFX.SoloFlame.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("SOLO FLAME", "flame");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "preset", "PRESET"))
            {
                BrazeEditorGUI.PillsEnum("Preset", P("preset"));
                BrazeEditorGUI.Hint("Preset selects one of the original shader's individual flame looks. Only one flame is rendered on the card at a time.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                BrazeEditorGUI.PropertyField(P("size"), new GUIContent("Size"));
                BrazeEditorGUI.PropertyField(P("autoFitCard"), new GUIContent("Auto Fit Card"));
                if (P("autoFitCard").boolValue)
                    BrazeEditorGUI.Slider(P("fitPadding"), 0.8f, 1.5f, new GUIContent("Fit Padding"));
                BrazeEditorGUI.PropertyField(P("pivot"), new GUIContent("Pivot"));
                BrazeEditorGUI.Hint(P("autoFitCard").boolValue
                    ? "The card is fitted to the original flame cell. Size Y controls height; Size X is ignored while fitting is enabled."
                    : "The card is a local XY plane. X is strip width, Y is flame height. Rotate or scale the GameObject for placement.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "look", "LOOK"))
            {
                var type = P("flameType");
                BrazeEditorGUI.PillsEnum("Flame Type", type);
                if ((BrazeSoloFlame.FlameType)type.enumValueIndex == BrazeSoloFlame.FlameType.Custom)
                    BrazeEditorGUI.PropertyField(P("customTint"), new GUIContent("Custom Tint"));
                BrazeEditorGUI.Slider(P("intensity"), 0f, 8f, new GUIContent("Intensity"));
                BrazeEditorGUI.Slider(P("billowHeat"), 0f, 2f, new GUIContent("Billow Heat"));
                BrazeEditorGUI.Slider(P("opacity"), 0f, 1f, new GUIContent("Opacity"));
                BrazeEditorGUI.PillsEnum("Blend", P("blendMode"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "flame", "FLAME"))
            {
                BrazeEditorGUI.Slider(P("scale"), 0.25f, 4f, new GUIContent("Scale"));
                BrazeEditorGUI.Slider(P("speed"), 0f, 8f, new GUIContent("Speed"));
                BrazeEditorGUI.IntSlider(P("detail"), 1, 8, new GUIContent("Detail"));
                BrazeEditorGUI.PillsEnum("Quality", P("renderQuality"));
                BrazeEditorGUI.Slider(P("sharpness"), 0.2f, 4f, new GUIContent("Sharpness"));
                BrazeEditorGUI.Slider(P("edgeErosion"), 0f, 1f, new GUIContent("Edge Erosion"));
                BrazeEditorGUI.Slider(P("flameHeight"), 0.5f, 3f, new GUIContent("Flame Height"));
                BrazeEditorGUI.Slider(P("verticalStretch"), 0.25f, 5f, new GUIContent("Vertical Stretch"));
                BrazeEditorGUI.Slider(P("smoke"), 0f, 1f, new GUIContent("Smoke"));
                if (P("smoke").floatValue > 0f)
                    BrazeEditorGUI.PropertyField(P("smokeColor"), new GUIContent("Smoke Tint"));
                BrazeEditorGUI.Slider(P("softEdge"), 0f, 0.45f, new GUIContent("Card Soft Edge"));
                BrazeEditorGUI.Slider(P("seed"), 0f, 1000f, new GUIContent("Seed"));
                if (GUILayout.Button("Randomize Seed"))
                {
                    serializedObject.ApplyModifiedProperties();
                    foreach (Object o in targets)
                    {
                        Undo.RecordObject(o, "Randomize Solo Flame Seed");
                        ((BrazeSoloFlame)o).seed = Random.value * 1000f;
                        EditorUtility.SetDirty(o);
                    }
                    serializedObject.Update();
                }
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
            // NOTE: edit-mode animation is driven by BrazeSoloFlame.EditorTick (EditorApplication.update),
            // NOT by repainting from here — a self-Repaint() in OnInspectorGUI trips the "named GUIStyle
            // without a current skin" error via InspectorWindow.RedrawFromNative.
        }
    }
}
