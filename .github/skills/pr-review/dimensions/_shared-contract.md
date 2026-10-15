# Shared output contract

## Output

Sub-agent output is internal synthesis input. Keep severity, confidence,
validation, and domain metadata here, but the orchestrator must not copy that
bookkeeping into the user-visible report unless it changes the action or
confidence.

Start with one line: `# <dimension>: <N> findings`.

Then one block per finding:

```markdown
## <plain finding title>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Validation**: static-only (needs runtime confirmation)
- **Domain**: <dimension>
- **Location**: <repo-relative path and post-change lines>
- **What is wrong**: <the defect, in plain language>
- **Show me**: <smallest command/input/code path; prefer input -> actual -> expected>
- **Why it matters**: <concrete user, build, security, or maintenance consequence>
- **Smallest fix**: <least-complex change that resolves it>
```

Always emit `Validation: static-only`; the orchestrator promotes it only after a
real reproduction. Define unavoidable jargon on first use. If you cannot provide
a concrete `Show me`, lower confidence or drop the finding.

End with a `## What I checked` section, one bullet per area inspected. This is
internal evidence for consolidation, not a user-visible coverage table.

## The bar

Before emitting anything, ask:

> Could a junior developer with no prior conversation understand what fails, see
> it happen, and know the smallest repair after one read?

> Would a busy maintainer, looking at a PR that is otherwise ready to ship,
> genuinely want this changed — or is this merely a true statement about the
> code?

Emit it only if you can finish the sentence **"a user doing X will hit Y."**

Drop it if:

- The code works and you are describing a tidier alternative.
- The fix adds more complexity than the problem costs users.
- It guards against something that cannot happen here — input the CLI controls,
  a state the caller guarantees, a config the repo does not support.
- It is a "for completeness" / "for consistency" item with no user-visible
  effect.
- The compiler, the analyzers, or `EnforceCodeStyleInBuild` already catch it.
  Style, naming, and formatting are never findings.

**Never drop** an in-scope security issue (see the threat model in
`security.md`), data loss, a crash, wrong output, or a broken install. This bar removes polish and speculation, not defects.

**There is no quota, and zero findings is a good result.** Two precise findings
beat eight thorough ones. Never invent one to avoid an empty report.

## Compatibility gate

The baseline is the latest supported published release, never an earlier commit,
review round, current PR implementation, or unreleased release work. Before
calling something a compatibility break or recommending an alias, fallback,
migration path, legacy branch, or compatibility abstraction, identify:

1. The supported published version containing the behavior.
2. The public contract or persisted user data involved.
3. A real external consumer that would break.

If any is missing, it is not a compatibility finding: prefer a clean replacement
of the unreleased behavior. A publicly supported preview contract is the only
exception.

## Recommendations

Write the **smallest** fix that resolves the finding, not the most thorough.

Try the subtractive fix first: delete the branch, drop the option, reject the
input, collapse the second code path, or document the limitation. If your fix
adds a new command / flag / service / abstraction, say in one clause why a
smaller one will not do — if you cannot, propose the smaller one instead. Never
recommend speculative generality.

## Severity

| | |
|---|---|
| critical | Breaks users, corrupts data, leaks secrets, or blocks release |
| high | Real bug, in-scope security issue, real UX defect, or a new public command with no tests |
| medium | Worth fixing, not a blocker |
| low | Only if the improvement is concrete and actionable |

Confidence is **high** when cause and effect are both visible in the diff,
**medium** when one half is inferred from repo context you read, **low** when the
pattern matches a known issue but key elements are unverifiable. In-scope
security findings are never suppressed for low confidence.

## Re-reviews

If told this branch was already reviewed: emit critical and high only, and treat
code added in response to earlier review comments as **re-openable — not settled
design**.
