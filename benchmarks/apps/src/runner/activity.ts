/**
 * Counts the skills an agent loaded and the winapp commands it ran, from a Copilot
 * session event stream (JSON lines). Mirrors Get-WinappCommands in
 * benchmarks/agents/lib/Benchmark.psm1 so both harnesses name commands the same way.
 */
import { existsSync, readFileSync } from "node:fs";

export interface Activity {
  skills: Array<{ name: string; count: number }>;
  winappCommands: Array<{ command: string; count: number }>;
}

const GROUPS = new Set(["cert", "config", "devtools", "manifest", "ui", "find-api", "target", "store", "node", "perf"]);
// Groups whose second token can be free text keep it only when it is a real subcommand.
const VERBS: Record<string, string[]> = {
  "find-api": ["members", "check-property", "types", "enums", "namespaces", "packages", "stats", "projects", "refresh"],
  node: ["create-addon", "add-electron-debug-identity", "clear-electron-debug-identity", "generate-bindings"],
};
const KNOWN = new Set([
  "az-sign", "cert", "config", "create-debug-identity", "devtools", "create-external-catalog", "embed-identity", "find-api", "find-ui",
  "get-winapp-path", "init", "manifest", "new", "node", "package", "pack", "perf", "restore", "run", "sign", "store", "target", "tool",
  "ui", "unregister", "update",
]);

/** Every `winapp <command> [<subcommand>]` in the text, in order, with repeats. */
export function winappCommandsIn(text: string): string[] {
  const found: string[] = [];
  for (const m of text.matchAll(/(?<![\w./\\-])winapp(?:\.exe)?\s+([a-z][a-z-]*)(?:\s+([a-z][a-z-]*))?/g)) {
    let cmd = m[1];
    if (!KNOWN.has(cmd)) continue;
    const sub = m[2];
    if (GROUPS.has(cmd) && sub && (!VERBS[cmd] || VERBS[cmd].includes(sub))) cmd += ` ${sub}`;
    found.push(cmd);
  }
  return found;
}

const bareSkillName = (n: string) => n.replace(/^.*[:/]/, "");

function tally(items: string[]): Array<[string, number]> {
  const counts = new Map<string, number>();
  for (const i of items) counts.set(i, (counts.get(i) ?? 0) + 1);
  return [...counts];
}

export function summarizeActivity(eventsPath: string): Activity | null {
  if (!existsSync(eventsPath)) return null;
  const skillEvents: string[] = [];
  const skillToolCalls: string[] = [];
  const commands: string[] = [];
  for (const line of readFileSync(eventsPath, "utf-8").split(/\r?\n/)) {
    if (!line.trim()) continue;
    let ev: any;
    try { ev = JSON.parse(line); } catch { continue; }
    const d = ev?.data;
    if (ev?.type === "skill.invoked" && typeof d?.name === "string") {
      skillEvents.push(bareSkillName(d.name));
    } else if (ev?.type === "tool.execution_start") {
      if (d?.toolName === "skill" && typeof d?.arguments?.skill === "string") skillToolCalls.push(bareSkillName(d.arguments.skill));
      if (typeof d?.arguments?.command === "string") commands.push(...winappCommandsIn(d.arguments.command));
    }
  }
  // `skill.invoked` and the `skill` tool call describe the same load; prefer the event.
  const skills = skillEvents.length > 0 ? skillEvents : skillToolCalls;
  return {
    skills: tally(skills).map(([name, count]) => ({ name, count })),
    winappCommands: tally(commands).map(([command, count]) => ({ command, count })),
  };
}
