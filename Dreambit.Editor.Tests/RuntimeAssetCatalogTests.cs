using System.Text;
using Dreambit.Editor.Assets;
using DreambitEngine.AssetBaker.Pipeline;
using DreambitEngine.AssetBaker.Pipeline.Docs;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class RuntimeAssetCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dreambit.CatalogTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SchemasRetainStableIdAndNameResolutionWithOptionalType(int schema)
    {
        var id = AssetId.New();
        var entry = new JObject { ["id"] = id.ToString(), ["name"] = "Items/Sword.asset" };
        if (schema == 2)
            entry["type"] = "game.weapon";
        using var stream = CreateRegistryStream(schema, entry);
        var registry = RuntimeAssetRegistry.Load(stream);
        Assert.True(registry.TryResolveAssetName(id, out var name));
        Assert.Equal("Items/Sword.asset", name);
        Assert.True(registry.TryGetAssetId("items/SWORD.asset", out var resolved));
        Assert.Equal(id, resolved);
        Assert.True(registry.TryGetAssetId(" items\\SWORD.asset ", out resolved));
        Assert.Equal(id, resolved);
        Assert.False(registry.TryResolveAssetName(AssetId.New(), out _));
        Assert.False(registry.TryGetAssetId("missing", out _));
        Assert.Equal(new AssetCatalogEntry(id, name, schema == 2 ? "game.weapon" : null), Assert.Single(registry.GetAssets()));
        Assert.Same(registry.GetAssets(), registry.GetAssets());
        Assert.Throws<NotSupportedException>(() => ((IList<AssetCatalogEntry>)registry.GetAssets()).Clear());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("normalized-name")]
    [InlineData("empty-id")]
    [InlineData("empty-name")]
    public void RuntimeCatalogRejectsInvalidAndDuplicateEntries(string failure)
    {
        var first = new JObject { ["id"] = AssetId.New().ToString(), ["name"] = "Items/Sword.asset" };
        var second = new JObject { ["id"] = AssetId.New().ToString(), ["name"] = "Items/Other.asset" };
        switch (failure)
        {
            case "id": second["id"] = first["id"]!.DeepClone(); break;
            case "name": second["name"] = "items/sword.ASSET"; break;
            case "normalized-name": second["name"] = " items\\sword.ASSET "; break;
            case "empty-id": second["id"] = Guid.Empty.ToString(); break;
            case "empty-name": second["name"] = " "; break;
        }
        using var stream = CreateRegistryStream(2, first, second);
        Assert.Throws<InvalidDataException>(() => RuntimeAssetRegistry.Load(stream));
    }

    [Fact]
    public void UnsupportedRuntimeSchemaIsRejected()
    {
        using var stream = CreateRegistryStream(3);
        Assert.Throws<NotSupportedException>(() => RuntimeAssetRegistry.Load(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorClassificationSurvivesBlobAndPakBakeAndExcludesTombstonesAndSourceOnlyFiles(bool pak)
    {
        var source = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Content");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "sword.asset"),
            """{"$dreambitType":"game.weapon.former","Damage":42}""");
        File.WriteAllText(Path.Combine(source, "gone.asset"), """{"$dreambitType":"game.weapon"}""");
        File.WriteAllText(Path.Combine(source, "project.tiled-project"), "{}");
        File.WriteAllText(Path.Combine(source, "unknown.extension"), "source only");
        File.WriteAllText(Path.Combine(source, "styles.ucss"), "Text { width: 10px; }");
        using var database = new AssetDatabase(_root, source, enableWatcher: false);
        var gone = database.GetSnapshot().Assets.Single(asset => asset.RelativePath == "gone.asset");
        var sword = database.GetSnapshot().Assets.Single(asset => asset.RelativePath == "sword.asset");
        var originalSnapshot = database.GetAssets();
        Assert.Equal(2, originalSnapshot.Count);
        File.Delete(Path.Combine(source, "gone.asset"));
        database.RefreshNow();
        Assert.Single(database.GetAssets());
        Assert.Equal(2, originalSnapshot.Count);
        Assert.Same(database.GetAssets(), database.GetAssets());
        var sourceDocument = JObject.Parse(File.ReadAllText(database.RegistryPath));
        Assert.Equal("game.weapon.former", sourceDocument["assets"]!.Single(a => a.Value<string>("path") == "sword.asset").Value<string>("type"));
        Assert.Contains(sourceDocument["assets"]!, a => a.Value<string>("path") == "gone.asset");

        RuntimeAssetRegistry registry;
        var pipeline = new AssetBakePipeline();
        if (pak)
        {
            var path = Path.Combine(output, "content.pak");
            pipeline.BakePak(new AssetBakeRequest(source, path, database.RegistryPath));
            using var reader = new PakReader(path);
            using var stream = reader.Open(RuntimeAssetRegistry.LogicalPath);
            registry = RuntimeAssetRegistry.Load(stream);
        }
        else
        {
            pipeline.BakeBlobs(new AssetBlobBakeRequest(source, output, database.RegistryPath));
            using var stream = new BlobContentReader(output).Open(RuntimeAssetRegistry.LogicalPath);
            registry = RuntimeAssetRegistry.Load(stream);
        }
        Assert.Equal(new AssetCatalogEntry(sword.Id, "sword.asset", "game.weapon.former"), Assert.Single(registry.GetAssets()));
        Assert.False(registry.TryResolveAssetName(gone.Id, out _));
        Assert.Equal(database.GetAssets(), registry.GetAssets());
    }

    [Fact]
    public void RegistryBakeInvalidatesOldCacheSignatureAndTypeChanges()
    {
        var source = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Blobs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "sword.asset"), """{"$dreambitType":"source.type"}""");
        using var database = new AssetDatabase(_root, source, enableWatcher: false);
        var request = new AssetBlobBakeRequest(source, output, database.RegistryPath);
        var pipeline = new AssetBakePipeline();
        pipeline.BakeBlobs(request);

        var cachePath = Path.Combine(output, "cache.json");
        var cache = JObject.Parse(File.ReadAllText(cachePath));
        var entry = cache["entries"]!["registry/runtime"]!;
        entry["optionSignature"] = "runtime-registry-v4";
        var blobPath = Path.Combine(output, entry.Value<string>("blobFile")!);
        File.WriteAllText(blobPath, "old incompatible registry blob");
        File.WriteAllText(cachePath, cache.ToString());
        pipeline.BakeBlobs(request);
        Assert.Equal("source.type", ReadOnlyEntry(output).TypeId);

        // The bake must use the already-classified registry rather than reopening the .asset
        // to rediscover its type. Only registry metadata changes in this second bake.
        var registry = JObject.Parse(File.ReadAllText(database.RegistryPath));
        registry["assets"]![0]!["type"] = "registry.type.changed";
        File.WriteAllText(database.RegistryPath, registry.ToString());
        pipeline.BakeBlobs(request);
        Assert.Equal("registry.type.changed", ReadOnlyEntry(output).TypeId);
    }

    private static AssetCatalogEntry ReadOnlyEntry(string output)
    {
        using var stream = new BlobContentReader(output).Open(RuntimeAssetRegistry.LogicalPath);
        return Assert.Single(RuntimeAssetRegistry.Load(stream).GetAssets());
    }

    private static MemoryStream CreateRegistryStream(int schema, params JObject[] entries)
    {
        var document = new JObject { ["schemaVersion"] = schema, ["assets"] = new JArray(entries) };
        var stream = new MemoryStream();
        JsnbWriter.Write(stream, Encoding.UTF8.GetBytes(document.ToString()), 0);
        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
