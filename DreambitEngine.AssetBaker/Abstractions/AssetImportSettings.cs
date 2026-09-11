using System.Text.Json.Serialization;

namespace DreambitEngine.AssetBaker.Abstractions;

/// <summary>
/// Describes how a texture source should be interpreted while baking. This is deliberately
/// separate from the source file format: both color textures and normal maps can be PNG files.
/// </summary>
public enum TextureSemantic
{
    Color,
    NormalMap
}

/// <summary>Typed, editor-authored import settings stored with a source asset.</summary>
public sealed record AssetImportSettings
{
    public TextureImportSettings? Texture { get; init; }
}

/// <summary>Import settings understood by Dreambit's raster texture baker.</summary>
public sealed record TextureImportSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextureSemantic Semantic { get; init; } = TextureSemantic.Color;
}
