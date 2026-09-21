# Developer skills

Skills in this directory are for **contributors working on the `microsoft/winappcli`
repository itself**. They are read by Copilot CLI (and other agents) to perform
repo-specific developer tasks like reviewing a PR before push.

> **Not the same as `plugins/winapp/skills/`.** That directory contains the
> *shipped* `winapp` plugin skills, which help end users of the `winapp`
> CLI tool. Both sets are hand-written, but skills under `.github/skills/`
> are repository-specific and are not shipped to end users.

## Available skills

| Skill | Purpose |
|-------|---------|
| [`pr-review/`](pr-review/SKILL.md) | Multi-dimensional review of a PR / feature branch diff (security, correctness, CLI UX, alternative solutions, tests, docs/samples, packaging, multi-model cross-check). Reports findings to stdout; does not apply fixes. |
| [`code-quality/`](code-quality/SKILL.md) | Statically checks your changed C# against the 69-rule CodeQL `csharp-code-quality` set that `github-code-quality[bot]` runs on the PR, so you can clear likely findings before pushing. Reads the diff only — no CodeQL download or build. Approximate (flags syntactic rules reliably, flow/type rules less so); reports to stdout; does not apply fixes unless asked. |
| [`spec-review/`](spec-review/SKILL.md) | Pre-code, decision-oriented review of a spec or proposed feature against the real codebase (necessity & scope, approach & alternatives, feasibility vs reality, risks/unknowns/edge cases, DX & user impact, multi-model cross-check). Reports a proceed / proceed-with-changes / reconsider recommendation to stdout; does not change code or the spec. |

## Conventions

- Each skill is a directory containing a `SKILL.md` (the entry point the
  orchestrating agent reads) and any supporting prompt fragments.
- Skills do not run scripts. The orchestrating agent uses its own tools
  (`task`, `grep`, `view`, `powershell` for git, etc.) following the
  instructions in `SKILL.md`.
- Prompt fragments meant to be passed verbatim to sub-agents live under a
  `dimensions/` (or similarly named) subfolder.
- Output goes to stdout unless the user explicitly asks for a file.
