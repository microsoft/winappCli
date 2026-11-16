# Benchmark results format

Each benchmark trial (one agent attempt: one scenario, model and arm) writes one
small JSON file. The schema is [`results.schema.json`](results.schema.json).
An example is in [`examples\`](examples).

Release results go in one folder per
release, flat files inside:

```text
v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json
       <scenario>__<arm>__<model>__<trial>.json
```

Development trials use the same format with `"release": "dev"` and stay local.

Validate files with:

```powershell
pwsh benchmarks\validate-results.ps1 -Path .\v0.7.1
```

## Fields

| Field | Meaning |
|---|---|
| `schemaVersion` | `1` |
| `release`, `commit` | winapp release (`v0.7.1` or `dev`) and the full commit SHA tested |
| `winappVersion` | Optional. `winapp --version` of the CLI the trial ran, such as `0.7.1+<sha>`. It can differ from `commit` when the CLI was built from another commit |
| `benchmark` | `apps` or `tasks` |
| `scenario`, `model`, `trial` | What ran. `trial` counts repeats, starting at 1 |
| `set` | `tuning` or `held-out` |
| `arm` | `none`, `cli-only` or `cli-plus-plugins` |
| `startedAt` | When the trial started (UTC) |
| `outcome` | Why the trial ended (below). Only `valid` trials count toward pass rates |
| `passed` | `true` when every `required` check passed |
| `improvements` | `{ met, total }` for `improvement` checks |
| `checks` | Every check that ran (below) |
| `judge` | `apps` only: judge model and design score (0-10). Detail only |
| `usage` | Tokens, AI credits and time (below) |
| `skills` | Optional: plugin skills the agent loaded, in order, with `atMs` after start |
| `error` | Set only when the harness failed, not the agent |
| `scoringVersion` | Optional. Which scoring rules produced the result. Results with different versions are not directly comparable |
| `launchLevel` | Optional, `apps` only. 0 did not run, 1 a window exists, 2 controls respond to input, 3 the scenario's named main-screen element appeared. Left out when the app ran but level 2 could not be measured, for example because UI input could not be sent |
| `requirementsNotRun` | Optional, `apps` only. Requirement numbers the judge did not rule on. They are not counted as failures and have no entry in `checks` |

## Outcome

`outcome` separates a real result from a trial that never produced one, so a harness problem is never counted as a model failure.

| `outcome` | Meaning |
|---|---|
| `valid` | The agent finished and the checks ran. Pass or fail is a real result |
| `app-failed` | The app built but crashed or did not run. Shown as a "works rate" beside the score |
| `build-failed` | No app was produced |
| `timed-out` | The agent ran out of time |
| `harness-error` | Our tooling failed (locked desktop, judge never ran). `error` is set. Excluded from pass rates and rerun |
| `asked-before-admin` | `tasks` only: the agent stopped to ask for administrator approval even though the prompt gave it. Shown as its own count, never a pass |

Headline numbers use `valid` trials only and show "n valid of n total". Any outcome other than `valid` has `passed: false`.

## Checks

There is no single score. Each check is `required` or an `improvement`:

- If any `required` check fails, the trial fails.
- If an `improvement` check fails, the trial still passes. It only lowers `improvements.met`.

```json
{ "id": "msix-trusted", "kind": "improvement", "passed": true, "source": "harness", "detail": "optional text" }
```

`source` is `harness` (deterministic) or `judge` (LLM).

| Benchmark | `required` | `improvement` |
|---|---|---|
| `tasks` | Checks marked required in the task | The other checks. A partly done task is not a pass |
| `apps` | Gates: builds, launches, right framework, Native AOT | Quality checks and judge-checked requirements. A scenario may mark a requirement as required |

## Usage

```json
"usage": {
  "tokens": { "input": 412000, "output": 18500, "cacheRead": 380000 },
  "subAgentTokens": null,
  "nanoAiu": 5230000000,
  "premiumRequests": 1,
  "wallClockMs": 612000
}
```

- No dollar costs and no price table. `nanoAiu` is the AI credits Copilot CLI
  reports, as an exact integer (1 AIU = 1,000,000,000). It is `null` if not reported.
- `subAgentTokens` is separate from `tokens`.

## Where the harnesses write them

| Harness | File | Notes |
|---|---|---|
| `tasks` | `<run dir>\trials\<scenario>__<arm>__<model>__<trial>.json` | `release` is the pinned release when run with `-Winapp release`, otherwise `dev`. `set` comes from `"set"` in `task.json` (default `tuning`) |
| `apps` | `<trial dir>\trial-result.json` | `release` comes from the `BENCH_RELEASE` environment variable (default `dev`). `set` and `required_requirements` (1-based numbers) come from the scenario config. Setups other than the three arms, such as `published`, write no file |

`apps` writes `harness-error`, `timed-out`, `build-failed` and `app-failed` outcomes. Runs with no usage data still write no file, and `tasks` writes only `valid` and `asked-before-admin`.
`skills` and `winappCommands` hold how many times each skill was loaded and each `winapp`
command was run in the trial; they are omitted when the harness could not read them.

## Rules the validator enforces

Besides the schema: `passed` and `improvements` must match `checks`, check ids are
unique, the file name matches its fields, and the folder name matches `release`.
