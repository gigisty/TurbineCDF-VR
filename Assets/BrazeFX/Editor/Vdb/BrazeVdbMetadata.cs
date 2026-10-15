using System;
using System.Collections.Generic;
using System.IO;

namespace BrazeFX.EditorTools.Vdb
{
    internal readonly struct BrazeVdbVector3Int : IEquatable<BrazeVdbVector3Int>
    {
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public BrazeVdbVector3Int(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(BrazeVdbVector3Int other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object value) => value is BrazeVdbVector3Int other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"[{x}, {y}, {z}]";
    }

    /// <summary>A bounded metadata value from the documented BrazeFX profile, or a preserved unknown payload.</summary>
    internal sealed class BrazeVdbMetadataValue
    {
        public string TypeName { get; }
        public object Value { get; }
        public byte[] UnknownPayload { get; }

        BrazeVdbMetadataValue(string typeName, object value, byte[] unknownPayload = null)
        {
            TypeName = typeName;
            Value = value;
            UnknownPayload = unknownPayload;
        }

        public static BrazeVdbMetadataValue String(string value) => new("string", value ?? string.Empty);
        public static BrazeVdbMetadataValue Boolean(bool value) => new("bool", value);
        public static BrazeVdbMetadataValue Int32(int value) => new("int32", value);
        public static BrazeVdbMetadataValue Int64(long value) => new("int64", value);
        public static BrazeVdbMetadataValue Float(float value) => new("float", value);
        public static BrazeVdbMetadataValue Vec3I(BrazeVdbVector3Int value) => new("vec3i", value);

        public static BrazeVdbMetadataValue Unknown(string typeName, byte[] payload)
        {
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("Metadata type is empty.", nameof(typeName));
            return new BrazeVdbMetadataValue(typeName, null,
                payload != null ? (byte[])payload.Clone() : throw new ArgumentNullException(nameof(payload)));
        }
    }

    /// <summary>Lexically ordered OpenVDB metadata map using the profile's size-prefixed entry representation.</summary>
    internal sealed class BrazeVdbMetadataMap
    {
        readonly SortedDictionary<string, BrazeVdbMetadataValue> values = new(StringComparer.Ordinal);

        public int Count => values.Count;
        public IEnumerable<KeyValuePair<string, BrazeVdbMetadataValue>> Entries => values;

        public void Add(string name, BrazeVdbMetadataValue value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Metadata name is empty.", nameof(name));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (values.ContainsKey(name)) throw new InvalidDataException($"Duplicate VDB metadata key '{name}'.");
            values.Add(name, value);
        }

        public bool TryGet(string name, out BrazeVdbMetadataValue value) => values.TryGetValue(name, out value);

        public T Get<T>(string name)
        {
            if (!values.TryGetValue(name, out BrazeVdbMetadataValue metadata))
                throw new KeyNotFoundException($"VDB metadata '{name}' does not exist.");
            if (metadata.Value is not T value)
                throw new InvalidDataException($"VDB metadata '{name}' is {metadata.TypeName}, not {typeof(T).Name}.");
            return value;
        }

        public void Write(BrazeVdbBinaryWriter writer, BrazeVdbSafetyLimits limits = null)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            limits ??= BrazeVdbSafetyLimits.Default;
            if (values.Count > limits.MaxMetadataEntries)
                throw new InvalidDataException($"Metadata entry count exceeds {limits.MaxMetadataEntries}.");
            writer.WriteInt32(values.Count);
            foreach (KeyValuePair<string, BrazeVdbMetadataValue> pair in values)
            {
                writer.WriteString(pair.Key, limits.MaxStringBytes, "VDB metadata name");
                writer.WriteString(pair.Value.TypeName, limits.MaxStringBytes, "VDB metadata type");
                WritePayload(writer, pair.Key, pair.Value, limits);
            }
        }

        static void WritePayload(BrazeVdbBinaryWriter writer, string name, BrazeVdbMetadataValue metadata,
            BrazeVdbSafetyLimits limits)
        {
            switch (metadata.TypeName)
            {
                case "string":
                {
                    byte[] bytes = writer.EncodeUtf8((string)metadata.Value, limits.MaxStringBytes,
                        $"VDB metadata '{name}'");
                    writer.WriteUInt32((uint)bytes.Length);
                    writer.WriteBytes(bytes);
                    break;
                }
                case "bool":
                    writer.WriteUInt32(1);
                    writer.WriteBoolean((bool)metadata.Value);
                    break;
                case "int32":
                    writer.WriteUInt32(sizeof(int));
                    writer.WriteInt32((int)metadata.Value);
                    break;
                case "int64":
                    writer.WriteUInt32(sizeof(long));
                    writer.WriteInt64((long)metadata.Value);
                    break;
                case "float":
                    writer.WriteUInt32(sizeof(float));
                    writer.WriteSingle((float)metadata.Value);
                    break;
                case "vec3i":
                {
                    writer.WriteUInt32(sizeof(int) * 3);
                    var vector = (BrazeVdbVector3Int)metadata.Value;
                    writer.WriteInt32(vector.x);
                    writer.WriteInt32(vector.y);
                    writer.WriteInt32(vector.z);
                    break;
                }
                default:
                {
                    byte[] payload = metadata.UnknownPayload ?? throw new InvalidDataException(
                        $"Metadata '{name}' has unsupported type '{metadata.TypeName}'.");
                    if (payload.LongLength > limits.MaxAllocationBytes || payload.LongLength > uint.MaxValue)
                        throw new InvalidDataException($"Metadata '{name}' payload exceeds the allocation ceiling.");
                    writer.WriteUInt32((uint)payload.Length);
                    writer.WriteBytes(payload);
                    break;
                }
            }
        }

        public static BrazeVdbMetadataMap Read(BrazeVdbBinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            int count = reader.ReadCount32(reader.Limits.MaxMetadataEntries, "VDB metadata entry count");
            var result = new BrazeVdbMetadataMap();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString($"VDB metadata name {i}");
                string type = reader.ReadString($"VDB metadata type for '{name}'");
                uint sizeValue = reader.ReadUInt32($"VDB metadata payload size for '{name}'");
                if (sizeValue > int.MaxValue || sizeValue > reader.Limits.MaxAllocationBytes)
                    throw new InvalidDataException($"VDB metadata '{name}' payload is too large.");
                int size = (int)sizeValue;
                reader.RequireAvailable(size, $"VDB metadata '{name}' payload");
                long end = checked(reader.Position + size);
                BrazeVdbMetadataValue value = ReadPayload(reader, name, type, size);
                if (reader.Position != end)
                    throw new InvalidDataException($"VDB metadata '{name}' did not consume its declared payload size.");
                result.Add(name, value);
            }
            return result;
        }

        static BrazeVdbMetadataValue ReadPayload(BrazeVdbBinaryReader reader, string name, string type, int size)
        {
            switch (type)
            {
                case "string": return BrazeVdbMetadataValue.String(reader.ReadUtf8(size, $"VDB metadata '{name}'"));
                case "bool":
                    RequireSize(name, type, size, 1);
                    return BrazeVdbMetadataValue.Boolean(reader.ReadBoolean($"VDB metadata '{name}'"));
                case "int32":
                    RequireSize(name, type, size, sizeof(int));
                    return BrazeVdbMetadataValue.Int32(reader.ReadInt32($"VDB metadata '{name}'"));
                case "int64":
                    RequireSize(name, type, size, sizeof(long));
                    return BrazeVdbMetadataValue.Int64(reader.ReadInt64($"VDB metadata '{name}'"));
                case "float":
                    RequireSize(name, type, size, sizeof(float));
                    return BrazeVdbMetadataValue.Float(reader.ReadSingle($"VDB metadata '{name}'"));
                case "vec3i":
                    RequireSize(name, type, size, sizeof(int) * 3);
                    return BrazeVdbMetadataValue.Vec3I(new BrazeVdbVector3Int(
                        reader.ReadInt32($"VDB metadata '{name}' X"),
                        reader.ReadInt32($"VDB metadata '{name}' Y"),
                        reader.ReadInt32($"VDB metadata '{name}' Z")));
                default:
                    return BrazeVdbMetadataValue.Unknown(type, reader.ReadBytes(size, $"VDB metadata '{name}'"));
            }
        }

        static void RequireSize(string name, string type, int actual, int expected)
        {
            if (actual != expected)
                throw new InvalidDataException(
                    $"VDB metadata '{name}' type {type} declares {actual} bytes; {expected} are required.");
        }
    }
}
