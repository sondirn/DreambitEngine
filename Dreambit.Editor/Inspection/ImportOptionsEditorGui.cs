using Dreambit.EditorApi;
using Dreambit.Tiled;

namespace Dreambit.Editor.Inspection;

/// <summary>
/// Shared presentation for linked-map import options. Callers retain ownership of mutation,
/// undo, scene creation and reimport behavior.
/// </summary>
internal static class ImportOptionsEditorGui
{
    public static string? Draw(TiledImportOptions options, string idPrefix = "Tiled")
    {
        ArgumentNullException.ThrowIfNull(options);

        var pixelsPerUnit = options.PixelsPerUnit;
        var baseDrawLayer = options.BaseDrawLayer;
        var drawLayerStep = options.DrawLayerStep;
        var worldDepth = options.WorldDepth;
        var worldDepthStride = options.WorldDepthDrawLayerStride;
        var renderBackgroundColor = options.RenderMapBackgroundColor;
        var includeInvisibleLayers = options.IncludeInvisibleLayers;
        string? mergeKey = null;

        using (var section = EditorGui.Section($"{idPrefix}.Options", "Import Settings"))
        {
            if (section.IsOpen)
            {
                if (EditorGui.Property(
                        $"{idPrefix}.PixelsPerUnit", "Pixels Per Unit", ref pixelsPerUnit,
                        speed: 0.1f, min: 0.001f, max: 100000f))
                    mergeKey = "Tiled.PixelsPerUnit";
                if (EditorGui.Property($"{idPrefix}.BaseDrawLayer", "Base Draw Layer", ref baseDrawLayer))
                    mergeKey = "Tiled.BaseDrawLayer";
                if (EditorGui.Property(
                        $"{idPrefix}.DrawLayerStep", "Draw Layer Step", ref drawLayerStep,
                        min: 1, max: 100000))
                    mergeKey = "Tiled.DrawLayerStep";
                if (EditorGui.Property($"{idPrefix}.WorldDepth", "World Depth", ref worldDepth))
                    mergeKey = "Tiled.WorldDepth";
                if (EditorGui.Property(
                        $"{idPrefix}.WorldDepthStride", "World Depth Stride", ref worldDepthStride,
                        min: 1, max: int.MaxValue))
                    mergeKey = "Tiled.WorldDepthStride";
                if (EditorGui.Property(
                        $"{idPrefix}.RenderBackgroundColor",
                        "Render Map Background Color",
                        ref renderBackgroundColor))
                    mergeKey = "Tiled.RenderBackgroundColor";
                if (EditorGui.Property(
                        $"{idPrefix}.IncludeInvisibleLayers",
                        "Include Invisible Layers",
                        ref includeInvisibleLayers))
                    mergeKey = "Tiled.IncludeInvisibleLayers";
            }
        }

        options.PixelsPerUnit = pixelsPerUnit;
        options.BaseDrawLayer = baseDrawLayer;
        options.DrawLayerStep = drawLayerStep;
        options.WorldDepth = worldDepth;
        options.WorldDepthDrawLayerStride = worldDepthStride;
        options.RenderMapBackgroundColor = renderBackgroundColor;
        options.IncludeInvisibleLayers = includeInvisibleLayers;
        return mergeKey;
    }
}
