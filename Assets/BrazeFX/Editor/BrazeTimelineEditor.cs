using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Minimal inspector for <see cref="BrazeTimeline"/>. The component is just a scene-serialized data
    /// holder + the play-mode driver — ALL authoring (length, loop, tracks, curves, zoom/pan) lives in
    /// <see cref="BrazeTimelineWindow"/>. So the inspector is a summary + one button into the editor.
    /// </summary>
    [CustomEditor(typeof(BrazeTimeline))]
    public sealed class BrazeTimelineEditor : Editor
    {
        Transform inspectedRoot;

        void OnEnable()
        {
            if (target is not BrazeTimeline timeline) return;
            inspectedRoot = timeline.transform;
            BrazeTimelineTransformLockManager.Lock(timeline);
        }

        void OnDisable()
        {
            Transform root = inspectedRoot;
            EditorApplication.delayCall += () =>
            {
                if (root != null && root.GetComponent<BrazeTimeline>() == null)
                    root.hideFlags &= ~(HideFlags.NotEditable | HideFlags.HideInInspector);
            };
        }

        public override void OnInspectorGUI()
        {
            var tl = (BrazeTimeline)target;
            if (tl.AdoptTrackTargets())
            {
                EditorUtility.SetDirty(tl);
                if (!Application.isPlaying && tl.gameObject.scene.IsValid() && tl.gameObject.scene.isLoaded)
                    EditorSceneManager.MarkSceneDirty(tl.gameObject.scene);
            }
            BrazeEditorGUI.Header("TIMELINE", "timeline");

            int missing = 0;
            int duplicates = 0;
            int outsideRoot = 0;
            var bindings = new HashSet<string>();
            for (int i = 0; i < tl.targets.Count; i++)
            {
                Component component = tl.targets[i]?.target;
                if (component != null && !tl.OwnsTarget(component)) outsideRoot++;
            }
            for (int i = 0; i < tl.tracks.Count; i++)
            {
                BrazeTrack track = tl.tracks[i];
                if (track == null || track.IsMissing) { missing++; continue; }
                string key = track.target.BrazeInstanceId() + ":" + track.BindingKey;
                if (!bindings.Add(key)) duplicates++;
            }
            BrazeEditorGUI.Hint($"{tl.targets.Count} card(s)  ·  {tl.tracks.Count} track(s)  ·  {tl.duration:0.##}s  ·  {tl.loopMode}" +
                                (missing > 0 ? $"  ·  {missing} missing" : "") +
                                (outsideRoot > 0 ? $"  ·  {outsideRoot} outside root" : "") +
                                (duplicates > 0 ? $"  ·  {duplicates} duplicate" : "") +
                                (Application.isPlaying && tl.playing ? $"  ·  ▶ {tl.time:0.00}s" : ""));

            GUILayout.Space(6);
            serializedObject.Update();
            if (BrazeEditorGUI.Section("BrazeFX.Timeline.playback", "Playback"))
            {
                BrazeEditorGUI.PropertyField(serializedObject.FindProperty("length"), new GUIContent("End"));
                BrazeEditorGUI.PillsEnum("Loop Mode", serializedObject.FindProperty("loopMode"));
                BrazeEditorGUI.Slider(serializedObject.FindProperty("speed"), 0.01f, 8f, new GUIContent("Speed"));
                BrazeEditorGUI.ToggleButton("Play On Enable", serializedObject.FindProperty("playOnEnable"));
                BrazeEditorGUI.ToggleButton("Ignore Time Scale", serializedObject.FindProperty("ignoreTimeScale"));
                BrazeEditorGUI.ToggleButton("Reset Sim On Loop", serializedObject.FindProperty("resetSimOnLoop"));
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section("BrazeFX.Timeline.frames", "Frames"))
            {
                BrazeEditorGUI.IntSlider(serializedObject.FindProperty("frameRate"), 1, 240,
                    new GUIContent("Frame Rate"));
                BrazeEditorGUI.ToggleButton("Snap To Frames", serializedObject.FindProperty("snapToFrames"));
            }
            BrazeEditorGUI.EndSection();
            serializedObject.ApplyModifiedProperties();

            GUILayout.Space(6);
            if (GUILayout.Button("Open Timeline Editor", GUILayout.Height(32)))
                BrazeTimelineWindow.OpenFor(tl);
            GUILayout.Space(4);
            if (outsideRoot > 0)
                EditorGUILayout.HelpBox("Move every animated GameObject under this Braze Timeline. Existing external " +
                                        "cards are preserved but ignored until their hierarchy is repaired.", MessageType.Warning);
            BrazeEditorGUI.Hint("The Timeline is a fixed, locked authoring root. Put animated GameObjects beneath it, " +
                                "then drop those children into the editor. External objects are rejected.");

            if (Application.isPlaying && tl.playing) Repaint();
        }
    }

    /// <summary>Keeps Timeline roots locked and releases the lock after the component is removed.</summary>
    [InitializeOnLoad]
    static class BrazeTimelineTransformLockManager
    {
        static readonly Dictionary<int, Transform> LockedRoots = new();
        const HideFlags RootFlags = HideFlags.NotEditable | HideFlags.HideInInspector;
        static bool sceneToolOverride;
        static bool previousToolsHidden;

        static BrazeTimelineTransformLockManager()
        {
            EditorApplication.hierarchyChanged += QueueSynchronize;
            EditorApplication.update += EnforceLockedRoots;
            Selection.selectionChanged += UpdateSceneToolVisibility;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseSceneToolVisibility;
            EditorApplication.quitting += ReleaseSceneToolVisibility;
            EditorApplication.delayCall += Synchronize;
        }

        internal static void Lock(BrazeTimeline timeline)
        {
            if (timeline == null || timeline.transform == null) return;
            Transform root = timeline.transform;
            Normalize(root);
            LockedRoots[root.BrazeInstanceId()] = root;
        }

        static void Normalize(Transform root)
        {
            if (root.localPosition != Vector3.zero) root.localPosition = Vector3.zero;
            if (root.localRotation != Quaternion.identity) root.localRotation = Quaternion.identity;
            if (root.localScale != Vector3.one) root.localScale = Vector3.one;
            if ((root.hideFlags & RootFlags) != RootFlags) root.hideFlags |= RootFlags;
        }

        static void EnforceLockedRoots()
        {
            foreach (Transform root in LockedRoots.Values)
                if (root != null && root.GetComponent<BrazeTimeline>() != null) Normalize(root);
            UpdateSceneToolVisibility();
        }

        static void UpdateSceneToolVisibility()
        {
            GameObject selected = Selection.activeGameObject;
            bool shouldHide = selected != null && selected.GetComponent<BrazeTimeline>() != null;
            if (shouldHide)
            {
                if (!sceneToolOverride)
                {
                    previousToolsHidden = Tools.hidden;
                    sceneToolOverride = true;
                }
                Tools.hidden = true;
                return;
            }
            ReleaseSceneToolVisibility();
        }

        static void ReleaseSceneToolVisibility()
        {
            if (!sceneToolOverride) return;
            Tools.hidden = previousToolsHidden;
            sceneToolOverride = false;
        }

        static void QueueSynchronize()
        {
            EditorApplication.delayCall -= Synchronize;
            EditorApplication.delayCall += Synchronize;
        }

        static void Synchronize()
        {
            foreach (BrazeTimeline timeline in Resources.FindObjectsOfTypeAll<BrazeTimeline>())
                if (timeline != null && timeline.gameObject.scene.IsValid()) Lock(timeline);

            var stale = new List<int>();
            foreach (var pair in LockedRoots)
            {
                Transform root = pair.Value;
                if (root == null) { stale.Add(pair.Key); continue; }
                if (root.GetComponent<BrazeTimeline>() != null) continue;
                root.hideFlags &= ~RootFlags;
                stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) LockedRoots.Remove(stale[i]);
            UpdateSceneToolVisibility();
        }
    }
}
