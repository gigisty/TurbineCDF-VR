using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Abstraction over a playable cache's GPU data so the renderer's scatter is identical whether the clip is fully
    /// RESIDENT (<see cref="BrazeCacheResident"/> — every frame uploaded once) or STREAMED frame-by-frame
    /// (<see cref="BrazeCacheStream"/> — a bounded ring, prefetched from disk). The player drives both through this
    /// interface: load (async) → <see cref="Pump"/> each frame → <see cref="Prefetch"/> around the playhead →
    /// <see cref="TryGetFrameSlice"/> to scatter.
    ///
    /// The scatter reads <see cref="Idx"/>/<see cref="Blocks"/> at a brick slice (offset+count): for the resident that's
    /// the frame's contiguous span in the whole-cache buffers; for the stream it's the frame's ring slot. Either way the
    /// per-brick block layout is the SAME (matches <c>CacheScatterBase</c>), so the GPU path never branches.
    /// </summary>
    public interface IBrazeCacheSource
    {
        bool IsReady { get; }
        bool IsFailed { get; }
        string Error { get; }

        int FrameCount { get; }
        float Fps { get; }
        Vector3Int BaseRes { get; }
        Vector3Int WarpRes { get; }

        bool HasWarp { get; }
        /// <summary>BASE-brick vector channels contain authoritative velocity in grid voxels/second. This is
        /// independent of Render Detail: a VDB cache can have velocity while <see cref="HasWarp"/> is false.</summary>
        bool HasVelocity { get; }
        bool WarpQuant { get; }
        /// <summary>Compatibility flag for explicitly-authored flow-anchor data that requests a Render Detail
        /// rebuild. Production final-look bakes leave this false and scatter their stored displacement directly.</summary>
        bool DetailFlow { get; }
        bool ScalarQuant { get; }
        bool Dedup { get; }

        ComputeBuffer Idx { get; }       // brick positions
        ComputeBuffer Blocks { get; }    // voxel-block pool
        ComputeBuffer BlockId { get; }   // dedup: per-slot index into Blocks (null when off / streaming)

        /// <summary>Main-thread per-frame pump. Resident: the one-shot CpuReady→GPU upload. Stream: drain freshly
        /// decoded frames into the GPU ring. Safe to call every frame.</summary>
        void Pump();

        /// <summary>Hint the current frame plus playback wrap semantics. A streaming source protects it and fills
        /// forward lookahead; no-op when the whole clip is already resident.</summary>
        void Prefetch(int frame, bool loop);

        /// <summary>Where frame <paramref name="frame"/>'s bricks live in <see cref="Idx"/>/<see cref="Blocks"/>.
        /// Returns false if the frame isn't on the GPU yet (streaming still loading it) — the caller should hold the
        /// previously scattered frame rather than scatter garbage.</summary>
        bool TryGetFrameSlice(int frame, out int brickOffset, out int brickCount);

        /// <summary>Release this holder's GPU data (ref-counted for the shared resident; direct for a per-player stream).</summary>
        void Release();
    }
}
