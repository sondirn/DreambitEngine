using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

public class EntityRepository
{
    // Entities that continue updating even when their normal Enabled chain is false.
    private readonly List<Entity> _alwaysUpdateEntities = new(16);

    // Active entities
    private readonly List<Entity> _entities = new(256);
    private readonly Dictionary<Guid, Entity> _entitiesById = new(256);
    private readonly HashSet<Entity> _entitiesSet = new(); // O(1) membership for fast checks

    // Creation queue + O(1) membership + Id index (pending)
    private readonly List<Entity> _entitiesToCreate = new(64);
    private readonly HashSet<Entity> _entitiesToCreateSet = new();

    // Destruction queue + O(1) membership
    private readonly List<Entity> _entitiesToDestroy = new(64);
    private readonly HashSet<Entity> _entitiesToDestroySet = new();

    private readonly Logger<EntityRepository> _logger = new();
    private readonly Scene _scene;
    private readonly Dictionary<Guid, Entity> _toCreateById = new(64);
    private int _iterationDepth;

    public EntityRepository(Scene scene)
    {
        _scene = scene;
    }

    public int Count => _entities.Count + _entitiesToCreate.Count;

    internal bool IsIterating => _iterationDepth > 0;

    internal Entity CreateEntity(string name, HashSet<string> tags, bool enabled, Vector3? createAt,
        Vector3? eulerRotation,
        Vector3? scale, Guid? guidOverride = null)
    {
        tags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default" };

        var guid = guidOverride ?? Guid.NewGuid();
        if (_entitiesById.ContainsKey(guid) || _toCreateById.ContainsKey(guid))
            throw new InvalidOperationException(
                $"An entity with GUID '{guid}' already exists in this scene.");

        var entity = new Entity(guid, name, tags, enabled, _scene);
        entity.Transform.Entity = entity;

        if (createAt.HasValue)
        {
            entity.Transform.Position = createAt.Value;
            entity.Transform.CaptureLastWorldPosition();
        }

        if (eulerRotation.HasValue)
            entity.Transform.SetEulerRotation(eulerRotation.Value);

        if (scale.HasValue)
            entity.Transform.Scale = scale.Value;

        // Queue creation if not already queued/added
        if (!_entitiesSet.Contains(entity) && !_entitiesToCreateSet.Contains(entity))
        {
            _entitiesToCreate.Add(entity);
            _entitiesToCreateSet.Add(entity);
            _toCreateById[entity.Id] = entity;
        }

        return entity;
    }

    internal void DestroyEntity(Entity entity)
    {
        if (entity == null)
        {
            Console.WriteLine(
                "Could not destroy entity, entity is null");

            return;
        }

        _scene.Services.EnsureCanRemove(entity);

        if (_entitiesToDestroySet.Contains(entity))
        {
            Console.WriteLine(
                "Entity {0} is already being removed",
                entity.Name);

            return;
        }

        // Pending creation: ownership already exists even though the entity
        // has not entered the active list.
        if (_entitiesToCreateSet.Contains(entity))
        {
            _entitiesToCreateSet.Remove(entity);
            _toCreateById.Remove(entity.Id);

            RemoveByReference(
                _entitiesToCreate,
                entity);

            RemoveByReference(
                _alwaysUpdateEntities,
                entity);

            var cleanupErrors =
                new List<Exception>();

            DestroyAndDisposeEntity(
                entity,
                cleanupErrors);

            ThrowIfCleanupFailed(
                cleanupErrors,
                "Pending entity cleanup failed.");

            return;
        }

        if (_entitiesSet.Contains(entity))
        {
            _entitiesToDestroy.Add(entity);
            _entitiesToDestroySet.Add(entity);
        }
    }

    internal void ClearLists()
    {
        var allEntities =
            new List<Entity>(
                _entities.Count +
                _entitiesToCreate.Count +
                _entitiesToDestroy.Count);

        AddUniqueByReference(
            allEntities,
            _entities);

        AddUniqueByReference(
            allEntities,
            _entitiesToCreate);

        AddUniqueByReference(
            allEntities,
            _entitiesToDestroy);

        var ordinaryEntities =
            new List<Entity>(
                allEntities.Count);

        var serviceEntities =
            new List<Entity>();

        foreach (var entity in allEntities)
            if (entity.ContainsSceneService())
                serviceEntities.Add(entity);
            else
                ordinaryEntities.Add(entity);

        var cleanupErrors =
            new List<Exception>();

        RemoveAndDestroyEntities(
            ordinaryEntities,
            cleanupErrors);

        _scene.Services.StopAll();

        RemoveAndDestroyEntities(
            serviceEntities,
            cleanupErrors);

        // Repository ownership must always be released, even when user cleanup
        // code throws.
        _entities.Clear();
        _entitiesSet.Clear();
        _entitiesById.Clear();

        _entitiesToCreate.Clear();
        _entitiesToCreateSet.Clear();
        _toCreateById.Clear();

        _entitiesToDestroy.Clear();
        _entitiesToDestroySet.Clear();

        _alwaysUpdateEntities.Clear();

        ThrowIfCleanupFailed(
            cleanupErrors,
            "One or more entities failed while clearing the scene.");
    }

    private void RemoveAndDestroyEntities(
        IReadOnlyList<Entity> entities,
        List<Exception> cleanupErrors)
    {
        for (var i = 0;
             i < entities.Count;
             i++)
        {
            var entity =
                entities[i];

            if (_entitiesSet.Contains(entity))
                TryCleanup(
                    cleanupErrors,
                    entity.OnRemovedFromScene);

            DestroyAndDisposeEntity(
                entity,
                cleanupErrors);
        }
    }

    private static void AddUniqueByReference(
        List<Entity> destination,
        IReadOnlyList<Entity> source)
    {
        for (var i = 0;
             i < source.Count;
             i++)
        {
            var candidate =
                source[i];

            var exists =
                false;

            for (var destinationIndex = 0;
                 destinationIndex < destination.Count;
                 destinationIndex++)
            {
                if (!ReferenceEquals(
                        destination[destinationIndex],
                        candidate))
                {
                    continue;
                }

                exists = true;
                break;
            }

            if (!exists)
                destination.Add(candidate);
        }
    }

    private void UpdateEntities()
    {
        for (var i = 0; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.Enabled && !_alwaysUpdateEntities.Contains(e))
                e.Update();
        }

        for (var i = 0; i < _alwaysUpdateEntities.Count; i++)
        {
            var entity = _alwaysUpdateEntities[i];
            if (_entitiesSet.Contains(entity))
                entity.Update();
        }
    }

    private void PhysicsUpdateEntities()
    {
        for (var i = 0; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.Enabled && !_alwaysUpdateEntities.Contains(e))
                e.PhysicsUpdate();
        }

        for (var i = 0; i < _alwaysUpdateEntities.Count; i++)
        {
            var entity = _alwaysUpdateEntities[i];
            if (_entitiesSet.Contains(entity))
                entity.PhysicsUpdate();
        }
    }

    public void SetEntityAlwaysUpdate(Entity entity, bool value)
    {
        if (value)
        {
            if (!_alwaysUpdateEntities.Contains(entity))
                _alwaysUpdateEntities.Add(entity);
        }
        else
        {
            // Remove without preserving order to be O(1) average
            for (var i = 0; i < _alwaysUpdateEntities.Count; i++)
                if (ReferenceEquals(_alwaysUpdateEntities[i], entity))
                {
                    var last = _alwaysUpdateEntities.Count - 1;
                    _alwaysUpdateEntities[i] = _alwaysUpdateEntities[last];
                    _alwaysUpdateEntities.RemoveAt(last);
                    break;
                }
        }
    }

    private void HandleEntityCreations()
    {
        // Move from create queue -> active, updating indices
        for (var i = 0; i < _entitiesToCreate.Count; i++)
        {
            var e = _entitiesToCreate[i];
            if (_entitiesSet.Contains(e)) continue;

            _entities.Add(e);
            _entitiesSet.Add(e);
            _entitiesById[e.Id] = e;
            _toCreateById.Remove(e.Id);

            e.OnAddedToScene();
        }

        _entitiesToCreate.Clear();
        _entitiesToCreateSet.Clear();
    }

    private void HandleEntityDeletions()
    {
        var cleanupErrors =
            new List<Exception>();

        for (var i = 0;
             i < _entitiesToDestroy.Count;
             i++)
        {
            var entity =
                _entitiesToDestroy[i];

            if (!_entitiesSet.Contains(entity))
                continue;

            RemoveByReference(
                _entities,
                entity);

            _entitiesSet.Remove(entity);
            _entitiesById.Remove(entity.Id);

            RemoveByReference(
                _alwaysUpdateEntities,
                entity);

            TryCleanup(
                cleanupErrors,
                entity.OnRemovedFromScene);

            DestroyAndDisposeEntity(
                entity,
                cleanupErrors);
        }

        _entitiesToDestroy.Clear();
        _entitiesToDestroySet.Clear();

        ThrowIfCleanupFailed(
            cleanupErrors,
            "One or more entities failed while being destroyed.");
    }

    internal void Tick()
    {
        _iterationDepth++;
        try
        {
            HandleEntityCreations();
            UpdateEntities();
            HandleEntityDeletions();
        }
        finally
        {
            _iterationDepth--;
        }
    }

    internal void FlushStructuralChanges()
    {
        _iterationDepth++;
        try
        {
            HandleEntityCreations();
            for (var index = 0; index < _entities.Count; index++)
                _entities[index].FlushStructuralChanges();
            HandleEntityDeletions();
        }
        finally
        {
            _iterationDepth--;
        }
    }

    internal void EditorUpdate()
    {
        FlushStructuralChanges();
        _iterationDepth++;
        try
        {
            for (var index = 0; index < _entities.Count; index++)
            {
                var entity = _entities[index];
                if (entity.Enabled)
                    entity.EditorUpdate();
            }
        }
        finally
        {
            _iterationDepth--;
        }
    }

    internal void PhysicsTick()
    {
        _iterationDepth++;
        try
        {
            PhysicsUpdateEntities();
        }
        finally
        {
            _iterationDepth--;
        }
    }

    public Entity GetEntity(Guid id)
    {
        // O(1): prefer active-by-id
        if (_entitiesById.TryGetValue(id, out var active))
            return active;

        // Also check pending creations
        if (_toCreateById.TryGetValue(id, out var pending))
            return pending;

        return null;
    }

    public Entity GetEntity(string name)
    {
        // Preserve original “first match” behavior without LINQ
        // Active first
        for (var i = 0; i < _entities.Count; i++)
            if (_entities[i].Name == name)
                return _entities[i];

        // Then pending creations
        for (var i = 0; i < _entitiesToCreate.Count; i++)
            if (_entitiesToCreate[i].Name == name)
                return _entitiesToCreate[i];

        return null;
    }

    public IReadOnlyList<Entity> GetEntitiesByTag(string tag)
    {
        // Rebuild result each call to stay correct if Tags mutate at runtime
        var result = new List<Entity>(16);

        for (var i = 0; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.Tags != null && e.Tags.Contains(tag))
                result.Add(e);
        }

        for (var i = 0; i < _entitiesToCreate.Count; i++)
        {
            var e = _entitiesToCreate[i];
            if (e.Tags != null && e.Tags.Contains(tag))
                result.Add(e);
        }

        return result;
    }

    public IReadOnlyList<Entity> GetActiveEntitiesByTag(string tag)
    {
        // Rebuild result each call to stay correct if Tags mutate at runtime
        var result = new List<Entity>(_entities.Count);

        for (var i = 0; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.Tags != null && e.Tags.Contains(tag) && e.Enabled)
                result.Add(e);
        }

        return result;
    }

    public IReadOnlyList<Entity> GetAllActiveEntities()
    {
        // Allocate once per call, but pre-size
        var result = new List<Entity>(_entities.Count);
        for (var i = 0; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.Enabled) result.Add(e);
        }

        return result;
    }

    public List<Entity> GetAllAddedEntities()
    {
        // Copy without LINQ
        return new List<Entity>(_entities);
    }

    public List<Entity> GetAllEntities()
    {
        var result = new List<Entity>(_entities.Count + _entitiesToCreate.Count);
        result.AddRange(_entities);
        result.AddRange(_entitiesToCreate);
        return result;
    }

    internal void DestroyEntityImmediately(Entity entity)
    {
        if (entity == null)
            return;

        entity.MarkDeadForImmediateDestruction();

        var wasActive =
            _entitiesSet.Remove(entity);

        _entitiesById.Remove(entity.Id);
        _toCreateById.Remove(entity.Id);

        _entitiesToCreateSet.Remove(entity);
        _entitiesToDestroySet.Remove(entity);

        RemoveByReference(
            _entities,
            entity);

        RemoveByReference(
            _entitiesToCreate,
            entity);

        RemoveByReference(
            _entitiesToDestroy,
            entity);

        RemoveByReference(
            _alwaysUpdateEntities,
            entity);

        var cleanupErrors =
            new List<Exception>();

        if (wasActive)
        {
            TryCleanup(
                cleanupErrors,
                entity.OnRemovedFromScene);
        }

        DestroyAndDisposeEntity(
            entity,
            cleanupErrors);

        ThrowIfCleanupFailed(
            cleanupErrors,
            "Immediate entity destruction failed.");
    }
    
    private static void DestroyAndDisposeEntity(
        Entity entity,
        List<Exception> cleanupErrors)
    {
        TryCleanup(
            cleanupErrors,
            entity.Destroy);

        TryCleanup(
            cleanupErrors,
            entity.Dispose);
    }

    private static void TryCleanup(
        List<Exception> cleanupErrors,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception);
        }
    }

    private static void ThrowIfCleanupFailed(
        List<Exception> cleanupErrors,
        string message)
    {
        if (cleanupErrors.Count == 0)
            return;

        throw new AggregateException(
            message,
            cleanupErrors);
    }

    private static void RemoveByReference(List<Entity> list, Entity entity)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (!ReferenceEquals(list[i], entity))
                continue;

            var lastIndex = list.Count - 1;
            list[i] = list[lastIndex];
            list.RemoveAt(lastIndex);
            return;
        }
    }
}
