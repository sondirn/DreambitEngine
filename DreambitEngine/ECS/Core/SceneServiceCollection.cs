using System;
using System.Collections.Generic;
using System.Reflection;
using Dreambit.Networking;

namespace Dreambit.ECS;

/// <summary>
///     Stores the unique component-backed services owned by a scene.
/// </summary>
public sealed class SceneServiceCollection
{
    private readonly List<SceneServiceComponent> _activationOrder = [];
    private readonly List<SceneServiceComponent> _registrationOrder = [];
    private readonly Scene _scene;
    private readonly Dictionary<Type, SceneServiceComponent> _services = [];

    private SceneServiceCollectionState _state;

    internal SceneServiceCollection(Scene scene)
    {
        _scene = scene;
    }

    /// <summary>Gets a required service from this scene.</summary>
    public T Get<T>() where T : SceneServiceComponent
    {
        if (TryGet<T>(out var service))
            return service;

        throw new InvalidOperationException(
            $"Scene '{_scene.GetType().FullName}' does not contain " +
            $"service '{typeof(T).FullName}'.");
    }

    /// <summary>Attempts to get a service from this scene.</summary>
    public bool TryGet<T>(out T service) where T : SceneServiceComponent
    {
        if (_services.TryGetValue(
                typeof(T),
                out var value))
        {
            service = (T)value;
            return true;
        }

        service = null;
        return false;
    }

    internal bool IsActive =>
        _state == SceneServiceCollectionState.Active;

    internal void Register(SceneServiceComponent service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (_state != SceneServiceCollectionState.Accepting)
            throw new InvalidOperationException(
                $"Scene service '{service.GetType().FullName}' cannot be added " +
                "after the scene service collection has been activated.");

        var serviceType =
            service.GetType();

        if (!_services.TryAdd(
                serviceType,
                service))
        {
            throw new InvalidOperationException(
                $"Scene '{_scene.GetType().FullName}' already contains " +
                $"service '{serviceType.FullName}'.");
        }

        _registrationOrder.Add(service);
    }

    internal void Unregister(SceneServiceComponent service)
    {
        if (service is null)
            return;

        var serviceType =
            service.GetType();

        if (_services.TryGetValue(
                serviceType,
                out var current) &&
            ReferenceEquals(
                current,
                service))
        {
            _services.Remove(serviceType);
        }

        RemoveByReference(
            _registrationOrder,
            service);

        RemoveByReference(
            _activationOrder,
            service);
    }

    internal void ActivateAll()
    {
        if (_state == SceneServiceCollectionState.Active)
            return;

        if (_state != SceneServiceCollectionState.Accepting)
            throw new InvalidOperationException(
                "The scene service collection can no longer be activated.");

        ValidateServiceHosts();

        _activationOrder.Clear();
        _activationOrder.AddRange(
            ResolveActivationOrder());

        _state = SceneServiceCollectionState.Active;

        if (_scene.ExecutionMode != SceneExecutionMode.Runtime)
            return;

        for (var i = 0;
             i < _activationOrder.Count;
             i++)
        {
            _activationOrder[i]
                .ServicesReady();
        }
    }

    internal void StopAll()
    {
        if (_state is SceneServiceCollectionState.Stopping or
            SceneServiceCollectionState.Stopped)
        {
            return;
        }

        var services =
            _activationOrder.Count > 0
                ? _activationOrder
                : _registrationOrder;

        _state = SceneServiceCollectionState.Stopping;

        if (_scene.ExecutionMode == SceneExecutionMode.Runtime)
            for (var i = services.Count - 1;
                 i >= 0;
                 i--)
            {
                services[i]
                    .ServicesStopping();
            }

        _state = SceneServiceCollectionState.Stopped;
    }

    internal void EnsureCanRemove(Entity entity)
    {
        if (!IsActive ||
            entity is null ||
            !entity.ContainsSceneServiceInHierarchy())
        {
            return;
        }

        throw new InvalidOperationException(
            $"Entity '{entity.Name}' owns an active scene service and cannot " +
            "be destroyed before its scene ends.");
    }

    private void ValidateServiceHosts()
    {
        for (var i = 0;
             i < _registrationOrder.Count;
             i++)
        {
            var service =
                _registrationOrder[i];

            foreach (var component in
                     service.Entity.GetAllComponents())
            {
                if (component is SceneServiceComponent or
                    NetworkObject
                    {
                        Presence: NetworkPresence.Replicated
                    })
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Scene service '{service.GetType().FullName}' must be placed " +
                    "on an entity containing only scene service components and, " +
                    "optionally, one replicated NetworkObject component.");
            }
        }
    }

    internal void EnsureCanRemove(SceneServiceComponent service)
    {
        if (!IsActive)
            return;

        throw new InvalidOperationException(
            $"Scene service '{service.GetType().FullName}' cannot be detached " +
            "before its scene ends.");
    }

    internal int GetDestructionOrder(SceneServiceComponent service)
    {
        for (var i = 0;
             i < _activationOrder.Count;
             i++)
        {
            if (ReferenceEquals(
                    _activationOrder[i],
                    service))
            {
                return i;
            }
        }

        for (var i = 0;
             i < _registrationOrder.Count;
             i++)
        {
            if (ReferenceEquals(
                    _registrationOrder[i],
                    service))
            {
                return i;
            }
        }

        return -1;
    }

    private IReadOnlyList<SceneServiceComponent>
        ResolveActivationOrder()
    {
        var result =
            new List<SceneServiceComponent>(
                _registrationOrder.Count);

        var remaining =
            new List<SceneServiceComponent>(
                _registrationOrder);

        var resolvedTypes =
            new HashSet<Type>();

        while (remaining.Count > 0)
        {
            var madeProgress =
                false;

            for (var i = 0;
                 i < remaining.Count;)
            {
                var service =
                    remaining[i];

                if (!DependenciesAreResolved(
                        service,
                        resolvedTypes))
                {
                    i++;
                    continue;
                }

                result.Add(service);
                resolvedTypes.Add(
                    service.GetType());

                remaining.RemoveAt(i);
                madeProgress = true;
            }

            if (madeProgress)
                continue;

            var unresolvedTypes =
                new List<string>(
                    remaining.Count);

            for (var i = 0;
                 i < remaining.Count;
                 i++)
            {
                unresolvedTypes.Add(
                    remaining[i]
                        .GetType()
                        .FullName ??
                    remaining[i]
                        .GetType()
                        .Name);
            }

            throw new InvalidOperationException(
                "Scene service dependencies contain a cycle: " +
                string.Join(
                    ", ",
                    unresolvedTypes));
        }

        return result;
    }

    private bool DependenciesAreResolved(
        SceneServiceComponent service,
        HashSet<Type> resolvedTypes)
    {
        var attributes =
            service.GetType()
                .GetCustomAttributes<RequiresSceneServiceAttribute>(true);

        foreach (var attribute in attributes)
        {
            if (!_services.ContainsKey(
                    attribute.ServiceType))
            {
                throw new InvalidOperationException(
                    $"Scene service '{service.GetType().FullName}' requires " +
                    $"missing service '{attribute.ServiceType.FullName}'.");
            }

            if (!resolvedTypes.Contains(
                    attribute.ServiceType))
            {
                return false;
            }
        }

        return true;
    }

    private static void RemoveByReference(
        List<SceneServiceComponent> services,
        SceneServiceComponent service)
    {
        for (var i = 0;
             i < services.Count;
             i++)
        {
            if (!ReferenceEquals(
                    services[i],
                    service))
            {
                continue;
            }

            services.RemoveAt(i);
            return;
        }
    }

    private enum SceneServiceCollectionState
    {
        Accepting,
        Active,
        Stopping,
        Stopped
    }
}
