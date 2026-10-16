import assert from "node:assert/strict";
import test from "node:test";
import { spawnSync } from "child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import {
  diffEnvValues,
  isTrialEnvPathEntry,
  manifestIdentities,
  manifestIdentityName,
  prunePathValue,
  snapshotUserEnv,
  trialPackages,
  userPathRaw,
} from "./machine-guard.js";
import { applyTrialEnv, createTrialEnv, removeTrialEnv } from "./benchmark.js";

const env = { LOCALAPPDATA: "C:\\Users\\me\\AppData\\Local" };

test("trial-env PATH entries are recognised, raw or with %VARS%", () => {
  assert.ok(isTrialEnvPathEntry("C:\\Users\\me\\AppData\\Local\\win-dev-bench\\trial-env\\run906-001-ab\\dotnet\\.dotnet\\tools", env));
  assert.ok(isTrialEnvPathEntry("%LOCALAPPDATA%\\win-dev-bench\\trial-env\\run9-000-cd\\dotnet\\.dotnet\\tools", env));
  assert.ok(!isTrialEnvPathEntry("C:\\Users\\me\\.dotnet\\tools", env));
  assert.ok(!isTrialEnvPathEntry("C:\\Users\\me\\AppData\\Local\\win-dev-bench\\cache", env));
});

test("prunePathValue removes only matching entries and keeps the rest byte-for-byte", () => {
  const raw = "%USERPROFILE%\\.cargo\\bin;C:\\x\\win-dev-bench\\trial-env\\r-1\\dotnet\\.dotnet\\tools;;C:\\Tools;C:\\x\\win-dev-bench\\trial-env\\r-2\\t";
  const { value, removed } = prunePathValue(raw, (e) => isTrialEnvPathEntry(e, env));
  assert.equal(value, "%USERPROFILE%\\.cargo\\bin;;C:\\Tools");
  assert.equal(removed.length, 2);
  const same = prunePathValue("C:\\a;C:\\b", () => false);
  assert.equal(same.value, "C:\\a;C:\\b");
  assert.equal(same.removed.length, 0);
});

test("diffEnvValues ignores Path and compares names case-insensitively", () => {
  assert.deepEqual(diffEnvValues({ Path: "a", TEMP: "t" }, { PATH: "b", temp: "t" }), []);
  assert.deepEqual(diffEnvValues({ TEMP: "t" }, { TEMP: "t", WINAPP_X: "1" }), ["winapp_x"]);
  assert.deepEqual(diffEnvValues({ DOTNET_ROOT: "x" }, {}), ["dotnet_root"]);
});

test("manifest identities are read across lines, including AppX layouts in bin", () => {
  const root = mkdtempSync(join(tmpdir(), "mg-"));
  try {
    writeFileSync(join(root, "Package.appxmanifest"), '<Package>\n  <Identity\n    Name="EA00A273-94CE"\n    Publisher="CN=x" />\n</Package>');
    mkdirSync(join(root, "bin", "x64", "Debug", "AppX"), { recursive: true });
    writeFileSync(join(root, "bin", "x64", "Debug", "AppX", "AppxManifest.xml"), '<Package><Identity Name="Other.App" /></Package>');
    assert.deepEqual(manifestIdentities(root).sort(), ["EA00A273-94CE", "Other.App"]);
    assert.equal(manifestIdentityName("<Package/>"), null);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("trialPackages only picks the trial's identities from trial/staging/deleted locations", () => {
  const all = [
    { name: "Calc.Trial", fullName: "Calc.Trial_1", location: "C:\\r\\run9\\001_calc\\app\\bin\\AppX" },
    { name: "Calc.Trial", fullName: "Calc.Trial_2", location: "" },
    { name: "Calc.Trial", fullName: "Calc.Trial_3", location: "C:\\Users\\me\\source\\Calc\\AppX" },
    { name: "Calc.Trial", fullName: "Calc.Trial_4", location: "C:\\winapp-stage\\Calc_ab12" },
    { name: "Unrelated", fullName: "Unrelated_1", location: "" },
  ];
  const picked = trialPackages(all, ["calc.trial"], ["C:\\r\\run9\\001_calc", "C:\\winapp-stage"]).map((p) => p.fullName);
  assert.deepEqual(picked, ["Calc.Trial_1", "Calc.Trial_2", "Calc.Trial_4"]);
});

// Regression for the 2026-10-15 incident: a fresh DOTNET_CLI_HOME made the
// SDK's first-run setup append `<home>\.dotnet\tools` to the registry user
// PATH on every trial until Windows sign-in broke.
test("a trial's dotnet first run leaves the registry user PATH unchanged", { skip: process.platform !== "win32", timeout: 300_000 }, () => {
  const before = userPathRaw(snapshotUserEnv());
  const ctx = createTrialEnv(join(tmpdir(), "run-guard-test"), "000_guard", {} as any);
  try {
    const env = applyTrialEnv(process.env, ctx);
    for (const args of [["--version"], ["new", "list", "console"]]) {
      spawnSync("dotnet", args, { env, encoding: "utf8", windowsHide: true, timeout: 180_000 });
    }
    assert.equal(userPathRaw(snapshotUserEnv()), before);
  } finally {
    removeTrialEnv(ctx);
  }
});
