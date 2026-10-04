# Agent plugin benchmark (local)

Measures which skills GitHub Copilot CLI loads for realistic developer requests, and how many
tokens it uses, under different plugin configurations:

| Configuration | Plugins installed |
|---|---|
| `none` | no plugins |
| `winapp` | this repo's `plugins/winapp` |
| `winui` | this repo's `plugins/winui/agent-plugin` |
| `both` | both plugins |

Use it to baseline the current plugins, check that a moved or restructured plugin still routes the
same way, and compare skill restructuring candidates.

Scenarios expect **capabilities** (`msix.sign`, `api.lookup`, ...), not skill names, so a plugin that
renames or merges skills can still be scored. They come in two sets:

| Set | Scenarios | Use |
|---|---|---|
| `dev` (default) | 67 | Iterate on descriptions and structure freely. Never cite it as proof. |
| `heldout` | 40, each with 2 paraphrases (120 prompts) | Release and decision checks only. See [Held-out set](#held-out-set). |

```powershell
pwsh benchmarks\agents\run.ps1 -Plan
```

## Prerequisites

- PowerShell 7.4 or later and `git`.
- Copilot CLI on `PATH`, authenticated through `GH_TOKEN` or `COPILOT_GITHUB_TOKEN` (each run uses
  an empty, isolated Copilot home, so a stored `copilot login` is not used).
- Pester 5 only to run the tests in `tests\`.

## Quick start

```powershell
# Print the expanded run list and session count without calling a model
pwsh benchmarks\agents\run.ps1 -Plan

# Recommended first step: a quick dev baseline (one model, one iteration: 172 sessions)
pwsh benchmarks\agents\run.ps1 -Model claude-sonnet-5.5 -Iterations 1

# Then the full dev matrix (3 models x 3 iterations: 1548 sessions)
pwsh benchmarks\agents\run.ps1

# Check scenario prompts for leaked skill vocabulary and unrealistic fixtures (no model calls)
pwsh benchmarks\agents\run.ps1 -Lint

# A single scenario
pwsh benchmarks\agents\run.ps1 -Scenario electron-notifications -Configuration winapp -Model claude-sonnet-5.5 -Iterations 1

# Compare against the published WinUI plugin (microsoft/win-dev-skills v0.7.1)
pwsh benchmarks\agents\run.ps1 -Configuration winui,both -WinUIPlugin published

# Compare a candidate WinUI plugin from a local folder
pwsh benchmarks\agents\run.ps1 -Configuration winui,both -WinUIPlugin C:\src\my-winui-candidate\agent-plugin
```

| Parameter | Default (`config.json`) | Notes |
|---|---|---|
| `-Set <dev\|heldout\|all>` | `dev` | Which scenario set to run. `-Lint` checks every set unless `-Set` is given |
| `-Scenario <id[]>` | all in the set | Scenario ids; a base id selects its paraphrases too |
| `-Variant <name[]>` | all | Paraphrase filter: `base`, `novice`, `terse` |
| `-Configuration <none\|winapp\|winui\|both[]>` | each scenario's list | Filters a scenario's `configurations` |
| `-Model <id[]>` | `claude-sonnet-5.5`, `claude-opus-5.5`, `gpt-6.1-sol` | Any model your Copilot account can use |
| `-Iterations <n>` | 3 | |
| `-TimeoutMinutes <n>` | scenario `timeoutMinutes`, else 5 | Per agent session |
| `-WinAppPlugin <path>` | `plugins\winapp` | Any local plugin folder |
| `-WinUIPlugin <path\|published>` | `plugins\winui\agent-plugin` | Any local plugin folder, or `published` for `win-dev-skills@v0.7.1:plugins/winui/agent-plugin` (fetched once into `results\.cache`) |
| `-Agent <name>` | none | Runs every session with `--agent <name>`, e.g. `winappcli:winapp` or `winui:winui-dev` (plugin agents are namespaced) |
| `-MaxCredits <n>` | none | Stops launching sessions once this invocation has spent `n` AI credits |
| `-CopilotVersion <v>` | newest Copilot CLI build already on the machine | Pinned with `--prefer-version` for every call |
| `-OutDir <path>` | `results\<timestamp>` | |
| `-KeepArtifacts` | off | Keeps each run's Copilot home, workspace, and logs under `%TEMP%\winapp-agent-bench` |
| `-Plan` | | Dry run; shows sessions per cohort |
| `-Lint` | | Leakage and fixture lint of the scenarios; exits 1 on errors. See [Scenario lint](#scenario-lint) |
| `-Rescore <resultsDir>` | | Re-evaluate recorded runs against the current scenario expectations; no model calls |
| `-Compare <dir[]> -Candidate <dir[]>` | | Markdown comparison of two sets of result folders; no model calls. `-Scenario`, `-Configuration`, and `-Model` filter it, and `-OutDir` writes `comparison.md` there instead of printing it |

Runs are sequential; parallel runs are a possible future addition. List parameters accept
comma-separated values (`-Scenario a,b`), including through `pwsh -File`.

## What each run does

1. Creates an empty Copilot home and a workspace under `%TEMP%` (outside any git repo) and copies
   the scenario's fixture into the workspace. Inherited `COPILOT_*` variables (other than
   `COPILOT_GITHUB_TOKEN`) are removed, and auto-update is disabled.
2. Installs the configuration's plugins into that home with `copilot plugin install <path>`.
   Copilot prints a deprecation warning for direct installs; it is expected.
3. Preflight: `copilot skill list --json` must show exactly the configuration's plugin skills (plus
   Copilot's built-in skills). Otherwise the run is `preflight_failed`.
4. Runs `copilot -p "<prompt>" --model <id> --output-format json` with shell, file writes, and URL
   access denied, built-in MCP servers disabled, and custom instructions off. The agent can read the
   fixture and load skills. Output streams straight to files.
5. Reads the session's persisted `events.jsonl` and records the skills the agent invoked, the size
   of the skill content delivered to the model, tokens, AI credits, turns, tool calls (including
   denied ones), `winapp` commands the agent tried to run or named in its final answer, the selected
   agent, duration, and exit status. Missing values are `null` with a reason, never 0. If the
   workspace changed, the run is a `harness_error`.
6. Maps the invoked skills to capabilities with `capabilities.json`, evaluates the scenario's
   expectations, appends a line to `runs.jsonl`, and deletes the temporary folders.

Nothing is installed into your own Copilot home.

## Reading results

Each invocation writes `results\<timestamp>\`:

- `summary.md`: totals (pass rate, tokens, credits, repeated deliveries, right command named
  without a skill), pass/partial/fail by set, cohort, and model, the no-plugin control, the `n/a`
  cells, and per scenario one row per configuration and model with pass rate, the most common set
  of loaded skills, median tokens, skill context, and duration, and repeated deliveries.
- `runs.jsonl`: one JSON object per run with all extracted fields.
- `run-info.json`: Copilot CLI version and path, models, and each plugin's path, version, git SHA,
  and skill list.

How to read the token columns:

- **Input** is the full prompt on every model turn, including cached tokens. Most of it is Copilot's
  own system prompt, tool definitions, and conversation, so it moves only a little when skills change.
  **Cache read** is the cached part of input.
- **Skill context** is the skill content delivered to the model (characters / 4). It is an
  approximation, not a tokenizer count, but it is the number that changes when a skill grows,
  shrinks, or stops loading.
- **Repeated deliveries** count the times a skill already delivered in a session was delivered
  again, and the skill context those repeats added. They show in the totals and in each row.

Run statuses:

| Status | Meaning |
|---|---|
| `pass` | A primary capability (or every capability of a primary group) loaded, nothing forbidden loaded, and `maxSkills` held |
| `partial` | Only part of a primary group, or only acceptable capabilities, loaded. Counts as scored but not passed |
| `fail` | A forbidden capability loaded, `maxSkills` was exceeded, or no expected capability loaded |
| `n/a` | The run completed, but no primary capability and no forbidden capability is installed in this configuration, and there is no `maxSkills`. Left out of pass rates and listed under **Not applicable** in `summary.md` |
| `timeout` | The session exceeded its timeout and was killed |
| `preflight_failed` | Plugin install failed or the installed skill set did not match the configuration |
| `harness_error` | Copilot exited with an error, no session log was found, or the workspace was modified |
| `cleanup_failed` | The run's temporary folders could not be deleted (`reason` keeps the original status) |

Pass rates are pass / (pass + partial + fail). The headline pass rate leaves out:

- the `none` configuration: a no-plugin control that shows what models do unaided (which `winapp`
  commands they name, and what it costs). It has its own table.
- the `explicit-command` cohort, where the prompt names the command. It has its own line.

Every scenario has a **cohort**, reported separately: `implicit` (a goal or feature ask), `error`
(starts from an error or symptom), `vague` (under-specified), `explicit-command`, `near-miss` (needs
no plugin), `trap` (the context baits a wrong capability), and `followup` (reserved for two-turn
scenarios).

**Right command, no skill** counts failed or partial runs where the agent still named the right
`winapp` command (from the scenario's `commands`, or the primary capabilities' command patterns in
`capabilities.json`). It separates "skipped the skill but knew the command" from "knew nothing".
Runs recorded before commands were captured show `n/a` for it.

### Rescoring after changing expectations

When you change a scenario's `expect` block, re-evaluate an earlier run instead of paying for new
sessions:

```powershell
pwsh benchmarks\agents\run.ps1 -Rescore benchmarks\agents\results\<timestamp>
```

This writes `runs.rescored.jsonl` and `summary.rescored.md` next to the originals, which are left
unchanged. Only `pass`, `partial`, `fail`, and `n/a` runs are re-evaluated. Each rescored run keeps
its `originalStatus`, and the command prints how many runs moved between statuses. Runs record a
hash of their prompt; a run whose prompt has changed since is not rescored and gets a note. A
changed fixture still needs a new run.

### Comparing runs

```powershell
pwsh benchmarks\agents\run.ps1 -Compare results\baseline-a,results\baseline-b -Candidate results\candidate -OutDir results\compare
```

`comparison.md` has one row per model and one per (model, scenario, configuration) cell with the
pass rate, mean skill context, mean input tokens, and mean AI credits per run on each side, the
change, and repeated deliveries. Only cells present on both sides are compared. Both sides are
re-evaluated in memory against the current scenario expectations; the folders are not modified.

- `n/a` runs are left out of pass rates; `partial` runs count as scored but not passed.
- The per-model rows leave out the `none` control and explicit-command scenarios. A **By set and
  cohort** table follows.
- A cell shows `check differs` when an expected skill is installed on one side only, for example
  when a candidate adds a skill to a plugin. It then tests something different on each side, so
  it is left out of the per-model pass rate.
- Runs recorded before repeated deliveries were measured show `-` for them.

## Recommended workflow for a plugin change

1. Screen with one model: `-Model claude-sonnet-5.5 -Iterations 1` against the candidate plugin
   (`-WinAppPlugin` or `-WinUIPlugin`), then `-Compare` it with a baseline of the same scope.
2. Confirm with all three models x 3 iterations on the scenarios the change affects plus the
   near-miss and trap cohorts, and compare again.
3. Before shipping, run the held-out set once on both sides (`-Set heldout`) and compare. Ship only
   if the held-out set improves or holds.
4. Set `-MaxCredits` on long runs. To measure a plugin agent, pass `-Agent` on both sides.
5. If the change renames, merges, or splits skills, add a map for the new skill set to
   `capabilities.json` first (see [Capabilities](#capabilities)).

With 3 iterations per cell, only a 0/3 versus 3/3 swing in a single cell is a signal; the per-model
rows are more reliable.

## Capabilities

`capabilities.json` defines each capability in plain language, with the `winapp` command patterns
that serve it, and maps each plugin's skills to capabilities:

```json
{ "id": "winapp-current", "plugin": "winapp", "skillSetHash": "4a4abc8b6541",
  "skills": { "winapp-signing": ["msix.sign"], "winapp-sandbox": ["sandbox.run", "ui.automate"] } }
```

A map applies to a run when every skill it names is installed; if several maps of one plugin apply,
the one naming the most skills wins. `skillSetHash` identifies the skill-name set the map was written
for. `run.ps1` warns when no map matches a plugin's current skills, and the tests fail when the repo
plugins change their skill names without a map update. For a candidate that renames, merges, or
splits skills, add a map for its skill set; the existing maps keep scoring older results.

Capabilities starting with `winui.` (and `ui.samples`) are WinUI-specific, so non-WinUI scenarios
forbid `winui.*`.

## Adding a scenario

Create `scenarios\<id>\scenario.json` (the id must match the folder name) and a `fixture\` folder
with the few files the prompt needs:

```json
{
  "id": "msix-publisher-mismatch-sign",
  "description": "Error-first: SignerSign 0x8007000b because the manifest Publisher does not match the certificate subject.",
  "set": "dev",
  "cohort": "error",
  "prompt": "sign.ps1 started failing after IT gave us the new cert: 'SignTool Error: ... (-2147024885/0x8007000b)'. Same command as before.",
  "configurations": ["winapp", "both"],
  "fixture": "fixture",
  "expect": {
    "capabilities": {
      "primary": ["msix.sign", "troubleshoot"],
      "acceptable": ["msix.manifest"],
      "forbid": ["winui.*", "ui.samples", "session.report"],
      "budgetTokens": 12000
    },
    "commands": ["^sign$", "^cert"],
    "maxSkills": 3
  }
}
```

- Write the prompt the way a developer would ask for the work: often an error, a symptom, or a vague
  goal. Never name a skill, and avoid the wording of skill descriptions (`-Lint` checks).
- `configurations` must include `both`, because most users install both plugins.
- `primary`: alternatives; each is a capability or an array of capabilities that must all load
  (`[["sandbox.run", "ui.automate"]]`). `acceptable`: partial credit. `forbid`: capability patterns
  (`winui.*`, or `*` for "nothing should load"). `maxSkills`: upper bound on distinct skills.
  `budgetTokens` (optional): runs whose skill context exceeds it are counted as over budget; it
  does not change the status. `commands` (optional): regexes over the recorded `winapp` commands;
  defaults to the primary capabilities' patterns.
- Near-misses and traps with nothing to load use `"primary": [], "forbid": ["*"], "maxSkills": 0`.
- `paraphrases` (optional): `{ "novice": "...", "terse": "..." }`. Each runs as `<id>.<name>` with the
  same fixture and expectations.
- `routingSnapshot: true` marks a scenario whose prompt refers to things the fixture cannot contain
  (a built MSIX, a running app). It only measures routing.
- `leakAllow`: phrases a prompt may share with a skill description because they are quoted from a
  real error message.
- The older `skillsAny` / `skillsAll` / `skillsForbid` format still loads and scores, so old result
  folders can be rescored, but new scenarios use capabilities.
- Run `pwsh benchmarks\agents\run.ps1 -Lint` and `Invoke-Pester benchmarks\agents\tests`.

## Scenario lint

`-Lint` compares each prompt, plus its fixture file names, with every skill and agent description of
the plugins being run:

| Rule | Held-out | Dev |
|---|---|---|
| `leak-bigram`: shares a distinctive two-word phrase (one that appears in at most 3 descriptions) | error | warning |
| `leak-jaccard`: content-word overlap with one description above 0.10 | error | warning |
| `fixed-name`, `names-tooling`: uses "Contoso", or names winapp, a skill, or a plugin | error | - |
| `fixture-empty`: the prompt names an empty fixture file (unless `routingSnapshot`) | error | error |
| `fixture-missing`: the prompt names a file the fixture lacks (unless `routingSnapshot`) | warning | warning |

Framework, product, and generic project words ("WinUI app", "Windows desktop", "Microsoft Store",
"MSIX") are not counted as leakage: a developer naming their own stack is not echoing a description.

## Held-out set

`scenarios\heldout\` holds 40 scenarios, each with a `novice` and a `terse` paraphrase. They were
written by a different model family (GPT-6.1 Sol) than the one that wrote the dev scenarios, from public developer
reports (GitHub issues, Stack Overflow, Microsoft Learn), personas, and plain capability definitions,
without seeing any skill or agent description. Fixtures use randomized names. Every capability is a
primary expectation at least twice, and near-misses and traps are over 20% of the set. Every
held-out scenario also runs in the `none` configuration as a control.

Treat it as gated:

- Run it for release and decision checks (`-Set heldout`), not while iterating on descriptions.
- Do not edit held-out prompts to make a candidate pass, and do not copy their wording into skill
  descriptions. If a held-out scenario is wrong, fix its expectation and say so in the change.
- A description change ships only if it improves or holds the held-out results.

## Limits

- Shell, file writes, and URLs are denied, so this measures routing and skill loading, not whether
  the agent completes the task. Models still try those tools; the attempts show up as denied calls.
- Local only, Copilot CLI only, no CI integration, sequential runs.
- Results vary between runs; use several iterations before drawing conclusions.
