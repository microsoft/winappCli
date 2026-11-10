import assert from "node:assert/strict";
import test from "node:test";
import type { RunEntry } from "../types.js";
import { HARNESS_ERROR_RETRY_LIMIT, retryHarnessErrors } from "./harness-retry.js";
import { toTrialResult, type TrialResultInput } from "./trial-result.js";

const entry = (over: Partial<RunEntry> = {}): RunEntry => ({
  id: "t", scenario: "s", scenarioPath: "", scenarioConfigName: "s", condition: "cli-only", pluginPath: "",
  model: "m", trialName: "t", status: "failed", currentOutput: "", builds: true, runs: true,
  harnessError: "Validation produced no score", ...over,
});

const deps = (onRevalidate: (n: number) => void, desktop = true) => {
  let calls = 0;
  return {
    calls: () => calls,
    deps: {
      revalidate: async () => { calls++; onRevalidate(calls); },
      waitForDesktop: async () => desktop,
      isRunning: () => true,
      log: () => {},
    },
  };
};

test("a judge failure is re-judged once and the retry is recorded when the second attempt succeeds", async () => {
  const e = entry();
  const d = deps(() => { e.score = 80; });
  assert.equal(await retryHarnessErrors(e, d.deps), 1);
  assert.equal(d.calls(), 1);
  assert.equal(e.harnessError, undefined);
  assert.equal(e.harnessRetries, 1);
  assert.equal(e.score, 80);
});

test("re-judging stops after the retry limit and keeps the harness error", async () => {
  const e = entry();
  const d = deps(() => { e.harnessError = "Validation produced no score"; });
  assert.equal(await retryHarnessErrors(e, d.deps), HARNESS_ERROR_RETRY_LIMIT);
  assert.equal(d.calls(), 2);
  assert.equal(e.harnessError, "Validation produced no score");
  assert.equal(e.harnessRetries, 2);
});

test("a revalidation that throws counts as another harness error", async () => {
  const e = entry();
  let n = 0;
  await retryHarnessErrors(e, { ...deps(() => {}).deps, revalidate: async () => { if (++n === 1) throw new Error("boom"); } });
  assert.equal(n, 2);
  assert.equal(e.harnessError, undefined);
  assert.equal(e.harnessRetries, 2);
});

test("no retry while the desktop stays locked, and none for agent failures", async () => {
  const locked = entry({ harnessError: "Desktop locked: UI could not be driven" });
  const d = deps(() => {}, false);
  assert.equal(await retryHarnessErrors(locked, d.deps), 0);
  assert.equal(d.calls(), 0);
  assert.equal(locked.harnessRetries, undefined);

  const buildFailed = entry({ harnessError: undefined, builds: false });
  const d2 = deps(() => {});
  assert.equal(await retryHarnessErrors(buildFailed, d2.deps), 0);
  assert.equal(d2.calls(), 0);
});

test("the trial file records how many times judging was retried", () => {
  const input: TrialResultInput = {
    release: "dev", commit: "a".repeat(40), scenario: "calc", condition: "cli-only", model: "m",
    startedAt: new Date("2026-01-01T00:00:00Z"), builds: true, runs: true, checks: [], requirements: [],
    designScore: null, usage: { tokens: { input: 1, output: 1, cacheRead: 0 }, subAgentTokens: null, nanoAiu: null, premiumRequests: null, wallClockMs: 1 },
  };
  assert.equal(toTrialResult({ ...input, retries: 2 })?.retries, 2);
  assert.equal("retries" in (toTrialResult(input) ?? {}), false);
});
