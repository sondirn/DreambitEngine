#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public enum TilemapRenderOrder
{
    RightDown,
    RightUp,
    LeftDown,
    LeftUp
}

public readonly record struct TilemapAnimationFrame(
    Rectangle SourceRectangle,
    int DurationMilliseconds,
    Texture2D? Texture = null);

public sealed class TilemapAnimation
{
    private readonly TilemapAnimationFrame[] _frames;

    public TilemapAnimation(IEnumerable<TilemapAnimationFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = [.. frames];
        if (_frames.Length == 0)
            throw new ArgumentException("A tile animation must contain at least one frame.", nameof(frames));

        var totalDuration = 0;
        foreach (var frame in _frames)
        {
            if (frame.SourceRectangle.Width <= 0 || frame.SourceRectangle.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(frames), "Tile animation frames need positive source rectangles.");
            if (frame.DurationMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(frames), "Tile animation frame durations must be positive.");
            totalDuration = checked(totalDuration + frame.DurationMilliseconds);
        }

        TotalDurationMilliseconds = totalDuration;
    }

    public IReadOnlyList<TilemapAnimationFrame> Frames => _frames;
    public int TotalDurationMilliseconds { get; }

    public TilemapAnimationFrame GetFrame(float elapsedMilliseconds)
    {
        if (!float.IsFinite(elapsedMilliseconds))
            elapsedMilliseconds = 0f;
        var playhead = elapsedMilliseconds % TotalDurationMilliseconds;
        if (playhead < 0f)
            playhead += TotalDurationMilliseconds;

        foreach (var frame in _frames)
        {
            if (playhead < frame.DurationMilliseconds)
                return frame;
            playhead -= frame.DurationMilliseconds;
        }

        return _frames[^1];
    }
}

/// <summary>
/// One tile prepared for Dreambit's renderer. Positions and sizes are expressed
/// in world units and are local to the entity that owns the tilemap renderer.
/// </summary>
public readonly record struct TilemapTile(
    Vector2 Position,
    Vector2 Size,
    Rectangle SourceRectangle,
    Color Tint,
    SpriteEffects Effects = SpriteEffects.None,
    Texture2D? Texture = null,
    float Rotation = 0f,
    TilemapAnimation? Animation = null,
    Point? Cell = null,
    Point? Chunk = null)
{
    public RectangleF Bounds => new(Position.X, Position.Y, Size.X, Size.Y);
}

/// <summary>
/// An occupied spatial chunk in a tile layer. Chunks are the unit used for
/// camera culling and optional static rendering caches.
/// </summary>
public sealed class TilemapChunkData
{
    internal TilemapChunkData(
        Point coordinate,
        RectangleF bounds,
        TilemapTile[] tiles,
        TilemapTile[] staticTiles,
        TilemapTile[] animatedTiles)
    {
        Coordinate = coordinate;
        Bounds = bounds;
        Tiles = tiles;
        StaticTiles = staticTiles;
        AnimatedTiles = animatedTiles;
    }

    public Point Coordinate { get; }
    public RectangleF Bounds { get; }
    public IReadOnlyList<TilemapTile> Tiles { get; }
    public IReadOnlyList<TilemapTile> StaticTiles { get; }
    public IReadOnlyList<TilemapTile> AnimatedTiles { get; }
    public int TileCount => Tiles.Count;
}

internal sealed class TilemapChunkChangedEventArgs(
    Point coordinate,
    TilemapChunkData? previousChunk,
    TilemapChunkData? currentChunk) : EventArgs
{
    public Point Coordinate { get; } = coordinate;
    public TilemapChunkData? PreviousChunk { get; } = previousChunk;
    public TilemapChunkData? CurrentChunk { get; } = currentChunk;
}

/// <summary>
/// Renderer-ready tile layer data with a fixed spatial grid. The initial data
/// remains optimized and immutable to callers, while an importer-owned runtime
/// layer can replace individual chunks without rebuilding unrelated chunks.
/// </summary>
public sealed class TilemapLayerData
{
    internal const int DefaultChunkSizeInCells = 32;
    private static readonly IReadOnlyList<TilemapTile> EmptyCell = Array.Empty<TilemapTile>();
    private readonly Dictionary<Point, List<TilemapTile>> _tilesByCell = [];
    private readonly Dictionary<Point, TilemapChunkData> _chunksByCoordinate = [];
    private readonly List<TilemapChunkData> _chunks = [];
    private readonly bool _allowCellsOutsideGrid;
    private TilemapTile[]? _allTiles;
    private int _tileCount;

    public TilemapLayerData(
        int columns,
        int rows,
        Vector2 cellSize,
        IEnumerable<TilemapTile> tiles,
        TilemapRenderOrder renderOrder = TilemapRenderOrder.RightDown)
        : this(columns, rows, cellSize, tiles, renderOrder, allowCellsOutsideGrid: false)
    {
    }

    internal TilemapLayerData(
        int columns,
        int rows,
        Vector2 cellSize,
        IEnumerable<TilemapTile> tiles,
        TilemapRenderOrder renderOrder,
        bool allowCellsOutsideGrid)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentNullException.ThrowIfNull(tiles);

        if (!float.IsFinite(cellSize.X) || cellSize.X <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell width must be positive and finite.");
        if (!float.IsFinite(cellSize.Y) || cellSize.Y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell height must be positive and finite.");

        Columns = columns;
        Rows = rows;
        CellSize = cellSize;
        RenderOrder = renderOrder;
        _allowCellsOutsideGrid = allowCellsOutsideGrid;

        var chunkBuilders = new Dictionary<Point, ChunkBuilder>();
        foreach (var tile in tiles)
        {
            ValidateTile(tile);
            var cell = ResolveCell(tile, cellSize);
            ValidateCell(cell, nameof(tiles));
            var chunkCoordinate = tile.Chunk ?? GetDefaultChunkCoordinate(cell);
            if (!chunkBuilders.TryGetValue(chunkCoordinate, out var builder))
            {
                builder = new ChunkBuilder(chunkCoordinate);
                chunkBuilders.Add(chunkCoordinate, builder);
            }
            builder.Add(tile);
        }

        foreach (var builder in chunkBuilders.Values)
            AddChunk(builder.Build(renderOrder));
        _chunks.Sort(new ChunkRenderOrderComparer(renderOrder));
        RecalculateInitialMetrics();
    }

    public int Columns { get; }
    public int Rows { get; }
    public Vector2 CellSize { get; }
    public RectangleF Bounds { get; private set; }
    public IReadOnlyList<TilemapTile> Tiles => _allTiles ??= _chunks.SelectMany(chunk => chunk.Tiles).ToArray();
    public Vector2 MaximumTileSize { get; private set; }
    public Vector2 MinimumTileOffset { get; private set; }
    public Vector2 MaximumTileExtent { get; private set; }
    public TilemapRenderOrder RenderOrder { get; }
    public int TileCount => _tileCount;
    public IReadOnlyList<TilemapChunkData> Chunks => _chunks;
    public int ChunkCount => _chunks.Count;

    internal event EventHandler<TilemapChunkChangedEventArgs>? ChunkChanged;

    /// <summary>
    /// Appends occupied chunks intersecting <paramref name="localView"/> in the
    /// layer's render order. Empty grid space is never visited.
    /// </summary>
    public void GetVisibleChunks(RectangleF localView, List<TilemapChunkData> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (!Bounds.Intersects(localView) || TileCount == 0)
            return;

        for (var index = 0; index < _chunks.Count; index++)
        {
            var chunk = _chunks[index];
            if (chunk.Bounds.Intersects(localView))
                destination.Add(chunk);
        }
    }

    public IReadOnlyList<TilemapTile> GetTiles(int column, int row)
    {
        var cell = new Point(column, row);
        ValidateCell(cell, column < 0 || column >= Columns ? nameof(column) : nameof(row));
        return _tilesByCell.TryGetValue(cell, out var tiles) ? tiles : EmptyCell;
    }

    public bool TryGetVisibleCellRange(
        RectangleF localView,
        out int minimumColumn,
        out int minimumRow,
        out int maximumColumn,
        out int maximumRow)
    {
        if (!Bounds.Intersects(localView) || TileCount == 0)
        {
            minimumColumn = minimumRow = maximumColumn = maximumRow = 0;
            return false;
        }

        minimumColumn = (int)MathF.Floor((localView.Left - MaximumTileExtent.X) / CellSize.X);
        minimumRow = (int)MathF.Floor((localView.Top - MaximumTileExtent.Y) / CellSize.Y);
        maximumColumn = (int)MathF.Floor((localView.Right - MinimumTileOffset.X) / CellSize.X);
        maximumRow = (int)MathF.Floor((localView.Bottom - MinimumTileOffset.Y) / CellSize.Y);
        if (!_allowCellsOutsideGrid)
        {
            minimumColumn = Math.Clamp(minimumColumn, 0, Columns - 1);
            minimumRow = Math.Clamp(minimumRow, 0, Rows - 1);
            maximumColumn = Math.Clamp(maximumColumn, 0, Columns - 1);
            maximumRow = Math.Clamp(maximumRow, 0, Rows - 1);
        }
        return true;
    }

    internal void ReplaceChunk(Point coordinate, IEnumerable<TilemapTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        _chunksByCoordinate.TryGetValue(coordinate, out var previous);
        if (previous is not null)
            RemoveChunk(previous);

        var builder = new ChunkBuilder(coordinate);
        foreach (var tile in tiles)
        {
            ValidateTile(tile);
            var cell = ResolveCell(tile, CellSize);
            ValidateCell(cell, nameof(tiles));
            if (tile.Chunk is { } declaredChunk && declaredChunk != coordinate)
                throw new ArgumentException(
                    $"Tile at cell {cell} declares chunk {declaredChunk} but is replacing chunk {coordinate}.",
                    nameof(tiles));
            builder.Add(tile with { Chunk = coordinate });
        }

        TilemapChunkData? current = null;
        if (builder.Count > 0)
        {
            current = builder.Build(RenderOrder);
            AddChunk(current);
            _chunks.Sort(new ChunkRenderOrderComparer(RenderOrder));
            ExpandMetrics(current);
        }
        else if (_chunks.Count == 0)
        {
            ResetMetrics();
        }

        _allTiles = null;
        ChunkChanged?.Invoke(this, new TilemapChunkChangedEventArgs(coordinate, previous, current));
    }

    internal static Point GetDefaultChunkCoordinate(Point cell) => new(
        FloorDivide(cell.X, DefaultChunkSizeInCells),
        FloorDivide(cell.Y, DefaultChunkSizeInCells));

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private void AddChunk(TilemapChunkData chunk)
    {
        _chunksByCoordinate.Add(chunk.Coordinate, chunk);
        _chunks.Add(chunk);
        _tileCount += chunk.TileCount;
        foreach (var tile in chunk.Tiles)
        {
            var cell = ResolveCell(tile, CellSize);
            if (!_tilesByCell.TryGetValue(cell, out var cellTiles))
            {
                cellTiles = [];
                _tilesByCell.Add(cell, cellTiles);
            }
            cellTiles.Add(tile);
        }
    }

    private void RemoveChunk(TilemapChunkData chunk)
    {
        _chunksByCoordinate.Remove(chunk.Coordinate);
        _chunks.Remove(chunk);
        _tileCount -= chunk.TileCount;
        foreach (var tile in chunk.Tiles)
        {
            var cell = ResolveCell(tile, CellSize);
            if (!_tilesByCell.TryGetValue(cell, out var cellTiles))
                continue;
            cellTiles.Remove(tile);
            if (cellTiles.Count == 0)
                _tilesByCell.Remove(cell);
        }
    }

    private void RecalculateInitialMetrics()
    {
        ResetMetrics();
        for (var index = 0; index < _chunks.Count; index++)
            ExpandMetrics(_chunks[index]);
        _allTiles = _chunks.SelectMany(chunk => chunk.Tiles).ToArray();
    }

    private void ResetMetrics()
    {
        Bounds = RectangleF.Empty;
        MaximumTileSize = Vector2.Zero;
        MinimumTileOffset = Vector2.Zero;
        MaximumTileExtent = CellSize;
    }

    // Metrics intentionally remain conservative when a boundary tile is removed.
    // This avoids scanning or rebuilding unrelated chunks in a mutation hot path;
    // visible chunks themselves are still culled precisely by their own bounds.
    private void ExpandMetrics(TilemapChunkData chunk)
    {
        Bounds = Bounds == RectangleF.Empty
            ? chunk.Bounds
            : Union(Bounds, chunk.Bounds);

        foreach (var tile in chunk.Tiles)
        {
            MaximumTileSize = new Vector2(
                MathF.Max(MaximumTileSize.X, tile.Size.X),
                MathF.Max(MaximumTileSize.Y, tile.Size.Y));
            var cell = ResolveCell(tile, CellSize);
            var cellOrigin = new Vector2(cell.X * CellSize.X, cell.Y * CellSize.Y);
            MinimumTileOffset = new Vector2(
                MathF.Min(MinimumTileOffset.X, tile.Position.X - cellOrigin.X),
                MathF.Min(MinimumTileOffset.Y, tile.Position.Y - cellOrigin.Y));
            MaximumTileExtent = new Vector2(
                MathF.Max(MaximumTileExtent.X, tile.Position.X + tile.Size.X - cellOrigin.X),
                MathF.Max(MaximumTileExtent.Y, tile.Position.Y + tile.Size.Y - cellOrigin.Y));
        }
    }

    private static RectangleF Union(RectangleF left, RectangleF right)
    {
        var minimumX = MathF.Min(left.Left, right.Left);
        var minimumY = MathF.Min(left.Top, right.Top);
        var maximumX = MathF.Max(left.Right, right.Right);
        var maximumY = MathF.Max(left.Bottom, right.Bottom);
        return new RectangleF(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
    }

    private void ValidateCell(Point cell, string parameterName)
    {
        if (_allowCellsOutsideGrid)
            return;
        if (cell.X < 0 || cell.X >= Columns || cell.Y < 0 || cell.Y >= Rows)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Cell ({cell.X}, {cell.Y}) is outside the {Columns}x{Rows} tilemap grid.");
    }

    private static Point ResolveCell(TilemapTile tile, Vector2 cellSize) => tile.Cell ?? new Point(
        (int)MathF.Floor(tile.Position.X / cellSize.X),
        (int)MathF.Floor(tile.Position.Y / cellSize.Y));

    private static int CompareCells(TilemapTile left, TilemapTile right, TilemapRenderOrder renderOrder)
    {
        var leftCell = left.Cell ?? Point.Zero;
        var rightCell = right.Cell ?? Point.Zero;
        var rowComparison = leftCell.Y.CompareTo(rightCell.Y);
        var columnComparison = leftCell.X.CompareTo(rightCell.X);

        return renderOrder switch
        {
            TilemapRenderOrder.RightDown => rowComparison != 0 ? rowComparison : columnComparison,
            TilemapRenderOrder.RightUp => rowComparison != 0 ? -rowComparison : columnComparison,
            TilemapRenderOrder.LeftDown => rowComparison != 0 ? rowComparison : -columnComparison,
            TilemapRenderOrder.LeftUp => rowComparison != 0 ? -rowComparison : -columnComparison,
            _ => throw new ArgumentOutOfRangeException(nameof(renderOrder))
        };
    }

    private static void ValidateTile(TilemapTile tile)
    {
        if (!float.IsFinite(tile.Position.X) || !float.IsFinite(tile.Position.Y))
            throw new ArgumentOutOfRangeException(nameof(tile), "Tile position must be finite.");
        if (!float.IsFinite(tile.Size.X) || tile.Size.X <= 0f ||
            !float.IsFinite(tile.Size.Y) || tile.Size.Y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tile), "Tile size must be positive and finite.");
        if (tile.SourceRectangle.Width <= 0 || tile.SourceRectangle.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(tile), "Tile source rectangle must have a positive size.");
        if (!float.IsFinite(tile.Rotation))
            throw new ArgumentOutOfRangeException(nameof(tile), "Tile rotation must be finite.");
    }

    private sealed class ChunkBuilder(Point coordinate)
    {
        private readonly List<TilemapTile> _tiles = [];
        private float _left = float.PositiveInfinity;
        private float _top = float.PositiveInfinity;
        private float _right = float.NegativeInfinity;
        private float _bottom = float.NegativeInfinity;

        public int Count => _tiles.Count;

        public void Add(TilemapTile tile)
        {
            _tiles.Add(tile);
            _left = MathF.Min(_left, tile.Bounds.Left);
            _top = MathF.Min(_top, tile.Bounds.Top);
            _right = MathF.Max(_right, tile.Bounds.Right);
            _bottom = MathF.Max(_bottom, tile.Bounds.Bottom);
        }

        public TilemapChunkData Build(TilemapRenderOrder renderOrder)
        {
            var ordered = _tiles
                .Select((tile, index) => (tile, index))
                .OrderBy(item => item.tile, new TileRenderOrderComparer(renderOrder))
                .ThenBy(item => item.index)
                .Select(item => item.tile)
                .ToArray();
            return new TilemapChunkData(
                coordinate,
                new RectangleF(_left, _top, _right - _left, _bottom - _top),
                ordered,
                ordered.Where(tile => tile.Animation is null).ToArray(),
                ordered.Where(tile => tile.Animation is not null).ToArray());
        }
    }

    private sealed class TileRenderOrderComparer(TilemapRenderOrder renderOrder) : IComparer<TilemapTile>
    {
        public int Compare(TilemapTile left, TilemapTile right) => CompareCells(left, right, renderOrder);
    }

    private sealed class ChunkRenderOrderComparer(TilemapRenderOrder renderOrder) : IComparer<TilemapChunkData>
    {
        public int Compare(TilemapChunkData? left, TilemapChunkData? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var rowComparison = left.Bounds.Top.CompareTo(right.Bounds.Top);
            var columnComparison = left.Bounds.Left.CompareTo(right.Bounds.Left);
            return renderOrder switch
            {
                TilemapRenderOrder.RightDown => rowComparison != 0 ? rowComparison : columnComparison,
                TilemapRenderOrder.RightUp => rowComparison != 0 ? -rowComparison : columnComparison,
                TilemapRenderOrder.LeftDown => rowComparison != 0 ? rowComparison : -columnComparison,
                TilemapRenderOrder.LeftUp => rowComparison != 0 ? -rowComparison : -columnComparison,
                _ => throw new ArgumentOutOfRangeException(nameof(renderOrder))
            };
        }
    }
}
