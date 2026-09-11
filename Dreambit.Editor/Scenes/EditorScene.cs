using Dreambit.Tiled;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Scenes;

internal class EditorScene : Scene
{
    public EditorScene() : base(SceneExecutionMode.Editor)
    {
    }

    protected override void OnInitialize()
    {
        MainCamera.SetTargetVerticalResolution(16f);
        MainCamera.PixelSnap = true;
        MainCamera.PixelPerfectPixelsPerUnit = 16f;
    }
}


/// <summary>
/// Editor-only host for scene blueprints linked to Tiled maps. Runtime games
/// use a TiledScene subclass; the editor retains its EditorScene hierarchy.
/// </summary>
internal sealed class TiledEditorScene : EditorScene, ITiledSceneBlueprintHost
{
    private TiledMapSceneService? _mapService;

    void ITiledSceneBlueprintHost.ValidateTiledBlueprint(TiledSceneReference reference)
    {
        if (_mapService is not null)
            throw new InvalidOperationException("The editor scene already hosts a Tiled map.");
    }

    void ITiledSceneBlueprintHost.ConfigureTiledBlueprint(
        TiledSceneLoadConfiguration configuration)
    {
        var service = TiledSceneBlueprintMaterializer.GetOrCreateLifetimeService(this);
        service.Load(configuration, new TiledMapImporter());
        _mapService = service;
    }
}
