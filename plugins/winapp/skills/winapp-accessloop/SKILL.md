---
name: winapp-accessloop
description: Audit an explicitly selected Windows app and its standard tab pages with winapp ui audit, or run configured accessibility loops, replay routes, and verify supported XAML Name repairs with winapp ui accessibility. Use for real accessibility evidence and bounded repairs, not arbitrary-control exploration, arbitrary-source fixes, or WCAG certification.
---

## Audit an explicitly selected running window

Run these examples from the extracted local bundle directory with
`.\winapp.exe`. In an ordinary installation, use the resolved `winapp` command
that provides `ui audit`. Keep the bundle together; scanning needs no
separate .NET installation or AI service.

```powershell
.\winapp.exe ui list-windows --json
.\winapp.exe ui audit -w 123456 --output .\tabs-001 --json
```

Replace `123456` throughout these examples with the positive decimal HWND of
the exact running window the user selected. Confirm ambiguous matches and
permission to collect its evidence before scanning. Never silently inspect
the bundled demo instead of the requested application.

`-a/--app` also accepts a process name, window title or PID. An exact
`-w/--window` takes precedence over `--app`.

**The default audit changes tab selection.** It automatically discovers and
visits supported standard UIA Tab/TabItem controls, including WinUI 3 TabView.
No navigation flags, agent-generated route, source, application AutomationIds
or manual page list are needed. Selecting tabs can trigger app side effects.
It never builds, restarts or closes the app. Use `--current-page` when the user
requests a read-only check or does not want navigation.

Read `tabs-001\report.html` and the aggregate `scan.json`, then the individual
`pages\page-001\scan.json` and subsequent page reports for findings, raw evidence
and capture limits. If there are no standard tab groups, the current page is
captured with navigation marked `not-applicable`; unsupported groups and
incomplete discovery remain partial. This does not claim other navigation
controls were explored.

The default page budget is 20; `--max-pages` allows 1-100. Do not bypass
skipped/unsupported entries with Invoke, clicks or scrolling. Check navigation,
per-page capture and restoration statuses separately. Completed navigation with
partial captures is not a completed audit. Every run needs a **new output
directory**; preserve failed or partial evidence. Do not assume interrupted
runs restored the original tab or undid app side effects.

See [automatic standard-tab scans](https://github.com/microsoft/WinAppCli/blob/main/samples/accessloop/local-bundle.md#automatically-scan-standard-tab-pages)
for nested/independent group coverage, skipped controls and output semantics.

## Audit only the current page

```powershell
.\winapp.exe ui audit -w 123456 --current-page --output .\scan-001 --json
```

This is read-only unless a keyboard probe is separately approved. Read
`scan-001\report.html` and `scan-001\scan.json`. Use `--no-screenshot` if images
are not authorized; it does not waive required verification.
`--observe-seconds 5` adds passive observation.

Only with separate approval for real keyboard input in a safe test state, add
`--tab-steps 8 --accept-input --observe-seconds 12` to `--current-page`. This
probes both directions; it is not page enumeration, proof of logical order, a
keyboard-trap verdict or actual screen-reader speech. `--max-pages` is not
valid with `--current-page`.

## Navigate other controls through a reviewed profile

Supply real, app-specific configuration files in place of the example paths
below. Review their exact target, permitted pages, expected headings and
interactions with the user. Choose the workflow they authorize:

```powershell
.\winapp.exe ui accessibility survey .\app.survey.json --window 123456 `
    --accept-navigation --output .\survey-001

.\winapp.exe ui accessibility explore .\app.explore.json --window 123456 `
    --accept-interactions --output .\explore-001
```

`survey` visits only declared navigation groups and internal pages, without
source, builds, AI or repairs. `explore` also visits declared finite scroll
positions, dropdown disclosures and known dialogs with safe dismissal. Neither
permission allows selecting different options, changing feature settings,
opening external links, elevation, updates or restarting the user's app.
These attached-window runs never own its lifecycle.

For an explicitly approved source demo, `explore --launch-owned` replaces
`--window` and authorizes building, launching and closing that profile's
project. Do not use it as a fallback for a failed attachment.

`survey --stop-after N` or Ctrl+C pauses collection. Resume only after the user
confirms settings and test data have not changed:

```powershell
.\winapp.exe ui accessibility survey --resume .\survey-001 `
    --accept-navigation --confirm-unchanged
```

Resume checks the original process/window, app, tools, environment and evidence;
it cannot prove the user's data is unchanged. A mismatch needs a fresh run.
`--retry-page <ID>` on resume preserves the previous attempt. Do not supply a
new target or output on resume, or run concurrent navigators. Interrupted
exploration needs a fresh output directory.

See the [survey profile guide](https://github.com/microsoft/WinAppCli/blob/main/samples/accessloop/survey-guide.md)
and [exploration profile](https://github.com/microsoft/WinAppCli/blob/main/samples/accessloop/state-demo.json)
for configuration details. Without a suitable reviewed profile, use the
standard-tab scan only for its supported controls or offer the passive scan.
Other profile-free navigation and a general observed-AI-plan import/verify
interface are not available in this bundle.

## Run a configured source-owned loop

Use the included example **only when the user requests that demo**. Read
`examples\loop-demo.json` and `examples\StateDemo` first. Obtain approval to
build trusted code, launch owned processes, and perform the exact route's
keyboard input and in-memory test changes. Building StateDemo requires its
normal prerequisites, including the .NET 10 SDK.

```powershell
.\winapp.exe ui accessibility loop .\examples\loop-demo.json `
    --accept-actions --output .\loop-001
```

This copies the source into a disposable workspace, executes the preset route,
then reproduces it in a fresh owned process. It does not edit the original
example or call AI. A configured route is not autonomous discovery.

To reproduce an archived source round:

```powershell
.\winapp.exe ui accessibility loop --replay .\loop-001 --round baseline `
    --accept-actions --output .\replay-001
```

Keep the complete original run, source archives and recorded tools intact.
`baseline` is the default; other round IDs must come from the report. Replay
resolves fresh owned processes and controls, not old HWNDs. It inherits capture
policy and cannot request repairs. Changed evidence or tools require review,
not edited hashes or fabricated execution records.

### Replay a single-window action recording

This is the separate, unchanged low-level workflow, not an accessibility plan:

```powershell
.\winapp.exe ui record-actions -w 123456 --output .\actions-001.json
.\winapp.exe ui replay .\actions-001.json -w 123456 --accept-recording
```

Obtain recording approval first. Before replay, review the complete draft,
locators, values and required parameters; supply only approved values with
`--parameter`. Separately approve replay and any restoration of the starting
test state. Never force an incomplete draft to replay or reset user data.
Successful input delivery is not an accessibility pass. See
[action recording and replay](https://github.com/microsoft/WinAppCli/blob/main/docs/ui-automation.md#record-actions)
for privacy, parameter and completeness requirements.

## Verify supported source repairs

For a project other than the bundled loop, use reviewed configurations pointing
to an authorized working copy. `journey`, `check` and `repair` build and launch
that configured source; they do **not** create the loop's isolated copy.
Keep evidence outside the source project. Choose a baseline:

```powershell
.\winapp.exe ui accessibility journey .\app.journey.json `
    --accept-navigation --output .\journey-001

.\winapp.exe ui accessibility check .\app.suite.json `
    --accept-actions --output .\check-001
```

`journey` visits declared pages and exercises their real keyboard checks.
`check` runs every configured scenario; it also accepts a single scenario file.
Both require permission for those actions, not just passive collection.
Use approved project-local or session-scoped restore configuration if needed;
never change global feeds or machine settings to make a run succeed.

Source edits and AI calls need **separate explicit approval**:

- `loop --apply` and `journey --apply` can start separate Copilot CLI processes
  and send bounded findings/source snippets unless reviewed `--proposals` are
  supplied. Do not imply the already-running outer agent performs every step.
- `repair` only proposes without `--apply`, but can still call Copilot unless
  given a reviewed `--proposal` file. Proposal-only runs still execute scenarios.
- `journey` uses the declared order by default. Its `--plan` accepts only a
  reviewed journey page-order file; `--copilot-plan` separately calls Copilot
  for bounded ordering of approved candidates. Neither is general AI discovery.
  Do not use either option as a substitute for the required configuration.

With approved edits and reviewed proposals, no Copilot repair call is needed:

```powershell
.\winapp.exe ui accessibility loop .\examples\loop-demo.json `
    --accept-actions --apply --proposals .\reviewed-proposals --output .\loop-repair-001

.\winapp.exe ui accessibility repair .\app.suite.json --accept-actions `
    --finding $findingId --proposal .\reviewed-proposal.json --apply --output .\repair-001
```

Choose only the requested workflow. Set `$findingId` to an actual finding from
the baseline report; selection is required when several findings exist.
Omit `--apply` from `repair` to review the proposal without editing. A journey
can likewise use `--apply --proposals` with its approved navigation.

Proposal files must match the actual source bytes, relative path, target
AutomationId and reviewed expected accessible name. The proposal fields are
`relativePath`, `sourceSha256`, `automationId`, `accessibleName`, `explanation`;
proposal folders use `<TargetAutomationId>.json`. Re-review after any source
change, including a previous repair to the same file. Never refresh a hash just
to bypass a stale-proposal rejection.

Supported automatic edits are constrained missing-Name repairs to uniquely
mapped static XAML Buttons, not arbitrary framework or source repairs.
Ambiguous mappings, bindings, templates and unsupported shapes need manual
work. Preserve expected names, assertions and scan scope, then require the
unchanged route or every scenario to run after the patch. A selected fix does
not mean unrelated findings or the whole suite passed.

## Attach an evidence-based assessment

```powershell
.\winapp.exe ui accessibility assessment-template .\scan-001\scan.json `
    --output .\assessment-001.json --author 'Reviewer' --source human
```

Fill the template with actual observations before attaching it. For AI-authored
judgments, use `--source ai --model <model>` and accurate author attribution;
template creation itself calls no AI and infers no judgment.

```powershell
.\winapp.exe ui accessibility assess .\scan-001\scan.json `
    --assessment .\assessment-001.json --output .\assessment-report-001
```

This produces a supplementary report without changing the original automatic
scan or converting a human/model opinion into an automatic rule result.

## Stop safely and report what actually happened

- Real input needs an unlocked interactive desktop. Stop on focus loss,
  unexpected windows, ambiguous targets or failed scope/visibility guards.
  Never bypass guards, replace Tab/Enter checks with Invoke, close user-owned
  apps, or reset their state. Report incomplete navigation restoration.
- Preserve the whole output directory, raw captures, action evidence, source
  versions and tool identity. Do not rewrite reports, acceptance inputs, hashes
  or withheld fields. Static screenshots alone do not prove keyboard behavior.
- Start with `survey-report.html` / `survey-state.json`,
  `explore-report.html` / `explore-report.json`, `loop-report.html` /
  `loop-report.json`, `journey-report.html` / `journey-report.json`, or
  `report.html` / `report.json` for `check` and `repair`.
- Report actual visited/unvisited states, distinct findings, accepted fixes,
  unresolved work, failures and rollback. Normal `capture.scope` observations,
  missing identifiers and unknown coverage are not rule failures. Never
  promise a finding-count progression or claim all WCAG checks were covered.
- For loops and journeys, read reproduction (where present), verification and
  capture statuses separately. Required checks can pass with privacy-only
  partial capture: retain accepted fixes, but overall `partial` and exit 2
  remain partial. Missing required pages/controls, truncation, capture errors
  or root mismatches block verification; do not turn them into a pass.
- On concurrent source changes or refused rollback, stop editing and compare
  the recorded source and patch with current work. Never overwrite concurrent
  edits. A rejected patch's latest capture may precede rollback; it is not
  evidence of the restored source. Use a fresh baseline after recovery.
