using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

public sealed class AssetEditingLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.AssetEditingLifecycleTests",
        Guid.NewGuid().ToString("N"));

    private string ContentRoot => Path.Combine(_root, "Content", "Assets");

    public AssetEditingLifecycleTests() => Directory.CreateDirectory(ContentRoot);

    [Fact]
    public void RenameRebindsOpenDocumentBeforeItsNextSave()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");

        Assert.True(fixture.Assets.TryRename(
            asset.RelativePath,
            "player.blueprint.json",
            out var renameError), renameError);
        fixture.Editing.RefreshFromDatabase();
        fixture.Editing.Save();

        Assert.False(File.Exists(Path.Combine(ContentRoot, "actors", "hero.blueprint.json")));
        var movedPath = Path.Combine(ContentRoot, "actors", "player.blueprint.json");
        Assert.True(File.Exists(movedPath));
        Assert.Equal("Unsaved Hero", DreambitJson.Deserialize<EntityBlueprint>(File.ReadAllText(movedPath))!.Name);
    }

    [Fact]
    public void DeletingDirtyOpenAssetCannotBeUndoneByAutosave()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");

        Assert.True(fixture.Assets.TryDelete(asset.RelativePath, out var deleteError), deleteError);
        fixture.Editing.RefreshFromDatabase();
        fixture.Editing.Update(autoSave: true, TimeSpan.Zero);

        Assert.Null(fixture.Editing.Current);
        Assert.Null(fixture.Editing.Selected);
        Assert.False(File.Exists(Path.Combine(ContentRoot, "actors", "hero.blueprint.json")));
    }

    [Fact]
    public void SavedAndClosedBlueprintPreviewDoesNotMaskLaterDiskRewrite()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");

        using (var preview = fixture.BlueprintSources.Load(asset))
            Assert.Equal("Unsaved Hero", preview.Name);

        fixture.Editing.Save();
        fixture.Editing.Clear();
        WriteBlueprint("actors/hero.blueprint.json", "Externally Rewritten Hero");
        fixture.Assets.RefreshNow();
        var refreshed = Assert.Single(fixture.Assets.GetSnapshot().Assets);

        using var loaded = fixture.BlueprintSources.Load(refreshed);
        Assert.Equal("Externally Rewritten Hero", loaded.Name);
    }

    [Fact]
    public void UndoToSavedBaselineEvictsTheBlueprintPreview()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");
        using (var preview = fixture.BlueprintSources.Load(asset))
            Assert.Equal("Unsaved Hero", preview.Name);

        fixture.Editing.Current.Undo.Undo();

        Assert.False(fixture.Editing.Current.IsDirty);
        using var loaded = fixture.BlueprintSources.Load(asset);
        Assert.Equal("Disk Hero", loaded.Name);
    }

    [Fact]
    public void RapidDocumentChangesCoalesceIntoOnePreviewUpdate()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        var previewUpdates = 0;
        fixture.Editing.PreviewChanged += _ => previewUpdates++;

        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero 1",
            "Blueprint.Name");
        fixture.Editing.Current.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero 2",
            "Blueprint.Name");
        fixture.Editing.Current.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero 3",
            "Blueprint.Name");

        Assert.Equal(0, previewUpdates);

        fixture.Editing.FlushPendingPreview();

        Assert.Equal(1, previewUpdates);
        using var preview = fixture.BlueprintSources.Load(asset);
        Assert.Equal("Unsaved Hero 3", preview.Name);
    }

    [Fact]
    public void SavingBeforeDeferredPreviewStillNotifiesBlueprintConsumersOnce()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        var sourceUpdates = 0;
        fixture.BlueprintSources.Changed += () => sourceUpdates++;

        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Saved Hero");
        fixture.Editing.Save();

        Assert.Equal(1, sourceUpdates);
        using var loaded = fixture.BlueprintSources.Load(asset);
        Assert.Equal("Saved Hero", loaded.Name);
    }

    [Fact]
    public void ActiveEditorInteractionDefersPreviewPastTheQuietWindow()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        var previewUpdates = 0;
        fixture.Editing.PreviewChanged += _ => previewUpdates++;
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Typing Hero");
        var afterQuietWindow = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);

        fixture.Editing.UpdatePendingPreview(
            editorInteractionActive: true,
            now: afterQuietWindow);

        Assert.Equal(0, previewUpdates);

        fixture.Editing.UpdatePendingPreview(
            editorInteractionActive: false,
            now: afterQuietWindow);

        Assert.Equal(1, previewUpdates);
    }

    [Fact]
    public void CleanExternalRewriteReopensTheAssetDocument()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        var originalDocument = fixture.Editing.Current;

        WriteBlueprint("actors/hero.blueprint.json", "Externally Rewritten Hero");
        fixture.Assets.RefreshNow();
        fixture.Editing.RefreshFromDatabase();

        var replacement = Assert.IsType<DreambitAssetDocument>(fixture.Editing.Current);
        Assert.NotSame(originalDocument, replacement);
        Assert.Equal("Externally Rewritten Hero", Assert.IsType<EntityBlueprint>(replacement.Instance).Name);
        Assert.False(replacement.IsDirty);
        Assert.Null(fixture.Editing.ExternalChangeConflict);
        Assert.Throws<ObjectDisposedException>(() => _ = originalDocument!.Instance);
    }

    [Fact]
    public void DirtyExternalRewriteRetainsEditorStateAndPausesAutosave()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        var document = fixture.Editing.Current!;
        document.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");

        WriteBlueprint("actors/hero.blueprint.json", "Externally Rewritten Hero");
        fixture.Assets.RefreshNow();
        fixture.Editing.RefreshFromDatabase();

        Assert.Same(document, fixture.Editing.Current);
        Assert.True(document.IsDirty);
        Assert.Equal("Unsaved Hero", Assert.IsType<EntityBlueprint>(document.Instance).Name);
        Assert.Contains("auto-save is paused", fixture.Editing.ExternalChangeConflict);
        Assert.Contains(fixture.Errors, message => message.Contains("auto-save is paused", StringComparison.Ordinal));
        using (var preview = fixture.BlueprintSources.Load(asset))
            Assert.Equal("Unsaved Hero", preview.Name);

        var conflictReportCount = fixture.Errors.Count(message =>
            message.Contains("auto-save is paused", StringComparison.Ordinal));
        fixture.Editing.Update(autoSave: true, TimeSpan.Zero);

        Assert.Equal(
            "Externally Rewritten Hero",
            DreambitJson.Deserialize<EntityBlueprint>(
                File.ReadAllText(Path.Combine(ContentRoot, "actors", "hero.blueprint.json")))!.Name);
        Assert.Equal(
            conflictReportCount,
            fixture.Errors.Count(message => message.Contains("auto-save is paused", StringComparison.Ordinal)));
    }

    [Fact]
    public void FailedAssetCreationDisposesTheInstanceAndLeavesNoSourceOrTemporaryFile()
    {
        using var fixture = CreateFixture();

        var created = fixture.Editing.TryCreate(
            typeof(ThrowingDisposeAsset),
            "broken/create.asset",
            out var error);

        Assert.False(created);
        Assert.Contains("Intentional asset cleanup failure", error);
        var folder = Path.Combine(ContentRoot, "broken");
        Assert.False(File.Exists(Path.Combine(folder, "create.asset")));
        Assert.Empty(Directory.EnumerateFiles(folder, "create.asset.*.tmp"));
    }

    [Fact]
    public void FailedAutosaveDoesNotRetryEveryUpdate()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Disk Hero");
        using var fixture = CreateFixture();
        var asset = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        fixture.Editing.Select(asset);
        fixture.Editing.Current!.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");
        var path = Path.Combine(ContentRoot, "actors", "hero.blueprint.json");

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fixture.Editing.Update(autoSave: true, TimeSpan.Zero);
            fixture.Editing.Update(autoSave: true, TimeSpan.Zero);
        }

        Assert.Single(fixture.Errors, message =>
            message.Contains("Could not auto-save", StringComparison.Ordinal));
        Assert.True(fixture.Editing.Current.IsDirty);

        // Explicit saves are never delayed by the background retry schedule.
        fixture.Editing.Save();
        Assert.False(fixture.Editing.Current.IsDirty);
    }

    [Fact]
    public void FailedSaveKeepsThePreviousAssetSelectedWhenSwitching()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Hero");
        WriteBlueprint("actors/villain.blueprint.json", "Villain");
        using var fixture = CreateFixture();
        var assets = fixture.Assets.GetSnapshot().Assets.ToDictionary(asset => asset.Name);
        var hero = assets["hero.blueprint.json"];
        var villain = assets["villain.blueprint.json"];
        Assert.True(fixture.Editing.Select(hero));
        var heroDocument = fixture.Editing.Current!;
        heroDocument.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");
        var heroPath = Path.Combine(ContentRoot, "actors", "hero.blueprint.json");

        using (File.Open(heroPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(fixture.Editing.Select(villain));

        Assert.Same(heroDocument, fixture.Editing.Current);
        Assert.Equal(hero.Id, fixture.Editing.Selected!.Id);
        Assert.Equal("Unsaved Hero", Assert.IsType<EntityBlueprint>(heroDocument.Instance).Name);
    }

    [Fact]
    public void FailedSaveKeepsThePreviousAssetSelectedWhenClearing()
    {
        WriteBlueprint("actors/hero.blueprint.json", "Hero");
        using var fixture = CreateFixture();
        var hero = Assert.Single(fixture.Assets.GetSnapshot().Assets);
        Assert.True(fixture.Editing.Select(hero));
        var heroDocument = fixture.Editing.Current!;
        heroDocument.Apply(
            "Rename Blueprint",
            instance => ((EntityBlueprint)instance).Name = "Unsaved Hero");
        var heroPath = Path.Combine(ContentRoot, "actors", "hero.blueprint.json");

        using (File.Open(heroPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(fixture.Editing.Clear());

        Assert.Same(heroDocument, fixture.Editing.Current);
        Assert.Equal(hero.Id, fixture.Editing.Selected!.Id);
    }

    [Fact]
    public void SelectingATiledMapDoesNotTryToOpenItAsAJsonAssetDocument()
    {
        var path = Path.Combine(ContentRoot, "maps", "world.tmx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<map version=\"1.10\" tiledversion=\"1.11.0\"/>");
        using var fixture = CreateFixture();
        var map = Assert.Single(fixture.Assets.GetSnapshot().Assets);

        Assert.True(fixture.Editing.Select(map));

        Assert.Equal(AssetKind.TiledMap, fixture.Editing.Selected!.Kind);
        Assert.Null(fixture.Editing.Current);
        Assert.Empty(fixture.Errors);
    }

    private Fixture CreateFixture()
    {
        var project = new DreambitProjectDefinition(
            _root,
            Path.Combine(_root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(_root, "Game.sln"),
            Path.Combine(_root, "Game.csproj"),
            Path.Combine(_root, "Game.Content.csproj"),
            ContentRoot,
            Path.Combine(_root, "Game.VK.csproj"));
        return new Fixture(project, _root, ContentRoot);
    }

    private void WriteBlueprint(string relativePath, string name)
    {
        var path = Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DreambitJson.Serialize(new EntityBlueprint { Name = name }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InspectorMetadataCache _metadata = new();

        public Fixture(DreambitProjectDefinition project, string root, string contentRoot)
        {
            Assets = new AssetDatabase(root, contentRoot, enableWatcher: false);
            Assemblies = new GameAssemblyLoadService(root);
            Types = new EditorTypeRegistry(Assemblies, _metadata);
            Editing = new AssetEditingService(
                project,
                Assets,
                Types,
                _metadata,
                Assemblies,
                (message, _) => Errors.Add(message));
            BlueprintSources = new BlueprintSourceService(Assets, Editing);
        }

        public AssetDatabase Assets { get; }
        public GameAssemblyLoadService Assemblies { get; }
        public EditorTypeRegistry Types { get; }
        public AssetEditingService Editing { get; }
        public BlueprintSourceService BlueprintSources { get; }
        public List<string> Errors { get; } = [];

        public void Dispose()
        {
            BlueprintSources.Dispose();
            Editing.Dispose();
            Types.Dispose();
            Assemblies.Dispose();
            Assets.Dispose();
        }
    }
}
