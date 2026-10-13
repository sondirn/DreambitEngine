using Dreambit;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Projects;
using Dreambit.UI;
using DreambitEngine.AssetBaker.Abstractions;
using DreambitEngine.AssetBaker.Pipeline;
using DreambitEngine.AssetBaker.Pipeline.Docs;
using DreambitEngine.AssetBaker.Pipeline.Textures;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dreambit.Editor.Tests;

public sealed class AssetBakePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.AssetBakePipelineTests",
        Guid.NewGuid().ToString("N"));

    public AssetBakePipelineTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task BlobBakesWaitForTheCurrentCacheWriterBeforeReadingSources()
    {
        var assets = Path.Combine(_root, "LeaseAssets");
        var cache = Path.Combine(_root, "LeaseCache");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(assets, "settings.json"), "{\"version\":1}");

        var waiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<AssetBakeProgress>(value =>
        {
            if (value.Stage == "Wait")
                waiting.TrySetResult();
        });
        var leasePath = Path.Combine(cache, "bake.lock");
        var lease = new FileStream(
            leasePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        Task<AssetBlobBakeResult>? bake = null;
        try
        {
            bake = new AssetBakePipeline().BakeBlobsAsync(
                new AssetBlobBakeRequest(assets, cache),
                progress);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(bake.IsCompleted);
        }
        finally
        {
            lease.Dispose();
        }

        var result = await bake!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.BakedCount);
        Assert.True(File.Exists(result.ManifestPath));
    }

    [Fact]
    public void DebugRuntimeSnapshotPublishesRapidSameSizeBlobChanges()
    {
        var assets = Path.Combine(_root, "RuntimeSnapshotAssets");
        var cache = Path.Combine(_root, "RuntimeSnapshotCache");
        var output = Path.Combine(_root, "RuntimeSnapshotOutput");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "settings.json");
        var request = new AssetBlobBakeRequest(
            assets,
            cache)
        {
            RuntimeOutputDirectory = output
        };
        var pipeline = new AssetBakePipeline();

        File.WriteAllText(source, "{\"version\":1}");
        pipeline.BakeBlobs(request);
        Assert.Equal(1, ReadBakedJsonVersion(output));

        // Both sources serialize to the same length. The old MSBuild copy could treat the blob
        // as unchanged when this happened inside the filesystem's timestamp resolution window.
        File.WriteAllText(source, "{\"version\":2}");
        pipeline.BakeBlobs(request);

        Assert.Equal(2, ReadBakedJsonVersion(output));
        Assert.Equal(
            File.ReadAllText(Path.Combine(cache, BlobContentManifest.FingerprintFileName)),
            File.ReadAllText(Path.Combine(output, BlobContentManifest.FingerprintFileName)));
    }

    private static int ReadBakedJsonVersion(string contentDirectory)
    {
        var reader = new BlobContentReader(contentDirectory);
        using var stream = reader.Open("settings.jsonb");
        return JObject.Parse(JsnbLoader.GetJsonString(stream)).Value<int>("version");
    }

    [Fact]
    public void CssBakerProducesVersionedRuntimeLoadableText()
    {
        var assets = Path.Combine(_root, "CssAssets");
        var output = Path.Combine(_root, "CssBlobs");
        Directory.CreateDirectory(Path.Combine(assets, "Ui"));
        const string css = "/* Grüße */ Text { text: \"Crème brûlée\"; color: #FFFFFF; width: 100px; }";
        File.WriteAllText(Path.Combine(assets, "Ui", "master.ucss"), css);

        var result = new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            output,
            RebuildAll: true));

        Assert.Equal(1, result.BakedCount);
        var reader = new BlobContentReader(output);
        using var stream = reader.Open("ui/master.cssb");
        Assert.Equal(css, CssbLoader.GetStylesheet(stream));

        var second = new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            output));
        Assert.Equal(0, second.BakedCount);
        Assert.Equal(1, second.CacheHitCount);
    }

    [Fact]
    public void CssBakerRejectsMalformedStylesheetBeforePublishing()
    {
        var assets = Path.Combine(_root, "BadCssAssets");
        var output = Path.Combine(_root, "BadCssBlobs");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "bad.ucss"), "Text { width: 100; }");

        var exception = Assert.Throws<UiStylesheetException>(() =>
            new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
                assets,
                output,
                RebuildAll: true)));

        Assert.Contains("width", exception.Message);
        Assert.False(File.Exists(Path.Combine(output, BlobContentManifest.FileName)));
    }

    [Theory]
    [InlineData("width", "-100px")]
    [InlineData("height", "-50%")]
    [InlineData("font-size", "-24px")]
    public void CssBakerRejectsNegativeSizesBeforePublishing(
        string property,
        string value)
    {
        var assets = Path.Combine(_root, "NegativeCssAssets", property);
        var output = Path.Combine(_root, "NegativeCssBlobs", property);
        Directory.CreateDirectory(assets);
        File.WriteAllText(
            Path.Combine(assets, "bad.ucss"),
            $"Text {{ {property}: {value}; }}");

        var exception = Assert.Throws<UiStylesheetException>(() =>
            new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
                assets,
                output,
                RebuildAll: true)));

        Assert.Contains(property, exception.Message);
        Assert.False(File.Exists(Path.Combine(output, BlobContentManifest.FileName)));
    }

    [Fact]
    public void SameStemXmlAndCssDoNotCollideInRuntimeRegistry()
    {
        var assets = Path.Combine(_root, "RegistryCssAssets");
        var output = Path.Combine(_root, "RegistryCssContent", "content.pak");
        var registry = Path.Combine(_root, "RegistryCss", "assets.json");
        Directory.CreateDirectory(Path.Combine(assets, "Ui"));
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(Path.Combine(assets, "Ui", "main.uxml"), "<Ui />");
        File.WriteAllText(Path.Combine(assets, "Ui", "main.ucss"), "Text { width: 10px; }");
        var xmlId = Guid.NewGuid();
        var cssId = Guid.NewGuid();
        File.WriteAllText(
            registry,
            $$"""
              {
                "schemaVersion": 2,
                "assets": [
                  { "id": "{{xmlId:D}}", "path": "Ui/main.uxml" },
                  { "id": "{{cssId:D}}", "path": "Ui/main.ucss" }
                ]
              }
              """);

        new AssetBakePipeline().BakePak(new AssetBakeRequest(
            assets,
            output,
            registry,
            RebuildAll: true));

        using var pak = new PakReader(output);
        using (var xml = pak.Open("ui/main.xmlb"))
            Assert.Contains("<Ui", XmlbLoader.GetXmlString(xml));
        using (var css = pak.Open("ui/main.cssb"))
            Assert.Contains("width", CssbLoader.GetStylesheet(css));
        using var registryStream = pak.Open(RuntimeAssetRegistry.LogicalPath);
        var runtimeRegistry = RuntimeAssetRegistry.Load(registryStream);
        Assert.True(runtimeRegistry.TryResolveAssetName(new AssetId(xmlId), out var xmlName));
        Assert.Equal("Ui/main", xmlName);
        Assert.False(runtimeRegistry.TryResolveAssetName(new AssetId(cssId), out _));
    }

    [Fact]
    public void RuntimeRegistryIgnoresDeletedSourceTombstones()
    {
        var assets = Path.Combine(_root, "RegistryTombstoneAssets");
        var output = Path.Combine(_root, "RegistryTombstoneContent", "content.pak");
        var registry = Path.Combine(_root, "RegistryTombstone", "assets.json");
        Directory.CreateDirectory(Path.Combine(assets, "Ui"));
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(Path.Combine(assets, "Ui", "main-title.uxml"), "<Ui />");
        var deletedXmlId = Guid.NewGuid();
        var deletedUxmlId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        File.WriteAllText(
            registry,
            $$"""
              {
                "schemaVersion": 2,
                "assets": [
                  { "id": "{{deletedXmlId:D}}", "path": "Ui/main-menu.xml" },
                  { "id": "{{deletedUxmlId:D}}", "path": "Ui/main-menu.uxml" },
                  { "id": "{{currentId:D}}", "path": "Ui/main-title.uxml" }
                ]
              }
              """);

        new AssetBakePipeline().BakePak(new AssetBakeRequest(
            assets,
            output,
            registry,
            RebuildAll: true));

        using var pak = new PakReader(output);
        using var registryStream = pak.Open(RuntimeAssetRegistry.LogicalPath);
        var runtimeRegistry = RuntimeAssetRegistry.Load(registryStream);
        Assert.True(runtimeRegistry.TryResolveAssetName(new AssetId(currentId), out var logicalName));
        Assert.Equal("Ui/main-title", logicalName);
        Assert.False(runtimeRegistry.TryResolveAssetName(new AssetId(deletedXmlId), out _));
        Assert.False(runtimeRegistry.TryResolveAssetName(new AssetId(deletedUxmlId), out _));
    }

    [Fact]
    public void BakeIsDeterministicIncrementalAndEmbedsRuntimeRegistry()
    {
        var assets = Path.Combine(_root, "Assets");
        var cache = Path.Combine(_root, "cache");
        var output = Path.Combine(_root, "Content", "content.pak");
        var registry = Path.Combine(_root, ".dreambit", "assets.json");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(Path.Combine(assets, "hero.sprite"), "{\"name\":\"hero\"}");
        var id = Guid.NewGuid();
        File.WriteAllText(
            registry,
            $$"""
              {
                "schemaVersion": 1,
                "assets": [
                  { "id": "{{id:D}}", "path": "hero.sprite", "kind": "Sprite" }
                ]
              }
              """);

        var pipeline = new AssetBakePipeline();
        var request = new AssetBakeRequest(assets, output, registry, cache);
        var first = pipeline.BakePak(request);
        var firstBytes = File.ReadAllBytes(output);
        var second = pipeline.BakePak(request);
        var secondBytes = File.ReadAllBytes(output);

        Assert.Equal(1, first.BakedCount);
        Assert.Equal(0, first.CacheHitCount);
        Assert.Equal(0, second.BakedCount);
        Assert.Equal(1, second.CacheHitCount);
        Assert.Equal(firstBytes, secondBytes);

        using var pak = new PakReader(output);
        using var stream = pak.Open(RuntimeAssetRegistry.LogicalPath);
        var runtimeRegistry = RuntimeAssetRegistry.Load(stream);
        Assert.True(runtimeRegistry.TryResolveAssetName(new AssetId(id), out var name));
        Assert.Equal("hero.sprite", name);
    }

    [Fact]
    public void RuntimeRegistryIgnoresTiledEditorMetadataButKeepsRuntimeMaps()
    {
        var assets = Path.Combine(_root, "TiledAssets");
        var output = Path.Combine(_root, "TiledContent", "content.pak");
        var registry = Path.Combine(_root, ".dreambit", "tiled-assets.json");
        Directory.CreateDirectory(Path.Combine(assets, "maps"));
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);

        File.WriteAllText(Path.Combine(assets, "maps", "Rootbound.tiled-project"), "{}");
        File.WriteAllText(Path.Combine(assets, "maps", "Rootbound.tiled-session"), "{}");
        File.WriteAllText(
            Path.Combine(assets, "maps", "Rootbound.tmx"),
            "<map version=\"1.10\" tiledversion=\"1.11.2\" orientation=\"orthogonal\" " +
            "renderorder=\"right-down\" width=\"1\" height=\"1\" tilewidth=\"16\" tileheight=\"16\"/>");

        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var mapId = Guid.NewGuid();
        File.WriteAllText(
            registry,
            $$"""
              {
                "schemaVersion": 1,
                "assets": [
                  { "id": "{{projectId:D}}", "path": "maps/Rootbound.tiled-project", "kind": "Unknown" },
                  { "id": "{{sessionId:D}}", "path": "maps/Rootbound.tiled-session", "kind": "Unknown" },
                  { "id": "{{mapId:D}}", "path": "maps/Rootbound.tmx", "kind": "TiledMap" }
                ]
              }
              """);

        new AssetBakePipeline().BakePak(new AssetBakeRequest(assets, output, registry));

        using var pak = new PakReader(output);
        using var stream = pak.Open(RuntimeAssetRegistry.LogicalPath);
        var runtimeRegistry = RuntimeAssetRegistry.Load(stream);
        Assert.False(runtimeRegistry.TryResolveAssetName(new AssetId(projectId), out _));
        Assert.False(runtimeRegistry.TryResolveAssetName(new AssetId(sessionId), out _));
        Assert.True(runtimeRegistry.TryResolveAssetName(new AssetId(mapId), out var mapName));
        Assert.Equal("maps/Rootbound", mapName);
    }

    [Fact]
    public void BlobBakeWritesNoPakAndCanBeLoadedByLogicalPath()
    {
        var assets = Path.Combine(_root, "BlobAssets");
        var cache = Path.Combine(_root, "BlobCache");
        Directory.CreateDirectory(Path.Combine(assets, "levels"));
        File.WriteAllText(
            Path.Combine(assets, "levels", "first.scene"),
            "{\"name\":\"From blobs\",\"entities\":[]}");

        var result = new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            cache,
            RebuildAll: true));

        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(Path.Combine(cache, BlobContentManifest.FingerprintFileName)));
        Assert.False(File.Exists(Path.Combine(cache, "content.pak")));

        var originalMode = Resources.ContentMode;
        try
        {
            Resources.SetBlobContentSource(cache);
            var loaded = Assert.IsType<SceneBlueprint>(
                new SceneBlueprintLoader().Load(
                    "levels/first.scene",
                    "content.pak",
                    Resources.UsePak,
                    Resources.ActiveContentDirectory));
            Assert.Equal("From blobs", loaded.Name);

            Resources.RefreshContent();
            Resources.ContentMode = AssetContentMode.Auto;
            var autoLoaded = Assert.IsType<SceneBlueprint>(
                new SceneBlueprintLoader().Load(
                    "levels/first.scene",
                    "content.pak",
                    Resources.UsePak,
                    Resources.ActiveContentDirectory));
            Assert.Equal("From blobs", autoLoaded.Name);
        }
        finally
        {
            Resources.ResetContentSource();
            Resources.ContentMode = originalMode;
        }
    }

    [Fact]
    public void IncrementalBlobBakeDoesNotMaterializeCachedPayloads()
    {
        const int payloadLength = 8 * 1024 * 1024;
        var assets = Path.Combine(_root, "LargeBlobAssets");
        var cache = Path.Combine(_root, "LargeBlobCache");
        Directory.CreateDirectory(assets);
        File.WriteAllText(
            Path.Combine(assets, "large.txt"),
            new string('x', payloadLength));

        var pipeline = new AssetBakePipeline();
        var request = new AssetBlobBakeRequest(assets, cache, RebuildAll: false);
        var first = pipeline.BakeBlobs(request);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var second = pipeline.BakeBlobs(request);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(1, first.BakedCount);
        Assert.Equal(1, second.CacheHitCount);
        Assert.Equal(first.OutputLength, second.OutputLength);
        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
        Assert.True(
            allocated < payloadLength / 2,
            $"A cached {payloadLength:N0}-byte blob allocated {allocated:N0} bytes while baking.");
    }

    [Fact]
    public void PakFingerprintBecomesStaleWhenBlobsChange()
    {
        var assets = Path.Combine(_root, "FingerprintAssets");
        var cache = Path.Combine(_root, "FingerprintCache");
        var output = Path.Combine(_root, "FingerprintContent", "content.pak");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "data.json");
        File.WriteAllText(source, "{\"version\":1}");
        var pipeline = new AssetBakePipeline();

        pipeline.BakePak(new AssetBakeRequest(
            assets,
            output,
            CacheDirectory: cache,
            RebuildAll: true));
        Assert.Equal(
            File.ReadAllText(Path.Combine(cache, BlobContentManifest.FingerprintFileName)).Trim(),
            File.ReadAllText(output + ".fingerprint").Trim());

        File.WriteAllText(source, "{\"version\":2}");
        pipeline.BakeBlobs(new AssetBlobBakeRequest(assets, cache));

        Assert.NotEqual(
            File.ReadAllText(Path.Combine(cache, BlobContentManifest.FingerprintFileName)).Trim(),
            File.ReadAllText(output + ".fingerprint").Trim());
    }

    [Fact]
    public void FailedBakeDoesNotReplaceLastKnownGoodPak()
    {
        var assets = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Content", "content.pak");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "data.json");
        File.WriteAllText(source, "{\"valid\":true}");
        var pipeline = new AssetBakePipeline();
        var request = new AssetBakeRequest(assets, output, RebuildAll: true);
        pipeline.BakePak(request);
        var goodBytes = File.ReadAllBytes(output);

        File.WriteAllText(source, "{ invalid json");
        Assert.ThrowsAny<Exception>(() => pipeline.BakePak(request));
        Assert.Equal(goodBytes, File.ReadAllBytes(output));
    }

    [Fact]
    public void GenericJsonBakePreservesDreambitTypeMetadata()
    {
        var assets = Path.Combine(_root, "CustomAssets");
        var output = Path.Combine(_root, "CustomContent", "content.pak");
        Directory.CreateDirectory(assets);
        File.WriteAllText(
            Path.Combine(assets, "weapon.asset"),
            "{\"$dreambitType\":\"test.custom-asset\",\"Health\":100}");

        new AssetBakePipeline().BakePak(new AssetBakeRequest(assets, output, RebuildAll: true));

        using var pak = new PakReader(output);
        using var stream = pak.Open("weapon.asset.jsonb");
        var baked = JObject.Parse(JsnbLoader.GetJsonString(stream));
        Assert.Equal("test.custom-asset", baked.Value<string>("$dreambitType"));
        Assert.Equal(100, baked.Value<int>("Health"));
    }

    [Fact]
    public void BakeAtomicallyReplacesPakWhileTheEditorIsReadingThePreviousVersion()
    {
        var assets = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Content", "content.pak");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "data.json");
        File.WriteAllText(source, "{\"version\":1}");
        var pipeline = new AssetBakePipeline();
        var request = new AssetBakeRequest(assets, output, RebuildAll: true);
        pipeline.BakePak(request);

        using var previousPak = new PakReader(output);
        using var previousStream = previousPak.Open("data.jsonb");
        File.WriteAllText(source, "{\"version\":2}");

        pipeline.BakePak(request);

        Assert.NotEqual(-1, previousStream.ReadByte());
        using var replacementPak = new PakReader(output);
        using var replacementStream = replacementPak.Open("data.jsonb");
        Assert.NotEqual(-1, replacementStream.ReadByte());
    }

    [Fact]
    public void SceneAssetsBakeToTheRuntimeNameAcceptedByTheSceneLoader()
    {
        var assets = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Content", "content.pak");
        Directory.CreateDirectory(Path.Combine(assets, "levels"));
        File.WriteAllText(
            Path.Combine(assets, "levels", "first.scene"),
            "{\"name\":\"First\",\"entities\":[]}");

        new AssetBakePipeline().BakePak(new AssetBakeRequest(assets, output, RebuildAll: true));

        using (var pak = new PakReader(output))
        using (var stream = pak.Open("levels/first.scene.jsonb"))
            Assert.True(stream.Length > 0);

        var originalMode = Resources.ContentMode;
        SceneBlueprint loaded;
        try
        {
            // An explicit PAK load must remain independent of an editor preview
            // temporarily using loose files or incremental blobs.
            Resources.ContentMode = AssetContentMode.LooseFiles;
            var loader = new SceneBlueprintLoader();
            loaded = Assert.IsType<SceneBlueprint>(
                loader.Load("levels/first.scene", "content.pak", true, Path.GetDirectoryName(output)!));
        }
        finally
        {
            Resources.ContentMode = originalMode;
        }
        Assert.Equal("First", loaded.Name);
        Assert.Equal("levels/first.scene", loaded.AssetName);
    }

    [Fact]
    public void CutsceneAssetsKeepTheirSemanticExtensionWhenBakedAndLoaded()
    {
        var assets = Path.Combine(_root, "CutsceneAssets");
        var output = Path.Combine(_root, "CutsceneContent", "content.pak");
        Directory.CreateDirectory(assets);
        File.WriteAllText(
            Path.Combine(assets, "intro.cutscene"),
            "- scriptGroup:\n    - script: IntroAction\n");

        new AssetBakePipeline().BakePak(new AssetBakeRequest(assets, output, RebuildAll: true));

        using (var pak = new PakReader(output))
        using (var stream = pak.Open("intro.cutscene.yamlb"))
            Assert.True(stream.Length > 0);

        var originalMode = Resources.ContentMode;
        Dreambit.Scripting.Cutscene loaded;
        try
        {
            // An explicit PAK request must not inherit an earlier blob-preview
            // source selected by another editor test.
            Resources.ContentMode = AssetContentMode.LooseFiles;
            loaded = Assert.IsType<Dreambit.Scripting.Cutscene>(
                new CutsceneLoader().Load(
                    "intro.cutscene",
                    "content.pak",
                    true,
                    Path.GetDirectoryName(output)!));
        }
        finally
        {
            Resources.ContentMode = originalMode;
        }
        Assert.Equal("intro.cutscene", loaded.AssetName);
        Assert.Equal("IntroAction", Assert.Single(Assert.Single(loaded.Groups).Actions).Script);
    }

    [Fact]
    public void EditorBakesIntoRepositoryCacheInsteadOfLauncherOutput()
    {
        var contentRoot = Path.Combine(_root, "src", "Game.Content", "Assets");
        Directory.CreateDirectory(contentRoot);
        var project = new DreambitProjectDefinition(
            _root,
            Path.Combine(_root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(_root, "Game.sln"),
            Path.Combine(_root, "src", "Game", "Game.csproj"),
            Path.Combine(_root, "src", "Game.Content", "Game.Content.csproj"),
            contentRoot,
            Path.Combine(_root, "src", "Game.VK", "Game.VK.csproj"));
        using var assets = new AssetDatabase(_root, contentRoot);
        using var baking = new AssetBakeService(project, assets, CancellationToken.None);

        Assert.Equal(
            Path.Combine(_root, ".cache", "dreambit", "content.pak"),
            baking.OutputPakPath);
        Assert.Equal(
            Path.Combine(_root, ".cache", "dreambit", "bake"),
            baking.CacheDirectory);
    }

    [Fact]
    public void TextureBakeDefaultsToLinearPremultipliedSrgbPixels()
    {
        var assets = Path.Combine(_root, "TextureAssets");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "pixel.png");
        using (var image = new Image<Rgba32>(1, 1))
        {
            image[0, 0] = new Rgba32(200, 100, 50, 128);
            image.SaveAsPng(source);
        }

        var blob = new TextureBaker().BakeToBytes(new DreambitEngine.AssetBaker.Abstractions.BakeContext
        {
            InputPath = source,
            OutputPath = string.Empty,
            LogicalRoot = assets,
            PremultiplyAlpha = true
        });

        Assert.Equal("pixel.texb", blob.LogicalPath);
        Assert.Equal(3u, BitConverter.ToUInt32(blob.Data, 12));
        Assert.Equal(new byte[] { 147, 72, 34, 128 }, blob.Data[20..24]);
    }

    [Fact]
    public void NormalMapBakeIsLinearUnpremultipliedAndRenormalizesEveryMip()
    {
        var assets = Path.Combine(_root, "NormalMapAssets");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "surface.png");
        using (var image = new Image<Rgba32>(2, 1))
        {
            image[0, 0] = new Rgba32(255, 128, 128, 64);
            image[1, 0] = new Rgba32(128, 255, 128, 192);
            image.SaveAsPng(source);
        }

        var blob = new TextureBaker().BakeToBytes(new BakeContext
        {
            InputPath = source,
            OutputPath = string.Empty,
            LogicalRoot = assets,
            GenerateMips = true,
            PremultiplyAlpha = true,
            MarkSRgb = true,
            ImportSettings = NormalMapImportSettings()
        });

        Assert.Equal((uint)TexbFlags.NormalMap, BitConverter.ToUInt32(blob.Data, 12));
        Assert.Equal(2, BitConverter.ToUInt16(blob.Data, 10));
        Assert.Equal((byte)64, blob.Data[23]);
        Assert.Equal((byte)192, blob.Data[27]);

        var offset = 16;
        for (var mip = 0; mip < 2; mip++)
        {
            var byteCount = BitConverter.ToInt32(blob.Data, offset);
            offset += sizeof(uint);
            for (var pixel = 0; pixel < byteCount; pixel += 4)
            {
                var normalX = blob.Data[offset + pixel] / 255f * 2f - 1f;
                var normalY = blob.Data[offset + pixel + 1] / 255f * 2f - 1f;
                var normalZ = blob.Data[offset + pixel + 2] / 255f * 2f - 1f;
                var length = MathF.Sqrt(
                    normalX * normalX + normalY * normalY + normalZ * normalZ);
                Assert.InRange(length, 0.99f, 1.01f);
            }
            offset += byteCount;
        }
    }

    [Fact]
    public void ChangingOnlyOneTextureSemanticRebakesOnlyThatTexture()
    {
        var assets = Path.Combine(_root, "SemanticCacheAssets");
        var cache = Path.Combine(_root, "SemanticCache");
        var output = Path.Combine(_root, "SemanticCacheOutput", "content.pak");
        var registry = Path.Combine(_root, ".dreambit", "semantic-assets.json");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        WritePng(Path.Combine(assets, "first.png"), new Rgba32(255, 128, 128, 128));
        WritePng(Path.Combine(assets, "second.png"), new Rgba32(200, 100, 50, 128));
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        WriteTextureRegistry(registry, firstId, secondId, normalMapFirst: false);

        var pipeline = new AssetBakePipeline();
        var request = new AssetBakeRequest(assets, output, registry, cache);
        var first = pipeline.BakePak(request);
        var cached = pipeline.BakePak(request);
        WriteTextureRegistry(registry, firstId, secondId, normalMapFirst: true);
        var changed = pipeline.BakePak(request);

        Assert.Equal(2, first.BakedCount);
        Assert.Equal(2, cached.CacheHitCount);
        Assert.Equal(1, changed.BakedCount);
        Assert.Equal(1, changed.CacheHitCount);
        using var pak = new PakReader(output);
        using var firstTexture = pak.Open("first.texb");
        using var secondTexture = pak.Open("second.texb");
        Assert.Equal(TexbFlags.NormalMap, ReadTexbFlags(firstTexture));
        Assert.Equal(TexbFlags.Premultiplied | TexbFlags.Srgb, ReadTexbFlags(secondTexture));
    }

    private static AssetImportSettings NormalMapImportSettings() => new()
    {
        Texture = new TextureImportSettings { Semantic = TextureSemantic.NormalMap }
    };

    private static void WritePng(string path, Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = pixel;
        image.SaveAsPng(path);
    }

    private static void WriteTextureRegistry(
        string path,
        Guid firstId,
        Guid secondId,
        bool normalMapFirst)
    {
        var importSettings = normalMapFirst
            ? "\"importSettings\": { \"texture\": { \"semantic\": \"NormalMap\" } },"
            : string.Empty;
        File.WriteAllText(
            path,
            $$"""
              {
                "schemaVersion": 2,
                "assets": [
                  { "id": "{{firstId:D}}", "path": "first.png", "kind": "Texture", {{importSettings}} "length": 0 },
                  { "id": "{{secondId:D}}", "path": "second.png", "kind": "Texture", "length": 0 }
                ]
              }
              """);
    }

    private static TexbFlags ReadTexbFlags(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        return (TexbFlags)BitConverter.ToUInt32(header[12..16]);
    }

    [Fact]
    public void BuiltInEffectsAndFontAreCompiledIntoThePak()
    {
        var assets = Path.Combine(_root, "EmptyAssets");
        var output = Path.Combine(_root, "BuiltIns", "content.pak");
        var cache = Path.Combine(_root, "BuiltIns", "cache");
        Directory.CreateDirectory(assets);
        var legacyEffects = Path.Combine(assets, "Effects");
        Directory.CreateDirectory(legacyEffects);
        File.WriteAllText(
            Path.Combine(legacyEffects, "Tint.fx"),
            "This copied legacy engine effect must never be compiled.");

        new AssetBakePipeline().BakePak(new AssetBakeRequest(
            assets,
            output,
            CacheDirectory: cache,
            RebuildAll: true,
            TargetPlatform: "DesktopVK",
            IncludeBuiltInContent: true));

        using var pak = new PakReader(output);
        var builtInEffects = new[]
        {
            "effects/basiclighting2d.fxb",
            "effects/colorcorrection.fxb",
            "effects/depth2d.fxb",
            "effects/depthlighting2d.fxb",
            "effects/forwarddiffuse.fxb",
            "effects/present.fxb",
            "effects/tint.fxb"
        };

        foreach (var logicalPath in builtInEffects)
        {
            using var effect = pak.Open(logicalPath);
            Assert.NotEqual(-1, effect.ReadByte());
        }

        using var font = pak.Open("fonts/monogram.ttfb");
        Assert.NotEqual(-1, font.ReadByte());
        Assert.True(AssetBakePipeline.HasCurrentBuiltInContent(cache));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
