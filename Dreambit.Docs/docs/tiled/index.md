# Tiled integration

Dreambit imports Tiled maps from the XML-based `.tmx` format and external
`.tsx` tilesets. A Tiled map is treated as a tile-authoring source: tile layers
become transient Dreambit entities and `TilemapRenderer` components, while
gameplay entities remain authored in Dreambit Editor.

## Supported maps

The importer supports:

- orthogonal fixed-size and infinite maps;
- embedded and external tilesets;
- atlas tilesets and image-collection tilesets;
- XML tile elements, CSV, and Base64 layer data;
- uncompressed, gzip, and zlib Base64 data;
- Tiled horizontal, vertical, and diagonal tile transforms;
- all four orthogonal render orders;
- nested groups, offsets, visibility, opacity, tint, and map background color;
- animated tiles using each frame's Tiled duration.

Object layers and image layers are intentionally not materialized. Create
gameplay entities, triggers, colliders, and decorations in Dreambit Editor
instead. Isometric, staggered, and hexagonal maps are rejected. Non-normal
layer blend modes, embedded image data, and zstd-compressed layer data are not
currently supported.

## Content layout

Keep the map, tilesets, and referenced images beneath the game's raw `Assets`
directory. Paths remain relative to the `.tmx` or `.tsx` file that owns them.

```text
Assets/
  maps/
    world.tmx
    tiles/
      terrain.tsx
  textures/
    terrain.png
```

The Asset Baker writes `.tmx` and `.tsx` sources as `.xmlb` resources and
preserves their logical directory structure. The map above is loaded with the
extensionless logical name `"maps/world"`.

## Dreambit Editor workflow

Choose **File > New Tiled Scene** and select a `.tmx` asset. The creation popup
and linked-scene Inspector expose `TiledImportOptions`:

- `PixelsPerUnit`;
- `BaseDrawLayer` and `DrawLayerStep`;
- `WorldDepth` and `WorldDepthDrawLayerStride`;
- `RenderMapBackgroundColor`;
- `IncludeInvisibleLayers`.

Generated nodes appear with a `[Tiled]` prefix. Their hierarchy and renderer
structure remain owned by the source map, but Dreambit-side names, enabled
state, tags, transforms, and serialized component values are stored as stable
overrides and re-applied after import. Saving the `.tmx` or a referenced `.tsx`
triggers live reimport; **Reimport Tiled Now** performs the same rebuild
manually. Dreambit-authored entities remain unchanged.

## Runtime scene

Derive from `TiledScene` when a code-authored scene owns one map:

```csharp
using Dreambit.Tiled;

public sealed class OverworldScene : TiledScene
{
    public OverworldScene()
        : base("maps/world")
    {
    }

    protected override TiledImportOptions CreateTiledImportOptions() => new()
    {
        PixelsPerUnit = 16f,
        BaseDrawLayer = -100,
        DrawLayerStep = 10,
        WorldDepth = 0,
        WorldDepthDrawLayerStride = 1000
    };

    protected override void OnTiledMapLoaded(TiledMapInstance map)
    {
        if (map.TryGetTileLayer("Foreground", out var foreground))
        {
            var foregroundDrawLayer = map.GetDrawLayer(foreground);
        }
    }
}
```

`TiledMapInstance` owns the generated entities and renderers. `Unload` removes
only that imported hierarchy. `TrackEntity` can attach a runtime-created
entity to the same lifetime, and `ApplyDrawLayer` copies a Tiled layer's
resolved Dreambit draw layer to an entity hierarchy.

### Multiple maps through additive Scene content

A Tiled-linked Scene Blueprint can also be loaded additively into an ordinary runtime `Scene`:

```csharp
var village = Scene.Instance.LoadAdditive("Scenes/Zones/Village.scene");
var tree = Scene.Instance.LoadAdditive("Scenes/Zones/AncientTree.scene");

TiledMapInstance villageMap = village.TiledMap!;
TiledMapInstance treeMap = tree.TiledMap!;

Scene.Instance.Unload(village);
```

Each `SceneContentInstance` owns a fresh `TiledMapInstance`. Generated entities, renderers,
colliders, runtime tile edits, Automapping output, generated-entity overrides, and runtime layer
handles remain isolated per instance. Loading the same Scene Blueprint twice creates two independent
maps; unloading one does not scan or destroy entities owned by the other.

Entities passed to an additive map's `TrackEntity` join the enclosing `SceneContentInstance`
lifetime. Additive unload invalidates the Tiled instance and its runtime layer handles before
destroying the complete owned entity set. A failure after import runs the same cleanup transaction,
so a partially materialized map cannot leak renderers or generated entities.

This additive path does not install a second `TiledMapSceneService` and does not replace a
`TiledScene.MapInstance`. A legacy primary `TiledScene` map and any number of additive maps can
coexist in the same Scene. The existing one-primary-map rule still applies to `TiledScene` and
`LoadIntoSelf` itself.

Additive Tiled entities are runtime-only editor state. Their map-local source keys are excluded from
document source identity and override recording, so edits to them cannot be saved into the open
Scene Blueprint.

### Loading a scene authored from Tiled in Dreambit Editor

A saved editor scene linked to a Tiled map must be loaded into a `TiledScene`
subclass. Use the parameterless base constructor because the scene blueprint
already contains the map asset and import options:

```csharp
using Dreambit.Tiled;

public sealed class OverworldScene : TiledScene
{
    public OverworldScene() : base()
    {
    }

    protected override void OnTiledMapLoaded(TiledMapInstance map)
    {
        var ground = map.GetRuntimeTileLayer("Ground");
        var terrain = map.GetTileset("tiles/terrain");
        ground.SetTile(12, 8, terrain.GetTile(4));
    }
}

Scene.SetNextScene<OverworldScene>("scenes/overworld.scene");
```

For a synchronized network Scene, register the same typed host and Scene asset on every peer:

```csharp
network.Scenes.RegisterBlueprint<OverworldScene>(
    "overworld",
    "scenes/overworld.scene");
```

The network catalog eagerly creates the authored `OverworldScene` while it is still in `Created`;
the linked map is then imported during Scene initialization before authored network objects bind.

The generic scene type is required for full Scene creation and `LoadIntoSelf`. Loading a
Tiled-linked scene asset through `Scene.SetNextScene("scenes/overworld.scene")`, or merging it into
an ordinary `Scene` with `LoadIntoSelf`, throws an actionable error before the map is resolved or
authored entities are materialized. `LoadAdditive` is the explicit ordinary-Scene path for an
independently owned linked map. Plain scene blueprints remain loadable through either path.

Use either a primary map supplied to `TiledScene(string mapAssetName)` or a primary map linked by the
Scene Blueprint. Combining those primary sources, loading a second primary linked map with
`LoadIntoSelf`, or adding the primary link after the Scene starts is rejected. Independent linked
maps use `LoadAdditive` instead. The Tiled-owned Scene lifetime invalidates the primary `MapInstance`,
all additive map instances, and runtime layer handles when the Scene is disposed. Dreambit Editor
uses an internal Tiled-capable preview host, so creation, live reimport, generated-entity overrides,
and selection recovery continue without requiring game code to run in the editor.

## Mutable runtime tile layers

Every imported tile layer has sparse mutable runtime state, including layers
that were empty in the source map. A tile is identified by its normalized
tileset asset name, tileset-local ID, and Tiled flip flags instead of a
map-specific GID.

```csharp
protected override void OnTiledMapLoaded(TiledMapInstance map)
{
    var ground = map.GetRuntimeTileLayer("Ground");
    var terrain = map.GetTileset("tiles/terrain");

    using (map.BeginTileEdit())
    {
        ground.SetTile(-12, 8, terrain.GetTile(4));
        ground.SetTile(-11, 8, terrain.GetTile(
            5,
            TmxTileFlipFlags.Horizontal));
        ground.ClearTile(-10, 8);
    }

    TiledTileReference? current = ground.GetTile(-12, 8);
}
```

`BeginTileEdit` is nestable. The outer scope runs Automapping once for the
logical changes and replaces each affected 32-by-32 render chunk once.
Untouched chunk objects and their static renderer caches remain valid. Direct
single-cell calls implicitly create their own edit scope.

Coordinates are logical Tiled cell coordinates and can be negative. Runtime
overrides, generated Automapping output, and source cells remain separate, so
retracting a generated tile reveals the authored or gameplay value beneath it.
These changes live only on `TiledMapInstance`; Dreambit never writes them back
to the `.tmx` or `.tsx` source.

## Runtime Automapping

The Asset Baker discovers `.tiled-project` files from the Dreambit project root
and reads each project's `folders` and `automappingRulesFile`. Rules can point
directly to a rule-map `.tmx` or to Tiled `rules.txt` files. Nested rule lists,
comments, and filename filter sections are resolved at bake time. A
same-directory `rules.txt` beside a map takes precedence over project rules.

The baker compiles applicable rules into one internal runtime catalog. Rule
maps and project metadata are not parsed during gameplay, and a missing or
invalid referenced rule file fails the bake with its path. Once loaded,
changing an input tile evaluates only rule origins whose input footprint can
touch the changed cell; it does not scan the whole map.

Dreambit supports modern Tiled tile-layer Automapping, including:

- `input`, `inputnot`, indexed input alternatives, and multiple target layers;
- `output` and indexed weighted output alternatives;
- `Empty`, `NonEmpty`, `Other`, negation, and ignored flip flags;
- `AutomappingRadius`, `MatchInOrder`, border matching/wrapping/overflow;
- rule probability, modulo/offset, disabled rules, lock handling,
  non-overlapping output, and `DeleteTiles`;
- fixed and infinite maps, negative coordinates, groups, and deterministic
  output controlled by `TiledImportOptions.AutomappingSeed`.

The embedded `qrc:/automap-tiles.tsx` match tiles used by current Tiled
versions are recognized without requiring a file on disk. `Other` follows
Tiled 1.10+ behavior: it also matches an empty cell unless the same rule has
an explicit `Empty` predicate for that target layer.

Output target layers must already exist in the gameplay map so their draw order
and renderer ownership are defined. Legacy region-based rules and object-layer
outputs are rejected with an actionable bake error. Rule maps use Dreambit's
orthogonal TMX pipeline; TMJ rule maps are not compiled. This keeps the
compiled runtime representation small and tile-focused.

Editor-authored scenes store a `TiledSceneReference` in the scene blueprint.
When loaded into a `TiledScene` host, runtime blueprint loading resolves that
reference, imports fresh tile data, and applies the saved Dreambit overrides
before the scene starts.

## Rendering and large maps

Infinite-map tile layers retain Tiled's source chunks. Fixed-size layers are
partitioned into sparse 32-by-32-cell chunks. `TilemapRenderer` culls occupied
chunks first, so empty space between distant parts of a map is not scanned one
cell at a time.

Static chunk contents are cached lazily when a tile becomes 12 screen pixels or
smaller. Animated tiles remain dynamic and are drawn over the cached static
content. Cache resolution follows a power-of-two screen-space LOD, so a chunk
first seen at a distant zoom does not consume a full-resolution texture. Cache
creation is limited to four chunks per frame, 256 retained chunks, and 64 MB per
renderer by default. These values can be tuned on each generated
`TilemapRenderer` through `ChunkCacheScreenSizeThreshold`,
`MaximumChunkCachesBuiltPerFrame`, `MaximumCachedChunks`, and
`MaximumChunkCacheMegabytes`; set
`EnableChunkCaching` to false for a layer that must always draw individual
tiles. Custom-effect tilemaps skip caching automatically.

For profiling, inspect `LastVisibleChunkCount`, `LastCandidateTileCount`,
`LastVisibleTileCount`, `LastSpriteSubmissionCount`, and
`FrameSpriteSubmissionCount`. Once the cache is warm, a static visible chunk
costs one sprite submission in each render pass instead of one per tile.
