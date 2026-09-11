using System.Collections.Generic;
using Dreambit.Networking.Transport;

namespace Dreambit.Networking.Session;

internal enum NetworkConnectionPhase : byte
{
    AwaitingHello = 0,
    AwaitingWelcome = 1,
    Ready = 2,
    AwaitingSceneLoad = 3,
    Synchronizing = 4,
    Rejected = 5
}

internal enum NetworkScopeSubscriptionPhase : byte
{
    Pending = 0,
    AwaitingLoaded = 1,
    AwaitingReady = 2,
    Ready = 3,
    Unloading = 4
}

internal sealed class NetworkScopeSubscription
{
    public required NetworkReplicationScopeId Scope { get; init; }
    public NetworkScopeSubscriptionPhase Phase { get; set; }
    public NetworkStructuralRevision ManifestRevision { get; set; }
    public NetworkStructuralRevision BaselineRevision { get; set; }
    public NetworkStructuralRevision UnloadRevision { get; set; }
}

internal sealed class NetworkPeer
{
    public required TransportConnectionId Connection { get; init; }
    public NetworkPeerId PeerId { get; set; }
    public NetworkConnectionPhase Phase { get; set; }
    public bool IsLocal { get; init; }
    public string? RemoteDiagnostic { get; set; }
    public NetworkStructuralRevision ProjectedStructuralRevision { get; set; }
    public Dictionary<NetworkReplicationScopeId, NetworkScopeSubscription> ScopeSubscriptions { get; } = [];
    public Dictionary<NetworkPeerId, NetworkEntityId> ProjectedPlayerEntities { get; } = [];
}
