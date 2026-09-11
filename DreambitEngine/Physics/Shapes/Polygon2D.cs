using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit;

public struct Polygon2D
{
    public Vector2[] Vertices;
    public int Length => Vertices.Length;

    private const float EPS = 1e-5f;

    /// <summary>Ensure polygon is CCW-wound. Reverses in-place if CW.</summary>
    public void NormalizeWindingCCW()
    {
        if (Vertices == null || Vertices.Length < 3) return;
        var area = 0f;
        for (var i = 0; i < Vertices.Length; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % Vertices.Length];
            area += a.X * b.Y - b.X * a.Y;
        }

        if (area < 0f) Array.Reverse(Vertices);
    }

    /// <summary>Remove duplicate adjacent points and collinear triplets; keeps polygon simple.</summary>
    public void CleanAndNormalize()
    {
        if (Vertices == null) return;
        // Remove duplicates
        var list = new List<Vector2>(Vertices.Length);
        Vector2? prev = null;
        foreach (var v in Vertices)
        {
            if (prev.HasValue && Vector2.DistanceSquared(prev.Value, v) < EPS * EPS) continue;
            list.Add(v);
            prev = v;
        }

        if (list.Count >= 2 && Vector2.DistanceSquared(list[0], list[^1]) < EPS * EPS)
            list.RemoveAt(list.Count - 1);
        // Remove collinear
        if (list.Count >= 3)
        {
            var cleaned = new List<Vector2>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[(i - 1 + list.Count) % list.Count];
                var b = list[i];
                var c = list[(i + 1) % list.Count];
                var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(cross) > EPS) cleaned.Add(b);
            }

            if (cleaned.Count >= 3) list = cleaned;
        }

        Vertices = list.ToArray();
        NormalizeWindingCCW();
    }

    /// <summary>Robust SAT that also outputs MTV axis and depth.</summary>
    public bool IntersectsSAT(Polygon2D other, ref Vector2 mtvAxis, ref float mtvDepth)
    {
        mtvDepth = float.MaxValue;
        mtvAxis = Vector2.Zero;

        if (Vertices is not { Length: >= 3 } ||
            other.Vertices is not { Length: >= 3 })
        {
            return false;
        }

        /*
         * SAT requires axes perpendicular to every edge of both polygons.
         *
         * We can test each axis immediately instead.
         */
        if (!TestSatAxes(
                this,
                this,
                other,
                ref mtvAxis,
                ref mtvDepth))
        {
            return false;
        }

        if (!TestSatAxes(
                other,
                this,
                other,
                ref mtvAxis,
                ref mtvDepth))
        {
            return false;
        }

        /*
         * Preserve the previous MTV orientation:
         * axis should point approximately from A toward B.
         */
        var centerA =
            GetCentroid();

        var centerB =
            other.GetCentroid();

        var direction =
            centerB - centerA;

        if (Vector2.Dot(
                direction,
                mtvAxis) < 0f)
        {
            mtvAxis = -mtvAxis;
        }

        return true;
    }
    
    private static bool TestSatAxes(
        Polygon2D axisSource,
        Polygon2D polygonA,
        Polygon2D polygonB,
        ref Vector2 mtvAxis,
        ref float mtvDepth)
    {
        var vertices =
            axisSource.Vertices;

        for (var index = 0;
             index < vertices.Length;
             index++)
        {
            var current =
                vertices[index];

            var next =
                vertices[
                    (index + 1) %
                    vertices.Length];

            var edge =
                next - current;

            var axis =
                new Vector2(
                    -edge.Y,
                    edge.X);

            var axisLengthSquared =
                axis.LengthSquared();

            /*
             * Degenerate edges do not define a useful separating axis.
             * Clean polygons should not normally contain these, but ignoring
             * them keeps SAT robust.
             */
            if (axisLengthSquared <= EPS * EPS)
                continue;

            axis *=
                1f /
                MathF.Sqrt(axisLengthSquared);

            var (minA, maxA) =
                polygonA.ProjectOntoAxis(axis);

            var (minB, maxB) =
                polygonB.ProjectOntoAxis(axis);

            if (maxA < minB ||
                maxB < minA)
            {
                // Separating axis found.
                return false;
            }

            var overlap =
                MathF.Min(maxA, maxB) -
                MathF.Max(minA, minB);

            if (overlap >= mtvDepth)
                continue;

            mtvDepth = overlap;
            mtvAxis = axis;
        }

        return true;
    }

    /// <summary>General intersection supporting concave by triangulation.</summary>
    public bool IntersectsGeneral(Polygon2D other, out Vector2 mtvAxis, out float mtvDepth)
    {
        mtvAxis = Vector2.Zero;
        mtvDepth = float.MaxValue;
        // Clean and normalize once
        CleanAndNormalize();
        other.CleanAndNormalize();

        var thisConcave = IsConcave();
        var otherConcave = other.IsConcave();

        if (!thisConcave && !otherConcave) return IntersectsSAT(other, ref mtvAxis, ref mtvDepth);

        var partsA = thisConcave ? SplitPolygon(this) : [this];
        var partsB = otherConcave ? other.SplitPolygon(other) : [other];

        var hit = false;
        foreach (var a in partsA)
        {
            var aClean = a;
            aClean.CleanAndNormalize();
            foreach (var b in partsB)
            {
                var bClean = b;
                bClean.CleanAndNormalize();
                var axis = Vector2.Zero;
                var depth = float.MaxValue;
                if (aClean.IntersectsSAT(bClean, ref axis, ref depth))
                {
                    hit = true;
                    if (depth < mtvDepth)
                    {
                        mtvDepth = depth;
                        mtvAxis = axis;
                    }
                }
            }
        }

        return hit;
    }

    /// <summary>Compute polygon centroid (area-weighted, robust for convex/concave simple poly).</summary>
    public Vector2 GetCentroid()
    {
        var areaSum = 0f;
        float cx = 0f, cy = 0f;
        for (var i = 0; i < Vertices.Length; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % Vertices.Length];
            var cross = a.X * b.Y - b.X * a.Y;
            areaSum += cross;
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }

        if (MathF.Abs(areaSum) < EPS) return Vertices[0];
        var inv = 1f / (3f * areaSum);
        return new Vector2(cx * inv, cy * inv);
    }

    public Vector2 this[int key]
    {
        get => Vertices[key];
        set => Vertices[key] = value;
    }

    public Vector2[] GetEdges()
    {
        var edges = new Vector2[Vertices.Length];
        for (var i = 0; i < Vertices.Length; i++)
        {
            var current = Vertices[i];
            var next = Vertices[(i + 1) % Vertices.Length];
            edges[i] = next - current;
        }

        return edges;
    }

    public bool IsConcave()
    {
        var isNegative = false;
        var isPositive = false;

        for (var i = 0; i < Vertices.Length; i++)
        {
            var p0 = Vertices[i];
            var p1 = Vertices[(i + 1) % Vertices.Length];
            var p2 = Vertices[(i + 2) % Vertices.Length];

            var crossProduct = CrossProduct(p0, p1, p2);

            if (crossProduct < 0) isNegative = true;
            if (crossProduct > 0) isPositive = true;

            if (isNegative && isPositive) return true; // we are concave
        }

        return false;
    }

    private float CrossProduct(Vector2 p0, Vector2 p1, Vector2 p2)
    {
        var d1 = p1 - p0;
        var d2 = p2 - p1;
        return d1.X * d2.Y - d1.Y * d2.X;
    }

    public (float min, float max) ProjectOntoAxis(Vector2 axis)
    {
        var min = Vector2.Dot(Vertices[0], axis);
        var max = min;

        for (var i = 1; i < Vertices.Length; i++)
        {
            var projection = Vector2.Dot(Vertices[i], axis);
            if (projection < min)
                min = projection;
            if (projection > max)
                max = projection;
        }

        return (min, max);
    }

    public bool Intersects(Polygon2D other)
    {
        if (Vertices is not { Length: >= 3 } ||
            other.Vertices is not { Length: >= 3 })
        {
            return false;
        }

        if (!IsConcave() &&
            !other.IsConcave())
        {
            var axis = Vector2.Zero;
            var depth = float.MaxValue;

            return IntersectsSAT(
                other,
                ref axis,
                ref depth);
        }

        return IntersectsGeneral(
            other,
            out _,
            out _);
    }

    public bool IsPointInside(Vector2 point)
    {
        var isInside = false;

        for (int i = 0, j = Length - 1; i < Length; j = i++)
        {
            var vertex1 = Vertices[i];
            var vertex2 = Vertices[j];

            // Check if the point is inside the polygon by casting a ray
            if (vertex1.Y > point.Y !=
                vertex2.Y > point.Y && // Check if the point's Y is between the Y values of the polygon's edge
                point.X < (vertex2.X - vertex1.X) * (point.Y - vertex1.Y) / (vertex2.Y - vertex1.Y) +
                vertex1.X) // Check if the point is to the left of the edge
                isInside = !isInside;
        }

        return isInside;
    }

    public bool IntersectsCircle(Vector2 center, float radius)
    {
        var radiusSq = radius * radius;
        var verts = Vertices;
        var count = verts.Length;

        if (count == 0) return false;

        // 1. Any vertex inside circle?
        for (var i = 0; i < count; i++)
        {
            var diff = verts[i] - center;
            if (diff.LengthSquared() <= radiusSq)
                return true;
        }

        // 2. Circle center inside polygon?
        if (ContainsPoint(center))
            return true;

        // 3. Edge-circle intersection
        for (var i = 0; i < count; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % count];

            var closest = ClosestPointOnSegment(center, a, b);
            var diff = closest - center;
            if (diff.LengthSquared() <= radiusSq)
                return true;
        }

        return false;
    }

    public bool RayIntersects(Vector2 rayStart, Vector2 rayEnd, out Vector2 intersection)
    {
        intersection = Vector2.Zero;

        for (var i = 0; i < Vertices.Length; i++)
        {
            var current = Vertices[i];
            var next = Vertices[(i + 1) % Vertices.Length];

            if (LineSegmentsIntersect(rayStart, rayEnd, current, next, out intersection))
                return true;
        }

        return false;
    }

    public bool ContainsPoint(Vector2 p, bool includeBoundary = true)
    {
        if (Vertices == null || Length < 3) return false;

        for (int i = 0, j = Length - 1; i < Length; j = i++)
            if (PointOnSegment(Vertices[j], Vertices[i], p))
                return includeBoundary;

        var inside = false;
        for (int i = 0, j = Length - 1; i < Length; j = i++)
        {
            var a = Vertices[j];
            var b = Vertices[i];

            var cond = a.Y > p.Y != b.Y > p.Y;
            if (!cond) continue;

            var t = (p.Y - a.Y) / (b.Y - a.Y);
            var xAtY = a.X + t * (b.X - a.X);

            if (xAtY > p.X) inside = !inside;
        }

        return inside;
    }

    private bool PointOnSegment(Vector2 a, Vector2 b, Vector2 p)
    {
        // Collinearity via cross product
        var ab = b - a;
        var ap = p - a;
        var cross = ab.X * ap.Y - ab.Y * ap.X;
        if (MathF.Abs(cross) > EPS) return false;

        // Within segment bounds via dot products
        var dot = Vector2.Dot(ap, ab);
        if (dot < -EPS) return false;

        var abLenSq = Vector2.Dot(ab, ab);
        if (dot > abLenSq + EPS) return false;

        return true;
    }

    public Polygon2D Transform(Transform transform)
    {
        var polygon = new Polygon2D
        {
            Vertices = new Vector2[Length]
        };

        for (var i = 0; i < Length; i++)
        {
            var point3D = new Vector3(Vertices[i], 0);
            var transformedPoint = Vector3.Transform(point3D, transform.WorldMatrix);

            polygon.Vertices[i] = new Vector2(transformedPoint.X, transformedPoint.Y);
        }

        return polygon;
    }

    public Polygon2D TransformWithDesiredPos(Transform transform, Vector3 desiredPos)
    {
        var polygon = new Polygon2D
        {
            Vertices = new Vector2[Length]
        };

        var translationMatrix = transform.WorldMatrix;
        translationMatrix.M41 = desiredPos.X;
        translationMatrix.M42 = desiredPos.Y;
        translationMatrix.M43 = desiredPos.Z;

        for (var i = 0; i < Length; i++)
        {
            var point3D = new Vector3(Vertices[i], 0);
            var transformedPoint = Vector3.Transform(point3D, translationMatrix);

            polygon.Vertices[i] = new Vector2(transformedPoint.X, transformedPoint.Y);
        }

        return polygon;
    }

    private bool LineSegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, out Vector2 intersection)
    {
        intersection = Vector2.Zero;

        var a1 = p2.Y - p1.Y;
        var b1 = p1.X - p2.X;
        var c1 = a1 * p1.X + b1 * p1.Y;

        var a2 = p4.Y - p3.Y;
        var b2 = p3.X - p4.X;
        var c2 = a2 * p3.X + b2 * p3.Y;

        var denominator = a1 * b2 - a2 * b1;

        if (MathF.Abs(denominator) < EPS)
            return false;

        var intersectX = (b2 * c1 - b1 * c2) / denominator;
        var intersectY = (a1 * c2 - a2 * c1) / denominator;

        intersection = new Vector2(intersectX, intersectY);

        return IsPointOnLineSegment(p1, p2, intersection) && IsPointOnLineSegment(p3, p4, intersection);
    }

    private bool IsPointOnLineSegment(Vector2 p1, Vector2 p2, Vector2 point)
    {
        return point.X >= Math.Min(p1.X, p2.X) && point.X <= Math.Max(p1.X, p2.X) &&
               point.Y >= Math.Min(p1.Y, p2.Y) && point.Y <= Math.Max(p1.Y, p2.Y);
    }

    public List<Polygon2D> SplitPolygon(Polygon2D polygon2D)
    {
        polygon2D.CleanAndNormalize();
        var triangles = new List<Polygon2D>();

        var remainingVertices = new List<Vector2>(polygon2D.Vertices);

        while (remainingVertices.Count > 3)
        {
            var earFound = false;
            for (var i = 0; i < remainingVertices.Count; i++)
            {
                var prev = remainingVertices[(i - 1 + remainingVertices.Count) % remainingVertices.Count];
                var current = remainingVertices[i];
                var next = remainingVertices[(i + 1) % remainingVertices.Count];

                if (IsEar(prev, current, next, remainingVertices))
                {
                    triangles.Add(new Polygon2D
                    {
                        Vertices = new[]
                        {
                            prev, current, next
                        }
                    });
                    remainingVertices.RemoveAt(i);
                    earFound = true;
                    break;
                }
            }

            if (!earFound)
                throw new InvalidOperationException(
                    "Unable to triangulate polygon. Ensure it is simple and non-self-intersecting.");
        }

        triangles.Add(new Polygon2D
        {
            Vertices = remainingVertices.ToArray()
        });
        return triangles;
    }

    private bool IsEar(Vector2 prev, Vector2 current, Vector2 next, List<Vector2> polygon)
    {
        // Check if the current triangle is convex and no other points are inside it
        if (CrossProduct(prev, current, next) >= 0)
        {
            for (var i = 0; i < polygon.Count; i++)
                if (polygon[i] != prev && polygon[i] != current && polygon[i] != next)
                    if (IsPointInTriangle(polygon[i], prev, current, next))
                        return false;
            return true;
        }

        return false;
    }

    private bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        // Compute the sign of the areas formed by the triangle's edges and the point
        var hasSameSignABP = Sign(point, a, b) > EPS;
        var hasSameSignBCP = Sign(point, b, c) > EPS;
        var hasSameSignCAP = Sign(point, c, a) > EPS;

        // If all the signs are the same, the point is inside the triangle
        return hasSameSignABP == hasSameSignBCP && hasSameSignBCP == hasSameSignCAP;
    }

    private float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
    }

    private static Vector2 ClosestPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var abLenSq = ab.LengthSquared();
        if (abLenSq <= float.Epsilon)
            return a;

        var t = Vector2.Dot(p - a, ab) / abLenSq;
        t = MathHelper.Clamp(t, 0f, 1f);
        return a + ab * t;
    }
}
