using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Dreambit.Networking.Direct;
using Dreambit.Networking.Messaging;
using Dreambit.Networking.Replication;
using Dreambit.Networking.Scenes;
using Dreambit.Networking.Transport;
using Dreambit.Networking.World;

namespace Dreambit.Networking;

/// <summary>
/// Core-owned networking façade. The service lives for the Core lifetime and owns at most one session.
/// </summary>
public sealed class NetworkService : IDisposable
{
    private readonly Core _core;
    private NetworkSession? _session;
    private bool _disposed;

    internal NetworkService(Core core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));

        RegisterInternalReplications();
    }

    /// <summary>
    /// Configuration copied into each session when it starts. Options can be prepared before
    /// a session and changed again after <see cref="Stop"/> for the next session.
    /// </summary>
    public NetworkOptions Options { get; } = new();

    /// <summary>
    /// Gets the persistent typed-message registry. Configure it while offline; registrations are
    /// frozen from session start until <see cref="Stop"/>.
    /// </summary>
    public NetworkMessageRegistry Messages { get; } = new();

    /// <summary>
    /// Gets the persistent Component replication registry. Configure it while offline;
    /// registrations are frozen from session start until <see cref="Stop"/>.
    /// </summary>
    public NetworkReplicationRegistry Replication { get; } = new();

    /// <summary>
    /// Gets the persistent synchronized-Scene catalog. Configure it while offline; registrations
    /// are frozen from session start until <see cref="Stop"/>.
    /// </summary>
    public NetworkSceneCatalog Scenes { get; } = new();

    /// <summary>Gets the local role, or <see cref="NetworkRole.Offline"/> when no session is active.</summary>
    public NetworkRole Role => _session?.Role ?? NetworkRole.Offline;

    /// <summary>Gets whether this process is authoritative: a dedicated server or listen host.</summary>
    public bool IsServer => Role is NetworkRole.Server or NetworkRole.Host;

    /// <summary>Gets whether this process has a client role: a remote client or listen host.</summary>
    public bool IsClient => Role is NetworkRole.Client or NetworkRole.Host;

    /// <summary>Gets whether this process is an authoritative listen host with a local peer.</summary>
    public bool IsHost => Role == NetworkRole.Host;

    /// <summary>
    /// Gets whether the local client has completed its protocol handshake. A host is connected as
    /// soon as it starts; a dedicated server listens for clients and reports <see langword="false"/>.
    /// </summary>
    public bool IsConnected => _session?.IsConnected == true;

    /// <summary>
    /// Gets the local client's peer ID. A dedicated server and an unconnected client return
    /// <see cref="NetworkPeerId.None"/>.
    /// </summary>
    public NetworkPeerId LocalPeerId => _session?.LocalPeerId ?? NetworkPeerId.None;

    /// <summary>
    /// Gets the active synchronized-scene generation, or <see cref="NetworkSceneEpoch.None"/> while
    /// the session is in a local menu/bootstrap Scene or offline.
    /// </summary>
    public NetworkSceneEpoch SceneEpoch => _session?.SceneEpoch ?? NetworkSceneEpoch.None;

    /// <summary>
    /// Gets the registered key for the current synchronized Scene, or <see langword="null"/> when
    /// the current Scene is local or no network Scene is active.
    /// </summary>
    public string? CurrentSceneKey => _session?.CurrentSceneKey;

    /// <summary>
    /// Gets the authoritative fixed-step tick. On a client this is the most recently accepted
    /// server tick; it is zero before synchronization begins.
    /// </summary>
    public ulong ServerTick => _session?.ServerTick ?? 0;

    /// <summary>Duration of the most recent inbound packet application pass.</summary>
    public TimeSpan LastApplyInboundDuration =>
        TimeSpan.FromMilliseconds(_session?.LastApplyInboundMilliseconds ?? 0);

    /// <summary>Largest inbound packet application pass observed in the active session.</summary>
    public TimeSpan MaximumApplyInboundDuration =>
        TimeSpan.FromMilliseconds(_session?.MaximumApplyInboundMilliseconds ?? 0);

    /// <summary>Wall time used by the most recent client scope-loader and replay slice.</summary>
    public TimeSpan LastClientScopeLoadSliceDuration =>
        _session?.LastClientScopeLoadSliceDuration ?? TimeSpan.Zero;

    /// <summary>Largest client scope-loader and replay slice observed in the active session.</summary>
    public TimeSpan MaximumClientScopeLoadSliceDuration =>
        _session?.MaximumClientScopeLoadSliceDuration ?? TimeSpan.Zero;

    /// <summary>Time spent replaying deferred structural packets in the most recent slice.</summary>
    public TimeSpan LastDeferredStructuralReplayDuration =>
        _session?.LastDeferredReplayDuration ?? TimeSpan.Zero;

    /// <summary>Gets whether this client has one or more scope loads that have not reached a terminal state.</summary>
    public bool HasPendingScopeLoads => _session?.HasPendingScopeLoads == true;

    /// <summary>
    /// Gets immutable snapshots for client-local scope loads retained by the active session.
    /// Terminal snapshots remain present until their scope is unloaded or the session is replaced.
    /// </summary>
    public IReadOnlyList<NetworkScopeLoadStatus> ScopeLoadStatuses =>
        _session?.GetScopeLoadStatuses() ?? [];

    /// <summary>
    /// Gets the network Entity assigned to the local peer, or <see langword="null"/> until the
    /// server publishes a player mapping in the current network world.
    /// </summary>
    public Entity? LocalPlayerEntity =>
        _session is { World: { } world, LocalPeerId.IsValid: true } session &&
        world.TryGetPlayerEntity(session.LocalPeerId, out var entity)
            ? entity
            : null;

    /// <summary>
    /// Occurs after a peer passes the protocol compatibility handshake. Scene synchronization may
    /// still be in progress. A remote client receives its own assigned peer ID through this event.
    /// </summary>
    public event Action<NetworkPeerId>? PeerConnected;

    /// <summary>Occurs after an identified peer disconnects.</summary>
    /// <remarks>
    /// The first argument is the peer ID, the second is the transport-neutral reason, and the third
    /// is optional diagnostic text intended for logging rather than gameplay decisions.
    /// </remarks>
    public event Action<NetworkPeerId, TransportDisconnectReason, string?>? PeerDisconnected;

    /// <summary>
    /// Occurs when a client fails before completing its protocol handshake. The diagnostic text is
    /// optional and intended for logging or a connection-error screen.
    /// </summary>
    public event Action<TransportDisconnectReason, string?>? ConnectionFailed;

    /// <summary>Occurs on the server when a peer has committed one subscribed additive scope.</summary>
    public event Action<NetworkPeerId, NetworkReplicationScopeId>? PeerScopeReady;

    /// <summary>Occurs on the server after a peer confirms that a scope was removed locally.</summary>
    public event Action<NetworkPeerId, NetworkReplicationScopeId>? PeerScopeUnloaded;

    /// <summary>
    /// Occurs on the game thread when client-local Dreambit scope loading changes phase or reports
    /// frame-coalesced progress. Ready does not imply game-specific player/camera presentation readiness.
    /// </summary>
    public event Action<NetworkScopeLoadStatus>? ScopeLoadStatusChanged;

    /// <summary>Gets the latest client-local loading snapshot retained for a replication scope.</summary>
    public bool TryGetScopeLoadStatus(
        NetworkReplicationScopeId scope,
        out NetworkScopeLoadStatus status)
    {
        if (_session is not null)
            return _session.TryGetScopeLoadStatus(scope, out status);
        status = default;
        return false;
    }

    /// <summary>
    /// Registers default dreambit replicated components i.e. NetworkTransform2D
    /// </summary>
    private void RegisterInternalReplications()
    {
        Replication.Register<NetworkTransform2D>();
    }

    /// <summary>
    /// Applies one or more explicit game networking modules.
    ///
    /// Configuration must occur before a networking session starts.
    /// Registrations persist for the lifetime of this NetworkService,
    /// including across Stop/start cycles.
    /// </summary>
    /// <param name="modules">
    /// The game networking modules to register.
    /// </param>
    public void Configure(
        params INetworkModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (_session is not null)
        {
            throw new InvalidOperationException(
                "Networking modules must be configured before starting a networking session.");
        }

        for (var i = 0; i < modules.Length; i++)
        {
            if (modules[i] is null)
            {
                throw new ArgumentException(
                    "Networking modules cannot contain null entries.",
                    nameof(modules));
            }
        }

        var context = new NetworkRegistrationContext(this);

        for (var i = 0; i < modules.Length; i++)
            modules[i].Register(context);
    }
    
    /// <summary>
    /// Starts an authoritative server. An existing local Scene remains local; use
    /// <see cref="ChangeScene"/> when the server is ready to enter a synchronized Scene.
    /// </summary>
    /// <param name="transport">
    /// A stopped server-configured transport. The service takes ownership and disposes it on failure
    /// or when the session stops.
    /// </param>
    public void StartServer(INetworkTransport transport) =>
        StartSession(NetworkRole.Server, transport);

    /// <summary>
    /// Starts an authoritative listen server/host. An existing local Scene remains local; use
    /// <see cref="ChangeScene"/> when the host is ready to enter a synchronized Scene.
    /// </summary>
    /// <param name="transport">
    /// A stopped server-configured transport. The service takes ownership and disposes it on failure
    /// or when the session stops.
    /// </param>
    public void StartHost(INetworkTransport transport) =>
        StartSession(NetworkRole.Host, transport);

    /// <summary>
    /// Starts a client connection. An existing local Scene remains active until the server
    /// requests a catalog-driven synchronized Scene transition.
    /// </summary>
    /// <param name="transport">
    /// A stopped client-configured transport. The service takes ownership and disposes it on failure
    /// or when the session stops.
    /// </param>
    public void Connect(INetworkTransport transport) =>
        StartSession(NetworkRole.Client, transport);

    /// <summary>Starts an authoritative dedicated server using the Direct IP transport.</summary>
    /// <param name="port">The local TCP and UDP port to bind.</param>
    /// <param name="options">Optional Direct IP transport settings.</param>
    public void StartServer(int port, DirectIpOptions? options = null) =>
        StartServer(DirectIpTransport.Listen(port, options));

    /// <summary>Starts an authoritative listen host using the Direct IP transport.</summary>
    /// <param name="port">The local TCP and UDP port to bind.</param>
    /// <param name="options">Optional Direct IP transport settings.</param>
    public void StartHost(int port, DirectIpOptions? options = null) =>
        StartHost(DirectIpTransport.Listen(port, options));

    /// <summary>Starts a client session that connects using the Direct IP transport.</summary>
    /// <param name="host">An IPv4 address or host name that resolves to IPv4.</param>
    /// <param name="port">The server's TCP and UDP port.</param>
    /// <param name="options">Optional Direct IP transport settings.</param>
    public void Connect(string host, int port, DirectIpOptions? options = null) =>
        Connect(DirectIpTransport.Connect(host, port, options));

    /// <summary>
    /// Schedules an authoritative transition to a registered synchronized Scene. Connected clients
    /// receive the same key and create the Scene through their local <see cref="Scenes"/> catalog.
    /// </summary>
    /// <param name="sceneKey">The case-sensitive key previously registered on every peer.</param>
    /// <exception cref="InvalidOperationException">
    /// The local process is not an active server/host, or another synchronized Scene change is pending.
    /// </exception>
    public void ChangeScene(string sceneKey)
    {
        if (!IsServer || _session is null)
            throw new InvalidOperationException("Only an active server can change the synchronized Scene.");
        var scene = Scenes.Create(sceneKey);
        try
        {
            _session.BeginServerSceneChange(sceneKey);
        }
        catch
        {
            scene.Terminate();
            throw;
        }
        // SetNextScene takes ownership immediately, including when terminating a displaced
        // pending Scene reports a cleanup failure.
        _core.SetNextSceneFromNetworking(scene, this);
    }

    /// <summary>Loads a Scene Blueprint into a new server-authoritative replication scope.</summary>
    public NetworkReplicationScopeId LoadScope(string sceneAssetName) =>
        _session?.LoadScope(sceneAssetName) ??
        throw new InvalidOperationException("A networking session is not active.");

    /// <summary>
    /// Unloads a server scope. All peers must first complete <see cref="Unsubscribe"/> for it.
    /// </summary>
    public void UnloadScope(NetworkReplicationScopeId scope)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.UnloadScope(scope);
    }

    /// <summary>Starts reliable materialization and baseline synchronization of a scope for a peer.</summary>
    public void Subscribe(NetworkPeerId peer, NetworkReplicationScopeId scope)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.Subscribe(peer, scope);
    }

    /// <summary>Stops scoped traffic and reliably removes the scope from a peer.</summary>
    public void Unsubscribe(NetworkPeerId peer, NetworkReplicationScopeId scope)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.Unsubscribe(peer, scope);
    }

    /// <summary>Looks up a scope in the current Scene epoch.</summary>
    public bool TryGetScope(NetworkReplicationScopeId id, out NetworkReplicationScope? scope)
    {
        if (_session is not null)
            return _session.TryGetScope(id, out scope);
        scope = null;
        return false;
    }

    /// <summary>Gets whether the server currently tracks a peer subscription.</summary>
    public bool IsPeerSubscribed(NetworkPeerId peer, NetworkReplicationScopeId scope) =>
        _session?.IsPeerSubscribed(peer, scope) == true;

    /// <summary>Gets whether a peer has acknowledged and committed a scope baseline.</summary>
    public bool IsPeerScopeReady(NetworkPeerId peer, NetworkReplicationScopeId scope) =>
        _session?.IsPeerScopeReady(peer, scope) == true;

    /// <summary>
    /// Materializes a Blueprint as a server-authoritative runtime entity and reliably reproduces it
    /// on synchronized clients with its initial replicated Component state.
    /// </summary>
    /// <param name="blueprint">
    /// A Blueprint with a stable, non-empty <see cref="DreambitAsset.AssetId"/> available to every peer.
    /// </param>
    /// <param name="options">Optional ownership and authored-value overrides.</param>
    /// <returns>The authoritative local Entity created in the current Scene.</returns>
    /// <exception cref="InvalidOperationException">
    /// The local process is not the server/host, no network Scene is active, or the Blueprint cannot
    /// be represented by the registered network contract.
    /// </exception>
    public Entity Spawn(
        EntityBlueprint blueprint,
        NetworkSpawnOptions? options = null) =>
        Spawn(blueprint, static _ => { }, options);

    /// <summary>
    /// Materializes a Blueprint as a server-authoritative runtime entity, initializes its gameplay
    /// state, and reliably reproduces it on synchronized clients with that initial replicated state.
    /// </summary>
    /// <param name="blueprint">
    /// A Blueprint with a stable, non-empty <see cref="DreambitAsset.AssetId"/> available to every peer.
    /// </param>
    /// <param name="initialize">
    /// Configures the authoritative Entity after Blueprint materialization and before its network
    /// identity is registered or initial replicated state is captured.
    /// </param>
    /// <param name="options">Optional ownership and authored-value overrides.</param>
    /// <returns>The initialized authoritative local Entity created in the current Scene.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="initialize"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The local process is not the server/host, no network Scene is active, or the Blueprint cannot
    /// be represented by the registered network contract.
    /// </exception>
    public Entity Spawn(
        EntityBlueprint blueprint,
        Action<Entity> initialize,
        NetworkSpawnOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(initialize);

        return _session?.Spawn(blueprint, initialize, options)
               ?? throw new InvalidOperationException(
                   "A networking session is not active.");
    }

    /// <summary>Authoritatively destroys a registered network entity on the server and clients.</summary>
    /// <param name="entity">The entity registered in the current network world.</param>
    /// <exception cref="InvalidOperationException">
    /// The local process is not the server/host or the Entity is not a current network entity.
    /// </exception>
    public void Despawn(Entity entity)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.Despawn(entity);
    }

    /// <summary>Sends a registered gameplay message from a client or host-local peer to the server.</summary>
    /// <typeparam name="T">The registered message type.</typeparam>
    /// <param name="message">The message value to encode.</param>
    /// <param name="delivery">The desired delivery mode.</param>
    /// <remarks>A host-local message is dispatched without transport and may invoke its handler synchronously.</remarks>
    public void SendToServer<T>(
        T message,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.SendToServer(message, delivery);
    }

    /// <summary>Sends a registered gameplay message from the server to one ready peer.</summary>
    /// <typeparam name="T">The registered message type.</typeparam>
    /// <param name="peer">The destination peer.</param>
    /// <param name="message">The message value to encode.</param>
    /// <param name="delivery">The desired delivery mode.</param>
    /// <remarks>Sending to the host's local peer dispatches without transport and may invoke its handler synchronously.</remarks>
    public void Send<T>(
        NetworkPeerId peer,
        T message,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.Send(peer, message, delivery);
    }

    /// <summary>Looks up the current network identity assigned to a local Entity.</summary>
    /// <param name="entity">The local Entity to find.</param>
    /// <param name="id">
    /// Receives the network identity, or <see cref="NetworkEntityId.None"/> when not found.
    /// </param>
    /// <returns><see langword="true"/> when the Entity is registered in the current network world.</returns>
    public bool TryGetNetworkId(Entity entity, out NetworkEntityId id)
    {
        if (_session?.World is { } world)
            return world.TryGetNetworkId(entity, out id);
        id = NetworkEntityId.None;
        return false;
    }

    /// <summary>
    /// Looks up the replication scope that owns an already-held network Entity. Scope metadata
    /// is available during baseline initialization so callbacks can resolve scoped content.
    /// Success does not imply that the scope is ready or that the Entity is publicly available.
    /// </summary>
    public bool TryGetReplicationScope(Entity entity, out NetworkReplicationScopeId scope)
    {
        if (_session?.World is { } world)
            return world.TryGetScope(entity, out scope);
        scope = NetworkReplicationScopeId.None;
        return false;
    }

    /// <summary>Resolves an authored source GUID inside one replication scope.</summary>
    public bool TryGetAuthoredEntity(
        NetworkReplicationScopeId scope,
        Guid sourceGuid,
        out Entity? entity)
    {
        if (_session?.World is { } world)
            return world.TryGetAuthoredEntity(scope, sourceGuid, out entity);
        entity = null;
        return false;
    }

    /// <summary>Looks up the local Entity for an ID in the current network world.</summary>
    /// <param name="id">The current-scene network entity ID.</param>
    /// <param name="entity">Receives the local Entity, or <see langword="null"/> when not found.</param>
    /// <returns><see langword="true"/> when the ID is registered in the current network world.</returns>
    public bool TryGetEntity(NetworkEntityId id, out Entity? entity)
    {
        if (_session?.World is { } world)
            return world.TryGetEntity(id, out entity);
        entity = null;
        return false;
    }

    /// <summary>Resolves a scene-safe network entity reference in the current network world.</summary>
    /// <param name="reference">The reference containing both scene epoch and entity ID.</param>
    /// <param name="entity">Receives the local Entity, or <see langword="null"/> when unresolved.</param>
    /// <returns>
    /// <see langword="true"/> only when the reference's epoch matches the current synchronized Scene
    /// and its entity ID is registered.
    /// </returns>
    public bool TryResolve(NetworkEntityRef reference, out Entity? entity)
    {
        if (_session?.World is { } world)
            return world.TryResolve(reference, out entity);
        entity = null;
        return false;
    }

    /// <summary>Determines whether the current local peer owns a network Entity.</summary>
    /// <param name="entity">The local Entity to test.</param>
    /// <returns>
    /// <see langword="true"/> when the Entity is registered and owned by this host/client's peer ID.
    /// Dedicated servers and unconnected clients return <see langword="false"/>.
    /// </returns>
    public bool IsOwnedByLocalPeer(Entity entity) =>
        _session is { World: { } world, LocalPeerId.IsValid: true } session &&
        world.IsOwnedBy(session.LocalPeerId, entity);

    /// <summary>
    /// Assigns a network Entity as a peer's player Entity and reliably publishes that mapping.
    /// </summary>
    /// <param name="peer">The peer receiving the player mapping.</param>
    /// <param name="entity">An Entity registered in the current network world.</param>
    /// <exception cref="InvalidOperationException">The local process is not an active server/host.</exception>
    public void SetPlayerEntity(NetworkPeerId peer, Entity entity)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.SetPlayerEntity(peer, entity);
    }

    /// <summary>Changes a network Entity's ownership and reliably publishes the change.</summary>
    /// <param name="entity">An Entity registered in the current network world.</param>
    /// <param name="owner">
    /// The new owning peer, or <see cref="NetworkPeerId.None"/> for server ownership.
    /// </param>
    /// <exception cref="InvalidOperationException">The local process is not an active server/host.</exception>
    public void SetOwner(Entity entity, NetworkPeerId owner)
    {
        if (_session is null)
            throw new InvalidOperationException("A networking session is not active.");
        _session.SetOwner(entity, owner);
    }

    /// <summary>
    /// Ends the active session, disposes its transport, clears runtime network identity, destroys
    /// dynamic network spawns, and unfreezes the registries for later configuration and restart.
    /// Calling this method while offline has no effect.
    /// </summary>
    public void Stop()
    {
        var session = _session;
        _session = null;
        if (session is null)
            return;

        try
        {
            session.Dispose();
        }
        finally
        {
            Messages.Unfreeze();
            Replication.Unfreeze();
            Scenes.Unfreeze();
        }
    }

    internal NetworkSession? ActiveSession => _session;
    internal void PollTransport() => _session?.PollTransport();
    internal void ApplyInbound() => _session?.ApplyInbound();
    internal void AdvanceClientScopeLoads() => _session?.AdvanceClientScopeLoads();
    internal void BeforeFixedStep(Scene scene) => _session?.BeforeFixedStep(scene);
    internal void AfterFixedStep(Scene scene) => _session?.AfterFixedStep(scene);
    internal void AfterSceneTick(Scene scene) => _session?.AfterSceneTick(scene);
    internal void BeforeSceneUnload(Scene scene) => _session?.BeforeSceneUnload(scene);
    internal void AfterSceneAssigned(Scene scene)
    {
        // A session can begin while a local menu/bootstrap Scene is already active, and a
        // local Scene may already be pending when it begins. Only a catalog-driven network
        // transition is allowed to create a NetworkWorld and consume a network Scene epoch.
        if (_session?.HasPendingSynchronizedScene == true)
            _session.AfterSceneAssigned(scene);
    }

    internal void StopIntake() => _session?.StopIntake();

    /// <summary>
    /// Permanently disposes this Core-owned service and its active session. Normal games should use
    /// <see cref="Stop"/> to return offline and let <see cref="Core"/> dispose the service at shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void StartSession(NetworkRole role, INetworkTransport transport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);
        if (_session is not null)
            throw new InvalidOperationException("A networking session is already active.");

        NetworkSession? session = null;
        var registriesFrozen = false;
        try
        {
            var defaultContentFingerprint = Options.ContentFingerprint is null
                ? Resources.ContentFingerprint
                : null;
            var sessionOptions = Options.Snapshot(defaultContentFingerprint);
            sessionOptions.Validate();
            Messages.Freeze();
            Replication.Freeze();
            Scenes.Freeze();
            registriesFrozen = true;
            session = new NetworkSession(role, transport, sessionOptions, Messages, Replication);
            session.PeerConnected += peer => PeerConnected?.Invoke(peer);
            session.PeerDisconnected += (peer, reason, diagnostic) =>
                PeerDisconnected?.Invoke(peer, reason, diagnostic);
            session.ConnectionFailed += (reason, diagnostic) =>
                ConnectionFailed?.Invoke(reason, diagnostic);
            session.PeerScopeReady += (peer, scope) => PeerScopeReady?.Invoke(peer, scope);
            session.PeerScopeUnloaded += (peer, scope) => PeerScopeUnloaded?.Invoke(peer, scope);
            session.ScopeLoadStatusChanged += PublishScopeLoadStatus;
            session.SceneChangeRequested += HandleSceneChangeRequested;
            session.Start();
            _session = session;
        }
        catch
        {
            if (session is not null)
                session.Dispose();
            else
                transport.Dispose();
            if (registriesFrozen)
            {
                Messages.Unfreeze();
                Replication.Unfreeze();
                Scenes.Unfreeze();
            }
            throw;
        }
    }

    private void HandleSceneChangeRequested(string sceneKey, NetworkSceneEpoch sceneEpoch)
    {
        var scene = Scenes.Create(sceneKey);
        _core.SetNextSceneFromNetworking(scene, this);
    }

    private void PublishScopeLoadStatus(NetworkScopeLoadStatus status)
    {
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
                Core.Logger.Error("Network scope-load status subscriber failed: {0}", exception);
            }
    }
}
