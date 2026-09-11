---
name: winapp-find-ui
description: Agent-first search of WinUI 3 controls and samples for a working code example. Built primarily for AI coding agents to pull real, compiling WinUI markup into the editor instead of inventing it, and equally usable by hand. Use when building a WinUI 3 UI and you need to discover which control fits an intent (e.g. 'tabbed layout', 'a card with an image and title', 'swipeable list rows') and get a real code example from the WinUI Gallery or the Windows Community Toolkit (Gallery/Toolkit return XAML and/or C#). The microsoft-ui-reactor ReactorGallery is an opt-in source (C#-only declarative WinUI) searched only via --source reactor. WinUI-only — not WPF/WinForms. Distinct from 'winapp ui', which automates a running app's UI, and from 'winapp find-api', which searches the API surface (types, members, enums) a project references.
---

## This is an agent-first command

`find-ui` was designed for **you, the agent** — not primarily for a human browsing a
gallery. It exists because the failure mode it prevents is an agent-specific one:
writing plausible-looking XAML for a control whose real markup, properties, or
namespace differ from what you recall. Treat it as the source of truth for WinUI
markup:

- **Fetch a real sample before writing WinUI markup.** If you are about to hand-write
  XAML for a control you have not just looked at, search for it first and fetch the
  scenario with `--id`. The returned code comes from the shipping WinUI 3 Gallery and
  Windows Community Toolkit, so it compiles.
- **Prefer it over recall and over web search.** It returns code from a pinned,
  cached corpus of the real galleries rather than a blog post of unknown vintage.
- **Use `--json`.** Search, `--id`, and `--list` all emit stable structured shapes,
  and *every* failure — including parser errors — is a flat `{"error": "..."}` on
  stdout with a non-zero exit code, so you can branch on the result on every path.
- **Pair it with `winapp find-api`.** `find-ui` gives you working markup; `find-api`
  verifies any property you then change or add.

Humans can and do run it directly, and everything below works fine typed by hand. But
the ergonomics (compact search → `--id` fetch, `--json`, exit codes) are tuned for
agent loops.

## When to use

Use this skill when building a **WinUI 3** UI and you need to discover which
control fits an intent and get a real, working code example — without leaving the
CLI or guessing at control names and APIs.

`winapp find-ui` searches the **WinUI 3 Gallery** and the **Windows Community
Toolkit** (plus a few curated **core** patterns) and returns a working code
snippet plus where it came from. A third source, the **microsoft-ui-reactor
ReactorGallery**, is **opt-in**: it is excluded from a normal search and is only
searched when you pass `--source reactor`.

- **WinUI-only.** The corpus is WinUI 3 Gallery + Windows Community Toolkit (+
  Reactor when opted in). It does **not** cover WPF, WinForms, or other UI
  frameworks.
- **Reactor is opt-in and for Reactor projects only.** Reactor is a C#-only
  declarative/MVU framework — its samples can't paste into a standard `dotnet new
  winui` XAML + code-behind app, so a default search deliberately omits it. Only
  reach for `--source reactor` when you're actually building a Reactor app.
- **Result shape varies by source.** Gallery and Toolkit scenarios return XAML,
  C#, or both (one-sided samples are kept); Reactor scenarios are C#-only
  declarative WinUI (no XAML).
- Distinct from `winapp ui search`, which searches a *running app's* UI tree via
  UI Automation — unrelated to control/sample discovery.

**Front-load lookups, then code.** Search for each feature you need up front, pick
the right control and scenario id, fetch its full code with `--id`, then write your
XAML — don't interleave search-and-code.

## Workflow

```bash
# 1. Search compactly to find the control + its scenario ids (WinUI-only)
winapp find-ui "tabbed layout"

# 2. Fetch the full XAML + C# (and prerequisite notes) for the best match
winapp find-ui --id gallery-tabview-1

# 3. Batch: fetch several scenarios at once
winapp find-ui --id gallery-tabview-1 --id toolkit-tabbedcommandbar-1
```

## Examples

```bash
# Natural-intent search
winapp find-ui "a card with an image and title"
winapp find-ui "swipeable list rows"

# Restrict to one source
winapp find-ui "settings card" --source toolkit
winapp find-ui "color picker" --source gallery

# Reactor is opt-in: excluded from a normal search, only searched with --source reactor
# (use this only for a Reactor/MVU project — its C#-only samples don't fit standard XAML apps)
winapp find-ui "flex layout" --source reactor

# Return more candidates
winapp find-ui "navigation" --max 6

# Browse everything (heavy — prefer search; excludes opt-in Reactor)
winapp find-ui --list

# Force a corpus refresh from GitHub
winapp find-ui "info bar" --refresh

# Search the built-in core patterns fully offline (no network, no fetch)
winapp find-ui "file picker" --source core
```

## Agent-friendly output

Add `--json` for a structured, grounded result an agent can consume in one shot:

```bash
winapp find-ui "color picker" --json
```

- **Search** → `{ query, matchCount, matches: [ { source, control, score, description?, scenarios: [ { id, header } ] } ] }` — compact; use the `id` values to fetch code.
- **`--id`** → `{ results: [ { id, found, content } ] }` — `content` is the code markdown block (XAML and/or C# for Gallery/Toolkit; C# only for Reactor).
- **`--list`** → `{ count, items: [ { id, header } ] }`.
- On error, `--json` emits `{ "error": "..." }` on stdout with a non-zero exit code.

## Notes & tips

- **One mode at a time.** A search query, `--id`, and `--list` are mutually
  exclusive — combining them is rejected. `--source` applies to search only.
- **Reactor is opt-in.** A normal search and `--list` cover Gallery + Toolkit +
  core only. Pass `--source reactor` to search Reactor (reactor-only results);
  a `reactor-<control>-<n>` `--id` still fetches even without the flag. Skipping
  Reactor by default keeps its C#-only samples from outranking usable controls in
  a standard XAML app.
- **Everything works offline.** The Gallery/Toolkit/Reactor corpus ships inside the
  CLI, so search, `--list`, and `--id` all work with no network access — including
  on a first run in a sandbox or behind a proxy that blocks
  `raw.githubusercontent.com`. When GitHub is reachable the CLI refreshes from it
  and caches per-user under `<global .winapp>/cache/find-ui` (refreshed at most
  every 24 hours, or on demand with `--refresh`); the built-in corpus is a floor,
  never a ceiling, so live data always wins. `--source core` searches the curated
  built-in patterns and never touches the network at all.
- **Check the corpus provenance when it matters.** `--json` carries `"corpus"`:
  `"network"` (fetched this run), `"cache"` (this machine's earlier fetch), or
  `"embedded"` (served from the corpus built into the CLI — either the fetch failed
  or the local cache predates the bake). Only `"embedded"` may lag upstream —
  re-run with `--refresh` if a sample looks out of date.
- **Scenario ids** are stable within a cached corpus and **case-insensitive** —
  `GALLERY-TABVIEW-1` resolves the same as `gallery-tabview-1`. Gallery/Toolkit/Reactor ids
  look like `gallery-<control>-<n>` / `toolkit-<control>-<n>` /
  `reactor-<control>-<n>`; the `<source>-` prefix disambiguates controls that
  exist in more than one gallery (e.g. `ColorPicker`). Curated **core** patterns
  use a plain descriptive id with **no** `<source>-<control>-<n>` shape (e.g.
  `file-picker-desktop`, `live-charts`); fetch them the same way with `--id`, and
  browse them with `--source core`.
- **`--json` is always JSON.** With `--json`, every failure — including argument/parser
  errors such as a non-integer `--max` — is emitted as a flat `{"error": "..."}` object
  on stdout with a non-zero exit code, so an agent can parse the result on every path.
- **Exit codes** are script-friendly: `0` on a hit, `1` on no match / error.
- Keep queries **focused** (one feature per query) — the lexical ranker rewards
  specific phrasing. Batch multiple focused queries rather than one broad one.

## Related skills

- **winapp-ui-automation** — inspect and drive a *running* app's UI tree (a
  different job from discovering controls to write).

## CLI reference

Run `winapp find-ui --help` for current command options, or `winapp --cli-schema`
for the complete machine-readable command schema.
