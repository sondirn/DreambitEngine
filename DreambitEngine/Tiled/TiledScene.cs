#nullable enable

using System;

namespace Dreambit.Tiled;

/// <summary>
/// Base scene backed by one Tiled TMX map. It supports either a code-authored
/// map asset supplied to the constructor or a Tiled-linked SceneBlueprint.
/// The map is materialized into transient entities and unloaded with the scene.
/// </summary>
public class TiledScene : Scene, ITiledSceneBlueprintHost
{
    private readonly string? _mapAssetName;
    private readonly TiledMapImporter _importer = new();
    private TiledSceneLoadConfiguration? _configuration;
    private TiledMapSceneService? _mapService;

    /// <summary>
    /// Creates a host whose map must come from a Tiled-linked SceneBlueprint.
    /// Load it with Scene.SetNextScene&lt;TScene&gt;(sceneAssetName).
    /// </summary>
    protected TiledScene()
    {
    }

    /// <summary>Creates a code-authored scene backed directly by one TMX asset.</summary>
    protected TiledScene(string mapAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapAssetName);
        _mapAssetName = mapAssetName;
    }

    public TmxMap? Map => _configuration?.Map ?? _mapService?.Map;
    public TiledMapInstance? MapInstance => _mapService?.MapInstance;
    public bool IsMapLoaded => MapInstance is { IsUnloaded: false };

    protected sealed override void OnInitialize()
    {
        if (_configuration is null)
        {
            if (string.IsNullOrWhiteSpace(_mapAssetName))
            {
                throw new InvalidOperationException(
                    $"Tiled scene '{GetType().FullName}' has no configured map. Supply a map " +
                    "asset to the TiledScene constructor or load a Tiled-linked scene asset " +
                    "with Scene.SetNextScene<TiledSceneSubclass>(sceneAssetName).");
            }

            var manager = TiledManager.Instance;
            manager.Initialize(_mapAssetName);
            _configuration = new TiledSceneLoadConfiguration(
                manager.Map,
                CreateTiledImportOptions(),
                Reference: null,
                MarkGeneratedEntitiesEditorOnly: false);
        }

        OnBeforeTiledMapLoaded();
        LoadMap();
        OnTiledSceneReady();
    }

    protected sealed override void OnEnd()
    {
        try
        {
            OnTiledSceneEnding();
        }
        finally
        {
            UnloadMap();
        }
    }

    public TiledMapInstance LoadMap()
    {
        if (IsMapLoaded)
            return MapInstance!;
        var configuration = _configuration
                            ?? throw new InvalidOperationException(
                                "The Tiled scene has not configured a map yet.");
        configuration.ImportOptions.Validate();

        var service = TiledSceneBlueprintMaterializer.GetOrCreateLifetimeService(this);
        var instance = service.Load(configuration, _importer);
        _mapService = service;
        try
        {
            OnTiledMapLoaded(instance);
            return instance;
        }
        catch
        {
            service.Unload();
            throw;
        }
    }

    public bool UnloadMap()
    {
        if (!IsMapLoaded)
            return false;
        var instance = MapInstance!;
        OnTiledMapUnloading(instance);
        _mapService!.Unload();
        OnTiledMapUnloaded(Map!);
        return true;
    }

    void ITiledSceneBlueprintHost.ValidateTiledBlueprint(TiledSceneReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.IsNullOrWhiteSpace(_mapAssetName))
        {
            throw new InvalidOperationException(
                $"Tiled scene '{GetType().FullName}' is already configured for map asset " +
                $"'{_mapAssetName}' and cannot also load the Tiled-linked map " +
                $"'{reference.AssetName}'. Use either the constructor map or the " +
                "scene blueprint link, not both.");
        }
        if (_configuration is not null || IsMapLoaded)
        {
            throw new InvalidOperationException(
                $"Tiled scene '{GetType().FullName}' already has a linked map configuration " +
                "and cannot load another Tiled-linked scene blueprint.");
        }
        if (State != SceneState.Created)
        {
            throw new InvalidOperationException(
                "A Tiled-linked scene blueprint must be loaded before the TiledScene starts.");
        }
    }

    void ITiledSceneBlueprintHost.ConfigureTiledBlueprint(
        TiledSceneLoadConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    protected virtual TiledImportOptions CreateTiledImportOptions() => new();

    protected virtual void OnBeforeTiledMapLoaded()
    {
    }

    protected virtual void OnTiledMapLoaded(TiledMapInstance map)
    {
    }

    protected virtual void OnTiledMapUnloading(TiledMapInstance map)
    {
    }

    protected virtual void OnTiledMapUnloaded(TmxMap map)
    {
    }

    protected virtual void OnTiledSceneReady()
    {
    }

    protected virtual void OnTiledSceneEnding()
    {
    }
}
