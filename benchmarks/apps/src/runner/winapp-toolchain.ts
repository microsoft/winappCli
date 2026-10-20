import { spawnSync } from "child_process";
import { existsSync } from "fs";
import { basename, dirname, extname, join } from "path";

const MIN_VERSION = [0, 6, 0] as const;
const REQUIRED_RUN_OPTIONS = [
  "--arch",
  "--clean",
  "--detach",
  "--json",
  "--no-build",
  "--no-launch",
] as const;
const REQUIRED_UI_COMMANDS = [
  "focus",
  "get-value",
  "inspect",
  "invoke",
  "list-windows",
  "screenshot",
  "scroll-into-view",
  "search",
  "set-value",
  "status",
  "wait-for",
] as const;

export interface WinAppToolchain {
  executable: string;
  dotnetExecutable: string;
  version: string;
  schemaVersion: string;
  uiCommands: Set<string>;
}

let cached: WinAppToolchain | null = null;

/**
 * Pick a natively spawnable executable from `where` output, in PATH order.
 * An npm global install puts `winapp`/`winapp.cmd` wrappers first; those can't
 * be spawned with `shell: false`, so map them to the package's bundled exe.
 */
export function pickNativeExecutable(candidates: string[], arch: string = process.arch): string | undefined {
  if (process.platform !== "win32") return candidates[0];
  const rid = arch === "arm64" ? "win-arm64" : "win-x64";
  for (const candidate of candidates) {
    const ext = extname(candidate).toLowerCase();
    if (ext === ".exe") return candidate;
    if (ext === "" || ext === ".cmd" || ext === ".ps1") {
      const bundled = join(dirname(candidate), "node_modules", "@microsoft", "winappcli", "bin", rid, `${basename(candidate, extname(candidate))}.exe`);
      if (existsSync(bundled)) return bundled;
    }
  }
  return undefined;
}

function resolveExecutable(name: string): string {
  const command = process.platform === "win32" ? "where" : "which";
  const result = spawnSync(command, [name], { encoding: "utf8", shell: false });
  const candidates = (result.stdout ?? "").split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const executable = result.status === 0 ? pickNativeExecutable(candidates) : undefined;
  if (!executable) {
    throw new Error(`${name} was not found on PATH${candidates.length ? ` as a native executable (found ${candidates.join(", ")})` : ""}. Run /winui-setup, then retry.`);
  }
  return executable;
}

function run(executable: string, args: string[]): string {
  const result = spawnSync(executable, args, {
    encoding: "utf8",
    shell: false,
    env: process.env,
    maxBuffer: 16 * 1024 * 1024,
  });
  if (result.status !== 0) {
    throw new Error((result.stderr || result.stdout || `winapp exited ${result.status}`).trim());
  }
  return result.stdout.trim();
}

function versionTuple(version: string): [number, number, number] {
  const match = version.match(/(\d+)\.(\d+)\.(\d+)/);
  if (!match) throw new Error(`Could not parse WinApp CLI version: ${version}`);
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

/** Compare major.minor.patch only (prerelease suffixes ignored): <0, 0, >0. */
export function compareVersionCore(a: string, b: string): number {
  const x = versionTuple(a);
  const y = versionTuple(b);
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}

function isAtLeast(actual: [number, number, number]): boolean {
  for (let i = 0; i < MIN_VERSION.length; i++) {
    if (actual[i] > MIN_VERSION[i]) return true;
    if (actual[i] < MIN_VERSION[i]) return false;
  }
  return true;
}

function optionNames(schema: any): Set<string> {
  if (Array.isArray(schema?.options)) {
    return new Set(schema.options.flatMap((option: any) => [option.name, ...(option.aliases || [])]).filter(Boolean));
  }
  return new Set(Object.keys(schema?.options || {}));
}

export function resolveWinAppToolchain(): WinAppToolchain {
  if (cached) return cached;
  const executable = resolveExecutable("winapp");
  const dotnetExecutable = resolveExecutable("dotnet");
  const versionOutput = run(executable, ["--version"]);
  const version = versionOutput.match(/\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?/)?.[0] ?? versionOutput;
  if (!isAtLeast(versionTuple(version))) {
    throw new Error(`WinApp CLI 0.6.0 or newer is required; found ${version}. Run /winui-setup.`);
  }

  const runSchema = JSON.parse(run(executable, ["run", "--cli-schema"]));
  const uiSchema = JSON.parse(run(executable, ["ui", "--cli-schema"]));
  const runOptions = optionNames(runSchema);
  const missingOptions = REQUIRED_RUN_OPTIONS.filter((option) => !runOptions.has(option));
  const uiCommands = new Set(Object.keys(uiSchema.subcommands || {}));
  const missingCommands = REQUIRED_UI_COMMANDS.filter((command) => !uiCommands.has(command));
  if (missingOptions.length || missingCommands.length) {
    throw new Error(
      `WinApp CLI ${version} is missing required capabilities: ` +
      [...missingOptions, ...missingCommands.map((command) => `ui ${command}`)].join(", ")
    );
  }

  cached = {
    executable,
    dotnetExecutable,
    version,
    schemaVersion: String(uiSchema.schemaVersion || "unknown"),
    uiCommands,
  };
  return cached;
}
