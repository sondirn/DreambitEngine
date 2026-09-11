using System;
using Dreambit.Tiled;

namespace Dreambit;

/// <summary>Controls how a serialized scene is materialized into a live scene.</summary>
public sealed class SceneBlueprintLoadOptions
{
    public static SceneBlueprintLoadOptions Runtime { get; } = new();

    public static SceneBlueprintLoadOptions Editor { get; } = new()
    {
        AllowMissingComponentTypes = true,
        PreserveEntityIds = true,
        TolerateComponentLoadErrors = true,
        MarkImportedTiledEntitiesEditorOnly = true
    };

    /// <summary>
    /// Keeps entities and their serialized component payloads loadable when a component
    /// assembly is temporarily unavailable. Missing components are omitted from the live ECS.
    /// </summary>
    public bool AllowMissingComponentTypes { get; init; }

    /// <summary>Uses serialized entity GUIDs as live entity IDs.</summary>
    public bool PreserveEntityIds { get; init; } = true;

    /// <summary>
    /// Keeps an editor scene open when a known component has stale members, invalid
    /// references, or cannot currently be constructed. Runtime loading remains strict.
    /// </summary>
    public bool TolerateComponentLoadErrors { get; init; }

    /// <summary>
    /// Optional host-specific resolver for boxed Blueprint instances. The runtime falls back to
    /// Resources; editors can resolve directly from source files before a bake completes.
    /// </summary>
    public Func<BlueprintInstanceReference, EntityBlueprint> BlueprintInstanceResolver { get; init; }

    /// <summary>Optional source-aware Tiled resolver used by editor hosts before a bake completes.</summary>
    public Func<TiledSceneReference, TmxMap> TiledMapResolver { get; init; }

    /// <summary>Keeps regenerated Tiled-owned entities out of serialized Dreambit entity data.</summary>
    public bool MarkImportedTiledEntitiesEditorOnly { get; init; }

    public bool ApplySceneSettings { get; set; } = true;
}
