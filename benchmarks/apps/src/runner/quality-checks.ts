/**
 * Automatic default quality checks (benchmark rules v2, reports/plans/11).
 *
 * Pure evaluators over project files, build output, and `winapp ui` JSON so
 * they can be unit-tested; the runner in benchmark.ts gathers the inputs.
 */
import { existsSync, readdirSync, readFileSync, statSync } from "fs";
import { basename, dirname, extname, join } from "path";

export type CheckStatus = "pass" | "fail" | "na";

export interface QualityCheck {
  id: string;
  label: string;
  /** "gate" checks zero the trial score on failure; "quality" checks feed the 30-point quality share. */
  kind: "gate" | "quality";
  /** Who decided: harness (deterministic) or judge (LLM). */
  source: "harness" | "judge";
  status: CheckStatus;
  detail?: string;
}

export interface ProjectFile {
  path: string;
  content: string;
}

const SKIP_DIRS = new Set(["bin", "obj", ".git", ".vs", "node_modules", "packages", "AppPackages"]);
const SOURCE_EXTS = new Set([".cs", ".xaml", ".csproj", ".props", ".targets", ".appxmanifest", ".xml"]);

/** Read project source files under `projectDir` (skips bin/obj and other generated folders). */
export function collectProjectFiles(projectDir: string, maxFiles = 2000): ProjectFile[] {
  const out: ProjectFile[] = [];
  const walk = (dir: string) => {
    if (out.length >= maxFiles) return;
    let entries: string[];
    try { entries = readdirSync(dir); } catch { return; }
    for (const name of entries) {
      const full = join(dir, name);
      let st;
      try { st = statSync(full); } catch { continue; }
      if (st.isDirectory()) {
        if (!SKIP_DIRS.has(name) && !name.startsWith(".")) walk(full);
      } else if (SOURCE_EXTS.has(extname(name).toLowerCase()) && st.size < 2_000_000) {
        try { out.push({ path: full, content: readFileSync(full, "utf-8") }); } catch { /* unreadable */ }
      }
      if (out.length >= maxFiles) return;
    }
  };
  if (existsSync(projectDir)) walk(projectDir);
  return out;
}

const isXaml = (f: ProjectFile) => f.path.toLowerCase().endsWith(".xaml");
const isCs = (f: ProjectFile) => f.path.toLowerCase().endsWith(".cs");
const isProjectFile = (f: ProjectFile) => /\.(csproj|props|targets)$/i.test(f.path);

/** XAML files that are resource definitions (App.xaml, ResourceDictionary roots) are exempt from style checks. */
function isResourceXaml(f: ProjectFile): boolean {
  if (basename(f.path).toLowerCase() === "app.xaml") return true;
  const root = f.content.replace(/<\?xml[\s\S]*?\?>/, "").replace(/<!--[\s\S]*?-->/g, "").match(/<\s*([\w:.]+)/);
  return !!root && /ResourceDictionary$/.test(root[1]);
}

function count(re: RegExp, text: string): number {
  return (text.match(new RegExp(re.source, re.flags.includes("g") ? re.flags : re.flags + "g")) || []).length;
}

function check(id: string, label: string, kind: QualityCheck["kind"], status: CheckStatus, detail?: string): QualityCheck {
  return { id, label, kind, source: "harness", status, ...(detail ? { detail } : {}) };
}

/**
 * Source-level checks. `framework` is the scenario's expected UI framework;
 * only "winui" checks are defined today (other frameworks get "na").
 */
export function evaluateSourceChecks(files: ProjectFile[], framework = "winui"): QualityCheck[] {
  if (framework.toLowerCase() !== "winui") {
    return [check("G0", "Right framework", "gate", "na", `framework '${framework}' has no automatic checks yet`)];
  }
  const proj = files.filter(isProjectFile).map((f) => f.content).join("\n");
  const cs = files.filter(isCs);
  const xaml = files.filter(isXaml);
  const pageXaml = xaml.filter((f) => !isResourceXaml(f));
  const allCode = [...cs, ...xaml].map((f) => f.content).join("\n");
  const checks: QualityCheck[] = [];

  const winui = /<UseWinUI>\s*true\s*<\/UseWinUI>/i.test(proj);
  checks.push(check("G0", "Right framework (WinUI 3 project)", "gate", winui ? "pass" : "fail",
    winui ? undefined : "No <UseWinUI>true</UseWinUI> in the project"));

  const manifest = files.some((f) => /(^|[\\/])(package\.appxmanifest|appxmanifest\.xml)$/i.test(f.path) || /\.appxmanifest$/i.test(f.path));
  checks.push(check("P2", "App manifest present", "quality", manifest ? "pass" : "fail",
    manifest ? undefined : "No Package.appxmanifest found"));

  const unpackaged = /<WindowsPackageType>\s*None\s*<\/WindowsPackageType>/i.test(proj);
  checks.push(check("P3", "Packaged app", "quality", unpackaged ? "fail" : "pass",
    unpackaged ? "WindowsPackageType is None" : undefined));

  const hygiene: string[] = [];
  if (/<Use(WPF|WindowsForms)>\s*true/i.test(proj)) hygiene.push("UseWPF/UseWindowsForms enabled");
  if (/(PackageReference|Reference)\s+Include="(PresentationFramework|PresentationCore|WindowsBase|Wpf\.Ui|WPF-UI|System\.Windows\.Forms|System\.Drawing\.Common)\b/i.test(proj)) {
    hygiene.push("WPF/WinForms/System.Drawing reference");
  }
  const legacyUsings = cs.filter((f) => /^\s*using\s+(System\.Windows\.(Controls|Media|Forms|Shapes|Markup|Data|Documents|Navigation)|Windows\.UI\.Xaml)\b/m.test(f.content));
  if (legacyUsings.length) hygiene.push(`WPF/WinForms/UWP namespaces in ${legacyUsings.map((f) => basename(f.path)).slice(0, 3).join(", ")}`);
  checks.push(check("P6", "Project hygiene (no WPF/WinForms/UWP leftovers)", "quality", hygiene.length ? "fail" : "pass",
    hygiene.join("; ") || undefined));

  const titleBar = /ExtendsContentIntoTitleBar\s*=\s*true/.test(allCode) || /<(\w+:)?TitleBar\b/.test(allCode);
  checks.push(check("V1", "Custom title bar", "quality", titleBar ? "pass" : "fail",
    titleBar ? undefined : "Window keeps the default Win32 title bar"));

  const backdrop = /\b(MicaBackdrop|DesktopAcrylicBackdrop|MicaController|DesktopAcrylicController)\b/.test(allCode);
  checks.push(check("V2", "Mica/Acrylic backdrop", "quality", backdrop ? "pass" : "fail",
    backdrop ? undefined : "No system backdrop set"));

  // Icon sizing (FontIcon FontSize="16") is the normal way to size icons, not a typography choice.
  const fontSizes = pageXaml.reduce((n, f) =>
    n + (f.content.match(/<([\w:]+)\b[^<>]*?\bFontSize="\d/g) || [])
      .filter((tag) => !/^<(\w+:)?(FontIcon|SymbolIcon|BitmapIcon|PathIcon|AnimatedIcon|FontIconSource|SymbolIconSource)\b/.test(tag)).length, 0);
  checks.push(check("V3", "Typography via styles (≤2 hard-coded FontSize in pages)", "quality", fontSizes <= 2 ? "pass" : "fail",
    `${fontSizes} hard-coded FontSize attribute(s) in page XAML`));

  const hexColors = pageXaml.reduce((n, f) => n + count(/\b(Background|Foreground|Fill|Stroke|BorderBrush|Color)="#[0-9A-Fa-f]{3,8}"/, f.content), 0);
  checks.push(check("V5", "Colors via theme resources (no hard-coded hex colors in pages)", "quality", hexColors === 0 ? "pass" : "fail",
    hexColors ? `${hexColors} hard-coded hex color(s) in page XAML` : undefined));

  const legacyUi: string[] = [];
  if (/\bMessageDialog\b/.test(allCode)) legacyUi.push("Windows.UI.Popups.MessageDialog");
  if (/System\.Windows\.Forms/.test(allCode)) legacyUi.push("System.Windows.Forms");
  if (/Microsoft\.VisualBasic\.Interaction/.test(allCode)) legacyUi.push("Microsoft.VisualBasic.Interaction");
  if (/\bMessageBox[AW]?\s*\(/.test(cs.map((f) => f.content).join("\n"))) legacyUi.push("Win32 MessageBox");
  checks.push(check("V9", "No legacy Win32/WinForms UI", "quality", legacyUi.length ? "fail" : "pass",
    legacyUi.join(", ") || undefined));

  const xBind = xaml.reduce((n, f) => n + count(/\{x:Bind\b/, f.content), 0);
  const binding = xaml.reduce((n, f) => n + count(/\{Binding\b/, f.content), 0);
  const total = xBind + binding;
  checks.push(check("Q3", "x:Bind over {Binding} (≤20% {Binding})", "quality",
    total === 0 ? "na" : binding <= total * 0.2 ? "pass" : "fail",
    total ? `${xBind} x:Bind, ${binding} {Binding}` : "No bindings"));

  return checks;
}

/** Warnings from the harness build output, deduplicated, grouped by code prefix. */
export function evaluateBuildWarnings(buildOutput: string): QualityCheck {
  const seen = new Set<string>();
  const byCategory: Record<string, number> = {};
  for (const line of buildOutput.split(/\r?\n/)) {
    const m = line.match(/^(.*?):\s*(?:\w+\s+)?warning\s+((CS|WMC|WUI|IL|CA|IDE)\d+)\b/i);
    if (!m) continue;
    const key = `${m[1].trim()}|${m[2].toUpperCase()}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const cat = m[3].toUpperCase();
    byCategory[cat] = (byCategory[cat] || 0) + 1;
  }
  const totalWarnings = Object.values(byCategory).reduce((a, b) => a + b, 0);
  const detail = totalWarnings
    ? Object.entries(byCategory).map(([k, v]) => `${k}: ${v}`).join(", ")
    : "No C#/XAML/trim/analyzer warnings";
  return check("Q4", "No build warnings (C#, XAML, WinUI analyzer, trim/AOT, analyzers)", "quality", totalWarnings === 0 ? "pass" : "fail", detail);
}

/**
 * Gate G3 (Native AOT artifact) from the harness's own `winapp run --aot`.
 * Fails when the publish/launch failed, the publish reported trim/AOT
 * warnings (IL2xxx/IL3xxx), or the project suppresses them project-wide.
 * `UnconditionalSuppressMessage` attributes are reported but allowed: they
 * are the documented way to justify a single reviewed call site.
 */
export function evaluateNativeAot(
  publishOutput: string,
  files: ProjectFile[],
  launch: { ok: boolean; reason?: string },
): QualityCheck {
  const warnings = new Set<string>();
  for (const line of publishOutput.split(/\r?\n/)) {
    const m = line.match(/^(.*?):\s*(?:\w+\s+)?warning\s+(IL[23]\d{3})\b/i);
    if (m) warnings.add(`${m[1].trim()}|${m[2].toUpperCase()}`);
  }
  const codes = [...new Set([...warnings].map((w) => w.split("|")[1]))].sort();

  const proj = files.filter(isProjectFile);
  const suppressions: string[] = [];
  for (const f of proj) {
    for (const m of f.content.matchAll(/<NoWarn>([^<]*)<\/NoWarn>/gi)) {
      const il = m[1].split(/[;,\s]+/).filter((c) => /^IL[23]\d{3}$/i.test(c.trim()));
      if (il.length) suppressions.push(`NoWarn ${il.join(",")} in ${basename(f.path)}`);
    }
    if (/<SuppressTrimAnalysisWarnings>\s*true/i.test(f.content)) suppressions.push(`SuppressTrimAnalysisWarnings in ${basename(f.path)}`);
    if (/<(EnableTrimAnalyzer|EnableAotAnalyzer)>\s*false/i.test(f.content)) suppressions.push(`trim/AOT analyzer disabled in ${basename(f.path)}`);
  }
  const attributeSuppressions = files.filter(isCs)
    .reduce((n, f) => n + count(/UnconditionalSuppressMessage\s*\(\s*"[^"]*"\s*,\s*"IL[23]\d{3}/, f.content), 0);

  const problems: string[] = [];
  if (!launch.ok) problems.push(launch.reason || "Native AOT publish/launch failed");
  if (warnings.size) problems.push(`${warnings.size} trim/AOT warning(s): ${codes.join(", ")}`);
  if (suppressions.length) problems.push(`suppressed: ${suppressions.join("; ")}`);
  const note = attributeSuppressions ? ` (${attributeSuppressions} UnconditionalSuppressMessage attribute(s) for IL2/IL3)` : "";
  return check("G3", "Native AOT Release publish launches without trim/AOT warnings", "gate",
    problems.length ? "fail" : "pass",
    (problems.length ? problems.join("; ") : "Published with Native AOT, window shown, no IL2/IL3 warnings") + note);
}

export interface UiElement {
  type?: string;
  name?: string;
  automationId?: string;
  selector?: string;
  isEnabled?: boolean;
  isOffscreen?: boolean;
  isInvokable?: boolean;
  children?: UiElement[];
}

const INTERACTIVE_TYPES = new Set([
  "Button", "SplitButton", "Edit", "ComboBox", "CheckBox", "RadioButton", "Slider", "Spinner",
  "Hyperlink", "TabItem", "MenuItem", "ListItem", "TreeItem", "DataItem",
]);

/** Flatten a `winapp ui inspect --interactive --json` envelope into visible interactive elements. */
export function interactiveElements(inspect: any): UiElement[] {
  const out: UiElement[] = [];
  const visit = (e: UiElement) => {
    if (!e || typeof e !== "object") return;
    if (!e.isOffscreen && (e.isInvokable || INTERACTIVE_TYPES.has(String(e.type)))) out.push(e);
    for (const c of e.children || []) visit(c);
  };
  for (const w of inspect?.windows || []) for (const e of w?.elements || []) visit(e);
  return out;
}

/**
 * Default ToString() leaking into the accessible name: a namespace-qualified
 * type ("MyApp.Models.Photo") or a C# record ("HistoryEntry { Expression = … }").
 */
const TYPE_NAME_LEAK = /^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*){1,}$|^[A-Z][\w]*\s*\{\s*\w+\s*=/;

export function evaluateAccessibleNames(elements: UiElement[]): QualityCheck {
  if (elements.length === 0) return check("Q0", "Accessible names on interactive controls", "quality", "na", "No interactive elements found");
  const bad = elements.filter((e) => !e.name?.trim() || TYPE_NAME_LEAK.test(e.name.trim()));
  const sample = bad.slice(0, 8).map((e) => `${e.type}${e.automationId ? `#${e.automationId}` : ""}${e.name ? ` '${e.name}'` : ""}`);
  return check("Q0", "Accessible names on interactive controls", "quality", bad.length === 0 ? "pass" : "fail",
    bad.length ? `${bad.length}/${elements.length} unnamed or type-name labels: ${sample.join(", ")}` : `${elements.length} interactive elements named`);
}

/** `focusSamples` are `get-focused --json` results after successive Tab presses. */
export function evaluateKeyboardTraversal(focusSamples: any[], interactiveCount: number): QualityCheck {
  if (interactiveCount === 0) return check("Q1", "Keyboard navigation (Tab moves focus through controls)", "quality", "na", "No interactive elements");
  const distinct = new Set(
    focusSamples
      .filter((s) => s?.hasFocus && s.element)
      .map((s) => s.element.selector || `${s.element.type}|${s.element.name}`),
  );
  const needed = Math.min(3, interactiveCount);
  return check("Q1", "Keyboard navigation (Tab moves focus through controls)", "quality", distinct.size >= needed ? "pass" : "fail",
    `Tab reached ${distinct.size} distinct control(s); needed ${needed}`);
}

/** Merge judge-reported quality items (e.g. Q5 states) into the check list. */
export function judgeChecks(quality: Record<string, { status?: string; reason?: string }> | undefined): QualityCheck[] {
  const out: QualityCheck[] = [];
  const states = quality?.states;
  if (states) {
    const status = (["pass", "fail", "na"] as const).find((s) => s === String(states.status).toLowerCase()) ?? "na";
    out.push({ id: "Q5", label: "Usable empty, loading, and error states", kind: "quality", source: "judge", status, ...(states.reason ? { detail: states.reason } : {}) });
  }
  return out;
}

/** Directory to scan for a given .csproj (its own folder). */
export function projectDirFor(csproj: string): string {
  return dirname(csproj);
}
