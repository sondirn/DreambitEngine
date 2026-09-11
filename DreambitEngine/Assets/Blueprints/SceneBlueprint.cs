using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dreambit.Tiled;
using Dreambit.ECS;
using Newtonsoft.Json;

namespace Dreambit;

[DreambitAssetType(
    "dreambit.blueprint.scene",
    FileExtension = DreambitAssetFileExtensions.SceneBlueprint)]
public class SceneBlueprint : DreambitAsset
{
    [DreambitSerialize]
    [JsonProperty("name", Required =  Required.Always)]
    public string Name { get; set; } = string.Empty;

    [DreambitSerialize]
    [JsonProperty("entities")] public List<EntityBlueprint> Entities { get; set; } = [];

    [DreambitSerialize]
    [JsonProperty("tiled")]
    public TiledSceneReference Tiled { get; set; }

    [DreambitSerialize]
    [JsonProperty("settings")]
    public SceneSettings Settings { get; set; } = new();

    /// <summary>
    /// Materializes source-owned content before ordinary authored entities. The
    /// blueprint owns source metadata while each integration owns its host contract.
    /// </summary>
    internal void MaterializeLinkedSources(Scene scene, SceneBlueprintLoadOptions options)
    {
        if (Tiled is not null)
            TiledSceneBlueprintMaterializer.Materialize(scene, Tiled, options);
    }

    /// <summary>
    /// Materializes linked runtime content directly under an additive content owner. This path
    /// deliberately bypasses the singular Tiled Scene service used by primary maps.
    /// </summary>
    internal TiledMapInstance? MaterializeAdditiveLinkedSources(
        Scene scene,
        SceneContentLoadOptions options,
        SceneContentInstance owner)
    {
        return Tiled is null
            ? null
            : TiledSceneBlueprintMaterializer.MaterializeAdditive(
                scene,
                Tiled,
                owner,
                options.TiledMapResolver,
                options.TiledMapImporter);
    }
}
