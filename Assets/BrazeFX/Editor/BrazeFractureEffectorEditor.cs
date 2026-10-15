using System;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Shared DCC-style inspector for direct-transform fracture effectors. Concrete editors only draw the
    /// function that generates their sample; field, transform, scope, and debug UI stay consistent.</summary>
    public abstract class BrazeFractureEffectorEditor : Editor
    {
        static readonly string[] PrimaryShapeNames = { "Infinite", "Sphere", "Box", "Capsule" };
        static readonly int[] PrimaryShapeValues = { 0, 1, 2, 3 };
        static readonly string[] SecondaryShapeNames = { "Cylinder", "Cone", "Torus", "Plane" };
        static readonly int[] SecondaryShapeValues = { 4, 5, 6, 7 };

        protected abstract string Title { get; }
        protected virtual string Icon => "effector";
        protected virtual bool ShowTransformSection => true;
        protected abstract void DrawFunctionFields(Func<string, SerializedProperty> property, string key);

        protected static void Card(string key, string title, Action draw)
        {
            if (BrazeEditorGUI.Section(key, title)) draw();
            BrazeEditorGUI.EndSection();
        }

        protected string PropertyName(string member)
        {
            SerializedProperty property = serializedObject.FindProperty(member);
            return BrazeAnimatableRegistry.DisplayName(target as Component, member, property?.displayName);
        }

        protected GUIContent PropertyLabel(string member)
        {
            SerializedProperty property = serializedObject.FindProperty(member);
            return new GUIContent(PropertyName(member), property?.tooltip);
        }

        protected string PropertyPathPart(string member, int index, string fallback)
        {
            string path = BrazeAnimatableRegistry.DisplayPath(target as Component, member, fallback);
            string[] parts = path.Split('/');
            return index >= 0 && index < parts.Length ? parts[index] : fallback;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header(Title, Icon);
            SerializedProperty P(string n) => serializedObject.FindProperty(n);
            string key = "BrazeFX.Effector." + target.GetType().Name + ".";

            if (BrazeEditorGUI.Section(key + "field", PropertyPathPart("shape", 0, "FIELD")))
            {
                SerializedProperty shapeProperty = P("shape");
                BrazeEditorGUI.PillsInt(PropertyName("shape"), shapeProperty, PrimaryShapeNames, PrimaryShapeValues);
                BrazeEditorGUI.PillsInt(string.Empty, shapeProperty, SecondaryShapeNames, SecondaryShapeValues);
                var shape = (FractureEffectorShape)shapeProperty.intValue;
                switch (shape)
                {
                    case FractureEffectorShape.Sphere:
                        BrazeEditorGUI.PropertyField(P("radius"), PropertyLabel("radius"));
                        break;
                    case FractureEffectorShape.Box:
                        BrazeEditorGUI.PropertyField(P("boxSize"), PropertyLabel("boxSize"));
                        break;
                    case FractureEffectorShape.Capsule:
                    case FractureEffectorShape.Cylinder:
                        BrazeEditorGUI.PropertyField(P("radius"), PropertyLabel("radius"));
                        BrazeEditorGUI.PropertyField(P("height"), PropertyLabel("height"));
                        break;
                    case FractureEffectorShape.Cone:
                        BrazeEditorGUI.PropertyField(P("radius"), PropertyLabel("radius"));
                        BrazeEditorGUI.PropertyField(P("height"), PropertyLabel("height"));
                        break;
                    case FractureEffectorShape.Torus:
                        BrazeEditorGUI.PropertyField(P("torusMajorRadius"), PropertyLabel("torusMajorRadius"));
                        BrazeEditorGUI.PropertyField(P("torusMinorRadius"), PropertyLabel("torusMinorRadius"));
                        break;
                    case FractureEffectorShape.Plane:
                        BrazeEditorGUI.PropertyField(P("planeSize"), PropertyLabel("planeSize"));
                        BrazeEditorGUI.PropertyField(P("planeThickness"), PropertyLabel("planeThickness"));
                        break;
                }

                if (shape != FractureEffectorShape.Infinite)
                {
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PropertyField(P("falloff"));
                    BrazeEditorGUI.Hint("Influence from field centre to edge. Position and rotation come from this object's Transform.");
                }
                BrazeEditorGUI.Separator();
                if (target is BrazeDelayEffector)
                    BrazeEditorGUI.Slider(P("strength"), 0f, 1f, PropertyLabel("strength"));
                else
                    BrazeEditorGUI.Slider(P("strength"), -2f, 2f, PropertyLabel("strength"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(key + "function", "EFFECTOR"))
                DrawFunctionFields(P, key + "function.");
            BrazeEditorGUI.EndSection();

            if (ShowTransformSection)
            {
                if (BrazeEditorGUI.Section(key + "transform", PropertyPathPart("transformSpace", 0, "TRANSFORM")))
                {
                    BrazeEditorGUI.PillsEnum(PropertyName("transformSpace"), P("transformSpace"));
                    BrazeEditorGUI.SubHeader(PropertyPathPart("affectPosition", 1, "POSITION"));
                    BrazeEditorGUI.ToggleButton(PropertyName("affectPosition"), P("affectPosition"));
                    if (P("affectPosition").boolValue)
                        BrazeEditorGUI.PropertyField(P("positionAmount"), PropertyLabel("positionAmount"));

                    BrazeEditorGUI.SubHeader(PropertyPathPart("affectRotation", 1, "ROTATION"));
                    BrazeEditorGUI.ToggleButton(PropertyName("affectRotation"), P("affectRotation"));
                    if (P("affectRotation").boolValue)
                        BrazeEditorGUI.PropertyField(P("rotationAmount"), PropertyLabel("rotationAmount"));

                    BrazeEditorGUI.SubHeader(PropertyPathPart("affectScale", 1, "SCALE"));
                    BrazeEditorGUI.ToggleButton(PropertyName("affectScale"), P("affectScale"));
                    if (P("affectScale").boolValue)
                    {
                        SerializedProperty scaleAmount = P("scaleAmount");
                        SerializedProperty uniformScale = P("uniformScale");
                        if (target is BrazeStepEffector or BrazePlaneEffector)
                        {
                            BrazeEditorGUI.ToggleButton(PropertyName("uniformScale"), uniformScale);
                            if (uniformScale.boolValue)
                                DrawUniformScaleSlider(scaleAmount, PropertyLabel("scaleAmount"));
                            else
                                BrazeEditorGUI.PropertyField(scaleAmount, PropertyLabel("scaleAmount"));
                        }
                        else
                        {
                            BrazeEditorGUI.PropertyField(scaleAmount, PropertyLabel("scaleAmount"));
                            BrazeEditorGUI.ToggleButton(PropertyName("uniformScale"), uniformScale);
                        }
                    }
                    BrazeEditorGUI.Hint("Shared direct-transform layer. It changes fragment matrices without adding force, velocity, or acceleration.");
                }
                BrazeEditorGUI.EndSection();
            }

            if (BrazeEditorGUI.Section(key + "scope", "SCOPE"))
            {
                var effector = target as BrazeFractureEffector;
                if (effector != null && !IsListed(effector))
                    EditorGUILayout.HelpBox("Not added to a Voronoi Fracture or Braze Cloner. Drop this component into an EFFECTORS list to use it.", MessageType.Warning);
                BrazeEditorGUI.Hint("An effector only affects fractures and cloners that explicitly list it. Stack order is top to bottom.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(key + "debug", "DEBUG"))
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawUniformScaleSlider(SerializedProperty scaleAmount, GUIContent label)
        {
            SerializedProperty amount = scaleAmount.FindPropertyRelative("x");
            EditorGUI.BeginChangeCheck();
            BrazeEditorGUI.Slider(amount, -2f, 2f, label);
            if (EditorGUI.EndChangeCheck())
                scaleAmount.vector3Value = Vector3.one * amount.floatValue;
        }

        static bool IsListed(BrazeFractureEffector effector)
        {
            var fractures = BrazeVoronoiFracture.Active;
            for (int i = 0; i < fractures.Count; i++)
                if (fractures[i] != null && fractures[i].effectors != null && fractures[i].effectors.Contains(effector))
                    return true;
            var cloners = BrazeCloner.Active;
            for (int i = 0; i < cloners.Count; i++)
                if (cloners[i] != null && cloners[i].effectors != null && cloners[i].effectors.Contains(effector))
                    return true;
            return false;
        }
    }

    [CustomEditor(typeof(BrazePlaneEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazePlaneEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "PLANE EFFECTOR";
        protected override string Icon => "plane";

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            BrazeEditorGUI.Hint("Applies the authored Position, Rotation, and Scale as one constant transform. " +
                                "The shared Field, Falloff, Strength, Transform Space, and stack order control the result entirely on the GPU.");
        }
    }

    [CustomEditor(typeof(BrazeRandomEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeRandomEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "RANDOM EFFECTOR";

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            var mode = (BrazeRandomEffector.RandomMode)P("randomMode").enumValueIndex;
            Card(key + "settings", PropertyPathPart("randomMode", 1, "RANDOMIZATION"), () =>
            {
                BrazeEditorGUI.PillsEnum(PropertyName("randomMode"), P("randomMode"));
                BrazeEditorGUI.PropertyField(P("seed"), PropertyLabel("seed"));
                BrazeEditorGUI.PropertyField(P("outputRange"), PropertyLabel("outputRange"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(PropertyName("synchronized"), P("synchronized"));
                BrazeEditorGUI.ToggleButton(PropertyName("indexed"), P("indexed"));
            });

            if (mode == BrazeRandomEffector.RandomMode.Noise || mode == BrazeRandomEffector.RandomMode.Turbulence)
            {
                Card(key + "noise", PropertyPathPart("noiseSpace", 1, "NOISE"), () =>
                {
                    BrazeEditorGUI.PillsEnum(PropertyName("noiseSpace"), P("noiseSpace"));
                    BrazeEditorGUI.PropertyField(P("noiseSize"), PropertyLabel("noiseSize"));
                    BrazeEditorGUI.PropertyField(P("animationSpeed"), PropertyLabel("animationSpeed"));
                });
            }

            string hint = mode switch
            {
                BrazeRandomEffector.RandomMode.Gaussian => "Gaussian concentrates most values near the middle of Range, with fewer extremes.",
                BrazeRandomEffector.RandomMode.Noise => "Noise creates smooth spatially-related variation and can animate continuously.",
                BrazeRandomEffector.RandomMode.Turbulence => "Turbulence layers several noise scales for a broken-up organic pattern.",
                _ => "Random gives every fragment an independent stable value. Change Seed for another variation.",
            };
            BrazeEditorGUI.Hint(hint + " Synchronized shares one vector across transform channels; Indexed XYZ gives each axis its own value.");
        }
    }

    [CustomEditor(typeof(BrazeDelayEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeDelayEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "DELAY EFFECTOR";
        protected override string Icon => "delay";
        protected override bool ShowTransformSection => false;

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            Card(key + "delay", PropertyPathPart("mode", 1, "DELAY"), () =>
            {
                BrazeEditorGUI.PillsEnum(PropertyName("mode"), P("mode"));
            });

            var mode = (BrazeDelayEffector.DelayMode)P("mode").enumValueIndex;
            string behavior = mode switch
            {
                BrazeDelayEffector.DelayMode.Even => "Even follows changes at a uniform rate.",
                BrazeDelayEffector.DelayMode.Spring => "Spring adds soft, damped overshoot and residual motion.",
                _ => "Blend reacts quickly to changes, then eases smoothly into the result.",
            };
            BrazeEditorGUI.Hint(behavior + " Strength controls the amount of delay. Place this effector below the transform effectors it should delay; history stays entirely on the GPU.");
        }
    }

    [CustomEditor(typeof(BrazeStepEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeStepEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "STEP EFFECTOR";
        protected override string Icon => "step";

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            Card(key + "distribution", PropertyPathPart("ordering", 1, "DISTRIBUTION"), () =>
            {
                BrazeEditorGUI.PillsEnum(PropertyName("ordering"), P("ordering"));
                BrazeEditorGUI.PropertyField(P("inputRange"), PropertyLabel("inputRange"));
            });
            Card(key + "remap", PropertyPathPart("offset", 1, "REMAP"), () =>
            {
                BrazeEditorGUI.PropertyField(P("stepCurve"), PropertyLabel("stepCurve"));
                BrazeEditorGUI.PropertyField(P("offset"), PropertyLabel("offset"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(PropertyName("loop"), P("loop"));
                BrazeEditorGUI.ToggleButton(PropertyName("reverse"), P("reverse"));
                BrazeEditorGUI.IntSlider(P("quantize"), 0, 64, PropertyLabel("quantize"));
            });

            var ordering = (BrazeStepEffector.Ordering)P("ordering").enumValueIndex;
            string source = ordering switch
            {
                BrazeStepEffector.Ordering.Index => "bake index (C4D-style sequence)",
                BrazeStepEffector.Ordering.ObjectX => "object-space X position",
                BrazeStepEffector.Ordering.ObjectY => "object-space Y position",
                BrazeStepEffector.Ordering.ObjectZ => "object-space Z position",
                _ => "distance from the fractured object's centre",
            };
            BrazeEditorGUI.Hint("Generates 0..1 from " + source + ", remaps it through Curve, then drives the shared transform channels. Bands above 1 creates discrete terraces.");
        }
    }

    [CustomEditor(typeof(BrazeTargetEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeTargetEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "TARGET EFFECTOR";
        protected override string Icon => "target";
        protected override bool ShowTransformSection => false;

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            Card(key + "aim", PropertyPathPart("aimAxis", 1, "AIM"), () =>
            {
                BrazeEditorGUI.PropertyField(P("targetObject"), PropertyLabel("targetObject"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.SignedAxis(P("aimAxis"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(PropertyName("usePitch"), P("usePitch"));
                BrazeEditorGUI.PillsEnum(PropertyName("upMode"), P("upMode"));
                if ((BrazeTargetEffector.UpMode)P("upMode").enumValueIndex == BrazeTargetEffector.UpMode.Custom)
                    BrazeEditorGUI.PropertyField(P("customUp"), PropertyLabel("customUp"));
            });

            Card(key + "repel", PropertyPathPart("repel", 1, "REPEL"), () =>
            {
                BrazeEditorGUI.ToggleButton(PropertyName("repel"), P("repel"));
                if (P("repel").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("repelDistance"), PropertyLabel("repelDistance"));
                    BrazeEditorGUI.PropertyField(P("repelStrength"), PropertyLabel("repelStrength"));
                }
            });

            BrazeEditorGUI.Hint("Aims each fragment directly at Target; an empty Target uses this effector's position. " +
                                "The field controls which fragments blend toward that orientation. Repel adds a direct offset, never velocity.");
        }
    }

    [CustomEditor(typeof(BrazeSplineEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeSplineEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "SPLINE EFFECTOR";
        protected override string Icon => "spline";
        protected override bool ShowTransformSection => false;

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            Card(key + "path", PropertyPathPart("ordering", 1, "PATH"), () =>
            {
                BrazeEditorGUI.PropertyField(P("spline"), PropertyLabel("spline"));
                BrazeEditorGUI.PillsEnum(PropertyName("ordering"), P("ordering"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.Slider(P("start"), 0f, 1f, PropertyLabel("start"));
                BrazeEditorGUI.Slider(P("end"), 0f, 1f, PropertyLabel("end"));
                BrazeEditorGUI.PropertyField(P("offset"), PropertyLabel("offset"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(PropertyName("loop"), P("loop"));
                BrazeEditorGUI.ToggleButton(PropertyName("constantSpacing"), P("constantSpacing"));
                BrazeEditorGUI.IntSlider(P("pathSamples"), 8, 256, PropertyLabel("pathSamples"));
            });

            Card(key + "transform", PropertyPathPart("positionOnSpline", 1, "TRANSFORM"), () =>
            {
                BrazeEditorGUI.ToggleButton(PropertyName("positionOnSpline"), P("positionOnSpline"));
                if (P("positionOnSpline").boolValue)
                    BrazeEditorGUI.PropertyField(P("positionOffset"), PropertyLabel("positionOffset"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton(PropertyName("alignRotation"), P("alignRotation"));
                if (P("alignRotation").boolValue)
                    BrazeEditorGUI.PropertyField(P("rotationOffset"), PropertyLabel("rotationOffset"));
            });

            if (P("alignRotation").boolValue)
            {
                Card(key + "aim", PropertyPathPart("aimAxis", 1, "AIM"), () =>
                {
                    BrazeEditorGUI.SignedAxis(P("aimAxis"));
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PillsEnum(PropertyName("upMode"), P("upMode"));
                    if ((BrazeSplineEffector.UpMode)P("upMode").enumValueIndex == BrazeSplineEffector.UpMode.Custom)
                        BrazeEditorGUI.PropertyField(P("customUp"), PropertyLabel("customUp"));
                });
            }

            BrazeEditorGUI.Hint("Orders fragments across the selected range, moves them onto the existing Braze spline, " +
                                "and optionally aligns the chosen local axis to its tangent. Offset is animatable for travel effects.");
        }
    }

    [CustomEditor(typeof(BrazePushApartEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazePushApartEffectorEditor : BrazeFractureEffectorEditor
    {
        protected override string Title => "PUSH APART EFFECTOR";
        protected override string Icon => "pushapart";
        protected override bool ShowTransformSection => false;

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            Card(key + "separation", PropertyPathPart("direction", 1, "SEPARATION"), () =>
            {
                BrazeEditorGUI.PillsEnum(PropertyName("direction"), P("direction"));
                BrazeEditorGUI.ToggleButton("Use Clone Bounds", P("useCloneBounds"));
                bool useBounds = !P("useCloneBounds").hasMultipleDifferentValues && P("useCloneBounds").boolValue;
                BrazeEditorGUI.PropertyField(P("separationRadius"),
                    new GUIContent(useBounds ? "Bounds Padding" : "Radius", P("separationRadius").tooltip));
                BrazeEditorGUI.IntSlider(P("iterations"), 1, 32, PropertyLabel("iterations"));
                BrazeEditorGUI.Hint("Clone Bounds uses every source mesh's pivot radius and current clone scale, then adds padding around each clone. " +
                                    "A bounded GPU spatial grid finds real neighbours after earlier effectors. Fractures use Radius as their virtual contact sphere. " +
                                    "Cloners bake the separation once and reuse it. Editing this Push Apart effector or changing clone/source topology rebakes it.");
            });

            BrazeEditorGUI.Hint("Separates neighboring Voronoi fragments after all earlier effectors in the stack. " +
                                "The result is a direct position offset and never adds force or velocity.");
        }
    }

    [CustomEditor(typeof(BrazeShaderEffector))]
    [CanEditMultipleObjects]
    public sealed class BrazeShaderEffectorEditor : BrazeFractureEffectorEditor
    {
        const int PreviewResolution = 112;
        const float PreviewSize = 132f;
        Texture2D noisePreview;
        Color32[] previewPixels;
        GUIStyle previewLabelStyle;
        int previewHash = int.MinValue;
        double lastPreviewAt = -1.0;
        double nextInspectorRepaintAt;

        protected override string Title => "SHADER EFFECTOR";
        protected override string Icon => "shader";

        void OnEnable()
        {
            EditorApplication.update += UpdateAnimatedPreview;
        }

        void OnDisable()
        {
            EditorApplication.update -= UpdateAnimatedPreview;
            if (noisePreview != null) DestroyImmediate(noisePreview);
            noisePreview = null;
            previewPixels = null;
            previewLabelStyle = null;
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            if (!GUI.changed) return;
            previewHash = int.MinValue;
        }

        void UpdateAnimatedPreview()
        {
            if (target is not BrazeShaderEffector effector || !effector.Animating) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < nextInspectorRepaintAt) return;
            nextInspectorRepaintAt = now + 1.0 / 15.0;
            Repaint();
        }

        protected override void DrawFunctionFields(Func<string, SerializedProperty> P, string key)
        {
            var noiseType = (BrazeShaderEffector.NoiseType)P("noiseType").enumValueIndex;
            Card(key + "noise", PropertyPathPart("noiseType", 1, "NOISE"), () =>
            {
                DrawNoisePreview();
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.PillsEnum(PropertyName("noiseType"), P("noiseType"));
                BrazeEditorGUI.PropertyField(P("seed"), PropertyLabel("seed"));
                BrazeEditorGUI.Hint(noiseType switch
                {
                    BrazeShaderEffector.NoiseType.Soft => "Soft uses one smooth frequency for broad, simple variation.",
                    BrazeShaderEffector.NoiseType.Turbulence => "Turbulence folds every octave into broken, energetic regions.",
                    BrazeShaderEffector.NoiseType.Ridged => "Ridged concentrates values into sharper crests and veins.",
                    _ => "Fractal layers multiple frequencies into natural large-to-small variation.",
                });

                BrazeEditorGUI.SubHeader(PropertyPathPart("noiseSpace", 2, "COORDINATES"));
                BrazeEditorGUI.PillsEnum(PropertyName("noiseSpace"), P("noiseSpace"));
                BrazeEditorGUI.PropertyField(P("globalScale"), PropertyLabel("globalScale"));
                BrazeEditorGUI.PropertyField(P("relativeScale"), PropertyLabel("relativeScale"));
                BrazeEditorGUI.PropertyField(P("coordinateOffset"), PropertyLabel("coordinateOffset"));

                if (noiseType != BrazeShaderEffector.NoiseType.Soft)
                {
                    BrazeEditorGUI.SubHeader(PropertyPathPart("octaves", 2, "DETAIL"));
                    BrazeEditorGUI.IntSlider(P("octaves"), 1, 8, PropertyLabel("octaves"));
                    BrazeEditorGUI.Slider(P("roughness"), 0f, 1f, PropertyLabel("roughness"));
                    BrazeEditorGUI.Slider(P("lacunarity"), 1.01f, 4f, PropertyLabel("lacunarity"));
                }

                BrazeEditorGUI.SubHeader(PropertyPathPart("evolutionSpeed", 2, "ANIMATION"));
                BrazeEditorGUI.PropertyField(P("evolutionSpeed"), PropertyLabel("evolutionSpeed"));
                BrazeEditorGUI.PropertyField(P("movement"), PropertyLabel("movement"));
                BrazeEditorGUI.PropertyField(P("movementSpeed"), PropertyLabel("movementSpeed"));

                BrazeEditorGUI.SubHeader(PropertyPathPart("lowClip", 2, "OUTPUT"));
                BrazeEditorGUI.Slider(P("lowClip"), 0f, 1f, PropertyLabel("lowClip"));
                BrazeEditorGUI.Slider(P("highClip"), 0f, 1f, PropertyLabel("highClip"));
                BrazeEditorGUI.Slider(P("brightness"), -1f, 1f, PropertyLabel("brightness"));
                BrazeEditorGUI.Slider(P("contrast"), -1f, 1f, PropertyLabel("contrast"));
                BrazeEditorGUI.ToggleButton(PropertyName("absolute"), P("absolute"));
                BrazeEditorGUI.ToggleButton(PropertyName("invert"), P("invert"));
                BrazeEditorGUI.PropertyField(P("remap"), PropertyLabel("remap"));

                BrazeEditorGUI.Hint("Samples a procedural 3D noise value at every fragment, shapes it through the output controls, " +
                                    "then drives the shared Position, Rotation, and Scale channels. Reverse Low and High Clip for a clipped inversion.");
            });
        }

        void DrawNoisePreview()
        {
            var effector = target as BrazeShaderEffector;
            if (effector == null) return;
            EnsureNoisePreview(effector);

            Rect row = GUILayoutUtility.GetRect(PreviewSize, PreviewSize + 18f, GUILayout.ExpandWidth(true));
            float size = Mathf.Min(PreviewSize, row.width);
            Rect preview = new(row.x + (row.width - size) * 0.5f, row.y, size, PreviewSize);
            EditorGUI.DrawRect(new Rect(preview.x - 1f, preview.y - 1f, preview.width + 2f, preview.height + 2f),
                               new Color(0.04f, 0.04f, 0.045f));
            if (noisePreview != null)
                EditorGUI.DrawPreviewTexture(preview, noisePreview, null, ScaleMode.StretchToFill);
            previewLabelStyle ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            EditorGUI.LabelField(new Rect(preview.x, preview.yMax + 3f, preview.width, 15f),
                                 "XY SLICE  ·  WHITE = FULL EFFECT", previewLabelStyle);
        }

        void EnsureNoisePreview(BrazeShaderEffector effector)
        {
            if (noisePreview == null)
            {
                noisePreview = new Texture2D(PreviewResolution, PreviewResolution, TextureFormat.RGBA32, false, true)
                {
                    name = "BrazeShaderEffectorPreview",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                previewPixels = new Color32[PreviewResolution * PreviewResolution];
                previewHash = int.MinValue;
            }

            double now = EditorApplication.timeSinceStartup;
            int hash = effector.ContentHash();
            bool animationFrame = effector.Animating && now - lastPreviewAt >= 1.0 / 15.0;
            if (hash == previewHash && !animationFrame) return;

            float time = Application.isPlaying ? Time.time : (float)now;
            for (int y = 0; y < PreviewResolution; y++)
            {
                float v = y / (float)(PreviewResolution - 1);
                for (int x = 0; x < PreviewResolution; x++)
                {
                    float u = x / (float)(PreviewResolution - 1);
                    byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(effector.SamplePreview(new Vector2(u, v), time)) * 255f);
                    previewPixels[y * PreviewResolution + x] = new Color32(value, value, value, 255);
                }
            }
            noisePreview.SetPixels32(previewPixels);
            noisePreview.Apply(false, false);
            previewHash = hash;
            lastPreviewAt = now;
        }
    }

    /// <summary>Invalidates cached kernel indices when the Voronoi compute asset is hot-reimported.</summary>
    sealed class BrazeComputeShaderReloadWatcher : AssetPostprocessor
    {
        const string VoronoiComputePath = "Assets/BrazeFX/Resources/BrazeVoronoi.compute";

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                                           string[] movedAssets, string[] movedFromAssetPaths)
        {
            for (int i = 0; i < importedAssets.Length; i++)
            {
                if (importedAssets[i] != VoronoiComputePath) continue;
                EditorApplication.delayCall += RefreshActiveFractures;
                return;
            }
        }

        static void RefreshActiveFractures()
        {
            var fractures = BrazeVoronoiFracture.Active;
            for (int i = 0; i < fractures.Count; i++)
                if (fractures[i] != null)
                    fractures[i].InvalidateComputeKernels();

            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
