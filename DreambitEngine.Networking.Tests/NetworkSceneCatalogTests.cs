using Dreambit;
using Dreambit.Networking.Scenes;
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
    public void BlueprintRegistrationEagerlyMaterializesTheRequestedSceneType()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sceneAssetName = $"tests/scenes/{suffix}";
        var authoredEntityId = Guid.NewGuid();
        var blueprint = new SceneBlueprint
        {
            AssetName = sceneAssetName,
            Name = "Networked World",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Authored Network Root",
                    Guid = authoredEntityId
                }
            ]
        };
        Assert.True(Resources.TryRegisterAsset(blueprint));

        try
        {
            var catalog = new NetworkSceneCatalog();
            catalog.RegisterBlueprint<CatalogScene>("world", sceneAssetName);

            using var scene = Assert.IsType<CatalogScene>(catalog.Create("world"));

            Assert.Equal(SceneState.Created, scene.State);
            Assert.Equal(authoredEntityId, scene.FindEntity("Authored Network Root").Id);
        }
        finally
        {
            Resources.UnloadAsset(sceneAssetName);
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
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Invalid Entity",
                    Components = [new ComponentBlueprint { Type = "Missing.Component" }]
                }
            ]
        };
        Assert.True(Resources.TryRegisterAsset(blueprint));
        TrackingScene.LastCreated = null;

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                Scene.CreateFromBlueprint<TrackingScene>(sceneAssetName));

            Assert.Contains("Missing.Component", exception.Message);
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

    private sealed class TrackingScene : Scene
    {
        public TrackingScene()
        {
            LastCreated = this;
        }

        public static TrackingScene? LastCreated { get; set; }
    }
}
