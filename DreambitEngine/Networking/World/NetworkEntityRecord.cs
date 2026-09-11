using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Dreambit.Networking.Replication;

namespace Dreambit.Networking.World;

internal enum NetworkSpawnOrigin : byte
{
    AuthoredScene = 0,
    DynamicBlueprint = 1
}

internal sealed class NetworkEntityRecord
{
    public required NetworkEntityId Id { get; init; }
    public required Entity Entity { get; init; }
    public required NetworkObject Marker { get; init; }
    public required NetworkSpawnOrigin Origin { get; init; }
    public required NetworkReplicationScopeId Scope { get; init; }
    public NetworkPeerId Owner { get; set; }
    public Guid SourceGuid { get; init; }
    public AssetId BlueprintAssetId { get; init; }
    public string? BlueprintAssetName { get; init; }
    public bool DestroyWithOwner { get; init; } = true;
    public IReadOnlyList<NetworkReplicationBinding> ReplicationBindings { get; init; } = [];
    public bool SpawnReadyNotified { get; set; }
}
