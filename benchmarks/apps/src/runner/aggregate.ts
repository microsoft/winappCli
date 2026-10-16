import type { RunEntry } from "../types.js";
import { parseTokenString } from "./format.js";
import { estimatePrice, type PriceEstimate } from "./pricing.js";

/** Seconds in a session-time string such as "1h 2m 3s", "4m 5s" or "6s". */
export function parseDurationText(text: string | undefined): number | undefined {
  if (!text) return undefined;
  const parts = [...text.matchAll(/(\d+(?:\.\d+)?)\s*([hms])/g)];
  if (parts.length === 0) return undefined;
  const unit = { h: 3600, m: 60, s: 1 } as const;
  return parts.reduce((sum, [, n, u]) => sum + Number(n) * unit[u as keyof typeof unit], 0);
}

/** Inverse of parseDurationText, in the harness's session-time format. */
export function formatDurationText(seconds: number): string {
  const total = Math.round(seconds);
  if (total < 60) return `${total}s`;
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m < 60) return `${m}m ${s}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m ${s}s`;
}

export interface AggregatedEntry {
  scenario: string;
  condition: string;
  model: string;
  avgScore: number;
  minScore: number;
  maxScore: number;
  iterations: number;
  completed: number;
  buildRate: number;
  runRate: number;
  avgSessionTime?: string;
  avgTokens?: number;
  avgInputTokens?: string;
  avgOutputTokens?: string;
  avgCachedTokens?: string;
  avgPremiumRequests?: number;
  avgCredits?: number;
  avgSubAgentInputTokens?: string;
  avgSubAgentCachedTokens?: string;
  avgSubAgentCount?: number;
  avgPrice?: PriceEstimate;
  entries: RunEntry[];
}

/**
 * Aggregate iterations into averaged entries.
 * If entries have no iterations (totalIterations <= 1), they pass through as-is.
 */
export function aggregateEntries(entries: RunEntry[]): AggregatedEntry[] {
  const completed = entries.filter((e) =>
    ["done", "failed", "interrupted", "timeout"].includes(e.status)
  );

  // Group by scenario + base condition (strip [1/2] suffix) + model
  const groups = new Map<string, RunEntry[]>();
  for (const entry of completed) {
    const baseCondition = entry.condition.replace(/\s*\[\d+\/\d+\]$/, "");
    const key = `${entry.scenario}|${baseCondition}|${entry.model}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key)!.push(entry);
  }

  const result: AggregatedEntry[] = [];
  for (const [, group] of groups) {
    const withScores = group.filter((e) => e.score != null);
    const scores = withScores.map((e) => e.score!);
    const baseCondition = group[0].condition.replace(/\s*\[\d+\/\d+\]$/, "");

    const tokens = withScores
      .map((e) => parseTokenString(e.inputTokens || "0"))
      .filter((t) => t > 0);

    // Compute average price across iterations (includes both main and sub-agent costs)
    const prices = withScores
      .filter((e) => e.inputTokens)
      .map((e) =>
        estimatePrice({
          model: e.model,
          inputTokens: e.inputTokens || "0",
          outputTokens: e.outputTokens || "0",
          cachedTokens: e.cachedTokens || "0",
          subAgentInputTokens: e.subAgentInputTokens || "0",
          subAgentOutputTokens: e.subAgentOutputTokens || "0",
          subAgentCachedTokens: e.subAgentCachedTokens || "0",
        })
      );
    const avgPrice = prices.length > 0
      ? {
          inputCost: prices.reduce((s, p) => s + p.inputCost, 0) / prices.length,
          cachedCost: prices.reduce((s, p) => s + p.cachedCost, 0) / prices.length,
          outputCost: prices.reduce((s, p) => s + p.outputCost, 0) / prices.length,
          totalCost: prices.reduce((s, p) => s + p.totalCost, 0) / prices.length,
          formatted: (() => {
            const avg = prices.reduce((s, p) => s + p.totalCost, 0) / prices.length;
            return avg < 0.01 ? "<$0.01" : `$${avg.toFixed(2)}`;
          })(),
        }
      : undefined;

    // Average token strings for display
    const avgIn = tokens.length > 0 ? formatTokenCount(tokens.reduce((a, b) => a + b, 0) / tokens.length) : undefined;
    const outTokens = withScores.map((e) => parseTokenString(e.outputTokens || "0")).filter((t) => t > 0);
    const avgOut = outTokens.length > 0 ? formatTokenCount(outTokens.reduce((a, b) => a + b, 0) / outTokens.length) : undefined;
    const cacheTokens = withScores.map((e) => parseTokenString(e.cachedTokens || "0")).filter((t) => t > 0);
    const avgCache = cacheTokens.length > 0 ? formatTokenCount(cacheTokens.reduce((a, b) => a + b, 0) / cacheTokens.length) : undefined;

    // Average session time ("1h 2m 3s" / "4m 5s" / "6s")
    const sessionSecs = withScores
      .map((e) => parseDurationText(e.sessionTime))
      .filter((s): s is number => s != null);
    const avgSession = sessionSecs.length > 0
      ? formatDurationText(sessionSecs.reduce((a, b) => a + b, 0) / sessionSecs.length)
      : undefined;

    // Average premium requests
    const premiums = withScores.map((e) => e.premiumRequests ?? 0).filter((p) => p > 0);
    const avgPremium = premiums.length > 0
      ? Math.round(premiums.reduce((a, b) => a + b, 0) / premiums.length)
      : undefined;

    // Average credit (AIU) cost across iterations
    const credits = withScores.map((e) => e.credits ?? 0).filter((c) => c > 0);
    const avgCredits = credits.length > 0
      ? Math.round((credits.reduce((a, b) => a + b, 0) / credits.length) * 10) / 10
      : undefined;

    // Average sub-agent tokens
    const subInputs = withScores.map((e) => parseTokenString(e.subAgentInputTokens || "0")).filter((t) => t > 0);
    const avgSubIn = subInputs.length > 0 ? formatTokenCount(subInputs.reduce((a, b) => a + b, 0) / subInputs.length) : undefined;
    const subCached = withScores.map((e) => parseTokenString(e.subAgentCachedTokens || "0")).filter((t) => t > 0);
    const avgSubCached = subCached.length > 0 ? formatTokenCount(subCached.reduce((a, b) => a + b, 0) / subCached.length) : undefined;
    const subCounts = withScores.map((e) => e.subAgentCount ?? 0).filter((c) => c > 0);
    const avgSubCount = subCounts.length > 0 ? Math.round(subCounts.reduce((a, b) => a + b, 0) / subCounts.length) : undefined;

    result.push({
      scenario: group[0].scenario,
      condition: baseCondition,
      model: group[0].model,
      avgScore: scores.length > 0 ? Math.round(scores.reduce((a, b) => a + b, 0) / scores.length) : 0,
      minScore: scores.length > 0 ? Math.min(...scores) : 0,
      maxScore: scores.length > 0 ? Math.max(...scores) : 0,
      iterations: group.length,
      completed: withScores.length,
      buildRate: group.filter((e) => e.builds).length / group.length,
      runRate: group.filter((e) => e.runs).length / group.length,
      avgTokens: tokens.length > 0 ? Math.round(tokens.reduce((a, b) => a + b, 0) / tokens.length) : undefined,
      avgInputTokens: avgIn,
      avgOutputTokens: avgOut,
      avgCachedTokens: avgCache,
      avgPremiumRequests: avgPremium,
      avgCredits,
      avgSubAgentInputTokens: avgSubIn,
      avgSubAgentCachedTokens: avgSubCached,
      avgSubAgentCount: avgSubCount,
      avgPrice,
      avgSessionTime: avgSession,
      entries: group,
    });
  }

  return result.sort((a, b) => {
    if (a.scenario !== b.scenario) return a.scenario.localeCompare(b.scenario);
    if (a.model !== b.model) return a.model.localeCompare(b.model);
    return b.avgScore - a.avgScore;
  });
}

function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}m`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`;
  return String(Math.round(n));
}
