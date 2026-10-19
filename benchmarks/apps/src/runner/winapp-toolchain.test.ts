import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { pickNativeExecutable } from "./winapp-toolchain.js";

test("pickNativeExecutable maps an npm wrapper to the bundled winapp.exe", { skip: process.platform !== "win32" }, () => {
  const root = mkdtempSync(join(tmpdir(), "toolchain-"));
  try {
    const npm = join(root, "npm");
    const bundled = join(npm, "node_modules", "@microsoft", "winappcli", "bin", "win-x64");
    mkdirSync(bundled, { recursive: true });
    for (const f of ["winapp", "winapp.cmd"]) writeFileSync(join(npm, f), "");
    writeFileSync(join(bundled, "winapp.exe"), "");
    const apps = join(root, "WindowsApps", "winapp.exe");

    assert.equal(pickNativeExecutable([join(npm, "winapp"), join(npm, "winapp.cmd"), apps], "x64"), join(bundled, "winapp.exe"));
    assert.equal(pickNativeExecutable([join(root, "other", "winapp.cmd"), apps], "x64"), apps);
    assert.equal(pickNativeExecutable([join(root, "other", "winapp.cmd")], "x64"), undefined);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
