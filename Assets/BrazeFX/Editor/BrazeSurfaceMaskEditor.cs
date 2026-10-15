using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Branded IMGUI inspector for <see cref="BrazeSurfaceMask"/> — matches the DCC/Cinema4D theme of the
    /// other BrazeFX components.</summary>
    [CustomEditor(typeof(BrazeSurfaceMask))]
    [CanEditMultipleObjects]
    public sealed class BrazeSurfaceMaskEditor : Editor
    {
        const string K = "BrazeFX.SurfaceMask.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("SURFACE MASK", "surface");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);
            var mask = target as BrazeSurfaceMask;

            // ── MASK ──────────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "mask", "MASK"))
            {
                BrazeEditorGUI.SubHeader("SPACE");
                BrazeEditorGUI.PropertyField(P("space"), new GUIContent("Space"));
                BrazeEditorGUI.Hint(P("space").enumValueIndex == (int)BrazeSurfaceMask.MaskSpace.Local
                    ? "Local = region + pattern are locked to Braze mask space: object-oriented world metres, not mesh UVs and not raw scaled object space. It rides with the object without stretching under non-uniform Transform scale."
                    : "World = region lives in world space (legacy). The pattern swims when the object moves and does NOT follow skinned deformation. Reach/Softness are in world metres.");
                BrazeEditorGUI.PropertyField(P("projectionMode"), new GUIContent("Projection"));
                if (P("projectionMode").enumValueIndex == (int)BrazeSurfaceMask.ProjectionMode.Planar2D)
                    BrazeEditorGUI.PropertyField(P("projectionPlane"), new GUIContent("Plane"));
                BrazeEditorGUI.Hint(P("projectionMode").enumValueIndex == (int)BrazeSurfaceMask.ProjectionMode.Planar2D
                    ? "Planar2D is projector/decal style: the seed is measured on a uniform mask plane and projected onto whatever surface is below it. This avoids mesh UVs, mesh density, and transform stretching."
                    : "Volume3D is the legacy spherical influence field: the seed affects any sampled point inside its 3D radius.");

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubHeader("SEEDS");
                BrazeEditorGUI.ObjectDropList(P("seeds"),
                    "＋   Drop a seed object here", "＋   Drop another seed",
                    o => (o as Transform) ?? (o as GameObject)?.transform);
                if (P("seeds").arraySize == 0)
                    EditorGUILayout.HelpBox("Drop empties or objects placed on/near the surface. Each seed is measured in the mask's projection space; the mask is their union and lands on whatever geometry is sampled below it. On a skinned mesh (Local space) a seed sticks to the nearest surface vertex, gluing it to that body part through animation.", MessageType.Info);
                // Skinned binding is automatic + GPU-resident: a seed binds to its nearest surface vertex (on enable,
                // and live as you drag), then the whole field/preview run on the GPU at that vertex's rest position.
                if (mask != null && mask.IsLocalSpace && mask.GetComponent<SkinnedMeshRenderer>() != null)
                    BrazeEditorGUI.Hint("On a skinned mesh, each seed snaps to the nearest surface vertex (place seeds with the character near its rest pose). The masked region then stays glued to that body part through animation — evaluated entirely on the GPU, with the live region shown by the preview overlay.");
                if (P("seeds").arraySize > 0)
                    BrazeEditorGUI.ToggleButton("Hide Seeds In Play", P("hideSeedsInPlay"));

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubHeader("REGION");
                BrazeEditorGUI.Slider(P("reach"), 0f, 30f, new GUIContent("Reach (m)"));
                BrazeEditorGUI.Slider(P("softness"), 0.01f, 5f, new GUIContent("Softness (m)"));
                BrazeEditorGUI.Hint("Each seed is a point in the mask's uniform coordinate system. Reach is the radius around it and Softness feathers the edge. The same field is what the preview shows and what consumers sample.");

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubHeader("PREVIEW");
                BrazeEditorGUI.ToggleButton("Show Preview", P("showPreview"));
                BrazeEditorGUI.ToggleButton("Invert", P("invert"));
                BrazeEditorGUI.ToggleButton("Debug Mask", P("debugPreview"));
                if (P("showPreview").boolValue)
                {
                    BrazeEditorGUI.ToggleButton("Render In Play", P("showPreviewInPlay"));
                    BrazeEditorGUI.PropertyField(P("previewStyle"), new GUIContent("Style"));
                    var style = (BrazeSurfaceMask.PreviewStyle)P("previewStyle").enumValueIndex;
                    if (style != BrazeSurfaceMask.PreviewStyle.Grayscale)
                    {
                        BrazeEditorGUI.PropertyField(P("previewColor"), new GUIContent(style == BrazeSurfaceMask.PreviewStyle.Texture ? "Tint" : "Color"));
                    }
                    if (style == BrazeSurfaceMask.PreviewStyle.Texture)
                    {
                        BrazeEditorGUI.PropertyField(P("previewTexture"), new GUIContent("Texture"));
                        BrazeEditorGUI.PropertyField(P("previewTextureScale"), new GUIContent("Tiling"));
                        BrazeEditorGUI.PropertyField(P("previewTextureOffset"), new GUIContent("Offset"));
                        BrazeEditorGUI.Slider(P("previewTextureStrength"), 0f, 1f, new GUIContent("Texture Mix"));
                    }
                    BrazeEditorGUI.Slider(P("previewOpacity"), 0f, 1f, new GUIContent("Preview Opacity"));
                    BrazeEditorGUI.Slider(P("surfaceOffset"), 0f, 0.1f, new GUIContent("Surface Offset (m)"));
                }
                if (mask != null && (P("showPreview").boolValue || P("debugPreview").boolValue))
                    EditorGUILayout.HelpBox(mask.DebugReport, mask.HasMask ? MessageType.Info : MessageType.Warning);

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubHeader("SAMPLING");
                BrazeEditorGUI.Slider(P("surfacePointSpacing"), 0.005f, 1f, new GUIContent("Surface Spacing"));
                BrazeEditorGUI.IntSlider(P("maxSurfacePoints"), 16, 50000, new GUIContent("Max Points"));
                BrazeEditorGUI.Hint("Consumers use these points when spawning particles or pyro from this mask. The point set is generated by surface area, not by mesh vertices, so low-poly and high-poly sources use the same mask resolution.");
            }
            BrazeEditorGUI.EndSection();

            // ── GROWTH ────────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "growth", "GROWTH"))
            {
                BrazeEditorGUI.ToggleButton("Animate Growth", P("growth"));
                if (P("growth").boolValue)
                {
                    BrazeEditorGUI.ToggleButton("Play On Enable", P("playOnEnable"));
                    BrazeEditorGUI.Slider(P("growthFrom"), 0f, 30f, new GUIContent("Reach Start (m)"));
                    BrazeEditorGUI.Slider(P("growthTo"), 0f, 30f, new GUIContent("Reach End (m)"));
                    BrazeEditorGUI.Slider(P("growthDuration"), 0.01f, 10f, new GUIContent("Duration (s)"));
                    BrazeEditorGUI.PropertyField(P("growthCurve"), new GUIContent("Curve"));
                    using (new EditorGUI.DisabledScope(!Application.isPlaying))
                        if (GUILayout.Button(Application.isPlaying ? "Trigger" : "Trigger (enter Play mode)"))
                            foreach (var t in targets) (t as BrazeSurfaceMask)?.Trigger();
                    BrazeEditorGUI.Hint("PLAY MODE ONLY — in edit the preview always shows the static MASK Reach, unchanged. On Play (or Trigger()) the region animates Reach Start → Reach End over Duration along Curve, then HOLDS at Reach End.");
                }
                else BrazeEditorGUI.Hint("Off = the region sits at the MASK Reach. Turn on for a Play-mode Start→End animation (nothing changes in edit mode).");
            }
            BrazeEditorGUI.EndSection();

            // ── FILTERS ───────────────────────────────────────────────────────────────
            if (BrazeEditorGUI.Section(K + "filters", "FILTERS"))
            {
                BrazeEditorGUI.Hint("Each filter is an independent on/off card — flip one on to tune it in isolation. Fixed pipeline (anisotropy → organic → rings → erosion → contrast), mirrored by the preview shader and GPU gate so the preview equals what consumers sample.");

                FilterCard("organicEnabled", "ORGANIC — bacterial edge", () =>
                {
                    BrazeEditorGUI.Slider(P("organicAmount"), 0f, 1f, new GUIContent("Amount"));
                    BrazeEditorGUI.Slider(P("organicScale"), 0.05f, 5f, new GUIContent("Scale"));
                    BrazeEditorGUI.Slider(P("organicSpeed"), 0f, 5f, new GUIContent("Boil"));
                });

                FilterCard("anisotropyEnabled", "ANISOTROPY — stretch", () =>
                {
                    BrazeEditorGUI.Slider(P("anisotropyAmount"), 0f, 4f, new GUIContent("Stretch"));
                    BrazeEditorGUI.PropertyField(P("anisotropyAxis"), new GUIContent("Axis"));
                });

                FilterCard("ringsEnabled", "RINGS — ripple", () =>
                {
                    BrazeEditorGUI.Slider(P("ringAmount"), 0f, 1f, new GUIContent("Amount"));
                    BrazeEditorGUI.Slider(P("ringSpacing"), 0.02f, 5f, new GUIContent("Spacing (m)"));
                    BrazeEditorGUI.Slider(P("ringThickness"), 0.02f, 1f, new GUIContent("Thickness"));
                });

                FilterCard("erodeEnabled", "EROSION — dissolve holes", () =>
                {
                    BrazeEditorGUI.Slider(P("erodeAmount"), 0f, 1f, new GUIContent("Amount"));
                    BrazeEditorGUI.Slider(P("erodeScale"), 0.1f, 10f, new GUIContent("Hole Density"));
                });

                FilterCard("contrastEnabled", "CONTRAST / RESPONSE", () =>
                {
                    BrazeEditorGUI.Slider(P("contrast"), 0f, 4f, new GUIContent("Contrast"));
                    BrazeEditorGUI.Slider(P("bias"), -1f, 1f, new GUIContent("Bias"));
                });
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        // One filter = a separated on/off card: a toggle header, and its sliders (indented) only when enabled.
        void FilterCard(string enabledProp, string title, System.Action body)
        {
            var p = serializedObject.FindProperty(enabledProp);
            BrazeEditorGUI.Separator();
            BrazeEditorGUI.ToggleButton(title, p);
            if (p.boolValue)
            {
                EditorGUI.indentLevel++;
                body();
                EditorGUI.indentLevel--;
            }
        }
    }
}
