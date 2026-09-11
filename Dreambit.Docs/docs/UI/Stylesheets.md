# UI stylesheets

Dreambit stylesheets remove repeated presentation and layout attributes from
UXML. They are a small, construction-time subset of CSS, not a browser CSS
engine. Rules are resolved once while `UiLoader` creates retained `UiElement`
instances. Update, layout, and draw do not reevaluate styles, so later C#
property changes remain authoritative.

## Basic use

Create a global stylesheet for a `UiFrame` and keep a layout-specific file next
to the UXML:

```text
Assets/Ui/Stylesheets/master.ucss
Assets/Ui/MainMenu/main-menu.uxml
Assets/Ui/MainMenu/main-menu.ucss
```

```csharp
var frame = CreateEntity("ui-frame")
    .AttachComponent<UiFrame>()
    .WithCss("Ui/Stylesheets/master.ucss")
    .WithLayout("Ui/MainMenu/main-menu.uxml");
```

```css
Text {
    font-family: monogram;
    color: #FFFFFF;
}

.h1 {
    font-size: 32px;
}

Button.primary {
    width: 180px;
    height: 48px;
    padding: 8px 16px;
    background-color: #355E3B;
}
```

```xml
<Ui>
  <Text class="h1 centered" text="Rootbound" />
  <Button class="primary" />
</Ui>
```

`UiElement.StyleClasses` exposes the authored class tokens as a read-only list.
Classes are metadata for construction-time matching; changing them at runtime
and recomputing styles is not supported.

## Supported selectors

Only three selector forms are supported, and element and class names are
case-sensitive:

```css
Text { }
.h1 { }
Text.h1 { }
```

Within one stylesheet, specificity is:

```text
Text < .h1 < Text.h1
```

For equal specificity, the later rule wins. Within a rule, a later declaration
of the same Dreambit property wins. Selector lists, descendant/child/attribute
selectors, pseudo selectors, state selectors, media queries, variables,
functions, animations, transitions, and inheritance are not supported.

## Cascade and component scope

Stylesheet layers are stronger by locality before selector specificity is
considered:

```text
C# defaults
→ UiFrame global stylesheet
→ layout sibling stylesheet
→ outer component sibling stylesheet
→ nested component sibling stylesheet
→ explicit XML attributes
→ component instance attributes
```

For example, a plain `Text` rule in a component stylesheet beats a
`Text.warning` rule in the global stylesheet. Explicit XML is always stronger,
including values such as `width="0"` and `is-visible="false"`.

For `Ui/MainMenu/main-menu.uxml`, Dreambit optionally loads
`Ui/MainMenu/main-menu.ucss`. For a component at
`Ui/Components/menu-button.uxml`, it optionally loads
`Ui/Components/menu-button.ucss`. A component stylesheet is active only while
constructing that component subtree. Nested component boundaries add nested
layers, and local styles never leak to host siblings. A missing automatic
sibling is normal.

Component template and instance classes are merged in template-then-instance
order with duplicate tokens removed. Instance attributes are applied to the
expanded component XML before style resolution, so they remain final authored
overrides.

## Supported values and property mappings

Stylesheet values use CSS notation. XML-only value syntax is normalized
internally but is not accepted as public CSS syntax.

| CSS | Dreambit meaning |
| --- | --- |
| `width: 180px` | fixed pixel width |
| `width: 50%` | parent-relative width |
| `width: auto` | automatic/content width |
| `width: 0` | zero width; nonzero unitless lengths are rejected |
| `x: -20px` | signed pixel offset |
| `font-size: 16px` | pixel font size |
| `padding: 8px 16px` | CSS top/right/bottom/left shorthand, with 1–4 values |
| `color: #RRGGBB` | `text-color` on elements that support it |
| `color: #RRGGBBAA` | `text-color` including alpha |
| `background-color: #RRGGBB` | a `SolidColorBrush` background on content controls |
| `font-family: monogram` | Dreambit `font` asset name |
| `font-family: "Font Name"` | quoted font asset name |
| `z-index: 4` | Dreambit `z` |

Dreambit-specific declarations use normal CSS declaration grammar and are
passed through the existing element parser. Examples include:

```css
Panel {
    anchor: center;
    origin: bottomright;
    is-enabled: true;
    is-focusable: false;
    grid-row: 2;
    grid-column: 1;
}
```

The built-in length normalization also applies to `x` and `y`. Other custom
values preserve identifier, quoted string, number, hash, dimension, percentage,
and comma tokens in a normalized authored form. The target element's existing
XML parser remains the final authority. Structural XML attributes `id`,
`class`, `source`, and `id-prefix` cannot be set from CSS.

`width`, `height`, and `font-size` must be non-negative. `x` and `y` are
offsets and may be negative. CSS token types remain significant when the value
reaches Dreambit's shared parsing helpers: Boolean and enum properties require
identifiers, integer/float properties require numbers, and color properties
require unquoted hash colors. For example, `is-enabled: false`, `grid-row: 2`,
`anchor: center`, and `hover-tint: #FF0000` are valid, while quoted versions of
those values are rejected.

The standard `font` shorthand is not implemented and is rejected; use
`font-family` for Dreambit's font asset name.

Comments, whitespace, single- and double-quoted strings, multiple declarations,
optional final semicolons, and empty declarations are supported. Colors must be
unquoted hex tokens; quoted `"#FFFFFF"`, `width: *`, and `width: 180` are
rejected.

## Runtime-created components

The existing overload automatically uses the frame's global stylesheet, the
current layout sibling when a layout exists, and the component's own sibling:

```csharp
var inventory = frame.CreateComponent(
    "Ui/Components/inventory.uxml",
    idPrefix: "inventory");
```

Supply one additional required stylesheet at construction time with the
three-argument overload:

```csharp
var inventory = frame.CreateComponent(
    componentPath: "Ui/Components/inventory.uxml",
    idPrefix: null,
    additionalCssPath: "Ui/Stylesheets/inventory-theme.ucss");
```

The runtime component order is global, current layout sibling, root component
sibling, additional stylesheet, then deeper nested component siblings and
explicit XML. The returned element is already parsed and detached. Attaching it
to this or another layout does not restyle it.

## Assets and errors

The Asset Baker validates source `.ucss` and emits versioned UTF-8 `.cssb`.
Runtime loading always uses baked content in loose-file, blob-manifest, or PAK
mode; it does not read source `.ucss` files from `Assets`.

Legacy `.xml`/`.css` UI pairs remain supported, but new UI assets should use
`.uxml`/`.ucss`.

An explicitly configured `CssPath` or `additionalCssPath` is required and fails
clearly when missing. Automatic layout and component siblings are optional.
Syntax and application errors report the stylesheet path, selector/property
context, target element type when applicable, and line/column.

Unknown properties are target-dependent: an unmatched rule is harmless, but a
matching element must consume the declaration. Custom elements should use
the typed `UiXmlParser` helpers, including `ParseEnum`, so CSS-origin token kinds
are validated consistently. A custom parser that reads `XmlNode.Attributes`
directly must call `UiXmlParser.MarkAttributeHandled(node, propertyName)` so
Dreambit can distinguish a consumed CSS declaration from an unsupported
property.

Stylesheets and selector indexes are cached only for one top-level UI load.
Reloading a frame rebuilds a fresh retained tree; there is no live stylesheet
reload or runtime class recomputation in this version.
