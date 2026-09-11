using Dreambit.Editor.Assets;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Persistence;

/// <summary>
/// Keeps persisted editor focus aligned with the active asset or scene document.
/// </summary>
internal sealed class EditorWorkspaceSelectionPersistence(EditorWorkspaceState workspaceState)
{
    public void RestoreAssetSelection(
        AssetDatabase assets,
        AssetEditingService assetEditing,
        EditorDocumentContext documents)
    {
        if (!string.Equals(workspaceState.LastSelectionKind, "asset", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(workspaceState.LastSelectedAssetPath))
        {
            return;
        }

        if (assets.TryGetAsset(workspaceState.LastSelectedAssetPath, out var asset) &&
            assetEditing.Select(asset))
        {
            documents.ActivateAsset();
        }
    }

    public void RestoreSceneSelection(SceneDocumentService scenes)
    {
        if (!string.Equals(workspaceState.LastSelectionKind, "entity", StringComparison.OrdinalIgnoreCase) ||
            scenes.Current is null)
        {
            return;
        }

        scenes.Selection.Restore(workspaceState.LastSelectedEntityIds);
        scenes.Selection.RemoveMissing(scenes.Current.Scene);
    }

    public void CaptureSelection(
        EditorDocumentContext documents,
        AssetEditingService assetEditing,
        SceneDocumentService scenes)
    {
        if (documents.ActiveKind is EditorDocumentKind.Asset or EditorDocumentKind.Blueprint)
        {
            if (assetEditing.Selected is { } asset)
            {
                workspaceState.LastSelectedAssetPath = asset.RelativePath;
                workspaceState.LastSelectedAssetIsFolder = false;
                workspaceState.LastSelectionKind = "asset";
            }

            return;
        }

        // Scene focus owns persisted selection even when it is empty. Otherwise a retained
        // asset document can become the apparent startup focus after an intentional deselect.
        workspaceState.LastSelectedEntityIds = scenes.Selection.EntityIds.ToList();
        workspaceState.LastSelectionKind = "entity";
    }

    public void CaptureCurrentScene(SceneDocumentService scenes)
    {
        if (scenes.Current?.Path is { } path)
            workspaceState.LastScenePath = path;
    }

}
