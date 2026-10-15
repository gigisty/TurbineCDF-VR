using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeVDBImport))]
    public sealed class BrazeVDBImportEditor : Editor
    {
        const string K = "BrazeFX.VdbImport.";
        static UnityEngine.Object cachedRendererFeature;
        static bool cachedRendererLiveSlices;
        static double rendererFeatureRefreshAt;
        double nextPreviewRepaint;

        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        void OnEnable()
        {
            BrazeVdbPreviewController.StateChanged += OnPreviewChanged;
            EditorApplication.delayCall += ImportMissingSource;
        }

        void OnDisable()
        {
            BrazeVdbPreviewController.StateChanged -= OnPreviewChanged;
            EditorApplication.delayCall -= ImportMissingSource;
        }

        void ImportMissingSource()
        {
            if (this == null || target is not BrazeVDBImport component || component == null ||
                string.IsNullOrWhiteSpace(component.sourceFile) ||
                !BrazeVdbImporter.NeedsComponentImport(component.sourceFile, component.cache)) return;
            QueueSourceImport(component, component.sourceFile);
        }
        void OnPreviewChanged()
        {
            if (this == null || EditorApplication.timeSinceStartup < nextPreviewRepaint) return;
            nextPreviewRepaint = EditorApplication.timeSinceStartup + 0.1d;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var component = (BrazeVDBImport)target;
            serializedObject.Update();
            BrazeEditorGUI.BrandHeader("VDB IMPORT & PLAYER");

            bool sourceChanged = DrawSource(component, out bool browseRequested);
            DrawPlayer(component);
            DrawRenderer(component);
            DrawParticleFluidFollow();

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                EditorUtility.SetDirty(component);
                if (component.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                BrazeVdbPreviewController.RefreshRenderer(component);
            }
            if (sourceChanged) QueueSourceImport(component, component.sourceFile);

            if (BrazeVdbImporter.Active && BrazeVdbImporter.Owner == component) Repaint();
            if (browseRequested) BrowseAndQueueSource(component);
        }

        bool DrawSource(BrazeVDBImport component, out bool browseRequested)
        {
            bool requestImport = false;
            browseRequested = false;
            bool group = BrazeEditorGUI.GroupBegin(K + "source", "SOURCE", 1);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "input", "OpenVDB Source"))
                {
                    SerializedProperty source = P("sourceFile");
                    using (new EditorGUI.DisabledScope(BrazeVdbImporter.Active))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string next = EditorGUILayout.DelayedTextField(new GUIContent("File"), source.stringValue);
                        if (!string.Equals(next, source.stringValue, StringComparison.Ordinal))
                        {
                            source.stringValue = next;
                            requestImport = true;
                        }
                        if (GUILayout.Button("Browse…", GUILayout.Width(76f))) browseRequested = true;
                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(source.stringValue)))
                            if (GUILayout.Button("Clear", GUILayout.Width(48f)))
                            {
                                source.stringValue = string.Empty;
                                requestImport = true;
                            }
                    }
                    BrazeEditorGUI.Hint(
                        "Choose a standalone .vdb for a still, any numbered .vdb beside its Braze manifest for the full " +
                        "sequence, or the _Volume.manifest.json itself. Import and playback data are prepared automatically.");

                    bool capabilityReady = BrazeVdbBridge.TryGetCapability(out string capability);
                    if (!capabilityReady) EditorGUILayout.HelpBox(capability, MessageType.Warning);
                    bool sourceReady = TryResolveSource(source.stringValue, out _, out string sourceError);
                    if (!sourceReady && !string.IsNullOrEmpty(source.stringValue))
                        EditorGUILayout.HelpBox(sourceError, MessageType.Warning);

                    bool ownsImport = BrazeVdbImporter.Active && BrazeVdbImporter.Owner == component;
                    if (ownsImport)
                    {
                        Rect progress = EditorGUILayout.GetControlRect(false, 18f);
                        EditorGUI.ProgressBar(progress, BrazeVdbImporter.Progress,
                            BrazeVdbImporter.Status ?? "Importing…");
                        if (GUILayout.Button("Cancel Import", GUILayout.Height(26f))) BrazeVdbImporter.Cancel();
                    }
                    else
                    {
                        if (BrazeVdbImporter.Active)
                            EditorGUILayout.HelpBox("Another VDB import is currently running.", MessageType.Info);
                        else if (!string.IsNullOrEmpty(BrazeVdbImporter.Error))
                            EditorGUILayout.HelpBox(BrazeVdbImporter.Error, MessageType.Error);
                        else if (component.cache != null && component.cache.IsValid)
                            EditorGUILayout.LabelField(
                                $"Ready · {component.cache.frameCount} frame{(component.cache.frameCount == 1 ? "" : "s")} · " +
                                $"{component.cache.fps:0.##} fps · {component.cache.Duration:0.##}s",
                                EditorStyles.miniLabel);
                    }
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(group);
            return requestImport;
        }

        void DrawPlayer(BrazeVDBImport component)
        {
            bool group = BrazeEditorGUI.GroupBegin(K + "player", "PLAYER", 4);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "transport", "Transport"))
                    DrawTransport(component);
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "behavior", "Playback"))
                {
                    BrazeEditorGUI.ToggleButton("Prepare On Enable", P("autoLoad"));
                    BrazeEditorGUI.ToggleButton("Play On Enable", P("playOnLoad"));
                    BrazeEditorGUI.ToggleButton("Loop", P("loop"));
                    BrazeEditorGUI.ToggleButton("Motion Smoothing", P("motionSmoothing"));
                    if (P("motionSmoothing").boolValue)
                        BrazeEditorGUI.Hint("Blends between imported frames along the velocity grid — sequences " +
                                            "play smoothly at any display rate. One cheap GPU pass, capped at " +
                                            "60 updates/sec; shadows and Definition still update per stored frame.");
                    BrazeEditorGUI.Slider(P("playbackSpeed"), 0.05f, 4f, new GUIContent("Speed"));
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "residency", "Frame Loading"))
                {
                    BrazeEditorGUI.ToggleButton("Stream Frames", P("streaming"));
                    using (new EditorGUI.DisabledScope(P("streaming").boolValue))
                        BrazeEditorGUI.ToggleButton("Share Duplicate Data", P("dedupResident"));
                    BrazeEditorGUI.Hint(P("streaming").boolValue
                        ? "Keeps only nearby frames in memory. Recommended for long sequences."
                        : "Keeps the whole sequence in memory for the fastest scrubbing. Best for short clips.");
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "variation", "Instance Variation", false))
                {
                    BrazeEditorGUI.Slider(P("phaseOffset01"), 0f, 1f, new GUIContent("Phase Offset"));
                    BrazeEditorGUI.ToggleButton("Randomize Phase", P("randomizePhase"));
                    BrazeEditorGUI.Slider(P("speedVariance"), 0f, 0.9f, new GUIContent("Speed Variance"));
                    BrazeEditorGUI.Hint("Stable per-instance phase and speed variation desynchronizes duplicated VDB players.");
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(group);
        }

        static void DrawTransport(BrazeVDBImport component)
        {
            DrawStatus(component);
            bool valid = component.cache != null && component.cache.IsValid;
            bool edit = !Application.isPlaying;

            using (new EditorGUI.DisabledScope(!valid))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play")) BrazeVdbPreviewController.Play(component);
                if (GUILayout.Button("Pause")) BrazeVdbPreviewController.Pause(component);
                if (GUILayout.Button("Restart")) BrazeVdbPreviewController.Restart(component);
                if (GUILayout.Button("Stop")) BrazeVdbPreviewController.Stop(component);
            }

            int frameCount = Mathf.Max(0, component.FrameCount);
            int frame = edit ? BrazeVdbPreviewController.Frame(component) : Mathf.Max(0, component.CurrentFrame);
            BrazeEditorGUI.Separator();
            using (new EditorGUI.DisabledScope(!valid || frameCount <= 0))
            {
                EditorGUI.BeginChangeCheck();
                int next = EditorGUILayout.IntSlider(new GUIContent("Frame"), frame, 0, Mathf.Max(0, frameCount - 1));
                if (EditorGUI.EndChangeCheck()) BrazeVdbPreviewController.SetFrame(component, next);
            }

            bool editorPlaying = edit && BrazeVdbPreviewController.IsPlaying(component);
            bool loading = component.Status == BrazeVolumePlayer.CacheStatus.Loading;
            string state = loading ? "Preparing" : component.Status == BrazeVolumePlayer.CacheStatus.Error ? "Error"
                : edit ? editorPlaying ? "Playing" : BrazeVdbPreviewController.IsPreviewVisible(component) ? "Paused" : "Stopped"
                : component.State.ToString();
            float time = edit ? BrazeVdbPreviewController.Time(component) : component.CurrentTime;
            EditorGUILayout.LabelField(
                $"{state} · frame {(frameCount > 0 ? frame + 1 : 0)}/{frameCount} · {time:0.00}s/{component.Duration:0.00}s",
                EditorStyles.miniLabel);
        }

        static void DrawStatus(BrazeVDBImport component)
        {
            string label;
            Color color;
            switch (component.Status)
            {
                case BrazeVolumePlayer.CacheStatus.Loading:
                    label = "● Preparing volume…"; color = new Color(0.95f, 0.75f, 0.25f); break;
                case BrazeVolumePlayer.CacheStatus.Ready:
                    label = component.RendererReady ? "● Volume ready" : "● Preparing renderer…";
                    color = new Color(0.45f, 0.82f, 0.45f); break;
                case BrazeVolumePlayer.CacheStatus.Error:
                    label = $"● {component.StatusMessage}"; color = new Color(0.92f, 0.42f, 0.42f); break;
                default:
                    label = component.cache != null && component.cache.IsValid ? "● Ready to preview" : "● Choose a source";
                    color = new Color(0.6f, 0.6f, 0.6f); break;
            }
            GUIStyle style = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel);
            style.normal.textColor = color;
            style.wordWrap = true;
            EditorGUILayout.LabelField(label, style);
        }

        void DrawRenderer(BrazeVDBImport component)
        {
            bool group = BrazeEditorGUI.GroupBegin(K + "renderer", "RENDERER", 3);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "rendererMaster", "Volume Renderer"))
                {
                    BrazeEditorGUI.ToggleButton("Enable Rendering", P("enableRendering"));
                    BrazeEditorGUI.PropertyField(P("keyLight"), new GUIContent("Key Light"));
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("Render Host", component.RenderHost, typeof(BrazeContainer), true);

                    UnityEngine.Object feature = FindRendererFeature(out bool liveSlices);
                    if (feature == null)
                    {
                        EditorGUILayout.HelpBox(
                            "No Braze Render Feature was found. Configure BrazeFX on the project's URP Renderer assets.",
                            MessageType.Warning);
                        if (GUILayout.Button("Configure URP Rendering")) BrazeUrpSetup.ConfigureFromMenu();
                    }
                    else
                    {
                        if (!liveSlices)
                            EditorGUILayout.HelpBox("Braze Render Feature was found, but Live Slices is disabled.", MessageType.Warning);
                        else
                            BrazeEditorGUI.Hint("Braze Render Feature · Live Slices enabled · using the real volume renderer.");
                        if (GUILayout.Button("Select Renderer Feature"))
                        {
                            Selection.activeObject = feature;
                            EditorGUIUtility.PingObject(feature);
                        }
                    }
                }
                BrazeEditorGUI.EndSection();

                if (BrazeEditorGUI.Section(K + "vdbReconstruction", "VDB GPU Reconstruction"))
                {
                    bool hasImportedVelocity = component.cache != null && component.cache.hasVelocity;
                    BrazeEditorGUI.ToggleButton(hasImportedVelocity
                        ? "Use Imported Velocity for Detail"
                        : "Reconstruct Renderer Data", P("reconstructRendererData"));
                    using (new EditorGUI.DisabledScope(!P("reconstructRendererData").boolValue))
                    {
                        if (!hasImportedVelocity)
                        {
                            BrazeEditorGUI.IntSlider(P("motionIterations"), 4, 24, new GUIContent("Motion Refinement"));
                            BrazeEditorGUI.Slider(P("motionSmoothness"), 0.02f, 1f, new GUIContent("Motion Coherence"));
                            BrazeEditorGUI.Slider(P("maxMotionVoxelsPerFrame"), 1f, 16f,
                                new GUIContent("Maximum Frame Travel"));
                        }
                        BrazeEditorGUI.Slider(P("detailFlowMemory"), 0.25f, 4f,
                            new GUIContent("Detail Flow Memory"));
                        if (!hasImportedVelocity)
                        {
                            BrazeEditorGUI.Slider(P("motionTemporalSmoothing"), 0f, 0.95f,
                                new GUIContent("Temporal Smoothing"));
                            BrazeEditorGUI.ToggleButton("Full-Resolution Motion", P("motionFullResolution"));
                        }
                    }
                    if (hasImportedVelocity)
                        BrazeEditorGUI.Hint(
                            "The VDB `vel` grid is used directly as authoritative motion for particles, curl, " +
                            "motion-aware detail, and up-res flow. Adjacent-frame optical flow is bypassed; the " +
                            "imported velocity remains available to connected particles even when renderer detail is off.");
                    else
                        BrazeEditorGUI.Hint(component.FrameCount > 1
                            ? "No `vel` grid is present. Adjacent scalar frames use the GPU optical-flow fallback to infer velocity, curl, and a flow anchor. Temporal Smoothing carries each sequential solve forward; seeking and scrubbing remain deterministic."
                            : "No `vel` grid or adjacent frame is available, so fluid motion remains neutral. Spatial Render Detail can still shape the visible volume.");
                }
                BrazeEditorGUI.EndSection();

                BrazeEditorGUI.Hint("Final imported-volume resolution, samples-per-voxel, and raymarch steps come from " +
                                    "the active Braze Render Feature. Import playback owns transport and the portable look.");

                DrawLook(component);
            }
            BrazeEditorGUI.GroupEnd(group);
        }

        void DrawParticleFluidFollow()
        {
            bool group = BrazeEditorGUI.GroupBegin(K + "particleFollow", "PARTICLE FOLLOW", 1);
            if (group)
            {
                if (BrazeEditorGUI.Section(K + "particleFollowSettings", "VDB FLUID MOTION"))
                {
                    BrazeEditorGUI.Slider(P("particleFluidFollow"), 0f, 1f, new GUIContent("Fluid Follow"));
                    using (new EditorGUI.DisabledScope(P("particleFluidFollow").floatValue <= 0f))
                    {
                        BrazeEditorGUI.Slider(P("particleFluidFollowRand"), 0f, 1f,
                            new GUIContent("Follow Jitter"));
                        BrazeEditorGUI.Separator();
                        BrazeEditorGUI.Slider(P("particleFluidFollowGain"), 0f, 8f,
                            new GUIContent("Follow Boost"));
                        BrazeEditorGUI.Slider(P("particleFluidFollowGainRand"), 0f, 1f,
                            new GUIContent("Boost Jitter"));
                    }

                    BrazeEditorGUI.SubHeader("TARGET PARTICLE SYSTEMS");
                    EditorGUILayout.PropertyField(P("connectedParticleEmitters"),
                        new GUIContent("Particle Systems"), true);
                    BrazeEditorGUI.Hint(
                        "This connection supplies lifetime fluid motion only. Each target keeps its own Duration, " +
                        "Looping, Rate, Burst, and Shape; it does not become a VDB spawn slave. If a target is also " +
                        "connected to a Pyro Emitter, the Pyro connection can still control spawning while this VDB " +
                        "wins only for Fluid Follow. An imported `vel` grid is authoritative; adjacent scalar-frame " +
                        "reconstruction is used only when `vel` is absent.");
                }
                BrazeEditorGUI.EndSection();
            }
            BrazeEditorGUI.GroupEnd(group);
        }

        void DrawLook(BrazeVDBImport component)
        {
            if (!BrazeEditorGUI.Section(K + "look", "Look Override", false))
            {
                BrazeEditorGUI.EndSection();
                return;
            }

            SerializedProperty enabled = P("overrideLook");
            BrazeEditorGUI.ToggleButton("Override Imported Look", enabled);
            if (enabled.boolValue)
            {
                SerializedProperty look = P("lookOverride");
                SerializedProperty seeded = P("lookOverrideSeeded");
                bool haveBaked = component.cache != null && component.cache.hasBakedLook;
                if (!seeded.boolValue && haveBaked)
                {
                    look.boxedValue = component.cache.bakedLook;
                    seeded.boolValue = true;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    BrazeEditorGUI.Hint(haveBaked ? "Started from the look carried by the VDB manifest." :
                        "This VDB has no portable Braze look; override starts from defaults.");
                    using (new EditorGUI.DisabledScope(!haveBaked))
                        if (GUILayout.Button("Copy From Import", GUILayout.Width(118f)))
                            look.boxedValue = component.cache.bakedLook;
                }
                BrazeEditorGUI.Separator();

                SerializedProperty slice = look.FindPropertyRelative("slice");
                SerializedProperty sliceVersion = slice.FindPropertyRelative("version");
                bool sliceActive = sliceVersion != null && sliceVersion.intValue > 0;
                if (!sliceActive)
                {
                    EditorGUILayout.HelpBox("This look predates the current slice renderer controls.", MessageType.Info);
                    if (GUILayout.Button("Enable Current Slice Controls"))
                        slice.boxedValue = BrazeSliceRenderSettings.Default;
                }
                BrazeRenderInspector.DrawVolumeLookControls(K + "lookControls.",
                    name => look.FindPropertyRelative(name), name => slice.FindPropertyRelative(name),
                    drawSun: false, sliceActive: sliceActive, liveContainer: null);
            }
            else
                BrazeEditorGUI.Hint("Rendering with the authored look carried by the manifest, or Braze defaults for a generic VDB.");
            BrazeEditorGUI.EndSection();
        }

        static UnityEngine.Object FindRendererFeature(out bool liveSlices)
        {
            if (EditorApplication.timeSinceStartup < rendererFeatureRefreshAt)
            {
                liveSlices = cachedRendererLiveSlices;
                return cachedRendererFeature;
            }
            rendererFeatureRefreshAt = EditorApplication.timeSinceStartup + 1d;
            liveSlices = false;
            UnityEngine.Object fallback = null;
            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableRendererData"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset == null || asset.GetType().Name != "BrazeRenderFeature") continue;
                    fallback ??= asset;
                    SerializedProperty enabled = new SerializedObject(asset).FindProperty("liveSlices");
                    if (enabled != null && enabled.boolValue)
                    {
                        liveSlices = true;
                        cachedRendererFeature = asset;
                        cachedRendererLiveSlices = true;
                        return cachedRendererFeature;
                    }
                }
            }
            cachedRendererFeature = fallback;
            cachedRendererLiveSlices = false;
            return cachedRendererFeature;
        }

        static bool BrowseSource(SerializedProperty source)
        {
            string start = ResolveBrowseDirectory(source.stringValue);
            string selected = EditorUtility.OpenFilePanelWithFilters("Select OpenVDB Source", start,
                new[] { "Braze volume manifest", "json", "OpenVDB frame", "vdb" });
            if (string.IsNullOrEmpty(selected)) return false;
            source.stringValue = ToProjectRelativeIfInside(selected);
            return true;
        }

        void BrowseAndQueueSource(BrazeVDBImport component)
        {
            // Open the native panel only after DrawSource, DrawPlayer, and DrawRenderer have closed every manual
            // GUILayout group. The panel may synchronously refresh the AssetDatabase before returning; opening it
            // inside the Source card invalidated those groups and caused EndLayoutGroup errors on both enclosing
            // EndVertical calls. Exit this one GUI event after the modal even when the user cancels.
            serializedObject.Update();
            SerializedProperty source = P("sourceFile");
            bool selected = BrowseSource(source);
            if (selected)
            {
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(component);
                if (component.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                QueueSourceImport(component, component.sourceFile);
            }
            GUIUtility.ExitGUI();
        }

        static void QueueSourceImport(BrazeVDBImport component, string requestedSource)
        {
            EditorApplication.delayCall += () =>
            {
                if (component == null || Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode ||
                    !string.Equals(component.sourceFile, requestedSource, StringComparison.Ordinal)) return;

                if (string.IsNullOrWhiteSpace(requestedSource))
                {
                    BrazeVdbPreviewController.Unload(component);
                    component.cache = null;
                    EditorUtility.SetDirty(component);
                    if (component.gameObject.scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                    return;
                }

                if (BrazeVdbImporter.Active) return;
                if (!TryResolveSource(requestedSource, out string sourcePath, out string sourceError))
                {
                    Debug.LogError($"[BrazeFX] OpenVDB source is invalid: {sourceError}", component);
                    return;
                }
                try
                {
                    BrazeVdbPreviewController.Unload(component);
                    BrazeVdbImporter.Start(sourcePath, component);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BrazeFX] OpenVDB import could not start: {e}", component);
                }
            };
        }

        static bool TryResolveSource(string value, out string absolute, out string error)
        {
            absolute = null;
            error = null;
            if (string.IsNullOrWhiteSpace(value)) { error = "Choose a VDB frame or Braze volume manifest."; return false; }
            try { absolute = Path.GetFullPath(value); }
            catch (Exception e) { error = $"The source path is invalid: {e.Message}"; return false; }
            string extension = Path.GetExtension(value);
            if (!string.Equals(extension, ".vdb", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            { error = "Source must be a .vdb frame or _Volume.manifest.json file."; return false; }
            if (!File.Exists(absolute)) { error = "The selected source file no longer exists."; return false; }
            return true;
        }

        static string ResolveBrowseDirectory(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    string absolute = Path.GetFullPath(value);
                    string directory = File.Exists(absolute) ? Path.GetDirectoryName(absolute) : absolute;
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
                }
                catch { }
            }
            return Application.dataPath;
        }

        static string ToProjectRelativeIfInside(string value)
        {
            string absolute = Path.GetFullPath(value).Replace('\\', '/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            return absolute.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                   absolute.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)
                ? "Assets" + absolute.Substring(assets.Length) : absolute;
        }

    }

    [CustomEditor(typeof(BrazeExport))]
    public sealed class BrazeExportEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var component = (BrazeExport)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("container"), new GUIContent("Source Container"));
            serializedObject.ApplyModifiedProperties();

            if (component.container == null)
            {
                EditorGUILayout.HelpBox("Assign the live Container whose timeline should be baked and exported.", MessageType.Info);
                if (GUILayout.Button("Find Nearby Container"))
                {
                    component.container = FindNearbyContainer(component);
                    if (component.container != null)
                    {
                        EditorUtility.SetDirty(component);
                        EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                    }
                }
            }

            EditorGUILayout.Space(5f);
            if (GUILayout.Button("Open Export Studio", GUILayout.Height(30f)))
            {
                if (component.container != null) BrazeExportWindow.OpenFor(component.container);
                else BrazeExportWindow.Open();
            }
        }

        internal static BrazeContainer FindNearbyContainer(BrazeExport component)
        {
            if (component == null) return null;
            BrazeContainer value = component.GetComponent<BrazeContainer>();
            if (value == null) value = component.GetComponentInParent<BrazeContainer>();
            if (value == null) value = component.GetComponentInChildren<BrazeContainer>(true);
            return value;
        }
    }
}
