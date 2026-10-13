using System;
using Newtonsoft.Json;

namespace Dreambit.Tiled;

public sealed class TiledImportOptions
{
    /// <summary>Number of Tiled source pixels represented by one Dreambit world unit.</summary>
    [JsonProperty("pixels_per_unit")]
    public float PixelsPerUnit { get; set; } = 1f;

    /// <summary>Lowest draw layer used for this imported map.</summary>
    [JsonProperty("base_draw_layer")]
    public int BaseDrawLayer { get; set; }

    /// <summary>Distance between adjacent Tiled tile layers.</summary>
    [JsonProperty("draw_layer_step")]
    public int DrawLayerStep { get; set; } = 1;

    /// <summary>World-depth slice assigned to this Tiled map.</summary>
    [JsonProperty("world_depth")]
    public int WorldDepth { get; set; }

    /// <summary>Draw-layer distance between Tiled world-depth values.</summary>
    [JsonProperty("world_depth_draw_layer_stride")]
    public int WorldDepthDrawLayerStride { get; set; } = 1000;

    [JsonProperty("render_map_background_color")]
    public bool RenderMapBackgroundColor { get; set; } = true;

    [JsonProperty("include_invisible_layers")]
    public bool IncludeInvisibleLayers { get; set; }

    /// <summary>Stable seed used for rule and indexed-output probability choices.</summary>
    [JsonProperty("automapping_seed")]
    public int AutomappingSeed { get; set; }

    public TiledImportOptions Clone() => new()
    {
        PixelsPerUnit = PixelsPerUnit,
        BaseDrawLayer = BaseDrawLayer,
        DrawLayerStep = DrawLayerStep,
        WorldDepth = WorldDepth,
        WorldDepthDrawLayerStride = WorldDepthDrawLayerStride,
        RenderMapBackgroundColor = RenderMapBackgroundColor,
        IncludeInvisibleLayers = IncludeInvisibleLayers,
        AutomappingSeed = AutomappingSeed
    };

    public void Validate()
    {
        if (!float.IsFinite(PixelsPerUnit) || PixelsPerUnit <= 0f)
            throw new ArgumentOutOfRangeException(nameof(PixelsPerUnit), "PixelsPerUnit must be positive and finite.");
        if (DrawLayerStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(DrawLayerStep), "DrawLayerStep must be positive.");
        if (WorldDepthDrawLayerStride <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(WorldDepthDrawLayerStride),
                "WorldDepthDrawLayerStride must be positive.");
    }
}
