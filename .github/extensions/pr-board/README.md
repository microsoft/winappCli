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

The first screen answers one question for every PR and issue: **whose move is
it?**

```text
[Your move 3] [Agent working 2] [Waiting on others 12] [Not started 3]   Sort: Newest

┌ Your move 3 ───────────────────────────────────────────────────────────┐
│ ➜ Ready for you      #1197 Add Microsoft.UI.Reactor support     3h    │
│   Re-review: the author responded         [Re-review] Done  Snooze ▾  │
│ ? Needs your decision  #1283 Memory fix                          1h    │
│   Pick A or B                              [A] [B]  Done  Snooze ▾     │
└────────────────────────────────────────────────────────────────────────┘
Yours 6
  ◌  #1322 Refuse --devtools…   Agent: fixing the test lines        agent  1m
  ○  #1352 Keep major NuGet…    needs an approval from someone else  1h
  ○  Issue #1306 0x8007007B…    Waiting on reporter PhilMinecraft    2h
```

Each row has three separate signals:

| Signal | Shows | Goes away when |
|---|---|---|
| **The ball** (the glyph at the start of the row and the line under the title) | Whose move it is and what the move is | Only when someone moves it (you act, the agent or GitHub moves on), or you press **Done** / **Snooze** |
| **Activity** (a spinner) | An agent is working on it right now | The agent finishes |
| **Unread** (a blue dot) | There's agent output you haven't seen | You open it. This clears **only** the dot |

The ball is one of these states, checked top to bottom:

| State | Glyph | When |
|---|---|---|
| **Needs your decision** | orange `?` | A session is waiting on a question, the agent asked you something, or set a next step with choices |
| **Agent working** | spinner | A session is busy, or the agent is answering you |
| **Blocked** | red `!` | On your PR: CI failing, merge conflicts, or the agent failed |
| **Ready for you** | orange `→` | Your review or re-review is wanted, a drafted review is ready to post, or the agent handed you a step |
| **Next: open a PR** | blue PR | An issue's fix is on a branch, the session is idle, and there's no PR yet |
| **Waiting on others** | hollow ring | Someone else's move: the author, a reviewer, the merge queue, CI or the reporter, with their avatar and how long. The age turns red (`Stuck 28d`) when it's waited too long |
| **Done / Not started** | check / dash | Merged or closed, or assigned to you but not started |

**Your move** (the box at the top) gathers every item where the move is yours,
oldest first, with the move as buttons: a session question's choices plus a box
for a typed answer, **Review**, **Post review…**, **Ask the agent to fix**, or
**Open a PR**. Each item stays in the box until you act, or until you press:

- **Done:** you handled it elsewhere. It returns if something changes, for
  example new commits or a new question.
- **Snooze ▾:** for 1 hour, until tomorrow 9:00, or until Monday 9:00. Then it
  comes back by itself. **Unsnooze** is in its flyout.

The chips at the top count each state and filter the list to it.

Below the box, the sections keep their rows in place (newest first, or the order
you pick under Sort). A row's state changes, but its position doesn't:

| Section | What's in it |
|---------|-----------|
| **Yours** | PRs you wrote, and PRs and issues a session here is working on (for example a Dependabot PR you're fixing, or an issue you're investigating) |
| **Reviewing** | You're a requested reviewer, chose **Track under Reviewing** or ran **Review**, you reviewed it, or pr-review ran here |
| **Everything else** | Open PRs nobody is working on here |
| **Issues assigned to you** | Open issues assigned to you that nobody here is working on yet, with no open PR |

Each row shows the owner (avatar and name) and how long it's been that way. Hover
a row for its quick commands. Issue rows show `Issue #n`, and their flyout shows
the PR's state (`No PR`, `Draft PR #1322`, `PR #1340 open`, `Merged #1340`). The
board finds that PR from GitHub's "Fixes #n" links, from the linked session's
branch, or from a PR the agent names in `pr_board_sync_sessions`. An item appears
in one section only, and a linked issue stays under Yours for two days after it
closes.

At the bottom, collapsed: **Merged today**, then **Removed from the board**.
### The flyout

Click anywhere on a row to open a panel on the right, without leaving the
Overview. Each row also shows its first quick command as a button (for example
**What changed?** on a PR you need to re-review); hover the row to see two more,
or click **⋯**. Links and buttons inside a row do their own thing.

The flyout puts quick commands at the top, then the item's thread: type a
question and the agent answers there, without starting a new session. Esc or ✕
closes it; **Open details** goes to the full PR workspace (**← Overview** or Esc
comes back). Its **⋯** menu has:

- **Track under Reviewing** (PRs in Everything else): keeps the PR in your
  Reviewing list. It doesn't run a review or change anything on GitHub.
  **Stop tracking** takes it out again.
- **Mark as read**, when there's an unread reply.
- **Remove from board**: hides the PR or issue on this board only. Find it again
  under **Removed from the board** at the bottom of the Overview.

| Row | Quick commands |
|-----|----------------|
| Yours | Status update, Turn on/off monitoring, Summarize, Ask |
| Issues | Status update, Start working on it (the agent creates a session), Ask |
| Reviewing | **What changed since my review?** lists the commits and files changed since the commit you reviewed (read from GitHub right away), then asks the agent for a plain summary in the thread. Also Summarize the author's replies, Re-run review |
| Everything else | **Review** runs `.github/skills/pr-review` in a session and drafts findings for you to post; it also moves the PR to Reviewing. Also Quick question, Summarize |

The ↗ icon on every row, and **Open in browser** in the flyout, open the PR or
issue on GitHub in a new tab in the app.

### Explain like I'm 5

The top of every flyout and every PR's Overview tab has two plain-language
sections the agent writes for you:

- **Explain like I'm 5:** what the feature is, the problem or change with a
  concrete example, why it matters, and where it stands on GitHub (reviews, CI,
  who it's waiting on). It's written so a PM or someone new to the code
  understands it.
- **Where we are:** what we've done in our sessions so far, each decision and
  its date (for example "Nov 18: ship the small fix first"), and the next step
  with who takes it.

When an item has none, the section shows **Explain this**, which asks the agent
to write it. When new commits or GitHub activity arrive after it was written,
it says so and shows **Update**. The agent also updates it on its own after a
review, or when an answer in the thread changes the picture.

### Unread replies

You can ask something, close the flyout, and keep going. Until the answer
arrives, the row shows **Replying…**. When it arrives, the row gets a blue dot
and a **New reply** chip:

```text
● Fix stale DevTools comment cards after resolving from Comments  [New reply]
  Clarify inherited FontSize reporting in DevTools  [Replying…]
```

The **Unread 2** chip at the top shows only those rows, and **Mark all read**
clears them. A new draft or structured review also counts as a reply. Opening
the flyout, or the PR's Overview, Review or Thread tab, marks it read and
scrolls to a **New** line above the first unread message. The canvas title shows
the count, for example `(2) PR board`. Read state is saved per PR and issue in
the board's state file.

### Sync

The board can't read other sessions by itself, so the agent in the session that
opened the board reports them. **Sync** asks it to check the sessions working on
this repo's PRs and call `pr_board_sync_sessions`. The agent also calls it after
turning monitoring on or off.

An issue shows under **Issues in progress** only while a session here is linked
to it or it's assigned to you; opening an issue yourself doesn't count. To stop
tracking a linked issue, the agent calls `pr_board_sync_sessions` with
`{ issue: 1349, untrack: true }`.

## The list beside a PR

When a PR is open, a compact copy of the Overview sits on its left, with the
same sections, the same rows and the same order:

```text
All items                                   ‹
▾ YOURS 5
  #1322 Refuse --devtools before launch…
        [Agent needs you] [Replying…]
  Issue #1283 DevTools inspector still uses…
        [No PR]
▾ REVIEWING 6
▌ #1316 DevTools: assign resources and…     ← the PR you're looking at
        [Author: changes requested]
› EVERYTHING ELSE 6                          ← collapsed
```

- Click a PR to switch to it in place. You stay on the same tab, for example
  Review. Click an issue to open its flyout.
- Click a section header to collapse it. **‹** or **List** at the top hides the
  whole list, so the PR gets the full width. The board remembers both choices.
- In a panel narrower than about 900px, the list starts hidden. **List** opens
  it over the PR, and it closes again once you pick something.
- ↑ and ↓ move between rows and headers. Drag the line between the list and the
  PR to resize it.

**Status chips** use one icon and a short label everywhere, such as
`Your review`, `Author: changes requested`, `Approved · CI`, `In queue #2` or
`Draft`. Your move is a solid orange chip, and a stuck PR is a solid red chip
with `Stuck · 12 days` under it on the Overview. Other states are tinted:
purple for an agent, blue for CI or the merge queue, gray for the author or
other reviewers.

**Sort** (on the Overview) orders every section: Newest (default), Oldest,
Recently updated, or Number. **More → Pin to top of the list** keeps a PR at the
top of its section. Both are remembered. **Summarize all needing me** asks the
agent to summarize every PR that needs you or an agent and doesn't have a
summary yet.

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

After you press Send, the message box clears and keeps focus, and the thread
scrolls to the newest message. The thread also follows new replies while you're
at the bottom.

While the agent works, the reply bubble shows what it's doing, for example
`Reading the commits since the review…` or `Asking the linked session…`. The
lines come from the agent's own short notes and the tools it runs. The answer
itself appears all at once when the agent posts it with `pr_board_reply`.
Extensions don't get the answer text while it's being written, so it can't
appear word by word.

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
| **Review** | Opens or reuses a session for the PR (`open_pr_session`, `send_session_message`, or `create_session`, whichever it has; otherwise it reviews inline), runs `.github/skills/pr-review`, and fills in the structured review (`pr_board_review_guide`, then `pr_board_set_review`). Moves the PR to Reviewing if it isn't yours. Nothing is posted. |
| **Post review…** | With a structured review, goes to the Review tab to pick findings and preview the post (see above). With only a Markdown draft from `pr_board_attach_draft`, pick Approve, Request changes, or Comment, then click again to confirm; the agent runs `gh pr review --body-file <draft>` with that file. |
| **Ship** | Enabled when the PR is approved and checks aren't failing. Click again to confirm. The agent follows `.github/skills/pr-lifecycle` and runs `gh pr merge --squash` (never `--admin`). If the PR isn't ready, it says what's blocking it. |
| **✨ Summarize** | Reads the PR and sets a one-sentence summary for a non-expert, up to 3 next steps, and a risk level. |
| **Open full chat** | A `ghapp://sessions/<id>` link to the linked session. **↪** asks the agent to call `navigate_to` instead. |
| **Reset status** | Clears a stuck agent status. |
| **Refresh** | Re-reads GitHub. The board also refreshes when it opens and every 2 minutes. |

## Agent tools

Every request from the board tells the agent which of these to call.

`repo` is optional on every tool. Without it, the board uses the repo of the current folder, then the repo of the open board. If a tool fails, the error says why.

| Tool | Use |
|------|-----|
| `pr_board_sync_sessions` | Report the sessions on each PR or issue: activity, a pending question, Agent merge state, and when pr-review ran (on which commit). `untrack: true` stops tracking an item. For an issue, `branch` (the session's branch) or `linkedPr` lets the board show its PR |
| `pr_board_set_next_step` | Say whose move it is: `{ pr` or `issue, owner, action, choices?, blocked? }` (owner: `you`, `agent`, `author`, `reviewer`, `reporter` or `ci`), or `clear: true`. Owner `you` puts it in **Your move** (choices become buttons that come back to the agent); `agent` shows the spinner |
| `pr_board_set_explainer` | Write the "Explain like I'm 5" and "Where we are" sections for a PR or issue (`{ pr \| issue, eli5, progress }`, Markdown) |
| `pr_board_set_summary` | Set the plain-words summary (≤140 characters), next steps, and risk (`low`, `medium`, `high`) |
| `pr_board_reply` | Post a reply in a PR's thread (Markdown) |
| `pr_board_link_session` | Link a session to a PR or issue (pass `branch` for an issue) |
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
| `stream.mjs` | Turns the agent's notes and tool steps into the live progress line in a reply bubble |
| `review.mjs` | The structured review schema, guide, and how picked findings become a GitHub review |
| `diagram.js` | Draws the architecture diagrams as SVG |
| `ui.html` | The inbox page (no build step, no dependencies) |

## Credits

The structured review, the diagram renderer (`diagram.js`) and the review guide
are adapted from angelser's
[pr-arch-review](https://gist.github.com/angelser/01c49ffdd3296f58b25d78194bfe1ebe)
canvas.
