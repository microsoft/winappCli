# Benchmark results dashboard

A static site that shows the `apps` and `tasks` benchmark results. It has four tabs: **Overview** (a one-sentence verdict, four "without → with" tiles and one chart), **By app** (one row per app scenario, sorted by change), **Skills** (what changed per quality-check topic with winapp + skills, then which skills loaded) and **Details** (outcomes, tasks, winapp commands and how we measured). It reads the per-run JSON files described in [RESULTS-FORMAT.md](../RESULTS-FORMAT.md) and needs only Node 20 or later. There are no packages to install.

## Build and preview

```powershell
node benchmarks\dashboard\build.mjs --data C:\path\to\trial-files --label "Run 2 (Nov 5-9)" --out $env:TEMP\winapp-dash
start $env:TEMP\winapp-dash\index.html
```

The build prints what it found:

```text
Run 2 (Nov 5-9): 495 trials (apps 243, tasks 252)
Wrote C:\Users\you\AppData\Local\Temp\winapp-dash\index.html
```

`index.html` opens straight from disk. To serve it over HTTP instead, run `npx serve $env:TEMP\winapp-dash`.

The page remembers the run and tab in the address, so `index.html#tab=details` opens the Details tab for the most recent run.

## What `--data` points at

`--data` takes a folder that contains release folders. A release folder is named `dev` or `vX.Y.Z` and holds the per-run JSON files. The build looks for release folders up to two levels down, and reads the benchmark (`apps` or `tasks`) from each file, so both of these layouts work:

```text
trial-files\                 benchmark-data branch\
  apps\dev\*.json              v0.7.1\*.json
  tasks\dev\*.json             v0.7.1\index.json
                               v0.8.0\*.json
```

Files for the same release from different folders are combined. Pass `--data` more than once to combine several roots:

```powershell
node benchmarks\dashboard\build.mjs --data D:\runs\apps-results --data D:\runs\tasks-results --out .\artifacts\dash
```

## Several development runs: `--label`

Every development run has the release value `dev`, so without labels all of them merge into one "dev" entry in the Run menu. Put `--label <text>` after a `--data` folder to name the run it holds. Folders with the same label merge into one entry, so one run's `apps` and `tasks` results can come from different folders, and different commits:

```powershell
node benchmarks\dashboard\build.mjs `
  --data <run2>\trial-files --label "Run 2 (Nov 5-9)" `
  --data <run3>\apps --label "Run 3 (Nov 10-12)" `
  --data <run3>\tasks --label "Run 3 (Nov 10-12)" `
  --out $env:TEMP\winapp-dash
```

- `--label` applies to the `--data` just before it. A `--data` without a label is listed under its release folder name, as before.
- The Run menu lists the most recent run first (by the latest run start time) and opens on it.
- Under Details, "How we measured" lists each benchmark's commits, winapp versions and dates for the selected run, so a run that mixes commits shows them.

If a release folder has an `index.json` (written by `publish-results.ps1`), the build checks that its trial count matches the files it read. The numbers always come from the trial files, because `index.json` does not include the individual checks.

The build skips a file and lists it under "Data notes" in Details when the file is not valid JSON, is missing a required field, or repeats a run that was already read. Fields the dashboard doesn't know are ignored, so newer files still build.

Use `--raw-data-url` to change where the "raw data" link points (default: the `benchmark-data` branch on GitHub).

## Output

| File | Contents |
|---|---|
| `index.html`, `app.js`, `styles.css` | The page. Plain HTML, CSS and JavaScript, with charts drawn as inline SVG |
| `data.json` | Every number the page shows, per release |
| `data.js` | The same data, loaded by `index.html` so it works from disk, where browsers block `fetch()` |

## How the numbers are calculated

All of this is in `lib\aggregate.mjs`.

- **Setups.** `barebones` is shown as "Without our tools", `cli-only` as "winapp CLI only", and `cli-plus-skills` as "With winapp + skills", with the id underneath. Every "change" is `cli-plus-skills` minus `barebones`. The Skills tab shows only `cli-plus-skills`, because the other setups have no skills installed.
- **Verdict.** The Overview sentence names whichever of quality checks met, requirements met, or built and launched changed most, says on how many models it moved the same way, and adds the change in credits per app. It uses no thresholds.
- **Built and launched** (`apps`). Runs where both the `builds` and `launches` checks passed, out of all runs, including ones that are not valid.

- **Valid runs only.** Pass rates and averages count `valid` runs. Every table says "n valid of n total", and the Outcomes tab lists every other run.
- **Debugging configurations.** The `tasks` configurations `winapp-skills`, `winui-skills` and `full-suite` are shown after the three setups and labeled as debugging.
- **Outcomes.** Only `valid` counts as valid. `harness-error` and every other outcome count in the total and are listed under Details.
- **Launch level** (`apps`). Counted per setup and model under Details. A file without `launchLevel` is shown as "not measured".
- **apps metrics.** Pass rate: every `required` check passed. Requirements: judge checks named `requirement-N`. Quality checks: every other `improvement` check. Design score: the judge's 0-10 score, shown as detail. Credits: `usage.nanoAiu / 1e9`. Minutes: `usage.wallClockMs`.
- **Confidence intervals.** Pass rates show a 95% Wilson interval.
- **Sanity checks.** A scenario or task that the `barebones` arm passes in 90% or more of valid runs is a sanity check (#1253). For tasks (under Details) these are left out of the headline pass rate, and the table shows both numbers. Apps scenarios are flagged but not removed, because in the first runs (November 2026) every apps scenario met the rule, and removing them would have left no headline.
- **Flags.** A model is flagged on a scenario when its pass rate differs from the other models' by 50 points or more, or when it uses more than 1.5x the credits it used on the other arms of that scenario. A scenario no setup passes is flagged as a possible checker bug.
- **Skills.** Counted in all runs, whatever the outcome. Runs whose file has no `skills` field are reported as "not recorded" rather than "0".
- **What changed with skills.** For each quality-check topic (for example Packaging = checks `p2` and `p3`), the pass rate in valid `apps` runs for `barebones` and `cli-plus-skills`, and the change. The topic list and related skills are `QUALITY_TOPICS` in `lib\aggregate.mjs`. This compares setups, not skills: a skill loading in a run is not evidence it helped.
- **Skill claims.** Every skill shows "Not enough data" until the claim rules in #1182 are decided.
- **Trend.** Shown once two `vX.Y.Z` releases exist. `dev` runs are never part of it.

## Tests

```powershell
cd benchmarks\dashboard
npm test
```

The tests use the small synthetic files in `test\fixtures\data`. Real results are not committed to this repository.

## Not done yet

- Deploying to GitHub Pages. Once the `benchmark-data` branch exists, a workflow should check it out, run `build.mjs --data <checkout> --out <site>`, and publish the site on every merged data PR.
- Tuning and held-out pass rates side by side (#1182). Every run so far is in the tuning set.
- Per-skill claim sentences, once the rules in #1182 are agreed.
- The `agents` and `ui` benchmarks (#1251 comments). They use other setups and formats.
