using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Scenes;
using DreambitEngine.AssetBaker.Pipeline;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class AssetRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dreambit.RegistryTests", Guid.NewGuid().ToString("N"));
    private readonly IAssetRegistry? _previousRegistry = Resources.AssetRegistry;
    private readonly AssetContentMode _previousMode = Resources.ContentMode;
    private readonly string? _previousContent = (string?)typeof(Resources)
        .GetField("_contentDirectoryOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null);
    private readonly string _previousPak = Resources.PakName;
    private readonly AssetDatabase _database;
    private readonly AssetCatalogEntry[] _entries;

    public AssetRegistryTests()
    {
        var source = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "Blobs");
        Directory.CreateDirectory(Path.Combine(source, "Items"));
        File.WriteAllText(Path.Combine(source, "Items", "Base.asset"),
            """{"$dreambitType":"test.registry.item","Value":1}""");
        File.WriteAllText(Path.Combine(source, "Items", "Food.asset"),
            """{"$dreambitType":"test.registry.food","Value":2}""");
        File.WriteAllText(Path.Combine(source, "Items", "Weapon.asset"),
            """{"$dreambitType":"test.registry.weapon.old","Value":3}""");
        DreambitAssetTypeRegistry.Refresh([typeof(RegistryTestItem), typeof(RegistryTestFood), typeof(RegistryTestWeapon)]);
        Resources.RefreshLoaders();
        _database = new AssetDatabase(_root, source, enableWatcher: false);
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(source, output, _database.RegistryPath));
        Resources.SetBlobContentSource(output);
        using var registryStream = new BlobContentReader(output).Open(RuntimeAssetRegistry.LogicalPath);
        Resources.AssetRegistry = RuntimeAssetRegistry.Load(registryStream);
        _entries = Resources.AssetRegistry.GetAssets().ToArray();
        RegistryTestItem.Constructions = 0;
    }

    [Fact]
    public void DiscoveryUsesMetadataIncludesInheritanceAndFormerIdsWithoutLoading()
    {
        Assert.Equal(3, Resources.FindAssets<RegistryTestItem>().Count);
        Assert.Equal(3, Resources.FindAssets<RegistryTestAbstractItem>().Count);
        var food = Assert.Single(Resources.FindAssets<RegistryTestFood>());
        Assert.Equal("Items/Food.asset", food.AssetName);
        var weapon = Assert.Single(Resources.FindAssets<RegistryTestWeapon>());
        Assert.Equal("test.registry.weapon.old", weapon.TypeId);
        Assert.Empty(Resources.FindAssets<SoundCue>());
        Assert.Equal(0, RegistryTestItem.Constructions);
        Assert.Equal(0, Resources.Instance.ContentCollection.Count);
    }

    [Fact]
    public void DiscoverySkipsUntypedEntriesButRejectsUnknownTypesAndAbsentCatalogs()
    {
        Resources.AssetRegistry = new RegistryTestCatalog(
            new AssetCatalogEntry(AssetId.New(), "legacy", null));
        Assert.Empty(Resources.FindAssets<RegistryTestItem>());
        Resources.AssetRegistry = new RegistryTestCatalog(
            new AssetCatalogEntry(AssetId.New(), "unknown", "missing.type"));
        var error = Assert.ThrowsAny<Exception>(() => Resources.FindAssets<RegistryTestItem>());
        Assert.Contains("missing.type", error.Message);
        Assert.Contains("unknown", error.Message);
        Resources.AssetRegistry = null!;
        Assert.Throws<InvalidOperationException>(() => Resources.FindAssets<RegistryTestItem>());
        Assert.Equal(0, RegistryTestItem.Constructions);
    }

    [Fact]
    public void PreloadCompletesBeforeDependentServiceOnBeginAndRunning()
    {
        using var scene = new RegistryLifecycleScene();
        // Deliberately register the consumer first to exercise dependency sorting.
        var consumer = scene.CreateEntity("consumer").AttachComponent<RegistryTestConsumer>();
        var registry = scene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        Assert.True(registry.CreatedBeforeLoad);
        Assert.False(registry.IsLoaded);
        scene.Tick();
        Assert.True(registry.IsLoaded);
        Assert.Equal(3, registry.Count);
        Assert.True(consumer.SawLoadedRegistry);
        Assert.True(scene.BeganWithLoadedRegistry);
        Assert.Equal(SceneState.Running, scene.State);
        Assert.IsType<RegistryTestWeapon>(registry.Get("Items/Weapon.asset"));
        Assert.IsType<RegistryTestFood>(registry.Get("Items/Food.asset"));
        Assert.Equal(3, RegistryTestItem.Constructions);
    }

    [Fact]
    public void ExplicitPreloadUsesStableIdsAndOnlyLoadsTheSelection()
    {
        using var scene = new RegistryLifecycleScene();
        var registry = scene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        registry.Source = AssetRegistrySource.Explicit;
        registry.ExplicitAssets = [new(_entries[2].Id, "old/moved-name.asset")];
        scene.Tick();
        Assert.True(registry.IsLoaded);
        Assert.Equal(1, registry.Count);
        Assert.IsType<RegistryTestWeapon>(registry.Get(_entries[2].Id));
        Assert.Equal(1, RegistryTestItem.Constructions);
        Assert.False(registry.TryGet(_entries[0].Id, out _));
    }

    [Fact]
    public void ManualLoadPopulatesAllIndexesAndLookupsNeverLoadOrAllocate()
    {
        using var scene = new RegistryLifecycleScene();
        var registry = scene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        registry.LoadMode = AssetRegistryLoadMode.Manual;
        scene.Tick();
        Assert.False(registry.IsLoaded);
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, RegistryTestItem.Constructions);
        Assert.False(registry.TryGet(_entries[0].Id, out _));
        registry.LoadAll();
        foreach (var entry in _entries)
        {
            var asset = registry.Get(entry.Id);
            Assert.Same(asset, registry.Get(entry.AssetName.ToUpperInvariant()));
            Assert.Same(asset, registry.Get("  " + entry.AssetName.Replace('/', '\\') + "  "));
            Assert.True(registry.TryGet(entry.Id, out var byId));
            Assert.Same(asset, byId);
            Assert.True(registry.TryGet(entry.AssetName, out var byName));
            Assert.Same(asset, byName);
        }
        registry.LoadAll();
        Assert.Equal(3, RegistryTestItem.Constructions);
        Assert.False(registry.TryGet((string)null!, out _));
        Assert.False(registry.TryGet("absent", out _));
        Assert.False(registry.TryGet(AssetId.Empty, out _));
        Assert.Contains(nameof(RegistryTestService), Assert.Throws<KeyNotFoundException>(() => registry.Get("absent")).Message);
        Assert.Contains("absent", Assert.Throws<KeyNotFoundException>(() => registry.Get("absent")).Message);
        var missingId = AssetId.New();
        Assert.Contains(missingId.ToString(), Assert.Throws<KeyNotFoundException>(() => registry.Get(missingId)).Message);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            registry.Get(_entries[0].Id);
            registry.Get("ITEMS/BASE.ASSET");
            registry.TryGet(_entries[0].Id, out _);
            registry.TryGet("items/base.asset", out _);
            registry.Get("  ITEMS\\BASE.ASSET  ");
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void BaseAndConcreteRegistriesShareResourcesCacheAndDisposalDoesNotUnload()
    {
        var scene = new RegistryLifecycleScene();
        var registry = scene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        var weapons = scene.CreateEntity("weapons").AttachComponent<RegistryTestWeaponService>();
        scene.Tick();
        var weapon = registry.Get(_entries[2].Id);
        Assert.Same(weapon, weapons.Get(_entries[2].Id));
        Assert.Same(weapon, Resources.LoadAsset<RegistryTestWeapon>("items/weapon.asset"));
        Assert.Same(weapon, Resources.LoadDreambitAsset(_entries[2].Id, "", typeof(RegistryTestWeapon)));
        Assert.Equal(3, RegistryTestItem.Constructions);
        var exposed = registry.Assets;
        Assert.Throws<NotSupportedException>(() => ((IList<RegistryTestItem>)exposed).Clear());
        scene.Dispose();
        Assert.Empty(exposed);
        Assert.Equal(0, registry.Count);
        Assert.False(registry.IsLoaded);
        Assert.False(weapon.Disposed);
        Assert.Same(weapon, Resources.LoadAsset<RegistryTestWeapon>("Items/Weapon.asset"));
        Assert.Throws<ObjectDisposedException>(registry.LoadAll);
        Resources.UnloadAsset("Items/Weapon.asset");
        Assert.True(weapon.Disposed);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-normalized-name")]
    [InlineData("unknown-type")]
    [InlineData("load-failure")]
    public void FailedPreloadLeavesEmptyIndexesAndStopsSceneStartup(string failure)
    {
        var bad = failure switch
        {
            "duplicate-id" => _entries[0] with { AssetName = "duplicate.asset" },
            "duplicate-name" => _entries[0] with { Id = AssetId.New(), AssetName = _entries[0].AssetName.ToUpperInvariant() },
            "duplicate-normalized-name" => _entries[0] with { Id = AssetId.New(), AssetName = " items\\base.asset " },
            "unknown-type" => new AssetCatalogEntry(AssetId.New(), "unknown.asset", "unresolved.type"),
            _ => new AssetCatalogEntry(AssetId.New(), "missing.asset", _entries[0].TypeId)
        };
        Resources.AssetRegistry = new RegistryTestCatalog(_entries[0], bad);
        using var scene = new RegistryLifecycleScene();
        var consumer = scene.CreateEntity("consumer").AttachComponent<RegistryTestConsumer>();
        var registry = scene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        var error = Assert.Throws<InvalidOperationException>(scene.Tick);
        Assert.Contains(nameof(RegistryTestService), error.Message);
        Assert.False(consumer.SawLoadedRegistry);
        Assert.False(scene.BeganWithLoadedRegistry);
        Assert.NotEqual(SceneState.Running, scene.State);
        Assert.False(registry.IsLoaded);
        Assert.Empty(registry.Assets);
        Assert.False(registry.TryGet(_entries[0].Id, out _));
        Assert.False(registry.TryGet(_entries[0].AssetName, out _));
        if (failure == "load-failure")
        {
            Assert.Contains("missing.asset", error.Message);
            Assert.NotNull(error.InnerException?.InnerException);
            Resources.AssetRegistry = new RegistryTestCatalog(_entries[0]);
            registry.LoadAll();
            Assert.Single(registry.Assets);
            Assert.Equal(1, RegistryTestItem.Constructions);
        }
    }

    [Theory]
    [InlineData("incompatible")]
    [InlineData("untyped")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void InvalidExplicitSelectionsFailClearly(string failure)
    {
        using var registry = new RegistryTestWeaponService { Source = AssetRegistrySource.Explicit };
        var selected = _entries[0];
        if (failure is "untyped" or "unknown")
        {
            selected = selected with { TypeId = failure == "untyped" ? null : "unknown.type" };
            Resources.AssetRegistry = new RegistryTestCatalog(selected);
        }
        registry.ExplicitAssets = failure switch
        {
            "missing" => [new(AssetId.New(), "missing.asset")],
            "duplicate" => [new(_entries[2].Id), new(_entries[2].Id)],
            "empty" => [null!],
            _ => [new(selected.Id)]
        };
        var error = Assert.Throws<InvalidOperationException>(registry.LoadAll);
        Assert.Contains(nameof(RegistryTestWeaponService), error.Message);
        Assert.False(registry.IsLoaded);
        Assert.Empty(registry.Assets);
    }

    [Fact]
    public void DeferredReferencesRoundTripThroughSceneAndBlueprintWithoutLoading()
    {
        using var sourceScene = new RegistryLifecycleScene();
        var registry = sourceScene.CreateEntity("registry").AttachComponent<RegistryTestService>();
        registry.Source = AssetRegistrySource.Explicit;
        registry.ExplicitAssets = [new(_entries[2].Id, _entries[2].AssetName)];
        var captured = SceneDocumentSerializer.Capture(sourceScene, new SceneBlueprint(), "Registry scene");
        var component = Assert.Single(Assert.Single(captured.Entities).Components);
        Assert.Equal(new[] { "ExplicitAssets", "LoadMode", "Source" }, component.Properties.Keys.Order().ToArray());
        var token = Assert.Single((JArray)component.Properties["ExplicitAssets"]);
        Assert.True(DreambitAssetReferenceToken.TryRead(token, out var id, out _));
        Assert.Equal(_entries[2].Id, id);
        Assert.Empty(BlueprintValidator.Validate(captured.Entities[0]));
        var json = DreambitJson.Serialize(captured);
        Assert.DoesNotContain("IsLoaded", json);
        Assert.DoesNotContain("AssetType", json);
        Assert.Equal(0, RegistryTestItem.Constructions);

        using var restored = new RegistryLifecycleScene();
        restored.LoadIntoSelf(DreambitJson.Deserialize<SceneBlueprint>(json));
        var restoredRegistry = restored.Services.Get<RegistryTestService>();
        Assert.Single(restoredRegistry.ExplicitAssets);
        Assert.True(restoredRegistry.CreatedBeforeLoad);
        Assert.False(restoredRegistry.IsLoaded);
        Assert.Equal(0, RegistryTestItem.Constructions);
        restored.Tick();
        Assert.Single(restoredRegistry.Assets);

        var loadedJson = JObject.Parse(DreambitJson.Serialize(restoredRegistry));
        Assert.Equal(new[] { "ExplicitAssets", "LoadMode", "Source" }, loadedJson.Properties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void EditorMaterializationAndActivationDoNotPreload()
    {
        using var scene = new RegistryLifecycleScene(SceneExecutionMode.Editor);
        scene.LoadIntoSelf(new SceneBlueprint
        {
            Entities = [new EntityBlueprint
            {
                Name = "registry",
                Components = [new ComponentBlueprint
                {
                    Type = typeof(RegistryTestService).FullName!,
                    Properties = new()
                    {
                        ["Source"] = JToken.FromObject(AssetRegistrySource.Explicit),
                        ["LoadMode"] = JToken.FromObject(AssetRegistryLoadMode.Preload),
                        ["ExplicitAssets"] = new JArray(DreambitAssetReferenceToken.Create(_entries[2].Id))
                    }
                }]
            }]
        }, SceneBlueprintLoadOptions.Editor);
        var registry = scene.Services.Get<RegistryTestService>();
        Assert.Single(registry.ExplicitAssets);
        Assert.Empty(registry.EditorSerializationFailures);
        scene.Services.ActivateAll();
        registry.OnServicesReady();
        Assert.False(registry.IsLoaded);
        Assert.Equal(0, RegistryTestItem.Constructions);
    }

    [Fact]
    public void EditorPickerUsesGenericTypeAndDeferredSelectionAndConditionalMetadata()
    {
        var type = typeof(AssetReference<RegistryTestItem>);
        Assert.Equal(typeof(RegistryTestItem), InspectorValueDrawerRegistry.GetReferencedAssetType(type));
        var weapon = _database.GetSnapshot().Assets.Single(asset => asset.Id == _entries[2].Id);
        var reference = Assert.IsType<AssetReference<RegistryTestItem>>(
            InspectorValueDrawerRegistry.CreateAssetSelection(type, weapon));
        Assert.Equal(weapon.Id, reference.Id);
        Assert.Null(InspectorValueDrawerRegistry.CreateAssetSelection(typeof(AssetReference<SoundCue>), weapon));
        Assert.Equal(0, RegistryTestItem.Constructions);
        var metadata = new InspectorMetadataCache().Get(typeof(RegistryTestService), InspectorTargetKind.Component);
        Assert.Equal(new[] { "ExplicitAssets", "LoadMode", "Source" }, metadata.Select(m => m.SerializedName).Order().ToArray());
        var explicitMember = metadata.Single(m => m.SerializedName == "ExplicitAssets");
        Assert.False(explicitMember.IsVisible(_ => AssetRegistrySource.AllOfType));
        Assert.True(explicitMember.IsVisible(_ => AssetRegistrySource.Explicit));
    }

    [Fact]
    public void SoundCuePreloadUsesSemanticLoaderAndWarmsItsTakeReferences()
    {
        var source = Path.Combine(_root, "Assets");
        var output = Path.Combine(_root, "AudioBlobs");
        File.WriteAllText(Path.Combine(source, "gameplay.soundcue"),
            """{"takes":["Audio/First","Audio/Second"]}""");
        _database.RefreshNow();
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(source, output, _database.RegistryPath));
        Resources.SetBlobContentSource(output);
        Resources.AssetRegistry = _database;

        // Supply cached takes without creating an audio device in a headless test. The real
        // SoundCueLoader must still call LoadInternal to populate both dependency references.
        var first = (Microsoft.Xna.Framework.Audio.SoundEffect)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Microsoft.Xna.Framework.Audio.SoundEffect));
        var second = (Microsoft.Xna.Framework.Audio.SoundEffect)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Microsoft.Xna.Framework.Audio.SoundEffect));
        GC.SuppressFinalize(first);
        GC.SuppressFinalize(second);
        Resources.Instance.ContentCollection.TryAdd("Audio/First", first.GetType(), first, false);
        Resources.Instance.ContentCollection.TryAdd("Audio/Second", second.GetType(), second, false);
        using var scene = new ServiceTestScene();
        var registry = scene.CreateEntity("audio").AttachComponent<RegistryTestSoundService>();
        registry.Source = AssetRegistrySource.Explicit;
        var cueEntry = Resources.FindAssets<SoundCue>().Single();
        registry.ExplicitAssets = [new(cueEntry.Id)];
        scene.Services.ActivateAll();
        var cue = Assert.Single(registry.Assets);
        Assert.Equal(2, cue.SfxTakes.Length);
        Assert.Same(first, cue.SfxTakes[0]);
        Assert.Same(second, cue.SfxTakes[1]);
        Assert.Same(cue, Resources.LoadAsset<SoundCue>("gameplay.soundcue"));
    }

    [Fact]
    public void DiscoveryResolvesAgainstTheCurrentAssemblyGeneration()
    {
        Assert.Equal(3, Resources.FindAssets<RegistryTestItem>().Count);
        DreambitAssetTypeRegistry.Refresh([]);
        Assert.ThrowsAny<Exception>(() => Resources.FindAssets<RegistryTestItem>());
        DreambitAssetTypeRegistry.Refresh([typeof(RegistryTestItem), typeof(RegistryTestFood), typeof(RegistryTestWeapon)]);
        Assert.Equal(3, Resources.FindAssets<RegistryTestItem>().Count);
        Assert.Equal(0, RegistryTestItem.Constructions);
    }

    [Fact]
    public void SerializingDeferredReferencesDoesNotRetainCollectibleGameTypes()
    {
        var weakContext = ExerciseCollectibleReference();
        for (var i = 0; i < 10 && weakContext.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weakContext.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseCollectibleReference()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("registry-reference-test", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(AssetRegistryTests).Assembly.Location);
        var assetType = assembly.GetType(typeof(RegistryTestItem).FullName!)!;
        var referenceType = typeof(AssetReference<>).MakeGenericType(assetType);
        var id = AssetId.New();
        var reference = DreambitJson.FromToken(DreambitAssetReferenceToken.Create(id), referenceType);
        Assert.Equal(id, ((IAssetReference)reference!).Id);
        var token = DreambitJson.ToToken(reference);
        Assert.True(DreambitAssetReferenceToken.TryRead(token, out var roundTripId, out _));
        Assert.Equal(id, roundTripId);
        foreach (var collectionType in new[] { typeof(List<>).MakeGenericType(referenceType), referenceType.MakeArrayType() })
        {
            var collection = DreambitJson.FromToken(new JArray(token), collectionType);
            var serialized = DreambitJson.ToToken(collection);
            Assert.Single((JArray)serialized);
        }
        DreambitAssemblyCaches.Release(assembly);
        context.Unload();
        return new WeakReference(context);
    }

    public void Dispose()
    {
        Resources.ResetContentSource();
        if (_previousContent is not null)
            Resources.SetContentSource(_previousContent, _previousPak);
        Resources.PakName = _previousPak;
        Resources.ContentMode = _previousMode;
        Resources.AssetRegistry = _previousRegistry!;
        DreambitAssetTypeRegistry.Refresh([]);
        _database.Dispose();
        Directory.Delete(_root, true);
    }
}

public abstract class RegistryTestAbstractItem : DreambitAsset;

[DreambitAssetType("test.registry.item")]
public class RegistryTestItem : RegistryTestAbstractItem
{
    public RegistryTestItem() => Constructions++;
    public static int Constructions { get; set; }
    [DreambitSerialize] public int Value { get; set; }
    public bool Disposed { get; private set; }
    protected override void CleanUp() => Disposed = true;
}

[DreambitAssetType("test.registry.food")]
public sealed class RegistryTestFood : RegistryTestItem;

[DreambitAssetType("test.registry.weapon", "test.registry.weapon.old")]
public sealed class RegistryTestWeapon : RegistryTestItem;

public sealed class RegistryTestService : AssetRegistry<RegistryTestItem>
{
    public bool CreatedBeforeLoad { get; private set; }
    public override void OnCreated() => CreatedBeforeLoad = !IsLoaded;
}

public sealed class RegistryTestWeaponService : AssetRegistry<RegistryTestWeapon>;

public sealed class RegistryTestSoundService : AssetRegistry<SoundCue>;

[RequiresSceneService(typeof(RegistryTestService))]
public sealed class RegistryTestConsumer : SceneServiceComponent
{
    public bool SawLoadedRegistry { get; private set; }
    public override void OnServicesReady() => SawLoadedRegistry = Scene.Services.Get<RegistryTestService>().IsLoaded;
}

public sealed class RegistryLifecycleScene(SceneExecutionMode mode = SceneExecutionMode.Runtime) : Scene(mode)
{
    public bool BeganWithLoadedRegistry { get; private set; }
    internal override void InitializeInternals() { }
    protected override void OnBegin() => BeganWithLoadedRegistry = Services.Get<RegistryTestService>().IsLoaded;
}

internal sealed class RegistryTestCatalog(params AssetCatalogEntry[] entries) : IAssetRegistry
{
    private readonly IReadOnlyList<AssetCatalogEntry> _entries = Array.AsReadOnly(entries);
    public IReadOnlyList<AssetCatalogEntry> GetAssets() => _entries;
    public bool TryResolveAssetName(AssetId assetId, out string assetName)
    {
        foreach (var entry in _entries)
            if (entry.Id == assetId)
            {
                assetName = entry.AssetName;
                return true;
            }
        assetName = null!;
        return false;
    }
    public bool TryGetAssetId(string assetName, out AssetId assetId)
    {
        foreach (var entry in _entries)
            if (StringComparer.OrdinalIgnoreCase.Equals(entry.AssetName, assetName))
            {
                assetId = entry.Id;
                return true;
            }
        assetId = default;
        return false;
    }
}
