// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { afterEach, mock, test } from 'node:test';
import * as assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import childProcess = require('child_process');

import {
  migrate,
  migrateDecideProjectItem,
  migrateVerify,
} from '../src/winapp-commands';

function captureSpawnArgs(): { calls: string[][] } {
  const state = { calls: [] as string[][] };
  mock.method(childProcess, 'spawn', ((_cmd: string, args: string[]) => {
    state.calls.push(args);
    const child = new EventEmitter() as EventEmitter & {
      stdout: EventEmitter;
      stderr: EventEmitter;
    };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);
  return state;
}

afterEach(() => {
  mock.restoreAll();
});

test('migrate parent and verify wrappers retain their distinct command paths', async () => {
  const state = captureSpawnArgs();

  await migrate({ source: 'C:\\source', output: 'C:\\target' });
  await migrateVerify({ target: 'C:\\target' });

  assert.deepEqual(
    state.calls[0].slice(0, 4),
    ['migrate', 'C:\\source', '--output', 'C:\\target']
  );
  assert.deepEqual(
    state.calls[1].slice(0, 3),
    ['migrate', 'verify', 'C:\\target']
  );
});

test('migrate decide-project-item emits repeatable evidence files and structured decision options', async () => {
  const state = captureSpawnArgs();

  await migrateDecideProjectItem({
    target: 'C:\\target',
    item: 'project-item-0123456789abcdef',
    strategy: 'copied-linked-content',
    targetPath: 'Strings\\NOTICE.json',
    targetItemType: 'Content',
    evidenceFile: ['App.csproj', 'Directory.Build.targets'],
    rationale: 'Copied and relinked with matching bytes.',
  });

  assert.deepEqual(state.calls[0], [
    'migrate',
    'decide-project-item',
    'C:\\target',
    '--evidence-file',
    'App.csproj',
    '--evidence-file',
    'Directory.Build.targets',
    '--item',
    'project-item-0123456789abcdef',
    '--rationale',
    'Copied and relinked with matching bytes.',
    '--strategy',
    'copied-linked-content',
    '--target-item-type',
    'Content',
    '--target-path',
    'Strings\\NOTICE.json',
  ]);
});
