using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace BrazeFX.EditorTools.Vdb
{
    /// <summary>Converts the existing sparse six-half Braze frame into canonical OpenVDB trees without a dense copy.</summary>
    internal static class BrazeVdbFrameBuilder
    {
        const string Creator = "BrazeFX Export Studio";
        const string CoordinateSystem = "Unity local space, Y-up, left-handed (+Z forward)";
        const string ScalarSemantics = "conditioned cache channel multiplied by baked gain";
        const string VelocitySemantics = "simulation velocity, world units per second";

        public static BrazeOpenVdbDocument Build(string uuid, in BrazeFluidCacheFormat.Header header,
            BrazeDecodedCacheFrame frame, BrazeExportSession.VolumeGrids grids, float threshold,
            int sourceFrame, float sourceTime, float fps, string units, string sourceName,
            float densityGain, float temperatureGain, float emissionGain,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInputs(header, frame, grids, threshold, sourceTime, fps, densityGain, temperatureGain,
                emissionGain);
            units = string.IsNullOrWhiteSpace(units) ? "meters" : units;
            sourceName = string.IsNullOrWhiteSpace(sourceName) ? "BrazeFX" : sourceName;

            BrazeVdbTransform transform = BrazeVdbTransform.FromCenteredDomain(
                new BrazeVdbVector3d(header.worldSize.x, header.worldSize.y, header.worldSize.z),
                new BrazeVdbVector3Int(header.baseRes.x, header.baseRes.y, header.baseRes.z));
            var density = IsSelected(grids, BrazeExportSession.VolumeGrids.Density)
                ? new BrazeVdbTree<float>(0f) : null;
            var temperature = IsSelected(grids, BrazeExportSession.VolumeGrids.Temperature)
                ? new BrazeVdbTree<float>(0f) : null;
            var emission = IsSelected(grids, BrazeExportSession.VolumeGrids.Emission)
                ? new BrazeVdbTree<float>(0f) : null;
            var velocity = IsSelected(grids, BrazeExportSession.VolumeGrids.Velocity)
                ? new BrazeVdbTree<Vector3>(Vector3.zero) : null;

            PopulateTrees(header, frame, Mathf.Max(0f, threshold), densityGain, temperatureGain, emissionGain,
                density, temperature, emission, velocity, cancellationToken);

            var document = new BrazeOpenVdbDocument(uuid, BuildFileMetadata(sourceName, units));
            if (density != null)
                document.AddGrid(new BrazeOpenVdbFloatGrid("density",
                    BuildScalarMetadata("density", "density", header, sourceFrame, sourceTime, fps, units,
                        sourceName, densityGain), transform, density));
            if (temperature != null)
                document.AddGrid(new BrazeOpenVdbFloatGrid("temperature",
                    BuildScalarMetadata("temperature", "temperature", header, sourceFrame, sourceTime, fps, units,
                        sourceName, temperatureGain), transform, temperature));
            if (emission != null)
                document.AddGrid(new BrazeOpenVdbFloatGrid("emission",
                    BuildScalarMetadata("emission", "burn", header, sourceFrame, sourceTime, fps, units,
                        sourceName, emissionGain), transform, emission));
            if (velocity != null)
                document.AddGrid(new BrazeOpenVdbVectorGrid("vel",
                    BuildVelocityMetadata(header, sourceFrame, sourceTime, fps, units, sourceName), transform,
                    velocity));
            return document;
        }

        /// <summary>Build each selected sparse grid only when the writer reaches it, limiting live tree memory.</summary>
        public static BrazeOpenVdbStreamingDocument BuildStreaming(string uuid,
            in BrazeFluidCacheFormat.Header header, BrazeDecodedCacheFrame frame,
            BrazeExportSession.VolumeGrids grids, float threshold, int sourceFrame, float sourceTime, float fps,
            string units, string sourceName, float densityGain, float temperatureGain, float emissionGain,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInputs(header, frame, grids, threshold, sourceTime, fps, densityGain, temperatureGain,
                emissionGain);
            units = string.IsNullOrWhiteSpace(units) ? "meters" : units;
            sourceName = string.IsNullOrWhiteSpace(sourceName) ? "BrazeFX" : sourceName;
            float normalizedThreshold = Mathf.Max(0f, threshold);
            BrazeFluidCacheFormat.Header frameHeader = header;
            BrazeVdbTransform transform = BrazeVdbTransform.FromCenteredDomain(
                new BrazeVdbVector3d(frameHeader.worldSize.x, frameHeader.worldSize.y, frameHeader.worldSize.z),
                new BrazeVdbVector3Int(frameHeader.baseRes.x, frameHeader.baseRes.y, frameHeader.baseRes.z));
            var document = new BrazeOpenVdbStreamingDocument(uuid, BuildFileMetadata(sourceName, units));

            if (IsSelected(grids, BrazeExportSession.VolumeGrids.Density))
                document.AddGrid(new BrazeOpenVdbGridSource("density", token =>
                {
                    var tree = new BrazeVdbTree<float>(0f);
                    PopulateTrees(frameHeader, frame, normalizedThreshold, densityGain, temperatureGain,
                        emissionGain, tree, null, null, null, token);
                    return new BrazeOpenVdbFloatGrid("density",
                        BuildScalarMetadata("density", "density", frameHeader, sourceFrame, sourceTime, fps, units,
                            sourceName, densityGain), transform, tree);
                }));
            if (IsSelected(grids, BrazeExportSession.VolumeGrids.Temperature))
                document.AddGrid(new BrazeOpenVdbGridSource("temperature", token =>
                {
                    var tree = new BrazeVdbTree<float>(0f);
                    PopulateTrees(frameHeader, frame, normalizedThreshold, densityGain, temperatureGain,
                        emissionGain, null, tree, null, null, token);
                    return new BrazeOpenVdbFloatGrid("temperature",
                        BuildScalarMetadata("temperature", "temperature", frameHeader, sourceFrame, sourceTime,
                            fps, units, sourceName, temperatureGain), transform, tree);
                }));
            if (IsSelected(grids, BrazeExportSession.VolumeGrids.Emission))
                document.AddGrid(new BrazeOpenVdbGridSource("emission", token =>
                {
                    var tree = new BrazeVdbTree<float>(0f);
                    PopulateTrees(frameHeader, frame, normalizedThreshold, densityGain, temperatureGain,
                        emissionGain, null, null, tree, null, token);
                    return new BrazeOpenVdbFloatGrid("emission",
                        BuildScalarMetadata("emission", "burn", frameHeader, sourceFrame, sourceTime, fps, units,
                            sourceName, emissionGain), transform, tree);
                }));
            if (IsSelected(grids, BrazeExportSession.VolumeGrids.Velocity))
                document.AddGrid(new BrazeOpenVdbGridSource("vel", token =>
                {
                    var tree = new BrazeVdbTree<Vector3>(Vector3.zero);
                    PopulateTrees(frameHeader, frame, normalizedThreshold, densityGain, temperatureGain,
                        emissionGain, null, null, null, tree, token);
                    return new BrazeOpenVdbVectorGrid("vel",
                        BuildVelocityMetadata(frameHeader, sourceFrame, sourceTime, fps, units, sourceName),
                        transform, tree);
                }));
            return document;
        }

        static BrazeVdbMetadataMap BuildFileMetadata(string sourceName, string units)
        {
            var metadata = new BrazeVdbMetadataMap();
            metadata.Add("creator", BrazeVdbMetadataValue.String(Creator));
            metadata.Add("braze:source", BrazeVdbMetadataValue.String(sourceName));
            metadata.Add("braze:units", BrazeVdbMetadataValue.String(units));
            return metadata;
        }

        static void PopulateTrees(in BrazeFluidCacheFormat.Header header, BrazeDecodedCacheFrame frame,
            float threshold, float densityGain, float temperatureGain, float emissionGain,
            BrazeVdbTree<float> density, BrazeVdbTree<float> temperature, BrazeVdbTree<float> emission,
            BrazeVdbTree<Vector3> velocity, CancellationToken cancellationToken)
        {
            Vector3Int brickGrid = header.BrickGrid;
            long brickPlane;
            long brickCapacity;
            try
            {
                brickPlane = checked((long)brickGrid.x * brickGrid.y);
                brickCapacity = checked(brickPlane * brickGrid.z);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The Braze brick grid exceeds managed VDB coordinate limits.",
                    exception);
            }
            int voxelsPerBrick = header.BaseBlockVoxels;
            int shortsPerBrick = header.BaseBlockShorts;
            Vector3 voxelSize = new(header.worldSize.x / header.baseRes.x,
                header.worldSize.y / header.baseRes.y, header.worldSize.z / header.baseRes.z);
            var seen = new HashSet<uint>();
            for (int brick = 0; brick < frame.brickCount; brick++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint flat = frame.indices[brick];
                if ((flat & BrazeFluidCacheFormat.WarpBrickBit) != 0u) continue;
                if (flat >= brickCapacity)
                    throw new InvalidDataException($"Braze brick index {flat} is outside the declared base domain.");
                if (!seen.Add(flat)) throw new InvalidDataException($"Duplicate Braze base brick index {flat}.");
                int brickZ = (int)(flat / brickPlane);
                int remainder = (int)(flat - brickZ * brickPlane);
                int brickY = remainder / brickGrid.x;
                int brickX = remainder - brickY * brickGrid.x;
                int brickOffset = checked(brick * shortsPerBrick);

                for (int local = 0; local < voxelsPerBrick; local++)
                {
                    int localX = local % header.brickSize;
                    int localY = local / header.brickSize % header.brickSize;
                    int localZ = local / (header.brickSize * header.brickSize);
                    int x = brickX * header.brickSize + localX;
                    int y = brickY * header.brickSize + localY;
                    int z = brickZ * header.brickSize + localZ;
                    if (x >= header.baseRes.x || y >= header.baseRes.y || z >= header.baseRes.z) continue;
                    var coordinate = new BrazeVdbCoord(x, y, z);
                    int valueOffset = checked(brickOffset + local * BrazeFluidCacheFormat.BaseChannels);
                    AddScalar(density, coordinate, frame.blocks[valueOffset], densityGain, threshold);
                    AddScalar(temperature, coordinate, frame.blocks[valueOffset + 1], temperatureGain, threshold);
                    AddScalar(emission, coordinate, frame.blocks[valueOffset + 2], emissionGain, threshold);
                    if (velocity == null) continue;
                    var value = new Vector3(
                        BrazeFluidCacheCodec.HalfToFloat(frame.blocks[valueOffset + 3]) * voxelSize.x,
                        BrazeFluidCacheCodec.HalfToFloat(frame.blocks[valueOffset + 4]) * voxelSize.y,
                        BrazeFluidCacheCodec.HalfToFloat(frame.blocks[valueOffset + 5]) * voxelSize.z);
                    ValidateFinite(value.x, "velocity X");
                    ValidateFinite(value.y, "velocity Y");
                    ValidateFinite(value.z, "velocity Z");
                    if (value.x != 0f || value.y != 0f || value.z != 0f) velocity.AddActive(coordinate, value);
                }
            }
        }

        static void AddScalar(BrazeVdbTree<float> tree, BrazeVdbCoord coordinate, ushort half, float gain,
            float threshold)
        {
            if (tree == null) return;
            float source = BrazeFluidCacheCodec.HalfToFloat(half);
            ValidateFinite(source, "scalar cache value");
            float value = Mathf.Max(0f, source) * gain;
            ValidateFinite(value, "conditioned scalar value");
            if (value > threshold) tree.AddActive(coordinate, value);
        }

        static BrazeVdbMetadataMap BuildScalarMetadata(string name, string sourceChannel,
            in BrazeFluidCacheFormat.Header header, int sourceFrame, float sourceTime, float fps, string units,
            string sourceName, float gain)
        {
            BrazeVdbMetadataMap metadata = BuildCommonMetadata(name, "fog volume", header, sourceFrame, sourceTime,
                fps, units, sourceName);
            metadata.Add("braze:render_conditioned", BrazeVdbMetadataValue.Boolean(true));
            metadata.Add("braze:gain", BrazeVdbMetadataValue.Float(gain));
            metadata.Add("braze:source_channel", BrazeVdbMetadataValue.String(sourceChannel));
            metadata.Add("braze:value_semantics", BrazeVdbMetadataValue.String(ScalarSemantics));
            return metadata;
        }

        static BrazeVdbMetadataMap BuildVelocityMetadata(in BrazeFluidCacheFormat.Header header, int sourceFrame,
            float sourceTime, float fps, string units, string sourceName)
        {
            BrazeVdbMetadataMap metadata = BuildCommonMetadata("vel", "unknown", header, sourceFrame, sourceTime,
                fps, units, sourceName);
            metadata.Add("vector_type", BrazeVdbMetadataValue.String("contravariant relative"));
            metadata.Add("braze:value_semantics", BrazeVdbMetadataValue.String(VelocitySemantics));
            return metadata;
        }

        static BrazeVdbMetadataMap BuildCommonMetadata(string name, string gridClass,
            in BrazeFluidCacheFormat.Header header, int sourceFrame, float sourceTime, float fps, string units,
            string sourceName)
        {
            var metadata = new BrazeVdbMetadataMap();
            metadata.Add("name", BrazeVdbMetadataValue.String(name));
            metadata.Add("class", BrazeVdbMetadataValue.String(gridClass));
            metadata.Add("braze:brick_size", BrazeVdbMetadataValue.Int32(header.brickSize));
            metadata.Add("braze:coordinate_system", BrazeVdbMetadataValue.String(CoordinateSystem));
            metadata.Add("braze:fps", BrazeVdbMetadataValue.Float(fps));
            metadata.Add("braze:resolution_x", BrazeVdbMetadataValue.Int32(header.baseRes.x));
            metadata.Add("braze:resolution_y", BrazeVdbMetadataValue.Int32(header.baseRes.y));
            metadata.Add("braze:resolution_z", BrazeVdbMetadataValue.Int32(header.baseRes.z));
            metadata.Add("braze:source", BrazeVdbMetadataValue.String(sourceName));
            metadata.Add("braze:source_frame", BrazeVdbMetadataValue.Int32(sourceFrame));
            metadata.Add("braze:source_time", BrazeVdbMetadataValue.Float(sourceTime));
            metadata.Add("braze:units", BrazeVdbMetadataValue.String(units));
            metadata.Add("braze:world_x", BrazeVdbMetadataValue.Float(header.worldSize.x));
            metadata.Add("braze:world_y", BrazeVdbMetadataValue.Float(header.worldSize.y));
            metadata.Add("braze:world_z", BrazeVdbMetadataValue.Float(header.worldSize.z));
            return metadata;
        }

        static void ValidateInputs(in BrazeFluidCacheFormat.Header header, BrazeDecodedCacheFrame frame,
            BrazeExportSession.VolumeGrids grids, float threshold, float sourceTime, float fps, float densityGain,
            float temperatureGain, float emissionGain)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (header.brickSize != 4) throw new InvalidDataException("Managed VDB export requires 4x4x4 Braze bricks.");
            if (header.baseRes.x <= 0 || header.baseRes.y <= 0 || header.baseRes.z <= 0)
                throw new InvalidDataException("Managed VDB export requires a positive base resolution.");
            ValidateFinitePositive(header.worldSize.x, "world size X");
            ValidateFinitePositive(header.worldSize.y, "world size Y");
            ValidateFinitePositive(header.worldSize.z, "world size Z");
            ValidateFiniteNonnegative(threshold, "activation threshold");
            ValidateFinite(sourceTime, "source time");
            ValidateFinitePositive(fps, "FPS");
            ValidateFiniteNonnegative(densityGain, "density gain");
            ValidateFiniteNonnegative(temperatureGain, "temperature gain");
            ValidateFiniteNonnegative(emissionGain, "emission gain");
            const BrazeExportSession.VolumeGrids supported = BrazeExportSession.VolumeGrids.Density |
                BrazeExportSession.VolumeGrids.Temperature | BrazeExportSession.VolumeGrids.Emission |
                BrazeExportSession.VolumeGrids.Velocity;
            if (grids == 0 || (grids & ~supported) != 0)
                throw new InvalidDataException("Managed VDB export requires at least one supported grid.");
            if (frame.brickCount < 0 || frame.indices == null || frame.blocks == null ||
                frame.indices.Length < frame.brickCount ||
                frame.blocks.LongLength < (long)frame.brickCount * header.BaseBlockShorts)
                throw new InvalidDataException("The decoded Braze frame buffers are truncated or inconsistent.");
        }

        static bool IsSelected(BrazeExportSession.VolumeGrids value, BrazeExportSession.VolumeGrids flag)
            => (value & flag) != 0;

        static void ValidateFiniteNonnegative(float value, string label)
        {
            ValidateFinite(value, label);
            if (value < 0f) throw new InvalidDataException($"Managed VDB {label} cannot be negative.");
        }

        static void ValidateFinitePositive(float value, string label)
        {
            ValidateFinite(value, label);
            if (value <= 0f) throw new InvalidDataException($"Managed VDB {label} must be positive.");
        }

        static void ValidateFinite(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException($"Managed VDB {label} must be finite.");
        }
    }
}
