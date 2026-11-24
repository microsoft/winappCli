// Proactive mode: what the board starts on its own, and when. The board decides *what* is due; the hosting
// agent does the work. Nothing here writes to GitHub; prompts carry the guardrails (see prompts.mjs).
import { cpus, freemem } from "node:os";

export const LAYERS = [
    { id: "notice", n: 1, title: "Notice and summarize", hint: "New activity on your items: write \"What changed since you last looked\"." },
    { id: "rereview", n: 2, title: "Re-review after a push", hint: "A PR you reviewed got new commits: run pr-review again." },
    { id: "firstReview", n: 3, title: "First review", hint: "Someone else's ready PR (not a draft, CI not failing) that asks for your review, or that nobody has reviewed yet: open a PR session and draft a review, so it's waiting when you get to it. Never posted." },
    { id: "issues", n: 4, title: "Issue follow-through", hint: "A reporter replied: summarize it, then try to reproduce or validate in a session." },
    { id: "keepReady", n: 5, title: "Keep your PRs ready", hint: "Your PRs and the ones our sessions open: turn on Get it ready & keep it ready (pr-lifecycle, pr-review, and Agent merge for reviews, CI and conflicts; never merges). Not for a PR where you turned it off." },
];

export const DEFAULTS = Object.freeze({
    paused: false,
    layers: { notice: true, rereview: true, firstReview: true, issues: true, keepReady: true },
    reviewBots: false, // first reviews of bot PRs (Dependabot and friends)
    v: 2,
    maxConcurrent: 2,
    dailyCap: 10,
    cooldownMin: 30,
    stableMin: 5,
    machineCheck: true,
    cpuMax: 70,
    minFreeGB: 4,
    hoursFrom: null, // 0-23, local time; both null = any time
    hoursTo: null,
});

const RUN_TIMEOUT_MS = 3 * 3600_000;
const int = (v, lo, hi, d) => { const n = Number(v); return Number.isFinite(n) ? Math.min(hi, Math.max(lo, Math.round(n))) : d; };
const hour = (v) => (v === null || v === "" || v === undefined ? null : int(v, 0, 23, null));

// Merge user input over the saved settings, keeping every value in range. Settings saved before v2 had first
// review off and no keep-ready layer; v2 turns both on once, and keeps everything else as it was.
export function normalizeSettings(input = {}, current = {}) {
    const cur = { ...DEFAULTS, ...current, layers: { ...DEFAULTS.layers, ...(current.layers || {}) } };
    if (current && Object.keys(current).length && (current.v || 1) < 2) { cur.layers.firstReview = true; cur.layers.keepReady = true; }
    const inp = input || {};
    const layers = { ...cur.layers };
    for (const l of LAYERS) if (typeof inp.layers?.[l.id] === "boolean") layers[l.id] = inp.layers[l.id];
    return {
        paused: typeof inp.paused === "boolean" ? inp.paused : !!cur.paused,
        layers,
        maxConcurrent: int(inp.maxConcurrent ?? cur.maxConcurrent, 0, 8, DEFAULTS.maxConcurrent),
        dailyCap: int(inp.dailyCap ?? cur.dailyCap, 0, 200, DEFAULTS.dailyCap),
        cooldownMin: int(inp.cooldownMin ?? cur.cooldownMin, 0, 24 * 60, DEFAULTS.cooldownMin),
        stableMin: int(inp.stableMin ?? cur.stableMin, 0, 24 * 60, DEFAULTS.stableMin),
        machineCheck: typeof inp.machineCheck === "boolean" ? inp.machineCheck : cur.machineCheck !== false,
        cpuMax: int(inp.cpuMax ?? cur.cpuMax, 1, 100, DEFAULTS.cpuMax),
        minFreeGB: int(inp.minFreeGB ?? cur.minFreeGB, 0, 512, DEFAULTS.minFreeGB),
        hoursFrom: "hoursFrom" in inp ? hour(inp.hoursFrom) : hour(cur.hoursFrom),
        hoursTo: "hoursTo" in inp ? hour(inp.hoursTo) : hour(cur.hoursTo),
        reviewBots: typeof inp.reviewBots === "boolean" ? inp.reviewBots : !!cur.reviewBots,
        v: 2,
    };
}

// Average CPU use since the previous call, and free memory. The first call has no CPU number yet.
let lastCpu = null;
export function sampleMachine() {
    const now = cpus().map((c) => c.times);
    let cpu = null;
    if (lastCpu && lastCpu.length === now.length) {
        let busy = 0, total = 0;
        now.forEach((t, i) => {
            const p = lastCpu[i];
            const d = (k) => t[k] - p[k];
            const all = d("user") + d("nice") + d("sys") + d("idle") + d("irq");
            busy += all - d("idle");
            total += all;
        });
        cpu = total > 0 ? Math.round((busy / total) * 100) : null;
    }
    lastCpu = now;
    return { cpu, freeGB: Math.round((freemem() / 1024 ** 3) * 10) / 10 };
}

const ts = (s) => (s ? new Date(s).getTime() || 0 : 0);
const short = (s) => String(s || "").slice(0, 7);
export const today = (d = new Date()) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const BOT = /\[bot\]$|^(copilot-pull-request-reviewer|copilot|github-code-quality|github-actions|microsoft-github-policy-service|dependabot)$/i;

// A run the board started that hasn't reported back (and hasn't timed out).
export const runningOf = (local, now = Date.now()) => (local?.auto?.running && now - ts(local.auto.running.at) < RUN_TIMEOUT_MS ? local.auto.running : null);

// Activity you haven't seen: not yours, not a bot, after you last looked (and after the last summary).
export function unseenActivity(c, viewer, readBaseline) {
    const l = c.local || {};
    const since = Math.max(ts(l.lastSeenAt), ts(readBaseline), ts(l.whatChanged?.upTo));
    return (c.activity || []).filter((e) => e.who && e.who !== viewer && !e.bot && !BOT.test(e.who) && ts(e.at) > since);
}

const involved = (c) => (c.kind === "issue" ? !!(c.local?.session?.id || (c.local?.thread || []).length) : ["yours", "reviewing"].includes(c.section));

// Every run that could start now or later, one per item at most (the most important).
export function candidates(snap, settings, now = Date.now()) {
    const out = [];
    const viewer = snap.viewer;
    const items = [...snap.cards.filter((c) => c.section !== "merged"), ...snap.issues.filter((i) => !i.covered && i.state === "OPEN")].filter((c) => !c.hidden && !c.local?.hidden);
    for (const c of items) {
        const l = c.local || {};
        const kind = c.kind === "issue" ? "issue" : "pr";
        const done = l.auto?.done || {};
        const add = (job) => { if (!done[job.key]) out.push({ kind, number: c.number, title: c.title, url: c.url, ...job }); };
        const mine = kind === "pr" && (c.author === viewer || l.role === "author");
        const fresh = unseenActivity(c, viewer, snap.readBaseline);
        let picked = false;

        // Layer 4 for an issue a PR fixes: the issue has no row of its own, so its reply shows on the PR.
        const ir = kind === "pr" && c.replied?.issue && c.replied.kind === "comment" ? c.replied : null;
        const icard = ir ? snap.issues.find((i) => i.number === ir.issue) : null;
        if (ir && icard && settings.layers.issues && !icard.local?.auto?.done?.[`issue:${ir.id}`]) {
            out.push({ kind: "issue", number: ir.issue, title: icard.title, url: icard.url, layer: "issues", key: `issue:${ir.id}`, priority: 0, reason: `${ir.who} replied`, readyAt: ts(ir.at) + settings.stableMin * 60_000, reply: ir, events: [ir] });
        }

        if (kind === "pr" && !mine && !c.isDraft && !c.queue) {
            // Layer 2: a PR we reviewed has new real commits. Merges that only bring the base branch in (and
            // their pushes) don't count: a review of the latest real commit still covers the head.
            const reviewed = l.prReview?.headSha || l.review?.headSha || null;
            const covers = c.reviewShas?.length ? c.reviewShas : [c.headSha];
            const realHead = covers[0];
            const pushes = (c.activity || []).filter((e) => e.kind === "push" && !e.baseMerge);
            const lastPush = pushes.at(-1);
            const pushedSinceGitHubReview = c.myReviewAt && lastPush && ts(lastPush.at) > ts(c.myReviewAt);
            if (settings.layers.rereview && realHead && ((reviewed && !covers.includes(reviewed)) || (!reviewed && pushedSinceGitHubReview))) {
                const who = lastPush?.who || c.author;
                add({ layer: "rereview", key: `rereview:${realHead}`, priority: 1,
                    reason: `${who} pushed ${short(realHead)}`, readyAt: ts(lastPush?.at) + settings.stableMin * 60_000, head: realHead, events: fresh });
                picked = true;
            }
            // Layer 3: someone else's ready PR that asks for your review, or that nobody has reviewed yet.
            const ours = reviewed || c.myReviewAt || l.draft || l.review || l.agent?.kind === "review";
            const requested = (c.requested || []).includes(viewer);
            const unreviewed = !(c.reviewers || []).length && c.reviewDecision !== "APPROVED";
            const ciOk = c.ci?.state === "passing" || c.ci?.state === "none" || !c.ci?.state;
            const botOk = !c.authorBot || settings.reviewBots;
            if (!picked && settings.layers.firstReview && !ours && ciOk && botOk && (requested || (["other", "reviewing"].includes(c.section) && unreviewed)) && !l.session?.id) {
                add({ layer: "firstReview", key: `review:${c.headSha}`, priority: requested ? 1 : 3,
                    reason: requested ? `${c.author} asked you to review it` : "it's ready and nobody here is on it", readyAt: ts(lastPush?.at || c.updatedAt) + settings.stableMin * 60_000, head: c.headSha, events: [] });
                picked = true;
            }
        }
        // Layer 5: your PR (or one our session opened) gets "Get it ready & keep it ready", once, unless you turned it off.
        const keepOn = l.sync?.agentMerge?.enabled || l.monitorPending?.enable || l.readyAsked;
        if (!picked && kind === "pr" && mine && settings.layers.keepReady && !keepOn && !l.keepReadyOff && !c.queue && !c.autoMerge) {
            add({ layer: "keepReady", key: `keep:${c.number}`, priority: 1, reason: l.role === "author" && c.author !== viewer ? "our session opened it" : "it's your PR",
                readyAt: ts(c.createdAt) + settings.stableMin * 60_000, head: c.headSha, events: [] });
            picked = true;
        }
        // Layer 4: the reporter replied on an issue you're in.
        if (!picked && kind === "issue" && settings.layers.issues && involved(c)) {
            const reply = c.replied && c.replied.kind === "comment" ? c.replied : fresh.filter((e) => e.kind === "comment").at(-1);
            if (reply) {
                add({ layer: "issues", key: `issue:${reply.id}`, priority: 0, reason: `${reply.who} replied`, readyAt: ts(reply.at) + settings.stableMin * 60_000, reply, events: fresh });
                picked = true;
            }
        }
        // Layer 1: anything else new on an item you're in. A PR you've never looked at or reviewed has nothing
        // "since you last looked"; a first review (layer 3) covers it instead.
        const knows = kind === "issue" || mine || l.lastSeenAt || l.review || l.prReview || c.myReviewAt || (l.thread || []).length;
        if (!picked && settings.layers.notice && involved(c) && knows && fresh.length) {
            const last = fresh.at(-1);
            const who = [...new Set(fresh.map((e) => e.who))].join(", ");
            const why = fresh.every((e) => e.baseMerge) ? `${who} merged the base branch in (no new changes of their own)` : `new activity from ${who}`;
            add({ layer: "notice", key: `notice:${last.id}`, priority: 0, reason: why, readyAt: ts(last.at) + settings.stableMin * 60_000, events: fresh });
        }
    }
    return out;
}

const inHours = (s, d = new Date()) => {
    if (s.hoursFrom === null || s.hoursTo === null) return true;
    const h = d.getHours();
    return s.hoursFrom <= s.hoursTo ? h >= s.hoursFrom && h < s.hoursTo : h >= s.hoursFrom || h < s.hoursTo;
};

// The queue: every candidate with whether it can start now, and if not, why. "start" lists what to start
// now, in order, within capacity. Layer 1 (a summary in this session) doesn't use a slot or the daily cap.
export function plan(snap, meta, { now = Date.now(), machine = null } = {}) {
    const s = normalizeSettings({}, meta.auto?.settings);
    const all = [...snap.cards, ...snap.issues];
    const running = all.filter((c) => runningOf(c.local, now)).map((c) => ({ kind: c.kind === "issue" ? "issue" : "pr", number: c.number, title: c.title, ...c.local.auto.running }));
    const autoSessions = new Set(all.filter((c) => runningOf(c.local, now)).map((c) => c.local.session?.id).filter(Boolean));
    const busy = new Set(all.filter((c) => c.local?.sync?.activity === "busy" && c.local?.session?.id && !autoSessions.has(c.local.session.id)).map((c) => c.local.session.id)).size;
    const heavyRunning = running.filter((r) => r.layer !== "notice").length;
    const used = heavyRunning + busy;
    const runsToday = meta.auto?.counts?.[today(new Date(now))] || 0;
    const m = machine || { cpu: null, freeGB: null };
    const machineWhy = !s.machineCheck ? null
        : m.cpu !== null && m.cpu > s.cpuMax ? `the CPU is busy (${m.cpu}% > ${s.cpuMax}%)`
        : m.freeGB !== null && m.freeGB < s.minFreeGB ? `free memory is low (${m.freeGB} GB < ${s.minFreeGB} GB)` : null;
    const busyItems = new Set(all.filter((c) => runningOf(c.local, now) || c.local?.pending || c.local?.agent?.status === "running").map((c) => `${c.kind === "issue" ? "issue" : "pr"}:${c.number}`));
    const lastRun = new Map(all.map((c) => [`${c.kind === "issue" ? "issue" : "pr"}:${c.number}`, ts(c.local?.auto?.lastAt)]));

    const queue = candidates(snap, s, now).sort((a, b) => a.priority - b.priority || a.readyAt - b.readyAt);
    let slots = Math.max(0, s.maxConcurrent - used);
    let cap = Math.max(0, s.dailyCap - runsToday);
    const start = [];
    for (const q of queue) {
        const heavy = q.layer !== "notice";
        const item = `${q.kind}:${q.number}`;
        const cooldownUntil = lastRun.get(item) + s.cooldownMin * 60_000;
        q.why = busyItems.has(item) ? "the agent is already working on it"
            : q.readyAt > now ? `waiting until it's ${s.stableMin} min quiet`
            : cooldownUntil > now ? `ran recently; next run after a ${s.cooldownMin} min pause`
            : s.paused ? "proactive mode is paused"
            : !inHours(s, new Date(now)) ? `outside active hours (${s.hoursFrom}:00–${s.hoursTo}:00)`
            : heavy && machineWhy ? machineWhy
            : heavy && cap <= 0 ? `the daily cap (${s.dailyCap}) is reached`
            : heavy && slots <= 0 ? `waiting for a free slot (${used} of ${s.maxConcurrent} busy)`
            : null;
        q.startsAt = Math.max(q.readyAt, cooldownUntil || 0);
        if (q.why) continue;
        start.push(q);
        busyItems.add(item);
        if (heavy) { slots--; cap--; }
    }
    return {
        settings: s,
        queue: queue.map(({ events, reply, ...q }) => ({ ...q, preview: (events || []).slice(-3).map((e) => `${e.who} ${e.kind === "push" ? "pushed" : e.kind === "review" ? "reviewed" : "commented"}`) })),
        start,
        running,
        capacity: { max: s.maxConcurrent, used, busySessions: busy, autoRunning: heavyRunning, runsToday, dailyCap: s.dailyCap, machine: m, machineWhy },
        log: (meta.auto?.log || []).slice(-20),
    };
}
