---
name: pr-lifecycle
description: Prepare, present, and finish a contributor PR in microsoft/winappcli. Activate for "create a PR", "prepare this PR", "get this PR ready for review", or "take this PR through readiness". Keeps fixes in the existing PR, communicates evidence, and tracks current-head readiness separately from human approval. A body-only draft or review-only request does not authorize edits, posting, labels, or merge. Never merges or enables auto-merge.
infer: true
---

# PR lifecycle: Prepare, Present, Finish

Use this for work on this repository, not as a shipped `winapp` plugin skill.
The outcome is a clear PR and an honest readiness state, not a merge.

## Authority and repository policy

- Establish the requested scope first. "Draft a PR body" returns text only;
  "review this PR" returns findings only. Neither authorizes changing code,
  pushing, posting, requesting reviews, or changing labels. A request to create
  or take a PR through readiness authorizes the necessary in-scope work, not
  unrelated changes or merging. Confirm any additional authority you need.
- Read [AGENTS.md](../../../AGENTS.md), the
  [PR template](../../PULL_REQUEST_TEMPLATE.md), affected workflow definitions in
  `.github\workflows`, and applicable `.pipelines` instructions. Live branch
  protection/rulesets are the source of truth for required checks and approvals;
  do not freeze their names or counts here.
- [pr-review](../pr-review/SKILL.md) owns independent, practical review of
  correctness, compatibility and performance. Use it for a full feature review;
  keep a narrow docs/fix review proportional rather than copying its entire
  orchestration. [spec-review](../spec-review/SKILL.md) is for unresolved design
  questions before implementation, not another mandatory post-code ceremony.
- Fix findings **in the existing PR** by default. Create a follow-up fix PR only
  when the user asks. Preserve the review-only default of `pr-review`.
- Use the host's `create_pull_request`, `update_pull_request`, and
  `reply_and_resolve_review_thread` tools when available; obey their contracts
  rather than substituting shell commands. Other GitHub reads/actions may use
  `gh`. Never merge, enable auto-merge, bypass protection, dismiss a human
  changes-request review, or manufacture an approval.

## Prepare

1. **Identify the real comparison.** Read the live PR (if it exists): repository,
   number, state, head repository/ref/SHA, base ref/SHA, draft state, checks,
   reviews and mergeability. Inspect local status, tracking refs and the full
   diff including relevant untracked files. Fetch the actual head and base;
   do not assume the local branch or `origin/main` is current. Record immutable
   head/base SHAs for the assessment.
2. **Preserve other work.** Do not stage unrelated files, delete scratch files,
   discard a dirty tree, or overwrite collaborator commits. If the remote head
   differs from your expected head, inspect it before continuing; stop and
   coordinate overlapping edits. Prefer a normal non-rewriting update. Rewrite
   only with permission and an explicit expected-SHA lease
   (`--force-with-lease=<ref>:<expected-sha>`); a failed lease means re-inspect,
   not force harder.
3. **Respect stacks.** Review against the actual parent branch, not the entire
   stack against main. Use the available native stack skill/tool to synchronize
   parents and propagate updates. Check the parent and ultimate main as
   appropriate to repository policy; do not blindly retarget to main or merge
   main into a child. For an ordinary PR, incorporate required base updates
   safely and resolve conflicts before assessing readiness.
4. **Review and repair.** Get independent review of the actual final diff.
   Review concrete user paths, supported released compatibility contracts and
   meaningful performance risks, not speculative edge cases. Fix actionable
   defects with regression tests where applicable. Explain false positives with
   evidence; do not silently mark them addressed. A prior review remains useful
   only if its SHA is recorded and every subsequent delta is reviewed, or
   range-equivalence is demonstrated. A rebase alone is not proof of equivalence.
5. **Validate the changed behavior.** Run the smallest relevant existing tests,
   builds and user-path checks required by repository instructions. Update only
   affected documentation. Record exact commands and observed outcomes,
   including limitations. For contributor Markdown-only changes, check
   frontmatter, links/paths and Markdown plus applicable docs tooling; no full
   NativeAOT build is needed unless that tooling requires it.

Do not call a local test pass a CI pass, or call your own inspection independent
review. An initial independent assessment needed to evaluate the work is part of
preparation. Do not invent edits to elicit a favorable review; distinguish that
assessment from waiting for re-review of feedback already addressed.

## Present

Use a short, outcome-oriented title and the repository PR template. Keep its
applicable headings; mark non-applicable sections honestly. The body describes
the **final feature**, not each round of agent activity:

- Explain the plain-language problem and what users can now do.
- Show the smallest concrete before/after command, input or interaction and
  actual result. Distinguish observed output from an illustrative example.
- Include focused validation evidence, honest limitations, and only real
  compatibility, dependency or performance tradeoffs. For compatibility, use
  the published-contract test in `AGENTS.md`.
- For visual changes, capture actual before/after screenshots. For interactions,
  use a short recording showing the relevant behavior. For nonvisual changes,
  use textual CLI/API examples; documentation-only work needs no screenshots.
  Performance claims need benchmark conditions (build, hardware, workload,
  cache state and baseline) and measured results.
- Never fabricate/decorate evidence or include secrets, personal data or other
  sensitive content. Use `github-pr-media` for uploads when available and follow
  its permissions. If evidence cannot be captured, state the limitation.
- Omit file inventories, agent transcripts, internal finding IDs, review-round
  logs and model coverage. Reviewers should not need the agent conversation.

Before editing an existing body, read its live content. Preserve human edits and
use the update tool's optimistic guard (`base_sha` when supplied). On a stale
edit, re-read and reconcile instead of retrying an old full-body replacement.
If the host has no guard, re-read immediately before a minimal update and stop
on conflicting edits.

Use the lifecycle label as the PR's readiness signal. Do not post or maintain
automated lifecycle status comments or round-by-round progress comments.
Necessary replies to review feedback are still required; they are not status
updates.

Keep operational details in a durable session checkpoint: PR URL/number,
head/base SHAs (and main/parent when relevant), review coverage, pending gates,
blocker/owner/next step, and the configured wakeup or resume action. Do not commit
these checkpoints or copy their history into the feature body. Ask the author
directly when their input is needed.

## Finish

### Read the complete live discussion

Read **all pages** of inline review threads, including replies, and all
top-level review bodies and issue comments. Inspect the current PR body for
actionable requests too. Do not infer "no findings" from a summary, the last
review, unresolved-thread count alone, or the first 100 results.

Reply to each actionable thread with the fix/evidence or a reasoned disagreement
**before** resolving it. Use `reply_and_resolve_review_thread` when available.
For actionable findings in a review body or top-level comment, answer in the
appropriate discussion surface and record the disposition in the session
checkpoint. Keep substantive disagreement and its supporting evidence explicit
in that reply until reassessed; do not present an unaddressed defect as a resolved
disagreement.

Never dismiss a human's changes request yourself. After addressing it, re-request
that reviewer and record the pending re-review and owner in the session checkpoint.
Waiting for that reassessment is not an agent blocker: if the agent has addressed
the feedback and completed the other technical gates, the PR is ready for review
even while GitHub still shows the earlier `CHANGES_REQUESTED` review.
Do not repeatedly request Copilot reviews merely to obtain favorable wording.
Distinguish:

| Evidence | Meaning |
|----------|---------|
| Current-head Copilot review with zero actionable findings | Technical review evidence, not a human approval |
| Formal `APPROVED` review | An approval event; still check author, SHA and repository rules |
| Review or re-review pending after feedback is addressed | Use `ready-for-review` once the other technical gates pass; approval remains pending |
| Unaddressed defect or substantive concern | Keep preparing if the agent can resolve it; block only when the agent cannot proceed without help or author input |

The absence of the exact words "Approval recommended" is not a defect. Do not
make fake changes, rubber-stamp, or argue an automated reviewer into approval.

### Verify checks and integration

Required CI must be completed and green for the **latest head** and the
integration/merge commit where applicable, not an older successful run. Inspect
live required-check policy as well as actual runs: missing required checks are
pending, never a pass. Accept neutral/skipped only where expected under
repository policy. Do not treat an inaccessible policy or unknown mergeability
as verified readiness.

For failure, inspect the failing job and logs first. Retry only a bounded number
of times when evidence shows a transient infrastructure failure; record why.
Fix deterministic failures. Never weaken assertions, bypass checks, or dismiss
failures as "environmental" without investigation and a concrete resolution.

Use bounded waits/backoff rather than a tight polling loop (for example, inspect
after 30, 60 and 120 seconds, then checkpoint). Prefer notifications or an
available supported wakeup mechanism. This skill **cannot wake an idle agent**.
If no mechanism is configured, say "Not actively monitoring" and name the pending
gate and resume action. Do not claim to be watching indefinitely.
Marking a PR ready for review does not end follow-through on a requested
re-review: keep configured notifications or wakeups active while that feedback
is pending, and address any new actionable findings.

### Set exactly one lifecycle label

For an open PR managed by this workflow, the following labels are mutually
exclusive. Preserve unrelated labels.

| Label | Apply when |
|-------|------------|
| `agent-preparing` | Agent work remains: addressing feedback, completing validation/CI, or obtaining the initial independent assessment |
| `agent-blocked` | The agent cannot finish addressing feedback or fixing CI with the available access, tools or information, or needs author input; name the concrete blocker, owner and next step |
| `ready-for-review` | Agent work and technical checks are complete for the current head/base; waiting for review or re-review is normal, **not** human approval or merge permission |

Choose `agent-blocked` based on the agent's inability to proceed, not a review
status. Fixable findings, fixable CI failures and ordinary CI waits mean
`agent-preparing`. After feedback has a fix or an evidence-backed disposition,
waiting for reviewer acceptance or reassessment means `ready-for-review` if the
other technical gates pass. Keep pending reviews and merge-blocking approval
requirements in the session checkpoint; do not dismiss them or claim approval.

Before **each label transition**, fetch a live snapshot. Before ready, confirm:
independent final-diff coverage and a fix or evidence-backed disposition for each
actionable finding, required CI completed successfully, required base/stack
integration current, and no merge conflicts. Re-fetch head/base (and relevant
parent/main), discussion and checks after assessment. If any changed, reassess
rather than applying a stale result. Unknown conflict state means not ready.

Replace the other lifecycle labels with the selected one. When adopting this
PR, also remove legacy `pr-review-done` if present; it is not a readiness signal
for this workflow. Create missing labels only with authorization. Do not relabel
unrelated PRs or redefine/delete their labels. If permissions prevent a change,
report the intended state and failed action; never claim a label was applied.
Re-read labels and head/base after writing; if a race invalidated readiness,
remove the ready label and reassess.

A new commit, actionable finding or conflict invalidates readiness; an unrelated
comment does not. Base/main movement requires appropriate integration and
revalidation, not a promise the PR stays permanently clean. When next invoked on
a merged/closed PR, remove active lifecycle labels and stop; do not reopen it.

### Useful read-only GitHub commands

Set `$repo` and `$number` from the actual PR, not from an unrelated checkout.
These reads supplement, not replace, the complete discussion and policy checks:

```powershell
gh pr view $number --repo $repo --json url,state,headRefOid,headRefName,headRepository,baseRefName,baseRefOid,isDraft,mergeable,mergeStateStatus,reviewDecision,body,labels,statusCheckRollup
gh pr checks $number --repo $repo --required
gh api --paginate "repos/$repo/pulls/$number/reviews"
gh api --paginate "repos/$repo/issues/$number/comments"
gh api --paginate "repos/$repo/pulls/$number/comments"
# URL-encode the actual base ref as a single path segment for this endpoint.
gh api "repos/$repo/rules/branches/$([Uri]::EscapeDataString($baseRef))"
# For a failing GitHub Actions run; use its provider's logs for external CI.
gh run view $runId --repo $repo --log-failed
```

REST pull comments do not expose thread resolution state. Use a GraphQL
`pullRequest.reviewThreads(first:100, after:...)` query with `pageInfo` and
`comments(first:100, after:...)`, fetching every thread page **and every nested
comment page**. Capture each thread `id` and comment `databaseId` for the reply
tool. If GraphQL/SSO access is unavailable, report the missing thread assessment
as a gate; a REST-only partial read is not a clean review.

End with the PR link, actual state, and any remaining blocker/next action.
Separate "technically ready" from "approved" and "merged"; never imply either
of the latter from a lifecycle label.
