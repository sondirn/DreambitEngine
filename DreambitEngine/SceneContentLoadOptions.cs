using System;
using Dreambit.Tiled;

namespace Dreambit;

/// <summary>Controls one additive Scene Blueprint materialization.</summary>
public sealed class SceneContentLoadOptions
{
    /// <summary>
    /// Applies the source Blueprint's global Scene settings. Additive content leaves
    /// Scene-wide settings unchanged by default.
    /// </summary>
    public bool ApplySceneSettings { get; init; }

    // Source-aware resolvers are intentionally internal. Runtime callers use Resources;
    // editor-hosted additive loading is prohibited, while tests can provide in-memory assets.
    internal Func<BlueprintInstanceReference, EntityBlueprint>? BlueprintInstanceResolver { get; init; }
    internal Func<TiledSceneReference, TmxMap>? TiledMapResolver { get; init; }
    internal TiledMapImporter? TiledMapImporter { get; init; }
}
