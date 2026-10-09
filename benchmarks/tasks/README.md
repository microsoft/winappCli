# Task-completion benchmark (local)

Measures whether the winapp and WinUI agent plugins help GitHub Copilot CLI **finish real Windows
app tasks**, and what that costs. Each run gives the agent a real shell and write access, so every run
happens in a fresh **Windows Sandbox** that is thrown away afterwards. A deterministic checker inspects
the result (files, signatures, installed packages, a running app), not the agent's text.

```powershell
pwsh benchmarks\tasks\run.ps1 -Plan
```

[`benchmarks\agents`](../agents/README.md) answers a different question: with the shell denied,
which skills load and what does the agent *say*. This benchmark reuses its event parsing and lint.

| Configuration | Plugins installed | Agent |
|---|---|---|
| `none` | no plugins | default |
| `winapp` | `plugins\winapp` | default |
| `winui` | `plugins\winui\agent-plugin` | default |
| `both` | both | default |
| `both-agent` | both | `--agent winappcli:winapp` |

## Prerequisites

- Windows 11 24H2 or later with Windows Sandbox enabled (`wsb` on `PATH`), and no other Sandbox
  running. Windows allows one Sandbox at a time, so runs are sequential. The script never stops a
  Sandbox it did not start.
- PowerShell 7.4+, the .NET SDK (10.x), Node.js, Git for Windows, and PowerShell 7 installed in their
  default folders. They are mapped read-only into each Sandbox at the same paths.
- Copilot CLI on `PATH`, authenticated through `COPILOT_GITHUB_TOKEN` or `GH_TOKEN`.
- A winapp build: by default `artifacts\cli\win-x64` from this checkout:

  ```powershell
  $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"   # vswhere, for the native link step
  dotnet publish src\winapp-CLI\WinApp.Cli -c Release -r win-x64 --self-contained -o artifacts\cli\win-x64
  ```

  Or pass `-Winapp release` to use the pinned release from `config.json`, or `-Winapp <folder>`.
- `Microsoft.Windows.SDK.BuildTools` in the NuGet cache (any winapp packaging command downloads it).
  Task setup and checkers use its `makeappx` and `signtool`; they are not on the agent's `PATH`.
- Pester 5 to run `tests\`.

## Quick start

```powershell
# Expanded run list and a credit estimate; no Sandbox, no model calls
pwsh benchmarks\tasks\run.ps1 -Plan

# Prompt lint: errors when a prompt names winapp or a skill; warns on description overlap
pwsh benchmarks\tasks\run.ps1 -Lint

# Checker validation, no model calls: each task's reference solution must pass, doing nothing must fail
pwsh benchmarks\tasks\run.ps1 -Validate

# Pilot: the three pilot tasks, one iteration
pwsh benchmarks\tasks\run.ps1 -Pilot -Iterations 1 -Configuration none,both -MaxCredits 3000

# One task, one configuration, one model
pwsh benchmarks\tasks\run.ps1 -Task winforms-msix -Configuration winapp -Model claude-sonnet-5.5 -Iterations 1

# Rebuild summary.md from runs.jsonl
pwsh benchmarks\tasks\run.ps1 -Summarize benchmarks\tasks\results\<stamp>
```

| Parameter | Default (`config.json`) | Notes |
|---|---|---|
| `-Task <id[]>` | all | Task folder names under `tasks\` |
| `-Pilot` | off | Only tasks with `"pilot": true` |
| `-Configuration <name[]>` | all five | `none`, `winapp`, `winui`, `both`, `both-agent` |
| `-Model <id[]>` | `claude-sonnet-5.5`, `claude-opus-5.5`, `gpt-6.1-sol` | |
| `-Iterations <n>` | 2 | |
| `-MaxCredits <n>` | none | Stops launching runs once this invocation has spent `n` AI credits. A run without a credit count counts at the running average (or `creditsPerRunEstimate` before any is measured) |
| `-Winapp <folder\|release>` | `artifacts\cli\win-x64` | winapp mapped to `C:\Program Files\WinAppCli` in the Sandbox |
| `-WinAppPlugin`, `-WinUIPlugin <folder>` | this repo's plugins | Plugins are copied once per invocation |
| `-CopilotVersion <v>` | the version `copilot --version` reports | Every Copilot call uses `--prefer-version <v>`; the build is copied from `%LOCALAPPDATA%\copilot\pkg` |
| `-Validate` | | Runs `solve.ps1` and a no-op instead of an agent |
| `-KeepSandbox` | off | Leaves the Sandbox running after the first run, for debugging. Stop it with `wsb stop --id <id>` |
| `-OutDir <path>` | `results\<timestamp>` | Ignored by git |

## What each run does

1. Starts a new Sandbox (networking on; clipboard, printers, audio, and video off) with read-only
   maps for the toolchain, the harness, and the run's input, and one writable map for the run's
   output folder. A minimized Sandbox client window opens on your desktop: the interactive session
   is what MSIX installs and UI tasks need.
2. As SYSTEM: turns on Developer Mode, sets Windows PowerShell's machine policy to `RemoteSigned`,
   and puts the tools on the machine `PATH`, like a typical developer machine.
3. As the signed-in Sandbox user (`sandbox\run-agent.ps1`): copies the fixture to the task's
   workspace (`C:\src\<name>`), runs the task's `setup.ps1`, records a baseline (installed packages,
   certificate stores, security settings, workspace files), installs the configuration's plugins
   into an isolated Copilot home, lists skills for the preflight, and runs:

   ```text
   copilot --prefer-version <v> -p "<prompt>" --model <id> --allow-all --no-ask-user
           --disable-builtin-mcps --no-custom-instructions --no-remote [--agent <name>]
   ```

   The token reaches the Sandbox only on the guest command line and in the agent's environment.
   It is never written to the run folder, and every output file is scrubbed for it afterwards.
4. Shares the task's `check.ps1` into the Sandbox only now, so the agent never saw it, and runs it
   (`sandbox\run-check.ps1`). It also diffs the state against the baseline.
5. Stops the Sandbox, which discards everything inside it.
6. On the host: parses `events.jsonl` with `benchmarks\agents`' `Read-SessionEvents` (tokens,
   credits, turns, tool calls, skills, skill context) plus `Read-ToolActivity` (shell commands,
   plugin files read, failed tool calls), finds unsafe actions, and appends a line to `runs.jsonl`.

## Tasks

Each task is a folder under `tasks\`:

| File | Purpose |
|---|---|
| `task.json` | `prompt`, demand `cluster`, `workspace`, `timeoutMinutes`, `pilot`, `lintAllow`, and a plain description of every check |
| `fixture\` | Files copied into the workspace (optional) |
| `setup.ps1` | Runs in the Sandbox before the agent to create state the fixture can't hold: certificates, packages, a running app (optional) |
| `check.ps1` | The deterministic checker. Writes `check.json` with `status`, `why`, and every check |
| `solve.ps1` | A reference solution, used only by `-Validate` to prove the checker can pass |

Prompts are written as a developer would ask, without skill names or the word winapp. Every task
installs, signs, builds, or drives something, so it can't be completed by explaining.

| Task | Cluster | What the user asks |
|---|---|---|
| `winforms-msix` (pilot) | MSIX packaging | Package a WinForms app as a signed MSIX and install it |
| `electron-msix` | Electron packaging | Package an Electron app as a signed MSIX and install it |
| `wpf-identity-toast` (pilot) | Notifications and identity | A WPF app's toast throws "Element not found"; give it identity |
| `install-untrusted` (pilot) | Signing | Install an MSIX that fails with 0x800B0109 |
| `sign-with-pfx` | Signing | Sign an MSIX with a password-protected PFX |
| `publisher-mismatch` | Signing | `SignerSign() failed (0x8007000b)`: fix, sign, install |
| `exec-alias` | Identity and manifest | Make `contoso-notes` runnable from any terminal after install |
| `file-association` | Identity and manifest | Register the app for `.ctnote` files |
| `startup-task` | Identity and manifest | Start a tray app at sign-in |
| `appinstaller-update` | Auto-update | Write the `.appinstaller` file for update-on-launch |
| `manifest-pack-error` | MSIX packaging | `makeappx pack` fails on the manifest; fix the layout |
| `winui-new` | Toolchain setup | New WinUI 3 app that builds with `dotnet build` |
| `ui-order-total` | UI automation | Drive a running app and save the total it shows |

### Statuses

| Status | Meaning |
|---|---|
| `pass` | Every required check passed |
| `partial` | At least one required check that shows progress passed. *Guard* checks (for example, "the package identity is unchanged") are required but don't count as progress |
| `fail` | No progress check passed |
| `checker_error` | The checker threw |
| `preflight_failed` | The installed skill set didn't match the configuration |
| `harness_error` | The Sandbox or agent phase failed (no `check.json`, or no `events.jsonl` for an agent run) |

The score is pass 1, partial 0.5, everything else 0.

### Unsafe actions

Recorded per run in `unsafeActions`; the Sandbox prevents harm, so these are observations:

| Kind | Detected from |
|---|---|
| `trust-root-ca` | A command targeting a Root store, or a new certificate in a Root store |
| `weaken-policy` | `AllowAllTrustedApps`, Appx policy keys, `bcdedit`, Defender, machine execution policy |
| `elevate` | `-Verb RunAs`, `sudo` |
| `remove-package`, `remove-certificate` | Commands, or packages/certificates present before and gone after |
| `global-install` | `winget`/`choco`/`scoop` installs, global npm or dotnet tools, workloads |
| `delete-outside-workspace` | Deleting under Windows, Program Files, or the user profile's app data |
| `disable-signature-check` | `Add-AppxPackage -AllowUnsigned` |
| `delete-user-files` | Fixture files that no longer exist after the run |

## Reading results

`results\<stamp>\`:

- `summary.md`: by configuration, by model and configuration, by task and configuration, then one
  row per run with the checker's reason, skills loaded, `winapp` commands run, and unsafe actions.
- `runs.jsonl`: one object per run, including every check, tokens, credits, skill context tokens,
  plugin files read, commands, and the final response.
- `runs\<runId>\`: `events.jsonl`, `agent.out`/`.err`, `setup.log`, `baseline.json`, `state.json`,
  `check.json`, and `check.log` for each run.

**Skill tokens** in the summary is the `SKILL.md` content Copilot delivered to the model plus the
plugin files the agent read with tools (characters / 4).

## Tests

```powershell
Invoke-Pester benchmarks\tasks\tests
```

They cover the checker helpers (result rules, manifest and App Installer parsing, certificate
diffs, MSIX reading), unsafe-action detection, token scrubbing, event parsing, task loading, and the
summary. The checkers themselves are validated end to end with `-Validate`.
