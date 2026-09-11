<div align="center">

# Dreambit Engine

### A source-first .NET 8 game engine for expressive 2D games

Build games with a scene-driven core, entity-component composition, lit 2D
rendering, polygon physics, XML user interfaces, baked content, particles,
audio, AI, coroutines, and Tiled worlds.

[Read the documentation](Dreambit.Docs/docs/index.md) ·
[Build your first game](Dreambit.Docs/docs/getting-started/first-game.md) ·
[Explore the examples](Dreambit.Examples)

</div>

---

## What is Dreambit?

Dreambit is a C# game engine built on MonoGame for developers who want a focused
2D workflow without giving up direct access to their code. Games are organized
into scenes, entities, and small reusable components. Engine content stays
source-controlled alongside the game. Development runs from incremental baked blobs, and
**Build > Bake Pak** creates a single runtime package when the game is ready to ship.

The repository includes the engine, content pipeline, Asset Baker, Tiled
integration, runnable examples, and a complete MkDocs learning guide.

## Engine systems

| System | What it provides |
| --- | --- |
| **Scenes and ECS** | Scene lifecycle, entities, tags, parent-child hierarchies, transforms, component requirements, and blueprints |
| **Rendering** | Sprites, animation, primitives, particles, cameras, draw layers, 2D lights, post-processing, and retained UI |
| **Physics** | Polygon colliders, triggers, spatial hashing, and point, ray, circle, polygon, and collider queries |
| **User interface** | XML layouts, responsive panels, controls, focus/navigation, popups, reusable components, and composable brushes |
| **Input** | Keyboard, mouse, controller, UI capture, named actions, maps, chords, and composite bindings |
| **Assets** | Textures, sprites, sprite sheets, animations, audio, typed blueprints, fonts, TMX/TSX data, and pak files |
| **Gameplay tools** | Coroutines, finite state machines, blackboards, A* pathfinding, cutscene scripting, logging, and debug drawing |

## A first scene

```csharp
using Dreambit;
using Dreambit.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

using var game = new Core(1280, 720, "My Dreambit Game");
Scene.SetNextScene<MainScene>();
game.Run();

public sealed class MainScene : Scene<MainScene>
{
    protected override void OnInitialize()
    {
        Window.SetSize(1280, 720);
        BackgroundColor = new Color(18, 20, 28);

        MainCamera.SetTargetVerticalResolution(720f);
        MainCamera.ForcePosition(new Vector3(640, 360, 0));

        CreateEntity("player", tags: ["player"],
                createAt: new Vector3(640, 360, 0))
            .AttachComponent<PlayerController>();
    }
}

[Require(typeof(RectDrawer))]
public sealed class PlayerController : Component
{
    [FromRequired] private RectDrawer _drawer;

    public override void OnCreated()
    {
        _drawer.Width = 32;
        _drawer.Height = 32;
        _drawer.Color = Color.CornflowerBlue;
    }

    public override void OnUpdate()
    {
        var direction = Vector2.Zero;
        if (Input.IsKeyHeld(Keys.A)) direction.X--;
        if (Input.IsKeyHeld(Keys.D)) direction.X++;
        if (Input.IsKeyHeld(Keys.W)) direction.Y--;
        if (Input.IsKeyHeld(Keys.S)) direction.Y++;

        if (direction != Vector2.Zero)
            direction.Normalize();

        Transform.TranslateWorld2D(direction * 240f * Time.DeltaTime);
    }
}
```

The `[Require]` attribute ensures that the drawable exists, while
`[FromRequired]` injects it before `PlayerController.OnCreated` runs.

### Loading a scene created by Dreambit Editor

Save the scene anywhere under the project's `Assets` directory. The Editor makes it available to
Debug runs through the blob cache, and **Build > Bake Pak** includes it in shipping content. A
runtime scene can load it directly:

```csharp
public sealed class GameScene : Scene<GameScene>
{
    protected override void OnInitialize()
    {
        LoadIntoSelf("scenes/first-level.scene");
    }
}
```

The equivalent explicit form is
`LoadIntoSelf(Resources.LoadAsset<SceneBlueprint>("scenes/first-level.scene"));`.

If the Editor scene was created from a Tiled map, its runtime class must derive
from `TiledScene` and use the blueprint-linked constructor mode:

```csharp
public sealed class WorldScene : TiledScene
{
    public WorldScene() : base() { }

    protected override void OnTiledMapLoaded(TiledMapInstance map)
    {
        var ground = map.GetRuntimeTileLayer("Ground");
    }
}

Scene.SetNextScene<WorldScene>("scenes/first-level.scene");
```

The non-generic scene-asset overload rejects Tiled-linked scenes because an
ordinary `Scene` has no Tiled map owner.

## UI without hard-coded screens

Dreambit layouts are readable XML files that can be edited without rebuilding
the engine:

```xml
<Ui>
  <Border width="100%" height="100%" padding="24"
          background-tint="#18202AFF">
    <Border.Background>
      <SolidColorBrush />
    </Border.Background>

    <VerticalStackPanel width="100%" height="100%" spacing="12">
      <Text text="Dreambit" font="monogram" font-size="32" />
      <Button id="play-button" width="240" height="44">
        <Text text="Play" font="monogram" font-size="20" />
      </Button>
    </VerticalStackPanel>
  </Border>
</Ui>
```

```csharp
var menu = CreateEntity("menu")
    .AttachComponent<UiFrame>()
    .WithLayout("Ui/main-menu.uxml");

menu.Layout.GetRequired<UiButton>("play-button").Clicked +=
    _ => Scene.SetNextScene<GameScene>();
```

Every shipped [UI element](Dreambit.Docs/docs/ui/index.md) and
[brush](Dreambit.Docs/docs/ui/brushes/ui-brush.md) has its own documentation
page.

## Build and run

Requirements:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A Vulkan-capable desktop environment for the included example runtime

```powershell
dotnet restore DreambitEngine.sln
dotnet build DreambitEngine.sln
dotnet run --project Dreambit.Examples
```

The examples open with the UI control gallery and link to the Pong, space game,
and particle demonstrations.

## Documentation

The documentation lives in [`Dreambit.Docs`](Dreambit.Docs) and contains a
guided introduction plus individual pages for every current ECS component, UI
element, and UI brush.

```powershell
cd Dreambit.Docs
python -m pip install -r requirements.txt
python -m mkdocs serve
```

Then open `http://127.0.0.1:8000`.

Useful starting points:

- [Installation](Dreambit.Docs/docs/getting-started/installation.md)
- [Your first game](Dreambit.Docs/docs/getting-started/first-game.md)
- [Entity-component system](Dreambit.Docs/docs/ecs/index.md)
- [User interface](Dreambit.Docs/docs/ui/index.md)
- [Physics](Dreambit.Docs/docs/physics/index.md)
- [Assets and content](Dreambit.Docs/docs/assets/index.md)
- [Tiled integration](Dreambit.Docs/docs/tiled/index.md)

## Repository map

```text
DreambitEngine/              Engine runtime and public API
DreambitEngine.AssetBaker/   Texture, audio, JSON, YAML, and pak builder
Dreambit.Content/            Shared engine effects and fonts
Dreambit.Examples/           Runnable UI, Pong, space game, and particle examples
Dreambit.Examples.Content/   Example source assets, including UI layouts
Dreambit.Docs/               MkDocs documentation project
DreambitEngine/Tiled/        Native TMX/TSX loading, import, and scene integration
```

## Project status

Dreambit is under active development. The documentation describes the current
repository implementation and marks obsolete, experimental, or incomplete paths
where appropriate. Review those notes before depending on an extension point in
production code.

