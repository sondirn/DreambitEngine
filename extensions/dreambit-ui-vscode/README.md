# Dreambit UI for Visual Studio Code

Dreambit UI adds first-class editing for the engine's authored UI formats:

- `.uxml` retained UI layouts and reusable components
- `.ucss` lightweight Dreambit stylesheets

The extension preserves familiar XML/CSS syntax highlighting and editing behavior, then adds suggestions based on Dreambit's actual UI vocabulary.

## Features

- Dreambit element, brush, property-element, component, and attribute completion in UXML
- Dreambit element, class, property, and typed-value completion in UCSS
- multiple-class suggestions collected from workspace UXML and UCSS files
- component-name suggestions collected from the current document's `<Component>` declarations
- UXML `source` path suggestions and Go to Definition
- enum, boolean, length, color, thickness, ID-reference, and font value suggestions
- hover documentation for built-in elements, brushes, attributes, selectors, and properties
- document outlines for UXML elements and UCSS rules
- color decorators and the VS Code color picker for `#RRGGBB` and `#RRGGBBAA`
- layout, component, include, brush, selector, and common-control snippets

The catalog includes Dreambit's built-in UI types. Projects can add completion names for assembly-discovered custom types through `dreambitUi.customElements` and `dreambitUi.customProperties`.

## Run locally

1. Open `extensions/dreambit-ui-vscode` in Visual Studio Code.
2. Press `F5` and choose **Extension Development Host** if prompted.
3. Open a `.uxml` or `.ucss` file in the development window.

The extension has no runtime npm dependencies or build step. Run its checks with:

```text
npm run check
```

## Dreambit-specific behavior

UCSS intentionally models Dreambit's small selector language, not browser CSS. Suggestions cover:

```css
Text { }
.h1 { }
Text.h1 { }
```

The extension suggests Dreambit's CSS aliases:

- `font-family` maps to the UXML `font` attribute
- `color` maps to `text-color`
- `z-index` maps to `z`

Structural values such as `id`, `class`, `source`, and `id-prefix` are offered only where they are valid in UXML, never as UCSS properties.

Workspace scanning is enabled by default and is capped at 1,000 `.uxml`/`.ucss` files. Disable it with `dreambitUi.scanWorkspace` if a very large workspace needs strictly local suggestions.

## Packaging

Install Microsoft's `@vscode/vsce` tool and run `vsce package` from this directory to produce a `.vsix` package. The package metadata is already self-contained.
