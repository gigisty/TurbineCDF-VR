using System;
using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Ref-counted SHARED GPU residency for one baked <c>.brazecache</c>. The heavy data — the brick-index buffer
    /// and the (optionally deduped) voxel-block pool — is uploaded to VRAM exactly ONCE per clip; every
    /// <see cref="BrazeCachePlayer"/> that plays the same clip points at the same buffers. So duplicating a player
    /// across a scene costs no extra source VRAM, regardless of instance count.
    ///
    /// Keyed by (binary path + dedup choice). Acquire bumps the ref-count (loads on first use); Release drops it
    /// (frees on last). This is the source-data half of instanced playback — the per-instance transform/time and
    /// the eventual sparse render path live in the player.
    /// </summary>
    public sealed class BrazeCacheResident : IBrazeCacheSource
    {
        // Shared GPU data (uploaded once).
        public ComputeBuffer Idx { get; private set; }       // every frame's brick positions
        public ComputeBuffer Blocks { get; private set; }    // the (deduped) voxel-block pool
        public ComputeBuffer BlockId { get; private set; }   // dedup: per-brick-slot index into Blocks (null when off)

        // Per-frame slice + header-derived metadata (immutable once loaded).
        public int[] FrameStart { get; private set; }
        public int[] FrameBrickCount { get; private set; }
        public int FrameCount { get; private set; }
        public float Fps { get; private set; }
        public bool HasWarp { get; private set; }
        public bool HasVelocity { get; private set; }
        public bool WarpQuant { get; private set; }
        public bool DetailFlow { get; private set; }
        public bool ScalarQuant { get; private set; }
        public bool Dedup { get; private set; }
        public Vector3Int BaseRes { get; private set; }
        public Vector3Int WarpRes { get; private set; }

        // Load runs ASYNC: a background thread reads + decodes + dedups into CPU staging arrays (the slow part), then
        // the main thread uploads to the GPU (Unity buffers are main-thread only) via PumpUpload. So Play never freezes.
        public enum Phase { Loading, CpuReady, Uploading, Uploaded, Failed }
        volatile Phase phase = Phase.Loading;
        public bool IsReady => phase == Phase.Uploaded;
        public bool IsFailed => phase == Phase.Failed;
        public string Error { get; private set; }

        string key;
        int refCount;
        readonly object loadGate = new object();
        volatile bool released;

        // CPU staging — produced by the background Task, consumed once by the main-thread upload, then released.
        uint[] cpuIdx, cpuBlocks, cpuBlockId;
        int[] cpuStarts, cpuCounts;
        int cpuTotalBricks;
        long tDecode, tDedup;
        int uploadIdxOffset, uploadBlockOffset, uploadBlockIdOffset, lastPumpFrame = -1;
        System.Diagnostics.Stopwatch uploadWatch;
        const int UploadBudgetBytesPerFrame = 16 * 1024 * 1024;

        static readonly Dictionary<string, BrazeCacheResident> Pool = new Dictionary<string, BrazeCacheResident>();

        /// <summary>Acquire the shared resident for a clip and kick its background load (or join one already in flight).
        /// Never blocks. Poll <see cref="IsReady"/>/<see cref="IsFailed"/> and call <see cref="PumpUpload"/> each frame.
        /// Every Acquire must be matched by exactly one <see cref="Release"/>.</summary>
        public static BrazeCacheResident Acquire(string path, bool dedupResident)
        {
            string k = path + "|" + (dedupResident ? "d" : "r");
            if (Pool.TryGetValue(k, out var res)) { res.refCount++; return res; }

            res = new BrazeCacheResident { key = k, refCount = 1 };
            Pool[k] = res;
            res.BeginLoad(path, dedupResident);
            return res;
        }

        /// <summary>Drop one reference. Frees the shared GPU buffers when the LAST holder lets go.</summary>
        public void Release()
        {
            lock (loadGate)
            {
                if (released) return;
                if (--refCount > 0) return;
                released = true;
            }
            Pool.Remove(key);
            Free();
        }

        void BeginLoad(string path, bool dedupResident)
        {
            phase = Phase.Loading;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { LoadCpu(path, dedupResident); }
                catch (Exception e)
                {
                    lock (loadGate)
                    {
                        if (released) return;
                        Error = e.Message;
                        phase = Phase.Failed;
                    }
                }
            });
        }

        // Background thread: read + decode + dedup into CPU arrays. NO Unity GPU calls here (main-thread only).
        void LoadCpu(string path, bool dedupResident)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            uint[] allIdx, allBlocks; int[] starts, counts; int stride;
            float loadedFps;
            bool loadedHasWarp, loadedHasVelocity, loadedWarpQuant, loadedDetailFlow, loadedScalarQuant;
            int loadedFrameCount;
            Vector3Int loadedBaseRes, loadedWarpRes;
            using (var rd = new BrazeFluidCacheFormat.Reader(path))
            {
                rd.LoadAll(out allIdx, out allBlocks, out starts, out counts);
                loadedFps = Math.Max(1e-3f, rd.header.fps);
                loadedHasWarp = BrazeFluidCacheFormat.HasRenderWarp(rd.header);
                loadedHasVelocity = BrazeFluidCacheFormat.HasVelocity(rd.header);
                loadedWarpQuant = BrazeFluidCacheFormat.HasStoredVectors(rd.header.channelMask) &&
                                  (rd.header.flags & BrazeFluidCacheFormat.CacheFlags.WarpQuant) != 0;
                loadedDetailFlow = loadedHasWarp &&
                                   (rd.header.flags & BrazeFluidCacheFormat.CacheFlags.DetailFlow) != 0;
                loadedScalarQuant = (rd.header.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;
                stride = BrazeFluidCacheFormat.ResidentUintsPerBrick(rd.header);
                loadedFrameCount = rd.FrameCount;
                loadedBaseRes = rd.header.baseRes;
                loadedWarpRes = rd.header.warpRes;
            }
            long loadedDecodeMs = sw.ElapsedMilliseconds;

            int totalBricks = counts.Length > 0 ? starts[counts.Length - 1] + counts[counts.Length - 1] : 0;
            uint[] blockId = null;
            bool loadedDedup = false;
            if (dedupResident && totalBricks > 1 && DedupLooksWorthwhile(allBlocks, totalBricks, stride))
            {
                int unique = DedupBlocks(allBlocks, totalBricks, stride, out uint[] pool, out blockId);
                if (unique < totalBricks) { allBlocks = pool; loadedDedup = true; }
            }
            long loadedDedupMs = sw.ElapsedMilliseconds - loadedDecodeMs;

            // Publish the complete staging set atomically. If the last player released us while disk work was running,
            // keep the decoded arrays local so they are immediately collectable instead of resurrecting this resident.
            lock (loadGate)
            {
                if (released) return;
                Fps = loadedFps;
                HasWarp = loadedHasWarp;
                HasVelocity = loadedHasVelocity;
                WarpQuant = loadedWarpQuant;
                DetailFlow = loadedDetailFlow;
                ScalarQuant = loadedScalarQuant;
                Dedup = loadedDedup;
                FrameCount = loadedFrameCount;
                BaseRes = loadedBaseRes;
                WarpRes = loadedWarpRes;
                tDecode = loadedDecodeMs;
                tDedup = loadedDedupMs;
                cpuIdx = allIdx; cpuBlocks = allBlocks; cpuBlockId = blockId;
                cpuStarts = starts; cpuCounts = counts; cpuTotalBricks = totalBricks;
                phase = Phase.CpuReady;
            }
        }

        /// <summary>Main thread: once the background decode is done, upload the staged arrays to the GPU (once).
        /// Idempotent — safe to call every frame; only the CpuReady→Uploaded transition does work.</summary>
        public void PumpUpload()
        {
            if (released) return;
            // Time.frameCount does not advance in Edit Mode. Applying the runtime once-per-frame guard there would
            // permanently strand an Export Studio load if the worker became CpuReady after its first pump.
            if (Application.isPlaying && lastPumpFrame == Time.frameCount) return;
            lastPumpFrame = Time.frameCount;
            try
            {
                if (phase == Phase.CpuReady)
                {
                    Idx = new ComputeBuffer(Mathf.Max(1, cpuIdx.Length), sizeof(uint));
                    Blocks = new ComputeBuffer(Mathf.Max(1, cpuBlocks.Length), sizeof(uint));
                    if (Dedup) BlockId = new ComputeBuffer(Mathf.Max(1, cpuTotalBricks), sizeof(uint));
                    FrameStart = cpuStarts;
                    FrameBrickCount = cpuCounts;
                    uploadIdxOffset = uploadBlockOffset = uploadBlockIdOffset = 0;
                    uploadWatch = System.Diagnostics.Stopwatch.StartNew();
                    phase = Phase.Uploading;
                }
                if (phase != Phase.Uploading) return;

                int budget = UploadBudgetBytesPerFrame / sizeof(uint);
                UploadChunk(Idx, cpuIdx, ref uploadIdxOffset, ref budget);
                UploadChunk(Blocks, cpuBlocks, ref uploadBlockOffset, ref budget);
                if (Dedup) UploadChunk(BlockId, cpuBlockId, ref uploadBlockIdOffset, ref budget);
                bool done = uploadIdxOffset >= cpuIdx.Length && uploadBlockOffset >= cpuBlocks.Length &&
                            (!Dedup || uploadBlockIdOffset >= cpuBlockId.Length);
                if (!done) return;

                long tUpload = uploadWatch?.ElapsedMilliseconds ?? 0;
                Debug.Log($"[BrazeFX] Cache load: {FrameCount} frames, {cpuTotalBricks:N0} bricks, " +
                          $"{(long)cpuBlocks.Length * 4 / (1024 * 1024)} MB resident (SHARED) — " +
                          $"decode {tDecode}ms · dedup {tDedup}ms · staged upload {tUpload}ms.");
                cpuIdx = cpuBlocks = cpuBlockId = null; cpuStarts = cpuCounts = null;
                uploadWatch = null;
                phase = Phase.Uploaded;
            }
            catch (Exception e)
            {
                Error = $"GPU cache upload failed: {e.Message}";
                Free();
                phase = Phase.Failed;
            }
        }

        static void UploadChunk(ComputeBuffer dst, uint[] src, ref int offset, ref int budget)
        {
            if (dst == null || src == null || offset >= src.Length || budget <= 0) return;
            int count = Mathf.Min(src.Length - offset, budget);
            dst.SetData(src, offset, offset, count);
            offset += count;
            budget -= count;
        }

        // ── IBrazeCacheSource ───────────────────────────────────────────────────────────────────────────
        /// <summary>Main-thread pump = the one-shot resident upload (CpuReady→GPU). No-op once uploaded.</summary>
        public void Pump() => PumpUpload();

        /// <summary>No-op: the whole clip is already resident, nothing to prefetch.</summary>
        public void Prefetch(int frame, bool loop) { }

        /// <summary>The frame's contiguous brick span in the whole-cache buffers (always available once Ready).</summary>
        public bool TryGetFrameSlice(int frame, out int brickOffset, out int brickCount)
        {
            brickOffset = 0; brickCount = 0;
            if (!IsReady || FrameStart == null || frame < 0 || frame >= FrameCount) return false;
            brickOffset = FrameStart[frame];
            brickCount = FrameBrickCount[frame];
            return true;
        }

        void Free()
        {
            Idx?.Release(); Idx = null;
            Blocks?.Release(); Blocks = null;
            BlockId?.Release(); BlockId = null;
            FrameStart = FrameBrickCount = null;
            FrameCount = 0;
            cpuIdx = cpuBlocks = cpuBlockId = null; cpuStarts = cpuCounts = null;
        }

        // ── Resident brick dedup ──────────────────────────────────────────────────────────────────────
        // Identical voxel blocks (saturated cores, steady regions, low-entropy quantized bricks) recur across frames.
        // Hash each brick's `stride` uints and keep one copy in a pool; every brick slot gets a pool index. Lossless
        // (resident blocks are byte-for-byte the same, just shared). One-time CPU pass at load.
        // Cheap (~ms) sample probe: hash up to ~50k evenly-spaced bricks; if few repeat, the full pass isn't worth it.
        static bool DedupLooksWorthwhile(uint[] blocks, int totalBricks, int stride)
        {
            int target = Mathf.Min(totalBricks, 50000);
            int step = Mathf.Max(1, totalBricks / target);
            var seen = new HashSet<int>();
            int n = 0, dups = 0;
            for (int b = 0; b < totalBricks; b += step)
            {
                if (!seen.Add(HashBlock(blocks, b * stride, stride))) dups++;
                n++;
            }
            return n > 0 && (float)dups / n > 0.05f;   // >5% repeats in the sample → run the full dedup
        }

        static int DedupBlocks(uint[] blocks, int totalBricks, int stride, out uint[] pool, out uint[] blockId)
        {
            blockId = new uint[totalBricks];
            var byHash = new Dictionary<int, List<int>>(totalBricks);
            var firstOff = new List<int>(totalBricks);   // pool index → first occurrence's offset
            int unique = 0;
            for (int b = 0; b < totalBricks; b++)
            {
                int off = b * stride;
                int hash = HashBlock(blocks, off, stride);
                int found = -1;
                if (byHash.TryGetValue(hash, out var cands))
                {
                    for (int i = 0; i < cands.Count; i++)
                        if (BlocksEqual(blocks, off, blocks, firstOff[cands[i]], stride)) { found = cands[i]; break; }
                }
                else { cands = new List<int>(1); byHash[hash] = cands; }

                if (found < 0) { found = unique++; firstOff.Add(off); cands.Add(found); }
                blockId[b] = (uint)found;
            }
            if (unique == totalBricks) { pool = blocks; return unique; }   // nothing shared — caller keeps the original
            pool = new uint[unique * stride];
            for (int p = 0; p < unique; p++) Array.Copy(blocks, firstOff[p], pool, p * stride, stride);
            return unique;
        }

        static int HashBlock(uint[] a, int off, int n)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < n; i++) h = (h ^ (int)a[off + i]) * 16777619;
                return h;
            }
        }

        static bool BlocksEqual(uint[] a, int ao, uint[] b, int bo, int n)
        {
            for (int i = 0; i < n; i++) if (a[ao + i] != b[bo + i]) return false;
            return true;
        }
    }
}
