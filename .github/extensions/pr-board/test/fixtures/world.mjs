// A small, fake GitHub repo for the UI tests. Each PR and issue covers one board scenario. Times are relative
// to when the world is created, so durations ("20m", "3 hours") don't drift between runs.
//
//   #101 yours      keep-ready on, every gate green, reviewer zed requested   -> "Waiting on zed"
//   #102 yours      CI failing, Copilot reviewed an older commit, agent working, unread reply, long thread
//   #103 other      someone else's PR, failing check                           -> "You: review (note: 1 failing check)"
//   #104 reviewing  structured review with a blocking finding (f1) and a nit (f2)
//   #105 reviewing  structured review with 0 findings (approve), plus agent choices for you
//   #106 other      approved, in the merge queue at #1
//   #107 yours      approved, CI green                                         -> ready to ship
//   #108 yours      draft that fixes Issue #201 (the issue gets no row of its own)
//   #90  merged     merged 2 hours ago
//   Issue #201      covered by #108
//   Issue #202      assigned to you, no PR
//   Issue #203      a session's branch with no PR yet (private branch review)
import { isGhWrite } from "../harness/fake-child-process.mjs";

export const REPO = "contoso/widgets";
export const VIEWER = "me";

const sha = (n, salt = "") => (String(n) + salt + "0123456789abcdef".repeat(3)).replace(/[^0-9a-f]/g, "a").slice(0, 40);
export const HEAD = { 101: sha(101), 102: sha(102), 103: sha(103), 104: sha(104), 105: sha(105), 106: sha(106), 107: sha(107), 108: sha(108), 203: sha(203) };
const OLD = { 102: sha(102, "f") };

export function createWorld({ now = Date.now() } = {}) {
    const ago = (min) => new Date(now - min * 60_000).toISOString();
    const url = (kind, n) => `https://github.com/${REPO}/${kind}/${n}`;
    const check = (name, state, min = 30) => ({
        __typename: "CheckRun", name, workflowName: "Build", detailsUrl: `https://github.com/${REPO}/actions/runs/1/job/${name.length}`,
        status: state === "IN_PROGRESS" ? "IN_PROGRESS" : "COMPLETED", conclusion: state === "IN_PROGRESS" ? "" : state, startedAt: ago(min + 10), completedAt: state === "IN_PROGRESS" ? "0001-01-01T00:00:00Z" : ago(min),
    });
    const review = (login, state, min, commit) => ({ id: "", author: { login }, state, submittedAt: ago(min), body: "", commit: { oid: commit || "" } });

    const prs = [
        { number: 101, title: "Add retry to the uploader", author: VIEWER, labels: ["ready-for-review"], requested: ["zed"], requestedMin: 20, checks: [check("build-and-package", "SUCCESS"), check("lint", "SUCCESS")],
            reviews: [review("copilot-pull-request-reviewer", "COMMENTED", 40, HEAD[101])], body: "Retries failed uploads three times.\n\nNo related issue.", created: 600, updated: 20 },
        { number: 102, title: "Fix crash on empty config", author: VIEWER, labels: ["agent-preparing"], checks: [check("build-and-package", "FAILURE"), check("lint", "SUCCESS")],
            reviews: [review("copilot-pull-request-reviewer", "COMMENTED", 300, OLD[102])], body: "Stops the crash when `config.json` is empty.\n\nNo related issue.", created: 900, updated: 30 },
        { number: 103, title: "Speed up the thumbnail cache", author: "alice", checks: [check("build-and-package", "FAILURE")], body: "Caches thumbnails in memory.", created: 300, updated: 60 },
        { number: 104, title: "Rework auth token refresh", author: "bob", checks: [check("build-and-package", "SUCCESS")], body: "Refreshes tokens before they expire.", created: 2000, updated: 120 },
        { number: 105, title: "Bump the logging library", author: "carol", checks: [check("build-and-package", "SUCCESS")], body: "Patch update.", created: 400, updated: 90 },
        { number: 106, title: "Docs: fix typos in the setup guide", author: "dave", reviewDecision: "APPROVED", checks: [check("build-and-package", "SUCCESS")],
            reviews: [review("zed", "APPROVED", 50, HEAD[106])], body: "Typos.", created: 500, updated: 15 },
        { number: 107, title: "Add CSV export", author: VIEWER, reviewDecision: "APPROVED", labels: ["ready-for-review"], checks: [check("build-and-package", "SUCCESS")],
            reviews: [review("zed", "APPROVED", 25, HEAD[107]), review("copilot-pull-request-reviewer", "COMMENTED", 70, HEAD[107])], body: "Exports the table as CSV.\n\nNo related issue.", created: 700, updated: 25 },
        { number: 108, title: "Handle 0-byte uploads", author: VIEWER, isDraft: true, headRefName: "fix-201-zero-bytes", checks: [check("build-and-package", "IN_PROGRESS")], closes: [201],
            body: "Fixes #201", created: 200, updated: 10 },
    ].map((p) => ({ headRefName: `branch-${p.number}`, reviewDecision: "REVIEW_REQUIRED", isDraft: false, labels: [], requested: [], reviews: [], ...p }));
    const merged = [{ number: 90, title: "Remove the old updater", author: "alice", mergedAt: ago(120), createdAt: ago(3000), additions: 3, deletions: 40, changedFiles: 2, latestReviews: [], body: "Removes it.", url: url("pull", 90) }];
    const queue = [{ position: 1, state: "AWAITING_CHECKS", enqueuedAt: ago(8), pullRequest: { number: 106 } }];
    const issues = {
        201: { number: 201, title: "Uploads of empty files fail", author: { login: "frank" }, state: "OPEN", closedByPullRequestsReferences: [{ number: 108, repository: { name: "widgets", owner: { login: "contoso" } } }], assignees: [{ login: VIEWER }], labels: [{ name: "bug" }], created: 3000, updated: 200 },
        202: { number: 202, title: "Dark mode icons are blurry", author: { login: "gina" }, state: "OPEN", closedByPullRequestsReferences: [], assignees: [{ login: VIEWER }], labels: [], created: 5000, updated: 400 },
        203: { number: 203, title: "Settings don't persist after restart", author: { login: "hank" }, state: "OPEN", closedByPullRequestsReferences: [], assignees: [], labels: [{ name: "bug" }], created: 4000, updated: 300 },
    };
    const issueJson = (i) => ({ number: i.number, title: i.title, url: url("issues", i.number), state: i.state, updatedAt: ago(i.updated), createdAt: ago(i.created), closedAt: null, author: i.author, labels: i.labels, assignees: i.assignees, closedByPullRequestsReferences: i.closedByPullRequestsReferences, comments: i.comments || [] });

    const listJson = (p) => ({
        number: p.number, title: p.title, url: url("pull", p.number), author: { login: p.author, is_bot: false }, isDraft: p.isDraft, additions: 40, deletions: 12, changedFiles: 3,
        reviewDecision: p.reviewDecision, mergeStateStatus: "BLOCKED", mergeable: "MERGEABLE", statusCheckRollup: p.checks, latestReviews: p.reviews,
        reviewRequests: p.requested.map((login) => ({ __typename: "User", login })), labels: p.labels.map((name) => ({ name })), updatedAt: ago(p.updated), createdAt: ago(p.created),
        autoMergeRequest: null, baseRefName: "main", body: p.body, headRefOid: (p.head || HEAD[p.number]),
    });
    const activityNode = (p) => ({
        number: p.number, mergeStateStatus: "BLOCKED", autoMergeRequest: null, commits: { nodes: [{ commit: { committedDate: ago(p.updated + 5) } }] },
        timelineItems: { nodes: [{ __typename: "PullRequestCommit", url: `${url("pull", p.number)}/commits/${HEAD[p.number] || p.head}`, commit: { oid: HEAD[p.number] || p.head, committedDate: ago(p.updated + 5), messageHeadline: "Make the change" } }, ...(p.requestedMin ? [{ __typename: "ReviewRequestedEvent", createdAt: ago(p.requestedMin) }] : []), ...p.reviews.map((r, k) => ({ __typename: "PullRequestReview", id: `PRR_${p.number}_${k}`, url: `${url("pull", p.number)}#pullrequestreview-${k}`, bodyText: "", ...r })), ...(p.events || [])] },
    });
    const readinessJson = (p) => ({
        number: p.number, url: url("pull", p.number), headRefOid: (p.head || HEAD[p.number]), headRefName: p.headRefName, baseRefName: "main", isDraft: p.isDraft, mergeable: "MERGEABLE", mergeStateStatus: "BLOCKED", body: p.body,
        author: { login: p.author }, labels: { nodes: p.labels.map((name) => ({ name })) },
        reviewRequests: { nodes: p.requested.map((login) => ({ requestedReviewer: { login } })) },
        reviews: { nodes: p.reviews.map((r) => ({ ...r, url: `${url("pull", p.number)}#pullrequestreview-${p.number}` })) },
        comments: { nodes: [] },
        history: { nodes: [{ commit: { oid: (p.head || HEAD[p.number]), committedDate: ago(p.updated + 5), parents: { totalCount: 1, nodes: [{ oid: sha(p.number, "p") }] } } }] },
        reqEvents: { nodes: p.requestedMin ? p.requested.map((login) => ({ createdAt: ago(p.requestedMin), requestedReviewer: { login } })) : [] },
        commits: { nodes: [{ commit: { committedDate: ago(p.updated + 5), statusCheckRollup: { contexts: { nodes: p.checks.map((c) => ({ __typename: "CheckRun", name: c.name, status: c.status, conclusion: c.conclusion, detailsUrl: c.detailsUrl, isRequired: c.name === "build-and-package" })) } } } }] },
        reviewThreads: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: [] },
    });
    // The GitHub tab. The PR description carries markup the board must never run.
    const prView = (p) => ({
        number: p.number, title: p.title, url: url("pull", p.number), state: "OPEN", isDraft: p.isDraft, createdAt: ago(p.created), updatedAt: ago(p.updated),
        bodyHTML: `<p>${p.body.split("\n")[0]}</p><img src="x" onerror="window.__xss=1"><script>window.__xss=2</script><a href="javascript:window.__xss=3">bad link</a>`,
        baseRefName: "main", headRefName: p.headRefName, author: { login: p.author, avatarUrl: null },
        labels: { nodes: p.labels.map((name) => ({ name, color: "ededed" })) }, assignees: { nodes: [] },
        reviewRequests: { nodes: p.requested.map((login) => ({ requestedReviewer: { login } })) },
        closingIssuesReferences: { nodes: (p.closes || []).map((n) => ({ number: n, title: issues[n].title, url: url("issues", n), state: "OPEN" })) },
        commits: { nodes: [{ commit: { statusCheckRollup: { contexts: { nodes: p.checks.map((c) => ({ __typename: "CheckRun", name: c.name, status: c.status, conclusion: c.conclusion, detailsUrl: c.detailsUrl, startedAt: c.startedAt, completedAt: c.completedAt, checkSuite: { workflowRun: { workflow: { name: "Build" } } } })) } } } }] },
        reviewThreads: { nodes: p.number === 102 ? [{ isResolved: false, isOutdated: false, path: "src/config.js", line: 12, originalLine: 12, comments: { nodes: [{ author: { login: "zed", avatarUrl: null }, createdAt: ago(100), bodyHTML: "<p>Why not default to <code>{}</code>?</p>", url: `${url("pull", 102)}#discussion_r777`, diffHunk: "@@ -10,3 +10,4 @@\n const raw = read();\n+if (!raw) return {};", pullRequestReview: { id: "R1" } }] } }] : [] },
        timelineItems: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: [
            { __typename: "PullRequestCommit", url: `${url("pull", p.number)}/commits/${(p.head || HEAD[p.number])}`, commit: { abbreviatedOid: (p.head || HEAD[p.number]).slice(0, 7), messageHeadline: "Make the change", committedDate: ago(p.updated + 5), author: { name: p.author, user: { login: p.author, avatarUrl: null } } } },
            { __typename: "IssueComment", author: { login: "zed", avatarUrl: null }, createdAt: ago(p.updated + 2), bodyHTML: "<p>Looks reasonable. <img src=x onerror=\"window.__xss=4\"></p>", url: `${url("pull", p.number)}#issuecomment-1` },
            ...(p.number === 102 ? [{ __typename: "PullRequestReview", id: "R1", author: { login: "zed", avatarUrl: null }, submittedAt: ago(100), createdAt: ago(100), state: "COMMENTED", bodyHTML: "", url: `${url("pull", 102)}#pullrequestreview-1` }] : []),
        ] },
    });
    const issueView = (i) => ({
        number: i.number, title: i.title, url: url("issues", i.number), state: i.state, stateReason: null, createdAt: ago(i.created), updatedAt: ago(i.updated), bodyHTML: `<p>${i.title}.</p>`, author: { login: i.author.login, avatarUrl: null },
        labels: { nodes: i.labels.map((l) => ({ name: l.name, color: "d73a4a" })) }, assignees: { nodes: i.assignees.map((a) => ({ login: a.login })) },
        closedByPullRequestsReferences: { nodes: i.closedByPullRequestsReferences.map((r) => ({ number: r.number, title: prs.find((p) => p.number === r.number)?.title || "", url: url("pull", r.number), state: "OPEN", isDraft: true })) },
        timelineItems: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: [{ __typename: "IssueComment", author: { login: "frank", avatarUrl: null }, createdAt: ago(i.updated), bodyHTML: "<p>Still happens.</p>", url: `${url("issues", i.number)}#issuecomment-2` }] },
    });
    const branchCompare = {
        html_url: `https://github.com/${REPO}/compare/main...fix-203-settings`, status: "ahead", ahead_by: 2, behind_by: 1,
        commits: [{ sha: sha(203, "a"), html_url: "", author: { login: VIEWER }, commit: { message: "Save settings on exit", author: { date: ago(90) } } }, { sha: HEAD[203], html_url: "", author: { login: VIEWER }, commit: { message: "Load saved settings", author: { date: ago(45) } } }],
        files: [{ filename: "src/settings.js", status: "modified", additions: 4, deletions: 1, blob_url: "", patch: "@@ -1,4 +1,7 @@\n import fs from \"fs\";\n-export const load = () => ({});\n+export function load() {\n+  if (!fs.existsSync(FILE)) return {};\n+  return JSON.parse(fs.readFileSync(FILE, \"utf8\"));\n+}\n export const FILE = \"settings.json\";" }],
    };

    const world = {
        now, prs, merged, queue, issues, viewer: VIEWER,
        mutations: [], unmocked: [], calls: 0,
        fault: null, // { match(args) -> bool, error?: string, delayMs?: number }
        // Someone comments on an issue, or on a PR's conversation.
        comment(n, login, body) {
            const at = new Date().toISOString();
            const id = `IC_${n}_${Math.random().toString(16).slice(2, 8)}`;
            const issue = world.issues[n];
            if (issue) { (issue.comments ||= []).push({ id, author: { login, is_bot: /\[bot\]$/.test(login) }, body, createdAt: at, url: `${url("issues", n)}#issuecomment-${id}` }); issue.updated = 0; return id; }
            const p = world.prs.find((x) => x.number === n);
            (p.events ||= []).push({ __typename: "IssueComment", id, url: `${url("pull", n)}#issuecomment-${id}`, author: { login }, createdAt: at, bodyText: body });
            p.updated = 0;
            return id;
        },
        // A new PR shows up (author, CI state, who's asked to review).
        addPr({ number, title, author, requested = [], ci = "SUCCESS" }) {
            world.prs.push({ number, title, author, requested, isDraft: false, reviewDecision: "REVIEW_REQUIRED", labels: [], reviews: [], headRefName: `branch-${number}`, head: sha(number), body: "New PR.", created: 30, updated: 20, checks: [check("build-and-package", ci)] });
        },
        otherMerges: new Set(), // second parents of merges that bring in more than the base branch
        // The author merges main into the PR ("Update branch"): a merge commit whose second parent is on main.
        mergeMain(n) { return world.merge(n, `m${n}`.padEnd(40, "0"), "Merge branch 'main' into the PR"); },
        // A merge of something that isn't on main yet (or with conflict fixes): it's real work.
        mergeOther(n) { const second = `e${n}`.padEnd(40, "0"); world.otherMerges.add(second); return world.merge(n, second, "Merge branch 'feature-x'"); },
        merge(n, second, message) {
            const p = world.prs.find((x) => x.number === n);
            const oid = sha(n, `merge${second[0]}${(p.events || []).length}`);
            (p.events ||= []).push({ __typename: "PullRequestCommit", url: `${url("pull", n)}/commits/${oid}`, commit: { oid, committedDate: new Date().toISOString(), messageHeadline: message, parents: { totalCount: 2, nodes: [{ oid: p.head || HEAD[n] }, { oid: second }] } } });
            p.head = oid;
            p.updated = 0;
            return oid;
        },
        // The PR's author pushes a commit.
        push(n, message = "Address the review") {
            const p = world.prs.find((x) => x.number === n);
            const oid = sha(n, `push${(p.events || []).length}`);
            (p.events ||= []).push({ __typename: "PullRequestCommit", url: `${url("pull", n)}/commits/${oid}`, commit: { oid, committedDate: new Date().toISOString(), messageHeadline: message } });
            p.head = oid;
            p.updated = 0;
            return oid;
        },
        allowMutations: false,
        async handle(args) {
            world.calls++;
            const f = world.fault;
            if (f && f.match(args)) {
                if (f.delayMs) await new Promise((r) => setTimeout(r, f.delayMs));
                if (f.error) throw new Error(f.error);
            }
            const a = args.join(" ");
            const q = args.find((x) => x.startsWith("query=")) || "";
            const n = Number((args.find((x) => /^n=\d+$/.test(x)) || "n=0").slice(2));
            if (isGhWrite(args)) {
                world.mutations.push(args);
                if (!world.allowMutations) throw new Error(`test world refuses a GitHub write: gh ${a.slice(0, 120)}`);
                return "{}";
            }
            if (a === "api user --jq .login") return `${VIEWER}\n`;
            if (a === "repo view --json nameWithOwner --jq .nameWithOwner") return `${REPO}\n`;
            if (a.startsWith(`repo view ${REPO} --json defaultBranchRef`)) return "main\n";
            if (a.startsWith(`pr list -R ${REPO} --state open`)) return world.prs.map(listJson);
            if (a.startsWith(`pr list -R ${REPO} --state merged`)) return world.merged;
            if (a.startsWith(`pr list -R ${REPO} --head `)) { const b = args[args.indexOf("--head") + 1]; return world.prs.filter((p) => p.headRefName === b).map((p) => ({ number: p.number, state: "OPEN", isDraft: p.isDraft, url: url("pull", p.number) })); }
            if (/^pr view \d+ -R .* --json number,state,isDraft,url$/.test(a)) { const p = world.prs.find((x) => x.number === Number(args[2])); if (!p) throw new Error("not found"); return { number: p.number, state: "OPEN", isDraft: p.isDraft, url: url("pull", p.number) }; }
            if (/^pr view \d+ -R .* --json files$/.test(a)) return { files: [{ path: "src/main.js", additions: 30, deletions: 10 }, { path: "README.md", additions: 10, deletions: 2 }] };
            if (a.startsWith(`issue list -R ${REPO} --assignee @me`)) return Object.values(world.issues).filter((i) => i.assignees.some((x) => x.login === VIEWER)).map(issueJson);
            if (/^issue view \d+/.test(a)) { const i = world.issues[Number(args[2])]; if (!i) throw new Error("not found"); return issueJson(i); }
            if (a.startsWith(`api repos/${REPO}/branches/`) && a.endsWith("--jq .commit.sha")) return `${HEAD[203]}\n`;
            if (a === `api repos/${REPO}/rules/branches/main`) return [{ type: "required_status_checks", parameters: { required_status_checks: [{ context: "build-and-package" }] } }];
            if (a === `api repos/${REPO}/branches/main/protection/required_status_checks`) throw new Error("HTTP 404: Branch not protected");
            if (a.startsWith(`api repos/${REPO}/compare/`) && a.endsWith("--jq .behind_by")) return "0\n";
            if (a.startsWith(`api repos/${REPO}/compare/`) && a.endsWith("--jq .status")) return world.otherMerges.has(a.split("...")[1].split(" ")[0]) ? "diverged\n" : "behind\n";
            if (a === `api repos/${REPO}/compare/main...fix-203-settings`) return branchCompare;
            if (a.startsWith(`api repos/${REPO}/compare/ffffffff`)) throw new Error("HTTP 404: No common ancestor (the commit is gone)");
            if (a.startsWith(`api repos/${REPO}/compare/`)) return { status: "ahead", ahead_by: 1, commits: [{ sha: "c".repeat(40), author: { login: "bob" }, commit: { message: "Address review", author: { date: ago(30) } } }], files: [{ filename: "src/auth.js", status: "modified", additions: 5, deletions: 2 }] };
            const pullRe = new RegExp(`^api repos/${REPO}/pulls/(\\d+)(/reviews\\?per_page=100)?$`);
            if (pullRe.test(a)) { const [, num, revs] = a.match(pullRe); return revs ? [{ user: { login: VIEWER }, commit_id: sha(Number(num), "r"), submitted_at: ago(200) }] : { head: { sha: HEAD[Number(num)] } }; }
            if (a.startsWith("api graphql")) {
                if (q.includes("pullRequests(states:OPEN")) return { data: { repository: { pullRequests: { nodes: world.prs.map(activityNode) } } } };
                if (q.includes("mergeQueue(")) return { data: { repository: { mergeQueue: { entries: { nodes: world.queue } } } } };
                if (q.includes("issueOrPullRequest")) return { data: { repository: Object.fromEntries([...q.matchAll(/n(\d+): issueOrPullRequest/g)].map(([, k]) => [`n${k}`, { __typename: world.issues[k] ? "Issue" : "PullRequest" }])) } };
                if (q.includes("history: commits")) { const p = world.prs.find((x) => x.number === n); return { data: { repository: { pullRequest: p ? readinessJson(p) : null } } }; }
                if (q.includes("closingIssuesReferences")) { const p = world.prs.find((x) => x.number === n); return { data: { repository: { pullRequest: p ? prView(p) : null } } }; }
                if (q.includes("closedByPullRequestsReferences(first: 10")) { const i = world.issues[n]; return { data: { repository: { issue: i ? issueView(i) : null } } }; }
            }
            world.unmocked.push(args);
            throw new Error(`unmocked gh call: gh ${a.slice(0, 160)}`);
        },
    };
    return world;
}
