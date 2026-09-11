using System;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType($"{nameof(PointLight2D)}")]
public class PointLight2D : Light2D
{
    [DreambitSerialize]
    public float Radius { get; set; } = 1f;

    public override RectangleF Bounds
    {
        get
        {
            var r =
                MathF.Max(
                    0f,
                    Radius);

            var left =
                Position.X - r;

            var top =
                Position.Y - r;

            var size =
                r * 2f;

            return new RectangleF(
                left,
                top,
                size,
                size);
        }
    }

    public override void OnDebugDraw()
    {
        Core.SpriteBatch.DrawHollowRectangle(
            Bounds,
            Color.White, Scene.Instance.MainCamera.WorldUnitsPerScreenPixel);
    }

    public override void OnEditorDrawGizmos(
        IEditorGizmoContext context)
    {
        context.PickableIcon(
            this,
            "light_mode",
            Position,
            Color,
            24f);
    }

    public override void OnEditorDrawGizmosSelected(
        IEditorGizmoContext context)
    {
        var color =
            new Color(
                255,
                196,
                64,
                230);

        context.RadiusHandle(
            this,
            nameof(Radius),
            Position,
            color,
            2f);
    }
}