using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

public sealed class EditorWorkspaceSelectionPersistenceTests
{
    [Fact]
    public void RestoreAssetSelectionSelectsAndActivatesTheAsset()
    {
        using var fixture = new EditorCommandTestFixture();

        var asset = fixture.AddBlueprint(
            "Blueprints/Hero.blueprint",
            "Hero");

        fixture.WorkspaceState.LastSelectionKind = "asset";
        fixture.WorkspaceState.LastSelectedAssetPath =
            asset.RelativePath;

        fixture.SelectionPersistence.RestoreAssetSelection(
            fixture.Assets,
            fixture.AssetEditing,
            fixture.Documents);

        Assert.NotNull(fixture.AssetEditing.Selected);
        Assert.Equal(
            asset.Id,
            fixture.AssetEditing.Selected.Id);

        Assert.Equal(
            EditorDocumentKind.Asset,
            fixture.Documents.ActiveKind);

        Assert.True(fixture.Documents.IsAsset);
    }

    [Fact]
    public void CaptureAssetSelectionPersistsTheSelectedAsset()
    {
        using var fixture = new EditorCommandTestFixture();

        var asset = fixture.AddBlueprint(
            "Blueprints/Hero.blueprint",
            "Hero");

        Assert.True(
            fixture.AssetEditing.Select(asset));

        fixture.Documents.ActivateAsset();

        fixture.SelectionPersistence.CaptureSelection(
            fixture.Documents,
            fixture.AssetEditing,
            fixture.Scenes);

        Assert.Equal(
            "asset",
            fixture.WorkspaceState.LastSelectionKind);

        Assert.Equal(
            asset.RelativePath,
            fixture.WorkspaceState.LastSelectedAssetPath);

        Assert.False(
            fixture.WorkspaceState.LastSelectedAssetIsFolder);
    }

    [Fact]
    public void RestoreSceneSelectionDropsEntitiesThatNoLongerExist()
    {
        using var fixture = new EditorCommandTestFixture();

        var document = fixture.Scenes.New("Selection Scene");
        fixture.Documents.ActivateScene();

        var existingEntity =
            document.CreateEmpty("Existing");

        var missingEntityId = Guid.NewGuid();

        fixture.WorkspaceState.LastSelectionKind = "entity";
        fixture.WorkspaceState.LastSelectedEntityIds =
        [
            existingEntity.Id,
            missingEntityId
        ];

        fixture.Scenes.Selection.Clear();

        fixture.SelectionPersistence.RestoreSceneSelection(
            fixture.Scenes);

        Assert.Equal(
            [existingEntity.Id],
            fixture.Scenes.Selection.EntityIds);

        Assert.DoesNotContain(
            missingEntityId,
            fixture.Scenes.Selection.EntityIds);
    }

    [Fact]
    public void CaptureEmptySceneSelectionOverridesStaleAssetFocus()
    {
        using var fixture = new EditorCommandTestFixture();

        fixture.WorkspaceState.LastSelectionKind = "asset";
        fixture.WorkspaceState.LastSelectedAssetPath =
            "Blueprints/OldSelection.blueprint";
        fixture.WorkspaceState.LastSelectedEntityIds =
        [
            Guid.NewGuid()
        ];

        fixture.Scenes.New("Empty Scene");
        fixture.Documents.ActivateScene();
        fixture.Scenes.Selection.Clear();

        fixture.SelectionPersistence.CaptureSelection(
            fixture.Documents,
            fixture.AssetEditing,
            fixture.Scenes);

        Assert.Equal(
            "entity",
            fixture.WorkspaceState.LastSelectionKind);

        Assert.Empty(
            fixture.WorkspaceState.LastSelectedEntityIds);
    }

    [Fact]
    public void CaptureCurrentScenePersistsOnlyASavedScenePath()
    {
        using var fixture = new EditorCommandTestFixture();

        fixture.Scenes.New("Scene");

        fixture.SelectionPersistence.CaptureCurrentScene(
            fixture.Scenes);

        Assert.Null(
            fixture.WorkspaceState.LastScenePath);

        const string relativePath = "Scenes/Saved.scene";

        var save = fixture.Commands.SaveScene(relativePath);

        Assert.True(
            save.Succeeded,
            save.Error);

        fixture.SelectionPersistence.CaptureCurrentScene(
            fixture.Scenes);

        Assert.Equal(
            fixture.Scenes.ResolveScenePath(relativePath),
            fixture.WorkspaceState.LastScenePath);
    }
}