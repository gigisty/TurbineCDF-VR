using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Compiles BrazeFX compute kernels once after import instead of letting their first dispatch block the
    /// transition into Play Mode. This only asks Unity for kernel metadata; it does not dispatch the solver,
    /// allocate simulation resources, or alter scene/physics state.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeComputeKernelPrecompiler
    {
        const string ResourceName = "BrazeSolver";
        const string PreparedKeyPrefix = "BrazeFX.ComputeKernelsPrepared.";
        const string KernelDirective = "#pragma kernel ";

        static bool preparationScheduled;

        static BrazeComputeKernelPrecompiler()
        {
            if (Application.isBatchMode) return;
            ScheduleAutomaticPreparation();
        }

        [MenuItem("Tools/BrazeFX/Setup/Prepare Compute Kernels", priority = 102)]
        public static void PrepareComputeKernels()
        {
            Prepare(showDialogs: true);
        }

        static void ScheduleAutomaticPreparation()
        {
            if (preparationScheduled) return;
            preparationScheduled = true;
            EditorApplication.delayCall += TryAutomaticPreparation;
        }

        static void TryAutomaticPreparation()
        {
            preparationScheduled = false;
            if (Application.isBatchMode) return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleAutomaticPreparation();
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Prepare(showDialogs: false);
        }

        static void Prepare(bool showDialogs)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                if (showDialogs)
                    EditorUtility.DisplayDialog("BrazeFX Compute Kernels",
                        "The active graphics device does not support compute shaders.", "OK");
                return;
            }

            ComputeShader shader = Resources.Load<ComputeShader>(ResourceName);
            if (shader == null)
            {
                if (showDialogs)
                    EditorUtility.DisplayDialog("BrazeFX Compute Kernels",
                        "BrazeSolver.compute could not be found in Resources.", "OK");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(assetPath))
            {
                if (showDialogs)
                    EditorUtility.DisplayDialog("BrazeFX Compute Kernels",
                        "BrazeSolver.compute is not backed by a project asset.", "OK");
                return;
            }

            string preparedKey = BuildPreparedKey(assetPath);
            if (!showDialogs && SessionState.GetBool(preparedKey, false)) return;

            List<string> kernels;
            try
            {
                kernels = ReadKernelNames(assetPath);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[BrazeFX] Compute-kernel preparation skipped: {exception.Message}");
                return;
            }

            if (kernels.Count == 0)
            {
                UnityEngine.Debug.LogWarning(
                    $"[BrazeFX] Compute-kernel preparation found no kernels in {assetPath}.");
                return;
            }

            var timer = Stopwatch.StartNew();
            UnityEngine.Debug.Log(
                $"[BrazeFX] Preparing {kernels.Count} compute kernels for " +
                $"{SystemInfo.graphicsDeviceType}. This is a one-time project import step.");

            int compiled = 0;
            try
            {
                for (int i = 0; i < kernels.Count; i++)
                {
                    string kernelName = kernels[i];
                    EditorUtility.DisplayProgressBar(
                        "BrazeFX — Preparing Compute Kernels",
                        $"{kernelName} ({i + 1} of {kernels.Count})",
                        (float)i / kernels.Count);

                    if (!shader.HasKernel(kernelName))
                        throw new InvalidOperationException(
                            $"BrazeSolver.compute does not contain its declared kernel '{kernelName}'.");

                    int kernel = shader.FindKernel(kernelName);
                    shader.GetKernelThreadGroupSizes(kernel, out _, out _, out _);
                    compiled++;
                }

                SessionState.SetBool(preparedKey, true);
                UnityEngine.Debug.Log(
                    $"[BrazeFX] Prepared {compiled} compute kernels in {timer.Elapsed.TotalSeconds:F1}s. " +
                    "Fire, smoke, and CFD scenes can now enter Play Mode without cold kernel compilation.");

                if (showDialogs)
                    EditorUtility.DisplayDialog("BrazeFX Compute Kernels Ready",
                        $"Prepared {compiled} compute kernels for {SystemInfo.graphicsDeviceType} in " +
                        $"{timer.Elapsed.TotalSeconds:F1} seconds.", "OK");
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
                if (showDialogs)
                    EditorUtility.DisplayDialog("BrazeFX Compute Kernel Preparation Failed",
                        "Unity could not prepare every BrazeFX compute kernel. See the Console for details.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static string BuildPreparedKey(string assetPath)
        {
            Hash128 dependencyHash = AssetDatabase.GetAssetDependencyHash(assetPath);
            return PreparedKeyPrefix + dependencyHash + "." + SystemInfo.graphicsDeviceType;
        }

        static List<string> ReadKernelNames(string assetPath)
        {
            var kernels = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string fullPath = Path.GetFullPath(assetPath);

            foreach (string sourceLine in File.ReadLines(fullPath))
            {
                string line = sourceLine.TrimStart();
                if (!line.StartsWith(KernelDirective, StringComparison.Ordinal)) continue;

                string declaration = line.Substring(KernelDirective.Length).Trim();
                int separator = declaration.IndexOfAny(new[] { ' ', '\t' });
                string kernelName = separator >= 0 ? declaration.Substring(0, separator) : declaration;
                if (kernelName.Length > 0 && seen.Add(kernelName)) kernels.Add(kernelName);
            }

            return kernels;
        }
    }
}
