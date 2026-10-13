using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Projects;
using DreambitEngine.AssetBaker.Abstractions;
using DreambitEngine.AssetBaker.Pipeline;
using DreambitEngine.AssetBaker.Pipeline.Docs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;

namespace Dreambit.Editor.Tests;

public sealed class CustomDreambitAssetTests
{
    public CustomDreambitAssetTests()
    {
        RefreshTestRegistry();
    }

    [Fact]
    public void AttributeValidatesIdsAndExposesFormerIds()
    {
        Assert.Throws<ArgumentException>(() => new DreambitAssetTypeAttribute(" "));
        Assert.Throws<ArgumentException>(() =>
            new DreambitAssetTypeAttribute("test.asset", ""));
        Assert.Throws<ArgumentException>(() =>
            new DreambitAssetTypeAttribute("test.asset", "TEST.ASSET"));

        var attribute = new DreambitAssetTypeAttribute(
            "test.asset.v2",
            "test.asset",
            "test.asset-definition");
        Assert.Equal("test.asset.v2", attribute.Id);
        Assert.Equal(["test.asset", "test.asset-definition"], attribute.FormerIds);
    }

    [Fact]
    public void RegistryResolvesCurrentFormerAndLegacyClrIdentities()
    {
        Assert.True(DreambitAssetTypeRegistry.TryResolve(
            "test.migrated-weapon.v2",
            out var current));
        Assert.Equal(typeof(MigratedWeaponConfig), current);

        Assert.True(DreambitAssetTypeRegistry.TryResolve(
            "test.migrated-weapon",
            out var former));
        Assert.Equal(typeof(MigratedWeaponConfig), former);

        Assert.True(DreambitAssetTypeRegistry.TryResolve(
            "ExampleGame.Items.WeaponDefinition",
            out var legacy));
        Assert.Equal(typeof(MigratedWeaponConfig), legacy);
        Assert.True(DreambitAssetTypeRegistry.TryResolve(
            typeof(MigratedWeaponConfig).FullName!,
            out var currentClrName));
        Assert.Equal(typeof(MigratedWeaponConfig), currentClrName);
    }

    [Fact]
    public void UnannotatedAssetUsesRenameUnsafeFullNameFallback()
    {
        Assert.False(DreambitAssetTypeRegistry.HasStableTypeId(typeof(UnannotatedCustomAsset)));
        Assert.Equal(
            typeof(UnannotatedCustomAsset).FullName,
            DreambitAssetTypeRegistry.GetTypeId(typeof(UnannotatedCustomAsset)));

        var json = JObject.Parse(DreambitJson.Serialize(new UnannotatedCustomAsset { Value = 12 }));
        Assert.Equal(
            typeof(UnannotatedCustomAsset).FullName,
            json.Value<string>(DreambitAssetTypeRegistry.MetadataPropertyName));
        Assert.Equal(12, json.Value<int>(nameof(UnannotatedCustomAsset.Value)));
    }

    [Fact]
    public void StableIdSurvivesClrClassAndNamespaceRenameWithoutContentMigration()
    {
        const string unchangedJson =
            "{\"$dreambitType\":\"test.renamed-stable\",\"Damage\":25}";

        var asset = Assert.IsType<RenamedStableWeaponConfig>(
            DreambitJson.DeserializeAsset(unchangedJson));
        Assert.Equal(25, asset.Damage);
    }

    [Fact]
    public void DuplicateCurrentAndFormerIdsFailDeterministically()
    {
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            DreambitAssetTypeRegistry.Validate(
                [typeof(DuplicateAssetA<int>), typeof(DuplicateAssetB<int>)]));
        Assert.Contains("test.duplicate", duplicate.Message);
        Assert.Contains(typeof(DuplicateAssetA<int>).FullName!, duplicate.Message);
        Assert.Contains(typeof(DuplicateAssetB<int>).FullName!, duplicate.Message);

        var currentFormer = Assert.Throws<InvalidOperationException>(() =>
            DreambitAssetTypeRegistry.Validate(
                [typeof(CurrentIdAsset<int>), typeof(FormerIdAsset<int>)]));
        Assert.Contains("test.current-former", currentFormer.Message);
        Assert.Contains(typeof(CurrentIdAsset<int>).FullName!, currentFormer.Message);
        Assert.Contains(typeof(FormerIdAsset<int>).FullName!, currentFormer.Message);
    }

    [Fact]
    public void CustomAssetJsonUsesOptInMembersMetadataAndReferenceTokens()
    {
        var spriteId = AssetId.New();
        var asset = new TestCustomAsset
        {
            Health = 125,
            Sprite = new Sprite
            {
                AssetId = spriteId,
                AssetName = "sprites/hero.sprite"
            },
            SpriteVariants =
            [
                new Sprite
                {
                    AssetId = AssetId.New(),
                    AssetName = "sprites/variant.sprite"
                }
            ],
            SpriteLookup = new Dictionary<string, Sprite?>
            {
                ["primary"] = new Sprite
                {
                    AssetId = AssetId.New(),
                    AssetName = "sprites/primary.sprite"
                }
            },
            ShouldNeverAppear = "NOPE"
        };

        var json = JObject.Parse(DreambitJson.Serialize(asset));

        Assert.Equal(
            DreambitAssetTypeRegistry.MetadataPropertyName,
            json.Properties().First().Name);
        Assert.Equal("test.custom-asset", json.Value<string>("$dreambitType"));
        Assert.Equal(125, json.Value<int>(nameof(TestCustomAsset.Health)));
        Assert.Null(json[nameof(TestCustomAsset.ShouldNeverAppear)]);
        Assert.True(DreambitAssetReferenceToken.TryRead(
            json[nameof(TestCustomAsset.Sprite)]!,
            out var parsedId,
            out var fallback));
        Assert.Equal(spriteId, parsedId);
        Assert.Equal("sprites/hero.sprite", fallback);
        Assert.True(DreambitAssetReferenceToken.TryRead(
            json[nameof(TestCustomAsset.SpriteVariants)]![0]!,
            out _,
            out _));
        Assert.True(DreambitAssetReferenceToken.TryRead(
            json[nameof(TestCustomAsset.SpriteLookup)]!["primary"]!,
            out _,
            out _));
    }

    [Fact]
    public void NestedObjectsBecomeOptInWhenTheyDeclareDreambitMembers()
    {
        var json = JObject.Parse(DreambitJson.Serialize(new NestedCustomAsset
        {
            Stats = new WeaponStats { Damage = 25, Knockback = 1.5f, RuntimeCounter = 99 },
            Legacy = new LegacyNestedObject { Editable = 3, ExistingLegacyValue = 4 }
        }));

        var stats = Assert.IsType<JObject>(json[nameof(NestedCustomAsset.Stats)]);
        Assert.Equal(25, stats.Value<int>(nameof(WeaponStats.Damage)));
        Assert.Equal(1.5f, stats.Value<float>(nameof(WeaponStats.Knockback)));
        Assert.Null(stats[nameof(WeaponStats.RuntimeCounter)]);

        var legacy = Assert.IsType<JObject>(json[nameof(NestedCustomAsset.Legacy)]);
        Assert.Equal(3, legacy.Value<int>(nameof(LegacyNestedObject.Editable)));
        Assert.Equal(4, legacy.Value<int>(nameof(LegacyNestedObject.ExistingLegacyValue)));
    }

    [Fact]
    public void LegacyJsonPropertyMembersAndEngineOwnedAssetsRemainCompatible()
    {
        var customJson = JObject.Parse(DreambitJson.Serialize(new LegacyMarkedCustomAsset
        {
            LegacyValue = 7,
            RuntimeValue = 8
        }));
        Assert.Equal(7, customJson.Value<int>("legacy_value"));
        Assert.Null(customJson[nameof(LegacyMarkedCustomAsset.RuntimeValue)]);

        var builtInJson = JObject.Parse(DreambitJson.Serialize(new ParticleFxConfig()));
        Assert.NotNull(builtInJson[nameof(ParticleFxConfig.EmissionMode)]);
        Assert.NotNull(builtInJson[nameof(ParticleFxConfig.EmissionRate)]);
        Assert.Null(builtInJson[DreambitAssetTypeRegistry.MetadataPropertyName]);
    }

    [Fact]
    public void EngineAssetContractsUseExplicitMembersStableIdsAndExistingJsonNames()
    {
        var engineAssetTypes = new[]
        {
            typeof(Sprite),
            typeof(SpriteSheet),
            typeof(SpriteAnimation),
            typeof(SoundCue),
            typeof(ParticleFxConfig),
            typeof(EntityBlueprint),
            typeof(SceneBlueprint),
            typeof(Tileset),
            typeof(TextureAsset),
            typeof(DreambitEffect),
            typeof(FontAsset),
            typeof(Dreambit.Scripting.Cutscene),
            typeof(Dreambit.Tiled.TmxMap),
            typeof(Dreambit.Tiled.TmxTileset)
        };

        foreach (var assetType in engineAssetTypes)
        {
            Assert.True(DreambitAssetTypeRegistry.HasStableTypeId(assetType));
            Assert.NotEqual(assetType.FullName, DreambitAssetTypeRegistry.GetTypeId(assetType));
        }

        Assert.True(DreambitSerializationRules.UsesOptInSerialization(typeof(Sprite)));
        Assert.True(DreambitSerializationRules.UsesOptInSerialization(typeof(ParticleFxConfig)));
        Assert.Equal(PivotType.Center, new Sprite().PivotType);

        var spriteAsset = new Sprite
        {
            AssetId = AssetId.New(),
            AssetName = "sprites/hero.sprite",
            SourceRect = new Microsoft.Xna.Framework.Rectangle(1, 2, 3, 4),
            PivotType = PivotType.Custom,
            Pivot = new Microsoft.Xna.Framework.Vector2(1.5f, 3f),
            PixelsPerUnit = 16f
        };

        var sprite = JObject.Parse(DreambitJson.Serialize(spriteAsset));
        Assert.NotNull(sprite["texture"]);
        Assert.Equal(new JArray(1, 2, 3, 4), sprite["source"]);
        Assert.Equal((int)PivotType.Custom, sprite.Value<int>("pivot_type"));
        Assert.Equal(new JArray(1.5f, 3f), sprite["pivot"]);
        Assert.Equal(16f, sprite.Value<float>("pixels_per_unit"));

        var animation = JObject.Parse(DreambitJson.Serialize(new SpriteAnimation
        {
            Frames = [new SpriteAnimationFrame
            {
                Sprite = spriteAsset,
                Event = new SpriteAnimationEvent
                {
                    Name = "step",
                    Args = new Dictionary<string, string> { ["surface"] = "stone" }
                }
            }]
        }));
        Assert.True(DreambitAssetReferenceToken.TryRead(
            animation["frames"]![0]!["sprite"]!,
            out var animationSpriteId,
            out var animationSpritePath));
        Assert.Equal(spriteAsset.AssetId, animationSpriteId);
        Assert.Equal(spriteAsset.AssetName, animationSpritePath);
        Assert.Equal("step", animation["frames"]![0]!["event"]!["name"]!.Value<string>());
        Assert.Equal("stone", animation["frames"]![0]!["event"]!["args"]!["surface"]!.Value<string>());
    }

    [Fact]
    public void FormerMemberNameDeserializesAndReserializesCanonically()
    {
        const string oldJson =
            "{\"$dreambitType\":\"test.migrated-weapon\",\"Damage\":25}";

        var asset = Assert.IsType<MigratedWeaponConfig>(DreambitJson.DeserializeAsset(oldJson));
        Assert.Equal(25, asset.BaseDamage);

        var saved = JObject.Parse(DreambitJson.Serialize(asset));
        Assert.Equal("test.migrated-weapon.v2", saved.Value<string>("$dreambitType"));
        Assert.Equal(25, saved.Value<int>(nameof(MigratedWeaponConfig.BaseDamage)));
        Assert.Null(saved["Damage"]);
    }

    [Fact]
    public void AssetDocumentCanonicalizesTypeAndMemberIdsWhilePreservingUnknownData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"weapon-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                "{\"$dreambitType\":\"test.migrated-weapon\",\"Damage\":25,\"unknown\":true}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.DreambitAsset,
                "test.migrated-weapon",
                file.Length,
                file.LastWriteTimeUtc);

            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(MigratedWeaponConfig),
                new InspectorMetadataCache());
            Assert.Equal(25, ((MigratedWeaponConfig)document.Instance).BaseDamage);

            var saved = JObject.Parse(document.CaptureJson());
            Assert.Equal("test.migrated-weapon.v2", saved.Value<string>("$dreambitType"));
            Assert.Equal(25, saved.Value<int>(nameof(MigratedWeaponConfig.BaseDamage)));
            Assert.Null(saved["Damage"]);
            Assert.True(saved.Value<bool>("unknown"));
            Assert.Equal("$dreambitType", saved.Properties().First().Name);

            document.Save(path);
            var persisted = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("test.migrated-weapon.v2", persisted.Value<string>("$dreambitType"));
            Assert.Null(persisted["Damage"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InspectorMetadataUsesTheSameOptInContractForAssetsAndComponents()
    {
        var cache = new InspectorMetadataCache();
        var assetMembers = cache.Get(typeof(TestCustomAsset), InspectorTargetKind.Asset);
        var health = Assert.Single(assetMembers, member =>
            member.SerializedName == nameof(TestCustomAsset.Health));
        Assert.Equal("Combat", health.Header);
        Assert.Equal(0, health.Range!.Minimum);
        Assert.Equal(1000, health.Range.Maximum);
        Assert.Contains(assetMembers, member =>
            member.SerializedName == nameof(TestCustomAsset.Sprite));
        Assert.DoesNotContain(assetMembers, member =>
            member.SerializedName == nameof(TestCustomAsset.ShouldNeverAppear));

        var componentMembers = cache.Get(
            typeof(TestCustomAssetReferenceComponent),
            InspectorTargetKind.Component);
        var config = Assert.Single(componentMembers);
        Assert.Equal(nameof(TestCustomAssetReferenceComponent.Config), config.SerializedName);
        Assert.Equal(typeof(TestCustomAsset), config.ValueType);
    }

    [Fact]
    public void PickerCompatibilityResolvesStableIdsAndUsesAssignability()
    {
        var sword = new AssetRecord(
            AssetId.New(),
            "weapons/sword.json",
            "sword.json",
            "weapons",
            "weapons/sword",
            AssetKind.DreambitAsset,
            "test.sword",
            0,
            DateTimeOffset.UtcNow);

        Assert.True(AssetTypeClassifier.IsCompatibleWith(sword, typeof(TestWeaponDefinition)));
        Assert.True(AssetTypeClassifier.IsCompatibleWith(sword, typeof(TestSwordDefinition)));
        Assert.False(AssetTypeClassifier.IsCompatibleWith(sword, typeof(TestCustomAsset)));
    }

    [Fact]
    public void BlueprintResolverRestoresCustomAssetReferencesThroughTheExistingAssetFlow()
    {
        var asset = new TestCustomAsset
        {
            AssetId = AssetId.New(),
            AssetName = $"tests/custom-{Guid.NewGuid():N}"
        };
        Assert.True(Resources.TryRegisterAsset(asset));
        try
        {
            var componentBlueprint = new ComponentBlueprint
            {
                Type = typeof(TestCustomAssetReferenceComponent).FullName!,
                Properties = new Dictionary<string, JToken>
                {
                    ["OldConfig"] = DreambitAssetReferenceToken.Create(
                        asset.AssetId,
                        asset.AssetName)
                }
            };
            var root = new EntityBlueprint
            {
                Name = "Test",
                Guid = Guid.NewGuid(),
                Components = [componentBlueprint]
            };
            var component = new TestCustomAssetReferenceComponent();

            BlueprintResolver.ResolveComponent(
                componentBlueprint,
                new BlueprintSpawnContext(root),
                component);

            Assert.Same(asset, component.Config);
        }
        finally
        {
            Resources.UnloadAsset(asset.AssetName);
            asset.Dispose();
        }
    }

    [Fact]
    public void CatalogDiscoversCustomAssetAndItsNormalAssetLoader()
    {
        var catalog = GameTypeCatalog.Discover(typeof(TestCustomAsset).Assembly);
        Assert.Contains(typeof(TestCustomAsset), catalog.AssetTypes);
        Assert.Contains(typeof(TestCustomAssetLoader), catalog.AssetLoaderTypes);
    }

    [Fact]
    public void GameDefinedAssetsLoadAutomaticallyFromPakAndLooseJsonbWhileExplicitLoadersWin()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dreambit.AutomaticAssetLoaderTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Assets");
        var content = Path.Combine(root, "Content");
        var assetId = AssetId.New();
        var originalUsePak = Resources.UsePak;
        var originalPakName = Resources.PakName;
        var originalRegistry = Resources.AssetRegistry;
        Directory.CreateDirectory(Path.Combine(source, "Config"));
        Directory.CreateDirectory(content);
        try
        {
            File.WriteAllText(
                Path.Combine(source, "Config", "BasicEnemy.json"),
                DreambitJson.Serialize(new AutomaticEnemyConfig { MaxHealth = 150, BaseDamage = 25 }));
            File.WriteAllText(
                Path.Combine(source, "Config", "SpecialEnemy.json"),
                DreambitJson.Serialize(new ExplicitOverrideEnemyConfig { MaxHealth = 10 }));

            new AssetBakePipeline().BakePak(new AssetBakeRequest(
                source,
                Path.Combine(content, "content.pak"),
                RebuildAll: true));
            new JsonbBaker().Bake(new BakeContext
            {
                InputPath = Path.Combine(source, "Config", "BasicEnemy.json"),
                OutputPath = Path.Combine(content, "Config", "BasicEnemy.jsonb"),
                LogicalRoot = source
            });

            DreambitAssemblyCaches.Refresh(
                [typeof(AutomaticEnemyConfig), typeof(ExplicitOverrideEnemyConfig)]);
            Resources.SetContentSource(content);
            Resources.PakName = "content.pak";
            Resources.AssetRegistry = new TestAssetRegistry(assetId, "Config/BasicEnemy");
            Resources.UsePak = true;

            var fromPak = Resources.LoadAsset<AutomaticEnemyConfig>("Config/BasicEnemy");
            Assert.NotNull(fromPak);
            Assert.Equal(150, fromPak.MaxHealth);
            Assert.Equal(25, fromPak.BaseDamage);
            Assert.Equal("Config/BasicEnemy", fromPak.AssetName);
            Assert.Equal(assetId, fromPak.AssetId);
            Resources.UnloadAsset("Config/BasicEnemy");

            var fromId = Assert.IsType<AutomaticEnemyConfig>(
                Resources.LoadDreambitAsset(assetId, string.Empty, typeof(AutomaticEnemyConfig)));
            Assert.Equal(150, fromId.MaxHealth);
            Assert.Equal(assetId, fromId.AssetId);
            Resources.UnloadAsset("Config/BasicEnemy");

            ExplicitOverrideEnemyConfigLoader.LoadCount = 0;
            var custom = Resources.LoadAsset<ExplicitOverrideEnemyConfig>("Config/SpecialEnemy");
            Assert.NotNull(custom);
            Assert.Equal(1, ExplicitOverrideEnemyConfigLoader.LoadCount);
            Assert.Equal(999, custom.MaxHealth);
            Resources.UnloadAsset("Config/SpecialEnemy");

            Resources.RefreshContent();
            Resources.UsePak = false;
            var fromLooseFile = Resources.LoadAsset<AutomaticEnemyConfig>("Config/BasicEnemy");
            Assert.NotNull(fromLooseFile);
            Assert.Equal(150, fromLooseFile.MaxHealth);
            Assert.Equal(25, fromLooseFile.BaseDamage);
            Assert.Equal("Config/BasicEnemy", fromLooseFile.AssetName);
        }
        finally
        {
            Resources.RefreshContent();
            Resources.UsePak = originalUsePak;
            Resources.PakName = originalPakName;
            Resources.AssetRegistry = originalRegistry;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void LoadedGameAssetAppearsInEditorTypesCreatesJsonAndOpensInDefaultInspector()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Dreambit.Editor.CustomAssetWorkflowTests",
            Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "Content", "Assets");
        Directory.CreateDirectory(contentRoot);
        try
        {
            RunLoadedAssetWorkflow(root, contentRoot);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (UnauthorizedAccessException)
            {
                // A collectible dependency can briefly retain its shadow-copy handle.
            }
            catch (IOException)
            {
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunLoadedAssetWorkflow(string root, string contentRoot)
    {
        var project = new DreambitProjectDefinition(
            root,
            Path.Combine(root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(root, "Game.sln"),
            Path.Combine(root, "Game.csproj"),
            Path.Combine(root, "Game.Content.csproj"),
            contentRoot,
            Path.Combine(root, "Game.VK.csproj"));
        using var assets = new AssetDatabase(root, contentRoot, enableWatcher: false);
        using var assemblies = new GameAssemblyLoadService(root);
        var metadata = new InspectorMetadataCache();
        using var types = new EditorTypeRegistry(assemblies, metadata);
        using var editing = new AssetEditingService(
            project,
            assets,
            types,
            metadata,
            assemblies);

        Assert.True(
            assemblies.TryLoad(typeof(TestCustomAsset).Assembly.Location, out var loadError),
            loadError);
        var loadedAssetType = Assert.Single(types.AssetTypes, type =>
            type.FullName == typeof(TestCustomAsset).FullName);

        Assert.True(
            editing.TryCreate(loadedAssetType, "items/test-custom.asset", out var createError),
            createError);
        var source = JObject.Parse(File.ReadAllText(
            Path.Combine(contentRoot, "items", "test-custom.asset")));
        Assert.Equal("test.custom-asset", source.Value<string>("$dreambitType"));
        Assert.Equal(100, source.Value<int>(nameof(TestCustomAsset.Health)));
        Assert.Null(source[nameof(TestCustomAsset.ShouldNeverAppear)]);

        var document = Assert.IsType<DreambitAssetDocument>(editing.Current);
        Assert.Equal(loadedAssetType, document.AssetType);
        var health = Assert.Single(
            metadata.Get(document.AssetType, InspectorTargetKind.Asset),
            member => member.SerializedName == nameof(TestCustomAsset.Health));
        health.SetValue(document.Instance, 150);
        var preview = Assert.IsAssignableFrom<DreambitAsset>(
            Activator.CreateInstance(loadedAssetType));
        document.CopyInspectableValuesTo(preview);
        Assert.Equal(150, health.GetValue(preview));
        preview.Dispose();
    }

    private static void RefreshTestRegistry()
    {
        DreambitAssetTypeRegistry.Refresh(
        [
            typeof(TestCustomAsset),
            typeof(NestedCustomAsset),
            typeof(LegacyMarkedCustomAsset),
            typeof(MigratedWeaponConfig),
            typeof(RenamedStableWeaponConfig),
            typeof(UnannotatedCustomAsset),
            typeof(TestSwordDefinition),
            typeof(TestBowDefinition)
        ]);
    }
}

[DreambitAssetType("test.custom-asset")]
public sealed class TestCustomAsset : DreambitAsset
{
    [DreambitSerialize]
    [Header("Combat")]
    [Dreambit.Range(0, 1000)]
    public int Health { get; set; } = 100;

    [DreambitSerialize]
    public Sprite? Sprite { get; set; }

    [DreambitSerialize]
    public List<Sprite?> SpriteVariants { get; set; } = [];

    [DreambitSerialize]
    public Dictionary<string, Sprite?> SpriteLookup { get; set; } = [];

    public string ShouldNeverAppear { get; set; } = "NOPE";
}

public sealed class TestCustomAssetReferenceComponent : Component
{
    [DreambitSerialize("OldConfig")]
    public TestCustomAsset? Config { get; set; }

    [DreambitSerialize]
    private int PrivateEditorTrap { get; set; }
}

public sealed class TestCustomAssetLoader : AssetLoaderBase<TestCustomAsset>
{
    public override string Extension => ".jsonb";
    public override bool AddToDisposableList => true;
    public override Type TargetType => typeof(TestCustomAsset);

    public override object Load(
        string assetName,
        string pakName,
        bool usePak,
        string contentDirectory)
    {
        using var stream = GetStream(GetPath(assetName), pakName, usePak, contentDirectory);
        var asset = JsnbLoader.Deserialize<TestCustomAsset>(stream);
        asset.AssetName = assetName;
        return asset;
    }
}

[DreambitAssetType("test.automatic-enemy")]
public sealed class AutomaticEnemyConfig : DreambitAsset
{
    [DreambitSerialize] public float MaxHealth;
    [DreambitSerialize] public float BaseDamage;
}

[DreambitAssetType("test.explicit-override-enemy")]
public sealed class ExplicitOverrideEnemyConfig : DreambitAsset
{
    [DreambitSerialize] public float MaxHealth;
}

public sealed class ExplicitOverrideEnemyConfigLoader : AssetLoaderBase<ExplicitOverrideEnemyConfig>
{
    public static int LoadCount { get; set; }
    public override string Extension => ".jsonb";
    public override bool AddToDisposableList => true;
    public override Type TargetType => typeof(ExplicitOverrideEnemyConfig);

    public override object Load(
        string assetName,
        string pakName,
        bool usePak,
        string contentDirectory)
    {
        LoadCount++;
        return new ExplicitOverrideEnemyConfig { AssetName = assetName, MaxHealth = 999 };
    }
}

internal sealed class TestAssetRegistry(AssetId assetId, string assetName) : IAssetRegistry
{
    private readonly IReadOnlyList<AssetCatalogEntry> _assets =
        Array.AsReadOnly(new[] { new AssetCatalogEntry(assetId, assetName, null) });

    public IReadOnlyList<AssetCatalogEntry> GetAssets() => _assets;

    public bool TryResolveAssetName(AssetId requestedAssetId, out string resolvedAssetName)
    {
        resolvedAssetName = assetName;
        return requestedAssetId == assetId;
    }

    public bool TryGetAssetId(string requestedAssetName, out AssetId resolvedAssetId)
    {
        resolvedAssetId = assetId;
        return string.Equals(requestedAssetName, assetName, StringComparison.OrdinalIgnoreCase);
    }
}

[DreambitAssetType("test.nested-asset")]
public sealed class NestedCustomAsset : DreambitAsset
{
    [DreambitSerialize]
    public WeaponStats Stats { get; set; } = new();

    [DreambitSerialize]
    public LegacyNestedObject Legacy { get; set; } = new();
}

public sealed class WeaponStats
{
    [DreambitSerialize]
    public int Damage { get; set; }

    [DreambitSerialize]
    public float Knockback { get; set; }

    public int RuntimeCounter { get; set; }
}

public sealed class LegacyNestedObject
{
    public int Editable { get; set; }
    public int ExistingLegacyValue;
}

[DreambitAssetType("test.legacy-marked")]
public sealed class LegacyMarkedCustomAsset : DreambitAsset
{
    [JsonProperty("legacy_value")]
    public int LegacyValue { get; set; }

    public int RuntimeValue { get; set; }
}

[DreambitAssetType(
    "test.migrated-weapon.v2",
    "test.migrated-weapon",
    "ExampleGame.Items.WeaponDefinition")]
public sealed class MigratedWeaponConfig : DreambitAsset
{
    [DreambitSerialize("Damage")]
    public int BaseDamage { get; set; }
}

[DreambitAssetType("test.renamed-stable")]
public sealed class RenamedStableWeaponConfig : DreambitAsset
{
    [DreambitSerialize]
    public int Damage { get; set; }
}

public sealed class UnannotatedCustomAsset : DreambitAsset
{
    [DreambitSerialize]
    public int Value { get; set; }
}

public abstract class TestWeaponDefinition : DreambitAsset;

[DreambitAssetType("test.sword")]
public sealed class TestSwordDefinition : TestWeaponDefinition;

[DreambitAssetType("test.bow")]
public sealed class TestBowDefinition : TestWeaponDefinition;

[DreambitAssetType("test.duplicate")]
internal sealed class DuplicateAssetA<T> : DreambitAsset;

[DreambitAssetType("test.duplicate")]
internal sealed class DuplicateAssetB<T> : DreambitAsset;

[DreambitAssetType("test.current-former")]
internal sealed class CurrentIdAsset<T> : DreambitAsset;

[DreambitAssetType("test.former-owner", "test.current-former")]
internal sealed class FormerIdAsset<T> : DreambitAsset;
