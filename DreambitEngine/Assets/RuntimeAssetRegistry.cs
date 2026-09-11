using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Dreambit;

/// <summary>
/// Compact catalog embedded into built content. Source paths, hashes, and editor metadata
/// remain editor-only; type IDs use Dreambit's durable identity rather than CLR assembly names.
/// </summary>
public sealed class RuntimeAssetRegistry : IAssetRegistry
{
    public const string LogicalPath = "__dreambit/asset-registry.jsonb";
    public const int CurrentSchemaVersion = 2;

    private readonly IReadOnlyList<AssetCatalogEntry> _assets;
    private readonly Dictionary<AssetId, string> _namesById;
    private readonly Dictionary<string, AssetId> _idsByName;

    private RuntimeAssetRegistry(IEnumerable<Entry> entries)
    {
        _namesById = [];
        _idsByName = new Dictionary<string, AssetId>(LogicalAssetNameComparer.Instance);
        var assets = new List<AssetCatalogEntry>();
        foreach (var entry in entries)
        {
            var id = new AssetId(entry.Id);
            if (id.IsEmpty || string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidDataException("The runtime asset registry contains an invalid entry.");
            if (!_namesById.TryAdd(id, entry.Name))
                throw new InvalidDataException($"Duplicate runtime asset ID '{id}'.");
            if (!_idsByName.TryAdd(entry.Name, id))
                throw new InvalidDataException($"Duplicate runtime asset name '{entry.Name}'.");
            assets.Add(new AssetCatalogEntry(id, entry.Name, entry.TypeId));
        }
        _assets = assets.AsReadOnly();
    }

    public IReadOnlyList<AssetCatalogEntry> GetAssets() => _assets;

    public bool TryResolveAssetName(AssetId assetId, out string assetName) =>
        _namesById.TryGetValue(assetId, out assetName);

    public bool TryGetAssetId(string assetName, out AssetId assetId) =>
        _idsByName.TryGetValue(assetName, out assetId);

    public static RuntimeAssetRegistry Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var json = JsnbLoader.GetJsonString(stream);
        var document = JsonConvert.DeserializeObject<Document>(json)
                       ?? throw new InvalidDataException("The runtime asset registry is empty.");
        if (document.SchemaVersion is not 1 and not CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Runtime asset registry schema {document.SchemaVersion} is not supported.");
        return new RuntimeAssetRegistry(document.Assets ?? []);
    }

    private sealed class Document
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("assets")] public List<Entry> Assets { get; set; }
    }

    private sealed class Entry
    {
        [JsonProperty("id")] public Guid Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string? TypeId { get; set; }
    }
}
