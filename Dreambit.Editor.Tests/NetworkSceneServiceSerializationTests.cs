using Dreambit.ECS;
using Dreambit.Editor.Scenes;
using Dreambit.Networking;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class NetworkSceneServiceSerializationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(
            Path.GetTempPath(),
            "Dreambit.Editor.NetworkSceneServiceSerializationTests",
            Guid.NewGuid().ToString("N"));

    public NetworkSceneServiceSerializationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SceneRoundTripPreservesIdsReferencesAndAuthoredMarkerState()
    {
        var serviceEntityId = Guid.NewGuid();
        var targetEntityId = Guid.NewGuid();
        var scenePath =
            Path.Combine(
                _root,
                "network-scene-service.scene");

        var source =
            new SceneBlueprint
            {
                Name = "Network Scene Service",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Services",
                        Guid = serviceEntityId,
                        Components =
                        [
                            CreateServiceBlueprint(
                                17,
                                targetEntityId),
                            CreateNetworkObjectBlueprint()
                        ]
                    },
                    new EntityBlueprint
                    {
                        Name = "Reference Target",
                        Guid = targetEntityId
                    }
                ]
            };

        SerializableNetworkSceneService firstService;

        using (var document =
               new SceneDocument(
                   source,
                   null,
                   new SelectionService()))
        {
            var serviceEntity =
                document.Scene!.FindEntity(serviceEntityId)!;

            firstService =
                serviceEntity
                    .GetComponent<SerializableNetworkSceneService>();

            Assert.Same(
                document.Scene.FindEntity(targetEntityId),
                firstService.Target);

            Assert.Equal(
                NetworkPresence.Replicated,
                serviceEntity.GetComponent<NetworkObject>().Presence);

            Assert.Same(
                firstService,
                document.Scene.Services
                    .Get<SerializableNetworkSceneService>());

            document.Save(scenePath);
        }

        var roundTripped =
            SceneDocumentSerializer.Deserialize(
                File.ReadAllText(scenePath));

        Assert.Equal(
            2,
            roundTripped.Entities
                .SelectMany(entity => entity.FlattenedHierarchy())
                .Select(entity => entity.Guid)
                .Distinct()
                .Count());

        var serializedServiceEntity =
            Assert.Single(
                roundTripped.Entities,
                entity => entity.Guid == serviceEntityId);

        var serializedNetworkObject =
            Assert.Single(
                serializedServiceEntity.Components,
                component => component.Type == "Dreambit.NetworkObject");

        var serializedPresence =
            Assert.Single(serializedNetworkObject.Properties);

        Assert.Equal(
            nameof(NetworkObject.Presence),
            serializedPresence.Key);

        Assert.Equal(
            (byte)NetworkPresence.Replicated,
            serializedPresence.Value.Value<byte>());

        using var reopened =
            SceneDocument.Open(
                scenePath,
                new SelectionService());

        var reopenedServiceEntity =
            reopened.Scene!.FindEntity(serviceEntityId)!;

        var reopenedService =
            reopenedServiceEntity
                .GetComponent<SerializableNetworkSceneService>();

        Assert.NotSame(firstService, reopenedService);
        Assert.Equal(17, reopenedService.Value);
        Assert.Equal(targetEntityId, reopenedService.Target!.Id);
        Assert.Equal(
            NetworkPresence.Replicated,
            reopenedServiceEntity
                .GetComponent<NetworkObject>()
                .Presence);

        reopened.BeforeAssemblyReload();
        reopened.AfterAssemblyReload();

        var reloadedService =
            reopened.Scene!
                .FindEntity(serviceEntityId)!
                .GetComponent<SerializableNetworkSceneService>();

        Assert.Equal(targetEntityId, reloadedService.Target!.Id);
        Assert.Same(
            reloadedService,
            reopened.Scene.Services
                .Get<SerializableNetworkSceneService>());
    }

    [Fact]
    public void BoxedBlueprintKeepsNetworkInfrastructureInSourceOnly()
    {
        var blueprintId = AssetId.New();
        var sourceRootId = Guid.NewGuid();
        var sourceChildId = Guid.NewGuid();

        var source =
            new EntityBlueprint
            {
                AssetId = blueprintId,
                AssetName = "services/network-clock.blueprint",
                Name = "Network Clock",
                Guid = sourceRootId,
                Components =
                [
                    CreateServiceBlueprint(
                        23,
                        sourceChildId),
                    CreateNetworkObjectBlueprint()
                ],
                Children =
                [
                    new EntityBlueprint
                    {
                        Name = "Reference Target",
                        Guid = sourceChildId
                    }
                ]
            };

        var scenePath =
            Path.Combine(
                _root,
                "boxed-network-scene-service.scene");

        Guid instanceId;
        Guid materializedChildId;

        using (var document =
               SceneDocument.CreateNew(
                   "Boxed Network Service",
                   new SelectionService(),
                   blueprintInstanceResolver: _ => source))
        {
            var instance =
                document.InstantiateBlueprint(source);

            instanceId = instance.Id;
            materializedChildId = Assert.Single(instance.Children).Id;

            var service =
                instance
                    .GetComponent<SerializableNetworkSceneService>();

            Assert.Equal(23, service.Value);
            Assert.Equal(materializedChildId, service.Target!.Id);
            Assert.NotEqual(sourceRootId, instanceId);
            Assert.NotEqual(sourceChildId, materializedChildId);

            document.Save(scenePath);
        }

        var saved =
            SceneDocumentSerializer.Deserialize(
                File.ReadAllText(scenePath));

        var boxedRoot = Assert.Single(saved.Entities);
        Assert.Equal(instanceId, boxedRoot.Guid);
        Assert.NotNull(boxedRoot.BlueprintInstance);
        Assert.Equal(blueprintId.Value, boxedRoot.BlueprintInstance.AssetId);
        Assert.Empty(boxedRoot.Components);
        Assert.Empty(boxedRoot.Children);

        using var reopened =
            SceneDocument.Open(
                scenePath,
                new SelectionService(),
                blueprintInstanceResolver: _ => source);

        var materializedRoot =
            reopened.Scene!.FindEntity(instanceId)!;

        var materializedChild =
            Assert.Single(materializedRoot.Children);

        var materializedService =
            materializedRoot
                .GetComponent<SerializableNetworkSceneService>();

        Assert.Equal(materializedChildId, materializedChild.Id);
        Assert.Equal(materializedChild.Id, materializedService.Target!.Id);
        Assert.Equal(
            NetworkPresence.Replicated,
            materializedRoot
                .GetComponent<NetworkObject>()
                .Presence);

        Assert.Equal(
            2,
            reopened.Scene
                .GetAllEntities()
                .Select(entity => entity.Id)
                .Distinct()
                .Count());

        var recaptured =
            SceneDocumentSerializer.Capture(
                reopened.Scene,
                saved,
                saved.Name);

        var recapturedRoot = Assert.Single(recaptured.Entities);
        Assert.NotNull(recapturedRoot.BlueprintInstance);
        Assert.Empty(recapturedRoot.Components);
        Assert.Empty(recapturedRoot.Children);
    }

    private static ComponentBlueprint CreateServiceBlueprint(
        int value,
        Guid targetId) =>
        new()
        {
            Type =
                "Dreambit.Editor.Tests.SerializableNetworkSceneService",
            Properties = new Dictionary<string, JToken>
            {
                [nameof(SerializableNetworkSceneService.Value)] = value,
                [nameof(SerializableNetworkSceneService.Target)] =
                    targetId.ToString()
            }
        };

    private static ComponentBlueprint CreateNetworkObjectBlueprint() =>
        new()
        {
            Type = "Dreambit.NetworkObject",
            Properties = new Dictionary<string, JToken>
            {
                [nameof(NetworkObject.Presence)] =
                    (byte)NetworkPresence.Replicated
            }
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}

[BlueprintType("Dreambit.Editor.Tests.SerializableNetworkSceneService")]
public sealed class SerializableNetworkSceneService : SceneServiceComponent
{
    [DreambitSerialize]
    public int Value { get; set; }

    [DreambitSerialize]
    public Entity? Target { get; set; }
}
