# Scene asset registries

`AssetRegistry<TAsset>` is a scene service that holds a typed index of shared
`Resources` assets. Use it for resident definitions or to preload expensive assets
before gameplay. Define a concrete service in the game assembly:

```csharp
using Dreambit;
using Dreambit.ECS;

[DreambitAssetType("rootbound.item")]
public class ItemDefinition : DreambitAsset
{
    [DreambitSerialize]
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ItemDefinitionRegistry : AssetRegistry<ItemDefinition>
{
}

public sealed class GameplaySoundRegistry : AssetRegistry<SoundCue>
{
}
```

Add the concrete component to a scene service entity. Set the item registry's
**Source** to **AllOfType** and **Load Mode** to **Preload**. For the sound registry,
select **Explicit**, then add the desired SoundCue assets to **Explicit Assets**
using the normal picker or project drag and drop.

Both registries belong to their scene. Different scenes can each have their own
registry of the same type. Existing scene-service host and uniqueness rules apply.

## Public API

```csharp
public enum AssetRegistrySource { AllOfType, Explicit }
public enum AssetRegistryLoadMode { Manual, Preload }

public abstract class AssetRegistry<TAsset> : SceneServiceComponent
    where TAsset : DreambitAsset
{
    public AssetRegistrySource Source { get; set; }
    public AssetRegistryLoadMode LoadMode { get; set; }
    public List<AssetReference<TAsset>> ExplicitAssets { get; set; }

    public IReadOnlyList<TAsset> Assets { get; }
    public int Count { get; }
    public bool IsLoaded { get; }

    public TAsset Get(AssetId id);
    public bool TryGet(AssetId id, out TAsset asset);
    public TAsset Get(string assetName);
    public bool TryGet(string assetName, out TAsset asset);
    public void LoadAll();
}

public readonly record struct AssetCatalogEntry(
    AssetId Id, string AssetName, string? TypeId);

// Resources methods:
public static IReadOnlyList<AssetCatalogEntry> FindAssets<TAsset>()
    where TAsset : DreambitAsset;
public static TAsset LoadDreambitAsset<TAsset>(AssetCatalogEntry entry)
    where TAsset : DreambitAsset;
```

Defaults are `AllOfType` and `Preload`. Set authoring configuration before loading.
`Manual` leaves the index empty until `LoadAll()` is called. `LoadAll()` is synchronous
and idempotent after success. It does not rescan a populated registry. Calling it
after service disposal throws `ObjectDisposedException`.

`Get` and `TryGet` only query the populated index; they never load assets. Names
follow the content cache's case-insensitive comparison, whitespace trimming, and
forward/backslash equivalence, without allocating normalized strings per lookup.
`Get` throws `KeyNotFoundException` naming the registry and requested ID/name when
absent. `TryGet` returns false and a null output.

```csharp
[RequiresSceneService(typeof(ItemDefinitionRegistry))]
public sealed class InventoryService : SceneServiceComponent
{
    public override void OnServicesReady()
    {
        var items = Scene.Services.Get<ItemDefinitionRegistry>();
        foreach (var item in items.Assets)
        {
            // Definitions are already resident.
        }
    }

    public ItemDefinition FindItem(AssetId id) =>
        Scene.Services.Get<ItemDefinitionRegistry>().Get(id);
}
```

## Catalog and loading architecture

1. Editor classification reads `$dreambitType` from generic `.asset` documents or
   uses the existing semantic format classification for engine assets.
2. `.dreambit/assets.json` persists that stable identity in `type` alongside the
   asset ID. Source registry schemas 1 and 2 remain supported by the baker.
3. The baker reads this metadata once and embeds ID, logical name, and `type` in
   `__dreambit/asset-registry.jsonb`. It excludes tombstones, unsupported sources,
   and stylesheet entries using the existing bake rules. It does not reopen
   source assets to rediscover type IDs.
4. `RuntimeAssetRegistry.GetAssets()` and the editor's `AssetDatabase.GetAssets()`
   expose read-only catalog snapshots. Runtime schema 2 includes `type`; schema 1
   remains readable with untyped entries. The registry cache signature is
   `runtime-registry-v5`, invalidating older incremental registry blobs.
5. `Resources.FindAssets<TAsset>()` resolves each typed entry through
   `DreambitAssetTypeRegistry.TryResolve` and checks `IsAssignableFrom`. Concrete,
   derived, abstract-base queries, and former stable type IDs are supported. No
   asset documents, PAK indexes, blob directories, or filesystems are scanned to
   discover types. Discovery does not retain CLR type caches across reloads.
6. The scene registry uses the catalog to select entries, and the typed Resources
   overload loads each through its actual concrete type and existing loader/cache.
   Successful completion publishes all indexes together for gameplay lookups.

Untyped entries are excluded from typed discovery, preserving legacy stable ID/name
resolution. An unknown non-empty type ID fails discovery because compatibility
cannot be established. An absent catalog also fails clearly. Explicit selection
requires a live catalog entry with a resolvable compatible type; old untyped
catalogs must be rebuilt before using explicit typed preload.

Explicit selections use `AssetReference<TAsset>`, an immutable deferred reference
with `Id` and an optional diagnostic `AssetName`. Its converter reuses the existing
`$dreambitAsset` token:

```json
{
  "$dreambitAsset": "a36a9caa-9196-4a5f-8d65-acd9cba36373",
  "path": "Audio/gameplay.soundcue"
}
```

The ID is authoritative, so moving an asset does not break a selection. Direct
`TAsset` references would load during scene deserialization; deferred references
do not load during materialization or editor selection. The editor infers the
asset type from `TAsset` and only shows compatible choices. The explicit collection
is visible when Source is Explicit. Only Source, LoadMode, and ExplicitAssets are
serialized; Assets, Count, IsLoaded, and the dictionaries are runtime state.

## Startup and shutdown

Scene materialization registers service components, deserializes their properties,
and calls `OnCreated`. In a runtime scene, service activation follows initialization
and orders `OnServicesReady` using `RequiresSceneService`. A Preload registry
populates synchronously in that callback, before its dependent services receive
their callbacks. Activation finishes before `Scene.OnBegin`, after which the scene
transitions to Running. Registry subclasses overriding `OnServicesReady` should
call the base implementation before using the loaded index.

Required load errors, incompatible/unresolved types, missing explicit IDs, empty
selections, duplicate IDs, and duplicate normalized names throw descriptive errors.
The registry stays empty and `IsLoaded` stays false on failure. Registry startup
exceptions use the usual component fault reporting and also propagate, preventing
dependent callbacks and `OnBegin` from proceeding. Other scene services retain
their existing callback quarantine behavior.

Editor execution mode does not activate runtime service callbacks, so editing a
scene does not preload. The registry also guards its startup callback against
editor execution. An explicit call to `LoadAll` still means load now.

During shutdown, ordinary entities are destroyed before services receive their
stopping callbacks in reverse dependency order. The index remains available for
that cleanup and is cleared when the service is disposed. Registry subclasses
overriding `OnServiceDisposing` must call the base implementation.

## Shared ownership

Resources and `DreambitContentCollection` remain the authoritative shared cache.
Base and concrete registries use the same concrete loader key and share cached
objects with other Resources callers. A registry owns its lists and dictionaries;
it does not own the underlying assets. Disposing it clears those references without
calling `UnloadAsset` or disposing assets. Even a failed preload leaves successfully
loaded shared content in Resources, because another caller may hold it.

SoundCue preload uses the unchanged `SoundCueLoader`, including `LoadInternal()`
and its existing loading of SoundEffect takes. Graphics and audio loaders still run
on the calling/resource thread. Registry preload should run on that same thread.

Incremental preload progress, a worker-I/O/resource-finalization split for true
asynchronous loading, and scoped resource leases are possible future work. This
implementation adds neither background loading nor reference counting.
