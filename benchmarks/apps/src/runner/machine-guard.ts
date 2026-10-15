/**
 * Keep benchmark runs from leaving persistent changes on the machine.
 *
 * Incident (2026-10-15): every trial gets a fresh DOTNET_CLI_HOME, and the
 * .NET SDK's first-run setup permanently appends `<DOTNET_CLI_HOME>\.dotnet\tools`
 * to the user PATH in the registry. After ~330 trials HKCU\Environment\Path
 * passed Windows' 32,767-character limit and sign-in broke (no shell, no
 * packaged apps). The trial env now sets DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false;
 * this module is the backstop and the tripwire:
 *
 * - remove any `win-dev-bench\trial-env` entries from the user PATH (raw,
 *   unexpanded value; same registry type; WM_SETTINGCHANGE broadcast),
 * - refuse to start a run when the PATH is close to the limit,
 * - unregister development-mode packages a trial registered,
 * - report other changes to HKCU\Environment made during a trial.
 *
 * All registry/AppX operations are Windows-only no-ops elsewhere. Pure helpers
 * are exported for unit tests.
 */
import { spawnSync } from "child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "fs";
import { join } from "path";

const isWindows = process.platform === "win32";

/** Abort a run above these (Windows hard limit per variable: 32,767). */
export const USER_PATH_MAX = 4000;
export const COMBINED_PATH_MAX = 16000;

const TRIAL_ENV_RE = /[\\/]win-dev-bench[\\/]trial-env[\\/]/i;

function expandEnvVars(s: string, env: NodeJS.ProcessEnv = process.env): string {
  return s.replace(/%([^%]+)%/g, (m, name: string) => {
    const key = Object.keys(env).find((k) => k.toLowerCase() === name.toLowerCase());
    return key ? String(env[key]) : m;
  });
}

/** A PATH entry that points into a per-trial scratch folder (never legitimate after a trial). */
export function isTrialEnvPathEntry(entry: string, env: NodeJS.ProcessEnv = process.env): boolean {
  return TRIAL_ENV_RE.test(expandEnvVars(entry.trim(), env));
}

/** Remove matching entries from a raw `;`-separated PATH value, leaving every other entry untouched. */
export function prunePathValue(raw: string, remove: (entry: string) => boolean): { value: string; removed: string[] } {
  const parts = raw.split(";");
  const removed = parts.filter((p) => p.trim() && remove(p));
  if (!removed.length) return { value: raw, removed };
  return { value: parts.filter((p) => !(p.trim() && remove(p))).join(";"), removed };
}

/** Names whose values differ between two HKCU\Environment snapshots (Path excluded). */
export function diffEnvValues(before: Record<string, string>, after: Record<string, string>): string[] {
  const names = new Set([...Object.keys(before), ...Object.keys(after)].map((n) => n.toLowerCase()));
  names.delete("path");
  const get = (o: Record<string, string>, n: string) => {
    const k = Object.keys(o).find((x) => x.toLowerCase() === n);
    return k === undefined ? undefined : o[k];
  };
  return [...names].filter((n) => get(before, n) !== get(after, n)).sort();
}

/** `<Identity Name="...">` from manifest text (attributes may span lines). */
export function manifestIdentityName(xml: string): string | null {
  return xml.match(/<Identity\b[^>]*?\bName\s*=\s*"([^"]+)"/s)?.[1] ?? null;
}

/** Package identity names declared by any app manifest under `dir` (incl. AppX layouts in bin/). */
export function manifestIdentities(dir: string, maxDepth = 8): string[] {
  const out = new Set<string>();
  const walk = (d: string, depth: number) => {
    let entries;
    try { entries = readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      const full = join(d, e.name);
      if (e.isDirectory()) {
        if (depth < maxDepth && e.name !== "node_modules" && e.name !== ".git") walk(full, depth + 1);
      } else if (/\.appxmanifest$|^appxmanifest\.xml$/i.test(e.name)) {
        try {
          if (statSync(full).size > 1_000_000) continue;
          const name = manifestIdentityName(readFileSync(full, "utf-8"));
          if (name) out.add(name);
        } catch { /* unreadable */ }
      }
    }
  };
  if (existsSync(dir)) walk(dir, 0);
  return [...out];
}

function ps(script: string, env: Record<string, string> = {}, timeout = 60_000): { ok: boolean; stdout: string; stderr: string } {
  const r = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
    encoding: "utf8",
    windowsHide: true,
    timeout,
    maxBuffer: 16 * 1024 * 1024,
    env: { ...process.env, ...env },
  });
  return { ok: r.status === 0, stdout: (r.stdout || "").trim(), stderr: (r.stderr || "").trim() };
}

export interface UserEnvSnapshot {
  /** Raw (unexpanded) values of HKCU\Environment. */
  values: Record<string, string>;
  /** Actual name of the PATH value ("Path" usually) and its registry kind. */
  pathName: string;
  pathKind: string;
}

const READ_ENV = `
$k=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
$o=[ordered]@{}; $pn='Path'; $kind='ExpandString'
foreach($n in $k.GetValueNames()){
  $o[$n]=[string]$k.GetValue($n,'',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  if($n -ieq 'Path'){ $pn=$n; $kind=[string]$k.GetValueKind($n) }
}
[pscustomobject]@{ values=$o; pathName=$pn; pathKind=$kind } | ConvertTo-Json -Compress -Depth 3`;

export function snapshotUserEnv(): UserEnvSnapshot | null {
  if (!isWindows) return null;
  const r = ps(READ_ENV);
  if (!r.ok) return null;
  try { return JSON.parse(r.stdout) as UserEnvSnapshot; } catch { return null; }
}

export function userPathRaw(s: UserEnvSnapshot | null): string {
  return s ? s.values[s.pathName] ?? "" : "";
}

// Compare-and-swap write: only replace PATH if it still equals what we read,
// so an append that happened in between is never lost.
const WRITE_PATH = `
$k=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment',$true)
$cur=[string]$k.GetValue($env:WDB_PATH_NAME,'',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
if($cur -cne $env:WDB_PATH_OLD){ 'changed'; exit 0 }
$k.SetValue($env:WDB_PATH_NAME,$env:WDB_PATH_NEW,[Microsoft.Win32.RegistryValueKind]$env:WDB_PATH_KIND)
$sig='[DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h,uint m,UIntPtr w,string l,uint f,uint t,out UIntPtr r);'
$u=Add-Type -MemberDefinition $sig -Name EnvBroadcast -Namespace WinDevBench -PassThru
$r=[UIntPtr]::Zero; [void]$u::SendMessageTimeout([IntPtr]0xffff,0x1A,[UIntPtr]::Zero,'Environment',2,5000,[ref]$r)
'written'`;

/**
 * Remove matching entries from the user PATH in the registry. Returns the
 * removed entries (empty when nothing matched or on non-Windows).
 */
export function removeUserPathEntries(remove: (entry: string) => boolean = isTrialEnvPathEntry): string[] {
  if (!isWindows) return [];
  for (let attempt = 0; attempt < 3; attempt++) {
    const snap = snapshotUserEnv();
    if (!snap) return [];
    const raw = userPathRaw(snap);
    const { value, removed } = prunePathValue(raw, remove);
    if (!removed.length) return [];
    const r = ps(WRITE_PATH, {
      WDB_PATH_NAME: snap.pathName,
      WDB_PATH_KIND: snap.pathKind === "String" ? "String" : "ExpandString",
      WDB_PATH_OLD: raw,
      WDB_PATH_NEW: value,
    });
    if (r.ok && r.stdout.includes("written")) return removed;
  }
  return [];
}

export function pathLengths(): { user: number; machine: number } {
  if (!isWindows) return { user: 0, machine: 0 };
  const r = ps(
    "[Environment]::GetEnvironmentVariable('PATH','User').Length; [Environment]::GetEnvironmentVariable('PATH','Machine').Length",
  );
  const [user, machine] = r.stdout.split(/\r?\n/).map((s) => Number(s.trim()) || 0);
  return { user: user ?? 0, machine: machine ?? 0 };
}

/**
 * Whether Windows long-path support is on. Trial builds and Copilot session
 * folders routinely go past 260 characters. Read-only: enabling it is a
 * machine-wide change the user must make.
 */
export function longPathsEnabled(): boolean {
  if (!isWindows) return true;
  const r = ps(
    "(Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem' -Name LongPathsEnabled -ErrorAction SilentlyContinue).LongPathsEnabled",
  );
  return r.stdout.trim() === "1";
}

export interface DevPackage { name: string; fullName: string; location: string }

export function devPackages(): DevPackage[] {
  if (!isWindows) return [];
  const r = ps(
    "@(Get-AppxPackage | Where-Object IsDevelopmentMode | ForEach-Object { [pscustomobject]@{ name=$_.Name; fullName=$_.PackageFullName; location=[string]$_.InstallLocation } }) | ConvertTo-Json -Compress",
    {},
    120_000,
  );
  if (!r.ok || !r.stdout) return [];
  try {
    const parsed = JSON.parse(r.stdout);
    return Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    return [];
  }
}

/**
 * Development-mode packages that belong to a trial: identity declared by the
 * trial's manifests, and registered from the trial folder, a harness staging
 * folder, or a folder that no longer exists (empty InstallLocation).
 */
export function trialPackages(all: DevPackage[], identities: string[], ownedRoots: string[]): DevPackage[] {
  const ids = new Set(identities.map((i) => i.toLowerCase()));
  const roots = ownedRoots.map((r) => r.toLowerCase().replace(/[\\/]+$/, ""));
  return all.filter((p) => {
    if (!ids.has(p.name.toLowerCase())) return false;
    const loc = (p.location || "").toLowerCase();
    return !loc || roots.some((r) => loc === r || loc.startsWith(r + "\\") || loc.startsWith(r + "/"));
  });
}

export function removePackages(pkgs: DevPackage[]): string[] {
  if (!isWindows || !pkgs.length) return [];
  const removed: string[] = [];
  for (const p of pkgs) {
    const r = ps(`Remove-AppxPackage -Package $env:WDB_PKG -ErrorAction Stop; 'ok'`, { WDB_PKG: p.fullName }, 180_000);
    if (r.ok && r.stdout.includes("ok")) removed.push(p.fullName);
  }
  return removed;
}
