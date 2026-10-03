# Scenario review (October 2026)

Three independent reviewers from different model families (GPT-6.1 Sol, Gemini 3.8 Flash, Claude Opus 5.5) read the 18 scenarios, every skill and agent, the CLI command list, and the 378-run baseline. Each answered: are we testing the right things to decide skill structure and catch routing and token regressions? This file records what changed and where the reviewers disagreed.

## Verdict (all three agreed)

The 18 scenarios are good smoke tests for "did a plausible skill load?" They were not enough to choose a skill structure:

- Several skills had no scenario where they were the main expected skill: `winapp-find-ui`, `winapp-identity`, `winui-packaging`, `winui-setup`, `winui-session-report`.
- Large CLI areas had no scenario at all: Azure Trusted Signing, Store, sparse packages, debug identity, certificate trust, CI pipelines, and the Tauri, Rust, and Flutter frameworks.
- Only one near-miss existed, and it had nothing Windows-specific to tempt a wrong skill.
- No scenario used `skillsAll` (a task that needs two skills).
- Some passes were vacuous. In the `winui` configuration, `find-winrt-api-battery` expected only `winapp-find-api`, which is not installed there. The harness ignores missing skills, so a run with no skills at all passed.

## Changes to existing scenarios

Prompts and fixtures are unchanged, so earlier runs stay comparable. Only expectations and configurations changed. Re-evaluate old runs with `-Rescore`.

| Scenario | Change | Why |
|---|---|---|
| `find-winrt-api-battery` | `skillsAny` += `winui-design` | Removes the vacuous `winui` pass. `winui-design` documents API checks. |
| `winforms-package-sign` | `skillsAny` = `winapp-package` | `winapp-frameworks` or `winapp-signing` alone does not cover building an installer. |
| `maui-windows-msix` | `skillsAny` = `winapp-maui` | Generic packaging guidance misses the MAUI manifest-placeholder issue this scenario exists for. |
| `wpf-add-notifications` | dropped `winapp-find-api` from `skillsAny` | API lookup alone does not deliver the identity a toast needs. |
| `electron-notifications` | dropped `winapp-setup` from `skillsAny` | Setup alone does not cover notifications. |
| `winui-feature-ui-test` | `skillsAny` += `winapp-ui-automation`; adds `winapp` config | UI automation can legitimately drive a WinUI app. |
| `new-winui-app`, `winui-settings-page` | add `winapp` config | `winapp-setup` advertises scaffolding WinUI apps, and `winapp-find-ui` covers WinUI controls. |

Rescoring the baseline with these expectations changed 3 of 378 runs.

## New scenarios (24)

| Scenario | Configs | What it decides |
|---|---|---|
| `winui-infobar-enum-lookup` | winapp, winui, both | find-api inside WinUI work: get enum and property names right for the referenced SDK |
| `wpf-bluetooth-api-lookup` | winapp, winui, both | find-api outside WinUI. WinUI skills are a trap for this WPF app. |
| `winui-card-grid-control` | winapp, winui, both | Implicit control choice: is `winapp-find-ui` needed, or does `winui-design` cover it? |
| `winui-control-sample-request` | winapp, winui, both | User explicitly wants a Gallery or Toolkit sample |
| `wpf-winappsdk-msix-trap` | winapp, winui, both | "Windows App SDK is not WinUI" for packaging |
| `winui-release-msix` | winapp, winui, both | `winui-packaging` versus `winapp-package` overlap and co-load cost |
| `winforms-startup-task-identity` | winapp, both | Debug identity for an identity-only API (primary `winapp-identity`) |
| `clean-machine-ui-smoke` | winapp, winui, both | Sandbox plus UI automation without the word "Sandbox" (job-based "UI automation incl. sandbox") |
| `electron-desktop-ui-test` | winapp, winui, both | Drive an Electron window. `winui-ui-testing` is allowed, WinUI build and design skills are not. |
| `winui-generic-csharp-bug` | winapp, winui, both | Near-miss: pure C# bug inside a WinUI project |
| `win32-registry-read` | winapp, winui, both | Near-miss: classic Win32 C++ |
| `powershell-log-retention` | winapp, winui, both | Near-miss: Windows server scripting |
| `electron-signed-msix` | winapp, both | Electron packaging job (frameworks, setup, package, and signing co-load) |
| `trusted-signing-ci` | winapp, both | Azure Trusted Signing in CI |
| `tauri-ci-msix` | winapp, both | Tauri plus CI packaging |
| `msi-app-sparse-identity` | winapp, both | Production sparse identity next to an existing MSI |
| `file-type-and-protocol` | winapp, both | Manifest extensions: file association and protocol |
| `rust-cli-alias-msix` | winapp, both | Multi-skill (`skillsAll`: package + manifest), Rust |
| `explicit-winapp-cli-package` | winapp, both | Explicit request: "use the winapp CLI" (never names a skill) |
| `explicit-find-api-numberbox` | winapp, winui, both | Explicit command request: control for Sonnet's find-api skipping |
| `store-submission` | winapp, winui, both | Gap probe: no skill documents `winapp store` |
| `winui-repair-prerequisites` | winui, both | Primary `winui-setup` |
| `explicit-session-diagnostic` | winui, both | Opt-in `winui-session-report` still loads when asked |
| `flutter-trust-certificate` | winapp, both | Certificate trust and install, Flutter |

Fixtures are 1-3 tiny files each. No prompt names a skill.

## Measurement changes made

- `-Agent <name>` runs every session with `--agent` (plugin agents are namespaced, for example `winappcli:winapp` and `winui:winui-dev`), so the cost of the custom agents can be measured.
- `-MaxCredits <n>` stops launching sessions once a run has spent `n` AI credits.
- Each run records `winappCommands`: the `winapp` commands the agent tried to run or named in its final answer. This separates "skipped the skill but still used the right command" from "knew nothing". Shell is denied in the benchmark, so attempted commands still show up.
- Each run records `selectedAgent`.

## Where reviewers disagreed

- **Fix weak prompts or keep them?** Opus and Gemini wanted to rewrite prompts that echo skill descriptions (`winui-code-review`, `restore-cloned-cpp`) and to replace `xaml-unknown-member`. `TextBox.Watermark` → `PlaceholderText` is trivia that models already know, so skipping the skill is reasonable. GPT argued to keep originals as regression sentinels and add companion scenarios. **Decision:** keep the originals unchanged, and add companions (`winui-infobar-enum-lookup`, `explicit-find-api-numberbox`). Both rewritten scenarios already pass 100%, so they do not discriminate either way.
- **Missing fixtures** (`sign-existing-msix`, `msix-install-fails`, `manifest-alias-icons`). All three flagged them: an agent that finds an empty folder may give up early. They are kept as is for comparability. The new scenarios ship with fixtures. Re-fixturing the originals is a follow-up, and it needs a fresh baseline.
- **`winforms-package-sign` expectations.** Opus would accept package or signing. GPT would accept package or frameworks. Both accept `winapp-package`, so that is what the scenario now expects.
- **Iterations.** Opus and GPT both said that three iterations is a screen, not proof: only 0/3 versus 3/3 swings are signal. GPT suggested 10-20 iterations for final structural decisions. Gemini suggested 5.
- **Electron UI test.** Opus said Playwright with no skill is legitimate. GPT wanted a positive expectation. The prompt now says to drive the desktop window, so UI automation is the expected route.

## Not done yet (recommended follow-ups)

1. **Capability-based expectations.** Opus and GPT both called this blocking for comparing restructured plugins. Today expectations name skills, so a candidate that renames or merges skills gets vacuous passes. Each variant should map its skills to capabilities (`sign`, `package`, `api-lookup`, and so on), and scenarios should expect capabilities.
2. **An explicit `n/a` status** when no expected skill is installed. The note `skillsAny not applicable: none installed` exists, but these runs still count as passes in summaries.
3. **Repeat-invocation and delivery-count metrics in `summary.md`.** Also report mean and p90 skill context alongside the median, and the first-turn input tokens.
4. **Content hashes** for prompts, fixtures, and installed skills in `run-info.json`.
