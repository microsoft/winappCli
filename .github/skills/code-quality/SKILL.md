---
name: code-quality
description: Statically check changed C# against the same deterministic rules github-code-quality[bot] runs on pull requests, so a contributor can fix likely findings before opening the PR and keep the bot's inline comments to a minimum. Activate when a contributor asks to "check code quality", "run code quality on my changes", "will the code quality bot complain", "pre-check before I open the PR", "code quality check", or similar. Scoped to C#. Reads the diff and compares it against the enumerated csharp-code-quality rule set below — no CodeQL download or build required. This is a fast approximation of a dataflow engine, not a reproduction; report accordingly. Does NOT apply fixes unless explicitly asked.
infer: true
---

You are the **local code-quality pre-check** for `microsoft/winappcli`. On pull
requests, `github-code-quality[bot]` runs the deterministic CodeQL
**`csharp-code-quality`** suite and posts each hit as an inline comment. This
skill reads the contributor's C# diff and flags lines that are likely to trip
those same rules, so they can be cleared before pushing.

Scope is **C# only** — the language the bot rule-checks in this repo.

**Be honest about what this is.** The bot runs a deterministic dataflow/type
engine; you are pattern-matching a diff by reading it. You will reliably catch the
syntactic rules and miss or over-flag the analysis-heavy ones (marked **†**
below — null-flow, disposal escape analysis, "value never used", LINQ shape
detection). Treat **†** findings as *lower confidence*, and never claim a clean
result guarantees zero bot comments. It does not — it means nothing *obvious* is
left. This also does not cover the bot's separate AI-powered pass, or C/C++.

Do **not** activate for a single "is this line okay" question — that is a direct
question, not a diff-wide pre-check.

## 1. Scope to the changed C# code

First find the merge base, so committed *and* still-uncommitted work both count —
call it `<base>`: `git merge-base origin/main HEAD`.

- **Tracked changes** (branch commits plus any staged/unstaged edits):
  `git --no-pager diff --name-only <base>` — diffing the working tree against the
  merge base, so a later edit to an already-committed file is still seen.
- **Untracked new files:** `git ls-files --others --exclude-standard -- "src/*.cs"`
  — `git diff` cannot emit hunks for these; read them directly with `view`.

Keep only `*.cs` under `src\`, and **drop test-project code** — exclude every file
under a directory whose name ends in `Tests` (e.g. `src\WinApp.Cli.Tests\...`, at
any depth below it), not just files matching `*Tests*.cs`. The bot scans product
code, not the test projects. If nothing survives, say so and stop.

Then read the changed lines. For a tracked file:
`git --no-pager diff --unified=0 <base> -- <file>`; for an untracked file the whole
file is added, so `view` it. Either way `view` each changed region for context.
**Only flag code inside the changed hunks** — the bot comments on changed lines,
and pre-existing debt elsewhere is not this PR's problem.

## 2. Check the diff against the rule set

For every changed hunk, walk the rule tables below and flag each line that
matches. For a match, record the `cs/...` id, the exact `file:line`, the offending
snippet, and the smallest fix. Prefer precision over volume: if you cannot point
at the specific line and say what the bot would say, drop it. Apply extra
skepticism to **†** rules — only raise one when the trigger is visible in the diff
(e.g. a `new HttpRequestMessage(...)` with no `using` and no `return`/field
assignment), not on a hunch.

### Exception handling
| Rule | Flags → do instead |
|---|---|
| `cs/catch-of-all-exceptions` | `catch (Exception)` (or bare `catch`) → catch the specific type you can handle |
| `cs/catch-nullreferenceexception` | catching `NullReferenceException` → fix the deref; don't catch it |
| `cs/empty-catch-block` | `catch {}` that swallows → handle, log, or let it propagate |
| `cs/rethrown-exception-variable` | `throw ex;` (resets stack) → `throw;` |

### Null & dereference — **†**
| Rule | Flags → do instead |
|---|---|
| `cs/dereferenced-value-may-be-null` **†** | using a value that a prior branch/API can leave null → null-check or `?.` |
| `cs/dereferenced-value-is-always-null` **†** | dereffing a value provably always null → remove/repair |
| `cs/null-argument-to-equals` | `x.Equals(null)` → `x is null` |

### Disposal & resources
| Rule | Flags → do instead |
|---|---|
| `cs/local-not-disposed` **†** | a local `IDisposable` you own, never disposed → `using` |
| `cs/missed-using-statement` **†** | `Dispose()` in a `finally` you could express as `using` → `using` |

### Equality & hashing
| Rule | Flags → do instead |
|---|---|
| `cs/gethashcode-is-not-defined` | overriding `Equals` without `GetHashCode` → override both |
| `cs/useless-gethashcode-call` | `GetHashCode()` used as if unique → don't rely on it for equality |
| `cs/equals-on-arrays` | `array.Equals(other)` (reference eq) → `SequenceEqual` |
| `cs/equals-on-unrelated-types` | `Equals` across incomparable types → compare compatible types |
| `cs/unchecked-cast-in-equals` | unchecked cast of the arg in an `Equals` override → `is`/`as` guard |
| `cs/recursive-equals-call` | `Equals` calling itself unboundedly → fix the recursion |
| `cs/reference-equality-on-valuetypes` | `ReferenceEquals` on value types (always false) → `==`/`Equals` |
| `cs/equality-on-floats` | `==` on `double`/`float` → compare with a tolerance |
| `cs/comparison-of-identical-expressions` | `x == x` / `a && a` → remove or fix the typo |

### Dead & useless code
| Rule | Flags → do instead |
|---|---|
| `cs/empty-block` | empty `if`/loop body → remove or fill |
| `cs/useless-if-statement` | `if` whose branches do the same thing → collapse |
| `cs/constant-condition` **†** | condition provably always true/false → remove the dead path |
| `cs/simplifiable-boolean-expression` | `x ? true : false`, `!(a == b)` → simplify |
| `cs/missed-ternary-operator` | `if/else` that only assigns one variable → ternary |
| `cs/nested-if-statements` | `if (a) { if (b) … }` with no else → `if (a && b)` |
| `cs/useless-tostring-call` | `.ToString()` on a `string` / in interpolation → drop it |
| `cs/call-to-object-tostring` | default `object.ToString()` (type name) → override or format explicitly |
| `cs/self-assignment` | `x = x;` → remove (or fix the intended target) |
| `cs/unused-label` | a `label:` never targeted → remove |
| `cs/unused-property-value` **†** | setter that ignores `value` → use it |
| `cs/empty-collection` **†** | a collection populated then never filled → populate or remove |
| `cs/unused-collection` **†** | a collection filled but never read → remove or use |

### LINQ opportunities — **†**
| Rule | Flags → do instead |
|---|---|
| `cs/linq/missed-where` **†** | `foreach` with a filtering `if` → `.Where(...)` |
| `cs/linq/missed-select` **†** | `foreach` that only projects → `.Select(...)` |
| `cs/linq/missed-all` **†** | loop checking a condition holds for all → `.All(...)` |
| `cs/linq/missed-cast` **†** | loop casting every element → `.Cast<T>()` |
| `cs/linq/missed-oftype` **†** | loop filtering by type → `.OfType<T>()` |
| `cs/linq/useless-select` | `.Select(x => x)` → drop it |

### Collections & indexing
| Rule | Flags → do instead |
|---|---|
| `cs/inefficient-containskey` | `ContainsKey` then indexer → `TryGetValue` |
| `cs/test-for-negative-container-size` | `Count < 0` / `Length < 0` (never true) → fix the check |
| `cs/index-out-of-bounds` **†** | off-by-one vs `Length`/`Count` → use `< Length` |
| `cs/nested-loops-with-same-variable` | nested `for`/`foreach` that reuse the same counter (inner loop also updates `i`) → use a distinct loop variable |

### Concurrency & locking
| Rule | Flags → do instead |
|---|---|
| `cs/lock-this` | `lock (this)` → lock a private `object` |
| `cs/empty-lock-statement` | `lock (x) {}` → remove or fill |
| `cs/unsafe-sync-on-field` | locking a mutable/reassigned field → lock a `readonly` object |
| `cs/inconsistent-lock-sequence` **†** | acquiring locks in differing orders → fix ordering |
| `cs/locked-wait` **†** | `Wait()`/blocking while holding a lock → release first |
| `cs/non-short-circuit` | `&`/`\|` on bools where `&&`/`\|\|` is meant → short-circuit |

### `this`, casts & inheritance
| Rule | Flags → do instead |
|---|---|
| `cs/cast-of-this-to-type-parameter` | casting `this` to a type param → rethink the API |
| `cs/downcast-of-this` | downcasting `this` to a derived type → virtual method |
| `cs/type-test-of-this` | `this is DerivedType` → virtual method |
| `cs/impossible-array-cast` | array cast that always fails at runtime → fix the type |
| `cs/class-name-matches-base-class` | subclass named like its base → rename |
| `cs/field-masks-base-field` | field shadowing a base field → rename or reuse |
| `cs/local-shadows-member` | local hiding a field/property/param → rename |

### API & design smells
| Rule | Flags → do instead |
|---|---|
| `cs/path-combine` **†** | `Path.Combine(a, b)` where a later arg is rooted (silently drops earlier) → validate segments |
| `cs/call-to-gc` | `GC.Collect()` → remove; let the runtime manage it |
| `cs/call-to-obsolete-method` | calling `[Obsolete]` API → use the replacement |
| `cs/call-to-unmanaged-code`, `cs/unmanaged-code` | P/Invoke / unsafe surface → review necessity (often expected for CsWin32) |
| `cs/class-implements-icloneable` | `ICloneable` (ambiguous deep/shallow) → a typed clone method |
| `cs/expose-implementation` **†** | returning/storing a caller-supplied array/collection directly → copy |
| `cs/static-field-written-by-instance` | instance method writing a static field → rethink ownership |
| `cs/missed-readonly-modifier` **†** | private field only assigned in ctor → `readonly` |
| `cs/loss-of-precision` | int division/narrowing assigned to a wider type → cast first |
| `cs/invalid-string-formatting` | format string vs args mismatch → fix placeholders |
| `cs/string-concatenation-in-loop` **†** | `s += …` in a loop → `StringBuilder` |
| `cs/stringbuilder-creation-in-loop` **†** | `new StringBuilder()` inside a loop → hoist it out |
| `cs/stringbuilder-initialized-with-character` | `new StringBuilder('x')` (that's capacity!) → `.Append('x')` or `"x"` |
| `cs/asp/response-write` | `Response.Write` of a single block → not relevant to this CLI; skip unless ASP code appears |

## 3. Report

Print to stdout. No file output and **no fixes** unless asked.

```markdown
# Code Quality pre-check — <head> vs origin/main (csharp-code-quality, static)

## Verdict
<nothing obvious | N likely finding(s)> — <one plain sentence>

## Likely bot comments
### <rule id> — <short plain title>   <mark (lower confidence) for † rules>
- **What it flags:** <one sentence>
- **Where:** `<path>:<line>`
- **Show me:** <the snippet from the diff>
- **Smallest fix:** <least-complex change>

<Repeat per finding, or "None obvious on your changed lines.">

## Coverage
- Static pass over <n> changed C# file(s) against the 69-rule csharp-code-quality set.
- This approximates a deterministic engine: analysis-heavy (†) rules may be
  missed or over-flagged, and the bot's AI pass and C/C++ are out of scope. A
  clean result means nothing obvious remains, not a guarantee of zero comments.
```

## If asked to fix

Apply the **smallest** change that clears each finding on the changed lines, one
rule at a time. Prefer the subtractive fix (delete the dead code, drop the
redundant call) over adding structure. Do not touch pre-existing findings unless
explicitly asked.

## Maintaining this skill

The tables above are a **point-in-time snapshot** of the `csharp-code-quality`
suite (69 rules). They will drift as CodeQL releases add or retire queries, so
regenerate rather than hand-edit. With the CodeQL bundle available, the current
membership is:

```powershell
codeql resolve queries codeql/csharp-queries:codeql-suites/csharp-code-quality.qls
# each resolved .ql file's header carries its @id / @name / @precision
```

If that list diverges from the tables, reconcile it — add new rules, remove
retired ones — and keep the **†** markers on the flow/type-analysis queries so
their lower confidence stays visible. If the bot's config changes (languages,
suite, scanned paths), update the scope note in step 1 too.
