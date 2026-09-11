'use strict';

const vscode = require('vscode');
const language = require('./src/dreambit-language');

const UXML_LANGUAGE = 'dreambit-uxml';
const UCSS_LANGUAGE = 'dreambit-ucss';

function activate(context) {
  const workspaceIndex = new DreambitWorkspaceIndex();
  context.subscriptions.push(workspaceIndex);

  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(
      { language: UXML_LANGUAGE },
      new UxmlCompletionProvider(workspaceIndex),
      '<', '/', ' ', '=', '"', "'"
    ),
    vscode.languages.registerCompletionItemProvider(
      { language: UCSS_LANGUAGE },
      new UcssCompletionProvider(workspaceIndex),
      '.', '{', ':', ' ', ';'
    ),
    vscode.languages.registerHoverProvider(
      { language: UXML_LANGUAGE },
      new UxmlHoverProvider()
    ),
    vscode.languages.registerHoverProvider(
      { language: UCSS_LANGUAGE },
      new UcssHoverProvider()
    ),
    vscode.languages.registerDocumentSymbolProvider(
      { language: UXML_LANGUAGE },
      new UxmlDocumentSymbolProvider()
    ),
    vscode.languages.registerDocumentSymbolProvider(
      { language: UCSS_LANGUAGE },
      new UcssDocumentSymbolProvider()
    ),
    vscode.languages.registerColorProvider(
      [{ language: UXML_LANGUAGE }, { language: UCSS_LANGUAGE }],
      new DreambitColorProvider()
    ),
    vscode.languages.registerDefinitionProvider(
      { language: UXML_LANGUAGE },
      new UxmlDefinitionProvider(workspaceIndex)
    )
  );
}

function deactivate() {}

class UxmlCompletionProvider {
  constructor(workspaceIndex) {
    this.workspaceIndex = workspaceIndex;
  }

  async provideCompletionItems(document, position, token) {
    const text = document.getText();
    const offset = document.offsetAt(position);
    const options = await buildOptions(document, this.workspaceIndex, token);
    const suggestions = language.getUxmlCompletions(text, offset, options);
    const context = language.analyzeUxml(text, offset);
    let replacementRange;
    if (context.kind === 'tag-name') {
      replacementRange = new vscode.Range(document.positionAt(text.lastIndexOf('<', offset - 1)), position);
    } else if (context.kind === 'attribute-value' && context.attributeName === 'source') {
      replacementRange = new vscode.Range(document.positionAt(offset - context.valuePrefix.length), position);
    }
    return suggestions.map(suggestion => toCompletionItem(suggestion, replacementRange));
  }
}

class UcssCompletionProvider {
  constructor(workspaceIndex) {
    this.workspaceIndex = workspaceIndex;
  }

  async provideCompletionItems(document, position, token) {
    const options = await buildOptions(document, this.workspaceIndex, token);
    const text = document.getText();
    const offset = document.offsetAt(position);
    const context = language.analyzeUcss(text, offset);
    const suggestions = language.getUcssCompletions(text, offset, options);
    let replacementRange;
    if (context.kind === 'selector') {
      const combinedMatch = context.prefix?.match(/^([A-Za-z_][\w-]*)\.([-_a-zA-Z0-9]*)$/);
      const prefixLength = combinedMatch
        ? combinedMatch[2].length
        : (text.slice(0, offset).match(/[._A-Za-z][\w.-]*$/)?.[0].length ?? 0);
      replacementRange = new vscode.Range(document.positionAt(offset - prefixLength), position);
    }
    return suggestions.map(suggestion => toCompletionItem(suggestion, replacementRange));
  }
}

class UxmlHoverProvider {
  provideHover(document, position) {
    const range = document.getWordRangeAtPosition(position, /[A-Za-z_][\w.-]*/);
    if (!range) return undefined;
    const word = document.getText(range);
    const text = document.getText();
    const offset = document.offsetAt(position);
    const open = text.lastIndexOf('<', offset);
    const close = text.indexOf('>', offset);
    if (open < 0 || close < 0) return undefined;
    const fragment = text.slice(open, close + 1);
    const tagName = fragment.match(/^<\s*\/?\s*([A-Za-z_][\w.-]*)/)?.[1];
    if (!tagName) return undefined;

    const tagNameOffset = open + fragment.indexOf(tagName);
    if (range.start.isEqual(document.positionAt(tagNameOffset))) {
      return tagHover(word, range);
    }

    const config = readConfiguration(document.uri);
    let definition = language.getAttributeDefinition(tagName, word, config.customProperties);
    if (tagName === 'Component' && word === 'name') {
      definition = { type: 'identifier', description: 'Name used as the component element in this document.' };
    } else if ((tagName === 'Component' || tagName === 'Include') && word === 'source') {
      definition = { type: 'uxml-path', description: 'Path to a reusable Dreambit .uxml component.' };
    } else if (tagName === 'Include' && word === 'id-prefix') {
      definition = { type: 'string', description: 'Prefix applied to IDs inside this component instance.' };
    }
    if (!definition) return undefined;
    return new vscode.Hover(markdownForDefinition(word, definition, 'UXML attribute'), range);
  }
}

class UcssHoverProvider {
  provideHover(document, position) {
    const range = document.getWordRangeAtPosition(position, /[-_a-zA-Z][-_a-zA-Z0-9]*/);
    if (!range) return undefined;
    const word = document.getText(range);
    const text = document.getText();
    const offset = document.offsetAt(position);
    const context = language.analyzeUcss(text, offset);
    const wordStart = document.offsetAt(range.start);
    if (text[wordStart - 1] === '.') {
      return new vscode.Hover(markdown(`.${word}`, 'Dreambit class selector', `Matches elements whose class list contains \`${word}\`.`), range);
    }

    const after = text.slice(document.offsetAt(range.end));
    if (after.match(/^\s*:/)) {
      const config = readConfiguration(document.uri);
      const definition = language.getCssPropertyDefinition(word, context.elementName, config.customProperties);
      if (definition) return new vscode.Hover(markdownForDefinition(word, definition, 'UCSS property'), range);
    }

    const definition = language.elements[word];
    if (definition) {
      return new vscode.Hover(markdown(word, 'Dreambit element selector', definition.description), range);
    }
    return undefined;
  }
}

class UxmlDocumentSymbolProvider {
  provideDocumentSymbols(document) {
    const originalText = document.getText();
    const text = stripXmlComments(originalText);
    const symbols = [];
    const pattern = /<\s*([A-Za-z_][\w.-]*)([^<>]*?)(\/?)>/g;
    for (const match of text.matchAll(pattern)) {
      if (match[0].startsWith('</') || match[0].startsWith('<!--')) continue;
      const name = match[1];
      const id = match[2].match(/\bid\s*=\s*(["'])([^"']+)\1/)?.[2];
      const start = document.positionAt(match.index);
      const end = document.positionAt(match.index + match[0].length);
      symbols.push(new vscode.SymbolInformation(
        id ? `${name} #${id}` : name,
        symbolKindForTag(name),
        '',
        new vscode.Location(document.uri, new vscode.Range(start, end))
      ));
    }
    return symbols;
  }
}

class UcssDocumentSymbolProvider {
  provideDocumentSymbols(document) {
    const text = language.sanitizeCss(document.getText());
    const symbols = [];
    const pattern = /(^|})\s*([A-Za-z_][\w-]*(?:\.[-_a-zA-Z][-_a-zA-Z0-9]*)?|\.[-_a-zA-Z][-_a-zA-Z0-9]*)\s*\{/gm;
    for (const match of text.matchAll(pattern)) {
      const selector = match[2];
      const selectorOffset = match.index + match[0].indexOf(selector);
      const range = new vscode.Range(
        document.positionAt(selectorOffset),
        document.positionAt(selectorOffset + selector.length)
      );
      symbols.push(new vscode.SymbolInformation(
        selector,
        vscode.SymbolKind.Class,
        '',
        new vscode.Location(document.uri, range)
      ));
    }
    return symbols;
  }
}

class DreambitColorProvider {
  provideDocumentColors(document) {
    const colors = [];
    const pattern = /#([0-9a-fA-F]{6})([0-9a-fA-F]{2})?\b/g;
    const documentText = document.getText();
    const searchableText = document.languageId === UCSS_LANGUAGE
      ? language.sanitizeCss(documentText)
      : stripXmlComments(documentText);
    for (const match of searchableText.matchAll(pattern)) {
      const red = Number.parseInt(match[1].slice(0, 2), 16) / 255;
      const green = Number.parseInt(match[1].slice(2, 4), 16) / 255;
      const blue = Number.parseInt(match[1].slice(4, 6), 16) / 255;
      const alpha = match[2] ? Number.parseInt(match[2], 16) / 255 : 1;
      colors.push(new vscode.ColorInformation(
        new vscode.Range(
          document.positionAt(match.index),
          document.positionAt(match.index + match[0].length)
        ),
        new vscode.Color(red, green, blue, alpha)
      ));
    }
    return colors;
  }

  provideColorPresentations(color, context) {
    const red = toHex(color.red);
    const green = toHex(color.green);
    const blue = toHex(color.blue);
    const alpha = toHex(color.alpha);
    const value = color.alpha >= 0.999
      ? `#${red}${green}${blue}`
      : `#${red}${green}${blue}${alpha}`;
    const presentation = new vscode.ColorPresentation(value);
    presentation.textEdit = new vscode.TextEdit(context.range, value);
    return [presentation];
  }
}

class UxmlDefinitionProvider {
  constructor(workspaceIndex) {
    this.workspaceIndex = workspaceIndex;
  }

  async provideDefinition(document, position, token) {
    const source = sourceValueAtPosition(document, position);
    if (!source) return undefined;
    const metadata = await this.workspaceIndex.get(token);
    const resolvedPath = resolveReferencePath(logicalUxmlPath(document.uri), source);
    const uri = metadata.pathUris.get(resolvedPath);
    return uri ? new vscode.Location(uri, new vscode.Position(0, 0)) : undefined;
  }
}

class DreambitWorkspaceIndex {
  constructor() {
    this.cached = undefined;
    this.pending = undefined;
    this.disposables = [];
    if (vscode.workspace.workspaceFolders) {
      const watcher = vscode.workspace.createFileSystemWatcher('**/*.{uxml,ucss}');
      watcher.onDidCreate(() => this.invalidate(), this, this.disposables);
      watcher.onDidChange(() => this.invalidate(), this, this.disposables);
      watcher.onDidDelete(() => this.invalidate(), this, this.disposables);
      this.disposables.push(watcher);
    }
    this.disposables.push(vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('dreambitUi')) this.invalidate();
    }));
  }

  invalidate() {
    this.cached = undefined;
    this.pending = undefined;
  }

  async get(token) {
    if (this.cached) return this.cached;
    if (!this.pending) this.pending = this.scan(token);
    try {
      this.cached = await this.pending;
      return this.cached;
    } finally {
      this.pending = undefined;
    }
  }

  async scan(token) {
    const empty = { classes: [], uxmlPaths: [], pathUris: new Map() };
    if (!vscode.workspace.workspaceFolders) return empty;
    const config = vscode.workspace.getConfiguration('dreambitUi');
    if (!config.get('scanWorkspace', true)) return empty;

    const uris = await vscode.workspace.findFiles(
      '**/*.{uxml,ucss}',
      '**/{bin,obj,build,artifacts,publish,node_modules}/**',
      1000,
      token
    );
    const classes = [];
    const uxmlPaths = [];
    const pathUris = new Map();
    await Promise.all(uris.map(async uri => {
      if (token?.isCancellationRequested) return;
      try {
        let text;
        const openDocument = vscode.workspace.textDocuments.find(document => document.uri.toString() === uri.toString());
        if (openDocument) {
          text = openDocument.getText();
        } else {
          const bytes = await vscode.workspace.fs.readFile(uri);
          text = new TextDecoder('utf-8').decode(bytes);
        }
        if (uri.path.toLowerCase().endsWith('.uxml')) {
          classes.push(...language.extractClassesFromUxml(text));
          const logicalPath = logicalUxmlPath(uri);
          uxmlPaths.push(logicalPath);
          pathUris.set(normalizeAssetPath(logicalPath), uri);
        } else {
          classes.push(...language.extractClassesFromUcss(text));
        }
      } catch {
        // A file can disappear between discovery and reading. The next watcher
        // invalidation will rebuild the index without interrupting completion.
      }
    }));
    return {
      classes: unique(classes).sort(),
      uxmlPaths: unique(uxmlPaths).sort(),
      pathUris
    };
  }

  dispose() {
    for (const disposable of this.disposables) disposable.dispose();
  }
}

async function buildOptions(document, workspaceIndex, token) {
  const config = readConfiguration(document.uri);
  const metadata = config.scanWorkspace
    ? await workspaceIndex.get(token)
    : { classes: [], uxmlPaths: [] };
  const text = document.getText();
  const documentPath = logicalUxmlPath(document.uri);
  const openDocumentClasses = vscode.workspace.textDocuments.flatMap(openDocument =>
    openDocument.languageId === UXML_LANGUAGE
      ? language.extractClassesFromUxml(openDocument.getText())
      : openDocument.languageId === UCSS_LANGUAGE
        ? language.extractClassesFromUcss(openDocument.getText())
        : []);
  const sourcePaths = unique(metadata.uxmlPaths
    .filter(path => normalizeAssetPath(path) !== normalizeAssetPath(documentPath))
    .flatMap(path => [relativeAssetPath(documentPath, path), `~/${normalizeDisplayPath(path)}`]));
  return {
    customElements: config.customElements,
    customProperties: config.customProperties,
    classes: unique([
      ...metadata.classes,
      ...openDocumentClasses,
      ...language.extractClassesFromUxml(text),
      ...language.extractClassesFromUcss(text)
    ]),
    componentNames: language.extractComponentNames(text),
    uxmlPaths: sourcePaths,
    ids: extractIds(text)
  };
}

function readConfiguration(uri) {
  const config = vscode.workspace.getConfiguration('dreambitUi', uri);
  return {
    customElements: cleanNames(config.get('customElements', [])),
    customProperties: cleanNames(config.get('customProperties', [])),
    scanWorkspace: config.get('scanWorkspace', true)
  };
}

function toCompletionItem(suggestion, range) {
  const item = new vscode.CompletionItem(
    suggestion.label,
    completionKind(suggestion.kind)
  );
  item.detail = suggestion.detail;
  item.documentation = new vscode.MarkdownString(suggestion.documentation ?? '');
  item.insertText = suggestion.insertText.includes('$')
    ? new vscode.SnippetString(suggestion.insertText)
    : suggestion.insertText;
  if (range) item.range = range;
  if (suggestion.kind === 'attribute' || suggestion.kind === 'property') {
    item.command = { command: 'editor.action.triggerSuggest', title: 'Suggest Dreambit value' };
  }
  return item;
}

function completionKind(kind) {
  switch (kind) {
    case 'element': return vscode.CompletionItemKind.Class;
    case 'component': return vscode.CompletionItemKind.Module;
    case 'brush': return vscode.CompletionItemKind.Constructor;
    case 'property': return vscode.CompletionItemKind.Property;
    case 'attribute': return vscode.CompletionItemKind.Field;
    case 'class': return vscode.CompletionItemKind.Class;
    case 'value': return vscode.CompletionItemKind.Value;
    default: return vscode.CompletionItemKind.Text;
  }
}

function tagHover(word, range) {
  if (language.elements[word]) {
    return new vscode.Hover(markdown(word, 'Dreambit UI element', language.elements[word].description), range);
  }
  if (language.brushes[word]) {
    return new vscode.Hover(markdown(word, 'Dreambit UI brush', language.brushes[word].description), range);
  }
  const structural = {
    Ui: ['Dreambit layout root', 'Root element for a complete retained UI layout.'],
    UiComponent: ['Dreambit component root', 'Root element for a reusable Dreambit UI component.'],
    Include: ['Dreambit component include', 'Expands the component referenced by its source attribute.'],
    Component: ['Dreambit component declaration', 'Associates a local element name with a reusable component source.']
  }[word];
  if (structural) return new vscode.Hover(markdown(word, structural[0], structural[1]), range);
  if (word.includes('.')) {
    return new vscode.Hover(markdown(word, 'Dreambit property element', 'Assigns a brush or nested UI element to a writable Dreambit property.'), range);
  }
  return undefined;
}

function markdownForDefinition(name, definition, category) {
  const values = definition.values?.length
    ? `\n\nValues: ${definition.values.map(value => `\`${value}\``).join(', ')}`
    : '';
  return markdown(name, `${category} · ${definition.type}`, `${definition.description}${values}`);
}

function markdown(title, detail, description) {
  const content = new vscode.MarkdownString();
  content.appendCodeblock(title, 'text');
  content.appendMarkdown(`**${detail}**\n\n${description}`);
  return content;
}

function symbolKindForTag(name) {
  if (language.brushes[name]) return vscode.SymbolKind.Object;
  if (name === 'Component' || name === 'Include') return vscode.SymbolKind.Module;
  if (name.includes('.')) return vscode.SymbolKind.Property;
  return vscode.SymbolKind.Class;
}

function sourceValueAtPosition(document, position) {
  const line = document.lineAt(position.line).text;
  const pattern = /\bsource\s*=\s*(["'])([^"']+)\1/g;
  for (const match of line.matchAll(pattern)) {
    const start = match.index + match[0].indexOf(match[2]);
    const end = start + match[2].length;
    if (position.character >= start && position.character <= end) return match[2];
  }
  return undefined;
}

function logicalUxmlPath(uri) {
  const normalized = uri.path.replace(/\\/g, '/');
  const assetsMarker = '/Assets/';
  const assetsIndex = normalized.toLowerCase().lastIndexOf(assetsMarker.toLowerCase());
  if (assetsIndex >= 0) return normalized.slice(assetsIndex + assetsMarker.length);
  const folder = vscode.workspace.getWorkspaceFolder(uri);
  if (!folder) return normalized.split('/').at(-1);
  const relative = normalized.slice(folder.uri.path.replace(/\\/g, '/').length).replace(/^\//, '');
  return relative;
}

function normalizeAssetPath(value) {
  return normalizeDisplayPath(value).toLowerCase();
}

function normalizeDisplayPath(value) {
  const segments = [];
  const normalized = value.replace(/\\/g, '/').replace(/^~\//, '');
  for (const segment of normalized.split('/')) {
    if (!segment || segment === '.') continue;
    if (segment === '..') {
      if (segments.length) segments.pop();
      continue;
    }
    segments.push(segment);
  }
  return segments.join('/');
}

function resolveReferencePath(documentPath, source) {
  if (source.replace(/\\/g, '/').startsWith('~/')) return normalizeAssetPath(source);
  const normalizedDocument = normalizeDisplayPath(documentPath);
  const separator = normalizedDocument.lastIndexOf('/');
  const directory = separator < 0 ? '' : normalizedDocument.slice(0, separator);
  return normalizeAssetPath(directory ? `${directory}/${source}` : source);
}

function relativeAssetPath(documentPath, targetPath) {
  const from = normalizeDisplayPath(documentPath).split('/');
  from.pop();
  const target = normalizeDisplayPath(targetPath).split('/');
  let shared = 0;
  while (shared < from.length && shared < target.length &&
         from[shared].toLowerCase() === target[shared].toLowerCase()) {
    shared++;
  }
  const result = [
    ...Array(from.length - shared).fill('..'),
    ...target.slice(shared)
  ];
  return result.join('/') || target.at(-1);
}

function extractIds(text) {
  const result = [];
  const pattern = /\bid\s*=\s*(["'])([^"']+)\1/gi;
  for (const match of text.matchAll(pattern)) result.push(match[2]);
  return unique(result);
}

function stripXmlComments(text) {
  return text.replace(/<!--[\s\S]*?-->/g, match => match.replace(/[^\r\n]/g, ' '));
}

function cleanNames(values) {
  return unique(values.filter(value => typeof value === 'string').map(value => value.trim()).filter(Boolean));
}

function unique(values) {
  return [...new Set(values)];
}

function toHex(value) {
  return Math.round(Math.min(1, Math.max(0, value)) * 255)
    .toString(16)
    .padStart(2, '0')
    .toUpperCase();
}

module.exports = { activate, deactivate };
