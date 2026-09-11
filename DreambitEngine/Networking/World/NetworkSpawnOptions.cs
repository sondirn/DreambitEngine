using Microsoft.Xna.Framework;

namespace Dreambit.Networking;

/// <summary>Server-authored runtime overrides included with a network Blueprint spawn.</summary>
public sealed class NetworkSpawnOptions
{
    /// <summary>
    /// Gets or sets the replication scope that owns this spawn. The default is the base Scene's
    /// <see cref="NetworkReplicationScopeId.Global"/> scope.
    /// </summary>
    public NetworkReplicationScopeId Scope { get; set; } = NetworkReplicationScopeId.Global;

    /// <summary>
    /// Gets or sets the peer that owns the spawned entity. Use <see cref="NetworkPeerId.None"/>
    /// for a server-owned entity.
    /// </summary>
    public NetworkPeerId Owner { get; set; }

    /// <summary>
    /// Gets or sets whether the authoritative server despawns the entity when its owning peer
    /// disconnects. This has no effect while <see cref="Owner"/> is unassigned.
    /// </summary>
    public bool DestroyWithOwner { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional enabled-state override. A <see langword="null"/> value keeps the
    /// Blueprint's authored value.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets an optional local-position override. A <see langword="null"/> value keeps the
    /// Blueprint's authored value.
    /// </summary>
    public Vector3? Position { get; set; }

    /// <summary>
    /// Gets or sets an optional local-rotation override. A <see langword="null"/> value keeps the
    /// Blueprint's authored value.
    /// </summary>
    public Vector3? Rotation { get; set; }

    /// <summary>
    /// Gets or sets an optional local-scale override. A <see langword="null"/> value keeps the
    /// Blueprint's authored value.
    /// </summary>
    public Vector3? Scale { get; set; }
}
