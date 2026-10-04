# Scripting and cutscenes

Dreambit cutscenes are reusable presentation assets. They advance on the main thread once per Scene update; waits do not sleep and playback does not pause networking, physics, entities, or input.

## Asset authoring

Create a YAML source file with the canonical `.cutscene` extension. A source at `Assets/Cutscenes/example.cutscene` bakes to `Cutscenes/example.cutscene.yamlb`; its logical asset name is `Cutscenes/example.cutscene`. The existing legacy `.yaml` workflow still loads using an extensionless logical name.

The YAML root is a sequence of `scriptGroup` entries. Each group contains a sequence of mappings: `script` names an action class and the other fields supply its public constructor parameters. Groups run in order. All actions within one group advance together on the game thread, and the next group begins on the following update after all current actions complete.

For example, an action mapping for a wait uses `script: WaitScript`, `duration: 1.5`, and optionally `unscaled: true`. A movement action uses `script: MoveScript`, `entity: speaker`, `speed: 2`, and `moveTo: [3, 4]`. These are configuration examples, not preinstalled content.

The editor recognizes `.cutscene` assets and creates a YAML placeholder. Replace its `TODO` action in a text editor. Cutscenes use their dedicated YAML loader, not the generic JSON asset inspector. Stable AssetIds, the typed catalog, and `AssetReference<Cutscene>` remain available to game-defined assets. Do not store paths as durable story identity.

## Starting and observing playback

Use the owning Scene's manager and explicit actor bindings. This helper can be placed in your own presentation code:

```csharp
using System;
using System.Collections.Generic;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Scripting;

public static class PresentationExample
{
    public static CutscenePlayback Play(Scene scene, Cutscene asset, Entity speaker)
    {
        var actors = new Dictionary<string, CutsceneActorBinding>
        {
            ["speaker"] = new(speaker, AllowMovement: true)
        };
        var context = new CutsceneContext(scene, actors);
        if (!scene.ScriptingManager.TryStart(asset, context, out var playback, out var error))
            throw new InvalidOperationException(error);

        if (playback.Status == CutscenePlaybackStatus.Running)
            playback.Finished += OnFinished;
        else
            OnFinished(playback); // A start observer may synchronously cancel playback.
        return playback;
    }

    private static void OnFinished(CutscenePlayback playback)
    {
        // Inspect Completed, Cancelled, or Failed and playback.Diagnostic.
        // Only the game server should decide whether a persistent effect is authorized.
    }
}
```

`TryStart` validates and constructs every action before starting any. A busy/disposed runner or invalid configuration returns false and an error without replacing current playback. A throwing start observer returns false with a failed handle after cleanup. The returned handle identifies exactly one run, exposes its outcome, and supports `Cancel(reason)`. Cancelling an old handle cannot cancel a later playback.

The Scene owns the runner and updates it automatically. Do not also call `Update` from a game component. `IsCutsceneActive` is now an **instance** property. Constructing or disposing another Scene cannot alter it.

`Scene.StartCutscene` and the manager's old boolean overloads remain for simple context-free sequences. They provide no actor bindings. Prefer `TryStart` for new code. `OnScriptingEnd` remains a compatibility success-only notification without playback identity; use the handle for reliable outcomes.

## Built-in actions

| Action | Constructor fields | Behavior |
|---|---|---|
| `WaitScript` | `duration`, optional `unscaled` (true) | Finite nonnegative duration; waits without blocking. Set unscaled false to follow game time. |
| `MoveScript` | `entity`, `speed`, `moveTo`, optional `unscaled` (true) | Moves a permitted presentation actor in world units; finite positive speed, no overshoot. Restores its original world position during playback cleanup. |
| `EnableEntityScript` | `entity` | Temporarily enables a bound non-network entity. Restores its previous enabled state during cleanup. |
| `SetAnimationScript` | `entity`, `animation`, optional `waitForCompletion` (false) | Selects an animation asset's first frame immediately. When waiting, explicitly plays a non-looping animation and waits for its completion. Restores prior animation/frame/queue/playing state during cleanup. |

`entity` is a **role in the context**, not a Scene-wide name. Missing roles/components fail clearly. Movement needs `AllowMovement: true` and rejects entities carrying `NetworkObject`; enabling network entities is also rejected. Use authoritative game movement for shared NPCs and player positions. Animation actions change presentation and must not be used as authoritative gameplay proof.

Temporary state lasts until the entire playback ends, so subsequent groups can use earlier movement/animation changes. Cleanup runs in reverse action order on all outcomes. The animator's generic `BeginPresentation` lease also restores an initially unanimated sprite; if another owner has selected a different animation, it leaves that replacement alone.

Animation playback uses the animator's existing scaled update clock. Do not pause game time and then wait on a scaled animation; unscaled waits and moves remain available. A stopped/disabled animator cannot advance. Rootbound adds a bounded timeout for abandoned story presentations.

There is no built-in camera action or skip policy in this version. Earlier documentation referenced a camera action that did not exist.

## Writing actions

Derive a public concrete class from `ScriptAction`. Use public constructor parameters for immutable configuration; YAML arguments match their names case-insensitively. Constructors must be side-effect free: validation constructs and cleans up instances without running them. Scalars, enums, arrays, Vector2, and Vector3 are supported. Use fully qualified class names if simple names collide across loaded assemblies.

- `Context` is assigned before playback hooks and supplies `Scene` and `GetActor(role)`.
- `OnStart` runs once before the first update. Resolve resources and subscribe here.
- `OnUpdate` performs a bounded amount of work. Set `IsComplete = true` on success.
- `OnCompleted` runs once, including when an external callback completes the action between frames.
- `OnGroupEnd` runs once after all actions in the group finish.
- `CleanUp` runs once for each constructed instance, including instances that never started. Release subscriptions and restore temporary state; tolerate absent setup.

Exceptions during action hooks fail the playback with asset/group/action diagnostics. Cleanup failures also produce Failed. Each finish observer is isolated and logged so one observer cannot stop others. Operations are intended for the game thread.

For additive content, supply a validity predicate such as `() => content.IsLoaded`. The runner also checks bound actor lifetime. Invalid context cancels playback. Whole-Scene shutdown cancels all owned work. Do not retain finished handles indefinitely if their context contains game entities.

`ScriptingManager.Validate(asset)` is available to required registries for startup validation. Action discovery uses reflection only during validation/startup, never in frame updates. Playback contains no event log, persistent story state, network protocol, or global pause policy; those belong to the game.
