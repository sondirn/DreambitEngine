# Dreambit UI Built-in Catalog

Source revision: `editor-refactor` @ `2606a7b6fd41ac1fcb66b83cfc189393b5074b22`.

The runtime registration rule is mirrored from `DreambitEngine/UI/UiLoader.cs`: only concrete, non-interface, non-generic `UiElement` / `IUiBrush` implementations are valid UXML type names.

## Elements — 30

1. `Container` — `UiContainer`
2. `Panel` — `UiPanel`
3. `Canvas` — `UiCanvas`
4. `ContentControl` — `UiContentControl`
5. `Control` — `UiControl`
6. `Border` — `UiBorder`
7. `Overlay` — `UiOverlay`
8. `Button` — `UiButton`
9. `ToggleButton` — `UiToggleButton`
10. `CheckBox` — `UiCheckBox`
11. `RadioButton` — `UiRadioButton`
12. `ComboBox` — `UiComboBox`
13. `Popup` — `UiPopup`
14. `Tooltip` — `UiTooltip`
15. `Grid` — `UiGrid`
16. `UniformGrid` — `UiUniformGrid`
17. `WrapPanel` — `UiWrapPanel`
18. `VerticalStackPanel` — `UiVerticalStackPanel`
19. `HorizontalStackPanel` — `UiHorizontalStackPanel`
20. `StackPanel` — `UiStackPanel`
21. `ItemsControl` — `UiItemsControl`
22. `ListBox` — `UiListBox`
23. `Text` — `UiText`
24. `TextBox` — `UiTextBox`
25. `Texture` — `UiTexture`
26. `Slider` — `UiSlider`
27. `ScrollBar` — `UiScrollBar`
28. `ProgressBar` — `UiProgressBar`
29. `Spacer` — `UiSpacer`
30. `Viewbox` — `UiViewbox`

### Correctly excluded abstract element bases

- `UiElement`
- `UiStackPanelBase`
- `UiRangeBase`
- `UiSelector`

## Brushes — 6

1. `SolidColorBrush`
2. `SpriteBrush`
3. `TiledSpriteBrush`
4. `NineSliceBrush`
5. `OutlineBrush`
6. `LayeredBrush`

### Correctly excluded brush bases

- `IUiBrush` (interface)
- `UiBrush` (abstract)

## Structural UXML nodes

These are syntax/composition nodes rather than `UiElement`/`IUiBrush` runtime types, and the plugin supports them separately:

- `Ui`
- `UiComponent`
- `Ui.Components`
- `UiComponent.Components`
- `Component`
- `Include`
- named component instances declared through `<Component name="..." source="..." />`

## Future/custom types

The built-in list above is a fallback, not a ceiling. The Rider plugin scans C# project content for concrete classes derived from `UiElement` or implementing/deriving from `IUiBrush`/`UiBrush`, follows inheritance, and honors `[UiXmlName("...")]` when present.
