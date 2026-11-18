import assert from "node:assert/strict";
import test from "node:test";
import {
  agentNameCandidatesFromCondition,
  makeTrialName,
  sanitizeRefForId,
} from "./format.js";

test("sanitizeRefForId preserves short safe identifiers", () => {
  assert.equal(sanitizeRefForId("feat/x"), "feat--x");
  assert.equal(sanitizeRefForId("release@26"), "release_26");
});

test("sanitizeRefForId names pull request refs prN", () => {
  assert.equal(sanitizeRefForId("pull/1108/head"), "pr1108");
  assert.equal(sanitizeRefForId("refs/pull/7/merge"), "pr7");
});

test("sanitizeRefForId bounds and differentiates long identifiers", () => {
  const first = sanitizeRefForId("feature/very-long-branch-name");
  const second = sanitizeRefForId("feature/another-long-branch-name");

  assert.equal(first.length, 12);
  assert.equal(second.length, 12);
  assert.match(first, /^feat[0-9a-f]{8}$/);
  assert.notEqual(first, second);
});

test("makeTrialName uses the bounded ref identifier", () => {
  assert.match(
    makeTrialName("scenario", "agent", "claude-sonnet-4.6", 2, "feature/very-long-branch-name"),
    /^scenario_agent-feat[0-9a-f]{8}_s46_i2$/,
  );
});

test("agentNameCandidatesFromCondition handles ref and iteration labels", () => {
  assert.deepEqual(agentNameCandidatesFromCondition("custom-dev"), ["custom-dev"]);
  assert.deepEqual(agentNameCandidatesFromCondition("custom-dev [1/3]"), ["custom-dev"]);
  assert.deepEqual(
    agentNameCandidatesFromCondition("custom-dev@feature/x [2/3]"),
    ["custom-dev@feature/x", "custom-dev"],
  );
});
