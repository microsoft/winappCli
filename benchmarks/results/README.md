# Benchmark results format

One JSON file per trial (one agent attempt at one scenario). Both benchmarks, [`tasks`](../tasks/README.md)
and [`apps`](../apps/README.md), write this format. Dashboards read these files; they do not parse
file names.

```json
{
  "schemaVersion": 1,
  "benchmark": "tasks",
  "release": "v0.7.1",
  "commit": "3f2a9c1e7b4d5a60918c2e0f6d7b8a1c4e5f9023",
  "set": "tuning",
  "scenario": "winforms-msix",
  "arm": "cli-plus-plugins",
  "model": "claude-sonnet-5.5",
  "trial": 1,
  "startedAt": "2026-10-20T14:03:11Z",
  "status": "completed",
  "passed": true,
  "result": {
    "summary": { "requiredMet": 2, "requiredTotal": 2, "improvementsMet": 0, "improvementsTotal": 1 },
    "checks": [
      { "id": "msix-built", "required": true, "passed": true, "detail": "C:\\src\\TaskBoard\\bin\\TaskBoard.msix" },
      { "id": "msix-trusted", "required": false, "passed": false, "detail": "the certificate is not in a trusted store" },
      { "id": "launches", "required": true, "passed": true, "detail": "TaskBoard.exe started from the package" }
    ]
  },
  "cost": {
    "tokens": { "input": 1412873, "output": 18204, "cacheRead": 1190442 },
    "aiCredits": 14.2,
    "premiumRequests": 1,
    "wallClockMs": 427000
  }
}
```

(Shortened; the full files are in [`examples\`](examples). The values are illustrative, not measured.)

Check files with:

```powershell
pwsh benchmarks\results\Test-TrialFiles.ps1 -Path benchmarks\results\examples
```

It exits 1 if any file is invalid. Tests: `Invoke-Pester benchmarks\results\tests`.

## Where files live

| What | Where |
|---|---|
| Schema, this page, examples, validator | default branch, `benchmarks\results\` |
| Release data | `benchmark-data` branch, one folder per release, flat files inside: `v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json` |
| Development trials | same format, kept on your machine |

The file name is for people. Every file repeats `release`, `commit`, `model`, `arm`, `scenario` and
`trial`, so grouping uses the fields.

## Fields

| Field | Meaning |
|---|---|
| `schemaVersion` | Always `1`. |
| `benchmark` | `tasks` or `apps`. Decides the shape of `result`. |
| `release` | Release tag measured. `null` for a development trial. |
| `commit` | Full SHA of the winapp commit measured. |
| `set` | `tuning` (scenarios used to improve the skills) or `held-out` (not used for tuning). |
| `scenario` | Task id (`tasks`) or scenario name (`apps`). |
| `arm` | `none`: no winapp CLI, no plugins. `cli-only`: CLI on PATH, no plugins. `cli-plus-plugins`: CLI and both plugins. |
| `model` | Model the trial asked for. `harness.modelsUsed` lists what actually ran. |
| `trial` | 1, 2, 3… for repeats of the same release, scenario, arm and model. |
| `startedAt` | UTC time the trial started. |
| `harness` | Optional. `configName` is the harness's own arm name (`both`, `published`), plus `copilotVersion`, `judgeModel` (`apps`) and `modelsUsed`. |
| `status` | `completed`, or `invalid` when the harness could not judge the trial (sandbox failure, checker crash, wrong skills installed). Invalid trials have `passed: null`, an `invalidReason`, and are not counted. |
| `passed` | See below. |
| `result` | Every check, not a score. See below. |
| `skillsUsed` | Optional. Skills the agent invoked, each with an optional `at` time. |
| `cost` | See below. Every field is optional. |

There is no 1 / 0.5 / 0 score. A partial result is a fail; the checks show how far it got.

### Pass or fail

Every check is either **required** or an **improvement**.

- A trial passes (`passed: true`) when every required check passed.
- A failed improvement does not fail the trial. It lowers `result.summary.improvementsMet`.
- `result.summary` is derived from the checks: `requiredMet`, `requiredTotal`, `improvementsMet`,
  `improvementsTotal`. Checks with status `na` are not counted.

There is no 1 / 0.5 / 0 score. A partial result is a fail; the checks show how far it got.

| | `tasks` | `apps` |
|---|---|---|
| Where | `result.checks`: `id`, `required`, `passed`, `detail`, and `guard` for a required check that only protects what the fixture already had. | `result.gates`, `result.qualityChecks`, `result.requirements`. Each has `required` and a `status` of `pass`, `fail` or `na`. |
| Required | Whatever the task marks required. | Every gate: `builds`, `launches`, and the gate checks (`G0` right framework, `G3` Native AOT). A scenario may also mark a requirement required. |
| Improvements | The other checks. | The harness quality checks, the judge's `Q5`, and requirements not marked required. |

`apps` also records `result.designScore` (0–10, from the judge). It is detail only and is not in the summary counts.

`Test-TrialFiles.ps1` checks that `passed` and `result.summary` agree with the checks.
### Cost

| Field | Meaning |
|---|---|
| `tokens.input` | Prompt tokens the main agent sent, summed over every model call. **Includes** `cacheRead`. |
| `tokens.output` | Tokens the main agent wrote. |
| `tokens.cacheRead` | The part of `input` served from cache. |
| `subAgentTokens` | Same three fields, for sub-agents the main agent started, summed. |
| `aiCredits` | AI credits the session used (Copilot reports nano-credits; this is that ÷ 1e9). |
| `premiumRequests` | Premium requests Copilot counted. |
| `wallClockMs` | Agent session, start to end, in milliseconds. Excludes sandbox start-up and checking. |

Omit a field the harness can't measure. Don't write `0` for unknown.

Files hold no dollar amounts and no price table. Token counts are exact, so readers can apply their own per-model prices.

## What each harness can fill today

Read from the code, not yet confirmed on a real run.

| Field | `tasks` | `apps` |
|---|---|---|
| `result` | Yes: `check.json` has every check with `required`, `passed`, `detail`. | Partly. `results.json` has `metrics.quality_checks`, `metrics.requirements` and `metrics.score_breakdown.design`. `builds` and `runs` are separate flags, and the gate checks are inside `quality_checks` (`kind: "gate"`). |
| `tokens` | Yes, as integers (`tokens.input`, `output`, `cacheRead`). | Strings rounded to one decimal (`"1.9m"`, `"41.0k"`). Needs integers. |
| `subAgentTokens` | No. Main-session totals only; not confirmed whether sub-agent usage is in them. | Yes, as rounded strings, per model. |
| `aiCredits` | Yes. | Yes (`credits`). |
| `premiumRequests` | Yes. | Yes. |
| `wallClockMs` | Yes (`durationMs`, agent phase only). | Text such as `"18m 3s"` (`session_time`). Needs milliseconds. |
| `harness.*` | `copilotVersion`, `configName`, `modelsUsed` partly (selected model). | `judgeModel`, `modelsUsed`, `configName` (the condition). |
| `commit`, `release` | Not recorded per run. `run-info.json` has the plugin SHAs and winapp version. | `agent_source.sha` for repo-based setups, and `toolchain.winapp`. |
| `skillsUsed` | Names only, no times (`skillsLoaded`). | **Not recorded.** `results.json` has no skill-use field. `agent_source.skills` lists the skills a plugin shipped, not the ones used. |
| `arm` | See below. | See below. |

### Arms don't match the harnesses yet

- `tasks`: `winapp` is on PATH in every configuration, including `none`. Its `none` is `cli-only`
  here, and `both` is `cli-plus-plugins`. There is no `none` arm without the CLI.
- `apps`: `none` hides `winapp` from PATH, so it is the real `none`. `published` and `winappcli`
  both load plugins, so they map to `cli-plus-plugins`. There is no `cli-only` arm.

Until the harnesses add the missing arms, set `arm` by the mapping above and keep the harness's own
name in `harness.configName`.

## Changing the format

Bump `schemaVersion` for any change that makes an old file invalid. Adding an optional field does not
need a bump. Update `trial.schema.json`, the examples, this page, and the tests together.
