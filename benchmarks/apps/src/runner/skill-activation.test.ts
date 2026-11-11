import assert from "node:assert/strict";
import test from "node:test";
import { activationMarkdown, activationVerdicts, skillActivation, type ActivationTrial } from "./skill-activation.js";

const trial = (scenario: string, arm: string, skills: string[] | null, targetSkill: string | null = "winapp-find-api", outcome = "valid"): ActivationTrial =>
  ({ scenario, arm, skills, targetSkill, outcome });

test("counts target-skill loads per scenario and setup, leaving out runs that can't load it", () => {
  const rows = skillActivation([
    trial("ai-journal", "cli-plus-skills", ["winui-dev-workflow", "winapp-find-api"]),
    trial("ai-journal", "cli-plus-skills", ["winui-dev-workflow"]),
    trial("ai-journal", "cli-plus-skills", []),
    trial("ai-journal", "cli-plus-skills", ["winapp-find-api"], "winapp-find-api", "harness-error"),
    trial("ai-journal", "cli-plus-skills", null),
    trial("ai-journal", "cli-only", []),
    trial("calc", "cli-plus-skills", ["winui-dev-workflow"], null),
  ]);
  assert.deepEqual(rows, [{ scenario: "ai-journal", arm: "cli-plus-skills", skill: "winapp-find-api", runs: 3, loaded: 1 }]);
  const [v] = activationVerdicts(rows);
  assert.equal(v.activationFailed, true);
});

test("a skill that loads in at least half of its target runs is activated", () => {
  const rows = skillActivation([
    trial("a", "cli-plus-skills", ["winapp-find-api"]),
    trial("b", "cli-plus-skills", []),
  ]);
  const [v] = activationVerdicts(rows);
  assert.deepEqual([v.scenarios, v.loaded, v.runs, v.activationFailed], [["a", "b"], 1, 2, false]);
  const md = activationMarkdown([trial("a", "cli-plus-skills", ["winapp-find-api"]), trial("b", "cli-plus-skills", [])]).join("\n");
  assert.match(md, /\| winapp-find-api \| cli-plus-skills \| a, b \| loaded in 1 of 2 runs \| activated \|/);
});

test("no section when no scenario names a target skill", () => {
  assert.deepEqual(activationMarkdown([trial("calc", "cli-plus-skills", [], null)]), []);
});