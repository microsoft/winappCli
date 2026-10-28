/**
 * Maps one finished apps trial to the trial file described in benchmarks/RESULTS-FORMAT.md.
 *
 * There is no single score. Gates (builds, launches, right framework, Native AOT) are
 * required checks: if one fails, the trial fails. Quality checks, judge-checked
 * requirements and the design score are improvements, unless the scenario lists a
 * requirement in `required_requirements`.
 */
import type { QualityCheck } from "./quality-checks.js";

export const ARMS = ["none", "cli-only", "cli-plus-plugins"] as const;

export interface ExactUsage {
  tokens: { input: number; output: number; cacheRead: number };
  subAgentTokens: { input: number; output: number; cacheRead: number } | null;
  nanoAiu: number | null;
  premiumRequests: number | null;
  wallClockMs: number;
}

export interface TrialResultInput {
  release: string;
  commit: string;
  scenario: string;
  set?: "tuning" | "held-out";
  /** Agent setup name; a " [1/2]" suffix is ignored. */
  condition: string;
  model: string;
  iteration?: number;
  startedAt: Date;
  builds: boolean;
  runs: boolean;
  /** Harness and judge checks, including gates. */
  checks: QualityCheck[];
  requirements: Array<{ id: number; status: string; reason?: string }>;
  /** 1-based ids of requirements the scenario marks as required. */
  requiredRequirements?: number[];
  designScore: number | null;
  judgeModel?: string;
  usage: ExactUsage;
  failReason?: string;
}

interface TrialCheck {
  id: string;
  kind: "required" | "improvement";
  passed: boolean;
  source: "harness" | "judge";
  detail?: string;
}

const slug = (s: string) => s.toLowerCase().replace(/[^a-z0-9.]+/g, "-").replace(/^-+|-+$/g, "");

export function toTrialResult(input: TrialResultInput): Record<string, unknown> | null {
  const arm = input.condition.replace(/\s*\[\d+\/\d+\]$/, "");
  if (!(ARMS as readonly string[]).includes(arm)) return null;

  const checks: TrialCheck[] = [
    { id: "builds", kind: "required", passed: input.builds, source: "harness" },
    { id: "launches", kind: "required", passed: input.runs, source: "harness" },
  ];
  for (const c of input.checks) {
    const id = slug(c.id);
    if (c.status === "na" || checks.some((x) => x.id === id)) continue;
    checks.push({
      id,
      kind: c.kind === "gate" ? "required" : "improvement",
      passed: c.status === "pass",
      source: c.source,
      ...(c.detail ? { detail: c.detail } : {}),
    });
  }
  const required = new Set(input.requiredRequirements ?? []);
  for (const r of input.requirements) {
    checks.push({
      id: `requirement-${r.id}`,
      kind: required.has(r.id) ? "required" : "improvement",
      passed: r.status === "pass",
      source: "judge",
      ...(r.reason ? { detail: r.reason } : {}),
    });
  }

  const improvements = checks.filter((c) => c.kind === "improvement");
  return {
    schemaVersion: 1,
    release: input.release,
    commit: input.commit,
    benchmark: "apps",
    scenario: slug(input.scenario),
    set: input.set ?? "tuning",
    arm,
    model: slug(input.model),
    trial: input.iteration ?? 1,
    startedAt: input.startedAt.toISOString().replace(/\.\d{3}Z$/, "Z"),
    passed: checks.every((c) => c.kind !== "required" || c.passed),
    improvements: { met: improvements.filter((c) => c.passed).length, total: improvements.length },
    checks,
    ...(input.judgeModel ? { judge: { model: input.judgeModel, designScore: input.designScore } } : {}),
    usage: input.usage,
  };
}

/** `<scenario>__<arm>__<model>__<trial>.json`, matching the folder layout on the benchmark-data branch. */
export function trialFileName(t: { scenario: string; arm: string; model: string; trial: number }): string {
  return `${t.scenario}__${t.arm}__${t.model}__${t.trial}.json`;
}
