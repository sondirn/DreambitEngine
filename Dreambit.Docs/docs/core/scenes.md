# Scenes

A scene is a level, menu, or self-contained game state. Override its protected
hooks; do not call `Tick`, `PhysicsTick`, or `OnDraw` yourself.

```csharp
public sealed class ArenaScene : Scene<ArenaScene>
{
    protected override void OnInitialize()
    {
        BackgroundColor = Color.Black;
        CreateEntity("arena-manager").AttachComponent<ArenaManager>();
    }

    protected override void OnBegin() { }
    protected override void OnUpdate() { }
    protected override void OnPhysicsUpdate() { }
    protected override void OnEnd() { }
}
```

`OnInitialize` is the normal place to configure cameras and create entities.
`OnBegin` runs when the scene starts. `OnUpdate` runs every game frame,
`OnPhysicsUpdate` on the fixed physics tick, and `OnEnd` during termination.

Every initialized scene creates a `MainCamera`, `UiCamera`, and `AmbientLight`.
It also owns rendering options, post-process settings, a coroutine service, and
the scripting manager.

## Switching scenes

```csharp
Scene.SetNextScene<GameScene>();
Scene.SetNextScene(new ResultsScene(score));
Scene.SetNextScene<GameplayScene>("Scenes/world");
```

The switch occurs on the next engine update.

## Creating an editor-authored scene

Use `CreateFromBlueprint<TScene>` when another system needs a fully authored Scene instance without
scheduling a local transition:

```csharp
var scene = Scene.CreateFromBlueprint<GameplayScene>("Scenes/world");
```

The factory constructs `GameplayScene`, loads the `.scene` asset immediately while the Scene is still
in `Created`, and returns it without running initialization. If materialization fails, it disposes the
partially constructed Scene before rethrowing the error.

For a Scene Blueprint linked to Tiled, `GameplayScene` must derive from `TiledScene`. Networking uses
this same factory through `NetworkSceneCatalog.RegisterBlueprint<TScene>` so every peer constructs the
same typed host before synchronization starts.

## Additive Scene content

Additive loading materializes one or more Scene Blueprint assets inside the current `Scene`. It does
not create additional Scene instances: all loaded content shares the Scene's Entity repository,
render pipeline, physics world, services, cameras, settings, and network world.

```csharp
var village = Scene.Instance.LoadAdditive("Scenes/Zones/Village.scene");
var tree = Scene.Instance.LoadAdditive("Scenes/Zones/AncientTree.scene");

// Only entities and Tiled content owned by village are removed.
Scene.Instance.Unload(village);
```

Each call returns a distinct `SceneContentInstance`, even when the same asset is loaded more than
once. `InstanceId` identifies that local runtime lifetime. `SourceAssetId` and `SourceAssetName`
identify its source asset; neither is used as the runtime instance identity.

Additive materialization always gives authored entities fresh runtime `Entity.Id` values. Use the
instance-local source map when code needs to find an authored object:

```csharp
Entity door = village.GetEntity(authoredDoorGuid);

if (Scene.Instance.TryGetContentInstance(door, out var owner))
{
    // owner is village
}
```

Internal entity and component references are remapped through the same instance-local table. Two
copies of one Scene Blueprint therefore resolve references only within their own copy. The serialized
source GUIDs are not modified.

Runtime entities can opt into the same lifetime:

```csharp
var droppedItem = village.CreateEntity("dropped-item");

// Adopt an entity already created in this Scene. Descendants are included by default.
village.TrackEntity(otherEntity);
```

Entities created directly through `Scene.CreateEntity` remain persistent unless they are explicitly
tracked. Ownership is exact rather than hierarchy-wide: a persistent child parented below an owned
entity is detached and survives unload unless it was also tracked.

Dreambit does not rewrite arbitrary runtime component fields when content unloads. A persistent
component that stores an `Entity` or `Component` from unloadable content must release it as part of
the game's unload flow or treat `Entity.IsNull` / `Component.IsNull` as the invalid-reference check.
Editor serialization rejects persistent fields that point into additive content instead of writing
a source GUID that would become dangling.

`SceneContentLoadOptions.ApplySceneSettings` defaults to `false`, so a zone does not replace global
lighting, exposure, or other Scene-wide settings. Setting it to `true` applies the Blueprint settings;
if loading fails, the previous settings are restored.

Loading is transactional. A failed Blueprint, component, or Tiled materialization leaves no content
handle, generated map, renderer, collider, or partial entity hierarchy. Unload invalidates the handle
immediately, suspends its entities, and completes destruction at the current or next safe ECS
structural boundary. Calling `Unload` again returns `false`.

`SceneServiceComponent` is deliberately forbidden in all additive content because Scene services
have whole-Scene lifetime. Ordinary, locally managed `Scene.LoadAdditive` calls also reject
`NetworkObject`, including required or dynamically attached markers. Networked additive content is
instead loaded through `NetworkService.LoadScope`; that private trusted path creates a
network-managed `SceneContentInstance` and binds authored objects by `(scope, source GUID)`. Game
code cannot manually mutate or unload that instance behind the network protocol. See
[Network replication scopes](../networking/#additive-content-and-replication-scopes).

Additive ownership is runtime-only. It is not serialized into Scene or Entity Blueprints, and
additive loading is rejected in editor-hosted Scenes so preview/runtime content cannot be baked back
into the open document. Existing `LoadIntoSelf` and `CreateFromBlueprint` behavior is unchanged.

## Finding entities

```csharp
var player = FindEntity("player");
var enemies = GetActiveEntitiesByTag("enemy");
var all = GetAllActiveEntities();
```

Names are convenient for unique managers. Tags are better for groups. Keep the
returned references only while this scene remains active.

