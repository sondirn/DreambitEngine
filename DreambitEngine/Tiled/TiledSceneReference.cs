using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.Tiled;

/// <summary>
/// Persistent link from a Dreambit scene to a Tiled TMX map. Tiled-owned tile
/// visualization entities are regenerated when the scene loads; regular
/// Dreambit entities remain authored in the scene blueprint.
/// </summary>
public sealed class TiledSceneReference
{
    [JsonProperty("asset_id")]
    public Guid AssetId { get; set; }

    [JsonProperty("asset", Required = Required.Always)]
    public string AssetName { get; set; } = string.Empty;

    [JsonProperty("import_options")]
    public TiledImportOptions ImportOptions { get; set; } = new();

    /// <summary>
    /// Editor-authored changes to generated Tiled visualization entities, keyed by
    /// stable TMX layer IDs. Fresh tile data is imported first and these values are applied last.
    /// </summary>
    [JsonProperty("entity_overrides")]
    public Dictionary<string, TiledGeneratedEntityOverride> EntityOverrides { get; set; } =
        new(StringComparer.Ordinal);

    [JsonIgnore]
    public float PixelsPerUnit
    {
        get => ImportOptions?.PixelsPerUnit ?? 1f;
        set
        {
            ImportOptions ??= new TiledImportOptions();
            ImportOptions.PixelsPerUnit = value;
        }
    }

    [JsonProperty("pixels_per_unit")]
    private float LegacyPixelsPerUnit
    {
        set => PixelsPerUnit = value;
    }
}

/// <summary>Persistent Dreambit-side edits layered over a regenerated Tiled entity.</summary>
public sealed class TiledGeneratedEntityOverride
{
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string Name { get; set; }

    [JsonProperty("enabled", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Enabled { get; set; }

    [JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)]
    public HashSet<string> Tags { get; set; }

    [JsonProperty("position", NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(Vector3Converter))]
    public Vector3? Position { get; set; }

    [JsonProperty("rotation_2d", NullValueHandling = NullValueHandling.Ignore)]
    public float? Rotation2D { get; set; }

    [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(Vector3Converter))]
    public Vector3? Scale { get; set; }

    [JsonProperty("components")]
    public Dictionary<string, Dictionary<string, JToken>> Components { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
