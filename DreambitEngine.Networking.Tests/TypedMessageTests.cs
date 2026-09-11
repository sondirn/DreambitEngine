using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class TypedMessageTests
{
    [Fact]
    public void ClientMessageUsesRegisteredCodecDirectionAndSenderContext()
    {
        var pair = InMemoryTransport.CreatePair();
        InputMessage? received = null;
        NetworkMessageContext context = default;
        var serverMessages = new NetworkMessageRegistry();
        var clientMessages = new NetworkMessageRegistry();
        serverMessages.Register<InputMessage>(
            7,
            NetworkMessageDirection.ClientToServer,
            4,
            new InputCodec(),
            (valueContext, value) =>
            {
                context = valueContext;
                received = value;
            });
        clientMessages.Register<InputMessage>(
            7,
            NetworkMessageDirection.ClientToServer,
            4,
            new InputCodec(),
            (_, _) => { });
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverMessages);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientMessages);
        Connect(server, client);

        client.SendToServer(new InputMessage(123), NetworkDelivery.ReliableOrdered);
        Pump(server, client);

        Assert.Equal(new InputMessage(123), received);
        Assert.Equal(client.LocalPeerId, context.Sender);
    }

    [Fact]
    public void PublicSendRejectsAnInvalidDirectionBeforeTransportUse()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverMessages = CreateServerEventRegistry();
        var clientMessages = CreateServerEventRegistry();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverMessages);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientMessages);
        Connect(server, client);

        var exception = Assert.Throws<InvalidOperationException>(
            () => client.SendToServer(new InputMessage(1), NetworkDelivery.ReliableOrdered));

        Assert.Contains("client-to-server", exception.Message);
    }

    [Fact]
    public void ServerDisconnectsClientThatSendsMessageInWrongDirection()
    {
        var pair = InMemoryTransport.CreatePair();
        var serverMessages = CreateServerEventRegistry();
        var clientMessages = CreateServerEventRegistry();
        using var server = CreateSession(NetworkRole.Server, pair.Server, serverMessages);
        using var client = CreateSession(NetworkRole.Client, pair.Client, clientMessages);
        Connect(server, client);
        using var messagePayload = new NetworkWriter(10, 10);
        messagePayload.WriteUInt16(8);
        messagePayload.WriteInt32(4);
        messagePayload.WriteInt32(9);
        var packet = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.UserMessage,
                client.SessionId,
                client.SceneEpoch,
                0,
                NetworkStructuralRevision.None),
            writer => writer.WriteBytes(messagePayload.WrittenSpan),
            128);

        pair.Client.Send(
            pair.Client.Connection,
            packet,
            NetworkDelivery.ReliableOrdered,
            1);
        Pump(server, client, 12);

        Assert.False(client.IsConnected);
        Assert.Equal(0, server.ReadyPeerCount);
    }

    [Fact]
    public void HostClientMessageUsesSerializedLoopbackPipeline()
    {
        InputMessage? received = null;
        var messages = new NetworkMessageRegistry();
        messages.Register<InputMessage>(
            9,
            NetworkMessageDirection.ClientToServer,
            4,
            new InputCodec(),
            (_, value) => received = value);
        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        using var host = CreateSession(NetworkRole.Host, transport, messages);
        host.Start();

        host.SendToServer(new InputMessage(55), NetworkDelivery.ReliableOrdered);

        Assert.Equal(new InputMessage(55), received);
    }

    [Fact]
    public void MessageRegistryRejectsDuplicateIdsAndPayloadOverflow()
    {
        var registry = new NetworkMessageRegistry();
        registry.Register<InputMessage>(
            1,
            NetworkMessageDirection.Bidirectional,
            4,
            new InputCodec(),
            (_, _) => { });
        Assert.Throws<InvalidOperationException>(
            () => registry.Register<OtherMessage>(
                1,
                NetworkMessageDirection.Bidirectional,
                4,
                new OtherCodec(),
                (_, _) => { }));

        using var transport = new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests();
        var overflow = new NetworkMessageRegistry();
        overflow.Register<InputMessage>(
            2,
            NetworkMessageDirection.ClientToServer,
            1,
            new InputCodec(),
            (_, _) => { });
        using var host = CreateSession(NetworkRole.Host, transport, overflow);
        host.Start();
        Assert.Throws<NetworkProtocolException>(
            () => host.SendToServer(new InputMessage(1), NetworkDelivery.ReliableOrdered));
    }

    private static NetworkMessageRegistry CreateServerEventRegistry()
    {
        var registry = new NetworkMessageRegistry();
        registry.Register<InputMessage>(
            8,
            NetworkMessageDirection.ServerToClient,
            4,
            new InputCodec(),
            (_, _) => { });
        return registry;
    }

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkMessageRegistry messages) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "message-tests" },
            messages,
            new NetworkReplicationRegistry());

    private static void Connect(NetworkSession server, NetworkSession client)
    {
        server.Start();
        client.Start();
        Pump(server, client);
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

    private readonly record struct InputMessage(int Value);
    private readonly record struct OtherMessage(int Value);

    private sealed class InputCodec : INetworkMessageCodec<InputMessage>
    {
        public void Write(NetworkWriter writer, InputMessage message) =>
            writer.WriteInt32(message.Value);

        public InputMessage Read(ref NetworkReader reader) =>
            new(reader.ReadInt32());
    }

    private sealed class OtherCodec : INetworkMessageCodec<OtherMessage>
    {
        public void Write(NetworkWriter writer, OtherMessage message) =>
            writer.WriteInt32(message.Value);

        public OtherMessage Read(ref NetworkReader reader) =>
            new(reader.ReadInt32());
    }
}
