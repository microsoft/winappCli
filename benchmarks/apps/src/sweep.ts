import { mkdirSync, writeFileSync } from "fs";
import { join } from "path";
import {
  getNextRunName,
  resultsRoot,
  discoverScenarios,
  discoverAgentSetups,
  DEFAULT_BUILD_MODEL,
} from "./runner/config.js";
import { makeTrialName } from "./runner/format.js";
import { BenchmarkQueue } from "./runner/queue.js";
import { runPreflight } from "./runner/benchmark.js";
import type { RunEntry } from "./types.js";

const args = process.argv.slice(2);
function arg(name: string, def?: string): string | undefined {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : def;
}
function list(name: string): string[] {
  const v = arg(name);
  return v ? v.split(",").map((s) => s.trim()).filter(Boolean) : [];
}

const scenarioNames = list("--scenarios");
const agentNames = list("--agents");
const model = arg("--model", DEFAULT_BUILD_MODEL)!;
const concurrency = parseInt(arg("--concurrency", "4")!);
const iterations = parseInt(arg("--iterations", "1")!);
const maxBuildMinutes = parseInt(arg("--max-build-minutes", "60")!);
if (args.includes("--skip-validation")) process.env.SKIP_VALIDATION = "1";

if (!scenarioNames.length || !agentNames.length) {
  console.error("Usage: tsx src/sweep.ts --scenarios a,b,c --agents x,y [--model M] [--iterations N] [--concurrency C] [--max-build-minutes M]");
  process.exit(2);
}

const scenarios = discoverScenarios();
const agents = discoverAgentSetups();

const missingScenarios = scenarioNames.filter((n) => !scenarios.find((s) => s.name === n));
const missingAgents = agentNames.filter((n) => !agents.find((a) => a.name === n));
if (missingScenarios.length) { console.error(`Scenarios not found: ${missingScenarios.join(", ")}`); process.exit(2); }
if (missingAgents.length) { console.error(`Agents not found: ${missingAgents.join(", ")}`); process.exit(2); }

// Preflight ALL selected agents' prechecks before allocating a run dir.
// A mixed sweep (e.g. agents with different toolchains) must fail before any trial runs if
// EITHER framework's toolchain is broken — half-completed comparisons aren't
// useful and waste copilot tokens.
const preflightAgents = agentNames.map((name) => {
  const a = agents.find((x) => x.name === name)!;
  return { name: a.name, pluginPath: a.path };
});
console.log(`[sweep] preflight: checking ${preflightAgents.length} agent(s)`);
const pf = runPreflight(preflightAgents);
for (const line of pf.log) console.log(line);
if (!pf.ok) {
  console.error(`\n[sweep] PREFLIGHT FAILED — ${pf.failures.length} unmet dependency check(s):`);
  for (const f of pf.failures) console.error(`  [${f.agent}] ${f.label}`);
  console.error(`\nNothing was run. Fix the dependencies above and re-invoke.`);
  process.exit(3);
}

const runName = getNextRunName();
const runDir = join(resultsRoot, runName);
mkdirSync(runDir, { recursive: true });

writeFileSync(join(runDir, "run-meta.json"), JSON.stringify({
  timestamp: new Date().toISOString(),
  scenarios: scenarioNames,
  agents: agentNames,
  models: [model],
  concurrency,
  iterations,
  maxBuildMinutes,
}, null, 2));

const entries: RunEntry[] = [];
for (const scenName of scenarioNames) {
  const scenario = scenarios.find((s) => s.name === scenName)!;
  for (const agentName of agentNames) {
    const agent = agents.find((a) => a.name === agentName)!;
    for (let iter = 1; iter <= iterations; iter++) {
      const trialName = makeTrialName(scenario.name, agent.name, model, iter);
      const iterLabel = iterations > 1 ? ` [${iter}/${iterations}]` : "";
      entries.push({
        id: `${scenario.name}-${agent.name}-${model}-iter${iter}`,
        scenario: scenario.name,
        scenarioPath: scenario.path,
        scenarioConfigName: scenario.name,
        condition: agent.name + iterLabel,
        pluginPath: agent.path,
        model,
        trialName,
        iteration: iter,
        totalIterations: iterations,
        status: "queued",
        currentOutput: "",
      });
    }
  }
}

console.log(`[sweep] run=${runName} pairs=${scenarioNames.length}x${agentNames.length} iters=${iterations} total=${entries.length} model=${model} concurrency=${concurrency}`);
console.log(`[sweep] runDir=${runDir}`);

const queue = new BenchmarkQueue(
  entries,
  runDir,
  { maxBuildMinutes, maxContinues: 50, concurrency },
  {
    onOutput: (_entryId, data) => process.stdout.write(data),
    onStatusChange: (entry) => {
      console.log(`[status] ${entry.trialName} -> ${entry.status}${entry.score != null ? ` score=${entry.score}` : ""}${entry.failReason ? ` reason=${entry.failReason}` : ""}`);
    },
  }
);

queue.start()
  .then(() => { console.log(`[sweep] done. runDir=${runDir}`); process.exit(0); })
  .catch((err) => { console.error("[sweep] queue error:", err); process.exit(1); });
