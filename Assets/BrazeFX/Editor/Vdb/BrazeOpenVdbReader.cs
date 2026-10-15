using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace BrazeFX.EditorTools.Vdb
{
    internal sealed class BrazeOpenVdbGridDescriptor
    {
        public string Name { get; }
        public string TreeType { get; }
        public long GridPosition { get; }
        public long BlockPosition { get; }
        public long EndPosition { get; }

        public BrazeOpenVdbGridDescriptor(string name, string treeType, long gridPosition, long blockPosition,
            long endPosition)
        {
            Name = name;
            TreeType = treeType;
            GridPosition = gridPosition;
            BlockPosition = blockPosition;
            EndPosition = endPosition;
        }
    }

    internal sealed class BrazeOpenVdbCatalog
    {
        public uint FileVersion { get; }
        public uint LibraryMajor { get; }
        public uint LibraryMinor { get; }
        public string Uuid { get; }
        public BrazeVdbMetadataMap Metadata { get; }
        public IReadOnlyList<BrazeOpenVdbGridDescriptor> Grids { get; }

        public BrazeOpenVdbCatalog(uint fileVersion, uint libraryMajor, uint libraryMinor, string uuid,
            BrazeVdbMetadataMap metadata, IReadOnlyList<BrazeOpenVdbGridDescriptor> grids)
        {
            FileVersion = fileVersion;
            LibraryMajor = libraryMajor;
            LibraryMinor = libraryMinor;
            Uuid = uuid;
            Metadata = metadata;
            Grids = grids;
        }
    }

    internal readonly struct BrazeOpenVdbFrameInfo
    {
        public readonly BrazeVdbVector3Int resolution;
        public readonly BrazeVdbVector3Int minimum;
        public readonly BrazeVdbVector3d worldSize;
        public readonly BrazeVdbTransform transform;
        public readonly long activeVoxelCount;
        public readonly int gridMask;
        public readonly bool isBraze;
        public readonly int sourceFrame;
        public readonly float sourceTime;
        public readonly float fps;
        public readonly float densityGain;
        public readonly float temperatureGain;
        public readonly float emissionGain;

        public BrazeOpenVdbFrameInfo(BrazeVdbVector3Int resolution, BrazeVdbVector3Int minimum,
            BrazeVdbVector3d worldSize, BrazeVdbTransform transform, long activeVoxelCount, int gridMask,
            bool isBraze, int sourceFrame, float sourceTime, float fps, float densityGain, float temperatureGain,
            float emissionGain)
        {
            this.resolution = resolution;
            this.minimum = minimum;
            this.worldSize = worldSize;
            this.transform = transform;
            this.activeVoxelCount = activeVoxelCount;
            this.gridMask = gridMask;
            this.isBraze = isBraze;
            this.sourceFrame = sourceFrame;
            this.sourceTime = sourceTime;
            this.fps = fps;
            this.densityGain = densityGain;
            this.temperatureGain = temperatureGain;
            this.emissionGain = emissionGain;
        }
    }

    /// <summary>Bounded clean-room reader for the documented BrazeFX OpenVDB interchange profile.</summary>
    internal static class BrazeOpenVdbReader
    {
        const int DensityBit = 1;
        const int TemperatureBit = 2;
        const int EmissionBit = 4;
        const int VelocityBit = 8;
        const byte NoMaskOrInactiveValuesMode = 0;
        const byte AllValuesMode = 6;

        sealed class GridHeader
        {
            public BrazeOpenVdbGridDescriptor descriptor;
            public uint compression;
            public BrazeVdbMetadataMap metadata;
            public BrazeVdbTransform transform;
            public long topologyPosition;
            public int channel;
            public int mask;
            public bool vector;
            public bool halfVector;
        }

        sealed class LeafRecord
        {
            public readonly BrazeVdbCoord origin;
            public readonly BrazeVdbMask activeMask;

            public LeafRecord(BrazeVdbCoord origin, BrazeVdbMask activeMask)
            {
                this.origin = origin;
                this.activeMask = activeMask;
            }
        }

        sealed class GridReadBudget
        {
            public int nodeCount;
            public long activeCount;
            public int minimumX = int.MaxValue;
            public int minimumY = int.MaxValue;
            public int minimumZ = int.MaxValue;
            public int maximumX = int.MinValue;
            public int maximumY = int.MinValue;
            public int maximumZ = int.MinValue;

            public void Include(int x, int y, int z)
            {
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                minimumZ = Math.Min(minimumZ, z);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
                maximumZ = Math.Max(maximumZ, z);
            }

            public void IncludeRange(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
            {
                Include(minX, minY, minZ);
                Include(maxX, maxY, maxZ);
            }
        }

        readonly struct Value
        {
            public readonly float x;
            public readonly float y;
            public readonly float z;

            public Value(float x, float y = 0f, float z = 0f)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }
        }

        sealed class BrickAccumulator : IDisposable
        {
            const int Channels = BrazeFluidCacheFormat.BaseChannels;
            readonly BrazeOpenVdbFrameInfo info;
            readonly BrazeVdbSafetyLimits limits;
            readonly int gridX;
            readonly int gridY;
            readonly int gridZ;
            readonly int shortsPerBrick;
            readonly int maximumBricks;
            readonly Dictionary<uint, int> indices = new();
            ushort[] blocks;
            int capacity;
            bool disposed;

            public int BrickCount => indices.Count;

            public BrickAccumulator(in BrazeOpenVdbFrameInfo info, BrazeVdbSafetyLimits limits)
            {
                this.info = info;
                this.limits = limits;
                gridX = CheckedCeilDiv(info.resolution.x, 4, "OpenVDB brick grid X");
                gridY = CheckedCeilDiv(info.resolution.y, 4, "OpenVDB brick grid Y");
                gridZ = CheckedCeilDiv(info.resolution.z, 4, "OpenVDB brick grid Z");
                long maximum = CheckedProduct(gridX, gridY, gridZ, "OpenVDB brick capacity");
                if (maximum > int.MaxValue)
                    throw new InvalidDataException("The OpenVDB domain exceeds the managed brick-count limit.");
                maximumBricks = (int)maximum;
                shortsPerBrick = checked(4 * 4 * 4 * Channels);
                capacity = Math.Min(64, maximumBricks);
                if (capacity > 0) blocks = ArrayPool<ushort>.Shared.Rent(checked(capacity * shortsPerBrick));
            }

            public void Set(BrazeVdbCoord coordinate, int channel, Value value, float scale)
            {
                int x = checked(coordinate.x - info.minimum.x);
                int y = checked(coordinate.y - info.minimum.y);
                int z = checked(coordinate.z - info.minimum.z);
                if ((uint)x >= (uint)info.resolution.x || (uint)y >= (uint)info.resolution.y ||
                    (uint)z >= (uint)info.resolution.z)
                    return;
                int brickX = x >> 2;
                int brickY = y >> 2;
                int brickZ = z >> 2;
                long flatLong = checked((long)brickX + (long)brickY * gridX + (long)brickZ * gridX * gridY);
                if (flatLong < 0 || flatLong > uint.MaxValue)
                    throw new InvalidDataException("The OpenVDB brick index exceeds the cache format range.");
                uint flat = (uint)flatLong;
                if (!indices.TryGetValue(flat, out int brick))
                {
                    brick = indices.Count;
                    if (brick >= maximumBricks)
                        throw new InvalidDataException("The OpenVDB frame exceeds its finite brick domain.");
                    EnsureCapacity(brick + 1);
                    indices.Add(flat, brick);
                    Array.Clear(blocks, brick * shortsPerBrick, shortsPerBrick);
                }

                int local = (x & 3) + (y & 3) * 4 + (z & 3) * 16;
                int offset = checked(brick * shortsPerBrick + local * Channels + channel);
                if (channel == 3)
                {
                    StoreHalf(offset, value.x / (float)info.transform.voxelSize.x);
                    StoreHalf(offset + 1, value.y / (float)info.transform.voxelSize.y);
                    StoreHalf(offset + 2, value.z / (float)info.transform.voxelSize.z);
                }
                else
                {
                    StoreHalf(offset, value.x * scale);
                }
            }

            public BrazeDecodedCacheFrame Finish()
            {
                var keys = new List<uint>(indices.Keys);
                keys.Sort();
                var outputIndices = new uint[keys.Count];
                var outputBlocks = new ushort[checked(keys.Count * shortsPerBrick)];
                for (int output = 0; output < keys.Count; output++)
                {
                    uint key = keys[output];
                    outputIndices[output] = key;
                    int source = indices[key];
                    Array.Copy(blocks, source * shortsPerBrick, outputBlocks, output * shortsPerBrick,
                        shortsPerBrick);
                }
                return new BrazeDecodedCacheFrame
                {
                    brickCount = keys.Count,
                    indices = outputIndices,
                    blocks = outputBlocks,
                };
            }

            void EnsureCapacity(int required)
            {
                if (required <= capacity) return;
                int next = capacity == 0 ? 1 : capacity;
                while (next < required)
                {
                    int doubled = next > maximumBricks / 2 ? maximumBricks : next * 2;
                    if (doubled <= next) throw new InvalidDataException("The OpenVDB brick capacity overflowed.");
                    next = doubled;
                }
                long bytes = checked((long)next * shortsPerBrick * sizeof(ushort));
                if (bytes > limits.MaxAllocationBytes)
                    throw new InvalidDataException("The OpenVDB sparse brick buffer exceeds the allocation ceiling.");
                ushort[] replacement = ArrayPool<ushort>.Shared.Rent(checked(next * shortsPerBrick));
                if (blocks != null)
                {
                    Array.Copy(blocks, replacement, checked(indices.Count * shortsPerBrick));
                    ArrayPool<ushort>.Shared.Return(blocks, true);
                }
                blocks = replacement;
                capacity = next;
            }

            void StoreHalf(int offset, float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidDataException("An imported OpenVDB value exceeds the finite cache range.");
                ushort half = BrazeFluidCacheCodec.FloatToHalf(value);
                float roundTrip = BrazeFluidCacheCodec.HalfToFloat(half);
                if (float.IsNaN(roundTrip) || float.IsInfinity(roundTrip))
                    throw new InvalidDataException("An imported OpenVDB value exceeds binary16 cache range.");
                blocks[offset] = half;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (blocks != null) ArrayPool<ushort>.Shared.Return(blocks, true);
                blocks = null;
            }
        }

        public static BrazeOpenVdbCatalog Inspect(Stream stream, BrazeVdbSafetyLimits limits = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            limits ??= BrazeVdbSafetyLimits.Default;
            using var reader = new BrazeVdbBinaryReader(stream, limits, true);
            reader.SeekAbsolute(0, "OpenVDB header");
            if (reader.ReadUInt64("OpenVDB magic") != BrazeVdbProfile.Magic)
                throw new InvalidDataException("The file does not contain the OpenVDB magic value.");
            uint fileVersion = reader.ReadUInt32("OpenVDB file version");
            if (fileVersion != BrazeVdbProfile.WriterFileVersion &&
                fileVersion != BrazeVdbProfile.NativeHalfVectorFileVersion)
                throw new NotSupportedException(
                    $"OpenVDB file format {fileVersion} is unsupported; BrazeFX accepts 224 and 225 only.");
            uint libraryMajor = reader.ReadUInt32("OpenVDB library major");
            uint libraryMinor = reader.ReadUInt32("OpenVDB library minor");
            if (libraryMajor == 0)
                throw new InvalidDataException("The OpenVDB library compatibility version is invalid.");
            if (!reader.ReadBoolean("OpenVDB grid-offset flag"))
                throw new NotSupportedException("OpenVDB files without seekable grid offsets are unsupported.");
            string uuid = reader.ReadUtf8(36, "OpenVDB UUID");
            if (!Guid.TryParseExact(uuid, "D", out _))
                throw new InvalidDataException("The OpenVDB UUID is not valid D-format text.");
            BrazeVdbMetadataMap metadata = BrazeVdbMetadataMap.Read(reader);
            int gridCount = reader.ReadCount32(limits.MaxGridCount, "OpenVDB grid count");
            if (gridCount == 0) throw new InvalidDataException("The OpenVDB file contains no grids.");
            var grids = new List<BrazeOpenVdbGridDescriptor>(gridCount);
            var names = new HashSet<string>(StringComparer.Ordinal);
            long previousEnd = reader.Position;
            for (int i = 0; i < gridCount; i++)
            {
                if (i > 0 && reader.Position != previousEnd)
                    throw new InvalidDataException("OpenVDB grid descriptors overlap or contain a gap.");
                string name = reader.ReadString($"OpenVDB grid {i} name");
                if (string.IsNullOrEmpty(name) || !names.Add(name))
                    throw new InvalidDataException($"Duplicate or empty OpenVDB grid name '{name}'.");
                string treeType = reader.ReadString($"OpenVDB grid '{name}' tree type");
                string parent = reader.ReadString($"OpenVDB grid '{name}' instance parent");
                if (!string.IsNullOrEmpty(parent))
                    throw new NotSupportedException($"OpenVDB grid instancing is unsupported (grid '{name}').");
                long gridPosition = reader.ReadInt64($"OpenVDB grid '{name}' grid position");
                long blockPosition = reader.ReadInt64($"OpenVDB grid '{name}' block position");
                long endPosition = reader.ReadInt64($"OpenVDB grid '{name}' end position");
                if (gridPosition != reader.Position)
                    throw new InvalidDataException($"OpenVDB grid '{name}' has an invalid grid offset.");
                reader.ValidateOffset(blockPosition, $"OpenVDB grid '{name}' block position", gridPosition + 1);
                reader.ValidateOffset(endPosition, $"OpenVDB grid '{name}' end position", blockPosition);
                if (i < gridCount - 1 && endPosition >= reader.Length)
                    throw new InvalidDataException($"OpenVDB grid '{name}' consumes the remaining descriptors.");
                grids.Add(new BrazeOpenVdbGridDescriptor(name, treeType, gridPosition, blockPosition, endPosition));
                previousEnd = endPosition;
                reader.SeekAbsolute(endPosition, $"OpenVDB grid '{name}' end", gridPosition);
            }
            if (reader.Position != reader.Length)
                throw new InvalidDataException($"The OpenVDB file has {reader.Remaining} trailing bytes.");
            return new BrazeOpenVdbCatalog(fileVersion, libraryMajor, libraryMinor, uuid, metadata, grids);
        }

        public static BrazeOpenVdbFrameInfo ReadFrameInfo(Stream stream, string densityName = "density",
            string temperatureName = "temperature", string emissionName = "emission", string velocityName = "vel",
            BrazeVdbSafetyLimits limits = null)
        {
            limits ??= BrazeVdbSafetyLimits.Default;
            BrazeOpenVdbCatalog catalog = Inspect(stream, limits);
            List<GridHeader> selected = SelectGridHeaders(stream, catalog, densityName, temperatureName,
                emissionName, velocityName, limits);
            return BuildFrameInfo(catalog, selected, limits);
        }

        public static BrazeDecodedCacheFrame ReadSparseFrame(Stream stream, in BrazeOpenVdbFrameInfo expectedInfo,
            float densityScale = 1f, float temperatureScale = 1f, float emissionScale = 1f,
            string densityName = "density", string temperatureName = "temperature", string emissionName = "emission",
            string velocityName = "vel", BrazeVdbSafetyLimits limits = null,
            CancellationToken cancellationToken = default, bool allowDomainOverride = false)
        {
            ValidateScale(densityScale, nameof(densityScale));
            ValidateScale(temperatureScale, nameof(temperatureScale));
            ValidateScale(emissionScale, nameof(emissionScale));
            limits ??= BrazeVdbSafetyLimits.Default;
            cancellationToken.ThrowIfCancellationRequested();
            BrazeOpenVdbCatalog catalog = Inspect(stream, limits);
            List<GridHeader> selected = SelectGridHeaders(stream, catalog, densityName, temperatureName,
                emissionName, velocityName, limits);
            BrazeOpenVdbFrameInfo actualInfo = BuildFrameInfo(catalog, selected, limits);
            BrazeOpenVdbFrameInfo outputInfo = ResolveOutputInfo(expectedInfo, actualInfo, allowDomainOverride);
            using var bricks = new BrickAccumulator(outputInfo, limits);
            using var reader = new BrazeVdbBinaryReader(stream, limits, true);
            for (int i = 0; i < selected.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GridHeader grid = selected[i];
                float scale = grid.channel switch
                {
                    0 => densityScale,
                    1 => temperatureScale,
                    2 => emissionScale,
                    _ => 1f,
                };
                ReadGrid(reader, catalog.FileVersion, grid, outputInfo, bricks, scale, limits,
                    cancellationToken);
            }
            return bricks.Finish();
        }

        static List<GridHeader> SelectGridHeaders(Stream stream, BrazeOpenVdbCatalog catalog,
            string densityName, string temperatureName, string emissionName, string velocityName,
            BrazeVdbSafetyLimits limits)
        {
            var requests = new Dictionary<string, (int channel, int mask, bool vector)>(StringComparer.Ordinal);
            AddRequest(requests, densityName, 0, DensityBit, false);
            AddRequest(requests, temperatureName, 1, TemperatureBit, false);
            AddRequest(requests, emissionName, 2, EmissionBit, false);
            AddRequest(requests, velocityName, 3, VelocityBit, true);
            var selected = new List<GridHeader>();
            using var reader = new BrazeVdbBinaryReader(stream, limits, true);
            for (int i = 0; i < catalog.Grids.Count; i++)
            {
                BrazeOpenVdbGridDescriptor descriptor = catalog.Grids[i];
                if (!requests.TryGetValue(descriptor.Name, out var request)) continue;
                if (!BrazeVdbProfile.IsSupportedReaderType(catalog.FileVersion, descriptor.TreeType))
                    throw new NotSupportedException(
                        $"OpenVDB grid '{descriptor.Name}' uses unsupported tree type '{descriptor.TreeType}'.");
                bool vector = descriptor.TreeType == BrazeVdbProfile.VectorTreeType ||
                              descriptor.TreeType == BrazeVdbProfile.HalfVectorTreeType;
                if (vector != request.vector)
                    throw new InvalidDataException(
                        $"OpenVDB grid '{descriptor.Name}' does not match its selected scalar/vector channel.");
                reader.SeekAbsolute(descriptor.GridPosition, $"OpenVDB grid '{descriptor.Name}' body");
                uint compression = reader.ReadUInt32($"OpenVDB grid '{descriptor.Name}' compression");
                ValidateCompression(descriptor.Name, compression);
                BrazeVdbMetadataMap metadata = BrazeVdbMetadataMap.Read(reader);
                BrazeVdbTransform transform = BrazeVdbTransform.Read(reader);
                if (reader.Position > descriptor.BlockPosition)
                    throw new InvalidDataException(
                        $"OpenVDB grid '{descriptor.Name}' metadata/transform overlaps its leaf block.");
                selected.Add(new GridHeader
                {
                    descriptor = descriptor,
                    compression = compression,
                    metadata = metadata,
                    transform = transform,
                    topologyPosition = reader.Position,
                    channel = request.channel,
                    mask = request.mask,
                    vector = vector,
                    halfVector = descriptor.TreeType == BrazeVdbProfile.HalfVectorTreeType,
                });
            }
            if (selected.Count == 0)
                throw new InvalidDataException("The OpenVDB file contains none of the selected grids.");
            selected.Sort((left, right) => left.channel.CompareTo(right.channel));
            return selected;
        }

        static BrazeOpenVdbFrameInfo BuildFrameInfo(BrazeOpenVdbCatalog catalog, List<GridHeader> selected,
            BrazeVdbSafetyLimits limits)
        {
            bool anyBrazeMetadata = false;
            bool allBrazeMetadata = true;
            for (int i = 0; i < selected.Count; i++)
            {
                GridHeader grid = selected[i];
                ValidateStandardGridMetadata(catalog.FileVersion, grid);
                bool hasBrazeMetadata = HasBrazeDomainMetadata(grid.metadata);
                anyBrazeMetadata |= hasBrazeMetadata;
                allBrazeMetadata &= hasBrazeMetadata;
            }
            if (anyBrazeMetadata && !allBrazeMetadata)
                throw new InvalidDataException(
                    "Selected OpenVDB grids mix Braze domain metadata with generic DCC metadata.");
            return anyBrazeMetadata
                ? BuildBrazeFrameInfo(selected, limits)
                : BuildDccFrameInfo(selected, limits);
        }

        static BrazeOpenVdbFrameInfo BuildBrazeFrameInfo(List<GridHeader> selected, BrazeVdbSafetyLimits limits)
        {
            GridHeader first = selected[0];
            BrazeVdbVector3Int resolution = ReadResolution(first.metadata, first.descriptor.Name);
            BrazeVdbVector3d worldSize = ReadWorldSize(first.metadata, first.descriptor.Name);
            BrazeVdbTransform expectedTransform = BrazeVdbTransform.FromCenteredDomain(worldSize, resolution);
            if (!expectedTransform.Equals(first.transform))
                throw new InvalidDataException(
                    $"OpenVDB grid '{first.descriptor.Name}' transform conflicts with its Braze domain metadata.");
            int sourceFrame = RequireInt32(first.metadata, "braze:source_frame", first.descriptor.Name);
            float sourceTime = RequireFiniteFloat(first.metadata, "braze:source_time", first.descriptor.Name);
            float fps = RequireFinitePositiveFloat(first.metadata, "braze:fps", first.descriptor.Name);
            long activeCount = 0;
            int gridMask = 0;
            float densityGain = 1f;
            float temperatureGain = 1f;
            float emissionGain = 1f;
            for (int i = 0; i < selected.Count; i++)
            {
                GridHeader grid = selected[i];
                ValidateBrazeGridMetadata(grid);
                if (!grid.transform.Equals(first.transform) || !ReadResolution(grid.metadata, grid.descriptor.Name)
                        .Equals(resolution) || !ReadWorldSize(grid.metadata, grid.descriptor.Name).Equals(worldSize))
                    throw new InvalidDataException("Selected OpenVDB grids do not share one domain and transform.");
                if (RequireInt32(grid.metadata, "braze:source_frame", grid.descriptor.Name) != sourceFrame ||
                    !RequireFiniteFloat(grid.metadata, "braze:source_time", grid.descriptor.Name).Equals(sourceTime) ||
                    !RequireFinitePositiveFloat(grid.metadata, "braze:fps", grid.descriptor.Name).Equals(fps))
                    throw new InvalidDataException("Selected OpenVDB grids do not describe one source frame.");
                long gridCount = RequireInt64(grid.metadata, "file_voxel_count", grid.descriptor.Name);
                if (gridCount < 0 || gridCount > limits.MaxActiveVoxelCount - activeCount)
                    throw new InvalidDataException("The selected OpenVDB grids exceed the active-voxel ceiling.");
                activeCount += gridCount;
                gridMask |= grid.mask;
                if (grid.channel == 0) densityGain = OptionalGain(grid.metadata);
                if (grid.channel == 1) temperatureGain = OptionalGain(grid.metadata);
                if (grid.channel == 2) emissionGain = OptionalGain(grid.metadata);
            }
            return new BrazeOpenVdbFrameInfo(resolution, new BrazeVdbVector3Int(0, 0, 0), worldSize,
                first.transform, activeCount, gridMask, true, sourceFrame, sourceTime, fps, densityGain,
                temperatureGain, emissionGain);
        }

        static BrazeOpenVdbFrameInfo BuildDccFrameInfo(List<GridHeader> selected, BrazeVdbSafetyLimits limits)
        {
            GridHeader first = selected[0];
            var minimum = new BrazeVdbVector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            var maximum = new BrazeVdbVector3Int(int.MinValue, int.MinValue, int.MinValue);
            bool hasDomain = false;
            long activeCount = 0;
            int gridMask = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                GridHeader grid = selected[i];
                if (!grid.transform.Equals(first.transform))
                    throw new InvalidDataException(
                        "Selected generic OpenVDB grids do not share one positive axis-aligned transform.");
                long gridCount = RequireInt64(grid.metadata, "file_voxel_count", grid.descriptor.Name);
                if (gridCount < 0 || gridCount > limits.MaxActiveVoxelCount - activeCount)
                    throw new InvalidDataException("The selected OpenVDB grids exceed the active-voxel ceiling.");
                activeCount += gridCount;
                gridMask |= grid.mask;
                ReadFileBounds(grid.metadata, grid.descriptor.Name, out BrazeVdbVector3Int gridMinimum,
                    out BrazeVdbVector3Int gridMaximum);
                if (gridCount == 0) continue;
                if (!hasDomain)
                {
                    minimum = gridMinimum;
                    maximum = gridMaximum;
                    hasDomain = true;
                }
                else
                {
                    minimum = new BrazeVdbVector3Int(Math.Min(minimum.x, gridMinimum.x),
                        Math.Min(minimum.y, gridMinimum.y), Math.Min(minimum.z, gridMinimum.z));
                    maximum = new BrazeVdbVector3Int(Math.Max(maximum.x, gridMaximum.x),
                        Math.Max(maximum.y, gridMaximum.y), Math.Max(maximum.z, gridMaximum.z));
                }
            }
            if (!hasDomain)
                throw new NotSupportedException(
                    "A generic OpenVDB frame with only empty selected grids has no finite import domain. " +
                    "Export at least one active voxel or include the documented Braze domain metadata.");

            var resolution = new BrazeVdbVector3Int(CheckedExtent(minimum.x, maximum.x, "X"),
                CheckedExtent(minimum.y, maximum.y, "Y"), CheckedExtent(minimum.z, maximum.z, "Z"));
            BrazeVdbVector3d worldSize = first.transform.WorldSize(resolution);
            RequireFinitePositive(worldSize.x, "generic OpenVDB world size X");
            RequireFinitePositive(worldSize.y, "generic OpenVDB world size Y");
            RequireFinitePositive(worldSize.z, "generic OpenVDB world size Z");
            return new BrazeOpenVdbFrameInfo(resolution, minimum, worldSize, first.transform, activeCount,
                gridMask, false, 0, 0f, 30f, 1f, 1f, 1f);
        }

        static void ReadGrid(BrazeVdbBinaryReader reader, uint fileVersion, GridHeader grid,
            BrazeOpenVdbFrameInfo info, BrickAccumulator bricks, float scale, BrazeVdbSafetyLimits limits,
            CancellationToken cancellationToken)
        {
            reader.SeekAbsolute(grid.topologyPosition, $"OpenVDB grid '{grid.descriptor.Name}' topology");
            if (reader.ReadUInt32("OpenVDB tree marker") != 1)
                throw new InvalidDataException($"OpenVDB grid '{grid.descriptor.Name}' has an invalid tree marker.");
            Value background = ReadValue(reader, grid, true, "OpenVDB tree background");
            if (background.x != 0f || background.y != 0f || background.z != 0f)
                throw new NotSupportedException(
                    $"OpenVDB grid '{grid.descriptor.Name}' uses a nonzero background, which is unsupported.");
            int rootTileCount = ReadUIntCount(reader, limits.MaxNodeCount, "OpenVDB root tile count");
            int rootChildCount = ReadUIntCount(reader, limits.MaxNodeCount, "OpenVDB root child count");
            var budget = new GridReadBudget();
            long rootIndexBytes = checked((long)rootTileCount + rootChildCount) * 64;
            if (rootIndexBytes > limits.MaxAllocationBytes)
                throw new InvalidDataException("The OpenVDB root index exceeds the allocation ceiling.");
            var rootOrigins = new HashSet<BrazeVdbCoord>();
            AddNodeBudget(budget, rootTileCount, limits);
            AddNodeBudget(budget, rootChildCount, limits);
            for (int i = 0; i < rootTileCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrazeVdbCoord origin = ReadCoordinate(reader, $"OpenVDB root tile {i}");
                RequireAligned(origin, BrazeVdbTreeLayout.UpperExtent, "root tile");
                if (!rootOrigins.Add(origin))
                    throw new InvalidDataException(
                        $"OpenVDB grid '{grid.descriptor.Name}' repeats root origin {origin}.");
                Value value = ReadValue(reader, grid, false, $"OpenVDB root tile {i} value");
                bool active = reader.ReadBoolean($"OpenVDB root tile {i} state");
                if (active) ExpandTile(origin, BrazeVdbTreeLayout.UpperExtent, value, grid, info, bricks, scale,
                    budget, limits, cancellationToken);
            }

            var leaves = new List<LeafRecord>();
            for (int i = 0; i < rootChildCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrazeVdbCoord origin = ReadCoordinate(reader, $"OpenVDB root child {i}");
                RequireAligned(origin, BrazeVdbTreeLayout.UpperExtent, "root child");
                if (!rootOrigins.Add(origin))
                    throw new InvalidDataException(
                        $"OpenVDB grid '{grid.descriptor.Name}' repeats root origin {origin}.");
                ReadUpper(reader, grid, origin, info, bricks, scale, leaves, budget, limits,
                    cancellationToken);
            }
            if (reader.Position != grid.descriptor.BlockPosition)
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' topology ended at {reader.Position}, expected " +
                    $"{grid.descriptor.BlockPosition}.");

            for (int i = 0; i < leaves.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LeafRecord leaf = leaves[i];
                BrazeVdbMask bufferMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.LeafVoxelCount,
                    $"OpenVDB leaf {i} buffer mask");
                if (!bufferMask.Equals(leaf.activeMask))
                    throw new InvalidDataException("OpenVDB leaf topology and value-buffer masks disagree.");
                ReadValues(reader, grid, leaf.activeMask, BrazeVdbTreeLayout.LeafVoxelCount,
                    (offset, value) =>
                    {
                        if (!leaf.activeMask.IsOn(offset)) return;
                        ValidateActiveValue(grid, value);
                        AddActive(BrazeVdbTreeLayout.LeafCoordinate(leaf.origin, offset), value, grid, info,
                            bricks, scale, budget, limits);
                    }, cancellationToken);
            }
            if (reader.Position != grid.descriptor.EndPosition)
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' leaf buffers ended at {reader.Position}, expected " +
                    $"{grid.descriptor.EndPosition}.");
            long expected = RequireInt64(grid.metadata, "file_voxel_count", grid.descriptor.Name);
            if (budget.activeCount != expected)
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' metadata reports {expected} active voxels; " +
                    $"topology contains {budget.activeCount}.");
            ValidateReadBounds(grid, budget);
        }

        static void ReadUpper(BrazeVdbBinaryReader reader, GridHeader grid, BrazeVdbCoord origin,
            BrazeOpenVdbFrameInfo info, BrickAccumulator bricks, float scale, List<LeafRecord> leaves,
            GridReadBudget budget, BrazeVdbSafetyLimits limits,
            CancellationToken cancellationToken)
        {
            BrazeVdbMask childMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.UpperSlotCount,
                "OpenVDB upper child mask");
            BrazeVdbMask valueMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.UpperSlotCount,
                "OpenVDB upper value mask");
            RejectMaskOverlap(childMask, valueMask, "upper");
            ReadValues(reader, grid, valueMask, BrazeVdbTreeLayout.UpperSlotCount,
                (offset, value) =>
                {
                    if (!valueMask.IsOn(offset)) return;
                    ValidateActiveValue(grid, value);
                    ExpandTile(BrazeVdbTreeLayout.UpperChildOrigin(origin, offset),
                        BrazeVdbTreeLayout.LowerExtent, value, grid, info, bricks, scale, budget, limits,
                        cancellationToken);
                }, cancellationToken);
            int children = childMask.CountOn();
            AddNodeBudget(budget, children, limits);
            foreach (int offset in childMask.OnIndices())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadLower(reader, grid, BrazeVdbTreeLayout.UpperChildOrigin(origin, offset), info, bricks, scale,
                    leaves, budget, limits, cancellationToken);
            }
        }

        static void ReadLower(BrazeVdbBinaryReader reader, GridHeader grid, BrazeVdbCoord origin,
            BrazeOpenVdbFrameInfo info, BrickAccumulator bricks, float scale, List<LeafRecord> leaves,
            GridReadBudget budget, BrazeVdbSafetyLimits limits,
            CancellationToken cancellationToken)
        {
            BrazeVdbMask childMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.LowerSlotCount,
                "OpenVDB lower child mask");
            BrazeVdbMask valueMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.LowerSlotCount,
                "OpenVDB lower value mask");
            RejectMaskOverlap(childMask, valueMask, "lower");
            ReadValues(reader, grid, valueMask, BrazeVdbTreeLayout.LowerSlotCount,
                (offset, value) =>
                {
                    if (!valueMask.IsOn(offset)) return;
                    ValidateActiveValue(grid, value);
                    ExpandTile(BrazeVdbTreeLayout.LowerChildOrigin(origin, offset),
                        BrazeVdbTreeLayout.LeafDimension, value, grid, info, bricks, scale, budget, limits,
                        cancellationToken);
                }, cancellationToken);
            int children = childMask.CountOn();
            AddNodeBudget(budget, children, limits);
            long leafBytes = checked((long)(leaves.Count + children) * 128);
            if (leafBytes > limits.MaxAllocationBytes)
                throw new InvalidDataException("The OpenVDB leaf topology exceeds the allocation ceiling.");
            foreach (int offset in childMask.OnIndices())
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrazeVdbCoord leafOrigin = BrazeVdbTreeLayout.LowerChildOrigin(origin, offset);
                BrazeVdbMask activeMask = BrazeVdbMask.Read(reader, BrazeVdbTreeLayout.LeafVoxelCount,
                    "OpenVDB leaf topology mask");
                leaves.Add(new LeafRecord(leafOrigin, activeMask));
            }
        }

        delegate void ValueAction(int offset, Value value);

        static void ReadValues(BrazeVdbBinaryReader reader, GridHeader grid, BrazeVdbMask valueMask,
            int valueCount, ValueAction accept, CancellationToken cancellationToken)
        {
            bool activeMask = (grid.compression & BrazeVdbProfile.CompressionActiveMask) != 0;
            bool zip = (grid.compression & BrazeVdbProfile.CompressionZip) != 0;
            byte mode = reader.ReadByte("OpenVDB compressed-value mode");
            if (activeMask ? mode != NoMaskOrInactiveValuesMode : mode != AllValuesMode)
                throw new NotSupportedException(
                    $"OpenVDB value mode {mode} is unsupported for compression flags {grid.compression}.");
            int storedCount = activeMask ? valueMask.CountOn() : valueCount;
            int bytesPerValue = grid.vector ? (grid.halfVector ? 6 : 12) : 4;
            int expectedBytes = checked(storedCount * bytesPerValue);

            // Uncompressed OpenVDB stores complete node/leaf value arrays even when only a small mask is active.
            // Reading every float through Stream.ReadByte made production sequences spend most of their time in
            // hundreds of millions of tiny virtual calls. Pull each bounded buffer in one operation, then decode
            // only the active values that can reach the Braze sparse cache.
            if (!zip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expectedBytes == 0) return;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(expectedBytes);
                try
                {
                    reader.ReadBytes(buffer, 0, expectedBytes, "OpenVDB uncompressed values");
                    if (activeMask)
                    {
                        int byteOffset = 0;
                        foreach (int offset in valueMask.OnIndices())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            accept(offset, ReadBufferedValue(buffer, byteOffset, grid));
                            byteOffset += bytesPerValue;
                        }
                    }
                    else
                    {
                        foreach (int offset in valueMask.OnIndices())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            accept(offset, ReadBufferedValue(buffer, checked(offset * bytesPerValue), grid));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return;
            }

            void Decode(BrazeVdbBinaryReader values)
            {
                if (activeMask)
                {
                    foreach (int offset in valueMask.OnIndices())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        accept(offset, ReadValue(values, grid, false, "OpenVDB active value"));
                    }
                }
                else
                {
                    for (int offset = 0; offset < valueCount; offset++)
                    {
                        if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        Value value = ReadValue(values, grid, false, "OpenVDB value");
                        if (valueMask.IsOn(offset)) accept(offset, value);
                    }
                }
            }

            BrazeVdbZipBuffer.ReadFramed(reader, expectedBytes, Decode);
        }

        static Value ReadBufferedValue(byte[] bytes, int offset, GridHeader grid)
        {
            static ushort UInt16(byte[] source, int index) =>
                (ushort)(source[index] | source[index + 1] << 8);

            static float Single(byte[] source, int index)
            {
                int bits = source[index] | source[index + 1] << 8 | source[index + 2] << 16 |
                           source[index + 3] << 24;
                return BitConverter.Int32BitsToSingle(bits);
            }

            if (!grid.vector)
            {
                float scalar = Single(bytes, offset);
                ValidateFinite(scalar, "OpenVDB active value");
                return new Value(scalar);
            }

            float x;
            float y;
            float z;
            if (grid.halfVector)
            {
                x = BrazeFluidCacheCodec.HalfToFloat(UInt16(bytes, offset));
                y = BrazeFluidCacheCodec.HalfToFloat(UInt16(bytes, offset + 2));
                z = BrazeFluidCacheCodec.HalfToFloat(UInt16(bytes, offset + 4));
            }
            else
            {
                x = Single(bytes, offset);
                y = Single(bytes, offset + 4);
                z = Single(bytes, offset + 8);
            }
            ValidateFinite(x, "OpenVDB active value X");
            ValidateFinite(y, "OpenVDB active value Y");
            ValidateFinite(z, "OpenVDB active value Z");
            return new Value(x, y, z);
        }

        static Value ReadValue(BrazeVdbBinaryReader reader, GridHeader grid, bool rootBackground, string label)
        {
            if (!grid.vector)
            {
                float scalar = reader.ReadSingle(label);
                ValidateFinite(scalar, label);
                return new Value(scalar);
            }
            float x;
            float y;
            float z;
            if (grid.halfVector && !rootBackground)
            {
                x = BrazeFluidCacheCodec.HalfToFloat(reader.ReadUInt16(label + " X"));
                y = BrazeFluidCacheCodec.HalfToFloat(reader.ReadUInt16(label + " Y"));
                z = BrazeFluidCacheCodec.HalfToFloat(reader.ReadUInt16(label + " Z"));
            }
            else
            {
                x = reader.ReadSingle(label + " X");
                y = reader.ReadSingle(label + " Y");
                z = reader.ReadSingle(label + " Z");
            }
            ValidateFinite(x, label + " X");
            ValidateFinite(y, label + " Y");
            ValidateFinite(z, label + " Z");
            return new Value(x, y, z);
        }

        static void ExpandTile(BrazeVdbCoord origin, int extent, Value value, GridHeader grid,
            BrazeOpenVdbFrameInfo info, BrickAccumulator bricks, float scale, GridReadBudget budget,
            BrazeVdbSafetyLimits limits, CancellationToken cancellationToken)
        {
            int minX = Math.Max(origin.x, info.minimum.x);
            int minY = Math.Max(origin.y, info.minimum.y);
            int minZ = Math.Max(origin.z, info.minimum.z);
            int maxX = Math.Min(checked(origin.x + extent), checked(info.minimum.x + info.resolution.x));
            int maxY = Math.Min(checked(origin.y + extent), checked(info.minimum.y + info.resolution.y));
            int maxZ = Math.Min(checked(origin.z + extent), checked(info.minimum.z + info.resolution.z));
            if (minX >= maxX || minY >= maxY || minZ >= maxZ) return;
            long count = CheckedProduct(maxX - minX, maxY - minY, maxZ - minZ, "OpenVDB active tile");
            if (count > limits.MaxActiveVoxelCount - budget.activeCount)
                throw new InvalidDataException("The OpenVDB active tiles exceed the active-voxel ceiling.");
            budget.activeCount += count;
            budget.IncludeRange(minX, minY, minZ, maxX - 1, maxY - 1, maxZ - 1);
            for (int x = minX; x < maxX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = minY; y < maxY; y++)
                    for (int z = minZ; z < maxZ; z++)
                        bricks.Set(new BrazeVdbCoord(x, y, z), grid.channel, value, scale);
            }
        }

        static void AddActive(BrazeVdbCoord coordinate, Value value, GridHeader grid,
            BrazeOpenVdbFrameInfo info, BrickAccumulator bricks, float scale, GridReadBudget budget,
            BrazeVdbSafetyLimits limits)
        {
            bool inside = coordinate.x >= info.minimum.x && coordinate.y >= info.minimum.y &&
                          coordinate.z >= info.minimum.z &&
                          (long)coordinate.x < (long)info.minimum.x + info.resolution.x &&
                          (long)coordinate.y < (long)info.minimum.y + info.resolution.y &&
                          (long)coordinate.z < (long)info.minimum.z + info.resolution.z;
            if (!inside)
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' contains active voxels outside its declared domain.");
            if (budget.activeCount >= limits.MaxActiveVoxelCount)
                throw new InvalidDataException("The OpenVDB grid exceeds the active-voxel ceiling.");
            budget.activeCount++;
            budget.Include(coordinate.x, coordinate.y, coordinate.z);
            bricks.Set(coordinate, grid.channel, value, scale);
        }

        static void ValidateCompression(string gridName, uint compression)
        {
            if ((compression & BrazeVdbProfile.CompressionBlosc) != 0)
                throw new NotSupportedException(
                    $"OpenVDB grid '{gridName}' uses legacy Blosc compression. Convert it to uncompressed or " +
                    "ZIP/active-mask OpenVDB in the source DCC, or re-export it from the original Braze cache.");
            uint known = BrazeVdbProfile.CompressionZip | BrazeVdbProfile.CompressionActiveMask;
            if ((compression & ~known) != 0 || compression == BrazeVdbProfile.CompressionZip)
                throw new NotSupportedException(
                    $"OpenVDB grid '{gridName}' uses unsupported compression flags {compression}.");
        }

        static void ValidateStandardGridMetadata(uint fileVersion, GridHeader grid)
        {
            string name = RequireString(grid.metadata, "name", grid.descriptor.Name);
            if (!string.Equals(name, grid.descriptor.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"OpenVDB grid '{grid.descriptor.Name}' metadata name disagrees.");
            string gridClass = RequireString(grid.metadata, "class", grid.descriptor.Name);
            if (grid.vector)
            {
                if (gridClass != "unknown" ||
                    RequireString(grid.metadata, "vector_type", grid.descriptor.Name) != "contravariant relative")
                    throw new InvalidDataException(
                        $"OpenVDB velocity grid '{grid.descriptor.Name}' has unsupported vector semantics.");
                bool hasHalf = grid.metadata.TryGet("is_saved_as_half_float", out BrazeVdbMetadataValue half);
                if (grid.halfVector)
                {
                    if (fileVersion != BrazeVdbProfile.NativeHalfVectorFileVersion || !hasHalf ||
                        half.TypeName != "bool" || half.Value is not bool value || !value)
                        throw new InvalidDataException("The legacy half-vector OpenVDB marker is missing or invalid.");
                }
                else if (hasHalf)
                {
                    throw new InvalidDataException("A full-float OpenVDB velocity grid cannot carry a half marker.");
                }
            }
            else if (gridClass != "fog volume")
            {
                throw new InvalidDataException(
                    $"OpenVDB scalar grid '{grid.descriptor.Name}' must use fog-volume class semantics.");
            }
            long voxelCount = RequireInt64(grid.metadata, "file_voxel_count", grid.descriptor.Name);
            if (voxelCount < 0)
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' has a negative active-voxel count.");
            ReadFileBounds(grid.metadata, grid.descriptor.Name, out BrazeVdbVector3Int minimum,
                out BrazeVdbVector3Int maximum);
            if (voxelCount == 0)
            {
                if (!IsEmptyBounds(minimum, maximum))
                    throw new InvalidDataException(
                        $"OpenVDB grid '{grid.descriptor.Name}' has a nonempty bounding box but no active voxels.");
            }
            else
            {
                RequireValidBounds(minimum, maximum, grid.descriptor.Name);
            }
        }

        static void ValidateBrazeGridMetadata(GridHeader grid)
        {
            if (RequireInt32(grid.metadata, "braze:brick_size", grid.descriptor.Name) != 4)
                throw new InvalidDataException("Managed OpenVDB import requires 4x4x4 Braze bricks.");
        }

        static bool HasBrazeDomainMetadata(BrazeVdbMetadataMap metadata)
        {
            return metadata.TryGet("braze:resolution_x", out _) ||
                   metadata.TryGet("braze:resolution_y", out _) ||
                   metadata.TryGet("braze:resolution_z", out _) ||
                   metadata.TryGet("braze:world_x", out _) ||
                   metadata.TryGet("braze:world_y", out _) ||
                   metadata.TryGet("braze:world_z", out _) ||
                   metadata.TryGet("braze:brick_size", out _);
        }

        static void ReadFileBounds(BrazeVdbMetadataMap metadata, string gridName,
            out BrazeVdbVector3Int minimum, out BrazeVdbVector3Int maximum)
        {
            minimum = RequireVec3I(metadata, "file_bbox_min", gridName);
            maximum = RequireVec3I(metadata, "file_bbox_max", gridName);
        }

        static void RequireValidBounds(BrazeVdbVector3Int minimum, BrazeVdbVector3Int maximum,
            string gridName)
        {
            if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' has an invalid active bounding box.");
        }

        static bool IsEmptyBounds(BrazeVdbVector3Int minimum, BrazeVdbVector3Int maximum) =>
            minimum.x == int.MaxValue && minimum.y == int.MaxValue && minimum.z == int.MaxValue &&
            maximum.x == int.MinValue && maximum.y == int.MinValue && maximum.z == int.MinValue;

        static void ValidateReadBounds(GridHeader grid, GridReadBudget budget)
        {
            ReadFileBounds(grid.metadata, grid.descriptor.Name, out BrazeVdbVector3Int expectedMinimum,
                out BrazeVdbVector3Int expectedMaximum);
            var actualMinimum = new BrazeVdbVector3Int(budget.minimumX, budget.minimumY, budget.minimumZ);
            var actualMaximum = new BrazeVdbVector3Int(budget.maximumX, budget.maximumY, budget.maximumZ);
            if (!actualMinimum.Equals(expectedMinimum) || !actualMaximum.Equals(expectedMaximum))
                throw new InvalidDataException(
                    $"OpenVDB grid '{grid.descriptor.Name}' active bounding box disagrees with its topology.");
        }

        static BrazeVdbVector3Int ReadResolution(BrazeVdbMetadataMap metadata, string gridName)
        {
            int x = RequireInt32(metadata, "braze:resolution_x", gridName);
            int y = RequireInt32(metadata, "braze:resolution_y", gridName);
            int z = RequireInt32(metadata, "braze:resolution_z", gridName);
            if (x <= 0 || y <= 0 || z <= 0)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' has an invalid Braze resolution.");
            return new BrazeVdbVector3Int(x, y, z);
        }

        static BrazeVdbVector3d ReadWorldSize(BrazeVdbMetadataMap metadata, string gridName)
            => new(RequireFinitePositiveFloat(metadata, "braze:world_x", gridName),
                RequireFinitePositiveFloat(metadata, "braze:world_y", gridName),
                RequireFinitePositiveFloat(metadata, "braze:world_z", gridName));

        static int RequireInt32(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "int32" ||
                value.Value is not int result)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' requires int32 metadata '{name}'.");
            return result;
        }

        static long RequireInt64(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "int64" ||
                value.Value is not long result)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' requires int64 metadata '{name}'.");
            return result;
        }

        static BrazeVdbVector3Int RequireVec3I(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "vec3i" ||
                value.Value is not BrazeVdbVector3Int result)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' requires vec3i metadata '{name}'.");
            return result;
        }

        static string RequireString(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "string" ||
                value.Value is not string result || string.IsNullOrEmpty(result))
                throw new InvalidDataException($"OpenVDB grid '{gridName}' requires string metadata '{name}'.");
            return result;
        }

        static float RequireFiniteFloat(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "float" ||
                value.Value is not float result)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' requires float metadata '{name}'.");
            ValidateFinite(result, $"OpenVDB metadata '{name}'");
            return result;
        }

        static float RequireFinitePositiveFloat(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            float result = RequireFiniteFloat(metadata, name, gridName);
            if (result <= 0f)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' metadata '{name}' must be positive.");
            return result;
        }

        static float OptionalGain(BrazeVdbMetadataMap metadata)
        {
            if (!metadata.TryGet("braze:gain", out BrazeVdbMetadataValue value)) return 1f;
            if (value.TypeName != "float" || value.Value is not float gain || float.IsNaN(gain) ||
                float.IsInfinity(gain) || gain < 0f)
                throw new InvalidDataException("OpenVDB scalar gain metadata is invalid.");
            return gain;
        }

        static void AddRequest(Dictionary<string, (int channel, int mask, bool vector)> requests, string name,
            int channel, int mask, bool vector)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (requests.ContainsKey(name))
                throw new ArgumentException($"OpenVDB grid '{name}' is selected for more than one channel.");
            requests.Add(name, (channel, mask, vector));
        }

        static BrazeOpenVdbFrameInfo ResolveOutputInfo(in BrazeOpenVdbFrameInfo expected,
            in BrazeOpenVdbFrameInfo actual, bool allowDomainOverride)
        {
            if (!expected.transform.Equals(actual.transform) || expected.activeVoxelCount != actual.activeVoxelCount ||
                expected.gridMask != actual.gridMask || expected.sourceFrame != actual.sourceFrame ||
                !expected.sourceTime.Equals(actual.sourceTime) || !expected.fps.Equals(actual.fps))
                throw new InvalidDataException("OpenVDB frame information changed between inspection and sparse read.");
            bool sameDomain = expected.resolution.Equals(actual.resolution) &&
                              expected.minimum.Equals(actual.minimum) && expected.worldSize.Equals(actual.worldSize);
            if (sameDomain) return actual;
            if (!allowDomainOverride || actual.isBraze)
                throw new InvalidDataException("OpenVDB frame domain changed between inspection and sparse read.");
            if (expected.resolution.x <= 0 || expected.resolution.y <= 0 || expected.resolution.z <= 0 ||
                !ContainsDomain(expected, actual))
                throw new InvalidDataException("The manifest OpenVDB domain does not contain the active grid bounds.");
            BrazeVdbVector3d expectedWorld = expected.transform.WorldSize(expected.resolution);
            if (!expected.worldSize.Equals(expectedWorld))
                throw new InvalidDataException("The manifest OpenVDB domain conflicts with the grid transform.");
            return expected;
        }

        static bool ContainsDomain(in BrazeOpenVdbFrameInfo outer, in BrazeOpenVdbFrameInfo inner) =>
            inner.minimum.x >= outer.minimum.x && inner.minimum.y >= outer.minimum.y &&
            inner.minimum.z >= outer.minimum.z &&
            (long)inner.minimum.x + inner.resolution.x <= (long)outer.minimum.x + outer.resolution.x &&
            (long)inner.minimum.y + inner.resolution.y <= (long)outer.minimum.y + outer.resolution.y &&
            (long)inner.minimum.z + inner.resolution.z <= (long)outer.minimum.z + outer.resolution.z;

        static void ValidateActiveValue(GridHeader grid, Value value)
        {
            if (grid.vector)
            {
                if (value.x == 0f && value.y == 0f && value.z == 0f)
                    throw new InvalidDataException(
                        $"OpenVDB velocity grid '{grid.descriptor.Name}' marks a zero vector active.");
            }
            else if (value.x <= 0f)
            {
                throw new InvalidDataException(
                    $"OpenVDB scalar grid '{grid.descriptor.Name}' marks a non-positive value active.");
            }
        }

        static void RejectMaskOverlap(BrazeVdbMask children, BrazeVdbMask values, string label)
        {
            for (int i = 0; i < children.BitCount; i++)
                if (children.IsOn(i) && values.IsOn(i))
                    throw new InvalidDataException($"OpenVDB {label} child and value masks overlap at slot {i}.");
        }

        static void AddNodeBudget(GridReadBudget budget, int count, BrazeVdbSafetyLimits limits)
        {
            if (count > limits.MaxNodeCount - budget.nodeCount)
                throw new InvalidDataException("The OpenVDB topology exceeds the node-count ceiling.");
            budget.nodeCount += count;
        }

        static int ReadUIntCount(BrazeVdbBinaryReader reader, int maximum, string label)
        {
            uint value = reader.ReadUInt32(label);
            if (value > maximum) throw new InvalidDataException($"{label} {value} exceeds the limit {maximum}.");
            return (int)value;
        }

        static BrazeVdbCoord ReadCoordinate(BrazeVdbBinaryReader reader, string label) => new(
            reader.ReadInt32(label + " X"), reader.ReadInt32(label + " Y"), reader.ReadInt32(label + " Z"));

        static void RequireAligned(BrazeVdbCoord coordinate, int extent, string label)
        {
            if (BrazeVdbTreeLayout.AlignDown(coordinate.x, extent) != coordinate.x ||
                BrazeVdbTreeLayout.AlignDown(coordinate.y, extent) != coordinate.y ||
                BrazeVdbTreeLayout.AlignDown(coordinate.z, extent) != coordinate.z)
                throw new InvalidDataException($"OpenVDB {label} origin {coordinate} is not {extent}-aligned.");
        }

        static int CheckedCeilDiv(int value, int divisor, string label)
        {
            try { return checked((value + divisor - 1) / divisor); }
            catch (OverflowException exception) { throw new InvalidDataException($"{label} overflowed.", exception); }
        }

        static int CheckedExtent(int minimum, int maximum, string axis)
        {
            long extent = (long)maximum - minimum + 1;
            if (extent <= 0 || extent > int.MaxValue)
                throw new InvalidDataException($"The generic OpenVDB {axis} domain exceeds the managed range.");
            return (int)extent;
        }

        static long CheckedProduct(int x, int y, int z, string label)
        {
            try { return checked((long)x * y * z); }
            catch (OverflowException exception) { throw new InvalidDataException($"{label} overflowed.", exception); }
        }

        static void ValidateScale(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                throw new ArgumentOutOfRangeException(label, "OpenVDB import scales must be finite and nonnegative.");
        }

        static void ValidateFinite(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException($"{label} must be finite.");
        }

        static void RequireFinitePositive(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                throw new InvalidDataException($"{label} must be finite and positive.");
        }
    }
}
