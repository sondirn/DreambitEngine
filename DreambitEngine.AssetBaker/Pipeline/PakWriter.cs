using System.Buffers.Binary;
using Dreambit;
using DreambitEngine.AssetBaker.Abstractions;
using ZstdSharp;

namespace DreambitEngine.AssetBaker.Pipeline;

public sealed class PakWriter
{
    private const ushort Version = 2;

    // Level 3 is a sensible default for build-time asset compression:
    // good compression without making every bake painfully slow.
    private const int ZstdCompressionLevel = 3;

    // Tiny payloads are not worth running through a compressor.
    private const int MinimumCompressionSize = 256;

    // Avoid paying runtime decompression cost to save a handful of bytes.
    private const int MinimumCompressionSavings = 64;

    private sealed record Entry(
        string Path,
        AssetType Type,
        byte[] Data,
        int StoredSize,
        long UncompressedSize,
        PakCompression Compression,
        uint Crc);

    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public void Add(AssetBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var path = Normalize(blob.LogicalPath);
        if (path.Length == 0)
            throw new InvalidOperationException(
                "A PAK entry path cannot be empty.");

        var entry = CreateEntry(
            path,
            blob.Type,
            blob.Data);

        if (!_entries.TryAdd(path, entry))
        {
            throw new InvalidOperationException(
                $"Two source assets produce the same case-insensitive PAK path '{path}'.");
        }
    }

    public void AddOrReplace(AssetBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var path = Normalize(blob.LogicalPath);
        if (path.Length == 0)
            throw new InvalidOperationException(
                "A PAK entry path cannot be empty.");

        _entries[path] = CreateEntry(
            path,
            blob.Type,
            blob.Data);
    }

    public void Save(string outputPak)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPak);

        if (_entries.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"PAK contains {_entries.Count} entries; " +
                $"the format supports at most {ushort.MaxValue}.");
        }

        var outputPath = Path.GetFullPath(outputPak);
        var directory = Path.GetDirectoryName(outputPath)!;

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                WritePak(stream);
                stream.Flush(true);
            }

            // Keep existing hot-reload semantics. Existing readers continue
            // using their old file handle while new readers see the new PAK.
            if (File.Exists(outputPath))
                File.Replace(temporaryPath, outputPath, null, true);
            else
                File.Move(temporaryPath, outputPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void WritePak(Stream stream)
    {
        var entries = _entries.Values
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        // Header remains the same size as PAK v1.
        stream.Write("PAK0"u8);

        Span<byte> buffer = stackalloc byte[16];

        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer[..2],
            Version);
        stream.Write(buffer[..2]);

        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer[..2],
            (ushort)entries.Length);
        stream.Write(buffer[..2]);

        var tocOffsetPosition = stream.Position;
        stream.Position += 8;

        var dataOffsetPosition = stream.Position;
        stream.Position += 8;

        var tocOffset = stream.Position;

        /*
         * PAK v2 TOC entry:
         *
         * ushort pathLength
         * byte[] path
         * ushort assetType
         * ushort compression
         * ulong  dataOffset
         * ulong  storedSize
         * ulong  uncompressedSize
         * uint   crc32
         */

        long tocSize = 0;

        foreach (var entry in entries)
        {
            var pathLength =
                System.Text.Encoding.UTF8.GetByteCount(entry.Path);

            if (pathLength > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"PAK path '{entry.Path}' is too long.");
            }

            tocSize +=
                2 +                 // path length
                pathLength +
                2 +                 // asset type
                2 +                 // compression
                8 +                 // offset
                8 +                 // stored size
                8 +                 // uncompressed size
                4;                  // CRC32
        }

        var dataOffset = tocOffset + tocSize;
        var currentDataOffset = dataOffset;

        foreach (var entry in entries)
        {
            var pathBytes =
                System.Text.Encoding.UTF8.GetBytes(entry.Path);

            BinaryPrimitives.WriteUInt16LittleEndian(
                buffer[..2],
                (ushort)pathBytes.Length);
            stream.Write(buffer[..2]);

            stream.Write(pathBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(
                buffer[..2],
                (ushort)entry.Type);
            stream.Write(buffer[..2]);

            BinaryPrimitives.WriteUInt16LittleEndian(
                buffer[..2],
                (ushort)entry.Compression);
            stream.Write(buffer[..2]);

            BinaryPrimitives.WriteUInt64LittleEndian(
                buffer[..8],
                (ulong)currentDataOffset);
            stream.Write(buffer[..8]);

            BinaryPrimitives.WriteUInt64LittleEndian(
                buffer[..8],
                (ulong)entry.StoredSize);
            stream.Write(buffer[..8]);

            BinaryPrimitives.WriteUInt64LittleEndian(
                buffer[..8],
                (ulong)entry.UncompressedSize);
            stream.Write(buffer[..8]);

            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer[..4],
                entry.Crc);
            stream.Write(buffer[..4]);

            currentDataOffset += entry.StoredSize;
        }

        stream.Position = tocOffsetPosition;

        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer[..8],
            (ulong)tocOffset);
        stream.Write(buffer[..8]);

        stream.Position = dataOffsetPosition;

        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer[..8],
            (ulong)dataOffset);
        stream.Write(buffer[..8]);

        stream.Position = dataOffset;

        foreach (var entry in entries)
        {
            stream.Write(
                entry.Data.AsSpan(
                    0,
                    entry.StoredSize));
        }
    }

    private static Entry CreateEntry(
        string path,
        AssetType type,
        byte[] data)
    {
        var crc = Crc32(data);

        if (data.Length < MinimumCompressionSize)
        {
            return CreateUncompressedEntry(
                path,
                type,
                data,
                crc);
        }

        var compressed = CompressZstd(data);

        // Don't compress an asset unless it produces a meaningful saving.
        if (compressed.Length + MinimumCompressionSavings >= data.Length)
        {
            return CreateUncompressedEntry(
                path,
                type,
                data,
                crc);
        }

        return new Entry(
            path,
            type,
            compressed,
            compressed.Length,
            data.LongLength,
            PakCompression.Zstd,
            crc);
    }

    private static Entry CreateUncompressedEntry(
        string path,
        AssetType type,
        byte[] data,
        uint crc)
    {
        return new Entry(
            path,
            type,
            data,
            data.Length,
            data.LongLength,
            PakCompression.None,
            crc);
    }

    private static byte[] CompressZstd(
        ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();

        // CompressionStream uses bounded internal buffers instead of
        // allocating ZSTD_compressBound(sourceSize) up front. This matters
        // for extremely compressible TEXB data such as large pixel-art sheets.
        using (var compressor = new CompressionStream(
                   output,
                   ZstdCompressionLevel,
                   leaveOpen: true))
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/')
            .Trim()
            .TrimStart('.', '/')
            .ToLowerInvariant();

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0xEDB88320u;

        Span<uint> table = stackalloc uint[256];

        for (uint index = 0; index < 256; index++)
        {
            var value = index;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? polynomial ^ (value >> 1)
                    : value >> 1;
            }

            table[(int)index] = value;
        }

        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc =
                table[(int)((crc ^ value) & 0xFF)] ^
                (crc >> 8);
        }

        return ~crc;
    }
}
