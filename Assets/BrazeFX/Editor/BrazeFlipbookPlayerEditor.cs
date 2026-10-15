using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeFlipbookPlayer))]
    public sealed class BrazeFlipbookPlayerEditor : Editor
    {
        const string K = "BrazeFX.FlipbookPlayer.";
        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var player = (BrazeFlipbookPlayer)target;
            BrazeEditorGUI.BrandHeader("FLIPBOOK PLAYER");

            BrazeFlipbookPreviewController.DrawInspector(player,
                Application.isPlaying ? "Runtime Player" : "Scene Preview");

            if (BrazeEditorGUI.Section(K + "Atlas", "ATLAS"))
            {
                DrawChildren(P("atlas"));
                BrazeEditorGUI.EndSection();
            }

            if (BrazeEditorGUI.Section(K + "Playback", "PLAYBACK"))
            {
                BrazeEditorGUI.ToggleButton("Play On Enable", P("playOnEnable"));
                BrazeEditorGUI.PropertyField(P("timeScale"), new GUIContent("Time Scale"));
                DrawChildren(P("playback"));
                BrazeEditorGUI.EndSection();
            }

            if (BrazeEditorGUI.Section(K + "Look", "LOOK"))
            {
                DrawChildren(P("look"));
                BrazeEditorGUI.EndSection();
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                BrazeFlipbookPlayer.ValidateSettings(player.playback, player.atlas, player.look);
                player.EditorPropertiesChanged();
                if (BrazeFlipbookPreviewController.IsActive(player))
                    BrazeFlipbookPreviewController.SetTime(player, BrazeFlipbookPreviewController.Time(player));
                EditorUtility.SetDirty(player);
                SceneView.RepaintAll();
            }
        }

        static void DrawChildren(SerializedProperty parent)
        {
            if (parent == null) return;
            var child = parent.Copy();
            var end = child.GetEndProperty();
            bool enterChildren = true;
            while (child.NextVisible(enterChildren) && !SerializedProperty.EqualContents(child, end))
            {
                BrazeEditorGUI.PropertyField(child, includeChildren: true);
                enterChildren = false;
            }
        }

        void OnSceneGUI()
        {
            if (target is not BrazeFlipbookPlayer player || player == null) return;
            BrazeFlipbookScenePreview.DrawGuide(player);
            BrazeFlipbookPreviewController.DrawSceneControls(player);
        }
    }
}
