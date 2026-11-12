import assert from "node:assert/strict";
import test from "node:test";
import { extractRequirementResults } from "./benchmark.js";

test("a judge status other than pass or not-run counts as a fail", () => {
  const r = extractRequirementResults({ requirements: { "1": { status: "PASS" }, "2": { status: "partial" }, "3": { status: "pass" } } }, ["a", "b", "c"]);
  assert.deepEqual(r.map((x) => x.status), ["fail", "fail", "pass"]);
});
