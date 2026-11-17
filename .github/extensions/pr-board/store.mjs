// Durable per-PR state, shared by every session and canvas on this machine.
// One JSON file per repo: $COPILOT_HOME/extensions/pr-board/artifacts/<owner>__<repo>.json
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const HISTORY_LIMIT = 30;

export function artifactsDir() {
    const home = process.env.COPILOT_HOME || join(homedir(), ".copilot");
    return join(home, "extensions", "pr-board", "artifacts");
}

export function stateFile(repo) {
    return join(artifactsDir(), `${repo.replace("/", "__")}.json`);
}

export function readState(repo) {
    const file = stateFile(repo);
    if (!existsSync(file)) return { repo, prs: {} };
    try {
        const data = JSON.parse(readFileSync(file, "utf8"));
        return { repo, prs: data.prs && typeof data.prs === "object" ? data.prs : {} };
    } catch {
        return { repo, prs: {} };
    }
}

// Read-modify-write against the file (not memory) so several extension
// processes (one per session) don't overwrite each other's updates.
export function updatePr(repo, number, mutate) {
    const state = readState(repo);
    const key = String(number);
    const pr = state.prs[key] || { history: [] };
    mutate(pr);
    pr.history = (pr.history || []).slice(-HISTORY_LIMIT);
    pr.updatedAt = new Date().toISOString();
    state.prs[key] = pr;
    mkdirSync(artifactsDir(), { recursive: true });
    const file = stateFile(repo);
    const tmp = `${file}.${process.pid}.tmp`;
    writeFileSync(tmp, JSON.stringify(state, null, 2));
    renameSync(tmp, file);
    return pr;
}

export function addHistory(pr, text) {
    pr.history = pr.history || [];
    pr.history.push({ at: new Date().toISOString(), text: String(text).slice(0, 300) });
}
