using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Converts OpenVDB into the normal Braze cache/player path. Component imports accept a manifest or discover it
    /// from any numbered VDB frame and import the complete sequence; a standalone VDB remains a still. Conversion is
    /// frame-bounded and the destination binary is published only after every source frame is read successfully.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeVdbImporter
    {
        const int BrickSize = BrazeFluidCacheFormat.DefaultBrickSize;
        static string[] frameFiles;
        static BrazeVolumeExportManifest manifest;
        static BrazeVdbBridge.FrameInfo firstInfo;
        static BrazeFluidCacheFormat.Writer writer;
        static string stageDirectory;
        static string stagedBinary;
        static string finalBinary;
        static string assetPath;
        static string binaryFile;
        static int frameIndex;
        static bool cancelRequested;
        static string status;
        static string error;
        static BrazeVDBImport importOwner;
        static Task importTask;
        static CancellationTokenSource importCancellation;

        public static bool Active => writer != null;
        public static float Progress => !Active || frameFiles == null || frameFiles.Length == 0
            ? 0f : Mathf.Clamp01(frameIndex / (float)frameFiles.Length);
        public static string Status => status;
        public static string Error => error;
        public static BrazeVDBImport Owner => importOwner;

        internal static bool NeedsComponentImport(string inputPath, BrazeFluidCacheAsset cache)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return false;
            try
            {
                string full = Path.GetFullPath(inputPath);
                if (!File.Exists(full)) return false;
                if (cache == null || !cache.IsValid) return true;

                BrazeVolumeExportManifest source = null;
                if (string.Equals(Path.GetExtension(full), ".vdb", StringComparison.OrdinalIgnoreCase))
                    source = FindSiblingManifest(full, out _);
                else if (string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
                    source = BrazeVolumeExporter.ReadManifestFile(full);
                if (source == null) return false;

                bool sourceHasVelocity = source.grids != null &&
                    Array.Exists(source.grids, grid => string.Equals(grid, "vel", StringComparison.Ordinal));
                return cache.frameCount != source.frameCount || cache.baseRes != source.resolution ||
                       !Mathf.Approximately(cache.fps, source.fps) || cache.hasVelocity != sourceHasVelocity;
            }
            catch { return false; }
        }

        static BrazeVdbImporter()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += AbortForReload;
        }

        internal static void Start(string inputPath, BrazeVDBImport owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            string identity = Path.GetFullPath(inputPath);
            if (string.Equals(Path.GetExtension(identity), ".vdb", StringComparison.OrdinalIgnoreCase) &&
                FindSiblingManifest(identity, out string manifestPath) != null)
                identity = manifestPath;
            Start(inputPath, AutomaticDestination(identity), owner);
        }

        internal static void Start(string inputPath, string destinationAssetPath, BrazeVDBImport owner = null)
        {
            if (Active) throw new InvalidOperationException("An OpenVDB import is already running.");
            if (BrazeFlipbookExporter.Active || BrazeVolumeExporter.Active)
                throw new InvalidOperationException("Wait for the current delivery export to finish before importing OpenVDB.");
            error = null;
            if (!BrazeVdbBridge.TryGetCapability(out string capability)) throw new InvalidOperationException(capability);
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                throw new FileNotFoundException("The OpenVDB input does not exist.", inputPath);
            destinationAssetPath = NormalizeAssetPath(destinationAssetPath);
            UnityEngine.Object occupied = AssetDatabase.LoadMainAssetAtPath(destinationAssetPath);
            if (occupied != null && occupied is not BrazeFluidCacheAsset)
                throw new InvalidOperationException($"'{destinationAssetPath}' is occupied by {occupied.GetType().Name}.");

            ResolveInput(Path.GetFullPath(inputPath), owner != null, out string[] selectedFiles,
                out BrazeVolumeExportManifest selectedManifest);
            BrazeVdbBridge.FrameInfo selectedInfo = BrazeVdbBridge.ReadFrameInfo(selectedFiles[0]);
            selectedInfo = ApplyManifestDomain(selectedInfo, selectedManifest);
            ValidateDimensions(selectedInfo);
            ValidateManifest(selectedManifest, selectedInfo, selectedFiles.Length);

            string destinationDirectory = Path.GetDirectoryName(destinationAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(destinationDirectory)) destinationDirectory = "Assets";
            if (!AssetDatabase.IsValidFolder(destinationDirectory))
                throw new DirectoryNotFoundException($"The destination asset folder does not exist: '{destinationDirectory}'.");
            string destinationAbsoluteDirectory = Path.GetFullPath(destinationDirectory);
            string baseName = Path.GetFileNameWithoutExtension(destinationAssetPath);
            string selectedFinalBinary = Path.Combine(destinationAbsoluteDirectory, baseName + ".brazecache");
            string selectedStage = BrazeExportStaging.Create(destinationAbsoluteDirectory, "vdb-import");
            string selectedStagedBinary = Path.Combine(selectedStage, baseName + ".brazecache");

            BrazeFluidCacheFormat.Writer selectedWriter = null;
            try
            {
                bool hasVelocity = (selectedInfo.gridMask & (int)BrazeExportSession.VolumeGrids.Velocity) != 0;
                var header = new BrazeFluidCacheFormat.Header
                {
                    baseRes = selectedInfo.Resolution,
                    warpRes = Vector3Int.zero,
                    brickSize = BrickSize,
                    fps = selectedManifest != null ? Mathf.Max(1e-3f, selectedManifest.fps) : Mathf.Max(1e-3f, selectedInfo.fps),
                    worldSize = selectedInfo.WorldSize,
                    flags = BrazeFluidCacheFormat.CacheFlags.RenderReady | BrazeFluidCacheFormat.CacheFlags.Deflate |
                            (hasVelocity ? BrazeFluidCacheFormat.CacheFlags.VelocityField : 0),
                    channelMask = hasVelocity ? BrazeFluidCacheFormat.ChannelMaskFull : 0x07u,
                };
                selectedWriter = new BrazeFluidCacheFormat.Writer(selectedStagedBinary, header);
                frameFiles = selectedFiles;
                manifest = selectedManifest;
                firstInfo = selectedInfo;
                writer = selectedWriter;
                selectedWriter = null;
                stageDirectory = selectedStage;
                stagedBinary = selectedStagedBinary;
                finalBinary = selectedFinalBinary;
                assetPath = destinationAssetPath;
                binaryFile = Path.GetFileName(selectedFinalBinary);
                frameIndex = 0;
                cancelRequested = false;
                importOwner = owner;
                status = $"Reading OpenVDB frame 1/{frameFiles.Length}";
            }
            catch
            {
                selectedWriter?.Dispose();
                TryRemoveStage(selectedStage);
                throw;
            }
        }

        public static void Cancel()
        {
            if (Active) cancelRequested = true;
        }

        static string AutomaticDestination(string inputPath)
        {
            const string folder = "Assets/BrazeFX Imports";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                if (!AssetDatabase.IsValidFolder("Assets"))
                    throw new DirectoryNotFoundException("The project Assets folder is unavailable.");
                string guid = AssetDatabase.CreateFolder("Assets", "BrazeFX Imports");
                if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
                    throw new IOException("Could not create the automatic VDB import data folder.");
            }
            string source = Path.GetFullPath(inputPath).Replace('\\', '/');
            string name = SuggestedName(inputPath);
            string suffix = Hash128.Compute(source.ToLowerInvariant()).ToString().Substring(0, 8);
            return $"{folder}/{name}_{suffix}.asset";
        }

        static string SuggestedName(string source)
        {
            string name = Path.GetFileNameWithoutExtension(source);
            if (name.EndsWith("_Volume.manifest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - "_Volume.manifest".Length);
            int underscore = name.LastIndexOf('_');
            if (underscore > 0 && name.Length - underscore == 5 && int.TryParse(name.Substring(underscore + 1), out _))
                name = name.Substring(0, underscore);
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "BrazeVDB" : name;
        }

        static void Tick()
        {
            if (!Active) return;
            try
            {
                if (!cancelRequested && EditorUtility.DisplayCancelableProgressBar("BrazeFX OpenVDB Import",
                        status ?? "Importing…", Progress))
                    cancelRequested = true;
                if (cancelRequested)
                {
                    importCancellation?.Cancel();
                    if (importTask != null && !importTask.IsCompleted)
                    {
                        status = "Cancelling OpenVDB import…";
                        return;
                    }
                    if (importTask != null)
                    {
                        try { CompletePendingFrame(); }
                        catch (OperationCanceledException) { }
                    }
                    Cleanup("OpenVDB import cancelled.");
                    return;
                }
                if (importTask != null)
                {
                    if (!importTask.IsCompleted) return;
                    CompletePendingFrame();
                    return;
                }
                if (frameIndex < frameFiles.Length)
                {
                    ImportNextFrame();
                    return;
                }
                writer.Finish();
                writer.Dispose();
                writer = null;
                ValidateStagedCache();
                BrazeFluidCacheAsset asset = Publish();
                bool selectAsset = importOwner == null;
                BrazeVDBImport completedOwner = importOwner;
                ApplyOwner(asset);
                Cleanup("OpenVDB import complete.");
                if (selectAsset)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
                else if (completedOwner != null && !Application.isPlaying)
                    BrazeVdbPreviewController.SetFrame(completedOwner, 0);
            }
            catch (OperationCanceledException) when (cancelRequested)
            {
                Cleanup("OpenVDB import cancelled.");
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"[BrazeFX] OpenVDB import failed: {e}");
                Cleanup("OpenVDB import failed.");
            }
        }

        static void ImportNextFrame()
        {
            string path = frameFiles[frameIndex];
            status = $"Reading OpenVDB frame {frameIndex + 1}/{frameFiles.Length}";
            BrazeVdbBridge.FrameInfo info = ApplyManifestDomain(BrazeVdbBridge.ReadFrameInfo(path), manifest);
            ValidateFrameConsistency(info, path);
            ResolveImportScales(info, out float densityScale, out float temperatureScale, out float emissionScale);
            importCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = importCancellation.Token;
            BrazeFluidCacheFormat.Writer targetWriter = writer;
            importTask = Task.Run(() =>
            {
                BrazeDecodedCacheFrame frame = BrazeVdbBridge.ReadSparseFrame(path, info,
                    densityScale, temperatureScale, emissionScale, BrickSize,
                    cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                targetWriter.WriteFrame(frame.brickCount, frame.indices, frame.blocks);
            }, cancellationToken);
        }

        static void CompletePendingFrame()
        {
            importTask.GetAwaiter().GetResult();
            frameIndex++;
            importTask = null;
            importCancellation.Dispose();
            importCancellation = null;
        }

        static BrazeFluidCacheAsset Publish()
        {
            string backup = null;
            string previousAssetJson = null;
            BrazeFluidCacheAsset existing = AssetDatabase.LoadAssetAtPath<BrazeFluidCacheAsset>(assetPath);
            bool created = existing == null;
            if (existing != null) previousAssetJson = EditorJsonUtility.ToJson(existing);
            try
            {
                if (File.Exists(finalBinary))
                {
                    backup = BrazeExportStaging.BackupPath(stageDirectory, finalBinary);
                    File.Move(finalBinary, backup);
                }
                File.Move(stagedBinary, finalBinary);
                BrazeFluidCacheAsset asset = existing ?? ScriptableObject.CreateInstance<BrazeFluidCacheAsset>();
                using (var reader = new BrazeFluidCacheFormat.Reader(finalBinary))
                {
                    BrazeFluidCacheFormat.Header header = reader.header;
                    asset.version = BrazeFluidCacheFormat.Version;
                    asset.baseRes = header.baseRes;
                    asset.warpRes = header.warpRes;
                    asset.hasVelocity = BrazeFluidCacheFormat.HasVelocity(header);
                    asset.scalarQuantized = (header.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;
                    asset.brickSize = header.brickSize;
                    asset.frameCount = header.frameCount;
                    asset.fps = header.fps;
                    asset.worldSizeAtBake = header.worldSize;
                }
                asset.binaryFile = binaryFile;
                asset.binaryBytes = new FileInfo(finalBinary).Length;
                ApplyAppearance(asset, manifest);
                if (created) AssetDatabase.CreateAsset(asset, assetPath);
                BrazeCacheBuildProcessor.EnsureBuildPath(asset, assetPath);
                BrazeCacheBuildProcessor.EnsureBuildFingerprint(asset, finalBinary);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                if (!string.IsNullOrEmpty(backup) && File.Exists(backup)) File.Delete(backup);
                Debug.Log($"[BrazeFX] Imported {asset.frameCount} OpenVDB frame(s) → {assetPath} " +
                          $"({BrazeContainer.FormatBytes(asset.binaryBytes)}). Runtime cache is ready for playback.");
                return asset;
            }
            catch
            {
                try
                {
                    if (File.Exists(finalBinary)) File.Delete(finalBinary);
                    if (!string.IsNullOrEmpty(backup) && File.Exists(backup)) File.Move(backup, finalBinary);
                    if (created && AssetDatabase.LoadMainAssetAtPath(assetPath) != null) AssetDatabase.DeleteAsset(assetPath);
                    else if (existing != null && !string.IsNullOrEmpty(previousAssetJson))
                    {
                        EditorJsonUtility.FromJsonOverwrite(previousAssetJson, existing);
                        EditorUtility.SetDirty(existing);
                    }
                }
                catch (Exception rollback) { Debug.LogError($"[BrazeFX] OpenVDB import rollback failed: {rollback}"); }
                throw;
            }
        }

        static void ApplyOwner(BrazeFluidCacheAsset asset)
        {
            if (importOwner == null) return;
            importOwner.cache = asset;
            EditorUtility.SetDirty(importOwner);
            if (importOwner.gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(importOwner.gameObject.scene);
        }

        static void ApplyAppearance(BrazeFluidCacheAsset asset, BrazeVolumeExportManifest source)
        {
            BrazeFluidCacheAsset localSource = source != null && source.version < 3
                ? FindMatchingSourceCache(source) : null;
            bool hasManifestLook = source != null && source.version >= 3 && source.hasBakedLook;
            asset.hasBakedLook = hasManifestLook || (localSource != null && localSource.hasBakedLook);
            asset.bakedLook = hasManifestLook ? source.bakedLook
                : localSource != null && localSource.hasBakedLook ? localSource.bakedLook : BrazeRenderSettings.Default;
            bool hasManifestLight = source != null && source.version >= 3 && source.hasBakedKeyLight;
            asset.hasBakedKeyLight = hasManifestLight || (localSource != null && localSource.hasBakedKeyLight);
            asset.bakedKeyLightDirection = asset.hasBakedKeyLight
                ? hasManifestLight ? source.bakedKeyLightDirection : localSource.bakedKeyLightDirection
                : new Vector3(0.3f, 0.9f, 0.2f).normalized;
            asset.bakedKeyLightColor = asset.hasBakedKeyLight
                ? hasManifestLight ? source.bakedKeyLightColor : localSource.bakedKeyLightColor : Color.white;
        }

        static BrazeFluidCacheAsset FindMatchingSourceCache(BrazeVolumeExportManifest source)
        {
            if (source == null || string.IsNullOrEmpty(source.sourceFingerprint)) return null;
            foreach (string guid in AssetDatabase.FindAssets("t:BrazeFluidCacheAsset"))
            {
                BrazeFluidCacheAsset candidate = AssetDatabase.LoadAssetAtPath<BrazeFluidCacheAsset>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (candidate == null || !candidate.IsValid) continue;
                if (!string.IsNullOrEmpty(source.sourceCache) &&
                    !string.Equals(candidate.name, source.sourceCache, StringComparison.Ordinal)) continue;
                if (string.Equals(BrazeExportFingerprint.CacheFingerprint(candidate), source.sourceFingerprint,
                        StringComparison.Ordinal)) return candidate;
            }
            return null;
        }

        static void ResolveInput(string input, bool preferSequence, out string[] files,
            out BrazeVolumeExportManifest sourceManifest)
        {
            string extension = Path.GetExtension(input);
            if (string.Equals(extension, ".vdb", StringComparison.OrdinalIgnoreCase))
            {
                sourceManifest = FindSiblingManifest(input, out string manifestPath);
                if (preferSequence && sourceManifest != null)
                    files = ResolveManifestFiles(manifestPath, sourceManifest);
                else
                    files = new[] { input };
                return;
            }
            if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a .vdb frame or a Braze _Volume.manifest.json sequence.");
            sourceManifest = BrazeVolumeExporter.ReadManifestFile(input)
                ?? throw new InvalidDataException("The selected JSON file is not a Braze volume manifest.");
            files = ResolveManifestFiles(input, sourceManifest);
        }

        static string[] ResolveManifestFiles(string manifestPath, BrazeVolumeExportManifest sourceManifest)
        {
            if (!Enum.TryParse(sourceManifest.format, true, out BrazeExportSession.VolumeFormat format) ||
                format != BrazeExportSession.VolumeFormat.OpenVdb)
                throw new NotSupportedException("BrazeFX volume import accepts OpenVDB (.vdb) files only.");
            string root = Path.GetDirectoryName(manifestPath);
            var files = new string[sourceManifest.files.Length];
            Dictionary<string, string> relocatedFiles = null;
            for (int i = 0; i < files.Length; i++)
            {
                string file = sourceManifest.files[i];
                if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file ||
                    !string.Equals(Path.GetExtension(file), ".vdb", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Manifest frame {i} has an unsafe or non-VDB file name.");
                string path = Path.GetFullPath(Path.Combine(root, file));
                if (!IsSameOrChild(path, root))
                    throw new InvalidDataException($"Manifest frame {i} escapes the sequence folder.");
                if (!File.Exists(path))
                {
                    string expectedPath = path;
                    relocatedFiles ??= IndexRelocatedFrames(root);
                    if (!relocatedFiles.TryGetValue(file, out path) || string.IsNullOrEmpty(path))
                        throw new FileNotFoundException(
                            $"Manifest frame {i} is missing or ambiguous under the sequence folder.", expectedPath);
                }
                files[i] = path;
            }
            return files;
        }

        static Dictionary<string, string> IndexRelocatedFrames(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in Directory.EnumerateFiles(root, "*.vdb", SearchOption.AllDirectories))
            {
                string file = Path.GetFileName(candidate);
                string full = Path.GetFullPath(candidate);
                if (!IsSameOrChild(full, root)) continue;
                if (result.ContainsKey(file)) result[file] = null; // ambiguous: never guess between two clips
                else result.Add(file, full);
            }
            return result;
        }

        static BrazeVolumeExportManifest FindSiblingManifest(string frame, out string manifestPath)
        {
            manifestPath = null;
            string name = Path.GetFileNameWithoutExtension(frame);
            int underscore = name.LastIndexOf('_');
            if (underscore <= 0 || name.Length - underscore != 5 ||
                !int.TryParse(name.Substring(underscore + 1), out _)) return null;
            string manifestName = name.Substring(0, underscore) + "_Volume.manifest.json";
            string directory = Path.GetDirectoryName(frame);
            for (int level = 0; level < 3 && !string.IsNullOrEmpty(directory); level++)
            {
                string candidate = Path.Combine(directory, manifestName);
                if (File.Exists(candidate))
                {
                    try
                    {
                        BrazeVolumeExportManifest value = BrazeVolumeExporter.ReadManifestFile(candidate);
                        if (value != null && Array.Exists(value.files,
                                file => string.Equals(file, Path.GetFileName(frame), StringComparison.OrdinalIgnoreCase)))
                        {
                            manifestPath = candidate;
                            return value;
                        }
                    }
                    catch { }
                }
                directory = Path.GetDirectoryName(directory);
            }
            return null;
        }

        static void ValidateManifest(BrazeVolumeExportManifest source, BrazeVdbBridge.FrameInfo info, int count)
        {
            if (source == null) return;
            // A sibling manifest may provide appearance for an explicit low-level still import; only require its full
            // sequence count when the component or manifest path selected the complete sequence.
            if (count > 1 && source.frameCount != count)
                throw new InvalidDataException("The volume manifest frame table is incomplete.");
            if (source.resolution != info.Resolution)
                throw new InvalidDataException($"Manifest resolution {source.resolution} does not match the VDB grid {info.Resolution}.");
            if ((source.worldSize - info.WorldSize).sqrMagnitude > 1e-6f)
                throw new InvalidDataException("Manifest world size does not match the VDB transform.");
            int expectedMask = 0;
            for (int i = 0; i < source.grids.Length; i++)
            {
                if (source.grids[i] == "density") expectedMask |= 1;
                else if (source.grids[i] == "temperature") expectedMask |= 2;
                else if (source.grids[i] == "emission") expectedMask |= 4;
                else if (source.grids[i] == "vel") expectedMask |= 8;
            }
            if ((info.gridMask & expectedMask) != expectedMask)
                throw new InvalidDataException("The first VDB frame is missing a grid declared by the manifest.");
        }

        static BrazeVdbBridge.FrameInfo ApplyManifestDomain(BrazeVdbBridge.FrameInfo info,
            BrazeVolumeExportManifest source)
        {
            if (source == null) return info;
            Vector3 expectedVoxel = source.voxelSize.sqrMagnitude > 0f ? source.voxelSize : new Vector3(
                source.worldSize.x / Mathf.Max(1, source.resolution.x),
                source.worldSize.y / Mathf.Max(1, source.resolution.y),
                source.worldSize.z / Mathf.Max(1, source.resolution.z));
            if ((info.VoxelSize - expectedVoxel).sqrMagnitude > 1e-10f)
                throw new InvalidDataException("The VDB transform does not match the volume manifest voxel size.");
            Vector3 expectedOrigin = BrazeVdbTransformUtility.IndexOrigin(source.worldSize, source.resolution);
            if ((info.Origin - expectedOrigin).sqrMagnitude > 1e-8f)
                throw new InvalidDataException("The VDB transform origin does not match the centered volume manifest domain.");
            info.resolutionX = source.resolution.x;
            info.resolutionY = source.resolution.y;
            info.resolutionZ = source.resolution.z;
            info.minX = info.minY = info.minZ = 0;
            info.worldX = source.worldSize.x;
            info.worldY = source.worldSize.y;
            info.worldZ = source.worldSize.z;
            // API-2 Braze exports already carried per-grid gain metadata but predated the explicit domain tags.
            info.isBraze = 1;
            return info;
        }

        static void ValidateDimensions(BrazeVdbBridge.FrameInfo info)
        {
            if (info.resolutionX < 8 || info.resolutionY < 8 || info.resolutionZ < 8)
                throw new InvalidDataException("Braze volume playback requires at least 8 voxels on every axis.");
            if (info.worldX <= 0f || info.worldY <= 0f || info.worldZ <= 0f)
                throw new InvalidDataException("The VDB transform has no positive world-space volume extent.");
        }

        static void ValidateFrameConsistency(BrazeVdbBridge.FrameInfo info, string path)
        {
            if (info.Resolution != firstInfo.Resolution || info.Minimum != firstInfo.Minimum)
                throw new InvalidDataException($"VDB sequence frame '{Path.GetFileName(path)}' changes the index-space bounds.");
            if (info.gridMask != firstInfo.gridMask)
                throw new InvalidDataException($"VDB sequence frame '{Path.GetFileName(path)}' changes the grid set.");
            if ((info.WorldSize - firstInfo.WorldSize).sqrMagnitude > 1e-6f ||
                (info.VoxelSize - firstInfo.VoxelSize).sqrMagnitude > 1e-10f)
                throw new InvalidDataException($"VDB sequence frame '{Path.GetFileName(path)}' changes the grid transform.");
        }

        static void ResolveImportScales(BrazeVdbBridge.FrameInfo info,
            out float density, out float temperature, out float emission)
        {
            if (!info.IsBraze)
            {
                density = temperature = emission = 1f;
                return;
            }
            float densityGain = manifest != null && manifest.version >= 2 ? manifest.densityGain : info.densityGain;
            float temperatureGain = manifest != null && manifest.version >= 2 ? manifest.temperatureGain : info.temperatureGain;
            float emissionGain = manifest != null && manifest.version >= 2 ? manifest.emissionGain : info.emissionGain;
            density = InverseGain(densityGain);
            temperature = InverseGain(temperatureGain);
            emission = InverseGain(emissionGain);
        }

        static float InverseGain(float value) => value > 1e-12f && float.IsFinite(value) ? 1f / value : 0f;

        static void ValidateStagedCache()
        {
            if (!File.Exists(stagedBinary) || new FileInfo(stagedBinary).Length == 0)
                throw new InvalidDataException("OpenVDB conversion produced no cache binary.");
            using var reader = new BrazeFluidCacheFormat.Reader(stagedBinary);
            if (reader.header.frameCount != frameFiles.Length || reader.header.baseRes != firstInfo.Resolution)
                throw new InvalidDataException("OpenVDB conversion produced an incomplete cache.");
            bool expectedVelocity = (firstInfo.gridMask & (int)BrazeExportSession.VolumeGrids.Velocity) != 0;
            bool storedVelocity = (reader.header.flags & BrazeFluidCacheFormat.CacheFlags.VelocityField) != 0;
            if (expectedVelocity != storedVelocity)
                throw new InvalidDataException("OpenVDB conversion did not preserve the source velocity field.");
        }

        static void Cleanup(string finalStatus)
        {
            EditorUtility.ClearProgressBar();
            importCancellation?.Cancel();
            importCancellation?.Dispose();
            importCancellation = null;
            importTask = null;
            writer?.Dispose();
            writer = null;
            TryRemoveStage(stageDirectory);
            status = finalStatus;
            frameFiles = null;
            manifest = null;
            stageDirectory = null;
            stagedBinary = null;
            finalBinary = null;
            assetPath = null;
            binaryFile = null;
            frameIndex = 0;
            cancelRequested = false;
            importOwner = null;
        }

        static void AbortForReload()
        {
            if (!Active) return;
            importCancellation?.Cancel();
            try { importTask?.GetAwaiter().GetResult(); }
            catch { }
            error = "OpenVDB import was interrupted by a script reload; no destination was changed.";
            Cleanup("OpenVDB import interrupted.");
        }

        static string NormalizeAssetPath(string path)
        {
            path = path?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(path), ".asset", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The destination must be a .asset path inside this project's Assets folder.");
            return "Assets/" + path.Substring("Assets/".Length);
        }

        static bool IsSameOrChild(string candidate, string root)
        {
            string a = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static void TryRemoveStage(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            if (!BrazeExportStaging.TryDelete(path, "vdb-import", out string error))
                Debug.LogWarning($"[BrazeFX] Could not remove VDB import staging data: {error}");
        }
    }
}
