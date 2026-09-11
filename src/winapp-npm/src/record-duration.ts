// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * The one rule every recording wrapper in this package has to enforce.
 *
 * A recording with no duration runs until someone stops it, and from the CLI that someone is a
 * person pressing Ctrl+C or closing redirected stdin. A programmatic caller has neither: the
 * wrapper spawns the CLI and awaits it, with no signal to send and no stdin to close, so an
 * unbounded recording never returns and never releases the capture. Requiring a duration up front
 * turns a hang into an immediate, readable error.
 *
 * Hand-maintained: `winapp-commands.ts` is generated, and every generated recording delegate is
 * kept module-internal so it cannot be reached without passing through here.
 */

/** Longest recording the CLI accepts, in seconds. */
export const MAX_RECORD_DURATION_SEC = 86400;

/**
 * Fails unless `durationSec` is a finite whole number of seconds the CLI will accept.
 *
 * @param fnName The wrapper the caller invoked, so the error names what they actually called.
 * @param options The caller's options; may be anything at runtime, since JavaScript callers are
 *   not bound by the TypeScript signature.
 * @throws {Error} if options is not an object, or `durationSec` is missing, non-numeric, NaN,
 *   infinite, fractional, less than 1, or greater than {@link MAX_RECORD_DURATION_SEC}.
 */
export function assertBoundedRecordDuration(fnName: string, options: unknown): void {
  if (options === null || typeof options !== 'object') {
    throw new Error(
      `${fnName}: options must be an object with durationSec as a finite integer in [1, ${MAX_RECORD_DURATION_SEC}]. ` +
        'Got: null or undefined options. Pass options.durationSec > 0.'
    );
  }

  const durationSec = (options as { durationSec?: unknown }).durationSec;

  if (
    typeof durationSec !== 'number' ||
    !Number.isFinite(durationSec) ||
    !Number.isInteger(durationSec) ||
    durationSec < 1 ||
    durationSec > MAX_RECORD_DURATION_SEC
  ) {
    throw new Error(
      `${fnName}: durationSec must be a finite integer in [1, ${MAX_RECORD_DURATION_SEC}]. Got: ${durationSec}. ` +
        'Unbounded recording (durationSec == 0) is only supported via the CLI with Ctrl+C or piped stdin. ' +
        'Pass options.durationSec > 0.'
    );
  }
}
