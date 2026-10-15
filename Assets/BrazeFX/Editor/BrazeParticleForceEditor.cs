using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Shared IMGUI inspector for the particle force components (Wind / Radial / Vortex / Drag).
    /// Draws the common Region (shape + C4D falloff), Strength, and Targets panels; concrete editors
    /// supply the title/icon and any type-specific fields via <see cref="DrawTypeFields"/>.
    /// </summary>
    public abstract class BrazeParticleForceEditor : Editor
    {
        protected abstract string Title { get; }
        protected abstract string Icon { get; }

        /// <summary>Hook for type-specific fields, drawn inside the Strength panel.</summary>
        protected virtual void DrawTypeFields(System.Func<string, SerializedProperty> P) { }

        /// <summary>False for force types whose GPU slot for direction jitter is repurposed (hides the slider).</summary>
        protected virtual bool ShowDirectionJitter => true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header(Title, Icon);

            SerializedProperty P(string n) => serializedObject.FindProperty(n);
            string K = "BrazeFX.Force." + target.GetType().Name + ".";

            if (BrazeEditorGUI.Section(K + "region", "Region"))
            {
                BrazeEditorGUI.PillsEnum("Shape", P("shape"));
                var shape = (ForceShape)P("shape").enumValueIndex;
                if (shape == ForceShape.Sphere)
                    BrazeEditorGUI.PropertyField(P("radius"));
                else if (shape == ForceShape.Box)
                    BrazeEditorGUI.PropertyField(P("boxSize"), new GUIContent("Box Size"));

                if (shape != ForceShape.Infinite)
                {
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PropertyField(P("falloff"), new GUIContent("Falloff"));
                    BrazeEditorGUI.Hint("Strength from region centre (left) to edge (right).");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "strength", "Strength"))
            {
                BrazeEditorGUI.PropertyField(P("strength"));
                DrawTypeFields(P);
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "variation", "Variation"))
            {
                BrazeEditorGUI.PropertyField(P("centerJitter"), new GUIContent("Center Jitter"));
                if (ShowDirectionJitter)
                    BrazeEditorGUI.PropertyField(P("directionJitter"), new GUIContent("Direction Jitter"));
                BrazeEditorGUI.PropertyField(P("strengthJitter"), new GUIContent("Strength Jitter"));
                BrazeEditorGUI.Hint("Per-particle randomness — breaks up uniform motion for organic forces.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "scope", "Scope"))
            {
                var force = target as BrazeParticleForce;
                if (force != null && !IsForceListedInAnySystem(force))
                    EditorGUILayout.HelpBox("Not added to any Particle System — this force does nothing yet. " +
                                            "Add it to a system's Forces list (Motion → Forces) to take effect.", MessageType.Warning);
                BrazeEditorGUI.Hint("This force only affects Particle Systems that list it in Motion → Forces.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "debug", "Debug"))
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        // True if any particle system explicitly lists this force in its Forces list (Motion → Forces).
        static bool IsForceListedInAnySystem(BrazeParticleForce force)
        {
#if UNITY_6000_5_OR_NEWER
            var systems = UnityEngine.Object.FindObjectsByType<BrazeParticleSystem>();
#else
            var systems = UnityEngine.Object.FindObjectsByType<BrazeParticleSystem>(FindObjectsSortMode.None);
#endif
            for (int i = 0; i < systems.Length; i++)
            {
                var s = systems[i];
                if (s != null && s.forces != null && s.forces.Contains(force)) return true;
            }
            return false;
        }
    }

    [CustomEditor(typeof(BrazeWind))]
    [CanEditMultipleObjects]
    public sealed class BrazeWindEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE WIND";
        protected override string Icon => "dynamics";   // wind glyph
    }

    [CustomEditor(typeof(BrazeGravity))]
    [CanEditMultipleObjects]
    public sealed class BrazeGravityEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE GRAVITY";
        protected override string Icon => "gravity";
        protected override void DrawTypeFields(System.Func<string, SerializedProperty> P) =>
            BrazeEditorGUI.Hint("Vertical pull (m/s²): negative = down, positive = up. For lateral pushes use Wind / Radial.");
    }

    [CustomEditor(typeof(BrazeRadial))]
    [CanEditMultipleObjects]
    public sealed class BrazeRadialEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE RADIAL";
        protected override string Icon => "radial";
    }

    [CustomEditor(typeof(BrazeVortex))]
    [CanEditMultipleObjects]
    public sealed class BrazeVortexEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE VORTEX";
        protected override string Icon => "force";       // vortex spiral

        protected override void DrawTypeFields(System.Func<string, SerializedProperty> P)
        {
            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PillsEnum("Motion", P("motion"));
            var motion = (BrazeVortex.VortexMotion)P("motion").enumValueIndex;
            if (motion == BrazeVortex.VortexMotion.Swirl)
                BrazeEditorGUI.Hint("Swirl rotates each particle's velocity around the axis (speed-preserving), so " +
                                    "they circle stably instead of being flung out. Strength = rotation rate (rad/s).");
            else
            {
                BrazeEditorGUI.PropertyField(P("holdStrength"), new GUIContent("Hold"));
                BrazeEditorGUI.Hint("Inward/Orbit add a radial pull on top of the swirl. Here Strength = target " +
                                    "tangential speed and Hold = how firmly particles are pulled to their target radius.");
            }
            if (motion == BrazeVortex.VortexMotion.Orbit)
                BrazeEditorGUI.PropertyField(P("orbitRadius"), new GUIContent("Orbit Radius"));

            BrazeEditorGUI.SubLabel("AXIAL (COLUMN HEIGHT)");
            BrazeEditorGUI.PropertyField(P("lift"), new GUIContent("Lift"));
            BrazeEditorGUI.Slider(P("axialDamp"), 0f, 1f, new GUIContent("Axial Damp"));
            BrazeEditorGUI.Slider(P("liftVariation"), 0f, 1f, new GUIContent("Lift Variation"));
            BrazeEditorGUI.Hint("Lift drives the column on its own — height ≈ Lift × Start Lifetime (− sinks). Axial Damp " +
                                "firms how tightly that speed is held; with Lift = 0 it flattens a free column to a disc. " +
                                "Lift Variation spreads the rise speed per particle for a softer, uneven top.");
        }
    }

    [CustomEditor(typeof(BrazeDrag))]
    [CanEditMultipleObjects]
    public sealed class BrazeDragEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE DRAG";
        protected override string Icon => "drag";
    }

    [CustomEditor(typeof(BrazeSurfaceFlow))]
    [CanEditMultipleObjects]
    public sealed class BrazeSurfaceFlowEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE SURFACE FLOW";
        protected override string Icon => "turbulence";
        protected override bool ShowDirectionJitter => false;   // its GPU slot carries Drift Speed

        protected override void DrawTypeFields(System.Func<string, SerializedProperty> P)
        {
            BrazeEditorGUI.Hint("Strength = FLOW SPEED — how fast particles slide across the surface. Use the Region " +
                                "falloff (Shape ≠ Infinite) to flow only part of the mesh.");
            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PropertyField(P("surfaceMesh"), new GUIContent("Surface Mesh"));
            BrazeEditorGUI.Hint("The mesh particles flow across. Leave empty to use a Renderer on this object. " +
                                "If the system has a Mesh Collider, that mesh is used instead (one grid per system).");

            BrazeEditorGUI.SubLabel("FLOW");
            BrazeEditorGUI.PropertyField(P("turbulenceScale"), new GUIContent("Scale (wave size)"));
            BrazeEditorGUI.Slider(P("turbulenceDetail"), 0f, 1f, new GUIContent("Detail"));
            BrazeEditorGUI.Slider(P("swirl"), 0f, 1f, new GUIContent("Swirl"));
            BrazeEditorGUI.Slider(P("flowAlign"), 0f, 1f, new GUIContent("Align"));
            BrazeEditorGUI.Hint("Swirl blends eddies vs current: 1 = on-surface vortices circulating around noise " +
                                "cells and racing along filament lines, 0 = one broad laminar current. Align = how " +
                                "tightly particles trace the streamlines; raise it if the pattern scatters.");

            BrazeEditorGUI.SubLabel("DRIFT");
            BrazeEditorGUI.PropertyField(P("driftSpeed"), new GUIContent("Drift Speed"));
            if (Mathf.Abs(P("driftSpeed").floatValue) > 1e-4f)
                BrazeEditorGUI.PropertyField(P("driftDirection"), new GUIContent("Direction"));
            BrazeEditorGUI.Hint("A constant world-direction current pressed onto the skin — (0,1,0) makes the " +
                                "energy lick upward across the whole form. Layers on top of the swirl.");

            BrazeEditorGUI.SubLabel("CONTAINMENT");
            BrazeEditorGUI.Slider(P("adhesion"), 0f, 1f, new GUIContent("Adhesion"));
            BrazeEditorGUI.PropertyField(P("surfaceOffset"), new GUIContent("Offset"));
            BrazeEditorGUI.PropertyField(P("bandThickness"), new GUIContent("Skin Thickness"));
            BrazeEditorGUI.Hint("POSITION-BASED: every frame each particle is projected back into the skin, after " +
                                "all forces and collisions — at Adhesion 1 particles cannot escape, period. Offset " +
                                "floats the skin above the mesh; Thickness is the free depth particles keep inside it " +
                                "(small = tight film, larger = volumetric aura).");
        }
    }

    [CustomEditor(typeof(BrazeTurbulence))]
    [CanEditMultipleObjects]
    public sealed class BrazeTurbulenceEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE TURBULENCE";
        protected override string Icon => "turbulence";

        protected override void DrawTypeFields(System.Func<string, SerializedProperty> P)
        {
            BrazeEditorGUI.Hint("Strength = enforcement: how strongly particles conform to the turbulent flow, " +
                                "not their speed. Turbulence runs after push forces, so Gravity/Wind drive motion " +
                                "and turbulence shapes the final path.");
            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PropertyField(P("scale"), new GUIContent("Scale (wave size)"));
            BrazeEditorGUI.PropertyField(P("detail"));
            BrazeEditorGUI.PropertyField(P("scaleRandom"), new GUIContent("Scale Random"));
            BrazeEditorGUI.Hint("Pure curl conformance: particles in motion bend to follow the turbulent flow at their " +
                                "own speed. It adds no pull or confinement of its own — use the Region falloff to scope " +
                                "where it acts, and Gravity/Wind to drive the motion it shapes.");

            BrazeEditorGUI.SubLabel("PREVIEW");
            BrazeEditorGUI.ToggleButton("Region Gizmo", P("showGizmo"));      // standard region falloff gizmo
            BrazeEditorGUI.ToggleButton("Noise Field", P("previewField"));    // flow-field visualiser
            if (P("previewField").boolValue)
            {
                BrazeEditorGUI.PillsEnum("Style", P("previewStyle"));
                BrazeEditorGUI.IntSlider(P("previewDensity"), 3, 12, new GUIContent("Density"));
                if ((ForceShape)P("shape").enumValueIndex == ForceShape.Infinite)
                    BrazeEditorGUI.PropertyField(P("previewExtent"), new GUIContent("Region Size"));
                BrazeEditorGUI.Hint("Select to see the flow (dim→bright along it). ARROWS sample the nominal raw " +
                                    "curl field and fade/shorten with Strength. STREAMLINES trace representative test particles using the same " +
                                    "speed-preserving conformance as the GPU, so Strength, Falloff, Scale, " +
                                    "Detail and Scale Random are visible. The preview is a static snapshot (t = 0); at " +
                                    "play time the field slowly evolves so particles never pool. Editor-only.\n" +
                                    "Note: Direction jitter does not change turbulence. Strength jitter varies the conformance response; Center Jitter only " +
                                    "jitters localized falloff per particle and is not drawn in this preview.");
            }
        }
    }

    [CustomEditor(typeof(BrazeSplineFlow))]
    [CanEditMultipleObjects]
    public sealed class BrazeSplineFlowEditor : BrazeParticleForceEditor
    {
        protected override string Title => "BRAZE SPLINE FLOW";
        protected override string Icon => "spline";

        protected override void DrawTypeFields(System.Func<string, SerializedProperty> P)
        {
            BrazeEditorGUI.Hint("Strength = FLOW SPEED — how fast particles stream along the curve. Pair with a " +
                                "surface-wrapped spline (Spline Surface) for energy that flows over a mesh.");
            BrazeEditorGUI.Separator();
            BrazeEditorGUI.PropertyField(P("spline"), new GUIContent("Spline"));
            BrazeEditorGUI.Hint("The path particles flow along. Leave empty to use a BrazeSpline on this object.");

            BrazeEditorGUI.SubLabel("PATH");
            BrazeEditorGUI.PropertyField(P("captureRadius"), new GUIContent("Capture Radius"));
            BrazeEditorGUI.ToggleButton("Reverse", P("reverse"));

            BrazeEditorGUI.SubLabel("FLOW");
            BrazeEditorGUI.PropertyField(P("capture"), new GUIContent("Capture"));
            BrazeEditorGUI.Slider(P("stick"), 0f, 1f, new GUIContent("Stick"));
            BrazeEditorGUI.PropertyField(P("swirl"), new GUIContent("Swirl"));
            BrazeEditorGUI.Hint("Capture pulls particles onto the curve (high = tight wire, low = loose stream). " +
                                "Stick seals the tube so they can't leak out. Swirl twists the stream into a corkscrew. " +
                                "Particles outside Capture Radius are unaffected.");
        }
    }
}
