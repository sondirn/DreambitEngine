using System.Text.Json;
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
    [JsonConverter(typeof(AssetKindJsonConverter))]
    public AssetKind Kind { get; set; }
    [JsonPropertyName("type")]
    public string? TypeId { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public int ClassificationVersion { get; set; }
    public AssetImportSettings? ImportSettings { get; set; }
}

/// <summary>
/// Asset classification is cached metadata. Retired or unfamiliar kinds must not prevent
/// loading stable asset IDs; the next database scan classifies the source files again.
/// </summary>
internal sealed class AssetKindJsonConverter : JsonConverter<AssetKind>
{
    public AssetKindJsonConverter() { }

    public override AssetKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return Enum.TryParse<AssetKind>(reader.GetString(), ignoreCase: true, out var kind) &&
                   Enum.IsDefined(kind)
                ? kind
                : AssetKind.Unknown;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
            return Enum.IsDefined((AssetKind)value) ? (AssetKind)value : AssetKind.Unknown;

        throw new JsonException("Asset kind must be a string or integer.");
    }

    public override void Write(Utf8JsonWriter writer, AssetKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : nameof(AssetKind.Unknown));
}
