using Dreambit.Editor.Compilation;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Assets;

namespace Dreambit.Editor.Scenes;

internal sealed class SceneDocumentService : IDisposable
{
    private readonly DreambitProjectDefinition _project;
    private readonly GameAssemblyLoadService _assemblies;
    private readonly AssetDatabase _assets;
    private readonly BlueprintSourceService _blueprintSources;
    private readonly Action<string, Exception?>? _reportError;
    private bool _disposed;

    public SceneDocumentService(
        DreambitProjectDefinition project,
        GameAssemblyLoadService assemblies,
        AssetDatabase assets,
        BlueprintSourceService blueprintSources,
        Action<string, Exception?>? reportError = null)
    {
        _project = project;
        _assemblies = assemblies;
        _assets = assets;
        _blueprintSources = blueprintSources;
        _reportError = reportError;
        Selection = new SelectionService();
        _blueprintSources.Changed += OnBlueprintSourcesChanged;
        _assemblies.Reloading += OnAssemblyReloading;
        _assemblies.Reloaded += OnAssemblyReloaded;
    }

    public SceneDocument? Current { get; private set; }
    public SelectionService Selection { get; }
    public event Action<SceneDocument?>? CurrentChanged;

    public SceneDocument New(string name = "Untitled")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replacement = SceneDocument.CreateNew(
            name,
            Selection,
            _reportError,
            ResolveBlueprintInstance,
            activeGameAssemblyNameProvider: GetActiveGameAssemblyName);
        ReplaceCurrent(replacement);
        return replacement;
    }

    public SceneDocument Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = ResolveScenePath(path);
        var replacement = SceneDocument.Open(
            fullPath,
            Selection,
            _reportError,
            ResolveBlueprintInstance,
            activeGameAssemblyNameProvider: GetActiveGameAssemblyName);
        ReplaceCurrent(replacement);
        return replacement;
    }

    public void Save(string? path = null)
    {
        var document = Current ?? throw new InvalidOperationException("No scene is open.");
        document.Save(path is null ? null : ResolveScenePath(path));
    }

    private string? GetActiveGameAssemblyName() =>
        _assemblies.Current?.Assembly.GetName().Name;

    public string ResolveScenePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var contentRoot = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(_project.ContentRootPath));
        var resolved = System.IO.Path.GetFullPath(
            System.IO.Path.IsPathFullyQualified(path)
                ? path
                : System.IO.Path.Combine(contentRoot, path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var contentPrefix = contentRoot + System.IO.Path.DirectorySeparatorChar;
        if (!string.Equals(resolved, contentRoot, comparison) &&
            !resolved.StartsWith(contentPrefix, comparison))
        {
            throw new InvalidOperationException(
                "Scene paths must remain inside the project's raw Assets folder.");
        }

        return resolved;
    }

    public void Update(bool autoSave, TimeSpan autoSaveDelay)
    {
        Current?.Update(autoSave, autoSaveDelay);
    }

    public void ReloadContent()
    {
        if (Current is not null)
            Current.ReloadContent();
        else
            Resources.RefreshContent();
    }

    public void PreviewBlueprint(AssetRecord asset, EntityBlueprint blueprint)
        => _blueprintSources.SetPreview(asset, blueprint);

    public void ClearBlueprintPreviews() => _blueprintSources.ClearPreviews();

    /// <summary>
    /// Repairs scene documents after an Entity Blueprint has been removed. The open scene is
    /// changed through its undo history; unopened scene assets are updated on disk so a later
    /// load cannot retain a dangling Blueprint instance.
    /// </summary>
    public int RemoveDeletedBlueprintReferences(AssetRecord deletedAsset)
    {
        ArgumentNullException.ThrowIfNull(deletedAsset);
        if (deletedAsset.Kind != AssetKind.Blueprint)
            return 0;

        var removed = Current?.RemoveDeletedBlueprintInstances(
            deletedAsset.Id,
            deletedAsset.LogicalAssetName) ?? 0;
        var currentPath = Current?.Path;
        foreach (var sceneAsset in _assets.GetSnapshot().Assets.Where(asset =>
                     asset.Kind == AssetKind.Scene &&
                     !string.Equals(
                         ResolveScenePath(asset.RelativePath),
                         currentPath,
                         OperatingSystem.IsWindows()
                             ? StringComparison.OrdinalIgnoreCase
                             : StringComparison.Ordinal)))
        {
            var path = ResolveScenePath(sceneAsset.RelativePath);
            var source = SceneDocumentSerializer.Deserialize(File.ReadAllText(path));
            var removedFromScene = SceneDocument.RemoveBlueprintInstanceReferences(
                source.Entities,
                deletedAsset.Id,
                deletedAsset.LogicalAssetName);
            if (removedFromScene == 0)
                continue;

            WriteSceneAtomically(path, SceneDocumentSerializer.Serialize(source));
            removed += removedFromScene;
        }

        return removed;
    }

    public void Close()
    {
        var current = Current;
        Current = null;
        DisposeDocument(current);
        Selection.Clear();
        CurrentChanged?.Invoke(null);
    }

    private void ReplaceCurrent(SceneDocument replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Current;
        Current = replacement;
        DisposeDocument(previous);
        CurrentChanged?.Invoke(replacement);
    }

    private void DisposeDocument(SceneDocument? document)
    {
        if (document is null)
            return;
        var cleanupFailure = EditorDisposal.TryDispose(document);
        if (cleanupFailure is not null)
        {
            _reportError?.Invoke(
                "Could not fully dispose the previous editor scene.\n" + cleanupFailure,
                null);
        }
    }

    private void OnAssemblyReloading(LoadedGameAssembly? _) => Current?.BeforeAssemblyReload();
    private void OnAssemblyReloaded(LoadedGameAssembly _) => Current?.AfterAssemblyReload();
    private void OnBlueprintSourcesChanged() => Current?.RefreshBlueprintInstances();

    private EntityBlueprint ResolveBlueprintInstance(BlueprintInstanceReference instance) =>
        _blueprintSources.Resolve(instance);

    private static void WriteSceneAtomically(string path, string content)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _blueprintSources.Changed -= OnBlueprintSourcesChanged;
        _assemblies.Reloading -= OnAssemblyReloading;
        _assemblies.Reloaded -= OnAssemblyReloaded;
        Close();
    }
}
