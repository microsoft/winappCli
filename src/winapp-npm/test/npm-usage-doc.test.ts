// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as ts from 'typescript';

const NPM_ROOT = process.cwd();
const DOC_PATH = path.resolve(NPM_ROOT, '..', '..', 'docs', 'npm-usage.md');
const doc = fs.readFileSync(DOC_PATH, 'utf8');

test('npm usage is a maintained guide rather than a generated reference', () => {
  assert.ok(!doc.includes('AUTO-GENERATED'));
  assert.ok(!doc.includes('npm run generate-docs'));
  for (const contract of ['stdout', 'stderr', 'AbortError', 'workflowId', 'durationSec', 'non-interactive']) {
    assert.ok(doc.includes(contract), `guide must explain ${contract}`);
  }
});

test('npm usage relative links resolve to tracked documentation', () => {
  for (const match of doc.matchAll(/\]\(([^)]+)\)/g)) {
    const target = match[1].split('#')[0];
    if (!target || /^[a-z]+:/i.test(target)) continue;
    assert.ok(fs.existsSync(path.resolve(path.dirname(DOC_PATH), target)), `missing link: ${target}`);
  }
});

test('every TypeScript example in the npm guide type-checks against the public API', () => {
  const examples = [...doc.matchAll(/```typescript\r?\n([\s\S]*?)```/g)];
  assert.ok(examples.length > 0, 'guide must include runnable TypeScript examples');
  for (const [index, match] of examples.entries()) {
    const file = path.resolve(NPM_ROOT, `guide-example-${index}.mts`);
    const options: ts.CompilerOptions = {
      strict: true,
      noEmit: true,
      skipLibCheck: true,
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.ESNext,
      moduleResolution: ts.ModuleResolutionKind.Bundler,
      paths: { '@microsoft/winappcli': [path.resolve(NPM_ROOT, 'src', 'index.ts')] },
    };
    const host = ts.createCompilerHost(options);
    const fileExists = host.fileExists.bind(host);
    host.fileExists = (name) => path.resolve(name) === file || fileExists(name);
    const readSource = host.getSourceFile.bind(host);
    host.getSourceFile = (name, languageVersion, onError, shouldCreateNewSourceFile) =>
      path.resolve(name) === file
        ? ts.createSourceFile(name, match[1], languageVersion)
        : readSource(name, languageVersion, onError, shouldCreateNewSourceFile);
    const program = ts.createProgram([file], options, host);
    const diagnostics = ts.getPreEmitDiagnostics(program);
    assert.equal(
      diagnostics.length,
      0,
      `example ${index + 1}: ${ts.formatDiagnosticsWithColorAndContext(diagnostics, {
        getCanonicalFileName: (name) => name,
        getCurrentDirectory: () => NPM_ROOT,
        getNewLine: () => '\n',
      })}`
    );
  }
});
