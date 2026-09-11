using System;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType(nameof(PolyShapeCollider))]
public class PolyShapeCollider : Collider
{
    public override void OnCreated()
    {
        base.OnCreated();
        EnsurePolyBounds();
    }

    public override void OnEditorCreated()
    {
        EnsurePolyBounds();
    }

    public override void OnEditorDrawGizmosSelected(IEditorGizmoContext context)
    {
        context.PolygonHandle(
            this,
            nameof(Bounds),
            new Color(82, 235, 140, 230),
            1.5f);
    }

    public void SetShape(PolyShape2D shape2D)
    {
        ArgumentNullException.ThrowIfNull(shape2D);
        Bounds = shape2D;
    }

    private void EnsurePolyBounds()
    {
        if (Bounds is PolyShape2D)
            return;

        if (Bounds is null)
        {
            Bounds = PolyShape2D.Create(
                Box2D.CreateSquare(Vector2.Zero, 2.0f)
                    .GetVertices());

            return;
        }

        Bounds = PolyShape2D.Create(Bounds.GetVertices());
    }
}