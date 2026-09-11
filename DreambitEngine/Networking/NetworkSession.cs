using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Dreambit.ECS;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Session;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;
using Microsoft.Xna.Framework;

namespace Dreambit.Networking;

/// <summary>Owns one active networking connection/session and survives scene transitions.</summary>
internal sealed class NetworkSession : IDisposable
{
    private readonly Queue<QueuedTransportEvent> _transportEvents = [];
    private readonly Dictionary<TransportConnectionId, NetworkPeer> _peersByConnection = [];
    private readonly Dictionary<NetworkPeerId, NetworkPeer> _peersById = [];
    private readonly NetworkOptions _options;
    private readonly NetworkMessageRegistry _messages;
    private readonly NetworkReplicationRegistry _replication;
    private bool _disposed;
    private bool _acceptTransportEvents = true;
    private uint _nextPeerId = 1;
    private ulong _nextEntityId = 1;
    private TransportConnectionId _serverConnection;
    private string? _pendingSceneKey;
    private NetworkSceneEpoch _pendingSceneEpoch;
    private readonly Dictionary<ReplicationStateKey, uint> _lastStateSequences = [];
    private readonly Dictionary<ClientTransformStateKey, uint> _lastClientTransformSequences = [];
    private readonly Dictionary<NetworkEntityId, PendingLiveSpawn> _pendingLiveSpawns = [];
    private readonly Dictionary<NetworkReplicationScopeId, NetworkReplicationScope> _scopes = [];
    private readonly HashSet<NetworkReplicationScopeId> _retiredScopes = [];
    private readonly Dictionary<NetworkReplicationScopeId, ScopeSourceIdentity> _knownScopeSources = [];
    private readonly Dictionary<NetworkReplicationScopeId, ClientBaselineState> _clientScopeBaselines = [];
    private readonly Dictionary<NetworkReplicationScopeId, List<SuspendedEntityState>> _suspendedScopeEntities = [];
    private readonly Dictionary<NetworkReplicationScopeId, ClientNetworkScopeLoadOperation> _clientScopeLoads = [];
    private readonly List<NetworkReplicationScopeId> _clientScopeLoadOrder = [];
    private readonly Dictionary<NetworkReplicationScopeId, NetworkScopeLoadStatus> _scopeLoadStatuses = [];
    private readonly Queue<QueuedTransportEvent> _deferredClientStructuralEvents = [];
    private readonly object _scopeCoordinator = new();
    private int _clientScopeLoadRoundRobinIndex;
    private int _deferredClientStructuralBytes;
    private ulong _clientScopeLoadFrame;
    private bool _advancingClientScopeLoadItem;
    private double _lastApplyInboundMilliseconds;
    private double _maximumApplyInboundMilliseconds;
    private TimeSpan _lastClientScopeLoadSliceDuration;
    private TimeSpan _maximumClientScopeLoadSliceDuration;
    private TimeSpan _lastDeferredReplayDuration;
    private double _snapshotElapsed;
    private uint _stateSequence;
    private ClientBaselineState? _clientBaseline;
    private bool _clientSceneLoadedSent;
    private bool _clientSceneReady;
    private uint _nextScopeId = 2;

    public NetworkSession(
        NetworkRole role,
        INetworkTransport transport,
        NetworkOptions options,
        NetworkMessageRegistry messages,
        NetworkReplicationRegistry replication)
    {
        if (role == NetworkRole.Offline)
            throw new ArgumentOutOfRangeException(nameof(role));

        Role = role;
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _replication = replication ?? throw new ArgumentNullException(nameof(replication));
        _options.Validate();
        Transport.Capabilities.Validate();
        if (Transport.Capabilities.MaxChannels < 4)
            throw new ArgumentException(
                "Dreambit networking requires at least four transport channels.",
                nameof(transport));
        _replication.ValidateForTransport(Transport.Capabilities, _options.MaxProtocolPayload);
        SessionId = IsServer ? Guid.NewGuid() : Guid.Empty;
        ValidateControlPacketsForTransport();
    }

    public event Action<NetworkPeerId>? PeerConnected;
    public event Action<NetworkPeerId, TransportDisconnectReason, string?>? PeerDisconnected;
    public event Action<TransportDisconnectReason, string?>? ConnectionFailed;
    public event Action<string, NetworkSceneEpoch>? SceneChangeRequested;
    public event Action<NetworkPeerId, NetworkReplicationScopeId>? PeerScopeReady;
    public event Action<NetworkPeerId, NetworkReplicationScopeId>? PeerScopeUnloaded;
    public event Action<NetworkScopeLoadStatus>? ScopeLoadStatusChanged;

    public NetworkRole Role { get; }
    public INetworkTransport Transport { get; }
    public Guid SessionId { get; private set; }
    public NetworkPeerId LocalPeerId { get; private set; }
    public bool IsServer => Role is NetworkRole.Server or NetworkRole.Host;
    public bool IsClient => Role is NetworkRole.Client or NetworkRole.Host;
    public bool IsHost => Role == NetworkRole.Host;
    public bool IsConnected => IsHost || (Role == NetworkRole.Client && LocalPeerId.IsValid);
    public int ReadyPeerCount => _peersById.Values.Count(peer => peer.Phase == NetworkConnectionPhase.Ready);
    public NetworkSceneEpoch SceneEpoch { get; private set; }
    public NetworkStructuralRevision StructuralRevision { get; private set; }
    public ulong ServerTick { get; private set; }
    public string? CurrentSceneKey { get; private set; }
    public NetworkWorld? World { get; private set; }
    internal IReadOnlyDictionary<NetworkReplicationScopeId, NetworkReplicationScope> Scopes => _scopes;
    internal bool HasPendingSynchronizedScene =>
        _pendingSceneEpoch.IsValid && _pendingSceneKey is not null;
    internal bool HasPendingScopeLoads => _clientScopeLoads.Values.Any(operation => !operation.IsTerminal);
    internal double LastApplyInboundMilliseconds => _lastApplyInboundMilliseconds;
    internal double MaximumApplyInboundMilliseconds => _maximumApplyInboundMilliseconds;
    internal TimeSpan LastClientScopeLoadSliceDuration => _lastClientScopeLoadSliceDuration;
    internal TimeSpan MaximumClientScopeLoadSliceDuration => _maximumClientScopeLoadSliceDuration;
    internal TimeSpan LastDeferredReplayDuration => _lastDeferredReplayDuration;

    internal bool TryGetScopeLoadStatus(
        NetworkReplicationScopeId scope,
        out NetworkScopeLoadStatus status) =>
        _scopeLoadStatuses.TryGetValue(scope, out status);

    internal IReadOnlyList<NetworkScopeLoadStatus> GetScopeLoadStatuses() =>
        _scopeLoadStatuses.Values.ToArray();

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsHost)
        {
            var localPeer = new NetworkPeer
            {
                Connection = TransportConnectionId.None,
                PeerId = AllocatePeerId(),
                Phase = NetworkConnectionPhase.Ready,
                IsLocal = true
            };
            LocalPeerId = localPeer.PeerId;
            _peersById.Add(localPeer.PeerId, localPeer);
        }

        if (IsServer)
            Transport.StartServer();
        else
            Transport.Connect();
    }

    public void PollTransport()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_acceptTransportEvents)
            return;

        // Leave excess events in the transport until the next inbound pass drains this queue.
        // A valid baseline can contain many more packets than one frame's staging capacity.
        while (_transportEvents.Count < _options.MaxQueuedTransportEvents &&
               Transport.TryPollEvent(out var transportEvent))
        {
            if (transportEvent.Payload.Length > _options.MaxProtocolPayload + NetworkProtocol.HeaderLength)
            {
                if (transportEvent.Connection.IsValid)
                    Transport.Disconnect(transportEvent.Connection, TransportDisconnectReason.ProtocolError);
                continue;
            }

            // Transport payload ownership ends at the next poll. Copy into bounded protocol work.
            _transportEvents.Enqueue(new QueuedTransportEvent(
                transportEvent.Kind,
                transportEvent.Connection,
                transportEvent.Payload.ToArray(),
                transportEvent.Delivery,
                transportEvent.Channel,
                transportEvent.Reason,
                transportEvent.Diagnostic));
        }
    }

    public void ApplyInbound()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var started = Stopwatch.GetTimestamp();
        while (_transportEvents.TryDequeue(out var transportEvent))
        {
            try
            {
                switch (transportEvent.Kind)
                {
                    case TransportEventKind.Connected:
                        HandleConnected(transportEvent.Connection);
                        break;
                    case TransportEventKind.Data:
                        HandleData(transportEvent);
                        break;
                    case TransportEventKind.Disconnected:
                    case TransportEventKind.Error:
                        HandleDisconnected(
                            transportEvent.Connection,
                            transportEvent.Reason == TransportDisconnectReason.None
                                ? TransportDisconnectReason.TransportError
                                : transportEvent.Reason,
                            transportEvent.Diagnostic);
                        break;
                    default:
                        throw new NetworkProtocolException(
                            $"Unknown transport event kind {(byte)transportEvent.Kind}.");
                }
            }
            catch (NetworkProtocolException exception)
            {
                RejectProtocolError(transportEvent.Connection, exception.Message);
                QuarantineConnectionWork(transportEvent.Connection, exception.Message);
            }
        }
        _lastApplyInboundMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (_lastApplyInboundMilliseconds > _maximumApplyInboundMilliseconds)
            _maximumApplyInboundMilliseconds = _lastApplyInboundMilliseconds;
    }

    public void AdvanceClientScopeLoads()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = _clientScopeLoadFrame++;
        if (!IsClient || IsHost)
            return;

        foreach (var operation in _clientScopeLoads.Values)
            operation.WorkItemsAdvancedLastFrame = 0;

        var frameStarted = Stopwatch.GetTimestamp();
        var workItems = 0;
        var visitedWithoutWork = 0;
        while (_clientScopeLoadOrder.Count != 0 &&
               workItems < _options.MaxClientScopeLoadWorkItemsPerFrame &&
               Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds <
               _options.ClientScopeLoadBudgetMilliseconds)
        {
            if (_clientScopeLoadRoundRobinIndex >= _clientScopeLoadOrder.Count)
                _clientScopeLoadRoundRobinIndex = 0;
            var scopeId = _clientScopeLoadOrder[_clientScopeLoadRoundRobinIndex];
            _clientScopeLoadRoundRobinIndex =
                (_clientScopeLoadRoundRobinIndex + 1) % _clientScopeLoadOrder.Count;
            if (!_clientScopeLoads.TryGetValue(scopeId, out var operation) ||
                operation.IsTerminal || frame < operation.EarliestAdvanceFrame)
            {
                visitedWithoutWork++;
                if (visitedWithoutWork >= _clientScopeLoadOrder.Count)
                    break;
                continue;
            }

            visitedWithoutWork = 0;
            var itemStarted = Stopwatch.GetTimestamp();
            var itemPhase = operation.Phase;
            try
            {
                _advancingClientScopeLoadItem = true;
                if (!AdvanceClientScopeLoad(operation))
                {
                    visitedWithoutWork++;
                    if (visitedWithoutWork >= _clientScopeLoadOrder.Count)
                        break;
                    continue;
                }
            }
            catch (Exception exception)
            {
                FailClientScopeLoad(operation, exception, false);
                if (_serverConnection.IsValid)
                {
                    RejectProtocolError(
                        _serverConnection,
                        $"Client scope {operation.Scope} loading failed: {exception.Message}");
                    QuarantineConnectionWork(_serverConnection, exception.Message);
                }
            }
            finally
            {
                _advancingClientScopeLoadItem = false;
            }
            var duration = Stopwatch.GetElapsedTime(itemStarted);
            RecordClientScopeLoadPhaseDuration(operation, itemPhase, duration);
            operation.LastWorkItemDuration = duration;
            if (duration > operation.MaximumWorkItemDuration)
                operation.MaximumWorkItemDuration = duration;
            operation.WorkItemsAdvancedLastFrame++;
            operation.CompletedItems++;
            workItems++;
            if (operation.IsTerminal)
                PublishScopeLoadStatus(operation, true);
        }

        var replayStarted = Stopwatch.GetTimestamp();
        ReplayDeferredClientStructuralPackets(frameStarted);
        _lastDeferredReplayDuration = Stopwatch.GetElapsedTime(replayStarted);
        _lastClientScopeLoadSliceDuration = Stopwatch.GetElapsedTime(frameStarted);
        if (_lastClientScopeLoadSliceDuration > _maximumClientScopeLoadSliceDuration)
            _maximumClientScopeLoadSliceDuration = _lastClientScopeLoadSliceDuration;
        foreach (var operation in _clientScopeLoads.Values)
            if (operation.WorkItemsAdvancedLastFrame > 0)
                PublishScopeLoadStatus(operation, false);
    }

    public void BeforeFixedStep(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
    }

    public void AfterFixedStep(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (IsServer)
            ServerTick++;

        if (IsServer || Role == NetworkRole.Client)
            _snapshotElapsed += 1d / 60d;
    }

    public void AfterSceneTick(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (ReferenceEquals(World?.Scene, scene))
        {
            World.ReconcileDestroyedEntities();
            if (_snapshotElapsed >= 1d / _options.ReplicationRate)
            {
                _snapshotElapsed %= 1d / _options.ReplicationRate;
                if (IsServer)
                    SendSnapshotNow();
                else if (Role == NetworkRole.Client)
                    SendClientTransformsNow();
            }
        }
    }

    public void BeforeSceneUnload(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.SetStartPreparationGate(null);
        if (ReferenceEquals(World?.Scene, scene))
        {
            CancelAllClientScopeLoads("The synchronized Scene is unloading.");
            World.EntityRemoved -= HandleWorldEntityRemoved;
            World.Dispose(false);
            World = null;
            ClearScopeState(false);
            _lastStateSequences.Clear();
            _lastClientTransformSequences.Clear();
            _pendingLiveSpawns.Clear();
            _clientBaseline = null;
            _clientSceneLoadedSent = false;
            _clientSceneReady = false;
        }
    }

    public void AfterSceneAssigned(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (World is not null)
            throw new InvalidOperationException("The previous NetworkWorld was not released before assigning a Scene.");
        var synchronizedClientAssignment = !IsServer && _pendingSceneEpoch.IsValid;
        SceneEpoch = _pendingSceneEpoch.IsValid
            ? _pendingSceneEpoch
            : NextSceneEpoch(SceneEpoch);
        if (_pendingSceneKey is not null)
            CurrentSceneKey = _pendingSceneKey;
        _pendingSceneEpoch = NetworkSceneEpoch.None;
        _pendingSceneKey = null;
        StructuralRevision = NetworkStructuralRevision.None;
        _nextScopeId = 2;
        ClearScopeState(false);
        World = new NetworkWorld(
            scene,
            SceneEpoch,
            IsServer,
            _replication,
            _options.MaxNetworkEntities);
        World.EntityRemoved += HandleWorldEntityRemoved;
        var globalScope = new NetworkReplicationScope(
            NetworkReplicationScopeId.Global,
            SceneEpoch,
            AssetId.Empty,
            CurrentSceneKey,
            null)
        {
            IsReady = IsServer || !synchronizedClientAssignment
        };
        _scopes.Add(NetworkReplicationScopeId.Global, globalScope);
        scene.SetStartPreparationGate(
            IsServer || synchronizedClientAssignment
                ? PrepareSceneStart
                : null);
        _clientBaseline = null;
        _clientSceneLoadedSent = false;
        _clientSceneReady = globalScope.IsReady;
        if (!globalScope.IsReady)
            World.MarkScopePending(NetworkReplicationScopeId.Global);
        if (IsServer && scene.State == SceneState.Running && !World.AuthoredEntitiesBound)
            BindServerAuthoredEntities();
    }

    public bool PrepareSceneStart(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!ReferenceEquals(World?.Scene, scene))
            throw new InvalidOperationException("Scene startup was requested for an unbound NetworkWorld.");
        if (IsServer && !World.AuthoredEntitiesBound)
            BindServerAuthoredEntities();
        if (IsServer)
            return true;
        if (_clientSceneReady)
            return true;
        if (!_clientSceneLoadedSent)
        {
            if (!_serverConnection.IsValid)
                return false;
            SendPacket(
                _serverConnection,
                NetworkProtocolMessage.SceneLoaded,
                writer => writer.WriteString(CurrentSceneKey, 256),
                SceneEpoch,
                ServerTick,
                StructuralRevision);
            _clientSceneLoadedSent = true;
        }
        return false;
    }

    public void StopIntake()
    {
        _acceptTransportEvents = false;
    }

    internal NetworkReplicationScopeId LoadScope(string sceneAssetName)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can load replication scopes.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        if (Encoding.UTF8.GetByteCount(sceneAssetName) > _options.MaxScopeAssetNameBytes)
            throw new ArgumentException(
                $"A scope asset name cannot exceed {_options.MaxScopeAssetNameBytes} UTF-8 bytes.",
                nameof(sceneAssetName));
        if (_scopes.Count >= _options.MaxReplicationScopes)
            throw new InvalidOperationException(
                $"The configured MaxReplicationScopes limit of {_options.MaxReplicationScopes} was reached.");

        var blueprint = Resources.LoadAsset<SceneBlueprint>(sceneAssetName) ??
            throw new InvalidOperationException($"Scene asset '{sceneAssetName}' could not be loaded.");
        if (blueprint.AssetId.IsEmpty)
            throw new InvalidOperationException(
                $"Network scope source '{sceneAssetName}' must have a stable AssetId.");
        var manifestValidation = EncodePacket(
            NetworkProtocolMessage.ScopeLoad,
            writer =>
            {
                writer.WriteUInt32(2);
                writer.WriteGuid(blueprint.AssetId.Value);
                writer.WriteString(sceneAssetName, _options.MaxScopeAssetNameBytes);
            },
            SceneEpoch,
            ServerTick,
            NetworkStructuralRevision.None);
        EnsurePacketFitsReliableTransport(manifestValidation, "ScopeLoad manifest");
        var scopeId = AllocateScopeId();
        SceneContentInstance? content = null;
        NetworkReplicationScope? scope = null;
        try
        {
            content = World.Scene.LoadNetworkAdditive(blueprint, sceneAssetName, _scopeCoordinator);
            scope = new NetworkReplicationScope(
                scopeId, SceneEpoch, blueprint.AssetId, sceneAssetName, content);
            _scopes.Add(scopeId, scope);
            World.BindServerAuthoredScope(scopeId, content, AllocateEntityId);
            if (World.GetAuthoredBindings(scopeId).Count > _options.MaxScopedAuthoredEntities)
                throw new InvalidOperationException(
                    $"Scope {scopeId} exceeds MaxScopedAuthoredEntities {_options.MaxScopedAuthoredEntities}.");
            foreach (var record in World.GetRecords(scopeId))
                NotifyNetworkSpawnReady(record, SceneEpoch, ServerTick);
            StructuralRevision = NextStructuralRevision(StructuralRevision);
            return scopeId;
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<Exception>();
            if (scope is not null)
            {
                scope.IsLoaded = false;
                scope.IsReady = false;
            }
            _scopes.Remove(scopeId);
            _retiredScopes.Add(scopeId);
            try
            {
                World.UnregisterScope(scopeId, false);
            }
            catch (Exception cleanupException)
            {
                cleanupErrors.Add(cleanupException);
            }
            if (content?.IsLoaded == true)
                try
                {
                    World.Scene.UnloadNetworkContent(content, _scopeCoordinator);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (cleanupErrors.Count == 0)
                throw;
            cleanupErrors.Insert(0, exception);
            throw new AggregateException(
                $"Replication scope {scopeId} failed to load and rollback also reported errors.",
                cleanupErrors);
        }
    }

    internal void UnloadScope(NetworkReplicationScopeId scopeId)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can unload replication scopes.");
        var scope = GetAdditiveScope(scopeId);
        foreach (var peer in _peersById.Values)
            if (peer.ScopeSubscriptions.ContainsKey(scopeId))
                throw new InvalidOperationException(
                    $"Scope {scopeId} cannot be unloaded while peer {peer.PeerId} is subscribed.");

        // Retire network visibility before any component/content cleanup callback can re-enter.
        scope.IsLoaded = false;
        scope.IsReady = false;
        _scopes.Remove(scopeId);
        _retiredScopes.Add(scopeId);
        StructuralRevision = NextStructuralRevision(StructuralRevision);
        var cleanupErrors = new List<Exception>();
        try
        {
            World.UnregisterScope(scopeId, false);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception);
        }
        if (scope.Content?.IsLoaded == true)
            try
            {
                World.Scene.UnloadNetworkContent(scope.Content, _scopeCoordinator);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        if (cleanupErrors.Count != 0)
            throw new AggregateException(
                $"Replication scope {scopeId} was retired, but one or more local cleanup operations failed.",
                cleanupErrors);
    }

    internal void Subscribe(NetworkPeerId peerId, NetworkReplicationScopeId scopeId)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can change scope subscriptions.");
        var scope = GetAdditiveScope(scopeId);
        var peer = GetPeer(peerId);
        if (peer.ScopeSubscriptions.ContainsKey(scopeId))
            return;
        if (peer.ScopeSubscriptions.Count >= _options.MaxScopeSubscriptionsPerPeer)
            throw new InvalidOperationException(
                $"Peer {peerId} reached MaxScopeSubscriptionsPerPeer {_options.MaxScopeSubscriptionsPerPeer}.");

        var subscription = new NetworkScopeSubscription
        {
            Scope = scopeId,
            Phase = peer.IsLocal
                ? NetworkScopeSubscriptionPhase.Ready
                : peer.Phase == NetworkConnectionPhase.Ready
                    ? NetworkScopeSubscriptionPhase.AwaitingLoaded
                    : NetworkScopeSubscriptionPhase.Pending
        };
        peer.ScopeSubscriptions.Add(scopeId, subscription);
        if (peer.IsLocal)
        {
            PeerScopeReady?.Invoke(peerId, scopeId);
            return;
        }
        if (subscription.Phase == NetworkScopeSubscriptionPhase.AwaitingLoaded)
        {
            try
            {
                SendScopeLoad(peer, scope, subscription);
            }
            catch
            {
                peer.ScopeSubscriptions.Remove(scopeId);
                throw;
            }
        }
    }

    internal void Unsubscribe(NetworkPeerId peerId, NetworkReplicationScopeId scopeId)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can change scope subscriptions.");
        if (scopeId.IsGlobal)
            throw new InvalidOperationException("The Global scope cannot be unsubscribed.");
        var peer = GetPeer(peerId);
        if (!peer.ScopeSubscriptions.TryGetValue(scopeId, out var subscription))
            return;
        if (peer.IsLocal)
        {
            peer.ScopeSubscriptions.Remove(scopeId);
            RemoveProjectedPlayersInScope(peer, scopeId);
            PeerScopeUnloaded?.Invoke(peerId, scopeId);
            return;
        }
        if (subscription.Phase == NetworkScopeSubscriptionPhase.Pending)
        {
            peer.ScopeSubscriptions.Remove(scopeId);
            return;
        }
        if (subscription.Phase == NetworkScopeSubscriptionPhase.Unloading)
            return;

        subscription.Phase = NetworkScopeSubscriptionPhase.Unloading;
        RemoveProjectedPlayersInScope(peer, scopeId);
        var revision = AdvancePeerRevision(peer);
        subscription.UnloadRevision = revision;
        SendPacket(peer.Connection, NetworkProtocolMessage.ScopeUnload,
            writer => writer.WriteUInt32(scopeId.Value), SceneEpoch, ServerTick, revision);
    }

    internal bool TryGetScope(NetworkReplicationScopeId scopeId, out NetworkReplicationScope? scope)
    {
        if (_scopes.TryGetValue(scopeId, out var found) && found.IsLoaded)
        { scope = found; return true; }
        scope = null; return false;
    }

    internal bool IsPeerSubscribed(NetworkPeerId peerId, NetworkReplicationScopeId scopeId) =>
        _peersById.TryGetValue(peerId, out var peer) &&
        (scopeId.IsGlobal || peer.ScopeSubscriptions.ContainsKey(scopeId));

    internal bool IsPeerScopeReady(NetworkPeerId peerId, NetworkReplicationScopeId scopeId) =>
        _peersById.TryGetValue(peerId, out var peer) &&
        (scopeId.IsGlobal
            ? peer.IsLocal || peer.Phase == NetworkConnectionPhase.Ready
            : peer.ScopeSubscriptions.TryGetValue(scopeId, out var subscription) &&
              subscription.Phase == NetworkScopeSubscriptionPhase.Ready);

    internal Entity Spawn(
        EntityBlueprint blueprint,
        NetworkSpawnOptions? options = null) =>
        Spawn(blueprint, static _ => { }, options);

    internal Entity Spawn(
        EntityBlueprint blueprint,
        Action<Entity> initialize,
        NetworkSpawnOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(initialize);

        if (!IsServer)
            throw new InvalidOperationException("Only the server can spawn network entities.");
        if (World is null)
            throw new InvalidOperationException("A Scene must be assigned before spawning a network entity.");
        ArgumentNullException.ThrowIfNull(blueprint);
        if (blueprint.AssetId.IsEmpty)
            throw new InvalidOperationException(
                "A remotely replicated Blueprint must have a stable AssetId.");
        if (blueprint.AssetName is not null && Encoding.UTF8.GetByteCount(blueprint.AssetName) > 1024)
            throw new InvalidOperationException(
                "A remotely replicated Blueprint fallback name cannot exceed 1024 UTF-8 bytes.");

        options ??= new NetworkSpawnOptions();
        if (!options.Scope.IsValid)
            throw new InvalidOperationException("A network spawn requires a valid replication scope.");
        var targetScope = GetScopeForSpawn(options.Scope);
        if (options.Owner.IsValid && !CanAssignScopeToPeer(options.Owner, options.Scope))
            throw new InvalidOperationException(
                $"Peer {options.Owner} is not ready for replication scope {options.Scope}.");
        Entity? entity = null;
        var networkId = NetworkEntityId.None;
        var registered = false;
        NetworkEntityRecord? record = null;
        try
        {
            entity = options.Scope.IsGlobal
                ? World.Scene.CreateNetworkEntity(
                    blueprint, options.Enabled, options.Position, options.Rotation, options.Scale)
                : World.Scene.CreateNetworkContentEntity(
                    targetScope.Content!, blueprint, _scopeCoordinator,
                    options.Enabled, options.Position, options.Rotation, options.Scale,
                    initialize);
            if (options.Scope.IsGlobal)
                initialize(entity);
            networkId = AllocateEntityId();
            World.RegisterDynamicEntity(
                entity,
                networkId,
                options.Owner,
                blueprint.AssetId,
                blueprint.AssetName,
                options.DestroyWithOwner,
                options.Scope);
            registered = true;

            if (!World.TryGetRecord(networkId, out record) || record is null)
                throw new InvalidOperationException("The registered network spawn could not be indexed.");
            var validationRevision = NextStructuralRevision(StructuralRevision);
            var validationPacket = EncodePacket(
                NetworkProtocolMessage.Spawn,
                writer => WriteSpawn(writer, entity, networkId, blueprint, options),
                SceneEpoch,
                ServerTick,
                validationRevision);
            EnsurePacketFitsReliableTransport(validationPacket, "Spawn");

            var sequence = NextStateSequence();
            foreach (var binding in record.ReplicationBindings)
                EnsurePacketFitsReliableTransport(
                    EncodeStateRecord(record.Scope, networkId, binding, sequence, validationRevision),
                    "initial Component state");
            var validationReady = EncodePacket(
                NetworkProtocolMessage.SpawnReady,
                writer =>
                {
                    writer.WriteUInt32(record.Scope.Value);
                    writer.WriteUInt64(networkId.Value);
                },
                SceneEpoch,
                ServerTick,
                validationRevision);
            EnsurePacketFitsReliableTransport(validationReady, "SpawnReady");
            NotifyNetworkSpawnReady(record, SceneEpoch, ServerTick);
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<Exception>();
            if (registered && networkId.IsValid)
                try
                {
                    World.Unregister(networkId, false);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (entity is not null)
                try
                {
                    World.Scene.DestroyEntityHierarchyImmediately(entity);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (cleanupErrors.Count != 0)
            {
                cleanupErrors.Insert(0, exception);
                throw new AggregateException(
                    "Network spawn failed and one or more rollback operations also failed.",
                    cleanupErrors);
            }
            throw;
        }

        StructuralRevision = NextStructuralRevision(StructuralRevision);
        foreach (var peer in _peersById.Values)
        {
            if (peer.IsLocal || !PeerReceivesStructure(peer, options.Scope))
                continue;
            var revision = AdvancePeerRevision(peer);
            var spawnPacket = EncodePacket(
                NetworkProtocolMessage.Spawn,
                writer => WriteSpawn(writer, entity!, networkId, blueprint, options),
                SceneEpoch, ServerTick, revision);
            Transport.Send(peer.Connection, spawnPacket, NetworkDelivery.ReliableOrdered, 0);
            var sequence = NextStateSequence();
            foreach (var binding in record!.ReplicationBindings)
            {
                var statePacket = EncodeStateRecord(
                    record.Scope, networkId, binding, sequence, revision);
                Transport.Send(peer.Connection, statePacket, NetworkDelivery.ReliableOrdered, 0);
            }
            var readyPacket = EncodePacket(NetworkProtocolMessage.SpawnReady, writer =>
            {
                writer.WriteUInt32(record.Scope.Value);
                writer.WriteUInt64(networkId.Value);
            }, SceneEpoch, ServerTick, revision);
            Transport.Send(peer.Connection, readyPacket, NetworkDelivery.ReliableOrdered, 0);
        }
        return entity;
    }

    internal void BeginServerSceneChange(string sceneKey)
    {
        if (!IsServer)
            throw new InvalidOperationException("Only the server controls synchronized Scene changes.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneKey);
        if (Encoding.UTF8.GetByteCount(sceneKey) > 256)
            throw new ArgumentException(
                "A synchronized Scene key cannot exceed 256 UTF-8 bytes.",
                nameof(sceneKey));
        if (_pendingSceneEpoch.IsValid)
            throw new InvalidOperationException("A synchronized Scene change is already pending.");

        _pendingSceneKey = sceneKey;
        _pendingSceneEpoch = NextSceneEpoch(SceneEpoch);
        foreach (var peer in _peersById.Values)
        {
            if (peer.IsLocal || !peer.PeerId.IsValid)
                continue;
            peer.ProjectedStructuralRevision = NetworkStructuralRevision.None;
            peer.ScopeSubscriptions.Clear();
            peer.ProjectedPlayerEntities.Clear();
            peer.Phase = NetworkConnectionPhase.AwaitingSceneLoad;
            SendPacket(
                peer.Connection,
                NetworkProtocolMessage.SceneChange,
                writer => writer.WriteString(sceneKey, 256),
                _pendingSceneEpoch,
                ServerTick,
                NetworkStructuralRevision.None);
        }
    }

    internal void Despawn(Entity entity)
    {
        if (!IsServer)
            throw new InvalidOperationException("Only the server can despawn network entities.");
        ArgumentNullException.ThrowIfNull(entity);
        if (World is null || !World.TryGetNetworkId(entity, out var id))
            throw new InvalidOperationException("The Entity is not registered in the current NetworkWorld.");
        entity.Scene.Services.EnsureCanRemove(entity);
        DespawnAuthoritative(id);
    }

    internal void SetPlayerEntity(NetworkPeerId peerId, Entity entity)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can assign player entities.");
        if (!World.TryGetNetworkId(entity, out var entityId))
            throw new InvalidOperationException("The player Entity is not registered in the current NetworkWorld.");
        var scope = World.GetScope(entityId);
        if (!CanAssignScopeToPeer(peerId, scope))
            throw new InvalidOperationException(
                $"Peer {peerId} is not ready for replication scope {scope}.");
        World.SetPlayerEntity(peerId, entityId);
        AdvanceStructuralRevision();
        PublishPlayerMapping(peerId, entityId, scope);
    }

    internal void SetOwner(Entity entity, NetworkPeerId owner)
    {
        if (!IsServer || World is null)
            throw new InvalidOperationException("Only an active server can change network ownership.");
        if (!World.TryGetNetworkId(entity, out var entityId))
            throw new InvalidOperationException("The Entity is not registered in the current NetworkWorld.");
        var scope = World.GetScope(entityId);
        if (owner.IsValid && !CanAssignScopeToPeer(owner, scope))
            throw new InvalidOperationException(
                $"Peer {owner} is not ready for replication scope {scope}.");
        if (owner.IsValid && entity.ContainsSceneServiceInHierarchy())
            throw new InvalidOperationException(
                "Scene service hosts are scene-owned and cannot be assigned to a network peer.");
        World.SetOwner(entityId, owner);
        AdvanceStructuralRevision();
        PublishOwnership(entityId, owner, scope);
    }

    internal void SendToServer<T>(
        T message,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        if (!IsClient || !LocalPeerId.IsValid)
            throw new InvalidOperationException("An active client or host is required to send to the server.");
        var registration = _messages.GetByType(typeof(T));
        if (registration.Direction is not (
                NetworkMessageDirection.ClientToServer or NetworkMessageDirection.Bidirectional))
            throw new InvalidOperationException(
                $"Networking message '{typeof(T).FullName}' is not registered for client-to-server delivery.");
        var payload = EncodeUserMessage(registration, message!);

        if (IsHost)
        {
            DispatchUserMessage(
                payload,
                inboundFromClient: true,
                LocalPeerId,
                SceneEpoch,
                ServerTick);
            return;
        }

        if (!_serverConnection.IsValid)
            throw new InvalidOperationException("The client has no active server connection.");
        SendPacket(
            _serverConnection,
            NetworkProtocolMessage.UserMessage,
            writer => writer.WriteBytes(payload),
            SceneEpoch,
            ServerTick,
            StructuralRevision,
            delivery,
            delivery == NetworkDelivery.ReliableOrdered ? (byte)1 : (byte)3);
    }

    internal void Send<T>(
        NetworkPeerId peerId,
        T message,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        if (!IsServer)
            throw new InvalidOperationException("Only the server can send directly to a peer.");
        var registration = _messages.GetByType(typeof(T));
        if (registration.Direction is not (
                NetworkMessageDirection.ServerToClient or NetworkMessageDirection.Bidirectional))
            throw new InvalidOperationException(
                $"Networking message '{typeof(T).FullName}' is not registered for server-to-client delivery.");
        if (!_peersById.TryGetValue(peerId, out var peer) ||
            peer.Phase != NetworkConnectionPhase.Ready)
            throw new KeyNotFoundException($"Network peer {peerId} is not ready.");
        var payload = EncodeUserMessage(registration, message!);
        if (peer.IsLocal)
        {
            DispatchUserMessage(
                payload,
                inboundFromClient: false,
                NetworkPeerId.None,
                SceneEpoch,
                ServerTick);
            return;
        }
        SendPacket(
            peer.Connection,
            NetworkProtocolMessage.UserMessage,
            writer => writer.WriteBytes(payload),
            SceneEpoch,
            ServerTick,
            peer.ProjectedStructuralRevision,
            delivery,
            delivery == NetworkDelivery.ReliableOrdered ? (byte)1 : (byte)2);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CancelAllClientScopeLoads("The networking session stopped.");
        _disposed = true;
        _acceptTransportEvents = false;
        _transportEvents.Clear();
        var cleanupErrors = new List<Exception>();
        if (World is not null)
        {
            World.Scene.SetStartPreparationGate(null);
            World.EntityRemoved -= HandleWorldEntityRemoved;
        }
        TryCleanup(() => ClearScopeState(true));
        _peersByConnection.Clear();
        _peersById.Clear();
        _pendingLiveSpawns.Clear();
        _lastClientTransformSequences.Clear();
        TryCleanup(() => World?.Dispose(true));
        World = null;
        TryCleanup(Transport.Stop);
        TryCleanup(Transport.Dispose);
        if (cleanupErrors.Count != 0)
            throw new AggregateException(
                "One or more resources failed while stopping the networking session.",
                cleanupErrors);

        void TryCleanup(Action cleanup)
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
    }

    private void HandleConnected(TransportConnectionId connection)
    {
        if (!connection.IsValid)
            throw new NetworkProtocolException("Transport reported a connection with ID zero.");
        if (_peersByConnection.ContainsKey(connection))
            throw new NetworkProtocolException($"Transport connection {connection} was reported twice.");

        if (IsServer)
        {
            _peersByConnection.Add(connection, new NetworkPeer
            {
                Connection = connection,
                Phase = NetworkConnectionPhase.AwaitingHello
            });
            return;
        }

        if (_serverConnection.IsValid)
            throw new NetworkProtocolException("Client transport reported more than one server connection.");

        _serverConnection = connection;
        _peersByConnection.Add(connection, new NetworkPeer
        {
            Connection = connection,
            Phase = NetworkConnectionPhase.AwaitingWelcome
        });
        SendHello(connection);
    }

    private void HandleData(QueuedTransportEvent transportEvent, bool allowDeferral = true)
    {
        if (!_peersByConnection.TryGetValue(transportEvent.Connection, out var peer))
            throw new NetworkProtocolException(
                $"Received data for unknown transport connection {transportEvent.Connection}.");
        if (!NetworkProtocol.TryDecode(
                transportEvent.Payload,
                _options.MaxProtocolPayload,
                out var packet,
                out var error))
            throw new NetworkProtocolException(error ?? "Malformed network packet.");

        if (allowDeferral && TryDeferClientStructuralPacket(transportEvent, packet))
            return;
        if (TryConsumeRetiredScopeStructuralPacket(packet))
            return;

        switch (packet.Header.Message)
        {
            case NetworkProtocolMessage.Hello:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                HandleHello(peer, packet);
                break;
            case NetworkProtocolMessage.Welcome:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                HandleWelcome(peer, packet);
                break;
            case NetworkProtocolMessage.Reject:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                HandleReject(peer, packet);
                break;
            case NetworkProtocolMessage.Spawn:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleSpawn(packet);
                break;
            case NetworkProtocolMessage.SpawnReady:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleSpawnReady(packet);
                break;
            case NetworkProtocolMessage.Despawn:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleDespawn(packet);
                break;
            case NetworkProtocolMessage.SceneChange:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleSceneChange(peer, packet);
                break;
            case NetworkProtocolMessage.SceneLoaded:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleSceneLoaded(peer, packet);
                break;
            case NetworkProtocolMessage.Baseline:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleBaseline(peer, packet);
                break;
            case NetworkProtocolMessage.Ready:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleReady(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeLoad:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeLoad(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeLoaded:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeLoaded(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeBaseline:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeBaseline(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeReady:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeReady(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeUnload:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeUnload(peer, packet);
                break;
            case NetworkProtocolMessage.ScopeUnloaded:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header)) return;
                HandleScopeUnloaded(peer, packet);
                break;
            case NetworkProtocolMessage.PlayerEntity:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandlePlayerEntity(packet);
                break;
            case NetworkProtocolMessage.Ownership:
                RequireDelivery(transportEvent, NetworkDelivery.ReliableOrdered, 0);
                ValidateReadyPacket(peer, packet.Header);
                if (IsStaleScenePacket(packet.Header))
                    return;
                HandleOwnership(packet);
                break;
            case NetworkProtocolMessage.UserMessage:
                ValidateReadyPacket(peer, packet.Header);
                RequireGameplayDelivery(transportEvent, inboundToServer: IsServer);
                if (IsStaleScenePacket(packet.Header))
                    return;
                if (IsServer)
                    ValidatePeerKnownProjection(peer, packet.Header);
                else
                    ValidateCurrentScenePacket(packet.Header, requireNextRevision: false);
                DispatchUserMessage(
                    packet.Payload.Span,
                    inboundFromClient: IsServer,
                    IsServer ? peer.PeerId : NetworkPeerId.None,
                    packet.Header.SceneEpoch,
                    packet.Header.ServerTick);
                break;
            case NetworkProtocolMessage.Snapshot:
                ValidateReadyPacket(peer, packet.Header);
                RequireSnapshotDelivery(transportEvent);
                HandleSnapshot(packet);
                break;
            case NetworkProtocolMessage.ClientTransform:
                ValidateReadyPacket(peer, packet.Header);
                RequireClientTransformDelivery(transportEvent);
                HandleClientTransform(peer, packet);
                break;
            default:
                ValidateReadyPacket(peer, packet.Header);
                throw new NetworkProtocolException(
                    $"Protocol message {packet.Header.Message} is not valid in the current connection state.");
        }

        if (!IsServer &&
            packet.Header.SessionId == SessionId &&
            packet.Header.ServerTick > ServerTick)
            ServerTick = packet.Header.ServerTick;
    }

    private bool TryDeferClientStructuralPacket(
        QueuedTransportEvent transportEvent,
        NetworkPacket packet)
    {
        if (IsServer || IsHost ||
            !IsClientDeferredMessage(packet.Header.Message, transportEvent.Delivery))
            return false;

        var shouldDefer = false;
        if (packet.Header.Message == NetworkProtocolMessage.ScopeBaseline)
        {
            if (packet.Header.StructuralRevision.Value > StructuralRevision.Value + 1)
                shouldDefer = true;
            else if (packet.Header.StructuralRevision.Value == StructuralRevision.Value + 1)
            {
                var scopeId = ReadScopeId(packet.Payload.Span);
                if (_clientScopeLoads.TryGetValue(scopeId, out var operation) &&
                    operation.Baseline is not null)
                    shouldDefer = true;
            }
        }
        else
        {
            var blockingRevision = _clientScopeLoads.Values
                .Where(operation => !operation.IsTerminal && operation.Baseline is not null)
                .Select(operation => operation.Baseline!.StructuralRevision.Value)
                .DefaultIfEmpty(ulong.MaxValue)
                .Min();
            shouldDefer = packet.Header.StructuralRevision.Value > blockingRevision;
        }

        if (!shouldDefer)
            return false;
        if (_deferredClientStructuralEvents.Count >= _options.MaxDeferredClientStructuralPackets ||
            transportEvent.Payload.Length >
            _options.MaxDeferredClientStructuralBytes - _deferredClientStructuralBytes)
            throw new NetworkProtocolException(
                "Deferred client structural packet limits were exceeded while applying a scope baseline.");

        _deferredClientStructuralEvents.Enqueue(transportEvent);
        _deferredClientStructuralBytes += transportEvent.Payload.Length;
        if (packet.Header.Message == NetworkProtocolMessage.ScopeUnload)
        {
            var scopeId = ReadScopeId(packet.Payload.Span);
            if (_clientScopeLoads.TryGetValue(scopeId, out var operation) && !operation.IsTerminal)
            {
                operation.CancelRequested = true;
                operation.ConsumeBaselineRevisionOnCancel = operation.Baseline is not null;
                operation.Diagnostic = "The server unloaded the scope before hydration completed.";
            }
        }
        return true;
    }

    private void ReplayDeferredClientStructuralPackets(long frameStarted)
    {
        var replayed = 0;
        while (_deferredClientStructuralEvents.TryPeek(out var transportEvent) &&
               replayed < _options.MaxDeferredClientStructuralPacketsPerFrame &&
               Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds <
               _options.ClientScopeLoadBudgetMilliseconds)
        {
            if (!NetworkProtocol.TryDecode(
                    transportEvent.Payload,
                    _options.MaxProtocolPayload,
                    out var packet,
                    out var error))
            {
                _deferredClientStructuralEvents.Dequeue();
                _deferredClientStructuralBytes -= transportEvent.Payload.Length;
                RejectProtocolError(
                    transportEvent.Connection,
                    error ?? "Malformed deferred network packet.");
                QuarantineConnectionWork(
                    transportEvent.Connection,
                    error ?? "Malformed deferred network packet.");
                break;
            }
            if (WouldDeferClientStructuralPacket(packet, transportEvent.Delivery))
                break;

            _deferredClientStructuralEvents.Dequeue();
            _deferredClientStructuralBytes -= transportEvent.Payload.Length;
            try
            {
                HandleData(transportEvent, false);
            }
            catch (NetworkProtocolException exception)
            {
                RejectProtocolError(transportEvent.Connection, exception.Message);
                QuarantineConnectionWork(transportEvent.Connection, exception.Message);
                break;
            }
            replayed++;
        }
    }

    private bool WouldDeferClientStructuralPacket(
        NetworkPacket packet,
        NetworkDelivery delivery)
    {
        if (!IsClientDeferredMessage(packet.Header.Message, delivery))
            return false;
        if (packet.Header.Message == NetworkProtocolMessage.ScopeBaseline)
        {
            if (packet.Header.StructuralRevision.Value > StructuralRevision.Value + 1)
                return true;
            if (packet.Header.StructuralRevision.Value < StructuralRevision.Value + 1)
                return false;
            var scopeId = ReadScopeId(packet.Payload.Span);
            return _clientScopeLoads.TryGetValue(scopeId, out var operation) &&
                   operation.Baseline is not null;
        }
        var blockingRevision = _clientScopeLoads.Values
            .Where(operation => !operation.IsTerminal && operation.Baseline is not null)
            .Select(operation => operation.Baseline!.StructuralRevision.Value)
            .DefaultIfEmpty(ulong.MaxValue)
            .Min();
        return packet.Header.StructuralRevision.Value > blockingRevision;
    }

    private bool TryConsumeRetiredScopeStructuralPacket(NetworkPacket packet)
    {
        if (IsServer || IsHost || packet.Payload.IsEmpty)
            return false;
        if (packet.Header.Message is not (NetworkProtocolMessage.Spawn or
            NetworkProtocolMessage.SpawnReady or NetworkProtocolMessage.Despawn or
            NetworkProtocolMessage.PlayerEntity or NetworkProtocolMessage.Ownership))
            return false;
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        if (!_retiredScopes.Contains(scopeId))
            return false;
        if (packet.Header.ServerTick > ServerTick)
            ServerTick = packet.Header.ServerTick;

        switch (packet.Header.Message)
        {
            case NetworkProtocolMessage.Spawn:
                ValidateCurrentScenePacket(packet.Header, true);
                reader.ReadUInt64();
                reader.ReadGuid();
                reader.ReadString(1024);
                reader.ReadUInt32();
                reader.ReadBoolean();
                reader.ReadBoolean();
                var flags = reader.ReadByte();
                if ((flags & ~14) != 0)
                    throw new NetworkProtocolException($"Spawn options contain unknown flags {flags}.");
                if ((flags & 2) != 0) ReadVector3(ref reader);
                if ((flags & 4) != 0) ReadVector3(ref reader);
                if ((flags & 8) != 0) ReadVector3(ref reader);
                reader.EnsureComplete();
                StructuralRevision = packet.Header.StructuralRevision;
                return true;
            case NetworkProtocolMessage.SpawnReady:
                if (packet.Header.StructuralRevision != StructuralRevision)
                    throw new NetworkProtocolException("Retired-scope SpawnReady has an invalid revision.");
                reader.ReadUInt64();
                reader.EnsureComplete();
                return true;
            case NetworkProtocolMessage.Despawn:
                ValidateCurrentScenePacket(packet.Header, true);
                reader.ReadUInt64();
                reader.EnsureComplete();
                StructuralRevision = packet.Header.StructuralRevision;
                return true;
            case NetworkProtocolMessage.PlayerEntity:
                ValidateCurrentScenePacket(packet.Header, true);
                reader.ReadUInt32();
                reader.ReadUInt64();
                reader.EnsureComplete();
                StructuralRevision = packet.Header.StructuralRevision;
                return true;
            case NetworkProtocolMessage.Ownership:
                ValidateCurrentScenePacket(packet.Header, true);
                reader.ReadUInt64();
                reader.ReadUInt32();
                reader.EnsureComplete();
                StructuralRevision = packet.Header.StructuralRevision;
                return true;
            default:
                return false;
        }
    }

    private static bool IsClientStructuralMessage(NetworkProtocolMessage message) =>
        message is NetworkProtocolMessage.ScopeBaseline or NetworkProtocolMessage.Spawn or
            NetworkProtocolMessage.SpawnReady or NetworkProtocolMessage.Despawn or
            NetworkProtocolMessage.PlayerEntity or NetworkProtocolMessage.Ownership or
            NetworkProtocolMessage.ScopeUnload;

    private static bool IsClientDeferredMessage(
        NetworkProtocolMessage message,
        NetworkDelivery delivery) =>
        IsClientStructuralMessage(message) ||
        message == NetworkProtocolMessage.Snapshot;

    private static NetworkReplicationScopeId ReadScopeId(ReadOnlySpan<byte> payload)
    {
        var reader = new NetworkReader(payload);
        return new NetworkReplicationScopeId(reader.ReadUInt32());
    }

    private NetworkStructuralRevision GetClientProjectedStructuralRevision()
    {
        var revision = StructuralRevision.Value;
        foreach (var operation in _clientScopeLoads.Values)
            if (!operation.IsTerminal && operation.Baseline is { } baseline &&
                baseline.StructuralRevision.Value > revision)
                revision = baseline.StructuralRevision.Value;
        return new NetworkStructuralRevision(revision);
    }

    private void HandleHello(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.AwaitingHello)
            throw new NetworkProtocolException("Unexpected Hello message.");
        if (packet.Header.SessionId != Guid.Empty)
            throw new NetworkProtocolException("Hello message must not claim a session ID.");

        var reader = new NetworkReader(packet.Payload.Span);
        var protocolVersion = reader.ReadUInt16();
        var buildId = reader.ReadString(256) ?? string.Empty;
        var contentFingerprint = reader.ReadString(256);
        var messageSchema = reader.ReadString(64) ?? string.Empty;
        var replicationSchema = reader.ReadString(64) ?? string.Empty;
        reader.EnsureComplete();

        string? mismatch = null;
        if (protocolVersion != NetworkProtocol.Version)
            mismatch = $"Protocol version mismatch: expected {NetworkProtocol.Version}, received {protocolVersion}.";
        else if (!string.Equals(buildId, _options.GameBuildId, StringComparison.Ordinal))
            mismatch = $"Game build mismatch: expected '{_options.GameBuildId}', received '{buildId}'.";
        else if (!string.Equals(contentFingerprint, _options.ContentFingerprint, StringComparison.Ordinal))
            mismatch = "Baked-content fingerprint mismatch.";
        else if (!string.Equals(messageSchema, _messages.SchemaHash.Hex, StringComparison.Ordinal))
            mismatch = "Networking message schema mismatch.";
        else if (!string.Equals(replicationSchema, _replication.SchemaHash.Hex, StringComparison.Ordinal))
            mismatch = "Component replication schema mismatch.";

        if (mismatch is not null)
        {
            SendReject(peer.Connection, mismatch);
            peer.Phase = NetworkConnectionPhase.Rejected;
            Transport.Disconnect(peer.Connection, TransportDisconnectReason.Incompatible);
            return;
        }

        peer.PeerId = AllocatePeerId();
        peer.Phase = NetworkConnectionPhase.Ready;
        _peersById.Add(peer.PeerId, peer);
        SendPacket(
            peer.Connection,
            NetworkProtocolMessage.Welcome,
            writer => writer.WriteUInt32(peer.PeerId.Value));
        var synchronizationKey = _pendingSceneKey ?? CurrentSceneKey;
        var synchronizationEpoch = _pendingSceneEpoch.IsValid
            ? _pendingSceneEpoch
            : SceneEpoch;
        if (synchronizationKey is not null &&
            (_pendingSceneEpoch.IsValid || World is not null))
        {
            peer.Phase = NetworkConnectionPhase.AwaitingSceneLoad;
            SendPacket(
                peer.Connection,
                NetworkProtocolMessage.SceneChange,
                writer => writer.WriteString(synchronizationKey, 256),
                synchronizationEpoch,
                ServerTick,
                NetworkStructuralRevision.None);
        }
        PeerConnected?.Invoke(peer.PeerId);
    }

    private void HandleWelcome(NetworkPeer peer, NetworkPacket packet)
    {
        if (Role != NetworkRole.Client || peer.Phase != NetworkConnectionPhase.AwaitingWelcome)
            throw new NetworkProtocolException("Unexpected Welcome message.");
        if (packet.Header.SessionId == Guid.Empty)
            throw new NetworkProtocolException("Welcome message has an empty session ID.");

        var reader = new NetworkReader(packet.Payload.Span);
        var peerId = new NetworkPeerId(reader.ReadUInt32());
        reader.EnsureComplete();
        if (!peerId.IsValid)
            throw new NetworkProtocolException("Server assigned peer ID zero.");

        SessionId = packet.Header.SessionId;
        LocalPeerId = peerId;
        peer.PeerId = peerId;
        peer.Phase = NetworkConnectionPhase.Ready;
        _peersById.Add(peerId, peer);
        PeerConnected?.Invoke(peerId);
    }

    private void HandleReject(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer || peer.Phase == NetworkConnectionPhase.Rejected)
            throw new NetworkProtocolException("Unexpected Reject message.");

        var reader = new NetworkReader(packet.Payload.Span);
        var diagnostic = reader.ReadString(1024) ?? "Connection rejected.";
        reader.EnsureComplete();
        peer.RemoteDiagnostic = diagnostic;
        var handshakeFailure = !peer.PeerId.IsValid;
        peer.Phase = NetworkConnectionPhase.Rejected;
        if (handshakeFailure)
            ConnectionFailed?.Invoke(TransportDisconnectReason.Incompatible, diagnostic);
        Transport.Disconnect(
            peer.Connection,
            handshakeFailure
                ? TransportDisconnectReason.Incompatible
                : TransportDisconnectReason.ProtocolError);
    }

    private void HandleSceneChange(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive SceneChange.");
        var reader = new NetworkReader(packet.Payload.Span);
        var sceneKey = reader.ReadString(256);
        reader.EnsureComplete();
        if (string.IsNullOrWhiteSpace(sceneKey) || !packet.Header.SceneEpoch.IsValid)
            throw new NetworkProtocolException("SceneChange contains an empty Scene key or epoch.");
        if (packet.Header.SceneEpoch.Value < SceneEpoch.Value)
            return;
        if (packet.Header.SceneEpoch == SceneEpoch &&
            string.Equals(CurrentSceneKey, sceneKey, StringComparison.Ordinal) &&
            World is not null)
            return;
        if (_pendingSceneEpoch.IsValid)
            throw new NetworkProtocolException(
                $"Received SceneChange for epoch {packet.Header.SceneEpoch} while epoch " +
                $"{_pendingSceneEpoch} is still loading.");

        _pendingSceneKey = sceneKey;
        _pendingSceneEpoch = packet.Header.SceneEpoch;
        _clientSceneReady = false;
        _clientSceneLoadedSent = false;
        _clientBaseline = null;
        peer.Phase = NetworkConnectionPhase.AwaitingSceneLoad;
        try
        {
            if (SceneChangeRequested is null)
                throw new InvalidOperationException(
                    "No Scene catalog handler is attached to the client NetworkSession.");
            SceneChangeRequested.Invoke(sceneKey, packet.Header.SceneEpoch);
        }
        catch (Exception exception) when (exception is not NetworkProtocolException)
        {
            _pendingSceneKey = null;
            _pendingSceneEpoch = NetworkSceneEpoch.None;
            throw new NetworkProtocolException(
                $"Could not construct synchronized Scene '{sceneKey}': {exception.Message}");
        }
    }

    private void HandleSceneLoaded(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.AwaitingSceneLoad)
            throw new NetworkProtocolException("Unexpected SceneLoaded message.");
        if (packet.Header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} loaded Scene epoch {packet.Header.SceneEpoch}; server is at {SceneEpoch}.");
        var reader = new NetworkReader(packet.Payload.Span);
        var sceneKey = reader.ReadString(256);
        reader.EnsureComplete();
        if (!string.Equals(sceneKey, CurrentSceneKey, StringComparison.Ordinal))
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} loaded Scene '{sceneKey}', expected '{CurrentSceneKey}'.");
        if (World is null)
            throw new NetworkProtocolException("The server has no NetworkWorld for the loaded Scene.");
        if (!World.AuthoredEntitiesBound)
            BindServerAuthoredEntities();

        try
        {
            SendBaseline(peer);
            peer.Phase = NetworkConnectionPhase.Synchronizing;
        }
        catch (NetworkSynchronizationException exception)
        {
            peer.RemoteDiagnostic = exception.Message;
            peer.Phase = NetworkConnectionPhase.Rejected;
            SendReject(peer.Connection, exception.Message);
            Transport.Disconnect(peer.Connection, TransportDisconnectReason.Incompatible);
        }
    }

    private void SendBaseline(NetworkPeer peer)
    {
        if (World is null)
            throw new InvalidOperationException("Cannot capture a baseline without a NetworkWorld.");
        World.ReconcileDestroyedEntities();
        // The baseline establishes this peer's projection, not the server world's revision.
        var revision = peer.ProjectedStructuralRevision;
        var tick = ServerTick;
        var stateSequence = _stateSequence;
        var authored = World.GetAuthoredBindings(NetworkReplicationScopeId.Global).ToArray();
        var globalRecords = World.GetRecords(NetworkReplicationScopeId.Global);
        var dynamic = globalRecords
            .Where(record => record.Origin == NetworkSpawnOrigin.DynamicBlueprint)
            .ToArray();
        var players = World.PlayerEntities
            .Where(pair => World.TryGetRecord(pair.Value, out var record) &&
                           record!.Scope.IsGlobal)
            .ToArray();
        var componentCount = globalRecords.Sum(record => record.ReplicationBindings.Count);
        if (authored.Length + dynamic.Length > _options.MaxNetworkEntities ||
            players.Length > _options.MaxNetworkEntities ||
            componentCount > _options.MaxBaselineComponentRecords)
            throw new NetworkSynchronizationException(
                $"Peer {peer.PeerId} cannot be synchronized because the current NetworkWorld baseline " +
                $"contains {authored.Length + dynamic.Length} Entities, {players.Length} player mappings, " +
                $"and {componentCount} Component states; configured limits are " +
                $"{_options.MaxNetworkEntities}, {_options.MaxNetworkEntities}, and " +
                $"{_options.MaxBaselineComponentRecords} respectively.");

        var packets = new List<byte[]>(
            checked(2 + authored.Length + dynamic.Length + players.Length + componentCount));
        AddBaselinePacket(
            NetworkBaselineRecordKind.Begin,
            writer =>
            {
                writer.WriteInt32(authored.Length);
                writer.WriteInt32(dynamic.Length);
                writer.WriteInt32(players.Length);
                writer.WriteInt32(componentCount);
                writer.WriteUInt32(stateSequence);
            },
            "baseline Begin");

        foreach (var binding in authored)
            AddBaselinePacket(
                NetworkBaselineRecordKind.AuthoredEntity,
                writer =>
                {
                    writer.WriteGuid(binding.SourceGuid);
                    writer.WriteUInt64(binding.NetworkEntityId.Value);
                    writer.WriteUInt32(binding.Owner.Value);
                    writer.WriteBoolean(binding.IsPresent);
                },
                $"authored Entity {binding.NetworkEntityId}");

        foreach (var record in dynamic)
            AddBaselinePacket(
                NetworkBaselineRecordKind.DynamicEntity,
                writer => WriteDynamicBaselineRecord(writer, record),
                $"dynamic Entity {record.Id}");

        foreach (var player in players)
        {
            AddBaselinePacket(
                NetworkBaselineRecordKind.PlayerEntity,
                writer =>
                {
                    writer.WriteUInt32(player.Key.Value);
                    writer.WriteUInt64(player.Value.Value);
                },
                $"player mapping {player.Key}");
        }

        foreach (var record in globalRecords)
            foreach (var binding in record.ReplicationBindings)
            {
                var payload = binding.Capture();
                AddBaselinePacket(
                    NetworkBaselineRecordKind.ComponentState,
                    writer =>
                    {
                        writer.WriteUInt64(record.Id.Value);
                        writer.WriteUInt16(binding.Descriptor.Id);
                        writer.WriteLengthPrefixedBytes(payload, binding.Descriptor.MaximumPayload);
                    },
                    $"Component {binding.Descriptor.Id} state on Entity {record.Id}");
            }

        AddBaselinePacket(NetworkBaselineRecordKind.End, null, "baseline End");
        foreach (var player in players)
            peer.ProjectedPlayerEntities[player.Key] = player.Value;
        foreach (var packet in packets)
            Transport.Send(peer.Connection, packet, NetworkDelivery.ReliableOrdered, 0);

        void AddBaselinePacket(
            NetworkBaselineRecordKind kind,
            Action<NetworkWriter>? writePayload,
            string label)
        {
            var packet = EncodePacket(
                NetworkProtocolMessage.Baseline,
                writer =>
                {
                    writer.WriteByte((byte)kind);
                    writePayload?.Invoke(writer);
                },
                SceneEpoch,
                tick,
                revision);
            if (packet.Length > Transport.Capabilities.MaxReliablePayload)
            {
                throw new NetworkSynchronizationException(
                    $"Peer {peer.PeerId} cannot be synchronized because {label} requires a " +
                    $"{packet.Length}-byte reliable packet, exceeding the active transport limit " +
                    $"of {Transport.Capabilities.MaxReliablePayload} bytes.");
            }
            packets.Add(packet);
        }
    }

    private static void WriteDynamicBaselineRecord(
        NetworkWriter writer,
        NetworkEntityRecord record)
    {
        writer.WriteUInt64(record.Id.Value);
        writer.WriteGuid(record.BlueprintAssetId.Value);
        writer.WriteString(record.BlueprintAssetName, 1024);
        writer.WriteUInt32(record.Owner.Value);
        writer.WriteBoolean(record.DestroyWithOwner);
        writer.WriteBoolean(record.Entity.LocallyEnabled);
        WriteVector3(writer, record.Entity.Transform.Position);
        var rotation = record.Entity.Transform.Rotation;
        writer.WriteSingle(rotation.X);
        writer.WriteSingle(rotation.Y);
        writer.WriteSingle(rotation.Z);
        writer.WriteSingle(rotation.W);
        WriteVector3(writer, record.Entity.Transform.Scale);
    }

    private void HandleBaseline(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive a world baseline.");
        if (packet.Header.SceneEpoch != SceneEpoch || World is null)
            throw new NetworkProtocolException(
                $"Baseline epoch {packet.Header.SceneEpoch} does not match the active client Scene {SceneEpoch}.");
        peer.Phase = NetworkConnectionPhase.Synchronizing;
        var reader = new NetworkReader(packet.Payload.Span);
        var kindValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(NetworkBaselineRecordKind), kindValue))
            throw new NetworkProtocolException($"Unknown baseline record kind {kindValue}.");
        var kind = (NetworkBaselineRecordKind)kindValue;

        if (kind == NetworkBaselineRecordKind.Begin)
        {
            if (_clientBaseline is not null)
                throw new NetworkProtocolException("Received a second baseline Begin record.");
            var authored = ReadBoundedCount(ref reader, _options.MaxNetworkEntities, "authored entities");
            var dynamic = ReadBoundedCount(ref reader, _options.MaxNetworkEntities, "dynamic entities");
            if (authored > _options.MaxNetworkEntities - dynamic)
                throw new NetworkProtocolException("Baseline entity count exceeds the configured limit.");
            var players = ReadBoundedCount(ref reader, _options.MaxNetworkEntities, "player mappings");
            var components = ReadBoundedCount(
                ref reader,
                _options.MaxBaselineComponentRecords,
                "Component states");
            var stateSequence = reader.ReadUInt32();
            reader.EnsureComplete();
            _clientBaseline = new ClientBaselineState
            {
                Scope = NetworkReplicationScopeId.Global,
                SceneEpoch = packet.Header.SceneEpoch,
                StructuralRevision = packet.Header.StructuralRevision,
                ServerTick = packet.Header.ServerTick,
                StateSequence = stateSequence,
                ExpectedAuthored = authored,
                ExpectedDynamic = dynamic,
                ExpectedPlayers = players,
                ExpectedComponents = components
            };
            return;
        }

        var baseline = _clientBaseline ??
                       throw new NetworkProtocolException("Baseline record arrived before Begin.");
        if (baseline.SceneEpoch != packet.Header.SceneEpoch ||
            baseline.StructuralRevision != packet.Header.StructuralRevision)
            throw new NetworkProtocolException("Baseline record header changed during synchronization.");

        switch (kind)
        {
            case NetworkBaselineRecordKind.AuthoredEntity:
                EnsureRecordCapacity(baseline.Authored.Count, baseline.ExpectedAuthored, "authored Entity");
                baseline.Authored.Add(new NetworkAuthoredBinding(
                    NetworkReplicationScopeId.Global,
                    reader.ReadGuid(),
                    new NetworkEntityId(reader.ReadUInt64()),
                    new NetworkPeerId(reader.ReadUInt32()))
                {
                    IsPresent = reader.ReadBoolean()
                });
                break;
            case NetworkBaselineRecordKind.DynamicEntity:
                EnsureRecordCapacity(baseline.Dynamic.Count, baseline.ExpectedDynamic, "dynamic Entity");
                baseline.Dynamic.Add(ReadDynamicBaselineRecord(
                    ref reader,
                    NetworkReplicationScopeId.Global));
                break;
            case NetworkBaselineRecordKind.PlayerEntity:
                EnsureRecordCapacity(baseline.Players.Count, baseline.ExpectedPlayers, "player mapping");
                baseline.Players.Add(new KeyValuePair<NetworkPeerId, NetworkEntityId>(
                    new NetworkPeerId(reader.ReadUInt32()),
                    new NetworkEntityId(reader.ReadUInt64())));
                break;
            case NetworkBaselineRecordKind.ComponentState:
                EnsureRecordCapacity(
                    baseline.Components.Count,
                    baseline.ExpectedComponents,
                    "Component state");
                var entityId = new NetworkEntityId(reader.ReadUInt64());
                var componentId = reader.ReadUInt16();
                var descriptor = _replication.GetById(componentId);
                baseline.Components.Add(new NetworkComponentStateRecord(
                    entityId,
                    componentId,
                    reader.ReadLengthPrefixedBytes(descriptor.MaximumPayload).ToArray()));
                break;
            case NetworkBaselineRecordKind.End:
                reader.EnsureComplete();
                if (_clientScopeLoads.ContainsKey(NetworkReplicationScopeId.Global))
                    throw new NetworkProtocolException("The initial world baseline is already being applied.");
                var globalOperation = new ClientNetworkScopeLoadOperation
                {
                    SessionId = SessionId,
                    World = World,
                    SceneEpoch = baseline.SceneEpoch,
                    Scope = NetworkReplicationScopeId.Global,
                    SourceAssetId = AssetId.Empty,
                    SourceAssetName = CurrentSceneKey,
                    ManifestRevision = StructuralRevision,
                    CreatedFrame = _clientScopeLoadFrame,
                    EarliestAdvanceFrame = _clientScopeLoadFrame + 1,
                    Phase = NetworkScopeLoadPhase.ValidatingBaseline,
                    Baseline = baseline
                };
                RegisterClientScopeLoad(globalOperation);
                return;
            default:
                throw new NetworkProtocolException($"Unexpected baseline record kind {kind}.");
        }
        reader.EnsureComplete();
    }

    private void MaterializeDynamicSpawn(NetworkDynamicSpawnRecord spawn)
    {
        if (World is null || !spawn.EntityId.IsValid || spawn.BlueprintAssetId.IsEmpty)
            throw new InvalidOperationException("Dynamic baseline spawn contains an invalid identity.");
        var blueprint = Resources.LoadDreambitAsset(
                            spawn.BlueprintAssetId,
                            spawn.BlueprintAssetName,
                            typeof(EntityBlueprint)) as EntityBlueprint
                        ?? throw new InvalidOperationException(
                            $"Blueprint '{spawn.BlueprintAssetName}' ({spawn.BlueprintAssetId}) could not be loaded.");
        Entity? entity = null;
        var registered = false;
        try
        {
            entity = spawn.Scope.IsGlobal
                ? World.Scene.CreateNetworkEntity(
                    blueprint, spawn.Enabled, spawn.Position, null, spawn.Scale)
                : World.Scene.CreateNetworkContentEntity(
                    GetClientScope(spawn.Scope).Content!, blueprint, _scopeCoordinator,
                    spawn.Enabled, spawn.Position, null, spawn.Scale);
            entity.Transform.Rotation = spawn.Rotation;
            SuspendScopeEntityHierarchy(spawn.Scope, entity);
            World.RegisterDynamicEntity(
                entity,
                spawn.EntityId,
                spawn.Owner,
                spawn.BlueprintAssetId,
                spawn.BlueprintAssetName,
                spawn.DestroyWithOwner,
                spawn.Scope);
            registered = true;
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<Exception>();
            if (registered)
                try
                {
                    World.Unregister(spawn.EntityId, false);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (entity is not null)
                try
                {
                    World.Scene.DestroyEntityHierarchyImmediately(entity);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (cleanupErrors.Count != 0)
            {
                cleanupErrors.Insert(0, exception);
                throw new AggregateException(
                    "Dynamic baseline materialization failed and rollback also reported errors.",
                    cleanupErrors);
            }
            throw;
        }
    }

    private static NetworkDynamicSpawnRecord ReadDynamicBaselineRecord(
        ref NetworkReader reader,
        NetworkReplicationScopeId scope)
    {
        var entityId = new NetworkEntityId(reader.ReadUInt64());
        var assetId = new AssetId(reader.ReadGuid());
        var assetName = reader.ReadString(1024);
        var owner = new NetworkPeerId(reader.ReadUInt32());
        var destroyWithOwner = reader.ReadBoolean();
        var enabled = reader.ReadBoolean();
        var position = ReadVector3(ref reader);
        var rotation = new Quaternion(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        var scale = ReadVector3(ref reader);
        return new NetworkDynamicSpawnRecord(
            scope,
            entityId,
            assetId,
            assetName,
            owner,
            destroyWithOwner,
            enabled,
            position,
            rotation,
            scale);
    }

    private void HandleReady(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.Synchronizing)
            throw new NetworkProtocolException("Unexpected Ready message.");
        if (packet.Header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} became ready for stale Scene epoch {packet.Header.SceneEpoch}.");
        var reader = new NetworkReader(packet.Payload.Span);
        reader.EnsureComplete();
        peer.Phase = NetworkConnectionPhase.Ready;
        foreach (var subscription in peer.ScopeSubscriptions.Values)
        {
            if (subscription.Phase != NetworkScopeSubscriptionPhase.Pending)
                continue;
            if (!_scopes.TryGetValue(subscription.Scope, out var scope))
                continue;
            subscription.Phase = NetworkScopeSubscriptionPhase.AwaitingLoaded;
            SendScopeLoad(peer, scope, subscription);
        }
    }

    private void SendScopeLoad(
        NetworkPeer peer,
        NetworkReplicationScope scope,
        NetworkScopeSubscription subscription)
    {
        subscription.ManifestRevision = peer.ProjectedStructuralRevision;
        SendPacket(peer.Connection, NetworkProtocolMessage.ScopeLoad,
            writer =>
            {
                writer.WriteUInt32(scope.Id.Value);
                writer.WriteGuid(scope.SourceAssetId.Value);
                writer.WriteString(scope.SourceAssetName, _options.MaxScopeAssetNameBytes);
            }, SceneEpoch, ServerTick, peer.ProjectedStructuralRevision);
    }

    private void HandleScopeLoad(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer || peer.Phase != NetworkConnectionPhase.Ready || World is null)
            throw new NetworkProtocolException("Unexpected ScopeLoad message.");
        ValidateCurrentScenePacket(packet.Header, false);
        if (packet.Header.StructuralRevision != GetClientProjectedStructuralRevision())
            throw new NetworkProtocolException("ScopeLoad was not sent at the current projected revision.");

        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var assetId = new AssetId(reader.ReadGuid());
        var assetName = reader.ReadString(_options.MaxScopeAssetNameBytes);
        reader.EnsureComplete();
        if (!scopeId.IsValid || scopeId.IsGlobal || assetId.IsEmpty)
            throw new NetworkProtocolException("ScopeLoad contains an invalid scope or source identity.");
        if (_scopes.ContainsKey(scopeId) || _clientScopeLoads.ContainsKey(scopeId))
            throw new NetworkProtocolException($"Active scope identity {scopeId} was duplicated.");
        var sourceIdentity = new ScopeSourceIdentity(assetId, assetName);
        if (_knownScopeSources.TryGetValue(scopeId, out var knownSource) && knownSource != sourceIdentity)
            throw new NetworkProtocolException(
                $"Scope identity {scopeId} was reused for a different source in the same Scene epoch.");
        _knownScopeSources.TryAdd(scopeId, sourceIdentity);
        _retiredScopes.Remove(scopeId);
        if (_scopes.Count >= _options.MaxReplicationScopes)
            throw new NetworkProtocolException("ScopeLoad exceeds the configured scope limit.");

        var operation = new ClientNetworkScopeLoadOperation
        {
            SessionId = SessionId,
            World = World,
            SceneEpoch = SceneEpoch,
            Scope = scopeId,
            SourceAssetId = assetId,
            SourceAssetName = assetName,
            ManifestRevision = packet.Header.StructuralRevision,
            CreatedFrame = _clientScopeLoadFrame,
            EarliestAdvanceFrame = _clientScopeLoadFrame + 1,
            Phase = NetworkScopeLoadPhase.LoadingContent,
            TotalItems = null
        };
        RegisterClientScopeLoad(operation);
    }

    private void HandleScopeLoaded(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.Ready)
            throw new NetworkProtocolException("Unexpected ScopeLoaded message.");
        if (packet.Header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException("ScopeLoaded uses a stale Scene epoch.");
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        reader.EnsureComplete();
        if (!peer.ScopeSubscriptions.TryGetValue(scopeId, out var subscription) ||
            subscription.Phase is not (NetworkScopeSubscriptionPhase.AwaitingLoaded or
                NetworkScopeSubscriptionPhase.Unloading))
            throw new NetworkProtocolException($"Peer {peer.PeerId} acknowledged unexpected scope {scopeId}.");
        if (packet.Header.StructuralRevision != subscription.ManifestRevision)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} acknowledged scope {scopeId} at revision " +
                $"{packet.Header.StructuralRevision}; expected manifest revision " +
                $"{subscription.ManifestRevision}.");
        if (subscription.Phase == NetworkScopeSubscriptionPhase.Unloading)
            return;
        if (!_scopes.TryGetValue(scopeId, out var scope) || !scope.IsLoaded)
            throw new NetworkProtocolException($"Peer {peer.PeerId} acknowledged unavailable scope {scopeId}.");
        try
        {
            SendScopeBaseline(peer, scope, subscription);
        }
        catch (Exception exception)
        {
            var diagnostic = exception is NetworkSynchronizationException
                ? exception.Message
                : $"Scope {scopeId} synchronization failed: {exception.Message}";
            peer.RemoteDiagnostic = diagnostic;
            peer.Phase = NetworkConnectionPhase.Rejected;
            try
            {
                SendReject(peer.Connection, diagnostic);
            }
            finally
            {
                Transport.Disconnect(peer.Connection, TransportDisconnectReason.Incompatible);
            }
        }
    }

    private void SendScopeBaseline(
        NetworkPeer peer,
        NetworkReplicationScope scope,
        NetworkScopeSubscription subscription)
    {
        if (World is null)
            throw new InvalidOperationException("Cannot capture a scope baseline without a NetworkWorld.");
        World.ReconcileDestroyedEntities();
        var records = World.GetRecords(scope.Id);
        var authored = World.GetAuthoredBindings(scope.Id);
        var dynamic = records.Where(record => record.Origin == NetworkSpawnOrigin.DynamicBlueprint).ToArray();
        var players = World.PlayerEntities
            .Where(pair => World.TryGetRecord(pair.Value, out var record) && record!.Scope == scope.Id)
            .ToArray();
        var componentCount = records.Sum(record => record.ReplicationBindings.Count);
        var stateSequence = _stateSequence;
        if (authored.Count > _options.MaxScopedAuthoredEntities ||
            dynamic.Length > _options.MaxNetworkEntities ||
            authored.Count > _options.MaxNetworkEntities - dynamic.Length ||
            players.Length > _options.MaxNetworkEntities ||
            componentCount > _options.MaxScopeBaselineComponentRecords)
            throw new NetworkSynchronizationException(
                $"Scope {scope.Id} exceeds configured synchronization limits.");

        var revision = NextStructuralRevision(peer.ProjectedStructuralRevision);
        var packets = new List<byte[]>(checked(2 + authored.Count + dynamic.Length + players.Length + componentCount));

        Add(NetworkBaselineRecordKind.Begin, writer =>
        {
            writer.WriteInt32(authored.Count);
            writer.WriteInt32(dynamic.Length);
            writer.WriteInt32(players.Length);
            writer.WriteInt32(componentCount);
            writer.WriteUInt32(stateSequence);
        });
        foreach (var binding in authored)
            Add(NetworkBaselineRecordKind.AuthoredEntity, writer =>
            {
                writer.WriteGuid(binding.SourceGuid);
                writer.WriteUInt64(binding.NetworkEntityId.Value);
                writer.WriteUInt32(binding.Owner.Value);
                writer.WriteBoolean(binding.IsPresent);
            });
        foreach (var record in dynamic)
            Add(NetworkBaselineRecordKind.DynamicEntity, writer => WriteDynamicBaselineRecord(writer, record));
        foreach (var player in players)
        {
            Add(NetworkBaselineRecordKind.PlayerEntity, writer =>
            {
                writer.WriteUInt32(player.Key.Value);
                writer.WriteUInt64(player.Value.Value);
            });
        }
        foreach (var record in records)
            foreach (var binding in record.ReplicationBindings)
            {
                var payload = binding.Capture();
                Add(NetworkBaselineRecordKind.ComponentState, writer =>
                {
                    writer.WriteUInt64(record.Id.Value);
                    writer.WriteUInt16(binding.Descriptor.Id);
                    writer.WriteLengthPrefixedBytes(payload, binding.Descriptor.MaximumPayload);
                });
        }
        Add(NetworkBaselineRecordKind.End, null);

        // Commit projection state only after the complete baseline has been captured and encoded.
        // A serializer or payload-limit failure therefore leaves the peer at its previous revision.
        peer.ProjectedStructuralRevision = revision;
        subscription.BaselineRevision = revision;
        subscription.Phase = NetworkScopeSubscriptionPhase.AwaitingReady;
        foreach (var player in players)
            peer.ProjectedPlayerEntities[player.Key] = player.Value;
        foreach (var bytes in packets)
            Transport.Send(peer.Connection, bytes, NetworkDelivery.ReliableOrdered, 0);

        void Add(NetworkBaselineRecordKind kind, Action<NetworkWriter>? payload)
        {
            var bytes = EncodePacket(NetworkProtocolMessage.ScopeBaseline, writer =>
            {
                writer.WriteUInt32(scope.Id.Value);
                writer.WriteByte((byte)kind);
                payload?.Invoke(writer);
            }, SceneEpoch, ServerTick, revision);
            if (bytes.Length > Transport.Capabilities.MaxReliablePayload)
                throw new NetworkSynchronizationException(
                    $"Scope {scope.Id} baseline record exceeds the reliable transport payload limit.");
            packets.Add(bytes);
        }
    }

    private void HandleScopeBaseline(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer || peer.Phase != NetworkConnectionPhase.Ready || World is null)
            throw new NetworkProtocolException("Unexpected ScopeBaseline message.");
        ValidateCurrentScenePacket(packet.Header, false);
        if (packet.Header.StructuralRevision.Value != StructuralRevision.Value + 1)
            throw new NetworkProtocolException("Scope baseline is not the next projected structural revision.");
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var kindValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(NetworkBaselineRecordKind), kindValue))
            throw new NetworkProtocolException($"Unknown scoped baseline record kind {kindValue}.");
        if (!_scopes.TryGetValue(scopeId, out var targetScope) ||
            scopeId.IsGlobal || targetScope.IsReady)
            throw new NetworkProtocolException($"Scoped baseline references unknown scope {scopeId}.");
        var kind = (NetworkBaselineRecordKind)kindValue;

        if (kind == NetworkBaselineRecordKind.Begin)
        {
            if (_clientScopeBaselines.ContainsKey(scopeId))
                throw new NetworkProtocolException($"Scope {scopeId} received a second baseline Begin.");
            var authored = ReadBoundedCount(ref reader, _options.MaxScopedAuthoredEntities, "scoped authored entities");
            var dynamic = ReadBoundedCount(ref reader, _options.MaxNetworkEntities, "scoped dynamic entities");
            if (authored > _options.MaxNetworkEntities - dynamic)
                throw new NetworkProtocolException(
                    "Scoped baseline entity count exceeds the configured limit.");
            var players = ReadBoundedCount(ref reader, _options.MaxNetworkEntities, "scoped player mappings");
            var components = ReadBoundedCount(ref reader, _options.MaxScopeBaselineComponentRecords, "scoped Component states");
            var stateSequence = reader.ReadUInt32();
            reader.EnsureComplete();
            _clientScopeBaselines.Add(scopeId, new ClientBaselineState
            {
                Scope = scopeId,
                SceneEpoch = packet.Header.SceneEpoch,
                StructuralRevision = packet.Header.StructuralRevision,
                ServerTick = packet.Header.ServerTick,
                StateSequence = stateSequence,
                ExpectedAuthored = authored,
                ExpectedDynamic = dynamic,
                ExpectedPlayers = players,
                ExpectedComponents = components
            });
            return;
        }

        if (!_clientScopeBaselines.TryGetValue(scopeId, out var baseline) ||
            baseline.StructuralRevision != packet.Header.StructuralRevision)
            throw new NetworkProtocolException($"Scope {scopeId} baseline record arrived before Begin.");
        switch (kind)
        {
            case NetworkBaselineRecordKind.AuthoredEntity:
                EnsureRecordCapacity(baseline.Authored.Count, baseline.ExpectedAuthored, "scoped authored Entity");
                baseline.Authored.Add(new NetworkAuthoredBinding(scopeId, reader.ReadGuid(),
                    new NetworkEntityId(reader.ReadUInt64()), new NetworkPeerId(reader.ReadUInt32()))
                {
                    IsPresent = reader.ReadBoolean()
                });
                break;
            case NetworkBaselineRecordKind.DynamicEntity:
                EnsureRecordCapacity(baseline.Dynamic.Count, baseline.ExpectedDynamic, "scoped dynamic Entity");
                baseline.Dynamic.Add(ReadDynamicBaselineRecord(ref reader, scopeId));
                break;
            case NetworkBaselineRecordKind.PlayerEntity:
                EnsureRecordCapacity(baseline.Players.Count, baseline.ExpectedPlayers, "scoped player mapping");
                baseline.Players.Add(new KeyValuePair<NetworkPeerId, NetworkEntityId>(
                    new NetworkPeerId(reader.ReadUInt32()), new NetworkEntityId(reader.ReadUInt64())));
                break;
            case NetworkBaselineRecordKind.ComponentState:
                EnsureRecordCapacity(baseline.Components.Count, baseline.ExpectedComponents, "scoped Component state");
                var entityId = new NetworkEntityId(reader.ReadUInt64());
                var componentId = reader.ReadUInt16();
                var descriptor = _replication.GetById(componentId);
                baseline.Components.Add(new NetworkComponentStateRecord(entityId, componentId,
                    reader.ReadLengthPrefixedBytes(descriptor.MaximumPayload).ToArray()));
                break;
            case NetworkBaselineRecordKind.End:
                reader.EnsureComplete();
                if (!_clientScopeLoads.TryGetValue(scopeId, out var operation) ||
                    operation.Phase != NetworkScopeLoadPhase.WaitingForBaseline ||
                    operation.Baseline is not null)
                    throw new NetworkProtocolException(
                        $"Scope {scopeId} is not waiting for a baseline to apply.");
                operation.Baseline = baseline;
                SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.ValidatingBaseline);
                return;
            default:
                throw new NetworkProtocolException($"Unexpected scoped baseline record kind {kind}.");
        }
        reader.EnsureComplete();
    }

    private void HandleScopeReady(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.Ready)
            throw new NetworkProtocolException("Unexpected ScopeReady message.");
        if (packet.Header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException("ScopeReady uses a stale Scene epoch.");
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var baselineRevision = new NetworkStructuralRevision(reader.ReadUInt64());
        reader.EnsureComplete();
        if (!peer.ScopeSubscriptions.TryGetValue(scopeId, out var subscription) ||
            subscription.Phase is not (NetworkScopeSubscriptionPhase.AwaitingReady or
                NetworkScopeSubscriptionPhase.Unloading) ||
            baselineRevision != subscription.BaselineRevision ||
            packet.Header.StructuralRevision != baselineRevision)
            throw new NetworkProtocolException($"Peer {peer.PeerId} sent an invalid ScopeReady for {scopeId}.");
        if (subscription.Phase == NetworkScopeSubscriptionPhase.Unloading)
            return;
        subscription.Phase = NetworkScopeSubscriptionPhase.Ready;
        PeerScopeReady?.Invoke(peer.PeerId, scopeId);
    }

    private void HandleScopeUnload(NetworkPeer peer, NetworkPacket packet)
    {
        if (IsServer || peer.Phase != NetworkConnectionPhase.Ready || World is null)
            throw new NetworkProtocolException("Unexpected ScopeUnload message.");
        ValidateCurrentScenePacket(packet.Header, true);
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        reader.EnsureComplete();
        if (!scopeId.IsValid || scopeId.IsGlobal)
            throw new NetworkProtocolException($"ScopeUnload references invalid scope {scopeId}.");

        if (_clientScopeLoads.TryGetValue(scopeId, out var loadingOperation) &&
            !loadingOperation.IsTerminal)
        {
            loadingOperation.CancelRequested = true;
            loadingOperation.Diagnostic = "The server unloaded the scope before loading completed.";
            CancelClientScopeLoad(loadingOperation);
        }

        if (!_scopes.TryGetValue(scopeId, out var scope) && !_retiredScopes.Contains(scopeId))
            throw new NetworkProtocolException($"ScopeUnload references unknown scope {scopeId}.");

        // Make the scope unavailable before cleanup callbacks can observe or mutate it.
        if (scope is not null)
        {
            scope.IsLoaded = false;
            scope.IsReady = false;
            _scopes.Remove(scopeId);
            _retiredScopes.Add(scopeId);
            _clientScopeBaselines.Remove(scopeId);
            _suspendedScopeEntities.Remove(scopeId);
        }
        StructuralRevision = packet.Header.StructuralRevision;
        var cleanupErrors = new List<Exception>();
        if (scope is not null)
            try
            {
                World.UnregisterScope(scopeId, false);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        if (scope?.Content?.IsLoaded == true)
            try
            {
                World.Scene.UnloadNetworkContent(scope.Content, _scopeCoordinator);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        if (cleanupErrors.Count != 0)
            throw new NetworkProtocolException(
                $"Could not completely unload replication scope {scopeId}.",
                new AggregateException(cleanupErrors));
        SendPacket(peer.Connection, NetworkProtocolMessage.ScopeUnloaded,
            writer => writer.WriteUInt32(scopeId.Value), SceneEpoch, ServerTick, StructuralRevision);
        RemoveClientScopeLoadTracking(scopeId);
    }

    private void HandleScopeUnloaded(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer || peer.Phase != NetworkConnectionPhase.Ready)
            throw new NetworkProtocolException("Unexpected ScopeUnloaded message.");
        if (packet.Header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException("ScopeUnloaded uses a stale Scene epoch.");
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        reader.EnsureComplete();
        if (!peer.ScopeSubscriptions.TryGetValue(scopeId, out var subscription) ||
            subscription.Phase != NetworkScopeSubscriptionPhase.Unloading)
            throw new NetworkProtocolException($"Peer {peer.PeerId} acknowledged unexpected scope unload {scopeId}.");
        if (packet.Header.StructuralRevision != subscription.UnloadRevision)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} acknowledged scope unload {scopeId} at revision " +
                $"{packet.Header.StructuralRevision}; expected {subscription.UnloadRevision}.");
        peer.ScopeSubscriptions.Remove(scopeId);
        PeerScopeUnloaded?.Invoke(peer.PeerId, scopeId);
    }

    private static int ReadBoundedCount(ref NetworkReader reader, int maximum, string label)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new NetworkProtocolException(
                $"Baseline {label} count {count} is outside 0..{maximum}.");
        return count;
    }

    private static void EnsureRecordCapacity(int actual, int expected, string label)
    {
        if (actual >= expected)
            throw new NetworkProtocolException(
                $"Baseline contains more {label} records than declared ({expected}).");
    }

    private static void ValidateBaselineCount(int actual, int expected, string label)
    {
        if (actual != expected)
            throw new NetworkProtocolException(
                $"Baseline ended with {actual} {label}; expected {expected}.");
    }

    private void ValidateReadyPacket(NetworkPeer peer, NetworkPacketHeader header)
    {
        if (!peer.PeerId.IsValid || peer.Phase is
            NetworkConnectionPhase.AwaitingHello or
            NetworkConnectionPhase.AwaitingWelcome or
            NetworkConnectionPhase.Rejected)
            throw new NetworkProtocolException("Received a session packet before handshake completion.");
        if (header.SessionId != SessionId)
            throw new NetworkProtocolException(
                $"Session identity mismatch for peer {peer.PeerId}: expected {SessionId}, received {header.SessionId}.");
    }

    private void HandleDisconnected(
        TransportConnectionId connection,
        TransportDisconnectReason reason,
        string? diagnostic)
    {
        if (!_peersByConnection.Remove(connection, out var peer))
            return;

        diagnostic = peer.RemoteDiagnostic ?? diagnostic;

        if (peer.PeerId.IsValid)
        {
            // Remove the dead peer before authoritative cleanup broadcasts structural changes.
            // Its transport connection has already been closed and must not be a send target.
            _peersById.Remove(peer.PeerId);
            if (IsServer && World is not null)
            {
                foreach (var entityId in World.GetOwnedEntities(peer.PeerId))
                    if (World.GetDestroyWithOwner(entityId))
                        DespawnAuthoritative(entityId);
                    else
                        SetOwnerAuthoritative(entityId, NetworkPeerId.None);
                ClearPlayerEntityAuthoritative(peer.PeerId);
            }
            PeerDisconnected?.Invoke(peer.PeerId, reason, diagnostic);
        }
        else if (!IsServer && peer.Phase != NetworkConnectionPhase.Rejected)
        {
            ConnectionFailed?.Invoke(reason, diagnostic);
        }

        if (!IsServer && connection == _serverConnection)
        {
            CancelAllClientScopeLoads("The server connection ended while scope loading was active.");
            _serverConnection = TransportConnectionId.None;
            LocalPeerId = NetworkPeerId.None;
            SessionId = Guid.Empty;
            List<Exception>? cleanupErrors = null;
            if (World is not null)
            {
                var disconnectedScene = World.Scene;
                World.EntityRemoved -= HandleWorldEntityRemoved;
                try
                {
                    ClearScopeState(true);
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
                try
                {
                    World.Dispose(true);
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
                finally
                {
                    disconnectedScene.SetStartPreparationGate(_ => false);
                    World = null;
                }
            }
            _clientBaseline = null;
            _clientSceneReady = false;
            _lastStateSequences.Clear();
            _lastClientTransformSequences.Clear();
            _pendingLiveSpawns.Clear();
            if (cleanupErrors is not null)
                throw new AggregateException(
                    "One or more resources failed while cleaning up a disconnected client world.",
                    cleanupErrors);
        }
    }

    private void SendHello(TransportConnectionId connection)
    {
        var packet = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Hello,
                Guid.Empty,
                NetworkSceneEpoch.None,
                0,
                NetworkStructuralRevision.None),
            writer =>
            {
                writer.WriteUInt16(NetworkProtocol.Version);
                writer.WriteString(_options.GameBuildId, 256);
                writer.WriteString(_options.ContentFingerprint, 256);
                writer.WriteString(_messages.SchemaHash.Hex, 64);
                writer.WriteString(_replication.SchemaHash.Hex, 64);
            },
            _options.MaxProtocolPayload);
        Transport.Send(connection, packet, NetworkDelivery.ReliableOrdered, 0);
    }

    private void SendReject(TransportConnectionId connection, string diagnostic)
    {
        var maximumDiagnosticBytes = Math.Min(
            1024,
            Math.Min(
                _options.MaxProtocolPayload - sizeof(int),
                Transport.Capabilities.MaxReliablePayload - NetworkProtocol.HeaderLength - sizeof(int)));
        if (maximumDiagnosticBytes < 0)
            throw new InvalidOperationException(
                "The active transport cannot fit the minimum networking Reject packet.");
        var packet = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Reject,
                SessionId,
                NetworkSceneEpoch.None,
                0,
                NetworkStructuralRevision.None),
            writer => writer.WriteString(
                TruncateUtf8(diagnostic, maximumDiagnosticBytes),
                maximumDiagnosticBytes),
            _options.MaxProtocolPayload);
        Transport.Send(connection, packet, NetworkDelivery.ReliableOrdered, 0);
    }

    private void ValidateControlPacketsForTransport()
    {
        if (Transport.Capabilities.MaxReliablePayload < NetworkProtocol.HeaderLength + sizeof(int))
            throw new InvalidOperationException(
                $"The active transport's reliable payload limit of " +
                $"{Transport.Capabilities.MaxReliablePayload} bytes cannot fit the minimum Dreambit " +
                $"network packet size of {NetworkProtocol.HeaderLength + sizeof(int)} bytes.");
        if (Transport.Capabilities.MaxUnreliablePayload < NetworkProtocol.HeaderLength)
            throw new InvalidOperationException(
                $"The active transport's unreliable payload limit of " +
                $"{Transport.Capabilities.MaxUnreliablePayload} bytes cannot fit the " +
                $"{NetworkProtocol.HeaderLength}-byte Dreambit network header.");

        var hello = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Hello,
                Guid.Empty,
                NetworkSceneEpoch.None,
                0,
                NetworkStructuralRevision.None),
            writer =>
            {
                writer.WriteUInt16(NetworkProtocol.Version);
                writer.WriteString(_options.GameBuildId, 256);
                writer.WriteString(_options.ContentFingerprint, 256);
                writer.WriteString(_messages.SchemaHash.Hex, 64);
                writer.WriteString(_replication.SchemaHash.Hex, 64);
            },
            _options.MaxProtocolPayload);
        if (hello.Length > Transport.Capabilities.MaxReliablePayload)
            throw new InvalidOperationException(
                $"The configured handshake requires a {hello.Length}-byte reliable packet, " +
                $"exceeding the active transport limit of " +
                $"{Transport.Capabilities.MaxReliablePayload} bytes.");
    }

    private void SendPacket(
        TransportConnectionId connection,
        NetworkProtocolMessage message,
        Action<NetworkWriter>? payload,
        NetworkSceneEpoch sceneEpoch = default,
        ulong serverTick = 0,
        NetworkStructuralRevision structuralRevision = default,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered,
        byte channel = 0)
    {
        var packet = EncodePacket(
            message,
            payload,
            sceneEpoch,
            serverTick,
            structuralRevision);
        Transport.Send(connection, packet, delivery, channel);
    }

    private byte[] EncodePacket(
        NetworkProtocolMessage message,
        Action<NetworkWriter>? payload,
        NetworkSceneEpoch sceneEpoch = default,
        ulong serverTick = 0,
        NetworkStructuralRevision structuralRevision = default) =>
        NetworkProtocol.Encode(
            new NetworkPacketHeader(
                message,
                SessionId,
                sceneEpoch,
                serverTick,
                structuralRevision),
            payload,
            _options.MaxProtocolPayload);

    private static void WriteSpawn(
        NetworkWriter writer,
        Entity entity,
        NetworkEntityId id,
        EntityBlueprint blueprint,
        NetworkSpawnOptions options)
    {
        writer.WriteUInt32(options.Scope.Value);
        writer.WriteUInt64(id.Value);
        writer.WriteGuid(blueprint.AssetId.Value);
        writer.WriteString(blueprint.AssetName, 1024);
        writer.WriteUInt32(options.Owner.Value);
        writer.WriteBoolean(options.DestroyWithOwner);
        writer.WriteBoolean(entity.LocallyEnabled);
        byte flags = 0;
        if (options.Position.HasValue) flags |= 2;
        if (options.Rotation.HasValue) flags |= 4;
        if (options.Scale.HasValue) flags |= 8;
        writer.WriteByte(flags);
        if (options.Position is { } position) WriteVector3(writer, position);
        if (options.Rotation is { } rotation) WriteVector3(writer, rotation);
        if (options.Scale is { } scale) WriteVector3(writer, scale);
    }

    private void HandleSpawn(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive an authoritative Spawn message.");
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: true);
        if (World is null)
            throw new NetworkProtocolException("Received a Spawn without an active NetworkWorld.");

        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var id = new NetworkEntityId(reader.ReadUInt64());
        var assetId = new AssetId(reader.ReadGuid());
        var fallbackName = reader.ReadString(1024);
        var owner = new NetworkPeerId(reader.ReadUInt32());
        var destroyWithOwner = reader.ReadBoolean();
        var intendedEnabled = reader.ReadBoolean();
        var flags = reader.ReadByte();
        if ((flags & ~14) != 0)
            throw new NetworkProtocolException($"Spawn options contain unknown flags {flags}.");
        Vector3? position = (flags & 2) != 0 ? ReadVector3(ref reader) : null;
        Vector3? rotation = (flags & 4) != 0 ? ReadVector3(ref reader) : null;
        Vector3? scale = (flags & 8) != 0 ? ReadVector3(ref reader) : null;
        reader.EnsureComplete();
        if (!scopeId.IsValid || !id.IsValid || assetId.IsEmpty)
            throw new NetworkProtocolException("Spawn contains an empty network or Blueprint identity.");
        if (!_scopes.TryGetValue(scopeId, out var scope) || !scope.IsLoaded || !scope.IsReady)
            throw new NetworkProtocolException($"Spawn references unavailable replication scope {scopeId}.");

        Entity? entity = null;
        var registered = false;
        try
        {
            var blueprint = Resources.LoadDreambitAsset(assetId, fallbackName, typeof(EntityBlueprint))
                            as EntityBlueprint
                            ?? throw new InvalidOperationException(
                                $"Blueprint '{fallbackName}' ({assetId}) could not be loaded.");
            entity = scopeId.IsGlobal
                ? World.Scene.CreateNetworkEntity(
                    blueprint, intendedEnabled, position, rotation, scale)
                : World.Scene.CreateNetworkContentEntity(
                    scope.Content!, blueprint, _scopeCoordinator,
                    intendedEnabled, position, rotation, scale);
            SetHierarchyUpdatesSuspended(entity, true);
            World.RegisterDynamicEntity(
                entity,
                id,
                owner,
                assetId,
                fallbackName,
                destroyWithOwner,
                scopeId);
            registered = true;
            if (!World.TryGetRecord(id, out var record) || record is null)
                throw new InvalidOperationException($"Network Entity {id} was not indexed after spawn.");
            _pendingLiveSpawns.Add(
                id,
                new PendingLiveSpawn(
                    entity,
                    intendedEnabled,
                    record.ReplicationBindings));
            StructuralRevision = packet.Header.StructuralRevision;
        }
        catch (Exception exception) when (exception is not NetworkProtocolException)
        {
            var cleanupErrors = new List<Exception>();
            _pendingLiveSpawns.Remove(id);
            if (registered)
                try
                {
                    World.Unregister(id, false);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            if (entity is not null)
                try
                {
                    World.Scene.DestroyEntityHierarchyImmediately(entity);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(cleanupException);
                }
            Exception failure = exception;
            if (cleanupErrors.Count != 0)
            {
                cleanupErrors.Insert(0, exception);
                failure = new AggregateException(
                    "Remote network spawn failed and rollback also reported errors.",
                    cleanupErrors);
            }
            throw new NetworkProtocolException(
                $"Could not materialize network Blueprint '{fallbackName}' ({assetId}): {failure.Message}",
                failure);
        }
    }

    private void HandleSpawnReady(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive SpawnReady.");
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: false);
        if (packet.Header.StructuralRevision != StructuralRevision)
            throw new NetworkProtocolException(
                $"SpawnReady revision {packet.Header.StructuralRevision} does not match " +
                $"the active structural revision {StructuralRevision}.");
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var id = new NetworkEntityId(reader.ReadUInt64());
        reader.EnsureComplete();
        if (!_pendingLiveSpawns.Remove(id, out var pending))
            throw new NetworkProtocolException($"SpawnReady references non-pending network Entity {id}.");
        if (pending.RemainingComponentIds.Count != 0)
            throw new NetworkProtocolException(
                $"SpawnReady for network Entity {id} arrived before initial state for Component(s) " +
                $"{string.Join(", ", pending.RemainingComponentIds)}.");

        if (World is null || !World.TryGetRecord(id, out var record) || record is null ||
            record.Scope != scopeId)
            throw new NetworkProtocolException(
                $"SpawnReady references missing or incorrectly scoped network Entity {id}.");
        NotifyNetworkSpawnReady(
            record,
            packet.Header.SceneEpoch,
            packet.Header.ServerTick);
        pending.Entity.Enabled = pending.IntendedEnabled;
        SetHierarchyUpdatesSuspended(pending.Entity, false);
    }

    private void HandleDespawn(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive an authoritative Despawn message.");
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: true);
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var id = new NetworkEntityId(reader.ReadUInt64());
        reader.EnsureComplete();
        if (World is null || !World.TryGetRecord(id, out var record) || record is null ||
            record.Scope != scopeId)
            throw new NetworkProtocolException(
                $"Despawn references unknown network Entity {id} in Scene epoch {SceneEpoch}.");
        World.DespawnLocal(id, false);
        StructuralRevision = packet.Header.StructuralRevision;
    }

    private void DespawnAuthoritative(NetworkEntityId id)
    {
        if (World is null)
            throw new InvalidOperationException("There is no active NetworkWorld.");
        if (!World.TryGetRecord(id, out var record) || record is null)
            throw new InvalidOperationException($"Network Entity {id} is not registered.");
        AdvanceStructuralRevision();
        foreach (var peer in _peersById.Values)
        {
            RemoveProjectedEntityMappings(peer, id);
            if (peer.IsLocal || !PeerReceivesStructure(peer, record.Scope))
                continue;
            var revision = AdvancePeerRevision(peer);
            SendPacket(
                peer.Connection,
                NetworkProtocolMessage.Despawn,
                writer =>
                {
                    writer.WriteUInt32(record.Scope.Value);
                    writer.WriteUInt64(id.Value);
                },
                SceneEpoch,
                ServerTick,
                revision);
        }
        World.DespawnLocal(id, false);
    }

    private void SetOwnerAuthoritative(NetworkEntityId id, NetworkPeerId owner)
    {
        if (World is null || !World.TryGetRecord(id, out var record) || record is null)
            return;
        World.SetOwner(id, owner);
        AdvanceStructuralRevision();
        PublishOwnership(id, owner, record.Scope);
    }

    private void ClearPlayerEntityAuthoritative(NetworkPeerId peerId)
    {
        if (World is null || !World.RemovePlayerEntity(peerId))
            return;
        AdvanceStructuralRevision();
        foreach (var recipient in _peersById.Values)
        {
            if (recipient.IsLocal || !ReceivesLiveStructure(recipient) ||
                !recipient.ProjectedPlayerEntities.Remove(peerId))
                continue;
            SendPlayerMapping(recipient, peerId, NetworkEntityId.None);
        }
    }

    private void HandlePlayerEntity(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive PlayerEntity assignments.");
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: true);
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var peerId = new NetworkPeerId(reader.ReadUInt32());
        var entityId = new NetworkEntityId(reader.ReadUInt64());
        reader.EnsureComplete();
        if (World is null)
            throw new NetworkProtocolException("PlayerEntity arrived without a NetworkWorld.");
        if (!peerId.IsValid)
            throw new NetworkProtocolException("PlayerEntity contains peer ID zero.");
        if (entityId.IsValid)
        {
            if (!World.TryGetRecord(entityId, out var record) || record is null || record.Scope != scopeId)
                throw new NetworkProtocolException(
                    $"PlayerEntity references missing network Entity {entityId}.");
            World.SetPlayerEntity(peerId, entityId);
        }
        else
            World.RemovePlayerEntity(peerId);
        StructuralRevision = packet.Header.StructuralRevision;
    }

    private void HandleOwnership(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("A server cannot receive ownership assignments.");
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: true);
        var reader = new NetworkReader(packet.Payload.Span);
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var entityId = new NetworkEntityId(reader.ReadUInt64());
        var owner = new NetworkPeerId(reader.ReadUInt32());
        reader.EnsureComplete();
        if (World is null || !World.TryGetRecord(entityId, out var record) || record is null ||
            record.Scope != scopeId)
            throw new NetworkProtocolException(
                $"Ownership assignment references missing network Entity {entityId}.");
        World.SetOwner(entityId, owner);
        StructuralRevision = packet.Header.StructuralRevision;
    }

    private void PublishOwnership(
        NetworkEntityId id,
        NetworkPeerId owner,
        NetworkReplicationScopeId scope)
    {
        foreach (var peer in _peersById.Values)
        {
            if (peer.IsLocal || !PeerReceivesStructure(peer, scope))
                continue;
            var revision = AdvancePeerRevision(peer);
            SendPacket(
                peer.Connection,
                NetworkProtocolMessage.Ownership,
                writer =>
                {
                    writer.WriteUInt32(scope.Value);
                    writer.WriteUInt64(id.Value);
                    writer.WriteUInt32(owner.Value);
                },
                SceneEpoch,
                ServerTick,
                revision);
        }
    }

    private void PublishPlayerMapping(
        NetworkPeerId player,
        NetworkEntityId entity,
        NetworkReplicationScopeId scope)
    {
        foreach (var recipient in _peersById.Values)
        {
            if (recipient.IsLocal || !ReceivesLiveStructure(recipient))
                continue;
            if (PeerReceivesStructure(recipient, scope))
            {
                recipient.ProjectedPlayerEntities[player] = entity;
                SendPlayerMapping(recipient, player, entity);
            }
            else if (recipient.ProjectedPlayerEntities.Remove(player))
            {
                SendPlayerMapping(recipient, player, NetworkEntityId.None);
            }
        }
    }

    private void SendPlayerMapping(
        NetworkPeer recipient,
        NetworkPeerId player,
        NetworkEntityId entity)
    {
        var scope = NetworkReplicationScopeId.Global;
        if (entity.IsValid && World?.TryGetRecord(entity, out var record) == true && record is not null)
            scope = record.Scope;
        var revision = AdvancePeerRevision(recipient);
        SendPacket(recipient.Connection, NetworkProtocolMessage.PlayerEntity, writer =>
        {
            writer.WriteUInt32(scope.Value);
            writer.WriteUInt32(player.Value);
            writer.WriteUInt64(entity.Value);
        }, SceneEpoch, ServerTick, revision);
    }

    private static bool ReceivesLiveStructure(NetworkPeer peer) =>
        peer.Phase is NetworkConnectionPhase.Ready or NetworkConnectionPhase.Synchronizing;

    private void HandleWorldEntityRemoved(NetworkEntityRecord record)
    {
        if (!IsServer || _disposed)
            return;
        AdvanceStructuralRevision();
        foreach (var peer in _peersById.Values)
        {
            RemoveProjectedEntityMappings(peer, record.Id);
            if (peer.IsLocal || !PeerReceivesStructure(peer, record.Scope))
                continue;
            var revision = AdvancePeerRevision(peer);
            SendPacket(
                peer.Connection,
                NetworkProtocolMessage.Despawn,
                writer =>
                {
                    writer.WriteUInt32(record.Scope.Value);
                    writer.WriteUInt64(record.Id.Value);
                },
                SceneEpoch,
                ServerTick,
                revision);
        }
    }

    private void ValidateCurrentScenePacket(
        NetworkPacketHeader header,
        bool requireNextRevision)
    {
        if (header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException(
                $"Scene epoch mismatch: expected {SceneEpoch}, received {header.SceneEpoch}.");
        if (requireNextRevision &&
            header.StructuralRevision.Value != StructuralRevision.Value + 1)
            throw new NetworkProtocolException(
                $"Structural revision mismatch in Scene epoch {SceneEpoch}: expected " +
                $"{StructuralRevision.Value + 1}, received {header.StructuralRevision}.");
    }

    internal void SendSnapshotNow()
    {
        if (!IsServer)
            throw new InvalidOperationException("Only the server can publish replicated state.");
        if (World is null)
            return;
        var sequence = NextStateSequence();
        foreach (var peer in _peersById.Values)
        {
            if (peer.IsLocal || peer.Phase != NetworkConnectionPhase.Ready)
                continue;
            SendScopeSnapshots(peer, NetworkReplicationScopeId.Global, sequence);
            foreach (var subscription in peer.ScopeSubscriptions.Values)
                if (subscription.Phase == NetworkScopeSubscriptionPhase.Ready)
                    SendScopeSnapshots(peer, subscription.Scope, sequence);
        }
    }

    private void SendScopeSnapshots(
        NetworkPeer peer,
        NetworkReplicationScopeId scope,
        uint sequence)
    {
        if (World is null)
            return;
        foreach (var record in World.GetRecords(scope))
            foreach (var binding in record.ReplicationBindings)
                SendStateRecord(
                    peer,
                    scope,
                    record.Id,
                    binding,
                    sequence,
                    NetworkDelivery.UnreliableSequenced,
                    2);
    }

    internal void SendClientTransformsNow()
    {
        if (Role != NetworkRole.Client)
            throw new InvalidOperationException(
                "Only a remote client can publish client-authoritative transforms.");
        if (World is null || !LocalPeerId.IsValid || !_serverConnection.IsValid)
            return;

        var sequence = NextStateSequence();
        foreach (var record in World.Records)
        {
            if (record.Owner != LocalPeerId)
                continue;
            if (record.Scope.IsGlobal
                    ? !_clientSceneReady
                    : !_scopes.TryGetValue(record.Scope, out var scope) || !scope.IsReady)
                continue;

            var transform = record.Entity.GetComponent<NetworkTransform2D>();
            if (transform is null || !transform.AllowsClientAuthority)
                continue;

            transform.CaptureAuthoritativeTransform();
            var position = transform.AuthoritativePosition;
            var rotation = transform.AuthoritativeRotation;
            var scale = transform.AuthoritativeScale;
            if (!IsFinite(position) || !float.IsFinite(rotation) || !IsFinite(scale))
                continue;

            SendPacket(
                _serverConnection,
                NetworkProtocolMessage.ClientTransform,
                writer =>
                {
                    writer.WriteUInt32(sequence);
                    writer.WriteUInt32(record.Scope.Value);
                    writer.WriteUInt64(record.Id.Value);
                    WriteVector2(writer, position);
                    writer.WriteSingle(rotation);
                    WriteVector2(writer, scale);
                },
                SceneEpoch,
                ServerTick,
                StructuralRevision,
                NetworkDelivery.UnreliableSequenced,
                3);
        }
    }

    private byte[] EncodeStateRecord(
        NetworkReplicationScopeId scope,
        NetworkEntityId entityId,
        NetworkReplicationBinding binding,
        uint sequence,
        NetworkStructuralRevision revision)
    {
        var componentPayload = binding.Capture();
        var packet = EncodePacket(
            NetworkProtocolMessage.Snapshot,
            writer =>
            {
                writer.WriteUInt32(sequence);
                writer.WriteUInt32(scope.Value);
                writer.WriteUInt64(entityId.Value);
                writer.WriteUInt16(binding.Descriptor.Id);
                writer.WriteLengthPrefixedBytes(
                    componentPayload,
                    binding.Descriptor.MaximumPayload);
            },
            SceneEpoch,
            ServerTick,
            revision);
        EnsurePacketFitsReliableTransport(
            packet,
            $"initial Component {binding.Descriptor.Id} state for Entity {entityId}");
        return packet;
    }

    private void SendStateRecord(
        NetworkPeer peer,
        NetworkReplicationScopeId scope,
        NetworkEntityId entityId,
        NetworkReplicationBinding binding,
        uint sequence,
        NetworkDelivery delivery,
        byte channel)
    {
        var componentPayload = binding.Capture();
        var maximumPacket = delivery == NetworkDelivery.ReliableOrdered
            ? Transport.Capabilities.MaxReliablePayload
            : Transport.Capabilities.MaxUnreliablePayload;
        const int recordOverhead = sizeof(uint) + sizeof(uint) + sizeof(ulong) + sizeof(ushort) + sizeof(int);
        if (NetworkProtocol.HeaderLength + recordOverhead + componentPayload.Length > maximumPacket)
            throw new InvalidOperationException(
                $"Replicated Component {binding.Descriptor.Id} on Entity {entityId} requires " +
                $"{componentPayload.Length} state bytes, exceeding the transport packet limit " +
                $"{maximumPacket}. Use a smaller/custom representation.");

        SendPacket(
            peer.Connection,
            NetworkProtocolMessage.Snapshot,
            writer =>
            {
                writer.WriteUInt32(sequence);
                writer.WriteUInt32(scope.Value);
                writer.WriteUInt64(entityId.Value);
                writer.WriteUInt16(binding.Descriptor.Id);
                writer.WriteLengthPrefixedBytes(
                    componentPayload,
                    binding.Descriptor.MaximumPayload);
            },
            SceneEpoch,
            ServerTick,
            peer.ProjectedStructuralRevision,
            delivery,
            channel);
    }

    private void HandleSnapshot(NetworkPacket packet)
    {
        if (IsServer)
            throw new NetworkProtocolException("The authoritative server cannot receive Component snapshots.");
        if (IsStaleScenePacket(packet.Header))
            return;
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: false);
        // A future topology cannot be applied safely. Full snapshots make dropping this state healable.
        if (packet.Header.StructuralRevision.Value > StructuralRevision.Value)
            return;
        if (World is null)
            return;

        var reader = new NetworkReader(packet.Payload.Span);
        var sequence = reader.ReadUInt32();
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        if (!scopeId.IsValid)
            throw new NetworkProtocolException("A Component snapshot contains scope ID zero.");
        if (!_scopes.TryGetValue(scopeId, out var snapshotScope))
        {
            if (_clientScopeLoads.TryGetValue(scopeId, out var loadingOperation) &&
                !loadingOperation.IsTerminal)
                return;
            if (_retiredScopes.Contains(scopeId))
                return;
            throw new NetworkProtocolException($"Component snapshot references unknown scope {scopeId}.");
        }
        if (!snapshotScope.IsReady)
            return;
        var entityId = new NetworkEntityId(reader.ReadUInt64());
        var componentId = reader.ReadUInt16();
        var descriptor = _replication.GetById(componentId);
        var componentPayload = reader.ReadLengthPrefixedBytes(descriptor.MaximumPayload);
        reader.EnsureComplete();
        if (!entityId.IsValid)
            throw new NetworkProtocolException("A Component snapshot contains network Entity ID zero.");
        if (!World.TryGetRecord(entityId, out var record) || record is null || record.Scope != scopeId ||
            !World.TryGetReplicationBinding(entityId, componentId, out var binding) || binding is null)
            throw new NetworkProtocolException(
                $"Component snapshot references missing Component {componentId} on network Entity {entityId}.");

        var key = new ReplicationStateKey(entityId, componentId);
        if (_lastStateSequences.TryGetValue(key, out var previous) &&
            !IsNewerSequence(sequence, previous))
            return;
        try
        {
            binding.Apply(componentPayload);
            var applyKind = _pendingLiveSpawns.ContainsKey(entityId)
                ? NetworkStateApplyKind.InitialSpawn
                : NetworkStateApplyKind.Snapshot;
            binding.Component.NetworkStateApplied(new NetworkStateAppliedContext(
                entityId,
                componentId,
                applyKind,
                packet.Header.SceneEpoch,
                packet.Header.ServerTick)
            {
                Scope = scopeId
            });
            _lastStateSequences[key] = sequence;
            if (_pendingLiveSpawns.TryGetValue(entityId, out var pending))
                pending.RemainingComponentIds.Remove(componentId);
        }
        catch (Exception exception) when (exception is not NetworkProtocolException)
        {
            throw new NetworkProtocolException(
                $"Could not apply Component {componentId} on network Entity {entityId}: {exception.Message}");
        }
    }

    private void HandleClientTransform(NetworkPeer peer, NetworkPacket packet)
    {
        if (!IsServer)
            throw new NetworkProtocolException(
                "A client cannot receive a client-authoritative transform update.");
        if (IsStaleScenePacket(packet.Header))
            return;
        ValidateCurrentScenePacket(packet.Header, requireNextRevision: false);
        if (packet.Header.StructuralRevision != peer.ProjectedStructuralRevision || World is null)
            return;

        var reader = new NetworkReader(packet.Payload.Span);
        var sequence = reader.ReadUInt32();
        var scopeId = new NetworkReplicationScopeId(reader.ReadUInt32());
        var entityId = new NetworkEntityId(reader.ReadUInt64());
        var position = ReadVector2(ref reader);
        var rotation = reader.ReadSingle();
        var scale = ReadVector2(ref reader);
        reader.EnsureComplete();

        if (!scopeId.IsValid || !entityId.IsValid)
            throw new NetworkProtocolException(
                "A client transform update contains network Entity ID zero.");
        if (!IsFinite(position) || !float.IsFinite(rotation) || !IsFinite(scale))
            throw new NetworkProtocolException(
                $"Client transform update for Entity {entityId} contains a non-finite value.");

        // Ownership and authority may legitimately change while an unreliable packet is in flight.
        // Drop stale/unauthorized poses instead of disconnecting an otherwise valid peer.
        if (!PeerScopeIsReady(peer, scopeId) ||
            !World.TryGetRecord(entityId, out var record) || record is null || record.Scope != scopeId ||
            !World.TryGetEntity(entityId, out var entity) || entity is null ||
            World.GetOwner(entityId) != peer.PeerId)
        {
            return;
        }

        var transform = entity.GetComponent<NetworkTransform2D>();
        if (transform is null || !transform.AllowsClientAuthority)
            return;

        var key = new ClientTransformStateKey(peer.PeerId, entityId);
        if (_lastClientTransformSequences.TryGetValue(key, out var previous) &&
            !IsNewerSequence(sequence, previous))
        {
            return;
        }

        // A dedicated server needs the submitted pose immediately for simulation. A listen host
        // keeps the pose as a presentation target for remote Client-authority entities, avoiding
        // visible network-rate stepping in the host's local view.
        var interpolateOnHost =
            IsHost && transform.Authority == TransformAuthority.Client;
        transform.AcceptClientTransform(
            position,
            rotation,
            scale,
            applyImmediately: !interpolateOnHost);
        _lastClientTransformSequences[key] = sequence;
    }

    private byte[] EncodeUserMessage(INetworkMessageRegistration registration, object message)
    {
        using var body = new NetworkWriter(
            Math.Min(256, registration.MaximumPayload),
            registration.MaximumPayload);
        registration.Write(body, message);
        if (body.Length > registration.MaximumPayload)
            throw new NetworkProtocolException(
                $"Networking message {registration.Id} exceeds its registered payload limit.");

        using var payload = new NetworkWriter(
            Math.Min(256, 6 + body.Length),
            checked(6 + registration.MaximumPayload));
        payload.WriteUInt16(registration.Id);
        payload.WriteLengthPrefixedBytes(body.WrittenSpan, registration.MaximumPayload);
        return payload.ToArray();
    }

    private void DispatchUserMessage(
        ReadOnlySpan<byte> payload,
        bool inboundFromClient,
        NetworkPeerId sender,
        NetworkSceneEpoch sceneEpoch,
        ulong serverTick)
    {
        var reader = new NetworkReader(payload);
        var id = reader.ReadUInt16();
        var registration = _messages.GetById(id);
        var allowed = inboundFromClient
            ? registration.Direction is NetworkMessageDirection.ClientToServer or
                NetworkMessageDirection.Bidirectional
            : registration.Direction is NetworkMessageDirection.ServerToClient or
                NetworkMessageDirection.Bidirectional;
        if (!allowed)
            throw new NetworkProtocolException(
                $"Networking message {id} is not allowed in this direction.");
        var messageBytes = reader.ReadLengthPrefixedBytes(registration.MaximumPayload);
        reader.EnsureComplete();
        var messageReader = new NetworkReader(messageBytes);
        registration.ReadAndHandle(
            ref messageReader,
            new NetworkMessageContext(sender, sceneEpoch, serverTick));
    }

    private bool IsStaleScenePacket(NetworkPacketHeader header)
    {
        var expectedEpoch = _pendingSceneEpoch.IsValid ? _pendingSceneEpoch : SceneEpoch;
        return header.SceneEpoch.IsValid && header.SceneEpoch.Value < expectedEpoch.Value;
    }

    private static void RequireDelivery(
        QueuedTransportEvent transportEvent,
        NetworkDelivery delivery,
        byte channel)
    {
        if (transportEvent.Delivery != delivery || transportEvent.Channel != channel)
            throw new NetworkProtocolException(
                $"Protocol message used {transportEvent.Delivery} channel {transportEvent.Channel}; " +
                $"expected {delivery} channel {channel}.");
    }

    private static void RequireGameplayDelivery(
        QueuedTransportEvent transportEvent,
        bool inboundToServer)
    {
        var valid = transportEvent.Delivery switch
        {
            NetworkDelivery.ReliableOrdered => transportEvent.Channel == 1,
            NetworkDelivery.UnreliableSequenced => transportEvent.Channel ==
                (inboundToServer ? (byte)3 : (byte)2),
            _ => false
        };
        if (!valid)
            throw new NetworkProtocolException(
                $"Gameplay message used invalid {transportEvent.Delivery} channel " +
                $"{transportEvent.Channel}.");
    }

    private static void RequireSnapshotDelivery(QueuedTransportEvent transportEvent)
    {
        var valid =
            (transportEvent.Delivery == NetworkDelivery.UnreliableSequenced &&
             transportEvent.Channel == 2) ||
            (transportEvent.Delivery == NetworkDelivery.ReliableOrdered &&
             transportEvent.Channel == 0);
        if (!valid)
            throw new NetworkProtocolException(
                $"Component state used invalid {transportEvent.Delivery} channel " +
                $"{transportEvent.Channel}.");
    }

    private static void RequireClientTransformDelivery(QueuedTransportEvent transportEvent)
    {
        if (transportEvent.Delivery != NetworkDelivery.UnreliableSequenced ||
            transportEvent.Channel != 3)
        {
            throw new NetworkProtocolException(
                $"Client transform used invalid {transportEvent.Delivery} channel " +
                $"{transportEvent.Channel}.");
        }
    }

    private static void WriteVector2(NetworkWriter writer, Vector2 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    private static Vector2 ReadVector2(ref NetworkReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle());

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static void WriteVector3(NetworkWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    private static Vector3 ReadVector3(ref NetworkReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private void EnsurePacketFitsReliableTransport(byte[] packet, string label)
    {
        if (packet.Length > Transport.Capabilities.MaxReliablePayload)
            throw new InvalidOperationException(
                $"{label} requires a {packet.Length}-byte reliable packet, exceeding the active " +
                $"transport limit of {Transport.Capabilities.MaxReliablePayload} bytes.");
    }

    private static void SetHierarchyUpdatesSuspended(Entity root, bool suspended)
    {
        root.UpdatesSuspended = suspended;
        foreach (var child in root.Children)
            SetHierarchyUpdatesSuspended(child, suspended);
    }

    private void BindServerAuthoredEntities()
    {
        if (World is null)
            throw new InvalidOperationException(
                "A NetworkWorld is required before binding authored network entities.");

        World.BindServerAuthoredEntities(AllocateEntityId);
        foreach (var record in World.Records.ToArray())
            if (record.Origin == NetworkSpawnOrigin.AuthoredScene)
                NotifyNetworkSpawnReady(record, SceneEpoch, ServerTick);
    }

    private void NotifyNetworkSpawnReady(
        NetworkEntityRecord record,
        NetworkSceneEpoch sceneEpoch,
        ulong serverTick)
    {
        if (record.SpawnReadyNotified)
            return;

        record.SpawnReadyNotified = true;
        var context = new NetworkSpawnReadyContext(
            record.Id,
            record.Owner,
            Role,
            sceneEpoch,
            serverTick)
        {
            Scope = record.Scope
        };
        NotifyNetworkSpawnReadyHierarchy(record.Entity, context);
    }

    private static void NotifyNetworkSpawnReadyHierarchy(
        Entity entity,
        NetworkSpawnReadyContext context)
    {
        var components = entity.GetAllComponents().ToArray();
        var children = entity.Children.ToArray();
        foreach (var component in components)
            component.NetworkSpawnReady(context);

        foreach (var child in children)
        {
            // An authored hierarchy may contain another independent NetworkObject. Its own record
            // delivers a separate callback with the correct identity and owner.
            if (child.GetComponent<NetworkObject>() is not null)
                continue;
            NotifyNetworkSpawnReadyHierarchy(child, context);
        }
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        Span<byte> buffer = stackalloc byte[maximumBytes];
        Encoding.UTF8.GetEncoder().Convert(
            value.AsSpan(),
            buffer,
            true,
            out var charsUsed,
            out _,
            out _);
        return value[..charsUsed];
    }

    private NetworkReplicationScope GetAdditiveScope(NetworkReplicationScopeId scopeId)
    {
        if (!scopeId.IsValid || scopeId.IsGlobal ||
            !_scopes.TryGetValue(scopeId, out var scope) || !scope.IsLoaded)
            throw new KeyNotFoundException($"Replication scope {scopeId} is not a loaded additive scope.");
        return scope;
    }

    private NetworkReplicationScope GetScopeForSpawn(NetworkReplicationScopeId scopeId)
    {
        if (!_scopes.TryGetValue(scopeId, out var scope) || !scope.IsLoaded)
            throw new KeyNotFoundException($"Replication scope {scopeId} is not loaded.");
        return scope;
    }

    private NetworkReplicationScope GetClientScope(NetworkReplicationScopeId scopeId)
    {
        if (IsServer || scopeId.IsGlobal || !_scopes.TryGetValue(scopeId, out var scope) || !scope.IsLoaded)
            throw new InvalidOperationException($"Client replication scope {scopeId} is not loaded.");
        return scope;
    }

    private NetworkPeer GetPeer(NetworkPeerId peerId)
    {
        if (!peerId.IsValid || !_peersById.TryGetValue(peerId, out var peer))
            throw new KeyNotFoundException($"Network peer {peerId} is not connected.");
        return peer;
    }

    private bool CanAssignScopeToPeer(
        NetworkPeerId peerId,
        NetworkReplicationScopeId scope) =>
        scope.IsGlobal
            ? _peersById.ContainsKey(peerId)
            : IsPeerScopeReady(peerId, scope);

    private bool PeerReceivesStructure(NetworkPeer peer, NetworkReplicationScopeId scope)
    {
        if (!ReceivesLiveStructure(peer))
            return false;
        if (scope.IsGlobal)
            return true;
        // Once the scoped baseline has been enqueued, reliable ordering guarantees that later
        // structural packets reach the client only after baseline End made the local scope ready.
        // Including AwaitingReady closes the mutation window between baseline capture and its ack;
        // unreliable snapshots still wait for the server-observed Ready phase.
        return peer.Phase == NetworkConnectionPhase.Ready &&
               peer.ScopeSubscriptions.TryGetValue(scope, out var subscription) &&
               subscription.Phase is NetworkScopeSubscriptionPhase.AwaitingReady or
                   NetworkScopeSubscriptionPhase.Ready;
    }

    private bool PeerScopeIsReady(NetworkPeer peer, NetworkReplicationScopeId scope) =>
        scope.IsGlobal
            ? peer.Phase == NetworkConnectionPhase.Ready
            : peer.Phase == NetworkConnectionPhase.Ready &&
              peer.ScopeSubscriptions.TryGetValue(scope, out var subscription) &&
              subscription.Phase == NetworkScopeSubscriptionPhase.Ready;

    private NetworkStructuralRevision AdvancePeerRevision(NetworkPeer peer)
    {
        peer.ProjectedStructuralRevision = NextStructuralRevision(peer.ProjectedStructuralRevision);
        return peer.ProjectedStructuralRevision;
    }

    private void ValidatePeerKnownProjection(NetworkPeer peer, NetworkPacketHeader header)
    {
        if (header.SceneEpoch != SceneEpoch)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} used Scene epoch {header.SceneEpoch}; expected {SceneEpoch}.");
        if (header.StructuralRevision.Value > peer.ProjectedStructuralRevision.Value)
            throw new NetworkProtocolException(
                $"Peer {peer.PeerId} claimed future projected revision {header.StructuralRevision}; " +
                $"server has sent {peer.ProjectedStructuralRevision}.");
    }

    private void RemoveProjectedPlayersInScope(
        NetworkPeer peer,
        NetworkReplicationScopeId scope)
    {
        if (World is null)
            return;
        List<NetworkPeerId>? removals = null;
        foreach (var mapping in peer.ProjectedPlayerEntities)
            if (World.TryGetRecord(mapping.Value, out var record) && record?.Scope == scope)
                (removals ??= []).Add(mapping.Key);
        if (removals is null)
            return;
        foreach (var player in removals)
        {
            peer.ProjectedPlayerEntities.Remove(player);
            SendPlayerMapping(peer, player, NetworkEntityId.None);
        }
    }

    private static void RemoveProjectedEntityMappings(NetworkPeer peer, NetworkEntityId entity)
    {
        List<NetworkPeerId>? removals = null;
        foreach (var mapping in peer.ProjectedPlayerEntities)
            if (mapping.Value == entity)
                (removals ??= []).Add(mapping.Key);
        if (removals is not null)
            foreach (var player in removals)
                peer.ProjectedPlayerEntities.Remove(player);
    }

    private void RegisterClientScopeLoad(ClientNetworkScopeLoadOperation operation)
    {
        if (!IsClient || IsHost)
            throw new NetworkProtocolException("Only a remote client can create a scope-load operation.");
        if (!_clientScopeLoads.TryAdd(operation.Scope, operation))
            throw new NetworkProtocolException($"Scope {operation.Scope} already has a loading operation.");
        _clientScopeLoadOrder.Add(operation.Scope);
        PublishScopeLoadStatus(operation, true);
    }

    private bool AdvanceClientScopeLoad(ClientNetworkScopeLoadOperation operation)
    {
        if (operation.CancelRequested)
        {
            CancelClientScopeLoad(operation);
            return true;
        }
        ValidateClientScopeLoadLifetime(operation);
        return operation.Phase switch
        {
            NetworkScopeLoadPhase.LoadingContent => LoadClientScopeContent(operation),
            NetworkScopeLoadPhase.WaitingForBaseline => false,
            NetworkScopeLoadPhase.ValidatingBaseline => ValidateClientBaselineItem(operation),
            NetworkScopeLoadPhase.BindingAuthoredEntities => BindClientAuthoredItem(operation),
            NetworkScopeLoadPhase.CreatingDynamicEntities => CreateClientDynamicEntityItem(operation),
            NetworkScopeLoadPhase.ApplyingPlayerMappings => ApplyClientPlayerMappingItem(operation),
            NetworkScopeLoadPhase.ApplyingComponentStates => ApplyClientComponentStateItem(operation),
            NetworkScopeLoadPhase.NotifyingSpawnReady => NotifyClientSpawnReadyItem(operation),
            NetworkScopeLoadPhase.Committing => CommitClientScopeLoad(operation),
            _ => false
        };
    }

    private void ValidateClientScopeLoadLifetime(ClientNetworkScopeLoadOperation operation)
    {
        if (_disposed || operation.SessionId != SessionId ||
            operation.SceneEpoch != SceneEpoch ||
            !ReferenceEquals(operation.World, World) ||
            World is null || !ReferenceEquals(operation.World.Scene, World.Scene))
            throw new OperationCanceledException(
                $"Scope {operation.Scope} belongs to an obsolete network Scene or session.");
    }

    private bool LoadClientScopeContent(ClientNetworkScopeLoadOperation operation)
    {
        if (World is null)
            throw new OperationCanceledException("The client NetworkWorld disappeared while loading content.");
        var blueprint = Resources.LoadDreambitAsset(
                            operation.SourceAssetId,
                            operation.SourceAssetName,
                            typeof(SceneBlueprint)) as SceneBlueprint
                        ?? throw new InvalidOperationException(
                            $"Scene Blueprint '{operation.SourceAssetName}' " +
                            $"({operation.SourceAssetId}) could not be loaded.");
        var content = World.Scene.LoadNetworkAdditive(
            blueprint,
            operation.SourceAssetName,
            _scopeCoordinator);
        operation.Content = content;
        var scope = new NetworkReplicationScope(
            operation.Scope,
            operation.SceneEpoch,
            operation.SourceAssetId,
            operation.SourceAssetName,
            content)
        {
            IsReady = false
        };
        _scopes.Add(operation.Scope, scope);
        SuspendScopeEntities(operation.Scope, content.OwnedEntities);
        World.MarkScopePending(operation.Scope);
        if (!operation.ScopeLoadedSent)
        {
            operation.ScopeLoadedSent = true;
            SendPacket(
                _serverConnection,
                NetworkProtocolMessage.ScopeLoaded,
                writer => writer.WriteUInt32(operation.Scope.Value),
                operation.SceneEpoch,
                ServerTick,
                operation.ManifestRevision);
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.WaitingForBaseline);
        return true;
    }

    private bool ValidateClientBaselineItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline ??
                       throw new NetworkProtocolException(
                           $"Scope {operation.Scope} entered validation without a complete baseline.");
        while (true)
        {
            switch (operation.ValidationStage)
            {
                case ClientBaselineValidationStage.Counts:
                    ValidateBaselineCount(baseline.Authored.Count, baseline.ExpectedAuthored, "authored entities");
                    ValidateBaselineCount(baseline.Dynamic.Count, baseline.ExpectedDynamic, "dynamic entities");
                    ValidateBaselineCount(baseline.Players.Count, baseline.ExpectedPlayers, "player mappings");
                    ValidateBaselineCount(baseline.Components.Count, baseline.ExpectedComponents, "Component states");
                    operation.PreviousStructuralRevision = StructuralRevision;
                    operation.PreviousServerTick = ServerTick;
                    operation.ContentEntities = operation.Scope.IsGlobal
                        ? operation.World.Scene.GetAllEntities()
                            .Where(entity => entity.ContentOwner is null)
                            .ToArray()
                        : (operation.Content ?? GetClientScope(operation.Scope).Content)!
                            .OwnedEntities.ToArray();
                    operation.SourceMappings = operation.Scope.IsGlobal
                        ? operation.ContentEntities
                            .Select(entity => new KeyValuePair<Guid, Entity>(entity.Id, entity))
                            .ToArray()
                        : operation.Content!.EntitiesBySourceGuid.ToArray();
                    if (!_suspendedScopeEntities.ContainsKey(operation.Scope))
                        SuspendScopeEntities(operation.Scope, operation.ContentEntities);
                    operation.World.MarkScopePending(operation.Scope);
                    var presentAuthoredCount = baseline.Authored.Count(binding => binding.IsPresent);
                    operation.TotalItems = checked(
                        (operation.Scope.IsGlobal ? 8 : 9) +
                        baseline.Authored.Count +
                        baseline.Dynamic.Count * 3 +
                        baseline.Players.Count * 2 +
                        baseline.Components.Count * 2 +
                        operation.SourceMappings.Length +
                        operation.ContentEntities.Length * 2 +
                        presentAuthoredCount);
                    operation.ValidationStage = ClientBaselineValidationStage.AuthoredRecords;
                    operation.ValidationCursor = 0;
                    return true;

                case ClientBaselineValidationStage.AuthoredRecords:
                    if (operation.ValidationCursor < baseline.Authored.Count)
                    {
                        var binding = baseline.Authored[operation.ValidationCursor++];
                        if (binding.Scope != operation.Scope || binding.SourceGuid == Guid.Empty ||
                            !binding.NetworkEntityId.IsValid)
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline contains an invalid authored binding.");
                        if (!operation.AuthoredBySource.TryAdd(binding.SourceGuid, binding))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline duplicates source GUID {binding.SourceGuid}.");
                        if (!operation.EntityIds.Add(binding.NetworkEntityId))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline duplicates Entity {binding.NetworkEntityId}.");
                        if (binding.IsPresent)
                            operation.PresentEntityIds.Add(binding.NetworkEntityId);
                        return true;
                    }
                    operation.ValidationStage = ClientBaselineValidationStage.DynamicRecords;
                    operation.ValidationCursor = 0;
                    continue;

                case ClientBaselineValidationStage.DynamicRecords:
                    if (operation.ValidationCursor < baseline.Dynamic.Count)
                    {
                        var spawn = baseline.Dynamic[operation.ValidationCursor++];
                        if (spawn.Scope != operation.Scope || !spawn.EntityId.IsValid ||
                            spawn.BlueprintAssetId.IsEmpty)
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline contains an invalid dynamic spawn.");
                        if (!operation.EntityIds.Add(spawn.EntityId))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline duplicates Entity {spawn.EntityId}.");
                        operation.PresentEntityIds.Add(spawn.EntityId);
                        return true;
                    }
                    operation.ValidationStage = ClientBaselineValidationStage.PlayerRecords;
                    operation.ValidationCursor = 0;
                    continue;

                case ClientBaselineValidationStage.PlayerRecords:
                    if (operation.ValidationCursor < baseline.Players.Count)
                    {
                        var player = baseline.Players[operation.ValidationCursor++];
                        if (!player.Key.IsValid || !player.Value.IsValid ||
                            !operation.PlayerIds.Add(player.Key))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline contains an invalid or duplicate player mapping.");
                        if (!operation.PresentEntityIds.Contains(player.Value))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} player {player.Key} references unavailable Entity {player.Value}.");
                        return true;
                    }
                    operation.ValidationStage = ClientBaselineValidationStage.ComponentRecords;
                    operation.ValidationCursor = 0;
                    continue;

                case ClientBaselineValidationStage.ComponentRecords:
                    if (operation.ValidationCursor < baseline.Components.Count)
                    {
                        var state = baseline.Components[operation.ValidationCursor++];
                        if (!state.EntityId.IsValid ||
                            !operation.ComponentKeys.Add((state.EntityId, state.ComponentId)))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} baseline contains an invalid or duplicate " +
                                $"Component {state.ComponentId} state for Entity {state.EntityId}.");
                        if (!operation.PresentEntityIds.Contains(state.EntityId))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} Component state references unavailable Entity {state.EntityId}.");
                        return true;
                    }
                    operation.ValidationStage = ClientBaselineValidationStage.SourceMappings;
                    operation.ValidationCursor = 0;
                    continue;

                case ClientBaselineValidationStage.SourceMappings:
                    if (operation.ValidationCursor < operation.SourceMappings.Length)
                    {
                        var mapping = operation.SourceMappings[operation.ValidationCursor++];
                        if (mapping.Key == Guid.Empty || Entity.IsDestroyed(mapping.Value) ||
                            !operation.SourceByEntity.TryAdd(mapping.Value, mapping.Key))
                            throw new NetworkProtocolException(
                                $"Scope {operation.Scope} content contains an invalid source mapping.");
                        return true;
                    }
                    operation.ValidationStage = ClientBaselineValidationStage.ContentEntities;
                    operation.ValidationCursor = 0;
                    continue;

                case ClientBaselineValidationStage.ContentEntities:
                    if (operation.ValidationCursor < operation.ContentEntities.Length)
                    {
                        var entity = operation.ContentEntities[operation.ValidationCursor++];
                        if (Entity.IsDestroyed(entity))
                            return true;
                        var marker = entity.GetComponent<NetworkObject>();
                        if (marker is null)
                            return true;
                        if (!Enum.IsDefined(marker.Presence))
                            throw new NetworkProtocolException(
                                $"Entity '{entity.Name}' has unsupported network presence {marker.Presence}.");
                        if (marker.Presence != NetworkPresence.Replicated)
                            return true;
                        if (!operation.SourceByEntity.TryGetValue(entity, out var source) ||
                            !operation.AuthoredBySource.ContainsKey(source))
                            throw new NetworkProtocolException(
                                $"Client Entity '{entity.Name}' has no authored binding in scope {operation.Scope}.");
                        operation.ConsumedAuthoredSources.Add(source);
                        _replication.ValidateEntityShape(entity);
                        return true;
                    }
                    foreach (var source in operation.AuthoredBySource.Keys)
                        if (!operation.ConsumedAuthoredSources.Contains(source))
                            throw new NetworkProtocolException(
                                $"Binding '{source}' has no replicated Entity in scope {operation.Scope}.");
                    operation.ValidationStage = ClientBaselineValidationStage.Complete;
                    SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.BindingAuthoredEntities);
                    return true;

                case ClientBaselineValidationStage.Complete:
                    SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.BindingAuthoredEntities);
                    continue;

                default:
                    throw new NetworkProtocolException("Unknown client baseline validation stage.");
            }
        }
    }

    private bool BindClientAuthoredItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline!;
        if (operation.BindingCursor < operation.ContentEntities.Length)
        {
            var entity = operation.ContentEntities[operation.BindingCursor++];
            if (Entity.IsDestroyed(entity) || entity.GetComponent<NetworkObject>() is null)
                return true;
            if (!operation.SourceByEntity.TryGetValue(entity, out var source))
            {
                if (entity.GetComponent<NetworkObject>()!.Presence == NetworkPresence.Replicated)
                    throw new NetworkProtocolException(
                        $"Replicated Entity '{entity.Name}' has no source GUID in scope {operation.Scope}.");
                source = entity.Id;
            }
            operation.AuthoredBySource.TryGetValue(source, out var binding);
            operation.World.ApplyClientAuthoredEntity(
                operation.Scope,
                entity,
                source,
                operation.AuthoredBySource.ContainsKey(source) ? binding : null);
            return true;
        }
        if (!operation.AuthoredBindingsCommitted)
        {
            operation.World.CommitClientAuthoredScope(operation.Scope, baseline.Authored);
            operation.AuthoredBindingsCommitted = true;
            SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.CreatingDynamicEntities);
            return true;
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.CreatingDynamicEntities);
        return false;
    }

    private bool CreateClientDynamicEntityItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline!;
        if (operation.DynamicCursor < baseline.Dynamic.Count)
        {
            MaterializeDynamicSpawn(baseline.Dynamic[operation.DynamicCursor++]);
            return true;
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.ApplyingPlayerMappings);
        return true;
    }

    private bool ApplyClientPlayerMappingItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline!;
        if (operation.PlayerCursor < baseline.Players.Count)
        {
            var player = baseline.Players[operation.PlayerCursor++];
            if (!operation.World.TryGetRecord(player.Value, out var record) ||
                record is null || record.Scope != operation.Scope)
                throw new NetworkProtocolException(
                    $"Baseline player {player.Key} references Entity {player.Value} outside scope {operation.Scope}.");
            operation.StagedPlayerMappings.Add(player.Key, player.Value);
            operation.PreviousPlayerMappings.Add(
                player.Key,
                operation.World.PlayerEntities.TryGetValue(player.Key, out var previous) ? previous : null);
            return true;
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.ApplyingComponentStates);
        return true;
    }

    private bool ApplyClientComponentStateItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline!;
        if (operation.ComponentCursor < baseline.Components.Count)
        {
            var state = baseline.Components[operation.ComponentCursor++];
            if (!operation.World.TryGetRecord(state.EntityId, out var record) || record is null ||
                record.Scope != operation.Scope ||
                !operation.World.TryGetReplicationBinding(
                    state.EntityId,
                    state.ComponentId,
                    out var binding) || binding is null)
                throw new NetworkProtocolException(
                    $"Baseline references missing Component {state.ComponentId} on Entity {state.EntityId}.");
            try
            {
                binding.Apply(state.Payload);
                binding.Component.NetworkStateApplied(new NetworkStateAppliedContext(
                    state.EntityId,
                    state.ComponentId,
                    NetworkStateApplyKind.InitialBaseline,
                    baseline.SceneEpoch,
                    baseline.ServerTick)
                {
                    Scope = baseline.Scope
                });
            }
            catch (Exception exception) when (exception is not NetworkProtocolException)
            {
                throw new NetworkProtocolException(
                    $"Could not apply Component {state.ComponentId} on Entity {state.EntityId}: " +
                    exception.Message,
                    exception);
            }
            var key = (state.EntityId, state.ComponentId);
            operation.StagedStateSequences.Add(key, baseline.StateSequence);
            var sessionKey = new ReplicationStateKey(state.EntityId, state.ComponentId);
            operation.PreviousStateSequences.Add(
                key,
                _lastStateSequences.TryGetValue(sessionKey, out var previous) ? previous : null);
            return true;
        }
        operation.SpawnReadyRecords = operation.World.GetRecords(operation.Scope).ToArray();
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.NotifyingSpawnReady);
        return true;
    }

    private bool NotifyClientSpawnReadyItem(ClientNetworkScopeLoadOperation operation)
    {
        var baseline = operation.Baseline!;
        if (operation.SpawnReadyCursor < operation.SpawnReadyRecords.Length)
        {
            NotifyNetworkSpawnReady(
                operation.SpawnReadyRecords[operation.SpawnReadyCursor++],
                baseline.SceneEpoch,
                baseline.ServerTick);
            return true;
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.Committing);
        return true;
    }

    private bool CommitClientScopeLoad(ClientNetworkScopeLoadOperation operation)
    {
        ValidateClientScopeLoadLifetime(operation);
        if (operation.CancelRequested)
            throw new OperationCanceledException(operation.Diagnostic);
        var baseline = operation.Baseline!;
        if (!operation.Scope.IsGlobal &&
            baseline.StructuralRevision.Value != StructuralRevision.Value + 1)
            throw new NetworkProtocolException(
                $"Scope {operation.Scope} baseline revision {baseline.StructuralRevision} cannot commit " +
                $"after {StructuralRevision}.");
        operation.CommitStarted = true;
        operation.World.CommitScope(operation.Scope);
        foreach (var mapping in operation.StagedPlayerMappings)
            operation.World.SetPlayerEntity(mapping.Key, mapping.Value);
        foreach (var state in operation.StagedStateSequences)
            _lastStateSequences[new ReplicationStateKey(state.Key.Entity, state.Key.Component)] = state.Value;
        StructuralRevision = baseline.StructuralRevision;
        ServerTick = baseline.ServerTick;

        if (!_scopes.TryGetValue(operation.Scope, out var scope))
            throw new NetworkProtocolException($"Scope {operation.Scope} disappeared before commit.");
        scope.IsReady = true;
        RestoreScopeEntities(operation.Scope);

        if (!operation.ScopeReadySent)
        {
            operation.ScopeReadySent = true;
            if (operation.Scope.IsGlobal)
            {
                _clientBaseline = null;
                _clientSceneReady = true;
                var peer = GetServerPeer();
                peer.Phase = NetworkConnectionPhase.Ready;
                SendPacket(peer.Connection, NetworkProtocolMessage.Ready, null,
                    SceneEpoch, ServerTick, StructuralRevision);
            }
            else
            {
                _clientScopeBaselines.Remove(operation.Scope);
                SendPacket(_serverConnection, NetworkProtocolMessage.ScopeReady,
                    writer =>
                    {
                        writer.WriteUInt32(operation.Scope.Value);
                        writer.WriteUInt64(baseline.StructuralRevision.Value);
                    }, SceneEpoch, ServerTick, baseline.StructuralRevision);
            }
        }
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.Ready);
        return true;
    }

    private NetworkPeer GetServerPeer()
    {
        if (!_serverConnection.IsValid ||
            !_peersByConnection.TryGetValue(_serverConnection, out var peer))
            throw new NetworkProtocolException("The server peer disappeared during client synchronization.");
        return peer;
    }

    private void SetClientScopeLoadPhase(
        ClientNetworkScopeLoadOperation operation,
        NetworkScopeLoadPhase phase,
        string? diagnostic = null)
    {
        if (operation.Phase == phase && diagnostic == operation.Diagnostic)
            return;
        operation.Phase = phase;
        operation.Diagnostic = diagnostic;
        if (_advancingClientScopeLoadItem && operation.IsTerminal)
            return;
        PublishScopeLoadStatus(operation, true);
    }

    private void PublishScopeLoadStatus(ClientNetworkScopeLoadOperation operation, bool force)
    {
        if (!force && operation.LastProgressPublishedFrame == _clientScopeLoadFrame)
            return;
        operation.LastProgressPublishedFrame = _clientScopeLoadFrame;
        var status = new NetworkScopeLoadStatus(
            operation.SceneEpoch,
            operation.Scope,
            operation.SourceAssetId,
            operation.SourceAssetName,
            operation.Phase,
            operation.CompletedItems,
            operation.TotalItems,
            operation.Elapsed.Elapsed,
            operation.Diagnostic,
            operation.LastWorkItemDuration,
            operation.MaximumWorkItemDuration,
            operation.LoadingContentDuration,
            operation.BaselineValidationDuration,
            operation.AuthoredBindingDuration,
            operation.DynamicMaterializationDuration,
            operation.PlayerMappingDuration,
            operation.ComponentStateApplicationDuration,
            operation.SpawnReadyNotificationDuration,
            operation.CommitDuration,
            _deferredClientStructuralEvents.Count,
            _deferredClientStructuralBytes,
            operation.WorkItemsAdvancedLastFrame);
        _scopeLoadStatuses[operation.Scope] = status;
        var handlers = ScopeLoadStatusChanged;
        if (handlers is null)
            return;
        foreach (Action<NetworkScopeLoadStatus> handler in handlers.GetInvocationList())
            try
            {
                handler(status);
            }
            catch (Exception exception)
            {
                Core.Logger.Error("Network scope-load status callback failed: {0}", exception);
        }
    }

    private static void RecordClientScopeLoadPhaseDuration(
        ClientNetworkScopeLoadOperation operation,
        NetworkScopeLoadPhase phase,
        TimeSpan duration)
    {
        switch (phase)
        {
            case NetworkScopeLoadPhase.LoadingContent:
                operation.LoadingContentDuration += duration;
                break;
            case NetworkScopeLoadPhase.ValidatingBaseline:
                operation.BaselineValidationDuration += duration;
                break;
            case NetworkScopeLoadPhase.BindingAuthoredEntities:
                operation.AuthoredBindingDuration += duration;
                break;
            case NetworkScopeLoadPhase.CreatingDynamicEntities:
                operation.DynamicMaterializationDuration += duration;
                break;
            case NetworkScopeLoadPhase.ApplyingPlayerMappings:
                operation.PlayerMappingDuration += duration;
                break;
            case NetworkScopeLoadPhase.ApplyingComponentStates:
                operation.ComponentStateApplicationDuration += duration;
                break;
            case NetworkScopeLoadPhase.NotifyingSpawnReady:
                operation.SpawnReadyNotificationDuration += duration;
                break;
            case NetworkScopeLoadPhase.Committing:
                operation.CommitDuration += duration;
                break;
        }
    }

    private void CancelClientScopeLoad(ClientNetworkScopeLoadOperation operation)
    {
        if (operation.IsTerminal)
            return;
        var diagnostic = operation.Diagnostic ?? $"Scope {operation.Scope} loading was cancelled.";
        var cancelledBaseline = operation.Baseline;
        RollbackClientScopeLoadOperation(operation, out var cleanupDiagnostic);
        if (operation.ConsumeBaselineRevisionOnCancel && cancelledBaseline is { } baseline &&
            baseline.StructuralRevision.Value > StructuralRevision.Value)
        {
            StructuralRevision = baseline.StructuralRevision;
            if (baseline.ServerTick > ServerTick)
                ServerTick = baseline.ServerTick;
        }
        if (cleanupDiagnostic is not null)
            diagnostic = $"{diagnostic} Cleanup also reported: {cleanupDiagnostic}";
        SetClientScopeLoadPhase(operation, NetworkScopeLoadPhase.Cancelled, diagnostic);
    }

    private void FailClientScopeLoad(
        ClientNetworkScopeLoadOperation operation,
        Exception exception,
        bool cancelled)
    {
        if (operation.IsTerminal)
            return;
        RollbackClientScopeLoadOperation(operation, out var cleanupDiagnostic);
        var diagnostic = exception.Message;
        if (cleanupDiagnostic is not null)
            diagnostic = $"{diagnostic} Cleanup also reported: {cleanupDiagnostic}";
        SetClientScopeLoadPhase(
            operation,
            cancelled || exception is OperationCanceledException
                ? NetworkScopeLoadPhase.Cancelled
                : NetworkScopeLoadPhase.Failed,
            diagnostic);
    }

    private void RollbackClientScopeLoadOperation(
        ClientNetworkScopeLoadOperation operation,
        out string? cleanupDiagnostic)
    {
        var cleanupErrors = new List<Exception>();
        cleanupDiagnostic = null;
        if (operation.CommitStarted && ReferenceEquals(World, operation.World))
        {
            StructuralRevision = operation.PreviousStructuralRevision;
            ServerTick = operation.PreviousServerTick;
            foreach (var state in operation.PreviousStateSequences)
            {
                var key = new ReplicationStateKey(state.Key.Entity, state.Key.Component);
                if (state.Value is { } previous)
                    _lastStateSequences[key] = previous;
                else
                    _lastStateSequences.Remove(key);
            }
            foreach (var mapping in operation.PreviousPlayerMappings)
                try
                {
                    if (mapping.Value is { } previous &&
                        operation.World.TryGetRecord(previous, out _))
                        operation.World.SetPlayerEntity(mapping.Key, previous);
                    else
                        operation.World.RemovePlayerEntity(mapping.Key);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
        }

        try
        {
            if (operation.Scope.IsGlobal)
                RollbackClientGlobalBaseline(operation);
            else
                RollbackClientScope(operation.Scope);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception);
        }
        operation.Baseline = null;
        if (cleanupErrors.Count != 0)
            cleanupDiagnostic = new AggregateException(cleanupErrors).Message;
    }

    private void RollbackClientGlobalBaseline(ClientNetworkScopeLoadOperation operation)
    {
        _clientBaseline = null;
        _clientSceneReady = false;
        if (!ReferenceEquals(World, operation.World))
            return;
        foreach (var record in operation.World.GetRecords(NetworkReplicationScopeId.Global).ToArray())
        {
            if (record.Origin == NetworkSpawnOrigin.DynamicBlueprint)
                operation.World.DespawnLocal(record.Id, false);
            else
                operation.World.Unregister(record.Id, false);
        }
        operation.World.MarkScopePending(NetworkReplicationScopeId.Global);
    }

    private void CancelAllClientScopeLoads(string diagnostic)
    {
        foreach (var operation in _clientScopeLoads.Values.ToArray())
        {
            if (operation.IsTerminal)
                continue;
            operation.Diagnostic = diagnostic;
            operation.CancelRequested = true;
            FailClientScopeLoad(operation, new OperationCanceledException(diagnostic), true);
        }
        _deferredClientStructuralEvents.Clear();
        _deferredClientStructuralBytes = 0;
    }

    private void QuarantineConnectionWork(
        TransportConnectionId connection,
        string? diagnostic = null)
    {
        if (!connection.IsValid)
            return;
        if (!IsServer && connection == _serverConnection)
            CancelAllClientScopeLoads(
                diagnostic ?? "The server connection was rejected or disconnected.");

        if (_transportEvents.Count != 0)
        {
            var retained = new Queue<QueuedTransportEvent>(_transportEvents.Count);
            while (_transportEvents.TryDequeue(out var item))
                if (item.Connection != connection)
                    retained.Enqueue(item);
            while (retained.TryDequeue(out var item))
                _transportEvents.Enqueue(item);
        }

        if (_deferredClientStructuralEvents.Count != 0)
        {
            var retained = new Queue<QueuedTransportEvent>(_deferredClientStructuralEvents.Count);
            _deferredClientStructuralBytes = 0;
            while (_deferredClientStructuralEvents.TryDequeue(out var item))
                if (item.Connection != connection)
                {
                    retained.Enqueue(item);
                    _deferredClientStructuralBytes += item.Payload.Length;
                }
            while (retained.TryDequeue(out var item))
                _deferredClientStructuralEvents.Enqueue(item);
        }
    }

    private void RemoveClientScopeLoadTracking(NetworkReplicationScopeId scope)
    {
        _clientScopeLoads.Remove(scope);
        var index = _clientScopeLoadOrder.IndexOf(scope);
        if (index >= 0)
        {
            _clientScopeLoadOrder.RemoveAt(index);
            if (index < _clientScopeLoadRoundRobinIndex)
                _clientScopeLoadRoundRobinIndex--;
            if (_clientScopeLoadRoundRobinIndex < 0 ||
                _clientScopeLoadRoundRobinIndex >= _clientScopeLoadOrder.Count)
                _clientScopeLoadRoundRobinIndex = 0;
        }
        _scopeLoadStatuses.Remove(scope);
    }

    private void SuspendScopeEntities(
        NetworkReplicationScopeId scope,
        IEnumerable<Entity> entities)
    {
        var states = new List<SuspendedEntityState>();
        foreach (var entity in entities)
        {
            if (Entity.IsDestroyed(entity))
                continue;
            states.Add(new SuspendedEntityState(entity, entity.LocallyEnabled, entity.UpdatesSuspended));
            entity.UpdatesSuspended = true;
            entity.Enabled = false;
        }
        _suspendedScopeEntities.Add(scope, states);
    }

    private void SuspendScopeEntityHierarchy(NetworkReplicationScopeId scope, Entity root)
    {
        if (!_suspendedScopeEntities.TryGetValue(scope, out var states))
        {
            states = [];
            _suspendedScopeEntities.Add(scope, states);
        }
        Suspend(root);
        return;

        void Suspend(Entity entity)
        {
            if (Entity.IsDestroyed(entity))
                return;
            states.Add(new SuspendedEntityState(entity, entity.LocallyEnabled, entity.UpdatesSuspended));
            entity.UpdatesSuspended = true;
            entity.Enabled = false;
            foreach (var child in entity.Children)
                Suspend(child);
        }
    }

    private void RestoreScopeEntities(NetworkReplicationScopeId scope)
    {
        if (!_suspendedScopeEntities.Remove(scope, out var states))
            return;
        foreach (var state in states)
        {
            if (Entity.IsDestroyed(state.Entity))
                continue;
            state.Entity.Enabled = state.Enabled;
            state.Entity.UpdatesSuspended = state.UpdatesSuspended;
        }
    }

    private void RollbackClientScope(NetworkReplicationScopeId scopeId)
    {
        var cleanupErrors = new List<Exception>();
        _clientScopeBaselines.Remove(scopeId);
        _suspendedScopeEntities.Remove(scopeId);
        SceneContentInstance? content = null;
        if (_scopes.Remove(scopeId, out var removed))
        {
            removed.IsLoaded = false;
            removed.IsReady = false;
            content = removed.Content;
        }
        else if (_clientScopeLoads.TryGetValue(scopeId, out var loadingOperation))
            content = loadingOperation.Content;
        _retiredScopes.Add(scopeId);
        if (World is not null)
        {
            try
            {
                World.UnregisterScope(scopeId, false);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
            if (content?.IsLoaded == true)
                try
                {
                    World.Scene.UnloadNetworkContent(content, _scopeCoordinator);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
        }
        if (cleanupErrors.Count != 0)
            throw new AggregateException(
                $"One or more resources failed while rolling back replication scope {scopeId}.",
                cleanupErrors);
    }

    private void ClearScopeState(bool unloadContent)
    {
        var scopes = _scopes.Values.ToArray();
        foreach (var scope in scopes)
        {
            scope.IsLoaded = false;
            scope.IsReady = false;
        }

        // Clear all observable scope/subscription state before invoking component cleanup.
        _scopes.Clear();
        _retiredScopes.Clear();
        _knownScopeSources.Clear();
        _clientScopeBaselines.Clear();
        _suspendedScopeEntities.Clear();
        _clientScopeLoads.Clear();
        _clientScopeLoadOrder.Clear();
        _scopeLoadStatuses.Clear();
        _clientScopeLoadRoundRobinIndex = 0;
        _deferredClientStructuralEvents.Clear();
        _deferredClientStructuralBytes = 0;
        foreach (var peer in _peersById.Values)
        {
            peer.ScopeSubscriptions.Clear();
            peer.ProjectedPlayerEntities.Clear();
        }

        if (!unloadContent || World is null)
            return;

        var cleanupErrors = new List<Exception>();
        foreach (var scope in scopes)
        {
            if (scope.IsGlobal)
                continue;
            try
            {
                World.UnregisterScope(scope.Id, false);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
            if (scope.Content?.IsLoaded == true)
                try
                {
                    World.Scene.UnloadNetworkContent(scope.Content, _scopeCoordinator);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
        }
        if (cleanupErrors.Count != 0)
            throw new AggregateException(
                "One or more replication scopes failed during session cleanup.",
                cleanupErrors);
    }

    private void RejectProtocolError(TransportConnectionId connection, string diagnostic)
    {
        if (!connection.IsValid)
            return;

        try
        {
            if (_peersByConnection.ContainsKey(connection))
                SendReject(connection, diagnostic);
        }
        catch (Exception)
        {
            // The peer is being disconnected anyway. Preserve the original protocol diagnostic.
        }
        Transport.Disconnect(connection, TransportDisconnectReason.ProtocolError);
    }

    private NetworkPeerId AllocatePeerId()
    {
        if (_nextPeerId == 0)
            throw new InvalidOperationException("Network peer ID space was exhausted.");
        return new NetworkPeerId(_nextPeerId++);
    }

    private NetworkReplicationScopeId AllocateScopeId()
    {
        if (_nextScopeId is 0 or 1)
            throw new InvalidOperationException("Network replication scope ID space was exhausted.");
        return new NetworkReplicationScopeId(_nextScopeId++);
    }

    private uint NextStateSequence()
    {
        _stateSequence++;
        if (_stateSequence == 0)
            _stateSequence++;
        return _stateSequence;
    }

    private static bool IsNewerSequence(uint candidate, uint previous) =>
        candidate != previous && unchecked(candidate - previous) < 0x80000000U;

    internal NetworkEntityId AllocateEntityId()
    {
        if (!IsServer)
            throw new InvalidOperationException("Only the server can allocate NetworkEntityId values.");
        if (_nextEntityId == 0)
            throw new InvalidOperationException("Network entity ID space was exhausted.");
        return new NetworkEntityId(_nextEntityId++);
    }

    internal NetworkStructuralRevision AdvanceStructuralRevision()
    {
        StructuralRevision = NextStructuralRevision(StructuralRevision);
        return StructuralRevision;
    }

    private static NetworkSceneEpoch NextSceneEpoch(NetworkSceneEpoch current)
    {
        var next = current.Value + 1;
        if (next == 0)
            throw new InvalidOperationException("Network Scene epoch space was exhausted.");
        return new NetworkSceneEpoch(next);
    }

    private static NetworkStructuralRevision NextStructuralRevision(NetworkStructuralRevision current)
    {
        var next = current.Value + 1;
        if (next == 0)
            throw new InvalidOperationException("Network structural revision space was exhausted.");
        return new NetworkStructuralRevision(next);
    }

    private readonly record struct QueuedTransportEvent(
        TransportEventKind Kind,
        TransportConnectionId Connection,
        byte[] Payload,
        NetworkDelivery Delivery,
        byte Channel,
        TransportDisconnectReason Reason,
        string? Diagnostic);

    private readonly record struct ReplicationStateKey(
        NetworkEntityId EntityId,
        ushort ComponentId);

    private readonly record struct ScopeSourceIdentity(
        AssetId AssetId,
        string? AssetName);

    private readonly record struct ClientTransformStateKey(
        NetworkPeerId PeerId,
        NetworkEntityId EntityId);

    private readonly record struct SuspendedEntityState(
        Entity Entity,
        bool Enabled,
        bool UpdatesSuspended);

    private sealed class PendingLiveSpawn
    {
        public PendingLiveSpawn(
            Entity entity,
            bool intendedEnabled,
            IReadOnlyList<NetworkReplicationBinding> bindings)
        {
            Entity = entity;
            IntendedEnabled = intendedEnabled;
            RemainingComponentIds = new HashSet<ushort>();
            foreach (var binding in bindings)
                RemainingComponentIds.Add(binding.Descriptor.Id);
        }

        public Entity Entity { get; }
        public bool IntendedEnabled { get; }
        public HashSet<ushort> RemainingComponentIds { get; }
    }

    private sealed class NetworkSynchronizationException(string message) : Exception(message);
}
