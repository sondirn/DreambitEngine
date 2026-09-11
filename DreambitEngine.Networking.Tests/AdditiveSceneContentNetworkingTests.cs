using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.World;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class AdditiveSceneContentNetworkingTests
{
    [Fact]
    public void NonNetworkedAdditiveContentDoesNotChangeBoundNetworkWorld()
    {
        using var scene = new AdditiveNetworkingScene();
        var persistent = scene.CreateEntity("persistent-network-object", guidOverride: Guid.NewGuid());
        persistent.AttachComponent<NetworkObject>();
        scene.FlushStructuralChanges();
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(17), true);
        var nextId = 100UL;
        world.BindServerAuthoredEntities(() => new NetworkEntityId(++nextId));
        var records = world.Records.ToArray();
        var bindings = world.AuthoredBindings.ToArray();

        var content = scene.LoadAdditive(new SceneBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "Scenes/NonNetworked.scene",
            Entities = [new EntityBlueprint { Guid = Guid.NewGuid(), Name = "local-only" }]
        });
        scene.Unload(content);

        Assert.Equal(new NetworkSceneEpoch(17), world.SceneEpoch);
        Assert.True(world.AuthoredEntitiesBound);
        Assert.Equal(records, world.Records);
        Assert.Equal(bindings, world.AuthoredBindings);
        Assert.True(world.TryGetNetworkId(persistent, out var networkId));
        Assert.Equal(new NetworkEntityId(101), networkId);
    }

    [Fact]
    public void AuthoredNetworkObjectInAdditiveBlueprintFailsBeforeSceneMutation()
    {
        using var scene = new AdditiveNetworkingScene();
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(4), true);
        world.BindServerAuthoredEntities(() => new NetworkEntityId(1));
        var source = new SceneBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "Scenes/UnsupportedNetworked.scene",
            Entities =
            [
                new EntityBlueprint
                {
                    Guid = Guid.NewGuid(),
                    Name = "unsupported-network-object",
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = typeof(NetworkObject).AssemblyQualifiedName!
                        }
                    ]
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => scene.LoadAdditive(source));

        Assert.Contains("NetworkObject", exception.Message);
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(world.Records);
        Assert.Empty(world.AuthoredBindings);
        Assert.True(world.AuthoredEntitiesBound);
    }

    [Fact]
    public void AuthoredNetworkObjectFailsBeforeAuthoredBindingBegins()
    {
        using var scene = new AdditiveNetworkingScene();
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(5), true);
        var source = CreateNetworkedSource();

        var exception = Assert.Throws<InvalidOperationException>(() => scene.LoadAdditive(source));

        Assert.Contains("NetworkObject", exception.Message);
        Assert.False(world.AuthoredEntitiesBound);
        Assert.Empty(world.Records);
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
    }

    [Fact]
    public void NonNetworkedAdditiveContentLeavesSessionEpochAndRevisionUnchanged()
    {
        using var scene = new AdditiveNetworkingScene();
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var session = new NetworkSession(
            NetworkRole.Host,
            transport,
            new NetworkOptions { GameBuildId = "additive-scene-content" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        session.Start();
        session.AfterSceneAssigned(scene);
        session.PrepareSceneStart(scene);
        var epoch = session.SceneEpoch;
        var revision = session.StructuralRevision;
        var world = session.World;

        var content = scene.LoadAdditive(new SceneBlueprint
        {
            AssetId = AssetId.New(),
            Entities = [new EntityBlueprint { Name = "local-only" }]
        });
        scene.Unload(content);

        Assert.Equal(epoch, session.SceneEpoch);
        Assert.Equal(revision, session.StructuralRevision);
        Assert.Same(world, session.World);
        Assert.Empty(session.World!.Records);
        Assert.True(session.World.AuthoredEntitiesBound);
    }

    [Fact]
    public void OwnedDynamicEntityCannotAcquireNetworkObject()
    {
        using var scene = new AdditiveNetworkingScene();
        var content = scene.LoadAdditive(new SceneBlueprint { AssetId = AssetId.New() });
        var owned = content.CreateEntity("owned-dynamic");

        var exception = Assert.Throws<InvalidOperationException>(
            () => owned.AttachComponent<NetworkObject>());

        Assert.Contains("NetworkObject", exception.Message);
        Assert.Null(owned.GetComponent<NetworkObject>());
        Assert.Same(content, owned.ContentOwner);
        scene.Unload(content);
        Assert.Null(scene.FindEntity(owned.Id));
    }

    [Fact]
    public void ExistingNetworkEntityCannotBeAdoptedByContentInstance()
    {
        using var scene = new AdditiveNetworkingScene();
        var networked = scene.CreateEntity("persistent-network-object");
        networked.AttachComponent<NetworkObject>();
        var content = scene.LoadAdditive(new SceneBlueprint { AssetId = AssetId.New() });

        var exception = Assert.Throws<InvalidOperationException>(() => content.TrackEntity(networked));

        Assert.Contains("NetworkObject", exception.Message);
        Assert.Null(networked.ContentOwner);
        scene.Unload(content);
        Assert.Same(networked, scene.FindEntity(networked.Id));
    }

    private static SceneBlueprint CreateNetworkedSource() => new()
    {
        AssetId = AssetId.New(),
        Entities =
        [
            new EntityBlueprint
            {
                Guid = Guid.NewGuid(),
                Name = "unsupported-network-object",
                Components =
                [
                    new ComponentBlueprint
                    {
                        Type = typeof(NetworkObject).AssemblyQualifiedName!
                    }
                ]
            }
        ]
    };

    private sealed class AdditiveNetworkingScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }
}
