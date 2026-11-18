// GitHub data for the PR board, read with the `gh` CLI.
import { execFile } from "node:child_process";

const PR_FIELDS = [
    "number", "title", "url", "author", "isDraft", "additions", "deletions", "changedFiles",
    "reviewDecision", "mergeStateStatus", "mergeable", "statusCheckRollup", "latestReviews",
    "reviewRequests", "labels", "updatedAt", "createdAt", "autoMergeRequest", "baseRefName", "body",
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

// Timing signals for every open PR in one GraphQL call: pushes, author replies,
// ready-for-review and review-requested events, and auto-merge.
const ACTIVITY_QUERY = `query($owner:String!,$name:String!){repository(owner:$owner,name:$name){pullRequests(states:OPEN,first:60,orderBy:{field:UPDATED_AT,direction:DESC}){nodes{
 number mergeStateStatus autoMergeRequest{enabledAt}
 commits(last:1){nodes{commit{committedDate}}}
 timelineItems(last:40,itemTypes:[PULL_REQUEST_REVIEW,ISSUE_COMMENT,READY_FOR_REVIEW_EVENT,REVIEW_REQUESTED_EVENT,HEAD_REF_FORCE_PUSHED_EVENT,PULL_REQUEST_COMMIT]){nodes{
  __typename
  ... on PullRequestReview{author{login} state submittedAt}
  ... on IssueComment{author{login} createdAt}
  ... on ReadyForReviewEvent{createdAt}
  ... on ReviewRequestedEvent{createdAt}
  ... on HeadRefForcePushedEvent{createdAt}
  ... on PullRequestCommit{commit{committedDate}}
 }}}}}}`;

async function fetchActivity(repo) {
    const [owner, name] = repo.split("/");
    try {
        const out = await gh(["api", "graphql", "-f", `query=${ACTIVITY_QUERY}`, "-F", `owner=${owner}`, "-F", `name=${name}`]);
        const nodes = JSON.parse(out)?.data?.repository?.pullRequests?.nodes || [];
        return new Map(nodes.map((n) => [n.number, n]));
    } catch {
        return new Map(); // Reasons fall back to what gh pr list provides.
    }
}

export async function fetchRepoData(repo) {
    const [openJson, mergedJson, viewer, defaultBranch, activity] = await Promise.all([
        gh(["pr", "list", "-R", repo, "--state", "open", "--limit", "60", "--json", PR_FIELDS]),
        gh(["pr", "list", "-R", repo, "--state", "merged", "--limit", "8", "--json", "number,title,url,author,mergedAt,createdAt,additions,deletions,changedFiles,latestReviews,body"]),
        viewerLogin(),
        gh(["repo", "view", repo, "--json", "defaultBranchRef", "--jq", ".defaultBranchRef.name"]).then((s) => s.trim() || "main"),
        fetchActivity(repo),
    ]);
    const queue = await mergeQueue(repo, defaultBranch);
    return {
        open: JSON.parse(openJson),
        merged: JSON.parse(mergedJson),
        viewer,
        defaultBranch,
        queue,
        activity,
    };
}

// Conversation and files for one PR, fetched when the user selects it.
export async function fetchDetail(repo, number) {
    const out = await gh(["pr", "view", String(number), "-R", repo, "--json", "comments,reviews,files"]);
    const d = JSON.parse(out);
    const conversation = [
        ...(d.comments || []).map((c) => ({ kind: "comment", author: c.author?.login || "unknown", bot: isBot(c.author), at: c.createdAt, body: c.body || "", url: c.url })),
        ...(d.reviews || []).filter((r) => r.body || r.state !== "COMMENTED").map((r) => ({ kind: "review", state: r.state, author: r.author?.login || "unknown", bot: isBot(r.author), at: r.submittedAt, body: r.body || "" })),
    ].sort((a, b) => String(a.at).localeCompare(String(b.at)));
    const files = (d.files || []).map((f) => ({ path: f.path, additions: f.additions || 0, deletions: f.deletions || 0 }))
        .sort((a, b) => (b.additions + b.deletions) - (a.additions + a.deletions));
    return { conversation, files, fetchedAt: new Date().toISOString() };
}

// First real sentence of a PR description: no template comments, headings, images or HTML.
export function fallbackSummary(body) {
    const text = String(body || "")
        .replace(/<!--[\s\S]*?-->/g, " ")
        .replace(/<[^>]+>/g, " ")
        .replace(/```[\s\S]*?```/g, " ")
        .split(/\r?\n/)
        .map((l) => l.trim())
        .filter((l) => l && !/^(#|\||>|-{3,}|!\[|\*\*?(what|why|before|after|not tested|checklist)\b)/i.test(l))
        .join(" ")
        .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
        .replace(/[*_`]/g, "")
        .replace(/\s+/g, " ")
        .trim();
    const m = text.match(/^.{20,200}?[.!?](\s|$)/);
    const s = (m ? m[0] : text).trim();
    return s.length > 160 ? s.slice(0, 157).trimEnd() + "…" : s;
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

const BOT_LOGINS = /\[bot\]$|^copilot-pull-request-reviewer$|^github-code-quality$|^copilot$|^github-actions$|^microsoft-github-policy-service$/i;
const isBot = (a) => !!a && (!!a.is_bot || BOT_LOGINS.test(a.login || ""));
const humanReview = (r) => r?.author?.login && !isBot(r.author);

// How long a PR can sit in one state before the board calls it stuck.
export const STUCK_AFTER_HOURS = {
    ciRunning: 2,           // approved, checks still running
    approvedBlocked: 24,    // approved, but failing checks, conflicts, behind main, or an approval that doesn't count
    changesNoResponse: 5 * 24, // changes requested, author hasn't pushed or replied
    reReview: 3 * 24,       // author responded, nobody re-reviewed yet
    draftNoPush: 14 * 24,   // draft with no new commits
    reviewNoReview: 3 * 24, // ready for review (or review requested), no review yet
    mergeQueue: 3,          // sitting in the merge queue
    autoMerge: 3,           // auto-merge on, not in the queue yet
    agentWaiting: 3 * 24,   // an agent says it's waiting on someone else
};

const HOUR = 3600_000;
const ts = (s) => (s ? new Date(s).getTime() : 0);
const maxIso = (...xs) => xs.filter(Boolean).sort().at(-1) || null;
const minIso = (...xs) => xs.filter(Boolean).sort()[0] || null;

export function duration(sinceIso, now = Date.now()) {
    const h = (now - ts(sinceIso)) / HOUR;
    if (!sinceIso || h < 0) return "";
    if (h < 1) return `${Math.max(1, Math.round(h * 60))} min`;
    if (h < 48) return `${Math.round(h)} hour${Math.round(h) === 1 ? "" : "s"}`;
    const d = Math.round(h / 24);
    return `${d} day${d === 1 ? "" : "s"}`;
}

// Activity facts from the GraphQL timeline (see fetchActivity).
function activity(pr, ext) {
    const author = pr.author?.login;
    const items = ext?.timelineItems?.nodes || [];
    const pushes = items.map((i) => i.__typename === "PullRequestCommit" ? i.commit?.committedDate : i.__typename === "HeadRefForcePushedEvent" ? i.createdAt : null);
    const lastPush = maxIso(...pushes, ext?.commits?.nodes?.[0]?.commit?.committedDate);
    const authorTalk = items.filter((i) => i.author?.login === author && (i.__typename === "IssueComment" || i.__typename === "PullRequestReview")).map((i) => i.createdAt || i.submittedAt);
    const readyAt = maxIso(...items.filter((i) => i.__typename === "ReadyForReviewEvent").map((i) => i.createdAt)) || pr.createdAt;
    const requestedAt = maxIso(...items.filter((i) => i.__typename === "ReviewRequestedEvent").map((i) => i.createdAt));
    return { lastPush, authorActivity: maxIso(lastPush, ...authorTalk), readyAt, requestedAt, autoMergeAt: ext?.autoMergeRequest?.enabledAt || null };
}

function stuckIf(since, key, why) {
    const limit = STUCK_AFTER_HOURS[key];
    return since && Date.now() - ts(since) > limit * HOUR ? `⚠️ Stuck: ${why} for ${duration(since)}` : null;
}

// One reason per PR: a short label, a plain sentence, since when, stuck or not, and a suggested next action.
function reason(label, text, since, stuck, action) {
    return { label, text, since, stuck, action: action || null };
}

// Decide a card's lane, its plain-words "waiting on" line, and why it's waiting.
export function classify(pr, ctx, local) {
    const { viewer, queue } = ctx;
    const ext = ctx.activity?.get(pr.number);
    const act = activity(pr, ext);
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
    const at = `@${pr.author.login}`;
    const nudge = (text) => ({ label: "Nudge the author", nudge: text });
    const ciPendingSince = minIso(...(pr.statusCheckRollup || []).filter((c) => PENDING.has(c.status === "COMPLETED" ? c.conclusion : c.status)).map((c) => c.startedAt).filter((s) => s && !s.startsWith("0001")));
    const approvals = reviews.filter((r) => r.state === "APPROVED");
    const approvedAt = maxIso(...approvals.map((r) => r.submittedAt));

    if (q) {
        const since = q.enqueuedAt;
        return { lane: "queue", waitingOn: `Merge queue: position ${q.position}${q.state && q.state !== "QUEUED" ? ` (${q.state.toLowerCase().replace(/_/g, " ")})` : ""}`, who: "github", ci,
            reason: reason("In merge queue", `Position ${q.position} in GitHub's merge queue.`, since, stuckIf(since, "mergeQueue", "in the merge queue"), { label: "Check the merge queue", url: pr.url.replace(/\/pull\/\d+$/, "/queue/" + (ctx.defaultBranch || "main")) }) };
    }
    if (agent?.status === "running") {
        if (agent.kind === "ship") return { lane: "approved", waitingOn: "Agent: sending it to the merge queue", who: "agent", ci };
        if (agent.kind === "post") return { lane: "draft-ready", waitingOn: "Agent: posting your review", who: "agent", ci };
        return { lane: "agent-reviewing", waitingOn: "Agent: working on it", who: "agent", ci };
    }
    if (draft) return { lane: "draft-ready", waitingOn: "You: read the draft review, then post it", who: "you", ci };
    if (agent?.status === "waiting") return { lane: "agent-reviewing", waitingOn: `You: ${agent.outcome || "the agent session needs your input"}`, who: "you", ci };
    if (agent?.status === "waiting_on_others") {
        const since = agent.updatedAt;
        return { lane: "waiting", waitingOn: `Someone else: ${agent.outcome || "the agent is waiting on another person"}`, who: "others", ci,
            reason: reason("Agent waiting on others", agent.outcome || "An agent is waiting on someone else.", since, stuckIf(since, "agentWaiting", "the agent has been waiting on someone"), { label: "Ask a maintainer to help", nudge: `Could a maintainer take a look at this one? ${agent.outcome ? `It ${agent.outcome.replace(/^needs/, "needs")}.` : ""}`.trim() }) };
    }

    if (pr.isDraft) {
        const since = act.lastPush || pr.createdAt;
        return { lane: "waiting", waitingOn: `${author}: PR is still a draft`, who: authorWho, ci,
            reason: reason("Draft, author still working", `It's a draft. Last push ${duration(since)} ago.`, since, stuckIf(since, "draftNoPush", "no new commits on this draft"), isMine ? null : { label: "Ask if it's still active", nudge: `${at}, is this draft still active? If you've moved on, closing it keeps the PR list tidy; if you're blocked, say what would help.` }) };
    }

    if (pr.reviewDecision === "CHANGES_REQUESTED") {
        // latestReviews hides a change request once that reviewer comments again, so also read the timeline.
        const timelineReqs = (ext?.timelineItems?.nodes || []).filter((i) => i.__typename === "PullRequestReview" && i.state === "CHANGES_REQUESTED" && i.author?.login !== pr.author.login && !isBot(i.author));
        const allReqs = [...reviews.filter((r) => r.state === "CHANGES_REQUESTED"), ...timelineReqs];
        const by = [...new Set(allReqs.map((r) => r.author.login))];
        const reqAt = maxIso(...allReqs.map((r) => r.submittedAt));
        if (reqAt && ts(act.authorActivity) > ts(reqAt)) {
            const since = act.authorActivity;
            const r = reason("Author responded, needs re-review", `${by.join(", ")} requested changes; the author pushed or replied ${duration(since)} ago.`, since, stuckIf(since, "reReview", "no re-review since the author responded"),
                isMine ? { label: "Ask for a re-review", nudge: `@${by.join(", @")}, I've addressed your feedback. Could you take another look?` } : { label: "Re-review the new changes", act: "review" });
            if (isMine) return { lane: "waiting", waitingOn: `${by.join(", ")}: re-review your changes`, who: "others", ci, reason: r };
            return { lane: "needs-review", waitingOn: `You: re-review (author responded to ${by.join(", ")})`, who: "you", ci, reason: r };
        }
        const since = reqAt;
        return { lane: "waiting", waitingOn: `${author}: address changes requested${by.length ? ` by ${by.join(", ")}` : ""}`, who: authorWho, ci,
            reason: reason("Changes requested, no response", `${by.join(", ")} requested changes ${duration(since)} ago; no push or reply from the author since.`, since, stuckIf(since, "changesNoResponse", "no response to changes requested"),
                isMine ? null : nudge(`${at}, friendly ping on the changes ${by.map((b) => "@" + b).join(", ")} requested ${duration(since)} ago. Is anything blocking you, or should someone else pick this up?`)) };
    }
    if (labels.includes("agent-blocked")) {
        const since = act.authorActivity;
        return { lane: "waiting", waitingOn: `${author}: ${isMine ? "your" : "their"} agent is blocked and needs help`, who: authorWho, ci,
            reason: reason("Author's agent is blocked", "The author's agent says it can't continue without help.", since, stuckIf(since, "approvedBlocked", "the agent has been blocked"), isMine ? null : nudge(`${at}, your agent marked this blocked. What does it need to continue?`)) };
    }

    const blockedWhy = ci.state === "failing" ? `failing checks (${ci.failing.slice(0, 2).join(", ")})` : pr.mergeable === "CONFLICTING" ? "merge conflicts" : (pr.mergeStateStatus || ext?.mergeStateStatus) === "BEHIND" ? "it's behind main" : null;
    if (pr.reviewDecision === "APPROVED") {
        if (blockedWhy) {
            const since = maxIso(approvedAt, act.lastPush);
            return { lane: "waiting", waitingOn: `${author}: fix ${blockedWhy}`, who: authorWho, ci,
                reason: reason("Approved, blocked", `Approved, but blocked by ${blockedWhy}.`, since, stuckIf(since, "approvedBlocked", `approved but blocked by ${blockedWhy}`), isMine ? null : nudge(`${at}, this is approved but blocked by ${blockedWhy}. Could you take a look?`)) };
        }
        const queueNote = act.autoMergeAt ? "auto-merge is on, so it goes to the merge queue when checks pass" : "nobody has turned on auto-merge yet";
        if (ci.state === "pending") {
            const since = ciPendingSince || act.lastPush;
            return { lane: "approved", waitingOn: `CI: ${ci.pending.length} check${ci.pending.length === 1 ? "" : "s"} still running`, who: "ci", ci,
                reason: reason("Approved, waiting on CI", `Approved; ${ci.pending.length} check${ci.pending.length === 1 ? "" : "s"} still running, and ${queueNote}.`, since, stuckIf(since, "ciRunning", "checks have been running"), { label: "Look at the running checks", url: pr.url + "/checks" }) };
        }
        if (act.autoMergeAt) {
            const since = act.autoMergeAt;
            return { lane: "approved", waitingOn: "GitHub: auto-merge is on; waiting to enter the queue", who: "github", ci,
                reason: reason("Auto-merge on", "Approved and checks pass; auto-merge should add it to the merge queue.", since, stuckIf(since, "autoMerge", "auto-merge hasn't queued it"), { label: "Check why it isn't queued", url: pr.url }) };
        }
        return { lane: "approved", waitingOn: "You: ready to ship", who: "you", ci, shippable: true,
            reason: reason("Ready to ship", "Approved and checks pass.", approvedAt, null, { label: "Ship it", act: "ship" }) };
    }
    if (pr.mergeable === "CONFLICTING") {
        const since = act.lastPush;
        return { lane: "waiting", waitingOn: `${author}: resolve merge conflicts`, who: authorWho, ci,
            reason: reason("Merge conflicts", "It conflicts with main, so it can't be reviewed or merged as is.", since, stuckIf(since, "approvedBlocked", "it has had conflicts"), isMine ? null : nudge(`${at}, this has merge conflicts with main. Could you update the branch?`)) };
    }
    if (approvals.length) {
        // GitHub says REVIEW_REQUIRED even though someone approved: that approval doesn't count.
        const since = approvedAt;
        return { lane: "waiting", waitingOn: `Someone else: an approval that counts`, who: "others", ci,
            reason: reason("Approval doesn't count", `${approvals.map((r) => r.author.login).join(", ")} approved, but GitHub still needs an approval that counts (for example from someone who didn't push the last commit, or a code owner).`, since, stuckIf(since, "approvedBlocked", "the approval hasn't counted"), { label: "Ask a different maintainer to approve", nudge: `Could another maintainer approve this? The existing approval doesn't count toward branch protection.` }) };
    }
    if (labels.includes("agent-preparing")) {
        const since = act.authorActivity;
        if (isMine) return { lane: "waiting", waitingOn: "Your agent: still preparing it", who: "agent", ci, reason: reason("Your agent is preparing it", "Your agent is still fixing feedback or waiting for CI.", since, null) };
        return { lane: "waiting", waitingOn: `${author}: their agent is still preparing it`, who: "author", ci,
            reason: reason("Author still preparing it", "The author's agent is still addressing feedback or CI; not ready for review.", since, stuckIf(since, "reReview", "the author's agent has been preparing it"), nudge(`${at}, is this close to ready for review? Anything blocking it?`)) };
    }
    const reviewSince = maxIso(act.readyAt, act.requestedAt) || pr.createdAt;
    const requested = (pr.reviewRequests || []).map((r) => r.login || r.name || r.slug).filter(Boolean);
    if (isMine) {
        const who = requested.length ? requested.join(", ") : "any maintainer";
        return { lane: "waiting", waitingOn: `${requested.length ? requested.join(", ") : "Another reviewer"}: needs an approval from someone else`, who: "others", ci,
            reason: reason("Waiting on a reviewer", `Waiting for ${who} to review; nobody has yet.`, reviewSince, stuckIf(reviewSince, "reviewNoReview", "no review"), { label: requested.length ? `Ask ${requested.join(", ")} to review` : "Ask a maintainer to review", nudge: `${requested.length ? requested.map((r) => "@" + r).join(", ") : "Could a maintainer"} take a look at this one when you have a moment?`.replace(/^(@[^ ]+(?:, @[^ ]+)*) take/, "$1, could you take") }) };
    }
    if (mine) {
        const since = mine.submittedAt;
        return { lane: "waiting", waitingOn: `Other approvers: you already ${mine.state === "APPROVED" ? "approved" : "reviewed"}`, who: "others", ci,
            reason: reason("Waiting on other reviewers", `You already ${mine.state === "APPROVED" ? "approved" : "reviewed"}; it needs another review.`, since, stuckIf(since, "reviewNoReview", "no other review"), { label: "Ask a maintainer to review", nudge: "Could another maintainer take a look at this one?" }) };
    }
    const r = reason("Waiting on your review", `Ready for review ${duration(reviewSince)} ago; no human review yet.`, reviewSince, stuckIf(reviewSince, "reviewNoReview", "no review"), { label: "Review it", act: "review" });
    if (ci.state === "failing") return { lane: "needs-review", waitingOn: `You: review (note: ${ci.failing.length} failing check${ci.failing.length === 1 ? "" : "s"})`, who: "you", ci, reason: r };
    return { lane: "needs-review", waitingOn: "You: review needed", who: "you", ci, reason: r };
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
            reason: c.reason || null,
            who: c.who,
            shippable: !!c.shippable,
            updatedAt: pr.updatedAt,
            createdAt: pr.createdAt,
            description: pr.body || "",
            fallbackSummary: fallbackSummary(pr.body),
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
            createdAt: pr.createdAt,
            description: pr.body || "",
            fallbackSummary: fallbackSummary(pr.body),
            reviewers: (pr.latestReviews || []).filter(humanReview).map((r) => ({ login: r.author.login, state: r.state })),
            events: githubEvents(pr),
            local,
        });
    }
    for (const c of cards) c.group = groupOf(c);
    // Stuck PRs first within their group; otherwise keep GitHub's order.
    cards.sort((a, b) => (b.reason?.stuck ? 1 : 0) - (a.reason?.stuck ? 1 : 0));
    return cards;
}
