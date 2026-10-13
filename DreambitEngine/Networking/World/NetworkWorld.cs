using System;
using System.Collections.Generic;
using System.Text;
using Dreambit.ECS;
using Dreambit.Networking.Replication;

namespace Dreambit.Networking.World;

/// <summary>Runtime network identity for one Scene, indexed by replication scope.</summary>
internal sealed class NetworkWorld : IDisposable
{
    private static readonly IReadOnlyList<NetworkEntityRecord> NoRecords = Array.Empty<NetworkEntityRecord>();
    private static readonly IReadOnlyList<NetworkAuthoredBinding> NoBindings = Array.Empty<NetworkAuthoredBinding>();
    private readonly Dictionary<NetworkEntityId, NetworkEntityRecord> _byNetworkId = [];
    private readonly Dictionary<Entity, NetworkEntityId> _byEntity = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NetworkPeerId, NetworkEntityId> _playerEntities = [];
    private readonly List<NetworkEntityRecord> _records = [];
    private readonly List<NetworkAuthoredBinding> _authoredBindings = [];
    private readonly Dictionary<NetworkReplicationScopeId, List<NetworkEntityRecord>> _recordsByScope = [];
    private readonly Dictionary<NetworkReplicationScopeId, List<NetworkAuthoredBinding>> _bindingsByScope = [];
    private readonly Dictionary<AuthoredEntityKey, NetworkEntityId> _authoredBySource = [];
    private readonly HashSet<NetworkReplicationScopeId> _boundScopes = [];
    private readonly HashSet<NetworkReplicationScopeId> _pendingScopes = [];
    private readonly NetworkReplicationRegistry _replication;
    private readonly int _maxNetworkEntities;
    private bool _disposed;

    public NetworkWorld(Scene scene, NetworkSceneEpoch sceneEpoch, bool server,
        NetworkReplicationRegistry? replication = null, int maxNetworkEntities = 100_000)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (!sceneEpoch.IsValid) throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
        if (maxNetworkEntities < 1) throw new ArgumentOutOfRangeException(nameof(maxNetworkEntities));
        SceneEpoch = sceneEpoch;
        IsServer = server;
        _replication = replication ?? new NetworkReplicationRegistry();
        _maxNetworkEntities = maxNetworkEntities;
    }

    public event Action<NetworkEntityRecord>? EntityRemoved;
    public Scene Scene { get; }
    public NetworkSceneEpoch SceneEpoch { get; }
    public bool IsServer { get; }
    public bool AuthoredEntitiesBound => IsAuthoredScopeBound(NetworkReplicationScopeId.Global);
    public IReadOnlyList<NetworkEntityRecord> Records => _records;
    public IReadOnlyList<NetworkAuthoredBinding> AuthoredBindings => _authoredBindings;
    public IReadOnlyDictionary<NetworkPeerId, NetworkEntityId> PlayerEntities => _playerEntities;

    public bool IsAuthoredScopeBound(NetworkReplicationScopeId scope) => _boundScopes.Contains(scope);
    public IReadOnlyList<NetworkEntityRecord> GetRecords(NetworkReplicationScopeId scope) =>
        _recordsByScope.TryGetValue(scope, out var records) ? records : NoRecords;
    public IReadOnlyList<NetworkAuthoredBinding> GetAuthoredBindings(NetworkReplicationScopeId scope) =>
        _bindingsByScope.TryGetValue(scope, out var bindings) ? bindings : NoBindings;

    public bool TryGetAuthoredEntity(
        NetworkReplicationScopeId scope,
        Guid sourceGuid,
        out Entity? entity)
    {
        ThrowIfDisposed();
        if (_authoredBySource.TryGetValue(new AuthoredEntityKey(scope, sourceGuid), out var id))
            return TryGetEntity(id, out entity);
        entity = null;
        return false;
    }

    public IReadOnlyList<NetworkAuthoredBinding> BindServerAuthoredEntities(Func<NetworkEntityId> allocate) =>
        BindServerAuthoredScope(NetworkReplicationScopeId.Global, null, allocate);

    public IReadOnlyList<NetworkAuthoredBinding> BindServerAuthoredScope(
        NetworkReplicationScopeId scope, SceneContentInstance? content, Func<NetworkEntityId> allocate)
    {
        ThrowIfDisposed();
        ValidateScopeSource(scope, content);
        if (!IsServer) throw new InvalidOperationException("Only a server world can assign authored network IDs.");
        if (_boundScopes.Contains(scope)) throw new InvalidOperationException($"Scope {scope} is already bound.");
        ArgumentNullException.ThrowIfNull(allocate);

        var entities = GetScopeEntities(scope, content);
        var sources = BuildSourceIndex(scope, content);
        var count = 0;
        foreach (var entity in entities)
        {
            var marker = entity.GetComponent<NetworkObject>();
            if (marker is null) continue;
            ValidatePresence(marker, entity);
            if (marker.Presence != NetworkPresence.Replicated) continue;
            RequireSourceGuid(scope, entity, sources);
            _replication.ValidateEntityShape(entity);
            count++;
        }
        EnsureCapacity(count);

        var bindings = new List<NetworkAuthoredBinding>(count);
        foreach (var entity in entities)
        {
            var marker = entity.GetComponent<NetworkObject>();
            if (marker is null) continue;
            if (marker.Presence == NetworkPresence.ClientOnly)
            {
                DisableAndDestroyNetworkHierarchy(entity);
                continue;
            }
            if (marker.Presence == NetworkPresence.ServerOnly) continue;
            var source = RequireSourceGuid(scope, entity, sources);
            var id = allocate();
            Register(entity, marker, id, NetworkPeerId.None, NetworkSpawnOrigin.AuthoredScene,
                scope, source, AssetId.Empty, null, true);
            bindings.Add(new NetworkAuthoredBinding(scope, source, id, NetworkPeerId.None));
        }
        CommitBindings(scope, bindings);
        return bindings;
    }

    public void BindClientAuthoredEntities(IReadOnlyList<NetworkAuthoredBinding> bindings) =>
        BindClientAuthoredScope(NetworkReplicationScopeId.Global, null, bindings);

    public void BindClientAuthoredScope(NetworkReplicationScopeId scope, SceneContentInstance? content,
        IReadOnlyList<NetworkAuthoredBinding> bindings)
    {
        ThrowIfDisposed();
        ValidateScopeSource(scope, content);
        if (IsServer) throw new InvalidOperationException("A server world cannot consume authored binding data.");
        if (_boundScopes.Contains(scope)) throw new InvalidOperationException($"Scope {scope} is already bound.");
        ArgumentNullException.ThrowIfNull(bindings);

        var bySource = new Dictionary<Guid, NetworkAuthoredBinding>();
        foreach (var binding in bindings)
        {
            if (binding.Scope != scope || binding.SourceGuid == Guid.Empty || !binding.NetworkEntityId.IsValid)
                throw new InvalidOperationException("Authored binding contains an invalid scoped identity.");
            if (!bySource.TryAdd(binding.SourceGuid, binding))
                throw new InvalidOperationException($"Source GUID '{binding.SourceGuid}' is duplicated in scope {scope}.");
        }

        EnsureCapacity(bindings.Count);
        var entities = GetScopeEntities(scope, content);
        var sources = BuildSourceIndex(scope, content);
        foreach (var entity in entities)
        {
            var marker = entity.GetComponent<NetworkObject>();
            if (marker is null) continue;
            ValidatePresence(marker, entity);
            if (marker.Presence == NetworkPresence.Replicated)
            {
                RequireSourceGuid(scope, entity, sources);
                _replication.ValidateEntityShape(entity);
            }
        }

        var consumed = new HashSet<Guid>();
        foreach (var entity in entities)
        {
            var marker = entity.GetComponent<NetworkObject>();
            if (marker is null) continue;
            if (marker.Presence == NetworkPresence.ServerOnly)
            {
                DisableAndDestroyNetworkHierarchy(entity);
                continue;
            }
            if (marker.Presence == NetworkPresence.ClientOnly) continue;
            var source = RequireSourceGuid(scope, entity, sources);
            if (!bySource.TryGetValue(source, out var binding))
                throw new InvalidOperationException(
                    $"Client Entity '{entity.Name}' has no authored network binding in scope {scope}.");
            consumed.Add(source);
            if (!binding.IsPresent)
            {
                DisableAndDestroyNetworkHierarchy(entity);
                continue;
            }
            Register(entity, marker, binding.NetworkEntityId, binding.Owner, NetworkSpawnOrigin.AuthoredScene,
                scope, source, AssetId.Empty, null, true);
        }
        foreach (var source in bySource.Keys)
            if (!consumed.Contains(source))
                throw new InvalidOperationException($"Binding '{source}' has no replicated Entity in scope {scope}.");
        CommitBindings(scope, new List<NetworkAuthoredBinding>(bindings));
    }

    public void RegisterDynamicEntity(Entity entity, NetworkEntityId id, NetworkPeerId owner,
        AssetId blueprintAssetId, string? blueprintAssetName, bool destroyWithOwner = true,
        NetworkReplicationScopeId scope = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entity);
        if (!scope.IsValid) scope = NetworkReplicationScopeId.Global;
        if (blueprintAssetId.IsEmpty) throw new ArgumentException("A stable Blueprint AssetId is required.", nameof(blueprintAssetId));
        if (blueprintAssetName is not null && Encoding.UTF8.GetByteCount(blueprintAssetName) > 1024)
            throw new ArgumentException("Blueprint fallback name cannot exceed 1024 UTF-8 bytes.", nameof(blueprintAssetName));
        var marker = entity.GetComponent<NetworkObject>() ??
            throw new InvalidOperationException($"Dynamic network Entity '{entity.Name}' has no NetworkObject.");
        if (marker.Presence != NetworkPresence.Replicated)
            throw new InvalidOperationException("Dynamic network entities must use Replicated presence.");
        Register(entity, marker, id, owner, NetworkSpawnOrigin.DynamicBlueprint, scope, Guid.Empty,
            blueprintAssetId, blueprintAssetName, destroyWithOwner);
    }

    public bool TryGetNetworkId(Entity? entity, out NetworkEntityId id)
    {
        ThrowIfDisposed();
        if (entity is not null && _byEntity.TryGetValue(entity, out id) &&
            _byNetworkId.TryGetValue(id, out var record) && !_pendingScopes.Contains(record.Scope))
            return true;
        id = NetworkEntityId.None;
        return false;
    }
    public bool TryGetRecord(NetworkEntityId id, out NetworkEntityRecord? record)
    { ThrowIfDisposed(); return _byNetworkId.TryGetValue(id, out record); }
    public bool TryGetEntity(NetworkEntityId id, out Entity? entity)
    {
        ThrowIfDisposed();
        if (_byNetworkId.TryGetValue(id, out var record) &&
            !_pendingScopes.Contains(record.Scope) &&
            !Entity.IsDestroyed(record.Entity))
        { entity = record.Entity; return true; }
        entity = null; return false;
    }
    public bool TryResolve(NetworkEntityRef reference, out Entity? entity)
    {
        ThrowIfDisposed();
        if (reference.SceneEpoch == SceneEpoch) return TryGetEntity(reference.EntityId, out entity);
        entity = null; return false;
    }
    public NetworkPeerId GetOwner(NetworkEntityId id) => GetRecord(id).Owner;
    public NetworkReplicationScopeId GetScope(NetworkEntityId id) => GetRecord(id).Scope;
    internal bool TryGetScope(Entity? entity, out NetworkReplicationScopeId scope)
    {
        ThrowIfDisposed();
        // Scope ownership is initialization metadata, not publication of a staged entity.
        // Components already holding their entity need it to resolve scoped content during
        // baseline callbacks, before public identity/player lookups become available.
        if (entity is not null && !Entity.IsDestroyed(entity) &&
            _byEntity.TryGetValue(entity, out var id) &&
            _byNetworkId.TryGetValue(id, out var record))
        {
            scope = record.Scope;
            return true;
        }
        scope = NetworkReplicationScopeId.None;
        return false;
    }
    public bool GetDestroyWithOwner(NetworkEntityId id) => GetRecord(id).DestroyWithOwner;
    public void SetOwner(NetworkEntityId id, NetworkPeerId owner)
    {
        var record = GetRecord(id);
        record.Owner = owner;
        if (record.Origin != NetworkSpawnOrigin.AuthoredScene ||
            !_bindingsByScope.TryGetValue(record.Scope, out var bindings))
            return;
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index].NetworkEntityId != record.Id)
                continue;
            var updated = bindings[index] with { Owner = owner };
            bindings[index] = updated;
            ReplaceAuthoredBinding(record.Id, updated);
            break;
        }
    }
    public bool IsOwnedBy(NetworkPeerId peer, Entity entity) =>
        peer.IsValid && TryGetNetworkId(entity, out var id) && GetOwner(id) == peer;

    public bool TryGetReplicationBinding(NetworkEntityId entityId, ushort componentId,
        out NetworkReplicationBinding? binding)
    {
        ThrowIfDisposed();
        if (_byNetworkId.TryGetValue(entityId, out var record))
            foreach (var candidate in record.ReplicationBindings)
                if (candidate.Descriptor.Id == componentId) { binding = candidate; return true; }
        binding = null; return false;
    }

    public void SetPlayerEntity(NetworkPeerId peer, NetworkEntityId entityId)
    {
        ThrowIfDisposed();
        if (!peer.IsValid) throw new ArgumentOutOfRangeException(nameof(peer));
        GetRecord(entityId);
        _playerEntities[peer] = entityId;
    }
    public bool TryGetPlayerEntity(NetworkPeerId peer, out Entity? entity)
    {
        ThrowIfDisposed();
        if (_playerEntities.TryGetValue(peer, out var id)) return TryGetEntity(id, out entity);
        entity = null; return false;
    }
    public bool RemovePlayerEntity(NetworkPeerId peer) { ThrowIfDisposed(); return _playerEntities.Remove(peer); }

    internal void MarkScopePending(NetworkReplicationScopeId scope)
    {
        ThrowIfDisposed();
        if (!scope.IsValid)
            throw new ArgumentOutOfRangeException(nameof(scope));
        _pendingScopes.Add(scope);
    }

    internal void CommitScope(NetworkReplicationScopeId scope)
    {
        ThrowIfDisposed();
        _pendingScopes.Remove(scope);
    }

    internal bool IsScopePending(NetworkReplicationScopeId scope) => _pendingScopes.Contains(scope);

    internal void ApplyClientAuthoredEntity(
        NetworkReplicationScopeId scope,
        Entity entity,
        Guid sourceGuid,
        NetworkAuthoredBinding? binding)
    {
        ThrowIfDisposed();
        var marker = entity.GetComponent<NetworkObject>() ??
                     throw new InvalidOperationException(
                         $"Authored Entity '{entity.Name}' no longer contains NetworkObject.");
        if (marker.Presence == NetworkPresence.ServerOnly)
        {
            DisableAndDestroyNetworkHierarchy(entity);
            return;
        }
        if (marker.Presence == NetworkPresence.ClientOnly)
            return;
        if (binding is not { } authored)
            throw new InvalidOperationException(
                $"Client Entity '{entity.Name}' has no authored network binding in scope {scope}.");
        if (!authored.IsPresent)
        {
            DisableAndDestroyNetworkHierarchy(entity);
            return;
        }
        Register(entity, marker, authored.NetworkEntityId, authored.Owner,
            NetworkSpawnOrigin.AuthoredScene, scope, sourceGuid, AssetId.Empty, null, true);
    }

    internal void CommitClientAuthoredScope(
        NetworkReplicationScopeId scope,
        IReadOnlyList<NetworkAuthoredBinding> bindings)
    {
        ThrowIfDisposed();
        CommitBindings(scope, new List<NetworkAuthoredBinding>(bindings));
    }

    public bool Unregister(NetworkEntityId id, bool notify = true)
    {
        ThrowIfDisposed();
        if (!_byNetworkId.Remove(id, out var record)) return false;
        _byEntity.Remove(record.Entity);
        _records.Remove(record);
        if (_recordsByScope.TryGetValue(record.Scope, out var scoped)) scoped.Remove(record);
        if (record.Origin == NetworkSpawnOrigin.AuthoredScene)
            MarkAuthoredEntityDespawned(record);
        record.Marker.UnbindDestroyed();
        RemovePlayerMappingsFor(id);
        if (notify) EntityRemoved?.Invoke(record);
        return true;
    }

    public void UnregisterScope(NetworkReplicationScopeId scope, bool notify = false)
    {
        ThrowIfDisposed();
        if (scope.IsGlobal) throw new InvalidOperationException("The Global scope cannot be unloaded independently.");
        if (_recordsByScope.TryGetValue(scope, out var records))
            foreach (var record in records.ToArray()) Unregister(record.Id, notify);
        _recordsByScope.Remove(scope);
        if (_bindingsByScope.Remove(scope, out var bindings))
            foreach (var binding in bindings)
            {
                _authoredBindings.Remove(binding);
                _authoredBySource.Remove(new AuthoredEntityKey(scope, binding.SourceGuid));
            }
        _boundScopes.Remove(scope);
        _pendingScopes.Remove(scope);
    }

    public void DespawnLocal(NetworkEntityId id, bool notify = true)
    {
        var record = GetRecord(id);
        Unregister(id, notify);
        DisableAndDestroyNetworkHierarchy(record.Entity);
    }
    public void ReconcileDestroyedEntities()
    {
        ThrowIfDisposed();
        for (var i = _records.Count - 1; i >= 0; i--)
            if (Entity.IsDestroyed(_records[i].Entity)) Unregister(_records[i].Id);
    }
    public IReadOnlyList<NetworkEntityId> GetOwnedEntities(NetworkPeerId peer)
    {
        ThrowIfDisposed();
        var result = new List<NetworkEntityId>();
        foreach (var record in _records) if (record.Owner == peer) result.Add(record.Id);
        return result;
    }

    public void Dispose() => Dispose(false);
    public void Dispose(bool destroyDynamicEntities)
    {
        if (_disposed) return;
        _disposed = true;
        var records = _records.ToArray();
        foreach (var record in records) record.Marker.UnbindDestroyed();
        _records.Clear(); _byNetworkId.Clear(); _byEntity.Clear(); _playerEntities.Clear();
        _authoredBindings.Clear(); _recordsByScope.Clear(); _bindingsByScope.Clear();
        _authoredBySource.Clear(); _boundScopes.Clear(); _pendingScopes.Clear();
        if (!destroyDynamicEntities) return;
        foreach (var record in records)
            if (record.Origin == NetworkSpawnOrigin.DynamicBlueprint && !Entity.IsDestroyed(record.Entity))
                DisableAndDestroyNetworkHierarchy(record.Entity);
    }

    private void Register(Entity entity, NetworkObject marker, NetworkEntityId id, NetworkPeerId owner,
        NetworkSpawnOrigin origin, NetworkReplicationScopeId scope, Guid sourceGuid,
        AssetId blueprintAssetId, string? blueprintAssetName, bool destroyWithOwner)
    {
        if (!scope.IsValid) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!id.IsValid) throw new ArgumentOutOfRangeException(nameof(id));
        if (!ReferenceEquals(entity.OwningScene, Scene)) throw new InvalidOperationException("Entity belongs to another Scene.");
        if (_byNetworkId.ContainsKey(id)) throw new InvalidOperationException($"Network entity ID {id} is already registered.");
        if (_byEntity.ContainsKey(entity)) throw new InvalidOperationException($"Entity '{entity.Name}' is already registered.");
        EnsureCapacity(1);
        var record = new NetworkEntityRecord
        {
            Id = id, Entity = entity, Marker = marker, Owner = owner, Origin = origin,
            Scope = scope, SourceGuid = sourceGuid, BlueprintAssetId = blueprintAssetId,
            BlueprintAssetName = blueprintAssetName, DestroyWithOwner = destroyWithOwner,
            ReplicationBindings = _replication.CreateBindings(entity)
        };
        _byNetworkId.Add(id, record); _byEntity.Add(entity, id); _records.Add(record);
        if (!_recordsByScope.TryGetValue(scope, out var scoped))
        { scoped = []; _recordsByScope.Add(scope, scoped); }
        scoped.Add(record);
        marker.BindDestroyed(HandleEntityDestroyed);
    }

    private List<Entity> GetScopeEntities(NetworkReplicationScopeId scope, SceneContentInstance? content)
    {
        var result = new List<Entity>();
        if (scope.IsGlobal)
        {
            foreach (var entity in Scene.GetAllEntities()) if (entity.ContentOwner is null) result.Add(entity);
        }
        else
            foreach (var entity in content!.OwnedEntities) if (!Entity.IsDestroyed(entity)) result.Add(entity);
        return result;
    }
    private static Dictionary<Entity, Guid>? BuildSourceIndex(NetworkReplicationScopeId scope,
        SceneContentInstance? content)
    {
        if (scope.IsGlobal) return null;
        var result = new Dictionary<Entity, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var pair in content!.EntitiesBySourceGuid) result.Add(pair.Value, pair.Key);
        return result;
    }
    private static Guid RequireSourceGuid(NetworkReplicationScopeId scope, Entity entity,
        Dictionary<Entity, Guid>? sources)
    {
        if (scope.IsGlobal) return entity.Id;
        if (sources!.TryGetValue(entity, out var source) && source != Guid.Empty) return source;
        throw new InvalidOperationException(
            $"Replicated additive Entity '{entity.Name}' has no authored source GUID. " +
            "Tiled-generated and runtime-generated NetworkObjects must be spawned explicitly.");
    }
    private void CommitBindings(NetworkReplicationScopeId scope, List<NetworkAuthoredBinding> bindings)
    {
        var sourceGuids = new HashSet<Guid>();
        foreach (var binding in bindings)
        {
            var key = new AuthoredEntityKey(scope, binding.SourceGuid);
            if (!sourceGuids.Add(binding.SourceGuid) || _authoredBySource.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Authored Entity ({scope}, {binding.SourceGuid}) is already registered.");
        }
        foreach (var binding in bindings)
            if (binding.IsPresent)
                _authoredBySource.Add(
                    new AuthoredEntityKey(scope, binding.SourceGuid),
                    binding.NetworkEntityId);
        _boundScopes.Add(scope); _bindingsByScope.Add(scope, bindings); _authoredBindings.AddRange(bindings);
    }
    private static void ValidateScopeSource(NetworkReplicationScopeId scope, SceneContentInstance? content)
    {
        if (!scope.IsValid) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope.IsGlobal && content is not null) throw new ArgumentException("Global scope cannot use additive content.");
        if (!scope.IsGlobal && (content is null || !content.IsLoaded))
            throw new ArgumentException("An additive scope requires loaded content.");
    }
    private static void ValidatePresence(NetworkObject marker, Entity entity)
    {
        if (!Enum.IsDefined(marker.Presence))
            throw new InvalidOperationException($"Entity '{entity.Name}' has unsupported presence {marker.Presence}.");
    }
    private void EnsureCapacity(int additional)
    {
        if (additional < 0 || additional > _maxNetworkEntities - _records.Count)
            throw new InvalidOperationException($"MaxNetworkEntities {_maxNetworkEntities} exceeded in epoch {SceneEpoch}.");
    }
    private void RemovePlayerMappingsFor(NetworkEntityId id)
    {
        List<NetworkPeerId>? remove = null;
        foreach (var pair in _playerEntities) if (pair.Value == id) (remove ??= []).Add(pair.Key);
        if (remove is not null) foreach (var peer in remove) _playerEntities.Remove(peer);
    }
    private void MarkAuthoredEntityDespawned(NetworkEntityRecord record)
    {
        _authoredBySource.Remove(new AuthoredEntityKey(record.Scope, record.SourceGuid));
        if (!_bindingsByScope.TryGetValue(record.Scope, out var bindings))
            return;
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index].NetworkEntityId != record.Id)
                continue;
            var binding = bindings[index];
            var tombstone = binding with { IsPresent = false };
            bindings[index] = tombstone;
            ReplaceAuthoredBinding(record.Id, tombstone);
            break;
        }
    }
    private void ReplaceAuthoredBinding(NetworkEntityId id, NetworkAuthoredBinding replacement)
    {
        for (var index = 0; index < _authoredBindings.Count; index++)
            if (_authoredBindings[index].NetworkEntityId == id)
            {
                _authoredBindings[index] = replacement;
                return;
            }
    }
    private void HandleEntityDestroyed(Entity entity)
    { if (!_disposed && _byEntity.TryGetValue(entity, out var id)) Unregister(id); }
    private NetworkEntityRecord GetRecord(NetworkEntityId id)
    {
        ThrowIfDisposed();
        return _byNetworkId.TryGetValue(id, out var record) ? record :
            throw new KeyNotFoundException($"Network entity {id} is not registered in epoch {SceneEpoch}.");
    }
    /// <summary>
    /// Destroys one network root and its ordinary hierarchy without crossing into another
    /// NetworkObject's independent lifetime. Whole-Scene and whole-content teardown deliberately
    /// do not use this boundary because those operations own every entity in their exact set.
    /// </summary>
    private static void DisableAndDestroyNetworkHierarchy(Entity entity)
    {
        if (Entity.IsDestroyed(entity))
            return;

        var nestedNetworkRoots = new List<Entity>();
        CollectNestedNetworkRoots(entity, nestedNetworkRoots);
        foreach (var nestedRoot in nestedNetworkRoots)
            nestedRoot.SetParent(null, true);

        entity.Enabled = false;
        Entity.Destroy(entity);
    }

    private static void CollectNestedNetworkRoots(Entity root, List<Entity> result)
    {
        foreach (var child in root.Children)
        {
            if (child.GetComponent<NetworkObject>() is not null)
            {
                result.Add(child);
                continue;
            }

            CollectNestedNetworkRoots(child, result);
        }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct AuthoredEntityKey(
        NetworkReplicationScopeId Scope,
        Guid SourceGuid);
}
