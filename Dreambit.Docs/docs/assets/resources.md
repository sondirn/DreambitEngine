# Loading resources

Load by runtime type and logical path without the baked extension:

```csharp
var texture = Resources.LoadAsset<Texture2D>("Textures/ship");
var sprite = Resources.LoadAsset<Sprite>("Sprites/ship-icon.sprite");
var sheet = Resources.LoadAsset<SpriteSheet>("SpriteSheets/player.spritesheet");
var cue = Resources.LoadAsset<SoundCue>("Audio/laser-cue.soundcue");
```

The first successful load is cached in Dreambit's `DreambitContentCollection`;
subsequent loads by the same concrete type and logical path return the cached object. A failed load is logged and
`LoadAsset<T>` returns null, so validate essential assets during scene setup.

```csharp
var sprite = Resources.LoadAsset<Sprite>(path)
    ?? throw new InvalidOperationException($"Missing sprite: {path}");
```

`Resources.UnloadAsset(path)` removes cached entries for the path and releases
Dreambit-owned resources. Do not unload a shared asset while components still reference it.

For resident definitions and startup preloads, use a
[scene asset registry](registries.md). `Resources.FindAssets<TAsset>()` discovers
authored assets by type from catalog metadata without loading them.

Fonts use `Resources.LoadSpriteFont(path, size)` and are cached per path+size.
`DreambitAsset.AssetName` is the logical identity used for registration and
references. Serialized Dreambit assets include their semantic extension in that identity; raw
textures, fonts, effects, and audio keep their existing source formats.

Set `Resources.UsePak = false` only when the equivalent baked loose files exist
at the paths expected by each loader.

