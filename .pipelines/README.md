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
- GitHub credential checks via `scripts/check-release-credentials.ps1` — PAT scopes and expiry,
  and fork push permission.
- **The two federated service connections, exercised for real.** `AzureCLI@2` acquires a token
  from the ESRP signing connection and from the symbol publishing connection. Nothing is signed
  and nothing is published, but it proves the connection exists, this pipeline is authorized to
  use it, **and the workload-identity federation credential still works** — the part that silently
  rotates or gets de-authorized under ES policy changes.

### Limitations

- **Signing is never exercised.** ESRP is off in a rehearsal, so a signing-side break still
  surfaces only during a real release.
- **Two connections cannot be verified at all.** `github-service-connection` is consumed only by
  `GitHubRelease@1`, whose every action mutates, and `NuGet-WinAppCLI` carries an API key that is
  only validated on push. There is deliberately no check for them — a metadata lookup would prove
  nothing about the credential, and it is better to say so than to fake coverage. They are covered
  the moment you cut a real release.
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

No extra permission grant is needed. An earlier version of this check read the service endpoints
over REST, which required the build identity to have `View Service Connection` — a permission
nothing else needs, and which it does not have. That produced four false "Not found" failures for
two weeks running (builds 20260907.1 and 20260914.1) against connections that were all present and
authorized.

> **Why using a connection beats inspecting it.** Pipelines are authorized to *use* a service
> connection through pipeline authorization, which is separate from the ACL that governs *reading*
> it over REST. So releases can work perfectly while the endpoint list comes back empty — the two
> are unrelated, and the empty list means nothing.
>
> More importantly, no amount of metadata tells you whether a connection's credential still works.
> Acquiring a token does. That is why the ESRP and symbol-publishing connections are now exercised
> directly in `release.yml` rather than looked up.

`always: true` on the schedule is deliberate: without it a quiet week produces no run, and a quiet
week is exactly when an external policy change slips in unnoticed.

### Triaging a failure

| Failing step | Usually means |
|---|---|
| Build CLI | An internal feed, package or SDK dependency changed, or a real code break. Compare against the last green CI run. |
| Replace Stubbed Files | The internal telemetry package or its feed permissions changed. CI does not run this. |
| Generate Release Notes | `GITHUB_TOKEN_2` expired or lost access. Cannot fail the build — it falls back to a git-log changelog. |
| `Preflight - assert asset name contract` | Package naming changed, or an architecture stopped building. Fix `templates/release-assets.yaml` — `Release_GitHub` uses the same copy. Note this gate is **not** rehearsal-only; it fails real releases too, by design. |
| `Check release credentials` | Read the PASS/WARN/FAIL summary at the end of the log. `FAIL` is definitive; `WARN` means the check could not determine an answer and is not actionable on its own. A token-expiry `WARN` is the one worth acting on early — the PAT is regenerated by hand. |
| `Verify ESRP signing connection (token only, signs nothing)` / `Verify symbol publishing connection (token only, publishes nothing)` | The federated identity behind that connection no longer works — rotated, expired, or de-authorized for this pipeline. A real release would fail at signing or symbol publishing. Nothing was signed or published by the check itself. Both steps run even if an earlier check failed, so treat their verdicts independently. |
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

This command checks GitHub credentials only — the federated service connections are exercised by
the pipeline steps above, so it needs no Azure access and runs fine offline.

### Related

- `scripts/check-release-credentials.ps1` (+ tests in `scripts/tests/`)
- `scripts/verify-release-assets.ps1` (+ tests in `scripts/tests/`)
- `.pipelines/templates/release-assets.yaml` — the renaming, shared with the real release

Both test suites run as part of `scripts/build-cli.ps1`, and are deliberately **offline-only**:
that suite also runs during a real release build, so a test that reached `api.github.com` would let
a GitHub outage block a release.

> **Planned (phase 2):** a scheduled agent prompt that reads each weekly run and posts a summary to
> the team.
