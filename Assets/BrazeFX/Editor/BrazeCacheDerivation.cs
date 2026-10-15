using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    internal sealed class BrazeDecodedCacheFrame
    {
        public uint[] indices;
        public ushort[] blocks;
        public int brickCount;
    }

    /// <summary>CPU sparse-frame decoding shared by loop derivation and interchange export. Resident layouts are
    /// expanded to the canonical six-half brick layout without constructing a dense volume.</summary>
    internal static class BrazeCacheFrameUtility
    {
        public static BrazeDecodedCacheFrame Decode(BrazeFluidCacheFormat.Reader reader, int frame,
            int knownBrickCount = -1)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            frame = Mathf.Clamp(frame, 0, Mathf.Max(0, reader.FrameCount - 1));
            int bc = knownBrickCount;
            if (bc < 0) bc = reader.ReadFrameBrickCounts()[frame];
            int residentStride = BrazeFluidCacheFormat.ResidentUintsPerBrick(reader.header);
            var indices = new uint[Mathf.Max(1, bc)];
            var resident = new uint[Mathf.Max(1, bc * residentStride)];
            byte[] scratch = null;
            int decoded = reader.DecodeFrameInto(frame, indices, resident, ref scratch);
            int fullHalfs = reader.header.BaseBlockShorts;
            var full = new ushort[Mathf.Max(1, decoded * fullHalfs)];
            for (int b = 0; b < decoded; b++) ExpandBrick(reader.header, resident, b * residentStride, full, b * fullHalfs);
            return new BrazeDecodedCacheFrame { indices = indices, blocks = full, brickCount = decoded };
        }

        static void ExpandBrick(in BrazeFluidCacheFormat.Header header, uint[] source, int sourceOffset,
            ushort[] destination, int destinationOffset)
        {
            int voxels = header.BaseBlockVoxels;
            bool hasVectors = BrazeFluidCacheFormat.HasStoredVectors(header.channelMask);
            bool warpQuant = hasVectors && (header.flags & BrazeFluidCacheFormat.CacheFlags.WarpQuant) != 0;
            bool scalarQuant = (header.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;

            if (scalarQuant)
            {
                int scalarBytes = voxels * 3;
                int scalarUints = (scalarBytes + 3) / 4;
                int warpBytes = hasVectors ? voxels * 3 : 0;
                int warpUints = (warpBytes + 3) / 4;
                float s0 = UIntFloat(source[sourceOffset + scalarUints + warpUints]);
                float s1 = UIntFloat(source[sourceOffset + scalarUints + warpUints + 1]);
                float s2 = UIntFloat(source[sourceOffset + scalarUints + warpUints + 2]);
                float ws = hasVectors ? UIntFloat(source[sourceOffset + scalarUints + warpUints + 3]) : 0f;
                for (int v = 0; v < voxels; v++)
                {
                    int dst = destinationOffset + v * 6;
                    destination[dst] = BrazeFluidCacheCodec.FloatToHalf(ReadByte(source, sourceOffset, v * 3) / 255f * s0);
                    destination[dst + 1] = BrazeFluidCacheCodec.FloatToHalf(ReadByte(source, sourceOffset, v * 3 + 1) / 255f * s1);
                    destination[dst + 2] = BrazeFluidCacheCodec.FloatToHalf(ReadByte(source, sourceOffset, v * 3 + 2) / 255f * s2);
                    if (!hasVectors) continue;
                    int warpByteBase = sourceOffset + scalarUints;
                    for (int c = 0; c < 3; c++)
                    {
                        int q = ReadByte(source, warpByteBase, v * 3 + c) - 128;
                        destination[dst + 3 + c] = BrazeFluidCacheCodec.FloatToHalf(q / 127f * ws);
                    }
                }
                return;
            }

            int scalarHalfCount = voxels * 3;
            if (!hasVectors)
            {
                for (int v = 0; v < voxels; v++)
                {
                    int dst = destinationOffset + v * 6;
                    destination[dst] = ReadHalf(source, sourceOffset, v * 3);
                    destination[dst + 1] = ReadHalf(source, sourceOffset, v * 3 + 1);
                    destination[dst + 2] = ReadHalf(source, sourceOffset, v * 3 + 2);
                }
                return;
            }

            if (warpQuant)
            {
                int scalarUints = (scalarHalfCount + 1) / 2;
                int warpUints = (voxels * 3 + 3) / 4;
                float scale = UIntFloat(source[sourceOffset + scalarUints + warpUints]);
                for (int v = 0; v < voxels; v++)
                {
                    int dst = destinationOffset + v * 6;
                    destination[dst] = ReadHalf(source, sourceOffset, v * 3);
                    destination[dst + 1] = ReadHalf(source, sourceOffset, v * 3 + 1);
                    destination[dst + 2] = ReadHalf(source, sourceOffset, v * 3 + 2);
                    int warpBase = sourceOffset + scalarUints;
                    for (int c = 0; c < 3; c++)
                    {
                        int q = ReadByte(source, warpBase, v * 3 + c) - 128;
                        destination[dst + 3 + c] = BrazeFluidCacheCodec.FloatToHalf(q / 127f * scale);
                    }
                }
                return;
            }

            for (int i = 0; i < voxels * 6; i++)
                destination[destinationOffset + i] = ReadHalf(source, sourceOffset, i);
        }

        public static BrazeDecodedCacheFrame Blend(BrazeDecodedCacheFrame a, BrazeDecodedCacheFrame b,
            float amount, int halfsPerBrick)
        {
            amount = Mathf.Clamp01(amount);
            var positions = new SortedSet<uint>();
            for (int i = 0; i < a.brickCount; i++) positions.Add(a.indices[i]);
            for (int i = 0; i < b.brickCount; i++) positions.Add(b.indices[i]);
            var mapA = IndexMap(a);
            var mapB = IndexMap(b);
            var result = new BrazeDecodedCacheFrame
            {
                brickCount = positions.Count,
                indices = new uint[Mathf.Max(1, positions.Count)],
                blocks = new ushort[Mathf.Max(1, positions.Count * halfsPerBrick)],
            };
            int outputBrick = 0;
            foreach (uint position in positions)
            {
                result.indices[outputBrick] = position;
                bool haveA = mapA.TryGetValue(position, out int ia);
                bool haveB = mapB.TryGetValue(position, out int ib);
                int da = ia * halfsPerBrick;
                int db = ib * halfsPerBrick;
                int dst = outputBrick * halfsPerBrick;
                bool detailBrick = (position & BrazeFluidCacheFormat.WarpBrickBit) != 0u;
                for (int h = 0; h < halfsPerBrick; h++)
                {
                    // Missing detail bricks mean neutral encoded displacement, not zero (which is maximum negative
                    // warp). Base scalar bricks continue to blend against empty zero as before.
                    float empty = detailBrick && h % BrazeFluidCacheFormat.BaseChannels >= 3 ? 0.5f : 0f;
                    float av = haveA ? BrazeFluidCacheCodec.HalfToFloat(a.blocks[da + h]) : empty;
                    float bv = haveB ? BrazeFluidCacheCodec.HalfToFloat(b.blocks[db + h]) : empty;
                    result.blocks[dst + h] = BrazeFluidCacheCodec.FloatToHalf(Mathf.Lerp(av, bv, amount));
                }
                outputBrick++;
            }
            return result;
        }

        /// <summary>Remove the renderer-only tagged detail bricks for scalar volume interchange. OpenVDB
        /// exports density/temperature/emission on the base grid; feeding native detail-grid indices to that writer
        /// would make them look like out-of-range base bricks.</summary>
        public static BrazeDecodedCacheFrame BaseFieldOnly(BrazeDecodedCacheFrame source, int halfsPerBrick)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int count = 0;
            for (int i = 0; i < source.brickCount; i++)
                if ((source.indices[i] & BrazeFluidCacheFormat.WarpBrickBit) == 0u) count++;
            if (count == source.brickCount) return source;
            var result = new BrazeDecodedCacheFrame
            {
                brickCount = count,
                indices = new uint[Mathf.Max(1, count)],
                blocks = new ushort[Mathf.Max(1, count * halfsPerBrick)],
            };
            int dst = 0;
            for (int i = 0; i < source.brickCount; i++)
            {
                if ((source.indices[i] & BrazeFluidCacheFormat.WarpBrickBit) != 0u) continue;
                result.indices[dst] = source.indices[i];
                Array.Copy(source.blocks, i * halfsPerBrick, result.blocks, dst * halfsPerBrick, halfsPerBrick);
                dst++;
            }
            return result;
        }

        static Dictionary<uint, int> IndexMap(BrazeDecodedCacheFrame frame)
        {
            var result = new Dictionary<uint, int>(frame.brickCount);
            for (int i = 0; i < frame.brickCount; i++) result[frame.indices[i]] = i;
            return result;
        }

        static ushort ReadHalf(uint[] source, int uintOffset, int halfIndex)
        {
            uint packed = source[uintOffset + halfIndex / 2];
            return (ushort)(halfIndex % 2 == 0 ? packed & 0xFFFFu : packed >> 16);
        }

        static byte ReadByte(uint[] source, int uintOffset, int byteIndex)
            => (byte)(source[uintOffset + byteIndex / 4] >> ((byteIndex & 3) * 8));

        static float UIntFloat(uint value) => BitConverter.Int32BitsToSingle((int)value);
    }

    internal static class BrazeCacheLoopUtility
    {
        public enum Derivation { HardCut, PingPong, Crossfade }

        public static int Derive(string sourcePath, string destinationPath, int startFrame, int frameCount,
            Derivation mode, int crossfadeFrames, Func<string, float, bool> progress = null)
        {
            string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using var reader = new BrazeFluidCacheFormat.Reader(sourcePath);
                startFrame = Mathf.Clamp(startFrame, 0, Mathf.Max(0, reader.FrameCount - 1));
                frameCount = Mathf.Clamp(frameCount, 1, reader.FrameCount - startFrame);
                int[] sourceFrames = BuildFrameMap(startFrame, frameCount, mode);
                crossfadeFrames = mode == Derivation.Crossfade
                    ? Mathf.Clamp(crossfadeFrames, 1, Mathf.Max(1, sourceFrames.Length / 2)) : 0;

                var header = reader.header;
                header.flags = BrazeFluidCacheFormat.CacheFlags.RenderReady | BrazeFluidCacheFormat.CacheFlags.Deflate;
                if ((reader.header.flags & BrazeFluidCacheFormat.CacheFlags.DetailFlow) != 0)
                    header.flags |= BrazeFluidCacheFormat.CacheFlags.DetailFlow;
                if ((reader.header.flags & BrazeFluidCacheFormat.CacheFlags.VelocityField) != 0)
                    header.flags |= BrazeFluidCacheFormat.CacheFlags.VelocityField;
                header.frameCount = 0;
                header.indexOffset = 0;
                int[] counts = reader.ReadFrameBrickCounts();
                var cache = new Dictionary<int, BrazeDecodedCacheFrame>();
                BrazeDecodedCacheFrame Get(int frame)
                {
                    if (!cache.TryGetValue(frame, out BrazeDecodedCacheFrame value))
                    {
                        value = BrazeCacheFrameUtility.Decode(reader, frame, counts[frame]);
                        cache[frame] = value;
                    }
                    return value;
                }

                using (var writer = new BrazeFluidCacheFormat.Writer(temporary, header))
                {
                    for (int output = 0; output < sourceFrames.Length; output++)
                    {
                        if (progress != null && !progress($"Writing frame {output + 1}/{sourceFrames.Length}",
                                output / (float)sourceFrames.Length))
                            throw new OperationCanceledException();
                        BrazeDecodedCacheFrame frame = Get(sourceFrames[output]);
                        if (mode == Derivation.Crossfade && output >= sourceFrames.Length - crossfadeFrames)
                        {
                            int blendIndex = output - (sourceFrames.Length - crossfadeFrames);
                            float amount = (blendIndex + 1f) / (crossfadeFrames + 1f);
                            frame = BrazeCacheFrameUtility.Blend(frame, Get(startFrame + blendIndex), amount,
                                header.BaseBlockShorts);
                        }
                        writer.WriteFrame(frame.brickCount, frame.indices, frame.blocks);
                    }
                    writer.Finish();
                }
                BrazeCacheBaker.ValidateAndPublishCache(temporary, destinationPath, sourceFrames.Length);
                return sourceFrames.Length;
            }
            catch
            {
                BrazeCacheBaker.TryDeleteTemporaryCache(temporary, out _);
                throw;
            }
        }

        internal static int[] BuildFrameMap(int startFrame, int frameCount, Derivation mode)
        {
            frameCount = Mathf.Max(1, frameCount);
            if (mode != Derivation.PingPong || frameCount == 1)
            {
                var forward = new int[frameCount];
                for (int i = 0; i < frameCount; i++) forward[i] = startFrame + i;
                return forward;
            }
            int outputCount = frameCount * 2 - 2;
            var result = new int[outputCount];
            for (int i = 0; i < frameCount; i++) result[i] = startFrame + i;
            for (int i = frameCount; i < outputCount; i++) result[i] = startFrame + (outputCount - i);
            return result;
        }

        public static BrazeFluidCacheAsset CreateOrUpdateAsset(string assetPath, string binaryPath,
            string binaryFileField, BrazeFluidCacheAsset sourceLook)
        {
            using var reader = new BrazeFluidCacheFormat.Reader(binaryPath);
            BrazeFluidCacheFormat.Header header = reader.header;
            UnityEngine.Object occupied = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (occupied != null && occupied is not BrazeFluidCacheAsset)
                throw new InvalidOperationException($"'{assetPath}' is occupied by {occupied.GetType().Name}.");
            BrazeFluidCacheAsset asset = occupied as BrazeFluidCacheAsset;
            bool created = asset == null;
            if (created) asset = ScriptableObject.CreateInstance<BrazeFluidCacheAsset>();
            asset.version = BrazeFluidCacheFormat.Version;
            asset.baseRes = header.baseRes;
            asset.warpRes = header.warpRes;
            asset.hasVelocity = BrazeFluidCacheFormat.HasVelocity(header);
            asset.scalarQuantized = (header.flags & BrazeFluidCacheFormat.CacheFlags.ScalarQuant) != 0;
            asset.brickSize = header.brickSize;
            asset.frameCount = header.frameCount;
            asset.fps = header.fps;
            asset.worldSizeAtBake = header.worldSize;
            asset.binaryFile = binaryFileField;
            asset.binaryBytes = new FileInfo(binaryPath).Length;
            if (sourceLook != null)
            {
                asset.bakedLook = sourceLook.bakedLook;
                asset.hasBakedLook = sourceLook.hasBakedLook;
                asset.bakedKeyLightDirection = sourceLook.bakedKeyLightDirection;
                asset.bakedKeyLightColor = sourceLook.bakedKeyLightColor;
                asset.hasBakedKeyLight = sourceLook.hasBakedKeyLight;
            }
            if (created) AssetDatabase.CreateAsset(asset, assetPath);
            BrazeCacheBuildProcessor.EnsureBuildPath(asset, assetPath);
            BrazeCacheBuildProcessor.EnsureBuildFingerprint(asset, binaryPath);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return asset;
        }
    }

    /// <summary>Creates the non-destructive cache required by Ping-Pong and Crossfade delivery. Exporters call this
    /// on demand so a valid master cache is the only preparation step the Export Studio user has to perform.</summary>
    internal static class BrazeDerivedRegionBuilder
    {
        public static bool IsReady(BrazeExportSession session)
        {
            if (session == null || !session.RegionNeedsDerivedCache) return true;
            BrazeFluidCacheAsset cache = session.derivedRegionCache;
            if (cache == null || !cache.IsValid) return false;
            if (!string.Equals(session.derivedRegionFingerprint, Fingerprint(session),
                    StringComparison.Ordinal)) return false;
            string path = BrazeExportCachePreview.ResolveBinaryPath(cache);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static BrazeFluidCacheAsset Ensure(BrazeExportSession session,
            Func<string, float, bool> progress = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.Normalize();
            if (!session.RegionNeedsDerivedCache) return session.cache;
            if (IsReady(session)) return session.derivedRegionCache;

            BrazeFluidCacheAsset master = session.cache;
            if (master == null || !master.IsValid)
                throw new InvalidOperationException("Bake a valid master cache before preparing the Region.");
            string sourcePath = BrazeExportCachePreview.ResolveBinaryPath(master);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("The master cache binary could not be found.", sourcePath);

            string name = session.outputName + "_Region";
            string jobId = session.BeginJob(BrazeExportSession.JobKind.DerivedRegion,
                "Preparing derived Region for export");
            try
            {
                BrazeCacheBakerEditor.ComputeBakePaths(session.cacheOutputFolder, name, false,
                    out string binaryPath, out string assetPath, out string binaryFile);
                BrazeCacheLoopUtility.Derivation derivation = session.regionPlayback switch
                {
                    BrazeExportSession.RegionPlayback.PingPong => BrazeCacheLoopUtility.Derivation.PingPong,
                    BrazeExportSession.RegionPlayback.CrossfadeLoop => BrazeCacheLoopUtility.Derivation.Crossfade,
                    _ => BrazeCacheLoopUtility.Derivation.HardCut,
                };
                int crossfadeFrames = Mathf.Max(1,
                    Mathf.RoundToInt(session.crossfadeDuration * session.BakeFps));
                BrazeCacheLoopUtility.Derive(sourcePath, binaryPath, session.RegionStartFrame,
                    session.RegionFrameCount, derivation, crossfadeFrames, progress);
                BrazeFluidCacheAsset asset = BrazeCacheLoopUtility.CreateOrUpdateAsset(assetPath, binaryPath,
                    binaryFile, master);
                session.derivedRegionCache = asset;
                session.derivedRegionFingerprint = Fingerprint(session);
                session.Persist();
                session.FinishJob(jobId, BrazeExportSession.JobState.Completed, "Derived Region ready for export");
                return asset;
            }
            catch (OperationCanceledException)
            {
                session.FinishJob(jobId, BrazeExportSession.JobState.Cancelled, "Region preparation cancelled");
                throw;
            }
            catch (Exception e)
            {
                session.FinishJob(jobId, BrazeExportSession.JobState.Failed, "Region preparation failed", e.Message);
                throw;
            }
        }

        static string Fingerprint(BrazeExportSession session)
            => $"{BrazeExportFingerprint.CacheFingerprint(session.cache)}|{session.RegionStartFrame}|" +
               $"{session.RegionFrameCount}|{(int)session.regionPlayback}|" +
               Mathf.Max(1, Mathf.RoundToInt(session.crossfadeDuration * session.BakeFps));
    }
}
