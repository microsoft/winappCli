import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { findMissingSkills, resolvePluginLayout } from "./plugin-layout.js";

function makeTree(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), "plugin-layout-"));
  for (const [rel, content] of Object.entries(files)) {
    const p = join(root, rel);
    mkdirSync(join(p, ".."), { recursive: true });
    writeFileSync(p, content);
  }
  return root;
}

test("resolves the legacy agents/ + skills/ layout", () => {
  const root = makeTree({
    "agents/winui-dev.agent.md": "x",
    "skills/b-skill/SKILL.md": "x",
    "skills/a-skill/SKILL.md": "x",
    "skills/not-a-skill/readme.md": "x",
  });
  try {
    const l = resolvePluginLayout(root);
    assert.equal(l.agentFile, join(root, "agents", "winui-dev.agent.md"));
    assert.equal(l.skillsDir, join(root, "skills"));
    assert.deepEqual(l.skills, ["a-skill", "b-skill"]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("honors the skills path declared in .claude-plugin/plugin.json (host wrapper root)", () => {
  const root = makeTree({
    ".claude-plugin/plugin.json": JSON.stringify({ name: "winui", skills: "./agent-plugin/skills/" }),
    "agents/winui-dev.agent.md": "x",
    "agent-plugin/skills/winui-setup/SKILL.md": "x",
  });
  try {
    const l = resolvePluginLayout(root);
    assert.equal(l.agentFile, join(root, "agents", "winui-dev.agent.md"));
    assert.deepEqual(l.skills, ["winui-setup"]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("finds agents under com.github.copilot/agents (Open Plugins layout)", () => {
  const root = makeTree({
    "plugin.json": JSON.stringify({ name: "winui" }),
    "com.github.copilot/agents/winui-dev.agent.md": "x",
    "skills/winui-design/SKILL.md": "x",
  });
  try {
    const l = resolvePluginLayout(root);
    assert.equal(l.agentFile, join(root, "com.github.copilot", "agents", "winui-dev.agent.md"));
    assert.deepEqual(l.skills, ["winui-design"]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("throws when the plugin has no skills instead of running skill-less", () => {
  const root = makeTree({ "agents/winui-dev.agent.md": "x", "agent-plugin/skills/winui-setup/SKILL.md": "x" });
  try {
    assert.throws(() => resolvePluginLayout(root), /No skills found/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("throws when the plugin has no agent file", () => {
  const root = makeTree({ "skills/winui-setup/SKILL.md": "x" });
  try {
    assert.throws(() => resolvePluginLayout(root), /No \*\.agent\.md found/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("findMissingSkills reports absent and disabled skills", () => {
  const loaded = [
    { name: "winui-design", enabled: true },
    { name: "winapp-setup" },
    { name: "winui-setup", enabled: false },
  ];
  assert.deepEqual(findMissingSkills(["winui-design", "winapp-setup"], loaded), []);
  assert.deepEqual(findMissingSkills(["winui-setup", "nope", "winui-design"], loaded), ["winui-setup", "nope"]);
});

test("findMissingSkills with roots rejects same-named skills from other sources", () => {
  const plugin = join(tmpdir(), "bench", "plugins", "winui");
  const loaded = [
    { name: "winui-design", path: join(plugin, "skills", "winui-design", "SKILL.md") },
    { name: "winui-setup", path: join(tmpdir(), "user-home", "installed-plugins", "winui", "skills", "winui-setup", "SKILL.md") },
    { name: "winui-dev-workflow" },
  ];
  assert.deepEqual(
    findMissingSkills(["winui-design", "winui-setup", "winui-dev-workflow"], loaded, [plugin.toUpperCase()]),
    ["winui-setup", "winui-dev-workflow"],
  );
});
