using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Authoring inspector for the spline-lathed fluid vortex.</summary>
    [CustomEditor(typeof(BrazeSplineVortex))]
    [CanEditMultipleObjects]
    public sealed class BrazeSplineVortexEditor : Editor
    {
        const string K = "BrazeFX.SplineVortex.";

        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("SPLINE VORTEX", "spline");

            if (BrazeEditorGUI.Section(K + "path", "PATH"))
            {
                BrazeEditorGUI.PropertyField(P("spline"), new GUIContent("Spline"));
                BrazeEditorGUI.ToggleButton("Reverse Tail / Eye", P("reversePath"));
                BrazeEditorGUI.IntSlider(P("pathSamples"), 8, 64, new GUIContent("Path Samples"));
                BrazeEditorGUI.Hint("The spline is the vortex centreline. Positive Flow travels from tail to eye. " +
                                    "24 samples is smooth for most funnels; raise it only for tight bends because " +
                                    "segment count is the component's main GPU cost.");
                if (targets.Length == 1 && ((BrazeSplineVortex)target).ResolvedSpline == null)
                    BrazeEditorGUI.Hint("Assign a Braze Spline, or add one to this GameObject, before simulation.");
                else if (targets.Length == 1 && ((BrazeSplineVortex)target).ResolvedSpline.closed)
                    BrazeEditorGUI.Hint("Tail and eye are clearest on an open spline. A closed spline works, but the " +
                                        "profile joins at a visible tail/eye seam.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "profile", "LATHE PROFILE"))
            {
                BrazeEditorGUI.PropertyField(P("tailRadius"), new GUIContent("Tail Radius"));
                BrazeEditorGUI.PropertyField(P("bodyRadius"), new GUIContent("Body Radius"));
                BrazeEditorGUI.PropertyField(P("eyeRadius"), new GUIContent("Eye Radius"));
                BrazeEditorGUI.Slider(P("tailLength"), 0.01f, 1f, new GUIContent("Tail Length"));
                BrazeEditorGUI.Slider(P("eyeLength"), 0.01f, 1f, new GUIContent("Eye Length"));
                BrazeEditorGUI.PropertyField(P("radiusProfile"), new GUIContent("Radius Profile"));
                BrazeEditorGUI.PropertyField(P("captureMargin"), new GUIContent("Capture Margin"));
                BrazeEditorGUI.PillsEnum("Conform To", P("conformMode"));
                if (P("conformMode").hasMultipleDifferentValues ||
                    P("conformMode").enumValueIndex == (int)BrazeSplineVortex.ConformMode.Wall)
                    BrazeEditorGUI.PropertyField(P("wallThickness"), new GUIContent("Wall Thickness"));
                BrazeEditorGUI.Hint("Tail, body and eye make the base funnel. Radius Profile multiplies it from " +
                                    "tail (left) to eye (right), so dips make a waist and peaks make bulges. Volume " +
                                    "fills the silhouette; Wall pulls smoke/fire into a hollow lathed shell.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "motion", "MOTION"))
            {
                BrazeEditorGUI.Slider(P("spinSpeed"), -10f, 10f, new GUIContent("Spin Speed (turns/s)"));
                BrazeEditorGUI.Slider(P("spinGrip"), 0f, 1f, new GUIContent("Spin Grip"));
                BrazeEditorGUI.Slider(P("flowSpeed"), -30f, 30f, new GUIContent("Flow Speed"));
                BrazeEditorGUI.Slider(P("flowGrip"), 0f, 1f, new GUIContent("Flow Response"));
                BrazeEditorGUI.Slider(P("conform"), 0f, 1f, new GUIContent("Conform"));
                BrazeEditorGUI.Slider(P("containment"), 0f, 1f, new GUIContent("Containment"));
                BrazeEditorGUI.Hint("Conform is the primary, calm shape authority: it removes inherited radial drift " +
                                    "and excess curls before applying the requested motion. Flow Speed is a target, " +
                                    "not an accumulating force; tight bends automatically use a stable local speed. " +
                                    "Global curls, turbulence and spiral detail remain bounded secondary motion.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "ends", "EYE & TAIL"))
            {
                BrazeEditorGUI.Slider(P("eyeFlow"), 0f, 1f, new GUIContent("Eye Flow"));
                BrazeEditorGUI.Slider(P("eyeOpening"), 0f, 1f, new GUIContent("Eye Opening"));
                BrazeEditorGUI.PropertyField(P("eyeOutflow"), new GUIContent("Eye Outflow"));
                BrazeEditorGUI.PropertyField(P("tailSuction"), new GUIContent("Tail Suction"));
                BrazeEditorGUI.Hint("Eye Flow smoothly reduces the main axial speed across Eye Length (0 = calm " +
                                    "terminal eye, 1 = full body flow). Eye Opening defines the central channel; " +
                                    "Eye Outflow is its maximum outward speed, so it cannot explosively overshoot. " +
                                    "Tail Suction narrows and feeds the opposite end into the funnel.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "detail", "SPIRAL DETAIL"))
            {
                BrazeEditorGUI.IntSlider(P("spiralArms"), 0, 8, new GUIContent("Spiral Arms"));
                if (P("spiralArms").hasMultipleDifferentValues || P("spiralArms").intValue > 0)
                {
                    BrazeEditorGUI.Slider(P("armStrength"), 0f, 1f, new GUIContent("Arm Strength"));
                    BrazeEditorGUI.Slider(P("armTwist"), -8f, 8f, new GUIContent("Arm Twist"));
                }
                BrazeEditorGUI.Slider(P("turbulence"), 0f, 1f, new GUIContent("Turbulence"));
                if (P("turbulence").hasMultipleDifferentValues || P("turbulence").floatValue > 0f)
                    BrazeEditorGUI.PropertyField(P("turbulenceScale"), new GUIContent("Turbulence Size"));
                BrazeEditorGUI.Hint("Optional secondary detail. Arms and turbulence are bounded target velocities, " +
                                    "never accumulating forces. Their radial motion is almost removed at high " +
                                    "Conform, while a small axial/tangential shimmer can remain visible.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "preview", "PREVIEW", false))
            {
                BrazeEditorGUI.ToggleButton("Scene Gizmo", P("showGizmo"));
                BrazeEditorGUI.Hint("Orange is the tail, blue is the eye. Selected gizmos also show the outer capture margin.");
            }
            BrazeEditorGUI.EndSection();

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed) SceneView.RepaintAll();
        }
    }
}
