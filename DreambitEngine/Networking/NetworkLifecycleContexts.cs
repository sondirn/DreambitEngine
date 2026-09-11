namespace Dreambit.Networking;

/// <summary>
/// Describes why authoritative replicated state was applied to a client Component.
/// </summary>
public enum NetworkStateApplyKind : byte
{
    /// <summary>
    /// Initial state for an Entity created by a live server spawn in an already synchronized Scene.
    /// </summary>
    InitialSpawn = 0,

    /// <summary>
    /// Initial state restored while a client applies the authoritative Scene baseline.
    /// </summary>
    InitialBaseline = 1,

    /// <summary>Ordinary authoritative state received after the Entity became network-ready.</summary>
    Snapshot = 2
}

/// <summary>
/// Information available when a network Entity has received all of its initial authoritative state.
/// </summary>
/// <param name="EntityId">The Entity's identity in the current synchronized Scene.</param>
/// <param name="Owner">The peer associated with the Entity, or <see cref="NetworkPeerId.None"/>.</param>
/// <param name="LocalRole">The networking role executing the callback.</param>
/// <param name="SceneEpoch">The synchronized Scene generation containing the Entity.</param>
/// <param name="ServerTick">The authoritative server tick associated with readiness.</param>
public readonly record struct NetworkSpawnReadyContext(
    NetworkEntityId EntityId,
    NetworkPeerId Owner,
    NetworkRole LocalRole,
    NetworkSceneEpoch SceneEpoch,
    ulong ServerTick)
{
    /// <summary>Gets the replication scope that owns this Entity.</summary>
    public NetworkReplicationScopeId Scope { get; init; } = NetworkReplicationScopeId.Global;
}

/// <summary>
/// Information available after one complete authoritative Component payload is applied locally.
/// </summary>
/// <param name="EntityId">The network identity of the Component's Entity.</param>
/// <param name="ComponentId">The registered protocol ID of the replicated Component.</param>
/// <param name="Kind">Whether the payload belongs to initial synchronization or a later snapshot.</param>
/// <param name="SceneEpoch">The synchronized Scene generation carried by the payload.</param>
/// <param name="ServerTick">The authoritative server tick carried by the payload.</param>
public readonly record struct NetworkStateAppliedContext(
    NetworkEntityId EntityId,
    ushort ComponentId,
    NetworkStateApplyKind Kind,
    NetworkSceneEpoch SceneEpoch,
    ulong ServerTick)
{
    /// <summary>Gets the replication scope that owns this Entity.</summary>
    public NetworkReplicationScopeId Scope { get; init; } = NetworkReplicationScopeId.Global;

    /// <summary>
    /// Gets whether this payload is part of the Entity's initial network state rather than a later
    /// snapshot.
    /// </summary>
    public bool IsInitial => Kind is
        NetworkStateApplyKind.InitialSpawn or NetworkStateApplyKind.InitialBaseline;
}
