// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Tests for the hand-written targetRecord guard wrapper.
 *
 * `target record` has the same hazard as `ui record`: a programmatic caller who omits a duration
 * would spawn a recording that never ends and cannot be stopped from JavaScript. These tests pin
 * that the guard exists, that it is the only way to reach the command, and that the arguments it
 * builds match what the CLI actually accepts.
 */

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';

import {
  targetRecord,
  buildTargetRecordArgs,
  _targetRecordWithCapture,
  TARGET_RECORD_ARG_SPECS,
} from '../src/target-record-guard';
import type { TargetRecordOptions } from '../src/target-record-guard';
import { targetRecord as publicTargetRecord, default as publicPackage } from '../src/index';

const anyOptions = (options: unknown) => targetRecord(options as TargetRecordOptions);

// ---------------------------------------------------------------------------
// The duration is mandatory
// ---------------------------------------------------------------------------

for (const [label, durationSec] of [
  ['omitted', undefined],
  ['zero (unbounded)', 0],
  ['negative', -1],
  ['NaN', NaN],
  ['Infinity', Infinity],
  ['fractional', 1.5],
  ['beyond the CLI maximum', 86401],
] as const) {
  test(`targetRecord rejects a duration that is ${label}`, async () => {
    await assert.rejects(
      () => anyOptions({ target: 'sandbox', output: 'out.mp4', durationSec }),
      (err: unknown) => {
        assert.ok(err instanceof Error, 'should throw an Error');
        assert.ok(
          err.message.includes('durationSec'),
          `error message should mention durationSec: got "${err.message}"`
        );
        assert.ok(
          err.message.includes('targetRecord'),
          `error message should name the function the caller invoked: got "${err.message}"`
        );
        return true;
      }
    );
  });
}

for (const [label, options] of [
  ['undefined', undefined],
  ['null', null],
] as const) {
  test(`targetRecord(${label}) explains the requirement instead of throwing a TypeError`, async () => {
    await assert.rejects(
      () => anyOptions(options),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.ok(
          !err.message.startsWith('Cannot read properties'),
          `must not be a raw TypeError; got: "${err.message}"`
        );
        assert.ok(err.message.includes('durationSec'), `got: "${err.message}"`);
        return true;
      }
    );
  });
}

test('targetRecord explains why the duration is required and how to fix the call', async () => {
  let caught: Error | undefined;
  try {
    await targetRecord({ target: 'sandbox', durationSec: 0 });
  } catch (e) {
    caught = e as Error;
  }
  assert.ok(caught, 'should have thrown');
  assert.ok(
    caught.message.includes('Ctrl+C') && caught.message.includes('durationSec > 0'),
    `error should say what the CLI can do that the wrapper cannot, and what to pass: "${caught.message}"`
  );
});

// ---------------------------------------------------------------------------
// Argument building
// ---------------------------------------------------------------------------

test('buildTargetRecordArgs: a minimal call names the target after the -- terminator', () => {
  assert.deepEqual(buildTargetRecordArgs({ target: 'sandbox', durationSec: 5 }), [
    'target',
    'record',
    '--duration-sec',
    '5',
    '--',
    'sandbox',
  ]);
});

test('buildTargetRecordArgs: every option reaches the CLI', () => {
  const args = buildTargetRecordArgs({
    target: 'sandbox',
    durationSec: 10,
    fps: 4,
    frames: true,
    json: true,
    maxEdge: 1280,
    output: 'C:\\out\\desktop.mp4',
    quiet: true,
    verbose: false, // falsy — must NOT appear
  });

  const termIdx = args.indexOf('--');
  assert.ok(termIdx >= 0, '-- terminator must be present');
  assert.equal(args[termIdx + 1], 'sandbox', 'the target must follow the -- terminator');
  assert.ok(args.indexOf('--duration-sec') < termIdx, 'named options must precede the terminator');

  assert.equal(args[args.indexOf('--duration-sec') + 1], '10');
  assert.equal(args[args.indexOf('--fps') + 1], '4');
  assert.ok(args.includes('--frames'));
  assert.ok(args.includes('--json'));
  assert.equal(args[args.indexOf('--max-edge') + 1], '1280');
  assert.equal(args[args.indexOf('--output') + 1], 'C:\\out\\desktop.mp4');
  assert.ok(args.includes('--quiet'));
  assert.ok(!args.includes('--verbose'), '--verbose must not appear when verbose is false');
});

test('buildTargetRecordArgs stays in sync with the generated target record options', () => {
  const generatedPath = path.resolve(process.cwd(), 'src', 'winapp-commands.ts');
  const source = fs.readFileSync(generatedPath, 'utf8');
  const match = source.match(/export interface TargetRecordOptions extends CommonOptions \{([\s\S]*?)\n\}/);
  assert.ok(match, 'generated TargetRecordOptions interface must be present');

  const generatedOptions = [...match[1].matchAll(/^\s+([a-zA-Z][a-zA-Z0-9]*)\??:/gm)].map((m) => m[1]).sort();
  const wrapperOptions = [
    ...TARGET_RECORD_ARG_SPECS.map((spec) => spec.property).filter(
      (property) => property !== 'quiet' && property !== 'verbose'
    ),
    'target',
  ].sort();

  assert.deepEqual(wrapperOptions, generatedOptions);
});

// ---------------------------------------------------------------------------
// The guard is the only route to the command
// ---------------------------------------------------------------------------

test('the generated delegate for target record is kept out of the package surface', () => {
  const source = fs.readFileSync(path.resolve(process.cwd(), 'src', 'winapp-commands.ts'), 'utf8');

  assert.ok(
    !/export async function targetRecord\b/.test(source),
    'an exported generated targetRecord would let callers bypass the duration guard'
  );
  assert.ok(
    /_targetRecordGenerated/.test(source),
    'the generator must still emit the internal marker for target record'
  );
});

test('the package entrypoint exposes the guarded targetRecord', async () => {
  assert.equal(
    publicTargetRecord,
    publicPackage.targetRecord,
    'default export must use the guarded targetRecord entrypoint'
  );

  await assert.rejects(
    () => publicTargetRecord({ target: 'sandbox', durationSec: 0 }),
    (err: unknown) => {
      assert.ok(err instanceof Error);
      assert.ok(err.message.includes('durationSec'), `got: "${err.message}"`);
      return true;
    }
  );
});

// ---------------------------------------------------------------------------
// Success path
// ---------------------------------------------------------------------------

test('_targetRecordWithCapture: a valid duration reaches the CLI once and the result is returned', async () => {
  const calls: { args: string[]; opts: unknown }[] = [];
  async function mockCapture(args: string[], opts: unknown) {
    calls.push({ args, opts });
    return { exitCode: 0, stdout: '{"frames":8}', stderr: '' };
  }

  const result = await _targetRecordWithCapture(
    { target: 'sandbox', durationSec: 1, output: 'rec.mp4', cwd: 'C:\\work' },
    mockCapture as Parameters<typeof _targetRecordWithCapture>[1]
  );

  assert.equal(calls.length, 1, 'capture must be called exactly once');
  assert.deepEqual(calls[0].args.slice(0, 2), ['target', 'record']);
  assert.equal(calls[0].args[calls[0].args.indexOf('--duration-sec') + 1], '1');
  assert.deepEqual(calls[0].opts, { cwd: 'C:\\work' });
  assert.deepEqual(result, { exitCode: 0, stdout: '{"frames":8}', stderr: '' });
});

test('_targetRecordWithCapture: no cwd → empty capture options', async () => {
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _targetRecordWithCapture(
    { target: 'sandbox', durationSec: 3 },
    mockCapture as Parameters<typeof _targetRecordWithCapture>[1]
  );
  assert.deepEqual(capturedOpts[0], {});
});
