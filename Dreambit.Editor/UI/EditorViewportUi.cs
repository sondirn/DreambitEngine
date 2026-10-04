using System.Numerics;
using Dreambit.ECS;
using Dreambit.Editor.Persistence;
using Dreambit.EditorApi;
using ImGuiNET;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Dreambit.Editor.UI;

internal static class EditorViewportUi
{
    public const float MinimumGridStep = 0.00001f;

    public const float MinimumZoom = 0.02f;

    public static float NormalizeZoom(float zoom) =>
        float.IsFinite(zoom) && zoom >= MinimumZoom ? zoom : MinimumZoom;

    public static float ApplyZoomWheel(float zoom, float wheel)
    {
        var current = NormalizeZoom(zoom);
        var next = current * MathF.Pow(1.15f, wheel);
        return float.IsFinite(next) ? MathF.Max(MinimumZoom, next) : current;
    }

    public static float NormalizeGridSize(float gridSize) =>
        float.IsFinite(gridSize) ? MathF.Max(MinimumGridStep, gridSize) : 1f;

    public static void DrawSettingsPopup(string id, EditorWorkspaceState workspace)
    {
        using var popup = EditorGui.Popup(id);
        if (!popup.IsOpen)
            return;

        EditorGui.Header("Scene View", "Grid and transform snapping");

        var showGrid = workspace.ShowGrid;
        if (EditorGui.Property("Viewport.ShowGrid", "Show Grid", ref showGrid))
            workspace.ShowGrid = showGrid;

        var gridSize = NormalizeGridSize(workspace.GridSize);
        if (EditorGui.Property(
                "Viewport.GridSize",
                "Grid Size",
                ref gridSize,
                speed: 0.05f,
                min: MinimumGridStep,
                format: "%.5f",
                tooltip: "World units between grid lines. Ctrl+click to type an exact value."))
            workspace.GridSize = NormalizeGridSize(gridSize);

        EditorGui.Space(EditorGuiSpacing.Compact);
        var snap = workspace.SnapEnabled;
        if (EditorGui.Property("Viewport.SnapEnabled", "Enable Snapping", ref snap))
            workspace.SnapEnabled = snap;

        using (EditorGui.Disabled(!workspace.SnapEnabled))
        {
            var moveSnap = workspace.MoveSnap;
            if (DrawPositiveSetting(
                    "Viewport.MoveSnap", "Move Step", ref moveSnap, 0.05f, MinimumGridStep, "%.5f"))
                workspace.MoveSnap = moveSnap;
            var rotateSnap = workspace.RotateSnapDegrees;
            if (DrawPositiveSetting(
                    "Viewport.RotateSnap", "Rotation Step", ref rotateSnap, 1f, 0.1f, "%.1f deg"))
                workspace.RotateSnapDegrees = rotateSnap;
            var scaleSnap = workspace.ScaleSnap;
            if (DrawPositiveSetting(
                    "Viewport.ScaleSnap", "Scale Step", ref scaleSnap, 0.01f, MinimumGridStep, "%.5f"))
                workspace.ScaleSnap = scaleSnap;
        }

        if (EditorGui.FullWidthButton("Viewport.Reset", "Reset View Settings"))
        {
            workspace.ShowGrid = true;
            workspace.GridSize = 1f;
            workspace.SnapEnabled = false;
            workspace.MoveSnap = 1f;
            workspace.RotateSnapDegrees = 15f;
            workspace.ScaleSnap = 0.1f;
        }
    }

    public static void DrawGrid(
        ImDrawListPtr drawList,
        Camera2D camera,
        Vector2 canvasPosition,
        Vector2 canvasSize,
        float configuredGridSize)
    {
        var bounds = camera.BoundsF;
        var step = NormalizeGridSize(configuredGridSize);
        var pixelsPerStep = MathF.Abs(step * camera.Scale);
        while (float.IsFinite(pixelsPerStep) && pixelsPerStep < 12f && step < 1e30f)
        {
            step *= 10f;
            pixelsPerStep *= 10f;
        }

        if (!float.IsFinite(step) || step <= 0f ||
            !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Right) ||
            !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Bottom))
        {
            return;
        }

        var minimumX = MathF.Floor(bounds.Left / step) * step;
        var minimumY = MathF.Floor(bounds.Top / step) * step;
        var color = ImGui.GetColorU32(EditorGuiTheme.Grid);
        var axisColor = ImGui.GetColorU32(EditorGuiTheme.GridAxis);

        var lineCount = 0;
        for (var worldX = minimumX;
             worldX <= bounds.Right && lineCount++ < 4096;
             worldX += step)
        {
            var screen = camera.WorldToScreen(new XnaVector2(worldX, 0));
            var x = canvasPosition.X + screen.X;
            if (x < canvasPosition.X || x > canvasPosition.X + canvasSize.X)
                continue;
            drawList.AddLine(
                new Vector2(x, canvasPosition.Y),
                new Vector2(x, canvasPosition.Y + canvasSize.Y),
                MathF.Abs(worldX) < step * 0.001f ? axisColor : color);
        }

        lineCount = 0;
        for (var worldY = minimumY;
             worldY <= bounds.Bottom && lineCount++ < 4096;
             worldY += step)
        {
            var screen = camera.WorldToScreen(new XnaVector2(0, worldY));
            var y = canvasPosition.Y + screen.Y;
            if (y < canvasPosition.Y || y > canvasPosition.Y + canvasSize.Y)
                continue;
            drawList.AddLine(
                new Vector2(canvasPosition.X, y),
                new Vector2(canvasPosition.X + canvasSize.X, y),
                MathF.Abs(worldY) < step * 0.001f ? axisColor : color);
        }
    }

    private static bool DrawPositiveSetting(
        string id,
        string label,
        ref float value,
        float speed,
        float minimum,
        string format)
    {
        var edited = float.IsFinite(value) ? MathF.Max(minimum, value) : minimum;
        if (!EditorGui.Property(id, label, ref edited, speed, minimum, format: format))
            return false;
        value = float.IsFinite(edited) ? MathF.Max(minimum, edited) : minimum;
        return true;
    }
}
