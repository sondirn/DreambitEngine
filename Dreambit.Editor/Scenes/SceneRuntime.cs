using Dreambit.ECS;
using Dreambit.Tiled;

namespace Dreambit.Editor.Scenes;

/// <summary>
/// Owns the materialized editor scene independently from a document's authored source and UI state.
/// </summary>
internal sealed class SceneRuntime : IDisposable
{
    private readonly Action<string, Exception?>? _reportError;
    private readonly Func<BlueprintInstanceReference, EntityBlueprint>? _blueprintInstanceResolver;
    private readonly Func<TiledSceneReference, TmxMap>? _tiledMapResolver;
    private readonly Func<EditorScene>? _sceneFactory;

    public SceneRuntime(
        Action<string, Exception?>? reportError = null,
        Func<BlueprintInstanceReference, EntityBlueprint>? blueprintInstanceResolver = null,
        Func<TiledSceneReference, TmxMap>? tiledMapResolver = null,
        Func<EditorScene>? sceneFactory = null)
    {
        _reportError = reportError;
        _blueprintInstanceResolver = blueprintInstanceResolver;
        _tiledMapResolver = tiledMapResolver;
        _sceneFactory = sceneFactory;

        EditorLoadOptions = CreateEditorLoadOptions();
    }

    public EditorScene? Scene { get; private set; }
    public bool HasLiveScene => Scene is not null;
    public int Generation { get; private set; }
    public SceneBlueprintLoadOptions EditorLoadOptions { get; }

    public EditorScene Build(SceneBlueprint source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var scene = (_sceneFactory?.Invoke() ??
                     (source.Tiled is null ? new EditorScene() : new TiledEditorScene()))
                    ?? throw new InvalidOperationException(
                        "The editor scene factory returned null.");

        try
        {
            scene.LoadIntoSelf(source, EditorLoadOptions);
            scene.FlushStructuralChanges();
            return scene;
        }
        catch
        {
            // A failed materialization must not retain a partial live scene, but cleanup
            // failure must not replace the original parser/materialization exception.
            EditorDisposal.TryDispose(scene);
            throw;
        }
    }

    public void Replace(EditorScene replacement, string cleanupMessage)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Scene;
        Scene = replacement;
        Generation++;
        ReportCleanupFailure(previous, cleanupMessage);
    }

    public void Release(string cleanupMessage)
    {
        var scene = Scene;
        Scene = null;
        Generation++;
        ReportCleanupFailure(scene, cleanupMessage);
    }

    public void Dispose()
    {
        var scene = Scene;
        Scene = null;
        Generation++;
        scene?.Dispose();
    }

    public SceneBlueprintLoadOptions CreateEditorLoadOptions(
        bool applySceneSettings = true) => new()
    {
        AllowMissingComponentTypes = true,
        PreserveEntityIds = true,
        TolerateComponentLoadErrors = true,
        BlueprintInstanceResolver = _blueprintInstanceResolver,
        TiledMapResolver = _tiledMapResolver,
        MarkImportedTiledEntitiesEditorOnly = true,
        ApplySceneSettings = applySceneSettings
    };

    private void ReportCleanupFailure(EditorScene? scene, string message)
    {
        var cleanupFailure = EditorDisposal.TryDispose(scene);
        if (cleanupFailure is not null)
            _reportError?.Invoke(message + "\n" + cleanupFailure, null);
    }
}
