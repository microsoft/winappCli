import { readdirSync } from "node:fs";
// Extension: pr-board
// An inbox of a repo's pull requests with a per-PR workspace: status, timeline,
// draft review, review/post/ship buttons, and a thread with the hosting agent.
import { createServer } from "node:http";
import { readFileSync, existsSync, statSync, watchFile, unwatchFile } from "node:fs";
import { randomBytes } from "node:crypto";
import { dirname, isAbsolute, join } from "node:path";
import { fileURLToPath } from "node:url";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { buildCards, buildIssueCards, detectRepo, computeReadiness, fetchReadinessRaw, fetchBranchCompare, fetchChangesSince, fetchDetail, fetchGitHubView, fetchIssues, fetchRepoData, GhError, GROUPS } from "./github.mjs";
import { addHistory, addMessage, readState, stateFile, updateItem, updateMeta, updatePr } from "./store.mjs";
import { composeGithubReview, GUIDE, mergeReview, normalizeReview, REVIEW_SCHEMA, withLinks } from "./review.mjs";
import { writeFileSync as writeFile, mkdirSync as mkdir } from "node:fs";
import { artifactsDir } from "./store.mjs";
import { createStreamer } from "./stream.mjs";
import { readyForReviewPrompt, requestReviewerPrompt, fixFindingPrompt, fixDescriptionPrompt, branchCommentsPrompt, openBranchPrPrompt, choosePrompt, fixPrompt, openPrPrompt, answerPrompt, explainerPrompt, explainPrompt, postFindingsPrompt, explainChangesPrompt, startIssuePrompt, statusUpdatePrompt, summarizeRepliesPrompt, messagePrompt, monitorPrompt, openChatPrompt, postPrompt, reviewPrompt, shipPrompt, summarizeAllPrompt, summarizePrompt, syncPrompt } from "./prompts.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO_RE = /^[\w.-]+\/[\w.-]+$/;
const AUTO_REFRESH_MS = 120_000;
const MIN_REFRESH_GAP_MS = 15_000;
const MIN_FORCED_GAP_MS = 5_000;
const TOKEN = randomBytes(16).toString("hex");

let session;

// ---- Boards (one per repo, shared by every canvas instance) ---------------

// An issue with an open, draft or merged PR on the board is "covered": it has no row of its own. The PR is the
// item, with an "Fixes Issue #n" chip, and it carries the issue's thread, explainer, next step and session.
function mergeCovered(cards, issues) {
    const onBoard = new Set(cards.map((c) => c.number));
    const byPr = new Map();
    for (const ic of issues) {
        ic.coveredBy = (ic.prs || []).filter((p) => p.state !== "closed" && onBoard.has(p.number)).map((p) => p.number);
        ic.covered = ic.coveredBy.length > 0 || (ic.state !== "OPEN" && ic.pr?.state === "merged");
        for (const n of ic.coveredBy) (byPr.get(n) || byPr.set(n, []).get(n)).push(ic);
    }
    const merged = cards.map((c) => {
        const iss = byPr.get(c.number);
        if (!iss) return c;
        const l = { ...(c.local || {}) };
        const extra = iss.flatMap((i) => (i.local?.thread || []).map((m) => ({ ...m, issue: i.number })));
        if (extra.length) l.thread = [...(l.thread || []), ...extra].sort((a, b) => String(a.at).localeCompare(String(b.at)));
        const first = (k) => iss.map((i) => i.local?.[k]).find(Boolean);
        if (!l.explainer && first("explainer")) l.explainer = { ...first("explainer"), fromIssue: iss.find((i) => i.local?.explainer).number };
        for (const k of ["next", "session", "pending", "explainerPending"]) if (!l[k] && first(k)) l[k] = first(k);
        if (!l.sync && first("sync")) l.sync = first("sync");
        // Replies you already read on the issue stay read on the PR.
        const seen = [l.lastSeenAt, ...iss.map((i) => i.local?.lastSeenAt)].filter(Boolean).sort().at(-1);
        if (seen) l.lastSeenAt = seen;
        return { ...c, issues: iss.map((i) => ({ number: i.number, title: i.title, url: i.url, state: i.state })), local: l };
    });
    return { cards: merged, issues };
}

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
        this.ready = new Map(); // PR number -> { key, at, raw }: GitHub data for the readiness gates
        this.readyInflight = null;
        // Replies older than this never count as unread, so a new install doesn't flag every old reply.
        if (!readState(repo).meta.readBaseline) updateMeta(repo, (m) => { m.readBaseline ??= new Date().toISOString(); });
    }

    async refresh({ force = false } = {}) {
        if (this.inflight) return this.inflight;
        const gap = Date.now() - this.fetchedAt;
        if (this.data && gap < (force ? MIN_FORCED_GAP_MS : MIN_REFRESH_GAP_MS)) return;
        this.inflight = (async () => {
            try {
                this.data = await fetchRepoData(this.repo);
                const st = readState(this.repo);
                // Only issues someone is working on here (a linked session); assigned issues come from GitHub.
                const linked = Object.entries(st.issues).filter(([, v]) => v.session?.id).map(([k]) => Number(k));
                const prIndex = new Map([...this.data.open.map((p) => [p.number, { number: p.number, state: "OPEN", isDraft: !!p.isDraft, url: p.url }]), ...this.data.merged.map((p) => [p.number, { number: p.number, state: "MERGED", isDraft: false, url: p.url }])]);
                this.issues = await fetchIssues(this.repo, { linked, prIndex, localIssues: st.issues });
                this.error = null;
                await refreshLiveSessions();
                this.refreshReadiness();
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

    // PRs you're responsible for getting ready: you wrote them, or a session here works on them as the author.
    lifecycleOf(p, st) {
        return p.author?.login === this.data?.viewer || st.prs[String(p.number)]?.role === "author";
    }

    // Read the readiness gates' GitHub data in the background. Re-read when the PR changes, or every two minutes
    // while a gate isn't green (CI finishing doesn't change the PR's updatedAt).
    refreshReadiness({ only = null, force = false } = {}) {
        if (this.readyInflight || !this.data) return this.readyInflight;
        const st = readState(this.repo);
        const due = this.data.open.filter((p) => (only ? p.number === only : this.lifecycleOf(p, st))).filter((p) => {
            const h = this.ready.get(p.number);
            if (force || !h || h.key !== `${p.headRefOid}|${p.updatedAt}`) return true;
            return !computeReadiness(h.raw, st.prs[String(p.number)] || {})?.allGreen && Date.now() - h.at > 2 * 60_000;
        });
        if (!due.length) return null;
        this.readyInflight = (async () => {
            for (let i = 0; i < due.length; i += 3) {
                await Promise.all(due.slice(i, i + 3).map((p) => fetchReadinessRaw(this.repo, p.number)
                    .then((raw) => this.ready.set(p.number, { key: `${p.headRefOid}|${p.updatedAt}`, at: Date.now(), raw }))
                    .catch((e) => this.ready.set(p.number, { key: null, at: Date.now(), raw: this.ready.get(p.number)?.raw || null, error: e.message }))));
            }
        })().finally(() => { this.readyInflight = null; this.broadcast(); });
        return this.readyInflight;
    }

    snapshot() {
        const state = readState(this.repo);
        const local = state.prs;
        return {
            repo: this.repo,
            sync: { syncedAt: state.meta.syncedAt || null, requestedAt: state.meta.syncRequestedAt || null },
            readBaseline: state.meta.readBaseline || null,
            liveSessions: liveSessions.list,
            liveSessionsNote: liveSessions.note,
            groups: GROUPS,
            viewer: this.data?.viewer || null,
            ...mergeCovered(this.data ? buildCards(this.data, local).map((card) => {
                const out = card.local?.review ? { ...card, local: { ...card.local, review: withLinks(card.local.review, card.url) } } : card;
                if (card.section === "merged") return out;
                const raw = this.data.open.find((p) => p.number === card.number);
                if (!raw || !this.lifecycleOf(raw, state)) return out;
                const r = this.ready.get(card.number);
                // Issues the board links to this PR, so the description check can suggest the right "Fixes #".
                const linkedIssues = Object.entries(state.issues).filter(([, v]) => v.linkedPr === card.number || (v.branch && v.branch === r?.raw?.headRefName)).map(([k]) => Number(k));
                return { ...out, lifecycle: true, readiness: r?.raw ? computeReadiness(r.raw, card.local || {}, { linkedIssues }) : null, readinessError: r?.error || null };
            }) : [], buildIssueCards(this.issues || [], state.issues)),
            streams: streamer.forRepo(this.repo),
            error: this.error,
            fetchedAt: this.fetchedAt ? new Date(this.fetchedAt).toISOString() : null,
            loading: !!this.inflight,
        };
    }

    issueCard(number) {
        return this.snapshot().issues.find((c) => c.number === number) || null;
    }

    item(kind, number) {
        return kind === "issue" ? this.issueCard(number) : this.card(number);
    }

    card(number) {
        return this.snapshot().cards.find((c) => c.number === number) || null;
    }

    broadcast() {
        if (!this.clients.size) return;
        const payload = `event: board\ndata: ${JSON.stringify(this.snapshot())}\n\n`;
        for (const res of this.clients) res.write(payload);
    }

    sendStream(update) {
        const payload = `event: stream\ndata: ${JSON.stringify(update)}\n\n`;
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
let knownRepo = null;
const streamer = createStreamer({
    defaultRepo: () => knownRepo,
    isPending: (repo, kind, number) => {
        const st = readState(repo);
        return !!(kind === "issue" ? st.issues : st.prs)[String(number)]?.pending;
    },
    onUpdate: (repo, update) => {
        for (const [r, b] of boards) if (r.toLowerCase() === repo.toLowerCase()) b.sendStream(update);
    },
});
function board(repo) {
    if (!boards.has(repo)) boards.set(repo, new Board(repo));
    return boards.get(repo);
}

let defaultRepoPromise = null;
function defaultRepo() {
    defaultRepoPromise ??= detectRepo(process.cwd()).then((r) => (knownRepo = r));
    return defaultRepoPromise;
}

// Without repo, use the current folder's repo. A coordinator often runs outside a checkout, so fall back to
// the repo of an open board, then to the only repo with saved board state.
async function resolveRepo(repo) {
    let r = repo || (await defaultRepo());
    if (!r) r = [...new Set(instances.values())][0] || [...boards.keys()][0] || null;
    if (!r) {
        const saved = (() => { try { return readdirSync(artifactsDir()).filter((f) => /^[\w.-]+__[\w.-]+\.json$/.test(f)); } catch { return []; } })();
        if (saved.length === 1) r = saved[0].replace(/\.json$/, "").replace("__", "/");
    }
    if (!r || !REPO_RE.test(r)) throw new Error("Couldn't tell which GitHub repo to use. Pass repo as \"owner/name\".");
    return r;
}

// ---- Card actions from the UI ----------------------------------------------

const POST_EVENTS = new Set(["approve", "request_changes", "comment"]);
const nowIso = () => new Date().toISOString();

// The user acted on an item, so an explicit "your move" from the agent is done, and any snooze ends.
function userActed(s) {
    if (s.next?.owner === "you") s.next = null;
    s.snoozeUntil = null;
}
const OWNERS = ["you", "agent", "author", "reviewer", "reporter", "ci"];

// Done / Snooze / a choice from the board's "Your move" box. Shared by PRs and issues.
async function runBallAction({ repo, kind, number, action, extra, card }) {
    const upd = (fn) => (kind === "issue" ? updateItem(repo, "issue", number, fn) : updatePr(repo, number, fn));
    if (action === "ball-done") {
        upd((s) => { if (s.next?.owner === "you") s.next = null; s.ballDone = { key: String(extra.key || "").slice(0, 400), at: nowIso() }; s.snoozeUntil = null; addHistory(s, "You marked your move done"); });
    } else if (action === "ball-snooze") {
        const until = new Date(String(extra.until || ""));
        if (Number.isNaN(until.getTime()) || until.getTime() <= Date.now()) throw httpError(400, "Pick a time in the future.");
        upd((s) => { s.snoozeUntil = until.toISOString(); addHistory(s, `You snoozed it until ${until.toLocaleString()}`); });
    } else if (action === "ball-unsnooze") {
        upd((s) => { s.snoozeUntil = null; });
    } else if (action === "choose") {
        const choice = String(extra.choice || "").trim().slice(0, 300);
        if (!choice) throw httpError(400, "Pick a choice.");
        const next = (readState(repo)[kind === "issue" ? "issues" : "prs"][String(number)] || {}).next || null;
        upd((s) => { s.next = null; s.snoozeUntil = null; s.pending = { id: "choice", at: nowIso() }; addMessage(s, "user", `${next?.action ? next.action + ": " : ""}${choice}`); });
        await send(choosePrompt(card, repo, next, choice), repo, number, { quiet: true, kind });
    } else if (action === "open-pr") {
        upd((s) => { userActed(s); s.pending = { id: "open-pr", at: nowIso() }; note(s, "You asked the agent to open a PR"); });
        await send(openPrPrompt(card, repo), repo, number, { quiet: true, kind });
    } else if (action === "fix") {
        upd((s) => { userActed(s); s.pending = { id: "fix", at: nowIso() }; note(s, "You asked the agent to fix what's blocking it"); });
        await send(fixPrompt(card, repo, String(extra.what || "").slice(0, 300)), repo, number, { quiet: true, kind });
    } else return null;
    board(repo).broadcast();
    return { ok: true };
}
const BALL_ACTIONS = new Set(["ball-done", "ball-snooze", "ball-unsnooze", "choose", "open-pr", "fix"]);
// Actions that are the user acting on the item (they hand the ball to the agent or GitHub).
const ACTING = new Set(["get-ready", "request-reviewer", "fix-finding", "fix-description", "review", "post", "post-findings", "ship", "answer", "monitor", "status-update", "summarize-replies", "explain-changes", "start-issue"]);

// Record an event in both the timeline and the thread, so the thread reads as one story.
function note(s, text) {
    addHistory(s, text);
    addMessage(s, "system", text);
}

async function runIssueAction({ repo, number, action, extra = {} }) {
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.issueCard(number);
    if (!card) throw httpError(404, `Issue #${number} isn't on the board. Press Refresh.`);
    const quiet = { quiet: true, kind: "issue" };
    if (BALL_ACTIONS.has(action)) return runBallAction({ repo, kind: "issue", number, action, extra, card });
    if (ACTING.has(action)) updateItem(repo, "issue", number, userActed);
    if (BRANCH_ACTIONS.has(action)) return runBranchAction({ repo, number, action, extra, card });
    if (action === "explain-item") {
        updateItem(repo, "issue", number, (s) => { s.explainerPending = nowIso(); });
        await send(explainerPrompt(card, repo), repo, number, quiet);
    } else if (action === "status-update") {
        updateItem(repo, "issue", number, (s) => { s.pending = { id: "status", at: nowIso() }; note(s, "You asked for a status update"); });
        await send(statusUpdatePrompt(card, repo), repo, number, quiet);
    } else if (action === "start-issue") {
        if (card.local?.session?.id) throw httpError(409, "A session is already linked to this issue.");
        updateItem(repo, "issue", number, (s) => { s.pending = { id: "start", at: nowIso() }; note(s, "You asked to start working on it"); });
        await send(startIssuePrompt(card, repo), repo, number, quiet);
    } else if (action === "hide" || action === "untrack") {
        updateItem(repo, "issue", number, (s) => { s.hidden = true; addHistory(s, "You removed it from the board"); });
    } else if (action === "unhide") {
        updateItem(repo, "issue", number, (s) => { s.hidden = false; addHistory(s, "You put it back on the board"); });
    } else {
        throw httpError(400, `Unknown action ${action}`);
    }
    b.broadcast();
    return { ok: true };
}

// Private comments on an issue's branch before a PR exists. Stored on the board only; never posted to GitHub.
const BRANCH_ACTIONS = new Set(["br-add", "br-delete", "br-resolve", "br-reviewed", "br-send", "br-open-pr"]);
const clip = (v, n) => String(v ?? "").slice(0, n);
async function runBranchAction({ repo, number, action, extra, card }) {
    const upd = (fn) => updateItem(repo, "issue", number, (s) => { s.branchReview ??= { comments: [] }; fn(s.branchReview, s); });
    const sha = /^[0-9a-f]{7,40}$/i.test(String(extra.head || "")) ? String(extra.head) : null;
    if (action === "br-add") {
        const c = extra.comment || {};
        const text = clip(c.text, 4000).trim();
        if (!text) throw httpError(400, "Write a comment first.");
        if (!sha) throw httpError(400, "Load the branch first.");
        const file = c.file ? clip(c.file, 400) : null;
        const start = Number(c.start), end = Number(c.end);
        if (file && !(Number.isInteger(start) && Number.isInteger(end) && start > 0 && end >= start)) throw httpError(400, "Pick a line.");
        upd((r, s) => {
            r.comments.push({ id: Math.random().toString(16).slice(2, 10), file, side: c.side === "old" ? "old" : "new", start: file ? start : null, end: file ? end : null, excerpt: clip(c.excerpt, 3000), text, sha, status: "pending", at: nowIso() });
            s.ballDone = null;
        });
    } else if (action === "br-delete" || action === "br-resolve") {
        upd((r) => {
            const c = r.comments.find((x) => x.id === extra.id);
            if (!c) throw httpError(404, "That comment is gone.");
            if (action === "br-delete") r.comments = r.comments.filter((x) => x !== c);
            else c.status = "resolved";
        });
    } else if (action === "br-reviewed") {
        if (!sha) throw httpError(400, "Load the branch first.");
        upd((r, s) => { r.reviewedHead = sha; s.snoozeUntil = null; addHistory(s, `You reviewed the branch at ${sha.slice(0, 7)}`); });
    } else if (action === "br-send") {
        const pending = (card.local?.branchReview?.comments || []).filter((c) => c.status === "pending");
        if (!pending.length) throw httpError(400, "There are no unsent comments.");
        if (!card.local?.session?.id) throw httpError(409, "No session is linked to this issue, so there's nobody to send them to.");
        const ids = new Set(pending.map((c) => c.id));
        upd((r, s) => {
            for (const c of r.comments) if (ids.has(c.id)) { c.status = "sent"; c.sentAt = nowIso(); }
            r.sentHead = sha || r.sentHead || null;
            r.sentAt = nowIso();
            userActed(s);
            s.pending = { id: "br-send", at: nowIso() };
            note(s, `You sent ${pending.length} branch comment${pending.length === 1 ? "" : "s"} to the agent`);
        });
        await send(branchCommentsPrompt(card, repo, { branch: card.local.branch, head: sha, comments: pending }), repo, number, { quiet: true, kind: "issue" });
    } else if (action === "br-open-pr") {
        const draft = extra.draft !== false;
        upd((r, s) => { userActed(s); s.pending = { id: "open-pr", at: nowIso() }; note(s, `You asked the agent to open a ${draft ? "draft " : ""}PR`); });
        await send(openBranchPrPrompt(card, repo, { draft, branch: card.local?.branch }), repo, number, { quiet: true, kind: "issue" });
    }
    board(repo).broadcast();
    return { ok: true };
}

// Exactly what will be posted, saved as gh api request bodies the agent posts unchanged.
function writePostPayload(repo, pr, preview) {
    const dir = join(artifactsDir(), "posts");
    mkdir(dir, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    const base = join(dir, `${repo.replace("/", "__")}-${pr}-${stamp}`);
    const strip = ({ findingId, ...c }) => c;
    const full = { event: preview.event, body: preview.body, comments: preview.comments.map(strip), ...(preview.commit_id ? { commit_id: preview.commit_id } : {}) };
    const folded = preview.comments.map((x) => `- \`${x.path}:${x.line}\`: ${x.body.replace(/\n\n/g, "\n  ")}`).join("\n");
    const bodyOnly = { event: preview.event, body: [preview.body, folded].filter(Boolean).join("\n\n"), ...(preview.commit_id ? { commit_id: preview.commit_id } : {}) };
    writeFile(`${base}.json`, JSON.stringify(full, null, 2));
    writeFile(`${base}-body-only.json`, JSON.stringify(bodyOnly, null, 2));
    return { full: `${base}.json`, bodyOnly: `${base}-body-only.json` };
}

async function runCardAction({ repo, pr, action, event, force, extra = {} }) {
    if (extra.kind === "issue") return runIssueAction({ repo, number: pr, action, extra });
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.card(pr);
    if (!card) throw httpError(404, `PR #${pr} isn't on the board. Press Refresh.`);
    const local = card.local || {};
    const busy = local.agent?.status === "running";
    if (BALL_ACTIONS.has(action)) return runBallAction({ repo, kind: "pr", number: pr, action, extra, card });
    if (ACTING.has(action)) updatePr(repo, pr, userActed);

    if (action === "review") {
        if (busy && !force) throw httpError(409, "An agent is already working on this PR. Use 'Reset agent status' if it's stuck.");
        updatePr(repo, pr, (s) => {
            s.agent = { status: "running", kind: "review", outcome: "Queued: asking the agent to start a review", updatedAt: nowIso() };
            s.draft = null;
            if (card.section !== "yours" && !s.tracked) s.tracked = "reviewer";
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
    } else if (action === "explain") {
        const f = (local.review?.findings || []).find((x) => x.id === extra.findingId);
        if (!f) throw httpError(404, "That finding isn't in the review anymore.");
        updatePr(repo, pr, (s) => { s.explaining = { ...(s.explaining || {}), [f.id]: nowIso() }; });
        await send(explainPrompt(card, repo, f), repo, pr, { quiet: true });
    } else if (action === "post-findings") {
        if (!POST_EVENTS.has(event)) throw httpError(400, "Pick Approve, Request changes, or Comment.");
        if (!local.review) throw httpError(409, "There's no structured review yet. Run Review first.");
        if (busy && !force) throw httpError(409, "An agent is already working on this PR.");
        const preview = composeGithubReview(local.review, local.triage, event, card.headSha);
        // GitHub accepts an approval with an empty body; a comment or change request needs something to say.
        if (event !== "approve" && !preview.counts.posted && !preview.body) throw httpError(409, "To comment or request changes, mark at least one finding to post, or write a note.");
        const files = writePostPayload(repo, pr, preview);
        const word = event.replace("_", " ");
        updatePr(repo, pr, (s) => {
            s.postEvent = event;
            s.agent = { status: "running", kind: "post", outcome: `Posting ${preview.counts.posted} finding(s) as "${word}"`, updatedAt: nowIso() };
            note(s, `You asked to post ${preview.counts.posted} finding(s) as "${word}"`);
        });
        await send(postFindingsPrompt(card, repo, event, preview, files), repo, pr);
    } else if (action === "explain-item") {
        updatePr(repo, pr, (s) => { s.explainerPending = nowIso(); });
        await send(explainerPrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "status-update") {
        updatePr(repo, pr, (s) => { s.pending = { id: "status", at: nowIso() }; note(s, "You asked for a status update"); });
        await send(statusUpdatePrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "summarize-replies") {
        updatePr(repo, pr, (s) => { s.pending = { id: "replies", at: nowIso() }; note(s, "You asked for a summary of the author's replies"); });
        await send(summarizeRepliesPrompt(card, repo), repo, pr, { quiet: true });
    } else if (action === "explain-changes") {
        const changes = await fetchChangesSince(repo, pr, b.data.viewer, local.prReview?.headSha).catch((e) => { throw httpError(502, e.message); });
        if (!changes.base || changes.base === changes.head) throw httpError(409, changes.note);
        updatePr(repo, pr, (s) => { s.pending = { id: "changes", at: nowIso() }; note(s, "You asked what changed since your review"); });
        await send(explainChangesPrompt(card, repo, changes), repo, pr, { quiet: true });
    } else if (action === "track") {
        updatePr(repo, pr, (s) => { s.tracked = "reviewer"; addHistory(s, "You started tracking it under Reviewing"); });
    } else if (action === "untrack") {
        updatePr(repo, pr, (s) => { s.tracked = null; addHistory(s, "You stopped tracking it"); });
    } else if (action === "hide" || action === "unhide") {
        updatePr(repo, pr, (s) => { s.hidden = action === "hide"; addHistory(s, action === "hide" ? "You removed it from the board" : "You put it back on the board"); });
    } else if (action === "get-ready") {
        updatePr(repo, pr, (s) => { s.pending = { id: "get-ready", at: nowIso() }; note(s, "You asked the agent to get it ready for review"); });
        await send(readyForReviewPrompt(card, repo, card.readiness), repo, pr, { quiet: true });
    } else if (action === "request-reviewer") {
        const who = String(extra.who || "*").replace(/[^\w.*-]/g, "").slice(0, 60) || "*";
        updatePr(repo, pr, (s) => { s.pending = { id: "request-reviewer", at: nowIso() }; note(s, who === "*" ? "You asked the agent to request a reviewer" : `You asked the agent to request ${who} as reviewer`); });
        await send(requestReviewerPrompt(card, repo, who), repo, pr, { quiet: true });
    } else if (action === "fix-finding") {
        const f = (local.review?.findings || []).find((x) => x.id === extra.findingId);
        if (!f) throw httpError(404, "That finding isn't in the current review.");
        updatePr(repo, pr, (s) => { s.triage = s.triage || {}; s.triage[f.id] = { ...(s.triage[f.id] || {}), fixRequestedAt: nowIso(), at: nowIso() }; s.pending = { id: "fix-finding", at: nowIso() }; note(s, `You asked the agent to fix: ${f.title}`); });
        await send(fixFindingPrompt(card, repo, f), repo, pr, { quiet: true });
    } else if (action === "fix-description") {
        const issue = Number(extra.issue);
        if (!Number.isInteger(issue) || issue <= 0) throw httpError(400, "Which issue?");
        updatePr(repo, pr, (s) => { s.pending = { id: "fix-description", at: nowIso() }; note(s, `You asked the agent to add "Fixes #${issue}" to the description`); });
        await send(fixDescriptionPrompt(card, repo, issue), repo, pr, { quiet: true });
    } else if (action === "readiness-refresh") {
        await board(repo).refreshReadiness({ only: pr, force: true });
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

// The GitHub tab: the PR or issue as GitHub shows it. Cached briefly, because image links in
// GitHub's rendered HTML are signed and expire after a few minutes.
const ghViewCache = new Map();
async function githubView(repo, kind, n, force) {
    const key = `${repo}#${kind}${n}`;
    const hit = ghViewCache.get(key);
    if (hit?.inflight) return hit.inflight;
    if (hit?.data && !force && Date.now() - hit.at < 4 * 60_000) return hit.data;
    const inflight = fetchGitHubView(repo, kind, n).then((data) => {
        ghViewCache.set(key, { data, at: Date.now() });
        if (ghViewCache.size > 40) ghViewCache.delete(ghViewCache.keys().next().value);
        return data;
    }).catch((e) => {
        ghViewCache.delete(key);
        throw httpError(502, e.message);
    });
    ghViewCache.set(key, { ...(hit || {}), inflight });
    return inflight;
}

// Private branch review: the diff of an issue's branch that has no PR yet. Cached briefly.
const branchCache = new Map();
async function branchView(repo, issue, force) {
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.issueCard(issue);
    const branch = card?.local?.branch;
    if (!branch) throw httpError(404, `Issue #${issue} has no branch linked on the board.`);
    const shas = (card.local.branchReview?.comments || []).map((c) => c.sha);
    const key = `${repo}#${issue}:${branch}:${[...new Set(shas)].sort().join(",")}`;
    const hit = branchCache.get(key);
    if (hit?.inflight) return hit.inflight;
    if (hit?.data && !force && Date.now() - hit.at < 60_000) return hit.data;
    const inflight = fetchBranchCompare(repo, b.data.defaultBranch || "main", branch, shas).then((data) => {
        branchCache.set(key, { data, at: Date.now() });
        if (branchCache.size > 20) branchCache.delete(branchCache.keys().next().value);
        return data;
    }).catch((e) => {
        branchCache.delete(key);
        throw httpError(502, e.message);
    });
    branchCache.set(key, { ...(hit || {}), inflight });
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

async function sendMessage({ repo, pr, text, kind = "pr" }) {
    text = String(text || "").trim();
    if (!text) throw httpError(400, "Type a message first.");
    if (text.length > MAX_MESSAGE) throw httpError(413, `Keep messages under ${MAX_MESSAGE} characters.`);
    const b = board(repo);
    if (!b.data) await b.refresh();
    const card = b.item(kind, pr);
    if (!card) throw httpError(404, `#${pr} isn't on the board. Press Refresh.`);
    const recent = (card.local?.thread || []).filter((m) => m.role !== "system").slice(-6);
    let msg;
    updateItem(repo, kind, pr, (s) => {
        msg = addMessage(s, "user", text);
        s.pending = { id: msg.id, at: msg.at };
        userActed(s);
    });
    b.broadcast();
    try {
        streamer.expect(repo, kind, pr);
        await session.send({ prompt: messagePrompt(card, repo, msg, recent) });
    } catch (e) {
        updateItem(repo, kind, pr, (s) => {
            s.pending = null;
            addMessage(s, "system", `Couldn't reach the agent: ${e.message}`);
        });
        b.broadcast();
        throw httpError(502, `Couldn't send to the agent: ${e.message}`);
    }
    return { ok: true, id: msg.id };
}

async function send(prompt, repo, pr, { quiet = false, kind = "pr" } = {}) {
    try {
        if (readState(repo)[kind === "issue" ? "issues" : "prs"][String(pr)]?.pending) streamer.expect(repo, kind, pr);
        await session.send({ prompt });
    } catch (e) {
        updateItem(repo, kind, pr, (s) => {
            s.pending = null;
            if (!quiet) s.agent = { status: "failed", kind: s.agent?.kind, outcome: `Couldn't reach the agent: ${e.message}`, updatedAt: nowIso() };
            note(s, "Couldn't send the request to the agent");
        });
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
    if (req.method === "GET" && url.pathname === "/diagram.js") {
        res.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8", "Cache-Control": "no-store" });
        return res.end(readFileSync(join(HERE, "diagram.js"), "utf8"));
    }
    if (req.method === "GET" && url.pathname === "/api/review") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const pr = Number(url.searchParams.get("pr"));
        const local = readState(repo).prs[String(pr)] || {};
        if (!local.review) return json(200, { review: null });
        return json(200, { review: withLinks(local.review, `https://github.com/${repo}/pull/${pr}`), triage: local.triage || {}, explanations: local.explanations || {}, explaining: local.explaining || {}, reviewedAt: local.reviewSetAt || null });
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
    if (req.method === "GET" && url.pathname === "/api/changes") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const pr = Number(url.searchParams.get("pr"));
        if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
        const b = board(repo);
        if (!b.data) await b.refresh();
        const local = readState(repo).prs[String(pr)] || {};
        try { return json(200, await fetchChangesSince(repo, pr, b.data.viewer, local.prReview?.headSha)); }
        catch (e) { throw httpError(502, e.message); }
    }
    if (req.method === "GET" && url.pathname === "/api/detail") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const pr = Number(url.searchParams.get("pr"));
        if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
        return json(200, await prDetail(repo, pr));
    }
    if (req.method === "GET" && url.pathname === "/api/branch") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const n = Number(url.searchParams.get("issue"));
        if (!Number.isInteger(n) || n <= 0) throw httpError(400, "Invalid issue number");
        return json(200, await branchView(repo, n, url.searchParams.get("force") === "1"));
    }
    if (req.method === "GET" && url.pathname === "/api/github") {
        const repo = checkRepo(url.searchParams.get("repo"));
        const n = Number(url.searchParams.get("n"));
        const kind = url.searchParams.get("kind") === "issue" ? "issue" : "pr";
        if (!Number.isInteger(n) || n <= 0) throw httpError(400, "Invalid number");
        return json(200, await githubView(repo, kind, n, url.searchParams.get("force") === "1"));
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
            return json(200, await sendMessage({ repo, pr, text: body.text, kind: body.kind === "issue" ? "issue" : "pr" }));
        }
        if (url.pathname === "/api/seen") {
            if (body.all) {
                updateMeta(repo, (m) => { m.readBaseline = nowIso(); });
            } else {
                const pr = Number(body.pr);
                if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid number");
                updateItem(repo, body.kind === "issue" ? "issue" : "pr", pr, (s) => { s.lastSeenAt = nowIso(); });
            }
            board(repo).broadcast();
            return json(200, { ok: true });
        }
        if (url.pathname === "/api/action") {
            const pr = Number(body.pr);
            if (!Number.isInteger(pr) || pr <= 0) throw httpError(400, "Invalid PR number");
            return json(200, await runCardAction({ repo, pr, action: String(body.action), event: body.event, force: !!body.force, extra: { enable: body.enable, mergePr: !!body.mergePr, answer: body.answer, findingId: body.findingId, key: body.key, until: body.until, choice: body.choice, what: body.what, comment: body.comment, id: body.id, draft: body.draft, head: body.head, who: body.who, issue: body.issue, kind: body.kind === "issue" ? "issue" : "pr" } }));
        }
        if (url.pathname === "/api/triage") {
            const pr = Number(body.pr);
            const id = String(body.findingId || "");
            if (!Number.isInteger(pr) || pr <= 0 || !id) throw httpError(400, "Invalid PR or finding");
            const decision = body.decision === undefined ? undefined : ["post", "ignore", "undecided"].includes(body.decision) ? body.decision : null;
            if (decision === null) throw httpError(400, "decision must be post, ignore or undecided");
            updatePr(repo, pr, (s) => {
                s.triage = s.triage || {};
                const cur = s.triage[id] || {};
                s.triage[id] = { ...cur, ...(decision !== undefined ? { decision } : {}), ...(body.note !== undefined ? { note: String(body.note).slice(0, 2000) } : {}), at: nowIso() };
            });
            board(repo).broadcast();
            return json(200, { ok: true });
        }
        if (url.pathname === "/api/review-preview") {
            const pr = Number(body.pr);
            const local = readState(repo).prs[String(pr)] || {};
            if (!local.review) throw httpError(404, "No structured review yet.");
            if (!POST_EVENTS.has(body.event)) throw httpError(400, "Pick Approve, Request changes, or Comment.");
            return json(200, composeGithubReview(local.review, local.triage, body.event, board(repo).card(pr)?.headSha));
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

// Issues the board hasn't fetched yet need a GitHub read before they can show.
function refreshIfNewIssues(repo, updated) {
    const b = board(repo);
    const known = new Set((b.issues || []).map((i) => i.number));
    const fresh = updated.filter((u) => typeof u === "string").map((u) => Number(u.replace(/\D/g, ""))).filter((n) => !known.has(n));
    if (fresh.length) { b.fetchedAt = 0; b.refresh(); }
}

// A tool call names either a PR (pr) or an issue (issue).
function target(args) {
    const issue = Number(args.issue), pr = Number(args.pr);
    if (Number.isInteger(issue) && issue > 0) return ["issue", issue];
    if (Number.isInteger(pr) && pr > 0) return ["pr", pr];
    throw new Error("Pass pr (a PR number) or issue (an issue number).");
}

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

function loadReviewArg(args) {
    if (args.review && typeof args.review === "object") return structuredClone(args.review);
    if (args.path) {
        if (!isAbsolute(args.path) || !existsSync(args.path)) throw new Error(`Review file not found: ${args.path}`);
        return JSON.parse(readFileSync(args.path, "utf8"));
    }
    throw new Error("Pass review (an object) or path (an absolute path to a JSON file).");
}

function storeReview(repo, pr, review, { replace }) {
    let warnings = [];
    const head = board(repo).card(pr)?.headSha || null;
    updatePr(repo, pr, (s) => {
        const next = replace ? review : mergeReview(s.review || {}, review);
        warnings = normalizeReview(next);
        next.headSha = next.headSha || head;
        s.review = next;
        s.reviewSetAt = nowIso();
        s.prReview = { ...(s.prReview || {}), result: s.prReview?.result === "posted" ? "posted" : "drafted", at: nowIso(), draftedAt: nowIso(), headSha: next.headSha };
        if (replace) note(s, `Structured review ready: ${next.findings.length} finding(s), ${next.diagrams.length} diagram(s)`);
        if (next.summary && !s.summary) s.summary = { text: next.summary.replace(/\s+/g, " ").replace(/[*_`#>]/g, "").slice(0, 139), nextSteps: [], risk: next.risk || null, at: nowIso() };
        if (s.agent?.kind === "review" && s.agent.status === "running") s.agent = { status: "draft_ready", kind: "review", outcome: `Review ready: ${next.findings.length} finding(s) to triage`, updatedAt: nowIso() };
    });
    board(repo).broadcast();
    return warnings;
}

// The review tools (review_guide, set_review, update_review, add_findings) are adapted from angelser's
// pr-arch-review, https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe
const tools = [
    {
        name: "pr_board_review_guide",
        description: "Read first before filling a PR board structured review: the workflow, plain-words rules, diagram conventions and the review schema used by pr_board_set_review.",
        parameters: { type: "object", properties: {} },
        handler: async () => ({ guide: GUIDE, schema: REVIEW_SCHEMA }),
    },
    {
        name: "pr_board_set_review",
        description: "Set (replace) the structured review for a PR on the PR board: summary, verdict, risk, functionality, components (changes by system), changedFiles (changes by file), diagrams and findings. Pass review as an object, or path to a JSON file with the same shape. The user's Post/Ignore choices survive when finding ids stay the same. Call pr_board_review_guide first.",
        parameters: { type: "object", properties: { repo: repoProp, pr: prProp, review: REVIEW_SCHEMA, path: { type: "string", description: "Absolute path to a JSON file holding the review, instead of review." } }, required: ["pr"] },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const warnings = storeReview(repo, Number(args.pr), loadReviewArg(args), { replace: true });
            return { ok: true, repo, pr: args.pr, warnings };
        },
    },
    {
        name: "pr_board_update_review",
        description: "Merge a partial structured review into a PR's review on the PR board (objects merge, id'd arrays merge by id). Use for big PRs, section by section.",
        parameters: { type: "object", properties: { repo: repoProp, pr: prProp, patch: REVIEW_SCHEMA }, required: ["pr", "patch"] },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const warnings = storeReview(repo, Number(args.pr), structuredClone(args.patch || {}), { replace: false });
            return { ok: true, repo, pr: args.pr, warnings };
        },
    },
    {
        name: "pr_board_add_findings",
        description: "Add or update findings in a PR's structured review on the PR board (merged by id).",
        parameters: { type: "object", properties: { repo: repoProp, pr: prProp, findings: REVIEW_SCHEMA.properties.findings }, required: ["pr", "findings"] },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const warnings = storeReview(repo, Number(args.pr), { findings: structuredClone(args.findings || []) }, { replace: false });
            return { ok: true, repo, pr: args.pr, warnings };
        },
    },
    {
        name: "pr_board_set_explanation",
        description: "Put a plain-words explanation under a finding on the PR board, after the board asked you to explain it.",
        parameters: { type: "object", properties: { repo: repoProp, pr: prProp, findingId: { type: "string" }, text: { type: "string", description: "Short Markdown." } }, required: ["pr", "findingId", "text"] },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            updatePr(repo, Number(args.pr), (s) => {
                s.explanations = { ...(s.explanations || {}), [args.findingId]: { text: String(args.text).slice(0, 6000), at: nowIso() } };
                if (s.explaining) delete s.explaining[args.findingId];
            });
            board(repo).broadcast();
            return { ok: true };
        },
    },
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
                            issue: { type: "number", description: "Issue number, for an issue with no PR yet. Use instead of pr." },
                            status: { type: "string", description: "One plain line about where it is, shown on the row, e.g. 'Waiting on the reporter to rerun the installed winapp'." },
                            untrack: { type: "boolean", description: "true = unlink this PR or issue from its session here and clear its synced state (an issue then leaves 'Issues in progress' unless it's assigned to the user)." },
                            branch: { type: "string", description: "For an issue: the linked session's git branch. The board finds the issue's PR from it." },
                            linkedPr: { type: "number", description: "For an issue: the PR that fixes it, if GitHub doesn't already link them." },
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
                let kind, n;
                try { [kind, n] = target(e); } catch { continue; }
                if (e.untrack) {
                    updateItem(repo, kind, n, (s) => { s.session = null; s.sync = null; s.status = null; s.tracked = false; s.role = null; addHistory(s, "Unlinked by the agent"); });
                    updated.push(kind === "issue" ? `issue #${n}` : n);
                    continue;
                }
                updateItem(repo, kind, n, (s) => {
                    if (e.status !== undefined) s.status = e.status ? String(e.status).slice(0, 200) : null;
                    if (e.sessionId) s.session = { id: String(e.sessionId), name: e.name ? String(e.name).slice(0, 120) : s.session?.name || null, linkedAt: s.session?.linkedAt || at };
                    if (e.role === "author" || e.role === "reviewer") s.role = e.role;
                    if (kind === "issue" && e.branch) s.branch = String(e.branch).slice(0, 200);
                    if (kind === "issue" && Number.isInteger(e.linkedPr) && e.linkedPr > 0) s.linkedPr = e.linkedPr;
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
                updated.push(kind === "issue" ? `issue #${n}` : n);
            }
            updateMeta(repo, (m) => { m.syncedAt = at; });
            refreshIfNewIssues(repo, updated);
            if ((Array.isArray(args.sessions) ? args.sessions : []).some((e) => e.branch || e.linkedPr)) { const b = board(repo); b.fetchedAt = 0; b.refresh(); }
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
        name: "pr_board_set_next_step",
        description: "Say whose move it is on a PR or issue, and what the move is. The board shows it as the row's state: owner 'you' puts it in the user's 'Your move' box (with choices as buttons, which come back to you as a prompt); 'agent' shows a spinner; author/reviewer/reporter/ci show who it's waiting on. Call it whenever your reply or work leaves the next step with someone, especially the user. It replaces the previous next step; clear: true removes it so the board derives the state from GitHub again.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                issue: { type: "number", description: "Issue number. Use instead of pr." },
                owner: { type: "string", enum: OWNERS, description: "Who has the ball." },
                action: { type: "string", description: "One plain line: the move, e.g. 'Pick A or B for the memory fix' or 'Approve the dependabot config PR'." },
                choices: { type: "array", items: { type: "string" }, description: "Optional buttons for the user (owner 'you'), e.g. ['A', 'B'] or ['Go', 'Not now']." },
                blocked: { type: "boolean", description: "true = it's blocked (CI red, conflicts, an error) and someone has to fix it." },
                clear: { type: "boolean", description: "true = remove the explicit next step." },
            },
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const [kind, n] = target(args);
            if (args.clear) {
                updateItem(repo, kind, n, (s) => { s.next = null; });
            } else {
                if (!OWNERS.includes(args.owner)) throw new Error(`owner must be one of: ${OWNERS.join(", ")}`);
                const action = String(args.action || "").replace(/\s+/g, " ").trim().slice(0, 240);
                if (!action) throw new Error("action is empty");
                const choices = (Array.isArray(args.choices) ? args.choices : []).map((x) => String(x).trim().slice(0, 60)).filter(Boolean).slice(0, 4);
                updateItem(repo, kind, n, (s) => {
                    s.next = { owner: args.owner, action, choices, blocked: !!args.blocked, at: nowIso() };
                    s.ballDone = null;
                    if (args.owner !== "you") s.snoozeUntil = null;
                    if (args.owner !== "agent") s.pending = null;
                    addHistory(s, `Next: ${args.owner} · ${action}`);
                });
            }
            board(repo).broadcast();
            return { ok: true, repo, [kind]: n };
        },
    },
    {
        name: "pr_board_set_explainer",
        description: "Set the plain-language explainer for a PR or issue on the PR board. eli5 = 'Explain like I'm 5': the problem or change with background and a concrete example, why it matters, and where it stands on GitHub. progress = 'Where we are': what we've done in our sessions, the current state, decisions made (with dates), and the next step with who takes it. Write it for a PM or a junior developer. Call it whenever your understanding or a decision changes.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                issue: { type: "number", description: "Issue number. Use instead of pr." },
                eli5: { type: "string", description: "Markdown, about 120-250 words." },
                progress: { type: "string", description: "Markdown, about 60-150 words. Dates for decisions; end with the next step and who takes it." },
            },
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const [kind, n] = target(args);
            const eli5 = String(args.eli5 || "").trim(), progress = String(args.progress || "").trim();
            if (!eli5 && !progress) throw new Error("Pass eli5, progress, or both.");
            const b = board(repo);
            const item = b.item(kind, n);
            updateItem(repo, kind, n, (s) => {
                s.explainer = {
                    eli5: eli5 ? eli5.slice(0, 6000) : s.explainer?.eli5 || "",
                    progress: progress ? progress.slice(0, 4000) : s.explainer?.progress || "",
                    updatedAt: nowIso(),
                    basis: { headSha: item?.headSha || null, ghUpdatedAt: item?.updatedAt || null },
                };
                s.explainerPending = null;
            });
            b.broadcast();
            return { ok: true, repo, [kind]: n };
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
                issue: { type: "number", description: "Issue number, for an issue with no PR yet. Use instead of pr." },
                text: { type: "string", description: "The reply, in Markdown. Short and self-contained." },
                from: { type: "string", description: "Who answered, e.g. \"Coordinator\" or the linked session's name." },
            },
            required: ["text"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            if (!String(args.text || "").trim()) throw new Error("text is empty");
            const [kind, n] = target(args);
            let msg;
            updateItem(repo, kind, n, (s) => {
                msg = addMessage(s, "agent", String(args.text).trim(), args.from || null);
                s.pending = null;
            });
            board(repo).broadcast();
            return { ok: true, repo, [kind]: n, id: msg.id };
        },
    },
    {
        name: "pr_board_link_session",
        description: "Link a Copilot session to a pull request (pr) or an issue (issue) on the PR board, so the board shows it and 'Open chat' can open it. Call right after creating or reusing a session from a PR board request.",
        parameters: {
            type: "object",
            properties: {
                repo: repoProp,
                pr: prProp,
                issue: { type: "number", description: "Issue number, for an issue with no PR yet. Use instead of pr." },
                sessionId: { type: "string", description: "The session's id (the id navigate_to / send_session_message accept)." },
                sessionName: { type: "string", description: "Short display name of the session." },
                branch: { type: "string", description: "For an issue: the session's git branch, so the board can find the issue's PR." },
            },
            required: ["sessionId"],
        },
        handler: async (args) => {
            const repo = await resolveRepo(args.repo);
            const name = args.sessionName ? String(args.sessionName).slice(0, 120) : null;
            const [kind, n] = target(args);
            const pr = updateItem(repo, kind, n, (s) => {
                if (kind === "issue") { s.tracked = true; s.pending = null; if (args.branch) s.branch = String(args.branch).slice(0, 200); }
                s.session = { id: String(args.sessionId), name, linkedAt: nowIso() };
                if (s.agent?.status === "running") s.agent.outcome = `Session started: ${name || args.sessionId}`;
                addHistory(s, `Linked session ${name || args.sessionId}`);
            });
            board(repo).broadcast();
            if (kind === "issue") refreshIfNewIssues(repo, [`issue #${n}`]);
            return { ok: true, repo, [kind]: n, session: pr.session };
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
                    userActed(s);
                } else {
                    s.agent = { status: args.status, kind: s.agent?.kind || null, outcome: outcome || s.agent?.outcome || null, updatedAt: nowIso() };
                }
                note(s, `${STATUS_WORDS[args.status]}${outcome ? `: ${outcome}` : ""}`);
            });
            board(repo).broadcast();
            // A posted review changes who's next on GitHub, so don't wait for the next poll.
            if (args.status === "posted") { const b = board(repo); b.fetchedAt = 0; b.refresh(); }
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

// The SDK reports a thrown error only as "Tool execution failed", so return the real message instead.
const readableTools = tools.map((tool) => ({
    ...tool,
    handler: async (...a) => {
        try {
            return await tool.handler(...a);
        } catch (e) {
            const message = e?.message || String(e);
            return { textResultForLlm: `Error: ${message}`, resultType: "failure", error: message };
        }
    },
}));

session = await joinSession({
    tools: readableTools,
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

// Mirror the agent's work on a board request into the thread's "replying" bubble (see stream.mjs).
session.on((event) => {
    try {
        streamer.handle(event);
    } catch {}
});
