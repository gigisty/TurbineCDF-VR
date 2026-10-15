using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for <see cref="BrazeFollowSpline"/>.</summary>
    [CustomEditor(typeof(BrazeFollowSpline))]
    [CanEditMultipleObjects]
    public sealed class BrazeFollowSplineEditor : Editor
    {
        const string K = "BrazeFX.FollowSpline.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("BRAZE FOLLOW SPLINE", "followspline");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "path", "PATH"))
            {
                BrazeEditorGUI.PropertyField(P("spline"), new GUIContent("Spline"));
                BrazeEditorGUI.Slider(P("position"), 0f, 1f, new GUIContent("Position"));
                BrazeEditorGUI.PropertyField(P("speed"), new GUIContent("Speed"));
                BrazeEditorGUI.PillsEnum("Speed Unit", P("speedUnit"));
                BrazeEditorGUI.PillsEnum("Wrap", P("wrap"));
                BrazeEditorGUI.ToggleButton("Constant Speed", P("constantSpeed"));
                BrazeEditorGUI.Hint("The spline is the rail; this object is the train. Position places it (animate " +
                                    "for a keyed ride), Speed adds continuous travel on top — Path/s = fractions of " +
                                    "the whole path, Units/s = metres per second. Wrap decides the behaviour at the " +
                                    "ends; Constant Speed makes travel arc-length-even (keep it on).");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "align", "ALIGNMENT"))
            {
                BrazeEditorGUI.ToggleButton("Align To Spline", P("alignToSpline"));
                if (P("alignToSpline").boolValue)
                {
                    BrazeEditorGUI.Slider(P("lookAhead"), 0f, 0.25f, new GUIContent("Look Ahead"));
                    BrazeEditorGUI.PillsEnum("Up", P("upMode"));
                    BrazeEditorGUI.ToggleButton("Face Travel Direction", P("faceTravelDirection"));
                    BrazeEditorGUI.Slider(P("banking"), 0f, 5f, new GUIContent("Banking"));
                    if (P("banking").floatValue > 1e-3f)
                    {
                        BrazeEditorGUI.Slider(P("maxBank"), 0f, 89f, new GUIContent("Max Bank"));
                        BrazeEditorGUI.Slider(P("bankSmoothing"), 0f, 2f, new GUIContent("Bank Smoothing"));
                    }
                    BrazeEditorGUI.PropertyField(P("rotationOffset"), new GUIContent("Rotation Offset"));
                }
                BrazeEditorGUI.Hint("Align turns the object into the direction of travel. Look Ahead aims at a point " +
                                    "further down the track for a smoother heading through corners. Face Travel " +
                                    "Direction flips the nose on a PingPong return leg / negative Speed. Banking " +
                                    "tilts into curves like a jet — Bank Smoothing eases the lean in/out so the roll " +
                                    "builds and settles instead of snapping; Rotation Offset fixes meshes not " +
                                    "authored facing +Z.");

                BrazeEditorGUI.SubHeader("AIM OVERRIDE");
                BrazeEditorGUI.PropertyField(P("lookTarget"), new GUIContent("Look Target"));
                if (P("lookTarget").objectReferenceValue != null)
                    BrazeEditorGUI.PropertyField(P("lookTargetOffset"), new GUIContent("Target Offset"));
                BrazeEditorGUI.Hint("Optional: still ride the rail, but FACE this transform instead of the travel " +
                                    "direction — a camera dolly or turret tracking a subject.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "feel", "OFFSET & FEEL"))
            {
                BrazeEditorGUI.PropertyField(P("offset"), new GUIContent("Offset"));
                BrazeEditorGUI.Slider(P("damping"), 0f, 2f, new GUIContent("Damping"));
                BrazeEditorGUI.Hint("Offset rides in the track frame when aligned (X = right of travel, Y = up — " +
                                    "lift a train onto its wheels), world units otherwise. Damping smooths position " +
                                    "and rotation; 0 = locked hard to the rail.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
