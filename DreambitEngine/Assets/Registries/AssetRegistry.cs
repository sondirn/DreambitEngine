using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Newtonsoft.Json;

namespace Dreambit;

public enum AssetRegistrySource
{
    AllOfType,
    Explicit
}

public enum AssetRegistryLoadMode
{
    Manual,
    Preload
}

/// <summary>
/// A scene-owned typed index and resident reference holder over the shared Resources cache.
/// Configure before loading. LoadAll is synchronous and idempotent until the service is disposed.
/// </summary>
public abstract class AssetRegistry<TAsset> : SceneServiceComponent where TAsset : DreambitAsset
{
    private readonly List<TAsset> _assets = [];
    private readonly Dictionary<AssetId, TAsset> _byId = [];
    private readonly Dictionary<string, TAsset> _byName = new(LogicalAssetNameComparer.Instance);

    protected AssetRegistry()
    {
        Assets = _assets.AsReadOnly();
    }

    [DreambitSerialize]
    public AssetRegistrySource Source { get; set; } = AssetRegistrySource.AllOfType;

    [DreambitSerialize]
    public AssetRegistryLoadMode LoadMode { get; set; } = AssetRegistryLoadMode.Preload;

    [DreambitSerialize]
    [ShowInInspectorWhen(nameof(Source), AssetRegistrySource.Explicit)]
    [Tooltip("Selected assets remain unloaded until LoadAll or scene preload. Stable IDs follow asset moves.")]
    public List<AssetReference<TAsset>> ExplicitAssets { get; set; } = [];

    [JsonIgnore] public IReadOnlyList<TAsset> Assets { get; }
    [JsonIgnore] public int Count => _assets.Count;
    [JsonIgnore] public bool IsLoaded { get; private set; }

    public TAsset Get(AssetId id) => TryGet(id, out var asset) ? asset :
        throw new KeyNotFoundException($"Registry '{GetType().FullName}' has no loaded asset with ID '{id}'.");

    public bool TryGet(AssetId id, out TAsset asset) => _byId.TryGetValue(id, out asset);

    public TAsset Get(string assetName) => TryGet(assetName, out var asset) ? asset :
        throw new KeyNotFoundException($"Registry '{GetType().FullName}' has no loaded asset named '{assetName}'.");

    public bool TryGet(string assetName, out TAsset asset)
    {
        asset = null;
        return assetName is not null && _byName.TryGetValue(assetName, out asset);
    }

    public void LoadAll()
    {
        ObjectDisposedException.ThrowIf(IsDestroyed, this);
        if (IsLoaded)
            return;

        try
        {
            var entries = GetEntries();
            var ids = new HashSet<AssetId>();
            var names = new HashSet<string>(LogicalAssetNameComparer.Instance);
            foreach (var entry in entries)
            {
                if (entry.Id.IsEmpty || string.IsNullOrWhiteSpace(entry.AssetName))
                    throw new InvalidOperationException($"Invalid catalog entry '{entry.AssetName}' ({entry.Id}).");
                if (!ids.Add(entry.Id))
                    throw new InvalidOperationException($"Duplicate asset ID '{entry.Id}'.");
                if (!names.Add(entry.AssetName))
                    throw new InvalidOperationException($"Duplicate logical asset name '{entry.AssetName}'.");
            }

            // Publish only a complete index. Failed loads may warm Resources, but never expose
            // a partially populated registry or unload content that another caller may hold.
            var loaded = new List<TAsset>(entries.Count);
            foreach (var entry in entries)
            {
                try
                {
                    loaded.Add(Resources.LoadDreambitAsset<TAsset>(entry));
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not load required asset '{entry.AssetName}' ({entry.Id}).", exception);
                }
            }
            for (var i = 0; i < entries.Count; i++)
            {
                _byId.Add(entries[i].Id, loaded[i]);
                _byName.Add(entries[i].AssetName, loaded[i]);
            }
            _assets.AddRange(loaded);
            IsLoaded = true;
        }
        catch (Exception exception)
        {
            ClearIndex();
            throw new InvalidOperationException(
                $"Asset registry '{GetType().FullName}' could not be populated: {exception.Message}", exception);
        }
    }

    private IReadOnlyList<AssetCatalogEntry> GetEntries()
    {
        if (Source == AssetRegistrySource.AllOfType)
            return Resources.FindAssets<TAsset>();
        if (Source != AssetRegistrySource.Explicit)
            throw new InvalidOperationException($"Unsupported registry source '{Source}'.");
        if (ExplicitAssets is null)
            throw new InvalidOperationException("The explicit asset collection cannot be null.");
        if (ExplicitAssets.Count == 0)
            return Array.Empty<AssetCatalogEntry>();

        var catalog = new Dictionary<AssetId, AssetCatalogEntry>();
        foreach (var entry in Resources.GetAssetCatalog())
            if (!catalog.TryAdd(entry.Id, entry))
                throw new InvalidOperationException($"Duplicate catalog asset ID '{entry.Id}'.");

        var entries = new List<AssetCatalogEntry>(ExplicitAssets.Count);
        foreach (var reference in ExplicitAssets)
        {
            if (reference is null || reference.Id.IsEmpty)
                throw new InvalidOperationException("An explicit asset selection is empty.");
            if (!catalog.TryGetValue(reference.Id, out var entry))
                throw new InvalidOperationException(
                    $"Selected asset '{reference.AssetName}' ({reference.Id}) is absent from the live catalog.");
            entries.Add(entry);
        }
        return entries;
    }

    protected sealed override bool PropagateStartupExceptions => true;

    public override void OnServicesReady()
    {
        if (Scene?.ExecutionMode == SceneExecutionMode.Editor)
            return;
        switch (LoadMode)
        {
            case AssetRegistryLoadMode.Manual:
                return;
            case AssetRegistryLoadMode.Preload:
                LoadAll();
                return;
            default:
                throw new InvalidOperationException($"Unsupported registry load mode '{LoadMode}'.");
        }
    }

    protected override void OnServiceDisposing()
    {
        ClearIndex();
        base.OnServiceDisposing();
    }

    private void ClearIndex()
    {
        _assets.Clear();
        _byId.Clear();
        _byName.Clear();
        IsLoaded = false;
    }
}
