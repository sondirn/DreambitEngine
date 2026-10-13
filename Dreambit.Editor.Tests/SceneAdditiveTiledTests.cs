using Dreambit.ECS;
using Dreambit.Editor.Scenes;
using Dreambit.Networking;
using Dreambit.Tiled;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Tests;

public sealed class SceneAdditiveTiledTests
{
    [Fact]
    public void AdditiveTiledBlueprintLoadsIntoOrdinarySceneAndUnloadsItsGeneratedEntities()
    {
        using var scene = new AdditiveTestScene();
        var persistent = scene.CreateEntity("persistent");
        var source = CreateTiledSource("village");

        var content = LoadTiled(scene, source, CreateEmptyMap("maps/village"));

        Assert.NotNull(content.TiledMap);
        Assert.All(content.TiledMap.OwnedEntities, entity =>
            Assert.Same(content, entity.ContentOwner));
        Assert.Contains(content.TiledMap.RootEntity, content.OwnedEntities);
        Assert.NotEmpty(content.TiledMap.TilemapRenderers);

        var map = content.TiledMap;
        Assert.True(scene.Unload(content));

        Assert.True(map.IsUnloaded);
        Assert.Null(content.TiledMap);
        Assert.Same(persistent, scene.FindEntity(persistent.Id));
        Assert.DoesNotContain(scene.GetAllEntities(), entity => entity.IsTiledGenerated);
    }

    [Fact]
    public void DeferredContentUnloadInvalidatesTiledMapBeforeSafelyDisposingItsEntities()
    {
        using var scene = new AdditiveTestScene();
        var content = LoadTiled(
            scene,
            CreateTiledSource("deferred"),
            CreateEmptyMap("maps/deferred"));
        var map = content.TiledMap!;
        var ownedEntities = map.OwnedEntities.ToArray();
        scene.FlushStructuralChanges();

        scene.RunAtContentCallbackBoundary(() =>
        {
            Assert.True(scene.Unload(content));

            Assert.False(content.IsLoaded);
            Assert.True(map.IsUnloaded);
            Assert.Throws<ObjectDisposedException>(() => map.GetRuntimeTileLayer("Ground"));
            Assert.All(ownedEntities, entity =>
            {
                Assert.False(Entity.IsNull(entity));
                Assert.False(entity.Enabled);
                Assert.True(entity.UpdatesSuspended);
            });
        });

        Assert.All(ownedEntities, entity => Assert.True(Entity.IsNull(entity)));
        Assert.Empty(scene.ContentInstances);
        Assert.DoesNotContain(scene.GetAllEntities(), entity => entity.IsTiledGenerated);
    }

    [Fact]
    public void AdditiveGeneratedEntitiesCannotRouteIntoDocumentImportedSourceOverrides()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateTiledSource("editor-isolation");
        var content = LoadTiled(scene, source, CreateEmptyMap("maps/editor-isolation"));
        var generated = content.TiledMap!.RootEntity;
        var importedSources = new ImportedSceneSources();

        Assert.False(importedSources.TryIdentify(generated, out _));
        generated.Name = "runtime-only-name";
        importedSources.RecordName(source, generated);

        Assert.Empty(source.Tiled!.EntityOverrides);
    }

    [Fact]
    public void TwoAdditiveMapsAndDuplicateSourceHaveIndependentIdsAndRuntimeState()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateTiledSource("shared");
        var map = CreateEmptyMap("maps/shared");

        var a = LoadTiled(scene, source, map);
        var b = LoadTiled(scene, source, map);

        Assert.NotSame(a.TiledMap, b.TiledMap);
        Assert.NotEqual(a.TiledMap!.RootEntity.Id, b.TiledMap!.RootEntity.Id);
        Assert.Equal(a.TiledMap.OwnedEntities.Count, b.TiledMap.OwnedEntities.Count);
        Assert.False(a.TiledMap.OwnedEntities
            .Select(entity => entity.Id)
            .Intersect(b.TiledMap.OwnedEntities.Select(entity => entity.Id))
            .Any());

        var tile = new TiledTileReference("tiles/test", 7, TmxTileFlipFlags.None);
        var aLayer = a.TiledMap.GetRuntimeTileLayer("Ground");
        var bLayer = b.TiledMap.GetRuntimeTileLayer("Ground");
        aLayer.SetRuntimeOverride(new Point(3, 4), tile);
        aLayer.SetGeneratedTile(new Point(8, 9), true, tile);

        Assert.Equal(tile, aLayer.GetTile(3, 4));
        Assert.Null(bLayer.GetTile(3, 4));
        Assert.Equal(tile, aLayer.GetTile(8, 9));
        Assert.Null(bLayer.GetTile(8, 9));

        var bRoot = b.TiledMap.RootEntity;
        scene.Unload(a);

        Assert.True(aLayer is not null);
        Assert.Throws<ObjectDisposedException>(() => aLayer.GetTile(3, 4));
        Assert.Same(bRoot, scene.FindEntity(bRoot.Id));
        Assert.Null(bLayer.GetTile(3, 4));
    }

    [Fact]
    public void PrimaryTiledSceneAndAdditiveMapCoexistWithoutSharingLifetimeService()
    {
        var primaryMap = CreateEmptyMap("maps/primary");
        var additiveMap = CreateEmptyMap("maps/additive");
        using var scene = new AdditivePrimaryTiledScene();
        scene.LoadIntoSelf(
            CreateTiledSource("primary"),
            new SceneBlueprintLoadOptions { TiledMapResolver = _ => primaryMap });
        var primary = scene.LoadMap();

        var additive = LoadTiled(scene, CreateTiledSource("additive"), additiveMap);

        Assert.Same(primary, scene.MapInstance);
        Assert.NotSame(primary, additive.TiledMap);
        Assert.False(primary.IsUnloaded);
        Assert.False(additive.TiledMap!.IsUnloaded);

        scene.Unload(additive);

        Assert.False(primary.IsUnloaded);
        Assert.Same(primary, scene.MapInstance);
    }

    [Fact]
    public void GeneratedOverridesApplyPerInstanceAndDoNotMutateSourceMap()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateTiledSource("overrides");
        source.Tiled!.EntityOverrides[TiledGeneratedEntityKeys.Map] =
            new TiledGeneratedEntityOverride
            {
                Name = "Overridden Map",
                Position = new Vector3(4, 5, 0)
            };
        var map = CreateEmptyMap("maps/overrides");
        var originalCsv = ((TmxTileLayer)map.Layers[0]).Data!.Value;

        var a = LoadTiled(scene, source, map);
        var b = LoadTiled(scene, source, map);

        Assert.Equal("Overridden Map", a.TiledMap!.RootEntity.Name);
        Assert.Equal(new Vector3(4, 5, 0), a.TiledMap.RootEntity.Transform.Position);
        Assert.Equal("Overridden Map", b.TiledMap!.RootEntity.Name);
        a.TiledMap.RootEntity.Name = "Runtime A";
        Assert.Equal("Overridden Map", b.TiledMap.RootEntity.Name);

        a.TiledMap.GetRuntimeTileLayer("Ground")
            .SetRuntimeOverride(new Point(2, 2), new TiledTileReference("tiles/test", 1));
        Assert.Equal(originalCsv, ((TmxTileLayer)map.Layers[0]).Data!.Value);
    }

    [Fact]
    public void TiledTrackEntityPropagatesOwnershipButForeignDescendantsAreNotAdoptedOnUnload()
    {
        using var scene = new AdditiveTestScene();
        var content = LoadTiled(
            scene,
            CreateTiledSource("tracking"),
            CreateEmptyMap("maps/tracking"));
        var tracked = scene.CreateEntity("tracked");
        content.TiledMap!.TrackEntity(tracked);
        var persistentChild = scene.CreateEntity("persistent-child");
        persistentChild.Parent = content.TiledMap.RootEntity;

        scene.Unload(content);

        Assert.Null(scene.FindEntity(tracked.Id));
        Assert.Same(persistentChild, scene.FindEntity(persistentChild.Id));
        Assert.Null(persistentChild.Parent);
    }

    [Fact]
    public void FailureAfterTiledImportRollsBackMapRenderersAuthoredEntitiesAndSettings()
    {
        using var scene = new AdditiveTestScene();
        scene.ApplySettings(new SceneSettings { Exposure = 1.6f });
        var source = CreateTiledSource("failure");
        source.Settings = new SceneSettings { Exposure = 0.4f };
        source.Entities =
        [
            new EntityBlueprint
            {
                Name = "authored-failure",
                Components = [BlueprintComponent<AdditiveConstructionFailureComponent>()]
            }
        ];
        AdditiveConstructionFailureComponent.FailConstruction = true;
        try
        {
            Assert.ThrowsAny<Exception>(() => LoadTiled(
                scene,
                source,
                CreateEmptyMap("maps/failure"),
                applySettings: true));
        }
        finally
        {
            AdditiveConstructionFailureComponent.FailConstruction = false;
        }

        Assert.Equal(1.6f, scene.Settings.Exposure);
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(scene.GetAllDrawables());
    }

    [Fact]
    public void PartialImporterFailureLeaksNoGeneratedEntities()
    {
        using var scene = new AdditiveTestScene();
        var map = CreateEmptyMap("maps/partial");
        map.Layers.Add(new TmxTileLayer
        {
            Id = 2,
            Name = "Unsupported",
            BlendMode = "multiply",
            Width = 1,
            Height = 1,
            Data = new TmxData { Encoding = "csv", Value = "0" }
        });

        Assert.Throws<TiledException>(() =>
            LoadTiled(scene, CreateTiledSource("partial"), map));

        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(scene.GetAllDrawables());
    }

    [Theory]
    [InlineData(typeof(NetworkObject))]
    [InlineData(typeof(AdditiveTestSceneService))]
    public void ForbiddenTiledOverrideComponentFailsBeforeImport(Type forbiddenType)
    {
        using var scene = new AdditiveTestScene();
        var source = CreateTiledSource("forbidden-override");
        source.Tiled!.EntityOverrides[TiledGeneratedEntityKeys.Map] =
            new TiledGeneratedEntityOverride
            {
                Components = new Dictionary<string, Dictionary<string, Newtonsoft.Json.Linq.JToken>>
                {
                    [forbiddenType.AssemblyQualifiedName!] = []
                }
            };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LoadTiled(scene, source, CreateEmptyMap("maps/forbidden-override")));

        Assert.Contains(forbiddenType.Name, exception.Message);
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(scene.GetAllDrawables());
    }

    [Fact]
    public void AuthoredColliderAndRendererComponentsAreDisposedWithAdditiveMap()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateTiledSource("collider");
        source.Entities =
        [
            new EntityBlueprint
            {
                Name = "authored-collider",
                Components = [BlueprintComponent<BoxCollider>()]
            }
        ];
        var content = LoadTiled(scene, source, CreateEmptyMap("maps/collider"));
        scene.FlushStructuralChanges();
        var collider = content.RootEntities[0].GetComponent<BoxCollider>();
        var renderers = content.TiledMap!.TilemapRenderers.ToArray();

        scene.Unload(content);

        Assert.True(Component.IsNull(collider));
        Assert.All(renderers, renderer => Assert.True(Component.IsNull(renderer)));
        Assert.Empty(scene.GetAllDrawables());
    }

    [Fact]
    public void SceneDisposalInvalidatesAdditiveTiledHandle()
    {
        var scene = new AdditiveTestScene();
        var content = LoadTiled(
            scene,
            CreateTiledSource("dispose"),
            CreateEmptyMap("maps/dispose"));
        var map = content.TiledMap!;

        scene.Dispose();

        Assert.False(content.IsLoaded);
        Assert.Null(content.TiledMap);
        Assert.True(map.IsUnloaded);
    }

    private static SceneContentInstance LoadTiled(
        Scene scene,
        SceneBlueprint source,
        TmxMap map,
        bool applySettings = false)
    {
        return scene.LoadAdditive(
            source,
            new SceneContentLoadOptions
            {
                ApplySceneSettings = applySettings,
                TiledMapResolver = _ => map,
                TiledMapImporter = new TiledMapImporter(_ => null)
            });
    }

    private static SceneBlueprint CreateTiledSource(string name) => new()
    {
        Name = name,
        Tiled = new TiledSceneReference { AssetName = $"maps/{name}" }
    };

    private static TmxMap CreateEmptyMap(string assetName) => new()
    {
        AssetName = assetName,
        Orientation = "orthogonal",
        RenderOrder = "right-down",
        Width = 1,
        Height = 1,
        TileWidth = 16,
        TileHeight = 16,
        Layers =
        [
            new TmxTileLayer
            {
                Id = 1,
                Name = "Ground",
                Width = 1,
                Height = 1,
                Data = new TmxData { Encoding = "csv", Value = "0" }
            }
        ]
    };

    private static ComponentBlueprint BlueprintComponent<T>() where T : Component => new()
    {
        Type = SceneDocumentSerializer.GetComponentTypeId(typeof(T))
    };

    private sealed class AdditivePrimaryTiledScene : TiledScene
    {
    }
}
