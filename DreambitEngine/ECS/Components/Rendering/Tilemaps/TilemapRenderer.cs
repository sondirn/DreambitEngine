#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.ECS;

/// <summary>
/// Draws generic Dreambit tilemap data. Occupied chunks are culled before their
/// tiles are visited, and static chunks can be cached when tiles become small
/// on screen. Importers remain responsible for preparing the layer data and
/// loading its textures.
/// </summary>
[BlueprintType(nameof(TilemapRenderer))]
public sealed class TilemapRenderer : DrawableComponent<TilemapRenderer>
{
    private const int MaximumCacheTextureDimension = 2048;
    private readonly List<TilemapChunkData> _visibleChunks = [];
    private readonly Dictionary<TilemapChunkData, CachedChunk> _chunkCaches = [];
    private long _cachedBytes;
    private ulong _diagnosticFrame = ulong.MaxValue;
    private ulong _visibleChunkFrame = ulong.MaxValue;
    private RectangleF _visibleChunkView;

    public Texture2D? Texture { get; private set; }
    public TilemapLayerData? Layer { get; private set; }

    [DreambitSerialize]
    public Color Tint { get; set; } = Color.White;

    /// <summary>Enables lazy render-target caches for static chunk contents.</summary>
    [DreambitSerialize]
    public bool EnableChunkCaching { get; set; } = true;

    /// <summary>
    /// Static chunks are cached when the layer's cells are no larger than this
    /// many screen pixels. Set to zero to use caches at every zoom level.
    /// </summary>
    [DreambitSerialize]
    public float ChunkCacheScreenSizeThreshold { get; set; } = 12f;

    /// <summary>Maximum number of cached chunks retained by this renderer.</summary>
    [DreambitSerialize]
    public int MaximumCachedChunks { get; set; } = 256;

    /// <summary>Approximate render-target memory budget for this renderer.</summary>
    [DreambitSerialize]
    public int MaximumChunkCacheMegabytes { get; set; } = 64;

    /// <summary>Maximum new chunk textures built in one frame.</summary>
    [DreambitSerialize]
    public int MaximumChunkCachesBuiltPerFrame { get; set; } = 4;

    /// <summary>Logical tiles submitted during the most recent render pass.</summary>
    public int LastVisibleTileCount { get; private set; }

    /// <summary>Occupied chunks intersecting the camera during the most recent render pass.</summary>
    public int LastVisibleChunkCount { get; private set; }

    /// <summary>Tiles considered after chunk culling during the most recent render pass.</summary>
    public int LastCandidateTileCount { get; private set; }

    /// <summary>SpriteBatch submissions made during the most recent render pass.</summary>
    public int LastSpriteSubmissionCount { get; private set; }

    /// <summary>SpriteBatch submissions accumulated across all render passes this frame.</summary>
    public int FrameSpriteSubmissionCount { get; private set; }

    public int CachedChunkCount => _chunkCaches.Count;
    public long CachedChunkBytes => _cachedBytes;

    /// <summary>Chunk caches invalidated by targeted runtime layer changes.</summary>
    public int ChunkCacheInvalidationCount { get; private set; }

    public override RectangleF Bounds
    {
        get
        {
            if (Layer is null || Transform is null)
                return RectangleF.Empty;

            return TransformBounds(Layer.Bounds, Transform.WorldMatrix);
        }
    }

    public TilemapRenderer Configure(Texture2D texture, TilemapLayerData layer)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ConfigureLayer(layer);
        Texture = texture;
        return this;
    }

    public TilemapRenderer Configure(TilemapLayerData layer)
    {
        ConfigureLayer(layer);
        Texture = null;
        return this;
    }

    /// <summary>
    /// Builds a bounded number of visible static caches before the render pass
    /// opens its main SpriteBatch.
    /// </summary>
    public override void OnPreDraw()
    {
        if (Layer is null || !ShouldUseChunkCaches())
            return;

        var worldMatrix = Transform.WorldMatrix;
        var localView = TransformBounds(Scene.MainCamera.BoundsF, Matrix.Invert(worldMatrix));
        UpdateVisibleChunks(localView);

        var frame = Time.FrameCount;
        for (var index = 0; index < _visibleChunks.Count; index++)
            if (_chunkCaches.TryGetValue(_visibleChunks[index], out var existing))
                existing.LastUsedFrame = frame;

        var remainingBuilds = Math.Max(0, MaximumChunkCachesBuiltPerFrame);
        if (remainingBuilds == 0 || MaximumCachedChunks <= 0 || MaximumChunkCacheMegabytes <= 0)
            return;

        var device = Core.Instance.GraphicsDevice;
        var previousTargets = device.GetRenderTargets();
        try
        {
            for (var index = 0; index < _visibleChunks.Count && remainingBuilds > 0; index++)
            {
                var chunk = _visibleChunks[index];
                if (chunk.StaticTiles.Count == 0)
                    continue;

                var layout = CreateCacheLayout(chunk.StaticTiles);
                if (_chunkCaches.TryGetValue(chunk, out var currentCache))
                {
                    if (currentCache.PixelsPerWorldUnit >= layout.PixelsPerWorldUnit)
                        continue;
                    RemoveCache(chunk, currentCache);
                }

                if (!MakeCacheRoom(layout.SizeInBytes))
                    continue;

                var cache = BuildChunkCache(device, chunk, layout);
                cache.LastUsedFrame = frame;
                _chunkCaches.Add(chunk, cache);
                _cachedBytes += cache.SizeInBytes;
                remainingBuilds--;
            }
        }
        finally
        {
            if (previousTargets.Length == 0)
                device.SetRenderTarget(null);
            else
                device.SetRenderTargets(previousTargets);
        }
    }

    protected override void OnDraw()
    {
        ArgumentNullException.ThrowIfNull(Layer);
        BeginDiagnostics();

        var cameraBounds = Scene.MainCamera.BoundsF;
        var worldMatrix = Transform.WorldMatrix;
        var localView = TransformBounds(cameraBounds, Matrix.Invert(worldMatrix));
        UpdateVisibleChunks(localView);
        LastVisibleChunkCount = _visibleChunks.Count;

        var useCaches = ShouldUseChunkCaches();
        var elapsedMilliseconds = Time.TimeSinceSceneLoaded * 1000f;
        for (var index = 0; index < _visibleChunks.Count; index++)
        {
            var chunk = _visibleChunks[index];
            LastCandidateTileCount += chunk.TileCount;

            if (useCaches && _chunkCaches.TryGetValue(chunk, out var cache))
            {
                cache.LastUsedFrame = Time.FrameCount;
                DrawCachedChunk(cache, worldMatrix);
                LastVisibleTileCount += chunk.StaticTiles.Count;
                LastSpriteSubmissionCount++;

                DrawTiles(
                    chunk.AnimatedTiles,
                    localView,
                    cameraBounds,
                    worldMatrix,
                    elapsedMilliseconds);
            }
            else
            {
                DrawTiles(
                    chunk.Tiles,
                    localView,
                    cameraBounds,
                    worldMatrix,
                    elapsedMilliseconds);
            }
        }

        FrameSpriteSubmissionCount += LastSpriteSubmissionCount;
    }

    public override void OnDestroyed()
    {
        if (Layer is not null)
            Layer.ChunkChanged -= OnLayerChunkChanged;
        DisposeChunkCaches();
        Texture = null;
        Layer = null;
        _visibleChunks.Clear();
        _visibleChunkFrame = ulong.MaxValue;
        LastVisibleTileCount = 0;
        LastVisibleChunkCount = 0;
        LastCandidateTileCount = 0;
        LastSpriteSubmissionCount = 0;
        FrameSpriteSubmissionCount = 0;
        ChunkCacheInvalidationCount = 0;
    }

    private void ConfigureLayer(TilemapLayerData layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (Layer is not null)
            Layer.ChunkChanged -= OnLayerChunkChanged;
        DisposeChunkCaches();
        Layer = layer;
        Layer.ChunkChanged += OnLayerChunkChanged;
        _visibleChunkFrame = ulong.MaxValue;
    }

    private void OnLayerChunkChanged(object? sender, TilemapChunkChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, Layer))
            return;

        if (args.PreviousChunk is { } previous &&
            _chunkCaches.TryGetValue(previous, out var cache))
        {
            RemoveCache(previous, cache);
            ChunkCacheInvalidationCount++;
        }

        // The visible set may contain the replaced chunk object. Force it to be
        // repopulated before either cache building or drawing visits the layer.
        _visibleChunkFrame = ulong.MaxValue;
    }

    private void BeginDiagnostics()
    {
        if (_diagnosticFrame != Time.FrameCount)
        {
            _diagnosticFrame = Time.FrameCount;
            FrameSpriteSubmissionCount = 0;
        }

        LastVisibleTileCount = 0;
        LastVisibleChunkCount = 0;
        LastCandidateTileCount = 0;
        LastSpriteSubmissionCount = 0;
    }

    private bool ShouldUseChunkCaches()
    {
        if (!EnableChunkCaching || Layer is null || UsesEffect)
            return false;
        if (!float.IsFinite(ChunkCacheScreenSizeThreshold) || ChunkCacheScreenSizeThreshold < 0f)
            return false;

        var worldScale = Transform.WorldScale2D;
        var cellScreenSize = MathF.Max(
            Layer.CellSize.X * MathF.Abs(worldScale.X),
            Layer.CellSize.Y * MathF.Abs(worldScale.Y)) * Scene.MainCamera.Scale;
        return ChunkCacheScreenSizeThreshold == 0f || cellScreenSize <= ChunkCacheScreenSizeThreshold;
    }

    private void UpdateVisibleChunks(RectangleF localView)
    {
        if (_visibleChunkFrame == Time.FrameCount && _visibleChunkView == localView)
            return;

        Layer!.GetVisibleChunks(localView, _visibleChunks);
        _visibleChunkView = localView;
        _visibleChunkFrame = Time.FrameCount;
    }

    private void DrawTiles(
        IReadOnlyList<TilemapTile> tiles,
        RectangleF localView,
        RectangleF cameraBounds,
        Matrix worldMatrix,
        float elapsedMilliseconds)
    {
        var requirePreciseWorldCull =
            MathF.Abs(Transform.WorldRotation2D) > Mathf.Epsilon;

        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            if (!localView.Intersects(tile.Bounds))
                continue;
            if (requirePreciseWorldCull &&
                !cameraBounds.Intersects(TransformBounds(tile.Bounds, worldMatrix)))
                continue;

            DrawTile(tile, worldMatrix, elapsedMilliseconds);
            LastVisibleTileCount++;
            LastSpriteSubmissionCount++;
        }
    }

    private void DrawTile(TilemapTile tile, Matrix worldMatrix, float elapsedMilliseconds)
    {
        var frame = tile.Animation?.GetFrame(elapsedMilliseconds);
        var sourceRectangle = frame?.SourceRectangle ?? tile.SourceRectangle;
        var texture = frame?.Texture ?? ResolveTexture(tile);
        var scale = GetTileScale(tile, sourceRectangle) * Transform.WorldScale2D;

        Core.SpriteBatch.DrawWorldSprite(
            texture,
            Vector2.Transform(tile.Position + tile.Size * 0.5f, worldMatrix),
            sourceRectangle,
            tile.Tint * Tint,
            Transform.WorldRotation2D + tile.Rotation,
            new Vector2(sourceRectangle.Width * 0.5f, sourceRectangle.Height * 0.5f),
            scale,
            tile.Effects);
    }

    private void DrawCachedChunk(CachedChunk cache, Matrix worldMatrix)
    {
        var bounds = cache.Bounds;
        Core.SpriteBatch.DrawWorldSprite(
            cache.Texture,
            Vector2.Transform(new Vector2(bounds.Center.X, bounds.Center.Y), worldMatrix),
            null,
            Tint,
            Transform.WorldRotation2D,
            new Vector2(cache.Texture.Width * 0.5f, cache.Texture.Height * 0.5f),
            new Vector2(
                bounds.Width / cache.Texture.Width,
                bounds.Height / cache.Texture.Height) * Transform.WorldScale2D);
    }

    private CacheLayout CreateCacheLayout(IReadOnlyList<TilemapTile> tiles)
    {
        var bounds = GetBounds(tiles);
        var sourcePixelsPerWorldUnit = GetSourcePixelsPerWorldUnit(tiles);
        var worldScale = Transform.WorldScale2D;
        var screenPixelsPerLocalWorldUnit = Scene.MainCamera.Scale * MathF.Max(
            MathF.Abs(worldScale.X),
            MathF.Abs(worldScale.Y));
        var minimumDensity = 1f / MathF.Max(Layer!.CellSize.X, Layer.CellSize.Y);
        var desiredDensity = MathF.Max(minimumDensity, screenPixelsPerLocalWorldUnit);
        var quantizedDensity = MathF.Pow(2f, MathF.Ceiling(MathF.Log2(desiredDensity)));
        var pixelsPerWorldUnit = MathF.Min(sourcePixelsPerWorldUnit, quantizedDensity);
        var targetWidth = Math.Max(1, (int)MathF.Ceiling(bounds.Width * pixelsPerWorldUnit));
        var targetHeight = Math.Max(1, (int)MathF.Ceiling(bounds.Height * pixelsPerWorldUnit));
        var reduction = MathF.Min(
            1f,
            MathF.Min(
                MaximumCacheTextureDimension / (float)targetWidth,
                MaximumCacheTextureDimension / (float)targetHeight));
        targetWidth = Math.Max(1, (int)MathF.Ceiling(targetWidth * reduction));
        targetHeight = Math.Max(1, (int)MathF.Ceiling(targetHeight * reduction));
        pixelsPerWorldUnit = MathF.Min(
            targetWidth / bounds.Width,
            targetHeight / bounds.Height);
        return new CacheLayout(bounds, targetWidth, targetHeight, pixelsPerWorldUnit);
    }

    private CachedChunk BuildChunkCache(
        GraphicsDevice device,
        TilemapChunkData chunk,
        CacheLayout layout)
    {
        var bounds = layout.Bounds;

        var target = new RenderTarget2D(
            device,
            layout.Width,
            layout.Height,
            false,
            SurfaceFormat.Color,
            DepthFormat.None,
            0,
            RenderTargetUsage.PreserveContents);

        try
        {
            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);
            var cacheMatrix =
                Matrix.CreateTranslation(-bounds.X, -bounds.Y, 0f) *
                Matrix.CreateScale(layout.Width / bounds.Width, layout.Height / bounds.Height, 1f);

            Core.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                Scene.RenderingOptions.SamplerState,
                DepthStencilState.None,
                RasterizerState.CullNone,
                transformMatrix: cacheMatrix);
            try
            {
                for (var index = 0; index < chunk.StaticTiles.Count; index++)
                {
                    var tile = chunk.StaticTiles[index];
                    var sourceRectangle = tile.SourceRectangle;
                    Core.SpriteBatch.DrawWorldSprite(
                        ResolveTexture(tile),
                        tile.Position + tile.Size * 0.5f,
                        sourceRectangle,
                        tile.Tint,
                        tile.Rotation,
                        new Vector2(sourceRectangle.Width * 0.5f, sourceRectangle.Height * 0.5f),
                        GetTileScale(tile, sourceRectangle),
                        tile.Effects);
                }
            }
            finally
            {
                Core.SpriteBatch.End();
            }

            return new CachedChunk(target, bounds, layout.PixelsPerWorldUnit);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private Texture2D ResolveTexture(TilemapTile tile)
        => tile.Texture ?? Texture
           ?? throw new InvalidOperationException(
               "A tilemap tile has no texture and the renderer has no fallback texture.");

    private static Vector2 GetTileScale(TilemapTile tile, Rectangle sourceRectangle)
    {
        var quarterTurn = MathF.Abs(MathF.Sin(tile.Rotation)) > 0.5f;
        return quarterTurn
            ? new Vector2(
                tile.Size.Y / sourceRectangle.Width,
                tile.Size.X / sourceRectangle.Height)
            : new Vector2(
                tile.Size.X / sourceRectangle.Width,
                tile.Size.Y / sourceRectangle.Height);
    }

    private static RectangleF GetBounds(IReadOnlyList<TilemapTile> tiles)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        for (var index = 0; index < tiles.Count; index++)
        {
            var bounds = tiles[index].Bounds;
            left = MathF.Min(left, bounds.Left);
            top = MathF.Min(top, bounds.Top);
            right = MathF.Max(right, bounds.Right);
            bottom = MathF.Max(bottom, bounds.Bottom);
        }
        return new RectangleF(left, top, right - left, bottom - top);
    }

    private static float GetSourcePixelsPerWorldUnit(IReadOnlyList<TilemapTile> tiles)
    {
        var result = 1f;
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var source = tile.SourceRectangle;
            var quarterTurn = MathF.Abs(MathF.Sin(tile.Rotation)) > 0.5f;
            result = quarterTurn
                ? MathF.Max(result, MathF.Max(source.Height / tile.Size.X, source.Width / tile.Size.Y))
                : MathF.Max(result, MathF.Max(source.Width / tile.Size.X, source.Height / tile.Size.Y));
        }
        return result;
    }

    private bool MakeCacheRoom(long requiredBytes)
    {
        var countLimit = Math.Max(0, MaximumCachedChunks);
        var byteLimit = Math.Max(0L, MaximumChunkCacheMegabytes) * 1024L * 1024L;
        if (countLimit == 0 || requiredBytes > byteLimit)
            return false;

        while ((_chunkCaches.Count >= countLimit || _cachedBytes + requiredBytes > byteLimit) &&
               _chunkCaches.Count > 0)
        {
            TilemapChunkData? oldestChunk = null;
            CachedChunk? oldestCache = null;
            foreach (var pair in _chunkCaches)
            {
                // If every cache is visible, keep a stable working set instead
                // of rebuilding and evicting the same chunks every frame.
                if (pair.Value.LastUsedFrame == Time.FrameCount)
                    continue;
                if (oldestCache is null || pair.Value.LastUsedFrame < oldestCache.LastUsedFrame)
                {
                    oldestChunk = pair.Key;
                    oldestCache = pair.Value;
                }
            }

            if (oldestChunk is null || oldestCache is null)
                return false;
            RemoveCache(oldestChunk, oldestCache);
        }

        return _chunkCaches.Count < countLimit && _cachedBytes + requiredBytes <= byteLimit;
    }

    private void RemoveCache(TilemapChunkData chunk, CachedChunk cache)
    {
        cache.Dispose();
        _chunkCaches.Remove(chunk);
        _cachedBytes -= cache.SizeInBytes;
    }

    private void DisposeChunkCaches()
    {
        foreach (var cache in _chunkCaches.Values)
            cache.Dispose();
        _chunkCaches.Clear();
        _cachedBytes = 0;
    }

    private static RectangleF TransformBounds(RectangleF bounds, Matrix matrix)
    {
        var topLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Top), matrix);
        var topRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Top), matrix);
        var bottomLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Bottom), matrix);
        var bottomRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), matrix);
        var minimumX = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        var minimumY = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        var maximumX = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        var maximumY = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return new RectangleF(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
    }

    private readonly record struct CacheLayout(
        RectangleF Bounds,
        int Width,
        int Height,
        float PixelsPerWorldUnit)
    {
        public long SizeInBytes => (long)Width * Height * 4L;
    }

    private sealed class CachedChunk(
        RenderTarget2D texture,
        RectangleF bounds,
        float pixelsPerWorldUnit) : IDisposable
    {
        public RenderTarget2D Texture { get; } = texture;
        public RectangleF Bounds { get; } = bounds;
        public float PixelsPerWorldUnit { get; } = pixelsPerWorldUnit;
        public long SizeInBytes => (long)Texture.Width * Texture.Height * 4L;
        public ulong LastUsedFrame { get; set; }

        public void Dispose() => Texture.Dispose();
    }
}
