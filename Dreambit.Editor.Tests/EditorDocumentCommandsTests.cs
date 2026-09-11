using Dreambit;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

public sealed class EditorDocumentCommandsTests
{
    [Fact]
    public void SavingAnUnsavedSceneRequestsSaveAs()
    {
        using var fixture = new EditorCommandTestFixture();

        fixture.Scenes.New("Unsaved Scene");
        fixture.Documents.ActivateScene();

        var result = fixture.Commands.SaveActiveDocument();

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresSaveAs);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SavingASavedSceneUsesItsExistingPath()
    {
        using var fixture = new EditorCommandTestFixture();

        var document = fixture.Scenes.New("Saved Scene");
        fixture.Documents.ActivateScene();

        var entity = document.CreateEmpty("Before Save");

        const string relativePath = "Scenes/SavedScene.scene";

        var initialSave = fixture.Commands.SaveScene(relativePath);

        Assert.True(
            initialSave.Succeeded,
            initialSave.Error);

        Assert.False(document.IsDirty);

        document.Rename(
            entity,
            "After Save");

        Assert.True(document.IsDirty);

        var save = fixture.Commands.SaveActiveDocument();

        Assert.True(
            save.Succeeded,
            save.Error);

        Assert.False(save.RequiresSaveAs);
        Assert.False(document.IsDirty);

        var fullPath = fixture.GetContentPath(relativePath);

        Assert.True(File.Exists(fullPath));

        var persisted = DreambitJson.Deserialize<SceneBlueprint>(
            File.ReadAllText(fullPath));

        Assert.NotNull(persisted);

        Assert.Contains(
            persisted.Entities,
            blueprint => blueprint.Name == "After Save");
    }

    [Fact]
    public void SavingAnAssetRoutesThroughAssetEditing()
    {
        using var fixture = new EditorCommandTestFixture();

        var asset = fixture.AddBlueprint(
            "Blueprints/Hero.blueprint",
            "Disk Hero");

        Assert.True(
            fixture.AssetEditing.Select(asset));

        fixture.Documents.ActivateAsset();

        var document = Assert.IsType<DreambitAssetDocument>(
            fixture.AssetEditing.Current);

        document.Apply(
            "Rename Blueprint",
            instance =>
            {
                var blueprint = Assert.IsType<EntityBlueprint>(instance);
                blueprint.Name = "Saved Hero";
            });

        Assert.True(document.IsDirty);

        var result = fixture.Commands.SaveActiveDocument();

        Assert.True(
            result.Succeeded,
            result.Error);

        Assert.False(result.RequiresSaveAs);
        Assert.False(document.IsDirty);

        var persisted = DreambitJson.Deserialize<EntityBlueprint>(
            File.ReadAllText(
                fixture.GetContentPath(
                    "Blueprints/Hero.blueprint")));

        Assert.NotNull(persisted);
        Assert.Equal(
            "Saved Hero",
            persisted.Name);
    }

    [Fact]
    public void OpenSceneActivatesSceneAndRestoresPersistedSelection()
    {
        using var fixture = new EditorCommandTestFixture();

        var original = fixture.Scenes.New("Persistent Scene");
        fixture.Documents.ActivateScene();

        var entity = original.CreateEmpty("Selected Entity");

        const string relativePath = "Scenes/Persistent.scene";

        var save = fixture.Commands.SaveScene(relativePath);

        Assert.True(
            save.Succeeded,
            save.Error);

        fixture.WorkspaceState.LastSelectionKind = "entity";
        fixture.WorkspaceState.LastSelectedEntityIds =
        [
            entity.Id
        ];

        fixture.Scenes.New("Temporary Scene");
        fixture.Scenes.Selection.Clear();

        // Deliberately put routing on a different document kind first.
        fixture.Documents.ActivateBlueprint();

        Assert.Equal(
            EditorDocumentKind.Blueprint,
            fixture.Documents.ActiveKind);

        var result = fixture.Commands.OpenScene(relativePath);

        Assert.True(
            result.Succeeded,
            result.Error);

        Assert.Equal(
            EditorDocumentKind.Scene,
            fixture.Documents.ActiveKind);

        Assert.Equal(
            entity.Id,
            fixture.Scenes.Selection.ActiveEntityId);

        Assert.Equal(
            fixture.Scenes.ResolveScenePath(relativePath),
            fixture.WorkspaceState.LastScenePath);
    }

    [Fact]
    public void UndoAndRedoUseTheActiveDocumentHistory()
    {
        using var fixture = new EditorCommandTestFixture();

        fixture.Scenes.New("Undo Scene");
        fixture.Documents.ActivateScene();

        Assert.False(fixture.Commands.CanUndo);
        Assert.False(fixture.Commands.CanRedo);

        var create = fixture.Commands.CreateEmptyEntity();

        Assert.True(
            create.Succeeded,
            create.Error);

        Assert.True(fixture.Commands.CanUndo);
        Assert.False(fixture.Commands.CanRedo);
        Assert.Equal(
            "Create Entity",
            fixture.Commands.UndoName);

        var undo = fixture.Commands.Undo();

        Assert.True(
            undo.Succeeded,
            undo.Error);

        Assert.False(fixture.Commands.CanUndo);
        Assert.True(fixture.Commands.CanRedo);
        Assert.Equal(
            "Create Entity",
            fixture.Commands.RedoName);

        var redo = fixture.Commands.Redo();

        Assert.True(
            redo.Succeeded,
            redo.Error);

        Assert.True(fixture.Commands.CanUndo);
        Assert.False(fixture.Commands.CanRedo);
    }

    [Fact]
    public void CreateEntityPlacesItAtTheActiveViewportCenter()
    {
        using var fixture = new EditorCommandTestFixture();
        fixture.Scenes.New("Centered");
        fixture.Documents.ActivateScene();
        fixture.WorkspaceState.SceneCameraX = 18.5f;
        fixture.WorkspaceState.SceneCameraY = -7.25f;

        var result = fixture.Commands.CreateEmptyEntity();

        Assert.True(result.Succeeded, result.Error);
        var created = fixture.Scenes.Selection.GetActive(fixture.Scenes.Current!.Scene)!;
        Assert.Equal(18.5f, created.Transform.WorldPosition.X);
        Assert.Equal(-7.25f, created.Transform.WorldPosition.Y);
    }

    [Fact]
    public void SaveWithoutAnActiveDocumentIsAHarmlessNoOp()
    {
        using var fixture = new EditorCommandTestFixture();

        var result = fixture.Commands.SaveActiveDocument();

        Assert.True(result.Succeeded);
        Assert.False(result.RequiresSaveAs);
        Assert.Null(result.Error);
    }
}
