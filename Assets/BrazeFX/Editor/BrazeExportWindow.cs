using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>First-class cache, loop, and delivery authoring surface for BrazeFX.</summary>
    public sealed class BrazeExportWindow : EditorWindow
    {
        const float HeaderHeight = 42f;
        const float DashboardHeight = 276f;
        const float MinimumTimelineHeight = 204f;
        const float Gap = 4f;
        const float RegionClipYOffset = 36f;
        const float RegionClipHeight = 58f;
        const float RegionClipFooterHeight = 16f;

        enum DragTarget { None, Playhead, RegionIn, RegionOut, RegionBody }

        BrazeExportSession session;
        BrazeContainer pendingContainer;
        float playhead;
        bool playing;
        int playbackDirection = 1;
        double lastTick;
        DragTarget dragTarget;
        float dragAnchorTime;
        float dragRegionIn;
        float dragRegionOut;
        BrazeExportCachePreview cachePreview;

        static readonly Color Background = new(0.075f, 0.078f, 0.086f);
        static readonly Color Grid = new(1f, 1f, 1f, 0.08f);

        [MenuItem("Window/BrazeFX/Export Studio", priority = 101)]
        public static void Open()
        {
            BrazeExportWindow window = GetWindow<BrazeExportWindow>();
            window.titleContent = new GUIContent("Braze Export Studio");
            window.minSize = new Vector2(980f, 620f);
            window.Show();
        }

        public static void OpenFor(BrazeContainer container)
        {
            Open();
            BrazeExportWindow window = GetWindow<BrazeExportWindow>();
            window.pendingContainer = container;
            if (window.session != null)
            {
                window.BindContainer(container);
                window.pendingContainer = null;
            }
            window.Repaint();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Braze Export Studio");
            minSize = new Vector2(980f, 620f);
            session = BrazeExportSessionStore.Current;
            playhead = session.regionIn;
            if (pendingContainer != null)
            {
                BindContainer(pendingContainer);
                pendingContainer = null;
            }
            cachePreview = new BrazeExportCachePreview();
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            if (session != null) session.Persist();
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            cachePreview?.Dispose();
            cachePreview = null;
        }

        void OnPlayModeChanged(PlayModeStateChange _)
        {
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                cachePreview?.Dispose();
            if (session != null) session.ResolveSources();
            Repaint();
        }

        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            double delta = lastTick > 0d ? now - lastTick : 0d;
            lastTick = now;
            // Authoring operations stay frame-snapped, but active playback must accumulate sub-frame Editor deltas.
            // Snapping here while playing erases every update smaller than half a frame and makes Play look frozen.
            if (session != null && !playing) ClampPlayheadToRegion();
            if (playing && session != null && !BrazeCacheBakeDriver.Active)
            {
                int requestedFrame = session.cache != null && session.cache.IsValid
                    ? Mathf.Clamp(Mathf.RoundToInt(playhead * session.cache.fps), 0,
                        Mathf.Max(0, session.cache.frameCount - 1))
                    : -1;
                // Keep the authoring transport on its requested frame while a bounded stream buffers. Advancing the
                // external playhead regardless of readiness causes the same stale-decode chase as runtime playback.
                bool canAdvance = requestedFrame < 0 || cachePreview == null ||
                                  cachePreview.Status == BrazeCachePlayer.CacheStatus.Ready &&
                                  cachePreview.CurrentFrame == requestedFrame;
                if (canAdvance)
                {
                    playing = AdvanceTransport(session, ref playhead, (float)delta, ref playbackDirection);
                    Repaint();
                }
            }
            bool deliveryActive = BrazeFlipbookExporter.Active || BrazeVolumeExporter.Active;
            if (!deliveryActive && cachePreview != null &&
                cachePreview.TickScene(session, playhead, playing, playbackDirection)) Repaint();
            if (BrazeCacheBakeDriver.Active) Repaint();
            if (BrazeFlipbookExporter.Active) Repaint();
            if (BrazeVolumeExporter.Active) Repaint();
        }

        internal static bool AdvanceTransport(BrazeExportSession session, ref float time, float delta,
            ref int direction)
        {
            if (session == null) return false;
            float start = session.regionIn;
            float end = session.regionOut;
            float last = Mathf.Max(start, end - session.FrameDuration);
            time += Mathf.Max(0f, delta) * (direction < 0 ? -1f : 1f);
            if (session.regionPlayback == BrazeExportSession.RegionPlayback.Once && time >= end)
            {
                time = last;
                return false;
            }
            if (session.regionPlayback == BrazeExportSession.RegionPlayback.PingPong)
            {
                if (time >= last)
                {
                    time = Mathf.Max(start, last - (time - last));
                    direction = -1;
                }
                else if (time <= start)
                {
                    time = Mathf.Min(last, start + (start - time));
                    direction = 1;
                }
            }
            else if (time >= end)
            {
                time = start + Mathf.Repeat(time - start, Mathf.Max(session.FrameDuration, end - start));
            }
            return true;
        }

        void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(Vector2.zero, position.size), Background);
            DrawHeader(new Rect(0f, 0f, position.width, HeaderHeight));

            session ??= BrazeExportSessionStore.Current;

            float availableDashboardHeight = position.height - HeaderHeight - Gap * 2f - MinimumTimelineHeight;
            float desiredDashboardHeight = DashboardHeight + (session.flipbookAutoBounds ? 0f : 22f);
            float dashboardHeight = Mathf.Min(desiredDashboardHeight, Mathf.Max(220f, availableDashboardHeight));
            Rect dashboard = new(0f, HeaderHeight + Gap, position.width, dashboardHeight);
            Rect timeline = new(0f, dashboard.yMax + Gap, position.width,
                Mathf.Max(MinimumTimelineHeight, position.height - dashboard.yMax - Gap));

            DrawDashboard(dashboard);
            DrawTimeline(timeline);
        }

        void DrawDashboard(Rect rect)
        {
            Rect content = new(rect.x + 8f, rect.y + 8f, rect.width - 16f, rect.height - 16f);
            const float cardGap = 6f;
            float cardWidth = (content.width - cardGap * 2f) / 3f;
            Rect source = new(content.x, content.y, cardWidth, content.height);
            Rect flipbook = new(source.xMax + cardGap, content.y, cardWidth, content.height);
            Rect volume = new(flipbook.xMax + cardGap, content.y, content.xMax - flipbook.xMax - cardGap, content.height);

            DrawSourceAndCache(source);
            DrawFlipbookCard(flipbook);
            DrawVolumeCard(volume);
        }

        void DrawHeader(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.045f, 0.047f, 0.052f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), BrazeEditorGUI.Accent);
            GUIStyle brand = new(EditorStyles.boldLabel) { fontSize = 15, normal = { textColor = BrazeEditorGUI.Accent } };
            GUI.Label(new Rect(12f, 10f, 190f, 22f), "BRAZE  EXPORT STUDIO", brand);
        }

        void DrawSourceAndCache(Rect rect)
        {
            DrawCard(rect, "SOURCE");
            GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 29f, rect.width - 16f, rect.height - 37f));

            BrazeContainer container = session.SourceContainer;
            EditorGUI.BeginChangeCheck();
            container = (BrazeContainer)EditorGUILayout.ObjectField("Container", container, typeof(BrazeContainer), true);
            if (EditorGUI.EndChangeCheck()) BindContainer(container);

            if (container == null)
            {
                BrazeContainer selected = SelectedContainer();
                using (new EditorGUI.DisabledScope(selected == null))
                    if (GUILayout.Button(selected != null ? $"Use Selected: {selected.name}" : "Select a Container"))
                        BindContainer(selected);
            }
            else if (!SourceIsLoadedScene)
            {
                EditorGUILayout.HelpBox("The source must be a Container instance in a loaded scene, not a prefab or project asset.",
                    MessageType.Error);
            }
            else if (string.IsNullOrEmpty(container.gameObject.scene.path))
            {
                EditorGUILayout.HelpBox("Save this scene before baking so the Export Session can restore its source after Play Mode reloads.",
                    MessageType.Warning);
            }
            EditorGUILayout.Space(6f);
            CompactSectionTitle("MASTER CACHE");
            SerializedObject so = new(session);
            so.Update();
            EditorGUI.BeginChangeCheck();
            // One master form, no modes: lossless Compact, scalars + velocity. It is simultaneously the exact
            // scrub/flipbook source and the authoritative OpenVDB source; Render Detail rebuilds from velocity.
            // Small realtime game clips are the Cache Baker's job, not Export Studio's.
            EditorGUILayout.PropertyField(so.FindProperty("cacheOutputFolder"), new GUIContent("Working Folder"));
            bool changed = EditorGUI.EndChangeCheck();
            if (so.ApplyModifiedProperties()) changed = true;
            if (changed) NormalizeAndSave();
            EditorGUILayout.LabelField(
                "Lossless master · scalars + velocity · Render Detail rebuilt from velocity",
                EditorStyles.miniLabel);

            bool cacheFileReady = TryGetBakedCacheFile(out string bakedPath, out long bakedBytes);
            bool cacheFormatOutdated = cacheFileReady && !CacheMatchesLeanMasterFormat();
            bool cacheReady = cacheFileReady && !cacheFormatOutdated;
            bool cacheNeedsRebake = session.cache != null &&
                                    session.cache.version != BrazeFluidCacheFormat.Version;
            EditorGUILayout.Space(4f);
            if (cacheReady)
            {
                string displayPath = DisplayCachePath(bakedPath);
                Rect bakedInfo = EditorGUILayout.GetControlRect(false, 18f);
                GUIStyle infoStyle = new(EditorStyles.miniLabel)
                {
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(0.72f, 0.75f, 0.8f) }
                };
                string summary = $"{BrazeContainer.FormatBytes(bakedBytes)}  ·  {displayPath}";
                GUI.Label(bakedInfo, new GUIContent(summary, displayPath), infoStyle);
            }
            else
            {
                GUIStyle status = new(EditorStyles.miniLabel);
                if (cacheNeedsRebake || cacheFormatOutdated)
                    status.normal.textColor = new Color(1f, 0.62f, 0.28f);
                EditorGUILayout.LabelField(cacheFormatOutdated
                    ? "Cache predates the lean master format · re-bake to replace it atomically"
                    : cacheNeedsRebake
                    ? "Older cache format · final-look rebake required"
                    : "Not baked yet", status);
            }

            if (cachePreview != null && cachePreview.Status == BrazeCachePlayer.CacheStatus.Error)
                EditorGUILayout.HelpBox($"Scene preview could not load: {cachePreview.Error}", MessageType.Error);

            GUILayout.FlexibleSpace();
            bool bakeBusy = BrazeCacheBakeDriver.Active;
            if (bakeBusy)
            {
                BrazeCacheBaker baker = BrazeCacheBakeDriver.ActiveBaker;
                int done = baker != null ? baker.ProcessedFrames : 0;
                int total = baker != null ? baker.RecordTotalTarget : session.CaptureFrameCount;
                Rect progress = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(progress, total > 0 ? Mathf.Clamp01((float)done / total) : 0f,
                    baker != null ? $"Capturing {baker.RecordedFrames} / {baker.RecordTarget}" : "Entering Play Mode…");
                Color old = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.86f, 0.34f, 0.25f);
                if (GUILayout.Button("CANCEL BAKE", GUILayout.Height(25f))) EditorApplication.ExitPlaymode();
                GUI.backgroundColor = old;
            }
            else
            {
                bool deliveryBusy = EditorApplication.isPlayingOrWillChangePlaymode ||
                                    BrazeFlipbookExporter.Active || BrazeVolumeExporter.Active;
                bool bakeDisabled = session.SourceContainer == null || !SourceIsLoadedScene || deliveryBusy;
                Rect actions = EditorGUILayout.GetControlRect(false, 25f);
                Rect bakeAction = Cell(actions, 0, 2, 5f);
                Rect clearAction = Cell(actions, 1, 2, 5f);
                if (cacheReady)
                {
                    Color oldBackground = GUI.backgroundColor;
                    Color oldContent = GUI.contentColor;
                    GUI.backgroundColor = new Color(0.28f, 0.66f, 0.36f);
                    GUI.contentColor = new Color(0.72f, 1f, 0.76f);
                    GUI.Box(bakeAction, "BAKED", GUI.skin.button);
                    GUI.backgroundColor = oldBackground;
                    GUI.contentColor = oldContent;
                }
                else
                {
                    using (new EditorGUI.DisabledScope(bakeDisabled))
                        if (GUI.Button(bakeAction, "BAKE")) StartBake();
                }
                using (new EditorGUI.DisabledScope(session.cache == null || deliveryBusy))
                    if (GUI.Button(clearAction, "CLEAR BAKE")) ClearBake();
            }
            GUILayout.EndArea();
        }

        void DrawFlipbookCard(Rect rect)
        {
            DrawCard(rect, "FLIPBOOK");
            SerializedObject flipbook = new(session);
            flipbook.Update();
            Rect row = new(rect.x + 8f, rect.y + 29f, rect.width - 16f, 19f);
            EditorGUI.BeginChangeCheck();
            DrawProperty(Cell(row, 0, 3, 6f, 2), flipbook.FindProperty("outputFolder"), "Folder", 44f);
            DrawProperty(Cell(row, 2, 3), flipbook.FindProperty("outputName"), "Name", 38f);

            // Region derivation, alpha representation, edge dilation, and player pivot are pipeline details with
            // safe defaults. Keep the delivery surface focused on choices that materially define the atlas.
            row.y += 24f;
            DrawProperty(Cell(row, 0, 2), flipbook.FindProperty("flipbookView"), "View", 38f);
            DrawProperty(Cell(row, 1, 2), flipbook.FindProperty("flipbookProjection"), "Projection", 60f);

            row.y += 22f;
            DrawProperty(Cell(row, 0, 2), flipbook.FindProperty("flipbookLayout"), "Layout", 42f);
            DrawProperty(Cell(row, 1, 2), flipbook.FindProperty("flipbookFormat"), "Format", 42f);

            bool manualLayout = (BrazeExportSession.FlipbookLayout)
                flipbook.FindProperty("flipbookLayout").enumValueIndex == BrazeExportSession.FlipbookLayout.Manual;
            row.y += 22f;
            if (manualLayout)
            {
                DrawProperty(Cell(row, 0, 2), flipbook.FindProperty("flipbookColumns"), "Columns", 54f);
                DrawProperty(Cell(row, 1, 2), flipbook.FindProperty("flipbookRows"), "Rows", 34f);
            }
            else
                DrawVector2IntProperty(row, flipbook.FindProperty("flipbookFrameResolution"), "Cell Size", 55f);

            row.y += 22f;
            DrawVector2IntProperty(row, flipbook.FindProperty("flipbookMaxAtlasResolution"),
                manualLayout ? "Atlas Size" : "Max Atlas", 62f);

            row.y += 22f;
            DrawProperty(Cell(row, 0, 2), flipbook.FindProperty("flipbookPadding"), "Padding", 48f);
            DrawProperty(Cell(row, 1, 2), flipbook.FindProperty("flipbookAutoBounds"), "Auto Bounds", 74f);

            row.y += 22f;
            DrawProperty(row, flipbook.FindProperty("flipbookBackground"), "Background", 68f);

            if (!flipbook.FindProperty("flipbookAutoBounds").boolValue)
            {
                row.y += 22f;
                DrawVector3Property(Cell(row, 0, 2), flipbook.FindProperty("flipbookBoundsCenter"), "Center", 42f);
                DrawVector3Property(Cell(row, 1, 2), flipbook.FindProperty("flipbookBoundsSize"), "Size", 30f);
            }
            bool flipbookChanged = EditorGUI.EndChangeCheck();
            if (flipbook.ApplyModifiedProperties()) flipbookChanged = true;
            if (flipbookChanged) NormalizeAndSave();

            bool derivedRegionReady = BrazeDerivedRegionBuilder.IsReady(session);
            bool flipbookLayoutReady = true;
            bool flipbookSourceReady = !MissingAuthoredRenderDetail();
            string flipbookStatus = null;
            MessageType flipbookStatusType = MessageType.None;
            if (!BrazeFlipbookExporter.Active && !string.IsNullOrEmpty(BrazeFlipbookExporter.Error))
            {
                flipbookStatus = BrazeFlipbookExporter.Error;
                flipbookStatusType = MessageType.Error;
            }
            else if (!flipbookSourceReady)
            {
                flipbookStatus = "This cache carries neither Render Detail data nor velocity, so authored detail " +
                                 "cannot render. Re-bake the master (lean masters rebuild detail from velocity).";
                flipbookStatusType = MessageType.Warning;
            }
            else if (!derivedRegionReady && session.cache != null && session.cache.IsValid)
            {
                flipbookStatus = "The derived Region will be prepared automatically on export.";
                flipbookStatusType = MessageType.Info;
            }
            else if (session.cache == null || !session.cache.IsValid)
            {
                flipbookStatus = "Bake the master cache first.";
                flipbookStatusType = MessageType.Info;
            }
            if (session.cache != null && session.cache.IsValid)
            {
                int frames = session.FlipbookUsesDerivedRegion && derivedRegionReady
                    ? session.derivedRegionCache.frameCount : session.RegionFrameCount;
                try
                {
                    int hardwareMaximum = Mathf.Max(1, SystemInfo.maxTextureSize);
                    _ = BrazeFlipbookExporter.EstimateWorkingBytes(frames,
                        session.flipbookFrameResolution, session.flipbookMaxAtlasResolution, session.flipbookLayout,
                        session.flipbookColumns, session.flipbookRows,
                        hardwareMaximum,
                        1,
                        session.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr,
                        out Vector2Int resolvedCellSize, out int resolvedColumns, out int resolvedRows,
                        out Vector2Int resolvedAtlasResolution);
                    if (flipbookStatus == null)
                        flipbookStatus = $"{frames} frames  ·  {resolvedColumns}×{resolvedRows}" +
                                         $"  ·  cell {resolvedCellSize.x}×{resolvedCellSize.y}" +
                                         $"  ·  atlas {resolvedAtlasResolution.x}×{resolvedAtlasResolution.y}";
                }
                catch (InvalidOperationException e)
                {
                    flipbookStatus = e.Message;
                    flipbookStatusType = MessageType.Error;
                    flipbookLayoutReady = false;
                }
            }

            Rect footer = new(rect.x + 8f, rect.yMax - 57f, rect.width - 16f, 25f);
            Rect statusRect = Cell(footer, 0, 3, 6f, 2);
            Rect action = Cell(footer, 2, 3, 6f);
            if (BrazeFlipbookExporter.Active)
            {
                EditorGUI.ProgressBar(statusRect, BrazeFlipbookExporter.Progress,
                    BrazeFlipbookExporter.Status ?? "Exporting…");
            }
            else if (!string.IsNullOrEmpty(flipbookStatus))
                DrawCardStatus(statusRect, flipbookStatus, flipbookStatusType);

            if (BrazeFlipbookExporter.Active)
            {
                if (GUI.Button(action, "Cancel")) BrazeFlipbookExporter.Cancel();
            }
            else
            {
                using (new EditorGUI.DisabledScope(!flipbookLayoutReady || !flipbookSourceReady ||
                                                    session.cache == null || !session.cache.IsValid ||
                                                    EditorApplication.isPlayingOrWillChangePlaymode ||
                                                    BrazeVolumeExporter.Active))
                    if (GUI.Button(action, "Export")) StartFlipbookExport();
            }

            Rect assetRow = new(rect.x + 8f, rect.yMax - 27f, rect.width - 16f, 19f);
            Rect asset = Cell(assetRow, 0, 3, 6f, 2);
            Rect player = Cell(assetRow, 2, 3, 6f);
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.ObjectField(asset, "Asset", session.flipbookAsset, typeof(BrazeFlipbookAsset), false);
            using (new EditorGUI.DisabledScope(session.flipbookAsset == null || !session.flipbookAsset.IsValid))
                if (GUI.Button(player, "Create / Update Player")) CreateFlipbookPlayer();
        }

        void DrawVolumeCard(Rect rect)
        {
            DrawCard(rect, "OPENVDB");
            SerializedObject volume = new(session);
            volume.Update();
            Rect row = new(rect.x + 8f, rect.y + 29f, rect.width - 16f, 19f);
            EditorGUI.BeginChangeCheck();
            DrawProperty(Cell(row, 0, 3, 6f, 2), volume.FindProperty("volumeOutputFolder"), "Folder", 44f);
            DrawProperty(Cell(row, 2, 3), volume.FindProperty("volumeOutputName"), "Name", 38f);
            row.y += 24f;
            DrawProperty(row, volume.FindProperty("volumeBackgroundThreshold"), "Sparse Cutoff", 78f);
            row.y += 22f;
            DrawProperty(row, volume.FindProperty("volumeGrids"), "Grids", 36f);
            bool volumeChanged = EditorGUI.EndChangeCheck();
            if (volume.ApplyModifiedProperties()) volumeChanged = true;
            if (volumeChanged) NormalizeAndSave();

            bool derivedRegionReady = BrazeDerivedRegionBuilder.IsReady(session);
            bool vdbReady = BrazeVdbBridge.TryGetCapability(out string capability);
            BrazeFluidCacheAsset selectedVolumeCache = session.VolumeUsesDerivedRegion &&
                                                       session.derivedRegionCache != null &&
                                                       session.derivedRegionCache.IsValid
                ? session.derivedRegionCache : session.cache;
            // Masters are always lossless now; only a legacy Realtime-quantized bake can disqualify the source.
            bool bakedSourceIsAuthoritative = selectedVolumeCache == null || !selectedVolumeCache.scalarQuantized;
            bool authoritativeVdbSource = bakedSourceIsAuthoritative;
            Rect capabilityRect = new(rect.x + 8f, row.yMax + 6f, rect.width - 16f, 40f);
            DrawCardStatus(capabilityRect, capability, vdbReady ? MessageType.Info : MessageType.Warning);

            string volumeStatus = null;
            MessageType volumeStatusType = MessageType.None;
            if (!authoritativeVdbSource)
            {
                volumeStatus = "The baked cache contains quantized Realtime values. Re-bake the master before OpenVDB export.";
                volumeStatusType = MessageType.Error;
            }
            else if (!BrazeVolumeExporter.Active && !string.IsNullOrEmpty(BrazeVolumeExporter.Error))
            {
                volumeStatus = BrazeVolumeExporter.Error;
                volumeStatusType = MessageType.Error;
            }
            else if (!derivedRegionReady && session.cache != null && session.cache.IsValid)
            {
                volumeStatus = "The derived Region will be prepared automatically on export.";
                volumeStatusType = MessageType.Info;
            }
            else if (session.cache == null || !session.cache.IsValid)
            {
                volumeStatus = "Bake the master cache first.";
                volumeStatusType = MessageType.Info;
            }
            if (session.cache != null && session.cache.IsValid)
            {
                int gridCount = CountVolumeGrids(session.volumeGrids);
                int frames = session.VolumeUsesDerivedRegion && derivedRegionReady
                    ? session.derivedRegionCache.frameCount : session.RegionFrameCount;
                long estimate = (long)(session.cache.binaryBytes * Mathf.Max(1, gridCount) / 3f *
                                       frames / Mathf.Max(1f, session.cache.frameCount));
                if (volumeStatus == null)
                {
                    Vector3 voxel = new(
                        session.cache.worldSizeAtBake.x / Mathf.Max(1, session.cache.baseRes.x),
                        session.cache.worldSizeAtBake.y / Mathf.Max(1, session.cache.baseRes.y),
                        session.cache.worldSizeAtBake.z / Mathf.Max(1, session.cache.baseRes.z));
                    volumeStatus = $"{frames} frames  ·  {session.cache.baseRes.x}×{session.cache.baseRes.y}×{session.cache.baseRes.z}" +
                                   $"  ·  voxel {voxel.x:0.###}×{voxel.y:0.###}×{voxel.z:0.###} m" +
                                   $"  ·  ~{BrazeContainer.FormatBytes(estimate)}";
                }
            }

            Rect footer = new(rect.x + 8f, rect.yMax - 57f, rect.width - 16f, 25f);
            Rect statusRect = Cell(footer, 0, 3, 6f, 2);
            Rect action = Cell(footer, 2, 3, 6f);
            if (BrazeVolumeExporter.Active)
            {
                EditorGUI.ProgressBar(statusRect, BrazeVolumeExporter.Progress,
                    BrazeVolumeExporter.Status ?? "Exporting…");
            }
            else if (!string.IsNullOrEmpty(volumeStatus))
                DrawCardStatus(statusRect, volumeStatus, volumeStatusType);

            if (BrazeVolumeExporter.Active)
            {
                if (GUI.Button(action, "Cancel")) BrazeVolumeExporter.Cancel();
            }
            else
            {
                using (new EditorGUI.DisabledScope(!vdbReady || !authoritativeVdbSource ||
                                                    session.cache == null || !session.cache.IsValid ||
                                                    EditorApplication.isPlayingOrWillChangePlaymode ||
                                                    BrazeFlipbookExporter.Active))
                    if (GUI.Button(action, "Export")) StartVolumeExport();
            }
            GUI.Label(new Rect(rect.x + 10f, rect.yMax - 27f, rect.width - 20f, 19f),
                "One sparse volume per Region frame · metres · local space", EditorStyles.miniLabel);
        }

        void DrawTimeline(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.065f, 0.068f, 0.075f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), BrazeEditorGUI.Accent);
            Rect regionControls = new(rect.x + 8f, rect.y + 7f, rect.width - 16f, 25f);
            DrawRegionControls(regionControls);
            Rect ruler = new(rect.x + 8f, regionControls.yMax + 3f, rect.width - 16f, 28f);
            Rect lanes = new(ruler.x, ruler.yMax, ruler.width, rect.yMax - ruler.yMax - 8f);
            DrawRuler(ruler);
            DrawRanges(lanes);
            HandleTimelineInput(new Rect(ruler.x, ruler.y, ruler.width, lanes.yMax - ruler.y));
        }

        void DrawTransportButtons(Rect rect)
        {
            float x = rect.x;
            if (GUI.Button(new Rect(x, rect.y + 1f, 28f, 22f), "|◀")) SetPlayhead(session.regionIn); x += 30f;
            if (GUI.Button(new Rect(x, rect.y + 1f, 28f, 22f), "◀")) Step(-1); x += 30f;
            if (GUI.Button(new Rect(x, rect.y + 1f, 42f, 22f), playing ? "❚❚" : "▶"))
            {
                if (!playing && session.regionPlayback == BrazeExportSession.RegionPlayback.Once &&
                    playhead >= session.regionOut - session.FrameDuration * 1.5f)
                    SetPlayhead(session.regionIn);
                playing = !playing;
                playbackDirection = 1;
                lastTick = EditorApplication.timeSinceStartup;
            }
            x += 44f;
            if (GUI.Button(new Rect(x, rect.y + 1f, 28f, 22f), "▶")) Step(1); x += 30f;
            if (GUI.Button(new Rect(x, rect.y + 1f, 28f, 22f), "▶|"))
                SetPlayhead(session.regionOut - session.FrameDuration);
        }

        void DrawRegionControls(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.09f, 0.094f, 0.104f));
            float x = rect.x + 6f;
            GUI.Label(new Rect(x, rect.y + 3f, 52f, 18f), "TIMELINE", EditorStyles.miniBoldLabel);
            x += 54f;

            // Authored in FRAMES (the unit people actually count bakes in — seconds here read as frame counts
            // and produced accidental multi-hour bakes). The session keeps seconds internally; fps converts.
            int fps = Mathf.Max(1, session.BakeFps);
            EditorGUI.BeginChangeCheck();
            float duration = DrawCompactFrameField(ref x, rect, "Length", session.duration, 38f, fps);
            float regionIn = DrawCompactFrameField(ref x, rect, "In", session.regionIn, 15f, fps);
            float regionOut = DrawCompactFrameField(ref x, rect, "Out", session.regionOut, 23f, fps);
            BrazeExportSession.RegionPlayback playback = (BrazeExportSession.RegionPlayback)EditorGUI.EnumPopup(
                new Rect(x, rect.y + 2f, 112f, 20f), session.regionPlayback);
            x += 118f;
            float crossfade = session.crossfadeDuration;
            if (playback == BrazeExportSession.RegionPlayback.CrossfadeLoop)
                crossfade = DrawCompactFrameField(ref x, rect, "Blend", crossfade, 34f, fps);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(session, "Edit Export Region");
                bool invalidatesDerived = session.duration != duration || session.regionIn != regionIn ||
                    session.regionOut != regionOut || session.regionPlayback != playback ||
                    session.crossfadeDuration != crossfade;
                session.duration = duration;
                session.regionIn = regionIn;
                session.regionOut = regionOut;
                session.regionPlayback = playback;
                session.crossfadeDuration = crossfade;
                session.Normalize();
                if (invalidatesDerived) InvalidateDerivedRegion();
                ClampPlayheadToRegion();
                session.Persist();
            }

            const float transportWidth = 164f;
            x = Mathf.Max(x + 8f, rect.center.x - transportWidth * 0.5f);
            DrawTransportButtons(new Rect(x, rect.y, transportWidth, rect.height));
            x += transportWidth + 8f;

            if (session.RegionNeedsDerivedCache)
            {
                using (new EditorGUI.DisabledScope(session.cache == null || !session.cache.IsValid))
                    if (GUI.Button(new Rect(x, rect.y + 2f, 92f, 20f), "Build Region")) BuildDerivedRegion();
                x += 96f;
            }

            float infoX = Mathf.Max(x, rect.xMax - 225f);
            if (rect.xMax - infoX >= 120f)
                GUI.Label(new Rect(infoX, rect.y + 3f, rect.xMax - infoX - 6f, 18f),
                    $"{playhead:0.000}s   ·   F {PlayheadFrame}   ·   {session.BakeFps} fps",
                    new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });
        }

        // Frame-unit field over a seconds-backed value. Round-trips exactly: unchanged frame counts return the
        // original seconds untouched, so repaints can't drift the session through repeated float conversion.
        static float DrawCompactFrameField(ref float x, Rect row, string label, float seconds, float labelWidth,
                                           int fps)
        {
            GUI.Label(new Rect(x, row.y + 3f, labelWidth, 18f), label, EditorStyles.miniLabel);
            x += labelWidth;
            int frames = Mathf.RoundToInt(seconds * fps);
            int edited = EditorGUI.DelayedIntField(new Rect(x, row.y + 2f, 44f, 20f), frames);
            x += 50f;
            return edited == frames ? seconds : Mathf.Max(0, edited) / (float)fps;
        }

        void DrawRuler(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.085f, 0.088f, 0.098f));
            int divisions = Mathf.Clamp(Mathf.RoundToInt(rect.width / 120f), 4, 16);
            for (int i = 0; i <= divisions; i++)
            {
                float t = session.duration * i / divisions;
                float x = TimeToX(t, rect);
                EditorGUI.DrawRect(new Rect(x, rect.yMax - 7f, 1f, 7f), Grid);
                GUI.Label(new Rect(x + 3f, rect.y + 4f, 65f, 18f),
                    $"F {Mathf.RoundToInt(t * Mathf.Max(1, session.BakeFps))}", EditorStyles.miniLabel);
            }
        }

        void DrawRanges(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.09f, 0.093f, 0.103f));
            int divisions = Mathf.Clamp(Mathf.RoundToInt(rect.width / 120f), 4, 16);
            for (int i = 0; i <= divisions; i++)
            {
                float x = rect.x + rect.width * i / divisions;
                EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height), Grid);
            }

            Rect simulation = new(rect.x, rect.y + 8f, rect.width, 20f);
            EditorGUI.DrawRect(simulation, new Color(1f, 1f, 1f, 0.035f));
            GUI.Label(new Rect(simulation.x + 5f, simulation.y + 1f, simulation.width - 10f, 18f),
                session.cache != null && session.cache.IsValid ? "BAKED SIMULATION" : "SIMULATION", EditorStyles.miniLabel);
            Rect region = RegionClipRect(rect);
            DrawRegionClip(region);
            DrawRangeHandle(rect, region, session.regionIn, new Color(0.3f, 0.7f, 1f), true);
            DrawRangeHandle(rect, region, session.regionOut, new Color(0.3f, 0.7f, 1f), false);
            Rect bodyCursor = new(region.x + 9f, region.y, Mathf.Max(0f, region.width - 18f), region.height);
            if (bodyCursor.width > 0f) EditorGUIUtility.AddCursorRect(bodyCursor, MouseCursor.MoveArrow);
            float px = TimeToX(playhead, rect);
            EditorGUI.DrawRect(new Rect(px - 1f, rect.y - 28f, 2f, rect.height + 28f), new Color(1f, 0.2f, 0.16f));
        }

        static string RegionPlaybackLabel(BrazeExportSession.RegionPlayback value) => value switch
        {
            BrazeExportSession.RegionPlayback.CrossfadeLoop => "CROSSFADE LOOP",
            BrazeExportSession.RegionPlayback.PingPong => "PING-PONG",
            BrazeExportSession.RegionPlayback.Loop => "LOOP",
            _ => "ONCE",
        };

        void HandleTimelineInput(Rect rect)
        {
            Event e = Event.current;
            if (e.button != 0) return;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                dragTarget = HitHandle(e.mousePosition, rect);
                if (dragTarget == DragTarget.None) dragTarget = DragTarget.Playhead;
                dragAnchorTime = TimeAtX(e.mousePosition.x, rect);
                dragRegionIn = session.regionIn;
                dragRegionOut = session.regionOut;
                ApplyDrag(e.mousePosition.x, rect);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && dragTarget != DragTarget.None)
            {
                ApplyDrag(e.mousePosition.x, rect);
                e.Use();
            }
            else if (e.rawType == EventType.MouseUp && dragTarget != DragTarget.None)
            {
                dragTarget = DragTarget.None;
                e.Use();
            }
        }

        DragTarget HitHandle(Vector2 position, Rect rect)
        {
            float x = position.x;
            Rect lanes = new(rect.x, rect.y + 28f, rect.width, Mathf.Max(0f, rect.height - 28f));
            Rect clip = RegionClipRect(lanes);
            Rect interaction = new(clip.x - 8f, clip.y - 3f, clip.width + 16f, clip.height + 6f);
            if (!interaction.Contains(position)) return DragTarget.None;
            if (Mathf.Abs(x - TimeToX(session.regionIn, rect)) <= 8f) return DragTarget.RegionIn;
            if (Mathf.Abs(x - TimeToX(session.regionOut, rect)) <= 8f) return DragTarget.RegionOut;
            if (clip.Contains(position) && x > TimeToX(session.regionIn, rect) &&
                x < TimeToX(session.regionOut, rect)) return DragTarget.RegionBody;
            return DragTarget.None;
        }

        void ApplyDrag(float x, Rect rect)
        {
            float time = TimeAtX(x, rect);
            if (dragTarget == DragTarget.Playhead) { SetPlayhead(time); return; }
            Undo.RecordObject(session, "Edit Export Region");
            float step = session.FrameDuration;
            switch (dragTarget)
            {
                case DragTarget.RegionIn:
                    session.regionIn = Mathf.Clamp(time, 0f, session.regionOut - step);
                    break;
                case DragTarget.RegionOut:
                    session.regionOut = Mathf.Clamp(time, session.regionIn + step, session.duration);
                    break;
                case DragTarget.RegionBody:
                    float length = dragRegionOut - dragRegionIn;
                    float newIn = Mathf.Clamp(dragRegionIn + time - dragAnchorTime, 0f,
                        Mathf.Max(0f, session.duration - length));
                    float delta = newIn - session.regionIn;
                    session.regionIn = newIn;
                    session.regionOut = newIn + length;
                    playhead += delta;
                    break;
            }
            InvalidateDerivedRegion();
            NormalizeAndSave();
            ClampPlayheadToRegion();
            Repaint();
        }

        float TimeAtX(float x, Rect rect)
            => session.SnapTime(Mathf.Clamp01((x - rect.x) / rect.width) * session.duration);

        void StartBake()
        {
            session.Normalize();
            BrazeContainer container = session.SourceContainer;
            if (container == null)
            {
                EditorUtility.DisplayDialog("Braze Export Studio", "Bind a source Container first.", "OK");
                return;
            }
            if (!SourceIsLoadedScene)
            {
                EditorUtility.DisplayDialog("Braze Export Studio",
                    "Bind a Container instance from a loaded scene, not a prefab or project asset.", "OK");
                return;
            }
            if (string.IsNullOrEmpty(container.gameObject.scene.path) || container.gameObject.scene.isDirty)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                // Saving an untitled scene gives it a real GUID. Refresh the Container ID before Play Mode reloads.
                session.BindContainer(container);
            }

            try
            {
                BrazeCacheBakerEditor.ComputeBakePaths(session.cacheOutputFolder, session.outputName, false,
                    out string binPath, out string assetPath, out string binFile);
                session.Persist();
                // Masters are LEAN: scalars + velocity, never the native Render Detail texture. Velocity is always
                // captured — playback, flipbook rendering, and scene preview rebuild Render Detail from it, and the
                // bake stays a valid `vel` source if the user toggles VDB grids later.
                BrazeCacheBakeDriver.StartBake(container, session, assetPath, binPath, binFile,
                    session.CaptureFrameCount, 0, session.BakeFps, BrazeCacheBaker.DefaultBakeCull,
                    BrazeCacheBaker.Storage.Compact, storeVelocity: true, captureRenderDetail: false);
                playing = false;
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Braze Export Studio", $"Could not start the bake:\n{e.Message}", "OK");
            }
        }

        void ClearBake()
        {
            if (session == null || session.cache == null) return;
            BrazeFluidCacheAsset master = session.cache;
            BrazeFluidCacheAsset derived = session.derivedRegionCache;
            var caches = new List<BrazeFluidCacheAsset>();
            if (derived != null && derived != master) caches.Add(derived);
            caches.Add(master);
            var binaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deletionSummary = new System.Text.StringBuilder();
            for (int i = 0; i < caches.Count; i++)
            {
                if (!TryValidateCacheDeletion(caches[i], out string binaryPath, out _, out string validationError))
                {
                    EditorUtility.DisplayDialog("Clear Bake",
                        $"The cache was not changed because its generated-file path failed safety validation:\n\n{validationError}",
                        "OK");
                    return;
                }
                if (!string.IsNullOrEmpty(binaryPath) && !binaryPaths.Add(binaryPath))
                {
                    EditorUtility.DisplayDialog("Clear Bake",
                        "The master and derived cache unexpectedly point to the same binary. Nothing was deleted.", "OK");
                    return;
                }
                deletionSummary.Append("\n• ").Append(string.IsNullOrEmpty(binaryPath)
                    ? "(no cache binary recorded)" : binaryPath);
            }
            if (!EditorUtility.DisplayDialog("Clear Bake",
                    "Delete the baked master cache and its derived Region cache? This cannot be undone.\n" +
                    deletionSummary,
                    "Clear Bake", "Cancel"))
                return;

            playing = false;
            cachePreview?.Dispose();
            for (int i = 0; i < caches.Count; i++)
            {
                if (DeleteCacheFiles(caches[i], out string deleteError)) continue;
                EditorUtility.DisplayDialog("Clear Bake",
                    $"Cache deletion stopped safely:\n\n{deleteError}", "OK");
                return;
            }

            session.cache = null;
            session.derivedRegionCache = null;
            session.derivedRegionFingerprint = string.Empty;
            session.bakedSourceFingerprint = string.Empty;
            session.bakedCacheFingerprint = string.Empty;
            session.lastBakeUtc = string.Empty;
            session.Persist();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            SceneView.RepaintAll();
            Repaint();
        }

        internal static bool DeleteCacheFiles(BrazeFluidCacheAsset cacheAsset, out string error)
        {
            if (!TryValidateCacheDeletion(cacheAsset, out string binaryPath, out string assetPath, out error))
                return false;
            try
            {
                if (!string.IsNullOrEmpty(binaryPath) && File.Exists(binaryPath))
                {
                    if (TryProjectAssetPath(binaryPath, out string binaryAssetPath))
                    {
                        if (!AssetDatabase.DeleteAsset(binaryAssetPath))
                        {
                            error = $"Unity could not delete the verified cache binary '{binaryAssetPath}'.";
                            return false;
                        }
                    }
                    else
                    {
                        File.Delete(binaryPath);
                        if (File.Exists(binaryPath))
                        {
                            error = $"The verified cache binary still exists after deletion: {binaryPath}";
                            return false;
                        }
                    }
                }

                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    error = $"Unity could not delete the cache asset '{assetPath}'.";
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = $"Could not delete the verified cache files: {e.Message}";
                return false;
            }
        }

        internal static bool TryValidateCacheDeletion(BrazeFluidCacheAsset cacheAsset,
            out string binaryPath, out string assetPath, out string error)
        {
            binaryPath = null;
            assetPath = cacheAsset != null ? AssetDatabase.GetAssetPath(cacheAsset) : string.Empty;
            error = null;
            if (cacheAsset == null || string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(assetPath), ".asset", StringComparison.OrdinalIgnoreCase))
            {
                error = "The cache must be a persistent .asset inside this project's Assets folder.";
                return false;
            }

            string declared = cacheAsset.binaryFile;
            if (string.IsNullOrWhiteSpace(declared)) return true;
            if (!Path.IsPathRooted(declared))
            {
                string relative = declared.Replace('\\', '/');
                if (relative.IndexOf('/') >= 0 || relative == "." || relative == "..")
                {
                    error = "A relative cache binary path must be a single sibling file name, without traversal.";
                    return false;
                }
            }

            try { binaryPath = Path.GetFullPath(BrazeExportCachePreview.ResolveBinaryPath(cacheAsset)); }
            catch (Exception e)
            {
                error = $"The cache binary path is invalid: {e.Message}";
                return false;
            }
            if (!string.Equals(Path.GetExtension(binaryPath), ".brazecache", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Refusing to delete a generated cache with an unexpected extension: {binaryPath}";
                return false;
            }
            if (!File.Exists(binaryPath)) return true;

            try
            {
                FileAttributes fileAttributes = File.GetAttributes(binaryPath);
                if ((fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = $"Refusing to delete a cache through a directory, symbolic link, or reparse point: {binaryPath}";
                    return false;
                }
                for (DirectoryInfo directory = new FileInfo(binaryPath).Directory;
                     directory != null; directory = directory.Parent)
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                    error = $"Refusing to delete a cache through a symbolic-link/reparse directory: {directory.FullName}";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = $"Could not verify the cache binary path: {e.Message}";
                return false;
            }
            return true;
        }

        static bool TryProjectAssetPath(string fullPath, out string assetPath)
        {
            assetPath = null;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = fullPath.Substring(rootPrefix.Length).Replace('\\', '/');
            if (!relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return false;
            assetPath = relative;
            return true;
        }

        void StartFlipbookExport()
        {
            try
            {
                if (CacheWasModified()) throw new InvalidOperationException("The master cache changed after its recorded bake. Re-bake it before delivery.");
                session.Normalize();
                if (!PrepareDerivedRegion()) return;
                session.Persist();
                cachePreview?.Dispose();
                BrazeFlipbookExporter.Start(session);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Braze Export Studio", $"Could not start flipbook export:\n{e.Message}", "OK");
            }
        }

        void StartVolumeExport()
        {
            try
            {
                if (CacheWasModified()) throw new InvalidOperationException("The master cache changed after its recorded bake. Re-bake it before delivery.");
                session.Normalize();
                if (!PrepareDerivedRegion()) return;
                session.Persist();
                cachePreview?.Dispose();
                BrazeVolumeExporter.Start(session);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Braze Export Studio", $"Could not start volume export:\n{e.Message}", "OK");
            }
        }

        static void CompactSectionTitle(string text)
        {
            Rect row = EditorGUILayout.GetControlRect(false, 18f);
            DrawSubsection(row, text);
        }

        static void DrawCard(Rect rect, string title)
        {
            EditorGUI.DrawRect(rect, new Color(0.082f, 0.086f, 0.096f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), new Color(1f, 0.45f, 0.14f, 0.55f));
            GUIStyle style = new(EditorStyles.boldLabel) { normal = { textColor = BrazeEditorGUI.Accent } };
            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 18f), title, style);
            EditorGUI.DrawRect(new Rect(rect.x + 8f, rect.y + 23f, rect.width - 16f, 1f),
                new Color(1f, 0.45f, 0.14f, 0.25f));
        }

        static void DrawSubsection(Rect rect, string title)
        {
            GUIStyle style = new(EditorStyles.miniBoldLabel) { normal = { textColor = BrazeEditorGUI.Accent } };
            Vector2 size = style.CalcSize(new GUIContent(title));
            float labelWidth = Mathf.Min(rect.width, size.x + 6f);
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), title, style);
            float lineX = rect.x + labelWidth + 3f;
            if (lineX < rect.xMax)
                EditorGUI.DrawRect(new Rect(lineX, rect.center.y, rect.xMax - lineX, 1f),
                    new Color(1f, 0.45f, 0.14f, 0.25f));
        }

        static void DrawProperty(Rect rect, SerializedProperty property, string label, float labelWidth)
        {
            float previous = EditorGUIUtility.labelWidth;
            try
            {
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUI.PropertyField(rect, property, new GUIContent(label));
            }
            finally { EditorGUIUtility.labelWidth = previous; }
        }

        static void DrawVector2IntProperty(Rect rect, SerializedProperty property, string label, float labelWidth)
        {
            EditorGUI.BeginProperty(rect, new GUIContent(label), property);
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, EditorStyles.label);
            Rect values = new(rect.x + labelWidth, rect.y, Mathf.Max(0f, rect.width - labelWidth), rect.height);
            const float gap = 3f;
            const float axisWidth = 13f;
            float width = Mathf.Max(1f, (values.width - axisWidth * 2f - gap) * 0.5f);
            Vector2Int value = property.vector2IntValue;
            GUI.Label(new Rect(values.x, values.y, axisWidth, values.height), "W", EditorStyles.miniLabel);
            value.x = EditorGUI.IntField(
                new Rect(values.x + axisWidth, values.y, width, values.height), value.x);
            float secondX = values.x + axisWidth + width + gap;
            GUI.Label(new Rect(secondX, values.y, axisWidth, values.height), "H", EditorStyles.miniLabel);
            value.y = EditorGUI.IntField(
                new Rect(secondX + axisWidth, values.y, width, values.height), value.y);
            property.vector2IntValue = value;
            EditorGUI.EndProperty();
        }

        static void DrawVector3Property(Rect rect, SerializedProperty property, string label, float labelWidth)
        {
            EditorGUI.BeginProperty(rect, new GUIContent(label), property);
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, EditorStyles.label);
            Rect values = new(rect.x + labelWidth, rect.y, Mathf.Max(0f, rect.width - labelWidth), rect.height);
            const float gap = 3f;
            float width = Mathf.Max(1f, (values.width - gap * 2f) / 3f);
            Vector3 value = property.vector3Value;
            value.x = EditorGUI.FloatField(new Rect(values.x, values.y, width, values.height), value.x);
            value.y = EditorGUI.FloatField(new Rect(values.x + width + gap, values.y, width, values.height), value.y);
            value.z = EditorGUI.FloatField(new Rect(values.x + (width + gap) * 2f, values.y, width, values.height), value.z);
            property.vector3Value = value;
            EditorGUI.EndProperty();
        }

        static void DrawCardStatus(Rect rect, string message, MessageType type)
        {
            if (type == MessageType.None)
            {
                GUIStyle style = new(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip
                };
                GUI.Label(new Rect(rect.x + 2f, rect.y, rect.width - 4f, rect.height),
                    new GUIContent(message, message), style);
                return;
            }
            EditorGUI.HelpBox(rect, message, type);
        }

        static Rect Cell(Rect row, int index, int count, float gap = 6f, int span = 1)
        {
            count = Mathf.Max(1, count);
            index = Mathf.Clamp(index, 0, count - 1);
            span = Mathf.Clamp(span, 1, count - index);
            float width = (row.width - gap * (count - 1)) / count;
            return new Rect(row.x + index * (width + gap), row.y,
                width * span + gap * (span - 1), row.height);
        }

        void CreateFlipbookPlayer()
        {
            if (session.flipbookAsset == null || !session.flipbookAsset.IsValid) return;
            BrazeFlipbookPlayer player = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<BrazeFlipbookPlayer>() : null;
            if (player == null)
            {
                GameObject go = new(session.outputName + " Flipbook");
                Undo.RegisterCreatedObjectUndo(go, "Create Braze Flipbook Player");
                player = go.AddComponent<BrazeFlipbookPlayer>();
            }
            Undo.RecordObject(player, "Assign Braze Flipbook");
            Undo.RecordObject(player.transform, "Fit Braze Flipbook Player");
            int viewIndex = session.flipbookAsset.views.FindIndex(view =>
                view != null && view.direction == session.flipbookView);
            viewIndex = Mathf.Max(0, viewIndex);
            AssignFlipbookToPlayer(player, session.flipbookAsset, viewIndex);
            FitFlipbookPlayer(player.transform, session.flipbookAsset, session.SourceContainer, viewIndex);
            EditorUtility.SetDirty(player);
            Selection.activeGameObject = player.gameObject;
        }

        internal static void FitFlipbookPlayer(Transform target, BrazeFlipbookAsset asset, BrazeContainer source,
            int viewIndex = 0)
        {
            if (target == null || asset == null || !asset.IsValid) return;
            Quaternion rotation = source != null ? source.transform.rotation : Quaternion.identity;
            Vector3 origin = source != null ? source.transform.position : Vector3.zero;
            Vector3 localCenter = asset.version >= 2 ? asset.boundsCenter : Vector3.zero;
            target.SetPositionAndRotation(origin + rotation * localCenter, rotation);
            BrazeFlipbookAsset.View view = asset.GetView(viewIndex);
            Vector2 frameSize = view != null ? view.frameWorldSize : Vector2.zero;
            if (frameSize.x <= 0f || frameSize.y <= 0f)
                frameSize = LegacyPlayerFrameSize(asset.worldSize,
                    view != null ? view.direction : BrazeFlipbookAsset.ViewDirection.Front);
            target.localScale = new Vector3(
                Mathf.Max(0.01f, frameSize.x), Mathf.Max(0.01f, frameSize.y), 1f);
        }

        static Vector2 LegacyPlayerFrameSize(Vector3 size, BrazeFlipbookAsset.ViewDirection direction)
            => direction switch
            {
                BrazeFlipbookAsset.ViewDirection.Left or BrazeFlipbookAsset.ViewDirection.Right
                    => new Vector2(size.z, size.y),
                BrazeFlipbookAsset.ViewDirection.Top or BrazeFlipbookAsset.ViewDirection.Bottom
                    => new Vector2(size.x, size.z),
                _ => new Vector2(size.x, size.y),
            };

        internal static void AssignFlipbookToPlayer(BrazeFlipbookPlayer player, BrazeFlipbookAsset asset,
            int viewIndex = 0)
        {
            if (player == null || asset == null || !asset.IsValid) return;
            BrazeFlipbookAsset.View view = asset.GetView(viewIndex);
            player.flipbookAsset = null;
            player.viewIndex = 0;
            player.atlas ??= new BrazeFlipbookPlayer.AtlasSettings();
            player.atlas.texture = view != null ? view.texture : null;
            player.atlas.columns = Mathf.Max(1, asset.columns);
            player.atlas.rows = Mathf.Max(1, asset.rows);
            player.atlas.frameCount = Mathf.Clamp(asset.frameCount, 1,
                player.atlas.columns * player.atlas.rows);
            player.atlas.premultipliedAlpha = asset.premultipliedAlpha;
            player.playback ??= new BrazeFlipbookPlayer.PlaybackSettings();
            player.playback.framesPerSecond = Mathf.Max(0.01f, asset.fps);
            player.playback.mode = asset.looping
                ? BrazeFlipbookPlayer.Playback.Loop : BrazeFlipbookPlayer.Playback.Once;
        }

        bool PrepareDerivedRegion()
        {
            if (!session.RegionNeedsDerivedCache || BrazeDerivedRegionBuilder.IsReady(session)) return true;
            try
            {
                BrazeFluidCacheAsset asset = BrazeDerivedRegionBuilder.Ensure(session, (label, progress) =>
                        !EditorUtility.DisplayCancelableProgressBar("BrazeFX Derived Cache", label, progress));
                EditorGUIUtility.PingObject(asset);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Braze Export Studio", $"Could not derive the region cache:\n{e.Message}", "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void BuildDerivedRegion() => PrepareDerivedRegion();

        bool CacheWasModified()
            => session != null && !string.IsNullOrEmpty(session.bakedCacheFingerprint) &&
               !string.Equals(session.bakedCacheFingerprint,
                   BrazeExportFingerprint.CacheFingerprint(session.cache), StringComparison.Ordinal);

        // A flipbook can render authored Render Detail from EITHER representation: legacy masters carry the native
        // detail texture, lean masters rebuild it from stored velocity. Only a cache with neither is unusable.
        bool MissingAuthoredRenderDetail()
        {
            if (session == null || session.cache == null || session.SourceContainer == null) return false;
            Vector3Int authored = session.SourceContainer.CacheFinalLookWarpRes;
            return authored.x > 0 && authored.y > 0 && authored.z > 0 &&
                   (session.cache.warpRes.x <= 0 || session.cache.warpRes.y <= 0 || session.cache.warpRes.z <= 0) &&
                   !session.cache.hasVelocity;
        }

        bool CacheMatchesLeanMasterFormat()
        {
            if (session == null || session.cache == null || session.SourceContainer == null) return true;
            return IsLeanMasterCache(session.cache);
        }

        /// <summary>The one current Export Studio master: lossless scalar fields plus velocity, with no stored
        /// native Render Detail texture. Keeping this independent of the source look ensures old scalar-only or
        /// quantized caches cannot masquerade as a current master merely because Render Detail happens to be off.</summary>
        internal static bool IsLeanMasterCache(BrazeFluidCacheAsset cache)
            => cache != null && cache.warpRes.x <= 0 && cache.warpRes.y <= 0 && cache.warpRes.z <= 0 &&
               cache.hasVelocity && !cache.scalarQuantized;

        // Mask to the defined flags BEFORE counting: the flags dropdown's "Everything" serializes -1, and an
        // arithmetic right shift on a negative int never reaches zero — an unmasked count is an infinite loop
        // that hangs the Editor on the window's first repaint.
        static int CountVolumeGrids(BrazeExportSession.VolumeGrids value)
            => CountBits((int)(value & (BrazeExportSession.VolumeGrids.Density |
                                        BrazeExportSession.VolumeGrids.Temperature |
                                        BrazeExportSession.VolumeGrids.Emission |
                                        BrazeExportSession.VolumeGrids.Velocity)));

        static int CountBits(int bits)
        {
            int count = 0;
            while (bits != 0) { count += bits & 1; bits >>= 1; }
            return count;
        }

        void BindContainer(BrazeContainer container)
        {
            if (session == null) return;
            Undo.RecordObject(session, "Bind Export Container");
            session.BindContainer(container);
            NormalizeAndSave();
        }

        void NormalizeAndSave()
        {
            if (session == null) return;
            session.Normalize();
            ClampPlayheadToRegion();
            session.Persist();
        }

        void SetPlayhead(float time)
        {
            playhead = Mathf.Clamp(session.SnapTime(time), session.regionIn,
                Mathf.Max(session.regionIn, session.regionOut - session.FrameDuration));
            Repaint();
        }

        void Step(int direction) => SetPlayhead(playhead + direction * session.FrameDuration);

        int PlayheadFrame => session == null ? 0 : Mathf.Clamp(
            Mathf.RoundToInt(playhead * session.BakeFps), 0,
            Mathf.Max(0, session.CaptureFrameCount - 1));

        void ClampPlayheadToRegion()
        {
            if (session == null) return;
            playhead = Mathf.Clamp(session.SnapTime(playhead), session.regionIn,
                Mathf.Max(session.regionIn, session.regionOut - session.FrameDuration));
        }

        void InvalidateDerivedRegion()
        {
            if (session == null || (session.derivedRegionCache == null &&
                                    string.IsNullOrEmpty(session.derivedRegionFingerprint))) return;
            session.derivedRegionCache = null;
            session.derivedRegionFingerprint = string.Empty;
            EditorUtility.SetDirty(session);
        }

        bool SourceIsLoadedScene
        {
            get
            {
                BrazeContainer container = session != null ? session.SourceContainer : null;
                return container != null && container.gameObject.scene.IsValid() && container.gameObject.scene.isLoaded;
            }
        }

        float TimeToX(float time, Rect rect)
            => rect.x + rect.width * Mathf.Clamp01(time / Mathf.Max(0.0001f, session != null ? session.duration : 1f));

        Rect RangeRect(Rect rect, float start, float end, float yOffset, float height)
        {
            float x0 = TimeToX(start, rect);
            float x1 = TimeToX(end, rect);
            return new Rect(x0, rect.y + yOffset, Mathf.Max(1f, x1 - x0), height);
        }

        Rect RegionClipRect(Rect lanes)
        {
            float height = Mathf.Min(RegionClipHeight,
                Mathf.Max(28f, lanes.height - RegionClipYOffset - 12f));
            return RangeRect(lanes, session.regionIn, session.regionOut, RegionClipYOffset, height);
        }

        void DrawRegionClip(Rect region)
        {
            if (region.width <= 0f || region.height <= 0f) return;
            Color border = new(0.28f, 0.72f, 1f, 0.95f);
            DrawRoundedRect(region, border, 6f);
            Rect inner = new(region.x + 1f, region.y + 1f,
                Mathf.Max(0f, region.width - 2f), Mathf.Max(0f, region.height - 2f));
            DrawRoundedRect(inner, new Color(0.12f, 0.27f, 0.39f, 0.98f), 5f);

            float capWidth = Mathf.Min(8f, region.width * 0.5f);
            if (capWidth > 0f)
            {
                DrawRoundedRect(new Rect(region.x + 1f, region.y + 1f, capWidth, region.height - 2f),
                    new Color(0.25f, 0.64f, 0.9f, 0.9f), new Vector4(5f, 0f, 0f, 5f));
                DrawRoundedRect(new Rect(region.xMax - capWidth - 1f, region.y + 1f, capWidth, region.height - 2f),
                    new Color(0.25f, 0.64f, 0.9f, 0.9f), new Vector4(0f, 5f, 5f, 0f));
            }

            Rect title = new(region.x + capWidth + 2f, region.y + 1f,
                Mathf.Max(0f, region.width - capWidth * 2f - 4f), 22f);
            EditorGUI.DrawRect(title, new Color(0.18f, 0.39f, 0.55f, 0.52f));
            if (title.width > 48f)
                GUI.Label(new Rect(title.x + 5f, title.y + 2f, title.width - 10f, 18f),
                    $"REGION  ·  {RegionPlaybackLabel(session.regionPlayback)}", EditorStyles.miniBoldLabel);

            Rect footer = new(region.x + capWidth + 2f, region.yMax - RegionClipFooterHeight - 1f,
                Mathf.Max(0f, region.width - capWidth * 2f - 4f), RegionClipFooterHeight);
            EditorGUI.DrawRect(footer, new Color(0.055f, 0.12f, 0.18f, 0.72f));
            if (footer.width > 54f)
            {
                GUI.Label(new Rect(footer.x + 4f, footer.y, 24f, footer.height), "IN", EditorStyles.miniLabel);
                GUI.Label(new Rect(footer.xMax - 30f, footer.y, 26f, footer.height), "OUT",
                    new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });
            }
        }

        static void DrawRoundedRect(Rect rect, Color color, float radius)
            => DrawRoundedRect(rect, color, new Vector4(radius, radius, radius, radius));

        static void DrawRoundedRect(Rect rect, Color color, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint || rect.width <= 0f || rect.height <= 0f) return;
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                color, Vector4.zero, radii);
        }

        void DrawRangeHandle(Rect rect, Rect region, float time, Color color, bool leftEdge)
        {
            float x = TimeToX(time, rect);
            Rect hitRect = new(x - 8f, region.y - 3f, 16f, region.height + 6f);
            bool hovered = hitRect.Contains(Event.current.mousePosition);
            bool active = dragTarget == (leftEdge ? DragTarget.RegionIn : DragTarget.RegionOut);
            Color handleColor = hovered || active ? new Color(0.5f, 0.86f, 1f) : color;

            float gripX = leftEdge ? x + 3f : x - 5f;
            float gripHeight = Mathf.Clamp(region.height * 0.36f, 16f, 22f);
            EditorGUI.DrawRect(new Rect(gripX, region.center.y - gripHeight * 0.5f, 2f, gripHeight),
                handleColor);
            EditorGUIUtility.AddCursorRect(hitRect, MouseCursor.ResizeHorizontal);
        }

        bool TryGetBakedCacheFile(out string path, out long bytes)
        {
            path = session != null ? BrazeExportCachePreview.ResolveBinaryPath(session.cache) : null;
            bytes = 0L;
            if (session?.cache == null || !session.cache.IsValid || string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            bytes = new FileInfo(path).Length;
            return true;
        }

        static string DisplayCachePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string fullPath = Path.GetFullPath(path);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootPrefix.Length).Replace('\\', '/')
                : fullPath;
        }

        static BrazeContainer SelectedContainer()
        {
            GameObject selected = Selection.activeGameObject;
            return selected != null ? selected.GetComponentInParent<BrazeContainer>() : null;
        }

    }
}
