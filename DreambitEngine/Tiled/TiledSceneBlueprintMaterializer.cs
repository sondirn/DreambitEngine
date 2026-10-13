#nullable enable

using System;
using System.Collections.Generic;
using Dreambit.ECS;

namespace Dreambit.Tiled;

/// <summary>
/// Implemented only by scene types that deliberately host a Tiled-linked scene
/// blueprint. Ordinary Scene remains unaware of Tiled map ownership.
/// </summary>
internal interface ITiledSceneBlueprintHost
{
    void ValidateTiledBlueprint(TiledSceneReference reference);
    void ConfigureTiledBlueprint(TiledSceneLoadConfiguration configuration);
}

internal sealed record TiledSceneLoadConfiguration(
    TmxMap Map,
    TiledImportOptions ImportOptions,
    TiledSceneReference? Reference,
    bool MarkGeneratedEntitiesEditorOnly);

/// <summary>
/// Owns the lifetime of a materialized Tiled map independently of the base Scene.
/// It is a scene service so every execution mode invalidates the runtime handle
/// during ordinary entity cleanup.
/// </summary>
internal sealed class TiledMapSceneService : SceneServiceComponent
{
    public TiledMapSceneService()
    {
    }

    public TmxMap? Map { get; private set; }
    public TiledMapInstance? MapInstance { get; private set; }
    public bool IsMapLoaded => MapInstance is { IsUnloaded: false };

    internal TiledMapInstance Load(
        TiledSceneLoadConfiguration configuration,
        TiledMapImporter importer)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(importer);
        if (IsMapLoaded)
            throw new InvalidOperationException(
                $"Scene '{Entity.Scene.GetType().FullName}' already has a loaded Tiled map.");

        var instance = importer.Import(
            Entity.Scene,
            configuration.Map,
            configuration.ImportOptions.Clone());
        try
        {
            if (configuration.Reference is { } reference)
            {
                TiledGeneratedEntityOverrides.Apply(
                    instance.OwnedEntities,
                    reference.EntityOverrides ??
                    new Dictionary<string, TiledGeneratedEntityOverride>());
            }

            if (configuration.MarkGeneratedEntitiesEditorOnly)
                foreach (var entity in instance.OwnedEntities)
                    entity.IsEditorOnly = true;

            Map = configuration.Map;
            MapInstance = instance;
            return instance;
        }
        catch
        {
            instance.Unload();
            throw;
        }
    }

    internal bool Unload()
    {
        var instance = MapInstance;
        if (instance is null || instance.IsUnloaded)
        {
            MapInstance = null;
            return false;
        }

        MapInstance = null;
        instance.Unload();
        return true;
    }

    protected override void OnServiceDisposing()
    {
        Unload();
        base.OnServiceDisposing();
    }
}

internal static class TiledSceneBlueprintMaterializer
{
    public static void Materialize(
        Scene scene,
        TiledSceneReference reference,
        SceneBlueprintLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(options);

        if (scene is not ITiledSceneBlueprintHost host)
        {
            throw new InvalidOperationException(
                $"Scene blueprint linked to Tiled map '{reference.AssetName}' cannot be loaded " +
                $"into scene type '{scene.GetType().FullName}'. Runtime scene types for " +
                "Tiled-linked blueprints must derive from TiledScene. Load the asset with " +
                "Scene.SetNextScene<TiledSceneSubclass>(sceneAssetName).");
        }

        host.ValidateTiledBlueprint(reference);
        host.ConfigureTiledBlueprint(CreateConfiguration(
            reference,
            options.TiledMapResolver,
            options.MarkImportedTiledEntitiesEditorOnly));
    }

    /// <summary>
    /// Imports a fresh map instance directly into an existing Scene. The generic content owner,
    /// rather than the singular primary-map Scene service, owns its lifetime.
    /// </summary>
    internal static TiledMapInstance MaterializeAdditive(
        Scene scene,
        TiledSceneReference reference,
        SceneContentInstance owner,
        Func<TiledSceneReference, TmxMap>? mapResolver = null,
        TiledMapImporter? importer = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(owner.Scene, scene))
            throw new ArgumentException(
                "The additive content owner belongs to another Scene.",
                nameof(owner));

        var configuration = CreateConfiguration(reference, mapResolver, false);
        importer ??= new TiledMapImporter();
        var instance = importer.Import(
            scene,
            configuration.Map,
            configuration.ImportOptions.Clone());
        try
        {
            instance.BindContentOwner(owner);
            TiledGeneratedEntityOverrides.Apply(
                instance.OwnedEntities,
                reference.EntityOverrides ??
                new Dictionary<string, TiledGeneratedEntityOverride>());
            return instance;
        }
        catch (Exception materializationException)
        {
            try
            {
                instance.Unload();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Additive Tiled materialization and cleanup both failed.",
                    materializationException,
                    cleanupException);
            }

            throw;
        }
    }

    internal static TiledMapSceneService GetOrCreateLifetimeService(Scene scene)
    {
        if (scene.Services.TryGet<TiledMapSceneService>(out var existing))
            return existing;

        var entity = scene.CreateEntity("__dreambit-tiled-map-lifetime");
        entity.IsEditorOnly = scene.ExecutionMode == SceneExecutionMode.Editor;
        return entity.AttachComponent<TiledMapSceneService>();
    }

    private static TiledSceneLoadConfiguration CreateConfiguration(
        TiledSceneReference reference,
        Func<TiledSceneReference, TmxMap>? mapResolver,
        bool markGeneratedEntitiesEditorOnly)
    {
        var map = (mapResolver ?? ResolveTiledMap)(reference)
                  ?? throw new InvalidOperationException(
                      $"Tiled map asset '{reference.AssetName}' could not be loaded.");
        var importOptions = (reference.ImportOptions ?? new TiledImportOptions()).Clone();
        importOptions.Validate();
        return new TiledSceneLoadConfiguration(
            map,
            importOptions,
            reference,
            markGeneratedEntitiesEditorOnly);
    }

    private static TmxMap ResolveTiledMap(TiledSceneReference reference)
    {
        var assetName = reference.AssetName;
        if (reference.AssetId != Guid.Empty &&
            Resources.AssetRegistry?.TryResolveAssetName(
                new AssetId(reference.AssetId),
                out var resolvedName) == true)
        {
            assetName = resolvedName;
        }

        return string.IsNullOrWhiteSpace(assetName)
            ? null!
            : Resources.LoadAsset<TmxMap>(assetName);
    }
}
