using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Bakes a <see cref="BrazeContainer"/>'s live sim to a <c>.brazecache</c> binary (the data a
    /// <see cref="BrazeCachePlayer"/> plays back). Split out of the old monolithic BrazeFluidCache: this component
    /// owns ONLY recording. Playback lives in <see cref="BrazeCachePlayer"/>.
    ///
    /// Capture happens DURING real play, so all of Unity's systems run first (animation, scripts, object lifecycles,
    /// the emitter's gameplay conditions) and the simulator reacts exactly as it would in gameplay. Driven by the
    /// editor (enter play → BeginRecording → exit play). Deterministic via Time.captureFramerate: every frame advances
    /// exactly 1/fps of game time, all systems in lockstep.
    /// </summary>
    [AddComponentMenu("BrazeFX/Baking/Braze Cache Baker", 10)]
    public sealed class BrazeCacheBaker : MonoBehaviour
    {
        public const float DefaultBakeCull = 0f;

        [Tooltip("The container to bake.")]
        public BrazeContainer container;

        [Header("Bake")]
        [Tooltip("Frames to SIMULATE at the container's sim rate (the capture duration). The stored frame count is " +
                 "this divided by Frame Skip.")]
        [Min(1)] public int bakeFrames = 120;
        // Bake rate is derived from the container's sim rate (CacheBakeFps), not a separate knob.
        [Tooltip("Sparse cull: a brick is stored only if its density or fire (burn) exceeds this. The default trims " +
                 "invisibly faint haze; 0 preserves every non-empty texel; higher values trade faint wisps for " +
                 "smaller files.")]
        [Range(0f, 0.1f)] public float bakeCull = 0.002f;
        [Tooltip("Store one of every N simulated frames. 2 (default) halves the cache size — playback advances at the " +
                 "stored rate, which most smoke/fire reads cleanly at. 1 stores every simulated frame (largest file).")]
        [Range(1, 4)] public int frameSkip = 2;
        // The cache stores the render-ready scalar channels (+ velocity). Playback reconstructs Render Detail from the
        // stored velocity, so no high-resolution detail texture is written. The lossless tiers remain half-bit-exact;
        // Realtime is an explicit per-brick 8-bit playback representation that cuts decode/upload bandwidth.
        public enum Storage
        {
            Fast,      // raw final-look half textures, no inflate → fastest load
            Compact,   // losslessly deflated final-look half textures → smaller file
            Precise,   // lossless Deflate archive → smallest file, slowest random/streamed decode
            Realtime,  // per-brick 8-bit fields/vectors + Deflate → smallest resident frames; opt-in quality tradeoff
        }
        // The Cache Baker is opinionated: game clips always use the Realtime representation (per-brick 8-bit
        // fields/vectors + reconstruction scales + deflate) — the smallest, best-streaming tier, dequantized on the
        // GPU scatter. The enum remains for cache-format compatibility and the lossless Compact Export Studio path.
        // Deliberately NOT serialized: there is no authored choice to persist, and stale serialized tiers from older
        // versions must not resurrect themselves.
        [NonSerialized] public Storage storage = Storage.Realtime;
        [Tooltip("Record raw simulation velocity in the cache. Playback reconstructs Render Detail from it, and it " +
                 "drives fluid-following particles and OpenVDB `vel` export. Turn OFF only for the smallest possible " +
                 "scalar-only clips (playback then renders the unwarped base field).")]
        public bool storeVelocity = true;
        [Tooltip("Also keep bricks that contain ONLY air motion (no visible smoke/fire). That halo is often larger " +
                 "than the smoke itself, so leaving this OFF substantially shrinks the cache. Render Detail never " +
                 "reads it; turn ON only when fluid-following particles must keep tracking through clear air, or " +
                 "for a complete OpenVDB vel grid.")]
        public bool velocityBounds = false;
        [Tooltip("Output folder for the bake. INSIDE Assets → the .asset + .brazecache sit together (portable, but " +
                 "Unity imports the binary). OUTSIDE Assets (e.g. a scratch/production drive) → only the small .asset " +
                 "goes in Assets and points at the external binary by absolute path — best for big caches.")]
        public string bakeFolder = "Assets/BrazeFX Caches";
        [Tooltip("Base file name for the baked .brazecache + .asset.")]
        public string bakeName = "FluidCache";

        // Set by the Editor bake driver for deterministic Export Studio capture. Kept non-serialized so the
        // recording path does not create another authored scene binding beside Export Studio's internal state.
        [NonSerialized] public BrazeTimeline captureTimeline;
        // Current Cache Baker clips and Export Studio masters are LEAN: scalar channels + velocity only, and
        // playback rebuilds Render Detail from velocity at the playing device's own scale. The flag remains as an
        // internal format-compatibility seam for prerelease/legacy native-detail cache tests and migration.
        [NonSerialized] public bool captureRenderDetail = false;

        // ── Recording (PLAY MODE) ─────────────────────────────────────────────────────────────────────
        // Capture the live sim DURING real play, so all of Unity's systems run first (animation, scripts,
        // object lifecycles, the emitter's gameplay conditions) and the simulator reacts exactly as it would
        // in gameplay. Driven by the editor (enter play → BeginRecording → exit play). Deterministic via
        // Time.captureFramerate: every frame advances exactly 1/fps of game time, all systems in lockstep.
        public bool IsRecording { get; private set; }
        public bool RecordingDone { get; private set; }
        public string RecordError { get; private set; }
        public bool RecordingSucceeded { get; private set; }
        public int RecordedFrames { get; private set; }
        public int ProcessedFrames { get; private set; }
        public int RecordTarget { get; private set; }
        public int WarmupTarget { get; private set; }
        /// <summary>Frames that will actually be written = ceil(RecordTarget / frame skip).</summary>
        public int StoredTarget { get; private set; }
        public int RecordTotalTarget => WarmupTarget + RecordTarget;
        public bool HasRecordedLook { get; private set; }
        public BrazeRenderSettings RecordedLook { get; private set; }
        public Vector3 RecordedKeyLightDirection { get; private set; }
        public Color RecordedKeyLightColor { get; private set; }

        BrazeFluidCacheFormat.Writer recWriter;
        uint[] recBi; ushort[] recBb;
        int prevCaptureFramerate;
        int recFrameSkip = 1;   // captured once at BeginRecording so a mid-bake inspector edit can't corrupt the clip
        string recFinalPath, recTempPath;

        void OnDisable() { if (IsRecording) AbortRecording(); }

        /// <summary>Begin capturing the live sim for <paramref name="frames"/> frames at <paramref name="fps"/> into
        /// <paramref name="binPath"/>. Optional warm-up frames advance the complete scene at the same deterministic
        /// fixed step but are deliberately not written. Call once, in play mode.</summary>
        public void BeginRecording(int frames, int fps, float cull, string binPath, int warmupFrames = 0)
        {
            if (IsRecording) return;
            RecordingDone = false; RecordingSucceeded = false; RecordError = null; RecordedFrames = 0; ProcessedFrames = 0;
            HasRecordedLook = false;
            RecordTarget = Mathf.Max(1, frames);
            WarmupTarget = Mathf.Max(0, warmupFrames);
            recFrameSkip = Mathf.Clamp(frameSkip, 1, 4);
            StoredTarget = (RecordTarget + recFrameSkip - 1) / recFrameSkip;
            StartCoroutine(RecordRoutine(RecordTarget, Mathf.Max(1, fps), cull, binPath, WarmupTarget));
        }

        IEnumerator RecordRoutine(int frames, int fps, float cull, string binPath, int warmupFrames)
        {
            // Entering Play can take one or more frames to build the solver. Do not let the authored Timeline run
            // ahead during that boot window; capture time must begin at zero on the same fixed clock as the sim.
            if (captureTimeline != null)
            {
                captureTimeline.Stop();
                captureTimeline.Scrub(0f);
                captureTimeline.Pause();
            }

            // Let the scene boot (Awake/Start/first emissions) and the container build its solver.
            int guard = 0;
            while ((container == null || !container.SolverReady) && guard++ < 240) yield return null;
            if (container == null || !container.SolverReady) { RecordError = "Container/solver not ready."; RecordingDone = true; yield break; }

            Vector3Int res = container.SolverRes;
            int brick = container.SolverBrickSize;

            // Store camera-independent renderer scalars plus velocity. Current workflows keep captureRenderDetail
            // false and rebuild Render Detail during playback; the legacy branch remains readable for v9 migration.
            Vector3Int warpRes = captureRenderDetail ? container.CacheFinalLookWarpRes : Vector3Int.zero;
            bool storesWarp = warpRes.x > 0 && warpRes.y > 0 && warpRes.z > 0;
            var flags = FinalLookFlags(storage);
            if (storeVelocity) flags |= BrazeFluidCacheFormat.CacheFlags.VelocityField;

            var header = new BrazeFluidCacheFormat.Header
            {
                baseRes = res, warpRes = storesWarp ? warpRes : Vector3Int.zero,
                // Decimated bakes advance the sim at the full rate but store every Nth frame; the header carries
                // the STORED rate so playback timing is exact without any knowledge of the skip.
                brickSize = brick, fps = fps / (float)recFrameSkip,
                worldSize = container.WorldSize(),
                flags = flags,
                channelMask = storesWarp || storeVelocity ? BrazeFluidCacheFormat.ChannelMaskFull : 0x07u,
            };
            recFinalPath = binPath;
            recTempPath = TemporaryCachePath(binPath);
            try { recWriter = new BrazeFluidCacheFormat.Writer(recTempPath, header); }
            catch (Exception e)
            {
                RecordError = $"Can't write temporary cache beside '{binPath}': {e.Message}";
                DeleteTemp();
                RecordingDone = true;
                yield break;
            }

            IsRecording = true;
            prevCaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = fps;   // deterministic fixed-step capture (all systems advance 1/fps per frame)
            if (captureTimeline != null)
            {
                captureTimeline.Scrub(0f);
                captureTimeline.FireEventsAt(0f);
                captureTimeline.Play();
            }

            int totalFrames = warmupFrames + frames;
            for (int f = 0; f < totalFrames; f++)
            {
                yield return new WaitForEndOfFrame();   // gameplay + sim for this frame are done
                if (container == null || !container.SolverReady) { RecordError = "Container lost mid-bake."; break; }
                ProcessedFrames = f + 1;
                if (f < warmupFrames) continue;
                // Frame decimation: the sim advances every frame; only every Nth is stored.
                if ((f - warmupFrames) % recFrameSkip != 0) continue;

                if (!HasRecordedLook)
                {
                    RecordedLook = container.CaptureRenderSettings();
                    RecordedKeyLightDirection = container.SliceMainLightDirection();
                    RecordedKeyLightColor = container.SliceMainLightColor();
                    HasRecordedLook = true;
                }

                try
                {
                    int n = container.PackCacheSparse(cull, out ComputeBuffer idxBuf, out ComputeBuffer blockBuf,
                                                      storeVelocity, captureRenderDetail, velocityBounds);
                    if (idxBuf == null || blockBuf == null) throw new InvalidOperationException("No cache pack buffers.");

                    int blockUints = n * brick * brick * brick * 3;
                    if (recBi == null || recBi.Length < n) Array.Resize(ref recBi, Mathf.NextPowerOfTwo(Mathf.Max(1, n)));
                    int blockShorts = blockUints * 2;
                    if (recBb == null || recBb.Length < blockShorts)
                        Array.Resize(ref recBb, Mathf.NextPowerOfTwo(Mathf.Max(1, blockShorts)));

                    if (n > 0)
                    {
                        var idxRequest = AsyncGPUReadback.Request(idxBuf, n * sizeof(uint), 0);
                        var blockRequest = AsyncGPUReadback.Request(blockBuf, blockUints * sizeof(uint), 0);
                        idxRequest.WaitForCompletion();
                        blockRequest.WaitForCompletion();
                        if (idxRequest.hasError || blockRequest.hasError)
                            throw new InvalidOperationException("GPU readback failed while retrieving sparse cache bricks.");

                        var idxData = idxRequest.GetData<uint>();
                        for (int i = 0; i < n; i++) recBi[i] = idxData[i];
                        var blockData = blockRequest.GetData<uint>();
                        for (int i = 0; i < blockUints; i++)
                        {
                            uint packed = blockData[i];
                            recBb[i * 2] = (ushort)(packed & 0xFFFFu);
                            recBb[i * 2 + 1] = (ushort)(packed >> 16);
                        }
                    }

                    recWriter.WriteFrame(n, recBi, recBb);
                    RecordedFrames = recWriter.FramesWritten;
                }
                catch (Exception e)
                {
                    RecordError = $"Frame {f - warmupFrames} failed: {e.Message}";
                    break;
                }
            }

            FinishRecording(string.IsNullOrEmpty(RecordError) && ProcessedFrames == totalFrames && RecordedFrames == StoredTarget);
        }

        internal static BrazeFluidCacheFormat.CacheFlags FinalLookFlags(Storage storage)
        {
            var flags = BrazeFluidCacheFormat.CacheFlags.RenderReady;
            switch (storage)
            {
                case Storage.Fast:
                    flags |= BrazeFluidCacheFormat.CacheFlags.RawResident;
                    break;
                case Storage.Compact:
                case Storage.Precise:
                    flags |= BrazeFluidCacheFormat.CacheFlags.Deflate;
                    break;
                case Storage.Realtime:
                    flags |= BrazeFluidCacheFormat.CacheFlags.Deflate |
                             BrazeFluidCacheFormat.CacheFlags.ScalarQuant;
                    break;
                default:
                    flags |= BrazeFluidCacheFormat.CacheFlags.Deflate;
                    break;
            }
            return flags;
        }

        void FinishRecording(bool publish)
        {
            try
            {
                if (recWriter != null)
                {
                    if (publish)
                    {
                        try { recWriter.Finish(); }
                        catch (Exception e) { RecordError = $"Couldn't finalize cache: {e.Message}"; publish = false; }
                    }
                    try { recWriter.Dispose(); }
                    catch (Exception e) { RecordError ??= $"Couldn't close cache: {e.Message}"; publish = false; }
                    recWriter = null;
                }

                if (publish)
                {
                    try
                    {
                        ValidateAndPublishCache(recTempPath, recFinalPath, RecordedFrames);
                        recTempPath = null;
                        RecordingSucceeded = true;
                    }
                    catch (Exception e)
                    {
                        RecordError = $"Couldn't publish cache atomically: {e.Message}";
                        RecordingSucceeded = false;
                    }
                }
            }
            finally
            {
                if (!RecordingSucceeded) DeleteTemp();
                if (IsRecording) Time.captureFramerate = prevCaptureFramerate;
                IsRecording = false;
                RecordingDone = true;
            }
        }

        void AbortRecording()
        {
            StopAllCoroutines();
            if (string.IsNullOrEmpty(RecordError)) RecordError = "Recording aborted.";
            FinishRecording(false);
        }

        void DeleteTemp()
        {
            if (string.IsNullOrEmpty(recTempPath)) return;
            if (!TryDeleteTemporaryCache(recTempPath, out string error))
            {
                RecordError ??= $"Couldn't remove temporary cache '{recTempPath}': {error}";
                Debug.LogWarning($"[BrazeFX] {RecordError} The previous published cache is still untouched.");
            }
            recTempPath = null;
        }

        internal static bool TryDeleteTemporaryCache(string path, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                // Older visible .tmp-* names could be discovered by Unity while a long bake was running. Remove
                // only the exact sidecar belonging to our owned temporary file; new dot-prefixed stages are ignored.
                string metadata = path + ".meta";
                if (File.Exists(metadata)) File.Delete(metadata);
                return !File.Exists(path) && !File.Exists(metadata);
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>Stage beside the destination so publication remains a same-volume atomic rename. The leading dot
        /// keeps Unity's AssetDatabase from importing multi-gigabyte, incomplete streams created inside Assets.</summary>
        internal static string TemporaryCachePath(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentException("Cache destination is empty.", nameof(destination));
            string directory = Path.GetDirectoryName(destination);
            string file = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(file))
                throw new ArgumentException("Cache destination has no file name.", nameof(destination));
            string temporary = $".{file}.braze-stage-{Guid.NewGuid():N}";
            return string.IsNullOrEmpty(directory) ? temporary : Path.Combine(directory, temporary);
        }

        /// <summary>Read back a completed temporary stream before publishing it. Kept as one testable operation so a
        /// future writer/driver change cannot accidentally replace a good destination with an unfinished cache.</summary>
        internal static void ValidateAndPublishCache(string temp, string destination, int expectedFrames)
        {
            using (var check = new BrazeFluidCacheFormat.Reader(temp))
            {
                if (check.FrameCount != expectedFrames || check.FrameCount <= 0)
                    throw new InvalidDataException($"final header has {check.FrameCount} frames; expected {expectedFrames}");
                int[] counts = check.ReadFrameBrickCounts();
                for (int i = 0; i < counts.Length; i++)
                    if (counts[i] < 0) throw new InvalidDataException($"frame {i} has a negative brick count");
            }
            PublishTempAtomically(temp, destination);
        }

        static void PublishTempAtomically(string temp, string destination)
        {
            if (!File.Exists(destination)) { File.Move(temp, destination); return; }
            string backup = destination + ".replace-backup-" + Guid.NewGuid().ToString("N");
            bool replaced = false;
            try
            {
                File.Replace(temp, destination, backup);
                replaced = true;
            }
            finally
            {
                // Delete the backup only after confirmed success. If the filesystem reports a failed replacement
                // after creating the backup, retaining that old-good copy is safer than trying to infer its state.
                if (replaced)
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
        }
    }
}
