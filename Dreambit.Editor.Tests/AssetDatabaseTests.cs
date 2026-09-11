using Dreambit;
using Dreambit.Editor.Assets;
using DreambitEngine.AssetBaker.Abstractions;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class AssetDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.AssetDatabaseTests",
        Guid.NewGuid().ToString("N"));

    private string ContentRoot => Path.Combine(_root, "Content", "Assets");

    public AssetDatabaseTests()
    {
        Directory.CreateDirectory(ContentRoot);
    }

    [Fact]
    public void InitialScanCreatesStableRegistryAndClassifiesSemanticTypes()
    {
        WriteAsset("characters/hero.sprite", "{\"source\":{}}");
        WriteAsset("characters/hero.texture.png", "not-a-real-png");

        AssetId spriteId;
        using (var database = CreateDatabase())
        {
            var snapshot = database.GetSnapshot();
            Assert.Equal(2, snapshot.Assets.Count);
            var sprite = Assert.Single(
                snapshot.Assets,
                asset => asset.RelativePath == "characters/hero.sprite");
            Assert.Equal(AssetKind.Sprite, sprite.Kind);
            Assert.Equal("dreambit.sprite", sprite.TypeId);
            Assert.Equal("characters/hero.sprite", sprite.LogicalAssetName);
            Assert.False(sprite.Id.IsEmpty);
            spriteId = sprite.Id;
            var texture = Assert.Single(
                snapshot.Assets,
                asset => asset.RelativePath == "characters/hero.texture.png");
            Assert.Equal(AssetKind.Texture, texture.Kind);
            Assert.Equal("dreambit.texture", texture.TypeId);
            Assert.True(File.Exists(database.RegistryPath));
        }

        using var reopened = CreateDatabase();
        Assert.True(reopened.TryGetAsset("characters/hero.sprite", out var reopenedSprite));
        Assert.Equal(spriteId, reopenedSprite!.Id);
    }

    [Fact]
    public void StylesheetKeepsExtensionInEditorLogicalName()
    {
        WriteAsset("Ui/main.ucss", "Text { width: 10px; }");

        using var database = CreateDatabase();
        var stylesheet = Assert.Single(database.GetSnapshot().Assets);

        Assert.Equal(AssetKind.Stylesheet, stylesheet.Kind);
        Assert.Equal("Ui/main.ucss", stylesheet.LogicalAssetName);
    }

    [Fact]
    public void EditorRenameAndFolderMovePreserveIdsAndRuntimeNames()
    {
        WriteAsset("characters/hero/hero.blueprint.json", "{}");
        WriteAsset("characters/hero/hero.sprite.json", "{}");
        using var database = CreateDatabase();
        var before = database.GetSnapshot().Assets.ToDictionary(
            asset => asset.Name,
            asset => asset.Id);

        Assert.True(database.TryRename(
            "characters/hero/hero.sprite.json",
            "player.sprite.json",
            out var renameError), renameError);
        Assert.True(database.TryCreateFolder("", "actors", out var createError), createError);
        Assert.True(database.TryMove("characters/hero", "actors", out var moveError), moveError);

        Assert.True(database.TryGetAsset(
            "actors/hero/player.sprite.json",
            out var movedSprite));
        Assert.True(database.TryGetAsset(
            "actors/hero/hero.blueprint.json",
            out var movedBlueprint));
        Assert.Equal(before["hero.sprite.json"], movedSprite!.Id);
        Assert.Equal(before["hero.blueprint.json"], movedBlueprint!.Id);
        Assert.True(database.TryResolveAssetName(movedSprite.Id, out var logicalName));
        Assert.Equal("actors/hero/player.sprite", logicalName);
    }

    [Fact]
    public void ExternalMoveIsRecoveredByFingerprintAcrossSessions()
    {
        WriteAsset("old/hero.animation.json", "{\"frames\":[1,2,3]}");
        AssetId originalId;
        using (var database = CreateDatabase())
            originalId = Assert.Single(database.GetSnapshot().Assets).Id;

        Directory.CreateDirectory(Path.Combine(ContentRoot, "new"));
        File.Move(
            Path.Combine(ContentRoot, "old", "hero.animation.json"),
            Path.Combine(ContentRoot, "new", "hero.animation.json"));

        using var reopened = CreateDatabase();
        var moved = Assert.Single(reopened.GetSnapshot().Assets);
        Assert.Equal("new/hero.animation.json", moved.RelativePath);
        Assert.Equal(originalId, moved.Id);
    }

    [Fact]
    public void DeleteLeavesTombstoneAndRestoringThePathRestoresTheId()
    {
        WriteAsset("audio/hit.soundcue.json", "{\"takes\":[]}");
        using var database = CreateDatabase();
        var id = Assert.Single(database.GetSnapshot().Assets).Id;

        Assert.True(database.TryDelete("audio/hit.soundcue.json", out var deleteError), deleteError);
        Assert.Empty(database.GetSnapshot().Assets);
        Assert.Equal(1, database.GetSnapshot().MissingAssetCount);
        Assert.True(database.TryResolveAssetName(id, out var missingName));
        Assert.Equal("audio/hit.soundcue", missingName);

        WriteAsset("audio/hit.soundcue.json", "{\"takes\":[]}");
        database.RefreshNow();
        Assert.Equal(id, Assert.Single(database.GetSnapshot().Assets).Id);
        Assert.Equal(0, database.GetSnapshot().MissingAssetCount);
    }

    [Fact]
    public void DuplicateGetsANewIdAndTraversalIsRejected()
    {
        WriteAsset("sprites/hero.sprite.json", "{}");
        using var database = CreateDatabase();
        var sourceId = Assert.Single(database.GetSnapshot().Assets).Id;

        Assert.True(database.TryDuplicate(
            "sprites/hero.sprite.json",
            out var duplicatePath,
            out var duplicateError), duplicateError);
        Assert.Equal("sprites/hero Copy.sprite.json", duplicatePath);
        Assert.True(database.TryGetAsset(duplicatePath!, out var duplicate));
        Assert.NotEqual(sourceId, duplicate!.Id);

        Assert.True(database.TryDelete(duplicatePath!, out var deleteError), deleteError);
        Assert.True(database.TryDuplicate(
            "sprites/hero.sprite.json",
            out var secondDuplicatePath,
            out var secondDuplicateError), secondDuplicateError);
        Assert.Equal("sprites/hero Copy 2.sprite.json", secondDuplicatePath);
        Assert.True(database.TryGetAsset(secondDuplicatePath!, out var secondDuplicate));
        Assert.NotEqual(duplicate.Id, secondDuplicate!.Id);

        Assert.False(database.TryCreateFolder("../outside", "bad", out var traversalError));
        Assert.Contains("cannot contain", traversalError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public void TextureSemanticPersistsAcrossEditorOperationsAndLegacyRegistriesMigrate()
    {
        WriteAsset("textures/wall.png", "png-source");
        var textureId = Guid.NewGuid();
        var registryPath = Path.Combine(_root, ".dreambit", "assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(
            registryPath,
            $$"""
              {
                "schemaVersion": 1,
                "assets": [
                  { "id": "{{textureId:D}}", "path": "textures/wall.png", "kind": "Texture" }
                ]
              }
              """);

        using var database = CreateDatabase();
        var legacyTexture = Assert.Single(database.GetSnapshot().Assets);
        Assert.Null(legacyTexture.ImportSettings);

        var previousVersion = database.GetSnapshot().Version;
        Assert.True(database.TrySetTextureSemantic(
            legacyTexture.Id,
            TextureSemantic.NormalMap,
            out var semanticError), semanticError);
        var configured = Assert.Single(database.GetSnapshot().Assets);
        Assert.Equal(TextureSemantic.NormalMap, configured.ImportSettings?.Texture?.Semantic);
        Assert.True(database.GetSnapshot().Version > previousVersion);

        Assert.True(database.TryRename(
            "textures/wall.png",
            "wall-normal.png",
            out var renameError), renameError);
        Assert.True(database.TryCreateFolder("", "materials", out var folderError), folderError);
        Assert.True(database.TryMove(
            "textures/wall-normal.png",
            "materials",
            out var moveError), moveError);
        Assert.True(database.TryDuplicate(
            "materials/wall-normal.png",
            out var duplicatePath,
            out var duplicateError), duplicateError);
        Assert.True(database.TryGetAsset(duplicatePath!, out var duplicate));
        Assert.Equal(TextureSemantic.NormalMap, duplicate!.ImportSettings?.Texture?.Semantic);

        Assert.True(database.TryDelete("materials/wall-normal.png", out var deleteError), deleteError);
        WriteAsset("materials/wall-normal.png", "png-source");
        database.RefreshNow();
        Assert.True(database.TryGetAsset("materials/wall-normal.png", out var restored));
        Assert.Equal(configured.Id, restored!.Id);
        Assert.Equal(TextureSemantic.NormalMap, restored.ImportSettings?.Texture?.Semantic);

        var registry = JObject.Parse(File.ReadAllText(registryPath));
        Assert.Equal(2, registry.Value<int>("schemaVersion"));
        var entries = Assert.IsType<JArray>(registry["assets"]);
        Assert.All(entries, entry =>
            Assert.Equal("NormalMap", entry["importSettings"]?["texture"]?.Value<string>("semantic")));
    }

    [Fact]
    public void TextureSemanticRejectsNonTexturesAndColorUsesDefaultMetadata()
    {
        WriteAsset("notes/readme.txt", "hello");
        WriteAsset("textures/color.png", "png-source");
        using var database = CreateDatabase();
        var assets = database.GetSnapshot().Assets.ToDictionary(asset => asset.RelativePath);

        Assert.False(database.TrySetTextureSemantic(
            assets["notes/readme.txt"].Id,
            TextureSemantic.NormalMap,
            out var nonTextureError));
        Assert.Contains("not a live texture", nonTextureError, StringComparison.OrdinalIgnoreCase);

        Assert.True(database.TrySetTextureSemantic(
            assets["textures/color.png"].Id,
            TextureSemantic.NormalMap,
            out var normalError), normalError);
        Assert.True(database.TrySetTextureSemantic(
            assets["textures/color.png"].Id,
            TextureSemantic.Color,
            out var colorError), colorError);
        Assert.Null(Assert.Single(
            database.GetSnapshot().Assets,
            asset => asset.RelativePath == "textures/color.png").ImportSettings);
    }

    [Fact]
    public void WatcherCoalescesAnExternalCreateIntoTheSnapshot()
    {
        using var database = CreateDatabase(enableWatcher: true);
        WriteAsset("external/new.scene.json", "{}");

        var observed = SpinWait.SpinUntil(() =>
        {
            Thread.Sleep(25);
            database.Update();
            return database.GetSnapshot().Assets.Any(asset =>
                asset.RelativePath == "external/new.scene.json");
        }, TimeSpan.FromSeconds(5));

        Assert.True(observed, "The external file was not observed by the filesystem watcher.");
    }

    [Fact]
    public void TaggedReferencesAreUnambiguousAndLegacyPathsStillSerialize()
    {
        var id = AssetId.New();
        var taggedJson = DreambitJson.Serialize(new AssetHolder
        {
            Asset = new TestAsset { AssetId = id, AssetName = "sprites/hero.sprite" }
        });
        var tagged = JObject.Parse(taggedJson)["Asset"]!;
        Assert.True(DreambitAssetReferenceToken.TryRead(tagged, out var parsedId, out var fallback));
        Assert.Equal(id, parsedId);
        Assert.Equal("sprites/hero.sprite", fallback);

        var legacyJson = DreambitJson.Serialize(new AssetHolder
        {
            Asset = new TestAsset { AssetName = "sprites/legacy.sprite" }
        });
        Assert.Equal("sprites/legacy.sprite", JObject.Parse(legacyJson).Value<string>("Asset"));
    }

    [Fact]
    public void GenericJsonClassificationPreservesRawDreambitTypeIdsAndOrdinaryJson()
    {
        WriteAsset(
            "items/weapon.asset",
            "{\"$dreambitType\":\"game.weapon\",\"Damage\":25}");
        WriteAsset(
            "items/missing.asset",
            "{\"$dreambitType\":\"game.deleted-type\"}");
        WriteAsset("data/settings.json", "{\"volume\":0.5}");
        WriteAsset("data/list.json", "[1,2,3]");
        WriteAsset("data/empty-type.json", "{\"$dreambitType\":\"\"}");
        WriteAsset("data/malformed.json", "{ invalid json");
        var diagnostics = new List<AssetDatabaseDiagnostic>();

        using var database = CreateDatabase(reportDiagnostic: diagnostics.Add);
        var assets = database.GetSnapshot().Assets.ToDictionary(asset => asset.RelativePath);

        Assert.Equal(AssetKind.DreambitAsset, assets["items/weapon.asset"].Kind);
        Assert.Equal("game.weapon", assets["items/weapon.asset"].TypeId);
        Assert.Equal("items/weapon.asset", assets["items/weapon.asset"].LogicalAssetName);
        Assert.Equal(AssetKind.DreambitAsset, assets["items/missing.asset"].Kind);
        Assert.Equal("game.deleted-type", assets["items/missing.asset"].TypeId);
        Assert.Equal(AssetKind.Json, assets["data/settings.json"].Kind);
        Assert.Null(assets["data/settings.json"].TypeId);
        Assert.Equal(AssetKind.Json, assets["data/list.json"].Kind);
        Assert.Null(assets["data/list.json"].TypeId);
        Assert.Equal(AssetKind.Json, assets["data/empty-type.json"].Kind);
        Assert.Null(assets["data/empty-type.json"].TypeId);
        Assert.Equal(AssetKind.Json, assets["data/malformed.json"].Kind);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Path == "data/empty-type.json" &&
            diagnostic.Message.Contains("non-empty string", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Path == "data/malformed.json" &&
            diagnostic.Message.Contains("Could not inspect JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomAssetTypeIdSurvivesRestartMoveRenameAndDuplication()
    {
        WriteAsset(
            "items/weapon.json",
            "{\"$dreambitType\":\"game.weapon\",\"Damage\":25}");
        AssetId originalId;
        using (var initial = CreateDatabase())
        {
            var original = Assert.Single(initial.GetSnapshot().Assets);
            originalId = original.Id;
            Assert.Equal("game.weapon", original.TypeId);
        }

        using var reopened = CreateDatabase();
        Assert.True(reopened.TryRename("items/weapon.json", "rifle.json", out var renameError), renameError);
        Assert.True(reopened.TryCreateFolder("", "equipment", out var folderError), folderError);
        Assert.True(reopened.TryMove("items/rifle.json", "equipment", out var moveError), moveError);
        Assert.True(reopened.TryGetAsset("equipment/rifle.json", out var moved));
        Assert.Equal(originalId, moved!.Id);
        Assert.Equal("game.weapon", moved.TypeId);

        Assert.True(reopened.TryDuplicate(
            "equipment/rifle.json",
            out var duplicatePath,
            out var duplicateError), duplicateError);
        Assert.True(reopened.TryGetAsset(duplicatePath!, out var duplicate));
        Assert.NotEqual(originalId, duplicate!.Id);
        Assert.Equal("game.weapon", duplicate.TypeId);
    }

    [Fact]
    public void UnchangedFilesReusePersistedClassificationWithoutReopeningJson()
    {
        WriteAsset(
            "items/weapon.json",
            "{\"$dreambitType\":\"game.weapon\",\"Damage\":25}");
        using (var initial = CreateDatabase())
            Assert.Equal("game.weapon", Assert.Single(initial.GetSnapshot().Assets).TypeId);

        var path = Path.Combine(ContentRoot, "items", "weapon.json");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reopened = CreateDatabase();
        var asset = Assert.Single(reopened.GetSnapshot().Assets);
        Assert.Equal(AssetKind.DreambitAsset, asset.Kind);
        Assert.Equal("game.weapon", asset.TypeId);
    }

    [Fact]
    public void ChangedGenericJsonRefreshesItsStoredTypeId()
    {
        WriteAsset("items/weapon.json", "{\"$dreambitType\":\"game.weapon\"}");
        using var database = CreateDatabase();
        Assert.Equal("game.weapon", Assert.Single(database.GetSnapshot().Assets).TypeId);

        Thread.Sleep(20);
        WriteAsset("items/weapon.json", "{\"$dreambitType\":\"game.weapon.v2\"}");
        database.RefreshNow();

        Assert.Equal("game.weapon.v2", Assert.Single(database.GetSnapshot().Assets).TypeId);
    }

    private AssetDatabase CreateDatabase(
        bool enableWatcher = false,
        Action<AssetDatabaseDiagnostic>? reportDiagnostic = null) =>
        new(_root, ContentRoot, reportDiagnostic, enableWatcher);

    private void WriteAsset(string relativePath, string contents)
    {
        var path = Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class AssetHolder
    {
        public TestAsset? Asset { get; set; }
    }

    private sealed class TestAsset : DreambitAsset;
}
