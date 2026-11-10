import assert from "node:assert/strict";
import test from "node:test";
import type { RunEntry } from "../types.js";
import { aggregateEntries, formatDurationText, parseDurationText, validOfTotal } from "./aggregate.js";

test("session time is averaged, not picked", () => {
  const secs = ["1m 0s", "9m 0s"].map((t) => parseDurationText(t)!);
  assert.equal(formatDurationText(secs.reduce((a, b) => a + b, 0) / secs.length), "5m 0s");
  assert.equal(parseDurationText("1h 2m 3s"), 3723);
  assert.equal(parseDurationText(undefined), undefined);
});

test("the average score uses valid trials only and says how many there were", () => {
  const trial = (i: number, over: Partial<RunEntry>): RunEntry => ({
    id: `t${i}`, scenario: "calc", scenarioPath: "", scenarioConfigName: "calc", condition: `cli-only [${i}/6]`, pluginPath: "",
    model: "m", trialName: `t${i}`, status: "done", currentOutput: "", builds: true, runs: true, ...over,
  });
  const [agg] = aggregateEntries([
    trial(1, { score: 90 }),
    trial(2, { score: 0, failedGate: "G0", runs: false, status: "failed" }),
    trial(3, { score: 0, builds: false, runs: false, status: "failed" }),
    trial(4, { score: 0, runs: false, status: "failed" }),
    trial(5, { score: 0, builds: false, runs: false, status: "timeout" }),
    trial(6, { harnessError: "Validation produced no score", status: "failed" }),
  ]);
  assert.equal(agg.avgScore, 45);
  assert.equal(agg.scoredTrials, 2);
  assert.equal(validOfTotal(agg), "2 valid of 6 (1 app failed, 1 build failed, 1 timed out, 1 harness error)");
});
