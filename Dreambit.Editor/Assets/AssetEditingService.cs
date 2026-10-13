using System.Runtime.CompilerServices;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Projects;
using Dreambit.Scripting;

namespace Dreambit.Editor.Assets;

internal sealed class AssetEditingService : IDisposable
{
    private static readonly TimeSpan InitialAutoSaveRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumAutoSaveRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PreviewUpdateDelay = TimeSpan.FromMilliseconds(100);

    private readonly DreambitProjectDefinition _project;
    private readonly AssetDatabase _assets;
    private readonly EditorTypeRegistry _types;
    private readonly InspectorMetadataCache _metadata;
    private readonly GameAssemblyLoadService _assemblies;
    private readonly Action<string, Exception?>? _reportError;
    private AssetRecord? _selected;
    private long _observedAssetVersion;
    private string? _observedDiskSource;
    private string? _externalChangeSource;
    private DateTimeOffset _nextAutoSaveAttemptUtc;
    private TimeSpan _autoSaveRetryDelay = InitialAutoSaveRetryDelay;
    private string? _reloadSnapshot;
    private bool _reloadDirty;
    private DreambitAssetDocument? _detachedReloadDocument;
    private DreambitAssetDocument? _pendingPreviewDocument;
    private DateTimeOffset _pendingPreviewChangedUtc;
    private bool _previewDeferredByInteraction;
    private bool _flushingPreview;
    private bool _disposed;

    public AssetEditingService(
        DreambitProjectDefinition project,
        AssetDatabase assets,
        EditorTypeRegistry types,
        InspectorMetadataCache metadata,
        GameAssemblyLoadService assemblies,
        Action<string, Exception?>? reportError = null)
    {
        _project = project;
        _assets = assets;
        _types = types;
        _metadata = metadata;
        _assemblies = assemblies;
        _reportError = reportError;
        _assemblies.Reloading += OnReloading;
        _assemblies.Unloading += OnUnloading;
        _assemblies.Reloaded += OnReloaded;
    }

    public AssetRecord? Selected => _selected;
    public DreambitAssetDocument? Current { get; private set; }
    public string? ExternalChangeConflict { get; private set; }
    public event Action? Changed;
    public event Action<DreambitAssetDocument>? PreviewChanged;
    public event Action<DreambitAssetDocument>? Saved;

    /// <summary>
    /// Selects an asset only after the current dirty document can be saved and the requested
    /// inspectable document can be opened. A false result leaves the previous selection intact.
    /// </summary>
    public bool Select(AssetRecord? asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is { } open && asset is not null && asset.Id == open.Asset.Id)
        {
            _selected = asset;
            open.RebindAsset(asset);
            return true;
        }
        if (Current is { IsDirty: true } current &&
            (asset is null || asset.Id != current.Asset.Id))
        {
            try
            {
                Save();
            }
            catch (Exception exception)
            {
                _reportError?.Invoke(
                    $"Could not save '{current.Asset.RelativePath}'. The asset remains open.",
                    exception);
                return false;
            }
        }
        FlushPendingPreview(force: true);

        DreambitAssetDocument? replacement = null;
        string? replacementDiskSource = null;
        if (asset is not null)
        {
            var type = ResolveAssetType(asset);
            if (type is not null)
            {
                try
                {
                    var path = GetAssetPath(asset);
                    replacement = DreambitAssetDocument.Open(
                        asset,
                        path,
                        type,
                        _metadata,
                        _reportError);
                    replacementDiskSource = File.ReadAllText(path);
                }
                catch (Exception exception)
                {
                    var cleanupFailure = EditorDisposal.TryDispose(replacement);
                    _reportError?.Invoke(
                        cleanupFailure is null
                            ? $"Could not inspect '{asset.RelativePath}'. The previous asset remains open."
                            : $"Could not inspect '{asset.RelativePath}'. The previous asset remains open.\n" +
                              cleanupFailure,
                        exception);
                    return false;
                }
            }
        }

        DetachAndDisposeCurrent();
        _selected = asset;
        Current = replacement;
        if (replacement is not null)
        {
            replacement.Changed += OnDocumentChanged;
            _observedDiskSource = replacementDiskSource;
            _observedAssetVersion = _assets.GetSnapshot().Version;
            ClearExternalChangeConflict();
        }
        Changed?.Invoke();
        return true;
    }

    public bool TryCreate(Type assetType, string relativePath, out string? error)
    {
        string? temporaryPath = null;
        try
        {
            if (!typeof(DreambitAsset).IsAssignableFrom(assetType) || assetType.IsAbstract)
                throw new InvalidOperationException($"'{assetType.FullName}' is not a creatable Dreambit asset.");
            var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
            if (normalized.Length == 0 || normalized.Contains("../", StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a path inside the Assets folder.");
            var expectedExtension = DreambitAssetTypeRegistry.GetFileExtension(assetType);
            if (!normalized.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{assetType.Name} assets must use the '{expectedExtension}' extension.");
            }
            var path = Path.GetFullPath(Path.Combine(_project.ContentRootPath, normalized));
            var contentPrefix = Path.TrimEndingDirectorySeparator(_project.ContentRootPath) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Asset path escapes the Assets folder.");
            if (File.Exists(path))
                throw new IOException($"'{normalized}' already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var source = CreateAssetSource(assetType, path);
            temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, source);
            // Do not overwrite a file created after the existence check above.
            File.Move(temporaryPath, path);
            temporaryPath = null;
            _assets.RefreshNow();
            if (_assets.TryGetAsset(normalized, out var created))
            {
                if (!Select(created))
                {
                    error = $"'{normalized}' was created, but the current asset could not be saved. " +
                            "The previous asset remains open.";
                    return false;
                }
            }
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupException)
                {
                    // The original create failure is more actionable; this uniquely named file is
                    // never considered an asset and can be removed on the next project cleanup.
                    _reportError?.Invoke(
                        $"Could not remove incomplete asset source '{temporaryPath}'.",
                        cleanupException);
                }
            }
        }
    }

    public void Update(bool autoSave, TimeSpan delay, bool editorInteractionActive = false)
    {
        RefreshFromDatabase();
        var now = DateTimeOffset.UtcNow;
        UpdatePendingPreview(editorInteractionActive, now);
        if (editorInteractionActive || !autoSave || ExternalChangeConflict is not null ||
            Current is not { IsDirty: true } document ||
            now - document.LastChangedUtc < delay ||
            now < _nextAutoSaveAttemptUtc)
            return;
        try
        {
            Save();
            ResetAutoSaveRetry();
        }
        catch (Exception exception)
        {
            ScheduleAutoSaveRetry(now);
            _reportError?.Invoke(
                $"Could not auto-save '{document.Asset.RelativePath}'. The editor will retry after " +
                $"{_nextAutoSaveAttemptUtc - now:g}.",
                exception);
        }
    }

    public void Save()
    {
        var document = Current;
        if (document is null)
            return;
        var path = GetAssetPath(document.Asset);
        if (!CanSaveWithoutOverwritingExternalChanges(document, path))
            throw new InvalidOperationException(ExternalChangeConflict);

        document.Save(path);
        ObserveDiskSource(path);
        // Refresh immediately so the incremental baker sees this exact save on the next frame.
        // The completed bake then rehydrates the open scene from fresh asset instances.
        _assets.RefreshNow();
        _observedAssetVersion = _assets.GetSnapshot().Version;
        FlushPendingPreview(force: true);
        try
        {
            Saved?.Invoke(document);
        }
        catch (Exception exception)
        {
            _reportError?.Invoke(
                $"'{document.Asset.RelativePath}' was saved, but its editor previews could not be refreshed.",
                exception);
        }
        ResetAutoSaveRetry();
    }

    public bool Clear() => Select(null);

    /// <summary>
    /// Rebinds an open document by stable asset ID after a filesystem operation. If the asset was
    /// deleted, the document is detached before autosave can recreate its former path.
    /// </summary>
    public void RefreshFromDatabase()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var identity = Current?.Asset.Id ?? _selected?.Id;
        if (identity is null)
            return;

        var snapshot = _assets.GetSnapshot();
        var refreshed = snapshot.Assets.FirstOrDefault(asset => asset.Id == identity.Value);
        if (refreshed is null)
        {
            var removedPath = Current?.Asset.RelativePath ?? _selected?.RelativePath;
            DetachAndDisposeCurrent();
            _selected = null;
            Changed?.Invoke();
            if (!string.IsNullOrWhiteSpace(removedPath))
            {
                _reportError?.Invoke(
                    $"The open asset '{removedPath}' was removed. Its editor document was closed.",
                    null);
            }
            return;
        }

        _selected = refreshed;
        if (Current is not { } document)
            return;

        document.RebindAsset(refreshed);
        if (snapshot.Version == _observedAssetVersion &&
            (ExternalChangeConflict is null || document.IsDirty))
        {
            return;
        }

        var path = GetAssetPath(refreshed);
        string diskSource;
        try
        {
            diskSource = File.ReadAllText(path);
        }
        catch (Exception exception)
        {
            _observedAssetVersion = snapshot.Version;
            SetExternalChangeConflict(
                $"unreadable:{exception.GetType().FullName}:{exception.Message}",
                $"The source for '{refreshed.RelativePath}' changed outside the editor, but could not be read. " +
                "The existing editor document was retained.",
                exception);
            return;
        }

        _observedAssetVersion = snapshot.Version;
        if (string.Equals(diskSource, _observedDiskSource, StringComparison.Ordinal))
        {
            ClearExternalChangeConflict();
            return;
        }

        if (document.IsDirty)
        {
            SetDirtyExternalChangeConflict(refreshed, diskSource);
            return;
        }

        ReopenCleanDocument(document, refreshed, path, diskSource);
    }

    public void BeforeContentReload()
    {
        var document = Current;
        if (document is null)
            return;
        SuspendForReload(document, captureOnlyWhenDirty: true);
    }

    public void AfterContentReload()
    {
        if (_selected is not null)
        {
            var selected = _assets.GetSnapshot().Assets.FirstOrDefault(asset => asset.Id == _selected.Id);
            if (selected is null)
            {
                _selected = null;
                ClearExternalTracking();
                Changed?.Invoke();
                ClearReloadState();
                return;
            }
            Select(selected);
            if (Current is not null && _reloadSnapshot is not null)
                Current.RestoreReloadSnapshot(_reloadSnapshot, _reloadDirty);
        }
        ClearReloadState();
    }

    private Type? ResolveAssetType(AssetRecord asset)
    {
        if (asset.Kind is AssetKind.Texture or AssetKind.Font or AssetKind.Effect or
            AssetKind.Cutscene or AssetKind.TiledMap)
            return null;
        if (asset.Kind == AssetKind.Blueprint)
            return typeof(EntityBlueprint);
        if (asset.Kind == AssetKind.Scene || string.IsNullOrWhiteSpace(asset.TypeId))
            return null;
        if (!DreambitAssetTypeRegistry.TryResolve(asset.TypeId, out var resolvedType))
        {
            if (asset.Kind == AssetKind.DreambitAsset)
            {
                _reportError?.Invoke(
                    $"Could not inspect '{asset.RelativePath}'. No loaded Dreambit asset type " +
                    $"claims ID '{asset.TypeId}'. The source file was preserved.",
                    null);
            }
            return null;
        }

        return _types.AssetTypes.FirstOrDefault(type => type == resolvedType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnReloading(LoadedGameAssembly? assembly)
    {
        var document = Current;
        if (document is null || assembly is null || document.AssetType.Assembly != assembly.Assembly)
            return;
        SuspendForReload(document, captureOnlyWhenDirty: false);
    }

    private void OnReloaded(LoadedGameAssembly _)
    {
        AfterContentReload();
    }

    private void OnUnloading(LoadedGameAssembly assembly)
    {
        _detachedReloadDocument?.ReleaseCollectibleReferences();
        _detachedReloadDocument = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _assemblies.Reloading -= OnReloading;
        _assemblies.Unloading -= OnUnloading;
        _assemblies.Reloaded -= OnReloaded;
        DetachAndDisposeCurrent();
        _selected = null;
        ClearExternalTracking();
        ClearReloadState();
        _pendingPreviewDocument = null;
        Changed = null;
        PreviewChanged = null;
        Saved = null;
    }

    private void OnDocumentChanged(DreambitAssetDocument document)
    {
        _pendingPreviewDocument = document;
        _pendingPreviewChangedUtc = DateTimeOffset.UtcNow;
    }

    internal void FlushPendingPreview() => FlushPendingPreview(force: true);

    internal void UpdatePendingPreview(bool editorInteractionActive, DateTimeOffset now)
    {
        if (editorInteractionActive)
        {
            _previewDeferredByInteraction = _pendingPreviewDocument is not null;
            return;
        }

        var force = _previewDeferredByInteraction;
        _previewDeferredByInteraction = false;
        FlushPendingPreview(now, force);
    }

    private void FlushPendingPreview(bool force) =>
        FlushPendingPreview(DateTimeOffset.UtcNow, force);

    private void FlushPendingPreview(DateTimeOffset now, bool force)
    {
        if (_flushingPreview || _pendingPreviewDocument is not { } document ||
            !force && now - _pendingPreviewChangedUtc < PreviewUpdateDelay)
        {
            return;
        }

        // Clear first because PreviewChanged handlers may synchronously resolve Blueprint
        // sources, which in turn ask us to flush pending editor state.
        _pendingPreviewDocument = null;
        _flushingPreview = true;
        try
        {
            var runtimeAsset = Resources.LoadDreambitAsset(
                document.Asset.Id,
                document.Asset.LogicalAssetName,
                document.AssetType) as DreambitAsset;
            if (runtimeAsset is not null && !ReferenceEquals(runtimeAsset, document.Instance))
                document.CopyInspectableValuesTo(runtimeAsset);
            PreviewChanged?.Invoke(document);
        }
        catch (Exception exception)
        {
            _reportError?.Invoke($"Could not preview '{document.Asset.RelativePath}' in the open scene.", exception);
        }
        finally
        {
            _flushingPreview = false;
        }
    }

    private void DetachAndDisposeCurrent()
    {
        var document = Current;
        if (document is null)
            return;
        Current = null;
        ClearExternalTracking();
        DetachAndDispose(document);
    }

    private void DetachAndDispose(DreambitAssetDocument document)
    {
        var relativePath = document.Asset.RelativePath;
        if (ReferenceEquals(_pendingPreviewDocument, document))
        {
            _pendingPreviewDocument = null;
            _previewDeferredByInteraction = false;
        }
        document.Changed -= OnDocumentChanged;
        var cleanupFailure = EditorDisposal.TryDispose(document);
        if (cleanupFailure is not null)
        {
            _reportError?.Invoke(
                $"Could not dispose the editor instance for '{relativePath}'. The document was detached.\n" +
                cleanupFailure,
                null);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SuspendForReload(
        DreambitAssetDocument document,
        bool captureOnlyWhenDirty)
    {
        try
        {
            _reloadDirty = document.IsDirty;
            _reloadSnapshot = !captureOnlyWhenDirty || document.IsDirty
                ? document.CaptureJson()
                : null;
        }
        catch (Exception exception)
        {
            _reloadSnapshot = null;
            _reloadDirty = false;
            _reportError?.Invoke(
                $"Could not capture '{document.Asset.RelativePath}' before content reload. " +
                "The last saved source will be reopened.",
                exception);
        }
        finally
        {
            if (ReferenceEquals(Current, document))
                Current = null;
            ClearExternalTracking();
            DetachAndDispose(document);
            _detachedReloadDocument = document;
        }
        Changed?.Invoke();
    }

    private void ReopenCleanDocument(
        DreambitAssetDocument previous,
        AssetRecord asset,
        string path,
        string diskSource)
    {
        var type = ResolveAssetType(asset);
        if (type is null)
        {
            SetExternalChangeConflict(
                diskSource,
                $"The source for '{asset.RelativePath}' changed outside the editor, but its asset type " +
                "is not currently available. The existing editor document was retained.",
                null);
            return;
        }

        DreambitAssetDocument replacement;
        try
        {
            replacement = DreambitAssetDocument.Open(asset, path, type, _metadata, _reportError);
        }
        catch (Exception exception)
        {
            SetExternalChangeConflict(
                diskSource,
                $"The source for '{asset.RelativePath}' changed outside the editor, but could not be reopened. " +
                "The existing editor document was retained.",
                exception);
            return;
        }

        replacement.Changed += OnDocumentChanged;
        Current = replacement;
        _selected = asset;
        _observedDiskSource = diskSource;
        ClearExternalChangeConflict();
        DetachAndDispose(previous);
        Changed?.Invoke();
    }

    private bool CanSaveWithoutOverwritingExternalChanges(
        DreambitAssetDocument document,
        string path)
    {
        string diskSource;
        try
        {
            diskSource = File.ReadAllText(path);
        }
        catch (Exception exception)
        {
            SetExternalChangeConflict(
                $"unreadable:{exception.GetType().FullName}:{exception.Message}",
                $"Could not verify the source for '{document.Asset.RelativePath}' before saving. " +
                "The editor document was retained and the file was not overwritten.",
                exception);
            return false;
        }

        if (_observedDiskSource is null ||
            string.Equals(diskSource, _observedDiskSource, StringComparison.Ordinal))
        {
            ClearExternalChangeConflict();
            return true;
        }

        SetDirtyExternalChangeConflict(document.Asset, diskSource);
        return false;
    }

    private void ObserveDiskSource(string path)
    {
        _observedDiskSource = File.ReadAllText(path);
        _observedAssetVersion = _assets.GetSnapshot().Version;
        ClearExternalChangeConflict();
    }

    private void SetDirtyExternalChangeConflict(AssetRecord asset, string diskSource) =>
        SetExternalChangeConflict(
            diskSource,
            $"The source for '{asset.RelativePath}' changed outside the editor while it has unsaved changes. " +
            "The editor copy was retained and auto-save is paused until the conflict is resolved.",
            null);

    private void SetExternalChangeConflict(
        string source,
        string message,
        Exception? exception)
    {
        if (string.Equals(_externalChangeSource, source, StringComparison.Ordinal) &&
            string.Equals(ExternalChangeConflict, message, StringComparison.Ordinal))
        {
            return;
        }

        _externalChangeSource = source;
        ExternalChangeConflict = message;
        _reportError?.Invoke(message, exception);
    }

    private void ClearExternalChangeConflict()
    {
        _externalChangeSource = null;
        ExternalChangeConflict = null;
    }

    private void ClearExternalTracking()
    {
        _observedAssetVersion = 0;
        _observedDiskSource = null;
        ClearExternalChangeConflict();
        ResetAutoSaveRetry();
    }

    private static string CreateAssetSource(Type assetType, string path)
    {
        if (assetType == typeof(Cutscene))
        {
            return """
                   - scriptGroup:
                       - script: TODO
                   """ + Environment.NewLine;
        }

        var instance = (DreambitAsset?)Activator.CreateInstance(assetType)
                       ?? throw new InvalidOperationException($"Could not create '{assetType.FullName}'.");
        Exception? failure = null;
        string? source = null;
        try
        {
            if (instance is EntityBlueprint blueprint && string.IsNullOrWhiteSpace(blueprint.Name))
                blueprint.Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            source = DreambitJson.Serialize(instance);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            instance.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(
                    "The new asset could not be serialized or disposed.",
                    failure,
                    exception);
        }

        if (failure is not null)
            throw failure;
        return source!;
    }

    private void ScheduleAutoSaveRetry(DateTimeOffset failedAt)
    {
        _nextAutoSaveAttemptUtc = failedAt + _autoSaveRetryDelay;
        _autoSaveRetryDelay = TimeSpan.FromTicks(Math.Min(
            MaximumAutoSaveRetryDelay.Ticks,
            _autoSaveRetryDelay.Ticks * 2));
    }

    private void ResetAutoSaveRetry()
    {
        _nextAutoSaveAttemptUtc = DateTimeOffset.MinValue;
        _autoSaveRetryDelay = InitialAutoSaveRetryDelay;
    }

    private string GetAssetPath(AssetRecord asset) =>
        Path.Combine(
            _project.ContentRootPath,
            asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private void ClearReloadState()
    {
        _reloadSnapshot = null;
        _reloadDirty = false;
    }
}
