using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Installs BrazeFX's single renderer feature into project-owned URP renderer data assets. Unity packages do
    /// not carry a customer's ProjectSettings references, so a fresh import needs this explicit, undoable setup.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeUrpSetup
    {
        const string PromptedThisSessionKey = "BrazeFX.UrpSetup.PromptedThisSession";
        const string DemoRoot = "Assets/BrazeFX/Demos/";
        const string FeaturesProperty = "m_RendererFeatures";
        const string FeatureMapProperty = "m_RendererFeatureMap";

        static BrazeUrpSetup()
        {
            if (Application.isBatchMode) return;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.delayCall += PromptForActiveDemoIfRequired;
        }

        [MenuItem("Tools/BrazeFX/Setup/Configure URP Renderers…", priority = 100)]
        internal static void ConfigureFromMenu()
        {
            List<ScriptableRendererData> renderers = FindProjectRendererData();
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog("BrazeFX URP Setup",
                    "No project-owned URP Renderer Data assets were found. Create or assign a Universal Render " +
                    "Pipeline asset first, then run this command again.", "OK");
                return;
            }

            List<ScriptableRendererData> missing = FindRenderersNeedingConfiguration(renderers);
            if (missing.Count == 0)
            {
                EditorUtility.DisplayDialog("BrazeFX URP Setup",
                    "Braze Render Feature is active with Live Slices enabled on every project URP renderer.", "OK");
                return;
            }

            string message =
                "BrazeFX smoke, fire, particles, trails, CFD overlays, and generated volumes render through one " +
                "URP renderer feature. Configure the following project renderer assets?\n\n" +
                Describe(missing) +
                "\n\nExisting renderer features and settings will be preserved. This operation is undoable.";
            if (!EditorUtility.DisplayDialog("Configure BrazeFX Rendering", message, "Configure", "Cancel")) return;

            Configure(missing, true);
        }

        [MenuItem("Tools/BrazeFX/Setup/Select URP Renderers", priority = 101)]
        static void SelectProjectRenderers()
        {
            List<ScriptableRendererData> renderers = FindProjectRendererData();
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog("BrazeFX URP Setup", "No project-owned URP Renderer Data assets were found.", "OK");
                return;
            }

            Selection.objects = renderers.ConvertAll(renderer => (UnityEngine.Object)renderer).ToArray();
            EditorGUIUtility.PingObject(renderers[0]);
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (scene.path.StartsWith(DemoRoot, StringComparison.Ordinal))
                EditorApplication.delayCall += PromptForActiveDemoIfRequired;
        }

        static void PromptForActiveDemoIfRequired()
        {
            if (Application.isBatchMode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(PromptedThisSessionKey, false))
                return;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.path.StartsWith(DemoRoot, StringComparison.Ordinal)) return;

            List<ScriptableRendererData> missing = FindRenderersNeedingConfiguration(FindProjectRendererData());
            if (missing.Count == 0) return;

            SessionState.SetBool(PromptedThisSessionKey, true);
            string message =
                "This BrazeFX demo contains effects that require Braze Render Feature, but the following URP " +
                "renderer assets are not configured:\n\n" + Describe(missing) +
                "\n\nConfigure them now? Existing renderer features and settings will be preserved.";
            if (EditorUtility.DisplayDialog("BrazeFX Rendering Setup Required", message, "Configure Now", "Later"))
                Configure(missing, true);
        }

        static void Configure(IReadOnlyList<ScriptableRendererData> renderers, bool showResult)
        {
            int changed = 0;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Configure BrazeFX URP Rendering");
            try
            {
                for (int i = 0; i < renderers.Count; i++)
                    if (EnsureBrazeRenderFeature(renderers[i])) changed++;

                AssetDatabase.SaveAssets();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("BrazeFX URP Setup Failed",
                    "BrazeFX could not configure every renderer. No assets outside the listed URP renderer data " +
                    "were changed. See the Console for details.", "OK");
                return;
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            if (!showResult) return;
            EditorUtility.DisplayDialog("BrazeFX URP Setup Complete",
                changed == 1
                    ? "Braze Render Feature was configured on 1 URP renderer. Smoke and fire are ready to render."
                    : $"Braze Render Feature was configured on {changed} URP renderers. Smoke and fire are ready to render.",
                "OK");
        }

        internal static List<ScriptableRendererData> FindProjectRendererData()
        {
            var renderersByPath = new SortedDictionary<string, ScriptableRendererData>(StringComparer.Ordinal);
            string[] pipelineGuids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets" });
            for (int i = 0; i < pipelineGuids.Length; i++)
            {
                string pipelinePath = AssetDatabase.GUIDToAssetPath(pipelineGuids[i]);
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
                if (pipeline == null) continue;

                var serializedPipeline = new SerializedObject(pipeline);
                SerializedProperty rendererList = serializedPipeline.FindProperty("m_RendererDataList");
                if (rendererList != null && rendererList.isArray)
                {
                    for (int r = 0; r < rendererList.arraySize; r++)
                        AddProjectRenderer(rendererList.GetArrayElementAtIndex(r).objectReferenceValue as ScriptableRendererData,
                            renderersByPath);
                }
                else
                {
                    SerializedProperty legacyRenderer = serializedPipeline.FindProperty("m_RendererData");
                    AddProjectRenderer(legacyRenderer?.objectReferenceValue as ScriptableRendererData, renderersByPath);
                }
            }

            return new List<ScriptableRendererData>(renderersByPath.Values);
        }

        static void AddProjectRenderer(ScriptableRendererData renderer,
            IDictionary<string, ScriptableRendererData> renderersByPath)
        {
            if (renderer == null) return;
            string path = AssetDatabase.GetAssetPath(renderer);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            renderersByPath[path] = renderer;
        }

        internal static List<ScriptableRendererData> FindRenderersNeedingConfiguration(
            IReadOnlyList<ScriptableRendererData> renderers)
        {
            var missing = new List<ScriptableRendererData>();
            for (int i = 0; i < renderers.Count; i++)
            {
                BrazeRenderFeature feature = FindFeature(renderers[i]);
                if (feature == null || !feature.isActive || !feature.liveSlices) missing.Add(renderers[i]);
            }
            return missing;
        }

        internal static bool EnsureBrazeRenderFeature(ScriptableRendererData renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            string rendererPath = AssetDatabase.GetAssetPath(renderer);
            if (string.IsNullOrEmpty(rendererPath) || !rendererPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("BrazeFX can configure only persistent URP renderer assets under Assets/.");

            var serializedRenderer = new SerializedObject(renderer);
            SerializedProperty features = serializedRenderer.FindProperty(FeaturesProperty);
            SerializedProperty featureMap = serializedRenderer.FindProperty(FeatureMapProperty);
            if (features == null || featureMap == null || !features.isArray || !featureMap.isArray)
                throw new InvalidOperationException($"{rendererPath} does not expose the expected URP renderer-feature data.");

            BrazeRenderFeature existing = FindFeature(renderer);
            bool changed = false;
            if (existing != null)
            {
                if (!existing.isActive || !existing.liveSlices)
                {
                    Undo.RecordObject(existing, "Enable Braze Render Feature");
                    existing.SetActive(true);
                    existing.liveSlices = true;
                    EditorUtility.SetDirty(existing);
                    changed = true;
                }
            }
            else
            {
                Undo.RecordObject(renderer, "Add Braze Render Feature");
                var feature = ScriptableObject.CreateInstance<BrazeRenderFeature>();
                feature.name = nameof(BrazeRenderFeature);
                feature.ApplyQualityPreset(BrazeRenderFeature.RenderQualityPreset.Mobile);
                Undo.RegisterCreatedObjectUndo(feature, "Add Braze Render Feature");
                AssetDatabase.AddObjectToAsset(feature, renderer);

                serializedRenderer.Update();
                features = serializedRenderer.FindProperty(FeaturesProperty);
                featureMap = serializedRenderer.FindProperty(FeatureMapProperty);
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                changed = true;
            }

            changed |= SynchronizeFeatureMap(features, featureMap);
            if (!changed) return false;

            serializedRenderer.ApplyModifiedProperties();
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            return true;
        }

        static BrazeRenderFeature FindFeature(ScriptableRendererData renderer)
        {
            if (renderer == null || renderer.rendererFeatures == null) return null;
            for (int i = 0; i < renderer.rendererFeatures.Count; i++)
                if (renderer.rendererFeatures[i] is BrazeRenderFeature feature) return feature;
            return null;
        }

        static bool SynchronizeFeatureMap(SerializedProperty features, SerializedProperty featureMap)
        {
            bool changed = featureMap.arraySize != features.arraySize;
            if (changed) featureMap.arraySize = features.arraySize;
            for (int i = 0; i < features.arraySize; i++)
            {
                long expectedId = 0;
                UnityEngine.Object feature = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (feature != null)
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out expectedId);

                SerializedProperty mapEntry = featureMap.GetArrayElementAtIndex(i);
                if (mapEntry.longValue == expectedId) continue;
                mapEntry.longValue = expectedId;
                changed = true;
            }
            return changed;
        }

        static string Describe(IReadOnlyList<ScriptableRendererData> renderers)
        {
            var lines = new List<string>(renderers.Count);
            for (int i = 0; i < renderers.Count; i++) lines.Add("• " + AssetDatabase.GetAssetPath(renderers[i]));
            return string.Join("\n", lines);
        }
    }
}
