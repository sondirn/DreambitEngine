namespace Dreambit;

/// <summary>Durable loading metadata for an authored asset; no asset is loaded by reading it.</summary>
public readonly record struct AssetCatalogEntry(AssetId Id, string AssetName, string? TypeId);
