using System;
using System.IO;
using System.Text;

namespace BrazeFX.EditorTools.Vdb
{
    /// <summary>Allocation and topology ceilings for the bounded Editor-only OpenVDB reader.</summary>
    internal sealed class BrazeVdbSafetyLimits
    {
        public static readonly BrazeVdbSafetyLimits Default = new();

        public int MaxStringBytes { get; }
        public int MaxMetadataEntries { get; }
        public int MaxGridCount { get; }
        public int MaxNodeCount { get; }
        public int MaxActiveVoxelCount { get; }
        public long MaxAllocationBytes { get; }

        public BrazeVdbSafetyLimits(int maxStringBytes = 1024 * 1024, int maxMetadataEntries = 4096,
            int maxGridCount = 64, int maxNodeCount = 8 * 1024 * 1024,
            int maxActiveVoxelCount = 512 * 1024 * 1024, long maxAllocationBytes = 1024L * 1024 * 1024)
        {
            if (maxStringBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxStringBytes));
            if (maxMetadataEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxMetadataEntries));
            if (maxGridCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxGridCount));
            if (maxNodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodeCount));
            if (maxActiveVoxelCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxActiveVoxelCount));
            if (maxAllocationBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxAllocationBytes));
            MaxStringBytes = maxStringBytes;
            MaxMetadataEntries = maxMetadataEntries;
            MaxGridCount = maxGridCount;
            MaxNodeCount = maxNodeCount;
            MaxActiveVoxelCount = maxActiveVoxelCount;
            MaxAllocationBytes = maxAllocationBytes;
        }
    }

    /// <summary>Explicit little-endian primitives. No platform-endian BitConverter byte arrays or native interop.</summary>
    internal sealed class BrazeVdbBinaryWriter : IDisposable
    {
        static readonly UTF8Encoding Utf8 = new(false, true);
        readonly Stream stream;
        readonly bool leaveOpen;

        public Stream BaseStream => stream;
        public long Position => stream.Position;

        public BrazeVdbBinaryWriter(Stream stream, bool leaveOpen = false)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite) throw new ArgumentException("The VDB output stream is not writable.", nameof(stream));
            this.leaveOpen = leaveOpen;
        }

        public void WriteByte(byte value) => stream.WriteByte(value);

        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            WriteByte((byte)value);
            WriteByte((byte)(value >> 8));
        }

        public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

        public void WriteUInt32(uint value)
        {
            WriteByte((byte)value);
            WriteByte((byte)(value >> 8));
            WriteByte((byte)(value >> 16));
            WriteByte((byte)(value >> 24));
        }

        public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

        public void WriteUInt64(ulong value)
        {
            WriteUInt32((uint)value);
            WriteUInt32((uint)(value >> 32));
        }

        public void WriteSingle(float value) => WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

        public void WriteDouble(double value) => WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

        public void WriteBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset), "The VDB byte range is outside the buffer.");
            stream.Write(bytes, offset, count);
        }

        public byte[] EncodeUtf8(string value, int maxBytes, string label)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            byte[] bytes;
            try { bytes = Utf8.GetBytes(value); }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException($"{label} is not valid UTF-8 text.", exception);
            }
            if (bytes.Length > maxBytes)
                throw new InvalidDataException($"{label} is {bytes.Length} bytes; the limit is {maxBytes} bytes.");
            return bytes;
        }

        public void WriteUtf8(string value, int maxBytes, string label)
            => WriteBytes(EncodeUtf8(value, maxBytes, label));

        public void WriteString(string value, int maxBytes, string label = "VDB string")
        {
            byte[] bytes = EncodeUtf8(value, maxBytes, label);
            WriteUInt32((uint)bytes.Length);
            WriteBytes(bytes);
        }

        public long ReserveInt64()
        {
            long position = Position;
            WriteInt64(0);
            return position;
        }

        public void PatchInt64(long position, long value)
        {
            if (!stream.CanSeek) throw new NotSupportedException("The VDB output stream cannot patch offsets.");
            if (position < 0 || position > stream.Length - sizeof(long))
                throw new ArgumentOutOfRangeException(nameof(position), "The VDB offset patch is outside the stream.");
            long restore = Position;
            stream.Position = position;
            WriteInt64(value);
            stream.Position = restore;
        }

        public void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }
    }

    /// <summary>Bounded little-endian reader with checked offsets, sizes, stream length, and strict UTF-8.</summary>
    internal sealed class BrazeVdbBinaryReader : IDisposable
    {
        static readonly UTF8Encoding Utf8 = new(false, true);
        readonly Stream stream;
        readonly bool leaveOpen;

        public BrazeVdbSafetyLimits Limits { get; }
        public Stream BaseStream => stream;
        public long Position => stream.Position;
        public long Length => stream.Length;
        public long Remaining => Length - Position;

        public BrazeVdbBinaryReader(Stream stream, BrazeVdbSafetyLimits limits = null, bool leaveOpen = false)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The bounded VDB input stream must be readable and seekable.", nameof(stream));
            Limits = limits ?? BrazeVdbSafetyLimits.Default;
            this.leaveOpen = leaveOpen;
        }

        public void RequireAvailable(long byteCount, string label)
        {
            if (byteCount < 0) throw new InvalidDataException($"{label} has a negative byte count.");
            if (Position < 0 || Position > Length || byteCount > Length - Position)
                throw new EndOfStreamException($"{label} is truncated at byte {Position}; {byteCount} bytes are required.");
        }

        public byte ReadByte(string label = "VDB byte")
        {
            RequireAvailable(1, label);
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException($"{label} is truncated at byte {Position}.");
            return (byte)value;
        }

        public bool ReadBoolean(string label = "VDB boolean")
        {
            byte value = ReadByte(label);
            if (value > 1) throw new InvalidDataException($"{label} must be encoded as zero or one.");
            return value != 0;
        }

        public ushort ReadUInt16(string label = "VDB UInt16")
        {
            RequireAvailable(sizeof(ushort), label);
            return (ushort)(ReadByte(label) | ReadByte(label) << 8);
        }

        public int ReadInt32(string label = "VDB Int32") => unchecked((int)ReadUInt32(label));

        public uint ReadUInt32(string label = "VDB UInt32")
        {
            RequireAvailable(sizeof(uint), label);
            return (uint)(ReadByte(label) | ReadByte(label) << 8 | ReadByte(label) << 16 | ReadByte(label) << 24);
        }

        public long ReadInt64(string label = "VDB Int64") => unchecked((long)ReadUInt64(label));

        public ulong ReadUInt64(string label = "VDB UInt64")
        {
            ulong low = ReadUInt32(label);
            ulong high = ReadUInt32(label);
            return low | high << 32;
        }

        public float ReadSingle(string label = "VDB float")
            => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32(label)));

        public double ReadDouble(string label = "VDB double")
            => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(label)));

        public byte[] ReadBytes(int byteCount, string label)
        {
            if (byteCount < 0) throw new InvalidDataException($"{label} has a negative byte count.");
            if (byteCount > Limits.MaxAllocationBytes)
                throw new InvalidDataException($"{label} exceeds the {Limits.MaxAllocationBytes}-byte allocation ceiling.");
            RequireAvailable(byteCount, label);
            var result = new byte[byteCount];
            int offset = 0;
            while (offset < result.Length)
            {
                int read = stream.Read(result, offset, result.Length - offset);
                if (read <= 0) throw new EndOfStreamException($"{label} is truncated at byte {Position}.");
                offset += read;
            }
            return result;
        }

        public void ReadBytes(byte[] destination, int offset, int byteCount, string label)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (offset < 0 || byteCount < 0 || offset > destination.Length - byteCount)
                throw new ArgumentOutOfRangeException(nameof(offset), "The VDB byte range is outside the buffer.");
            RequireAvailable(byteCount, label);
            int end = offset + byteCount;
            while (offset < end)
            {
                int read = stream.Read(destination, offset, end - offset);
                if (read <= 0) throw new EndOfStreamException($"{label} is truncated at byte {Position}.");
                offset += read;
            }
        }

        public string ReadUtf8(int byteCount, string label)
        {
            if (byteCount > Limits.MaxStringBytes)
                throw new InvalidDataException($"{label} is {byteCount} bytes; the limit is {Limits.MaxStringBytes} bytes.");
            try { return Utf8.GetString(ReadBytes(byteCount, label)); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{label} is not valid UTF-8 text.", exception);
            }
        }

        public string ReadString(string label = "VDB string")
        {
            uint byteCount = ReadUInt32($"{label} length");
            if (byteCount > int.MaxValue || byteCount > Limits.MaxStringBytes)
                throw new InvalidDataException($"{label} length {byteCount} exceeds the bounded string limit.");
            return ReadUtf8((int)byteCount, label);
        }

        public int ReadCount32(int maximum, string label)
        {
            int count = ReadInt32(label);
            if (count < 0 || count > maximum)
                throw new InvalidDataException($"{label} {count} is outside the supported range 0..{maximum}.");
            return count;
        }

        public long ValidateOffset(long offset, string label, long minimum = 0)
        {
            if (offset < minimum || offset > Length)
                throw new InvalidDataException($"{label} {offset} is outside the stream range {minimum}..{Length}.");
            return offset;
        }

        public void SeekAbsolute(long offset, string label, long minimum = 0)
            => stream.Position = ValidateOffset(offset, label, minimum);

        public void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }
    }
}
