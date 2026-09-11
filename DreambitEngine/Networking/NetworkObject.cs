using System;
using Dreambit.ECS;

namespace Dreambit.Networking;

/// <summary>
/// Determines on which network roles an authored entity containing a <see cref="NetworkObject"/>
/// is materialized.
/// </summary>
public enum NetworkPresence : byte
{
    /// <summary>Exists on the authoritative server/host and synchronized remote clients.</summary>
    Replicated = 0,

    /// <summary>Exists only on the authoritative server/host.</summary>
    ServerOnly = 1,

    /// <summary>
    /// Exists only on non-authoritative remote clients. A listen server/host is authoritative,
    /// so it removes ClientOnly entities just like a dedicated server.
    /// </summary>
    ClientOnly = 2
}

/// <summary>
/// Inert authored marker for network-managed entities. Runtime identity and ownership live in
/// NetworkWorld and are deliberately not Dreambit-serialized here.
/// </summary>
public sealed class NetworkObject : Component
{
    private Action<Entity>? _destroyed;

    /// <summary>
    /// Gets or sets where this authored entity exists during a synchronized scene. This is
    /// serialized source metadata; runtime network identity and ownership are not serialized.
    /// </summary>
    [DreambitSerialize]
    public NetworkPresence Presence { get; set; } = NetworkPresence.Replicated;

    internal void BindDestroyed(Action<Entity> destroyed)
    {
        _destroyed = destroyed ?? throw new ArgumentNullException(nameof(destroyed));
    }

    internal void UnbindDestroyed()
    {
        _destroyed = null;
    }

    /// <inheritdoc />
    public override void OnDestroyed()
    {
        var entity = Entity;
        if (entity is not null)
            _destroyed?.Invoke(entity);
        _destroyed = null;
        base.OnDestroyed();
    }
}
