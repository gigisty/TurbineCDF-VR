using System;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Drives a scene-owned VDB import through the normal cache player while Unity is in Edit Mode. Only one imported
    /// volume is previewed at a time, but that preview deliberately survives hierarchy selection changes so artists can
    /// place and align other scene objects against a chosen VDB frame. Runtime transport remains owned by
    /// <see cref="BrazeVolumePlayer"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeVdbPreviewController
    {
        const double TickInterval = 1.0 / 60.0;
        const string ResumeObjectKey = "BrazeFX.VdbPreview.PlayModeObject";
        const string ResumeInstanceKey = "BrazeFX.VdbPreview.PlayModeInstance";
        const string ResumeEntityKey = "BrazeFX.VdbPreview.PlayModeEntity";
        const string ResumeFrameKey = "BrazeFX.VdbPreview.PlayModeFrame";
        const int MaximumRestoreAttempts = 5;

        static BrazeVDBImport player;
        static BrazeFluidCacheAsset boundCache;
        static bool playing;
        static bool previewVisible;
        static int requestedFrame;
        static float previewTime;
        static double lastTick;
        static int restoreAttempts;

        public static event Action StateChanged;

        static BrazeVdbPreviewController()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += HandlePlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseCurrent;
            EditorApplication.quitting += ReleaseCurrent;
        }

        public static bool IsActive(BrazeVDBImport value) =>
            !Application.isPlaying && player == value && value != null;

        public static bool IsPlaying(BrazeVDBImport value) => IsActive(value) && playing;
        public static bool IsPreviewVisible(BrazeVDBImport value) => IsActive(value) && previewVisible;
        public static int Frame(BrazeVDBImport value)
        {
            if (!IsActive(value)) return value != null ? Mathf.Max(0, value.CurrentFrame) : 0;
            return value.CurrentFrame >= 0 ? value.CurrentFrame : requestedFrame;
        }
        public static float Time(BrazeVDBImport value) =>
            IsActive(value) ? previewTime : value != null ? value.CurrentTime : 0f;

        public static void Load(BrazeVDBImport value)
        {
            if (!Activate(value, retryError: true)) return;
            playing = false;
            previewVisible = true;
            requestedFrame = Mathf.Clamp(requestedFrame, 0, LastFrame(value));
            previewTime = FrameTime(value, requestedFrame);
            PumpNow();
        }

        public static void Reload(BrazeVDBImport value)
        {
            if (value == null || Application.isPlaying) return;
            if (player == value) ReleaseCurrent();
            Load(value);
        }

        public static void Unload(BrazeVDBImport value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.UnloadCache(); return; }
            if (player == value) ReleaseCurrent();
            else if (value.Status != BrazeVolumePlayer.CacheStatus.Unloaded) value.UnloadCache();
        }

        public static void Play(BrazeVDBImport value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Play(); return; }
            if (!Activate(value, retryError: true)) return;
            previewVisible = true;
            if (!value.loop && requestedFrame >= LastFrame(value))
            {
                requestedFrame = 0;
                previewTime = 0f;
            }
            playing = true;
            lastTick = EditorApplication.timeSinceStartup;
            PumpNow();
        }

        public static void Pause(BrazeVDBImport value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Pause(); return; }
            if (!Activate(value, retryError: true)) return;
            playing = false;
            previewVisible = true;
            requestedFrame = Mathf.Clamp(Frame(value), 0, LastFrame(value));
            previewTime = FrameTime(value, requestedFrame);
            PumpNow();
        }

        public static void Stop(BrazeVDBImport value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Stop(); return; }
            if (!IsActive(value)) return;
            playing = false;
            previewVisible = false;
            requestedFrame = 0;
            previewTime = 0f;
            value.Stop();
            Notify(repaintScene: true);
        }

        public static void Restart(BrazeVDBImport value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Restart(); return; }
            if (!Activate(value, retryError: true)) return;
            requestedFrame = 0;
            previewTime = 0f;
            previewVisible = true;
            playing = true;
            lastTick = EditorApplication.timeSinceStartup;
            PumpNow();
        }

        public static void SetFrame(BrazeVDBImport value, int frame)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.SeekFrame(frame); return; }
            if (!Activate(value, retryError: true)) return;
            requestedFrame = Mathf.Clamp(frame, 0, LastFrame(value));
            previewTime = FrameTime(value, requestedFrame);
            playing = false;
            previewVisible = true;
            PumpNow();
        }

        public static void Step(BrazeVDBImport value, int delta)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.StepFrames(delta); return; }
            SetFrame(value, Mathf.Clamp(Frame(value) + delta, 0, LastFrame(value)));
        }

        public static void RefreshRenderer(BrazeVDBImport value)
        {
            if (value == null) return;
            value.RefreshLook();
            if (!Application.isPlaying && IsActive(value) && previewVisible) PumpNow();
            else Notify(repaintScene: true);
        }

        static bool Activate(BrazeVDBImport value, bool retryError)
        {
            if (value == null || Application.isPlaying || value.cache == null || !value.cache.IsValid) return false;
            if (player != value || boundCache != value.cache)
            {
                ReleaseCurrent();
                player = value;
                boundCache = value.cache;
                requestedFrame = 0;
                previewTime = 0f;
                playing = false;
                previewVisible = false;
            }
            if (retryError && value.Status == BrazeVolumePlayer.CacheStatus.Error) value.UnloadCache();
            if (value.Status == BrazeVolumePlayer.CacheStatus.Unloaded) value.LoadCache();
            return true;
        }

        static void Tick()
        {
            if (player == null || Application.isPlaying || EditorApplication.isCompiling) return;
            if (player.cache == null || !player.cache.IsValid || player.cache != boundCache)
            {
                ReleaseCurrent();
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (now - lastTick < TickInterval) return;
            float elapsed = Mathf.Clamp((float)(now - lastTick), 0f, 1f / 15f);
            lastTick = now;
            Pump(elapsed);
        }

        static void PumpNow()
        {
            lastTick = EditorApplication.timeSinceStartup;
            Pump(0f);
        }

        static void Pump(float elapsed)
        {
            if (player == null) return;
            BrazeVolumePlayer.CacheStatus previousStatus = player.Status;
            int previousFrame = player.CurrentFrame;
            bool previousPlaying = playing;

            if (playing && player.Status == BrazeVolumePlayer.CacheStatus.Ready)
            {
                float fps = Mathf.Max(1e-3f, player.FramesPerSecond);
                previewTime += elapsed * Mathf.Max(0f, player.playbackSpeed);
                if (player.loop)
                    previewTime = Mathf.Repeat(previewTime, player.FrameCount / fps);
                else
                {
                    float lastTime = LastFrame(player) / fps;
                    if (previewTime >= lastTime)
                    {
                        previewTime = lastTime;
                        playing = false;
                    }
                }
                requestedFrame = player.loop
                    ? Mod(Mathf.RoundToInt(previewTime * fps), Mathf.Max(1, player.FrameCount))
                    : Mathf.Clamp(Mathf.RoundToInt(previewTime * fps), 0, LastFrame(player));
            }

            player.EditorRequestFrame(requestedFrame, playing, 1, player.loop);
            player.EditorTick();
            bool frameReady = false;
            if (player.Status == BrazeVolumePlayer.CacheStatus.Ready && previewVisible)
            {
                player.EditorRequestFrame(requestedFrame, playing, 1, player.loop);
                frameReady = player.EditorPreviewFrame(requestedFrame, playing, 1, player.loop);
                BrazeContainer host = player.EditorPreviewContainer;
                if (frameReady && host != null && !BrazeContainer.Active.Contains(host)) BrazeContainer.Active.Add(host);
            }

            bool changed = previousStatus != player.Status || previousFrame != player.CurrentFrame ||
                           previousPlaying != playing;
            if (changed) Notify(repaintScene: previousStatus != player.Status || previousFrame != player.CurrentFrame);
        }

        static int LastFrame(BrazeVDBImport value) => Mathf.Max(0, value != null ? value.FrameCount - 1 : 0);
        static float FrameTime(BrazeVDBImport value, int frame) =>
            value != null && value.FramesPerSecond > 0f ? frame / value.FramesPerSecond : 0f;
        static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;

        internal static void HandlePlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                RememberPreviewForPlayMode();
                ReleaseCurrent();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                restoreAttempts = 0;
                EditorApplication.delayCall -= RestorePreviewAfterPlayMode;
                EditorApplication.delayCall += RestorePreviewAfterPlayMode;
            }
        }

        static void RememberPreviewForPlayMode()
        {
            if (player == null || !previewVisible)
            {
                ClearPlayModeResume();
                return;
            }

            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(player);
            SessionState.SetString(ResumeObjectKey, id.ToString());
            // The transient identity handles unsaved scenes when domain reload is disabled; GlobalObjectId handles
            // the normal scene reload, where the edit-mode component is recreated on exit.
#if UNITY_6000_5_OR_NEWER
            SessionState.SetString(ResumeEntityKey, player.GetEntityId().ToString());
#else
            SessionState.SetInt(ResumeInstanceKey, player.BrazeInstanceId());
#endif
            SessionState.SetInt(ResumeFrameKey, Mathf.Clamp(Frame(player), 0, LastFrame(player)));
        }

        static void RestorePreviewAfterPlayMode()
        {
            EditorApplication.delayCall -= RestorePreviewAfterPlayMode;
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;

            string serializedId = SessionState.GetString(ResumeObjectKey, string.Empty);
            if (string.IsNullOrEmpty(serializedId)) return;

            BrazeVDBImport value = null;
#if UNITY_6000_5_OR_NEWER
            string serializedEntity = SessionState.GetString(ResumeEntityKey, string.Empty);
            if (ulong.TryParse(serializedEntity, out ulong rawEntity))
                value = EditorUtility.EntityIdToObject(EntityId.FromULong(rawEntity)) as BrazeVDBImport;
#elif UNITY_6000_3_OR_NEWER
            EntityId entityId = SessionState.GetInt(ResumeInstanceKey, 0);
            value = EditorUtility.EntityIdToObject(entityId) as BrazeVDBImport;
#else
            value = EditorUtility.InstanceIDToObject(SessionState.GetInt(ResumeInstanceKey, 0)) as BrazeVDBImport;
#endif
            if (value != null && !string.Equals(
                    GlobalObjectId.GetGlobalObjectIdSlow(value).ToString(), serializedId, StringComparison.Ordinal))
                value = null; // Instance IDs may be recycled across the play-mode scene replacement.
            if (value == null && GlobalObjectId.TryParse(serializedId, out GlobalObjectId id))
                value = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as BrazeVDBImport;

            if (value == null)
            {
                if (++restoreAttempts < MaximumRestoreAttempts)
                {
                    EditorApplication.delayCall += RestorePreviewAfterPlayMode;
                    return;
                }
                ClearPlayModeResume();
                return;
            }

            int frame = SessionState.GetInt(ResumeFrameKey, 0);
            ClearPlayModeResume();
            // SetFrame owns loading, visibility, and the paused transport state. Runtime never sees this value: it is
            // captured and restored entirely by the editor controller around the Play Mode boundary.
            SetFrame(value, frame);
        }

        static void ClearPlayModeResume()
        {
            SessionState.SetString(ResumeObjectKey, string.Empty);
            SessionState.SetString(ResumeEntityKey, string.Empty);
            SessionState.SetInt(ResumeInstanceKey, 0);
            SessionState.SetInt(ResumeFrameKey, 0);
        }

        static void ReleaseCurrent()
        {
            BrazeVDBImport previous = player;
            player = null;
            boundCache = null;
            playing = false;
            previewVisible = false;
            requestedFrame = 0;
            previewTime = 0f;
            if (previous != null && !Application.isPlaying) previous.UnloadCache();
            Notify(repaintScene: true);
        }

        static void Notify(bool repaintScene)
        {
            StateChanged?.Invoke();
            if (!repaintScene) return;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
