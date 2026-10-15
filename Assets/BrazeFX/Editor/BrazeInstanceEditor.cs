using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeInstance))]
    [CanEditMultipleObjects]
    public sealed class BrazeInstanceEditor : Editor
    {
        const string K = "BrazeFX.Instance.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("INSTANCE", "instance");
            SerializedProperty P(string name) => serializedObject.FindProperty(name);

            if (BrazeEditorGUI.Section(K + "source", "SOURCE"))
            {
                BrazeEditorGUI.PropertyField(P("source"), new GUIContent("Source"));
                BrazeEditorGUI.PropertyField(P("materialOverride"), new GUIContent("Material Override"));
                BrazeEditorGUI.Hint("One GPU copy of the source, drawn at this Transform through the Braze Cloner " +
                    "indirect path: batched, frustum-culled, and LOD-aware, with no Transform hierarchy duplicated. " +
                    "The source may be a mesh, a group, or a composition containing Braze Cloners — its cloners and " +
                    "effectors stay live in the copy. Move, rotate, and scale this object to place the instance.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "rendering", "RENDERING"))
            {
                BrazeEditorGUI.Slider(P("lodBias"), 0.1f, 4f, new GUIContent("LOD Bias"));
                BrazeEditorGUI.PropertyField(P("maxDrawDistance"));
                BrazeEditorGUI.PropertyField(P("boundsPadding"));
                BrazeEditorGUI.PropertyField(P("shadowCasting"));
                BrazeEditorGUI.ToggleButton("Receive Shadows", P("receiveShadows"));
                BrazeEditorGUI.ToggleButton("Scene View", P("renderInSceneView"));
            }
            BrazeEditorGUI.EndSection();

            if (targets.Length == 1 && target is BrazeInstance instance)
            {
                if (BrazeEditorGUI.Section(K + "status", "STATUS"))
                {
                    EditorGUILayout.HelpBox(instance.Status,
                        instance.source != null ? MessageType.Info : MessageType.Warning);
                    if (!SystemInfo.supportsComputeShaders)
                        EditorGUILayout.HelpBox("This platform does not report compute-shader support.", MessageType.Error);
                }
                BrazeEditorGUI.EndSection();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
