using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Dreambit.ECS;
using Dreambit.Tiled;
using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>
/// One runtime materialization of a Scene Blueprint inside an existing Scene.
/// Source identity, runtime instance identity, and runtime Entity identity are independent.
/// </summary>
public sealed class SceneContentInstance
{
    private readonly Dictionary<Guid, Entity> _entitiesBySourceGuid = [];
    private readonly Dictionary<Entity, Guid> _sourceGuidByEntity =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<Entity> _ownedEntities = [];
    private readonly HashSet<Entity> _ownedEntitySet =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<Entity> _rootEntities = [];
    private readonly ReadOnlyDictionary<Guid, Entity> _entitiesBySourceGuidView;
    private readonly ReadOnlyCollection<Entity> _ownedEntitiesView;
    private readonly ReadOnlyCollection<Entity> _rootEntitiesView;
    private SceneContentInstanceState _state = SceneContentInstanceState.Loading;
    private object? _networkCoordinator;

    internal SceneContentInstance(
        Scene scene,
        AssetId sourceAssetId,
        string? sourceAssetName)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        InstanceId = Guid.NewGuid();
        SourceAssetId = sourceAssetId;
        SourceAssetName = sourceAssetName;
        _rootEntitiesView = _rootEntities.AsReadOnly();
        _ownedEntitiesView = _ownedEntities.AsReadOnly();
        _entitiesBySourceGuidView = new ReadOnlyDictionary<Guid, Entity>(_entitiesBySourceGuid);
    }

    /// <summary>Unique local lifetime identity. It is not a source or networking identity.</summary>
    public Guid InstanceId { get; }

    public Scene Scene { get; }

    public AssetId SourceAssetId { get; }

    public string? SourceAssetName { get; }

    /// <summary>Authored roots from the Scene Blueprint; the Tiled map root is not included.</summary>
    public IReadOnlyList<Entity> RootEntities => _rootEntitiesView;

    /// <summary>Every currently live Entity explicitly owned by this content lifetime.</summary>
    public IReadOnlyCollection<Entity> OwnedEntities => _ownedEntitiesView;

    /// <summary>Final materialized source GUIDs mapped to fresh runtime Entities.</summary>
    public IReadOnlyDictionary<Guid, Entity> EntitiesBySourceGuid => _entitiesBySourceGuidView;

    public bool IsLoaded => _state == SceneContentInstanceState.Loaded;

    /// <summary>
    /// Gets whether this lifetime is controlled by the active networking session. Network-managed
    /// content must be removed through the replication-scope API so peers cannot diverge.
    /// </summary>
    public bool IsNetworkManaged => _networkCoordinator is not null;

    public TiledMapInstance? TiledMap { get; private set; }

    public bool TryGetEntity(Guid sourceEntityGuid, out Entity? entity)
    {
        if (!IsLoaded || !_entitiesBySourceGuid.TryGetValue(sourceEntityGuid, out var found) ||
            Entity.IsNull(found))
        {
            entity = null;
            return false;
        }

        entity = found;
        return true;
    }

    public Entity GetEntity(Guid sourceEntityGuid)
    {
        if (TryGetEntity(sourceEntityGuid, out var entity))
            return entity;

        throw new KeyNotFoundException(
            $"Content instance '{InstanceId}' has no loaded Entity for source GUID " +
            $"'{sourceEntityGuid}'.");
    }

    public Entity CreateEntity(
        string name = "entity",
        HashSet<string>? tags = null,
        bool enabled = true,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null)
    {
        EnsureLoaded();
        EnsureUserMutationAllowed();
        return Scene.CreateContentEntity(
            this,
            name,
            tags,
            enabled,
            createAt,
            eulerRotation,
            scale);
    }

    public Entity CreateEntity(
        EntityBlueprint blueprint,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null)
    {
        EnsureLoaded();
        EnsureUserMutationAllowed();
        return Scene.CreateContentEntity(
            this,
            blueprint,
            enabled,
            createAt,
            eulerRotation,
            scale);
    }

    public void TrackEntity(Entity entity, bool includeDescendants = true)
    {
        EnsureLoaded();
        EnsureUserMutationAllowed();
        Scene.TrackContentEntity(this, entity, includeDescendants);
    }

    internal bool AcceptsOwnership =>
        _state is SceneContentInstanceState.Loading or SceneContentInstanceState.Loaded;

    internal void BindNetworkCoordinator(object coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if (_networkCoordinator is not null && !ReferenceEquals(_networkCoordinator, coordinator))
            throw new InvalidOperationException("The content instance already has a networking coordinator.");
        _networkCoordinator = coordinator;
    }

    internal bool IsNetworkCoordinator(object? coordinator) =>
        coordinator is not null && ReferenceEquals(_networkCoordinator, coordinator);

    internal void TrackCreatedEntity(Entity entity)
    {
        if (!AcceptsOwnership)
            throw new InvalidOperationException(
                $"Content instance '{InstanceId}' is no longer accepting Entity ownership.");

        if (entity.ContentOwner is { } owner && !ReferenceEquals(owner, this))
            throw new InvalidOperationException(
                $"Entity '{entity.Name}' already belongs to content instance '{owner.InstanceId}'.");

        if (!_ownedEntitySet.Add(entity))
            return;

        entity.ContentOwner = this;
        _ownedEntities.Add(entity);
    }

    internal void SetAuthoredEntities(
        IReadOnlyList<EntityBlueprint> materializedRoots,
        IReadOnlyDictionary<Guid, Entity> spawnedEntities)
    {
        foreach (var root in materializedRoots)
        {
            if (!spawnedEntities.TryGetValue(root.Guid, out var entity) || Entity.IsNull(entity))
                continue;
            _rootEntities.Add(entity);
        }

        foreach (var (sourceGuid, entity) in spawnedEntities)
        {
            if (Entity.IsNull(entity) || !_ownedEntitySet.Contains(entity))
                continue;
            _entitiesBySourceGuid.Add(sourceGuid, entity);
            _sourceGuidByEntity.Add(entity, sourceGuid);
        }
    }

    internal void SetTiledMap(TiledMapInstance tiledMap)
    {
        ArgumentNullException.ThrowIfNull(tiledMap);
        if (TiledMap is not null)
            throw new InvalidOperationException(
                $"Content instance '{InstanceId}' already owns a Tiled map.");
        TiledMap = tiledMap;
    }

    internal void Commit()
    {
        if (_state != SceneContentInstanceState.Loading)
            throw new InvalidOperationException(
                $"Content instance '{InstanceId}' cannot be committed from state '{_state}'.");
        _state = SceneContentInstanceState.Loaded;
    }

    internal bool BeginUnload()
    {
        if (_state is SceneContentInstanceState.Unloading or SceneContentInstanceState.Unloaded)
            return false;

        _state = SceneContentInstanceState.Unloading;
        return true;
    }

    internal IReadOnlyList<Entity> GetOwnedEntitiesChildFirst()
    {
        var ordered = new List<Entity>(_ownedEntities.Count);
        var visited = new HashSet<Entity>(ReferenceEqualityComparer.Instance);

        void Visit(Entity entity)
        {
            if (!visited.Add(entity))
                return;

            foreach (var child in entity.Children)
                if (_ownedEntitySet.Contains(child))
                    Visit(child);

            ordered.Add(entity);
        }

        foreach (var entity in _ownedEntities)
            Visit(entity);

        return ordered;
    }

    internal void OnEntityDestroyed(Entity entity)
    {
        if (!ReferenceEquals(entity.ContentOwner, this))
            return;

        entity.ContentOwner = null;

        // Unload clears the complete backing collections once all cleanup has been
        // attempted. Avoid quadratic list removal while walking the owned set.
        if (_state == SceneContentInstanceState.Unloading)
            return;

        _ownedEntitySet.Remove(entity);
        RemoveByReference(_ownedEntities, entity);
        RemoveByReference(_rootEntities, entity);

        if (_sourceGuidByEntity.Remove(entity, out var sourceGuid))
            _entitiesBySourceGuid.Remove(sourceGuid);
    }

    internal void CompleteUnload()
    {
        foreach (var entity in _ownedEntities)
            if (ReferenceEquals(entity.ContentOwner, this))
                entity.ContentOwner = null;

        _ownedEntitySet.Clear();
        _ownedEntities.Clear();
        _rootEntities.Clear();
        _entitiesBySourceGuid.Clear();
        _sourceGuidByEntity.Clear();
        TiledMap = null;
        _networkCoordinator = null;
        _state = SceneContentInstanceState.Unloaded;
    }

    private void EnsureLoaded()
    {
        if (!IsLoaded)
            throw new InvalidOperationException(
                $"Content instance '{InstanceId}' is not loaded.");
    }

    private void EnsureUserMutationAllowed()
    {
        if (IsNetworkManaged)
            throw new InvalidOperationException(
                "Network-managed content can only be mutated through NetworkService scope APIs.");
    }

    private static void RemoveByReference(List<Entity> entities, Entity entity)
    {
        for (var index = 0; index < entities.Count; index++)
        {
            if (!ReferenceEquals(entities[index], entity))
                continue;
            entities.RemoveAt(index);
            return;
        }
    }

    private enum SceneContentInstanceState : byte
    {
        Loading,
        Loaded,
        Unloading,
        Unloaded
    }
}
