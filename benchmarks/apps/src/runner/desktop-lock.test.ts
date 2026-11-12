import assert from "node:assert/strict";
import test from "node:test";
import { evaluateLoaded } from "./quality-checks.js";
import { LockedDesktopStreak, uiBlockedByLockedDesktop, waitForInteractiveDesktop } from "./desktop-lock.js";

const noSleep = async () => {};

test("waiting returns at once when the desktop is free", async () => {
  let calls = 0;
  assert.equal(await waitForInteractiveDesktop(() => { calls++; return true; }, { sleep: noSleep }), true);
  assert.equal(calls, 1);
});

test("waiting continues once the desktop unlocks", async () => {
  const answers = [false, false, true];
  assert.equal(await waitForInteractiveDesktop(() => answers.shift()!, { timeoutMs: 60_000, intervalMs: 10_000, sleep: noSleep }), true);
});

test("waiting gives up after the time limit", async () => {
  let calls = 0;
  assert.equal(await waitForInteractiveDesktop(() => { calls++; return false; }, { timeoutMs: 30_000, intervalMs: 10_000, sleep: noSleep }), false);
  assert.equal(calls, 4);
});

test("L2 not measured because of a lock blocks the trial", () => {
  assert.equal(uiBlockedByLockedDesktop([evaluateLoaded(3, null, true)], true), true);
  // Input refused for an unknown reason: blocked only if the desktop is locked now.
  assert.equal(uiBlockedByLockedDesktop([evaluateLoaded(3, null)], false), true);
  assert.equal(uiBlockedByLockedDesktop([evaluateLoaded(3, null)], true), false);
  assert.equal(uiBlockedByLockedDesktop([evaluateLoaded(3, [{ hasFocus: true }])], false), false);
  assert.equal(uiBlockedByLockedDesktop([], false), false);
});

test("the run stops after three locked trials in a row", () => {
  const streak = new LockedDesktopStreak(3);
  assert.equal(streak.record(true), false);
  assert.equal(streak.record(undefined), false); // a build failure says nothing about the desktop
  assert.equal(streak.record(true), false);
  assert.equal(streak.record(false), false); // a trial that drove the UI resets the count
  assert.equal(streak.record(true), false);
  assert.equal(streak.record(true), false);
  assert.equal(streak.record(true), true);
});
