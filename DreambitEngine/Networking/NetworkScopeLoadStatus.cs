using System;

namespace Dreambit.Networking;

/// <summary>Describes the client-local progress of one network replication scope.</summary>
public enum NetworkScopeLoadPhase : byte
{
    LoadingContent = 0,
    WaitingForBaseline = 1,
    ValidatingBaseline = 2,
    BindingAuthoredEntities = 3,
    CreatingDynamicEntities = 4,
    ApplyingPlayerMappings = 5,
    ApplyingComponentStates = 6,
    NotifyingSpawnReady = 7,
    Committing = 8,
    Ready = 9,
    Failed = 10,
    Cancelled = 11
}

/// <summary>
/// Immutable client-local scope-loading state. <see cref="NetworkScopeLoadPhase.Ready"/> means
/// Dreambit has committed the scope; a game may still require presentation-specific readiness.
/// </summary>
public readonly record struct NetworkScopeLoadStatus(
    NetworkSceneEpoch SceneEpoch,
    NetworkReplicationScopeId Scope,
    AssetId SourceAssetId,
    string? SourceAssetName,
    NetworkScopeLoadPhase Phase,
    int CompletedItems,
    int? TotalItems,
    TimeSpan Elapsed,
    string? Diagnostic,
    TimeSpan LastWorkItemDuration,
    TimeSpan MaximumWorkItemDuration,
    TimeSpan LoadingContentDuration,
    TimeSpan BaselineValidationDuration,
    TimeSpan AuthoredBindingDuration,
    TimeSpan DynamicMaterializationDuration,
    TimeSpan PlayerMappingDuration,
    TimeSpan ComponentStateApplicationDuration,
    TimeSpan SpawnReadyNotificationDuration,
    TimeSpan CommitDuration,
    int DeferredStructuralPackets,
    int DeferredStructuralBytes,
    int WorkItemsAdvancedLastFrame);
