using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX.EditorTools
{
    internal enum BrazeStageRecoveryResult { None, Missing, Removed, Rejected }

    /// <summary>Keeps incomplete delivery files and rollback copies outside Unity's import roots.</summary>
    internal static class BrazeExportStaging
    {
        const string MarkerFileName = ".brazefx-stage-owner";
        const string MarkerContents = "BrazeFX Export Studio staging directory v1\n";

        internal static string Create(string outputDirectory, string kind)
        {
            if (!IsSafeKind(kind)) throw new ArgumentException("Invalid BrazeFX staging kind.", nameof(kind));
            string output = Path.GetFullPath(outputDirectory);
            string assets = Path.GetFullPath(Application.dataPath);
            string parent = IsSameOrChild(output, assets)
                ? Path.Combine(Path.GetDirectoryName(assets) ?? output, "Temp", "BrazeFX Export Studio")
                : output;
            Directory.CreateDirectory(parent);
            string stage = Path.Combine(parent, $".braze-{kind}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            try
            {
                File.WriteAllText(Path.Combine(stage, MarkerFileName), MarkerContents);
                return stage;
            }
            catch
            {
                try { Directory.Delete(stage, false); } catch { }
                throw;
            }
        }

        internal static string BackupPath(string stageDirectory, string finalPath)
            => Path.Combine(stageDirectory, $".backup-{Guid.NewGuid():N}-{Path.GetFileName(finalPath)}");

        internal static bool IsSameOrChild(string path, string directory)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            string root = Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryDelete(string stageDirectory, string expectedKind, out string error)
        {
            if (!TryValidate(stageDirectory, expectedKind, out string fullPath, out error)) return false;
            try
            {
                Directory.Delete(fullPath, true);
                if (Directory.Exists(fullPath))
                {
                    error = "The verified staging directory still exists after deletion.";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = $"Could not remove the verified staging directory: {e.Message}";
                return false;
            }
        }

        internal static BrazeStageRecoveryResult Recover(string editorPrefsKey, string expectedKind, out string detail)
        {
            detail = null;
            string stale = EditorPrefs.GetString(editorPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(stale)) return BrazeStageRecoveryResult.None;

            // Never retry a rejected/corrupted global preference on every reload. A valid owned stage is deleted
            // below; anything else is left untouched for explicit manual inspection.
            EditorPrefs.DeleteKey(editorPrefsKey);
            if (!Directory.Exists(stale)) return BrazeStageRecoveryResult.Missing;
            if (TryDelete(stale, expectedKind, out detail)) return BrazeStageRecoveryResult.Removed;
            return BrazeStageRecoveryResult.Rejected;
        }

        internal static bool TryValidate(string stageDirectory, string expectedKind,
            out string fullPath, out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                error = "The staging path is empty.";
                return false;
            }
            if (!IsSafeKind(expectedKind))
            {
                error = "The expected staging kind is invalid.";
                return false;
            }

            try { fullPath = Path.GetFullPath(stageDirectory); }
            catch (Exception e)
            {
                error = $"The staging path is invalid: {e.Message}";
                return false;
            }

            if (IsProtectedDirectory(fullPath))
            {
                error = "Refusing to treat a filesystem, home, project, or Assets root as staging data.";
                return false;
            }
            if (!Directory.Exists(fullPath))
            {
                error = "The staging directory does not exist.";
                return false;
            }

            string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            string prefix = $".braze-{expectedKind}-";
            string token = name != null && name.StartsWith(prefix, StringComparison.Ordinal)
                ? name.Substring(prefix.Length) : string.Empty;
            if (!Guid.TryParseExact(token, "N", out _))
            {
                error = $"The directory name is not an owned BrazeFX '{expectedKind}' staging name.";
                return false;
            }

            string marker = Path.Combine(fullPath, MarkerFileName);
            if (!File.Exists(marker))
            {
                error = "The BrazeFX staging ownership marker is missing.";
                return false;
            }
            try
            {
                FileAttributes markerAttributes = File.GetAttributes(marker);
                if ((markerAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = "The BrazeFX staging ownership marker is not a regular file.";
                    return false;
                }
                if (!string.Equals(File.ReadAllText(marker), MarkerContents, StringComparison.Ordinal))
                {
                    error = "The BrazeFX staging ownership marker is invalid.";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = $"The BrazeFX staging ownership marker could not be read: {e.Message}";
                return false;
            }
            return !ContainsReparsePoint(fullPath, out error);
        }

        static bool IsSafeKind(string kind)
        {
            if (string.IsNullOrEmpty(kind) || kind.Length > 32) return false;
            for (int i = 0; i < kind.Length; i++)
            {
                char c = kind[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-') return false;
            }
            return true;
        }

        static bool IsProtectedDirectory(string path)
        {
            string full = Comparable(path);
            string filesystemRoot = Comparable(Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty);
            string assets = Comparable(Application.dataPath);
            string project = Comparable(Path.GetDirectoryName(Application.dataPath) ?? string.Empty);
            string home = Comparable(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return string.Equals(full, filesystemRoot, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, assets, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, project, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(home) && string.Equals(full, home, StringComparison.OrdinalIgnoreCase));
        }

        static string Comparable(string path)
            => string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

        static bool ContainsReparsePoint(string root, out string error)
        {
            error = null;
            try
            {
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string directory = pending.Pop();
                    FileAttributes directoryAttributes = File.GetAttributes(directory);
                    if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = $"Refusing to delete staging data through a symbolic link or reparse point: {directory}";
                        return true;
                    }
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            error = $"Refusing to delete staging data containing a symbolic link or reparse point: {entry}";
                            return true;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                error = $"Could not verify the staging directory tree: {e.Message}";
                return true;
            }
        }
    }

    [Serializable]
    internal sealed class BrazeFlipbookExportManifest
    {
        public int version = 1;
        public string generator = "BrazeFX Export Studio";
        public string sourceCache;
        public string sourceFingerprint;
        public string createdUtc;
        public string format;
        public string projection;
        public string[] views;
        public string[] files;
        public int frameCount;
        public float fps;
        public int columns;
        public int rows;
        public Vector2Int frameResolution;
        public Vector2Int atlasResolution;
        public Vector3 worldSize;
        public Vector3 boundsCenter;
        public Vector3 pivot;
        public int alphaDilation;
        public bool premultipliedAlpha;
        public bool looping;
    }

    [InitializeOnLoad]
    internal static class BrazeFlipbookExporter
    {
        const string InterruptedStageKey = "BrazeFX.ExportStudio.FlipbookStage";
        enum State { Idle, AnalyzingBounds, Preparing, WaitingFrame, WaitingReadback, Finalizing }

        static State state;
        static BrazeExportSession sourceSession;
        static BrazeExportSession renderSession;
        static BrazeExportCachePreview preview;
        static BrazeFlipbookSequenceBounds boundsAnalyzer;
        static BrazeFluidCacheAsset sourceCache;
        static readonly List<BrazeFlipbookAsset.ViewDirection> directions = new();
        static readonly List<Texture2D> atlases = new();
        static readonly List<string> atlasFiles = new();
        static readonly List<string> atlasFinalFiles = new();
        static readonly HashSet<string> retainedAtlasFinalFiles = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<BrazeFlipbookAsset.ViewDirection> retainedDirections = new();
        static readonly List<(string final, string backup)> publicationBackups = new();
        static readonly List<string> publishedFiles = new();
        static RenderTexture frameTarget;
        static AsyncGPUReadbackRequest readback;
        static Color32[] atlasLdr;
        static ushort[] atlasHdr;
        static int[] sourceFrames;
        static int viewIndex;
        static int outputFrame;
        static Vector2Int frameResolution;
        static int columns, rows, atlasWidth, atlasHeight;
        static Bounds captureBounds;
        static bool cancelRequested;
        static string status;
        static string error;
        static string stageDirectory;
        static string jobId;

        public static bool Active => state != State.Idle;
        public static string Status => status;
        public static string Error => error;
        public static float Progress
        {
            get
            {
                if (!Active || directions.Count == 0 || sourceFrames == null || sourceFrames.Length == 0) return 0f;
                if (state == State.AnalyzingBounds && boundsAnalyzer != null)
                    return boundsAnalyzer.Progress * 0.05f;
                float completed = viewIndex * sourceFrames.Length + outputFrame;
                float boundsShare = sourceSession != null && sourceSession.flipbookAutoBounds ? 0.05f : 0f;
                return boundsShare + Mathf.Clamp01(completed / (directions.Count * sourceFrames.Length)) *
                    (1f - boundsShare);
            }
        }

        static BrazeFlipbookExporter()
        {
            RecoverInterruptedStage();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += AbortForReload;
        }

        public static void Start(BrazeExportSession session)
        {
            if (Active) throw new InvalidOperationException("A flipbook export is already running.");
            if (BrazeVdbImporter.Active) throw new InvalidOperationException("An OpenVDB import is already running.");
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.Normalize();
            BrazeDerivedRegionBuilder.Ensure(session);
            BrazeFluidCacheAsset selectedCache = session.FlipbookUsesDerivedRegion && session.derivedRegionCache != null &&
                                                 session.derivedRegionCache.IsValid
                ? session.derivedRegionCache : session.cache;
            if (selectedCache == null || !selectedCache.IsValid)
                throw new InvalidOperationException("Bake a valid master or derived cache first.");

            int[] selectedFrames = BuildSourceFrames(session, selectedCache == session.derivedRegionCache);

            int hardwareMaximum = Mathf.Max(1, SystemInfo.maxTextureSize);
            ResolveLayout(selectedFrames.Length, session.flipbookFrameResolution,
                session.flipbookMaxAtlasResolution, session.flipbookLayout,
                session.flipbookColumns, session.flipbookRows, hardwareMaximum,
                out Vector2Int selectedFrameResolution, out int selectedColumns, out int selectedRows,
                out Vector2Int selectedAtlasResolution);
            PreflightAssetPath(session.outputFolder, session.outputName);
            string outputDirectory = Path.GetFullPath(session.outputFolder);
            Directory.CreateDirectory(outputDirectory);
            string selectedStage = null;
            BrazeExportSession selectedRenderSession = null;
            try
            {
                selectedRenderSession = ScriptableObject.CreateInstance<BrazeExportSession>();
                selectedRenderSession.hideFlags = HideFlags.HideAndDontSave;
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(session), selectedRenderSession);
                selectedRenderSession.cache = selectedCache;
                selectedRenderSession.duration = selectedCache.Duration;
                selectedRenderSession.regionIn = 0f;
                selectedRenderSession.regionOut = selectedCache.Duration;
                selectedRenderSession.regionPlayback = BrazeExportSession.RegionPlayback.Once;
                selectedRenderSession.ResolveSources();

                selectedStage = BrazeExportStaging.Create(outputDirectory, "flipbook");
                EditorPrefs.SetString(InterruptedStageKey, selectedStage);
                sourceSession = session;
                sourceCache = selectedCache;
                renderSession = selectedRenderSession;
                selectedRenderSession = null;
                sourceFrames = selectedFrames;
                captureBounds = session.flipbookAutoBounds
                    ? new Bounds(Vector3.zero, selectedCache.worldSizeAtBake)
                    : new Bounds(session.flipbookBoundsCenter, session.flipbookBoundsSize);
                if (!session.flipbookAutoBounds) ApplyCaptureBounds(renderSession, captureBounds);
                directions.Clear();
                directions.Add(session.flipbookView);
                frameResolution = selectedFrameResolution;
                columns = selectedColumns;
                rows = selectedRows;
                atlasWidth = selectedAtlasResolution.x;
                atlasHeight = selectedAtlasResolution.y;
                stageDirectory = selectedStage;
                preview = new BrazeExportCachePreview();
                viewIndex = 0;
                outputFrame = 0;
                cancelRequested = false;
                error = null;
                atlases.Clear();
                atlasFiles.Clear();
                atlasFinalFiles.Clear();
                retainedAtlasFinalFiles.Clear();
                retainedDirections.Clear();
                publicationBackups.Clear();
                publishedFiles.Clear();
                AllocateViewAtlas();
                jobId = session.BeginJob(BrazeExportSession.JobKind.Flipbook, "Preparing flipbook export");
                if (session.flipbookAutoBounds)
                {
                    boundsAnalyzer = new BrazeFlipbookSequenceBounds(
                        BrazeExportCachePreview.ResolveBinaryPath(selectedCache), selectedFrames);
                    state = State.AnalyzingBounds;
                    status = "Analyzing sequence-wide occupied bounds…";
                }
                else
                {
                    state = State.Preparing;
                    status = "Loading export cache…";
                }
            }
            catch
            {
                if (selectedRenderSession != null) UnityEngine.Object.DestroyImmediate(selectedRenderSession);
                ResetFailedStart(selectedStage);
                throw;
            }
        }

        static void ResetFailedStart(string selectedStage)
        {
            boundsAnalyzer?.Dispose();
            boundsAnalyzer = null;
            preview?.Dispose();
            preview = null;
            if (renderSession != null) UnityEngine.Object.DestroyImmediate(renderSession);
            renderSession = null;
            if (!string.IsNullOrEmpty(selectedStage) && Directory.Exists(selectedStage) &&
                !BrazeExportStaging.TryDelete(selectedStage, "flipbook", out string stageError))
                Debug.LogWarning($"[BrazeFX] Could not remove flipbook staging folder: {stageError}");
            EditorPrefs.DeleteKey(InterruptedStageKey);
            sourceSession = null;
            sourceCache = null;
            sourceFrames = null;
            frameResolution = default;
            captureBounds = default;
            directions.Clear();
            retainedAtlasFinalFiles.Clear();
            retainedDirections.Clear();
            atlasLdr = null;
            atlasHdr = null;
            stageDirectory = null;
            jobId = null;
            state = State.Idle;
        }

        public static void Cancel()
        {
            if (Active) cancelRequested = true;
        }

        static void Tick()
        {
            if (!Active) return;
            if (sourceSession != null) sourceSession.UpdateJob(jobId, status, Progress);
            try
            {
                if (cancelRequested && state != State.WaitingReadback)
                {
                    Cleanup("Flipbook export cancelled.", false);
                    return;
                }
                switch (state)
                {
                    case State.AnalyzingBounds:
                        TickBounds();
                        break;
                    case State.Preparing:
                    case State.WaitingFrame:
                        TickFrame();
                        break;
                    case State.WaitingReadback:
                        TickReadback();
                        break;
                    case State.Finalizing:
                        FinalizeAsset();
                        Cleanup("Flipbook export complete.", true);
                        break;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"[BrazeFX] Flipbook export failed: {e}");
                Cleanup("Flipbook export failed.", false);
            }
        }

        static void TickBounds()
        {
            if (boundsAnalyzer == null) throw new InvalidOperationException("Flipbook bounds analyzer was not created.");
            status = $"Analyzing stable occupied bounds · {Mathf.RoundToInt(boundsAnalyzer.Progress * 100f)}%";
            if (!boundsAnalyzer.Step()) return;
            boundsAnalyzer.TryGetBounds(out captureBounds);
            boundsAnalyzer.Dispose();
            boundsAnalyzer = null;
            ApplyCaptureBounds(renderSession, captureBounds);
            state = State.Preparing;
            status = "Loading export cache…";
        }

        static void ApplyCaptureBounds(BrazeExportSession session, Bounds bounds)
        {
            if (session == null) return;
            // The renderer receives one immutable union for the complete sequence. PositionCamera's manual path is
            // intentionally reused so no individual frame can recenter or resize the camera.
            session.flipbookAutoBounds = false;
            session.flipbookBoundsCenter = bounds.center;
            session.flipbookBoundsSize = bounds.size;
        }

        static void TickFrame()
        {
            int cacheFrame = sourceFrames[outputFrame];
            status = $"{directions[viewIndex]} · frame {outputFrame + 1}/{sourceFrames.Length}";
            if (!preview.PrepareFrame(renderSession, cacheFrame))
            {
                status += $" · {preview.Status} · cache frame {preview.CurrentFrame}";
                if (!string.IsNullOrEmpty(preview.Error)) status += $" · {preview.Error}";
                if (preview.Status == BrazeCachePlayer.CacheStatus.Error)
                    throw new InvalidOperationException(preview.Error ?? "Cache preview failed while rendering the flipbook.");
                state = State.WaitingFrame;
                return;
            }
            EnsureFrameTarget();
            preview.RenderFrame(frameTarget, directions[viewIndex], sourceSession.flipbookProjection,
                sourceSession.flipbookPadding, sourceSession.flipbookBackground);
            TextureFormat readFormat = sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr
                ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            readback = AsyncGPUReadback.Request(frameTarget, 0, readFormat);
            state = State.WaitingReadback;
        }

        static void TickReadback()
        {
            if (!readback.done) return;
            if (readback.hasError) throw new InvalidOperationException("GPU readback failed while composing the flipbook atlas.");
            if (sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr)
                CopyHdr(readback.GetData<ushort>());
            else
                CopyLdr(readback.GetData<Color32>());
            outputFrame++;
            if (outputFrame < sourceFrames.Length)
            {
                state = State.WaitingFrame;
                return;
            }

            FinishView();
            viewIndex++;
            outputFrame = 0;
            if (cancelRequested)
            {
                Cleanup("Flipbook export cancelled.", false);
                return;
            }
            if (viewIndex >= directions.Count)
            {
                state = State.Finalizing;
                return;
            }
            AllocateViewAtlas();
            state = State.WaitingFrame;
        }

        static void CopyLdr(NativeArray<Color32> source)
        {
            int width = frameResolution.x;
            int height = frameResolution.y;
            int atlasX = (outputFrame % columns) * width;
            int atlasY = (rows - 1 - outputFrame / columns) * height;
            Color32[] pixels = source.ToArray();
            PrepareLdrPixels(pixels, width, height, sourceSession.flipbookPremultipliedAlpha,
                sourceSession.flipbookAlphaDilation);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                atlasLdr[(atlasY + y) * atlasWidth + atlasX + x] = pixels[y * width + x];
        }

        internal static void PrepareLdrPixels(Color32[] pixels, int width, int height,
            bool premultipliedAlpha, int dilation)
        {
            if (pixels == null || pixels.Length < width * height) return;
            if (premultipliedAlpha) return;
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                Color32 value = pixels[pixel];
                if (value.a > 0 && value.a < 255)
                {
                    float inverse = 255f / value.a;
                    value.r = (byte)Mathf.Clamp(Mathf.RoundToInt(value.r * inverse), 0, 255);
                    value.g = (byte)Mathf.Clamp(Mathf.RoundToInt(value.g * inverse), 0, 255);
                    value.b = (byte)Mathf.Clamp(Mathf.RoundToInt(value.b * inverse), 0, 255);
                }
                pixels[pixel] = value;
            }
            DilateTransparentRgb(pixels, width, height, dilation);
        }

        internal static void DilateTransparentRgb(Color32[] pixels, int width, int height, int radius)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height || radius <= 0) return;
            radius = Mathf.Clamp(radius, 0, 16);
            var originalAlpha = new byte[width * height];
            for (int i = 0; i < originalAlpha.Length; i++) originalAlpha[i] = pixels[i].a;
            Color32[] working = (Color32[])pixels.Clone();
            for (int iteration = 0; iteration < radius; iteration++)
            {
                Color32[] next = (Color32[])working.Clone();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (working[index].a != 0) continue;
                    Color32 best = default;
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if ((ox == 0 && oy == 0) || nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        Color32 candidate = working[ny * width + nx];
                        if (candidate.a > best.a) best = candidate;
                    }
                    if (best.a == 0) continue;
                    next[index] = new Color32(best.r, best.g, best.b, 1);
                }
                working = next;
            }
            for (int i = 0; i < originalAlpha.Length; i++)
                pixels[i] = new Color32(working[i].r, working[i].g, working[i].b, originalAlpha[i]);
        }

        static void CopyHdr(NativeArray<ushort> source)
        {
            int width = frameResolution.x;
            int height = frameResolution.y;
            int atlasX = (outputFrame % columns) * width;
            int atlasY = (rows - 1 - outputFrame / columns) * height;
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 4;
                int dst = ((atlasY + y) * atlasWidth + atlasX) * 4;
                for (int x = 0; x < width * 4; x++) atlasHdr[dst + x] = source[src + x];
            }
        }

        static void AllocateViewAtlas()
        {
            if (sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr)
            {
                atlasHdr = new ushort[checked(atlasWidth * atlasHeight * 4)];
                atlasLdr = null;
            }
            else
            {
                atlasLdr = new Color32[checked(atlasWidth * atlasHeight)];
                atlasHdr = null;
            }
        }

        static void FinishView()
        {
            bool hdr = sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr;
            var texture = new Texture2D(atlasWidth, atlasHeight, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32,
                false, hdr)
            {
                name = sourceSession.outputName + "_" + directions[viewIndex],
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            if (hdr) texture.SetPixelData(atlasHdr, 0);
            else texture.SetPixels32(atlasLdr);
            texture.Apply(false, false);

            string extension = hdr ? "exr" : sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Tga ? "tga" : "png";
            byte[] bytes = hdr ? texture.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)
                : sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Tga ? texture.EncodeToTGA()
                : texture.EncodeToPNG();
            string folder = Path.GetFullPath(sourceSession.outputFolder);
            string finalFile = Path.Combine(folder, $"{sourceSession.outputName}_{directions[viewIndex]}.{extension}");
            string temporaryFile = Path.Combine(stageDirectory, Path.GetFileName(finalFile));
            File.WriteAllBytes(temporaryFile, bytes);
            atlases.Add(texture);
            atlasFiles.Add(temporaryFile);
            atlasFinalFiles.Add(finalFile);
            atlasHdr = null;
            atlasLdr = null;
        }

        static void FinalizeAsset()
        {
            WriteManifest();
            PublishAtlasFiles();
            string assetPath = FlipbookAssetPath(sourceSession.outputFolder, sourceSession.outputName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(assetPath)) ?? Application.dataPath);
            AssetDatabase.Refresh();
            ConfigurePublishedAtlasImporters();
            UnityEngine.Object occupied = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (occupied != null && occupied is not BrazeFlipbookAsset)
                throw new InvalidOperationException($"'{assetPath}' is occupied by {occupied.GetType().Name}.");
            BrazeFlipbookAsset asset = occupied as BrazeFlipbookAsset;
            bool created = asset == null;
            var oldTextures = new List<Texture2D>();
            string previousJson = null;
            if (created)
            {
                asset = ScriptableObject.CreateInstance<BrazeFlipbookAsset>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }
            else
            {
                previousJson = EditorJsonUtility.ToJson(asset);
                UnityEngine.Object[] old = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                for (int i = 0; i < old.Length; i++)
                    if (old[i] is Texture2D texture) oldTextures.Add(texture);
            }

            var addedTextures = new List<Texture2D>();
            try
            {
                var preservedViews = new List<BrazeFlipbookAsset.View>();
                if (asset.views != null)
                    for (int i = 0; i < asset.views.Count; i++)
                    {
                        BrazeFlipbookAsset.View existing = asset.views[i];
                        if (existing?.texture == null || directions.Contains(existing.direction) ||
                            !retainedDirections.Contains(existing.direction)) continue;
                        preservedViews.Add(existing);
                    }

                asset.version = 2;
                asset.frameCount = sourceFrames.Length;
                asset.fps = sourceCache.fps;
                asset.columns = columns;
                asset.rows = rows;
                asset.frameResolution = frameResolution;
                asset.worldSize = captureBounds.size;
                asset.boundsCenter = captureBounds.center;
                asset.pivot = sourceSession.flipbookPivot;
                asset.hdr = sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr;
                asset.linear = asset.hdr;
                asset.premultipliedAlpha = sourceSession.flipbookPremultipliedAlpha;
                asset.looping = sourceSession.RegionLoops;
                asset.sourceFingerprint = BrazeExportFingerprint.CacheFingerprint(sourceCache);
                asset.createdUtc = DateTime.UtcNow.ToString("O");
                asset.projection = sourceSession.flipbookProjection.ToString();
                asset.views ??= new List<BrazeFlipbookAsset.View>();
                asset.views.Clear();
                asset.views.AddRange(preservedViews);
                for (int i = 0; i < atlases.Count; i++)
                {
                    Texture2D texture = atlases[i];
                    texture.name = directions[i].ToString();
                    AssetDatabase.AddObjectToAsset(texture, asset);
                    addedTextures.Add(texture);
                    asset.views.Add(new BrazeFlipbookAsset.View
                    {
                        name = directions[i].ToString(),
                        direction = directions[i],
                        texture = texture,
                        frameWorldSize = ResolvePlayerFrameSize(directions[i]),
                    });
                }
                asset.views.Sort((a, b) => a.direction.CompareTo(b.direction));
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            catch
            {
                if (created)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
                else
                {
                    EditorJsonUtility.FromJsonOverwrite(previousJson, asset);
                    for (int i = 0; i < addedTextures.Count; i++)
                        if (addedTextures[i] != null) UnityEngine.Object.DestroyImmediate(addedTextures[i], true);
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
                throw;
            }

            try
            {
                var retainedTextures = new HashSet<Texture2D>();
                for (int i = 0; i < asset.views.Count; i++)
                    if (asset.views[i]?.texture != null) retainedTextures.Add(asset.views[i].texture);
                bool removedObsoleteTexture = false;
                for (int i = 0; i < oldTextures.Count; i++)
                    if (oldTextures[i] != null && !retainedTextures.Contains(oldTextures[i]))
                    {
                        UnityEngine.Object.DestroyImmediate(oldTextures[i], true);
                        removedObsoleteTexture = true;
                    }
                if (removedObsoleteTexture) AssetDatabase.SaveAssets();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BrazeFX] Flipbook asset was updated, but obsolete embedded textures could not all be removed: {e.Message}");
            }

            try
            {
                sourceSession.flipbookAsset = asset;
                EditorUtility.SetDirty(sourceSession);
                AssetDatabase.SaveAssetIfDirty(sourceSession);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BrazeFX] Flipbook asset was saved, but its session link could not be updated: {e.Message}");
            }
            AssetDatabase.Refresh();
            CommitPublication();
            EditorGUIUtility.PingObject(asset);
        }

        static void WriteManifest()
        {
            string output = Path.GetFullPath(sourceSession.outputFolder).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var merged = new Dictionary<BrazeFlipbookAsset.ViewDirection, string>();
            BrazeFlipbookExportManifest previous = ReadPublishedManifest(output);
            if (previous != null)
            {
                var seen = new HashSet<BrazeFlipbookAsset.ViewDirection>();
                int entryCount = Mathf.Min(previous.views.Length, previous.files.Length);
                for (int i = 0; i < entryCount; i++)
                {
                    if (!Enum.TryParse(previous.views[i], true,
                            out BrazeFlipbookAsset.ViewDirection direction) ||
                        !Enum.IsDefined(typeof(BrazeFlipbookAsset.ViewDirection), direction) ||
                        !seen.Add(direction))
                        throw new InvalidDataException(
                            $"Existing flipbook manifest contains an invalid or duplicate view at entry {i}.");
                    string candidate = PublishedFilePath(output, previous.files[i]);
                    if (candidate == null)
                        throw new InvalidDataException(
                            $"Existing flipbook manifest contains an unsafe file path at entry {i}.");
                    if (directions.Contains(direction)) continue;
                    if (!File.Exists(candidate))
                        throw new FileNotFoundException(
                            $"Cannot preserve the existing {direction} view because its atlas is missing.", candidate);
                    merged[direction] = Path.GetFileName(candidate);
                }
            }
            ValidateExistingAssetViews(merged.Keys);
            if (merged.Count > 0) ValidateCompatibleViewSet(previous);

            for (int i = 0; i < directions.Count; i++)
                merged[directions[i]] = Path.GetFileName(atlasFinalFiles[i]);
            var orderedDirections = new List<BrazeFlipbookAsset.ViewDirection>(merged.Keys);
            orderedDirections.Sort();
            string[] viewNames = new string[orderedDirections.Count];
            string[] files = new string[orderedDirections.Count];
            retainedAtlasFinalFiles.Clear();
            retainedDirections.Clear();
            for (int i = 0; i < orderedDirections.Count; i++)
            {
                viewNames[i] = orderedDirections[i].ToString();
                files[i] = merged[orderedDirections[i]];
                retainedDirections.Add(orderedDirections[i]);
                retainedAtlasFinalFiles.Add(Path.Combine(output, files[i]));
            }
            var manifest = new BrazeFlipbookExportManifest
            {
                sourceCache = sourceCache.name,
                sourceFingerprint = BrazeExportFingerprint.CacheFingerprint(sourceCache),
                createdUtc = DateTime.UtcNow.ToString("O"),
                format = sourceSession.flipbookFormat.ToString(),
                projection = sourceSession.flipbookProjection.ToString(),
                views = viewNames,
                files = files,
                frameCount = sourceFrames.Length,
                fps = sourceCache.fps,
                columns = columns,
                rows = rows,
                frameResolution = BrazeFlipbookExporter.frameResolution,
                atlasResolution = new Vector2Int(atlasWidth, atlasHeight),
                worldSize = captureBounds.size,
                boundsCenter = captureBounds.center,
                pivot = sourceSession.flipbookPivot,
                alphaDilation = sourceSession.flipbookPremultipliedAlpha
                    ? 0 : sourceSession.flipbookAlphaDilation,
                premultipliedAlpha = sourceSession.flipbookPremultipliedAlpha,
                looping = sourceSession.RegionLoops,
            };
            string fileName = sourceSession.outputName + "_Flipbook.manifest.json";
            string temporary = Path.Combine(stageDirectory, fileName);
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true));
            atlasFiles.Add(temporary);
            atlasFinalFiles.Add(Path.Combine(output, fileName));
        }

        static BrazeFlipbookExportManifest ReadPublishedManifest(string output)
        {
            string path = Path.Combine(output, sourceSession.outputName + "_Flipbook.manifest.json");
            return ReadManifestFile(path);
        }

        internal static BrazeFlipbookExportManifest ReadManifestFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                BrazeFlipbookExportManifest manifest =
                    JsonUtility.FromJson<BrazeFlipbookExportManifest>(File.ReadAllText(path));
                if (manifest?.views == null || manifest.files == null || manifest.views.Length == 0 ||
                    manifest.views.Length != manifest.files.Length)
                    throw new InvalidDataException("The manifest has no complete view/file table.");
                return manifest;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception e)
            {
                throw new InvalidDataException(
                    $"Existing flipbook manifest '{path}' could not be read. No outputs were changed.", e);
            }
        }

        static string PublishedFilePath(string output, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            string candidate = Path.GetFullPath(Path.Combine(output, file));
            string root = output + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }

        static void ValidateCompatibleViewSet(BrazeFlipbookExportManifest previous)
        {
            string fingerprint = BrazeExportFingerprint.CacheFingerprint(sourceCache);
            int dilation = sourceSession.flipbookPremultipliedAlpha ? 0 : sourceSession.flipbookAlphaDilation;
            bool compatible = previous != null &&
                              previous.sourceFingerprint == fingerprint &&
                              previous.format == sourceSession.flipbookFormat.ToString() &&
                              previous.projection == sourceSession.flipbookProjection.ToString() &&
                              previous.frameCount == sourceFrames.Length &&
                              Mathf.Approximately(previous.fps, sourceCache.fps) &&
                              previous.columns == columns && previous.rows == rows &&
                              previous.frameResolution == frameResolution &&
                              previous.atlasResolution == new Vector2Int(atlasWidth, atlasHeight) &&
                              Approximately(previous.worldSize, captureBounds.size) &&
                              Approximately(previous.boundsCenter, captureBounds.center) &&
                              Approximately(previous.pivot, sourceSession.flipbookPivot) &&
                              previous.alphaDilation == dilation &&
                              previous.premultipliedAlpha == sourceSession.flipbookPremultipliedAlpha &&
                              previous.looping == sourceSession.RegionLoops;
            if (compatible) return;
            throw new InvalidOperationException(
                $"Cannot add {directions[0]} to '{sourceSession.outputName}' because its existing views use " +
                "different source, layout, projection, bounds, or colour settings. Match the existing book settings " +
                "or choose a new Name; existing views were preserved.");
        }

        static void ValidateExistingAssetViews(
            ICollection<BrazeFlipbookAsset.ViewDirection> manifestDirections)
        {
            string assetPath = FlipbookAssetPath(sourceSession.outputFolder, sourceSession.outputName);
            BrazeFlipbookAsset asset = AssetDatabase.LoadAssetAtPath<BrazeFlipbookAsset>(assetPath);
            var assetDirections = new HashSet<BrazeFlipbookAsset.ViewDirection>();
            if (asset?.views != null)
                for (int i = 0; i < asset.views.Count; i++)
                {
                    BrazeFlipbookAsset.View view = asset.views[i];
                    if (view == null || directions.Contains(view.direction)) continue;
                    if (view.texture == null || !assetDirections.Add(view.direction))
                        throw new InvalidDataException(
                            $"Existing Flipbook Asset '{assetPath}' has an invalid {view.direction} view. " +
                            "No outputs were changed.");
                }
            if (assetDirections.SetEquals(manifestDirections)) return;
            throw new InvalidDataException(
                $"Existing Flipbook Asset '{assetPath}' and its manifest disagree about preserved views. " +
                "No outputs were changed; repair the missing asset/file or use a new Name.");
        }

        static bool Approximately(Vector3 a, Vector3 b)
            => (a - b).sqrMagnitude <= 1e-8f;

        static Vector2 ResolvePlayerFrameSize(BrazeFlipbookAsset.ViewDirection direction)
        {
            float aspect = frameResolution.x / (float)Mathf.Max(1, frameResolution.y);
            BrazeFlipbookCameraFraming.Frame frame = BrazeFlipbookCameraFraming.Resolve(
                Vector3.zero, Quaternion.identity, captureBounds.center, captureBounds.size, direction,
                sourceSession.flipbookProjection, aspect, sourceSession.flipbookPadding);
            return frame.PlaneSizeAt(captureBounds.center, aspect);
        }

        static void ConfigurePublishedAtlasImporters()
        {
            int requiredMaximum = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(atlasWidth, atlasHeight)), 32, 16384);
            for (int i = 0; i < atlasFinalFiles.Count; i++)
            {
                string extension = Path.GetExtension(atlasFinalFiles[i]);
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".exr", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryAssetPath(atlasFinalFiles[i], out string assetPath)) continue;
                if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) continue;

                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                bool hdr = extension.Equals(".exr", StringComparison.OrdinalIgnoreCase);
                importer.sRGBTexture = !hdr;
                importer.alphaIsTransparency = false; // Preserve the authored RGB/alpha representation verbatim.
                TextureImporterPlatformSettings defaults = importer.GetDefaultPlatformTextureSettings();
                defaults.maxTextureSize = requiredMaximum;
                defaults.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SetPlatformTextureSettings(defaults);
                importer.SaveAndReimport();
            }
        }

        static bool TryAssetPath(string path, out string assetPath)
        {
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string full = Path.GetFullPath(path).Replace('\\', '/');
            if (full.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase))
            {
                assetPath = "Assets" + full.Substring(assets.Length);
                return true;
            }
            assetPath = null;
            return false;
        }

        static void EnsureFrameTarget()
        {
            int width = frameResolution.x;
            int height = frameResolution.y;
            RenderTextureFormat format = sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr
                ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            if (frameTarget != null && frameTarget.width == width && frameTarget.height == height && frameTarget.format == format)
                return;
            ReleaseFrameTarget();
            frameTarget = new RenderTexture(width, height, 24, format,
                sourceSession.flipbookFormat == BrazeExportSession.FlipbookFormat.Exr
                    ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
            {
                name = "Braze Flipbook Frame",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1,
                useMipMap = false,
            };
            frameTarget.Create();
        }

        static int[] BuildSourceFrames(BrazeExportSession session, bool derived)
        {
            if (derived)
            {
                var all = new int[session.derivedRegionCache.frameCount];
                for (int i = 0; i < all.Length; i++) all[i] = i;
                return all;
            }
            int count = session.RegionFrameCount;
            int start = session.RegionStartFrame;
            count = Mathf.Clamp(count, 1, session.cache.frameCount - start);
            var selected = new int[count];
            for (int i = 0; i < count; i++) selected[i] = start + i;
            return selected;
        }

        internal static void ComputeLayout(int frameCount, int frameWidth, int frameHeight, int maximumSize,
            out int outputColumns, out int outputRows)
            => ComputeLayout(frameCount, frameWidth, frameHeight, maximumSize, maximumSize,
                out outputColumns, out outputRows);

        internal static void ComputeLayout(int frameCount, int frameWidth, int frameHeight,
            int maximumWidth, int maximumHeight, out int outputColumns, out int outputRows)
        {
            frameCount = Mathf.Max(1, frameCount);
            if (frameWidth <= 0 || frameHeight <= 0 || maximumWidth <= 0 || maximumHeight <= 0 ||
                frameWidth > maximumWidth || frameHeight > maximumHeight)
                throw new InvalidOperationException(
                    $"A {frameWidth}×{frameHeight} cell exceeds the {maximumWidth}×{maximumHeight} atlas limit.");

            int maxColumns = Mathf.Max(1, maximumWidth / Mathf.Max(1, frameWidth));
            int maxRows = Mathf.Max(1, maximumHeight / Mathf.Max(1, frameHeight));
            if ((long)maxColumns * maxRows < frameCount)
                throw new InvalidOperationException(
                    $"{frameCount} frames at {frameWidth}×{frameHeight} do not fit a {maximumWidth}×{maximumHeight} atlas. " +
                    "Reduce Cell Size, increase Max Atlas, or shorten the Region.");
            outputColumns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(frameCount * frameHeight / (float)Mathf.Max(1, frameWidth))), 1, maxColumns);
            outputRows = Mathf.CeilToInt(frameCount / (float)outputColumns);
            while (outputRows > maxRows && outputColumns < maxColumns)
            {
                outputColumns++;
                outputRows = Mathf.CeilToInt(frameCount / (float)outputColumns);
            }
        }

        internal static void ResolveLayout(int frameCount, Vector2Int automaticCellSize,
            Vector2Int atlasResolution, BrazeExportSession.FlipbookLayout layout,
            int requestedColumns, int requestedRows, int hardwareMaximum,
            out Vector2Int resolvedCellSize, out int resolvedColumns, out int resolvedRows,
            out Vector2Int resolvedAtlasResolution)
        {
            hardwareMaximum = Mathf.Max(1, hardwareMaximum);
            if (layout == BrazeExportSession.FlipbookLayout.Manual)
            {
                resolvedColumns = Mathf.Max(1, requestedColumns);
                resolvedRows = Mathf.Max(1, requestedRows);
                long capacity = (long)resolvedColumns * resolvedRows;
                if (capacity < Mathf.Max(1, frameCount))
                    throw new InvalidOperationException(
                        $"A {resolvedColumns}×{resolvedRows} layout has {capacity} cells but needs {Mathf.Max(1, frameCount)}.");
                if (atlasResolution.x <= 0 || atlasResolution.y <= 0 ||
                    atlasResolution.x > hardwareMaximum || atlasResolution.y > hardwareMaximum)
                    throw new InvalidOperationException(
                        $"The {atlasResolution.x}×{atlasResolution.y} atlas exceeds this GPU's {hardwareMaximum} texture limit.");
                if (atlasResolution.x % resolvedColumns != 0 || atlasResolution.y % resolvedRows != 0)
                    throw new InvalidOperationException(
                        $"Atlas Size must divide evenly by {resolvedColumns} Columns and {resolvedRows} Rows.");

                resolvedCellSize = new Vector2Int(
                    atlasResolution.x / resolvedColumns, atlasResolution.y / resolvedRows);
                if (resolvedCellSize.x < 64 || resolvedCellSize.y < 64 ||
                    resolvedCellSize.x > 4096 || resolvedCellSize.y > 4096)
                    throw new InvalidOperationException(
                        $"The manual layout produces {resolvedCellSize.x}×{resolvedCellSize.y} cells; each axis must be 64–4096.");
                resolvedAtlasResolution = atlasResolution;
                return;
            }

            int maximumWidth = Mathf.Min(atlasResolution.x, hardwareMaximum);
            int maximumHeight = Mathf.Min(atlasResolution.y, hardwareMaximum);
            resolvedCellSize = automaticCellSize;
            ComputeLayout(frameCount, resolvedCellSize.x, resolvedCellSize.y,
                maximumWidth, maximumHeight, out resolvedColumns, out resolvedRows);
            resolvedAtlasResolution = new Vector2Int(
                resolvedColumns * resolvedCellSize.x, resolvedRows * resolvedCellSize.y);
        }

        internal static long EstimateWorkingBytes(int frameCount, Vector2Int automaticCellSize,
            Vector2Int atlasResolution, BrazeExportSession.FlipbookLayout layout,
            int requestedColumns, int requestedRows, int hardwareMaximum, int views, bool hdr,
            out Vector2Int resolvedCellSize, out int resolvedColumns, out int resolvedRows,
            out Vector2Int resolvedAtlasResolution)
        {
            ResolveLayout(frameCount, automaticCellSize, atlasResolution, layout,
                requestedColumns, requestedRows, hardwareMaximum,
                out resolvedCellSize, out resolvedColumns, out resolvedRows, out resolvedAtlasResolution);
            long pixels = checked((long)resolvedAtlasResolution.x * resolvedAtlasResolution.y);
            return checked(pixels * (hdr ? 8L : 4L) * Mathf.Max(1, views));
        }

        static string FlipbookAssetPath(string outputFolder, string outputName)
        {
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string folder = Path.GetFullPath(outputFolder).Replace('\\', '/').TrimEnd('/');
            string assetFolder;
            if (folder.Equals(assets, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase))
                assetFolder = "Assets" + folder.Substring(assets.Length);
            else
                assetFolder = "Assets/BrazeFX Exports";
            return assetFolder.TrimEnd('/') + "/" + outputName + "_Flipbook.asset";
        }

        static void PreflightAssetPath(string outputFolder, string outputName)
        {
            string assetPath = FlipbookAssetPath(outputFolder, outputName);
            UnityEngine.Object occupied = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (occupied != null && occupied is not BrazeFlipbookAsset)
                throw new InvalidOperationException($"'{assetPath}' is occupied by {occupied.GetType().Name}.");
        }

        static void AbortForReload()
        {
            if (Active) Cleanup("Flipbook export interrupted by script reload.", false);
        }

        static void Cleanup(string finalStatus, bool success)
        {
            BrazeExportSession completedSession = sourceSession;
            string completedJob = jobId;
            boundsAnalyzer?.Dispose();
            boundsAnalyzer = null;
            preview?.Dispose();
            preview = null;
            ReleaseFrameTarget();
            if (renderSession != null) UnityEngine.Object.DestroyImmediate(renderSession);
            renderSession = null;
            if (!success)
            {
                for (int i = 0; i < atlases.Count; i++)
                    if (atlases[i] != null && !AssetDatabase.Contains(atlases[i])) UnityEngine.Object.DestroyImmediate(atlases[i]);
            }
            atlases.Clear();
            for (int i = 0; i < atlasFiles.Count; i++)
                try { if (File.Exists(atlasFiles[i])) File.Delete(atlasFiles[i]); } catch { }
            atlasFiles.Clear();
            atlasFinalFiles.Clear();
            retainedAtlasFinalFiles.Clear();
            retainedDirections.Clear();
            if (!success) RollbackPublication();
            else CommitPublication();
            if (!string.IsNullOrEmpty(stageDirectory) && Directory.Exists(stageDirectory) &&
                !BrazeExportStaging.TryDelete(stageDirectory, "flipbook", out string stageError))
                Debug.LogWarning($"[BrazeFX] Could not remove flipbook staging folder: {stageError}");
            EditorPrefs.DeleteKey(InterruptedStageKey);
            atlasLdr = null;
            atlasHdr = null;
            sourceFrames = null;
            frameResolution = default;
            captureBounds = default;
            directions.Clear();
            sourceCache = null;
            sourceSession = null;
            cancelRequested = false;
            stageDirectory = null;
            jobId = null;
            status = finalStatus;
            state = State.Idle;
            if (success) error = null;
            if (completedSession != null)
                completedSession.FinishJob(completedJob,
                    success ? BrazeExportSession.JobState.Completed : error != null
                        ? BrazeExportSession.JobState.Failed : BrazeExportSession.JobState.Cancelled,
                    finalStatus, error);
        }

        static void ReleaseFrameTarget()
        {
            if (frameTarget == null) return;
            frameTarget.Release();
            UnityEngine.Object.DestroyImmediate(frameTarget);
            frameTarget = null;
        }

        static void PublishAtlasFiles()
        {
            publicationBackups.Clear();
            publishedFiles.Clear();
            try
            {
                BackupStaleAtlasFiles();
                for (int i = 0; i < atlasFiles.Count; i++)
                {
                    string temporary = atlasFiles[i];
                    string final = atlasFinalFiles[i];
                    string backup = null;
                    if (File.Exists(final))
                    {
                        backup = BrazeExportStaging.BackupPath(stageDirectory, final);
                        File.Move(final, backup);
                        publicationBackups.Add((final, backup));
                    }
                    File.Move(temporary, final);
                    publishedFiles.Add(final);
                }
                atlasFiles.Clear();
            }
            catch
            {
                RollbackPublication();
                throw;
            }
        }

        static void BackupStaleAtlasFiles()
        {
            string output = Path.GetFullPath(sourceSession.outputFolder).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string manifestPath = Path.Combine(output, sourceSession.outputName + "_Flipbook.manifest.json");
            if (!File.Exists(manifestPath)) return;
            BrazeFlipbookExportManifest previous;
            try { previous = JsonUtility.FromJson<BrazeFlipbookExportManifest>(File.ReadAllText(manifestPath)); }
            catch { return; }
            if (previous?.files == null) return;
            string prefix = output + Path.DirectorySeparatorChar;
            for (int i = 0; i < previous.files.Length; i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(output, previous.files[i] ?? string.Empty));
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    retainedAtlasFinalFiles.Contains(candidate) ||
                    !File.Exists(candidate)) continue;
                string backup = BrazeExportStaging.BackupPath(stageDirectory, candidate);
                File.Move(candidate, backup);
                publicationBackups.Add((candidate, backup));
            }
        }

        static void CommitPublication()
        {
            for (int i = 0; i < publicationBackups.Count; i++)
                try { if (File.Exists(publicationBackups[i].backup)) File.Delete(publicationBackups[i].backup); } catch { }
            publicationBackups.Clear();
            publishedFiles.Clear();
        }

        static void RollbackPublication()
        {
            for (int i = 0; i < publishedFiles.Count; i++)
                try { if (File.Exists(publishedFiles[i])) File.Delete(publishedFiles[i]); } catch { }
            for (int i = 0; i < publicationBackups.Count; i++)
                try
                {
                    if (File.Exists(publicationBackups[i].backup))
                        File.Move(publicationBackups[i].backup, publicationBackups[i].final);
                }
                catch { }
            publicationBackups.Clear();
            publishedFiles.Clear();
        }

        static void RecoverInterruptedStage()
        {
            BrazeStageRecoveryResult result = BrazeExportStaging.Recover(
                InterruptedStageKey, "flipbook", out string detail);
            if (result == BrazeStageRecoveryResult.Removed)
            {
                status = "Recovered and removed incomplete flipbook staging data.";
            }
            else if (result == BrazeStageRecoveryResult.Rejected)
            {
                status = "An interrupted flipbook path failed ownership validation and was left untouched.";
                Debug.LogWarning($"[BrazeFX] {status} {detail}");
            }
        }
    }
}
