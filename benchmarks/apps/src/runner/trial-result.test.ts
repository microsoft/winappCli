import assert from "node:assert/strict";
import test from "node:test";
import { toTrialResult, trialFileName, type TrialResultInput } from "./trial-result.js";

const base: TrialResultInput = {
  release: "dev",
  commit: "a".repeat(40),
  scenario: "Calc",
  condition: "cli-plus-plugins [1/2]",
  model: "claude-sonnet-5.5",
  iteration: 1,
  startedAt: new Date("2026-10-27T18:00:00.123Z"),
  builds: true,
  runs: true,
  checks: [
    { id: "G0-framework", label: "Framework", kind: "gate", source: "harness", status: "pass" },
    { id: "Has-Icon", label: "Icon", kind: "quality", source: "harness", status: "fail" },
    { id: "no-na", label: "n/a", kind: "quality", source: "harness", status: "na" },
  ],
  requirements: [{ id: 1, status: "pass" }, { id: 2, status: "fail", reason: "missing" }],
  designScore: 7,
  judgeModel: "judge-1",
  usage: { tokens: { input: 10, output: 5, cacheRead: 7 }, subAgentTokens: null, nanoAiu: 1500000000, premiumRequests: 1, wallClockMs: 1000 },
};

test("gates are required; quality checks and requirements are improvements", () => {
  const t = toTrialResult(base) as any;
  assert.equal(t.arm, "cli-plus-plugins");
  assert.equal(t.scenario, "calc");
  assert.equal(t.startedAt, "2026-10-27T18:00:00Z");
  assert.equal(t.passed, true);
  assert.deepEqual(t.improvements, { met: 1, total: 3 });
  assert.deepEqual(t.checks.map((c: any) => `${c.id}:${c.kind}`), [
    "builds:required", "launches:required", "g0-framework:required", "has-icon:improvement", "requirement-1:improvement", "requirement-2:improvement",
  ]);
  assert.deepEqual(t.judge, { model: "judge-1", designScore: 7 });
});

test("a failed gate fails the trial; a failed improvement does not", () => {
  assert.equal((toTrialResult({ ...base, builds: false }) as any).passed, false);
  assert.equal((toTrialResult(base) as any).passed, true);
});

test("a scenario can mark a requirement as required", () => {
  const t = toTrialResult({ ...base, requiredRequirements: [2] }) as any;
  assert.equal(t.passed, false);
});

test("agent setups that are not one of the three arms are skipped", () => {
  assert.equal(toTrialResult({ ...base, condition: "published" }), null);
});

test("file name follows the benchmark-data layout", () => {
  const t = toTrialResult(base) as any;
  assert.equal(trialFileName(t), "calc__cli-plus-plugins__claude-sonnet-5.5__1.json");
});
