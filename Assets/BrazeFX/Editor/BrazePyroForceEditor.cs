using UnityEditor;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector for <see cref="BrazePyroForce"/> (DCC theme).</summary>
    [CustomEditor(typeof(BrazePyroForce), true)]
    [CanEditMultipleObjects]
    public sealed class BrazePyroForceEditor : Editor
    {
        const string K = "BrazeFX.PyroForce.";
        static readonly string[] CauliShapeNames = { "Sphere", "Disc", "Torus" };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            var typeProp = P("type");
            bool fixedType = target is IBrazeFixedPyroForce;
            var ft = fixedType
                ? ((IBrazeFixedPyroForce)target).FixedForceType
                : (BrazePyroForce.ForceType)typeProp.enumValueIndex;
            if (fixedType && !typeProp.hasMultipleDifferentValues)
                typeProp.enumValueIndex = (int)ft;
            string header = fixedType
                ? UnityEditor.ObjectNames.NicifyVariableName(ft.ToString()).ToUpperInvariant() + " FORCE"
                : "PYRO FORCE";
            BrazeEditorGUI.Header(header, "force");

            bool isDisc = ft == BrazePyroForce.ForceType.Disc;
            bool isCauli = ft == BrazePyroForce.ForceType.CauliBillows;
            bool isLift = ft == BrazePyroForce.ForceType.Lift;
            bool isJet = ft == BrazePyroForce.ForceType.Jet;
            bool isSpinner = ft == BrazePyroForce.ForceType.Spinner;
            bool isVortex = ft == BrazePyroForce.ForceType.Vortex;

            if (BrazeEditorGUI.Section(K + "type", fixedType ? "FORCE" : "TYPE"))
            {
                if (!fixedType) BrazeEditorGUI.PropertyField(typeProp);
                if (!isDisc && !isCauli && !isSpinner)
                    BrazeEditorGUI.PropertyField(P("strength"),
                        new UnityEngine.GUIContent(isLift ? "Lift Speed" : isJet ? "Jet Speed" : isVortex ? "Spin Speed" : "Strength"));
                if (isSpinner)
                    BrazeEditorGUI.Slider(P("spinnerSpeed"), -10f, 10f, new UnityEngine.GUIContent("Spin Speed (turns/s)"));
                if (isLift || isJet)
                    BrazeEditorGUI.Slider(P("targetResponse"), 0.5f, 20f, new UnityEngine.GUIContent("Response"));
                BrazeEditorGUI.Hint(TypeHint(ft));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "falloff", isDisc ? "SIZE" : "FORCE FIELD"))
            {
                var shapeProp = P("shape");
                bool nonSphere = !isDisc && shapeProp.enumValueIndex != (int)BrazePyroForce.FalloffShape.Sphere;
                if (isCauli)
                {
                    int selected = CauliShapeToSegment(shapeProp.enumValueIndex);
                    int next = BrazeEditorGUI.Segmented("Shape", CauliShapeNames, selected);
                    shapeProp.enumValueIndex = SegmentToCauliShape(next);
                    nonSphere = shapeProp.enumValueIndex != (int)BrazePyroForce.FalloffShape.Sphere;
                }
                else if (!isDisc) BrazeEditorGUI.PillsEnum("Shape", shapeProp);
                // A Sphere can be Infinite (fills the container); Cylinder/Torus + Spinner are inherently localized.
                if (!isDisc && !isCauli && !isSpinner && !nonSphere) BrazeEditorGUI.ToggleButton("Infinite", P("infinite"));
                BrazeEditorGUI.PropertyField(P("radius"), new UnityEngine.GUIContent(isDisc ? "Radius (cap width)" : "Radius"));
                if (shapeProp.enumValueIndex == (int)BrazePyroForce.FalloffShape.Cylinder ||
                    shapeProp.enumValueIndex == (int)BrazePyroForce.FalloffShape.Disc)
                    BrazeEditorGUI.PropertyField(P("fieldHeight"),
                        new UnityEngine.GUIContent(shapeProp.enumValueIndex == (int)BrazePyroForce.FalloffShape.Disc ? "Thickness (half)" : "Height (half)"));

                bool localized = !isDisc && (isCauli || isSpinner || nonSphere || !P("infinite").boolValue);
                if (localized)
                {
                    BrazeEditorGUI.PropertyField(P("hole"), new UnityEngine.GUIContent("Hole (centre)"));
                    BrazeEditorGUI.PropertyField(P("falloffInner"), new UnityEngine.GUIContent("Core (full strength)"));
                    BrazeEditorGUI.PropertyField(P("falloffPower"), new UnityEngine.GUIContent("Falloff Curve"));
                    BrazeEditorGUI.Hint("The Force Field is just the AREA the force acts in — it adds no push of its own. " +
                                        "Hole carves the centre out (Sphere→shell, Cylinder→tube, Torus→opening) so you can " +
                                        "affect a plume's OUTER edges and leave the middle free. Core/Falloff Curve shape " +
                                        "how the force's strength fades across the area. The scene gizmo shows the volume.");
                }
                if (ft == BrazePyroForce.ForceType.Hold && P("infinite").boolValue && !nonSphere)
                    BrazeEditorGUI.Hint("Hold is meant to be LOCALIZED — turn Infinite OFF and set a Radius.");
                else if (!isDisc && !isCauli && !nonSphere && P("infinite").boolValue)
                    BrazeEditorGUI.Hint("Infinite Sphere fills the whole container — no falloff or hole. Turn it OFF, " +
                                        "or pick a Cylinder/Torus shape, for a localized field area with a boundary.");
            }
            BrazeEditorGUI.EndSection();

            if (isDisc && BrazeEditorGUI.Section(K + "discForces", "FUME FORCES"))
            {
                BrazeEditorGUI.PropertyField(P("discPower"), new UnityEngine.GUIContent("Overall Power"));
                BrazeEditorGUI.PropertyField(P("discCrownAttraction"), new UnityEngine.GUIContent("Chimney/Crown Suction"));
                BrazeEditorGUI.PropertyField(P("discFlatten"), new UnityEngine.GUIContent("Conform Strength"));
                BrazeEditorGUI.PropertyField(P("discHold"), new UnityEngine.GUIContent("Inert Hold"));
                BrazeEditorGUI.PropertyField(P("discSpread"), new UnityEngine.GUIContent("Outward Spread"));
                BrazeEditorGUI.PropertyField(P("discSeal"), new UnityEngine.GUIContent("Containment Seal"));
                BrazeEditorGUI.PropertyField(P("discRoll"), new UnityEngine.GUIContent("Rim Roll"));
                BrazeEditorGUI.PropertyField(P("discSwirl"), new UnityEngine.GUIContent("Twist"));
                BrazeEditorGUI.Hint("These sliders move or calm the fume. Chimney/Crown Suction pulls smoke/fire inward/up through the opening, then curves it into the crown; Conform Strength and Inert Hold remove free buoyancy so the fume can settle; Spread/Roll/Twist shape motion along the shelves; Seal contains the crown and rim.");
            }
            if (isDisc) BrazeEditorGUI.EndSection();

            if (isDisc && BrazeEditorGUI.Section(K + "discShape", "SHAPE CARVING"))
            {
                BrazeEditorGUI.PropertyField(P("discDome"), new UnityEngine.GUIContent("Dome Height"));
                BrazeEditorGUI.PropertyField(P("discLayers"), new UnityEngine.GUIContent("Layers"));
                if (P("discLayers").intValue > 1)
                {
                    BrazeEditorGUI.PropertyField(P("discLayerSpacing"), new UnityEngine.GUIContent("Layer Spacing"));
                    BrazeEditorGUI.PropertyField(P("discTaper"), new UnityEngine.GUIContent("Taper (wide base)"));
                }
                BrazeEditorGUI.PropertyField(P("discCoreOpening"), new UnityEngine.GUIContent("Core Opening"));
                BrazeEditorGUI.PropertyField(P("discThickness"), new UnityEngine.GUIContent("Sheet Thickness"));
                BrazeEditorGUI.Hint("These sliders carve the target shape shown by the gizmo. They do not directly move fume: Dome Height, Layers, Taper, Core Opening, and Thickness define the shelves/chimney/crown the forces conform to.");
            }
            if (isDisc) BrazeEditorGUI.EndSection();

            if (isDisc && BrazeEditorGUI.Section(K + "discDetail", "EDGE DETAIL"))
            {
                BrazeEditorGUI.PropertyField(P("discDistort"), new UnityEngine.GUIContent("Edge Turbulence"));
                BrazeEditorGUI.PropertyField(P("discDistortScale"), new UnityEngine.GUIContent("Edge Frequency"));
                BrazeEditorGUI.Hint("Detail only roughens the edge motion. Keep this low while tuning suction/conform forces.");
            }
            if (isDisc) BrazeEditorGUI.EndSection();

            if (isCauli && BrazeEditorGUI.Section(K + "cauli", "CAULI BILLOWS"))
            {
                BrazeEditorGUI.PillsEnum("Motion", P("billowMotion"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.Slider(P("billowStrength"), 0f, 1f, new UnityEngine.GUIContent("Strength"));
                BrazeEditorGUI.Slider(P("billowOrigin"), 0f, 1f, new UnityEngine.GUIContent("Origin (center ↔ rim)"));
                BrazeEditorGUI.Slider(P("billowScale"), 2f, 96f, new UnityEngine.GUIContent("Size (voxels)"));
                BrazeEditorGUI.Slider(P("billowSpeed"), 0f, 4f, new UnityEngine.GUIContent("Speed"));
                BrazeEditorGUI.Slider(P("billowLines"), 0f, 1f, new UnityEngine.GUIContent("Lines"));
                BrazeEditorGUI.Slider(P("billowSmokeDeposit"), 0f, 4f, new UnityEngine.GUIContent("Deposit Smoke"));
                BrazeEditorGUI.Slider(P("billowFireDeposit"), 0f, 4f, new UnityEngine.GUIContent("Deposit Fire"));
                BrazeEditorGUI.Hint("Boils whatever fluid sits inside the field (set its Shape/Radius above) into " +
                                    "cauliflower lobes. Motion = Up (rising mushroom boil), In Place (roils without " +
                                    "drifting), or Down (collapsing). Origin biases the boil toward the centre or rim; " +
                                    "Lines breaks it into the vertical fingers of a mushroom underside.");
            }
            if (isCauli) BrazeEditorGUI.EndSection();

            if (isVortex && BrazeEditorGUI.Section(K + "vortex", "VORTEX"))
            {
                BrazeEditorGUI.PillsEnum("Motion", P("vortexMotion"));
                var vm = (BrazePyroForce.VortexMotion)P("vortexMotion").enumValueIndex;
                BrazeEditorGUI.Slider(P("vortexGrip"), 0f, 1f, new UnityEngine.GUIContent("Grip"));
                if (vm == BrazePyroForce.VortexMotion.Swirl)
                {
                    // Float property (animatable) shown as an int slider — fractional harmonics seam at θ = ±π.
                    var bl = P("vortexBlades");
                    bl.floatValue = BrazeEditorGUI.IntSlider(new UnityEngine.GUIContent("Blades"),
                        UnityEngine.Mathf.RoundToInt(bl.floatValue), 0, 6);
                }
                if (vm != BrazePyroForce.VortexMotion.Swirl)
                    BrazeEditorGUI.Slider(P("vortexHold"), 0f, 20f, new UnityEngine.GUIContent("Hold"));
                if (vm == BrazePyroForce.VortexMotion.Orbit)
                    BrazeEditorGUI.PropertyField(P("vortexOrbitRadius"), new UnityEngine.GUIContent("Orbit Radius"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PropertyField(P("vortexLift"), new UnityEngine.GUIContent("Lift (axial)"));
                BrazeEditorGUI.Slider(P("vortexAxialDamp"), 0f, 1f, new UnityEngine.GUIContent("Axial Damp"));
                BrazeEditorGUI.Slider(P("vortexLiftVariation"), 0f, 1f, new UnityEngine.GUIContent("Lift Variation"));
                BrazeEditorGUI.Hint("Spin Speed (above) drives angular volume rotation (negative reverses). In a " +
                                    "finite field, Grip only clips outward escape near the edge; it does not pull " +
                                    "inward or damp spin. Blades stamps rotating lumps into the swirl so the spin " +
                                    "is visible on evenly mixed smoke (0 = smooth vortex). " +
                                    "Swirl spins the smoke stably around the axis; Inward also sucks it toward the " +
                                    "axis (tornado); Orbit holds it at the Orbit Radius and flattens the column. Lift " +
                                    "drives the column along the axis (height ≈ Lift × life); Axial Damp firms that " +
                                    "flow (Lift 0 + Damp flattens to a disc); Lift Variation softens it spatially.");
            }
            if (isVortex) BrazeEditorGUI.EndSection();

            if (isSpinner && BrazeEditorGUI.Section(K + "spinner", "SPINNER"))
            {
                BrazeEditorGUI.Slider(P("spinnerGrip"), 0f, 1f, new UnityEngine.GUIContent("Grip"));
                BrazeEditorGUI.Slider(P("spinnerContain"), 0f, 1f, new UnityEngine.GUIContent("Containment"));
                BrazeEditorGUI.Slider(P("spinnerFlatten"), 0f, 1f, new UnityEngine.GUIContent("Flatten"));
                BrazeEditorGUI.Hint("Spins the fume inside the field like a solid wheel (rim fast, hub slow) instead " +
                                    "of flinging it outward the way Vortex does. Spin Speed (above) sets turns/sec; " +
                                    "Grip = how tightly smoke locks to the wheel; Containment = how hard it's held in " +
                                    "(stops drift-out); Flatten = keep the spin in the wheel's plane. Pick a Disc or " +
                                    "Cylinder Shape + a finite Radius above to define the tyre.");
            }
            if (isSpinner) BrazeEditorGUI.EndSection();

            if (ft == BrazePyroForce.ForceType.Turbulence && BrazeEditorGUI.Section(K + "turbulence", "TURBULENCE"))
            {
                BrazeEditorGUI.PropertyField(P("turbulenceScale"));
            }
            if (ft == BrazePyroForce.ForceType.Turbulence) BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static string TypeHint(BrazePyroForce.ForceType t) => t switch
        {
            BrazePyroForce.ForceType.Wind       => "Uniform push along this object's forward axis (rotate to aim).",
            BrazePyroForce.ForceType.Vortex     => "Swirl around the forward axis — twisters / rotating columns. Spin Speed drives volume rotation; Grip contains outward drift.",
            BrazePyroForce.ForceType.Turbulence => "Curl-noise churn that breaks the smoke up. Set the scale below.",
            BrazePyroForce.ForceType.Gravity    => "Uniform push along forward — point the object DOWN for gravity.",
            BrazePyroForce.ForceType.Attract    => "Pull toward the centre. Negative Strength = repel (blow outward).",
            BrazePyroForce.ForceType.Disc       => "Mushroom-cap guide field. Tune Fume Forces to move/hold smoke; tune Shape Carving to change the gizmo shelves, chimney, and crown.",
            BrazePyroForce.ForceType.Hold       => "Damps velocity inside the Radius → settles/holds smoke in place (e.g. keep ground smoke down). Strength = how hard it calms. Use a finite Radius.",
            BrazePyroForce.ForceType.Lift       => "Raises whatever smoke enters the field UP to a target rise speed (Lift Speed) — an updraft. Only lifts; it won't brake smoke already rising faster. Place it where downwash is pushing smoke down (e.g. the rotor wash edges) so the fume rises instead. Negative Lift Speed = a downdraft. Use a finite Radius (turn Infinite off) to localize it.",
            BrazePyroForce.ForceType.Jet        => "Like Lift, but along THIS object's forward axis (rotate to aim) instead of straight up. Drives whatever smoke enters the field to a target speed (Jet Speed) in that direction — a steerable blast/draft. Only drives forward; it won't brake faster-moving smoke. Negative Jet Speed jets backward. Use a finite Radius (turn Infinite off) to localize it.",
            BrazePyroForce.ForceType.CauliBillows => "Boils whatever fluid is inside the field into vertical rising/sinking/in-place cauliflower lobes. Deposit Smoke/Fire can add gated fume where fluid already exists. Pick Shape (Sphere/Disc/Torus) + Radius, then tune the Cauli Billows knobs.",
            BrazePyroForce.ForceType.Spinner    => "Spins the smoke inside the field about this object's forward axis like a car tyre — a LOCALIZED solid-body rotation. Unlike Vortex (where smoke accelerates and flings out into larger swirls), the Spinner grips the fume and rotates it in place: the rim turns fast, the hub barely moves. Rotate the object to aim the axle; pick a Disc or Cylinder Shape + finite Radius, then tune Spin Speed / Grip / Containment / Flatten.",
            _ => "",
        };

        static int CauliShapeToSegment(int shape)
        {
            if (shape == (int)BrazePyroForce.FalloffShape.Disc) return 1;
            if (shape == (int)BrazePyroForce.FalloffShape.Torus) return 2;
            return 0;
        }

        static int SegmentToCauliShape(int segment)
        {
            if (segment == 1) return (int)BrazePyroForce.FalloffShape.Disc;
            if (segment == 2) return (int)BrazePyroForce.FalloffShape.Torus;
            return (int)BrazePyroForce.FalloffShape.Sphere;
        }
    }
}
