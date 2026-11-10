# Benchmark results dashboard

A static site that shows the `apps` and `tasks` benchmark results: a whole-plugin headline, per-scenario tables, every outcome, which skills loaded, and how we measured. It reads the per-run JSON files described in [RESULTS-FORMAT.md](../RESULTS-FORMAT.md) and needs only Node 20 or later. There are no packages to install.

## Build and preview

```powershell
node benchmarks\dashboard\build.mjs --data C:\path\to\trial-files --out $env:TEMP\winapp-dash
start $env:TEMP\winapp-dash\index.html
```

The build prints what it found:

```text
dev: 495 trials (apps 243, tasks 252)
Wrote C:\Users\you\AppData\Local\Temp\winapp-dash\index.html
```

`index.html` opens straight from disk. To serve it over HTTP instead, run `npx serve $env:TEMP\winapp-dash`.

The page remembers the release and tab in the address, so `index.html#release=dev&tab=tasks` opens the Tasks tab for `dev` runs.

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

If a release folder has an `index.json` (written by `publish-results.ps1`), the build checks that its trial count matches the files it read. The numbers always come from the trial files, because `index.json` does not include the individual checks.

The build skips a file and lists it under "Data notes" on the How we measured tab when the file is not valid JSON, is missing a required field, or repeats a run that was already read. Fields the dashboard doesn't know are ignored, so newer files still build.

Use `--raw-data-url` to change where the "raw data" link points (default: the `benchmark-data` branch on GitHub).

## Output

| File | Contents |
|---|---|
| `index.html`, `app.js`, `styles.css` | The page. Plain HTML, CSS and JavaScript, with charts drawn as inline SVG |
| `data.json` | Every number the page shows, per release |
| `data.js` | The same data, loaded by `index.html` so it works from disk, where browsers block `fetch()` |

## How the numbers are calculated

All of this is in `lib\aggregate.mjs`.

- **Valid runs only.** Pass rates and averages count `valid` runs. Every table says "n valid of n total", and the Outcomes tab lists every other run.
- **Arms.** `barebones`, `cli-only` and `cli-plus-skills`, as recorded. The `tasks` debugging configurations (`winapp-skills`, `winui-skills`, `full-suite`) are shown after them and labeled as debugging, not part of the comparison.
- **Outcomes.** Only `valid` counts as valid. `harness-error` and every other outcome count in the total and are listed on the Outcomes tab.
- **Launch level** (`apps`). Counted per arm and model on the Outcomes tab. A file without `launchLevel` is shown as "not measured".
- **apps metrics.** Pass rate: every `required` check passed. Requirements: judge checks named `requirement-N`. Quality checks: every other `improvement` check. Design score: the judge's 0-10 score, shown as detail. Credits: `usage.nanoAiu / 1e9`. Minutes: `usage.wallClockMs`.
- **Confidence intervals.** Pass rates show a 95% Wilson interval.
- **Sanity checks.** A scenario or task that the `barebones` arm passes in 90% or more of valid runs is a sanity check (#1253). On the Tasks tab these are left out of the headline pass rate, and the table shows both numbers. On the apps tabs they are flagged but not removed, because in the first runs (November 2026) every apps scenario met the rule, and removing them would have left no headline.
- **Flags.** A model is flagged on a scenario when its pass rate differs from the other models' by 50 points or more, or when it uses more than 1.5x the credits it used on the other arms of that scenario. A scenario no setup passes is flagged as a possible checker bug.
- **Skills.** Counted in all runs, whatever the outcome. Runs whose file has no `skills` field are reported as "not recorded" rather than "0".
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
