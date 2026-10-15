using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeFractureBake))]
    public sealed class BrazeFractureBakeEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var bake = (BrazeFractureBake)target;

            EditorGUILayout.LabelField("Braze Fracture Bake", BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel));
            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Valid", bake.HasData);
                EditorGUILayout.Toggle("Blob Payload", bake.HasBlob);
                EditorGUILayout.ObjectField("Blob", bake.blob, typeof(TextAsset), false);
                EditorGUILayout.IntField("Blob Version", bake.blobVersion);
                EditorGUILayout.IntField("Fragments", bake.fragmentCount);
                EditorGUILayout.IntField("Seeds", bake.SeedCount);
                EditorGUILayout.IntField("Vertices", bake.VertexCount);
                EditorGUILayout.IntField("Triangles", bake.TriangleCount);
                EditorGUILayout.IntField("Neighbour Links", bake.NeighborLinkCount);
                EditorGUILayout.TextField("Signature", bake.signature ?? "");
                EditorGUILayout.TextField("Payload Size", FormatBytes(bake.PayloadBytes));
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                bake.HasBlob
                    ? "Heavy fracture data lives in the referenced .bytes blob. This .asset only stores metadata."
                    : "No valid blob payload is assigned. Re-bake this fracture to create the .bytes payload.",
                bake.HasBlob ? MessageType.Info : MessageType.Warning);
        }

        static string FormatBytes(long bytes)
        {
            const double kb = 1024.0;
            const double mb = kb * 1024.0;
            const double gb = mb * 1024.0;
            if (bytes >= gb) return $"{bytes / gb:0.##} GB";
            if (bytes >= mb) return $"{bytes / mb:0.##} MB";
            if (bytes >= kb) return $"{bytes / kb:0.##} KB";
            return $"{bytes} B";
        }
    }
}
