#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dreambit.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.Tiled;

/// <summary>
/// Converts orthogonal TMX tile layers into Dreambit-native rendering components.
/// Object and image layers are intentionally ignored.
/// </summary>
public sealed class TiledMapImporter
{
    private readonly Func<string, Texture2D?> _textureResolver;
    private readonly Func<TiledAutomappingCatalog?> _automappingCatalogResolver;

    public TiledMapImporter()
        : this(
            assetName => Resources.LoadAsset<Texture2D>(assetName),
            TiledAutomappingCatalog.TryLoad)
    {
    }

    public TiledMapImporter(Func<string, Texture2D?> textureResolver)
        : this(textureResolver, static () => null)
    {
    }

    public TiledMapImporter(
        Func<string, Texture2D?> textureResolver,
        Func<TiledAutomappingCatalog?> automappingCatalogResolver)
    {
        _textureResolver = textureResolver ?? throw new ArgumentNullException(nameof(textureResolver));
        _automappingCatalogResolver = automappingCatalogResolver ??
                                        throw new ArgumentNullException(nameof(automappingCatalogResolver));
    }

    public TiledMapInstance Import(
        Scene scene,
        TmxMap map,
        TiledImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(map);
        options ??= new TiledImportOptions();
        options.Validate();
        ValidateMap(map);

        var ownedEntities = new List<Entity>();
        var tilemapRenderers = new List<TilemapRenderer>();
        var runtimeTileLayers = new List<TiledRuntimeTileLayer>();
        var layerDrawLayers = new Dictionary<int, int>();
        var importedLayerIds = new HashSet<int>();
        var context = new ImportContext(map, _textureResolver);
        var mapName = string.IsNullOrWhiteSpace(map.AssetName)
            ? "Map"
            : Path.GetFileName(map.AssetName.Replace('/', Path.DirectorySeparatorChar));
        var root = scene.CreateEntity(
            $"Tiled Map: {mapName}",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tiled", "tiled-map" });
        root.TiledSourceKey = TiledGeneratedEntityKeys.Map;
        ownedEntities.Add(root);

        try
        {
            var depthBase = checked(
                options.BaseDrawLayer + options.WorldDepth * options.WorldDepthDrawLayerStride);
            var tileLayerIndex = 0;
            var infiniteBounds = new PixelBoundsAccumulator();
            ImportLayers(
                scene,
                root,
                map.Layers,
                string.Empty,
                Vector2.Zero,
                ancestorVisible: true,
                inheritedOpacity: 1f,
                inheritedTint: Color.White,
                materialize: true,
                options,
                context,
                ownedEntities,
                tilemapRenderers,
                runtimeTileLayers,
                layerDrawLayers,
                importedLayerIds,
                infiniteBounds,
                depthBase,
                ref tileLayerIndex);

            if (options.RenderMapBackgroundColor && !string.IsNullOrWhiteSpace(map.BackgroundColor))
            {
                var backgroundBounds = GetBackgroundBounds(map, infiniteBounds);
                if (backgroundBounds.HasValue)
                    ImportBackground(scene, root, map, options, depthBase, backgroundBounds.Value, ownedEntities);
            }

            return new TiledMapInstance(
                scene,
                map,
                options,
                root,
                ownedEntities,
                tilemapRenderers,
                runtimeTileLayers,
                layerDrawLayers,
                _automappingCatalogResolver());
        }
        catch
        {
            for (var index = ownedEntities.Count - 1; index >= 0; index--)
                scene.DestroyEntity(ownedEntities[index]);
            throw;
        }
    }

    public TilemapLayerData CreateTilemapLayerData(
        TmxMap map,
        TmxTileLayer layer,
        float pixelsPerUnit = 1f)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(layer);
        if (!float.IsFinite(pixelsPerUnit) || pixelsPerUnit <= 0f)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerUnit));
        ValidateMap(map);

        var bounds = GetLayerGridBounds(map, layer);
        if (!bounds.HasValue)
            throw new TiledException($"Tiled layer '{layer.Name}' has no finite tile grid.");
        var opacity = GetOpacity(layer.Opacity, layer.Name);
        var tint = ApplyOpacity(ParseColor(layer.TintColor, Color.White), opacity);
        var context = new ImportContext(map, _textureResolver);
        return context.CreateLayerData(
            layer,
            TmxTileDataDecoder.DecodeLayer(map, layer),
            bounds.Value,
            pixelsPerUnit,
            tint);
    }

    private static void ImportLayers(
        Scene scene,
        Entity parent,
        IReadOnlyList<TmxLayer> layers,
        string pathPrefix,
        Vector2 accumulatedOffsetPixels,
        bool ancestorVisible,
        float inheritedOpacity,
        Color inheritedTint,
        bool materialize,
        TiledImportOptions options,
        ImportContext context,
        List<Entity> ownedEntities,
        List<TilemapRenderer> tilemapRenderers,
        List<TiledRuntimeTileLayer> runtimeTileLayers,
        Dictionary<int, int> layerDrawLayers,
        HashSet<int> importedLayerIds,
        PixelBoundsAccumulator infiniteBounds,
        int depthBase,
        ref int tileLayerIndex)
    {
        foreach (var layer in layers)
        {
            var name = string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {layer.Id}" : layer.Name!;
            var layerPath = string.IsNullOrWhiteSpace(pathPrefix) ? name : $"{pathPrefix}/{name}";
            var visible = ancestorVisible && layer.Visible;
            var includeLayer = materialize && (visible || options.IncludeInvisibleLayers);
            var opacity = inheritedOpacity * GetOpacity(layer.Opacity, name);
            var tint = MultiplyColors(inheritedTint, ParseColor(layer.TintColor, Color.White));
            var offsetPixels = new Vector2((float)layer.OffsetX, (float)layer.OffsetY);

            switch (layer)
            {
                case TmxGroupLayer group:
                {
                    if (!importedLayerIds.Add(group.Id))
                        throw new TiledException($"Tiled map contains duplicate layer ID '{group.Id}'.");
                    if (includeLayer)
                        ValidateBlendMode(group, layerPath);
                    var groupParent = parent;
                    if (includeLayer)
                    {
                        groupParent = CreateChild(
                            scene,
                            parent,
                            $"Tiled Group: {layerPath}",
                            offsetPixels / options.PixelsPerUnit,
                            "tiled-group");
                        groupParent.TiledSourceKey = TiledGeneratedEntityKeys.Group(group.Id);
                        ownedEntities.Add(groupParent);
                    }

                    ImportLayers(
                        scene,
                        groupParent,
                        group.Layers,
                        layerPath,
                        accumulatedOffsetPixels + offsetPixels,
                        visible,
                        opacity,
                        tint,
                        includeLayer,
                        options,
                        context,
                        ownedEntities,
                        tilemapRenderers,
                        runtimeTileLayers,
                        layerDrawLayers,
                        importedLayerIds,
                        infiniteBounds,
                        depthBase,
                        ref tileLayerIndex);
                    break;
                }
                case TmxTileLayer tileLayer:
                {
                    if (!importedLayerIds.Add(tileLayer.Id))
                        throw new TiledException($"Tiled map contains duplicate layer ID '{tileLayer.Id}'.");
                    var drawLayer = checked(depthBase + (++tileLayerIndex) * options.DrawLayerStep);
                    layerDrawLayers.Add(tileLayer.Id, drawLayer);
                    ImportTileLayer(
                        scene,
                        parent,
                        tileLayer,
                        layerPath,
                        accumulatedOffsetPixels,
                        offsetPixels,
                        options,
                        context,
                        ownedEntities,
                        tilemapRenderers,
                        runtimeTileLayers,
                        infiniteBounds,
                        ApplyOpacity(tint, opacity),
                        drawLayer,
                        includeLayer);
                    break;
                }
            }
        }
    }

    private static void ImportTileLayer(
        Scene scene,
        Entity parent,
        TmxTileLayer layer,
        string layerPath,
        Vector2 accumulatedOffsetPixels,
        Vector2 layerOffsetPixels,
        TiledImportOptions options,
        ImportContext context,
        List<Entity> ownedEntities,
        List<TilemapRenderer> tilemapRenderers,
        List<TiledRuntimeTileLayer> runtimeTileLayers,
        PixelBoundsAccumulator infiniteBounds,
        Color tint,
        int drawLayer,
        bool materialize)
    {
        if (materialize)
            ValidateBlendMode(layer, layerPath);

        var bounds = GetLayerGridBounds(context.Map, layer);
        var logicalOrigin = bounds.HasValue
            ? new Point(bounds.Value.MinimumX, bounds.Value.MinimumY)
            : new Point(layer.X, layer.Y);
        var layerOriginPixels = layerOffsetPixels;
        if (bounds.HasValue)
        {
            layerOriginPixels += new Vector2(
                bounds.Value.MinimumX * context.Map.TileWidth,
                bounds.Value.MinimumY * context.Map.TileHeight);
            infiniteBounds.Include(new RectangleF(
                accumulatedOffsetPixels.X + layerOriginPixels.X,
                accumulatedOffsetPixels.Y + layerOriginPixels.Y,
                bounds.Value.Columns * context.Map.TileWidth,
                bounds.Value.Rows * context.Map.TileHeight));
        }

        var cells = layer.Data is null
            ? Array.Empty<TmxTileCell>()
            : TmxTileDataDecoder.DecodeLayer(context.Map, layer);
        var sourceTiles = context.CreateRuntimeTiles(layer, cells);
        TilemapTile RenderTile(Point cell, TiledTileReference tile) => context.CreateRenderTile(
            layer,
            cell,
            logicalOrigin,
            tile,
            options.PixelsPerUnit,
            tint);

        var initialRenderTiles = new TilemapTile[sourceTiles.Count];
        var tileIndex = 0;
        foreach (var pair in sourceTiles)
            initialRenderTiles[tileIndex++] = RenderTile(pair.Key, pair.Value);
        var data = new TilemapLayerData(
            Math.Max(1, bounds?.Columns ?? layer.Width),
            Math.Max(1, bounds?.Rows ?? layer.Height),
            new Vector2(
                context.Map.TileWidth / options.PixelsPerUnit,
                context.Map.TileHeight / options.PixelsPerUnit),
            initialRenderTiles,
            GetRenderOrder(context.Map.RenderOrder),
            allowCellsOutsideGrid: true);

        TilemapRenderer? renderer = null;
        if (materialize)
        {
            var entity = CreateChild(
                scene,
                parent,
                $"Tiled Layer: {layerPath}",
                layerOriginPixels / options.PixelsPerUnit,
                "tiled-layer");
            entity.TiledSourceKey = TiledGeneratedEntityKeys.Layer(layer.Id);
            ownedEntities.Add(entity);
            renderer = entity.AttachComponent<TilemapRenderer>().Configure(data);
            renderer.DrawLayer = drawLayer;
            tilemapRenderers.Add(renderer);
        }

        runtimeTileLayers.Add(new TiledRuntimeTileLayer(
            layer,
            sourceTiles,
            data,
            RenderTile,
            renderer));
    }

    private static void ImportBackground(
        Scene scene,
        Entity root,
        TmxMap map,
        TiledImportOptions options,
        int drawLayer,
        RectangleF pixelBounds,
        List<Entity> ownedEntities)
    {
        var entity = CreateChild(
            scene,
            root,
            "Tiled Background Color",
            new Vector2(pixelBounds.X, pixelBounds.Y) / options.PixelsPerUnit,
            "tiled-background");
        entity.TiledSourceKey = TiledGeneratedEntityKeys.BackgroundColor;
        ownedEntities.Add(entity);
        var rectangle = entity.AttachComponent<FilledRectDrawer>();
        rectangle.Width = pixelBounds.Width / options.PixelsPerUnit;
        rectangle.Height = pixelBounds.Height / options.PixelsPerUnit;
        rectangle.Color = ParseColor(map.BackgroundColor, Color.Transparent);
        rectangle.DrawLayer = drawLayer;
    }

    private static Entity CreateChild(
        Scene scene,
        Entity parent,
        string name,
        Vector2 localPosition,
        string tag)
    {
        var entity = scene.CreateEntity(
            name,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tiled", tag });
        entity.Parent = parent;
        entity.Transform.Position2D = localPosition;
        entity.Transform.ResetLastWorldPosition();
        return entity;
    }

    private static RectangleF? GetBackgroundBounds(TmxMap map, PixelBoundsAccumulator infiniteBounds)
    {
        if (!map.Infinite && map.Width > 0 && map.Height > 0)
        {
            return new RectangleF(
                0f,
                0f,
                checked(map.Width * map.TileWidth),
                checked(map.Height * map.TileHeight));
        }
        return infiniteBounds.TryGetBounds(out var bounds) ? bounds : null;
    }

    private static LayerGridBounds? GetLayerGridBounds(TmxMap map, TmxTileLayer layer)
    {
        if (layer.Data?.Chunks is { Count: > 0 } chunks)
        {
            var minimumX = int.MaxValue;
            var minimumY = int.MaxValue;
            var maximumX = int.MinValue;
            var maximumY = int.MinValue;
            foreach (var chunk in chunks)
            {
                if (chunk.Width <= 0 || chunk.Height <= 0)
                    throw new TiledException(
                        $"Tiled layer '{layer.Name}' contains an invalid {chunk.Width}x{chunk.Height} chunk.");
                minimumX = Math.Min(minimumX, checked(layer.X + chunk.X));
                minimumY = Math.Min(minimumY, checked(layer.Y + chunk.Y));
                maximumX = Math.Max(maximumX, checked(layer.X + chunk.X + chunk.Width));
                maximumY = Math.Max(maximumY, checked(layer.Y + chunk.Y + chunk.Height));
            }
            return new LayerGridBounds(minimumX, minimumY, maximumX, maximumY);
        }

        var width = layer.Width > 0 ? layer.Width : map.Width;
        var height = layer.Height > 0 ? layer.Height : map.Height;
        return width > 0 && height > 0
            ? new LayerGridBounds(layer.X, layer.Y, checked(layer.X + width), checked(layer.Y + height))
            : null;
    }

    private static TilemapRenderOrder GetRenderOrder(string? renderOrder)
        => renderOrder?.Trim().ToLowerInvariant() switch
        {
            null or "" or "right-down" => TilemapRenderOrder.RightDown,
            "right-up" => TilemapRenderOrder.RightUp,
            "left-down" => TilemapRenderOrder.LeftDown,
            "left-up" => TilemapRenderOrder.LeftUp,
            _ => throw new TiledException($"Unsupported Tiled render order '{renderOrder}'.")
        };

    private static void ValidateMap(TmxMap map)
    {
        if (!string.Equals(map.Orientation, "orthogonal", StringComparison.OrdinalIgnoreCase))
            throw new TiledException(
                $"Tiled map '{map.AssetName}' uses unsupported orientation '{map.Orientation}'. Only orthogonal maps are supported.");
        if (map.TileWidth <= 0 || map.TileHeight <= 0)
            throw new TiledException(
                $"Tiled map '{map.AssetName}' has invalid tile dimensions {map.TileWidth}x{map.TileHeight}.");
        _ = GetRenderOrder(map.RenderOrder);
    }

    private static float GetOpacity(double value, string? layerName)
    {
        if (!double.IsFinite(value))
            throw new TiledException($"Tiled layer '{layerName}' has non-finite opacity.");
        return MathHelper.Clamp((float)value, 0f, 1f);
    }

    private static void ValidateBlendMode(TmxLayer layer, string layerPath)
    {
        if (string.IsNullOrWhiteSpace(layer.BlendMode) ||
            string.Equals(layer.BlendMode, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new TiledException(
            $"Tiled layer '{layerPath}' uses unsupported blend mode '{layer.BlendMode}'.");
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var text = value.Trim().TrimStart('#');
        try
        {
            return text.Length switch
            {
                6 => new Color(
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    byte.MaxValue),
                8 => new Color(
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16),
                    Convert.ToByte(text[..2], 16)),
                _ => throw new FormatException()
            };
        }
        catch (FormatException exception)
        {
            throw new TiledException($"Invalid Tiled color '{value}'.", exception);
        }
    }

    private static Color MultiplyColors(Color left, Color right)
        => new(
            left.R * right.R / byte.MaxValue,
            left.G * right.G / byte.MaxValue,
            left.B * right.B / byte.MaxValue,
            left.A * right.A / byte.MaxValue);

    private static Color ApplyOpacity(Color color, float opacity)
        => new(color.R, color.G, color.B, (byte)Math.Clamp(MathF.Round(color.A * opacity), 0f, byte.MaxValue));

    private readonly record struct LayerGridBounds(
        int MinimumX,
        int MinimumY,
        int MaximumX,
        int MaximumY)
    {
        public int Columns => checked(MaximumX - MinimumX);
        public int Rows => checked(MaximumY - MinimumY);
    }

    private sealed class PixelBoundsAccumulator
    {
        private bool _hasBounds;
        private float _minimumX;
        private float _minimumY;
        private float _maximumX;
        private float _maximumY;

        public void Include(RectangleF bounds)
        {
            if (bounds.Width <= 0f || bounds.Height <= 0f)
                return;
            if (!_hasBounds)
            {
                _hasBounds = true;
                _minimumX = bounds.Left;
                _minimumY = bounds.Top;
                _maximumX = bounds.Right;
                _maximumY = bounds.Bottom;
                return;
            }
            _minimumX = MathF.Min(_minimumX, bounds.Left);
            _minimumY = MathF.Min(_minimumY, bounds.Top);
            _maximumX = MathF.Max(_maximumX, bounds.Right);
            _maximumY = MathF.Max(_maximumY, bounds.Bottom);
        }

        public bool TryGetBounds(out RectangleF bounds)
        {
            bounds = _hasBounds
                ? new RectangleF(_minimumX, _minimumY, _maximumX - _minimumX, _maximumY - _minimumY)
                : RectangleF.Empty;
            return _hasBounds;
        }
    }

    private sealed class ImportContext
    {
        private readonly Func<string, Texture2D?> _textureResolver;
        private readonly TilesetBinding[] _tilesets;
        private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(TmxTileset Tileset, int TileId), TilemapAnimation> _animations = [];

        public ImportContext(TmxMap map, Func<string, Texture2D?> textureResolver)
        {
            Map = map;
            _textureResolver = textureResolver;
            _tilesets = map.Tilesets
                .Select(reference => new TilesetBinding(reference.FirstGid, reference.EffectiveTileset))
                .OrderBy(binding => binding.FirstGid)
                .ToArray();
            if (_tilesets.Any(binding => binding.FirstGid == 0))
                throw new TiledException($"Tiled map '{map.AssetName}' contains a tileset without firstgid.");
            if (_tilesets.Select(binding => binding.FirstGid).Distinct().Count() != _tilesets.Length)
                throw new TiledException($"Tiled map '{map.AssetName}' contains duplicate firstgid values.");
        }

        public TmxMap Map { get; }

        public Dictionary<Point, TiledTileReference> CreateRuntimeTiles(
            TmxTileLayer layer,
            IReadOnlyList<TmxTileCell> cells)
        {
            var result = new Dictionary<Point, TiledTileReference>();
            foreach (var cell in cells)
            {
                if (cell.GlobalTileId == 0)
                    continue;
                if (cell.FlipFlags.HasFlag(TmxTileFlipFlags.Hexagonal120))
                    throw new TiledException(
                        $"Tiled layer '{layer.Name}' uses the hexagonal 120-degree rotation flag in an orthogonal map.");

                var binding = ResolveTileset(cell.GlobalTileId);
                var localTileId = checked((int)(cell.GlobalTileId - binding.FirstGid));
                var reference = new TiledTileReference(
                    binding.Tileset.AssetName,
                    localTileId,
                    cell.FlipFlags);
                if (!result.TryAdd(new Point(cell.X, cell.Y), reference))
                    throw new TiledException(
                        $"Tiled layer '{layer.Name}' contains more than one tile at ({cell.X}, {cell.Y}).");
            }
            return result;
        }

        public TilemapTile CreateRenderTile(
            TmxTileLayer layer,
            Point logicalCell,
            Point logicalOrigin,
            TiledTileReference tile,
            float pixelsPerUnit,
            Color tint)
        {
            var binding = _tilesets.FirstOrDefault(candidate => string.Equals(
                TiledTileReference.NormalizeAssetName(candidate.Tileset.AssetName),
                tile.TilesetAssetName,
                StringComparison.OrdinalIgnoreCase));
            if (binding.Tileset is null)
                throw new TiledException(
                    $"Tiled layer '{layer.Name}' references unknown tileset '{tile.TilesetAssetName}'.");
            if (tile.FlipFlags.HasFlag(TmxTileFlipFlags.Hexagonal120))
                throw new TiledException(
                    $"Tiled layer '{layer.Name}' uses the hexagonal 120-degree rotation flag in an orthogonal map.");

            var visual = ResolveVisual(binding.Tileset, tile.TileId);
            var transform = ResolveTransform(tile.FlipFlags);
            var pixelSize = transform.QuarterTurn
                ? new Vector2(visual.PixelSize.Y, visual.PixelSize.X)
                : visual.PixelSize;
            var localCell = new Point(
                checked(logicalCell.X - logicalOrigin.X),
                checked(logicalCell.Y - logicalOrigin.Y));
            var tileOffset = binding.Tileset.TileOffset;
            var positionPixels = new Vector2(
                localCell.X * Map.TileWidth + (tileOffset?.X ?? 0),
                localCell.Y * Map.TileHeight + Map.TileHeight - pixelSize.Y + (tileOffset?.Y ?? 0));
            return new TilemapTile(
                positionPixels / pixelsPerUnit,
                pixelSize / pixelsPerUnit,
                visual.SourceRectangle,
                tint,
                transform.Effects,
                visual.Texture,
                transform.Rotation,
                ResolveAnimation(binding.Tileset, tile.TileId),
                localCell,
                TiledRuntimeTileLayer.GetRenderChunk(logicalCell));
        }

        public TilemapLayerData CreateLayerData(
            TmxTileLayer layer,
            IReadOnlyList<TmxTileCell> cells,
            LayerGridBounds bounds,
            float pixelsPerUnit,
            Color tint)
        {
            var tiles = new List<TilemapTile>();
            foreach (var cell in cells)
            {
                if (cell.GlobalTileId == 0)
                    continue;
                if (cell.FlipFlags.HasFlag(TmxTileFlipFlags.Hexagonal120))
                    throw new TiledException(
                        $"Tiled layer '{layer.Name}' uses the hexagonal 120-degree rotation flag in an orthogonal map.");

                var binding = ResolveTileset(cell.GlobalTileId);
                var localTileId = checked((int)(cell.GlobalTileId - binding.FirstGid));
                var visual = ResolveVisual(binding.Tileset, localTileId);
                var transform = ResolveTransform(cell.FlipFlags);
                var pixelSize = transform.QuarterTurn
                    ? new Vector2(visual.PixelSize.Y, visual.PixelSize.X)
                    : visual.PixelSize;
                var localCell = new Point(
                    checked(cell.X - bounds.MinimumX),
                    checked(cell.Y - bounds.MinimumY));
                var tileOffset = binding.Tileset.TileOffset;
                var positionPixels = new Vector2(
                    localCell.X * Map.TileWidth + (tileOffset?.X ?? 0),
                    localCell.Y * Map.TileHeight + Map.TileHeight - pixelSize.Y + (tileOffset?.Y ?? 0));
                var animation = ResolveAnimation(binding.Tileset, localTileId);
                tiles.Add(new TilemapTile(
                    positionPixels / pixelsPerUnit,
                    pixelSize / pixelsPerUnit,
                    visual.SourceRectangle,
                    tint,
                    transform.Effects,
                    visual.Texture,
                    transform.Rotation,
                    animation,
                    localCell,
                    cell.ChunkX.HasValue && cell.ChunkY.HasValue
                        ? new Point(cell.ChunkX.Value, cell.ChunkY.Value)
                        : null));
            }

            return new TilemapLayerData(
                bounds.Columns,
                bounds.Rows,
                new Vector2(Map.TileWidth / pixelsPerUnit, Map.TileHeight / pixelsPerUnit),
                tiles,
                GetRenderOrder(Map.RenderOrder));
        }

        private TilesetBinding ResolveTileset(uint globalTileId)
        {
            for (var index = _tilesets.Length - 1; index >= 0; index--)
                if (globalTileId >= _tilesets[index].FirstGid)
                    return _tilesets[index];
            throw new TiledException(
                $"Tiled map '{Map.AssetName}' references GID {globalTileId} before its first tileset.");
        }

        private TileVisual ResolveVisual(TmxTileset tileset, int tileId)
        {
            var tile = tileset.Tiles.FirstOrDefault(candidate => candidate.Id == tileId);
            var tileImage = tile?.Image;
            var image = tileImage ?? tileset.Image
                        ?? throw new TiledException(
                            $"Tiled tileset '{tileset.Name}' has no image for tile {tileId}.");
            if (string.IsNullOrWhiteSpace(image.Source))
                throw new TiledException(
                    $"Tiled tileset '{tileset.Name}' uses embedded image data, which is not supported.");

            var texture = LoadTexture(tileset, image);
            Rectangle sourceRectangle;
            if (tileImage is not null)
            {
                var width = tile is { Width: > 0 } ? tile.Width : image.Width > 0 ? image.Width : texture.Width;
                var height = tile is { Height: > 0 } ? tile.Height : image.Height > 0 ? image.Height : texture.Height;
                sourceRectangle = new Rectangle(tile?.X ?? 0, tile?.Y ?? 0, width, height);
            }
            else
            {
                var tileWidth = tile is { Width: > 0 } ? tile.Width : tileset.TileWidth;
                var tileHeight = tile is { Height: > 0 } ? tile.Height : tileset.TileHeight;
                if (tileWidth <= 0 || tileHeight <= 0)
                    throw new TiledException(
                        $"Tiled tileset '{tileset.Name}' has invalid tile dimensions {tileWidth}x{tileHeight}.");
                var columns = tileset.Columns > 0
                    ? tileset.Columns
                    : Math.Max(1, (texture.Width - tileset.Margin * 2 + tileset.Spacing) / (tileWidth + tileset.Spacing));
                sourceRectangle = tile is { Width: > 0, Height: > 0 }
                    ? new Rectangle(tile.X, tile.Y, tileWidth, tileHeight)
                    : new Rectangle(
                        tileset.Margin + tileId % columns * (tileWidth + tileset.Spacing),
                        tileset.Margin + tileId / columns * (tileHeight + tileset.Spacing),
                        tileWidth,
                        tileHeight);
            }

            if (sourceRectangle.Left < 0 || sourceRectangle.Top < 0 ||
                sourceRectangle.Right > texture.Width || sourceRectangle.Bottom > texture.Height)
            {
                throw new TiledException(
                    $"Tile {tileId} in Tiled tileset '{tileset.Name}' resolves outside texture '{image.ResolvedAssetName}'.");
            }

            var pixelSize = string.Equals(tileset.TileRenderSize, "grid", StringComparison.OrdinalIgnoreCase)
                ? new Vector2(Map.TileWidth, Map.TileHeight)
                : new Vector2(sourceRectangle.Width, sourceRectangle.Height);
            if (string.Equals(tileset.FillMode, "preserve-aspect-fit", StringComparison.OrdinalIgnoreCase))
            {
                var fit = MathF.Min(
                    Map.TileWidth / (float)sourceRectangle.Width,
                    Map.TileHeight / (float)sourceRectangle.Height);
                pixelSize = new Vector2(sourceRectangle.Width * fit, sourceRectangle.Height * fit);
            }
            return new TileVisual(texture, sourceRectangle, pixelSize);
        }

        private TilemapAnimation? ResolveAnimation(TmxTileset tileset, int tileId)
        {
            var tile = tileset.Tiles.FirstOrDefault(candidate => candidate.Id == tileId);
            if (tile?.Animation?.Frames is not { Count: > 0 } frames)
                return null;
            if (_animations.TryGetValue((tileset, tileId), out var cached))
                return cached;

            var animationFrames = new TilemapAnimationFrame[frames.Count];
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.DurationMilliseconds <= 0)
                    throw new TiledException(
                        $"Animated tile {tileId} in Tiled tileset '{tileset.Name}' has a non-positive frame duration.");
                var visual = ResolveVisual(tileset, frame.TileId);
                animationFrames[index] = new TilemapAnimationFrame(
                    visual.SourceRectangle,
                    frame.DurationMilliseconds,
                    visual.Texture);
            }
            var animation = new TilemapAnimation(animationFrames);
            _animations[(tileset, tileId)] = animation;
            return animation;
        }

        private Texture2D LoadTexture(TmxTileset tileset, TmxImage image)
        {
            var assetName = image.ResolvedAssetName;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                var source = Path.ChangeExtension(image.Source, null) ?? image.Source!;
                assetName = TmxResolver.ResolveRelativeAssetPath(tileset.AssetName, source);
                image.ResolvedAssetName = assetName;
            }
            if (_textures.TryGetValue(assetName, out var cached))
                return cached;
            var texture = _textureResolver(assetName)
                          ?? throw new TiledException(
                              $"Could not load Tiled tileset texture '{assetName}'.");
            _textures.Add(assetName, texture);
            return texture;
        }

        private static TileTransform ResolveTransform(TmxTileFlipFlags flags)
        {
            var horizontal = flags.HasFlag(TmxTileFlipFlags.Horizontal);
            var vertical = flags.HasFlag(TmxTileFlipFlags.Vertical);
            var diagonal = flags.HasFlag(TmxTileFlipFlags.Diagonal);
            if (!diagonal)
            {
                var effects = SpriteEffects.None;
                if (horizontal)
                    effects |= SpriteEffects.FlipHorizontally;
                if (vertical)
                    effects |= SpriteEffects.FlipVertically;
                return new TileTransform(0f, effects, false);
            }

            if (horizontal && vertical)
                return new TileTransform(MathHelper.PiOver2, SpriteEffects.FlipHorizontally, true);
            if (horizontal)
                return new TileTransform(MathHelper.PiOver2, SpriteEffects.None, true);
            if (vertical)
                return new TileTransform(-MathHelper.PiOver2, SpriteEffects.None, true);
            return new TileTransform(-MathHelper.PiOver2, SpriteEffects.FlipHorizontally, true);
        }

        private readonly record struct TilesetBinding(uint FirstGid, TmxTileset Tileset);
        private readonly record struct TileVisual(Texture2D Texture, Rectangle SourceRectangle, Vector2 PixelSize);
        private readonly record struct TileTransform(float Rotation, SpriteEffects Effects, bool QuarterTurn);
    }
}
