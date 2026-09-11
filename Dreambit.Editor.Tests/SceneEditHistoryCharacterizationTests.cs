using Dreambit.ECS;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

/// <summary>
/// Characterizes the document-level history contract through the public
/// SceneDocument facade rather than its internal collaborators.
/// </summary>
public sealed class SceneEditHistoryCharacterizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.SceneEditHistoryCharacterizationTests",
        Guid.NewGuid().ToString("N"));

    public SceneEditHistoryCharacterizationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SaveDuringAnActiveTransactionRollsBackTheGestureBeforePersisting()
    {
        var scenePath = Path.Combine(_root, "active-transaction.scene.json");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        WriteScene(scenePath, "Before", firstId, secondId);

        var selection = new SelectionService();
        using var document = SceneDocument.Open(scenePath, selection);
        selection.Set(document.Scene!.FindEntity(firstId));
        var changed = 0;
        document.Changed += _ => changed++;
        var transaction = document.BeginTransaction("Uncommitted Rename");
        transaction.Update(scene =>
        {
            scene.FindEntity(firstId)!.Name = "Uncommitted";
            selection.Set(scene.FindEntity(secondId));
        });

        document.Save();

        var persisted = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        Assert.Equal("Before", persisted.Entities.Single(entity => entity.Guid == firstId).Name);
        Assert.Equal("Before", document.Scene!.FindEntity(firstId)!.Name);
        Assert.Equal(firstId, Assert.Single(selection.EntityIds));
        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.False(document.Undo.CanRedo);
        Assert.Equal(0, changed);

        Assert.Throws<InvalidOperationException>(() =>
            transaction.Update(_ => throw new InvalidOperationException("The saved gesture is finished.")));
        using var next = document.BeginTransaction("Next Gesture");
        next.Abandon();
    }

    [Fact]
    public void UndoAndRedoRestoreSelectionIdsWithTheirSnapshots()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var selection = new SelectionService();
        using var document = CreateDocument(selection, firstId, secondId);
        selection.Set(document.Scene!.FindEntity(firstId));

        document.Apply("Rename And Select", scene =>
        {
            scene.FindEntity(firstId)!.Name = "After";
            selection.Set(scene.FindEntity(secondId));
        });

        Assert.Equal(secondId, Assert.Single(selection.EntityIds));
        Assert.True(document.Undo.Undo());
        Assert.Equal("Before", document.Scene!.FindEntity(firstId)!.Name);
        Assert.Equal(firstId, Assert.Single(selection.EntityIds));
        var undoneSelection = selection.GetActive(document.Scene);
        Assert.NotNull(undoneSelection);
        Assert.Equal(firstId, undoneSelection!.Id);

        Assert.True(document.Undo.Redo());
        Assert.Equal("After", document.Scene!.FindEntity(firstId)!.Name);
        Assert.Equal(secondId, Assert.Single(selection.EntityIds));
        var redoneSelection = selection.GetActive(document.Scene);
        Assert.NotNull(redoneSelection);
        Assert.Equal(secondId, redoneSelection!.Id);
    }

    [Fact]
    public void FailedSaveKeepsThePreviousSavedBaseline()
    {
        var scenePath = Path.Combine(_root, "saved-baseline.scene.json");
        var entityId = Guid.NewGuid();
        WriteScene(scenePath, "Before", entityId);
        var blockerPath = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blockerPath, "This file intentionally blocks the save directory.");

        using var document = SceneDocument.Open(scenePath, new SelectionService());
        document.Rename(document.Scene!.FindEntity(entityId)!, "After");

        Assert.ThrowsAny<IOException>(() =>
            document.Save(Path.Combine(blockerPath, "failed.scene.json")));

        Assert.True(document.IsDirty);
        Assert.Equal("After", document.Scene!.FindEntity(entityId)!.Name);
        Assert.Equal("Before", SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath))
            .Entities.Single(entity => entity.Guid == entityId).Name);

        Assert.True(document.Undo.Undo());
        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);
        Assert.False(document.IsDirty);

        Assert.True(document.Undo.Redo());
        Assert.Equal("After", document.Scene!.FindEntity(entityId)!.Name);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void OwnedHistoryMergesCompatibleEditsWhileExternalHistoryOnlyPublishesChanges()
    {
        var ownedId = Guid.NewGuid();
        using var owned = CreateDocument(new SelectionService(), ownedId);
        owned.Apply(
            "Rename",
            scene => scene.FindEntity(ownedId)!.Name = "First",
            "Entity.Name");
        owned.Apply(
            "Rename",
            scene => scene.FindEntity(ownedId)!.Name = "Second",
            "Entity.Name");

        Assert.True(owned.IsDirty);
        Assert.True(owned.Undo.Undo());
        Assert.Equal("Before", owned.Scene!.FindEntity(ownedId)!.Name);
        Assert.False(owned.Undo.CanUndo);
        Assert.True(owned.Undo.Redo());
        Assert.Equal("Second", owned.Scene!.FindEntity(ownedId)!.Name);

        var externalId = Guid.NewGuid();
        var externalChanges = 0;
        using var external = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Externally Owned",
                Entities = [new EntityBlueprint { Name = "Before", Guid = externalId }]
            },
            null,
            new SelectionService(),
            historyOwnership: SceneDocumentHistoryOwnership.External);
        external.Changed += _ => externalChanges++;
        external.Apply(
            "Rename",
            scene => scene.FindEntity(externalId)!.Name = "First",
            "Entity.Name");
        external.Apply(
            "Rename",
            scene => scene.FindEntity(externalId)!.Name = "Second",
            "Entity.Name");

        Assert.Equal("Second", external.Scene!.FindEntity(externalId)!.Name);
        Assert.False(external.IsDirty);
        Assert.False(external.Undo.CanUndo);
        Assert.False(external.Undo.CanRedo);
        Assert.Equal(2, externalChanges);
    }

    private static SceneDocument CreateDocument(SelectionService selection, params Guid[] entityIds) =>
        new(
            new SceneBlueprint
            {
                Name = "History Characterization",
                Entities = entityIds.Select(id => new EntityBlueprint { Name = "Before", Guid = id }).ToList()
            },
            null,
            selection);

    private static void WriteScene(string path, string firstName, params Guid[] entityIds)
    {
        var entities = entityIds
            .Select((id, index) => new EntityBlueprint
            {
                Name = index == 0 ? firstName : "Other",
                Guid = id
            })
            .ToList();
        File.WriteAllText(
            path,
            SceneDocumentSerializer.Serialize(new SceneBlueprint
            {
                Name = "History Characterization",
                Entities = entities
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
