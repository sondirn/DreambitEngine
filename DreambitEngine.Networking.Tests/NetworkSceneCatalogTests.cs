using Dreambit;
using Dreambit.Networking.Scenes;
using Dreambit.Tiled;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkSceneCatalogTests
{
    [Fact]
    public void StableKeysResolveFactoriesAndRejectDuplicates()
    {
        var catalog = new NetworkSceneCatalog();
        catalog.Register("arena", () => new CatalogScene());

        using var scene = catalog.Create("arena");

        Assert.IsType<CatalogScene>(scene);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Register("arena", () => new CatalogScene()));
    }

    [Fact]
    public void RegistrationsAreFrozenForAnActiveSession()
    {
        var catalog = new NetworkSceneCatalog();
        catalog.Register("arena", () => new CatalogScene());

        catalog.Freeze();

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Register("lobby", () => new CatalogScene()));
    }

    [Fact]
    public void BlueprintRegistrationEagerlyMaterializesTheRequestedTiledSceneType()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sceneAssetName = $"tests/scenes/{suffix}";
        var mapAssetName = $"tests/maps/{suffix}";
        var authoredEntityId = Guid.NewGuid();
        var map = new TmxMap
        {
            AssetName = mapAssetName,
            Orientation = "orthogonal",
            TileWidth = 16,
            TileHeight = 16,
            Width = 1,
            Height = 1
        };
        var blueprint = new SceneBlueprint
        {
            AssetName = sceneAssetName,
            Name = "Networked Tiled World",
            Tiled = new TiledSceneReference { AssetName = mapAssetName },
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Authored Network Root",
                    Guid = authoredEntityId
                }
            ]
        };
        Assert.True(Resources.TryRegisterAsset(map));
        Assert.True(Resources.TryRegisterAsset(blueprint));

        try
        {
            var catalog = new NetworkSceneCatalog();
            catalog.RegisterBlueprint<CatalogTiledScene>("world", sceneAssetName);

            using var scene = Assert.IsType<CatalogTiledScene>(catalog.Create("world"));

            Assert.Equal(SceneState.Created, scene.State);
            Assert.Same(map, scene.Map);
            Assert.Null(scene.MapInstance);
            Assert.Equal(authoredEntityId, scene.FindEntity("Authored Network Root").Id);
        }
        finally
        {
            Resources.UnloadAsset(sceneAssetName);
            Resources.UnloadAsset(mapAssetName);
        }
    }

    [Fact]
    public void BlueprintFactoryDisposesTheSceneWhenMaterializationFails()
    {
        var sceneAssetName = $"tests/scenes/{Guid.NewGuid():N}";
        var blueprint = new SceneBlueprint
        {
            AssetName = sceneAssetName,
            Name = "Invalid Host",
            Tiled = new TiledSceneReference { AssetName = "tests/maps/not-resolved" }
        };
        Assert.True(Resources.TryRegisterAsset(blueprint));
        TrackingScene.LastCreated = null;

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                Scene.CreateFromBlueprint<TrackingScene>(sceneAssetName));

            Assert.Contains("must derive from TiledScene", exception.Message);
            Assert.NotNull(TrackingScene.LastCreated);
            Assert.Equal(SceneState.Disposed, TrackingScene.LastCreated.State);
        }
        finally
        {
            Resources.UnloadAsset(sceneAssetName);
            TrackingScene.LastCreated = null;
        }
    }

    private sealed class CatalogScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }

    private sealed class CatalogTiledScene : TiledScene
    {
        public CatalogTiledScene() : base()
        {
        }
    }

    private sealed class TrackingScene : Scene
    {
        public TrackingScene()
        {
            LastCreated = this;
        }

        public static TrackingScene? LastCreated { get; set; }
    }
}
