using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

public class Entity : IDisposable
{
    public readonly Guid Id;
    private readonly List<Component> _blueprintComponentCreateOrder = [];
    private readonly List<Entity> _children = [];

    private readonly ILogger _logger = new Logger<Entity>();
    public string Name;

    private bool _alwaysUpdate;
    private bool _enabled;
    private bool _isDead;
    private bool _isDestroyed;
    private bool _isDisposed;

    private Entity? _parent;

    internal Entity(Guid id, string name, HashSet<string> tags, bool enabled, Scene scene)
    {
        Id = id;
        Name = name;
        _enabled = enabled;
        Scene = scene;

        if (tags == null)
            Tags = ["default"];
        else
            foreach (var tag in tags)
                Tags.Add(tag);

        if (Name == "entity")
            Name += $": {id}";

        ComponentRepository = new ComponentRepository(scene);
        Transform = new Transform(this);
    }

    private Entity()
    {
    }

    public bool IsFaulted { get; private set; }

    public Exception FaultException { get; private set; }

    public Component FaultSource { get; private set; }

    public string FaultCallback { get; private set; }

    private ComponentRepository ComponentRepository { get; }
    public Transform Transform { get; }
    public HashSet<string> Tags { get; } = [];
    internal Scene Scene { get; private set; }

    /// <summary>
    /// Runtime-only additive lifetime owner. Source Blueprints and editor serialization never
    /// read or write this field.
    /// </summary>
    internal SceneContentInstance? ContentOwner { get; set; }

    public Entity? Parent
    {
        get => _parent;
        set
        {
            if (_parent == value) return;
            SetParent(value);
        }
    }

    /// <summary>Direct children in hierarchy order.</summary>
    public IReadOnlyList<Entity> Children => _children;

    /// <summary>The entity's own enabled flag, excluding parent state.</summary>
    public bool LocallyEnabled => _enabled;

    /// <summary>The scene that owns this entity.</summary>
    public Scene OwningScene => Scene;

    /// <summary>True for transient tooling entities that must never be serialized as scene content.</summary>
    public bool IsEditorOnly { get; internal set; }

    /// <summary>
    /// Stable source identity for entities regenerated from a Tiled TMX map.
    /// </summary>
    public string TiledSourceKey { get; internal set; }

    public bool IsTiledGenerated => !string.IsNullOrWhiteSpace(TiledSourceKey);

    /// <summary>
    /// Runtime-only gate used while an owning subsystem completes transactional initialization.
    /// It is deliberately not serialized and does not change the entity's authored enabled state.
    /// </summary>
    internal bool UpdatesSuspended { get; set; }

    public bool IsImportedMapGenerated => IsTiledGenerated;

    public bool AlwaysUpdate
    {
        get => _alwaysUpdate;
        set
        {
            if (_alwaysUpdate == value) return;
            _alwaysUpdate = value;

            foreach (var child in _children)
                child.AlwaysUpdate = value;

            Scene.SetEntityAlwaysUpdate(this, value);
        }
    }

    public bool Enabled
    {
        get
        {
            if (IsFaulted)
                return false;

            if (Parent == null)
                return _enabled;

            return Parent.Enabled && _enabled;
        }
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;

            if (_enabled)
                OnEnabled();
            else
                OnDisabled();
        }
    }

    public void Dispose()
    {
        Scene?.Services.EnsureCanRemove(this);

        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Entity()
    {
        Dispose(false);
    }

    public static Entity Create(
        string name = "entity",
        HashSet<string> tags = null,
        bool enabled = true,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null,
        Guid? guidOverride = null)
    {
        var entity =
            Core.Instance.CurrentScene.CreateEntity(name, tags, enabled, createAt, eulerRotation, scale, guidOverride);

        entity.Transform.CaptureLastWorldPosition();
        return entity;
    }

    public static Entity Create(
        EntityBlueprint blueprint,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? rotation = null,
        Vector3? scale = null)
    {
        var entity = Core.Instance.CurrentScene.CreateEntity(
            blueprint,
            enabled,
            createAt,
            rotation,
            scale);

        entity.Transform.CaptureLastWorldPosition();
        return entity;
    }

    public static Entity CreateChildOf(
        Entity parent,
        string name = "entity",
        HashSet<string> tags = null,
        bool enabled = true)
    {
        var entity = Core.Instance.CurrentScene.CreateEntity(name, tags, enabled);
        entity.Parent = parent;

        entity.Transform.CaptureLastWorldPosition();
        return entity;
    }

    public static Entity FindByName(string name)
    {
        var entity = Core.Instance.CurrentScene.FindEntity(name);

        return entity;
    }

    public static Entity FindById(Guid iid)
    {
        var entity = Core.Instance.CurrentScene.FindEntity(iid);

        return entity;
    }

    public Entity FindChild(string name)
    {
        var children = GetChildren();

        foreach (var child in children)
            if (child.Name == name)
                return child;

        return null;
    }

    public List<Entity> GetChildren()
    {
        var result = new List<Entity>();

        foreach (var child in _children)
        {
            result.Add(child);
            result.AddRange(child.GetChildren());
        }

        return result;
    }

    public static void Destroy(Entity entity)
    {
        if (entity is null || entity._isDead || entity._isDestroyed)
            return;

        entity.Scene?.Services.EnsureCanRemove(entity);

        entity._isDead = true;

        var children = new Entity[entity._children.Count];

        entity._children.CopyTo(children);

        for (var i = 0; i < children.Length; i++)
            Destroy(children[i]);

        entity.Scene?.DestroyEntity(entity);
    }

    public static bool IsDestroyed(Entity entity)
    {
        return entity is null || entity._isDead;
    }

    public static bool CompareTag(Component component, string tag)
    {
        return component.Entity.Tags.Contains(tag);
    }

    public bool HasTag(string tag)
    {
        return Tags.Contains(tag);
    }

    public bool HasAnyTag(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
            if (Tags.Contains(tag))
                return true;

        return false;
    }

    internal void UpdateTransform()
    {
        if (Transform.LastWorldPosition != Transform.WorldPosition)
        {
        }
    }

    internal void Update()
    {
        if (_isDestroyed || UpdatesSuspended) return;

        ComponentRepository.UpdateLists();
        ComponentRepository.UpdateComponents();
    }

    internal void FlushStructuralChanges()
    {
        if (!_isDestroyed)
            ComponentRepository.UpdateLists();
    }

    internal void EditorUpdate()
    {
        if (_isDestroyed) return;
        ComponentRepository.EditorUpdateComponents();
    }

    internal void PhysicsUpdate()
    {
        if (_isDestroyed || UpdatesSuspended) return;
        ComponentRepository.PhysicsUpdateComponents();
    }

    internal void MarkDeadForImmediateDestruction()
    {
        _isDead = true;
    }

    /// <summary>
    ///     Attaches a component to the entity, if it already exists
    ///     it will return the existing component
    /// </summary>
    /// <typeparam name="T">Component Type</typeparam>
    /// <returns></returns>
    public T AttachComponent<T>() where T : Component
    {
        var component = ComponentRepository.GetComponent<T>();
        if (component != null) return component;

        Scene?.ValidateContentComponentAttachment(this, typeof(T));

        component = (T)Activator.CreateInstance<T>().SetUpAndCreateChildren(this);

        if (component == null)
            return null;

        component.Create();
        ComponentRepository.AttachComponent(component);

        return component;
    }

    /// <summary>
    ///     Attaches a component to the entity, if it already exists
    ///     it will return the existing component
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public Component AttachComponent(Type type)
    {
        var component = ComponentRepository.GetComponent(type);
        if (component != null) return component;

        if (type is null || !type.IsSubclassOf(typeof(Component)))
            return null;

        Scene?.ValidateContentComponentAttachment(this, type);

        component = (Component)Activator.CreateInstance(type);
        if (component == null) return null;
        component.SetUpAndCreateChildren(this);

        component.Create();
        ComponentRepository.AttachComponent(component);

        return component;
    }

    internal void BuildComponentsFromBlueprint(
        EntityBlueprint entityBlueprint,
        bool tolerateLoadErrors = false)
    {
        _blueprintComponentCreateOrder.Clear();

        var declaredTypes = new List<Type>(entityBlueprint.Components.Count);
        var enabledByType = new Dictionary<Type, bool>();

        foreach (var componentBlueprint in entityBlueprint.Components)
        {
            Type componentType;
            try
            {
                componentType = BlueprintResolver.ResolveComponentType(componentBlueprint.Type);
            }
            catch (Exception exception) when (tolerateLoadErrors)
            {
                _logger.Error(
                    "Could not resolve component type {0} while opening entity {1}: {2}",
                    componentBlueprint.Type,
                    Name,
                    exception);
                continue;
            }
            if (componentType == null)
            {
                _logger.Warn("{0} is not a valid component type", componentBlueprint.Type);
                continue;
            }

            declaredTypes.Add(componentType);
            enabledByType[componentType] = componentBlueprint.Enabled;
        }

        IReadOnlyList<Type> creationOrder;
        try
        {
            creationOrder = ComponentRequirementResolver.ResolveCreationOrder(
                declaredTypes,
                HasComponentOfType);
        }
        catch (Exception exception) when (tolerateLoadErrors)
        {
            _logger.Error(
                "Could not resolve component requirements for entity {0}: {1}",
                Name,
                exception);
            return;
        }

        foreach (var componentType in creationOrder)
            Scene?.ValidateContentComponentAttachment(this, componentType);

        foreach (var componentType in creationOrder)
        {
            // Required-only components default to enabled. Explicit blueprint components
            // use the enabled state written in JSON.
            var enabled = enabledByType.TryGetValue(componentType, out var configuredEnabled)
                ? configuredEnabled
                : true;

            Component component;
            try
            {
                component = Component.BpFromType(componentType, this, enabled);
            }
            catch (Exception exception) when (tolerateLoadErrors)
            {
                _logger.Error(
                    "Could not construct component {0} for entity {1}: {2}",
                    componentType.FullName,
                    Name,
                    exception);
                continue;
            }
            if (component == null)
            {
                _logger.Warn(
                    "Could not construct component of type {0} for entity {1}",
                    componentType.FullName,
                    Name);
                continue;
            }

            ComponentRepository.AttachComponent(component);
            _blueprintComponentCreateOrder.Add(component);
        }
    }

    internal void DeserializeComponentsFromBlueprints(
        EntityBlueprint entityBlueprint,
        BlueprintSpawnContext context,
        bool tolerateLoadErrors = false)
    {
        var componentsByType = new Dictionary<Type, Component>();

        foreach (var component in ComponentRepository.GetAllComponents())
        {
            componentsByType[component.GetType()] = component;
            try
            {
                component.MapRequiredFieldComponents();
            }
            catch (Exception exception) when (tolerateLoadErrors)
            {
                _logger.Error(
                    "Could not map required fields for component {0} on entity {1}: {2}",
                    component.GetType().FullName,
                    Name,
                    exception);
            }
        }

        foreach (var componentBlueprint in entityBlueprint.Components)
        {
            Type componentType;
            try
            {
                componentType = BlueprintResolver.ResolveComponentType(componentBlueprint.Type);
            }
            catch (Exception exception) when (tolerateLoadErrors)
            {
                _logger.Error(
                    "Could not resolve component type {0} while deserializing entity {1}: {2}",
                    componentBlueprint.Type,
                    Name,
                    exception);
                continue;
            }
            if (componentType == null)
                continue;

            if (!componentsByType.TryGetValue(componentType, out var component))
            {
                if (tolerateLoadErrors)
                    continue;
                throw new InvalidOperationException(
                    $"Entity '{Name}' did not construct blueprint component " +
                    $"'{componentType.FullName}'.");
            }

            try
            {
                component.BeforeDeserialize();
                if (tolerateLoadErrors)
                {
                    component.SetEditorSerializationFailures(
                        BlueprintResolver.ResolveComponentForEditor(
                            componentBlueprint,
                            context,
                            component));
                }
                else
                {
                    BlueprintResolver.ResolveComponent(componentBlueprint, context, component);
                }
                component.AfterDeserialize();
            }
            catch (Exception exception) when (tolerateLoadErrors)
            {
                component.SetEditorSerializationFailures(componentBlueprint.Properties.Keys);
                _logger.Error(
                    "Could not deserialize component {0} on entity {1}: {2}",
                    componentType.FullName,
                    Name,
                    exception);
            }
        }
    }


    internal void CallComponentOnCreateAfterDeserialized()
    {
        foreach (var component in _blueprintComponentCreateOrder)
            component.Create();

        _blueprintComponentCreateOrder.Clear();
    }

    /// <summary>
    ///     Detaches a component from the entity and cleans it up
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void DetachComponent<T>() where T : Component
    {
        var componentToRemove = ComponentRepository.GetComponent<T>();

        if (componentToRemove == null)
            return;

        ComponentRepository.DetachComponent(componentToRemove);
    }

    internal bool ContainsSceneService()
    {
        foreach (var component in
                 ComponentRepository.GetAllComponents())
        {
            if (component is SceneServiceComponent)
                return true;
        }

        return false;
    }

    internal bool ContainsSceneServiceInHierarchy()
    {
        if (ContainsSceneService())
            return true;

        for (var i = 0;
             i < _children.Count;
             i++)
        {
            if (_children[i]
                .ContainsSceneServiceInHierarchy())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Detaches a component from the entity and cleans it up
    ///     only if it exists in the entities internal component list.
    /// </summary>
    /// <param name="component"></param>
    /// <typeparam name="T"></typeparam>
    public void DetachComponent<T>(T component) where T : Component
    {
        ComponentRepository.DetachComponent(component);
    }

    /// <summary>
    ///     Retrieves a component by Type
    /// </summary>
    /// <typeparam name="T">Component Type</typeparam>
    /// <returns></returns>
    public T GetComponent<T>() where T : Component
    {
        return ComponentRepository.GetComponent<T>();
    }
    

    public Component GetComponent(Type type)
    {
        if (type is null || !type.IsSubclassOf(typeof(Component)))
            return null;

        return ComponentRepository.GetComponent(type);
    }

    public T GetComponentInChildren<T>() where T : Component
    {
        foreach (var child in _children)
        {
            //check if we have the component
            var component = child.GetComponent<T>();

            //if we do have it, return
            if (component != null) return component;

            //if we don't check in children
            component = child.GetComponentInChildren<T>();
            if (component != null) return component;
        }

        //only get here if no children have component
        return null;
    }

    /// <summary>
    ///     gets a list of all components that are attached, regardless if they are active
    /// </summary>
    /// <returns></returns>
    public IReadOnlyCollection<Component> GetAllAttachedComponents()
    {
        return ComponentRepository.GetAllAttachedComponents();
    }

    /// <summary>
    ///     gets a list of all components only if they are active
    /// </summary>
    /// <returns></returns>
    public IReadOnlyCollection<Component> GetAllActiveComponents()
    {
        return ComponentRepository.GetAllActiveComponents();
    }

    public IReadOnlyCollection<Component> GetAllComponents()
    {
        return ComponentRepository.GetAllComponents();
    }

    internal bool HasComponentOfType(Type componentType)
    {
        return ComponentRepository.ComponentOfTypeExists(componentType);
    }

    internal void OnAddedToScene()
    {
        //Todo: Implement on call back for components
    }

    internal void OnRemovedFromScene()
    {
        //Todo: Implement on call back for components
    }

    internal void OnEnabled()
    {
        //Todo: Implement on call back for components
    }

    internal void OnDisabled()
    {
        //Todo: Implement on call back for components
    }

    public void SetParent(Entity? parentEntity, bool preserveWorldTransform)
    {
        if (ReferenceEquals(parentEntity, this))
            throw new InvalidOperationException("An entity cannot be parented to itself.");
        if (parentEntity is not null && !ReferenceEquals(parentEntity.Scene, Scene))
            throw new InvalidOperationException("Entities from different scenes cannot be parented together.");

        for (var ancestor = parentEntity; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, this))
                throw new InvalidOperationException("Reparenting would create an entity hierarchy cycle.");

        if (!preserveWorldTransform)
        {
            SetParentInternal(parentEntity);
            return;
        }

        var worldPosition = Transform.WorldPosition;
        var worldRotation = Transform.WorldRotation;
        var worldScale = Transform.WorldScale;
        var previousParent = _parent;

        SetParentInternal(parentEntity);
        try
        {
            Transform.WorldScale = worldScale;
            Transform.WorldRotation = worldRotation;
            Transform.WorldPosition = worldPosition;
        }
        catch
        {
            SetParentInternal(previousParent);
            throw;
        }
    }

    private void SetParent(Entity? parentEntity)
    {
        SetParent(parentEntity, false);
    }

    private void SetParentInternal(Entity? parentEntity)
    {
        if (_parent != null)
            _parent._children.Remove(this);

        _parent = parentEntity;

        if (_parent != null && !_parent._children.Contains(this))
            _parent._children.Add(this);
    }

    internal void Destroy()
    {
        if (_isDestroyed)
            return;

        _isDestroyed = true;
        _isDead = true;

        try
        {
            ComponentRepository.DestroyAllComponentsNow();
        }
        finally
        {
            var owningScene = Scene;

            // Never allow component cleanup failure to leave repository state alive.
            ComponentRepository.ClearLists();

            // Sever the upward hierarchy reference.
            if (_parent != null)
            {
                _parent._children.Remove(this);
                _parent = null;
            }

            // Scene.DestroyEntity(entity) historically destroys only that entity.
            // Therefore, surviving children become roots rather than being implicitly
            // destroyed here. Public Entity.Destroy() performs recursive destruction.
            for (var i = 0; i < _children.Count; i++)
            {
                var child = _children[i];

                if (ReferenceEquals(
                        child._parent,
                        this))
                {
                    child._parent = null;
                }
            }

            _children.Clear();

            owningScene?.NotifyContentEntityDestroyed(this);
            Scene = null;
        }
    }

    internal void Quarantine(Component source, string callback, Exception exception)
    {
        if (IsFaulted) return;

        IsFaulted = true;
        FaultSource = source;
        FaultCallback = callback;
        FaultException = exception;
    }

    public static bool operator ==(Entity a, Entity b)
    {
        switch (a)
        {
            // Handle both being null
            case null when ReferenceEquals(b, null):
                return true;
            case null:
                return false;
        }

        if (a._isDestroyed && ReferenceEquals(b, null)) return true;

        return ReferenceEquals(a, b);
    }

    public static bool operator !=(Entity a, Entity b)
    {
        return !(a == b);
    }

    public override bool Equals(object obj)
    {
        if (obj is Entity otherEntity) return this == otherEntity;

        return false;
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    public static bool IsNull(Entity entity)
    {
        return entity == null || entity._isDestroyed;
    }

    protected virtual void OnDisposing()
    {
    }

    private void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing) OnDisposing();

        _isDestroyed = true;
        _isDisposed = true;
    }
}
