#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dreambit.Tiled;

[Flags]
public enum TmxTileFlipFlags
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Diagonal = 4,
    Hexagonal120 = 8
}

public readonly record struct TmxTileCell(
    int X,
    int Y,
    uint EncodedGlobalTileId,
    int? ChunkX = null,
    int? ChunkY = null)
{
    public uint GlobalTileId => TmxTileDataDecoder.ClearTransformFlags(EncodedGlobalTileId);
    public TmxTileFlipFlags FlipFlags => TmxTileDataDecoder.GetFlipFlags(EncodedGlobalTileId);
}

public static class TmxTileDataDecoder
{
    public const uint HorizontalFlipFlag = 0x80000000;
    public const uint VerticalFlipFlag = 0x40000000;
    public const uint DiagonalFlipFlag = 0x20000000;
    public const uint Hexagonal120Flag = 0x10000000;
    public const uint TransformFlagsMask =
        HorizontalFlipFlag | VerticalFlipFlag | DiagonalFlipFlag | Hexagonal120Flag;

    public static IReadOnlyList<TmxTileCell> DecodeLayer(TmxMap map, TmxTileLayer layer)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Data is null)
            return Array.Empty<TmxTileCell>();

        var result = new List<TmxTileCell>();
        if (layer.Data.Chunks.Count > 0)
        {
            foreach (var chunk in layer.Data.Chunks)
            {
                if (chunk.Width <= 0 || chunk.Height <= 0)
                    throw new TiledException(
                        $"Tiled layer '{layer.Name}' contains a chunk with invalid dimensions {chunk.Width}x{chunk.Height}.");

                var count = checked(chunk.Width * chunk.Height);
                var gids = DecodeValues(
                    layer.Data.Encoding,
                    layer.Data.Compression,
                    chunk.Value,
                    chunk.Tiles,
                    count,
                    $"chunk ({chunk.X}, {chunk.Y}) in layer '{layer.Name}'");
                for (var index = 0; index < gids.Length; index++)
                {
                    var localX = index % chunk.Width;
                    var localY = index / chunk.Width;
                    result.Add(new TmxTileCell(
                        checked(layer.X + chunk.X + localX),
                        checked(layer.Y + chunk.Y + localY),
                        gids[index],
                        chunk.X,
                        chunk.Y));
                }
            }

            return result;
        }

        // Tiled writes an empty infinite-map layer as a self-closing data element
        // (for example, <data encoding="csv"/>), while retaining the editor's
        // nominal layer width and height. It represents no cells, not a truncated
        // fixed-size tile array.
        if (map.Infinite &&
            string.IsNullOrWhiteSpace(layer.Data.Value) &&
            layer.Data.Tiles.Count == 0)
        {
            return result;
        }

        var width = layer.Width > 0 ? layer.Width : map.Width;
        var height = layer.Height > 0 ? layer.Height : map.Height;
        if (width <= 0 || height <= 0)
            return Array.Empty<TmxTileCell>();

        var expectedCount = checked(width * height);
        var layerGids = DecodeValues(
            layer.Data.Encoding,
            layer.Data.Compression,
            layer.Data.Value,
            layer.Data.Tiles,
            expectedCount,
            $"layer '{layer.Name}'");
        for (var index = 0; index < layerGids.Length; index++)
        {
            result.Add(new TmxTileCell(
                checked(layer.X + index % width),
                checked(layer.Y + index / width),
                layerGids[index]));
        }

        return result;
    }

    public static uint ClearTransformFlags(uint encodedGlobalTileId)
        => encodedGlobalTileId & ~TransformFlagsMask;

    public static TmxTileFlipFlags GetFlipFlags(uint encodedGlobalTileId)
    {
        var result = TmxTileFlipFlags.None;
        if ((encodedGlobalTileId & HorizontalFlipFlag) != 0)
            result |= TmxTileFlipFlags.Horizontal;
        if ((encodedGlobalTileId & VerticalFlipFlag) != 0)
            result |= TmxTileFlipFlags.Vertical;
        if ((encodedGlobalTileId & DiagonalFlipFlag) != 0)
            result |= TmxTileFlipFlags.Diagonal;
        if ((encodedGlobalTileId & Hexagonal120Flag) != 0)
            result |= TmxTileFlipFlags.Hexagonal120;
        return result;
    }

    private static uint[] DecodeValues(
        string? encoding,
        string? compression,
        string? text,
        IReadOnlyList<TmxLayerTile> xmlTiles,
        int expectedCount,
        string description)
    {
        uint[] values;
        switch (encoding?.Trim().ToLowerInvariant())
        {
            case null or "":
                if (!string.IsNullOrWhiteSpace(compression))
                    throw new TiledException(
                        $"Tiled {description} declares compression without Base64 encoding.");
                values = new uint[xmlTiles.Count];
                for (var index = 0; index < xmlTiles.Count; index++)
                    values[index] = xmlTiles[index].Gid;
                break;
            case "csv":
                if (!string.IsNullOrWhiteSpace(compression))
                    throw new TiledException(
                        $"Tiled {description} declares compression for CSV data.");
                values = DecodeCsv(text, description);
                break;
            case "base64":
                values = DecodeBase64(text, compression, description);
                break;
            default:
                throw new TiledException(
                    $"Tiled {description} uses unsupported encoding '{encoding}'.");
        }

        if (values.Length != expectedCount)
        {
            throw new TiledException(
                $"Tiled {description} contains {values.Length} tile values but expected {expectedCount}.");
        }

        return values;
    }

    private static uint[] DecodeCsv(string? text, string description)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<uint>();

        var parts = text.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new uint[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!uint.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                throw new TiledException(
                    $"Tiled {description} contains invalid CSV tile value '{parts[index]}'.");
        }
        return values;
    }

    private static uint[] DecodeBase64(
        string? text,
        string? compression,
        string description)
    {
        var normalized = RemoveWhitespace(text);
        byte[] encoded;
        try
        {
            encoded = Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new TiledException($"Tiled {description} contains invalid Base64 data.", exception);
        }

        var decoded = Decompress(encoded, compression, description);
        if (decoded.Length % sizeof(uint) != 0)
            throw new TiledException(
                $"Tiled {description} decoded to {decoded.Length} bytes, which is not a sequence of 32-bit GIDs.");

        var values = new uint[decoded.Length / sizeof(uint)];
        for (var index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(index * sizeof(uint), sizeof(uint)));
        return values;
    }

    private static byte[] Decompress(
        byte[] encoded,
        string? compression,
        string description)
    {
        if (string.IsNullOrWhiteSpace(compression))
            return encoded;

        using var input = new MemoryStream(encoded, writable: false);
        using Stream decompressor = compression.Trim().ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "zlib" => new ZLibStream(input, CompressionMode.Decompress),
            "zstd" => throw new TiledException(
                $"Tiled {description} uses zstd compression, which is not supported by this Dreambit build."),
            _ => throw new TiledException(
                $"Tiled {description} uses unsupported compression '{compression}'.")
        };
        using var output = new MemoryStream();
        try
        {
            decompressor.CopyTo(output);
        }
        catch (InvalidDataException exception)
        {
            throw new TiledException(
                $"Tiled {description} contains invalid {compression} data.",
                exception);
        }
        return output.ToArray();
    }

    private static string RemoveWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        return builder.ToString();
    }
}
