# Runtime Tiled Automapping and Mutable Tilemaps — Implementation Report

## Outcome

Dreambit now provides first-class, runtime-only mutation of imported Tiled tile layers and incremental Tiled Automapping compiled during asset baking. Runtime tile identity is based on a normalized tileset asset name, a tileset-local tile ID, and Tiled flip flags, so rule maps and gameplay maps do not depend on their unrelated global GID assignments.

The implementation preserves the existing TMX/TSX source models and imported rendering hierarchy. It does not write runtime edits or generated output back to TMX, TSX, rule maps, rules lists, Tiled project metadata, baked PAKs, or bake-cache files.

LDtk runtime, editor, baker, test, template, and documentation integration has been removed. The former LDtk enum position is retained as `ReservedLegacyTilemap`, preserving the numeric value of `AssetKind.TiledMap`.

## Architecture

### Runtime map state

`TiledMapInstance` owns one `TiledRuntimeTileLayer` for every imported tile layer, including initially empty layers and layers not materialized for rendering. Each layer keeps three sparse states:

1. authored source cells;
2. explicit gameplay overrides;
3. generated Automapping contributions.

Generated state takes precedence while present. When a rule stops matching, its contribution is retracted and the next generated contribution—or the gameplay/authored cell underneath—is revealed. Editing an Automapping output cell explicitly removes generated ownership of that cell before applying the gameplay override.

A `TiledRuntimeTileset` validates local IDs and creates stable `TiledTileReference` values without duplicating texture ownership.

### Editor-authored Tiled scene hosting

An editor scene linked to a Tiled map now has an explicit runtime host contract without adding Tiled state or APIs to the base `Scene` class. `Scene.LoadIntoSelf` performs only generic blueprint loading and asks the blueprint to materialize any linked sources. The Tiled blueprint materializer then requires the destination to implement the internal Tiled host contract; game scenes satisfy that contract by deriving from `TiledScene`.

`TiledScene` supports two mutually exclusive modes: the existing direct-map constructor (`base("maps/world")`) and a protected parameterless constructor for maps supplied by an editor-authored scene blueprint. A linked blueprint is validated before its map resolver runs or any authored entity is materialized. Loading one into a plain `Scene`, combining a constructor map with a blueprint map, adding a second map link, or configuring the link after scene initialization all fail with actionable errors and no partial Tiled import.

A hidden Tiled scene service owns the imported `TiledMapInstance`. It unloads the map when its scene is disposed, destroys imported entities, and invalidates previously obtained runtime-layer handles. The editor selects an internal Tiled-aware `EditorScene` host whenever a blueprint has a Tiled source, so authoring and preview continue to work without making every editor or game scene Tiled-aware.

`Scene.CreateFromBlueprint<TScene>` provides the common eager-construction path for systems that need an authored Scene without scheduling a local transition. `NetworkSceneCatalog.RegisterBlueprint<TScene>` uses that factory on every peer, ensuring the typed host is configured while still in `Scene.Created`; the network startup gate binds authored identities only after Scene and Tiled initialization complete. Factory failures dispose the partially constructed Scene before propagating the error.

### Batched edits and rendering

`BeginTileEdit()` is nestable. The outer scope collects changed logical cells, runs Automapping to convergence, and rebuilds each dirty 32-by-32 render chunk once. Single-cell setters create an implicit one-operation edit scope.

Chunk coordinates use mathematical floor division, so negative cells and boundaries such as -33/-32/-1/0/31/32 map correctly. `TilemapLayerData` replaces only the affected chunk object. `TilemapRenderer` listens for that replacement and disposes only the old chunk cache, leaving unrelated cached chunks intact. Static and animated output tiles use the same existing import/render path as authored tiles.

### Bake-time Automapping compiler

The Asset Baker searches the supplied Dreambit project root for `.tiled-project` files, applies each project's `folders` and `automappingRulesFile`, and gives a same-directory `rules.txt` precedence. It resolves direct TMX rules, nested TXT lists, comments, wildcard filename filters, missing files, ambiguous projects, and list cycles.

Rule TMXs are normalized into an internal JSONB catalog named `__dreambit/tiled-automapping-catalog`. The catalog bytes participate in the incremental cache hash, so changes to project metadata, TXT lists, rule maps, and referenced TSX content invalidate the generated catalog while unchanged results reuse it. Gameplay loads the compiled catalog; it does not parse rule XML or project metadata.

At runtime, rules are indexed by input layer. A changed cell produces only the rule origins whose input footprints can touch it, expanded by `AutomappingRadius`. There is no whole-map scan and no Automapping object when a map has no compiled rules.

## Public runtime API

For a scene authored in Dreambit Editor from a Tiled map, derive the gameplay scene from `TiledScene` using its parameterless constructor, then load the scene asset through the typed scene transition:

```csharp
public sealed class OverworldScene : TiledScene
{
    public OverworldScene() : base()
    {
    }

    protected override void OnTiledMapLoaded(TiledMapInstance map)
    {
        var ground = map.GetRuntimeTileLayer("Ground");
        // Cache handles or apply initial gameplay edits here.
    }
}

Scene.SetNextScene<OverworldScene>("scenes/overworld.scene");
```

The scene blueprint supplies the linked TMX map and import options. The direct-map constructor remains available for scenes that are not loaded from an editor-authored blueprint.

```csharp
var map = tiledImporter.Import(scene, tmxMap, new TiledImportOptions
{
    AutomappingSeed = 1234
});

var ground = map.GetRuntimeTileLayer("Ground");
var terrain = map.GetTileset("tiles/terrain");

using (map.BeginTileEdit())
{
    ground.SetTile(-33, 4, terrain.GetTile(7));
    ground.SetTile(32, 4, terrain.GetTile(
        9,
        TmxTileFlipFlags.Horizontal));
    ground.ClearTile(0, 0);
}

TiledTileReference? current = ground.GetTile(-33, 4);
bool hasRules = map.HasAutomappingRules;

map.Unload(); // destroys owned entities and invalidates runtime layer handles
```

New or materially extended API:

- `Scene.CreateFromBlueprint/CreateFromBlueprint<TScene>`: eagerly materializes an editor-authored Scene into an unscheduled typed runtime host.
- `NetworkSceneCatalog.RegisterBlueprint/RegisterBlueprint<TScene>`: registers stable network Scene keys directly against editor-authored Scene assets.
- `TiledScene`: direct-map and blueprint-linked construction modes, `Map`, `MapInstance`, and Tiled lifecycle callbacks.
- `TiledTileReference`: normalized tileset asset identity, local tile ID, and flip flags.
- `TiledRuntimeTileset.GetTile/TryGetTile`: validated reference construction.
- `TiledRuntimeTileLayer.GetTile/TryGetTile/SetTile/ClearTile`: sparse logical cell access and mutation.
- `TiledMapInstance.RuntimeTileLayers/Tilesets/GetRuntimeTileLayer/TryGetRuntimeTileLayer/GetTileset/TryGetTileset/BeginTileEdit/HasAutomappingRules`.
- `TiledImportOptions.AutomappingSeed`: deterministic probability and indexed-output selection.
- `TilemapRenderer.ChunkCacheInvalidationCount`: targeted cache invalidation diagnostic.
- `AssetBakeRequest.ProjectRoot` and `AssetBlobBakeRequest.ProjectRoot`: project-level Tiled metadata discovery.

## Supported Automapping features

The compiled implementation follows the current Tiled 1.12 tile-oriented rule model:

- modern 1.9+ 8-way contiguous rule regions, ordered top-to-bottom and left-to-right;
- simultaneous matching by default and `MatchInOrder`;
- `input[not][index]_target`, duplicate alternative input layers, indexed OR conditions, and dummy empty missing input layers;
- `output[index]_target`, unconditional unindexed outputs, weighted indexed alternatives, and empty indexed alternatives excluded;
- `Empty`, `Ignore`, `NonEmpty`, `Other`, and `Negate`, including custom `MatchType` tiles;
- embedded `qrc:/automap-tiles.tsx` match tiles without an on-disk tileset;
- Tiled 1.10+ `Other` behavior, including empty unless `Empty` is explicitly used for the same target;
- `AutoEmpty`/`StrictEmpty`;
- horizontal, vertical, diagonal, and hex-120 ignored-flip properties (the map itself remains orthogonal);
- `DeleteTiles`, `AutomappingRadius`, `MatchOutsideMap`, `OverflowBorder`, `WrapBorder`, and `MatchInOrder`;
- map defaults and per-rule `rule_options`: `ModX`, `ModY`, `OffsetX`, `OffsetY`, `Probability`, `Disabled`, `NoOverlappingOutput`, and `IgnoreLock`;
- fixed and infinite maps, sparse/empty layers, nested layer groups, negative cells, independently assigned TMX GIDs, flips, and animated output.

Probabilistic decisions are deterministic for the map seed, rule map, rule, and origin.

## Deliberate restrictions

- Runtime Automapping is tile-layer only. Legacy `regions` rules and object-layer output fail the bake with an actionable error.
- Dreambit's Tiled pipeline accepts orthogonal TMX gameplay and rule maps. TMJ, isometric, staggered, and hexagonal maps are not compiled.
- Unlike Tiled Editor, Dreambit does not create a missing output layer at runtime. The output layer must exist in the gameplay TMX so draw order, visibility, transforms, and renderer ownership are explicit.
- Runtime mutation is intentionally ephemeral. There is no source-save API.
- Tiled object and image layers remain outside the imported runtime materialization path.
- Removing LDtk also removes the dormant LDtk-backed A* grid/path components and their documentation.

## Correctness and performance notes

- Stable tileset-local identities remove target/rule GID coupling.
- Mutation is sparse and chunk-local; it does not rebuild an entire layer.
- Batch scopes coalesce dirty render chunks and Automapping input changes.
- Generated-output provenance supports stale-output retraction and overlapping-rule fallback.
- Static and animated tile construction reuses the normal Tiled importer path.
- The source TMX/TSX object graphs remain unchanged.
- Maps without compiled rules allocate no runtime automapper and incur no per-frame Automapping work.
- Imported tile layers do retain sparse runtime cell dictionaries to support mutation; this is a deliberate load-time memory tradeoff and adds no per-frame scan.

## Verification

Final checks performed on 2026-08-30:

- `dotnet build DreambitEngine.sln --no-restore`: succeeded, 0 errors, 6 existing NuGet vulnerability warnings.
- Focused Tiled suite: 26 passed, 0 failed.
- Full solution: 356 editor tests plus 74 networking tests passed, 0 failed, 0 skipped (430 total).
- `scripts\publish-sdk.cmd 0.9.1`: produced all four local SDK packages and installed the Dreambit 0.9.1 project templates. `Dreambit-local` now resolves to the 0.9.1 package directory, and future local publishes update that source automatically. NuGet.org push was skipped.
- MkDocs clean build: succeeded. It reported the pre-existing `virtual-camera.md` navigation warning.
- `git diff --check`: no whitespace errors; Git emitted only line-ending conversion notices.
- Active source and documentation LDtk scan (excluding this report's historical file inventory): only the four deliberate negative assertions in `TiledImportTests` remain, verifying `.ldtk` and `.ldtkl` classify as unknown and are not baked.

## Complete file inventory

This is the complete working-tree inventory after implementation. The two-character prefix is Git's standard status: `M` modified, `D` deleted, and `??` added. Generated documentation-site files are listed individually.

```text
 M Dreambit.Docs/docs/ai/index.md
 D Dreambit.Docs/docs/ai/pathfinding.md
 M Dreambit.Docs/docs/assets/content-pipeline.md
 M Dreambit.Docs/docs/assets/index.md
 M Dreambit.Docs/docs/core/scenes.md
 D Dreambit.Docs/docs/ecs/components/astar-grid.md
 D Dreambit.Docs/docs/ecs/components/astar-path-follower.md
 D Dreambit.Docs/docs/ecs/components/astar-pathfinder.md
 D Dreambit.Docs/docs/ecs/components/tile-mover.md
 M Dreambit.Docs/docs/index.md
 D Dreambit.Docs/docs/ldtk/entities.md
 D Dreambit.Docs/docs/ldtk/index.md
 D Dreambit.Docs/docs/ldtk/loading-levels.md
 D Dreambit.Docs/docs/ldtk/monogame-conversions.md
 D Dreambit.Docs/docs/ldtk/quick-start.md
 M Dreambit.Docs/docs/networking/index.md
 M Dreambit.Docs/docs/tiled/index.md
 M Dreambit.Docs/mkdocs.yml
 M Dreambit.Docs/site/404.html
 M Dreambit.Docs/site/ai/fsm/index.html
 M Dreambit.Docs/site/ai/index.html
 D Dreambit.Docs/site/ai/pathfinding/index.html
 M Dreambit.Docs/site/assets/animations/index.html
 M Dreambit.Docs/site/assets/blueprints/index.html
 M Dreambit.Docs/site/assets/content-pipeline/index.html
 M Dreambit.Docs/site/assets/index.html
 M Dreambit.Docs/site/assets/particles/index.html
 M Dreambit.Docs/site/assets/resources/index.html
 M Dreambit.Docs/site/assets/sound-cues/index.html
 M Dreambit.Docs/site/assets/sprites/index.html
 M Dreambit.Docs/site/audio/index.html
 M Dreambit.Docs/site/core/core/index.html
 M Dreambit.Docs/site/core/coroutines/index.html
 M Dreambit.Docs/site/core/index.html
 M Dreambit.Docs/site/core/logging/index.html
 M Dreambit.Docs/site/core/scenes/index.html
 M Dreambit.Docs/site/core/time/index.html
 M Dreambit.Docs/site/core/window/index.html
 M Dreambit.Docs/site/ecs/blueprints/index.html
 M Dreambit.Docs/site/ecs/component-lifecycle/index.html
 M Dreambit.Docs/site/ecs/components/ambient-light-2d/index.html
 D Dreambit.Docs/site/ecs/components/astar-grid/index.html
 D Dreambit.Docs/site/ecs/components/astar-path-follower/index.html
 D Dreambit.Docs/site/ecs/components/astar-pathfinder/index.html
 M Dreambit.Docs/site/ecs/components/box-collider/index.html
 M Dreambit.Docs/site/ecs/components/camera2d/index.html
 M Dreambit.Docs/site/ecs/components/circle-drawer/index.html
 M Dreambit.Docs/site/ecs/components/collider/index.html
 M Dreambit.Docs/site/ecs/components/fsm/index.html
 M Dreambit.Docs/site/ecs/components/light2d/index.html
 M Dreambit.Docs/site/ecs/components/mover/index.html
 M Dreambit.Docs/site/ecs/components/particle-system-drawer/index.html
 M Dreambit.Docs/site/ecs/components/point-light-2d/index.html
 M Dreambit.Docs/site/ecs/components/poly-shape-collider/index.html
 M Dreambit.Docs/site/ecs/components/rect-drawer/index.html
 M Dreambit.Docs/site/ecs/components/rigid-body-2d/index.html
 M Dreambit.Docs/site/ecs/components/sound-effect-emitter/index.html
 M Dreambit.Docs/site/ecs/components/sound-emitter-2d/index.html
 M Dreambit.Docs/site/ecs/components/sound-listener-2d/index.html
 M Dreambit.Docs/site/ecs/components/sprite-animator/index.html
 M Dreambit.Docs/site/ecs/components/sprite-drawer/index.html
 D Dreambit.Docs/site/ecs/components/tile-mover/index.html
 M Dreambit.Docs/site/ecs/components/ui-frame/index.html
 M Dreambit.Docs/site/ecs/components/virtual-camera/index.html
 M Dreambit.Docs/site/ecs/entities/index.html
 M Dreambit.Docs/site/ecs/index.html
 M Dreambit.Docs/site/ecs/requirements/index.html
 M Dreambit.Docs/site/ecs/transform/index.html
 M Dreambit.Docs/site/ecs/writing-components/index.html
 M Dreambit.Docs/site/getting-started/first-game/index.html
 M Dreambit.Docs/site/getting-started/index.html
 M Dreambit.Docs/site/getting-started/installation/index.html
 M Dreambit.Docs/site/getting-started/project-structure/index.html
 M Dreambit.Docs/site/index.html
 M Dreambit.Docs/site/input/actions/index.html
 M Dreambit.Docs/site/input/direct-input/index.html
 M Dreambit.Docs/site/input/index.html
 D Dreambit.Docs/site/ldtk/entities/index.html
 D Dreambit.Docs/site/ldtk/index.html
 D Dreambit.Docs/site/ldtk/loading-levels/index.html
 D Dreambit.Docs/site/ldtk/monogame-conversions/index.html
 D Dreambit.Docs/site/ldtk/quick-start/index.html
 M Dreambit.Docs/site/networking/index.html
 M Dreambit.Docs/site/physics/colliders/index.html
 M Dreambit.Docs/site/physics/index.html
 M Dreambit.Docs/site/physics/movement/index.html
 M Dreambit.Docs/site/physics/queries/index.html
 M Dreambit.Docs/site/physics/shapes/index.html
 M Dreambit.Docs/site/rendering/cameras/index.html
 M Dreambit.Docs/site/rendering/drawing/index.html
 M Dreambit.Docs/site/rendering/index.html
 M Dreambit.Docs/site/rendering/lighting/index.html
 M Dreambit.Docs/site/rendering/pipeline/index.html
 M Dreambit.Docs/site/scripting/index.html
 M Dreambit.Docs/site/search.html
 M Dreambit.Docs/site/search/search_index.json
 M Dreambit.Docs/site/sitemap.xml.gz
 M Dreambit.Docs/site/tiled/index.html
 M Dreambit.Docs/site/UI/Brushes/IUiBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/LayeredBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/NineSliceBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/OutlineBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/SolidColorBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/SpriteBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/TiledSpriteBrush/index.html
 M Dreambit.Docs/site/UI/Brushes/UiBrush/index.html
 M Dreambit.Docs/site/UI/Elements/UiBorder/index.html
 M Dreambit.Docs/site/UI/Elements/UiButton/index.html
 M Dreambit.Docs/site/UI/Elements/UiCanvas/index.html
 M Dreambit.Docs/site/UI/Elements/UiCheckBox/index.html
 M Dreambit.Docs/site/UI/Elements/UiComboBox/index.html
 M Dreambit.Docs/site/UI/Elements/UiContainer/index.html
 M Dreambit.Docs/site/UI/Elements/UiContentControl/index.html
 M Dreambit.Docs/site/UI/Elements/UiControl/index.html
 M Dreambit.Docs/site/UI/Elements/UiElement/index.html
 M Dreambit.Docs/site/UI/Elements/UiGrid/index.html
 M Dreambit.Docs/site/UI/Elements/UiHorizontalStackPanel/index.html
 M Dreambit.Docs/site/UI/Elements/UiItemsControl/index.html
 M Dreambit.Docs/site/UI/Elements/UiListBox/index.html
 M Dreambit.Docs/site/UI/Elements/UiOverlay/index.html
 M Dreambit.Docs/site/UI/Elements/UiPanel/index.html
 M Dreambit.Docs/site/UI/Elements/UiPopup/index.html
 M Dreambit.Docs/site/UI/Elements/UiProgressBar/index.html
 M Dreambit.Docs/site/UI/Elements/UiRadioButton/index.html
 M Dreambit.Docs/site/UI/Elements/UiRangeBase/index.html
 M Dreambit.Docs/site/UI/Elements/UiScrollBar/index.html
 M Dreambit.Docs/site/UI/Elements/UiSelector/index.html
 M Dreambit.Docs/site/UI/Elements/UiSlider/index.html
 M Dreambit.Docs/site/UI/Elements/UiSpacer/index.html
 M Dreambit.Docs/site/UI/Elements/UiStackPanel/index.html
 M Dreambit.Docs/site/UI/Elements/UiStackPanelBase/index.html
 M Dreambit.Docs/site/UI/Elements/UiText/index.html
 M Dreambit.Docs/site/UI/Elements/UiTextBox/index.html
 M Dreambit.Docs/site/UI/Elements/UiTexture/index.html
 M Dreambit.Docs/site/UI/Elements/UiToggleButton/index.html
 M Dreambit.Docs/site/UI/Elements/UiTooltip/index.html
 M Dreambit.Docs/site/UI/Elements/UiUniformGrid/index.html
 M Dreambit.Docs/site/UI/Elements/UiVerticalStackPanel/index.html
 M Dreambit.Docs/site/UI/Elements/UiViewbox/index.html
 M Dreambit.Docs/site/UI/Elements/UiWrapPanel/index.html
 M Dreambit.Docs/site/UI/index.html
 M Dreambit.Docs/site/UI/Stylesheets/index.html
 M Dreambit.Docs/site/utilities/collections/index.html
 M Dreambit.Docs/site/utilities/math/index.html
 M Dreambit.Editor.Abstractions/Dreambit.Editor.Abstractions.csproj
 M Dreambit.Editor.Tests/InspectorMetadataTests.cs
 D Dreambit.Editor.Tests/LDtkGeneratedEntitySelectionTests.cs
 M Dreambit.Editor.Tests/SceneDocumentCharacterizationTests.cs
 M Dreambit.Editor.Tests/SceneDocumentTests.cs
 M Dreambit.Editor.Tests/TiledImportTests.cs
 M Dreambit.Editor/Assets/AssetBakeService.cs
 M Dreambit.Editor/Assets/AssetEditingService.cs
 M Dreambit.Editor/Assets/AssetKind.cs
 M Dreambit.Editor/Assets/AssetTypeClassifier.cs
 M Dreambit.Editor/Commands/EditorDocumentCommands.cs
 M Dreambit.Editor/Compilation/GameTypeCatalog.cs
 M Dreambit.Editor/EditorApplication.cs
 M Dreambit.Editor/Inspection/ImportOptionsEditorGui.cs
 D Dreambit.Editor/Inspection/LDtkImportInspector.cs
 M Dreambit.Editor/Inspection/SceneEntityInspector.cs
 M Dreambit.Editor/Projects/DreambitSdkConstants.cs
 M Dreambit.Editor/Scenes/EditorScene.cs
 M Dreambit.Editor/Scenes/ImportedSceneSources.cs
 M Dreambit.Editor/Scenes/SceneDocument.cs
 M Dreambit.Editor/Scenes/SceneDocumentSerializer.cs
 M Dreambit.Editor/Scenes/SceneDocumentService.cs
 M Dreambit.Editor/Scenes/SceneRuntime.cs
 M Dreambit.Editor/UI/DefaultDockLayout.cs
 M Dreambit.Editor/UI/Dialogs/SceneDocumentDialogs.cs
 M Dreambit.Editor/UI/EditorTransformGizmo.cs
 M Dreambit.Editor/UI/Panels/EditorPanelIds.cs
 M Dreambit.Editor/UI/Panels/HierarchyPanel.cs
 D Dreambit.Editor/UI/Panels/LDtkImportOptionsPanel.cs
 M Dreambit.Editor/UI/Panels/ProjectPanel.cs
 M Dreambit.Editor/UI/ProjectWorkspace/EditorProjectWorkspace.cs
 M Dreambit.Editor/Dreambit.Editor.csproj
 M Dreambit.Editor/docs/project-format.md
 M DreambitEngine.AssetBaker/Commands/BakeBlobsCommand.cs
 M DreambitEngine.AssetBaker/Commands/BakePakCommand.cs
 M DreambitEngine.AssetBaker/Pipeline/AssetBakePipeline.cs
 M DreambitEngine.AssetBaker/Pipeline/Docs/JsonbBaker.cs
?? DreambitEngine.AssetBaker/Pipeline/Tiled/TiledAutomappingAssetCompiler.cs
 M DreambitEngine.Build/DreambitEngine.Build.csproj
 M DreambitEngine.Build/buildTransitive/DreambitEngine.Build.props
 M DreambitEngine.Networking.Tests/NetworkSceneCatalogTests.cs
 M DreambitEngine.Templates/DreambitEngine.Templates.csproj
 M DreambitEngine.Templates/README.md
 M DreambitEngine.Templates/content/dreambit-game-source/.template.config/template.json
 M DreambitEngine.Templates/content/dreambit-game-source/src/DreambitGame.Content/Assets/README.md
 M DreambitEngine.Templates/content/dreambit-game/.template.config/template.json
 M DreambitEngine/Assets/Blueprints/SceneBlueprint.cs
 M DreambitEngine/Assets/Blueprints/SceneBlueprintLoadOptions.cs
 M DreambitEngine/Assets/Tilemaps/TilemapLayerData.cs
 M DreambitEngine/DreambitAssemblyCaches.cs
 M DreambitEngine/DreambitEngine.csproj
 M DreambitEngine/DreambitEngine.csproj.DotSettings
 D DreambitEngine/ECS/Components/AI/AStarGrid.cs
 D DreambitEngine/ECS/Components/AI/AStarPathfinder.cs
 D DreambitEngine/ECS/Components/AI/AStarPathFollower.cs
 M DreambitEngine/ECS/Components/Rendering/Tilemaps/TilemapRenderer.cs
 M DreambitEngine/ECS/Core/Entity.cs
 D DreambitEngine/LDtk/Attributes/LDtkLoader.cs
 D DreambitEngine/LDtk/LDtkEntity.cs
 D DreambitEngine/LDtk/LDtkEntityBuilderRepository.cs
 D DreambitEngine/LDtk/LDtkFile.cs
 D DreambitEngine/LDtk/LDtkGeneratedEntityOverrides.cs
 D DreambitEngine/LDtk/LDtkImporter.cs
 D DreambitEngine/LDtk/LDtkImportOptions.cs
 D DreambitEngine/LDtk/LdtkJson.cs
 D DreambitEngine/LDtk/LDtkLevelInstance.cs
 D DreambitEngine/LDtk/LDtkLoaderBase.cs
 D DreambitEngine/LDtk/LDtkManager.cs
 D DreambitEngine/LDtk/LDtkMonoGameExtensions.cs
 D DreambitEngine/LDtk/LDtkScene.cs
 D DreambitEngine/LDtk/LDtkSceneEntityMaterializer.cs
 D DreambitEngine/LDtk/LDtkSceneReference.cs
 D DreambitEngine/LDtk/Loaders/ILDtkEntityBuilder.cs
 D DreambitEngine/LDtk/Loaders/LDtkFileLoader.cs
 D DreambitEngine/LDtk/Loaders/LDtkLevelLoader.cs
 D DreambitEngine/LDtk/Schema/LDtkJsonSchema.cs
 D DreambitEngine/LDtk/Schema/LdtkPrimitives.cs
 D DreambitEngine/LDtk/Schema/LdtkResolvedReferences.cs
 D DreambitEngine/LDtk/Schema/LICENSE.md
 M DreambitEngine/Networking/Scenes/NetworkSceneCatalog.cs
 M DreambitEngine/Properties/AssemblyInfo.cs
 M DreambitEngine/Scene.cs
?? DreambitEngine/Tiled/Automapping/TiledAutomappingCatalog.cs
?? DreambitEngine/Tiled/Automapping/TiledAutomappingRuleCompiler.cs
?? DreambitEngine/Tiled/Automapping/TiledRuntimeAutomapper.cs
 M DreambitEngine/Tiled/Models/TmxMap.cs
 M DreambitEngine/Tiled/TiledImportOptions.cs
 M DreambitEngine/Tiled/TiledMapImporter.cs
 M DreambitEngine/Tiled/TiledMapInstance.cs
 M DreambitEngine/Tiled/TiledScene.cs
?? DreambitEngine/Tiled/TiledSceneBlueprintMaterializer.cs
?? DreambitEngine/Tiled/TiledRuntimeTileLayer.cs
?? DreambitEngine/Tiled/TiledTileReference.cs
 M DreambitEngine/Tiled/TmxSourceLoader.cs
 M README.md
 M scripts/Publish-DreambitSdk.ps1
 M scripts/README.md
?? TILED_AUTOMAPPING_IMPLEMENTATION_REPORT.md
```
