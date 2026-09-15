# Pipelines

| Pipeline | File | Trigger | Purpose |
|----------|------|---------|---------|
| WinDevCLI - CI | [`ci.yml`](ci.yml) | `main`, `dev/**` | Prerelease build and tests on every push. |
| WinDevCLI - Release | [`release.yml`](release.yml) | `rel/v*` **and** a weekly schedule on `main` | Ships a release from `rel/v*`; rehearses one every Monday from `main`. |
| WinDevCLI - Fuzz | [`fuzz.yml`](fuzz.yml) | Manual | Submits the OneFuzz job. |

Shared step templates live in [`templates/`](templates):

- [`build-env.yaml`](templates/build-env.yaml) — agent setup (.NET, Node, internal NuGet/npm feeds and their auth). Shared by CI and release.
- [`build.yaml`](templates/build.yaml) — the build, packaging, optional ESRP signing, and artifact publishing.
- [`release-assets.yaml`](templates/release-assets.yaml) — renames built packages to the unversioned asset names the release publishes. Used twice: as a preflight dry-run on a copy in the `Build` stage (where a checkout exists, so the result can be verified by a tested script), and for real in `Release_GitHub` (which has no checkout).

> **Why templates and not scripts for release jobs.** 1ES release jobs
> (`templateContext.type: releaseJob`) cannot check out the repo, so no `.ps1` from the working
> tree is on disk there. A YAML `- template:` reference is fine regardless, because it is expanded
> at **compile** time. Share release-job logic as YAML, never as a script file.

---

## Publishing a prerelease of the library packages

The three source-built library packages — `Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation`,
`...UIAutomation.Recording`, and `...WinUIAnalyzer` — can be published to nuget.org as a prerelease
**without cutting a release**, to validate them before the first official release.

Queue **WinDevCLI - Release** manually with the **`publishPrereleaseNugets`** parameter ticked
(optionally set `prereleaseNugetVersion`; blank auto-computes `version.json` + `-prerelease.<build>`).
That adds two stages — `Prerelease_Nugets_Build` and `Prerelease_Nugets_Publish` — which pack just
those three packages (`package-nuget.ps1 -SkipCliPackage`, so the CLI tools package is never built),
ESRP-sign them, and push only them via the `NuGet-WinAppCLI` service connection.

Both stages are gated on the parameter, which **defaults false**, so they are pruned at *compile*
time on every scheduled rehearsal and every real `rel/v*` release — they are absent from the graph
unless someone explicitly ticks the box. A second positive gate, `not(startsWith(Build.SourceBranch,
'refs/heads/rel/v'))`, means even a manual queue against a release branch publishes nothing, so this
can never ride a real release. This is the one deliberate exception to the "mode from branch, not
parameter" rule below: it is not a discriminator between two automatic behaviours (which parameter
defaults would betray), but an additive, default-off, manual action.

Notes:

- Signing is required: the stages are also gated on `DoEsrp` (on by default), so they can never
  publish unsigned packages. Unticking `DoEsrp` prunes the path entirely — nothing publishes.
- The version must be a SemVer prerelease (e.g. `0.6.3-prerelease.1`); a stable version, including
  one with build metadata like `0.7.0+build-1`, is rejected. Blank auto-computes the prerelease.
- Run it from a **non-`rel/v*`, non-`main`** branch to also keep the `main`-gated rehearsal stages
  (WinGet, MS Learn, credentials) out of the run. The unconditional `Build` stage still runs
  regardless — that is the cost of folding this into the release pipeline rather than a separate one.
- The push runs as `NuGet-WinAppCLI`, which owns the `Microsoft.Windows.SDK.BuildTools.*` reserved
  prefix, so no personal namespace ownership is needed. The run branch must be allowed by the Branch
  control checks on `NuGet-WinAppCLI` and the signing connection.
- A successful push is an immutable nuget.org publish — bump the prerelease number per attempt.

---

## The weekly release rehearsal

### Why

ES and 1ES policies change continuously, and we kept discovering which ones broke us **while a
release was in flight** — the most expensive possible moment. The rehearsal moves that discovery
to a Monday morning.

### How it works

There is no separate dry-run pipeline. `release.yml` runs **the real release** every Monday from
`main`, with only the final publishing actions omitted. Same 1ES Official template, same stages,
same jobs, same tasks, same service connections. A lookalike pipeline could only ever approximate
those; this *is* them.

**The mode is derived from the branch, never from a parameter.** This is the load-bearing detail:

```yaml
${{ if startsWith(variables['Build.SourceBranch'], 'refs/heads/rel/v') }}:
```

Azure DevOps compiles both scheduled **and** branch-triggered runs with the **default** values of
`parameters:`. A `dryRun` parameter therefore could not distinguish a Monday rehearsal from a real
release — defaulting it `false` would make the weekly run publish for real, and defaulting it
`true` would make every real release silently ship nothing. The branch can tell them apart.

Every gate is written as a **positive** test for `rel/v*`, so an unexpected branch fails **closed**.
Grep `refs/heads/rel/v` in `release.yml` to find all of them.

### Why this runs on the Official template

A rehearsal that publishes nothing looks like it belongs on `1ES.Unofficial.PipelineTemplate.yml`,
and that was the original design. It is deliberately **not** what this does.

Unofficial enforces a weaker SDL subset — PoliCheck runs in both, but TSA upload, CodeQL TSA
integration, SBOM enforcement and signing validation are Official-only. Those are exactly the
policies that change under us, so a rehearsal on Unofficial would be blind to the failures it
exists to catch. Staying on Official is the difference between rehearsing the release and
rehearsing something that resembles it.

Nothing is signed (`DoEsrp` is ANDed with the branch) and nothing is published, so a rehearsal
produces no production binaries despite running the Official template.

Do not "fix" this by switching to Unofficial.

### What the rehearsal skips

Exactly seven things, each gated:

| Action | Gate |
|---|---|
| ESRP code signing | `DoEsrp` ANDed with the branch |
| GitHub release | compile-time `${{ if }}` |
| Symbol publication | whole stage omitted |
| npm publish (ESRP Release) | whole stage omitted |
| nuget.org push (`Release_NuGet`) | whole stage omitted |
| WinGet fork sync + `--submit` | `WINGET_SUBMIT` env, `'false'` unless `rel/v*` |
| MS Learn push + PR | `MSLEARN_PUBLISH` env, `'false'` unless `rel/v*` |

Where a step is a shell script rather than a task, the gate is a **YAML-conditional environment
variable** that the script branches on. The condition stays at compile time, and the variable is
always defined — explicitly `'false'` in a rehearsal — so the script cannot inherit a stray
`WINGET_SUBMIT` from the agent environment. Only the literal `'true'` publishes.

### What the rehearsal still does

Everything else, for real:

- The full stable build for x64 and ARM64 with telemetry unstubbed (`Unstub.ps1`), plus every test
  suite, under the same `Permissive,CFSClean` isolation the release uses.
- MSIX, NuGet and npm packaging.
- Release-notes generation. This step cannot fail: `generate-release-notes.ps1` catches its own
  API failures and falls back to a git-log changelog. **GitHub Models was retired on 2026-07-30
  and now returns 410 for every token**, so the AI-summarised path is permanently dead and every
  release ships the git-log fallback. There is deliberately no credential check for it — a probe
  that can never pass would just turn the Monday build red forever.
- The real `Release_GitHub` job — artifact download, zip archiving, asset renaming — right up to
  the `GitHubRelease@1` call.
- **WinGet:** installs `wingetcreate`, verifies fork access, downloads the installers and generates
  the manifest. Only `--submit` is skipped. Because no release exists yet for the in-flight
  version, it rehearses against the **last published release**, so the download and hashing are
  real — that is where #568 broke.
- **MS Learn:** clones the fork, runs the port and validation scripts against this week's docs,
  prunes date-only churn, and commits locally. Only the push and PR are skipped — that is where
  #685 and #777 broke.
- Asset-name preflight. This one runs on **every** build, release or rehearsal: the packages are
  renamed on a copy and checked by `scripts/verify-release-assets.ps1` against the exact names the
  WinGet URLs and documented download links hardcode. The real rename happens later in
  `Release_GitHub`, which has no checkout and so cannot run the verifier — doing it here means a
  naming regression fails the build instead of silently publishing wrong URLs (#568).
- Credential and service-connection checks via `scripts/check-release-credentials.ps1` — PAT scopes
  and expiry, fork push permission, and service-connection readiness.

### Limitations

- **Signing is never exercised.** ESRP is off in a rehearsal, so a signing-side break still
  surfaces only during a real release.
- **Connection existence is not credential validity.** `github-service-connection` and
  `NuGet-WinAppCLI` are checked for existence and readiness only; nothing authenticates the secret
  inside them without publishing.
- **The publish calls themselves never run.** The rehearsal validates their preconditions, not the
  final API call.

### Setup

The schedule is defined in YAML and needs **no new pipeline** — it runs on the existing
**WinDevCLI - Release** definition, which already has its variable groups and service connections
authorized. That is the main practical advantage of this design.

Two things to check in the ADO UI:

1. **"Override the YAML schedule" must be off**, or the weekly trigger will not fire.
2. Any **Branch control** check on the shared service connections must allow **both**
   `refs/heads/rel/v*` and `refs/heads/main`. A `rel/v*`-only filter would block the rehearsal;
   a `main`-only filter would block real releases.

`always: true` on the schedule is deliberate: without it a quiet week produces no run, and a quiet
week is exactly when an external policy change slips in unnoticed.

### Triaging a failure

| Failing step | Usually means |
|---|---|
| Build CLI | An internal feed, package or SDK dependency changed, or a real code break. Compare against the last green CI run. |
| Replace Stubbed Files | The internal telemetry package or its feed permissions changed. CI does not run this. |
| Generate Release Notes | `GITHUB_TOKEN_2` expired or lost access. Cannot fail the build — it falls back to a git-log changelog. |
| `Preflight - assert asset name contract` | Package naming changed, or an architecture stopped building. Fix `templates/release-assets.yaml` — `Release_GitHub` uses the same copy. Note this gate is **not** rehearsal-only; it fails real releases too, by design. |
| `[Rehearsal] Check release credentials` | Read the PASS/WARN/FAIL summary. `FAIL` is definitive; `WARN` means the check could not determine an answer. |
| WinGet (rehearsal path) | `wingetcreate`, the installer downloads, or the manifest schema changed. A real submission would fail the same way. |
| MS Learn (rehearsal path) | A doc edit landed that the port or validation script rejects. |

### Running it on demand

Queue **WinDevCLI - Release** against `main` for a full rehearsal. Queueing against `rel/v*` is a
real release.

Any other branch gets **only** the build and the asset preflight — `Release_WinGet`,
`Release_MSLearn` and `Verify_Release_Credentials` are all restricted to `main`, and no step is
handed `GITHUB_TOKEN_2`. That is deliberate, but it means a green run on a feature branch is *not*
evidence the release path works. Use `main` for that.

The branch conditions are defense in depth, not a security boundary — the branch being run could
edit them. The authoritative control is the Branch control check on the service connections
described above.

The script also runs locally:

```powershell
$env:GH_TOKEN = 'ghp_...'
.\scripts\check-release-credentials.ps1 `
    -WingetPkgsFork '<owner>/winget-pkgs' `
    -MSLearnDocsFork '<owner>/windows-dev-docs-pr'
```

Service connection checks are skipped outside Azure Pipelines, so this runs fine offline.

### Related

- `scripts/check-release-credentials.ps1` (+ tests in `scripts/tests/`)
- `scripts/verify-release-assets.ps1` (+ tests in `scripts/tests/`)
- `.pipelines/templates/release-assets.yaml` — the renaming, shared with the real release

Both test suites run as part of `scripts/build-cli.ps1`, and are deliberately **offline-only**:
that suite also runs during a real release build, so a test that reached `api.github.com` would let
a GitHub outage block a release.

> **Planned (phase 2):** a scheduled agent prompt that reads each weekly run and posts a summary to
> the team.
