import { readFileSync, readdirSync, existsSync, statSync } from "fs";
import { join, resolve, dirname, isAbsolute } from "path";
import { fileURLToPath } from "url";
import { homedir, platform } from "os";
import {
  GlobalConfig,
  ScenarioConfig,
  AgentSetupInfo,
  ScriptEntry,
  FlatTask,
} from "../types.js";
import { parse as parseYaml } from "yaml";
import { parseTokenString, makeTrialName, makeFlatTrialName } from "./format.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

/** `benchmarks/apps` in the winappCli repo (override with BENCH_ROOT). */
export const benchRoot = process.env.BENCH_ROOT
  ? resolve(process.env.BENCH_ROOT)
  : resolve(__dirname, "..", "..");
/** The winappCli repository root (plugins are loaded from here by `from_local` agents). */
export const repoRoot = resolve(benchRoot, "..", "..");
/**
 * Runs and generated apps live OUTSIDE the repo by default (override with
 * BENCH_RESULTS_DIR). Inside the checkout, trials would inherit the repo's
 * AGENTS.md / .github instructions, nuget.config and global.json, and the
 * agent would see a git repo it didn't create.
 */
export const resultsRoot = process.env.BENCH_RESULTS_DIR
  ? resolve(process.env.BENCH_RESULTS_DIR)
  : join(process.env.LOCALAPPDATA || join(homedir(), ".local", "share"), "winapp-benchmarks", "apps", "results");
/** Agent setups: `agents/<name>/config.json`, plus git-ignored `agents/.local/<name>/` experiments. */
export const agentsDir = join(benchRoot, "agents");

/** Code-level fallback build timeout (minutes) for a flat task that omits its
 *  own `maxBuildMinutes`. This is an optional-knob default, NOT a configurable
 *  run-level `defaults` block — there is intentionally no run-level model or
 *  skip-validation fallback; those are required / naturally-false per task. */
export const DEFAULT_MAX_BUILD_MINUTES = 60;
export const scenariosDir = join(benchRoot, "scenarios");

export function loadGlobalConfig(): GlobalConfig {
  const configPath = join(benchRoot, "common", "config.json");
  return JSON.parse(readFileSync(configPath, "utf-8"));
}

export const DEFAULT_JUDGE_MODEL = "claude-sonnet-5.5";

/** Default build model for headless/sweep/quick runs when `--model` isn't given. */
export const DEFAULT_BUILD_MODEL = "claude-sonnet-5.5";

/** Validation/scoring model: `BENCH_JUDGE_MODEL` > config `judge.model` > default. */
export function getJudgeModel(): string {
  const fromEnv = process.env.BENCH_JUDGE_MODEL?.trim();
  if (fromEnv) return fromEnv;
  try {
    return loadGlobalConfig().judge?.model?.trim() || DEFAULT_JUDGE_MODEL;
  } catch {
    return DEFAULT_JUDGE_MODEL;
  }
}

/**
 * Make `test_assets[].path` portable: expand `{scenario_dir}` / `{repo_root}`
 * and resolve relative paths against the scenario folder.
 */
export function expandAssetPaths(config: ScenarioConfig, scenarioDir: string): ScenarioConfig {
  if (!config || !Array.isArray(config.test_assets)) return config;
  config.test_assets = config.test_assets.map((asset) => {
    if (!asset?.path) return asset;
    const expanded = String(asset.path)
      .replace(/\{scenario_dir\}/g, scenarioDir)
      .replace(/\{repo_root\}/g, repoRoot)
      .replace(/%([^%]+)%/g, (m, name: string) => {
        const key = Object.keys(process.env).find((k) => k.toLowerCase() === name.toLowerCase());
        return key ? String(process.env[key]) : m;
      });
    return { ...asset, path: isAbsolute(expanded) ? expanded : resolve(scenarioDir, expanded) };
  });
  return config;
}

// Parse scenario.md (YAML frontmatter + markdown body) or fall back to scenario.json + prompt.md
export function loadScenario(scenarioDir: string): { config: ScenarioConfig; prompt: string } | null {
  const scenarioMd = join(scenarioDir, "scenario.md");
  const scenarioJson = join(scenarioDir, "scenario.json");

  if (existsSync(scenarioMd)) {
    const raw = readFileSync(scenarioMd, "utf-8").replace(/\r\n/g, "\n");
    const fmMatch = raw.match(/^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]*)$/);
    if (fmMatch) {
      const config = expandAssetPaths(parseYaml(fmMatch[1]) as ScenarioConfig, scenarioDir);
      const prompt = fmMatch[2].trim();
      return { config, prompt };
    }
  }

  if (existsSync(scenarioJson)) {
    const config = JSON.parse(readFileSync(scenarioJson, "utf-8")) as ScenarioConfig;
    const promptFile = join(scenarioDir, "prompt.md");
    const prompt = existsSync(promptFile) ? readFileSync(promptFile, "utf-8") : "";
    return { config, prompt };
  }

  return null;
}

// Resolve a scenario name to its path, checking both flat and nested group folders.
// Handles legacy names (e.g. "macos-counter") by scanning all nested dirs.
export function resolveScenarioPath(scenarioName: string): string {
  if (!scenarioName) return "";
  // Direct match (flat)
  const direct = join(scenariosDir, scenarioName);
  if (existsSync(direct)) return direct;
  // Search inside group folders (one level deep)
  if (existsSync(scenariosDir)) {
    for (const group of readdirSync(scenariosDir)) {
      const nested = join(scenariosDir, group, scenarioName);
      if (existsSync(nested) && statSync(nested).isDirectory()) return nested;
    }
  }
  return "";
}

export function discoverScenarios(): Array<{
  name: string;
  path: string;
  config: ScenarioConfig;
}> {
  if (!existsSync(scenariosDir)) return [];
  const results: Array<{ name: string; path: string; config: ScenarioConfig }> = [];

  for (const entry of readdirSync(scenariosDir)) {
    // `_retired` (and other `_`-prefixed folders) hold scenarios kept for
    // history; resolveScenarioPath still finds them for loading old runs.
    if (entry.startsWith("_")) continue;
    const dir = join(scenariosDir, entry);
    if (!statSync(dir).isDirectory()) continue;

    // Direct scenario folder (e.g. scenarios/ai-journal/)
    const direct = loadScenario(dir);
    if (direct) {
      results.push({ name: entry, path: dir, config: direct.config });
      continue;
    }

    // Group folder (e.g. scenarios/counter/counter-winui/)
    for (const sub of readdirSync(dir)) {
      const subDir = join(dir, sub);
      if (!statSync(subDir).isDirectory()) continue;
      const nested = loadScenario(subDir);
      if (nested) {
        results.push({ name: sub, path: subDir, config: nested.config });
      }
    }
  }

  return results;
}

export function discoverAgentSetups(): AgentSetupInfo[] {
  const agentSetups: AgentSetupInfo[] = [];

  // Cross-platform filter: skip agents that target a foreign platform on the
  // current host. Detect via framework_hint (declarative) or build/scaffold
  // commands that reference toolchains only available on macOS (xcodebuild,
  // xcodegen, brew, swift, open .app) or Windows (msbuild, dotnet, winget,
  // powershell). The env var BENCH_SHOW_FOREIGN_AGENTS=1 disables filtering.
  const hostIsWindows = platform() === "win32";
  const hostIsMac = platform() === "darwin";
  const showForeign = process.env.BENCH_SHOW_FOREIGN_AGENTS === "1";
  const isMacOnlyAgent = (cfg: import("../types.js").AgentSetupConfig | undefined): boolean => {
    if (!cfg) return false;
    const hint = (cfg.framework_hint || "").toLowerCase();
    if (/swift|swiftui|macos|ios|ipad|tvos|watchos|xcode/.test(hint)) return true;
    const cmds = [cfg.scaffold_command, cfg.build_command, cfg.launch_command]
      .filter((s): s is string => typeof s === "string")
      .join(" \n ");
    return /\b(xcodebuild|xcodegen|xcrun|swift build|swift package|brew install|brew list)\b/i.test(cmds);
  };
  const isWindowsOnlyAgent = (cfg: import("../types.js").AgentSetupConfig | undefined): boolean => {
    if (!cfg) return false;
    const hint = (cfg.framework_hint || "").toLowerCase();
    if (/winui|wpf|win32|windows app sdk|winappsdk|uwp|\.net maui/.test(hint)) return true;
    const cmds = [cfg.scaffold_command, cfg.build_command, cfg.launch_command]
      .filter((s): s is string => typeof s === "string")
      .join(" \n ");
    return /\b(msbuild|winget|powershell\.exe|pwsh\.exe)\b/i.test(cmds);
  };
  const shouldHide = (cfg: import("../types.js").AgentSetupConfig | undefined): boolean => {
    if (showForeign) return false;
    if (hostIsWindows && isMacOnlyAgent(cfg)) return true;
    if (hostIsMac && isWindowsOnlyAgent(cfg)) return true;
    return false;
  };

  // Scan agent directories: agents/ and agents/.local/
  const agentDirs = [
    agentsDir,
    join(agentsDir, ".local"),
  ];
  for (const srcAgentsDir of agentDirs) {
    if (!existsSync(srcAgentsDir)) continue;
    for (const d of readdirSync(srcAgentsDir)) {
      const full = join(srcAgentsDir, d);
      if (!statSync(full).isDirectory()) continue;
      if (d.startsWith("_")) continue; // skip _sections, etc.

      // Direct agent folder (has config.json)
      if (existsSync(join(full, "config.json"))) {
        let config: import("../types.js").AgentSetupConfig | undefined;
        try {
          config = JSON.parse(readFileSync(join(full, "config.json"), "utf-8"));
        } catch {}
        if (shouldHide(config)) continue;
        agentSetups.push({ name: d, path: full, config });
        continue;
      }

      // Group folder (e.g. agents/group/variant/)
      for (const sub of readdirSync(full)) {
        const subFull = join(full, sub);
        if (!statSync(subFull).isDirectory()) continue;
        if (!existsSync(join(subFull, "config.json"))) continue;
        let config: import("../types.js").AgentSetupConfig | undefined;
        try {
          config = JSON.parse(readFileSync(join(subFull, "config.json"), "utf-8"));
        } catch {}
        if (shouldHide(config)) continue;
        agentSetups.push({ name: sub, path: subFull, config });
      }
    }
  }

  if (agentSetups.length > 0) return agentSetups;

  // Legacy path: if no agents found under agents/, try reading agentsetups.root
  // from config.json. This supports older setups that store agent variants in a
  // separate directory (defined in config.agentsetups.root).
  // If config.agentsetups.root is not defined, we return an empty array 
  //   - this is not an error, since running with only bare/starter conditions (no agent setups) is valid.
  const config = loadGlobalConfig();
  const legacyRootRaw = config.agentsetups?.root;
  if (!legacyRootRaw) return [];

  let legacyRoot = legacyRootRaw;
  if (!legacyRoot.startsWith("/") && !legacyRoot.match(/^[A-Z]:/i)) {
    legacyRoot = resolve(benchRoot, legacyRoot);
  }

  if (!existsSync(legacyRoot)) return [];

  return readdirSync(legacyRoot)
    .filter((d) => {
      const full = join(legacyRoot, d);
      return (
        statSync(full).isDirectory() &&
        (existsSync(join(full, "agents")) || existsSync(join(full, "skills")))
      );
    })
    .map((d) => ({
      name: d,
      path: join(legacyRoot, d),
    }));
}

export function getNextRunName(): string {
  if (!existsSync(resultsRoot)) return "run1";

  const existing = readdirSync(resultsRoot)
    .filter((d) => d.match(/^run(\d+)/))
    .map((d) => parseInt(d.match(/^run(\d+)/)![1]));

  const nextNum =
    existing.length > 0 ? Math.max(...existing) + 1 : 1;
  return `run${nextNum}`;
}

function formatTimestamp(): string {
  const now = new Date();
  const mm = String(now.getMonth() + 1).padStart(2, "0");
  const dd = String(now.getDate()).padStart(2, "0");
  const yy = String(now.getFullYear()).slice(-2);
  const HH = String(now.getHours()).padStart(2, "0");
  const MM = String(now.getMinutes()).padStart(2, "0");
  const SS = String(now.getSeconds()).padStart(2, "0");
  return `${mm}${dd}${yy}-${HH}${MM}${SS}`;
}

export function loadPrompt(scenarioPath: string): string {
  const result = loadScenario(scenarioPath);
  if (result) return result.prompt;
  // Fallback
  const promptFile = join(scenarioPath, "prompt.md");
  return existsSync(promptFile) ? readFileSync(promptFile, "utf-8") : "";
}

export function loadValidationPrompt(platformHint?: string): string {
  // Route to platform-specific validation prompt
  const variant = platformHint?.toLowerCase().includes("swiftui") ? "swiftui"
    : "winui";
  return readFileSync(join(benchRoot, "common", `validate-${variant}.prompt.md`), "utf-8");
}

function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}m`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(Math.round(n));
}

// =============================================================================
// Matrix Loading
// =============================================================================

/** Context a flat task list is validated against — the names that actually
 *  exist on disk, so unknown scenario/agent references are caught up front. */
export interface TaskValidationContext {
  scenarioNames: Set<string>;
  agentNames: Set<string>;
}

/**
 * Validate a flat task list BEFORE a run is created. Collects EVERY problem
 * across ALL tasks (it does not stop at the first) and returns them as a list
 * of human-readable messages; an empty list means the task list is runnable.
 *
 * Required per task (run-meta has no `defaults` block to fall back to):
 *   - `name`     — used for the `{NNN}_{name}` trial folder
 *   - `scenario` — must resolve to a discovered scenario
 *   - `agent`    — must resolve to a discovered agent setup
 *   - `model`    — each task is self-contained; there is no run-level model
 * Optional (sensible code-level fallbacks apply, so absence is NOT an error):
 *   `maxBuildMinutes` (→ DEFAULT_MAX_BUILD_MINUTES), `skipValidation` (→ false),
 *   `upstreamRef` (→ null), `iteration`/`totalIterations` (→ 1). When present,
 *   their types are still sanity-checked.
 */
export function validateFlatTasks(
  tasks: Array<Partial<FlatTask>>,
  ctx: TaskValidationContext
): string[] {
  const errors: string[] = [];
  if (!Array.isArray(tasks) || tasks.length === 0) {
    return ["Task list is empty — there is nothing to run."];
  }
  tasks.forEach((t, i) => {
    const label = `task[${i}]${typeof t.name === "string" && t.name.trim() ? ` "${t.name}"` : ""}`;
    const requireStr = (field: "name" | "scenario" | "agent" | "model"): boolean => {
      const v = t[field];
      if (typeof v !== "string" || v.trim() === "") {
        errors.push(`${label}: missing required field "${field}"`);
        return false;
      }
      return true;
    };
    requireStr("name");
    if (requireStr("scenario") && !ctx.scenarioNames.has(t.scenario as string)) {
      errors.push(`${label}: unknown scenario "${t.scenario}" (not found on disk)`);
    }
    if (requireStr("agent") && !ctx.agentNames.has(t.agent as string)) {
      errors.push(`${label}: unknown agent "${t.agent}" (not found on disk)`);
    }
    requireStr("model");
    if (t.maxBuildMinutes !== undefined && (typeof t.maxBuildMinutes !== "number" || t.maxBuildMinutes <= 0)) {
      errors.push(`${label}: "maxBuildMinutes" must be a positive number when set`);
    }
    if (t.skipValidation !== undefined && typeof t.skipValidation !== "boolean") {
      errors.push(`${label}: "skipValidation" must be a boolean when set`);
    }
  });
  return errors;
}

export interface RunMatrix {
  scenarios: string[];
  agents: string[];
  models: string[];
  concurrency: number;
  iterations: number;
  /** Authoritative number of trials in the run. For a flat v2 meta this is
   *  `tasks.length`; for a legacy v1 meta it's the cross-product size. Use this
   *  instead of multiplying the dimension lengths — a flat task list is not
   *  necessarily a clean scenario × agent × model × iteration cartesian. */
  taskCount: number;
}

/** Load a benchmark matrix summary from a run-meta.json (or any same-shaped
 *  JSON). For a flat v2 meta the distinct scenario/agent/model lists, iteration
 *  count and trial total are DERIVED from `tasks[]` (the single source of
 *  truth); a legacy v1 meta falls back to its top-level summary arrays. */
export function loadRunMatrix(filePath: string): RunMatrix | null {
  if (!existsSync(filePath)) return null;
  try {
    const raw = JSON.parse(readFileSync(filePath, "utf-8"));
    const concurrency = typeof raw.concurrency === "number" ? raw.concurrency : 3;

    // ── v2 (flat): derive everything from tasks[] ──
    if (Array.isArray(raw.tasks) && raw.tasks.length > 0) {
      const defModel = raw.defaults?.model;
      const uniq = (xs: unknown[]) => [...new Set(xs.filter((x): x is string => typeof x === "string" && x.length > 0))];
      return {
        scenarios: uniq(raw.tasks.map((t: { scenario?: string }) => t.scenario)),
        agents: uniq(raw.tasks.map((t: { agent?: string }) => t.agent)),
        models: uniq(raw.tasks.map((t: { model?: string }) => t.model ?? defModel)),
        concurrency,
        iterations: raw.tasks.reduce((mx: number, t: { totalIterations?: number }) => Math.max(mx, t.totalIterations ?? 1), 1),
        taskCount: raw.tasks.length,
      };
    }

    // ── v1 (legacy): top-level summary arrays + cross-product trial count ──
    const scenarios: string[] = Array.isArray(raw.scenarios) ? raw.scenarios : [];
    const agents: string[] = Array.isArray(raw.agents) ? raw.agents : [];
    const models: string[] = Array.isArray(raw.models) ? raw.models : [];
    const iterations: number = typeof raw.iterations === "number" ? raw.iterations : 1;
    return {
      scenarios,
      agents,
      models,
      concurrency,
      iterations,
      taskCount: scenarios.length * agents.length * models.length * iterations,
    };
  } catch {
    return null;
  }
}

// =============================================================================
// Run Discovery & Loading
// =============================================================================

export function discoverRuns(): Array<{ name: string; path: string; date: Date }> {
  if (!existsSync(resultsRoot)) return [];
  return readdirSync(resultsRoot)
    .filter((d) => d.match(/^run\d+/) && statSync(join(resultsRoot, d)).isDirectory())
    .map((d) => ({
      name: d,
      path: join(resultsRoot, d),
      date: statSync(join(resultsRoot, d)).mtime,
    }))
    .sort((a, b) => {
      const aNum = parseInt(a.name.match(/^run(\d+)/)![1]);
      const bNum = parseInt(b.name.match(/^run(\d+)/)![1]);
      return bNum - aNum; // newest first
    });
}

export function loadRunFromDisk(
  runDir: string
): { entries: import("../types.js").RunEntry[]; runName: string } {
  const runName = runDir.split(/[\\/]/).pop() || "unknown";
  const entries: import("../types.js").RunEntry[] = [];

  // Recursively find all results.json files
  function findResults(dir: string): string[] {
    const found: string[] = [];
    if (!existsSync(dir)) return found;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isFile() && entry.name === "results.json") {
        found.push(full);
      } else if (
        entry.isDirectory() &&
        entry.name !== ".attempts" &&
        entry.name !== "app" &&
        entry.name !== "bin" &&
        entry.name !== "obj"
      ) {
        found.push(...findResults(full));
      }
    }
    return found;
  }

  const resultFiles = findResults(runDir);

  for (const file of resultFiles) {
    try {
      const raw = JSON.parse(readFileSync(file, "utf-8"));
      if (!raw?.trial || !raw?.scenario || !raw?.model || !raw?.metrics) continue;
      const m = raw.metrics || {};
      const tt = m.time_and_tokens || {};

      // Determine condition type from condition name
      const condition = raw.condition || "";
      const iterationLabel = condition.match(/\s*\[(\d+)\/(\d+)\]$/);
      const trialIteration = String(raw.trial || "").match(/_i(\d+)$/);
      const iteration =
        typeof raw.iteration === "number"
          ? raw.iteration
          : Number(iterationLabel?.[1] || trialIteration?.[1] || 1);
      const totalIterations =
        typeof raw.totalIterations === "number"
          ? raw.totalIterations
          : Number(iterationLabel?.[2] || 1);
      const condBase = condition.replace(/\s*\[\d+\/\d+\]$/, "");
      const normalizedCondition = condBase.replace(/^agentsetup-/, "");
      const refSeparator = normalizedCondition.indexOf("@");
      const agentName =
        refSeparator === -1
          ? normalizedCondition
          : normalizedCondition.slice(0, refSeparator);
      const conditionRef =
        refSeparator === -1
          ? undefined
          : normalizedCondition.slice(refSeparator + 1);
      const upstreamRef =
        typeof raw.agent_source?.ref === "string"
          ? raw.agent_source.ref
          : conditionRef;

      // Extract token info from first model
      let inputTokens: string | undefined;
      let outputTokens: string | undefined;
      let cachedTokens: string | undefined;
      if (tt.models) {
        const firstModel = Object.keys(tt.models)[0];
        if (firstModel) {
          inputTokens = tt.models[firstModel].input;
          outputTokens = tt.models[firstModel].output;
          cachedTokens = tt.models[firstModel].cached;
        }
      }

      // Extract sub-agent token info
      let subAgentInputTokens: string | undefined;
      let subAgentOutputTokens: string | undefined;
      let subAgentCachedTokens: string | undefined;
      let subAgentCount: number | undefined;
      if (tt.sub_agent_models && Object.keys(tt.sub_agent_models).length > 0) {
        let subIn = 0, subOut = 0, subCached = 0;
        for (const m of Object.values(tt.sub_agent_models) as any[]) {
          subIn += parseTokenString(m.input);
          subOut += parseTokenString(m.output);
          subCached += parseTokenString(m.cached);
        }
        subAgentInputTokens = subIn > 0 ? formatTokenCount(subIn) : undefined;
        subAgentOutputTokens = subOut > 0 ? formatTokenCount(subOut) : undefined;
        subAgentCachedTokens = subCached > 0 ? formatTokenCount(subCached) : undefined;
        subAgentCount = tt.sub_agent_count || 0;
      } else if (tt.sub_agent_total_tokens) {
        // Fallback: totalTokens from subagent.completed (no cache or output breakdown)
        subAgentInputTokens = tt.sub_agent_total_tokens;
        subAgentCount = tt.sub_agent_count || 0;
      }

      // Determine scenario path (best effort — check flat and nested group folders)
      const scenarioName = raw.scenario || "";
      const scenarioPath = resolveScenarioPath(scenarioName);

      // Determine status from metrics
      let status: import("../types.js").RunStatus = "done";
      if (m.timeout) status = "timeout";
      else if (raw.fail_reason || m.score === 0) status = "failed";
      else if (m.score !== undefined) status = "done";

      // Find pluginPath from agent name
      let pluginPath = "";
      const agentSetups = discoverAgentSetups();
      const match = agentSetups.find((c) => c.name === agentName);
      if (match) pluginPath = match.path;

      const entry: import("../types.js").RunEntry = {
        id: raw.trial || "unknown",
        scenario: scenarioName,
        scenarioPath,
        scenarioConfigName: scenarioName,
        condition: condition || condBase,
        pluginPath,
        model: raw.model || "unknown",
        trialName: raw.trial || "unknown",
        iteration,
        totalIterations,
        status,
        score: m.score,
        builds: m.builds,
        runs: m.runs,
        sessionTime: tt.session_time,
        apiTime: tt.api_time,
        codeChanges: tt.code_changes,
        inputTokens,
        outputTokens,
        cachedTokens,
        premiumRequests: tt.premium_requests,
        credits: tt.credits,
        subAgentInputTokens,
        subAgentOutputTokens,
        subAgentCachedTokens,
        subAgentCount,
        subAgentDetails: tt.sub_agent_details,
        failReason: raw.fail_reason ?? (m.score === 0 ? (m.timeout ? "Timeout" : !m.builds ? "Build failed" : !m.runs ? "App failed to run" : undefined) : undefined),
        currentOutput: "",
        startedAt: raw.timestamp ? new Date(raw.timestamp) : undefined,
        finishedAt: raw.timestamp ? new Date(raw.timestamp) : undefined,
        buildSessionId: raw.session_ids?.build,
        validationSessionId: raw.session_ids?.validation,
        ...(upstreamRef ? { upstreamRef } : {}),
      };

      entries.push(entry);
    } catch {
      // Skip unparseable results
    }
  }

  // ── Reconstruct missing entries from run-meta.json ──
  // If run-meta.json exists, rebuild the full matrix so incomplete runs
  // show "queued" entries that can be rerun from the dashboard.
  const metaPath = join(runDir, "run-meta.json");
  if (existsSync(metaPath)) {
    try {
      const meta = JSON.parse(readFileSync(metaPath, "utf-8"));
      const agentSetups = discoverAgentSetups();
      const loadedIds = new Set(entries.map(e => e.id));

      // ── v2 (flat) run-meta: reconstruct from the explicit `tasks[]` list ──
      // Folder = `{NNN}_{name}`, so the array index is authoritative. This is
      // the path for new runs and any hand-authored / scripted task lists.
      if (Array.isArray(meta.tasks) && meta.tasks.length > 0) {
        const defaults = meta.defaults || {};
        meta.tasks.forEach((t: any, i: number) => {
          const trialName = makeFlatTrialName(i, t.name ?? t.scenario ?? "task");
          if (loadedIds.has(trialName)) return; // already loaded from results.json
          const altId = `${t.scenario}/${trialName}`;
          if (loadedIds.has(altId)) return;
          const scenarioPath = resolveScenarioPath(t.scenario);
          const agentMatch = agentSetups.find(a => a.name === t.agent);
          const pluginPath = agentMatch?.path || "";
          const iter = typeof t.iteration === "number" ? t.iteration : 1;
          const totalIter = typeof t.totalIterations === "number" ? t.totalIterations : 1;
          const ref = t.upstreamRef ?? null;
          const iterLabel = totalIter > 1 ? ` [${iter}/${totalIter}]` : "";
          const condLabel = ref ? `${t.agent}@${ref}${iterLabel}` : t.agent + iterLabel;
          const trialDir = join(runDir, trialName);
          const hasTrialDir = existsSync(trialDir);
          entries.push({
            id: trialName,
            name: t.name,
            scenario: t.scenario,
            scenarioPath,
            scenarioConfigName: t.scenario,
            condition: condLabel,
            pluginPath,
            model: t.model ?? defaults.model ?? "",
            trialName,
            maxBuildMinutes: t.maxBuildMinutes ?? defaults.maxBuildMinutes,
            skipValidation: t.skipValidation ?? defaults.skipValidation,
            iteration: iter,
            totalIterations: totalIter,
            status: hasTrialDir ? "interrupted" : "queued",
            failReason: hasTrialDir ? "Interrupted (no results)" : undefined,
            currentOutput: "",
            ...(ref ? { upstreamRef: ref } : {}),
          });
        });
        return { entries, runName };
      }

      // ── Legacy run-meta: rebuild the (scenario × agent × model × iter) matrix ──
      const scenarios: string[] = Array.isArray(meta.scenarios) ? meta.scenarios : [];
      const agents: string[] = Array.isArray(meta.agents) ? meta.agents : [];
      const models: string[] = Array.isArray(meta.models) ? meta.models : [];
      const iterations: number = typeof meta.iterations === "number" ? meta.iterations : 1;
      // Track loaded combinations by (scenario, condition/ref, model, iter) so
      // old runs (which used hash-based trial names) match the new naming scheme.
      const normalizedCondition = (condition: string) =>
        condition
          .replace(/\s*\[\d+\/\d+\]$/, "")
          .replace(/^agentsetup-/, "");
      const loadedComboKey = (scenario: string, condition: string, model: string, iter: number) =>
        `${scenario}::${condition}::${model}::${iter}`;
      const loadedCombos = new Set(
        entries.map(e =>
          loadedComboKey(
            e.scenario,
            normalizedCondition(e.condition || ""),
            e.model,
            e.iteration || 1
          )
        )
      );

      const refsForAgent = (agentName: string): (string | null)[] => {
        const configured = Array.isArray(meta.upstreamRefs)
          ? meta.upstreamRefs
          : meta.upstreamRefs?.[agentName];
        if (Array.isArray(configured)) {
          const refs = Array.from(
            new Set(configured.filter((ref: unknown): ref is string =>
              typeof ref === "string" && ref.length > 0
            ))
          );
          if (refs.length > 0) return refs;
        }

        const prefix = `${agentName}@`;
        const observed = Array.from(
          new Set(
            entries
              .map((entry) => normalizedCondition(entry.condition || ""))
              .filter((condition) => condition.startsWith(prefix))
              .map((condition) => condition.slice(prefix.length))
              .filter(Boolean)
          )
        );
        if (observed.length > 0) return observed;

        const agentMatch = agentSetups.find((agent) => agent.name === agentName);
        const fromRepo = agentMatch?.config?.from_repo;
        return fromRepo ? [fromRepo.default_ref || "main"] : [null];
      };

      for (let iter = 1; iter <= iterations; iter++) {
        for (const scenarioName of scenarios) {
          for (const agentName of agents) {
            for (const model of models) {
              for (const ref of refsForAgent(agentName)) {
                const conditionBase = ref ? `${agentName}@${ref}` : agentName;
                if (
                  loadedCombos.has(
                    loadedComboKey(scenarioName, conditionBase, model, iter)
                  )
                ) {
                  continue;
                }

                const trialName = makeTrialName(
                  scenarioName,
                  agentName,
                  model,
                  iter,
                  ref
                );
                const id = `${scenarioName}/${trialName}`;
                if (loadedIds.has(id)) continue;
                const scenarioPath = resolveScenarioPath(scenarioName);
                const agentMatch = agentSetups.find(a => a.name === agentName);
                const pluginPath = agentMatch?.path || "";
                const iterLabel = iterations > 1 ? ` [${iter}/${iterations}]` : "";

                // Check if trial directory exists (partially completed)
                const trialDir = join(runDir, trialName);
                const hasTrialDir = existsSync(trialDir);

                entries.push({
                  id,
                  scenario: scenarioName,
                  scenarioPath,
                  scenarioConfigName: scenarioName,
                  condition: conditionBase + iterLabel,
                  pluginPath,
                  model,
                  trialName,
                  iteration: iter,
                  totalIterations: iterations,
                  status: hasTrialDir ? "failed" : "queued",
                  failReason: hasTrialDir ? "Interrupted (no results)" : undefined,
                  currentOutput: "",
                  ...(ref ? { upstreamRef: ref } : {}),
                });
              }
            }
          }
        }
      }
    } catch {
      // If run-meta.json is unparseable, just use the results.json entries
    }
  }

  return { entries, runName };
}

// =============================================================================
// Script Resolution (Feature: Agent Setup Scripts)
// =============================================================================

/** Preset setup scripts (`preset_scripts` in an agent config): `presets/<name>/action.ps1`. */
export const scriptsDir = join(benchRoot, "presets");

/**
 * Resolve the entry-point script file for a script subfolder under presets/.
 * On Windows, looks for .ps1 files; on macOS/Linux, looks for .sh files (then .ps1 as fallback).
 * Returns the absolute path to the entry-point script.
 * Throws with a descriptive message if the script cannot be resolved.
 */
export function resolveScriptEntryPoint(scriptName: string): string {
  const scriptFolder = join(scriptsDir, scriptName);
  if (!existsSync(scriptFolder) || !statSync(scriptFolder).isDirectory()) {
    throw new Error(`Setup script folder not found: presets/${scriptName}`);
  }

  const isWin = platform() === "win32";
  const primaryExt = isWin ? ".ps1" : ".sh";
  const fallbackExt = isWin ? ".sh" : ".ps1";

  let scriptFiles = readdirSync(scriptFolder).filter(
    (f) => f.endsWith(primaryExt) && statSync(join(scriptFolder, f)).isFile()
  );

  // Fallback to other extension if no primary scripts found
  if (scriptFiles.length === 0) {
    scriptFiles = readdirSync(scriptFolder).filter(
      (f) => f.endsWith(fallbackExt) && statSync(join(scriptFolder, f)).isFile()
    );
  }

  if (scriptFiles.length === 0) {
    throw new Error(
      `No ${primaryExt} or ${fallbackExt} file found in presets/${scriptName}/`
    );
  }

  if (scriptFiles.length === 1) {
    return join(scriptFolder, scriptFiles[0]);
  }

  // Multiple script files - look for well-known names
  const wellKnownBases = ["action", "setup", "run"];
  for (const base of wellKnownBases) {
    for (const ext of [primaryExt, fallbackExt]) {
      const candidate = `${base}${ext}`;
      if (scriptFiles.includes(candidate)) {
        return join(scriptFolder, candidate);
      }
    }
  }

  throw new Error(
    `Multiple script files in presets/${scriptName}/ and none named action/setup/run${primaryExt}`
  );
}

/**
 * Validate all script references for an agent setup config.
 * Returns resolved entries with absolute paths and timeouts.
 * Throws on first invalid reference.
 */
export function validateAgentSetupScripts(
  agentSetupName: string,
  scripts: ScriptEntry[]
): Array<{ name: string; entryPoint: string; timeoutMinutes: number; scriptDir: string }> {
  return scripts.map((entry) => {
    const name = typeof entry === "string" ? entry : entry.name;
    const timeoutMinutes =
      typeof entry === "object" && entry.timeout_minutes
        ? entry.timeout_minutes
        : 5;
    const entryPoint = resolveScriptEntryPoint(name);
    return {
      name,
      entryPoint,
      timeoutMinutes,
      scriptDir: join(scriptsDir, name),
    };
  });
}
