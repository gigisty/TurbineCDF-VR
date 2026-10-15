using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for <see cref="BrazeGroundFollow"/>.</summary>
    [CustomEditor(typeof(BrazeGroundFollow))]
    [CanEditMultipleObjects]
    public sealed class BrazeGroundFollowEditor : Editor
    {
        const string K = "BrazeFX.GroundFollow.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("BRAZE GROUND FOLLOW", "groundfollow");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "ground", "GROUND FOLLOW"))
            {
                BrazeEditorGUI.Slider(P("offset"), 0f, 20f, new GUIContent("Offset"));
                BrazeEditorGUI.PropertyField(P("groundMask"), new GUIContent("Ground Mask"));
                BrazeEditorGUI.ToggleButton("Ignore Own Rig", P("ignoreOwnHierarchy"));
                BrazeEditorGUI.PropertyField(P("castHeight"), new GUIContent("Cast Height"));
                BrazeEditorGUI.PropertyField(P("maxDistance"), new GUIContent("Max Distance"));
                BrazeEditorGUI.Slider(P("positionSmoothing"), 0f, 1f, new GUIContent("Smoothing"));
                BrazeEditorGUI.Hint("Casts straight down and holds this object Offset units above the ground. Only the " +
                                    "VERTICAL position is pinned — the parent's horizontal motion (and tumble) still " +
                                    "carries through. Cast Height starts the ray above the object so it never misses the " +
                                    "ground; Smoothing softens a bobbing follow (0 = exact snap).");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "orient", "ORIENTATION"))
            {
                var mode = P("align");
                BrazeEditorGUI.PillsEnum("Align", mode);
                if (mode.enumValueIndex == (int)BrazeGroundFollow.Align.Surface)
                {
                    BrazeEditorGUI.Slider(P("alignAmount"), 0f, 1f, new GUIContent("Align Amount"));
                    BrazeEditorGUI.Slider(P("maxTilt"), 0f, 90f, new GUIContent("Max Tilt"));
                }
                BrazeEditorGUI.Slider(P("rotationSmoothing"), 0f, 1f, new GUIContent("Rot Smoothing"));
                BrazeEditorGUI.Hint("None keeps the parent's rotation. Upright cancels the parent's tilt (the source " +
                                    "stays vertical while the helicopter banks). Surface tilts toward the ground slope " +
                                    "— Align Amount blends it in, Max Tilt stops a steep slope from flipping it.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "gizmo", "GIZMO"))
            {
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Draws the cast ray, the ground hit, the held-offset target and the surface normal when selected.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
