using System.Collections.Generic;
using Dreambit.Networking.World;
using Microsoft.Xna.Framework;

namespace Dreambit.Networking.Session;

internal enum NetworkBaselineRecordKind : byte
{
    Begin = 1,
    AuthoredEntity = 2,
    DynamicEntity = 3,
    PlayerEntity = 4,
    ComponentState = 5,
    End = 6
}

internal readonly record struct NetworkDynamicSpawnRecord(
    NetworkReplicationScopeId Scope,
    NetworkEntityId EntityId,
    AssetId BlueprintAssetId,
    string? BlueprintAssetName,
    NetworkPeerId Owner,
    bool DestroyWithOwner,
    bool Enabled,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale);

internal readonly record struct NetworkComponentStateRecord(
    NetworkEntityId EntityId,
    ushort ComponentId,
    byte[] Payload);

internal sealed class ClientBaselineState
{
    public required NetworkReplicationScopeId Scope { get; init; }
    public required NetworkSceneEpoch SceneEpoch { get; init; }
    public required NetworkStructuralRevision StructuralRevision { get; init; }
    public required ulong ServerTick { get; init; }
    public required uint StateSequence { get; init; }
    public required int ExpectedAuthored { get; init; }
    public required int ExpectedDynamic { get; init; }
    public required int ExpectedPlayers { get; init; }
    public required int ExpectedComponents { get; init; }
    public List<NetworkAuthoredBinding> Authored { get; } = [];
    public List<NetworkDynamicSpawnRecord> Dynamic { get; } = [];
    public List<KeyValuePair<NetworkPeerId, NetworkEntityId>> Players { get; } = [];
    public List<NetworkComponentStateRecord> Components { get; } = [];
}
