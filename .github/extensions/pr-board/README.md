# PR board (prototype)

A canvas that shows every open pull request in a GitHub repo as a card, sorted
into lanes by what happens next. Each card says who the PR is waiting on, and
has buttons that hand the next step to a Copilot agent: draft a review, post it,
or ship it.

Open it from a chat in a winappCli session:

```text
Open the PR board canvas
```

The board uses the current repo. To pick another one, pass `{ "repo": "owner/name" }`.

## What you see

```text
Needs review │ Agent reviewing │ Draft ready │ Waiting on author / others │ Approved │ In merge queue │ Merged recently
```

| Lane | A PR is here when |
|------|-------------------|
| Needs review | Nobody else is driving it and you haven't reviewed it yet |
| Agent reviewing | An agent session is drafting a review, or is waiting for your input |
| Draft ready | A draft review is attached and hasn't been posted yet |
| Waiting on author / others | It's a draft PR, has changes requested, has conflicts, its author's agent is still preparing it (`agent-preparing` / `agent-blocked` labels), or it needs someone else's approval |
| Approved | It's approved; CI is still running, or it's ready to ship |
| In merge queue | It's in GitHub's merge queue; the card shows its position |
| Merged recently | Merged in the last 3 days |

Each card shows the PR number and title, author, size (XS–XL, from lines
changed), CI state, human reviewers and their verdicts, the merge-queue
position, the linked agent session, and the agent's latest one-line outcome,
for example `Draft ready: Recommends merge; 1 optional nit`.

The **⏭ line** says who's next, in plain words: `You: review needed`,
`zateutsch: address changes requested by nmetulev`, `CI: 9 checks still running`.
The card's left edge uses the same color: orange is you, purple is an agent,
blue is CI or GitHub, gray is the author or other reviewers.

Click a card's title to see its labels, failing checks, the draft review text,
and its history on the board.

## Buttons

Every button sends a request to the agent in the session that opened the board.
Nothing is posted, approved, or merged unless you click the button for it.

| Button | What the agent does |
|--------|---------------------|
| **Review** | Opens or reuses a session for the PR (`open_pr_session`, `send_session_message`, or `create_session`, whichever it has; otherwise it reviews inline), runs `.github/skills/pr-review`, and saves the review as a file. Nothing is posted. |
| **Post review ▾** | Enabled once a draft is attached. Pick Approve, Request changes, or Comment, then click again to confirm. The agent runs `gh pr review --body-file <draft>` with that exact file. |
| **Ship** | Enabled when the PR is approved and checks aren't failing. Click again to confirm. The agent follows `.github/skills/pr-lifecycle` and runs `gh pr merge --squash` (never `--admin`). If the PR isn't ready, it says what's blocking it instead. |
| **Open chat** | A `ghapp://sessions/<id>` link to the linked session. **↪** next to it asks the agent to call `navigate_to` instead. |
| **Refresh** | Re-reads GitHub. The board also refreshes when it opens and every 2 minutes. |

## Agent tools

The agent calls these to keep cards current. Every request from the board
tells it which ones to call.

| Tool | Use |
|------|-----|
| `pr_board_link_session` | Link a session to a PR card |
| `pr_board_set_status` | Set the card's agent status: `running`, `waiting`, `posted`, `done`, `failed`, or `clear`, with a one-line outcome |
| `pr_board_attach_draft` | Attach the draft review file and its recommendation; moves the card to Draft ready |
| `pr_board_get_state` | Read the board, or one card |

The canvas also has `refresh` and `get_board` actions for `invoke_canvas_action`.

## Where state lives

PR data comes from GitHub on every refresh, using the `gh` CLI. If `gh` is
missing or signed out, the board says so and tells you what to run.

What the board adds (linked session, agent status, draft path, history) is
saved per PR in one file per repo:

```text
%USERPROFILE%\.copilot\extensions\pr-board\artifacts\microsoft__winappCli.json
```

(`$COPILOT_HOME` replaces `~/.copilot` when it's set.) Every session and every
open board on your machine reads the same file, so a status set in one session
shows up on the board in another, and it survives restarts. It's per user and
isn't committed. Delete the file to reset the board.

## Files

| File | Contents |
|------|----------|
| `extension.mjs` | Canvas, agent tools, loopback server, button handling |
| `github.mjs` | `gh` calls and lane rules |
| `prompts.mjs` | The requests sent to the agent for each button |
| `store.mjs` | The per-repo state file |
| `ui.html` | The board page (no build step, no dependencies) |
