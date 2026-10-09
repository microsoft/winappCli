# Demand scenario writer spec (read fully before starting)

You are hand-writing benchmark scenarios: realistic requests that Windows desktop app developers send to an AI coding
assistant, each with a frozen grading rubric. Every scenario comes from a REAL problem mined from GitHub issues or
Stack Overflow. An earlier attempt was discarded because it was script-generated. Do not repeat its mistakes:
- an `error.log` that literally stated the root cause;
- fixtures that didn't contain the evidence the rubric relied on;
- boilerplate must_include text such as "Identifies the fixture-specific root cause: …";
- rubrics copied from one template.

## Hard isolation rules (critical for experimental validity)
- Do NOT read, list, or search any `plugins\` folder, anything under `benchmarks\agents\` EXCEPT
  `benchmarks\agents\scenarios\demand\` (your output), any `SKILL.md` or `*.agent.md` file, or any working folder
  other than the `demand-work\` folder you were given. You must not learn how any AI skill or plugin organizes this
  domain.
- Do not modify git state (no commit, checkout, reset, or stash). Do not push.
- You MAY read the repo's `docs\` and `src\` to verify facts about the winapp CLI (Microsoft's Windows app development
  CLI). Use Microsoft Learn and other official docs (web_fetch) for everything else.
- **Write every scenario by hand**, one at a time. Do NOT write scripts or templates that generate scenarios, fixtures,
  or rubrics. A small script that only validates JSON is fine.

## Inputs
`demand-work\problems.jsonl` (300 problems with `cluster_id`, `url`, `title`, `problem`, `framework`,
`resolution`) and `clusters.md`. Cluster assignments were made heuristically and can be wrong. Read the problem before
using it, and only use it for a cluster it truly belongs to. Open the source (`gh issue view <url> --comments`, or
web_fetch for Stack Overflow) for every problem you use. You need the real error text, configuration, and resolution.

## Choosing problems
Pick problems that:
- concern Windows desktop development today (skip macOS/Linux-only, Windows 8/Phone-era, and pure framework-internal
  bugs with no developer-side fix);
- an assistant that can read project files but cannot run commands can meaningfully answer;
- have a known resolution or well-documented correct approach;
- are diverse in framework and phrasing.

When the real resolution is "upstream bug, use workaround X", that's fine: the rubric rewards the workaround and
recognizing the bug.

## Output per scenario: `benchmarks\agents\scenarios\demand\<id>\`
`<id>`: kebab-case, starts with the cluster id, unique and descriptive (`c07-signtool-0x8007000b-new-cert`).

### scenario.json (exact shape)
```json
{
  "id": "<id>",
  "description": "One sentence: the real problem and its source URL.",
  "set": "demand",
  "cohort": "error | implicit | vague | near-miss | trap | explicit-command",
  "prompt": "...",
  "configurations": ["none", "winapp", "winui", "both"],
  "fixture": "fixture",
  "expect": { "capabilities": { "primary": [], "acceptable": [], "forbid": [] } }
}
```

Cohorts:
- `error`: starts from an error or symptom.
- `implicit`: a concrete goal.
- `vague`: an under-specified goal.
- `near-miss`: Windows-flavored, but needs no Windows-specific tooling.
- `trap`: context baits a wrong approach.
- `explicit-command`: ONLY when the source user explicitly used the `winapp` CLI, and then the prompt may name it.

No other prompt may contain the words "winapp", "skill", or "plugin". Aim for a natural mix: errors are common but not
everything. Include some goal-oriented and vague asks.

### prompt
The developer's own first-person words. Vary the tone: terse, chatty, novice, or a pasted error. When the source had an
exact error message, paste the real error text (shortened if long). Use the developer's real versions where they
matter. Use randomized product, company, and folder names (never Contoso or Fabrikam). Don't put the answer in the
prompt. Name a tool only if the source developer used it. Keep it to 1–4 sentences plus an optional pasted error.

### fixture\
2–5 small, realistic, non-empty files that contain the evidence a good engineer would look at. If the root cause is a
wrong property, the `.csproj` has that wrong property. If it's a manifest mismatch, the manifest shows it. If it's a CI
problem, the workflow yaml shows it. Logs contain realistic output (the real error lines), NOT explanations. Every file
the prompt mentions exists. No binaries: describe a `.pfx` or `.msix` in the prompt or a text file. Realistic file
names (`build.log`, `MainWindow.xaml.cs`, `electron-builder.yml`, `tauri.conf.json`, …).

### rubric.json
```json
{
  "cluster": "<cluster id>",
  "framework": "WinUI3 | WPF | WinForms | Electron | Tauri | Flutter | MAUI | C++/Win32 | Rust | .NET console | other",
  "goal": "What a successful answer achieves for this developer (one sentence).",
  "must_include": ["2–5 specific, checkable points"],
  "must_not": ["1–4 specific wrong or unsafe advice"],
  "acceptable_alternatives": ["specific alternative approaches/tools that also count"],
  "solved": "Specific definition for THIS scenario.",
  "partial": "Specific definition for THIS scenario.",
  "sources": ["source URL(s)"],
  "verified_with": ["official doc URLs or repo paths checked for each fact"],
  "notes": "Version caveats; anything the grader must know."
}
```

**must_include** points are specific and checkable, for example: "Says the manifest `Identity/@Publisher` must equal
the certificate Subject exactly (`CN=…`)", or "Removes `<StartupObject>` (or sets `<EnableDefaultApplicationDefinition>`
back) so MAUI's generated entry point is used". Write them solution-agnostic where several correct paths exist.

**must_not** points are specific mistakes a plausible but wrong answer would make for THIS problem (e.g. "Suggests
`Add-AppxPackage -AllowUnsigned` for a production fix", "Tells them to port the WPF app to WinUI to get Mica"). One
generic safety item is OK only if it is relevant.

**acceptable_alternatives** name concrete alternatives (e.g. "signtool sign /fd SHA256 …", "MSBuild
`/p:AppxPackageSigningEnabled=true`", "Visual Studio Package and Publish wizard", "electron-builder `win.signtoolOptions`",
"the winapp CLI's equivalent command"). Never require a specific tool when another correct path exists. A correct
answer that does not use the winapp CLI must be able to reach "solved". Only explicit-command scenarios may require
winapp.

**Verify** every fact in must_include and must_not against official docs (Microsoft Learn, framework docs) or, for
winapp CLI behavior, against `docs\` or `src\winapp-CLI\`. Record what you checked in `verified_with`. Drop or soften
anything you can't verify. Don't rely on facts the files and prompt don't reveal, unless the right move is to ask for
them; then must_include says exactly what to ask.

## Before you finish
- **Re-read every file you wrote.** JSON is valid, the id matches the folder name, prompt-referenced files exist,
  nothing is empty, no log states the root cause, and no prompt says winapp/skill/plugin (except explicit-command).
- **Append to `demand-work\sampling.md`** one line per scenario: `| <id> | <cluster> | <problem id(s)> | <source URL> | <cohort> | <framework> |`.
  Create the file with a header if it doesn't exist; other writers append too.
- **Reply** with your scenario ids, cohort and framework counts, and any problems you rejected and why.
