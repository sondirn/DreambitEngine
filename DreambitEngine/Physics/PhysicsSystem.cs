using System.Collections.Generic;
using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit;

public class PhysicsSystem : Singleton<PhysicsSystem>
{
    private readonly List<Collider> _candidateList =
        new(256);

    private readonly HashSet<Collider> _candidateSet =
        new(256);

    private readonly SpatialHash _grid =
        new(6f);

    #region Registration

    public void RegisterCollider(
        Collider collider)
    {
        if (collider == null ||
            !collider.HasCollisionGeometry ||
            !collider.IsQueryable)
        {
            return;
        }

        _grid.InsertOrUpdate(
            collider,
            collider.AABB);
    }

    public void DeregisterCollider(
        Collider collider)
    {
        if (collider == null)
            return;

        _grid.Remove(collider);
    }

    public void Touch(
        Collider collider)
    {
        if (collider == null ||
            !collider.HasCollisionGeometry ||
            !collider.IsQueryable)
        {
            return;
        }

        _grid.InsertOrUpdate(
            collider,
            collider.AABB);
    }

    public void CleanUp()
    {
        _candidateList.Clear();
        _candidateSet.Clear();
        _grid.Clear();
    }

    #endregion

    #region Collider Cast

    public bool ColliderCast(
        Collider collider,
        out CollisionResult result)
    {
        result = new CollisionResult();

        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool ColliderCastByTag(
        Collider collider,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    internal bool ColliderCastAny(
        Collider collider)
    {
        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                return true;
            }
        }

        return false;
    }

    internal bool ColliderCastAnyByTag(
        Collider collider,
        HashSet<string> tags)
    {
        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (!HasAnyTag(
                    other.Entity,
                    tags))
            {
                continue;
            }

            if (Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                return true;
            }
        }

        return false;
    }

    internal bool CollectColliderOverlaps(
        Collider collider,
        HashSet<Collider> destination)
    {
        destination.Clear();

        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            destination.Add(other);
        }

        return destination.Count > 0;
    }

    internal bool CollectColliderOverlapsByTag(
        Collider collider,
        HashSet<Collider> destination,
        IReadOnlyList<string> tags)
    {
        destination.Clear();

        if (!IsColliderValid(collider))
            return false;

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            destination.Add(other);
        }

        return destination.Count > 0;
    }

    #endregion

    #region Polygon Cast

    public bool PolygonCast(
        Polygon2D polygon,
        out CollisionResult result)
    {
        result = new CollisionResult();

        var geometry =
            ColliderGeometry2D
                .FromPolygon(polygon);

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool PolygonCastByTag(
        Polygon2D polygon,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        var geometry =
            ColliderGeometry2D
                .FromPolygon(polygon);

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    #endregion

    #region Point Cast

    public bool PointCast(
        Vector2 point,
        out CollisionResult result)
    {
        result = new CollisionResult();

        _candidateList.Clear();

        _grid.QueryPoint(
            point,
            _candidateList);

        for (var index = 0;
             index < _candidateList.Count;
             index++)
        {
            var other =
                _candidateList[index];

            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.ContainsPoint(
                    other.WorldGeometry2D,
                    point))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool PointCastByTag(
        Vector2 point,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        _candidateList.Clear();

        _grid.QueryPoint(
            point,
            _candidateList);

        for (var index = 0;
             index < _candidateList.Count;
             index++)
        {
            var other =
                _candidateList[index];

            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.ContainsPoint(
                    other.WorldGeometry2D,
                    point))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    #endregion

    #region Ray Cast

    public bool RayCast(
        Ray2D ray,
        out CollisionResult result)
    {
        result = new CollisionResult();

        _candidateList.Clear();

        _grid.QueryRay(
            ray.Start,
            ray.End,
            _candidateList);

        _candidateSet.Clear();

        for (var index = 0;
             index < _candidateList.Count;
             index++)
        {
            _candidateSet.Add(
                _candidateList[index]);
        }

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.IntersectsRay(
                    other.WorldGeometry2D,
                    ray))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool RayCastByTag(
        Ray2D ray,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        _candidateList.Clear();

        _grid.QueryRay(
            ray.Start,
            ray.End,
            _candidateList);

        _candidateSet.Clear();

        for (var index = 0;
             index < _candidateList.Count;
             index++)
        {
            _candidateSet.Add(
                _candidateList[index]);
        }

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.IntersectsRay(
                    other.WorldGeometry2D,
                    ray))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    #endregion

    #region Circle Cast

    public bool CircleCast(
        Vector2 center,
        float radius,
        out CollisionResult result,
        AABB? aabb = null)
    {
        result = new CollisionResult();

        if (!float.IsFinite(radius) ||
            radius <= 0f)
        {
            return false;
        }

        var geometry =
            ColliderGeometry2D.FromCircle(
                new Circle2D(
                    center,
                    radius));

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            aabb ?? geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool CircleCastByTag(
        Vector2 center,
        float radius,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        if (!float.IsFinite(radius) ||
            radius <= 0f)
        {
            return false;
        }

        var geometry =
            ColliderGeometry2D.FromCircle(
                new Circle2D(
                    center,
                    radius));

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    #endregion

    #region Capsule Cast

    public bool CapsuleCast(
        Vector2 start,
        Vector2 end,
        float radius,
        out CollisionResult result)
    {
        result = new CollisionResult();

        if (!float.IsFinite(radius) ||
            radius <= 0f)
        {
            return false;
        }

        var geometry =
            ColliderGeometry2D.FromCapsule(
                new Capsule2D(
                    start,
                    end,
                    radius));

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }

    public bool CapsuleCastByTag(
        Vector2 start,
        Vector2 end,
        float radius,
        out CollisionResult result,
        IReadOnlyList<string> tags)
    {
        result = new CollisionResult();

        if (!float.IsFinite(radius) ||
            radius <= 0f)
        {
            return false;
        }

        var geometry =
            ColliderGeometry2D.FromCapsule(
                new Capsule2D(
                    start,
                    end,
                    radius));

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            geometry.Aabb,
            _candidateSet);

        foreach (var other in _candidateSet)
        {
            if (!IsColliderValid(other))
                continue;

            if (!other.Entity.HasAnyTag(tags))
                continue;

            if (!Collision2D.Intersects(
                    geometry,
                    other.WorldGeometry2D))
            {
                continue;
            }

            result.Collisions.Add(other);
        }

        return result.Collisions.Count > 0;
    }
    
    internal bool TryGetBestSolidContact(
        Collider collider,
        HashSet<string> interestedTags,
        out Vector2 normal,
        out float penetration)
    {
        const float penetrationEpsilon =
            0.000001f;

        normal =
            Vector2.Zero;

        penetration =
            0f;

        if (!IsColliderValid(collider) ||
            collider.IsTrigger)
        {
            return false;
        }

        var geometry =
            collider.WorldGeometry2D;

        if (!geometry.IsValid)
            return false;

        _candidateSet.Clear();

        _grid.QueryAABB(
            collider.AABB,
            _candidateSet);

        var found =
            false;

        foreach (var other in _candidateSet)
        {
            if (ReferenceEquals(
                    other,
                    collider))
            {
                continue;
            }

            if (!IsColliderValid(other))
                continue;

            /*
             * A collider should not physically block another collider
             * belonging to the same entity.
             */
            if (ReferenceEquals(
                    other.Entity,
                    collider.Entity))
            {
                continue;
            }

            /*
             * Triggers participate in overlap/cast queries but must not
             * physically stop a RigidBody2D.
             */
            if (other.IsTrigger)
                continue;

            if (interestedTags is
                    { Count: > 0 } &&
                !HasAnyTag(
                    other.Entity,
                    interestedTags))
            {
                continue;
            }

            if (!CollisionManifoldSolver2D
                    .TryGetManifold(
                        geometry,
                        other.WorldGeometry2D,
                        out var manifold))
            {
                continue;
            }

            /*
             * Touching without penetration is a valid geometric contact,
             * but there is nothing to depenetrate.
             */
            if (!float.IsFinite(
                    manifold.Penetration) ||
                manifold.Penetration <=
                penetrationEpsilon)
            {
                continue;
            }

            var normalLengthSquared =
                manifold.Normal
                    .LengthSquared();

            if (!float.IsFinite(
                    normalLengthSquared) ||
                normalLengthSquared <=
                penetrationEpsilon *
                penetrationEpsilon)
            {
                continue;
            }

            /*
             * Resolve the deepest contact first.
             *
             * RigidBody2D repeats this query after each correction, so
             * corners and multiple simultaneous contacts settle iteratively.
             */
            if (found &&
                manifold.Penetration <=
                penetration)
            {
                continue;
            }

            found =
                true;

            normal =
                manifold.Normal;

            penetration =
                manifold.Penetration;
        }

        return found;
    }

    #endregion

    #region Helpers

    private static bool IsColliderValid(
        Collider collider)
    {
        return collider != null &&
               collider.HasCollisionGeometry &&
               collider.IsQueryable &&
               collider.Enabled &&
               collider.Entity?.Enabled == true;
    }

    private static bool HasAnyTag(
        Entity entity,
        HashSet<string> tags)
    {
        if (entity == null ||
            tags == null ||
            tags.Count == 0)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (entity.Tags.Contains(tag))
                return true;
        }

        return false;
    }

    #endregion
}

public readonly struct CollisionResult()
{
    public List<Collider> Collisions { get; } = [];

    public int Count =>
        Collisions.Count;

    public Collider First =>
        Collisions[0];

    public Collider this[int key] =>
        Collisions[key];
}