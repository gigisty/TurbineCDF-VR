using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for <see cref="BrazeMotion"/>.</summary>
    [CustomEditor(typeof(BrazeMotion))]
    [CanEditMultipleObjects]
    public sealed class BrazeMotionEditor : Editor
    {
        const string K = "BrazeFX.Motion.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("BRAZE MOTION", "motion");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "main", "MOTION"))
            {
                BrazeEditorGUI.ToggleButton("Relative", P("relative"));
                BrazeEditorGUI.PropertyField(P("seed"), new GUIContent("Seed"));
                BrazeEditorGUI.Slider(P("strength"), 0f, 4f, new GUIContent("Strength"));
                BrazeEditorGUI.ToggleButton("Playing", P("playing"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "cycle", "CYCLE"))
            {
                BrazeEditorGUI.PillsEnum("Mode", P("cycleMode"));
                BrazeEditorGUI.PropertyField(P("duration"), new GUIContent("Duration"));
                BrazeEditorGUI.Slider(P("timeScale"), 0f, 4f, new GUIContent("Time Scale"));
                BrazeEditorGUI.ToggleButton("Seamless Loop", P("seamlessLoop"));
                BrazeEditorGUI.ToggleButton("Restore Off", P("restoreOnDisable"));
                BrazeEditorGUI.Hint("Spin: Amount / Cycle is degrees per spin cycle; Speed is cycles per second. FreeRun keeps accumulating. Loop/PingPong/Once use Duration for wrapping, easing, and the strength curve.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "curves", "EASING"))
            {
                BrazeEditorGUI.PropertyField(P("timeEase"), new GUIContent("Time Ease"));
                BrazeEditorGUI.PropertyField(P("strengthCurve"), new GUIContent("Strength Curve"));
            }
            BrazeEditorGUI.EndSection();

            DrawPosition(P);
            DrawRotation(P);
            DrawScale(P);

            if (BrazeEditorGUI.Section(K + "rest", "REST POSE"))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Capture Current"))
                    foreach (var t in targets) Capture((BrazeMotion)t);
                if (GUILayout.Button("Restore"))
                    foreach (var t in targets) Restore((BrazeMotion)t);
                if (GUILayout.Button("Restart"))
                    foreach (var t in targets) Restart((BrazeMotion)t);
                EditorGUILayout.EndHorizontal();
                BrazeEditorGUI.Hint("Capture stores the no-motion transform. Restart rewinds the procedural clock.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawPosition(System.Func<string, SerializedProperty> P)
        {
            if (BrazeEditorGUI.Section(K + "position", "POSITION"))
            {
                BrazeEditorGUI.ToggleButton("Enable Position", P("enablePosition"));
                using (new EditorGUI.DisabledScope(!P("enablePosition").boolValue))
                {
                    BrazeEditorGUI.PillsEnum("Mode", P("positionMode"));
                    BrazeEditorGUI.PropertyField(P("positionAmount"), new GUIContent("Amount"));
                    BrazeEditorGUI.PropertyField(P("positionSpeed"), new GUIContent("Speed"));
                    BrazeEditorGUI.PropertyField(P("positionOffset"), new GUIContent("Timing Offset"));
                }
            }
            BrazeEditorGUI.EndSection();
        }

        static void DrawRotation(System.Func<string, SerializedProperty> P)
        {
            if (BrazeEditorGUI.Section(K + "rotation", "ROTATION"))
            {
                BrazeEditorGUI.ToggleButton("Enable Rotation", P("enableRotation"));
                using (new EditorGUI.DisabledScope(!P("enableRotation").boolValue))
                {
                    var mode = P("rotationMode");
                    BrazeEditorGUI.PillsEnum("Mode", mode);
                    BrazeEditorGUI.PropertyField(P("rotationAmount"), new GUIContent(mode.enumValueIndex == 1 ? "Amount / Cycle °" : "Amount °"));
                    BrazeEditorGUI.PropertyField(P("rotationSpeed"), new GUIContent(mode.enumValueIndex == 1 ? "Speed (cycles/sec)" : "Speed"));
                    if (mode.enumValueIndex != 1)
                        BrazeEditorGUI.PropertyField(P("rotationOffset"), new GUIContent("Timing Offset"));
                }
            }
            BrazeEditorGUI.EndSection();
        }

        static void DrawScale(System.Func<string, SerializedProperty> P)
        {
            if (BrazeEditorGUI.Section(K + "scale", "SCALE"))
            {
                BrazeEditorGUI.ToggleButton("Enable Scale", P("enableScale"));
                using (new EditorGUI.DisabledScope(!P("enableScale").boolValue))
                {
                    BrazeEditorGUI.PillsEnum("Mode", P("scaleMode"));
                    BrazeEditorGUI.PillsEnum("Apply", P("scaleApply"));
                    BrazeEditorGUI.ToggleButton("Uniform", P("uniformScale"));
                    BrazeEditorGUI.PropertyField(P("scaleAmount"), new GUIContent("Amount"));
                    BrazeEditorGUI.PropertyField(P("scaleSpeed"), new GUIContent("Speed"));
                    BrazeEditorGUI.PropertyField(P("scaleOffset"), new GUIContent("Timing Offset"));
                }
            }
            BrazeEditorGUI.EndSection();
        }

        static void Capture(BrazeMotion m)
        {
            Undo.RecordObject(m.transform, "Capture Braze Motion Rest Pose");
            Undo.RecordObject(m, "Capture Braze Motion Rest Pose");
            m.CaptureRestPose();
            EditorUtility.SetDirty(m);
        }

        static void Restore(BrazeMotion m)
        {
            Undo.RecordObject(m.transform, "Restore Braze Motion Rest Pose");
            m.RestoreRestPose();
            EditorUtility.SetDirty(m.transform);
        }

        static void Restart(BrazeMotion m)
        {
            Undo.RecordObject(m, "Restart Braze Motion");
            m.Restart();
            EditorUtility.SetDirty(m);
        }
    }
}
