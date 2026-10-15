using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Edit-mode cache host and export camera used by Export Studio. The window drives the host directly for
    /// Scene-view scrubbing; delivery creates the camera lazily only when it renders an output frame.</summary>
    internal sealed class BrazeExportCachePreview : IDisposable
    {
        const int UnknownGpuBudgetMB = 512;
        const int MinimumGpuBudgetMB = 256;
        const int MaximumGpuBudgetMB = 512;

        GameObject playerObject;
        BrazeCachePlayer player;
        GameObject cameraObject;
        Camera previewCamera;
        BrazeFluidCacheAsset boundCache;
        bool streaming;
        string error;
        long residentGpuBytes;
        bool haveResidentEstimate;
        BrazeExportSession currentSession;
        BrazeContainer suppressedSource;
        bool suppressedSourcePreviousState;

        public BrazeCachePlayer.CacheStatus Status => player != null ? player.Status : BrazeCachePlayer.CacheStatus.Unloaded;
        public string Error => !string.IsNullOrEmpty(error) ? error : player != null ? player.StatusMessage : null;
        public int CurrentFrame => player != null ? player.CurrentFrame : -1;
        internal int ScatterCount => player != null ? player.EditorPreviewScatterCount : 0;

        public bool TickScene(BrazeExportSession session, float playhead, bool sequentialPlayback = false,
                              int direction = 1)
        {
            if (Application.isPlaying || BrazeCacheBakeDriver.Active || session == null ||
                session.cache == null || !session.cache.IsValid)
            {
                Dispose();
                return false;
            }

            int frame = Mathf.Clamp(Mathf.RoundToInt(playhead * session.cache.fps),
                0, Mathf.Max(0, session.cache.frameCount - 1));
            int previousFrame = CurrentFrame;
            BrazeCachePlayer.CacheStatus previousStatus = Status;
            bool ready = PrepareFrame(session, frame, sequentialPlayback, direction);
            if (player == null) return false;
            if (player.Status == BrazeCachePlayer.CacheStatus.Error)
            {
                error = player.StatusMessage;
                RestoreSuppressedSource();
                return previousStatus != player.Status;
            }
            bool frameChanged = ready && previousFrame != CurrentFrame;
            if (frameChanged)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
            return frameChanged || previousStatus != player.Status;
        }

        public bool PrepareFrame(BrazeExportSession session, int frame, bool sequentialPlayback = false,
                                 int direction = 1)
        {
            if (Application.isPlaying || session == null || session.cache == null || !session.cache.IsValid) return false;
            EnsureBound(session);
            currentSession = session;
            if (player == null) return false;
            SyncPlacement(session);
            frame = Mathf.Clamp(frame, 0, Mathf.Max(0, session.cache.frameCount - 1));
            // Set the target before Pump. Otherwise a large stream uploads a stale decoded neighbour first and a
            // rapid drag can spend every Editor update moving data for frames the cursor has already left behind.
            player.EditorRequestFrame(frame, sequentialPlayback, direction);
            player.EditorTick();
            if (player.Status == BrazeCachePlayer.CacheStatus.Error && !streaming)
            {
                // A footprint estimate cannot predict every driver/allocation failure. Keep the authoring workflow
                // alive by transparently retrying with the bounded frame ring.
                Bind(session, true);
                currentSession = session;
                SyncPlacement(session);
                player.EditorRequestFrame(frame, sequentialPlayback, direction);
                player.EditorTick();
            }
            if (player.Status != BrazeCachePlayer.CacheStatus.Ready) return false;
            SuppressSource(session.SourceContainer);
            // Older v9 assets do not carry renderer scheduling metadata. The source is available in Export Studio,
            // so make the hidden host inherit the exact live Quality Mode/downsample/step budget before it draws.
            player.EditorPreviewContainer?.MatchCacheRendererState(session.SourceContainer);
            bool ready = player.EditorPreviewFrame(frame, sequentialPlayback, direction);
            BrazeContainer volume = player.EditorPreviewContainer;
            if (ready && volume != null && !BrazeContainer.Active.Contains(volume)) BrazeContainer.Active.Add(volume);
            return ready;
        }

        public void RenderFrame(RenderTexture target, BrazeFlipbookAsset.ViewDirection direction,
            BrazeExportSession.FlipbookProjection projection, float padding, Color background)
        {
            EnsureCamera();
            if (previewCamera == null || target == null || currentSession == null)
                throw new InvalidOperationException("The cache preview is not ready to render.");
            BrazeContainer volume = player != null ? player.EditorPreviewContainer : null;
            if (volume == null || !volume.SliceRenderReady)
                throw new InvalidOperationException("The cache volume is not ready to render.");
            previewCamera.aspect = target.width / (float)Mathf.Max(1, target.height);
            PositionCamera(currentSession, direction, projection, padding);
            RenderTexture previous = previewCamera.targetTexture;
            Color previousBackground = previewCamera.backgroundColor;
            try
            {
                previewCamera.backgroundColor = background;
                previewCamera.targetTexture = target;
                using (BrazeCameraRenderIsolation.Begin(previewCamera, volume))
                    previewCamera.Render();
            }
            finally
            {
                previewCamera.targetTexture = previous;
                previewCamera.backgroundColor = previousBackground;
            }
        }

        void EnsureBound(BrazeExportSession session)
        {
            if (player != null && boundCache == session.cache) return;
            Bind(session, null);
        }

        void Bind(BrazeExportSession session, bool? forceStreaming)
        {
            Dispose();
            boundCache = session.cache;
            error = null;

            haveResidentEstimate = TryEstimateResidentBytes(session.cache, out residentGpuBytes, out string inspectError);
            if (!haveResidentEstimate && !string.IsNullOrEmpty(inspectError)) error = inspectError;
            streaming = forceStreaming ?? (haveResidentEstimate &&
                        residentGpuBytes > AutomaticResidentBudgetBytes(SystemInfo.graphicsMemorySize));

            playerObject = new GameObject("Braze Export Cache Preview") { hideFlags = HideFlags.HideAndDontSave };
            player = playerObject.AddComponent<BrazeCachePlayer>();
            player.autoLoad = false;
            player.playOnLoad = false;
            player.cache = session.cache;
            player.streaming = streaming;
            player.dedupResident = false;
            player.loop = false;
            player.enableRendering = true;
            player.realtimeMode = false;

            player.LoadCache();
        }

        internal static long AutomaticResidentBudgetBytes(int graphicsMemoryMB)
        {
            int budgetMB = graphicsMemoryMB > 0
                ? Mathf.Clamp(Mathf.FloorToInt(graphicsMemoryMB * 0.4f), MinimumGpuBudgetMB, MaximumGpuBudgetMB)
                : UnknownGpuBudgetMB;
            return (long)budgetMB * 1024L * 1024L;
        }

        void SyncPlacement(BrazeExportSession session)
        {
            if (playerObject == null) return;
            BrazeContainer source = session.SourceContainer;
            Vector3 sourceCenter = source != null ? source.transform.position : Vector3.zero;
            Quaternion rotation = source != null ? source.transform.rotation : Quaternion.identity;
            playerObject.transform.SetPositionAndRotation(sourceCenter, rotation);
            playerObject.transform.localScale = Vector3.one;
        }

        void EnsureCamera()
        {
            if (previewCamera != null) return;
            cameraObject = new GameObject("Braze Export Camera") { hideFlags = HideFlags.HideAndDontSave };
            previewCamera = cameraObject.AddComponent<Camera>();
            ConfigureEffectOnlyCamera(previewCamera);
        }

        internal static void ConfigureEffectOnlyCamera(Camera camera)
        {
            if (camera == null) return;
            camera.enabled = false;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.allowDynamicResolution = false;
            camera.useOcclusionCulling = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.038f, 0.045f, 0f);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100000f;
            // Zero means no Unity Renderer, Terrain, particle mesh, skybox, or scene-only helper can enter the
            // capture. The scoped Braze slice pass is procedural and intentionally remains available.
            camera.cullingMask = 0;
        }

        internal void SuppressSource(BrazeContainer source)
        {
            if (suppressedSource == source) return;
            RestoreSuppressedSource();
            suppressedSource = source;
            if (suppressedSource != null)
            {
                suppressedSourcePreviousState = suppressedSource.editorRenderSuppressed;
                suppressedSource.editorRenderSuppressed = true;
            }
        }

        internal void RestoreSuppressedSource()
        {
            if (suppressedSource != null)
                suppressedSource.editorRenderSuppressed = suppressedSourcePreviousState;
            suppressedSource = null;
            suppressedSourcePreviousState = false;
        }

        void PositionCamera(BrazeExportSession session, BrazeFlipbookAsset.ViewDirection direction,
            BrazeExportSession.FlipbookProjection projection, float padding)
        {
            if (previewCamera == null || session == null) return;
            BrazeContainer source = session.SourceContainer;
            Quaternion rotation = source != null ? source.transform.rotation : Quaternion.identity;
            Vector3 sourceCenter = source != null ? source.transform.position : Vector3.zero;
            Vector3 size = !session.flipbookAutoBounds ? session.flipbookBoundsSize
                : session.cache != null && session.cache.worldSizeAtBake.sqrMagnitude > 1e-6f
                    ? session.cache.worldSizeAtBake : Vector3.one;
            Vector3 localCenter = session.flipbookAutoBounds ? Vector3.zero : session.flipbookBoundsCenter;
            BrazeFlipbookCameraFraming.Frame frame = BrazeFlipbookCameraFraming.Resolve(
                sourceCenter, rotation, localCenter, size, direction, projection, previewCamera.aspect, padding);
            frame.Apply(previewCamera);
        }

        static bool TryEstimateResidentBytes(BrazeFluidCacheAsset cache, out long result, out string error)
        {
            result = 0L;
            error = null;
            try
            {
                string path = ResolveBinaryPath(cache);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new FileNotFoundException("Master cache binary was not found.", path);
                using var reader = new BrazeFluidCacheFormat.Reader(path);
                int[] counts = reader.ReadFrameBrickCounts();
                long total = 0;
                for (int i = 0; i < counts.Length; i++) total += counts[i];
                int stride = BrazeFluidCacheFormat.ResidentUintsPerBrick(reader.header);
                result = total * (1L + stride) * sizeof(uint);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        internal static string ResolveBinaryPath(BrazeFluidCacheAsset cache)
        {
            if (cache == null || string.IsNullOrEmpty(cache.binaryFile)) return null;
            if (Path.IsPathRooted(cache.binaryFile)) return cache.binaryFile;
            string assetPath = AssetDatabase.GetAssetPath(cache);
            return string.IsNullOrEmpty(assetPath) ? null
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetPath) ?? string.Empty, cache.binaryFile));
        }

        public void Dispose()
        {
            bool repaintScene = playerObject != null || suppressedSource != null;
            RestoreSuppressedSource();
            if (player != null) player.UnloadCache();
            if (previewCamera != null) previewCamera.targetTexture = null;
            if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            if (playerObject != null) UnityEngine.Object.DestroyImmediate(playerObject);
            cameraObject = null;
            previewCamera = null;
            playerObject = null;
            player = null;
            boundCache = null;
            currentSession = null;
            haveResidentEstimate = false;
            residentGpuBytes = 0L;
            streaming = false;
            if (repaintScene) SceneView.RepaintAll();
        }
    }
}
