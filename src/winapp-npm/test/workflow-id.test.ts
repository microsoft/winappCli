// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test, mock } from 'node:test';
import * as assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import childProcess = require('child_process');

import { WINAPP_UI_WORKFLOW_ID } from '../src/winapp-cli-utils';
import { uiListWindows, uiYield } from '../src/winapp-commands';

// Cooperative desktop turns group commands by workflow id. The wrapper must pass that id to the
// spawned child ONLY: writing it into process.env would silently enrol every later call in this
// process — including unrelated ones — into a workflow the caller meant for one command, and would
// race across concurrent calls.

test('workflowId is not written into the parent process environment', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];

  // The CLI binary is not present in a unit-test checkout, so the call fails; the assertion is about
  // what the wrapper did to this process before spawning, which happens either way.
  await uiListWindows({ workflowId: 'unit-test-workflow' }).catch(() => undefined);

  assert.equal(
    process.env[WINAPP_UI_WORKFLOW_ID],
    before,
    'the wrapper must never mutate process.env — the id belongs to the child only'
  );
});

test('omitting workflowId leaves an inherited value untouched', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];
  process.env[WINAPP_UI_WORKFLOW_ID] = 'inherited-workflow';
  try {
    await uiListWindows({}).catch(() => undefined);

    assert.equal(
      process.env[WINAPP_UI_WORKFLOW_ID],
      'inherited-workflow',
      'an environment-provided workflow id must still reach the child unchanged'
    );
  } finally {
    if (before === undefined) {
      delete process.env[WINAPP_UI_WORKFLOW_ID];
    } else {
      process.env[WINAPP_UI_WORKFLOW_ID] = before;
    }
  }
});

// An unpaired surrogate is not text. Node replaces every one of them with U+FFFD while building the
// child environment, so "\uD800", "\uD801" and a literal "\uFFFD" all arrive at the CLI as the same
// valid string. The CLI cannot tell them apart — it would see well-formed text and group three
// unrelated workflows into one owner sharing one desktop turn — so the check has to happen here,
// before the value crosses the process boundary and the distinction is lost.

test('an ill-formed workflowId is rejected before the CLI is spawned', async () => {
  const spawnCalls: unknown[] = [];
  mock.method(childProcess, 'spawn', ((..._args: unknown[]) => {
    spawnCalls.push(_args);
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);

  for (const ill of ['\uD800', '\uDC00', 'wf-\uD801-tail', 'lead\uDFFF']) {
    await assert.rejects(
      () => uiListWindows({ workflowId: ill }),
      /unpaired UTF-16 surrogate/,
      `expected ${JSON.stringify(ill)} to be refused`
    );
  }

  assert.equal(spawnCalls.length, 0, 'an ill-formed workflow id must never reach a child process');
  mock.restoreAll();
});

test('well-formed workflow ids — including a real U+FFFD — are still accepted', async () => {
  const spawnCalls: unknown[] = [];
  mock.method(childProcess, 'spawn', ((..._args: unknown[]) => {
    spawnCalls.push(_args);
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);

  // A surrogate PAIR is one valid astral character, and U+FFFD is an ordinary character a caller is
  // entitled to use; neither may be caught by the ill-formed check.
  for (const ok of ['plain-workflow', '550e8400-e29b-41d4-a716-446655440000', '\uD83D\uDE80', '\uFFFD']) {
    await uiListWindows({ workflowId: ok }).catch(() => undefined);
  }

  assert.equal(spawnCalls.length, 4, 'every well-formed workflow id must still spawn the CLI');
  mock.restoreAll();
});

// `ui yield` releases the turn belonging to one specific workflow, so the id it carries decides
// which reservation is ended. A generated wrapper that dropped it — or that reached for the ambient
// process.env instead — would either yield nothing or yield somebody else's turn.

test('uiYield sends its per-call workflowId to the child and nowhere else', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];
  const childEnvs: (NodeJS.ProcessEnv | undefined)[] = [];

  mock.method(childProcess, 'spawn', ((..._args: unknown[]) => {
    const options = _args[2] as { env?: NodeJS.ProcessEnv } | undefined;
    childEnvs.push(options?.env);
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);

  await uiYield({ workflowId: 'yield-unit-test-workflow' }).catch(() => undefined);

  assert.equal(childEnvs.length, 1, 'ui yield must spawn the CLI');
  assert.equal(
    childEnvs[0]?.[WINAPP_UI_WORKFLOW_ID],
    'yield-unit-test-workflow',
    'the id decides whose turn is released, so it has to reach the child'
  );
  assert.equal(
    process.env[WINAPP_UI_WORKFLOW_ID],
    before,
    'and it must not leak into this process'
  );
  mock.restoreAll();
});

test('uiYield refuses an ill-formed workflowId before spawning', async () => {
  const spawnCalls: unknown[] = [];
  mock.method(childProcess, 'spawn', ((..._args: unknown[]) => {
    spawnCalls.push(_args);
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);

  await assert.rejects(() => uiYield({ workflowId: '\uD800' }), /unpaired UTF-16 surrogate/);
  assert.equal(spawnCalls.length, 0);
  mock.restoreAll();
});
