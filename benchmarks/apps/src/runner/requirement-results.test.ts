import assert from "node:assert/strict";
import test from "node:test";
import { extractRequirementResults } from "./benchmark.js";
import { incompleteJudging } from "./scoring.js";

test("a judge status other than pass or not-run counts as a fail", () => {
  const r = extractRequirementResults({ requirements: { "1": { status: "PASS" }, "2": { status: "partial" }, "3": { status: "pass" } } }, ["a", "b", "c"]);
  assert.deepEqual(r.map((x) => x.status), ["fail", "fail", "pass"]);
});

test("skipping a required requirement or every requirement is incomplete judging, not a score", () => {
  const skipped2 = [{ id: 1, status: "pass" }, { id: 2, status: "not-run" }];
  assert.match(incompleteJudging(skipped2, [2]) ?? "", /required requirement 2/);
  assert.equal(incompleteJudging(skipped2, []), undefined);
  assert.equal(incompleteJudging([{ id: 1, status: "not-run" }]), "Validation ruled on no requirements");
  assert.equal(incompleteJudging([{ id: 1, status: "fail" }, { id: 2, status: "pass" }], [2]), undefined);
});
