// Live progress for thread replies. The board asks the hosting agent a question with session.send, and the
// answer arrives later through pr_board_reply. This module watches the hosting session's events so the
// thread's "replying" bubble shows what the agent is doing: its own one-line narration (assistant.message)
// and the tool it's running (tool.execution_start).
//
// Extensions don't receive assistant.message_delta or assistant.tool_call_delta today, so the answer text
// can't stream word by word. If the runtime starts forwarding tool_call_delta, the bubble shows the
// pr_board_reply text as it's written, with no other change.

const REPLY_TOOL = /(^|[-_.])pr_board_reply$/;
const TARGET_RE = /^(?:The PR board canvas|\[PR board\])[^\n]*?([\w.-]+\/[\w.-]+)#(\d+)/;
const THROTTLE_MS = 80;

function basename(p) {
    return String(p || "").split(/[\\/]/).filter(Boolean).pop() || "a file";
}

export function stepLabel(toolName, args = {}) {
    const name = String(toolName || "");
    if (REPLY_TOOL.test(name)) return "Writing the answer";
    if (/powershell|bash|shell/.test(name)) return args.description ? String(args.description).slice(0, 80) : "Running a command";
    if (name === "view") return `Reading ${basename(args.path)}`;
    if (name === "grep" || name === "glob") return "Searching the code";
    if (/get_session|sessions_status|list_sessions/.test(name)) return "Checking the linked session";
    if (/send_session_message/.test(name)) return "Asking the linked session";
    if (/web_fetch|web_search/.test(name)) return "Reading the web";
    if (name === "task" || /read_agent/.test(name)) return "Asking a helper agent";
    if (/pr_board_get_state/.test(name)) return "Reading the board";
    if (/pr_board_/.test(name)) return "Updating the board";
    return "Working";
}

// Reads the "text" string out of a JSON object that may still be cut off mid-way.
export function partialJsonString(buf, key) {
    const m = new RegExp(`"${key}"\\s*:\\s*"`).exec(buf);
    if (!m) return null;
    let out = "";
    for (let i = m.index + m[0].length; i < buf.length; i++) {
        const ch = buf[i];
        if (ch === '"') return out;
        if (ch !== "\\") { out += ch; continue; }
        const nx = buf[i + 1];
        if (nx === undefined) return out;
        if (nx === "u") {
            const hex = buf.slice(i + 2, i + 6);
            if (!/^[0-9a-fA-F]{4}$/.test(hex)) return out;
            out += String.fromCharCode(parseInt(hex, 16));
            i += 5;
            continue;
        }
        out += { n: "\n", t: "\t", r: "", b: "\b", f: "\f" }[nx] ?? nx;
        i++;
    }
    return out;
}

function targetOf(buf, fallback) {
    const issue = /"issue"\s*:\s*(\d+)\s*[,}]/.exec(buf);
    const pr = /"pr"\s*:\s*(\d+)\s*[,}]/.exec(buf);
    const repo = /"repo"\s*:\s*"([\w.-]+\/[\w.-]+)"/.exec(buf);
    if (issue) return { repo: repo?.[1] || fallback?.repo || null, kind: "issue", number: Number(issue[1]) };
    if (pr) return { repo: repo?.[1] || fallback?.repo || null, kind: "pr", number: Number(pr[1]) };
    return fallback;
}

function narration(content) {
    const line = String(content || "").replace(/[*_`#>]/g, "").split(/\n+/).map((s) => s.trim()).find(Boolean);
    if (!line) return "";
    const sentence = line.split(/(?<=[.!?])\s/)[0];
    return sentence.length > 140 ? sentence.slice(0, 139) + "…" : sentence.replace(/[.:]$/, "");
}

// onUpdate(repo, { kind, number, text, step } | { kind, number, done: true })
// isPending(repo, kind, number) says whether that item is still waiting for a reply.
export function createStreamer({ onUpdate, defaultRepo, isPending = () => true }) {
    let target = null;
    const expected = [];
    const calls = new Map();
    const live = new Map();
    const timers = new Map();
    const keyOf = (t) => `${t.repo}|${t.kind}|${t.number}`;

    function emit(t, entry) {
        if (!t?.repo || !t.number) return;
        const key = keyOf(t);
        if (entry.done) {
            live.delete(key);
            clearTimeout(timers.get(key));
            timers.delete(key);
            onUpdate(t.repo, { kind: t.kind, number: t.number, done: true });
            return;
        }
        live.set(key, { ...(live.get(key) || {}), ...entry, at: Date.now() });
        if (timers.has(key)) return;
        timers.set(key, setTimeout(() => {
            timers.delete(key);
            const cur = live.get(key);
            if (cur) onUpdate(t.repo, { kind: t.kind, number: t.number, text: cur.text || "", step: cur.step || "" });
        }, THROTTLE_MS));
    }

    // The board sent a request for this item; the agent's next turns are probably about it.
    function expect(repo, kind, number) {
        const i = expected.findIndex((e) => e.repo === repo && e.kind === kind && e.number === number);
        if (i >= 0) expected.splice(i, 1);
        expected.push({ repo, kind, number });
    }

    function current() {
        if (target) return target;
        while (expected.length && !isPending(expected[0].repo, expected[0].kind, expected[0].number)) expected.shift();
        return expected[0] || null;
    }

    function finish(t) {
        const i = expected.findIndex((e) => e.repo.toLowerCase() === String(t.repo).toLowerCase() && e.kind === t.kind && e.number === t.number);
        if (i >= 0) expected.splice(i, 1);
        if (target && keyOf(target).toLowerCase() === keyOf(t).toLowerCase()) target = null;
        emit(t, { done: true });
    }

    function handle(ev) {
        if (!ev || ev.agentId) return;
        const d = ev.data || {};
        switch (ev.type) {
            case "user.message": {
                const m = TARGET_RE.exec(String(d.content || ""));
                const firstLine = String(d.content || "").split("\n")[0];
                target = m ? { repo: m[1], kind: /\/issues\/\d+|\bissue\b/.test(firstLine) ? "issue" : "pr", number: Number(m[2]) } : null;
                calls.clear();
                if (target) emit(target, { step: "Reading your question", text: "" });
                break;
            }
            case "tool.execution_start": {
                const cur = current();
                if (REPLY_TOOL.test(d.toolName || "")) {
                    const buf = JSON.stringify(d.arguments || {});
                    const t = targetOf(buf, cur ? { ...cur } : null);
                    if (t && !t.repo) t.repo = defaultRepo?.() || null;
                    calls.set(d.toolCallId, { name: d.toolName, buf, target: t });
                    if (t) emit(t, { step: "Writing the answer" });
                    break;
                }
                if (cur) emit(cur, { step: stepLabel(d.toolName, d.arguments || {}) });
                break;
            }
            case "assistant.message": {
                const cur = current();
                const said = narration(d.content);
                if (cur && said) emit(cur, { step: said });
                break;
            }
            case "assistant.tool_call_delta": {
                const c = calls.get(d.toolCallId) || { buf: "", name: "" };
                if (d.toolName) c.name = d.toolName;
                c.buf += d.inputDelta || "";
                calls.set(d.toolCallId, c);
                if (!REPLY_TOOL.test(c.name)) break;
                const cur = current();
                const t = targetOf(c.buf, cur ? { ...cur } : null);
                if (t && !t.repo) t.repo = defaultRepo?.() || null;
                const text = partialJsonString(c.buf, "text");
                if (t) emit(t, { step: "Writing the answer", ...(text !== null ? { text } : {}) });
                c.target = t;
                break;
            }
            case "tool.execution_complete": {
                const c = calls.get(d.toolCallId);
                if (c?.target && REPLY_TOOL.test(c.name)) finish(c.target);
                calls.delete(d.toolCallId);
                break;
            }
            case "session.idle":
            case "assistant.idle": {
                for (const key of [...live.keys()]) {
                    const [repo, kind, number] = key.split("|");
                    emit({ repo, kind, number: Number(number) }, { done: true });
                }
                target = null;
                calls.clear();
                break;
            }
        }
    }

    function forRepo(repo) {
        const out = {};
        for (const [key, v] of live) {
            const [r, kind, number] = key.split("|");
            if (r.toLowerCase() === String(repo).toLowerCase()) out[`${kind}:${number}`] = { text: v.text || "", step: v.step || "" };
        }
        return out;
    }

    return { handle, expect, forRepo };
}
