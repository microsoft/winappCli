# Demonstrate accessibility across three tabs and their UI states

## Run the complete preset loop

```powershell
dotnet build .\samples\accessloop\AccessLoop\AccessLoop.csproj -c Release
$accessloop = (Resolve-Path .\samples\accessloop\AccessLoop\bin\Release\net10.0\AccessLoop.exe).Path
$winapp = (Resolve-Path .\artifacts\cli\win-x64\winapp.exe).Path

& $accessloop loop .\samples\accessloop\loop-demo.json `
    --accept-actions --winapp $winapp --output .\state-demo-loop
Invoke-Item .\state-demo-loop\loop-report.html
```

This copies the demo into `state-demo-loop\workspace`, builds and launches that
owned copy, runs the preset route, then starts a fresh owned process and
reproduces the same checks. It does not edit the checked-in fixture or call an
AI service. `--accept-actions` approves building trusted project code and the
route's real keyboard input, navigation, scrolling and in-memory test changes.
Keep the desktop unlocked and do not switch focus or type during a run.

The route uses actual Tab/Shift+Tab and activation keys, observes the preview
switch's on/off states, opens the dropdown without changing its selection,
captures declared scroll positions and inspects an in-window dialog. Navigation
preparation may focus the selected tab header; the subsequent keyboard steps
must reach their own targets without programmatic focus correction.
Each key step uses `winapp ui send-keys --via send-input` without `--target`,
after checking the actual focused control. The loop records that command and
its observed result. `loop --replay` re-executes this frozen route; it is separate
from the general-purpose `winapp ui record-actions` / `ui replay` workflow.

The fixture has two intentional missing accessible names: the refresh-preview
icon and the save-layout icon at the bottom of the long page. It changes no
files or user settings. Read the actual findings rather than assuming those
are the only findings on every machine.

To additionally authorize the constrained Copilot proposals and actual source
edits in the owned copy, choose another new output directory:

```powershell
& $accessloop loop .\samples\accessloop\loop-demo.json `
    --accept-actions --apply --winapp $winapp --output .\state-demo-repair
Invoke-Item .\state-demo-repair\loop-report.html
```

`--apply` sends each bounded source snippet and its finding to the tool-disabled
Copilot advisor. The expected accessible names are reviewed inputs in the preset;
the advisor does not invent the acceptance criteria. Each accepted Name-only
patch is followed by a rebuild and the unchanged route. An unverified patch is
rolled back only when its source bytes still match the applied patch. Concurrent
source edits are never overwritten. The original fixture remains unchanged.

### Replay a recorded source version

```powershell
& $accessloop loop --replay .\state-demo-repair --round baseline `
    --accept-actions --winapp $winapp --output .\state-demo-replay
```

Replay copies the selected round's archived sources into a new workspace and
executes the same reviewed route and checks. It resolves a new owned process and
controls; it does not reuse old PIDs, HWNDs or screen coordinates. Omit `--round`
for the baseline, or use an existing ID shown in the report, such as `after-01`.
Keep the original run directory and the recorded native/runner binaries intact;
changed evidence, sources, tools or acceptance inputs are rejected.

### Read loop results

Start with `loop-report.html`. A completed replay is not an accessibility pass:
it can correctly reproduce the original defects. Read `reproductionStatus`,
`verificationStatus` and `captureStatus` separately in `loop-report.json`.
Target checks can pass while optional capture stays `partial`; that retains
overall partial status and exit code 2. Other exit codes are 0 for complete
capture and passing checks, and 1 for complete capture with failed checks.
Blocked or interrupted work exits 2 and keeps completed evidence.

Each round's `actions.json` records actual executed commands, observed state and
explicit checks. It is not merely a proposed action list. Each checkpoint keeps
its own static audit and screenshot. These static reports may say keyboard
checks were not requested: the loop's separate action evidence supplies its
declared keyboard checks, not a claim that all possible keyboard paths were
tested.

Repeated findings are grouped only for unambiguous, explicitly mapped source
targets; their individual state links remain available. Unmapped findings are
not merged by name or transient runtime ID. The report retains capture limits,
unexecuted work and unsupported checks, including actual screen-reader output.
If a scope guard stops the run, review its reason and start a fresh output;
do not change expected results or evidence hashes to force it to pass.

## Run exploration without repair or state-value changes

Run the small owned WinUI 3 app live, then finish with a previously captured
PowerToys report. The live segment explores declared states; it does not click
every control or change selected settings.

From the repository root, build AccessLoop and use a native winapp build that
includes `ui expand-collapse` and `ui scroll --vertical-percent`:

```powershell
dotnet build .\samples\accessloop\AccessLoop\AccessLoop.csproj -c Release
$accessloop = (Resolve-Path .\samples\accessloop\AccessLoop\bin\Release\net10.0\AccessLoop.exe).Path
$winapp = (Resolve-Path .\artifacts\cli\win-x64\winapp.exe).Path

& $accessloop explore .\samples\accessloop\state-demo.json `
    --launch-owned --accept-interactions --winapp $winapp `
    --output .\state-demo-run
Invoke-Item .\state-demo-run\explore-report.html
```

`--launch-owned` explicitly approves building and starting the profile's local
source project. The runner owns that newly created process and closes it when
the invocation ends. It will not claim or close an already-running singleton.
Use a new output directory for each run. Prepare builds and dependencies before
presenting; use your approved NuGet configuration on restricted networks.

## The three-page route

| Page | What the audience sees |
|---|---|
| Basics | A preview switch, an intentional unnamed icon button, and a dropdown that is expanded, audited and collapsed without selecting a different option. `explore` does not toggle the switch. |
| Long content | A bounded vertical content area, overlapping viewport captures and a bottom control that was not visible initially. |
| Dialogs | A named information dialog, its own visible controls, an explicit safe dismissal and the recorded return state. |

The fixture uses in-memory state only. Its intentional defects belong to the
app's accessible UI, not to a fake scanner response. The older `Demo` and
`JourneyDemo` retain their original source-repair scenarios.

The sample keeps its dropdown list inside the original application window.
This explicitly supported display mode is not proof of coverage for separate
native popup windows in other apps; unverified popup scopes remain excluded.

The command runs WinAppCLI interactions and independent per-state audits. No
AI planner, source patch or online service is needed. Three pages can produce
more than three captures: a scrolled viewport or an expanded control is another
state, not another page.

## Permissions and boundaries

`--accept-interactions` approves only the profile's navigation buttons,
scroll containers, dropdown expansion/collapse and dialog dismissal actions.
The runner does not select dropdown values, toggle feature settings, press
arbitrary confirmation buttons or explore undeclared links.

Keep the interactive desktop unlocked and the window visible. Do not resize,
move focus to another application or change the display environment during a
run. A detected boundary or environment interruption stops later actions; the
report retains completed captures and says what could not be completed.
Window events do not prove that an application caused a foreground change.

Only explicitly identified states are eligible:

- Scrolling requires the declared container's own scroll pattern and readable
  position/view size. Position limits, unstable extent, no progress and an
  unconfirmed bottom remain incomplete coverage.
- Dropdowns require readable selected-item identifiers and expected option
  identifiers. Expanding a list does not approve selecting its options.
- Dialogs require the declared opener, marker and safe dismiss control.
  This first executor supports in-window dropdowns and dialogs only. A
  separate native popup stops further actions; it cannot silently widen the
  original window's scan or acquire a successful coverage count.
- Restoration is limited to verified navigation, disclosure and scroll state.
  It is not a rollback of app data or arbitrary action side effects.

This first exploration workflow saves progress for inspection, not an automatic
resume contract. After an interruption, inspect the reason and start a fresh
run/output after addressing it. The existing installed-app `survey` command
retains its separate pause/resume and individual-page retry workflow.

## Optional boundary-stop demonstration

To deliberately exercise the guard, add `--probe-scope-guard` to an owned run:

```powershell
& $accessloop explore .\samples\accessloop\state-demo.json `
    --launch-owned --accept-interactions --probe-scope-guard `
    --winapp $winapp --output .\state-demo-guard-run
```

The fixture's declared probe opens an auxiliary test window, not a browser or
another user's app. That window is deliberately outside the allowed audit
states. The report records whether a real boundary was observed and whether
later actions were stopped. This is a safety observation, not an accessibility
pass. The probe is not allowed when attaching to an existing process/window.

## Read the report

Open `explore-report.html`. Start with page/state coverage and actual findings,
then follow a state's page report for its control images and rule evidence.
Before/after witnesses, scope events and tool identities remain available in
technical details. A missing or changed evidence file cannot supply a trusted
successful count.

Every captured state has a separate directory containing `scan.json`,
`report.html` and its raw audit evidence. `profile.json` preserves the approved
input; `explore-request.json` records the original target and tools;
`explore-progress.json` is the mutable checkpoint; `explore-report.json` is the
final result. Keep the directory together and inspect labels/images before
sharing it.

State coverage, capture completeness and accessibility findings are different
results. `partial` data stays partial. Reaching an observed scroll endpoint
does not prove that hidden sections, every option combination, all keyboard
workflows or actual screen-reader output were tested. Repeated findings across
states are occurrences, not a certified count of distinct root causes.

For a short presentation, show the base state beside its expanded or scrolled
state: the lower button is not initially visible, dropdown options appear in
the open list, and the dialog has its own controls.
Use the measured duration in that run's summary, not a promised universal
runtime.

Exit codes: **0** means finished with complete captures and no rule failures;
**1** means complete captures with rule failures; **2** means partial or
incomplete execution/capture; **3** means paused. Read the state and restoration
details rather than interpreting zero as WCAG compliance.

## Finish with PowerToys

Open the existing PowerToys `survey-report.html` after the small live run.
Show its capture date and installed-app scope, page reports, image locations,
repeated navigation findings and retained partial coverage. Do not wait for
another full survey on stage or present that saved result as a fresh live scan.
The saved PowerToys survey also does not prove that this new scrolling,
dropdown or dialog exploration was executed on all its pages.

Use the [survey guide](survey-guide.md) for a separately approved installed-app
run and the [journey guide](journey-guide.md) for the existing AI-assisted
source-repair demonstration. Do not mix their ownership or repair permissions.
