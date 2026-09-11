using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Session;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Dreambit.Tiled;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkReplicationScopeTests
{
    private readonly ITestOutputHelper _output;

    public NetworkReplicationScopeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ScopedAuthoredIdentityIsScopeAndSourceGuidAndUnbindIsExact()
    {
        using var scene = new ScopeTestScene();
        using var world = new NetworkWorld(
            scene, new NetworkSceneEpoch(3), true, CreateReplication());
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("world-identity", sourceGuid);
        var coordinator = new object();
        var firstContent = scene.LoadNetworkAdditive(source, source.AssetName, coordinator);
        var secondContent = scene.LoadNetworkAdditive(source, source.AssetName, coordinator);
        var firstScope = new NetworkReplicationScopeId(2);
        var secondScope = new NetworkReplicationScopeId(3);
        var next = 0UL;

        world.BindServerAuthoredScope(firstScope, firstContent, () => new NetworkEntityId(++next));
        world.BindServerAuthoredScope(secondScope, secondContent, () => new NetworkEntityId(++next));

        var firstBinding = Assert.Single(world.GetAuthoredBindings(firstScope));
        var secondBinding = Assert.Single(world.GetAuthoredBindings(secondScope));
        Assert.Equal(sourceGuid, firstBinding.SourceGuid);
        Assert.Equal(sourceGuid, secondBinding.SourceGuid);
        Assert.NotEqual(firstBinding.NetworkEntityId, secondBinding.NetworkEntityId);
        Assert.Equal(firstScope, firstBinding.Scope);
        Assert.Equal(secondScope, secondBinding.Scope);

        world.UnregisterScope(firstScope);
        scene.UnloadNetworkContent(firstContent, coordinator);
        Assert.False(world.TryGetEntity(firstBinding.NetworkEntityId, out _));
        Assert.True(world.TryGetEntity(secondBinding.NetworkEntityId, out var surviving));
        Assert.Same(secondContent.GetEntity(sourceGuid), surviving);
        Assert.Empty(world.GetAuthoredBindings(firstScope));
        Assert.Single(world.GetAuthoredBindings(secondScope));
    }

    [Fact]
    public void TrustedScopeBindsAuthoredAndDynamicEntitiesAndRequiresCoordinatedUnload()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = CreateReplication();
        var clientReplication = CreateReplication();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverReplication);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientReplication);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        string? connectionDiagnostic = null;
        client.PeerDisconnected += (_, _, diagnostic) => connectionDiagnostic = diagnostic;
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("single", sourceGuid);
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var scopeId = server.LoadScope(source.AssetName!);
        Assert.NotEqual(NetworkReplicationScopeId.None, scopeId);
        Assert.NotEqual(NetworkReplicationScopeId.Global, scopeId);
        Assert.True(server.TryGetScope(scopeId, out var serverScope));
        var serverAuthored = serverScope!.Content!.GetEntity(sourceGuid);
        serverAuthored.GetComponent<ScopedState>().Value = 17;
        server.Subscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 8);

        client.TryGetScopeLoadStatus(scopeId, out var loadStatus);
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, scopeId),
            connectionDiagnostic ?? loadStatus.ToString());
        Assert.True(client.TryGetScope(scopeId, out var clientScope));
        Assert.True(clientScope!.IsReady);
        Assert.NotEqual(serverScope.Content.InstanceId, clientScope!.Content!.InstanceId);
        var firstClientContentId = clientScope.Content.InstanceId;
        var clientAuthored = clientScope.Content.GetEntity(sourceGuid);
        Assert.Equal(17, clientAuthored.GetComponent<ScopedState>().Value);
        Assert.True(server.World!.TryGetNetworkId(serverAuthored, out var authoredId));
        Assert.True(client.World!.TryGetNetworkId(clientAuthored, out var clientAuthoredId));
        Assert.Equal(authoredId, clientAuthoredId);
        Assert.Throws<InvalidOperationException>(() => clientScene.Unload(clientScope.Content));
        Assert.Throws<InvalidOperationException>(() => clientScope.Content.CreateEntity("unmanaged"));

        var serverDynamic = server.Spawn(
            dynamicBlueprint,
            entity => entity.GetComponent<ScopedState>().Value = 29,
            new NetworkSpawnOptions { Scope = scopeId });
        Pump(server, [client], 4);
        Assert.True(server.World.TryGetNetworkId(serverDynamic, out var dynamicId));
        Assert.True(client.World.TryGetEntity(dynamicId, out var clientDynamic));
        Assert.Equal(29, clientDynamic!.GetComponent<ScopedState>().Value);

        Assert.Throws<InvalidOperationException>(() => server.UnloadScope(scopeId));
        var staleSnapshotRevision = client.StructuralRevision;
        server.Unsubscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 5);
        Assert.False(client.TryGetScope(scopeId, out _));
        Assert.False(client.World.TryGetEntity(authoredId, out _));
        Assert.False(client.World.TryGetEntity(dynamicId, out _));
        Assert.True(server.TryGetScope(scopeId, out _));

        var staleSnapshot = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Snapshot,
                client.SessionId,
                client.SceneEpoch,
                client.ServerTick,
                staleSnapshotRevision),
            writer =>
            {
                writer.WriteUInt32(2);
                writer.WriteUInt32(scopeId.Value);
                writer.WriteUInt64(dynamicId.Value);
                writer.WriteUInt16(901);
                writer.WriteLengthPrefixedBytes(BitConverter.GetBytes(999), sizeof(int));
            },
            NetworkOptions.DefaultMaxProtocolPayload);
        pair.Server.Send(pair.Server.Connection, staleSnapshot,
            NetworkDelivery.UnreliableSequenced, 2);
        Pump(server, [client], 2);
        Assert.True(client.IsConnected);
        Assert.False(client.TryGetScope(scopeId, out _));

        server.Subscribe(client.LocalPeerId, scopeId);
        pair.Server.Send(pair.Server.Connection, staleSnapshot,
            NetworkDelivery.UnreliableSequenced, 2);
        Pump(server, [client], 16);
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, scopeId));
        Assert.True(client.TryGetScope(scopeId, out var reloadedScope));
        Assert.True(reloadedScope!.IsReady);
        Assert.NotEqual(firstClientContentId, reloadedScope.Content!.InstanceId);
        Assert.True(client.World.TryGetEntity(dynamicId, out var reloadedDynamic));
        Assert.Equal(29, reloadedDynamic!.GetComponent<ScopedState>().Value);

        pair.Server.Send(pair.Server.Connection, staleSnapshot,
            NetworkDelivery.UnreliableSequenced, 2);
        Pump(server, [client], 2);
        Assert.True(client.IsConnected);
        Assert.Equal(29, reloadedDynamic.GetComponent<ScopedState>().Value);

        server.Unsubscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 5);

        server.UnloadScope(scopeId);
        Assert.False(server.TryGetScope(scopeId, out _));
        Assert.False(server.World.TryGetEntity(authoredId, out _));
        Assert.Empty(serverScene.ContentInstances);
    }

    [Fact]
    public void LateSubscriberDoesNotRecreateAnAuthoredEntityDespawnedBeforeItsBaseline()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication());
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("authored-tombstone", sourceGuid);
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var scopeId = server.LoadScope(source.AssetName!);
        Assert.True(server.TryGetScope(scopeId, out var serverScope));
        var authored = serverScope!.Content!.GetEntity(sourceGuid);
        Assert.True(server.World!.TryGetNetworkId(authored, out var authoredId));
        server.Despawn(authored);
        Assert.False(server.World.TryGetEntity(authoredId, out _));
        Assert.False(Assert.Single(server.World.GetAuthoredBindings(scopeId)).IsPresent);

        server.Subscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 8);

        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, scopeId));
        Assert.True(client.TryGetScope(scopeId, out var clientScope));
        Assert.False(clientScope!.Content!.TryGetEntity(sourceGuid, out _));
        Assert.False(client.World!.TryGetEntity(authoredId, out _));
        Assert.False(Assert.Single(client.World.GetAuthoredBindings(scopeId)).IsPresent);
    }

    [Fact]
    public void TwoPeersReceiveIndependentScopeProjectionsAndCanTransitionWithoutRevisionGaps()
    {
        var hub = MultiClientInMemoryTransport.Create(3);
        var serverReplication = CreateReplication();
        using var server = CreateSession(NetworkRole.Server, hub.Server, serverReplication);
        using var clientA = CreateSession(NetworkRole.Client, hub.Clients[0], CreateReplication());
        using var clientB = CreateSession(NetworkRole.Client, hub.Clients[1], CreateReplication());
        using var clientC = CreateSession(NetworkRole.Client, hub.Clients[2], CreateReplication());
        using var serverScene = new ScopeTestScene();
        using var sceneA = new ScopeTestScene();
        using var sceneB = new ScopeTestScene();
        using var sceneC = new ScopeTestScene();
        var sharedSourceGuid = Guid.NewGuid();
        var village = CreateScopeBlueprint("village", sharedSourceGuid);
        var tree = CreateScopeBlueprint("tree", sharedSourceGuid);
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(village));
        Assert.True(Resources.TryRegisterAsset(tree));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        clientA.Start();
        clientB.Start();
        Pump(server, [clientA, clientB], 8);
        server.AfterSceneAssigned(serverScene);
        clientA.AfterSceneAssigned(sceneA);
        clientB.AfterSceneAssigned(sceneB);

        var villageScope = server.LoadScope(village.AssetName!);
        var treeScope = server.LoadScope(tree.AssetName!);
        Assert.NotEqual(villageScope, treeScope);
        server.Subscribe(clientA.LocalPeerId, villageScope);
        server.Subscribe(clientB.LocalPeerId, treeScope);
        Pump(server, [clientA, clientB], 10);
        Assert.True(clientA.TryGetScope(villageScope, out _));
        Assert.False(clientA.TryGetScope(treeScope, out _));
        Assert.True(clientB.TryGetScope(treeScope, out _));
        Assert.False(clientB.TryGetScope(villageScope, out _));
        Assert.True(server.TryGetScope(treeScope, out var serverTree));
        var treeAuthored = serverTree!.Content!.GetEntity(sharedSourceGuid);
        server.SetOwner(treeAuthored, clientB.LocalPeerId);
        Pump(server, [clientA, clientB], 4);

        var villageNpc = server.Spawn(dynamicBlueprint,
            entity => entity.GetComponent<ScopedState>().Value = 101,
            new NetworkSpawnOptions { Scope = villageScope });
        var treeNpc = server.Spawn(dynamicBlueprint,
            entity => entity.GetComponent<ScopedState>().Value = 202,
            new NetworkSpawnOptions { Scope = treeScope });
        var global = server.Spawn(dynamicBlueprint,
            entity => entity.GetComponent<ScopedState>().Value = 303);
        Pump(server, [clientA, clientB], 8);
        Assert.True(server.World!.TryGetNetworkId(villageNpc, out var villageNpcId));
        Assert.True(server.World.TryGetNetworkId(treeNpc, out var treeNpcId));
        Assert.True(server.World.TryGetNetworkId(global, out var globalId));
        Assert.True(clientA.World!.TryGetEntity(villageNpcId, out _));
        Assert.False(clientA.World.TryGetEntity(treeNpcId, out _));
        Assert.True(clientB.World!.TryGetEntity(treeNpcId, out _));
        Assert.False(clientB.World.TryGetEntity(villageNpcId, out _));
        Assert.True(clientA.World.TryGetEntity(globalId, out _));
        Assert.True(clientB.World.TryGetEntity(globalId, out _));

        server.SetOwner(villageNpc, clientA.LocalPeerId);
        server.SetPlayerEntity(clientA.LocalPeerId, villageNpc);
        villageNpc.GetComponent<ScopedState>().Value = 111;
        treeNpc.GetComponent<ScopedState>().Value = 222;
        global.GetComponent<ScopedState>().Value = 333;
        server.SendSnapshotNow();
        Pump(server, [clientA, clientB], 5);
        Assert.Equal(clientA.LocalPeerId, clientA.World.GetOwner(villageNpcId));
        Assert.True(clientA.World.TryGetPlayerEntity(clientA.LocalPeerId, out var playerA));
        Assert.Same(clientA.World.TryGetEntity(villageNpcId, out var villageOnA) ? villageOnA : null, playerA);
        Assert.False(clientB.World.TryGetPlayerEntity(clientA.LocalPeerId, out _));
        Assert.Equal(111, villageOnA!.GetComponent<ScopedState>().Value);
        Assert.True(clientB.World.TryGetEntity(treeNpcId, out var treeOnB));
        Assert.Equal(222, treeOnB!.GetComponent<ScopedState>().Value);
        Assert.True(clientA.World.TryGetEntity(globalId, out var globalOnA));
        Assert.True(clientB.World.TryGetEntity(globalId, out var globalOnB));
        Assert.Equal(333, globalOnA!.GetComponent<ScopedState>().Value);
        Assert.Equal(333, globalOnB!.GetComponent<ScopedState>().Value);

        server.Subscribe(clientA.LocalPeerId, treeScope);
        Pump(server, [clientA, clientB], 8);
        Assert.True(clientA.World.TryGetEntity(treeNpcId, out var transitionedNpc));
        Assert.Equal(222, transitionedNpc!.GetComponent<ScopedState>().Value);
        server.Unsubscribe(clientA.LocalPeerId, villageScope);
        Pump(server, [clientA, clientB], 6);
        Assert.False(clientA.TryGetScope(villageScope, out _));
        Assert.True(clientA.World.TryGetEntity(treeNpcId, out _));
        Assert.True(clientB.World.TryGetEntity(treeNpcId, out _));

        clientC.Start();
        Pump(server, [clientA, clientB, clientC], 8);
        clientC.AfterSceneAssigned(sceneC);
        server.Subscribe(clientC.LocalPeerId, treeScope);
        Pump(server, [clientA, clientB, clientC], 8);
        Assert.True(clientC.TryGetScope(treeScope, out _));
        Assert.True(clientC.World!.TryGetEntity(treeNpcId, out _));
        Assert.False(clientC.TryGetScope(villageScope, out _));
        Assert.True(clientC.World!.TryGetAuthoredEntity(
            treeScope, sharedSourceGuid, out var treeAuthoredOnC));
        Assert.True(clientC.World.TryGetNetworkId(treeAuthoredOnC, out var treeAuthoredIdOnC));
        Assert.Equal(clientB.LocalPeerId, clientC.World.GetOwner(treeAuthoredIdOnC));

        server.UnloadScope(villageScope);
        Assert.False(server.TryGetScope(villageScope, out _));
        Assert.True(server.TryGetScope(treeScope, out _));
        Assert.True(clientA.IsConnected);
        Assert.True(clientB.IsConnected);
        Assert.True(clientC.IsConnected);
    }

    [Fact]
    public void DuplicateSourceAssetUsesDistinctScopeAndRuntimeIdentities()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, CreateReplication());
        using var scene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("duplicate", sourceGuid);
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        server.AfterSceneAssigned(scene);

        var first = server.LoadScope(source.AssetName!);
        var second = server.LoadScope(source.AssetName!);
        Assert.NotEqual(first, second);
        Assert.True(server.TryGetScope(first, out var firstScope));
        Assert.True(server.TryGetScope(second, out var secondScope));
        var firstEntity = firstScope!.Content!.GetEntity(sourceGuid);
        var secondEntity = secondScope!.Content!.GetEntity(sourceGuid);
        Assert.NotEqual(firstScope.Content.InstanceId, secondScope.Content.InstanceId);
        Assert.NotEqual(firstEntity.Id, secondEntity.Id);
        Assert.True(server.World!.TryGetNetworkId(firstEntity, out var firstNetworkId));
        Assert.True(server.World.TryGetNetworkId(secondEntity, out var secondNetworkId));
        Assert.NotEqual(firstNetworkId, secondNetworkId);

        server.UnloadScope(first);
        Assert.True(Entity.IsDestroyed(firstEntity));
        Assert.False(Entity.IsDestroyed(secondEntity));
        var third = server.LoadScope(source.AssetName!);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void ScopeAndSubscriptionLimitsFailBeforeCreatingAmbiguousState()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = new NetworkSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkOptions
            {
                GameBuildId = "scope-limit-tests",
                MaxReplicationScopes = 3,
                MaxScopeSubscriptionsPerPeer = 1
            },
            new NetworkMessageRegistry(),
            CreateReplication());
        using var client = new NetworkSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkOptions
            {
                GameBuildId = "scope-limit-tests",
                MaxReplicationScopes = 3,
                MaxScopeSubscriptionsPerPeer = 1
            },
            new NetworkMessageRegistry(),
            CreateReplication());
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var firstSource = CreateScopeBlueprint("limit-a", Guid.NewGuid());
        var secondSource = CreateScopeBlueprint("limit-b", Guid.NewGuid());
        var thirdSource = CreateScopeBlueprint("limit-c", Guid.NewGuid());
        Assert.True(Resources.TryRegisterAsset(firstSource));
        Assert.True(Resources.TryRegisterAsset(secondSource));
        Assert.True(Resources.TryRegisterAsset(thirdSource));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var first = server.LoadScope(firstSource.AssetName!);
        var second = server.LoadScope(secondSource.AssetName!);
        Assert.Throws<InvalidOperationException>(() => server.LoadScope(thirdSource.AssetName!));
        server.Subscribe(client.LocalPeerId, first);
        Assert.Throws<InvalidOperationException>(() => server.Subscribe(client.LocalPeerId, second));
        Assert.True(server.IsPeerSubscribed(client.LocalPeerId, first));
        Assert.False(server.IsPeerSubscribed(client.LocalPeerId, second));
    }

    [Fact]
    public void SessionStopUnloadsNetworkManagedContentExactlyOnce()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        var server = CreateSession(NetworkRole.Host, transport, CreateReplication());
        using var scene = new ScopeTestScene();
        var source = CreateScopeBlueprint("stop-cleanup", Guid.NewGuid());
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        server.AfterSceneAssigned(scene);
        var scope = server.LoadScope(source.AssetName!);
        Assert.True(server.TryGetScope(scope, out var loaded));
        var content = loaded!.Content!;

        server.Dispose();

        Assert.False(content.IsLoaded);
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
    }

    [Fact]
    public void ScopedClientTransformIsAcceptedOnlyWhileThePeerIsScopeReady()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = new NetworkReplicationRegistry();
        var clientReplication = new NetworkReplicationRegistry();
        serverReplication.Register<NetworkTransform2D>();
        clientReplication.Register<NetworkTransform2D>();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverReplication);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientReplication);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var source = new SceneBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = $"tests/scopes/transform-scope-{Guid.NewGuid():N}.scene"
        };
        var dynamicBlueprint = new EntityBlueprint
        {
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"tests/scopes/transform-{Guid.NewGuid():N}",
            Components =
            [
                new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
                new ComponentBlueprint { Type = typeof(NetworkTransform2D).AssemblyQualifiedName! }
            ]
        };
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        var scope = server.LoadScope(source.AssetName!);
        server.Subscribe(client.LocalPeerId, scope);
        Pump(server, [client], 8);

        var authoritative = server.Spawn(dynamicBlueprint,
            new NetworkSpawnOptions { Scope = scope, Owner = client.LocalPeerId });
        authoritative.GetComponent<NetworkTransform2D>().Authority = TransformAuthority.Client;
        Pump(server, [client], 4);
        server.SendSnapshotNow();
        Pump(server, [client], 4);
        Assert.True(server.World!.TryGetNetworkId(authoritative, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var remote));
        remote!.Transform.WorldPosition2D = new Vector2(14f, -6f);
        client.SendClientTransformsNow();
        Pump(server, [client], 4);
        Assert.Equal(new Vector2(14f, -6f), authoritative.Transform.WorldPosition2D);

        server.Unsubscribe(client.LocalPeerId, scope);
        Pump(server, [client], 5);
        Assert.False(client.World.TryGetEntity(id, out _));
        var malicious = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.ClientTransform,
                client.SessionId,
                client.SceneEpoch,
                client.ServerTick,
                client.StructuralRevision),
            writer =>
            {
                writer.WriteUInt32(999);
                writer.WriteUInt32(scope.Value);
                writer.WriteUInt64(id.Value);
                writer.WriteSingle(100f);
                writer.WriteSingle(200f);
                writer.WriteSingle(0f);
                writer.WriteSingle(1f);
                writer.WriteSingle(1f);
            },
            NetworkOptions.DefaultMaxProtocolPayload);
        pair.Client.Send(pair.Client.Connection, malicious,
            NetworkDelivery.UnreliableSequenced, 3);
        Pump(server, [client], 3);
        Assert.Equal(new Vector2(14f, -6f), authoritative.Transform.WorldPosition2D);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void SynchronizedSceneTransitionInvalidatesScopesAndResetsEpochLocalAllocator()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, CreateReplication());
        var firstScene = new ScopeTestScene();
        using var secondScene = new ScopeTestScene();
        var source = CreateScopeBlueprint("scene-transition", Guid.NewGuid());
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        server.AfterSceneAssigned(firstScene);
        var firstEpoch = server.SceneEpoch;
        var oldScope = server.LoadScope(source.AssetName!);
        Assert.Equal(new NetworkReplicationScopeId(2), oldScope);

        server.BeginServerSceneChange("next");
        server.BeforeSceneUnload(firstScene);
        firstScene.Dispose();
        server.AfterSceneAssigned(secondScene);

        Assert.Equal(firstEpoch.Value + 1, server.SceneEpoch.Value);
        Assert.False(server.TryGetScope(oldScope, out _));
        var newEpochScope = server.LoadScope(source.AssetName!);
        Assert.Equal(new NetworkReplicationScopeId(2), newEpochScope);
        Assert.True(server.TryGetScope(newEpochScope, out var current));
        Assert.Equal(server.SceneEpoch, current!.SceneEpoch);
    }

    [Fact]
    public void NetworkManagedTiledScopeKeepsMapLocalAndBindsOnlyAuthoredNetworkEntities()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication());
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var mapName = $"tests/scopes/map-{Guid.NewGuid():N}";
        var map = CreateEmptyMap(mapName);
        var source = CreateScopeBlueprint("tiled-network", sourceGuid);
        source.Tiled = new TiledSceneReference { AssetName = mapName };
        Assert.True(Resources.TryRegisterAsset(map));
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var scopeId = server.LoadScope(source.AssetName!);
        server.Subscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 8);
        Assert.True(server.TryGetScope(scopeId, out var serverScope));
        Assert.True(client.TryGetScope(scopeId, out var clientScope));
        var serverMap = serverScope!.Content!.TiledMap!;
        var clientMap = clientScope!.Content!.TiledMap!;
        Assert.NotSame(serverMap, clientMap);
        Assert.NotEqual(serverMap.RootEntity.Id, clientMap.RootEntity.Id);
        Assert.False(server.World!.TryGetNetworkId(serverMap.RootEntity, out _));
        Assert.False(client.World!.TryGetNetworkId(clientMap.RootEntity, out _));
        var serverAuthored = serverScope.Content.GetEntity(sourceGuid);
        var clientAuthored = clientScope.Content.GetEntity(sourceGuid);
        Assert.True(server.World.TryGetNetworkId(serverAuthored, out var networkId));
        Assert.True(client.World.TryGetNetworkId(clientAuthored, out var clientId));
        Assert.Equal(networkId, clientId);

        var tile = new TiledTileReference("tests/tile", 7);
        serverMap.GetRuntimeTileLayer("Ground")
            .SetRuntimeOverride(new Point(0, 0), tile);
        Assert.Equal(tile, serverMap.GetRuntimeTileLayer("Ground").GetTile(0, 0));
        Assert.Null(clientMap.GetRuntimeTileLayer("Ground").GetTile(0, 0));

        server.Unsubscribe(client.LocalPeerId, scopeId);
        Pump(server, [client], 5);
        Assert.True(clientMap.IsUnloaded);
        Assert.False(serverMap.IsUnloaded);
    }

    [Fact]
    public void FailedServerScopeMaterializationRollsBackAndRetiresItsIdentity()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, CreateReplication());
        using var scene = new ScopeTestScene();
        var source = CreateScopeBlueprint("rollback", Guid.NewGuid());
        source.Entities.Add(new EntityBlueprint
        {
            Guid = Guid.NewGuid(),
            Name = "failure",
            Components =
            [
                new ComponentBlueprint { Type = typeof(ScopeFailureComponent).AssemblyQualifiedName! }
            ]
        });
        Assert.True(Resources.TryRegisterAsset(source));
        server.Start();
        server.AfterSceneAssigned(scene);
        ScopeFailureComponent.Fail = true;
        try
        {
            Assert.ThrowsAny<Exception>(() => server.LoadScope(source.AssetName!));
        }
        finally
        {
            ScopeFailureComponent.Fail = false;
        }
        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(server.World!.Records);

        var recovered = server.LoadScope(source.AssetName!);
        Assert.Equal(new NetworkReplicationScopeId(3), recovered);
        Assert.Single(scene.ContentInstances);
        Assert.Single(server.World.Records);
    }

    [Fact]
    public void ScopedDynamicSpawnRejectsAndRollsBackEntitiesCreatedOutsideItsHierarchy()
    {
        var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, CreateReplication());
        using var scene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("spawn-side-effect", sourceGuid);
        var dynamicBlueprint = CreateDynamicBlueprint();
        dynamicBlueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(OutOfHierarchySpawnComponent).AssemblyQualifiedName!
        });
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));
        server.Start();
        server.AfterSceneAssigned(scene);
        var scopeId = server.LoadScope(source.AssetName!);
        Assert.True(server.TryGetScope(scopeId, out var scope));
        var authored = scope!.Content!.GetEntity(sourceGuid);
        Assert.True(server.World!.TryGetNetworkId(authored, out var authoredId));

        var exception = Assert.Throws<InvalidOperationException>(() => server.Spawn(
            dynamicBlueprint,
            new NetworkSpawnOptions { Scope = scopeId }));

        Assert.Contains("outside its root hierarchy", exception.Message);
        Assert.True(scope.IsLoaded);
        Assert.True(server.World.TryGetEntity(authoredId, out var surviving));
        Assert.Same(authored, surviving);
        Assert.Single(scope.Content.OwnedEntities);
        Assert.Single(scene.GetAllEntities());
        Assert.Single(server.World.Records);
    }

    [Fact]
    public void ScopeAcknowledgementsRemainValidWhenThePeerProjectionAdvancesInFlight()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication());
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var firstSource = CreateScopeBlueprint("ack-race-a", Guid.NewGuid());
        var secondSource = CreateScopeBlueprint("ack-race-b", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(firstSource));
        Assert.True(Resources.TryRegisterAsset(secondSource));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        var firstScope = server.LoadScope(firstSource.AssetName!);
        var secondScope = server.LoadScope(secondSource.AssetName!);

        server.Subscribe(client.LocalPeerId, firstScope);
        server.Subscribe(client.LocalPeerId, secondScope);
        client.PollTransport();
        client.ApplyInbound();

        var firstGlobal = server.Spawn(dynamicBlueprint);
        Assert.True(server.World!.TryGetNetworkId(firstGlobal, out var firstGlobalId));
        server.PollTransport();
        server.ApplyInbound();
        var baselineCatchUp = server.Spawn(
            dynamicBlueprint,
            entity => entity.GetComponent<ScopedState>().Value = 41,
            new NetworkSpawnOptions { Scope = secondScope });
        Assert.True(server.World.TryGetNetworkId(baselineCatchUp, out var baselineCatchUpId));
        Pump(server, [client], 32);

        Assert.True(client.IsConnected);
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, firstScope));
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, secondScope));
        Assert.True(client.TryGetScope(firstScope, out var firstClientScope));
        Assert.True(firstClientScope!.IsReady);
        Assert.True(client.TryGetScope(secondScope, out var secondClientScope));
        Assert.True(secondClientScope!.IsReady);
        Assert.True(client.World!.TryGetEntity(firstGlobalId, out _));
        Assert.True(client.World.TryGetEntity(baselineCatchUpId, out var caughtUp));
        Assert.Equal(41, caughtUp!.GetComponent<ScopedState>().Value);

        server.Unsubscribe(client.LocalPeerId, firstScope);
        client.PollTransport();
        client.ApplyInbound();
        var secondGlobal = server.Spawn(dynamicBlueprint);
        Assert.True(server.World.TryGetNetworkId(secondGlobal, out var secondGlobalId));
        server.PollTransport();
        server.ApplyInbound();
        Pump(server, [client], 5);

        Assert.True(client.IsConnected);
        Assert.False(server.IsPeerSubscribed(client.LocalPeerId, firstScope));
        Assert.False(client.TryGetScope(firstScope, out _));
        Assert.True(client.TryGetScope(secondScope, out var survivingScope));
        Assert.True(survivingScope!.IsReady);
        Assert.True(client.World.TryGetEntity(secondGlobalId, out _));
    }

    [Fact]
    public void ListenHostAndRemotePeersUseScopesWithoutDuplicatingHostContent()
    {
        var hub = MultiClientInMemoryTransport.Create(2);
        using var host = CreateSession(NetworkRole.Host, hub.Server, CreateReplication());
        using var clientA = CreateSession(NetworkRole.Client, hub.Clients[0], CreateReplication());
        using var clientB = CreateSession(NetworkRole.Client, hub.Clients[1], CreateReplication());
        using var hostScene = new ScopeTestScene();
        using var sceneA = new ScopeTestScene();
        using var sceneB = new ScopeTestScene();
        var village = CreateScopeBlueprint("host-village", Guid.NewGuid());
        var tree = CreateScopeBlueprint("host-tree", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(village));
        Assert.True(Resources.TryRegisterAsset(tree));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        host.Start();
        clientA.Start();
        clientB.Start();
        Pump(host, [clientA, clientB], 8);
        host.AfterSceneAssigned(hostScene);
        clientA.AfterSceneAssigned(sceneA);
        clientB.AfterSceneAssigned(sceneB);
        var villageScope = host.LoadScope(village.AssetName!);
        var treeScope = host.LoadScope(tree.AssetName!);
        var hostContentCount = hostScene.ContentInstances.Count;

        host.Subscribe(host.LocalPeerId, villageScope);
        host.Subscribe(clientA.LocalPeerId, villageScope);
        host.Subscribe(clientB.LocalPeerId, treeScope);
        Pump(host, [clientA, clientB], 10);

        Assert.True(host.IsPeerScopeReady(host.LocalPeerId, villageScope));
        Assert.True(host.IsPeerScopeReady(clientA.LocalPeerId, villageScope));
        Assert.True(host.IsPeerScopeReady(clientB.LocalPeerId, treeScope));
        Assert.Equal(2, hostContentCount);
        Assert.Equal(hostContentCount, hostScene.ContentInstances.Count);
        Assert.Single(sceneA.ContentInstances);
        Assert.Single(sceneB.ContentInstances);
        Assert.True(clientA.TryGetScope(villageScope, out _));
        Assert.False(clientA.TryGetScope(treeScope, out _));
        Assert.True(clientB.TryGetScope(treeScope, out _));
        Assert.False(clientB.TryGetScope(villageScope, out _));

        var villageEntity = host.Spawn(
            dynamicBlueprint,
            new NetworkSpawnOptions { Scope = villageScope });
        var treeEntity = host.Spawn(
            dynamicBlueprint,
            new NetworkSpawnOptions { Scope = treeScope });
        var globalEntity = host.Spawn(dynamicBlueprint);
        Pump(host, [clientA, clientB], 8);
        Assert.True(host.World!.TryGetNetworkId(villageEntity, out var villageId));
        Assert.True(host.World.TryGetNetworkId(treeEntity, out var treeId));
        Assert.True(host.World.TryGetNetworkId(globalEntity, out var globalId));
        Assert.True(clientA.World!.TryGetEntity(villageId, out _));
        Assert.False(clientA.World.TryGetEntity(treeId, out _));
        Assert.True(clientB.World!.TryGetEntity(treeId, out _));
        Assert.False(clientB.World.TryGetEntity(villageId, out _));
        Assert.True(clientA.World.TryGetEntity(globalId, out _));
        Assert.True(clientB.World.TryGetEntity(globalId, out _));

        host.Subscribe(host.LocalPeerId, treeScope);
        host.Unsubscribe(host.LocalPeerId, villageScope);

        Assert.False(host.IsPeerSubscribed(host.LocalPeerId, villageScope));
        Assert.True(host.IsPeerScopeReady(host.LocalPeerId, treeScope));
        Assert.True(host.IsPeerScopeReady(clientA.LocalPeerId, villageScope));
        Assert.True(host.IsPeerScopeReady(clientB.LocalPeerId, treeScope));
        Assert.Equal(hostContentCount, hostScene.ContentInstances.Count);
        Assert.True(host.World.TryGetEntity(villageId, out _));
        Assert.True(host.World.TryGetEntity(treeId, out _));
        Assert.True(host.TryGetScope(villageScope, out _));
        Assert.True(host.TryGetScope(treeScope, out _));

        var laterGlobal = host.Spawn(dynamicBlueprint);
        Pump(host, [clientA, clientB], 5);
        Assert.True(host.World.TryGetNetworkId(laterGlobal, out var laterGlobalId));
        Assert.True(clientA.World.TryGetEntity(laterGlobalId, out _));
        Assert.True(clientB.World.TryGetEntity(laterGlobalId, out _));
        Assert.True(clientA.IsConnected);
        Assert.True(clientB.IsConnected);
    }

    [Fact]
    public void ClientScopeLoadDefersItsFirstFrameAndAdvancesAtMostOneItemPerFrame()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1,
            maxQueuedTransportEvents: 8);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint("incremental", sourceGuid);
        var dynamicBlueprint = CreateDynamicBlueprint();
        dynamicBlueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(ScopeActivityProbe).AssemblyQualifiedName!
        });
        dynamicBlueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(ScopeDrawableProbe).AssemblyQualifiedName!
        });
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);

        var scope = server.LoadScope(source.AssetName!);
        var dynamicIds = new List<NetworkEntityId>();
        for (var i = 0; i < 128; i++)
        {
            var entity = server.Spawn(
                dynamicBlueprint,
                item => item.GetComponent<ScopedState>().Value = i + 1,
                new NetworkSpawnOptions { Scope = scope });
            Assert.True(server.World!.TryGetNetworkId(entity, out var id));
            dynamicIds.Add(id);
        }
        Assert.True(server.World!.TryGetEntity(dynamicIds[0], out var playerEntity));
        server.World.SetPlayerEntity(client.LocalPeerId, dynamicIds[0]);

        var readyEvents = 0;
        var clientReadyStatusEvents = 0;
        server.PeerScopeReady += (peer, readyScope) =>
        {
            if (peer == client.LocalPeerId && readyScope == scope)
                readyEvents++;
        };
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Scope == scope && status.Phase == NetworkScopeLoadPhase.Ready)
                clientReadyStatusEvents++;
        };

        server.Subscribe(client.LocalPeerId, scope);
        client.PollTransport();
        client.ApplyInbound();

        Assert.True(client.TryGetScopeLoadStatus(scope, out var initial));
        Assert.Equal(NetworkScopeLoadPhase.LoadingContent, initial.Phase);
        Assert.Empty(clientScene.ContentInstances);
        Assert.False(server.IsPeerScopeReady(client.LocalPeerId, scope));
        Assert.False(client.World!.TryGetPlayerEntity(client.LocalPeerId, out _));

        var liveEntity = server.Spawn(
            dynamicBlueprint,
            item => item.GetComponent<ScopedState>().Value = 99,
            new NetworkSpawnOptions { Scope = scope });
        Assert.True(server.World.TryGetNetworkId(liveEntity, out var liveEntityId));
        client.PollTransport();
        client.ApplyInbound();
        Assert.False(client.World!.TryGetEntity(liveEntityId, out _));

        // ScopeLoad was accepted during this logical frame. The first scheduler call must
        // do no materialization so the loading cover can be drawn first.
        client.AdvanceClientScopeLoads();
        Assert.Empty(clientScene.ContentInstances);

        var observedPartialScope = false;
        for (var frame = 0; frame < 2048 && readyEvents == 0; frame++)
        {
            server.PollTransport();
            server.ApplyInbound();
            client.PollTransport();
            client.ApplyInbound();
            client.AdvanceClientScopeLoads();

            Assert.True(client.TryGetScopeLoadStatus(scope, out var status));
            Assert.InRange(status.WorkItemsAdvancedLastFrame, 0, 1);
            if (status.TotalItems is { } total)
                Assert.InRange(status.CompletedItems, 0, total);

            if (status.Phase is not NetworkScopeLoadPhase.Ready &&
                client.TryGetScope(scope, out var loadingScope))
            {
                observedPartialScope = true;
                Assert.False(loadingScope!.IsReady);
                Assert.All(
                    loadingScope.Content!.OwnedEntities,
                    entity => Assert.False(entity.Enabled));
                foreach (var id in dynamicIds)
                    Assert.False(client.World!.TryGetEntity(id, out _));
                Assert.False(client.World.TryGetPlayerEntity(client.LocalPeerId, out _));
                Assert.False(server.IsPeerScopeReady(client.LocalPeerId, scope));
                Assert.DoesNotContain(
                    clientScene.Drawables.GetAllEnabledDrawables(),
                    drawable => drawable is ScopeDrawableProbe);

                clientScene.Tick();
                clientScene.PhysicsTick();
                Assert.All(
                    clientScene.GetAllEntities()
                        .Select(entity => entity.GetComponent<ScopeActivityProbe>())
                        .Where(probe => probe is not null),
                    probe =>
                    {
                        Assert.Equal(0, probe!.UpdateCount);
                        Assert.Equal(0, probe.PhysicsCount);
                    });
            }
        }

        Assert.True(observedPartialScope);
        Assert.Equal(1, readyEvents);
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, scope));
        Assert.True(client.TryGetScopeLoadStatus(scope, out var completed));
        Assert.Equal(NetworkScopeLoadPhase.Ready, completed.Phase);
        Assert.Equal(completed.TotalItems, completed.CompletedItems);
        _output.WriteLine(
            "Scope load timing: total={0:F3}ms, max-item={1:F3}ms, loading-content={2:F3}ms, " +
            "dynamic-materialization={3:F3}ms, max-slice={4:F3}ms, max-apply-inbound={5:F3}ms.",
            completed.Elapsed.TotalMilliseconds,
            completed.MaximumWorkItemDuration.TotalMilliseconds,
            completed.LoadingContentDuration.TotalMilliseconds,
            completed.DynamicMaterializationDuration.TotalMilliseconds,
            client.MaximumClientScopeLoadSliceDuration.TotalMilliseconds,
            client.MaximumApplyInboundMilliseconds);
        Assert.All(dynamicIds, id => Assert.True(client.World!.TryGetEntity(id, out _)));
        Assert.True(client.World!.TryGetEntity(liveEntityId, out _));
        Assert.True(client.World.TryGetPlayerEntity(client.LocalPeerId, out _));

        Pump(server, [client], 8);
        Assert.Equal(1, readyEvents);
        Assert.Equal(1, clientReadyStatusEvents);
    }

    [Fact]
    public void PendingScopeCallbacksCanResolveScopeMetadataWithoutPublishingEntities()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        // Exercise the public facade used by game components without opening a graphics device.
        var core = (Core)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Core));
        using var network = new NetworkService(core);
        typeof(NetworkService).GetField("_session",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(network, client);
        var source = CreateScopeBlueprint("callback-scope-metadata", Guid.NewGuid());
        var blueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(blueprint));
        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        server.Spawn(blueprint, options: new NetworkSpawnOptions { Scope = scope });

        var stateCallbacks = 0;
        var readyCallbacks = 0;
        Entity? stagedEntity = null;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Scope != scope || status.Phase != NetworkScopeLoadPhase.ApplyingComponentStates)
                return;
            foreach (var record in client.World!.GetRecords(scope))
            {
                var state = record.Entity.GetComponent<ScopedState>()!;
                state.StateAppliedCallback = () => { AssertScopeMetadata(); stateCallbacks++; };
                state.SpawnReadyCallback = () => { AssertScopeMetadata(); readyCallbacks++; };

                void AssertScopeMetadata()
                {
                    stagedEntity = state.Entity;
                    Assert.True(network.TryGetReplicationScope(state.Entity, out var actualScope));
                    Assert.Equal(scope, actualScope);
                    Assert.True(network.TryGetScope(actualScope, out var contentScope));
                    Assert.True(contentScope!.IsLoaded);
                    Assert.NotNull(contentScope.Content);
                    Assert.False(contentScope.IsReady);
                    Assert.False(state.Entity.Enabled);
                    Assert.True(state.Entity.UpdatesSuspended);
                    Assert.False(network.TryGetNetworkId(state.Entity, out _));
                    Assert.False(client.World.TryGetEntity(record.Id, out _));
                }
            }
        };
        server.Subscribe(client.LocalPeerId, scope);
        Pump(server, [client], 128);

        Assert.True(client.TryGetScopeLoadStatus(scope, out var completed));
        Assert.True(completed.Phase == NetworkScopeLoadPhase.Ready, completed.Diagnostic);
        Assert.Equal(2, stateCallbacks); // Authored and dynamic components both initialize while staged.
        Assert.Equal(2, readyCallbacks);
        Assert.NotNull(stagedEntity);
        Assert.True(network.TryGetNetworkId(stagedEntity, out _));
        Assert.True(network.TryGetReplicationScope(stagedEntity, out var committedScope));
        Assert.Equal(scope, committedScope);
        Assert.False(network.TryGetReplicationScope(clientScene.CreateEntity("not-networked"), out _));

        server.Unsubscribe(client.LocalPeerId, scope);
        Pump(server, [client], 16);
        Assert.False(network.TryGetReplicationScope(stagedEntity, out _));
    }

    [Fact]
    public void ClientComponentStateFailureRollsBackAndNeverAcknowledgesScopeReady()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var source = CreateScopeBlueprint("component-state-failure", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        dynamicBlueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(FailingScopedState).AssemblyQualifiedName!
        });
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        server.Spawn(
            dynamicBlueprint,
            entity => entity.GetComponent<FailingScopedState>().Value = 777,
            new NetworkSpawnOptions { Scope = scope });
        var failed = false;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Scope == scope && status.Phase == NetworkScopeLoadPhase.Failed)
                failed = true;
        };

        server.Subscribe(client.LocalPeerId, scope);
        FailingScopedState.FailOnApply = true;
        try
        {
            Pump(server, [client], 128);
        }
        finally
        {
            FailingScopedState.FailOnApply = false;
        }

        Assert.True(failed);
        Assert.False(server.IsPeerScopeReady(client.LocalPeerId, scope));
        Assert.Empty(clientScene.ContentInstances);
        Assert.Empty(clientScene.GetAllEntities());
    }

    [Theory]
    [InlineData(MalformedScopeBaselineCase.DuplicateRecord)]
    [InlineData(MalformedScopeBaselineCase.IncorrectCount)]
    [InlineData(MalformedScopeBaselineCase.UnknownComponent)]
    [InlineData(MalformedScopeBaselineCase.InvalidEntityReference)]
    [InlineData(MalformedScopeBaselineCase.BadScopeIdentity)]
    [InlineData(MalformedScopeBaselineCase.TrailingPayload)]
    [InlineData(MalformedScopeBaselineCase.StaleRevision)]
    public void MalformedIncrementalScopeBaselineRollsBackWithoutScopeReady(
        MalformedScopeBaselineCase malformedCase)
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = CreateScopeBlueprint($"malformed-{malformedCase}", sourceGuid);
        Assert.True(Resources.TryRegisterAsset(source));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        var terminal = false;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Scope == scope && status.Phase is
                NetworkScopeLoadPhase.Failed or NetworkScopeLoadPhase.Cancelled)
                terminal = true;
        };

        server.Subscribe(client.LocalPeerId, scope);
        client.PollTransport();
        client.ApplyInbound();
        client.AdvanceClientScopeLoads();
        client.AdvanceClientScopeLoads();
        Assert.True(client.TryGetScopeLoadStatus(scope, out var waiting));
        Assert.Equal(NetworkScopeLoadPhase.WaitingForBaseline, waiting.Phase);

        var nextRevision = new NetworkStructuralRevision(client.StructuralRevision.Value + 1);
        switch (malformedCase)
        {
            case MalformedScopeBaselineCase.DuplicateRecord:
                Send(NetworkBaselineRecordKind.Begin, writer =>
                {
                    writer.WriteInt32(2);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteUInt32(1);
                });
                SendAuthoredRecord();
                SendAuthoredRecord();
                Send(NetworkBaselineRecordKind.End);
                break;
            case MalformedScopeBaselineCase.IncorrectCount:
                Send(NetworkBaselineRecordKind.Begin, writer =>
                {
                    writer.WriteInt32(1);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteUInt32(1);
                });
                Send(NetworkBaselineRecordKind.End);
                break;
            case MalformedScopeBaselineCase.UnknownComponent:
                Send(NetworkBaselineRecordKind.Begin, writer =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(1);
                    writer.WriteUInt32(1);
                });
                Send(NetworkBaselineRecordKind.ComponentState, writer =>
                {
                    writer.WriteUInt64(1);
                    writer.WriteUInt16(ushort.MaxValue);
                });
                break;
            case MalformedScopeBaselineCase.InvalidEntityReference:
                Send(NetworkBaselineRecordKind.Begin, writer =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(1);
                    writer.WriteInt32(0);
                    writer.WriteUInt32(1);
                });
                Send(NetworkBaselineRecordKind.PlayerEntity, writer =>
                {
                    writer.WriteUInt32(client.LocalPeerId.Value);
                    writer.WriteUInt64(999);
                });
                Send(NetworkBaselineRecordKind.End);
                break;
            case MalformedScopeBaselineCase.BadScopeIdentity:
                Send(
                    NetworkBaselineRecordKind.Begin,
                    writer =>
                    {
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteUInt32(1);
                    },
                    new NetworkReplicationScopeId(scope.Value + 100));
                break;
            case MalformedScopeBaselineCase.TrailingPayload:
                Send(NetworkBaselineRecordKind.Begin, writer =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteInt32(0);
                    writer.WriteUInt32(1);
                    writer.WriteByte(42);
                });
                break;
            case MalformedScopeBaselineCase.StaleRevision:
                Send(
                    NetworkBaselineRecordKind.Begin,
                    writer =>
                    {
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteInt32(0);
                        writer.WriteUInt32(1);
                    },
                    revision: client.StructuralRevision);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(malformedCase));
        }

        client.PollTransport();
        client.ApplyInbound();
        for (var frame = 0; frame < 128 && !terminal; frame++)
            client.AdvanceClientScopeLoads();

        Assert.True(terminal);
        Assert.False(server.IsPeerScopeReady(client.LocalPeerId, scope));
        Assert.Empty(clientScene.ContentInstances);
        Assert.Empty(clientScene.GetAllEntities());

        void SendAuthoredRecord() =>
            Send(NetworkBaselineRecordKind.AuthoredEntity, writer =>
            {
                writer.WriteGuid(sourceGuid);
                writer.WriteUInt64(500);
                writer.WriteUInt32(0);
                writer.WriteBoolean(true);
            });

        void Send(
            NetworkBaselineRecordKind kind,
            Action<NetworkWriter>? payload = null,
            NetworkReplicationScopeId? targetScope = null,
            NetworkStructuralRevision? revision = null)
        {
            var bytes = NetworkProtocol.Encode(
                new NetworkPacketHeader(
                    NetworkProtocolMessage.ScopeBaseline,
                    client.SessionId,
                    client.SceneEpoch,
                    client.ServerTick,
                    revision ?? nextRevision),
                writer =>
                {
                    writer.WriteUInt32((targetScope ?? scope).Value);
                    writer.WriteByte((byte)kind);
                    payload?.Invoke(writer);
                },
                NetworkOptions.DefaultMaxProtocolPayload);
            pair.Server.Send(
                pair.Server.Connection,
                bytes,
                NetworkDelivery.ReliableOrdered,
                0);
        }
    }

    [Fact]
    public void UnloadingScopeDuringIncrementalBaselineCancelsAndRollsBackPartialContent()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        string? disconnectDiagnostic = null;
        string? serverDisconnectDiagnostic = null;
        string? loadDiagnostic = null;
        var observedCancelled = false;
        client.PeerDisconnected += (_, _, diagnostic) =>
            disconnectDiagnostic = diagnostic;
        server.PeerDisconnected += (_, _, diagnostic) =>
            serverDisconnectDiagnostic = diagnostic;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Diagnostic is not null)
                loadDiagnostic = status.Diagnostic;
            if (status.Phase == NetworkScopeLoadPhase.Cancelled)
                observedCancelled = true;
        };
        var source = CreateScopeBlueprint("cancel-incremental", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        for (var i = 0; i < 16; i++)
            server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = scope });

        server.Subscribe(client.LocalPeerId, scope);
        for (var frame = 0; frame < 20; frame++)
        {
            server.PollTransport();
            server.ApplyInbound();
            client.PollTransport();
            client.ApplyInbound();
            client.AdvanceClientScopeLoads();
        }

        Assert.True(client.TryGetScope(scope, out var partial));
        Assert.False(partial!.IsReady);
        Assert.NotEmpty(clientScene.ContentInstances);

        server.Unsubscribe(client.LocalPeerId, scope);
        Pump(server, [client], 64);

        Assert.True(
            client.IsConnected,
            $"client: {disconnectDiagnostic}; server: {serverDisconnectDiagnostic}; " +
            $"load: {loadDiagnostic}");
        Assert.False(client.TryGetScope(scope, out _));
        Assert.Empty(clientScene.ContentInstances);
        Assert.True(observedCancelled);
        Assert.False(client.TryGetScopeLoadStatus(scope, out _));
        Assert.False(server.IsPeerSubscribed(client.LocalPeerId, scope));
    }

    [Fact]
    public void ClientMaterializationFailureRollsBackAndNeverAcknowledgesScopeReady()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var source = CreateScopeBlueprint("client-failure", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        dynamicBlueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(ScopeFailureComponent).AssemblyQualifiedName!
        });
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = scope });
        var failed = false;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Scope == scope && status.Phase == NetworkScopeLoadPhase.Failed)
                failed = true;
        };

        server.Subscribe(client.LocalPeerId, scope);
        ScopeFailureComponent.Fail = true;
        try
        {
            Pump(server, [client], 64);
        }
        finally
        {
            ScopeFailureComponent.Fail = false;
        }

        Assert.True(failed);
        Assert.False(server.IsPeerScopeReady(client.LocalPeerId, scope));
        Assert.Empty(clientScene.ContentInstances);
        Assert.Empty(clientScene.GetAllEntities());
    }

    [Fact]
    public void DisconnectDuringIncrementalLoadRemovesContentAndPendingWork()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var source = CreateScopeBlueprint("disconnect-incremental", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        for (var i = 0; i < 16; i++)
            server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = scope });
        server.Subscribe(client.LocalPeerId, scope);
        Pump(server, [client], 12);
        Assert.NotEmpty(clientScene.ContentInstances);
        Assert.True(client.HasPendingScopeLoads);

        pair.Server.Disconnect(pair.Server.Connection, TransportDisconnectReason.LocalShutdown);
        client.PollTransport();
        client.ApplyInbound();

        Assert.False(client.IsConnected);
        Assert.False(client.HasPendingScopeLoads);
        Assert.Empty(clientScene.ContentInstances);
        Assert.Empty(clientScene.GetAllEntities());
    }

    [Fact]
    public void SceneReplacementCancelsOldGenerationBeforeAssigningNewWorld()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var oldClientScene = new ScopeTestScene();
        using var newClientScene = new ScopeTestScene();
        var source = CreateScopeBlueprint("scene-replacement", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(source));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(oldClientScene);
        Pump(server, [client], 64);
        var scope = server.LoadScope(source.AssetName!);
        for (var i = 0; i < 16; i++)
            server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = scope });
        server.Subscribe(client.LocalPeerId, scope);
        Pump(server, [client], 12);
        Assert.NotEmpty(oldClientScene.ContentInstances);

        client.BeforeSceneUnload(oldClientScene);
        Assert.Empty(oldClientScene.ContentInstances);
        client.AfterSceneAssigned(newClientScene);
        for (var i = 0; i < 64; i++)
            client.AdvanceClientScopeLoads();

        Assert.False(client.HasPendingScopeLoads);
        Assert.Empty(newClientScene.ContentInstances);
        Assert.Empty(newClientScene.GetAllEntities());
        Assert.Same(newClientScene, client.World!.Scene);
    }

    [Fact]
    public void SharedOneItemBudgetAdvancesConcurrentScopesWithoutStarvation()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            CreateReplication(),
            maxScopeLoadItemsPerFrame: 1);
        using var serverScene = new ScopeTestScene();
        using var clientScene = new ScopeTestScene();
        var firstSource = CreateScopeBlueprint("fair-a", Guid.NewGuid());
        var secondSource = CreateScopeBlueprint("fair-b", Guid.NewGuid());
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(firstSource));
        Assert.True(Resources.TryRegisterAsset(secondSource));
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        client.Start();
        Pump(server, [client], 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        Pump(server, [client], 64);
        var first = server.LoadScope(firstSource.AssetName!);
        var second = server.LoadScope(secondSource.AssetName!);
        for (var i = 0; i < 8; i++)
        {
            server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = first });
            server.Spawn(dynamicBlueprint, new NetworkSpawnOptions { Scope = second });
        }
        server.Subscribe(client.LocalPeerId, first);
        server.Subscribe(client.LocalPeerId, second);

        var firstAdvanced = false;
        var secondAdvanced = false;
        for (var frame = 0; frame < 1024 &&
             (!server.IsPeerScopeReady(client.LocalPeerId, first) ||
              !server.IsPeerScopeReady(client.LocalPeerId, second)); frame++)
        {
            Pump(server, [client], 1);
            firstAdvanced |= client.TryGetScopeLoadStatus(first, out var firstStatus) &&
                             firstStatus.CompletedItems > 0;
            secondAdvanced |= client.TryGetScopeLoadStatus(second, out var secondStatus) &&
                              secondStatus.CompletedItems > 0;
        }

        Assert.True(firstAdvanced);
        Assert.True(secondAdvanced);
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, first));
        Assert.True(server.IsPeerScopeReady(client.LocalPeerId, second));
        Assert.True(client.TryGetScope(first, out var firstScope) && firstScope!.IsReady);
        Assert.True(client.TryGetScope(second, out var secondScope) && secondScope!.IsReady);
    }

    private static NetworkReplicationRegistry CreateReplication()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register<ScopedState>();
        registry.Register<FailingScopedState>();
        return registry;
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        Dreambit.Networking.Transport.INetworkTransport transport,
        NetworkReplicationRegistry replication,
        int maxScopeLoadItemsPerFrame = 32,
        int maxQueuedTransportEvents = NetworkOptions.DefaultMaxQueuedEvents) =>
        new(role, transport,
            new NetworkOptions
            {
                GameBuildId = "scope-tests",
                ClientScopeLoadBudgetMilliseconds = 1000,
                MaxQueuedTransportEvents = maxQueuedTransportEvents,
                MaxClientScopeLoadWorkItemsPerFrame = maxScopeLoadItemsPerFrame
            },
            new NetworkMessageRegistry(), replication);

    private static SceneBlueprint CreateScopeBlueprint(string label, Guid sourceGuid) => new()
    {
        AssetId = AssetId.New(),
        AssetName = $"tests/scopes/{label}-{Guid.NewGuid():N}.scene",
        Entities =
        [
            new EntityBlueprint
            {
                Guid = sourceGuid,
                Name = label,
                Components =
                [
                    new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
                    new ComponentBlueprint { Type = typeof(ScopedState).AssemblyQualifiedName! }
                ]
            }
        ]
    };

    private static EntityBlueprint CreateDynamicBlueprint() => new()
    {
        Guid = Guid.NewGuid(),
        AssetId = AssetId.New(),
        AssetName = $"tests/scopes/dynamic-{Guid.NewGuid():N}",
        Name = "scoped-dynamic",
        Components =
        [
            new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
            new ComponentBlueprint { Type = typeof(ScopedState).AssemblyQualifiedName! }
        ]
    };

    private static TmxMap CreateEmptyMap(string assetName) => new()
    {
        AssetId = AssetId.New(),
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

    private static void Pump(NetworkSession server, NetworkSession[] clients, int count)
    {
        for (var i = 0; i < count; i++)
        {
            server.PollTransport();
            foreach (var client in clients) client.PollTransport();
            server.ApplyInbound();
            foreach (var client in clients) client.ApplyInbound();
            server.AdvanceClientScopeLoads();
            foreach (var client in clients) client.AdvanceClientScopeLoads();
        }
        server.PollTransport();
        server.ApplyInbound();
        server.AdvanceClientScopeLoads();
    }

    private sealed class ScopeTestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }

    public enum MalformedScopeBaselineCase : byte
    {
        DuplicateRecord,
        IncorrectCount,
        UnknownComponent,
        InvalidEntityReference,
        BadScopeIdentity,
        TrailingPayload,
        StaleRevision
    }

    [NetworkReplicated(901)]
    public sealed class ScopedState : Component
    {
        [Replicated(1)] public int Value { get; set; }

        public Action? StateAppliedCallback { get; set; }
        public Action? SpawnReadyCallback { get; set; }

        public override void OnNetworkStateApplied(NetworkStateAppliedContext context) =>
            StateAppliedCallback?.Invoke();

        public override void OnNetworkSpawnReady(NetworkSpawnReadyContext context) =>
            SpawnReadyCallback?.Invoke();
    }

    public sealed class ScopeFailureComponent : Component
    {
        public static bool Fail { get; set; }

        public ScopeFailureComponent()
        {
            if (Fail)
                throw new InvalidOperationException("Intentional scoped materialization failure.");
        }
    }

    public sealed class ScopeActivityProbe : Component
    {
        public int UpdateCount { get; private set; }
        public int PhysicsCount { get; private set; }

        public override void OnUpdate() => UpdateCount++;

        public override void OnPhysicsUpdate() => PhysicsCount++;
    }

    public sealed class ScopeDrawableProbe : DrawableComponent
    {
    }

    [NetworkReplicated(902)]
    public sealed class FailingScopedState : Component
    {
        private int _value;

        public static bool FailOnApply { get; set; }

        [Replicated(1)]
        public int Value
        {
            get => _value;
            set
            {
                if (FailOnApply && value == 777)
                    throw new InvalidOperationException("Intentional component-state apply failure.");
                _value = value;
            }
        }
    }

    public sealed class OutOfHierarchySpawnComponent : Component
    {
        public override void OnCreated()
        {
            Scene!.CreateEntity("invalid-scoped-side-effect");
        }
    }
}
