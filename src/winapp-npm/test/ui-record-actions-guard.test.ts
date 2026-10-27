// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import { uiRecordActions, buildUiRecordActionsArgs, _uiRecordActionsWithCapture } from '../src/ui-record-actions-guard';
import { uiRecordActions as publicRecordActions, default as winapp } from '../src/index';

test('programmatic action recordings require a finite positive duration', async () => {
  for (const durationSec of [0, -1, 3601, 0.5, NaN, Infinity]) {
    await assert.rejects(() => uiRecordActions({ durationSec }), /durationSec/);
  }
  await assert.rejects(
    () => (uiRecordActions as (options: unknown) => Promise<unknown>)({ app: 'Demo' }),
    /durationSec/
  );
});

test('recording guard emits all options and never opts into literal text by default', () => {
  assert.deepEqual(buildUiRecordActionsArgs({ durationSec: 10, app: 'Demo' }), [
    'ui',
    'record-actions',
    '--duration-sec',
    '10',
    '--app',
    'Demo',
  ]);
  assert.deepEqual(
    buildUiRecordActionsArgs({
      durationSec: 5,
      window: 123,
      output: 'flow.json',
      maxSteps: 100,
      captureText: true,
      includeInjected: true,
      json: true,
      quiet: true,
      verbose: true,
    }),
    [
      'ui',
      'record-actions',
      '--duration-sec',
      '5',
      '--window',
      '123',
      '--output',
      'flow.json',
      '--max-steps',
      '100',
      '--capture-text',
      '--include-injected',
      '--json',
      '--quiet',
      '--verbose',
    ]
  );
});

test('recording wrapper forwards cwd and structured process results', async () => {
  const result = await _uiRecordActionsWithCapture(
    { durationSec: 3, app: 'Demo', cwd: 'C:\\owned-demo' },
    async (args, options) => {
      assert.deepEqual(args.slice(0, 2), ['ui', 'record-actions']);
      assert.deepEqual(options, { cwd: 'C:\\owned-demo' });
      return { exitCode: 0, stdout: '{"status":"recorded"}', stderr: '' };
    }
  );
  assert.equal(result.stdout, '{"status":"recorded"}');
});

test('both public npm entry points expose the bounded action recorder', () => {
  assert.equal(publicRecordActions, uiRecordActions);
  assert.equal(winapp.uiRecordActions, uiRecordActions);
});

test('recording wrapper forwards cancellation and desktop workflow identity', async () => {
  const controller = new AbortController();
  await _uiRecordActionsWithCapture(
    { durationSec: 3, app: 'Demo', signal: controller.signal, workflowId: 'accessloop-test' },
    async (_args, options) => {
      assert.equal(options.signal, controller.signal);
      assert.equal(options.workflowId, 'accessloop-test');
      return { exitCode: 0, stdout: '', stderr: '' };
    }
  );
});
