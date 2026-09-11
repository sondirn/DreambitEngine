using System;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class InitialSynchronizationTests
{
    [Fact]
    public void LateJoinReconstructsAuthoredDynamicStateOwnershipAndLiveMutationBeforeBegin()
    {
        var sourceGuid = Guid.NewGuid();
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = CreateReplication();
        var clientReplication = CreateReplication();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverReplication);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientReplication);
        using var serverScene = CreateScene(sourceGuid, 10);
        string? connectionDiagnostic = null;
        client.ConnectionFailed += (_, diagnostic) => connectionDiagnostic = diagnostic;
        client.PeerDisconnected += (_, _, diagnostic) => connectionDiagnostic ??= diagnostic;
        client.ScopeLoadStatusChanged += status =>
        {
            if (status.Diagnostic is not null)
                connectionDiagnostic = status.Diagnostic;
        };
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));

        server.Start();
        server.BeginServerSceneChange("arena");
        server.AfterSceneAssigned(serverScene);
        serverScene.Tick();
        var firstDynamic = server.Spawn(
            dynamicBlueprint,
            new NetworkSpawnOptions { Position = new(4, 5, 0) });
        firstDynamic.GetComponent<SyncState>().Value = 50;

        TestScene? clientScene = null;
        client.SceneChangeRequested += (key, epoch) =>
        {
            Assert.Equal("arena", key);
            Assert.Equal(server.SceneEpoch, epoch);
            clientScene = CreateScene(sourceGuid, -1);
            client.AfterSceneAssigned(clientScene);
        };
        client.Start();
        PumpTransport(server, client, 8);
        Assert.NotNull(clientScene);
        Assert.True(client.IsConnected);
        Assert.Equal(SceneState.Created, clientScene!.State);

        server.SetPlayerEntity(client.LocalPeerId, firstDynamic);
        server.SetOwner(firstDynamic, client.LocalPeerId);
        clientScene.Tick();
        Assert.Equal(SceneState.Starting, clientScene.State);
        Assert.Equal(0, clientScene.BeginCount);

        // The server captures the baseline here. A spawn immediately afterward must be ordered
        // after the baseline and still arrive before the client declares the Scene ready.
        server.PollTransport();
        server.ApplyInbound();
        var mutationDuringBaseline = server.Spawn(dynamicBlueprint);
        mutationDuringBaseline.GetComponent<SyncState>().Value = 75;
        server.SendSnapshotNow();

        client.PollTransport();
        client.ApplyInbound();
        Assert.Equal(SceneState.Starting, clientScene.State);
        Assert.Equal(0, clientScene.BeginCount);
        clientScene.Tick();

        PumpTransport(server, client, 3);
        Assert.Equal(1, server.ReadyPeerCount);
        server.SendSnapshotNow();
        PumpTransport(server, client, 32);
        clientScene.Tick();
        clientScene.Tick();

        Assert.True(client.IsConnected, connectionDiagnostic);
        Assert.Equal(SceneState.Running, clientScene.State);
        Assert.Equal(1, clientScene.BeginCount);
        Assert.True(server.World!.TryGetNetworkId(firstDynamic, out var firstId));
        Assert.True(server.World.TryGetNetworkId(mutationDuringBaseline, out var mutationId));
        Assert.True(client.World!.TryGetEntity(firstId, out var clientDynamic));
        Assert.True(client.World.TryGetEntity(mutationId, out var clientMutation));
        Assert.NotEqual(firstDynamic.Id, clientDynamic!.Id);
        Assert.Equal(50, clientDynamic.GetComponent<SyncState>().Value);
        Assert.Equal(75, clientMutation!.GetComponent<SyncState>().Value);
        Assert.Equal(new Microsoft.Xna.Framework.Vector3(4, 5, 0), clientDynamic.Transform.Position);
        Assert.Equal(client.LocalPeerId, client.World.GetOwner(firstId));
        Assert.True(client.World.TryGetPlayerEntity(client.LocalPeerId, out var localPlayer));
        Assert.Same(clientDynamic, localPlayer);

        var serverAuthored = serverScene.FindEntity(sourceGuid)!;
        var clientAuthored = clientScene.FindEntity(sourceGuid)!;
        Assert.True(server.World.TryGetNetworkId(serverAuthored, out var authoredId));
        Assert.True(client.World.TryGetNetworkId(clientAuthored, out var clientAuthoredId));
        Assert.Equal(authoredId, clientAuthoredId);
        Assert.Equal(10, clientAuthored.GetComponent<SyncState>().Value);
    }

    [Fact]
    public void SessionAndTransportSurviveSceneTransitionAndIgnoreOldEpochPackets()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = CreateReplication();
        var clientReplication = CreateReplication();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverReplication);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientReplication);
        var firstSourceGuid = Guid.NewGuid();
        var secondSourceGuid = Guid.NewGuid();
        using var firstServerScene = CreateScene(firstSourceGuid, 1);
        var dynamicBlueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(dynamicBlueprint));
        TestScene? activeClientScene = null;
        NetworkWorld? releasedWorld = null;
        client.SceneChangeRequested += (key, _) =>
        {
            if (activeClientScene is not null)
            {
                releasedWorld = client.World;
                client.BeforeSceneUnload(activeClientScene);
                activeClientScene.Dispose();
            }
            activeClientScene = CreateScene(
                key == "first" ? firstSourceGuid : secondSourceGuid,
                key == "first" ? -1 : 2);
            client.AfterSceneAssigned(activeClientScene);
        };

        server.Start();
        server.BeginServerSceneChange("first");
        server.AfterSceneAssigned(firstServerScene);
        firstServerScene.Tick();
        client.Start();
        Synchronize(server, client, () => activeClientScene);
        var sessionId = client.SessionId;
        var oldEpoch = client.SceneEpoch;
        var oldClientWorld = client.World!;

        server.BeginServerSceneChange("second");
        server.BeforeSceneUnload(firstServerScene);
        using var secondServerScene = CreateScene(secondSourceGuid, 2);
        server.AfterSceneAssigned(secondServerScene);
        secondServerScene.Tick();
        Synchronize(server, client, () => activeClientScene);

        Assert.Equal(sessionId, client.SessionId);
        Assert.Equal(oldEpoch.Value + 1, client.SceneEpoch.Value);
        Assert.NotSame(oldClientWorld, client.World);
        Assert.Same(oldClientWorld, releasedWorld);
        Assert.Throws<ObjectDisposedException>(
            () => oldClientWorld.TryGetEntity(new NetworkEntityId(1), out _));
        Assert.Equal(TransportState.Listening, pair.Server.State);
        Assert.Equal(TransportState.Connected, pair.Client.State);

        var stalePacket = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Despawn,
                sessionId,
                oldEpoch,
                0,
                new NetworkStructuralRevision(999)),
            writer => writer.WriteUInt64(1),
            128);
        pair.Client.Queue(new TransportEvent(
            TransportEventKind.Data,
            pair.Client.Connection,
            stalePacket,
            NetworkDelivery.ReliableOrdered,
            0));
        client.PollTransport();
        client.ApplyInbound();

        Assert.True(client.IsConnected);
        Assert.Equal(SceneState.Running, activeClientScene!.State);
    }

    [Fact]
    public void DisconnectDestroysOwnedEntitiesAndClearsRetainedOwnership()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkReplicationRegistry());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkReplicationRegistry());
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateDynamicBlueprint(includeState: false);
        Assert.True(Resources.TryRegisterAsset(blueprint));
        server.Start();
        client.Start();
        PumpTransport(server, client, 8);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        var destroyed = server.Spawn(
            blueprint,
            new NetworkSpawnOptions { Owner = client.LocalPeerId, DestroyWithOwner = true });
        var retained = server.Spawn(
            blueprint,
            new NetworkSpawnOptions { Owner = client.LocalPeerId, DestroyWithOwner = false });
        Assert.True(server.World!.TryGetNetworkId(retained, out var retainedId));
        server.SetPlayerEntity(client.LocalPeerId, retained);

        pair.Client.Disconnect(pair.Client.Connection);
        PumpTransport(server, client, 8);

        Assert.True(Entity.IsDestroyed(destroyed));
        Assert.False(Entity.IsDestroyed(retained));
        Assert.Equal(NetworkPeerId.None, server.World.GetOwner(retainedId));
        Assert.False(server.World.TryGetPlayerEntity(client.LocalPeerId, out _));
    }

    [Fact]
    public void StoppingClientDuringInitialSyncReleasesSceneStartupGate()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkReplicationRegistry());
        var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkReplicationRegistry());
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        client.SceneChangeRequested += (_, _) => client.AfterSceneAssigned(clientScene);

        server.Start();
        server.BeginServerSceneChange("startup-stop");
        server.AfterSceneAssigned(serverScene);
        serverScene.Tick();
        client.Start();
        PumpTransport(server, client, 8);
        clientScene.Tick();
        Assert.Equal(SceneState.Starting, clientScene.State);

        client.Dispose();
        clientScene.Tick();

        Assert.Equal(SceneState.Running, clientScene.State);
        Assert.Equal(1, clientScene.BeginCount);
    }

    private static NetworkReplicationRegistry CreateReplication()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register<SyncState>();
        return registry;
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkReplicationRegistry replication) =>
        new(
            role,
            transport,
            new NetworkOptions
            {
                GameBuildId = "synchronization-tests",
                ClientScopeLoadBudgetMilliseconds = 1000
            },
            new NetworkMessageRegistry(),
            replication);

    private static TestScene CreateScene(Guid sourceGuid, int state)
    {
        var scene = new TestScene();
        var authored = scene.CreateEntity("authored", guidOverride: sourceGuid);
        authored.AttachComponent<NetworkObject>();
        authored.AttachComponent<SyncState>().Value = state;
        return scene;
    }

    private static EntityBlueprint CreateDynamicBlueprint(bool includeState = true)
    {
        var components = new System.Collections.Generic.List<ComponentBlueprint>
        {
            new() { Type = typeof(NetworkObject).AssemblyQualifiedName! }
        };
        if (includeState)
            components.Add(new ComponentBlueprint { Type = typeof(SyncState).AssemblyQualifiedName! });
        return new EntityBlueprint
        {
            Name = "dynamic-sync",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/dynamic-sync-{Guid.NewGuid():N}",
            Components = components
        };
    }

    private static void Synchronize(
        NetworkSession server,
        NetworkSession client,
        Func<TestScene?> getClientScene)
    {
        for (var index = 0; index < 20; index++)
        {
            PumpTransport(server, client, 1);
            getClientScene()?.Tick();
            if (getClientScene()?.State == SceneState.Running)
            {
                PumpTransport(server, client, 2);
                return;
            }
        }
        throw new Xunit.Sdk.XunitException("Client Scene did not complete synchronization.");
    }

    private static void PumpTransport(NetworkSession server, NetworkSession client, int count)
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

    private sealed class TestScene : Scene
    {
        public int BeginCount { get; private set; }
        internal override void InitializeInternals()
        {
        }
        protected override void OnBegin() => BeginCount++;
    }

    [NetworkReplicated(301)]
    public sealed class SyncState : Component
    {
        [Replicated(1)] public int Value { get; set; }
    }
}
