using System;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType(nameof(CircleCollider))]
public class CircleCollider : Collider
{
    private Vector2 _center =
        Vector2.Zero;

    private float _radius =
        5f;

    /// <summary>
    /// Circle center in entity-local space.
    /// </summary>
    [DreambitSerialize]
    public Vector2 Center
    {
        get => _center;

        set
        {
            if (!float.IsFinite(value.X) ||
                !float.IsFinite(value.Y))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "CircleCollider center must be finite.");
            }

            if (_center == value)
                return;

            _center = value;

            NotifyGeometryChanged();
        }
    }

    /// <summary>
    /// Circle radius in entity-local world units.
    /// </summary>
    [DreambitSerialize]
    public float Radius
    {
        get => _radius;

        set
        {
            if (!float.IsFinite(value) ||
                value < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "CircleCollider radius must be finite and non-negative.");
            }

            if (_radius == value)
                return;

            _radius = value;

            NotifyGeometryChanged();
        }
    }

    protected override bool HasLocalGeometry =>
        float.IsFinite(_center.X) &&
        float.IsFinite(_center.Y) &&
        float.IsFinite(_radius) &&
        _radius > 0f;

    protected override int GetLocalGeometryHash()
    {
        unchecked
        {
            var hash = 17;

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _center.X);

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _center.Y);

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _radius);

            return hash;
        }
    }

    protected override ColliderGeometry2D BuildWorldGeometry(
        Matrix worldMatrix,
        ColliderGeometry2D previousGeometry)
    {
        var center =
            TransformPointToWorld(
                _center,
                worldMatrix);

        var scale =
            ResolveUniformWorldScale(
                worldMatrix,
                nameof(CircleCollider));

        return ColliderGeometry2D.FromCircle(
            new Circle2D(
                center,
                _radius * scale));
    }

    public override void OnEditorDrawGizmosSelected(IEditorGizmoContext context)
    {
        context.RadiusHandle(this, nameof(Radius),
            Transform.WorldPosition2D,
            new Color(82, 235, 140, 230), 1.5f);
    }
}