import assert from "node:assert/strict";
import test from "node:test";
import { aotGateTimeoutMessage, aotGateTimeoutMinutes, DEFAULT_AOT_GATE_TIMEOUT_MINUTES, withDeadline } from "./aot-gate.js";

test("AOT gate timeout defaults to 20 minutes and reads BENCH_AOT_GATE_TIMEOUT_MINUTES", () => {
  assert.equal(DEFAULT_AOT_GATE_TIMEOUT_MINUTES, 20);
  assert.equal(aotGateTimeoutMinutes({}), 20);
  assert.equal(aotGateTimeoutMinutes({ BENCH_AOT_GATE_TIMEOUT_MINUTES: "45" }), 45);
  for (const bad of ["0", "-5", "abc", ""]) assert.equal(aotGateTimeoutMinutes({ BENCH_AOT_GATE_TIMEOUT_MINUTES: bad }), 20);
});

test("timeout message names the limit", () => {
  assert.equal(aotGateTimeoutMessage(20), "Native AOT gate timed out after 20 minutes");
});

test("a gate that never finishes times out and runs the kill callback once", async () => {
  let kills = 0;
  const r = await withDeadline(new Promise<string>(() => {}), 20, () => { kills++; });
  assert.deepEqual(r, { timedOut: true });
  assert.equal(kills, 1);
});

test("a gate that finishes in time returns its result and never kills", async () => {
  let kills = 0;
  const r = await withDeadline(Promise.resolve("ok"), 1000, () => { kills++; });
  assert.deepEqual(r, { timedOut: false, value: "ok" });
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(kills, 0);
});

test("a gate that throws in time rejects instead of timing out", async () => {
  await assert.rejects(withDeadline(Promise.reject(new Error("boom")), 1000, () => {}), /boom/);
});
