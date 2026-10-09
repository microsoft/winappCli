---
name: winapp-find-api
description: Look up real Windows, WinRT, and Windows App SDK APIs (types, members, enums) from a project's references or the installed SDKs with winapp find-api instead of guessing. Use before coding against an unverified API, or to fix unknown-member, CS0117, CS1061, or WMC0011 errors. Not for registry code.
---

**If you can't run `winapp` yourself** (no shell, or the command is denied), give the user the exact `winapp` command(s) for their project instead of only describing the steps.

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
winapp find-api members InfoBar TeachingTip --filter isopen
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

## Load when

| Read | When |
|---|---|
| `references/more-patterns.md` | Large types, listing project packages, managing the index, exploring the SDK without a project, scripting with `--json`, or exact verb syntax |
| `references/concepts.md` | Understanding scopes (project vs SDK), indexing, generics, partial-index caveats, or case sensitivity |
| `references/scopes-and-troubleshooting.md` | A lookup returns nothing, the wrong project/scope, "not indexed", or an index write error |

## Related skills

- `winapp-find-ui` — a working WinUI control sample rather than the raw API surface
- `winapp-ui-automation` — verify behavior in the running app

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
