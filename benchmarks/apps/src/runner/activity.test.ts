import assert from "node:assert/strict";
import test from "node:test";
import { mkdtempSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { summarizeActivity, winappCommandsIn } from "./activity.js";

const write = (lines: unknown[]) => {
  const p = join(mkdtempSync(join(tmpdir(), "activity-")), "events.jsonl");
  writeFileSync(p, lines.map((l) => JSON.stringify(l)).join("\n"));
  return p;
};

test("winappCommandsIn keeps repeats and subcommands, and ignores prose", () => {
  assert.deepEqual(
    winappCommandsIn("winapp init; winapp cert generate --if-needed; winapp package .\\out; winapp is great; winapp cert install x"),
    ["init", "cert generate", "package", "cert install"],
  );
});

test("summarizeActivity counts skills and commands", () => {
  const a = summarizeActivity(write([
    { type: "skill.invoked", data: { name: "winapp-setup" } },
    { type: "skill.invoked", data: { name: "winapp-setup" } },
    { type: "skill.invoked", data: { name: "winui-design" } },
    { type: "tool.execution_start", data: { toolName: "powershell", arguments: { command: "winapp package out; winapp package out" } } },
    { type: "tool.execution_start", data: { toolName: "powershell", arguments: { command: "dotnet build" } } },
  ]));
  assert.deepEqual(a?.skills, [{ name: "winapp-setup", count: 2 }, { name: "winui-design", count: 1 }]);
  assert.deepEqual(a?.winappCommands, [{ command: "package", count: 2 }]);
});

test("summarizeActivity falls back to skill tool calls", () => {
  const a = summarizeActivity(write([{ type: "tool.execution_start", data: { toolName: "skill", arguments: { skill: "winapp-setup" } } }]));
  assert.deepEqual(a?.skills, [{ name: "winapp-setup", count: 1 }]);
});

test("summarizeActivity returns null when the file is missing", () => {
  assert.equal(summarizeActivity(join(tmpdir(), "no-such-events.jsonl")), null);
});
