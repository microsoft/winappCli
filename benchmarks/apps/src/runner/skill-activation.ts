/**
 * Skill activation: for each scenario that names the one skill it targets, how many runs of
 * each skills setup loaded that skill. A skill that loads in fewer than half of its target
 * runs gets no value claim, because its score then measures the agent without the skill.
 * Mirrors Get-SkillActivation in benchmarks/tasks/lib/TaskBench.psm1.
 */

/** Setups that install no plugins; their runs can't load a skill and are left out. */
export const NO_PLUGIN_ARMS = new Set(["barebones", "cli-only"]);

export interface ActivationTrial {
  scenario: string;
  arm: string;
  targetSkill?: string | null;
  /** Skills the agent loaded; null when the event stream was unavailable (the trial is left out). */
  skills: string[] | null;
  outcome?: string;
}

export interface ActivationRow { scenario: string; arm: string; skill: string; runs: number; loaded: number }
export interface ActivationVerdict { skill: string; arm: string; scenarios: string[]; runs: number; loaded: number; activationFailed: boolean }

export function skillActivation(trials: ActivationTrial[]): ActivationRow[] {
  const rows = new Map<string, ActivationRow>();
  for (const t of trials) {
    if (!t.targetSkill || t.skills === null || t.outcome === "harness-error") continue;
    if (NO_PLUGIN_ARMS.has(t.arm.split("@")[0])) continue;
    const key = `${t.scenario}|${t.arm}`;
    const row = rows.get(key) ?? { scenario: t.scenario, arm: t.arm, skill: t.targetSkill, runs: 0, loaded: 0 };
    row.runs++;
    if (t.skills.includes(t.targetSkill)) row.loaded++;
    rows.set(key, row);
  }
  return [...rows.values()].sort((a, b) => a.scenario.localeCompare(b.scenario) || a.arm.localeCompare(b.arm));
}

export function activationVerdicts(rows: ActivationRow[]): ActivationVerdict[] {
  const out = new Map<string, ActivationVerdict>();
  for (const r of rows) {
    const key = `${r.skill}|${r.arm}`;
    const v = out.get(key) ?? { skill: r.skill, arm: r.arm, scenarios: [], runs: 0, loaded: 0, activationFailed: false };
    v.scenarios.push(r.scenario);
    v.runs += r.runs;
    v.loaded += r.loaded;
    v.activationFailed = v.runs > 0 && 2 * v.loaded < v.runs;
    out.set(key, v);
  }
  return [...out.values()].sort((a, b) => a.skill.localeCompare(b.skill) || a.arm.localeCompare(b.arm));
}

export const loadedText = (loaded: number, runs: number) => `loaded in ${loaded} of ${runs} runs`;

/** Markdown for the scorecard; empty when no scenario names a target skill. */
export function activationMarkdown(trials: ActivationTrial[]): string[] {
  const rows = skillActivation(trials);
  if (!rows.length) return [];
  const lines = [
    "## Skill activation",
    "",
    "Scenarios that name a target skill. A skill that loads in fewer than half of its target runs gets no value claim: its score measures the agent without the skill.",
    "",
    "| Skill | Setup | Target scenarios | Loaded | Verdict |",
    "|---|---|---|---|---|",
    ...activationVerdicts(rows).map((v) =>
      `| ${v.skill} | ${v.arm} | ${v.scenarios.join(", ")} | ${loadedText(v.loaded, v.runs)} | ${v.activationFailed ? "activation failed: no value claim" : "activated"} |`),
    "",
    "| Scenario | Setup | Target skill | Loaded |",
    "|---|---|---|---|",
    ...rows.map((r) => `| ${r.scenario} | ${r.arm} | ${r.skill} | ${loadedText(r.loaded, r.runs)} |`),
    "",
  ];
  return lines;
}
