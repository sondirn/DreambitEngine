# UiFrame

`UiFrame` loads a Dreambit `*.uxml` UI document, routes
pointer/keyboard/gamepad input, and draws it with the scene's UI camera.

```csharp
var frame = CreateEntity("hud")
    .AttachComponent<UiFrame>()
    .WithCss("Ui/Stylesheets/master.ucss")
    .WithLayout("Ui/hud.uxml");

frame.Layout.GetRequired<UiText>("score").Text = "0";
```

`CssPath`/`WithCss` configure an optional global stylesheet. `LayoutPath` and
`WithLayout` load immediately; changing the CSS of an existing frame rebuilds
its retained layout. Both changes are transactional, so a failed rebuild keeps
the prior paths and working layout. Blueprint property assignment order does
not matter, and old Blueprints without `CssPath` retain their previous behavior.

Paths must be relative to the
Dreambit content root and cannot escape it. Author the source path as
`Ui/hud.uxml`; the Asset Baker emits `Ui/hud.xmlb`, and `UiFrame` opens it from
the active blob manifest or `content.pak`.

`CreateComponent(path, idPrefix)` uses the same baked asset source, including
relative and `~/` references inside component UXML, and returns one detached UI
subtree that you can add to a `UiContainer` in the current layout. See
[`UiContainer`](../../UI/Elements/UiContainer.md).

`CreateComponent(path, idPrefix, additionalCssPath)` adds a required stylesheet
during detached component construction. Styling is not recomputed when the
component is attached. See [UI stylesheets](../../UI/Stylesheets.md) for the
complete cascade and supported CSS subset.
