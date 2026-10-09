# UI automation benchmarks

Two benchmarks that measure how well Windows UI automation works against real apps: inbox **Calculator** and a pinned build of **WinUI 3 Gallery**.

- **`run.ps1` is the agent benchmark.** Copilot CLI gets a natural user request (for example "add 123 and 456 in the Calculator that shows 42"), with shell allowed, and drives the real app. Afterwards the harness reads the app state itself through UI Automation, process counts, and window state. It never trusts the agent's answer. This is the end-to-end number that matters for agent UX, because model turns, not tool calls, dominate wall time.
- **`perf.ps1` is the scripted perf benchmark, with no agent.** It times single UI commands (startup, inspect, invoke, and so on) and runs fixed command sequences that do each scenario's task. It is cheap, deterministic, and good for spotting regressions in the CLI itself.

Both benchmarks take over the desktop while they run. Run them on a machine you are not using.

## Requirements

- Windows 11 and PowerShell 7.4 or later.
- [GitHub Copilot CLI](https://docs.github.com/copilot/how-tos/copilot-cli) on `PATH`, with `GH_TOKEN` or `COPILOT_GITHUB_TOKEN` set. Each run uses its own isolated Copilot home, so your own Copilot settings, plugins, and sessions are not used or changed.
- `winapp` on `PATH`. To benchmark a local build, put its folder first on `PATH`. The version is recorded in the results.
- Calculator, installed from the Microsoft Store.
- For Gallery scenarios: the benchmark build of WinUI 3 Gallery. Install it once:

  ```powershell
  .\benchmarks\ui\setup-gallery.ps1
  ```

  This builds microsoft/WinUI-Gallery at the tag pinned in `config.json` and installs it as a separate app named **WinUI 3 Gallery (UI Benchmark)**, so it never touches a Gallery you installed yourself. The build is cached under `results\.cache`, and rerunning the script does nothing when that build is already installed. The first build needs `git`, the .NET SDK, and the Windows SDK version named in `config.json` (`gallery.windowsSdk`), and takes several minutes. With Developer Mode on, no certificate is needed. Otherwise follow the printed `winapp cert install` instruction. Use `-Uninstall` to remove it.

## Agent benchmark

Plan first. A plan prints the session count and the estimated AI credits and minutes, without running anything:

```powershell
.\benchmarks\ui\run.ps1 -Plan
```

Run one scenario once as a smoke test:

```powershell
.\benchmarks\ui\run.ps1 -Scenario calc-running -Method winapp -Iterations 1
```

Run the full benchmark for both checked-in methods. Each method runs 7 scenarios × 5 iterations with `claude-sonnet-5.5`:

```powershell
.\benchmarks\ui\run.ps1 -Method winapp,none -OutDir benchmarks\ui\results\overnight
```

| Parameter | Default | Meaning |
|---|---|---|
| `-Scenario` | all | Scenario ids to run (folders under `scenarios\`). |
| `-Method` | `winapp,none` | Checked-in method names, or paths to local method files. |
| `-Model` | `claude-sonnet-5.5` | One or more models. |
| `-Iterations` | 5 | Runs per scenario, method, and model. |
| `-MaxCredits` | none | Stop once this many AI credits have been spent. |
| `-OutDir` | `results\<timestamp>` | Results folder. Rerun with the same folder to resume. |
| `-Force` | off | Run even when a target app is already open outside the benchmark. |
| `-KeepArtifacts` | off | Keep each run's Copilot home and logs, for debugging. |
| `-Compare a,b` | | Compare two or more result folders. |
| `-Lint` | | Check that scenario prompts do not echo skill descriptions. |

### Methods

A method is what the agent gets to work with. Two are checked in:

- **`winapp`** installs this repo's `plugins\winapp` plugin and uses the `winapp` CLI on `PATH`.
- **`none`** installs no plugin and hides `winapp` from `PATH`, so the agent improvises, for example with PowerShell and UI Automation.

To compare another tool, write a method file anywhere outside the repo and pass its path:

```powershell
.\benchmarks\ui\run.ps1 -Method winapp,C:\bench\my-tool.method.json -OutDir C:\bench\results\my-tool
```

```json
{
  "name": "my-tool",
  "displayName": "My UI tool",
  "description": "My tool's plugin and MCP server.",
  "plugins": ["C:\\src\\my-tool\\plugin"],
  "skills": ["C:\\src\\my-tool\\skills\\drive-ui"],
  "mcpServers": {
    "my-tool": {
      "type": "local",
      "command": "C:\\src\\my-tool\\bin\\my-tool-mcp.exe",
      "args": [],
      "env": { "MY_TOOL_TOKEN": "${env:MY_TOOL_TOKEN}" },
      "tools": ["*"]
    }
  },
  "allowTools": ["my-tool"],
  "path": ["C:\\src\\my-tool\\bin"],
  "env": { "MY_TOOL_MODE": "fast" },
  "hideCommands": ["winapp"],
  "requires": ["my-tool"]
}
```

| Field | Meaning |
|---|---|
| `name` | Required. Lowercase letters, digits, and dashes. Must be unique within a run. |
| `displayName`, `description` | Shown in the results. |
| `plugins` | Plugin folders to install with `copilot plugin install`. |
| `skills` | Skill folders (each with a `SKILL.md`) to copy into the run's Copilot home. |
| `mcpServers` | MCP server configuration, in Copilot CLI's `mcpServers` format. |
| `allowTools`, `denyTools` | Extra `--allow-tool` and `--deny-tool` values. Shell and all other tools are already allowed; URL access is denied. |
| `path` | Folders to put first on `PATH` for the agent. |
| `env` | Extra environment variables. `PATH` and `COPILOT_HOME` are managed by the harness. |
| `hideCommands` | Command names to hide from `PATH`. The agent sees a stub that exits with "not recognized". |
| `requires` | Commands that must be on `PATH`; the run stops before starting if one is missing. |

Relative paths are resolved from the method file's folder. `${env:NAME}` in `env` or `mcpServers` is filled in from your environment, so secrets stay out of the file. Before each run, the harness checks that the agent sees exactly the skills the method declares, and stops if it doesn't.

### Scenarios

| Scenario | What the agent faces |
|---|---|
| `calc-running` | Calculator is open; compute 123 + 456. |
| `calc-not-running` | Calculator is closed; the agent must start it. |
| `calc-two-instances` | Two Calculators are open. Only the one showing 42 may change. |
| `calc-minimized-scientific` | Calculator is minimized, in Scientific mode. |
| `gallery-combobox` | Navigate from Home to the ComboBox page and pick a color. |
| `gallery-dialog-left-open` | A dialog is already open. Dismiss it, then flip a toggle on another page. |
| `gallery-cold-start` | Gallery is closed (slow start); same task as `gallery-combobox`. |

Each scenario lives in `scenarios\<id>\scenario.json`: the prompt, how to set the app up, and the checks to run afterwards. Prompts read like real user requests and never name tool commands.

### Reading the results

Each results folder holds:

- `summary.md`, with one row per scenario and method and a note for every run that did not pass.
- `runs.jsonl`, with one full record per run: before and after app state, every check, tool calls, and the `winapp` commands used.
- `run-info.json`, with the versions, model, scenario and method hashes, and the machine.

| Column | Meaning |
|---|---|
| Pass | Runs where the app ended in the requested state with no collateral damage. |
| Wall | Median time of the agent session. |
| Turns, Tool calls | Median model turns and tool calls per run. |
| Tool time | Median time with at least one tool running. |
| API time | Median time spent waiting on the model. |
| Tokens (in+out) | Median input plus output tokens per run. |
| Credits | AI credits reported by the sessions: the total per method, the median per run per scenario. |
| Collateral | Number of collateral-damage findings. |

A run passes only when the state checks pass **and** nothing else was disturbed. Collateral damage is any of these:

- `extra-instance`: the agent started more app windows than the task needs.
- `app-closed`: an app the task needed was closed (even if it was reopened).
- `wrong-window-modified`: a window the task did not target was changed.
- `wrong-app-launched`: a related app, such as the Store Gallery, was started.

A run that times out counts as a failure. A run that could not be scored, because app setup failed or Copilot exited with an error, is listed as excluded instead of counted. Rerunning the same `-OutDir` runs those again and skips the rest.

Compare two runs, for example before and after a `winapp ui` change:

```powershell
.\benchmarks\ui\run.ps1 -Compare benchmarks\ui\results\before,benchmarks\ui\results\after
```

## Perf benchmark

```powershell
.\benchmarks\ui\perf.ps1 -Plan
.\benchmarks\ui\perf.ps1 -OutDir benchmarks\ui\results\perf
.\benchmarks\ui\perf.ps1 -Compare benchmarks\ui\results\perf-before,benchmarks\ui\results\perf
```

Each operation in `perf.json` runs once to warm up, and then 20 more times (`-Repeat`). The benchmark reports the median, p95, and max wall time of one command, measured from outside the process. Each sequence (`-SequenceRepeat`, 3 by default) sets the scenario up, runs its fixed commands, and is scored with the scenario's own checks. Use `-Operation` and `-Sequence` to pick ids, and `-SkipOperations` or `-SkipSequences` to skip a part. Results go to `perf-results.json` and `summary.md`, with the machine and tool versions recorded.

### Adapters

An adapter tells `perf.ps1` how to run each operation with one tool. `winapp` is checked in. To time another tool, write an adapter file and pass its path:

```powershell
.\benchmarks\ui\perf.ps1 -Adapter winapp,C:\bench\my-tool.adapter.json
```

```json
{
  "name": "my-tool",
  "displayName": "My UI tool",
  "command": "C:\\src\\my-tool\\bin\\my-tool.exe",
  "operations": {
    "version": ["--version"],
    "invoke": ["click", "{selector}", "--window", "{hwnd}", ["--action", "{action}"]],
    "get-value": ["read", "{selector}", "--window", "{hwnd}"]
  }
}
```

Each operation maps to the arguments for one process. Operations are `version`, `list-windows`, `search`, `inspect`, `get-property`, `get-value`, `invoke`, `set-value`, `wait-for`, `focus`, and `screenshot`. Placeholders are `{pid}`, `{hwnd}`, `{selector}`, `{type}`, `{value}`, `{property}`, `{action}`, `{timeout}`, `{output}` (a fresh file path), and `{adapterDir}`. A nested list is optional and is dropped when one of its placeholders has no value. Operations and sequences that an adapter doesn't implement are skipped and listed. `prefixArgs`, `env`, and `path` work as in method files, so a script-based tool can use `"command": "pwsh"` with `"prefixArgs": ["-File", "{adapterDir}\\my-tool.ps1"]`.

## Safety

- Runs are strictly sequential, and each run sets up and tears down its own app state.
- The benchmark only closes processes it started, or that a run started, matched by process id. It never closes other windows.
- It refuses to start while a target app is already open outside the benchmark. Close the app, or pass `-Force`; even then, those windows are never closed, but an agent might act on them.
- If something it started can't be closed, it stops instead of running on a dirty desktop. Rerun with the same `-OutDir` to continue.

## Tests

The scenario and method schemas, scoring, resume logic, and reports are covered by Pester tests that don't touch the desktop:

```powershell
Invoke-Pester -Path .\benchmarks\ui\tests
```
