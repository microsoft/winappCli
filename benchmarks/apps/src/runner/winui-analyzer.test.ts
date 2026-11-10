import assert from "node:assert/strict";
import test from "node:test";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import {
  analyzerTargets,
  failedBecauseOfAnalyzer,
  planWinUIAnalyzer,
  prepareWinUIAnalyzer,
  projectBuildFiles,
  WINUI_ANALYZER_PACKAGE,
  WINUI_ANALYZER_VERSION,
} from "./winui-analyzer.js";

const csproj = (items = "") => ({ path: "C:/app/App.csproj", content: `<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>${items}</ItemGroup></Project>` });

test("a project that doesn't reference the WinUI analyzer gets the pinned version for the scoring build", () => {
  assert.deepEqual(planWinUIAnalyzer([csproj('<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.0" />')]),
    { inject: true, source: "harness", version: WINUI_ANALYZER_VERSION });
});

test("a project that already references the analyzer keeps its own reference and version", () => {
  assert.deepEqual(planWinUIAnalyzer([csproj(`<PackageReference Include="${WINUI_ANALYZER_PACKAGE}" Version="0.7.0" PrivateAssets="all" />`)]),
    { inject: false, source: "project", version: "0.7.0" });
  // Different casing, and a version from central package management.
  const plan = planWinUIAnalyzer([
    csproj(`<PackageReference Include="${WINUI_ANALYZER_PACKAGE.toLowerCase()}" />`),
    { path: "C:/app/Directory.Packages.props", content: `<Project><ItemGroup><PackageVersion Include="${WINUI_ANALYZER_PACKAGE}" Version="0.6.2" /></ItemGroup></Project>` },
  ]);
  assert.deepEqual(plan, { inject: false, source: "project", version: "0.6.2" });
  // A reference in Directory.Build.props counts too.
  assert.equal(planWinUIAnalyzer([csproj(), { path: "C:/app/Directory.Build.props", content: `<PackageReference Include="${WINUI_ANALYZER_PACKAGE}" Version="0.7.1" />` }]).inject, false);
});

test("the added reference is skipped by MSBuild too when the project already has one", () => {
  const xml = analyzerTargets();
  assert.match(xml, /AnyHaveMetadataValue\('Identity', 'Microsoft\.Windows\.SDK\.BuildTools\.WinUIAnalyzer'\)\)' != 'true'/);
  assert.match(xml, new RegExp(`Version="${WINUI_ANALYZER_VERSION.replace(/\./g, "\\.")}" PrivateAssets="all"`));
  assert.match(xml, /VersionOverride=/);
});

test("the temporary targets file is outside the app, passed as a property, and deleted by cleanup", () => {
  const dir = mkdtempSync(join(tmpdir(), "winui-analyzer-"));
  try {
    const injected = prepareWinUIAnalyzer({ inject: true, source: "harness", version: WINUI_ANALYZER_VERSION }, dir);
    const file = join(dir, "benchmark-winui-analyzer.targets");
    assert.deepEqual(injected.args, [`-p:CustomAfterMicrosoftCommonTargets=${file}`]);
    assert.match(readFileSync(file, "utf-8"), /WinUIAnalyzer/);
    injected.cleanup();
    assert.equal(existsSync(file), false);
    injected.cleanup();

    const own = prepareWinUIAnalyzer({ inject: false, source: "project", version: "0.7.0" }, dir);
    assert.deepEqual(own.args, []);
    assert.equal(existsSync(file), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("project build files include Directory.Build files inside the app folder only", () => {
  const root = mkdtempSync(join(tmpdir(), "winui-analyzer-files-"));
  try {
    const app = join(root, "app");
    mkdirSync(join(app, "src", "App"), { recursive: true });
    writeFileSync(join(root, "Directory.Build.props"), "<Project />");
    writeFileSync(join(app, "Directory.Build.props"), "<Project />");
    writeFileSync(join(app, "src", "App", "App.csproj"), "<Project />");
    const files = projectBuildFiles(join(app, "src", "App", "App.csproj"), app).map((f) => f.path);
    assert.deepEqual(files, [join(app, "src", "App", "App.csproj"), join(app, "Directory.Build.props")]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("only a failure caused by the added analyzer triggers a build without it", () => {
  assert.equal(failedBecauseOfAnalyzer(`error NU1101: Unable to find package ${WINUI_ANALYZER_PACKAGE}`), true);
  assert.equal(failedBecauseOfAnalyzer("MainPage.xaml.cs(12,5): error WUI1001: something"), true);
  assert.equal(failedBecauseOfAnalyzer("Program.cs(3,1): error CS1002: ; expected"), false);
});
