# Benchmark results format

Each benchmark trial (one agent attempt: one scenario, model and arm) writes one
small JSON file. The schema is [`results.schema.json`](results.schema.json).
An example is in [`examples\`](examples).

Release results are published to the `benchmark-data` branch, one folder per
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
| `benchmark` | `apps` or `tasks` |
| `scenario`, `model`, `trial` | What ran. `trial` counts repeats, starting at 1 |
| `set` | `tuning` or `held-out` |
| `arm` | `none`, `cli-only` or `cli-plus-plugins` |
| `startedAt` | When the trial started (UTC) |
| `passed` | `true` when every `required` check passed |
| `improvements` | `{ met, total }` for `improvement` checks |
| `checks` | Every check that ran (below) |
| `judge` | `apps` only: judge model and design score (0-10). Detail only |
| `usage` | Tokens, AI credits and time (below) |
| `skills` | Optional: plugin skills the agent loaded, in order, with `atMs` after start |
| `error` | Set only when the harness failed, not the agent |

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

Runs that did not reach a checked result (harness error, no usage data) write no file.
`skills` and `winappCommands` hold how many times each skill was loaded and each `winapp`
command was run in the trial; they are omitted when the harness could not read them.

## Publishing a release

Everything above stays on the machine that ran it. Only one deliberate step publishes:
the release owner runs the benchmark on the chosen release commit, then:

```powershell
pwsh benchmarks\publish-results.ps1 -Path .\<run folder>             # preview: stages files, touches nothing else
pwsh benchmarks\publish-results.ps1 -Path .\<run folder> -OpenPr     # opens a PR into benchmark-data
```

The script finds the trial files under `-Path`, names them as above, validates them and
writes `<release>\index.json`: one row per trial and one summary per
scenario, arm and model (pass count, improvements met, median credits and time, skill and
command counts), so a reader loads one file instead of hundreds.

`-OpenPr` refuses unless the release is `vX.Y.Z` (not `dev`), every file was measured on
the same commit, the working tree is clean and checked out at that commit, and the release
is not already on `benchmark-data`. It pushes `results/<release>` and opens a PR whose base
is `benchmark-data`, not `main`. A second person reviews the numbers; merging is the publish.
Protect `benchmark-data` so that a merged PR is the only way to change it.

## Rules the validator enforces

Besides the schema: `passed` and `improvements` must match `checks`, check ids are
unique, the file name matches its fields, and the folder name matches `release`.
