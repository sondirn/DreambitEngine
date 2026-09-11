using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;

namespace Dreambit.Editor.Scenes;

/// <summary>
/// Presents an EntityBlueprint as a one-root SceneDocument. This lets every
/// hierarchy and inspector operation use the same structural/lifecycle path as
/// normal scene editing while snapshots are written back to the asset document.
/// </summary>
internal sealed class BlueprintEditingService : IDisposable
{
    private readonly AssetEditingService _assetEditing;
    private readonly GameAssemblyLoadService _assemblies;
    private readonly BlueprintSourceService _blueprintSources;
    private readonly Action<string, Exception?>? _reportError;
    private AssetId _openAssetId;
    private Guid _rootEntityId;
    private long _assetRevisionAlreadyApplied = -1;
    private bool _synchronizing;
    private bool _rebuildRequested;
    private bool _disposed;

    public BlueprintEditingService(
        AssetEditingService assetEditing,
        GameAssemblyLoadService assemblies,
        BlueprintSourceService blueprintSources,
        Action<string, Exception?>? reportError = null)
    {
        _assetEditing = assetEditing;
        _assemblies = assemblies;
        _blueprintSources = blueprintSources;
        _reportError = reportError;
        Selection = new SelectionService();
        _assetEditing.Changed += OnAssetEditingChanged;
        _assetEditing.PreviewChanged += OnAssetPreviewChanged;
        _assemblies.Reloading += OnAssemblyReloading;
        _assemblies.Reloaded += OnAssemblyReloaded;
    }

    public SceneDocument? Current { get; private set; }
    public SelectionService Selection { get; }
    public string? Error { get; private set; }

    public bool Open(AssetRecord asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (asset.Kind != AssetKind.Blueprint)
            throw new ArgumentException("The asset is not an Entity Blueprint.", nameof(asset));
        if (!_assetEditing.Select(asset))
            return false;
        if (_openAssetId != asset.Id)
        {
            Selection.Clear();
            _assetRevisionAlreadyApplied = -1;
        }
        _openAssetId = asset.Id;
        if (Current is null || _assetEditing.Current?.Asset.Id != asset.Id)
            RebuildFromAssetDocument();
        return true;
    }

    /// <summary>
    /// The authored Blueprint root. The editor host also creates parentless,
    /// editor-only entities (for example its preview camera), so root identity
    /// must not be inferred by counting every parentless runtime entity.
    /// </summary>
    public Entity? Root => FindAuthoredRoot(Current, _rootEntityId);

    /// <summary>Repairs a preview whose source synchronization failed on the previous frame.</summary>
    public void Update()
    {
        if (!_rebuildRequested)
            return;
        _rebuildRequested = false;
        RebuildFromAssetDocument();
    }

    internal static Entity? FindAuthoredRoot(
        SceneDocument? document,
        Guid rootEntityId) =>
        rootEntityId == Guid.Empty
            ? null
            : document?.Scene?.FindEntity(rootEntityId);

    private void RebuildFromAssetDocument()
    {
        if (_synchronizing || _disposed)
            return;
        var assetDocument = _assetEditing.Current;
        if (assetDocument?.Instance is not EntityBlueprint blueprint ||
            (_openAssetId != default && assetDocument.Asset.Id != _openAssetId))
        {
            CloseDocument();
            return;
        }

        try
        {
            var selectedIds = Selection.EntityIds.ToArray();
            var clone = DreambitJson.Deserialize<EntityBlueprint>(assetDocument.CaptureJson())
                        ?? throw new InvalidDataException("The Blueprint file is empty.");
            clone.AssetId = assetDocument.Asset.Id;
            clone.AssetName = assetDocument.Asset.LogicalAssetName;
            var rootEntityId = clone.Guid;
            var replacement = new SceneDocument(
                new SceneBlueprint
                {
                    Name = clone.Name,
                    Entities = [clone]
                },
                null,
                Selection,
                _reportError,
                ResolveBlueprintInstance,
                historyOwnership: SceneDocumentHistoryOwnership.External,
                sceneFactory: static () => new BlueprintEditorScene());
            replacement.Changed += OnSceneDocumentChanged;
            var previous = Current;
            Current = replacement;
            _rootEntityId = rootEntityId;
            DisposeDocument(previous);
            Selection.Restore(selectedIds);
            Selection.RemoveMissing(Current.Scene);
            Error = null;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _reportError?.Invoke("Could not open the Blueprint hierarchy.", exception);
            // Keep the previous preview intact when replacement construction fails. It remains
            // the only live object graph until a new preview has fully materialized; disposing it
            // here would turn a transient resolver/load failure into lost editor context.
        }
    }

    private void OnSceneDocumentChanged(SceneDocument sceneDocument)
    {
        var assetDocument = _assetEditing.Current;
        if (_synchronizing || assetDocument?.Instance is not EntityBlueprint ||
            assetDocument.Asset.Id != _openAssetId)
            return;
        try
        {
            _synchronizing = true;
            assetDocument.ReplaceBlueprint(
                "Edit Blueprint Hierarchy",
                sceneDocument.CaptureSingleRoot(),
                sceneDocument.ActiveChangeMergeKey);
            _assetRevisionAlreadyApplied = assetDocument.Revision;
            Error = null;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _rebuildRequested = true;
            _reportError?.Invoke("Could not update the Blueprint asset from its hierarchy.", exception);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private EntityBlueprint ResolveBlueprintInstance(BlueprintInstanceReference instance) =>
        _blueprintSources.Resolve(instance);

    private void OnAssetEditingChanged()
    {
        if (_assetEditing.Current?.Asset.Id == _openAssetId)
        {
            RebuildFromAssetDocument();
            return;
        }

        if (_assetEditing.Selected?.Id == _openAssetId)
        {
            SuspendDocumentPreservingSelection();
            return;
        }

        CloseDocument();
    }

    private void SuspendDocumentPreservingSelection()
    {
        var current = Current;
        Current = null;
        _rootEntityId = Guid.Empty;
        DisposeDocument(current);
    }

    private void OnAssetPreviewChanged(DreambitAssetDocument document)
    {
        if (_synchronizing || document.Asset.Id != _openAssetId)
            return;
        if (document.Revision == _assetRevisionAlreadyApplied)
        {
            _assetRevisionAlreadyApplied = -1;
            return;
        }

        _assetRevisionAlreadyApplied = -1;
        RebuildFromAssetDocument();
    }

    private void OnAssemblyReloading(LoadedGameAssembly? _)
    {
        var current = Current;
        Current = null;
        DisposeDocument(current);
    }

    private void OnAssemblyReloaded(LoadedGameAssembly _) => RebuildFromAssetDocument();

    private void CloseDocument()
    {
        var current = Current;
        Current = null;
        _rootEntityId = Guid.Empty;
        _assetRevisionAlreadyApplied = -1;
        Selection.Clear();
        DisposeDocument(current);
    }

    private void DisposeDocument(SceneDocument? document)
    {
        if (document is null)
            return;
        document.Changed -= OnSceneDocumentChanged;
        var cleanupFailure = EditorDisposal.TryDispose(document);
        if (cleanupFailure is not null)
        {
            _reportError?.Invoke(
                "Could not fully dispose the previous Blueprint preview scene.\n" + cleanupFailure,
                null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _assetEditing.Changed -= OnAssetEditingChanged;
        _assetEditing.PreviewChanged -= OnAssetPreviewChanged;
        _assemblies.Reloading -= OnAssemblyReloading;
        _assemblies.Reloaded -= OnAssemblyReloaded;
        CloseDocument();
    }
}
