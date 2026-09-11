using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>
/// World- or local-space circle geometry.
/// </summary>
public readonly record struct Circle2D(
    Vector2 Center,
    float Radius)
{
    public bool IsValid =>
        float.IsFinite(Center.X) &&
        float.IsFinite(Center.Y) &&
        float.IsFinite(Radius) &&
        Radius >= 0f;

    public AABB ComputeAabb()
    {
        var extent = new Vector2(Radius);

        return new AABB
        {
            Min = Center - extent,
            Max = Center + extent
        };
    }
}