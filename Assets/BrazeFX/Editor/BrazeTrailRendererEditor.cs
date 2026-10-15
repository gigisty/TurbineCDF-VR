using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeTrailRenderer))]
    [CanEditMultipleObjects]
    public sealed class BrazeTrailRendererEditor : Editor
    {
        const string K = "BrazeFX.Trail.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("TRAIL RENDERER", "trail");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "source", "SOURCE"))
            {
                var sourceType = P("sourceType");
                BrazeEditorGUI.PillsEnum("Type", sourceType);
                bool clonerSource = !sourceType.hasMultipleDifferentValues &&
                    (BrazeTrailRenderer.SourceType)sourceType.enumValueIndex == BrazeTrailRenderer.SourceType.Cloner;
                if (clonerSource)
                    BrazeEditorGUI.PropertyField(P("cloner"), new GUIContent("Cloner"));
                else
                    BrazeEditorGUI.PropertyField(P("particles"), new GUIContent("Particle System"));

                BrazeEditorGUI.Slider(P("trailTime"), 0.01f, 8f, new GUIContent("Trail Time (s)"));
                var detail = P("trailDetail");
                BrazeEditorGUI.Slider(detail, 0f, 1f, new GUIContent("Trail Detail"));
                if (!detail.hasMultipleDifferentValues)
                {
                    int renderSegments = BrazeTrailRenderer.ResolveRenderSegments(detail.floatValue);
                    int historySamples = BrazeTrailRenderer.ResolveHistorySamples(detail.floatValue);
                    BrazeEditorGUI.SubLabel($"{renderSegments} ribbon segments / {historySamples} path samples");
                }
                var trackedRatio = P("trackedParticleRatio");
                BrazeEditorGUI.Slider(trackedRatio, 0f, 1f,
                    new GUIContent(clonerSource ? "Tracked Clones" : "Tracked Particles"));

                if (clonerSource && !trackedRatio.hasMultipleDifferentValues && !P("cloner").hasMultipleDifferentValues &&
                    P("cloner").objectReferenceValue is BrazeCloner cloner)
                {
                    int total = cloner.InstanceCount;
                    int tracked = BrazeTrailRenderer.ResolveTrackedCount(total, trackedRatio.floatValue);
                    BrazeEditorGUI.SubLabel($"{tracked:n0} of {total:n0} clones record GPU history");
                }
                else if (!clonerSource && !trackedRatio.hasMultipleDifferentValues &&
                         !P("particles").hasMultipleDifferentValues &&
                         P("particles").objectReferenceValue is BrazeParticleSystem particles)
                {
                    int total = particles.ParticleCapacity > 0 ? particles.ParticleCapacity : particles.capacity;
                    int tracked = BrazeTrailRenderer.ResolveTrackedCount(total, trackedRatio.floatValue);
                    BrazeEditorGUI.SubLabel($"{tracked:n0} of {total:n0} particle slots allocate GPU history");
                }

                BrazeEditorGUI.ToggleButton("GPU Frustum Culling", P("frustumCull"));

                BrazeEditorGUI.Hint(clonerSource
                    ? "Cloner distribution and effector positions stay on the GPU. Tracked Clones also controls history allocation, so lower values scale efficiently for very large cloners."
                    : "The source's compact alive list drives indirect GPU work. Tracked Particles controls history/node allocation; camera culling removes complete off-screen ribbons before drawing.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                DrawWidthRange(P("width"), P("widthMax"));
                BrazeEditorGUI.PropertyField(P("widthCurve"), new GUIContent("Width Curve"));
                BrazeEditorGUI.Slider(P("pathSmoothing"), 0f, 1f, new GUIContent("Trail Smoothing"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shading", "SHADING"))
            {
                BrazeEditorGUI.SubLabel("COLOR");
                var colorModeProp = P("colorMode");
                BrazeEditorGUI.PillsEnum("Color Mode", colorModeProp);
                switch ((BrazeTrailRenderer.ColorMode)colorModeProp.enumValueIndex)
                {
                    case BrazeTrailRenderer.ColorMode.FireSmoke:
                        BrazeEditorGUI.PropertyField(P("fireColor"), new GUIContent("Fire Color"));
                        BrazeEditorGUI.PropertyField(P("smokeColor"), new GUIContent("Smoke Color"));
                        BrazeEditorGUI.Slider(P("colorMid"), 0f, 1f, new GUIContent("Fire Smoke"));
                        break;
                    case BrazeTrailRenderer.ColorMode.Gradient:
                        BrazeEditorGUI.PropertyField(P("ramp"), new GUIContent("Ramp"));
                        break;
                    case BrazeTrailRenderer.ColorMode.ByAge:
                        BrazeEditorGUI.PropertyField(P("ramp"), new GUIContent("Age Ramp"));
                        break;
                    case BrazeTrailRenderer.ColorMode.Constant:
                        BrazeEditorGUI.PropertyField(P("constantColor"), new GUIContent("Color"));
                        break;
                }

                BrazeEditorGUI.PropertyField(P("emissive"), new GUIContent("Emissive"));
                BrazeEditorGUI.Slider(P("opacity"), 0f, 1f, new GUIContent("Opacity"));
                BrazeEditorGUI.PropertyField(P("lengthAlpha"), new GUIContent("Length Fade"));
                BrazeEditorGUI.ToggleButton("Opacity Over Life", P("opacityOverLife"));
                using (new EditorGUI.DisabledScope(!P("opacityOverLife").boolValue))
                    BrazeEditorGUI.PropertyField(P("opacityLifeCurve"), new GUIContent("Life Fade"));
                BrazeEditorGUI.Slider(P("softEdge"), 0f, 0.5f, new GUIContent("Soft Edge"));
                BrazeEditorGUI.Slider(P("selfShadow"), 0f, 1f, new GUIContent("Self Shadow"));
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

            if (BrazeEditorGUI.Section(K + "smoke", "SMOKE"))
            {
                var smokeAmt = P("smokeAmount");
                BrazeEditorGUI.Slider(smokeAmt, 0f, 1f, new GUIContent("Amount"));
                using (new EditorGUI.DisabledScope(smokeAmt.floatValue <= 1e-4f))
                {
                    BrazeEditorGUI.PropertyField(P("smokeLayerColor"), new GUIContent("Color"));
                    BrazeEditorGUI.Slider(P("smokeScale"), 0.25f, 16f, new GUIContent("Scale"));
                    BrazeEditorGUI.Slider(P("smokeSpeed"), 0f, 4f, new GUIContent("Speed"));
                    BrazeEditorGUI.IntSlider(P("smokeDetail"), 1, 5, new GUIContent("Detail"));
                    BrazeEditorGUI.Slider(P("smokeDepth"), 0f, 1f, new GUIContent("Depth"));
                    BrazeEditorGUI.Slider(P("smokeEdge"), 0f, 1f, new GUIContent("Edge Erosion"));
                }
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawWidthRange(SerializedProperty minProp, SerializedProperty maxProp)
        {
            if (minProp.hasMultipleDifferentValues || maxProp.hasMultipleDifferentValues)
            {
                BrazeEditorGUI.PropertyField(minProp, new GUIContent("Width Min"));
                BrazeEditorGUI.PropertyField(maxProp, new GUIContent("Width Max"));
                return;
            }

            float min = Mathf.Max(0f, minProp.floatValue);
            float max = Mathf.Max(min, maxProp.floatValue);
            float sliderMax = Mathf.Max(2f, min, max);

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Width Range");
                min = BrazeEditorGUI.DelayedFloatField(min, GUILayout.Width(54));
                EditorGUILayout.MinMaxSlider(ref min, ref max, 0f, sliderMax);
                max = BrazeEditorGUI.DelayedFloatField(max, GUILayout.Width(54));
            }

            if (EditorGUI.EndChangeCheck())
            {
                min = Mathf.Max(0f, min);
                max = Mathf.Max(min, max);
                minProp.floatValue = min;
                maxProp.floatValue = max;
            }
        }
    }
}
