using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DreambitEngine.AssetBaker.Abstractions;

namespace DreambitEngine.AssetBaker.Pipeline;

/// <summary>
/// Immutable, path-indexed view of editor-authored source metadata used throughout one bake.
/// Reading the registry once keeps cache decisions and the emitted runtime registry consistent.
/// </summary>
internal sealed class SourceAssetRegistryCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, AssetImportSettings?> _importSettingsByPath;

    private SourceAssetRegistryCatalog(SourceAssetRegistryDocument document, string sourceHash)
    {
        Assets = document.Assets;
        SourceHash = sourceHash;
        _importSettingsByPath = new Dictionary<string, AssetImportSettings?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Assets)
        {
            var path = NormalizeRelativePath(entry.Path);
            if (!_importSettingsByPath.TryAdd(path, entry.ImportSettings))
                throw new InvalidDataException(
                    $"The Dreambit asset registry contains duplicate path '{path}'.");
        }
    }

    public IReadOnlyList<SourceAssetRegistryEntry> Assets { get; }
    public string SourceHash { get; }

    public AssetImportSettings? GetImportSettings(string relativePath) =>
        _importSettingsByPath.GetValueOrDefault(NormalizeRelativePath(relativePath));

    public static SourceAssetRegistryCatalog? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        byte[] sourceBytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            sourceBytes = memory.ToArray();
        }

        var document = JsonSerializer.Deserialize<SourceAssetRegistryDocument>(
                           sourceBytes,
                           SerializerOptions)
                       ?? throw new InvalidDataException(
                           "The Dreambit asset registry is empty.");
        if (document.SchemaVersion is not 1 and not 2)
            throw new InvalidDataException(
                $"Dreambit asset registry schema {document.SchemaVersion} is not supported.");
        document.Assets ??= [];

        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        return new SourceAssetRegistryCatalog(document, sourceHash);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private sealed class SourceAssetRegistryDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<SourceAssetRegistryEntry> Assets { get; set; } = [];
    }
}

internal sealed class SourceAssetRegistryEntry
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string? TypeId { get; set; }
    public AssetImportSettings? ImportSettings { get; set; }
}
