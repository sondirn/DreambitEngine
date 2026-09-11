using System;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType(nameof(CapsuleCollider))]
public class CapsuleCollider : Collider
{
    private Vector2 _start =
        new(
            0f,
            -5f);

    private Vector2 _end =
        new(
            0f,
            5f);

    private float _radius =
        5f;

    /// <summary>
    /// First center-line endpoint in entity-local space.
    /// </summary>
    [DreambitSerialize]
    public Vector2 Start
    {
        get => _start;

        set
        {
            ValidatePoint(
                value,
                nameof(Start));

            if (_start == value)
                return;

            _start = value;

            NotifyGeometryChanged();
        }
    }

    /// <summary>
    /// Second center-line endpoint in entity-local space.
    /// </summary>
    [DreambitSerialize]
    public Vector2 End
    {
        get => _end;

        set
        {
            ValidatePoint(
                value,
                nameof(End));

            if (_end == value)
                return;

            _end = value;

            NotifyGeometryChanged();
        }
    }

    /// <summary>
    /// Capsule radius in entity-local world units.
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
                    "CapsuleCollider radius must be finite and non-negative.");
            }

            if (_radius == value)
                return;

            _radius = value;

            NotifyGeometryChanged();
        }
    }

    protected override bool HasLocalGeometry =>
        float.IsFinite(_start.X) &&
        float.IsFinite(_start.Y) &&
        float.IsFinite(_end.X) &&
        float.IsFinite(_end.Y) &&
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
                    _start.X);

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _start.Y);

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _end.X);

            hash =
                hash * 31 +
                BitConverter.SingleToInt32Bits(
                    _end.Y);

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
        var start =
            TransformPointToWorld(
                _start,
                worldMatrix);

        var end =
            TransformPointToWorld(
                _end,
                worldMatrix);

        var scale =
            ResolveUniformWorldScale(
                worldMatrix,
                nameof(CapsuleCollider));

        return ColliderGeometry2D.FromCapsule(
            new Capsule2D(
                start,
                end,
                _radius * scale));
    }

    private static void ValidatePoint(
        Vector2 value,
        string parameterName)
    {
        if (float.IsFinite(value.X) &&
            float.IsFinite(value.Y))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            parameterName,
            "CapsuleCollider endpoints must be finite.");
    }
}