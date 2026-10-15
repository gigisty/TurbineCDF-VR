using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazeObstacle"/> (DCC theme).</summary>
    [CustomEditor(typeof(BrazeObstacle))]
    [CanEditMultipleObjects]
    public sealed class BrazeObstacleEditor : Editor
    {
        const string K = "BrazeFX.Obstacle.";
        static readonly string[] ShapeNames = { "Sphere", "Box", "Cylinder", "Cone", "Capsule", "Torus" };
        static readonly int[] ShapeValues = { 0, 1, 2, 3, 4, 5 };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("OBSTACLE", "obstacle");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                var cols = P("useColliders");
                BrazeEditorGUI.ToggleButton("Use Colliders", cols);
                if (cols.boolValue)
                {
                    BrazeEditorGUI.Hint("Compound from Unity Sphere, Box and Capsule Colliders on this object + " +
                                        "children. Supported enabled colliders union automatically.");
                }
                else
                {
                    BrazeEditorGUI.Separator();
                    SerializedProperty shape = P("shape");
                    BrazeEditorGUI.PillsIntGrid("Shape", shape, ShapeNames, ShapeValues, 3);
                    if (!shape.hasMultipleDifferentValues)
                    {
                        switch ((BrazeObstacle.Shape)shape.intValue)
                        {
                            case BrazeObstacle.Shape.Sphere:
                                BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                                break;
                            case BrazeObstacle.Shape.Capsule:
                            case BrazeObstacle.Shape.Cylinder:
                            case BrazeObstacle.Shape.Cone:
                                BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Radius"));
                                BrazeEditorGUI.PropertyField(P("height"), new GUIContent("Height"));
                                break;
                            case BrazeObstacle.Shape.Torus:
                                BrazeEditorGUI.PropertyField(P("radius"), new GUIContent("Major Radius"));
                                BrazeEditorGUI.PropertyField(P("tubeRadius"), new GUIContent("Tube Radius"));
                                break;
                        }
                    }
                    BrazeEditorGUI.Hint("Sphere, Capsule, Cylinder, Cone and Torus use the dimensions above plus " +
                                        "Transform scale; their axis is local Y. Box uses Transform scale directly. " +
                                        "A mesh on this object auto-fits the selected primitive to its bounds.");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "debug", "DEBUG"))
            {
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Draws the collision shape in the Scene view. Off = no gizmo at all (even when selected).");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
