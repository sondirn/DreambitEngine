using System.Collections;
using Dreambit.ECS;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Undo;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Assets;

internal sealed class DreambitAssetDocument : IDisposable
{
    private readonly InspectorMetadataCache _metadata;
    private readonly Action<string, Exception?>? _reportError;
    private JObject _source;
    private string _savedSnapshot;
    private string _currentSnapshot = string.Empty;
    private Type? _assetType;
    private DreambitAsset? _instance;
    private bool _disposed;

    private DreambitAssetDocument(
        AssetRecord asset,
        Type assetType,
        DreambitAsset instance,
        JObject source,
        InspectorMetadataCache metadata,
        Action<string, Exception?>? reportError)
    {
        Asset = asset;
        _assetType = assetType;
        _instance = instance;
        _source = source;
        _metadata = metadata;
        _reportError = reportError;
        Undo = new UndoService();
        _savedSnapshot = CaptureJson();
    }

    public AssetRecord Asset { get; private set; }
    public Type AssetType => _assetType ?? throw new ObjectDisposedException(nameof(DreambitAssetDocument));
    public DreambitAsset Instance => _instance ?? throw new ObjectDisposedException(nameof(DreambitAssetDocument));
    public UndoService Undo { get; }
    public bool IsDirty { get; private set; }
    public long Revision { get; private set; }
    public DateTimeOffset LastChangedUtc { get; private set; }
    public event Action<DreambitAssetDocument>? Changed;

    public static DreambitAssetDocument Open(
        AssetRecord asset,
        string path,
        Type assetType,
        InspectorMetadataCache metadata,
        Action<string, Exception?>? reportError = null)
    {
        var json = File.ReadAllText(path);
        var source = JObject.Parse(json);
        var instance = DreambitJson.Deserialize(json, assetType) as DreambitAsset
                       ?? throw new InvalidDataException($"'{asset.RelativePath}' is not a {assetType.FullName} asset.");
        instance.AssetId = asset.Id;
        instance.AssetName = asset.LogicalAssetName;
        return new DreambitAssetDocument(asset, assetType, instance, source, metadata, reportError);
    }

    public void Apply(
        string name,
        Action<DreambitAsset> mutation,
        string? mergeKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mutation);
        var before = _currentSnapshot;
        string after;
        try
        {
            mutation(Instance);
            after = CaptureJson();
        }
        catch (Exception exception)
        {
            RollBackFailedMutation(before, exception);
            throw;
        }
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            UpdateDirtyState(after);
            return;
        }
        UpdateDirtyState(after);
        LastChangedUtc = DateTimeOffset.UtcNow;
        Undo.Record(new AssetSnapshotCommand(name, this, before, after, mergeKey));
        Revision++;
        Changed?.Invoke(this);
    }

    public void ReplaceBlueprint(
        string name,
        EntityBlueprint blueprint,
        string? mergeKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(blueprint);
        if (AssetType != typeof(EntityBlueprint))
            throw new InvalidOperationException("Only Entity Blueprint documents can accept a hierarchy snapshot.");

        var before = _currentSnapshot;
        var after = DreambitJson.Serialize(blueprint);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            UpdateDirtyState(after);
            return;
        }

        ReplaceInstance(after);
        // ReplaceBlueprint starts from the scene serializer, while ordinary asset edits use
        // the document's stable merge/order rules. Normalize once so the cached snapshot used
        // by the next Apply call remains equivalent to its serialized document state.
        after = CaptureJson();
        UpdateDirtyState(after);
        LastChangedUtc = DateTimeOffset.UtcNow;
        Undo.Record(new AssetSnapshotCommand(name, this, before, after, mergeKey));
        Revision++;
        Changed?.Invoke(this);
    }

    public void Save(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = CaptureJson();
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        _savedSnapshot = json;
        IsDirty = false;
    }

    public string CaptureJson()
    {
        var merged = (JObject)_source.DeepClone();
        foreach (var member in _metadata.Get(AssetType, InspectorTargetKind.Asset))
        {
            RemoveProperties(
                merged,
                DreambitSerializationRules.GetFormerNames(member.Member));
            RemoveProperties(merged, [member.SerializedName]);
            var value = member.GetValue(Instance);
            merged[member.SerializedName] = SerializeValue(value, member.ValueType);
        }

        if (DreambitAssetTypeRegistry.ShouldPersistTypeMetadata(AssetType))
        {
            RemoveProperties(merged, [DreambitAssetTypeRegistry.MetadataPropertyName]);
            merged.AddFirst(new JProperty(
                DreambitAssetTypeRegistry.MetadataPropertyName,
                DreambitAssetTypeRegistry.GetTypeId(AssetType)));
        }
        _source = merged;
        _currentSnapshot = merged.ToString(Newtonsoft.Json.Formatting.Indented);
        return _currentSnapshot;
    }

    private static void RemoveProperties(JObject document, IEnumerable<string> names)
    {
        var serializedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (serializedNames.Count == 0)
            return;

        foreach (var property in document.Properties()
                     .Where(property => serializedNames.Contains(property.Name))
                     .ToArray())
        {
            property.Remove();
        }
    }

    private static JToken SerializeValue(object? value, Type declaredType)
    {
        if (value is null)
            return JValue.CreateNull();
        if (value is DreambitAsset asset)
        {
            if (!asset.AssetId.IsEmpty)
                return DreambitAssetReferenceToken.Create(asset.AssetId, asset.AssetName);
            if (!string.IsNullOrWhiteSpace(asset.AssetName))
                return new JValue(asset.AssetName);
        }
        if (value is Entity entity)
            return new JValue(entity.Id.ToString());
        if (value is Component component)
            return new JValue(component.Entity.Id.ToString());
        if (value is IDictionary dictionary)
        {
            var valueType = declaredType.IsGenericType
                ? declaredType.GetGenericArguments()[1]
                : typeof(object);
            var result = new JObject();
            foreach (DictionaryEntry entry in dictionary)
                result[Convert.ToString(entry.Key) ?? string.Empty] = SerializeValue(entry.Value, valueType);
            return result;
        }
        if (value is IEnumerable sequence && value is not string)
        {
            var elementType = declaredType.IsArray
                ? declaredType.GetElementType() ?? typeof(object)
                : declaredType.IsGenericType
                    ? declaredType.GetGenericArguments()[0]
                    : typeof(object);
            var result = new JArray();
            foreach (var item in sequence)
                result.Add(SerializeValue(item, elementType));
            return result;
        }
        return DreambitJson.ToToken(value);
    }

    public void CopyInspectableValuesTo(DreambitAsset target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.GetType() != AssetType)
            throw new ArgumentException(
                $"Preview target '{target.GetType().FullName}' is not '{AssetType.FullName}'.",
                nameof(target));

        foreach (var member in _metadata.Get(AssetType, InspectorTargetKind.Asset))
        {
            if (member.IsReadOnly)
                continue;
            var value = member.GetValue(Instance);
            object? copy;
            if (value is DreambitAsset)
            {
                copy = value;
            }
            else
            {
                var token = SerializeValue(value, member.ValueType);
                copy = token.Type == JTokenType.Null
                    ? null
                    : DreambitJson.FromToken(token, member.ValueType);
            }
            member.SetValue(target, copy);
        }
    }

    private void Restore(string json, bool notifyChanged = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReplaceInstance(json);
        UpdateDirtyState(json);
        LastChangedUtc = DateTimeOffset.UtcNow;
        if (notifyChanged)
        {
            Revision++;
            Changed?.Invoke(this);
        }
    }

    private void ReplaceInstance(string json)
    {
        var source = JObject.Parse(json);
        var replacement = DreambitJson.Deserialize(json, AssetType) as DreambitAsset
                          ?? throw new InvalidDataException("Could not restore the asset snapshot.");
        replacement.AssetId = Asset.Id;
        replacement.AssetName = Asset.LogicalAssetName;
        var previous = Instance;
        _source = source;
        _instance = replacement;
        _currentSnapshot = json;
        var cleanupFailure = EditorDisposal.TryDispose(previous);
        if (cleanupFailure is not null)
        {
            _reportError?.Invoke(
                $"Could not dispose the previous editor instance for '{Asset.RelativePath}'. " +
                "The replacement remains active.\n" + cleanupFailure,
                null);
        }
    }

    private void UpdateDirtyState(string snapshot) =>
        IsDirty = !string.Equals(_savedSnapshot, snapshot, StringComparison.Ordinal);

    private void RollBackFailedMutation(string before, Exception mutationException)
    {
        try
        {
            Restore(before, notifyChanged: false);
        }
        catch (Exception rollbackException)
        {
            throw new AggregateException(
                "The asset mutation failed and its previous snapshot could not be restored.",
                mutationException,
                rollbackException);
        }
    }

    internal void RestoreReloadSnapshot(string json, bool dirty)
    {
        _ = dirty;
        Restore(json);
    }

    internal void RebindAsset(AssetRecord asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id != Asset.Id)
            throw new ArgumentException("An open asset document can only be rebound to the same asset ID.", nameof(asset));
        Asset = asset;
        Instance.AssetId = asset.Id;
        Instance.AssetName = asset.LogicalAssetName;
    }

    /// <summary>
    /// Releases references to a collectible asset generation without invoking game cleanup.
    /// Cleanup already ran during reload preparation; this is a final ownership barrier before GC.
    /// </summary>
    internal void ReleaseCollectibleReferences()
    {
        _instance = null;
        _assetType = null;
        Undo.Clear();
        Changed = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var instance = _instance;
        _instance = null;
        _assetType = null;
        Undo.Clear();
        Changed = null;
        instance?.Dispose();
    }

    private sealed class AssetSnapshotCommand(
        string name,
        DreambitAssetDocument document,
        string before,
        string after,
        string? mergeKey) : IUndoableEditorCommand
    {
        public string Name { get; } = name;
        public string? MergeKey { get; } = mergeKey;
        private DreambitAssetDocument Document { get; } = document;
        private string Before { get; } = before;
        private string After { get; set; } = after;
        public bool IsNoOp => string.Equals(Before, After, StringComparison.Ordinal);

        public bool TryMerge(IUndoableEditorCommand subsequent)
        {
            if (subsequent is not AssetSnapshotCommand next ||
                !ReferenceEquals(Document, next.Document) ||
                !string.Equals(MergeKey, next.MergeKey, StringComparison.Ordinal))
            {
                return false;
            }

            After = next.After;
            return true;
        }

        public void Undo() => Document.Restore(Before);
        public void Redo() => Document.Restore(After);
    }
}
