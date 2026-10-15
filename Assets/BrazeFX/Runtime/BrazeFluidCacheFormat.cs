using System;
using System.IO;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// On-disk format for the streamed BrazeFX fluid cache (<c>.brazecache</c>). SPARSE (active bricks only),
    /// HALF precision (raw 16-bit bits — GPU readback → file → GPU upload round-trips bit-exact). Version 9 stores the
    /// conditioned render-ready density + temperature + burn at the live renderer's base resolution and, when
    /// authored, the complete Render Detail texture at its own native (possibly 2×/4×) resolution. Playback scatters
    /// both textures directly to the renderer without simulation or velocity history.
    ///
    /// Production bakes contain final renderer inputs: the conditioned smoke/fire field and the already-baked Render
    /// Detail texture. Playback never regenerates or downsamples procedural detail. The historical
    /// <see cref="CacheFlags.DetailFlow"/> bit remains for explicitly-authored compatibility data only.
    ///
    /// Byte layout (v9):
    ///   [Header]  magic 'BRZC', version, baseRes(3×int), warpRes(3×int), brickSize, frameCount, fps,
    ///             worldSize(3×float), flags(uint), channelMask(uint), indexOffset(long)
    ///   [Frames]  frameCount × Frame
    ///   [Index]   frameCount × long  — byte offset of each frame's start (O(1) scrub seeks)
    ///
    ///   Frame (every codec starts with brickCount so the index pass can read it without decoding):
    ///     brickCount (int)
    ///     <body>   — encoded per <see cref="CacheFlags"/>:
    ///       • raw SoA (flags has no codec bit): brickIndices[bc]·uint, then blocks[bc × BaseBlockShorts]·ushort
    ///       • Deflate: rawLen(int), compLen(int), deflate( byte-plane-split(brickIndices ++ blocks) )
    ///
    ///   blocks ordering per brick: voxel-major, channel-minor → halfIndex = voxel·BaseChannels + channel.
    ///   brickIndex = bx + by·brickGrid.x + bz·brickGrid.x·brickGrid.y   (BASE occupancy grid).
    ///   A set high bit marks a Render Detail brick. Its remaining 31 bits use the same formula against warpRes.
    ///   Keeping both textures in one indexed stream preserves the resident/streaming fast path while allowing the
    ///   detail texture to retain its full authored resolution.
    ///
    /// flags + channelMask + frameCount + indexOffset are patched on <see cref="Writer.Finish"/> (written last),
    /// so a bake can stream frames to disk without knowing the total up front.
    /// </summary>
    public static class BrazeFluidCacheFormat
    {
        public const uint Magic = 0x435A5242;   // 'B','R','Z','C' little-endian
        public const int Version = 9;
        public const int DefaultBrickSize = 4;
        const int HeaderBytes = 72;
        public const uint WarpBrickBit = 0x80000000u;
        // Channels in the baker's GPU staging block, in order. The optional final three are vector storage. Their
        // meaning is explicit in the header: BASE bricks use raw simulation velocity when VelocityField is set;
        // high-bit-tagged bricks use the native Render Detail texture when warpRes is present. A cache may carry both.
        public const int BaseChannels = 6;      // density, temp, burn, vectorX, vectorY, vectorZ
        public const uint ChannelMaskFull = 0x3Fu;   // all six channels present (bits 0..5)

        /// <summary>The resident block contains xyz vector channels. This controls codec/stride only; inspect the
        /// header flags and warp resolution to decide whether those bytes are velocity, Render Detail, or both.</summary>
        public static bool HasStoredVectors(uint channelMask) => (channelMask & 0x38u) == 0x38u;

        /// <summary>Compatibility alias for callers that only need to know the resident payload layout. New playback
        /// code should use the header overload below so velocity-only VDB caches are not mistaken for Render Detail.</summary>
        public static bool HasRenderWarp(uint channelMask) => HasStoredVectors(channelMask);

        /// <summary>BASE-brick vector channels contain authoritative velocity in grid voxels/second.</summary>
        public static bool HasVelocity(in Header h)
            => HasStoredVectors(h.channelMask) && (h.flags & CacheFlags.VelocityField) != 0;

        /// <summary>High-bit-tagged native Render Detail bricks are present. A positive warp resolution is the
        /// unambiguous discriminator: velocity-only VDB caches deliberately keep it zero.</summary>
        public static bool HasRenderWarp(in Header h)
            => HasStoredVectors(h.channelMask) && h.warpRes.x > 0 && h.warpRes.y > 0 && h.warpRes.z > 0;

        /// <summary>Resident uints per brick for the full (half) layout: brickVox × 6 halfs, 2 halfs per uint.</summary>
        public static int FullUintsPerBrick(int brickSize) => brickSize * brickSize * brickSize * BaseChannels / 2;

        /// <summary>Resident uints per brick for the scalars-only layout (no render warp): brickVox × 3 halfs, 2/uint.</summary>
        public static int ScalarUintsPerBrick(int brickSize)
        { int vpb = brickSize * brickSize * brickSize; return (vpb * 3 + 1) / 2; }

        /// <summary>Resident uints per brick for whatever layout this header describes (the size of one block in the
        /// GPU pool). The single source of truth for the scatter stride; matches CacheScatterBase in HLSL.</summary>
        public static int ResidentUintsPerBrick(in Header h)
        {
            bool hasVel = HasStoredVectors(h.channelMask);
            bool velQuant = hasVel && (h.flags & CacheFlags.WarpQuant) != 0;
            bool scalarQuant = (h.flags & CacheFlags.ScalarQuant) != 0;
            if (scalarQuant) return ScalarQuantUintsPerBrick(h.brickSize, hasVel);
            return !hasVel ? ScalarUintsPerBrick(h.brickSize)
                 : velQuant ? WarpQuantUintsPerBrick(h.brickSize)
                 : FullUintsPerBrick(h.brickSize);
        }

        /// <summary>Resident uints per brick for the ScalarQuant layout (MUST match CacheScatterBase in HLSL): scalars
        /// (density/temp/burn) 8-bit + displacement 8-bit (when present), then the per-brick scales:
        ///   hasVel : [scalar bytes: vpb×3, 4/uint][vel bytes: vpb×3, 4/uint][3 scalar scales][1 vel scale]
        ///   !hasVel: [scalar bytes: vpb×3, 4/uint][3 scalar scales].
        /// Scalars get a per-channel max scale; displacement a single max-abs scale (dequantized on the GPU).</summary>
        public static int ScalarQuantUintsPerBrick(int brickSize, bool hasVel)
        {
            int vpb = brickSize * brickSize * brickSize;
            int scalarByteUints = (vpb * 3 + 3) / 4;
            if (!hasVel) return scalarByteUints + 3;                  // + 3 per-channel scalar scales
            int velByteUints = (vpb * 3 + 3) / 4;
            return scalarByteUints + velByteUints + 4;               // + 3 scalar scales + 1 vel scale
        }

        /// <summary>Resident uints per brick for the WarpQuant layout (MUST match CacheScatterBase in HLSL):
        ///   [scalar plane: brickVox × 3 halfs, 2/uint][warp plane: brickVox × 3 bytes, 4/uint][1 uint per-brick scale].
        /// Scalars stay half-exact; displacement is 8-bit signed about a per-brick max-abs scale.</summary>
        public static int WarpQuantUintsPerBrick(int brickSize)
        {
            int vpb = brickSize * brickSize * brickSize;
            int scalarUints = (vpb * 3 + 1) / 2;   // 3 scalar halfs/voxel, 2 halfs/uint (ceil)
            int velUints = (vpb * 3 + 3) / 4;       // 3 quantized bytes/voxel, 4 bytes/uint (ceil)
            return scalarUints + velUints + 1;      // + per-brick scale (float bits)
        }

        /// <summary>Codec / feature bits in the header's <c>flags</c>. None = raw SoA half (the v3 behaviour,
        /// renumbered). Each later tier flips one bit; readers branch on it, so a single Version hosts them all.</summary>
        [Flags]
        public enum CacheFlags : uint
        {
            None          = 0,
            Deflate       = 1u << 0,   // per-frame byte-plane deinterleave + DeflateStream (lossless, disk-only)
            WarpQuant     = 1u << 1,   // render-warp displacement 8-bit signed + per-brick scale
            BrickDedup    = 1u << 2,   // frames reference unique blocks by id (reserved)
            TemporalDelta = 1u << 3,   // keyframe + delta P-frames (reserved)
            RawResident   = 1u << 4,   // frame body is byte-identical to the resident GPU pool → load is a blit (no decode)
            ScalarQuant   = 1u << 5,   // density/temp/burn 8-bit per brick/channel plus scales; compact resident layout is dequantized by CacheScatterBase. LOSSY.
            RenderReady   = 1u << 6,   // scalars are the conditioned renderer field; playback must not rebuild/smooth them.
            DetailFlow    = 1u << 7,   // compatibility: channels carry a flow anchor and request BakeWarp reconstruction. Production bakes leave this off.
            VelocityField = 1u << 8,   // BASE-brick vector halfs carry raw simulation velocity (grid voxels/sec). Purely semantic: those halfs are zero padding otherwise, so layout and size are unchanged. Used for OpenVDB `vel` interchange.
        }

        public struct Header
        {
            public Vector3Int baseRes;
            /// <summary>Native live Render Detail texture resolution. Zero when Render Detail was disabled.</summary>
            public Vector3Int warpRes;
            public int brickSize;
            public int frameCount;
            public float fps;
            public Vector3 worldSize;
            public CacheFlags flags;     // codec / feature bits (patched on Finish)
            public uint channelMask;     // bit0..5 = density,temp,burn,warpX,warpY,warpZ present
            public long indexOffset;     // byte offset of the frame-index table (patched on Finish)

            /// <summary>Voxels in one base block (brickSize³).</summary>
            public int BaseBlockVoxels => brickSize * brickSize * brickSize;
            /// <summary>ushort count in one base block (× BaseChannels).</summary>
            public int BaseBlockShorts => BaseBlockVoxels * BaseChannels;

            public Vector3Int BrickGrid => new Vector3Int(
                Mathf.CeilToInt((float)baseRes.x / Mathf.Max(1, brickSize)),
                Mathf.CeilToInt((float)baseRes.y / Mathf.Max(1, brickSize)),
                Mathf.CeilToInt((float)baseRes.z / Mathf.Max(1, brickSize)));

            public Vector3Int WarpBrickGrid => new Vector3Int(
                Mathf.CeilToInt((float)warpRes.x / Mathf.Max(1, brickSize)),
                Mathf.CeilToInt((float)warpRes.y / Mathf.Max(1, brickSize)),
                Mathf.CeilToInt((float)warpRes.z / Mathf.Max(1, brickSize)));
        }

        static void WriteHeader(BinaryWriter w, in Header h)
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(h.baseRes.x); w.Write(h.baseRes.y); w.Write(h.baseRes.z);
            w.Write(h.warpRes.x); w.Write(h.warpRes.y); w.Write(h.warpRes.z);
            w.Write(h.brickSize);
            w.Write(h.frameCount);
            w.Write(h.fps);
            w.Write(h.worldSize.x); w.Write(h.worldSize.y); w.Write(h.worldSize.z);
            w.Write((uint)h.flags);
            w.Write(h.channelMask);
            w.Write(h.indexOffset);
        }

        static Header ReadHeader(BinaryReader r)
        {
            uint magic = r.ReadUInt32();
            if (magic != Magic) throw new InvalidDataException("[BrazeFX] Not a .brazecache file (bad magic).");
            int version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException($"[BrazeFX] .brazecache version {version} != {Version} (re-bake needed).");
            var h = new Header
            {
                baseRes = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                warpRes = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                brickSize = r.ReadInt32(),
                frameCount = r.ReadInt32(),
                fps = r.ReadSingle(),
                worldSize = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                flags = (CacheFlags)r.ReadUInt32(),
                channelMask = r.ReadUInt32(),
                indexOffset = r.ReadInt64(),
            };
            if (h.channelMask == 0) h.channelMask = ChannelMaskFull;
            if ((h.flags & CacheFlags.RenderReady) == 0)
                throw new InvalidDataException("[BrazeFX] Cache is not render-ready (re-bake with the current baker).");
            return h;
        }

        /// <summary>Streaming writer: write the header (placeholder counts), append frames, then Finish to
        /// patch the frame count + write the seek index. Dispose only closes the file: callers must explicitly
        /// Finish successful output so cancellation can never turn a partial stream into a publishable cache.</summary>
        public sealed class Writer : IDisposable
        {
            readonly FileStream fs;
            readonly BinaryWriter w;
            Header header;
            readonly System.Collections.Generic.List<long> frameOffsets = new();
            bool finished;

            public Writer(string path, Header h)
            {
                header = h;
                if (header.channelMask == 0) header.channelMask = ChannelMaskFull;
                // Programmatically-authored scale-1 caches may omit warpRes. Production baking always supplies the
                // live texture resolution; treating an omitted value as baseRes keeps the writer convenient without
                // permitting an ambiguous file on disk.
                if (HasStoredVectors(header.channelMask) && !HasVelocity(header) &&
                    (header.warpRes.x <= 0 || header.warpRes.y <= 0 || header.warpRes.z <= 0))
                    header.warpRes = header.baseRes;
                header.frameCount = 0;
                header.indexOffset = 0;
                fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                w = new BinaryWriter(fs);
                WriteHeader(w, header);   // placeholder; patched in Finish
            }

            /// <summary>Append one frame. Arrays are tightly packed: brickIndices[brickCount];
            /// baseBlocks[brickCount × BaseBlockShorts].</summary>
            public void WriteFrame(int brickCount, uint[] brickIndices, ushort[] baseBlocks)
            {
                if (finished) throw new InvalidOperationException("Writer already finished.");
                if (brickCount < 0) throw new ArgumentOutOfRangeException(nameof(brickCount));
                if (brickIndices == null || brickIndices.Length < brickCount)
                    throw new ArgumentException($"Brick-index source needs {brickCount} entries.", nameof(brickIndices));
                int requiredHalfs = checked(brickCount * header.BaseBlockShorts);
                if (baseBlocks == null || baseBlocks.Length < requiredHalfs)
                    throw new ArgumentException($"Brick source needs {requiredHalfs} half values.", nameof(baseBlocks));
                frameOffsets.Add(fs.Position);
                w.Write(brickCount);   // first int of every frame, codec-agnostic (the index pass reads it)

                bool hasVel = HasStoredVectors(header.channelMask);
                bool velQuant = hasVel && (header.flags & CacheFlags.WarpQuant) != 0;
                bool scalarQuant = (header.flags & CacheFlags.ScalarQuant) != 0;
                if ((header.flags & CacheFlags.RawResident) != 0)   // GPU-resident layout on disk → load is a blit
                    BrazeFluidCacheCodec.WriteRawResident(w, header, brickCount, brickIndices, baseBlocks, hasVel, velQuant, scalarQuant);
                else if (scalarQuant)                               // legacy experimental scalar quant (+ packed warp)
                    BrazeFluidCacheCodec.WriteScalarVel(w, header, brickCount, brickIndices, baseBlocks, hasVel, scalarQuant: true);
                else if (!hasVel)                                   // half-exact scalars only
                    BrazeFluidCacheCodec.WriteScalarVel(w, header, brickCount, brickIndices, baseBlocks, hasVel: false, scalarQuant: false);
                else if (velQuant)                                  // half-exact scalars + 8-bit displacement
                    BrazeFluidCacheCodec.WriteScalarVel(w, header, brickCount, brickIndices, baseBlocks, hasVel: true, scalarQuant: false);
                else if ((header.flags & CacheFlags.Deflate) != 0)  // full 6-half, lossless deflate
                    BrazeFluidCacheCodec.WriteDeflate(w, header, brickCount, brickIndices, baseBlocks);
                else                                                // full 6-half, raw
                    WriteRaw(brickCount, brickIndices, baseBlocks);
            }

            // Raw SoA: all indices, then all blocks (planes, not interleaved) — clean for downstream compression.
            void WriteRaw(int brickCount, uint[] idx, ushort[] blocks)
            {
                int baseStride = header.BaseBlockShorts;
                for (int b = 0; b < brickCount; b++) w.Write(idx[b]);
                for (int b = 0; b < brickCount; b++)
                {
                    int bo = b * baseStride;
                    for (int i = 0; i < baseStride; i++) w.Write(blocks[bo + i]);
                }
            }

            /// <summary>Write the seek index and patch the header's frame count + index offset + codec flags.</summary>
            public void Finish()
            {
                if (finished) return;
                long indexPos = fs.Position;
                foreach (long off in frameOffsets) w.Write(off);
                w.Flush();

                header.frameCount = frameOffsets.Count;
                header.indexOffset = indexPos;
                fs.Position = 0;
                WriteHeader(w, header);   // patch
                w.Flush();
                finished = true;
            }

            public int FramesWritten => frameOffsets.Count;
            public long BytesWritten => fs?.Length ?? 0;

            public void Dispose()
            {
                w?.Dispose();
                fs?.Dispose();
            }
        }

        /// <summary>Random-access reader: opens the file, reads the header + seek index, and streams any frame.</summary>
        public sealed class Reader : IDisposable
        {
            readonly FileStream fs;
            readonly BinaryReader r;
            public Header header;
            long[] frameOffsets;
            byte[] frameScratch;   // reused per-frame raw bytes (bulk read, then convert — no per-element file reads)

            public Reader(string path)
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                r = new BinaryReader(fs);
                try
                {
                    header = ReadHeader(r);
                    ValidateHeaderAndIndexBounds(fs.Length, header);
                    fs.Position = header.indexOffset;
                    frameOffsets = new long[header.frameCount];
                    long previous = HeaderBytes - 1;
                    for (int i = 0; i < header.frameCount; i++)
                    {
                        long offset = r.ReadInt64();
                        if (offset < HeaderBytes || offset >= header.indexOffset || offset <= previous)
                            throw new InvalidDataException($"[BrazeFX] Invalid frame-index entry {i}: {offset}.");
                        frameOffsets[i] = offset;
                        previous = offset;
                    }
                }
                catch
                {
                    r.Dispose();
                    fs.Dispose();
                    throw;
                }
            }

            static void ValidateHeaderAndIndexBounds(long fileLength, in Header h)
            {
                if (h.baseRes.x < 8 || h.baseRes.y < 8 || h.baseRes.z < 8 ||
                    h.baseRes.x > 512 || h.baseRes.y > 512 || h.baseRes.z > 512)
                    throw new InvalidDataException($"[BrazeFX] Invalid cache grid {h.baseRes}.");
                if ((h.channelMask & 0x07u) != 0x07u || (h.channelMask & ~ChannelMaskFull) != 0 ||
                    ((h.channelMask & 0x38u) != 0 && !HasStoredVectors(h.channelMask)))
                    throw new InvalidDataException($"[BrazeFX] Invalid cache channel mask 0x{h.channelMask:x}.");
                const CacheFlags supported = CacheFlags.Deflate | CacheFlags.WarpQuant | CacheFlags.RawResident |
                                             CacheFlags.ScalarQuant | CacheFlags.RenderReady | CacheFlags.DetailFlow |
                                             CacheFlags.VelocityField;
                if ((h.flags & ~supported) != 0)
                    throw new InvalidDataException($"[BrazeFX] Cache uses unsupported flags 0x{(uint)(h.flags & ~supported):x}.");

                bool hasVectors = HasStoredVectors(h.channelMask);
                bool hasVelocity = HasVelocity(h);
                bool warpResZero = h.warpRes == Vector3Int.zero;
                bool warpResPositive = h.warpRes.x > 0 && h.warpRes.y > 0 && h.warpRes.z > 0;
                if (!warpResZero && !warpResPositive)
                    throw new InvalidDataException($"[BrazeFX] Invalid Render Detail cache grid {h.warpRes}.");
                bool warpResMissing = !warpResPositive;
                if (hasVectors && !hasVelocity && warpResMissing)
                    throw new InvalidDataException("[BrazeFX] Vector cache declares Render Detail without its native texture resolution.");
                if (!warpResMissing && (h.warpRes.x > 2048 || h.warpRes.y > 2048 || h.warpRes.z > 2048))
                    throw new InvalidDataException($"[BrazeFX] Invalid Render Detail cache grid {h.warpRes}.");
                if (h.brickSize != DefaultBrickSize)
                    throw new InvalidDataException($"[BrazeFX] Unsupported cache brick size {h.brickSize}; expected {DefaultBrickSize}.");
                if (!hasVectors && (h.flags & (CacheFlags.WarpQuant | CacheFlags.VelocityField | CacheFlags.DetailFlow)) != 0)
                    throw new InvalidDataException("[BrazeFX] Cache flags require vector channels that are not present.");
                if ((h.flags & CacheFlags.DetailFlow) != 0 && !HasRenderWarp(h))
                    throw new InvalidDataException("[BrazeFX] Detail Flow requires a native Render Detail grid.");
                int storageFlags = ((h.flags & CacheFlags.Deflate) != 0 ? 1 : 0) +
                                   ((h.flags & CacheFlags.RawResident) != 0 ? 1 : 0);
                if (storageFlags > 1)
                    throw new InvalidDataException("[BrazeFX] Cache declares multiple mutually exclusive storage codecs.");
                if (h.frameCount < 0) throw new InvalidDataException("[BrazeFX] Negative cache frame count.");
                if (float.IsNaN(h.fps) || float.IsInfinity(h.fps) || h.fps <= 0f)
                    throw new InvalidDataException($"[BrazeFX] Invalid cache frame rate {h.fps}.");
                long indexBytes = (long)h.frameCount * sizeof(long);
                if (h.indexOffset < HeaderBytes || h.indexOffset > fileLength || indexBytes > fileLength - h.indexOffset)
                    throw new InvalidDataException("[BrazeFX] Cache seek index is outside the file (truncated or unfinished bake).");
            }

            public int FrameCount => header.frameCount;

            // A single managed array tops out at ~2 GB and our brick offsets are 32-bit (start·uintsPerBrick·4 must stay
            // under int.MaxValue), so the whole-clip RESIDENT load can hold at most ~int.MaxValue/4 uints (~2 GB). Bigger
            // clips MUST stream. Fail fast with a clear, actionable message — otherwise a 2–8 GB clip reads + decodes for
            // ages and then crashes deep in a buffer copy ("object must be an array of primitives") from the int overflow.
            const long MaxResidentUints = int.MaxValue / 4;   // ~536M uints ≈ 2 GB

            static void EnsureResidentFits(long totalBricks, long totalUints)
            {
                if (totalBricks > MaxResidentUints || totalUints > MaxResidentUints)
                    throw new InvalidDataException(
                        $"[BrazeFX] Cache too large to load fully into memory (~{totalUints * 4f / (1024f * 1024f * 1024f):0.0} GB " +
                        "resident; the limit is ~2 GB). Turn ON 'Stream From Disk' on the Braze Cache Player to play it with " +
                        "bounded memory, or re-bake smaller (lower resolution, higher Sparse Cull, or fewer frames).");
            }

            internal static uint ReadU32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

            internal static void ReadFully(Stream s, byte[] buf, int count)
            {
                int read = 0;
                while (read < count)
                {
                    int n = s.Read(buf, read, count - read);
                    if (n <= 0) throw new EndOfStreamException("[BrazeFX] Truncated .brazecache frame.");
                    read += n;
                }
            }

            long FrameEnd(int frame) => frame + 1 < frameOffsets.Length ? frameOffsets[frame + 1] : header.indexOffset;

            long MaxLogicalBrickCount()
            {
                static long Capacity(Vector3Int res, int brick)
                {
                    long x = (res.x + brick - 1L) / brick;
                    long y = (res.y + brick - 1L) / brick;
                    long z = (res.z + brick - 1L) / brick;
                    return x * y * z;
                }

                long result = Capacity(header.baseRes, header.brickSize);
                if (HasRenderWarp(header)) result += Capacity(header.warpRes, header.brickSize);
                return result;
            }

            void ValidateBrickCount(int frame, int brickCount)
            {
                long bytes = FrameEnd(frame) - frameOffsets[frame];
                if (bytes < sizeof(int))
                    throw new InvalidDataException($"[BrazeFX] Cache frame {frame} is truncated before its brick count.");
                if (brickCount < 0 || brickCount > MaxLogicalBrickCount())
                    throw new InvalidDataException($"[BrazeFX] Cache frame {frame} has invalid brick count {brickCount}.");
            }

            int ReadFrameBrickCountChecked(int frame)
            {
                if (frame < 0 || frame >= header.frameCount) throw new ArgumentOutOfRangeException(nameof(frame));
                fs.Position = frameOffsets[frame];
                int brickCount = r.ReadInt32();
                ValidateBrickCount(frame, brickCount);
                return brickCount;
            }

            long ExpectedEncodedRawBytes(int brickCount, bool scalarCodec, bool scalarQuant,
                bool packedVectors)
            {
                if (!scalarCodec)
                    return (long)brickCount * sizeof(uint) + (long)brickCount * header.BaseBlockShorts * sizeof(ushort);

                long scalarsPerBrick = (long)header.BaseBlockVoxels * 3;
                long result = (long)brickCount * sizeof(uint) +
                              (long)brickCount * scalarsPerBrick * (scalarQuant ? 1 : 2);
                if (scalarQuant) result += (long)brickCount * 3 * sizeof(float);
                if (packedVectors) result += (long)brickCount * scalarsPerBrick + (long)brickCount * sizeof(float);
                return result;
            }

            void ValidateFrameBody(int frame, int brickCount, bool hasVectors, bool velocityQuant,
                bool compressed, bool rawResident, bool scalarQuant)
            {
                if (brickCount == 0) return;
                int uintsPerBrick = ResidentUintsPerBrick(header);
                bool scalarCodec = scalarQuant || !hasVectors || velocityQuant;
                bool packedVectors = hasVectors && (scalarQuant || velocityQuant);
                long expectedRaw = rawResident
                    ? (long)brickCount * sizeof(uint) + (long)brickCount * uintsPerBrick * sizeof(uint)
                    : ExpectedEncodedRawBytes(brickCount, scalarCodec, scalarQuant, packedVectors);
                long available = FrameEnd(frame) - frameOffsets[frame] - sizeof(int);
                if (expectedRaw > int.MaxValue)
                    throw new InvalidDataException($"[BrazeFX] Cache frame {frame} exceeds the managed decode limit.");

                if (rawResident || (!scalarCodec && !compressed))
                {
                    if (available < expectedRaw)
                        throw new InvalidDataException($"[BrazeFX] Cache frame {frame} is truncated ({available} of {expectedRaw} body bytes).");
                    return;
                }

                if (available < sizeof(int) * 2L)
                    throw new InvalidDataException($"[BrazeFX] Cache frame {frame} is missing its compressed-body header.");
                int rawLength;
                int compressedLength;
                long restore = fs.Position;
                fs.Position = frameOffsets[frame] + sizeof(int);
                rawLength = r.ReadInt32();
                compressedLength = r.ReadInt32();
                fs.Position = restore;
                long maximumCompressed = expectedRaw + expectedRaw / 100 + 65536;
                if (rawLength != expectedRaw || compressedLength <= 0 ||
                    compressedLength > available - sizeof(int) * 2L || compressedLength > maximumCompressed)
                    throw new InvalidDataException(
                        $"[BrazeFX] Cache frame {frame} has an invalid compressed body ({rawLength} raw, {compressedLength} compressed bytes).");
            }

            /// <summary>Load the ENTIRE cache into flat, GPU-ready arrays — all brick indices and all blocks (as uint,
            /// 2 halfs/uint) concatenated across frames, plus a per-frame {start, count} table. Done ONCE at playback
            /// start so per-frame playback is pure GPU (no disk/CPU). Throws if the cache exceeds a single array.</summary>
            public void LoadAll(out uint[] allBrickIdx, out uint[] allBlocksUint, out int[] frameStart, out int[] frameBrickCount)
            {
                int baseStride = header.BaseBlockShorts;        // halfs per brick (full layout)
                bool hasVel = HasStoredVectors(header.channelMask);
                bool velQuant = hasVel && (header.flags & CacheFlags.WarpQuant) != 0;
                bool deflate = (header.flags & CacheFlags.Deflate) != 0;
                bool rawResident = (header.flags & CacheFlags.RawResident) != 0;
                bool scalarQuant = (header.flags & CacheFlags.ScalarQuant) != 0;   // density/temp/burn 8-bit on disk → dequant to half on load
                // Resident stride depends on the layout: scalars-only and WarpQuant pack smaller bricks.
                int uintsPerBrick = ResidentUintsPerBrick(header);
                int frames = header.frameCount;
                frameBrickCount = new int[frames];
                frameStart = new int[frames];

                // Pull a bounded frame region into RAM in one read, so per-frame decode can run in PARALLEL (frames are
                // independent and write disjoint slices). Larger sources decode sequentially: retaining a 1–2 GB file
                // image beside the resident arrays can otherwise double peak memory and fail before streaming is advised.
                long dataLen = header.indexOffset;   // frames occupy [0, indexOffset); the seek table follows
                const long MaxParallelSourceBytes = 512L * 1024 * 1024;
                // RawResident already matches the GPU layout: decode it sequentially straight into the destination
                // arrays instead of retaining a second full-file byte image alongside the resident payload.
                if (rawResident || dataLen <= 0 || dataLen > MaxParallelSourceBytes)
                {
                    LoadAllStreamed(baseStride, uintsPerBrick, hasVel, velQuant, deflate, rawResident, scalarQuant,
                                    out allBrickIdx, out allBlocksUint, frameStart, frameBrickCount);
                    return;
                }

                long totalBricks = 0;
                for (int f = 0; f < frames; f++)
                {
                    int bc = ReadFrameBrickCountChecked(f);
                    ValidateFrameBody(f, bc, hasVel, velQuant, deflate, rawResident, scalarQuant);
                    frameBrickCount[f] = bc;
                    EnsureResidentFits(totalBricks, totalBricks * uintsPerBrick);
                    frameStart[f] = (int)totalBricks;
                    totalBricks += bc;
                }
                long totalUints = totalBricks * uintsPerBrick;
                EnsureResidentFits(totalBricks, totalUints);

                allBrickIdx = new uint[Math.Max(1, (int)totalBricks)];
                allBlocksUint = new uint[Math.Max(1, (int)totalUints)];

                byte[] fileBytes = new byte[(int)dataLen];
                fs.Position = 0;
                ReadFully(fs, fileBytes, (int)dataLen);

                var hdr = header;   // immutable during load; local copy for the worker lambdas
                var idxOut = allBrickIdx; var blkOut = allBlocksUint;   // locals (can't capture out-params)
                int[] fStart = frameStart, fCount = frameBrickCount;    // ditto
                long[] fOff = frameOffsets;
                int dataLenI = (int)dataLen, stride = uintsPerBrick, bstride = baseStride;
                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    // Each compressed frame needs inflate scratch. Bound concurrency so a dense cache cannot create
                    // processorCount × maxFrame transient memory on top of the full source + resident arrays.
                    MaxDegreeOfParallelism = Mathf.Clamp(System.Environment.ProcessorCount / 2, 1, 4)
                };
                System.Threading.Tasks.Parallel.For(0, frames, parallelOptions, f =>
                {
                    int bc = fCount[f];
                    if (bc <= 0) return;
                    int pos = (int)fOff[f] + 4;   // skip the brickCount int
                    int start = fStart[f];
                    byte[] scratch = null;
                    if (rawResident)   // on-disk bytes == resident pool → pure blit, no decode
                        BrazeFluidCacheCodec.ReadRawResidentInto(fileBytes, pos, bc, stride, idxOut, blkOut, start);
                    else if (scalarQuant || !hasVel || velQuant)
                    {
                        var ms = new MemoryStream(fileBytes, pos, dataLenI - pos, false);
                        var br = new BinaryReader(ms);
                        BrazeFluidCacheCodec.ReadScalarVelInto(ms, br, hdr, bc, idxOut, blkOut, start, stride,
                            hasVel && (scalarQuant || velQuant), scalarQuant, ref scratch);
                    }
                    else if (deflate)
                    {
                        var ms = new MemoryStream(fileBytes, pos, dataLenI - pos, false);
                        var br = new BinaryReader(ms);
                        BrazeFluidCacheCodec.ReadDeflateInto(ms, br, hdr, bc, idxOut, blkOut, start, ref scratch);
                    }
                    else   // raw SoA — read straight from the in-memory bytes
                        BrazeFluidCacheCodec.UnpackSoA(fileBytes, pos, bc, bstride, stride, idxOut, blkOut, start);
                });
            }

            // Streamed fallback (>2GB frame region): the original per-frame sequential decode.
            void LoadAllStreamed(int baseStride, int uintsPerBrick, bool hasVel, bool velQuant, bool deflate,
                                 bool rawResident, bool scalarQuant,
                                 out uint[] allBrickIdx, out uint[] allBlocksUint, int[] frameStart, int[] frameBrickCount)
            {
                int frames = header.frameCount;
                long totalBricks = 0;
                for (int f = 0; f < frames; f++)
                {
                    int bc = ReadFrameBrickCountChecked(f);
                    ValidateFrameBody(f, bc, hasVel, velQuant, deflate, rawResident, scalarQuant);
                    frameBrickCount[f] = bc;
                    EnsureResidentFits(totalBricks, totalBricks * uintsPerBrick);
                    frameStart[f] = (int)totalBricks;
                    totalBricks += bc;
                }
                long totalUints = totalBricks * uintsPerBrick;
                EnsureResidentFits(totalBricks, totalUints);
                allBrickIdx = new uint[Math.Max(1, (int)totalBricks)];
                allBlocksUint = new uint[Math.Max(1, (int)totalUints)];
                for (int f = 0; f < frames; f++)
                {
                    int bc = frameBrickCount[f];
                    if (bc <= 0) continue;
                    fs.Position = frameOffsets[f] + 4;
                    int start = frameStart[f];
                    if (rawResident)
                    {
                        int total = bc * 4 + bc * uintsPerBrick * 4;
                        if (frameScratch == null || frameScratch.Length < total) frameScratch = new byte[total];
                        ReadFully(fs, frameScratch, total);
                        BrazeFluidCacheCodec.ReadRawResidentInto(frameScratch, 0, bc, uintsPerBrick, allBrickIdx, allBlocksUint, start);
                    }
                    else if (scalarQuant || !hasVel || velQuant)
                        BrazeFluidCacheCodec.ReadScalarVelInto(fs, r, header, bc, allBrickIdx, allBlocksUint, start,
                            uintsPerBrick, hasVel && (scalarQuant || velQuant), scalarQuant, ref frameScratch);
                    else if (deflate)
                        BrazeFluidCacheCodec.ReadDeflateInto(fs, r, header, bc, allBrickIdx, allBlocksUint, start, ref frameScratch);
                    else
                        ReadRawInto(bc, baseStride, uintsPerBrick, allBrickIdx, allBlocksUint, start);
                }
            }

            // Raw SoA frame → resident arrays. Bulk-read the body once, then convert (indices, then 2-halfs-per-uint blocks).
            void ReadRawInto(int bc, int baseStride, int uintsPerBrick, uint[] outIdx, uint[] outBlocks, int start)
            {
                int idxBytes = bc * 4;
                int blkBytes = bc * baseStride * 2;
                int total = idxBytes + blkBytes;
                if (frameScratch == null || frameScratch.Length < total) frameScratch = new byte[total];
                ReadFully(fs, frameScratch, total);
                BrazeFluidCacheCodec.UnpackSoA(frameScratch, 0, bc, baseStride, uintsPerBrick, outIdx, outBlocks, start);
            }

            // ── Per-frame access (streaming) ─────────────────────────────────────────────────────────────
            // The whole-cache LoadAll above is the resident path. For STREAMING we read one frame at a time on
            // demand: scan the brick counts up front (cheap — one int per frame via the seek index, no body decode)
            // to size the ring, then DecodeFrameInto each frame as playback needs it.

            /// <summary>Read every frame's brick count from its header (via the seek index) without decoding any voxel
            /// data. Cheap O(frames) seek+read — used to size the streaming ring (max bricks) + the frame table.</summary>
            public int[] ReadFrameBrickCounts()
            {
                var counts = new int[header.frameCount];
                for (int f = 0; f < header.frameCount; f++)
                    counts[f] = ReadFrameBrickCountChecked(f);
                return counts;
            }

            /// <summary>Read only one frame's brick-index plane when the cache is already in RawResident form. Other
            /// codecs keep indices and voxel payload in one compressed stream, so they fall back to the normal bounded
            /// frame decoder. This lets tooling inspect clip-wide sparse occupancy without reading a multi-gigabyte
            /// Fast cache twice.</summary>
            public int ReadFrameBrickIndicesInto(int f, uint[] idxOut, ref uint[] blocksScratch,
                ref byte[] scratch)
            {
                if (f < 0 || f >= header.frameCount) throw new ArgumentOutOfRangeException(nameof(f));
                int bc = ReadFrameBrickCountChecked(f);
                bool hasVectors = HasStoredVectors(header.channelMask);
                bool velocityQuant = hasVectors && (header.flags & CacheFlags.WarpQuant) != 0;
                bool deflate = (header.flags & CacheFlags.Deflate) != 0;
                bool rawResident = (header.flags & CacheFlags.RawResident) != 0;
                bool scalarQuant = (header.flags & CacheFlags.ScalarQuant) != 0;
                ValidateFrameBody(f, bc, hasVectors, velocityQuant, deflate, rawResident, scalarQuant);
                if (bc == 0) return 0;
                fs.Position = frameOffsets[f] + sizeof(int);
                if (idxOut == null || idxOut.Length < bc)
                    throw new ArgumentException($"Brick-index destination needs {bc} entries.", nameof(idxOut));

                if ((header.flags & CacheFlags.RawResident) != 0)
                {
                    int bytes = checked(bc * sizeof(uint));
                    if (scratch == null || scratch.Length < bytes) scratch = new byte[bytes];
                    ReadFully(fs, scratch, bytes);
                    for (int i = 0; i < bc; i++) idxOut[i] = ReadU32(scratch, i * sizeof(uint));
                    return bc;
                }

                int requiredBlocks = checked(bc * ResidentUintsPerBrick(header));
                if (blocksScratch == null || blocksScratch.Length < requiredBlocks)
                    blocksScratch = new uint[requiredBlocks];
                return DecodeFrameInto(f, idxOut, blocksScratch, ref scratch);
            }

            /// <summary>Decode ONE frame into resident-layout arrays at brick offset 0 — <paramref name="idxOut"/>[0..bc)
            /// and <paramref name="blocksOut"/>[0..bc·uintsPerBrick). Mirrors LoadAll's per-frame dispatch exactly, so a
            /// streamed frame is byte-identical to the same frame loaded resident. Returns the brick count.
            /// NOT thread-safe against other reads on this Reader — the streaming worker owns its own Reader instance.</summary>
            public int DecodeFrameInto(int f, uint[] idxOut, uint[] blocksOut, ref byte[] scratch)
            {
                if (f < 0 || f >= header.frameCount) throw new ArgumentOutOfRangeException(nameof(f));
                bool hasVel = HasStoredVectors(header.channelMask);
                bool velQuant = hasVel && (header.flags & CacheFlags.WarpQuant) != 0;
                bool deflate = (header.flags & CacheFlags.Deflate) != 0;
                bool rawResident = (header.flags & CacheFlags.RawResident) != 0;
                bool scalarQuant = (header.flags & CacheFlags.ScalarQuant) != 0;
                int uintsPerBrick = BrazeFluidCacheFormat.ResidentUintsPerBrick(header);
                int baseStride = header.BaseBlockShorts;

                int bc = ReadFrameBrickCountChecked(f);
                ValidateFrameBody(f, bc, hasVel, velQuant, deflate, rawResident, scalarQuant);
                if (bc == 0) return 0;
                if (idxOut == null || idxOut.Length < bc)
                    throw new ArgumentException($"Brick-index destination needs {bc} entries.", nameof(idxOut));
                long requiredBlocks = (long)bc * uintsPerBrick;
                if (requiredBlocks > int.MaxValue || blocksOut == null || blocksOut.Length < requiredBlocks)
                    throw new ArgumentException($"Brick destination needs {requiredBlocks} uints.", nameof(blocksOut));
                fs.Position = frameOffsets[f] + sizeof(int);
                // fs is now positioned at the frame body (just past the brickCount int).
                if (rawResident)
                {
                    int total = bc * 4 + bc * uintsPerBrick * 4;
                    if (scratch == null || scratch.Length < total) scratch = new byte[total];
                    ReadFully(fs, scratch, total);
                    BrazeFluidCacheCodec.ReadRawResidentInto(scratch, 0, bc, uintsPerBrick, idxOut, blocksOut, 0);
                }
                else if (scalarQuant || !hasVel || velQuant)
                    BrazeFluidCacheCodec.ReadScalarVelInto(fs, r, header, bc, idxOut, blocksOut, 0, uintsPerBrick,
                        hasVel && (scalarQuant || velQuant), scalarQuant, ref scratch);
                else if (deflate)
                    BrazeFluidCacheCodec.ReadDeflateInto(fs, r, header, bc, idxOut, blocksOut, 0, ref scratch);
                else
                {
                    int total = bc * 4 + bc * baseStride * 2;
                    if (scratch == null || scratch.Length < total) scratch = new byte[total];
                    ReadFully(fs, scratch, total);
                    BrazeFluidCacheCodec.UnpackSoA(scratch, 0, bc, baseStride, uintsPerBrick, idxOut, blocksOut, 0);
                }
                return bc;
            }

            public void Dispose()
            {
                r?.Dispose();
                fs?.Dispose();
            }
        }
    }
}
