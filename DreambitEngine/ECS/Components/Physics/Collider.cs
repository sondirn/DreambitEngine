using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

/// <summary>
/// Physics collider component. Can act as a trigger, participate in spatial queries,
/// and raise collision callbacks (enter/stay/exit).
/// </summary>
[BlueprintType(nameof(Collider))]
public class Collider : Component
{
    #region Debug

    public override void OnEditorDrawGizmos(
        IEditorGizmoContext context)
    {
    }

    public override void OnEditorDrawGizmosSelected(
        IEditorGizmoContext context)
    {
        DrawEditorOutline(
            context,
            new Color(
                82,
                235,
                140,
                150),
            1.5f);
    }

    public override void OnDebugDraw()
    {
        var geometry =
            WorldGeometry2D;

        if (!geometry.IsValid)
            return;

        var thickness =
            Scene.Instance.MainCamera
                .WorldUnitsPerScreenPixel;

        switch (geometry.Kind)
        {
            case ColliderGeometryKind.Polygon:
                Core.SpriteBatch.DrawPolygon(
                    geometry.Polygon.Vertices,
                    Color.White,
                    thickness);

                break;

            case ColliderGeometryKind.Circle:
                Core.SpriteBatch.DrawCircle(
                    geometry.Circle.Center,
                    geometry.Circle.Radius,
                    Color.White,
                    32,
                    thickness);

                break;

            case ColliderGeometryKind.Capsule:
                DrawDebugCapsule(
                    geometry.Capsule,
                    Color.White,
                    thickness);

                break;
        }
    }

    private void DrawEditorOutline(
        IEditorGizmoContext context,
        Color color,
        float thickness)
    {
        var geometry =
            WorldGeometry2D;

        if (!geometry.IsValid)
            return;

        switch (geometry.Kind)
        {
            case ColliderGeometryKind.Polygon:
            {
                var vertices =
                    geometry.Polygon.Vertices;

                if (vertices is not { Length: >= 2 })
                    return;

                for (var index = 0;
                     index < vertices.Length;
                     index++)
                {
                    context.Line(
                        vertices[index],
                        vertices[
                            (index + 1) %
                            vertices.Length],
                        color,
                        thickness);
                }

                break;
            }

            case ColliderGeometryKind.Circle:
                context.Circle(
                    geometry.Circle.Center,
                    geometry.Circle.Radius,
                    color,
                    thickness);

                break;

            case ColliderGeometryKind.Capsule:
                DrawEditorCapsule(
                    context,
                    geometry.Capsule,
                    color,
                    thickness);

                break;
        }
    }

    private static void DrawEditorCapsule(
        IEditorGizmoContext context,
        Capsule2D capsule,
        Color color,
        float thickness)
    {
        var axis =
            capsule.End -
            capsule.Start;

        var length =
            axis.Length();

        if (length <= Mathf.Epsilon)
        {
            context.Circle(
                capsule.Start,
                capsule.Radius,
                color,
                thickness);

            return;
        }

        var normal =
            new Vector2(
                -axis.Y,
                axis.X) /
            length;

        var offset =
            normal *
            capsule.Radius;

        context.Line(
            capsule.Start + offset,
            capsule.End + offset,
            color,
            thickness);

        context.Line(
            capsule.Start - offset,
            capsule.End - offset,
            color,
            thickness);

        context.Circle(
            capsule.Start,
            capsule.Radius,
            color,
            thickness);

        context.Circle(
            capsule.End,
            capsule.Radius,
            color,
            thickness);
    }

    private static void DrawDebugCapsule(
        Capsule2D capsule,
        Color color,
        float thickness)
    {
        var axis =
            capsule.End -
            capsule.Start;

        var length =
            axis.Length();

        if (length <= Mathf.Epsilon)
        {
            Core.SpriteBatch.DrawCircle(
                capsule.Start,
                capsule.Radius,
                color,
                32,
                thickness);

            return;
        }

        var normal =
            new Vector2(
                -axis.Y,
                axis.X) /
            length;

        var offset =
            normal *
            capsule.Radius;

        Core.SpriteBatch.DrawLine(
            capsule.Start + offset,
            capsule.End + offset,
            color,
            thickness);

        Core.SpriteBatch.DrawLine(
            capsule.Start - offset,
            capsule.End - offset,
            color,
            thickness);

        Core.SpriteBatch.DrawCircle(
            capsule.Start,
            capsule.Radius,
            color,
            32,
            thickness);

        Core.SpriteBatch.DrawCircle(
            capsule.End,
            capsule.Radius,
            color,
            32,
            thickness);
    }

    #endregion

    #region Trigger Collision Checks

    private void CheckForTriggerCollisions()
    {
        if (InterestedIn.Count == 0)
        {
            PhysicsSystem.Instance
                .CollectColliderOverlaps(
                    this,
                    _overlapsCurr);
        }
        else
        {
            PhysicsSystem.Instance
                .CollectColliderOverlapsByTag(
                    this,
                    _overlapsCurr,
                    InterestedIn);
        }

        foreach (var collider in _overlapsCurr)
        {
            if (!_overlapsPrev.Contains(collider))
                OnCollisionEnter?.Invoke(collider);
        }

        foreach (var collider in _overlapsPrev)
        {
            if (!_overlapsCurr.Contains(collider))
                OnCollisionExit?.Invoke(collider);
        }

        foreach (var collider in _overlapsCurr)
            OnCollisionStay?.Invoke(collider);

        _overlapsPrev.Clear();

        foreach (var collider in _overlapsCurr)
            _overlapsPrev.Add(collider);
    }

    #endregion

    #region Collision Casting Checks

    public virtual void ColliderCast(
        out CollisionResult hits)
    {
        PhysicsSystem.Instance.ColliderCast(
            this,
            out hits);
    }

    public virtual void ColliderCastByTags(
        out CollisionResult hits,
        params string[] tags)
    {
        PhysicsSystem.Instance.ColliderCastByTag(
            this,
            out hits,
            tags);
    }

    #endregion

    #region Broadphase / Spatial Hash Participation

    internal void RefreshSpatialHash()
    {
        if (!_isAttached ||
            !Enabled ||
            !IsQueryable ||
            !HasCollisionGeometry)
        {
            DeregisterFromPhysics();
            return;
        }

        var previousAabb =
            AABB;

        var geometryChanged =
            EnsureWorldGeometry();

        if (!_hasCachedWorldGeometry)
        {
            DeregisterFromPhysics();
            return;
        }

        /*
         * EnsureWorldGeometry() calls SetAabb() whenever geometry is rebuilt.
         * Preserve the existing virtual SetAabb contract even when the cached
         * geometry itself did not need rebuilding.
         */
        if (!geometryChanged)
            SetAabb();

        var aabbChanged =
            !AabbEquals(
                previousAabb,
                AABB);

        if (!_isRegistered)
        {
            PhysicsSystem.Instance
                .RegisterCollider(this);

            _isRegistered = true;
            return;
        }

        if (geometryChanged ||
            aabbChanged)
        {
            PhysicsSystem.Instance
                .Touch(this);
        }
    }

    protected virtual void SetAabb()
    {
        if (!_hasCachedWorldGeometry ||
            !_cachedWorldGeometry.IsValid)
        {
            AABB = default;
            return;
        }

        AABB =
            _cachedWorldGeometry.Aabb;
    }

    /// <summary>
    /// Whether the authored collider currently contains valid local geometry.
    /// Specialized collider types override this instead of requiring Bounds.
    /// </summary>
    protected virtual bool HasLocalGeometry =>
        Bounds?.GetVertices()
            is { Length: >= 3 };

    /// <summary>
    /// Hash of authored local geometry used to detect in-place changes.
    /// </summary>
    protected virtual int GetLocalGeometryHash()
    {
        return Bounds == null
            ? 0
            : ComputeShapeHash(
                Bounds.GetVertices());
    }

    /// <summary>
    /// Builds world-space geometry from local authored state.
    ///
    /// previousGeometry is supplied so polygon colliders can preserve and
    /// reuse their transformed vertex array rather than allocating each step.
    /// </summary>
    protected virtual ColliderGeometry2D BuildWorldGeometry(
        Matrix worldMatrix,
        ColliderGeometry2D previousGeometry)
    {
        var localVertices =
            Bounds.GetVertices();

        var worldPolygon =
            previousGeometry.Kind ==
            ColliderGeometryKind.Polygon
                ? previousGeometry.Polygon
                : default;

        var worldVertices =
            worldPolygon.Vertices;

        if (worldVertices == null ||
            worldVertices.Length !=
            localVertices.Length)
        {
            worldVertices =
                new Vector2[
                    localVertices.Length];

            worldPolygon =
                new Polygon2D
                {
                    Vertices =
                        worldVertices
                };
        }

        for (var index = 0;
             index < localVertices.Length;
             index++)
        {
            var point =
                new Vector3(
                    localVertices[index],
                    0f);

            var transformed =
                Vector3.Transform(
                    point,
                    worldMatrix);

            worldVertices[index] =
                new Vector2(
                    transformed.X,
                    transformed.Y);
        }

        return ColliderGeometry2D
            .FromPolygon(worldPolygon);
    }

    private bool EnsureWorldGeometry()
    {
        if (!HasLocalGeometry)
        {
            _cachedWorldGeometry =
                default;

            _hasCachedWorldGeometry =
                false;

            _worldGeometryDirty =
                true;

            AABB =
                default;

            return false;
        }

        var worldMatrix =
            Transform.WorldMatrix;

        var shapeHash =
            GetLocalGeometryHash();

        var needsRebuild =
            !_hasCachedWorldGeometry ||
            _worldGeometryDirty ||
            !_cachedWorldMatrix.Equals(
                worldMatrix) ||
            _cachedShapeHash !=
            shapeHash;

        if (!needsRebuild)
            return false;

        try
        {
            _cachedWorldGeometry =
                BuildWorldGeometry(
                    worldMatrix,
                    _cachedWorldGeometry);

            _cachedWorldMatrix =
                worldMatrix;

            _cachedShapeHash =
                shapeHash;

            _hasCachedWorldGeometry =
                _cachedWorldGeometry.IsValid;

            _worldGeometryDirty =
                false;

            if (!_hasCachedWorldGeometry)
            {
                AABB = default;
                return true;
            }

            SetAabb();

            return true;
        }
        catch
        {
            _worldGeometryDirty =
                true;

            throw;
        }
    }

    private void InvalidateWorldGeometry()
    {
        _worldGeometryDirty =
            true;
    }

    /// <summary>
    /// Call from specialized collider property setters after local geometry
    /// has changed.
    /// </summary>
    protected void NotifyGeometryChanged()
    {
        InvalidateWorldGeometry();
        RefreshSpatialHash();
    }

    protected static Vector2 TransformPointToWorld(
        Vector2 point,
        Matrix worldMatrix)
    {
        var transformed =
            Vector3.Transform(
                new Vector3(
                    point,
                    0f),
                worldMatrix);

        return new Vector2(
            transformed.X,
            transformed.Y);
    }

    /// <summary>
    /// Circles and capsules remain mathematically circles/capsules only under
    /// uniform, non-sheared scaling.
    /// </summary>
    protected static float ResolveUniformWorldScale(
        Matrix worldMatrix,
        string colliderType)
    {
        var xAxis =
            new Vector2(
                worldMatrix.M11,
                worldMatrix.M12);

        var yAxis =
            new Vector2(
                worldMatrix.M21,
                worldMatrix.M22);

        var scaleX =
            xAxis.Length();

        var scaleY =
            yAxis.Length();

        if (!float.IsFinite(scaleX) ||
            !float.IsFinite(scaleY))
        {
            throw new InvalidOperationException(
                $"{colliderType} encountered a non-finite world transform.");
        }

        if (scaleX <= Mathf.Epsilon &&
            scaleY <= Mathf.Epsilon)
        {
            return 0f;
        }

        var scaleTolerance =
            0.0001f *
            MathF.Max(
                1f,
                MathF.Max(
                    scaleX,
                    scaleY));

        if (MathF.Abs(
                scaleX -
                scaleY) >
            scaleTolerance)
        {
            throw new InvalidOperationException(
                $"{colliderType} requires uniform world scale. " +
                $"Current world scale is approximately " +
                $"({scaleX:0.####}, {scaleY:0.####}).");
        }

        var orthogonalityTolerance =
            0.0001f *
            MathF.Max(
                1f,
                scaleX * scaleY);

        if (MathF.Abs(
                Vector2.Dot(
                    xAxis,
                    yAxis)) >
            orthogonalityTolerance)
        {
            throw new InvalidOperationException(
                $"{colliderType} does not support a sheared world transform.");
        }

        return (
                   scaleX +
                   scaleY) *
               0.5f;
    }

    private static int ComputeShapeHash(
        Vector2[] vertices)
    {
        unchecked
        {
            var hash = 17;

            hash =
                hash * 31 +
                vertices.Length;

            for (var index = 0;
                 index < vertices.Length;
                 index++)
            {
                hash =
                    hash * 31 +
                    BitConverter.SingleToInt32Bits(
                        vertices[index].X);

                hash =
                    hash * 31 +
                    BitConverter.SingleToInt32Bits(
                        vertices[index].Y);
            }

            return hash;
        }
    }

    #endregion

    #region Flags & Configuration

    [DreambitSerialize]
    public bool IsTrigger { get; set; }

    [DreambitSerialize]
    public bool IsSilent { get; set; }

    [DreambitSerialize]
    public bool IsQueryable
    {
        get => _isQueryable;

        set
        {
            if (_isQueryable == value)
                return;

            _isQueryable = value;
            RefreshSpatialHash();
        }
    }

    [DreambitSerialize]
    public List<string> InterestedIn = [];

    #endregion

    #region Events / Callbacks

    public event Action<Collider>? OnCollisionEnter;

    public event Action<Collider>? OnCollisionStay;
    public event Action<Collider>? OnCollisionExit;

    #endregion

    #region Bounds & Shape

    /// <summary>
    /// Polygon-backed collider shape.
    ///
    /// Specialized native collider types such as CircleCollider and
    /// CapsuleCollider do not require Bounds.
    /// </summary>
    [DreambitSerialize]
    public Shape2D Bounds
    {
        get => _bounds;

        set
        {
            if (ReferenceEquals(
                    _bounds,
                    value))
            {
                return;
            }

            _bounds = value;

            NotifyGeometryChanged();
        }
    }

    public AABB AABB { get; set; }

    /// <summary>
    /// Native world-space collider geometry.
    /// </summary>
    public ColliderGeometry2D WorldGeometry2D
    {
        get
        {
            EnsureWorldGeometry();

            return _hasCachedWorldGeometry
                ? _cachedWorldGeometry
                : default;
        }
    }

    /// <summary>
    /// True when this collider has usable authored collision geometry.
    /// </summary>
    public bool HasCollisionGeometry =>
        HasLocalGeometry;

    /// <summary>
    /// Backwards-compatible polygon access.
    ///
    /// CircleCollider and CapsuleCollider return an empty/default polygon.
    /// New generic physics code should use WorldGeometry2D.
    /// </summary>
    public Polygon2D WorldPolygon2D =>
        GetTransformedPolygon();

    #endregion

    #region Internal State

    private Shape2D _bounds;

    private bool _isAttached;
    private bool _isQueryable = true;
    private bool _isRegistered;

    private ColliderGeometry2D
        _cachedWorldGeometry;

    private Matrix
        _cachedWorldMatrix;

    private int
        _cachedShapeHash;

    private bool
        _hasCachedWorldGeometry;

    private bool
        _worldGeometryDirty = true;

    private readonly HashSet<Collider>
        _overlapsPrev = [];

    private readonly HashSet<Collider>
        _overlapsCurr = [];

    #endregion

    #region Lifecycle Overrides

    public override void OnAddedToEntity()
    {
        _isAttached = true;

        RefreshSpatialHash();

        Transform.CaptureLastWorldPosition();
    }

    public override void OnDestroyed()
    {
        _isAttached = false;

        DeregisterFromPhysics();

        OnCollisionEnter = null;
        OnCollisionStay = null;
        OnCollisionExit = null;

        _overlapsPrev.Clear();
        _overlapsCurr.Clear();

        InvalidateWorldGeometry();
    }

    public override void OnRemovedFromEntity()
    {
        _isAttached = false;

        DeregisterFromPhysics();

        _overlapsPrev.Clear();
        _overlapsCurr.Clear();
    }

    public override void OnDisabled()
    {
        DeregisterFromPhysics();
    }

    public override void OnEnabled()
    {
        RefreshSpatialHash();

        Transform.CaptureLastWorldPosition();
    }

    public override void OnUpdate()
    {
        if (IsTrigger &&
            HasCollisionGeometry &&
            !IsSilent)
        {
            CheckForTriggerCollisions();
        }
    }

    public override void OnPhysicsUpdate()
    {
        RefreshSpatialHash();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Backwards-compatible polygon access.
    /// </summary>
    public Polygon2D GetTransformedPolygon()
    {
        var geometry =
            WorldGeometry2D;

        return geometry.Kind ==
               ColliderGeometryKind.Polygon
            ? geometry.Polygon
            : default;
    }

    /// <summary>
    /// Polygon-only speculative transform API retained for compatibility.
    /// </summary>
    public Polygon2D GetTransformedPolyWithDesiredPos(
        Vector3 desiredPos)
    {
        if (Bounds == null)
            return default;

        return Bounds.TransformWithDesiredPos(
            Transform,
            desiredPos);
    }

    private void DeregisterFromPhysics()
    {
        if (!_isRegistered)
            return;

        PhysicsSystem.Instance
            .DeregisterCollider(this);

        _isRegistered =
            false;
    }

    private static bool AabbEquals(
        AABB left,
        AABB right)
    {
        return left.Min ==
               right.Min &&
               left.Max ==
               right.Max;
    }

    #endregion
}