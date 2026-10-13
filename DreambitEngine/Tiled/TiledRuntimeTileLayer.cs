#nullable enable

using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit.Tiled;

/// <summary>
/// Mutable, sparse logical cell state for one imported Tiled tile layer. Source,
/// gameplay overrides and Automapping output are kept separate so stale generated
/// output can be removed without destroying authored cells.
/// </summary>
public sealed class TiledRuntimeTileLayer
{
    private readonly Dictionary<Point, TiledTileReference> _sourceTiles;
    private readonly Dictionary<Point, TiledTileReference?> _runtimeOverrides = [];
    private readonly Dictionary<Point, TiledTileReference?> _generatedTiles = [];
    private readonly Dictionary<Point, TiledTileReference> _effectiveTiles;
    private readonly Dictionary<Point, Dictionary<Point, TiledTileReference>> _effectiveChunks = [];
    private readonly Func<Point, TiledTileReference, TilemapTile> _renderTileFactory;
    private TiledMapInstance? _owner;

    internal TiledRuntimeTileLayer(
        TmxTileLayer sourceLayer,
        Dictionary<Point, TiledTileReference> sourceTiles,
        TilemapLayerData rendererData,
        Func<Point, TiledTileReference, TilemapTile> renderTileFactory,
        TilemapRenderer? renderer)
    {
        SourceLayer = sourceLayer ?? throw new ArgumentNullException(nameof(sourceLayer));
        _sourceTiles = sourceTiles ?? throw new ArgumentNullException(nameof(sourceTiles));
        _effectiveTiles = new Dictionary<Point, TiledTileReference>(sourceTiles);
        RendererData = rendererData ?? throw new ArgumentNullException(nameof(rendererData));
        _renderTileFactory = renderTileFactory ?? throw new ArgumentNullException(nameof(renderTileFactory));
        Renderer = renderer;

        foreach (var pair in _effectiveTiles)
            AddEffectiveChunkCell(pair.Key, pair.Value);
    }

    public string Name => SourceLayer.Name ?? string.Empty;
    public TmxTileLayer SourceLayer { get; }
    public TilemapLayerData RendererData { get; }
    public TilemapRenderer? Renderer { get; }
    public int TileCount => _effectiveTiles.Count;

    public TiledTileReference? GetTile(int x, int y)
    {
        EnsureAvailable();
        return _effectiveTiles.TryGetValue(new Point(x, y), out var tile) ? tile : null;
    }

    public bool TryGetTile(int x, int y, out TiledTileReference tile)
    {
        EnsureAvailable();
        return _effectiveTiles.TryGetValue(new Point(x, y), out tile);
    }

    public void SetTile(int x, int y, TiledTileReference tile)
    {
        EnsureAvailable();
        _owner!.SetExplicitTile(this, new Point(x, y), tile);
    }

    public void ClearTile(int x, int y)
    {
        EnsureAvailable();
        _owner!.SetExplicitTile(this, new Point(x, y), null);
    }

    internal void Attach(TiledMapInstance owner)
    {
        if (_owner is not null)
            throw new InvalidOperationException("The runtime Tiled layer is already attached to a map instance.");
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    internal TiledTileReference? GetBaseTile(Point cell)
    {
        if (_runtimeOverrides.TryGetValue(cell, out var runtime))
            return runtime;
        return _sourceTiles.TryGetValue(cell, out var source) ? source : null;
    }

    internal bool SetRuntimeOverride(Point cell, TiledTileReference? tile)
    {
        var before = GetEffectiveTile(cell);
        _runtimeOverrides[cell] = tile;
        return UpdateEffectiveCell(cell, before);
    }

    internal bool SetGeneratedTile(Point cell, bool hasGeneratedValue, TiledTileReference? tile)
    {
        var before = GetEffectiveTile(cell);
        if (hasGeneratedValue)
            _generatedTiles[cell] = tile;
        else
            _generatedTiles.Remove(cell);
        return UpdateEffectiveCell(cell, before);
    }

    internal void RebuildRenderChunk(Point chunkCoordinate)
    {
        if (!_effectiveChunks.TryGetValue(chunkCoordinate, out var cells))
        {
            RendererData.ReplaceChunk(chunkCoordinate, Array.Empty<TilemapTile>());
            return;
        }

        var tiles = new TilemapTile[cells.Count];
        var index = 0;
        foreach (var pair in cells)
            tiles[index++] = _renderTileFactory(pair.Key, pair.Value) with { Chunk = chunkCoordinate };
        RendererData.ReplaceChunk(chunkCoordinate, tiles);
    }

    internal static Point GetRenderChunk(Point logicalCell) =>
        TilemapLayerData.GetDefaultChunkCoordinate(logicalCell);

    private TiledTileReference? GetEffectiveTile(Point cell)
    {
        if (_generatedTiles.TryGetValue(cell, out var generated))
            return generated;
        return GetBaseTile(cell);
    }

    private bool UpdateEffectiveCell(Point cell, TiledTileReference? before)
    {
        var after = GetEffectiveTile(cell);
        if (before == after)
            return false;

        var chunk = GetRenderChunk(cell);
        if (after is { } tile)
        {
            _effectiveTiles[cell] = tile;
            AddEffectiveChunkCell(cell, tile);
        }
        else
        {
            _effectiveTiles.Remove(cell);
            if (_effectiveChunks.TryGetValue(chunk, out var cells))
            {
                cells.Remove(cell);
                if (cells.Count == 0)
                    _effectiveChunks.Remove(chunk);
            }
        }

        _owner?.RecordCellChanged(this, cell);
        return true;
    }

    private void AddEffectiveChunkCell(Point cell, TiledTileReference tile)
    {
        var chunk = GetRenderChunk(cell);
        if (!_effectiveChunks.TryGetValue(chunk, out var cells))
        {
            cells = [];
            _effectiveChunks.Add(chunk, cells);
        }
        cells[cell] = tile;
    }

    private void EnsureAvailable()
    {
        if (_owner is null)
            throw new InvalidOperationException("The runtime Tiled layer is not attached to a map instance.");
        if (_owner.IsUnloaded)
            throw new ObjectDisposedException(nameof(TiledMapInstance));
    }
}
