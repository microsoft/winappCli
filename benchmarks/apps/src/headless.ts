import { mkdirSync, writeFileSync, existsSync, readFileSync } from "fs";
import { join, isAbsolute, resolve } from "path";
import {
  getNextRunName,
  resultsRoot,
  discoverScenarios,
  discoverAgentSetups,
  validateFlatTasks,
  getJudgeModel,
  DEFAULT_BUILD_MODEL,
} from "./runner/config.js";
import {
  agentNameCandidatesFromCondition,
  makeFlatTrialName,
  sanitizeRefForId,
} from "./runner/format.js";
import { BenchmarkQueue } from "./runner/queue.js";
import { revalidateBenchmark, runPreflight } from "./runner/benchmark.js";
import type { RunEntry, FlatTask, RunMetaV2 } from "./types.js";

const args = process.argv.slice(2);
function arg(name: string, def?: string): string | undefined {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : def;
}

const scenarioName = arg("--scenario");
const agentName = arg("--agent");
const model = arg("--model", DEFAULT_BUILD_MODEL)!;
// `--judge-model` overrides the fixed validation/judge model for this run
// (default: `judge.model` in common/config.json). It is recorded per trial.
const judgeModelFlag = arg("--judge-model");
if (judgeModelFlag) process.env.BENCH_JUDGE_MODEL = judgeModelFlag;
const concurrency = parseInt(arg("--concurrency", "1")!);
const iterations = parseInt(arg("--iterations", "1")!);
const maxBuildMinutes = parseInt(arg("--max-build-minutes", "60")!);
const revalidateRun = arg("--revalidate-run");
const revalidateTrial = arg("--revalidate-trial");
const resumeRun = arg("--resume-run");
const runMetaPath = arg("--run-meta");
const upstreamRefsRaw = arg("--upstream-ref");
const upstreamRefs: string[] = upstreamRefsRaw
  ? upstreamRefsRaw.split(/[,\s]+/).map((s) => s.trim()).filter(Boolean)
  : [];
const listRefsAgent = arg("--list-refs");
// `--skip-validation` (or SKIP_VALIDATION=1 env) bypasses the LLM-judge step
// for the whole run. Trial still reports builds/runs but no score; useful for
// sweeps (and flat runs that do their own scoring as separate tasks) that only
// care whether the agent's code compiles + launches. Per-task `skipValidation`
// in a `--run-meta` file can override this per entry.
const skipValidationFlag = args.includes("--skip-validation");
if (skipValidationFlag) process.env.SKIP_VALIDATION = "1";
// `--dry-run` prints the resolved (scenario × agent × iteration) matrix and exits
// without launching the queue. Handy for verifying a big sweep before committing to it.
const dryRun = args.includes("--dry-run");

if (listRefsAgent) {
  const { discoverAgentSetups: discA } = await import("./runner/config.js");
  const a = discA().find((x) => x.name === listRefsAgent);
  if (!a) { console.error(`Agent not found: ${listRefsAgent}`); process.exit(2); }
  if (!a.config?.from_repo) { console.error(`Agent ${listRefsAgent} has no from_repo source`); process.exit(2); }
  const { listRefs } = await import("./runner/repo-cache.js");
  const refs = await listRefs(a.config.from_repo.repo, ["branches"]);
  for (const r of refs) {
    const def = r.isDefault ? "  (default)" : "";
    console.log(`${r.kind}\t${r.name}\t${r.sha?.slice(0, 7) || ""}${def}`);
  }
  process.exit(0);
}

if (revalidateRun && revalidateTrial) {
  const { join: pjoin } = await import("path");
  const { existsSync: pexists, readFileSync: pread } = await import("fs");
  const runDir = pjoin(resultsRoot, revalidateRun);
  const trialDir = pjoin(runDir, revalidateTrial);
  if (!pexists(trialDir)) { console.error(`Trial dir not found: ${trialDir}`); process.exit(2); }
  const resultsPath = pjoin(trialDir, "results.json");
  if (!pexists(resultsPath)) { console.error(`results.json not found: ${resultsPath}`); process.exit(2); }
  const prior = JSON.parse(pread(resultsPath, "utf-8"));
  const priorAgentNames = agentNameCandidatesFromCondition(prior.condition);
  const agentsAll = discoverAgentSetups();
  const a = priorAgentNames
    .map((name) => agentsAll.find((candidate) => candidate.name === name))
    .find((candidate) => candidate !== undefined);
  const scenarios2 = discoverScenarios();
  const s = scenarios2.find((x) => x.name === prior.scenario);
  if (!a || !s) { console.error(`Could not resolve agent (${priorAgentNames.join(" or ")}) / scenario (${prior.scenario}) from results.json`); process.exit(2); }
  const reEntry: RunEntry = {
    id: `${prior.scenario}-${prior.condition}-${prior.model}-iter${prior.iteration || 1}`,
    scenario: prior.scenario,
    scenarioPath: s.path,
    scenarioConfigName: prior.scenario,
    condition: prior.condition,
    pluginPath: a.path,
    model: prior.model,
    trialName: revalidateTrial,
    iteration: prior.iteration || 1,
    totalIterations: 1,
    status: "queued",
    currentOutput: "",
  };
  console.log(`[headless] revalidating run=${revalidateRun} trial=${revalidateTrial}`);
  await revalidateBenchmark(reEntry, runDir, {
    onOutput: (data) => process.stdout.write(data),
    onStatusChange: (e) => console.log(`[status] ${e.trialName} -> ${e.status}${e.score != null ? ` score=${e.score}` : ""}${e.failReason ? ` reason=${e.failReason}` : ""}`),
  });
  console.log(`[headless] revalidate done.`);
  process.exit(0);
}

if (resumeRun) {
  const runDir = join(resultsRoot, resumeRun);
  if (!existsSync(runDir)) {
    console.error(`Run not found: ${runDir}`);
    process.exit(2);
  }
  const { loadRunFromDisk } = await import("./runner/config.js");
  const loaded = loadRunFromDisk(runDir);
  const incomplete = loaded.entries.filter(
    (entry) => entry.status === "queued" || entry.status === "interrupted"
  );
  if (incomplete.length === 0) {
    console.log(`[headless] ${resumeRun} has no incomplete trials.`);
    process.exit(0);
  }
  const distinctAgents = [...new Map(
    incomplete.map((entry) => [entry.condition.replace(/@.*$/, "").replace(/\s*\[.*$/, ""), entry.pluginPath])
  ).entries()].map(([name, pluginPath]) => ({ name, pluginPath }));
  const pf = runPreflight(distinctAgents);
  for (const line of pf.log) console.log(line);
  if (!pf.ok) process.exit(3);
  const meta = JSON.parse(readFileSync(join(runDir, "run-meta.json"), "utf8"));
  const queue = new BenchmarkQueue(
    incomplete,
    runDir,
    { maxBuildMinutes, concurrency: Number(meta.concurrency) || concurrency },
    {
      onOutput: (_entryId, data) => process.stdout.write(data),
      onStatusChange: (entry) =>
        console.log(`[status] ${entry.trialName} -> ${entry.status}`),
    }
  );
  console.log(`[headless] resuming ${incomplete.length} incomplete trials in ${resumeRun}`);
  await queue.start();
  process.exit(0);
}

if (!runMetaPath && (!scenarioName || !agentName)) {
  console.error("Usage: tsx src/headless.ts --scenario X[,Y,...] --agent A[,B,...] [--model Z] [--iterations N] [--max-build-minutes M] [--concurrency C] [--skip-validation] [--upstream-ref ref1,ref2] [--judge-model J]");
  console.error("   --scenario / --agent accept a comma-separated list, or 'all' to run every discovered one.");
  console.error("   A matrix run executes every (scenario × agent × iteration) combination.");
  console.error("   or: tsx src/headless.ts --run-meta <path/to/run-meta.json>   (run an explicit flat task list)");
  console.error("   or: tsx src/headless.ts --revalidate-run runN --revalidate-trial <trial-folder>");
  console.error("   or: tsx src/headless.ts --resume-run runN");
  console.error("   or: tsx src/headless.ts --list-refs <agent>");
  process.exit(2);
}

const scenarios = discoverScenarios();
const agents = discoverAgentSetups();

// --scenario / --agent accept a comma-separated list or the literal "all".
function selectScenarios(spec: string) {
  if (spec.trim().toLowerCase() === "all") return scenarios;
  const names = spec.split(",").map((s) => s.trim()).filter(Boolean);
  const picked = [];
  for (const n of names) {
    const s = scenarios.find((x) => x.name === n);
    if (!s) { console.error(`Scenario not found: ${n}`); process.exit(2); }
    picked.push(s);
  }
  return picked;
}
function selectAgents(spec: string): Array<{ agent: (typeof agents)[number]; refs?: string[] }> {
  if (spec.trim().toLowerCase() === "all") return agents.map((agent) => ({ agent }));
  // `name@ref` picks a git ref for a from_repo setup (branch, tag, SHA, or
  // pull/N/head); repeat the name to compare refs: winappcli@main,winappcli@my-branch.
  const picked: Array<{ agent: (typeof agents)[number]; refs?: string[] }> = [];
  for (const token of spec.split(",").map((s) => s.trim()).filter(Boolean)) {
    const at = token.indexOf("@");
    const n = at >= 0 ? token.slice(0, at) : token;
    const ref = at >= 0 ? token.slice(at + 1).trim() : undefined;
    const a = agents.find((x) => x.name === n);
    if (!a) { console.error(`Agent not found: ${n}`); process.exit(2); }
    if (ref && !a.config?.from_repo) { console.error(`Agent ${n} has no from_repo source, so it can't take a ref (${token})`); process.exit(2); }
    let entry = picked.find((p) => p.agent.name === n);
    if (!entry) { entry = { agent: a }; picked.push(entry); }
    if (ref) entry.refs = [...new Set([...(entry.refs ?? []), ref])];
  }
  return picked;
}

// Resolve which refs to fan out across for a given agent. Explicit `name@ref`
// refs win; otherwise from_repo setups use --upstream-ref (not for catalog
// setups, whose ref is a catalog version in another repo) or their default_ref.
function refsForAgent(agent: (typeof agents)[number], explicit?: string[]): (string | null)[] {
  if (explicit?.length) return explicit;
  const fr = agent.config?.from_repo;
  if (fr) {
    return upstreamRefs.length > 0 && !fr.catalog
      ? upstreamRefs
      : [fr.default_ref || "main"];
  }
  return [null];
}

// Derive a default, readable task name for cross-product (UI/CLI) runs. The
// folder is `{NNN}_{name}`, so the index already disambiguates; we only add an
// agent / iteration suffix to the NAME when more than one is in play, to keep
// folders self-describing without bloating them.
function defaultTaskName(scenario: string, agent: string, iter: number, multiAgent: boolean, multiIter: boolean): string {
  let n = scenario;
  if (multiAgent) n += `-${agent}`;
  if (multiIter) n += `-i${iter}`;
  return n;
}

// ── Build the flat task list ──────────────────────────────────────────────
// Either loaded verbatim from an explicit `--run-meta` file (advanced /
// scripted runs), or expanded from the (scenario × agent × ref × iteration)
// cross-product the wizard/CLI collected. Both paths converge on `FlatTask[]`.
let tasks: FlatTask[] = [];
// When loaded from a file, its top-level `concurrency` acts as the fallback;
// otherwise the CLI flag supplies it. (There is no `defaults` block anymore —
// every task is self-contained.)
let loadedConcurrency: number | undefined;

if (runMetaPath) {
  const p = isAbsolute(runMetaPath) ? runMetaPath : resolve(process.cwd(), runMetaPath);
  if (!existsSync(p)) { console.error(`--run-meta file not found: ${p}`); process.exit(2); }
  let meta: Partial<RunMetaV2> & { defaults?: { model?: string; maxBuildMinutes?: number; skipValidation?: boolean } };
  try { meta = JSON.parse(readFileSync(p, "utf-8")); }
  catch (e) { console.error(`--run-meta is not valid JSON: ${e instanceof Error ? e.message : e}`); process.exit(2); }
  if (!Array.isArray(meta.tasks) || meta.tasks.length === 0) {
    console.error(`--run-meta must contain a non-empty "tasks" array.`); process.exit(2);
  }
  tasks = meta.tasks;
  loadedConcurrency = typeof meta.concurrency === "number" ? meta.concurrency : undefined;
  // Back-compat: an early-v2 meta may still carry a `defaults` block. Fold it
  // into any task that omitted a field so old files keep working; new metas
  // have no `defaults` and reach validation fully self-contained.
  const legacyDefaults = meta.defaults;
  if (legacyDefaults) {
    tasks = tasks.map((t) => ({
      ...t,
      model: t.model ?? (legacyDefaults.model as string),
      maxBuildMinutes: t.maxBuildMinutes ?? legacyDefaults.maxBuildMinutes,
      skipValidation: t.skipValidation ?? legacyDefaults.skipValidation,
    }));
  }
} else {
  const selScenarios = selectScenarios(scenarioName!);
  const selAgents = selectAgents(agentName!);
  if (selScenarios.length === 0) { console.error("No scenarios selected."); process.exit(2); }
  if (selAgents.length === 0) { console.error("No agents selected."); process.exit(2); }
  const arms = selAgents.flatMap(({ agent, refs }) => {
    const r = refsForAgent(agent, refs);
    // Name trials `agent@ref` when the ref was chosen or several are compared.
    const showRef = !!refs?.length || r.length > 1;
    return r.map((ref) => ({ agent, ref, label: showRef && ref ? `${agent.name}@${sanitizeRefForId(ref)}` : agent.name }));
  });
  const multiAgent = arms.length > 1;
  const multiIter = iterations > 1;
  for (const scenario of selScenarios)
    for (const { agent, ref, label } of arms)
        for (let iter = 1; iter <= iterations; iter++)
          tasks.push({
            name: defaultTaskName(scenario.name, label, iter, multiAgent, multiIter),
            scenario: scenario.name,
            agent: agent.name,
            model,
            maxBuildMinutes,
            upstreamRef: ref,
            skipValidation: skipValidationFlag || undefined,
            iteration: iter,
            totalIterations: iterations,
          });
}

const effConcurrency = loadedConcurrency ?? concurrency;

// Validate the whole task list up front — collect EVERY problem (missing
// required field, unknown scenario/agent) across all tasks and report them
// together, before any run dir is created.
const validationErrors = validateFlatTasks(tasks, {
  scenarioNames: new Set(scenarios.map((s) => s.name)),
  agentNames: new Set(agents.map((a) => a.name)),
});
if (validationErrors.length > 0) {
  console.error(`\n[headless] run-meta validation FAILED — ${validationErrors.length} problem(s):`);
  for (const e of validationErrors) console.error(`  • ${e}`);
  process.exit(2);
}

// Resolve + validate each task's scenario + agent, capturing paths.
interface ResolvedTask { task: FlatTask; scenarioPath: string; agentPath: string; }
const resolved: ResolvedTask[] = [];
for (const t of tasks) {
  const s = scenarios.find((x) => x.name === t.scenario);
  if (!s) { console.error(`Scenario not found: ${t.scenario}`); process.exit(2); }
  const a = agents.find((x) => x.name === t.agent);
  if (!a) { console.error(`Agent not found: ${t.agent}`); process.exit(2); }
  resolved.push({ task: t, scenarioPath: s.path, agentPath: a.path });
}

if (dryRun) {
  console.log(`[headless] --dry-run: ${resolved.length} task(s) would run (judge model: ${getJudgeModel()}):`);
  resolved.forEach((r, i) => {
    const t = r.task;
    const folder = makeFlatTrialName(i, t.name);
    console.log(`  - ${folder}  (scenario=${t.scenario}, agent=${t.agent}, model=${t.model}${t.upstreamRef ? `, ref=${t.upstreamRef}` : ""}, timeout=${t.maxBuildMinutes ?? maxBuildMinutes}m, skipValidation=${!!(t.skipValidation ?? skipValidationFlag)})`);
  });
  process.exit(0);
}

// Preflight only the distinct agents referenced by the task list.
{
  const distinctAgents = [...new Map(resolved.map((r) => [r.task.agent, r.agentPath])).entries()]
    .map(([name, pluginPath]) => ({ name, pluginPath }));
  console.log(`[headless] preflight: checking ${distinctAgents.length} agent(s)`);
  const pf = runPreflight(distinctAgents);
  for (const line of pf.log) console.log(line);
  if (!pf.ok) {
    console.error(`\n[headless] PREFLIGHT FAILED — ${pf.failures.length} unmet dependency check(s):`);
    for (const f of pf.failures) console.error(`  [${f.agent}] ${f.label}`);
    process.exit(3);
  }
}

const runName = getNextRunName();
const runDir = join(resultsRoot, runName);
mkdirSync(runDir, { recursive: true });

// Write the flat run-meta (v2). Top level is whole-list controls only
// (`concurrency`); `tasks[]` is the single source of truth — no `defaults`
// block. The distinct scenario/agent/model lists and the trial count are
// derived from it on read (loadRunMatrix), never stored here.
const runMeta: RunMetaV2 = {
  version: 2,
  timestamp: new Date().toISOString(),
  concurrency: effConcurrency,
  tasks: resolved.map((r) => r.task),
};
writeFileSync(join(runDir, "run-meta.json"), JSON.stringify(runMeta, null, 2));

const entries: RunEntry[] = resolved.map((r, i) => {
  const t = r.task;
  const ref = t.upstreamRef ?? null;
  const trialName = makeFlatTrialName(i, t.name);
  const iter = t.iteration ?? 1;
  const totalIter = t.totalIterations ?? 1;
  const iterLabel = totalIter > 1 ? ` [${iter}/${totalIter}]` : "";
  const condLabel = ref ? `${t.agent}@${ref}${iterLabel}` : t.agent + iterLabel;
  return {
    id: `${trialName}`,
    name: t.name,
    scenario: t.scenario,
    scenarioPath: r.scenarioPath,
    scenarioConfigName: t.scenario,
    condition: condLabel,
    pluginPath: r.agentPath,
    model: t.model,
    trialName,
    maxBuildMinutes: t.maxBuildMinutes ?? maxBuildMinutes,
    skipValidation: t.skipValidation ?? skipValidationFlag,
    iteration: iter,
    totalIterations: totalIter,
    status: "queued",
    currentOutput: "",
    ...(ref ? { upstreamRef: ref } : {}),
  };
});

console.log(`[headless] run=${runName} tasks=${entries.length} judge=${getJudgeModel()}`);
console.log(`[headless] runDir=${runDir}`);

const queue = new BenchmarkQueue(
  entries,
  runDir,
  { maxBuildMinutes, concurrency: effConcurrency },
  {
    onOutput: (entryId, data) => {
      // stream a trimmed copy to stdout so users can tail the log
      process.stdout.write(data);
    },
    onStatusChange: (entry) => {
      console.log(`[status] ${entry.trialName} -> ${entry.status}${entry.score != null ? ` score=${entry.score}` : ""}${entry.failReason ? ` reason=${entry.failReason}` : ""}`);
    },
  }
);

queue.start()
  .then(() => {
    console.log(`[headless] done. runDir=${runDir}`);
    process.exit(0);
  })
  .catch((err) => {
    console.error("[headless] queue error:", err);
    process.exit(1);
  });
