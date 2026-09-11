using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>
/// Describes the minimum translation needed to move the first collision
/// geometry out of the second collision geometry.
/// </summary>
public readonly struct CollisionManifold2D
{
    public CollisionManifold2D(
        Vector2 normal,
        float penetration)
    {
        Normal = normal;
        Penetration = penetration;
    }

    /// <summary>
    /// Unit direction in which the first geometry must move
    /// to leave the second geometry.
    /// </summary>
    public Vector2 Normal { get; }

    /// <summary>
    /// Distance the first geometry must move along Normal
    /// to leave the second geometry.
    /// </summary>
    public float Penetration { get; }
}

/// <summary>
/// Generates collision-resolution information for Dreambit's native
/// 2D collider geometry.
///
/// Unlike Collision2D, which answers only whether two shapes overlap,
/// this class also computes a separation normal and penetration depth.
/// </summary>
public static class CollisionManifoldSolver2D
{
    private const float Epsilon = 0.000001f;
    private const float EpsilonSquared =
        Epsilon * Epsilon;

    public static bool TryGetManifold(
        in ColliderGeometry2D first,
        in ColliderGeometry2D second,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        if (!first.IsValid ||
            !second.IsValid)
        {
            return false;
        }

        switch (first.Kind)
        {
            case ColliderGeometryKind.Polygon:
            {
                switch (second.Kind)
                {
                    case ColliderGeometryKind.Polygon:
                        return TryPolygonPolygon(
                            first.Polygon,
                            second.Polygon,
                            out manifold);

                    case ColliderGeometryKind.Circle:
                    {
                        if (!TryCirclePolygon(
                                second.Circle,
                                first.Polygon,
                                out var circleManifold))
                        {
                            return false;
                        }

                        manifold =
                            Invert(circleManifold);

                        return true;
                    }

                    case ColliderGeometryKind.Capsule:
                    {
                        if (!TryCapsulePolygon(
                                second.Capsule,
                                first.Polygon,
                                out var capsuleManifold))
                        {
                            return false;
                        }

                        manifold =
                            Invert(capsuleManifold);

                        return true;
                    }

                    default:
                        return false;
                }
            }

            case ColliderGeometryKind.Circle:
            {
                switch (second.Kind)
                {
                    case ColliderGeometryKind.Polygon:
                        return TryCirclePolygon(
                            first.Circle,
                            second.Polygon,
                            out manifold);

                    case ColliderGeometryKind.Circle:
                        return TryCircleCircle(
                            first.Circle,
                            second.Circle,
                            out manifold);

                    case ColliderGeometryKind.Capsule:
                        return TryCircleCapsule(
                            first.Circle,
                            second.Capsule,
                            out manifold);

                    default:
                        return false;
                }
            }

            case ColliderGeometryKind.Capsule:
            {
                switch (second.Kind)
                {
                    case ColliderGeometryKind.Polygon:
                        return TryCapsulePolygon(
                            first.Capsule,
                            second.Polygon,
                            out manifold);

                    case ColliderGeometryKind.Circle:
                    {
                        if (!TryCircleCapsule(
                                second.Circle,
                                first.Capsule,
                                out var circleManifold))
                        {
                            return false;
                        }

                        manifold =
                            Invert(circleManifold);

                        return true;
                    }

                    case ColliderGeometryKind.Capsule:
                        return TryCapsuleCapsule(
                            first.Capsule,
                            second.Capsule,
                            out manifold);

                    default:
                        return false;
                }
            }

            default:
                return false;
        }
    }

    #region Polygon vs Polygon

    private static bool TryPolygonPolygon(
        Polygon2D first,
        Polygon2D second,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        if (first.Vertices is not { Length: >= 3 } ||
            second.Vertices is not { Length: >= 3 })
        {
            return false;
        }

        var firstConcave =
            first.IsConcave();

        var secondConcave =
            second.IsConcave();

        if (!firstConcave &&
            !secondConcave)
        {
            return TryConvexPolygonPolygon(
                first,
                second,
                out manifold);
        }

        /*
         * Dreambit already supports concave polygons by triangulation.
         *
         * Resolve concave polygons in terms of their convex pieces as well.
         * This path allocates, but only concave collider pairs use it.
         */
        var firstParts =
            firstConcave
                ? first.SplitPolygon(first)
                : new List<Polygon2D>(1)
                {
                    first
                };

        var secondParts =
            secondConcave
                ? second.SplitPolygon(second)
                : new List<Polygon2D>(1)
                {
                    second
                };

        var found =
            false;

        var foundPositivePenetration =
            false;

        var best =
            default(CollisionManifold2D);

        for (var firstIndex = 0;
             firstIndex < firstParts.Count;
             firstIndex++)
        {
            for (var secondIndex = 0;
                 secondIndex < secondParts.Count;
                 secondIndex++)
            {
                if (!TryConvexPolygonPolygon(
                        firstParts[firstIndex],
                        secondParts[secondIndex],
                        out var candidate))
                {
                    continue;
                }

                var isPositive =
                    candidate.Penetration >
                    Epsilon;

                if (!found)
                {
                    best = candidate;
                    found = true;
                    foundPositivePenetration =
                        isPositive;

                    continue;
                }

                /*
                 * A zero-depth triangle contact should not hide a real
                 * penetration against another triangle.
                 */
                if (isPositive &&
                    !foundPositivePenetration)
                {
                    best = candidate;
                    foundPositivePenetration = true;
                    continue;
                }

                if (isPositive !=
                    foundPositivePenetration)
                {
                    continue;
                }

                /*
                 * Pick the minimum translation among overlapping convex pieces.
                 * Multiple contacts are handled iteratively by RigidBody2D.
                 */
                if (candidate.Penetration <
                    best.Penetration)
                {
                    best = candidate;
                }
            }
        }

        if (!found)
            return false;

        manifold = best;
        return true;
    }

    private static bool TryConvexPolygonPolygon(
        Polygon2D first,
        Polygon2D second,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var bestNormal =
            Vector2.Zero;

        var bestPenetration =
            float.MaxValue;

        var hasAxis =
            false;

        if (!TestPolygonAxes(
                first,
                first,
                second,
                ref bestNormal,
                ref bestPenetration,
                ref hasAxis))
        {
            return false;
        }

        if (!TestPolygonAxes(
                second,
                first,
                second,
                ref bestNormal,
                ref bestPenetration,
                ref hasAxis))
        {
            return false;
        }

        if (!hasAxis ||
            bestPenetration ==
            float.MaxValue)
        {
            return false;
        }

        manifold =
            new CollisionManifold2D(
                bestNormal,
                MathF.Max(
                    0f,
                    bestPenetration));

        return true;
    }

    private static bool TestPolygonAxes(
        Polygon2D axisSource,
        Polygon2D first,
        Polygon2D second,
        ref Vector2 bestNormal,
        ref float bestPenetration,
        ref bool hasAxis)
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

            if (!TryNormalize(
                    axis,
                    out axis))
            {
                continue;
            }

            var firstProjection =
                first.ProjectOntoAxis(axis);

            var secondProjection =
                second.ProjectOntoAxis(axis);

            if (!TryUpdateBestAxis(
                    firstProjection.min,
                    firstProjection.max,
                    secondProjection.min,
                    secondProjection.max,
                    axis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Circle vs Polygon

    private static bool TryCirclePolygon(
        Circle2D circle,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        if (!circle.IsValid ||
            polygon.Vertices is not
                { Length: >= 3 })
        {
            return false;
        }

        /*
         * SAT works cleanly for convex polygons.
         *
         * Concave polygons require actual boundary handling, otherwise
         * triangulation introduces internal edges that are not real collision
         * surfaces.
         */
        if (polygon.IsConcave())
        {
            return TryCircleConcavePolygon(
                circle,
                polygon,
                out manifold);
        }

        return TryCircleConvexPolygon(
            circle,
            polygon,
            out manifold);
    }

    private static bool TryCircleConvexPolygon(
        Circle2D circle,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var bestNormal =
            Vector2.Zero;

        var bestPenetration =
            float.MaxValue;

        var hasAxis =
            false;

        var vertices =
            polygon.Vertices;

        /*
         * Polygon face normals.
         */
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

            if (!TryNormalize(
                    axis,
                    out axis))
            {
                continue;
            }

            if (!TestCirclePolygonAxis(
                    circle,
                    polygon,
                    axis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        /*
         * Curved circle contacts require an axis between the circle center
         * and a polygon vertex. Testing every vertex is slightly more work
         * than finding only the closest one, but keeps this robust and simple.
         */
        for (var index = 0;
             index < vertices.Length;
             index++)
        {
            var axis =
                circle.Center -
                vertices[index];

            if (!TryNormalize(
                    axis,
                    out axis))
            {
                continue;
            }

            if (!TestCirclePolygonAxis(
                    circle,
                    polygon,
                    axis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        if (!hasAxis ||
            bestPenetration ==
            float.MaxValue)
        {
            return false;
        }

        manifold =
            new CollisionManifold2D(
                bestNormal,
                MathF.Max(
                    0f,
                    bestPenetration));

        return true;
    }

    private static bool TestCirclePolygonAxis(
        Circle2D circle,
        Polygon2D polygon,
        Vector2 axis,
        ref Vector2 bestNormal,
        ref float bestPenetration,
        ref bool hasAxis)
    {
        ProjectCircle(
            circle,
            axis,
            out var circleMinimum,
            out var circleMaximum);

        var polygonProjection =
            polygon.ProjectOntoAxis(axis);

        return TryUpdateBestAxis(
            circleMinimum,
            circleMaximum,
            polygonProjection.min,
            polygonProjection.max,
            axis,
            ref bestNormal,
            ref bestPenetration,
            ref hasAxis);
    }

    private static bool TryCircleConcavePolygon(
        Circle2D circle,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        if (!polygon.IntersectsCircle(
                circle.Center,
                circle.Radius))
        {
            return false;
        }

        if (!FindClosestPointOnPolygonBoundary(
                polygon,
                circle.Center,
                out var closestPoint,
                out var closestEdgeIndex,
                out var distanceSquared))
        {
            return false;
        }

        var centerInside =
            polygon.ContainsPoint(
                circle.Center,
                false);

        if (distanceSquared >
            EpsilonSquared)
        {
            var distance =
                MathF.Sqrt(
                    distanceSquared);

            Vector2 normal;
            float penetration;

            if (centerInside)
            {
                /*
                 * Circle center is inside the polygon.
                 *
                 * Move toward the nearest boundary, then continue by the
                 * circle radius.
                 */
                normal =
                    (
                        closestPoint -
                        circle.Center
                    ) /
                    distance;

                penetration =
                    circle.Radius +
                    distance;
            }
            else
            {
                normal =
                    (
                        circle.Center -
                        closestPoint
                    ) /
                    distance;

                penetration =
                    circle.Radius -
                    distance;
            }

            manifold =
                new CollisionManifold2D(
                    normal,
                    MathF.Max(
                        0f,
                        penetration));

            return true;
        }

        /*
         * Center is exactly on the boundary. Use the actual polygon edge
         * orientation to determine the outside direction.
         */
        var outwardNormal =
            GetPolygonOutwardNormal(
                polygon,
                closestEdgeIndex);

        manifold =
            new CollisionManifold2D(
                outwardNormal,
                circle.Radius);

        return true;
    }

    #endregion

    #region Circle vs Circle

    private static bool TryCircleCircle(
        Circle2D first,
        Circle2D second,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var combinedRadius =
            first.Radius +
            second.Radius;

        var difference =
            first.Center -
            second.Center;

        var distanceSquared =
            difference.LengthSquared();

        var combinedRadiusSquared =
            combinedRadius *
            combinedRadius;

        if (distanceSquared >
            combinedRadiusSquared)
        {
            return false;
        }

        if (distanceSquared >
            EpsilonSquared)
        {
            var distance =
                MathF.Sqrt(
                    distanceSquared);

            manifold =
                new CollisionManifold2D(
                    difference / distance,
                    MathF.Max(
                        0f,
                        combinedRadius -
                        distance));

            return true;
        }

        /*
         * Coincident centers have no unique geometric normal.
         */
        manifold =
            new CollisionManifold2D(
                Vector2.UnitX,
                combinedRadius);

        return true;
    }

    #endregion

    #region Circle vs Capsule

    private static bool TryCircleCapsule(
        Circle2D circle,
        Capsule2D capsule,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var closest =
            Collision2D
                .ClosestPointOnSegment(
                    circle.Center,
                    capsule.Start,
                    capsule.End);

        var difference =
            circle.Center -
            closest;

        var distanceSquared =
            difference.LengthSquared();

        var combinedRadius =
            circle.Radius +
            capsule.Radius;

        if (distanceSquared >
            combinedRadius *
            combinedRadius)
        {
            return false;
        }

        if (distanceSquared >
            EpsilonSquared)
        {
            var distance =
                MathF.Sqrt(
                    distanceSquared);

            manifold =
                new CollisionManifold2D(
                    difference / distance,
                    MathF.Max(
                        0f,
                        combinedRadius -
                        distance));

            return true;
        }

        /*
         * Circle center lies directly on the capsule center segment.
         *
         * Any perpendicular direction is a valid shortest way out.
         */
        var segment =
            capsule.End -
            capsule.Start;

        Vector2 fallbackNormal;

        if (TryNormalize(
                new Vector2(
                    -segment.Y,
                    segment.X),
                out var perpendicular))
        {
            fallbackNormal =
                perpendicular;
        }
        else
        {
            fallbackNormal =
                Vector2.UnitX;
        }

        manifold =
            new CollisionManifold2D(
                fallbackNormal,
                combinedRadius);

        return true;
    }

    #endregion

    #region Capsule vs Capsule

    private static bool TryCapsuleCapsule(
        Capsule2D first,
        Capsule2D second,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        ClosestPointsOnSegments(
            first.Start,
            first.End,
            second.Start,
            second.End,
            out var firstPoint,
            out var secondPoint);

        var difference =
            firstPoint -
            secondPoint;

        var distanceSquared =
            difference.LengthSquared();

        var combinedRadius =
            first.Radius +
            second.Radius;

        if (distanceSquared >
            combinedRadius *
            combinedRadius)
        {
            return false;
        }

        if (distanceSquared >
            EpsilonSquared)
        {
            var distance =
                MathF.Sqrt(
                    distanceSquared);

            manifold =
                new CollisionManifold2D(
                    difference / distance,
                    MathF.Max(
                        0f,
                        combinedRadius -
                        distance));

            return true;
        }

        var firstMidpoint =
            (
                first.Start +
                first.End
            ) *
            0.5f;

        var secondMidpoint =
            (
                second.Start +
                second.End
            ) *
            0.5f;

        var midpointDifference =
            firstMidpoint -
            secondMidpoint;

        if (TryNormalize(
                midpointDifference,
                out var midpointNormal))
        {
            manifold =
                new CollisionManifold2D(
                    midpointNormal,
                    combinedRadius);

            return true;
        }

        var firstSegment =
            first.End -
            first.Start;

        if (TryNormalize(
                new Vector2(
                    -firstSegment.Y,
                    firstSegment.X),
                out var firstPerpendicular))
        {
            manifold =
                new CollisionManifold2D(
                    firstPerpendicular,
                    combinedRadius);

            return true;
        }

        var secondSegment =
            second.End -
            second.Start;

        if (TryNormalize(
                new Vector2(
                    -secondSegment.Y,
                    secondSegment.X),
                out var secondPerpendicular))
        {
            manifold =
                new CollisionManifold2D(
                    secondPerpendicular,
                    combinedRadius);

            return true;
        }

        manifold =
            new CollisionManifold2D(
                Vector2.UnitX,
                combinedRadius);

        return true;
    }

    #endregion

    #region Capsule vs Polygon

    private static bool TryCapsulePolygon(
        Capsule2D capsule,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        if (!capsule.IsValid ||
            polygon.Vertices is not
                { Length: >= 3 })
        {
            return false;
        }

        if (polygon.IsConcave())
        {
            return TryCapsuleConcavePolygon(
                capsule,
                polygon,
                out manifold);
        }

        return TryCapsuleConvexPolygon(
            capsule,
            polygon,
            out manifold);
    }

    private static bool TryCapsuleConvexPolygon(
        Capsule2D capsule,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var bestNormal =
            Vector2.Zero;

        var bestPenetration =
            float.MaxValue;

        var hasAxis =
            false;

        var vertices =
            polygon.Vertices;

        /*
         * Polygon face normals.
         */
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

            if (!TryNormalize(
                    axis,
                    out axis))
            {
                continue;
            }

            if (!TestCapsulePolygonAxis(
                    capsule,
                    polygon,
                    axis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        /*
         * Capsule body side normal.
         */
        var capsuleSegment =
            capsule.End -
            capsule.Start;

        var capsuleSideAxis =
            new Vector2(
                -capsuleSegment.Y,
                capsuleSegment.X);

        if (TryNormalize(
                capsuleSideAxis,
                out capsuleSideAxis))
        {
            if (!TestCapsulePolygonAxis(
                    capsule,
                    polygon,
                    capsuleSideAxis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        /*
         * Polygon vertex vs curved capsule surface axes.
         */
        for (var index = 0;
             index < vertices.Length;
             index++)
        {
            var vertex =
                vertices[index];

            var closestOnCapsuleSegment =
                Collision2D
                    .ClosestPointOnSegment(
                        vertex,
                        capsule.Start,
                        capsule.End);

            var axis =
                closestOnCapsuleSegment -
                vertex;

            if (!TryNormalize(
                    axis,
                    out axis))
            {
                continue;
            }

            if (!TestCapsulePolygonAxis(
                    capsule,
                    polygon,
                    axis,
                    ref bestNormal,
                    ref bestPenetration,
                    ref hasAxis))
            {
                return false;
            }
        }

        if (!hasAxis ||
            bestPenetration ==
            float.MaxValue)
        {
            return false;
        }

        manifold =
            new CollisionManifold2D(
                bestNormal,
                MathF.Max(
                    0f,
                    bestPenetration));

        return true;
    }

    private static bool TestCapsulePolygonAxis(
        Capsule2D capsule,
        Polygon2D polygon,
        Vector2 axis,
        ref Vector2 bestNormal,
        ref float bestPenetration,
        ref bool hasAxis)
    {
        ProjectCapsule(
            capsule,
            axis,
            out var capsuleMinimum,
            out var capsuleMaximum);

        var polygonProjection =
            polygon.ProjectOntoAxis(axis);

        return TryUpdateBestAxis(
            capsuleMinimum,
            capsuleMaximum,
            polygonProjection.min,
            polygonProjection.max,
            axis,
            ref bestNormal,
            ref bestPenetration,
            ref hasAxis);
    }

    private static bool TryCapsuleConcavePolygon(
        Capsule2D capsule,
        Polygon2D polygon,
        out CollisionManifold2D manifold)
    {
        manifold = default;

        var capsuleGeometry =
            ColliderGeometry2D
                .FromCapsule(capsule);

        var polygonGeometry =
            ColliderGeometry2D
                .FromPolygon(polygon);

        if (!Collision2D.Intersects(
                capsuleGeometry,
                polygonGeometry))
        {
            return false;
        }

        if (!FindClosestPointsSegmentPolygonBoundary(
                capsule.Start,
                capsule.End,
                polygon,
                out var closestOnCapsule,
                out var closestOnPolygon,
                out var closestEdgeIndex,
                out var distanceSquared))
        {
            return false;
        }

        if (distanceSquared >
            EpsilonSquared)
        {
            var distance =
                MathF.Sqrt(
                    distanceSquared);

            var capsuleCoreInside =
                polygon.ContainsPoint(
                    closestOnCapsule,
                    false);

            Vector2 normal;
            float penetration;

            if (capsuleCoreInside)
            {
                normal =
                    (
                        closestOnPolygon -
                        closestOnCapsule
                    ) /
                    distance;

                penetration =
                    capsule.Radius +
                    distance;
            }
            else
            {
                normal =
                    (
                        closestOnCapsule -
                        closestOnPolygon
                    ) /
                    distance;

                penetration =
                    capsule.Radius -
                    distance;
            }

            manifold =
                new CollisionManifold2D(
                    normal,
                    MathF.Max(
                        0f,
                        penetration));

            return true;
        }

        /*
         * The capsule center segment itself reaches/crosses the polygon
         * boundary. Resolve using the real polygon edge normal.
         *
         * Deep center-line penetration may require multiple solver
         * iterations, which RigidBody2D already performs.
         */
        manifold =
            new CollisionManifold2D(
                GetPolygonOutwardNormal(
                    polygon,
                    closestEdgeIndex),
                capsule.Radius);

        return true;
    }

    #endregion

    #region Projection / SAT Helpers

    private static bool TryUpdateBestAxis(
        float firstMinimum,
        float firstMaximum,
        float secondMinimum,
        float secondMaximum,
        Vector2 axis,
        ref Vector2 bestNormal,
        ref float bestPenetration,
        ref bool hasAxis)
    {
        /*
         * Strict separation.
         *
         * Equality means the shapes are touching, which is treated as a
         * zero-penetration contact.
         */
        if (firstMaximum <
            secondMinimum ||
            secondMaximum <
            firstMinimum)
        {
            return false;
        }

        /*
         * Distance needed to move the FIRST interval out of the SECOND
         * interval in either direction.
         *
         * This formulation also handles interval containment correctly,
         * unlike just measuring intersection length.
         */

        var moveNegative =
            firstMaximum -
            secondMinimum;

        var movePositive =
            secondMaximum -
            firstMinimum;

        Vector2 normal;
        float penetration;

        if (moveNegative <
            movePositive)
        {
            normal =
                -axis;

            penetration =
                moveNegative;
        }
        else
        {
            normal =
                axis;

            penetration =
                movePositive;
        }

        penetration =
            MathF.Max(
                0f,
                penetration);

        if (!hasAxis ||
            penetration <
            bestPenetration)
        {
            bestNormal =
                normal;

            bestPenetration =
                penetration;

            hasAxis =
                true;
        }

        return true;
    }

    private static void ProjectCircle(
        Circle2D circle,
        Vector2 axis,
        out float minimum,
        out float maximum)
    {
        var center =
            Vector2.Dot(
                circle.Center,
                axis);

        minimum =
            center -
            circle.Radius;

        maximum =
            center +
            circle.Radius;
    }

    private static void ProjectCapsule(
        Capsule2D capsule,
        Vector2 axis,
        out float minimum,
        out float maximum)
    {
        var start =
            Vector2.Dot(
                capsule.Start,
                axis);

        var end =
            Vector2.Dot(
                capsule.End,
                axis);

        minimum =
            MathF.Min(
                start,
                end) -
            capsule.Radius;

        maximum =
            MathF.Max(
                start,
                end) +
            capsule.Radius;
    }

    #endregion

    #region Polygon Boundary Helpers

    private static bool FindClosestPointOnPolygonBoundary(
        Polygon2D polygon,
        Vector2 point,
        out Vector2 closestPoint,
        out int edgeIndex,
        out float distanceSquared)
    {
        closestPoint =
            Vector2.Zero;

        edgeIndex =
            -1;

        distanceSquared =
            float.MaxValue;

        var vertices =
            polygon.Vertices;

        if (vertices is not
            { Length: >= 2 })
        {
            return false;
        }

        for (var index = 0;
             index < vertices.Length;
             index++)
        {
            var start =
                vertices[index];

            var end =
                vertices[
                    (index + 1) %
                    vertices.Length];

            var candidate =
                Collision2D
                    .ClosestPointOnSegment(
                        point,
                        start,
                        end);

            var candidateDistanceSquared =
                Vector2.DistanceSquared(
                    point,
                    candidate);

            if (candidateDistanceSquared >=
                distanceSquared)
            {
                continue;
            }

            distanceSquared =
                candidateDistanceSquared;

            closestPoint =
                candidate;

            edgeIndex =
                index;
        }

        return edgeIndex >= 0;
    }

    private static bool FindClosestPointsSegmentPolygonBoundary(
        Vector2 segmentStart,
        Vector2 segmentEnd,
        Polygon2D polygon,
        out Vector2 closestOnSegment,
        out Vector2 closestOnPolygon,
        out int edgeIndex,
        out float distanceSquared)
    {
        closestOnSegment =
            Vector2.Zero;

        closestOnPolygon =
            Vector2.Zero;

        edgeIndex =
            -1;

        distanceSquared =
            float.MaxValue;

        var vertices =
            polygon.Vertices;

        if (vertices is not
            { Length: >= 2 })
        {
            return false;
        }

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

            ClosestPointsOnSegments(
                segmentStart,
                segmentEnd,
                edgeStart,
                edgeEnd,
                out var segmentPoint,
                out var polygonPoint);

            var candidateDistanceSquared =
                Vector2.DistanceSquared(
                    segmentPoint,
                    polygonPoint);

            if (candidateDistanceSquared >=
                distanceSquared)
            {
                continue;
            }

            distanceSquared =
                candidateDistanceSquared;

            closestOnSegment =
                segmentPoint;

            closestOnPolygon =
                polygonPoint;

            edgeIndex =
                index;
        }

        return edgeIndex >= 0;
    }

    private static Vector2 GetPolygonOutwardNormal(
        Polygon2D polygon,
        int edgeIndex)
    {
        var vertices =
            polygon.Vertices;

        if (vertices is not
            { Length: >= 2 } ||
            edgeIndex < 0 ||
            edgeIndex >=
            vertices.Length)
        {
            return Vector2.UnitX;
        }

        var start =
            vertices[edgeIndex];

        var end =
            vertices[
                (edgeIndex + 1) %
                vertices.Length];

        var edge =
            end -
            start;

        if (edge.LengthSquared() <=
            EpsilonSquared)
        {
            return Vector2.UnitX;
        }

        var signedAreaTimesTwo =
            0f;

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

            signedAreaTimesTwo +=
                current.X *
                next.Y -
                next.X *
                current.Y;
        }

        /*
         * CCW polygon:
         * interior is on the left side of each edge,
         * therefore outward is the right-hand normal.
         *
         * CW polygon is the opposite.
         */
        var normal =
            signedAreaTimesTwo >= 0f
                ? new Vector2(
                    edge.Y,
                    -edge.X)
                : new Vector2(
                    -edge.Y,
                    edge.X);

        if (!TryNormalize(
                normal,
                out normal))
        {
            return Vector2.UnitX;
        }

        return normal;
    }

    #endregion

    #region Segment Helpers

    private static void ClosestPointsOnSegments(
        Vector2 firstStart,
        Vector2 firstEnd,
        Vector2 secondStart,
        Vector2 secondEnd,
        out Vector2 firstPoint,
        out Vector2 secondPoint)
    {
        var firstDirection =
            firstEnd -
            firstStart;

        var secondDirection =
            secondEnd -
            secondStart;

        var offset =
            firstStart -
            secondStart;

        var firstLengthSquared =
            Vector2.Dot(
                firstDirection,
                firstDirection);

        var secondLengthSquared =
            Vector2.Dot(
                secondDirection,
                secondDirection);

        var secondProjection =
            Vector2.Dot(
                secondDirection,
                offset);

        float firstT;
        float secondT;

        if (firstLengthSquared <=
            EpsilonSquared &&
            secondLengthSquared <=
            EpsilonSquared)
        {
            firstPoint =
                firstStart;

            secondPoint =
                secondStart;

            return;
        }

        if (firstLengthSquared <=
            EpsilonSquared)
        {
            firstT =
                0f;

            secondT =
                Math.Clamp(
                    secondProjection /
                    secondLengthSquared,
                    0f,
                    1f);
        }
        else
        {
            var firstProjection =
                Vector2.Dot(
                    firstDirection,
                    offset);

            if (secondLengthSquared <=
                EpsilonSquared)
            {
                secondT =
                    0f;

                firstT =
                    Math.Clamp(
                        -firstProjection /
                        firstLengthSquared,
                        0f,
                        1f);
            }
            else
            {
                var directionsDot =
                    Vector2.Dot(
                        firstDirection,
                        secondDirection);

                var denominator =
                    firstLengthSquared *
                    secondLengthSquared -
                    directionsDot *
                    directionsDot;

                if (MathF.Abs(
                        denominator) >
                    Epsilon)
                {
                    firstT =
                        Math.Clamp(
                            (
                                directionsDot *
                                secondProjection -
                                firstProjection *
                                secondLengthSquared
                            ) /
                            denominator,
                            0f,
                            1f);
                }
                else
                {
                    /*
                     * Parallel segments.
                     */
                    firstT =
                        0f;
                }

                secondT =
                    (
                        directionsDot *
                        firstT +
                        secondProjection
                    ) /
                    secondLengthSquared;

                if (secondT < 0f)
                {
                    secondT =
                        0f;

                    firstT =
                        Math.Clamp(
                            -firstProjection /
                            firstLengthSquared,
                            0f,
                            1f);
                }
                else if (secondT > 1f)
                {
                    secondT =
                        1f;

                    firstT =
                        Math.Clamp(
                            (
                                directionsDot -
                                firstProjection
                            ) /
                            firstLengthSquared,
                            0f,
                            1f);
                }
            }
        }

        firstPoint =
            firstStart +
            firstDirection *
            firstT;

        secondPoint =
            secondStart +
            secondDirection *
            secondT;
    }

    #endregion

    #region General Helpers

    private static CollisionManifold2D Invert(
        CollisionManifold2D manifold)
    {
        return new CollisionManifold2D(
            -manifold.Normal,
            manifold.Penetration);
    }

    private static bool TryNormalize(
        Vector2 vector,
        out Vector2 normalized)
    {
        var lengthSquared =
            vector.LengthSquared();

        if (lengthSquared <=
            EpsilonSquared)
        {
            normalized =
                Vector2.Zero;

            return false;
        }

        normalized =
            vector /
            MathF.Sqrt(
                lengthSquared);

        return true;
    }

    #endregion
}