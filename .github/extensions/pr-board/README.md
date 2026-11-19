# PR board (prototype)

An overview of a GitHub repo's open pull requests, split into the ones you're
working on, the ones you're reviewing, and everything else. Each row says where
the PR is and what it needs, so you don't have to open it. Click a PR to open
its workspace: a plain-words review with architecture diagrams, changes by
system and by file, findings you pick to post, the PR as it is on GitHub, and a thread with the
agent.

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
| **Agent working** | spinner (sparkle when no session is busy right now) | A session is busy, the agent is answering you, Agent merge is addressing new feedback on a watched PR, or your PR is labeled `agent-preparing` or isn't ready for human review yet |
| **Blocked** | red `!` | On your PR: CI failing, merge conflicts, the `agent-blocked` label, or the agent failed |
| **Ready for you** | orange `→` | Your review or re-review is wanted, a drafted review is ready to post, you have unsent branch comments, the agent pushed after your branch comments, or the agent handed you a step |
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
  comes back by itself. **Unsnooze** is in its panel.

The chips at the top count each state and filter the list to it. **Watching N**
filters to the PRs Agent merge is watching.

A row never shows *Needs your decision* and a spinner together. If the agent is
also working on an item that needs you, the row says so in grey instead.

Below the box, the sections keep their rows in place (newest first, or the order
you pick under Sort). A row's state changes, but its position doesn't:

| Section | What's in it |
|---------|-----------|
| **Yours** | PRs you wrote, and PRs and issues a session here is working on (for example a Dependabot PR you're fixing, or an issue you're investigating) |
| **Reviewing** | You're a requested reviewer, chose **Track under Reviewing** or ran **Review**, you reviewed it, or pr-review ran here |
| **Everything else** | Open PRs nobody is working on here |
| **Issues assigned to you** | Open issues assigned to you that nobody here is working on yet, with no open PR |

Each row starts with what it is: a PR icon and `PR #1359` (`Draft PR` for drafts),
or an issue icon and `Issue #1321`. It also shows the owner (avatar and name) and
how long it's been that way. Hover a row for its quick commands.

**An issue and the PR that fixes it are one row.** Once a PR fixes an issue,
the PR row stands for both, with a chip:

```text
PR #1359  Stop the DevTools inspector from growing app memory   [◉ Fixes Issue #1283]
          Agent: Preparing it for review · 3/4 ready
```

The board links them when the PR says "Fixes #n", when the agent links them in
`pr_board_sync_sessions`, or when the issue's session branch has a PR. The issue
then has no row of its own anywhere: not in the Overview or Your move.
The PR's thread includes the issue's messages, marked *from Issue #1283*. If the
PR has no explanation of its own, it uses the issue's, and a session linked to
either one counts for both. The chip opens the issue. When the PR merges, the pair
moves to Merged together. An issue with no PR yet keeps its own row, showing
`No PR` or a closed PR. An item appears in one section only, and a linked issue
stays under Yours for two days after it closes.

At the bottom, collapsed: **Merged today**, then **Removed from the board**.
### Opening an item: peek or expand

Click anywhere on a row to open the item over the list. The list stays exactly
as it was underneath, dimmed. Each row also shows its first quick command as a
button (for example **What changed?** on a PR you need to re-review); hover the
row to see two more, or click **⋯**. Links and buttons inside a row do their own
thing.

```text
┌ Overview (dimmed) ───────────┬ PR #1350          ‹ 7/24 ›  ↗  ⤢ Expand ⋯ ✕ ┐
│ Your move                    │ Reject malformed element handles…           │
│  PR #1322 Refuse --devtools… │ Summary   GitHub                            │
│ Reviewing                    │ …                                           │
│  PR #1350 Reject malformed…  │ [Ready for you  Request reviewer ▾  Done]   │
│                              │ [Ask a question or give direction…]  Send   │
└──────────────────────────────┴─────────────────────────────────────────────┘
```

- **Peek** is the panel on the right, for a quick look.
- **Expand** (**⤢**, the `E` key, or drag the panel's left edge) grows it into
  a large sheet with the full workspace (see [The workspace](#the-workspace)).
  The list stays visible, dimmed, around its edges. In a panel narrower than
  about 700px, the sheet fills the width and has **← Back**. **⤡** goes back to
  peek. The board remembers which one you used last.
- **Close** with Esc, ✕, or a click on the dimmed list. You're back on the list
  with the same scroll position and the same row selected. A drag or text
  selection that starts inside the panel never closes it.
- **‹ 7/24 ›** (or `J` and `K`) moves to the next or previous item in the list
  or filter you opened it from, without closing.
- What you type in the message box is kept per item. Close the panel or move
  to another item, and it's still there when you come back.
- The move that's yours (for example **Request reviewer ▾**, **Fix**, or the
  agent's choices) is pinned just above the message box, so it's always in
  reach.

The peek panel has two tabs, and remembers the last one you used. An issue whose fix is on a branch also gets **Changes** (see [Reviewing a branch before there's a PR](#reviewing-a-branch-before-theres-a-pr)).

- **Summary:** the move, quick commands, the plain-words explanation, and the
  item's thread. Type a question and the agent answers there, without starting a
  new session.
- **GitHub:** the PR or issue exactly as GitHub shows it, read-only. The header
  has the state, author, `head` → `base`, labels, assignees, requested reviewers
  and linked issues or PRs. Then the description, every check with a **Details**
  link to its log, and the full timeline, oldest first: comments, reviews with
  their inline threads (file and line, the diff lines, replies, Resolved and
  Outdated), commits, labels, review requests, force-pushes and merge-queue
  events. Links open in a new tab.

The message box stays at the bottom on both tabs. Its **⋯** menu has:

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

The ↗ icon on every row and in the panel's header opens the PR or issue on
GitHub in a new tab in the app.

### Explain like I'm 5

The top of every peek panel and every PR's Story tab has two plain-language
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

You can ask something, close the panel, and keep going. Until the answer
arrives, the row shows **Replying…**. When it arrives, the row gets a blue dot
and a **New reply** chip:

```text
● Fix stale DevTools comment cards after resolving from Comments  [New reply]
  Clarify inherited FontSize reporting in DevTools  [Replying…]
```

The **Unread 2** chip at the top shows only those rows, and **Mark all read**
clears them. A new draft or structured review also counts as a reply. Opening
the item, or the PR's Story, Review or Thread tab, marks it read and
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

## Status chips and sorting

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
| Waiting on the author | You commented or asked for changes; the author hasn't pushed or replied since. A comment-only review also shows *Your comment doesn't count as approval* | 5 days |
| Author responded, needs re-review | You (or someone) commented or requested changes, then the author pushed or replied. Moves to **Your move** | 3 days |
| Changes requested, no response | No push or reply from the author since the change request | 5 days |
| Draft, author still working | Draft PR; timed from the last push | 14 days |
| Author still preparing it | The author's agent is still working (`agent-preparing`) | 3 days |
| Waiting on a reviewer | Your PR; nobody has reviewed it yet | 3 days |
| Approval doesn't count | Someone approved (maybe you), but GitHub still needs another approval | 1 day |
| Approved, ready to merge | Someone else's PR is approved and green; the author merges it (**Ship** still works) | none |
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

## Getting your PR ready for human review

Your PRs (you wrote them, or a session here works on them as the author) get a
**Ready for human review?** checklist at the top of the peek panel's Summary and the
expanded Story tab, read live from GitHub and following `.github/skills/pr-lifecycle`.

**Get it ready & keep it ready** is one switch. It asks the agent to run
pr-lifecycle in the PR's session (or open one), including pr-review and fixing the
obvious findings. Then it keeps the PR ready: Agent merge addresses new reviews,
fixes CI and resolves conflicts, and for each new commit the board itself:

- requests a Copilot review once (through GitHub's Copilot code review API). It
  never does this for a PR that isn't being kept ready.
- once the commit has been stable for 5 minutes, asks the agent once to rerun
  pr-review on it.

It never merges or approves. The PR's row and header show a purple **👁 Watching**
badge while it's on. **Stop keeping it ready** is in the checklist and the **⋯**
menu.

```text
Ready for human review?  ●●●○○ 3/5 ready   Watching
⏳ Copilot review   Requested by the board 1m ago (for 34bcf4e)
⏳ pr-review        Not run on 34bcf4e yet
✅ Discussion       All 3 threads resolved or answered
✅ Required CI      build-and-package passed on daf3e2b                    Log ↗
✅ Up to date       No conflicts · 2 behind main
Also, these don't block it
✅ Description      Links #1283
🏷️ Lifecycle label  agent-preparing
👁 Keeping it ready: …                [Stop keeping it ready]  [Recheck]
For commit daf3e2b (reviews count from 34bcf4e, the latest commit that isn't a merge of main)
```

| Core gate | Passes when |
|-----------|-------------|
| Copilot review | Copilot reviewed the latest commit that isn't a merge of main, and none of its comments are unresolved. It shows *Not requested*, *Requested 4m ago (for 0298fe1)*, *Reviewed 0298fe1: clean*, or *Reviewed an older commit* |
| pr-review (only while keeping it ready) | pr-review ran on that commit, and every blocking finding has a fix or a decision |
| Discussion | Every review thread is resolved or answered, and nobody asked something after the last update |
| Required CI | Every required check from the branch rules passed on the PR's head. A missing check is ⏳, never a pass |
| Up to date | No merge conflicts. "GitHub is still checking" is ⏳. Shows how far behind main it is |

Reviews count for the latest commit that isn't only a merge of main, so pressing
"Update branch" doesn't make Copilot or pr-review run again.

The extras never block "ready":

- **pr-review**, when the PR isn't being kept ready, is *Recommended* with **Run
  pr-review**.
- Blocking findings (critical, high or medium, not a nit or question) with no fix
  or decision get **Fix** (the session fixes it) and **Not needed** (records that),
  in the checklist and on the Review tab.
- **Description** warns only when there's an issue the PR plausibly fixes but
  doesn't link, with **Fix** to add `Fixes #n`.
- **Lifecycle label** is for information, with a grey "out of date" note when it
  disagrees with the core gates. The board never changes labels.

Rows show the core meter (`3/5 ready` while keeping it ready, `3/4` otherwise),
with ⚠ when an extra needs attention.

Whose move it is:

| Situation | Row says |
|-----------|----------|
| In the merge queue | *In merge queue #2* (waiting on the queue) |
| Approved, all core gates green | *Approved: merge it* (your move, **Merge…**) |
| Kept ready, and new feedback, failing CI or conflicts arrive | *New feedback: agent addressing it* (spinner) |
| Labeled `agent-blocked` | *Agent blocked: …* (your move) |
| Blocking findings, and the agent is preparing it or keeping it ready | *Addressing N pr-review findings* (agent) |
| Blocking findings, and nobody is working on it | *Decide on N pr-review findings* (your move, **Decide…**) |
| Not all green yet, labeled `agent-preparing`, or a reviewer step is waiting | *Preparing it for review · 3/5 ready · Then: request a reviewer* (agent) |
| All green, no reviewer requested | *Ready for human review: request a reviewer* (your move, **Request reviewer ▾**) |

A real decision for you, such as a choice that isn't about reviewers, still shows
as your move. A row never shows *Needs your decision* next to a spinner; if the
agent is also working on it, the row says so in grey.

## Reviewing a branch before there's a PR

When a session here works on an issue, its fix sits on a branch before anyone
opens a PR. You can review that branch privately on the board, the way you'd
review a PR, without anything showing up on GitHub.

The issue's panel, peeked or expanded, gets a **Changes**
tab. It shows the branch against `main`: how far ahead and behind it is, a
**Compare on GitHub** link, the commits, and every file's diff.

```text
nmetulev-devtools-inspector-pane-reuse → main · 3 commits ahead, 2 behind
[Send 2 comments to the agent] [Open PR ▾]

M scripts/test-e2e-winui-ui.ps1                       💬1  +33 −0
 880 +  # Each selection updates the details pane in place (#1283)...
 881 +  # selection, so the pane must be the same element afterwards
 ┌ Not sent · test-e2e-winui-ui.ps1:881–883 ─────────────────────┐
 │ Is this check still needed once the pool is shared?            │
 └────────────────────────────────────────────────────────────────┘
```

1. **Comment:** click a line number, or drag over several, and write a comment
   (Markdown; Ctrl+Enter adds it). **Add an overall note** comments on the whole
   branch. Comments stay on the board.
2. **Send N comments to the agent:** passes every unsent comment, with its file,
   lines and code, to the linked session. The session fixes them and pushes.
3. **Re-review:** when the branch moves, the item comes back to **Your move** as
   *Re-review the changes*, and a banner says how many commits landed. Each
   comment says whether its file **Changed since** or is **Not changed yet**.
   A comment whose code is gone is **Outdated**. **Resolve** a comment once it's
   handled, and press **Mark reviewed** when you've looked at the new commits.
4. **Open PR ▾:** asks the session to open it as a **Draft PR** or **Ready for
   review**. Once the PR exists, the issue shows it, and you review it as a PR.

## The workspace

The expanded sheet holds everything about one item. It opens on the tab that
matters for whose move it is: **Review** when there are findings to decide or a
review to post, **Thread** while an agent works, and **Story** otherwise.

**Story** reads top to bottom in five steps:

1. **What this is:** the plain-words explanation.
2. **What changed:** what the PR does, with links to the diagram and changes.
3. **What we found:** the review's verdict and risk.
4. **Where it stands:** the readiness checklist, or who it's waiting on.
5. **What's needed from you:** the current move.

Everything else (next steps, status, the PR description) is under **More**.

Buttons: the one that fits the stage is blue (Review, Post review…, Ship, or
Answer). **Summarize** is next to it, and **More** holds Open full chat, Pin to
top, Open on GitHub, and Reset agent status. Tabs:

| Tab | Shows |
|-----|-------|
| Story | The five steps above, then **More**: next steps, status (who's next, CI, reviews, merge queue, agent, session, size, labels), and the PR description. GitHub events are under **Timeline** |
| Architecture | Diagrams of what the PR touches. Green is new, orange is changed, red dashed is removed. A number on a box counts its findings. Click a box to see its files, its findings, and **Ask about this** |
| Changes | By system: each area the PR touches, with one plain line and +/− counts. By file: one plain line per file. Without a review it lists the files with +/− counts |
| Review | The findings, most severe first, each with what's wrong, the suggested fix, and the file and lines. Mark each one **Post**, **Ignore** or **Undecided**, and add a note. **Explain** asks the agent to explain the finding in plain words; the answer appears under it |
| GitHub | The PR as GitHub shows it, the same view as the peek panel's **GitHub** tab |
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

### The agent can point at things

A reply can link to a spot in the workspace. The agent writes an anchor in its
`pr_board_reply` text, and the board shows it as a chip. Clicking the chip
opens the right tab, scrolls to the spot, and highlights it.

| Anchor | Goes to |
|--------|---------|
| `[[finding:f1]]` | Finding f1 on the Review tab |
| `[[file:src/a.cs#L12-L20]]` | Those lines on the Changes tab |
| `[[node:diagramId/nodeId]]` | A box on the Architecture tab |
| `[[gate:ci]]` | A readiness check (`copilot`, `review`, `discussion`, `ci`, `uptodate`, `description`, `label`) |
| `[[thread:r4622965814]]` | A GitHub review thread on the GitHub tab |

The chip shows the item's name, for example "HandleRpc" for a finding. A reply
can also pass `focus` (one anchor) to jump there as soon as it arrives, while
the item is open.

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
| `pr_board_reply` | Post a reply in a PR's thread (Markdown, with optional [anchors](#the-agent-can-point-at-things) and `focus`) |
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
PR's files when you select it, and the GitHub tab when you open it (cached for
a few minutes, because GitHub's image links expire).
If `gh` is missing or signed out, the board says so and tells you what to run.

What the board adds (summary, structured review, your Post/Ignore choices and
notes, private branch comments, linked session, agent status, timeline, thread) is saved per PR in one file per repo:

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
