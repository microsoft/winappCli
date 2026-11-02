// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { afterEach, mock, test } from 'node:test';
import * as assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import childProcess = require('child_process');
import { uiAccessibilityLoop, uiAudit } from '../src/winapp-commands';
import { WINAPP_UI_WORKFLOW_ID } from '../src/winapp-cli-utils';

afterEach(() => mock.restoreAll());

test('audit and configured loop forward cancellation and workflow only to the native child', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];
  const calls: { args: string[]; signal?: AbortSignal; env?: NodeJS.ProcessEnv }[] = [];
  mock.method(childProcess, 'spawn', ((
    _command: string,
    args: string[],
    options: { signal?: AbortSignal; env?: NodeJS.ProcessEnv }
  ) => {
    calls.push({ args, signal: options.signal, env: options.env });
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);
  const controller = new AbortController();
  const context = { signal: controller.signal, workflowId: 'accessloop-context' };

  await uiAudit({ app: '123', ...context });
  await uiAccessibilityLoop({ preset: 'reviewed.json', output: 'evidence', acceptActions: true, ...context });

  assert.equal(calls.length, 2);
  assert.deepEqual(calls[0].args.slice(0, 2), ['ui', 'audit']);
  assert.deepEqual(calls[1].args.slice(0, 3), ['ui', 'accessibility', 'loop']);
  for (const call of calls) {
    assert.equal(call.signal, controller.signal);
    assert.equal(call.env?.[WINAPP_UI_WORKFLOW_ID], context.workflowId);
  }
  assert.equal(process.env[WINAPP_UI_WORKFLOW_ID], before);
});
