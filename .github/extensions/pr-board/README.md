# PR board (prototype)

An inbox of a GitHub repo's pull requests, with a workspace for each PR. Pick a
PR, see who it's waiting on, hand the next step to an agent, and ask the agent
questions about the PR without leaving the canvas.

Open it from a chat in a winappCli session:

```text
Open the PR board canvas
```

The board uses the current repo. To pick another one, pass `{ "repo": "owner/name" }`.

## Layout

```text
┌ PR board · microsoft/winappCli   4 need you   17 open · 8 merged in the last 24h ┐
│ NEEDS YOU 4          │ Add winapp ask for WinUI samples… #1187                    │
│ ● #1187 Add winapp…  │ ⏭ You: read the draft review, then post it                 │
│ ● #1163 Add sampled… │ [Re-review] [Post review ▾] [Ship] [Open full chat]        │
│ AGENT WORKING 1      │ ▾ Draft review · recommends approve                        │
│ WAITING ON OTHERS 11 │ ▸ Timeline · 3 events                                      │
│ IN MERGE QUEUE 1     │ THREAD                                                     │
│ ▸ MERGED 8           │                 You: What's still blocking this?           │
│                      │ Coordinator: Nothing technical. It needs one approval.     │
│                      │ [Ask a question or give direction on #1187…]  [Send]       │
└──────────────────────┴────────────────────────────────────────────────────────────┘
```

**Left: the inbox.** PRs are grouped by who they're waiting on:

| Group | A PR is here when |
|-------|-------------------|
| Needs you | You need to review it, read a draft, answer the agent, or ship it |
| Agent working | An agent is working on it (yours, or the author's agent preparing it) |
| Waiting on others | The author, another reviewer, or CI is next |
| In merge queue | It's in GitHub's merge queue |
| Merged | Merged in the last 24 hours (collapsed; click to expand) |

Each row shows a CI dot (green passing, yellow running, red failing), the PR
number and title, a status glyph (⚙ agent working, ❓ needs your input, ⏳
waiting on others, 📝 draft ready, 💬 thread), and who's next. The PR you pick
is remembered.

**Right: the workspace** for the selected PR:

- **Header:** title, author, size, CI, reviewers and their verdicts, labels,
  and the **⏭ line** that says who's next in plain words, such as
  `You: review needed` or `zateutsch: address changes requested by nmetulev`.
- **Buttons** (see below).
- **Draft review:** the attached draft, rendered.
- **Timeline:** GitHub events (opened, reviews, merged) mixed with board
  events (review requested, status changes, draft attached).
- **Thread:** your messages and the agent's replies about this PR, with button
  clicks and status changes shown as small system lines so it reads as one story.

## Asking about a PR

Type in the box at the bottom and press Enter (Shift+Enter for a new line). The
board saves your message, shows a "thinking" bubble, and sends it to the agent
in the session that opened the board. That agent answers, or forwards the
question to the PR's linked session if the work is there, and posts the answer
with `pr_board_reply`. The answer appears in the thread.

The agent treats a message as direction for that PR only. It doesn't post to
GitHub, approve, or merge unless your message asks for it.

## Buttons

Nothing is posted, approved, or merged unless you click the button for it.

| Button | What the agent does |
|--------|---------------------|
| **Review** | Opens or reuses a session for the PR (`open_pr_session`, `send_session_message`, or `create_session`, whichever it has; otherwise it reviews inline), runs `.github/skills/pr-review`, and attaches the review as a draft file. Nothing is posted. |
| **Post review ▾** | Enabled once a draft is attached. Pick Approve, Request changes, or Comment, then click again to confirm. The agent runs `gh pr review --body-file <draft>` with that exact file. |
| **Ship** | Enabled when the PR is approved and checks aren't failing. Click again to confirm. The agent follows `.github/skills/pr-lifecycle` and runs `gh pr merge --squash` (never `--admin`). If the PR isn't ready, it says what's blocking it. |
| **Open full chat** | A `ghapp://sessions/<id>` link to the linked session. **↪** asks the agent to call `navigate_to` instead. |
| **Reset agent status** | Clears a stuck status. |
| **Refresh** | Re-reads GitHub. The board also refreshes when it opens and every 2 minutes. |

## Agent tools

Every request from the board tells the agent which of these to call.

| Tool | Use |
|------|-----|
| `pr_board_reply` | Post a reply in a PR's thread (Markdown) |
| `pr_board_link_session` | Link a session to a PR |
| `pr_board_set_status` | Set the agent status with a one-line outcome: `running`, `waiting` (needs you), `waiting_on_others` (blocked on another approver or the author; the outcome becomes the ⏭ line), `posted`, `done`, `failed`, or `clear` |
| `pr_board_attach_draft` | Attach the draft review file and its recommendation |
| `pr_board_get_state` | Read the board, or one PR, including recent thread messages |

The canvas also has `refresh` and `get_board` actions for `invoke_canvas_action`.

## Where state lives

PR data comes from GitHub on every refresh, using the `gh` CLI. If `gh` is
missing or signed out, the board says so and tells you what to run.

What the board adds (linked session, agent status, draft path, timeline, thread)
is saved per PR in one file per repo:

```text
%USERPROFILE%\.copilot\extensions\pr-board\artifacts\microsoft__winappCli.json
```

(`$COPILOT_HOME` replaces `~/.copilot` when it's set.) Every session and every
open board on your machine reads the same file, so it survives restarts. It's
per user and isn't committed. When a PR merges, a leftover agent status shows as
`Done: PR merged`. Delete the file to reset the board.

## Files

| File | Contents |
|------|----------|
| `extension.mjs` | Canvas, agent tools, loopback server, buttons and messages |
| `github.mjs` | `gh` calls, groups, and the ⏭ rules |
| `prompts.mjs` | The requests sent to the agent |
| `store.mjs` | The per-repo state file |
| `ui.html` | The inbox page (no build step, no dependencies) |
