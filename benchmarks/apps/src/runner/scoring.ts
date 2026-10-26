/**
 * Benchmark scoring v2 (reports/plans/11, Part B).
 *
 *   score = 0                                  if any gate fails
 *   score = 60·requirements + 30·quality + 10·design/10   otherwise
 *
 * Components that are unavailable (e.g. no applicable quality checks, judge
 * gave no design score) are dropped and the remaining weights renormalized to
 * 100, so a trial is never penalized for a check that doesn't apply.
 */
import type { QualityCheck } from "./quality-checks.js";

export const SCORE_WEIGHTS = { requirements: 60, quality: 30, design: 10 } as const;

export interface ScoreInput {
  builds: boolean;
  runs: boolean;
  checks: QualityCheck[];
  requirements: Array<{ status?: string }>;
  /** 0–10 from the judge, or null/undefined if not given. */
  designScore?: number | null;
}

export interface ScoreBreakdown {
  version: 2;
  score: number;
  gates: { builds: boolean; runs: boolean; failed: string[] };
  requirements: { passed: number; total: number; pct: number | null };
  quality: { passed: number; failed: number; na: number; pct: number | null };
  design: number | null;
  /** Points each component contributed to `score` (after renormalization). */
  points: { requirements: number; quality: number; design: number };
  weights: typeof SCORE_WEIGHTS;
}

export function computeScoreV2(input: ScoreInput): ScoreBreakdown {
  const failedGates = [
    ...(input.builds ? [] : ["builds"]),
    ...(input.runs ? [] : ["runs"]),
    ...input.checks.filter((c) => c.kind === "gate" && c.status === "fail").map((c) => c.id),
  ];

  const reqTotal = input.requirements.length;
  const reqPassed = input.requirements.filter((r) => r.status === "pass").length;
  const reqPct = reqTotal > 0 ? reqPassed / reqTotal : null;

  const quality = input.checks.filter((c) => c.kind === "quality");
  const qPassed = quality.filter((c) => c.status === "pass").length;
  const qFailed = quality.filter((c) => c.status === "fail").length;
  const qNa = quality.filter((c) => c.status === "na").length;
  const qPct = qPassed + qFailed > 0 ? qPassed / (qPassed + qFailed) : null;

  const design = typeof input.designScore === "number" && Number.isFinite(input.designScore)
    ? Math.min(10, Math.max(0, input.designScore))
    : null;

  let score = 0;
  const points = { requirements: 0, quality: 0, design: 0 };
  if (failedGates.length === 0) {
    const parts: Array<[keyof typeof points, number, number]> = [];
    if (reqPct !== null) parts.push(["requirements", SCORE_WEIGHTS.requirements, reqPct]);
    if (qPct !== null) parts.push(["quality", SCORE_WEIGHTS.quality, qPct]);
    if (design !== null) parts.push(["design", SCORE_WEIGHTS.design, design / 10]);
    const weight = parts.reduce((a, [, w]) => a + w, 0);
    if (weight > 0) {
      for (const [k, w, v] of parts) points[k] = Math.round((1000 * w * v) / weight) / 10;
      score = Math.round((100 * parts.reduce((a, [, w, v]) => a + w * v, 0)) / weight);
    }
  }

  return {
    version: 2,
    score,
    gates: { builds: input.builds, runs: input.runs, failed: failedGates },
    requirements: { passed: reqPassed, total: reqTotal, pct: reqPct },
    quality: { passed: qPassed, failed: qFailed, na: qNa, pct: qPct },
    design,
    points,
    weights: SCORE_WEIGHTS,
  };
}

/** Compact "req:quality:design" string for the dashboard's breakdown column. */
export function formatBreakdown(b: ScoreBreakdown): string {
  const pct = (v: number | null) => (v === null ? "–" : `${Math.round(v * 100)}%`);
  return `R${pct(b.requirements.pct)} Q${pct(b.quality.pct)} D${b.design ?? "–"}`;
}
