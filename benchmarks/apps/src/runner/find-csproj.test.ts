import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { findCsproj } from "./benchmark.js";

function tree(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), "findcsproj-"));
  for (const [rel, content] of Object.entries(files)) {
    const full = join(root, rel);
    mkdirSync(join(full, ".."), { recursive: true });
    writeFileSync(full, content);
  }
  return root;
}

const lib = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
const tests = "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
const app = "<Project><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";

test("findCsproj prefers the WinUI app over a library in a folder that sorts first", () => {
  const root = tree({ "Journal.Core/Journal.Core.csproj": lib, "Journal.Tests/Journal.Tests.csproj": tests, "Stillwater.csproj": app });
  try { assert.equal(findCsproj(root), join(root, "Stillwater.csproj")); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("findCsproj prefers a nested app project over a root library and skips bin/obj", () => {
  const root = tree({ "Checks.csproj": lib, "src/App/App.csproj": app, "src/App/obj/Fake.csproj": app });
  try { assert.equal(findCsproj(root), join(root, "src", "App", "App.csproj")); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("findCsproj returns the only project, or null", () => {
  const one = tree({ "A/A.csproj": lib });
  const none = tree({ "readme.md": "" });
  try {
    assert.equal(findCsproj(one), join(one, "A", "A.csproj"));
    assert.equal(findCsproj(none), null);
  } finally { rmSync(one, { recursive: true, force: true }); rmSync(none, { recursive: true, force: true }); }
});