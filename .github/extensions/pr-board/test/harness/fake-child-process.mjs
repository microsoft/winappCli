// Replaces node:child_process for github.mjs only. Every `gh` call goes to the handler a test installs
// (globalThis.__prBoardGh), so tests are offline and can never post to GitHub.
// With PR_BOARD_RECORD=<dir>, calls go to the real gh and each response is saved there (for learning
// response shapes; never in a normal run).
import * as real from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export const calls = [];

// gh commands that change something on GitHub. Tests never let these through.
export function isGhWrite(args) {
    const verbs = { pr: ["edit", "merge", "review", "ready", "close", "comment", "create"], issue: ["edit", "close", "comment", "create"] };
    return args.includes("-X") || args.includes("--method") || !!verbs[args[0]]?.includes(args[1])
        || (args[0] === "api" && args[1] === "graphql" && args.some((a) => /^query=\s*mutation\b/.test(a)));
}
globalThis.__prBoardGhCalls = calls;
let n = 0;

export function execFile(file, args, opts, cb) {
    if (typeof opts === "function") { cb = opts; opts = {}; }
    const rec = process.env.PR_BOARD_RECORD;
    if (rec) {
        return real.execFile(file, args, opts, (err, stdout, stderr) => {
            mkdirSync(rec, { recursive: true });
            writeFileSync(join(rec, `${String(++n).padStart(3, "0")}.json`), JSON.stringify({ args, err: err ? String(stderr || err.message) : null, stdout: String(stdout || "") }, null, 1));
            cb(err, stdout, stderr);
        });
    }
    const entry = { args, at: Date.now() };
    calls.push(entry);
    const handler = globalThis.__prBoardGh;
    Promise.resolve()
        .then(() => {
            if (!handler) throw new Error("no gh handler installed");
            return handler(args, opts);
        })
        .then((out) => {
            entry.ok = true;
            cb(null, typeof out === "string" ? out : JSON.stringify(out), "");
        }, (e) => {
            entry.ok = false;
            entry.error = e.message;
            cb(Object.assign(new Error(e.message), { code: e.code ?? 1 }), "", e.stderr || e.message);
        });
    return { kill() {}, on() {} };
}

export default { ...real, execFile };
export const { spawn, exec, spawnSync, execSync, execFileSync, fork } = real;
