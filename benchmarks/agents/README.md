# Agent plugin benchmark (local)

Measures which skills GitHub Copilot CLI loads for realistic developer requests, how many tokens it
uses, and (for the demand set) whether the answer solves the developer's problem, under different
plugin configurations:

| Configuration | Plugins installed |
|---|---|
| `none` | no plugins |
| `winapp` | this repo's `plugins/winapp` |
| `winui` | this repo's `plugins/winui/agent-plugin` |
| `both` | both plugins |

Use it to baseline the current plugins, check that a moved or restructured plugin still routes the
same way, and compare skill restructuring candidates.

Scenarios expect **capabilities** (`msix.sign`, `api.lookup`, ...), not skill names, so a plugin that
renames or merges skills can still be scored. They come in three sets:

| Set | Scenarios | Use |
|---|---|---|
| `dev` (default) | 74 | Iterate on descriptions and structure freely. Never cite it as proof. |
| `heldout` | 41, each with 2 paraphrases (123 prompts) | Release and decision checks only. See [Held-out set](#held-out-set). |
| `demand` | 60, sampled from real developer problems | Outcome checks: graded against a frozen rubric, not by which skill loaded. See [Demand set](#demand-set). |

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

# Recommended first step: a quick dev baseline (one model, one iteration: 190 sessions)
pwsh benchmarks\agents\run.ps1 -Model claude-sonnet-5.5 -Iterations 1

# Then the full dev matrix (3 models x 3 iterations: 1710 sessions)
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
| `-Set <dev\|heldout\|demand\|all>` | `dev` | Which scenario set to run. `-Lint` checks every set unless `-Set` is given |
| `-Scenario <id[]>` | all in the set | Scenario ids; a base id selects its paraphrases too |
| `-Variant <name[]>` | all | Paraphrase filter: `base`, `novice`, `terse` |
| `-Configuration <none\|winapp\|winui\|both[]>` | each scenario's list | Filters a scenario's `configurations` |
| `-Model <id[]>` | `claude-sonnet-5.5`, `claude-opus-5.5`, `gpt-6.1-sol` | Any model your Copilot account can use |
| `-Iterations <n>` | 3 | |
| `-TimeoutMinutes <n>` | scenario `timeoutMinutes`, else 5 | Per agent session |
| `-WinAppPlugin <path>` | `plugins\winapp` | Any local plugin folder |
| `-WinUIPlugin <path\|published>` | `plugins\winui\agent-plugin` | Any local plugin folder, or `published` for `win-dev-skills@v0.7.1:plugins/winui/agent-plugin` (fetched once into `results\.cache`) |
| `-Agent <name>` | none | Runs every session with `--agent <name>`, e.g. `winappcli:winapp` or `winui:winui-dev` (plugin agents are namespaced) |
| `-MaxCredits <n>` | none | Stops launching sessions once this invocation has spent `n` AI credits. A session that ran but reported no credits (for example a timeout) counts at the average of the measured sessions, or 35 before any is measured |
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
   of the skill content delivered to the model, the other files inside a skill folder it read (its
   references, as `<skill>/<path>`, with their approximate size, and separately the reads that were
   denied), tokens, AI credits, turns, tool calls (including
   denied ones), `winapp` commands the agent tried to run or named in its final answer (and, separately,
   the ones it tried in a shell call that was denied), the selected agent, duration, and exit status.
   Missing values are `null` with a reason, never 0. If the workspace changed, the run is a
   `harness_error`.
6. Maps the invoked skills and the references read to capabilities with `capabilities.json`,
   evaluates the scenario's
   expectations, scores the final response against the answer signals (see
   [Routing and answer](#routing-and-answer)), appends a line to `runs.jsonl`, and deletes the
   temporary folders.

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
- **Reference reads** (`skillFileTokensApprox` in `runs.jsonl`) is the size of the files inside skill
  folders the agent read on top of `SKILL.md` (characters / 4). Add it to skill context for the skill
  text a run actually loaded; a skill split into a smaller core plus references should lower the sum.
- **Repeated deliveries** count the times a skill already delivered in a session was delivered
  again, and the skill context those repeats added. They show in the totals and in each row. When a
  repeated skill's size is unknown, the tokens show as `unknown` or `partial`.

Run statuses (pass rates score `pass`, `partial`, and `fail`; every other status is listed as excluded,
for example `1/2 (50%), 1 partial; excluded: 1 timeout`):

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

The headline pass rate leaves out:

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

### Routing and answer

Every run gets two independent statuses:

- **Routing** (`status`): did the expected skill load? See the status table above.
- **Answer** (`answer`): does the final response name what a correct answer must name? It does not
  depend on which skills loaded, so it is also scored for the `none` control.

Answer signals live in `capabilities.json`, next to each capability:

```json
"msix.sign": { "answer": { "require": [["winapp (sign|az-sign|cert)\\b"]], "forbid": [] } }
```

`require` is a list of groups; each group needs one matching regex (case-insensitive) in the final
response. `forbid` regexes fail the answer. For a scenario:

| Answer | Meaning |
|---|---|
| `pass` | Every capability of a primary alternative met its signals |
| `partial` | Some signals of a primary or acceptable capability were met, but no full alternative |
| `blocked` | The response alone is `partial` or `fail`, but the `winapp` commands the agent tried in a denied shell call would have passed: it knew the command and didn't tell the user |
| `fail` | No signals were met (and every alternative could be checked), or a `forbid` pattern matched |
| `n/a` | No primary capability has signals, a miss can't be judged because another alternative has no signals, or the run recorded nothing to score |

Scenarios that need no plugin (`"primary": []`) pass when the response names no `winapp` command.
Scenarios that expect and forbid nothing (the [demand set](#demand-set)) are `n/a`; their rubric grades
the answer instead.

`blocked` exists because the benchmark denies the shell: an agent that runs the right command, gets
denied, and then answers "blocked" without repeating the command would otherwise look the same as an
agent that never knew the command. A blocked answer counts as scored but not passed, so it does not
change answer pass rates; it is reported as its own count. Only the command name is recorded, not its
arguments, so a signal that needs an argument (for example `--on sandbox`) can't be met by a tried
command. Runs recorded before denied commands were captured derive them: commands recorded for the run
that its final response doesn't name came from shell calls, and count when the run had a denied shell
call.

Signals are deliberately small and objective: the `winapp` command for capabilities that have one,
and a key term for a few that do not (`Microsoft.UI.Xaml` for a WinUI port, a `--version` check for
prerequisites, manifest extension elements). Capabilities whose correct answer depends on the
scenario (`winui.design`, `winui.review`, `winui.build`, `troubleshoot`, `framework.guidance`) have
no signals and score `n/a`.

Runs recorded before the final response was kept are scored from their recorded `winapp` commands
instead (`answerBasis: commands`). Those commands include ones the agent tried to run, have no
arguments, and contain no prose, so only signal groups made entirely of `winapp <command>` patterns
are checked; the rest are `n/a`. Runs recorded before commands were captured stay `n/a`.

`summary.md` shows routing and answer side by side per set, model, cohort, and primary capability,
plus a routing x answer table: routed and answered, routed and blocked, routed only, answered only,
blocked only, and neither. `-Compare` adds answer and answer-blocked columns and the same table for
each side.

### Rescoring after changing expectations

When you change a scenario's `expect` block, re-evaluate an earlier run instead of paying for new
sessions:

```powershell
pwsh benchmarks\agents\run.ps1 -Rescore benchmarks\agents\results\<timestamp>
```

This writes `runs.rescored.jsonl` and `summary.rescored.md` next to the originals, which are left
unchanged. Only `pass`, `partial`, `fail`, and `n/a` runs are re-evaluated. Each rescored run keeps
its `originalStatus`, and the command prints how many runs moved between statuses. Runs record a
hash of their prompt; a run whose prompt has changed since is not rescored and gets a note. Scored
runs (`pass`, `partial`, `fail`, `n/a`) of a scenario that no longer exists become `scenario_removed`,
so they are left out of pass rates like timeouts and errors already are. A
changed fixture still needs a new run.

### Comparing runs

```powershell
pwsh benchmarks\agents\run.ps1 -Compare results\baseline-a,results\baseline-b -Candidate results\candidate -OutDir results\compare
```

`comparison.md` has one row per model and one per (model, scenario, configuration) cell with the
pass rate, mean skill context, mean input tokens, mean AI credits, and mean reference reads per run
on each side, the change, and repeated deliveries. Only cells present on both sides are compared. Both sides are
re-evaluated in memory against the current scenario expectations; the folders are not modified.

- Pass rates use the same format as `summary.md`: `pass`, `partial`, and `fail` are scored (`partial`
  counts against the rate); every other status is listed as excluded. Runs of a scenario that no
  longer exists are excluded as `scenario_removed`, and runs recorded with a different prompt than the
  scenario has now as `prompt_changed`.
- The per-model rows leave out the `none` control and explicit-command scenarios. **By set and
  cohort** and **By primary capability** tables follow.
- A cell shows `check differs` when what its expectation checks differs between the sides: the
  installed primary, acceptable, and forbidden capabilities (or, for skill-name expectations, the
  installed expected and forbidden skills). For example, a candidate that adds or removes an expected
  capability. It then tests something different on each side, so it is left out of the per-model
  pass rate. Renaming or merging skills does not change the check when the capability map covers it.
- `-Scenario` and `-Model` must name values present in the compared results.
- Runs recorded before repeated deliveries were measured show `-` for them, and runs recorded before
  reference reads were tracked show `n/a` for those.

## Recommended workflow for a plugin change

1. Screen with one model: `-Model claude-sonnet-5.5 -Iterations 1` against the candidate plugin
   (`-WinAppPlugin` or `-WinUIPlugin`), then `-Compare` it with a baseline of the same scope.
2. Confirm with all three models x 3 iterations on the scenarios the change affects plus the
   near-miss and trap cohorts, and compare again.
3. Before shipping, run the held-out set once on both sides (`-Set heldout`) and compare. Ship only
   if the held-out set improves or holds. For a change to skill content or structure, also run the
   demand set on both sides and grade it (see [Demand set](#demand-set)); routing can move while
   outcomes don't, and the reverse.
4. Set `-MaxCredits` on long runs. To measure a plugin agent, pass `-Agent` on both sides.
5. If the change renames, merges, or splits skills, add a map for the new skill set to
   `capabilities.json` first (see [Capabilities](#capabilities)). When content moves from `SKILL.md`
   into a reference file, map the file so its capabilities count only when it's read.

With 3 iterations per cell, only a 0/3 versus 3/3 swing in a single cell is a signal; the per-model
rows are more reliable.

## Capabilities

`capabilities.json` defines each capability in plain language, with the `winapp` command patterns
that serve it, and maps each plugin's skills to capabilities:

```json
{ "id": "winapp-current", "plugin": "winapp", "skillSetHash": "ea21288d7949",
  "skills": { "winapp-signing": ["msix.sign"], "winapp-sandbox": ["sandbox.run", "ui.automate"] } }
```

A map applies to a run when every skill (and file, below) it names is installed; if several maps of
one plugin apply, the one naming the most keys wins. `skillSetHash` identifies the skill-name set the map was written
for. `run.ps1` warns when no map matches a plugin's current skills, and the tests fail when the repo
plugins change their skill names without a map update. For a candidate that renames, merges, or
splits skills, add a map for its skill set; the existing maps keep scoring older results.

A key can also name a file inside a skill, as `<skill>/<path>`:

```json
"skills": { "winapp-setup": ["project.setup"], "winapp-setup/references/new-winui-app.md": ["project.scaffold"] }
```

The file's capabilities count only in runs where the agent read that file, so content moved from
`SKILL.md` into a reference is credited only when the model actually opened it. Each run records the
files its plugins ship (`skillFilesInstalled`), and a map with file keys applies only when those
files are installed. So a candidate that splits a skill into new reference files keeps the same
skill names as the baseline but still gets its own map, while baseline runs keep the old one. If a
split only moves content into a file the baseline already ships, the two maps can't be told apart;
score each side from its own branch. Runs recorded before installed files were tracked use maps
without file keys. File keys don't count toward `maxSkills`, and a file whose skill carries a forbidden
capability is unusable, like its skill. When a
skill is removed, remove its map entry, any capability only it provided, and the scenarios that
expected that capability. Older runs that had it installed are then scored by the remaining map,
and runs of the removed scenarios are left out of pass rates (scored ones as `scenario_removed`).

Capabilities starting with `winui.` (and `ui.samples`) are WinUI-specific, so non-WinUI scenarios
forbid `winui.*`. Mapping choices that affect scores:

- `winui-packaging` provides `winui.package`, not `msix.package`, so loading it in a WPF, Electron,
  or other non-WinUI scenario fails the `winui.*` forbid.
- `winui-design` provides `api.lookup` (it documents checking APIs against the project), so it can
  satisfy API-lookup scenarios in WinUI projects.
- `winapp-sandbox` and `winui-ui-testing` both provide `sandbox.run` and `ui.automate`.
- `winui-devtools` provides only `winui.inspect`: looking inside a running WinUI app's XAML (resolved
  property values, bindings, the declaring XAML line, live edits, and review notes pinned to
  elements). It does not provide `ui.automate`, so loading it for a click, type, read-text, or
  screenshot task earns no routing credit, and scenarios where it is the wrong tool forbid
  `winui.inspect`. Loading it in a non-WinUI scenario fails the `winui.*` forbid.
- Only `winui-packaging` provides `store.publish`. In the `winapp` configuration a Store scenario is
  `n/a`; where it is installed, a run that loads only packaging guidance scores `partial`.

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
      "forbid": ["winui.*", "ui.samples"],
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
  (a built MSIX, a running app). The agent cannot complete the task there, so the run measures which
  skills load and what the answer names, not whether the task would succeed.
- `leakAllow`: phrases a prompt may share with a skill description because they are quoted from a
  real error message.
- The older `skillsAny` / `skillsAll` / `skillsForbid` format still loads and scores, so old result
  folders can be rescored, but new scenarios use capabilities.
- Run `pwsh benchmarks\agents\run.ps1 -Lint` and `Invoke-Pester benchmarks\agents\tests`.

## Scenario lint

`-Lint` compares each prompt, plus its fixture file names, with every skill and agent description of
the plugins being run:

| Rule | Held-out and demand | Dev |
|---|---|---|
| `leak-bigram`: shares a distinctive two-word phrase (one that appears in at most 3 descriptions) | error | warning |
| `leak-jaccard`: content-word overlap with one description above 0.10 | error | warning |
| `fixed-name`, `names-tooling`: uses "Contoso", or names winapp, a skill, or a plugin | error | - |
| `fixture-empty`: the prompt names an empty fixture file (unless `routingSnapshot`) | error | error |
| `fixture-missing`: the prompt names a file the fixture lacks (unless `routingSnapshot`) | warning | warning |
| `leak-answer`: the prompt already contains an answer signal of its own primary or acceptable capabilities | error | warning |

The dev set keeps a few prompts that warn on purpose: explicit-command scenarios name the command,
and some older prompts stay unchanged so earlier results remain comparable. In the held-out and
demand sets, `explicit-command` scenarios may name winapp, and their leak findings are warnings. Framework, product, and generic project words ("WinUI app", "Windows desktop", "Microsoft Store",
"MSIX") are not counted as leakage: a developer naming their own stack is not echoing a description.

## Held-out set

`scenarios\heldout\` holds 41 scenarios, each with a `novice` and a `terse` paraphrase. They were
written separately from the dev set, from public developer reports (GitHub issues, Stack Overflow,
Microsoft Learn), personas, and plain capability definitions, without seeing any skill or agent
description. Fixtures use randomized names. Every capability is a
primary expectation at least twice, and near-misses and traps are over 20% of the set. Every
held-out scenario also runs in the `none` configuration as a control.

Treat it as gated:

- Run it for release and decision checks (`-Set heldout`), not while iterating on descriptions.
- Do not edit held-out prompts to make a candidate pass, and do not copy their wording into skill
  descriptions. If a held-out scenario is wrong, fix its expectation and say so in the change.
- A description change ships only if it improves or holds the held-out results.

## Demand set

`scenarios\demand\` holds 60 scenarios sampled in proportion to what Windows desktop developers
actually ask about. Each answer is graded against the scenario's rubric by two LLM judges, so the
set measures whether the developer's problem got solved, not which skill loaded. A correct answer
that doesn't use winapp can still be solved.

Where the scenarios came from (`demand-meta\`):

| File | Contents |
|---|---|
| `problems.jsonl` | 300 real problems from GitHub issues (WindowsAppSDK, microsoft-ui-xaml, winappCli, electron-builder, Tauri, Flutter, MAUI, ...) and Stack Overflow, each with its source URL and cluster |
| `dropped.jsonl` | Problems left out, with the reason (outdated, not Windows, not a concrete problem, ...) |
| `clusters.json`, `clusters.md` | 27 problem clusters and each one's estimated share of demand |
| `sampling.md` | The source problem and URL for each scenario |
| `writer-spec.md` | The instructions scenario writers followed. They never saw the plugins |
| `review-log.md` | Changes from the independent review of every rubric against its source and the docs |

Each scenario folder has a `rubric.json` next to `scenario.json`:

| Field | Meaning |
|---|---|
| `cluster`, `framework` | Demand cluster and the developer's stack |
| `goal` | What a successful answer achieves |
| `must_include` | Specific points a solved answer covers |
| `must_not` | Wrong or unsafe advice; any one caps the verdict at partial |
| `acceptable_alternatives` | Other correct approaches (signtool, MSBuild, Visual Studio, ...) |
| `solved`, `partial` | What each verdict means for this scenario |
| `sources`, `verified_with` | The source thread and the docs each point was checked against |

Loading a scenario without a complete rubric fails. Demand scenarios expect no capabilities, so
their routing status is `n/a`; the outcome comes from the judges.

The rubrics were committed before any run. Treat them like the held-out set: don't edit a rubric to
make a candidate pass or copy prompt wording into skill descriptions. If a rubric is wrong, fix it,
say so in the change, and regrade both sides into a new judge folder.

### Running and grading

```powershell
# 1. Run the baseline and the candidate. One model and one iteration in the both configuration is
#    60 sessions per side; all 4 configurations are 240.
pwsh benchmarks\agents\run.ps1 -Set demand -Configuration both -Model claude-sonnet-5.5 -Iterations 1 -OutDir benchmarks\agents\results\dm-base-sonnet-i1
pwsh benchmarks\agents\run.ps1 -Set demand -Configuration both -Model claude-sonnet-5.5 -Iterations 1 -WinAppPlugin C:\src\candidate\plugins\winapp -OutDir benchmarks\agents\results\dm-cand-sonnet-i1

# 2. Grade every answer with both judges (cached; rerun to grade only new answers)
pwsh benchmarks\agents\judge.ps1 -Results benchmarks\agents\results\dm-base-sonnet-i1,benchmarks\agents\results\dm-cand-sonnet-i1 -OutDir benchmarks\agents\results\dm-judge -MaxCredits 300

# 3. Tables: outcome per candidate and model, paired difference with a 95% CI, by cluster, judge agreement
'{ "base": ["dm-base-sonnet-i1"], "cand": ["dm-cand-sonnet-i1"] }' | Set-Content benchmarks\agents\results\spec.json
pwsh benchmarks\agents\demand-analyze.ps1 -Spec benchmarks\agents\results\spec.json -Judgments benchmarks\agents\results\dm-judge\judgments.jsonl -Clusters benchmarks\agents\demand-meta\clusters.json -Out benchmarks\agents\results\demand.md
```

`judge.ps1` shows how many batches it would run with `-Plan`. It grades in batches of up to 6
answers to one scenario, with the answering model, plugin, and configuration hidden and the order
shuffled. Each judge runs in an empty Copilot home with shell, writes, and URLs denied. Every
answer gets a verdict from every judge (default `claude-opus-5.5` and `gpt-6.1-sol`), so no model
is graded only by its own family. Empty answers are `unsolved` without a judge. A batch whose reply
isn't one valid verdict per answer is retried once and then reported as failed (exit code 1); rerun
to grade what's missing. `-MaxCredits` is a hard cap: each attempt reserves an estimate before it
starts, so parallel batches can't overshoot it, and every attempt's cost is kept in `spend.jsonl`
in `-OutDir`, so reruns count earlier spending, failed batches included. Judgments append to `judgments.jsonl` in `-OutDir`, keyed by result folder
name and line, so use unique result folder names. With full batches of 6, grading costs about 1.2
AI credits per answer per judge; smaller batches cost more per answer.

How to read `demand-analyze.ps1` output:

- **Score** is the mean over judges of solved = 1, partial = 0.5, unsolved = 0. An answer counts only
  when every judge in `-Judge` (default: the same two) graded it. **Cluster-weighted** weights each cluster's mean by its demand share
  (`-Clusters`).
- **Paired difference** pairs runs by scenario, model, and iteration and resamples scenarios
  (2,000 bootstrap samples) for the 95% interval. Treat an interval that includes 0 as no change.
  With 60 scenarios, 3 models, and 2 iterations the interval is about ±0.03.
- **Judge agreement** reports exact agreement, Cohen's kappa, and how often each judge favors its
  own model family. `-Calibration <file>` writes 15 random graded answers with their rubric and both
  verdicts for a spot check.

## Limits

- Shell, file writes, and URLs are denied, so this measures routing, skill loading, and the final
  answer, not whether the agent completes the task. Models still try those tools; the attempts show
  up as denied calls.
- Local only, Copilot CLI only, no CI integration, sequential runs.
- Results vary between runs; use several iterations before drawing conclusions.
- Demand-set judges agree moderately (kappa about 0.56 in the first full run), and the Opus judge
  grades more leniently than the GPT judge. Compare candidates by paired differences, not raw
  scores, and spot-check with `-Calibration`.
