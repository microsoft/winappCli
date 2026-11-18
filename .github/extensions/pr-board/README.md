# PR board (prototype)

An inbox of a GitHub repo's pull requests, with a workspace for each PR. The
list tells you what each PR does, how it's doing, and what happens next, so
you don't have to open it. Pick one to read its description, conversation and
files, ask the agent about it, or hand it the next step.

Open it from a chat in a winappCli session:

```text
Open the PR board canvas
```

The board uses the current repo. To pick another one, pass `{ "repo": "owner/name" }`.

## The list

One flat list of open pull requests, newest first. A row never moves when its
status changes; the status is shown in the row. Each row has a status column on
the left, so you can scan the states top to bottom:

```text
[Your review]        Add winapp ask for WinUI samples and Microsoft Learn docs   [Review]
for 22 days          #1187 · Jaylyn-Barbee · XL · ✓ CI passing · zateutsch
                     ✦ Adds `winapp ask` so you can ask a WinUI question and get…
```

- **Status chip:** an icon and a short label, such as `Your review`,
  `Author: changes requested`, `Approved · CI`, `In queue #2`, or `Draft`.
  Your move is a solid orange chip, and a stuck PR is a solid red chip, with
  `Stuck · 12 days` under it. Other states are tinted: purple for an agent,
  blue for CI or the merge queue, gray for the author or other reviewers.
- **Second line:** the number, author, size, CI (icon and label), and reviews.
- **Third line:** the agent's plain-words summary (✦), the agent's latest
  status when it's working, or the first sentence of the description.
- **The button** appears when it's your move (Review, Post review, Ship,
  Answer), and on hover for other rows.

Above the list:

- **Filters**, with counts: All open, Needs you, Stuck, Waiting on others,
  Agent working, Mine, Drafts, and Merged today. Filters hide rows; they never
  reorder them. Merged PRs appear only under **Merged today**.
- **Sort:** Newest (default), Oldest, Recently updated, or Number. The board
  remembers your choice.
- **Pin:** pin a PR to the top with **More → Pin to top of the list**. Pins are
  saved per repo in this board.

Use ↑ and ↓ to move through the list. Drag the line between the list and the
workspace to resize it. In a panel narrower than about 760px, the list and the
workspace take turns; **← All pull requests** goes back. **Summarize all
needing me** asks the agent to summarize every PR that needs you or an agent
and doesn't have a summary yet.

## Why a PR is waiting, and for how long

Every open PR gets one reason, worked out from GitHub data, with how long it's
been in that state. The row shows the reason and how long it has been waiting,
for example `Author: changes requested` with `for 2 days` under it:

| Reason | Meaning | Stuck after |
|--------|---------|-------------|
| Waiting on your review | Ready for review; no human review yet | 3 days |
| Author responded, needs re-review | Changes were requested, then the author pushed or replied. Moves to **Needs you** | 3 days |
| Changes requested, no response | No push or reply from the author since the change request | 5 days |
| Draft, author still working | Draft PR; timed from the last push | 14 days |
| Author still preparing it | The author's agent is still working (`agent-preparing`) | 3 days |
| Waiting on a reviewer | Your PR; nobody has reviewed it yet | 3 days |
| Approval doesn't count | Someone approved, but GitHub still needs an approval that counts | 1 day |
| Approved, blocked | Approved, but failing checks, conflicts, or behind main | 1 day |
| Approved, waiting on CI | Approved; checks still running (says whether auto-merge is on) | 2 hours |
| Auto-merge on | Approved and green; waiting to enter the merge queue | 3 hours |
| In merge queue | Position N in the queue | 3 hours |
| Agent waiting on others | An agent set `waiting_on_others`; shows its text | 3 days |

A stuck PR shows `⚠️ Stuck: no response to changes requested for 12 days`. The
thresholds live in `STUCK_AFTER_HOURS` at the top of the rules in `github.mjs`.

The Overview's Status section shows the reason, since when, and a **suggested
next action**: open a link (the checks or the merge queue), Review, Ship, or a
nudge such as **Nudge the author…**. A nudge puts a draft comment in the
message box:

```text
Please post this comment on #1310:

@Jaylyn-Barbee, friendly ping on the changes @zateutsch requested 2 days ago.
Is anything blocking you, or should someone else pick this up?
```

Edit it, then press Send to ask the agent to post it. Nothing is posted until
you send it.

## The workspace

Buttons: the one that fits the stage is blue (Review, Post review, Ship, or
Answer). **Summarize** is next to it, and **More** holds Architecture review,
Open full chat, Pin to top, Open on GitHub, and Reset agent status. Tabs:

| Tab | Shows |
|-----|-------|
| Overview | What the PR does in plain words, with a risk level; next steps as a checklist; status (who's next, CI and failing checks, reviews, merge queue, agent, session, size, labels); the draft review, if any; and the PR description |
| Conversation | GitHub comments and reviews, oldest first. Bot comments are collapsed |
| Files | Changed files with +/− counts, largest first |
| Thread | Your conversation with the agent about this PR |
| Timeline | GitHub events (opened, reviews, merged) and board events |

The message box at the bottom works from any tab. Type a question or a
direction and press Enter, for example `What's still blocking this?` or
`Explain the risky part in simple terms`. The agent in the session that opened
the board answers in the Thread tab, or forwards the question to the PR's own
session and posts the answer when it comes back.

**🏛 Architecture review** asks the agent to open the
[PR architecture review canvas](https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe)
(`pr-arch-review`) for this PR in its own panel, with diagrams, architecture
changes and findings. Install that extension separately. The Thread tab notes
the request and the agent's one-line result.

## What the buttons do

Nothing is posted, approved, or merged unless you click the button for it.

| Button | What the agent does |
|--------|---------------------|
| **Review** | Opens or reuses a session for the PR (`open_pr_session`, `send_session_message`, or `create_session`, whichever it has; otherwise it reviews inline), runs `.github/skills/pr-review`, sets the plain-words summary, and attaches the review as a draft file. Nothing is posted. |
| **Post review ▾** | Enabled once a draft is attached. Pick Approve, Request changes, or Comment, then click again to confirm. The agent runs `gh pr review --body-file <draft>` with that exact file. |
| **Ship** | Enabled when the PR is approved and checks aren't failing. Click again to confirm. The agent follows `.github/skills/pr-lifecycle` and runs `gh pr merge --squash` (never `--admin`). If the PR isn't ready, it says what's blocking it. |
| **✨ Summarize** | Reads the PR and sets a one-sentence summary for a non-expert, up to 3 next steps, and a risk level. |
| **🏛 Architecture review** | Opens `pr-arch-review` for the PR and follows its `review_guide`. |
| **Open full chat** | A `ghapp://sessions/<id>` link to the linked session. **↪** asks the agent to call `navigate_to` instead. |
| **Reset status** | Clears a stuck agent status. |
| **Refresh** | Re-reads GitHub. The board also refreshes when it opens and every 2 minutes. |

## Agent tools

Every request from the board tells the agent which of these to call.

| Tool | Use |
|------|-----|
| `pr_board_set_summary` | Set the plain-words summary (≤140 characters), next steps, and risk (`low`, `medium`, `high`) |
| `pr_board_reply` | Post a reply in a PR's thread (Markdown) |
| `pr_board_link_session` | Link a session to a PR |
| `pr_board_set_status` | Set the agent status with a one-line outcome: `running`, `waiting` (needs you), `waiting_on_others` (blocked on another approver or the author; the outcome becomes the ⏭ line), `posted`, `done`, `failed`, or `clear` |
| `pr_board_attach_draft` | Attach the draft review file and its recommendation |
| `pr_board_get_state` | Read the board, or one PR, including its summary and recent thread messages |

The canvas also has `refresh` and `get_board` actions for `invoke_canvas_action`.

## Where state lives

PR data comes from GitHub with the `gh` CLI: the list on every refresh, and a
PR's conversation and files when you select it (cached until the PR changes).
If `gh` is missing or signed out, the board says so and tells you what to run.

What the board adds (summary, linked session, agent status, draft path,
timeline, thread) is saved per PR in one file per repo:

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
| `github.mjs` | `gh` calls, groups, the ⏭ rules, waiting reasons and stuck thresholds, and fallback summaries |
| `prompts.mjs` | The requests sent to the agent |
| `store.mjs` | The per-repo state file |
| `ui.html` | The inbox page (no build step, no dependencies) |
