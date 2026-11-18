// Structured PR review: schema, agent guide, normalize/merge, and the GitHub review payload.
// Adapted from angelser's pr-arch-review, https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe
// (lib/guide.mjs and lib/review.mjs), trimmed to what the PR board shows.
import { createHash } from "node:crypto";

export const CATEGORIES = ["blocker", "performance", "architecture", "risk", "question", "nit"];
export const SEVERITIES = ["critical", "high", "medium", "low", "info"];
const STATUSES = ["added", "modified", "removed", "unchanged", "external"];
const KINDS = ["flow", "layers", "sequence"];
const MAX_NODES = 12;
export const AI_FOOTER = "🤖 _Generated with AI assistance (GitHub Copilot PR board). Reviewed by a human._";

const arr = (v) => (Array.isArray(v) ? v : []);
const status = { type: "string", enum: STATUSES };
const fileRef = {
    type: "object",
    description: "File pointer. line/endLine are on the PR head (right side).",
    properties: { path: { type: "string" }, line: { type: "integer" }, endLine: { type: "integer" }, note: { type: "string" } },
    required: ["path"],
};
const diagramRef = { type: "object", properties: { diagram: { type: "string" }, node: { type: "string" } }, required: ["diagram"] };

const diagram = {
    type: "object",
    description: "kind=flow (components and how they connect), layers (nodes grouped into tiers via `group`), or sequence (runtime call order). <=12 nodes / <=7 participants; prefer several small diagrams.",
    properties: {
        id: { type: "string" },
        title: { type: "string" },
        kind: { type: "string", enum: KINDS },
        direction: { type: "string", enum: ["TB", "LR"], description: "Omit to auto-pick the orientation that fits the panel." },
        description: { type: "string", description: "Markdown under the diagram: what to look at, in plain words." },
        groups: { type: "array", items: { type: "object", properties: { id: { type: "string" }, label: { type: "string" } }, required: ["id"] } },
        nodes: {
            type: "array",
            items: {
                type: "object",
                properties: {
                    id: { type: "string" },
                    label: { type: "string", description: "Short name (<=30 chars)." },
                    sublabel: { type: "string" },
                    kind: { type: "string", enum: ["component", "service", "datastore", "queue", "api", "ui", "external", "actor", "library"] },
                    status,
                    group: { type: "string" },
                    component: { type: "string", description: "Id of a components entry." },
                    description: { type: "string" },
                    files: { type: "array", items: fileRef },
                },
                required: ["id", "label"],
            },
        },
        edges: {
            type: "array",
            items: {
                type: "object",
                properties: { from: { type: "string" }, to: { type: "string" }, label: { type: "string" }, status, style: { type: "string", enum: ["sync", "async", "data", "dependency"] } },
                required: ["from", "to"],
            },
        },
        participants: { type: "array", items: { type: "object", properties: { id: { type: "string" }, label: { type: "string" }, sublabel: { type: "string" }, status }, required: ["id", "label"] } },
        messages: {
            type: "array",
            items: { type: "object", properties: { from: { type: "string" }, to: { type: "string" }, label: { type: "string" }, kind: { type: "string", enum: ["sync", "async", "return"] }, status, divider: { type: "string" }, note: { type: "string" } } },
        },
    },
    required: ["title"],
};

const finding = {
    type: "object",
    properties: {
        id: { type: "string", description: "Stable id; keep it when updating so triage survives." },
        category: { type: "string", enum: CATEGORIES },
        severity: { type: "string", enum: SEVERITIES },
        title: { type: "string", description: "Plain words, <=80 chars." },
        description: { type: "string", description: "Markdown: what's wrong and why it matters to users, in plain words first." },
        recommendation: { type: "string", description: "Markdown: the smallest concrete fix, or the question to ask the author." },
        confidence: { type: "string", enum: ["high", "medium", "low"] },
        files: { type: "array", items: fileRef },
        diagramRefs: { type: "array", items: diagramRef },
    },
    required: ["category", "title"],
};

export const REVIEW_SCHEMA = {
    type: "object",
    properties: {
        headSha: { type: "string", description: "The PR head commit you reviewed." },
        verdict: { type: "string", enum: ["approve", "approve-with-comments", "needs-discussion", "request-changes"] },
        risk: { type: "string", enum: ["low", "medium", "high"] },
        summary: { type: "string", description: "Markdown, 2-4 plain sentences: what the PR does for users and the 2-3 things that matter most." },
        functionality: { type: "array", description: "User-visible behavior changes.", items: { type: "object", properties: { id: { type: "string" }, title: { type: "string" }, detail: { type: "string" } }, required: ["title"] } },
        components: {
            type: "array",
            description: "Changes by system: each touched area with one plain line about what changed.",
            items: {
                type: "object",
                properties: { id: { type: "string" }, name: { type: "string" }, status, role: { type: "string", description: "One plain line: what changed in this system." }, description: { type: "string" }, files: { type: "array", items: fileRef } },
                required: ["name"],
            },
        },
        changedFiles: {
            type: "array",
            description: "Changes by file: one plain line per changed file, and the component it belongs to.",
            items: { type: "object", properties: { path: { type: "string" }, summary: { type: "string" }, component: { type: "string" } }, required: ["path"] },
        },
        diagrams: { type: "array", items: diagram },
        findings: { type: "array", items: finding },
    },
};

export const GUIDE = `# PR board structured review

The PR board shows your review as: Overview (summary, verdict, risk), Architecture (diagrams),
Changes (by system, then by file) and Review (findings the user triages and posts). Write for a
product manager first: plain words, what changes for the person using the product, then detail.

1. Gather: \`gh pr view <n> -R owner/repo --json title,body,headRefOid,files\` and \`gh pr diff <n> -R owner/repo\`.
   Read surrounding code for anything that crosses a component boundary. Read-only.
2. If the repo has a review skill (e.g. \`.github/skills/pr-review\`), follow it to find issues; its
   findings become \`findings\` here.
3. Fill the review (schema below):
   - summary, verdict, risk, functionality.
   - components: every touched system with \`role\` = one plain line about what changed, and its files.
   - changedFiles: one plain line per changed file, with its \`component\` id.
   - diagrams: a) kind=flow "How it fits together": touched components and neighbours, with
     status added/modified/removed/unchanged/external and short edge labels. b) kind=sequence for any
     new or changed runtime flow. <=12 nodes / <=7 participants. Link nodes to components with
     \`node.component\` and list their files.
   - findings: category, severity, plain title, description (what and why it matters), recommendation
     (smallest fix), files with line numbers on the PR head (so it can be posted inline), diagramRefs.
4. Push it: \`pr_board_set_review({ pr, review })\`, or for big PRs \`pr_board_update_review\` /
   \`pr_board_add_findings\` in steps. If you can't call pr_board_* tools (another session), write the
   review as JSON to a file and report its absolute path; the coordinator calls
   \`pr_board_set_review({ pr, path })\`. Read the returned warnings and fix them.
5. Keep finding ids stable when updating, so the user's Post / Ignore choices survive.
6. When the board asks you to explain a finding, read the code and call
   \`pr_board_set_explanation({ pr, findingId, text })\` with short Markdown.
7. Never post to GitHub, approve or merge yourself. The user picks findings to post and confirms.`;

function ensureIds(items, prefix) {
    const seen = new Set();
    let n = 1;
    for (const it of items) {
        if (!it.id || seen.has(it.id)) {
            while (seen.has(`${prefix}${n}`)) n++;
            it.id = `${prefix}${n}`;
        }
        seen.add(it.id);
    }
    return items;
}

const normFiles = (files) => arr(files).map((f) => (typeof f === "string" ? { path: f } : f)).filter((f) => f && typeof f.path === "string" && f.path);

/** Normalize in place; return warnings for the agent. */
export function normalizeReview(review) {
    const warnings = [];
    review.findings = ensureIds(arr(review.findings), "f");
    review.diagrams = ensureIds(arr(review.diagrams), "d");
    review.components = ensureIds(arr(review.components), "c");
    review.functionality = arr(review.functionality);
    review.changedFiles = arr(review.changedFiles).filter((f) => f && f.path);
    for (const f of review.findings) {
        if (!CATEGORIES.includes(f.category)) { warnings.push(`finding ${f.id}: unknown category "${f.category}", using "risk"`); f.category = "risk"; }
        if (!SEVERITIES.includes(f.severity)) f.severity = f.category === "blocker" ? "high" : "medium";
        f.files = normFiles(f.files);
        f.diagramRefs = arr(f.diagramRefs);
    }
    for (const c of review.components) c.files = normFiles(c.files);
    const nodeIds = new Map();
    for (const d of review.diagrams) {
        if (!KINDS.includes(d.kind)) d.kind = d.participants ? "sequence" : "flow";
        if (d.kind === "sequence") {
            d.participants = ensureIds(arr(d.participants), "p");
            d.messages = arr(d.messages);
            const ids = new Set(d.participants.map((p) => p.id));
            nodeIds.set(d.id, ids);
            for (const m of d.messages) if (!m.divider && (!ids.has(m.from) || !ids.has(m.to))) warnings.push(`diagram ${d.id}: message "${m.label ?? ""}" uses an unknown participant`);
            if (d.participants.length > 7) warnings.push(`diagram ${d.id}: ${d.participants.length} participants; keep <=7`);
        } else {
            d.nodes = ensureIds(arr(d.nodes), "n");
            d.groups = arr(d.groups);
            const ids = new Set(d.nodes.map((n) => n.id));
            nodeIds.set(d.id, ids);
            for (const n of d.nodes) { if (n.status && !STATUSES.includes(n.status)) n.status = "modified"; n.files = normFiles(n.files); }
            d.edges = arr(d.edges).filter((e) => {
                const ok = ids.has(e.from) && ids.has(e.to);
                if (!ok) warnings.push(`diagram ${d.id}: dropped edge ${e.from}->${e.to} (unknown node)`);
                return ok;
            });
            if (d.nodes.length > MAX_NODES) warnings.push(`diagram ${d.id}: ${d.nodes.length} nodes; split into diagrams of <=${MAX_NODES}`);
        }
    }
    for (const f of review.findings) {
        for (const r of f.diagramRefs) {
            if (!nodeIds.has(r.diagram)) warnings.push(`finding ${f.id}: unknown diagram "${r.diagram}"`);
            else if (r.node && !nodeIds.get(r.diagram).has(r.node)) warnings.push(`finding ${f.id}: unknown node "${r.node}" in ${r.diagram}`);
        }
        if (!f.files.some((x) => x.line)) warnings.push(`finding ${f.id}: no file line, so it can only be posted in the review summary`);
    }
    return warnings;
}

function mergeById(base, patch) {
    const out = base.map((x) => ({ ...x }));
    for (const item of patch) {
        const i = item?.id ? out.findIndex((x) => x.id === item.id) : -1;
        if (i >= 0) out[i] = { ...out[i], ...item };
        else out.push(item);
    }
    return out;
}

/** Objects shallow-merge, id'd arrays merge by id, everything else replaces. */
export function mergeReview(base, patch) {
    const out = { ...base };
    for (const [k, v] of Object.entries(patch || {})) {
        const cur = out[k];
        if (Array.isArray(v) && Array.isArray(cur) && v.every((x) => x && typeof x === "object")) out[k] = mergeById(cur, v);
        else if (v && typeof v === "object" && !Array.isArray(v) && cur && typeof cur === "object" && !Array.isArray(cur)) out[k] = { ...cur, ...v };
        else out[k] = v;
    }
    return out;
}

/** Copy of the review with a GitHub diff link on every file reference. */
export function withLinks(review, prUrl) {
    const r = structuredClone(review);
    const href = (f) => {
        const anchor = createHash("sha256").update(f.path).digest("hex");
        return `${prUrl}/files#diff-${anchor}${f.line ? `R${f.line}${f.endLine ? `-R${f.endLine}` : ""}` : ""}`;
    };
    const add = (files) => { for (const f of arr(files)) f.href = href(f); };
    for (const f of arr(r.findings)) add(f.files);
    for (const c of arr(r.components)) add(c.files);
    for (const d of arr(r.diagrams)) for (const n of arr(d.nodes)) add(n.files);
    for (const f of arr(r.changedFiles)) f.href = href(f);
    return r;
}

const SEV_ORDER = Object.fromEntries(SEVERITIES.map((s, i) => [s, i]));
const EVENTS = { approve: "APPROVE", request_changes: "REQUEST_CHANGES", comment: "COMMENT" };

function findingText(f, note) {
    const parts = [`**[${f.severity}] ${f.title}**`];
    if (f.description) parts.push(f.description.trim());
    if (f.recommendation) parts.push(`_Suggestion:_ ${f.recommendation.trim()}`);
    if (note) parts.push(`_Reviewer note:_ ${note.trim()}`);
    return parts.join("\n\n");
}

/**
 * Build exactly what will be posted: findings marked Post become inline comments when they
 * point at a line, and go in the summary body otherwise. The user's top note leads the body.
 */
export function composeGithubReview(review, triage, event, headSha) {
    const tri = triage || {};
    const posted = arr(review?.findings)
        .filter((f) => tri[f.id]?.decision === "post")
        .sort((a, b) => SEV_ORDER[a.severity] - SEV_ORDER[b.severity]);
    const comments = [];
    const inBody = [];
    for (const f of posted) {
        const at = f.files.find((x) => x.line);
        if (at) comments.push({ findingId: f.id, path: at.path, line: at.endLine || at.line, ...(at.endLine && at.endLine !== at.line ? { start_line: at.line, start_side: "RIGHT" } : {}), side: "RIGHT", body: `${findingText(f, tri[f.id]?.note)}\n\n${AI_FOOTER}` });
        else inBody.push(f);
    }
    const lines = [];
    const top = (tri._note?.note || "").trim();
    if (top) lines.push(top);
    if (inBody.length) {
        if (lines.length) lines.push("");
        for (const f of inBody) {
            const loc = f.files[0] ? ` (\`${f.files[0].path}\`)` : "";
            lines.push(`- ${findingText(f, tri[f.id]?.note).replace(/\n\n/g, "\n  ")}${loc}`);
        }
    }
    if (comments.length && !inBody.length && !top) lines.push(`${comments.length} comment${comments.length === 1 ? "" : "s"} inline.`);
    if (posted.length || top) lines.push("", AI_FOOTER);
    const body = lines.join("\n").trim();
    return {
        event: EVENTS[event],
        body,
        comments,
        commit_id: headSha || review?.headSha || undefined,
        counts: { posted: posted.length, inline: comments.length, inBody: inBody.length, ignored: arr(review?.findings).filter((f) => tri[f.id]?.decision === "ignore").length, undecided: arr(review?.findings).filter((f) => !tri[f.id]?.decision || tri[f.id].decision === "undecided").length },
    };
}
