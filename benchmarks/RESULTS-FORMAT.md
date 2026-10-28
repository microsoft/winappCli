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
`apps` does not record skill usage yet, so it omits `skills`.

## Rules the validator enforces

Besides the schema: `passed` and `improvements` must match `checks`, check ids are
unique, the file name matches its fields, and the folder name matches `release`.
