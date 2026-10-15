using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeSplineSurface))]
    public sealed class BrazeSplineSurfaceEditor : Editor
    {
        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        public override void OnInspectorGUI()
        {
            var ss = (BrazeSplineSurface)target;
            serializedObject.Update();

            BrazeEditorGUI.Header("SPLINE SURFACE", "spline");
            BrazeEditorGUI.PropertyField(P("spline"), new GUIContent("Spline"));
            BrazeEditorGUI.PropertyField(P("surfaceMesh"), new GUIContent("Surface Mesh"));
            BrazeEditorGUI.Hint("Separate authoring component: it writes points onto the referenced spline, but the spline remains a normal reusable Braze Spline.");

            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PillsEnum("Mode", P("mode"));
            BrazeEditorGUI.PropertyField(P("surfaceOffset"), new GUIContent("Surface Offset"));

            var mode = (BrazeSplineSurface.SurfaceMode)P("mode").enumValueIndex;
            if (mode == BrazeSplineSurface.SurfaceMode.GenerateWrap)
            {
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubLabel("WRAP");
                BrazeEditorGUI.PropertyField(P("axis"), new GUIContent("Axis"));
                BrazeEditorGUI.PropertyField(P("turns"), new GUIContent("Turns"));
                BrazeEditorGUI.PropertyField(P("stairCount"), new GUIContent("Stair Count"));
                using (new EditorGUI.DisabledScope(P("stairCount").intValue > 0))
                    BrazeEditorGUI.PropertyField(P("pointsPerTurn"), new GUIContent("Points / Turn"));
                BrazeEditorGUI.PropertyField(P("heightPadding"), new GUIContent("Height Padding"));
                BrazeEditorGUI.PropertyField(P("wobble"), new GUIContent("Wobble"));
                BrazeEditorGUI.PropertyField(P("wobbleFrequency"), new GUIContent("Wobble Frequency"));
                BrazeEditorGUI.PropertyField(P("randomness"), new GUIContent("Randomness"));
                BrazeEditorGUI.PropertyField(P("randomSeed"), new GUIContent("Random Seed"));
                BrazeEditorGUI.PropertyField(P("smoothness"), new GUIContent("Smoothness"));
                BrazeEditorGUI.PropertyField(P("phase"), new GUIContent("Phase"));
                BrazeEditorGUI.Hint("Stair Count overrides the total generated control points. Randomness is stable per seed; Smoothness relaxes kinked hits and snaps them back to the surface.");
            }
            else
            {
                BrazeEditorGUI.Hint("Project Existing keeps the current spline point count and snaps each point to the nearest surface.");
            }

            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PropertyField(P("liveUpdate"), new GUIContent("Live Update"));
            BrazeEditorGUI.PropertyField(P("rebuildInPlayMode"), new GUIContent("CPU Rebuild In Play"));
            BrazeEditorGUI.PropertyField(P("trackSkinnedInPlayMode"), new GUIContent("Track Skinned In Play"));
            BrazeEditorGUI.PropertyField(P("drawPreview"), new GUIContent("Draw Preview"));
            if (P("drawPreview").boolValue)
                BrazeEditorGUI.PropertyField(P("previewColor"), new GUIContent("Preview Color"));

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed) SceneView.RepaintAll();

            using (new EditorGUI.DisabledScope(ss.TargetSpline == null || ss.TargetSurface == null))
            {
                if (GUILayout.Button("Wrap Spline Now"))
                {
                    foreach (Object o in targets)
                    {
                        var item = (BrazeSplineSurface)o;
                        Undo.RecordObject(item, "Wrap Spline Surface");
                        if (item.TargetSpline != null) Undo.RecordObject(item.TargetSpline, "Wrap Spline Surface");
                        item.WrapNow();
                        EditorUtility.SetDirty(item);
                        if (item.TargetSpline != null) EditorUtility.SetDirty(item.TargetSpline);
                    }
                    SceneView.RepaintAll();
                }
            }

            if (!string.IsNullOrEmpty(ss.Status)) BrazeEditorGUI.Hint(ss.Status);
        }
    }
}
