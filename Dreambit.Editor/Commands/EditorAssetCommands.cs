using Dreambit.Editor.Assets;
using Dreambit.Editor.Inspection;
using Dreambit.EditorApi;

namespace Dreambit.Editor.Commands;

/// <summary>
/// Shared asset-menu actions. Asset source entry remains in ProjectPanel for this refactor phase,
/// so that panel contributes only the narrow request callback for its existing creation dialog.
/// </summary>
internal sealed class EditorAssetCommands(
    EditorTypeRegistry editorTypes,
    EditorBuildCommands buildCommands,
    Action<Type> requestAssetCreation)
{
    public IEnumerable<Type> CreatableAssetTypes => editorTypes.AssetTypes
        .Where(type => type != typeof(EntityBlueprint) && AssetTypeClassifier.CanCreateAsset(type));

    public void RequestEntityBlueprintCreation() => requestAssetCreation(typeof(EntityBlueprint));
    public void RequestAssetCreation(Type type) => requestAssetCreation(type);
    public void UpdateBlobs() => buildCommands.UpdateBlobs();
    public void RebuildAllBlobs() => buildCommands.RebuildAllBlobs();
}
