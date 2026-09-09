// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Coverage for `CommonOptions.signal` (issue #764).
 *
 * `winapp ui` commands take cooperative turns on the shared desktop, so a call can wait an unbounded
 * time for another workflow to finish. `signal` is the only way a programmatic caller can stop
 * waiting, so these tests pin down that it actually reaches `child_process.spawn` on every path and
 * that an abort surfaces as an `AbortError` rather than a generic spawn failure.
 */

import { test, mock, afterEach } from 'node:test';
import * as assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
// Use import-equals so childProcess is the REAL cached module object (not an __importStar copy).
// winapp-cli-utils calls `require('child_process').spawn`, so the mock must be installed on the same
// shared exports object to be observed.
import childProcess = require('child_process');

import { callWinappCli, callWinappCliCapture } from '../src/winapp-cli-utils';
import { uiInspect } from '../src/winapp-commands';
import { uiRecord } from '../src/ui-record-guard';

type SpawnOptions = { signal?: AbortSignal };

/** Records the options object handed to spawn and returns a child that closes successfully. */
function captureSpawnOptions(): { calls: SpawnOptions[] } {
  const state = { calls: [] as SpawnOptions[] };
  mock.method(childProcess, 'spawn', ((_cmd: string, _args: string[], options: SpawnOptions) => {
    state.calls.push(options);
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('close', 0));
    return child;
  }) as unknown as typeof childProcess.spawn);
  return state;
}

/** Emits the AbortError Node raises when a spawn is cancelled through its signal. */
function abortingSpawn(): void {
  mock.method(childProcess, 'spawn', ((_cmd: string, _args: string[], _options: SpawnOptions) => {
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => {
      const error = new Error('The operation was aborted');
      error.name = 'AbortError';
      child.emit('error', error);
    });
    return child;
  }) as unknown as typeof childProcess.spawn);
}

afterEach(() => {
  mock.restoreAll();
});

test('callWinappCli forwards the signal to spawn', async () => {
  const spawned = captureSpawnOptions();
  const controller = new AbortController();

  await callWinappCli(['ui', 'status'], { signal: controller.signal });

  assert.equal(spawned.calls.length, 1);
  assert.equal(spawned.calls[0].signal, controller.signal);
});

test('callWinappCliCapture forwards the signal to spawn', async () => {
  const spawned = captureSpawnOptions();
  const controller = new AbortController();

  await callWinappCliCapture(['ui', 'status'], { signal: controller.signal });

  assert.equal(spawned.calls.length, 1);
  assert.equal(spawned.calls[0].signal, controller.signal);
});

test('omitting the signal leaves spawn uncancellable rather than passing undefined semantics', async () => {
  const spawned = captureSpawnOptions();

  await callWinappCliCapture(['ui', 'status']);

  assert.equal(spawned.calls.length, 1);
  assert.equal(spawned.calls[0].signal, undefined);
});

test('generated command wrappers thread the signal through captureOpts', async () => {
  // The generator emits captureOpts() for every command, so proving it for one wrapper proves the
  // shape for all of them.
  const spawned = captureSpawnOptions();
  const controller = new AbortController();

  await uiInspect({ app: 'notepad', signal: controller.signal });

  assert.equal(spawned.calls.length, 1);
  assert.equal(spawned.calls[0].signal, controller.signal);
});

test('the hand-written uiRecord guard threads the signal through', async () => {
  const spawned = captureSpawnOptions();
  const controller = new AbortController();

  await uiRecord({ app: 'notepad', durationSec: 1, signal: controller.signal });

  assert.equal(spawned.calls.length, 1);
  assert.equal(spawned.calls[0].signal, controller.signal);
});

test('uiRecord still requires a finite positive duration even with a signal', async () => {
  // An AbortSignal can only stop a recording by killing the child, which does not finalize the MP4,
  // so it must not be treated as a way to opt into unbounded recording.
  const controller = new AbortController();

  await assert.rejects(
    () => uiRecord({ app: 'notepad', durationSec: 0, signal: controller.signal } as never),
    /durationSec must be a finite integer/
  );
});

test('an aborted call rejects with AbortError, not a generic spawn failure', async () => {
  abortingSpawn();
  const controller = new AbortController();

  await assert.rejects(
    () => callWinappCliCapture(['ui', 'click'], { signal: controller.signal }),
    (error: Error) => {
      assert.equal(error.name, 'AbortError', 'callers must be able to tell cancellation from a launch failure');
      return true;
    }
  );
});

test('an aborted inherit-stdio call also rejects with AbortError', async () => {
  abortingSpawn();
  const controller = new AbortController();

  await assert.rejects(
    () => callWinappCli(['ui', 'click'], { signal: controller.signal }),
    (error: Error) => {
      assert.equal(error.name, 'AbortError');
      return true;
    }
  );
});

test('a real spawn failure is still wrapped with the winapp-cli context', async () => {
  mock.method(childProcess, 'spawn', ((_cmd: string, _args: string[], _options: SpawnOptions) => {
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => child.emit('error', new Error('ENOENT')));
    return child;
  }) as unknown as typeof childProcess.spawn);

  await assert.rejects(
    () => callWinappCliCapture(['ui', 'status']),
    /Failed to execute winapp-cli/
  );
});

/**
 * Emits what Node really emits for an aborted spawn: the AbortError on 'error', and then 'close'
 * with a non-zero code, because the child was killed.
 */
function abortingSpawnThatAlsoCloses(): void {
  mock.method(childProcess, 'spawn', ((_cmd: string, _args: string[], _options: SpawnOptions) => {
    const child = new EventEmitter() as EventEmitter & { stdout: EventEmitter; stderr: EventEmitter };
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    process.nextTick(() => {
      const error = new Error('The operation was aborted');
      error.name = 'AbortError';
      child.emit('error', error);
      child.emit('close', 1);
    });
    return child;
  }) as unknown as typeof childProcess.spawn);
}

test('aborting an exitOnError call rejects without terminating the host process', async () => {
  // Both events arrive for a single aborted spawn. The promise ignores the second settle on its own,
  // but process.exit does not: without a gate the caller is handed an AbortError to handle and the
  // whole process is then torn down underneath it, which for a library is the worst of both.
  abortingSpawnThatAlsoCloses();
  const controller = new AbortController();

  const exitCalls: (number | undefined)[] = [];
  mock.method(process, 'exit', ((code?: number) => {
    exitCalls.push(code);
    // Deliberately does NOT terminate, so the test can observe the bug instead of dying with it.
    return undefined as never;
  }) as typeof process.exit);

  await assert.rejects(
    () => callWinappCli(['ui', 'click'], { signal: controller.signal, exitOnError: true }),
    (error: Error) => {
      assert.equal(error.name, 'AbortError');
      return true;
    }
  );

  // Give the trailing 'close' every chance to be processed before asserting.
  await new Promise((r) => setImmediate(r));

  assert.deepEqual(exitCalls, [], 'a cancelled call must never call process.exit');
});

test('a late close cannot re-settle an aborted capture call', async () => {
  abortingSpawnThatAlsoCloses();
  const controller = new AbortController();

  await assert.rejects(
    () => callWinappCliCapture(['ui', 'click'], { signal: controller.signal }),
    (error: Error) => {
      assert.equal(error.name, 'AbortError', 'the first outcome is the one the caller asked about');
      return true;
    }
  );

  await new Promise((r) => setImmediate(r));
});
