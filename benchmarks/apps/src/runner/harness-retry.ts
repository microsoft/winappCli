/**
 * Automatic rerun of trials that ended in a harness error (judge failed or
 * produced no scores, locked desktop). Only judging is redone: the agent's
 * app is reused, so the agent is never run again. The earlier attempt's
 * evidence is archived under `.attempts` by the revalidation itself.
 */
import type { RunEntry } from "../types.js";

/** Reruns after the first attempt, so a trial is judged at most 3 times. */
export const HARNESS_ERROR_RETRY_LIMIT = 2;

export interface HarnessRetryDeps {
  /** Rebuilds, relaunches and re-judges the existing app (revalidateBenchmark). */
  revalidate: () => Promise<void>;
  /** Resolves true once the desktop accepts input, false when it stayed locked. */
  waitForDesktop: () => Promise<boolean>;
  /** False once the run is stopping; no new retry starts. */
  isRunning: () => boolean;
  log: (msg: string) => void;
}

/** True when the trial has no real result because our tooling failed after the app was built. */
export function needsHarnessRetry(entry: RunEntry): boolean {
  return !!entry.harnessError && !!entry.builds;
}

/**
 * Re-judges `entry` until it no longer ends in a harness error, up to `limit` times.
 * Sets `entry.harnessRetries` before each attempt so the saved result records it. Returns the number of retries.
 */
export async function retryHarnessErrors(entry: RunEntry, deps: HarnessRetryDeps, limit = HARNESS_ERROR_RETRY_LIMIT): Promise<number> {
  let retries = 0;
  while (needsHarnessRetry(entry) && retries < limit && deps.isRunning()) {
    if (!(await deps.waitForDesktop())) {
      deps.log(`  ⏹️  Not retrying "${entry.harnessError}": the desktop is still locked.`);
      break;
    }
    retries++;
    deps.log(`\n🔁 Harness error "${entry.harnessError}". Re-judging the existing app (retry ${retries} of ${limit}).\n`);
    entry.harnessRetries = retries;
    entry.harnessError = undefined;
    entry.failReason = undefined;
    entry.score = undefined;
    entry.validationSessionId = undefined;
    delete (entry as any)._validationData;
    try {
      await deps.revalidate();
    } catch (err) {
      // revalidateBenchmark has already saved a failed result; keep a harness error so the retry loop continues.
      const detail = err instanceof Error ? err.message : String(err);
      entry.harnessError ??= `Revalidation failed: ${detail}`;
      deps.log(`  Retry ${retries} failed: ${detail}`);
    }
  }
  return retries;
}
