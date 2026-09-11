using System;
using System.Collections.Generic;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkSceneServiceTests
{
    [Fact]
    public void AuthoredSceneServiceReplicatesOwnershipStateAndReloadsCleanly()
    {
        var sourceGuid = Guid.NewGuid();
        var pair = InMemoryTransport.CreatePair();

        using var server = CreateSession(
            NetworkRole.Server,
            pair.Server);

        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client);

        var firstServerScene =
            CreateScene(sourceGuid, 10);

        NetworkSceneServiceTestScene? activeClientScene = null;
        NetworkWorld? releasedClientWorld = null;
        ReplicatedSceneService? releasedClientService = null;

        client.SceneChangeRequested += (_, _) =>
        {
            if (activeClientScene is not null)
            {
                releasedClientWorld = client.World;
                releasedClientService =
                    activeClientScene.Services
                        .Get<ReplicatedSceneService>();

                client.BeforeSceneUnload(activeClientScene);
                activeClientScene.Dispose();
            }

            activeClientScene =
                CreateScene(sourceGuid, -1);

            client.AfterSceneAssigned(activeClientScene);
        };

        server.Start();
        server.BeginServerSceneChange("network-scene-service");
        server.AfterSceneAssigned(firstServerScene);
        firstServerScene.Tick();
        firstServerScene.Tick();

        var firstServerService =
            firstServerScene.Services
                .Get<ReplicatedSceneService>();

        AssertServiceRegistration(
            server.World!,
            firstServerService,
            sourceGuid,
            NetworkRole.Server,
            10);

        client.Start();

        Synchronize(
            server,
            client,
            () => activeClientScene);

        activeClientScene!.Tick();

        var firstClientService =
            activeClientScene!.Services
                .Get<ReplicatedSceneService>();

        AssertServiceRegistration(
            client.World!,
            firstClientService,
            sourceGuid,
            NetworkRole.Client,
            10);

        Assert.True(
            server.World!.TryGetNetworkId(
                firstServerService.Entity,
                out var firstNetworkId));

        Assert.True(
            client.World!.TryGetNetworkId(
                firstClientService.Entity,
                out var firstClientNetworkId));

        Assert.Equal(
            firstNetworkId,
            firstClientNetworkId);

        Assert.Equal(
            NetworkPeerId.None,
            server.World.GetOwner(firstNetworkId));

        Assert.Equal(
            NetworkPeerId.None,
            client.World.GetOwner(firstNetworkId));

        Assert.Throws<InvalidOperationException>(
            () => server.SetOwner(
                firstServerService.Entity,
                client.LocalPeerId));

        Assert.Throws<InvalidOperationException>(
            () => server.Despawn(
                firstServerService.Entity));

        Assert.True(
            server.World.TryGetNetworkId(
                firstServerService.Entity,
                out var retainedNetworkId));

        Assert.Equal(firstNetworkId, retainedNetworkId);
        Assert.Single(server.World.Records);
        Assert.True(firstServerService.Entity.Enabled);

        firstServerService.Value = 42;
        server.SendSnapshotNow();
        PumpTransport(server, client, 4);

        Assert.Equal(
            42,
            firstClientService.Value);

        Assert.Equal(
            NetworkStateApplyKind.Snapshot,
            firstClientService.AppliedContexts[^1].Kind);

        var firstServerWorld = server.World;
        var firstClientWorld = client.World;
        var firstEpoch = server.SceneEpoch;

        server.BeginServerSceneChange("network-scene-service-reload");
        server.BeforeSceneUnload(firstServerScene);
        firstServerScene.Dispose();

        using var secondServerScene =
            CreateScene(sourceGuid, 84);

        server.AfterSceneAssigned(secondServerScene);
        secondServerScene.Tick();
        secondServerScene.Tick();

        Synchronize(
            server,
            client,
            () => activeClientScene);

        activeClientScene!.Tick();

        var secondServerService =
            secondServerScene.Services
                .Get<ReplicatedSceneService>();

        var secondClientService =
            activeClientScene!.Services
                .Get<ReplicatedSceneService>();

        AssertServiceRegistration(
            server.World!,
            secondServerService,
            sourceGuid,
            NetworkRole.Server,
            84);

        AssertServiceRegistration(
            client.World!,
            secondClientService,
            sourceGuid,
            NetworkRole.Client,
            84);

        Assert.Equal(
            firstEpoch.Value + 1,
            server.SceneEpoch.Value);

        Assert.Equal(
            server.SceneEpoch,
            client.SceneEpoch);

        Assert.NotSame(
            firstServerWorld,
            server.World);

        Assert.NotSame(
            firstClientWorld,
            client.World);

        Assert.Same(
            firstClientWorld,
            releasedClientWorld);

        Assert.Same(
            firstClientService,
            releasedClientService);

        Assert.Throws<ObjectDisposedException>(
            () => firstServerWorld!.TryGetEntity(
                firstNetworkId,
                out _));

        Assert.Throws<ObjectDisposedException>(
            () => firstClientWorld!.TryGetEntity(
                firstNetworkId,
                out _));

        Assert.Equal(1, firstServerService.ServicesStoppingCount);
        Assert.Equal(1, firstServerService.ServiceDisposingCount);
        Assert.Equal(1, firstClientService.ServicesStoppingCount);
        Assert.Equal(1, firstClientService.ServiceDisposingCount);

        Assert.False(
            firstServerScene.Services
                .TryGet<ReplicatedSceneService>(out _));

        Assert.False(
            releasedClientService!.OwnerServices
                .TryGet<ReplicatedSceneService>(out _));

        Assert.True(
            server.World!.TryGetNetworkId(
                secondServerService.Entity,
                out var secondNetworkId));

        Assert.NotEqual(
            firstNetworkId,
            secondNetworkId);

        Assert.Equal(
            TransportState.Listening,
            pair.Server.State);

        Assert.Equal(
            TransportState.Connected,
            pair.Client.State);

        client.BeforeSceneUnload(activeClientScene);
        activeClientScene.Dispose();
        server.BeforeSceneUnload(secondServerScene);
    }

    private static void AssertServiceRegistration(
        NetworkWorld world,
        ReplicatedSceneService service,
        Guid sourceGuid,
        NetworkRole expectedRole,
        int expectedValue)
    {
        Assert.Equal(1, service.ServicesReadyCount);
        Assert.Equal(0, service.NetworkReadyCountAtServicesReady);
        Assert.Equal(1, service.NetworkReadyCount);
        Assert.Equal(expectedRole, service.ReadyContext!.Value.LocalRole);
        Assert.Equal(NetworkPeerId.None, service.ReadyContext.Value.Owner);
        Assert.Equal(expectedValue, service.Value);
        Assert.True(service.UpdateCount > 0);

        if (expectedRole == NetworkRole.Client)
        {
            var initialState = Assert.Single(service.AppliedContexts);
            Assert.Equal(
                NetworkStateApplyKind.InitialBaseline,
                initialState.Kind);
            Assert.Equal(902, initialState.ComponentId);
        }
        else
        {
            Assert.Empty(service.AppliedContexts);
        }

        var record = Assert.Single(world.Records);
        Assert.Same(service.Entity, record.Entity);
        Assert.Same(
            service.Entity.GetComponent<NetworkObject>(),
            record.Marker);
        Assert.Equal(sourceGuid, record.SourceGuid);
        Assert.Equal(record.Id, service.ReadyContext.Value.EntityId);

        var binding = Assert.Single(record.ReplicationBindings);
        Assert.Same(service, binding.Component);
        Assert.Equal(902, binding.Descriptor.Id);
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport)
    {
        var replication =
            new NetworkReplicationRegistry();

        replication.Register<ReplicatedSceneService>();

        return new NetworkSession(
            role,
            transport,
            new NetworkOptions
            {
                GameBuildId = "network-scene-service-tests"
            },
            new NetworkMessageRegistry(),
            replication);
    }

    private static NetworkSceneServiceTestScene CreateScene(
        Guid sourceGuid,
        int value)
    {
        var scene =
            new NetworkSceneServiceTestScene();

        var serviceHost =
            scene.CreateEntity(
                "network-services",
                guidOverride: sourceGuid);

        serviceHost.AttachComponent<NetworkObject>();

        serviceHost.AttachComponent<ReplicatedSceneService>()
            .Value = value;

        return scene;
    }

    private static void Synchronize(
        NetworkSession server,
        NetworkSession client,
        Func<NetworkSceneServiceTestScene?> getClientScene)
    {
        for (var index = 0;
             index < 20;
             index++)
        {
            PumpTransport(server, client, 1);
            getClientScene()?.Tick();

            if (getClientScene()?.State == SceneState.Running)
            {
                PumpTransport(server, client, 2);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Client Scene did not complete synchronization.");
    }

    private static void PumpTransport(
        NetworkSession server,
        NetworkSession client,
        int count)
    {
        for (var index = 0;
             index < count;
             index++)
        {
            server.PollTransport();
            client.PollTransport();
            server.ApplyInbound();
            client.ApplyInbound();
            server.AdvanceClientScopeLoads();
            client.AdvanceClientScopeLoads();
        }
    }
}

public sealed class NetworkSceneServiceTestScene : Scene
{
    internal override void InitializeInternals()
    {
    }
}

[NetworkReplicated(902)]
public sealed class ReplicatedSceneService : SceneServiceComponent
{
    [Replicated(1)]
    public int Value { get; set; }

    public int ServicesReadyCount { get; private set; }

    public int NetworkReadyCountAtServicesReady { get; private set; }

    public int NetworkReadyCount { get; private set; }

    public int UpdateCount { get; private set; }

    public int ServicesStoppingCount { get; private set; }

    public int ServiceDisposingCount { get; private set; }

    public NetworkSpawnReadyContext? ReadyContext { get; private set; }

    public List<NetworkStateAppliedContext> AppliedContexts { get; } = [];

    public SceneServiceCollection OwnerServices { get; private set; } = null!;

    public override void OnServicesReady()
    {
        ServicesReadyCount++;
        NetworkReadyCountAtServicesReady = NetworkReadyCount;
        OwnerServices = Scene.Services;
    }

    public override void OnNetworkSpawnReady(
        NetworkSpawnReadyContext context)
    {
        NetworkReadyCount++;
        ReadyContext = context;
    }

    public override void OnNetworkStateApplied(
        NetworkStateAppliedContext context)
    {
        AppliedContexts.Add(context);
    }

    public override void OnUpdate()
    {
        UpdateCount++;
    }

    public override void OnServicesStopping()
    {
        ServicesStoppingCount++;
    }

    protected override void OnServiceDisposing()
    {
        ServiceDisposingCount++;
    }
}
