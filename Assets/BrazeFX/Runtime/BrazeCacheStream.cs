using System;
using System.Threading;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrazeFX
{
    /// <summary>
    /// STREAMING cache source: instead of uploading the whole clip to VRAM (<see cref="BrazeCacheResident"/>), keep a
    /// bounded RING of recently/soon-needed frames in two GPU buffers and prefetch the upcoming window from disk on a
    /// background thread. VRAM is O(ring) not O(frames), so long clips and many simultaneous players stay cheap, and
    /// playback starts almost immediately (only the first few frames load, not all 500).
    ///
    /// Frames are dynamically assigned to ring slots, at brick offset <c>slot · maxBricks</c> in the shared buffers, so
    /// wrapped lookahead frames cannot alias. The existing <c>CacheScatterBase</c> path reads it unchanged (just a
    /// different brick offset). The disk decode reuses the
    /// exact per-frame codec the resident path uses (<see cref="BrazeFluidCacheFormat.Reader.DecodeFrameInto"/>), so a
    /// streamed frame is byte-identical to the same frame loaded resident.
    ///
    /// Per-player (NOT pooled): each player has its own playhead → its own window, so sharing a ring across players
    /// wouldn't work. Dedup is unavailable while streaming (it's a cross-frame, whole-clip transform).
    ///
    /// Threading: a small byte-bounded set of background decode lanes fills independent CPU staging pairs while the
    /// main thread (<see cref="Pump"/>) uploads earlier frames to the GPU ring (Unity buffers are main-thread only).
    /// A small lock guards slot STATE only; disk decode and GPU upload run outside it.
    /// </summary>
    public sealed class BrazeCacheStream : IBrazeCacheSource
    {
        // Window the worker would like to keep ahead of the playhead. The ring is capped both by this frame window and
        // a byte budget; large frames may use fewer slots and advance them dynamically with the playhead.
        const int Lookahead = 8;
        const int ScrubLookahead = 0;   // Editor seeks decode only the frame under the cursor; neighbours made large
                                        // full-detail frames block the next seek behind stale, uninterruptible Deflate.
        const int RingMargin = 4;
        const long DefaultRingBudgetBytes = 256L * 1024L * 1024L;
        const int MaxDecodeLanes = 3;
        const int MaxUploadsPerPump = 2;
        const long UploadBudgetPerPumpBytes = 128L * 1024L * 1024L;

        enum Slot { Empty, Decoding, CpuReady, Uploading, Uploaded }
        internal enum RequestMode { Forward, Backward, Scrub }

        sealed class DecodeLane
        {
            public BrazeFluidCacheFormat.Reader Reader;
            public uint[] Indices;
            public uint[] Blocks;
            public byte[] Scratch;
            public Thread Worker;
            public int Slot = -1;
        }

        readonly string path;

        // Phase: Loading (opening + scanning) → Ready (ring allocated, worker running) | Failed.
        enum Phase { Loading, MetaReady, Ready, Failed }
        volatile Phase phase = Phase.Loading;

        public bool IsReady => phase == Phase.Ready;
        public bool IsFailed => phase == Phase.Failed;
        public string Error { get; private set; }

        public int FrameCount { get; private set; }
        public float Fps { get; private set; } = 30f;
        public Vector3Int BaseRes { get; private set; }
        public Vector3Int WarpRes { get; private set; }
        public bool HasWarp { get; private set; }
        public bool HasVelocity { get; private set; }
        public bool WarpQuant { get; private set; }
        public bool DetailFlow { get; private set; }
        public bool ScalarQuant { get; private set; }
        public bool Dedup => false;          // streaming never dedups
        public ComputeBuffer BlockId => null;

        public ComputeBuffer Idx { get; private set; }     // idxRing
        public ComputeBuffer Blocks { get; private set; }  // blockRing

        // Resident-layout metadata (read off-thread before the GPU ring is built).
        int uintsPerBrick, maxBricks, ring;

        // Ring slot state (guarded by `gate`).
        readonly object gate = new object();
        int[] slotFrame;     // which frame each slot is assigned to (-1 = none)
        Slot[] slotState;
        int[] slotBc;        // brick count of the frame in each slot
        int[] slotLane;      // staging owner while Decoding/CpuReady/Uploading (-1 once uploaded)
        DecodeLane[] lanes;

        volatile bool running;
        volatile bool released;
        int requestFrame;
        bool requestLoop;
        RequestMode requestMode;
        long decodedFrames, decodedBytes, decodeTicks;
        long uploadedFrames, uploadedBytes, uploadTicks;
        readonly int systemMemoryMB;
        readonly int processorCount;

        public BrazeCacheStream(string path)
        {
            this.path = path;
            // Unity system properties are main-thread APIs on some versions. Capture them before the scan task starts.
            systemMemoryMB = Math.Max(0, SystemInfo.systemMemorySize);
            processorCount = Math.Max(1, SystemInfo.processorCount);
            // Open + scan brick counts off the main thread (file open on a slow drive shouldn't stall Play).
            System.Threading.Tasks.Task.Run(OpenAndScan);
        }

        void OpenAndScan()
        {
            BrazeFluidCacheFormat.Reader opened = null;
            DecodeLane[] localLanes = null;
            bool published = false;
            try
            {
                opened = new BrazeFluidCacheFormat.Reader(path);
                var h = opened.header;
                int localFrameCount = h.frameCount;
                float localFps = Math.Max(1e-3f, h.fps);
                bool localHasWarp = BrazeFluidCacheFormat.HasRenderWarp(h);
                bool localHasVelocity = BrazeFluidCacheFormat.HasVelocity(h);
                bool localWarpQuant = BrazeFluidCacheFormat.HasStoredVectors(h.channelMask) &&
                                      (h.flags & BrazeFluidCacheFormat.CacheFlags.WarpQuant) != 0;
                bool localDetailFlow = localHasWarp &&
                                       (h.flags & BrazeFluidCacheFormat.CacheFlags.DetailFlow) != 0;
                bool localScalarQuant = (h.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;
                int localUintsPerBrick = BrazeFluidCacheFormat.ResidentUintsPerBrick(h);
                int[] localFrameBrickCount = opened.ReadFrameBrickCounts();
                int mb = 1;
                for (int f = 0; f < localFrameBrickCount.Length; f++)
                    if (localFrameBrickCount[f] > mb) mb = localFrameBrickCount[f];
                long perSlotBlockUints = (long)mb * localUintsPerBrick;
                if (perSlotBlockUints > int.MaxValue)
                    throw new InvalidOperationException("A streamed cache frame exceeds the managed staging-array limit; re-bake at a lower resolution/cull.");
                int localRing = ComputeRingSizeForTests(localFrameCount, mb, localUintsPerBrick,
                                                        DefaultRingBudgetBytes);

                // Allocate one maximum-frame staging pair per bounded decode lane HERE (background thread). A lane may
                // reuse its pair only after Pump uploads the frame it owns.
                var localSlotFrame = new int[localRing];
                var localSlotState = new Slot[localRing];
                var localSlotBc = new int[localRing];
                var localSlotLane = new int[localRing];
                for (int s = 0; s < localRing; s++)
                {
                    localSlotFrame[s] = -1;
                    localSlotLane[s] = -1;
                    localSlotState[s] = Slot.Empty;
                }
                long residentFrameBytes = checked((long)mb * (localUintsPerBrick + 1L) * sizeof(uint));
                int localLaneCount = ComputeDecodeLaneCountForTests(localRing, systemMemoryMB, processorCount,
                                                                     residentFrameBytes);
                localLanes = new DecodeLane[localLaneCount];
                for (int lane = 0; lane < localLaneCount; lane++)
                {
                    localLanes[lane] = new DecodeLane
                    {
                        Reader = lane == 0 ? opened : new BrazeFluidCacheFormat.Reader(path),
                        Indices = new uint[mb],
                        Blocks = new uint[(int)perSlotBlockUints]
                    };
                    if (lane == 0) opened = null;
                }

                lock (gate)
                {
                    if (released) return;
                    FrameCount = localFrameCount;
                    Fps = localFps;
                    BaseRes = h.baseRes;
                    WarpRes = h.warpRes;
                    HasWarp = localHasWarp;
                    HasVelocity = localHasVelocity;
                    WarpQuant = localWarpQuant;
                    DetailFlow = localDetailFlow;
                    ScalarQuant = localScalarQuant;
                    uintsPerBrick = localUintsPerBrick;
                    maxBricks = mb;
                    ring = localRing;
                    slotFrame = localSlotFrame;
                    slotState = localSlotState;
                    slotBc = localSlotBc;
                    slotLane = localSlotLane;
                    lanes = localLanes;
                    localLanes = null;
                    phase = Phase.MetaReady;   // main thread builds the GPU ring + starts the worker in Pump()
                    published = true;
                }
            }
            catch (Exception e)
            {
                if (!released)
                {
                    Error = e.Message;
                    phase = Phase.Failed;
                }
            }
            finally
            {
                opened?.Dispose();
                if (!published && localLanes != null)
                    for (int i = 0; i < localLanes.Length; i++)
                        try { localLanes[i]?.Reader?.Dispose(); } catch { /* best effort after failed scan */ }
            }
        }

        // ── IBrazeCacheSource ───────────────────────────────────────────────────────────────────────────
        /// <summary>Main thread: build the GPU ring once metadata is scanned, then drain decoded slots to the GPU.</summary>
        public void Pump()
        {
            if (released) return;
            if (phase == Phase.MetaReady)
            {
                AllocateRingAndStart();
                return;   // give the worker a tick before the first drain
            }
            if (phase != Phase.Ready) return;

            // Always allow one ready frame through, then use a byte budget for catch-up. Small frames can refill two
            // slots after a hiccup; two production-size SetData pairs never stack into an unbounded Editor stall.
            try
            {
                int uploads = 0;
                long bytesThisPump = 0;
                while (uploads < MaxUploadsPerPump)
                {
                    int f, bc, laneIndex;
                    int s;
                    lock (gate)
                    {
                        s = FindUploadCandidateLocked(bytesThisPump, uploads);
                        if (s < 0) break;
                        slotState[s] = Slot.Uploading;
                        f = slotFrame[s]; bc = slotBc[s]; laneIndex = slotLane[s];
                    }
                    DecodeLane lane = lanes[laneIndex];
                    long started = Stopwatch.GetTimestamp();
                    if (bc > 0)
                    {
                        int boff = s * maxBricks;
                        Idx.SetData(lane.Indices, 0, boff, bc);
                        Blocks.SetData(lane.Blocks, 0, boff * uintsPerBrick, bc * uintsPerBrick);
                    }
                    long frameBytes = ResidentBytes(bc, uintsPerBrick);
                    Interlocked.Increment(ref uploadedFrames);
                    Interlocked.Add(ref uploadedBytes, frameBytes);
                    Interlocked.Add(ref uploadTicks, Stopwatch.GetTimestamp() - started);
                    lock (gate)
                    {
                        if (slotFrame[s] == f && slotLane[s] == laneIndex)
                        {
                            slotState[s] = Slot.Uploaded;
                            slotLane[s] = -1;
                            lane.Slot = -1;
                        }
                        else slotState[s] = Slot.Empty;
                    }
                    bytesThisPump += frameBytes;
                    uploads++;
                }
            }
            catch (Exception e)
            {
                running = false;
                Idx?.Release(); Idx = null;
                Blocks?.Release(); Blocks = null;
                Error = $"Streaming GPU upload failed: {e.Message}";
                phase = Phase.Failed;
            }
        }

        void AllocateRingAndStart()
        {
            string readyLog = null;
            try
            {
                if (released) return;
                // Only the GPU buffers are created on the main thread (Unity requires it); the big CPU staging arrays
                // were already allocated off-thread in OpenAndScan, so this stays light (no Load freeze).
                long idxCount = (long)ring * maxBricks;
                long blockCount = idxCount * uintsPerBrick;
                long maxBytes = SystemInfo.maxGraphicsBufferSize;
                if (idxCount > int.MaxValue || blockCount > int.MaxValue ||
                    idxCount * sizeof(uint) > maxBytes || blockCount * sizeof(uint) > maxBytes)
                    throw new InvalidOperationException(
                        $"Streaming ring needs {blockCount * sizeof(uint) / (1024 * 1024)} MB in one GPU buffer, " +
                        "which exceeds this device's buffer limit. Re-bake smaller or raise Sparse Cull.");
                Idx = new ComputeBuffer((int)idxCount, sizeof(uint));
                Blocks = new ComputeBuffer((int)blockCount, sizeof(uint));
                running = true;
                // Publish Ready before the worker starts. If its first decode fails immediately, its Failed transition
                // must win instead of being overwritten by the main thread after Thread.Start returns.
                phase = Phase.Ready;
                long vramMB = (long)ring * maxBricks * (1 + uintsPerBrick) * 4 / (1024 * 1024);
                long cpuMB = (long)lanes.Length * maxBricks * (1 + uintsPerBrick) * 4 / (1024 * 1024);
                readyLog = $"[BrazeFX] Cache streaming: {FrameCount} frames, ring {ring} × {maxBricks:N0} bricks " +
                           $"→ {vramMB} MB VRAM + {lanes.Length} decode lane(s), {cpuMB} MB CPU staging " +
                           $"(+ bounded per-lane codec scratch).";
                for (int i = 0; i < lanes.Length; i++)
                {
                    int laneIndex = i;
                    lanes[i].Worker = new Thread(() => WorkerLoop(laneIndex))
                    {
                        IsBackground = true,
                        Name = $"BrazeCacheStream {i + 1}/{lanes.Length}"
                    };
                    lanes[i].Worker.Start();
                }
            }
            catch (Exception e)
            {
                running = false;
                Idx?.Release(); Idx = null;
                Blocks?.Release(); Blocks = null;
                Error = e.Message;
                phase = Phase.Failed;
                bool workerStarted = false;
                if (lanes != null)
                    for (int i = 0; i < lanes.Length; i++)
                        workerStarted |= lanes[i]?.Worker != null && lanes[i].Worker.IsAlive;
                if (!workerStarted) CleanupCpuState();
            }
            if (readyLog != null && phase == Phase.Ready) Debug.Log(readyLog);
        }

        public void Prefetch(int frame, bool loop)
            => SetRequest(frame, loop, RequestMode.Forward);

#if UNITY_EDITOR
        /// <summary>Editor scrubbing decodes only the requested frame. A changed request invalidates unpublished stale
        /// work so rapid playhead drags converge on the newest frame instead of speculatively decoding neighbours.</summary>
        internal void PrefetchForScrub(int frame, bool loop)
            => SetRequest(frame, loop, RequestMode.Scrub);

        /// <summary>Sequential Editor preview keeps a directional lookahead, unlike exact random scrubbing.</summary>
        internal void PrefetchForPlayback(int frame, bool loop, int direction)
            => SetRequest(frame, loop, direction < 0 ? RequestMode.Backward : RequestMode.Forward);
#endif

        void SetRequest(int frame, bool loop, RequestMode mode)
        {
            lock (gate)
            {
                if (released) return;
                bool changed = requestFrame != frame || requestLoop != loop || requestMode != mode;
                if (changed)
                {
                    requestFrame = frame;
                    requestLoop = loop;
                    requestMode = mode;
                    // Reclaim unpublished frames outside the new window. In-flight lanes cannot be interrupted safely;
                    // they are accepted on completion only if their frame is still useful to the latest request.
                    for (int s = 0; s < ring; s++)
                    {
                        if (slotState[s] != Slot.CpuReady || IsRequestedLocked(slotFrame[s])) continue;
                        ReleaseSlotLaneLocked(s);
                        ClearSlotLocked(s);
                    }
                }
            }
        }

        public bool TryGetFrameSlice(int frame, out int brickOffset, out int brickCount)
        {
            brickOffset = 0; brickCount = 0;
            if (phase != Phase.Ready || frame < 0 || frame >= FrameCount) return false;
            lock (gate)
            {
                int s = FindFrameSlotLocked(frame);
                if (s < 0 || slotState[s] != Slot.Uploaded) return false;
                brickOffset = s * maxBricks;
                brickCount = slotBc[s];
            }
            return true;
        }

        // Each background lane owns one Reader + staging pair. It may decode while other lanes wait for/upload previous
        // frames, so disk/inflate work finally overlaps the main-thread SetData path and builds real lookahead headroom.
        void WorkerLoop(int laneIndex)
        {
            try
            {
                while (running)
                {
                    int f, s;
                    bool claimed;
                    lock (gate)
                        claimed = TryClaimNextFrameLocked(laneIndex, out s, out f);
                    if (!claimed) { Thread.Sleep(2); continue; }

                    DecodeLane lane = lanes[laneIndex];
                    int bc;
                    long started = Stopwatch.GetTimestamp();
                    try { bc = lane.Reader.DecodeFrameInto(f, lane.Indices, lane.Blocks, ref lane.Scratch); }
                    catch (Exception e)
                    {
                        if (!released) { Error = e.Message; phase = Phase.Failed; }
                        running = false;
                        break;
                    }
                    Interlocked.Increment(ref decodedFrames);
                    Interlocked.Add(ref decodedBytes, ResidentBytes(bc, uintsPerBrick));
                    Interlocked.Add(ref decodeTicks, Stopwatch.GetTimestamp() - started);

                    lock (gate)
                    {
                        if (slotFrame[s] == f && slotLane[s] == laneIndex)
                        {
                            if (running && IsRequestedLocked(f))
                            {
                                slotBc[s] = bc;
                                slotState[s] = Slot.CpuReady;
                            }
                            else
                            {
                                ClearSlotLocked(s);
                                lane.Slot = -1;
                            }
                        }
                    }
                }
            }
            finally { CleanupLane(laneIndex); }
        }

        int FindFrameSlotLocked(int frame)
        {
            for (int s = 0; s < ring; s++)
                if (slotFrame[s] == frame && slotState[s] != Slot.Empty) return s;
            return -1;
        }

        bool TryClaimNextFrameLocked(int laneIndex, out int slot, out int frame)
        {
            slot = -1;
            frame = -1;
            DecodeLane lane = lanes[laneIndex];
            if (lane.Slot >= 0) return false;
            int lookahead = requestMode == RequestMode.Scrub ? ScrubLookahead : Lookahead;
            for (int i = 0; i <= lookahead; i++)
            {
                int f = ScheduledFrameAtForTests(FrameCount, i, requestFrame, requestLoop, requestMode);
                if (f < 0)
                {
                    if (requestMode == RequestMode.Scrub) continue;
                    break;
                }
                if (FindFrameSlotLocked(f) >= 0) continue;
                int s = FindReusableSlotLocked();
                if (s < 0) return false;
                slotFrame[s] = f;
                slotBc[s] = 0;
                slotLane[s] = laneIndex;
                slotState[s] = Slot.Decoding;
                lane.Slot = s;
                slot = s;
                frame = f;
                return true;
            }
            return false;
        }

        int FindUploadCandidateLocked(long bytesAlreadyUploaded, int uploadsAlreadyMade)
        {
            int lookahead = requestMode == RequestMode.Scrub ? ScrubLookahead : Lookahead;
            for (int i = 0; i <= lookahead; i++)
            {
                int frame = ScheduledFrameAtForTests(FrameCount, i, requestFrame, requestLoop, requestMode);
                if (frame < 0) continue;
                int s = FindFrameSlotLocked(frame);
                if (s < 0 || slotState[s] != Slot.CpuReady) continue;
                long frameBytes = ResidentBytes(slotBc[s], uintsPerBrick);
                if (uploadsAlreadyMade > 0 && bytesAlreadyUploaded + frameBytes > UploadBudgetPerPumpBytes)
                    return -1;
                return s;
            }
            return -1;
        }

        int FindReusableSlotLocked()
        {
            for (int s = 0; s < ring; s++) if (slotState[s] == Slot.Empty) return s;
            for (int s = 0; s < ring; s++)
                if (slotState[s] == Slot.Uploaded && !IsRequestedLocked(slotFrame[s])) return s;
            return -1;
        }

        bool IsRequestedLocked(int frame)
        {
            int lookahead = requestMode == RequestMode.Scrub ? ScrubLookahead : Lookahead;
            for (int i = 0; i <= lookahead; i++)
            {
                int f = ScheduledFrameAtForTests(FrameCount, i, requestFrame, requestLoop, requestMode);
                if (f < 0)
                {
                    if (requestMode == RequestMode.Scrub) continue;
                    break;
                }
                if (f == frame) return true;
            }
            return false;
        }

        void ReleaseSlotLaneLocked(int slot)
        {
            int laneIndex = slotLane[slot];
            if (laneIndex >= 0 && laneIndex < lanes.Length && lanes[laneIndex].Slot == slot)
                lanes[laneIndex].Slot = -1;
            slotLane[slot] = -1;
        }

        void ClearSlotLocked(int slot)
        {
            slotFrame[slot] = -1;
            slotBc[slot] = 0;
            slotLane[slot] = -1;
            slotState[slot] = Slot.Empty;
        }

        static long ResidentBytes(int brickCount, int residentUintsPerBrick)
            => Math.Max(0L, (long)brickCount * (residentUintsPerBrick + 1L) * sizeof(uint));

        internal static int ScheduledFrameAtForTests(int frameCount, int i, int primary, bool looping,
                                                     RequestMode mode)
        {
            if (mode == RequestMode.Scrub) return ScrubFrameAtForTests(frameCount, i, primary, looping);
            return RequestedFrameAtForTests(frameCount, i, primary, looping,
                                            mode == RequestMode.Backward ? -1 : 1);
        }

        // Pure scheduling core used by focused Edit Mode tests. It deliberately has no ring-slot arithmetic: dynamic
        // ownership permits the last and first clip frames to coexist even when frame % ring would collide.
        internal static int RequestedFrameAtForTests(int frameCount, int i, int primary, bool looping,
                                                     int direction = 1)
        {
            if (frameCount <= 0) return -1;
            int f = primary + i * (direction < 0 ? -1 : 1);
            if (looping) return ((f % frameCount) + frameCount) % frameCount;
            return f >= 0 && f < frameCount ? f : -1;
        }

        internal static int ScrubFrameAtForTests(int frameCount, int i, int primary, bool looping)
        {
            if (frameCount <= 0) return -1;
            int offset = i == 0 ? 0 : ((i + 1) / 2) * (i % 2 == 1 ? 1 : -1);
            int f = primary + offset;
            if (looping) return ((f % frameCount) + frameCount) % frameCount;
            return f >= 0 && f < frameCount ? f : -1;
        }

        /// <summary>Size the GPU ring by bytes, not only by frame count. One frame is always permitted so callers get
        /// a useful device-limit error later when even the minimum ring cannot be allocated.</summary>
        internal static int ComputeRingSizeForTests(int frameCount, int maximumBricks, int residentUintsPerBrick,
                                                    long byteBudget)
        {
            int frameLimit = Math.Min(Math.Max(frameCount, 1), Lookahead + RingMargin);
            if (maximumBricks <= 0 || residentUintsPerBrick <= 0 || byteBudget <= 0) return 1;
            long bytesPerFrame;
            try { bytesPerFrame = checked((long)maximumBricks * (residentUintsPerBrick + 1L) * sizeof(uint)); }
            catch (OverflowException) { return 1; }
            if (bytesPerFrame <= 0) return 1;
            long budgetSlots = Math.Max(1L, byteBudget / bytesPerFrame);
            return (int)Math.Min(frameLimit, budgetSlots);
        }

        /// <summary>Each Compact lane can simultaneously retain resident staging, inflated scratch, and compressed
        /// input. Budget three maximum-frame payloads per lane; small systems stay single-lane and desktop authoring
        /// machines can overlap up to three decoders without recreating the old per-ring-slot gigabyte allocation.</summary>
        internal static int ComputeDecodeLaneCountForTests(int ringSize, int systemMemoryMegabytes,
                                                           int availableProcessors, long residentFrameBytes)
        {
            int concurrencyLimit = Math.Min(MaxDecodeLanes, Math.Max(1, availableProcessors / 2));
            concurrencyLimit = Math.Min(concurrencyLimit, Math.Max(1, ringSize));
            long memoryBudget = systemMemoryMegabytes >= 16384 ? 768L * 1024L * 1024L
                              : systemMemoryMegabytes >= 8192  ? 512L * 1024L * 1024L
                              :                                  256L * 1024L * 1024L;
            long perLanePeak;
            try { perLanePeak = checked(Math.Max(1L, residentFrameBytes) * 3L); }
            catch (OverflowException) { return 1; }
            int memoryLimit = (int)Math.Max(1L, Math.Min(MaxDecodeLanes, memoryBudget / perLanePeak));
            return Math.Max(1, Math.Min(concurrencyLimit, memoryLimit));
        }

        internal long DecodedFrameCount => Interlocked.Read(ref decodedFrames);
        internal long UploadedFrameCount => Interlocked.Read(ref uploadedFrames);
        internal double DecodeMilliseconds => TicksToMilliseconds(Interlocked.Read(ref decodeTicks));
        internal double UploadMilliseconds => TicksToMilliseconds(Interlocked.Read(ref uploadTicks));
        internal long DecodedResidentBytes => Interlocked.Read(ref decodedBytes);
        internal long UploadedResidentBytes => Interlocked.Read(ref uploadedBytes);

        static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        internal static int ScrubLookaheadForTests => ScrubLookahead;

        public void Release()
        {
            lock (gate)
            {
                if (released) return;
                released = true;
                running = false;
            }
            DecodeLane[] currentLanes = lanes;
            // One shared deadline keeps shutdown bounded even with multiple in-flight reads. Workers that outlive it
            // own their Reader/arrays and clean their lane in finally after the decode returns.
            bool stopped = true;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(350);
            if (currentLanes != null)
            {
                for (int i = 0; i < currentLanes.Length; i++)
                {
                    Thread worker = currentLanes[i]?.Worker;
                    if (worker == null || !worker.IsAlive) continue;
                    int remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining == 0 || !worker.Join(remaining)) stopped = false;
                }
            }

            // GPU buffers are touched only by the main thread (Pump), never the worker → always safe to release now.
            Idx?.Release(); Idx = null;
            Blocks?.Release(); Blocks = null;

            // If a decode outlives the bounded join, its finally block owns cleanup. This avoids both use-after-dispose
            // and the old file-handle/large-array leak on slow external drives.
            if (stopped) CleanupCpuState();
        }

        void CleanupCpuState()
        {
            DecodeLane[] toDispose;
            lock (gate)
            {
                toDispose = lanes;
                lanes = null;
                slotFrame = slotBc = slotLane = null;
                slotState = null;
            }
            if (toDispose == null) return;
            for (int i = 0; i < toDispose.Length; i++) DisposeLane(toDispose[i]);
        }

        void CleanupLane(int laneIndex)
        {
            DecodeLane lane = null;
            BrazeFluidCacheFormat.Reader reader = null;
            lock (gate)
            {
                if (lanes == null || laneIndex < 0 || laneIndex >= lanes.Length) return;
                lane = lanes[laneIndex];
                if (lane == null) return;
                reader = lane.Reader;
                lane.Reader = null;
                lane.Indices = null;
                lane.Blocks = null;
                lane.Scratch = null;
            }
            try { reader?.Dispose(); } catch { /* best effort during shutdown */ }
        }

        static void DisposeLane(DecodeLane lane)
        {
            if (lane == null) return;
            BrazeFluidCacheFormat.Reader reader = lane.Reader;
            lane.Reader = null;
            lane.Indices = null;
            lane.Blocks = null;
            lane.Scratch = null;
            try { reader?.Dispose(); } catch { /* best effort during shutdown */ }
        }
    }
}
