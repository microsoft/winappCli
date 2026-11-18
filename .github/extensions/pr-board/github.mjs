// GitHub data for the PR board, read with the `gh` CLI.
import { execFile } from "node:child_process";

const PR_FIELDS = [
    "number", "title", "url", "author", "isDraft", "additions", "deletions", "changedFiles",
    "reviewDecision", "mergeStateStatus", "mergeable", "statusCheckRollup", "latestReviews",
    "reviewRequests", "labels", "updatedAt", "createdAt", "autoMergeRequest", "baseRefName",
].join(",");

export class GhError extends Error {
    constructor(code, message) {
        super(message);
        this.code = code;
    }
}

export function gh(args, { cwd, timeoutMs = 30000 } = {}) {
    return new Promise((resolve, reject) => {
        execFile("gh", args, { cwd, timeout: timeoutMs, maxBuffer: 32 * 1024 * 1024, windowsHide: true }, (err, stdout, stderr) => {
            if (!err) return resolve(stdout);
            if (err.code === "ENOENT") {
                return reject(new GhError("gh_missing", "The GitHub CLI (gh) isn't installed or isn't on PATH. Install it from https://cli.github.com, then run `gh auth login`."));
            }
            const text = String(stderr || err.message || "");
            if (/auth login|not logged|authentication|HTTP 401/i.test(text)) {
                return reject(new GhError("gh_auth", "The GitHub CLI isn't signed in. Run `gh auth login`, then press Refresh."));
            }
            reject(new GhError("gh_failed", text.trim().split(/\r?\n/).slice(-3).join(" ") || "gh failed"));
        });
    });
}

export async function detectRepo(cwd) {
    try {
        const out = await gh(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], { cwd, timeoutMs: 15000 });
        const repo = out.trim();
        if (/^[\w.-]+\/[\w.-]+$/.test(repo)) return repo;
    } catch {
        // Fall through: caller shows a clear "pass a repo" message.
    }
    return null;
}

let viewerCache = null;
export async function viewerLogin() {
    if (viewerCache) return viewerCache;
    viewerCache = (await gh(["api", "user", "--jq", ".login"])).trim();
    return viewerCache;
}

async function mergeQueue(repo, branch) {
    const [owner, name] = repo.split("/");
    const query = `query($owner:String!,$name:String!,$branch:String!){repository(owner:$owner,name:$name){mergeQueue(branch:$branch){entries(first:50){nodes{position state enqueuedAt pullRequest{number}}}}}}`;
    try {
        const out = await gh(["api", "graphql", "-f", `query=${query}`, "-F", `owner=${owner}`, "-F", `name=${name}`, "-F", `branch=${branch}`]);
        const nodes = JSON.parse(out)?.data?.repository?.mergeQueue?.entries?.nodes || [];
        return new Map(nodes.filter((n) => n?.pullRequest).map((n) => [n.pullRequest.number, { position: n.position, state: n.state, enqueuedAt: n.enqueuedAt }]));
    } catch {
        return new Map(); // Repos without a merge queue return null; treat as empty.
    }
}

export async function fetchRepoData(repo) {
    const [openJson, mergedJson, viewer, defaultBranch] = await Promise.all([
        gh(["pr", "list", "-R", repo, "--state", "open", "--limit", "60", "--json", PR_FIELDS]),
        gh(["pr", "list", "-R", repo, "--state", "merged", "--limit", "8", "--json", "number,title,url,author,mergedAt,createdAt,additions,deletions,changedFiles,latestReviews"]),
        viewerLogin(),
        gh(["repo", "view", repo, "--json", "defaultBranchRef", "--jq", ".defaultBranchRef.name"]).then((s) => s.trim() || "main"),
    ]);
    const queue = await mergeQueue(repo, defaultBranch);
    return {
        open: JSON.parse(openJson),
        merged: JSON.parse(mergedJson),
        viewer,
        defaultBranch,
        queue,
    };
}

// ---- Classification -------------------------------------------------------

const FAIL = new Set(["FAILURE", "ERROR", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE"]);
const PENDING = new Set(["IN_PROGRESS", "QUEUED", "PENDING", "WAITING", "REQUESTED", "EXPECTED"]);

export function ciSummary(rollup) {
    const checks = (rollup || []).map((c) => ({
        name: c.name || c.context || "check",
        state: c.__typename === "StatusContext" ? c.state : (c.status === "COMPLETED" ? c.conclusion : c.status),
    }));
    const failing = checks.filter((c) => FAIL.has(c.state));
    const pending = checks.filter((c) => PENDING.has(c.state));
    if (!checks.length) return { state: "none", text: "No checks", failing: [], pending: [] };
    if (failing.length) return { state: "failing", text: `${failing.length} failing`, failing: failing.map((c) => c.name), pending: pending.map((c) => c.name) };
    if (pending.length) return { state: "pending", text: `${pending.length} running`, failing: [], pending: pending.map((c) => c.name) };
    return { state: "passing", text: "All passing", failing: [], pending: [] };
}

function sizeLabel(pr) {
    const n = (pr.additions || 0) + (pr.deletions || 0);
    const label = n < 10 ? "XS" : n < 50 ? "S" : n < 250 ? "M" : n < 1000 ? "L" : "XL";
    return { label, text: `+${pr.additions || 0} −${pr.deletions || 0}, ${pr.changedFiles || 0} files` };
}

const BOT_LOGINS = /\[bot\]$|^copilot-pull-request-reviewer$|^github-code-quality$|^copilot$/i;
const humanReview = (r) => r?.author?.login && !r.author.is_bot && !BOT_LOGINS.test(r.author.login);

// Decide a card's lane and a plain-words "waiting on" line.
export function classify(pr, ctx, local) {
    const { viewer, queue } = ctx;
    const ci = ciSummary(pr.statusCheckRollup);
    const labels = (pr.labels || []).map((l) => l.name);
    const reviews = (pr.latestReviews || []).filter(humanReview);
    const mine = reviews.find((r) => r.author.login === viewer);
    const isMine = pr.author?.login === viewer;
    const agent = local?.agent;
    const draft = local?.draft && !local.draft.postedAt ? local.draft : null;
    const q = queue.get(pr.number);
    const author = isMine ? "You" : pr.author.login;
    const authorWho = isMine ? "you" : "author";

    if (q) return { lane: "queue", waitingOn: `Merge queue: position ${q.position}${q.state && q.state !== "QUEUED" ? ` (${q.state.toLowerCase().replace(/_/g, " ")})` : ""}`, who: "github", ci };
    if (agent?.status === "running") {
        if (agent.kind === "ship") return { lane: "approved", waitingOn: "Agent: sending it to the merge queue", who: "agent", ci };
        if (agent.kind === "post") return { lane: "draft-ready", waitingOn: "Agent: posting your review", who: "agent", ci };
        return { lane: "agent-reviewing", waitingOn: "Agent: working on it", who: "agent", ci };
    }
    if (draft) return { lane: "draft-ready", waitingOn: "You: read the draft review, then post it", who: "you", ci };
    if (agent?.status === "waiting") return { lane: "agent-reviewing", waitingOn: `You: ${agent.outcome || "the agent session needs your input"}`, who: "you", ci };
    if (agent?.status === "waiting_on_others") return { lane: "waiting", waitingOn: `Someone else: ${agent.outcome || "the agent is waiting on another person"}`, who: "others", ci };

    if (pr.isDraft) return { lane: "waiting", waitingOn: `${author}: PR is still a draft`, who: authorWho, ci };
    if (pr.reviewDecision === "CHANGES_REQUESTED") {
        const by = reviews.filter((r) => r.state === "CHANGES_REQUESTED").map((r) => r.author.login);
        return { lane: "waiting", waitingOn: `${author}: address changes requested${by.length ? ` by ${by.join(", ")}` : ""}`, who: authorWho, ci };
    }
    if (labels.includes("agent-blocked")) return { lane: "waiting", waitingOn: `${author}: ${isMine ? "your" : "their"} agent is blocked and needs help`, who: authorWho, ci };
    if (pr.mergeable === "CONFLICTING") return { lane: "waiting", waitingOn: `${author}: resolve merge conflicts`, who: authorWho, ci };

    if (pr.reviewDecision === "APPROVED") {
        if (ci.state === "failing") return { lane: "waiting", waitingOn: `${author}: fix failing checks (${ci.failing.slice(0, 2).join(", ")})`, who: authorWho, ci };
        if (ci.state === "pending") return { lane: "approved", waitingOn: `CI: ${ci.pending.length} check${ci.pending.length === 1 ? "" : "s"} still running`, who: "ci", ci };
        if (pr.autoMergeRequest) return { lane: "approved", waitingOn: "GitHub: auto-merge is on; waiting to enter the queue", who: "github", ci };
        return { lane: "approved", waitingOn: "You: ready to ship", who: "you", ci, shippable: true };
    }

    if (labels.includes("agent-preparing")) return { lane: "waiting", waitingOn: isMine ? "Your agent: still preparing it" : `${author}: their agent is still preparing it`, who: isMine ? "agent" : "author", ci };
    if (isMine) return { lane: "waiting", waitingOn: "Another reviewer: needs an approval from someone else", who: "others", ci };
    if (mine) return { lane: "waiting", waitingOn: `Other approvers: you already ${mine.state === "APPROVED" ? "approved" : "reviewed"}`, who: "others", ci };
    if (ci.state === "failing") return { lane: "needs-review", waitingOn: `You: review (note: ${ci.failing.length} failing check${ci.failing.length === 1 ? "" : "s"})`, who: "you", ci };
    return { lane: "needs-review", waitingOn: "You: review needed", who: "you", ci };
}

export const GROUPS = [
    { id: "you", title: "Needs you", hint: "Your move: review, read a draft, answer the agent, or ship." },
    { id: "agent", title: "Agent working", hint: "An agent is on it." },
    { id: "others", title: "Waiting on others", hint: "The author, another reviewer, or CI is next." },
    { id: "queue", title: "In merge queue", hint: "Queued for merge on GitHub." },
    { id: "merged", title: "Merged", hint: "Merged in the last 24 hours." },
];

function groupOf(card) {
    if (card.lane === "merged") return "merged";
    if (card.lane === "queue") return "queue";
    if (card.who === "you") return "you";
    if (card.who === "agent") return "agent";
    return "others";
}

// GitHub-side events for the PR timeline (local board events are merged in by the UI).
function githubEvents(pr) {
    const events = [];
    if (pr.createdAt) events.push({ at: pr.createdAt, text: `${pr.author?.login || "Someone"} opened the PR`, source: "github" });
    for (const r of (pr.latestReviews || []).filter(humanReview)) {
        if (!r.submittedAt) continue;
        const verb = { APPROVED: "approved", CHANGES_REQUESTED: "requested changes", COMMENTED: "commented", DISMISSED: "review dismissed" }[r.state] || r.state.toLowerCase();
        events.push({ at: r.submittedAt, text: `${r.author.login} ${verb}`, source: "github" });
    }
    if (pr.mergedAt) events.push({ at: pr.mergedAt, text: "Merged", source: "github" });
    return events;
}

export function buildCards(data, localPrs) {
    const cards = [];
    for (const pr of data.open) {
        const local = localPrs[String(pr.number)] || {};
        const c = classify(pr, data, local);
        cards.push({
            number: pr.number,
            title: pr.title,
            url: pr.url,
            author: pr.author?.login || "unknown",
            isDraft: pr.isDraft,
            size: sizeLabel(pr),
            ci: c.ci,
            reviewDecision: pr.reviewDecision || "",
            reviewers: (pr.latestReviews || []).filter(humanReview).map((r) => ({ login: r.author.login, state: r.state })),
            requested: (pr.reviewRequests || []).map((r) => r.login || r.name || r.slug).filter(Boolean),
            labels: (pr.labels || []).map((l) => l.name),
            queue: data.queue.get(pr.number) || null,
            lane: c.lane,
            waitingOn: c.waitingOn,
            who: c.who,
            shippable: !!c.shippable,
            updatedAt: pr.updatedAt,
            events: githubEvents(pr),
            local,
        });
    }
    for (const pr of [...data.merged].sort((a, b) => b.mergedAt.localeCompare(a.mergedAt))) {
        const ageDays = (Date.now() - new Date(pr.mergedAt).getTime()) / 86400000;
        if (ageDays > 1) continue;
        const local = { ...(localPrs[String(pr.number)] || {}) };
        // A merged PR needs nothing more; don't keep showing a stale "running" or "waiting" line.
        if (local.agent && local.agent.status !== "done") local.agent = { ...local.agent, status: "done", outcome: "PR merged" };
        cards.push({
            number: pr.number,
            title: pr.title,
            url: pr.url,
            author: pr.author?.login || "unknown",
            size: sizeLabel(pr),
            lane: "merged",
            waitingOn: `Merged ${new Date(pr.mergedAt).toLocaleString()}`,
            who: "done",
            mergedAt: pr.mergedAt,
            events: githubEvents(pr),
            local,
        });
    }
    for (const c of cards) c.group = groupOf(c);
    return cards;
}
