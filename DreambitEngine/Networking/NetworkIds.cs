using System;

namespace Dreambit.Networking;

/// <summary>Identifies one protocol peer within a single network session.</summary>
/// <param name="Value">
/// The session-local numeric identifier. Zero is reserved for <see cref="None"/>.
/// </param>
public readonly record struct NetworkPeerId(uint Value)
{
    /// <summary>Gets the invalid identifier used when no peer is assigned.</summary>
    public static NetworkPeerId None => default;

    /// <summary>Gets whether this identifier refers to an assigned peer.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Returns the numeric peer identifier as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies an entity within one synchronized scene epoch. This identity is separate from
/// Dreambit's serialized source GUIDs and local runtime <see cref="ECS.Entity"/> instances.
/// </summary>
/// <param name="Value">
/// The session-assigned numeric identifier. Zero is reserved for <see cref="None"/>.
/// </param>
public readonly record struct NetworkEntityId(ulong Value)
{
    /// <summary>Gets the invalid identifier used when no network entity is assigned.</summary>
    public static NetworkEntityId None => default;

    /// <summary>Gets whether this identifier refers to an assigned network entity.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Returns the numeric network entity identifier as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies one synchronized scene generation within a session. The epoch changes whenever
/// the server enters another network scene so stale entity references and packets can be rejected.
/// </summary>
/// <param name="Value">The scene generation. Zero represents no synchronized scene.</param>
public readonly record struct NetworkSceneEpoch(uint Value)
{
    /// <summary>Gets the value used when no synchronized scene is active.</summary>
    public static NetworkSceneEpoch None => default;

    /// <summary>Gets whether this value identifies a synchronized scene generation.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Returns the numeric scene epoch as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies structural state within one Scene epoch. On the server, the session property tracks
/// authoritative world changes. In packets and on clients, the value tracks the receiving peer's
/// projected structural stream so scope-filtered events remain strictly sequential.
/// </summary>
/// <param name="Value">The monotonically increasing revision within the scene epoch.</param>
public readonly record struct NetworkStructuralRevision(ulong Value)
{
    /// <summary>Gets the initial revision, before any synchronized structural change.</summary>
    public static NetworkStructuralRevision None => default;

    /// <summary>Returns the numeric structural revision as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies one independently replicated content lifetime inside a synchronized Scene.
/// Scope identity is assigned by the server, is never reused within a Scene epoch, and is
/// deliberately separate from both source asset identity and <see cref="SceneContentInstance.InstanceId"/>.
/// </summary>
/// <param name="Value">The epoch-local numeric identity. Zero is invalid and one is global.</param>
public readonly record struct NetworkReplicationScopeId(uint Value)
{
    /// <summary>Gets the invalid value used when no scope is assigned.</summary>
    public static NetworkReplicationScopeId None => default;

    /// <summary>Gets the always-present replication scope for the base synchronized Scene.</summary>
    public static NetworkReplicationScopeId Global => new(1);

    /// <summary>Gets whether this value identifies a replication scope.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Gets whether this value identifies the base synchronized Scene.</summary>
    public bool IsGlobal => Value == Global.Value;

    /// <summary>Returns the numeric scope identifier as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A scene-safe reference to a network entity. Both parts must match the active network world
/// before the reference can resolve to a local <see cref="ECS.Entity"/>.
/// </summary>
/// <param name="SceneEpoch">The synchronized scene generation containing the entity.</param>
/// <param name="EntityId">The entity's identifier within that scene generation.</param>
public readonly record struct NetworkEntityRef(
    NetworkSceneEpoch SceneEpoch,
    NetworkEntityId EntityId)
{
    /// <summary>Gets an invalid reference that does not identify an entity.</summary>
    public static NetworkEntityRef None => default;

    /// <summary>Gets whether both the scene epoch and entity identifier are valid.</summary>
    public bool IsValid => SceneEpoch.IsValid && EntityId.IsValid;
}
