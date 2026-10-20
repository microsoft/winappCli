# Audit and repair a three-page Windows demo

After preparing the fresh copy and paths below, run a baseline without source
edits or AI:

```powershell
dotnet run --project $runner -- journey $journey --accept-navigation --winapp $winapp --output (Join-Path $work 'baseline')
Invoke-Item (Join-Path $work 'baseline\journey-report.html')
```

This builds and launches **your copy of JourneyDemo**, discovers its allowed
navigation entries, visits Home, Documents and Export, audits each page, and
performs the declared keyboard exercises. It is not a passive scan of an
existing application. These examples use the repository's sample runner.
The [local WinAppCLI bundle](local-bundle.md) exposes the same workflow through
`winapp ui accessibility journey`; installing a skill alone does not supply
the executable backend.
For an already-open application, use the separate
[single-page scan workflow](README.md#scan-a-page-that-is-already-open).

## Prepare a fresh, source-owned copy

Requirements:

- Windows with an **unlocked interactive desktop**. Keep the demo foregroundable
  and do not type, switch windows or lock the session during the run.
- .NET 10 SDK. Restore supplies the fixture's pinned WinUI/Windows SDK packages
  and the runner's Axe.Windows dependency. No separate Accessibility Insights
  installation is required.
- The **current native build** from this repository, including the audit commands,
  at `artifacts\cli\win-x64\winapp.exe`. Do not substitute the npm wrapper or
  assume an older installed release has this sample's required commands.
- Permission to build the project and use disposable test state. A project
  build executes trusted project code even though `journey.json` does not
  accept arbitrary shell commands.

From the repository root, copy **only JourneyDemo and journey.json**, not the
old `Demo`, runner, or generated build output:

```powershell
$repo = (Get-Location).Path
$runner = Join-Path $repo 'samples\accessloop\AccessLoop\AccessLoop.csproj'
$winapp = (Resolve-Path (Join-Path $repo 'artifacts\cli\win-x64\winapp.exe')).Path
$work = Join-Path $repo 'accessloop-journey-playground'

New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
$demo = Join-Path $work 'JourneyDemo'
New-Item -ItemType Directory -Path $demo -ErrorAction Stop | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'samples\accessloop\JourneyDemo') |
    Where-Object { $_.Name -notin @('bin', 'obj') } |
    Copy-Item -Destination $demo -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'samples\accessloop\journey.json') -Destination $work
$journey = Join-Path $work 'journey.json'
```

Use a new working-directory name if it already exists. Keep the runner path
absolute and inside the repository; only the fixture's copied sources may be
repaired. The checked-in fixture deliberately retains its two missing names.
Every run also needs a **new output directory**, separate from the fixture's
source directory.

### Restore through an approved source when required

On a restricted network, use a user-approved NuGet configuration. Do not change
machine-wide feeds. In a **dedicated PowerShell session**, after the setup above,
scope NuGet's user configuration to a directory in this working copy:

```powershell
$approvedConfig = (Resolve-Path .\approved.nuget.config).Path
$nugetHome = Join-Path $work 'nuget-session'
New-Item -ItemType Directory -Path (Join-Path $nugetHome 'NuGet') -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath $approvedConfig -Destination (Join-Path $nugetHome 'NuGet\NuGet.Config')
$env:APPDATA = $nugetHome
```

`approved.nuget.config` is your reviewed source configuration, not a file
provided by this sample. Use your organization's normal authorization for any
private feed; do not put tokens into the guide, source, proposals or reports.
The session-scoped configuration is inherited by the runner and its child
builds. End the dedicated shell afterward rather than changing shared settings.
If you choose AI below, ensure Copilot authentication is available in that same
session; changing `APPDATA` can change where tools look for user settings.

## Understand exactly what is authorized

| Permission | What it allows |
|---|---|
| `--accept-navigation` | Build and launch the owned project, use declared safe navigation, supply fixed test input, and perform the declared real Tab/Enter exercises. It does **not** authorize source edits. |
| `--apply` | Additionally apply supported, source-hash-bound `AutomationProperties.Name` repairs and rebuild/retest every declared page after each repair. |
| `--copilot-plan` | Ask Copilot for a bounded page order from observed navigation candidates. It does not authorize extra pages or actions. |
| Default proposals with `--apply` | Ask the tool-disabled Copilot advisor for each supported Name proposal. Use this only with authorization to send the bounded source snippet, source hash and finding data to Copilot. |

The baseline at the top uses declared configuration order and calls no AI.
Copilot authentication and AI authorization are needed only when requesting
Copilot planning or using the default AI proposal path. `--copilot <exe>` can
select an explicitly approved Copilot executable; it otherwise resolves
`copilot`.

The navigation root is `MainNavigation`, with `NavHome`, `NavDocuments` and
`NavExport`. The fixture has three distinct pages:

| Page | Ready marker and preserved controls | Declared exercise |
|---|---|---|
| Home | `HomePageRoot`, `HomeHeading` | No business action. |
| Documents | `DocumentsPageRoot`, `DocumentTitleInput`, `SaveDocumentButton`, `SaveStatus` | Enter the fixed title, reach Save with real Tab, then activate it with Enter. |
| Export | `ExportPageRoot`, `ExportTitleInput`, `ExportDocumentButton`, `ExportStatus` | Enter the fixed title, reach Export with real Tab, then activate it with Enter. |

Save and Export are intentionally unnamed icon buttons with adjacent visible
labels. Their pre-reviewed expected names, **Save document** and **Export
document**, are in `journey.json`. The advisor receives those exact names; it
does not independently discover what the controls mean.

Each round launches a fresh owned process at Home. Before each page visit, the
runner selects `NavHome` and then replays the frozen Home-to-target route. Pages
are recreated with `Untitled` and `Not saved yet` / `Not exported yet`.
Save/Export change only in-memory status text; no document is saved or exported.
This reset belongs to the disposable fixture, not to arbitrary user
applications. The runner does not modify an existing PowerToys installation,
its configuration, or other applications.

## Run one approved repair journey

After authorizing both the edits and the AI calls, one invocation performs its
own fresh baseline, requests a plan, repairs supported findings serially, and
verifies each accepted repair against every declared page:

```powershell
dotnet run --project $runner -- journey $journey --accept-navigation --apply --copilot-plan --winapp $winapp --output (Join-Path $work 'repair-run')
Invoke-Item (Join-Path $work 'repair-run\journey-report.html')
```

Copilot's planning and proposal calls have tools disabled. The runner validates
the returned JSON and executes the allowed operations. It does not let a model
change navigation selectors, input data, expected names, acceptance checks or
source hashes to make a result pass.

The intended demonstration has two baseline name findings, then one, then zero
after two accepted repairs. **Read the actual rounds rather than assuming
`2 → 1 → 0`.** Other findings, unreliable required evidence, unavailable keyboard
input, source changes, repair limits or failed verification can stop that sequence.
Unsupported repairs remain reported for manual review.

Use `--no-screenshot` if you do not want image evidence. It does not disable
auditing or keyboard checks, supply missing UIA data, or convert partial
evidence into complete coverage. Add `--json` for a machine-readable final
summary and use the saved `journey-report.json` for detailed results.

### Supply a reviewed navigation plan

Instead of `--copilot-plan`, save a reviewed file such as
`reviewed-plan.json` in the working directory:

```json
{
  "pageOrder": ["home", "documents", "export"],
  "explanation": "Start at Home, inspect document editing, then inspect export."
}
```

Pass `--plan <full-path-to-reviewed-plan.json>`. The runner still discovers and
checks runtime candidates. A plan cannot skip, add or duplicate declared
pages; `--plan` and `--copilot-plan` are alternatives. Supplying a plan alone
does not call AI, but `--apply` still uses AI proposals unless you also supply
reviewed proposals.

### Supply reviewed repair proposals without AI

Create a folder containing reviewed `SaveDocumentButton.json` and
`ExportDocumentButton.json` files for the unchanged working sources. Each file
has exactly five fields; for example:

```json
{
  "relativePath": "Pages\\DocumentsPage.xaml",
  "sourceSha256": "REPLACE_WITH_THE_ACTUAL_64_CHARACTER_SOURCE_SHA256",
  "automationId": "SaveDocumentButton",
  "accessibleName": "Save document",
  "explanation": "Use the reviewed adjacent-label wording for the save action."
}
```

Read the real hash with
`Get-FileHash -Algorithm SHA256 (Join-Path $demo 'Pages\DocumentsPage.xaml')`
and review the source and all five values before using the proposal.
The Export proposal must refer to its own file, hash, target and reviewed
name. These files are proposals, **not prompt code**. A stale hash is a refusal
to edit: review the changed source and create a fresh proposal, rather than
altering acceptance records to force a match.

With reviewed proposals and declared-order navigation, this apply run calls
no AI:

```powershell
dotnet run --project $runner -- journey $journey --accept-navigation --apply --proposals (Join-Path $work 'reviewed-proposals') --winapp $winapp --output (Join-Path $work 'reviewed-repair-run')
```

Use another fresh fixture copy if a previous apply already repaired this one.

## Read the evidence before calling the run successful

Open `journey-report.html` and keep the entire output directory together:

1. **Three separate statuses:** read `status`, `verificationStatus` (required
   defect and functional verification), and `captureStatus` (capture
   completeness, including optional fields). `verificationStatus=passed` with
   `captureStatus=partial` means the required checks passed but capture is still
   partial: overall `status=partial`, **exit 2**, not a compliance-green result.
   Check declared/reached/unvisited pages and the separate required-evidence,
   complete-capture and partial-capture counts. A page with
   `audit.Status=partial` never counts as a complete capture.
2. **Plan:** check whether it was declared-order, reviewed JSON or Copilot.
   The saved plan, navigation candidates, frozen routes and round JSON show
   what was proposed and what was visited. A model's explanation is not
   evidence that an action happened.
3. **Problems and repairs:** follow actual round counts, page occurrences,
   source candidates, diffs, outcomes and retained findings. Normal
   `capture.scope` observations and missing AutomationId testability notes
   are not automatically accessibility issues.
4. **Original versus latest:** each page links to its own `report.html` and
   `scan.json`. Finding links reuse the original report's rule-failure cards
   and available screenshot crops; the journey report does not rewrite or
   duplicate those images. Missing, unsafe or linked/reparse-point evidence
   is shown as unavailable rather than linked outside the output directory.
5. **Failures and sources:** inspect errors, rollback records and original
   hashes, including files with no recorded repair. Original hashes are not
   measurements of the current worktree. If verification was rejected, the
   latest capture may describe the attempted patch before rollback, not the
   restored source.

**Privacy-only partial captures can coexist with verified repairs.** A WinUI
`InputSiteWindowClass` host may not expose an explicit `IsPassword` value, so
its Name/text remains withheld and the snapshot stays partial. Supported Name
repairs can continue only when the required routes, preserved controls, Axe
results and fixed behavior checks remain reliable, with no truncation, capture
errors or root-identity mismatch. Accepted repair transactions still count as
fixed; withholding optional fields alone does not undo them. Other partials
block verification. Original audit statuses and withheld fields are never
rewritten as complete or populated with guesses.

If rollback is refused because the source changed concurrently, stop editing
and compare the current source with `source.original.txt` and `repair.diff`.
Restore only after reviewing those concurrent changes. A forcibly terminated
process cannot promise rollback. After recovery, use a new baseline output
directory; do not overwrite earlier evidence.

An unavailable or locked interactive desktop makes the real keyboard exercise
inconclusive. Run again on an authorized unlocked desktop; do **not** substitute
UIA Invoke for Tab/Enter or weaken an assertion. For restore, navigation or
required-evidence failures, read the saved errors and correct the approved
environment or capture problem before rerunning. A privacy-only capture gap
stays visible even when required verification succeeds. Do not suppress privacy
guards or relax the rule/coverage requirements to obtain a green report.

This workflow covers the declared states, supported rules and fixed exercises,
**not all WCAG criteria or all possible application states**. Missing/withheld
UIA fields, unsupported image-based contrast and other untested capabilities
remain explicit. Inspect names, notifications, screenshots, source snippets
and local paths before sharing any evidence. Opening the static HTML itself
does not connect to an application, Copilot or a credential service.
