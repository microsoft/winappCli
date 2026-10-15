# App benchmark (local)

Measures how well GitHub Copilot **builds complete Windows apps** with and without this repo's
plugins. Each trial gives an agent a realistic request ("build a markdown editor with tabs…"),
lets it work in a clean environment, then the harness builds, launches, inspects and scores the
app it produced.

It complements [`benchmarks/agents`](../agents/README.md):

| | `benchmarks/agents` | `benchmarks/apps` (this) |
|---|---|---|
| Question | Which skills load for a request, and does the answer solve it? | Does the agent ship a working, well-built app? |
| Session | Short, 1 request | Full build (5–60 min), then a judge session |
| Scored on | Expected capabilities, rubric | Gates, scenario requirements, quality checks, design |
| Cost | Cents per session | ~20–450 AI credits per trial (model dependent) |
| Runtime | PowerShell, sequential | Node (TypeScript), parallel, with a terminal dashboard |

## Setups

| Setup | Plugins | Use |
|---|---|---|
| `none` | none, and `winapp` hidden from PATH | No-skills baseline |
| `winappcli` | `plugins/winapp` + `plugins/winui/agent-plugin` from `microsoft/winappCli` at a **ref you pick**: `main` (default), a branch, tag, SHA, or `pull/N/head` | `winappcli@main` for the current state; `winappcli@my-branch` for your change. Each ref is its own setup in the results |
| `published` | both plugins exactly as the published catalog (`microsoft/win-dev-skills`) pins them | What users get today |

A typical comparison is `none`, `published`, `winappcli@main` and `winappcli@<your branch>`. Push
the branch first; the harness clones it, and pins every ref to one commit for the whole run. In the
dashboard, the setup list is `none`, `published`, `winappcli main`, and the repo's open pull
requests; select any to compare. For another branch, a tag or a SHA, use headless runs
(`--agent winappcli@my-branch`).

All setups load plugins the way a marketplace install does (SDK `pluginDirectories`, default
agent). Before the prompt is sent, each trial checks that every plugin skill is loaded from the
expected folder; if not, it fails without spending credits.

## Before you run it

- **Machine:** Windows 10 1903+ with Developer Mode and long paths enabled (`LongPathsEnabled=1`), Node.js 20+, .NET SDK 10+, the WinApp CLI,
  and GitHub Copilot CLI signed in (`copilot login`). Use a **dedicated machine or VM**: trials
  launch apps, take focus for UI automation, and run for hours.
- **Cost:** a trial is one build session plus one judge session. Rough averages from a
  270-trial run: Sonnet 5.5 ~70–100 credits, GPT-6.1 Sol ~100–160, Opus 5.5 ~375–420 per trial;
  5–25 minutes each.
- **Results live outside the repo** in `%LOCALAPPDATA%\winapp-benchmarks\apps\results\runN\`
  (override with `BENCH_RESULTS_DIR`). Inside the checkout, trials would inherit this repo's
  `AGENTS.md`, `nuget.config` and `global.json`.

```powershell
cd benchmarks\apps
npm ci
npm run doctor        # prerequisites, PATH health, stray project files
npm start             # terminal dashboard (setup wizard)
```

In the dashboard's scenario, setup and model lists: Space/Enter toggles, `A` selects all,
`D`/Tab/→ (or the **Continue →** row at the top) moves on, and ← goes back.

## Headless runs

```powershell
# One trial
npm run bench -- --scenario calc --agent winappcli

# Compare your branch with main, the published plugins and no plugins (3 iterations)
npm run bench -- --scenario calc,counter-winui,text-editor-winui --agent "none,published,winappcli@main,winappcli@my-branch" --iterations 3 --concurrency 3

# A pull request (also works for forks)
npm run bench -- --scenario calc --agent "winappcli@main,winappcli@pull/1234/head"

# Preview without running
npm run bench -- --scenario all --agent all --dry-run

# Summary with confidence intervals and paired comparisons
npm run scorecard -- run3 --markdown scorecard.md
```

| Flag | Default | Notes |
|---|---|---|
| `--scenario a,b` / `all` | | Scenario names (frontmatter `name`) |
| `--agent a,b` / `all` | | Setup folders under `agents/`. `name@ref` picks a ref for a `from_repo` setup; repeat the name to compare refs |
| `--model m` | `claude-sonnet-5.5` | Build model (one per run; run separate sweeps per model, or use `--run-meta`) |
| `--iterations n` | 1 | |
| `--concurrency n` | 1 | 3–4 is fine on a 16-core machine |
| `--max-build-minutes n` | 60 | Per build session |
| `--upstream-ref r1,r2` | `default_ref` | Same as `winappcli@r1,winappcli@r2` for every `from_repo` setup without an explicit ref (catalog setups keep their default) |
| `--judge-model m` | `judge.model` in `common/config.json` | Fixed grader for every trial |
| `--skip-validation` | off | Build + launch only, no judge |
| `--run-meta file.json` | | Explicit task list (mix models, setups, refs) |
| `--resume-run runN` | | Run queued/interrupted trials of a run |
| `--revalidate-run runN --revalidate-trial <folder>` | | Rebuild, relaunch and re-judge an existing app; no new build session |
| `--dry-run` | | Print the task list |

Rescore a trial offline after a scoring change: `npx tsx src/rescore.ts --run runN --trial <folder>`.

## How a trial works

1. **Environment.** The trial gets:
   - a fresh `DOTNET_CLI_HOME` (no preinstalled templates, and
     `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`);
   - `DOTNET_ROOT` matching the `dotnet` on PATH;
   - an isolated Copilot config (your `config.json` only);
   - `env.hide_tools` applied, which is how `none` loses `winapp`.
2. **Build session.** The agent works in `results\runN\<trial>\app`. Events stream to
   `session-logs-dir\build-events.jsonl`. The session ends when the agent stops, goes silent, or
   hits the wall-clock limit.
3. **Harness build and gates.** `dotnet build` (Debug), then:
   - **G0**, the right framework;
   - **G3**, Native AOT, for scenarios with `gates: [native_aot]`: a harness-run
     `winapp run --aot -c Release` must publish without trim/AOT warnings and show a window;
   - launch with `winapp run`.
4. **Quality checks.** Source and build checks, plus UI Automation checks on the running app:
   accessible names and keyboard traversal.
5. **Judge.** A validation session on the fixed judge model writes and runs a UI test script for
   the scenario requirements, then scores design.
6. **Cleanup.** Removes the trial environment and any trial folders that leaked into the user
   PATH, unregisters development packages the trial registered, and records a `machine` block
   in `results.json`.

## Scoring (v2)

A failed gate (build, launch, G0, G3) scores **0** and skips the judge. Otherwise:

**score = 60 × requirements + 30 × quality checks + 10 × design** (each part is its pass rate;
parts that don't apply are dropped and the rest renormalized).

| Part | Checked by | Details |
|---|---|---|
| Requirements (60) | Judge | Each scenario requirement, tested with a UI script and screenshots. Unreported requirements fail. |
| Quality checks (30) | Harness | P2 manifest · P3 packaged · P6 no WPF/WinForms/UWP leftovers · V1 custom title bar · V2 Mica/Acrylic · V3 typography via styles · V5 no hard-coded hex colors in pages · V9 no legacy Win32/WinForms UI · Q3 `x:Bind` over `{Binding}` · Q4 no build warnings (C#, XAML, WinUI analyzer, trim/AOT) · Q0 accessible names (UIA) · Q1 Tab moves focus (UIA) |
| | Judge | Q5 usable empty/loading/error states |
| Design (10) | Judge | 0–10 relative to an unmodified template |

`results.json` has `metrics.score_breakdown`, `metrics.quality_checks`, `metrics.requirements`,
`judge_model`, `models_used` (billed models; `model_mismatch` when they differ from the request),
`agent_source` (setup, ref, SHA, plugin versions), and `toolchain.winapp`.

**Comparing setups:** use the scorecard. It pairs trials on model + scenario + iteration and
reports the mean difference with a 95% CI, wins/ties/losses, and credit and time deltas. In a
270-trial run the paired score difference had an SD of about 20 points, so 30 pairs per model
resolve roughly ±8 points and 90 pairs (3 models) about ±4.

## Scenarios

`scenarios/<name>/scenario.md`: YAML frontmatter plus the prompt the agent receives.

| Field | Description |
|---|---|
| `name`, `description` | Identifier and summary |
| `type` | `new`, `convert`, or `improve` |
| `app_name` | App name for the package identity |
| `framework` | Expected UI framework (default `winui`); drives G0 |
| `gates` | Extra gates, e.g. `[native_aot]` |
| `requirements` | Checked by the judge. Write what the prompt states or implies, or what anyone would expect; no hidden specifics; don't restate the default quality checks |
| `test_notes` | Notes for the judge only |
| `test_assets` | Files for testing; `include_in_build: true` also shares them with the builder. Paths may use `{scenario_dir}`, `{repo_root}` and `%ENV%` |
| `original_app` | Source app for convert/improve scenarios |

Some scenarios need extra setup:
- `local-llm-chat` expects a Foundry Local model under `%USERPROFILE%\.foundry`.
- `imageresizer-wpf-to-winui` expects a PowerToys checkout next to this repo.

## Setup configs

`agents/<name>/config.json`; put experiments in git-ignored `agents/.local/<name>/`.

```jsonc
{
  "description": "Shown in the dashboard",
  // Plugins from a GitHub ref (default_ref, or pick refs per run); pinned per run in results\runN\upstream-pins.json
  "from_repo": { "repo": "microsoft/winappCli", "default_ref": "main", "plugins": ["plugins/winapp"] },
  // or: plugins as a marketplace catalog pins them
  "from_repo": { "repo": "microsoft/win-dev-skills", "default_ref": "main", "catalog": ".github/plugin/marketplace.json", "plugins": ["winappcli", "winui"] },
  // or: plugins straight from this checkout, including uncommitted edits (handy in agents/.local/ for quick local iteration)
  "from_local": { "plugins": ["plugins/winapp", "plugins/winui/agent-plugin"], "agent": "optional-agent-name" },
  "env": {
    "hide_tools": ["winapp"],          // drop these from the trial PATH (other tools in the same folders stay reachable)
    "path_prepend": [],                // extra PATH entries
    "isolate_dotnet_home": true        // fresh DOTNET_CLI_HOME per trial (default)
  },
  "framework_hint": "WinUI 3",         // appended to the prompt when missing
  "prechecks": { "required_on_path": [{ "tool": "dotnet" }] }
}
```

Older options (`sections`, `skills`, `preset_scripts`, `agent_from_package`, custom
`build_command`/`launch_command`) still work but none of the setups here use them.

## Keeping the machine clean

A run must not change the machine outside the results folder:
- Preflight refuses to start when the user PATH is over 4,000 characters, or user + machine over
  16,000. It also removes trial folders left in the PATH.
- Every trial and revalidation undoes its own PATH entries and development package registrations.
- The queue keeps the system awake while it runs. Trials that span a sleep or host suspend are set
  aside (`results.suspended.json`) and retried.

Any process that points `DOTNET_CLI_HOME` at a new folder must also set
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`. Otherwise the .NET SDK permanently adds that folder to
the user PATH in the registry. Hundreds of trials doing this once pushed the PATH past Windows'
32,767-character limit and broke sign-in. `src/runner/machine-guard.test.ts` guards it.

Agents run as you, so they can still make global changes the harness can't undo (for example
`winget install`). Check `machine.env_changes` in `results.json` after large runs.

## Dashboard keys

`1`–`5` switch views (live, progress, results, charts, summary). `R` reruns and `V` revalidates
selected trials. `H` writes a self-contained HTML report into the run folder. `O` opens the run
folder. `Q` quits.

## Layout

```
benchmarks/apps/
  agents/        setup configs (none, winappcli, published)
  common/        judge prompt, UI test harness, judge model
  scenarios/     one folder per scenario
  scripts/       Test-Prerequisites.ps1 (npm run doctor)
  src/           harness (runner/), dashboard views, headless.ts, scorecard.ts, rescore.ts
  docs/          design notes and open proposals
```

Development: `npm run typecheck` and `npm test` (unit tests in `src/runner/*.test.ts`).

## Further reading

- [docs/handoff.md](docs/handoff.md): status, known issues, baseline results, and what's next
- [docs/proposals/performance.md](docs/proposals/performance.md): measuring app performance with `winapp perf`
- [docs/proposals/theme-and-identity.md](docs/proposals/theme-and-identity.md): theme correctness and app identity checks
