using Dreambit.ECS;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

public sealed class SceneDocumentCharacterizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.SceneDocumentCharacterizationTests",
        Guid.NewGuid().ToString("N"));

    public SceneDocumentCharacterizationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TransactionCommitRecordsOneUndoEntryForMultipleUpdates()
    {
        var entityId = Guid.NewGuid();
        using var document = CreateDocument(
            new EntityBlueprint { Name = "Before", Guid = entityId });
        var changed = 0;
        document.Changed += _ => changed++;

        var transaction = document.BeginTransaction("Rename Gesture");
        transaction.Update(scene => scene.FindEntity(entityId)!.Name = "First");
        transaction.Update(scene => scene.FindEntity(entityId)!.Name = "Second");
        transaction.Commit();

        Assert.Equal("Second", document.Scene!.FindEntity(entityId)!.Name);
        Assert.True(document.IsDirty);
        Assert.True(document.Undo.CanUndo);
        Assert.Equal("Rename Gesture", document.Undo.UndoName);
        Assert.Equal(1, changed);

        Assert.True(document.Undo.Undo());
        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);
        Assert.False(document.Undo.CanUndo);
        Assert.True(document.Undo.CanRedo);

        Assert.True(document.Undo.Redo());
        Assert.Equal("Second", document.Scene!.FindEntity(entityId)!.Name);
    }

    [Fact]
    public void TransactionCancelRestoresStateAndSelectionWithoutPublishingAChange()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var selection = new SelectionService();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Cancel Gesture",
                Entities =
                [
                    new EntityBlueprint { Name = "Before", Guid = firstId },
                    new EntityBlueprint { Name = "Other", Guid = secondId }
                ]
            },
            null,
            selection);
        selection.Set(document.Scene!.FindEntity(firstId));
        var changed = 0;
        document.Changed += _ => changed++;

        var transaction = document.BeginTransaction("Temporary Rename");
        transaction.Update(scene =>
        {
            scene.FindEntity(firstId)!.Name = "Temporary";
            selection.Set(scene.FindEntity(secondId));
        });
        transaction.Cancel();

        Assert.Equal("Before", document.Scene!.FindEntity(firstId)!.Name);
        Assert.Equal(firstId, Assert.Single(selection.EntityIds));
        Assert.False(document.Undo.CanUndo);
        Assert.False(document.IsDirty);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void FailedTransactionUpdateRestoresStateAndReleasesTheTransaction()
    {
        var entityId = Guid.NewGuid();
        using var document = CreateDocument(
            new EntityBlueprint { Name = "Before", Guid = entityId });

        var transaction = document.BeginTransaction("Failing Gesture");
        Assert.Throws<InvalidOperationException>(() => transaction.Update(scene =>
        {
            scene.FindEntity(entityId)!.Name = "Partial";
            throw new InvalidOperationException("Intentional failure.");
        }));

        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);
        Assert.False(document.Undo.CanUndo);
        Assert.False(document.IsDirty);

        var next = document.BeginTransaction("Next Gesture");
        next.Abandon();
    }

    [Fact]
    public void BlueprintRefreshPreservesTheSelectedBoxedRoot()
    {
        var source = new EntityBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "actors/hero.blueprint",
            Name = "Hero",
            Guid = Guid.NewGuid()
        };
        var selection = new SelectionService();
        using var document = SceneDocument.CreateNew(
            "Blueprint Refresh",
            selection,
            blueprintInstanceResolver: _ => source);
        var instance = document.InstantiateBlueprint(source);
        var instanceId = instance.Id;
        selection.Set(instance);
        source.Name = "Updated Hero";

        document.RefreshBlueprintInstances();

        var refreshed = document.Scene!.FindEntity(instanceId);
        Assert.NotNull(refreshed);
        Assert.Equal("Updated Hero", refreshed.Name);
        Assert.Equal(instanceId, Assert.Single(selection.EntityIds));
        Assert.Same(refreshed, selection.GetActive(document.Scene));
    }

    [Fact]
    public void FailedBlueprintRefreshKeepsTheWorkingSceneAndSelection()
    {
        var source = new EntityBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "actors/hero.blueprint",
            Name = "Hero",
            Guid = Guid.NewGuid()
        };
        var resolverAvailable = true;
        var selection = new SelectionService();
        using var document = SceneDocument.CreateNew(
            "Blueprint Refresh Failure",
            selection,
            blueprintInstanceResolver: _ => resolverAvailable
                ? source
                : throw new InvalidOperationException("Blueprint source is unavailable."));
        var instance = document.InstantiateBlueprint(source);
        var instanceId = instance.Id;
        selection.Set(instance);
        var workingScene = document.Scene;
        var generation = document.SceneGeneration;
        resolverAvailable = false;

        Assert.Throws<InvalidOperationException>(document.RefreshBlueprintInstances);

        Assert.Same(workingScene, document.Scene);
        Assert.Equal(generation, document.SceneGeneration);
        Assert.NotNull(document.Scene!.FindEntity(instanceId));
        Assert.Equal(instanceId, Assert.Single(selection.EntityIds));
        Assert.Same(document.Scene.FindEntity(instanceId), selection.GetActive(document.Scene));
    }

    private static SceneDocument CreateDocument(EntityBlueprint entity) =>
        new(
            new SceneBlueprint { Name = "Characterization", Entities = [entity] },
            null,
            new SelectionService());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
