using System;
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

public sealed class NetworkTransformTests
{
    [Fact]
    public void DynamicSpawnPublishesItsActualInitialTransform()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);

        var serverEntity = server.Spawn(
            blueprint,
            new NetworkSpawnOptions
            {
                Position = new Vector3(7f, -4f, 0f),
                Scale = new Vector3(2f, 3f, 1f)
            });
        Pump(server, client);

        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));
        var transform = clientEntity!.GetComponent<NetworkTransform2D>();
        Assert.Equal(new Vector2(7f, -4f), transform.AuthoritativePosition);
        Assert.Equal(new Vector2(2f, 3f), transform.AuthoritativeScale);
        Assert.Equal(new Vector2(7f, -4f), clientEntity.Transform.WorldPosition2D);
    }

    [Theory]
    [InlineData(TransformAuthority.Client)]
    [InlineData(TransformAuthority.Both)]
    public void ClientCapableAuthorityAcceptsOwnerPoseAndRelaysItThroughServerState(
        TransformAuthority authority)
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);

        var serverEntity = server.Spawn(
            blueprint,
            new NetworkSpawnOptions { Owner = client.LocalPeerId });
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));

        var serverTransform = serverEntity.GetComponent<NetworkTransform2D>();
        serverTransform.Authority = authority;
        server.SendSnapshotNow();
        Pump(server, client);

        var clientTransform = clientEntity!.GetComponent<NetworkTransform2D>();
        Assert.Equal(authority, clientTransform.Authority);
        clientEntity.Transform.WorldPosition2D = new Vector2(12f, -8f);
        clientEntity.Transform.WorldRotation2D = 0.75f;
        clientEntity.Transform.WorldScale2D = new Vector2(1.5f, 0.5f);

        client.SendClientTransformsNow();
        Pump(server, client);

        Assert.Equal(new Vector2(12f, -8f), serverEntity.Transform.WorldPosition2D);
        Assert.Equal(0.75f, serverEntity.Transform.WorldRotation2D);
        Assert.Equal(new Vector2(1.5f, 0.5f), serverEntity.Transform.WorldScale2D);
        Assert.Equal(new Vector2(12f, -8f), serverTransform.AuthoritativePosition);

        server.SendSnapshotNow();
        Pump(server, client);
        Assert.Equal(new Vector2(12f, -8f), clientTransform.AuthoritativePosition);
    }

    [Fact]
    public void ServerAuthoritySilentlyRejectsClientPose()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);

        var serverEntity = server.Spawn(
            blueprint,
            new NetworkSpawnOptions { Owner = client.LocalPeerId });
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));

        // Simulate a modified client opting itself into client authority. The server's copy remains
        // the source of truth for the authority decision.
        var clientTransform = clientEntity!.GetComponent<NetworkTransform2D>();
        clientTransform.Authority = TransformAuthority.Client;
        clientEntity.Transform.WorldPosition2D = new Vector2(99f, 101f);
        client.SendClientTransformsNow();
        Pump(server, client);

        Assert.Equal(Vector2.Zero, serverEntity.Transform.WorldPosition2D);
        Assert.Equal(Vector2.Zero,
            serverEntity.GetComponent<NetworkTransform2D>().AuthoritativePosition);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void ListenHostKeepsRemoteClientPoseAsInterpolationTarget()
    {
        var pair = InMemoryTransport.CreatePair();
        using var host = CreateSession(NetworkRole.Host, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var hostScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(host, client, hostScene, clientScene);

        var hostEntity = host.Spawn(
            blueprint,
            new NetworkSpawnOptions { Owner = client.LocalPeerId });
        Pump(host, client);
        Assert.True(host.World!.TryGetNetworkId(hostEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));

        var hostTransform = hostEntity.GetComponent<NetworkTransform2D>();
        hostTransform.Authority = TransformAuthority.Client;
        host.SendSnapshotNow();
        Pump(host, client);

        clientEntity!.Transform.WorldPosition2D = new Vector2(10f, 5f);
        client.SendClientTransformsNow();
        Pump(host, client);

        Assert.Equal(new Vector2(10f, 5f), hostTransform.AuthoritativePosition);
        Assert.Equal(Vector2.Zero, hostEntity.Transform.WorldPosition2D);
    }

    [Fact]
    public void ClientAuthoritySilentlyRejectsPoseFromNonOwner()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        var blueprint = CreateBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));
        ConnectAndAssign(server, client, serverScene, clientScene);

        var serverEntity = server.Spawn(blueprint);
        Pump(server, client);
        Assert.True(server.World!.TryGetNetworkId(serverEntity, out var id));
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));

        serverEntity.GetComponent<NetworkTransform2D>().Authority =
            TransformAuthority.Client;
        server.SendSnapshotNow();
        Pump(server, client);

        // Simulate a modified client claiming local ownership without a server ownership message.
        client.World.SetOwner(id, client.LocalPeerId);
        clientEntity!.Transform.WorldPosition2D = new Vector2(-50f, 80f);
        client.SendClientTransformsNow();
        Pump(server, client);

        Assert.Equal(NetworkPeerId.None, server.World.GetOwner(id));
        Assert.Equal(Vector2.Zero, serverEntity.Transform.WorldPosition2D);
        Assert.True(client.IsConnected);
    }

    private static EntityBlueprint CreateBlueprint() =>
        new()
        {
            Name = "network-transform",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/network-transform-{Guid.NewGuid():N}",
            Components =
            [
                new ComponentBlueprint { Type = typeof(NetworkObject).AssemblyQualifiedName! },
                new ComponentBlueprint { Type = typeof(NetworkTransform2D).AssemblyQualifiedName! }
            ]
        };

    private static NetworkSession CreateSession(
        NetworkRole role,
        INetworkTransport transport)
    {
        var replication = new NetworkReplicationRegistry();
        replication.Register<NetworkTransform2D>();
        return new NetworkSession(
            role,
            transport,
            new NetworkOptions { GameBuildId = "network-transform-tests" },
            new NetworkMessageRegistry(),
            replication);
    }

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
}
