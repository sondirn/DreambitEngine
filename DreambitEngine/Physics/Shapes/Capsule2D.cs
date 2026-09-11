using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>
/// A capsule represented by a center line segment plus a radius.
/// Start == End degenerates cleanly into a circle.
/// </summary>
public readonly record struct Capsule2D(
    Vector2 Start,
    Vector2 End,
    float Radius)
{
    public bool IsValid =>
        float.IsFinite(Start.X) &&
        float.IsFinite(Start.Y) &&
        float.IsFinite(End.X) &&
        float.IsFinite(End.Y) &&
        float.IsFinite(Radius) &&
        Radius >= 0f;

    public AABB ComputeAabb()
    {
        var extent = new Vector2(Radius);

        var minimum = new Vector2(
            Mathf.Min(Start.X, End.X),
            Mathf.Min(Start.Y, End.Y));

        var maximum = new Vector2(
            Mathf.Max(Start.X, End.X),
            Mathf.Max(Start.Y, End.Y));

        return new AABB
        {
            Min = minimum - extent,
            Max = maximum + extent
        };
    }
}