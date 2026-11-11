/**
 * Maps one finished apps trial to the trial file described in benchmarks/RESULTS-FORMAT.md.
 *
 * There is no single score. Gates (builds, launches, right framework, Native AOT) are
 * required checks: if one fails, the trial fails. Quality checks, judge-checked
 * requirements and the design score are improvements, unless the scenario lists a
 * requirement in `required_requirements`.
 */
import type { Activity } from "./activity.js";
import type { QualityCheck } from "./quality-checks.js";
import { SCORING_VERSION } from "./scoring.js";

/**
 * 0 = no window, 1 = window, 2 = loaded, 3 = ready. Level 3 is only reachable when the scenario names a ready element.
 * Undefined when the app ran but level 2 was not measured (UI input could not be sent), so a window alone is not reported as level 1.
 */
function launchLevel(runs: boolean, checks: QualityCheck[]): number | undefined {
  if (!runs) return 0;
  const status = (id: string) => checks.find((c) => c.id === id)?.status;
  const l2 = status("L2");
  if (l2 === undefined || l2 === "na") return undefined;
  return l2 === "pass" ? (status("L3") === "pass" ? 3 : 2) : 1;
}

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
  /** `winapp --version` output of the CLI the trial used, e.g. `0.7.1+<sha>`. */
  winappVersion?: string;
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
  /** Skills loaded and winapp commands run; omitted when the event stream is unavailable. */
  activity?: Activity | null;
  failReason?: string;
  /** Our tooling failed (judge never produced a result, locked desktop). Written as outcome `harness-error`. */
  harnessError?: string;
  /** The agent hit its time limit. Written as outcome `timed-out`. */
  timedOut?: boolean;
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
  const arm = input.condition.replace(/\s*\[\d+\/\d+\]$/, "").replace(/@.+$/, "");
  if (!(ARMS as readonly string[]).includes(arm)) return null;

  // A failed gate (wrong framework, Native AOT) stops the run before launch. That is a valid failing
  // trial, not an app that could not be measured.
  const gateFailed = input.checks.some((c) => c.kind === "gate" && c.status === "fail");
  const checks: TrialCheck[] = [{ id: "builds", kind: "required", passed: input.builds, source: "harness" }];
  if (input.runs || !gateFailed) checks.push({ id: "launches", kind: "required", passed: input.runs, source: "harness" });
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
  const notRun: number[] = [];
  for (const r of input.requirements) {
    if (r.status !== "pass" && r.status !== "fail") { notRun.push(r.id); continue; }
    checks.push({
      id: `requirement-${r.id}`,
      kind: required.has(r.id) ? "required" : "improvement",
      passed: r.status === "pass",
      source: "judge",
      ...(r.reason ? { detail: r.reason } : {}),
    });
  }

  // A required requirement that was never judged (for example with --skip-validation) is not a pass.
  for (const id of required) {
    if (input.requirements.some((r) => r.id === id)) continue;
    checks.push({ id: `requirement-${id}`, kind: "required", passed: false, source: "judge", detail: "not checked" });
  }

  // A required requirement the judge did not rule on can't be a pass or an app failure, so the judging is incomplete.
  const unjudged = notRun.filter((id) => required.has(id));
  const harnessError = input.harnessError ?? (unjudged.length ? `Judge did not rule on required requirement ${unjudged.join(", ")}` : undefined);
  const improvements = checks.filter((c) => c.kind === "improvement");
  const outcome = harnessError ? "harness-error"
    : input.timedOut ? "timed-out"
    : !input.builds ? "build-failed"
    : !input.runs && !gateFailed ? "app-failed"
    : "valid";
  const level = launchLevel(input.runs, input.checks);
  return {
    schemaVersion: 1,
    release: input.release,
    commit: input.commit,
    ...(input.winappVersion ? { winappVersion: input.winappVersion } : {}),
    benchmark: "apps",
    scenario: slug(input.scenario),
    set: input.set ?? "tuning",
    arm,
    model: slug(input.model),
    trial: input.iteration ?? 1,
    startedAt: input.startedAt.toISOString().replace(/\.\d{3}Z$/, "Z"),
    outcome,
    passed: outcome === "valid" && checks.every((c) => c.kind !== "required" || c.passed),
    ...(harnessError ? { error: harnessError } : {}),
    improvements: { met: improvements.filter((c) => c.passed).length, total: improvements.length },
    checks,
    scoringVersion: SCORING_VERSION,
    ...(level !== undefined ? { launchLevel: level } : {}),
    ...(notRun.length ? { requirementsNotRun: notRun } : {}),
    ...(input.judgeModel ? { judge: { model: input.judgeModel, designScore: input.designScore } } : {}),
    ...(input.activity ? { skills: input.activity.skills, winappCommands: input.activity.winappCommands } : {}),
    usage: input.usage,
  };
}

/** `<scenario>__<arm>__<model>__<trial>.json`, matching the folder layout on the benchmark-data branch. */
export function trialFileName(t: { scenario: string; arm: string; model: string; trial: number }): string {
  return `${t.scenario}__${t.arm}__${t.model}__${t.trial}.json`;
}
