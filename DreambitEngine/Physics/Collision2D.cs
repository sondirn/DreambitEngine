using System;
using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>
/// Allocation-free narrowphase collision tests for Dreambit's native 2D shapes.
/// </summary>
public static class Collision2D
{
    private const float Epsilon = 0.000001f;
    private const float EpsilonSquared = Epsilon * Epsilon;

    public static bool Intersects(
        in ColliderGeometry2D first,
        in ColliderGeometry2D second)
    {
        if (!first.IsValid ||
            !second.IsValid)
        {
            return false;
        }

        return first.Kind switch
        {
            ColliderGeometryKind.Polygon =>
                second.Kind switch
                {
                    ColliderGeometryKind.Polygon =>
                        first.Polygon.Intersects(
                            second.Polygon),

                    ColliderGeometryKind.Circle =>
                        first.Polygon.IntersectsCircle(
                            second.Circle.Center,
                            second.Circle.Radius),

                    ColliderGeometryKind.Capsule =>
                        PolygonIntersectsCapsule(
                            first.Polygon,
                            second.Capsule),

                    _ => false
                },

            ColliderGeometryKind.Circle =>
                second.Kind switch
                {
                    ColliderGeometryKind.Polygon =>
                        second.Polygon.IntersectsCircle(
                            first.Circle.Center,
                            first.Circle.Radius),

                    ColliderGeometryKind.Circle =>
                        CircleIntersectsCircle(
                            first.Circle,
                            second.Circle),

                    ColliderGeometryKind.Capsule =>
                        CircleIntersectsCapsule(
                            first.Circle,
                            second.Capsule),

                    _ => false
                },

            ColliderGeometryKind.Capsule =>
                second.Kind switch
                {
                    ColliderGeometryKind.Polygon =>
                        PolygonIntersectsCapsule(
                            second.Polygon,
                            first.Capsule),

                    ColliderGeometryKind.Circle =>
                        CircleIntersectsCapsule(
                            second.Circle,
                            first.Capsule),

                    ColliderGeometryKind.Capsule =>
                        CapsuleIntersectsCapsule(
                            first.Capsule,
                            second.Capsule),

                    _ => false
                },

            _ => false
        };
    }

    public static bool ContainsPoint(
        in ColliderGeometry2D geometry,
        Vector2 point)
    {
        if (!geometry.IsValid)
            return false;

        return geometry.Kind switch
        {
            ColliderGeometryKind.Polygon =>
                geometry.Polygon.ContainsPoint(point),

            ColliderGeometryKind.Circle =>
                PointIntersectsCircle(
                    point,
                    geometry.Circle),

            ColliderGeometryKind.Capsule =>
                PointIntersectsCapsule(
                    point,
                    geometry.Capsule),

            _ => false
        };
    }

    public static bool IntersectsRay(
        in ColliderGeometry2D geometry,
        Ray2D ray)
    {
        if (!geometry.IsValid)
            return false;

        return geometry.Kind switch
        {
            ColliderGeometryKind.Polygon =>
                geometry.Polygon.RayIntersects(
                    ray.Start,
                    ray.End,
                    out _),

            ColliderGeometryKind.Circle =>
                PointSegmentDistanceSquared(
                    geometry.Circle.Center,
                    ray.Start,
                    ray.End) <=
                geometry.Circle.Radius *
                geometry.Circle.Radius,

            ColliderGeometryKind.Capsule =>
                SegmentSegmentDistanceSquared(
                    ray.Start,
                    ray.End,
                    geometry.Capsule.Start,
                    geometry.Capsule.End) <=
                geometry.Capsule.Radius *
                geometry.Capsule.Radius,

            _ => false
        };
    }

    private static bool PointIntersectsCircle(
        Vector2 point,
        Circle2D circle)
    {
        var difference =
            point - circle.Center;

        return difference.LengthSquared() <=
               circle.Radius * circle.Radius;
    }

    private static bool PointIntersectsCapsule(
        Vector2 point,
        Capsule2D capsule)
    {
        return PointSegmentDistanceSquared(
                   point,
                   capsule.Start,
                   capsule.End) <=
               capsule.Radius * capsule.Radius;
    }

    private static bool CircleIntersectsCircle(
        Circle2D first,
        Circle2D second)
    {
        var combinedRadius =
            first.Radius +
            second.Radius;

        var difference =
            second.Center -
            first.Center;

        return difference.LengthSquared() <=
               combinedRadius *
               combinedRadius;
    }

    private static bool CircleIntersectsCapsule(
        Circle2D circle,
        Capsule2D capsule)
    {
        var combinedRadius =
            circle.Radius +
            capsule.Radius;

        return PointSegmentDistanceSquared(
                   circle.Center,
                   capsule.Start,
                   capsule.End) <=
               combinedRadius *
               combinedRadius;
    }

    private static bool CapsuleIntersectsCapsule(
        Capsule2D first,
        Capsule2D second)
    {
        var combinedRadius =
            first.Radius +
            second.Radius;

        return SegmentSegmentDistanceSquared(
                   first.Start,
                   first.End,
                   second.Start,
                   second.End) <=
               combinedRadius *
               combinedRadius;
    }

    private static bool PolygonIntersectsCapsule(
        Polygon2D polygon,
        Capsule2D capsule)
    {
        var vertices =
            polygon.Vertices;

        if (vertices is not { Length: >= 3 })
            return false;

        /*
         * If either endpoint is contained by the polygon, the capsule's
         * center segment is already touching/intersecting the polygon.
         */
        if (polygon.ContainsPoint(capsule.Start) ||
            polygon.ContainsPoint(capsule.End))
        {
            return true;
        }

        var radiusSquared =
            capsule.Radius *
            capsule.Radius;

        for (var index = 0;
             index < vertices.Length;
             index++)
        {
            var edgeStart =
                vertices[index];

            var edgeEnd =
                vertices[
                    (index + 1) %
                    vertices.Length];

            if (SegmentSegmentDistanceSquared(
                    capsule.Start,
                    capsule.End,
                    edgeStart,
                    edgeEnd) <=
                radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    public static Vector2 ClosestPointOnSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        var segment =
            end - start;

        var lengthSquared =
            segment.LengthSquared();

        if (lengthSquared <= EpsilonSquared)
            return start;

        var t =
            Vector2.Dot(
                point - start,
                segment) /
            lengthSquared;

        t = Math.Clamp(
            t,
            0f,
            1f);

        return start +
               segment * t;
    }

    public static float PointSegmentDistanceSquared(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        var closest =
            ClosestPointOnSegment(
                point,
                start,
                end);

        return Vector2.DistanceSquared(
            point,
            closest);
    }

    public static float SegmentSegmentDistanceSquared(
        Vector2 firstStart,
        Vector2 firstEnd,
        Vector2 secondStart,
        Vector2 secondEnd)
    {
        if (SegmentsIntersect(
                firstStart,
                firstEnd,
                secondStart,
                secondEnd))
        {
            return 0f;
        }

        var distanceSquared =
            PointSegmentDistanceSquared(
                firstStart,
                secondStart,
                secondEnd);

        distanceSquared =
            MathF.Min(
                distanceSquared,
                PointSegmentDistanceSquared(
                    firstEnd,
                    secondStart,
                    secondEnd));

        distanceSquared =
            MathF.Min(
                distanceSquared,
                PointSegmentDistanceSquared(
                    secondStart,
                    firstStart,
                    firstEnd));

        distanceSquared =
            MathF.Min(
                distanceSquared,
                PointSegmentDistanceSquared(
                    secondEnd,
                    firstStart,
                    firstEnd));

        return distanceSquared;
    }

    private static bool SegmentsIntersect(
        Vector2 firstStart,
        Vector2 firstEnd,
        Vector2 secondStart,
        Vector2 secondEnd)
    {
        var firstDirection =
            firstEnd -
            firstStart;

        var secondDirection =
            secondEnd -
            secondStart;

        var firstLengthSquared =
            firstDirection.LengthSquared();

        var secondLengthSquared =
            secondDirection.LengthSquared();

        if (firstLengthSquared <= EpsilonSquared)
        {
            return PointSegmentDistanceSquared(
                       firstStart,
                       secondStart,
                       secondEnd) <=
                   EpsilonSquared;
        }

        if (secondLengthSquared <= EpsilonSquared)
        {
            return PointSegmentDistanceSquared(
                       secondStart,
                       firstStart,
                       firstEnd) <=
                   EpsilonSquared;
        }

        var offset =
            secondStart -
            firstStart;

        var denominator =
            Cross(
                firstDirection,
                secondDirection);

        if (MathF.Abs(denominator) <= Epsilon)
        {
            /*
             * Parallel but not collinear.
             */
            if (MathF.Abs(
                    Cross(
                        offset,
                        firstDirection)) >
                Epsilon)
            {
                return false;
            }

            /*
             * Collinear: project the second segment onto the first.
             */
            var t0 =
                Vector2.Dot(
                    offset,
                    firstDirection) /
                firstLengthSquared;

            var t1 =
                t0 +
                Vector2.Dot(
                    secondDirection,
                    firstDirection) /
                firstLengthSquared;

            var minimum =
                MathF.Min(
                    t0,
                    t1);

            var maximum =
                MathF.Max(
                    t0,
                    t1);

            return maximum >= -Epsilon &&
                   minimum <= 1f + Epsilon;
        }

        var firstT =
            Cross(
                offset,
                secondDirection) /
            denominator;

        var secondT =
            Cross(
                offset,
                firstDirection) /
            denominator;

        return firstT >= -Epsilon &&
               firstT <= 1f + Epsilon &&
               secondT >= -Epsilon &&
               secondT <= 1f + Epsilon;
    }

    private static float Cross(
        Vector2 first,
        Vector2 second)
    {
        return first.X * second.Y -
               first.Y * second.X;
    }
}