// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Hand-written guard wrapper for `targetRecord`.
 *
 * `winapp-commands.ts` is AUTO-GENERATED. The raw generated delegate for `target record` is
 * intentionally NOT exported (underscore-prefixed, module-internal) so it cannot bypass this
 * guard, exactly as `ui record` does — recording an execution target's desktop has the same
 * problem as recording a window: a programmatic caller has no way to stop an unbounded run.
 *
 * This file must NOT be edited by the code generator; it is hand-maintained.
 */

import { callWinappCliCapture } from './winapp-cli-utils';
import type { CallWinappCliCaptureOptions, CallWinappCliCaptureResult } from './winapp-cli-utils';
import type { TargetRecordOptions as GeneratedTargetRecordOptions, WinappResult } from './winapp-commands';
import { assertBoundedRecordDuration } from './record-duration';

/**
 * Stricter version of the generated `TargetRecordOptions` where `durationSec` is **required**.
 * This type is the public surface of `targetRecord`; the generated type has it optional.
 * Survives regeneration because it is defined here in the hand-written guard module.
 */
export type TargetRecordOptions = Omit<GeneratedTargetRecordOptions, 'durationSec'> & {
  durationSec: number;
};

type TargetRecordArgSpec = {
  property: keyof TargetRecordOptions;
  flag: string;
  kind: 'boolean' | 'value';
};

export const TARGET_RECORD_ARG_SPECS: readonly TargetRecordArgSpec[] = [
  { property: 'durationSec', flag: '--duration-sec', kind: 'value' },
  { property: 'fps', flag: '--fps', kind: 'value' },
  { property: 'frames', flag: '--frames', kind: 'boolean' },
  { property: 'json', flag: '--json', kind: 'boolean' },
  { property: 'maxEdge', flag: '--max-edge', kind: 'value' },
  { property: 'output', flag: '--output', kind: 'value' },
  { property: 'quiet', flag: '--quiet', kind: 'boolean' },
  { property: 'verbose', flag: '--verbose', kind: 'boolean' },
] as const;

/**
 * Builds the CLI argument list for `target record` from a validated options object.
 * The target name goes after a `--` terminator, matching the generated wrappers, so a value that
 * happens to start with a dash is never mistaken for an option.
 *
 * Exported for unit testing — do not use externally.
 * @internal
 */
export function buildTargetRecordArgs(options: TargetRecordOptions): string[] {
  const args: string[] = ['target', 'record'];
  for (const spec of TARGET_RECORD_ARG_SPECS) {
    const value = options[spec.property];
    if (spec.kind === 'boolean') {
      if (value) args.push(spec.flag);
    } else if (value !== undefined && value !== '') {
      args.push(spec.flag, value.toString());
    }
  }
  args.push('--', options.target);
  return args;
}

/**
 * Record an execution target's entire desktop to an H.264 MP4 on this machine.
 *
 * **`durationSec` is required and must be > 0.** Unbounded recording (`durationSec == 0`) is only
 * supported from the CLI, where Ctrl+C or closing redirected stdin ends it; this wrapper has no
 * way to stop the spawned process, so an unbounded call would never return.
 * Set `frames` to write timestamped JPEG evidence beside the MP4.
 *
 * @throws {Error} if `options.durationSec` is missing or is not a finite integer in [1, 86400].
 */
export async function targetRecord(options: TargetRecordOptions): Promise<WinappResult> {
  assertBoundedRecordDuration('targetRecord', options);

  const args = buildTargetRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = options.cwd ? { cwd: options.cwd } : {};
  const result = await callWinappCliCapture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Internal implementation that accepts an injectable capture function — used by tests
 * to verify the full success path without spawning a real process.
 * @internal
 */
export async function _targetRecordWithCapture(
  options: TargetRecordOptions,
  capture: (args: string[], opts: CallWinappCliCaptureOptions) => Promise<CallWinappCliCaptureResult>
): Promise<WinappResult> {
  assertBoundedRecordDuration('targetRecord', options);
  const args = buildTargetRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = options.cwd ? { cwd: options.cwd } : {};
  const result = await capture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}
