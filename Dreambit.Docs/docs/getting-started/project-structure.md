# Project and content layout

A practical solution keeps engine source, game code, content recipes, and source
assets separate:

```text
MyGame/
  MyGame.csproj
  Program.cs
  Scenes/
  Components/
MyGame.Content/
  MyGame.Content.csproj
  BuildContent.targets
  Builder/Builder.cs
  Assets/
    Ui/
    Textures/
    Audio/
    Blueprints/
```

The content build produces files under the host application's `Content`
directory. Runtime paths are logical paths relative to that root and normally
omit their baked extension:

```csharp
Resources.LoadAsset<Sprite>("Sprites/player");
Resources.LoadAsset<Texture2D>("Textures/background");
```

Dreambit UI XML is part of the same asset build: source `*.uxml` files become
`*.xmlb` assets in the development blob cache and in `content.pak` for shipping
builds.
`UiFrame.WithLayout("Ui/hud.uxml")` keeps using the readable source-style path;
the runtime resolves it through the active Dreambit content source. See
[Content projects and Asset Baker](../assets/content-pipeline.md).

!!! tip
    Copy the structure and build-target wiring from `Dreambit.Examples.Content`
    before inventing a new content build. It already handles the blob/PAK asset
    build and the host project's output directory.

