// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import {
  shouldSkipBindingsAfterInit,
  makeIndentedLog,
  printInitWrapperOnlyHelp,
  handleGenerateBindings,
  formatJsBindingsImportsHints,
  planJsBindingsImportsHints,
} from '../src/jsbindings/cli-hooks';

// Shorthand: build a default `false` set, override with `overrides`.
const make = (overrides: Partial<Parameters<typeof shouldSkipBindingsAfterInit>[0]> = {}) => ({
  explicitWorkspace: false,
  useDefaults: false,
  packageJsonExistedBeforeInit: false,
  packageJsonExistsNow: false,
  ...overrides,
});

test('shouldSkipBindingsAfterInit trusts cwd when user passes an explicit workspace', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ explicitWorkspace: true })), false);
  assert.equal(
    shouldSkipBindingsAfterInit(make({ explicitWorkspace: true, packageJsonExistedBeforeInit: false })),
    false
  );
});

test('shouldSkipBindingsAfterInit trusts cwd when package.json already existed before init', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ packageJsonExistedBeforeInit: true })), false);
});

test('shouldSkipBindingsAfterInit trusts cwd in non-interactive flow if a package.json exists now', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ useDefaults: true, packageJsonExistsNow: true })), false);
});

test('shouldSkipBindingsAfterInit skips when there is no signal that cwd is the workspace', () => {
  assert.equal(shouldSkipBindingsAfterInit(make()), true);
});

test('shouldSkipBindingsAfterInit still skips --use-defaults with no package.json anywhere', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ useDefaults: true, packageJsonExistsNow: false })), true);
});

test('makeIndentedLog prefixes single-line messages with the given indent', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('hello');
    log('world');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['  hello', '  world']);
});

test('makeIndentedLog prefixes every non-empty line in multi-line messages', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('first\nsecond\n\nfourth');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['  first\n  second\n\n  fourth']);
});

test('makeIndentedLog passes empty messages through untouched', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['']);
});

test('printInitWrapperOnlyHelp documents --add-js-bindings', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    printInitWrapperOnlyHelp();
  } finally {
    console.log = original;
  }
  const joined = captured.join('\n');
  assert.match(joined, /--add-js-bindings/);
  assert.match(joined, /JS bindings/);
  assert.equal(captured[1], 'npm wrapper', 'the wrapper option gets its own group heading');
});

test('formatJsBindingsImportsHints emits the added-hint when the map was written', () => {
  const hints = formatJsBindingsImportsHints({ outcome: 'added', diverged: [] });
  assert.deepEqual(hints, ['💡 Added "#winapp/bindings" package imports to package.json.']);
});

test('formatJsBindingsImportsHints is silent when the map was already present', () => {
  const hints = formatJsBindingsImportsHints({ outcome: 'unchanged', diverged: [] });
  assert.deepEqual(hints, []);
});

test('formatJsBindingsImportsHints warns per divergent alias without overwriting', () => {
  const hints = formatJsBindingsImportsHints({
    outcome: 'unchanged',
    diverged: ['#winapp/bindings', '#winapp/bindings/*'],
  });
  assert.equal(hints.length, 2);
  for (const hint of hints) {
    assert.match(hint, /differs from the winapp default/);
    assert.match(hint, /Delete it and rerun `npx winapp init --add-js-bindings`/);
  }
  assert.match(hints[0], /"#winapp\/bindings"/);
  assert.match(hints[1], /"#winapp\/bindings\/\*"/);
});

test('formatJsBindingsImportsHints combines added + divergent warnings', () => {
  const hints = formatJsBindingsImportsHints({ outcome: 'added', diverged: ['#winapp/bindings'] });
  assert.equal(hints.length, 2);
  assert.match(hints[0], /Added "#winapp\/bindings" package imports/);
  assert.match(hints[1], /differs from the winapp default/);
});

test('planJsBindingsImportsHints suppresses configured hints when quiet', () => {
  const hints = planJsBindingsImportsHints(
    { kind: 'configured', result: { outcome: 'added', diverged: [] } },
    { quiet: true }
  );
  assert.deepEqual(hints, []);
});

test('planJsBindingsImportsHints suppresses unsupported hints when quiet', () => {
  const hints = planJsBindingsImportsHints({ kind: 'unsupported' }, { quiet: true });
  assert.deepEqual(hints, []);
});

test('planJsBindingsImportsHints emits the added hint for configured imports', () => {
  const hints = planJsBindingsImportsHints(
    { kind: 'configured', result: { outcome: 'added', diverged: [] } },
    { quiet: false }
  );
  assert.deepEqual(hints, ['💡 Added "#winapp/bindings" package imports to package.json.']);
});

test('planJsBindingsImportsHints combines configured added hint with divergence warning', () => {
  const hints = planJsBindingsImportsHints(
    { kind: 'configured', result: { outcome: 'added', diverged: ['#winapp/bindings'] } },
    { quiet: false }
  );
  assert.equal(hints.length, 2);
  assert.match(hints[0], /Added "#winapp\/bindings" package imports/);
  assert.match(hints[1], /differs from the winapp default/);
});

test('planJsBindingsImportsHints emits an upgrade hint when package imports are unsupported', () => {
  const hints = planJsBindingsImportsHints({ kind: 'unsupported' }, { quiet: false });
  assert.equal(hints.length, 1);
  assert.match(hints[0], /npm i -D @microsoft\/dynwinrt-codegen@latest && npx winapp init --add-js-bindings/);
});

test('handleGenerateBindings --help prints command help (no fs probe, no exit)', async () => {
  // The hook should short-circuit on --help before touching fs / spawning anything.
  // Capture stdout to assert the --quiet flag is documented (added during this PR).
  const captured: string[] = [];
  const originalLog = console.log;
  console.log = (line: string) => captured.push(typeof line === 'string' ? line : String(line));
  try {
    await handleGenerateBindings(['--help']);
  } finally {
    console.log = originalLog;
  }
  const joined = captured.join('\n');
  assert.match(joined, /Usage:/);
  assert.match(joined, /generate-bindings/);
  assert.match(joined, /--quiet, -q/);
  assert.match(joined, /--verbose/);
});

test('handleGenerateBindings --help works with -h alias', async () => {
  const captured: string[] = [];
  const originalLog = console.log;
  console.log = (line: string) => captured.push(typeof line === 'string' ? line : String(line));
  try {
    await handleGenerateBindings(['-h']);
  } finally {
    console.log = originalLog;
  }
  assert.match(captured.join('\n'), /Usage:/);
});

test('handleGenerateBindings preflight reports missing package.json and exits non-zero', async () => {
  // Empty workspace: no package.json → preflight kind='noPackageJson' → stderr + exit(1).
  const ws = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-cli-hooks-gen-')));
  const errCaptured: string[] = [];
  const exitCalls: number[] = [];
  const originalCwd = process.cwd();
  const originalErr = console.error;
  const originalExit = process.exit;
  // process.exit is throw-replaced so handleGenerateBindings unwinds cleanly.
  process.exit = ((code?: number) => {
    exitCalls.push(typeof code === 'number' ? code : 0);
    throw new Error('__exit__');
  }) as typeof process.exit;
  console.error = (line: string) => errCaptured.push(typeof line === 'string' ? line : String(line));
  try {
    process.chdir(ws);
    try {
      await handleGenerateBindings([]);
    } catch (err) {
      if ((err as Error).message !== '__exit__') {
        throw err;
      }
    }
  } finally {
    process.chdir(originalCwd);
    console.error = originalErr;
    process.exit = originalExit;
    fs.rmSync(ws, { recursive: true, force: true });
  }
  assert.deepEqual(exitCalls, [1]);
  assert.ok(
    errCaptured.some((l) => l.includes('No package.json')),
    `expected a 'No package.json' message, got: ${JSON.stringify(errCaptured)}`
  );
});
