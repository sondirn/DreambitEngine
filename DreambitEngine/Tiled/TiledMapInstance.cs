#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit.Tiled;

/// <summary>
/// Runtime ownership handle for everything materialized from one Tiled TMX map.
/// It owns sparse mutable layer state; source TMX/TSX assets remain unchanged.
/// </summary>
public sealed class TiledMapInstance : IDisposable
{
    private readonly Scene _scene;
    private readonly List<Entity> _ownedEntities;
    private readonly HashSet<Entity> _ownedEntitySet;
    private readonly List<TilemapRenderer> _tilemapRenderers;
    private readonly List<TiledRuntimeTileLayer> _runtimeTileLayers;
    private readonly IReadOnlyDictionary<int, int> _layerDrawLayers;
    private readonly Dictionary<string, TiledRuntimeTileset> _tilesets;
    private readonly HashSet<CellChange> _pendingCellChanges = [];
    private readonly HashSet<RenderChunkChange> _dirtyRenderChunks = [];
    private readonly TiledRuntimeAutomapper? _automapper;
    private SceneContentInstance? _contentOwner;
    private int _editDepth;
    private bool _flushing;

    internal TiledMapInstance(
        Scene scene,
        TmxMap map,
        TiledImportOptions importOptions,
        Entity rootEntity,
        List<Entity> ownedEntities,
        List<TilemapRenderer> tilemapRenderers,
        List<TiledRuntimeTileLayer> runtimeTileLayers,
        IReadOnlyDictionary<int, int> layerDrawLayers,
        TiledAutomappingCatalog? automappingCatalog)
    {
        _scene = scene;
        Map = map;
        ImportOptions = importOptions;
        RootEntity = rootEntity;
        _ownedEntities = ownedEntities;
        _ownedEntitySet = new HashSet<Entity>(ownedEntities, ReferenceEqualityComparer.Instance);
        _tilemapRenderers = tilemapRenderers;
        _runtimeTileLayers = runtimeTileLayers;
        _layerDrawLayers = layerDrawLayers;
        _tilesets = new Dictionary<string, TiledRuntimeTileset>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in map.Tilesets)
        {
            var tileset = new TiledRuntimeTileset(reference.EffectiveTileset);
            if (!_tilesets.TryAdd(tileset.AssetName, tileset))
                throw new TiledException(
                    $"Tiled map '{Identifier}' references tileset '{tileset.AssetName}' more than once.");
        }

        foreach (var layer in _runtimeTileLayers)
            layer.Attach(this);

        if (automappingCatalog?.TryGetMapRules(Identifier, out var rules) == true)
            _automapper = new TiledRuntimeAutomapper(this, rules, importOptions.AutomappingSeed);
    }

    public TmxMap Map { get; }
    public TiledImportOptions ImportOptions { get; }
    public float PixelsPerUnit => ImportOptions.PixelsPerUnit;
    public string Identifier => Map.AssetName;
    public Entity RootEntity { get; }
    public IReadOnlyList<Entity> OwnedEntities => _ownedEntities;
    public IReadOnlyList<TilemapRenderer> TilemapRenderers => _tilemapRenderers;
    public IReadOnlyList<TiledRuntimeTileLayer> RuntimeTileLayers => _runtimeTileLayers;
    public IReadOnlyCollection<TiledRuntimeTileset> Tilesets => _tilesets.Values;
    public bool HasAutomappingRules => _automapper is not null;
    public bool IsUnloaded { get; private set; }

    public TiledRuntimeTileLayer GetRuntimeTileLayer(string name)
    {
        if (!TryGetRuntimeTileLayer(name, out var layer))
            throw new KeyNotFoundException($"Tiled map '{Identifier}' has no tile layer named '{name}'.");
        return layer;
    }

    public bool TryGetRuntimeTileLayer(string name, out TiledRuntimeTileLayer layer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureAvailable();
        // TMX stores layers in render order, so the first matching layer is the
        // bottom-most one selected by Tiled Automapping.
        layer = _runtimeTileLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal))!;
        return layer is not null;
    }

    public TiledRuntimeTileset GetTileset(string assetName)
    {
        if (!TryGetTileset(assetName, out var tileset))
            throw new KeyNotFoundException($"Tiled map '{Identifier}' has no tileset asset named '{assetName}'.");
        return tileset;
    }

    public bool TryGetTileset(string assetName, out TiledRuntimeTileset tileset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        EnsureAvailable();
        return _tilesets.TryGetValue(TiledTileReference.NormalizeAssetName(assetName), out tileset!);
    }

    /// <summary>
    /// Defers Automapping and renderer chunk replacement until the outermost edit
    /// scope ends. Nested edit scopes are supported.
    /// </summary>
    public IDisposable BeginTileEdit()
    {
        EnsureAvailable();
        _editDepth++;
        return new TileEditScope(this);
    }

    public int GetDrawLayer(TmxTileLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!EnumerateTileLayers(Map.Layers).Any(candidate => ReferenceEquals(candidate, layer)) ||
            !_layerDrawLayers.TryGetValue(layer.Id, out var drawLayer))
        {
            throw new ArgumentException(
                $"Layer '{layer.Name}' does not belong to loaded map '{Identifier}'.",
                nameof(layer));
        }
        return drawLayer;
    }

    public bool TryGetTileLayer(string name, out TmxTileLayer layer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        layer = EnumerateTileLayers(Map.Layers).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal))!;
        return layer is not null;
    }

    public void ApplyDrawLayer(Entity entity, TmxTileLayer layer, bool includeDescendants = true)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!ReferenceEquals(entity.Scene, _scene))
            throw new InvalidOperationException("The runtime entity belongs to another scene.");

        var drawLayer = GetDrawLayer(layer);
        ApplyDrawLayer(entity, drawLayer);
        if (!includeDescendants)
            return;
        foreach (var child in entity.GetChildren())
            ApplyDrawLayer(child, drawLayer);
    }

    public void TrackEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureAvailable();
        if (!ReferenceEquals(entity.Scene, _scene))
            throw new InvalidOperationException("Only entities belonging to this map's scene can be tracked.");

        _contentOwner?.TrackEntity(entity, true);
        TrackSingleEntity(entity);
        foreach (var child in entity.GetChildren())
            TrackSingleEntity(child);
    }

    public void Unload()
    {
        if (IsUnloaded)
            return;

        // Legacy primary maps own later hierarchy descendants implicitly. Additive maps use
        // the outer SceneContentInstance's exact ownership set and never adopt foreign children.
        if (_contentOwner is null)
        {
            for (var index = 0; index < _ownedEntities.Count; index++)
            {
                var ownedEntity = _ownedEntities[index];
                if (Entity.IsNull(ownedEntity))
                    continue;
                foreach (var child in ownedEntity.GetChildren())
                    if (!Entity.IsNull(child) && ReferenceEquals(child.Scene, _scene))
                        TrackSingleEntity(child);
            }
        }

        Invalidate();
        var cleanupErrors = new List<Exception>();
        for (var index = _ownedEntities.Count - 1; index >= 0; index--)
        {
            var entity = _ownedEntities[index];
            if (Entity.IsNull(entity))
                continue;
            try
            {
                entity.UpdatesSuspended = true;
                entity.Enabled = false;
                _scene.DestroyEntity(entity);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        }

        if (cleanupErrors.Count > 0)
            throw new AggregateException(
                $"One or more Entities failed while unloading Tiled map '{Identifier}'.",
                cleanupErrors);
    }

    /// <summary>
    /// Makes an additive map unavailable without destroying renderer resources while a Scene
    /// callback still holds Component references. The outer SceneContentInstance owns and later
    /// destroys the exact Entity set at the safe callback boundary.
    /// </summary>
    internal void InvalidateForDeferredContentUnload()
    {
        if (IsUnloaded)
            return;
        if (_contentOwner is null)
            throw new InvalidOperationException(
                "Only a Tiled map owned by additive Scene content can use deferred invalidation.");

        Invalidate();
    }

    public void Dispose()
    {
        Unload();
        GC.SuppressFinalize(this);
    }

    internal void SetExplicitTile(
        TiledRuntimeTileLayer layer,
        Point cell,
        TiledTileReference? tile)
    {
        EnsureAvailable();
        if (!_runtimeTileLayers.Contains(layer))
            throw new ArgumentException("The runtime layer belongs to another map instance.", nameof(layer));
        if (tile is { } value)
            ValidateTileReference(value);

        var implicitEdit = _editDepth == 0;
        if (implicitEdit)
            _editDepth++;
        try
        {
            _automapper?.RemoveGeneratedAt(layer, cell);
            layer.SetRuntimeOverride(cell, tile);
        }
        finally
        {
            if (implicitEdit)
                EndTileEdit();
        }
    }

    internal void ValidateTileReference(TiledTileReference tile)
    {
        if (!_tilesets.TryGetValue(tile.TilesetAssetName, out var tileset))
            throw new ArgumentException(
                $"Tile references tileset '{tile.TilesetAssetName}', which is not used by map '{Identifier}'.",
                nameof(tile));
        if (!tileset.ContainsTile(tile.TileId))
            throw new ArgumentOutOfRangeException(
                nameof(tile),
                $"Tileset '{tile.TilesetAssetName}' does not contain local tile ID {tile.TileId}.");
    }

    internal void RecordCellChanged(TiledRuntimeTileLayer layer, Point cell)
    {
        _pendingCellChanges.Add(new CellChange(layer, cell));
        _dirtyRenderChunks.Add(new RenderChunkChange(layer, TiledRuntimeTileLayer.GetRenderChunk(cell)));
    }

    private void EndTileEdit()
    {
        if (_editDepth <= 0)
            throw new InvalidOperationException("No Tiled tile edit is active.");
        _editDepth--;
        if (_editDepth == 0)
            FlushTileChanges();
    }

    private void FlushTileChanges()
    {
        if (_flushing)
            return;
        _flushing = true;
        try
        {
            var pass = 0;
            while (_pendingCellChanges.Count > 0)
            {
                if (++pass > 256)
                    throw new TiledException(
                        $"Runtime Automapping for map '{Identifier}' did not converge after 256 incremental passes.");
                var changes = _pendingCellChanges.ToArray();
                _pendingCellChanges.Clear();
                _automapper?.ProcessChanges(changes);
                if (_automapper is null)
                    break;
            }
            _pendingCellChanges.Clear();

            foreach (var dirty in _dirtyRenderChunks)
                dirty.Layer.RebuildRenderChunk(dirty.Chunk);
            _dirtyRenderChunks.Clear();
        }
        finally
        {
            _flushing = false;
        }
    }

    private void EnsureAvailable()
    {
        if (IsUnloaded)
            throw new ObjectDisposedException(nameof(TiledMapInstance));
    }

    private static IEnumerable<TmxTileLayer> EnumerateTileLayers(IEnumerable<TmxLayer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer is TmxTileLayer tileLayer)
                yield return tileLayer;
            if (layer is not TmxGroupLayer group)
                continue;
            foreach (var child in EnumerateTileLayers(group.Layers))
                yield return child;
        }
    }

    private static void ApplyDrawLayer(Entity entity, int drawLayer)
    {
        foreach (var component in entity.GetAllComponents())
            if (component is DrawableComponent drawable)
                drawable.DrawLayer = drawLayer;
    }

    private void Invalidate()
    {
        IsUnloaded = true;
        _automapper?.Clear();
        _pendingCellChanges.Clear();
        _dirtyRenderChunks.Clear();
    }

    private void TrackSingleEntity(Entity entity)
    {
        if (!ReferenceEquals(entity.Scene, _scene))
            throw new InvalidOperationException("Only entities belonging to this map's scene can be tracked.");
        if (_ownedEntitySet.Add(entity))
            _ownedEntities.Add(entity);
    }

    internal void BindContentOwner(SceneContentInstance owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureAvailable();
        if (!ReferenceEquals(owner.Scene, _scene))
            throw new InvalidOperationException("The content owner belongs to another Scene.");
        if (_contentOwner is { } existing && !ReferenceEquals(existing, owner))
            throw new InvalidOperationException("The Tiled map already belongs to another content instance.");

        foreach (var entity in _ownedEntities)
        {
            if (entity.ContentOwner is { } entityOwner && !ReferenceEquals(entityOwner, owner))
                throw new InvalidOperationException(
                    $"Tiled Entity '{entity.Name}' already belongs to another content instance.");
        }

        foreach (var entity in _ownedEntities)
            owner.TrackCreatedEntity(entity);
        _contentOwner = owner;
    }

    internal readonly record struct CellChange(TiledRuntimeTileLayer Layer, Point Cell);
    private readonly record struct RenderChunkChange(TiledRuntimeTileLayer Layer, Point Chunk);

    private sealed class TileEditScope(TiledMapInstance owner) : IDisposable
    {
        private TiledMapInstance? _owner = owner;

        public void Dispose()
        {
            var current = _owner;
            if (current is null)
                return;
            _owner = null;
            current.EndTileEdit();
        }
    }
}
