using System;
using System.IO;
using System.Threading;
using BrazeFX.EditorTools.Vdb;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    internal static class BrazeVdbTransformUtility
    {
        public static Vector3 VoxelSize(Vector3 worldSize, Vector3Int resolution) => new(
            worldSize.x / Mathf.Max(1, resolution.x),
            worldSize.y / Mathf.Max(1, resolution.y),
            worldSize.z / Mathf.Max(1, resolution.z));

        public static Vector3 IndexOrigin(Vector3 worldSize, Vector3Int resolution)
        {
            Vector3 voxel = VoxelSize(worldSize, resolution);
            return worldSize * -0.5f + voxel * 0.5f;
        }
    }

    /// <summary>Stable Editor facade for dependency-free managed OpenVDB interchange.</summary>
    internal static class BrazeVdbBridge
    {
        internal struct FrameInfo
        {
            public double voxelX, voxelY, voxelZ;
            public double originX, originY, originZ;
            public long activeVoxelCount;
            public int resolutionX, resolutionY, resolutionZ;
            public int minX, minY, minZ;
            public int gridMask;
            public int isBraze;
            public int sourceFrame;
            public float worldX, worldY, worldZ;
            public float sourceTime;
            public float fps;
            public float densityGain;
            public float temperatureGain;
            public float emissionGain;

            public Vector3Int Resolution => new(resolutionX, resolutionY, resolutionZ);
            public Vector3Int Minimum => new(minX, minY, minZ);
            public Vector3 WorldSize => new(worldX, worldY, worldZ);
            public Vector3 VoxelSize => new((float)voxelX, (float)voxelY, (float)voxelZ);
            public Vector3 Origin => new((float)originX, (float)originY, (float)originZ);
            public bool IsBraze => isBraze != 0;
        }

        internal static bool IsSupportedEditorPlatform(RuntimePlatform platform)
            => platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.WindowsEditor;

        public static bool TryGetCapability(out string detail)
        {
            if (!IsSupportedEditorPlatform(Application.platform))
            {
                detail = "Managed OpenVDB import/export is currently qualified for macOS and Windows Unity Editors.";
                return false;
            }
            detail = "Managed OpenVDB import/export ready";
            return true;
        }

        public static void WriteFrame(string path, in BrazeFluidCacheFormat.Header header,
            BrazeDecodedCacheFrame frame, BrazeExportSession.VolumeGrids grids, float threshold,
            int sourceFrame, float sourceTime, float fps, string units, string sourceName,
            float densityGain, float temperatureGain, float emissionGain,
            CancellationToken cancellationToken = default)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            frame = BrazeCacheFrameUtility.BaseFieldOnly(frame, header.BaseBlockShorts);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("VDB output path is empty.", nameof(path));
            cancellationToken.ThrowIfCancellationRequested();
            string uuid = CreateFrameUuid(header, frame, grids, threshold, sourceFrame, sourceTime, fps, units,
                sourceName, densityGain, temperatureGain, emissionGain);
            BrazeOpenVdbStreamingDocument document = BrazeVdbFrameBuilder.BuildStreaming(uuid, header, frame,
                grids, threshold, sourceFrame, sourceTime, fps, units, sourceName, densityGain, temperatureGain,
                emissionGain, cancellationToken);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.SequentialScan);
            // Uncompressed format 224 remains the broadest DCC default until the licensed C4D/Unreal matrix closes.
            BrazeOpenVdbWriter.Write(stream, document, BrazeVdbWriteCompression.None, cancellationToken);
            stream.Flush(true);
        }

        internal static FrameInfo ReadFrameInfo(string path, string densityName = "density",
            string temperatureName = "temperature", string emissionName = "emission", string velocityName = "vel")
        {
            using var stream = OpenManagedRead(path);
            BrazeOpenVdbFrameInfo managed = BrazeOpenVdbReader.ReadFrameInfo(stream,
                densityName ?? string.Empty, temperatureName ?? string.Empty, emissionName ?? string.Empty,
                velocityName ?? string.Empty);
            return ToFacadeInfo(managed);
        }

        internal static BrazeDecodedCacheFrame ReadSparseFrame(string path, in FrameInfo info,
            float densityScale, float temperatureScale, float emissionScale, int brickSize = 4,
            string densityName = "density", string temperatureName = "temperature", string emissionName = "emission",
            string velocityName = "vel", CancellationToken cancellationToken = default)
        {
            if (brickSize != BrazeFluidCacheFormat.DefaultBrickSize)
                throw new NotSupportedException("Managed OpenVDB import requires 4x4x4 cache bricks.");
            using var stream = OpenManagedRead(path);
            BrazeOpenVdbFrameInfo actual = BrazeOpenVdbReader.ReadFrameInfo(stream,
                densityName ?? string.Empty, temperatureName ?? string.Empty, emissionName ?? string.Empty,
                velocityName ?? string.Empty);
            BrazeOpenVdbFrameInfo expected = BuildManagedExpectedInfo(info, actual);
            bool domainOverride = !expected.resolution.Equals(actual.resolution) ||
                                  !expected.minimum.Equals(actual.minimum) ||
                                  !expected.worldSize.Equals(actual.worldSize);
            return BrazeOpenVdbReader.ReadSparseFrame(stream, expected, Mathf.Max(0f, densityScale),
                Mathf.Max(0f, temperatureScale), Mathf.Max(0f, emissionScale), densityName ?? string.Empty,
                temperatureName ?? string.Empty, emissionName ?? string.Empty, velocityName ?? string.Empty,
                cancellationToken: cancellationToken, allowDomainOverride: domainOverride);
        }

        static FileStream OpenManagedRead(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("VDB input path is empty.", nameof(path));
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.RandomAccess);
        }

        static FrameInfo ToFacadeInfo(in BrazeOpenVdbFrameInfo value) => new()
        {
            voxelX = value.transform.voxelSize.x,
            voxelY = value.transform.voxelSize.y,
            voxelZ = value.transform.voxelSize.z,
            originX = value.transform.origin.x,
            originY = value.transform.origin.y,
            originZ = value.transform.origin.z,
            activeVoxelCount = value.activeVoxelCount,
            resolutionX = value.resolution.x,
            resolutionY = value.resolution.y,
            resolutionZ = value.resolution.z,
            minX = value.minimum.x,
            minY = value.minimum.y,
            minZ = value.minimum.z,
            gridMask = value.gridMask,
            isBraze = value.isBraze ? 1 : 0,
            sourceFrame = value.sourceFrame,
            worldX = (float)value.worldSize.x,
            worldY = (float)value.worldSize.y,
            worldZ = (float)value.worldSize.z,
            sourceTime = value.sourceTime,
            fps = value.fps,
            densityGain = value.densityGain,
            temperatureGain = value.temperatureGain,
            emissionGain = value.emissionGain,
        };

        static BrazeOpenVdbFrameInfo BuildManagedExpectedInfo(in FrameInfo expected,
            in BrazeOpenVdbFrameInfo actual)
        {
            if (expected.resolutionX <= 0 || expected.resolutionY <= 0 || expected.resolutionZ <= 0)
                throw new ArgumentException("VDB frame dimensions are invalid.", nameof(expected));
            if (expected.voxelX != actual.transform.voxelSize.x ||
                expected.voxelY != actual.transform.voxelSize.y ||
                expected.voxelZ != actual.transform.voxelSize.z ||
                expected.originX != actual.transform.origin.x || expected.originY != actual.transform.origin.y ||
                expected.originZ != actual.transform.origin.z || expected.activeVoxelCount != actual.activeVoxelCount ||
                expected.gridMask != actual.gridMask || expected.sourceFrame != actual.sourceFrame ||
                !expected.sourceTime.Equals(actual.sourceTime) || !expected.fps.Equals(actual.fps))
                throw new InvalidDataException("OpenVDB frame information changed between inspection and sparse read.");

            var resolution = new BrazeVdbVector3Int(expected.resolutionX, expected.resolutionY,
                expected.resolutionZ);
            var minimum = new BrazeVdbVector3Int(expected.minX, expected.minY, expected.minZ);
            bool sameIndexDomain = resolution.Equals(actual.resolution) && minimum.Equals(actual.minimum);
            BrazeVdbVector3d worldSize = sameIndexDomain
                ? actual.worldSize
                : actual.transform.WorldSize(resolution);
            Vector3 facadeWorld = expected.WorldSize;
            if (Math.Abs(facadeWorld.x - worldSize.x) > 1e-5 ||
                Math.Abs(facadeWorld.y - worldSize.y) > 1e-5 ||
                Math.Abs(facadeWorld.z - worldSize.z) > 1e-5)
                throw new InvalidDataException("OpenVDB frame world size changed between inspection and sparse read.");
            return new BrazeOpenVdbFrameInfo(resolution, minimum, worldSize, actual.transform,
                actual.activeVoxelCount, actual.gridMask, expected.IsBraze, actual.sourceFrame, actual.sourceTime,
                actual.fps, actual.densityGain, actual.temperatureGain, actual.emissionGain);
        }

        static string CreateFrameUuid(in BrazeFluidCacheFormat.Header header, BrazeDecodedCacheFrame frame,
            BrazeExportSession.VolumeGrids grids, float threshold, int sourceFrame, float sourceTime, float fps,
            string units, string sourceName, float densityGain, float temperatureGain, float emissionGain)
        {
            long requiredBlocks = frame == null || frame.brickCount < 0
                ? -1
                : (long)frame.brickCount * header.BaseBlockShorts;
            if (frame == null || frame.brickCount < 0 || frame.indices == null || frame.blocks == null ||
                frame.indices.Length < frame.brickCount || requiredBlocks < 0 ||
                requiredBlocks > frame.blocks.LongLength || requiredBlocks > int.MaxValue)
                throw new InvalidDataException("The decoded Braze frame buffers are truncated or inconsistent.");
            ulong first = 14695981039346656037UL;
            ulong second = 0x9E3779B97F4A7C15UL;

            void Mix(uint value)
            {
                unchecked
                {
                    first ^= value;
                    first *= 1099511628211UL;
                    second ^= value + 0x9E3779B9UL + (second << 6) + (second >> 2);
                    second = (second << 27) | (second >> 37);
                    second *= 0x94D049BB133111EBUL;
                }
            }

            void MixFloat(float value) => Mix(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
            void MixString(string value)
            {
                value ??= string.Empty;
                Mix((uint)value.Length);
                for (int index = 0; index < value.Length; index++) Mix(value[index]);
            }

            Mix((uint)header.baseRes.x);
            Mix((uint)header.baseRes.y);
            Mix((uint)header.baseRes.z);
            MixFloat(header.worldSize.x);
            MixFloat(header.worldSize.y);
            MixFloat(header.worldSize.z);
            Mix((uint)header.brickSize);
            Mix((uint)frame.brickCount);
            Mix((uint)grids);
            MixFloat(threshold);
            Mix((uint)sourceFrame);
            MixFloat(sourceTime);
            MixFloat(fps);
            MixFloat(densityGain);
            MixFloat(temperatureGain);
            MixFloat(emissionGain);
            MixString(string.IsNullOrWhiteSpace(units) ? "meters" : units);
            MixString(string.IsNullOrWhiteSpace(sourceName) ? "BrazeFX" : sourceName);
            for (int index = 0; index < frame.brickCount; index++) Mix(frame.indices[index]);
            int blockCount = (int)requiredBlocks;
            for (int index = 0; index < blockCount; index++) Mix(frame.blocks[index]);

            short version = (short)(((first >> 48) & 0x0FFFUL) | 0x5000UL);
            byte variant = (byte)(((second >> 56) & 0x3FUL) | 0x80UL);
            var uuid = new Guid(unchecked((int)first), unchecked((short)(first >> 32)), version,
                variant, (byte)(second >> 48), (byte)(second >> 40), (byte)(second >> 32),
                (byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second);
            return uuid.ToString("D").ToUpperInvariant();
        }

    }
}
