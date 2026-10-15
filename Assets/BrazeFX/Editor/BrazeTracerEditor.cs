using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeTracer))]
    [CanEditMultipleObjects]
    public sealed class BrazeTracerEditor : Editor
    {
        const string K = "BrazeFX.Tracer.";
        static readonly string[] ModeNames =
            { "Trace Paths", "Connect All", "Connect Elements", "Nearest Points", "Web" };
        static readonly int[] ModeValues =
        {
            (int)BrazeTracer.TracingMode.TracePaths,
            (int)BrazeTracer.TracingMode.ConnectAllObjects,
            (int)BrazeTracer.TracingMode.ConnectElements,
            (int)BrazeTracer.TracingMode.NearestPoints,
            (int)BrazeTracer.TracingMode.Web,
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("GPU TRACER", "tracer");
            SerializedProperty P(string name) => serializedObject.FindProperty(name);

            if (BrazeEditorGUI.Section(K + "sources", "SOURCES"))
            {
                BrazeEditorGUI.ObjectDropList(P("sources"),
                    "＋   Drop a Braze Cloner here", "＋   Drop another Cloner", ResolveCloner, reorderable: true);
                BrazeEditorGUI.Hint("Source order defines point order. Clone base positions and all effector offsets are read directly from each Cloner's GPU buffers.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "tracer", "TRACER"))
            {
                BrazeEditorGUI.PillsIntGrid("Mode", P("tracingMode"), ModeNames, ModeValues, 2);
                if ((BrazeTracer.TracingMode)P("tracingMode").enumValueIndex == BrazeTracer.TracingMode.TracePaths)
                    BrazeEditorGUI.ToggleButton("Trace Active", P("traceActive"));
                BrazeEditorGUI.PillsEnum("Space", P("space"));
                Pair(P("includeCloner"), "Include Cloner", P("reverseSequence"), "Reverse Sequence");
            }
            BrazeEditorGUI.EndSection();

            var mode = (BrazeTracer.TracingMode)P("tracingMode").enumValueIndex;
            if (mode == BrazeTracer.TracingMode.TracePaths)
            {
                if (BrazeEditorGUI.Section(K + "paths", "TRACE PATHS"))
                {
                    Pair(P("sampleStep"), "Sample Step", P("tracedCloneRatio"), "Traced Clones");
                    BrazeEditorGUI.PillsEnum("Limit", P("limit"));
                    if ((BrazeTracer.LimitMode)P("limit").enumValueIndex != BrazeTracer.LimitMode.None)
                        Pair(P("amount"), "Amount", P("variance"), "Variance");
                    BrazeEditorGUI.IntSlider(P("historyCapacity"), 2, 512, new GUIContent("History Capacity"));
                    BrazeEditorGUI.Hint("Sample Step is measured in rendered frames. Trace Active can be toggled to write clean gaps; From End drags a bounded trail behind every tracked clone.");
                }
                BrazeEditorGUI.EndSection();
            }
            else
            {
                if (BrazeEditorGUI.Section(K + "connections", "CONNECTIONS"))
                {
                    if (mode == BrazeTracer.TracingMode.ConnectAllObjects ||
                        mode == BrazeTracer.TracingMode.ConnectElements)
                        BrazeEditorGUI.ToggleButton("Closed", P("closed"));
                    BrazeEditorGUI.Slider(P("connectionRatio"), 0f, 1f, new GUIContent("Connection Ratio"));
                    BrazeEditorGUI.PropertyField(P("maximumLines"), new GUIContent("Maximum Lines"));

                    if (mode == BrazeTracer.TracingMode.Web)
                    {
                        BrazeEditorGUI.SubHeader("WEB TOPOLOGY");
                        Pair(P("linksPerClone"), "Links / Clone", P("indexStep"), "Index Step");
                        Pair(P("randomizeLinks"), "Random Partners", P("seed"), "Seed");
                        BrazeEditorGUI.PropertyField(P("maximumDistance"), new GUIContent("Maximum Distance"));
                    }
                    else if (mode == BrazeTracer.TracingMode.NearestPoints)
                    {
                        BrazeEditorGUI.SubHeader("NEAREST TOPOLOGY");
                        BrazeEditorGUI.PropertyField(P("maximumDistance"), new GUIContent("Maximum Distance"));
                    }

                    string behavior = mode switch
                    {
                        BrazeTracer.TracingMode.ConnectElements => "Each Cloner becomes an independent spline segment; sources are never bridged together.",
                        BrazeTracer.TracingMode.NearestPoints => "Every selected point connects to its exact closest neighbour across all Cloners. Connection Ratio controls how many source points participate; Maximum Distance can reject long links.",
                        BrazeTracer.TracingMode.Web => "Every clone creates multiple deterministic links inside its own Cloner. Use distance filtering and Connection Ratio to shape dense webs safely.",
                        _ => "Every clone from every source becomes one continuous ordered sequence. The end of one Cloner connects to the start of the next.",
                    };
                    BrazeEditorGUI.Hint(behavior);
                }
                BrazeEditorGUI.EndSection();
            }

            if (BrazeEditorGUI.Section(K + "spline", "SPLINE"))
            {
                BrazeEditorGUI.PillsEnum("Type", P("splineType"));
                if (mode == BrazeTracer.TracingMode.TracePaths)
                    BrazeEditorGUI.IntSlider(P("pathSegments"), 8, 64, new GUIContent("Path Detail"));
                else if ((BrazeTracer.SplineType)P("splineType").enumValueIndex != BrazeTracer.SplineType.Linear)
                    BrazeEditorGUI.IntSlider(P("connectionSubdivisions"), 1, 16, new GUIContent("Subdivisions"));
                BrazeEditorGUI.Hint("Linear is angular and exact. Cubic passes through clone points, Akima suppresses tight-bend overshoot, B-Spline makes a smooth approximation, and Bezier generates smooth local handles.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shape", "SHAPE"))
            {
                Pair(P("width"), "Width Min", P("widthMax"), "Width Max");
                BrazeEditorGUI.PropertyField(P("widthCurve"), new GUIContent("Width Along Line"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "shading", "SHADING"))
            {
                BrazeEditorGUI.PillsEnum("Colour", P("colorMode"));
                if ((BrazeTracer.ColorMode)P("colorMode").enumValueIndex == BrazeTracer.ColorMode.Gradient)
                    BrazeEditorGUI.PropertyField(P("gradient"), new GUIContent("Gradient"));
                else
                    BrazeEditorGUI.PropertyField(P("color"), new GUIContent("Colour"));
                Pair(P("glow"), "Glow", P("opacity"), "Opacity");
                BrazeEditorGUI.PropertyField(P("opacityCurve"), new GUIContent("Opacity Along Line"));
                BrazeEditorGUI.Slider(P("softEdge"), 0f, 0.5f, new GUIContent("Soft Edge"));
                BrazeEditorGUI.PillsEnum("Blend", P("blendMode"));
            }
            BrazeEditorGUI.EndSection();

            if (targets.Length == 1 && target is BrazeTracer tracer)
            {
                if (BrazeEditorGUI.Section(K + "output", "OUTPUT"))
                {
                    BrazeEditorGUI.SubLabel($"{tracer.GeneratedLines:n0} LINES  ·  {FormatBytes(tracer.GpuBytes)} GPU");
                    BrazeEditorGUI.Hint(tracer.Status);
                }
                BrazeEditorGUI.EndSection();
            }

            serializedObject.ApplyModifiedProperties();
        }

        static Object ResolveCloner(Object value)
        {
            if (value is BrazeCloner cloner) return cloner;
            if (value is GameObject gameObject && gameObject.TryGetComponent(out BrazeCloner onGameObject)) return onGameObject;
            if (value is Component component && component.TryGetComponent(out BrazeCloner onComponent)) return onComponent;
            return null;
        }

        static void Pair(SerializedProperty left, string leftLabel, SerializedProperty right, string rightLabel)
        {
            BrazeEditorGUI.PairedFields(left, new GUIContent(leftLabel, left.tooltip),
                right, new GUIContent(rightLabel, right.tooltip));
        }

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024f * 1024f * 1024f):0.00} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }
    }
}
