using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrazeFX.EditorTools.Vdb
{
    internal readonly struct BrazeVdbCoord : IEquatable<BrazeVdbCoord>, IComparable<BrazeVdbCoord>
    {
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public BrazeVdbCoord(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public int CompareTo(BrazeVdbCoord other)
        {
            int result = x.CompareTo(other.x);
            if (result != 0) return result;
            result = y.CompareTo(other.y);
            return result != 0 ? result : z.CompareTo(other.z);
        }

        public bool Equals(BrazeVdbCoord other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object value) => value is BrazeVdbCoord other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"[{x}, {y}, {z}]";
    }

    /// <summary>OpenVDB bit ordering: bit zero is the lowest slot; bytes and bits are both little-first.</summary>
    internal sealed class BrazeVdbMask : IEquatable<BrazeVdbMask>
    {
        readonly byte[] bytes;

        public int BitCount { get; }
        public int ByteCount => bytes.Length;

        public BrazeVdbMask(int bitCount)
        {
            if (bitCount <= 0 || (bitCount & 7) != 0)
                throw new ArgumentOutOfRangeException(nameof(bitCount), "VDB masks require a positive multiple of eight bits.");
            BitCount = bitCount;
            bytes = new byte[bitCount >> 3];
        }

        BrazeVdbMask(int bitCount, byte[] source)
        {
            BitCount = bitCount;
            bytes = source;
        }

        public bool IsOn(int index)
        {
            ValidateIndex(index);
            return (bytes[index >> 3] & (1 << (index & 7))) != 0;
        }

        public void Set(int index, bool on = true)
        {
            ValidateIndex(index);
            byte bit = (byte)(1 << (index & 7));
            if (on) bytes[index >> 3] |= bit;
            else bytes[index >> 3] &= (byte)~bit;
        }

        public int CountOn()
        {
            int count = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                int value = bytes[i];
                while (value != 0)
                {
                    value &= value - 1;
                    count++;
                }
            }
            return count;
        }

        public IEnumerable<int> OnIndices()
        {
            for (int i = 0; i < BitCount; i++)
                if (IsOn(i)) yield return i;
        }

        public byte[] ToArray() => (byte[])bytes.Clone();

        public void Write(BrazeVdbBinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteBytes(bytes);
        }

        public static BrazeVdbMask Read(BrazeVdbBinaryReader reader, int bitCount, string label)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (bitCount <= 0 || (bitCount & 7) != 0)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            return new BrazeVdbMask(bitCount, reader.ReadBytes(bitCount >> 3, label));
        }

        void ValidateIndex(int index)
        {
            if ((uint)index >= (uint)BitCount) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public bool Equals(BrazeVdbMask other)
        {
            if (other == null || BitCount != other.BitCount) return false;
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] != other.bytes[i]) return false;
            return true;
        }

        public override bool Equals(object value) => value is BrazeVdbMask other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(BitCount);
            for (int i = 0; i < bytes.Length; i++) hash.Add(bytes[i]);
            return hash.ToHashCode();
        }
    }

    internal static class BrazeVdbTreeLayout
    {
        public const int LeafDimension = 8;
        public const int LowerFanout = 16;
        public const int UpperFanout = 32;
        public const int LeafVoxelCount = LeafDimension * LeafDimension * LeafDimension;
        public const int LowerSlotCount = LowerFanout * LowerFanout * LowerFanout;
        public const int UpperSlotCount = UpperFanout * UpperFanout * UpperFanout;
        public const int LowerExtent = LeafDimension * LowerFanout;       // 128 voxels
        public const int UpperExtent = LowerExtent * UpperFanout;         // 4096 voxels

        public static int AlignDown(int coordinate, int powerOfTwo)
        {
            if (powerOfTwo <= 0 || (powerOfTwo & (powerOfTwo - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(powerOfTwo));
            return coordinate & -powerOfTwo;
        }

        public static BrazeVdbCoord RootOrigin(BrazeVdbCoord coordinate) => new(
            AlignDown(coordinate.x, UpperExtent), AlignDown(coordinate.y, UpperExtent),
            AlignDown(coordinate.z, UpperExtent));

        public static BrazeVdbCoord LowerOrigin(BrazeVdbCoord coordinate) => new(
            AlignDown(coordinate.x, LowerExtent), AlignDown(coordinate.y, LowerExtent),
            AlignDown(coordinate.z, LowerExtent));

        public static BrazeVdbCoord LeafOrigin(BrazeVdbCoord coordinate) => new(
            AlignDown(coordinate.x, LeafDimension), AlignDown(coordinate.y, LeafDimension),
            AlignDown(coordinate.z, LeafDimension));

        public static int UpperChildOffset(BrazeVdbCoord coordinate)
            => ((coordinate.x >> 7) & 31) * UpperFanout * UpperFanout +
               ((coordinate.y >> 7) & 31) * UpperFanout + ((coordinate.z >> 7) & 31);

        public static int LowerChildOffset(BrazeVdbCoord coordinate)
            => ((coordinate.x >> 3) & 15) * LowerFanout * LowerFanout +
               ((coordinate.y >> 3) & 15) * LowerFanout + ((coordinate.z >> 3) & 15);

        public static int LeafVoxelOffset(BrazeVdbCoord coordinate)
            => (coordinate.x & 7) * LeafDimension * LeafDimension +
               (coordinate.y & 7) * LeafDimension + (coordinate.z & 7);

        public static BrazeVdbCoord UpperChildOrigin(BrazeVdbCoord rootOrigin, int offset)
            => ChildOrigin(rootOrigin, offset, UpperFanout, LowerExtent, UpperSlotCount);

        public static BrazeVdbCoord LowerChildOrigin(BrazeVdbCoord lowerOrigin, int offset)
            => ChildOrigin(lowerOrigin, offset, LowerFanout, LeafDimension, LowerSlotCount);

        public static BrazeVdbCoord LeafCoordinate(BrazeVdbCoord leafOrigin, int offset)
            => ChildOrigin(leafOrigin, offset, LeafDimension, 1, LeafVoxelCount);

        static BrazeVdbCoord ChildOrigin(BrazeVdbCoord origin, int offset, int dimension, int scale, int count)
        {
            if ((uint)offset >= (uint)count) throw new ArgumentOutOfRangeException(nameof(offset));
            int plane = dimension * dimension;
            int x = offset / plane;
            int remainder = offset - x * plane;
            int y = remainder / dimension;
            int z = remainder - y * dimension;
            return new BrazeVdbCoord(checked(origin.x + x * scale), checked(origin.y + y * scale),
                checked(origin.z + z * scale));
        }
    }

    internal readonly struct BrazeVdbTile<T>
    {
        public readonly T value;
        public readonly bool active;

        public BrazeVdbTile(T value, bool active)
        {
            this.value = value;
            this.active = active;
        }
    }

    internal readonly struct BrazeVdbTreeRecord<T>
    {
        public readonly BrazeVdbCoord coordinate;
        public readonly T value;
        public readonly bool active;

        public BrazeVdbTreeRecord(BrazeVdbCoord coordinate, T value, bool active)
        {
            this.coordinate = coordinate;
            this.value = value;
            this.active = active;
        }
    }

    internal sealed class BrazeVdbLeaf<T>
    {
        readonly T[] values = new T[BrazeVdbTreeLayout.LeafVoxelCount];
        public BrazeVdbCoord Origin { get; }
        public BrazeVdbMask ActiveMask { get; } = new(BrazeVdbTreeLayout.LeafVoxelCount);

        public BrazeVdbLeaf(BrazeVdbCoord origin)
        {
            if (!BrazeVdbTreeLayout.LeafOrigin(origin).Equals(origin))
                throw new ArgumentException("Leaf origin is not aligned to eight voxels.", nameof(origin));
            Origin = origin;
        }

        public void Set(BrazeVdbCoord coordinate, T value, bool rejectDuplicate)
        {
            if (!BrazeVdbTreeLayout.LeafOrigin(coordinate).Equals(Origin))
                throw new ArgumentOutOfRangeException(nameof(coordinate));
            int offset = BrazeVdbTreeLayout.LeafVoxelOffset(coordinate);
            if (rejectDuplicate && ActiveMask.IsOn(offset))
                throw new InvalidDataException($"Duplicate active VDB voxel {coordinate}.");
            values[offset] = value;
            ActiveMask.Set(offset);
        }

        public bool TryGet(BrazeVdbCoord coordinate, out T value)
        {
            int offset = BrazeVdbTreeLayout.LeafVoxelOffset(coordinate);
            value = values[offset];
            return ActiveMask.IsOn(offset);
        }

        public T ValueAt(int offset)
        {
            if ((uint)offset >= BrazeVdbTreeLayout.LeafVoxelCount) throw new ArgumentOutOfRangeException(nameof(offset));
            return values[offset];
        }
    }

    internal sealed class BrazeVdbLowerNode<T>
    {
        readonly SortedDictionary<int, BrazeVdbLeaf<T>> children = new();
        readonly SortedDictionary<int, BrazeVdbTile<T>> tiles = new();

        public BrazeVdbCoord Origin { get; }
        public int ChildCount => children.Count;
        public IEnumerable<KeyValuePair<int, BrazeVdbLeaf<T>>> Children => children;
        public IEnumerable<KeyValuePair<int, BrazeVdbTile<T>>> Tiles => tiles;

        public BrazeVdbLowerNode(BrazeVdbCoord origin) => Origin = origin;

        public BrazeVdbLeaf<T> GetOrCreateLeaf(BrazeVdbCoord coordinate)
        {
            int offset = BrazeVdbTreeLayout.LowerChildOffset(coordinate);
            if (tiles.ContainsKey(offset))
                throw new InvalidDataException($"VDB leaf {BrazeVdbTreeLayout.LeafOrigin(coordinate)} overlaps a tile.");
            if (!children.TryGetValue(offset, out BrazeVdbLeaf<T> child))
            {
                child = new BrazeVdbLeaf<T>(BrazeVdbTreeLayout.LeafOrigin(coordinate));
                children.Add(offset, child);
            }
            return child;
        }

        public void AddTile(BrazeVdbCoord origin, T value, bool active)
        {
            int offset = BrazeVdbTreeLayout.LowerChildOffset(origin);
            if (children.ContainsKey(offset) || tiles.ContainsKey(offset))
                throw new InvalidDataException($"Duplicate or overlapping lower VDB slot at {origin}.");
            tiles.Add(offset, new BrazeVdbTile<T>(value, active));
        }

        public bool TryGetChild(int offset, out BrazeVdbLeaf<T> child) => children.TryGetValue(offset, out child);
        public bool TryGetTile(int offset, out BrazeVdbTile<T> tile) => tiles.TryGetValue(offset, out tile);
    }

    internal sealed class BrazeVdbUpperNode<T>
    {
        readonly SortedDictionary<int, BrazeVdbLowerNode<T>> children = new();
        readonly SortedDictionary<int, BrazeVdbTile<T>> tiles = new();

        public BrazeVdbCoord Origin { get; }
        public int ChildCount => children.Count;
        public IEnumerable<KeyValuePair<int, BrazeVdbLowerNode<T>>> Children => children;
        public IEnumerable<KeyValuePair<int, BrazeVdbTile<T>>> Tiles => tiles;

        public BrazeVdbUpperNode(BrazeVdbCoord origin) => Origin = origin;

        public BrazeVdbLowerNode<T> GetOrCreateLower(BrazeVdbCoord coordinate)
        {
            int offset = BrazeVdbTreeLayout.UpperChildOffset(coordinate);
            if (tiles.ContainsKey(offset))
                throw new InvalidDataException($"VDB lower node {BrazeVdbTreeLayout.LowerOrigin(coordinate)} overlaps a tile.");
            if (!children.TryGetValue(offset, out BrazeVdbLowerNode<T> child))
            {
                child = new BrazeVdbLowerNode<T>(BrazeVdbTreeLayout.LowerOrigin(coordinate));
                children.Add(offset, child);
            }
            return child;
        }

        public void AddTile(BrazeVdbCoord origin, T value, bool active)
        {
            int offset = BrazeVdbTreeLayout.UpperChildOffset(origin);
            if (children.ContainsKey(offset) || tiles.ContainsKey(offset))
                throw new InvalidDataException($"Duplicate or overlapping upper VDB slot at {origin}.");
            tiles.Add(offset, new BrazeVdbTile<T>(value, active));
        }

        public bool TryGetChild(int offset, out BrazeVdbLowerNode<T> child) => children.TryGetValue(offset, out child);
        public bool TryGetTile(int offset, out BrazeVdbTile<T> tile) => tiles.TryGetValue(offset, out tile);
    }

    /// <summary>Typed standard Tree4&lt;T,5,4,3&gt; model with deterministic node ordering and overlap rejection.</summary>
    internal sealed class BrazeVdbTree<T>
    {
        readonly SortedDictionary<BrazeVdbCoord, BrazeVdbUpperNode<T>> children = new();
        readonly SortedDictionary<BrazeVdbCoord, BrazeVdbTile<T>> tiles = new();

        public T Background { get; }
        public int ChildCount => children.Count;
        public IEnumerable<KeyValuePair<BrazeVdbCoord, BrazeVdbUpperNode<T>>> Children => children;
        public IEnumerable<KeyValuePair<BrazeVdbCoord, BrazeVdbTile<T>>> RootTiles => tiles;

        public BrazeVdbTree(T background) => Background = background;

        public void SetActive(BrazeVdbCoord coordinate, T value) => AddVoxel(coordinate, value, false);
        public void AddActive(BrazeVdbCoord coordinate, T value) => AddVoxel(coordinate, value, true);

        void AddVoxel(BrazeVdbCoord coordinate, T value, bool rejectDuplicate)
        {
            BrazeVdbUpperNode<T> upper = GetOrCreateUpper(coordinate);
            BrazeVdbLowerNode<T> lower = upper.GetOrCreateLower(coordinate);
            lower.GetOrCreateLeaf(coordinate).Set(coordinate, value, rejectDuplicate);
        }

        BrazeVdbUpperNode<T> GetOrCreateUpper(BrazeVdbCoord coordinate)
        {
            BrazeVdbCoord origin = BrazeVdbTreeLayout.RootOrigin(coordinate);
            if (tiles.ContainsKey(origin)) throw new InvalidDataException($"VDB node {origin} overlaps a root tile.");
            if (!children.TryGetValue(origin, out BrazeVdbUpperNode<T> child))
            {
                child = new BrazeVdbUpperNode<T>(origin);
                children.Add(origin, child);
            }
            return child;
        }

        public void AddRootTile(BrazeVdbCoord origin, T value, bool active)
        {
            if (!BrazeVdbTreeLayout.RootOrigin(origin).Equals(origin))
                throw new ArgumentException("Root tile origin is not aligned to 4096 voxels.", nameof(origin));
            if (children.ContainsKey(origin) || tiles.ContainsKey(origin))
                throw new InvalidDataException($"Duplicate or overlapping root VDB slot at {origin}.");
            tiles.Add(origin, new BrazeVdbTile<T>(value, active));
        }

        public void AddUpperTile(BrazeVdbCoord origin, T value, bool active)
        {
            if (!BrazeVdbTreeLayout.LowerOrigin(origin).Equals(origin))
                throw new ArgumentException("Upper tile origin is not aligned to 128 voxels.", nameof(origin));
            GetOrCreateUpper(origin).AddTile(origin, value, active);
        }

        public void AddLowerTile(BrazeVdbCoord origin, T value, bool active)
        {
            if (!BrazeVdbTreeLayout.LeafOrigin(origin).Equals(origin))
                throw new ArgumentException("Lower tile origin is not aligned to eight voxels.", nameof(origin));
            GetOrCreateUpper(origin).GetOrCreateLower(origin).AddTile(origin, value, active);
        }

        public bool TryGetValue(BrazeVdbCoord coordinate, out T value, out bool active)
        {
            BrazeVdbCoord rootOrigin = BrazeVdbTreeLayout.RootOrigin(coordinate);
            if (tiles.TryGetValue(rootOrigin, out BrazeVdbTile<T> rootTile))
            {
                value = rootTile.value;
                active = rootTile.active;
                return true;
            }
            if (!children.TryGetValue(rootOrigin, out BrazeVdbUpperNode<T> upper))
            {
                value = Background;
                active = false;
                return false;
            }
            int upperOffset = BrazeVdbTreeLayout.UpperChildOffset(coordinate);
            if (upper.TryGetTile(upperOffset, out BrazeVdbTile<T> upperTile))
            {
                value = upperTile.value;
                active = upperTile.active;
                return true;
            }
            if (!upper.TryGetChild(upperOffset, out BrazeVdbLowerNode<T> lower))
            {
                value = Background;
                active = false;
                return false;
            }
            int lowerOffset = BrazeVdbTreeLayout.LowerChildOffset(coordinate);
            if (lower.TryGetTile(lowerOffset, out BrazeVdbTile<T> lowerTile))
            {
                value = lowerTile.value;
                active = lowerTile.active;
                return true;
            }
            if (lower.TryGetChild(lowerOffset, out BrazeVdbLeaf<T> leaf) && leaf.TryGet(coordinate, out value))
            {
                active = true;
                return true;
            }
            value = Background;
            active = false;
            return false;
        }

        public IEnumerable<BrazeVdbTreeRecord<T>> EnumerateUpperTiles()
        {
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbUpperNode<T>> upperPair in children)
                foreach (KeyValuePair<int, BrazeVdbTile<T>> tilePair in upperPair.Value.Tiles)
                    yield return new BrazeVdbTreeRecord<T>(
                        BrazeVdbTreeLayout.UpperChildOrigin(upperPair.Key, tilePair.Key), tilePair.Value.value,
                        tilePair.Value.active);
        }

        public IEnumerable<BrazeVdbTreeRecord<T>> EnumerateLowerTiles()
        {
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbUpperNode<T>> upperPair in children)
                foreach (KeyValuePair<int, BrazeVdbLowerNode<T>> lowerPair in upperPair.Value.Children)
                    foreach (KeyValuePair<int, BrazeVdbTile<T>> tilePair in lowerPair.Value.Tiles)
                        yield return new BrazeVdbTreeRecord<T>(
                            BrazeVdbTreeLayout.LowerChildOrigin(lowerPair.Value.Origin, tilePair.Key),
                            tilePair.Value.value, tilePair.Value.active);
        }

        public IEnumerable<BrazeVdbTreeRecord<T>> EnumerateActiveVoxels()
        {
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbUpperNode<T>> upperPair in children)
                foreach (KeyValuePair<int, BrazeVdbLowerNode<T>> lowerPair in upperPair.Value.Children)
                    foreach (KeyValuePair<int, BrazeVdbLeaf<T>> leafPair in lowerPair.Value.Children)
                        foreach (int offset in leafPair.Value.ActiveMask.OnIndices())
                            yield return new BrazeVdbTreeRecord<T>(
                                BrazeVdbTreeLayout.LeafCoordinate(leafPair.Value.Origin, offset),
                                leafPair.Value.ValueAt(offset), true);
        }
    }

    /// <summary>Private phase-2 round-trip envelope for validating the tree model before complete .vdb serialization.</summary>
    internal static class BrazeVdbTreeCodec
    {
        const uint Magic = 0x31544256; // "VBT1" in little-endian bytes
        const byte FloatType = 1;
        const byte VectorType = 2;

        delegate void ValueWriter<T>(BrazeVdbBinaryWriter writer, T value);
        delegate T ValueReader<T>(BrazeVdbBinaryReader reader);

        public static void Write(BrazeVdbBinaryWriter writer, BrazeVdbTree<float> tree)
            => WriteTree(writer, tree, FloatType, (output, value) => output.WriteSingle(value));

        public static void Write(BrazeVdbBinaryWriter writer, BrazeVdbTree<Vector3> tree)
            => WriteTree(writer, tree, VectorType, (output, value) =>
            {
                output.WriteSingle(value.x);
                output.WriteSingle(value.y);
                output.WriteSingle(value.z);
            });

        public static BrazeVdbTree<float> ReadFloat(BrazeVdbBinaryReader reader)
            => ReadTree(reader, FloatType, input => input.ReadSingle("VDB tree float"));

        public static BrazeVdbTree<Vector3> ReadVector(BrazeVdbBinaryReader reader)
            => ReadTree(reader, VectorType, input => new Vector3(input.ReadSingle("VDB tree vector X"),
                input.ReadSingle("VDB tree vector Y"), input.ReadSingle("VDB tree vector Z")));

        static void WriteTree<T>(BrazeVdbBinaryWriter writer, BrazeVdbTree<T> tree, byte type,
            ValueWriter<T> writeValue)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            writer.WriteUInt32(Magic);
            writer.WriteByte(type);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writeValue(writer, tree.Background);
            WriteRecords(writer, tree.RootTiles, writeValue);
            WriteRecords(writer, tree.EnumerateUpperTiles(), writeValue);
            WriteRecords(writer, tree.EnumerateLowerTiles(), writeValue);
            WriteRecords(writer, tree.EnumerateActiveVoxels(), writeValue);
        }

        static void WriteRecords<T>(BrazeVdbBinaryWriter writer,
            IEnumerable<KeyValuePair<BrazeVdbCoord, BrazeVdbTile<T>>> source, ValueWriter<T> writeValue)
        {
            var records = new List<BrazeVdbTreeRecord<T>>();
            foreach (KeyValuePair<BrazeVdbCoord, BrazeVdbTile<T>> pair in source)
                records.Add(new BrazeVdbTreeRecord<T>(pair.Key, pair.Value.value, pair.Value.active));
            WriteRecords(writer, records, writeValue);
        }

        static void WriteRecords<T>(BrazeVdbBinaryWriter writer, IEnumerable<BrazeVdbTreeRecord<T>> source,
            ValueWriter<T> writeValue)
        {
            var records = new List<BrazeVdbTreeRecord<T>>(source);
            writer.WriteInt32(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                BrazeVdbTreeRecord<T> record = records[i];
                WriteCoord(writer, record.coordinate);
                writer.WriteBoolean(record.active);
                writeValue(writer, record.value);
            }
        }

        static BrazeVdbTree<T> ReadTree<T>(BrazeVdbBinaryReader reader, byte expectedType,
            ValueReader<T> readValue)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.ReadUInt32("VDB tree envelope magic") != Magic)
                throw new InvalidDataException("The VDB tree envelope magic is invalid.");
            if (reader.ReadByte("VDB tree value type") != expectedType)
                throw new InvalidDataException("The VDB tree envelope value type is incompatible.");
            if (reader.ReadByte("VDB tree reserved byte") != 0 || reader.ReadUInt16("VDB tree reserved word") != 0)
                throw new InvalidDataException("The VDB tree envelope has nonzero reserved fields.");
            var tree = new BrazeVdbTree<T>(readValue(reader));
            int budget = 0;
            ReadRecords(reader, readValue, "root tile", ref budget,
                record => tree.AddRootTile(record.coordinate, record.value, record.active));
            ReadRecords(reader, readValue, "upper tile", ref budget,
                record => tree.AddUpperTile(record.coordinate, record.value, record.active));
            ReadRecords(reader, readValue, "lower tile", ref budget,
                record => tree.AddLowerTile(record.coordinate, record.value, record.active));
            ReadRecords(reader, readValue, "active voxel", ref budget,
                record =>
                {
                    if (!record.active) throw new InvalidDataException("An active VDB voxel record is marked inactive.");
                    tree.AddActive(record.coordinate, record.value);
                }, reader.Limits.MaxActiveVoxelCount);
            if (reader.Remaining != 0)
                throw new InvalidDataException($"The VDB tree envelope has {reader.Remaining} unexpected trailing bytes.");
            return tree;
        }

        static void ReadRecords<T>(BrazeVdbBinaryReader reader, ValueReader<T> readValue, string label,
            ref int budget, Action<BrazeVdbTreeRecord<T>> add, int maximum = -1)
        {
            if (maximum < 0) maximum = reader.Limits.MaxNodeCount;
            int count = reader.ReadCount32(maximum, $"VDB {label} count");
            if (count > reader.Limits.MaxNodeCount - budget)
                throw new InvalidDataException("The VDB tree exceeds the combined topology record ceiling.");
            budget += count;
            for (int i = 0; i < count; i++)
            {
                BrazeVdbCoord coordinate = ReadCoord(reader, $"VDB {label} {i}");
                bool active = reader.ReadBoolean($"VDB {label} {i} state");
                add(new BrazeVdbTreeRecord<T>(coordinate, readValue(reader), active));
            }
        }

        static void WriteCoord(BrazeVdbBinaryWriter writer, BrazeVdbCoord coordinate)
        {
            writer.WriteInt32(coordinate.x);
            writer.WriteInt32(coordinate.y);
            writer.WriteInt32(coordinate.z);
        }

        static BrazeVdbCoord ReadCoord(BrazeVdbBinaryReader reader, string label) => new(
            reader.ReadInt32(label + " X"), reader.ReadInt32(label + " Y"), reader.ReadInt32(label + " Z"));
    }
}
