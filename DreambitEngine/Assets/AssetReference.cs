using System;
using Newtonsoft.Json;

namespace Dreambit;

/// <summary>Untyped metadata used by the editor's asset picker; accessing it never loads content.</summary>
public interface IAssetReference
{
    AssetId Id { get; }
    string? AssetName { get; }
    Type AssetType { get; }
}

/// <summary>
/// A deferred, typed selection using the existing $dreambitAsset token. The catalog ID is
/// authoritative; the optional name is retained for display and diagnostics after moves.
/// </summary>
public sealed record AssetReference<TAsset>(AssetId Id, string? AssetName = null) : IAssetReference
    where TAsset : DreambitAsset
{
    [JsonIgnore] public Type AssetType => typeof(TAsset);
}
