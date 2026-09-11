using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dreambit.ECS;
using Dreambit.Networking.World;

namespace Dreambit.Networking.Session;

internal enum ClientBaselineValidationStage : byte
{
    Counts,
    AuthoredRecords,
    DynamicRecords,
    PlayerRecords,
    ComponentRecords,
    SourceMappings,
    ContentEntities,
    Complete
}

internal sealed class ClientNetworkScopeLoadOperation
{
    public required Guid SessionId { get; init; }
    public required NetworkWorld World { get; init; }
    public required NetworkSceneEpoch SceneEpoch { get; init; }
    public required NetworkReplicationScopeId Scope { get; init; }
    public required AssetId SourceAssetId { get; init; }
    public required string? SourceAssetName { get; init; }
    public required NetworkStructuralRevision ManifestRevision { get; init; }
    public required ulong CreatedFrame { get; init; }
    public required ulong EarliestAdvanceFrame { get; init; }

    public NetworkScopeLoadPhase Phase { get; set; }
    public ClientBaselineState? Baseline { get; set; }
    public SceneContentInstance? Content { get; set; }
    public bool ScopeLoadedSent { get; set; }
    public bool ScopeReadySent { get; set; }
    public bool CancelRequested { get; set; }
    public bool ConsumeBaselineRevisionOnCancel { get; set; }
    public string? Diagnostic { get; set; }
    public int CompletedItems { get; set; }
    public int? TotalItems { get; set; }
    public ulong LastProgressPublishedFrame { get; set; } = ulong.MaxValue;
    public int WorkItemsAdvancedLastFrame { get; set; }

    public ClientBaselineValidationStage ValidationStage { get; set; }
    public int ValidationCursor { get; set; }
    public int BindingCursor { get; set; }
    public int DynamicCursor { get; set; }
    public int PlayerCursor { get; set; }
    public int ComponentCursor { get; set; }
    public int SpawnReadyCursor { get; set; }
    public bool AuthoredBindingsCommitted { get; set; }
    public bool CommitStarted { get; set; }
    public NetworkStructuralRevision PreviousStructuralRevision { get; set; }
    public ulong PreviousServerTick { get; set; }

    public Dictionary<Guid, NetworkAuthoredBinding> AuthoredBySource { get; } = [];
    public HashSet<NetworkEntityId> EntityIds { get; } = [];
    public HashSet<NetworkEntityId> PresentEntityIds { get; } = [];
    public HashSet<NetworkPeerId> PlayerIds { get; } = [];
    public HashSet<(NetworkEntityId Entity, ushort Component)> ComponentKeys { get; } = [];
    public Dictionary<Entity, Guid> SourceByEntity { get; } = new(ReferenceEqualityComparer.Instance);
    public Entity[] ContentEntities { get; set; } = [];
    public KeyValuePair<Guid, Entity>[] SourceMappings { get; set; } = [];
    public Dictionary<NetworkPeerId, NetworkEntityId> StagedPlayerMappings { get; } = [];
    public Dictionary<(NetworkEntityId Entity, ushort Component), uint> StagedStateSequences { get; } = [];
    public Dictionary<NetworkPeerId, NetworkEntityId?> PreviousPlayerMappings { get; } = [];
    public Dictionary<(NetworkEntityId Entity, ushort Component), uint?> PreviousStateSequences { get; } = [];
    public HashSet<Guid> ConsumedAuthoredSources { get; } = [];
    public NetworkEntityRecord[] SpawnReadyRecords { get; set; } = [];

    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public TimeSpan LastWorkItemDuration { get; set; }
    public TimeSpan MaximumWorkItemDuration { get; set; }
    public TimeSpan LoadingContentDuration { get; set; }
    public TimeSpan BaselineValidationDuration { get; set; }
    public TimeSpan AuthoredBindingDuration { get; set; }
    public TimeSpan DynamicMaterializationDuration { get; set; }
    public TimeSpan PlayerMappingDuration { get; set; }
    public TimeSpan ComponentStateApplicationDuration { get; set; }
    public TimeSpan SpawnReadyNotificationDuration { get; set; }
    public TimeSpan CommitDuration { get; set; }

    public bool IsTerminal => Phase is NetworkScopeLoadPhase.Ready or
        NetworkScopeLoadPhase.Failed or NetworkScopeLoadPhase.Cancelled;
}
