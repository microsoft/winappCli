// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { callWinappCliCapture } from './winapp-cli-utils';
import type { CallWinappCliCaptureOptions, CallWinappCliCaptureResult } from './winapp-cli-utils';
import type { UiRecordActionsOptions as GeneratedOptions, WinappResult } from './winapp-commands';

/** Captured npm calls cannot send a stop signal, so action recording must be time-bounded. */
export type UiRecordActionsOptions = Omit<GeneratedOptions, 'durationSec'> & {
  durationSec: number;
};

/** @internal Build a bounded recording invocation without spawning or capturing user input. */
export function buildUiRecordActionsArgs(options: UiRecordActionsOptions): string[] {
  if (
    !options ||
    typeof options !== 'object' ||
    !Number.isInteger(options.durationSec) ||
    options.durationSec < 1 ||
    options.durationSec > 3600
  ) {
    throw new Error(
      'uiRecordActions requires durationSec as an integer in [1, 3600]. ' +
        'Use the CLI for recordings stopped by Ctrl+C or stdin.'
    );
  }
  const args = ['ui', 'record-actions', '--duration-sec', String(options.durationSec)];
  if (options.app) args.push('--app', options.app);
  if (options.window !== undefined) args.push('--window', String(options.window));
  if (options.output) args.push('--output', options.output);
  if (options.maxSteps !== undefined) args.push('--max-steps', String(options.maxSteps));
  if (options.captureText) args.push('--capture-text');
  if (options.includeInjected) args.push('--include-injected');
  if (options.json) args.push('--json');
  if (options.quiet) args.push('--quiet');
  if (options.verbose) args.push('--verbose');
  return args;
}

/** Record reviewed UI action data for a fixed duration. Text is parameterized by default. */
export async function uiRecordActions(options: UiRecordActionsOptions): Promise<WinappResult> {
  return _uiRecordActionsWithCapture(options, callWinappCliCapture);
}

/** @internal Injectable process boundary used by tests; validates the same public options. */
export async function _uiRecordActionsWithCapture(
  options: UiRecordActionsOptions,
  capture: (args: string[], opts: CallWinappCliCaptureOptions) => Promise<CallWinappCliCaptureResult>
): Promise<WinappResult> {
  const args = buildUiRecordActionsArgs(options);
  const result = await capture(args, options.cwd ? { cwd: options.cwd } : {});
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}
