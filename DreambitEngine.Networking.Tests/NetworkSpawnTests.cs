using System;
using Dreambit;
using Dreambit.Networking;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class NetworkSpawnTests
{
    [Fact]
    public void ServerSpawnMaterializesIndependentClientEntityAndDespawnRemovesIt()
    {
        var pair = InMemoryTransport.CreatePair();
        using var server = CreateSession(NetworkRole.Server, pair.Server);
        using var client = CreateSession(NetworkRole.Client, pair.Client);
        using var serverScene = new TestScene();
        using var clientScene = new TestScene();
        string? disconnectDiagnostic = null;
        server.PeerDisconnected += (_, _, diagnostic) => disconnectDiagnostic = diagnostic;
        var blueprint = CreateNetworkBlueprint();
        Assert.True(Resources.TryRegisterAsset(blueprint));

        server.Start();
        client.Start();
        Pump(server, client);
        server.AfterSceneAssigned(serverScene);
        client.AfterSceneAssigned(clientScene);

        var serverEntity = server.Spawn(blueprint, new NetworkSpawnOptions
        {
            Owner = client.LocalPeerId
        });
        Pump(server, client);

        Assert.True(
            server.World!.TryGetNetworkId(serverEntity, out var id),
            disconnectDiagnostic);
        Assert.True(client.World!.TryGetEntity(id, out var clientEntity));
        Assert.NotNull(clientEntity);
        Assert.NotEqual(serverEntity.Id, clientEntity!.Id);
        Assert.Equal(client.LocalPeerId, client.World.GetOwner(id));

        server.Despawn(serverEntity);
        Pump(server, client);

        Assert.False(serverEntity.Enabled);
        Assert.True(Dreambit.ECS.Entity.IsDestroyed(serverEntity));
        Assert.True(Dreambit.ECS.Entity.IsDestroyed(clientEntity));
        Assert.False(client.World.TryGetEntity(id, out _));
    }

    [Fact]
    public void BoxedBlueprintIsMaterializedBeforeNetworkShapeValidationAndSpawn()
    {
        using var scene = new TestScene();
        var source = CreateNetworkBlueprint("boxed-source");
        var childGuid = Guid.NewGuid();
        source.Children.Add(new EntityBlueprint
        {
            Name = "referenced-child",
            Guid = childGuid
        });
        source.Components.Add(new ComponentBlueprint
        {
            Type = typeof(BlueprintEntityReference).AssemblyQualifiedName!,
            Properties = { [nameof(BlueprintEntityReference.Target)] = new JValue(childGuid.ToString()) }
        });
        Assert.True(Resources.TryRegisterAsset(source));
        var instance = new EntityBlueprint
        {
            Name = "instance",
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = $"test/instance-{Guid.NewGuid():N}",
            BlueprintInstance = new BlueprintInstanceReference
            {
                AssetId = source.AssetId.Value,
                AssetName = source.AssetName
            }
        };

        var entity = scene.CreateNetworkEntity(instance);

        Assert.Equal(source.Name, entity.Name);
        Assert.NotNull(entity.GetComponent<NetworkObject>());
        Assert.NotEqual(instance.Guid, entity.Id);
        var referencedChild = entity.GetComponent<BlueprintEntityReference>().Target;
        Assert.NotNull(referencedChild);
        Assert.Equal("referenced-child", referencedChild.Name);
        Assert.Same(entity.Children[0], referencedChild);
    }

    [Fact]
    public void NetworkBlueprintRequiresExactlyOneRootMarkerAndNoNestedMarkers()
    {
        using var scene = new TestScene();
        var missing = new EntityBlueprint { Name = "missing", AssetId = AssetId.New() };
        var nested = CreateNetworkBlueprint("root");
        var child = CreateNetworkBlueprint("child");
        child.AssetId = AssetId.Empty;
        child.AssetName = string.Empty;
        nested.Children.Add(child);

        var missingError = Assert.Throws<InvalidOperationException>(
            () => scene.CreateNetworkEntity(missing));
        var nestedError = Assert.Throws<InvalidOperationException>(
            () => scene.CreateNetworkEntity(nested));

        Assert.Contains("exactly one NetworkObject", missingError.Message);
        Assert.Contains("nested NetworkObject", nestedError.Message);
        Assert.Empty(scene.GetAllEntities());
    }

    [Fact]
    public void RemoteSpawnRejectsBlueprintWithoutStableAssetId()
    {
        using var server = CreateSession(
            NetworkRole.Host,
            new SessionHandshakeTests.StandaloneInMemoryServerTransportForTests());
        using var scene = new TestScene();
        server.Start();
        server.AfterSceneAssigned(scene);

        var exception = Assert.Throws<InvalidOperationException>(
            () => server.Spawn(new EntityBlueprint { Name = "runtime-only" }));

        Assert.Contains("stable AssetId", exception.Message);
    }

    private static EntityBlueprint CreateNetworkBlueprint(string name = "network-spawn")
    {
        var assetName = $"test/{name}-{Guid.NewGuid():N}";
        return new EntityBlueprint
        {
            Name = name,
            Guid = Guid.NewGuid(),
            AssetId = AssetId.New(),
            AssetName = assetName,
            Components =
            [
                new ComponentBlueprint
                {
                    Type = typeof(NetworkObject).AssemblyQualifiedName!
                }
            ]
        };
    }

    private static NetworkSession CreateSession(NetworkRole role, INetworkTransport transport) =>
        new(
            role,
            transport,
            new NetworkOptions { GameBuildId = "spawn-tests" },
            new NetworkMessageRegistry(),
            new NetworkReplicationRegistry());

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

    public sealed class BlueprintEntityReference : Dreambit.ECS.Component
    {
        [Dreambit.ECS.DreambitSerialize]
        public Dreambit.ECS.Entity Target { get; set; } = null!;
    }
}
