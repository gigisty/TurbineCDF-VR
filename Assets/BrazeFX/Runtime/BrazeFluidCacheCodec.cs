using System;
using System.Runtime.CompilerServices;
using System.IO;
using System.IO.Compression;
using System.Buffers;
using UnityEngine;
using Header = BrazeFX.BrazeFluidCacheFormat.Header;
using Reader = BrazeFX.BrazeFluidCacheFormat.Reader;

namespace BrazeFX
{
    /// <summary>
    /// Per-frame disk codecs for <see cref="BrazeFluidCacheFormat"/>. The on-disk encoding is decoupled from the
    /// resident GPU layout: every codec decodes back to the same flat arrays (brick indices + blocks packed 2 halfs
    /// per uint) that <c>CacheScatterBase</c> reads, so the GPU path never changes.
    ///
    /// Codecs:
    ///   • raw SoA      — indices plane, then blocks plane (half bits). The v3 payload, just de-interleaved.
    ///   • <b>Deflate</b> — lossless. Byte-plane split (low/high byte of every half into separate planes) + a 1-byte
    ///     delta predictor per plane, then <see cref="DeflateStream"/>. Smoke/fire fields are smooth, so the HIGH-byte
    ///     plane (sign+exponent+top mantissa) is almost constant and the delta'd stream is mostly zeros — typically
    ///     2–4× smaller than raw. Brick indices are ascending in scan order, so they delta to tiny values too.
    /// </summary>
    public static class BrazeFluidCacheCodec
    {
        // ── Bake-side: dense readback → sparse active-brick blocks ──────────────────────────────────────
        // Readback fields (raw half) → sparse active-brick blocks. Used by the cache baker; pure/runtime-safe.
        // baseReadback: res³ × 6 ushort (density,temp,burn,velX,velY,velZ — cache order). A brick is active when
        // its peak density OR burn exceeds cull. Returns brick count.
        public static int Compress(
            Vector3Int res, int brickSize, float cull,
            ushort[] baseReadback,
            ref uint[] outBrickIdx, ref ushort[] outBaseBlocks)
        {
            const int C = BrazeFluidCacheFormat.BaseChannels;   // 6
            int B = brickSize;
            int baseStride = B * B * B * C;
            Vector3Int bg = new Vector3Int(
                Mathf.CeilToInt((float)res.x / B), Mathf.CeilToInt((float)res.y / B), Mathf.CeilToInt((float)res.z / B));

            int count = 0;
            for (int bz = 0; bz < bg.z; bz++)
            for (int by = 0; by < bg.y; by++)
            for (int bx = 0; bx < bg.x; bx++)
            {
                // Active test: any voxel in the brick with density or burn above cull.
                bool activeBrick = false;
                int ox = bx * B, oy = by * B, oz = bz * B;
                for (int lz = 0; lz < B && !activeBrick; lz++)
                {
                    int gz = oz + lz; if (gz >= res.z) break;
                    for (int ly = 0; ly < B && !activeBrick; ly++)
                    {
                        int gy = oy + ly; if (gy >= res.y) break;
                        for (int lx = 0; lx < B; lx++)
                        {
                            int gx = ox + lx; if (gx >= res.x) break;
                            int vi = ((gz * res.y + gy) * res.x + gx) * C;
                            if (HalfToFloat(baseReadback[vi]) > cull || HalfToFloat(baseReadback[vi + 2]) > cull)
                            { activeBrick = true; break; }   // density (0) or burn (2)
                        }
                    }
                }
                if (!activeBrick) continue;

                EnsureCapacity(ref outBrickIdx, count + 1);
                EnsureCapacity(ref outBaseBlocks, (count + 1) * baseStride);

                outBrickIdx[count] = (uint)((bz * bg.y + by) * bg.x + bx);

                int bo = count * baseStride;
                for (int lz = 0; lz < B; lz++)
                {
                    int gz = oz + lz;
                    for (int ly = 0; ly < B; ly++)
                    {
                        int gy = oy + ly;
                        for (int lx = 0; lx < B; lx++)
                        {
                            int gx = ox + lx;
                            int si = bo + ((lz * B + ly) * B + lx) * C;
                            bool inside = gx < res.x && gy < res.y && gz < res.z;
                            if (inside)
                            {
                                int vi = ((gz * res.y + gy) * res.x + gx) * C;
                                for (int c = 0; c < C; c++) outBaseBlocks[si + c] = baseReadback[vi + c];   // density,temp,burn,velXYZ
                            }
                            else { for (int c = 0; c < C; c++) outBaseBlocks[si + c] = 0; }
                        }
                    }
                }
                count++;
            }
            return count;
        }

        static void EnsureCapacity(ref uint[] a, int n) { if (a == null || a.Length < n) Array.Resize(ref a, Mathf.NextPowerOfTwo(n)); }
        static void EnsureCapacity(ref ushort[] a, int n) { if (a == null || a.Length < n) Array.Resize(ref a, Mathf.NextPowerOfTwo(n)); }

        /// <summary>IEEE half (raw 16-bit) → float. Used at bake time for the sparse cull test + velocity quantization.</summary>
        public static float HalfToFloat(ushort h)
        {
            uint sign = (uint)(h & 0x8000) << 16;
            uint exp = (uint)(h & 0x7C00) >> 10;
            uint mant = (uint)(h & 0x03FF);
            uint bits;
            if (exp == 0)
            {
                if (mant == 0) bits = sign;
                else
                {
                    exp = 1;
                    while ((mant & 0x0400) == 0) { mant <<= 1; exp--; }
                    mant &= 0x03FF;
                    bits = sign | ((exp + 112) << 23) | (mant << 13);
                }
            }
            else if (exp == 0x1F) bits = sign | 0x7F800000u | (mant << 13);
            else bits = sign | ((exp + 112) << 23) | (mant << 13);
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        /// <summary>Float → IEEE 754 binary16 bits with round-to-nearest-even. Shared by derived-cache and interchange
        /// exporters so sparse processing never needs a dense texture or a GPU readback.</summary>
        public static ushort FloatToHalf(float value)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(value);
            uint sign = (bits >> 16) & 0x8000u;
            uint exponent = (bits >> 23) & 0xFFu;
            uint mantissa = bits & 0x7FFFFFu;
            if (exponent == 0xFFu)
                return (ushort)(sign | (mantissa == 0 ? 0x7C00u : 0x7E00u));

            int halfExponent = (int)exponent - 127 + 15;
            if (halfExponent >= 31) return (ushort)(sign | 0x7C00u);
            if (halfExponent <= 0)
            {
                if (halfExponent < -10) return (ushort)sign;
                mantissa |= 0x800000u;
                int shift = 14 - halfExponent;
                uint rounded = mantissa >> shift;
                uint remainder = mantissa & ((1u << shift) - 1u);
                uint halfway = 1u << (shift - 1);
                if (remainder > halfway || (remainder == halfway && (rounded & 1u) != 0)) rounded++;
                return (ushort)(sign | rounded);
            }

            uint halfMantissa = mantissa >> 13;
            uint lost = mantissa & 0x1FFFu;
            if (lost > 0x1000u || (lost == 0x1000u && (halfMantissa & 1u) != 0))
            {
                halfMantissa++;
                if (halfMantissa == 0x400u)
                {
                    halfMantissa = 0;
                    halfExponent++;
                    if (halfExponent >= 31) return (ushort)(sign | 0x7C00u);
                }
            }
            return (ushort)(sign | ((uint)halfExponent << 10) | halfMantissa);
        }

        // ── shared: raw SoA body → resident arrays ───────────────────────────────────────────────────
        // buf at [off]: brickIndices[bc]·uint32, then blocks[bc × baseStride]·uint16 (half bits). Packs the blocks
        // 2 halfs per uint (low/high 16 bits) — the exact layout CacheScatterBase expects.
        public static void UnpackSoA(byte[] buf, int off, int bc, int baseStride, int uintsPerBrick,
                                     uint[] outIdx, uint[] outBlocks, int start)
        {
            for (int b = 0; b < bc; b++) outIdx[start + b] = Reader.ReadU32(buf, off + b * 4);
            int blkOff = off + bc * 4;
            for (int b = 0; b < bc; b++)
            {
                int dstBase = (start + b) * uintsPerBrick;
                int srcBase = blkOff + b * baseStride * 2;
                for (int u = 0; u < uintsPerBrick; u++)
                {
                    int so = srcBase + (u << 2);
                    uint lo = (uint)(buf[so] | (buf[so + 1] << 8));
                    uint hi = (uint)(buf[so + 2] | (buf[so + 3] << 8));
                    outBlocks[dstBase + u] = lo | (hi << 16);
                }
            }
        }

        // ── Deflate codec ────────────────────────────────────────────────────────────────────────────
        // Frame body (after the codec-agnostic brickCount int): rawLen(int), compLen(int), deflate(payload).
        // payload (rawLen bytes) = [ idx delta·uint32×bc ][ lo-byte plane delta×nHalfs ][ hi-byte plane delta×nHalfs ].
        public static void WriteDeflate(BinaryWriter w, in Header h, int bc, uint[] idx, ushort[] blocks)
        {
            byte[] pre = BuildLosslessPrefilter(h, bc, idx, blocks, out int rawLen);

            byte[] comp = Deflate(pre, rawLen);
            w.Write(rawLen);
            w.Write(comp.Length);
            w.Write(comp, 0, comp.Length);
        }

        // Deflate frame → resident arrays. Inflates, reverses the plane-split + delta into a raw SoA buffer, unpacks.
        public static void ReadDeflateInto(Stream s, BinaryReader r, in Header h, int bc,
                                           uint[] outIdx, uint[] outBlocks, int start, ref byte[] scratch)
        {
            int baseStride = h.BaseBlockShorts;
            int uintsPerBrick = baseStride / 2;
            int nHalfs = checked(bc * baseStride);
            int expectedRawLen = checked(bc * sizeof(uint) + nHalfs * sizeof(ushort));
            int rawLen = r.ReadInt32();
            int compLen = r.ReadInt32();
            long maximumCompressed = (long)expectedRawLen + expectedRawLen / 100L + 65536;
            if (rawLen != expectedRawLen || compLen <= 0 || compLen > maximumCompressed ||
                (s.CanSeek && compLen > s.Length - s.Position))
                throw new InvalidDataException(
                    $"[BrazeFX] Invalid compressed cache frame ({rawLen} raw, {compLen} compressed bytes).");

            // Keep one reusable inflated plane buffer per reader. The previous implementation also rented a second
            // rawLen buffer, reconstructed an AoS copy into `scratch`, then read that copy again to pack uints. A
            // production full-detail frame can be ~80 MB, so that doubled transient memory and memory bandwidth on
            // every seek. Decode the delta planes directly into the resident destination instead.
            if (scratch == null || scratch.Length < rawLen) scratch = new byte[rawLen];
            byte[] comp = ArrayPool<byte>.Shared.Rent(Mathf.Max(1, compLen));
            try
            {
                Reader.ReadFully(s, comp, compLen);
                InflateInto(comp, compLen, scratch, rawLen);
                DecodeLosslessPrefilterInto(scratch, bc, nHalfs, uintsPerBrick, outIdx, outBlocks, start);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(comp);
            }
        }

        static byte[] BuildLosslessPrefilter(in Header h, int bc, uint[] idx, ushort[] blocks, out int rawLen)
        {
            int nHalfs = checked(bc * h.BaseBlockShorts);
            rawLen = checked(bc * sizeof(uint) + nHalfs * sizeof(ushort));
            byte[] pre = new byte[rawLen];
            uint previousIndex = 0;
            for (int b = 0; b < bc; b++)
            {
                uint delta = idx[b] - previousIndex;
                previousIndex = idx[b];
                WriteU32(pre, b * sizeof(uint), delta);
            }
            int loOff = bc * sizeof(uint), hiOff = loOff + nHalfs;
            byte previousLow = 0, previousHigh = 0;
            for (int i = 0; i < nHalfs; i++)
            {
                ushort value = blocks[i];
                byte low = (byte)value, high = (byte)(value >> 8);
                pre[loOff + i] = (byte)(low - previousLow);
                pre[hiOff + i] = (byte)(high - previousHigh);
                previousLow = low;
                previousHigh = high;
            }
            return pre;
        }

        static void DecodeLosslessPrefilterInto(byte[] pre, int bc, int nHalfs, int uintsPerBrick,
                                                uint[] outIdx, uint[] outBlocks, int start)
        {
            uint accumulatedIndex = 0;
            for (int b = 0; b < bc; b++)
            {
                accumulatedIndex += Reader.ReadU32(pre, b * sizeof(uint));
                outIdx[start + b] = accumulatedIndex;
            }
            int loOff = bc * sizeof(uint), hiOff = loOff + nHalfs;
            byte previousLow = 0, previousHigh = 0;
            int destination = start * uintsPerBrick;
            for (int i = 0; i < nHalfs; i += 2)
            {
                byte low0 = (byte)(previousLow + pre[loOff + i]); previousLow = low0;
                byte high0 = (byte)(previousHigh + pre[hiOff + i]); previousHigh = high0;
                ushort half0 = (ushort)(low0 | (high0 << 8));
                if (i + 1 < nHalfs)
                {
                    byte low1 = (byte)(previousLow + pre[loOff + i + 1]); previousLow = low1;
                    byte high1 = (byte)(previousHigh + pre[hiOff + i + 1]); previousHigh = high1;
                    ushort half1 = (ushort)(low1 | (high1 << 8));
                    outBlocks[destination++] = (uint)(half0 | (half1 << 16));
                }
                else outBlocks[destination++] = half0;
            }
        }

        // ── Scalar(+WarpQuant) codec ──────────────────────────────────────────────────────────────────
        // Scalars (density/temp/burn) always stay 16-bit half. The render displacement is optional; the historical
        // local names `hasVel`/`vel` describe the unchanged six-channel byte layout, not simulation velocity.
        //   • hasVel  : 8-bit signed displacement about a per-brick max-abs scale.
        //               resident block = [brickVox×3 scalar halfs][brickVox×3 warp bytes][1 float scale].
        //   • !hasVel : scalars only.
        //               resident block = [brickVox×3 scalar halfs].
        // Always deflated. Pre-deflate planes: [idx Δ][scalar lo Δ][scalar hi Δ] (+ [vel bytes Δ][scales raw] if hasVel).
        public static void WriteScalarVel(BinaryWriter w, in Header h, int bc, uint[] idx, ushort[] blocks, bool hasVel, bool scalarQuant)
        {
            int vpb = h.BaseBlockVoxels;
            int baseStride = h.BaseBlockShorts;     // vpb × 6 (input 6-half block stride from Compress)
            int scalarPer = vpb * 3, velPer = vpb * 3;
            int idxBytes = bc * 4;
            // Scalar plane is 1 byte/scalar (ScalarQuant, 8-bit) or 2 (lo+hi half planes). Velocity is always 8-bit here.
            int scalarBytes = bc * scalarPer * (scalarQuant ? 1 : 2);
            int scalarScaleBytes = scalarQuant ? bc * 3 * 4 : 0;   // ScalarQuant: 3 per-channel max-scales / brick
            int velPlane = hasVel ? bc * velPer : 0, velScaleBytes = hasVel ? bc * 4 : 0;
            int rawLen = idxBytes + scalarBytes + scalarScaleBytes + velPlane + velScaleBytes;
            // Plane offsets. Non-quant: [idx][lo][hi][vel][velScale]. Quant: [idx][sBytes][sScales][vel][velScale].
            int loOff = idxBytes, hiOff = loOff + bc * scalarPer;                       // non-quant only
            int sByteOff = idxBytes, sScaleOff = sByteOff + bc * scalarPer;             // quant only
            int velOff = idxBytes + scalarBytes + scalarScaleBytes, velScaleOff = velOff + velPlane;
            byte[] pre = new byte[rawLen];

            uint prev = 0;
            for (int b = 0; b < bc; b++) { uint d = idx[b] - prev; prev = idx[b]; WriteU32(pre, b * 4, d); }

            for (int b = 0; b < bc; b++)
            {
                int bo = b * baseStride;
                // Per-brick scales: 3 per-channel max for scalars (quant), 1 max-abs for velocity (hasVel).
                float s0 = 0f, s1 = 0f, s2 = 0f, vscale = 0f;
                for (int v = 0; v < vpb; v++)
                {
                    int o = bo + v * 6;
                    if (scalarQuant)
                    {
                        s0 = Mathf.Max(s0, Mathf.Abs(HalfToFloat(blocks[o])));
                        s1 = Mathf.Max(s1, Mathf.Abs(HalfToFloat(blocks[o + 1])));
                        s2 = Mathf.Max(s2, Mathf.Abs(HalfToFloat(blocks[o + 2])));
                    }
                    if (hasVel)
                    {
                        float m = Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 3])),
                                  Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 4])), Mathf.Abs(HalfToFloat(blocks[o + 5]))));
                        if (m > vscale) vscale = m;
                    }
                }
                float i0 = 0f, i1 = 0f, i2 = 0f, vinv = 0f;
                if (scalarQuant)
                {
                    WriteU32(pre, sScaleOff + (b * 3 + 0) * 4, (uint)BitConverter.SingleToInt32Bits(s0));
                    WriteU32(pre, sScaleOff + (b * 3 + 1) * 4, (uint)BitConverter.SingleToInt32Bits(s1));
                    WriteU32(pre, sScaleOff + (b * 3 + 2) * 4, (uint)BitConverter.SingleToInt32Bits(s2));
                    i0 = s0 > 0f ? 255f / s0 : 0f; i1 = s1 > 0f ? 255f / s1 : 0f; i2 = s2 > 0f ? 255f / s2 : 0f;
                }
                if (hasVel)
                {
                    WriteU32(pre, velScaleOff + b * 4, (uint)BitConverter.SingleToInt32Bits(vscale));
                    vinv = vscale > 0f ? 127f / vscale : 0f;
                }

                int vBase = b * velPer;
                for (int v = 0; v < vpb; v++)
                {
                    int o = bo + v * 6;
                    if (scalarQuant)
                    {
                        int sb = b * scalarPer + v * 3;
                        pre[sByteOff + sb + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt(HalfToFloat(blocks[o]) * i0), 0, 255);
                        pre[sByteOff + sb + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(HalfToFloat(blocks[o + 1]) * i1), 0, 255);
                        pre[sByteOff + sb + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(HalfToFloat(blocks[o + 2]) * i2), 0, 255);
                    }
                    else
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            ushort hv = blocks[o + c];   // density/temp/burn half bits → lo/hi byte planes
                            int sj = b * scalarPer + v * 3 + c;
                            pre[loOff + sj] = (byte)(hv & 0xFF);
                            pre[hiOff + sj] = (byte)(hv >> 8);
                        }
                    }
                    if (hasVel)
                        for (int c = 0; c < 3; c++)
                        {
                            int q = vscale > 0f ? Mathf.RoundToInt(HalfToFloat(blocks[o + 3 + c]) * vinv) + 128 : 128;
                            pre[velOff + vBase + v * 3 + c] = (byte)Mathf.Clamp(q, 1, 255);
                        }
                }
            }

            // Delta-predict the noisy byte planes (smooth → mostly zeros after delta → deflates small). Scales are raw.
            if (scalarQuant) DeltaInPlace(pre, sByteOff, bc * scalarPer);
            else { DeltaInPlace(pre, loOff, bc * scalarPer); DeltaInPlace(pre, hiOff, bc * scalarPer); }
            if (hasVel) DeltaInPlace(pre, velOff, velPlane);

            byte[] comp = Deflate(pre, rawLen);
            w.Write(rawLen);
            w.Write(comp.Length);
            w.Write(comp, 0, comp.Length);
        }

        // Scalar(+WarpQuant) frame → resident arrays (matches ScalarUintsPerBrick/WarpQuantUintsPerBrick + CacheScatterBase).
        public static void ReadScalarVelInto(Stream s, BinaryReader r, in Header h, int bc,
                                             uint[] outIdx, uint[] outBlocks, int start, int uintsPerBrick, bool hasVel, bool scalarQuant, ref byte[] scratch)
        {
            int vpb = h.BaseBlockVoxels;
            int scalarPer = vpb * 3, velPer = vpb * 3;
            int idxBytes = checked(bc * 4);
            int scalarBytes = checked(bc * scalarPer * (scalarQuant ? 1 : 2));
            int scalarScaleBytes = scalarQuant ? checked(bc * 3 * 4) : 0;
            int velPlane = hasVel ? checked(bc * velPer) : 0;
            int loOff = idxBytes, hiOff = loOff + bc * scalarPer;                       // non-quant
            int sByteOff = idxBytes, sScaleOff = sByteOff + bc * scalarPer;             // quant
            int velOff = idxBytes + scalarBytes + scalarScaleBytes, velScaleOff = velOff + velPlane;
            int scalarUints = (vpb * 3 + 1) / 2, velUints = hasVel ? (vpb * 3 + 3) / 4 : 0;
            int scalarByteUints = (vpb * 3 + 3) / 4;   // ScalarQuant resident: scalars 8-bit, 4 bytes/uint

            int fileRawLen = r.ReadInt32();
            int compLen = r.ReadInt32();
            int expectedRawLen = checked(velScaleOff + (hasVel ? bc * 4 : 0));
            long maximumCompressed = (long)expectedRawLen + expectedRawLen / 100L + 65536;
            if (fileRawLen != expectedRawLen || compLen <= 0 || compLen > maximumCompressed ||
                (s.CanSeek && compLen > s.Length - s.Position))
                throw new InvalidDataException(
                    $"[BrazeFX] Invalid compact cache frame ({fileRawLen} raw, {compLen} compressed bytes).");
            if (scratch == null || scratch.Length < compLen) scratch = new byte[compLen];
            Reader.ReadFully(s, scratch, compLen);
            byte[] pre = ArrayPool<byte>.Shared.Rent(Mathf.Max(1, fileRawLen));
            try
            {
                InflateInto(scratch, compLen, pre, fileRawLen);

                if (scalarQuant) UndeltaInPlace(pre, sByteOff, bc * scalarPer);
                else { UndeltaInPlace(pre, loOff, bc * scalarPer); UndeltaInPlace(pre, hiOff, bc * scalarPer); }
                if (hasVel) UndeltaInPlace(pre, velOff, velPlane);

                uint acc = 0;
                for (int b = 0; b < bc; b++) { acc += Reader.ReadU32(pre, b * 4); outIdx[start + b] = acc; }

                for (int b = 0; b < bc; b++)
                {
                    int brickBase = (start + b) * uintsPerBrick;
                    int sBase = b * scalarPer, vBase = b * velPer;

                if (scalarQuant)
                {
                    // RESIDENT-QUANT: scalars stay 8-bit in the GPU pool; the scatter kernel dequantizes. The CPU only
                    // packs bytes (no FloatToHalf). Resident: [scalar bytes][vel bytes (hasVel)][3 scalar scales][1 vel scale].
                    for (int u = 0; u < scalarByteUints; u++)
                    {
                        int bj = u * 4;
                        uint w0 = bj + 0 < scalarPer ? (uint)pre[sByteOff + sBase + bj + 0] : 0u;
                        uint w1 = bj + 1 < scalarPer ? (uint)pre[sByteOff + sBase + bj + 1] : 0u;
                        uint w2 = bj + 2 < scalarPer ? (uint)pre[sByteOff + sBase + bj + 2] : 0u;
                        uint w3 = bj + 3 < scalarPer ? (uint)pre[sByteOff + sBase + bj + 3] : 0u;
                        outBlocks[brickBase + u] = w0 | (w1 << 8) | (w2 << 16) | (w3 << 24);
                    }
                    if (hasVel)
                        for (int u = 0; u < velUints; u++)
                        {
                            int bj = u * 4;
                            uint w0 = bj + 0 < velPer ? (uint)pre[velOff + vBase + bj + 0] : 0u;
                            uint w1 = bj + 1 < velPer ? (uint)pre[velOff + vBase + bj + 1] : 0u;
                            uint w2 = bj + 2 < velPer ? (uint)pre[velOff + vBase + bj + 2] : 0u;
                            uint w3 = bj + 3 < velPer ? (uint)pre[velOff + vBase + bj + 3] : 0u;
                            outBlocks[brickBase + scalarByteUints + u] = w0 | (w1 << 8) | (w2 << 16) | (w3 << 24);
                        }
                    int scaleBase = brickBase + scalarByteUints + velUints;
                    outBlocks[scaleBase + 0] = Reader.ReadU32(pre, sScaleOff + (b * 3 + 0) * 4);   // density scale
                    outBlocks[scaleBase + 1] = Reader.ReadU32(pre, sScaleOff + (b * 3 + 1) * 4);   // temp scale
                    outBlocks[scaleBase + 2] = Reader.ReadU32(pre, sScaleOff + (b * 3 + 2) * 4);   // burn scale
                    if (hasVel) outBlocks[scaleBase + 3] = Reader.ReadU32(pre, velScaleOff + b * 4);   // vel scale
                        continue;
                    }

                // Lossless scalars (16-bit half from lo/hi planes) + optional 8-bit velocity.
                for (int u = 0; u < scalarUints; u++)
                {
                    int hj0 = u * 2, hj1 = u * 2 + 1;
                    uint h0 = hj0 < scalarPer ? (uint)(pre[loOff + sBase + hj0] | (pre[hiOff + sBase + hj0] << 8)) : 0u;
                    uint h1 = hj1 < scalarPer ? (uint)(pre[loOff + sBase + hj1] | (pre[hiOff + sBase + hj1] << 8)) : 0u;
                    outBlocks[brickBase + u] = h0 | (h1 << 16);
                }
                if (!hasVel) continue;
                for (int u = 0; u < velUints; u++)
                {
                    int bj = u * 4;
                    uint w0 = bj + 0 < velPer ? (uint)pre[velOff + vBase + bj + 0] : 0u;
                    uint w1 = bj + 1 < velPer ? (uint)pre[velOff + vBase + bj + 1] : 0u;
                    uint w2 = bj + 2 < velPer ? (uint)pre[velOff + vBase + bj + 2] : 0u;
                    uint w3 = bj + 3 < velPer ? (uint)pre[velOff + vBase + bj + 3] : 0u;
                    outBlocks[brickBase + scalarUints + u] = w0 | (w1 << 8) | (w2 << 16) | (w3 << 24);
                }
                    outBlocks[brickBase + scalarUints + velUints] = Reader.ReadU32(pre, velScaleOff + b * 4);   // vel scale float bits
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pre);
            }
        }

        // ── Raw-resident codec (GPU-resident layout on disk) ────────────────────────────────────────────
        // The frame body is byte-IDENTICAL to the resident _ScatterBase ComputeBuffer the scatter kernel reads:
        //   [ brickIndices[bc]·uint32 ][ blocks[bc × ResidentUintsPerBrick]·uint32 ].
        // No deflate, no plane-split, no delta — so LoadAll is a pure Buffer.BlockCopy (zero CPU transform). Trades a
        // bigger file for near-instant load (disk-I/O + GPU-upload bound). Scalars stay half; displacement may be 8-bit
        // (velQuant local) exactly as the resident WarpQuant layout. Block packing here MUST match CacheScatterBase + the
        // ReadScalarVelInto output, voxel-major channel-minor.
        public static void WriteRawResident(BinaryWriter w, in Header h, int bc, uint[] idx, ushort[] blocks, bool hasVel, bool velQuant, bool scalarQuant)
        {
            int vpb = h.BaseBlockVoxels;
            int baseStride = h.BaseBlockShorts;     // 6 halfs/voxel (input block stride from Compress)
            int scalarHalfs = vpb * 3;
            int scalarUints = (scalarHalfs + 1) / 2;
            int velUints = (vpb * 3 + 3) / 4;
            int scalarByteUints = (vpb * 3 + 3) / 4;   // ScalarQuant: scalars 8-bit, 4/uint

            for (int b = 0; b < bc; b++) w.Write(idx[b]);   // index plane (matches _ScatterBrickIdx)

            for (int b = 0; b < bc; b++)
            {
                int bo = b * baseStride;

                if (scalarQuant)
                {
                    // ScalarQuant resident layout, written raw (no deflate) = fastest-load + smallest VRAM:
                    //   [scalar bytes][vel bytes (hasVel)][density,temp,burn scale][vel scale (hasVel)]. Must match
                    //   ScalarQuantUintsPerBrick + CacheScatterBase + ReadScalarVelInto.
                    float s0 = 0f, s1 = 0f, s2 = 0f, vscale = 0f;
                    for (int v = 0; v < vpb; v++)
                    {
                        int o = bo + v * 6;
                        s0 = Mathf.Max(s0, Mathf.Abs(HalfToFloat(blocks[o])));
                        s1 = Mathf.Max(s1, Mathf.Abs(HalfToFloat(blocks[o + 1])));
                        s2 = Mathf.Max(s2, Mathf.Abs(HalfToFloat(blocks[o + 2])));
                        if (hasVel)
                            vscale = Mathf.Max(vscale, Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 3])),
                                     Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 4])), Mathf.Abs(HalfToFloat(blocks[o + 5])))));
                    }
                    float i0 = s0 > 0f ? 255f / s0 : 0f, i1 = s1 > 0f ? 255f / s1 : 0f, i2 = s2 > 0f ? 255f / s2 : 0f;
                    for (int u = 0; u < scalarByteUints; u++)   // scalar bytes (density,temp,burn) 4/uint
                    {
                        uint packed = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int bj = u * 4 + k; if (bj >= vpb * 3) break;
                            int v = bj / 3, c = bj % 3;
                            float ic = c == 0 ? i0 : c == 1 ? i1 : i2;
                            packed |= (uint)Mathf.Clamp(Mathf.RoundToInt(HalfToFloat(blocks[bo + v * 6 + c]) * ic), 0, 255) << (k * 8);
                        }
                        w.Write(packed);
                    }
                    if (hasVel)
                    {
                        float vinv = vscale > 0f ? 127f / vscale : 0f;
                        for (int u = 0; u < velUints; u++)       // vel bytes 4/uint
                        {
                            uint packed = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int bj = u * 4 + k; if (bj >= vpb * 3) break;
                                int v = bj / 3, c = bj % 3;
                                int q = vscale > 0f ? Mathf.RoundToInt(HalfToFloat(blocks[bo + v * 6 + 3 + c]) * vinv) + 128 : 128;
                                packed |= (uint)Mathf.Clamp(q, 1, 255) << (k * 8);
                            }
                            w.Write(packed);
                        }
                    }
                    w.Write((uint)BitConverter.SingleToInt32Bits(s0));
                    w.Write((uint)BitConverter.SingleToInt32Bits(s1));
                    w.Write((uint)BitConverter.SingleToInt32Bits(s2));
                    if (hasVel) w.Write((uint)BitConverter.SingleToInt32Bits(vscale));
                    continue;
                }

                if (hasVel && !velQuant)
                {
                    // Full 6-half: pack straight, 2 halfs/uint (same bytes as the input block).
                    int fullUints = vpb * 6 / 2;
                    for (int u = 0; u < fullUints; u++)
                        w.Write((uint)(blocks[bo + u * 2] | (blocks[bo + u * 2 + 1] << 16)));
                    continue;
                }

                // Scalars (density,temp,burn) — half index in the scalar plane = v*3 + c.
                for (int u = 0; u < scalarUints; u++)
                {
                    int hj0 = u * 2, hj1 = u * 2 + 1;
                    uint h0 = hj0 < scalarHalfs ? blocks[bo + (hj0 / 3) * 6 + (hj0 % 3)] : (ushort)0;
                    uint h1 = hj1 < scalarHalfs ? blocks[bo + (hj1 / 3) * 6 + (hj1 % 3)] : (ushort)0;
                    w.Write(h0 | (h1 << 16));
                }
                if (!hasVel) continue;   // scalars-only layout ends here

                // WarpQuant: per-brick max-abs scale → 8-bit signed displacement bytes, then the scale float.
                float scale = 0f;
                for (int v = 0; v < vpb; v++)
                {
                    int o = bo + v * 6;
                    float m = Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 3])),
                              Mathf.Max(Mathf.Abs(HalfToFloat(blocks[o + 4])),
                                        Mathf.Abs(HalfToFloat(blocks[o + 5]))));
                    if (m > scale) scale = m;
                }
                float inv = scale > 0f ? 127f / scale : 0f;
                for (int u = 0; u < velUints; u++)
                {
                    uint packed = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int bj = u * 4 + k;
                        if (bj >= vpb * 3) break;
                        int v = bj / 3, c = bj % 3;
                        int q = scale > 0f ? Mathf.RoundToInt(HalfToFloat(blocks[bo + v * 6 + 3 + c]) * inv) + 128 : 128;
                        packed |= (uint)Mathf.Clamp(q, 1, 255) << (k * 8);
                    }
                    w.Write(packed);
                }
                w.Write((uint)BitConverter.SingleToInt32Bits(scale));
            }
        }

        // Raw-resident frame → resident arrays: a straight byte blit (no decode). buf at [off] holds the index plane
        // then the block plane, both already in resident layout. outBlocks is uint[], so the byte offsets land aligned.
        public static void ReadRawResidentInto(byte[] buf, int off, int bc, int uintsPerBrick,
                                               uint[] outIdx, uint[] outBlocks, int start)
        {
            Buffer.BlockCopy(buf, off, outIdx, start * 4, bc * 4);
            Buffer.BlockCopy(buf, off + bc * 4, outBlocks, start * uintsPerBrick * 4, bc * uintsPerBrick * 4);
        }

        static void DeltaInPlace(byte[] a, int off, int len)
        { byte p = 0; for (int i = 0; i < len; i++) { byte v = a[off + i]; a[off + i] = (byte)(v - p); p = v; } }
        static void UndeltaInPlace(byte[] a, int off, int len)
        { byte p = 0; for (int i = 0; i < len; i++) { p = (byte)(p + a[off + i]); a[off + i] = p; } }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────
        static void WriteU32(byte[] b, int i, uint v)
        { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }

        static byte[] Deflate(byte[] data, int len)
        {
            using var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, len);
            return ms.ToArray();
        }

        static void InflateInto(byte[] comp, int compLen, byte[] outBuf, int rawLen)
        {
            using var ms = new MemoryStream(comp, 0, compLen);
            using var ds = new DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
            Reader.ReadFully(ds, outBuf, rawLen);
        }
    }
}
