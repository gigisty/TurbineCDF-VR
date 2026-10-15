using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace BrazeFX.EditorTools.Vdb
{
    internal abstract class BrazeOpenVdbGrid
    {
        public string Name { get; }
        public BrazeVdbMetadataMap Metadata { get; }
        public BrazeVdbTransform Transform { get; }
        internal abstract string TreeType { get; }

        protected BrazeOpenVdbGrid(string name, BrazeVdbMetadataMap metadata, BrazeVdbTransform transform)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("VDB grid name is empty.", nameof(name));
            Name = name;
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            Transform = transform;
        }

        internal abstract BrazeOpenVdbTreeWriteResult WriteTopology(BrazeVdbBinaryWriter writer,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken);
        internal abstract void WriteLeafBuffers(BrazeVdbBinaryWriter writer, BrazeOpenVdbTreeWriteResult result,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken);
        internal abstract BrazeVdbGridStatistics GetStatistics();
    }

    internal sealed class BrazeOpenVdbFloatGrid : BrazeOpenVdbGrid
    {
        public BrazeVdbTree<float> Tree { get; }
        internal override string TreeType => BrazeVdbProfile.FloatTreeType;

        public BrazeOpenVdbFloatGrid(string name, BrazeVdbMetadataMap metadata, BrazeVdbTransform transform,
            BrazeVdbTree<float> tree) : base(name, metadata, transform)
            => Tree = tree ?? throw new ArgumentNullException(nameof(tree));

        internal override BrazeOpenVdbTreeWriteResult WriteTopology(BrazeVdbBinaryWriter writer,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken)
            => BrazeOpenVdbTreeWriter.WriteTopology(writer, Tree, BrazeOpenVdbTreeWriter.WriteFloat, compression,
                zipBuffer, cancellationToken);

        internal override void WriteLeafBuffers(BrazeVdbBinaryWriter writer, BrazeOpenVdbTreeWriteResult result,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken)
            => BrazeOpenVdbTreeWriter.WriteLeafBuffers(writer, Tree, result, BrazeOpenVdbTreeWriter.WriteFloat,
                compression, zipBuffer, cancellationToken);

        internal override BrazeVdbGridStatistics GetStatistics()
        {
            if (Tree.Background != 0f)
                throw new InvalidDataException($"Scalar grid '{Name}' must use a zero background.");
            return BrazeVdbGridStatistics.From(Tree.EnumerateActiveVoxels(), value =>
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                    throw new InvalidDataException(
                        $"Scalar grid '{Name}' contains a non-finite or non-positive active value.");
            });
        }
    }

    internal sealed class BrazeOpenVdbVectorGrid : BrazeOpenVdbGrid
    {
        public BrazeVdbTree<Vector3> Tree { get; }
        internal override string TreeType => BrazeVdbProfile.VectorTreeType;

        public BrazeOpenVdbVectorGrid(string name, BrazeVdbMetadataMap metadata, BrazeVdbTransform transform,
            BrazeVdbTree<Vector3> tree) : base(name, metadata, transform)
            => Tree = tree ?? throw new ArgumentNullException(nameof(tree));

        internal override BrazeOpenVdbTreeWriteResult WriteTopology(BrazeVdbBinaryWriter writer,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken)
            => BrazeOpenVdbTreeWriter.WriteTopology(writer, Tree, BrazeOpenVdbTreeWriter.WriteVector, compression,
                zipBuffer, cancellationToken);

        internal override void WriteLeafBuffers(BrazeVdbBinaryWriter writer, BrazeOpenVdbTreeWriteResult result,
            uint compression, BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken)
            => BrazeOpenVdbTreeWriter.WriteLeafBuffers(writer, Tree, result, BrazeOpenVdbTreeWriter.WriteVector,
                compression, zipBuffer, cancellationToken);

        internal override BrazeVdbGridStatistics GetStatistics()
        {
            if (Tree.Background.x != 0f || Tree.Background.y != 0f || Tree.Background.z != 0f)
                throw new InvalidDataException($"Vector grid '{Name}' must use a zero background.");
            return BrazeVdbGridStatistics.From(Tree.EnumerateActiveVoxels(), value =>
            {
                if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                    float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                    float.IsNaN(value.z) || float.IsInfinity(value.z) ||
                    (value.x == 0f && value.y == 0f && value.z == 0f))
                    throw new InvalidDataException(
                        $"Vector grid '{Name}' contains a non-finite or zero active value.");
            });
        }
    }

    internal sealed class BrazeOpenVdbDocument
    {
        readonly List<BrazeOpenVdbGrid> grids = new();

        public string Uuid { get; }
        public BrazeVdbMetadataMap Metadata { get; }
        public IReadOnlyList<BrazeOpenVdbGrid> Grids => grids;

        public BrazeOpenVdbDocument(string uuid, BrazeVdbMetadataMap metadata)
        {
            Uuid = uuid ?? throw new ArgumentNullException(nameof(uuid));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public void AddGrid(BrazeOpenVdbGrid grid)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            grids.Add(grid);
        }
    }

    /// <summary>Deferred grid construction keeps only one sparse tree live while a frame is serialized.</summary>
    internal sealed class BrazeOpenVdbGridSource
    {
        readonly Func<CancellationToken, BrazeOpenVdbGrid> build;

        public string Name { get; }

        public BrazeOpenVdbGridSource(string name, Func<CancellationToken, BrazeOpenVdbGrid> build)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("VDB grid name is empty.", nameof(name));
            Name = name;
            this.build = build ?? throw new ArgumentNullException(nameof(build));
        }

        public BrazeOpenVdbGrid Build(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrazeOpenVdbGrid grid = build(cancellationToken) ??
                throw new InvalidDataException($"Deferred OpenVDB grid '{Name}' returned no grid.");
            if (!string.Equals(Name, grid.Name, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Deferred OpenVDB grid '{Name}' produced unexpected grid '{grid.Name}'.");
            return grid;
        }
    }

    internal sealed class BrazeOpenVdbStreamingDocument
    {
        readonly List<BrazeOpenVdbGridSource> grids = new();

        public string Uuid { get; }
        public BrazeVdbMetadataMap Metadata { get; }
        public IReadOnlyList<BrazeOpenVdbGridSource> Grids => grids;

        public BrazeOpenVdbStreamingDocument(string uuid, BrazeVdbMetadataMap metadata)
        {
            Uuid = uuid ?? throw new ArgumentNullException(nameof(uuid));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public void AddGrid(BrazeOpenVdbGridSource grid)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            grids.Add(grid);
        }
    }

    internal readonly struct BrazeVdbGridStatistics
    {
        public readonly long activeVoxelCount;
        public readonly BrazeVdbCoord minimum;
        public readonly BrazeVdbCoord maximum;

        BrazeVdbGridStatistics(long activeVoxelCount, BrazeVdbCoord minimum, BrazeVdbCoord maximum)
        {
            this.activeVoxelCount = activeVoxelCount;
            this.minimum = minimum;
            this.maximum = maximum;
        }

        public static BrazeVdbGridStatistics From<T>(IEnumerable<BrazeVdbTreeRecord<T>> records,
            Action<T> validateValue)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (validateValue == null) throw new ArgumentNullException(nameof(validateValue));
            long count = 0;
            var minimum = new BrazeVdbCoord(int.MaxValue, int.MaxValue, int.MaxValue);
            var maximum = new BrazeVdbCoord(int.MinValue, int.MinValue, int.MinValue);
            foreach (BrazeVdbTreeRecord<T> record in records)
            {
                validateValue(record.value);
                count = checked(count + 1);
                if (count > BrazeVdbSafetyLimits.Default.MaxActiveVoxelCount)
                    throw new InvalidDataException("The VDB grid exceeds the active-voxel safety ceiling.");
                minimum = new BrazeVdbCoord(Math.Min(minimum.x, record.coordinate.x),
                    Math.Min(minimum.y, record.coordinate.y), Math.Min(minimum.z, record.coordinate.z));
                maximum = new BrazeVdbCoord(Math.Max(maximum.x, record.coordinate.x),
                    Math.Max(maximum.y, record.coordinate.y), Math.Max(maximum.z, record.coordinate.z));
            }
            return new BrazeVdbGridStatistics(count, minimum, maximum);
        }
    }

    internal sealed class BrazeOpenVdbTreeWriteResult
    {
        internal object Leaves { get; }

        internal BrazeOpenVdbTreeWriteResult(object leaves) => Leaves = leaves;
    }

    /// <summary>Clean-room format-224 OpenVDB writer for BrazeFX's four-grid Editor interchange profile.</summary>
    internal static class BrazeOpenVdbWriter
    {
        readonly struct FrameIdentity
        {
            public readonly BrazeVdbTransform transform;
            public readonly int sourceFrame;
            public readonly float sourceTime;
            public readonly float fps;
            public readonly int brickSize;

            public FrameIdentity(BrazeOpenVdbGrid grid)
            {
                transform = grid.Transform;
                sourceFrame = grid.Metadata.Get<int>("braze:source_frame");
                sourceTime = grid.Metadata.Get<float>("braze:source_time");
                fps = grid.Metadata.Get<float>("braze:fps");
                brickSize = grid.Metadata.Get<int>("braze:brick_size");
            }
        }

        public static void Write(Stream stream, BrazeOpenVdbDocument document,
            BrazeVdbWriteCompression compression = BrazeVdbWriteCompression.None,
            CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            ValidateOutput(stream, compression);
            ValidateFile(document.Uuid, document.Metadata, out string fileSource, out string fileUnits);
            var grids = new List<BrazeOpenVdbGrid>(document.Grids);
            ValidateGridCount(grids.Count);
            grids.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            FrameIdentity? identity = null;
            for (int i = 0; i < grids.Count; i++)
            {
                if (i > 0 && string.Equals(grids[i - 1].Name, grids[i].Name, StringComparison.Ordinal))
                    throw new InvalidDataException($"Duplicate OpenVDB grid name '{grids[i].Name}'.");
                ValidateGridAgainstFrame(grids[i], fileSource, fileUnits, identity);
                identity ??= new FrameIdentity(grids[i]);
            }

            WriteTransaction(stream, document.Uuid, document.Metadata, grids.Count, compression,
                cancellationToken, (writer, zipBuffer) =>
                {
                    for (int i = 0; i < grids.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteGrid(writer, grids[i], compression, zipBuffer, cancellationToken);
                    }
                });
        }

        public static void Write(Stream stream, BrazeOpenVdbStreamingDocument document,
            BrazeVdbWriteCompression compression = BrazeVdbWriteCompression.None,
            CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            ValidateOutput(stream, compression);
            ValidateFile(document.Uuid, document.Metadata, out string fileSource, out string fileUnits);
            var grids = new List<BrazeOpenVdbGridSource>(document.Grids);
            ValidateGridCount(grids.Count);
            grids.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            for (int i = 0; i < grids.Count; i++)
            {
                if (!BrazeVdbProfile.IsProductGridName(grids[i].Name))
                    throw new InvalidDataException(
                        $"Grid '{grids[i].Name}' is outside the BrazeFX OpenVDB profile.");
                if (i > 0 && string.Equals(grids[i - 1].Name, grids[i].Name, StringComparison.Ordinal))
                    throw new InvalidDataException($"Duplicate OpenVDB grid name '{grids[i].Name}'.");
            }

            WriteTransaction(stream, document.Uuid, document.Metadata, grids.Count, compression,
                cancellationToken, (writer, zipBuffer) =>
                {
                    FrameIdentity? identity = null;
                    for (int i = 0; i < grids.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        BrazeOpenVdbGrid grid = grids[i].Build(cancellationToken);
                        ValidateGridAgainstFrame(grid, fileSource, fileUnits, identity);
                        WriteGrid(writer, grid, compression, zipBuffer, cancellationToken);
                        identity ??= new FrameIdentity(grid);
                    }
                });
        }

        static void WriteTransaction(Stream stream, string uuid, BrazeVdbMetadataMap metadata, int gridCount,
            BrazeVdbWriteCompression compression, CancellationToken cancellationToken,
            Action<BrazeVdbBinaryWriter, BrazeVdbZipBuffer> writeGrids)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = 0;
                stream.SetLength(0);
                using var writer = new BrazeVdbBinaryWriter(stream, true);
                writer.WriteUInt64(BrazeVdbProfile.Magic);
                writer.WriteUInt32(BrazeVdbProfile.WriterFileVersion);
                writer.WriteUInt32(BrazeVdbProfile.WriterLibraryMajor);
                writer.WriteUInt32(BrazeVdbProfile.WriterLibraryMinor);
                writer.WriteBoolean(true);
                writer.WriteUtf8(uuid, 36, "OpenVDB UUID");
                metadata.Write(writer);
                writer.WriteInt32(gridCount);
                using var zipBuffer = new BrazeVdbZipBuffer();
                writeGrids(writer, zipBuffer);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                TryDiscardPartialOutput(stream);
                throw;
            }
        }

        static void ValidateOutput(Stream stream, BrazeVdbWriteCompression compression)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite || !stream.CanSeek)
                throw new ArgumentException("Canonical OpenVDB output must be writable and seekable.", nameof(stream));
            if (compression != BrazeVdbWriteCompression.None &&
                compression != BrazeVdbWriteCompression.ZipActiveMask)
                throw new ArgumentOutOfRangeException(nameof(compression));
        }

        static void ValidateFile(string uuid, BrazeVdbMetadataMap metadata, out string source, out string units)
        {
            ValidateUuid(uuid);
            RequireString(metadata, "creator", "BrazeFX Export Studio", "file");
            RequireString(metadata, "braze:source", null, "file");
            RequireString(metadata, "braze:units", null, "file");
            source = metadata.Get<string>("braze:source");
            units = metadata.Get<string>("braze:units");
        }

        static void ValidateGridCount(int count)
        {
            if (count < 1 || count > BrazeVdbProfile.ProductGridLimit)
                throw new InvalidDataException(
                    $"BrazeFX OpenVDB output requires 1..{BrazeVdbProfile.ProductGridLimit} grids.");
        }

        static void ValidateGridAgainstFrame(BrazeOpenVdbGrid grid, string fileSource, string fileUnits,
            FrameIdentity? first)
        {
            ValidateGrid(grid);
            if (!string.Equals(grid.Metadata.Get<string>("braze:source"), fileSource,
                    StringComparison.Ordinal) ||
                !string.Equals(grid.Metadata.Get<string>("braze:units"), fileUnits, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Grid '{grid.Name}' source/units metadata must match the file metadata.");
            if (!first.HasValue) return;
            FrameIdentity identity = first.Value;
            if (!identity.transform.Equals(grid.Transform))
                throw new InvalidDataException(
                    "All canonical BrazeFX grids in a frame must use one identical transform.");
            if (identity.sourceFrame != grid.Metadata.Get<int>("braze:source_frame") ||
                !identity.sourceTime.Equals(grid.Metadata.Get<float>("braze:source_time")) ||
                !identity.fps.Equals(grid.Metadata.Get<float>("braze:fps")) ||
                identity.brickSize != grid.Metadata.Get<int>("braze:brick_size"))
                throw new InvalidDataException(
                    $"Grid '{grid.Name}' frame/time/FPS/brick metadata does not match the first grid.");
        }

        static void TryDiscardPartialOutput(Stream stream)
        {
            try
            {
                if (!stream.CanSeek || !stream.CanWrite) return;
                stream.Position = 0;
                stream.SetLength(0);
            }
            catch
            {
                // Preserve the original write/cancellation exception. Staging ownership deletes the file later.
            }
        }

        static void WriteGrid(BrazeVdbBinaryWriter writer, BrazeOpenVdbGrid grid,
            BrazeVdbWriteCompression compression, BrazeVdbZipBuffer zipBuffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteString(grid.Name, BrazeVdbSafetyLimits.Default.MaxStringBytes, "OpenVDB grid name");
            writer.WriteString(grid.TreeType, BrazeVdbSafetyLimits.Default.MaxStringBytes, "OpenVDB tree type");
            writer.WriteString(string.Empty, BrazeVdbSafetyLimits.Default.MaxStringBytes, "OpenVDB instance parent");
            long gridPositionPatch = writer.ReserveInt64();
            long blockPositionPatch = writer.ReserveInt64();
            long endPositionPatch = writer.ReserveInt64();

            long gridPosition = writer.Position;
            writer.PatchInt64(gridPositionPatch, gridPosition);
            uint compressionFlags = CompressionFlags(grid, compression);
            writer.WriteUInt32(compressionFlags);
            BuildFileMetadata(grid, CompressionLabel(compressionFlags)).Write(writer);
            grid.Transform.Write(writer);
            BrazeOpenVdbTreeWriteResult result = grid.WriteTopology(writer, compressionFlags, zipBuffer,
                cancellationToken);
            writer.PatchInt64(blockPositionPatch, writer.Position);
            grid.WriteLeafBuffers(writer, result, compressionFlags, zipBuffer, cancellationToken);
            writer.PatchInt64(endPositionPatch, writer.Position);
        }

        static uint CompressionFlags(BrazeOpenVdbGrid grid, BrazeVdbWriteCompression compression)
        {
            if (compression == BrazeVdbWriteCompression.None) return BrazeVdbProfile.CompressionNone;
            uint flags = BrazeVdbProfile.CompressionActiveMask;
            if (grid is BrazeOpenVdbVectorGrid) flags |= BrazeVdbProfile.CompressionZip;
            return flags;
        }

        static string CompressionLabel(uint compression) => compression switch
        {
            BrazeVdbProfile.CompressionNone => "none",
            BrazeVdbProfile.CompressionActiveMask => "active values",
            BrazeVdbProfile.CompressionZip | BrazeVdbProfile.CompressionActiveMask => "zip + active values",
            _ => throw new InvalidDataException($"Unsupported canonical compression flags {compression}."),
        };

        static BrazeVdbMetadataMap BuildFileMetadata(BrazeOpenVdbGrid grid, string compressionLabel)
        {
            var result = new BrazeVdbMetadataMap();
            foreach (KeyValuePair<string, BrazeVdbMetadataValue> pair in grid.Metadata.Entries)
                result.Add(pair.Key, pair.Value);

            BrazeVdbGridStatistics statistics = grid.GetStatistics();
            if (statistics.activeVoxelCount > 0)
            {
                int resolutionX = grid.Metadata.Get<int>("braze:resolution_x");
                int resolutionY = grid.Metadata.Get<int>("braze:resolution_y");
                int resolutionZ = grid.Metadata.Get<int>("braze:resolution_z");
                if (statistics.minimum.x < 0 || statistics.minimum.y < 0 || statistics.minimum.z < 0 ||
                    statistics.maximum.x >= resolutionX || statistics.maximum.y >= resolutionY ||
                    statistics.maximum.z >= resolutionZ)
                    throw new InvalidDataException(
                        $"Grid '{grid.Name}' contains active voxels outside its declared Braze domain.");
            }
            AddOrRequire(result, "file_bbox_min", BrazeVdbMetadataValue.Vec3I(
                new BrazeVdbVector3Int(statistics.minimum.x, statistics.minimum.y, statistics.minimum.z)));
            AddOrRequire(result, "file_bbox_max", BrazeVdbMetadataValue.Vec3I(
                new BrazeVdbVector3Int(statistics.maximum.x, statistics.maximum.y, statistics.maximum.z)));
            AddOrRequire(result, "file_compression", BrazeVdbMetadataValue.String(compressionLabel));
            AddOrRequire(result, "file_voxel_count", BrazeVdbMetadataValue.Int64(statistics.activeVoxelCount));
            return result;
        }

        static void AddOrRequire(BrazeVdbMetadataMap metadata, string name, BrazeVdbMetadataValue expected)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue actual))
            {
                metadata.Add(name, expected);
                return;
            }
            if (!string.Equals(actual.TypeName, expected.TypeName, StringComparison.Ordinal) ||
                !Equals(actual.Value, expected.Value))
                throw new InvalidDataException($"OpenVDB metadata '{name}' conflicts with computed grid statistics.");
        }

        static void ValidateGrid(BrazeOpenVdbGrid grid)
        {
            BrazeVdbProfile.ValidateCanonicalWriterType(grid.Name, grid.TreeType);
            RequireString(grid.Metadata, "name", grid.Name, $"grid '{grid.Name}'");
            RequireString(grid.Metadata, "class", grid.Name == "vel" ? "unknown" : "fog volume",
                $"grid '{grid.Name}'");
            RequireString(grid.Metadata, "braze:source", null, $"grid '{grid.Name}'");
            RequireString(grid.Metadata, "braze:units", null, $"grid '{grid.Name}'");
            RequireString(grid.Metadata, "braze:coordinate_system",
                "Unity local space, Y-up, left-handed (+Z forward)", $"grid '{grid.Name}'");
            RequireInt32(grid.Metadata, "braze:source_frame", $"grid '{grid.Name}'");
            int resolutionX = RequirePositiveInt32(grid.Metadata, "braze:resolution_x", grid.Name);
            int resolutionY = RequirePositiveInt32(grid.Metadata, "braze:resolution_y", grid.Name);
            int resolutionZ = RequirePositiveInt32(grid.Metadata, "braze:resolution_z", grid.Name);
            int brickSize = RequirePositiveInt32(grid.Metadata, "braze:brick_size", grid.Name);
            if (brickSize != 4)
                throw new InvalidDataException($"Grid '{grid.Name}' metadata 'braze:brick_size' must be 4.");
            float sourceTime = RequireFloat(grid.Metadata, "braze:source_time", grid.Name);
            float fps = RequireFloat(grid.Metadata, "braze:fps", grid.Name);
            float worldX = RequireFloat(grid.Metadata, "braze:world_x", grid.Name);
            float worldY = RequireFloat(grid.Metadata, "braze:world_y", grid.Name);
            float worldZ = RequireFloat(grid.Metadata, "braze:world_z", grid.Name);
            RequireFinite(sourceTime, $"Grid '{grid.Name}' source time");
            RequireFinitePositive(fps, $"Grid '{grid.Name}' FPS");
            RequireFinitePositive(worldX, $"Grid '{grid.Name}' world X");
            RequireFinitePositive(worldY, $"Grid '{grid.Name}' world Y");
            RequireFinitePositive(worldZ, $"Grid '{grid.Name}' world Z");
            BrazeVdbTransform expectedTransform = BrazeVdbTransform.FromCenteredDomain(
                new BrazeVdbVector3d(worldX, worldY, worldZ),
                new BrazeVdbVector3Int(resolutionX, resolutionY, resolutionZ));
            if (!grid.Transform.Equals(expectedTransform))
                throw new InvalidDataException(
                    $"Grid '{grid.Name}' transform conflicts with its resolution/world metadata.");
            if (grid.Name == "vel")
            {
                if (grid is not BrazeOpenVdbVectorGrid)
                    throw new InvalidDataException("The 'vel' grid must contain Vec3f values.");
                RequireString(grid.Metadata, "vector_type", "contravariant relative", "grid 'vel'");
                RequireString(grid.Metadata, "braze:value_semantics",
                    "simulation velocity, world units per second", "grid 'vel'");
                if (grid.Metadata.TryGet("is_saved_as_half_float", out _))
                    throw new InvalidDataException(
                        "The format-224 compatibility writer cannot mark 'vel' as half-float.");
            }
            else if (grid is not BrazeOpenVdbFloatGrid)
            {
                throw new InvalidDataException($"The '{grid.Name}' grid must contain float values.");
            }
            else
            {
                RequireString(grid.Metadata, "braze:source_channel",
                    grid.Name == "emission" ? "burn" : grid.Name, $"grid '{grid.Name}'");
                RequireString(grid.Metadata, "braze:value_semantics",
                    "conditioned cache channel multiplied by baked gain", $"grid '{grid.Name}'");
                float gain = RequireFloat(grid.Metadata, "braze:gain", grid.Name);
                RequireFinite(gain, $"Grid '{grid.Name}' gain");
                if (gain < 0f) throw new InvalidDataException($"Grid '{grid.Name}' gain cannot be negative.");
                if (!grid.Metadata.TryGet("braze:render_conditioned", out BrazeVdbMetadataValue conditioned) ||
                    conditioned.TypeName != "bool" || conditioned.Value is not bool value || !value)
                    throw new InvalidDataException(
                        $"Grid '{grid.Name}' metadata 'braze:render_conditioned' must be true.");
            }
        }

        static int RequirePositiveInt32(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            int value = RequireInt32(metadata, name, $"grid '{gridName}'");
            if (value <= 0) throw new InvalidDataException($"Grid '{gridName}' metadata '{name}' must be positive.");
            return value;
        }

        static int RequireInt32(BrazeVdbMetadataMap metadata, string name, string owner)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "int32" ||
                value.Value is not int result)
                throw new InvalidDataException($"OpenVDB {owner} metadata requires int32 '{name}'.");
            return result;
        }

        static float RequireFloat(BrazeVdbMetadataMap metadata, string name, string gridName)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "float" ||
                value.Value is not float result)
                throw new InvalidDataException($"OpenVDB grid '{gridName}' metadata requires float '{name}'.");
            return result;
        }

        static void RequireFinite(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException($"{label} must be finite.");
        }

        static void RequireFinitePositive(float value, string label)
        {
            RequireFinite(value, label);
            if (value <= 0f) throw new InvalidDataException($"{label} must be positive.");
        }

        static void RequireString(BrazeVdbMetadataMap metadata, string name, string expected, string owner)
        {
            if (!metadata.TryGet(name, out BrazeVdbMetadataValue value) || value.TypeName != "string" ||
                value.Value is not string text || string.IsNullOrEmpty(text))
                throw new InvalidDataException($"OpenVDB {owner} metadata requires nonempty string '{name}'.");
            if (expected != null && !string.Equals(text, expected, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"OpenVDB {owner} metadata '{name}' must be '{expected}', not '{text}'.");
        }

        static void ValidateUuid(string uuid)
        {
            if (uuid.Length != 36 || !Guid.TryParseExact(uuid, "D", out Guid parsed) ||
                !string.Equals(uuid, parsed.ToString("D").ToUpperInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException("The OpenVDB UUID must be 36-character uppercase D-format text.");
        }
    }

    internal static class BrazeOpenVdbTreeWriter
    {
        const byte NoMaskOrInactiveValuesMode = 0;
        const byte AllValuesMode = 6;
        internal delegate void ValueWriter<T>(BrazeVdbBinaryWriter writer, T value);

        internal static void WriteFloat(BrazeVdbBinaryWriter writer, float value) => writer.WriteSingle(value);

        internal static void WriteVector(BrazeVdbBinaryWriter writer, Vector3 value)
        {
            writer.WriteSingle(value.x);
            writer.WriteSingle(value.y);
            writer.WriteSingle(value.z);
        }

        internal static BrazeOpenVdbTreeWriteResult WriteTopology<T>(BrazeVdbBinaryWriter writer,
            BrazeVdbTree<T> tree, ValueWriter<T> writeValue, uint compression, BrazeVdbZipBuffer zipBuffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectTiles(tree);
            var leaves = new List<BrazeVdbLeaf<T>>();
            writer.WriteUInt32(1);
            writeValue(writer, tree.Background);
            writer.WriteUInt32(0);
            writer.WriteUInt32((uint)tree.ChildCount);
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbUpperNode<T>> upperPair in tree.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteCoordinate(writer, upperPair.Key);
                WriteUpperTopology(writer, upperPair.Value, tree.Background, writeValue, compression, zipBuffer,
                    leaves, cancellationToken);
            }
            return new BrazeOpenVdbTreeWriteResult(leaves);
        }

        internal static void WriteLeafBuffers<T>(BrazeVdbBinaryWriter writer, BrazeVdbTree<T> tree,
            BrazeOpenVdbTreeWriteResult result, ValueWriter<T> writeValue, uint compression,
            BrazeVdbZipBuffer zipBuffer, CancellationToken cancellationToken)
        {
            if (result?.Leaves is not List<BrazeVdbLeaf<T>> leaves)
                throw new InvalidDataException("OpenVDB leaf-buffer state is incompatible with the grid type.");
            for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrazeVdbLeaf<T> leaf = leaves[leafIndex];
                leaf.ActiveMask.Write(writer);
                WriteValues(writer, leaf.ActiveMask, BrazeVdbTreeLayout.LeafVoxelCount,
                    offset => leaf.ActiveMask.IsOn(offset) ? leaf.ValueAt(offset) : tree.Background, writeValue,
                    compression, zipBuffer);
            }
        }

        static void WriteUpperTopology<T>(BrazeVdbBinaryWriter writer, BrazeVdbUpperNode<T> node, T background,
            ValueWriter<T> writeValue, uint compression, BrazeVdbZipBuffer zipBuffer,
            List<BrazeVdbLeaf<T>> leaves, CancellationToken cancellationToken)
        {
            var childMask = new BrazeVdbMask(BrazeVdbTreeLayout.UpperSlotCount);
            foreach (KeyValuePair<int, BrazeVdbLowerNode<T>> child in node.Children) childMask.Set(child.Key);
            childMask.Write(writer);
            var valueMask = new BrazeVdbMask(BrazeVdbTreeLayout.UpperSlotCount);
            valueMask.Write(writer);
            WriteValues(writer, valueMask, BrazeVdbTreeLayout.UpperSlotCount, _ => background, writeValue,
                compression, zipBuffer);
            foreach (KeyValuePair<int, BrazeVdbLowerNode<T>> child in node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteLowerTopology(writer, child.Value, background, writeValue, compression, zipBuffer, leaves,
                    cancellationToken);
            }
        }

        static void WriteLowerTopology<T>(BrazeVdbBinaryWriter writer, BrazeVdbLowerNode<T> node, T background,
            ValueWriter<T> writeValue, uint compression, BrazeVdbZipBuffer zipBuffer,
            List<BrazeVdbLeaf<T>> leaves, CancellationToken cancellationToken)
        {
            var childMask = new BrazeVdbMask(BrazeVdbTreeLayout.LowerSlotCount);
            foreach (KeyValuePair<int, BrazeVdbLeaf<T>> child in node.Children) childMask.Set(child.Key);
            childMask.Write(writer);
            var valueMask = new BrazeVdbMask(BrazeVdbTreeLayout.LowerSlotCount);
            valueMask.Write(writer);
            WriteValues(writer, valueMask, BrazeVdbTreeLayout.LowerSlotCount, _ => background, writeValue,
                compression, zipBuffer);
            foreach (KeyValuePair<int, BrazeVdbLeaf<T>> child in node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                child.Value.ActiveMask.Write(writer);
                leaves.Add(child.Value);
            }
        }

        static void WriteValues<T>(BrazeVdbBinaryWriter writer, BrazeVdbMask valueMask, int valueCount,
            Func<int, T> valueAt, ValueWriter<T> writeValue, uint compression, BrazeVdbZipBuffer zipBuffer)
        {
            bool activeMask = (compression & BrazeVdbProfile.CompressionActiveMask) != 0;
            bool zip = (compression & BrazeVdbProfile.CompressionZip) != 0;
            writer.WriteByte(activeMask ? NoMaskOrInactiveValuesMode : AllValuesMode);

            void WriteSelected(BrazeVdbBinaryWriter destination)
            {
                for (int offset = 0; offset < valueCount; offset++)
                    if (!activeMask || valueMask.IsOn(offset)) writeValue(destination, valueAt(offset));
            }

            if (zip) zipBuffer.WriteFramed(writer, WriteSelected);
            else WriteSelected(writer);
        }

        static void RejectTiles<T>(BrazeVdbTree<T> tree)
        {
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbTile<T>> _ in tree.RootTiles)
                throw new InvalidDataException("The canonical BrazeFX writer does not emit root tiles.");
            foreach (BrazeVdbTreeRecord<T> _ in tree.EnumerateUpperTiles())
                throw new InvalidDataException("The canonical BrazeFX writer does not emit upper tiles.");
            foreach (BrazeVdbTreeRecord<T> _ in tree.EnumerateLowerTiles())
                throw new InvalidDataException("The canonical BrazeFX writer does not emit lower tiles.");
        }

        static void WriteCoordinate(BrazeVdbBinaryWriter writer, BrazeVdbCoord coordinate)
        {
            writer.WriteInt32(coordinate.x);
            writer.WriteInt32(coordinate.y);
            writer.WriteInt32(coordinate.z);
        }
    }
}
