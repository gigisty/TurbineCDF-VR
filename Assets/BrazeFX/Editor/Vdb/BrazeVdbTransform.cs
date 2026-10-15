using System;
using System.IO;

namespace BrazeFX.EditorTools.Vdb
{
    internal readonly struct BrazeVdbVector3d : IEquatable<BrazeVdbVector3d>
    {
        public readonly double x;
        public readonly double y;
        public readonly double z;

        public BrazeVdbVector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(BrazeVdbVector3d other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);
        public override bool Equals(object value) => value is BrazeVdbVector3d other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"[{x:R}, {y:R}, {z:R}]";
    }

    /// <summary>Positive axis-aligned cell-centred transform supported by the first-party interchange profile.</summary>
    internal readonly struct BrazeVdbTransform : IEquatable<BrazeVdbTransform>
    {
        const string UniformMap = "UniformScaleTranslateMap";
        const string ScaleMap = "ScaleTranslateMap";
        const double ValidationTolerance = 1e-12;

        public readonly BrazeVdbVector3d voxelSize;
        public readonly BrazeVdbVector3d origin;

        public string MapType => IsUniform ? UniformMap : ScaleMap;
        public bool IsUniform => NearlyEqual(voxelSize.x, voxelSize.y) && NearlyEqual(voxelSize.x, voxelSize.z);

        public BrazeVdbTransform(BrazeVdbVector3d voxelSize, BrazeVdbVector3d origin)
        {
            ValidateFinitePositive(voxelSize.x, nameof(voxelSize));
            ValidateFinitePositive(voxelSize.y, nameof(voxelSize));
            ValidateFinitePositive(voxelSize.z, nameof(voxelSize));
            ValidateFinite(origin.x, nameof(origin));
            ValidateFinite(origin.y, nameof(origin));
            ValidateFinite(origin.z, nameof(origin));
            this.voxelSize = voxelSize;
            this.origin = origin;
        }

        public static BrazeVdbTransform FromCenteredDomain(BrazeVdbVector3d worldSize,
            BrazeVdbVector3Int resolution)
        {
            if (resolution.x <= 0 || resolution.y <= 0 || resolution.z <= 0)
                throw new ArgumentOutOfRangeException(nameof(resolution), "VDB resolution must be positive.");
            ValidateFinitePositive(worldSize.x, nameof(worldSize));
            ValidateFinitePositive(worldSize.y, nameof(worldSize));
            ValidateFinitePositive(worldSize.z, nameof(worldSize));
            var voxel = new BrazeVdbVector3d(worldSize.x / resolution.x, worldSize.y / resolution.y,
                worldSize.z / resolution.z);
            var origin = new BrazeVdbVector3d(-worldSize.x * 0.5 + voxel.x * 0.5,
                -worldSize.y * 0.5 + voxel.y * 0.5, -worldSize.z * 0.5 + voxel.z * 0.5);
            return new BrazeVdbTransform(voxel, origin);
        }

        public BrazeVdbVector3d IndexToWorld(BrazeVdbVector3d index) => new(
            origin.x + index.x * voxelSize.x,
            origin.y + index.y * voxelSize.y,
            origin.z + index.z * voxelSize.z);

        public BrazeVdbVector3d WorldSize(BrazeVdbVector3Int resolution) => new(
            voxelSize.x * resolution.x, voxelSize.y * resolution.y, voxelSize.z * resolution.z);

        /// <summary>Write the observed OpenVDB 13 ScaleTranslateMap payload: map name plus 18 doubles.</summary>
        public void Write(BrazeVdbBinaryWriter writer, BrazeVdbSafetyLimits limits = null)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            limits ??= BrazeVdbSafetyLimits.Default;
            writer.WriteString(MapType, limits.MaxStringBytes, "VDB transform map type");
            WriteVector(writer, origin);
            WriteVector(writer, voxelSize);
            WriteVector(writer, voxelSize);
            var inverse = new BrazeVdbVector3d(1.0 / voxelSize.x, 1.0 / voxelSize.y, 1.0 / voxelSize.z);
            WriteVector(writer, inverse);
            WriteVector(writer, new BrazeVdbVector3d(inverse.x * inverse.x, inverse.y * inverse.y,
                inverse.z * inverse.z));
            WriteVector(writer, new BrazeVdbVector3d(inverse.x * 0.5, inverse.y * 0.5, inverse.z * 0.5));
        }

        public static BrazeVdbTransform Read(BrazeVdbBinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            string type = reader.ReadString("VDB transform map type");
            if (type != UniformMap && type != ScaleMap)
                throw new NotSupportedException(
                    $"VDB transform '{type}' is unsupported; only positive axis-aligned scale/translate maps are accepted.");
            BrazeVdbVector3d origin = ReadVector(reader, "VDB transform origin");
            BrazeVdbVector3d scale = ReadVector(reader, "VDB transform scale");
            BrazeVdbVector3d voxel = ReadVector(reader, "VDB transform voxel size");
            BrazeVdbVector3d inverse = ReadVector(reader, "VDB transform inverse scale");
            BrazeVdbVector3d inverseSquared = ReadVector(reader, "VDB transform inverse squared scale");
            BrazeVdbVector3d halfInverse = ReadVector(reader, "VDB transform half inverse scale");
            var result = new BrazeVdbTransform(scale, origin);
            if (type == UniformMap && !result.IsUniform)
                throw new InvalidDataException("UniformScaleTranslateMap contains nonuniform scale values.");
            ValidateRedundantVector(voxel, scale, "voxel size");
            var expectedInverse = new BrazeVdbVector3d(1.0 / scale.x, 1.0 / scale.y, 1.0 / scale.z);
            ValidateRedundantVector(inverse, expectedInverse, "inverse scale");
            ValidateRedundantVector(inverseSquared,
                new BrazeVdbVector3d(expectedInverse.x * expectedInverse.x,
                    expectedInverse.y * expectedInverse.y, expectedInverse.z * expectedInverse.z),
                "inverse squared scale");
            ValidateRedundantVector(halfInverse,
                new BrazeVdbVector3d(expectedInverse.x * 0.5, expectedInverse.y * 0.5,
                    expectedInverse.z * 0.5), "half inverse scale");
            return result;
        }

        static void WriteVector(BrazeVdbBinaryWriter writer, BrazeVdbVector3d vector)
        {
            writer.WriteDouble(vector.x);
            writer.WriteDouble(vector.y);
            writer.WriteDouble(vector.z);
        }

        static BrazeVdbVector3d ReadVector(BrazeVdbBinaryReader reader, string label) => new(
            reader.ReadDouble(label + " X"), reader.ReadDouble(label + " Y"), reader.ReadDouble(label + " Z"));

        static void ValidateRedundantVector(BrazeVdbVector3d actual, BrazeVdbVector3d expected, string label)
        {
            if (!NearlyEqual(actual.x, expected.x) || !NearlyEqual(actual.y, expected.y) ||
                !NearlyEqual(actual.z, expected.z))
                throw new InvalidDataException($"VDB transform {label} is inconsistent with its scale.");
        }

        static bool NearlyEqual(double a, double b)
        {
            double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            return Math.Abs(a - b) <= ValidationTolerance * scale;
        }

        static void ValidateFinitePositive(double value, string label)
        {
            ValidateFinite(value, label);
            if (value <= 0.0) throw new ArgumentOutOfRangeException(label, "VDB transform scale must be positive.");
        }

        static void ValidateFinite(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(label, "VDB transform values must be finite.");
        }

        public bool Equals(BrazeVdbTransform other) => voxelSize.Equals(other.voxelSize) && origin.Equals(other.origin);
        public override bool Equals(object value) => value is BrazeVdbTransform other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(voxelSize, origin);
    }
}
