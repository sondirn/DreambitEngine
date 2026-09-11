using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Microsoft.Xna.Framework;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkHierarchyLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DespawningParentPreservesNestedNetworkObjectUntilItsOwnDespawn(
        bool includeOrdinaryBridge)
    {
        var parentSource = Guid.NewGuid();
        var nestedSource = Guid.NewGuid();
        var pair = InMemoryTransport.CreatePair();
        using var serverScene = CreateNestedScene(
            parentSource,
            nestedSource,
            includeOrdinaryBridge,
            NetworkPresence.Replicated);
        NestedScene? clientScene = null;
        using (var server = CreateSession(NetworkRole.Server, pair.Server))
        using (var client = CreateSession(NetworkRole.Client, pair.Client))
        {
            client.SceneChangeRequested += (key, epoch) =>
            {
                Assert.Equal("nested-network-hierarchy", key);
                Assert.Equal(server.SceneEpoch, epoch);
                clientScene = CreateNestedScene(
                    parentSource,
                    nestedSource,
                    includeOrdinaryBridge,
                    NetworkPresence.Replicated);
                client.AfterSceneAssigned(clientScene);
            };

            server.Start();
            server.BeginServerSceneChange("nested-network-hierarchy");
            server.AfterSceneAssigned(serverScene);
            serverScene.Tick();
            client.Start();
            Synchronize(server, client, () => clientScene);

            var serverParent = serverScene.FindEntity(parentSource)!;
            var serverNested = serverScene.FindEntity(nestedSource)!;
            var clientParent = clientScene!.FindEntity(parentSource)!;
            var clientNested = clientScene.FindEntity(nestedSource)!;
            var ordinaryDescendants = GetNetworkLifetimeOrdinaryDescendants(serverParent);
            var clientOrdinaryDescendants = GetNetworkLifetimeOrdinaryDescendants(clientParent);
            var serverNestedChildren = serverNested.GetChildren();
            var clientNestedChildren = clientNested.GetChildren();
            var serverNestedWorldPosition = serverNested.Transform.WorldPosition;
            var clientNestedWorldPosition = clientNested.Transform.WorldPosition;
            Assert.True(server.World!.TryGetNetworkId(serverParent, out var parentId));
            Assert.True(server.World.TryGetNetworkId(serverNested, out var nestedId));
            Assert.True(client.World!.TryGetNetworkId(clientParent, out var clientParentId));
            Assert.True(client.World.TryGetNetworkId(clientNested, out var clientNestedId));
            Assert.Equal(parentId, clientParentId);
            Assert.Equal(nestedId, clientNestedId);
            server.SetPlayerEntity(client.LocalPeerId, serverParent);
            Pump(server, client, 2);
            Assert.True(client.World.TryGetPlayerEntity(client.LocalPeerId, out var mappedParent));
            Assert.Same(clientParent, mappedParent);

            server.Despawn(serverParent);
            Pump(server, client, 4);

            Assert.True(Entity.IsDestroyed(serverParent));
            Assert.True(Entity.IsDestroyed(clientParent));
            Assert.All(ordinaryDescendants, entity => Assert.True(Entity.IsDestroyed(entity)));
            Assert.All(clientOrdinaryDescendants, entity => Assert.True(Entity.IsDestroyed(entity)));
            Assert.False(Entity.IsDestroyed(serverNested));
            Assert.False(Entity.IsDestroyed(clientNested));
            Assert.Null(serverNested.Parent);
            Assert.Null(clientNested.Parent);
            Assert.Equal(serverNestedWorldPosition, serverNested.Transform.WorldPosition);
            Assert.Equal(clientNestedWorldPosition, clientNested.Transform.WorldPosition);
            Assert.False(server.World.TryGetEntity(parentId, out _));
            Assert.False(client.World.TryGetEntity(parentId, out _));
            Assert.False(server.World.TryGetNetworkId(serverParent, out _));
            Assert.False(client.World.TryGetNetworkId(clientParent, out _));
            Assert.False(server.World.TryGetPlayerEntity(client.LocalPeerId, out _));
            Assert.False(client.World.TryGetPlayerEntity(client.LocalPeerId, out _));
            Assert.True(server.World.TryGetEntity(nestedId, out var survivingServer));
            Assert.True(client.World.TryGetEntity(nestedId, out var survivingClient));
            Assert.Same(serverNested, survivingServer);
            Assert.Same(clientNested, survivingClient);
            Assert.Single(server.World.GetRecords(NetworkReplicationScopeId.Global));
            Assert.Single(client.World.GetRecords(NetworkReplicationScopeId.Global));
            Assert.False(server.World.TryGetAuthoredEntity(
                NetworkReplicationScopeId.Global, parentSource, out _));
            Assert.False(client.World.TryGetAuthoredEntity(
                NetworkReplicationScopeId.Global, parentSource, out _));
            Assert.True(server.World.TryGetAuthoredEntity(
                NetworkReplicationScopeId.Global, nestedSource, out var authoredServerNested));
            Assert.True(client.World.TryGetAuthoredEntity(
                NetworkReplicationScopeId.Global, nestedSource, out var authoredClientNested));
            Assert.Same(serverNested, authoredServerNested);
            Assert.Same(clientNested, authoredClientNested);
            Assert.False(server.World.GetAuthoredBindings(NetworkReplicationScopeId.Global)
                .Single(binding => binding.SourceGuid == parentSource).IsPresent);
            Assert.False(client.World.GetAuthoredBindings(NetworkReplicationScopeId.Global)
                .Single(binding => binding.SourceGuid == parentSource).IsPresent);

            server.Despawn(serverNested);
            Pump(server, client, 4);

            Assert.True(Entity.IsDestroyed(serverNested));
            Assert.True(Entity.IsDestroyed(clientNested));
            Assert.All(serverNestedChildren, entity => Assert.True(Entity.IsDestroyed(entity)));
            Assert.All(clientNestedChildren, entity => Assert.True(Entity.IsDestroyed(entity)));
            Assert.False(server.World.TryGetEntity(nestedId, out _));
            Assert.False(client.World.TryGetEntity(nestedId, out _));
            Assert.Empty(server.World.GetRecords(NetworkReplicationScopeId.Global));
            Assert.Empty(client.World.GetRecords(NetworkReplicationScopeId.Global));
            Assert.True(client.IsConnected);
        }

        clientScene?.Dispose();
    }

    [Theory]
    [InlineData(NetworkPresence.ClientOnly)]
    [InlineData(NetworkPresence.ServerOnly)]
    public void PresencePruningPreservesNestedReplicatedNetworkObject(
        NetworkPresence parentPresence)
    {
        var parentSource = Guid.NewGuid();
        var nestedSource = Guid.NewGuid();
        using var serverScene = CreateNestedScene(
            parentSource,
            nestedSource,
            false,
            parentPresence);
        using var clientScene = CreateNestedScene(
            parentSource,
            nestedSource,
            false,
            parentPresence);
        using var serverWorld = new NetworkWorld(
            serverScene,
            new NetworkSceneEpoch(1),
            true);
        using var clientWorld = new NetworkWorld(
            clientScene,
            new NetworkSceneEpoch(1),
            false);
        var nextId = 0UL;

        var bindings = serverWorld.BindServerAuthoredEntities(
            () => new NetworkEntityId(++nextId));
        clientWorld.BindClientAuthoredEntities(bindings);

        var serverParent = serverScene.FindEntity(parentSource);
        var serverNested = serverScene.FindEntity(nestedSource)!;
        var clientParent = clientScene.FindEntity(parentSource);
        var clientNested = clientScene.FindEntity(nestedSource)!;
        var binding = Assert.Single(bindings);
        Assert.Equal(nestedSource, binding.SourceGuid);
        Assert.True(serverWorld.TryGetNetworkId(serverNested, out var serverNestedId));
        Assert.True(clientWorld.TryGetNetworkId(clientNested, out var clientNestedId));
        Assert.Equal(binding.NetworkEntityId, serverNestedId);
        Assert.Equal(serverNestedId, clientNestedId);

        if (parentPresence == NetworkPresence.ClientOnly)
        {
            Assert.Null(serverParent);
            Assert.Null(serverNested.Parent);
            Assert.NotNull(clientParent);
            Assert.Same(clientParent, clientNested.Parent);
        }
        else
        {
            Assert.NotNull(serverParent);
            Assert.Same(serverParent, serverNested.Parent);
            Assert.Null(clientParent);
            Assert.Null(clientNested.Parent);
        }

        Assert.False(Entity.IsDestroyed(serverNested));
        Assert.False(Entity.IsDestroyed(clientNested));
    }

    [Fact]
    public void ScopedDespawnUsesNetworkBoundaryButScopeUnloadStillDestroysExactContentSet()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var host = CreateSession(NetworkRole.Host, transport);
        using var scene = new NestedScene();
        var parentSource = Guid.NewGuid();
        var nestedSource = Guid.NewGuid();
        var blueprint = CreateNestedScopeBlueprint(parentSource, nestedSource);
        Assert.True(Resources.TryRegisterAsset(blueprint));
        host.Start();
        host.AfterSceneAssigned(scene);

        var individualScopeId = host.LoadScope(blueprint.AssetName!);
        Assert.True(host.TryGetScope(individualScopeId, out var individualScope));
        var parent = individualScope!.Content!.GetEntity(parentSource);
        var nested = individualScope.Content.GetEntity(nestedSource);
        Assert.True(host.World!.TryGetNetworkId(parent, out var parentId));
        Assert.True(host.World.TryGetNetworkId(nested, out var nestedId));

        host.Despawn(parent);

        Assert.True(Entity.IsDestroyed(parent));
        Assert.False(Entity.IsDestroyed(nested));
        Assert.False(host.World.TryGetEntity(parentId, out _));
        Assert.True(host.World.TryGetEntity(nestedId, out var surviving));
        Assert.Same(nested, surviving);

        var wholeScopeId = host.LoadScope(blueprint.AssetName!);
        Assert.True(host.TryGetScope(wholeScopeId, out var wholeScope));
        var wholeParent = wholeScope!.Content!.GetEntity(parentSource);
        var wholeNested = wholeScope.Content.GetEntity(nestedSource);
        Assert.True(host.World.TryGetNetworkId(wholeParent, out var wholeParentId));
        Assert.True(host.World.TryGetNetworkId(wholeNested, out var wholeNestedId));

        host.UnloadScope(wholeScopeId);

        Assert.False(wholeScope.IsLoaded);
        Assert.False(wholeScope.Content.IsLoaded);
        Assert.True(Entity.IsDestroyed(wholeParent));
        Assert.True(Entity.IsDestroyed(wholeNested));
        Assert.False(host.World.TryGetEntity(wholeParentId, out _));
        Assert.False(host.World.TryGetEntity(wholeNestedId, out _));

        host.Despawn(nested);
        Assert.True(Entity.IsDestroyed(nested));
        Assert.False(host.World.TryGetEntity(nestedId, out _));
        host.UnloadScope(individualScopeId);
    }

    private static NetworkSession CreateSession(NetworkRole role, INetworkTransport transport) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "network-hierarchy-tests" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());

    private static NestedScene CreateNestedScene(
        Guid parentSource,
        Guid nestedSource,
        bool includeOrdinaryBridge,
        NetworkPresence parentPresence)
    {
        var scene = new NestedScene();
        var parent = scene.CreateEntity("network-parent", guidOverride: parentSource);
        parent.AttachComponent<NetworkObject>().Presence = parentPresence;
        parent.Transform.Position = new Vector3(100, 25, 0);
        Entity nestedParent = parent;
        if (includeOrdinaryBridge)
        {
            nestedParent = scene.CreateEntity("ordinary-bridge");
            nestedParent.Parent = parent;
            nestedParent.Transform.Position = new Vector3(10, 5, 0);
            var ordinaryLeaf = scene.CreateEntity("ordinary-leaf");
            ordinaryLeaf.Parent = nestedParent;
        }

        var nested = scene.CreateEntity("nested-network-object", guidOverride: nestedSource);
        nested.AttachComponent<NetworkObject>();
        nested.Parent = nestedParent;
        nested.Transform.Position = new Vector3(3, 7, 0);
        var nestedOrdinaryChild = scene.CreateEntity("nested-owned-child");
        nestedOrdinaryChild.Parent = nested;
        return scene;
    }

    private static SceneBlueprint CreateNestedScopeBlueprint(Guid parentSource, Guid nestedSource) =>
        new()
        {
            AssetId = AssetId.New(),
            AssetName = $"tests/scopes/nested-hierarchy-{Guid.NewGuid():N}.scene",
            Entities =
            [
                new EntityBlueprint
                {
                    Guid = parentSource,
                    Name = "scoped-parent",
                    Components =
                    [
                        new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! }
                    ],
                    Children =
                    [
                        new EntityBlueprint
                        {
                            Guid = Guid.NewGuid(),
                            Name = "ordinary-bridge",
                            Children =
                            [
                                new EntityBlueprint
                                {
                                    Guid = nestedSource,
                                    Name = "scoped-nested-network-object",
                                    Components =
                                    [
                                        new ComponentBlueprint
                                        {
                                            Type = typeof(NetworkObject).AssemblyQualifiedName!
                                        }
                                    ],
                                    Children =
                                    [
                                        new EntityBlueprint
                                        {
                                            Guid = Guid.NewGuid(),
                                            Name = "scoped-nested-owned-child"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

    private static void Synchronize(
        NetworkSession server,
        NetworkSession client,
        Func<NestedScene?> getClientScene)
    {
        for (var index = 0; index < 20; index++)
        {
            Pump(server, client, 1);
            getClientScene()?.Tick();
            if (getClientScene()?.State == SceneState.Running)
            {
                Pump(server, client, 2);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("Client Scene did not complete synchronization.");
    }

    private static void Pump(NetworkSession server, NetworkSession client, int count)
    {
        for (var index = 0; index < count; index++)
        {
            server.PollTransport();
            client.PollTransport();
            server.ApplyInbound();
            client.ApplyInbound();
            server.AdvanceClientScopeLoads();
            client.AdvanceClientScopeLoads();
        }
    }

    private static Entity[] GetNetworkLifetimeOrdinaryDescendants(Entity root)
    {
        var result = new List<Entity>();
        Collect(root);
        return result.ToArray();

        void Collect(Entity parent)
        {
            foreach (var child in parent.Children)
            {
                if (child.GetComponent<NetworkObject>() is not null)
                    continue;
                result.Add(child);
                Collect(child);
            }
        }
    }

    private sealed class NestedScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }
}
