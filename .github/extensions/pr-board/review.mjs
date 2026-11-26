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
        status: { type: "string", enum: ["open", "fixed", "declined"], description: "fixed = already fixed in the PR head (kept for the record; it doesn't block). declined = the session disagrees; the user decides. Default open." },
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
        tryIt: {
            type: "array",
            description: "3-6 commands the user can paste into a terminal that has this PR's winapp build first on PATH, to see the change for themselves. Commands you actually ran while reviewing.",
            items: { type: "object", properties: { title: { type: "string", description: "What it shows, in plain words." }, command: { type: "string", description: "One command line, using `winapp` (the PR's build)." }, expect: { type: "string", description: "What to look for in the result." } }, required: ["command"] },
        },
        media: {
            type: "array",
            description: "What the change looks like, so the user can see it without running anything. Files are absolute paths on this machine; the board copies them. kind=comparison: before (main) and after (this PR), as images, videos, or text transcripts for CLI output. kind=image or video: one file.",
            items: {
                type: "object",
                properties: {
                    kind: { type: "string", enum: ["comparison", "image", "video"] },
                    title: { type: "string" },
                    caption: { type: "string", description: "One plain line: what to notice." },
                    path: { type: "string", description: "kind image or video: absolute path to a .png/.jpg/.gif/.webp or .mp4/.webm. A .txt/.log file is shown as text." },
                    before: { type: "string", description: "kind comparison: absolute path to the main-branch image or video, or a .txt/.log transcript (read as beforeText)." },
                    after: { type: "string", description: "kind comparison: absolute path to this PR's image or video, or a .txt/.log transcript (read as afterText)." },
                    beforeText: { type: "string", description: "kind comparison: main's terminal output, inline, for CLI changes." },
                    afterText: { type: "string", description: "kind comparison: this PR's terminal output, inline." },
                },
                required: ["kind"],
            },
        },
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
   - tryIt: 3-6 commands you ran while reviewing that show the change, each with what to look for. The
     user runs them in a terminal where \`winapp\` is this PR's build.
   - media, when the change is user-visible: before (main) and after (this PR) screenshots or a short
     video, captured with \`winapp ui screenshot\` / \`winapp ui record\` (add \`--audio\` for sound), saved as
     files; pass their absolute paths. For CLI-only changes, a before/after transcript of the same
     command on main and on this PR: inline as beforeText / afterText, or as .txt files in before / after.
   - findings: category, severity, plain title, description (what and why it matters), recommendation
     (smallest fix), files with line numbers on the PR head (so it can be posted inline), diagramRefs.
     On your own PR, when you fix a finding, set its \`status: "fixed"\` (pr_board_update_review with the
     same id), or \`status: "declined"\` with the reason when you disagree, so fixed findings don't
     show as still open.
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

// ---- Other review shapes sessions write ---------------------------------------------------------------
const VERDICTS = ["approve", "approve-with-comments", "needs-discussion", "request-changes"];
const text = (...v) => v.find((x) => typeof x === "string" && x.trim())?.trim() || "";
const num = (v) => (Number.isInteger(Number(v)) && Number(v) > 0 ? Number(v) : undefined);
function verdictOf(v) {
    const s = String(v || "").toLowerCase().replace(/_/g, "-");
    if (!s) return undefined;
    if (VERDICTS.includes(s)) return s;
    if (/request|changes|block|reject/.test(s)) return "request-changes";
    if (/discuss|question|unsure/.test(s)) return "needs-discussion";
    if (/comment|nit/.test(s) && /approve|lgtm|clean|pass/.test(s)) return "approve-with-comments";
    if (/approve|lgtm|clean|pass|no (blocking )?(issues|findings)|ready|merge/.test(s)) return "approve";
    if (/comment/.test(s)) return "approve-with-comments";
    return undefined;
}
function severityOf(v) {
    const s = String(v || "").toLowerCase();
    if (SEVERITIES.includes(s)) return s;
    if (/crit/.test(s)) return "critical";
    if (/block|high|must|major|severe/.test(s)) return "high";
    if (/med|should|moderate/.test(s)) return "medium";
    if (/low|minor|nit|optional|could|suggest/.test(s)) return "low";
    if (/info|note|fyi/.test(s)) return "info";
    return undefined;
}
// "src/a.cs:12", "src/a.cs:12-20", "src/a.cs#L12-L20"
function locOf(s) {
    const m = String(s || "").match(/^(.+?)(?::(\d+)(?:-(\d+))?|#L(\d+)(?:-L?(\d+))?)?$/);
    if (!m || !m[1]) return null;
    const line = num(m[2] || m[4]), end = num(m[3] || m[5]);
    return { path: m[1], ...(line ? { line } : {}), ...(end && end !== line ? { endLine: end } : {}) };
}
// partial: a patch for an existing finding (merged by id), so only fields it actually carries are set.
export function adaptFinding(f, extra = "", { partial = false } = {}) {
    if (typeof f === "string") f = { title: f };
    if (!f || typeof f !== "object") return null;
    const sev = severityOf(f.severity || f.priority || f.level);
    const files = Array.isArray(f.files) && f.files.length ? f.files
        : [f.file || f.path || f.location].filter(Boolean).map((p) => { const l = locOf(p); if (!l) return null; const line = num(f.line ?? f.startLine ?? f.start_line) ?? l.line, end = num(f.endLine ?? f.end_line) ?? l.endLine; return { path: l.path, ...(line ? { line } : {}), ...(end && end !== line ? { endLine: end } : {}) }; }).filter(Boolean);
    const desc = text(f.description, f.what, f.body, f.details, f.detail, f.message, f.why, f.rationale);
    const title = text(f.title, f.summary, f.name, desc.split("\n")[0]).slice(0, 140) || (partial ? "" : "Finding");
    const category = CATEGORIES.includes(f.category) ? f.category : /block/i.test(String(f.severity || "")) ? "blocker" : /nit/i.test(String(f.severity || f.category || "")) ? "nit" : partial ? "" : "risk";
    const { file, path, location, startLine, start_line, end_line, priority, level, fixed, resolved, ...rest } = f;
    const st = String(f.status || f.state || "").toLowerCase();
    const status = fixed === true || resolved === true || /^(fixed|resolved|addressed|done|closed)$/.test(st) ? "fixed" : /^(declined|wont.?fix|won't fix|rejected|not.?needed|disagree)/.test(st) ? "declined" : st === "open" ? "open" : undefined;
    const description = [desc && desc !== title ? desc : "", extra].filter(Boolean).join("\n\n");
    const recommendation = text(f.recommendation, f.fix, f.suggestion, f.smallestFix, f.smallest_fix, f.proposedFix);
    const { state, ...kept } = rest;
    const keep = (k, v) => (v || !partial ? { [k]: v } : {});
    return { ...kept, ...(status ? { status } : state !== undefined ? { state } : {}), ...(f.id ? { id: String(f.id) } : {}), ...keep("title", title), ...keep("category", category), ...(sev ? { severity: sev } : {}),
        ...keep("description", description), ...keep("recommendation", recommendation), ...(files.length || !partial ? { files } : {}), ...(Array.isArray(f.diagramRefs) ? { diagramRefs: f.diagramRefs } : {}) };
}
/**
 * Accept the structured review, or the shapes pr-review sessions often write (rounds of findings and declined
 * findings, finalHeadSha / reviewedHeadSha, file:line locations, free-form severities and verdicts), and say
 * what was converted or is missing. Throws when nothing recognizable is there.
 */
export function adaptReview(raw) {
    const notes = [];
    let r = raw && typeof raw === "object" ? raw : {};
    if (r.review && typeof r.review === "object" && !Array.isArray(r.findings)) { r = { ...r.review, ...r, review: undefined }; notes.push("unwrapped `review`"); }
    const rounds = Array.isArray(r.rounds) ? r.rounds.filter((x) => x && typeof x === "object") : [];
    const last = rounds.at(-1) || {};
    const out = { ...r };
    delete out.rounds; delete out.review;
    const head = text(r.headSha, r.finalHeadSha, r.reviewedHeadSha, r.head, r.headRefOid, r.commit, last.headSha, last.reviewedHeadSha, last.head);
    if (head) out.headSha = head;
    for (const k of ["finalHeadSha", "reviewedHeadSha", "head", "headRefOid", "commit"]) delete out[k];
    let findings = Array.isArray(r.findings) ? r.findings : null;
    if (!findings && rounds.length) {
        findings = [...(Array.isArray(last.findings) ? last.findings : [])];
        const declined = rounds.flatMap((x) => (Array.isArray(x.declined) ? x.declined : []));
        const seen = new Set(findings.map((f) => text(f?.title, f?.summary)));
        for (const d of declined) {
            const t = text(d?.title, d?.summary, typeof d === "string" ? d : "");
            if (seen.has(t)) continue;
            seen.add(t);
            findings.push({ ...(typeof d === "string" ? { title: d } : d), _declined: text(d?.reason, d?.declineReason, d?.why) || "no reason given" });
        }
        notes.push(`converted pr-review rounds: ${Array.isArray(last.findings) ? last.findings.length : 0} finding(s) from round ${rounds.length}, ${declined.length} declined`);
    }
    if (findings) out.findings = findings.map((f) => (f?._declined ? adaptFinding({ ...f, _declined: undefined, status: "declined" }, `_Declined by the session:_ ${f._declined}`) : adaptFinding(f))).filter(Boolean);
    if (findings?.some((f) => f && typeof f === "object" && (["file", "path", "location", "body", "message", "fix", "suggestion"].some((k) => k in f) || (f.severity && !SEVERITIES.includes(f.severity))))) notes.push("mapped finding fields (file:line, severity, body/fix) to the board's shape");
    const v = verdictOf(r.verdict ?? r.recommendation ?? r.decision ?? r.outcome ?? last.verdict ?? last.decision);
    if (v) { if (v !== r.verdict) notes.push(`verdict "${r.verdict ?? r.recommendation ?? r.decision ?? r.outcome ?? last.verdict ?? last.decision}" → ${v}`); out.verdict = v; } else delete out.verdict;
    out.summary = text(r.summary, r.overall, r.conclusion, last.summary) || undefined;
    for (const k of ["recommendation", "decision", "outcome", "overall", "conclusion"]) if (typeof out[k] === "string") delete out[k];
    if (!out.summary && !out.verdict && !out.findings?.length && !arr(out.components).length && !arr(out.diagrams).length && !arr(out.changedFiles).length) {
        throw new Error(`Nothing the board can show: expected at least one of summary, verdict, findings (or rounds[].findings), components, diagrams. Got keys: ${Object.keys(r).join(", ") || "none"}. Call pr_board_review_guide for the shape.`);
    }
    const missing = [!out.summary && "summary", !out.verdict && "verdict", !out.headSha && "headSha (or finalHeadSha / reviewedHeadSha)"].filter(Boolean);
    if (missing.length) notes.push(`missing ${missing.join(", ")}`);
    return { review: out, notes };
}

// A Mermaid flowchart or sequence diagram, as sessions sometimes write it, turned into a board diagram.
// Handles the common forms: A[Label], A(Label), A{Label}, A --> B, A -->|label| B, A -- label --> B, -.->, ==>, ---;
// and participant / A->>B: text. Returns null when it isn't one of those.
export function mermaidToDiagram(src, index = 0) {
    const lines = String(src || "").split(/\r?\n|;/).map((l) => l.trim()).filter((l) => l && !l.startsWith("%%"));
    const head = (lines[0] || "").match(/^(flowchart|graph)\s*(TB|TD|BT|LR|RL)?/i);
    if (/^sequenceDiagram/i.test(lines[0] || "")) {
        const participants = new Map(), messages = [];
        const part = (id, label) => { if (!participants.has(id)) participants.set(id, { id, label: label || id }); };
        for (const l of lines.slice(1)) {
            let m = l.match(/^(?:participant|actor)\s+(\S+)(?:\s+as\s+(.+))?$/i);
            if (m) { part(m[1], m[2]?.trim()); continue; }
            m = l.match(/^([^-\s>]+)\s*-{1,2}>>?\+?\-?\s*([^:\s]+)\s*:\s*(.*)$/);
            if (m) { part(m[1]); part(m[2]); messages.push({ from: m[1], to: m[2], label: m[3].trim() }); }
        }
        return participants.size ? { id: `d${index + 1}`, title: "Sequence", kind: "sequence", participants: [...participants.values()], messages } : null;
    }
    if (!head) return null;
    const nodes = new Map(), edges = [];
    const node = (tok) => {
        const m = String(tok).trim().match(/^([\w.-]+)\s*(?:\[\(?|\(\(?|\{\{?|>)?\s*"?([^\]\)\}"]*)"?\s*(?:\)?\]|\)\)?|\}\}?)?$/);
        if (!m) return null;
        const id = m[1], label = (m[2] || "").trim();
        if (!nodes.has(id)) nodes.set(id, { id, label: label || id });
        else if (label) nodes.get(id).label = label;
        return id;
    };
    for (const l of lines.slice(1)) {
        if (/^(subgraph|end$|style|classDef|class|click|linkStyle)\b/i.test(l)) continue;
        const parts = l.split(/\s*(-->\|[^|]*\||--\s[^-]+?\s-->|-\.\s[^.]+?\s\.->|-\.->|==>|-->|---)\s*/);
        if (parts.length < 3) { node(l); continue; }
        for (let i = 0; i + 2 < parts.length; i += 2) {
            const from = node(parts[i]), to = node(parts[i + 2]);
            const lab = (parts[i + 1].match(/\|([^|]*)\|/) || parts[i + 1].match(/^--\s(.+?)\s-->$/) || parts[i + 1].match(/^-\.\s(.+?)\s\.->$/) || [])[1];
            if (from && to) edges.push({ from, to, ...(lab ? { label: lab.trim() } : {}) });
        }
    }
    if (!nodes.size) return null;
    const dir = (head[2] || "TB").toUpperCase();
    return { id: `d${index + 1}`, title: "How it fits together", kind: "flow", direction: dir === "LR" || dir === "RL" ? "LR" : "TB", nodes: [...nodes.values()], edges };
}

/** Normalize in place; return warnings for the agent. */
export function normalizeReview(review) {
    const warnings = [];
    // Entries must be objects; a Mermaid string becomes a diagram, anything else is dropped with a warning.
    const objects = (items, what) => arr(items).map((x, i) => {
        if (x && typeof x === "object" && !Array.isArray(x)) return x;
        if (what === "diagram" && typeof x === "string") {
            const d = mermaidToDiagram(x, i);
            if (d) { warnings.push(`diagram ${i + 1}: converted a Mermaid string into a ${d.kind} diagram; send the object shape for titles, status colors and files`); return d; }
        }
        warnings.push(`${what} ${i + 1}: skipped; expected an object, got ${Array.isArray(x) ? "an array" : typeof x}${typeof x === "string" ? ` ("${x.slice(0, 40)}")` : ""}`);
        return null;
    }).filter(Boolean);
    review.findings = ensureIds(objects(review.findings, "finding"), "f");
    review.diagrams = ensureIds(objects(review.diagrams, "diagram"), "d");
    review.components = ensureIds(objects(review.components, "component"), "c");
    review.functionality = arr(review.functionality);
    review.changedFiles = arr(review.changedFiles).map((f) => (typeof f === "string" ? { path: f } : f)).filter((f) => f && f.path);
    for (const f of review.findings) {
        if (!CATEGORIES.includes(f.category)) { warnings.push(`finding ${f.id}: unknown category "${f.category}", using "risk"`); f.category = "risk"; }
        if (!SEVERITIES.includes(f.severity)) f.severity = f.category === "blocker" ? "high" : "medium";
        f.files = normFiles(f.files);
        f.diagramRefs = arr(f.diagramRefs);
    }
    for (const c of review.components) c.files = normFiles(c.files);
    // Try it: commands to paste into a terminal with the PR's build. See it: screenshots, videos or transcripts.
    review.tryIt = arr(review.tryIt).map((t) => (typeof t === "string" ? { command: t } : t)).filter((t) => t && typeof t.command === "string" && t.command.trim())
        .slice(0, 8).map((t) => ({ title: String(t.title || "").slice(0, 160), command: t.command.trim().slice(0, 600), expect: String(t.expect || t.lookFor || "").slice(0, 400) }));
    review.media = arr(review.media).filter((m) => m && typeof m === "object").slice(0, 8);
    for (const m of review.media) if (!["comparison", "image", "video"].includes(m.kind)) m.kind = m.before || m.after || m.beforeText || m.afterText ? "comparison" : /\.(mp4|webm)$/i.test(m.path || "") ? "video" : "image";
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
        if (k === "tryIt" || k === "media") out[k] = v; // lists without ids: a patch replaces them
        else if (Array.isArray(v) && Array.isArray(cur) && v.every((x) => x && typeof x === "object")) out[k] = mergeById(cur, v);
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
 * The lines GitHub lets a review comment on, per file: the right-side lines inside each hunk of the PR's
 * diff (added and unchanged context lines). Returns [{ from, to, lines: Set }] per hunk.
 */
export function commentableHunks(patch) {
    const hunks = [];
    let cur = null, n = 0;
    for (const raw of String(patch || "").split("\n")) {
        const h = raw.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/);
        if (h) { n = Number(h[1]); cur = { lines: new Set() }; hunks.push(cur); continue; }
        if (!cur || raw.startsWith("\\")) continue;
        if (raw.startsWith("-")) continue;
        cur.lines.add(n++);
    }
    return hunks;
}

// Why a comment can't go on its line, in plain words, or null if it can. diff = { files: Map(path -> { patch }) }.
function anchorProblem(diff, c) {
    const f = diff.files.get(c.path);
    if (!f) return { why: `\`${c.path}\` isn't one of the files this PR changes` };
    if (!f.patch) return { why: `GitHub doesn't show a diff for \`${c.path}\` (binary or too large)` };
    const hunks = commentableHunks(f.patch);
    const hunk = hunks.find((h) => h.lines.has(c.line));
    if (!hunk) return { why: `line ${c.line} of \`${c.path}\` isn't part of this PR's diff` };
    if (c.start_line && !hunk.lines.has(c.start_line)) return { trim: true };
    return null;
}

/**
 * Build exactly what will be posted: findings marked Post become inline comments when they
 * point at a line in the PR's diff, and go in the summary body otherwise. The user's top note
 * leads the body. With `diff` (the PR's files and patches at `diff.head`), every inline comment is
 * checked against it first, so GitHub never rejects the post with "Line could not be resolved".
 */
export function composeGithubReview(review, triage, event, headSha, diff = null) {
    const tri = triage || {};
    const posted = arr(review?.findings)
        .filter((f) => tri[f.id]?.decision === "post")
        .sort((a, b) => SEV_ORDER[a.severity] - SEV_ORDER[b.severity]);
    const comments = [];
    const inBody = [];
    const unplaced = []; // { findingId, title, path, line, why }: inline comments moved into the body
    for (const f of posted) {
        const at = f.files.find((x) => x.line);
        if (!at) { inBody.push(f); continue; }
        const c = { findingId: f.id, path: at.path, line: at.endLine || at.line, ...(at.endLine && at.endLine !== at.line ? { start_line: at.line, start_side: "RIGHT" } : {}), side: "RIGHT", body: `${findingText(f, tri[f.id]?.note)}\n\n${AI_FOOTER}` };
        const p = diff ? anchorProblem(diff, c) : null;
        if (p?.trim) { delete c.start_line; delete c.start_side; }
        if (p?.why) { unplaced.push({ findingId: f.id, title: f.title, path: c.path, line: c.line, why: p.why }); inBody.push({ ...f, at: `${c.path}:${c.start_line ? `${c.start_line}-` : ""}${c.line}` }); }
        else comments.push(c);
    }
    const lines = [];
    // Until the user edits the note, it starts as the review's summary.
    const top = (tri._note && typeof tri._note.note === "string" ? tri._note.note : review?.summary || "").trim();
    if (top) lines.push(top);
    if (inBody.length) {
        if (lines.length) lines.push("");
        for (const f of inBody) {
            const loc = f.at ? ` (\`${f.at}\`)` : f.files[0] ? ` (\`${f.files[0].path}\`)` : "";
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
        commit_id: diff?.head || headSha || review?.headSha || undefined,
        unplaced,
        anchorCheck: diff ? "checked" : "unchecked",
        counts: { posted: posted.length, inline: comments.length, inBody: inBody.length, unplaced: unplaced.length, ignored: arr(review?.findings).filter((f) => tri[f.id]?.decision === "ignore").length, undecided: arr(review?.findings).filter((f) => !tri[f.id]?.decision || tri[f.id].decision === "undecided").length },
    };
}
