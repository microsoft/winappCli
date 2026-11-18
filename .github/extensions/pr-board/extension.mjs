// Extension: pr-board
// An inbox of a repo's pull requests with a per-PR workspace: status, timeline,
// draft review, review/post/ship buttons, and a thread with the hosting agent.
import { createServer } from "node:http";
import { readFileSync, existsSync, statSync, watchFile, unwatchFile } from "node:fs";
import { randomBytes } from "node:crypto";
import { dirname, isAbsolute, join } from "node:path";
import { fileURLToPath } from "node:url";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { buildCards, detectRepo, fetchDetail, fetchRepoData, GhError, GROUPS } from "./github.mjs";
import { addHistory, addMessage, readState, stateFile, updateMeta, updatePr } from "./store.mjs";
import { answerPrompt, archReviewPrompt, messagePrompt, monitorPrompt, openChatPrompt, postPrompt, reviewPrompt, shipPrompt, summarizeAllPrompt, summarizePrompt, syncPrompt } from "./prompts.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO_RE = /^[\w.-]+\/[\w.-]+$/;
const AUTO_REFRESH_MS = 120_000;
const MIN_REFRESH_GAP_MS = 15_000;
const MIN_FORCED_GAP_MS = 5_000;
const TOKEN = randomBytes(16).toString("hex");

let session;

// ---- Boards (one per repo, shared by every canvas instance) ---------------

class Board {
    constructor(repo) {
        this.repo = repo;
        this.data = null;
        this.error = null;
        this.fetchedAt = 0;
        this.inflight = null;
        this.clients = new Set();
        this.timer = null;
        this.watching = false;
    }

    async refresh({ force = false } = {}) {
        if (this.inflight) return this.inflight;
        const gap = Date.now() - this.fetchedAt;
        if (this.data && gap < (force ? MIN_FORCED_GAP_MS : MIN_REFRESH_GAP_MS)) return;
        this.inflight = (async () => {
            try {
                this.data = await fetchRepoData(this.repo);
                this.error = null;
                await refreshLiveSessions();
            } catch (e) {
                this.error = { code: e instanceof GhError ? e.code : "error", message: e.message };
            } finally {
                this.fetchedAt = Date.now();
                this.inflight = null;
                this.broadcast();
            }
        })();
        this.broadcast();
        return this.inflight;
    }

    snapshot() {
        const state = readState(this.repo);
        const local = state.prs;
        return {
            repo: this.repo,
            sync: { syncedAt: state.meta.syncedAt || null, requestedAt: state.meta.syncRequestedAt || null },
            liveSessions: liveSessions.list,
            liveSessionsNote: liveSessions.note,
            groups: GROUPS,
            viewer: this.data?.viewer || null,
            cards: this.data ? buildCards(this.data, local) : [],
            error: this.error,
            fetchedAt: this.fetchedAt ? new Date(this.fetchedAt).toISOString() : null,
            loading: !!this.inflight,
        };
    }

    card(number) {
        return this.snapshot().cards.find((c) => c.number === number) || null;
    }

    broadcast() {
        if (!this.clients.size) return;
        const payload = `event: board\ndata: ${JSON.stringify(this.snapshot())}\n\n`;
        for (const res of this.clients) res.write(payload);
    }

    addClient(res) {
        this.clients.add(res);
        if (!this.timer) this.timer = setInterval(() => this.refresh(), AUTO_REFRESH_MS);
        if (!this.watching) {
            // Other sessions' extension processes write the same file; follow their updates.
            watchFile(stateFile(this.repo), { interval: 1500 }, () => this.broadcast());
            this.watching = true;
        }
    }

    removeClient(res) {
        this.clients.delete(res);
        if (!this.clients.size) {
            clearInterval(this.timer);
            this.timer = null;
            if (this.watching) unwatchFile(stateFile(this.repo));
            this.watching = false;
        }
    }
}

const boards = new Map();
function board(repo) {
    if (!boards.has(repo)) boards.set(repo, new Board(repo));
    return boards.get(repo);
}

let defaultRepoPromise = null;
function defaultRepo() {
    defaultRepoPromise ??= detectRepo(process.cwd());
    return defaultRepoPromise;
}

async function resolveRepo(repo) {
    const r = repo || (await defaultRepo());
    if (!r || !REPO_RE.test(r)) throw new Error("Couldn't tell which GitHub repo to use. Pass repo as \"owner/name\".");
    return r;
}

// ---- Card actions from the UI ----------------------------------------------

const POST_EVENTS = new Set(["approve", "request_changes", "comment"]);
const nowIso = () => new Date().toISOString();

// Record an event in both the timeline and the thread, so the thread reads as one story.
function note(s, text) {
    addHistory(s, text);
    addMessage(s, "system", text);
}

async function runCardAction({ repo, pr, action, event, force, extra = {} }) {
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.card(pr);
    if (!card) throw httpError(404, `PR #${pr} isn't on the board. Press Refresh.`);
    const local = card.local || {};
    const busy = local.agent?.status === "running";

    if (action === "review") {
        if (busy && !force) throw httpError(409, "An agent is already working on this PR. Use 'Reset agent status' if it's stuck.");
        updatePr(repo, pr, (s) => {
            s.agent = { status: "running", kind: "review", outcome: "Queued: asking the agent to start a review", updatedAt: nowIso() };
            s.draft = null;
            note(s, "You asked for an agent review");
        });
        await send(reviewPrompt(card, repo), repo, pr);
    } else if (action === "post") {
        if (!POST_EVENTS.has(event)) throw httpError(400, "Pick Approve, Request changes, or Comment.");
        if (!local.draft?.path || local.draft.postedAt) throw httpError(409, "There's no unposted draft to post. Run Review first.");
        if (busy && !force) throw httpError(409, "An agent is already working on this PR.");
        const word = event.replace("_", " ");
        updatePr(repo, pr, (s) => {
            s.postEvent = event;
            s.agent = { status: "running", kind: "post", outcome: `Posting the draft as "${word}"`, updatedAt: nowIso() };
            note(s, `You asked to post the draft as "${word}"`);
        });
        await send(postPrompt(card, repo, event), repo, pr);
    } else if (action === "ship") {
        if (card.lane === "merged" || card.queue) throw httpError(409, "This PR is already merged or queued.");
        if (busy && !force) throw httpError(409, "An agent is already working on this PR.");
        updatePr(repo, pr, (s) => {
            s.agent = { status: "running", kind: "ship", outcome: "Asking the agent to send it to the merge queue", updatedAt: nowIso() };
            note(s, "You asked to ship it");
        });
        await send(shipPrompt(card, repo), repo, pr);
    } else if (action === "summarize") {
        updatePr(repo, pr, (s) => {
            s.summarizing = { at: nowIso() };
            addHistory(s, "You asked for a plain-words summary");
        });
        await send(summarizePrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "arch-review") {
        updatePr(repo, pr, (s) => note(s, "You asked for an architecture review (opens in its own panel)"));
        await send(archReviewPrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "monitor") {
        const enable = extra.enable !== false;
        const actions = enable ? (extra.mergePr ? ["address_reviews", "fix_ci", "resolve_conflicts", "merge_pr"] : ["address_reviews", "fix_ci", "resolve_conflicts"]) : [];
        updatePr(repo, pr, (s) => {
            s.monitorPending = { enable, actions, at: nowIso() };
            note(s, enable ? "You asked to turn monitoring on" : "You asked to turn monitoring off");
        });
        await send(monitorPrompt(card, repo, enable, actions), repo, pr, { quiet: true });
    } else if (action === "answer") {
        const input = local.sync?.awaitingInput;
        const answer = String(extra.answer || "").trim();
        if (!input?.requestId) throw httpError(409, "There's no pending question for this PR. Press Sync.");
        if (!answer) throw httpError(400, "Type or pick an answer.");
        if (answer.length > 2000) throw httpError(413, "Keep answers under 2000 characters.");
        updatePr(repo, pr, (s) => {
            s.answered = { requestId: input.requestId, answer, at: nowIso() };
            note(s, `You answered: ${answer.slice(0, 120)}`);
        });
        await send(answerPrompt(card, repo, input, answer), repo, pr, { quiet: true });
    } else if (action === "dismiss") {
        const input = local.sync?.awaitingInput;
        updatePr(repo, pr, (s) => {
            s.dismissed = input?.requestId || "all";
            addHistory(s, "You dismissed the question on the board (it wasn't answered)");
        });
    } else if (action === "open-chat") {
        if (!local.session?.id) throw httpError(409, "No session is linked to this PR yet.");
        await send(openChatPrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "reset") {
        updatePr(repo, pr, (s) => {
            s.agent = null;
            note(s, "You reset the agent status");
        });
    } else {
        throw httpError(400, `Unknown action ${action}`);
    }
    b.broadcast();
    return { ok: true };
}

const MAX_MESSAGE = 4000;

// Conversation + files, fetched on selection and cached until the PR changes on GitHub.
const detailCache = new Map();
async function prDetail(repo, pr) {
    const card = board(repo).card(pr);
    const key = `${repo}#${pr}`;
    const hit = detailCache.get(key);
    const fresh = hit && hit.updatedAt === (card?.updatedAt || card?.mergedAt) && Date.now() - hit.at < 10 * 60_000;
    if (fresh) return hit.data;
    if (hit?.inflight) return hit.inflight;
    const inflight = fetchDetail(repo, pr).then((data) => {
        detailCache.set(key, { data, at: Date.now(), updatedAt: card?.updatedAt || card?.mergedAt });
        return data;
    }).catch((e) => {
        detailCache.delete(key);
        throw httpError(502, e.message);
    });
    detailCache.set(key, { ...(hit || {}), inflight });
    return inflight;
}

async function requestSync(repo) {
    const b = board(repo);
    if (!b.data) await b.refresh();
    const tracked = b.snapshot().cards.filter((c) => c.section !== "merged" && (c.local?.session?.id || c.section !== "other")).map((c) => ({ pr: c.number, sessionId: c.local?.session?.id || null, name: c.local?.session?.name || null }));
    updateMeta(repo, (m) => { m.syncRequestedAt = nowIso(); });
    b.broadcast();
    await session.send({ prompt: syncPrompt(repo, tracked) });
    return { ok: true, tracked: tracked.length };
}

// Best effort: live, opted-in local sessions the SDK can see directly. App sessions
// usually aren't opted in, so the coordinator-fed sync is the main source.
const liveSessions = { list: [], note: "not checked yet" };
async function refreshLiveSessions() {
    try {
        const r = await session.rpc.management.list({});
        liveSessions.list = (r?.sessions || []).map((s) => ({ sessionId: s.sessionId, name: s.name, state: s.state, parentSessionId: s.parentSessionId || null }));
        liveSessions.note = `management.list returned ${liveSessions.list.length}`;
    } catch (e) {
        liveSessions.list = [];
        liveSessions.note = `management.list unavailable: ${String(e?.message || e).slice(0, 160)}`;
    }
}

async function summarizeAll(repo) {
    const b = board(repo);
    if (!b.data) await b.refresh();
    const targets = b.snapshot().cards.filter((c) => c.group !== "merged" && !c.local?.summary && (c.group === "you" || c.group === "agent"));
    if (!targets.length) return { ok: true, count: 0 };
    for (const c of targets) updatePr(repo, c.number, (s) => { s.summarizing = { at: nowIso() }; });
    b.broadcast();
    await session.send({ prompt: summarizeAllPrompt(targets, repo) });
    return { ok: true, count: targets.length };
}

async function sendMessage({ repo, pr, text }) {
    text = String(text || "").trim();
    if (!text) throw httpError(400, "Type a message first.");
    if (text.length > MAX_MESSAGE) throw httpError(413, `Keep messages under ${MAX_MESSAGE} characters.`);
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.card(pr);
    if (!card) throw httpError(404, `PR #${pr} isn't on the board. Press Refresh.`);
    const recent = (card.local?.thread || []).filter((m) => m.role !== "system").slice(-6);
    let msg;
    updatePr(repo, pr, (s) => {
        msg = addMessage(s, "user", text);
        s.pending = { id: msg.id, at: msg.at };
    });
    b.broadcast();
    try {
        await session.send({ prompt: messagePrompt(card, repo, msg, recent) });
    } catch (e) {
        updatePr(repo, pr, (s) => {
            s.pending = null;
            addMessage(s, "system", `Couldn't reach the agent: ${e.message}`);
        });
        b.broadcast();
        throw httpError(502, `Couldn't send to the agent: ${e.message}`);
    }
    return { ok: true, id: msg.id };
}

async function send(prompt, repo, pr, { quiet = false } = {}) {
    try {
        await session.send({ prompt });
    } catch (e) {
        if (!quiet) {
            updatePr(repo, pr, (s) => {
                s.agent = { status: "failed", kind: s.agent?.kind, outcome: `Couldn't reach the agent: ${e.message}`, updatedAt: nowIso() };
                note(s, "Couldn't send the request to the agent");
            });
        }
        throw httpError(502, `Couldn't send to the agent: ${e.message}`);
    }
}

function httpError(status, message) {
    const e = new Error(message);
    e.status = status;
    return e;
}

// ---- Loopback server (one for the whole extension) -------------------------

let serverEntry = null;
const instances = new Map(); // instanceId → repo

function readBody(req) {
    return new Promise((resolve, reject) => {
        let body = "";
        req.on("data", (c) => {
            body += c;
            if (body.length > 64 * 1024) reject(httpError(413, "Body too large"));
        });
        req.on("end", () => {
            try { resolve(body ? JSON.parse(body) : {}); } catch { reject(httpError(400, "Invalid JSON")); }
        });
        req.on("error", reject);
    });
}

function checkRepo(r) {
    if (!r || !REPO_RE.test(r)) throw httpError(400, "Missing or invalid repo");
    return r;
}

async function handle(req, res) {
    const url = new URL(req.url, "http://127.0.0.1");
    const json = (status, obj) => {
        res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
        res.end(JSON.stringify(obj));
    };

    if (req.method === "GET" && url.pathname === "/") {
        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
        return res.end(readFileSync(join(HERE, "ui.html"), "utf8"));
    }
    if (req.method === "GET" && url.pathname === "/events") {
        const b = board(checkRepo(url.searchParams.get("repo")));
        res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-store", Connection: "keep-alive" });
        res.write(`event: board\ndata: ${JSON.stringify(b.snapshot())}\n\n`);
        b.addClient(res);
        const ping = setInterval(() => res.write(": ping\n\n"), 25_000);
        req.on("close", () => { clearInterval(ping); b.removeClient(res); });
        b.refresh();
        return;
    }
    if (req.method === "GET" && url.pathname === "/api/detail") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const pr = Number(url.searchParams.get("pr"));
        if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
        return json(200, await prDetail(repo, pr));
    }
    if (req.method === "GET" && url.pathname === "/api/draft") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const draft = readState(repo).prs[String(Number(url.searchParams.get("pr")))]?.draft;
        // Only ever serve the path an agent attached via pr_board_attach_draft.
        if (!draft?.path || !existsSync(draft.path)) return json(404, { error: "Draft file not found" });
        if (statSync(draft.path).size > 512 * 1024) return json(413, { error: "Draft is too large to preview" });
        return json(200, { path: draft.path, text: readFileSync(draft.path, "utf8") });
    }
    if (req.method === "POST") {
        // A custom header forces a CORS preflight, which this server never approves,
        // so other local web pages can't trigger agent actions.
        if (req.headers["x-pr-board-token"] !== TOKEN) return json(403, { error: "Forbidden" });
        const body = await readBody(req);
        const repo = checkRepo(body.repo);
        if (url.pathname === "/api/sync") {
            return json(200, await requestSync(repo));
        }
        if (url.pathname === "/api/summarize-all") {
            return json(200, await summarizeAll(repo));
        }
        if (url.pathname === "/api/refresh") {
            await board(repo).refresh({ force: true });
            return json(200, { ok: true });
        }
        if (url.pathname === "/api/message") {
            const pr = Number(body.pr);
            if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
            return json(200, await sendMessage({ repo, pr, text: body.text }));
        }
        if (url.pathname === "/api/action") {
            const pr = Number(body.pr);
            if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
            return json(200, await runCardAction({ repo, pr, action: String(body.action), event: body.event, force: !!body.force, extra: { enable: body.enable, mergePr: !!body.mergePr, answer: body.answer } }));
        }
    }
    json(404, { error: "Not found" });
}

async function ensureServer() {
    if (serverEntry) return serverEntry;
    const server = createServer((req, res) => {
        handle(req, res).catch((e) => {
            if (res.headersSent) return res.end();
            res.writeHead(e.status || 500, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify({ error: e.message }));
        });
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    serverEntry = { server, port: server.address().port };
    return serverEntry;
}

// ---- Agent tools --------------------------------------------------------------

const repoProp = { type: "string", description: "GitHub repo as owner/name. Defaults to the current repo." };
const prProp = { type: "number", description: "Pull request number." };

function summarizeCard(c) {
    return {
        pr: c.number,
        title: c.title,
        group: c.group,
        waitingOn: c.waitingOn,
        ci: c.ci?.text,
        session: c.local?.session || null,
        agent: c.local?.agent || null,
        draft: c.local?.draft || null,
        awaitingReply: !!c.local?.pending,
        section: c.section,
        review: c.review,
        sync: c.local?.sync || null,
        monitorPending: c.local?.monitorPending || null,
        summary: c.local?.summary || null,
        thread: (c.local?.thread || []).slice(-5).map((m) => ({ role: m.role, from: m.from, text: m.text.slice(0, 300), at: m.at })),
    };
}

const STATUS_WORDS = {
    running: "Agent working",
    waiting: "Agent needs your input",
    waiting_on_others: "Waiting on someone else",
    posted: "Review posted",
    done: "Done",
    failed: "Agent failed",
};

const tools = [
    {
        name: "pr_board_sync_sessions",
        description: "Tell the PR board about the sessions working on or reviewing PRs: activity, a pending question (awaiting input), Agent merge (monitoring) state, and when pr-review ran. Call it after a PR board Sync request, after turning monitoring on or off, and whenever a linked session's state changes. Omit fields you can't determine.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                sessions: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            pr: prProp,
                            sessionId: { type: "string" },
                            name: { type: "string" },
                            role: { type: "string", enum: ["author", "reviewer"], description: "author = working on the PR; reviewer = reviewing it." },
                            activity: { type: "string", enum: ["busy", "idle", "unknown"] },
                            awaitingInput: {
                                type: ["object", "null"],
                                properties: { requestId: { type: "string" }, question: { type: "string" }, choices: { type: "array", items: { type: "string" } } },
                                required: ["requestId", "question"],
                            },
                            agentMerge: {
                                type: ["object", "null"],
                                properties: { enabled: { type: "boolean" }, actions: { type: "array", items: { type: "string" } } },
                                required: ["enabled"],
                            },
                            prReview: {
                                type: ["object", "null"],
                                properties: { ranAt: { type: "string" }, headSha: { type: "string" }, result: { type: "string", enum: ["drafted", "posted"] }, event: { type: "string", enum: ["approve", "request_changes", "comment"] } },
                                required: ["ranAt"],
                            },
                        },
                        required: ["pr"],
                    },
                },
            },
            required: ["sessions"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const at = nowIso();
            const updated = [];
            for (const e of Array.isArray(args.sessions) ? args.sessions : []) {
                const n = Number(e.pr);
                if (!Number.isInteger(n) || n <= 0) continue;
                updatePr(repo, n, (s) => {
                    if (e.sessionId) s.session = { id: String(e.sessionId), name: e.name ? String(e.name).slice(0, 120) : s.session?.name || null, linkedAt: s.session?.linkedAt || at };
                    if (e.role === "author" || e.role === "reviewer") s.role = e.role;
                    const input = e.awaitingInput?.requestId ? { requestId: String(e.awaitingInput.requestId), question: String(e.awaitingInput.question || "").slice(0, 2000), choices: (e.awaitingInput.choices || []).map((x) => String(x).slice(0, 200)).slice(0, 8) } : null;
                    s.sync = {
                        activity: ["busy", "idle", "unknown"].includes(e.activity) ? e.activity : (s.sync?.activity || "unknown"),
                        awaitingInput: e.awaitingInput === undefined ? (s.sync?.awaitingInput || null) : input,
                        agentMerge: e.agentMerge === undefined ? (s.sync?.agentMerge ?? null) : (e.agentMerge ? { enabled: !!e.agentMerge.enabled, actions: (e.agentMerge.actions || []).map(String) } : null),
                        at,
                    };
                    if (e.agentMerge !== undefined) s.monitorPending = null;
                    if (!input || s.answered?.requestId !== input.requestId) s.answered = null;
                    if (input && s.dismissed && s.dismissed !== input.requestId) s.dismissed = null;
                    if (e.prReview?.ranAt) s.prReview = { ...(s.prReview || {}), result: e.prReview.result || s.prReview?.result || "drafted", at: e.prReview.ranAt, headSha: e.prReview.headSha || s.prReview?.headSha || null, event: e.prReview.event || s.prReview?.event || null, ...(e.prReview.result === "posted" ? { postedAt: e.prReview.ranAt } : { draftedAt: s.prReview?.draftedAt || e.prReview.ranAt }) };
                });
                updated.push(n);
            }
            updateMeta(repo, (m) => { m.syncedAt = at; });
            board(repo).broadcast();
            return { ok: true, repo, updated };
        },
    },
    {
        name: "pr_board_set_summary",
        description: "Set a PR's plain-words summary on the PR board, shown in the inbox row and the PR's Overview. Write it for a product manager: what changes for the person using the product, not how the code does it.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                summary: { type: "string", description: "One sentence, at most 140 characters, e.g. 'Adds `winapp ask` so you can ask a WinUI question and get answers from samples and Microsoft Learn.'" },
                nextSteps: { type: "array", items: { type: "string" }, description: "1 to 3 short steps, each starting with who acts, e.g. 'You: review the new help text'." },
                risk: { type: "string", enum: ["low", "medium", "high"], description: "low = small, tested, easy to undo; medium = shared behavior or thin tests; high = could break users, a release, or security." },
            },
            required: ["pr", "summary"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const summary = String(args.summary || "").replace(/\s+/g, " ").trim();
            if (!summary) throw new Error("summary is empty");
            const clipped = summary.length > 140 ? summary.slice(0, 139).trimEnd() + "…" : summary;
            const nextSteps = (Array.isArray(args.nextSteps) ? args.nextSteps : []).map((s) => String(s).trim().slice(0, 160)).filter(Boolean).slice(0, 3);
            updatePr(repo, args.pr, (s) => {
                s.summary = { text: clipped, nextSteps, risk: args.risk || null, at: nowIso() };
                s.summarizing = null;
                addHistory(s, "Summary updated");
            });
            board(repo).broadcast();
            return { ok: true, repo, pr: args.pr, summary: clipped, trimmed: clipped !== summary };
        },
    },
    {
        name: "pr_board_reply",
        description: "Post a reply in a PR's thread on the PR board. Use it to answer every \"[PR board] Message about ...\" request; the user reads replies only in the board, not in chat. Markdown: bold, italics, inline code, code blocks, lists and links.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                text: { type: "string", description: "The reply, in Markdown. Short and self-contained." },
                from: { type: "string", description: "Who answered, e.g. \"Coordinator\" or the linked session's name." },
            },
            required: ["pr", "text"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            if (!String(args.text || "").trim()) throw new Error("text is empty");
            let msg;
            updatePr(repo, args.pr, (s) => {
                msg = addMessage(s, "agent", String(args.text).trim(), args.from || null);
                s.pending = null;
            });
            board(repo).broadcast();
            return { ok: true, repo, pr: args.pr, id: msg.id };
        },
    },
    {
        name: "pr_board_link_session",
        description: "Link a Copilot session to a pull request card on the PR board, so the card shows it and 'Open chat' can open it. Call right after creating or reusing a session for a PR from a PR board request.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                sessionId: { type: "string", description: "The session's id (the id navigate_to / send_session_message accept)." },
                sessionName: { type: "string", description: "Short display name of the session." },
            },
            required: ["pr", "sessionId"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const name = args.sessionName ? String(args.sessionName).slice(0, 120) : null;
            const pr = updatePr(repo, args.pr, (s) => {
                s.session = { id: String(args.sessionId), name, linkedAt: nowIso() };
                if (s.agent?.status === "running") s.agent.outcome = `Session started: ${name || args.sessionId}`;
                addHistory(s, `Linked session ${name || args.sessionId}`);
            });
            board(repo).broadcast();
            return { ok: true, repo, pr: args.pr, session: pr.session };
        },
    },
    {
        name: "pr_board_set_status",
        description: "Set the agent status line on a PR board card. running = agent working; waiting = the session needs the user's input; waiting_on_others = blocked on someone other than the user, e.g. another approver or the author (put who and what in outcome, e.g. 'needs a second approval from a maintainer'); posted = the draft review was posted to GitHub; done = finished (e.g. queued for merge); failed = gave up (reason in outcome); clear = remove the status.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                status: { type: "string", enum: ["running", "waiting", "waiting_on_others", "posted", "done", "failed", "clear"] },
                outcome: { type: "string", description: "One short plain-words line shown on the card, e.g. 'Queued for merge' or 'Blocked: build failing'." },
            },
            required: ["pr", "status"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const outcome = args.outcome ? String(args.outcome).slice(0, 200) : null;
            const head = board(repo).card(args.pr)?.headSha || null;
            const pr = updatePr(repo, args.pr, (s) => {
                if (args.status === "clear") {
                    s.agent = null;
                    note(s, "Agent status cleared");
                    return;
                }
                if (args.status === "posted") {
                    if (s.draft) s.draft.postedAt = nowIso();
                    s.prReview = { ...(s.prReview || {}), result: "posted", at: nowIso(), postedAt: nowIso(), event: s.postEvent || s.prReview?.event || null, headSha: head || s.prReview?.headSha || null };
                    s.agent = { status: "done", kind: "post", outcome: outcome || "Review posted", updatedAt: nowIso() };
                } else {
                    s.agent = { status: args.status, kind: s.agent?.kind || null, outcome: outcome || s.agent?.outcome || null, updatedAt: nowIso() };
                }
                note(s, `${STATUS_WORDS[args.status]}${outcome ? `: ${outcome}` : ""}`);
            });
            board(repo).broadcast();
            return { ok: true, repo, pr: args.pr, agent: pr.agent };
        },
    },
    {
        name: "pr_board_attach_draft",
        description: "Attach a draft review file to a PR board card. Moves the card to 'Draft ready' and enables its Post review button. The file must contain exactly the review body to post.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                path: { type: "string", description: "Absolute path of the draft review Markdown file." },
                recommendation: { type: "string", enum: ["approve", "request_changes", "comment"] },
                summary: { type: "string", description: "One-line summary, e.g. 'Recommends merge; 1 optional nit'." },
            },
            required: ["pr", "path", "recommendation"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            if (!isAbsolute(args.path) || !existsSync(args.path)) throw new Error(`Draft file not found: ${args.path}. Pass an absolute path to an existing file.`);
            const words = { approve: "recommends approve", request_changes: "recommends request changes", comment: "recommends comment" }[args.recommendation];
            const summary = args.summary ? String(args.summary).slice(0, 200) : null;
            const head = board(repo).card(args.pr)?.headSha || null;
            updatePr(repo, args.pr, (s) => {
                s.draft = { path: args.path, recommendation: args.recommendation, summary, attachedAt: nowIso(), postedAt: null };
                s.prReview = { result: "drafted", at: nowIso(), draftedAt: nowIso(), headSha: head, recommendation: args.recommendation };
                s.agent = { status: "draft_ready", kind: "review", outcome: `Draft ready: ${summary || words}`, updatedAt: nowIso() };
                note(s, `Draft review attached (${words})${summary ? `: ${summary}` : ""}`);
            });
            board(repo).broadcast();
            return { ok: true, repo, pr: args.pr, lane: "draft-ready" };
        },
    },
    {
        name: "pr_board_get_state",
        description: "Read the PR board: each PR's lane, who it's waiting on, linked session, agent status and draft. Pass pr for a single card.",
        parameters: { type: "object", properties: { repo: repoProp, pr: prProp } },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const b = board(repo);
            await b.refresh();
            const cards = b.snapshot().cards.map(summarizeCard);
            if (args.pr) return cards.find((c) => c.pr === args.pr) || { pr: args.pr, local: readState(repo).prs[String(args.pr)] || null, note: "Not open or recently merged" };
            return { repo, error: b.error, cards };
        },
    },
];

// ---- Canvas ------------------------------------------------------------------

function instanceRepo(ctx) {
    const repo = instances.get(ctx.instanceId);
    if (!repo) throw new CanvasError("not_open", "This canvas instance isn't open.");
    return repo;
}

session = await joinSession({
    tools,
    canvases: [
        createCanvas({
            id: "pr-board",
            displayName: "PR board",
            description: "Inbox of a GitHub repo's pull requests grouped by who they wait on, with a per-PR workspace: status, timeline, draft review, review/post/ship buttons and an inline thread with the agent.",
            inputSchema: {
                type: "object",
                additionalProperties: false,
                properties: { repo: { type: "string", pattern: "^[\\w.-]+/[\\w.-]+$", description: "owner/name; defaults to the current repo" } },
            },
            actions: [
                {
                    name: "refresh",
                    description: "Re-read pull request data from GitHub now.",
                    inputSchema: { type: "object", additionalProperties: false, properties: {} },
                    handler: async (ctx) => {
                        const b = board(instanceRepo(ctx));
                        await b.refresh({ force: true });
                        const snap = b.snapshot();
                        return { ok: !snap.error, error: snap.error, cards: snap.cards.length, fetchedAt: snap.fetchedAt };
                    },
                },
                {
                    name: "get_board",
                    description: "Return the PRs: group (who it waits on), next step, linked session, agent status and recent thread messages.",
                    inputSchema: { type: "object", additionalProperties: false, properties: { group: { type: "string", enum: GROUPS.map((g) => g.id) } } },
                    handler: async (ctx) => {
                        const repo = instanceRepo(ctx);
                        const b = board(repo);
                        await b.refresh();
                        const all = b.snapshot().cards;
                        return {
                            repo,
                            error: b.error,
                            counts: Object.fromEntries(GROUPS.map((g) => [g.id, all.filter((c) => c.group === g.id).length])),
                            cards: all.filter((c) => !ctx.input?.group || c.group === ctx.input.group).map(summarizeCard),
                        };
                    },
                },
            ],
            open: async (ctx) => {
                let repo;
                try {
                    repo = await resolveRepo(ctx.input?.repo);
                } catch (e) {
                    throw new CanvasError("repo_unknown", e.message);
                }
                const { port } = await ensureServer();
                instances.set(ctx.instanceId, repo);
                board(repo).refresh();
                const qs = new URLSearchParams({ repo, t: TOKEN });
                return { title: `PR board · ${repo}`, status: "Pull requests", url: `http://127.0.0.1:${port}/?${qs}` };
            },
            onClose: async (ctx) => {
                instances.delete(ctx.instanceId);
                if (!instances.size && serverEntry) {
                    const { server } = serverEntry;
                    serverEntry = null;
                    server.closeAllConnections?.();
                    await new Promise((r) => server.close(() => r()));
                }
            },
        }),
    ],
});
