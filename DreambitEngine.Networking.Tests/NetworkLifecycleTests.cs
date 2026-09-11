using System;
using System.Collections.Generic;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkLifecycleTests
{
    [Fact]
    public void LiveSpawnNotifiesReadyAfterInitialStateAndLaterSnapshotsNotifyStateApplied()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication());
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));

        server.Start();
        client.Start();
        Pump(server, client);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var serverEntity = server.Spawn(
            blueprint,
            entity => entity.GetComponent<LifecycleState>().Value = 42);
        var serverState = serverEntity.GetComponent<LifecycleState>();
        var serverProbe = Assert.Single(serverEntity.Children).GetComponent<LifecycleProbe>();

        Assert.Equal(0, serverState.ValueDuringCreated);
        Assert.Equal(42, serverState.ValueDuringReady);
        Assert.Equal(1, serverState.ReadyCount);
        Assert.Empty(serverState.Applied);
        Assert.Equal(["created", "ready"], serverState.Events);
        Assert.Equal(42, serverProbe.ValueDuringReady);
        Assert.Equal(NetworkRole.Server, serverProbe.ReadyContext.LocalRole);

        Pump(server, client);

        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var entityId));
        Assert.True(client.World!.TryGetEntity(entityId, out var clientEntity));
        var clientState = clientEntity!.GetComponent<LifecycleState>();
        var clientProbe = Assert.Single(clientEntity.Children).GetComponent<LifecycleProbe>();

        Assert.Equal(0, clientState.ValueDuringCreated);
        Assert.Equal(42, clientState.ValueDuringReady);
        Assert.Equal(1, clientState.ReadyCount);
        var initial = Assert.Single(clientState.Applied);
        Assert.Equal(NetworkStateApplyKind.InitialSpawn, initial.Context.Kind);
        Assert.Equal(401, initial.Context.ComponentId);
        Assert.True(initial.Context.IsInitial);
        Assert.Equal(42, initial.Value);
        Assert.Equal(["created", "state:InitialSpawn", "ready"], clientState.Events);
        Assert.Equal(42, clientProbe.ValueDuringReady);
        Assert.Equal(NetworkRole.Client, clientProbe.ReadyContext.LocalRole);
        Assert.Equal(entityId, clientProbe.ReadyContext.EntityId);

        serverState.Value = 99;
        server.SendSnapshotNow();
        Assert.Empty(serverState.Applied);
        Pump(server, client);

        Assert.Equal(99, clientState.Value);
        Assert.Equal(2, clientState.Applied.Count);
        Assert.Equal(NetworkStateApplyKind.Snapshot, clientState.Applied[1].Context.Kind);
        Assert.False(clientState.Applied[1].Context.IsInitial);
        Assert.Equal(99, clientState.Applied[1].Value);
        Assert.Equal(1, clientState.ReadyCount);
        Assert.Equal(1, clientProbe.ReadyCount);
    }

    [Fact]
    public void ListenHostDoesNotApplyOutboundStateAndEachRemoteClientAppliesItOnce()
    {
        var hub = MultiClientInMemoryTransport.Create(2);
        using var host = CreateSession(NetworkRole.Host, hub.Server, CreateReplication());
        using var clientA = CreateSession(NetworkRole.Client, hub.Clients[0], CreateReplication());
        using var clientB = CreateSession(NetworkRole.Client, hub.Clients[1], CreateReplication());
        using var hostScene = new TestScene();
        using var clientSceneA = new TestScene();
        using var clientSceneB = new TestScene();
        var blueprint = CreateDynamicBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));

        host.Start();
        clientA.Start();
        clientB.Start();
        Pump(host, [clientA, clientB]);
        host.AfterSceneAssigned(hostScene);
        clientA.AfterSceneAssigned(clientSceneA);
        clientB.AfterSceneAssigned(clientSceneB);

        var hostEntity = host.Spawn(
            blueprint,
            entity => entity.GetComponent<LifecycleState>().Value = 42);
        var hostState = hostEntity.GetComponent<LifecycleState>();
        Assert.Empty(hostState.Applied);

        Pump(host, [clientA, clientB]);

        Assert.True(host.World!.TryGetNetworkId(hostEntity, out var entityId));
        Assert.True(clientA.World!.TryGetEntity(entityId, out var entityA));
        Assert.True(clientB.World!.TryGetEntity(entityId, out var entityB));
        var stateA = entityA!.GetComponent<LifecycleState>();
        var stateB = entityB!.GetComponent<LifecycleState>();

        Assert.Equal(NetworkStateApplyKind.InitialSpawn, Assert.Single(stateA.Applied).Context.Kind);
        Assert.Equal(NetworkStateApplyKind.InitialSpawn, Assert.Single(stateB.Applied).Context.Kind);

        hostState.Value = 99;
        host.SendSnapshotNow();
        Assert.Empty(hostState.Applied);
        Pump(host, [clientA, clientB]);

        Assert.Equal(99, stateA.Value);
        Assert.Equal(99, stateB.Value);
        Assert.Single(stateA.Applied, applied => applied.Context.Kind == NetworkStateApplyKind.Snapshot);
        Assert.Single(stateB.Applied, applied => applied.Context.Kind == NetworkStateApplyKind.Snapshot);
    }

    [Fact]
    public void ListenHostDoesNotApplyItsAuthoritativeInitialBaseline()
    {
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var host = CreateSession(NetworkRole.Host, transport, CreateReplication());
        var sourceGuid = Guid.NewGuid();
        using var scene = CreateAuthoredScene(sourceGuid, 73);

        host.Start();
        host.AfterSceneAssigned(scene);
        scene.Tick();

        var state = scene.FindEntity(sourceGuid)!.GetComponent<LifecycleState>();
        Assert.Empty(state.Applied);
        Assert.Equal(1, state.ReadyCount);
    }

    [Fact]
    public void BaselineNotifiesAuthoredHierarchyAfterAllInitialStateIsApplied()
    {
        var sourceGuid = Guid.NewGuid();
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, CreateReplication());
        using var client = CreateSession(NetworkRole.Client, pair.Client, CreateReplication());
        using var serverScene = CreateAuthoredScene(sourceGuid, 73);
        TestScene? clientScene = null;
        client.SceneChangeRequested += (_, _) =>
        {
            clientScene = CreateAuthoredScene(sourceGuid, -1);
            client.AfterSceneAssigned(clientScene);
        };

        server.Start();
        server.BeginServerSceneChange("network-lifecycle");
        server.AfterSceneAssigned(serverScene);
        serverScene.Tick();

        var serverState = serverScene.FindEntity(sourceGuid)!.GetComponent<LifecycleState>();
        Assert.Equal(73, serverState.ValueDuringReady);
        Assert.Equal(1, serverState.ReadyCount);
        Assert.Empty(serverState.Applied);

        client.Start();
        Synchronize(server, client, () => clientScene);

        var clientRoot = clientScene!.FindEntity(sourceGuid)!;
        var clientState = clientRoot.GetComponent<LifecycleState>();
        var clientProbe = Assert.Single(clientRoot.Children).GetComponent<LifecycleProbe>();
        var applied = Assert.Single(clientState.Applied);

        Assert.Equal(73, clientState.Value);
        Assert.Equal(NetworkStateApplyKind.InitialBaseline, applied.Context.Kind);
        Assert.True(applied.Context.IsInitial);
        Assert.Equal(73, clientState.ValueDuringReady);
        Assert.Equal(73, clientProbe.ValueDuringReady);
        Assert.Equal(1, clientState.ReadyCount);
        Assert.Equal(1, clientProbe.ReadyCount);
        Assert.Equal(SceneState.Running, clientScene.State);
    }

    private static NetworkReplicationRegistry CreateReplication()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register<LifecycleState>();
        return registry;
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkReplicationRegistry replication) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "network-lifecycle-tests" },
            new NetworkMessageRegistry(),
            replication);

    private static EntityBlueprint CreateDynamicBlueprint() =>
        new()
        {
            Name = "network-lifecycle",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/network-lifecycle-{Guid.NewGuid():N}",
            Components =
            [
                new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
                new ComponentBlueprint { Type = typeof(LifecycleState).AssemblyQualifiedName! }
            ],
            Children =
            [
                new EntityBlueprint
                {
                    Name = "presentation",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = typeof(LifecycleProbe).AssemblyQualifiedName!
                        }
                    ]
                }
            ]
        };

    private static TestScene CreateAuthoredScene(Guid sourceGuid, int value)
    {
        var scene = new TestScene();
        var root = scene.CreateEntity("authored", guidOverride: sourceGuid);
        root.AttachComponent<NetworkObject>();
        root.AttachComponent<LifecycleState>().Value = value;
        var child = scene.CreateEntity("presentation");
        child.Parent = root;
        child.AttachComponent<LifecycleProbe>();
        return scene;
    }

    private static void Synchronize(
        NetworkSession server,
        NetworkSession client,
        Func<TestScene?> getClientScene)
    {
        for (var index = 0; index < 20; index++)
        {
            Pump(server, client, 1);
            getClientScene()?.Tick();
            if (getClientScene()?.State == SceneState.Running)
                return;
        }

        throw new Xunit.Sdk.XunitException("Client Scene did not complete synchronization.");
    }

    private static void Pump(NetworkSession server, NetworkSession client, int count = 8)
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

    private static void Pump(
        NetworkSession server,
        IReadOnlyList<NetworkSession> clients,
        int count = 8)
    {
        for (var index = 0; index < count; index++)
        {
            server.PollTransport();
            foreach (var client in clients)
                client.PollTransport();
            server.ApplyInbound();
            foreach (var client in clients)
                client.ApplyInbound();
            server.AdvanceClientScopeLoads();
            foreach (var client in clients)
                client.AdvanceClientScopeLoads();
        }
    }

    private sealed class TestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }

    [NetworkReplicated(401)]
    public sealed class LifecycleState : Component
    {
        [Replicated(1)] public int Value { get; set; }

        public int ValueDuringCreated { get; private set; }
        public int ValueDuringReady { get; private set; }
        public int ReadyCount { get; private set; }
        public List<(NetworkStateAppliedContext Context, int Value)> Applied { get; } = [];
        public List<string> Events { get; } = [];

        public override void OnCreated()
        {
            ValueDuringCreated = Value;
            Events.Add("created");
        }

        public override void OnNetworkStateApplied(NetworkStateAppliedContext context)
        {
            Applied.Add((context, Value));
            Events.Add($"state:{context.Kind}");
        }

        public override void OnNetworkSpawnReady(NetworkSpawnReadyContext context)
        {
            ValueDuringReady = Value;
            ReadyCount++;
            Events.Add("ready");
        }
    }

    public sealed class LifecycleProbe : Component
    {
        public int ReadyCount { get; private set; }
        public int ValueDuringReady { get; private set; }
        public NetworkSpawnReadyContext ReadyContext { get; private set; }

        public override void OnNetworkSpawnReady(NetworkSpawnReadyContext context)
        {
            ReadyContext = context;
            ReadyCount++;
            ValueDuringReady = Entity.Parent!.GetComponent<LifecycleState>().Value;
        }
    }
}
