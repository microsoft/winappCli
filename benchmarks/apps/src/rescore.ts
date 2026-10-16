// Re-score an existing trial by re-parsing its validation-events.jsonl.
// Use when validation completed but `results.json` shows the precheck-pass
// + run-success baseline only (because the readable-text reconstruction
// missed the agent's JSON output — fixed in eventsToReadableText).
//
// Usage: tsx src/rescore.ts --run <runN> --trial <trialFolder>

import { readFileSync, writeFileSync, existsSync, readdirSync, statSync } from "fs";
import { dirname, join } from "path";
import { resultsRoot, loadScenario, resolveScenarioPath } from "./runner/config.js";
import {
  eventsToReadableText,
  parseValidationJson,
  extractRequirementResults,
} from "./runner/benchmark.js";
import { collectProjectFiles, evaluateBuildWarnings, evaluateSourceChecks, judgeChecks, type QualityCheck } from "./runner/quality-checks.js";
import { computeScoreV2, formatBreakdown } from "./runner/scoring.js";

export interface RescoreResult {
  runName: string;
  trialName: string;
  score: number;
  builds: boolean;
  runs: boolean;
  /** Always 0 in scoring v2 (builds/runs are a gate, not points); kept for older UIs. */
  baseRuns: number;
  /** v2: quality-check + design points. */
  generalPoints: number;
  /** v2: requirement points. */
  reqPoints: number;
  /** v2 compact breakdown, e.g. "R75% Q90% D7". */
  breakdown: string;
  requirementsPassed: number;
  requirementsTotal: number;
  resultsPath: string;
}

export class RescoreError extends Error {
  constructor(public readonly code: "missing-results" | "missing-events" | "scenario-unresolved" | "no-validation-json", message: string) {
    super(message);
    this.name = "RescoreError";
  }
}

function findCsproj(dir: string, depth = 0): string | null {
  if (!existsSync(dir) || depth > 4) return null;
  for (const f of readdirSync(dir)) if (f.endsWith(".csproj")) return join(dir, f);
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (!["bin", "obj", ".git", "node_modules"].includes(f) && statSync(p).isDirectory()) {
      const r = findCsproj(p, depth + 1);
      if (r) return r;
    }
  }
  return null;
}

/**
 * Re-parse validation-events.jsonl for a finished trial and rewrite
 * `results.json` with a score computed by scoring v2. Uses the stored
 * `quality_checks` when present; for older trials it recomputes the source
 * and build checks from the app folder (UI checks are then unavailable).
 * Pure data-layer function: callers (CLI + Electron IPC) handle output.
 */
export function rescoreTrial(runName: string, trialName: string): RescoreResult {
  const trialDir = join(resultsRoot, runName, trialName);
  const resultsPath = join(trialDir, "results.json");
  const eventsFile = join(trialDir, "session-logs-dir", "validation-events.jsonl");
  const logFile = join(trialDir, "session-logs-dir", "validation-log.txt");

  if (!existsSync(resultsPath)) throw new RescoreError("missing-results", `results.json missing: ${resultsPath}`);
  if (!existsSync(eventsFile)) throw new RescoreError("missing-events", `validation-events.jsonl missing: ${eventsFile}`);

  const prior = JSON.parse(readFileSync(resultsPath, "utf-8"));
  const scenarioPath = resolveScenarioPath(prior.scenario);
  if (!scenarioPath) throw new RescoreError("scenario-unresolved", `Cannot resolve scenario: ${prior.scenario}`);
  const scenario = loadScenario(scenarioPath);
  if (!scenario) throw new RescoreError("scenario-unresolved", `Cannot load scenario: ${scenarioPath}`);
  // Score against the requirements the trial was actually judged on: scenarios
  // change over time, and the judge's numbering refers to the list at run time.
  const priorReqs: string[] | undefined = Array.isArray(prior.metrics?.requirements)
    ? prior.metrics.requirements.map((r: any) => String(r.text ?? ""))
    : undefined;
  const scenarioReqs = priorReqs?.length ? priorReqs : (scenario.config.requirements || []);

  // Regenerate readable text (now includes assistant.message.content) and parse.
  const readable = eventsToReadableText(eventsFile);
  writeFileSync(logFile, readable);
  const validation = parseValidationJson(readable);
  if (!validation) {
    throw new RescoreError(
      "no-validation-json",
      "No validation JSON found in events. (Check validation-log.txt for what the agent emitted.)"
    );
  }

  const reported = extractRequirementResults(validation, scenarioReqs);
  const byId = new Map(reported.map((r) => [r.id, r]));
  const requirements = scenarioReqs.length
    ? scenarioReqs.map((text, i) => byId.get(i + 1) ?? { id: i + 1, text, status: "fail" as const, reason: "Not reported by the judge" })
    : reported;

  let checks: QualityCheck[] = Array.isArray(prior.metrics?.quality_checks) ? prior.metrics.quality_checks : [];
  if (checks.length === 0) {
    const csproj = findCsproj(join(trialDir, "app"));
    if (csproj) {
      checks = evaluateSourceChecks(collectProjectFiles(dirname(csproj)), scenario.config.framework ?? "winui");
      const buildOut = [join(trialDir, "session-logs-dir", "build-output.txt"), join(trialDir, "build-output.txt")].find((p) => existsSync(p));
      if (buildOut) checks.push(evaluateBuildWarnings(readFileSync(buildOut, "utf-8")));
    }
  }
  checks = [...checks.filter((c) => c.source !== "judge"), ...judgeChecks(validation.quality)];

  const runs = !!prior.metrics?.runs;
  const builds = !!prior.metrics?.builds;
  const breakdown = computeScoreV2({
    builds, runs, checks, requirements,
    designScore: typeof validation.design_score === "number" ? validation.design_score : null,
  });

  prior.metrics = {
    ...prior.metrics,
    score: breakdown.score,
    builds,
    runs,
    requirements,
    score_breakdown: breakdown,
    quality_checks: checks,
  };
  delete prior.validation;

  writeFileSync(resultsPath, JSON.stringify(prior, null, 2));

  return {
    runName,
    trialName,
    score: breakdown.score,
    builds,
    runs,
    baseRuns: 0,
    generalPoints: Math.round(breakdown.points.quality + breakdown.points.design),
    reqPoints: Math.round(breakdown.points.requirements),
    breakdown: formatBreakdown(breakdown),
    requirementsPassed: breakdown.requirements.passed,
    requirementsTotal: breakdown.requirements.total,
    resultsPath,
  };
}
// ─── CLI entry ────────────────────────────────────────────────────────────
// Only runs when this module is invoked directly via `tsx src/rescore.ts ...`.
// Skipped when imported (e.g. by the Electron main process via runner-bridge).
const isCliEntry =
  typeof process !== "undefined" &&
  process.argv[1] &&
  /[\\/]rescore\.[cm]?[tj]s$/.test(process.argv[1]);

if (isCliEntry) {
  const args = process.argv.slice(2);
  const arg = (name: string): string | undefined => {
    const i = args.indexOf(name);
    return i >= 0 ? args[i + 1] : undefined;
  };
  const runName = arg("--run");
  const trialName = arg("--trial");
  if (!runName || !trialName) {
    console.error("Usage: tsx src/rescore.ts --run <runN> --trial <trialFolder>");
    process.exit(2);
  }
  try {
    const r = rescoreTrial(runName, trialName);
    console.log(`[rescore] ${r.runName}/${r.trialName}`);
    console.log(`  Score: ${r.score}/100  (${r.breakdown}; reqs ${r.reqPoints} + quality/design ${r.generalPoints})`);
    console.log(`  Builds: ${r.builds ? "yes" : "no"}   Runs: ${r.runs ? "yes" : "no"}`);
    console.log(`  Requirements: ${r.requirementsPassed}/${r.requirementsTotal} passed`);
    console.log(`  results.json updated: ${r.resultsPath}`);
  } catch (e: unknown) {
    if (e instanceof RescoreError) { console.error(e.message); process.exit(e.code === "no-validation-json" ? 1 : 2); }
    throw e;
  }
}
