'use strict';

const assert = require('node:assert/strict');
const Module = require('node:module');
const test = require('node:test');

test('extension loads and registers all Dreambit language providers', () => {
  const registrations = [];
  const disposable = { dispose() {} };
  const vscode = {
    languages: {
      registerCompletionItemProvider(selector) {
        registrations.push(['completion', selector]);
        return disposable;
      },
      registerHoverProvider(selector) {
        registrations.push(['hover', selector]);
        return disposable;
      },
      registerDocumentSymbolProvider(selector) {
        registrations.push(['symbols', selector]);
        return disposable;
      },
      registerColorProvider(selector) {
        registrations.push(['colors', selector]);
        return disposable;
      },
      registerDefinitionProvider(selector) {
        registrations.push(['definition', selector]);
        return disposable;
      }
    },
    workspace: {
      workspaceFolders: undefined,
      onDidChangeConfiguration() {
        return disposable;
      }
    }
  };

  const originalLoad = Module._load;
  Module._load = function patchedLoad(request, parent, isMain) {
    if (request === 'vscode') return vscode;
    return originalLoad.call(this, request, parent, isMain);
  };
  try {
    const extension = require('../extension');
    const context = { subscriptions: [] };
    extension.activate(context);
    assert.equal(registrations.filter(item => item[0] === 'completion').length, 2);
    assert.equal(registrations.filter(item => item[0] === 'hover').length, 2);
    assert.equal(registrations.filter(item => item[0] === 'symbols').length, 2);
    assert.equal(registrations.filter(item => item[0] === 'colors').length, 1);
    assert.equal(registrations.filter(item => item[0] === 'definition').length, 1);
    assert.equal(context.subscriptions.length, 9);
  } finally {
    Module._load = originalLoad;
    delete require.cache[require.resolve('../extension')];
  }
});
