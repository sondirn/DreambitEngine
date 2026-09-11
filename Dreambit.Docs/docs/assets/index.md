# Assets and content

Dreambit identifies runtime content by logical paths relative to the application's
`Content` root. Source textures, audio, JSON, and Tiled files are normally baked
into `content.pak`; UI XML and fonts can be copied as loose files.

`Resources.LoadAsset<T>` selects a Dreambit loader for known types, caches the
result, and falls back to MonoGame's `ContentManager` for other types.

The main asset types are textures, sprites, sprite sheets, animations, entity
blueprints, sound cues, particle configurations, Tiled maps/tilesets, songs, and
sound effects.

Start with [Content projects and Asset Baker](content-pipeline.md), then
[Loading resources](resources.md).

