import { fork, spawn, spawnSync } from "child_process";
import { AsyncLocalStorage } from "async_hooks";
import { homedir, arch as osArch } from "os";
import { createHash, randomBytes } from "crypto";
import { CopilotClient, approveAll } from "@github/copilot-sdk";
import type { CustomAgentConfig } from "@github/copilot-sdk";
import {
  existsSync,
  mkdirSync,
  writeFileSync,
  appendFileSync,
  readFileSync,
  readdirSync,
  rmSync,
  copyFileSync,
  statSync,
  lstatSync,
  renameSync,
  symlinkSync,
  unlinkSync,
} from "fs";
import { basename, dirname, join, resolve } from "path";
import { fileURLToPath } from "url";
import {
  agentsDir,
  benchRoot,
  repoRoot,
  resultsRoot,
  getJudgeModel,
  loadGlobalConfig,
  loadPrompt,
  loadScenario,
  loadValidationPrompt,
  validateAgentSetupScripts,
} from "./config.js";
import { parseTokenString } from "./format.js";
import { resolveRepo } from "./repo-cache.js";
import { agentNameOf, findMissingSkills, resolvePluginLayout } from "./plugin-layout.js";
import {
  collectProjectFiles,
  evaluateAccessibleNames,
  evaluateBuildWarnings,
  evaluateKeyboardTraversal,
  evaluateNativeAot,
  evaluateSourceChecks,
  interactiveElements,
  judgeChecks,
  projectDirFor,
  type QualityCheck,
} from "./quality-checks.js";
import { computeScoreV2, formatBreakdown, type ScoreBreakdown } from "./scoring.js";
import { pinRef, readPin } from "./upstream-pins.js";
import { findAncestorProjectFiles, hideToolsFromPath } from "./trial-env.js";
import {
  COMBINED_PATH_MAX,
  USER_PATH_MAX,
  devPackages,
  diffEnvValues,
  manifestIdentities,
  pathLengths,
  longPathsEnabled,
  removePackages,
  removeUserPathEntries,
  snapshotUserEnv,
  trialPackages,
  userPathRaw,
  type UserEnvSnapshot,
} from "./machine-guard.js";
import { resolveCopilotCliPath } from "./copilot-cli.js";
import { appendBounded, PROCESS_OUTPUT_MAX_CHARS } from "./live-output.js";
import { runUntilSdkCompletion, type SdkCompletionReason } from "./sdk-lifecycle.js";
import { compareVersionCore, resolveWinAppToolchain } from "./winapp-toolchain.js";
import { readLinesSync } from "./jsonl-stream.js";
import { evaluateConcurrentProcessCommand, extractCommand } from "./process-safety.js";
import type { RunEntry, ScenarioConfig, AgentSetupConfig, GlobalConfig } from "../types.js";
import { parse as parseYaml } from "yaml";
import { platform } from "os";

const isWindows = platform() === "win32";

function writeTextAtomic(path: string, content: string): void {
  const temp = `${path}.${process.pid}.${Date.now()}.tmp`;
  writeFileSync(temp, content);
  renameSync(temp, path);
}

/**
 * Resolve `copilot` to its absolute path once at startup.
 *
 * `copilot.exe` on Windows is a Node SEA (Single Executable Application). Its
 * bootstrap reads `process.argv[0]` to locate the embedded payload inside the
 * .exe. When Node's `spawn("copilot", args, { shell: false })` is used, libuv
 * resolves the binary via PATH for `CreateProcess.lpApplicationName` (so the
 * process starts), but passes the bare string `"copilot"` as `argv[0]` to the
 * child. The SEA bootstrap then can't find its payload and silently exits with
 * code 1 within ~1 second — no stdout, no stderr, no log file. `--version`
 * works because it's parsed before the SEA payload is needed; `-p ...
 * --output-format json` triggers full session init and crashes.
 *
 * Fix: resolve to the absolute path so the child gets a usable `argv[0]`.
 * Falls back to `"copilot"` on non-Windows or if `where`/`which` fails.
 */
const COPILOT_EXE: string = (() => {
  try {
    const cmd = isWindows ? "where" : "which";
    const r = spawnSync(cmd, ["copilot"], { encoding: "utf-8", shell: false });
    if (r.status === 0 && r.stdout) {
      const first = r.stdout.split(/\r?\n/).map((s) => s.trim()).find(Boolean);
      if (first && existsSync(first)) return first;
    }
  } catch {
    /* fall through */
  }
  return "copilot";
})();

/** Kill a process tree by PID — cross-platform. */
function killProcessTree(pid: number, force = false): void {
  if (isWindows) {
    const args = ["/PID", String(pid), "/T"];
    if (force) args.push("/F");
    spawn("taskkill", args, { shell: true });
  } else {
    // On macOS/Linux, kill the process group (negative PID)
    try { process.kill(-pid, force ? "SIGKILL" : "SIGTERM"); } catch {}
    // Fallback: kill the individual process
    try { process.kill(pid, force ? "SIGKILL" : "SIGTERM"); } catch {}
  }
}

function readWindowsProcessStartTime(pid: number): number | undefined {
  if (!isWindows || !Number.isInteger(pid) || pid <= 0) return undefined;
  const result = spawnSync(
    "powershell.exe",
    [
      "-NoProfile",
      "-NonInteractive",
      "-Command",
      `(Get-Process -Id ${pid} -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o')`,
    ],
    { encoding: "utf8", windowsHide: true, timeout: 10_000 }
  );
  if (result.status !== 0) return undefined;
  const startedAt = Date.parse(result.stdout.trim());
  return Number.isFinite(startedAt) ? startedAt : undefined;
}

/**
 * Module-level tracker of every PID we spawn during a benchmark run.
 * Lets `runner-bridge.cancel()` actually terminate in-flight subprocesses
 * (copilot, dotnet build, winapp launches) instead of just preventing the
 * queue from picking up the next entry.
 */
const trackedPids = new Set<number>();
const packageLockTails = new Map<string, Promise<void>>();

async function acquirePackageLock(identity: string): Promise<() => void> {
  const previous = packageLockTails.get(identity) ?? Promise.resolve();
  let releaseCurrent!: () => void;
  const current = new Promise<void>((resolve) => {
    releaseCurrent = resolve;
  });
  packageLockTails.set(identity, current);
  await previous;
  let released = false;
  const safetyTimer = setTimeout(() => release(), 90 * 60 * 1000);
  const release = () => {
    if (released) return;
    released = true;
    clearTimeout(safetyTimer);
    releaseCurrent();
    if (packageLockTails.get(identity) === current) packageLockTails.delete(identity);
  };
  return release;
}

function packageIdentityForProject(csproj: string): string | null {
  const projectDir = dirname(csproj);
  const manifest = ["Package.appxmanifest", "appxmanifest.xml"]
    .map((name) => join(projectDir, name))
    .find((candidate) => existsSync(candidate));
  if (!manifest) return null;
  try {
    const xml = readFileSync(manifest, "utf8");
    return xml.match(/<Identity\b[^>]*\bName="([^"]+)"/i)?.[1] ?? null;
  } catch {
    return null;
  }
}

function trackPid(pid: number | undefined): number | undefined {
  if (pid) trackedPids.add(pid);
  return pid;
}
function untrackPid(pid: number | undefined): void {
  if (pid) trackedPids.delete(pid);
}

/** Kill every still-running tracked process (force). Called on user cancel. */
export function killAllTrackedProcesses(): void {
  const pids = Array.from(trackedPids);
  trackedPids.clear();
  for (const pid of pids) {
    killProcessTree(pid, true);
  }
}

/** Kill a process by name — cross-platform. */
function killProcessByName(name: string, force = false): Promise<void> {
  if (isWindows) {
    const exeName = name.endsWith(".exe") ? name : `${name}.exe`;
    return runProcess("taskkill", ["/IM", exeName, "/F"], ".", () => {}, 5000).then(() => {});
  } else {
    const signal = force ? "SIGKILL" : "SIGTERM";
    return runProcess("pkill", [force ? "-9" : "-15", "-x", name], ".", () => {}, 5000).then(() => {});
  }
}

/**
 * Force-kill an app launched by a trial. Used both at trial start (clear stale
 * instances) and at trial end (don't leave the user with a dozen windows open).
 *
 * Windows cleanup must be trial-scoped: concurrent scenarios commonly produce
 * the same executable name, so name/prefix package cleanup would kill another
 * trial. Use captured PID and executable path under this trial's workDir only.
 */
async function killAppProcesses(opts: {
  appPid?: string;
  appStartedAt?: number;
  appName: string;
  baseAppName?: string;
  expandedLaunchDetect?: string;
  workDir: string;
  uninstallAppx?: boolean;
}): Promise<void> {
  const { appPid, appStartedAt, appName, baseAppName, expandedLaunchDetect, workDir } = opts;

  // 1. Kill the PID we captured at launch (most reliable for packaged apps
  // whose process name may be truncated).
  if (appPid) {
    const pid = parseInt(appPid, 10);
    if (!isNaN(pid)) {
      const mayKill = !isWindows || (
        appStartedAt !== undefined
        && readWindowsProcessStartTime(pid) === appStartedAt
      );
      if (mayKill) {
        try { killProcessTree(pid, true); } catch {}
      }
    }
  }

  // 2. macOS: try a graceful quit first so AppKit cleans up cleanly.
  if (!isWindows) {
    try {
      await runProcess("osascript", ["-e", `tell application "${appName}" to quit`], ".", () => {}, 5000);
      await new Promise(r => setTimeout(r, 2000));
    } catch {}
  }

  if (!isWindows) {
    // Non-Windows launchers do not currently expose reliable executable paths,
    // so preserve the existing name-based fallback there.
    try { await killProcessByName(appName, true); } catch {}
    if (baseAppName && baseAppName !== appName) {
      try { await killProcessByName(baseAppName, true); } catch {}
    }
    if (expandedLaunchDetect && expandedLaunchDetect !== appName && expandedLaunchDetect !== baseAppName) {
      try {
        await runProcess("osascript", ["-e", `tell application "${expandedLaunchDetect}" to quit`], ".", () => {}, 5000);
        await new Promise(r => setTimeout(r, 2000));
      } catch {}
      try { await killProcessByName(expandedLaunchDetect, true); } catch {}
    }
  }

  if (isWindows) {
    // The agent may rename the executable (e.g. "TabNotepad" instead of
    // "TextEditorwinui3-base1"). Kill any process whose executable path is
    // inside the trial workDir — that's a guaranteed-good identifier.
    // PowerShell single-quoted strings don't interpret backslash escapes, so
    // we only escape embedded single quotes — backslashes are left as-is.
    try {
      const escapedDir = workDir.replace(/'/g, "''");
      await runProcess(
        "powershell", ["-NoProfile", "-Command",
          `Get-Process | Where-Object { try { $_.Path -and $_.Path.StartsWith('${escapedDir}', [System.StringComparison]::OrdinalIgnoreCase) } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue`],
        ".", () => {}, 10000, false
      );
    } catch {}
  }
}

/** Check if a macOS app is running by name. */
async function isMacAppRunning(appName: string, log: (msg: string) => void): Promise<boolean> {
  const result = await runProcess("pgrep", ["-x", appName], ".", () => {}, 5000);
  return result.exitCode === 0;
}

// Parse YAML frontmatter from a section .md file
function parseSectionDeps(sectionFile: string): { skills?: string[]; inline_skills?: string[]; mcp?: string[] } {
  if (!existsSync(sectionFile)) return {};
  const raw = readFileSync(sectionFile, "utf-8").replace(/\r\n/g, "\n");
  const fmMatch = raw.match(/^---\s*\n([\s\S]*?)\n---/);
  if (!fmMatch) return {};
  try { return parseYaml(fmMatch[1]) || {}; } catch { return {}; }
}

/** Recursively find files matching a filename pattern (simple glob: just the filename part). */
function findFilesRecursive(dir: string, pattern: string): string[] {
  const results: string[] = [];
  if (!existsSync(dir)) return results;
  try {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const fullPath = join(dir, entry.name);
      if (entry.isDirectory() && !entry.name.startsWith(".") && entry.name !== "node_modules" && entry.name !== "bin" && entry.name !== "obj") {
        results.push(...findFilesRecursive(fullPath, pattern));
      } else if (entry.isFile()) {
        // Simple wildcard match
        const asteriskCount = (pattern.match(/\*/g) || []).length;
        if (asteriskCount === 1 && pattern.startsWith("*")) {
          // Pattern like *.xaml or *.cs — match by extension
          const ext = pattern.slice(1);
          if (entry.name.endsWith(ext)) results.push(fullPath);
        } else if (asteriskCount > 0) {
          // Pattern like *ViewModel*.cs — all non-wildcard parts must appear in order
          const parts = pattern.split("*").filter(Boolean);
          if (parts.every(p => entry.name.includes(p))) results.push(fullPath);
        } else {
          if (entry.name === pattern) results.push(fullPath);
        }
      }
    }
  } catch { /* permission errors etc */ }
  return results;
}


// ─────────────────────────────────────────────────────────────────────────────
// NuGet-cache resolution & per-trial PATH augmentation
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Expand variables in path-like config strings:
 *   ${HOME}        → user's home directory (always)
 *   ${arch}        → lowercase process.arch ("arm64", "x64", ...)
 *   ${pkg:<id>}    → root of the latest restored version of `<id>` under
 *                    ~/.nuget/packages/<id>/. Empty if the package hasn't been
 *                    restored (the caller's existsSync filter drops empties).
 *   ${VARNAME}     → any other token resolves from process.env (case-sensitive
 *                    on the original casing). Unset env vars resolve to "".
 */
function expandConfigVars(s: string): string {
  return s
    .replace(/\$\{pkg:([^}]+)\}/g, (_, id: string) => resolveNugetPackageRoot(id.trim()) || "")
    .replace(/\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (_, name: string) => {
      if (name === "HOME") return homedir();
      if (name === "arch") return osArch();
      return resolveEnvVar(name);
    });
}

/**
 * Read an env var, preferring the Windows user-scope environment
 * (HKCU\Environment) over the inherited process.env. This makes TOOL_HOME (and
 * other `setx`-persisted vars) "just work" even when:
 *   (a) the harness was launched from a shell that doesn't have them — e.g.
 *       an editor or background agent that started before the var was
 *       persisted (registry is the only source), OR
 *   (b) the harness inherited a STALE value from a long-running shell that
 *       predates a more recent `setx` (registry is fresher than process.env).
 *
 * Case (b) is the trickier one and the reason registry wins on Windows: a
 * user who runs `setx TOOL_HOME <new>` inside a terminal and then triggers
 * a trial in the same terminal would otherwise silently keep using the old
 * tool.exe because `setx` only updates the registry, not the running process.
 *
 * Linux/macOS: no equivalent durable user-scope store — process.env is the
 * only source.
 */
const userEnvCache = new Map<string, string>();
function resolveEnvVar(name: string): string {
  if (isWindows) {
    const reg = readRegistryEnv(name);
    if (reg) {
      process.env[name] = reg; // hoist so child processes inherit
      return reg;
    }
  }
  return process.env[name] ?? "";
}
function readRegistryEnv(name: string): string {
  if (userEnvCache.has(name)) return userEnvCache.get(name) || "";
  let value = "";
  try {
    const r = spawnSync("reg", ["query", "HKCU\\Environment", "/v", name], { encoding: "utf-8" });
    if (r.status === 0) {
      // Output: `    TOOL_HOME    REG_SZ    C:\path\to\dir`
      const m = r.stdout.match(/REG_(?:SZ|EXPAND_SZ)\s+(.+?)\s*$/m);
      if (m) value = m[1].trim();
    }
  } catch { /* reg.exe missing — never happens on Windows */ }
  userEnvCache.set(name, value);
  return value;
}
/**
 * Resolve the latest restored version directory for a NuGet package id.
 * Returns null if the package has not been restored.
 */
function resolveNugetPackageRoot(packageId: string): string | null {
  const cacheRoot = join(homedir(), ".nuget", "packages", packageId.toLowerCase());
  if (!existsSync(cacheRoot)) return null;
  let versions: string[];
  try {
    versions = readdirSync(cacheRoot).filter((d) =>
      statSync(join(cacheRoot, d)).isDirectory()
    );
  } catch {
    return null;
  }
  if (versions.length === 0) return null;
  // Sort version-aware: chunks of digits compared numerically; falls back to lexical.
  versions.sort((a, b) => {
    const pa = a.split(/[.\-]/);
    const pb = b.split(/[.\-]/);
    const len = Math.max(pa.length, pb.length);
    for (let i = 0; i < len; i++) {
      const sa = pa[i] ?? "";
      const sb = pb[i] ?? "";
      const na = parseInt(sa, 10);
      const nb = parseInt(sb, 10);
      if (!isNaN(na) && !isNaN(nb) && na !== nb) return na - nb;
      if (sa !== sb) return sa < sb ? -1 : 1;
    }
    return 0;
  });
  return join(cacheRoot, versions[versions.length - 1]);
}

/**
 * Search every directory of a PATH string for an executable. Returns the
 * absolute path of the first match, or null.
 */
function findOnPath(tool: string, pathStr: string): string | null {
  if (!tool) return null;
  const sep = isWindows ? ";" : ":";
  const candidates = isWindows
    ? (tool.toLowerCase().endsWith(".exe") ? [tool] : [tool, `${tool}.exe`, `${tool}.cmd`, `${tool}.bat`])
    : [tool];
  for (const dir of pathStr.split(sep)) {
    if (!dir) continue;
    for (const name of candidates) {
      const full = join(dir, name);
      // App Execution Aliases (e.g. winapp.exe in %LOCALAPPDATA%\Microsoft\
      // WindowsApps) are reparse points to packaged apps. `existsSync` and
      // `statSync` both fail (EACCES on stat) because the target dir is
      // protected, but `lstatSync` correctly reports them as symlinks. Treat
      // any lstat-visible reparse point on Windows as a found tool — if it's
      // really broken, the later exec will surface that error.
      try {
        const lst = lstatSync(full);
        if (lst.isFile()) return full;
        if (isWindows && lst.isSymbolicLink()) return full;
      } catch { /* ENOENT — try the next candidate */ }
    }
  }
  return null;
}

/** Combine package root + subpath, returning null if either doesn't resolve. */
function resolvePackageSubpath(pkg: string, subpath: string): string | null {
  const root = resolveNugetPackageRoot(pkg);
  if (!root) return null;
  const full = join(root, subpath);
  return existsSync(full) ? full : null;
}

interface TrialContext {
  pathPrepend?: string;
  uiWorkflowId?: string;
  /** Extra environment for every child process of the trial (e.g. DOTNET_CLI_HOME). */
  env?: Record<string, string>;
  /** Per-trial scratch root (dotnet home, PATH shims); removed when the trial ends. */
  envRoot?: string;
  /** Tools hidden from PATH for this trial (agent config `env.hide_tools`). */
  hiddenTools?: { tools: string[]; removedDirs: string[]; shims: number };
}
const trialContext = new AsyncLocalStorage<TrialContext>();

/**
 * Per-trial environment (reports/plans/11, A6): a fresh `DOTNET_CLI_HOME`
 * (no preinstalled templates or global .NET tools) for every trial, and the
 * agent config's `env.hide_tools` removed from PATH (e.g. `winapp` for bare).
 */
export function createTrialEnv(runDir: string, trialName: string, cfg: AgentSetupConfig): TrialContext {
  const envRoot = isWindows
    ? join(process.env.LOCALAPPDATA || homedir(), "win-dev-bench", "trial-env",
        `${basename(runDir)}-${trialName.slice(0, 3)}-${randomBytes(4).toString("hex")}`)
    : join(runDir, trialName, ".trial-env");
  const dotnetHome = join(envRoot, "dotnet");
  const isolate = cfg.env?.isolate_dotnet_home !== false;
  if (isolate) mkdirSync(dotnetHome, { recursive: true });
  else mkdirSync(envRoot, { recursive: true });
  const ctx: TrialContext = {
    envRoot,
    env: {
      ...(isolate ? { DOTNET_CLI_HOME: dotnetHome } : {}),
      DOTNET_NOLOGO: "1",
      DOTNET_GENERATE_ASPNET_CERTIFICATE: "false",
      // A new DOTNET_CLI_HOME triggers the SDK's first-run setup, which
      // otherwise appends `<DOTNET_CLI_HOME>\.dotnet\tools` to the *registry*
      // user PATH. One entry per trial overflowed PATH after ~330 trials and
      // broke Windows sign-in (see machine-guard.ts).
      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
    },
  };
  ctx.pathPrepend = computeTrialPath(cfg, ctx);
  // Run apps on the same .NET install that builds them. A user-level
  // DOTNET_ROOT pointing at another install (e.g. ~\.dotnet with fewer
  // runtimes) otherwise makes framework-dependent apps show "You must install
  // .NET" at launch, which is machine noise rather than an agent failure.
  const dotnet = findOnPath("dotnet", ctx.pathPrepend ?? process.env.PATH ?? "");
  if (dotnet) ctx.env!.DOTNET_ROOT = dirname(dotnet);
  return ctx;
}

/** PATH for the trial: `env.path_prepend` first, then `env.hide_tools` applied. */
function computeTrialPath(cfg: AgentSetupConfig, ctx: TrialContext): string | undefined {
  let path = cfg.env?.path_prepend ? buildPrependedPath(cfg.env.path_prepend) || undefined : undefined;
  const tools = cfg.env?.hide_tools?.filter(Boolean) ?? [];
  if (tools.length && ctx.envRoot) {
    const hidden = hideToolsFromPath(path ?? process.env.PATH ?? "", tools, join(ctx.envRoot, "shims"), pathSeparator());
    ctx.hiddenTools = { tools, removedDirs: hidden.removedDirs, shims: hidden.shims.length };
    path = hidden.path;
  }
  return path;
}

export function removeTrialEnv(ctx: TrialContext | undefined): void {
  if (!ctx?.envRoot) return;
  try { rmSync(ctx.envRoot, { recursive: true, force: true, maxRetries: 3, retryDelay: 250 }); } catch { /* best effort */ }
}

/**
 * Undo persistent machine changes a trial may have made (machine-guard.ts):
 * trial folders in the registry user PATH, development-mode packages
 * registered from the trial, and report other HKCU\Environment changes.
 * Recorded as `machine` in results.json. Never throws.
 */
async function restoreMachineState(
  trialDir: string,
  before: UserEnvSnapshot | null,
  onOutput: (data: string) => void,
): Promise<void> {
  if (!isWindows) return;
  const log = (m: string) => { try { onOutput(`${m}\n`); } catch { /* ignore */ } };
  try {
    const pathRemoved = removeUserPathEntries();
    if (pathRemoved.length) {
      log(`  ⚠️  Machine: removed ${pathRemoved.length} trial folder(s) from the user PATH (${pathRemoved.slice(0, 2).join("; ")}${pathRemoved.length > 2 ? "; …" : ""})`);
    }
    const identities = manifestIdentities(trialDir);
    const packagesRemoved: string[] = [];
    if (identities.length) {
      for (const pkg of trialPackages(devPackages(), identities, [trialDir, "C:\\winapp-stage"])) {
        const release = await acquirePackageLock(pkg.name);
        try { packagesRemoved.push(...removePackages([pkg])); } finally { release(); }
      }
      if (packagesRemoved.length) log(`  🧹 Machine: unregistered ${packagesRemoved.length} development package(s) from this trial`);
    }
    const after = snapshotUserEnv();
    const envChanges = before && after ? diffEnvValues(before.values, after.values) : [];
    if (envChanges.length) log(`  ⚠️  Machine: user environment variable(s) changed during the trial: ${envChanges.join(", ")}`);
    const resultsPath = join(trialDir, "results.json");
    if (existsSync(resultsPath)) {
      const results = JSON.parse(readFileSync(resultsPath, "utf-8"));
      results.machine = {
        user_path_chars: { before: userPathRaw(before).length, after: userPathRaw(after).length },
        path_entries_removed: pathRemoved,
        packages_removed: packagesRemoved,
        env_changes: envChanges,
      };
      writeFileSync(resultsPath, JSON.stringify(results, null, 2));
    }
  } catch (e) {
    log(`  ⚠️  Machine cleanup failed: ${e instanceof Error ? e.message : String(e)}`);
  }
}

/**
 * Apply the trial context to an environment: PATH (replacing every
 * case-variant key, since Windows env names are case-insensitive but a JS
 * object keeps `Path` and `PATH` apart), extra env vars, and the cooperative
 * UI workflow id. `keepPath` lets a caller's explicit PATH win.
 */
export function applyTrialEnv(
  base: NodeJS.ProcessEnv,
  ctx: Pick<TrialContext, "pathPrepend" | "env" | "uiWorkflowId"> | undefined,
  keepPath = false,
): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...base };
  if (ctx?.pathPrepend && !keepPath) {
    for (const k of Object.keys(env)) if (k.toLowerCase() === "path") delete env[k];
    env.PATH = ctx.pathPrepend;
  }
  if (ctx?.env) Object.assign(env, ctx.env);
  if (ctx?.uiWorkflowId) env.WINAPP_UI_WORKFLOW_ID = ctx.uiWorkflowId;
  return env;
}

function uiWorkflowIdFor(runDir: string, entryId: string): string {
  const identity = `${process.pid}|${resolve(runDir)}|${entryId}`;
  return `benchmark-${createHash("sha256").update(identity).digest("hex").slice(0, 32)}`;
}

function pathSeparator(): string { return isWindows ? ";" : ":"; }

/** Build a PATH string with `prepend` paths in front; `null` if no augmentation needed. */
function buildPrependedPath(prepend: string[]): string | null {
  const filtered = prepend.map((p) => expandConfigVars(p)).filter((p) => p && existsSync(p));
  if (filtered.length === 0) return null;
  const sep = pathSeparator();
  return filtered.join(sep) + sep + (process.env.PATH || "");
}

/**
 * Result row for one precheck (path tool OR required env var).
 * `label` is a short identifier suitable for logs and fail_reason strings.
 */
export interface PrecheckResult {
  ok: boolean;
  /** Short ID, e.g. "tool.exe", "env:TOOL_HOME" */
  label: string;
  /** Where the resource was found (when ok=true) */
  detail?: string;
  guidance?: string;
}

/**
 * Run every precheck declared by an agent config against the supplied PATH.
 * Used by both the sweep-level preflight and the per-trial check.
 *
 * Why two layers: preflight catches misconfiguration before any trial runs
 * (the typical case — missing TOOL_HOME on a fresh machine). The per-trial
 * check guards against the edge cases where preflight was skipped (e.g.
 * single-trial dashboard launch) or where a preset_script changed the env
 * inconsistently.
 */
export function checkAgentPrechecks(
  cfg: AgentSetupConfig,
  trialPath: string
): PrecheckResult[] {
  const results: PrecheckResult[] = [];
  for (const c of cfg.prechecks?.required_on_path ?? []) {
    const found = findOnPath(c.tool, trialPath);
    if (!found) {
      results.push({ ok: false, label: c.tool, guidance: c.guidance });
      continue;
    }
    if (!c.verify) {
      results.push({ ok: true, label: c.tool, detail: found });
      continue;
    }
    // Run the tool against the trial PATH so any sibling binaries it needs
    // resolve the same way they will at trial time.
    const args = c.verify.args ?? ["--help"];
    const probe = spawnSync(found, args, {
      env: applyTrialEnv(process.env, { pathPrepend: trialPath }),
      encoding: "utf8",
      windowsHide: true,
      timeout: 10_000,
    });
    const out = `${probe.stdout ?? ""}\n${probe.stderr ?? ""}`;
    const needle = c.verify.stdout_contains;
    const passes = needle ? out.toLowerCase().includes(needle.toLowerCase()) : probe.status === 0;
    if (passes) {
      results.push({ ok: true, label: c.tool, detail: found });
    } else {
      const why = needle
        ? `${c.tool} found at ${found}, but \`${[c.tool, ...args].join(" ")}\` output did not contain "${needle}". The binary is likely stale (e.g. TOOL_HOME points at an older build that predates this subcommand).`
        : `${c.tool} found at ${found}, but \`${[c.tool, ...args].join(" ")}\` exited with status ${probe.status}.`;
      results.push({
        ok: false,
        label: c.tool,
        guidance: `${why}\n${c.verify.guidance ?? c.guidance ?? ""}`.trim(),
      });
    }
  }
  for (const e of cfg.prechecks?.required_env ?? []) {
    // resolveEnvVar() hoists user-scope registry values into process.env on
    // Windows, so children inherit them automatically.
    const value = resolveEnvVar(e.name);
    if (!value) {
      results.push({ ok: false, label: `env:${e.name}`, guidance: e.guidance });
    } else if (e.must_exist_path && !existsSync(value)) {
      results.push({ ok: false, label: `env:${e.name}`, guidance:
        `${e.name} is set to "${value}" but that path does not exist.\n${e.guidance ?? ""}` });
    } else {
      results.push({ ok: true, label: `env:${e.name}`, detail: value });
    }
  }
  return results;
}

/**
 * Aggregate preflight across every unique agent in the queued entries. Runs
 * once before BenchmarkQueue.start() so a sweep that includes a broken agent
 * fails before any trial folder is created — the user's "do zero work if
 * anything is missing" guarantee.
 *
 * Crucially: this checks *every* selected agent. A mixed (e.g. two different toolchains)
 * sweep will not run any agent's trials if another agent's deps are missing, and vice
 * versa. That's the contract — half-finished runs caused by one broken
 * dependency are not a useful comparison.
 */
export interface PreflightAgent {
  name: string;
  pluginPath: string;
}
export interface PreflightFailure {
  agent: string;
  label: string;
  guidance?: string;
}
export function runPreflight(agents: PreflightAgent[]): {
  ok: boolean;
  failures: PreflightFailure[];
  log: string[];
} {
  const log: string[] = [];
  const failures: PreflightFailure[] = [];
  // A project/solution file above the results folder makes every `dotnet new`
  // (template install/list) walk up to it and glob that whole tree — minutes
  // per trial when it sits in the user profile.
  const strayProjects = findAncestorProjectFiles(resultsRoot);
  if (strayProjects.length) {
    const label = `stray project files above the results folder: ${strayProjects.slice(0, 3).join(", ")}`;
    log.push(`  ❌ harness: ${label}`);
    failures.push({
      agent: "harness",
      label,
      guidance: "Move or delete them. They make `dotnet new` scan the whole parent folder, adding minutes to every trial.",
    });
  } else {
    log.push("  ✅ harness: no stray project files above the results folder");
  }
  if (isWindows) {
    // Machine hygiene (machine-guard.ts): never start a run that could push
    // the user PATH toward Windows' 32,767-character limit.
    const removed = removeUserPathEntries();
    if (removed.length) {
      log.push(`  ⚠️  harness: removed ${removed.length} leftover trial folder(s) from the user PATH`);
    }
    const { user, machine } = pathLengths();
    if (user > USER_PATH_MAX || user + machine > COMBINED_PATH_MAX) {
      const label = `user PATH is ${user} characters (machine ${machine}); limits ${USER_PATH_MAX} user / ${COMBINED_PATH_MAX} combined`;
      log.push(`  ❌ harness: ${label}`);
      failures.push({
        agent: "harness",
        label,
        guidance: "Remove stale entries from your user PATH (Settings > System > About > Advanced system settings > Environment Variables) before running. Windows breaks above 32,767 characters.",
      });
    } else {
      log.push(`  ✅ harness: user PATH ${user} characters (machine ${machine})`);
    }
    if (longPathsEnabled()) {
      log.push("  ✅ harness: Windows long paths enabled");
    } else {
      const label = "Windows long paths are disabled (paths over 260 characters fail)";
      log.push(`  ❌ harness: ${label}`);
      failures.push({
        agent: "harness",
        label,
        guidance: "Trial builds and session folders go past 260 characters. From an elevated PowerShell: New-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force",
      });
    }
  }
  if (isWindows) {
    try {
      const toolchain = resolveWinAppToolchain();
      log.push(`  ✅ harness: WinApp CLI ${toolchain.version} (${toolchain.executable})`);
      log.push("  ✅ harness: per-trial WINAPP_UI_WORKFLOW_ID configured");
      const dotnetProbe = spawnSync(toolchain.dotnetExecutable, ["--version"], {
        encoding: "utf8",
        shell: false,
      });
      if (dotnetProbe.status !== 0) throw new Error("dotnet could not execute in the harness environment.");
      log.push(`  ✅ harness: dotnet ${dotnetProbe.stdout.trim()} (${toolchain.dotnetExecutable})`);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      log.push(`  ❌ harness: ${message}`);
      failures.push({ agent: "harness", label: message, guidance: "Run /winui-setup, then retry." });
    }
  }
  const seen = new Set<string>();
  for (const a of agents) {
    if (seen.has(a.pluginPath)) continue;
    seen.add(a.pluginPath);
    const cfg = loadAgentConfig(a.pluginPath);
    if (!cfg.prechecks?.required_on_path?.length && !cfg.prechecks?.required_env?.length) {
      log.push(`  · ${a.name}: no prechecks declared`);
      continue;
    }
    const trialPath = cfg.env?.path_prepend
      ? (buildPrependedPath(cfg.env.path_prepend) ?? process.env.PATH ?? "")
      : (process.env.PATH ?? "");
    const results = checkAgentPrechecks(cfg, trialPath);
    log.push(`  · ${a.name}:`);
    for (const r of results) {
      if (r.ok) log.push(`      ✅ ${r.label}${r.detail ? ` → ${r.detail}` : ""}`);
      else {
        log.push(`      ❌ ${r.label}`);
        if (r.guidance) for (const line of r.guidance.split("\n")) log.push(`         ${line}`);
        failures.push({ agent: a.name, label: r.label, guidance: r.guidance });
      }
    }
  }
  return { ok: failures.length === 0, failures, log };
}

/** Load the agent config.json from its pluginPath. */
function loadAgentConfig(pluginPath: string): AgentSetupConfig {
  const configPath = join(pluginPath, "config.json");
  if (existsSync(configPath)) {
    try { return JSON.parse(readFileSync(configPath, "utf-8")); } catch {}
  }
  return {};
}

export interface BenchmarkCallbacks {
  onOutput: (data: string) => void;
  onStatusChange: (entry: RunEntry) => void;
}

interface ProcessResult {
  exitCode: number;
  output: string;
  timedOut?: boolean;
}

function runProcess(
  command: string,
  args: string[],
  cwd: string,
  onOutput: (data: string) => void,
  timeoutMs?: number,
  useShell = true,
  extraEnv?: Record<string, string>
): Promise<ProcessResult> {
  return new Promise((resolve) => {
    const trialCtx = trialContext.getStore();
    let envForChild: NodeJS.ProcessEnv | undefined = undefined;
    if (extraEnv || trialCtx?.pathPrepend || trialCtx?.uiWorkflowId || trialCtx?.env) {
      // PATH from extraEnv (caller) takes priority over the trial PATH.
      // Windows is case-insensitive on env-var names but node's process.env
      // object treats PATH/Path/path as distinct keys, so check all three.
      const callerSetPath =
        !!extraEnv && Object.keys(extraEnv).some((k) => k.toLowerCase() === "path");
      envForChild = applyTrialEnv({ ...process.env, ...(extraEnv || {}) }, trialCtx, callerSetPath);
    }
    const proc = spawn(command, args, {
      cwd,
      shell: useShell,
      stdio: "pipe",
      env: envForChild,
    });
    trackPid(proc.pid);
    let output = "";
    let timedOut = false;
    let timer: NodeJS.Timeout | undefined;
    let resolved = false;
    let completionDetected = false;

    // Silence detection: if process produced substantial output but goes
    // quiet for 120s, assume it's stuck (e.g. copilot finished but winapp
    // run child keeps process tree alive without printing "Total session time:")
    // 120s is long enough for builds (MSBuild can take 30-60s) to complete.
    const SILENCE_THRESHOLD_MS = 300_000;
    const MIN_OUTPUT_FOR_SILENCE = 10_000; // 10KB — enough to know copilot actually ran
    let silenceTimer: NodeJS.Timeout | undefined;

    const resetSilenceTimer = () => {
      if (silenceTimer) clearTimeout(silenceTimer);
      if (output.length >= MIN_OUTPUT_FOR_SILENCE) {
        silenceTimer = setTimeout(() => {
          if (!resolved && !completionDetected && proc.pid) {
            onOutput("\n⚠️  Output silent for 5 minutes — requesting graceful shutdown\n");
            gracefulThenForceKill(proc.pid, 15000);
          }
        }, SILENCE_THRESHOLD_MS);
      }
    };

    // Graceful shutdown: send Ctrl+C to let copilot write session.shutdown, then force kill
    const gracefulThenForceKill = (pid: number, graceMs: number) => {
      killProcessTree(pid, false);
      setTimeout(() => {
        if (!resolved) {
          killProcessTree(pid, true);
        }
      }, graceMs);
    };

    // Close stdin so the process knows no input is coming
    proc.stdin?.end();

    const finish = (code: number | null) => {
      if (resolved) return;
      resolved = true;
      untrackPid(proc.pid);
      if (timer) clearTimeout(timer);
      if (silenceTimer) clearTimeout(silenceTimer);
      resolve({ exitCode: code ?? 1, output, timedOut });
    };

    const forceKillAfterDelay = (delayMs: number) => {
      if (completionDetected) return; // already scheduled a kill
      completionDetected = true;
      if (silenceTimer) clearTimeout(silenceTimer);
      setTimeout(() => {
        if (!resolved && proc.pid) {
          killProcessTree(proc.pid, true);
        }
      }, delayMs);
    };

    if (timeoutMs) {
      timer = setTimeout(() => {
        timedOut = true;
        if (proc.pid) {
          // Graceful shutdown first to allow session.shutdown event to be written
          gracefulThenForceKill(proc.pid, 15000);
        } else {
          proc.kill();
        }
      }, timeoutMs);
    }

    const checkCompletion = () => {
      // Detect copilot completion via multiple patterns — copilot is done
      // but may be stuck because winapp run keeps the process tree alive
      if (!completionDetected && !resolved) {
        if (
          output.includes("Total session time:") ||
          output.includes("Total usage est:") ||
          output.includes("Reached maximum number of auto")
        ) {
          forceKillAfterDelay(5000);
        }
      }
    };

    proc.stdout?.on("data", (chunk: Buffer) => {
      const text = chunk.toString();
      output = appendBounded(output, text, PROCESS_OUTPUT_MAX_CHARS);
      onOutput(text);
      resetSilenceTimer();
      checkCompletion();
    });
    proc.stderr?.on("data", (chunk: Buffer) => {
      const text = chunk.toString();
      output = appendBounded(output, text, PROCESS_OUTPUT_MAX_CHARS);
      onOutput(text);
      resetSilenceTimer();
      checkCompletion();
    });
    proc.on("close", (code) => finish(code));
    proc.on("exit", (code) => finish(code));
    proc.on("error", () => finish(1));
  });
}

// Run a PowerShell script via -EncodedCommand (base64 UTF-16LE).
// This bypasses cmd.exe entirely so pipes (`|`), curly braces, dollar signs,
// and embedded quotes inside the script are passed verbatim to powershell.exe.
// Without this, shell:true wraps the call in `cmd.exe /d /s /c "..."` and the
// pipe in scripts like `Get-Process | Where-Object {...}` is parsed by cmd,
// producing `'Where-Object' is not recognized...` and the cleanup silently fails.
function runPowershellScript(
  script: string,
  cwd: string,
  onOutput: (data: string) => void,
  timeoutMs?: number,
): Promise<ProcessResult> {
  const encoded = Buffer.from(script, "utf16le").toString("base64");
  return runProcess(
    "powershell",
    ["-NoProfile", "-EncodedCommand", encoded],
    cwd,
    onOutput,
    timeoutMs,
    false,
  );
}

// =============================================================================
// Copilot-specific process runner with --output-format json
// =============================================================================

interface CopilotProcessResult extends ProcessResult {
  sessionId?: string;
  usage?: {
    premiumRequests?: number;
    totalApiDurationMs?: number;
    sessionDurationMs?: number;
    codeChanges?: { linesAdded: number; linesRemoved: number };
  };
  tokenTotals: {
    mainOutput: number;
    subTotal: number;
    subAgentTotalTokens: number;
    subAgentCount: number;
    subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  };
  completionReason?: SdkCompletionReason;
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared event processor — used by both the CLI (runCopilotProcess) and SDK
// (runCopilotSdkSession) paths so they emit the same JSONL files, reconstruct
// the same dashboard text, and track the same token / sub-agent stats.
// ─────────────────────────────────────────────────────────────────────────────

interface EventProcessorOpts {
  eventsFile: string;
  onOutput: (data: string) => void;
  onTokenUpdate?: (totals: {
    mainOutput: number;
    subTotal: number;
    premiumRequests: number;
    subAgentTotalTokens: number;
    subAgentCount: number;
    subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  }) => void;
  onMeaningfulEvent?: () => void;
  onResult?: (resultEv: any) => void;
}

interface EventProcessorState {
  mainOutputTokens: number;
  subTotalTokens: number;
  totalPremiumRequests: number;
  subAgentTotalTokens: number;
  subAgentCount: number;
  subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  resultEvent: any;
}

function makeCopilotEventProcessor(opts: EventProcessorOpts): {
  process: (ev: any) => void;
  state: EventProcessorState;
} {
  const state: EventProcessorState = {
    mainOutputTokens: 0,
    subTotalTokens: 0,
    totalPremiumRequests: 0,
    subAgentTotalTokens: 0,
    subAgentCount: 0,
    subAgentDetails: [],
    resultEvent: null,
  };
  const taskNameMap = new Map<string, string>();
  const partialOutputLengths = new Map<string, number>();

  const fireTokenUpdate = () => {
    if (opts.onTokenUpdate) {
      opts.onTokenUpdate({
        mainOutput: state.mainOutputTokens,
        subTotal: state.subTotalTokens,
        premiumRequests: state.totalPremiumRequests,
        subAgentTotalTokens: state.subAgentTotalTokens,
        subAgentCount: state.subAgentCount,
        subAgentDetails: state.subAgentDetails,
      });
    }
  };

  const process = (ev: any) => {
    if (!ev || typeof ev !== "object") return;

    // Append every event to the persistent JSONL.
    try { appendFileSync(opts.eventsFile, JSON.stringify(ev) + "\n"); } catch {}

    const type: string = ev.type || "";

    switch (type) {
      case "assistant.message_delta":
        if (ev.data?.deltaContent && !ev.data?.parentToolCallId) {
          opts.onOutput(ev.data.deltaContent);
        }
        break;

      case "assistant.reasoning_delta":
        // Reasoning stream — model is actively working.
        break;

      case "assistant.message":
        opts.onMeaningfulEvent?.();
        if (ev.data?.outputTokens) {
          if (ev.data.parentToolCallId) state.subTotalTokens += ev.data.outputTokens;
          else state.mainOutputTokens += ev.data.outputTokens;
          fireTokenUpdate();
        }
        if (!ev.data?.parentToolCallId && ev.data?.toolRequests?.length > 0) {
          for (const tr of ev.data.toolRequests) {
            if (tr.name && tr.name !== "report_intent") {
              opts.onOutput(`\n🔧 ${tr.name}(${summarizeArgs(tr.arguments)})\n`);
            }
          }
        }
        break;

      case "tool.execution_start":
        if (ev.data?.toolName === "task" && ev.data?.arguments?.name && ev.data?.toolCallId) {
          taskNameMap.set(ev.data.toolCallId, ev.data.arguments.description || ev.data.arguments.name);
        }
        break;

      case "tool.execution_partial_result":
        if (ev.data?.partialOutput && ev.data?.toolCallId) {
          const callId = ev.data.toolCallId;
          const prevLen = partialOutputLengths.get(callId) || 0;
          const full = ev.data.partialOutput;
          if (full.length > prevLen) {
            const delta = full.substring(prevLen);
            partialOutputLengths.set(callId, full.length);
            const newLines = delta.split("\n").filter((l: string) => l.trim());
            for (const nl of newLines) {
              if (nl.trim().length > 0 && nl.trim().length < 300) {
                opts.onOutput(`  ${nl.trim()}\n`);
              }
            }
          }
        }
        break;

      case "tool.execution_complete":
        opts.onMeaningfulEvent?.();
        if (ev.data?.toolCallId) partialOutputLengths.delete(ev.data.toolCallId);
        if (ev.data?.result?.content) {
          const content = ev.data.result.content;
          const display = content.length > 500 ? content.substring(0, 500) + "…" : content;
          opts.onOutput(`${display}\n`);
        }
        break;

      case "subagent.completed":
      case "subagent.failed":
        opts.onMeaningfulEvent?.();
        if (ev.data) {
          const status = type === "subagent.completed" ? "✅" : "❌";
          const tokenInfo = ev.data.totalTokens ? ` (${ev.data.totalTokens} total tokens, ${ev.data.durationMs || 0}ms)` : "";
          opts.onOutput(`\n${status} Sub-agent ${type.split(".")[1]}: ${ev.data.agentDisplayName || ev.data.agentName || "?"} ${ev.data.model || ""}${tokenInfo}\n`);
          if (ev.data.totalTokens) {
            state.subAgentTotalTokens += ev.data.totalTokens;
            state.subAgentCount++;
            const customName = taskNameMap.get(ev.data.toolCallId) || ev.data.agentDisplayName || ev.data.agentName || `sub-${state.subAgentCount}`;
            state.subAgentDetails.push({
              name: customName,
              totalTokens: ev.data.totalTokens,
              durationMs: ev.data.durationMs || 0,
            });
            fireTokenUpdate();
          }
        }
        break;

      case "result":
        state.resultEvent = ev;
        if (ev.usage) {
          state.totalPremiumRequests = ev.usage.premiumRequests || 0;
          fireTokenUpdate();
        }
        opts.onResult?.(ev);
        break;

      case "assistant.turn_start":
        if (ev.data?.turnId && parseInt(ev.data.turnId) > 0) {
          opts.onOutput(`\n── Turn ${ev.data.turnId} ──\n`);
        }
        break;

      case "subagent.started":
        if (ev.data) {
          opts.onOutput(`\n🚀 Sub-agent started: ${ev.data.agentDisplayName || ev.data.agentName || "unknown"}\n`);
        }
        opts.onMeaningfulEvent?.();
        break;

      // Quiet events.
      case "assistant.turn_end":
      case "assistant.reasoning":
      case "user.message":
      case "session.mcp_servers_loaded":
      case "session.mcp_server_status_changed":
      case "session.tools_updated":
      case "session.skills_loaded":
      case "session.background_tasks_changed":
      case "session.info":
      case "system.notification":
      case "session.idle":
      case "session.busy":
      case "session.started":
      case "session.shutdown":
      case "assistant.usage":
      case "assistant.intent":
        break;

      default:
        if (!ev.ephemeral) {
          opts.onOutput(`[${type}]\n`);
        }
        break;
    }
  };

  return { process, state };
}

/**
 * Run a copilot session via @github/copilot-sdk. Mirrors `runCopilotProcess`'s
 * return shape so the build call site only differs in agent/skills wiring.
 *
 * The agent + its preloaded skills are passed in-memory via `customAgents` and
 * `skillDirectories` — no `.github/agents/` / `.github/skills/` files needed.
 * This is the path every agent setup uses; the CLI-based path
 * (`runCopilotProcess`) is retained for legacy non-v2-agent setups.
 */
interface CopilotSdkSessionOptions {
  prompt: string;
  model: string;
  workspace: string;
  customAgents?: CustomAgentConfig[];
  agent?: string;
  skillDirectories?: string[];
  disabledSkills?: string[];
  /** Plugin roots loaded as real plugins (Open Plugins format). */
  pluginDirectories?: string[];
  /**
   * Skills that must be visible and enabled in the session before the prompt
   * is sent. If any are missing the session is not prompted and the call
   * throws, so no premium requests are spent on a skill-less trial.
   */
  expectedSkills?: string[];
  /** When set, expected skills only count if their file is under one of these dirs. */
  expectedSkillRoots?: string[];
  configDir?: string;
  eventsFile: string;
  onOutput: (data: string) => void;
  onTokenUpdate?: (totals: {
    mainOutput: number;
    subTotal: number;
    premiumRequests: number;
    subAgentTotalTokens: number;
    subAgentCount: number;
    subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  }) => void;
  timeoutMs: number;
  requireValidationJson?: boolean;
}

export async function runCopilotSdkSession(
  opts: CopilotSdkSessionOptions
): Promise<CopilotProcessResult> {
  const workerPath = fileURLToPath(new URL("./sdk-worker.ts", import.meta.url));
  if (
    process.env.BENCH_SDK_WORKER !== "1" &&
    !process.versions.electron &&
    existsSync(workerPath)
  ) {
    return new Promise<CopilotProcessResult>((resolve, reject) => {
      const context = trialContext.getStore();
      const child = fork(workerPath, [], {
        execArgv: ["--expose-gc", "--import", "tsx"],
        env: applyTrialEnv({ ...process.env, BENCH_SDK_WORKER: "1" }, context),
        stdio: ["ignore", "ignore", "inherit", "ipc"],
      });
      trackPid(child.pid);
      let settled = false;
      const watchdog = setTimeout(() => {
        if (settled) return;
        settled = true;
        untrackPid(child.pid);
        if (child.pid) killProcessTree(child.pid, true);
        reject(new Error(
          `SDK worker exceeded its ${Math.round(opts.timeoutMs / 60000)}m session limit plus cleanup grace`
        ));
      }, opts.timeoutMs + 2 * 60 * 1000);
      const settle = (action: () => void) => {
        if (settled) return;
        settled = true;
        clearTimeout(watchdog);
        untrackPid(child.pid);
        action();
      };
      child.on("message", (message: any) => {
        if (message?.type === "output") opts.onOutput(String(message.data || ""));
        else if (message?.type === "tokens") opts.onTokenUpdate?.(message.data);
        else if (message?.type === "result") {
          settle(() => resolve(message.data as CopilotProcessResult));
        } else if (message?.type === "error") {
          settle(() => reject(new Error(String(message.error || "SDK worker failed"))));
        }
      });
      child.on("error", (error) => {
        settle(() => reject(error));
      });
      child.on("exit", (code) => {
        settle(() => reject(new Error(`SDK worker exited unexpectedly with code ${code}`)));
      });
      const { onOutput: _onOutput, onTokenUpdate: _onTokenUpdate, ...serializable } = opts;
      child.send({ type: "run", options: serializable }, (error) => {
        if (error) settle(() => reject(error));
      });
    });
  }
  return runCopilotSdkSessionInProcess(opts);
}

async function runCopilotSdkSessionInProcess(
  opts: CopilotSdkSessionOptions
): Promise<CopilotProcessResult> {
  const processor = makeCopilotEventProcessor({
    eventsFile: opts.eventsFile,
    onOutput: opts.onOutput,
    onTokenUpdate: opts.onTokenUpdate,
  });

  // Propagate the per-trial `path_prepend` PATH into `process.env.PATH` for
  // the lifetime of this SDK session. Reason: `runProcess` reads PATH via
  // The SDK starts its own JSON-RPC server (and that server spawns
  // powershell/bash subprocesses for tool calls) using whatever env the
  // CopilotClient is constructed with. Without an explicit `env`, the SDK
  // would inherit process.env at spawn time, missing the agent's
  // path_prepend (e.g. a custom toolchain on PATH).
  //
  // We build the client-scoped env from process.env + trial PATH augmentation.
  // No process.env mutation — keeps concurrent trials with different
  // path_prepend lists race-free.
  const trialPath = trialContext.getStore()?.pathPrepend;

  // Bypass `@github/copilot/npm-loader.js` (see copilot-cli.ts).
  const resolvedCliPath = resolveCopilotCliPath();
  opts.onOutput(
    resolvedCliPath
      ? `  Copilot CLI: ${resolvedCliPath} (bypassing npm-loader.js shim)\n`
      : `  Copilot CLI: using SDK default (platform-specific resolve failed)\n`
  );

  const clientEnv: NodeJS.ProcessEnv = {
    ...applyTrialEnv(process.env, trialContext.getStore()),
    ...(resolvedCliPath ? { COPILOT_CLI_PATH: resolvedCliPath } : {}),
  };

  const client = new CopilotClient({ env: clientEnv });
  await client.start();

  let session: any;
  try {
    session = await client.createSession({
      model: opts.model,
      onPermissionRequest: approveAll,
      workingDirectory: opts.workspace,
      customAgents: opts.customAgents,
      agent: opts.agent,
      skillDirectories: opts.skillDirectories,
      disabledSkills: opts.disabledSkills,
      ...(opts.pluginDirectories?.length ? { pluginDirectories: opts.pluginDirectories } : {}),
      hooks: {
        onPreToolUse: (input: any) => {
          if (String(input?.toolName || "").toLowerCase() !== "powershell") return;
          const decision = evaluateConcurrentProcessCommand(extractCommand(input.toolArgs));
          if (decision.allowed) return;
          return {
            permissionDecision: "deny" as const,
            permissionDecisionReason: decision.reason,
            additionalContext: decision.reason,
          };
        },
      },
      configDir: opts.configDir,
      // `configDir` was renamed to `configDirectory` in newer SDK lines
      // (>= 1.0.0-beta.12). Pass both so the same source works whether the
      // runtime resolves an older (dashboard) or newer (desktop) SDK.
      configDirectory: opts.configDir,
    } as any);
  } catch (err) {
    // Mirror the error into onOutput so it lands in session-log.txt + the live
    // view; the returned `output` field alone doesn't reach those sinks.
    const msg = err instanceof Error
      ? `${err.message}\n${err.stack ?? ""}`
      : String(err);
    const banner = `\n❌ SDK createSession failed:\n${msg}\n`;
    try { opts.onOutput(banner); } catch { /* onOutput must never break the trial */ }
    await client.stop().catch(() => {});
    return {
      exitCode: 1,
      output: `createSession failed: ${msg}`,
      timedOut: false,
      tokenTotals: {
        mainOutput: 0,
        subTotal: 0,
        subAgentTotalTokens: 0,
        subAgentCount: 0,
        subAgentDetails: [],
      },
    };
  }

  if (opts.expectedSkills?.length) {
    let loaded: Array<{ name: string; enabled?: boolean; source?: string; pluginName?: string; path?: string }> = [];
    try {
      loaded = (await session.rpc.skills.list())?.skills ?? [];
    } catch (err) {
      await client.stop().catch(() => {});
      throw new Error(`Skill preflight: could not list session skills: ${err instanceof Error ? err.message : String(err)}`);
    }
    const enabledList = loaded.filter(s => s.enabled !== false);
    const missing = findMissingSkills(opts.expectedSkills, loaded, opts.expectedSkillRoots);
    opts.onOutput(
      `  Session skills (${enabledList.length}): ${enabledList
        .map(s => s.pluginName ? `${s.name}[${s.pluginName}]` : s.name).join(", ")}\n`
    );
    if (missing.length > 0) {
      await client.stop().catch(() => {});
      throw new Error(
        `Skill preflight failed: ${missing.length} expected skill(s) not loaded from the benchmarked plugin(s): ${missing.join(", ")}. ` +
        `Not sending the prompt.`
      );
    }
  }

  // Events emitted while the session was being created (session.start, etc.)
  // fire before anyone can subscribe. Backfill them from the session history so
  // build-events.jsonl is complete. No prompt has been sent yet, so nothing else
  // arrives between this read and the lifecycle's subscription.
  try {
    for (const ev of await session.getEvents()) processor.process(ev);
  } catch { /* history unavailable: continue with live events only */ }

  try {
    const completion = await runUntilSdkCompletion({
      session,
      prompt: opts.prompt,
      timeoutMs: opts.timeoutMs,
      onEvent: (ev) => processor.process(ev),
      isTerminalMessage: opts.requireValidationJson
        ? (event) => parseValidationJson(String(event?.data?.content || "")) != null
        : undefined,
    });
    const timedOut = completion.reason === "hard_timeout";
    if (timedOut) {
      opts.onOutput(`\n⚠️  SDK session timed out after ${Math.round(opts.timeoutMs / 60000)}m\n`);
      try { await session.abort(); } catch {}
    } else if (completion.reason === "session_error") {
      opts.onOutput(`\nSDK session error: ${completion.error}\n`);
    } else if (completion.reason === "terminal_turn") {
      opts.onOutput("\n✓ Final assistant turn completed; continuing to deterministic grading.\n");
    }

    return {
      exitCode: timedOut ? 124 : completion.reason === "session_error" ? 1 : 0,
      output: completion.reason === "session_error" ? completion.error || "" : "",
      timedOut,
      sessionId: processor.state.resultEvent?.sessionId ?? session.sessionId,
      usage: processor.state.resultEvent?.usage,
      tokenTotals: {
        mainOutput: processor.state.mainOutputTokens,
        subTotal: processor.state.subTotalTokens,
        subAgentTotalTokens: processor.state.subAgentTotalTokens,
        subAgentCount: processor.state.subAgentCount,
        subAgentDetails: processor.state.subAgentDetails,
      },
      completionReason: completion.reason,
    };
  } finally {
    try { await session.disconnect(); } catch {}
    try { await client.stop(); } catch {}
  }
}

/**
 * Run copilot CLI with --output-format json.
 * Parses JSONL events from stdout in real-time:
 *   - Writes each event to `eventsFile` (JSONL) for persistence
 *   - Tracks running token totals
 *   - Reconstructs human-readable text for `onOutput` callback
 *   - Calls `onTokenUpdate` with running totals after each assistant.message
 *   - Detects session completion via the `result` event
 */
function runCopilotProcess(
  args: string[],
  cwd: string,
  eventsFile: string,
  onOutput: (data: string) => void,
  onTokenUpdate?: (totals: {
    mainOutput: number;
    subTotal: number;
    premiumRequests: number;
    subAgentTotalTokens: number;
    subAgentCount: number;
    subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  }) => void,
  timeoutMs?: number,
): Promise<CopilotProcessResult> {
  return new Promise((resolve) => {
    // Inject --output-format json
    const fullArgs = [...args, "--output-format", "json"];

    const proc = spawn(COPILOT_EXE, fullArgs, {
      cwd,
      shell: false,
      stdio: "pipe",
    });
    trackPid(proc.pid);

    let rawOutput = "";
    let timedOut = false;
    let timer: NodeJS.Timeout | undefined;
    let resolved = false;
    let resultEvent: any = null;

    // Running token totals — main agent vs sub-agents
    let mainOutputTokens = 0;
    let subTotalTokens = 0;
    let totalPremiumRequests = 0;
    let subAgentTotalTokens = 0;  // totalTokens from subagent.completed (input+output)
    let subAgentCount = 0;
    const subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }> = [];
    const taskNameMap = new Map<string, string>();  // toolCallId → custom task name

    const fireTokenUpdate = () => {
      if (onTokenUpdate) {
        onTokenUpdate({ mainOutput: mainOutputTokens, subTotal: subTotalTokens, premiumRequests: totalPremiumRequests, subAgentTotalTokens, subAgentCount, subAgentDetails });
      }
    };

    // Silence detection — based on meaningful JSONL events, not raw bytes.
    // Only resets on events that indicate real progress (assistant messages,
    // tool completions, sub-agent results). Ephemeral events like reasoning
    // deltas and partial tool output do NOT reset the timer — copilot could
    // be stuck in a reasoning loop producing deltas without making progress.
    const SILENCE_THRESHOLD_MS = 300_000;
    const MIN_EVENTS_FOR_SILENCE = 3; // Need at least a few real events before activating
    let silenceTimer: NodeJS.Timeout | undefined;
    let meaningfulEventCount = 0;

    const resetSilenceTimer = () => {
      if (silenceTimer) clearTimeout(silenceTimer);
      if (meaningfulEventCount >= MIN_EVENTS_FOR_SILENCE) {
        silenceTimer = setTimeout(() => {
          if (!resolved && proc.pid) {
            onOutput("\n⚠️  No meaningful output for 5 minutes — requesting graceful shutdown\n");
            killProcessTree(proc.pid, false);
            setTimeout(() => { if (!resolved && proc.pid) killProcessTree(proc.pid, true); }, 15000);
          }
        }, SILENCE_THRESHOLD_MS);
      }
    };

    proc.stdin?.end();

    const finish = (code: number | null) => {
      if (resolved) return;
      resolved = true;
      untrackPid(proc.pid);
      if (timer) clearTimeout(timer);
      if (silenceTimer) clearTimeout(silenceTimer);
      resolve({
        exitCode: code ?? 1,
        output: rawOutput,
        timedOut,
        sessionId: resultEvent?.sessionId,
        usage: resultEvent?.usage,
        tokenTotals: { mainOutput: mainOutputTokens, subTotal: subTotalTokens, subAgentTotalTokens, subAgentCount, subAgentDetails },
      });
    };

    if (timeoutMs) {
      timer = setTimeout(() => {
        timedOut = true;
        if (proc.pid) {
          killProcessTree(proc.pid, false);
          setTimeout(() => { if (!resolved && proc.pid) killProcessTree(proc.pid, true); }, 15000);
        } else {
          proc.kill();
        }
      }, timeoutMs);
    }

    // Buffer for incomplete JSONL lines (data may arrive in partial chunks)
    let lineBuffer = "";
    // Track cumulative partial output length per tool call to show only deltas
    const partialOutputLengths = new Map<string, number>();

    const processJsonLine = (line: string) => {
      if (!line.trim()) return;

      // Append raw line to events file
      try { appendFileSync(eventsFile, line + "\n"); } catch {}

      let ev: any;
      try { ev = JSON.parse(line); } catch { return; }

      const type: string = ev.type || "";

      // Reconstruct human-readable output for dashboard display
      switch (type) {
        case "assistant.message_delta":
          // Streaming text — show delta content (only main agent, not sub-agents)
          resetSilenceTimer();
          if (ev.data?.deltaContent && !ev.data?.parentToolCallId) {
            onOutput(ev.data.deltaContent);
          }
          break;

        case "assistant.reasoning_delta":
          // Reasoning/thinking stream — model is actively working
          resetSilenceTimer();
          break;

        case "assistant.message":
          // Complete message with token count
          meaningfulEventCount++;
          resetSilenceTimer();
          if (ev.data?.outputTokens) {
            if (ev.data.parentToolCallId) {
              // Sub-agent message — track separately
              subTotalTokens += ev.data.outputTokens;
            } else {
              // Main agent message
              mainOutputTokens += ev.data.outputTokens;
            }
            fireTokenUpdate();
          }
          // Show tool requests summary (only for main agent, not sub-agent noise)
          if (!ev.data?.parentToolCallId && ev.data?.toolRequests?.length > 0) {
            for (const tr of ev.data.toolRequests) {
              if (tr.name && tr.name !== "report_intent") {
                onOutput(`\n🔧 ${tr.name}(${summarizeArgs(tr.arguments)})\n`);
              }
            }
          }
          break;

        case "tool.execution_start":
          // Track task() tool calls to map toolCallId to custom agent name
          if (ev.data?.toolName === "task" && ev.data?.arguments?.name && ev.data?.toolCallId) {
            taskNameMap.set(ev.data.toolCallId, ev.data.arguments.description || ev.data.arguments.name);
          }
          break;

        case "tool.execution_partial_result":
          // Streaming output from long-running tools (e.g., dotnet build, powershell, sub-agents)
          // Reset silence timer — partial output means the process is actively working
          resetSilenceTimer();
          // partialOutput is cumulative — extract only the new delta
          if (ev.data?.partialOutput && ev.data?.toolCallId) {
            const callId = ev.data.toolCallId;
            const prevLen = partialOutputLengths.get(callId) || 0;
            const full = ev.data.partialOutput;
            if (full.length > prevLen) {
              const delta = full.substring(prevLen);
              partialOutputLengths.set(callId, full.length);
              // Show non-empty trimmed lines from the delta
              const newLines = delta.split("\n").filter((l: string) => l.trim());
              for (const nl of newLines) {
                if (nl.trim().length > 0 && nl.trim().length < 300) {
                  onOutput(`  ${nl.trim()}\n`);
                }
              }
            }
          }
          break;

        case "tool.execution_complete":
          meaningfulEventCount++;
          resetSilenceTimer();
          if (ev.data?.result?.content) {
            const content = ev.data.result.content;
            // Truncate very long tool results for display
            const display = content.length > 500 ? content.substring(0, 500) + "…" : content;
            onOutput(`${display}\n`);
          }
          break;

        case "subagent.completed":
        case "subagent.failed":
          meaningfulEventCount++;
          resetSilenceTimer();
          if (ev.data) {
            const status = type === "subagent.completed" ? "✅" : "❌";
            const tokenInfo = ev.data.totalTokens ? ` (${ev.data.totalTokens} total tokens, ${ev.data.durationMs || 0}ms)` : "";
            onOutput(`\n${status} Sub-agent ${type.split(".")[1]}: ${ev.data.agentDisplayName || ev.data.agentName || "?"} ${ev.data.model || ""}${tokenInfo}\n`);
            // Track sub-agent total tokens (input+output combined)
            if (ev.data.totalTokens) {
              subAgentTotalTokens += ev.data.totalTokens;
              subAgentCount++;
              const customName = taskNameMap.get(ev.data.toolCallId) || ev.data.agentDisplayName || ev.data.agentName || `sub-${subAgentCount}`;
              subAgentDetails.push({
                name: customName,
                totalTokens: ev.data.totalTokens,
                durationMs: ev.data.durationMs || 0,
              });
              fireTokenUpdate();
            }
          }
          break;

        case "result":
          // Final summary event
          resultEvent = ev;
          if (ev.usage) {
            totalPremiumRequests = ev.usage.premiumRequests || 0;
            fireTokenUpdate();
          }
          // Also signal completion — kill process tree after brief delay since copilot
          // may have child processes (winapp run) keeping the tree alive
          if (!resolved && proc.pid) {
            setTimeout(() => { if (!resolved && proc.pid) killProcessTree(proc.pid, true); }, 5000);
          }
          break;

        case "assistant.turn_start":
          if (ev.data?.turnId && parseInt(ev.data.turnId) > 0) {
            onOutput(`\n── Turn ${ev.data.turnId} ──\n`);
          }
          break;

        case "subagent.started":
          // Sub-agent spawned — log it for visibility
          if (ev.data) {
            onOutput(`\n🚀 Sub-agent started: ${ev.data.agentDisplayName || ev.data.agentName || "unknown"}\n`);
          }
          meaningfulEventCount++;
          resetSilenceTimer();
          break;

        // Quiet events — skip display
        case "assistant.turn_end":
        case "assistant.reasoning":
        case "user.message":
        case "session.mcp_servers_loaded":
        case "session.mcp_server_status_changed":
        case "session.tools_updated":
        case "session.skills_loaded":
        case "session.background_tasks_changed":
        case "session.info":
        case "system.notification":
          break;

        default:
          // Log unexpected event types for debugging
          if (!ev.ephemeral) {
            onOutput(`[${type}]\n`);
          }
          break;
      }
    };

    proc.stdout?.on("data", (chunk: Buffer) => {
      const text = chunk.toString();
      rawOutput = appendBounded(rawOutput, text, PROCESS_OUTPUT_MAX_CHARS);

      // Parse complete JSONL lines
      lineBuffer += text;
      const lines = lineBuffer.split("\n");
      lineBuffer = lines.pop() || ""; // Keep incomplete last line in buffer
      for (const line of lines) {
        processJsonLine(line);
      }
    });

    proc.stderr?.on("data", (chunk: Buffer) => {
      const text = chunk.toString();
      rawOutput = appendBounded(rawOutput, text, PROCESS_OUTPUT_MAX_CHARS);
      onOutput(text);
    });

    proc.on("close", (code) => finish(code));
    proc.on("exit", (code) => finish(code));
    proc.on("error", () => finish(1));
  });
}

/**
 * Convert a JSONL events file to human-readable text transcript.
 * Mirrors the logic in scripts/dev-get-session-txt.ps1.
 */
export function eventsToReadableText(eventsFile: string): string {
  if (!existsSync(eventsFile)) return "";
  const out: string[] = [];

  for (const line of readLinesSync(eventsFile)) {
    if (!line.trim()) continue;
    let ev: any;
    try { ev = JSON.parse(line); } catch { continue; }

    switch (ev.type) {
      case "assistant.turn_start":
        out.push(`\n=== TURN ${ev.data?.turnId} ===\n`);
        break;
      case "assistant.reasoning_delta":
        if (ev.data?.deltaContent) out.push(ev.data.deltaContent);
        break;
      case "assistant.message_delta":
        if (ev.data?.deltaContent) out.push(ev.data.deltaContent);
        break;
      case "assistant.message":
        // The CLI streamed final text via `assistant.message_delta` events; the
        // SDK delivers the complete text on the final `assistant.message`'s
        // `.data.content`. Emit either source so parseValidationJson can see
        // the JSON code fence the validation agent prints at the end.
        if (ev.data?.content) {
          out.push(ev.data.content + "\n");
        }
        if (ev.data?.toolRequests?.length > 0) {
          for (const tr of ev.data.toolRequests) {
            out.push(`\n--- TOOL: ${tr.name} ---\n`);
            if (tr.arguments) out.push(JSON.stringify(tr.arguments) + "\n");
          }
        }
        break;
      case "tool.execution_complete":
        if (ev.data?.result?.content) {
          out.push(`--- RESULT ---\n${ev.data.result.content}\n`);
        }
        break;
      case "subagent.started":
        out.push(`\n=== SUB-AGENT STARTED: ${ev.data?.agentDisplayName || ev.data?.agentName || "unknown"} ===\n`);
        break;
      case "subagent.completed":
        out.push(`\n=== SUB-AGENT COMPLETED: ${ev.data?.agentDisplayName || ev.data?.agentName || "?"} ${ev.data?.model || ""} (${ev.data?.totalTokens || "?"} tokens, ${ev.data?.durationMs || "?"}ms) ===\n`);
        break;
      case "subagent.failed":
        out.push(`\n=== SUB-AGENT FAILED: ${ev.data?.agentDisplayName || ev.data?.agentName || "?"} ${ev.data?.model || ""} (${ev.data?.totalTokens || "?"} tokens, ${ev.data?.durationMs || "?"}ms) ===\n`);
        break;
      case "result":
        out.push(`\n=== SESSION END ===\n`);
        if (ev.usage) {
          out.push(`Premium requests: ${ev.usage.premiumRequests}\n`);
          out.push(`API time: ${ev.usage.totalApiDurationMs}ms\n`);
          out.push(`Session time: ${ev.usage.sessionDurationMs}ms\n`);
          if (ev.usage.codeChanges) {
            out.push(`Code changes: +${ev.usage.codeChanges.linesAdded} -${ev.usage.codeChanges.linesRemoved}\n`);
          }
        }
        break;
    }
  }
  return out.join("");
}

/** Summarize tool call arguments for display (short form) */
function summarizeArgs(args: Record<string, any> | undefined): string {
  if (!args) return "";
  const entries = Object.entries(args);
  if (entries.length === 0) return "";
  // Show first arg value, truncated
  const [key, val] = entries[0];
  const str = typeof val === "string" ? val : JSON.stringify(val);
  const short = str.length > 80 ? str.substring(0, 80) + "…" : str;
  return entries.length === 1 ? short : `${short}, …`;
}


function copyDirRecursive(src: string, dest: string): void {
  mkdirSync(dest, { recursive: true });
  for (const entry of readdirSync(src, { withFileTypes: true })) {
    const srcPath = join(src, entry.name);
    const destPath = join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDirRecursive(srcPath, destPath);
    } else {
      copyFileSync(srcPath, destPath);
    }
  }
}

function flattenSkills(skillsSrc: string, targetSkillsDir: string): number {
  let count = 0;
  function walk(dir: string) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (existsSync(join(full, "SKILL.md"))) {
          copyDirRecursive(full, join(targetSkillsDir, entry.name));
          count++;
        } else {
          walk(full);
        }
      }
    }
  }
  walk(skillsSrc);
  return count;
}

function parseUsage(output: string) {
  const usage: Record<string, any> = {};
  let m;
  m = output.match(/Total usage est:\s+(\d+)\s+Premium/);
  if (m) usage.premium_requests = parseInt(m[1]);
  m = output.match(/API time spent:\s+(.+?)[\r\n]/);
  if (m) usage.api_time = m[1].trim();
  m = output.match(/Total session time:\s+(.+?)[\r\n]/);
  if (m) usage.session_time = m[1].trim();
  m = output.match(/Total code changes:\s+(.+?)[\r\n]/);
  if (m) usage.code_changes = m[1].trim();

  const modelMatches = [
    ...output.matchAll(
      /(\S+)\s+(\d+\.?\d*[mk]?) in, (\d+\.?\d*[mk]?) out, (\d+\.?\d*[mk]?) cached/g
    ),
  ];
  usage.models = {};
  for (const mm of modelMatches) {
    usage.models[mm[1]] = {
      input: mm[2],
      output: mm[3],
      cached: mm[4],
    };
  }
  return usage;
}

/** Format token count with k/m suffix */
function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}m`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

/** Format milliseconds to human-readable duration */
function formatDurationMs(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  if (m < 60) return `${m}m ${s}s`;
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return `${h}h ${rm}m ${s}s`;
}

/**
 * Aggregate token usage from copilot session-state events.jsonl files.
 * Matches sessions by cwd (working directory) to reliably associate sub-agent
 * sessions with their parent trial, even when multiple trials run concurrently.
 * Falls back to summing individual assistant.message events if no session.shutdown exists.
 */
/**
 * Sum the precise AI Unit (AIU) credit usage for a single session from its own
 * event stream (e.g. build-events.jsonl). Reads each `assistant.usage` event's
 * `copilotUsage.totalNanoAiu` (billed in nano-AIU = 1e-9 AIU) and converts the
 * total to AIU.
 *
 * This is the authoritative per-session credit cost: it is derived from the
 * actual token cost of every API call in THIS session, so it is independent of
 * the account's remaining-quota snapshots (which are unreliable for internal /
 * unlimited-entitlement accounts and can be shared across concurrent sessions).
 *
 * Returns the AIU total (rounded to 2 decimals) or undefined when the stream
 * carries no usage events.
 */
function sumSessionCredits(eventsFile: string): number | undefined {
  if (!existsSync(eventsFile)) return undefined;
  let nanoAiu = 0;
  let found = false;
  try {
    for (const line of readLinesSync(eventsFile)) {
      if (!line || line.indexOf("assistant.usage") === -1) continue;
      try {
        const ev = JSON.parse(line);
        const n = ev?.data?.copilotUsage?.totalNanoAiu;
        if (typeof n === "number" && n > 0) {
          nanoAiu += n;
          found = true;
        }
      } catch { /* skip malformed line */ }
    }
  } catch {
    return undefined;
  }
  return found ? Math.round((nanoAiu / 1e9) * 100) / 100 : undefined;
}

function aggregateSessionUsage(
  trialWorkDir: string,
  log: (msg: string) => void,
  localSessionStateDir?: string,
): Record<string, any> | null {
  // Prefer local session-state dir (from --config-dir) over global ~/.copilot
  const sessionStateDir = localSessionStateDir && existsSync(localSessionStateDir)
    ? localSessionStateDir
    : join(process.env.HOME || process.env.USERPROFILE || "", ".copilot", "session-state");
  if (!existsSync(sessionStateDir)) return null;

  // When using local session-state, skip cwd matching — all sessions belong to this trial
  const useLocalDir = localSessionStateDir && existsSync(localSessionStateDir);

  // Normalize the trial dir path for comparison (only needed for global dir scanning)
  const normalizedTrialDir = trialWorkDir.toLowerCase().replace(/[\\/]+/g, "/").replace(/\/$/, "");

  const sessionDirs = readdirSync(sessionStateDir, { withFileTypes: true })
    .filter((d: any) => d.isDirectory())
    .map((d: any) => ({
      name: d.name,
      path: join(sessionStateDir, d.name),
      eventsPath: join(sessionStateDir, d.name, "events.jsonl"),
    }))
    .filter((d: any) => existsSync(d.eventsPath));

  // Aggregate across all matching sessions — separate main vs sub-agent metrics
  const mainModelTotals: Record<string, { input: number; output: number; cached: number }> = {};
  const subModelTotals: Record<string, { input: number; output: number; cached: number }> = {};
  let totalPremium = 0;
  let totalCreditsNanoAiu = 0;
  let totalApiMs = 0;
  let matchedSessions = 0;
  let totalAdded = 0;
  let totalRemoved = 0;
  let earliestStart = Infinity;
  let latestEnd = 0;
  let subAgentCount = 0;

  for (const sd of sessionDirs) {
    try {
      const iterator = readLinesSync(sd.eventsPath)[Symbol.iterator]();
      let first = iterator.next();
      while (!first.done && !first.value.trim()) first = iterator.next();
      if (first.done) continue;

      // Check session.start cwd — must be within the trial directory (skip check for local dir).
      const startEv = JSON.parse(first.value);
      if (startEv.type !== "session.start") continue;
      if (!useLocalDir) {
        const cwd = (startEv.data?.context?.cwd || "").toLowerCase().replace(/[\\/]+/g, "/").replace(/\/$/, "");
        if (!cwd.startsWith(normalizedTrialDir)) continue;
      }

      matchedSessions++;
      const sessionStartMs = new Date(startEv.data.startTime).getTime();
      if (sessionStartMs < earliestStart) earliestStart = sessionStartMs;

      // Parse ALL session.shutdown events — classify as main vs sub-agent
      // Sub-agent shutdowns: no shutdownType field
      // Main session shutdown: shutdownType === "routine", has totalPremiumRequests
      // Retrospective --resume shutdown: also "routine" but appears after main — skip
      let mainFound = false;
      let fallbackOutputTokens = 0;
      for (let next = iterator.next(); !next.done; next = iterator.next()) {
        try {
          const ev = JSON.parse(next.value);
          if (ev.type === "assistant.message" && ev.data?.outputTokens) {
            fallbackOutputTokens += ev.data.outputTokens;
          }
          if (ev.type !== "session.shutdown" || !ev.data) continue;

          // Track latest shutdown timestamp for session duration calculation
          const shutdownTs = ev.timestamp ? new Date(ev.timestamp).getTime() : 0;
          if (shutdownTs > latestEnd) latestEnd = shutdownTs;

          const d = ev.data;
          const isRoutine = d.shutdownType === "routine";

          if (isRoutine && !mainFound) {
            // Main session — first routine shutdown
            mainFound = true;
            totalPremium += d.totalPremiumRequests || 0;
            totalCreditsNanoAiu += d.totalNanoAiu || 0;
            totalApiMs += d.totalApiDurationMs || 0;
            if (d.codeChanges) {
              totalAdded += d.codeChanges.linesAdded || 0;
              totalRemoved += d.codeChanges.linesRemoved || 0;
            }
            if (d.modelMetrics) {
              for (const [model, metrics] of Object.entries(d.modelMetrics)) {
                const mu = (metrics as any).usage || {};
                if (!mainModelTotals[model]) mainModelTotals[model] = { input: 0, output: 0, cached: 0 };
                mainModelTotals[model].input += mu.inputTokens || 0;
                mainModelTotals[model].output += mu.outputTokens || 0;
                mainModelTotals[model].cached += mu.cacheReadTokens || 0;
              }
            }
          } else if (!isRoutine && !d.shutdownType) {
            // Sub-agent session — no shutdownType field
            subAgentCount++;
            if (d.modelMetrics) {
              for (const [model, metrics] of Object.entries(d.modelMetrics)) {
                const mu = (metrics as any).usage || {};
                if (!subModelTotals[model]) subModelTotals[model] = { input: 0, output: 0, cached: 0 };
                subModelTotals[model].input += mu.inputTokens || 0;
                subModelTotals[model].output += mu.outputTokens || 0;
                subModelTotals[model].cached += mu.cacheReadTokens || 0;
              }
            }
          }
          // Skip subsequent routine shutdowns (retrospective --resume)
        } catch {}
      }

      // Fallback: if no shutdown (process was killed), aggregate from assistant.message events
      if (!mainFound) {
        const model = startEv.data.selectedModel || "unknown";
        if (!mainModelTotals[model]) mainModelTotals[model] = { input: 0, output: 0, cached: 0 };
        mainModelTotals[model].output += fallbackOutputTokens;
      }
    } catch {
      // Skip unparseable sessions
    }
  }

  if (matchedSessions === 0) return null;

  log(`  Session-state: found ${matchedSessions} session(s), ${subAgentCount} sub-agent shutdown(s)`);

  // Compute session duration from earliest start to latest shutdown (or now if still running)
  const endMs = latestEnd > 0 ? latestEnd : Date.now();
  const sessionDurationMs = earliestStart < Infinity
    ? endMs - earliestStart
    : 0;

  // Build usage object — includes both main and sub-agent breakdowns
  const usage: Record<string, any> = {
    premium_requests: totalPremium,
    credits: totalCreditsNanoAiu > 0 ? Math.round((totalCreditsNanoAiu / 1e9) * 100) / 100 : undefined,
    api_time: totalApiMs > 0 ? formatDurationMs(totalApiMs) : undefined,
    session_time: sessionDurationMs > 0 ? formatDurationMs(sessionDurationMs) : undefined,
    models: {} as Record<string, { input: string; output: string; cached: string }>,
    sub_agent_count: subAgentCount,
    sub_agent_models: {} as Record<string, { input: string; output: string; cached: string }>,
  };
  if (totalAdded || totalRemoved) {
    usage.code_changes = `+${totalAdded} -${totalRemoved}`;
  }
  for (const [model, totals] of Object.entries(mainModelTotals)) {
    usage.models[model] = {
      input: formatTokenCount(totals.input),
      output: formatTokenCount(totals.output),
      cached: formatTokenCount(totals.cached),
    };
  }
  for (const [model, totals] of Object.entries(subModelTotals)) {
    usage.sub_agent_models[model] = {
      input: formatTokenCount(totals.input),
      output: formatTokenCount(totals.output),
      cached: formatTokenCount(totals.cached),
    };
  }
  return usage;
}

// =============================================================================
// Extracted Build & Launch Helpers
// =============================================================================

const CSPROJ_SKIP_DIRS = new Set(["bin", "obj", ".github", ".copilot", ".uwp-source", "Generated Files", "node_modules", ".vs"]);

/**
 * Find the app's .csproj. Solutions often hold several projects (a core
 * library, tests, the app), so collect them all and rank: WinUI app first,
 * then any executable, then non-test projects, then the shallowest path.
 * (Returning the first match in walk order picked `Journal.Core.csproj` over
 * the app in a sibling file and failed the framework gate.)
 */
export function findCsproj(dir: string): string | null {
  const found: Array<{ path: string; depth: number }> = [];
  const walk = (d: string, depth: number) => {
    let entries;
    try { entries = readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = join(d, entry.name);
      if (entry.isFile() && entry.name.toLowerCase().endsWith(".csproj")) found.push({ path: full, depth });
      else if (entry.isDirectory() && !CSPROJ_SKIP_DIRS.has(entry.name) && depth < 6) walk(full, depth + 1);
    }
  };
  walk(dir, 0);
  if (found.length <= 1) return found[0]?.path ?? null;
  const rank = ({ path, depth }: { path: string; depth: number }) => {
    let text = "";
    try { text = readFileSync(path, "utf-8"); } catch { /* unreadable */ }
    const winui = /<UseWinUI>\s*true\s*<\/UseWinUI>/i.test(text);
    const exe = /<OutputType>\s*(WinExe|Exe)\s*<\/OutputType>/i.test(text);
    const test = /Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true/i.test(text) || /tests?\.csproj$/i.test(path);
    return [winui ? 0 : 1, exe ? 0 : 1, test ? 1 : 0, depth, path.length];
  };
  const ranked = found.map((f) => ({ f, r: rank(f) }));
  ranked.sort((a, b) => {
    for (let i = 0; i < a.r.length; i++) if (a.r[i] !== b.r[i]) return a.r[i] - b.r[i];
    return a.f.path.localeCompare(b.f.path);
  });
  return ranked[0].f.path;
}

/** Default dotnet/MSBuild build flow. */
async function defaultDotnetBuild(
  workDir: string,
  trialDir: string,
  globalConfig: GlobalConfig,
  callbacks: BenchmarkCallbacks,
  log: (msg: string) => void,
): Promise<{ success: boolean; csproj: string | null; output: string }> {
  const csproj = findCsproj(workDir);
  if (!csproj) {
    return { success: false, csproj: null, output: "" };
  }
  log(`  Found: ${csproj}`);

  let result: ProcessResult;
  if (isWindows) {
    const toolchain = resolveWinAppToolchain();
    const buildPlatform = osArch() === "arm64" ? "arm64" : "x64";
    const rid = `win-${buildPlatform}`;
    const args = [
      "build",
      csproj,
      "-c",
      "Debug",
      "-r",
      rid,
      "-p:Platform=" + buildPlatform,
    ];
    log(`  Using harness dotnet: dotnet ${args.join(" ")}`);
    result = await runProcess(
      toolchain.dotnetExecutable,
      args,
      workDir,
      callbacks.onOutput,
      undefined,
      false
    );
  } else {
    // Non-Windows only; Windows always builds for the host architecture above.
    const buildCommand = (globalConfig.build?.fallback_command || globalConfig.build?.command || "dotnet build {csproj} -c Debug")
      .replace(/\{csproj\}/g, `"${csproj}"`);
    log(`  Using configured build command: ${buildCommand}`);
    result = await runProcess(buildCommand, [], workDir, callbacks.onOutput);
  }
  writeFileSync(join(trialDir, "build-output.txt"), result.output);
  return { success: result.exitCode === 0, csproj, output: result.output };
}

/** Default WinApp launch flow (packaged/unpackaged WinUI). */
async function defaultWinappLaunch(
  workDir: string,
  appName: string,
  csproj: string,
  launchMode: "packaged" | "unpackaged" | undefined,
  callbacks: BenchmarkCallbacks,
  log: (msg: string) => void,
  buildOutput?: string,
): Promise<{
  success: boolean;
  pid?: string;
  releasePackageLock?: () => void;
  stagingDir?: string;
}> {
  if (isWindows) {
    const toolchain = resolveWinAppToolchain();
    const hostArch = osArch() === "arm64" ? "arm64" : "x64";
    const stageRoot = "C:\\winapp-stage";
    mkdirSync(stageRoot, { recursive: true });
    const stagingDir = join(stageRoot, `${basename(csproj, ".csproj")}_${randomBytes(4).toString("hex")}`);
    const args = [
      "run",
      csproj,
      "--no-build",
      "--detach",
      "--clean",
      "--arch",
      hostArch,
      // Evaluate with the same Platform the harness built with (-p:Platform),
      // or projects without <Platforms> resolve bin\Debug instead of
      // bin\<arch>\Debug and report "no launchable executable".
      "-p",
      `Platform=${hostArch}`,
      "--output-appx-directory",
      stagingDir,
      "--json",
    ];
    const packageIdentity = packageIdentityForProject(csproj);
    const releasePackageLock = packageIdentity
      ? await acquirePackageLock(packageIdentity)
      : undefined;
    if (packageIdentity) log(`  Package identity lock acquired: ${packageIdentity}`);
    let retainPackageLock = false;
    try {
      log(`  Launching with WinApp CLI ${toolchain.version}: winapp ${args.join(" ")}`);
      let result = await runProcess(
        toolchain.executable,
        args,
        workDir,
        callbacks.onOutput,
        90_000,
        false
      );
      // WinApp rejects the MSIX-only options for unpackaged apps
      // (WindowsPackageType=None, possibly set via Directory.Build.props), so
      // retry without them rather than scoring a working app as "doesn't run".
      if (result.exitCode !== 0 && /apply to unpackaged apps/i.test(result.output)) {
        const unpackagedArgs = args.filter((a, i) =>
          a !== "--clean" && a !== "--output-appx-directory" && args[i - 1] !== "--output-appx-directory");
        log(`  Unpackaged app: retrying without MSIX-only options: winapp ${unpackagedArgs.join(" ")}`);
        result = await runProcess(
          toolchain.executable,
          unpackagedArgs,
          workDir,
          callbacks.onOutput,
          90_000,
          false
        );
      }
      writeFileSync(join(workDir, "..", "launch-output.txt"), result.output);
      if (result.exitCode !== 0) return { success: false };
      let parsed: any;
      try { parsed = JSON.parse(result.output.trim()); } catch {}
      const pid = parsed?.processId ?? parsed?.ProcessId ?? parsed?.pid ?? parsed?.Pid;
      retainPackageLock = Boolean(pid);
      return {
        success: Boolean(pid),
        ...(pid ? { pid: String(pid) } : {}),
        ...(pid && releasePackageLock ? { releasePackageLock } : {}),
        ...(pid ? { stagingDir } : {}),
      };
    } finally {
      if (!retainPackageLock) {
        releasePackageLock?.();
        try { rmSync(stagingDir, { recursive: true, force: true }); } catch {}
      }
    }
  }

  const csprojDir = join(csproj, "..");
  const processName = basename(csproj, ".csproj");
  // Match the host arch first (build also uses host arch), but fall back to
  // x64 / cross-arch outputs so cross-compiled artefacts still launch if a
  // future config targets a non-host platform.
  const hostArch = osArch() === "arm64" ? "arm64" : "x64";
  const otherArch = hostArch === "arm64" ? "x64" : "arm64";
  const binDirs = [
    join(csprojDir, "bin", hostArch.toUpperCase(), "Debug"),
    join(csprojDir, "bin", hostArch, "Debug"),
    join(csprojDir, "bin", otherArch.toUpperCase(), "Debug"),
    join(csprojDir, "bin", otherArch, "Debug"),
    join(csprojDir, "bin", "Debug"),
  ];
  const rids = [`win-${hostArch}`, `win-${otherArch}`];
  let outputFolder: string | null = null;

  for (const bd of binDirs) {
    if (!existsSync(bd)) continue;
    const tfmDir = readdirSync(bd).find((d) =>
      d.match(/net\d/) && statSync(join(bd, d)).isDirectory()
    );
    if (tfmDir) {
      let resolved: string | null = null;
      for (const rid of rids) {
        const ridDir = join(bd, tfmDir, rid);
        if (existsSync(ridDir)) { resolved = ridDir; break; }
      }
      outputFolder = resolved || join(bd, tfmDir);
      break;
    }
  }

  // Fallback: if bin/ wasn't found under csprojDir (agent may have redirected
  // output via Directory.Build.props), parse the MSBuild output line to locate
  // the actual build artefacts.
  if (!outputFolder && buildOutput) {
    const msbuildOutputMatch = buildOutput.match(/-> (.+\.dll)\s*$/m);
    if (msbuildOutputMatch) {
      const dllPath = msbuildOutputMatch[1].trim();
      const candidate = dirname(dllPath);
      if (existsSync(candidate)) {
        log(`  Output not in csproj bin/ — found via MSBuild output: ${candidate}`);
        outputFolder = candidate;
      }
    }
  }

  if (!outputFolder) return { success: false };

  // Path-length rescue: AppX deployment fails (0x800700CE / 0x80073CF9) when the
  // metadata path under outputFolder exceeds MAX_PATH (260). If close to the limit,
  // robocopy to a short staging dir and launch from there. (subst/junction don't
  // help — AppX deployment runs as SYSTEM and rejects mount points.)
  let launchFolder = outputFolder;
  const MAX_PATH_SAFE = 150;
  if (outputFolder.length > MAX_PATH_SAFE) {
    try {
      const stageRoot = "C:\\winapp-stage";
      if (!existsSync(stageRoot)) mkdirSync(stageRoot, { recursive: true });
      // Sweep stale stage dirs older than 1h so this directory doesn't grow unbounded across runs.
      const cutoff = Date.now() - 60 * 60 * 1000;
      for (const entry of readdirSync(stageRoot)) {
        const p = join(stageRoot, entry);
        try {
          if (statSync(p).mtimeMs < cutoff) rmSync(p, { recursive: true, force: true });
        } catch {}
      }
      const stageId = randomBytes(4).toString("hex");
      const stageDir = join(stageRoot, `${processName}_${stageId}`);
      log(
        `  outputFolder is ${outputFolder.length} chars (> ${MAX_PATH_SAFE}); ` +
          `staging to ${stageDir} to avoid MAX_PATH (260)`,
      );
      const robocopyResult = await runProcess(
        "robocopy",
        [outputFolder, stageDir, "/E", "/NFL", "/NDL", "/NJH", "/NJS"],
        workDir,
        () => {},
        60000,
        false,
      );
      // robocopy exit codes 0-7 are informational success; >7 is failure.
      if (
        robocopyResult.exitCode === null ||
        robocopyResult.exitCode > 7
      ) {
        log(
          `  robocopy to staging failed (exit ${robocopyResult.exitCode}); ` +
            `falling back to original path`,
        );
      } else {
        launchFolder = stageDir;
      }
    } catch (e: any) {
      log(
        `  staging copy failed: ${e?.message ?? e}; falling back to original path`,
      );
    }
  }

  // Kill any stale instances of this app before launching.
  // Use the real process name (from the csproj), not the munged appName.
  try {
    // useShell:false → spawn invokes powershell.exe directly and passes the
    // -Command string as a single arg. With shell:true, Node concatenates
    // everything and runs through cmd.exe, which then sees the embedded `|`
    // and tries to pipe to `Select-Object` itself (cmd has no such command),
    // producing "'Select-Object' is not recognized…".
    const staleCheck = await runProcess(
      "powershell", ["-NoProfile", "-Command",
        `Get-Process -Name '${processName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id`],
      workDir, () => {}, 5000, false
    );
    if (staleCheck.output.trim()) {
      const stalePids = staleCheck.output.trim().split(/\r?\n/).filter(Boolean);
      log(`  Killing ${stalePids.length} stale ${processName} process(es): ${stalePids.join(", ")}`);
      for (const pid of stalePids) {
        try { process.kill(parseInt(pid, 10)); } catch {}
      }
      await new Promise((r) => setTimeout(r, 2000));
    }
  } catch {}

  let launchPid: string | undefined;
  const forceUnpackaged = launchMode === "unpackaged";

  // Check project root for manifest (the source of truth for packaged apps)
  // Look in both workDir and csprojDir since the agent may create the project in a subdirectory
  const hasManifest = !forceUnpackaged && (
    existsSync(join(workDir, "Package.appxmanifest")) ||
    existsSync(join(workDir, "appxmanifest.xml")) ||
    existsSync(join(csprojDir, "Package.appxmanifest")) ||
    existsSync(join(csprojDir, "appxmanifest.xml"))
  );

  if (hasManifest) {
    // Remove any stale AppX registration for the same package identity.
    // The agent may have registered the package from a different path during
    // its session, which would cause winapp run to fail with 0x80073CF9.
    const manifestPath = join(launchFolder, "AppxManifest.xml");
    if (existsSync(manifestPath)) {
      try {
        const manifestXml = readFileSync(manifestPath, "utf-8");
        const identityMatch = manifestXml.match(/Identity\s+Name="([^"]+)"/);
        if (identityMatch) {
          const pkgIdentity = identityMatch[1];
          log(`  Removing stale AppX registration for ${pkgIdentity}`);
          await runProcess(
            "powershell", ["-NoProfile", "-Command",
              `Get-AppxPackage -Name '${pkgIdentity}' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue`],
            workDir, () => {}, 15000, false
          );
        }
      } catch {}
    }

    log(`  Packaged app: winapp run --detach --json "${launchFolder}"`);
    let stdoutData = "";
    let stderrData = "";
    const winappProc = spawn("winapp", ["run", launchFolder, "--detach", "--json"], {
      cwd: workDir,
      shell: true,
      stdio: "pipe",
    });
    trackPid(winappProc.pid);

    const exitCode = await new Promise<number | null>((resolve) => {
      const timer = setTimeout(() => {
        log("  Launch timeout (90s) — continuing");
        resolve(null);
      }, 90000);

      winappProc.stdout?.on("data", (chunk: Buffer) => {
        stdoutData += chunk.toString();
        try {
          const json = JSON.parse(stdoutData.trim());
          if (json.ProcessId) {
            launchPid = String(json.ProcessId);
            clearTimeout(timer);
            // Wait for the app window to initialize
            setTimeout(() => resolve(0), 8000);
          }
        } catch {}
      });
      winappProc.stderr?.on("data", (chunk: Buffer) => {
        stderrData += chunk.toString();
      });
      winappProc.on("close", (code) => {
        clearTimeout(timer);
        if (!launchPid) resolve(code);
      });
    });

    winappProc.unref();

    if (stderrData.trim()) {
      log(`  winapp stderr: ${stderrData.trim()}`);
    }

    if (launchPid) {
      log(`  App launched (PID: ${launchPid})`);
    } else {
      log(`  winapp run failed (exit ${exitCode}): ${stdoutData.trim()}`);
      // winapp run failed — try to find an already-running instance by process name
      const findResult = await runProcess(
        "powershell", ["-NoProfile", "-Command",
          `Get-Process -Name '${processName}' -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1 -ExpandProperty Id`],
        workDir, () => {}, 5000, false
      );
      if (findResult.output.trim()) {
        launchPid = findResult.output.trim();
        log(`  Found existing ${processName} process (PID: ${launchPid})`);
      } else {
        log("  No running instance found — launch failed");
      }
    }
  } else {
    // Unpackaged app — launch the exe directly
    const exes = readdirSync(outputFolder).filter(
      (f) =>
        f.endsWith(".exe") &&
        !f.match(/createdump|hostfxr|RestartAgent/)
    );
    if (exes.length > 0) {
      log(`  Launching: ${join(outputFolder, exes[0])}`);
      const exeProc = spawn(join(outputFolder, exes[0]), [], {
        detached: true,
        stdio: "ignore",
      });
      trackPid(exeProc.pid);
      if (exeProc.pid) {
        launchPid = String(exeProc.pid);
        log(`  Unpackaged app PID: ${launchPid}`);
      }
      await new Promise((r) => setTimeout(r, 8000));
    }
  }

  // Detect the app window — always by PID (deterministic), never by name (fragile)
  let success = false;
  if (launchPid) {
    for (let attempt = 1; attempt <= 5; attempt++) {
      // Verify the process is still alive before checking for windows
      try {
        process.kill(parseInt(launchPid, 10), 0);
      } catch {
        log(`  Process ${launchPid} died — app crashed on startup`);
        break;
      }
      const listResult = await runProcess(
        "winapp",
        ["ui", "list-windows", "-a", launchPid, "--json"],
        workDir,
        (d) => log(d),
        15000
      );
      if (listResult.output.includes('"hwnd"')) { success = true; break; }
      if (attempt < 5) {
        log(`  Window not found for PID ${launchPid}, retrying... (${attempt}/5)`);
        await new Promise((r) => setTimeout(r, 10000));
      }
    }
  } else {
    log("  No PID available — cannot detect window");
  }

  return { success, pid: launchPid };
}

/** Custom build command — run the command, write build-output.txt, return success. */
async function customBuild(
  command: string,
  workDir: string,
  trialDir: string,
  callbacks: BenchmarkCallbacks,
  log: (msg: string) => void,
): Promise<boolean> {
  log(`  Running custom build: ${command}`);
  const result = await runProcess(command, [], workDir, callbacks.onOutput, 120000);
  writeFileSync(join(trialDir, "build-output.txt"), result.output);
  return result.exitCode === 0;
}

/** Auto-detect macOS app executable name from build products. */
function detectMacAppExecutable(workDir: string): string | null {
  // XcodeGen/xcodebuild: look for .app bundle in build/Build/Products/Debug/
  const debugDir = join(workDir, "build", "Build", "Products", "Debug");
  if (existsSync(debugDir)) {
    const apps = readdirSync(debugDir).filter(f => f.endsWith(".app") && !f.includes("-Runner"));
    if (apps.length > 0) return apps[0].replace(/\.app$/, "");
  }
  // SPM: look for executables in .build/debug/ (symlink to arch-specific dir)
  const spmDebugDir = join(workDir, ".build", "debug");
  if (existsSync(spmDebugDir)) {
    try {
      const files = readdirSync(spmDebugDir);
      for (const f of files) {
        const fp = join(spmDebugDir, f);
        try {
          const st = statSync(fp);
          if (st.isFile() && !f.includes(".") && (st.mode & 0o111)) return f;
        } catch {}
      }
    } catch {}
  }
  return null;
}

/** Custom launch command — run (detached), wait for window with detectApp name. */
async function customLaunch(
  command: string,
  detectApp: string,
  workDir: string,
  log: (msg: string) => void,
): Promise<{ success: boolean; pid?: string }> {
  log(`  Running custom launch: ${command}`);
  const parts = command.split(/\s+/);
  const proc = spawn(parts[0], parts.slice(1), {
    cwd: workDir, shell: true, stdio: "pipe", detached: true,
  });
  trackPid(proc.pid);
  proc.unref();
  await new Promise(r => setTimeout(r, 10000));

  let success = false;
  let pid: string | undefined;

  if (isWindows) {
    // Windows: use winapp to detect window
    const toolchain = resolveWinAppToolchain();
    for (let attempt = 1; attempt <= 5; attempt++) {
      const listResult = await runProcess(
        toolchain.executable, ["ui", "list-windows", "-a", detectApp, "--json"],
        workDir, () => {}, 15000, false
      );
      try {
        const windows = JSON.parse(listResult.output);
        const target = (Array.isArray(windows) ? windows : [windows]).find(
          (window) => window?.processId || window?.pid
        );
        if (target) {
          success = true;
          pid = String(target.processId ?? target.pid);
          break;
        }
      } catch {}
      if (attempt < 5) {
        log(`  Window not found, retrying... (${attempt}/5)`);
        await new Promise(r => setTimeout(r, 8000));
      }
    }
  } else {
    // macOS: find the app process by matching the workDir in its path
    // This avoids name mismatches (agent may name the app differently) and
    // disambiguates concurrent runs (multiple apps with the same executable name).
    const autoDetected = detectMacAppExecutable(workDir);
    const namesToTry = [detectApp];
    if (autoDetected && autoDetected !== detectApp) namesToTry.push(autoDetected);

    for (let attempt = 1; attempt <= 5; attempt++) {
      // First: try to find a process whose executable path is inside this trial's workDir
      const psResult = await runProcess(
        "bash", ["-c", `ps axo pid,comm | grep -F "${workDir}" | grep -v grep | grep -v xcodebuild | head -1 | awk '{print $1}'`],
        workDir, () => {}, 5000
      );
      const foundPid = psResult.output.trim();
      if (foundPid && /^\d+$/.test(foundPid)) {
        pid = foundPid;
        success = true;
        log(`  Found app process by path (PID: ${pid})`);
        break;
      }

      // Fallback: exact name match
      for (const name of namesToTry) {
        const pgrepResult = await runProcess(
          "pgrep", ["-x", name],
          workDir, () => {}, 5000
        );
        if (pgrepResult.exitCode === 0) {
          pid = pgrepResult.output.trim().split("\n")[0];
          success = true;
          break;
        }
      }
      if (success) break;
      // Fallback: partial match on any of the names
      for (const name of namesToTry) {
        const pgrepResult2 = await runProcess(
          "pgrep", ["-f", name],
          workDir, () => {}, 5000
        );
        if (pgrepResult2.exitCode === 0) {
          pid = pgrepResult2.output.trim().split("\n")[0];
          success = true;
          break;
        }
      }
      if (success) break;
      if (attempt < 5) {
        log(`  Process not found, retrying... (${attempt}/5)`);
        await new Promise(r => setTimeout(r, 8000));
      }
    }
  }
  if (pid) log(`  App PID: ${pid}`);
  return { success, pid };
}

export function parseValidationJson(output: string): any | null {
  // Strip ANSI escape codes — copilot CLI colorizes output which breaks regex matching
  const clean = output.replace(/\x1b\[[\d;]*m/g, "");

  // Helper: try to extract valid JSON from a block of text
  const tryParseBlock = (block: string): any | null => {
    if (block.includes("<0-10>") || block.includes("<0–10>")) return null;
    const firstBrace = block.indexOf("{");
    if (firstBrace < 0) return null;
    let depth = 0;
    let lastBrace = -1;
    for (let i = firstBrace; i < block.length; i++) {
      if (block[i] === "{") depth++;
      else if (block[i] === "}") {
        depth--;
        if (depth === 0) { lastBrace = i; break; }
      }
    }
    if (lastBrace > firstBrace) {
      try { return JSON.parse(block.substring(firstBrace, lastBrace + 1)); }
      catch { return null; }
    }
    return null;
  };

  // Try ```json blocks (closed with ```) — iterate ALL matches, skipping templates
  const jsonBlockRegex = /```json\s*([\s\S]+?)\s*```/g;
  let jsonBlockMatch: RegExpExecArray | null;
  while ((jsonBlockMatch = jsonBlockRegex.exec(clean)) !== null) {
    const result = tryParseBlock(jsonBlockMatch[1].trim());
    if (result) return result;
  }

  // Fallback: unclosed ```json block (copilot output sometimes omits closing fence)
  const unclosedMatch = clean.match(/```json\s*([\s\S]+)$/);
  if (unclosedMatch) {
    const result = tryParseBlock(unclosedMatch[1].trim());
    if (result) return result;
  }

  // Fallback: find ANY `{...}` block that contains "project_score" or "ui_score",
  // using a brace-depth walk so nested objects (e.g. `requirements: { "1": {…} }`)
  // are captured. The previous flat-regex version truncated at the first inner `}`.
  // Models that emit raw JSON with no ```json fence (e.g. gpt-5.5) land here.
  let searchStart = 0;
  while (searchStart < clean.length) {
    const braceIdx = clean.indexOf("{", searchStart);
    if (braceIdx < 0) break;
    const parsed = tryParseBlock(clean.substring(braceIdx));
    if (parsed && (parsed.project_score !== undefined || parsed.ui_score !== undefined || parsed.design_score !== undefined)) {
      return parsed;
    }
    searchStart = braceIdx + 1;
  }
  return null;
}

interface StructuredReqResult {
  id: number;
  text: string;
  status: "pass" | "fail";
  reason: string;
}

/**
 * Extract structured requirement results from validation JSON.
 * Handles both new format (requirements: {"1": {status, reason}})
 * and old format (requirements_passed: [...], requirements_failed: [...]).
 */
export function extractRequirementResults(
  validation: Record<string, any>,
  scenarioRequirements: string[]
): StructuredReqResult[] {
  const results: StructuredReqResult[] = [];

  // New format: requirements object keyed by number
  if (validation.requirements && typeof validation.requirements === "object" && !Array.isArray(validation.requirements)) {
    for (const [key, val] of Object.entries(validation.requirements)) {
      const id = parseInt(key, 10);
      if (isNaN(id)) continue;
      const v = val as Record<string, any>;
      const status = v.status === "pass" ? "pass" : "fail";
      const reason = v.reason || "";
      const text = (id >= 1 && id <= scenarioRequirements.length)
        ? scenarioRequirements[id - 1]
        : `Requirement ${id}`;
      results.push({ id, text, status, reason });
    }
  }
  // Old format: requirements_passed / requirements_failed arrays
  else {
    const passed: string[] = Array.isArray(validation.requirements_passed) ? validation.requirements_passed : [];
    const failed: string[] = Array.isArray(validation.requirements_failed) ? validation.requirements_failed : [];

    // Try to match each to a scenario requirement by number prefix
    const seen = new Set<number>();
    for (const r of passed) {
      const m = r.match(/^(\d+)\.\s*/);
      const id = m ? parseInt(m[1], 10) : 0;
      if (id > 0 && !seen.has(id)) {
        seen.add(id);
        const text = (id >= 1 && id <= scenarioRequirements.length)
          ? scenarioRequirements[id - 1]
          : r.replace(/^\d+\.\s*/, "").trim();
        results.push({ id, text, status: "pass", reason: "" });
      }
    }
    for (const r of failed) {
      const m = r.match(/^(\d+)\.\s*/);
      const id = m ? parseInt(m[1], 10) : 0;
      if (id > 0 && !seen.has(id)) {
        seen.add(id);
        const text = (id >= 1 && id <= scenarioRequirements.length)
          ? scenarioRequirements[id - 1]
          : r.replace(/^\d+\.\s*/, "").split(/:\s*/)[0].trim();
        const reason = r.replace(/^\d+\.\s*/, "").trim();
        results.push({ id, text, status: "fail", reason });
      }
    }
    // Handle unnumbered requirements (assign sequential IDs not yet seen)
    let nextId = 1;
    for (const r of [...passed, ...failed]) {
      if (/^\d+\.\s*/.test(r)) continue;
      while (seen.has(nextId)) nextId++;
      seen.add(nextId);
      const isPassed = passed.includes(r);
      const text = (nextId >= 1 && nextId <= scenarioRequirements.length)
        ? scenarioRequirements[nextId - 1]
        : r.split(/:\s*/)[0].trim();
      results.push({ id: nextId, text, status: isPassed ? "pass" : "fail", reason: isPassed ? "" : r });
      nextId++;
    }
  }

  return results.sort((a, b) => a.id - b.id);
}

/** Count passed/failed from structured results */
export function countReqResults(results: StructuredReqResult[]): { passed: number; failed: number; total: number } {
  const passed = results.filter(r => r.status === "pass").length;
  const failed = results.filter(r => r.status === "fail").length;
  return { passed, failed, total: passed + failed };
}

// ─────────────────────────────────────────────────────────────────────────────
// Content-filter detection
//
// Some Copilot model providers ship a server-side content-safety filter that terminates a turn when the prompt OR response matches certain patterns (e.g. low-level Win32 interop, keyboard polling, raw buffer access). The runner sees this as a generic "build failed" — the agent's last turn never landed, so the project doesn't compile. Reclassifying these flakes lets the dashboard separate "agent can't write working code" from "platform refused to serve the request".
//
// Detection: the copilot CLI surfaces filter blocks as `"session.error"` events with a message containing "content filtering policy". Scanning the build's events.jsonl after a failed build is enough to identify them.
// ─────────────────────────────────────────────────────────────────────────────
function detectContentFilterBlock(eventsFile: string): boolean {
  if (!existsSync(eventsFile)) return false;
  try {
    for (const line of readLinesSync(eventsFile)) {
      if (line.includes('"session.error"') && /content filtering policy/i.test(line)) {
        return true;
      }
    }
    return false;
  } catch {
    return false;
  }
}

export async function runBenchmark(
  entry: RunEntry,
  runDir: string,
  opts: { maxBuildMinutes: number; concurrency?: number },
  callbacks: BenchmarkCallbacks
): Promise<void> {
  const cfg = loadAgentConfig(entry.pluginPath);
  const ctx = createTrialEnv(runDir, entry.trialName, cfg);
  ctx.uiWorkflowId = uiWorkflowIdFor(runDir, entry.id);
  const envBefore = isWindows ? snapshotUserEnv() : null;
  try {
    return await trialContext.run(ctx, () =>
      runBenchmarkImpl(entry, runDir, opts, callbacks)
    );
  } finally {
    removeTrialEnv(ctx);
    await restoreMachineState(join(runDir, entry.trialName), envBefore, callbacks.onOutput);
  }
}

async function runBenchmarkImpl(
  entry: RunEntry,
  runDir: string,
  opts: { maxBuildMinutes: number; concurrency?: number },
  callbacks: BenchmarkCallbacks
): Promise<void> {
  const globalConfig = loadGlobalConfig();
  const scenarioResult = loadScenario(entry.scenarioPath);
  const scenarioConfig: ScenarioConfig = scenarioResult?.config || { name: entry.scenarioConfigName, description: "", type: "new" };
  // Per-task overrides (flat run-meta): fall back to the run-wide queue option /
  // env when the entry doesn't set its own value, so existing homogeneous runs
  // behave exactly as before.
  const effMaxBuildMinutes = entry.maxBuildMinutes ?? opts.maxBuildMinutes;
  // Cooperative UI turns serialize only desktop-sensitive commands, but that
  // queue wait is wall-clock time inside the SDK request. Preserve the user's
  // active-work budget by adding a bounded wait allowance per peer trial.
  const cooperativeWaitMinutes = Math.min(60, Math.max(0, (opts.concurrency ?? 1) - 1) * 15);
  const buildWallClockMinutes = effMaxBuildMinutes + cooperativeWaitMinutes;
  const validationWallClockMinutes = 40 + cooperativeWaitMinutes;
  const baseAppName = scenarioConfig.app_name || scenarioConfig.name;
  // Unique app name per run to avoid MSIX registration conflicts in parallel runs
  const runIndex = entry.iteration || 1;
  const condShort = entry.condition.replace(/\s*\[\d+\/\d+\]$/, "");
  // Load agent config early so we can check unique_app_name
  const agentConfig = loadAgentConfig(entry.pluginPath);
  const appName = agentConfig.unique_app_name === false
    ? baseAppName
    : `${baseAppName}${condShort}${runIndex}`;
  // Flat trial folder directly under runDir (short paths avoid MAX_PATH issues)
  const trialDir = join(runDir, entry.trialName);
  const workDir = join(trialDir, "app");
  const logsDir = join(trialDir, "session-logs-dir");
  mkdirSync(workDir, { recursive: true });
  mkdirSync(logsDir, { recursive: true });

  // Keep SDK SQLite/session state on a short physical path. Deep benchmark
  // trial names can push SQLite paths beyond Windows' practical path limit.
  const persistedConfigDir = join(logsDir, ".copilot");
  const trialConfigDir = isWindows
    ? join(
        process.env.LOCALAPPDATA || homedir(),
        "win-dev-bench",
        "sdk",
        `${basename(runDir)}-${entry.trialName.slice(0, 3)}-${randomBytes(4).toString("hex")}`
      )
    : persistedConfigDir;
  mkdirSync(trialConfigDir, { recursive: true });
  if (trialConfigDir !== persistedConfigDir && !existsSync(persistedConfigDir)) {
    try { symlinkSync(trialConfigDir, persistedConfigDir, "junction"); } catch {}
  }
  // Only the CLI config (it names the logged-in user; the token itself lives in
  // the OS credential store) is shared, as a snapshot copy so trials can't write
  // into the user's global config. The global session store (hundreds of MB),
  // installed plugins and IDE state are deliberately NOT linked: trials must
  // not see or grow the user's history, and plugins come only from the agent
  // configuration.
  const globalCopilot = join(process.env.HOME || process.env.USERPROFILE || "", ".copilot");
  const globalCliConfig = join(globalCopilot, "config.json");
  const trialConfigJson = join(trialConfigDir, "config.json");
  if (existsSync(globalCliConfig) && !existsSync(trialConfigJson)) {
    try { copyFileSync(globalCliConfig, trialConfigJson); } catch { /* ignore */ }
  }

  const setStatus = (status: RunEntry["status"]) => {
    entry.status = status;
    callbacks.onStatusChange(entry);
  };

  const log = (msg: string) => callbacks.onOutput(msg + "\n");

  const banner = (stage: string, icon: string, color: "cyan" | "yellow" | "green" | "magenta" | "red" = "cyan") => {
    const colors: Record<string, string> = {
      cyan: "\x1b[36m",
      yellow: "\x1b[33m",
      green: "\x1b[32m",
      magenta: "\x1b[35m",
      red: "\x1b[31m",
    };
    const c = colors[color] || "";
    const reset = "\x1b[0m";
    callbacks.onOutput(`\n${c}${"━".repeat(60)}${reset}\n`);
    callbacks.onOutput(`${c}  ${icon}  ${stage}${reset}\n`);
    callbacks.onOutput(`${c}${"━".repeat(60)}${reset}\n\n`);
  };

  // Expand launch_detect template variables (same as launch_command)
  const expandedLaunchDetect = (agentConfig.launch_detect || "")
    .replace(/\{app_dir\}/g, workDir)
    .replace(/\{app_name\}/g, appName) || appName;

  // Declare appPid early so cleanupApps closure can reference it
  let appPid: string | undefined;
  let appStartedAt: number | undefined;
  let releasePackageRegistration: (() => void) | undefined;
  let launchStagingDir: string | undefined;

  const cleanupApps = async (finalizeConfig = false) => {
    await killAppProcesses({ appPid, appStartedAt, appName, baseAppName, expandedLaunchDetect, workDir });
    if (isWindows) {
      const project = findCsproj(workDir);
      const projectDir = project ? dirname(project) : workDir;
      const manifest = ["Package.appxmanifest", "appxmanifest.xml"]
        .map((name) => join(projectDir, name))
        .find((candidate) => existsSync(candidate));
      if (manifest) {
        try {
          const toolchain = resolveWinAppToolchain();
          await runProcess(
            toolchain.executable,
            ["unregister", "--manifest", manifest, "--force", "--json"],
            projectDir,
            () => {},
            30_000,
            false
          );
        } catch {
          // Registration cleanup is best-effort; the next --clean launch repairs it.
        }
      }
      releasePackageRegistration?.();
      releasePackageRegistration = undefined;
      if (launchStagingDir) {
        try { rmSync(launchStagingDir, { recursive: true, force: true }); } catch {}
        launchStagingDir = undefined;
      }
    }
    // Remove screenshot files left by the validation agent (keep final-screenshot/screenshot for HTML report)
    const keepScreenshots = /^(final-screenshot|screenshot)\.(png|jpg|jpeg|bmp)$/i;
    for (const dir of [trialDir, workDir]) {
      if (!existsSync(dir)) continue;
      try {
        for (const f of readdirSync(dir)) {
          if (/\.(png|jpg|jpeg|bmp)$/i.test(f) && !keepScreenshots.test(f)) {
            rmSync(join(dir, f), { force: true });
          }
        }
      } catch {}
    }

    // Remove bulky build artifacts (xcodebuild DerivedData, .NET bin/obj)
    // Keeps source code but frees ~500MB-1GB per trial
    const bulkDirs = [
      join(workDir, "build"),           // xcodebuild -derivedDataPath ./build
      join(workDir, "build-derived"),    // alternate derivedData path
      join(workDir, "DerivedData"),
      join(workDir, "bin"),             // .NET build output
      join(workDir, "obj"),             // .NET intermediate
    ];
    for (const d of bulkDirs) {
      if (existsSync(d)) {
        try { rmSync(d, { recursive: true, force: true }); } catch {}
      }
    }
    // Also remove any nested bin/obj from subdirectories (e.g., csproj subfolders)
    if (existsSync(workDir)) {
      try {
        for (const sub of readdirSync(workDir)) {
          const subPath = join(workDir, sub);
          if (!statSync(subPath).isDirectory()) continue;
          for (const target of ["bin", "obj"]) {
            const t = join(subPath, target);
            if (existsSync(t)) {
              try { rmSync(t, { recursive: true, force: true }); } catch {}
            }
          }
        }
      } catch {}
    }
    if (finalizeConfig && trialConfigDir !== persistedConfigDir && existsSync(trialConfigDir)) {
      try {
        if (existsSync(persistedConfigDir) && lstatSync(persistedConfigDir).isSymbolicLink()) {
          unlinkSync(persistedConfigDir);
        }
        if (!existsSync(persistedConfigDir)) renameSync(trialConfigDir, persistedConfigDir);
      } catch {
        // Keep the junction to the short state directory if archival cannot move it.
      }
    }
  };

  // ─── SETUP ───
  entry.startedAt = new Date();
  setStatus("setup");
  banner(`SETUP: ${entry.condition} / ${entry.model}`, "🔧", "cyan");
  {
    const tctx = trialContext.getStore();
    if (tctx?.env?.DOTNET_CLI_HOME) log(`  Trial env: clean DOTNET_CLI_HOME (no preinstalled templates/tools) → ${tctx.env.DOTNET_CLI_HOME}`);
    else if (tctx?.env) log(`  Trial env: ⚠️  DOTNET_CLI_HOME not isolated (env.isolate_dotnet_home=false); the user's templates are visible`);
    if (tctx?.env?.DOTNET_ROOT) log(`  Trial env: DOTNET_ROOT → ${tctx.env.DOTNET_ROOT} (the dotnet on PATH)`);
    if (tctx?.hiddenTools) {
      log(`  Trial env: hidden from PATH: ${tctx.hiddenTools.tools.join(", ")} ` +
        `(${tctx.hiddenTools.removedDirs.length} PATH dir(s) removed, ${tctx.hiddenTools.shims} other executable(s) shimmed)`);
    }
  }

  // Kill any stale instances from previous runs to avoid launch collisions
  await cleanupApps();

  let agentFlag = false;
  let mcpConfigPath: string | undefined;

  // ── 1. Run setup scripts (if any) ──
  if (agentConfig.preset_scripts && agentConfig.preset_scripts.length > 0) {
    let resolvedScripts;
    try {
      resolvedScripts = validateAgentSetupScripts(
        condShort,
        agentConfig.preset_scripts
      );
    } catch (err: any) {
      log(`  ❌ Script validation failed: ${err.message}`);
      entry.failReason = `setup_script_failed: ${err.message}`;
      setStatus("failed");
      entry.finishedAt = new Date();
      writeFileSync(
        join(trialDir, "results.json"),
        JSON.stringify(
          {
            trial: entry.trialName,
            scenario: entry.scenarioConfigName,
            condition: entry.condition,
            model: entry.model,
            metrics: { score: 0, builds: false, runs: false, timeout: false },
            setup_scripts: [],
          },
          null,
          2
        )
      );
      return;
    }

    const setupScriptResults: Array<{ script: string; exit_code: number; duration_seconds: number }> = [];
    const setupLogPath = join(trialDir, "setup-script.log");

    for (const script of resolvedScripts) {
      const scriptStartTime = Date.now();
      const header = `\n=== ${script.name} (${new Date().toISOString()}) ===\n`;
      writeFileSync(setupLogPath, header, { flag: "a" });
      log(`  Running setup script: ${script.name} (timeout: ${script.timeoutMinutes}m)`);

      // Run script with the appropriate shell based on file extension
      const isShellScript = script.entryPoint.endsWith(".sh");
      const scriptCmd = isShellScript ? "bash" : "powershell";
      const scriptArgs = isShellScript
        ? [script.entryPoint]
        : ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script.entryPoint];

      const scriptResult = await runProcess(
        scriptCmd,
        scriptArgs,
        workDir,
        (data) => {
          writeFileSync(setupLogPath, data, { flag: "a" });
          callbacks.onOutput(data);
        },
        script.timeoutMinutes * 60 * 1000,
        false,
        {
          BENCH_APP_DIR: workDir,
          BENCH_APP_NAME: appName,
          BENCH_SCENARIO_DIR: entry.scenarioPath,
          BENCH_SCENARIO_NAME: entry.scenarioConfigName,
          BENCH_AGENTSETUP_NAME: condShort,
          BENCH_AGENTSETUP_DIR: entry.pluginPath,
          BENCH_SCRIPT_DIR: script.scriptDir,
          BENCH_ROOT: benchRoot,
        }
      );

      const durationSeconds = Math.round((Date.now() - scriptStartTime) / 1000);
      setupScriptResults.push({
        script: script.name,
        exit_code: scriptResult.exitCode,
        duration_seconds: durationSeconds,
      });

      if (scriptResult.exitCode !== 0) {
        const reason = scriptResult.timedOut
          ? `setup_script_failed: ${script.name} (timed out after ${script.timeoutMinutes}m)`
          : `setup_script_failed: ${script.name} (exit code: ${scriptResult.exitCode})`;
        log(`  ❌ ${reason}`);
        entry.failReason = reason;
        setStatus("failed");
        entry.finishedAt = new Date();
        writeFileSync(
          join(trialDir, "results.json"),
          JSON.stringify(
            {
              trial: entry.trialName,
              scenario: entry.scenarioConfigName,
              condition: entry.condition,
              model: entry.model,
              metrics: { score: 0, builds: false, runs: false, timeout: false },
              setup_scripts: setupScriptResults,
            },
            null,
            2
          )
        );
        return;
      }

      log(`  ✅ ${script.name} completed (${durationSeconds}s)`);
    }

    (entry as any)._setupScriptResults = setupScriptResults;

    // Preset scripts can populate caches that path_prepend references
    // (e.g. restoring a NuGet package referenced by `${pkg:<id>}`). Recompute
    // the trial PATH now that the cache has had a chance to materialize.
    const ctx = trialContext.getStore();
    if (ctx && agentConfig.env?.path_prepend) {
      const refreshed = computeTrialPath(agentConfig, ctx);
      if (refreshed && refreshed !== ctx.pathPrepend) {
        ctx.pathPrepend = refreshed;
        log(`  PATH refreshed after preset scripts`);
      }
    }
  }

  // ── 1b. Prechecks (fail fast before paying for copilot) ──
  // Mirrors the sweep-level preflight in `runPreflight()`. Belt-and-braces:
  // if the dashboard launched this trial individually (bypassing preflight)
  // or if a preset_script created an inconsistent env, we still bail before
  // the costly copilot session.
  {
    const trialPath = trialContext.getStore()?.pathPrepend || process.env.PATH || "";
    const checkResults = checkAgentPrechecks(agentConfig, trialPath);
    for (const r of checkResults) {
      if (r.ok) log(`  ✅ precheck: ${r.label} → ${r.detail}`);
      else {
        log(`  ❌ precheck FAILED: ${r.label}`);
        if (r.guidance) for (const line of r.guidance.split("\n")) log(`     ${line}`);
      }
    }
    const missing = checkResults.filter((r) => !r.ok);
    if (missing.length > 0) {
      const reason = `precheck_failed: ${missing.map((m) => m.label).join(", ")}`;
      entry.failReason = reason;
      setStatus("failed");
      entry.finishedAt = new Date();
      writeFileSync(
        join(trialDir, "results.json"),
        JSON.stringify(
          {
            trial: entry.trialName,
            scenario: entry.scenarioConfigName,
            condition: entry.condition,
            model: entry.model,
            metrics: { score: 0, builds: false, runs: false, timeout: false },
            fail_reason: reason,
            prechecks: missing.map((m) => ({ label: m.label, guidance: m.guidance })),
          },
          null,
          2
        )
      );
      return;
    }
  }

  // ── 2. Run scaffold_command (if any) ──
  if (agentConfig.scaffold_command) {
    const templateCmd = agentConfig.scaffold_command
      .replace(/\{app_name\}/g, appName)
      .replace(/\{app_dir\}/g, workDir);
    // Custom scaffold tools may fail if dir already exists — remove it first
    if (existsSync(workDir)) {
      rmSync(workDir, { recursive: true, force: true });
    }
    log(`  Scaffolding: ${templateCmd}`);
    await runProcess(templateCmd, [], trialDir, () => {});
    if (!existsSync(workDir)) mkdirSync(workDir, { recursive: true });
  }

  // Init git (after scaffold so it doesn't get deleted)
  await runProcess("git", ["init", "--quiet"], workDir, () => {});

  // ── 3. Install agent ──
  const targetGh = join(workDir, ".github");
  mkdirSync(join(targetGh, "skills"), { recursive: true });
  mkdirSync(join(targetGh, "agents"), { recursive: true });

  const srcSkillsDirs = [join(benchRoot, "skills"), join(benchRoot, "skills", ".local")];
  // When the agent comes entirely from a remote plugin (from_repo), we want a
  // clean install from that plugin only — don't leak local repo skills in.
  const isFromRepoAgent = !!agentConfig.from_repo || !!agentConfig.from_local;
  if (isFromRepoAgent) {
    srcSkillsDirs.length = 0;
  }
  // Append skill source roots declared via `skills.from_packages` (NuGet caches).
  if (agentConfig.skills?.from_packages) {
    for (const spec of agentConfig.skills.from_packages) {
      const resolved = resolvePackageSubpath(spec.package, spec.subpath);
      if (resolved) {
        srcSkillsDirs.push(resolved);
        log(`  Skill source (nuget): ${spec.package} → ${resolved}`);
      } else {
        log(`  ⚠️  Skill source (nuget) not found: ${spec.package}/${spec.subpath} — has the package been restored?`);
      }
    }
  }
  // Resolve from_local → plugins from this winappCli checkout (the branch under
  // test), loaded through the SDK's pluginDirectories like a marketplace install.
  if (agentConfig.from_local) {
    const fl = agentConfig.from_local;
    if (!fl.plugins?.length) throw new Error("from_local needs plugins[]");
    const plugins = fl.plugins.map((subpath) => {
      const root = join(repoRoot, subpath);
      if (!existsSync(root)) throw new Error(`from_local plugin "${subpath}" not found under ${repoRoot}`);
      // Throws when a plugin has no skills, so a broken checkout fails the trial.
      const layout = resolvePluginLayout(root, { requireAgent: false });
      log(`  Plugin ${layout.name ?? subpath}${layout.version ? ` v${layout.version}` : ""} (local): ${layout.skills.length} skills (${layout.skills.join(", ")})`);
      return { subpath, root, layout };
    });
    const git = (args: string[]) =>
      (spawnSync("git", ["-C", repoRoot, ...args], { encoding: "utf8", windowsHide: true }).stdout ?? "").trim();
    const sha = git(["rev-parse", "HEAD"]);
    const branch = git(["rev-parse", "--abbrev-ref", "HEAD"]);
    const dirty = git(["status", "--porcelain", "--", ...fl.plugins]).length > 0;
    log(`  Agent source (local): ${repoRoot} @ ${branch} (${sha.slice(0, 7)}${dirty ? ", with uncommitted plugin changes" : ""})`);
    const allSkills = [...new Set(plugins.flatMap((p) => p.layout.skills))].sort();
    const missing = (agentConfig.skills?.include ?? []).filter((s) => !allSkills.includes(s));
    if (missing.length) throw new Error(`skills.include not found in plugins: ${missing.join(", ")}`);
    (entry as any)._upstreamSha = sha;
    (entry as any)._upstreamRef = branch;
    (entry as any)._expectedSkills = agentConfig.skills?.include ?? allSkills;
    (entry as any)._expectedSkillRoots = plugins.map((p) => p.root);
    (entry as any)._pluginDirectories = plugins.map((p) => p.root);
    entry.agentSource = {
      type: "local",
      repo: "local",
      ref: branch,
      sha,
      dirty,
      plugin_subpath: fl.plugins.join(","),
      skills: allSkills,
      plugins: plugins.map((p) => ({ subpath: p.subpath, name: p.layout.name, version: p.layout.version, skills: p.layout.skills })),
      load_mode: "plugin",
    };
    if (fl.agent && !agentConfig.agent) {
      const agentFile = plugins.flatMap((p) => p.layout.agentFiles).find((f) => agentNameOf(f) === fl.agent);
      if (!agentFile) throw new Error(`from_local.agent "${fl.agent}" not found in plugins: ${fl.plugins.join(", ")}`);
      agentConfig.agent = agentFile;
    }
  }
  // Resolve from_repo → materialize plugin(s) from a GitHub ref.
  //   Legacy mode (plugin_subpath): agent → custom agent, skills → skill dirs.
  //   Plugin mode (plugins[]):      roots → SDK pluginDirectories, default
  //                                 agent unless from_repo.agent is set.
  if (agentConfig.from_repo) {
    const fr = agentConfig.from_repo;
    const ref = (entry as any).upstreamRef ?? fr.default_ref ?? "main";
    const pluginMode = Array.isArray(fr.plugins) && fr.plugins.length > 0;
    const subpaths = pluginMode ? fr.plugins! : (fr.plugin_subpath ? [fr.plugin_subpath] : []);
    try {
      if (subpaths.length === 0) throw new Error("from_repo needs plugin_subpath or plugins[]");
      // Pin floating refs once per run, then always load from the immutable
      // per-SHA checkout. The branch cache dir (e.g. `__main`) is shared and
      // gets fetched/reset by later runs while this trial's session reads it.
      const pinnedSha = readPin(runDir, fr.repo, ref)
        ?? pinRef(runDir, fr.repo, ref, (await resolveRepo({ repo: fr.repo, ref })).sha);
      const resolved = await resolveRepo({ repo: fr.repo, ref: pinnedSha });
      const sha = resolved.sha;
      log(`  Agent source (repo): ${fr.repo}@${ref} (${sha.slice(0, 7)}${pinnedSha !== ref ? ", pinned for this run" : ""})`);

      // Plugin roots. Catalog mode: names → the sources the catalog pins.
      const sources: Array<{ subpath: string; root: string; repo: string; sha: string }> = [];
      if (fr.catalog) {
        const catalogPath = join(resolved.path, fr.catalog);
        if (!existsSync(catalogPath)) throw new Error(`Catalog "${fr.catalog}" not found in ${fr.repo}@${ref}`);
        const catalog = JSON.parse(readFileSync(catalogPath, "utf-8"));
        for (const name of subpaths) {
          const entryDef = (catalog.plugins || []).find((p: any) => p?.name === name);
          if (!entryDef) throw new Error(`Plugin "${name}" not in catalog ${fr.catalog} (has: ${(catalog.plugins || []).map((p: any) => p?.name).join(", ")})`);
          const src = entryDef.source;
          if (typeof src === "string") {
            sources.push({ subpath: name, root: join(resolved.path, src), repo: fr.repo, sha });
          } else if (src?.source === "github" && src.repo) {
            const srcRef: string = src.sha || src.ref || "main";
            const srcPin = /^[0-9a-f]{40}$/i.test(srcRef)
              ? srcRef
              : readPin(runDir, src.repo, srcRef) ?? pinRef(runDir, src.repo, srcRef, (await resolveRepo({ repo: src.repo, ref: srcRef })).sha);
            const srcRepo = await resolveRepo({ repo: src.repo, ref: srcPin });
            sources.push({ subpath: name, root: join(srcRepo.path, src.path || ""), repo: src.repo, sha: srcRepo.sha });
            log(`  Catalog plugin ${name} v${entryDef.version ?? "?"}: ${src.repo}/${src.path || ""}@${src.ref ? `${src.ref} ` : ""}(${srcRepo.sha.slice(0, 7)})`);
          } else {
            throw new Error(`Catalog plugin "${name}" has an unsupported source: ${JSON.stringify(src)}`);
          }
        }
      } else {
        for (const subpath of subpaths) sources.push({ subpath, root: join(resolved.path, subpath), repo: fr.repo, sha });
      }

      const plugins = sources.map(({ subpath, root, repo, sha: pluginSha }) => {
        if (!existsSync(root)) throw new Error(`Subpath "${subpath}" not found in ${repo}@${pluginSha.slice(0, 7)}`);
        // Throws when a plugin has no skills (or no agent in legacy mode), so
        // an upstream layout change fails the trial instead of running it
        // without skills.
        const layout = resolvePluginLayout(root, { requireAgent: !pluginMode });
        log(`  Plugin ${layout.name ?? subpath}${layout.version ? ` v${layout.version}` : ""}: ${layout.skills.length} skills (${layout.skills.join(", ")})`);
        return { subpath, root, layout, repo, sha: pluginSha };
      });
      const allSkills = [...new Set(plugins.flatMap(p => p.layout.skills))].sort();
      // The winappcli plugin's version is stamped from the CLI it documents;
      // skills may name commands an older installed CLI doesn't have.
      const cliPlugin = plugins.find(p => p.layout.name === "winappcli" && p.layout.version);
      if (cliPlugin && isWindows) {
        try {
          const cli = resolveWinAppToolchain().version;
          if (compareVersionCore(cli, cliPlugin.layout.version!) < 0) {
            log(`  ⚠️  WinApp CLI ${cli} is older than the winappcli plugin v${cliPlugin.layout.version}; skills may reference commands this CLI lacks.`);
          }
        } catch { /* toolchain problems are reported by preflight */ }
      }
      const missing = (agentConfig.skills?.include ?? []).filter(s => !allSkills.includes(s));
      if (missing.length > 0) {
        throw new Error(`skills.include not found in plugin: ${missing.join(", ")} (plugin has: ${allSkills.join(", ")})`);
      }
      (entry as any)._upstreamSha = sha;
      (entry as any)._upstreamRef = ref;
      (entry as any)._expectedSkills = agentConfig.skills?.include ?? allSkills;
      // Skills must come from this checkout (or the legacy copy installed
      // from it), not from a same-named user-installed plugin.
      (entry as any)._expectedSkillRoots = [...plugins.map(p => p.root), join(targetGh, "skills")];
      entry.agentSource = {
        type: "from_repo",
        repo: fr.repo,
        ref,
        sha,
        plugin_subpath: subpaths.join(","),
        skills: allSkills,
        plugins: plugins.map(p => ({
          subpath: p.subpath, name: p.layout.name, version: p.layout.version, skills: p.layout.skills,
          ...(fr.catalog ? { repo: p.repo, sha: p.sha } : {}),
        })),
        ...(fr.catalog ? { catalog: fr.catalog } : {}),
        load_mode: pluginMode ? "plugin" : "agent",
      };

      if (pluginMode) {
        (entry as any)._pluginDirectories = plugins.map(p => p.root);
        if (fr.agent && !agentConfig.agent) {
          const agentFile = plugins.flatMap(p => p.layout.agentFiles).find(f => agentNameOf(f) === fr.agent);
          if (!agentFile) throw new Error(`from_repo.agent "${fr.agent}" not found in plugins: ${subpaths.join(", ")}`);
          agentConfig.agent = agentFile;
        }
      } else {
        srcSkillsDirs.push(plugins[0].layout.skillsDir);
        if (!agentConfig.agent) agentConfig.agent = plugins[0].layout.agentFile;
        // Default skills.all=true so we install everything the plugin ships.
        if (!agentConfig.skills) agentConfig.skills = { all: true } as any;
      }
    } catch (e: any) {
      log(`  ERROR: Could not resolve from_repo ${fr.repo}@${ref}: ${e?.message || e}`);
      throw e;
    }
  }
  // Resolve agent_from_package → agentConfig.agent so the v2 branch below picks it up.
  if (agentConfig.agent_from_package && !agentConfig.agent) {
    const resolved = resolvePackageSubpath(
      agentConfig.agent_from_package.package,
      agentConfig.agent_from_package.subpath
    );
    if (resolved) {
      // Set as an absolute path; the v2 branch joins with repoRoot only if relative.
      agentConfig.agent = resolved;
      log(`  Agent source (nuget): ${agentConfig.agent_from_package.package} → ${resolved}`);
    } else {
      log(`  ⚠️  Agent source (nuget) not found: ${agentConfig.agent_from_package.package}/${agentConfig.agent_from_package.subpath}`);
    }
  }
  const srcMcpDir = join(benchRoot, "mcp");

  if (agentConfig.agent) {
    // ── v2 mode: pre-built agent.md file ──
    const agentSrc = resolve(repoRoot, agentConfig.agent);
    if (existsSync(agentSrc)) {
      const agentContent = readFileSync(agentSrc, "utf-8");
      const nameMatch = agentContent.match(/^---\s*\n[\s\S]*?name:\s*(\S+)[\s\S]*?\n---/);
      const agentName = nameMatch ? nameMatch[1] : "winui3";
      // Strip frontmatter to get the agent body — this is what the SDK's
      // customAgents[i].prompt field expects.
      const bodyMatch = agentContent.match(/^---\s*\n[\s\S]*?\n---\s*\n([\s\S]*)$/);
      const agentBody = (bodyMatch ? bodyMatch[1] : agentContent).trim();
      // Keep the file copy for any legacy code paths that read it; the build
      // phase reads `_customAgentBody` instead and feeds the SDK.
      copyFileSync(agentSrc, join(targetGh, "agents", `${agentName}.agent.md`));
      agentFlag = true;
      (entry as any)._agentName = agentName;
      (entry as any)._customAgentBody = agentBody;
      log(`  Installed v2 agent: ${agentName} from ${agentConfig.agent}`);
    } else {
      log(`  WARNING: Agent file not found: ${agentSrc}`);
    }

  } else if (agentConfig.sections) {
    const sectionsRoot = agentConfig.sections_root
      ? join(repoRoot, agentConfig.sections_root)
      : join(agentsDir, "_sections");
    const sectionsDir = sectionsRoot;

    if (existsSync(sectionsDir)) {
      const sections = agentConfig.sections;
      const baseSection = sections[0];
      const baseFile = join(sectionsDir, `${baseSection}.md`);
      const baseRaw = existsSync(baseFile) ? readFileSync(baseFile, "utf-8") : "";
      const nameMatch = baseRaw.match(/^---\s*\n[\s\S]*?name:\s*(\S+)[\s\S]*?\n---/);
      const agentName = nameMatch ? nameMatch[1] : baseSection;
      const fmMatch = baseRaw.match(/^(---\s*\n[\s\S]*?\n---\s*\n)/);
      const frontmatter = fmMatch ? fmMatch[1] : "";
      let template = baseRaw.replace(/^---\s*\n[\s\S]*?\n---\s*\n/, "");

      for (const section of sections) {
        if (section === baseSection) continue;
        const sectionFile = join(sectionsDir, `${section}.md`);
        if (existsSync(sectionFile)) {
          const content = readFileSync(sectionFile, "utf-8")
            .replace(/^---\s*\n[\s\S]*?\n---\s*\n/, "").trim();
          template = template.replace(`{{${section}}}`, content);
        }
      }

      template = template.replace(/\{\{[a-z_-]+\}\}\n?/g, "");

      // Inline skill content into agent.md if configured
      if (agentConfig.inline_skills) {
        const inlinedSkills: string[] = [];
        for (const section of sections) {
          const deps2 = parseSectionDeps(join(sectionsDir, `${section}.md`));
          const toInline = deps2.inline_skills || [];
          for (const skill of toInline) {
            if (inlinedSkills.includes(skill)) continue;
            let skillMd: string | null = null;
            for (const dir of srcSkillsDirs) {
              const skillPath = join(dir, skill, "SKILL.md");
              if (existsSync(skillPath)) { skillMd = skillPath; break; }
            }
            if (skillMd) {
              const skillContent = readFileSync(skillMd, "utf-8")
                .replace(/^---[\s\S]*?---\s*/m, "");
              template += "\n\n" + skillContent.trim() + "\n";
              inlinedSkills.push(skill);
            }
          }
        }
        if (inlinedSkills.length > 0) {
          log(`  Inlined ${inlinedSkills.length} skill(s): ${inlinedSkills.join(", ")}`);
        }
      }

      writeFileSync(join(targetGh, "agents", `${agentName}.agent.md`), frontmatter + template);
      log(`  Assembled ${agentName} agent with slots: ${sections.filter(s => s !== baseSection).join("+") || "(base only)"}`);

      agentFlag = true;
      (entry as any)._agentName = agentName;

      // Auto-resolve section dependencies (skills + mcp from section frontmatter)
      if (!agentConfig.skills) agentConfig.skills = {};
      if (!agentConfig.mcp) agentConfig.mcp = {};
      for (const section of sections) {
        const deps = parseSectionDeps(join(sectionsDir, `${section}.md`));
        if (deps.skills) {
          if (!agentConfig.skills.include) agentConfig.skills.include = [];
          for (const s of deps.skills) {
            if (!agentConfig.skills.include.includes(s)) {
              agentConfig.skills.include.push(s);
            }
          }
        }
        if (deps.inline_skills) {
          if (!agentConfig.skills.include) agentConfig.skills.include = [];
          for (const s of deps.inline_skills) {
            if (!agentConfig.skills.include.includes(s)) {
              agentConfig.skills.include.push(s);
            }
          }
        }
        if (deps.mcp) {
          if (!agentConfig.mcp.include) agentConfig.mcp.include = [];
          for (const m of deps.mcp) {
            if (!agentConfig.mcp.include.includes(m)) {
              agentConfig.mcp.include.push(m);
            }
          }
        }
      }
    }
  } else {
    // No sections — check for a standalone agent file
    const agentFile = join(entry.pluginPath, "winui3.agent.md");
    if (existsSync(agentFile)) {
      copyFileSync(agentFile, join(targetGh, "agents", "winui3.agent.md"));
      agentFlag = true;
    }
    // Also check old plugin structure: agents/ + skills/ folders
    const legacyAgents = join(entry.pluginPath, "agents");
    if (existsSync(legacyAgents)) {
      for (const f of readdirSync(legacyAgents)) {
        if (f.endsWith(".agent.md")) {
          copyFileSync(
            join(legacyAgents, f),
            join(targetGh, "agents", f)
          );
          agentFlag = true;
        }
      }
    }

    const legacySkills = join(entry.pluginPath, "skills");
    if (existsSync(legacySkills)) {
      const count = flattenSkills(legacySkills, join(targetGh, "skills"));
      log(`  Installed ${count} skills from agent setup`);
    }

    // Install MCP config from old structure
    const mcpJson = join(entry.pluginPath, ".mcp.json");
    if (existsSync(mcpJson)) {
      const mcpContent = JSON.parse(readFileSync(mcpJson, "utf-8"));
      const mcpConfig = mcpContent.mcpServers ? mcpContent : { mcpServers: mcpContent };
      const copilotDir = join(workDir, ".copilot");
      mkdirSync(copilotDir, { recursive: true });
      mcpConfigPath = join(copilotDir, "mcp-config.json");
      writeFileSync(mcpConfigPath, JSON.stringify(mcpConfig, null, 2));
      log("  Installed MCP config at .copilot/mcp-config.json");
    }
  }

  // ── 4. Install skills ──
  if (agentConfig.skills) {
    const findAllSkills = () => {
      const found = new Set<string>();
      for (const dir of srcSkillsDirs) {
        if (existsSync(dir)) {
          for (const d of readdirSync(dir)) {
            if (statSync(join(dir, d)).isDirectory()) found.add(d);
          }
        }
      }
      return Array.from(found);
    };
    const findSkillPath = (name: string): string | null => {
      for (const dir of srcSkillsDirs) {
        const p = join(dir, name);
        if (existsSync(p)) return p;
      }
      return null;
    };

    let skillsToInstall: string[];
    if (agentConfig.skills.include) {
      skillsToInstall = agentConfig.skills.include;
    } else if (agentConfig.skills.exclude) {
      skillsToInstall = findAllSkills().filter(d => !agentConfig.skills!.exclude!.includes(d));
    } else if (agentConfig.skills.all) {
      skillsToInstall = findAllSkills();
    } else {
      skillsToInstall = [];
    }

    let skillCount = 0;
    for (const skill of skillsToInstall) {
      const skillSrc = findSkillPath(skill);
      if (skillSrc) {
        copyDirRecursive(skillSrc, join(targetGh, "skills", skill));
        skillCount++;
      }
    }
    if (skillCount > 0) log(`  Installed ${skillCount} skills`);
  }

  // ── 5. Install MCP servers ──
  if (agentConfig.mcp && !mcpConfigPath && (agentConfig.mcp.include || agentConfig.mcp.exclude || agentConfig.mcp.all)) {
    let mcpServers: string[];
    if (agentConfig.mcp.include) {
      mcpServers = agentConfig.mcp.include;
    } else if (agentConfig.mcp.exclude) {
      mcpServers = existsSync(srcMcpDir)
        ? readdirSync(srcMcpDir)
            .filter(f => f.endsWith(".json"))
            .map(f => f.replace(".json", ""))
            .filter(n => !agentConfig.mcp!.exclude!.includes(n))
        : [];
    } else {
      mcpServers = existsSync(srcMcpDir)
        ? readdirSync(srcMcpDir).filter(f => f.endsWith(".json")).map(f => f.replace(".json", ""))
        : [];
    }

    if (mcpServers.length > 0) {
      const mergedMcp: Record<string, any> = {};
      for (const server of mcpServers) {
        const mcpFile = join(srcMcpDir, `${server}.json`);
        if (existsSync(mcpFile)) {
          const content = JSON.parse(readFileSync(mcpFile, "utf-8"));
          if (content.mcpServers) {
            Object.assign(mergedMcp, content.mcpServers);
          } else {
            Object.assign(mergedMcp, content);
          }
        }
      }
      if (Object.keys(mergedMcp).length > 0) {
        const copilotDir = join(workDir, ".copilot");
        mkdirSync(copilotDir, { recursive: true });
        mcpConfigPath = join(copilotDir, "mcp-config.json");
        writeFileSync(mcpConfigPath, JSON.stringify({ mcpServers: mergedMcp }, null, 2));
        log(`  Installed ${mcpServers.length} MCP server(s)`);
      }
    }
  }

  // Copy BuildAndRun.ps1 if present in installed skills
  const buildScript = join(targetGh, "skills", "winui-dev-workflow", "BuildAndRun.ps1");
  if (existsSync(buildScript)) {
    log("  BuildAndRun.ps1 available in winui-dev-workflow skill");
  }

  // Git commit
  await runProcess("git", ["add", "-A"], workDir, () => {});
  await runProcess(
    "git",
    ["commit", "-m", "initial setup", "--quiet", "--allow-empty"],
    workDir,
    () => {}
  );

  // ─── BUILD PHASE ───
  setStatus("building");
  const resolvedAgentName = (entry as any)._agentName || "winui3";
  banner(`COPILOT BUILD: ${entry.model}${agentFlag ? ` --agent ${resolvedAgentName}` : ""}`, "🤖", "yellow");

  // ── 6. Build prompt ──
  const promptRaw = loadPrompt(entry.scenarioPath);
  let prompt = promptRaw.trim();
  const sourcePath = scenarioConfig.original_app?.source_dir
    ?.replace(/\{repo_root\}/g, repoRoot)
    ?.replace(/\{scenario_dir\}/g, entry.scenarioPath);
  if (sourcePath) {
    prompt += `\n\nThe original app source code is at: ${sourcePath}`;
  }
  prompt += `\n\nIMPORTANT: Create the project in the current directory: ${workDir}`;

  // Include test assets flagged for the build agent
  const buildAssets = scenarioConfig.test_assets?.filter(a => a.include_in_build);
  if (buildAssets && buildAssets.length > 0) {
    prompt += "\n\n## Test Assets\nThe following test assets are available:\n";
    for (const asset of buildAssets) {
      prompt += `\n- **${asset.name}**: \`${asset.path}\``;
      if (asset.description) prompt += `\n  ${asset.description}`;
    }
  }

  if (agentConfig.prompt_addendum) {
    const expandedAddendum = agentConfig.prompt_addendum
      .replace(/\{app_name\}/g, appName)
      .replace(/\{app_dir\}/g, workDir);
    prompt += `\n\n${expandedAddendum}`;
  }

  // Add framework hint if not already present in prompt
  if (agentConfig.framework_hint && !prompt.includes(agentConfig.framework_hint)) {
    prompt += `\n\nIMPORTANT: Build this as a **${agentConfig.framework_hint}** app.`;
  }

  prompt += `\n\nDo NOT run any git operations (git add, git commit, git status, etc.) — focus only on building the app.`;

  // ── 7. Run copilot ──
  const promptFile = join(logsDir, "build-prompt.txt");
  writeFileSync(promptFile, prompt);

  // Show the full prompt in the live view
  log(`\n\x1b[36m${"─".repeat(60)}\x1b[0m`);
  log(`\x1b[36m  📝  PROMPT\x1b[0m`);
  log(`\x1b[36m${"─".repeat(60)}\x1b[0m\n`);
  log(prompt);
  log(`\n\x1b[36m${"─".repeat(60)}\x1b[0m\n`);

  entry.startedAt = new Date();

  const onTokenUpdate = (totals: {
    mainOutput: number;
    subTotal: number;
    premiumRequests: number;
    subAgentTotalTokens: number;
    subAgentCount: number;
    subAgentDetails: Array<{ name: string; totalTokens: number; durationMs: number }>;
  }) => {
    const mainOut = formatTokenCount(totals.mainOutput);
    const totalOut = formatTokenCount(totals.mainOutput + totals.subTotal);
    if (totals.subTotal > 0) {
      const subTot = formatTokenCount(totals.subTotal);
      entry.tokenDisplay = `out: ${totalOut} (main: ${mainOut}, subs: ${subTot})`;
    } else {
      entry.tokenDisplay = `out: ${mainOut}`;
    }
    entry.outputTokens = formatTokenCount(totals.mainOutput);
    entry.premiumRequests = totals.premiumRequests;
    if (totals.subAgentTotalTokens > 0) {
      entry.subAgentInputTokens = formatTokenCount(totals.subAgentTotalTokens);
      entry.subAgentCount = totals.subAgentCount;
      entry.subAgentDetails = totals.subAgentDetails;
    }
    callbacks.onStatusChange(entry);
  };

  // Build phase always goes through @github/copilot-sdk. Even non-v2 setups
  // (no custom agent file — e.g. the `starter` agent, which just scaffolds
  // `dotnet new winui` and lets copilot's default agent build) used to use
  // the CLI, but the CLI binary fails with "Native addon 'runtime' not found
  // for win32-arm64" on arm64 hosts. The SDK doesn't share that dependency.
  // customAgents / agent / skillDirectories are all optional — pass them only
  // when we actually have an agent file.
  const customAgentBody = (entry as any)._customAgentBody as string | undefined;
  const skillDirs = srcSkillsDirs.filter((d) => existsSync(d));

  // `include` = which skills the agent can discover via skill(name).
  // `preload` = subset to eagerly inject (customAgents[0].skills).
  // Skills found in skillDirs but absent from `include` are pushed into
  // `disabledSkills` so the discoverable set matches `include` exactly.
  const includeSet = agentConfig.skills?.include
    ? new Set(agentConfig.skills.include)
    : null;
  const preloadList: string[] = agentConfig.skills?.preload || [];
  let disabledSkills: string[] | undefined;
  if (includeSet) {
    const seen = new Set<string>();
    const disabled: string[] = [];
    for (const d of skillDirs) {
      try {
        for (const f of readdirSync(d)) {
          if (seen.has(f)) continue;
          seen.add(f);
          try {
            if (!statSync(join(d, f)).isDirectory()) continue;
          } catch { continue; }
          if (!existsSync(join(d, f, "SKILL.md"))) continue;
          if (!includeSet.has(f)) disabled.push(f);
        }
      } catch { /* unreadable dir */ }
    }
    if (disabled.length > 0) disabledSkills = disabled;
  }

  const customAgents: CustomAgentConfig[] | undefined =
    agentFlag && customAgentBody
      ? [{
          name: resolvedAgentName,
          prompt: customAgentBody,
          skills: preloadList,
        }]
      : undefined;
  if (customAgents) {
    log(`  Build via @github/copilot-sdk`);
    log(`    customAgent: ${resolvedAgentName}  (preloaded: ${preloadList.length > 0 ? preloadList.join(", ") : "(none — load on demand)"})`);
    for (const d of skillDirs) log(`    skillDirectory: ${d}`);
    if (includeSet) log(`    discoverable skills: ${[...includeSet].join(", ")}`);
    if (disabledSkills) log(`    disabledSkills (not in include): ${disabledSkills.length}`);
  } else {
    log(`  Build via @github/copilot-sdk (default agent — no customAgent declared)`);
  }
  if (isWindows) {
    log(`  Cooperative WinApp UI workflow: ${trialContext.getStore()?.uiWorkflowId}`);
  }
  const buildResult = await runCopilotSdkSession({
    prompt,
    model: entry.model,
    workspace: workDir,
    customAgents,
    agent: customAgents ? resolvedAgentName : undefined,
    skillDirectories: customAgents ? skillDirs : undefined,
    disabledSkills,
    pluginDirectories: (entry as any)._pluginDirectories,
    expectedSkills: (entry as any)._expectedSkills,
    expectedSkillRoots: (entry as any)._expectedSkillRoots,
    configDir: trialConfigDir,
    eventsFile: join(logsDir, "build-events.jsonl"),
    onOutput: callbacks.onOutput,
    onTokenUpdate,
    timeoutMs: buildWallClockMinutes * 60 * 1000,
  });
  entry.completionReason = buildResult.completionReason;

  writeFileSync(join(logsDir, "session-log.txt"), eventsToReadableText(join(logsDir, "build-events.jsonl")));

  // Stop anything the agent left running (e.g. its own `winapp run` of the
  // app) before the harness builds, publishes, and launches: a running copy
  // locks bin/ and AppX files and holds the package registration.
  if (!buildResult.timedOut) await cleanupApps();

  if (buildResult.timedOut) {
    banner(
      `TIMEOUT: Build exceeded ${buildWallClockMinutes} minutes ` +
      `(${effMaxBuildMinutes}m work + ${cooperativeWaitMinutes}m cooperative wait allowance)`,
      "⏰",
      "red"
    );
    await cleanupApps(true);
    setStatus("timeout");
    entry.finishedAt = new Date();
    writeFileSync(
      join(trialDir, "results.json"),
      JSON.stringify(
        {
          trial: entry.trialName,
          scenario: entry.scenarioConfigName,
          condition: entry.condition,
          model: entry.model,
          metrics: { score: 0, builds: false, runs: false, timeout: true },
        },
        null,
        2
      )
    );
    return;
  }

  // Build session failed before producing a usable session. Persist a
  // failed-status results.json so the trial doesn't dead-end (UI stuck on
  // "building", no aggregate / re-score data).
  if (buildResult.exitCode !== 0) {
    banner(`BUILD SESSION FAILED (exit ${buildResult.exitCode})`, "❌", "red");
    // Mirror to session-log.txt so the error survives a UI reload (the live
    // onOutput buffer is renderer-side only).
    try {
      const existing = existsSync(join(logsDir, "session-log.txt"))
        ? readFileSync(join(logsDir, "session-log.txt"), "utf-8")
        : "";
      writeFileSync(
        join(logsDir, "session-log.txt"),
        existing + "\n\n--- SDK error (build phase) ---\n" + (buildResult.output || "(no output)") + "\n"
      );
    } catch { /* best-effort */ }
    await cleanupApps(true);
    entry.failReason = `sdk_session_failed: exit ${buildResult.exitCode}` +
      (buildResult.output ? ` — ${buildResult.output.split("\n")[0]}` : "");
    setStatus("failed");
    entry.finishedAt = new Date();
    writeFileSync(
      join(trialDir, "results.json"),
      JSON.stringify(
        {
          trial: entry.trialName,
          scenario: entry.scenarioConfigName,
          condition: entry.condition,
          model: entry.model,
          metrics: { score: 0, builds: false, runs: false, timeout: false },
          fail_reason: entry.failReason,
          sdk_output_excerpt: (buildResult.output || "").slice(0, 4000),
        },
        null,
        2
      )
    );
    return;
  }

  // The SDK reports the session ID directly. (A previous fallback guessed it
  // from the newest folder in the global session-state dir, which attributed
  // concurrent trials to the same, unrelated session.)
  const buildSessionId = buildResult.sessionId;
  if (buildSessionId) {
    entry.buildSessionId = buildSessionId;
    log(`  Build session ID: ${buildSessionId}`);
  }

  // Parse usage — prefer structured data from --output-format json result event
  let usage: Record<string, any> = {};
  if (buildResult.usage) {
    usage = {
      premium_requests: buildResult.usage.premiumRequests,
      api_time: buildResult.usage.totalApiDurationMs
        ? formatDurationMs(buildResult.usage.totalApiDurationMs) : undefined,
      session_time: buildResult.usage.sessionDurationMs
        ? formatDurationMs(buildResult.usage.sessionDurationMs) : undefined,
      code_changes: buildResult.usage.codeChanges
        ? `+${buildResult.usage.codeChanges.linesAdded} -${buildResult.usage.codeChanges.linesRemoved}` : undefined,
      models: {},
    };
    log(`  Usage from JSON result: ${usage.premium_requests} premium, ${usage.session_time}`);
  } else {
    // Fallback: parse from text output  
    usage = parseUsage(buildResult.output);
  }

  // Fallback: if parseUsage found no models (e.g., orchestrator agents that delegate to sub-agents),
  // aggregate token usage from copilot session-state events.jsonl files matching this trial's cwd.
  const localSessionState = join(logsDir, ".copilot", "session-state");
  if (!usage.models || Object.keys(usage.models).length === 0) {
    const sessionUsage = aggregateSessionUsage(workDir, log, localSessionState);
    if (sessionUsage) {
      usage = { ...usage, ...sessionUsage };
      log(`  Aggregated usage from session-state: ${Object.keys(sessionUsage.models || {}).length} model(s)`);
    }
  } else {
    // Even when we have main model data, still aggregate to get sub-agent breakdown
    const sessionUsage = aggregateSessionUsage(workDir, log, localSessionState);
    if (sessionUsage) {
      usage.sub_agent_count = sessionUsage.sub_agent_count;
      usage.sub_agent_models = sessionUsage.sub_agent_models;
      if (usage.credits == null && sessionUsage.credits != null) usage.credits = sessionUsage.credits;
    }
  }

  // Precise per-session credit (AIU) cost — summed directly from THIS build
  // session's event stream, so it reflects only the target task and is immune
  // to account quota state / concurrent sessions. Overrides any quota-derived
  // estimate when available.
  const buildCredits = sumSessionCredits(join(logsDir, "build-events.jsonl"));
  if (buildCredits != null) usage.credits = buildCredits;

  entry.sessionTime = usage.session_time;
  entry.apiTime = usage.api_time;
  entry.codeChanges = usage.code_changes;
  entry.premiumRequests = usage.premium_requests;
  entry.credits = usage.credits;
  {
    // The CLI can serve a different model than requested (e.g. a retired
    // model id silently mapped to its successor); usage is keyed by the model
    // that actually ran.
    const used = Object.keys(usage.models || {});
    if (used.length && !used.includes(entry.model)) {
      log(`  ⚠️  Model mismatch: requested ${entry.model}, session used ${used.join(", ")}`);
    }
  }
  if (usage.models) {
    const firstModel = Object.keys(usage.models)[0];
    if (firstModel) {
      entry.inputTokens = usage.models[firstModel].input;
      entry.outputTokens = usage.models[firstModel].output;
      entry.cachedTokens = usage.models[firstModel].cached;
    }
  }
  // Populate sub-agent token breakdown from session-state shutdown events
  // (may be empty if sub-agent shutdowns lack modelMetrics)
  if (usage.sub_agent_models && Object.keys(usage.sub_agent_models).length > 0) {
    let subIn = 0, subOut = 0, subCached = 0;
    for (const m of Object.values(usage.sub_agent_models) as any[]) {
      subIn += parseTokenString(m.input);
      subOut += parseTokenString(m.output);
      subCached += parseTokenString(m.cached);
    }
    entry.subAgentInputTokens = formatTokenCount(subIn);
    entry.subAgentOutputTokens = formatTokenCount(subOut);
    entry.subAgentCachedTokens = formatTokenCount(subCached);
    entry.subAgentCount = usage.sub_agent_count || 0;
  } else if (buildResult.tokenTotals.subAgentTotalTokens > 0) {
    // Fallback: use totalTokens from subagent.completed events (no cache breakdown)
    entry.subAgentInputTokens = formatTokenCount(buildResult.tokenTotals.subAgentTotalTokens);
    entry.subAgentCount = buildResult.tokenTotals.subAgentCount;
    entry.subAgentDetails = buildResult.tokenTotals.subAgentDetails;
    // Also store in usage for persistence
    usage.sub_agent_count = buildResult.tokenTotals.subAgentCount;
    usage.sub_agent_total_tokens = formatTokenCount(buildResult.tokenTotals.subAgentTotalTokens);
    usage.sub_agent_details = buildResult.tokenTotals.subAgentDetails;
  }

  // Write aggregated session usage to logs dir
  if (Object.keys(usage).length > 0) {
    writeFileSync(
      join(logsDir, "session-usage.json"),
      JSON.stringify({
        build_session_id: buildSessionId || null,
        ...usage,
      }, null, 2)
    );
  }

  // ── 8. Build ──
  setStatus("dotnet_build");
  if (agentConfig.build_command) {
    banner("CUSTOM BUILD", "🔨", "cyan");
    const customCsproj = findCsproj(workDir);
    const expandedBuildCmd = agentConfig.build_command
      .replace(/\{app_dir\}/g, workDir)
      .replace(/\{app_name\}/g, appName)
      .replace(/\{csproj\}/g, customCsproj ? `"${customCsproj}"` : "");
    entry.builds = await customBuild(expandedBuildCmd, workDir, logsDir, callbacks, log);
    if (customCsproj) (entry as any)._csproj = customCsproj;
    log(`  ${entry.builds ? "PASS ✅" : "FAIL ❌"}`);
    if (!entry.builds) {
      banner("FAILED: Custom build failed", "❌", "red");
      entry.runs = false;
      entry.score = 0;
      entry.failReason = "Build failed";
    }
  } else {
    banner("DOTNET BUILD", "🔨", "cyan");
    const dotnetResult = await defaultDotnetBuild(workDir, logsDir, globalConfig, callbacks, log);
    if (!dotnetResult.csproj) {
      banner("FAILED: No .csproj found", "❌", "red");
      entry.builds = false;
      entry.runs = false;
      entry.score = 0;
      entry.failReason = "No csproj";
    } else {
      entry.builds = dotnetResult.success;
      log(`  ${entry.builds ? "PASS ✅" : "FAIL ❌"}`);
      if (!entry.builds) {
        banner("FAILED: dotnet build failed", "❌", "red");
        entry.runs = false;
        entry.score = 0;
        entry.failReason = "Build failed";
      }
      // Store csproj for launch phase
      (entry as any)._csproj = dotnetResult.csproj;
    }
  }

  // If the build failed AND the session was blocked by the content-safety
  // filter, reclassify the failure so the dashboard surfaces these flakes
  // separately from genuine build failures.
  if (entry.builds === false && detectContentFilterBlock(join(logsDir, "build-events.jsonl"))) {
    entry.failReason = "Content filter blocked";
    (entry as any)._contentFilterBlocked = true;
  }

  // Rules v2: automatic project/build checks, including gate G0 (right framework).
  let gateFailed = false;
  if (entry.builds) {
    const checks = collectHarnessQualityChecks((entry as any)._csproj, logsDir, scenarioConfig.framework ?? "winui");
    (entry as any)._qualityChecks = checks;
    if (checks.length) {
      log("  Quality checks (source/build):");
      logQualityChecks(checks, log);
    }
    const gate = checks.find((c) => c.kind === "gate" && c.status === "fail");
    if (gate) {
      gateFailed = true;
      banner(`FAILED: gate ${gate.id} — ${gate.label}`, "❌", "red");
      entry.runs = false;
      entry.score = 0;
      entry.failReason = `Gate ${gate.id} failed: ${gate.detail || gate.label}`;
    }
  }

  // Rules v2 G3: scenario artifact gates (Native AOT), run by the harness.
  if (entry.builds && !gateFailed && scenarioConfig.gates?.length) {
    setStatus("launching");
    const artifactChecks = await runArtifactGates(scenarioConfig, (entry as any)._csproj, workDir, trialDir, log, banner);
    (entry as any)._qualityChecks = [...(((entry as any)._qualityChecks as QualityCheck[]) || []), ...artifactChecks];
    const gate = artifactChecks.find((c) => c.status === "fail");
    if (gate) {
      gateFailed = true;
      banner(`FAILED: gate ${gate.id} — ${gate.label}`, "❌", "red");
      entry.runs = false;
      entry.score = 0;
      entry.failReason = `Gate ${gate.id} failed: ${gate.detail || gate.label}`;
    }
  }

  let elapsedStr = "0m 0s";
  try {
  // ── 9. Launch ──
  if (entry.builds && !gateFailed) {
    setStatus("launching");
    if (agentConfig.launch_command) {
      banner("LAUNCH APP (custom)", "🚀", "cyan");
      const expandedLaunchCmd = agentConfig.launch_command
        .replace(/\{app_dir\}/g, workDir)
        .replace(/\{app_name\}/g, appName);
      const launchResult = await customLaunch(
        expandedLaunchCmd,
        expandedLaunchDetect,
        workDir,
        log
      );
      entry.runs = launchResult.success;
      appPid = launchResult.pid;
      appStartedAt = appPid ? readWindowsProcessStartTime(Number(appPid)) : undefined;
    } else {
      banner("LAUNCH APP", "🚀", "cyan");
      const csproj = (entry as any)._csproj as string | undefined;
      if (csproj) {
        const buildOutputPath = join(trialDir, "build-output.txt");
        const buildOutputText = existsSync(buildOutputPath) ? readFileSync(buildOutputPath, "utf-8") : undefined;
        const launchResult = await defaultWinappLaunch(
          workDir, appName, csproj, agentConfig.launch_mode, callbacks, log, buildOutputText
        );
        entry.runs = launchResult.success;
        appPid = launchResult.pid;
        appStartedAt = appPid ? readWindowsProcessStartTime(Number(appPid)) : undefined;
        releasePackageRegistration = launchResult.releasePackageLock;
        launchStagingDir = launchResult.stagingDir;
      } else {
        entry.runs = false;
      }
    }
    log(`  ${entry.runs ? "PASS ✅ App running" : "FAIL ❌ No window"}`);
    if (!entry.runs) {
      entry.score = 0;
      banner("App didn't run — skipping validation", "⏭️", "yellow");
    } else if (appPid) {
      const uiChecks = await runUiQualityChecks(String(appPid), workDir, log);
      if (uiChecks.length) {
        log("  Quality checks (UI):");
        logQualityChecks(uiChecks, log);
        (entry as any)._qualityChecks = [...(((entry as any)._qualityChecks as QualityCheck[]) || []), ...uiChecks];
      }
    }
  }

  // ─── VALIDATION ─── (only if app is running)
  //
  // Skip the LLM-judge step when either the per-task `entry.skipValidation`
  // flag (flat run-meta) OR the global `SKIP_VALIDATION=1` env (set via
  // headless.ts `--skip-validation`, or a calling shell) is set. The trial
  // still gets builds/runs metrics, but no `score` and no `validation` block
  // in results.json — used by sweeps (and advanced flat runs that do their own
  // scoring as separate tasks) that only care whether the code compiles +
  // launches.
  if (entry.runs && !(entry.skipValidation || process.env.SKIP_VALIDATION)) {
    setStatus("validating");
    banner("VALIDATION", "🔍", "magenta");

  const valTemplate = loadValidationPrompt(agentConfig.framework_hint);
  const uiHarnessSource = join(benchRoot, "common", "ui-test-harness.ps1");
  const uiHarnessTarget = join(trialDir, "ui-test-harness.ps1");
  const winAppToolchain = isWindows ? resolveWinAppToolchain() : null;
  if (winAppToolchain && existsSync(uiHarnessSource)) copyFileSync(uiHarnessSource, uiHarnessTarget);
  // Resolve the actual process name for macOS (may differ from appName for bare agents)
  const actualAppProcess = detectMacAppExecutable(workDir) || appName;
  let valPrompt = valTemplate
    .replace(/\{original_prompt\}/g, promptRaw.trim())
    .replace(/\{app_name\}/g, actualAppProcess)
    .replace(/\{app_pid\}/g, appPid || "")
    .replace(/\{winapp_path\}/g, winAppToolchain?.executable || "")
    .replace(/\{task_type\}/g, scenarioConfig.type)
    .replace(/\{results_dir\}/g, trialDir)
    .replace(/\{reference_section\}/g, "")
    .replace(/\{original_app_name\}/g, "")
    .replace(/\{test_image_section\}/g, "");

  // Add requirements
  if (scenarioConfig.requirements && scenarioConfig.requirements.length > 0) {
    let reqSection = "## Scenario-specific requirements\n\n";
    scenarioConfig.requirements.forEach((r, i) => {
      reqSection += `${i + 1}. ${r}\n`;
    });
    valPrompt = valPrompt.replace(/\{scenario_requirements\}/g, reqSection);
  } else {
    valPrompt = valPrompt.replace(/\{scenario_requirements\}/g, "");
  }

  // Add test assets
  if (scenarioConfig.test_assets && scenarioConfig.test_assets.length > 0) {
    let assetSection = "\n## Test Assets\nUse these assets to test the app:\n";
    for (const asset of scenarioConfig.test_assets) {
      assetSection += `\n- **${asset.name}**: \`${asset.path}\``;
      if (asset.description) assetSection += `\n  ${asset.description}`;
    }
    valPrompt += assetSection;
  }

  // Add test notes
  if (scenarioConfig.test_notes) {
    valPrompt += `\n\n## Test Notes\n${scenarioConfig.test_notes}`;
  }

  // Point validation at the actual project directory (may be a subdirectory of workDir)
  const valCsproj = (entry as any)._csproj as string | undefined;
  const projectDir = valCsproj ? join(valCsproj, "..") : workDir;
  valPrompt += `\n\n## Project source code location\nThe app source code is at: ${projectDir}\n`;
  if (winAppToolchain) {
    valPrompt += `\n## Validation harness\nWinApp path: ${winAppToolchain.executable}\nWinApp version: ${winAppToolchain.version}\nUI helper: ${uiHarnessTarget}\nLaunch PID: ${appPid || ""}\n`;
  }

  // Validation goes through the SDK too. The legacy `copilot -p … --yolo` CLI
  // invocation hits "Native addon 'runtime' not found for win32-arm64" on arm64
  // hosts; the SDK (JSON-RPC client) doesn't share that native-addon dependency.
  const judgeModel = getJudgeModel();
  (entry as any)._judgeModel = judgeModel;
  log(`  Judge model: ${judgeModel}`);
  const valResult = await runCopilotSdkSession({
    prompt: valPrompt,
    model: judgeModel,
    workspace: trialDir,
    configDir: trialConfigDir,
    eventsFile: join(logsDir, "validation-events.jsonl"),
    onOutput: callbacks.onOutput,
    timeoutMs: validationWallClockMinutes * 60 * 1000,
    requireValidationJson: true,
  });
  writeFileSync(join(logsDir, "validation-log.txt"), eventsToReadableText(join(logsDir, "validation-events.jsonl")));

  if (valResult.sessionId) {
    entry.validationSessionId = valResult.sessionId;
    log(`  Validation session ID: ${entry.validationSessionId}`);
  }

  // Parse validation scores from the readable text (not raw JSONL events)
  const validationText = eventsToReadableText(join(logsDir, "validation-events.jsonl"));
  let validation = parseValidationJson(validationText);

  // If validation ended without scoring JSON, ask for a focused scoring follow-up.
  if (!validation && entry.validationSessionId) {
    banner("VALIDATION INCOMPLETE — requesting scores", "⏭️", "yellow");
    log("  Validation ended before producing scores. Asking for JSON based on completed evidence...");

    const followUpPrompt = `Based on everything you've already checked and observed, output your evaluation JSON now. Do NOT do any more investigation. Reconcile brittle script failures against screenshots, UI-tree evidence, and source before scoring. Output ONLY the JSON block in a \`\`\`json code fence.`;
    const followUpResult = await runCopilotProcess(
      [`--resume=${entry.validationSessionId}`, "-p", followUpPrompt, "--yolo", "--model", judgeModel, "--config-dir", trialConfigDir],
      trialDir,
      join(logsDir, "validation-followup-events.jsonl"),
      callbacks.onOutput,
      undefined, // no token update
      5 * 60 * 1000,  // 5 minute timeout for follow-up
    );

    // Append follow-up transcript to validation log
    const followUpText = "\n\n=== VALIDATION TIMEOUT FOLLOW-UP ===\n" + eventsToReadableText(join(logsDir, "validation-followup-events.jsonl"));
    appendFileSync(join(logsDir, "validation-log.txt"), followUpText);

    validation = parseValidationJson(followUpText);
    if (validation) {
      log("  Follow-up produced scores successfully");
    } else {
      log("  Follow-up also failed to produce scores");
    }
  }

  if (validation) {
    applyValidationScore(entry, validation, scenarioConfig, log);
  } else {
    if (valResult.timedOut) {
      log("  ERROR: Validation timed out and failed to produce scores");
      entry.failReason = "Validation timed out";
    } else if (valResult.completionReason === "session_error") {
      const detail = valResult.output?.split("\n")[0] || "SDK session error";
      log(`  ERROR: Validation infrastructure failed: ${detail}`);
      entry.failReason = `Validation infrastructure failed: ${detail}`;
    } else {
      log("  ERROR: Validation completed without scoring JSON");
      entry.failReason = "Validation produced no score";
    }
    entry.score = 0;
  }
  } // end if (entry.runs) for validation

  // ─── SAVE RESULTS ───
  entry.finishedAt = new Date();
  const runElapsed = entry.startedAt
    ? Math.round((entry.finishedAt.getTime() - entry.startedAt.getTime()) / 1000)
    : 0;
  elapsedStr = `${Math.floor(runElapsed / 60)}m ${runElapsed % 60}s`;
  saveResults(trialDir, entry, scenarioConfig, usage);

  } finally {
    // Validation failures must not strand the launched app,
    // package registration lock, staging directory, or short SDK state.
    banner("CLEANUP & RESULTS", "✅", "green");
    await cleanupApps(true);
  }

  // ─── FINALIZE ───
  setStatus(entry.failReason ? "failed" : "done");

  banner(`DONE: ${entry.score ?? 0}/100 in ${elapsedStr}`, entry.failReason ? "❌" : "✅", entry.failReason ? "red" : "green");
  log(`  Build: ${entry.builds ? "✅" : "❌"} | Run: ${entry.runs ? "✅" : "❌"} | Score: ${entry.score ?? 0}/100`);
  log(`  Copilot: ${entry.sessionTime || "—"} | Elapsed: ${elapsedStr}`);
  if (entry.failReason) log(`  Failure: ${entry.failReason}`);
}

// =============================================================================
// Rules v2: harness quality checks + scoring (reports/plans/11)
// =============================================================================

/** Source + build-warning checks for the built project. Empty when there is no .csproj. */
function collectHarnessQualityChecks(
  csproj: string | undefined,
  logsDir: string,
  framework: string,
): QualityCheck[] {
  if (!csproj) return [];
  const checks = evaluateSourceChecks(collectProjectFiles(projectDirFor(csproj)), framework);
  const buildOutputPath = join(logsDir, "build-output.txt");
  if (existsSync(buildOutputPath)) {
    try { checks.push(evaluateBuildWarnings(readFileSync(buildOutputPath, "utf-8"))); } catch { /* unreadable */ }
  }
  return checks;
}

/** Remove the project's Release build/publish outputs so the gate's publish is clean and reprints all warnings. */
function removeReleaseOutputs(projectDir: string): void {
  for (const base of ["bin", "obj"]) {
    const root = join(projectDir, base);
    const visit = (dir: string, depth: number) => {
      let names: string[] = [];
      try { names = readdirSync(dir); } catch { return; }
      for (const name of names) {
        const full = join(dir, name);
        try { if (!statSync(full).isDirectory()) continue; } catch { continue; }
        if (name.toLowerCase() === "release") { try { rmSync(full, { recursive: true, force: true }); } catch { /* locked */ } }
        else if (depth < 1) visit(full, depth + 1);
      }
    };
    visit(root, 0);
  }
}

/**
 * Gate G3 for scenarios with `gates: [native_aot]`: the harness publishes and
 * launches the app itself with `winapp run --aot -c Release`, so an agent
 * can't pass by launching a Debug build. The window must appear; the process
 * is closed afterwards and the Debug launch used for validation runs next.
 */
async function runNativeAotGate(
  csproj: string,
  workDir: string,
  trialDir: string,
  log: (msg: string) => void,
): Promise<QualityCheck> {
  const projectDir = projectDirFor(csproj);
  const files = collectProjectFiles(projectDir);
  if (!isWindows) return evaluateNativeAot("", files, { ok: false, reason: "Native AOT gate needs Windows" });
  const tc = resolveWinAppToolchain();
  removeReleaseOutputs(projectDir);
  const hostArch = osArch() === "arm64" ? "arm64" : "x64";
  const args = ["run", csproj, "--aot", "-c", "Release", "--arch", hostArch, "--detach", "--json"];
  const identity = packageIdentityForProject(csproj);
  const release = identity ? await acquirePackageLock(identity) : undefined;
  let launch: { ok: boolean; reason?: string } = { ok: false };
  let output = "";
  try {
    log(`  Publishing and launching: winapp ${args.join(" ")}`);
    const result = await runProcess(tc.executable, args, workDir, () => {}, 20 * 60_000, false);
    output = result.output;
    writeFileSync(join(trialDir, "aot-publish-output.txt"), output);
    // `--json` prints the result object after the build log.
    let json: any = null;
    const brace = output.search(/^\{/m);
    if (brace >= 0) { try { json = JSON.parse(output.slice(brace).trim()); } catch { /* not JSON */ } }
    const pid = Number(json?.ProcessId ?? json?.processId ?? output.match(/"(?:ProcessId|processId)"\s*:\s*(\d+)/)?.[1]);
    if (result.exitCode !== 0 || !pid) {
      const err = (typeof json?.Error === "string" ? json.Error : "") || output.trim().split(/\r?\n/).filter(Boolean).pop();
      launch = { ok: false, reason: `winapp run --aot failed (exit ${result.exitCode})${err ? `: ${err.slice(0, 200)}` : ""}` };
    } else {
      launch = { ok: false, reason: `Published app (PID ${pid}) showed no window` };
      for (let attempt = 1; attempt <= 6; attempt++) {
        try { process.kill(pid, 0); } catch { launch = { ok: false, reason: `Published app (PID ${pid}) exited during startup` }; break; }
        const w = await runProcess(tc.executable, ["ui", "list-windows", "-a", String(pid), "--json"], workDir, () => {}, 15_000, false);
        if (w.output.includes('"hwnd"')) {
          const title = w.output.match(/"title"\s*:\s*"([^"]*)"/)?.[1];
          // Still alive a moment later: a window that closes on a startup exception doesn't count.
          await new Promise((r) => setTimeout(r, 5000));
          try { process.kill(pid, 0); launch = { ok: true }; log(`  Published app PID ${pid} window: "${title ?? "?"}"`); }
          catch { launch = { ok: false, reason: `Published app (PID ${pid}) exited right after showing a window` }; }
          break;
        }
        await new Promise((r) => setTimeout(r, 5000));
      }
      try { process.kill(pid); } catch { /* already gone */ }
    }
  } finally {
    release?.();
  }
  return evaluateNativeAot(output, files, launch);
}

/** Runs the scenario's artifact gates (G3). Returns the checks; any "fail" is a gate failure. */
async function runArtifactGates(
  scenario: ScenarioConfig,
  csproj: string | undefined,
  workDir: string,
  trialDir: string,
  log: (msg: string) => void,
  banner: (stage: string, icon: string) => void,
): Promise<QualityCheck[]> {
  if (!scenario.gates?.includes("native_aot")) return [];
  banner("NATIVE AOT GATE (G3)", "📦");
  const check = csproj
    ? await runNativeAotGate(csproj, workDir, trialDir, log)
    : evaluateNativeAot("", [], { ok: false, reason: "No .csproj found" });
  logQualityChecks([check], log);
  return [check];
}

/**
 * Accessible names (Q0) and keyboard traversal (Q1) on the running app via
 * `winapp ui`. Runs under the trial's cooperative UI workflow (runProcess
 * passes WINAPP_UI_WORKFLOW_ID) and yields the desktop when done.
 */
export async function runUiQualityChecks(appPid: string, cwd: string, log: (msg: string) => void): Promise<QualityCheck[]> {
  if (!isWindows || !appPid) return [];
  const tc = resolveWinAppToolchain();
  const ui = (args: string[], timeout = 20000) => runProcess(tc.executable, ["ui", ...args], cwd, () => {}, timeout, false);
  const parse = (s: string) => { try { return JSON.parse(s); } catch { return null; } };
  const na = (id: string, label: string, detail: string): QualityCheck => ({ id, label, kind: "quality", source: "harness", status: "na", detail });
  try {
    // Right after launch the window may not be listed yet; give it a few seconds.
    let hwnd: unknown;
    for (let attempt = 0; attempt < 6 && !hwnd; attempt++) {
      if (attempt > 0) await new Promise((r) => setTimeout(r, 2000));
      const windows = parse((await ui(["list-windows", "-a", appPid, "--json"])).output);
      hwnd = (Array.isArray(windows) ? windows : [windows])
        .find((w: any) => w?.hwnd && w.title && w.title !== "PopupHost")?.hwnd;
    }
    if (!hwnd) {
      return [
        na("Q0", "Accessible names on interactive controls", "Main window not found"),
        na("Q1", "Keyboard navigation (Tab moves focus through controls)", "Main window not found"),
      ];
    }
    const inspect = parse((await ui(["inspect", "-w", String(hwnd), "--interactive", "--json"], 30000)).output);
    const elements = interactiveElements(inspect);
    const q0 = evaluateAccessibleNames(elements);

    const enabled = elements.filter((e) => e.isEnabled !== false && e.selector);
    // Caption buttons don't take keyboard focus, and Tab inside a text editor
    // inserts a tab instead of moving focus, so start from another control.
    const caption = /^(minimize|maximize|restore|close|system)$/i;
    const focusable = enabled.filter((e) =>
      !caption.test((e.name ?? "").trim()) && !caption.test((e.automationId ?? "").trim()) && !/^(Edit|Document)$/.test(String(e.type)));
    let q1: QualityCheck;
    if (enabled.length === 0) {
      q1 = evaluateKeyboardTraversal([], 0);
    } else {
      // Prefer focusing a control; if none accepts focus (e.g. IsTabStop=False
      // everywhere) fall back to wherever focus already is, so the Tab test
      // still measures what a keyboard user would experience.
      let focusError = "";
      let focusedOk = false;
      for (const candidate of focusable.slice(0, 3)) {
        const r = await ui(["focus", String(candidate.selector), "-w", String(hwnd)]);
        if (r.exitCode === 0) { focusedOk = true; break; }
        focusError = r.output.trim().split(/\r?\n/).pop()?.slice(0, 160) ?? "";
      }
      if (!focusedOk) focusedOk = !!parse((await ui(["get-focused", "-w", String(hwnd), "--json"])).output)?.hasFocus;
      if (!focusedOk) {
        q1 = na("Q1", "Keyboard navigation (Tab moves focus through controls)",
          `Could not focus a control (desktop busy or activation refused)${focusError ? `: ${focusError}` : ""}`);
      } else {
        const samples: any[] = [];
        for (let i = 0; i < 12; i++) {
          samples.push(parse((await ui(["get-focused", "-w", String(hwnd), "--json"])).output));
          await ui(["send-keys", "tab", "--via", "send-input", "-w", String(hwnd)]);
        }
        q1 = evaluateKeyboardTraversal(samples, enabled.length);
      }
    }
    return [q0, q1];
  } catch (err) {
    log(`  ⚠️ UI quality checks failed: ${err instanceof Error ? err.message : String(err)}`);
    return [];
  } finally {
    await ui(["yield"], 5000).catch(() => undefined);
  }
}

function logQualityChecks(checks: QualityCheck[], log: (msg: string) => void): void {
  for (const c of checks) {
    const icon = c.status === "pass" ? "✅" : c.status === "fail" ? "❌" : "➖";
    log(`    ${icon} ${c.id} ${c.label}${c.detail ? ` — ${c.detail}` : ""}`);
  }
}

/**
 * Score a trial from the judge's JSON plus the harness checks. Requirements
 * the judge didn't report count as failed so the denominator is always the
 * scenario's full requirement list.
 */
function applyValidationScore(
  entry: RunEntry,
  validation: Record<string, any>,
  scenarioConfig: ScenarioConfig,
  log: (msg: string) => void,
): void {
  const scenarioReqs = scenarioConfig.requirements || [];
  const reported = extractRequirementResults(validation, scenarioReqs);
  const byId = new Map(reported.map((r) => [r.id, r]));
  const reqResults: StructuredReqResult[] = scenarioReqs.length
    ? scenarioReqs.map((text, i) => byId.get(i + 1) ?? { id: i + 1, text, status: "fail", reason: "Not reported by the judge" })
    : reported;
  const checks = [...(((entry as any)._qualityChecks as QualityCheck[]) || []), ...judgeChecks(validation.quality)];
  const designScore = typeof validation.design_score === "number" ? validation.design_score : null;
  const breakdown = computeScoreV2({ builds: !!entry.builds, runs: !!entry.runs, checks, requirements: reqResults, designScore });

  entry.score = breakdown.score;
  entry.qualityBreakdown = formatBreakdown(breakdown);
  (entry as any)._validationData = {
    requirements: reqResults,
    scoreBreakdown: breakdown,
    qualityChecks: checks,
    ...(validation.project_score !== undefined ? {
      legacySubscores: {
        project: validation.project_score, ui: validation.ui_score,
        visual: validation.visual_score, functionality: validation.functionality_score,
      },
    } : {}),
  };
  const q = breakdown.quality;
  log(`  Score: ${breakdown.score}/100 (Reqs ${breakdown.requirements.passed}/${breakdown.requirements.total} · Quality ${q.passed}/${q.passed + q.failed} · Design ${breakdown.design ?? "–"})`);
}

function saveResults(
  trialDir: string,
  entry: RunEntry,
  config: ScenarioConfig,
  usage: Record<string, any>
) {
  // Extract structured validation data if available
  const valData = (entry as any)._validationData as {
    requirements: StructuredReqResult[];
    scoreBreakdown?: ScoreBreakdown;
    qualityChecks?: QualityCheck[];
    legacySubscores?: { project: number; ui: number; visual: number; functionality: number };
  } | undefined;
  const qualityChecks = valData?.qualityChecks ?? ((entry as any)._qualityChecks as QualityCheck[] | undefined);

  // Read build errors if build failed (the build writes into session-logs-dir)
  let buildErrors = "";
  if (!entry.builds) {
    const buildOutputPath = [join(trialDir, "session-logs-dir", "build-output.txt"), join(trialDir, "build-output.txt")]
      .find((p) => existsSync(p));
    if (buildOutputPath) {
      try {
        const output = readFileSync(buildOutputPath, "utf-8");
        // Extract error lines (MSBuild/dotnet error patterns)
        const errorLines = output.split("\n").filter(l =>
          /\berror\b/i.test(l) && !/\d+ Warning/.test(l) && !/Build succeeded/.test(l)
        );
        buildErrors = errorLines.slice(0, 20).join("\n").trim();
        if (!buildErrors) {
          // Fallback: last 30 lines
          buildErrors = output.split("\n").slice(-30).join("\n").trim();
        }
      } catch { /* ignore */ }
    }
  }

  const results: Record<string, any> = {
    trial: entry.trialName,
    scenario: entry.scenarioConfigName,
    condition: entry.condition,
    type: config.type,
    model: entry.model,
    iteration: entry.iteration,
    timestamp: new Date().toISOString(),
    session_ids: {
      build: entry.buildSessionId || null,
      validation: entry.validationSessionId || null,
    },
    completion_reason: entry.completionReason,
    metrics: {
      score: entry.score,
      builds: entry.builds,
      runs: entry.runs,
      time_and_tokens: usage,
      ...(valData ? {
        requirements: valData.requirements,
        ...(valData.scoreBreakdown ? { score_breakdown: valData.scoreBreakdown } : {}),
        ...(valData.legacySubscores ? { subscores: valData.legacySubscores } : {}),
      } : {}),
      ...(qualityChecks?.length ? { quality_checks: qualityChecks } : {}),
    },
    ...(entry.failReason ? { fail_reason: entry.failReason } : {}),
    ...(buildErrors ? { build_errors: buildErrors } : {}),
  };
  if ((entry as any)._setupScriptResults) {
    results.setup_scripts = (entry as any)._setupScriptResults;
  }
  if (entry.agentSource) {
    results.agent_source = entry.agentSource;
  }
  {
    const used = Object.keys(usage?.models || {});
    if (used.length) {
      results.models_used = used;
      if (!used.includes(entry.model)) results.model_mismatch = true;
    }
  }
  if ((entry as any)._judgeModel) {
    results.judge_model = (entry as any)._judgeModel;
  }
  if (isWindows) {
    try { results.toolchain = { winapp: resolveWinAppToolchain().version }; } catch { /* recorded elsewhere as a launch failure */ }
  }
  writeTextAtomic(join(trialDir, "results.json"), JSON.stringify(results, null, 2));
}

function saveRevalidationResults(
  trialDir: string,
  entry: RunEntry,
  scenarioConfig: ScenarioConfig
): void {
  let usage: Record<string, any> = {};
  const existingResults = join(trialDir, "results.json");
  if (existsSync(existingResults)) {
    try {
      const old = JSON.parse(readFileSync(existingResults, "utf-8"));
      usage = old.metrics?.time_and_tokens || {};
      entry.sessionTime = usage.session_time;
      entry.apiTime = usage.api_time;
      entry.codeChanges = usage.code_changes;
      if (usage.models) {
        const firstModel = Object.keys(usage.models)[0];
        if (firstModel) {
          entry.inputTokens = usage.models[firstModel].input;
          entry.outputTokens = usage.models[firstModel].output;
          entry.cachedTokens = usage.models[firstModel].cached;
        }
      }
      entry.buildSessionId = old.session_ids?.build || entry.buildSessionId;
      // Revalidation doesn't re-resolve plugins; keep the original provenance.
      if (!entry.agentSource && old.agent_source) entry.agentSource = old.agent_source;
    } catch {}
  }
  saveResults(trialDir, entry, scenarioConfig, usage);
}


// =============================================================================
// Revalidate — skip copilot build, just rebuild + launch + validate
// =============================================================================

export async function revalidateBenchmark(
  entry: RunEntry,
  runDir: string,
  callbacks: BenchmarkCallbacks
): Promise<void> {
  const trialDir = join(runDir, entry.trialName);
  const archiveDir = join(
    runDir,
    ".attempts",
    entry.trialName,
    `revalidate-${new Date().toISOString().replace(/[:.]/g, "-")}`
  );
  const evidence = [
    { source: join(trialDir, "results.json"), archiveName: "results.json" },
    { source: join(trialDir, "validation-events.jsonl"), archiveName: "revalidation-events.jsonl" },
    { source: join(trialDir, "validation-log.txt"), archiveName: "revalidation-log.txt" },
    {
      source: join(trialDir, "session-logs-dir", "validation-events.jsonl"),
      archiveName: "validation-events.jsonl",
    },
    {
      source: join(trialDir, "session-logs-dir", "validation-log.txt"),
      archiveName: "validation-log.txt",
    },
  ];
  const existingEvidence = evidence.filter(({ source }) => existsSync(source));
  if (existingEvidence.length > 0) {
    mkdirSync(archiveDir, { recursive: true });
    for (const { source, archiveName } of existingEvidence) {
      copyFileSync(source, join(archiveDir, archiveName));
    }
  }
  const cfg = loadAgentConfig(entry.pluginPath);
  const ctx = createTrialEnv(runDir, entry.trialName, cfg);
  ctx.uiWorkflowId = uiWorkflowIdFor(runDir, entry.id);
  const envBefore = isWindows ? snapshotUserEnv() : null;
  try {
    return await trialContext.run(ctx, () =>
      revalidateBenchmarkImpl(entry, runDir, callbacks)
    );
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    entry.status = "failed";
    entry.score = 0;
    entry.failReason = `Revalidation failed: ${detail}`;
    entry.finishedAt = new Date();
    callbacks.onOutput(`\nERROR: ${entry.failReason}\n`);
    callbacks.onStatusChange(entry);
    const scenario = loadScenario(entry.scenarioPath)?.config || {
      name: entry.scenarioConfigName,
      description: "",
      type: "new",
    };
    saveRevalidationResults(trialDir, entry, scenario);
    throw error;
  } finally {
    removeTrialEnv(ctx);
    await restoreMachineState(trialDir, envBefore, callbacks.onOutput);
  }
}

async function revalidateBenchmarkImpl(
  entry: RunEntry,
  runDir: string,
  callbacks: BenchmarkCallbacks
): Promise<void> {
  const globalConfig = loadGlobalConfig();

  // Find scenario config
  const scenarioResult = loadScenario(entry.scenarioPath);
  const scenarioConfig: ScenarioConfig = scenarioResult?.config || { name: entry.scenarioConfigName, description: "", type: "new" };

  const baseAppName = scenarioConfig.app_name || scenarioConfig.name;
  const runIndex = entry.iteration || 1;
  const condShort = entry.condition.replace(/\s*\[\d+\/\d+\]$/, "");
  const trialDir = join(runDir, entry.trialName);
  const workDir = join(trialDir, "app");

  // Load agent config for build/launch behavior
  const agentConfig = loadAgentConfig(entry.pluginPath);
  const appName = agentConfig.unique_app_name === false
    ? baseAppName
    : `${baseAppName}${condShort}${runIndex}`;

  const setStatus = (status: RunEntry["status"]) => {
    entry.status = status;
    callbacks.onStatusChange(entry);
  };
  const log = (msg: string) => callbacks.onOutput(msg + "\n");
  const banner = (stage: string, icon: string, color: "cyan" | "yellow" | "green" | "magenta" | "red" = "cyan") => {
    const colors: Record<string, string> = { cyan: "\x1b[36m", yellow: "\x1b[33m", green: "\x1b[32m", magenta: "\x1b[35m", red: "\x1b[31m" };
    const c = colors[color] || "";
    const reset = "\x1b[0m";
    callbacks.onOutput(`\n${c}${"━".repeat(60)}${reset}\n`);
    callbacks.onOutput(`${c}  ${icon}  ${stage}${reset}\n`);
    callbacks.onOutput(`${c}${"━".repeat(60)}${reset}\n\n`);
  };

  // Expand launch_detect template variables
  const expandedLaunchDetect = (agentConfig.launch_detect || "")
    .replace(/\{app_dir\}/g, workDir)
    .replace(/\{app_name\}/g, appName) || appName;
  let appPid: string | undefined;
  let appStartedAt: number | undefined;
  let releasePackageRegistration: (() => void) | undefined;
  let launchStagingDir: string | undefined;

  if (!existsSync(workDir)) {
    log(`  ERROR: App directory not found: ${workDir}`);
    entry.score = 0;
    entry.builds = false;
    entry.runs = false;
    entry.failReason = "No app directory";
    entry.finishedAt = new Date();
    setStatus("failed");
    saveRevalidationResults(trialDir, entry, scenarioConfig);
    return;
  }

  entry.startedAt = new Date();
  banner(`REVALIDATE: ${entry.condition}`, "🔄", "cyan");

  try {
  // Kill stale instances from prior trials (uninstallAppx:false → don't undo a fresh sideload)
  try { await killAppProcesses({ appName, baseAppName, expandedLaunchDetect, workDir, uninstallAppx: false }); } catch {}

  // ─── BUILD & LAUNCH ───
  setStatus("dotnet_build");
  if (agentConfig.build_command) {
    banner("CUSTOM BUILD", "🔨", "cyan");
    const customCsproj = findCsproj(workDir);
    const expandedBuildCmd = agentConfig.build_command
      .replace(/\{app_dir\}/g, workDir)
      .replace(/\{app_name\}/g, appName)
      .replace(/\{csproj\}/g, customCsproj ? `"${customCsproj}"` : "");
    entry.builds = await customBuild(expandedBuildCmd, workDir, trialDir, callbacks, log);
    if (customCsproj) (entry as any)._csproj = customCsproj;
    log(`  ${entry.builds ? "PASS ✅" : "FAIL ❌"}`);
    if (!entry.builds) {
      entry.runs = false; entry.score = 0; entry.failReason = "Build failed";
      entry.finishedAt = new Date();
      setStatus("failed");
      saveRevalidationResults(trialDir, entry, scenarioConfig);
      return;
    }
  } else {
    banner("DOTNET BUILD", "🔨", "cyan");
    const dotnetResult = await defaultDotnetBuild(workDir, trialDir, globalConfig, callbacks, log);
    if (!dotnetResult.csproj) {
      entry.builds = false; entry.runs = false; entry.score = 0;
      entry.failReason = "No csproj";
      setStatus("failed");
      entry.finishedAt = new Date();
      saveRevalidationResults(trialDir, entry, scenarioConfig);
      return;
    }
    entry.builds = dotnetResult.success;
    log(`  ${entry.builds ? "PASS ✅" : "FAIL ❌"}`);
    if (!entry.builds) {
      entry.runs = false; entry.score = 0; entry.failReason = "Build failed";
      entry.finishedAt = new Date();
      setStatus("failed");
      saveRevalidationResults(trialDir, entry, scenarioConfig);
      return;
    }
    (entry as any)._csproj = dotnetResult.csproj;
  }

  // Rules v2: automatic project/build checks, including gate G0 (right framework).
  {
    const checks = collectHarnessQualityChecks((entry as any)._csproj, trialDir, scenarioConfig.framework ?? "winui");
    (entry as any)._qualityChecks = checks;
    if (checks.length) {
      log("  Quality checks (source/build):");
      logQualityChecks(checks, log);
    }
    const gate = checks.find((c) => c.kind === "gate" && c.status === "fail");
    if (gate) {
      entry.runs = false; entry.score = 0;
      entry.failReason = `Gate ${gate.id} failed: ${gate.detail || gate.label}`;
      entry.finishedAt = new Date();
      setStatus("failed");
      saveRevalidationResults(trialDir, entry, scenarioConfig);
      return;
    }
    const artifactChecks = await runArtifactGates(scenarioConfig, (entry as any)._csproj, workDir, trialDir, log, banner);
    (entry as any)._qualityChecks = [...checks, ...artifactChecks];
    const artifactGate = artifactChecks.find((c) => c.status === "fail");
    if (artifactGate) {
      entry.runs = false; entry.score = 0;
      entry.failReason = `Gate ${artifactGate.id} failed: ${artifactGate.detail || artifactGate.label}`;
      entry.finishedAt = new Date();
      setStatus("failed");
      saveRevalidationResults(trialDir, entry, scenarioConfig);
      return;
    }
  }

  // ─── LAUNCH ───
  setStatus("launching");
  if (agentConfig.launch_command) {
    banner("LAUNCH APP (custom)", "🚀", "cyan");
    const expandedLaunchCmd = agentConfig.launch_command
      .replace(/\{app_dir\}/g, workDir)
      .replace(/\{app_name\}/g, appName);
    const launchResult = await customLaunch(
      expandedLaunchCmd,
      expandedLaunchDetect,
      workDir,
      log
    );
    entry.runs = launchResult.success;
    appPid = launchResult.pid;
    appStartedAt = appPid ? readWindowsProcessStartTime(Number(appPid)) : undefined;
  } else {
    banner("LAUNCH APP", "🚀", "cyan");
    const csproj = (entry as any)._csproj as string | undefined;
    if (csproj) {
      const buildOutputPath = join(trialDir, "build-output.txt");
      const buildOutputText = existsSync(buildOutputPath) ? readFileSync(buildOutputPath, "utf-8") : undefined;
      const launchResult = await defaultWinappLaunch(
        workDir, appName, csproj, agentConfig.launch_mode, callbacks, log, buildOutputText
      );
      entry.runs = launchResult.success;
      appPid = launchResult.pid;
      appStartedAt = appPid ? readWindowsProcessStartTime(Number(appPid)) : undefined;
      releasePackageRegistration = launchResult.releasePackageLock;
      launchStagingDir = launchResult.stagingDir;
    } else {
      entry.runs = false;
    }
  }

  log(`  ${entry.runs ? "PASS ✅" : "FAIL ❌"}`);
  if (!entry.runs) {
    entry.score = 0;
  } else if (appPid) {
    const uiChecks = await runUiQualityChecks(String(appPid), workDir, log);
    if (uiChecks.length) {
      log("  Quality checks (UI):");
      logQualityChecks(uiChecks, log);
      (entry as any)._qualityChecks = [...(((entry as any)._qualityChecks as QualityCheck[]) || []), ...uiChecks];
    }
  }

  // ─── VALIDATION ─── (per-task skipValidation OR global SKIP_VALIDATION env)
  if (entry.runs && !(entry.skipValidation || process.env.SKIP_VALIDATION)) {
    setStatus("validating");
    banner("VALIDATION", "🔍", "magenta");

    const promptRaw = loadPrompt(entry.scenarioPath);
    const valTemplate = loadValidationPrompt(agentConfig.framework_hint);
    const uiHarnessSource = join(benchRoot, "common", "ui-test-harness.ps1");
    const uiHarnessTarget = join(trialDir, "ui-test-harness.ps1");
    const winAppToolchain = isWindows ? resolveWinAppToolchain() : null;
    if (winAppToolchain && existsSync(uiHarnessSource)) {
      copyFileSync(uiHarnessSource, uiHarnessTarget);
    }
    const actualAppProcess = detectMacAppExecutable(workDir) || appName;
    let valPrompt = valTemplate
      .replace(/\{original_prompt\}/g, promptRaw.trim())
      .replace(/\{app_name\}/g, actualAppProcess)
      .replace(/\{app_pid\}/g, appPid || "")
      .replace(/\{winapp_path\}/g, winAppToolchain?.executable || "")
      .replace(/\{task_type\}/g, scenarioConfig.type)
      .replace(/\{results_dir\}/g, trialDir)
      .replace(/\{reference_section\}/g, "")
      .replace(/\{original_app_name\}/g, "")
      .replace(/\{test_image_section\}/g, "")
      .replace(/\{scenario_requirements\}/g, "");

    if (scenarioConfig.requirements) {
      valPrompt += "\n\n## Scenario Requirements\n" + scenarioConfig.requirements.map((r, i) => `${i+1}. ${r}`).join("\n");
    }

    if (scenarioConfig.test_assets && scenarioConfig.test_assets.length > 0) {
      let assetSection = "\n## Test Assets\nUse these assets to test the app:\n";
      for (const asset of scenarioConfig.test_assets) {
        assetSection += `\n- **${asset.name}**: \`${asset.path}\``;
        if (asset.description) assetSection += `\n  ${asset.description}`;
      }
      valPrompt += assetSection;
    }

    if (scenarioConfig.test_notes) {
      valPrompt += `\n\n## Test Notes\n${scenarioConfig.test_notes}`;
    }

    // Point validation at the actual project directory (may be a subdirectory of workDir)
    const revalCsproj = (entry as any)._csproj as string | undefined;
    const revalProjectDir = revalCsproj ? join(revalCsproj, "..") : workDir;
    valPrompt += `\n\n## Project source code location\nThe app source code is at: ${revalProjectDir}\n`;
    if (winAppToolchain) {
      valPrompt += `\n## Validation harness\nWinApp path: ${winAppToolchain.executable}\nWinApp version: ${winAppToolchain.version}\nUI helper: ${uiHarnessTarget}\nLaunch PID: ${appPid || ""}\n`;
    }

    // Revalidate validation also goes through the SDK (arm64 "Native addon
    // 'runtime' not found for win32-arm64" affects the CLI path).
    const valEventsFile = join(trialDir, "validation-events.jsonl");
    try { writeFileSync(valEventsFile, ""); } catch {}
    const judgeModel = getJudgeModel();
    (entry as any)._judgeModel = judgeModel;
    log(`  Judge model: ${judgeModel}`);
    const valResult = await runCopilotSdkSession({
      prompt: valPrompt,
      model: judgeModel,
      workspace: workDir,
      eventsFile: valEventsFile,
      onOutput: callbacks.onOutput,
      timeoutMs: 40 * 60 * 1000,
      requireValidationJson: true,
    });
    const valReadable = eventsToReadableText(valEventsFile);
    writeFileSync(join(trialDir, "validation-log.txt"), valReadable);

    let validation = parseValidationJson(valReadable);

    // If validation ended without JSON, ask for a focused scoring follow-up.
    if (!validation) {
      banner("VALIDATION INCOMPLETE — requesting scores", "⏭️", "yellow");
      log("  Validation ended before producing scores. Asking for JSON based on completed evidence...");

      const valSessionId = valResult.sessionId;
      if (valSessionId) {
        const followUpPrompt = `Based on everything you've already checked and observed, output your evaluation JSON now. Do NOT do any more investigation. Reconcile brittle script failures against screenshots, UI-tree evidence, and source before scoring. Output ONLY the JSON block in a \`\`\`json code fence.`;
        const followUpResult = await runProcess(
          COPILOT_EXE,
          [`--resume=${valSessionId}`, "-p", followUpPrompt, "--yolo", "--model", judgeModel],
          workDir,
          callbacks.onOutput,
          5 * 60 * 1000,
          false
        );
        appendFileSync(join(trialDir, "validation-log.txt"), "\n\n=== VALIDATION TIMEOUT FOLLOW-UP ===\n" + followUpResult.output);
        validation = parseValidationJson(followUpResult.output);
        if (validation) log("  Follow-up produced scores successfully");
        else log("  Follow-up also failed to produce scores");
      }
    }

    if (validation) {
      applyValidationScore(entry, validation, scenarioConfig, log);
    } else {
      if (valResult.timedOut) {
        log("  ERROR: Validation timed out and failed to produce scores");
        entry.failReason = "Validation timed out";
      } else if (valResult.completionReason === "session_error") {
        const detail = valResult.output?.split("\n")[0] || "SDK session error";
        log(`  ERROR: Validation infrastructure failed: ${detail}`);
        entry.failReason = `Validation infrastructure failed: ${detail}`;
      } else {
        log("  ERROR: Validation completed without scoring JSON");
        entry.failReason = "Validation produced no score";
      }
      entry.score = 0;
    }
  }

  } finally {
    // Revalidation exceptions must not leave a window, package lock, or short
    // staging directory behind for the next concurrent trial.
    try {
      await killAppProcesses({ appPid, appStartedAt, appName, baseAppName, expandedLaunchDetect, workDir });
    } catch {}
    if (isWindows) {
      const project = findCsproj(workDir);
      const projectDir = project ? dirname(project) : workDir;
      const manifest = ["Package.appxmanifest", "appxmanifest.xml"]
        .map((name) => join(projectDir, name))
        .find((candidate) => existsSync(candidate));
      if (manifest) {
        try {
          const toolchain = resolveWinAppToolchain();
          await runProcess(
            toolchain.executable,
            ["unregister", "--manifest", manifest, "--force", "--json"],
            projectDir,
            () => {},
            30_000,
            false
          );
        } catch {}
      }
    }
    try { releasePackageRegistration?.(); } catch {}
    if (launchStagingDir) {
      try { rmSync(launchStagingDir, { recursive: true, force: true }); } catch {}
    }
  }

  // ─── CLEANUP & SAVE ───
  // Remove screenshot files left by the validation agent (keep final-screenshot/screenshot for HTML report)
  const keepScreenshots = /^(final-screenshot|screenshot)\.(png|jpg|jpeg|bmp)$/i;
  for (const dir of [trialDir, workDir]) {
    if (!existsSync(dir)) continue;
    try {
      for (const f of readdirSync(dir)) {
        if (/\.(png|jpg|jpeg|bmp)$/i.test(f) && !keepScreenshots.test(f)) {
          rmSync(join(dir, f), { force: true });
        }
      }
    } catch {}
  }

  // Remove bulky build artifacts (xcodebuild DerivedData, .NET bin/obj)
  const bulkDirs = [
    join(workDir, "build"),
    join(workDir, "build-derived"),
    join(workDir, "DerivedData"),
    join(workDir, "bin"),
    join(workDir, "obj"),
  ];
  for (const d of bulkDirs) {
    if (existsSync(d)) {
      try { rmSync(d, { recursive: true, force: true }); } catch {}
    }
  }
  if (existsSync(workDir)) {
    try {
      for (const sub of readdirSync(workDir)) {
        const subPath = join(workDir, sub);
        if (!statSync(subPath).isDirectory()) continue;
        for (const target of ["bin", "obj"]) {
          const t = join(subPath, target);
          if (existsSync(t)) {
            try { rmSync(t, { recursive: true, force: true }); } catch {}
          }
        }
      }
    } catch {}
  }

  entry.finishedAt = new Date();
  setStatus(entry.score && entry.score > 10 ? "done" : "failed");

  saveRevalidationResults(trialDir, entry, scenarioConfig);
}
