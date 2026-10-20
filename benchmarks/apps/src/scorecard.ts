/**
 * Scorecard for a run (benchmark rules v2, reports/plans/11 Part B): per-setup
 * headline score with a 95% CI, success rate, cost and time, paired deltas
 * between setups (same model + scenario + iteration), and per-check pass
 * rates. Runs with several models get a pooled section plus one per model.
 * Works on partial runs (reads whatever results.json files exist).
 *
 *   npm run scorecard -- run906 [--markdown out.md]
 */
import { existsSync, readdirSync, readFileSync, writeFileSync } from "fs";
import { join } from "path";
import { resultsRoot } from "./runner/config.js";

interface Trial {
  arm: string;
  model: string;
  scenario: string;
  iteration: number;
  score: number;
  success: boolean;
  req: number | null;
  quality: number | null;
  design: number | null;
  credits: number | null;
  minutes: number | null;
  checks: Record<string, string>;
}

const args = process.argv.slice(2);
const runName = args.find((a) => !a.startsWith("--"));
const mdIdx = args.indexOf("--markdown");
const mdOut = mdIdx >= 0 ? args[mdIdx + 1] : undefined;
if (!runName) {
  console.error("Usage: npm run scorecard -- <runN> [--markdown out.md]");
  process.exit(2);
}
const runDir = join(resultsRoot, runName);
if (!existsSync(runDir)) { console.error(`Run not found: ${runDir}`); process.exit(2); }

function minutes(s: unknown): number | null {
  if (typeof s !== "string") return null;
  const h = Number(s.match(/(\d+)h/)?.[1] ?? 0), m = Number(s.match(/(\d+)m/)?.[1] ?? 0), sec = Number(s.match(/(\d+)s/)?.[1] ?? 0);
  return h || m || sec ? h * 60 + m + sec / 60 : null;
}

const trials: Trial[] = [];
for (const name of readdirSync(runDir)) {
  const f = join(runDir, name, "results.json");
  if (!existsSync(f)) continue;
  let r: any;
  try { r = JSON.parse(readFileSync(f, "utf-8")); } catch { continue; }
  const m = r.metrics ?? {};
  const b = m.score_breakdown;
  const checks: Record<string, string> = {};
  for (const c of m.quality_checks ?? []) checks[c.id] = c.status;
  trials.push({
    arm: String(r.condition ?? "?").replace(/\s*\[\d+\/\d+\]\s*$/, ""),
    model: String(r.model ?? "?"),
    scenario: r.scenario,
    iteration: r.iteration ?? 1,
    score: typeof m.score === "number" ? m.score : 0,
    success: !!m.builds && !!m.runs && !(b?.gates?.failed?.length) && !r.fail_reason,
    req: b?.requirements?.pct ?? null,
    quality: b?.quality?.pct ?? null,
    design: typeof b?.design === "number" ? b.design : null,
    credits: typeof m.time_and_tokens?.credits === "number" ? m.time_and_tokens.credits : null,
    minutes: minutes(m.time_and_tokens?.session_time),
    checks,
  });
}
if (!trials.length) { console.error(`No results.json in ${runDir}`); process.exit(1); }

// Keep `setup@ref` only when a setup ran at more than one ref (e.g. winappcli@main
// vs winappcli@my-branch); a single ref collapses to the setup name.
{
  const refsBySetup = new Map<string, Set<string>>();
  for (const t of trials) {
    const [setup, ref = ""] = t.arm.split("@");
    refsBySetup.set(setup, (refsBySetup.get(setup) ?? new Set()).add(ref));
  }
  for (const t of trials) {
    const setup = t.arm.split("@")[0];
    if ((refsBySetup.get(setup)?.size ?? 0) <= 1) t.arm = setup;
  }
}

const T95 = [0, 12.71, 4.30, 3.18, 2.78, 2.57, 2.45, 2.36, 2.31, 2.26, 2.23, 2.20, 2.18, 2.16, 2.14, 2.13, 2.12, 2.11, 2.10, 2.09, 2.09];
const mean = (xs: number[]) => xs.reduce((a, b) => a + b, 0) / xs.length;
function ci(xs: number[]): { mean: number; half: number | null } {
  const n = xs.length, mu = mean(xs);
  if (n < 2) return { mean: mu, half: null };
  const sd = Math.sqrt(xs.reduce((a, x) => a + (x - mu) ** 2, 0) / (n - 1));
  return { mean: mu, half: (T95[n - 1] ?? 1.96) * sd / Math.sqrt(n) };
}
const vals = <K extends keyof Trial>(ts: Trial[], k: K) => ts.map((t) => t[k]).filter((v): v is NonNullable<Trial[K]> => v !== null) as number[];
const fmt = (x: number | null | undefined, d = 0) => (x === null || x === undefined || Number.isNaN(x) ? "–" : x.toFixed(d));
const fmtCi = (c: { mean: number; half: number | null }, d = 0) => `${fmt(c.mean, d)}${c.half !== null ? ` ±${fmt(c.half, d)}` : ""}`;
const pct = (x: number | null) => (x === null || Number.isNaN(x) ? "–" : `${Math.round(x * 100)}%`);

const arms = [...new Set(trials.map((t) => t.arm))];
const models = [...new Set(trials.map((t) => t.model))];
const lines: string[] = [];
const out = (s = "") => lines.push(s);

function headline(ts: Trial[]): void {
  out("| Setup | n | Score | Success | Requirements | Quality | Design | Credits | Session min |");
  out("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
  for (const arm of arms) {
    const at = ts.filter((t) => t.arm === arm);
    if (!at.length) continue;
    const ok = at.filter((t) => t.success);
    out(`| ${arm} | ${at.length} | ${fmtCi(ci(at.map((t) => t.score)))} | ${pct(ok.length / at.length)} | ${pct(ok.length ? mean(vals(ok, "req")) : null)} | ${pct(ok.length ? mean(vals(ok, "quality")) : null)} | ${fmt(vals(ok, "design").length ? mean(vals(ok, "design")) : null, 1)} | ${fmtCi(ci(vals(at, "credits")), 1)} | ${fmtCi(ci(vals(at, "minutes")), 1)} |`);
  }
  out();
}

function paired(ts: Trial[]): void {
  out("| B vs A | pairs | Δ score | wins / ties / losses | Δ credits | Δ session min |");
  out("|---|---:|---:|---:|---:|---:|");
  const key = (t: Trial) => `${t.model}|${t.scenario}#${t.iteration}`;
  for (let i = 0; i < arms.length; i++) {
    for (let j = i + 1; j < arms.length; j++) {
      const [a, b] = [arms[i], arms[j]];
      const A = new Map(ts.filter((t) => t.arm === a).map((t) => [key(t), t]));
      const pairs = ts.filter((t) => t.arm === b && A.has(key(t))).map((t) => [A.get(key(t))!, t] as const);
      if (!pairs.length) continue;
      const d = pairs.map(([x, y]) => y.score - x.score);
      const dc = pairs.filter(([x, y]) => x.credits !== null && y.credits !== null).map(([x, y]) => y.credits! - x.credits!);
      const dm = pairs.filter(([x, y]) => x.minutes !== null && y.minutes !== null).map(([x, y]) => y.minutes! - x.minutes!);
      const w = d.filter((x) => x > 0).length, l = d.filter((x) => x < 0).length;
      out(`| ${b} vs ${a} | ${pairs.length} | ${fmtCi(ci(d))} | ${w} / ${d.length - w - l} / ${l} | ${dc.length ? fmtCi(ci(dc), 1) : "–"} | ${dm.length ? fmtCi(ci(dm), 1) : "–"} |`);
    }
  }
  out();
}

function byScenario(ts: Trial[]): void {
  const scenarios = [...new Set(ts.map((t) => t.scenario))].sort();
  out(`| Scenario | ${arms.join(" | ")} |`);
  out(`|---|${arms.map(() => "---:").join("|")}|`);
  for (const s of scenarios) {
    out(`| ${s} | ${arms.map((a) => {
      const xs = ts.filter((t) => t.arm === a && t.scenario === s).map((t) => t.score);
      return xs.length ? `${fmt(mean(xs))} (${xs.join(", ")})` : "–";
    }).join(" | ")} |`);
  }
  out();
}

const scenarioCount = new Set(trials.map((t) => t.scenario)).size;
out(`# Scorecard: ${runName}`);
out();
out(`${trials.length} trials · ${arms.length} setups · ${models.length} model(s) (${models.join(", ")}) · ${scenarioCount} scenarios. Means with 95% CI; pairs match model, scenario, and iteration. Small samples show direction, not significance.`);
out();

if (models.length > 1) {
  out("## All models");
  out();
  headline(trials);
  out("### Paired comparisons");
  out();
  paired(trials);
}
for (const model of models) {
  const ts = trials.filter((t) => t.model === model);
  out(`## ${models.length > 1 ? model : "Results"}`);
  out();
  headline(ts);
  out("### Paired comparisons");
  out();
  paired(ts);
  out("### Score by scenario");
  out();
  byScenario(ts);
}

out("## Check pass rates (trials that passed the gates, all models)");
out();
const ids = [...new Set(trials.flatMap((t) => Object.keys(t.checks)))].filter((id) => !id.startsWith("G"));
out(`| Check | ${arms.join(" | ")} |`);
out(`|---|${arms.map(() => "---:").join("|")}|`);
for (const id of ids) {
  out(`| ${id} | ${arms.map((a) => {
    const st = trials.filter((t) => t.arm === a && t.success).map((t) => t.checks[id]).filter((s) => s === "pass" || s === "fail");
    return st.length ? `${pct(st.filter((s) => s === "pass").length / st.length)} (${st.length})` : "–";
  }).join(" | ")} |`);
}
const failed = trials.filter((t) => !t.success);
if (failed.length) {
  out();
  out(`Gate failures (${failed.length}): ${failed.map((t) => `${t.model}/${t.arm}/${t.scenario}#${t.iteration}`).join(", ")}`);
}

const text = lines.join("\n");
console.log(text);
if (mdOut) { writeFileSync(mdOut, text + "\n"); console.error(`Wrote ${mdOut}`); }
