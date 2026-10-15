import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { findAncestorProjectFiles, hideToolsFromPath } from "./trial-env.js";

test("hideToolsFromPath removes dirs containing the tool and shims their other executables", () => {
  const root = mkdtempSync(join(tmpdir(), "trial-env-"));
  try {
    const apps = join(root, "WindowsApps");
    const other = join(root, "bin");
    mkdirSync(apps);
    mkdirSync(other);
    for (const f of ["winapp.exe", "winget.exe", "python.exe", "readme.txt"]) writeFileSync(join(apps, f), "");
    writeFileSync(join(other, "dotnet.exe"), "");
    const shimDir = join(root, "shims");

    const r = hideToolsFromPath([apps, other].join(";"), ["winapp"], shimDir);
    const slot = join(shimDir, "0");
    assert.deepEqual(r.removedDirs, [apps]);
    assert.equal(r.path, [slot, other].join(";"));
    assert.deepEqual(readdirSync(slot).sort(), ["python.cmd", "winget.cmd"]);
    assert.match(readFileSync(join(slot, "winget.cmd"), "utf8"), /WindowsApps[\\/]winget\.exe" %\*/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("hideToolsFromPath keeps PATH precedence for the other tools", () => {
  const root = mkdtempSync(join(tmpdir(), "trial-env-"));
  try {
    const python = join(root, "Python");
    const apps = join(root, "WindowsApps");
    mkdirSync(python);
    mkdirSync(apps);
    writeFileSync(join(python, "python.exe"), "");
    for (const f of ["winapp.exe", "python.exe"]) writeFileSync(join(apps, f), "");
    const shimDir = join(root, "shims");

    const r = hideToolsFromPath([python, apps].join(";"), ["winapp"], shimDir);
    // The real Python still comes before the Store alias shim.
    assert.equal(r.path, [python, join(shimDir, "0")].join(";"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("hideToolsFromPath is a no-op when no PATH dir has the tool", () => {
  const root = mkdtempSync(join(tmpdir(), "trial-env-"));
  try {
    const r = hideToolsFromPath(join(root, "nothing"), ["winapp"], join(root, "shims"));
    assert.deepEqual(r.removedDirs, []);
    assert.deepEqual(r.shims, []);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("findAncestorProjectFiles finds stray projects in parents but not in the start dir", () => {
  const root = mkdtempSync(join(tmpdir(), "trial-env-"));
  try {
    const profile = join(root, "user");
    const results = join(profile, "source", "repo", "results");
    mkdirSync(results, { recursive: true });
    writeFileSync(join(profile, "FileExplorer.csproj"), "<Project/>");
    writeFileSync(join(profile, "source", "Directory.Build.props"), "<Project/>");
    writeFileSync(join(results, "Inside.csproj"), "<Project/>");
    const found = findAncestorProjectFiles(results).map((p) => p.replace(root, "<root>"));
    assert.ok(found.some((p) => p.endsWith("FileExplorer.csproj")));
    assert.ok(found.some((p) => p.endsWith("Directory.Build.props")));
    assert.ok(!found.some((p) => p.endsWith("Inside.csproj")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});
