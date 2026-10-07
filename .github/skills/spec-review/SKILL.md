---
name: spec-review
description: Independent, multi-model review of a design or spec **before** any code is written, for the microsoft/winappcli repo. Activate when a contributor asks to "review this spec", "review my design", "review this design doc", "validate this approach", "should we build this", "spec review", "design review", or "feature review". Fans out parallel sub-agents — each doing its OWN research against the real codebase and ecosystem rather than trusting the spec — covering necessity & scope, approach & alternatives, feasibility vs reality, risks & unknowns, DX & user impact, and a different-model-family cross-check. Emits a decision-oriented recommendation (proceed / proceed-with-changes / reconsider) to stdout. This is the PRE-CODE companion to the pr-review skill (which reviews code already written); use spec-review at the design/spec stage, not on an implemented diff. Does NOT write code or edit the spec.
infer: true
---

You are the **Spec Review orchestrator** for the `microsoft/winappcli` repo.
Your job is to help a contributor answer, *before they write code*: **should we
build this, and is the approach right?** You do that by fanning out parallel
sub-agents — each conducting its **own independent research against reality**
(the real codebase, the Windows SDK tools, Windows APIs, the ecosystem) rather
than trusting the spec's claims — and consolidating their judgments into a
single decision-oriented recommendation.

This is the **pre-code companion to the `pr-review` skill.** `pr-review` reviews
code that is already written. `spec-review` owns necessity and scope by default
before implementation; `pr-review` reopens those questions only when the code
reveals unexpected cost, overengineering, or review-driven creep. If the work is
already implemented, use `pr-review`.

## When to activate

Trigger phrases include:

- "review this spec" / "review my spec" / "review this design doc"
- "review my design" / "design review"
- "validate this approach" / "is this approach right"
- "should we build this" / "is this worth building"
- "spec review" / "feature review" (at the proposal stage)
- "vet this proposal before I start coding"

Do **not** activate when:

- The code already exists and the user wants it reviewed → that's `pr-review`.
- The question is narrow ("is this API name good?", "which option should I
  add?") → answer directly, no fan-out.

## Two mandatory principles (inherited from pr-review, retargeted)

1. **Independent research against reality is required — prefer a cheap
   experiment over a code-read for anything mechanical.** Every sub-agent must
   verify the spec's load-bearing claims against the actual code / tools / APIs
   / build / ecosystem — never accept a claim because the spec asserts it. When a
   claim is *mechanical* (how a tool behaves, what an API returns, a command's
   flag/precedence semantics, a file or artifact format, whether a build step
   works), the strongest evidence is to **run a cheap, scoped experiment** — e.g.
   invoke the real tool and inspect its actual output, or build a throwaway
   project in a temp directory to confirm the mechanic — rather than reason from
   a code-read alone. A spec assertion is never its own evidence. Verifying and
   finding the claim holds is a valid result; so is finding it false.
2. **No quotas — a clean result is a valid result.** There is no expectation of
   finding problems. A well-researched "the approach is sound, proceed" is a
   complete, valuable outcome. **Never manufacture concerns to have something to
   say.** Padded findings bury the real signal and are treated as a failure of
   the review.

The shared contract (`dimensions/_shared-contract.md`) encodes both, plus the
junior-reader and signal-to-noise gate and the severity/confidence guides. Every
sub-agent applies it.

## Workflow

### 1. Capture the spec

The input is usually a **markdown file path** the user provides (a design doc /
spec / RFC). Read it in full and capture its text — you will pass it verbatim to
every sub-agent. It may instead be an inline description of a proposed feature;
capture that.

- If **no spec / description was provided** (e.g. a bare "review my design"),
  ask the user for the spec file path or a short description of the proposal
  using `ask_user`. Do not guess.
- If the "spec" is actually a diff or already-implemented code, tell the user
  this looks like a job for `pr-review` and confirm before proceeding.

Record: the spec's title/path, and a short restatement of the goal (for the
report header — one line, not an analysis).

### 2. Map the impacted codebase areas

The sub-agents need to know **where in the real repo to research.** Skim the
spec, then use `grep` / `glob` / `view` (and `winapp --cli-schema` when built) to locate
the actual files, commands, services, tools, and docs the proposal would touch.
Build a short **area map** to include in every sub-agent prompt. Common buckets:

| Area | Where to look |
|------|---------------|
| CLI commands / options | `src/winapp-CLI/WinApp.Cli/Commands/`, `winapp --cli-schema` when built |
| Services & helpers | `src/winapp-CLI/WinApp.Cli/Services/`, `*Helper.cs`, `AppxManifestDocument` |
| Packaging / MSIX / signing | `MsixService`, cert/signing services, `makeappx`/`signtool` usage |
| Manifest handling | `AppxManifestDocument`, `ManifestHelper` |
| npm wrapper | `src/winapp-npm/` |
| NuGet targets | `src/winapp-NuGet/` |
| VS Code extension | `src/winapp-VSC/` |
| Docs / guides / samples | `docs/`, `docs/guides/`, `samples/`, `README.md` |
| Build orchestration | `scripts/build-cli.ps1` |

The map is guidance, not a fence — sub-agents may research beyond it. Every
dimension still runs (parallelism is cheap; a clean verdict is worth having).

### 3. Establish model-family diversity

The **heart of this skill** is independent research from **different model
families**. Before fanning out:

1. Identify **your own** model family (Opus / GPT / Gemini).
2. Choose the two **other** families to bring in. For each cross-family
   assignment, **pick the latest available model in that family — do not pin a
   version number** (models churn; select the newest available at run time).
3. Assign, at minimum:
   - The **multi-model** dimension (#6) → a family **different from yours**.
   - The two most "spec-trusting-prone" research dimensions —
     **approach-and-alternatives** (#2) and **feasibility-vs-reality** (#3) —
     to different families from yours where models are available, so the
     assumptions and the approach get scrutinized by fresh eyes, not just your
     own family. This directly serves the skill's purpose.
   The remaining dimensions may run on your family.
4. **Degrade gracefully.** If only your family is available, run everything on it
   and record that internally. Mention it in the final report only if the missing
   diversity changes confidence or the decision.

### 4. Fan out the dimension sub-agents

Launch dimensions **#1–#5 in the same response** using the `task` tool. Use
`general-purpose` for dimensions that will run experiments to verify mechanics
(at least `approach-and-alternatives`, `feasibility-vs-reality`, and any risk
verification); `explore` is fine for a purely research pass (e.g. a
necessity/scope or DX read that needs no experiment). Each prompt is
self-contained (see template below). Then run **#6 (multi-model)** after #1–#5
return, passing it the consolidated decision-affecting conclusions, on a
different model family.

| # | Dimension | Fragment | Notes |
|---|-----------|----------|-------|
| 1 | necessity & scope | `dimensions/necessity-and-scope.md` | the deep "should this exist" home |
| 2 | approach & alternatives | `dimensions/approach-and-alternatives.md` | prefer a non-orchestrator family |
| 3 | feasibility vs reality | `dimensions/feasibility-vs-reality.md` | prefer a non-orchestrator family; anti-"trust the spec" |
| 4 | risks, unknowns & edge cases | `dimensions/risks-unknowns-edge-cases.md` | |
| 5 | DX & user impact | `dimensions/dx-and-user-impact.md` | |
| 6 | multi-model cross-check | `dimensions/multi-model.md` | **must** use a different family than you; picks latest in that family |

### 5. Consolidate

Collect all outputs. Then:

1. **Dedupe.** Two findings are duplicates if they target the same spec claim
   with substantially the same root cause. Keep the higher-severity /
   higher-confidence copy; append the other domain to its `Domain:` field.
2. **Keep bookkeeping internal.** IDs, severity, confidence, domain, model, and
   coverage help consolidation but do not belong in the final report.
3. **Sort internally.** critical → high → medium → low; within a severity, group
   by user impact.
4. **Record cross-model agreement — the strongest signal.** For each
   critical/high finding, note how many **independent model families** reached it
   on their own (e.g. "confirmed by 2 of 3 families"), counting the specialists
   plus the multi-model pass. Keep the matrix internal; surface at most one
   sentence when agreement or disagreement changes confidence or the decision.
   Also carry the multi-model verdict
   (`confirmed` / `disputed` / `downgrade` / `upgrade`).
5. **Resolve factual disagreements with evidence, not seniority.** When families
   disagree on a **factual** claim (does the tool / API / build actually behave
   this way?), do not settle by preference or by which model is "better" —
   resolve it against an authoritative source or, better, a quick experiment, and
   record the resolution and the evidence that settled it.
6. **Collect open decisions** (design decisions still to be made) from every
   dimension into one deduped list.
7. **Collect proofs required** — load-bearing technical
   assumptions that neither research nor experiment could fully close. These are
   pre-implementation spikes/proofs, and are **distinct** from open decisions
   (which are design decisions).
8. **Describe the leanest design** using the best supported alternative from
   `approach-and-alternatives` and any smaller scope from `necessity-and-scope`.
9. **Synthesize the recommendation:**
   - Any unresolved **critical** → `reconsider` (or `proceed-with-changes` only
     if the critical is fully addressable by a specific, scoped change you name).
   - One or more **high**, no critical → `proceed-with-changes`.
   - Only **medium/low**, or none → `proceed`; fold only decision-relevant items
     into `Leanest design` or `Open decisions`.
   - A load-bearing assumption left **unproven** (under `Proofs required`) should
     pull the recommendation toward `proceed-with-changes` at least, since it
     gates a safe build.
   - If your synthesized recommendation **diverges from the multi-model pass's
     independent recommendation**, say so explicitly and explain which research
     (ideally which experiment) you find more convincing — do not silently
     override a dissenting family.

### 6. Report to stdout

Print exactly the structure below. **Do not** save to a file, implement the
feature, modify the repo, or edit the spec unless explicitly asked. Cheap
experiments stay in temp directories. State each conclusion once.

```markdown
# Spec Review — <spec title or path>

## Decision
<proceed | proceed-with-changes | reconsider> — <plain rationale grounded in evidence>

<Only when it changes confidence or the decision: one sentence about independent
model agreement or disagreement and the experiment/source that resolved it.>

## User journey
- **Today:** <current command/input -> observable result>
- **Proposed:** <new command/input -> observable result>
- **Problem demonstrated:** <smallest experiment or real example proving the gap>

## Must change before implementation
### <plain finding title>
- **What is wrong:** <the design defect>
- **Show me:** <independent input -> actual -> expected evidence>
- **Why it matters:** <concrete user, delivery, or maintenance consequence>
- **Smallest fix:** <least-complex design change>
- **Location:** <spec section and supporting path/doc when useful>

<Repeat only for decision-affecting findings, or write "None.">

## Leanest design
<smallest design that serves the demonstrated journey, plus its key tradeoff>

## Open decisions
- <decision the author must make before implementation>
<Or "None.">

## Proofs required
- <unclosed load-bearing assumption> — <specific cheap experiment that closes it>
<Or "None.">
```

Fold medium/low suggestions into `Leanest design` or `Open decisions` only when
they affect the recommendation; otherwise drop them. Do not repeat findings in a
summary, risk list, details section, agreement matrix, or coverage notes. Keep
paths as support, not titles.

## Rules the orchestrator must enforce

- **Parallelism in one turn.** Fan out #1–#5 in a single response; run #6 after.
- **Independent research, not spec-trust.** Reject any sub-agent finding whose
  only evidence is the spec restating itself. Evidence must come from reality.
- **No quotas.** Accept and surface clean verdicts. Reject manufactured or
  padded concerns using the shared junior-reader and signal-to-noise gate.
- **No feature implementation. No repo or spec edits.** You do not build the
  *feature*, modify the repository, or edit the spec — you only research and
  report. "Read-only" means the repo and the spec stay untouched.
- **No file output.** Stdout only, unless the user explicitly asks for a file.
- **Verify load-bearing mechanics with cheap experiments.** Do not stop at
  reading code. When the design rests on how a tool, API, command, or build
  actually behaves, verify it *empirically* — invoke the real tool and inspect
  its output, build a small throwaway project in a temp directory to confirm a
  mechanic, or test a command's real flag/precedence behavior — and record what
  you observed. Keep experiments cheap, scoped, and confined to temp directories
  (never the repo working tree). Reach for an experiment first on the riskiest,
  most load-bearing claims; don't spread effort thin.
- **Decision-oriented.** The report leads with a clear recommendation and the
  user journey, changes, decisions, and proofs needed before coding starts.

## Sub-agent prompt template

Build each dimension prompt from these blocks, in order:

1. **Role line.** "You are the `<dimension>` sub-agent for the winappcli
   spec-review skill."
2. **The spec.** The full captured spec text (or feature description).
3. **Area map.** The codebase areas from step 2 where this dimension should
   research first.
4. **Shared contract.** Inline the contents of
   `dimensions/_shared-contract.md`.
5. **Dimension instructions.** Inline the contents of
   `dimensions/<name>.md`.
6. **Closing instruction.** "Do your own research against the real repo and
   ecosystem before concluding, and verify load-bearing *mechanics* with cheap,
   scoped experiments in a temp directory (invoke the real tool, build a
   throwaway project, test the real command behavior) rather than trusting the
   spec's claims or a code-read alone — never modify the repo or the spec.
   Return only the markdown specified by the shared contract. No preamble, no
   narration."

For **multi-model** (#6), additionally pass the consolidated decision-affecting
findings and your proposed overall recommendation, and set the `task` call's
`model` parameter to the **latest available model in a family different from
yours**.

## Example invocation pattern

```
1. Read the spec doc the user pointed at            → captured verbatim
2. grep/glob the repo for the areas it touches       → area map (Commands + MsixService + docs)
3. Note own family (e.g. Opus); assign #2/#3/#6 to GPT and Gemini (latest each)
4. Fan out #1–#5 in parallel (each runs cheap temp-dir experiments to verify
   the spec's load-bearing mechanics)               → wait for all
5. Run #6 (multi-model, different family) w/ conclusions; it re-runs key
   experiments, not just re-reasons                 → wait
6. Dedupe, sort, resolve cross-family disagreement, collect open decisions +
   proofs, identify the leanest design, synthesize the recommendation
7. Print the decision-oriented stdout report
```

## Output discipline

The final stdout block is the *only* user-visible output. Do not narrate the
process, expose model/domain/coverage bookkeeping, repeat a finding in multiple
sections, or apologize for a short list. A clean, confident recommendation is the
goal.
