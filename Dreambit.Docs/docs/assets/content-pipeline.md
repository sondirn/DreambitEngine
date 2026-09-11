# Content projects and Asset Baker

The Asset Baker command registered by the current executable is `bake-pak`:

```powershell
dotnet run --project DreambitEngine.AssetBaker -- bake-pak MyGame.Content/Assets MyGame/Build/Content/content.pak --premul --srgb
```

It recursively mirrors logical paths inside the pak and supports:

| Source | Baked entry |
| --- | --- |
| `.png`, `.jpg`, `.jpeg`, `.bmp`, `.tga` | `.texb` |
| `.wav`, `.ogg`, `.mp3` | `.audb` |
| `.json` | `.jsonb` |
| `.asset`, `.blueprint`, `.particlefx`, `.scene`, `.soundcue`, `.sprite`, `.spriteanimation`, `.spritesheet`, `.tileset` | source extension + `.jsonb` |
| `.yaml`, `.cutscene` | `.yamlb` (`.cutscene` keeps its source extension) |
| `.uxml`, `.xml`, `.tmx`, `.tx`, `.tsx` | `.xmlb` |
| `.ucss`, `.css` | `.cssb` |

Serialized Dreambit assets use their semantic source extension as part of their logical runtime
name. For example, `Sprites/hero.sprite` bakes to `sprites/hero.sprite.jsonb` and is loaded as
`Resources.LoadAsset<Sprite>("Sprites/hero.sprite")`. Game-defined `DreambitAsset` classes use
`.asset` by default.

Texture options include `--mips`, `--premul`, `--max-size N`, and `--srgb`.
The runtime defaults to `Resources.UsePak = true` and
`Resources.PakName = "content.pak"`.

Dreambit UI XML is authored as `*.uxml` and loaded through the active content
source. During development,
`UiFrame` reads the baked `*.xmlb` payloads named by `content.blobs.json`; a
shipping build reads the same logical assets from `content.pak`. Layout and
component references remain source-style paths such as `Ui/hud.uxml`, and the UI
loader maps them to `Ui/hud.xmlb` internally. Do not add raw `*.uxml` copy rules
to the MonoGame content builder.

UI stylesheets use `*.ucss`: source `Ui/hud.ucss` becomes
`ui/hud.cssb`. Automatic layout/component sibling lookup and explicit
`UiFrame.CssPath` open only the baked asset. CSS remains path-loaded in this
version and is excluded from the stable-ID runtime registry so `Ui/hud.uxml` and
`Ui/hud.ucss` can coexist without producing the same extensionless registry
name. See [UI stylesheets](../UI/Stylesheets.md).

Legacy UI `*.xml` and `*.css` sources are still accepted for compatibility, but
new UI assets should use `*.uxml` and `*.ucss`.

!!! note
    `BakeDirectoryCommand` exists in source but is not registered in the current
    Asset Baker command application. Use `bake-pak` unless you intentionally add
    that command to `Program.cs`.
