/**
 * Time limit for the Native AOT gate (G3). `winapp run --aot` can hang (a stuck
 * publish, or a process Windows can't end), and runProcess only resolves once the
 * process exits, so the gate gets its own deadline that does not wait for that.
 */

export const DEFAULT_AOT_GATE_TIMEOUT_MINUTES = 20;

/** Minutes from `BENCH_AOT_GATE_TIMEOUT_MINUTES`, or the default when unset or invalid. */
export function aotGateTimeoutMinutes(env: NodeJS.ProcessEnv = process.env): number {
  const value = Number(env.BENCH_AOT_GATE_TIMEOUT_MINUTES);
  return Number.isFinite(value) && value > 0 ? value : DEFAULT_AOT_GATE_TIMEOUT_MINUTES;
}

export function aotGateTimeoutMessage(minutes: number): string {
  return `Native AOT gate timed out after ${minutes} minutes`;
}

export type DeadlineResult<T> = { timedOut: false; value: T } | { timedOut: true };

/**
 * Resolves with `work`'s value, or with `{ timedOut: true }` after `ms` even if
 * `work` never settles. `onTimeout` runs once, before the timed-out result.
 */
export function withDeadline<T>(work: Promise<T>, ms: number, onTimeout: () => void): Promise<DeadlineResult<T>> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      try { onTimeout(); } catch { /* best effort */ }
      resolve({ timedOut: true });
    }, ms);
    work.then(
      (value) => { if (settled) return; settled = true; clearTimeout(timer); resolve({ timedOut: false, value }); },
      (err) => { if (settled) return; settled = true; clearTimeout(timer); reject(err); },
    );
  });
}
