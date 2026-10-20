# Local WinAppCLI accessibility bundle

Extract the ZIP to a short local path, such as `C:\work\accessloop`, then open a
terminal in that directory:

```powershell
.\winapp.exe ui audit --help
```

The native CLI remains NativeAOT. The `accessibility` directory contains the
managed accessibility engine, Axe.Windows, and their .NET runtime. Keep that
directory beside `winapp.exe`; do not move only the main executable.
Scanning does not require a separately installed .NET runtime or SDK.

## Check an existing window without an agent

Find the exact window, then use its HWND:

```powershell
.\winapp.exe ui list-windows --json
.\winapp.exe ui audit -w 123456 --output .\tabs-report --json
```

Replace `123456` with the intended window handle. Alternatively, use
`-a/--app` with a process name, window title or PID. Use an exact
`-w/--window` when the app match is ambiguous; a window takes precedence over
`--app`.

By default, `ui audit` discovers and selects supported standard tab pages.
Selecting a tab can trigger app side effects. It never builds, restarts or
closes the app. Use `--current-page` below when navigation is unwanted.
Open `tabs-report\report.html` to read the actual findings and capture limits.
A zero finding count does not establish complete accessibility coverage.

Output directories must be new. If collection is partial or blocked, retain the
report and resolve its stated cause rather than treating missing evidence as a
pass.

## Automatically scan standard tab pages

The default audit needs **no agent, source project, navigation flags or
hand-written page profile**. It discovers
standard UI Automation `Tab` / `TabItem` controls, selects each supported page,
and runs the same real accessibility checks and screenshot capture on each one.
Application-specific `AutomationId` values and tab names are not required.

Open `tabs-report\report.html` for the combined results. `scan.json` is the
aggregate; each `pages\page-001`, `pages\page-002`, and subsequent directory
contains its own `scan.json`, `report.html`, UIA/Axe evidence and screenshot.
Use an individual page's `scan.json` when attaching an assessment.

The default limit is 20 page captures; use `--max-pages 40` to increase it
(maximum 100). Nested groups are visited in their parent page, up to eight
groups deep. Independent groups are visited one at a time, not in every
combination. The scanner waits for a stable UIA structure and checks selected
tabs before and after each page capture; this cannot establish app-specific
loading or business readiness.

Disabled, offscreen, unrealized or unsupported tabs are reported as coverage
gaps, not operated through a fallback click. This mode does not open menus,
dialogs, links or arbitrary navigation items, and it does not scroll to reveal hidden content.
The app itself may move its tab strip when a tab is selected. An unexpected
dialog or visible owned popup (including a
separate tooltip window), changed window identity, or changed tab inventory
stops further navigation. Dismiss the popup before starting a fresh run;
the scanner does not close it for you. If no standard tab groups are found,
only the current page is captured and navigation is `not-applicable`, not a
failure. If groups exist but cannot be traversed, their coverage remains partial.

The scanner attempts to restore the original tab selection only while its
scope remains valid. It never restarts or closes the app, and restoring a tab
does not undo settings or other app side effects. Read the restoration result
before continuing. If the original tab has moved offscreen, return to it
manually; the scanner will not scroll or click to restore it.
Exit code `2` means partial or blocked collection; retain
the report, resolve its stated cause, and start a fresh output directory.
Rule-failure counts are occurrences across page scans, not deduplicated
whole-app issue counts.

If the report says **Not evaluated**, no usable page audit results were
collected. The JSON has `hasAuditResults: false` and `ruleFailureCount: null`;
this is not a zero-issue result. For a settling timeout, retry in a fresh
directory with `--timeout-ms 60000`, or use `--current-page` below to inspect
the currently displayed page without tab navigation.

## Audit only the current page

```powershell
.\winapp.exe ui audit -w 123456 --current-page --output .\page-report
```

This does not select tabs, type or change settings. Read `page-report\report.html`
and its assessment-compatible `scan.json`. `--no-screenshot` omits images;
`--observe-seconds 5` adds passive event observation. Do not combine
`--current-page` with `--max-pages`.

Only when a real keyboard probe is wanted, add
`--tab-steps 8 --accept-input --observe-seconds 12` to `--current-page`.
This sends Tab/Shift+Tab to examine focus, not to switch through tab pages.
The bounded trace is not a keyboard-trap verdict or actual screen-reader speech.

## Run the included real application workflow

The included WinUI project requires its normal build prerequisites, including
the .NET 10 SDK. Package restore uses the standard NuGet configuration hierarchy;
use your approved proxy or feed configuration when public feeds are unavailable.

```powershell
.\winapp.exe ui accessibility loop .\examples\loop-demo.json `
    --accept-actions --output .\workflow-report
```

This builds and launches an isolated source copy of the three-page demo,
executes its configured keyboard and control interactions, and repeats the
same route in a fresh process. It does not invoke an AI service or change the
original example. Keep the desktop unlocked and the owned app in the foreground
while real input runs. Do not type or switch to other windows during the route.

The example intentionally contains missing accessible names. Read
`workflow-report\loop-report.html`; reproduction, repair verification, and
capture completeness are separate results.

Source repair requires additional explicit approval. Inspect
`winapp ui accessibility loop --help` before using `--apply`; the existing loop
can call Copilot for bounded proposals, or consume reviewed proposal files.
Do not mistake a configured route for autonomous AI exploration.

## Use the included skill

The bundle contains the portable plugin under `plugin` and a project-scoped
`winapp-accessloop` skill under `.github\skills`. Start your agent in the
extracted bundle directory so it can discover the project skill. Use the
bundled `.\winapp.exe`, not a different installed version of `winapp`.

The skill coordinates real commands and reads their reports. It does not add
executable capabilities or turn unsupported controls into supported ones.

## Local bundle scope

This bundle exposes the existing implemented workflows through WinAppCLI.
Automatic standard-tab scans, configured navigation and constrained source
repair are real operations. Other profile-free navigation controls, a general
observed-plan API, and broader repair support are not provided by this package.
