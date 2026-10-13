using Dreambit.Editor.Assets;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Scenes;
using Dreambit.EditorApi;
using Dreambit.Tiled;
using XnaVec3 = Microsoft.Xna.Framework.Vector3;

namespace Dreambit.Editor.Commands;

internal sealed record EditorCommandResult(bool Succeeded, string? Error = null)
{
    public static EditorCommandResult Success() => new(true);
    public static EditorCommandResult Failure(string error) => new(false, error);
}

internal sealed record SaveDocumentResult(
    bool Succeeded,
    bool RequiresSaveAs = false,
    string? Error = null)
{
    public static SaveDocumentResult Success() => new(true);
    public static SaveDocumentResult NeedsSaveAs() => new(false, true);
    public static SaveDocumentResult Failure(string error) => new(false, false, error);
}

/// <summary>
/// Performs document-level editor operations without knowing which UI invoked them.
/// Dialogs own input state and menus/shortcuts choose when these operations are requested.
/// </summary>
internal sealed class EditorDocumentCommands
{
    private readonly SceneDocumentService _scenes;
    private readonly EditorDocumentContext _documents;
    private readonly AssetEditingService _assetEditing;
    private readonly BlueprintSourceService _blueprintSources;
    private readonly EditorWorkspaceSelectionPersistence _workspaceSelection;
    private readonly EditorWorkspaceState _workspace;
    private readonly EditorLogService _logs;

    public EditorDocumentCommands(
        SceneDocumentService scenes,
        EditorDocumentContext documents,
        AssetEditingService assetEditing,
        BlueprintSourceService blueprintSources,
        EditorWorkspaceSelectionPersistence workspaceSelection,
        EditorWorkspaceState workspace,
        EditorLogService logs)
    {
        _scenes = scenes;
        _documents = documents;
        _assetEditing = assetEditing;
        _blueprintSources = blueprintSources;
        _workspaceSelection = workspaceSelection;
        _workspace = workspace;
        _logs = logs;
    }

    public bool CanCreateEntities => !_documents.IsAsset && _documents.Current is not null;
    public bool CanUndo => _documents.Undo?.CanUndo == true;
    public bool CanRedo => _documents.Undo?.CanRedo == true;
    public string? UndoName => _documents.Undo?.UndoName;
    public string? RedoName => _documents.Undo?.RedoName;

    public EditorCommandResult CreateScene(string name)
    {
        try
        {
            _scenes.New(name.Trim());
            _documents.ActivateScene();
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure("Scene", "Could not create the scene.", exception);
        }
    }

    public EditorCommandResult CreateSceneFromTiled(
        AssetRecord asset,
        TiledImportOptions importOptions)
    {
        try
        {
            if (!_assetEditing.Clear())
            {
                return EditorCommandResult.Failure(
                    "Could not create the Tiled scene because the current asset could not be saved.");
            }

            _scenes.NewFromTiled(asset, importOptions);
            _documents.ActivateScene();
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            var message = $"Could not create a Tiled scene from '{asset.RelativePath}'.";
            _logs.Error("Tiled", message, exception);
            return EditorCommandResult.Failure($"{message} {exception.Message}");
        }
    }

    public EditorCommandResult OpenScene(string path)
    {
        try
        {
            _scenes.Open(path);
            _documents.ActivateScene();
            _workspaceSelection.CaptureCurrentScene(_scenes);
            _workspaceSelection.RestoreSceneSelection(_scenes);
            _logs.Info("Scene", $"Opened '{_scenes.Current!.DisplayName}'.");
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure("Scene", "Could not open scene.", exception);
        }
    }

    public SaveDocumentResult SaveActiveDocument()
    {
        if (_documents.AssetDocument is { } assetDocument)
        {
            try
            {
                _assetEditing.Save();
                _logs.Info("Assets", $"Saved '{assetDocument.Asset.RelativePath}'.");
                return SaveDocumentResult.Success();
            }
            catch (Exception exception)
            {
                _logs.Error("Assets", "Could not save asset.", exception);
                return SaveDocumentResult.Failure(exception.Message);
            }
        }

        if (_documents.ActiveKind != EditorDocumentKind.Scene || _scenes.Current is null)
            return SaveDocumentResult.Success();
        if (_scenes.Current.Path is null)
            return SaveDocumentResult.NeedsSaveAs();

        var result = SaveScene(_scenes.Current.Path);
        return result.Succeeded
            ? SaveDocumentResult.Success()
            : SaveDocumentResult.Failure(result.Error!);
    }

    public string? GetSaveSceneAsInitialPath()
    {
        if (_documents.ActiveKind != EditorDocumentKind.Scene || _scenes.Current is not { } document)
            return null;

        return document.Path ??
               $"Scenes/{document.DisplayName}{DreambitAssetFileExtensions.SceneBlueprint}";
    }

    public EditorCommandResult SaveScene(string path)
    {
        try
        {
            _scenes.Save(path);
            _workspaceSelection.CaptureCurrentScene(_scenes);
            _logs.Info("Scene", $"Saved '{_scenes.Current!.DisplayName}'.");
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure("Scene", "Could not save scene.", exception);
        }
    }

    public EditorCommandResult Undo() => ChangeHistory(redo: false);
    public EditorCommandResult Redo() => ChangeHistory(redo: true);

    public EditorCommandResult CreateEmptyEntity()
    {
        var document = _documents.IsAsset ? null : _documents.Current;
        if (document is null)
            return EditorCommandResult.Failure("No scene or Blueprint document is active.");

        try
        {
            document.CreateEmpty(
                "Entity",
                _documents.IsBlueprint ? _documents.Blueprints.Root : null,
                GetViewportCenter());
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure(document.Name, "Could not create the entity.", exception);
        }
    }

    public EditorCommandResult CreateEntityFromBlueprint(AssetRecord blueprint)
    {
        var document = _documents.IsAsset ? null : _documents.Current;
        if (document is null)
            return EditorCommandResult.Failure("No scene or Blueprint document is active.");

        try
        {
            using var source = _blueprintSources.Load(blueprint);
            document.InstantiateBlueprint(
                source,
                GetViewportCenter(source.Position.Z),
                parent: _documents.IsBlueprint ? _documents.Blueprints.Root : null);
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure("Scene", "Could not create the entity from the Blueprint.", exception);
        }
    }

    private XnaVec3 GetViewportCenter(float z = 0f) =>
        _documents.IsBlueprint
            ? new XnaVec3(_workspace.BlueprintCameraX, _workspace.BlueprintCameraY, z)
            : new XnaVec3(_workspace.SceneCameraX, _workspace.SceneCameraY, z);

    private EditorCommandResult ChangeHistory(bool redo)
    {
        var undo = _documents.Undo;
        if (undo is null)
            return EditorCommandResult.Success();

        try
        {
            if (redo)
                undo.Redo();
            else
                undo.Undo();
            return EditorCommandResult.Success();
        }
        catch (Exception exception)
        {
            return LogFailure(
                "Undo",
                redo ? "Could not redo the editor change." : "Could not undo the editor change.",
                exception);
        }
    }

    private EditorCommandResult LogFailure(string category, string message, Exception exception)
    {
        _logs.Error(category, message, exception);
        return EditorCommandResult.Failure(exception.Message);
    }
}
