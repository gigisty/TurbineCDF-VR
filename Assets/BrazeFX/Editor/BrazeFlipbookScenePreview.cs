using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX.EditorTools
{
    /// <summary>Persistent authoring bounds and stopped-state atlas reference surface.</summary>
    internal static class BrazeFlipbookScenePreview
    {
        static Material referenceMaterial;

        public static void DrawGuide(BrazeFlipbookPlayer player)
        {
            if (player == null || !player.isActiveAndEnabled || Application.isPlaying ||
                Event.current.type != EventType.Repaint) return;

            Transform t = player.transform;
            Quaternion rotation = player.look != null && player.look.facing == BrazeFlipbookPlayer.Facing.World
                ? Quaternion.identity
                : t.rotation;
            Vector3 scale = t.lossyScale;
            Matrix4x4 matrix = Matrix4x4.TRS(t.position, rotation,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            Mesh shapeMesh = BrazeFlipbookMeshLibrary.Get(player.look != null
                ? player.look.renderShape
                : BrazeFlipbookPlayer.RenderShape.Quad);

            if (!BrazeFlipbookPreviewController.IsActive(player)) DrawReferenceSurface(player, shapeMesh, matrix);

            Color oldColor = Handles.color;
            CompareFunction oldDepth = Handles.zTest;
            Matrix4x4 oldMatrix = Handles.matrix;
            Handles.color = new Color(1f, 0.45f, 0.14f, 1f);
            Handles.zTest = CompareFunction.LessEqual;
            Handles.matrix = matrix;
            Handles.DrawWireCube(shapeMesh.bounds.center, shapeMesh.bounds.size);
            Handles.matrix = oldMatrix;
            Handles.zTest = oldDepth;
            Handles.color = oldColor;
        }

        static void DrawReferenceSurface(BrazeFlipbookPlayer player, Mesh shapeMesh, Matrix4x4 matrix)
        {
            if (player.atlas == null || player.playback == null || player.atlas.texture == null ||
                !EnsureResources()) return;
            int columns = Mathf.Max(1, player.atlas.columns);
            int rows = Mathf.Max(1, player.atlas.rows);
            int frameCount = Mathf.Max(1, columns * rows);
            int frame = Mathf.Clamp(player.playback.startFrame, 0, frameCount - 1);
            int column = frame % columns;
            int textureRow = rows - 1 - frame / columns;
            Texture texture = player.atlas.texture;
            float insetX = 0.5f / Mathf.Max(1, texture.width);
            float insetY = 0.5f / Mathf.Max(1, texture.height);
            referenceMaterial.SetTexture("_MainTex", texture);
            referenceMaterial.SetVector("_FrameRect", new Vector4(
                1f / columns - insetX * 2f,
                1f / rows - insetY * 2f,
                column / (float)columns + insetX,
                textureRow / (float)rows + insetY));
            if (referenceMaterial.SetPass(0)) Graphics.DrawMeshNow(shapeMesh, matrix);
        }

        static bool EnsureResources()
        {
            if (referenceMaterial != null) return true;
            Shader shader = Shader.Find("Hidden/BrazeFX/FlipbookReference");
            if (shader == null) return false;
            referenceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }
    }
}
