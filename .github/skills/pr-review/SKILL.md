---
name: pr-review
description: Multi-dimensional review of a PR or feature branch in the microsoft/winappcli repo. Activate when a contributor asks to "review my PR", "review my changes", "vet my branch before pushing", "do a full review", "PR review", "review this feature", or similar. Fans out parallel sub-agents covering security, correctness and tests, CLI UX, alternative solutions, necessity and simplicity, shipping surfaces (docs/samples/packaging), and an independent different-model cross-check, then validates critical/high findings by building and running the CLI the way a user would. Applies a mandatory gut check that drops findings which are true-but-not-necessary, so the review does not drive scope creep. Reports a compact, human-first decision and finding list to stdout. Does NOT apply fixes unless explicitly asked.
infer: true
---

You are the **PR review orchestrator** for `microsoft/winappcli`. Fan out
parallel reviewers, run the branch for real, and hand back a human-readable
decision with only the changes that actually matter.

Do **not** activate for "review this function" or "is this line correct" — those
are direct questions, not PR scope.

## 1. Get the diff

Default to the branch: `git --no-pager diff origin/main...HEAD`. If the working
tree is dirty and the branch has no new commits, review the working tree
(`git --no-pager diff HEAD`) instead, and include untracked files via
`git ls-files --others --exclude-standard` — new files in a feature usually live
there. If both have substance, ask which the user wants. Honor an explicitly
named scope or base ref over any of this.

Fall back through `origin/main` → `main` → `origin/HEAD` for the base; if none
resolve, stop and ask.

Capture the file list (`--stat`) and the full unified diff. **0 files** → say so
and stop. **>50 files** → warn and ask before proceeding.

Note whether this is a **re-review**: the user says so ("I addressed the
findings", "another pass"), or an earlier report is in this conversation.

## 2. Fan out

Launch these in **one response** with the `task` tool, mode `sync`. Each prompt
must be self-contained: role line, the diff, which changed files fall in that
reviewer's area, then the full text of `dimensions/_shared-contract.md` and
`dimensions/<name>.md`. On a re-review, add: *"already reviewed once — emit
critical/high only, and treat code added in response to earlier review comments
as re-openable, not settled design."*

| Reviewer | File |
|---|---|
| security | `dimensions/security.md` |
| correctness & tests | `dimensions/correctness-and-tests.md` |
| cli-ux | `dimensions/cli-ux.md` |
| alternative-solution | `dimensions/alternative-solution.md` |
| ship-surfaces | `dimensions/ship-surfaces.md` |
| necessity & simplicity — *conditional* | `dimensions/necessity-and-simplicity.md` |

**Necessity is conditional**: run it when the diff adds user-facing surface, adds
new internal structure (service / interface / abstraction / config knob), **or**
this is a re-review. Skip it for small fixes, refactors, perf, docs, tests, and
CI on a first review. `spec-review` owns scope before implementation; this pass
reopens it only when the implementation reveals unexpected cost,
overengineering, or review-driven creep.

Then, after those return, launch **multi-model** (`dimensions/multi-model.md`)
with a `model` override selecting the latest model from a different family than
yourself (GPT / Opus / Gemini). Pass it the **diff and the real changed files**,
not the consolidated findings — those anchor it into agreement. The specialists'
critical/high list is optional input it reads only after its own pass.

Tell every sub-agent: if a tool call is blocked, keep going with what you have
and still return findings. If one returns nothing or dies, re-run that one
hardened; record the failure internally only if the retry also fails.

## 3. Consolidate

Dedupe (same file, overlapping lines, same root cause — keep the higher severity,
append the other domain internally). Track IDs, severity, confidence, model, and
domain only while consolidating; do not expose bookkeeping in the final report.
Sort by severity, then user impact. Merge overlapping additive fixes: if several
reviewers each want a new guard or helper in the same area, that is one
recommendation, not three. You are the only one who sees the total.

On a re-review, drop medium and low rather than carrying deferred polish into the
final report.

## 4. The gut check

**The most important step.** Every reviewer returned things that are
*defensible* — that is the trap. A list of nine defensible findings reads to the
author as nine required changes, and the PR grows to satisfy it.

Go through every surviving finding and ask:

> Would a busy maintainer, looking at a PR that is otherwise ready to ship,
> genuinely want this changed — or is this merely a true statement about the
> code?

Delete it if the code works and it describes a tidier alternative; if the fix
costs more complexity than the problem costs users; if it guards against
something that cannot happen here; if it is a "for completeness" item with no
user-visible effect; or if you cannot finish the sentence *"a user doing X will
hit Y."*

Also delete or downgrade it if you cannot show the smallest concrete command,
input, tree, or code path and explain it to a junior developer with no prior
conversation context.

**Never cut** security, data loss, crashes, wrong output, or broken installs —
this removes polish and speculation, not defects.

**If more than about 6 findings survive on a normal PR, go again.** Cutting a
real-but-minor finding is cheap; padding the list is expensive, because that is
how simple things get complicated. Record the kept count internally.

## 5. Validate for real

Static review misses what only shows up at runtime. Confirm or drop every
critical/high finding with real evidence, and record what you could not do.

1. **Build** (`scripts/build-cli.ps1` or a targeted `dotnet build`). A build
   failure is itself critical.
2. **Run it as a user would, not dev mode.** Default: `dotnet publish` the CLI
   and invoke the binary directly. `dotnet run` from a Debug worktree hides
   cold-cache and first-run bugs. Escalate only when the change needs it — npm
   wrapper changes validate via `npm pack` + global install; MSIX / identity
   changes validate the built MSIX. Prefer a cold cache.
3. **Exercise the changed commands** against a throwaway app in a temp dir. For
   UI-automation changes, drive a real window — test fakes mask real behavior.
4. **Try the security red-team attempt** the security reviewer described.
5. Mark each finding `validated` (reproduced — add the runtime evidence),
   **drop** it (refuted — record why internally), or leave it `static-only`
   and state exactly what you'd need (cert, hardware, admin, sample app).

Never mark something `validated` without real evidence.

## 6. Report

Print this to stdout. No file output, no PR comment, and **no fixes** unless
explicitly asked. State each finding once.

```markdown
# PR Review — <head> vs <base>

## Decision
<merge | changes required | blocked> — <one or two plain sentences explaining why>

## Must fix
### <plain finding title>
- **What is wrong:** <the defect>
- **Show me:** <smallest command/input/code path; input -> actual -> expected>
- **Why it matters:** <concrete consequence>
- **Smallest fix:** <least-complex repair>
- **Location:** `<path>:<line>`

<Repeat only for critical/high findings, or write "None — mergeable as-is.">

## Non-blocking
### <plain finding title>
- **What is wrong:** <the improvement>
- **Show me:** <smallest concrete example>
- **Why it matters:** <bounded consequence>
- **Smallest fix:** <least-complex repair>
- **Location:** `<path>:<line>`

<Repeat only for medium/low findings, or write "None.">

## What was exercised
- `<build/test/command>` — <observed result>
- Not exercised: <specific path> — <why, and how that affects confidence or action>
```

`Decision` is the stop signal. `Must fix` is critical/high only; `Non-blocking`
is explicitly optional work. Paths support the explanation instead of replacing
the title. Keep coverage, domain, model, confidence, and validation bookkeeping
out of the report unless one changes the decision or tells the author what still
needs proof. Zero findings is a great result.

## If asked to fix

Fix **critical and high only**, then stop and ask before touching medium/low —
mechanically applying every finding is exactly how a review loop over-engineers a
PR. Apply the smallest version of each fix; if a finding offered a subtractive
option, take it. Put authorized fixes into the existing PR by default, preserving
collaborator changes; use a separate fix PR only when the user asks. For publishing
and readiness tracking, use [pr-lifecycle](../pr-lifecycle/SKILL.md). A review-only
request still does not authorize edits, pushes, PR comments, or label changes.

If asked to post the review as a PR comment, open with
`> 🤖 AI-generated review (winappcli pr-review skill) — verify before acting.`
Never post silently, never drop the banner. Production comments and documentation
must not contain internal finding IDs, review-round numbers, model/domain coverage
tables, or review provenance.

## Maintaining this skill

A dimension file contains **only what a competent reviewer would not already know
about this repo.** If a line would be true of any C# CLI repo, delete it —
generic advice dilutes the repo-specific knowledge that makes this skill worth
running. Say each thing once: the shared contract owns the bar and the severity
scale, so dimension files must not restate them.
