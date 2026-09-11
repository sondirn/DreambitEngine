using Dreambit.ECS;
using Dreambit.Editor.Scenes;
using Dreambit.Tiled;

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

    [Fact]
    public void TiledReimportPreservesSelectionForGeneratedEntitiesBySourceIdentity()
    {
        var contentRoot = Path.Combine(_root, "Assets");
        var mapsDirectory = Path.Combine(contentRoot, "maps");
        Directory.CreateDirectory(mapsDirectory);
        var mapPath = Path.Combine(mapsDirectory, "world.tmx");
        WriteTiledMap(mapPath, "Ground");

        TmxMap ResolveMap(TiledSceneReference _) =>
            TmxMap.FromContentFile(mapPath, "maps/world", contentRoot);

        var selection = new SelectionService();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Imported Selection",
                Tiled = new TiledSceneReference { AssetName = "maps/world" }
            },
            null,
            selection,
            tiledMapResolver: ResolveMap);
        var selected = Assert.Single(
            document.Scene!.GetAllEntities()
                .SelectMany(entity => entity.GetAllComponents())
                .OfType<FilledRectDrawer>()).Entity;
        var sourceKey = selected.TiledSourceKey;
        selection.Set(selected);

        WriteTiledMap(mapPath, "Ground Updated");
        document.ReimportTiled();

        var restored = selection.GetActive(document.Scene);
        Assert.NotNull(restored);
        Assert.True(restored.IsTiledGenerated);
        Assert.Equal(sourceKey, restored.TiledSourceKey);
        Assert.NotEqual(selected.Id, restored.Id);
    }

    [Fact]
    public void FailedTiledImportOptionUpdateLeavesTheWorkingDocumentUntouched()
    {
        var contentRoot = Path.Combine(_root, "Assets");
        var mapsDirectory = Path.Combine(contentRoot, "maps");
        Directory.CreateDirectory(mapsDirectory);
        var mapPath = Path.Combine(mapsDirectory, "world.tmx");
        WriteTiledMap(mapPath, "Ground");

        TmxMap ResolveMap(TiledSceneReference reference)
        {
            if (reference.ImportOptions.PixelsPerUnit == 2f)
                throw new InvalidOperationException("The updated Tiled options cannot be materialized.");
            return TmxMap.FromContentFile(mapPath, "maps/world", contentRoot);
        }

        var selection = new SelectionService();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Tiled Option Failure",
                Tiled = new TiledSceneReference
                {
                    AssetName = "maps/world",
                    ImportOptions = new TiledImportOptions { PixelsPerUnit = 1f }
                }
            },
            null,
            selection,
            tiledMapResolver: ResolveMap);
        var selected = Assert.Single(
            document.Scene!.GetAllEntities()
                .SelectMany(entity => entity.GetAllComponents())
                .OfType<FilledRectDrawer>()).Entity;
        selection.Set(selected);
        var workingScene = document.Scene;
        var generation = document.SceneGeneration;
        var changed = 0;
        document.Changed += _ => changed++;

        Assert.Throws<InvalidOperationException>(() => document.UpdateTiledImportOptions(
            "Change Tiled Pixels Per Unit",
            options => options.PixelsPerUnit = 2f));

        Assert.Same(workingScene, document.Scene);
        Assert.Equal(generation, document.SceneGeneration);
        Assert.Equal(1f, document.TiledReference!.ImportOptions.PixelsPerUnit);
        Assert.Equal(selected.Id, Assert.Single(selection.EntityIds));
        Assert.Same(selected, selection.GetActive(document.Scene));
        Assert.False(document.Undo.CanUndo);
        Assert.False(document.IsDirty);
        Assert.Equal(0, changed);
    }

    private static SceneDocument CreateDocument(EntityBlueprint entity) =>
        new(
            new SceneBlueprint { Name = "Characterization", Entities = [entity] },
            null,
            new SelectionService());

    private static void WriteTiledMap(string path, string layerName)
    {
        File.WriteAllText(path, $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="1" height="1" tilewidth="16" tileheight="16" infinite="0" backgroundcolor="#123456">
          <layer id="2" name="{{layerName}}" width="1" height="1">
            <data encoding="csv">0</data>
          </layer>
        </map>
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
