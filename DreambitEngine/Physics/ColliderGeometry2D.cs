namespace Dreambit;

public enum ColliderGeometryKind : byte
{
    None = 0,
    Polygon = 1,
    Circle = 2,
    Capsule = 3
}

/// <summary>
/// Cached world-space collision geometry.
///
/// Only the member corresponding to Kind should be consumed.
/// </summary>
public readonly struct ColliderGeometry2D
{
    private ColliderGeometry2D(
        ColliderGeometryKind kind,
        Polygon2D polygon,
        Circle2D circle,
        Capsule2D capsule,
        AABB aabb)
    {
        Kind = kind;
        Polygon = polygon;
        Circle = circle;
        Capsule = capsule;
        Aabb = aabb;
    }

    public ColliderGeometryKind Kind { get; }

    public Polygon2D Polygon { get; }

    public Circle2D Circle { get; }

    public Capsule2D Capsule { get; }

    public AABB Aabb { get; }

    public bool IsValid =>
        Kind != ColliderGeometryKind.None;

    public static ColliderGeometry2D FromPolygon(
        Polygon2D polygon)
    {
        if (polygon.Vertices is not { Length: >= 3 })
            return default;

        return new ColliderGeometry2D(
            ColliderGeometryKind.Polygon,
            polygon,
            default,
            default,
            polygon.ComputeAabb());
    }

    public static ColliderGeometry2D FromCircle(
        Circle2D circle)
    {
        if (!circle.IsValid)
            return default;

        return new ColliderGeometry2D(
            ColliderGeometryKind.Circle,
            default,
            circle,
            default,
            circle.ComputeAabb());
    }

    public static ColliderGeometry2D FromCapsule(
        Capsule2D capsule)
    {
        if (!capsule.IsValid)
            return default;

        return new ColliderGeometry2D(
            ColliderGeometryKind.Capsule,
            default,
            default,
            capsule,
            capsule.ComputeAabb());
    }
}