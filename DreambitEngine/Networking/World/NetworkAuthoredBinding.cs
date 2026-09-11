using System;

namespace Dreambit.Networking.World;

/// <summary>
/// Associates an authored entity's serialized source GUID with its runtime network identity
/// and owner for initial scene synchronization.
/// </summary>
/// <param name="SourceGuid">The stable entity GUID stored in the source scene or Blueprint.</param>
/// <param name="NetworkEntityId">The entity's runtime identity in the network scene.</param>
/// <param name="Owner">
/// The owning peer, or <see cref="NetworkPeerId.None"/> when the server owns the entity.
/// </param>
public readonly record struct NetworkAuthoredBinding(
    Guid SourceGuid,
    NetworkEntityId NetworkEntityId,
    NetworkPeerId Owner)
{
    /// <summary>Gets the replication scope containing the authored entity.</summary>
    public NetworkReplicationScopeId Scope { get; init; } = NetworkReplicationScopeId.Global;

    /// <summary>
    /// Gets whether the authored entity still exists authoritatively. A false value is a baseline
    /// tombstone for peers that subscribe after the authored entity was despawned.
    /// </summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>Creates an explicitly scoped authored binding.</summary>
    public NetworkAuthoredBinding(
        NetworkReplicationScopeId scope,
        Guid sourceGuid,
        NetworkEntityId networkEntityId,
        NetworkPeerId owner)
        : this(sourceGuid, networkEntityId, owner)
    {
        Scope = scope;
    }
}
