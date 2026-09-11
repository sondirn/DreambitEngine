using Dreambit.Editor.Assets;

namespace Dreambit.Editor.Inspection;

/// <summary>
/// Extension point for inspectors that own authored settings for source assets which are not
/// serialized Dreambit documents. Registrations are evaluated in order and end in a fallback.
/// </summary>
internal interface ISourceAssetInspector
{
    bool CanInspect(AssetRecord asset);
    void Draw(AssetRecord asset);
}

internal sealed class SourceAssetInspectorRegistry(
    IReadOnlyList<ISourceAssetInspector> inspectors)
{
    public void Draw(AssetRecord asset) => Resolve(asset).Draw(asset);

    internal ISourceAssetInspector Resolve(AssetRecord asset)
    {
        for (var index = 0; index < inspectors.Count; index++)
        {
            if (inspectors[index].CanInspect(asset))
                return inspectors[index];
        }

        throw new InvalidOperationException(
            $"No source-asset inspector is registered for '{asset.RelativePath}'.");
    }
}
