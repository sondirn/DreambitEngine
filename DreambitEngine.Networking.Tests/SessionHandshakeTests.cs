using Dreambit.ECS;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class SessionHandshakeTests
{
    [Fact]
    public void CompatibleClientCompletesHandshake()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, "build-a");
        using var client = CreateSession(NetworkRole.Client, pair.Client, "build-a");

        server.Start();
        client.Start();
        Pump(server, client);

        Assert.True(client.IsConnected);
        Assert.True(client.LocalPeerId.IsValid);
        Assert.NotEqual(Guid.Empty, client.SessionId);
        Assert.Equal(server.SessionId, client.SessionId);
        Assert.Equal(1, server.ReadyPeerCount);
    }

    [Fact]
    public void SessionCapturesOptionsAtConstruction()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverOptions = new NetworkOptions { GameBuildId = "captured-build" };
        using var server = new NetworkSession(
            NetworkRole.Server,
            pair.Server,
            serverOptions,
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        using var client = CreateSession(
            NetworkRole.Client,
            pair.Client,
            "captured-build");
        serverOptions.GameBuildId = "mutated-after-construction";

        server.Start();
        client.Start();
        Pump(server, client);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public void BuildMismatchIsRejected()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, "build-a");
        using var client = CreateSession(NetworkRole.Client, pair.Client, "build-b");
        string? diagnostic = null;
        client.ConnectionFailed += (_, value) => diagnostic = value;

        server.Start();
        client.Start();
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Contains("build mismatch", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostCreatesLogicalLocalPeerWithoutSocketLoopback()
    {
        using var transport = new StandaloneInMemoryServerTransportForTests();
        using var host = CreateSession(NetworkRole.Host, transport, "build-a");

        host.Start();

        Assert.True(host.IsConnected);
        Assert.True(host.LocalPeerId.IsValid);
        Assert.Equal(1, host.ReadyPeerCount);
    }

    [Fact]
    public void MessageSchemaMismatchIsRejectedBeforeSceneSynchronization()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverMessages = new NetworkMessageRegistry();
        serverMessages.Register<TestMessage>(
            1,
            NetworkMessageDirection.Bidirectional,
            4,
            new TestMessageCodec(),
            (_, _) => { });
        using var server = new NetworkSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkOptions { GameBuildId = "schema" },
            serverMessages,
            new NetworkReplicationRegistry());
        using var client = new NetworkSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkOptions { GameBuildId = "schema" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        string? diagnostic = null;
        client.ConnectionFailed += (_, value) => diagnostic = value;

        server.Start();
        client.Start();
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Contains("message schema mismatch", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContentFingerprintMismatchIsRejected()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = new NetworkSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkOptions { GameBuildId = "content", ContentFingerprint = "server-content" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        using var client = new NetworkSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkOptions { GameBuildId = "content", ContentFingerprint = "client-content" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        string? diagnostic = null;
        client.ConnectionFailed += (_, value) => diagnostic = value;

        server.Start();
        client.Start();
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Contains("fingerprint mismatch", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplicationSchemaMismatchIsRejectedBeforeSceneSynchronization()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverReplication = new NetworkReplicationRegistry();
        serverReplication.Register<HandshakeState>();
        using var server = new NetworkSession(
            NetworkRole.Server,
            pair.Server,
            new NetworkOptions { GameBuildId = "replication-schema" },
            new NetworkMessageRegistry(),
            serverReplication);
        using var client = new NetworkSession(
            NetworkRole.Client,
            pair.Client,
            new NetworkOptions { GameBuildId = "replication-schema" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
        string? diagnostic = null;
        client.ConnectionFailed += (_, value) => diagnostic = value;

        server.Start();
        client.Start();
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Contains("replication schema mismatch", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedPacketDisconnectsOnlyTheOffendingPeer()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server, "malformed");
        using var client = CreateSession(NetworkRole.Client, pair.Client, "malformed");
        server.Start();
        client.Start();
        Pump(server, client);

        pair.Client.Send(
            pair.Client.Connection,
            [1, 2, 3],
            NetworkDelivery.ReliableOrdered,
            0);
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Equal(0, server.ReadyPeerCount);
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        string buildId)
    {
        return new NetworkSession(
            role,
            transport,
            new NetworkOptions { GameBuildId = buildId },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());
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

    internal sealed class StandaloneInMemoryServerTransportForTests : INetworkTransport
    {
        public TransportCapabilities Capabilities { get; } = new(1024, 512, 4);
        public TransportState State { get; private set; }
        public void StartServer() => State = TransportState.Listening;
        public void Connect() => throw new NotSupportedException();
        public bool TryPollEvent(out TransportEvent transportEvent)
        {
            transportEvent = default;
            return false;
        }
        public void Send(TransportConnectionId connection, ReadOnlySpan<byte> payload, NetworkDelivery delivery, byte channel) =>
            throw new NotSupportedException();
        public void Disconnect(TransportConnectionId connection, TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown)
        {
        }
        public void Stop() => State = TransportState.Stopped;
        public void Dispose() => State = TransportState.Disposed;
    }

    private readonly record struct TestMessage(int Value);

    [NetworkReplicated(601)]
    public sealed class HandshakeState : Component
    {
        [Replicated(1)] public int Value { get; set; }
    }

    private sealed class TestMessageCodec : INetworkMessageCodec<TestMessage>
    {
        public void Write(NetworkWriter writer, TestMessage message) =>
            writer.WriteInt32(message.Value);

        public TestMessage Read(ref NetworkReader reader) =>
            new(reader.ReadInt32());
    }
}
