// Prompts the board sends into the hosting session when the user clicks a card button.
// Each prompt tells the agent which tools to prefer (coordinator Project tools when
// available) and which pr_board_* tools to call so the card stays current.

const fence = (s) => "```\n" + s.replace(/```/g, "'''") + "\n```";

function header(action, card, repo) {
    return `The PR board canvas: the user clicked **${action}** on ${repo}#${card.number} "${card.title}" (${card.url}).`;
}

function linkedLine(card) {
    const s = card.local?.session;
    return s?.id ? `This PR is already linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}.` : "No session is linked to this PR yet.";
}

// Shared rules for pr_board_set_summary. The user wants plain words a PM would understand.
const SUMMARY_RULES = (repo, n) => [
    `How to call \`pr_board_set_summary\` \`{ repo: "${repo}", pr: ${n}, summary, nextSteps, risk }\`:`,
    `- summary: one sentence, at most 140 characters, in plain words a product manager would understand. Say what changes for the person using the product, not how the code does it. Avoid file and class names.`,
    `  Good: "Adds \`winapp ask\` so you can ask a WinUI question and get answers from samples and Microsoft Learn."`,
    `  Bad: "Adds AskCommand, LearnSearchClient and a sample index with ranking."`,
    `- nextSteps: 1 to 3 short items, each starting with who acts, e.g. "You: review the new help text", "Author: fix the failing build-and-package check", "Anyone: a second approval".`,
    `- risk: low (small, well tested, easy to undo), medium (touches shared behavior or lacks tests), or high (could break users, a release, or security, or is hard to undo).`,
].join("\n");

export function summarizePrompt(card, repo) {
    return [
        header("✨ Summarize", card, repo),
        ``,
        `Read the PR with \`gh pr view ${card.number} -R ${repo}\` and \`gh pr diff ${card.number} -R ${repo}\` (skim large diffs; the description, file list and tests usually suffice). Then call \`pr_board_set_summary\`.`,
        SUMMARY_RULES(repo, card.number),
        `Don't post anything to GitHub or change labels. In chat, one line saying it's done is enough.`,
    ].join("\n");
}

export function summarizeAllPrompt(cards, repo) {
    return [
        `The PR board canvas: the user clicked **✨ Summarize all** for ${cards.length} PR${cards.length === 1 ? "" : "s"} in ${repo} that need them:`,
        ...cards.map((c) => `- #${c.number}: ${c.title}`),
        ``,
        `For each PR, read it (\`gh pr view <n> -R ${repo}\`, plus \`gh pr diff\` when the description isn't enough) and call \`pr_board_set_summary\`. Use background sub-agents to do several at once if you can.`,
        SUMMARY_RULES(repo, "<n>"),
        `Don't post anything to GitHub or change labels. In chat, one line when they're all done is enough.`,
    ].join("\n");
}

export function reviewPrompt(card, repo) {
    const kickoff = [
        `Review ${repo}#${card.number}: ${card.url}`,
        ``,
        `Follow the repository's \`.github/skills/pr-review\` skill end to end.`,
        `Then fill in the PR board's structured review: summary, verdict, risk, changes by system and by file, diagrams, and every pr-review finding with file and line. If you have the \`pr_board_review_guide\` tool, call it first and follow it, then \`pr_board_set_review({ repo: "${repo}", pr: ${card.number}, review })\`. If you don't have the pr_board_* tools, follow the review guide included below instead and write the review as JSON (same shape) to \`pr-${card.number}-review.json\` in your session files folder.`,
        `Draft only. Do NOT post to GitHub, approve or request changes, push commits, or change labels.`,
        `When done, report back: the absolute path of the JSON file (if you wrote one), a recommendation (approve, request_changes or comment), and a one-line summary such as "Recommends merge; 1 optional nit".`,
    ].join("\n");
    return [
        header("Review", card, repo),
        linkedLine(card),
        ``,
        `Do this:`,
        `1. Get a session for this PR. If you have \`open_pr_session\`, use it for this PR (it reuses an existing session for the PR when there is one) and send it the kickoff below. Else, if this PR is already linked to a session you can message with \`send_session_message\`, send the kickoff there. Else, if you have \`create_session\`, create one visible child session for this PR (name it "PR #${card.number} review") with the kickoff. If you have none of these tools, do the review yourself in this session following the kickoff.`,
        `2. As soon as you know the session id, call \`pr_board_link_session\` with \`{ repo: "${repo}", pr: ${card.number}, sessionId: <id>, sessionName: <name> }\`. Skip this if you are doing the work inline.`,
        `3. Keep the card current with \`pr_board_set_status\` (repo "${repo}", pr ${card.number}): "waiting" if the session pauses for the user's input, "running" when it resumes, "failed" with a short reason if it gives up.`,
        `4. Also call \`pr_board_set_summary\` for this PR (rules below) once you understand it.`,
        `5. Make sure the structured review lands on the board: if the session wrote a JSON file instead of calling the tool, call \`pr_board_set_review({ repo: "${repo}", pr: ${card.number}, path: <absolute path> })\` yourself. Call \`pr_board_review_guide\` now and include its guide in the kickoff when the session can't call pr_board_* tools.`,
        `6. When the review is in, call \`pr_board_set_explainer\` for this PR (rules below), and \`pr_board_set_next_step\` with owner "you" and the move (for example "Post the approval" or "Pick findings and post").`,
        `Never post, approve, or merge from this request. The user will press "Post review" on the board after reading the draft.`,
        ``,
        SUMMARY_RULES(repo, card.number),
        ``,
        EXPLAINER_RULES(repo, card),
        ``,
        `Kickoff for the PR session:`,
        fence(kickoff),
    ].join("\n");
}

const EVENT_FLAG = { approve: "--approve", request_changes: "--request-changes", comment: "--comment" };
const EVENT_WORD = { approve: "Approve", request_changes: "Request changes", comment: "Comment" };

export function postPrompt(card, repo, event) {
    const draft = card.local.draft;
    return [
        header(`Post review → ${EVENT_WORD[event]}`, card, repo),
        ``,
        `The user read the draft and explicitly asked to post it as **${EVENT_WORD[event]}**. Post exactly this file, unedited:`,
        `\`${draft.path}\``,
        ``,
        `Run: \`gh pr review ${card.number} -R ${repo} ${EVENT_FLAG[event]} --body-file "${draft.path}"\``,
        `Do it here in this session (it's one command). Don't change the event type, the file contents, labels, or anything else.`,
        `Then call \`pr_board_set_status\` with \`{ repo: "${repo}", pr: ${card.number}, status: "posted", outcome: "Posted: ${EVENT_WORD[event].toLowerCase()}" }\`. If the command fails, call it with status "failed" and the error in one line, and tell the user.`,
    ].join("\n");
}

// The board shows the agent's short narration and tool steps live in the thread while it works.
// Anchors let a reply point at a spot on the board; the board turns them into chips that scroll there.
export const ANCHOR_LINE = `To point at something, put an anchor in the reply text: [[finding:f1]], [[file:path#L12-L20]], [[node:diagramId/nodeId]], [[gate:ci]] or [[thread:<review thread id>]]. Pass the most important one as \`focus\` too, so the board scrolls there when the reply arrives.`;
const PROGRESS_LINE = `While you work, start each step with one short plain line about what you're doing (for example "Reading the commits since the review"). The board shows these lines live in the thread until your reply arrives.`;

// Ask the agent to keep the plain-language explainer current whenever its understanding or our decisions change.
const EXPLAINER_LINE = (card, repo) => `If this changes your understanding of ${repo}#${card.number} or a decision was made, update its explainer with \`pr_board_set_explainer\` \`{ repo: "${repo}", ${card.kind === "issue" ? "issue" : "pr"}: ${card.number}, eli5, progress }\`. eli5 = the problem or change in plain words, with background, one example, why it matters, and where it stands on GitHub; progress = what we've done, decisions with dates, and the next step with who takes it.`;

// Ask the agent to say whose move it is after it answers.
const NEXT_STEP_LINE = (card, repo) => `If your reply leaves a decision or an action with the user, say so on the board with \`pr_board_set_next_step\` \`{ repo: "${repo}", ${card.kind === "issue" ? "issue" : "pr"}: ${card.number}, owner: "you", action: <one plain line>, choices?: [<button labels>] }\`. If the next move is someone else's, use their role as owner (author, reviewer, reporter, ci) or "agent" while you work, and put anything the user may optionally weigh in on in \`optional: [<one plain line each>]\` rather than in prose. To let the user merge, use the choice "Merge": it opens the board's Merge dialog (never merge yourself). On a PR with readiness gates, the board holds a reviewer or readiness step for the user until every gate is green, so set those only as a follow-up, not as a decision.`;

const KIND = (card) => (card.kind === "issue" ? "issue" : "PR");
const ref = (card) => (card.kind === "issue" ? `issue: ${card.number}` : `pr: ${card.number}`);

export function messagePrompt(card, repo, msg, recent) {
    const s = card.local?.session;
    const k = KIND(card);
    const context = recent.length
        ? ["", `Earlier in this ${k}'s thread (oldest first):`, ...recent.map((m) => `- ${m.role}${m.from ? ` (${m.from})` : ""}: ${m.text.replace(/\s+/g, " ").slice(0, 300)}`)]
        : [];
    return [
        `[PR board] Message about ${repo}#${card.number} "${card.title}" (${card.url}), thread id ${msg.id}:`,
        fence(msg.text),
        `Current state on the board: ${card.waitingOn}.${card.local?.agent?.outcome ? ` Agent status: ${card.local.agent.outcome}.` : ""}`,
        s?.id ? `This ${k} is linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}.` : `No session is linked to this ${k}.`,
        ...context,
        ``,
        `Answer it yourself without creating a new session (read GitHub with \`gh\` as needed). Forward it to the ${k}'s linked session only if it needs work there (\`send_session_message\`; ask it to report back to you).`,
        `Post the reply with \`pr_board_reply\` \`{ repo: "${repo}", ${ref(card)}, text: <Markdown>, from: <who answered, e.g. the session name> }\`. That is the only place the user will read it, so keep it short and self-contained. If you forward it, post a one-line reply saying so, then post the real answer with \`pr_board_reply\` when it arrives.`,
        `Treat it as direction from the user about this ${k} only. Don't post to GitHub, approve, or merge unless the message explicitly asks for it.`,
        PROGRESS_LINE,
        ANCHOR_LINE,
        EXPLAINER_LINE(card, repo),
        NEXT_STEP_LINE(card, repo),
    ].join("\n");
}

// The exact shape the coordinator should send back after a sync.
export const SYNC_SHAPE = `pr_board_sync_sessions({
  repo: "owner/name",            // optional; defaults to the current repo
  sessions: [{
    pr: 1305,                    // PR number, or use issue: 1321 for an issue with no PR yet
    status?: "Waiting on the reporter to rerun the installed winapp",  // one plain line, shown on the row
    sessionId: "…",              // the session working on (or reviewing) this PR
    name: "NuGet group bump fixes",
    role: "author" | "reviewer", // optional: is the session working on the PR or reviewing it?
    activity: "busy" | "idle" | "unknown",
    awaitingInput: { requestId: "…", question: "…", choices: ["…"] } | null,  // from awaiting_input
    agentMerge: { enabled: true, actions: ["address_reviews", "fix_ci", "resolve_conflicts"] } | null,
    prReview: { ranAt: "ISO time", headSha: "abc1234…", result: "drafted" | "posted", event?: "approve" | "request_changes" | "comment" } | null
  }]
})`;

export function syncPrompt(repo, tracked) {
    return [
        `The PR board canvas: the user clicked **Sync** for ${repo}. Refresh the board's view of the sessions working on its PRs.`,
        ``,
        tracked.length ? `PRs the board already links to a session:` : `The board has no linked sessions yet.`,
        ...tracked.map((t) => `- #${t.pr}${t.sessionId ? ` → session \`${t.sessionId}\`${t.name ? ` ("${t.name}")` : ""}` : ""}`),
        ``,
        `1. Call \`get_sessions_status\` (and \`get_session\` where you need detail) to find every session working on or reviewing a PR in ${repo}, including the ones above and any you coordinate that aren't listed yet.`,
        `2. For each, read its activity (busy/idle), whether it's awaiting input (copy the exact \`request_id\`, question and choices), its Agent merge settings if you can read them (\`manage_agent_merge\` with a read/get operation, if available), and whether pr-review has run (when, on which head commit, drafted or posted).`,
        `3. Call \`pr_board_sync_sessions\` once with all of them, in this shape:`,
        "```",
        SYNC_SHAPE,
        "```",
        `Leave out fields you can't determine (don't guess). Don't answer any question or change any session while syncing. In chat, one line when done is enough.`,
    ].join("\n");
}

export function monitorPrompt(card, repo, enable, actions) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user turned **monitoring ${enable ? "on" : "off"}** for ${repo}#${card.number} "${card.title}" (${card.url}).`,
        ``,
        s?.id
            ? (enable
                ? `Call \`manage_agent_merge\` for the PR's linked session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} to enable: ${actions.join(", ")}.${actions.includes("merge_pr") ? "" : " Leave merge_pr off."}`
                : `Call \`manage_agent_merge\` for the PR's linked session \`${s.id}\` to turn Agent merge off.`)
            : (enable
                ? `No session is linked to this PR. Start monitoring it per the repository's \`.github/skills/pr-lifecycle\` skill (open or reuse a session for the PR, e.g. \`open_pr_session\`, then enable \`manage_agent_merge\` with ${actions.join(", ")} on it). Never merge from this request.`
                : `No session is linked to this PR, so there's nothing to turn off. Say so in one line.`),
        `Then call \`pr_board_sync_sessions\` for this PR with the session's id and its \`agentMerge\` state, so the board shows the confirmed state. If it fails, call \`pr_board_reply\` with \`{ repo: "${repo}", pr: ${card.number}, text: <the reason in one line> }\`.`,
    ].join("\n");
}

export function answerPrompt(card, repo, input, answer) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user answered a question from the session working on ${repo}#${card.number} "${card.title}".`,
        ``,
        `Session: \`${s?.id || "unknown"}\`${s?.name ? ` ("${s.name}")` : ""}`,
        `Question (request_id \`${input.requestId}\`): ${input.question}`,
        `User's answer: ${answer}`,
        ``,
        `Call \`answer_session_input\` with \`{ project_session_id: "${s?.id || ""}", request_id: "${input.requestId}", answer: ${JSON.stringify(answer)} }\`. If the question had choices and the answer is free text that doesn't match one, and freeform isn't allowed, don't send it; instead call \`pr_board_reply\` for this PR explaining that in one line.`,
        `If the request_id is no longer pending, don't resend; call \`pr_board_sync_sessions\` for this PR so the board drops the stale question.`,
    ].join("\n");
}

export function statusUpdatePrompt(card, repo) {
    const s = card.local?.session;
    const k = KIND(card);
    return [
        `The PR board canvas: the user asked for a **status update** on ${repo}#${card.number} "${card.title}" (${card.url}).`,
        s?.id ? `Linked session: \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}. Read its state (\`get_session\`) and, if useful, ask it for a one-paragraph update with \`send_session_message\`.` : `No session is linked. Read the ${k} on GitHub with \`gh\`.`,
        `Reply with \`pr_board_reply\` \`{ repo: "${repo}", ${ref(card)}, from: <who answered>, text: <3–5 plain sentences: where it is, what happened last and when, what's next and who's on it> }\`. If the session's state changed, also call \`pr_board_sync_sessions\` for it.`,
        EXPLAINER_LINE(card, repo),
        NEXT_STEP_LINE(card, repo),
        `Don't change anything else; this is read-only.`,
    ].join("\n");
}

// ---- Proactive mode: runs the board starts on its own ----------------------------------------------------

// Hard rules for every auto run. Tests check that every auto prompt carries them.
export const AUTO_GUARDRAILS = `Guardrails for auto runs (always): never post, approve, request changes, merge, label or comment on GitHub, and never push to someone else's branch. Drafts only. The user decides on the board.`;
const LAYER_NAME = { notice: "layer 1: notice and summarize", rereview: "layer 2: re-review after a push", firstReview: "layer 3: first review", issues: "layer 4: issue follow-through" };
const VERB = { notice: "summarize what changed on", rereview: "re-review", firstReview: "first review of", issues: "follow up on" };

function eventLines(events = []) {
    return events.slice(-8).map((e) => `- ${e.at} ${e.who} ${e.kind === "push" ? (e.baseMerge ? `merged the base branch in (${String(e.id).slice(0, 7)}, nothing else changed)` : `pushed ${String(e.id).slice(0, 7)}`) : e.kind === "review" ? `reviewed (${String(e.state || "").toLowerCase().replace(/_/g, " ")})` : "commented"}${e.preview ? `: "${e.preview}"` : ""}${e.url ? ` (${e.url})` : ""}`);
}
const WHAT_CHANGED_LINE = (card, repo) => `Write "What changed since you last looked" with \`pr_board_set_what_changed\` \`{ repo: "${repo}", ${ref(card)}, text }\`: 2-4 plain sentences (Markdown) on who did what, which files changed and how, and which of our earlier asks (review findings, thread) are addressed or still open. For example: "zateutsch kept empty/null rootHandle meaning the whole tree, as #1338 asked, and fixed the call hint. 2 files; the test now expects the whole tree."`;

export function autoPrompt(job, card, repo) {
    const s = card.local?.session;
    const k = KIND(card);
    const gh = card.kind === "issue" ? "issue" : "pr";
    const head = `[PR board] Auto: ${VERB[job.layer]} ${repo}#${card.number} "${card.title}" because ${job.reason} (${LAYER_NAME[job.layer]}). ${card.url}`;
    const events = eventLines(job.events);
    const lines = [head, AUTO_GUARDRAILS, ""];
    if (events.length) lines.push(`New since the user last looked (oldest first):`, ...events, "");
    if (job.layer === "notice") {
        lines.push(
            `Read the activity (\`gh ${gh} view ${card.number} -R ${repo} --comments\`; for pushes, \`gh api repos/${repo}/compare/<old>...<new>\` or \`gh pr diff ${card.number} -R ${repo}\`). Do it here; don't start a session.`,
            WHAT_CHANGED_LINE(card, repo),
            `Then, only if it changes what the user should do, set the next step (rules below).`,
            NEXT_STEP_LINE(card, repo),
        );
    } else if (job.layer === "rereview" || job.layer === "firstReview") {
        const again = job.layer === "rereview";
        lines.push(
            `1. ${again ? "Re-run" : "Run"} \`.github/skills/pr-review\` on head ${String(job.head || "").slice(0, 7)} in the PR's session: ${s?.id ? `it's linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}; message it with \`send_session_message\`` : `use \`open_pr_session\` if you have it, else \`create_session\` (name it "PR #${card.number} review"), and link it with \`pr_board_link_session\``}. If you have none of these tools, do it here.`,
            `2. Put the result on the board with \`pr_board_set_review({ repo: "${repo}", pr: ${card.number}, review })\` (call \`pr_board_review_guide\` first), with headSha ${job.head || "<head>"}.${again ? " Keep finding ids stable for findings that are still there, so the user's Post/Ignore choices stay." : ""}`,
            `3. ${WHAT_CHANGED_LINE(card, repo)}`,
            `4. Set the next step with \`pr_board_set_next_step\` \`{ repo: "${repo}", pr: ${card.number}, owner: "you", action }\`, where action is the decision in one line, for example ${again ? `"Re-review done: both asks fixed, no new findings → approve?"` : `"Review drafted: 1 blocking finding → request changes?"`}. The board shows a Post review… button for the draft.`,
        );
    } else if (job.layer === "issues") {
        lines.push(
            `1. Read the reply (\`gh issue view ${card.number} -R ${repo} --comments\`). ${WHAT_CHANGED_LINE(card, repo)}`,
            `2. Try to reproduce or validate what the reporter says, with evidence (the commands you ran and their output). ${s?.id ? `The issue is linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}: ask it with \`send_session_message\` and have it report back.` : `Use a session for it (\`create_session\`, then \`pr_board_link_session\` with issue ${card.number}) if you can; else do it here.`} Don't change code or open a PR unless the issue's session was already doing that.`,
            `3. Report with \`pr_board_reply\` \`{ repo: "${repo}", issue: ${card.number}, from: <who did it>, text }\`. Start with one of "Reproduced", "Not reproduced" or "Fixed by #n", then the evidence in a few lines.`,
            `4. Set the next step with \`pr_board_set_next_step\` \`{ repo: "${repo}", issue: ${card.number}, owner: "you", action, choices? }\`: what the user should decide, for example "Not reproduced on 0.8.1: ask for logs, or close?".`,
        );
    }
    lines.push("", `Keep the card current with \`pr_board_set_status\` ("running" while you work, "failed" with a reason if you give up). ${PROGRESS_LINE}`, EXPLAINER_LINE(card, repo), AUTO_GUARDRAILS);
    return lines.join("\n");
}
export function summarizeRepliesPrompt(card, repo) {
    return [
        `The PR board canvas: the user asked to **summarize the author's replies** on ${repo}#${card.number} "${card.title}" (${card.url}).`,
        `Read the review threads and comments (\`gh pr view ${card.number} -R ${repo} --comments\`, and \`gh api repos/${repo}/pulls/${card.number}/comments\` for inline threads). Summarize what the author (${card.author}) said or changed in response to each review point, in plain words: which points are addressed, which are pushed back on (and why), and which are still open.`,
        `Reply with \`pr_board_reply\` \`{ repo: "${repo}", pr: ${card.number}, from: "Reply summary", text: <a short list> }\`. Read-only: don't post or resolve anything.`,
    ].join("\n");
}

export function explainChangesPrompt(card, repo, changes) {
    return [
        `The PR board canvas: the user asked **what changed since their review** on ${repo}#${card.number} "${card.title}" (${card.url}).`,
        `Compare \`${changes.base}\`...\`${changes.head}\` (${changes.commits.length} commit(s), ${changes.files.length} file(s)). The board already shows the raw commit and file list, so don't repeat it.`,
        `Read the diff (\`gh api repos/${repo}/compare/${changes.base}...${changes.head}\`, or check out the commits) and explain in 3–6 plain bullets what changed and whether it addresses the earlier review feedback. Say what still needs a look.`,
        `Reply with \`pr_board_reply\` \`{ repo: "${repo}", pr: ${card.number}, from: "What changed", text: <bullets> }\`. Read-only.`,
    ].join("\n");
}

export function startIssuePrompt(card, repo) {
    return [
        `The PR board canvas: the user asked to **start working on** issue ${repo}#${card.number} "${card.title}" (${card.url}).`,
        `Create one visible session for it (\`create_session\`, or \`open_issue_session\` if you have it) with a kickoff that quotes the issue link and asks it to reproduce, plan, and fix it, following the repository's normal workflow. Don't open a PR unless the user asks.`,
        `As soon as you have the session id, call \`pr_board_link_session\` \`{ repo: "${repo}", issue: ${card.number}, sessionId, sessionName }\`, then \`pr_board_sync_sessions\` with \`{ issue: ${card.number}, sessionId, activity: "busy", status: <one plain line> }\` whenever its state changes.`,
        `Pass the session's branch too (\`branch\` on \`pr_board_link_session\` / \`pr_board_sync_sessions\`), so the board can show the issue's PR once it exists.`,
        `If you can't create sessions, say so with \`pr_board_reply\` \`{ repo: "${repo}", issue: ${card.number}, text: <one line> }\`.`,
        EXPLAINER_LINE(card, repo),
    ].join("\n");
}

export function explainPrompt(card, repo, f) {
    return [
        `The PR board canvas: the user asked to **explain a review finding** on ${repo}#${card.number} "${card.title}" (${card.url}).`,
        `Finding \`${f.id}\` [${f.severity} ${f.category}]: ${f.title}`,
        f.files?.length ? `Files: ${f.files.map((x) => x.path + (x.line ? ":" + x.line : "")).join(", ")}` : "",
        f.description ? `Description: ${f.description.replace(/\s+/g, " ").slice(0, 600)}` : "",
        `Read the code involved and explain in plain words: what is raised, why it matters to users, and what to do about it (or why it might be fine). 3-6 short sentences or bullets.`,
        `Call \`pr_board_set_explanation({ repo: "${repo}", pr: ${card.number}, findingId: "${f.id}", text })\`. Read-only: don't post anything.`,
    ].filter(Boolean).join("\n");
}

export function postFindingsPrompt(card, repo, event, preview, files) {
    const word = { approve: "Approve", request_changes: "Request changes", comment: "Comment" }[event];
    return [
        `The PR board canvas: the user reviewed the findings on ${repo}#${card.number} "${card.title}" (${card.url}), previewed exactly what will be posted, and asked to post it as **${word}**.`,
        `It has ${preview.counts.inline} inline comment(s) and ${preview.counts.inBody} finding(s) in the summary body. The exact request body is in:`,
        `\`${files.full}\``,
        `Post it unchanged: \`gh api repos/${repo}/pulls/${card.number}/reviews --method POST --input "${files.full}"\`.`,
        `If GitHub rejects an inline comment (422, line not part of the diff), post \`${files.bodyOnly}\` instead, which has the same comments folded into the body.`,
        `Don't edit the text, change the event, or post anything else. Then call \`pr_board_set_status({ repo: "${repo}", pr: ${card.number}, status: "posted", outcome: "Posted: ${word.toLowerCase()}, ${preview.counts.posted} finding(s)" })\`. If it fails, call it with status "failed" and the error in one line.`,
    ].join("\n");
}

export function openChatPrompt(card, repo) {
    const s = card.local.session;
    return [
        header("Open chat", card, repo),
        ``,
        `Switch the user's view to the session linked to this PR: \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}.`,
        `If you have \`navigate_to\`, call it with \`{ id: "${s.id}" }\`. Otherwise tell the user the session name and id in one line. Do nothing else.`,
    ].join("\n");
}

// What the "Explain like I'm 5" and "Where we are" sections should say. The text always comes from the agent.
export function EXPLAINER_RULES(repo, card) {
    const which = card.kind === "issue" ? "issue" : "pr";
    return [
        `How to call \`pr_board_set_explainer\` \`{ repo: "${repo}", ${which}: ${card.number}, eli5, progress }\` (Markdown, written for a PM or a junior developer who has never seen this code):`,
        `- eli5 ("Explain like I'm 5", about 120-250 words): what the feature or area is, in a sentence of background; the problem or change, with one concrete example (what you do, what happens, what should happen); who runs into it and why it matters; and where it stands on GitHub now (comments, reviews, CI, who it's waiting on). No file or class names unless they're essential.`,
        `- progress ("Where we are", about 60-150 words): what we've already done in our sessions, the current state, each decision made and when (for example "Nov 18: ship the small fix first"), and the next step with who takes it.`,
        `- Write it yourself from what you know; don't use a template. Short paragraphs or bullets.`,
    ].join("\n");
}

export function explainerPrompt(card, repo) {
    const k = KIND(card);
    const s = card.local?.session;
    const read = card.kind === "issue"
        ? `\`gh issue view ${card.number} -R ${repo} --comments\``
        : `\`gh pr view ${card.number} -R ${repo} --comments\` and \`gh pr checks ${card.number} -R ${repo}\``;
    return [
        `The PR board canvas: the user asked to **explain** ${repo}#${card.number} "${card.title}" (${card.url}) in plain language.`,
        `Read it on GitHub (${read}). Read the board's thread for it (\`pr_board_get_state\`).`,
        s?.id
            ? `It's linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}: read its state (\`get_session\`), and if you need details, ask it with \`send_session_message\` for what it has done, what was decided, and what's next.`
            : `No session is linked to this ${k}, so "Where we are" covers what has happened on GitHub and on the board.`,
        `Then call \`pr_board_set_explainer\`.`,
        ``,
        EXPLAINER_RULES(repo, card),
        ``,
        `Read-only: don't post to GitHub or change the ${k}.`,
    ].join("\n");
}

const ref2 = (card) => (card.kind === "issue" ? `issue: ${card.number}` : `pr: ${card.number}`);

export function choosePrompt(card, repo, next, choice) {
    const s = card.local?.session;
    return [
        `The PR board canvas: on ${repo}#${card.number} "${card.title}" (${card.url}), the user answered your question${next?.action ? ` "${next.action}"` : ""} with: **${choice}**.`,
        s?.id
            ? `It's linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}; if the work happens there, pass the decision on with \`send_session_message\` and ask it to report back.`
            : `No session is linked; act on it yourself.`,
        `Treat it as the user's decision. Confirm in one line with \`pr_board_reply\` \`{ repo: "${repo}", ${ref2(card)}, text }\`, then set the next step with \`pr_board_set_next_step\` (owner "agent" while work runs).`,
        `Don't post to GitHub or approve unless the choice says so. Never merge, queue or turn on auto-merge yourself: the user does that with the board's Merge button, which confirms first. If the choice is "do X, then merge", do X, then set the next step for the user (owner "you") with the choice "Merge", which opens that dialog.`,
    ].join("\n");
}

export function openPrPrompt(card, repo) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user asked to **open a pull request** for ${repo}#${card.number} "${card.title}" (${card.url}).`,
        s?.id
            ? `Ask its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\` to open the PR from its branch, following the repository's PR template and \`.github/skills/pr-lifecycle\`. Open it as a draft unless the work is ready for review.`
            : `No session is linked; say so with \`pr_board_reply\` and ask the user which branch to use.`,
        `Then link it: \`pr_board_sync_sessions\` with \`{ issue: ${card.number}, linkedPr: <number> }\`, and set the next step (\`pr_board_set_next_step\`, usually owner "reviewer").`,
    ].join("\n");
}

export function fixPrompt(card, repo, what) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user asked the agent to **fix what's blocking** ${repo}#${card.number} "${card.title}" (${card.url})${what ? `: ${what}` : ""}.`,
        s?.id
            ? `Send it to its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\`.`
            : `No session is linked: use \`open_pr_session\` or \`create_session\` if you have them, otherwise say what's needed with \`pr_board_reply\`.`,
        `Set \`pr_board_set_next_step\` owner "agent" while it works, and report the result with \`pr_board_reply\`. Don't merge.`,
    ].join("\n");
}

// Private branch review: comments the user wrote on a branch that has no PR yet. Nothing goes to GitHub.
export function branchCommentsPrompt(card, repo, { branch, head, comments }) {
    const s = card.local?.session;
    const list = comments.map((c, i) => {
        if (!c.file) return `${i + 1}. **Overall note:**\n${c.text}`;
        const where = `\`${c.file}:${c.start}${c.end !== c.start ? `-${c.end}` : ""}\`${c.side === "old" ? " (removed lines)" : ""}`;
        return `${i + 1}. ${where}\n\`\`\`diff\n${c.excerpt}\n\`\`\`\n${c.text}`;
    }).join("\n\n");
    return [
        `The PR board canvas: the user reviewed branch \`${branch}\` for ${repo}#${card.number} "${card.title}" (${card.url}) **privately, before a PR exists**, and sent ${comments.length} comment${comments.length === 1 ? "" : "s"} on commit ${String(head || "").slice(0, 7)}. None of this is on GitHub.`,
        s?.id
            ? `Forward them to its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\`, quoted exactly. Ask it to address each one (or say why not), push to \`${branch}\`, and report back per comment. Don't open a PR unless the user asks.`
            : `No session is linked to this issue. Say so with \`pr_board_reply\` and ask the user which session should take them.`,
        `Then set \`pr_board_set_next_step\` \`{ repo: "${repo}", issue: ${card.number}, owner: "agent", action: "Addressing your ${comments.length} comment${comments.length === 1 ? "" : "s"}" }\`.`,
        `When the session reports back, post a short per-comment summary with \`pr_board_reply\` \`{ repo: "${repo}", issue: ${card.number}, text, from }\`, clear the next step (the board shows "Re-review the changes" once the branch moves), and update the explainer with \`pr_board_set_explainer\` if a decision was made.`,
        ``,
        `The comments:`,
        ``,
        list,
    ].join("\n");
}

export function openBranchPrPrompt(card, repo, { draft, branch }) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user asked to **open a ${draft ? "draft " : ""}pull request** for ${repo}#${card.number} "${card.title}" (${card.url})${branch ? ` from branch \`${branch}\`` : ""}.`,
        s?.id
            ? `Ask its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\` to open it ${draft ? "as a **draft**" : "**ready for review**"}, following the repository's PR template and \`.github/skills/pr-lifecycle\`, with "Fixes #${card.number}" in the body.`
            : `No session is linked; say so with \`pr_board_reply\` and ask the user which branch to use.`,
        `Then link it: \`pr_board_sync_sessions\` with \`{ issue: ${card.number}, linkedPr: <number> }\`. Set the next step (\`pr_board_set_next_step\`, usually owner "reviewer"), reply with the PR link (\`pr_board_reply\`), and update the explainer (\`pr_board_set_explainer\`).`,
    ].join("\n");
}

// "Get it ready for review": run pr-lifecycle to the ready-for-review label, with Agent merge watching.
export function readyForReviewPrompt(card, repo, readiness) {
    const s = card.local?.session;
    const core = (readiness?.gates || []).filter((g) => g.state !== "pass").map((g) => `- ${g.label}: ${g.reason}${g.url ? ` (${g.url})` : ""}`);
    const extra = (readiness?.extras || []).filter((x) => x.state === "todo" || x.state === "decide" || x.state === "warn").map((x) => `- ${x.label}: ${x.reason}${x.items?.length ? "\n" + x.items.map((f) => `  - [${f.severity}] ${f.title} (id ${f.id})`).join("\n") : ""}`);
    return [
        `The PR board canvas: the user asked to **get ${repo}#${card.number} "${card.title}" ready for human review and keep it ready** (${card.url}).`,
        s?.id
            ? `Ask its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\` to follow \`.github/skills/pr-lifecycle\` (Prepare, Present, Finish) to the \`ready-for-review\` label for the current head, and report back.`
            : `No session is linked. If you have \`open_pr_session\`, open one for this PR and ask it to follow \`.github/skills/pr-lifecycle\` to the \`ready-for-review\` label; otherwise do it yourself.`,
        `As part of that, it should run pr-review on the latest non-merge commit and **fix the obvious blocking findings itself**. For a finding it disagrees with or can't fix safely, it records why, and the board asks the user to decide. While this is on, pr-review is a core gate: "ready" needs a clean pr-review of the latest non-merge commit, and the board requests a Copilot review of each new non-merge commit itself.`,
        `Turn on Agent merge for the PR with \`manage_agent_merge\`: address_reviews, fix_ci and resolve_conflicts **on**, merge_pr **off**. Never merge, and never approve.`,
        core.length ? `Core gates (Copilot, pr-review, discussion, CI, up to date) for head ${String(readiness?.head || "").slice(0, 7)} that aren't green yet (these decide "ready"):\n${core.join("\n")}` : `Every core gate is green for head ${String(readiness?.head || "").slice(0, 7)}.`,
        extra.length ? `Also worth doing (they don't block "ready"):\n${extra.join("\n")}` : "",
        `Then call \`pr_board_sync_sessions\` for this PR with the session's id, role "author" and its \`agentMerge\` state, and set the next step with \`pr_board_set_next_step\` (owner "agent" while it works). Report the outcome with \`pr_board_reply\`, and update the explainer (\`pr_board_set_explainer\`) if a decision was made.`,
    ].filter(Boolean).join("\n");
}

export function fixFindingPrompt(card, repo, f) {
    const s = card.local?.session;
    return [
        `The PR board canvas: on ${repo}#${card.number} "${card.title}" (${card.url}), the user asked to **fix this pr-review finding**:`,
        `[${f.severity}] ${f.title}${f.file ? ` (${f.file}${f.line ? ":" + f.line : ""})` : ""}\n${f.description || ""}${f.recommendation ? `\nSuggested fix: ${f.recommendation}` : ""}`,
        s?.id
            ? `Send it to its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\`: fix it, validate, push, and report back.`
            : `No session is linked: use \`open_pr_session\` if you have it, otherwise say what's needed with \`pr_board_reply\`.`,
        `Set \`pr_board_set_next_step\` owner "agent" while it works, and report with \`pr_board_reply\`. Don't merge or approve.`,
    ].join("\n");
}

export function fixDescriptionPrompt(card, repo, issue) {
    const s = card.local?.session;
    return [
        `The PR board canvas: the user asked to link issue #${issue} in the description of ${repo}#${card.number} "${card.title}" (${card.url}).`,
        `Add "Fixes #${issue}" to the PR body (keep the rest of the body unchanged), with \`gh pr edit ${card.number} -R ${repo} --body-file <file>\`${s?.id ? `, or ask its session \`${s.id}\` to do it` : ""}. If #${issue} isn't actually fixed by this PR, don't add it; say why with \`pr_board_reply\` instead.`,
        `Confirm in one line with \`pr_board_reply\` \`{ repo: "${repo}", pr: ${card.number}, text }\`.`,
    ].join("\n");
}

export function requestReviewerPrompt(card, repo, who) {
    const anyone = !who || who === "*";
    return [
        `The PR board canvas: the user asked to **request a human review** on ${repo}#${card.number} "${card.title}" (${card.url}); its readiness gates are green.`,
        anyone
            ? `Pick a suitable reviewer (a CODEOWNER for the changed paths, or a maintainer who reviewed this area recently), tell the user who and why in one line, then request them with \`gh pr edit ${card.number} -R ${repo} --add-reviewer <login>\`.`
            : `Request **${who}** with \`gh pr edit ${card.number} -R ${repo} --add-reviewer ${who}\`.`,
        `Don't approve, merge or change labels. Confirm with \`pr_board_reply\` \`{ repo: "${repo}", pr: ${card.number}, text }\`, and set the next step to owner "reviewer" with \`pr_board_set_next_step\`.`,
    ].join("\n");
}

// Keep it ready: a new non-merge commit landed on a watched PR, so pr-review needs to run on it again.
export function rerunReviewPrompt(card, repo, sha) {
    const s = card.local?.session;
    return [
        `The PR board canvas: ${repo}#${card.number} "${card.title}" (${card.url}) is being kept ready for review, and its latest non-merge commit is now ${String(sha).slice(0, 7)}. pr-review hasn't run on it.`,
        s?.id
            ? `Ask its session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""} with \`send_session_message\` to run \`.github/skills/pr-review\` on ${String(sha).slice(0, 7)}, fix the obvious blocking findings, re-check the pr-lifecycle label for the current head, and report back.`
            : `No session is linked: use \`open_pr_session\` if you have it, otherwise run pr-review yourself.`,
        `Record the result with \`pr_board_set_review\` (headSha ${sha}) or \`pr_board_sync_sessions\` with \`prReview: { ranAt, headSha: "${sha}", result }\`. Set the next step (owner "agent" while it works), and report with \`pr_board_reply\`. Don't merge or approve.`,
    ].join("\n");
}
