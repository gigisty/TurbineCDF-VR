using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Premium IMGUI inspector for <see cref="BrazeParticleSystem"/>, adapted from the publisher-owned
    /// three-js-particles prototype: grouped sub-card panels (SYSTEM / SPAWN / MOTION / RENDERING) with
    /// enable toggles, controls GATED to the active selection, the CONST / RANGE / CURVE value widget,
    /// and a draggable Scene-view Braze Stats HUD.
    ///
    /// Done: Renderer, over-life modules (velocity/size/opacity/colour + colour-by-speed), Collision.
    /// Still to come: Events / Bursts (sub-emitter spawn on birth/death/collision).
    /// </summary>
    [CustomEditor(typeof(BrazeParticleSystem))]
    [CanEditMultipleObjects]
    public sealed class BrazeParticleSystemEditor : Editor
    {
        const string K = "BrazeFX.Particles.";
        static readonly string[] ModeShort = { "Const", "Range", "Curve" };
        static readonly string[] ModeShortInf = { "Const", "Range", "Curve", "Inf" };   // Lifetime adds Infinite
        static readonly int[] CapacityValues = { 100_000, 500_000, 1_000_000, 3_000_000, 5_000_000 };
        static readonly string[] CapacityLabels = { "100K", "500K", "1M", "3M", "5M" };
        // Sub-emitters are capped at 10K — locked choices to keep chain reactions bounded.
        static readonly int[] SubCapValues = { 1_000, 2_500, 5_000, 10_000 };
        static readonly string[] SubCapLabels = { "1K", "2.5K", "5K", "10K" };

        const double PreviewInterval = 1.0 / 60.0;   // never dispatch faster than a 60 Hz Scene preview
        bool previewPlaying;
        float previewTime;
        int previewFrame;
        double lastPreviewTick;

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            StopPreview();
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) StopPreview();
        }

        void PlayPreview()
        {
            if (Application.isPlaying || previewPlaying) return;
            previewPlaying = true;
            lastPreviewTick = EditorApplication.timeSinceStartup;
            EditorApplication.update -= TickPreview;
            EditorApplication.update += TickPreview;
            RenderPipelineManager.beginCameraRendering -= RenderPreviewCamera;
            RenderPipelineManager.beginCameraRendering += RenderPreviewCamera;
            SceneView.RepaintAll();
        }

        void PausePreview()
        {
            previewPlaying = false;
            EditorApplication.update -= TickPreview;
            if (!Application.isPlaying && target is BrazeParticleSystem sys && sys != null)
                sys.EditorPreviewPause();
            SceneView.RepaintAll();
        }

        void RestartPreview()
        {
            StopPreview();
            PlayPreview();
        }

        void StopPreview()
        {
            previewPlaying = false;
            EditorApplication.update -= TickPreview;
            RenderPipelineManager.beginCameraRendering -= RenderPreviewCamera;

            if (!Application.isPlaying && target is BrazeParticleSystem sys && sys != null)
                sys.EditorPreviewStop();

            previewTime = 0f;
            previewFrame = 0;
            SceneView.RepaintAll();
        }

        // Immediate/indirect draws exist for one camera frame only. The simulation tick and Scene-view render loop
        // are independent in edit mode, so submitting the mesh from TickPreview made it alternate between present
        // and absent frames (and made a paused preview disappear). Resubmit the frozen/latest GPU state for every
        // Scene camera render; TickPreview remains responsible only for advancing simulation at the capped rate.
        void RenderPreviewCamera(ScriptableRenderContext context, Camera camera)
        {
            if (Application.isPlaying || camera == null || camera.cameraType != CameraType.SceneView) return;
            if (target is BrazeParticleSystem sys && sys != null && sys.isActiveAndEnabled && sys.Ready)
                sys.EditorPreviewRender(camera);
        }

        void TickPreview()
        {
            if (!previewPlaying || Application.isPlaying || EditorApplication.isCompiling)
            {
                if (Application.isPlaying) PausePreview();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - lastPreviewTick;
            if (elapsed < PreviewInterval) return;
            lastPreviewTick = now;

            float dt = Mathf.Clamp((float)elapsed, 0f, 1f / 20f);
            var sys = target as BrazeParticleSystem;
            if (sys == null || !sys.isActiveAndEnabled)
            {
                StopPreview();
                return;
            }
            sys.EditorPreviewStep(dt, previewTime, previewFrame);

            previewTime += dt;
            previewFrame++;
            SceneView.RepaintAll();
        }

        static int NearestIndex(int[] vals, int v)
        {
            int best = 0, bd = Mathf.Abs(v - vals[0]);
            for (int i = 1; i < vals.Length; i++) { int d = Mathf.Abs(v - vals[i]); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        static int NearestCapacityIndex(int v)
        {
            int best = 0;
            int bd = Mathf.Abs(v - CapacityValues[0]);
            for (int i = 1; i < CapacityValues.Length; i++)
            {
                int d = Mathf.Abs(v - CapacityValues[i]);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var sys = (BrazeParticleSystem)target;
            bool sub = sys.isSubEmitter;
            bool debrisTarget = !sub && (sys.IsSurfaceDisintegratorDebrisTarget || sys.IsVoronoiFractureDebrisTarget);
            BrazePyroEmitter pyroMaster = null;
            bool emitterTarget = !sub && !debrisTarget && BrazePyroEmitter.TryGetParticleSource(sys, out pyroMaster);
            var secondaryMaster = debrisTarget ? FindSecondaryMaster(sys) : null;
            string headerSub = sub ? "SUB-EMITTER"
                             : debrisTarget ? (secondaryMaster != null ? "SECONDARY · " + secondaryMaster.name : "SECONDARY PARTICLES")
                             : emitterTarget ? (pyroMaster != null ? "SLAVE · " + pyroMaster.name : "SLAVE EMITTER") : "PARTICLES";
            BrazeEditorGUI.BrandHeader(headerSub);
            if (emitterTarget) DrawPyroMasterBanner(pyroMaster);

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (sub)
            {
                // ============================ SUB-EMITTER ============================
                bool gSub = BrazeEditorGUI.GroupBegin(K + "g_sub", "SUB-EMITTER", 1);
                if (gSub && BrazeEditorGUI.Section(K + "subcap", "Buffer"))
                {
                    var capS = P("capacity");
                    int curC = System.Array.IndexOf(SubCapValues, capS.intValue);
                    if (curC < 0) { curC = NearestIndex(SubCapValues, capS.intValue); capS.intValue = SubCapValues[curC]; }
                    int selC = BrazeEditorGUI.Segmented("Capacity", SubCapLabels, curC);
                    if (selC != curC) capS.intValue = SubCapValues[selC];
                    BrazeEditorGUI.Hint("Capped at 10K to keep chain reactions safe. Driven by the parent event — position, count & timing come from there.");
                }
                if (gSub) BrazeEditorGUI.EndSection();
                BrazeEditorGUI.GroupEnd(gSub);
            }
            else if (debrisTarget)
            {
                // ============================ SECONDARY PARTICLES (request-driven) ============================
                DrawSecondaryMasterBanner(secondaryMaster);
                bool gDebris = BrazeEditorGUI.GroupBegin(K + "g_debris_target", "SECONDARY PARTICLES", 1);
                if (gDebris && BrazeEditorGUI.Section(K + "debrisbuf", "Buffer"))
                {
                    var capP = P("capacity");
                    int cur = System.Array.IndexOf(CapacityValues, capP.intValue);
                    if (cur < 0) { cur = NearestCapacityIndex(capP.intValue); capP.intValue = CapacityValues[cur]; }
                    int sel = BrazeEditorGUI.Segmented("Capacity", CapacityLabels, cur);
                    if (sel != cur) capP.intValue = CapacityValues[sel];

                    EditorGUI.BeginChangeCheck();
                    float ts = BrazeEditorGUI.Slider("Time Scale", P("timeScale").floatValue, 0f, 2f);
                    if (EditorGUI.EndChangeCheck()) P("timeScale").floatValue = ts;

                    string masterKind = secondaryMaster is BrazeVoronoiFracture ? "Voronoi Fracture"
                                      : secondaryMaster is BrazeSurfaceDisintegrator ? "Surface Disintegrator"
                                      : "master effect";
                    BrazeEditorGUI.Hint($"Controlled by a {masterKind}. Its own Rate, Burst, Shape, and shape gizmo are " +
                                        "disabled; it only drains GPU spawn requests from the master. Lifetime, Motion, " +
                                        "Renderer and over-life modules below still apply.");
                }
                if (gDebris) BrazeEditorGUI.EndSection();
                BrazeEditorGUI.GroupEnd(gDebris);
            }
            else
            {

            // ============================ SYSTEM ============================
            bool gSystem = BrazeEditorGUI.GroupBegin(K + "g_system", "SYSTEM", 1);
            if (gSystem)
            {
                if (BrazeEditorGUI.Section(K + "system", "System"))
                {
                    using (new EditorGUI.DisabledScope(emitterTarget))
                    {
                        BrazeEditorGUI.PropertyField(P("duration"));
                        BrazeEditorGUI.ToggleButton("Looping", P("looping"));
                    }
                    if (emitterTarget)
                        BrazeEditorGUI.Hint("Duration / Looping are driven by the connected Braze Pyro Emitter — this system spawns only while it emits (its Burst windows included).");
                    EditorGUI.BeginChangeCheck();
                    float ts = BrazeEditorGUI.Slider("Time Scale", P("timeScale").floatValue, 0f, 2f);
                    if (EditorGUI.EndChangeCheck()) P("timeScale").floatValue = ts;
                    BrazeEditorGUI.PillsEnum("Sim Space", P("simSpace"));
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(gSystem);

            // ============================ SPAWN ============================
            bool gSpawn = BrazeEditorGUI.GroupBegin(K + "g_spawn", "SPAWN", 2);
            if (gSpawn)
            {
                if (BrazeEditorGUI.Section(K + "emission", "Emission"))
                {
                    BrazeEditorGUI.PropertyField(P("rate"), new GUIContent("Rate / sec"));
                    if (emitterTarget)
                        BrazeEditorGUI.Hint("Slave timing mirrors the master: particles spawn at this Rate exactly while the " +
                                            "Braze Pyro Emitter emits, and stop when it stops. Pulsing / intervals come from the " +
                                            "master's own Burst — the slave has none.");

                    var capP = P("capacity");
                    int cur = System.Array.IndexOf(CapacityValues, capP.intValue);
                    if (cur < 0) { cur = NearestCapacityIndex(capP.intValue); capP.intValue = CapacityValues[cur]; }
                    int sel = BrazeEditorGUI.Segmented("Capacity", CapacityLabels, cur);
                    if (sel != cur) capP.intValue = CapacityValues[sel];

                    // Burst is a self-emission feature; a slave's intervals come from its master, so it's hidden here.
                    if (!emitterTarget)
                    {
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.ToggleButton("Burst", P("burst"));
                        if (P("burst").boolValue)
                        {
                            IntRangeField("Count", P("burstCount"));
                            IntRangeField("Start Frame", P("burstStart"));
                            BrazeEditorGUI.ToggleButton("Loop", P("burstLoop"));
                            if (P("burstLoop").boolValue)
                            {
                                BrazeEditorGUI.PropertyField(P("burstLoops"), new GUIContent("Loops (0 = ∞)"));
                                IntRangeField("Loop Delay", P("burstLoopDelay"));
                            }
                            BrazeEditorGUI.Hint("Spawns Count particles instantly at each burst, ON TOP of continuous Rate (Rate = 0 for burst-only). Count / Start / Delay are random between their min..max.");
                        }
                    }
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "shape", "Shape"))
                {
                    if (emitterTarget)
                    {
                        BrazeEditorGUI.Hint("Spawn shape is supplied by the connected Braze Pyro Emitter — particles are born inside ITS footprint (rotation and Shape Axis included; a Mesh emitter spawns them on its surface / flying fragments). This system keeps its Rate, Burst, Lifetime, Motion and Renderer settings; its own Shape controls are ignored.");
                        if (pyroMaster != null && pyroMaster.shape == BrazePyroEmitter.Shape.Mesh)
                        {
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.Slider(P("shapeInheritVelocity"), -2f, 2f,
                                new GUIContent("Source Motion", "Negative = trail against motion, 0 = detached, positive = follow source motion."));
                            BrazeEditorGUI.Hint("Controls the Pyro mesh / fragment motion transferred at birth. " +
                                                "Use 0 for a natural world-space trail, or a small negative value " +
                                                "for stronger counter-motion lag.");
                        }
                    }
                    else
                    {
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("shape"), new GUIContent("Kind"));
                        BrazeEditorGUI.Separator();

                        var shape = (BrazeParticleSystem.EmitShape)P("shape").enumValueIndex;
                        bool radial = shape is BrazeParticleSystem.EmitShape.Sphere
                                             or BrazeParticleSystem.EmitShape.Hemisphere
                                             or BrazeParticleSystem.EmitShape.Cone
                                             or BrazeParticleSystem.EmitShape.Circle
                                             or BrazeParticleSystem.EmitShape.Torus;

                        if (radial) BrazeEditorGUI.PropertyField(P("shapeRadius"), new GUIContent("Radius"));
                        if (shape == BrazeParticleSystem.EmitShape.Cone)
                            BrazeEditorGUI.PropertyField(P("coneAngle"), new GUIContent("Angle"));
                        if (shape == BrazeParticleSystem.EmitShape.Torus)
                            BrazeEditorGUI.PropertyField(P("torusThickness"), new GUIContent("Thickness"));
                        if (shape == BrazeParticleSystem.EmitShape.Box)
                            BrazeEditorGUI.PropertyField(P("boxSize"), new GUIContent("Box Size"));
                        if (shape is BrazeParticleSystem.EmitShape.Plane or BrazeParticleSystem.EmitShape.Terrain)
                            BrazeEditorGUI.PropertyField(P("planeSize"), new GUIContent("Size"));
                        if (shape == BrazeParticleSystem.EmitShape.Terrain)
                        {
                            BrazeEditorGUI.PropertyField(P("terrainHeight"), new GUIContent("Height"));
                            BrazeEditorGUI.PropertyField(P("terrainFreq"), new GUIContent("Frequency"));
                        }
                        if (radial)
                            BrazeEditorGUI.ToggleButton("From Shell", P("emitFromShell"));
                        if (shape == BrazeParticleSystem.EmitShape.Mesh)
                        {
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.PropertyField(P("shapeMesh"), new GUIContent("Mesh"));
                            BrazeEditorGUI.PropertyField(P("meshEmitSource"), new GUIContent("Spawn From"));
                            var srcProp = P("meshEmitSource");
                            if (srcProp.enumValueIndex != (int)BrazeParticleSystem.MeshEmitSource.Vertices)
                            {
                                BrazeEditorGUI.PropertyField(P("meshScatterCount"), new GUIContent("Scatter Count"));
                                BrazeEditorGUI.Hint(srcProp.enumValueIndex == (int)BrazeParticleSystem.MeshEmitSource.Edges
                                    ? "Scatters spawn points evenly along the mesh's edges (length-weighted). Higher Scatter Count = finer."
                                    : "Scatters spawn points evenly across the faces (area-weighted), along the surface normals. Higher Scatter Count = finer, more uniform.");
                            }
                            else BrazeEditorGUI.Hint("Spawns at the mesh's vertices (clumps where the topology is denser).");

                            // MASK — explicit slot wins; empty slot falls back to a mask on the mesh renderer object.
                            var pMesh = P("shapeMesh").objectReferenceValue as Renderer;
                            var autoMask = pMesh != null ? pMesh.GetComponent<BrazeSurfaceMask>() : null;
                            var explicitMask = P("mask").objectReferenceValue as BrazeSurfaceMask;
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.ToggleButton("Use Surface Mask", P("useMask"));
                            using (new EditorGUI.DisabledScope(!P("useMask").boolValue))
                            {
                                BrazeEditorGUI.PropertyField(P("mask"), new GUIContent("Mask", "Optional explicit Braze Surface Mask. Leave empty to auto-use a mask on the Mesh object."));
                                if (explicitMask == null)
                                {
                                    using (new EditorGUI.DisabledScope(true))
                                        EditorGUILayout.ObjectField(new GUIContent("Auto Mask"), autoMask, typeof(BrazeSurfaceMask), true);
                                }
                                if (explicitMask == null && autoMask == null)
                                    EditorGUILayout.HelpBox("Assign a Braze Surface Mask here, or add one to the Mesh object for auto-detection.", MessageType.Info);
                                else
                                    BrazeEditorGUI.Hint("Particles spawn only from the masked region. The Surface Mask's Projection, Surface Spacing and Max Points define the candidate surface resolution, so masked spawning does not depend on mesh vertex density.");
                            }
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.Slider(P("shapeInheritVelocity"), -2f, 2f,
                                new GUIContent("Source Motion", "Negative = trail against motion, 0 = detached, positive = follow source motion."));
                            BrazeEditorGUI.Hint("Controls source motion only at birth. Negative values add counter-motion " +
                                                "lag, 0 leaves particles naturally behind in world space, and positive " +
                                                "values carry them with the moving / animating mesh. -1 / 0 / 1 = Trail / Detached / Follow.");
                        }
                        if (shape == BrazeParticleSystem.EmitShape.Cloner)
                        {
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.PropertyField(P("shapeCloner"), new GUIContent("Cloner"));
                            BrazeEditorGUI.Slider(P("shapeInheritVelocity"), -2f, 2f,
                                new GUIContent("Source Motion", "Negative = trail against motion, 0 = detached, positive = follow source motion."));

                            var cloner = P("shapeCloner").objectReferenceValue as BrazeCloner;
                            if (cloner == null)
                                EditorGUILayout.HelpBox("Assign a Braze Cloner to emit from its clones.", MessageType.Info);
                            else
                                BrazeEditorGUI.Hint($"Emits from {cloner.InstanceCount:N0} final GPU clone positions. " +
                                    "Distribution, alignment and effectors are inherited; each clone's local +Y sets " +
                                    "the emission direction. Source Motion: negative trails against animated clone " +
                                    "motion, 0 detaches, and positive follows it.");
                        }
                    }
                }
                BrazeEditorGUI.EndSection();

            }
            BrazeEditorGUI.GroupEnd(gSpawn);
            } // end standalone (SYSTEM + SPAWN)

            // ============================ MOTION ============================
            bool gMotion = BrazeEditorGUI.GroupBegin(K + "g_motion", "MOTION", 4);
            if (gMotion)
            {
                if (BrazeEditorGUI.Section(K + "particle", "Particle"))
                {
                    MinMaxField("Lifetime", P("startLifetime"), 0f, 10f, P("infiniteLifetime"));
                    MinMaxField("Speed", P("startSpeed"), 0f, 20f);
                    MinMaxField("Size", P("startSize"), 0f, 2f);
                    MinMaxField("Rotation", P("startRotation"), 0f, 6.2832f);
                }
                BrazeEditorGUI.EndSection();

                ModuleSection(K + "vol", "Velocity over Lifetime", P("velocityOverLifetime"), () =>
                {
                    MinMaxField("X", P("velocityX"), -5f, 5f);
                    MinMaxField("Y", P("velocityY"), -5f, 5f);
                    MinMaxField("Z", P("velocityZ"), -5f, 5f);
                });

                ModuleSection(K + "collision", "Collision", P("collisionEnabled"), () =>
                {
                    BrazeEditorGUI.PropertyField(P("collisionRadiusScale"), new GUIContent("Radius Scale"));
                    BrazeEditorGUI.Hint("Particle contact radius = size × this (green sphere). Add Braze Particle Collider components (Plane / Sphere / Box) to the scene to collide against.");

                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.ToggleButton("Sleep at Rest", P("collisionSleep"));
                    if (P("collisionSleep").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("collisionSleepSpeed"), new GUIContent("Rest Threshold"));
                        BrazeEditorGUI.PropertyField(P("collisionSleepDelay"), new GUIContent("Sleep Delay"));
                        BrazeEditorGUI.Hint("Suppresses low-speed bounce under steady gravity, then sleeps residual motion. Tangential/outward forces wake particles automatically.");
                    }

                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.ToggleButton("Self Collision", P("separationEnabled"));
                    if (P("separationEnabled").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("separationScale"), new GUIContent("Spacing"));
                        BrazeEditorGUI.PropertyField(P("separationStiffness"), new GUIContent("Stiffness"));
                        BrazeEditorGUI.PropertyField(P("separationIterations"), new GUIContent("Iterations"));
                        BrazeEditorGUI.Hint("Particles push each other apart so piles/stacks read as solid instead of all merging into one spot. Cost scales with how densely particles clump.");
                    }

                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.ToggleButton("Show Gizmo", P("showCollisionGizmo"));
                });

                if (BrazeEditorGUI.Section(K + "forces", "Forces"))
                {
                    DrawForcesList(P("forces"));
                    BrazeEditorGUI.Hint("Only the forces dropped here affect this particle system. A force placed elsewhere in the hierarchy is inert until listed by a system.");
                }
                BrazeEditorGUI.EndSection();

            }
            BrazeEditorGUI.GroupEnd(gMotion);

            // ============================ RENDERING ============================
            bool gRender = BrazeEditorGUI.GroupBegin(K + "g_rendering", "RENDERING", 5);
            if (gRender)
            {

                if (BrazeEditorGUI.Section(K + "renderer", "Renderer"))
                {
                    BrazeEditorGUI.PillsEnum("Render Shape", P("renderMode"));
                    var rm = (BrazeParticleSystem.RenderMode)P("renderMode").enumValueIndex;
                    if (rm == BrazeParticleSystem.RenderMode.None)
                    {
                        BrazeEditorGUI.Hint("None = particles simulate but don't render.");
                    }
                    else if (rm == BrazeParticleSystem.RenderMode.Mesh)
                    {
                        // Mesh mode: 3D meshes drawn per particle (random pick), opaque + lit + depth-sorted.
                        BrazeEditorGUI.PillsEnum("Alignment", P("facing"));
                        BrazeEditorGUI.Hint("Alignment = Velocity aligns each mesh's +Y to its motion; any other Alignment " +
                                            "gives a stable random tumble. Meshes render OPAQUE — solid, depth-sorted, and " +
                                            "they receive main-light shadows (Blend modes don't apply).");
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("meshes"), new GUIContent("Meshes"), true);
                        BrazeEditorGUI.Hint("One entry per mesh, drawn one-per-particle (random pick). Each entry has an " +
                                            "optional per-mesh albedo (sampled via the mesh UV0; empty → Default Texture, " +
                                            "else white). Read/Write is not required. Triangle submeshes are supported.");
                        BrazeEditorGUI.Slider(P("meshLightStrength"), 0f, 1f, new GUIContent("Light"));
                        BrazeEditorGUI.PropertyField(P("meshScale"), new GUIContent("Scale"));
                        BrazeEditorGUI.PropertyField(P("meshSpin"), new GUIContent("Spin (rad/s)"));
                        BrazeEditorGUI.PropertyField(P("meshSpinBySpeed"), new GUIContent("Spin by Speed"));
                        BrazeEditorGUI.Hint("Size follows Start Size × Size over Lifetime; Scale is an extra multiplier. " +
                                            "Spin integrates continuously; Spin by Speed adds only the current frame's motion, " +
                                            "and both pause at collision sleep. Velocity alignment blends to a stable resting orientation.");
                        BrazeEditorGUI.ToggleButton("Cast Shadows", P("meshCastShadows"));
                        if (P("meshCastShadows").boolValue)
                            BrazeEditorGUI.Hint("EXPENSIVE at high counts — geometry is re-rendered per shadow cascade " +
                                                "(up to ~5×). Leave off for many small meshes; on only for a few hero meshes.");
                        BrazeEditorGUI.ToggleButton("Receive Shadows", P("meshReceiveShadows"));
                        if (P("meshReceiveShadows").boolValue)
                        {
                            BrazeEditorGUI.Slider(P("meshShadowStrength"), 0f, 1f, new GUIContent("Shadow Strength"));
                            BrazeEditorGUI.Hint("A dense cloud samples a shadow map full of OTHER particles, so " +
                                                "inter-particle shadowing accumulates with density and crushes to black over time. " +
                                                "Shadow Strength caps that (lower = brighter floor) while keeping the self-shadow " +
                                                "shaping. The Lambert + ambient 3D form is unaffected.");
                        }
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.ToggleButton("Frustum Cull", P("meshFrustumCull"));
                        if (P("meshFrustumCull").boolValue)
                        {
                            BrazeEditorGUI.Slider(P("meshCullRadiusScale"), 0.1f, 4f, new GUIContent("Cull Margin"));
                            BrazeEditorGUI.Hint("Only renders meshes the camera can see, so cost scales with what's on-screen. " +
                                                "Culls against the main camera — Scene-view meshes may pop in Play mode; turn off while authoring.");
                        }
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("meshDefaultTexture"),
                            new GUIContent("Default Texture", "Mesh-only albedo used for entries that don't set their own. Empty = white."));
                        BrazeEditorGUI.Hint("Mesh textures are independent from Sprite Custom/Flipbook settings. Nothing is inherited when Render Shape changes.");
                        BrazeEditorGUI.PropertyField(P("startColor"), new GUIContent("Start Color"));
                        BrazeEditorGUI.PropertyField(P("tint"));
                        BrightnessRangeField(P("emissiveGainMin"), P("emissiveGain"));
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("alphaCutoff"), new GUIContent("Alpha Cutoff"));
                    }
                    else
                    {
                        // Alignment applies to Sprite (full billboard) and Stretched (the streak's facing plane).
                        // Flat / Upright are fixed-axis orientations, so it's not shown for them.
                        if (rm == BrazeParticleSystem.RenderMode.Sprite || rm == BrazeParticleSystem.RenderMode.Stretched)
                        {
                            BrazeEditorGUI.PillsEnum("Alignment", P("facing"));
                            if (rm == BrazeParticleSystem.RenderMode.Stretched)
                                BrazeEditorGUI.Hint("Stretched always elongates along velocity. Alignment sets the facing plane: " +
                                                    "Camera = billboard streak (faces you); World/Local = streak lies in a fixed plane.");
                        }
                        BrazeEditorGUI.PillsEnum("Blend", P("blendMode"));
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("texture"), new GUIContent("Texture"));
                        BrazeEditorGUI.PropertyField(P("customTexture"), new GUIContent("Custom"));
                        BrazeEditorGUI.PropertyField(P("startColor"), new GUIContent("Start Color"));
                        BrazeEditorGUI.PropertyField(P("tint"));
                        BrightnessRangeField(P("emissiveGainMin"), P("emissiveGain"));
                        if (rm == BrazeParticleSystem.RenderMode.Stretched)
                        {
                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.PropertyField(P("stretchVelocity"), new GUIContent("Stretch Velocity"));
                            BrazeEditorGUI.PropertyField(P("stretchLength"), new GUIContent("Stretch Length"));
                            BrazeEditorGUI.Slider(P("stretchVariation"), 0f, 1f, new GUIContent("Stretch Variation"));
                        }
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.PropertyField(P("pivot"));
                        BrazeEditorGUI.PropertyField(P("flip"), new GUIContent("Flip (prob.)"));
                        BrazeEditorGUI.PropertyField(P("alphaCutoff"), new GUIContent("Alpha Cutoff"));
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.ToggleButton("Flipbook", P("flipbook"));
                        if (P("flipbook").boolValue)
                        {
                            BrazeEditorGUI.PropertyField(P("flipbookColumns"), new GUIContent("Columns"));
                            BrazeEditorGUI.PropertyField(P("flipbookRows"), new GUIContent("Rows"));
                            BrazeEditorGUI.PropertyField(P("flipbookAnimation"), new GUIContent("Animation"));

                            var animation = (BrazeParticleSystem.FlipbookAnimation)P("flipbookAnimation").enumValueIndex;
                            if (animation == BrazeParticleSystem.FlipbookAnimation.SingleRow)
                            {
                                BrazeEditorGUI.PropertyField(P("flipbookRowMode"), new GUIContent("Row Mode"));
                                if ((BrazeParticleSystem.FlipbookRowMode)P("flipbookRowMode").enumValueIndex
                                    == BrazeParticleSystem.FlipbookRowMode.Custom)
                                {
                                    int maxRow = Mathf.Max(0, P("flipbookRows").intValue - 1);
                                    BrazeEditorGUI.IntSlider(P("flipbookRow"), 0, maxRow, new GUIContent("Row"));
                                }
                            }

                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.PropertyField(P("flipbookTimeMode"), new GUIContent("Time Mode"));
                            var timeMode = (BrazeParticleSystem.FlipbookTimeMode)P("flipbookTimeMode").enumValueIndex;
                            if (timeMode == BrazeParticleSystem.FlipbookTimeMode.FPS)
                            {
                                BrazeEditorGUI.PropertyField(P("flipbookFps"), new GUIContent("Frames / Sec"));
                                BrazeEditorGUI.PropertyField(P("flipbookPlayback"), new GUIContent("Playback"));
                            }
                            else if (timeMode == BrazeParticleSystem.FlipbookTimeMode.Lifetime)
                            {
                                BrazeEditorGUI.PropertyField(P("flipbookPlayback"), new GUIContent("Playback"));
                                if ((BrazeParticleSystem.FlipbookPlayback)P("flipbookPlayback").enumValueIndex
                                    != BrazeParticleSystem.FlipbookPlayback.Once)
                                    BrazeEditorGUI.PropertyField(P("flipbookCycles"), new GUIContent("Cycles"));
                            }
                            else
                            {
                                var speed = P("flipbookSpeedRange");
                                Vector2 range = speed.vector2Value;
                                EditorGUILayout.BeginHorizontal();
                                GUILayout.Label("Speed Range", GUILayout.Width(EditorGUIUtility.labelWidth - 2));
                                EditorGUI.BeginChangeCheck();
                                range.x = BrazeEditorGUI.DelayedFloatField(range.x);
                                GUILayout.Label("to", GUILayout.Width(16));
                                range.y = BrazeEditorGUI.DelayedFloatField(range.y);
                                if (EditorGUI.EndChangeCheck())
                                    speed.vector2Value = new Vector2(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
                                EditorGUILayout.EndHorizontal();
                            }

                            int sequenceFrames = animation == BrazeParticleSystem.FlipbookAnimation.SingleRow
                                ? Mathf.Max(1, P("flipbookColumns").intValue)
                                : Mathf.Max(1, P("flipbookColumns").intValue * P("flipbookRows").intValue);
                            BrazeEditorGUI.ToggleButton("Random Start Frame", P("flipbookRandomStart"));
                            using (new EditorGUI.DisabledScope(P("flipbookRandomStart").boolValue))
                                BrazeEditorGUI.IntSlider(P("flipbookStartFrame"), 0, sequenceFrames - 1,
                                    new GUIContent("Start Frame"));

                            if (timeMode == BrazeParticleSystem.FlipbookTimeMode.FPS)
                                BrazeEditorGUI.Hint("FPS playback uses seconds alive, independent of particle lifetime. Once disappears after its final frame; Loop repeats; Ping Pong reverses; Hold Last remains visible.");
                            else if (timeMode == BrazeParticleSystem.FlipbookTimeMode.Lifetime)
                                BrazeEditorGUI.Hint("Lifetime maps the sequence across normalized particle age. Use FPS when the animation must keep a fixed speed as lifetime changes.");
                            else
                                BrazeEditorGUI.Hint("Speed selects a frame from the particle's current speed; it does not advance as a separate clock.");
                            if (rm != BrazeParticleSystem.RenderMode.Stretched)
                                BrazeEditorGUI.Hint("Flipbook selects atlas frames. Use Render Shape = Stretched to elongate those frames by particle velocity.");

                            BrazeEditorGUI.Separator();
                            BrazeEditorGUI.ToggleButton("Opacity over Flipbook", P("flipbookOpacityOverTime"));
                            if (P("flipbookOpacityOverTime").boolValue)
                            {
                                MinMaxField("Alpha", P("flipbookOpacityCurve"), 0f, 1f);
                                BrazeEditorGUI.Hint("Maps alpha across the flipbook's own cycle and multiplies texture, Start Color, and lifetime alpha. For a smooth Once fade, end the curve at 0 and keep Alpha Cutoff at 0.");
                            }
                        }
                        BrazeEditorGUI.ToggleButton("Soft Particles", P("softParticles"));
                        if (P("softParticles").boolValue)
                            BrazeEditorGUI.PropertyField(P("softFade"), new GUIContent("Fade Distance"));
                    }
                }
                BrazeEditorGUI.EndSection();

                ModuleSection(K + "sol", "Size over Lifetime", P("sizeOverLifetime"), () =>
                {
                    MinMaxField("Size", P("sizeCurve"), 0f, 2f);
                });

                ModuleSection(K + "ool", "Opacity over Lifetime", P("opacityOverLifetime"), () =>
                {
                    MinMaxField("Alpha", P("opacityCurve"), 0f, 1f);
                });

                ModuleSection(K + "col", "Color over Lifetime", P("colorOverLifetime"), () =>
                {
                    BrazeEditorGUI.PropertyField(P("colorGradient"), new GUIContent("Gradient"));
                    BrazeEditorGUI.Hint("Multiplies the Start Color over age. Author a hot→cool ramp; alpha combines with Opacity.");
                });

                ModuleSection(K + "cbs", "Color by Speed", P("colorBySpeed"), () =>
                {
                    BrazeEditorGUI.PropertyField(P("colorBySpeedGradient"), new GUIContent("Gradient"));

                    var rng = P("colorBySpeedRange");
                    Vector2 v = rng.vector2Value;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Speed Range", GUILayout.Width(EditorGUIUtility.labelWidth - 2));
                    EditorGUI.BeginChangeCheck();
                    v.x = BrazeEditorGUI.DelayedFloatField(v.x, GUILayout.Width(56));
                    EditorGUILayout.MinMaxSlider(ref v.x, ref v.y, 0f, 20f);
                    v.y = BrazeEditorGUI.DelayedFloatField(v.y, GUILayout.Width(56));
                    if (EditorGUI.EndChangeCheck())
                        rng.vector2Value = new Vector2(Mathf.Min(v.x, v.y), Mathf.Max(v.x, v.y));
                    EditorGUILayout.EndHorizontal();
                    BrazeEditorGUI.Hint("Maps speed (min→max) to the gradient, then multiplies the color in.");
                });
            }
            BrazeEditorGUI.GroupEnd(gRender);

            // ============================ EVENTS ============================
            bool gEvents = BrazeEditorGUI.GroupBegin(K + "g_events", "EVENTS", 1);
            if (gEvents)
            {
                SpawnEventSection(K + "onDeath", "On Death", P("onDeath"));
                SpawnEventSection(K + "onCollision", "On Collision", P("onCollision"));
            }
            BrazeEditorGUI.GroupEnd(gEvents);

            // Debug (ungrouped).
            if (BrazeEditorGUI.Section(K + "debug", "Debug"))
            {
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
                BrazeEditorGUI.Separator();
                BrazeEditorGUI.ToggleButton("Show Stats", P("showStats"));
                BrazeEditorGUI.Hint("Braze Stats overlay (Play mode or Scene preview): GPU memory, alive count, per-frame dispatch cost, FPS. Off = zero stats-readback cost.");
            }
            BrazeEditorGUI.EndSection();

            if (serializedObject.ApplyModifiedProperties())
                sys.EditorPropertiesChanged();
        }

        // ---------------- Braze Stats — Scene-view HUD (not a game-view overlay) ----------------
        static GUIStyle hudTitle, hudArrow, hudLive, hudDim, hudVal;

        static void EnsureHud()
        {
            if (hudTitle != null) return;
            hudTitle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            hudTitle.normal.textColor = new Color(1f, 0.52f, 0.22f);
            hudArrow = new GUIStyle { fontSize = 8, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            hudArrow.normal.textColor = new Color(0.85f, 0.62f, 0.4f);
            hudLive = new GUIStyle { fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            hudLive.normal.textColor = new Color(0.4f, 0.92f, 0.5f);
            hudDim = new GUIStyle { fontSize = 10 };
            hudDim.normal.textColor = new Color(0.5f, 0.52f, 0.55f);
            hudVal = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold };
            hudVal.normal.textColor = new Color(0.9f, 0.92f, 0.95f);
        }

        // Rounded filled rect via GUI.DrawTexture's border-radius (no texture asset needed).
        static void RoundRect(Rect r, Color c, Vector4 radii)
            => GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);

        const string HudPosKey = "BrazeFX.Particles.hudPos";
        const string HudCollapsedKey = "BrazeFX.Particles.hudCollapsed";
        static Vector2 hudPos = new(-1f, -1f);   // BOTTOM-left corner; the panel grows upward from here
        static bool hudPosLoaded, hudCollapsed;

        void OnSceneGUI()
        {
            var sys = (BrazeParticleSystem)target;
            if (sys == null) return;

            DrawPlaybackControls(sys);
            if (!sys.showStats) return;
            EnsureHud();

            const float W = 254f, Hfull = 180f, pad = 12f, bar = 26f, r = 8f;
            float H = hudCollapsed ? bar : Hfull;
            var sv = SceneView.currentDrawingSceneView;
            float viewW = sv != null ? sv.position.width : 900f;
            float viewH = sv != null ? sv.position.height : 600f;

            if (!hudPosLoaded)
            {
                hudPos = new Vector2(EditorPrefs.GetFloat(HudPosKey + ".x", -1f), EditorPrefs.GetFloat(HudPosKey + ".y", -1f));
                hudCollapsed = EditorPrefs.GetBool(HudCollapsedKey, false);
                hudPosLoaded = true;
            }
            if (hudPos.x < 0f) hudPos = new Vector2(16f, viewH - 16f);   // default: bottom-left

            Handles.BeginGUI();
            var e = Event.current;

            // Title bar = top strip; left 26px toggles collapse, the rest drags. Panel grows UP from the anchor.
            var titleRect = new Rect(hudPos.x, hudPos.y - H, W, bar);
            var toggleRect = new Rect(titleRect.x, titleRect.y, 26f, bar);
            EditorGUIUtility.AddCursorRect(titleRect, MouseCursor.Pan);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && titleRect.Contains(e.mousePosition))
                    {
                        if (toggleRect.Contains(e.mousePosition))
                        {
                            hudCollapsed = !hudCollapsed;
                            EditorPrefs.SetBool(HudCollapsedKey, hudCollapsed);
                        }
                        else GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) { hudPos += e.delta; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        EditorPrefs.SetFloat(HudPosKey + ".x", hudPos.x);
                        EditorPrefs.SetFloat(HudPosKey + ".y", hudPos.y);
                        e.Use();
                    }
                    break;
            }
            hudPos.x = Mathf.Clamp(hudPos.x, 0f, Mathf.Max(0f, viewW - W));
            hudPos.y = Mathf.Clamp(hudPos.y, H, viewH);

            var panel = new Rect(hudPos.x, hudPos.y - H, W, H);
            var radAll = new Vector4(r, r, r, r);

            // Soft drop shadow for depth, then the body, then a DISTINCT header band on top.
            RoundRect(new Rect(panel.x + 2f, panel.y + 4f, W, H), new Color(0f, 0f, 0f, 0.30f), radAll);
            RoundRect(panel, new Color(0.085f, 0.09f, 0.10f, 0.97f), radAll);

            var header = new Rect(panel.x, panel.y, W, bar);
            RoundRect(header, new Color(0.145f, 0.15f, 0.17f, 1f), new Vector4(r, r, 0f, 0f));   // lighter header band
            RoundRect(new Rect(header.x, header.y, W, 1f), new Color(1f, 1f, 1f, 0.06f), new Vector4(r, r, 0f, 0f)); // top bevel highlight
            RoundRect(new Rect(header.x, header.yMax - 2f, W, 2f), new Color(1f, 0.45f, 0.14f, 1f), Vector4.zero);    // accent underline

            // Title row — vertically centered in the header band.
            GUI.Label(new Rect(panel.x + 11f, header.y, 12f, bar), hudCollapsed ? "▶" : "▼", hudArrow);
            GUI.Label(new Rect(panel.x + 27f, header.y, W - 96f, bar), "BRAZE PARTICLES", hudTitle);
            bool live = Application.isPlaying || sys.Ready;
            if (Application.isPlaying)
                GUI.Label(new Rect(panel.xMax - 64f, header.y, 54f, bar), "● LIVE", hudLive);
            else if (sys.Ready)
                GUI.Label(new Rect(panel.xMax - 76f, header.y, 66f, bar), previewPlaying ? "● PREVIEW" : "Ⅱ PAUSED", hudLive);

            if (!hudCollapsed)
            {
                GUILayout.BeginArea(new Rect(panel.x + pad, panel.y + bar + 8f, W - pad * 2, H - bar - pad));
                if (live)
                {
                    HudRow("PARTICLES", $"{sys.AliveCount:N0} / {sys.Capacity:N0}");
                    HudRow("GPU MEMORY", FormatBytes(sys.GpuBytes()));
                    HudRow("DISPATCH", !Application.isPlaying && !previewPlaying
                        ? "Paused · 0 / frame"
                        : $"{sys.Capacity:N0} ×2 / frame");
                    HudRow("EFFECTORS", $"{sys.ActiveForces} force · {sys.ActiveColliders} collide");
                    HudRow("SPEED", $"{sys.StatsFps:0} FPS · {1000f / Mathf.Max(sys.StatsFps, 1e-3f):0.0} ms");
                }
                else GUILayout.Label("Enter Play mode or start the Scene preview.", hudDim);
                GUILayout.EndArea();
            }
            Handles.EndGUI();

            // Keep the numbers live while playing (and only then — no idle repaint cost).
            if (Application.isPlaying && e.type == EventType.Repaint)
                SceneView.currentDrawingSceneView?.Repaint();
        }

        static GUIStyle previewTitle, previewState, previewTimeStyle;
        const string PreviewPosKey = "BrazeFX.Particles.previewPos";
        static Vector2 previewPos = new(-1f, -1f);
        static bool previewPosLoaded;

        static void EnsurePreviewStyles()
        {
            if (previewTitle != null) return;
            previewTitle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 10,
            };
            previewTitle.normal.textColor = new Color(0.95f, 0.56f, 0.25f);
            previewState = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = 9,
            };
            previewTimeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
            };
            previewTimeStyle.normal.textColor = new Color(0.66f, 0.68f, 0.72f);
        }

        void DrawPlaybackControls(BrazeParticleSystem sys)
        {
            if (Application.isPlaying) return;
            EnsurePreviewStyles();

            var sv = SceneView.currentDrawingSceneView;
            float viewW = sv != null ? sv.position.width : 900f;
            float viewH = sv != null ? sv.position.height : 600f;
            const float width = 286f, height = 66f, margin = 12f;

            if (!previewPosLoaded)
            {
                previewPos = new Vector2(
                    EditorPrefs.GetFloat(PreviewPosKey + ".x", -1f),
                    EditorPrefs.GetFloat(PreviewPosKey + ".y", -1f));
                previewPosLoaded = true;
            }
            if (previewPos.x < 0f || previewPos.y < 0f)
                previewPos = new Vector2(Mathf.Max(margin, viewW - width - margin),
                                         Mathf.Max(34f, viewH - height - 28f));

            // Keep a previously-saved position reachable after resizing or changing the Scene-view layout.
            previewPos.x = Mathf.Clamp(previewPos.x, 0f, Mathf.Max(0f, viewW - width));
            previewPos.y = Mathf.Clamp(previewPos.y, 24f, Mathf.Max(24f, viewH - height - 18f));
            var panel = new Rect(previewPos.x, previewPos.y, width, height);

            Handles.BeginGUI();

            // Match Unity's particle preview: the whole title strip is a drag handle. Capturing hotControl keeps
            // the drag alive after the pointer leaves the panel (or crosses another Scene overlay).
            var dragRect = new Rect(panel.x, panel.y, panel.width, 21f);
            EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.Pan);
            int dragId = GUIUtility.GetControlID("BrazeParticlePreviewDrag".GetHashCode(), FocusType.Passive, dragRect);
            var e = Event.current;
            switch (e.GetTypeForControl(dragId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && dragRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = dragId;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == dragId)
                    {
                        previewPos += e.delta;
                        previewPos.x = Mathf.Clamp(previewPos.x, 0f, Mathf.Max(0f, viewW - width));
                        previewPos.y = Mathf.Clamp(previewPos.y, 24f, Mathf.Max(24f, viewH - height - 18f));
                        panel.position = previewPos;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == dragId)
                    {
                        GUIUtility.hotControl = 0;
                        EditorPrefs.SetFloat(PreviewPosKey + ".x", previewPos.x);
                        EditorPrefs.SetFloat(PreviewPosKey + ".y", previewPos.y);
                        e.Use();
                    }
                    break;
            }

            RoundRect(new Rect(panel.x + 2f, panel.y + 3f, panel.width, panel.height),
                new Color(0f, 0f, 0f, 0.28f), new Vector4(6f, 6f, 6f, 6f));
            RoundRect(panel, new Color(0.085f, 0.09f, 0.10f, 0.97f), new Vector4(6f, 6f, 6f, 6f));
            RoundRect(new Rect(panel.x, panel.y, 3f, panel.height),
                new Color(1f, 0.45f, 0.14f, 0.9f), new Vector4(6f, 0f, 0f, 6f));

            GUI.Label(new Rect(panel.x + 10f, panel.y + 3f, 180f, 18f), "≡  BRAZE PARTICLES", previewTitle);
            string state = previewPlaying ? "● PLAYING" : sys.Ready ? "Ⅱ PAUSED" : "■ STOPPED";
            GUI.Label(new Rect(panel.xMax - 92f, panel.y + 3f, 82f, 18f), state, previewState);

            float x = panel.x + 9f;
            float y = panel.y + 23f;
            const float buttonH = 24f;
            if (GUI.Button(new Rect(x, y, 88f, buttonH), previewPlaying ? "Ⅱ  Pause" : "▶  Play", EditorStyles.miniButtonLeft))
            {
                if (previewPlaying) PausePreview(); else PlayPreview();
            }
            if (GUI.Button(new Rect(x + 88f, y, 91f, buttonH), "↻  Restart", EditorStyles.miniButtonMid))
                RestartPreview();
            using (new EditorGUI.DisabledScope(!sys.Ready && !previewPlaying))
                if (GUI.Button(new Rect(x + 179f, y, 89f, buttonH), "■  Stop", EditorStyles.miniButtonRight))
                    StopPreview();

            GUI.Label(new Rect(panel.x + 10f, panel.yMax - 17f, panel.width - 20f, 14f),
                $"Playback Time  {previewTime:0.00} s   ·   Preview capped at 60 FPS", previewTimeStyle);
            Handles.EndGUI();
        }

        static void HudRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, hudDim, GUILayout.Width(94));
            GUILayout.Label(value, hudVal);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        static string FormatBytes(long b)
        {
            if (b >= 1 << 20) return $"{b / 1048576f:0.0} MB";
            if (b >= 1 << 10) return $"{b / 1024f:0.0} KB";
            return $"{b} B";
        }

        // C4D-style force list: rows of added forces (each removable) + a drop zone that auto-adds.
        static GUIStyle dropStyle, forceRowStyle;
        static void EnsureForceStyles()
        {
            if (dropStyle != null) return;
            dropStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            dropStyle.alignment = TextAnchor.MiddleCenter;
            dropStyle.fontSize = 11;
            dropStyle.fontStyle = FontStyle.Bold;
            dropStyle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
            forceRowStyle = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            forceRowStyle.fontSize = 11;
            forceRowStyle.alignment = TextAnchor.MiddleLeft;
            forceRowStyle.normal.textColor = new Color(0.86f, 0.88f, 0.92f);
        }

        static BrazeParticleForce ForceFrom(Object o)
        {
            var f = o as BrazeParticleForce;
            if (f == null && o is GameObject go) f = go.GetComponent<BrazeParticleForce>();
            return f;
        }

        static void AddForceUnique(SerializedProperty arr, BrazeParticleForce f)
        {
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == f) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = f;
        }

        void DrawForcesList(SerializedProperty arr)
        {
            EnsureForceStyles();
            if (!BrazeEditorGUI.CanEditObjectList(arr)) return;

            // Rows: each added force, clickable (ping) + a remove button.
            for (int i = 0; i < arr.arraySize; i++)
            {
                var f = arr.GetArrayElementAtIndex(i).objectReferenceValue;
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(1f, 0.45f, 0.14f, 0.7f), Vector4.zero);  // accent tick

                var nameR = new Rect(row.x + 10f, row.y, row.width - 36f, row.height);
                GUI.Label(nameR, BrazeEditorGUI.HierarchyName(f), forceRowStyle);
                if (f != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                { EditorGUIUtility.PingObject(f); Event.current.Use(); }

                if (GUI.Button(new Rect(row.xMax - 24f, row.y + 2f, 20f, 18f), "✕"))
                {
                    arr.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    arr.DeleteArrayElementAtIndex(i);
                    break;
                }
            }

            // Drop zone.
            GUILayout.Space(2);
            var e = Event.current;
            var drop = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            bool hovering = drop.Contains(e.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            RoundRect(drop, hovering ? new Color(1f, 0.45f, 0.14f, 0.16f) : new Color(1f, 1f, 1f, 0.035f), new Vector4(6, 6, 6, 6));
            RoundRect(new Rect(drop.x, drop.y, drop.width, 1.5f), new Color(1f, 0.45f, 0.14f, hovering ? 0.85f : 0.3f), new Vector4(6, 6, 0, 0));
            GUI.Label(drop, arr.arraySize == 0 ? "＋   Drop a force here" : "＋   Drop another force", dropStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences) if (ForceFrom(o) != null) { any = true; break; }
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        var f = ForceFrom(o);
                        if (f != null) AddForceUnique(arr, f);
                    }
                }
                e.Use();
            }
        }

        // A particle-event reaction: enable the event → toggle Spawn Particles and/or Fire Unity Event, each
        // its OWN card (toggle in the header, options revealed when on). Both can run at the same event.
        // Find the master effect (Surface Disintegrator or Voronoi Fracture) that uses `sys` as its secondary emitter.
        static bool VoronoiUsesDebrisEmitter(BrazeVoronoiFracture fracture, BrazeParticleSystem sys)
        {
            if (fracture == null || sys == null || fracture.debrisEmitters == null) return false;
            for (int i = 0; i < fracture.debrisEmitters.Count; i++)
                if (fracture.debrisEmitters[i] == sys) return true;
            return false;
        }

        static Component FindSecondaryMaster(BrazeParticleSystem sys)
        {
            if (sys == null) return null;
            var dis = Resources.FindObjectsOfTypeAll<BrazeSurfaceDisintegrator>();
            for (int i = 0; i < dis.Length; i++)
                if (dis[i] != null && dis[i].debrisEmitter == sys && dis[i].gameObject.scene.IsValid()) return dis[i];
            var vor = Resources.FindObjectsOfTypeAll<BrazeVoronoiFracture>();
            for (int i = 0; i < vor.Length; i++)
                if (vor[i] != null && VoronoiUsesDebrisEmitter(vor[i], sys) && vor[i].gameObject.scene.IsValid()) return vor[i];
            return null;
        }

        // Banner at the top of a secondary emitter's inspector: states which master drives it + a jump button.
        static void DrawSecondaryMasterBanner(Component master)
        {
            string kind = master is BrazeVoronoiFracture ? "Voronoi Fracture"
                        : master is BrazeSurfaceDisintegrator ? "Surface Disintegrator"
                        : "master effect";
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(master != null
                ? $"Secondary particles for {kind} “{master.name}”.\nSelf-emission (Rate / Burst / Shape) is off — it only spawns from that master's requests."
                : "Secondary particle emitter — its master could not be found in the open scene.", MessageType.Info);
            if (master != null && GUILayout.Button("Select\nMaster", GUILayout.Width(70), GUILayout.ExpandHeight(true)))
            {
                Selection.activeObject = master.gameObject;
                EditorGUIUtility.PingObject(master);
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        // Banner at the top of a SLAVE emitter's inspector: states which Braze Pyro Emitter drives it + a jump button.
        static void DrawPyroMasterBanner(BrazePyroEmitter master)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(master != null
                ? $"Slave of Braze Pyro Emitter “{master.name}”.\nSpawns ONLY while that emitter emits, from ITS shape — Duration / Looping and this system's own Shape are ignored. Rate, Burst, Lifetime, Motion and Renderer below still apply."
                : "Slave particle emitter — its Braze Pyro Emitter master could not be found in the open scene.", MessageType.Info);
            if (master != null && GUILayout.Button("Select\nMaster", GUILayout.Width(70), GUILayout.ExpandHeight(true)))
            {
                Selection.activeObject = master.gameObject;
                EditorGUIUtility.PingObject(master);
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        void SpawnEventSection(string key, string title, SerializedProperty evt)
        {
            var en = evt.FindPropertyRelative("enabled");
            if (BrazeEditorGUI.Section(key, title))
            {
                BrazeEditorGUI.ToggleButton("Enabled", en);
                if (en.boolValue)
                {
                    BrazeEditorGUI.Separator();
                    BrazeEditorGUI.PropertyField(evt.FindPropertyRelative("chance"));   // fire probability (both ops)

                    ModuleSection(key + ".spawn", "Spawn Particles", evt.FindPropertyRelative("spawnParticles"), () =>
                    {
                        var targetProp = evt.FindPropertyRelative("target");
                        EditorGUI.BeginChangeCheck();
                        BrazeEditorGUI.PropertyField(targetProp, new GUIContent("Sub-Emitter"));
                        if (EditorGUI.EndChangeCheck())
                        {
                            // Reject a sub-emitter that spawns back into this system (directly or through its own
                            // chain) — that's an infinite spawn loop. Clear the assignment and warn.
                            var self = target as BrazeParticleSystem;
                            var cand = targetProp.objectReferenceValue as BrazeParticleSystem;
                            if (self != null && cand != null && CreatesSpawnCycle(self, cand))
                            {
                                Debug.LogWarning($"[BrazeFX] \"{cand.name}\" can't be a sub-emitter of \"{self.name}\" — it would spawn back into \"{self.name}\" and create an infinite spawn loop. Assignment cleared.");
                                targetProp.objectReferenceValue = null;
                            }
                        }
                        if (targetProp.objectReferenceValue == null)
                        {
                            if (GUILayout.Button("+ Create Sub-Emitter")) CreateSubEmitter(targetProp);
                            BrazeEditorGUI.Hint("A child particle system you fully art-direct (it can have its own events → chain reactions). Empty = spawn into THIS system.");
                        }
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.IntSlider(evt.FindPropertyRelative("count"), 0, BrazeParticleSystem.MaxEventCount, new GUIContent("Count / Particle"));
                        BrazeEditorGUI.Slider(evt.FindPropertyRelative("positionJitter"), 0f, 3f, new GUIContent("Position Jitter"));
                        BrazeEditorGUI.ToggleButton("Inherit Color", evt.FindPropertyRelative("inheritColor"));
                        BrazeEditorGUI.Hint("Spawned PER triggering particle. Excess over the sub-emitter's Capacity is dropped. NOTE: one shared spawn buffer — if On Death and On Collision both spawn, they share the resolved target.");
                    });

                    ModuleSection(key + ".fire", "Fire Unity Event", evt.FindPropertyRelative("fireEvent"), () =>
                    {
                        BrazeEditorGUI.PropertyField(evt.FindPropertyRelative("onFire"), new GUIContent("On Fire (world pos)"));
                        BrazeEditorGUI.Hint("Capped to 10 events/frame (a sample) — won't flood the CPU. The callback receives the world position.");
                    });
                }
            }
            BrazeEditorGUI.EndSection();
        }

        // Spawn a child particle system wired as this event's sub-emitter (driven by the parent event,
        // not continuous emission). It's a full system — art-direct it, and it can have its own events.
        void CreateSubEmitter(SerializedProperty targetProp)
        {
            var parent = (BrazeParticleSystem)target;
            var go = new GameObject("Sub Emitter");
            Undo.RegisterCreatedObjectUndo(go, "Create Sub-Emitter");
            go.transform.SetParent(parent.transform, false);
            var sub = Undo.AddComponent<BrazeParticleSystem>(go);
            sub.isSubEmitter = true;       // lite system: no SYSTEM/SPAWN, event-driven, world-space
            sub.rate = 0f;
            sub.capacity = BrazeParticleSystem.SubEmitterMaxCapacity;   // default to the (capped) max
            EditorUtility.SetDirty(sub);
            targetProp.objectReferenceValue = sub;
            serializedObject.ApplyModifiedProperties();
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // True if assigning `candidate` as `self`'s sub-emitter would form a spawn loop: walk the
        // candidate's own On-Death sub-emitter chain — if it leads back to `self` (or IS `self`), it
        // would recurse forever. (Walks committed targets, so the pending edge self→candidate closes
        // the loop exactly when candidate already reaches self.) Guard-capped against pre-existing cycles.
        static bool CreatesSpawnCycle(BrazeParticleSystem self, BrazeParticleSystem candidate)
        {
            var node = candidate;
            for (int guard = 0; node != null && guard < 256; guard++)
            {
                if (node == self) return true;
                node = node.onDeath != null ? node.onDeath.target : null;
            }
            return false;
        }

        void ModuleSection(string key, string title, SerializedProperty enableProp, System.Action body)
        {
            if (BrazeEditorGUI.Section(key, title))
            {
                BrazeEditorGUI.ToggleButton("Enabled", enableProp);
                if (enableProp.boolValue)
                {
                    BrazeEditorGUI.Separator();
                    body();
                }
            }
            BrazeEditorGUI.EndSection();
        }

        static readonly string[] IntModeShort = { "Const", "Range" };

        // Brightness is stored as the existing maximum plus one sentinel-backed minimum so every old scene remains
        // fixed-brightness. Equal handles are a constant; separating them enables stable per-particle randomization.
        static void BrightnessRangeField(SerializedProperty minProp, SerializedProperty maxProp)
        {
            var tooltip = new GUIContent("Brightness",
                "Stable random HDR brightness per particle between Min and Max. Equal values are fixed. " +
                "Push above 1 to drive Bloom; this does not emit scene light.");

            // Avoid applying one selected emitter's range to every target accidentally. The existing maximum remains
            // safely multi-editable; select one emitter when the selected objects carry different ranges.
            if (minProp.hasMultipleDifferentValues || maxProp.hasMultipleDifferentValues)
            {
                BrazeEditorGUI.PropertyField(maxProp, tooltip);
                return;
            }

            float max = Mathf.Max(0f, maxProp.floatValue);
            float min = minProp.floatValue < 0f ? max : Mathf.Max(0f, minProp.floatValue);
            if (min > max) (min, max) = (max, min);
            float sliderMax = Mathf.Max(8f, Mathf.Max(min, max));

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(tooltip);
                min = BrazeEditorGUI.DelayedFloatField(min, GUILayout.Width(54));
                EditorGUILayout.MinMaxSlider(ref min, ref max, 0f, sliderMax);
                max = BrazeEditorGUI.DelayedFloatField(max, GUILayout.Width(54));
            }

            if (EditorGUI.EndChangeCheck())
            {
                min = Mathf.Max(0f, min);
                max = Mathf.Max(0f, max);
                minProp.floatValue = Mathf.Min(min, max);
                maxProp.floatValue = Mathf.Max(min, max);
            }
        }

        // BrazeIntRange widget: Const (single field) or Range (min → max), with a mode popup — matches
        // the CONST / RANGE value widget used for the particle start values.
        static void IntRangeField(string label, SerializedProperty r)
        {
            var rangeP = r.FindPropertyRelative("range");
            var minP = r.FindPropertyRelative("min");
            var maxP = r.FindPropertyRelative("max");

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(EditorGUIUtility.labelWidth - 2));
            if (rangeP.boolValue)
            {
                EditorGUI.BeginChangeCheck();
                int mn = BrazeEditorGUI.DelayedIntField(minP.intValue);
                GUILayout.Label("to", GUILayout.Width(16));
                int mx = BrazeEditorGUI.DelayedIntField(maxP.intValue);
                if (EditorGUI.EndChangeCheck()) { minP.intValue = mn; maxP.intValue = mx; }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                int v = BrazeEditorGUI.DelayedIntField(minP.intValue);
                if (EditorGUI.EndChangeCheck()) { minP.intValue = v; maxP.intValue = v; }
            }
            int ni = EditorGUILayout.Popup(rangeP.boolValue ? 1 : 0, IntModeShort, GUILayout.Width(60));
            if ((ni == 1) != rangeP.boolValue) rangeP.boolValue = ni == 1;
            EditorGUILayout.EndHorizontal();
        }

        // The publisher-owned three-js-particles prototype's CONST / RANGE / CURVE value widget.
        // `infiniteP` (optional) adds an "Inf" mode to the selector — used by Lifetime so particles can live forever.
        static void MinMaxField(string label, SerializedProperty c, float lo, float hi, SerializedProperty infiniteP = null)
        {
            var modeP = c.FindPropertyRelative("mode");
            var cMin = c.FindPropertyRelative("constantMin");
            var cMax = c.FindPropertyRelative("constantMax");
            var curMin = c.FindPropertyRelative("curveMin");

            bool infinite = infiniteP != null && infiniteP.boolValue;
            var mode = (BrazeCurveMode)modeP.enumValueIndex;
            bool isCurve = !infinite && mode == BrazeCurveMode.Curve;

            // Row 1: label + the scalar control (Const/Range) + mode selector. In Curve mode the
            // scalar slot is left empty and the curve gets its own FULL-WIDTH row below — a cramped
            // curve field shares its rect badly with the popup and is fiddly to edit.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(EditorGUIUtility.labelWidth - 2));

            if (infinite)
            {
                GUILayout.Label("∞ never dies", BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniLabel));
                GUILayout.FlexibleSpace();
            }
            else switch (mode)
            {
                case BrazeCurveMode.Constant:
                {
                    EditorGUI.BeginChangeCheck();
                    float v = BrazeEditorGUI.Slider(cMin.floatValue, lo, hi);
                    if (EditorGUI.EndChangeCheck()) cMin.floatValue = v;
                    break;
                }
                case BrazeCurveMode.RandomBetweenConstants:
                {
                    float mn = cMin.floatValue, mx = cMax.floatValue;
                    EditorGUI.BeginChangeCheck();
                    mn = BrazeEditorGUI.DelayedFloatField(mn, GUILayout.Width(46));
                    // Explicit control rect (not auto-layout) so the slider handle renders correctly on
                    // the first paint instead of "looking broken until you touch it".
                    var sr = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                    EditorGUI.MinMaxSlider(sr, ref mn, ref mx, lo, hi);
                    mx = BrazeEditorGUI.DelayedFloatField(mx, GUILayout.Width(46));
                    if (EditorGUI.EndChangeCheck())
                    {
                        cMin.floatValue = Mathf.Min(mn, mx);
                        cMax.floatValue = Mathf.Max(mn, mx);
                    }
                    break;
                }
                default: // Curve — drawn full-width below
                    GUILayout.FlexibleSpace();
                    break;
            }

            if (infiniteP != null)
            {
                int cur = infinite ? 3 : modeP.enumValueIndex;
                int ni = EditorGUILayout.Popup(cur, ModeShortInf, GUILayout.Width(60));
                if (ni != cur)
                {
                    if (ni == 3) infiniteP.boolValue = true;
                    else { infiniteP.boolValue = false; modeP.enumValueIndex = ni; }
                }
            }
            else
            {
                int ni = EditorGUILayout.Popup(modeP.enumValueIndex, ModeShort, GUILayout.Width(60));
                if (ni != modeP.enumValueIndex) modeP.enumValueIndex = ni;
            }
            EditorGUILayout.EndHorizontal();

            // Row 2 (Curve mode): full-width curve field drawn with an EXPLICIT control rect.
            // Unity's curve popup binds to the field via a position-derived control ID; a lone
            // auto-layout curve field gets an inconsistent rect between the layout and repaint passes,
            // so the popup can't match it back and keyframe drags are dropped. A reserved fixed-height
            // rect makes the position deterministic.
            if (isCurve)
            {
                var r = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                BrazeEditorGUI.PropertyField(r, curMin, GUIContent.none);
            }
        }
    }
}
