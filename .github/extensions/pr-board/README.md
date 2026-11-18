# PR board (prototype)

An overview of a GitHub repo's open pull requests, split into the ones you're
working on, the ones you're reviewing, and everything else. Each row says where
the PR is and what it needs, so you don't have to open it. Click a PR to open
its workspace: a plain-words review with architecture diagrams, changes by
system and by file, findings you pick to post, the conversation, and a thread
with the agent.

Open it from a chat in a winappCli session:

```text
Open the PR board canvas
```

The board uses the current repo. To pick another one, pass `{ "repo": "owner/name" }`.

## Overview

The first screen. The header shows `15 open · 6 waiting on you · 4 stuck`, when
sessions were last synced, and a **Sync** button. Each section keeps its rows in
place (newest first, or the order you pick under Sort); a row's state changes,
not its position.

| Section | Which PRs | Columns |
|---------|-----------|---------|
| **Yours** | You wrote it, or a session here is working on it (for example a Dependabot PR you're fixing) | Agent, Monitoring, pr-review, PR |
| **Issues in progress** | Open issues a session here is working on, or assigned to you, with no open PR yet | Agent, Last activity |
| **Reviewing** | You're a requested reviewer, added it with **Add to my reviews**, you reviewed it, or pr-review ran here | pr-review, PR, Last activity |
| **Everything else** | Open PRs nobody is working on here | PR |

- **Agent:** `Working`, `Waiting on you`, `Idle`, `Linked` (not synced yet), or
  `No session here` (it may be worked on another machine).
- **Monitoring:** whether Agent merge watches the PR (address reviews, fix CI,
  resolve conflicts). **Turn on** asks the agent to enable it per
  `.github/skills/pr-lifecycle`; it shows `Turning on…` until the agent confirms.
  It never merges unless you ask.
- **pr-review:** `Drafted`, `Posted` or `You reviewed`; `Re-run needed` /
  `Needs re-review` when new commits landed after the review; or `Not run`.
- **PR:** the waiting reason and how long, as in the list below.

**Reviewing** rows also show what's happened so far, for example
`You requested changes 1d ago → moooyo pushed 12h ago`.

When a session is waiting on you, its row shows the question with a button for
each choice, a box for a typed answer, and **Dismiss**. An answer goes to the
agent, which answers the session for you. Dismiss only hides the question on the
board; the session keeps waiting.

Merged PRs are in a collapsed **Merged today** section at the bottom.

### The flyout

Click anywhere on a row to open a panel on the right, without leaving the
Overview. Each row also shows its first quick command as a button (for example
**What changed?** on a PR you need to re-review); hover the row to see two more,
or click **⋯**. Links and buttons inside a row do their own thing.

The flyout puts quick commands at the top, then the item's thread: type a
question and the agent answers there, without starting a new session. Esc or ✕
closes it; **Open details** goes to the full PR workspace (**← Overview** or Esc
comes back). Its **⋯** menu has **Remove from board**, which hides the PR or
issue on this board only; find it again under **Removed from the board** at the
bottom of the Overview.

| Row | Quick commands |
|-----|----------------|
| Yours | Status update, Turn on/off monitoring, Summarize, Ask |
| Issues | Status update, Start working on it (the agent creates a session), Ask |
| Reviewing | **What changed since my review?** lists the commits and files changed since the commit you reviewed (read from GitHub right away), then asks the agent for a plain summary in the thread. Also Summarize the author's replies, Re-run review |
| Everything else | Quick question, Add to my reviews (tracked on the board only; nothing changes on GitHub), Start review, Summarize |

The ↗ icon on every row, and **Open in browser** in the flyout, open the PR or
issue in your default browser. The extension opens it for you, because links
inside the canvas may not leave the app.

### Sync

The board can't read other sessions by itself, so the agent in the session that
opened the board reports them. **Sync** asks it to check the sessions working on
this repo's PRs and call `pr_board_sync_sessions`. The agent also calls it after
turning monitoring on or off.

An issue shows under **Issues in progress** only while a session here is linked
to it or it's assigned to you; opening an issue yourself doesn't count. To stop
tracking a linked issue, the agent calls `pr_board_sync_sessions` with
`{ issue: 1349, untrack: true }`.

## The full list

In a PR's workspace, the left side shows one flat list of open pull requests, newest first. A row never moves when its
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

Buttons: the one that fits the stage is blue (Review, Post review…, Ship, or
Answer). **Summarize** is next to it, and **More** holds Open full chat, Pin to
top, Open on GitHub, and Reset agent status. Tabs:

| Tab | Shows |
|-----|-------|
| Overview | The review's verdict, risk and plain-words summary, with what the PR does for users; next steps; status (who's next, CI, reviews, merge queue, agent, session, size, labels); and the PR description. GitHub events are under **Timeline** |
| Architecture | Diagrams of what the PR touches. Green is new, orange is changed, red dashed is removed. A number on a box counts its findings. Click a box to see its files, its findings, and **Ask about this** |
| Changes | By system: each area the PR touches, with one plain line and +/− counts. By file: one plain line per file. Without a review it lists the files with +/− counts |
| Review | The findings, most severe first, each with what's wrong, the suggested fix, and the file and lines. Mark each one **Post**, **Ignore** or **Undecided**, and add a note. **Explain** asks the agent to explain the finding in plain words; the answer appears under it |
| Conversation | GitHub comments and reviews, oldest first. Bot comments are collapsed |
| Thread | Your conversation with the agent about this PR |

Until a review has run, Architecture, Changes and Review offer **Review this
PR**.

The message box at the bottom works from any tab. Type a question or a
direction and press Enter, for example `What's still blocking this?` or
`Explain the risky part in simple terms`. **Ask about this** on a finding,
diagram box, system or file fills it in with a question about that item. The
agent in the session that opened the board answers in the Thread tab, or
forwards the question to the PR's own session and posts the answer when it comes
back.

### Posting the findings you picked

On the Review tab, **Post review ▾** → Approve, Request changes or Comment shows
exactly what will be posted, before anything is sent:

```text
Preview: Comment  2 inline comments, 0 in the summary, 1 ignored, 1 undecided (not posted)

Summary comment
  Nice work on the undo answers. One question on rollback below.

Inline on src/…/DevToolsTap.EditRpc.inc:182
  [medium] A failed resource assign can leave a binding removed
  …
  Reviewer note: This is the one I would block on if the agent can be missing in practice.
```

Only findings marked **Post** go out. A finding with a file and line becomes an
inline comment; the rest go in the summary, under your note. Every comment ends
with "Generated with AI assistance (GitHub Copilot PR board). Reviewed by a
human." **Post this as …** asks the agent to post it with
`gh api repos/{owner}/{repo}/pulls/{n}/reviews`. If GitHub rejects an inline
line, it posts everything in the summary instead.

## What the buttons do

Nothing is posted, approved, or merged unless you click the button for it.

| Button | What the agent does |
|--------|---------------------|
| **Review** | Opens or reuses a session for the PR (`open_pr_session`, `send_session_message`, or `create_session`, whichever it has; otherwise it reviews inline), runs `.github/skills/pr-review`, and fills in the structured review (`pr_board_review_guide`, then `pr_board_set_review`). Nothing is posted. |
| **Post review…** | With a structured review, goes to the Review tab to pick findings and preview the post (see above). With only a Markdown draft from `pr_board_attach_draft`, pick Approve, Request changes, or Comment, then click again to confirm; the agent runs `gh pr review --body-file <draft>` with that file. |
| **Ship** | Enabled when the PR is approved and checks aren't failing. Click again to confirm. The agent follows `.github/skills/pr-lifecycle` and runs `gh pr merge --squash` (never `--admin`). If the PR isn't ready, it says what's blocking it. |
| **✨ Summarize** | Reads the PR and sets a one-sentence summary for a non-expert, up to 3 next steps, and a risk level. |
| **Open full chat** | A `ghapp://sessions/<id>` link to the linked session. **↪** asks the agent to call `navigate_to` instead. |
| **Reset status** | Clears a stuck agent status. |
| **Refresh** | Re-reads GitHub. The board also refreshes when it opens and every 2 minutes. |

## Agent tools

Every request from the board tells the agent which of these to call.

| Tool | Use |
|------|-----|
| `pr_board_sync_sessions` | Report the sessions on each PR or issue: activity, a pending question, Agent merge state, and when pr-review ran (on which commit). `untrack: true` stops tracking an item |
| `pr_board_set_summary` | Set the plain-words summary (≤140 characters), next steps, and risk (`low`, `medium`, `high`) |
| `pr_board_reply` | Post a reply in a PR's thread (Markdown) |
| `pr_board_link_session` | Link a session to a PR |
| `pr_board_set_status` | Set the agent status with a one-line outcome: `running`, `waiting` (needs you), `waiting_on_others` (blocked on another approver or the author; the outcome becomes the ⏭ line), `posted`, `done`, `failed`, or `clear` |
| `pr_board_review_guide` | Read first: the review workflow, plain-words rules, diagram conventions and schema |
| `pr_board_set_review` | Set the whole structured review: `{ pr, review }`, or `{ pr, path }` to a JSON file with the same shape (for sessions that don't have the board's tools) |
| `pr_board_update_review` | Change part of a review, such as the verdict or one diagram |
| `pr_board_add_findings` | Add or update findings by id. Your Post/Ignore choices stay when ids stay the same |
| `pr_board_set_explanation` | Put the answer to **Explain** under a finding |
| `pr_board_attach_draft` | Attach a Markdown draft review file and its recommendation (older flow) |
| `pr_board_get_state` | Read the board, or one PR, including its summary and recent thread messages |

The canvas also has `refresh` and `get_board` actions for `invoke_canvas_action`.

## Where state lives

PR data comes from GitHub with the `gh` CLI: the list on every refresh, and a
PR's conversation and files when you select it (cached until the PR changes).
If `gh` is missing or signed out, the board says so and tells you what to run.

What the board adds (summary, structured review, your Post/Ignore choices and
notes, linked session, agent status, timeline, thread) is saved per PR in one file per repo:

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
| `review.mjs` | The structured review schema, guide, and how picked findings become a GitHub review |
| `diagram.js` | Draws the architecture diagrams as SVG |
| `ui.html` | The inbox page (no build step, no dependencies) |

## Credits

The structured review, the diagram renderer (`diagram.js`) and the review guide
are adapted from angelser's
[pr-arch-review](https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe)
canvas.
