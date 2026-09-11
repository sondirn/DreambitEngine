using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Type = System.Type;

namespace Dreambit.ECS;

public class ComponentRepository
{
    private static readonly ConcurrentDictionary<Type, bool>
        HasOnUpdateOverrideByType = [];

    private static readonly ConcurrentDictionary<Type, bool>
        HasOnPhysicsUpdateOverrideByType = [];

    private readonly HashSet<Component> _attachedComponents = [];
    private readonly HashSet<Component> _componentsToAttach = [];
    private readonly HashSet<Component> _componentsToDetach = [];

    private readonly Logger<ComponentRepository> _logger = new();
    private readonly HashSet<Component> _physicsUpdatableComponents = [];

    private readonly HashSet<Component> _updatableComponents = [];

    private Scene _scene;

    public ComponentRepository(
        Scene scene)
    {
        _scene = scene;
    }

    public void AttachComponent<T>(
        T component)
        where T : Component
    {
        if (component == null)
            return;

        if (_componentsToAttach.Contains(component))
            return;

        if (_attachedComponents.Contains(component))
            return;

        if (_componentsToDetach.Contains(component))
            return;

        _componentsToAttach.Add(component);
    }

    public void DetachComponent<T>(
        T component)
        where T : Component
    {
        if (component == null)
        {
            _logger.Warn("Could not destroy component, component is null");

            return;
        }

        if (component is SceneServiceComponent sceneService)
            _scene?.Services.EnsureCanRemove(sceneService);

        if (_componentsToDetach.Contains(component))
        {
            _logger.Trace("ComponentList: {0} is already being removed", component.GetType().Name);

            return;
        }

        if (_componentsToAttach.Remove(component))
        {
            DestroyComponentNow(
                component,
                false);

            return;
        }

        if (_attachedComponents.Contains(component))
            _componentsToDetach.Add(component);
    }

    internal void DestroyAllComponentsNow()
    {
        var cleanupErrors = new List<Exception>();

        var components =
            new List<Component>(
                _componentsToAttach.Count +
                _attachedComponents.Count);

        foreach (var component in _componentsToAttach)
            if (!components.Contains(component))
                components.Add(component);

        foreach (var component in _attachedComponents)
            if (!components.Contains(component))
                components.Add(component);

        components.Sort(
            CompareForDestruction);

        foreach (var component in components)
        {
            try
            {
                DestroyComponentNow(
                    component,
                    _attachedComponents.Contains(component));
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        }

        // Ownership is gone regardless of individual cleanup failures.
        _componentsToAttach.Clear();
        _attachedComponents.Clear();
        _updatableComponents.Clear();
        _physicsUpdatableComponents.Clear();
        _componentsToDetach.Clear();

        if (cleanupErrors.Count > 0)
            throw new AggregateException(
                "One or more components failed while being destroyed.",
                cleanupErrors);
    }

    private int CompareForDestruction(
        Component left,
        Component right)
    {
        var leftService =
            left as SceneServiceComponent;

        var rightService =
            right as SceneServiceComponent;

        if (leftService is null)
            return rightService is null
                ? 0
                : -1;

        if (rightService is null)
            return 1;

        return _scene.Services
            .GetDestructionOrder(rightService)
            .CompareTo(
                _scene.Services
                    .GetDestructionOrder(leftService));
    }

    public T GetComponent<T>()
        where T : Component
    {
        foreach (var component in _attachedComponents)
            if (component is T typed)
                return typed;

        foreach (var component in _componentsToAttach)
            if (component is T typed)
                return typed;

        return null;
    }

    public bool ComponentOfTypeExists(
        Type type)
    {
        if (type == null)
            return false;

        foreach (var component in _attachedComponents)
            if (type.IsAssignableFrom(
                    component.GetType()))
                return true;

        foreach (var component in _componentsToAttach)
            if (type.IsAssignableFrom(
                    component.GetType()))
                return true;

        return false;
    }

    public Component GetComponent(
        Type type)
    {
        if (type == null)
            return null;

        if (!typeof(Component)
                .IsAssignableFrom(type))
            return null;

        foreach (var component in _attachedComponents)
            if (component.GetType() == type)
                return component;

        foreach (var component in _componentsToAttach)
            if (component.GetType() == type)
                return component;

        foreach (var component in _attachedComponents)
            if (type.IsAssignableFrom(
                    component.GetType()))
                return component;

        foreach (var component in _componentsToAttach)
            if (type.IsAssignableFrom(
                    component.GetType()))
                return component;

        return null;
    }

    public IReadOnlyCollection<Component>
        GetAllAttachedComponents()
    {
        return _attachedComponents;
    }

    public IReadOnlyCollection<Component>
        GetAllActiveComponents()
    {
        var list =
            new List<Component>(
                _attachedComponents.Count);

        foreach (var component in _attachedComponents)
            if (component.Enabled)
                list.Add(component);

        return list;
    }

    public IReadOnlyCollection<Component>
        GetAllComponents()
    {
        var list =
            new List<Component>(
                _componentsToAttach.Count +
                _attachedComponents.Count);

        var seen =
            new HashSet<Component>();

        foreach (var component in _componentsToAttach)
            if (seen.Add(component))
                list.Add(component);

        foreach (var component in _attachedComponents)
            if (seen.Add(component))
                list.Add(component);

        return list;
    }

    public IReadOnlyCollection<Component>
        GetAllComponentsToAttach()
    {
        var list =
            new List<Component>(
                _componentsToAttach.Count);

        list.AddRange(
            _componentsToAttach);

        return list;
    }

    public void ClearLists()
    {
        _scene = null;

        _attachedComponents.Clear();
        _updatableComponents.Clear();
        _physicsUpdatableComponents.Clear();
        _componentsToAttach.Clear();
        _componentsToDetach.Clear();
    }

    public void UpdateComponents()
    {
        foreach (var component in _updatableComponents)
            if (component.Enabled)
                component.Update();
    }

    internal void EditorUpdateComponents()
    {
        foreach (var component in _attachedComponents)
            if (component.Enabled)
                component.EditorUpdate();
    }

    public void PhysicsUpdateComponents()
    {
        /*
         * Only components whose type actually overrides OnPhysicsUpdate
         * participate in the fixed physics loop.
         */
        foreach (var component in _physicsUpdatableComponents)
            if (component.Enabled)
                component.PhysicsUpdate();
    }

    public void UpdateLists()
    {
        // Handle creation.
        foreach (var component in _componentsToAttach)
        {
            if (!_attachedComponents.Add(component))
                continue;

            if (component is DrawableComponent drawable &&
                _scene != null)
                _scene.Drawables.Add(drawable);

            if (_scene?.ExecutionMode ==
                SceneExecutionMode.Runtime)
                component.AddToEntity();

            var componentType =
                component.GetType();

            if (OverridesOnUpdate(componentType))
                _updatableComponents.Add(component);

            if (OverridesOnPhysicsUpdate(componentType))
                _physicsUpdatableComponents.Add(component);
        }

        _componentsToAttach.Clear();

        // Handle deletion.
        var cleanupErrors = new List<Exception>();

        foreach (var component in _componentsToDetach)
        {
            if (!_attachedComponents.Remove(component))
                continue;

            _updatableComponents.Remove(component);
            _physicsUpdatableComponents.Remove(component);

            try
            {
                DestroyComponentNow(
                    component,
                    true);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        }

        _componentsToDetach.Clear();

        if (cleanupErrors.Count > 0)
            throw new AggregateException(
                "One or more components failed while being detached.",
                cleanupErrors);
    }

    private void DestroyComponentNow(
        Component component,
        bool removeDrawable)
    {
        var cleanupErrors = new List<Exception>();

        if (removeDrawable &&
            component is DrawableComponent drawable &&
            _scene != null)
            TryCleanup(
                cleanupErrors,
                () => _scene.Drawables.Remove(drawable));

        TryCleanup(
            cleanupErrors,
            component.RemoveFromEntity);

        TryCleanup(
            cleanupErrors,
            component.Destroy);

        // Preserve the existing lifetime contract: OnDisposing does not
        // depend on Entity/Scene remaining attached.
        component.Entity = null;

        TryCleanup(
            cleanupErrors,
            component.Dispose);

        if (cleanupErrors.Count > 0)
            throw new AggregateException(
                $"Component '{component.GetType().FullName}' failed during cleanup.",
                cleanupErrors);
    }

    private static void TryCleanup(
        List<Exception> errors,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static bool OverridesOnUpdate(
        Type componentType)
    {
        return HasOnUpdateOverrideByType.GetOrAdd(
            componentType,
            static type =>
            {
                var method =
                    type.GetMethod(
                        nameof(Component.OnUpdate),
                        BindingFlags.Instance |
                        BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);

                return
                    method is not null &&
                    method.DeclaringType !=
                    typeof(Component) &&
                    method.GetBaseDefinition()
                        .DeclaringType ==
                    typeof(Component);
            });
    }

    private static bool OverridesOnPhysicsUpdate(
        Type componentType)
    {
        return HasOnPhysicsUpdateOverrideByType.GetOrAdd(
            componentType,
            static type =>
            {
                var method =
                    type.GetMethod(
                        nameof(Component.OnPhysicsUpdate),
                        BindingFlags.Instance |
                        BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);

                return
                    method is not null &&
                    method.DeclaringType !=
                    typeof(Component) &&
                    method.GetBaseDefinition()
                        .DeclaringType ==
                    typeof(Component);
            });
    }

    internal static void ReleaseAssembly(
        Assembly assembly)
    {
        foreach (var type in
                 HasOnUpdateOverrideByType.Keys
                     .Where(type =>
                         type.Assembly == assembly)
                     .ToArray())
            HasOnUpdateOverrideByType.TryRemove(
                type,
                out _);

        foreach (var type in
                 HasOnPhysicsUpdateOverrideByType.Keys
                     .Where(type =>
                         type.Assembly == assembly)
                     .ToArray())
            HasOnPhysicsUpdateOverrideByType.TryRemove(
                type,
                out _);
    }
}
