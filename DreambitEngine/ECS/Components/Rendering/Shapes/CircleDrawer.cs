using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType($"{nameof(CircleDrawer)}")]
public class CircleDrawer : DrawableComponent
{
    public override RectangleF Bounds => GetBounds();

    [DreambitSerialize] public Color Color { get; set; } = Color.White;
    [DreambitSerialize] public float Radius { get; set; } = 2f;
    [DreambitSerialize] public int Segments { get; set; } = 64;
    [DreambitSerialize] public float LineThickness { get; set; } = 1.0f;

    private RectangleF GetBounds()
    {
        var center =  Transform.WorldPosition2D;
        var topLeft = center - new Vector2(Radius);

        return new RectangleF(topLeft, Radius * 2);
    }
    

    protected override void OnDraw()
    {
        var center = Transform.WorldPosition2D;
        Core.SpriteBatch.DrawCircle(center, Radius, Color, Segments,
            LineThickness * Scene.MainCamera.WorldUnitsPerScreenPixel);
    }

    public override void OnEditorDrawGizmosSelected(IEditorGizmoContext context)
    {
        var center = Transform.WorldPosition2D;

        context.RadiusHandle(this, nameof(Radius), center, Color.Yellow, 1.5f);
    }
}
