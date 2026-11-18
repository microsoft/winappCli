/**
 * Locate the agent file and skills folder inside a plugin checkout.
 *
 * Supported plugin layouts:
 *   - Open Plugins (winappCli plugins/*):  <root>/plugin.json,
 *       <root>/com.github.copilot/agents/*.agent.md, <root>/skills/
 *   - Host wrapper roots:  <root>/agents/*.agent.md, skills path declared in
 *       <root>/.claude-plugin/plugin.json (e.g. "./agent-plugin/skills/")
 *   - Flat:  <root>/agents/*.agent.md, <root>/skills/
 *
 * Resolution fails loudly (throws) when no agent or no skills are found, so a
 * layout change upstream can't silently produce a skill-less benchmark trial.
 */
import { existsSync, readdirSync, readFileSync, statSync } from "fs";
import { join, resolve } from "path";

export interface PluginLayout {
  /** Absolute path to the first agent file, if the plugin ships one. */
  agentFile?: string;
  /** All agent files the plugin ships, sorted. */
  agentFiles: string[];
  /** Absolute path to the folder containing one subfolder per skill. */
  skillsDir: string;
  /** Names of skill folders that contain a SKILL.md, sorted. */
  skills: string[];
  /** `name` from the plugin manifest, when present. */
  name?: string;
  /** `version` from the plugin manifest, when present. */
  version?: string;
}

const MANIFESTS = ["plugin.json", join(".claude-plugin", "plugin.json")];
const AGENT_DIRS = ["agents", join("com.github.copilot", "agents")];

function readManifests(pluginRoot: string): any[] {
  const out: any[] = [];
  for (const rel of MANIFESTS) {
    const p = join(pluginRoot, rel);
    if (!existsSync(p)) continue;
    try { out.push(JSON.parse(readFileSync(p, "utf-8"))); } catch { /* ignore malformed manifest */ }
  }
  return out;
}

function isDir(p: string): boolean {
  try { return statSync(p).isDirectory(); } catch { return false; }
}

export function listSkills(skillsDir: string): string[] {
  if (!isDir(skillsDir)) return [];
  return readdirSync(skillsDir)
    .filter((d) => existsSync(join(skillsDir, d, "SKILL.md")))
    .sort();
}

export function resolvePluginLayout(
  pluginRoot: string,
  opts: { requireAgent?: boolean } = {},
): PluginLayout {
  const requireAgent = opts.requireAgent ?? true;
  const manifests = readManifests(pluginRoot);

  const skillCandidates: string[] = [];
  for (const m of manifests) {
    if (typeof m?.skills === "string") skillCandidates.push(resolve(pluginRoot, m.skills));
  }
  skillCandidates.push(join(pluginRoot, "skills"));
  const skillsDir = skillCandidates.find((d) => listSkills(d).length > 0);
  if (!skillsDir) {
    throw new Error(
      `No skills found in plugin at ${pluginRoot} (looked in: ${skillCandidates.join(", ")}). ` +
      `The plugin layout may have changed upstream; refusing to run a skill-less trial.`,
    );
  }

  const agentFiles: string[] = [];
  for (const m of manifests) {
    const declared: unknown = m?.agents?.[0]?.path ?? m?.agent;
    if (typeof declared === "string") {
      const abs = resolve(pluginRoot, declared);
      if (existsSync(abs) && !isDir(abs)) { agentFiles.push(abs); break; }
    }
  }
  for (const rel of AGENT_DIRS) {
    const dir = join(pluginRoot, rel);
    if (!isDir(dir)) continue;
    for (const f of readdirSync(dir).filter((f) => f.endsWith(".agent.md")).sort()) {
      const abs = join(dir, f);
      if (!agentFiles.includes(abs)) agentFiles.push(abs);
    }
  }
  if (requireAgent && agentFiles.length === 0) {
    throw new Error(
      `No *.agent.md found in plugin at ${pluginRoot} (looked in: ${AGENT_DIRS.join(", ")}).`,
    );
  }

  const named = manifests.find((m) => typeof m?.name === "string");
  const versioned = manifests.find((m) => typeof m?.version === "string");
  return {
    agentFile: agentFiles[0],
    agentFiles,
    skillsDir,
    skills: listSkills(skillsDir),
    name: named?.name,
    version: versioned?.version,
  };
}

/** Agent name from frontmatter `name:`, falling back to the file name. */
export function agentNameOf(agentFile: string): string {
  const fm = readFileSync(agentFile, "utf-8").match(/^---\s*\r?\n([\s\S]*?)\r?\n---/);
  const name = fm?.[1].match(/^name:\s*["']?([^"'\r\n]+?)["']?\s*$/m)?.[1];
  return name ?? agentFile.replace(/^.*[\\/]/, "").replace(/\.agent\.md$/, "");
}

/**
 * Expected skills that the session does not list as enabled. When `roots` is
 * given, a skill only counts if its file lives under one of them, so a
 * same-named skill from another source (e.g. a user-installed plugin) can't
 * satisfy the check.
 */
export function findMissingSkills(
  expected: string[],
  loaded: Array<{ name: string; enabled?: boolean; path?: string }>,
  roots?: string[],
): string[] {
  const norm = (p: string) => resolve(p).replace(/\\/g, "/").toLowerCase().replace(/\/?$/, "/");
  const normRoots = roots?.map(norm);
  const ok = new Set(
    loaded
      .filter((s) => s.enabled !== false)
      .filter((s) => !normRoots || (!!s.path && normRoots.some((r) => norm(s.path!).startsWith(r))))
      .map((s) => s.name),
  );
  return expected.filter((s) => !ok.has(s));
}
