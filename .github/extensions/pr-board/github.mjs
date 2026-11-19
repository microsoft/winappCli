// GitHub data for the PR board, read with the `gh` CLI.
import { execFile } from "node:child_process";

const PR_FIELDS = [
    "number", "title", "url", "author", "isDraft", "additions", "deletions", "changedFiles",
    "reviewDecision", "mergeStateStatus", "mergeable", "statusCheckRollup", "latestReviews",
    "reviewRequests", "labels", "updatedAt", "createdAt", "autoMergeRequest", "baseRefName", "body", "headRefOid",
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

// Issues on the board: open issues assigned to you with no open PR yet, plus every issue a session here is
// linked to (shown under Yours with its PR's state). A linked issue that closed in the last two days stays,
// so you can see it merged.
const ISSUE_FIELDS = "number,title,url,state,updatedAt,createdAt,closedAt,author,labels,assignees,closedByPullRequestsReferences";
const RECENT_CLOSE_MS = 2 * 86400000;

// prIndex: PR number -> { number, state: "OPEN" | "MERGED" | "CLOSED", isDraft, url } for PRs already loaded.
export async function fetchIssues(repo, { linked = [], prIndex = new Map(), localIssues = {} } = {}) {
    const sameRepo = (r) => !r.repository || `${r.repository.owner?.login}/${r.repository.name}`.toLowerCase() === repo.toLowerCase();
    const isOpenPr = (n) => prIndex.get(n)?.state === "OPEN";
    let assigned = [];
    try {
        assigned = JSON.parse(await gh(["issue", "list", "-R", repo, "--assignee", "@me", "--state", "open", "--limit", "50", "--json", ISSUE_FIELDS]));
    } catch {
        assigned = [];
    }
    const linkedSet = new Set(linked);
    const out = new Map();
    for (const i of assigned) {
        const hasOpenPr = (i.closedByPullRequestsReferences || []).some((r) => sameRepo(r) && isOpenPr(r.number));
        if (linkedSet.has(i.number) || !hasOpenPr) out.set(i.number, { ...i, assignedToYou: true });
    }
    const missing = linked.filter((n) => !out.has(n));
    const viewed = await Promise.all(missing.slice(0, 20).map((n) => gh(["issue", "view", String(n), "-R", repo, "--json", ISSUE_FIELDS]).then((s) => JSON.parse(s)).catch(() => null)));
    for (const i of viewed) {
        if (!i) continue;
        const recent = i.state !== "OPEN" && i.closedAt && Date.now() - new Date(i.closedAt).getTime() < RECENT_CLOSE_MS;
        if (i.state === "OPEN" || recent) out.set(i.number, { ...i, assignedToYou: false });
    }

    // Find each linked issue's PR: an explicit link, GitHub's "closes #n" references, or the session's branch.
    const lookups = [];
    for (const i of out.values()) {
        const local = localIssues[String(i.number)] || {};
        const nums = new Set((i.closedByPullRequestsReferences || []).filter(sameRepo).map((r) => r.number));
        if (Number.isInteger(local.linkedPr)) nums.add(local.linkedPr);
        i.prNumbers = [...nums];
        if (local.branch && local.session?.id) lookups.push(gh(["pr", "list", "-R", repo, "--head", local.branch, "--state", "all", "--limit", "3", "--json", "number,state,isDraft,url"]).then((s) => { for (const p of JSON.parse(s)) { prIndex.set(p.number, p); if (!i.prNumbers.includes(p.number)) i.prNumbers.push(p.number); } }).catch(() => {}));
    }
    await Promise.all(lookups);
    const unknown = [...new Set([...out.values()].flatMap((i) => i.prNumbers))].filter((n) => !prIndex.has(n)).slice(0, 10);
    await Promise.all(unknown.map((n) => gh(["pr", "view", String(n), "-R", repo, "--json", "number,state,isDraft,url"]).then((s) => prIndex.set(n, JSON.parse(s))).catch(() => {})));
    const rank = (p) => (p.state === "OPEN" ? (p.isDraft ? 1 : 0) : p.state === "MERGED" ? 2 : 3);
    for (const i of out.values()) {
        const prs = i.prNumbers.map((n) => prIndex.get(n)).filter(Boolean).sort((a, b) => rank(a) - rank(b) || b.number - a.number);
        i.prs = prs.map((p) => ({ number: p.number, state: p.state === "OPEN" ? (p.isDraft ? "draft" : "open") : p.state === "MERGED" ? "merged" : "closed" }));
        i.pr = prs[0] ? { number: prs[0].number, url: prs[0].url, state: prs[0].state === "OPEN" ? (prs[0].isDraft ? "draft" : "open") : prs[0].state === "MERGED" ? "merged" : "closed" } : null;
    }
    // A branch with no open PR yet: read its head, so the board notices when the agent pushes.
    await Promise.all([...out.values()].filter((i) => { const l = localIssues[String(i.number)] || {}; return l.branch && l.session?.id && (!i.pr || i.pr.state === "closed"); }).slice(0, 10)
        .map((i) => gh(["api", `repos/${repo}/branches/${encodeURIComponent(localIssues[String(i.number)].branch)}`, "--jq", ".commit.sha"]).then((s) => { i.branchHead = s.trim() || null; }).catch(() => {})));
    return [...out.values()];
}

export function buildIssueCards(issues, localIssues) {
    return issues.map((i) => {
        const local = localIssues[String(i.number)] || {};
        const status = local.status
            || (local.session?.id ? `Being worked on in "${local.session.name || "a session here"}"` : i.assignedToYou ? "Assigned to you; no PR yet" : "Linked here; no PR yet");
        return {
            kind: "issue",
            number: i.number,
            title: i.title,
            url: i.url,
            state: i.state,
            author: i.author?.login || "unknown",
            createdAt: i.createdAt,
            updatedAt: i.updatedAt,
            labels: (i.labels || []).map((l) => l.name),
            assignees: (i.assignees || []).map((a) => a.login),
            assignedToYou: !!i.assignedToYou,
            pr: i.pr || null,
            prs: i.prs || [],
            branch: local.branch || null,
            branchHead: i.branchHead || null,
            // An issue someone is working on here belongs with your other work; assigned-only ones get their own list.
            section: local.session?.id ? "yours" : "issues",
            group: "issue",
            status,
            waitingOn: status,
            hidden: !!local.hidden,
            local,
        };
    });
}

// What changed since your last review: compare the commit you reviewed with the PR head.
export async function fetchChangesSince(repo, number, viewer, fallbackSha) {
    const reviews = JSON.parse(await gh(["api", `repos/${repo}/pulls/${number}/reviews?per_page=100`]));
    const mine = reviews.filter((r) => r.user?.login === viewer && r.commit_id).sort((a, b) => String(a.submitted_at).localeCompare(String(b.submitted_at))).at(-1);
    const base = mine?.commit_id || fallbackSha;
    const pr = JSON.parse(await gh(["api", `repos/${repo}/pulls/${number}`]));
    const head = pr.head?.sha;
    if (!base) return { base: null, head, reviewedAt: null, commits: [], files: [], note: "You haven't reviewed this PR, and pr-review hasn't run here, so there's nothing to compare against." };
    if (base === head) return { base, head, reviewedAt: mine?.submitted_at || null, commits: [], files: [], note: "Nothing changed: the PR is still on the commit you reviewed." };
    const cmp = JSON.parse(await gh(["api", `repos/${repo}/compare/${base}...${head}`]));
    return {
        base,
        head,
        reviewedAt: mine?.submitted_at || null,
        status: cmp.status, // "ahead", or "diverged" after a force push
        commits: (cmp.commits || []).map((k) => ({ sha: k.sha, message: String(k.commit?.message || "").split("\n")[0].slice(0, 200), author: k.author?.login || k.commit?.author?.name || "", at: k.commit?.author?.date })),
        files: (cmp.files || []).map((f) => ({ path: f.filename, status: f.status, additions: f.additions, deletions: f.deletions })).sort((a, b) => (b.additions + b.deletions) - (a.additions + a.deletions)),
        note: cmp.status === "diverged" ? "The branch was rebased or force-pushed since your review, so this compares across the rewrite." : null,
    };
}

// Files for one PR, fetched when the user selects it. The GitHub tab reads the conversation itself.
export async function fetchDetail(repo, number) {
    const out = await gh(["pr", "view", String(number), "-R", repo, "--json", "files"]);
    const d = JSON.parse(out);
    const files = (d.files || []).map((f) => ({ path: f.path, additions: f.additions || 0, deletions: f.deletions || 0 }))
        .sort((a, b) => (b.additions + b.deletions) - (a.additions + a.deletions));
    return { files, fetchedAt: new Date().toISOString() };
}

// ---- GitHub view: the PR or issue as it is on GitHub ---------------------------

const WHO = "login avatarUrl";
const EVENT_FIELDS = `
    __typename
    ... on IssueComment { author { ${WHO} } createdAt bodyHTML url }
    ... on LabeledEvent { actor { ${WHO} } createdAt label { name color } }
    ... on UnlabeledEvent { actor { ${WHO} } createdAt label { name color } }
    ... on AssignedEvent { actor { ${WHO} } createdAt assignee { ... on User { login } ... on Bot { login } } }
    ... on UnassignedEvent { actor { ${WHO} } createdAt assignee { ... on User { login } ... on Bot { login } } }
    ... on ClosedEvent { actor { ${WHO} } createdAt }
    ... on ReopenedEvent { actor { ${WHO} } createdAt }
    ... on RenamedTitleEvent { actor { ${WHO} } createdAt previousTitle currentTitle }
    ... on CrossReferencedEvent { actor { ${WHO} } createdAt willCloseTarget source { __typename ... on Issue { number title url } ... on PullRequest { number title url } } }`;
const PR_EVENT_FIELDS = EVENT_FIELDS + `
    ... on PullRequestReview { id author { ${WHO} } submittedAt createdAt state bodyHTML url }
    ... on PullRequestCommit { url commit { abbreviatedOid messageHeadline committedDate author { name user { ${WHO} } } } }
    ... on ReviewRequestedEvent { actor { ${WHO} } createdAt requestedReviewer { ... on User { login } ... on Team { name } ... on Bot { login } } }
    ... on ReviewRequestRemovedEvent { actor { ${WHO} } createdAt requestedReviewer { ... on User { login } ... on Team { name } ... on Bot { login } } }
    ... on ReadyForReviewEvent { actor { ${WHO} } createdAt }
    ... on ConvertToDraftEvent { actor { ${WHO} } createdAt }
    ... on HeadRefForcePushedEvent { actor { ${WHO} } createdAt beforeCommit { abbreviatedOid } afterCommit { abbreviatedOid } }
    ... on AddedToMergeQueueEvent { actor { ${WHO} } createdAt }
    ... on RemovedFromMergeQueueEvent { actor { ${WHO} } createdAt reason }
    ... on MergedEvent { actor { ${WHO} } createdAt commit { abbreviatedOid } }
    ... on AutoMergeEnabledEvent { actor { ${WHO} } createdAt }
    ... on AutoMergeDisabledEvent { actor { ${WHO} } createdAt reason }
    ... on ReviewDismissedEvent { actor { ${WHO} } createdAt dismissalMessage }`;
const ISSUE_TYPES = "ISSUE_COMMENT, LABELED_EVENT, UNLABELED_EVENT, ASSIGNED_EVENT, UNASSIGNED_EVENT, CLOSED_EVENT, REOPENED_EVENT, RENAMED_TITLE_EVENT, CROSS_REFERENCED_EVENT";
const PR_TYPES = ISSUE_TYPES + ", PULL_REQUEST_REVIEW, PULL_REQUEST_COMMIT, REVIEW_REQUESTED_EVENT, REVIEW_REQUEST_REMOVED_EVENT, READY_FOR_REVIEW_EVENT, CONVERT_TO_DRAFT_EVENT, HEAD_REF_FORCE_PUSHED_EVENT, ADDED_TO_MERGE_QUEUE_EVENT, REMOVED_FROM_MERGE_QUEUE_EVENT, MERGED_EVENT, AUTO_MERGE_ENABLED_EVENT, AUTO_MERGE_DISABLED_EVENT, REVIEW_DISMISSED_EVENT";
const timelineSel = (pr) => `timelineItems(first: 100, after: $after, itemTypes: [${pr ? PR_TYPES : ISSUE_TYPES}]) { pageInfo { hasNextPage endCursor } nodes { ${pr ? PR_EVENT_FIELDS : EVENT_FIELDS} } }`;
const PR_VIEW_QUERY = `query($owner: String!, $name: String!, $n: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $n) {
    number title url state isDraft createdAt updatedAt bodyHTML baseRefName headRefName author { ${WHO} }
    labels(first: 30) { nodes { name color } }
    assignees(first: 20) { nodes { login } }
    reviewRequests(first: 30) { nodes { requestedReviewer { ... on User { login } ... on Team { name } ... on Bot { login } } } }
    closingIssuesReferences(first: 10) { nodes { number title url state } }
    commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 100) { nodes { __typename
        ... on CheckRun { name status conclusion detailsUrl startedAt completedAt checkSuite { workflowRun { workflow { name } } } }
        ... on StatusContext { context state targetUrl description createdAt } } } } } } }
    reviewThreads(first: 100) { nodes { isResolved isOutdated path line originalLine comments(first: 50) { nodes { author { ${WHO} } createdAt bodyHTML url diffHunk pullRequestReview { id } } } } }
    ${timelineSel(true)}
} } }`;
const PR_TIMELINE_QUERY = `query($owner: String!, $name: String!, $n: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $n) { ${timelineSel(true)} } } }`;
const ISSUE_VIEW_QUERY = `query($owner: String!, $name: String!, $n: Int!, $after: String) { repository(owner: $owner, name: $name) { issue(number: $n) {
    number title url state stateReason createdAt updatedAt bodyHTML author { ${WHO} }
    labels(first: 30) { nodes { name color } }
    assignees(first: 20) { nodes { login } }
    closedByPullRequestsReferences(first: 10, includeClosedPrs: true) { nodes { number title url state isDraft } }
    ${timelineSel(false)}
} } }`;
const ISSUE_TIMELINE_QUERY = `query($owner: String!, $name: String!, $n: Int!, $after: String) { repository(owner: $owner, name: $name) { issue(number: $n) { ${timelineSel(false)} } } }`;

const person = (a) => (a?.login ? { login: a.login, avatar: a.avatarUrl || null } : { login: a?.name || "ghost", avatar: null });
const reviewerName = (r) => r?.login || r?.name || "someone";
const lastLines = (hunk, n = 8) => String(hunk || "").split("\n").slice(-n).join("\n");

// One event from the GitHub timeline, in a shape the board renders directly.
function viewEvent(e) {
    const base = { at: e.createdAt || null, who: person(e.actor || e.author) };
    switch (e.__typename) {
        case "IssueComment": return { ...base, type: "comment", html: e.bodyHTML || "", url: e.url };
        case "PullRequestReview": return { ...base, at: e.submittedAt || e.createdAt, type: "review", id: e.id, state: e.state, html: e.bodyHTML || "", url: e.url, threads: [] };
        case "PullRequestCommit": return { at: e.commit?.committedDate, who: person(e.commit?.author?.user || { name: e.commit?.author?.name }), type: "commit", sha: e.commit?.abbreviatedOid, text: e.commit?.messageHeadline || "", url: e.url };
        case "LabeledEvent": return { ...base, type: "event", icon: "label", text: "added the label", label: e.label };
        case "UnlabeledEvent": return { ...base, type: "event", icon: "label", text: "removed the label", label: e.label };
        case "AssignedEvent": return { ...base, type: "event", icon: "person", text: e.assignee?.login && e.assignee.login === e.actor?.login ? "self-assigned this" : `assigned ${e.assignee?.login || "someone"}` };
        case "UnassignedEvent": return { ...base, type: "event", icon: "person", text: `unassigned ${e.assignee?.login || "someone"}` };
        case "ClosedEvent": return { ...base, type: "event", icon: "x", text: "closed this" };
        case "ReopenedEvent": return { ...base, type: "event", icon: "reset", text: "reopened this" };
        case "RenamedTitleEvent": return { ...base, type: "event", icon: "pencil", text: `changed the title from "${e.previousTitle}" to "${e.currentTitle}"` };
        case "CrossReferencedEvent": return e.source?.number ? { ...base, type: "event", icon: "link", text: e.willCloseTarget ? "linked a PR that will close this:" : "mentioned this in", ref: { number: e.source.number, title: e.source.title, url: e.source.url, pr: e.source.__typename === "PullRequest" } } : null;
        case "ReviewRequestedEvent": return { ...base, type: "event", icon: "eye", text: `requested a review from ${reviewerName(e.requestedReviewer)}` };
        case "ReviewRequestRemovedEvent": return { ...base, type: "event", icon: "eye", text: `removed the review request for ${reviewerName(e.requestedReviewer)}` };
        case "ReadyForReviewEvent": return { ...base, type: "event", icon: "eye", text: "marked this ready for review" };
        case "ConvertToDraftEvent": return { ...base, type: "event", icon: "pencil", text: "converted this to a draft" };
        case "HeadRefForcePushedEvent": return { ...base, type: "event", icon: "reset", text: `force-pushed from ${e.beforeCommit?.abbreviatedOid || "?"} to ${e.afterCommit?.abbreviatedOid || "?"}` };
        case "AddedToMergeQueueEvent": return { ...base, type: "event", icon: "queue", text: "added this to the merge queue" };
        case "RemovedFromMergeQueueEvent": return { ...base, type: "event", icon: "queue", text: `removed this from the merge queue${e.reason ? ": " + e.reason.toLowerCase().replace(/_/g, " ") : ""}` };
        case "MergedEvent": return { ...base, type: "event", icon: "merge", text: `merged commit ${e.commit?.abbreviatedOid || ""}` };
        case "AutoMergeEnabledEvent": return { ...base, type: "event", icon: "merge", text: "enabled auto-merge" };
        case "AutoMergeDisabledEvent": return { ...base, type: "event", icon: "merge", text: `disabled auto-merge${e.reason ? ": " + e.reason : ""}` };
        case "ReviewDismissedEvent": return { ...base, type: "event", icon: "x", text: `dismissed a review${e.dismissalMessage ? ": " + e.dismissalMessage : ""}` };
        default: return null;
    }
}

const checkState = (c) => (c.__typename === "StatusContext" ? c.state : c.status === "COMPLETED" ? c.conclusion : c.status);

// Everything GitHub shows for one PR or issue, read-only, for the board's GitHub tab.
export async function fetchGitHubView(repo, kind, number) {
    const [owner, name] = repo.split("/");
    const pr = kind !== "issue";
    const run = async (query, after) => {
        const args = ["api", "graphql", "-f", `query=${query}`, "-f", `owner=${owner}`, "-f", `name=${name}`, "-F", `n=${number}`];
        if (after) args.push("-f", `after=${after}`);
        const d = JSON.parse(await gh(args, { timeoutMs: 60000 }));
        const node = d.data?.repository?.[pr ? "pullRequest" : "issue"];
        if (!node) throw new Error(d.errors?.[0]?.message || `${pr ? "PR" : "Issue"} #${number} not found`);
        return node;
    };
    const node = await run(pr ? PR_VIEW_QUERY : ISSUE_VIEW_QUERY);
    const raw = [...node.timelineItems.nodes];
    let page = node.timelineItems.pageInfo;
    for (let i = 0; page.hasNextPage && i < 9; i++) {
        const more = await run(pr ? PR_TIMELINE_QUERY : ISSUE_TIMELINE_QUERY, page.endCursor);
        raw.push(...more.timelineItems.nodes);
        page = more.timelineItems.pageInfo;
    }
    const timeline = raw.map(viewEvent).filter(Boolean);

    if (pr) {
        // Inline review threads sit under the review that started them, as on GitHub.
        const reviews = new Map(timeline.filter((e) => e.type === "review").map((e) => [e.id, e]));
        for (const th of node.reviewThreads?.nodes || []) {
            const cs = th.comments?.nodes || [];
            if (!cs.length) continue;
            const thread = { path: th.path, line: th.line ?? th.originalLine ?? null, outdated: !!th.isOutdated, resolved: !!th.isResolved, hunk: lastLines(cs[0].diffHunk), url: cs[0].url,
                comments: cs.map((c) => ({ who: person(c.author), at: c.createdAt, html: c.bodyHTML || "", url: c.url })) };
            const parent = reviews.get(cs[0].pullRequestReview?.id);
            if (parent) parent.threads.push(thread);
            else timeline.push({ type: "thread", at: cs[0].createdAt, who: person(cs[0].author), thread });
        }
        // Replies in a thread create their own empty "commented" reviews; GitHub hides those, so do we.
        for (let i = timeline.length - 1; i >= 0; i--) { const e = timeline[i]; if (e.type === "review" && e.state === "COMMENTED" && !e.html.trim() && !e.threads.length) timeline.splice(i, 1); }
    }
    timeline.sort((a, b) => String(a.at || "").localeCompare(String(b.at || "")));

    const state = pr ? (node.state === "MERGED" ? "merged" : node.state === "CLOSED" ? "closed" : node.isDraft ? "draft" : "open")
        : node.state === "CLOSED" ? (node.stateReason === "NOT_PLANNED" ? "closed as not planned" : "closed") : "open";
    const checks = pr ? (node.commits?.nodes?.[0]?.commit?.statusCheckRollup?.contexts?.nodes || []).map((c) => ({
        name: c.name || c.context || "check",
        workflow: c.checkSuite?.workflowRun?.workflow?.name || null,
        state: checkState(c) || "PENDING",
        url: c.detailsUrl || c.targetUrl || null,
        description: c.description || null,
        at: c.completedAt || c.startedAt || c.createdAt || null,
    })) : [];
    const linked = pr ? (node.closingIssuesReferences?.nodes || []).map((i) => ({ number: i.number, title: i.title, url: i.url, state: i.state.toLowerCase(), pr: false }))
        : (node.closedByPullRequestsReferences?.nodes || []).map((p) => ({ number: p.number, title: p.title, url: p.url, state: p.state === "OPEN" && p.isDraft ? "draft" : p.state.toLowerCase(), pr: true }));
    return {
        kind: pr ? "pr" : "issue",
        number: node.number,
        title: node.title,
        url: node.url,
        state,
        author: person(node.author),
        createdAt: node.createdAt,
        updatedAt: node.updatedAt,
        base: node.baseRefName || null,
        head: node.headRefName || null,
        html: node.bodyHTML || "",
        labels: (node.labels?.nodes || []).map((l) => ({ name: l.name, color: l.color })),
        assignees: (node.assignees?.nodes || []).map((a) => a.login),
        requested: (node.reviewRequests?.nodes || []).map((r) => reviewerName(r.requestedReviewer)),
        linked,
        checks,
        timeline,
        truncated: !!page.hasNextPage,
        fetchedAt: new Date().toISOString(),
    };
}

// ---- Branch review: the diff of a branch that has no PR yet --------------------

// The branch against the default branch (three-dot, like a PR), plus which files changed since each older
// commit a private comment was made on, so comments can say "changed since" or "outdated".
export async function fetchBranchCompare(repo, base, branch, commentShas = []) {
    const cmp = JSON.parse(await gh(["api", `repos/${repo}/compare/${encodeURIComponent(base)}...${encodeURIComponent(branch)}`], { timeoutMs: 60000 }));
    const head = cmp.commits?.at(-1)?.sha || null;
    const changedSince = {};
    for (const sha of [...new Set(commentShas)].filter((s) => s && s !== head).slice(0, 10)) {
        try {
            const d = JSON.parse(await gh(["api", `repos/${repo}/compare/${sha}...${head}`]));
            changedSince[sha] = { files: (d.files || []).map((f) => f.filename), commits: d.ahead_by || 0, rewritten: d.status === "diverged" };
        } catch {
            changedSince[sha] = null; // the commit is gone: the branch was rewritten
        }
    }
    return {
        base,
        branch,
        head,
        aheadBy: cmp.ahead_by || 0,
        behindBy: cmp.behind_by || 0,
        url: cmp.html_url || `https://github.com/${repo}/compare/${base}...${branch}`,
        commits: (cmp.commits || []).map((k) => ({ sha: k.sha, message: String(k.commit?.message || "").split("\n")[0].slice(0, 200), author: k.author?.login || k.commit?.author?.name || "", avatar: k.author?.avatar_url || null, at: k.commit?.author?.date, url: k.html_url })),
        files: (cmp.files || []).map((f) => ({ path: f.filename, previous: f.previous_filename || null, status: f.status, additions: f.additions, deletions: f.deletions, patch: f.patch ?? null, url: f.blob_url || null })),
        changedSince,
        fetchedAt: new Date().toISOString(),
    };
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
// The viewer's latest review: GitHub's, or one just posted from the board that GitHub's data doesn't show yet.
const POSTED_STATE = { approve: "APPROVED", request_changes: "CHANGES_REQUESTED", comment: "COMMENTED" };
function myLatestReview(pr, viewer, local) {
    const mine = (pr.latestReviews || []).find((r) => r.author?.login === viewer);
    const pv = local?.prReview;
    const posted = pv?.result === "posted" && pv.postedAt && POSTED_STATE[pv.event] ? { state: POSTED_STATE[pv.event], submittedAt: pv.postedAt, author: { login: viewer } } : null;
    return posted && (!mine || ts(posted.submittedAt) > ts(mine.submittedAt)) ? posted : mine || null;
}

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
    const mine = myLatestReview(pr, viewer, local);
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
        // Someone else's PR: once it's approved and green, merging is the author's move.
        if (!isMine && local?.role !== "author" && !isBot(pr.author)) return { lane: "approved", waitingOn: `${author}: merge it (approved, checks pass)`, who: "author", ci, shippable: true,
            reason: reason("Approved, ready to merge", "Approved and checks pass; the author can merge it.", approvedAt, null, { label: "Ship it", act: "ship" }) };
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
        return { lane: "waiting", waitingOn: approvals.some((r) => r.author.login === viewer) ? "Another reviewer: one more approval" : "Someone else: an approval that counts", who: "others", ci,
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
        if (mine.state === "APPROVED") return { lane: "waiting", waitingOn: "Another reviewer: one more approval", who: "others", ci,
            reason: reason("Waiting on another approval", "You approved; GitHub still needs another approval.", since, stuckIf(since, "reviewNoReview", "no other approval"), { label: "Ask a maintainer to review", nudge: "Could another maintainer take a look at this one?" }) };
        // You commented or asked for changes: the author is next, until they push or reply.
        if (ts(act.authorActivity) > ts(since)) {
            const s2 = act.authorActivity;
            return { lane: "needs-review", waitingOn: "You: re-review (the author responded)", who: "you", ci,
                reason: reason("Author responded, needs re-review", `You reviewed ${duration(since)} ago; the author pushed or replied since.`, s2, stuckIf(s2, "reReview", "no re-review since the author responded"), { label: "Re-review the new changes", act: "review" }) };
        }
        return { lane: "waiting", waitingOn: `${author}: respond to your review`, who: authorWho, ci,
            reason: reason("Waiting on the author", `You ${mine.state === "CHANGES_REQUESTED" ? "requested changes" : "commented"} ${duration(since)} ago; no push or reply from the author since.`, since, stuckIf(since, "changesNoResponse", "no response to your review"), nudge(`${at}, friendly ping on my review from ${duration(since)} ago. Is anything blocking you?`)) };
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

// Which Overview section a PR belongs to, and what our own review work looks like.
// yours: you wrote it, or a session here is working on it (not reviewing it).
// reviewing: you're a requested reviewer, you reviewed it, or pr-review ran here.
function tracking(pr, data, local, ext) {
    const viewer = data.viewer;
    const items = ext?.timelineItems?.nodes || [];
    const viewerReviewAt = maxIso(...items.filter((i) => i.__typename === "PullRequestReview" && i.author?.login === viewer).map((i) => i.submittedAt),
        ...(pr.latestReviews || []).filter((r) => r.author?.login === viewer).map((r) => r.submittedAt));
    const lastPush = maxIso(...items.map((i) => i.__typename === "PullRequestCommit" ? i.commit?.committedDate : i.__typename === "HeadRefForcePushedEvent" ? i.createdAt : null), ext?.commits?.nodes?.[0]?.commit?.committedDate);
    const requested = (pr.reviewRequests || []).some((r) => r.login === viewer);
    const reviewWork = !!(local.prReview || local.draft || ["review", "post"].includes(local.agent?.kind));
    let section = "other";
    if (local.role === "author" || pr.author?.login === viewer) section = "yours";
    else if (local.tracked === "reviewer") section = "reviewing";
    else if (local.role === "reviewer" || requested || viewerReviewAt || reviewWork) section = "reviewing";
    else if (local.session?.id) section = "yours";

    // pr-review state: ran here (and on which commit), or a human review on GitHub.
    let review;
    const pv = local.prReview;
    if (pv?.headSha && pr.headRefOid && pv.headSha !== pr.headRefOid) review = { state: "stale", label: "Re-run needed", text: `pr-review ran on an older commit (${pv.headSha.slice(0, 7)}); new commits landed since.`, at: pv.at };
    else if (pv) review = { state: "done", label: pv.result === "posted" ? "Posted" : "Drafted", text: pv.result === "posted" ? `Review posted${pv.event ? ` (${pv.event.replace("_", " ")})` : ""}.` : "A pr-review draft is ready.", at: pv.at };
    else if (local.draft) review = { state: "done", label: local.draft.postedAt ? "Posted" : "Drafted", text: local.draft.postedAt ? "Review posted." : "A pr-review draft is ready.", at: local.draft.postedAt || local.draft.attachedAt };
    else if (local.agent?.kind === "post" && local.agent.status === "done") review = { state: "done", label: "Posted", text: local.agent.outcome || "Review posted.", at: local.agent.updatedAt };
    else if (viewerReviewAt && lastPush && lastPush > viewerReviewAt) review = { state: "stale", label: "Needs re-review", text: "The author pushed after your review.", at: lastPush };
    else if (viewerReviewAt) review = { state: "done", label: "You reviewed", text: "You reviewed it on GitHub.", at: viewerReviewAt };
    else review = { state: "none", label: "Not run", text: "No pr-review here yet.", at: null };
    // Without a recorded commit, fall back to time: a push after our review means it needs another look.
    if (review.state === "done" && !pv?.headSha && lastPush && review.at && lastPush > review.at) review = { state: "stale", label: "Needs re-review", text: "The author pushed after this review.", at: lastPush };

    // A short "what's happened so far" for reviews: human reviews, our drafts/posts, and the latest push.
    const story = [];
    for (const r of items.filter((i) => i.__typename === "PullRequestReview" && i.author && !isBot(i.author) && i.author.login !== pr.author?.login && i.state && i.state !== "COMMENTED")) {
        story.push({ at: r.submittedAt, text: `${r.author.login === viewer ? "You" : r.author.login} ${r.state === "APPROVED" ? "approved" : r.state === "CHANGES_REQUESTED" ? "requested changes" : r.state.toLowerCase()}` });
    }
    if (pv?.draftedAt) story.push({ at: pv.draftedAt, text: "pr-review drafted" });
    if (pv?.postedAt) story.push({ at: pv.postedAt, text: `Review posted${pv.event ? ` (${pv.event.replace("_", " ")})` : ""}` });
    if (!pv && local.draft?.attachedAt) story.push({ at: local.draft.attachedAt, text: "pr-review drafted" });
    if (!pv && local.draft?.postedAt) story.push({ at: local.draft.postedAt, text: "Review posted" });
    if (lastPush) story.push({ at: lastPush, text: `${pr.author?.login || "Author"} pushed` });
    story.sort((a, b) => String(a.at).localeCompare(String(b.at)));
    return { section, review, story: story.slice(-3), headSha: pr.headRefOid || null, viewerReviewAt };
}

export function buildCards(data, localPrs) {
    const cards = [];
    for (const pr of data.open) {
        const local = localPrs[String(pr.number)] || {};
        const c = classify(pr, data, local);
        const t = tracking(pr, data, local, data.activity?.get(pr.number));
        const my = myLatestReview(pr, data.viewer, local);
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
            section: t.section,
            review: t.review,
            story: t.story,
            headSha: t.headSha,
            who: c.who,
            shippable: !!c.shippable,
            myReviewAt: maxIso(t.viewerReviewAt, my?.submittedAt),
            hint: pr.author?.login !== data.viewer && my?.state === "COMMENTED" && pr.reviewDecision === "REVIEW_REQUIRED" ? "Your comment doesn't count as approval" : null,
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
            section: "merged",
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
    return cards;
}

// ---- Readiness for human review (pr-lifecycle "Finish") -------------------------

const READY_QUERY = `query($owner: String!, $name: String!, $n: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $n) {
    number url headRefOid headRefName baseRefName isDraft mergeable mergeStateStatus body author { login }
    labels(first: 30) { nodes { name } }
    reviewRequests(first: 20) { nodes { requestedReviewer { ... on User { login } ... on Team { name } ... on Bot { login } } } }
    reviews(last: 100) { nodes { author { login } state submittedAt url body commit { oid } } }
    comments(last: 60) { nodes { author { login } createdAt url } }
    history: commits(last: 30) { nodes { commit { oid committedDate parents(first: 2) { totalCount nodes { oid } } } } }
    reqEvents: timelineItems(last: 30, itemTypes: [REVIEW_REQUESTED_EVENT]) { nodes { ... on ReviewRequestedEvent { createdAt requestedReviewer { ... on Bot { login } ... on User { login } } } } }
    commits(last: 1) { nodes { commit { committedDate statusCheckRollup { contexts(first: 100) { nodes { __typename
        ... on CheckRun { name status conclusion detailsUrl isRequired(pullRequestNumber: $n) }
        ... on StatusContext { context state targetUrl isRequired(pullRequestNumber: $n) } } } } } } }
    reviewThreads(first: 100) { pageInfo { hasNextPage endCursor } nodes { isResolved isOutdated path line comments(first: 50) { nodes { author { login } createdAt url } } } }
} } }`;
const THREADS_PAGE = `query($owner: String!, $name: String!, $n: Int!, $after: String!) { repository(owner: $owner, name: $name) { pullRequest(number: $n) {
    reviewThreads(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { isResolved isOutdated path line comments(first: 50) { nodes { author { login } createdAt url } } } }
} } }`;

const requiredCache = new Map();
// The base branch's required status checks, from rulesets and classic branch protection. null = couldn't read.
export async function fetchRequiredChecks(repo, base) {
    const key = `${repo}#${base}`;
    const hit = requiredCache.get(key);
    if (hit && Date.now() - hit.at < 10 * 60_000) return hit.value;
    let value = null;
    try {
        const rules = JSON.parse(await gh(["api", `repos/${repo}/rules/branches/${encodeURIComponent(base)}`]));
        value = [...new Set(rules.filter((r) => r.type === "required_status_checks").flatMap((r) => (r.parameters?.required_status_checks || []).map((c) => c.context)))];
        try {
            const prot = JSON.parse(await gh(["api", `repos/${repo}/branches/${encodeURIComponent(base)}/protection/required_status_checks`]));
            for (const c of prot.contexts || []) if (!value.includes(c)) value.push(c);
        } catch { /* no classic protection, or no permission to read it */ }
    } catch { value = null; }
    requiredCache.set(key, { value, at: Date.now() });
    return value;
}

// Everything the readiness gates read, for one PR. Paginates every review thread.
export async function fetchReadinessRaw(repo, number) {
    const [owner, name] = repo.split("/");
    const q = async (query, extra = []) => {
        const d = JSON.parse(await gh(["api", "graphql", "-f", `query=${query}`, "-f", `owner=${owner}`, "-f", `name=${name}`, "-F", `n=${number}`, ...extra], { timeoutMs: 60000 }));
        const pr = d.data?.repository?.pullRequest;
        if (!pr) throw new Error(d.errors?.[0]?.message || `PR #${number} not found`);
        return pr;
    };
    const pr = await q(READY_QUERY);
    const threads = [...pr.reviewThreads.nodes];
    let page = pr.reviewThreads.pageInfo;
    for (let i = 0; page.hasNextPage && i < 20; i++) {
        const more = await q(THREADS_PAGE, ["-f", `after=${page.endCursor}`]);
        threads.push(...more.reviewThreads.nodes);
        page = more.reviewThreads.pageInfo;
    }
    let behind = null;
    try { behind = Number(await gh(["api", `repos/${repo}/compare/${encodeURIComponent(pr.baseRefName)}...${pr.headRefOid}`, "--jq", ".behind_by"])); } catch { behind = null; }
    // The latest commit that isn't only a merge of the base branch ("Update branch"). Reviews of it still count.
    const hist = (pr.history?.nodes || []).map((n) => n.commit);
    const baseMerges = [];
    let at = hist.length - 1;
    while (at > 0 && hist[at].parents.totalCount >= 2 && baseMerges.length < 5) {
        const second = hist[at].parents.nodes[1]?.oid;
        let inBase = false;
        try { inBase = ["behind", "identical"].includes((await gh(["api", `repos/${repo}/compare/${encodeURIComponent(pr.baseRefName)}...${second}`, "--jq", ".status"])).trim()); } catch { inBase = false; }
        if (!inBase) break;
        baseMerges.push(hist[at].oid);
        at--;
    }
    const reviewHead = hist[at]?.oid || pr.headRefOid;
    const required = await fetchRequiredChecks(repo, pr.baseRefName);
    // Which "#n" in the body or branch name are issues (not PRs), for the description check.
    const bodyNums = [...String(pr.body || "").replace(/<!--[\s\S]*?-->/g, "").matchAll(/(?<![\w/])#(\d{2,6})\b/g)].map((m) => Number(m[1]));
    const branchNum = Number((String(pr.headRefName || "").match(/(?:^|[-/_])(\d{3,6})(?:[-/_]|$)/) || [])[1]) || null;
    const nums = [...new Set([...bodyNums, branchNum])].filter((n) => n && n !== number).slice(0, 6);
    const issueNums = [];
    if (nums.length) {
        try {
            const sel = nums.map((n) => `n${n}: issueOrPullRequest(number: ${n}) { __typename }`).join(" ");
            const d = JSON.parse(await gh(["api", "graphql", "-f", `query=query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { ${sel} } }`, "-f", `owner=${owner}`, "-f", `name=${name}`]));
            for (const n of nums) if (d.data?.repository?.[`n${n}`]?.__typename === "Issue") issueNums.push(n);
        } catch { /* no suggestion beats a wrong one */ }
    }
    return { ...pr, threads, behind, required, issueNums, reviewHead, reviewShas: [reviewHead, ...baseMerges], fetchedAt: new Date().toISOString() };
}

// Ask Copilot to review the PR's current head: the Copilot code review endpoint first, then
// `gh pr edit --add-reviewer @copilot`. "Already pending" counts as done.
export async function requestCopilotReview(repo, number) {
    const res = (out) => ({ ok: true, ...out });
    let first;
    try { await gh(["api", "-X", "POST", `repos/${repo}/pulls/${number}/copilot-code-review`]); return res({ how: "copilot-code-review" }); }
    catch (e) {
        if (/\b409\b/.test(e.message)) return res({ how: "already pending" });
        first = e.message.split("\n")[0].slice(0, 120);
    }
    try { await gh(["pr", "edit", String(number), "-R", repo, "--add-reviewer", "@copilot"]); return res({ how: "gh pr edit --add-reviewer @copilot" }); }
    catch (e) { return { ok: false, error: `${e.message.split("\n")[0].slice(0, 120)} (API: ${first})` }; }
}

const COPILOT = /^(copilot-pull-request-reviewer|copilot)(\[bot\])?$/i;
const LIFECYCLE = ["agent-preparing", "agent-blocked", "ready-for-review"];
const PASS_CHECK = new Set(["SUCCESS"]);
const SOFT_CHECK = new Set(["NEUTRAL", "SKIPPED"]);

// Readiness for the PR's current head. Four core gates decide "ready for human review"; the rest are extras:
// pr-review (recommended, or a decision on blocking findings), the description (a warning), the label (info).
const BLOCKING_SEV = new Set(["critical", "high", "medium"]);
export const isBlockingFinding = (f) => BLOCKING_SEV.has(f.severity) && !["nit", "question"].includes(f.category) && !f.outOfScope;
export function computeReadiness(raw, local = {}, ctx = {}) {
    if (!raw) return null;
    const head = raw.headRefOid;
    const author = raw.author?.login;
    const short = (s) => String(s || "").slice(0, 7);
    const gates = [], extras = [];
    const gate = (id, label, state, reason, url) => gates.push({ id, label, state, reason, url: url || null });

    // Reviews count when they cover the latest non-merge commit (or a base-only merge after it).
    const reviewHead = raw.reviewHead || head;
    const covers = (sha) => (raw.reviewShas || [head]).includes(sha);
    const keepReady = !!ctx.keepReady;

    // Copilot review of the latest non-merge commit, with no unresolved Copilot threads.
    const cop = raw.reviews.nodes.filter((r) => COPILOT.test(r.author?.login || ""));
    const copNow = cop.filter((r) => covers(r.commit?.oid));
    const copOpen = raw.threads.filter((t) => !t.isResolved && !t.isOutdated && COPILOT.test(t.comments.nodes[0]?.author?.login || ""));
    const copRequested = raw.reviewRequests.nodes.some((r) => COPILOT.test(r.requestedReviewer?.login || ""));
    const copReqAt = maxIso(...(raw.reqEvents?.nodes || []).filter((e) => COPILOT.test(e.requestedReviewer?.login || "")).map((e) => e.createdAt));
    const boardAsked = local.copilotAsked?.sha === reviewHead ? local.copilotAsked : null;
    const ago = (iso) => { const m = Math.max(1, Math.round((Date.now() - ts(iso)) / 60000)); return m < 60 ? `${m}m ago` : m < 2880 ? `${Math.round(m / 60)}h ago` : `${Math.round(m / 1440)}d ago`; };
    if (copOpen.length) gate("copilot", "Copilot review", "fail", `${copOpen.length} unresolved Copilot comment${copOpen.length === 1 ? "" : "s"}`, copOpen[0].comments.nodes[0]?.url);
    else if (copNow.length) gate("copilot", "Copilot review", "pass", `Reviewed ${short(copNow.at(-1).commit?.oid)}: clean`, copNow.at(-1).url);
    else if (copRequested) gate("copilot", "Copilot review", "pending", `Requested${copReqAt ? ` ${ago(copReqAt)}` : ""} (for ${short(reviewHead)})`);
    else if (boardAsked?.ok) gate("copilot", "Copilot review", "pending", `Requested by the board ${ago(boardAsked.at)} (for ${short(reviewHead)})`);
    else if (boardAsked && !boardAsked.ok) gate("copilot", "Copilot review", "fail", `The board couldn't request it: ${boardAsked.error || "unknown error"}`);
    else if (cop.length) gate("copilot", "Copilot review", keepReady ? "pending" : "fail", `Reviewed an older commit (${short(cop.at(-1).commit?.oid)}); ${keepReady ? `the board will request ${short(reviewHead)}` : `not requested for ${short(reviewHead)}`}`, cop.at(-1)?.url);
    else gate("copilot", "Copilot review", keepReady ? "pending" : "fail", keepReady ? "Not requested yet; the board will request it" : "Not requested");

    // Discussion: every thread resolved or replied to, and no unanswered top-level request.
    const human = (l) => l && l !== author && !COPILOT.test(l) && !BOT_LOGINS.test(l);
    const authorAt = maxIso(raw.commits.nodes[0]?.commit?.committedDate, ...raw.comments.nodes.filter((c) => c.author?.login === author).map((c) => c.createdAt),
        ...raw.threads.flatMap((t) => t.comments.nodes.filter((c) => c.author?.login === author).map((c) => c.createdAt)));
    const unreplied = raw.threads.filter((t) => !t.isResolved && !t.isOutdated && !COPILOT.test(t.comments.nodes[0]?.author?.login || "") && t.comments.nodes.at(-1)?.author?.login !== author);
    const asks = [
        ...raw.comments.nodes.filter((c) => human(c.author?.login)).map((c) => ({ who: c.author.login, at: c.createdAt, url: c.url, what: "commented" })),
        ...raw.reviews.nodes.filter((r) => human(r.author?.login) && (r.state === "CHANGES_REQUESTED" || (r.state === "COMMENTED" && r.body?.trim()))).map((r) => ({ who: r.author.login, at: r.submittedAt, url: r.url, what: r.state === "CHANGES_REQUESTED" ? "requested changes" : "commented" })),
    ].filter((x) => ts(x.at) > ts(authorAt)).sort((a, b) => ts(b.at) - ts(a.at));
    if (unreplied.length) gate("discussion", "Discussion", "fail", `${unreplied.length} review thread${unreplied.length === 1 ? "" : "s"} with no reply`, unreplied[0].comments.nodes[0]?.url);
    else if (asks.length) gate("discussion", "Discussion", "fail", `${asks[0].who} ${asks[0].what} after the last update; no answer yet`, asks[0].url);
    else gate("discussion", "Discussion", "pass", raw.threads.length ? `All ${raw.threads.length} thread${raw.threads.length === 1 ? "" : "s"} resolved or answered` : "No open threads or requests");

    // Required CI: every required check green on this head. Missing required checks are pending, never a pass.
    const ctxs = (raw.commits.nodes[0]?.commit?.statusCheckRollup?.contexts?.nodes || []).map((c) => ({ name: c.name || c.context, state: c.__typename === "StatusContext" ? c.state : c.status === "COMPLETED" ? c.conclusion : c.status, url: c.detailsUrl || c.targetUrl || null, required: !!c.isRequired }));
    const reqNames = [...new Set([...(raw.required || []), ...ctxs.filter((c) => c.required).map((c) => c.name)])];
    if (!reqNames.length) gate("ci", "Required CI", raw.required === null ? "pending" : "pass", raw.required === null ? "Couldn't read the required checks" : "No required checks on this branch");
    else {
        const rows = reqNames.map((n) => ctxs.find((c) => c.name === n) || { name: n, state: "MISSING" });
        const bad = rows.filter((r) => !PASS_CHECK.has(r.state) && !SOFT_CHECK.has(r.state) && !PENDING.has(r.state) && r.state !== "MISSING");
        const wait = rows.filter((r) => PENDING.has(r.state) || r.state === "MISSING");
        const soft = rows.filter((r) => SOFT_CHECK.has(r.state));
        if (bad.length) gate("ci", "Required CI", "fail", `${bad.map((r) => r.name).join(", ")} ${bad[0].state.toLowerCase().replace(/_/g, " ")}`, bad[0].url);
        else if (wait.length) gate("ci", "Required CI", "pending", wait.map((r) => `${r.name} ${r.state === "MISSING" ? "hasn't started" : "is running"}`).join(", "), wait[0].url);
        else if (soft.length) gate("ci", "Required CI", "pending", `${soft.map((r) => r.name).join(", ")} was ${soft[0].state.toLowerCase()}; check that's expected`, soft[0].url);
        else gate("ci", "Required CI", "pass", `${rows.map((r) => r.name).join(", ")} passed on ${short(head)}`, rows[0].url);
    }

    // Up to date: no conflicts. Unknown mergeability is not ready.
    const behind = Number.isInteger(raw.behind) ? raw.behind : null;
    const behindText = behind === null ? "" : behind ? ` · ${behind} behind ${raw.baseRefName}` : ` · up to date with ${raw.baseRefName}`;
    if (raw.mergeable === "CONFLICTING" || raw.mergeStateStatus === "DIRTY") gate("uptodate", "Up to date", "fail", `Merge conflicts with ${raw.baseRefName}${behindText}`, `${raw.url}#partial-pull-merging`);
    else if (raw.mergeable !== "MERGEABLE") gate("uptodate", "Up to date", "pending", `GitHub is still checking for conflicts${behindText}`);
    else if (raw.mergeStateStatus === "BEHIND") gate("uptodate", "Up to date", "fail", `Branch rules require updating it${behindText}`, `${raw.url}#partial-pull-merging`);
    else gate("uptodate", "Up to date", "pass", `No conflicts${behindText}`);

    // pr-review: a core gate while "keep it ready" is on, otherwise recommended. Blocking findings without a fix or
    // disposition need a decision (or the agent fixes them while it keeps the PR ready).
    const rv = local.review, pv = local.prReview;
    const rvHead = rv?.headSha || pv?.headSha || null;
    const findings = { blocking: [], fixing: 0, other: 0 };
    const askedRerun = local.reviewAsked?.sha === reviewHead ? local.reviewAsked : null;
    let rvItem;
    if (!rv && !pv) rvItem = { state: keepReady ? "pending" : "todo", reason: askedRerun ? `Asked the agent to run it on ${short(reviewHead)} ${ago(askedRerun.at)}` : `Not run on ${short(reviewHead)} yet` };
    else if (rvHead && !covers(rvHead) && rvHead !== reviewHead) rvItem = { state: keepReady ? "pending" : "todo", reason: askedRerun ? `Asked the agent to rerun it on ${short(reviewHead)} ${ago(askedRerun.at)}` : `Ran on ${short(rvHead)}; not on ${short(reviewHead)} yet` };
    else {
        for (const f of rv?.findings || []) {
            const t = local.triage?.[f.id] || {};
            if (!isBlockingFinding(f) || t.decision === "ignore" || t.decision === "post") { findings.other++; continue; }
            if (t.fixRequestedAt) { findings.fixing++; continue; }
            findings.blocking.push({ id: f.id, title: f.title, severity: f.severity, category: f.category });
        }
        const n = rv?.findings?.length || 0;
        rvItem = findings.blocking.length
            ? { state: keepReady ? "fail" : "decide", reason: `${findings.blocking.length} finding${findings.blocking.length === 1 ? "" : "s"} to fix or mark not needed`, items: findings.blocking }
            : { state: "pass", reason: n ? `Ran on ${short(rvHead || reviewHead)}; ${findings.fixing ? `${findings.fixing} being fixed, ` : ""}nothing blocking among ${n} finding${n === 1 ? "" : "s"}` : `Ran on ${short(rvHead || reviewHead)}${rv ? "; no findings" : ""}` };
    }
    if (keepReady) gates.splice(1, 0, { id: "review", label: "pr-review", url: null, ...rvItem });
    else extras.push({ id: "review", label: "pr-review", ...rvItem });

    // Extra: the description. Warn only when there's an issue it plausibly fixes but doesn't link.
    const body = String(raw.body || "").replace(/<!--[\s\S]*?-->/g, "").replace(/\r\n/g, "\n");
    const fixes = body.match(/\b(?:fix(?:es|ed)?|close[sd]?|resolve[sd]?)\s+(?:#(\d+)|https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/issues\/(\d+))/i);
    const saysNone = /\bno (?:linked |related |tracking )?issue\b|\bnot tied to an issue\b|\bwithout an issue\b/i.test(body);
    const candidate = (ctx.linkedIssues || [])[0] || (raw.issueNums || [])[0] || null;
    if (fixes) extras.push({ id: "description", label: "Description", state: "pass", reason: `Links #${fixes[1] || fixes[2]}` });
    else if (saysNone) extras.push({ id: "description", label: "Description", state: "pass", reason: "Says there's no related issue" });
    else if (candidate) extras.push({ id: "description", label: "Description", state: "warn", reason: `Looks related to #${candidate} but doesn't say "Fixes #${candidate}"`, issue: candidate, url: raw.url });
    else extras.push({ id: "description", label: "Description", state: "pass", reason: "No related issue found" });

    // Extra: the pr-lifecycle label, for information. It should agree with the core gates.
    const label = LIFECYCLE.find((l) => raw.labels.nodes.some((x) => x.name === l)) || null;
    const passed = gates.filter((g) => g.state === "pass").length;
    const ready = passed === gates.length;
    const outOfDate = (label === "ready-for-review" && !ready) ? "says ready-for-review, but a core gate isn't green"
        : (label && label !== "ready-for-review" && ready) ? `still says ${label}, but every core gate is green` : null;
    extras.push({ id: "label", label: "Lifecycle label", state: "info", reason: `${label || "none"}${outOfDate ? ` · out of date: ${outOfDate}` : ""}` });

    const humans = raw.reviewRequests.nodes.map((r) => r.requestedReviewer?.login || r.requestedReviewer?.name).filter((l) => l && !COPILOT.test(l));
    return { head, reviewHead, keepReady, label, gates, extras, passed, total: gates.length, ready, allGreen: ready, outOfDate, findings, requestedHumans: humans,
        copilot: { covered: copNow.length > 0, requested: copRequested, open: copOpen.length }, reviewCovered: rvItem.state === "pass" || !!rvItem.items, fetchedAt: raw.fetchedAt };
}
