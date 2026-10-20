import assert from "node:assert/strict";
import test from "node:test";
import {
  evaluateAccessibleNames,
  evaluateBuildWarnings,
  evaluateKeyboardTraversal,
  evaluateNativeAot,
  evaluateSourceChecks,
  interactiveElements,
  judgeChecks,
  type ProjectFile,
} from "./quality-checks.js";
import { computeScoreV2 } from "./scoring.js";

const templateApp: ProjectFile[] = [
  { path: "C:/app/App.csproj", content: "<Project><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>" },
  { path: "C:/app/Package.appxmanifest", content: "<Package/>" },
  { path: "C:/app/App.xaml", content: '<Application><Application.Resources><SolidColorBrush x:Key="Brand" Color="#FF0078D4"/></Application.Resources></Application>' },
  { path: "C:/app/MainWindow.xaml", content: '<Window><Window.SystemBackdrop><MicaBackdrop/></Window.SystemBackdrop><Grid><TitleBar/><TextBlock Text="{x:Bind ViewModel.Title}" Style="{StaticResource TitleTextBlockStyle}"/></Grid></Window>' },
  { path: "C:/app/MainWindow.xaml.cs", content: "using Microsoft.UI.Xaml;\nusing System.Windows.Input;\nclass MainWindow { MainWindow() { ExtendsContentIntoTitleBar = true; } }" },
];
const byId = (checks: { id: string; status: string }[]) => Object.fromEntries(checks.map((c) => [c.id, c.status]));

test("a template-style WinUI app passes every source check", () => {
  const s = byId(evaluateSourceChecks(templateApp));
  assert.deepEqual(s, { G0: "pass", P2: "pass", P3: "pass", P6: "pass", V1: "pass", V2: "pass", V3: "pass", V5: "pass", V9: "pass", Q3: "pass" });
});

test("System.Windows.Input (ICommand) is not flagged as WPF, but System.Windows.Controls is", () => {
  const wpf = [...templateApp, { path: "C:/app/Old.cs", content: "using System.Windows.Controls;" }];
  assert.equal(byId(evaluateSourceChecks(wpf)).P6, "fail");
});

test("a non-WinUI, unpackaged, hand-rolled app fails the gate and style checks", () => {
  const files: ProjectFile[] = [
    { path: "C:/app/App.csproj", content: "<Project><PropertyGroup><UseWPF>true</UseWPF><WindowsPackageType>None</WindowsPackageType></PropertyGroup></Project>" },
    { path: "C:/app/MainPage.xaml", content: '<Page><Grid Background="#FFFFFF"><TextBlock FontSize="40" Text="{Binding A}"/><TextBlock FontSize="12"/><TextBlock FontSize="14"/><FontIcon Glyph="x" FontSize="16"/></Grid></Page>' },
    { path: "C:/app/MainPage.xaml.cs", content: "var d = new Windows.UI.Popups.MessageDialog(\"hi\");" },
  ];
  const s = byId(evaluateSourceChecks(files));
  assert.equal(s.G0, "fail");
  assert.equal(s.P2, "fail");
  assert.equal(s.P3, "fail");
  assert.equal(s.P6, "fail");
  assert.equal(s.V1, "fail");
  assert.equal(s.V2, "fail");
  assert.equal(s.V3, "fail");
  assert.equal(s.V5, "fail");
  assert.equal(s.V9, "fail");
  assert.equal(s.Q3, "fail");
});

test("icon FontSize does not count against typography", () => {
  const icons = [...templateApp, { path: "C:/app/Nav.xaml", content: '<Page><FontIcon Glyph="a" FontSize="16"/><SymbolIcon FontSize="20"/><FontIcon\n  Glyph="b"\n  FontSize="12"/></Page>' }];
  const v3 = evaluateSourceChecks(icons).find((c) => c.id === "V3")!;
  assert.equal(v3.status, "pass");
  assert.match(v3.detail!, /^0 /);
});

test("hard-coded colors in resource dictionaries are allowed", () => {
  const rd = [...templateApp, { path: "C:/app/Themes/Colors.xaml", content: '<ResourceDictionary><SolidColorBrush x:Key="A" Color="#123456"/></ResourceDictionary>' }];
  assert.equal(byId(evaluateSourceChecks(rd)).V5, "pass");
});

test("build warnings are deduplicated and categorized", () => {
  const out = [
    "C:\\app\\A.cs(3,5): warning CS8618: Non-nullable field [C:\\app\\App.csproj]",
    "C:\\app\\A.cs(3,5): warning CS8618: Non-nullable field [C:\\app\\App.csproj]",
    "C:\\app\\B.xaml(10,3): XamlCompiler warning WMC1506: something",
    "C:\\app\\MainPage.xaml(127,18): warning WUI2011: x:Bind defaults to OneTime [C:\\app\\App.csproj]",
    "  warning NU1701: Package restored using .NETFramework",
  ].join("\n");
  const c = evaluateBuildWarnings(out);
  assert.equal(c.status, "fail");
  assert.match(c.detail!, /CS: 1/);
  assert.match(c.detail!, /WMC: 1/);
  assert.match(c.detail!, /WUI: 1/);
  assert.doesNotMatch(c.detail!, /NU/);
  assert.equal(evaluateBuildWarnings("Build succeeded.\n    0 Warning(s)").status, "pass");
});

test("accessible names: unnamed controls and ToString type-name leaks fail", () => {
  const inspect = { windows: [{ elements: [
    { type: "Button", name: "Save", isInvokable: true },
    { type: "Pane", children: [
      { type: "ListItem", name: "PhotoApp.Models.Photo", isInvokable: true },
      { type: "ListItem", name: "HistoryEntry { Expression = 5 × 4 =, Result = 20 }", isInvokable: true },
      { type: "Button", name: "", automationId: "IconBtn", isInvokable: true },
      { type: "Button", name: "Hidden", isOffscreen: true, isInvokable: true },
    ] },
  ] }] };
  const els = interactiveElements(inspect);
  assert.equal(els.length, 4);
  const c = evaluateAccessibleNames(els);
  assert.equal(c.status, "fail");
  assert.match(c.detail!, /3\/4/);
  assert.equal(evaluateAccessibleNames([]).status, "na");
});

test("keyboard traversal needs focus on min(3, interactive) distinct controls", () => {
  const f = (sel: string) => ({ hasFocus: true, element: { selector: sel } });
  assert.equal(evaluateKeyboardTraversal([f("a"), f("b"), f("c")], 10).status, "pass");
  assert.equal(evaluateKeyboardTraversal([f("a"), f("a"), { hasFocus: false }], 10).status, "fail");
  assert.equal(evaluateKeyboardTraversal([f("a"), f("b")], 2).status, "pass");
  assert.equal(evaluateKeyboardTraversal([], 0).status, "na");
});

test("judge-reported states map to Q5", () => {
  assert.deepEqual(judgeChecks({ states: { status: "fail", reason: "no empty state" } }).map((c) => [c.id, c.status, c.source]), [["Q5", "fail", "judge"]]);
  assert.equal(judgeChecks({ states: { status: "weird" } })[0].status, "na");
});

test("score: gate failure zeroes the score; weights renormalize when parts are missing", () => {
  const pass = (id: string, kind: "gate" | "quality" = "quality") => ({ id, label: id, kind, source: "harness" as const, status: "pass" as const });
  const fail = (id: string, kind: "gate" | "quality" = "quality") => ({ id, label: id, kind, source: "harness" as const, status: "fail" as const });
  const reqs = [{ status: "pass" }, { status: "pass" }, { status: "fail" }, { status: "pass" }];

  const full = computeScoreV2({ builds: true, runs: true, checks: [pass("G0", "gate"), pass("P2"), fail("V3")], requirements: reqs, designScore: 7 });
  assert.equal(full.score, Math.round(60 * 0.75 + 30 * 0.5 + 7)); // 67
  assert.deepEqual(full.points, { requirements: 45, quality: 15, design: 7 });

  const gated = computeScoreV2({ builds: true, runs: true, checks: [fail("G0", "gate")], requirements: reqs, designScore: 10 });
  assert.equal(gated.score, 0);
  assert.deepEqual(gated.gates.failed, ["G0"]);

  assert.equal(computeScoreV2({ builds: false, runs: false, checks: [], requirements: [], designScore: null }).score, 0);

  const noDesign = computeScoreV2({ builds: true, runs: true, checks: [pass("P2")], requirements: reqs, designScore: null });
  assert.equal(noDesign.score, Math.round((100 * (60 * 0.75 + 30 * 1)) / 90)); // 83
});

test("G3 passes a clean Native AOT publish that shows a window", () => {
  const out = "  Generating native code\n  Calc -> C:\\app\\bin\\Release\\publish\\\n{ \"ProcessId\": 9040 }";
  const g = evaluateNativeAot(out, templateApp, { ok: true });
  assert.equal(g.id, "G3");
  assert.equal(g.kind, "gate");
  assert.equal(g.status, "pass");
});

test("G3 fails on trim/AOT warnings from the publish (deduplicated)", () => {
  const w = "C:\\app\\AotBad.cs(4,44): warning IL2026: Using member 'JsonSerializer.Serialize' [C:\\app\\Calc.csproj]";
  const out = [w, w, "C:\\app\\AotBad.cs(4,44): warning IL3050: RequiresDynamicCode", "C:\\app\\X.cs(1,1): warning CS8618: nullable"].join("\n");
  const g = evaluateNativeAot(out, templateApp, { ok: true });
  assert.equal(g.status, "fail");
  assert.match(g.detail!, /2 trim\/AOT warning\(s\): IL2026, IL3050/);
});

test("G3 fails when the project suppresses trim/AOT warnings", () => {
  const files = [...templateApp, { path: "C:/app/Directory.Build.props", content: "<Project><PropertyGroup><NoWarn>$(NoWarn);CS1591;IL2026</NoWarn><SuppressTrimAnalysisWarnings>true</SuppressTrimAnalysisWarnings></PropertyGroup></Project>" }];
  const g = evaluateNativeAot("", files, { ok: true });
  assert.equal(g.status, "fail");
  assert.match(g.detail!, /NoWarn IL2026 in Directory\.Build\.props/);
  assert.match(g.detail!, /SuppressTrimAnalysisWarnings/);
});

test("G3 allows UnconditionalSuppressMessage but reports it, and fails when the launch failed", () => {
  const files = [...templateApp, { path: "C:/app/Svc.cs", content: '[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated", Justification = "reviewed")] void M() {}' }];
  const ok = evaluateNativeAot("", files, { ok: true });
  assert.equal(ok.status, "pass");
  assert.match(ok.detail!, /1 UnconditionalSuppressMessage/);
  const bad = evaluateNativeAot("", templateApp, { ok: false, reason: "winapp run --aot failed (exit 1): PublishAot is not enabled" });
  assert.equal(bad.status, "fail");
  assert.match(bad.detail!, /PublishAot/);
});

test("a failed G3 gate zeroes the v2 score", () => {
  const g = evaluateNativeAot("", templateApp, { ok: false, reason: "no window" });
  const s = computeScoreV2({ builds: true, runs: true, checks: [g], requirements: [{ status: "pass" }, { status: "pass" }], designScore: 8 });
  assert.equal(s.score, 0);
  assert.deepEqual(s.gates.failed, ["G3"]);
});