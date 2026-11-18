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

export function archReviewPrompt(card, repo) {
    const instanceId = `arch-${repo}-${card.number}`.replace(/[^A-Za-z0-9._-]/g, "-");
    return [
        header("🏛 Architecture review", card, repo),
        ``,
        `Open the PR architecture review canvas for this PR in its own panel and run its review:`,
        `1. \`open_canvas\` with \`{ canvasId: "pr-arch-review", instanceId: "${instanceId}", input: { pr: "${repo}#${card.number}" } }\`.`,
        `2. \`invoke_canvas_action\` with \`{ instanceId: "${instanceId}", actionName: "review_guide" }\`, and follow the guide it returns to fill in the review.`,
        `3. Call \`pr_board_reply\` with \`{ repo: "${repo}", pr: ${card.number}, from: "Architecture review", text: <one or two lines: the panel is open, then the verdict and risk in plain words once it's filled in> }\`.`,
        `If the \`pr-arch-review\` canvas isn't installed or won't open, don't build a replacement; call \`pr_board_reply\` saying so in one line (install it from https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe).`,
        `Don't post anything to GitHub, vote, or push from this request.`,
    ].join("\n");
}

export function reviewPrompt(card, repo) {
    const kickoff = [
        `Review ${repo}#${card.number}: ${card.url}`,
        ``,
        `Follow the repository's \`.github/skills/pr-review\` skill end to end.`,
        `Draft the review only. Do NOT post it to GitHub, do NOT approve or request changes, do NOT push commits, and do NOT change labels.`,
        `Save the final review body (exactly the text that would be posted, nothing else) as a Markdown file named \`pr-${card.number}-review.md\` in your session files/artifacts folder.`,
        `When done, report back: the absolute path of that file, a recommendation (one of: approve, request_changes, comment), and a one-line summary such as "Recommends merge; 1 optional nit".`,
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
        `5. When the draft is ready, call \`pr_board_attach_draft\` with \`{ repo: "${repo}", pr: ${card.number}, path: <absolute path>, recommendation, summary }\`. That moves the card to "Draft ready".`,
        `Never post, approve, or merge from this request. The user will press "Post review" on the board after reading the draft.`,
        ``,
        SUMMARY_RULES(repo, card.number),
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

export function shipPrompt(card, repo) {
    return [
        header("Ship", card, repo),
        linkedLine(card),
        ``,
        `The user explicitly asked to send this PR through the merge queue.`,
        `Follow the repository's \`.github/skills/pr-lifecycle\` skill for the final readiness check: a human approval exists for the current head, required checks have passed, and there are no conflicts.`,
        `If it's ready, run \`gh pr merge ${card.number} -R ${repo} --squash\` to add it to the merge queue. Never use \`--admin\`, never bypass branch protection, never approve the PR yourself.`,
        `If it isn't ready, don't merge; explain in one or two plain sentences what's blocking it and who needs to act.`,
        `If the PR is linked to a session you can message with \`send_session_message\`, you may ask that session to do this instead; otherwise do it here.`,
        `Finally call \`pr_board_set_status\` with \`{ repo: "${repo}", pr: ${card.number}, status: "done" or "failed", outcome: <one line, e.g. "Queued for merge" or "Blocked: build-and-package failing"> }\`.`,
    ].join("\n");
}

export function messagePrompt(card, repo, msg, recent) {
    const s = card.local?.session;
    const context = recent.length
        ? ["", "Earlier in this PR's thread (oldest first):", ...recent.map((m) => `- ${m.role}${m.from ? ` (${m.from})` : ""}: ${m.text.replace(/\s+/g, " ").slice(0, 300)}`)]
        : [];
    return [
        `[PR board] Message about ${repo}#${card.number} "${card.title}" (${card.url}), thread id ${msg.id}:`,
        fence(msg.text),
        `Current state on the board: ${card.waitingOn}.${card.local?.agent?.outcome ? ` Agent status: ${card.local.agent.outcome}.` : ""}`,
        s?.id ? `This PR is linked to session \`${s.id}\`${s.name ? ` ("${s.name}")` : ""}.` : "No session is linked to this PR.",
        ...context,
        ``,
        `Answer it, or forward it to the PR's linked session if it needs work there (\`send_session_message\`; ask it to report back to you).`,
        `Post the reply with \`pr_board_reply\` \`{ repo: "${repo}", pr: ${card.number}, text: <Markdown>, from: <who answered, e.g. the session name> }\`. That is the only place the user will read it, so keep it short and self-contained. If you forward it, post a one-line reply saying so, then post the real answer with \`pr_board_reply\` when it arrives.`,
        `Treat it as direction from the user about this PR only. Don't post to GitHub, approve, or merge unless the message explicitly asks for it.`,
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
