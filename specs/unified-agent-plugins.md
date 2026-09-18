# Unified authoring and publishing for two Windows agent plugins

**Status:** Proposed

**Date:** 2026-09-18

**Summary:** Maintain and publish the standalone WinApp and WinUI plugins from
`microsoft/winappCli`, sharing complete workflows without merging the products.

This is a design proposal, not installation guidance or approval to implement.
This PR changes no plugins, CLI behavior, dependencies, or publishing workflows.

## Problem and goals

Windows platform integration and WinUI development need overlapping guidance:
packaging, signing, identity, API discovery, and UI automation. Maintaining those
procedures separately invites inconsistent fixes and duplicated release work.
Large entrypoints also obscure which instructions a task actually needs.

Today, WinApp content is authored under `plugins\winapp`; WinUI is maintained in
`win-dev-skills`. WinApp's plugin versions are synchronized with the CLI by
`scripts\generate-llm-docs.ps1`, and its agent is roughly 55 KB.

Consolidate source, host adapters, tests, and publishing in this repository.
Keep two independently installable products:

| Product | Plugin ID | Agent where supported | Skills |
| --- | --- | --- | ---: |
| Windows platform integration | `winappcli` | `winapp` | 10 |
| WinUI application development | `winui` | `winui-dev` | 16 |

Neither requires the other. There is no merged plugin, third core dependency,
WinUI deprecation, or implied repository archival. After complete cutover,
`win-dev-skills` becomes historical/transition-only rather than an active source
or publishing dependency.

## Workflow membership

Author **18 unique workflows**: eight shared, two WinApp-only, eight WinUI-only.
Reuse each shared skill in full, not just fragments of overlapping procedures.
Retain these canonical IDs; host-specific generated names may be qualified.

### Both plugins: eight shared skills

| Skill ID | Purpose and completion boundary |
| --- | --- |
| `winapp-ui-automation` | Observe, target, interact with, and verify a running Windows UI; a single action is not a test suite. |
| `winapp-find-api` | Resolve Windows/WinRT types and members against project or SDK metadata, across frameworks. |
| `winapp-manifest` | Edit identity, capabilities, extensions, and icons; validate the manifest and assets. |
| `winapp-package` | Produce and verify MSIX/bundle output while preserving the existing build and packaging choices. |
| `winapp-signing` | Generate a requested certificate or sign an existing artifact; verify identity/signature, without implying trust or installation. |
| `winapp-identity` | Enable required package identity and verify run/debug activation for packaged, loose, or sparse applications. |
| `winapp-troubleshoot` | Diagnose a failed Windows operation from evidence, apply an authorized scoped repair, and retry. |
| `winapp-sandbox` | Run/test in the selected Windows Sandbox guest and return evidence while preserving host/guest isolation. |

### WinApp only: two skills

| Skill ID | Purpose and completion boundary |
| --- | --- |
| `winapp-setup` | Prepare, restore, or update an existing project's Windows SDK/configuration; stop when setup is ready. |
| `winapp-maui` | Publish/package the MAUI Windows target with its correct runtime identifier and generated manifest. |

### WinUI only: eight skills

| Skill ID | Purpose and completion boundary |
| --- | --- |
| `winui-setup` | Prepare/repair the machine's WinUI toolchain with consent, separate from routine project work. |
| `winui-dev-workflow` | Create/open, build, run, or diagnose a selected WinUI project through supported CLI/NuGet workflows. |
| `winui-design` | Implement/review WinUI controls, layout, bindings, themes, and accessibility. |
| `winapp-find-ui` | Find WinUI samples and prerequisites; adapt and verify them when requested. |
| `winui-code-review` | Report scoped, evidence-backed findings beyond compiler/analyzer diagnostics. |
| `winui-ui-testing` | Verify agreed behavior with assertions and evidence; report failures and gaps, using shared UI automation underneath. |
| `winui-wpf-migration` | Explicitly migrate agreed WPF components with behavior/build checkpoints. |
| `winui-session-report` | Analyze an explicitly selected agent session with privacy boundaries; retain its analysis script. |

Keep API discovery broader than WinUI sample lookup, testing above individual
automation actions, and design distinct from sample discovery.

## Content and dependency changes

Replace `winapp-frameworks` with conditional framework references and
`winui-packaging` with shared package/signing workflows plus WinUI/MSBuild
references. Move `winapp-find-ui` delivery to WinUI without renaming it.
Document explicit-invocation transitions before removing or moving entrypoints.

Each skill states its trigger, inputs, safe steps, and verification. Aim for
600-1,200 words, not a hard limit; keep critical consent, targeting, identity,
and failure rules inline. Move large examples and error catalogs into optional
references. Trim both agents into coordinators rather than command manuals;
do not require loading unrelated skills.

Preserve the chosen framework and build pipeline. Windows App SDK, XAML, or
.NET alone does not establish that a project is WinUI. Signing an existing file
must not trigger rebuilding or registration. Preserve C++ WinUI's supported
native build path; do not force a migration.

The WinUI target is the planned `winapp` workflow plus a NuGet analyzer, not an
assertion that replacement commands/packages are already released. Validate
the replacement before removing `BuildAndRun.ps1` or bundled analyzer DLLs.
Retain the session-analysis script as the only script in WinUI skill content.
Resolve remaining analyzer source/tests/package ownership and `winmd-cli`
consumer/parity requirements before transfer or retirement. Preserve licenses,
package/rule contracts, and help links; never import personal reports.

## Source and generated packages

Proposed layout; catalog field names and adapter schemas remain implementation
details:

```text
main
  agent-plugins\
    catalog.json                 Membership, pair version, dependencies
    content\shared\              Shared skills and references
    content\winapp\               Agent and WinApp-only skills
    content\winui\                Agent and WinUI-only skills
    adapters\                    Host-specific packaging
    scripts\                     Build, validation, publishing
    tests\                       Contract, lifecycle, workflow checks
  src\                           CLI and owned tool source

plugins-staging / plugins-production
  plugins\winapp\                 Generated standalone package
  plugins\winui\                  Generated standalone package
  host catalogs and skill selections
  release provenance
```

Both distribution branches live in this repository. **No generated output goes
in source PRs**; CI attaches package previews. Authors edit source, not generated
manifests. Keep one canonical agent body per product and generate host
representations.

Portable packages use immediate `skills\<skill-id>\SKILL.md` directories, with
all required references inside the package, following
[Agent Plugins 1.0](https://agent-plugins.org/specification).
Shared content must match within a release except for declared host
transformations. No runtime reads from the other plugin or mutable downloads
of required instructions.

Keep CLI schema generation but remove plugin-version synchronization from
`generate-llm-docs.ps1`. Adapt `validate-llm-docs.ps1`, `build-cli.ps1`,
package validators, and contributor instructions to the new ownership.
Plugin-only validation should not compile the CLI unless needed; the plugin
build packages guidance and declares dependencies, not CLI/analyzer binaries.

## Host delivery

**Baseline: register the marketplace directly from `plugins-production`, with
relative paths to packages on that branch.** Copilot, Claude Code, and Codex
support branch registration. Use the equivalent staging route with a distinct
catalog identity. A `main` forwarding catalog is optional, not required.
Generate each host's schema rather than inventing one universal marketplace JSON.

| Host | Proposed delivery and limits |
| --- | --- |
| [Copilot](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-plugin-reference) | Production-branch marketplace; preserve custom agents. Verify target CLI/app versions separately. |
| [Claude Code](https://code.claude.com/docs/en/discover-plugins#add-from-other-git-hosts) | Production-branch marketplace; preserve agents. Forwarding and archive routes are optional supported-version additions. |
| [Codex](https://developers.openai.com/plugins/build/plugins#add-a-marketplace-from-the-cli) | Production-branch marketplace and skills. Do not claim plugin-provided custom agent profiles under the inspected contract. |
| [OpenClaw](https://docs.openclaw.ai/plugins/bundles) | Production-ref catalog with relative paths, or downloaded release archive. Use a portable bundle, not a no-op native `index.js` wrapper. |
| [OpenCode](https://github.com/anomalyco/opencode/blob/5a8335857b0ebec44ef6aa1d52b339cf25c329ca/packages/opencode/src/skill/index.ts) | Complete skill-directory selections: WinApp 10, WinUI 16, or unique union 18; not its JavaScript/TypeScript plugin mechanism. |

Essential task rules belong in skills when profiles are unavailable. Codex's
[inspected agent loader](https://github.com/openai/codex/blob/b0659c53865dd48b0cd69c454368cea3980017cc/codex-rs/agent-roles/src/loader.rs)
uses configuration layers; this is not a claim that Codex lacks custom agents.

OpenClaw's
[inspected publisher](https://github.com/openclaw/openclaw/blob/81e64210cf664c8133d036d84d87c375455eb7a2/src/skills/loading/plugin-skills.ts)
keeps the first duplicate directory basename. Validate product-qualified
directory/frontmatter names and internal references. OpenCode needs shared-file
ownership tracking so updating/removing one selection preserves the other's
files and never silently overwrites unrelated user skills.

These routes have documentation/source support, not completed live compatibility
coverage. Establish minimum supported host versions through clean-install and
update tests; naming and deduplication cannot be assumed identical across hosts.

## Coexistence and validation

Both plugins may expose **26 installed entries for 18 unique workflows** because
eight shared sources appear twice. Accept duplicated installation as a hypothesis
to test, not duplicated authoring or a promise of harmless context overhead.

An isolated-home Copilot **1.0.84-5** inert-fixture test on **2026-09-17** found one
shared entry with either plugin alone and two enabled, plugin-qualified copies
in both registration orders; unique controls remained visible. This proves
discovery only, not invocation, routing, body loading, or unchanged context cost.

Test single/both installs, both orders, matched versions and one-release version
skew, explicit invocation and automatic routing. Verify loaded bodies, task
outcomes, and absence of shadowed skills or repeated state-changing actions.
Compare identical trimmed content with one/both plugins enabled, measuring
metadata and actual loaded context rather than counting entries alone.

Lifecycle coverage includes upgrade, disable/remove, reinstall, caches, and
pinned/channel installs. Representative tasks cover Electron notifications,
clone/restore, existing-MSIX signing, manifest/alias edits, API/sample lookup,
C# and C++ WinUI builds, UI actions versus feature tests, WPF integration versus
migration, MAUI, Sandbox, and mixed-framework repositories.
Model-driven evaluations still need an agreed scope, budget, and context-cost
tolerance; none have established the proposed design's runtime behavior.

## Versioning and publishing

Use one **plugin-pair version**, independent of CLI versions, with separate
CLI/NuGet dependency requirements.

| Workflow concept | Gate and output |
| --- | --- |
| PR validation | Unprivileged content/package checks and downloadable previews; no publication credentials. |
| Trusted staging | Build from a selected trusted source revision/version; stage both packages, host outputs, dependencies, provenance, and checksums. |
| Approved promotion | Approve a candidate summary, then promote the exact frozen candidate; never rebuild newer `main`. |

The summary records changed skills/source PRs, host results, shared-content
checks, and blockers. Stamp final production versions/metadata **before hashing
and approval**; an earlier staging preview need not have identical bytes.
Keep workflows on source `main`, distribution files on channel branches.

A CLI release may prepare a candidate, but neither automatically nor necessarily
publishes plugins. New prerequisites require a configurable few-day hold
**and actual clean-install availability checks** on advertised WinGet/npm/NuGet
channels. A timer alone is insufficient; skill-only fixes can ship independently.

Production creates an immutable `plugins-vX.Y.Z` tag and GitHub Release containing
both ZIPs, needed host-specific packages, hashes, provenance, dependency
requirements, and plugin release notes. Serialize promotions. Retries reuse
matching existing content or fail on mismatches; never overwrite tags/assets.

## Protect CLI releases first

Plugin tags alone do not isolate repository-wide release consumers.
Before publishing any plugin GitHub Release:

| Surface | Required protection |
| --- | --- |
| Plugin publisher | Set `make_latest: false`; preserve CLI `v*` tags, assets, and stable latest-download behavior. |
| [Release pipeline](../.pipelines/release.yml) | Replace its first-nondraft selection with CLI-tag/required-asset checks and pagination; preserve its intentional CLI-prerelease rehearsal policy. |
| [Update notifications](../src/winapp-CLI/WinApp.Cli/Services/UpdateNotificationService.cs) | Validate CLI tag/assets before caching latest-release data; retain stable-update policy. |
| README/docs and CMake downloads | Audit latest-release paths in samples/test apps against mixed plugin/CLI releases. |

Test newer plugin releases, CLI prereleases, drafts, missing assets, pagination,
and no eligible CLI release. Plugin publication is blocked until these
protections are in place.

## Alternatives

| Alternative | Why not the proposed baseline |
| --- | --- |
| Merge products or require a core plugin | Loses independent audiences or adds installation/dependency coordination. |
| Share snippets but keep separate repositories/publishing | Leaves duplicated workflow ownership and release handoffs. |
| Commit generated packages on `main` | Adds review noise and a second editable representation of source. |

## Rollout and migration

| Phase | Completion gate |
| --- | --- |
| Compatibility spike | Host routes, layouts, collision adapters, and supported version floors are viable. |
| Release isolation | Mixed-release fixtures pass before plugin publication. |
| Mechanical source/build move | One source builds both standalone products; licenses/ownership preserved, no generated source-PR output. |
| Content refactor | Exact membership and task coverage retained; CLI/NuGet replacements and entrypoint transitions verified. |
| Staging/lifecycle evaluation | Approved candidate, channel catalogs, tag, release, and assets agree; recovery and coexistence pass. |
| Production cutover | Own/third-party listings and user migration paths work; old publishing automation stops. |

Separate mechanical moves from behavior changes. Test marketplace registrations
separately from root/direct Git installs, pins, cached installs, and cloned skill
directories. A README edit or root-manifest alias cannot redirect an existing
Git URL to missing files. Document each supported transition explicitly.

Before adding compatibility shims, identify the supported published version,
public contract/persisted data, and real external consumer that requires them,
per [repository guidance](../AGENTS.md#compatibility-boundary). Do not preserve
unreleased behavior through blanket aliases, except a publicly committed preview
contract.

Preserve old `win-dev-skills` tags and a migration landing/catalog as needed.
Stop its old promotion/tag/backmerge workflows only after complete cutover;
archival or deletion requires a separate decision. Fix authored source and roll
back through a new patch containing known-good content, not rewritten history.

## Review questions

- Are the two product boundaries and the 8/2/8 workflow split appropriate?
- Which host versions/surfaces must pass before cutover?
- What dependency propagation hold and channel checks are sufficient?
- What evaluation budget and measured context overhead are acceptable?
- Who owns remaining analyzer/NuGet and `winmd-cli` source, consumers, and parity decisions?

Resolve these at their corresponding implementation/release gates. Success means
both branded plugins are maintained and released here, shared workflows have one
source, supported migrations work, and CLI releases remain correct.
