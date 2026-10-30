---
name: winapp-ask
description: Research Windows app features before implementing them with winapp ask. Search official Microsoft Learn docs and WinUI samples together for API prerequisites, desktop integration, packaging concepts, or UI guidance. Use when building Windows apps and samples alone may not explain the feature.
---

If you cannot run `winapp`, give the user the exact command.

## Search both sources

```powershell
winapp ask "how do I add a jump list?" --json
```

Every question is sent to Microsoft Learn. Do not send secrets or private project
text. Use `winapp find-ui` instead when the question must not leave the machine.
See the [ask reference](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#ask)
for privacy, sources, limits, and exit codes.

Read `matches` for WinUI sample choices and `docs` for official documentation
links and excerpts. The command returns search results, not a generated answer.
Use the returned evidence to answer the question; do not claim a sample match
alone verifies API support in the user's project.

## Research before writing code

Identify the project's framework, SDK version, and whether it has package
identity before choosing an implementation. Include the relevant public context
in a focused query, not private project names, paths, or source code:

```powershell
winapp ask "WinUI 3 custom title bar drag regions" --json
```

For several independent features, plan one focused query per feature before
coding. Run the separate commands in parallel when your tools support it;
otherwise run them sequentially. For example, title bar and file picker research
can start together. Each invocation accepts one question; do not combine
unrelated questions into one broad query or invent batch flags.

Keep follow-up searches dependent on earlier evidence sequential. If the first
page identifies an API or prerequisite you did not know about, read it before
choosing the next query. Check each command's source errors separately.

Read both sources before coding. A WinForms or Win32 result is not a drop-in
WinUI implementation; choose documentation that applies to the existing
framework and SDK rather than changing the project to fit a search hit.

Excerpts help select a page, but may omit setup or lifecycle requirements. Open
the relevant returned Learn link with an available documentation or web tool
when you need those details. If you cannot read the page, state that limit;
do not claim the full documentation confirms the implementation.

Fetch a selected sample's full code with its returned scenario ID. For example,
if the matches include `gallery-tabview-1`:

```powershell
winapp find-ui --id gallery-tabview-1 --json
```

Use `winapp-find-api` when you need to verify exact API names or members against
the project's SDK. Adapt the evidence to the existing app, then build and check
the requested behavior. A successful build alone does not prove that a picker
opens, a notification arrives, or another Windows feature works at runtime.

If the results do not address the feature, try a more focused public query
that names the framework or API. Do not repeat an unchanged query or substitute
an unrelated result for missing evidence.

## Handle partial results

Check `docsError` and `samplesError` even on exit code `0`. A source can fail while
the other still returns useful results. An empty array without its error field
means no matches, not a network failure. If docs are unavailable, report that
limit rather than presenting sample results as documentation.

For exact options, run `winapp ask --help`.
