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
- `n/a` status: a run where nothing in the expectation applies to the installed skills is no longer a pass; it is left out of pass rates.
- Each run records repeated skill deliveries and the context they add, and `summary.md` reports them.
- `-Compare` reports per-model and per-cell differences between two sets of result folders.
- Every scenario must include the `both` configuration.

## Where reviewers disagreed

- **Fix weak prompts or keep them?** Opus and Gemini wanted to rewrite prompts that echo skill descriptions (`winui-code-review`, `restore-cloned-cpp`) and to replace `xaml-unknown-member`. `TextBox.Watermark` → `PlaceholderText` is trivia that models already know, so skipping the skill is reasonable. GPT argued to keep originals as regression sentinels and add companion scenarios. **Decision:** keep the originals unchanged, and add companions (`winui-infobar-enum-lookup`, `explicit-find-api-numberbox`). Both rewritten scenarios already pass 100%, so they do not discriminate either way.
- **Missing fixtures** (`sign-existing-msix`, `msix-install-fails`, `manifest-alias-icons`). All three flagged them: an agent that finds an empty folder may give up early. They are kept as is for comparability. The new scenarios ship with fixtures. Re-fixturing the originals is a follow-up, and it needs a fresh baseline. A one-off control (the `sign-existing-msix` prompt with the MSIX and PFX present; Sonnet, 6 runs with the current plugins and 6 with a candidate description) still skipped `winapp-signing` 12/12 while naming `winapp sign` 12/12, so the empty folder does not explain the skip. The control was not kept as a scenario: it duplicates `sign-existing-msix`.
- **`winforms-package-sign` expectations.** Opus would accept package or signing. GPT would accept package or frameworks. Both accept `winapp-package`, so that is what the scenario now expects.
- **Iterations.** Opus and GPT both said that three iterations is a screen, not proof: only 0/3 versus 3/3 swings are signal. GPT suggested 10-20 iterations for final structural decisions. Gemini suggested 5.
- **Electron UI test.** Opus said Playwright with no skill is legitimate. GPT wanted a positive expectation. The prompt now says to drive the desktop window, so UI automation is the expected route.

## Not done yet (recommended follow-ups)

1. ~~**Capability-based expectations.**~~ Done in v2 (below).
2. **More skill-context statistics in `summary.md`:** mean and p90 alongside the median, and first-turn input tokens.
3. **Content hashes** for prompts, fixtures, and installed skills in `run-info.json`.

## Benchmark v2: realism review (October 2026)

A second review (GPT: real developer phrasing from GitHub and Stack Overflow; Gemini: personas and journeys; Sonnet: leakage, gaming, held-out design) found that 19 of 39 skill-expecting prompts echoed skill-description vocabulary, only 3 of 43 started from an error, near-misses were 9% of the set and all of the "load nothing" kind, and always loading the same 3 skills passed 35% of scenarios. v2 changes:

- **Capabilities, not skill names.** `capabilities.json` maps each plugin version's skills to 22 capabilities. Scenarios list primary, acceptable, and forbidden capabilities; `partial` is a new status. Renamed or merged skills are scored through a new map.
- **Cohorts.** Every scenario is `implicit`, `error`, `vague`, `explicit-command`, `near-miss`, or `trap` (`followup` reserved). Explicit-command scenarios are reported apart from implicit routing.
- **Right command without the skill** is reported separately from a routing miss.
- **Dev set (67):** the 42 scenarios plus 25 from the reviews. Reworded to remove leaked vocabulary: `winui-code-review`, `restore-cloned-cpp`, `winui-infobar-enum-lookup`, `winui-card-grid-control`, `winui-control-sample-request`, `winui-repair-prerequisites`, `electron-desktop-ui-test`, `winui-generic-csharp-bug` (now with real tests), `flutter-trust-certificate`, `electron-notifications`, `clean-machine-ui-smoke`, `explicit-session-diagnostic`. Expectation fixes: `xaml-unknown-member` (API lookup, WinUI design, or WinUI build; troubleshooting only partial), `store-submission` (Store publishing is primary; packaging is partial; `n/a` where no skill covers the Store), `msi-app-sparse-identity` (identity primary), `electron-notifications` (framework guidance or identity), `clean-machine-ui-smoke` (needs Sandbox and UI automation together). New: 9 error-first, 3 vague, 3 journeys, 10 near-misses and traps. Near-misses and traps are 22% of dev.
- **Held-out set (40 x 3 prompts)** written blind by GPT-6.1 Sol from capability definitions, personas, and public sources; two revision rounds against the lint. 27.5% near-miss/trap, 14 conflicting-signal cases, every capability primary at least twice, every scenario with a `none` control.
- **Lint** (`run.ps1 -Lint`) for leaked phrases, description overlap, fixed names, and fixture realism. Held-out: 0 errors. Dev: warnings only (the dev set keeps some leaky originals as regression sentinels).

### Where judgment calls were made

- **WinUI-scoped capabilities.** `winui-packaging` maps to `winui.package`, not `msix.package`, so non-WinUI scenarios that forbid `winui.*` now fail it. This turned 9 `winui`-configuration passes of `wpf-winappsdk-msix-trap` into failures on rescore.
- **`winui-design` provides `api.lookup`** because it documents project-aware API checks. In WPF scenarios it still fails through the `winui.*` forbid.
- **Neutral vocabulary.** The lint ignores framework and product names ("WinUI app", "MSIX", "Microsoft Store") so the held-out prompts do not need euphemisms. The first held-out draft avoided vocabulary by inventing words ("envelope" for manifest); it was rewritten.
- **Held-out expectation fixes after the baseline.** Five held-out scenarios forbade a capability that the only skill providing their primary capability also carries (for example, `project.setup` primary with `project.scaffold` forbidden, when `winapp-setup` provides both), so they could not pass. Their forbids were narrowed (`cairnwatch`, `larkspur`, `fenwick`, `orris`, and `bluefern`, which forbade `winui.*` while only `winui-packaging` covers the Store). Scoring now also treats a primary capability that is only reachable through a skill carrying a forbidden capability as not installed, and a test keeps held-out scenarios free of such contradictions. Prompts were not changed.

### v2 baseline (current plugins, October 2026)

Held-out: 3 models x 1 run per prompt (base + 2 paraphrases) in `winapp`/`winui` (whichever provides the primary capability) and `both`, plus the `none` control on base prompts. Dev: new and reworded scenarios 3 models x 2 iterations; unchanged scenarios are the prior 3 x 3 runs rescored. 1,335 new sessions, 13,193 AI credits.

| Model | Dev routing | Held-out routing | Held-out base / novice / terse |
|---|---|---|---|
| claude-sonnet-5.5 | 57% | 32% | 35% / 28% / 34% |
| claude-opus-5.5 | 92% | 78% | 76% / 81% / 76% |
| gpt-6.1-sol | 94% | 84% | 86% / 80% / 85% |

Routing leaves out the `none` control and explicit-command scenarios; pass = pass / (pass + partial + fail).

- The dev set overstates routing: every model drops 10-25 points on held-out prompts. Rewording the 12 leaky dev prompts alone dropped several from 100% (for example `restore-cloned-cpp` 18/18 to 9/12, `winui-code-review` 18/18 to 9/12, `electron-notifications` 18/18 to 8/12).
- Sonnet mostly loads no skill on realistic prompts (held-out 32%).
- Weakest capabilities on held-out (`both`): `winui.migrate` 17%, `framework.guidance` 22%, `winui.review` 28%, `project.setup` 33%, `store.publish` 33% (8 partial: packaging loaded, no Store guidance).
- Without plugins, models named the right `winapp` command on 4 of 71 base prompts; with no plugins, sessions cost 66-82% of the `both` cost for the same prompts.
- In 46 of 137 held-out misses the agent still named the right `winapp` command.
- Loading the three most-loaded skills on every prompt passes 15 of 65 dev and 15 of 120 held-out prompts (it passed 35% of the old scenarios).
- Near-miss traps that should load nothing pass 100%. The failing traps expect `framework.guidance` for "WPF, but Fluent-looking" asks (`holloway` 1/18, `alderfield` 9/18); no current skill documents WPF styling, so read these as a coverage gap as much as a routing miss.
