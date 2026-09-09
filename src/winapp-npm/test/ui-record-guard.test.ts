// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Tests for the hand-written uiRecord guard wrapper.
 * Verifies that uiRecord rejects missing/non-positive durationSec with a clear error,
 * and that buildUiRecordArgs + _uiRecordWithCapture work correctly on the success path.
 */

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';

import { uiRecord, buildUiRecordArgs, _uiRecordWithCapture, UI_RECORD_ARG_SPECS } from '../src/ui-record-guard';
import { uiRecord as publicUiRecord, default as publicPackage } from '../src/index';
import type { UiRecordOptions } from '../src/ui-record-guard';

// ---------------------------------------------------------------------------
// durationSec validation tests
// JS callers may omit durationSec at runtime despite the TypeScript type, so
// we use `as any` casts for the invalid-input tests.
// ---------------------------------------------------------------------------

test('uiRecord with no durationSec throws clear error', async () => {
  await assert.rejects(
    () => (uiRecord as (o: unknown) => Promise<unknown>)({ app: 'myapp' }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord with durationSec = 0 throws clear error', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: 0 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord with negative durationSec throws clear error', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: -1 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord error message explains why and how to fix it', async () => {
  let caught: Error | undefined;
  try {
    await uiRecord({ durationSec: 0 });
  } catch (e) {
    caught = e as Error;
  }
  assert.ok(caught, 'should have thrown');
  const msg = caught.message;
  assert.ok(
    msg.includes('durationSec > 0') || msg.includes('positive durationSec') || msg.includes('[1, 86400]'),
    `error message should describe the fix: "${msg}"`
  );
});

// ---------------------------------------------------------------------------
// M7 — buildUiRecordArgs: correct CLI args for every option
// ---------------------------------------------------------------------------

test('buildUiRecordArgs: minimal options (only durationSec)', () => {
  const args = buildUiRecordArgs({ durationSec: 5 });
  assert.deepEqual(args, ['ui', 'record', '--duration-sec', '5']);
});

test('buildUiRecordArgs: all options produce correct arg list', () => {
  const opts: UiRecordOptions = {
    app: 'myapp',
    captureScreen: true,
    durationSec: 10,
    fps: 30,
    frames: true,
    json: true,
    maxEdge: 1080,
    output: 'out.mp4',
    window: 12345,
    quiet: true,
    verbose: false, // falsy — must NOT appear
    selector: 'btn-ok-a1b2',
    cwd: 'C:\\Projects\\test',
  };
  const args = buildUiRecordArgs(opts);

  // Named options come before the selector
  const dIdx = args.indexOf('--duration-sec');
  const sIdx = args.indexOf('--');
  assert.ok(dIdx >= 0, '--duration-sec must be present');
  assert.ok(sIdx >= 0, '-- terminator must be present');
  assert.ok(dIdx < sIdx, 'named options must precede the -- terminator');
  assert.equal(args[sIdx + 1], 'btn-ok-a1b2', 'selector must follow the -- terminator');

  // Spot-check required flags
  assert.ok(args.includes('--app'), '--app must be present');
  assert.equal(args[args.indexOf('--app') + 1], 'myapp');
  assert.ok(args.includes('--capture-screen'), '--capture-screen must be present');
  assert.equal(args[args.indexOf('--duration-sec') + 1], '10');
  assert.ok(args.includes('--fps'));
  assert.equal(args[args.indexOf('--fps') + 1], '30');
  assert.ok(args.includes('--frames'));
  assert.ok(args.includes('--json'));
  assert.ok(args.includes('--max-edge'));
  assert.equal(args[args.indexOf('--max-edge') + 1], '1080');
  assert.ok(args.includes('--output'));
  assert.equal(args[args.indexOf('--output') + 1], 'out.mp4');
  assert.ok(args.includes('--window'));
  assert.equal(args[args.indexOf('--window') + 1], '12345');
  assert.ok(args.includes('--quiet'));
  assert.ok(!args.includes('--verbose'), '--verbose must not appear when verbose is false');
});

test('buildUiRecordArgs: option-shaped selector goes after -- terminator', () => {
  const args = buildUiRecordArgs({ durationSec: 5, selector: '--capture-screen' });
  const termIdx = args.indexOf('--');
  assert.ok(termIdx >= 0, '-- terminator must be present');
  assert.equal(args[termIdx + 1], '--capture-screen', 'option-shaped selector must follow --');
  // The -- terminator must come after all named options
  assert.ok(termIdx > 2, '-- must not be the very first arg after ui record');
});

test('buildUiRecordArgs: no selector produces no -- terminator', () => {
  const args = buildUiRecordArgs({ durationSec: 5, app: 'myapp' });
  assert.ok(!args.includes('--'), 'no selector means no -- terminator');
});

// ---------------------------------------------------------------------------
// M7 — _uiRecordWithCapture: full success-path test (mocked capture function)
// ---------------------------------------------------------------------------

test('_uiRecordWithCapture: calls capture with correct args and returns result', async () => {
  const capturedArgs: string[][] = [];
  const capturedOpts: unknown[] = [];
  const fakeResult = { exitCode: 0, stdout: '{"frames":30}', stderr: '' };

  async function mockCapture(args: string[], opts: unknown) {
    capturedArgs.push(args);
    capturedOpts.push(opts);
    return fakeResult;
  }

  const opts: UiRecordOptions = {
    durationSec: 5,
    app: 'myapp',
    fps: 15,
    output: 'rec.mp4',
    cwd: 'C:\\work',
    workflowId: 'wf-forwarded',
  };

  const result = await _uiRecordWithCapture(opts, mockCapture as Parameters<typeof _uiRecordWithCapture>[1]);

  // Exactly one CLI invocation
  assert.equal(capturedArgs.length, 1, 'capture must be called exactly once');

  // Args must start with ui record
  assert.equal(capturedArgs[0][0], 'ui');
  assert.equal(capturedArgs[0][1], 'record');

  // --duration-sec is present with value 5
  const dIdx = capturedArgs[0].indexOf('--duration-sec');
  assert.ok(dIdx >= 0, '--duration-sec must be in args');
  assert.equal(capturedArgs[0][dIdx + 1], '5');

  // cwd and workflowId are forwarded
  assert.deepEqual(capturedOpts[0], { cwd: 'C:\\work', workflowId: 'wf-forwarded' });

  // Result is correctly mapped
  assert.equal(result.exitCode, 0);
  assert.equal(result.stdout, '{"frames":30}');
  assert.equal(result.stderr, '');
});

test('_uiRecordWithCapture: workflowId reaches the CLI child environment', async () => {
  // A recording is the one long-running ui command, so it is the one most likely to be running while
  // its workflow also clicks. Dropping workflowId here made the recording an anonymous one-shot, which
  // blocks every other command for its whole duration instead of overlapping with its own workflow.
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _uiRecordWithCapture(
    { durationSec: 3, workflowId: 'wf-only' },
    mockCapture as Parameters<typeof _uiRecordWithCapture>[1]
  );

  assert.deepEqual(
    capturedOpts[0],
    { workflowId: 'wf-only' },
    'workflowId must be forwarded even when no other capture option is set'
  );
});

test('_uiRecordWithCapture: an empty workflowId is forwarded, not silently dropped', async () => {
  // An empty string is a scripting mistake — an unset variable that expanded to "" — and the CLI
  // rejects it with invalid_ui_workflow_id so the caller finds out. Testing it for truthiness instead
  // of for undefined swallowed that mistake here and ran the recording as an anonymous one-shot, so
  // the same bad input failed loudly through the generated wrappers and passed quietly through this
  // one. Forwarding keeps a single answer for a single input.
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _uiRecordWithCapture(
    { durationSec: 3, workflowId: '' },
    mockCapture as Parameters<typeof _uiRecordWithCapture>[1]
  );

  assert.deepEqual(
    capturedOpts[0],
    { workflowId: '' },
    'an empty workflowId must reach the CLI so it can reject it'
  );
});

test('_uiRecordWithCapture: an omitted workflowId is still omitted', async () => {
  // The other half of the contract: only `undefined` means "no opinion", and it must leave any
  // inherited WINAPP_UI_WORKFLOW_ID in the environment alone.
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _uiRecordWithCapture(
    { durationSec: 3 },
    mockCapture as Parameters<typeof _uiRecordWithCapture>[1]
  );

  assert.deepEqual(capturedOpts[0], {}, 'no workflowId key at all when the caller omitted it');
});

// The public uiRecord differs from _uiRecordWithCapture only in that it spawns the CLI instead of
// taking an injected capture function — the forwarding line above is the same one. It is not
// exercised end-to-end here because the compiled tests run from dist-test/, where the package's own
// binary resolution (dist/../bin) does not find the built CLI. _uiRecordWithCapture exists for
// exactly this reason, and the environment half of the contract is covered in workflow-id.test.ts.

test('_uiRecordWithCapture: no cwd → empty capture options', async () => {
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _uiRecordWithCapture({ durationSec: 3 }, mockCapture as Parameters<typeof _uiRecordWithCapture>[1]);
  assert.deepEqual(capturedOpts[0], {}, 'empty cwd must produce empty capture options object');
});

// ---------------------------------------------------------------------------
// L2 — option-shaped selectors: confirmed by buildUiRecordArgs tests above
// Extra edge-case test
// ---------------------------------------------------------------------------

test('buildUiRecordArgs: leading-dash selector does not appear before --', () => {
  const args = buildUiRecordArgs({ durationSec: 5, selector: '--json' });
  const termIdx = args.indexOf('--');
  assert.ok(termIdx >= 0, '-- must be present');
  // '--json' must ONLY appear after '--', not as a named option before it
  const jsonBeforeTerm = args.slice(0, termIdx).includes('--json');
  assert.ok(!jsonBeforeTerm, '--json selector must not appear as a flag before the -- terminator');
  assert.equal(args[termIdx + 1], '--json', 'selector must be the arg after --');
});

// ---------------------------------------------------------------------------
// L1 (round-9) — null/undefined options must throw a clear usage error, not TypeError
// ---------------------------------------------------------------------------

test('uiRecord(undefined) throws documented range error, not raw TypeError', async () => {
  await assert.rejects(
    () => (uiRecord as (o: unknown) => Promise<unknown>)(undefined),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error (not a raw TypeError)');
      // Must NOT be a raw property-access TypeError ("Cannot read properties of undefined")
      assert.ok(
        !err.message.startsWith('Cannot read properties'),
        `must not be a raw TypeError; got: "${err.message}"`
      );
      // Must mention durationSec (same style as the existing guard)
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec; got: "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord(null) throws documented range error, not raw TypeError', async () => {
  await assert.rejects(
    () => (uiRecord as (o: unknown) => Promise<unknown>)(null),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error (not a raw TypeError)');
      assert.ok(
        !err.message.startsWith('Cannot read properties'),
        `must not be a raw TypeError; got: "${err.message}"`
      );
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec; got: "${err.message}"`
      );
      return true;
    }
  );
});

test('public package entrypoint exposes guarded uiRecord', async () => {
  assert.equal(publicUiRecord, publicPackage.uiRecord, 'default export must use the guarded uiRecord entrypoint');
  await assert.rejects(
    () => publicUiRecord({ durationSec: 0 }),
    (err: unknown) => {
      assert.ok(err instanceof Error);
      assert.ok(err.message.includes('durationSec'), `error should come from guarded public entrypoint: ${err.message}`);
      return true;
    }
  );
});

test('buildUiRecordArgs stays in sync with generated ui record options', () => {
  const generatedPath = path.resolve(process.cwd(), 'src', 'winapp-commands.ts');
  const source = fs.readFileSync(generatedPath, 'utf8');
  const match = source.match(/export interface UiRecordOptions extends CommonOptions \{([\s\S]*?)\n\}/);
  assert.ok(match, 'generated UiRecordOptions interface must be present');

  const generatedOptions = [...match[1].matchAll(/^\s+([a-zA-Z][a-zA-Z0-9]*)\??:/gm)]
    .map((m) => m[1])
    .sort();
  const wrapperOptions = [
    ...UI_RECORD_ARG_SPECS.map((spec) => spec.property).filter((property) => property !== 'quiet' && property !== 'verbose'),
    'selector',
  ].sort();
  assert.deepEqual(wrapperOptions, generatedOptions);
  assert.ok(UI_RECORD_ARG_SPECS.some((spec) => spec.property === 'quiet'), 'wrapper must still forward CommonOptions.quiet');
  assert.ok(UI_RECORD_ARG_SPECS.some((spec) => spec.property === 'verbose'), 'wrapper must still forward CommonOptions.verbose');
});



test('uiRecord with NaN durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: NaN }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with Infinity durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: Infinity }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with -Infinity durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: -Infinity }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with fractional durationSec (1.5) throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: 1.5 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with durationSec = 1 (minimum valid) proceeds to capture', async () => {
  // durationSec = 1 is the minimum valid value — guard must NOT throw.
  let captureCalledWith: string[][] = [];
  async function mockCapture(args: string[]) {
    captureCalledWith.push(args);
    return { exitCode: 0, stdout: '', stderr: '' };
  }
  await _uiRecordWithCapture(
    { durationSec: 1 },
    mockCapture as Parameters<typeof _uiRecordWithCapture>[1]
  );
  assert.equal(captureCalledWith.length, 1, 'capture must be called exactly once for valid durationSec=1');
});
