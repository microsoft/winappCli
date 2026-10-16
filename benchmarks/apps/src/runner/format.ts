// Token / number formatting helpers — extracted so the runner can use them
// without pulling in Ink/React from scatter-plot.tsx.

/** Shorten a model name for use in trial identifiers (e.g. "claude-opus-4.6" -> "o46"). */
export function shortenModelName(model: string): string {
  return model
    .replace("claude-", "")
    .replace("sonnet-", "s")
    .replace("opus-", "o")
    .replace(".", "");
}

/**
 * Sanitize an upstream ref (branch/tag) so it is safe to embed in a trial
 * folder name: `/` becomes `--` and any other non `[A-Za-z0-9_.-]` char `_`.
 * PR refs (`pull/N/head`) become `prN`. Other refs are truncated to avoid
 * MAX_PATH issues: if the sanitized result exceeds 12 chars, keep the first
 * 4 chars plus an 8-character hash.
 */
export function sanitizeRefForId(ref: string): string {
  const pr = /^(?:refs\/)?pull\/(\d+)\/(?:head|merge)$/.exec(ref);
  if (pr) return `pr${pr[1]}`;
  const sanitized = ref.replace(/\//g, "--").replace(/[^A-Za-z0-9_.-]/g, "_");
  if (sanitized.length <= 12) return sanitized;

  let hash = 5381;
  for (let i = 0; i < ref.length; i++) {
    hash = ((hash << 5) + hash + ref.charCodeAt(i)) | 0;
  }
  return sanitized.slice(0, 4) + Math.abs(hash).toString(16).padStart(8, "0").slice(0, 8);
}

/** Compact aliases for verbose agent names, used in trial folder names to stay under MAX_PATH. Must be unique. */
const AGENT_SHORT_NAMES: Record<string, string> = {
  "winui3-fromuwp-migration": "w3uwp",
  "winui3-starter": "w3st",
  "winui3-agentsetup": "w3as",
};

const AGENT_LONG_NAMES: Record<string, string> = Object.fromEntries(
  Object.entries(AGENT_SHORT_NAMES).map(([long, short]) => [short, long]),
);

/** Shorten an agent name for use in trial identifiers. Unknown names returned as-is. */
export function shortenAgentName(agent: string): string {
  return AGENT_SHORT_NAMES[agent] ?? agent;
}

/** Inverse of shortenAgentName. Unknown values returned as-is. */
export function expandAgentName(short: string): string {
  return AGENT_LONG_NAMES[short] ?? short;
}

/**
 * Sanitize a task name for use as a trial folder segment: collapse path
 * separators and any non `[A-Za-z0-9_.-]` char to `_`, trim stray separators.
 * Keeps folders short and filesystem-safe.
 */
export function sanitizeTaskName(name: string): string {
  return (name || "")
    .replace(/[\\/]+/g, "_")
    .replace(/[^A-Za-z0-9_.-]/g, "_")
    .replace(/^_+|_+$/g, "") || "task";
}

/**
 * Flat-run trial folder name: a zero-padded auto-increment index plus the
 * task's declared name, e.g. `000_AdaptiveStreaming`, `001_score-AdaptiveStreaming`.
 *
 * The index keeps folders ordered + unique even when names repeat; the name
 * keeps them human-readable. Full agent/model/ref provenance lives in
 * `condition` + run-meta.json, so the folder stays short (which also sidesteps
 * the Windows MAX_PATH issues the long `{scenario}_{agent}-{ref}_...` scheme hit
 * when refs were long branch names).
 */
export function makeFlatTrialName(index: number, name: string): string {
  return `${String(index).padStart(3, "0")}_${sanitizeTaskName(name)}`;
}

/**
 * Build the canonical trial folder name from its components.
 * Format: `{scenario}_{shortAgent}{refSuffix}_{shortModel}_i{iter}` where
 * `refSuffix` is `-{sanitizedRef}` when an upstream ref is supplied (omitted
 * otherwise).
 */
export function makeTrialName(
  scenarioName: string,
  agentName: string,
  model: string,
  iter: number,
  ref?: string | null,
): string {
  const refSuffix = ref ? `-${sanitizeRefForId(ref)}` : "";
  return `${scenarioName}_${shortenAgentName(agentName)}${refSuffix}_${shortenModelName(model)}_i${iter}`;
}

/** Return exact and upstream-ref-free agent names from a stored condition label. */
export function agentNameCandidatesFromCondition(condition: unknown): string[] {
  const exactName = String(condition).replace(/\s*\[\d+\/\d+\]\s*$/, "");
  const baseName = exactName.replace(/@.*$/, "");
  return baseName === exactName ? [exactName] : [exactName, baseName];
}

/** Parse token strings like "2.4m", "137.5k" to numbers. */
export function parseTokenString(s: string): number {
  if (!s) return 0;
  const match = s.match(/^([\d.]+)\s*([mk])?$/i);
  if (!match) return 0;
  const num = parseFloat(match[1]);
  const unit = (match[2] || "").toLowerCase();
  if (unit === "m") return num * 1_000_000;
  if (unit === "k") return num * 1_000;
  return num;
}

/** Format a token count compactly: 1500 → "1.5k", 1_500_000 → "1.5m". */
export function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}m`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(Math.round(n));
}
