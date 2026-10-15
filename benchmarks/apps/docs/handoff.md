# App benchmark: status and handoff

_Last updated: 2026-10-15._

The app benchmark moved here from an internal repository (`win-dev-skills-benchmark`) in
October 2026 without its history. Everything needed to run it is in this folder; the
[README](../README.md) is the reference.

## Where things stand

- **Harness:** stable for unattended runs. A 270-trial run (3 models × 3 setups × 10 scenarios ×
  3 iterations) completed in about 30 hours at concurrency 4.
- **Scoring v2** is in place: gates, requirements, automatic quality checks, design, and a
  scorecard with paired comparisons. The design decisions are summarized below.
- **Machine hygiene and suspend handling** were added after real incidents (see Known issues).
  Don't remove them.
- **Open proposals** (not implemented):
  - [performance](proposals/performance.md): `winapp perf`, spike done;
  - [theme and identity](proposals/theme-and-identity.md).

## Baseline: run906 (2026-10-14/15)

Setup: 10 scenarios (all except `calc`, `counter-winui`, `imageresizer-wpf-to-winui`,
`local-llm-chat`) × `none` / `published` / `winappcli@main` (then named `bare`,
`winappcli-published`, `winappcli`) × Claude Sonnet 5.5, Claude Opus 5.5, GPT-6.1 Sol ×
3 iterations. Judge: Claude Sonnet 5.5. WinApp CLI 0.7.2 prerelease; winappCli `main` at
`76f2b9f`; published catalog winappcli 0.7.1 / winui 0.7.1.

| Model | Setup | n | Score | Δ vs `none` (paired, 95% CI) | Quality checks | Credits/trial |
|---|---|---:|---:|---:|---:|---:|
| Sonnet 5.5 | none | 30 | 82.1 | – | 69% | 69 |
|  | published | 30 | 87.9 | +5.8 ±4.5 | 89% | 96 |
|  | winappcli@main | 30 | 88.1 | +6.0 ±7.5 | 91% | 98 |
| Opus 5.5 | none | 30 | 89.6 | – | 75% | 375 |
|  | published | 30 | 92.9 | +3.4 ±3.1 | 92% | 420 |
|  | winappcli@main | 30 | 95.4 | +5.8 ±1.1 | 94% | 408 |
| GPT-6.1 Sol | none | 30 | 82.8 | – | 58% | 103 |
|  | published | 30 | 91.0 | +8.1 ±1.8 | 80% | 149 |
|  | winappcli@main | 30 | 91.4 | +8.6 ±2.1 | 82% | 160 |
| **All** | none | 90 | 84.8 | – | 68% | 183 |
|  | published | 90 | 90.6 | **+5.8 ±1.9** | 87% | 222 |
|  | winappcli@main | 90 | 91.6 | **+6.8 ±2.6** | 89% | 222 |

All 270 trials are valid. 13 trials originally lost to machine or harness problems were re-validated after fixing those problems: a VM suspend, a stuck AppX service, a mismatched user `DOTNET_ROOT`, and a library project picked instead of the app. `winappcli@main` vs `published`: +1.0 ±2.4 (no measurable difference). The three scores below 50 are real app failures: a crash at startup, a crash on navigation, and tagging that didn't work.

**Findings**
- **The plugins help every model.** The quality checks are where most of the gain comes from:
  packaging, title bar, typography, accessible names and keyboard navigation.
- **`main` and the published plugins were indistinguishable** at this sample size.
- **Cost:** the plugins cost about 20% more credits and 2–3 more minutes per trial. Two reasons:
  - agents load skills and test their own UI (25–50% more tool calls);
  - `none` takes shortcuts. It hand-writes a minimal project file, and 83% of its apps are
    unpackaged.
- **Models:**
  - Opus scores highest and most consistently, at 2–4× the credits.
  - GPT-6.1 Sol builds branded, distinctive apps (custom palette, name, logo). With the plugins
    it defines those colors properly as Light/Dark/HighContrast theme resources.
  - Claude apps look like stock templates, which motivated the
    [theme and identity proposal](proposals/theme-and-identity.md).
- **Every one of the 270 apps was WinUI.** `none` scaffolds by hand (no templates or `winapp`
  available); the plugin setups use `winapp new` in every trial.

## Running a comparison

```powershell
cd benchmarks\apps
npm ci; npm run doctor
# Your branch vs main vs published vs no plugins: one model, 3 iterations, 3 quick scenarios (36 trials, ~2–3 h, ~1,800 credits on Sonnet)
npm run bench -- --scenario calc,counter-winui,text-editor-winui --agent "none,published,winappcli@main,winappcli@my-branch" --iterations 3 --concurrency 3
npm run scorecard -- runN
```

For a decision-grade comparison, use all 10 larger scenarios, 3 iterations and at least two
models. Multiple models in one run need `--run-meta` (see the README); order tasks by
iteration first, so a stopped run still has balanced pairs.

## Design decisions

- **Prompts** read like a natural user request. **Requirements** are what the prompt states or
  implies, or what anyone would expect. No hidden specifics.
- **One headline score** (0–100) plus a scorecard: paired deltas with CIs, wins/losses, credits
  and time. Decisions should rest on the paired comparison, not raw means.
- **Gates zero the score** (build, launch, right framework, Native AOT where required). A failed
  gate skips the judge to save credits.
- **The judge is one fixed model** for every trial, never the model under test.
- **`none` has no `winapp`** on PATH and no preinstalled templates: a genuine no-skills baseline.
- **Every trial starts clean:** fresh `DOTNET_CLI_HOME`, isolated Copilot config, results outside
  the repo.
- **Scoring is deterministic where possible:** quality checks are source, build or UI Automation
  checks, not judge opinions.
- **TypeScript, not a rewrite.** A Rust/ratatui spike cut harness memory 15× but not the
  Copilot runtime's, and fixing the TypeScript harness (production React build, CLI pin,
  isolated config) removed the problems that motivated it. Revisit only if a headless runner
  becomes a bottleneck.

## Known issues and gotchas

| Symptom | Cause | What to do |
|---|---|---|
| Windows sign-in broken, Start menu dead, "kernel information version is invalid" | User PATH over 32,767 chars. The .NET SDK first run adds `<DOTNET_CLI_HOME>\.dotnet\tools` to the registry PATH | Fixed: `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` plus per-trial cleanup and a preflight limit. Never set `DOTNET_CLI_HOME` in scripts without that variable |
| Trials time out overnight, Kernel-Power sleep events | The VM/host suspended the machine | Keep-awake plus automatic retry. Prefer an always-on machine |
| Packaged launches hang ~90 s after a suspend; AppX log shows IndexedDB timeouts (7750/330) | AppX deployment service wedged | Reboot, then `--revalidate-run` the affected trials |
| Packaged net8 apps show "You must install .NET" | User `DOTNET_ROOT` points at another .NET install | The doctor fails on it. Remove the variable |
| `dotnet new` takes minutes | A stray `.csproj`/`.sln`/`Directory.Build.*` in a parent of the results folder | The doctor and preflight flag it. Move it |
| A low score whose requirements all say "Not reported by the judge" | The judge session ended without its JSON | Revalidate the trial |
| Native AOT publish of the `winui-mvvm` template crashes on start (`0xc000027b` in Microsoft.UI.Xaml) | Template/WinAppSDK under `PublishAot` | Not a harness bug; G3 correctly fails it. Worth raising with the template owners |
| Requested model not the billed one | Model ids retired or remapped | `models_used` / `model_mismatch` in `results.json` |
| Copilot CLI stalls on streaming shell commands | Older CLI builds | `package.json` overrides `@github/copilot`; bump deliberately and run a smoke trial |

A large run writes several GB to the results folder and to `C:\winapp-stage` (launch staging,
cleaned per trial). Delete old runs you no longer need.

## Next steps

1. **Align with `benchmarks/agents`:**
   - shared setup names (`benchmarks/agents` uses `none`/`winapp`/`winui`/`both`; this uses `none`/`winappcli`/`published`);
   - a common `published` definition;
   - one comparison report format.
2. **New scenarios** beyond greenfield WinUI:
   - add a feature to or fix a bug in an existing app;
   - WPF → WinUI;
   - non-WinUI apps that use winapp (WPF + notifications, Electron packaging, WinForms identity).

   This needs scenario-level build and launch settings. `benchmarks/agents` fixtures are
   candidates.
3. **[Performance](proposals/performance.md), phase 1:** report-only `perf` block per trial.
4. **[Theme and identity](proposals/theme-and-identity.md):** decide the weights, implement the
   checks, rescore run906.
5. **Cleanup:** remove dormant code paths (macOS/SwiftUI, prompt `sections`, preset scripts,
   `agent_from_package`) once nobody needs them.
