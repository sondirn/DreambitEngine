using System.Collections.Generic;

namespace Dreambit;

/// <summary>
/// Resolves stable asset identities to the logical names consumed by runtime loaders.
/// The Editor owns the source registry; packaged games can install a baked implementation.
/// </summary>
public interface IAssetRegistry
{
    /// <summary>Returns a read-only snapshot of the live authored asset catalog.</summary>
    IReadOnlyList<AssetCatalogEntry> GetAssets();

    bool TryResolveAssetName(AssetId assetId, out string assetName);

    bool TryGetAssetId(string assetName, out AssetId assetId);
}
