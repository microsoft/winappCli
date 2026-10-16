import assert from "node:assert/strict";
import test from "node:test";
import { keepAwakeScript, systemSuspendedBetween } from "./suspend-guard.js";

test("keep-awake script uses a decimal UInt32 flag and watches the owner PID", () => {
  const s = keepAwakeScript(4242);
  assert.match(s, /SetThreadExecutionState\(\[uint32\]2147483651\)/);
  assert.doesNotMatch(s, /0x80000003/);
  assert.match(s, /Get-Process -Id 4242/);
});

test("no suspend is reported for an empty range", () => {
  const now = new Date();
  assert.equal(systemSuspendedBetween(now, now), null);
});