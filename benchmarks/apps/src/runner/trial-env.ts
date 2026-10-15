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
 * Drop every PATH entry that contains one of `tools`, and put `shimDir` first
 * with `.cmd` shims for every other executable those entries provided.
 */
export function hideToolsFromPath(pathValue: string, tools: string[], shimDir: string, sep = ";"): HiddenPath {
  const hidden = new Set(tools.map((t) => t.toLowerCase()));
  const keep: string[] = [];
  const removedDirs: string[] = [];
  for (const dir of pathValue.split(sep).filter(Boolean)) {
    // Compare directory listings instead of existsSync: App Execution Aliases
    // (WindowsApps\winapp.exe) are reparse points that stat() can't follow.
    let names: Set<string>;
    try { names = new Set(readdirSync(dir).map((n) => n.toLowerCase())); } catch { keep.push(dir); continue; }
    const hasTool = tools.some((t) => TOOL_EXTS.some((ext) => names.has((t + ext).toLowerCase())));
    (hasTool ? removedDirs : keep).push(dir);
  }
  const shims: string[] = [];
  if (removedDirs.length) {
    mkdirSync(shimDir, { recursive: true });
    for (const dir of removedDirs) {
      let names: string[] = [];
      try { names = readdirSync(dir); } catch { continue; }
      for (const name of names) {
        const ext = extname(name).toLowerCase();
        const stem = basename(name, extname(name));
        if (!SHIM_EXTS.has(ext) || hidden.has(stem.toLowerCase())) continue;
        const shim = join(shimDir, `${stem}.cmd`);
        if (shims.includes(shim)) continue; // first PATH entry wins, like the shell
        writeFileSync(shim, `@"${join(dir, name)}" %*\r\n`);
        shims.push(shim);
      }
    }
  }
  return { path: [shimDir, ...keep].join(sep), removedDirs, shims };
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
