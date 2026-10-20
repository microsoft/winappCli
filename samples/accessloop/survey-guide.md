# Audit an installed app with a resumable page survey

Open **PowerToys Settings**, keep it visible, and run the survey from the
repository root after building AccessLoop:

```powershell
dotnet build .\samples\accessloop\AccessLoop\AccessLoop.csproj -c Release
$accessloop = (Resolve-Path .\samples\accessloop\AccessLoop\bin\Release\net10.0\AccessLoop.exe).Path
$winapp = (Resolve-Path .\artifacts\cli\win-x64\winapp.exe).Path
$profile = (Resolve-Path .\samples\accessloop\profiles\powertoys-settings.json).Path

& $winapp ui list-windows -a PowerToys.Settings --json
# Replace 526648 with the HWND from the output above.
& $accessloop survey $profile --window 526648 --accept-navigation --winapp $winapp --output .\powertoys-audit
Invoke-Item .\powertoys-audit\survey-report.html
```

**No PowerToys source or build is required.** The command connects to the
already-running settings window, discovers the profile's permitted internal
navigation pages, confirms each selected page and heading, and produces a
combined report with links to individual UIA/Axe/screenshot reports.
It does not launch, restart or close PowerToys, change feature settings,
edit source code, or call an AI service.

AccessLoop is the tool being built in the example; the native `winapp` build
must already include the audit commands. Use the native executable, not an npm
shim. Restore the tools through your approved NuGet configuration if required;
do not change machine-wide sources on a shared computer.

## Choose the right AccessLoop mode

| Mode | Target and effect |
|---|---|
| `scan` | One state of an existing window; read-only by default. |
| `survey` | Existing app, approved navigation profile, multiple page reports, pause/resume and retries. No source changes. |
| `journey` | A source-owned demo copy; builds, navigates and performs its declared keyboard exercises, with separately approved repairs. |

All page captures use the same automatic report and evidence format. A skill
can guide an agent through these commands; installing a skill alone does not
install or implement the commands.

## Navigation permission and preflight

`--accept-navigation` approves the profile's navigation operations. They can
move focus, scroll the navigation pane, expand/collapse navigation groups and
select internal settings pages. This is not an entirely passive screenshot.

The PowerToys profile restricts discovery to `PaneRoot` / `MenuItemsHost` and
WinUI `NavigationViewItem` controls. It declares the permitted navigation
groups and excludes welcome, release-notes and feedback entries from page
exploration. It does not operate content-area switches, update buttons,
restart/elevation actions, editors, external links or other tool windows.
The whole visible window is still audited, so excluded navigation links can
appear as controls with rule findings without being opened.

Before navigation, the tool checks the selected process/window and whether
the window and interactive desktop are usable. If the window is minimized or
the desktop is locked, restore/unlock it and retry; the tool will not bypass
those conditions. Keep the window size, display scaling and theme unchanged
while running. The app can still perform its normal background activity.

At the end of an invocation, including a pause, the tool attempts to restore
the recorded selected page and navigation-group states. Read the restoration
result: failed restoration is not silently reported as success. Exact scroll
position and application-data rollback are not promised.

## Pause and resume

For a short first demonstration, intentionally pause after three page attempts:

```powershell
& $accessloop survey $profile --window 526648 --accept-navigation --winapp $winapp --output .\powertoys-audit --stop-after 3
```

Ctrl+C also requests a pause. A page interrupted before its final checks is
not marked complete; any retained evidence stays in its own attempt directory.

When the same PowerToys process/window is still running and you have **not
changed its feature settings or test data**, continue:

```powershell
& $accessloop survey --resume .\powertoys-audit --accept-navigation --confirm-unchanged --winapp $winapp
```

Resume verifies the frozen profile/inventory, existing evidence hashes,
original process/window, installed app identity, tool binaries and recorded
environment. It skips pages already captured and retries pending or blocked
pages in new directories. Only one process can hold the job lock.

`--confirm-unchanged` is **your declaration** that app settings and test data
did not change while paused. Process, version and file checks cannot establish
that fact. Do not pass it when you cannot make that declaration. If the app was
restarted, updated, reconfigured, or the tool/profile changed, start a new job
with a new output directory rather than editing fingerprints to force reuse.
Restore the original window dimensions, DPI or system theme if an environment
check reports a mismatch.

Do not rebuild/update AccessLoop or winapp between pausing and resuming the
same job. A different tool binary needs a new run, even if its displayed
version string is unchanged.

### Retry a specific page

To obtain a fresh attempt for one page, including an already captured page:

```powershell
& $accessloop survey --resume .\powertoys-audit --accept-navigation --confirm-unchanged --winapp $winapp --retry-page GeneralNavItem
```

Repeat `--retry-page` for a small explicit set. IDs must exist in the frozen
inventory; they are not arbitrary selectors. Prior attempts remain available.
If the new attempt is blocked, the overview shows that latest outcome instead
of silently presenting the old successful capture as current.

## Reports and status

Open `survey-report.html`. Start with **巡检摘要** and **问题概览**, then use
**按页面查看** to open a page's issue descriptions and control images.
The default view keeps coverage limits, blocked pages and restoration problems
visible. Page identifiers, detailed timings, retry history and fingerprints
are available in expandable details rather than repeated in the main list.
Cumulative time is shown in minutes and seconds, with explicitly requested
page retries separated from traversal segments; time spent paused is excluded.
See the [single-page report guide](README.md) for the page-level reading flow.

Genuine rule failures remain separate from ordinary collection information,
candidate concerns and unknown/untested coverage. Collapsing a record does not
remove it from the report or change its result.

Common navigation findings may recur on many pages. The overview links their
occurrences rather than treating every repetition as an independent defect.
Page-local or ambiguously identified findings remain separate; grouped-entry
counts are not a certified count of unique root causes.

| Result | Meaning |
|---|---|
| Job `finished` | Every page in the frozen navigation inventory has a completed, navigation-confirmed latest attempt. Not WCAG compliance. |
| Job `paused` | More work or an actionable blocker remains; completed pages are retained. |
| Page capture `partial` | The page report contains explicit collection gaps. It can coexist with confirmed page navigation. |
| Rule failures | Actual engine/check failures, not the number of metadata or coverage records. |

WinUI can withhold properties under an unknown privacy boundary. Those fields
stay unknown/withheld; a completed survey does not turn them into passes.
The profile uses explicit heading aliases for known differences between
navigation and content labels, rather than guessing that any changed screen
is the requested page.

PowerToys Home and Command Palette expose their visible titles without the
usual heading AutomationId. Only those explicitly listed pages can use a
unique matching visible text label outside the navigation pane, in the
profile's upper content-heading region, with stable pane geometry. Lower
body text with the same wording does not qualify. The recorded
`evidenceKind` distinguishes this profiled positional check from the normal
heading-ID-and-name check; arbitrary missing heading IDs are not silently
accepted. Leading/trailing whitespace is ignored in title comparison, but
different wording still requires an explicit profile alias.

Exit codes are **0** for a finished run with complete captures and no rule
failures, **1** for complete captures with rule failures, **2** for partial
captures or a preflight/error condition, and **3** for a paused job. Read the
structured statuses as well as the exit code.

Add `--json` for the final machine-readable stdout summary. Progress events
are emitted separately on stderr. `--no-screenshot` is a capture choice for
a new job; it is frozen for resume and does not suppress other missing data.

## Evidence layout

```text
powertoys-audit\
  profile.json                 frozen navigation policy
  survey-request.json          original target, versions and environment
  inventory.json               frozen page list and restoration state
  survey-state.json             atomic progress and attempt history
  survey-report.html            current overview
  pages\
    001\
      attempt-01\               scan.json, report.html, raw evidence, images
      attempt-02\               explicit retry; attempt-01 is unchanged
```

Keep the whole directory when sharing a report. Snapshots, accessible labels
and images may contain local information; inspect them before sharing.
The hashes detect changed bytes; they are not an author signature.

## Application profiles

The shipped `profiles\powertoys-settings.json` describes navigation scope,
group IDs, exclusions, a content-heading ID, exact heading aliases and
collection budgets. It is a policy for the installed UI version, not PowerToys
source code.

This first reusable adapter supports explicitly profiled WinUI navigation
menus. A different framework or navigation behavior needs a verified adapter,
not a broader click-everything rule. The inventory covers observed internal
navigation leaves, not every popup, collapsed settings section, login state,
conditional view or possible application state.
