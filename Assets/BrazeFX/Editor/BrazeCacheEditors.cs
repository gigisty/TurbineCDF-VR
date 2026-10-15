using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Inspector for <see cref="BrazeCachePlayer"/> — how an existing cache is played back.</summary>
    [CustomEditor(typeof(BrazeCachePlayer))]
    public sealed class BrazeCachePlayerEditor : Editor
    {
        const string K = "BrazeFX.CachePlayer.";
        double nextRepaint;   // throttle live-status repaints (see the note at the end of OnInspectorGUI)

        public override void OnInspectorGUI()
        {
            var comp = (BrazeCachePlayer)target;
            serializedObject.Update();
            BrazeEditorGUI.BeginCards();   // reset group dark/light alternation each pass (no Header here) — else LOOK blinks

            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("cache"));

            // ── Residency: status line + Load / Unload buttons ──
            DrawResidency(comp);

            BrazeEditorGUI.ToggleButton("Auto Load", serializedObject.FindProperty("autoLoad"));
            BrazeEditorGUI.ToggleButton("Play On Load", serializedObject.FindProperty("playOnLoad"));

            if (BrazeEditorGUI.Section(K + "playback", "Playback"))
            {
                BrazeEditorGUI.ToggleButton("Enable Rendering (master)", serializedObject.FindProperty("enableRendering"));
                if (!comp.enableRendering)
                    BrazeEditorGUI.Hint("Volume NOT drawn — playback + scatter still run. Toggle this live in Play mode " +
                                        "and watch the FPS delta: that delta is the volume's pure render cost.");
                BrazeEditorGUI.ToggleButton("Loop", serializedObject.FindProperty("loop"));
                BrazeEditorGUI.ToggleButton("Motion Smoothing", serializedObject.FindProperty("motionSmoothing"));
                if (comp.motionSmoothing)
                    BrazeEditorGUI.Hint("Blends between stored frames along the clip's velocity — frame-skipped " +
                                        "bakes play smoothly instead of stepping. One cheap GPU pass, capped at " +
                                        "60 updates/sec; needs a clip baked with velocity.");
                BrazeEditorGUI.PropertyField(serializedObject.FindProperty("playbackSpeed"));
                BrazeEditorGUI.PropertyField(serializedObject.FindProperty("keyLight"), new GUIContent("Key Light"));
                BrazeEditorGUI.ToggleButton("Dedup in VRAM", serializedObject.FindProperty("dedupResident"));
                BrazeEditorGUI.ToggleButton("Stream From Disk", serializedObject.FindProperty("streaming"));
                if (comp.streaming)
                    BrazeEditorGUI.Hint("Bounded VRAM — only a small ring of upcoming frames is resident, prefetched from " +
                                        "disk. Best for long clips / many players. Near-instant start. Set before Load.");

                BrazeEditorGUI.Hint("Final cache resolution, samples-per-voxel, and raymarch steps come from the active " +
                                    "Braze Render Feature, exactly like live containers. Playback owns transport, " +
                                    "residency, streaming, and the baked look.");

                if (comp.cache != null && comp.cache.IsValid)
                {
                    var c = comp.cache;
                    EditorGUILayout.HelpBox(
                        $"{c.frameCount} frames · {c.fps:0} fps · {c.Duration:0.0}s\n" +
                        $"grid {c.baseRes.x}×{c.baseRes.y}×{c.baseRes.z}\n" +
                        $"{BrazeContainer.FormatBytes(c.binaryBytes)} on disk · look: {(c.hasBakedLook ? "baked" : "default")}",
                        MessageType.None);
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "variation", "Instance Variation"))
            {
                BrazeEditorGUI.Slider(serializedObject.FindProperty("phaseOffset01"), 0f, 1f, new GUIContent("Phase Offset"));
                BrazeEditorGUI.ToggleButton("Randomize Phase", serializedObject.FindProperty("randomizePhase"));
                BrazeEditorGUI.Slider(serializedObject.FindProperty("speedVariance"), 0f, 0.9f, new GUIContent("Speed Variance"));
                BrazeEditorGUI.Hint("Desync duplicated instances so a crowd of the same clip doesn't play in lockstep. " +
                                    "Randomize Phase auto-offsets each copy; Speed Variance spreads their speeds. Both are " +
                                    "stable per instance (won't jitter).");
            }
            BrazeEditorGUI.EndSection();

            // LOOK group — mirrors the container's RENDERER group so the override re-grades with the same UI.
            bool gLook = BrazeEditorGUI.GroupBegin(K + "g_look", "LOOK", comp.overrideLook ? 1 : 0);
            if (gLook)
            {
                BrazeEditorGUI.ToggleButton("Override Look (re-grade this instance)", serializedObject.FindProperty("overrideLook"));
                if (comp.overrideLook)
                {
                    var lo = serializedObject.FindProperty("lookOverride");
                    var seeded = serializedObject.FindProperty("lookOverrideSeeded");
                    bool haveBaked = comp.cache != null && comp.cache.hasBakedLook;

                    // Seed the override from the clip's baked look the FIRST time it's enabled, so toggling it on doesn't
                    // jump the look to a generic default (which changes Soft Depth + raymarch and reads as a depth bug).
                    if (!seeded.boolValue && haveBaked)
                    {
                        lo.boxedValue = comp.cache.bakedLook;
                        seeded.boolValue = true;
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        BrazeEditorGUI.Hint("Started from the clip's baked look — tweak from here.");
                        using (new EditorGUI.DisabledScope(!haveBaked))
                            if (GUILayout.Button("Copy From Clip", GUILayout.Width(110)))
                                lo.boxedValue = comp.cache.bakedLook;
                    }
                    BrazeEditorGUI.Separator();

                    // The exact curated RENDERER UI from the container inspector (shared drawer): shared/legacy
                    // fields come straight off the override struct, slice-native fields off its embedded payload.
                    // The payload is versioned: on a legacy clip (version 0) the runtime ignores it, so its rows
                    // draw greyed until the user explicitly enables the current slice controls.
                    var slice = lo.FindPropertyRelative("slice");
                    var sliceVersion = slice.FindPropertyRelative("version");
                    bool sliceActive = sliceVersion != null && sliceVersion.intValue > 0;
                    if (!sliceActive)
                    {
                        EditorGUILayout.HelpBox(
                            "This clip predates the slice-renderer payload, so the greyed controls below are " +
                            "inert (the shared look still applies). Enable them to author the current renderer " +
                            "explicitly.", MessageType.Info);
                        if (GUILayout.Button("Enable Current Slice Controls"))
                            slice.boxedValue = BrazeSliceRenderSettings.Default;
                    }
                    BrazeRenderInspector.DrawVolumeLookControls("BrazeFX.CachePlayerLook.rnd",
                        n => lo.FindPropertyRelative(n), n => slice.FindPropertyRelative(n),
                        drawSun: false, sliceActive: sliceActive, liveContainer: null);

                }
                else
                    BrazeEditorGUI.Hint("Rendering with the look baked into the clip. Turn on Override Look to re-grade this instance.");
            }
            BrazeEditorGUI.GroupEnd(gLook);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.HelpBox(
                "Plays a baked cache STANDALONE — no container/simulator needed. The clip's data loads once and is " +
                "shared across all players of it, so you can duplicate this freely. Create caches with a Braze Cache " +
                "Baker. Needs the BrazeFX Volume renderer feature.",
                MessageType.None);
        }

        // NOTE deliberately NO "full slice payload" dump here. The payload's orphaned pyro-froxel look
        // fields (extinction, scatter albedo, fire intensity/power/opacity, a slice-local fire colour
        // system, AO, powder, sooty smoke colours…) were never sampled by the restored-response shader and
        // were REMOVED from BrazeSliceRenderSettings on 2026-07-10. The curated drawer above covers every
        // knob that is actually live.

        // Residency status line + Load / Unload buttons. Loading is async (off the main thread), so this repaints while
        // it streams. Load/Unload drive the GPU residency in Play mode (or via code: player.LoadCache()/UnloadCache()).
        void DrawResidency(BrazeCachePlayer comp)
        {
            string label; Color color;
            switch (comp.Status)
            {
                case BrazeCachePlayer.CacheStatus.Loading: label = "● Loading…"; color = new Color(0.95f, 0.75f, 0.25f); break;
                case BrazeCachePlayer.CacheStatus.Ready:   label = "● Ready";    color = new Color(0.45f, 0.82f, 0.45f); break;
                case BrazeCachePlayer.CacheStatus.Error:   label = $"● Error: {comp.StatusMessage}"; color = new Color(0.92f, 0.42f, 0.42f); break;
                default:                                   label = "● Unloaded"; color = new Color(0.6f, 0.6f, 0.6f); break;
            }
            var style = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel); style.normal.textColor = color; style.wordWrap = true;
            EditorGUILayout.LabelField(label, style);

            bool playing = Application.isPlaying;
            bool ready = comp.Status == BrazeCachePlayer.CacheStatus.Ready;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!playing || comp.Status == BrazeCachePlayer.CacheStatus.Loading || ready))
                    if (GUILayout.Button("Load", GUILayout.Height(22))) comp.LoadCache();
                using (new EditorGUI.DisabledScope(!playing || comp.Status == BrazeCachePlayer.CacheStatus.Unloaded))
                    if (GUILayout.Button("Unload", GUILayout.Height(22))) comp.UnloadCache();
            }
            // Transport — active only once Ready.
            using (new EditorGUI.DisabledScope(!playing || !ready))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(comp.IsPlaying ? "❚❚ Pause" : "▶ Play", GUILayout.Height(22)))
                { if (comp.IsPlaying) comp.Pause(); else comp.Play(); }
                if (GUILayout.Button("■ Stop", GUILayout.Height(22))) comp.Stop();
            }
            if (!playing)
                BrazeEditorGUI.Hint("Load / Play / Unload run in Play mode (or from code: player.LoadCache() / Play() / " +
                                    "Pause() / Stop() / UnloadCache()).");
            // Animate the brief async-LOAD spinner only (throttled). We deliberately do NOT force-repaint while
            // Playing: a per-frame Repaint() (a) redraws this whole heavy inspector every frame on the main thread,
            // tanking Play-mode FPS while the object is selected, and (b) races your value edits, making fields
            // blink/flicker mid-drag. Transport buttons repaint on click, so nothing live is lost. Editor-only.
            if (comp.Status == BrazeCachePlayer.CacheStatus.Loading && EditorApplication.timeSinceStartup >= nextRepaint)
            {
                nextRepaint = EditorApplication.timeSinceStartup + 0.1;
                Repaint();
            }
        }
    }

    /// <summary>
    /// Inspector for <see cref="BrazeCacheBaker"/>. Baking captures the sim DURING PLAY (so Unity's systems —
    /// animation, scripts, object lifecycles, the emitter's gameplay conditions — all run and the simulator reacts
    /// exactly like gameplay). The Bake button enters Play, the component records each frame at a fixed timestep,
    /// then it exits Play and writes the header asset. Driven by <see cref="BrazeCacheBakeDriver"/>.
    /// </summary>
    [CustomEditor(typeof(BrazeCacheBaker))]
    public sealed class BrazeCacheBakerEditor : Editor
    {
        const string K = "BrazeFX.CacheBaker.";

        public override void OnInspectorGUI()
        {
            var comp = (BrazeCacheBaker)target;
            serializedObject.Update();

            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("container"));

            bool bakeActive = BrazeCacheBakeDriver.Active;

            if (BrazeEditorGUI.Section(K + "bake", "Bake (create cache)"))
            {
                using (new EditorGUI.DisabledScope(bakeActive))
                {
                    BrazeEditorGUI.PropertyField(serializedObject.FindProperty("bakeFrames"));
                    BrazeEditorGUI.PropertyField(serializedObject.FindProperty("frameSkip"), new GUIContent("Frame Skip (store 1/N)"));
                    int skip = Mathf.Clamp(comp.frameSkip, 1, 4);
                    int stored = (Mathf.Max(1, comp.bakeFrames) + skip - 1) / skip;
                    BrazeEditorGUI.Hint(skip > 1
                        ? $"The sim advances every frame; every {skip}. frame is stored → {stored} stored frames, " +
                          $"~1/{skip} of the full-rate file size. Playback runs at the stored rate."
                        : "Every simulation frame is stored (largest file). Frame Skip 2 halves the cache and reads " +
                          "cleanly for most smoke/fire.");
                    BrazeEditorGUI.PropertyField(serializedObject.FindProperty("bakeCull"), new GUIContent("Sparse Cull"));

                    // No storage tiers here on purpose: game bakes always use the compact realtime representation
                    // (per-brick 8-bit + reconstruction scales, deflated on disk, dequantized on the GPU scatter).
                    // Archival/lossless tiers live in Export Studio, where OpenVDB delivery needs them.
                    BrazeEditorGUI.ToggleButton("Store Velocity", serializedObject.FindProperty("storeVelocity"));
                    BrazeEditorGUI.Hint(comp.storeVelocity
                        ? "Playback rebuilds Render Detail from the stored velocity at the playing device's own " +
                          "detail scale — the cache never stores a high-res detail texture. Velocity also drives " +
                          "fluid-following particles and OpenVDB vel export."
                        : "OFF: the clip plays as the unwarped base field — Render Detail is unavailable on playback " +
                          "and fluid-following particles have no motion. Only use for the smallest scalar-only clips.");
                    if (comp.storeVelocity)
                    {
                        BrazeEditorGUI.ToggleButton("Velocity Bounds (air halo)", serializedObject.FindProperty("velocityBounds"));
                        if (comp.velocityBounds)
                            BrazeEditorGUI.Hint("Bricks with ONLY air motion (no visible smoke) are also stored — " +
                                                "often larger than the smoke itself. Needed only for particles " +
                                                "tracking through clear air or a complete OpenVDB vel grid.");
                    }

                    // FPS is the container's own sim rate (its FPS Limit) — capturing at the sim rate avoids
                    // duplicate/skipped frames, so there's no separate knob.
                    int rate = comp.container != null ? comp.container.CacheBakeFps : 30;
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.IntField(new GUIContent("Bake FPS (from container)"), rate);

                    var folderProp = serializedObject.FindProperty("bakeFolder");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        BrazeEditorGUI.PropertyField(folderProp, new GUIContent("Output Folder"));
                        if (GUILayout.Button("…", GUILayout.Width(28)))
                        {
                            string start = string.IsNullOrEmpty(folderProp.stringValue) ? Application.dataPath : Path.GetFullPath(folderProp.stringValue);
                            string picked = EditorUtility.OpenFolderPanel("Fluid cache output folder", start, "");
                            if (!string.IsNullOrEmpty(picked)) folderProp.stringValue = ToProjectRelativeIfInside(picked);
                        }
                    }
                    BrazeEditorGUI.PropertyField(serializedObject.FindProperty("bakeName"));
                }
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();

            // ── Bake action zone (always visible so you can bake with the cards collapsed) ────────────────
            try
            {
                string nm = string.IsNullOrEmpty(comp.bakeName) ? "FluidCache" : comp.bakeName;
                ComputeBakePaths(comp.bakeFolder, nm, dryRun: true, out string binPath, out string assetPath, out _);
                EditorGUILayout.LabelField(" ", $"→ {binPath}", BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniLabel));
                if (IsExternal(comp.bakeFolder))
                    EditorGUILayout.HelpBox($"External binary (Unity won't import it). The header asset lands at {assetPath} and references the binary by absolute path.", MessageType.None);
            }
            catch { /* invalid folder mid-edit */ }

            EditorGUILayout.Space(6);
            if (bakeActive)
            {
                int done = comp.IsRecording ? comp.ProcessedFrames : 0;
                int total = comp.RecordTotalTarget > 0 ? comp.RecordTotalTarget : comp.bakeFrames;
                int warmup = comp.IsRecording ? comp.WarmupTarget : 0;
                EditorGUILayout.HelpBox(comp.IsRecording
                    ? (done < warmup
                        ? $"Simulating pre-roll… frame {done} / {warmup}"
                        : $"Recording gameplay… stored {comp.RecordedFrames} / {comp.StoredTarget} " +
                          $"(sim frame {done - warmup} / {comp.RecordTarget})")
                    : "Entering Play to record…", MessageType.Info);
                Rect r = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(r, total > 0 ? (float)done / total : 0f, $"{done}/{total}");
                if (GUILayout.Button("Cancel (stop Play)", GUILayout.Height(24)))
                    EditorApplication.ExitPlaymode();
                Repaint();
            }
            else
            {
                using (new EditorGUI.DisabledScope(comp.container == null || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    var bg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.55f, 0.85f, 0.5f);
                    int rate = comp.container != null ? comp.container.CacheBakeFps : 30;
                    int bakeSkip = Mathf.Clamp(comp.frameSkip, 1, 4);
                    int bakeStored = (Mathf.Max(1, comp.bakeFrames) + bakeSkip - 1) / bakeSkip;
                    if (GUILayout.Button($"▶ Bake in Play  ({comp.bakeFrames} sim frames @ {rate}fps → {bakeStored} stored)",
                                         GUILayout.Height(30)))
                        TriggerBake(comp);
                    GUI.backgroundColor = bg;
                }
            }

            EditorGUILayout.HelpBox(
                "Bake enters Play and captures the LIVE sim (animation, scripts, emitter conditions all run) at a " +
                "fixed timestep, then exits. Caches density + temperature + burn + velocity as sparse bricks in a " +
                "compact realtime format built for streaming playback; Render Detail is rebuilt from the velocity. " +
                "Play it back with a Braze Cache Player. Needs the BrazeFX Volume renderer feature. Lossless " +
                "archival tiers for OpenVDB delivery live in Export Studio.",
                MessageType.None);

            EditorGUILayout.Space(5f);
            if (GUILayout.Button("Open in Export Studio", GUILayout.Height(26f)))
                BrazeExportWindow.OpenFor(comp.container);
        }

        void TriggerBake(BrazeCacheBaker comp)
        {
            if (comp.container == null) { EditorUtility.DisplayDialog("BrazeFX", "Assign a Container first.", "OK"); return; }
            string clipName = string.IsNullOrEmpty(comp.bakeName) ? "FluidCache" : comp.bakeName;
            string binPath, assetPath, binFile;
            try { ComputeBakePaths(comp.bakeFolder, clipName, dryRun: false, out binPath, out assetPath, out binFile); }
            catch (Exception e) { EditorUtility.DisplayDialog("BrazeFX", $"Bad output folder:\n{e.Message}", "OK"); return; }
            BrazeCacheBakeDriver.StartBake(comp, assetPath, binPath, binFile, comp.bakeFrames, comp.container.CacheBakeFps, comp.bakeCull);
        }

        // ── Output path resolution ───────────────────────────────────────────────────────────────────
        internal static void ComputeBakePaths(string folder, string name, bool dryRun,
                                              out string binPath, out string assetPath, out string binaryFileField)
        {
            if (string.IsNullOrEmpty(folder)) folder = "Assets/BrazeFX Caches";
            string assetsAbs = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string folderAbs = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
            if (!dryRun) Directory.CreateDirectory(folderAbs);
            binPath = folderAbs + "/" + name + ".brazecache";

            if (IsPathInside(folderAbs, assetsAbs))
            {
                string rel = "Assets" + folderAbs.Substring(assetsAbs.Length);
                assetPath = rel.TrimEnd('/') + "/" + name + ".asset";
                binaryFileField = name + ".brazecache";
            }
            else
            {
                const string def = "Assets/BrazeFX Caches";
                if (!dryRun) Directory.CreateDirectory(Path.Combine(Directory.GetParent(assetsAbs).FullName, def));
                assetPath = def + "/" + name + ".asset";
                binaryFileField = binPath;
            }
        }

        static bool IsExternal(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string assetsAbs = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string folderAbs = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
            return !IsPathInside(folderAbs, assetsAbs);
        }

        static string ToProjectRelativeIfInside(string abs)
        {
            string assetsAbs = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string a = Path.GetFullPath(abs).Replace('\\', '/').TrimEnd('/');
            return IsPathInside(a, assetsAbs) ? "Assets" + a.Substring(assetsAbs.Length) : abs;
        }

        static bool IsPathInside(string candidate, string root)
            => string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Orchestrates a play-mode bake across the domain reload that entering Play causes: state lives in
    /// SessionState; the target component is re-found by GlobalObjectId. Enters Play → tells the component to
    /// record → waits for it → exits Play → reads the binary's header and writes the cache asset.
    /// </summary>
    [InitializeOnLoad]
    static class BrazeCacheBakeDriver
    {
        const string KActive = "BrazeFX.Bake.Active";
        const string KGid = "BrazeFX.Bake.Gid";
        const string KContainerGid = "BrazeFX.Bake.ContainerGid";
        const string KTimelineGid = "BrazeFX.Bake.TimelineGid";
        // Keep the original SessionState key so an in-flight bake survives this locator migration.
        const string KSessionLocator = "BrazeFX.Bake.ExportSessionAsset";
        const string KAsset = "BrazeFX.Bake.Asset";
        const string KBin = "BrazeFX.Bake.Bin";
        const string KBinFile = "BrazeFX.Bake.BinFile";
        const string KFrames = "BrazeFX.Bake.Frames";
        const string KWarmupFrames = "BrazeFX.Bake.WarmupFrames";
        const string KFps = "BrazeFX.Bake.Fps";
        const string KCull = "BrazeFX.Bake.Cull";
        const string KFrameSkip = "BrazeFX.Bake.FrameSkip";
        const string KStorage = "BrazeFX.Bake.Storage";
        const string KStoreVelocity = "BrazeFX.Bake.StoreVelocity";
        const string KVelocityBounds = "BrazeFX.Bake.VelocityBounds";
        const string KCaptureRenderDetail = "BrazeFX.Bake.CaptureRenderDetail";
        const string KSuccess = "BrazeFX.Bake.Success";
        const string KError = "BrazeFX.Bake.Error";
        const string KAppearance = "BrazeFX.Bake.Appearance";
        const string KSourceFingerprint = "BrazeFX.Bake.SourceFingerprint";
        const string KJobId = "BrazeFX.Bake.JobId";

        static BrazeCacheBaker active;

        [Serializable]
        sealed class RecordedAppearance
        {
            public bool valid;
            public BrazeRenderSettings look;
            public Vector3 keyLightDirection;
            public Color keyLightColor;
        }

        public static bool Active => SessionState.GetBool(KActive, false);
        public static BrazeCacheBaker ActiveBaker => active;

        static BrazeCacheBakeDriver()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnUpdate;
        }

        public static void StartBake(BrazeCacheBaker comp, string assetPath, string binPath, string binFile, int frames, int fps, float cull)
        {
            if (comp == null || comp.container == null || !comp.container.gameObject.scene.IsValid() ||
                !comp.container.gameObject.scene.isLoaded)
                throw new InvalidOperationException("The Cache Baker needs a Container in a loaded scene.");
            BeginBake(GlobalId(comp), GlobalId(comp != null ? comp.container : null),
                GlobalId(comp != null ? comp.GetComponentInParent<BrazeTimeline>() : null), string.Empty,
                assetPath, binPath, binFile, frames, 0, fps, cull,
                comp != null ? Mathf.Clamp(comp.frameSkip, 1, 4) : 1,
                BrazeCacheBaker.Storage.Realtime,   // standalone game bakes are always the realtime representation
                comp == null || comp.storeVelocity, false,
                comp != null && comp.velocityBounds, string.Empty, string.Empty);
        }

        /// <summary>Start an Export Studio bake without requiring a persistent Cache Baker component in the scene.</summary>
        public static void StartBake(BrazeContainer container, BrazeExportSession session,
            string assetPath, string binPath, string binFile, int frames, int warmupFrames,
            int fps, float cull, BrazeCacheBaker.Storage storage, bool storeVelocity = true,
            bool captureRenderDetail = false)
        {
            if (container == null || !container.gameObject.scene.IsValid() || !container.gameObject.scene.isLoaded)
                throw new InvalidOperationException("Export Studio needs a Container in a loaded scene.");
            // The parameters remain in the signature so prerelease integrations continue to compile, but the current
            // Studio deliberately has one master. Do not let a legacy caller resurrect raw/quantized storage,
            // scalar-only data, or the native-detail stream that made old masters enormous.
            storage = BrazeCacheBaker.Storage.Compact;
            storeVelocity = true;
            captureRenderDetail = false;
            string sessionLocator = BrazeExportSessionStore.Locator(session);
            string jobId = session != null
                ? session.BeginJob(BrazeExportSession.JobKind.MasterCache, "Entering Play Mode for cache bake")
                : string.Empty;
            try
            {
                // Export Studio masters keep every simulated frame and the complete velocity halo — VDB delivery
                // needs the full authored rate and an air-motion-complete `vel` grid.
                BeginBake(string.Empty, GlobalId(container), string.Empty,
                    sessionLocator, assetPath, binPath, binFile,
                    frames, warmupFrames, fps, cull, 1, storage, storeVelocity, captureRenderDetail, true,
                    session != null ? BrazeExportFingerprint.SourceFingerprint(session) : string.Empty, jobId);
            }
            catch (Exception e)
            {
                session?.FinishJob(jobId, BrazeExportSession.JobState.Failed, "Cache bake could not start", e.Message);
                throw;
            }
        }

        static void BeginBake(string bakerId, string containerId, string timelineId, string sessionLocator,
            string assetPath, string binPath, string binFile, int frames, int warmupFrames,
            int fps, float cull, int frameSkip, BrazeCacheBaker.Storage storage, bool storeVelocity,
            bool captureRenderDetail, bool velocityBounds, string sourceFingerprint, string jobId)
        {
            if (Active) throw new InvalidOperationException("A Braze cache bake is already running.");
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null && existing is not BrazeFluidCacheAsset)
                throw new InvalidOperationException($"'{assetPath}' is already occupied by {existing.GetType().Name}.");
            SessionState.SetString(KGid, bakerId);
            SessionState.SetString(KContainerGid, containerId);
            SessionState.SetString(KTimelineGid, timelineId);
            SessionState.SetString(KSessionLocator, sessionLocator);
            SessionState.SetString(KAsset, assetPath);
            SessionState.SetString(KBin, binPath);
            SessionState.SetString(KBinFile, binFile);
            SessionState.SetInt(KFrames, Mathf.Max(1, frames));
            SessionState.SetInt(KWarmupFrames, Mathf.Max(0, warmupFrames));
            SessionState.SetInt(KFps, Mathf.Max(1, fps));
            SessionState.SetFloat(KCull, cull);
            SessionState.SetInt(KFrameSkip, Mathf.Clamp(frameSkip, 1, 4));
            SessionState.SetInt(KStorage, (int)storage);
            SessionState.SetBool(KStoreVelocity, storeVelocity);
            SessionState.SetBool(KCaptureRenderDetail, captureRenderDetail);
            SessionState.SetBool(KVelocityBounds, velocityBounds);
            SessionState.SetBool(KSuccess, false);
            SessionState.SetString(KError, "Bake cancelled before completion.");
            SessionState.SetString(KAppearance, string.Empty);
            SessionState.SetString(KSourceFingerprint, sourceFingerprint ?? string.Empty);
            SessionState.SetString(KJobId, jobId ?? string.Empty);
            SessionState.SetBool(KActive, true);
            EditorApplication.EnterPlaymode();
        }

        static string GlobalId(UnityEngine.Object value)
            => value == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();

        static T Resolve<T>(string key) where T : UnityEngine.Object
        {
            if (GlobalObjectId.TryParse(SessionState.GetString(key, ""), out var gid))
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as T;
            return null;
        }

        static BrazeCacheBaker ResolveBaker()
        {
            BrazeCacheBaker baker = Resolve<BrazeCacheBaker>(KGid);
            if (baker != null) return baker;
            BrazeContainer container = Resolve<BrazeContainer>(KContainerGid);
            return container != null ? container.GetComponent<BrazeCacheBaker>() : null;
        }

        static void OnPlayModeChanged(PlayModeStateChange s)
        {
            if (!Active) return;
            if (s == PlayModeStateChange.EnteredPlayMode)
            {
                active = ResolveBaker();
                if (active == null)
                {
                    BrazeContainer container = Resolve<BrazeContainer>(KContainerGid);
                    if (container != null)
                    {
                        active = container.gameObject.AddComponent<BrazeCacheBaker>();
                        active.hideFlags = HideFlags.DontSaveInEditor;
                        active.container = container;
                    }
                }
                if (active == null)
                {
                    SessionState.SetString(KError, "Source Container was not found after entering Play Mode.");
                    Debug.LogError("[BrazeFX] Bake: source Container not found in Play mode.");
                    EditorApplication.ExitPlaymode();
                    return;
                }
                active.storage = (BrazeCacheBaker.Storage)SessionState.GetInt(
                    KStorage, (int)BrazeCacheBaker.Storage.Realtime);
                active.storeVelocity = SessionState.GetBool(KStoreVelocity, false);
                active.captureRenderDetail = SessionState.GetBool(KCaptureRenderDetail, false);
                active.velocityBounds = SessionState.GetBool(KVelocityBounds, true);
                active.frameSkip = Mathf.Clamp(SessionState.GetInt(KFrameSkip, 1), 1, 4);
                if (active.container == null) active.container = Resolve<BrazeContainer>(KContainerGid);
                active.captureTimeline = Resolve<BrazeTimeline>(KTimelineGid);
                active.BeginRecording(SessionState.GetInt(KFrames, 120), SessionState.GetInt(KFps, 30),
                                      SessionState.GetFloat(KCull, 0.004f), SessionState.GetString(KBin, ""),
                                      SessionState.GetInt(KWarmupFrames, 0));
            }
            else if (s == PlayModeStateChange.ExitingPlayMode)
            {
                PersistResult();
            }
            else if (s == PlayModeStateChange.EnteredEditMode)
            {
                if (SessionState.GetBool(KSuccess, false))
                {
                    if (BuildAsset()) FinishSessionJob(BrazeExportSession.JobState.Completed, "Master cache baked");
                    else FinishSessionJob(BrazeExportSession.JobState.Failed, "Cache bake output could not be published");
                }
                else
                {
                    string error = SessionState.GetString(KError, "Bake did not complete.");
                    Debug.LogWarning($"[BrazeFX] Bake not published: {error} The previous cache was left untouched.");
                    FinishSessionJob(error.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0
                        ? BrazeExportSession.JobState.Cancelled : BrazeExportSession.JobState.Failed,
                        "Master cache was not published", error);
                }
                Cleanup();
            }
        }

        static void OnUpdate()
        {
            if (!Active || !EditorApplication.isPlaying) return;
            if (active == null) active = ResolveBaker();
            if (active == null || !active.RecordingDone) return;
            PersistResult();
            if (!active.RecordingSucceeded) Debug.LogError($"[BrazeFX] Bake error: {active.RecordError}");
            EditorApplication.ExitPlaymode();
        }

        static void PersistResult()
        {
            if (active == null) return;
            SessionState.SetBool(KSuccess, active.RecordingDone && active.RecordingSucceeded);
            SessionState.SetString(KError, string.IsNullOrEmpty(active.RecordError)
                ? (active.RecordingSucceeded ? "" : "Bake cancelled before completion.")
                : active.RecordError);
            if (active.HasRecordedLook)
            {
                var appearance = new RecordedAppearance
                {
                    valid = true,
                    look = active.RecordedLook,
                    keyLightDirection = active.RecordedKeyLightDirection,
                    keyLightColor = active.RecordedKeyLightColor,
                };
                SessionState.SetString(KAppearance, JsonUtility.ToJson(appearance));
            }
        }

        static bool BuildAsset()
        {
            string binPath = SessionState.GetString(KBin, "");
            string assetPath = SessionState.GetString(KAsset, "");
            string binFile = SessionState.GetString(KBinFile, "");
            if (string.IsNullOrEmpty(binPath) || !File.Exists(binPath)) { Debug.LogWarning("[BrazeFX] Bake produced no file."); return false; }

            BrazeFluidCacheFormat.Header h;
            try { using var rd = new BrazeFluidCacheFormat.Reader(binPath); h = rd.header; }
            catch (Exception e) { Debug.LogError($"[BrazeFX] Bake: can't read cache header: {e.Message}"); return false; }
            if (h.frameCount <= 0) { Debug.LogWarning("[BrazeFX] Bake wrote 0 frames (did the sim produce anything during play?)."); return false; }

            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null && existing is not BrazeFluidCacheAsset)
            {
                Debug.LogError($"[BrazeFX] Bake: '{assetPath}' is occupied by {existing.GetType().Name}; the cache asset was not replaced.");
                return false;
            }
            var asset = existing as BrazeFluidCacheAsset;
            bool isNew = asset == null;
            if (isNew) asset = ScriptableObject.CreateInstance<BrazeFluidCacheAsset>();
            asset.version = BrazeFluidCacheFormat.Version;
            asset.baseRes = h.baseRes; asset.warpRes = h.warpRes; asset.brickSize = h.brickSize;
            asset.hasVelocity = BrazeFluidCacheFormat.HasVelocity(h);
            asset.scalarQuantized = (h.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;
            asset.frameCount = h.frameCount; asset.fps = h.fps; asset.worldSizeAtBake = h.worldSize;
            asset.binaryFile = binFile; asset.binaryBytes = new FileInfo(binPath).Length;

            var baker = Resolve<BrazeCacheBaker>(KGid); // edit-mode instance, when the legacy component workflow was used
            var container = Resolve<BrazeContainer>(KContainerGid);
            if (container == null && baker != null) container = baker.container;

            // Stamp the look captured when recording begins (after any legacy Cache Baker warmup), so a
            // container-free player starts from the same authored appearance as the master cache.
            RecordedAppearance appearance = null;
            string appearanceJson = SessionState.GetString(KAppearance, string.Empty);
            if (!string.IsNullOrEmpty(appearanceJson)) appearance = JsonUtility.FromJson<RecordedAppearance>(appearanceJson);
            if (appearance != null && appearance.valid)
            {
                asset.bakedLook = appearance.look;
                asset.hasBakedLook = true;
                asset.bakedKeyLightDirection = appearance.keyLightDirection;
                asset.bakedKeyLightColor = appearance.keyLightColor;
                asset.hasBakedKeyLight = true;
            }
            else if (container != null)
            {
                asset.bakedLook = container.CaptureRenderSettings();
                asset.hasBakedLook = true;
                asset.bakedKeyLightDirection = container.SliceMainLightDirection();
                asset.bakedKeyLightColor = container.SliceMainLightColor();
                asset.hasBakedKeyLight = true;
            }

            if (isNew) AssetDatabase.CreateAsset(asset, assetPath);
            BrazeCacheBuildProcessor.EnsureBuildPath(asset, assetPath);
            BrazeCacheBuildProcessor.EnsureBuildFingerprint(asset, binPath);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            BrazeExportSession session = ResolveExportSession();
            if (session != null)
            {
                session.cache = asset;
                SynchronizeSessionTiming(session, asset);
                // A derived Region is tied to the exact master payload. Never let an apparently-valid Region from
                // the previous bake remain export-ready after the master has been replaced. Keep the reference so
                // Clear Bake can still clean up the generated asset; the next export safely rebuilds it in place.
                session.derivedRegionFingerprint = string.Empty;
                session.bakedSourceFingerprint = SessionState.GetString(KSourceFingerprint, string.Empty);
                session.bakedCacheFingerprint = BrazeExportFingerprint.CacheFingerprint(asset);
                session.lastBakeUtc = DateTime.UtcNow.ToString("O");
                session.Persist();
            }

            // Convenience: if a player sits on the same GameObject, point it at the fresh clip so you can hit Play.
            GameObject sourceObject = baker != null ? baker.gameObject : container != null ? container.gameObject : null;
            if (sourceObject != null)
            {
                var player = sourceObject.GetComponent<BrazeCachePlayer>();
                if (player != null) { player.cache = asset; EditorUtility.SetDirty(player); }
            }
            Debug.Log($"[BrazeFX] Baked {h.frameCount} frames → {binPath} ({BrazeContainer.FormatBytes(asset.binaryBytes)}). " +
                      "Assign it to a Braze Cache Player and set Mode = Play to view.");
            return true;
        }

        internal static void SynchronizeSessionTiming(BrazeExportSession session, BrazeFluidCacheAsset asset)
        {
            if (session == null || asset == null || !asset.IsValid) return;
            float previousDuration = session.duration;
            float tolerance = 0.5f / Mathf.Max(1f, asset.fps);
            bool fullRegion = session.regionIn <= tolerance &&
                              session.regionOut >= previousDuration - tolerance;
            session.duration = asset.Duration;
            if (fullRegion)
            {
                session.regionIn = 0f;
                session.regionOut = asset.Duration;
            }
            session.Normalize();
        }

        static void FinishSessionJob(BrazeExportSession.JobState state, string status, string error = null)
        {
            BrazeExportSession session = ResolveExportSession();
            session?.FinishJob(SessionState.GetString(KJobId, string.Empty), state, status, error);
        }

        static BrazeExportSession ResolveExportSession()
            => BrazeExportSessionStore.Resolve(SessionState.GetString(KSessionLocator, string.Empty));

        static void Cleanup()
        {
            SessionState.SetBool(KActive, false);
            SessionState.EraseBool(KSuccess);
            SessionState.EraseString(KError);
            SessionState.EraseString(KAppearance);
            SessionState.EraseString(KSourceFingerprint);
            SessionState.EraseString(KJobId);
            SessionState.EraseString(KGid);
            SessionState.EraseString(KContainerGid);
            SessionState.EraseString(KTimelineGid);
            SessionState.EraseString(KSessionLocator);
            SessionState.EraseString(KAsset);
            SessionState.EraseString(KBin);
            SessionState.EraseString(KBinFile);
            SessionState.EraseInt(KFrames);
            SessionState.EraseInt(KWarmupFrames);
            SessionState.EraseInt(KFps);
            SessionState.EraseFloat(KCull);
            SessionState.EraseInt(KFrameSkip);
            SessionState.EraseInt(KStorage);
            SessionState.EraseBool(KStoreVelocity);
            SessionState.EraseBool(KCaptureRenderDetail);
            SessionState.EraseBool(KVelocityBounds);
            active = null;
        }
    }
}
