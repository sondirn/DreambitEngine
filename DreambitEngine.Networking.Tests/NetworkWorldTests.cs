using System;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.World;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkWorldTests
{
    [Fact]
    public void AuthoredEntitiesUseSourceGuidOnlyAsBindingLocator()
    {
        var sourceGuid = Guid.NewGuid();
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var serverEntity = CreateNetworkEntity(serverScene, sourceGuid);
        var clientEntity = CreateNetworkEntity(clientScene, sourceGuid);
        using var serverWorld = new NetworkWorld(serverScene, new NetworkSceneEpoch(1), true);
        using var clientWorld = new NetworkWorld(clientScene, new NetworkSceneEpoch(1), false);

        var nextId = 40UL;
        var bindings = serverWorld.BindServerAuthoredEntities(
            () => new NetworkEntityId(++nextId));
        clientWorld.BindClientAuthoredEntities(bindings);

        Assert.Equal(sourceGuid, serverEntity.Id);
        Assert.Equal(sourceGuid, clientEntity.Id);
        Assert.True(serverWorld.TryGetNetworkId(serverEntity, out var serverId));
        Assert.True(clientWorld.TryGetNetworkId(clientEntity, out var clientId));
        Assert.Equal(new NetworkEntityId(41), serverId);
        Assert.Equal(serverId, clientId);
    }

    [Fact]
    public void DynamicEntitiesCanMapDifferentRuntimeGuidsToTheSameRemoteIdentity()
    {
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var serverEntity = CreateNetworkEntity(serverScene);
        var clientEntity = CreateNetworkEntity(clientScene);
        var networkId = new NetworkEntityId(77);
        var blueprintId = AssetId.New();
        using var serverWorld = new NetworkWorld(serverScene, new NetworkSceneEpoch(3), true);
        using var clientWorld = new NetworkWorld(clientScene, new NetworkSceneEpoch(3), false);

        serverWorld.RegisterDynamicEntity(
            serverEntity, networkId, new NetworkPeerId(2), blueprintId, "player");
        clientWorld.RegisterDynamicEntity(
            clientEntity, networkId, new NetworkPeerId(2), blueprintId, "player");

        Assert.NotEqual(serverEntity.Id, clientEntity.Id);
        Assert.True(serverWorld.TryGetEntity(networkId, out var resolvedServer));
        Assert.True(clientWorld.TryGetEntity(networkId, out var resolvedClient));
        Assert.Same(serverEntity, resolvedServer);
        Assert.Same(clientEntity, resolvedClient);
    }

    [Fact]
    public void DespawnDisablesImmediatelyAndLetsTheSceneFlushDestruction()
    {
        using var scene = new TestScene();
        var entity = CreateNetworkEntity(scene);
        scene.FlushStructuralChanges();
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(1), true);
        var id = new NetworkEntityId(9);
        world.RegisterDynamicEntity(entity, id, NetworkPeerId.None, AssetId.New(), null);

        world.DespawnLocal(id);

        Assert.False(entity.Enabled);
        Assert.True(Entity.IsDestroyed(entity));
        Assert.False(world.TryGetEntity(id, out _));
        Assert.Same(entity, scene.FindEntity(entity.Id));

        scene.FlushStructuralChanges();
        Assert.Null(scene.FindEntity(entity.Id));
    }

    [Fact]
    public void DirectEntityDestructionRemovesTheNetworkMapping()
    {
        using var scene = new TestScene();
        var entity = CreateNetworkEntity(scene);
        scene.FlushStructuralChanges();
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(1), true);
        var id = new NetworkEntityId(10);
        world.RegisterDynamicEntity(entity, id, NetworkPeerId.None, AssetId.New(), null);

        Entity.Destroy(entity);
        world.ReconcileDestroyedEntities();

        Assert.False(world.TryGetEntity(id, out _));
    }

    [Fact]
    public void ClientAuthoredBindingRejectsMissingSourceGuid()
    {
        using var scene = new TestScene();
        CreateNetworkEntity(scene, Guid.NewGuid());
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(2), false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => world.BindClientAuthoredEntities(
                [new NetworkAuthoredBinding(Guid.NewGuid(), new NetworkEntityId(1), NetworkPeerId.None)]));

        Assert.Contains("no authored network binding", exception.Message);
    }

    [Fact]
    public void NetworkEntityReferenceCannotResolveAcrossSceneEpochs()
    {
        using var scene = new TestScene();
        var entity = CreateNetworkEntity(scene);
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(8), true);
        var id = new NetworkEntityId(99);
        world.RegisterDynamicEntity(entity, id, NetworkPeerId.None, AssetId.New(), null);

        Assert.True(world.TryResolve(new NetworkEntityRef(new NetworkSceneEpoch(8), id), out var resolved));
        Assert.Same(entity, resolved);
        Assert.False(world.TryResolve(new NetworkEntityRef(new NetworkSceneEpoch(7), id), out _));
    }

    private static Entity CreateNetworkEntity(Scene scene, Guid? id = null)
    {
        var entity = scene.CreateEntity("networked", guidOverride: id);
        entity.AttachComponent<NetworkObject>();
        return entity;
    }

    private sealed class TestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }
}
