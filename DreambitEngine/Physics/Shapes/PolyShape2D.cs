using System;
using Microsoft.Xna.Framework;

namespace Dreambit;

public class PolyShape2D : Shape2D
{
    private PolyShape2D(Vector2[] points)
        : base(points.Length)
    {
        Polygon2D.Vertices = points;
    }

    public static PolyShape2D Create(Vector2[] points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Length < 3)
        {
            throw new ArgumentException(
                "A polygon required at least three vertices.", nameof(points));
        }

        var vertices = new Vector2[points.Length];

        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];

            if (!float.IsFinite(point.X) ||
                !float.IsFinite(point.Y))
            {
                throw new ArgumentException(
                    $"Polygon vertex {index} contains a non-finite coordinate.",
                    nameof(points));
            }

            vertices[index] = point;
        }

        return new PolyShape2D(vertices);
    }
}