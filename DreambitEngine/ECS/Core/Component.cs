using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dreambit.Networking;

namespace Dreambit.ECS;

public abstract class Component : IDisposable
{
    protected readonly ILogger Logger;
    internal bool IsDestroyed;
    private IReadOnlyList<Type> _requiredComponentTypes = [];
    private bool _enabled = true;
    private bool _isDisposed;
    private readonly HashSet<string> _editorSerializationFailures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<CoroutineHandle> _coroutineHandles = [];

    protected Component()
    {
        Logger = new Logger(GetType());
    }

    protected ICoroutineService CoroutineService => Core.Instance.CurrentScene.CoroutineService;

    public Transform Transform => Entity?.Transform;

    public Entity Entity { get; internal set; }
    public Scene Scene => Entity?.Scene;

    /// <summary>
    /// Serialized members the editor could not materialize. Their source JSON is retained
    /// verbatim until the member is deliberately changed.
    /// </summary>
    public IReadOnlySet<string> EditorSerializationFailures => _editorSerializationFailures;

    /// <summary>Marks a previously invalid serialized member as deliberately replaced.</summary>
    public void AcknowledgeEditorSerializationFailure(string memberName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        _editorSerializationFailures.Remove(memberName);
    }

    internal void SetEditorSerializationFailures(IEnumerable<string> memberNames)
    {
        _editorSerializationFailures.Clear();
        foreach (var memberName in memberNames)
            _editorSerializationFailures.Add(memberName);
    }


    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;

            _enabled = value;

            if (value)
                Enable();
            else
                Disable();
        }
    }

    public void Dispose()
    {
        if (this is SceneServiceComponent sceneService)
            Scene?.Services.EnsureCanRemove(sceneService);

        try
        {
            Dispose(true);
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    ~Component()
    {
        Dispose(false);
    }

    internal virtual Component SetUpAndCreateChildren(Entity entity, bool enabled = true)
    {
        Entity = entity;
        _enabled = enabled;

        _requiredComponentTypes = GetRequiredComponents();

        foreach (var cType in _requiredComponentTypes) Entity.AttachComponent(cType);
        MapRequiredFieldComponents();
        return this;
    }

    internal static Component BpFromType(
        Type type,
        Entity entity,
        bool enabled = true)
    {
        if (!type.IsSubclassOf(typeof(Component)))
        {
            Core.Logger.Warn(
                "{0} is not a valid component type on deserialization",
                type.FullName);

            return null;
        }

        var component =
            entity.GetComponent(type) ??
            (Component)Activator.CreateInstance(type);

        if (component is null)
            return null;

        return component.SetUpAndCreateChildren(
            entity,
            enabled);
    }

    private IReadOnlyList<Type> GetRequiredComponents()
    {
        var list = new List<Type>();

        var attributes = GetType().GetCustomAttributes<RequireAttribute>(inherit: true);
        foreach (var requireAttribute in attributes)
        {
            foreach (var requiredType in requireAttribute.RequiredTypes)
            {
                var hasRequired = Entity.HasComponentOfType(requiredType);
                if (hasRequired) continue;

                list.Add(requiredType);
            }
        }

        return list;
    }

    internal void MapRequiredFieldComponents()
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var type = GetType();

        var fields = type.GetFields(flags);
        foreach (var field in fields)
        {
            var attribute = field.GetCustomAttribute<FromRequiredAttribute>();
            if (attribute == null) continue;

            var requiredType = field.FieldType;
            var requiredComponent = Entity.GetComponent(requiredType);

            if (requiredComponent is null)
                Logger.Warn("{0} unable to reference component. Ensure use of Require component attribute",
                    requiredType.FullName);

            field.SetValue(this, requiredComponent);
        }

        var props = type.GetProperties(flags);
        foreach (var prop in props)
        {
            var attribute = prop.GetCustomAttribute<FromRequiredAttribute>();
            if (attribute == null) continue;

            var requiredType = prop.PropertyType;
            var requiredComponent = Entity.GetComponent(requiredType);

            if (requiredComponent is null)
                Logger.Warn("{0} unable to reference component. Ensure use of Require component attribute",
                    requiredType.FullName);

            prop.SetValue(this, requiredComponent);
        }
    }

    /// <summary>
    ///     Gets called immediately before the component is de-serialized
    /// </summary>
    public virtual void OnBeforeDeserialize()
    {
    }

    /// <summary>
    ///     Gets called immediately after the component is de-serialized
    /// </summary>
    public virtual void OnAfterDeserialize()
    {
    }

    /// <summary>
    ///     Gets called immediately when the component is instantiated and serialized.
    /// </summary>
    public virtual void OnCreated()
    {
    }

    /// <summary>
    /// Called once after this Component's network Entity has received all initial authoritative
    /// state and has a registered network identity and owner.
    /// </summary>
    /// <param name="context">Identity, ownership, role, Scene, and server-tick information.</param>
    /// <remarks>
    /// On a server or host, this runs after authoritative spawn initialization and initial state
    /// capture. On a remote client, it runs after every initial replicated Component payload has
    /// been applied and before gameplay updates resume. Unlike <see cref="OnCreated"/>, this callback
    /// may safely read runtime values established by <see cref="NetworkService.Spawn(
    /// EntityBlueprint, Action{Entity}, NetworkSpawnOptions?)"/>. It is also called for replicated
    /// Components and presentation Components on descendants of the network Entity root.
    /// </remarks>
    public virtual void OnNetworkSpawnReady(NetworkSpawnReadyContext context)
    {
    }

    /// <summary>
    /// Called on a replicated Component after one complete authoritative payload has been applied
    /// to it on a remote client.
    /// </summary>
    /// <param name="context">The applied Component identity, synchronization kind, Scene, and tick.</param>
    /// <remarks>
    /// All <see cref="Networking.Replication.ReplicatedAttribute"/> members on this Component have
    /// been decoded before the callback runs. During initial synchronization, other replicated
    /// Components on the Entity may still be pending; use <see cref="OnNetworkSpawnReady"/> for work
    /// that requires the entire Entity to be initialized. Direct authoritative assignments on a
    /// server or host do not invoke this callback. Full snapshots may invoke this callback even when
    /// the decoded values equal the Component's existing values, so expensive presentation work
    /// should cache and compare the state relevant to it.
    /// </remarks>
    public virtual void OnNetworkStateApplied(NetworkStateAppliedContext context)
    {
    }

    /// <summary>Called once when this component is created in an editor-hosted scene.</summary>
    public virtual void OnEditorCreated()
    {
    }

    /// <summary>Called once per editor frame. Gameplay update callbacks remain suppressed.</summary>
    public virtual void OnEditorUpdate()
    {
    }

    /// <summary>Draws an always-visible editor visualization for this component.</summary>
    public virtual void OnEditorDrawGizmos(IEditorGizmoContext context)
    {
    }

    /// <summary>Draws editor visualization only while this component's entity is selected.</summary>
    public virtual void OnEditorDrawGizmosSelected(IEditorGizmoContext context)
    {
    }

    /// <summary>Called when an editor-hosted component is removed or its scene closes.</summary>
    public virtual void OnEditorDestroyed()
    {
    }

    /// <summary>
    ///     Called when the component is destroyed. Called after it has been de-attached from the Entity
    /// </summary>
    public virtual void OnDestroyed()
    {
    }

    /// <summary>
    ///     Called after created, and when component has been attached to the entity.
    /// </summary>
    public virtual void OnAddedToEntity()
    {
    }

    /// <summary>
    ///     Called when has been detached from the entity, but not yet destroyed.
    /// </summary>
    public virtual void OnRemovedFromEntity()
    {
    }

    /// <summary>
    ///     Called when the entity is enabled. This is not called when the component is added to the entity
    ///     for the first time, regardless of the enabled value. This is only called when the enabled value
    ///     has been altered after the component has been added to the entity
    /// </summary>
    public virtual void OnEnabled()
    {
    }

    /// <summary>
    ///     Called when the entity is disabled. This is not called when the component is added to the entity
    ///     for the first time, regardless of the enabled value. This is only called when the enabled value
    ///     has been altered after the component has been added to the entity
    /// </summary>
    public virtual void OnDisabled()
    {
    }

    /// <summary>
    ///     called every update loop of the game
    /// </summary>
    public virtual void OnUpdate()
    {
    }

    /// <summary>
    ///     Called every physics update during the loop of the game
    /// </summary>
    public virtual void OnPhysicsUpdate()
    {
    }

    /// <summary>
    ///     Called if debug mode is activated. Used to render debug data.
    /// </summary>
    public virtual void OnDebugDraw()
    {
    }

    public CoroutineHandle StartCoroutine(IEnumerator routine)
    {
        var handle = CoroutineService.StartCoroutine(routine);
        _coroutineHandles.Add(handle);

        return handle;
    }

    public void StopCoroutine(CoroutineHandle handle)
    {
        CoroutineService.StopCoroutine(handle);
    }

    public void StopAllCoroutines()
    {
        foreach(var handle in _coroutineHandles)
        {
            CoroutineService.StopCoroutine(handle);
        }
    }

    internal void BeforeDeserialize()
    {
        if (IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnBeforeDeserialize();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnBeforeDeserialize), exception);
        }
    }

    internal void AfterDeserialize()
    {
        if (IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnAfterDeserialize();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnAfterDeserialize), exception);
        }
    }

    internal void Create()
    {
        if (IsFaulted()) return;

        try
        {
            if (Scene?.ExecutionMode == SceneExecutionMode.Editor)
                OnEditorCreated();
            else
                OnCreated();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnCreated), exception);
        }
    }

    internal void NetworkSpawnReady(NetworkSpawnReadyContext context)
    {
        if (IsDestroyed || IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnNetworkSpawnReady(context);
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnNetworkSpawnReady), exception);
        }
    }

    internal void NetworkStateApplied(NetworkStateAppliedContext context)
    {
        if (IsDestroyed || IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnNetworkStateApplied(context);
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnNetworkStateApplied), exception);
        }
    }

    internal void EditorUpdate()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnEditorUpdate();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnEditorUpdate), exception);
        }
    }

    internal void EditorDrawGizmos(IEditorGizmoContext context, bool selected)
    {
        if (IsFaulted() || !Enabled) return;
        try
        {
            OnEditorDrawGizmos(context);
            if (selected)
                OnEditorDrawGizmosSelected(context);
        }
        catch (Exception exception)
        {
            HandleCallbackException(
                selected ? nameof(OnEditorDrawGizmosSelected) : nameof(OnEditorDrawGizmos),
                exception);
        }
    }

    internal void AddToEntity()
    {
        if (IsFaulted()) return;

        try
        {
            OnAddedToEntity();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnAddedToEntity), exception);
        }
    }

    internal void Update()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnUpdate();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnUpdate), exception);
        }
    }

    internal void PhysicsUpdate()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnPhysicsUpdate();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnPhysicsUpdate), exception);
        }
    }

    internal void RemoveFromEntity()
    {
        if (Scene?.ExecutionMode == SceneExecutionMode.Editor)
            return;

        try
        {
            OnRemovedFromEntity();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnRemovedFromEntity), exception);
        }
    }

    internal void Enable()
    {
        if (IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnEnabled();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnUpdate), exception);
        }
    }

    internal void Disable()
    {
        if (IsFaulted() || Scene?.ExecutionMode == SceneExecutionMode.Editor) return;

        try
        {
            OnDisabled();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnDisabled), exception);
        }
    }

    internal void Destroy()
    {
        if (IsDestroyed) return;
        
        StopAllCoroutines();

        try
        {
            if(Scene?.ExecutionMode == SceneExecutionMode.Editor)
                OnEditorDestroyed();
            else
                OnDestroyed();
        }
        catch (Exception exception)
        {
            HandleCallbackException(
                Scene?.ExecutionMode == SceneExecutionMode.Editor
                    ? nameof(OnEditorDestroyed)
                    : nameof(OnDestroyed),
                exception);
        }
    }


    public static bool operator ==(Component a, Component b)
    {
        switch (a)
        {
            // Handle both being null
            case null when ReferenceEquals(b, null):
                return true;
            case null:
                return false;
        }

        if (a.IsDestroyed && ReferenceEquals(b, null)) return true;

        return ReferenceEquals(a, b);
    }

    public static bool operator !=(Component a, Component b)
    {
        return !(a == b);
    }

    public override bool Equals(object obj)
    {
        if (obj is Component otherEntity) return this == otherEntity;

        return false;
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    /// <summary>
    ///     Checks if this entity is null (used as component deletion is usually handled at the
    ///     beginning of every frame.
    /// </summary>
    /// <param name="component"></param>
    /// <returns></returns>
    public static bool IsNull(Component component)
    {
        return component == null || component.IsDestroyed;
    }

    protected bool HandleCallbackException(string callbackName, Exception exception)
    {
        if (Entity == null)
            return false;

        Entity.Quarantine(
            this,
            callbackName,
            exception);

        Logger.Error(
            "Entity callback failed.\n" +
            $"Entity: {Entity.Name}\n" +
            $"Entity ID: {Entity.Id}\n" +
            $"Component: {GetType().FullName}\n" +
            $"Callback: {callbackName}\n" +
            $"Exception: {exception}");

        return false;
    }

    protected bool IsFaulted()
    {
        return Entity != null && Entity.IsFaulted;
    }

    protected virtual void OnDisposing()
    {
    }

    private void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        try
        {
            if(disposing)
                OnDisposing();
        }
        finally
        {
            IsDestroyed = true;
            _isDisposed = true;
            Entity = null;
        }
    }
}

public class SingletonComponent<T> : Component where T : SingletonComponent<T>
{
    public static T Instance { get; private set; }

    public static bool HasInstance =>
        !IsNull(Instance);

    internal override Component SetUpAndCreateChildren(Entity entity, bool enabled = true)
    {
        // Editor previews can coexist briefly while SceneRuntime builds a replacement before
        // disposing the outgoing scene. They never run gameplay callbacks, so they must not
        // participate in the process-wide runtime singleton registry.
        if (entity.Scene?.ExecutionMode == SceneExecutionMode.Editor)
            return base.SetUpAndCreateChildren(entity, enabled);

        if (!IsNull(Instance) && !ReferenceEquals(Instance, this))
            throw new InvalidOperationException(
                $"A singleton component of type '{typeof(T).FullName}' " +
                "already exists.");

        Instance = (T)this;

        return base.SetUpAndCreateChildren(entity, enabled);
    }

    public override void OnDestroyed()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;

        base.OnDestroyed();
    }

    protected override void OnDisposing()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;

        base.OnDisposing();
    }
}
