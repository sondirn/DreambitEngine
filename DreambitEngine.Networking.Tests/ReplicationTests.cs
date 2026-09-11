using System;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Microsoft.Xna.Framework;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class ReplicationTests
{
    [Fact]
    public void AutomaticReplicationRoundTripsSupportedValuesAndReferences()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverRegistry = new NetworkReplicationRegistry();
        var clientRegistry = new NetworkReplicationRegistry();
        serverRegistry.Register<ReplicatedState>();
        clientRegistry.Register<ReplicatedState>();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverRegistry);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientRegistry);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint<ReplicatedState>();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);

        var serverEntity = server.Spawn(blueprint);
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        var source = serverEntity.GetComponent<ReplicatedState>();
        source.SetValues(
            93,
            12.5f,
            new Vector3(1, 2, 3),
            "ready",
            blueprint.AssetId,
            new NetworkEntityRef(server.SceneEpoch, id),
            TestMode.Active);

        server.SendSnapshotNow();
        Pump(server, client);

        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));
        var replicated = clientEntity!.GetComponent<ReplicatedState>();
        Assert.Equal(source.Health, replicated.Health);
        Assert.Equal(source.Speed, replicated.Speed);
        Assert.Equal(source.Position, replicated.Position);
        Assert.Equal(source.Label, replicated.Label);
        Assert.Equal(source.Asset, replicated.Asset);
        Assert.Equal(source.Target, replicated.Target);
        Assert.Equal(source.Mode, replicated.Mode);
        Assert.True(client.World.TryGetEntity(replicated.Target.EntityId, out var resolved));
        Assert.Same(clientEntity, resolved);
    }

    [Fact]
    public void LostStateIsHealedByTheNextFullSnapshot()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverRegistry = new NetworkReplicationRegistry();
        var clientRegistry = new NetworkReplicationRegistry();
        serverRegistry.Register<ReplicatedState>();
        clientRegistry.Register<ReplicatedState>();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverRegistry);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientRegistry);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint<ReplicatedState>();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);
        var serverEntity = server.Spawn(blueprint);
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));
        var source = serverEntity.GetComponent<ReplicatedState>();
        var target = clientEntity!.GetComponent<ReplicatedState>();
        source.SetHealth(25);
        pair.Server.DropNextUnreliableSend = true;

        server.SendSnapshotNow();
        Pump(server, client);
        Assert.NotEqual(25, target.Health);

        server.AfterFixedStep(serverScene);
        server.SendSnapshotNow();
        Pump(server, client);
        Assert.Equal(25, target.Health);
        Assert.Equal(server.ServerTick, client.ServerTick);
    }

    [Fact]
    public void SnapshotForFutureStructuralRevisionIsDiscardedSafely()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverRegistry = new NetworkReplicationRegistry();
        var clientRegistry = new NetworkReplicationRegistry();
        serverRegistry.Register<ReplicatedState>();
        clientRegistry.Register<ReplicatedState>();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverRegistry);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientRegistry);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint<ReplicatedState>();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);
        var serverEntity = server.Spawn(blueprint);
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));
        var target = clientEntity!.GetComponent<ReplicatedState>();
        var previous = target.Health;

        var packet = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Snapshot,
                server.SessionId,
                server.SceneEpoch,
                5,
                new NetworkStructuralRevision(client.StructuralRevision.Value + 1)),
            writer =>
            {
                writer.WriteUInt32(500);
                writer.WriteUInt64(id.Value);
                writer.WriteUInt16(101);
                writer.WriteInt32(0);
            },
            1024);
        pair.Client.Queue(new TransportEvent(
            TransportEventKind.Data,
            pair.Client.Connection,
            packet,
            NetworkDelivery.UnreliableSequenced,
            2));

        client.PollTransport();
        client.ApplyInbound();

        Assert.True(client.IsConnected);
        Assert.Equal(previous, target.Health);
    }

    [Fact]
    public void RegistrationRejectsDuplicateIdsAndRawEcsReferences()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register<ReplicatedState>();

        var duplicate = Assert.Throws<InvalidOperationException>(
            () => registry.Register<DuplicateComponentId>());
        var rawEntity = Assert.Throws<InvalidOperationException>(
            () => new NetworkReplicationRegistry().Register<RawEntityReference>());
        var rawComponent = Assert.Throws<InvalidOperationException>(
            () => new NetworkReplicationRegistry().Register<RawComponentReference>());

        Assert.Contains("ID 101", duplicate.Message);
        Assert.Contains("NetworkEntityRef", rawEntity.Message);
        Assert.Contains("NetworkEntityRef", rawComponent.Message);
    }

    [Fact]
    public void CustomComponentCodecRoundTripsWithoutAutomaticMembers()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register(202, 4, new CustomStateCodec());
        using var sourceScene = new TestScene();
        using var targetScene = new TestScene();
        var sourceEntity = sourceScene.CreateEntity();
        var targetEntity = targetScene.CreateEntity();
        var source = sourceEntity.AttachComponent<CustomState>();
        var target = targetEntity.AttachComponent<CustomState>();
        source.Value = 808;

        var sourceBinding = Assert.Single(registry.CreateBindings(sourceEntity));
        var targetBinding = Assert.Single(registry.CreateBindings(targetEntity));
        targetBinding.Apply(sourceBinding.Capture());

        Assert.Equal(808, target.Value);
    }

    [Fact]
    public void SchemaHashIsStableAcrossRegistrationOrder()
    {
        var first = new NetworkReplicationRegistry();
        var second = new NetworkReplicationRegistry();
        first.Register<ReplicatedState>();
        first.Register(202, 4, new CustomStateCodec());
        second.Register(202, 4, new CustomStateCodec());
        second.Register<ReplicatedState>();

        Assert.Equal(first.SchemaHash, second.SchemaHash);
    }

    private static EntityBlueprint CreateBlueprint<T>() where T : Component =>
        new()
        {
            Name = "replicated",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/replicated-{Guid.NewGuid():N}",
            Components =
            [
                new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
                new ComponentBlueprint { Type = typeof(T).AssemblyQualifiedName! }
            ]
        };

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkReplicationRegistry replication) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "replication-tests" },
            new NetworkMessageRegistry(),
            replication);

    private static void ConnectAndAssign(
        NetworkSession server,
        NetworkSession client,
        Scene serverScene,
        Scene clientScene)
    {
        server.Start();
        client.Start();
        Pump(server, client);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
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

    private sealed class TestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }

    public enum TestMode : short
    {
        Idle,
        Active
    }

    [NetworkReplicated(101)]
    public sealed class ReplicatedState : Component
    {
        [Replicated(1)] public int Health { get; private set; }
        [Replicated(2)] public float Speed;
        [Replicated(3)] public Vector3 Position { get; private set; }
        [Replicated(4, MaxLength = 32)] public string Label { get; private set; } = string.Empty;
        [Replicated(5)] public AssetId Asset { get; private set; }
        [Replicated(6)] public NetworkEntityRef Target { get; private set; }
        [Replicated(7)] public TestMode Mode { get; private set; }

        public void SetHealth(int value) => Health = value;

        public void SetValues(
            int health,
            float speed,
            Vector3 position,
            string label,
            AssetId asset,
            NetworkEntityRef target,
            TestMode mode)
        {
            Health = health;
            Speed = speed;
            Position = position;
            Label = label;
            Asset = asset;
            Target = target;
            Mode = mode;
        }
    }

    [NetworkReplicated(101)]
    public sealed class DuplicateComponentId : Component
    {
        [Replicated(1)] public int Value;
    }

    [NetworkReplicated(102)]
    public sealed class RawEntityReference : Component
    {
        [Replicated(1)] public Entity Value { get; set; } = null!;
    }

    [NetworkReplicated(103)]
    public sealed class RawComponentReference : Component
    {
        [Replicated(1)] public Component Value { get; set; } = null!;
    }

    public sealed class CustomState : Component
    {
        public int Value { get; set; }
    }

    private sealed class CustomStateCodec : INetworkComponentCodec<CustomState>
    {
        public void Write(NetworkWriter writer, CustomState component) =>
            writer.WriteInt32(component.Value);

        public void Read(ref NetworkReader reader, CustomState component) =>
            component.Value = reader.ReadInt32();
    }
}
