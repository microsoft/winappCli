// Durable per-PR state, shared by every session and canvas on this machine.
// One JSON file per repo: $COPILOT_HOME/extensions/pr-board/artifacts/<owner>__<repo>.json
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const HISTORY_LIMIT = 30;
const THREAD_LIMIT = 100;
const MESSAGE_LIMIT = 8000;

export function artifactsDir() {
    const home = process.env.COPILOT_HOME || join(homedir(), ".copilot");
    return join(home, "extensions", "pr-board", "artifacts");
}

export function stateFile(repo) {
    return join(artifactsDir(), `${repo.replace("/", "__")}.json`);
}

export function readState(repo) {
    const file = stateFile(repo);
    if (!existsSync(file)) return { repo, prs: {}, meta: {} };
    try {
        const data = JSON.parse(readFileSync(file, "utf8"));
        return { repo, prs: data.prs && typeof data.prs === "object" ? data.prs : {}, meta: data.meta && typeof data.meta === "object" ? data.meta : {} };
    } catch {
        return { repo, prs: {}, meta: {} };
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
    pr.thread = (pr.thread || []).slice(-THREAD_LIMIT);
    pr.updatedAt = new Date().toISOString();
    state.prs[key] = pr;
    writeState(repo, state);
    return pr;
}

// Repo-level board data, e.g. when sessions were last synced.
export function updateMeta(repo, mutate) {
    const state = readState(repo);
    mutate(state.meta);
    writeState(repo, state);
    return state.meta;
}

function writeState(repo, state) {
    mkdirSync(artifactsDir(), { recursive: true });
    const file = stateFile(repo);
    const tmp = `${file}.${process.pid}.tmp`;
    writeFileSync(tmp, JSON.stringify(state, null, 2));
    renameSync(tmp, file);
}

export function addHistory(pr, text) {
    pr.history = pr.history || [];
    pr.history.push({ at: new Date().toISOString(), text: String(text).slice(0, 300) });
}

// One conversation per PR. role: "user" (typed in the canvas), "agent" (pr_board_reply)
// or "system" (a button click or status change, so the thread reads as one story).
export function addMessage(pr, role, text, from = null) {
    pr.thread = pr.thread || [];
    const msg = { id: randomBytes(6).toString("hex"), role, text: String(text).slice(0, MESSAGE_LIMIT), at: new Date().toISOString() };
    if (from) msg.from = String(from).slice(0, 80);
    pr.thread.push(msg);
    return msg;
}
