using System.Reflection;
using Dreambit.ECS;
using Dreambit.Networking;

namespace Dreambit.Editor.Tests;

public sealed class SceneServiceTests
{
    [Fact]
    public void SameServiceTypeCanBelongToOverlappingScenes()
    {
        using var firstScene = new ServiceTestScene();
        using var secondScene = new ServiceTestScene();

        var first = firstScene
            .CreateEntity("first-services")
            .AttachComponent<IndependentService>();

        var second = secondScene
            .CreateEntity("second-services")
            .AttachComponent<IndependentService>();

        Assert.Same(
            first,
            firstScene.Services.Get<IndependentService>());

        Assert.Same(
            second,
            secondScene.Services.Get<IndependentService>());

        Assert.NotSame(
            first,
            second);
    }

    [Fact]
    public void DuplicateServiceTypeInOneSceneIsRejected()
    {
        using var scene = new ServiceTestScene();

        scene.CreateEntity("first-services")
            .AttachComponent<IndependentService>();

        var secondEntity =
            scene.CreateEntity("second-services");

        var exception =
            Assert.Throws<InvalidOperationException>(
                secondEntity.AttachComponent<IndependentService>);

        Assert.Contains(
            typeof(IndependentService).FullName!,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependenciesBecomeReadyBeforeTheirConsumers()
    {
        using var scene = new ServiceTestScene();
        var serviceEntity =
            scene.CreateEntity("services");

        var consumer =
            serviceEntity.AttachComponent<DependentService>();

        var dependency =
            serviceEntity.AttachComponent<DependencyService>();

        ActivateServices(scene);

        Assert.True(dependency.IsReady);
        Assert.True(consumer.SawReadyDependency);
    }

    [Fact]
    public void OrdinaryComponentsAreDestroyedBeforeServicesStop()
    {
        var scene = new ServiceTestScene();

        var service = scene
            .CreateEntity("services")
            .AttachComponent<LifetimeService>();

        scene.CreateEntity("consumer")
            .AttachComponent<LifetimeConsumer>();

        ActivateServices(scene);

        scene.Dispose();

        Assert.Equal(
            [
                "consumer-destroyed",
                "service-stopping",
                "service-destroyed"
            ],
            service.Events);

        Assert.False(
            scene.Services.TryGet<LifetimeService>(out _));
    }

    [Fact]
    public void ActiveServiceEntityCannotBeDestroyedExplicitly()
    {
        using var scene = new ServiceTestScene();
        var serviceEntity =
            scene.CreateEntity("services");

        serviceEntity.AttachComponent<IndependentService>();
        ActivateServices(scene);

        Assert.Throws<InvalidOperationException>(
            () => Entity.Destroy(serviceEntity));

        Assert.Throws<InvalidOperationException>(
            serviceEntity.Dispose);

        Assert.Throws<InvalidOperationException>(
            serviceEntity
                .GetComponent<IndependentService>()
                .Dispose);

        Assert.False(
            Entity.IsDestroyed(serviceEntity));
    }

    [Fact]
    public void ServiceHostCannotContainOrdinaryComponents()
    {
        using var scene = new ServiceTestScene();
        var serviceEntity =
            scene.CreateEntity("services");

        serviceEntity.AttachComponent<IndependentService>();
        serviceEntity.AttachComponent<OrdinaryComponent>();

        var exception =
            Assert.Throws<TargetInvocationException>(
                () => ActivateServices(scene));

        Assert.IsType<InvalidOperationException>(
            exception.InnerException);
    }

    [Fact]
    public void ServiceHostCanContainReplicatedNetworkObject()
    {
        using var scene = new ServiceTestScene();
        var serviceEntity =
            scene.CreateEntity("services");

        var service =
            serviceEntity.AttachComponent<IndependentService>();

        var networkObject =
            serviceEntity.AttachComponent<NetworkObject>();

        ActivateServices(scene);

        Assert.Same(
            service,
            scene.Services.Get<IndependentService>());

        Assert.Same(
            networkObject,
            serviceEntity.GetComponent<NetworkObject>());
    }

    [Theory]
    [InlineData(NetworkPresence.ServerOnly)]
    [InlineData(NetworkPresence.ClientOnly)]
    public void ServiceHostRejectsRoleSpecificNetworkObject(
        NetworkPresence presence)
    {
        using var scene = new ServiceTestScene();
        var serviceEntity =
            scene.CreateEntity("services");

        serviceEntity.AttachComponent<IndependentService>();

        serviceEntity.AttachComponent<NetworkObject>()
            .Presence = presence;

        var exception =
            Assert.Throws<TargetInvocationException>(
                () => ActivateServices(scene));

        Assert.IsType<InvalidOperationException>(
            exception.InnerException);
    }

    private static void ActivateServices(Scene scene)
    {
        var method =
            typeof(SceneServiceCollection)
                .GetMethod(
                    "ActivateAll",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

        Assert.NotNull(method);

        method.Invoke(
            scene.Services,
            null);
    }
}

public sealed class ServiceTestScene : Scene
{
}

public sealed class IndependentService : SceneServiceComponent
{
}

public sealed class DependencyService : SceneServiceComponent
{
    public bool IsReady { get; private set; }

    public override void OnServicesReady()
    {
        IsReady = true;
    }
}

[RequiresSceneService(typeof(DependencyService))]
public sealed class DependentService : SceneServiceComponent
{
    public bool SawReadyDependency { get; private set; }

    public override void OnServicesReady()
    {
        SawReadyDependency = Scene.Services
            .Get<DependencyService>()
            .IsReady;
    }
}

public sealed class LifetimeService : SceneServiceComponent
{
    public List<string> Events { get; } = [];

    public override void OnServicesStopping()
    {
        Events.Add("service-stopping");
    }

    public override void OnDestroyed()
    {
        Events.Add("service-destroyed");
    }
}

public sealed class LifetimeConsumer : Component
{
    public override void OnDestroyed()
    {
        Scene.Services
            .Get<LifetimeService>()
            .Events.Add("consumer-destroyed");
    }
}

public sealed class OrdinaryComponent : Component
{
}
