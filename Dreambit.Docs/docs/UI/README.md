# User interface

Dreambit UI is a retained-mode interface system for game HUDs, menus, forms,
and overlays. Define the initial visual tree in UXML, apply presentation with
UCSS, then look up the created elements and change their C# properties as the
game runs.

The normal workflow is:

1. Author a `*.uxml` layout under the game's `Assets` directory.
2. Optionally add a sibling `*.ucss` stylesheet and a shared global stylesheet.
3. Load the layout through an ECS [`UiFrame`](../ecs/components/ui-frame.md).
4. Use element IDs to get typed references, subscribe to events, and display
   game state.

UXML and UCSS create the starting tree. They are not a data-binding layer.
Runtime behavior lives in C#.

## How the pieces fit together

| Part | Responsibility | When it is evaluated |
| --- | --- | --- |
| `UiFrame` | Owns the layout, routes input, and draws through the scene's UI camera | Every frame |
| `UiLayout` | Holds the retained element tree and provides ID lookup and focus APIs | For the life of the loaded layout |
| UXML | Declares elements, hierarchy, initial values, and reusable components | When the layout is loaded |
| UCSS | Applies construction-time styles to UXML-created elements | When the layout is loaded |
| C# | Wires events, changes live properties, and creates dynamic content | Whenever the game needs it |

Most property changes do not require a reload or a manual layout call. Elements
such as `UiText`, `UiTexture`, and `UiElement` invalidate their cached layout or
asset dependencies when a relevant property changes. `UiFrame` resolves those
changes during its normal update.

## Build a working HUD

Suppose the content project contains:

```text
Assets/
└── Ui/
    ├── master.ucss
    └── Hud/
        ├── hud.uxml
        └── hud.ucss
```

The runtime paths omit `Assets/`. The Asset Baker converts the source UXML and
UCSS files to Dreambit's baked content formats.

### 1. Declare the layout

`Assets/Ui/Hud/hud.uxml`:

```xml
<Ui>
  <Border class="hud-card"
          x="24" y="24"
          width="360" height="280">
    <VerticalStackPanel width="100%" height="100%" spacing="12">
      <Text class="heading" text="Mission status" />
      <Text id="score" text="Score: 0" />
      <ProgressBar id="health"
                   width="100%" height="20"
                   minimum="0" maximum="100" value="100" />
      <ItemsControl id="objective-list"
                    width="100%" height="*"
                    spacing="6" />
      <Button id="pause-button"
              class="primary"
              width="100%" height="44">
        <Text text="Pause" />
      </Button>
    </VerticalStackPanel>
  </Border>
</Ui>
```

Concrete C# type names become UXML tags by removing the `Ui` prefix, so
`UiProgressBar` is `<ProgressBar>` and `UiVerticalStackPanel` is
`<VerticalStackPanel>`. IDs are case-sensitive.

### 2. Style the authored elements

`Assets/Ui/master.ucss` can hold shared defaults:

```css
Text {
    font-family: monogram;
    font-size: 18px;
    color: #FFFFFF;
}

.heading {
    font-size: 26px;
}
```

`Assets/Ui/Hud/hud.ucss` is discovered automatically as the layout's sibling
stylesheet:

```css
.hud-card {
    padding: 18px;
    background-color: #151B24E8;
}

ProgressBar {
    track-tint: #343946;
    fill-tint: #50BE78;
}

Button.primary {
    padding: 8px 16px;
    background-color: #355E3B;
    hover-tint: #477C50;
    pressed-tint: #29482E;
}
```

See [UI stylesheets](Stylesheets.md) for selector scope, cascade order, value
syntax, component styles, and the supported CSS subset.

### 3. Load and wire the layout

During scene setup, create the frame and keep typed references to the elements
the game will update:

```csharp
using Dreambit.ECS;
using Dreambit.UI;

UiFrame hud = CreateEntity("hud")
    .AttachComponent<UiFrame>()
    .WithCss("Ui/master.ucss")
    .WithLayout("Ui/Hud/hud.uxml");

UiLayout layout = hud.Layout;
UiText scoreText = layout.GetRequired<UiText>("score");
UiProgressBar healthBar = layout.GetRequired<UiProgressBar>("health");
UiItemsControl objectiveList =
    layout.GetRequired<UiItemsControl>("objective-list");
UiButton pauseButton = layout.GetRequired<UiButton>("pause-button");

pauseButton.Clicked += _ => TogglePause();
```

`GetRequired<T>` is best for required authored elements: it fails immediately
when the ID is missing or has the wrong type. Use `Find` for genuinely optional
elements:

```csharp
if (layout.Find("tutorial-hint") is UiText tutorialHint)
    tutorialHint.Text = "Press E to interact";
```

## Change the UI at runtime

The references returned by `GetRequired<T>` are the live objects being updated
and drawn. Assign their properties when game state changes:

```csharp
scoreText.Text = $"Score: {score:N0}";
healthBar.Value = player.Health;

pauseButton.IsEnabled = !isLoading;
objectiveList.IsVisible = objectives.Count > 0;
```

Common runtime operations include:

| Goal | API |
| --- | --- |
| Change text | `UiText.Text`, `UiTextBox.Text` |
| Change a range | `UiProgressBar.Value`, `UiSlider.Value`, `UiScrollBar.Value` |
| Show or hide a subtree | `UiElement.IsVisible` |
| Enable or disable input for a subtree | `UiElement.IsEnabled` |
| Replace an image | `UiTexture.SpritePath` or `UiTexture.Sprite` |
| Move or resize an element | `X`, `Y`, `Width`, `Height`, `Anchor`, and `Origin` |
| Replace one control's content | `UiContentControl.SetContent(...)` |
| Add or remove children | `UiContainer.AddChild`, `RemoveChild`, and `ClearChildren` |
| Move keyboard/controller focus | `UiElement.Focus()` or `UiLayout.Focus(...)` |

Use the container methods instead of editing `Children` directly. They maintain
parent ownership, layout attachment, ID validation, and interaction state.

### Listen to controls

Controls expose events rather than requiring per-frame polling:

```csharp
UiSlider music = layout.GetRequired<UiSlider>("music-volume");
music.ValueChanged += (_, value) => SetMusicVolume(value / 100f);

UiTextBox playerName = layout.GetRequired<UiTextBox>("player-name");
playerName.TextChanged += (_, value) => profile.Name = value;

UiComboBox resolution = layout.GetRequired<UiComboBox>("resolution");
resolution.SelectionChanged += (_, index, value) =>
    ApplyResolution(index, value);
```

Button activation, selection, text input, sliders, and other controls use the
same routed input path for the supported pointer, keyboard, and controller
interactions described on their reference pages.

## Fill a list from game data

Use an `ItemsControl` when each data value should produce one arbitrary UI
element. Keep an empty host in UXML:

```xml
<ItemsControl id="objective-list"
              width="100%" height="*"
              spacing="6" />
```

Then materialize the current game data in C#:

```csharp
void RefreshObjectives(IEnumerable<string> objectives)
{
    objectiveList.SetItems(objectives, objective => new UiText
    {
        Width = UiLength.Percent(1f),
        Height = UiLength.Auto(),
        Text = $"• {objective}",
        FontPath = "monogram",
        FontSize = 18f,
        HorizontalAlignment = HorizontalAlignment.Left
    });
}
```

`SetItems` clears the existing children and invokes the template once for every
value. It is explicit refresh, not observable data binding. Call it when the
source collection changes.

For incremental changes, retain the created row and use:

```csharp
UiText row = new() { Text = "Find the observatory" };
objectiveList.AddItem(row);

// Later:
objectiveList.RemoveItem(row);
```

Use `AddItem`, `RemoveItem`, and updates to existing row properties when a list
changes frequently. Rebuilding is convenient for small and medium collections;
there is no recycling or virtualization for very large lists.

### Build an interactive list

`UiListBox` adds single selection and pointer/keyboard/controller navigation.
Return a `UiControl`, such as a button, when the row should show selected-state
visuals:

```csharp
using Microsoft.Xna.Framework;

UiListBox saves = layout.GetRequired<UiListBox>("save-list");

saves.SetItems(saveSlotNames, slotName =>
{
    var row = new UiButton
    {
        Width = UiLength.Percent(1f),
        Height = UiLength.Pixels(40),
        Background = new SolidColorBrush(),
        BackgroundTint = new Color(36, 39, 48),
        SelectedTint = new Color(53, 94, 130)
    };

    row.SetContent(new UiText
    {
        Width = UiLength.Percent(1f),
        Text = slotName,
        FontPath = "monogram",
        FontSize = 18f,
        HorizontalAlignment = HorizontalAlignment.Left
    });

    return row;
});

saves.SelectionChanged += (_, args) =>
    ShowSaveDetails(args.NewIndex);
```

UCSS is applied while UXML is constructed; it is not automatically applied to
elements created with `new`. Set presentation in the C# factory, or create rows
from a styled UXML component.

### Choose the right collection control

| Control | Use it for | Runtime API |
| --- | --- | --- |
| [`UiItemsControl`](Elements/UiItemsControl.md) | A generated stack of arbitrary elements | `SetItems`, `AddItem`, `RemoveItem`, `ClearItems` |
| [`UiListBox`](Elements/UiListBox.md) | An arbitrary-element list with one selected row | The item APIs plus `SelectedIndex` and `SelectionChanged` |
| [`UiComboBox`](Elements/UiComboBox.md) | A compact popup selector for strings | `SetItems(IEnumerable<string>)` and `SelectionChanged` |
| [`UiContainer`](Elements/UiContainer.md) | Fully manual dynamic child ownership | `AddChild`, `RemoveChild`, `ClearChildren` |

Prefer `UiComboBox.SetItems(...)` over mutating its `Items` collection directly,
because `SetItems` also rebuilds an existing popup.

## Reuse styled UXML components

Components are useful when dynamic rows need more structure or should inherit
UCSS styling. A component contains exactly one visual root.

`Assets/Ui/Components/objective-row.uxml`:

```xml
<UiComponent>
  <Border id="row" class="objective-row"
          width="100%" height="40">
    <Text id="label"
          width="100%"
          horizontal-alignment="Left" />
  </Border>
</UiComponent>
```

An optional `objective-row.ucss` beside it styles every instance. Create each
instance with a unique ID prefix, fill its data, and attach it:

```csharp
void RefreshObjectiveRows(IReadOnlyList<string> objectives)
{
    objectiveList.ClearItems();

    for (int i = 0; i < objectives.Count; i++)
    {
        string prefix = $"objective-{i}";
        UiElement row = hud.CreateComponent(
            "Ui/Components/objective-row.uxml",
            prefix);

        row.GetRequired<UiText>(prefix, "label").Text = objectives[i];
        objectiveList.AddItem(row);
    }
}
```

The prefix `objective-0` produces IDs such as `objective-0.row` and
`objective-0.label`, preventing collisions when the component is repeated.
`CreateComponent` returns a detached element; adding it to an items control or
container attaches it to the layout. See [`UiFrame`](../ecs/components/ui-frame.md)
and [runtime-created components](Stylesheets.md#runtime-created-components) for
the overload that adds another construction-time stylesheet.

Layouts can also declare and instantiate components directly in UXML:

```xml
<Ui>
  <Ui.Components>
    <Component name="MenuButton"
               source="~/Ui/Components/menu-button.uxml" />
  </Ui.Components>

  <MenuButton id-prefix="play" />
</Ui>
```

## Reloading and object lifetime

Changing `UiFrame.LayoutPath`, calling `LoadLayout`, or changing `CssPath` on a
loaded frame constructs a new retained tree. Existing element references and
event subscriptions belong to the old tree. After a reload, run the lookup and
wiring step again.

Use reloads for replacing a whole screen or rebuilding construction-time
styles. Use normal property assignment for scores, health, visibility,
selection, inventory contents, and other live game data.

## Important conventions

- UXML element width and height default to `100%`; C#-constructed base elements
  default to zero pixels unless their constructor changes a dimension.
- UXML dimensions accept pixels, percentages, or `*` for automatic content
  size. C# uses `UiLength.Pixels`, `UiLength.Percent`, and `UiLength.Auto`.
- Grid track definitions use `*` for weighted remaining space.
- Colors accept `#RRGGBB` and `#RRGGBBAA`.
- Thickness accepts one integer or `left,top,right,bottom` in UXML. UCSS uses
  CSS top/right/bottom/left shorthand.
- Brush properties use property-element syntax, such as
  `<Button.Background>...</Button.Background>`.
- An element can have only one parent. Remove or detach it before attaching it
  somewhere else.

## API reference

Start with the types that define the system's common behavior:

- [`UiFrame`](../ecs/components/ui-frame.md) — load, own, update, and draw a layout.
- [`UiElement`](Elements/UiElement.md) — geometry, visibility, input, focus, and lookup.
- [`UiContainer`](Elements/UiContainer.md) — dynamic child ownership.
- [`UiControl`](Elements/UiControl.md) — interactive visual states and templates.
- [`UiItemsControl`](Elements/UiItemsControl.md) — collections generated from game data.
- [UI stylesheets](Stylesheets.md) — construction-time styling and cascade rules.

### Elements

- Layout and composition:
  [`UiPanel`](Elements/UiPanel.md),
  [`UiCanvas`](Elements/UiCanvas.md),
  [`UiBorder`](Elements/UiBorder.md),
  [`UiGrid`](Elements/UiGrid.md),
  [`UiStackPanel`](Elements/UiStackPanel.md),
  [`UiVerticalStackPanel`](Elements/UiVerticalStackPanel.md),
  [`UiHorizontalStackPanel`](Elements/UiHorizontalStackPanel.md),
  [`UiUniformGrid`](Elements/UiUniformGrid.md),
  [`UiWrapPanel`](Elements/UiWrapPanel.md),
  [`UiViewbox`](Elements/UiViewbox.md), and
  [`UiSpacer`](Elements/UiSpacer.md).
- Content:
  [`UiText`](Elements/UiText.md),
  [`UiTexture`](Elements/UiTexture.md),
  [`UiContentControl`](Elements/UiContentControl.md), and
  [`UiOverlay`](Elements/UiOverlay.md).
- Interaction:
  [`UiButton`](Elements/UiButton.md),
  [`UiToggleButton`](Elements/UiToggleButton.md),
  [`UiCheckBox`](Elements/UiCheckBox.md),
  [`UiRadioButton`](Elements/UiRadioButton.md),
  [`UiTextBox`](Elements/UiTextBox.md),
  [`UiSlider`](Elements/UiSlider.md),
  [`UiScrollBar`](Elements/UiScrollBar.md), and
  [`UiComboBox`](Elements/UiComboBox.md).
- Collections and selection:
  [`UiItemsControl`](Elements/UiItemsControl.md),
  [`UiSelector`](Elements/UiSelector.md), and
  [`UiListBox`](Elements/UiListBox.md).
- Feedback and popup surfaces:
  [`UiProgressBar`](Elements/UiProgressBar.md),
  [`UiPopup`](Elements/UiPopup.md), and
  [`UiTooltip`](Elements/UiTooltip.md).
- Base types:
  [`UiElement`](Elements/UiElement.md),
  [`UiContainer`](Elements/UiContainer.md),
  [`UiContentControl`](Elements/UiContentControl.md),
  [`UiControl`](Elements/UiControl.md),
  [`UiStackPanelBase`](Elements/UiStackPanelBase.md), and
  [`UiRangeBase`](Elements/UiRangeBase.md).

### Brushes

- [`IUiBrush`](Brushes/IUiBrush.md) and [`UiBrush`](Brushes/UiBrush.md)
- [`SolidColorBrush`](Brushes/SolidColorBrush.md)
- [`SpriteBrush`](Brushes/SpriteBrush.md)
- [`NineSliceBrush`](Brushes/NineSliceBrush.md)
- [`TiledSpriteBrush`](Brushes/TiledSpriteBrush.md)
- [`OutlineBrush`](Brushes/OutlineBrush.md)
- [`LayeredBrush`](Brushes/LayeredBrush.md)
