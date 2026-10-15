using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [Serializable]
    internal sealed class BrazeVolumeExportManifest
    {
        public int version = 3;
        public string generator = "BrazeFX Export Studio";
        public string format;
        public string valueSemantics = "render-conditioned scalar fields";
        public string coordinateSystem = "Unity local space, Y-up, left-handed (+Z forward)";
        public string sourceCache;
        public string sourceFingerprint;
        public string createdUtc;
        public string units;
        public string[] grids;
        public string[] files;
        public int[] sourceFrames;
        public int frameCount;
        public float fps;
        public Vector3Int resolution;
        public Vector3 worldSize;
        public Vector3 voxelSize;
        public bool derivedRegion;
        public bool looping;
        public float densityGain;
        public float temperatureGain;
        public float emissionGain;
        public bool hasBakedLook;
        public BrazeRenderSettings bakedLook = BrazeRenderSettings.Default;
        public bool hasBakedKeyLight;
        public Vector3 bakedKeyLightDirection;
        public Color bakedKeyLightColor = Color.white;
    }

    /// <summary>Incremental sparse volume sequence exporter. Frames are decoded and handed to the VDB facade one
    /// at a time; all results stay in a staging directory until the complete sequence can be published atomically.</summary>
    [InitializeOnLoad]
    internal static class BrazeVolumeExporter
    {
        const string InterruptedStageKey = "BrazeFX.ExportStudio.VolumeStage";
        static BrazeExportSession session;
        static BrazeExportSession.VolumeGrids exportGrids;
        static BrazeFluidCacheAsset sourceCache;
        static BrazeFluidCacheFormat.Reader reader;
        static int[] brickCounts;
        static int[] sourceFrames;
        static readonly List<string> stagedFiles = new();
        static readonly List<string> finalFiles = new();
        static string stageDirectory;
        static string outputDirectory;
        static int outputFrame;
        static bool cancelRequested;
        static string status;
        static string error;
        static string jobId;
        static Task writeTask;
        static CancellationTokenSource writeCancellation;
        static string pendingStagedFile;
        static string pendingFinalFile;

        public static bool Active => reader != null;
        public static string Status => status;
        public static string Error => error;
        public static float Progress => !Active || sourceFrames == null || sourceFrames.Length == 0
            ? 0f : Mathf.Clamp01(outputFrame / (float)sourceFrames.Length);

        static BrazeVolumeExporter()
        {
            RecoverInterruptedStage();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += AbortForReload;
        }

        public static void Start(BrazeExportSession value)
        {
            if (Active) throw new InvalidOperationException("A volume export is already running.");
            if (BrazeVdbImporter.Active) throw new InvalidOperationException("An OpenVDB import is already running.");
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.Normalize();
            if (!BrazeVdbBridge.TryGetCapability(out string capability)) throw new InvalidOperationException(capability);
            BrazeDerivedRegionBuilder.Ensure(value);
            BrazeFluidCacheAsset selectedCache = value.VolumeUsesDerivedRegion && value.derivedRegionCache != null && value.derivedRegionCache.IsValid
                ? value.derivedRegionCache : value.cache;
            if (selectedCache == null || !selectedCache.IsValid)
                throw new InvalidOperationException("Bake a valid master or derived cache first.");
            string sourcePath = BrazeExportCachePreview.ResolveBinaryPath(selectedCache);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("The source cache binary could not be found.", sourcePath);

            BrazeFluidCacheFormat.Reader selectedReader = null;
            string selectedStage = null;
            try
            {
                selectedReader = new BrazeFluidCacheFormat.Reader(sourcePath);
                if (!IsAuthoritativeVdbSource(selectedReader.header))
                    throw new InvalidOperationException(
                        "Realtime cache values are quantized and cannot be exported as authoritative OpenVDB grids. " +
                        "Re-bake the source in Export Studio to create the current lossless master.");
                // Current Export Studio masters always contain velocity. Keep the compatibility behavior below for
                // older masters: a velocity-only selection fails loudly; a broader selection exports its scalar
                // grids and explains that the legacy source needs a fresh master bake.
                BrazeExportSession.VolumeGrids selectedGrids = value.volumeGrids;
                if ((selectedGrids & BrazeExportSession.VolumeGrids.Velocity) != 0 &&
                    !BrazeFluidCacheFormat.HasVelocity(selectedReader.header))
                {
                    selectedGrids &= ~BrazeExportSession.VolumeGrids.Velocity;
                    if ((selectedGrids & (BrazeExportSession.VolumeGrids.Density |
                                          BrazeExportSession.VolumeGrids.Temperature |
                                          BrazeExportSession.VolumeGrids.Emission)) == 0)
                        throw new InvalidOperationException(
                            "This legacy cache was baked without velocity. Re-bake the master in Export Studio " +
                            "to export a vel grid.");
                    Debug.LogWarning("[BrazeFX] The source cache was baked without velocity — exporting without " +
                                     "the vel grid. Re-bake the master in Export Studio to include it.");
                }
                int[] selectedBrickCounts = selectedReader.ReadFrameBrickCounts();
                int[] selectedFrames = BuildSourceFrames(value, selectedCache == value.derivedRegionCache, selectedCache.frameCount);
                string selectedOutput = Path.GetFullPath(value.volumeOutputFolder);
                Directory.CreateDirectory(selectedOutput);
                string manifestPath = Path.Combine(selectedOutput, value.volumeOutputName + "_Volume.manifest.json");
                ValidatePublishedManifest(ReadManifestFile(manifestPath), selectedOutput, value.volumeOutputName);
                selectedStage = BrazeExportStaging.Create(selectedOutput, "volume");
                EditorPrefs.SetString(InterruptedStageKey, selectedStage);

                session = value;
                exportGrids = selectedGrids;
                sourceCache = selectedCache;
                reader = selectedReader;
                selectedReader = null;
                brickCounts = selectedBrickCounts;
                sourceFrames = selectedFrames;
                outputDirectory = selectedOutput;
                stageDirectory = selectedStage;
                stagedFiles.Clear();
                finalFiles.Clear();
                outputFrame = 0;
                cancelRequested = false;
                error = null;
                status = "Preparing sparse volume sequence…";
                jobId = value.BeginJob(BrazeExportSession.JobKind.OpenVdb, status);
            }
            catch
            {
                selectedReader?.Dispose();
                reader?.Dispose();
                reader = null;
                if (!string.IsNullOrEmpty(selectedStage) && RemoveStage(selectedStage))
                    EditorPrefs.DeleteKey(InterruptedStageKey);
                session = null;
                sourceCache = null;
                brickCounts = null;
                sourceFrames = null;
                outputDirectory = null;
                stageDirectory = null;
                jobId = null;
                throw;
            }
        }

        public static void Cancel()
        {
            if (Active) cancelRequested = true;
        }

        internal static bool IsAuthoritativeVdbSource(in BrazeFluidCacheFormat.Header header)
            => (header.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) == 0;

        static void Tick()
        {
            if (!Active) return;
            if (session != null) session.UpdateJob(jobId, status, Progress);
            try
            {
                if (cancelRequested)
                {
                    writeCancellation?.Cancel();
                    if (writeTask != null && !writeTask.IsCompleted)
                    {
                        status = "Cancelling OpenVDB export…";
                        return;
                    }
                    if (writeTask != null) CompletePendingFrame();
                    Cleanup("OpenVDB export cancelled.", false);
                    return;
                }
                if (writeTask != null)
                {
                    if (!writeTask.IsCompleted) return;
                    CompletePendingFrame();
                    return;
                }
                if (outputFrame < sourceFrames.Length)
                {
                    ExportNextFrame();
                    return;
                }
                WriteManifest();
                Publish();
                Cleanup("Volume export complete.", true);
            }
            catch (OperationCanceledException) when (cancelRequested)
            {
                Cleanup("OpenVDB export cancelled.", false);
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"[BrazeFX] Volume export failed: {e}");
                Cleanup("Volume export failed.", false);
            }
        }

        static void ExportNextFrame()
        {
            int sourceFrame = sourceFrames[outputFrame];
            string fileName = $"{session.volumeOutputName}_{outputFrame:D4}.vdb";
            string staged = Path.Combine(stageDirectory, fileName);
            string final = Path.Combine(outputDirectory, fileName);
            status = $"Writing OpenVDB frame {outputFrame + 1}/{sourceFrames.Length}";
            BrazeDecodedCacheFrame decoded = BrazeCacheFrameUtility.Decode(reader, sourceFrame, brickCounts[sourceFrame]);
            ResolveGains(out float densityGain, out float temperatureGain, out float emissionGain);
            float sourceTime = sourceCache == session.derivedRegionCache ? outputFrame / Mathf.Max(1e-3f, sourceCache.fps)
                : sourceFrame / Mathf.Max(1e-3f, sourceCache.fps);
            BrazeFluidCacheFormat.Header header = reader.header;
            BrazeExportSession.VolumeGrids grids = exportGrids;
            float threshold = session.volumeBackgroundThreshold;
            float fps = sourceCache.fps;
            string units = session.volumeUnits;
            string sourceName = sourceCache.name;
            writeCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = writeCancellation.Token;
            pendingStagedFile = staged;
            pendingFinalFile = final;
            writeTask = Task.Run(() => BrazeVdbBridge.WriteFrame(staged, header, decoded, grids, threshold,
                sourceFrame, sourceTime, fps, units, sourceName, densityGain, temperatureGain, emissionGain,
                cancellationToken), cancellationToken);
        }

        static void CompletePendingFrame()
        {
            writeTask.GetAwaiter().GetResult();
            if (!File.Exists(pendingStagedFile) || new FileInfo(pendingStagedFile).Length == 0)
                throw new InvalidDataException($"OpenVDB writer produced an empty frame: {Path.GetFileName(pendingStagedFile)}");
            stagedFiles.Add(pendingStagedFile);
            finalFiles.Add(pendingFinalFile);
            outputFrame++;
            writeTask = null;
            writeCancellation.Dispose();
            writeCancellation = null;
            pendingStagedFile = null;
            pendingFinalFile = null;
        }

        static void WriteManifest()
        {
            var gridNames = new List<string>();
            if ((exportGrids & BrazeExportSession.VolumeGrids.Density) != 0) gridNames.Add("density");
            if ((exportGrids & BrazeExportSession.VolumeGrids.Temperature) != 0) gridNames.Add("temperature");
            if ((exportGrids & BrazeExportSession.VolumeGrids.Emission) != 0) gridNames.Add("emission");
            if ((exportGrids & BrazeExportSession.VolumeGrids.Velocity) != 0) gridNames.Add("vel");
            string[] files = new string[finalFiles.Count];
            for (int i = 0; i < files.Length; i++) files[i] = Path.GetFileName(finalFiles[i]);
            ResolveGains(out float densityGain, out float temperatureGain, out float emissionGain);
            var manifest = new BrazeVolumeExportManifest
            {
                format = BrazeExportSession.VolumeFormat.OpenVdb.ToString(),
                sourceCache = sourceCache.name,
                sourceFingerprint = BrazeExportFingerprint.CacheFingerprint(sourceCache),
                createdUtc = DateTime.UtcNow.ToString("O"),
                units = session.volumeUnits,
                grids = gridNames.ToArray(),
                files = files,
                sourceFrames = (int[])sourceFrames.Clone(),
                frameCount = sourceFrames.Length,
                fps = sourceCache.fps,
                resolution = reader.header.baseRes,
                worldSize = reader.header.worldSize,
                voxelSize = BrazeVdbTransformUtility.VoxelSize(reader.header.worldSize, reader.header.baseRes),
                derivedRegion = sourceCache == session.derivedRegionCache,
                looping = session.RegionLoops,
                densityGain = densityGain,
                temperatureGain = temperatureGain,
                emissionGain = emissionGain,
                hasBakedLook = sourceCache.hasBakedLook,
                bakedLook = sourceCache.bakedLook,
                hasBakedKeyLight = sourceCache.hasBakedKeyLight,
                bakedKeyLightDirection = sourceCache.bakedKeyLightDirection,
                bakedKeyLightColor = sourceCache.bakedKeyLightColor,
            };
            string name = session.volumeOutputName + "_Volume.manifest.json";
            string staged = Path.Combine(stageDirectory, name);
            File.WriteAllText(staged, JsonUtility.ToJson(manifest, true));
            stagedFiles.Add(staged);
            finalFiles.Add(Path.Combine(outputDirectory, name));
        }

        static void Publish()
        {
            var backups = new List<(string final, string backup)>();
            var published = new List<string>();
            try
            {
                BackupStaleVolumeFiles(backups);
                for (int i = 0; i < stagedFiles.Count; i++)
                {
                    string final = finalFiles[i];
                    if (File.Exists(final))
                    {
                        string backup = BrazeExportStaging.BackupPath(stageDirectory, final);
                        File.Move(final, backup);
                        backups.Add((final, backup));
                    }
                    File.Move(stagedFiles[i], final);
                    published.Add(final);
                }
                stagedFiles.Clear();
            }
            catch
            {
                for (int i = 0; i < published.Count; i++)
                    try { if (File.Exists(published[i])) File.Delete(published[i]); } catch { }
                for (int i = 0; i < backups.Count; i++)
                    try { if (File.Exists(backups[i].backup)) File.Move(backups[i].backup, backups[i].final); } catch { }
                throw;
            }
            for (int i = 0; i < backups.Count; i++)
                try { if (File.Exists(backups[i].backup)) File.Delete(backups[i].backup); }
                catch (Exception e) { Debug.LogWarning($"[BrazeFX] Could not remove volume export backup: {e.Message}"); }
        }

        static void BackupStaleVolumeFiles(List<(string final, string backup)> backups)
        {
            string manifestPath = Path.Combine(outputDirectory, session.volumeOutputName + "_Volume.manifest.json");
            BrazeVolumeExportManifest previous = ReadManifestFile(manifestPath);
            ValidatePublishedManifest(previous, outputDirectory, session.volumeOutputName);
            if (previous == null) return;
            var current = new HashSet<string>(finalFiles, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < previous.files.Length; i++)
            {
                string candidate = PublishedFilePath(outputDirectory, previous.files[i]);
                if (candidate == null || current.Contains(candidate) || !File.Exists(candidate)) continue;
                string backup = BrazeExportStaging.BackupPath(stageDirectory, candidate);
                File.Move(candidate, backup);
                backups.Add((candidate, backup));
            }
        }

        internal static BrazeVolumeExportManifest ReadManifestFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                BrazeVolumeExportManifest manifest =
                    JsonUtility.FromJson<BrazeVolumeExportManifest>(File.ReadAllText(path));
                if (manifest == null || manifest.version < 1 || manifest.version > 3)
                    throw new InvalidDataException("The manifest version is unsupported.");
                if (!Enum.TryParse(manifest.format, true, out BrazeExportSession.VolumeFormat _))
                    throw new InvalidDataException("The manifest format is invalid.");
                if (manifest.frameCount <= 0 || manifest.files == null ||
                    manifest.files.Length != manifest.frameCount)
                    throw new InvalidDataException("The manifest has no complete frame/file table.");
                if (manifest.grids == null || manifest.grids.Length == 0)
                    throw new InvalidDataException("The manifest has no volume grids.");
                if (manifest.version >= 2 && (manifest.sourceFrames == null ||
                                              manifest.sourceFrames.Length != manifest.frameCount))
                    throw new InvalidDataException("The manifest has no complete source-frame table.");
                return manifest;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception e)
            {
                throw new InvalidDataException(
                    $"Existing volume manifest '{path}' could not be read. No outputs were changed.", e);
            }
        }

        static void ValidatePublishedManifest(BrazeVolumeExportManifest manifest, string output, string outputName)
        {
            if (manifest == null) return;
            if (!Enum.TryParse(manifest.format, true, out BrazeExportSession.VolumeFormat format))
                throw new InvalidDataException("Existing volume manifest format is invalid. No outputs were changed.");
            string extension = format == BrazeExportSession.VolumeFormat.NanoVdb ? "nvdb" : "vdb";
            var seenGrids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < manifest.grids.Length; i++)
            {
                string grid = manifest.grids[i];
                if ((grid != "density" && grid != "temperature" && grid != "emission" && grid != "vel") ||
                    !seenGrids.Add(grid))
                    throw new InvalidDataException(
                        $"Existing volume manifest contains an invalid or duplicate grid at entry {i}. No outputs were changed.");
            }
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < manifest.files.Length; i++)
            {
                string expected = $"{outputName}_{i:D4}.{extension}";
                string file = manifest.files[i];
                string candidate = PublishedFilePath(output, file);
                if (!string.Equals(file, expected, StringComparison.OrdinalIgnoreCase) || candidate == null ||
                    !seenFiles.Add(candidate))
                    throw new InvalidDataException(
                        $"Existing volume manifest contains an unsafe or unexpected file at entry {i}. No outputs were changed.");
            }
        }

        static string PublishedFilePath(string output, string file)
        {
            if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file) return null;
            string root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root, file));
            return BrazeExportStaging.IsSameOrChild(candidate, root) ? candidate : null;
        }

        static void ResolveGains(out float densityGain, out float temperatureGain, out float emissionGain)
        {
            BrazeRenderSettings look = sourceCache != null && sourceCache.hasBakedLook
                ? sourceCache.bakedLook : BrazeRenderSettings.Default;
            densityGain = look.slice.version > 0
                ? Mathf.Max(0f, look.slice.densityGain) : Mathf.Max(0f, look.densityScale);
            temperatureGain = Mathf.Max(0f, look.fireTempScale);
            emissionGain = look.slice.version > 0
                ? Mathf.Max(0f, look.slice.fireGain) : Mathf.Max(0f, look.fireScale);
        }

        internal static int[] BuildSourceFrames(BrazeExportSession value, bool derived, int availableFrames)
        {
            availableFrames = Mathf.Max(1, availableFrames);
            if (derived)
            {
                var all = new int[availableFrames];
                for (int i = 0; i < all.Length; i++) all[i] = i;
                return all;
            }
            int start = value.RegionStartFrame;
            start = Mathf.Clamp(start, 0, availableFrames - 1);
            int count = value.RegionFrameCount;
            count = Mathf.Clamp(count, 1, availableFrames - start);
            var selected = new int[count];
            for (int i = 0; i < count; i++) selected[i] = start + i;
            return selected;
        }

        static void AbortForReload()
        {
            if (!Active) return;
            writeCancellation?.Cancel();
            try { writeTask?.GetAwaiter().GetResult(); }
            catch { }
            Cleanup("Volume export interrupted by script reload; staging output was discarded.", false);
        }

        static void Cleanup(string finalStatus, bool success)
        {
            BrazeExportSession completedSession = session;
            string completedJob = jobId;
            writeCancellation?.Cancel();
            writeCancellation?.Dispose();
            writeCancellation = null;
            writeTask = null;
            pendingStagedFile = null;
            pendingFinalFile = null;
            reader?.Dispose();
            reader = null;
            if (!success)
            {
                for (int i = 0; i < stagedFiles.Count; i++)
                    try { if (File.Exists(stagedFiles[i])) File.Delete(stagedFiles[i]); } catch { }
            }
            if (RemoveStage(stageDirectory)) EditorPrefs.DeleteKey(InterruptedStageKey);
            stagedFiles.Clear();
            finalFiles.Clear();
            brickCounts = null;
            sourceFrames = null;
            sourceCache = null;
            session = null;
            stageDirectory = null;
            outputDirectory = null;
            outputFrame = 0;
            cancelRequested = false;
            jobId = null;
            status = finalStatus;
            if (success) error = null;
            if (completedSession != null)
                completedSession.FinishJob(completedJob,
                    success ? BrazeExportSession.JobState.Completed : error != null
                        ? BrazeExportSession.JobState.Failed : BrazeExportSession.JobState.Cancelled,
                    finalStatus, error);
        }

        static void RecoverInterruptedStage()
        {
            BrazeStageRecoveryResult result = BrazeExportStaging.Recover(
                InterruptedStageKey, "volume", out string detail);
            if (result == BrazeStageRecoveryResult.Removed)
            {
                status = "Recovered and removed incomplete volume-export staging data.";
            }
            else if (result == BrazeStageRecoveryResult.Rejected)
            {
                status = "An interrupted volume-export path failed ownership validation and was left untouched.";
                Debug.LogWarning($"[BrazeFX] {status} {detail}");
            }
        }

        static bool RemoveStage(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return true;
            if (BrazeExportStaging.TryDelete(path, "volume", out string error)) return true;
            Debug.LogWarning($"[BrazeFX] Could not remove VDB staging folder: {error}");
            return false;
        }
    }
}
