using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for every <see cref="BrazeVolumeField"/> (and subclasses). Draws the default fields but
    /// hides the Spline-region knobs unless the field's shape is set to Spline.</summary>
    [CustomEditor(typeof(BrazeVolumeField), true)]
    [CanEditMultipleObjects]
    public sealed class BrazeVolumeFieldEditor : Editor
    {
        // These are only meaningful when shape == Spline, so they're drawn conditionally (excluded from the default pass).
        static readonly string[] SplineProps =
            { "regionSpline", "regionRadius", "regionEdge", "regionRadiusProfile", "regionEndCaps",
              "regionEdgeNoise", "regionNoiseSize", "regionFlowSpeed", "regionWall" };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var exclude = new string[SplineProps.Length + 1];
            exclude[0] = "m_Script";
            System.Array.Copy(SplineProps, 0, exclude, 1, SplineProps.Length);
            BrazeEditorGUI.VisibleProperties(serializedObject, exclude);

            var shapeProp = serializedObject.FindProperty("shape");
            if (shapeProp != null && shapeProp.enumValueIndex == (int)BrazeVolumeField.FieldShape.Spline)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Spline Region", BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel));
                foreach (var name in SplineProps)
                {
                    var p = serializedObject.FindProperty(name);
                    if (p != null) BrazeEditorGUI.PropertyField(p);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
