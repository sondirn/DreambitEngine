using Dreambit.ECS;
using Dreambit.Editor.Scenes;
using Dreambit.Networking;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class SceneContentInstanceTests
{
    [Fact]
    public void DistinctContentInstancesUnloadIndependentlyAndPreservePersistentEntities()
    {
        using var scene = new AdditiveTestScene();
        var persistent = scene.CreateEntity("persistent");
        var villageSource = CreateSource("village", "village-root");
        var treeSource = CreateSource("tree", "tree-root");

        var village = scene.LoadAdditive(villageSource);
        var tree = scene.LoadAdditive(treeSource);

        Assert.Equal(2, scene.ContentInstances.Count);
        Assert.True(scene.TryGetContentInstance(village.InstanceId, out var villageById));
        Assert.Same(village, villageById);
        Assert.True(scene.TryGetContentInstance(village.RootEntities[0], out var villageByEntity));
        Assert.Same(village, villageByEntity);
        Assert.NotNull(scene.FindEntity("village-root"));
        Assert.NotNull(scene.FindEntity("tree-root"));

        Assert.True(scene.Unload(village));

        Assert.False(village.IsLoaded);
        Assert.Empty(village.RootEntities);
        Assert.Empty(village.OwnedEntities);
        Assert.Empty(village.EntitiesBySourceGuid);
        Assert.Null(scene.FindEntity("village-root"));
        Assert.NotNull(scene.FindEntity("tree-root"));
        Assert.Same(persistent, scene.FindEntity(persistent.Id));
        Assert.Single(scene.ContentInstances);
        Assert.False(scene.TryGetContentInstance(village.InstanceId, out _));
    }

    [Fact]
    public void DuplicateSourceUsesFreshRuntimeIdsAndInstanceLocalReferences()
    {
        using var scene = new AdditiveTestScene();
        var targetGuid = Guid.NewGuid();
        var holderGuid = Guid.NewGuid();
        var source = new SceneBlueprint
        {
            Name = "house",
            AssetId = AssetId.New(),
            AssetName = "Scenes/House.scene",
            Entities =
            [
                new EntityBlueprint { Name = "chest", Guid = targetGuid },
                new EntityBlueprint
                {
                    Name = "door",
                    Guid = holderGuid,
                    Components =
                    [
                        Component<AdditiveReferenceComponent>(
                            (nameof(AdditiveReferenceComponent.Target), targetGuid.ToString()))
                    ]
                }
            ]
        };

        var a = scene.LoadAdditive(source);
        var b = scene.LoadAdditive(source);

        Assert.Equal(a.SourceAssetId, b.SourceAssetId);
        Assert.Equal(a.SourceAssetName, b.SourceAssetName);
        Assert.NotEqual(a.InstanceId, b.InstanceId);
        Assert.Equal(a.EntitiesBySourceGuid.Keys.Order(), b.EntitiesBySourceGuid.Keys.Order());
        Assert.NotEqual(a.GetEntity(targetGuid).Id, b.GetEntity(targetGuid).Id);
        Assert.NotEqual(a.GetEntity(holderGuid).Id, b.GetEntity(holderGuid).Id);
        Assert.Same(
            a.GetEntity(targetGuid),
            a.GetEntity(holderGuid).GetComponent<AdditiveReferenceComponent>().Target);
        Assert.Same(
            b.GetEntity(targetGuid),
            b.GetEntity(holderGuid).GetComponent<AdditiveReferenceComponent>().Target);
        Assert.NotSame(
            a.GetEntity(holderGuid).GetComponent<AdditiveReferenceComponent>().Target,
            b.GetEntity(holderGuid).GetComponent<AdditiveReferenceComponent>().Target);

        Assert.True(scene.Unload(a));
        Assert.True(b.IsLoaded);
        Assert.Same(b.GetEntity(targetGuid), scene.FindEntity(b.GetEntity(targetGuid).Id));
    }

    [Fact]
    public void ContentCanUnloadReloadAndDoesNotMutateSourceBlueprint()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateSource("village", "root");
        source.Entities[0].Children.Add(new EntityBlueprint { Name = "child" });
        var serializedBefore = DreambitJson.Serialize(source);

        var first = scene.LoadAdditive(source);
        var firstId = first.RootEntities[0].Id;
        Assert.True(scene.Unload(first));
        var second = scene.LoadAdditive(source);

        Assert.NotEqual(firstId, second.RootEntities[0].Id);
        Assert.Equal(serializedBefore, DreambitJson.Serialize(source));
    }

    [Fact]
    public void BoxedBlueprintUsesMaterializedGuidNamespaceAndUnloadsCompleteHierarchy()
    {
        using var scene = new AdditiveTestScene();
        var linkedRootGuid = Guid.NewGuid();
        var linkedChildGuid = Guid.NewGuid();
        var markerGuid = Guid.NewGuid();
        var linked = new EntityBlueprint
        {
            Name = "linked-root",
            Guid = linkedRootGuid,
            Children = [new EntityBlueprint { Name = "linked-child", Guid = linkedChildGuid }]
        };
        var source = new SceneBlueprint
        {
            Name = "boxed",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "marker",
                    Guid = markerGuid,
                    BlueprintInstance = new BlueprintInstanceReference
                    {
                        AssetId = Guid.NewGuid(),
                        AssetName = "Blueprints/Linked"
                    }
                }
            ]
        };

        var instance = scene.LoadAdditive(
            source,
            new SceneContentLoadOptions { BlueprintInstanceResolver = _ => linked });

        Assert.Equal(2, instance.OwnedEntities.Count);
        Assert.True(instance.EntitiesBySourceGuid.ContainsKey(markerGuid));
        Assert.False(instance.EntitiesBySourceGuid.ContainsKey(linkedRootGuid));
        Assert.Single(instance.RootEntities[0].Children);

        Assert.True(scene.Unload(instance));
        Assert.Null(scene.FindEntity("linked-root"));
        Assert.Null(scene.FindEntity("linked-child"));
    }

    [Fact]
    public void DynamicOwnedEntitiesUnloadWhilePersistentDescendantsSurvive()
    {
        using var scene = new AdditiveTestScene();
        var instance = scene.LoadAdditive(CreateSource("content", "owned-parent"));
        var dynamicOwned = instance.CreateEntity("dynamic-owned");
        var adopted = scene.CreateEntity("adopted");
        instance.TrackEntity(adopted);
        var persistentChild = scene.CreateEntity("persistent-child");
        persistentChild.Parent = instance.RootEntities[0];

        Assert.True(scene.Unload(instance));

        Assert.Null(scene.FindEntity(dynamicOwned.Id));
        Assert.Null(scene.FindEntity(adopted.Id));
        Assert.Same(persistentChild, scene.FindEntity(persistentChild.Id));
        Assert.Null(persistentChild.Parent);
    }

    [Fact]
    public void TrackEntityIsAtomicWhenDescendantBelongsToAnotherInstance()
    {
        using var scene = new AdditiveTestScene();
        var a = scene.LoadAdditive(CreateSource("a", "a-root"));
        var b = scene.LoadAdditive(CreateSource("b", "b-root"));
        var persistentParent = scene.CreateEntity("persistent-parent");
        b.RootEntities[0].Parent = persistentParent;

        Assert.Throws<InvalidOperationException>(() => a.TrackEntity(persistentParent));

        Assert.Null(persistentParent.ContentOwner);
        Assert.Same(b, b.RootEntities[0].ContentOwner);
        Assert.DoesNotContain(persistentParent, a.OwnedEntities);
    }

    [Fact]
    public void OnCreatedHelperEntityInheritsProvisionalOwnership()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateSource("helpers", "owner");
        source.Entities[0].Components.Add(Component<AdditiveHelperSpawner>());

        var instance = scene.LoadAdditive(source);
        var spawner = instance.RootEntities[0].GetComponent<AdditiveHelperSpawner>();

        Assert.NotNull(spawner.Helper);
        Assert.Contains(spawner.Helper, instance.OwnedEntities);
        Assert.True(scene.Unload(instance));
        Assert.Null(scene.FindEntity(spawner.Helper.Id));
    }

    [Fact]
    public void AdditiveSettingsAreOptInAndFailedLoadRestoresPreviousSettings()
    {
        using var scene = new AdditiveTestScene();
        scene.ApplySettings(new SceneSettings { AmbientLightIntensity = 0.8f, Exposure = 1.4f });
        var source = CreateSource("settings", "settings-root");
        source.Settings = new SceneSettings { AmbientLightIntensity = 0.2f, Exposure = 0.5f };

        var defaultLoad = scene.LoadAdditive(source);
        Assert.Equal(0.8f, scene.Settings.AmbientLightIntensity);
        Assert.Equal(1.4f, scene.Settings.Exposure);
        scene.Unload(defaultLoad);

        var explicitLoad = scene.LoadAdditive(
            source,
            new SceneContentLoadOptions { ApplySceneSettings = true });
        Assert.Equal(0.2f, scene.Settings.AmbientLightIntensity);
        Assert.Equal(0.5f, scene.Settings.Exposure);
        scene.Unload(explicitLoad);
        Assert.Equal(0.2f, scene.Settings.AmbientLightIntensity);
        Assert.Equal(0.5f, scene.Settings.Exposure);

        scene.ApplySettings(new SceneSettings { AmbientLightIntensity = 0.7f, Exposure = 1.2f });
        source.Entities[0].Components.Add(Component<AdditiveConstructionFailureComponent>());
        AdditiveConstructionFailureComponent.FailConstruction = true;
        try
        {
            Assert.ThrowsAny<Exception>(() => scene.LoadAdditive(
                source,
                new SceneContentLoadOptions { ApplySceneSettings = true }));
        }
        finally
        {
            AdditiveConstructionFailureComponent.FailConstruction = false;
        }

        Assert.Equal(0.7f, scene.Settings.AmbientLightIntensity);
        Assert.Equal(1.2f, scene.Settings.Exposure);
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("settings-root"));
    }

    [Fact]
    public void LoadFailureRollsBackEntitiesCreatedByCleanupCallbacks()
    {
        using var scene = new AdditiveTestScene();
        var source = CreateSource("cleanup-spawn", "failing-root");
        source.Entities[0].Components =
        [
            Component<AdditiveSpawnOnDisposeComponent>(),
            Component<AdditiveConstructionFailureComponent>()
        ];
        AdditiveConstructionFailureComponent.FailConstruction = true;
        try
        {
            Assert.ThrowsAny<Exception>(() => scene.LoadAdditive(source));
        }
        finally
        {
            AdditiveConstructionFailureComponent.FailConstruction = false;
        }

        Assert.Empty(scene.ContentInstances);
        Assert.Empty(scene.GetAllEntities());
    }

    [Fact]
    public void CleanupFailureDoesNotPreventLaterOwnedEntityCleanup()
    {
        using var scene = new AdditiveTestScene();
        AdditiveCleanupProbeComponent.DisposeCount = 0;
        var source = new SceneBlueprint
        {
            Name = "cleanup",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "throws",
                    Components = [Component<AdditiveThrowingDisposeComponent>()]
                },
                new EntityBlueprint
                {
                    Name = "probe",
                    Components = [Component<AdditiveCleanupProbeComponent>()]
                }
            ]
        };
        var instance = scene.LoadAdditive(source);

        Assert.Throws<AggregateException>(() => scene.Unload(instance));

        Assert.False(instance.IsLoaded);
        Assert.Empty(instance.OwnedEntities);
        Assert.Equal(1, AdditiveCleanupProbeComponent.DisposeCount);
        Assert.Null(scene.FindEntity("throws"));
        Assert.Null(scene.FindEntity("probe"));
        Assert.Empty(scene.ContentInstances);
    }

    [Fact]
    public void UnloadDuringEntityUpdateFinishesAtCurrentSafeBoundary()
    {
        using var scene = new AdditiveTestScene();
        var instance = scene.LoadAdditive(CreateSource("deferred", "owned"));
        var trigger = scene.CreateEntity("trigger").AttachComponent<UnloadDuringUpdateComponent>();
        trigger.Target = instance;

        scene.Tick();
        scene.Tick();

        Assert.Equal(1, trigger.UnloadCount);
        Assert.False(instance.IsLoaded);
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("owned"));
    }

    [Fact]
    public void UnloadRequestedByEntityCleanupFinishesAfterRepositoryDeletionWalk()
    {
        using var scene = new AdditiveTestScene();
        var persistent = scene.CreateEntity("persistent");
        var instance = scene.LoadAdditive(CreateSource("cleanup-boundary", "owned"));
        var trigger = scene.CreateEntity("cleanup-trigger")
            .AttachComponent<UnloadDuringDisposeComponent>();
        trigger.Target = instance;
        scene.FlushStructuralChanges();

        scene.DestroyEntity(trigger.Entity);
        scene.FlushStructuralChanges();

        Assert.Equal(1, trigger.UnloadCount);
        Assert.False(instance.IsLoaded);
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("owned"));
        Assert.Same(persistent, scene.FindEntity(persistent.Id));
    }

    [Fact]
    public void UnloadDuringDrawDefersPhysicalDisposalUntilRenderCallbackBoundaryEnds()
    {
        using var scene = new AdditiveTestScene();
        var instance = scene.LoadAdditive(CreateRenderCallbackSource("draw"));
        var unloadingDrawable = instance.RootEntities[0]
            .AttachComponent<UnloadDuringDrawDrawable>();
        var retainedDrawable = instance.RootEntities[1]
            .AttachComponent<AdditiveRenderLifetimeProbe>();
        unloadingDrawable.Target = instance;
        scene.FlushStructuralChanges();

        scene.RunAtContentCallbackBoundary(() =>
        {
            unloadingDrawable.Draw();

            Assert.False(instance.IsLoaded);
            Assert.False(unloadingDrawable.WasDisposed);
            Assert.False(unloadingDrawable.WasDisposedWhenUnloadReturned);
            Assert.False(retainedDrawable.WasDisposed);
            Assert.NotNull(unloadingDrawable.Entity);
            Assert.NotNull(retainedDrawable.Entity);
            Assert.False(unloadingDrawable.Entity.Enabled);
            Assert.True(unloadingDrawable.Entity.UpdatesSuspended);

            // This models the next entry in AlbedoPass's already-captured render list.
            retainedDrawable.Draw();
            Assert.True(retainedDrawable.DrawObservedLiveEntity);
        });

        Assert.True(unloadingDrawable.WasDisposed);
        Assert.True(retainedDrawable.WasDisposed);
        Assert.Null(unloadingDrawable.Entity);
        Assert.Null(retainedDrawable.Entity);
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("draw-unloader"));
        Assert.Null(scene.FindEntity("draw-retained"));
    }

    [Fact]
    public void UnloadDuringPreDrawDefersPhysicalDisposalUntilRenderCallbackBoundaryEnds()
    {
        using var scene = new AdditiveTestScene();
        var instance = scene.LoadAdditive(CreateRenderCallbackSource("predraw"));
        var unloadingDrawable = instance.RootEntities[0]
            .AttachComponent<UnloadDuringPreDrawDrawable>();
        var retainedDrawable = instance.RootEntities[1]
            .AttachComponent<AdditiveRenderLifetimeProbe>();
        unloadingDrawable.Target = instance;
        scene.FlushStructuralChanges();

        scene.RunAtContentCallbackBoundary(() =>
        {
            unloadingDrawable.PreDraw();

            Assert.False(instance.IsLoaded);
            Assert.False(unloadingDrawable.WasDisposed);
            Assert.False(unloadingDrawable.WasDisposedWhenUnloadReturned);
            Assert.False(retainedDrawable.WasDisposed);
            Assert.NotNull(unloadingDrawable.Entity);
            Assert.NotNull(retainedDrawable.Entity);

            // PreDraw also walks the render list captured by SortDrawablesPass.
            retainedDrawable.PreDraw();
            Assert.True(retainedDrawable.PreDrawObservedLiveEntity);
        });

        Assert.True(unloadingDrawable.WasDisposed);
        Assert.True(retainedDrawable.WasDisposed);
        Assert.Null(unloadingDrawable.Entity);
        Assert.Null(retainedDrawable.Entity);
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("predraw-unloader"));
        Assert.Null(scene.FindEntity("predraw-retained"));
    }

    [Fact]
    public void UnloadValidationAndSceneDisposalInvalidateHandles()
    {
        var scene = new AdditiveTestScene();
        using var other = new AdditiveTestScene();
        var instance = scene.LoadAdditive(CreateSource("lifetime", "root"));

        Assert.Throws<ArgumentException>(() => other.Unload(instance));
        Assert.True(scene.Unload(instance));
        Assert.False(scene.Unload(instance));

        var remaining = scene.LoadAdditive(CreateSource("remaining", "remaining-root"));
        scene.Dispose();

        Assert.False(remaining.IsLoaded);
        Assert.Empty(remaining.OwnedEntities);
        Assert.Throws<InvalidOperationException>(() => remaining.CreateEntity("late"));
    }

    [Fact]
    public void SceneServicesAndNetworkObjectsAreRejectedAcrossOwnershipPaths()
    {
        using var scene = new AdditiveTestScene();

        AssertForbiddenBlueprint<AdditiveTestSceneService>(scene);
        AssertForbiddenBlueprint<NetworkObject>(scene);
        AssertForbiddenBlueprint<RequiresAdditiveSceneServiceComponent>(scene);
        AssertForbiddenBlueprint<RequiresAdditiveNetworkObjectComponent>(scene);

        var nested = CreateSource("nested-network", "nested-root");
        nested.Entities[0].Children.Add(new EntityBlueprint
        {
            Name = "nested-network-object",
            Components = [Component<NetworkObject>()]
        });
        Assert.Throws<InvalidOperationException>(() => scene.LoadAdditive(nested));

        var boxed = CreateSource("boxed-network", "boxed-marker");
        boxed.Entities[0].BlueprintInstance = new BlueprintInstanceReference
        {
            AssetId = Guid.NewGuid(),
            AssetName = "Blueprints/Networked"
        };
        Assert.Throws<InvalidOperationException>(() => scene.LoadAdditive(
            boxed,
            new SceneContentLoadOptions
            {
                BlueprintInstanceResolver = _ => new EntityBlueprint
                {
                    Name = "boxed-network-object",
                    Components = [Component<NetworkObject>()]
                }
            }));

        var instance = scene.LoadAdditive(CreateSource("safe", "owned"));
        var owned = instance.RootEntities[0];
        Assert.Throws<InvalidOperationException>(() => owned.AttachComponent<AdditiveTestSceneService>());
        Assert.Throws<InvalidOperationException>(() => owned.AttachComponent<NetworkObject>());
        Assert.Throws<InvalidOperationException>(() =>
            owned.AttachComponent<RequiresAdditiveSceneServiceComponent>());
        Assert.Null(owned.GetComponent<RequiresAdditiveSceneServiceComponent>());
        Assert.Null(owned.GetComponent<AdditiveTestSceneService>());
        Assert.Throws<InvalidOperationException>(() =>
            instance.CreateEntity(new EntityBlueprint
            {
                Name = "network-dynamic",
                Components = [Component<NetworkObject>()]
            }));
        Assert.Throws<InvalidOperationException>(() =>
            instance.CreateEntity(new EntityBlueprint
            {
                Name = "service-dynamic",
                Components = [Component<AdditiveTestSceneService>()]
            }));

        var adoptRoot = scene.CreateEntity("adopt-root");
        var adoptChild = scene.CreateEntity("adopt-child");
        adoptChild.Parent = adoptRoot;
        adoptChild.AttachComponent<NetworkObject>();

        Assert.Throws<InvalidOperationException>(() => instance.TrackEntity(adoptRoot));
        Assert.Null(adoptRoot.ContentOwner);
        Assert.Null(adoptChild.ContentOwner);

        var serviceHost = scene.CreateEntity("persistent-service-host");
        serviceHost.AttachComponent<AdditiveTestSceneService>();
        Assert.Throws<InvalidOperationException>(() => instance.TrackEntity(serviceHost));
        Assert.Null(serviceHost.ContentOwner);
    }

    [Fact]
    public void LoadIntoSelfStillPreservesIdsAndAppliesSettings()
    {
        using var scene = new AdditiveTestScene();
        var sourceGuid = Guid.NewGuid();
        var source = new SceneBlueprint
        {
            Name = "legacy",
            Settings = new SceneSettings { AmbientLightIntensity = 0.35f },
            Entities = [new EntityBlueprint { Name = "legacy-root", Guid = sourceGuid }]
        };

        scene.LoadIntoSelf(source);

        Assert.NotNull(scene.FindEntity(sourceGuid));
        Assert.Equal(0.35f, scene.Settings.AmbientLightIntensity);
        Assert.Empty(scene.ContentInstances);
    }

    [Fact]
    public void EditorLoadingAndSerializationCannotPersistRuntimeContent()
    {
        using (var editorScene = new EditorScene())
        {
            Assert.Throws<InvalidOperationException>(() =>
                editorScene.LoadAdditive(CreateSource("editor", "runtime")));
            Assert.Empty(editorScene.GetAllEntities());
        }

        using var scene = new AdditiveTestScene();
        var persistent = scene.CreateEntity("persistent");
        var instance = scene.LoadAdditive(CreateSource("runtime", "runtime-root"));
        var captured = SceneDocumentSerializer.Capture(
            scene,
            new SceneBlueprint { Name = "source" },
            "source");

        Assert.Single(captured.Entities);
        Assert.Equal(persistent.Id, captured.Entities[0].Guid);
        Assert.DoesNotContain("ContentOwner", DreambitJson.Serialize(captured), StringComparison.Ordinal);

        var holder = persistent.AttachComponent<AdditiveReferenceComponent>();
        holder.Target = instance.RootEntities[0];
        Assert.Throws<InvalidOperationException>(() => SceneDocumentSerializer.Capture(
            scene,
            new SceneBlueprint { Name = "source" },
            "source"));

        scene.Unload(instance);
        Assert.Throws<InvalidOperationException>(() => SceneDocumentSerializer.Capture(
            scene,
            new SceneBlueprint { Name = "source" },
            "source"));
    }

    private static void AssertForbiddenBlueprint<T>(Scene scene) where T : Component
    {
        var source = CreateSource(typeof(T).Name, "forbidden");
        source.Entities[0].Components.Add(Component<T>());
        Assert.Throws<InvalidOperationException>(() => scene.LoadAdditive(source));
        Assert.Empty(scene.ContentInstances);
        Assert.Null(scene.FindEntity("forbidden"));
    }

    private static SceneBlueprint CreateSource(string name, string rootName) => new()
    {
        Name = name,
        Entities = [new EntityBlueprint { Name = rootName }]
    };

    private static SceneBlueprint CreateRenderCallbackSource(string name) => new()
    {
        Name = name,
        Entities =
        [
            new EntityBlueprint { Name = $"{name}-unloader" },
            new EntityBlueprint { Name = $"{name}-retained" }
        ]
    };

    private static ComponentBlueprint Component<T>(params (string Name, string Value)[] properties)
        where T : Component
    {
        return new ComponentBlueprint
        {
            Type = SceneDocumentSerializer.GetComponentTypeId(typeof(T)),
            Properties = properties.ToDictionary(
                item => item.Name,
                item => (JToken)new JValue(item.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

public sealed class AdditiveTestScene : Scene
{
    internal override void InitializeInternals()
    {
    }
}

public sealed class AdditiveReferenceComponent : Component
{
    [DreambitSerialize]
    public Entity? Target { get; set; }
}

public sealed class AdditiveHelperSpawner : Component
{
    public Entity Helper { get; private set; } = null!;

    public override void OnCreated()
    {
        Helper = Scene.CreateEntity("materialization-helper");
    }
}

public sealed class AdditiveConstructionFailureComponent : Component
{
    public AdditiveConstructionFailureComponent()
    {
        if (FailConstruction)
            throw new InvalidOperationException("Intentional additive construction failure.");
    }

    public static bool FailConstruction { get; set; }
}

public sealed class AdditiveThrowingDisposeComponent : Component
{
    protected override void OnDisposing()
    {
        throw new InvalidOperationException("Intentional additive cleanup failure.");
    }
}

public sealed class AdditiveSpawnOnDisposeComponent : Component
{
    protected override void OnDisposing()
    {
        Scene.CreateEntity("cleanup-created-helper");
    }
}

public sealed class AdditiveCleanupProbeComponent : Component
{
    public static int DisposeCount { get; set; }

    protected override void OnDisposing()
    {
        DisposeCount++;
    }
}

public sealed class UnloadDuringUpdateComponent : Component
{
    public SceneContentInstance? Target { get; set; }
    public int UnloadCount { get; private set; }

    public override void OnUpdate()
    {
        if (Target is not { IsLoaded: true } target)
            return;
        if (Scene.Unload(target))
            UnloadCount++;
    }
}

public sealed class UnloadDuringDisposeComponent : Component
{
    public SceneContentInstance? Target { get; set; }
    public int UnloadCount { get; private set; }

    public override void OnDestroyed()
    {
        if (Target is { IsLoaded: true } target && Scene.Unload(target))
            UnloadCount++;
    }
}

public sealed class UnloadDuringDrawDrawable : DrawableComponent
{
    public SceneContentInstance? Target { get; set; }
    public bool WasDisposed { get; private set; }
    public bool WasDisposedWhenUnloadReturned { get; private set; }

    protected override void OnDraw()
    {
        if (Target is { IsLoaded: true } target)
            Scene.Unload(target);
        WasDisposedWhenUnloadReturned = WasDisposed;
    }

    protected override void OnDisposing()
    {
        WasDisposed = true;
    }
}

public sealed class UnloadDuringPreDrawDrawable : DrawableComponent
{
    public SceneContentInstance? Target { get; set; }
    public bool WasDisposed { get; private set; }
    public bool WasDisposedWhenUnloadReturned { get; private set; }

    public override void OnPreDraw()
    {
        if (Target is { IsLoaded: true } target)
            Scene.Unload(target);
        WasDisposedWhenUnloadReturned = WasDisposed;
    }

    protected override void OnDisposing()
    {
        WasDisposed = true;
    }
}

public sealed class AdditiveRenderLifetimeProbe : DrawableComponent
{
    public bool WasDisposed { get; private set; }
    public bool DrawObservedLiveEntity { get; private set; }
    public bool PreDrawObservedLiveEntity { get; private set; }

    protected override void OnDraw()
    {
        DrawObservedLiveEntity = !WasDisposed && Entity is not null;
    }

    public override void OnPreDraw()
    {
        PreDrawObservedLiveEntity = !WasDisposed && Entity is not null;
    }

    protected override void OnDisposing()
    {
        WasDisposed = true;
    }
}

public sealed class AdditiveTestSceneService : SceneServiceComponent
{
}

[Require(typeof(AdditiveTestSceneService))]
public sealed class RequiresAdditiveSceneServiceComponent : Component
{
}

[Require(typeof(NetworkObject))]
public sealed class RequiresAdditiveNetworkObjectComponent : Component
{
}
