/**
 * Same WinUI analyzer for every app in the harness's scoring build, so Q4 ("no build
 * warnings") counts the same C# and WUI warnings whichever setup made the app.
 *
 * The `winapp new` template references Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer and
 * most hand-made projects don't, so without this only some apps were analyzed. The
 * package is a development dependency: `analyzers/dotnet/cs` holds the Roslyn analyzer
 * and `build`/`buildTransitive` add the project's XAML as AdditionalFiles. A direct
 * PackageReference brings in both after restore.
 *
 * The reference is added only for the harness build, through a temporary targets file
 * passed as `CustomAfterMicrosoftCommonTargets` and deleted afterwards. The agent's
 * project files are never edited. A project that already references the analyzer keeps
 * its own reference and version.
 */
import { existsSync, readFileSync, readdirSync, rmSync, writeFileSync } from "fs";
import { dirname, join, relative, resolve } from "path";

export const WINUI_ANALYZER_PACKAGE = "Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer";
/** One version for every app the harness adds it to. Recorded in results.json. */
export const WINUI_ANALYZER_VERSION = "0.7.1";

export interface WinUIAnalyzerPlan {
  /** True when the harness adds the reference for its build. */
  inject: boolean;
  /** "harness" when the harness adds it, "project" when the project already references it. */
  source: "harness" | "project";
  /** Version applied; null when the project's own version could not be read. */
  version: string | null;
}

const MSBUILD_FILES = new Set(["directory.build.props", "directory.build.targets", "directory.packages.props"]);
const escapeRx = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const REF_RX = new RegExp(`<PackageReference\\b[^>]*\\b(?:Include|Update)\\s*=\\s*"\\s*${escapeRx(WINUI_ANALYZER_PACKAGE)}\\s*"([^>]*)>`, "i");
const VERSION_RX = new RegExp(`<PackageVersion\\b[^>]*\\bInclude\\s*=\\s*"\\s*${escapeRx(WINUI_ANALYZER_PACKAGE)}\\s*"[^>]*\\bVersion\\s*=\\s*"([^"]+)"`, "i");

/**
 * Decides whether the harness adds the analyzer. `files` are the project file and the
 * Directory.Build.props/.targets and Directory.Packages.props files that apply to it.
 */
export function planWinUIAnalyzer(files: Array<{ path: string; content: string }>): WinUIAnalyzerPlan {
  for (const f of files) {
    const m = f.content.match(REF_RX);
    if (!m) continue;
    const version = m[1].match(/\b(?:Version|VersionOverride)\s*=\s*"([^"]+)"/i)?.[1]
      ?? files.map((x) => x.content.match(VERSION_RX)?.[1]).find((v) => v)
      ?? null;
    return { inject: false, source: "project", version };
  }
  return { inject: true, source: "harness", version: WINUI_ANALYZER_VERSION };
}

/** The project file plus the MSBuild files MSBuild would import for it from its folder up to `rootDir`. */
export function projectBuildFiles(csproj: string, rootDir: string): Array<{ path: string; content: string }> {
  const read = (p: string) => { try { return readFileSync(p, "utf-8"); } catch { return ""; } };
  const out = [{ path: csproj, content: read(csproj) }];
  const root = resolve(rootDir);
  for (let dir = resolve(dirname(csproj)); ; dir = dirname(dir)) {
    let names: string[] = [];
    try { names = readdirSync(dir); } catch { /* unreadable */ }
    for (const n of names) if (MSBUILD_FILES.has(n.toLowerCase())) out.push({ path: join(dir, n), content: read(join(dir, n)) });
    if (dir === root || relative(root, dir).startsWith("..") || dirname(dir) === dir) break;
  }
  return out;
}

/** Targets file that adds the analyzer unless a PackageReference to it already exists. */
export function analyzerTargets(version = WINUI_ANALYZER_VERSION): string {
  const id = WINUI_ANALYZER_PACKAGE;
  const notReferenced = `'@(PackageReference->AnyHaveMetadataValue('Identity', '${id}'))' != 'true'`;
  return `<Project>
  <!-- Added by the apps benchmark for its scoring build only; never part of the app's project. -->
  <ItemGroup Condition="${notReferenced}">
    <PackageReference Include="${id}" Version="${version}" PrivateAssets="all" Condition="'$(ManagePackageVersionsCentrally)' != 'true'" />
    <PackageReference Include="${id}" VersionOverride="${version}" PrivateAssets="all" Condition="'$(ManagePackageVersionsCentrally)' == 'true'" />
  </ItemGroup>
</Project>
`;
}

/**
 * Writes the temporary targets file into `dir` when the plan injects, and returns the extra
 * build argument plus a cleanup that deletes the file. `dir` must be outside the app folder.
 */
export function prepareWinUIAnalyzer(plan: WinUIAnalyzerPlan, dir: string): { args: string[]; cleanup: () => void } {
  if (!plan.inject) return { args: [], cleanup: () => {} };
  const path = join(dir, "benchmark-winui-analyzer.targets");
  writeFileSync(path, analyzerTargets(plan.version ?? WINUI_ANALYZER_VERSION));
  return {
    args: [`-p:CustomAfterMicrosoftCommonTargets=${path}`],
    cleanup: () => { if (existsSync(path)) rmSync(path, { force: true }); },
  };
}

/** What results.json records as `winui_analyzer`. `applied` is false when the build had to run without it. */
export interface WinUIAnalyzerRecord {
  package: string;
  version: string | null;
  source: "harness" | "project";
  applied: boolean;
}

/** True when a failed build failed because of the added analyzer (restore or an analyzer error), not the app. */
export function failedBecauseOfAnalyzer(output: string): boolean {
  return output.toLowerCase().includes(WINUI_ANALYZER_PACKAGE.toLowerCase()) || /\berror\s+WUI\d+/i.test(output);
}
