using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Direct;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Scenes;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkingHardeningTests
{
    [Fact]
    public void LiveSpawnDoesNotUpdateBeforeInitialStateCommit()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = CreateInitialStateReplication();
        var clientReplication = CreateInitialStateReplication();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverReplication);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientReplication);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateInitialStateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));

        server.Start();
        client.Start();
        Pump(server, client);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        serverScene.Tick();
        clientScene.Tick();
        pair.Client.MaxEventsPerPollWindow = 1;

        var serverEntity = server.Spawn(blueprint);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));

        PollOnce(client); // Spawn only.
        Assert.True(client.World!.TryGetEntity(id, out var remote));
        var state = remote!.GetComponent<InitialStateProbe>();
        var childProbe = remote.Children[0].GetComponent<AlwaysUpdateChildProbe>();
        Assert.True(remote.Enabled);
        Assert.True(remote.UpdatesSuspended);
        clientScene.Tick();
        clientScene.PhysicsTick();
        Assert.Equal(0, state.UpdateCount);
        Assert.Equal(0, state.PhysicsCount);
        Assert.Equal(0, childProbe.UpdateCount);

        PollOnce(client); // Initial reliable Component state, still not committed.
        Assert.Equal(123, state.Value);
        Assert.True(remote.UpdatesSuspended);
        clientScene.Tick();
        clientScene.PhysicsTick();
        Assert.Equal(0, state.UpdateCount);
        Assert.Equal(0, state.PhysicsCount);
        Assert.Equal(0, childProbe.UpdateCount);

        PollOnce(client); // SpawnReady commits the entity.
        Assert.True(remote.Enabled);
        Assert.False(remote.UpdatesSuspended);
        clientScene.Tick();
        clientScene.PhysicsTick();
        Assert.Equal(1, state.UpdateCount);
        Assert.Equal(1, state.PhysicsCount);
        Assert.Equal(1, childProbe.UpdateCount);
        Assert.Equal(123, state.ValueObservedOnFirstUpdate);
    }

    [Fact]
    public void LiveSpawnRestoresBlueprintAndExplicitEnabledSemantics()
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
        var authoredDisabled = CreateNetworkBlueprint();
        authoredDisabled.Enabled = false;
        authoredDisabled.Children.Add(new EntityBlueprint
        {
            Name = "disabled-child",
            Guid = Guid.NewGuid(),
            Enabled = false
        });
        var explicitlyEnabled = CreateNetworkBlueprint();
        explicitlyEnabled.Enabled = false;
        Assert.True(Resources.TryRegisterAsset(authoredDisabled));
        Assert.True(Resources.TryRegisterAsset(explicitlyEnabled));

        server.Start();
        client.Start();
        Pump(server, client);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);
        var first = server.Spawn(authoredDisabled);
        var second = server.Spawn(
            explicitlyEnabled,
            new NetworkSpawnOptions { Enabled = true });
        Pump(server, client);

        Assert.True(server.World!.TryGetNetworkId(first, out var firstId));
        Assert.True(server.World.TryGetNetworkId(second, out var secondId));
        Assert.True(client.World!.TryGetEntity(firstId, out var remoteDisabled));
        Assert.True(client.World.TryGetEntity(secondId, out var remoteEnabled));
        Assert.False(remoteDisabled!.LocallyEnabled);
        Assert.False(remoteDisabled.UpdatesSuspended);
        Assert.False(remoteDisabled.Children[0].LocallyEnabled);
        Assert.True(remoteEnabled!.LocallyEnabled);
        Assert.False(remoteEnabled.UpdatesSuspended);
    }

    [Fact]
    public void DynamicSpawnRollsBackWhenMarkerIsServerOnly()
    {
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, new NetworkReplicationRegistry());
        using var scene = new TestScene();
        server.Start();
        server.AfterSceneAssigned(scene);
        var blueprint = CreateNetworkBlueprint(NetworkPresence.ServerOnly);

        var exception = Assert.Throws<InvalidOperationException>(() => server.Spawn(blueprint));

        Assert.Contains("Replicated presence", exception.Message);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(server.World!.Records);
        Assert.Equal(NetworkStructuralRevision.None, server.StructuralRevision);
    }

    [Fact]
    public void DynamicSpawnRollsBackWhenReplicatedComponentIsOnChild()
    {
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        var replication = CreateInitialStateReplication();
        using var server = CreateSession(NetworkRole.Host, transport, replication);
        using var scene = new TestScene();
        server.Start();
        server.AfterSceneAssigned(scene);
        var blueprint = CreateNetworkBlueprint();
        blueprint.Children.Add(new EntityBlueprint
        {
            Name = "replicated-child",
            Guid = Guid.NewGuid(),
            Components =
            [
                new ComponentBlueprint { Type = typeof(InitialStateProbe).AssemblyQualifiedName! }
            ]
        });

        var exception = Assert.Throws<InvalidOperationException>(() => server.Spawn(blueprint));

        Assert.Contains("network root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replicated-child", exception.Message);
        Assert.Contains(typeof(InitialStateProbe).FullName!, exception.Message);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(server.World!.Records);
        Assert.Equal(NetworkStructuralRevision.None, server.StructuralRevision);
    }

    [Fact]
    public void AuthoredNetworkRootRejectsReplicatedComponentOnDescendant()
    {
        var replication = CreateInitialStateReplication();
        using var scene = new TestScene();
        var root = scene.CreateEntity("authored-root");
        root.AttachComponent<NetworkObject>();
        var child = scene.CreateEntity("authored-child");
        child.Parent = root;
        child.AttachComponent<InitialStateProbe>();
        using var world = new NetworkWorld(
            scene,
            new NetworkSceneEpoch(1),
            true,
            replication);

        var exception = Assert.Throws<InvalidOperationException>(
            () => world.BindServerAuthoredEntities(() => new NetworkEntityId(1)));

        Assert.Contains("authored-root", exception.Message);
        Assert.Contains("authored-child", exception.Message);
        Assert.Contains(typeof(InitialStateProbe).FullName!, exception.Message);
        Assert.Empty(world.Records);
    }

    [Fact]
    public void NetworkEntityLimitRejectsSpawnWithoutChangingWorldOrRevision()
    {
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = new NetworkSession(
            NetworkRole.Host,
            transport,
            new NetworkOptions { GameBuildId = "entity-cap", MaxNetworkEntities = 1 },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        using var scene = new TestScene();
        server.Start();
        server.AfterSceneAssigned(scene);
        var firstBlueprint = CreateNetworkBlueprint();
        var secondBlueprint = CreateNetworkBlueprint();

        var first = server.Spawn(firstBlueprint);
        var revision = server.StructuralRevision;
        var exception = Assert.Throws<InvalidOperationException>(() => server.Spawn(secondBlueprint));

        Assert.Contains("MaxNetworkEntities", exception.Message);
        Assert.Single(server.World!.Records);
        Assert.Equal(revision, server.StructuralRevision);
        Assert.Single(scene.GetAllEntities());
        Assert.False(Entity.IsDestroyed(first));
    }

    [Fact]
    public void HostRemovesClientOnlyAuthoredEntity()
    {
        using var scene = new TestScene();
        var clientOnly = scene.CreateEntity("client-only");
        clientOnly.AttachComponent<NetworkObject>().Presence = NetworkPresence.ClientOnly;
        using var world = new NetworkWorld(scene, new NetworkSceneEpoch(1), true);

        world.BindServerAuthoredEntities(() => new NetworkEntityId(1));

        Assert.True(Entity.IsDestroyed(clientOnly));
        Assert.Empty(world.Records);
        Assert.Empty(scene.GetAllEntities());
    }

    [Fact]
    public void ReplicatedComponentTooLargeForDirectUnreliablePacketFailsAtSessionStartup()
    {
        var replication = new NetworkReplicationRegistry();
        replication.Register(900, 128, new OversizedStateCodec());
        var port = ReservePort();
        using var transport = DirectIpTransport.Listen(
            port,
            new DirectIpOptions { MaxUnreliablePayload = 128 });

        var exception = Assert.Throws<InvalidOperationException>(() => new NetworkSession(
            NetworkRole.Server,
            transport,
            new NetworkOptions { GameBuildId = "oversized-component" },
            new NetworkMessageRegistry(),
            replication));

        Assert.Contains("unreliable payload limit", exception.Message);
        Assert.Contains(typeof(OversizedState).FullName!, exception.Message);
    }

    [Fact]
    public void UnsynchronizableLateJoinIsRejectedWithoutAffectingReadyPeer()
    {
        var transport = new ScriptedServerTransport();
        var replication = CreateInitialStateReplication();
        var messages = new NetworkMessageRegistry();
        using var server = new NetworkSession(
            NetworkRole.Server,
            transport,
            new NetworkOptions
            {
                GameBuildId = "late-join-limit",
                MaxBaselineComponentRecords = 1
            },
            messages,
            replication);
        using var scene = new TestScene();
        var authored = scene.CreateEntity("authored");
        authored.AttachComponent<NetworkObject>();
        authored.AttachComponent<InitialStateProbe>().Value = 1;

        server.Start();
        server.BeginServerSceneChange("arena");
        server.AfterSceneAssigned(scene);
        scene.Tick();

        var existingConnection = new TransportConnectionId(1);
        CompleteServerHandshake(server, transport, existingConnection, messages, replication);
        QueueSceneLoaded(server, transport, existingConnection, "arena");
        QueueReady(server, transport, existingConnection);
        Assert.Equal(1, server.ReadyPeerCount);

        var dynamicBlueprint = CreateInitialStateBlueprint();
        server.Spawn(dynamicBlueprint);
        Assert.Equal(2, server.World!.Records.Count);

        var lateConnection = new TransportConnectionId(2);
        CompleteServerHandshake(server, transport, lateConnection, messages, replication);
        QueueSceneLoaded(server, transport, lateConnection, "arena");

        Assert.Equal(1, server.ReadyPeerCount);
        Assert.Equal(TransportState.Listening, transport.State);
        Assert.Contains(
            transport.Disconnects,
            value => value.Connection == lateConnection &&
                     value.Reason == TransportDisconnectReason.Incompatible);
        var reject = transport.Sent
            .Where(value => value.Connection == lateConnection)
            .Select(value => Decode(value.Payload))
            .Last(packet => packet.Header.Message == NetworkProtocolMessage.Reject);
        var reader = new NetworkReader(reject.Payload.Span);
        var diagnostic = reader.ReadString(1024);
        Assert.Contains("cannot be synchronized", diagnostic);
        Assert.Contains("2 Component states", diagnostic);
        Assert.Equal(2, server.World.Records.Count);
    }

    [Fact]
    public void DynamicBaselinePacketIsValidatedBeforeAnyBaselineTransmission()
    {
        var transport = new ScriptedServerTransport(210);
        var messages = new NetworkMessageRegistry();
        var replication = new NetworkReplicationRegistry();
        using var server = new NetworkSession(
            NetworkRole.Server,
            transport,
            new NetworkOptions { GameBuildId = "late-join-fit" },
            messages,
            replication);
        using var scene = new TestScene();
        server.Start();
        server.BeginServerSceneChange("arena");
        server.AfterSceneAssigned(scene);
        scene.Tick();
        var blueprint = CreateNetworkBlueprint();
        blueprint.AssetName = new string('a', 120);
        server.Spawn(blueprint); // Live Spawn fits; the fuller baseline record does not.

        var connection = new TransportConnectionId(1);
        CompleteServerHandshake(
            server,
            transport,
            connection,
            messages,
            replication,
            "late-join-fit");
        var sentBeforeSceneLoaded = transport.Sent.Count;
        QueueSceneLoaded(server, transport, connection, "arena");

        var newlySent = transport.Sent.Skip(sentBeforeSceneLoaded).ToArray();
        Assert.DoesNotContain(
            newlySent,
            value => Decode(value.Payload).Header.Message == NetworkProtocolMessage.Baseline);
        Assert.Contains(
            newlySent,
            value => Decode(value.Payload).Header.Message == NetworkProtocolMessage.Reject);
        Assert.Contains(
            transport.Disconnects,
            value => value.Connection == connection &&
                     value.Reason == TransportDisconnectReason.Incompatible);
        Assert.Single(server.World!.Records);
        Assert.Equal(TransportState.Listening, transport.State);
    }

    [Fact]
    public void Utf8WireLimitsUseBytesInsteadOfCharacters()
    {
        var multibyte = new string('\u754c', 100);
        var options = new NetworkOptions { GameBuildId = multibyte };
        var fingerprintOptions = new NetworkOptions
        {
            GameBuildId = "utf8",
            ContentFingerprint = multibyte
        };
        var catalog = new NetworkSceneCatalog();

        Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Throws<InvalidOperationException>(fingerprintOptions.Validate);
        Assert.Throws<ArgumentException>(() => catalog.Register(multibyte, () => new TestScene()));
    }

    [Fact]
    public void Utf8BlueprintFallbackLimitFailsBeforeMaterialization()
    {
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var server = CreateSession(NetworkRole.Host, transport, new NetworkReplicationRegistry());
        using var scene = new TestScene();
        server.Start();
        server.AfterSceneAssigned(scene);
        var blueprint = CreateNetworkBlueprint();
        blueprint.AssetName = new string('\u754c', 400);

        var exception = Assert.Throws<InvalidOperationException>(() => server.Spawn(blueprint));

        Assert.Contains("1024 UTF-8 bytes", exception.Message);
        Assert.Empty(scene.GetAllEntities());
        Assert.Empty(server.World!.Records);
    }

    [Fact]
    public void DirectSceneChangesAreRejectedOnlyWhileNetworkSessionIsActive()
    {
        var core = CreateUninitializedCore(makeCurrent: true);
        var networking = core.Networking;
        networking.Options.ContentFingerprint = "test-content";
        networking.Scenes.Register("arena", () => new TestScene());
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        networking.StartHost(transport);
        var direct = new TestScene();

        var exception = Assert.Throws<InvalidOperationException>(() => Scene.SetNextScene(direct));
        Assert.Contains("Networking.ChangeScene", exception.Message);
        networking.ChangeScene("arena");
        Assert.NotNull(core.NextScene);

        networking.Stop();
        var offline = new TestScene();
        Scene.SetNextScene(offline);
        Assert.Same(offline, core.NextScene);
        core.NextScene.Terminate();
        direct.Dispose();
        SetCoreInstance(null);
    }

    [Fact]
    public void SessionCanStartAndRestartWhileLocalSceneIsActive()
    {
        var core = CreateUninitializedCore();
        using var localMenu = new TestScene();
        SetCurrentScene(core, localMenu);
        var networking = core.Networking;
        networking.Options.ContentFingerprint = "test-content";
        networking.Options.GameBuildId = "menu-build-1";
        networking.Options.ReplicationRate = 30;
        networking.Scenes.Register("arena", () => new TestScene());
        using var firstTransport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();

        networking.StartHost(firstTransport);
        networking.AfterSceneAssigned(localMenu);

        Assert.Equal(NetworkRole.Host, networking.Role);
        Assert.Same(localMenu, core.CurrentScene);
        Assert.Null(networking.CurrentSceneKey);
        Assert.Equal(NetworkSceneEpoch.None, networking.SceneEpoch);
        Assert.Null(networking.ActiveSession!.World);

        networking.Stop();
        networking.Options.GameBuildId = "menu-build-2";
        networking.Options.ReplicationRate = 20;
        using var secondTransport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();

        networking.StartHost(secondTransport);
        networking.ChangeScene("arena");

        Assert.Equal(NetworkRole.Host, networking.Role);
        Assert.Same(localMenu, core.CurrentScene);
        Assert.IsType<TestScene>(core.NextScene);
        Assert.Null(networking.ActiveSession!.World);

        networking.Stop();
        core.NextScene.Terminate();
        SetCurrentScene(core, null);
    }

    [Fact]
    public void NetworkChangeSceneSynchronizesAndClientUsesAuthorizedSchedulingPath()
    {
        var serverCore = CreateUninitializedCore();
        var clientCore = CreateUninitializedCore();
        using var serverMenu = new TestScene();
        using var clientMenu = new TestScene();
        SetCurrentScene(serverCore, serverMenu);
        SetCurrentScene(clientCore, clientMenu);
        var serverNetwork = serverCore.Networking;
        var clientNetwork = clientCore.Networking;
        serverNetwork.Options.ContentFingerprint = "test-content";
        clientNetwork.Options.ContentFingerprint = "test-content";
        serverNetwork.Scenes.Register("arena", () => new TestScene());
        clientNetwork.Scenes.Register("arena", () => new TestScene());
        var pair = InMemoryTransport.CreatePair();

        serverNetwork.StartServer(pair.Server);
        clientNetwork.Connect(pair.Client);
        Pump(serverNetwork.ActiveSession!, clientNetwork.ActiveSession!);

        Assert.Null(serverNetwork.ActiveSession!.World);
        Assert.Null(clientNetwork.ActiveSession!.World);
        Assert.Same(serverMenu, serverCore.CurrentScene);
        Assert.Same(clientMenu, clientCore.CurrentScene);

        serverNetwork.ChangeScene("arena");
        Pump(serverNetwork.ActiveSession!, clientNetwork.ActiveSession!);

        var serverScene = Assert.IsType<TestScene>(serverCore.NextScene);
        var clientScene = Assert.IsType<TestScene>(clientCore.NextScene);
        serverNetwork.AfterSceneAssigned(serverScene);
        clientNetwork.AfterSceneAssigned(clientScene);
        serverScene.Tick();
        clientScene.Tick();
        Pump(serverNetwork.ActiveSession!, clientNetwork.ActiveSession!);
        clientScene.Tick();
        Pump(serverNetwork.ActiveSession!, clientNetwork.ActiveSession!);

        Assert.Equal("arena", serverNetwork.CurrentSceneKey);
        Assert.Equal("arena", clientNetwork.CurrentSceneKey);
        Assert.Equal(serverNetwork.SceneEpoch, clientNetwork.SceneEpoch);
        Assert.Equal(SceneState.Running, clientScene.State);
        Assert.True(clientNetwork.IsConnected);

        clientNetwork.Stop();
        serverNetwork.Stop();
        clientScene.Terminate();
        serverScene.Terminate();
        SetCurrentScene(clientCore, null);
        SetCurrentScene(serverCore, null);
    }

    private static Core CreateUninitializedCore(bool makeCurrent = false)
    {
        var core = (Core)RuntimeHelpers.GetUninitializedObject(typeof(Core));
        if (makeCurrent)
            SetCoreInstance(core);
        return core;
    }

    private static void SetCoreInstance(Core? core)
    {
        typeof(Core).GetProperty(nameof(Core.Instance), BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, core);
    }

    private static void SetCurrentScene(Core core, Scene? scene)
    {
        typeof(Core).GetProperty(nameof(Core.CurrentScene), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(core, scene);
    }

    private static NetworkReplicationRegistry CreateInitialStateReplication()
    {
        var registry = new NetworkReplicationRegistry();
        registry.Register<InitialStateProbe>();
        return registry;
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkReplicationRegistry replication) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "hardening-tests" },
            new NetworkMessageRegistry(),
            replication);

    private static EntityBlueprint CreateInitialStateBlueprint()
    {
        var blueprint = CreateNetworkBlueprint();
        blueprint.Components.Add(new ComponentBlueprint
        {
            Type = typeof(InitialStateProbe).AssemblyQualifiedName!,
            Properties = { [nameof(InitialStateProbe.Value)] = new JValue(123) }
        });
        blueprint.Children.Add(new EntityBlueprint
        {
            Name = "always-update-child",
            Guid = Guid.NewGuid(),
            Components =
            [
                new ComponentBlueprint { Type = typeof(AlwaysUpdateChildProbe).AssemblyQualifiedName! }
            ]
        });
        return blueprint;
    }

    private static EntityBlueprint CreateNetworkBlueprint(
        NetworkPresence presence = NetworkPresence.Replicated)
    {
        var marker = new ComponentBlueprint
        {
            Type = typeof(NetworkObject).AssemblyQualifiedName!
        };
        if (presence != NetworkPresence.Replicated)
            marker.Properties[nameof(NetworkObject.Presence)] = new JValue((byte)presence);
        return new EntityBlueprint
        {
            Name = "network-root",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/hardening-{Guid.NewGuid():N}",
            Components = [marker]
        };
    }

    private static void PollOnce(NetworkSession session)
    {
        session.PollTransport();
        session.ApplyInbound();
        session.AdvanceClientScopeLoads();
    }

    private static void Pump(NetworkSession server, NetworkSession client, int count = 8)
    {
        for (var index = 0; index < count; index++)
        {
            PollOnce(server);
            PollOnce(client);
        }
    }

    private static void CompleteServerHandshake(
        NetworkSession server,
        ScriptedServerTransport transport,
        TransportConnectionId connection,
        NetworkMessageRegistry messages,
        NetworkReplicationRegistry replication,
        string gameBuildId = "late-join-limit")
    {
        transport.Queue(new TransportEvent(
            TransportEventKind.Connected,
            connection,
            ReadOnlyMemory<byte>.Empty));
        PollOnce(server);
        transport.QueueData(
            connection,
            NetworkProtocol.Encode(
                new NetworkPacketHeader(
                    NetworkProtocolMessage.Hello,
                    Guid.Empty,
                    NetworkSceneEpoch.None,
                    0,
                    NetworkStructuralRevision.None),
                writer =>
                {
                    writer.WriteUInt16(NetworkProtocol.Version);
                    writer.WriteString(gameBuildId, 256);
                    writer.WriteString(null, 256);
                    writer.WriteString(messages.SchemaHash.Hex, 64);
                    writer.WriteString(replication.SchemaHash.Hex, 64);
                },
                NetworkOptions.DefaultMaxProtocolPayload));
        PollOnce(server);
    }

    private static void QueueSceneLoaded(
        NetworkSession server,
        ScriptedServerTransport transport,
        TransportConnectionId connection,
        string sceneKey)
    {
        transport.QueueData(
            connection,
            NetworkProtocol.Encode(
                new NetworkPacketHeader(
                    NetworkProtocolMessage.SceneLoaded,
                    server.SessionId,
                    server.SceneEpoch,
                    server.ServerTick,
                    server.StructuralRevision),
                writer => writer.WriteString(sceneKey, 256),
                NetworkOptions.DefaultMaxProtocolPayload));
        PollOnce(server);
    }

    private static void QueueReady(
        NetworkSession server,
        ScriptedServerTransport transport,
        TransportConnectionId connection)
    {
        transport.QueueData(
            connection,
            NetworkProtocol.Encode(
                new NetworkPacketHeader(
                    NetworkProtocolMessage.Ready,
                    server.SessionId,
                    server.SceneEpoch,
                    server.ServerTick,
                    server.StructuralRevision),
                null,
                NetworkOptions.DefaultMaxProtocolPayload));
        PollOnce(server);
    }

    private static NetworkPacket Decode(byte[] payload)
    {
        Assert.True(NetworkProtocol.TryDecode(
            payload,
            NetworkOptions.DefaultMaxProtocolPayload,
            out var packet,
            out var error), error);
        return packet;
    }

    private static int ReservePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class TestScene : Scene
    {
        internal override void InitializeInternals()
        {
        }
    }

    [NetworkReplicated(800)]
    public sealed class InitialStateProbe : Component
    {
        [DreambitSerialize]
        [Replicated(1)]
        public int Value { get; set; }

        public int UpdateCount { get; private set; }
        public int PhysicsCount { get; private set; }
        public int ValueObservedOnFirstUpdate { get; private set; }

        public override void OnUpdate()
        {
            UpdateCount++;
            if (UpdateCount == 1)
                ValueObservedOnFirstUpdate = Value;
        }

        public override void OnPhysicsUpdate() => PhysicsCount++;
    }

    public sealed class AlwaysUpdateChildProbe : Component
    {
        public int UpdateCount { get; private set; }
        public override void OnCreated() => Entity.AlwaysUpdate = true;
        public override void OnUpdate() => UpdateCount++;
    }

    public sealed class OversizedState : Component
    {
    }

    private sealed class OversizedStateCodec : INetworkComponentCodec<OversizedState>
    {
        public void Write(NetworkWriter writer, OversizedState component)
        {
        }

        public void Read(ref NetworkReader reader, OversizedState component)
        {
        }
    }

    private sealed class ScriptedServerTransport : INetworkTransport
    {
        private readonly ConcurrentQueue<TransportEvent> _events = new();

        public ScriptedServerTransport(int maximumReliablePayload = 64 * 1024)
        {
            Capabilities = new TransportCapabilities(maximumReliablePayload, 1200, 4);
        }

        public TransportCapabilities Capabilities { get; }
        public TransportState State { get; private set; }
        public List<(TransportConnectionId Connection, byte[] Payload)> Sent { get; } = [];
        public List<(TransportConnectionId Connection, TransportDisconnectReason Reason)> Disconnects { get; } = [];

        public void StartServer() => State = TransportState.Listening;
        public void Connect() => throw new NotSupportedException();
        public bool TryPollEvent(out TransportEvent transportEvent) =>
            _events.TryDequeue(out transportEvent);

        public void Send(
            TransportConnectionId connection,
            ReadOnlySpan<byte> payload,
            NetworkDelivery delivery,
            byte channel)
        {
            var maximum = delivery == NetworkDelivery.ReliableOrdered
                ? Capabilities.MaxReliablePayload
                : Capabilities.MaxUnreliablePayload;
            if (payload.Length > maximum)
                throw new ArgumentOutOfRangeException(nameof(payload));
            if (channel >= Capabilities.MaxChannels)
                throw new ArgumentOutOfRangeException(nameof(channel));
            Sent.Add((connection, payload.ToArray()));
        }

        public void Disconnect(
            TransportConnectionId connection,
            TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown) =>
            Disconnects.Add((connection, reason));

        public void Stop() => State = TransportState.Stopped;
        public void Dispose() => State = TransportState.Disposed;
        public void Queue(TransportEvent transportEvent) => _events.Enqueue(transportEvent);
        public void QueueData(TransportConnectionId connection, byte[] payload) =>
            Queue(new TransportEvent(
                TransportEventKind.Data,
                connection,
                payload,
                NetworkDelivery.ReliableOrdered,
                0));
    }
}
