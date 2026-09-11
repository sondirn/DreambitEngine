'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const language = require('../src/dreambit-language');

function labels(items) {
  return items.map(item => item.label);
}

test('catalog contains every built-in concrete Dreambit UI element', () => {
  const expected = [
    'Border', 'Button', 'Canvas', 'CheckBox', 'ComboBox', 'Container',
    'ContentControl', 'Control', 'Grid', 'HorizontalStackPanel', 'ItemsControl',
    'ListBox', 'Overlay', 'Panel', 'Popup', 'ProgressBar', 'RadioButton',
    'ScrollBar', 'Slider', 'Spacer', 'StackPanel', 'Text', 'TextBox',
    'Texture', 'ToggleButton', 'Tooltip', 'UniformGrid',
    'VerticalStackPanel', 'Viewbox', 'WrapPanel'
  ];
  assert.deepEqual(Object.keys(language.elements).sort(), expected.sort());
});

test('catalog contains every built-in Dreambit brush', () => {
  assert.deepEqual(Object.keys(language.brushes).sort(), [
    'LayeredBrush', 'NineSliceBrush', 'OutlineBrush', 'SolidColorBrush',
    'SpriteBrush', 'TiledSpriteBrush'
  ]);
});

test('UXML suggests document roots at the document level', () => {
  const items = language.getUxmlCompletions('<', 1);
  assert.ok(labels(items).includes('Ui'));
  assert.ok(labels(items).includes('UiComponent'));
  assert.ok(!labels(items).includes('Text'));
});

test('UXML suggests built-in elements and the component section under a layout', () => {
  const text = '<Ui><';
  const items = language.getUxmlCompletions(text, text.length);
  assert.ok(labels(items).includes('Text'));
  assert.ok(labels(items).includes('Button'));
  assert.ok(labels(items).includes('Ui.Components'));
});

test('UXML suggests inherited and element-specific attributes', () => {
  const text = '<Ui>\n  <Text ';
  const items = language.getUxmlCompletions(text, text.length);
  assert.ok(labels(items).includes('width'));
  assert.ok(labels(items).includes('class'));
  assert.ok(labels(items).includes('font'));
  assert.ok(labels(items).includes('horizontal-alignment'));
  assert.ok(!labels(items).includes('track-tint'));
});

test('UXML suppresses attributes already present on an element', () => {
  const text = '<Button width="180" class="primary" ';
  const items = language.getUxmlCompletions(text, text.length);
  assert.ok(!labels(items).includes('width'));
  assert.ok(!labels(items).includes('class'));
  assert.ok(labels(items).includes('height'));
});

test('Grid rows and columns use grid-definition values while UniformGrid uses counts', () => {
  assert.equal(language.getAttributeDefinition('Grid', 'rows').type, 'grid-definitions');
  assert.equal(language.getAttributeDefinition('Grid', 'columns').type, 'grid-definitions');
  assert.equal(language.getAttributeDefinition('UniformGrid', 'rows').type, 'integer');
  assert.equal(language.getAttributeDefinition('UniformGrid', 'columns').type, 'integer');
});

test('UXML suggests enum and boolean values', () => {
  let text = '<Button anchor="';
  let items = language.getUxmlCompletions(text, text.length);
  assert.ok(labels(items).includes('Center'));
  assert.ok(labels(items).includes('BottomRight'));

  text = '<Button is-enabled="';
  items = language.getUxmlCompletions(text, text.length);
  assert.deepEqual(labels(items), ['true', 'false']);
});

test('UXML suggests multiple workspace classes', () => {
  const text = '<Text class="';
  const items = language.getUxmlCompletions(text, text.length, {
    classes: ['h1', 'centered', 'highlighted']
  });
  assert.deepEqual(labels(items), ['h1', 'centered', 'highlighted']);
});

test('UXML suggests IDs for popup placement targets', () => {
  const text = '<Popup placement-target="';
  const items = language.getUxmlCompletions(text, text.length, {
    ids: ['inventory-button', 'menu-root']
  });
  assert.deepEqual(labels(items), ['inventory-button', 'menu-root']);
});

test('UXML suggests UXML source paths for declarations and includes', () => {
  for (const prefix of ['<Component source="', '<Include source="']) {
    const items = language.getUxmlCompletions(prefix, prefix.length, {
      uxmlPaths: ['Ui/Components/menu-button.uxml']
    });
    assert.ok(labels(items).includes('Ui/Components/menu-button.uxml'));
  }
});

test('UXML discovers and suggests declared component names', () => {
  const text = [
    '<Ui>',
    '  <Ui.Components>',
    '    <Component name="PrimaryButton" source="button.uxml" />',
    '  </Ui.Components>',
    '  <'
  ].join('\n');
  const items = language.getUxmlCompletions(text, text.length);
  const component = items.find(item => item.label === 'PrimaryButton');
  assert.equal(component.kind, 'component');
});

test('component invocations and Includes suggest instance override attributes', () => {
  let text = [
    '<Ui>',
    '  <Ui.Components>',
    '    <Component name="PrimaryButton" source="button.uxml" />',
    '  </Ui.Components>',
    '  <PrimaryButton '
  ].join('\n');
  let itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.ok(itemLabels.includes('id-prefix'));
  assert.ok(itemLabels.includes('width'));
  assert.ok(itemLabels.includes('class'));

  text = '<Ui><Include source="button.uxml" ';
  itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.ok(itemLabels.includes('id-prefix'));
  assert.ok(itemLabels.includes('width'));
});

test('UXML suggests brush and tooltip property elements for the parent', () => {
  const text = '<Ui><Button><';
  const itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.ok(itemLabels.includes('Button.Background'));
  assert.ok(itemLabels.includes('Button.Tooltip'));
});

test('UXML suggests specialized brush property elements', () => {
  const text = '<Ui><Slider><';
  const itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.ok(itemLabels.includes('Slider.TrackBrush'));
  assert.ok(itemLabels.includes('Slider.FillBrush'));
  assert.ok(itemLabels.includes('Slider.ThumbBrush'));
});

test('UXML narrows property-element children to their supported value kind', () => {
  let text = '<Ui><Button><Button.Background><';
  let itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.ok(itemLabels.includes('SolidColorBrush'));
  assert.ok(itemLabels.includes('NineSliceBrush'));
  assert.ok(!itemLabels.includes('Text'));

  text = '<Ui><Text><Text.Tooltip><';
  itemLabels = labels(language.getUxmlCompletions(text, text.length));
  assert.deepEqual(itemLabels, ['Tooltip']);
});

test('UXML suggests the currently open closing tag', () => {
  const text = '<Ui><VerticalStackPanel></';
  const items = language.getUxmlCompletions(text, text.length);
  assert.deepEqual(labels(items), ['VerticalStackPanel']);
});

test('UXML structure analysis ignores tags and classes inside comments', () => {
  const text = '<Ui><!-- <Panel class="ignored"> --><';
  assert.equal(language.findOpenParent(text), 'Ui');
  assert.equal(language.analyzeUxml(text, text.length).parentName, 'Ui');
  assert.deepEqual(language.extractClassesFromUxml(text), []);
});

test('custom elements and properties augment UXML suggestions', () => {
  let text = '<Ui><';
  let items = language.getUxmlCompletions(text, text.length, {
    customElements: ['InventorySlot']
  });
  assert.ok(labels(items).includes('InventorySlot'));

  text = '<InventorySlot ';
  items = language.getUxmlCompletions(text, text.length, {
    customProperties: ['rarity']
  });
  assert.ok(labels(items).includes('rarity'));
});

test('UCSS suggests supported element and discovered class selectors', () => {
  const items = language.getUcssCompletions('', 0, { classes: ['h1', 'primary'] });
  assert.ok(labels(items).includes('Text'));
  assert.ok(labels(items).includes('Button'));
  assert.ok(labels(items).includes('.h1'));
  assert.ok(labels(items).includes('.primary'));
});

test('UCSS completes a class after an element selector', () => {
  const text = 'Text.';
  const items = language.getUcssCompletions(text, text.length, { classes: ['h1'] });
  assert.deepEqual(labels(items), ['.h1']);
  assert.equal(items[0].insertText, 'h1 {\n\t$0\n}');
});

test('UCSS narrows properties to the selector element', () => {
  const text = 'Text.h1 {\n  ';
  const items = language.getUcssCompletions(text, text.length);
  assert.ok(labels(items).includes('font-family'));
  assert.ok(labels(items).includes('color'));
  assert.ok(labels(items).includes('width'));
  assert.ok(!labels(items).includes('track-tint'));
});

test('UCSS offers Dreambit aliases and excludes structural properties', () => {
  const text = '.primary {\n  ';
  const itemLabels = labels(language.getUcssCompletions(text, text.length));
  assert.ok(itemLabels.includes('font-family'));
  assert.ok(itemLabels.includes('color'));
  assert.ok(itemLabels.includes('z-index'));
  assert.ok(!itemLabels.includes('font'));
  assert.ok(!itemLabels.includes('text-color'));
  assert.ok(!itemLabels.includes('z'));
  assert.ok(!itemLabels.includes('id'));
  assert.ok(!itemLabels.includes('class'));
});

test('UCSS suggests valid values for typed Dreambit properties', () => {
  let text = 'Button { width: ';
  let items = language.getUcssCompletions(text, text.length);
  assert.deepEqual(labels(items), ['auto', '0', '48px', '100%']);

  text = 'Button { is-enabled: ';
  items = language.getUcssCompletions(text, text.length);
  assert.deepEqual(labels(items), ['true', 'false']);

  text = 'Text { color: ';
  items = language.getUcssCompletions(text, text.length);
  assert.ok(labels(items).includes('#FFFFFF80'));

  text = 'Grid { rows: ';
  items = language.getUcssCompletions(text, text.length);
  assert.ok(labels(items).includes('"*,*"'));
});

test('UCSS recognizes a selector after an earlier rule', () => {
  const text = 'Text { color: #FFFFFF; }\nButton.primary {\n  ';
  const context = language.analyzeUcss(text, text.length);
  assert.equal(context.kind, 'property-name');
  assert.equal(context.elementName, 'Button');
  const itemLabels = labels(language.getUcssCompletions(text, text.length));
  assert.ok(itemLabels.includes('background-color'));
  assert.ok(itemLabels.includes('hover-tint'));
});

test('UCSS comment and quoted brace content do not corrupt context', () => {
  const text = [
    '/* ignored { color: fake; } */',
    'Text {',
    '  font-family: "brace } font";',
    '  '
  ].join('\n');
  const context = language.analyzeUcss(text, text.length);
  assert.equal(context.kind, 'property-name');
  assert.equal(context.elementName, 'Text');
});

test('class extraction supports multiple UXML classes and UCSS selectors', () => {
  assert.deepEqual(
    language.extractClassesFromUxml('<Text class="h1 centered highlighted" />'),
    ['h1', 'centered', 'highlighted']
  );
  assert.deepEqual(
    language.extractClassesFromUcss('.h1 {}\nText.centered {}\n/* .ignored {} */'),
    ['h1', 'centered']
  );
});

test('property lookup maps UCSS aliases back to UXML parsing names', () => {
  assert.equal(language.getCssPropertyDefinition('font-family', 'Text').xmlName, 'font');
  assert.equal(language.getCssPropertyDefinition('color', 'Text').xmlName, 'text-color');
  assert.equal(language.getCssPropertyDefinition('z-index', 'Text').xmlName, 'z');
});
