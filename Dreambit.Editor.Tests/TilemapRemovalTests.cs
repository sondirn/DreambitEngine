using Dreambit.Editor.Assets;
using DreambitEngine.AssetBaker.Pipeline;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class TilemapRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dreambit.TilemapRemovalTests", Guid.NewGuid().ToString("N"));
    private static readonly string[] UnsupportedExtensions = [".tmx", ".tsx", ".tx", ".tmj", ".tsj", ".tileset", ".tiled-project", ".tiled-session"];

    [Theory]
    [InlineData("TiledMap")]
    [InlineData("ReservedLegacyTilemap")]
    [InlineData(17)]
    [InlineData(16)]
    public void RetiredRegistryKindsAreReclassifiedWithoutLosingAssetIds(object retiredKind)
    {
        var source = CreateSources();
        string registryPath;
        Dictionary<string, AssetId> ids;
        using (var database = new AssetDatabase(_root, source, enableWatcher: false))
        {
            registryPath = database.RegistryPath;
            ids = database.GetSnapshot().Assets.ToDictionary(asset => asset.RelativePath, asset => asset.Id);
        }

        var registry = JObject.Parse(File.ReadAllText(registryPath));
        foreach (var entry in registry["assets"]!)
        {
            entry["classificationVersion"] = 7;
            if (entry.Value<string>("path") == "world.tmx")
            {
                entry["kind"] = JToken.FromObject(retiredKind);
                entry["type"] = "dreambit.tiled.map";
            }
            if (entry.Value<string>("path") == "world.tileset")
            {
                entry["kind"] = "DreambitAsset";
                entry["type"] = "dreambit.tileset";
            }
        }
        File.WriteAllText(registryPath, registry.ToString());

        using var reopened = new AssetDatabase(_root, source, enableWatcher: false);
        foreach (var asset in reopened.GetSnapshot().Assets)
        {
            Assert.Equal(ids[asset.RelativePath], asset.Id);
            if (asset.RelativePath == "world.scene")
                Assert.Equal(AssetKind.Scene, asset.Kind);
            else
            {
                Assert.Equal(AssetKind.Unknown, asset.Kind);
                Assert.Null(asset.TypeId);
            }
        }
        Assert.Equal("world.scene", Assert.Single(reopened.GetAssets()).AssetName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedMapFormatsDoNotEnterBakedContentOrRuntimeCatalog(bool pak)
    {
        var source = CreateSources();
        using var database = new AssetDatabase(_root, source, enableWatcher: false);
        var output = Path.Combine(_root, "Content");
        var pipeline = new AssetBakePipeline();
        var sceneId = database.GetSnapshot().Assets.Single(asset => asset.Kind == AssetKind.Scene).Id;

        if (pak)
        {
            var path = Path.Combine(output, "content.pak");
            var result = pipeline.BakePak(new AssetBakeRequest(source, path, database.RegistryPath));
            Assert.Equal(UnsupportedExtensions.Length, result.UnsupportedCount);
            using var reader = new PakReader(path);
            using var stream = reader.Open(RuntimeAssetRegistry.LogicalPath);
            Assert.Equal(new AssetCatalogEntry(sceneId, "world.scene", "dreambit.blueprint.scene"),
                Assert.Single(RuntimeAssetRegistry.Load(stream).GetAssets()));
        }
        else
        {
            var result = pipeline.BakeBlobs(new AssetBlobBakeRequest(source, output, database.RegistryPath));
            Assert.Equal(UnsupportedExtensions.Length, result.UnsupportedCount);
            var manifest = JObject.Parse(File.ReadAllText(result.ManifestPath));
            var paths = manifest["assets"]!.Select(entry => entry.Value<string>("path")).ToArray();
            Assert.Equal(2, paths.Length); // The authored scene and runtime registry only.
            Assert.Contains("world.scene.jsonb", paths);
            using var stream = new BlobContentReader(output).Open(RuntimeAssetRegistry.LogicalPath);
            Assert.Equal(new AssetCatalogEntry(sceneId, "world.scene", "dreambit.blueprint.scene"),
                Assert.Single(RuntimeAssetRegistry.Load(stream).GetAssets()));
        }
    }

    private string CreateSources()
    {
        var source = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(source);
        foreach (var extension in UnsupportedExtensions)
            File.WriteAllText(Path.Combine(source, "world" + extension), "source only");
        File.WriteAllText(Path.Combine(source, "world.scene"), """{"name":"World","entities":[]}""");
        return source;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
