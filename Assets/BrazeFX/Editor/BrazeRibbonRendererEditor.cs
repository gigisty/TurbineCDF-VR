using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeRibbonRenderer))]
    [CanEditMultipleObjects]
    public sealed class BrazeRibbonRendererEditor : Editor
    {
        const string K = "BrazeFX.Ribbon.";
        static readonly Color PointFill = new Color(1f, 0.45f, 0.14f, 1f);
        static readonly Color PointEdge = new Color(0.12f, 0.07f, 0.04f, 0.85f);
        static readonly Color SelFill = new Color(0.35f, 0.85f, 1f, 1f);
        static readonly Color SelEdge = new Color(0.05f, 0.18f, 0.25f, 1f);
        static readonly Color LineColor = new Color(1f, 0.55f, 0.18f, 0.85f);
        const float PointHandleScale = 0.14f;
        const float SelectedPointHandleScale = 0.16f;
        const float PickRadiusScale = 1.9f;

        int selectedPoint;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("RIBBON RENDERER", "renderer");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                var preview = P("preview");
                BrazeEditorGUI.ToggleButton("Preview", preview);
                using (new EditorGUI.DisabledScope(!preview.boolValue))
                    BrazeEditorGUI.PropertyField(P("previewLength"), new GUIContent("Preview Length"));
                BrazeEditorGUI.PropertyField(P("localPoints"), new GUIContent("Local Points"), true);
                BrazeEditorGUI.IntSlider(P("historySamples"), 2, 128, new GUIContent("Samples"));
                BrazeEditorGUI.Slider(P("ribbonTime"), 0.02f, 2f, new GUIContent("Trail Length (s)"));
                BrazeEditorGUI.PropertyField(P("widthScale"), new GUIContent("Width Scale"));
                BrazeEditorGUI.PropertyField(P("widthOverTime"), new GUIContent("Width Over Time"));
                BrazeEditorGUI.Hint("Parent this to the sword and set Local Points to the blade profile, usually " +
                                    "base and tip. The component records those points over time and renders the swept " +
                                    "surface, so it follows the weapon instead of particles. Select the object and drag " +
                                    "the point handles in the Scene view to place the profile physically. Preview is an " +
                                    "edit-mode card for tuning fire and colour; Preview Length 0 auto-sizes from the profile.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shading", "SHADING"))
            {
                var colorMode = P("colorMode");
                BrazeEditorGUI.PillsEnum("Color Mode", colorMode);
                switch ((BrazeRibbonRenderer.ColorMode)colorMode.enumValueIndex)
                {
                    case BrazeRibbonRenderer.ColorMode.FireSmoke:
                        BrazeEditorGUI.PropertyField(P("fireColor"), new GUIContent("Fire Color"));
                        BrazeEditorGUI.PropertyField(P("smokeColor"), new GUIContent("Smoke Color"));
                        BrazeEditorGUI.Slider(P("colorMid"), 0f, 1f, new GUIContent("Fire -> Smoke"));
                        break;
                    case BrazeRibbonRenderer.ColorMode.Gradient:
                        BrazeEditorGUI.PropertyField(P("ramp"), new GUIContent("Ramp"));
                        break;
                    case BrazeRibbonRenderer.ColorMode.Constant:
                        BrazeEditorGUI.PropertyField(P("constantColor"), new GUIContent("Color"));
                        break;
                }

                BrazeEditorGUI.PropertyField(P("emissive"), new GUIContent("Emissive"));
                BrazeEditorGUI.Slider(P("opacity"), 0f, 1f, new GUIContent("Opacity"));
                BrazeEditorGUI.PropertyField(P("lengthAlpha"), new GUIContent("Length Fade"));
                BrazeEditorGUI.Slider(P("softEdge"), 0f, 0.5f, new GUIContent("Soft Edge"));
                BrazeEditorGUI.PillsEnum("Blend", P("blendMode"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "flame", "FLAME"))
            {
                var fireAmt = P("fireAmount");
                BrazeEditorGUI.Slider(fireAmt, 0f, 1f, new GUIContent("Amount"));
                using (new EditorGUI.DisabledScope(fireAmt.floatValue <= 1e-4f))
                {
                    BrazeEditorGUI.Slider(P("fireScale"), 0.25f, 16f, new GUIContent("Scale"));
                    BrazeEditorGUI.Slider(P("fireSpeed"), 0f, 8f, new GUIContent("Speed"));
                    BrazeEditorGUI.IntSlider(P("fireDetail"), 1, 5, new GUIContent("Detail"));
                    BrazeEditorGUI.Slider(P("fireSharpness"), 0.2f, 4f, new GUIContent("Sharpness"));
                    BrazeEditorGUI.Slider(P("fireEdge"), 0f, 1f, new GUIContent("Edge Erosion"));
                    BrazeEditorGUI.Slider(P("fireHeat"), 0f, 4f, new GUIContent("Heat"));
                }
            }
            BrazeEditorGUI.EndSection();

            if (GUILayout.Button("Reset History"))
            {
                foreach (Object o in targets)
                    ((BrazeRibbonRenderer)o).ResetHistory();
            }

            bool previewing = P("preview").boolValue;
            serializedObject.ApplyModifiedProperties();

            if (previewing && !Application.isPlaying)
            {
                SceneView.RepaintAll();
                Repaint();
            }
        }

        void OnSceneGUI()
        {
            var ribbon = (BrazeRibbonRenderer)target;
            if (ribbon == null || ribbon.localPoints == null || ribbon.localPoints.Count == 0) return;

            var t = ribbon.transform;
            selectedPoint = Mathf.Clamp(selectedPoint, 0, ribbon.localPoints.Count - 1);

            DrawProfile(ribbon, t);
            DrawPointButtons(ribbon, t);
            DrawSelectedPointHandle(ribbon, t);
        }

        void DrawProfile(BrazeRibbonRenderer ribbon, Transform t)
        {
            Handles.color = LineColor;
            for (int i = 1; i < ribbon.localPoints.Count; i++)
                Handles.DrawLine(t.TransformPoint(ribbon.localPoints[i - 1]), t.TransformPoint(ribbon.localPoints[i]), 2f);
        }

        void DrawPointButtons(BrazeRibbonRenderer ribbon, Transform t)
        {
            for (int i = 0; i < ribbon.localPoints.Count; i++)
            {
                if (i == selectedPoint) continue;

                Vector3 world = t.TransformPoint(ribbon.localPoints[i]);
                float size = HandleUtility.GetHandleSize(world) * PointHandleScale;
                Handles.color = PointFill;

                if (Handles.Button(world, Quaternion.identity, size, size * PickRadiusScale, PointCap))
                {
                    selectedPoint = i;
                    Repaint();
                }
            }
        }

        void DrawSelectedPointHandle(BrazeRibbonRenderer ribbon, Transform t)
        {
            if (selectedPoint < 0 || selectedPoint >= ribbon.localPoints.Count) return;

            Vector3 world = t.TransformPoint(ribbon.localPoints[selectedPoint]);
            Handles.color = SelFill;
            SelectedPointCap(0, world, Quaternion.identity, HandleUtility.GetHandleSize(world) * SelectedPointHandleScale, EventType.Repaint);

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(world, t.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ribbon, "Move Ribbon Point");
                ribbon.localPoints[selectedPoint] = t.InverseTransformPoint(moved);
                ribbon.ResetHistory();
                EditorUtility.SetDirty(ribbon);
            }
        }

        static void PointCap(int id, Vector3 pos, Quaternion rot, float size, EventType ev)
        {
            if (ev == EventType.Repaint)
            {
                Color c = Handles.color;
                Handles.SphereHandleCap(id, pos, rot, size, ev);
                Handles.color = PointEdge;
                Handles.DrawWireDisc(pos, SceneNormal(), size * 0.72f, 3f);
                Handles.color = c;
            }
            else if (ev == EventType.Layout)
            {
                HandleUtility.AddControl(id, HandleUtility.DistanceToCircle(pos, size));
            }
        }

        static void SelectedPointCap(int id, Vector3 pos, Quaternion rot, float size, EventType ev)
        {
            if (ev == EventType.Repaint)
            {
                Color c = Handles.color;
                Handles.SphereHandleCap(id, pos, rot, size, ev);
                Handles.color = SelEdge;
                Handles.DrawWireDisc(pos, SceneNormal(), size * 0.78f, 4.5f);
                Handles.color = c;
            }
            else if (ev == EventType.Layout)
            {
                HandleUtility.AddControl(id, HandleUtility.DistanceToCircle(pos, size));
            }
        }

        static Vector3 SceneNormal()
        {
            var view = SceneView.currentDrawingSceneView;
            if (view != null && view.camera != null) return view.camera.transform.forward;
            return Vector3.forward;
        }
    }
}
