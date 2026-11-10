import assert from "node:assert/strict";
import test from "node:test";
import { extractRequirementResults } from "./benchmark.js";
import { loadValidationPrompt } from "./config.js";
import { computeScoreV2 } from "./scoring.js";

test("judge prompt tells the judge to report an unverifiable requirement as not-run, not fail", () => {
  const prompt = loadValidationPrompt("winui");
  assert.match(prompt, /`status` in `requirements` is exactly `"pass"`, `"fail"`, or `"not-run"`/);
  assert.match(prompt, /Use `"fail"` only when you observed the requirement not met or the source clearly lacks it/);
  assert.match(prompt, /"3": \{ "status": "not-run"/);
});

test("a not-run verdict keeps the judge's reason and does not lower the requirement score", () => {
  const reqs = extractRequirementResults(
    {
      requirements: {
        "1": { status: "pass" },
        "2": { status: "not-run", reason: "Real-input steps failed with 'No interactive desktop is available'" },
        "3": { status: "fail", reason: "Delete button missing from the source and the UI tree" },
      },
    },
    ["Add photos", "Drag to reorder", "Delete a photo"],
  );
  assert.deepEqual(reqs.map((r) => r.status), ["pass", "not-run", "fail"]);
  assert.equal(reqs[1].reason, "Real-input steps failed with 'No interactive desktop is available'");

  const score = computeScoreV2({ builds: true, runs: true, checks: [], requirements: reqs, designScore: null });
  assert.equal(score.requirements.notRun, 1);
  assert.equal(score.requirements.pct, 0.5);
});
