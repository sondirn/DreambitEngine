# Dreambit UI for JetBrains Rider

Local JetBrains Rider plugin source for DreambitEngine's authored UI formats:

- `.uxml` retained UI layouts and reusable components
- `.ucss` Dreambit stylesheets

This package was prepared from the current `editor-refactor` branch at commit:

`2606a7b6fd41ac1fcb66b83cfc189393b5074b22` (`update to 0.7.5`)

It is intentionally a **local source package**. Nothing in this package publishes to JetBrains Marketplace or pushes to GitHub.

## Source-of-truth policy

The Rider plugin does not treat the VS Code extension's hardcoded catalog as authoritative.

Dreambit's runtime `UiLoader.UiTypeCatalog` discovers UI types from loaded assemblies:

- concrete `UiElement` subclasses become UXML elements;
- concrete `IUiBrush` implementations become brush elements;
- abstract/interface/generic types are excluded;
- `[UiXmlName("...")]` overrides the XML name;
- otherwise element names drop a leading `Ui`, while brush names use the CLR type name.

The Rider plugin mirrors that model in two layers:

1. **Complete built-in fallback catalog** for the current engine revision: 30 concrete elements and 6 concrete brushes.
2. **Workspace C# discovery** that scans project source for additional/future `UiElement`/`IUiBrush` types, follows inheritance, honors `[UiXmlName]`, and collects authored attributes/property elements when possible.

The workspace scan is deliberately best-effort rather than a C# compiler. The built-in catalog is therefore still present so Dreambit's shipped controls work even in a game project that references the engine only as a binary/NuGet package.

See [CATALOG.md](CATALOG.md) for the exact built-in list.

## Features

### UXML

- XML syntax highlighting/parsing supplied by Rider's XML support.
- Dreambit root completion: `Ui`, `UiComponent`.
- Completion for every current concrete Dreambit UI element.
- Completion for every current concrete Dreambit brush.
- Completion for custom/future UI elements and brushes discovered from C# source.
- Element-specific attribute completion.
- Typed values for booleans, enums, lengths, sizes, colors, thicknesses, numbers, IDs, and common font values.
- `class` completion from classes found in workspace `.uxml` and `.ucss` files.
- Property-element completion such as `Button.Background`, `Slider.TrackBrush`, and `Element.Tooltip`.
- Brush completion inside property elements and layered brushes.
- `<Ui.Components>`, `<UiComponent.Components>`, `<Component>`, named-component, and `<Include>` authoring support.
- `source="..."` UXML path completion.
- Go to Declaration from a UXML `source` value.
- `id-prefix` support for includes/named component instances.
- Quick Documentation/hover help.
- Hex color preview/editor support for `#RRGGBB` and `#RRGGBBAA`.
- Live templates: `dbui`, `dbcomponent`, `dbinclude`.

### UCSS

- CSS syntax highlighting/parsing supplied by Rider's CSS support.
- Dreambit selector completion for exactly the supported selector forms:
  - `Element { }`
  - `.class { }`
  - `Element.class { }`
- Element-specific property completion.
- Workspace class completion.
- Typed property values.
- Dreambit aliases:
  - `font-family` -> UXML `font`
  - `color` -> UXML `text-color`
  - `z-index` -> UXML `z`
- Structural UXML-only properties (`id`, `class`, `source`, `id-prefix`) are not offered as UCSS properties.
- Quick Documentation/hover help.
- Hex color preview/editor support.
- Generic browser-CSS inspections are suppressed for `.ucss`, because UCSS is deliberately a smaller Dreambit dialect.
- Live templates: `dbclass`, `dbelement`.

## Requirements

- JetBrains Rider 2026.1.x (build 261) is the primary target.
- Plugin metadata permits Rider 2026.2.x (build 262) as well.
- JDK 21 for the 2026.1 build target.
- Internet access on the first build so Gradle can obtain the Rider SDK and bundled CSS plugin metadata/dependencies.

## Build on Windows

From PowerShell in this directory:

```powershell
./build.ps1
```

The script prefers an existing Gradle wrapper, then an installed `gradle`, and otherwise downloads a local Gradle distribution into `.tools/`. It only runs `buildPlugin`; it does not publish anything.

The installable plugin ZIP will be under:

```text
build/distributions/
```

## Build manually

With Gradle 9+ installed:

```text
gradle buildPlugin
```

To launch a Rider sandbox for testing:

```text
gradle runIde
```

To run JetBrains plugin validation once dependencies are available:

```text
gradle verifyPluginProjectConfiguration
```

## Install in Rider

1. Build the plugin.
2. Open **Settings | Plugins** in Rider.
3. Click the gear icon.
4. Choose **Install Plugin from Disk...**.
5. Select the ZIP from `build/distributions/`.
6. Restart Rider when prompted.

Do **not** extract the built distribution ZIP before installing it.

## Development notes

The plugin reuses Rider's XML and CSS parsers rather than implementing separate parsers for Dreambit. Dreambit-specific completion/documentation/navigation is layered on top and gated by the `.uxml` / `.ucss` extension.

The C# workspace discovery intentionally has bounded scans (1,000 UXML/UCSS files and 5,000 C# files) and caches against the PSI modification counter. This keeps it appropriate for normal game workspaces without putting regex/reflection-like work in an editor hot path on every keystroke.

## Verification checklist

After installing the plugin, create a scratch `.uxml` and verify:

```xml
<Ui>
    <VerticalStackPanel>
        <Button>
            <Button.Background>
                <NineSliceBrush sprite="" />
            </Button.Background>
            <Text text="Hello" />
        </Button>
    </VerticalStackPanel>
</Ui>
```

Expected:

- typing `<` inside `Ui` offers all 30 built-in element tags;
- typing `<` inside `Button.Background` offers all 6 built-in brushes;
- typing inside a tag offers only valid/common Dreambit attributes plus any discovered project-defined attributes;
- `class="..."` suggests classes found in UXML/UCSS;
- a component `source` suggests `.uxml` paths and Go to Declaration opens the selected file;
- `#FFFFFF80` receives a color gutter/editor affordance.

Then create a `.ucss` file:

```css
Button.primary {
    background-tint: #FFFFFF;
    width: 160px;
}
```

Expected:

- `Button` and `.primary` are suggested;
- only Dreambit's selector subset is treated as authored UCSS;
- property completion is Dreambit-specific;
- `font-family`, `color`, and `z-index` are offered as the supported aliases.


## Windows build prerequisites

`build.ps1` requires a Java 21 **JDK** (not only Rider's bundled JBR runtime). If no suitable JDK is installed, the script downloads a private Temurin JDK 21 into `.tools/jdk-21` and uses it only for this plugin build. It does not change the machine-wide Java installation.
