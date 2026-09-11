using Dreambit.ECS;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Dreambit.Editor.UI.Viewport;

/// <summary>
/// Adapts runtime-neutral editor drawing primitives to the active ImGui viewport.
/// </summary>
internal sealed class ImGuiEditorGizmoContext(
    ImDrawListPtr drawList,
    Camera2D camera,
    Vector2 canvasPosition,
    EditorComponentGizmoSystem componentGizmos,
    EditorPickProxyBuffer pickProxies,
    EditorIconService icons) : IEditorGizmoContext
{
    public void Line(
        XnaVector2 from,
        XnaVector2 to,
        Color color,
        float thickness = 1f) =>
        drawList.AddLine(
            Screen(from),
            Screen(to),
            ColorU32(color),
            MathF.Max(1f, thickness));

    public void Circle(
        XnaVector2 center,
        float radius,
        Color color,
        float thickness = 1f) =>
        drawList.AddCircle(
            Screen(center),
            MathF.Abs(radius * camera.Scale),
            ColorU32(color),
            48,
            MathF.Max(1f, thickness));

    public void Rectangle(
        RectangleF rectangle,
        Color color,
        float thickness = 1f) =>
        drawList.AddRect(
            Screen(
                new XnaVector2(
                    rectangle.Left,
                    rectangle.Top)),
            Screen(
                new XnaVector2(
                    rectangle.Right,
                    rectangle.Bottom)),
            ColorU32(color),
            0f,
            ImDrawFlags.None,
            MathF.Max(1f, thickness));

    public void Label(
        XnaVector2 position,
        string text,
        Color color)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            drawList.AddText(
                Screen(position),
                ColorU32(color),
                text);
        }
    }

    public void ShowIcon(
        string icon,
        XnaVector2 position,
        Color color,
        float size = 24f)
    {
        TryDrawIcon(
            icon,
            position,
            color,
            size,
            out _);
    }

    public void PickableIcon(
        Component owner,
        string icon,
        XnaVector2 position,
        Color color,
        float size = 24f)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (owner.Entity is not { } entity ||
            !owner.Enabled ||
            !entity.Enabled)
        {
            return;
        }

        if (!TryDrawIcon(
                icon,
                position,
                color,
                size,
                out var screenCenter))
        {
            return;
        }

        pickProxies.RegisterIcon(
            entity,
            screenCenter,
            size);
    }

    public void RadiusHandle(
        Component component,
        string memberName,
        XnaVector2 center,
        Color color,
        float thickness = 1f)
    {
        componentGizmos.RegisterRadiusHandle(
            component,
            memberName,
            center,
            color,
            thickness);
    }

    public void BoxHandle(
        Component component,
        string memberName,
        Color color,
        float thickness = 1f)
    {
        componentGizmos.RegisterBoxHandle(
            component,
            memberName,
            color,
            thickness);
    }

    public void PolygonHandle(
        Component component,
        string memberName,
        Color color,
        float thickness = 1f)
    {
        componentGizmos.RegisterPolygonHandle(
            component,
            memberName,
            color,
            thickness);
    }

    private bool TryDrawIcon(
        string icon,
        XnaVector2 position,
        Color color,
        float size,
        out Vector2 screenCenter)
    {
        screenCenter = default;

        if (string.IsNullOrWhiteSpace(icon) ||
            !float.IsFinite(size) ||
            size <= 0f ||
            !icons.HasIcon(icon))
        {
            return false;
        }

        screenCenter = Screen(position);

        var iconSize = new Vector2(
            size,
            size);

        var minimum =
            screenCenter -
            iconSize * 0.5f;

        icons.DrawAt(
            drawList,
            icon,
            minimum,
            iconSize,
            new Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f));

        return true;
    }

    private Vector2 Screen(XnaVector2 world)
    {
        var screen = camera.WorldToScreen(world);

        return canvasPosition +
               new Vector2(
                   screen.X,
                   screen.Y);
    }

    private static uint ColorU32(Color color) =>
        ImGui.GetColorU32(
            new Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f));
}