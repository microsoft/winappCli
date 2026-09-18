# Design specs

Use this folder to propose changes that benefit from design review before
implementation. Specs explain the problem, intended behavior, tradeoffs, and
how to tell whether the change works. They are not product usage instructions.

## Index

| Spec | Status | Summary |
| --- | --- | --- |
| [Unified agent plugin authoring and publishing](unified-agent-plugins.md) | Proposed | Maintain and publish the standalone WinApp and WinUI plugins from one repository. |

## Adding a spec

Create a Markdown file with a descriptive kebab-case name, such as
`unified-agent-plugins.md`. Include a title, status, date, and short summary.
Link it in the index above and open a PR for discussion.

Use sections that help reviewers make a decision: problem, goals, proposal,
alternatives, rollout, validation, and open questions where meaningful.
Keep examples concrete, distinguish current behavior from proposed behavior,
and identify decisions that must be resolved before implementation or release.
There is no required template or minimum length.

## Status

These labels keep the index and each document understandable:

| Status | Meaning |
| --- | --- |
| Proposed | Open for discussion; not approved for implementation. |
| Accepted | Maintainers explicitly accepted the design; implementation may still be pending. |
| Implemented | The design has shipped; link to implementation and current user documentation. |
| Superseded | A newer design replaces this one; link to its replacement. |

Merging a **Proposed** spec records the design for review. It does not authorize
implementation unless maintainers explicitly mark it **Accepted**.
Update the document and index together when its status changes. Keep unresolved
questions visible rather than presenting them as settled implementation details.
