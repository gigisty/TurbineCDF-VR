using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Injects cache binaries referenced by enabled build scenes (plus Resources caches) directly into
    /// StreamingAssets. No duplicate multi-GB file needs to live under Assets/StreamingAssets.</summary>
    public sealed class BrazeCacheBuildProcessor : BuildPlayerProcessor
    {
        public override int callbackOrder => 0;

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Respect the actual BuildPlayerOptions. Scripted builds can pass a scene list that is intentionally
            // different from EditorBuildSettings; scanning the latter silently omitted caches from those Players.
            foreach (string scene in buildPlayerContext.BuildPlayerOptions.scenes)
            {
                if (string.IsNullOrEmpty(scene)) continue;
                foreach (string dep in AssetDatabase.GetDependencies(scene, true)) required.Add(dep);
            }

            // Assets under Resources can be loaded without a scene reference, so include those explicitly.
            foreach (string guid in AssetDatabase.FindAssets("t:BrazeFluidCacheAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0) required.Add(path);
            }

            bool dirtied = false;
            foreach (string assetPath in required)
            {
                var cache = AssetDatabase.LoadAssetAtPath<BrazeFluidCacheAsset>(assetPath);
                if (cache == null || !cache.IsValid) continue;

                string source = ResolveSourcePath(cache, assetPath);
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                    throw new BuildFailedException($"[BrazeFX] Missing cache binary for '{assetPath}': '{source}'.");

                string oldBuildPath = cache.buildBinaryFile;
                string buildPath = EnsureBuildPath(cache, assetPath);
                bool fingerprintChanged = EnsureBuildFingerprint(cache, source);
                dirtied |= !string.Equals(oldBuildPath, buildPath, StringComparison.Ordinal) || fingerprintChanged;
                buildPlayerContext.AddAdditionalPathToStreamingAssets(source, buildPath);
            }
            if (dirtied) AssetDatabase.SaveAssets();
        }

        internal static string EnsureBuildPath(BrazeFluidCacheAsset cache, string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) guid = Hash128.Compute(assetPath).ToString();
            string file = Path.GetFileName(cache.binaryFile);
            if (string.IsNullOrEmpty(file)) file = cache.name + ".brazecache";
            string expected = $"BrazeFXCaches/{guid}/{file}";
            if (!string.Equals(cache.buildBinaryFile?.Replace('\\', '/'), expected, StringComparison.Ordinal))
            {
                cache.buildBinaryFile = expected;
                EditorUtility.SetDirty(cache);
            }
            return cache.buildBinaryFile;
        }

        internal static bool EnsureBuildFingerprint(BrazeFluidCacheAsset cache, string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            string expected = $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
            if (string.Equals(cache.buildFingerprint, expected, StringComparison.Ordinal)) return false;
            cache.buildFingerprint = expected;
            EditorUtility.SetDirty(cache);
            return true;
        }

        internal static string ResolveSourcePath(BrazeFluidCacheAsset cache, string assetPath)
        {
            if (cache == null || string.IsNullOrEmpty(cache.binaryFile)) return null;
            if (Path.IsPathRooted(cache.binaryFile)) return cache.binaryFile;
            string dir = Path.GetDirectoryName(assetPath);
            return Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(dir) ? "Assets" : dir, cache.binaryFile));
        }
    }
}
