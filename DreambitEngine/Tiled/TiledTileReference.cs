#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dreambit.Tiled;

/// <summary>
/// Stable runtime identity for a Tiled tile. Unlike a TMX GID, this identity can
/// be compared between maps because the ID is local to a resolved tileset asset.
/// </summary>
public readonly record struct TiledTileReference
{
    public TiledTileReference(
        string tilesetAssetName,
        int tileId,
        TmxTileFlipFlags flipFlags = TmxTileFlipFlags.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tilesetAssetName);
        ArgumentOutOfRangeException.ThrowIfNegative(tileId);
        TilesetAssetName = NormalizeAssetName(tilesetAssetName);
        TileId = tileId;
        FlipFlags = flipFlags;
    }

    public string TilesetAssetName { get; }
    public int TileId { get; }
    public TmxTileFlipFlags FlipFlags { get; }

    internal static string NormalizeAssetName(string value) =>
        value.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
}

/// <summary>
/// Lightweight runtime view over an already resolved TMX/TSX tileset. It does
/// not own textures and produces references using tileset-local IDs.
/// </summary>
public sealed class TiledRuntimeTileset
{
    private readonly HashSet<int>? _imageCollectionTileIds;
    private readonly int _atlasTileCount;

    internal TiledRuntimeTileset(TmxTileset tileset)
    {
        Source = tileset ?? throw new ArgumentNullException(nameof(tileset));
        AssetName = TiledTileReference.NormalizeAssetName(tileset.AssetName);
        if (string.IsNullOrWhiteSpace(AssetName))
            throw new TiledException($"Tiled tileset '{tileset.Name}' has no stable asset name.");

        if (tileset.Image is null)
        {
            _imageCollectionTileIds = tileset.Tiles.Select(tile => tile.Id).ToHashSet();
            TileCount = _imageCollectionTileIds.Count;
            return;
        }

        _atlasTileCount = tileset.TileCount;
        if (_atlasTileCount <= 0 && tileset.Columns > 0 && tileset.Image.Height > 0 && tileset.TileHeight > 0)
        {
            var rows = Math.Max(
                0,
                (tileset.Image.Height - tileset.Margin * 2 + tileset.Spacing) /
                (tileset.TileHeight + tileset.Spacing));
            _atlasTileCount = checked(tileset.Columns * rows);
        }
        if (_atlasTileCount <= 0 && tileset.Tiles.Count > 0)
            _atlasTileCount = checked(tileset.Tiles.Max(tile => tile.Id) + 1);
        TileCount = _atlasTileCount;
    }

    public string AssetName { get; }
    public string? Name => Source.Name;
    public int TileCount { get; }
    public TmxTileset Source { get; }

    public TiledTileReference GetTile(
        int tileId,
        TmxTileFlipFlags flipFlags = TmxTileFlipFlags.None)
    {
        if (!ContainsTile(tileId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileId),
                tileId,
                $"Tileset '{AssetName}' does not contain local tile ID {tileId}.");
        }
        return new TiledTileReference(AssetName, tileId, flipFlags);
    }

    public bool TryGetTile(
        int tileId,
        out TiledTileReference tile,
        TmxTileFlipFlags flipFlags = TmxTileFlipFlags.None)
    {
        if (!ContainsTile(tileId))
        {
            tile = default;
            return false;
        }
        tile = new TiledTileReference(AssetName, tileId, flipFlags);
        return true;
    }

    internal bool ContainsTile(int tileId) => tileId >= 0 &&
        (_imageCollectionTileIds?.Contains(tileId) ?? tileId < _atlasTileCount);
}
