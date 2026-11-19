// Boot the real pr-board extension in this process with its own COPILOT_HOME. Offline, it runs against the fake
// world and seeds the board's state through its own agent tools (so seeding goes through the same validation).
// Live, it reads GitHub with the real gh and refuses every write.
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { execFile } from "node:child_process";
import "./hooks.mjs";
import { isGhWrite } from "./fake-child-process.mjs";
import { createWorld, HEAD, REPO } from "../fixtures/world.mjs";

export async function startBoard({ live = false } = {}) {
    const home = mkdtempSync(join(tmpdir(), "prb-ui-"));
    process.env.COPILOT_HOME = home;
    const repo = live ? (process.env.PR_BOARD_LIVE_REPO || "microsoft/winappCli") : REPO;
    const stateFile = join(home, "extensions", "pr-board", "artifacts", `${repo.replace("/", "__")}.json`);
    // Live mode reads a copy of your real board state, so opening items can't mark anything read for real.
    if (live) {
        const real = join(process.env.USERPROFILE || process.env.HOME || "", ".copilot", "extensions", "pr-board", "artifacts", `${repo.replace("/", "__")}.json`);
        mkdirSync(dirname(stateFile), { recursive: true });
        if (existsSync(real)) copyFileSync(real, stateFile);
    }
    const world = live ? null : createWorld();
    globalThis.__prBoardGh = world ? (args) => world.handle(args) : liveGh;
    const { host } = await import("./fake-sdk.mjs");
    await import("../../extension.mjs");
    const canvas = host.config.canvases[0];
    const tools = Object.fromEntries(host.config.tools.map((t) => [t.name, t]));
    const tool = async (name, args) => {
        const r = await tools[name].handler(args, {});
        if (r?.resultType === "failure") throw new Error(`${name}: ${r.error}`);
        return r;
    };
    const { url } = await canvas.open({ instanceId: "ui-test", input: { repo }, sessionId: "test-session" });
    const token = new URL(url).searchParams.get("t");
    const api = async (path, body) => {
        const r = await fetch(new URL(path, url), { method: "POST", headers: { "Content-Type": "application/json", "x-pr-board-token": token }, body: JSON.stringify({ repo, ...body }) });
        const j = await r.json();
        if (!r.ok) throw new Error(`${path}: ${j.error}`);
        return j;
    };
    const board = { url, repo, world, host, tool, api, home, stateFile, live, canvas, liveWrites };
    if (!live) {
        await waitUntil(async () => (await canvas.actions.find((a) => a.name === "get_board").handler({ instanceId: "ui-test", input: {} })).cards.length > 0, 20000, "board never loaded");
        await seed(board);
        board.baseline = readFileSync(stateFile, "utf8");
    }
    board.reset = () => { if (board.baseline) writeFileSync(stateFile, board.baseline); };
    board.close = async () => {
        await canvas.onClose({ instanceId: "ui-test" }).catch(() => {});
        try { rmSync(home, { recursive: true, force: true }); } catch { /* still open on Windows */ }
    };
    return board;
}

// Live mode: the real gh for reads. Anything that would change GitHub is refused and recorded.
const liveWrites = [];
function liveGh(args) {
    if (isGhWrite(args)) {
        liveWrites.push(args);
        return Promise.reject(new Error(`live smoke test refuses a GitHub write: gh ${args.join(" ").slice(0, 120)}`));
    }
    return new Promise((resolve, reject) => execFile("gh", args, { maxBuffer: 64 * 1024 * 1024, windowsHide: true }, (err, out, errText) => (err ? reject(Object.assign(new Error(String(errText || err.message)), { stderr: errText })) : resolve(out))));
}
export async function waitUntil(fn, ms = 10000, what = "condition") {
    const end = Date.now() + ms;
    for (;;) {
        if (await fn()) return;
        if (Date.now() > end) throw new Error(`Timed out waiting for ${what}`);
        await new Promise((r) => setTimeout(r, 100));
    }
}

const minutesAgo = (m) => new Date(Date.now() - m * 60_000).toISOString();

async function seed(b) {
    const { tool } = b;
    await tool("pr_board_sync_sessions", { repo: REPO, sessions: [
        { pr: 101, sessionId: "s-101", name: "Uploader retries", role: "author", activity: "idle", awaitingInput: null, agentMerge: { enabled: true, actions: ["address_reviews", "fix_ci", "resolve_conflicts"] }, prReview: { ranAt: minutesAgo(30), headSha: HEAD[101], result: "drafted" } },
        { pr: 102, sessionId: "s-102", name: "Empty config crash", role: "author", activity: "busy", awaitingInput: null, agentMerge: { enabled: false, actions: [] } },
        { pr: 104, sessionId: "s-104", name: "PR #104 review", role: "reviewer", activity: "idle" },
        { issue: 201, sessionId: "s-201", name: "Empty uploads", branch: "fix-201-zero-bytes", activity: "idle" },
        { issue: 203, sessionId: "s-203", name: "Settings persistence", branch: "fix-203-settings", activity: "idle" },
    ] });
    await tool("pr_board_set_review", { repo: REPO, pr: 104, review: {
        headSha: HEAD[104], verdict: "request-changes", risk: "medium", summary: "Refreshes tokens early. One race can log users out.",
        functionality: [{ title: "Tokens refresh five minutes before they expire" }],
        components: [{ id: "c1", name: "Auth", role: "Refresh logic moved into a timer", files: [{ path: "src/auth.js" }] }],
        diagrams: [{ id: "d1", kind: "flow", title: "Token refresh", nodes: [{ id: "n1", label: "RefreshTimer", status: "added" }, { id: "n2", label: "TokenStore", status: "modified" }], edges: [{ from: "n1", to: "n2", label: "writes" }] }],
        findings: [
            { id: "f1", title: "Two refreshes can race", category: "risk", severity: "high", what: "If two tabs refresh at once, the second overwrites a newer token.", fix: "Take a lock around the refresh.", files: [{ path: "src/auth.js", line: 42 }], diagramRefs: [{ diagram: "d1", node: "n1" }] },
            { id: "f2", title: "Rename `t` to `token`", category: "nit", severity: "low", what: "Short name.", fix: "Rename it.", files: [{ path: "src/auth.js", line: 10 }] },
        ],
    } });
    await tool("pr_board_set_review", { repo: REPO, pr: 105, review: { headSha: HEAD[105], verdict: "approve", risk: "low", summary: "Patch bump; nothing changes for users.", findings: [] } });
    await tool("pr_board_set_explainer", { repo: REPO, pr: 102, eli5: Array.from({ length: 8 }, (_, i) => `**Part ${i + 1}.** When \`config.json\` is empty the app crashed on start. This paragraph is long on purpose so the panel has to scroll.`).join("\n\n"), progress: "- **Today:** found the cause.\n\n**Next:** the agent fixes CI." });
    for (let i = 1; i <= 5; i++) await tool("pr_board_reply", { repo: REPO, pr: 102, text: `Update ${i}: still working on the CI failure.`, from: "Empty config crash" });
    await tool("pr_board_reply", { repo: REPO, pr: 104, text: "The risky part is [[finding:f1]] in [[file:src/auth.js#L40-L44]].", from: "PR #104 review" });
    await tool("pr_board_set_next_step", { repo: REPO, pr: 102, owner: "you", action: "Request a reviewer" });
    await tool("pr_board_set_next_step", { repo: REPO, pr: 105, owner: "you", action: "Approve now, or wait for the changelog?", choices: ["Approve now", "Wait for the changelog"] });
    // Everything is read except the last reply on #102, so exactly one item is unread.
    await b.api("/api/seen", { pr: 104 });
    const st = JSON.parse(readFileSync(b.stateFile, "utf8"));
    const last = st.prs["102"].thread.filter((m) => m.role === "agent").at(-1);
    st.prs["102"].lastSeenAt = new Date(new Date(last.at).getTime() - 1).toISOString();
    st.meta.readBaseline = minutesAgo(600);
    writeFileSync(b.stateFile, JSON.stringify(st, null, 2));
}
