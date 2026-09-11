using Dreambit.EditorApi;
using Vector2 = System.Numerics.Vector2;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Dreambit.Editor.Inspection;

/// <summary>Box2D uses factories and has no publicly writable nested properties.</summary>
internal sealed class Box2DValueDrawer : IInspectorValueDrawer
{
    public int Priority => 70;

    public bool CanDraw(Type type) => type == typeof(Box2D);

    public InspectorValueDrawResult Draw(
        InspectorValueDrawerRegistry registry,
        string label,
        Type type,
        object? value,
        InspectorValueDrawContext context)
    {
        if (value is not Box2D box)
        {
            var create = EditorGui.CustomProperty(
                context.Id,
                label,
                () => EditorGui.Button("Create", "Create", primary: true, enabled: !context.ReadOnly),
                readOnly: context.ReadOnly,
                tooltip: context.Metadata.Tooltip);
            return create && !context.ReadOnly
                ? new InspectorValueDrawResult(true, CreateBox(Vector2.Zero, Vector2.One))
                : InspectorValueDrawResult.Unchanged(value);
        }

        using var group = EditorGui.CollapsibleGroup(
            context.Id, label, defaultOpen: true, tooltip: context.Metadata.Tooltip);
        if (!group.IsOpen)
            return InspectorValueDrawResult.Unchanged(value);

        var (center, size) = GetDimensions(box);
        var changed = EditorGui.Property(
            $"{context.Id}.Center", "Center", ref center, speed: 0.05f,
            mixed: context.Mixed, readOnly: context.ReadOnly,
            tooltip: "Box center relative to the entity.");
        changed = EditorGui.Property(
            $"{context.Id}.Size", "Size", ref size, speed: 0.05f,
            min: 0.001f, max: float.MaxValue,
            mixed: context.Mixed, readOnly: context.ReadOnly,
            tooltip: "Full width and height in local units.") || changed;

        // Return a replacement so the inspector can record undo and serialize the edit.
        return changed && !context.ReadOnly
            ? new InspectorValueDrawResult(true, CreateBox(center, size))
            : InspectorValueDrawResult.Unchanged(value);
    }

    internal static Box2D CreateBox(Vector2 center, Vector2 size) =>
        Box2D.CreateRectangle(
            new XnaVector2(center.X, center.Y),
            Math.Max(0.001f, size.X) * 0.5f,
            Math.Max(0.001f, size.Y) * 0.5f);

    internal static (Vector2 Center, Vector2 Size) GetDimensions(Box2D box)
    {
        var min = box.TopLeft;
        var max = min;
        foreach (var vertex in box.GetVertices())
        {
            min = XnaVector2.Min(min, vertex);
            max = XnaVector2.Max(max, vertex);
        }

        var center = (min + max) * 0.5f;
        var size = max - min;
        return (new Vector2(center.X, center.Y), new Vector2(size.X, size.Y));
    }
}
