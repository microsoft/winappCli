/**
 * Per-trial environment isolation helpers (reports/plans/11, A6).
 *
 * - `hideToolsFromPath`: remove tools such as `winapp` from a trial's PATH
 *   (for the `bare` baseline) without losing the other executables that live
 *   in the same directories (e.g. `winget` next to `winapp` in WindowsApps).
 * - `findAncestorProjectFiles`: detect MSBuild project files in parent
 *   directories. `dotnet new` evaluates template constraints by walking up to
 *   the nearest project, so a stray project in the user profile makes every
 *   template operation glob the whole profile (minutes per trial).
 */
import { mkdirSync, readdirSync, statSync, writeFileSync } from "fs";
import { basename, dirname, extname, join, resolve } from "path";

const TOOL_EXTS = ["", ".exe", ".cmd", ".bat", ".ps1"];
const SHIM_EXTS = new Set([".exe", ".cmd", ".bat"]);

export interface HiddenPath {
  path: string;
  removedDirs: string[];
  shims: string[];
}

/**
 * Drop every PATH entry that contains one of `tools`. Each dropped entry is
 * replaced, at the same position, by a folder under `shimDir` holding `.cmd`
 * shims for its other executables, so PATH precedence for everything else is
 * unchanged.
 */
export function hideToolsFromPath(pathValue: string, tools: string[], shimDir: string, sep = ";"): HiddenPath {
  const hidden = new Set(tools.map((t) => t.toLowerCase()));
  const out: string[] = [];
  const removedDirs: string[] = [];
  const shims: string[] = [];
  for (const dir of pathValue.split(sep).filter(Boolean)) {
    // Compare directory listings instead of existsSync: App Execution Aliases
    // (WindowsApps\winapp.exe) are reparse points that stat() can't follow.
    let names: string[];
    try { names = readdirSync(dir); } catch { out.push(dir); continue; }
    const lower = new Set(names.map((n) => n.toLowerCase()));
    const hasTool = tools.some((t) => TOOL_EXTS.some((ext) => lower.has((t + ext).toLowerCase())));
    if (!hasTool) { out.push(dir); continue; }

    const slot = join(shimDir, String(removedDirs.length));
    removedDirs.push(dir);
    const written = new Set<string>();
    for (const name of names) {
      const ext = extname(name).toLowerCase();
      const stem = basename(name, extname(name));
      if (!SHIM_EXTS.has(ext) || hidden.has(stem.toLowerCase()) || written.has(stem.toLowerCase())) continue;
      if (written.size === 0) mkdirSync(slot, { recursive: true });
      const shim = join(slot, `${stem}.cmd`);
      writeFileSync(shim, `@"${join(dir, name)}" %*\r\n`);
      written.add(stem.toLowerCase());
      shims.push(shim);
    }
    if (written.size > 0) out.push(slot);
  }
  return { path: out.join(sep), removedDirs, shims };
}

const PROJECT_FILE = /\.(csproj|vbproj|fsproj|vcxproj|sln|slnx)$/i;
const PROJECT_PROPS = new Set(["directory.build.props", "directory.build.targets", "directory.packages.props"]);

/** Project/solution/Directory.Build files in any *parent* of `startDir` (not `startDir` itself). */
export function findAncestorProjectFiles(startDir: string): string[] {
  const found: string[] = [];
  let dir = dirname(resolve(startDir));
  for (;;) {
    let names: string[] = [];
    try { names = readdirSync(dir); } catch { names = []; }
    for (const name of names) {
      if (!PROJECT_FILE.test(name) && !PROJECT_PROPS.has(name.toLowerCase())) continue;
      try { if (statSync(join(dir, name)).isFile()) found.push(join(dir, name)); } catch { /* ignore */ }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return found;
}
