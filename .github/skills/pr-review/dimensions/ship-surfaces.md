# Ship surfaces (docs, samples, packaging)

You own one question for the `microsoft/winappcli` repo: **which shipped surfaces
does this observable change affect, and were those surfaces updated?**

Apply `_shared-contract.md`. Set `Domain: ship-surfaces`.

This is checklist work against a diff, not judgment work. If the change is
internal and user-invisible, none of it applies — say so and return clean.

## What ships

| Artifact | Source | Note |
|----------|--------|------|
| Native CLI | `src/winapp-CLI/` | Built by `scripts/build-cli.ps1` |
| npm package | `src/winapp-npm/` | Wraps the CLI; `npm run build` rebuilds + copies binaries |
| NuGet package | `src/winapp-NuGet/` | MSBuild targets (manifest auto-detect, etc.) |
| Copilot + Claude plugin | `plugins/winapp/` | Agent Plugins 1.0 package: shared `skills/` + `com.github.copilot/agents/winapp.agent.md` |

## Generated and hand-authored surfaces

- The CLI schema is generated into ignored `artifacts/docs/cli-schema.json` by
  `scripts/build-cli.ps1`. There is no tracked snapshot to update.
- `docs/npm-usage.md` is a hand-authored task guide; npm tests type-check its examples.
- `plugins/winapp/skills/winapp-*/SKILL.md` are hand-authored shipped files.
  Update them directly when a workflow or command changes.
- `src/winapp-npm/src/winapp-commands.ts` regenerates via
  `npm run generate-commands`.
- Copilot and Claude consume the same content from `plugins/winapp/`;
  only their manifests are host-specific. `plugins/winapp/plugin.json` is the
  portable Agent Plugins 1.0 manifest and its schema is **closed** — flag any PR
  that adds a top-level field outside `$schema`, `name`, `version`, `description`,
  `author`, `homepage`, `repository`, `license`, `keywords`, `extensions`.

If `Commands/` changed, verify that live schema extraction and npm generation
cover the change; do not require generated files in the diff. Review hand-authored skills only when the command changes a
documented workflow, example, or troubleshooting path. Do not run the scripts
yourself.

## Match the change to affected surfaces

- CLI syntax or help changes appear in the live schema and generated npm wrappers;
  update `docs/usage.md` when it is the canonical hand-authored reference.
- The relevant shipped skill in `plugins/winapp/skills/winapp-<area>/SKILL.md`
  when its workflow, examples, or troubleshooting changed.
- Update `plugins/winapp/com.github.copilot/agents/winapp.agent.md` when its
  decision tree, command reference, or critical rules are affected.
- Forwarding in `src/winapp-npm/cli.js` for a new **top-level** command.
- Hand-written lists that are *not* generated: `docs/usage.md`'s `### ui`
  Commands list, and `winapp.agent.md`'s "Key subcommands". A new `winapp ui`
  subcommand also needs a `### <name>` section in `docs/ui-automation.md`.
- A new top-level command that warrants its own shipped skill needs a new
  `plugins/winapp/skills/winapp-<area>/SKILL.md` and plugin installation smoke test.

Internal refactors need none of these. User documentation explains what to type,
what happens, and how to recover from failure; it never narrates implementation
details, review history, or review-round identifiers. Keep each fact on one
canonical surface and link to it elsewhere.

## A new sample needs

A README, a guide in `docs/guides/`, an entry in the top-level `README.md`
framework list, and `test.Tests.ps1` + a matrix entry (also a correctness-and-tests
finding — do not double-report).

## Packaging specifics

- **`version.json`** drives native CLI and NuGet versioning. A version bumped on
  one artifact but not the sibling that ships with it is `high`.
- **Breaking changes** — apply the shared compatibility gate first. A published
  command/default/output contract with a real npm or NuGet consumer may be
  `high`; behavior introduced only on this branch should be replaced cleanly.
- `WinAppManifestPath` auto-detect in the NuGet targets must stay consistent with
  the CLI's `ManifestHelper`.
- New NuGet `.csproj` needs `<Copyright>© Microsoft Corporation. All rights
  reserved.</Copyright>`.
- MSBuild globs are `**\*` / `**\*.*`, never bare `**`.
- Release runs `.pipelines/release.yml` on `rel/v*` with ESRP signing — changes
  to signing flow, output paths, or artifact names need pipeline review.
- Release builds treat warnings as errors with `EnforceCodeStyleInBuild=true`;
  flag new `<NoWarn>` that suppresses something real.
