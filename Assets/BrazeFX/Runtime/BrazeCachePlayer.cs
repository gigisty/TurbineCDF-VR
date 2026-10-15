using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BrazeFX
{
    /// <summary>
    /// Shared playback engine for baked/imported <see cref="BrazeFluidCacheAsset"/> volumes. The clip's GPU data is held by a shared,
    /// ref-counted <see cref="BrazeCacheResident"/> (uploaded once per clip); the player drives a HIDDEN
    /// <see cref="BrazeContainer"/> in cache-host mode, scattering each frame into its solver. The slice render
    /// feature then draws that container — the SAME renderer live containers use — so a clip can be rendered
    /// anywhere and duplicated freely (each player owns its own hidden container).
    ///
    /// Explicit residency control: <see cref="LoadCache"/> streams disk→GPU (async — never freezes), <see cref="UnloadCache"/>
    /// frees this player's GPU hold. Poll <see cref="Status"/> / <see cref="IsReady"/> or subscribe to
    /// <see cref="StatusChanged"/> to know when data is ready / freed. With Auto Load on, the simple case just plays.
    ///
    /// Runs at an earlier execution order so the frame is scattered before the URP volume feature composites.
    /// </summary>
    public abstract class BrazeVolumePlayer : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum CacheStatus { Unloaded, Loading, Ready, Error }
        public enum PlaybackState { Stopped, Playing, Paused }

        [Tooltip("Automatically Load the cache when this player is enabled. OFF (default) = manual: you press Load (or call " +
                 "LoadCache() from code) when you want it in GPU memory, and Unload to free it.")]
        public bool autoLoad = false;
        [Tooltip("Start playing automatically once the cache finishes loading. OFF (default) = it loads paused on frame 0; " +
                 "you press Play (or call Play()) to start advancing.")]
        public bool playOnLoad = false;
        [Tooltip("The baked clip to play back.")]
        public BrazeFluidCacheAsset cache;
        [Tooltip("MASTER RENDER TOGGLE. Off = keep advancing + scattering the clip (sim/cache work still runs) but " +
                 "DON'T draw the volume — no raymarch, no fullscreen passes. Toggle it live in play mode and watch the " +
                 "FPS delta: that delta is the volume's pure RENDER cost, isolated from playback/scatter.")]
        public bool enableRendering = true;
        [Tooltip("Loop the clip.")]
        public bool loop = true;
        [Tooltip("Motion-smooth playback between stored frames using the clip's velocity (fixes the stop-motion " +
                 "feel of frame-skipped bakes). One cheap GPU pass per frame; adds two base-resolution working " +
                 "volumes in VRAM while playing. Needs a clip with velocity — scalar-only clips play exact frames.")]
        public bool motionSmoothing = true;
        [Tooltip("Playback speed multiplier.")]
        [Range(0.05f, 4f)] public float playbackSpeed = 1f;
        [Tooltip("Key light for the volume's self-shadowing. Empty = a default warm top-down light.")]
        public Light keyLight;
        [Tooltip("Deduplicate byte-identical voxel bricks in VRAM (lossless). EVOLVING sims share ~0%, so leave OFF " +
                 "unless the clip is a baked LOOP or largely STATIC. Shared across players of the same clip + setting.")]
        public bool dedupResident = false;
        [Tooltip("STREAM the clip from disk instead of loading it all into VRAM. Keeps only a small ring of upcoming " +
                 "frames resident (bounded VRAM, near-instant start) — best for long clips or many simultaneous players. " +
                 "Off = the whole clip is uploaded once (fastest per-frame, VRAM scales with length). Dedup is ignored " +
                 "while streaming. Set before Load (it picks the load path).")]
        public bool streaming = false;

        [Header("Realtime Playback")]
        [HideInInspector, Tooltip("Legacy serialized compatibility. Final cache quality is owned by Braze Render Feature.")]
        public bool realtimeMode = true;
        [HideInInspector, Tooltip("Legacy serialized compatibility. Final cache quality is owned by Braze Render Feature.")]
        [Range(16, 256)] public int realtimeMaxSteps = 256;
        [HideInInspector, Tooltip("Legacy serialized compatibility. Final cache quality is owned by Braze Render Feature.")]
        [Range(0.25f, 1f)] public float realtimeSamplesPerVoxel = 1f;
        [HideInInspector, Tooltip("Legacy serialized compatibility. Final cache quality is owned by Braze Render Feature.")]
        [Min(0)] public int realtimeMaxRaymarchHeight = 0;

        [Header("Instance Variation")]
        [Tooltip("Start the clip at this phase (0..1 of its length). Desyncs duplicated instances so they don't play " +
                 "in lockstep.")]
        [Range(0f, 1f)] public float phaseOffset01 = 0f;
        [Tooltip("Auto-offset each instance's phase by a stable per-instance random — duplicate this player freely and " +
                 "every copy plays at a different point in the clip (no 'hall of mirrors'). Adds to Phase Offset.")]
        public bool randomizePhase = false;
        [Tooltip("Per-instance random speed spread (± fraction of Playback Speed). 0 = every instance plays at exactly " +
                 "Playback Speed; 0.2 = each instance is randomly ±20% faster/slower (stable per instance).")]
        [Range(0f, 0.9f)] public float speedVariance = 0f;

        [Header("Look")]
        [Tooltip("Override the look baked into the clip. Off = render with the clip's baked appearance. When first " +
                 "turned on it copies the clip's baked look, so the look doesn't jump — you tweak from there.")]
        public bool overrideLook = false;
        [Tooltip("The appearance to render with when Override Look is on.")]
        public BrazeRenderSettings lookOverride = BrazeRenderSettings.Default;
        [HideInInspector] public bool lookOverrideSeeded = false;

        // ── Status (load/unload state machine) ──────────────────────────────────────────────────────────
        public CacheStatus Status { get; private set; } = CacheStatus.Unloaded;
        /// <summary>Error detail when <see cref="Status"/> == Error (else null).</summary>
        public string StatusMessage { get; private set; }
        public bool IsReady => Status == CacheStatus.Ready;
        /// <summary>Fired on every Status transition (Loading→Ready, →Error, →Unloaded, …).</summary>
        public event Action<CacheStatus> StatusChanged;
        /// <summary>Fired when Play, Pause, Stop, loading, or unloading changes the transport.</summary>
        public event Action<PlaybackState> PlaybackStateChanged;
        /// <summary>Fired after a requested frame has actually reached the render host.</summary>
        public event Action<int> FrameChanged;

        // Transport: Stopped = loaded but NOT drawn (silent — nothing in the scene); Playing = drawn + advancing;
        // Paused = drawn + frozen. So after Load the volume is invisible until Play; Stop hides it again.
        PlaybackState transport = PlaybackState.Stopped;
        /// <summary>True while the clip is advancing (Play). Pause/Stop set it false.</summary>
        public bool IsPlaying => transport == PlaybackState.Playing;
        public bool IsPaused => transport == PlaybackState.Paused;
        public bool IsStopped => transport == PlaybackState.Stopped;
        public PlaybackState State => transport;

        // ── Playback state ────────────────────────────────────────────────────────────────────────────
        IBrazeCacheSource source;   // resident (whole-clip) OR stream (ring), per the `streaming` toggle
        BrazeContainer playbackContainer;   // hidden container hosting the clip's fields + driving the slice renderer
        int curFrame = -1;
        float playTime;
        PlaybackState? transportAfterLoad;
        int frameAfterLoad = -1;
        bool wantAutoLoad;   // one-shot: auto-load once after enable, so a manual Unload isn't immediately re-loaded
        bool preparingSource;
        UnityWebRequest sourceRequest;
        string sourceTempPath;

        public int CurrentFrame => curFrame;
        /// <summary>Number of stored frames known before or after loading.</summary>
        public int FrameCount => source != null && source.FrameCount > 0
            ? source.FrameCount : cache != null ? Mathf.Max(0, cache.frameCount) : 0;
        /// <summary>Stored-frame rate known before or after loading.</summary>
        public float FramesPerSecond => source != null && source.Fps > 0f
            ? source.Fps : cache != null ? Mathf.Max(1e-3f, cache.fps) : 0f;
        public float Duration => FrameCount > 0 && FramesPerSecond > 0f ? FrameCount / FramesPerSecond : 0f;
        /// <summary>Time of the frame currently visible through the render host.</summary>
        public float CurrentTime => curFrame >= 0 && FramesPerSecond > 0f ? curFrame / FramesPerSecond : 0f;
        /// <summary>Current stored-frame position, where 0 is the first frame and 1 is the last.</summary>
        public float NormalizedTime => curFrame >= 0 && FrameCount > 1 ? curFrame / (float)(FrameCount - 1) : 0f;
        /// <summary>The hidden cache-only container that feeds the normal Braze volume renderer. Read-only.</summary>
        public BrazeContainer RenderHost => playbackContainer;
        public bool RendererReady => playbackContainer != null && playbackContainer.SolverReady;
        public bool IsVisible => playbackContainer != null && playbackContainer.SliceRenderReady;

        /// <summary>VDB players override this to request the adjacent-frame GPU reconstruction path. Normal baked
        /// caches remain byte-exact and do not allocate its working volumes.</summary>
        protected virtual BrazeVdbReconstructionSettings VdbReconstruction => default;

        protected virtual void OnEnable() => wantAutoLoad = autoLoad;
        protected virtual void OnDisable() => UnloadCache();

        protected virtual void OnValidate()
        {
            if (Status == CacheStatus.Ready && playbackContainer != null) ApplyLookToContainer();
        }

        // ── Public API ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reapply animated public settings to the active render host.</summary>
        public void OnBrazeAnimated() => OnValidate();

        /// <summary>Begin streaming the cache disk→GPU. Returns immediately; the work runs on a background thread + a
        /// main-thread upload. Watch <see cref="Status"/>/<see cref="IsReady"/> or <see cref="StatusChanged"/>.
        /// Idempotent — a no-op while already Loading or Ready.</summary>
        public void LoadCache()
        {
            if (Status == CacheStatus.Loading || Status == CacheStatus.Ready) return;
            if (cache == null || !cache.IsValid) { SetStatus(CacheStatus.Error, "No valid cache assigned."); return; }
            string path = ResolveBinaryPath();
            SetCurrentFrame(-1);
            playTime = 0f;
            SetStatus(CacheStatus.Loading);
            if (IsUri(path))
            {
                preparingSource = true;
                StartCoroutine(MaterializeAndOpen(path));
                return;
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus(CacheStatus.Error, $"Cache binary not found: '{path}'.");
                return;
            }
            OpenSource(path);
        }

        /// <summary>Release this player's GPU hold + renderer. The shared VRAM frees once the LAST player of this clip
        /// unloads (ref-counted). Status → Unloaded.</summary>
        public void UnloadCache()
        {
            transportAfterLoad = null;
            frameAfterLoad = -1;
            SetTransport(PlaybackState.Stopped);
            preparingSource = false;
            if (sourceRequest != null) { sourceRequest.Abort(); sourceRequest.Dispose(); sourceRequest = null; }
            if (!string.IsNullOrEmpty(sourceTempPath))
            {
                try { if (File.Exists(sourceTempPath)) File.Delete(sourceTempPath); } catch { }
                sourceTempPath = null;
            }
            DestroyPlaybackContainer();
            if (source != null) { source.Release(); source = null; }
            SetCurrentFrame(-1);
            SetStatus(CacheStatus.Unloaded);
        }

        /// <summary>Start (or resume) advancing the clip and make it visible. If needed, loading starts automatically
        /// and playback begins as soon as the cache is ready.</summary>
        [BrazeTimelineEvent("PLAYBACK/Play")]
        public void Play()
        {
            if (Status == CacheStatus.Ready)
            {
                SetTransport(PlaybackState.Playing);
                return;
            }
            if (cache == null || !cache.IsValid) return;
            if (Status == CacheStatus.Error) UnloadCache();
            transportAfterLoad = PlaybackState.Playing;
            if (Status == CacheStatus.Unloaded) LoadCache();
        }

        /// <summary>Hold on the current frame (stays visible, frozen).</summary>
        [BrazeTimelineEvent("PLAYBACK/Pause")]
        public void Pause()
        {
            if (Status == CacheStatus.Ready)
            {
                if (curFrame < 0) SeekFrame(0);
                else SetTransport(PlaybackState.Paused);
                return;
            }
            if (cache == null || !cache.IsValid) return;
            if (Status == CacheStatus.Error) UnloadCache();
            frameAfterLoad = Mathf.Clamp(frameAfterLoad >= 0 ? frameAfterLoad : 0, 0, Mathf.Max(0, FrameCount - 1));
            transportAfterLoad = PlaybackState.Paused;
            if (Status == CacheStatus.Unloaded) LoadCache();
        }

        /// <summary>Stop: hide the volume and rewind to the start. The cache stays loaded in VRAM, ready to Play again.</summary>
        [BrazeTimelineEvent("PLAYBACK/Stop")]
        public void Stop()
        {
            frameAfterLoad = -1;
            transportAfterLoad = Status == CacheStatus.Loading ? PlaybackState.Stopped : null;
            SetTransport(PlaybackState.Stopped);
            playTime = 0f;
            SetCurrentFrame(-1);
            if (playbackContainer != null) playbackContainer.renderVolume = false;
        }

        /// <summary>Rewind to the first stored frame and start playing.</summary>
        [BrazeTimelineEvent("PLAYBACK/Restart")]
        public void Restart()
        {
            if (Status != CacheStatus.Ready)
            {
                if (cache == null || !cache.IsValid) return;
                if (Status == CacheStatus.Error) UnloadCache();
                frameAfterLoad = 0;
                transportAfterLoad = PlaybackState.Playing;
                if (Status == CacheStatus.Unloaded) LoadCache();
                return;
            }
            playTime = -PhaseSeconds(FrameCount, FramesPerSecond);
            SetCurrentFrame(-1);
            SetTransport(PlaybackState.Playing);
        }

        /// <summary>Jump playback to a normalized [0,1] position. Loading starts automatically when needed.</summary>
        public void Seek01(float t)
        {
            if (FrameCount <= 0) return;
            SeekFrame(Mathf.RoundToInt(Mathf.Clamp01(t) * Mathf.Max(0, FrameCount - 1)));
        }

        /// <summary>Request one exact stored frame and hold it visibly. Streaming sources may complete on a later tick.</summary>
        public void SeekFrame(int frame)
        {
            if (Status != CacheStatus.Ready || source == null || source.FrameCount <= 0)
            {
                if (cache == null || !cache.IsValid || FrameCount <= 0) return;
                if (Status == CacheStatus.Error) UnloadCache();
                frameAfterLoad = Mathf.Clamp(frame, 0, FrameCount - 1);
                transportAfterLoad = PlaybackState.Paused;
                if (Status == CacheStatus.Unloaded) LoadCache();
                return;
            }
            frame = Mathf.Clamp(frame, 0, source.FrameCount - 1);
            float fps = Mathf.Max(1e-3f, source.Fps);
            playTime = frame / fps - PhaseSeconds(source.FrameCount, fps);
            SetTransport(PlaybackState.Paused);
            if (playbackContainer != null) playbackContainer.renderVolume = enableRendering;
            source.Prefetch(frame, loop);
            if (frame != curFrame && ScatterOne(frame)) SetCurrentFrame(frame);
        }

        /// <summary>Seek to the nearest stored frame at the supplied clip time.</summary>
        public void SeekSeconds(float seconds)
        {
            if (FrameCount <= 0 || FramesPerSecond <= 0f) return;
            SeekFrame(Mathf.RoundToInt(Mathf.Max(0f, seconds) * FramesPerSecond));
        }

        /// <summary>Pause and move by a signed number of stored frames.</summary>
        public void StepFrames(int delta)
        {
            if (FrameCount <= 0) return;
            int origin = curFrame >= 0 ? curFrame : frameAfterLoad >= 0 ? frameAfterLoad : 0;
            SeekFrame(Mathf.Clamp(origin + delta, 0, FrameCount - 1));
        }

#if UNITY_EDITOR
        int editorPreviewScatterCount;
        bool editorSequentialPreview;
        int editorPreviewDirection = 1;
        bool editorPreviewLoop;

        /// <summary>Drive the normal async load/upload state machine from an explicit Editor update loop.</summary>
        internal void EditorTick() => LateUpdate();

        /// <summary>Publish the requested Editor frame before Pump chooses which decoded slot to upload. Random seeks
        /// stay exact-only; an active transport supplies direction so streaming can build useful lookahead.</summary>
        internal void EditorRequestFrame(int frame, bool sequentialPlayback = false, int direction = 1,
                                         bool requestLoop = false)
        {
            editorSequentialPreview = sequentialPlayback;
            editorPreviewDirection = direction < 0 ? -1 : 1;
            editorPreviewLoop = requestLoop;
            if (Application.isPlaying || source == null) return;
            int count = source.FrameCount > 0 ? source.FrameCount : cache != null ? cache.frameCount : 0;
            if (count <= 0) return;
            frame = Mathf.Clamp(frame, 0, count - 1);
            if (source is BrazeCacheStream stream)
            {
                if (sequentialPlayback) stream.PrefetchForPlayback(frame, requestLoop, editorPreviewDirection);
                else stream.PrefetchForScrub(frame, requestLoop);
            }
            else source.Prefetch(frame, requestLoop);
        }

        /// <summary>Show one exact stored frame in Edit Mode. Streaming sources prioritize only this frame so a miss
        /// cannot queue speculative large-frame work ahead of the next cursor request.</summary>
        internal bool EditorPreviewFrame(int frame, bool sequentialPlayback = false, int direction = 1,
                                         bool requestLoop = false)
        {
            if (Application.isPlaying || Status != CacheStatus.Ready || source == null || source.FrameCount <= 0)
                return false;
            frame = Mathf.Clamp(frame, 0, source.FrameCount - 1);
            SetTransport(PlaybackState.Paused);
            // Keep LateUpdate's transport clock on the explicitly requested frame. Previously it remained at zero,
            // so every Editor tick scattered frame 0 before this method scattered the timeline frame — two full GPU
            // uploads (and two Render Detail rebuilds) per update, which could lock the Editor during playback.
            float fps = Mathf.Max(1e-3f, source.Fps);
            playTime = frame / fps - PhaseSeconds(source.FrameCount, fps);
            if (playbackContainer != null) playbackContainer.renderVolume = enableRendering;
            EditorRequestFrame(frame, sequentialPlayback, direction, requestLoop);
            // EditorTick already pumps once. A second pump here defeated the streaming upload budget and doubled
            // main-thread buffer uploads while the playhead was moving.
            if (frame != curFrame && ScatterOne(frame)) SetCurrentFrame(frame);
            return curFrame == frame;
        }

        internal BrazeContainer EditorPreviewContainer => playbackContainer;
        internal int EditorPreviewScatterCount => editorPreviewScatterCount;
#endif

        // ── Driver ──────────────────────────────────────────────────────────────────────────────────────
        protected virtual void LateUpdate()
        {
            // One-shot auto-load (after enable, once the cache is valid). A manual Unload won't re-trigger this.
            if (wantAutoLoad && Status == CacheStatus.Unloaded && cache != null && cache.IsValid)
            {
                wantAutoLoad = false;
                LoadCache();
            }

            source?.Pump();   // main-thread GPU work: resident's one-shot upload, or the stream's per-frame ring drain

            if (Status == CacheStatus.Loading)
            {
                if (source == null) { if (preparingSource) return; SetStatus(CacheStatus.Unloaded); return; }
                if (source.IsFailed)
                {
                    string err = source.Error;
                    source.Release(); source = null;
                    SetStatus(CacheStatus.Error, err);
                    return;
                }
                if (!source.IsReady) return;   // still opening/decoding on the worker — no freeze
                FinishLoad();
            }

            if (Status != CacheStatus.Ready) return;

            // Stopped = silent: data stays in VRAM but nothing happens (no scatter, no draw) — a loaded clip waiting
            // for its game cue. Only Playing/Paused are live.
            if (transport == PlaybackState.Stopped) { if (playbackContainer != null) playbackContainer.renderVolume = false; return; }

            // Master render toggle: when off we still advance + scatter (cache work runs) but the container isn't drawn
            // (renderVolume gates SliceRenderReady), so the FPS delta of toggling it is the volume's pure render cost.
            if (playbackContainer != null) playbackContainer.renderVolume = enableRendering;

            float previousPlayTime = playTime;
            float dtPlay = transport == PlaybackState.Playing ? Time.deltaTime * Mathf.Max(0f, playbackSpeed) * SpeedFactor() : 0f;
            playTime += dtPlay;
            if (loop && source.FrameCount > 0)
                playTime = Mathf.Repeat(playTime, source.FrameCount / Mathf.Max(1e-3f, source.Fps));
            else if (source.FrameCount > 0)
            {
                float fps = Mathf.Max(1e-3f, source.Fps);
                float phase = PhaseSeconds(source.FrameCount, fps);
                float last = Mathf.Max(0, source.FrameCount - 1) / fps;
                playTime = Mathf.Clamp(playTime + phase, 0f, last) - phase;
            }
            bool frameReady = ScatterCurrentFrame();
            // A bounded stream cannot cancel a large frame decode. Letting its clock continue during a miss makes every
            // completed frame stale and can create a permanent chase. Buffer at the last presented time instead; resident
            // playback is unchanged, while an undersupplied stream slows gracefully and resumes as soon as data arrives.
            if (ShouldHoldPlaybackClockForTests(frameReady, transport == PlaybackState.Playing,
                                                source is BrazeCacheStream))
                playTime = previousPlayTime;
            RefreshCurrentFrameReconstruction();
            // The slice render feature draws the current render-ready cache field.
        }

        // Resolve the stored frame(s) for the current play time. Motion smoothing floors to the KEY frame and
        // blends toward its neighbour with a single scatter-time compute pass (the raymarch stays one-state-tap —
        // this is deliberately NOT the old per-sample two-volume interpolation); exact playback rounds and snaps.
        bool ScatterCurrentFrame()
        {
            int frameCount = source.FrameCount;
            if (frameCount <= 0) return false;
            float fps = source.Fps;
            float phaseSec = PhaseSeconds(frameCount, fps);   // per-instance desync offset
            float pos = (playTime + phaseSec) * fps;
            bool smooth = motionSmoothing && frameCount > 1 && source.HasVelocity && playbackContainer != null;
            int f = smooth ? Mathf.FloorToInt(pos) : Mathf.RoundToInt(pos);
            float blend = smooth ? Mathf.Clamp01(pos - Mathf.Floor(pos)) : 0f;
            f = loop ? Mod(f, frameCount) : Mathf.Clamp(f, 0, frameCount - 1);
            if (smooth && !loop && (pos <= 0f || pos >= frameCount - 1)) blend = 0f;   // clamped ends hold exactly
#if UNITY_EDITOR
            // Export Studio sets an exact scrub request before driving this otherwise-normal LateUpdate. Reissuing the
            // same frame as a forward-playback request changed the stream generation every Editor tick; a large
            // in-flight decode was then discarded forever while tiny frames happened to win the timing race.
            if (ShouldUseScrubPrefetch(Application.isPlaying, source is BrazeCacheStream, editorSequentialPreview))
                ((BrazeCacheStream)source).PrefetchForScrub(f, editorPreviewLoop);
            else if (!Application.isPlaying && source is BrazeCacheStream editorStream)
                editorStream.PrefetchForPlayback(f, editorPreviewLoop, editorPreviewDirection);
            else
#endif
                source.Prefetch(f, loop);
            if (smooth)
            {
                // Cap smoothing at ~60 blend updates/sec: quantize the blend position to sub-steps of the stored
                // interval, so faster display rates land on the same sub-step and early-out in the container.
                int subSteps = Mathf.Max(1, Mathf.RoundToInt(60f / Mathf.Max(1f, fps)));
                blend = Mathf.Min(Mathf.Floor(blend * subSteps), subSteps - 1) / subSteps;
                // A missing streamed neighbour holds the key frame instead of blending into garbage.
                int next = f + 1 < frameCount ? f + 1 : loop ? 0 : -1;
                if (!playbackContainer.UploadCacheFrameBlended(source, f, next, blend)) return false;
#if UNITY_EDITOR
                if (!Application.isPlaying && playbackContainer.CacheBlendDisplayChanged)
                    playbackContainer.RefreshCachePlaybackRenderer();
#endif
                if (f != curFrame) SetCurrentFrame(f);
                return true;
            }
            // Streaming misses hold the previous frame and retry next update instead of scattering uninitialized data.
            if (f == curFrame) return true;
            if (!ScatterOne(f)) return false;
            SetCurrentFrame(f);
            return true;
        }

        void RefreshCurrentFrameReconstruction()
        {
            BrazeVdbReconstructionSettings settings = VdbReconstruction.Validated();
            if (!settings.enabled || curFrame < 0 || source == null || source.HasVelocity || playbackContainer == null)
                return;
            ResolveMotionNeighbour(curFrame, out int neighbour, out int direction);
            if (neighbour >= 0) source.Prefetch(neighbour, loop);
            playbackContainer.RefreshVdbPlaybackReconstruction(source, curFrame, neighbour, direction);
        }

        void ResolveMotionNeighbour(int frame, out int neighbour, out int direction)
        {
            neighbour = -1;
            direction = 1;
            int count = source != null ? source.FrameCount : FrameCount;
            if (count < 2) return;
            if (frame + 1 < count) { neighbour = frame + 1; return; }
            if (loop) { neighbour = 0; return; }
            neighbour = Mathf.Max(0, frame - 1);
            direction = -1;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;

        internal static bool ShouldHoldPlaybackClockForTests(bool frameReady, bool playing, bool streamingSource)
            => !frameReady && playing && streamingSource;

#if UNITY_EDITOR
        internal static bool ShouldUseScrubPrefetch(bool applicationIsPlaying, bool streamingSource,
                                                     bool sequentialPreview = false)
            => !applicationIsPlaying && streamingSource && !sequentialPreview;
#endif

        // Stand up the hidden playback container once the resident is GPU-ready (BaseRes is known now).
        void FinishLoad()
        {
            try
            {
                playbackContainer = CreatePlaybackContainer(source.BaseRes, source.HasWarp, source.DetailFlow,
                                                            source.WarpRes, VdbReconstruction.Validated(),
                                                            source.HasVelocity);
                if (playbackContainer == null || !playbackContainer.SolverReady)
                    throw new InvalidOperationException("Cache playback solver could not be created" +
                        (playbackContainer != null ? $": {playbackContainer.ResourceStatus}" : "."));
                SetCurrentFrame(-1);
                playTime = 0f;
                SetStatus(CacheStatus.Ready);
                PlaybackState requestedTransport = transportAfterLoad ??
                                                   (playOnLoad ? PlaybackState.Playing : PlaybackState.Stopped);
                int requestedFrame = frameAfterLoad;
                transportAfterLoad = null;
                frameAfterLoad = -1;
                if (requestedFrame >= 0)
                {
                    SeekFrame(requestedFrame);
                    SetTransport(requestedTransport);
                    if (requestedTransport == PlaybackState.Stopped && playbackContainer != null)
                        playbackContainer.renderVolume = false;
                }
                else
                    SetTransport(requestedTransport);
            }
            catch (Exception e)
            {
                DestroyPlaybackContainer();
                source?.Release();
                source = null;
                SetStatus(CacheStatus.Error, $"Couldn't create cache playback host: {e.Message}");
            }
        }

        // Scatter one baked frame into the playback container's solver (false on a not-yet-resident streaming frame).
        bool ScatterOne(int frame)
        {
            int neighbour = -1, direction = 1;
            BrazeVdbReconstructionSettings reconstruction = VdbReconstruction.Validated();
            if (source != null && !source.HasVelocity && reconstruction.enabled)
                ResolveMotionNeighbour(frame, out neighbour, out direction);
            if (neighbour >= 0) source?.Prefetch(neighbour, loop);
            bool scattered = playbackContainer != null && playbackContainer.UploadCacheFrame(source, frame, neighbour, direction);
#if UNITY_EDITOR
            if (scattered && !Application.isPlaying)
            {
                editorPreviewScatterCount++;
                // ExecuteAlways is deliberately off on BrazeContainer. In Edit Mode its normal LateUpdate therefore
                // never rebuilds the live renderer's shadow/sculpt support textures after a cache scatter. Keep this
                // beside the scatter so both immediate and asynchronously-completed preview frames refresh the look.
                playbackContainer?.RefreshCachePlaybackRenderer();
            }
#endif
            return scattered;
        }

        // Create the hidden container that hosts the clip's fields. Parented to this player so it follows our transform
        // (position/rotation/scale). Configured as a cache host (no emitter warning, no frame-clock governance) BEFORE it
        // is enabled, then put into cache-playback mode so its solver rebuilds at the baked resolution with the sim
        // suspended. Once frames are uploaded, BrazeRenderFeature's live slice pass draws it automatically.
        BrazeContainer CreatePlaybackContainer(Vector3Int baseRes, bool hasWarp, bool detailFlow, Vector3Int warpRes,
                                               BrazeVdbReconstructionSettings reconstruction, bool hasVelocity)
        {
            var go = new GameObject("BrazeFX Cache Volume") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.transform.SetParent(transform, false);
                go.SetActive(false);                              // configure BEFORE OnEnable runs
                var c = go.AddComponent<BrazeContainer>();
                c.ConfigureAsCacheHost(baseRes, hasWarp, detailFlow, warpRes, reconstruction, hasVelocity);
                c.size = cache != null && cache.worldSizeAtBake.sqrMagnitude > 1e-8f ? cache.worldSizeAtBake : Vector3.one;
                c.renderVolume = false;                           // loaded but SILENT until Play (no stray frame)
                ApplyLookTo(c);
                go.SetActive(true);                               // OnEnable builds one playback-only solver
#if UNITY_EDITOR
                // BrazeContainer intentionally is not ExecuteAlways, so activating a runtime-created host in Edit
                // Mode does not run its normal OnEnable resource path. Export Studio must enter playback explicitly.
                if (!Application.isPlaying)
                    c.BeginCachePlayback(baseRes, hasWarp, detailFlow, warpRes, reconstruction, hasVelocity);
#endif
                return c;
            }
            catch
            {
                if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
                throw;
            }
        }

        void DestroyPlaybackContainer()
        {
            if (playbackContainer == null) return;
            GameObject go = playbackContainer.gameObject;
            playbackContainer = null;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        // Push the complete portable look onto the hidden container. BrazeContainer owns the capture/apply inverse so
        // cache and live rendering cannot drift as new slice controls are added.
        void ApplyLookTo(BrazeContainer c)
        {
            if (c == null) return;
            var look = ResolveLook();
            c.ApplyRenderSettings(look);
            c.ConfigureVdbPlaybackReconstruction(VdbReconstruction.Validated());
            c.ConfigureCachePlaybackQuality(realtimeMode, realtimeMaxSteps, realtimeSamplesPerVoxel,
                                            realtimeMaxRaymarchHeight);
            c.sun = keyLight;
            if (keyLight != null && keyLight.type == LightType.Directional)
                c.ConfigureCacheKeyLight(true, -keyLight.transform.forward,
                                         keyLight.color.linear * Mathf.Max(0f, keyLight.intensity));
            else if (cache != null && cache.hasBakedKeyLight)
                c.ConfigureCacheKeyLight(true, cache.bakedKeyLightDirection, cache.bakedKeyLightColor);
            else
                c.ConfigureCacheKeyLight(false, Vector3.up, Color.white);
        }

        void ApplyLookToContainer()
        {
            ApplyLookTo(playbackContainer);
            // Re-scatter the held final-look frame so appearance overrides land immediately while paused.
            // Compatibility DetailFlow data may also rebuild its warp during that scatter.
            SetCurrentFrame(-1);
        }

        /// <summary>Reapply public look/key-light fields after changing them from runtime code.</summary>
        public void RefreshLook() => ApplyLookToContainer();

        void SetStatus(CacheStatus s, string msg = null)
        {
            StatusMessage = msg;
            if (Status == s) return;
            Status = s;
            StatusChanged?.Invoke(s);
        }

        void SetTransport(PlaybackState value)
        {
            if (transport == value) return;
            transport = value;
            PlaybackStateChanged?.Invoke(value);
        }

        void SetCurrentFrame(int value)
        {
            if (curFrame == value) return;
            curFrame = value;
            FrameChanged?.Invoke(value);
        }

        BrazeRenderSettings ResolveLook()
            => overrideLook ? lookOverride : (cache != null && cache.hasBakedLook ? cache.bakedLook : BrazeRenderSettings.Default);

        // ── Per-instance variation (stable per GameObject so duplicates desync but don't jitter frame-to-frame) ──
        float PhaseSeconds(int frameCount, float fps)
        {
            float p = phaseOffset01;
            if (randomizePhase) p += StableRand01(0);
            return Mathf.Repeat(p, 1f) * frameCount / Mathf.Max(1e-3f, fps);
        }

        float SpeedFactor()
            => speedVariance <= 0f ? 1f : 1f + (StableRand01(1) * 2f - 1f) * speedVariance;

        float StableRand01(int salt)
        {
            unchecked
            {
                int h = this.BrazeInstanceId() * 73856093 ^ (salt * 19349663);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h >> 8) & 0xFFFFFF) / (float)0x1000000;
            }
        }

        string ResolveBinaryPath()
        {
            if (cache == null || string.IsNullOrEmpty(cache.binaryFile)) return null;
            if (Path.IsPathRooted(cache.binaryFile)) return cache.binaryFile;   // external (out-of-project) cache
#if UNITY_EDITOR
            string assetPath = AssetDatabase.GetAssetPath(cache);
            if (!string.IsNullOrEmpty(assetPath))
                return Path.Combine(Path.GetDirectoryName(assetPath), cache.binaryFile);
#endif
            string buildFile = string.IsNullOrEmpty(cache.buildBinaryFile) ? cache.binaryFile : cache.buildBinaryFile;
            string root = Application.streamingAssetsPath.TrimEnd('/', '\\');
            return root + "/" + buildFile.Replace('\\', '/');
        }

        void OpenSource(string localPath)
        {
            try
            {
                source = streaming
                    ? (IBrazeCacheSource)new BrazeCacheStream(localPath)
                    : BrazeCacheResident.Acquire(localPath, dedupResident);
                preparingSource = false;
            }
            catch (Exception e)
            {
                preparingSource = false;
                SetStatus(CacheStatus.Error, e.Message);
            }
        }

        IEnumerator MaterializeAndOpen(string uri)
        {
            string local;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "BrazeFXCaches");
                Directory.CreateDirectory(dir);
                string key = StablePathHash(uri + "|" + cache.buildFingerprint + "|" + cache.binaryBytes + "|" +
                                            cache.frameCount + "|" + cache.baseRes + "|" + cache.warpRes + "|" +
                                            Application.version);
                local = Path.Combine(dir, key + ".brazecache");
                if (File.Exists(local) && (cache.binaryBytes <= 0 || new FileInfo(local).Length == cache.binaryBytes))
                {
                    preparingSource = false;
                    if (Status == CacheStatus.Loading) OpenSource(local);
                    yield break;
                }
                sourceTempPath = local + ".tmp-" + Guid.NewGuid().ToString("N");
            }
            catch (Exception e)
            {
                preparingSource = false;
                SetStatus(CacheStatus.Error, $"Couldn't prepare the local cache copy: {e.Message}");
                yield break;
            }

            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(uri);
                sourceRequest = request;
                request.downloadHandler.Dispose();
                request.downloadHandler = new DownloadHandlerFile(sourceTempPath);
            }
            catch (Exception e)
            {
                sourceRequest?.Dispose();
                sourceRequest = null;
                preparingSource = false;
                try { if (File.Exists(sourceTempPath)) File.Delete(sourceTempPath); } catch { }
                sourceTempPath = null;
                SetStatus(CacheStatus.Error, $"Couldn't start cache extraction: {e.Message}");
                yield break;
            }
            UnityWebRequestAsyncOperation operation;
            try { operation = request.SendWebRequest(); }
            catch (Exception e)
            {
                if (sourceRequest == request) sourceRequest = null;
                request.Dispose();
                preparingSource = false;
                try { if (File.Exists(sourceTempPath)) File.Delete(sourceTempPath); } catch { }
                sourceTempPath = null;
                SetStatus(CacheStatus.Error, $"Couldn't start cache extraction: {e.Message}");
                yield break;
            }
            yield return operation;

            // UnloadCache aborts, disposes, and clears the shared request. Do not inspect a request that was cancelled
            // while this coroutine was suspended, and do not let an older coroutine interfere with a newer load.
            if (!preparingSource || sourceRequest != request) yield break;

            bool ok = request.result == UnityWebRequest.Result.Success;
            string error = request.error;
            request.Dispose();
            sourceRequest = null;

            if (!ok)
            {
                preparingSource = false;
                try { if (File.Exists(sourceTempPath)) File.Delete(sourceTempPath); } catch { }
                sourceTempPath = null;
                SetStatus(CacheStatus.Error, $"Couldn't extract cache from StreamingAssets: {error}");
                yield break;
            }

            try
            {
                if (File.Exists(local)) File.Delete(local);   // packaged source remains authoritative if this copy fails
                File.Move(sourceTempPath, local);
                sourceTempPath = null;
                OpenSource(local);
            }
            catch (Exception e)
            {
                preparingSource = false;
                try { if (!string.IsNullOrEmpty(sourceTempPath) && File.Exists(sourceTempPath)) File.Delete(sourceTempPath); }
                catch { }
                sourceTempPath = null;
                SetStatus(CacheStatus.Error, $"Couldn't publish extracted cache: {e.Message}");
            }
        }

        static bool IsUri(string path)
            => !string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) >= 0;

        static string StablePathHash(string value)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < value.Length; i++) { h ^= value[i]; h *= 1099511628211UL; }
                return h.ToString("x16");
            }
        }
    }

    /// <summary>Standalone player for caches baked directly from a live Braze Container.</summary>
    [AddComponentMenu("BrazeFX/Baking/Braze Cache Player", 11)]
    [DefaultExecutionOrder(-100)]
    public sealed class BrazeCachePlayer : BrazeVolumePlayer { }
}
