# `UiContentControl`

Single-child element with padding, content alignment, tint, and a composable background brush.

**Status:** XML tag: `ContentControl`  
**Namespace:** `Dreambit.UI`  
**Source:** `DreambitEngine/UI/Elements/UiContentControl.cs`  
**Validated against:** DreambitEngine `main` / `ef6e5b9c600ad6e215c53ea287a0c7858884ce00`

## Inheritance

`UiElement` → `UiContainer` → `UiContentControl`

## When to use

Use as the base for controls that own exactly one arbitrary visual tree. Wrap multiple logical children in a stack, grid, or panel and assign that wrapper as the content.

## Declared API

### Properties and fields

| Member | Type | Behavior |
|---|---|---|
| `Content` | `UiElement` (read-only) | The one hosted child, or null. |
| `Padding` | `UiThickness` | Inset between bounds and content. |
| `ContentAlignment` | `UiAnchor` | Alignment of content within padded bounds; default `Center`. |
| `BackgroundTint` | `Color` | Tint passed to `Background`; default white. |
| `Background` | `IUiBrush` | Visual drawn before content. |

### Methods

| Member | Behavior |
|---|---|
| `AddChild(UiElement)` | Adds content and throws if content already exists. |
| `SetContent(UiElement)` | Replaces or clears content with correct detachment and invalidation. |

## Common inherited XML attributes

| XML attribute | Type | XML default | Meaning |
|---|---|---|---|
| `id` | `string` | empty | Case-sensitive lookup ID used by `UiLayout.Find` and `GetRequired<T>`. |
| `x` | `UiLength` | `0%` | Horizontal offset resolved against the parent width. |
| `y` | `UiLength` | `0%` | Vertical offset resolved against the parent height. |
| `width` | `UiLength` | `100%` | Fixed pixels, percentage, or `*` for automatic desired width. |
| `height` | `UiLength` | `100%` | Fixed pixels, percentage, or `*` for automatic desired height. |
| `anchor` | `UiAnchor` | `TopLeft` | Reference point on the parent. |
| `origin` | `UiAnchor` | `TopLeft` | Point on this element placed at the anchored position. |
| `z` | `int` | `0` | Sibling draw order. Higher values draw and hit-test above lower values. |
| `grid-row` | `int` | `0` | Zero-based row used by a `UiGrid` parent. |
| `grid-column` | `int` | `0` | Zero-based column used by a `UiGrid` parent. |
| `grid-row-span` | `int` | `1` | Grid row span, clamped to at least one. |
| `grid-column-span` | `int` | `1` | Grid column span, clamped to at least one. |
| `is-visible` | `bool` | `true` | Removes the element subtree from layout, drawing, and input when false. |
| `is-enabled` | `bool` | `true` | Disables input for the element subtree when false. |
| `is-hit-test-visible` | `bool` | type default | Controls whether this element can be the direct pointer target. |
| `is-focusable` | `bool` | type default | Controls keyboard/controller focus eligibility. |
| `captures-keyboard-input` | `bool` | type default | Consumes keyboard availability while focused. |
| `clip-to-bounds` | `bool` | `false` | Clips descendants to this element's bounds using the UI scissor stack. |

## Type-specific XML

| Attribute | Type | Default | Meaning |
|---|---|---|---|
| `padding` | `UiThickness` | `0` | One value or left,top,right,bottom. |
| `content-alignment` | `UiAnchor` | `Center` | Alignment within padded bounds. |
| `background-color` | `Color` | absent | Creates a `SolidColorBrush` and uses this color as its tint. |
| `background-tint` | `Color` | white | Brush tint. |

## XML example

```xml
<ContentControl width="320" height="120"
                padding="16"
                content-alignment="Center"
                background-tint="#202733">
    <ContentControl.Background>
        <NineSliceBrush sprite="Ui/panel.sprite" slice="8" />
    </ContentControl.Background>

    <Text width="100%" height="*"
          text="Single arbitrary content tree"
          font="monogram" font-size="20" />
</ContentControl>
```

## C# example

```csharp
var control = new UiContentControl
{
    Width = UiLength.Pixels(320),
    Height = UiLength.Pixels(120),
    Padding = UiThickness.Uniform(16),
    Background = new SolidColorBrush(),
    BackgroundTint = new Color(32, 39, 51)
};
control.SetContent(new UiText { Text = "Single content" });
```

## Layout behavior

- Natural size is the maximum of padded content size and `Background.MinimumSize`.
- Arrangement rewrites the content's position to zero and its anchor/origin to `ContentAlignment`.

## Ownership and lifecycle

- Exactly one direct child is allowed; use `SetContent` for replacement.
- An explicit `<ContentControl.Background>` property element replaces a
  `background-color` shorthand brush. An independently authored
  `background-tint` still applies to the replacement brush.

## Production pitfalls

- The removed `background-sprite` attribute throws an `XmlException`. Use a property element and brush.
- Do not rely on a child's authored `X`, `Y`, `Anchor`, or `Origin`; content alignment owns those values during arrangement.

## See also

- [`UiBorder`](UiBorder.md)
- [`UiControl`](UiControl.md)
- [`IUiBrush`](../Brushes/IUiBrush.md)

---

_Source reviewed 2026-08-03. This page documents current implemented behavior, not a proposed API._
