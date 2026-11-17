/**
 * Fallback builder for the harness build gate. Some WinUI projects build with
 * Visual Studio's MSBuild but not with `dotnet build`, for example when the
 * PRI packaging tasks (MSB4062, Microsoft.Build.Packaging.Pri.Tasks.dll) ship
 * only with Visual Studio. The gate retries once with Visual Studio MSBuild.
 */
import { existsSync } from "fs";
import { join } from "path";
import { spawnSync } from "child_process";

export type RunSync = (exe: string, args: string[]) => { status: number | null; stdout: string };

const defaultRun: RunSync = (exe, args) => {
  const r = spawnSync(exe, args, { encoding: "utf8", windowsHide: true, timeout: 30_000 });
  return { status: r.status, stdout: r.stdout || "" };
};

export function vswherePath(env: NodeJS.ProcessEnv = process.env): string {
  const root = env["ProgramFiles(x86)"] || env.ProgramFiles || "C:\\Program Files (x86)";
  return join(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
}

/** Path to the newest Visual Studio MSBuild.exe, or null when vswhere or MSBuild is not installed. */
export function findVsMsBuild(
  run: RunSync = defaultRun,
  exists: (p: string) => boolean = existsSync,
  env: NodeJS.ProcessEnv = process.env,
): string | null {
  const vswhere = vswherePath(env);
  if (!exists(vswhere)) return null;
  let r: ReturnType<RunSync>;
  try {
    r = run(vswhere, ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", "MSBuild\\**\\Bin\\MSBuild.exe"]);
  } catch {
    return null;
  }
  if (r.status !== 0) return null;
  // The first match is the 32-bit Bin\MSBuild.exe; Bin\amd64 and Bin\arm64 follow it.
  return r.stdout.split(/\r?\n/).map((l) => l.trim()).find((l) => l && exists(l)) ?? null;
}

export function msbuildRetryArgs(csproj: string, platform: string): string[] {
  // RuntimeIdentifier is explicit because 32-bit MSBuild would otherwise default `winapp init` projects to win-x86.
  return [csproj, "/restore", "/p:Configuration=Debug", `/p:Platform=${platform}`, `/p:RuntimeIdentifier=win-${platform}`];
}
