import assert from "node:assert/strict";
import test from "node:test";
import { findVsMsBuild, msbuildRetryArgs, vswherePath } from "./vs-msbuild.js";

const env = { "ProgramFiles(x86)": "C:\\PF86" } as NodeJS.ProcessEnv;
const vswhere = "C:\\PF86\\Microsoft Visual Studio\\Installer\\vswhere.exe";
const msbuild = "C:\\VS\\MSBuild\\Current\\Bin\\MSBuild.exe";

test("vswhere is found under Program Files (x86)", () => {
  assert.equal(vswherePath(env), vswhere);
});

test("finds the first MSBuild.exe that vswhere lists", () => {
  let args: string[] = [];
  const found = findVsMsBuild(
    (_exe, a) => { args = a; return { status: 0, stdout: `${msbuild}\r\nC:\\VS\\MSBuild\\Current\\Bin\\amd64\\MSBuild.exe\r\n` }; },
    (p) => p === vswhere || p === msbuild,
    env,
  );
  assert.equal(found, msbuild);
  assert.ok(args.includes("Microsoft.Component.MSBuild"));
});

test("no retry builder without vswhere, or when vswhere finds nothing", () => {
  assert.equal(findVsMsBuild(() => { throw new Error("not called"); }, () => false, env), null);
  assert.equal(findVsMsBuild(() => ({ status: 0, stdout: "" }), (p) => p === vswhere, env), null);
  assert.equal(findVsMsBuild(() => ({ status: 1, stdout: msbuild }), () => true, env), null);
});

test("retry builds Debug for the same platform and restores first", () => {
  assert.deepEqual(msbuildRetryArgs("C:\\a\\App.csproj", "x64"), ["C:\\a\\App.csproj", "/restore", "/p:Configuration=Debug", "/p:Platform=x64"]);
});
