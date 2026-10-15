using UnityEditor;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazeSpread"/> (DCC theme) — gives the Mesh Effect
    /// the shared BrazeFX skin instead of Unity's default inspector.</summary>
    [CustomEditor(typeof(BrazeSpread))]
    [CanEditMultipleObjects]
    public sealed class BrazeSpreadEditor : Editor
    {
        const string K = "BrazeFX.Spread.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("SPREAD", "spread");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "target", "TARGET"))
            {
                BrazeEditorGUI.PropertyField(P("target"));
                BrazeEditorGUI.Hint("Mesh / skinned renderer the effect crawls across. Empty = auto-find the " +
                                    "nearest skinned renderer.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "spread", "SPREAD"))
            {
                var expand = P("expandSpread");
                BrazeEditorGUI.ToggleButton("Expand", expand);
                if (expand.boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("spreadSpeed"));
                    BrazeEditorGUI.Hint("Each hit's coverage grows outward over time, like a stain.");
                }
                else
                {
                    BrazeEditorGUI.PropertyField(P("dabRadius"));
                    BrazeEditorGUI.Hint("Each hit stains a fixed spot — keep hitting from all angles to fill the mesh.");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "look", "LOOK"))
            {
                BrazeEditorGUI.PillsEnum("Look", P("look"));
                BrazeEditorGUI.ToggleButton("Render Surface", P("renderSurface"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("fillColor"));
                BrazeEditorGUI.PropertyField(P("edgeColor"));
                BrazeEditorGUI.PropertyField(P("edgeWidth"));
                BrazeEditorGUI.PropertyField(P("fadeBehind"));

                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SubLabel("NOISE");
                BrazeEditorGUI.PropertyField(P("noiseTexture"));
                BrazeEditorGUI.PropertyField(P("noiseTiling"));
                BrazeEditorGUI.PropertyField(P("noiseBlend"));
                BrazeEditorGUI.PropertyField(P("noiseStrength"));
                BrazeEditorGUI.Hint("Mixes the noise into the fill value to break up flat patches and add depth. " +
                                    "Projected cubically (object-space triplanar), so it never stretches on bad " +
                                    "UVs and stays attached to the body. No texture = flat fill.");

                BrazeEditorGUI.SubLabel("LEVELS");
                BrazeEditorGUI.PropertyField(P("noiseLow"));
                BrazeEditorGUI.PropertyField(P("noiseHigh"));
                BrazeEditorGUI.PropertyField(P("noiseMid"));
                BrazeEditorGUI.PropertyField(P("noiseContrast"));
                BrazeEditorGUI.Hint("Shape the noise like image levels — Low/High clip the input range, Mid is the " +
                                    "midtone gamma, Contrast punches it around the midpoint.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "viewer", "VIEWER"))
            {
                BrazeEditorGUI.ToggleButton("Show Dots", P("showDots"));
                if (P("showDots").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("maxDots"));
                    BrazeEditorGUI.PropertyField(P("dotSize"));
                }
                BrazeEditorGUI.Hint("Draws the sampled surface points as Scene-view gizmo dots to verify sampling.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "fluid", "FIRE / SMOKE"))
            {
                var emit = P("emitToFluid");
                BrazeEditorGUI.ToggleButton("Emit To Fluid", emit);
                if (emit.boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("emitDensity"));
                    BrazeEditorGUI.PropertyField(P("emitTemperature"));
                    BrazeEditorGUI.PropertyField(P("emitFuel"));
                    BrazeEditorGUI.PropertyField(P("emitStrength"));
                    BrazeEditorGUI.ToggleButton("Front Only", P("emitFrontOnly"));
                    BrazeEditorGUI.Hint("Injects the covered mask into an overlapping BrazeFX Container as a " +
                                        "fire/smoke emission source.");
                }
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
