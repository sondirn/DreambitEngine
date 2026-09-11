using System.Text.Json.Serialization;
using DreambitEngine.AssetBaker.Abstractions;

namespace Dreambit.Editor.Assets;

internal sealed class AssetRegistryDocument
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
    public const string RelativePath = ".dreambit/assets.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<AssetRegistryEntry> Assets { get; set; } = [];
}

internal sealed class AssetRegistryEntry
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AssetKind Kind { get; set; }
    [JsonPropertyName("type")]
    public string? TypeId { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public int ClassificationVersion { get; set; }
    public AssetImportSettings? ImportSettings { get; set; }
}
