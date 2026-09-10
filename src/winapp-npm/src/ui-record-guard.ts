// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Hand-written guard wrapper for uiRecord.
 *
 * winapp-commands.ts is AUTO-GENERATED. The raw generated delegate for `ui record`
 * is intentionally NOT exported (underscore-prefixed, module-internal) so it cannot
 * bypass this guard. This module is the only public entry point for recording.
 *
 * The guard validates that `durationSec` is provided and positive before calling the
 * CLI, because unbounded recording (durationSec == 0) is only supportable via the CLI
 * with Ctrl+C or piped stdin, which the npm wrapper has no way to drive.
 *
 * `CommonOptions.signal` (issue #764) does NOT relax this. An `AbortSignal` force-terminates the
 * child, so it can stop an unbounded recording only by killing it — leaving partial or invalid MP4
 * output with no graceful finalization. A finite `durationSec` remains required so the normal path
 * always produces a valid recording.
 *
 * This file must NOT be edited by the code generator; it is hand-maintained.
 */

import { callWinappCliCapture } from './winapp-cli-utils';
import type { CallWinappCliCaptureOptions, CallWinappCliCaptureResult } from './winapp-cli-utils';
import type { UiRecordOptions as GeneratedUiRecordOptions, WinappResult } from './winapp-commands';

/**
 * Stricter version of `UiRecordOptions` where `durationSec` is **required** (not optional).
 * This type is the public surface of `uiRecord`; the generated type has it optional.
 * Survives regeneration because it is defined here in the hand-written guard module.
 */
export type UiRecordOptions = Omit<GeneratedUiRecordOptions, 'durationSec'> & {
  durationSec: number;
};

type UiRecordArgSpec = {
  property: keyof UiRecordOptions;
  flag: string;
  kind: 'boolean' | 'value';
};

export const UI_RECORD_ARG_SPECS: readonly UiRecordArgSpec[] = [
  { property: 'app', flag: '--app', kind: 'value' },
  { property: 'captureScreen', flag: '--capture-screen', kind: 'boolean' },
  { property: 'durationSec', flag: '--duration-sec', kind: 'value' },
  { property: 'fps', flag: '--fps', kind: 'value' },
  { property: 'frames', flag: '--frames', kind: 'boolean' },
  { property: 'json', flag: '--json', kind: 'boolean' },
  { property: 'maxEdge', flag: '--max-edge', kind: 'value' },
  { property: 'output', flag: '--output', kind: 'value' },
  { property: 'window', flag: '--window', kind: 'value' },
  { property: 'quiet', flag: '--quiet', kind: 'boolean' },
  { property: 'verbose', flag: '--verbose', kind: 'boolean' },
] as const;

/**
 * Builds the CLI argument list for `ui record` from a validated options object.
 * Named options are placed before the positional selector, and the selector is
 * placed after a `--` terminator so option-shaped selectors (e.g. `--capture-screen`)
 * are never misinterpreted as CLI flags.
 *
 * Exported for unit testing — do not use externally.
 * @internal
 */
export function buildUiRecordArgs(options: UiRecordOptions): string[] {
  const args: string[] = ['ui', 'record'];
  for (const spec of UI_RECORD_ARG_SPECS) {
    const value = options[spec.property];
    if (spec.kind === 'boolean') {
      if (value) args.push(spec.flag);
    } else if (value !== undefined && value !== '') {
      args.push(spec.flag, value.toString());
    }
  }
  // Place the positional selector AFTER '--' so a selector like '--capture-screen' is
  // not misinterpreted as a CLI flag.
  if (options.selector) {
    args.push('--', options.selector);
  }
  return args;
}

/**
 * Record a window or element region to an H.264 MP4.
 *
 * **`durationSec` is required and must be > 0.** Unbounded recording (durationSec == 0) is only
 * supported via the CLI with Ctrl+C or piped stdin. The npm wrapper has no mechanism to stop
 * an unbounded spawn, so passing durationSec == 0 or omitting it will throw a clear error.
 * Set `frames` to write timestamped JPEG evidence beside the MP4.
 *
 * @throws {Error} if `options.durationSec` is not provided or is ≤ 0.
 */
export async function uiRecord(options: UiRecordOptions): Promise<WinappResult> {
  // Runtime guard for JS callers who may pass undefined/null despite the TypeScript type.
  if (options === null || typeof options !== 'object') {
    throw new Error(
      `uiRecord: options must be an object with durationSec as a finite integer in [1, 86400]. ` +
        'Got: null or undefined options. Pass options.durationSec > 0.'
    );
  }
  // durationSec must be a finite integer in [1, 86400]: reject NaN, ±Infinity, non-integers,
  // values < 1, and values > 86400 (CLI upper bound). durationSec == 0 (unbounded) is only
  // supported via the CLI with Ctrl+C or piped stdin — the npm wrapper has no way to stop it.
  if (
    typeof options.durationSec !== 'number' ||
    !Number.isFinite(options.durationSec) ||
    !Number.isInteger(options.durationSec) ||
    options.durationSec < 1 ||
    options.durationSec > 86400
  ) {
    throw new Error(
      `uiRecord: durationSec must be a finite integer in [1, 86400]. Got: ${options.durationSec}. ` +
        'Unbounded recording (durationSec == 0) is only supported via the CLI with Ctrl+C or piped stdin. ' +
        'Pass options.durationSec > 0.'
    );
  }

  const args = buildUiRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = {};
  if (options.cwd) captureOpts.cwd = options.cwd;
  if (options.signal) captureOpts.signal = options.signal;
  if (options.workflowId !== undefined) captureOpts.workflowId = options.workflowId;
  const result = await callWinappCliCapture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Internal implementation that accepts an injectable capture function — used by tests
 * to verify the full success path without spawning a real process.
 * @internal
 */
export async function _uiRecordWithCapture(
  options: UiRecordOptions,
  capture: (args: string[], opts: CallWinappCliCaptureOptions) => Promise<CallWinappCliCaptureResult>
): Promise<WinappResult> {
  // Runtime guard for JS callers who may pass undefined/null despite the TypeScript type.
  if (options === null || typeof options !== 'object') {
    throw new Error(
      `uiRecord: options must be an object with durationSec as a finite integer in [1, 86400]. ` +
        'Got: null or undefined options. Pass options.durationSec > 0.'
    );
  }
  if (
    typeof options.durationSec !== 'number' ||
    !Number.isFinite(options.durationSec) ||
    !Number.isInteger(options.durationSec) ||
    options.durationSec < 1 ||
    options.durationSec > 86400
  ) {
    throw new Error(
      `uiRecord: durationSec must be a finite integer in [1, 86400]. Got: ${options.durationSec}. ` +
        'Unbounded recording (durationSec == 0) is only supported via the CLI with Ctrl+C or piped stdin. ' +
        'Pass options.durationSec > 0.'
    );
  }
  const args = buildUiRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = {};
  if (options.cwd) captureOpts.cwd = options.cwd;
  if (options.signal) captureOpts.signal = options.signal;
  if (options.workflowId !== undefined) captureOpts.workflowId = options.workflowId;
  const result = await capture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}
