# Dreambit Engine

Dreambit is a .NET 8 game engine built on MonoGame. It combines a scene-based
game loop, an entity-component system, reusable multiplayer networking, 2D rendering and lighting, polygon
physics, retained-mode XML UI, input actions, content baking, particles, audio,
AI helpers, coroutines, and Tiled integration.

This guide is organized around what you are trying to build. Start with
[Your first game](getting-started/first-game.md), then follow the system links
when your game needs them. Type pages document the concrete controls, brushes,
and ECS components you can attach today.

## A useful mental model

1. `Core` owns the MonoGame application and frame loop.
2. A `Scene` owns the current level or screen.
3. A scene creates `Entity` objects.
4. Behavior and rendering live in `Component` objects attached to entities.
5. Every entity has a `Transform`; parented transforms form a hierarchy.
6. Assets are loaded by logical paths from `Content` or `content.pak`.

## Learning paths

- **Build a playable scene:** [Getting started](getting-started/index.md) →
  [ECS](ecs/index.md) → [Input](input/index.md) → [Rendering](rendering/index.md)
- **Build menus and HUDs:** [UI overview](UI/README.md) →
  [base elements](UI/Elements/UiElement.md) → [buttons](UI/Elements/UiButton.md)
- **Add collisions:** [Physics overview](physics/index.md) →
  [colliders](physics/colliders.md) → [movement](physics/movement.md)
- **Create data-driven content:** [Assets](assets/index.md) →
  [content pipeline](assets/content-pipeline.md) → [blueprints](assets/blueprints.md)
- **Add multiplayer:** [Networking](networking/index.md) → configure the protocol contract →
  host or join from a local menu → enter a synchronized Scene

!!! note "Documentation scope"
    These pages describe the current repository implementation. A few APIs are
    explicitly marked experimental, obsolete, or limited where the code has an
    unfinished path. That helps examples stay predictable.
