'use strict';

const anchorValues = [
  'TopLeft', 'TopCenter', 'TopRight',
  'CenterLeft', 'Center', 'CenterRight',
  'BottomLeft', 'BottomCenter', 'BottomRight'
];

const definitions = {
  id: attribute('id', 'string', 'Unique identifier used by UiLayout.Find. XML only.'),
  class: attribute('class', 'class-list', 'Whitespace-separated style classes. XML only.'),
  x: attribute('x', 'length', 'Horizontal position: pixels or a percentage.'),
  y: attribute('y', 'length', 'Vertical position: pixels or a percentage.'),
  width: attribute('width', 'size', 'Element width: pixels, percentage, or auto.'),
  height: attribute('height', 'size', 'Element height: pixels, percentage, or auto.'),
  anchor: attribute('anchor', 'enum', 'Reference point on the parent.', anchorValues),
  origin: attribute('origin', 'enum', 'Reference point on this element.', anchorValues),
  z: attribute('z', 'integer', 'Draw and hit-test order within the parent.'),
  'grid-row': attribute('grid-row', 'integer', 'Zero-based grid row.'),
  'grid-column': attribute('grid-column', 'integer', 'Zero-based grid column.'),
  'grid-row-span': attribute('grid-row-span', 'integer', 'Number of grid rows occupied.'),
  'grid-column-span': attribute('grid-column-span', 'integer', 'Number of grid columns occupied.'),
  'is-visible': boolAttribute('is-visible', 'Whether the element participates in layout and drawing.'),
  'is-enabled': boolAttribute('is-enabled', 'Whether the element accepts interaction.'),
  'is-hit-test-visible': boolAttribute('is-hit-test-visible', 'Whether pointer hit testing can target the element.'),
  'is-focusable': boolAttribute('is-focusable', 'Whether keyboard/gamepad focus can target the element.'),
  'captures-keyboard-input': boolAttribute('captures-keyboard-input', 'Whether the element consumes keyboard input.'),
  'clip-to-bounds': boolAttribute('clip-to-bounds', 'Whether descendants are clipped to the element bounds.'),
  'background-color': colorAttribute('background-color', 'Creates a solid background brush with this tint.'),
  'background-tint': colorAttribute('background-tint', 'Tint supplied to the background brush.'),
  padding: attribute('padding', 'thickness', 'Insets in left, top, right, bottom order in UXML; CSS accepts 1–4 px values.'),
  'content-alignment': attribute('content-alignment', 'enum', 'Alignment of content inside a content control.', anchorValues),
  'hover-tint': colorAttribute('hover-tint', 'Tint multiplier while the pointer is over the control.'),
  'pressed-tint': colorAttribute('pressed-tint', 'Tint multiplier while the control is pressed.'),
  'focused-tint': colorAttribute('focused-tint', 'Tint multiplier while the control has focus.'),
  'disabled-tint': colorAttribute('disabled-tint', 'Tint multiplier while the control is disabled.'),
  'checked-tint': colorAttribute('checked-tint', 'Tint multiplier while a toggle control is checked.'),
  'selected-tint': colorAttribute('selected-tint', 'Tint multiplier while the control is selected.'),
  'open-tint': colorAttribute('open-tint', 'Tint multiplier while the control is open.'),
  text: attribute('text', 'string', 'Displayed or editable text.'),
  placeholder: attribute('placeholder', 'string', 'Text shown when a text box is empty.'),
  font: attribute('font', 'asset', 'Dreambit font asset path. Use font-family in UCSS.'),
  'font-size': attribute('font-size', 'number', 'Font size in pixels.'),
  'text-color': colorAttribute('text-color', 'Text color. The UCSS color alias maps here.'),
  'horizontal-alignment': attribute('horizontal-alignment', 'enum', 'Horizontal text alignment.', ['Left', 'Center', 'Right']),
  'multi-line': boolAttribute('multi-line', 'Whether text wraps over multiple lines.'),
  'auto-resize-height': boolAttribute('auto-resize-height', 'Whether text changes its own height to fit wrapped lines.'),
  'max-length': attribute('max-length', 'integer', 'Maximum editable character count; zero means unlimited.'),
  'password-character': attribute('password-character', 'character', 'Optional character used to mask text input.'),
  'placeholder-color': colorAttribute('placeholder-color', 'Text box placeholder color.'),
  'selection-color': colorAttribute('selection-color', 'Text box selection highlight color.'),
  'caret-color': colorAttribute('caret-color', 'Text box caret color.'),
  sprite: attribute('sprite', 'asset', 'Sprite asset path.'),
  tint: colorAttribute('tint', 'Texture tint.'),
  orientation: attribute('orientation', 'enum', 'Layout or fill axis.', ['Vertical', 'Horizontal']),
  spacing: attribute('spacing', 'integer', 'Space between adjacent children.'),
  'line-spacing': attribute('line-spacing', 'integer', 'Space between wrapped lines.'),
  'column-spacing': attribute('column-spacing', 'integer', 'Space between uniform-grid columns.'),
  'row-spacing': attribute('row-spacing', 'integer', 'Space between uniform-grid rows.'),
  'cross-alignment': attribute('cross-alignment', 'enum', 'Child alignment on the cross axis.', ['Start', 'Center', 'End']),
  'grow-direction': attribute('grow-direction', 'enum', 'Placement of the complete stack on its primary axis.', ['Start', 'Top', 'Left', 'Center', 'End', 'Bottom', 'Right']),
  'row-definitions': attribute('row-definitions', 'grid-definitions', 'Comma-separated Grid lengths, for example *, 2*, 40.'),
  'column-definitions': attribute('column-definitions', 'grid-definitions', 'Comma-separated Grid lengths, for example *, 2*, 120.'),
  rows: attribute('rows', 'integer', 'Explicit uniform-grid row count; zero is automatic.'),
  columns: attribute('columns', 'integer', 'Explicit uniform-grid column count; zero is automatic.'),
  'blocks-input': boolAttribute('blocks-input', 'Whether the overlay blocks input to content behind it.'),
  'is-checked': boolAttribute('is-checked', 'Initial checked state.'),
  group: attribute('group', 'string', 'Radio-button group name.'),
  minimum: attribute('minimum', 'number', 'Minimum range value.'),
  maximum: attribute('maximum', 'number', 'Maximum range value.'),
  step: attribute('step', 'number', 'Range increment.'),
  value: attribute('value', 'number', 'Current range value.'),
  'track-thickness': attribute('track-thickness', 'integer', 'Slider track thickness in pixels.'),
  'thumb-size': attribute('thumb-size', 'integer', 'Slider thumb size in pixels.'),
  'track-tint': colorAttribute('track-tint', 'Track brush tint.'),
  'fill-tint': colorAttribute('fill-tint', 'Filled-range brush tint.'),
  'thumb-tint': colorAttribute('thumb-tint', 'Thumb brush tint.'),
  'viewport-size': attribute('viewport-size', 'number', 'Visible portion represented by a scrollbar thumb.'),
  'large-change': attribute('large-change', 'number', 'Scrollbar page increment.'),
  'minimum-thumb-size': attribute('minimum-thumb-size', 'integer', 'Smallest scrollbar thumb size in pixels.'),
  'indicator-size': attribute('indicator-size', 'integer', 'Checkbox indicator size in pixels.'),
  'indicator-spacing': attribute('indicator-spacing', 'integer', 'Space between indicator and content.'),
  'indicator-tint': colorAttribute('indicator-tint', 'Checkbox indicator brush tint.'),
  'mark-tint': colorAttribute('mark-tint', 'Checkbox mark brush tint.'),
  'item-height': attribute('item-height', 'integer', 'Combo-box popup item height.'),
  items: attribute('items', 'csv', 'Comma-separated combo-box items.'),
  'selected-index': attribute('selected-index', 'integer', 'Initially selected item index; -1 means no selection.'),
  'popup-tint': colorAttribute('popup-tint', 'Combo-box popup tint.'),
  'is-open': boolAttribute('is-open', 'Whether a popup is initially open.'),
  'stays-open': boolAttribute('stays-open', 'Whether a popup remains open after outside interaction.'),
  'placement-target': attribute('placement-target', 'id-reference', 'ID of the element used to place a popup.'),
  'horizontal-offset': attribute('horizontal-offset', 'integer', 'Popup horizontal offset in pixels.'),
  'vertical-offset': attribute('vertical-offset', 'integer', 'Popup vertical offset in pixels.'),
  placement: attribute('placement', 'enum', 'Popup placement relative to its target.', ['Bottom', 'Top', 'Left', 'Right', 'Center', 'Absolute']),
  delay: attribute('delay', 'number', 'Tooltip delay in seconds.'),
  stretch: attribute('stretch', 'enum', 'How viewbox content fits its bounds.', ['None', 'Fill', 'Uniform', 'UniformToFill'])
};

const commonAttributes = [
  'id', 'class', 'x', 'y', 'width', 'height', 'anchor', 'origin', 'z',
  'grid-row', 'grid-column', 'grid-row-span', 'grid-column-span',
  'is-visible', 'is-enabled', 'is-hit-test-visible', 'is-focusable',
  'captures-keyboard-input', 'clip-to-bounds'
];

const contentAttributes = ['background-color', 'padding', 'content-alignment', 'background-tint'];
const controlAttributes = ['hover-tint', 'pressed-tint', 'focused-tint', 'disabled-tint', 'checked-tint', 'selected-tint', 'open-tint'];
const rangeAttributes = ['minimum', 'maximum', 'step', 'value'];
const sliderAttributes = ['orientation', 'track-thickness', 'thumb-size', 'track-tint', 'fill-tint', 'thumb-tint'];
const stackAttributes = ['padding', 'spacing', 'cross-alignment', 'grow-direction'];

const elements = {
  Container: element('General-purpose retained UI container.'),
  Panel: element('Basic container for freely positioned children.'),
  Canvas: element('Panel intended for explicit child positioning.'),
  ContentControl: element('Container with one content child and a brush background.', contentAttributes, ['Background']),
  Control: element('Interactive single-content control with focus and pointer state tints.', [...contentAttributes, ...controlAttributes], ['Background']),
  Border: element('Content control used to provide a background or border brush.', contentAttributes, ['Background']),
  Overlay: element('Content control that can block interaction behind it.', [...contentAttributes, 'blocks-input'], ['Background']),
  Button: element('Focusable clickable content control.', [...contentAttributes, ...controlAttributes], ['Background']),
  ToggleButton: element('Button with a persistent checked state.', [...contentAttributes, ...controlAttributes, 'is-checked'], ['Background']),
  CheckBox: element('Toggle button with indicator and mark brushes.', [...contentAttributes, ...controlAttributes, 'is-checked', 'indicator-size', 'indicator-spacing', 'indicator-tint', 'mark-tint'], ['Background', 'IndicatorBrush', 'MarkBrush']),
  RadioButton: element('Checkbox that is mutually exclusive within a named group.', [...contentAttributes, ...controlAttributes, 'is-checked', 'indicator-size', 'indicator-spacing', 'indicator-tint', 'mark-tint', 'group'], ['Background', 'IndicatorBrush', 'MarkBrush']),
  ComboBox: element('Focusable drop-down selector populated from a comma-separated item list.', [...contentAttributes, ...controlAttributes, 'font', 'font-size', 'item-height', 'items', 'selected-index', 'text-color', 'popup-tint'], ['Background']),
  Popup: element('Floating content control placed relative to another element.', [...contentAttributes, ...controlAttributes, 'is-open', 'stays-open', 'placement-target', 'horizontal-offset', 'vertical-offset', 'placement'], ['Background']),
  Tooltip: element('Popup shown for another element after a configurable delay.', [...contentAttributes, ...controlAttributes, 'is-open', 'stays-open', 'placement-target', 'horizontal-offset', 'vertical-offset', 'placement', 'delay'], ['Background']),
  Grid: element('Container with authored row and column definitions.', ['rows', 'columns', 'row-definitions', 'column-definitions', 'padding']),
  UniformGrid: element('Container that gives every cell the same size.', ['rows', 'columns', 'spacing', 'column-spacing', 'row-spacing', 'padding']),
  WrapPanel: element('Container that wraps children onto additional lines.', ['orientation', 'spacing', 'line-spacing', 'cross-alignment', 'padding']),
  VerticalStackPanel: element('Container that arranges children vertically.', stackAttributes),
  HorizontalStackPanel: element('Container that arranges children horizontally.', stackAttributes),
  StackPanel: element('Legacy configurable-axis stack panel.', [...stackAttributes, 'orientation']),
  ItemsControl: element('Stack panel with an item-oriented API.', [...stackAttributes, 'orientation']),
  ListBox: element('Selectable stack of child items.', [...stackAttributes, 'orientation', 'selected-index', 'background-tint'], ['Background']),
  Text: element('Renders text with a Dreambit font asset.', ['text', 'font-size', 'font', 'multi-line', 'auto-resize-height', 'horizontal-alignment', 'text-color']),
  TextBox: element('Focusable single-line text editor.', [...contentAttributes, ...controlAttributes, 'text', 'placeholder', 'font', 'font-size', 'max-length', 'password-character', 'text-color', 'placeholder-color', 'selection-color', 'caret-color'], ['Background']),
  Texture: element('Renders a sprite asset.', ['sprite', 'tint']),
  Slider: element('Interactive numeric range control.', [...contentAttributes, ...controlAttributes, ...rangeAttributes, ...sliderAttributes], ['Background', 'TrackBrush', 'FillBrush', 'ThumbBrush']),
  ScrollBar: element('Slider whose thumb represents a viewport.', [...contentAttributes, ...controlAttributes, ...rangeAttributes, ...sliderAttributes, 'viewport-size', 'large-change', 'minimum-thumb-size'], ['Background', 'TrackBrush', 'FillBrush', 'ThumbBrush']),
  ProgressBar: element('Non-interactive visual representation of a numeric range.', [...contentAttributes, ...controlAttributes, ...rangeAttributes, 'orientation', 'track-tint', 'fill-tint'], ['Background', 'TrackBrush', 'FillBrush']),
  Spacer: element('Empty layout element used to reserve space.'),
  Viewbox: element('Scales one content child to fit its available bounds.', [...contentAttributes, 'stretch'], ['Background'])
};

const brushes = {
  SolidColorBrush: brush('Fills the owner bounds using its tint.'),
  SpriteBrush: brush('Stretches one sprite over the owner bounds.', ['sprite']),
  TiledSpriteBrush: brush('Tiles one sprite at native size.', ['sprite']),
  NineSliceBrush: brush('Draws a sprite with independently scalable nine-slice regions.', ['sprite', 'slice', 'slice-left', 'slice-top', 'slice-right', 'slice-bottom']),
  OutlineBrush: brush('Draws a solid outline using the supplied tint.', ['thickness', 'left', 'top', 'right', 'bottom']),
  LayeredBrush: brush('Draws child brushes in back-to-front order.')
};

const brushAttributes = {
  sprite: definitions.sprite,
  slice: attribute('slice', 'thickness', 'Default nine-slice edge thickness.'),
  'slice-left': attribute('slice-left', 'integer', 'Nine-slice left edge thickness.'),
  'slice-top': attribute('slice-top', 'integer', 'Nine-slice top edge thickness.'),
  'slice-right': attribute('slice-right', 'integer', 'Nine-slice right edge thickness.'),
  'slice-bottom': attribute('slice-bottom', 'integer', 'Nine-slice bottom edge thickness.'),
  thickness: attribute('thickness', 'thickness', 'Default outline thickness.'),
  left: attribute('left', 'integer', 'Outline left thickness.'),
  top: attribute('top', 'integer', 'Outline top thickness.'),
  right: attribute('right', 'integer', 'Outline right thickness.'),
  bottom: attribute('bottom', 'integer', 'Outline bottom thickness.')
};

const cssAliases = {
  'font-family': 'font',
  color: 'text-color',
  'z-index': 'z'
};

const xmlToCss = {
  font: 'font-family',
  'text-color': 'color',
  z: 'z-index'
};

const structuralAttributes = new Set(['id', 'class', 'source', 'id-prefix']);

const elementAttributeOverrides = {
  Grid: {
    rows: attribute('rows', 'grid-definitions', 'Preferred alias for Grid row-definitions.'),
    columns: attribute('columns', 'grid-definitions', 'Preferred alias for Grid column-definitions.')
  }
};

function attribute(name, type, description, values = []) {
  return { name, type, description, values };
}

function boolAttribute(name, description) {
  return attribute(name, 'boolean', description, ['true', 'false']);
}

function colorAttribute(name, description) {
  return attribute(name, 'color', description);
}

function element(description, attributes = [], propertyNodes = []) {
  return { description, attributes, propertyNodes };
}

function brush(description, attributes = []) {
  return { description, attributes };
}

function getElementAttributes(name, customProperties = []) {
  const elementDefinition = elements[name];
  const names = [
    ...commonAttributes,
    ...(elementDefinition?.attributes ?? []),
    ...customProperties
  ];
  return unique(names).map(attributeName =>
    elementAttributeOverrides[name]?.[attributeName] ??
    definitions[attributeName] ??
    genericAttribute(attributeName));
}

function getBrushAttributes(name) {
  return (brushes[name]?.attributes ?? []).map(attributeName => brushAttributes[attributeName]);
}

function getCssProperties(elementName, customProperties = []) {
  const candidates = elementName && elements[elementName]
    ? getElementAttributes(elementName, customProperties)
    : unique([
      ...Object.keys(definitions),
      ...customProperties
    ]).map(name => definitions[name] ?? genericAttribute(name));

  const result = [];
  for (const definition of candidates) {
    if (structuralAttributes.has(definition.name)) continue;
    const cssName = xmlToCss[definition.name] ?? definition.name;
    if (result.some(item => item.name === cssName)) continue;
    result.push({ ...definition, name: cssName, xmlName: definition.name });
  }
  return result.sort((left, right) => left.name.localeCompare(right.name));
}

function getAttributeDefinition(elementName, attributeName, customProperties = []) {
  if (brushes[elementName]) {
    return getBrushAttributes(elementName).find(item => item.name === attributeName);
  }
  return getElementAttributes(elementName, customProperties).find(item => item.name === attributeName);
}

function getCssPropertyDefinition(propertyName, elementName, customProperties = []) {
  const normalized = propertyName.toLowerCase();
  const xmlName = cssAliases[normalized] ?? normalized;
  const candidates = getCssProperties(elementName, customProperties);
  return candidates.find(item => item.name === normalized || item.xmlName === xmlName);
}

function getPropertyNodes(elementName) {
  const nodes = ['Tooltip', ...(elements[elementName]?.propertyNodes ?? [])];
  return unique(nodes).map(propertyName => `${elementName}.${propertyName}`);
}

function getTagSuggestions(options = {}) {
  const customElements = options.customElements ?? [];
  const componentNames = options.componentNames ?? [];
  const parentName = options.parentName;
  if (!parentName) {
    return [
      tagItem('Ui', 'Dreambit layout document root.', '<Ui>\n\t$0\n</Ui>'),
      tagItem('UiComponent', 'Reusable Dreambit component document root.', '<UiComponent>\n\t$0\n</UiComponent>')
    ];
  }

  if (parentName === 'Ui.Components' || parentName === 'UiComponent.Components') {
    return [tagItem('Component', 'Declares a reusable component name and source.', '<Component name="$1" source="$2" />')];
  }

  if (parentName.endsWith('.Tooltip')) {
    return [tagItem('Tooltip', elements.Tooltip.description, '<Tooltip delay="${1:0.5}">\n\t$0\n</Tooltip>')];
  }

  if (parentName.includes('.') || brushes[parentName]) {
    return Object.entries(brushes).map(([name, definition]) => ({
      label: name,
      kind: 'brush',
      detail: 'Dreambit brush',
      documentation: definition.description,
      insertText: tagSnippet(name)
    }));
  }

  const nonContainers = new Set(['Text', 'Texture', 'Spacer']);
  const canContainElements = !nonContainers.has(parentName);
  const items = canContainElements ? [
    ...Object.entries(elements).map(([name, definition]) => ({
      label: name,
      kind: 'element',
      detail: `Dreambit ${name}`,
      documentation: definition.description,
      insertText: tagSnippet(name)
    })),
    ...customElements.map(name => ({
      label: name,
      kind: 'element',
      detail: 'Project-defined Dreambit element',
      documentation: 'Custom element configured in dreambitUi.customElements.',
      insertText: tagSnippet(name)
    })),
    ...componentNames.map(name => ({
      label: name,
      kind: 'component',
      detail: 'Component declared in UXML',
      documentation: 'Expands the matching <Component> declaration.',
      insertText: `<${name} $1/>`
    }))
  ] : [];

  if (canContainElements) {
    items.push(tagItem('Include', 'Includes a reusable UXML component by source path.', '<Include source="$1"$0 />'));
  }
  if (parentName === 'Ui') {
    items.unshift(tagItem('Ui.Components', 'Declares reusable component aliases for this layout.', '<Ui.Components>\n\t$0\n</Ui.Components>'));
  } else if (parentName === 'UiComponent') {
    items.unshift(tagItem('UiComponent.Components', 'Declares nested component aliases for this component.', '<UiComponent.Components>\n\t$0\n</UiComponent.Components>'));
  }
  if (elements[parentName]) {
    for (const propertyNode of getPropertyNodes(parentName)) {
      items.push({
        label: propertyNode,
        kind: 'property',
        detail: 'Dreambit property element',
        documentation: propertyNode.endsWith('.Tooltip')
          ? 'Assigns a Tooltip element to this element.'
          : 'Assigns a brush to this element property.',
        insertText: `<${propertyNode}>\n\t$0\n</${propertyNode}>`
      });
    }
  }

  return dedupeItems(items);
}

function getUxmlCompletions(text, offset, options = {}) {
  const context = analyzeUxml(text, offset);
  const customProperties = options.customProperties ?? [];
  const componentNames = unique([
    ...extractComponentNames(text),
    ...(options.componentNames ?? [])
  ]);

  if (context.kind === 'closing-tag') {
    return context.parentName
      ? [{ label: context.parentName, kind: 'element', detail: 'Close current element', insertText: `${context.parentName}>` }]
      : [];
  }
  if (context.kind === 'tag-name') {
    return getTagSuggestions({
      customElements: options.customElements,
      componentNames,
      parentName: context.parentName
    });
  }
  if (context.kind === 'attribute-value') {
    let definition = getAttributeDefinition(context.tagName, context.attributeName, customProperties);
    if ((context.tagName === 'Component' || context.tagName === 'Include') && context.attributeName === 'source') {
      definition = attribute('source', 'uxml-path', 'Workspace-relative path to a reusable .uxml file.');
    } else if ((context.tagName === 'Include' || componentNames.includes(context.tagName)) && context.attributeName === 'id-prefix') {
      definition = attribute('id-prefix', 'string', 'Prefix applied to IDs inside this component instance.');
    } else if (context.tagName === 'Component' && context.attributeName === 'name') {
      definition = attribute('name', 'identifier', 'Name used as the component element in this document.');
    }
    return valueSuggestions(definition, options);
  }
  if (context.kind === 'attribute-name') {
    if (context.tagName === 'Component') {
      return attributeItems([
        attribute('name', 'identifier', 'Name used as the component element in this document.'),
        attribute('source', 'uxml-path', 'Workspace-relative path to the reusable .uxml file.')
      ], context.existingAttributes);
    }
    if (context.tagName === 'Include') {
      return attributeItems([
        attribute('source', 'uxml-path', 'Workspace-relative path to the reusable .uxml file.'),
        attribute('id-prefix', 'string', 'Prefix applied to IDs inside this component instance.'),
        ...getElementAttributes('', customProperties)
      ], context.existingAttributes);
    }
    if (componentNames.includes(context.tagName)) {
      return attributeItems([
        attribute('id-prefix', 'string', 'Prefix applied to IDs inside this component instance.'),
        ...getElementAttributes('', customProperties)
      ], context.existingAttributes);
    }
    const definitionsForTag = brushes[context.tagName]
      ? getBrushAttributes(context.tagName)
      : getElementAttributes(context.tagName, customProperties);
    return attributeItems(definitionsForTag, context.existingAttributes);
  }
  return [];
}

function getUcssCompletions(text, offset, options = {}) {
  const context = analyzeUcss(text, offset);
  const customElements = options.customElements ?? [];
  const customProperties = options.customProperties ?? [];
  if (context.kind === 'selector') {
    const combinedMatch = context.prefix?.match(/^([A-Za-z_][\w-]*)\.([-_a-zA-Z0-9]*)$/);
    if (combinedMatch) {
      return unique(options.classes ?? []).map(name => ({
        label: `.${name}`,
        kind: 'class',
        detail: `Dreambit ${combinedMatch[1]}.class selector`,
        documentation: `Matches ${combinedMatch[1]} elements whose class list contains ${name}.`,
        insertText: `${name} {\n\t$0\n}`
      }));
    }
    const elementItems = [...Object.entries(elements), ...customElements.map(name => [name, { description: 'Project-defined Dreambit element.' }])]
      .map(([name, definition]) => ({
        label: name,
        kind: 'element',
        detail: 'Dreambit element selector',
        documentation: definition.description,
        insertText: `${name} {\n\t$0\n}`
      }));
    const classItems = unique(options.classes ?? []).map(name => ({
      label: `.${name}`,
      kind: 'class',
      detail: 'Dreambit class selector',
      documentation: `Matches elements whose class list contains ${name}.`,
      insertText: `.${name} {\n\t$0\n}`
    }));
    return [...elementItems, ...classItems];
  }
  if (context.kind === 'property-name') {
    return getCssProperties(context.elementName, customProperties).map(definition => ({
      label: definition.name,
      kind: 'property',
      detail: cssDetail(definition),
      documentation: definition.description,
      insertText: `${definition.name}: ${cssValueSnippet(definition)};`
    }));
  }
  if (context.kind === 'property-value') {
    const definition = getCssPropertyDefinition(context.propertyName, context.elementName, customProperties);
    return valueSuggestions(definition, options, true);
  }
  return [];
}

function analyzeUxml(text, offset) {
  const before = text.slice(0, offset);
  const parentName = findOpenParent(before);
  if (before.lastIndexOf('<!--') > before.lastIndexOf('-->') ||
      before.lastIndexOf('<![CDATA[') > before.lastIndexOf(']]>')) {
    return { kind: 'none', parentName };
  }
  const lastOpen = before.lastIndexOf('<');
  const lastClose = before.lastIndexOf('>');
  if (lastOpen <= lastClose) return { kind: 'content', parentName };

  const fragment = before.slice(lastOpen);
  if (/^<\s*!/.test(fragment) || /^<\s*\?/.test(fragment)) return { kind: 'none', parentName };
  if (/^<\s*\//.test(fragment)) return { kind: 'closing-tag', parentName };

  const tagMatch = fragment.match(/^<\s*([A-Za-z_][\w.-]*)?/);
  const tagName = tagMatch?.[1] ?? '';
  if (!tagName || !/\s/.test(fragment.slice(tagMatch[0].length))) {
    return { kind: 'tag-name', parentName };
  }

  const valueMatch = fragment.match(/([A-Za-z_][\w.-]*)\s*=\s*(["'])([^"']*)$/);
  if (valueMatch) {
    return {
      kind: 'attribute-value',
      parentName,
      tagName,
      attributeName: valueMatch[1],
      valuePrefix: valueMatch[3]
    };
  }

  return {
    kind: 'attribute-name',
    parentName,
    tagName,
    existingAttributes: extractAttributes(fragment)
  };
}

function analyzeUcss(text, offset) {
  const sanitized = sanitizeCss(text.slice(0, offset));
  let depth = 0;
  let lastOpen = -1;
  for (let index = 0; index < sanitized.length; index++) {
    if (sanitized[index] === '{') {
      depth++;
      if (depth === 1) lastOpen = index;
    } else if (sanitized[index] === '}') {
      depth = Math.max(0, depth - 1);
      if (depth === 0) lastOpen = -1;
    }
  }
  if (depth === 0 || lastOpen < 0) {
    const previousClose = sanitized.lastIndexOf('}');
    return {
      kind: 'selector',
      prefix: sanitized.slice(previousClose < 0 ? 0 : previousClose + 1).trim()
    };
  }

  const previousClose = sanitized.lastIndexOf('}', lastOpen - 1);
  const selectorStart = previousClose < 0 ? 0 : previousClose + 1;
  const selector = sanitized.slice(selectorStart, lastOpen).trim();
  const elementName = selector.match(/^([A-Za-z_][\w-]*)/)?.[1];
  const block = sanitized.slice(lastOpen + 1);
  const declarationStart = Math.max(block.lastIndexOf(';'), block.lastIndexOf('{')) + 1;
  const declaration = block.slice(declarationStart);
  const colon = declaration.indexOf(':');
  if (colon >= 0) {
    return {
      kind: 'property-value',
      elementName,
      propertyName: declaration.slice(0, colon).trim()
    };
  }
  return { kind: 'property-name', elementName };
}

function sanitizeCss(text) {
  let result = '';
  let state = 'normal';
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    const next = text[index + 1];
    if (state === 'comment') {
      if (character === '*' && next === '/') {
        result += '  ';
        index++;
        state = 'normal';
      } else {
        result += character === '\n' ? '\n' : ' ';
      }
      continue;
    }
    if (state === 'string') {
      if (character === '\\') {
        result += ' ';
        if (index + 1 < text.length) {
          result += ' ';
          index++;
        }
      } else if (character === '"') {
        result += ' ';
        state = 'normal';
      } else {
        result += character === '\n' ? '\n' : ' ';
      }
      continue;
    }
    if (state === 'single-string') {
      if (character === '\\') {
        result += ' ';
        if (index + 1 < text.length) {
          result += ' ';
          index++;
        }
      } else if (character === "'") {
        result += ' ';
        state = 'normal';
      } else {
        result += character === '\n' ? '\n' : ' ';
      }
      continue;
    }
    if (character === '/' && next === '*') {
      result += '  ';
      index++;
      state = 'comment';
    } else if (character === '"') {
      result += ' ';
      state = 'string';
    } else if (character === "'") {
      result += ' ';
      state = 'single-string';
    } else {
      result += character;
    }
  }
  return result;
}

function findOpenParent(text) {
  const stack = [];
  let cursor = 0;
  while (cursor < text.length) {
    const open = text.indexOf('<', cursor);
    if (open < 0) break;
    if (text.startsWith('<!--', open)) {
      const end = text.indexOf('-->', open + 4);
      cursor = end < 0 ? text.length : end + 3;
      continue;
    }
    if (text.startsWith('<![CDATA[', open)) {
      const end = text.indexOf(']]>', open + 9);
      cursor = end < 0 ? text.length : end + 3;
      continue;
    }
    const end = findXmlTagEnd(text, open + 1);
    if (end < 0) break;
    const body = text.slice(open + 1, end).trim();
    cursor = end + 1;
    if (!body || body.startsWith('!') || body.startsWith('?')) continue;
    const closing = body.startsWith('/');
    const name = body.match(/^\/?\s*([A-Za-z_][\w.-]*)/)?.[1];
    if (!name) continue;
    const selfClosing = body.endsWith('/');
    if (closing) {
      for (let index = stack.length - 1; index >= 0; index--) {
        if (stack[index] === name) {
          stack.length = index;
          break;
        }
      }
    } else if (!selfClosing) {
      stack.push(name);
    }
  }
  return stack.at(-1);
}

function findXmlTagEnd(text, start) {
  let quote;
  for (let index = start; index < text.length; index++) {
    const character = text[index];
    if (quote) {
      if (character === quote) quote = undefined;
    } else if (character === '"' || character === "'") {
      quote = character;
    } else if (character === '>') {
      return index;
    }
  }
  return -1;
}

function extractAttributes(fragment) {
  const names = [];
  const attributePattern = /\s([A-Za-z_][\w.-]*)\s*=/g;
  for (const match of fragment.matchAll(attributePattern)) names.push(match[1]);
  return names;
}

function extractComponentNames(text) {
  const names = [];
  const pattern = /<\s*Component\b[^>]*\bname\s*=\s*(["'])([^"']+)\1/gi;
  for (const match of stripXmlComments(text).matchAll(pattern)) names.push(match[2].trim());
  return unique(names.filter(Boolean));
}

function extractClassesFromUxml(text) {
  const classes = [];
  const pattern = /\bclass\s*=\s*(["'])([^"']*)\1/gi;
  for (const match of stripXmlComments(text).matchAll(pattern)) {
    classes.push(...match[2].split(/\s+/).filter(Boolean));
  }
  return unique(classes);
}

function stripXmlComments(text) {
  return text.replace(/<!--[\s\S]*?-->/g, match => match.replace(/[^\r\n]/g, ' '));
}

function extractClassesFromUcss(text) {
  const classes = [];
  const sanitized = sanitizeCss(text);
  const pattern = /(?:^|[}\s])(?:[A-Za-z_][\w-]*)?\.([-_a-zA-Z][-_a-zA-Z0-9]*)\s*\{/g;
  for (const match of sanitized.matchAll(pattern)) classes.push(match[1]);
  return unique(classes);
}

function attributeItems(items, existing = []) {
  const used = new Set(existing);
  return items.filter(Boolean).filter(item => !used.has(item.name)).map(definition => ({
    label: definition.name,
    kind: 'attribute',
    detail: `${definition.type} · Dreambit attribute`,
    documentation: definition.description,
    insertText: `${definition.name}="${xmlValueSnippet(definition)}"`
  }));
}

function valueSuggestions(definition, options = {}, css = false) {
  if (!definition) return [];
  if (definition.type === 'class-list') {
    return unique(options.classes ?? []).map(name => valueItem(name, 'Dreambit style class'));
  }
  if (definition.type === 'uxml-path') {
    return unique(options.uxmlPaths ?? []).map(path => valueItem(path, 'Dreambit UXML asset'));
  }
  if (definition.type === 'id-reference') {
    return unique(options.ids ?? []).map(id => valueItem(id, 'Element ID in this document'));
  }
  if (definition.values?.length) {
    return definition.values.map(value => valueItem(value, definition.description));
  }
  const samples = valueSamples(definition, css);
  return samples.map(value => valueItem(value, `${definition.type} value`));
}

function valueSamples(definition, css) {
  switch (definition.type) {
    case 'length': return css ? ['0', '8px', '50%', '-8px'] : ['0', '8', '50%', '-8'];
    case 'size': return css ? ['auto', '0', '48px', '100%'] : ['*', '0', '48', '100%'];
    case 'number': return css && definition.name === 'font-size' ? ['0', '16px', '24px', '32px'] : ['0', '1', '10', '0.5'];
    case 'integer': return ['0', '1', '2', '-1'];
    case 'color': return ['#FFFFFF', '#000000', '#FFFFFF80'];
    case 'thickness': return css ? ['0', '8px', '8px 16px', '4px 8px 12px 16px'] : ['0', '8', '8,16', '4,8,12,16'];
    case 'grid-definitions': return css
      ? ['"*"', '"*,*"', '"Auto,*"', '"80,2*"']
      : ['*', '*,*', 'Auto,*', '80,2*'];
    case 'asset': return definition.name === 'font' || definition.name === 'font-family' ? ['monogram'] : [];
    default: return [];
  }
}

function xmlValueSnippet(definition) {
  if (definition.values?.length) return `\${1|${definition.values.join(',')}|}`;
  switch (definition.type) {
    case 'boolean': return '${1|true,false|}';
    case 'length': return '${1:0}';
    case 'size': return '${1:100%}';
    case 'integer': return '${1:0}';
    case 'number': return '${1:0}';
    case 'color': return '${1:#FFFFFF}';
    case 'thickness': return '${1:0}';
    case 'asset': return definition.name === 'font' ? '${1:monogram}' : '$1';
    default: return '$1';
  }
}

function cssValueSnippet(definition) {
  if (definition.values?.length) return `\${1|${definition.values.join(',')}|}`;
  switch (definition.type) {
    case 'boolean': return '${1|true,false|}';
    case 'length': return '${1:0}';
    case 'size': return '${1:auto}';
    case 'integer': return '${1:0}';
    case 'number': return definition.name === 'font-size' ? '${1:16px}' : '${1:0}';
    case 'color': return '${1:#FFFFFF}';
    case 'thickness': return '${1:0}';
    case 'asset': return definition.name === 'font-family' ? '"${1:monogram}"' : '"$1"';
    case 'grid-definitions': return '"${1:*}"';
    case 'string': return '"$1"';
    case 'character': return '"$1"';
    case 'csv': return '"$1"';
    case 'id-reference': return '"$1"';
    default: return '$1';
  }
}

function tagSnippet(name) {
  const textLike = name === 'Text' ? ' text="$1"' : '';
  const requiredSprite = ['SpriteBrush', 'TiledSpriteBrush', 'NineSliceBrush', 'Texture'].includes(name)
    ? ' sprite="$1"'
    : textLike;
  return `<${name}${requiredSprite} $0/>`;
}

function tagItem(label, documentation, insertText) {
  return { label, kind: 'element', detail: 'Dreambit UXML', documentation, insertText };
}

function valueItem(label, documentation) {
  return { label, kind: 'value', detail: 'Dreambit value', documentation, insertText: label };
}

function cssDetail(definition) {
  const mapped = definition.xmlName && definition.xmlName !== definition.name
    ? ` → ${definition.xmlName}`
    : '';
  return `${definition.type} · Dreambit CSS${mapped}`;
}

function genericAttribute(name) {
  return attribute(name, 'value', 'Project-defined Dreambit property.');
}

function unique(items) {
  return [...new Set(items)];
}

function dedupeItems(items) {
  const seen = new Set();
  return items.filter(item => {
    if (seen.has(item.label)) return false;
    seen.add(item.label);
    return true;
  });
}

module.exports = {
  elements,
  brushes,
  definitions,
  analyzeUxml,
  analyzeUcss,
  extractClassesFromUxml,
  extractClassesFromUcss,
  extractComponentNames,
  findOpenParent,
  getAttributeDefinition,
  getCssPropertyDefinition,
  getCssProperties,
  getElementAttributes,
  getPropertyNodes,
  getTagSuggestions,
  getUxmlCompletions,
  getUcssCompletions,
  sanitizeCss
};
