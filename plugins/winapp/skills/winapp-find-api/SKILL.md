---
name: winapp-find-api
description: Agent-first search and inspection of the Windows/WinRT API surface (types, members, enums, namespaces) available to a project, resolved from its referenced .winmd/.dll metadata — or, outside any project, from the machine-wide Windows SDK. Built primarily for AI coding agents to ground code generation in real metadata instead of guessing, and equally usable by hand. Use when an AI agent or developer needs to discover an API, list a type's properties/events/methods, validate that a property exists before writing XAML/code, enumerate an enum's values, explore the namespaces and packages a project can call, or resolve a compile error that names a type or member (CS0246, CS0117, CS1061, CS0104, XAML unknown member). Works with WinUI 3, WinRT/UWP, and any project with .winmd/.dll references. Distinct from 'winapp find-ui', which returns working WinUI control samples.
---

## This is an agent-first command

`find-api` was designed for **you, the agent** — not primarily for a human reading a
terminal. It exists because the failure mode it prevents is an agent-specific one:
confidently writing a type, property, or enum value that does not exist. Treat it as
the authority on the API surface, not as an optional convenience:

- **Ground every Windows/WinRT symbol you emit.** If you are not certain a type,
  property, or enum value exists in *this project's* metadata, look it up before you
  write it. A lookup is cheaper than a build.
- **Prefer it over recall and over web search.** Your training data describes some
  version of WinUI/WinRT; `find-api` describes the exact metadata this project
  references. When they disagree, `find-api` wins.
- **Use `--json`.** Every verb emits structured output with stable shapes and
  non-zero exit codes on missing subjects, so you can gate codegen on the result
  instead of parsing prose.
- **Batch subjects into one call.** See below — this matters more here than anywhere
  else in the CLI.

Humans can and do run it directly, and everything below works fine typed by hand. But
the ergonomics (batching, `--json`, exit codes, compile-error workflows) are tuned for
agent loops.

## When to use
- Discovering which Windows/WinRT type or member does what you need ("what's the acrylic brush type?", "which control is a NavigationView?")
- Listing a type's properties, events, and methods (declared and inherited) before writing XAML or code against it
- Validating that a property exists on a type — catching typos and wrong-type mistakes before they become CS0117/XAML binding errors
- Enumerating an enum's values (e.g. `Symbol`, `Visibility`)
- Exploring the namespaces and packages a project can call into
- AI agents grounding code generation in the *actual* API surface a project references, instead of guessing
- **Diagnosing a compile error that names a type or member** — see below

## Use it on compile errors, not just before writing code
When a build fails with any of these, the error is a claim about the API surface, and
`find-api` is the authority on that surface. Look the symbol up **before** editing:

| Error | What it means | Query to run first |
|---|---|---|
| `CS0246` type not found | The type doesn't exist, or needs a different namespace/package | `winapp find-api <TypeName>` |
| `CS0117` no such member | The member doesn't exist on that type | `winapp find-api members <Type> --filter <member>` |
| `CS1061` no definition for | Same, usually on an inherited/extension member | `winapp find-api members <Type> --filter <member>` |
| XAML "unknown member/property" | The property isn't on that element | `winapp find-api check-property <Type> <Property>` |
| `CS0104` ambiguous reference | The short name exists in two namespaces | `winapp find-api <TypeName>` (lists every candidate fully-qualified) |

**Read the whole error list first, then make one call.** A failed build almost never
reports exactly one bad symbol, and fixing them one at a time means one lookup, one edit,
and one rebuild per symbol — the slowest possible loop. Collect *every* uncertain type and
member from the complete build output, then verify them together:

```powershell
# Build failed with CS0117 on Severity, CS1061 on Titel, CS0246 on TeachingTipBar.
# One call, not three:
winapp find-api check-property InfoBar Severity Titel IsOpen
winapp find-api TeachingTipBar TeachingTip
```

Then apply all the fixes in one edit and rebuild once. Guessing a replacement name and
rebuilding is slower than one lookup and is how hallucinated APIs survive several build
cycles. If a fix doesn't work the first time, you *must* look it up rather than guessing
again.

## Batch your lookups — one call, many subjects
**This is the single most important thing to get right.** The dominant cost of a
lookup is not the size of the answer, it is the round trip: every extra call re-sends
the whole conversation. Ten small calls cost far more than one call that returns ten
answers.

`search`, `members`, `enums`, and `check-property` all accept **multiple subjects in a
single invocation**. Verify everything you are unsure about in one shot, *before* you
start writing code:

```powershell
# One call, five properties — instead of five calls
winapp find-api check-property InfoBar Severity IsOpen Message Title IsClosable

# One call, several types
winapp find-api members InfoBar TeachingTip --filter severity
winapp find-api enums InfoBarSeverity Symbol Visibility
winapp find-api "acrylic brush" "teaching tip" --max 5
```

`check-property` batches *properties on one type* (type first, then every property).
The other verbs take a list of types/queries. In batch mode `check-property` prints a
one-line ✅ per property that exists and the full near-miss detail only for ones that
don't, so a clean batch is nearly free to read.

**Two moments to batch, and the second is the one people miss:**

1. **Before you write code** — verify every type and property the screen needs, in one call.
2. **After a build fails** — read the *entire* error list, collect every uncertain symbol
   across all of it, and verify them in one call before you edit anything. Fixing errors
   one at a time is the most expensive loop available: it costs a lookup, an edit, and a
   full rebuild per symbol, and a rebuild usually surfaces the next bad symbol you could
   have caught in the same call.

**Exit code:** a batch exits `0` only if *every* subject resolved and was found. Any
missing type or property exits `1`, so you can still gate codegen on a whole batch.

A single subject returns exactly the same output as before, so nothing you already
know how to do changes.

## Use `--filter` on big member lists — not on enums
`--filter` is a case-insensitive substring match on the member/value name, and it
exists for one case: a type with hundreds of members (`Button` has ~370) where you
already know roughly what you're looking for.

```powershell
winapp find-api members Button --filter background   # 4 of 368 members — worth it
```

Do **not** filter enums. Almost every enum is small enough to read whole, and even the
largest one in WinUI (`Symbol`, 197 values) costs less to dump once than to probe two
or three times with guessed substrings:

```powershell
winapp find-api enums Symbol                          # ~580 tokens, one call, done
winapp find-api enums Symbol --filter folder          # a guess; you'll likely re-run
```

The same rule applies everywhere: **never re-run the same command with different
filter text.** If you don't know the right substring, dump the list once and read it.
Iterative narrowing is the most expensive thing you can do with this tool.

Output always reports the unfiltered total, so a narrow view is never mistaken for a
small API. A filter that matches nothing exits `0` and says so explicitly — that means
"nothing matched your filter", not "no such type".

## Prerequisites
- **Querying a project:** run from (or point `--project-dir` at) a project that has been **restored** — the index is built from `project.assets.json` and the restored NuGet/SDK packages. If the project has never been restored, run `winapp restore` (or `dotnet restore`) first. A solution directory works too: run from the folder holding the `.sln`/`.slnx` and the projects it builds are indexed and answer the query.
- **Querying with no project:** nothing is required. From a directory with no project and no solution, `find-api` answers from the machine-wide **SDK scope** (Windows SDK + Windows App SDK), so an agent can explore the API surface *before* scaffolding an app. No network access is needed in either case.
- The first query builds the index automatically (this can take a few seconds for a large SDK like WindowsAppSDK); subsequent queries are served from the warm cache. The project index refreshes automatically when the project is re-restored.
- No setup is needed beyond a restored project — the index lives under the global `.winapp` cache (`cache/find-api/`) and is shared across projects.

## Common patterns

### Search for an API
```powershell
# Bare form is a search — matched against type and member names, then their summaries
winapp find-api "acrylic brush"
winapp find-api NavigationView
winapp find-api "list view" --max 10

# Several searches in one call
winapp find-api "acrylic brush" "teaching tip" NavigationView --max 5
```

### Inspect a type's members
```powershell
# Short name or fully-qualified name both work
winapp find-api members NavigationView
winapp find-api members Microsoft.UI.Xaml.Controls.NavigationView

# Several types in one call
winapp find-api members InfoBar TeachingTip ContentDialog

# Narrow a large type instead of dumping ~370 members and searching the output
winapp find-api members NavigationView --filter selected

# Unfiltered listings show declared members with signatures and summarize
# inherited members by name; they also omit dependency-property statics and
# descriptions. --all restores everything (works with --json; --verbose does not).
winapp find-api members NavigationView --all
```

### Validate a property before you write it
`check-property` is the cheapest way to avoid a hallucinated property: it exits
non-zero when the property does not exist, so you can gate codegen on it. Run it for
any property you are not certain about — especially one you are about to put in XAML,
where a wrong name surfaces as a runtime `XamlParseException` rather than a build error.

```powershell
# Check every property you're unsure about in one call — type first, then properties
winapp find-api check-property InfoBar Severity IsOpen Message Title
# ✅ one line each for the ones that exist; full detail only for the ones that don't
# Exits non-zero if ANY property is missing — safe to gate codegen on

# Single property form is unchanged
winapp find-api check-property Button Background

# It also finds attached properties and suggests near-misses and other types
# that do have the property, so a failed check usually tells you the real answer
winapp find-api check-property Window SystemBackdrop

# Read-only properties come back ⚠️ "read-only, cannot be assigned" instead of ✅
# — they exist (so the exit code stays 0), but assigning to them won't compile
winapp find-api check-property Button ActualWidth

# Property names are matched case-sensitively, because C# and XAML are.
# The wrong case exits non-zero and offers the real spelling as a near match.
winapp find-api check-property Button background   # exits 1, suggests Background
```

### List enum values
```powershell
# Dump enums whole — they're small. Batch them rather than filtering them.
winapp find-api enums Symbol
winapp find-api enums InfoBarSeverity Visibility Microsoft.UI.Xaml.TextWrapping
```

### Inspect a large type without dumping it
```powershell
winapp find-api members Button --filter background
winapp find-api members NavigationView --filter selection
```

### See what the project references
```powershell
winapp find-api packages
winapp find-api stats
```

### Manage the index
```powershell
# Force a re-index (usually automatic after restore); --scan indexes every project under the dir
winapp find-api refresh
winapp find-api refresh --scan
```

### Explore the SDK with no project
```powershell
# From a directory with no project, results come from the machine-wide Windows SDK
# scope (reported as scope: sdk) — useful before an app has been scaffolded
winapp find-api "acrylic brush"
winapp find-api members Button --project sdk

# Rebuild the SDK scope after installing a new Windows SDK
winapp find-api refresh --project sdk
```

### Script against it with --json
```powershell
# Every verb supports --json for a clean, machine-readable payload on stdout
winapp find-api NavigationView --json
winapp find-api check-property Button Backgruond --json   # exits 1, JSON reports found:false

# Payloads say which index answered: scope, projectName, and projectDir
winapp find-api enums Symbol --json
# { "scope": "project", "projectName": "MyApp", "projectDir": "C:\\src\\MyApp",
#   "fullName": "Microsoft.UI.Xaml.Controls.Symbol",
#   "totalValues": 197, "values": [ "Accept", "Add", ... ] }

# A batch wraps the same per-subject payloads in an envelope
winapp find-api check-property InfoBar Severity Backgruond --json
# { "count": 2, "missingCount": 1, "results": [ { ...found:true... }, { ...found:false... } ] }
```

## Key concepts
- **Batch, don't iterate.** `search`, `members`, `enums`, and `check-property` all take multiple subjects per call. Cost scales with the number of calls, not the size of the answer.
- **Bare form = search.** `winapp find-api "<query>"` searches; the sub-verbs (`members`, `check-property`, `enums`, `packages`, `stats`, `refresh`) drill into specifics.
- **Batch payload shape.** One subject returns the plain per-subject payload (text and `--json`) exactly as before. Two or more return an envelope: `{ count, results: [...] }`, plus `missingCount` for `check-property`. A batch exits `0` only if every subject resolved *and* was found.
- **Lexical, not semantic.** Search matches type and member *names* (and signatures) by whole identifier word — `llm` finds `IImageLLMAdapterSession`, not `ScrollMode`. Spaces between identifier words are supported; see [query matching](https://github.com/microsoft/winappCli/blob/main/docs/usage.md#find-api) for ranking and examples. A query that matches no name is then tried against the documented summaries, so `"random-access stream"` finds `IRandomAccessStream`; description hits rank below every name hit. There are no embeddings, and only summaries the packages actually ship are searchable — a package without XML documentation has no description text to match. Phrase queries the way the API is named.
- **Automatic indexing.** The index builds on first query and refreshes when `project.assets.json` changes, so it stays in sync with restores. Use `refresh` only to force a rebuild or index a project for the first time without querying.
- **Project resolution and scopes.** Every answer names its scope (`scope` in `--json`, a note in text) and the index that produced it (`projectName`, `projectDir`). A project in the current directory (or `--project` / `--project-dir`) gives `scope: project`, covering the Windows SDK, Windows App SDK, *and* the project's NuGet packages. A directory with **no** project and **no** solution gives `scope: sdk` — the machine-wide Windows SDK + Windows App SDK only, which excludes third-party NuGet packages. Such a query is *never* answered from some other indexed project, so results don't depend on unrelated global state. From a solution directory, the projects the solution builds answer instead; if it builds more than one, the query lists them and asks for `--project <name>`. Use `--project sdk` to pick the SDK scope explicitly from inside a project.
- **Exit codes for scripting.** `search` with no hits, `check-property` on a missing property, and `enums` on a non-enum all exit non-zero — gate code generation and CI checks on them. Read-only is not a failure: the property exists, so the exit code stays `0` while the output flags it (`writable: false` in `--json`).
- **Property names are case-sensitive.** C# and XAML are, so `check-property Button background` exits non-zero and offers `Background` as a near match rather than confirming a name you cannot write. (`--filter` on `members`/`enums` is a separate, case-*insensitive* substring search.)
- **Ambiguity detection.** When a short type name resolves to multiple namespaces (a CS0104 risk), search surfaces every candidate with its fully-qualified name so you can pick the right one. Candidates are de-duplicated, so each fully-qualified name appears once even when several packages ship the same type, and every listed candidate is a genuinely different name you can choose between. Only *exact*-name collisions are listed when the query names a real type, and the list obeys `--max` (default `5`), so an ambiguous short name costs a few lines rather than pages.
- **Short names in `members` / `enums` / `check-property`.** A short name shared by a modern `Microsoft.*` type and its legacy `Windows.*` UWP twin resolves to the `Microsoft.*` one — that is the projection a Windows App SDK app uses, and the resolved fully-qualified name is always printed so you can see which type answered. Any other collision is an error listing the candidates; re-run with the fully-qualified name.
- **Search results exclude `ABI.*` projection types.** These compiler-generated interop structs mirror real types and are never what you want to write in source, so search omits them. They remain reachable by exact name — `members ABI.Some.Type` still works if you are debugging interop.
- **Projects without an MSBuild project file.** An Electron (or other non-.NET) app driven by `winapp.yaml` has no `.csproj` and no `project.assets.json`. `find-api` indexes it from the `.winapp/winmds.lock.json` that `winapp restore` writes, and names the project after its directory. A directory holding both a `.csproj` and a `winapp.yaml` is indexed from the `.csproj`, which describes what it actually compiles against.
- **Negative answers are qualified when the index is incomplete.** If a package's metadata failed to parse, "no such type/property" is indistinguishable from "that package was never read" — the false negative you must not generate code from. So a miss (including a `search` with zero results) carries a note saying the index is partial and to run `winapp find-api refresh`. A positive answer never needs it.
- **Generic types resolve however you write them.** Metadata stores generics with an arity suffix (`` IAsyncOperation`1 ``), but nobody writes that. `members IAsyncOperation`, `members IAsyncOperation<StorageFile>`, and ``members TypedEventHandler`2`` all resolve. Bare names match any arity; a stated arity (either form) must match, so `Holder<A, B>` will not resolve to a one-parameter `Holder<T>`.
- **Inherited members.** `members` covers inherited properties/events/methods and marks their declaring type, so you see the full usable surface of a control. Overloads that differ only in their parameters are all listed — a name is never collapsed to a single signature.
- **Signatures are copyable as printed.** A method you call on the type rather than on an instance is marked `static`, and a by-reference parameter carries the keyword C# requires — `out`, `in`, or `ref`. `Boolean TryGetValue(String key, out String value)` compiles as written; do not "fix" it to `ref`.
- **Unfiltered listings are trimmed.** An unfiltered `members` call is an *orientation* query, so it answers that shape and leaves out the rest. Declared members keep full signatures inline; inherited members are grouped by declaring type and listed **by name only** (Button: 8 declared, 280 inherited across 6 base types). It also omits dependency-property identifier statics (`BackgroundProperty`, ~28% of a WinUI control's properties), per-member descriptions, and JSON fields implied by their surroundings (`kind`, `returnType`, `inherited` when false). Measured: `members Button --json` went from 91,954 to 10,567 characters. What was left out is always reported (`hiddenDependencyProperties`, `descriptionsOmitted`, `hint`), and both `--filter` and `--all` see the complete surface with full signatures, so `members Button --filter BackgroundProperty` still finds it and `--filter Click` still returns the inherited `Click` signature. Use `--all` for the exhaustive listing; `--verbose` does the same but cannot be combined with `--json`.
- **`--json` omits diagnostics.** Cache file paths appear only under `--verbose`, and empty suggestion arrays are omitted rather than sent as `[]`.

## Troubleshooting
- **"No indexed API metadata was found for this project."** You are standing in a real project that hasn't been indexed — usually because it has not been restored. Run `winapp restore` (or `dotnet restore` for a .NET project without `winapp.yaml`), then retry. `find-api` deliberately does *not* silently narrow to the SDK scope here, because that would hide the project's own NuGet packages and make its types look nonexistent.
- **Results say `scope: sdk` but you expected project APIs.** There is no project (and no solution) in the current directory, so the machine-wide SDK scope answered. `cd` into the project (or pass `--project-dir <path>`); third-party NuGet packages such as the Community Toolkit only exist in the `project` scope. A `--project-dir` that doesn't exist is a hard error, not a silent fallback to `sdk`.
- **"No project was found here and no Windows SDK metadata is available on this machine."** Neither a project nor an installed Windows SDK / Windows App SDK was found. Run from a project directory, or install the SDK.
- **"Project '<name>' is not indexed."** The name passed to `--project` doesn't match a cached project. Run `winapp find-api refresh` in that project's directory, or use `--project-dir <path>` instead. `refresh --project <name>` fails the same way rather than quietly indexing the current directory instead.
- **"'<Type>' is ambiguous."** Two indexed types share that short name and neither is the `Microsoft.*`/`Windows.*` twin of the other. Re-run with the fully-qualified name from the listed candidates.
- **"Installed Windows App Runtime X does not match the referenced Windows App SDK Y."** The machine has a newer Windows App Runtime than the release your project references, so its metadata is left out of the project's API surface. That is deliberate: including it would confirm types your project cannot compile against. To use those APIs, reference the matching Windows App SDK version.
- **A type/member you expect is missing.** The owning package may not be restored, or the index is stale. Re-restore the project (auto-refreshes) or run `winapp find-api refresh` to force a rebuild. After installing a *new Windows SDK*, rebuild the SDK scope with `winapp find-api refresh --project sdk`.
- **First query is slow.** That's the one-time index build for the project's packages; subsequent queries are fast against the warm cache.

## Related skills
- **`winapp-find-ui`** — when you need a *working WinUI control sample* (XAML + C#) rather than the raw API surface. Use `find-api` to confirm a type/member exists and inspect its shape; use `find-ui` to get example usage.
- **`winapp-ui-automation`** (`winapp ui`) — inspects a *running app's* UI tree; `find-api` inspects the *static API surface* a project references.

## CLI reference
- `winapp find-api "<query>" [<query>...] [--max N]` — search across type and member names, then their summaries (bare form). Each hit carries its owning package and a one-line purpose. Exits non-zero on no hits; with no query at all it prints usage and exits `0`.
- `winapp find-api members <type> [<type>...] [--filter <text>] [--all]` — properties, events, and methods of a type. An unfiltered listing shows declared members with signatures, summarizes inherited members by declaring type (names only), and omits dependency-property statics and descriptions; `--filter` and `--all` see everything with full signatures.
- `winapp find-api check-property <type> <property> [<property>...]` — validate properties exist; exits non-zero if any is missing. Read-only properties are flagged (`writable: false`) but still exit `0`.
- `winapp find-api enums <type> [<type>...] [--filter <text>]` — enum values; exits non-zero when the type is not an enum.
- `winapp find-api packages` — indexed NuGet/SDK packages with per-package counts.
- `winapp find-api stats` — aggregate index statistics for the project.
- `winapp find-api refresh [--scan]` — force a re-index; `--scan` walks all projects under the directory.

Common options (all verbs): `--json` for machine-readable output, `--project <Name>` / `--project-dir <path>` to select a project, `--project sdk` to query the machine-wide Windows SDK scope.

`--filter` means **case-insensitive substring** on `members` and `enums`. Filtered payloads also report the unfiltered totals (`totalValues`, `totalProperties`/`totalEvents`/`totalMethods`). Prefer it on large member lists; prefer dumping enums whole.

Every `--json` query payload identifies the index that answered: `scope` (`project` or `sdk`), `projectName`, and `projectDir` (omitted for the SDK scope). Because project names are not unique across directories, `projectDir` is the reliable identity when you need to confirm *which* project a result came from.
