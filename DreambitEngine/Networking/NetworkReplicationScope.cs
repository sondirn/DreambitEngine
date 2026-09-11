using System;

namespace Dreambit.Networking;

/// <summary>
/// One server-assigned replication lifetime inside the current synchronized Scene.
/// It owns one local additive content instance, but its protocol identity is independent
/// from that process-local content instance identity.
/// </summary>
public sealed class NetworkReplicationScope
{
    internal NetworkReplicationScope(
        NetworkReplicationScopeId id,
        NetworkSceneEpoch sceneEpoch,
        AssetId sourceAssetId,
        string? sourceAssetName,
        SceneContentInstance? content)
    {
        if (!id.IsValid)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (!sceneEpoch.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
        Id = id;
        SceneEpoch = sceneEpoch;
        SourceAssetId = sourceAssetId;
        SourceAssetName = sourceAssetName;
        Content = content;
        IsLoaded = true;
        IsReady = true;
    }

    /// <summary>Gets the server-assigned identity, unique for the current Scene epoch.</summary>
    public NetworkReplicationScopeId Id { get; }

    /// <summary>Gets the synchronized Scene generation that owns this identity.</summary>
    public NetworkSceneEpoch SceneEpoch { get; }

    /// <summary>Gets the stable source Scene Blueprint asset identity.</summary>
    public AssetId SourceAssetId { get; }

    /// <summary>Gets the fallback source asset name used when a registry lookup is unavailable.</summary>
    public string? SourceAssetName { get; }

    /// <summary>
    /// Gets the local additive content lifetime. The global scope has no additive content instance.
    /// </summary>
    public SceneContentInstance? Content { get; }

    /// <summary>Gets whether this is the base synchronized Scene scope.</summary>
    public bool IsGlobal => Id.IsGlobal;

    /// <summary>Gets whether this scope is still registered in the active session.</summary>
    public bool IsLoaded { get; internal set; }

    /// <summary>
    /// Gets whether this process has committed the scope's initial network baseline. Server scopes
    /// are ready immediately; a client scope remains false between ScopeLoad and ScopeReady.
    /// </summary>
    public bool IsReady { get; internal set; }
}
