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

# Recommended first step: a quick baseline (one model, one iteration: 42 sessions)
pwsh benchmarks\agents\run.ps1 -Model claude-sonnet-5.5 -Iterations 1

# Then the full default matrix (3 models x 3 iterations: 378 sessions)
pwsh benchmarks\agents\run.ps1

# A single scenario
pwsh benchmarks\agents\run.ps1 -Scenario electron-notifications -Configuration winapp -Model claude-sonnet-5.5 -Iterations 1

# Compare against the published WinUI plugin (microsoft/win-dev-skills v0.7.1)
pwsh benchmarks\agents\run.ps1 -Configuration winui,both -WinUIPlugin published

# Compare a candidate WinUI plugin from a local folder
pwsh benchmarks\agents\run.ps1 -Configuration winui,both -WinUIPlugin C:\src\my-winui-candidate\agent-plugin
```

| Parameter | Default (`config.json`) | Notes |
|---|---|---|
| `-Scenario <id[]>` | all | Folder names under `scenarios\` |
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
| `-Plan` | | Dry run |
| `-Rescore <resultsDir>` | | Re-evaluate recorded runs against the current scenario expectations; no model calls |

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
6. Evaluates the scenario's expectations against the invoked skills, appends a line to
   `runs.jsonl`, and deletes the temporary folders.

Nothing is installed into your own Copilot home.

## Reading results

Each invocation writes `results\<timestamp>\`:

- `summary.md`: per scenario, one row per configuration and model with pass rate, the most common
  set of loaded skills, and median tokens, skill context, and duration.
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

Run statuses:

| Status | Meaning |
|---|---|
| `pass` / `fail` | The run completed and its expectations were / were not met |
| `timeout` | The session exceeded its timeout and was killed |
| `preflight_failed` | Plugin install failed or the installed skill set did not match the configuration |
| `harness_error` | Copilot exited with an error, no session log was found, or the workspace was modified |
| `cleanup_failed` | The run's temporary folders could not be deleted (`reason` keeps the original status) |

### Rescoring after changing expectations

When you change a scenario's `expect` block, re-evaluate an earlier run instead of paying for new
sessions:

```powershell
pwsh benchmarks\agents\run.ps1 -Rescore benchmarks\agents\results\<timestamp>
```

This writes `runs.rescored.jsonl` and `summary.rescored.md` next to the originals, which are left
unchanged. Only `pass` and `fail` runs are re-evaluated. Each rescored run keeps its
`originalStatus`. A changed prompt or fixture still needs a new run.

## Adding a scenario

Create `scenarios\<id>\scenario.json` (the id must match the folder name) and, optionally, a
`fixture\` folder with the few files the prompt needs:

```json
{
  "id": "sign-existing-msix",
  "description": "Only sign an MSIX that a pipeline already built.",
  "prompt": "Our build pipeline already produces dist\\ContosoApp.msix. Sign it with our company certificate ...",
  "configurations": ["winapp", "both"],
  "fixture": "fixture",
  "timeoutMinutes": 5,
  "expect": {
    "skillsAny": ["winapp-signing"],
    "skillsAll": [],
    "skillsForbid": ["winui-*"],
    "maxSkills": 3
  }
}
```

- Write the prompt the way a developer would ask for the work. Never name a skill.
- `skillsAny`: at least one must load. `skillsAll`: all must load. `skillsForbid`: none may load.
  `maxSkills`: upper bound on distinct skills loaded. Names accept `*` wildcards. "Loaded" means the
  agent invoked the skill.
- Expected skills that are not installed in a configuration are ignored for that configuration, so
  one scenario can list skills from both plugins.
- Run `Invoke-Pester benchmarks\agents\tests` to validate scenario files.

## Limits

- Shell, file writes, and URLs are denied, so this measures routing and skill loading, not whether
  the agent completes the task. Models still try those tools; the attempts show up as denied calls.
- Local only, Copilot CLI only, no CI integration, sequential runs.
- Results vary between runs; use several iterations before drawing conclusions.
