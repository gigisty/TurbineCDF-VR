using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace BrazeFX.EditorTools.Vdb
{
    internal enum BrazeVdbWriteCompression
    {
        None = 0,
        ZipActiveMask = 1,
    }

    /// <summary>Reusable bounded per-node zlib workspace for standard OpenVDB ZIP value buffers.</summary>
    internal sealed class BrazeVdbZipBuffer : IDisposable
    {
        const uint AdlerModulus = 65521;
        const int WorkspaceBytes = 16 * 1024;
        readonly byte[] rawBytes;
        readonly byte[] compressedBytes;
        readonly MemoryStream raw;
        readonly MemoryStream compressed;
        bool disposed;

        public BrazeVdbZipBuffer()
        {
            rawBytes = ArrayPool<byte>.Shared.Rent(WorkspaceBytes);
            compressedBytes = ArrayPool<byte>.Shared.Rent(WorkspaceBytes);
            raw = new MemoryStream(rawBytes, 0, rawBytes.Length, true, true);
            compressed = new MemoryStream(compressedBytes, 0, compressedBytes.Length, true, true);
            raw.SetLength(0);
            compressed.SetLength(0);
        }

        public void WriteFramed(BrazeVdbBinaryWriter output, Action<BrazeVdbBinaryWriter> writeRaw)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (writeRaw == null) throw new ArgumentNullException(nameof(writeRaw));
            raw.Position = 0;
            raw.SetLength(0);
            using (var rawWriter = new BrazeVdbBinaryWriter(raw, true)) writeRaw(rawWriter);
            int rawLength = checked((int)raw.Length);
            if (rawLength == 0)
            {
                output.WriteInt64(0);
                return;
            }

            compressed.Position = 0;
            compressed.SetLength(0);
            compressed.WriteByte(0x78); // Deflate, 32 KiB window.
            compressed.WriteByte(0x9C); // Default compression, FCHECK satisfied.
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
                deflate.Write(rawBytes, 0, rawLength);
            WriteAdler32(compressed, rawBytes, rawLength);

            int compressedLength = checked((int)compressed.Length);
            if (compressedLength < rawLength)
            {
                output.WriteInt64(compressedLength);
                output.WriteBytes(compressedBytes, 0, compressedLength);
            }
            else
            {
                output.WriteInt64(-rawLength);
                output.WriteBytes(rawBytes, 0, rawLength);
            }
        }

        static void WriteAdler32(Stream stream, byte[] bytes, int count)
        {
            uint a = 1;
            uint b = 0;
            for (int offset = 0; offset < count;)
            {
                int end = Math.Min(offset + 5552, count);
                while (offset < end)
                {
                    a += bytes[offset++];
                    b += a;
                }
                a %= AdlerModulus;
                b %= AdlerModulus;
            }
            uint checksum = b << 16 | a;
            stream.WriteByte((byte)(checksum >> 24));
            stream.WriteByte((byte)(checksum >> 16));
            stream.WriteByte((byte)(checksum >> 8));
            stream.WriteByte((byte)checksum);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            raw.Dispose();
            compressed.Dispose();
            ArrayPool<byte>.Shared.Return(rawBytes, true);
            ArrayPool<byte>.Shared.Return(compressedBytes, true);
        }

        public static void ReadFramed(BrazeVdbBinaryReader input, int expectedBytes,
            Action<BrazeVdbBinaryReader> readRaw)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (expectedBytes < 0 || expectedBytes > input.Limits.MaxAllocationBytes)
                throw new InvalidDataException("The OpenVDB ZIP value buffer exceeds the allocation ceiling.");
            if (readRaw == null) throw new ArgumentNullException(nameof(readRaw));
            long marker = input.ReadInt64("OpenVDB ZIP byte-count marker");
            if (expectedBytes == 0)
            {
                if (marker != 0)
                    throw new InvalidDataException("An empty OpenVDB ZIP value buffer must use a zero marker.");
                using var empty = new BrazeVdbBinaryReader(new MemoryStream(Array.Empty<byte>(), false),
                    input.Limits);
                readRaw(empty);
                if (empty.Remaining != 0)
                    throw new InvalidDataException("The OpenVDB ZIP value decoder left trailing bytes.");
                return;
            }
            if (marker == 0 || marker == long.MinValue)
                throw new InvalidDataException("The OpenVDB ZIP value buffer has an invalid byte-count marker.");

            byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(expectedBytes);
            byte[] compressedBuffer = null;
            try
            {
                if (marker < 0)
                {
                    long rawLength = -marker;
                    if (rawLength != expectedBytes)
                        throw new InvalidDataException(
                            $"OpenVDB raw ZIP fallback declares {rawLength} bytes; {expectedBytes} are required.");
                    input.ReadBytes(rawBuffer, 0, expectedBytes, "OpenVDB raw ZIP fallback");
                }
                else
                {
                    if (marker > int.MaxValue || marker > input.Limits.MaxAllocationBytes)
                        throw new InvalidDataException("The OpenVDB ZIP payload exceeds the allocation ceiling.");
                    int compressedLength = (int)marker;
                    if (compressedLength < 6)
                        throw new InvalidDataException("The OpenVDB zlib payload is too short.");
                    compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedLength);
                    input.ReadBytes(compressedBuffer, 0, compressedLength, "OpenVDB zlib payload");
                    InflateZlib(compressedBuffer, compressedLength, rawBuffer, expectedBytes);
                }

                using var rawStream = new MemoryStream(rawBuffer, 0, expectedBytes, false, true);
                using var rawReader = new BrazeVdbBinaryReader(rawStream, input.Limits);
                readRaw(rawReader);
                if (rawReader.Remaining != 0)
                    throw new InvalidDataException(
                        $"The OpenVDB ZIP value decoder left {rawReader.Remaining} trailing bytes.");
            }
            finally
            {
                if (compressedBuffer != null) ArrayPool<byte>.Shared.Return(compressedBuffer);
                ArrayPool<byte>.Shared.Return(rawBuffer);
            }
        }

        static void InflateZlib(byte[] compressedBytes, int compressedLength, byte[] rawBytes, int rawLength)
        {
            int cmf = compressedBytes[0];
            int flg = compressedBytes[1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flg) % 31 != 0 || (flg & 0x20) != 0)
                throw new InvalidDataException("The OpenVDB ZIP buffer has an unsupported zlib header.");
            int deflateLength = compressedLength - 6;
            try
            {
                using var payload = new MemoryStream(compressedBytes, 2, deflateLength, false, true);
                using var deflate = new DeflateStream(payload, CompressionMode.Decompress, true);
                int offset = 0;
                while (offset < rawLength)
                {
                    int read = deflate.Read(rawBytes, offset, rawLength - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != rawLength || deflate.ReadByte() != -1)
                    throw new InvalidDataException(
                        $"The OpenVDB ZIP buffer expands to a size other than {rawLength} bytes.");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("The OpenVDB ZIP buffer contains invalid DEFLATE data.", exception);
            }

            uint expected = (uint)compressedBytes[compressedLength - 4] << 24 |
                            (uint)compressedBytes[compressedLength - 3] << 16 |
                            (uint)compressedBytes[compressedLength - 2] << 8 |
                            compressedBytes[compressedLength - 1];
            uint actual = Adler32(rawBytes, rawLength);
            if (actual != expected)
                throw new InvalidDataException("The OpenVDB ZIP buffer has an invalid Adler-32 checksum.");
        }

        static uint Adler32(byte[] bytes, int count)
        {
            uint a = 1;
            uint b = 0;
            for (int offset = 0; offset < count;)
            {
                int end = Math.Min(offset + 5552, count);
                while (offset < end)
                {
                    a += bytes[offset++];
                    b += a;
                }
                a %= AdlerModulus;
                b %= AdlerModulus;
            }
            return b << 16 | a;
        }
    }
}
