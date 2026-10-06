---
name: winui-ui-testing
description: "Automated UI testing for Windows desktop apps — generate a batch test script with the `winapp ui` UI Automation harness (WinApp CLI 0.7+), run all tests in one pass, read results. Covers assertions, interactions, keyboard/touch/pen input, file pickers, dialogs, persistence, accessibility, and screenshot/video capture for Win32, WPF, WinForms, and WinUI 3, in Windows Sandbox or locally."
---

### Scope and execution boundary

`winapp ui` uses Windows UI Automation (UIA), so the AutomationId-based workflow works with any Windows desktop framework, packaged or unpackaged. Skip WinUI-specific binding/dialog advice for other frameworks.

Windows Sandbox keeps synthetic input off the user's desktop; target selection is in Step 1. Discover the installed contract with `winapp run --help`, `winapp target --help`, and `winapp ui <verb> --help`. Use `--on sandbox`, not `--sandbox` or a top-level `sandbox` command. Do not enable Windows features or launch an app without the task's permission.

- Prerequisites and enablement: see `winui-setup`.
- Project builds/publishes execute on the **host**; deployment, app launch, and `ui --on sandbox` execute in the **guest**. Run the batch script below on the host, not inside `target exec` (which would double-route).
- Real input and capture require an unlocked host and a connected, nonminimized Sandbox client. Tree inspection may work while input cannot; a readable tree is not an input-readiness check.
- `winapp target snapshot sandbox --json` is a read-only readiness query: it neither starts nor reconnects a guest. Use it to diagnose readiness rather than probing the user's desktop.
- The persistent guest is shared, not isolation between mutually untrusted workflows. Coordinate with other users; there is no supported `--unique-identity` option. Use separate machines for mutually untrusted work.
- Guest `--debug-output` supports **packaged** apps only.

### Approach and command discovery

Prefer a single scripted batch over a long series of interactive calls. If you wrote the app, use its XAML/source AutomationIds directly; otherwise inspect its current tree and read source for hidden/lazy flyouts and dialogs. Either way, validate a real window/tree before passing an accessibility audit.

Core verbs: `list-windows`, `inspect`, `search`, `get-property`, `get-value`, `wait-for`; `invoke`, `click`, `set-value`, `focus`, `scroll-into-view`; `send-keys`, `hover`, `drag`, `touch`, `pen`; `screenshot`, `record`. Use each verb's `--help` for selectors and options, rather than guessing from this short reference.

### Step 1: Select a target, then keep the PID and target together

Prefer `sandbox` when Windows Sandbox is available; otherwise tell the user and choose `local`. **If the user explicitly requested Windows Sandbox and it is unavailable, stop** and point them to the enablement steps in `winui-setup`. A stopped guest does not prove unavailability: `target snapshot` can report no running target while the feature is enabled. App build/test failures are not Sandbox unavailability. The same policy applies to diagnostics: unpackaged apps can't use guest `--debug-output`, so diagnose them locally unless Sandbox was explicitly requested.

Pass the selected target to the template: it launches with `winapp run . --on sandbox --detach --json` for a guest, or omits `--on sandbox` for local execution. Reuse an already-running app only when its captured target matches the selected target and the guest has not been recreated. Never pass a guest PID to default-host `winapp ui`. If a target becomes unavailable after selection, report it and select again under the same policy; the script itself never retries in another target.

The run JSON includes **`ProcessId`**, **`ProcessScope`**, and **`UiTargetArgs`** (PascalCase). In Windows Sandbox, `UiTargetArgs` is **`--on sandbox -a <guestPID>`**: preserve both parts, not just the number. For an explicitly requested **unpackaged** guest run, the launch result has no app PID: launch separately, find the intended app by process/title with target-wide `winapp ui list-windows --on sandbox --json`, then pass its PID with `-Target sandbox -AppPid <PID> -AppScope sandbox`. Never use the containment PID. A fresh guest invalidates old PIDs/HWNDs.

### Step 2: Write the host-side batch

Create `ui-tests.ps1`, replace the sample AutomationIds/expected values with the app's requirements, and add the relevant examples below **inside the outer `try`**, before its `finally`. The two small command helpers only route and check CLI calls; they do not implement a target manager. Use them for **every** added command so an earlier native failure cannot be hidden by a later success.

```powershell
# ui-tests.ps1
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [ValidateSet('sandbox', 'local')][string]$AppScope,
    [ValidateNotNullOrEmpty()][string]$WorkflowId = [guid]::NewGuid().ToString('N'),
    [string]$ArtifactDirectory = (Join-Path '.\ui-test-artifacts' ([guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ScopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$uiStarted = $false

function Invoke-WinAppChecked {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    $code = $global:LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
    }
    $output
}

function Invoke-Ui {
    param([string[]]$Arguments, [string]$Window)
    $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + $selector)
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        & $Script | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
    }
}

function Save-Screenshot {
    param([string]$Name)
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Choose a new evidence path: $path" }
    Invoke-Ui @('screenshot', '--output', $path) -Window $hwnd | Out-Null
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Screenshot was not delivered to the host: $path"
    }
    $screenshots.Add($path)
}

try {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    if ($AppPid -lt 0) { throw 'AppPid must be positive, or zero to launch.' }
    if ($AppPid -gt 0) {
        if ($AppScope -ne $Target) {
            throw 'Reusing a PID requires matching -AppScope and -Target from the same live target.'
        }
    } else {
        $launch = Invoke-WinAppChecked (@('run', '.') + $ScopeArgs + @('--detach', '--json')) |
            ConvertFrom-Json
        if ($launch.Error -or -not $launch.ProcessId -or [int]$launch.ProcessId -le 0) {
            throw "No app ProcessId. For unpackaged guest runs, discover the app in that target and rerun with -AppPid and -AppScope. $($launch.Error)"
        }
        $AppPid = [int]$launch.ProcessId
        if ($Target -eq 'sandbox' -and
            ($launch.ProcessScope -ne 'sandbox' -or $launch.UiTargetArgs -ne "--on sandbox -a $AppPid")) {
            throw 'Launch response is missing the expected Sandbox ProcessScope / UiTargetArgs pair.'
        }
        if ($Target -eq 'local' -and $launch.ProcessScope -and $launch.ProcessScope -ne 'local') {
            throw 'Launch response belongs to a different target.'
        }
    }

    $uiStarted = $true
    $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
    $main = $windows | Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.title -ne 'PopupHost' } |
        Select-Object -First 1
    if (-not $main) { throw 'No app window found in the selected target; rediscover after a fresh guest.' }
    $hwnd = [string]$main.hwnd

    Test-UI 'Initial screenshot delivered' { Save-Screenshot '01-initial.png' }
    Test-UI 'Home exists' { Invoke-Ui @('wait-for', 'NavHome', '-t', '3000') }
    Test-UI 'Navigate to Settings' {
        Invoke-Ui @('invoke', 'NavSettings')
        Invoke-Ui @('wait-for', 'TxtUserName', '-t', '3000')
    }
    Test-UI 'Save username' {
        Invoke-Ui @('set-value', 'TxtUserName', 'TestUser')
        Invoke-Ui @('invoke', 'BtnSave')
        Invoke-Ui @('wait-for', 'TxtUserName', '--value', 'TestUser', '-t', '2000')
    }
    Test-UI 'Default values' {
        Invoke-Ui @('wait-for', 'CmbTheme', '--value', 'System default', '-t', '2000')
        Invoke-Ui @('wait-for', 'TglLogging', '--value', 'Off', '-t', '2000')
    }
    Test-UI 'App controls have AutomationIds' {
        $inspection = Invoke-Ui @('inspect', '--interactive', '--json') -Window $hwnd | ConvertFrom-Json
        function Get-Elements($nodes) {
            foreach ($node in $nodes) {
                if ($null -ne $node) { $node; Get-Elements $node.children }
            }
        }
        $allElements = @(Get-Elements @($inspection.windows | ForEach-Object { $_.elements }))
        if (-not $allElements.Count) { throw 'Inspection returned no elements; not an accessibility PASS.' }
        $appElements = @($allElements | Where-Object {
            $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit' -and
            $_.name -notmatch 'Minimize|Maximize|Close|System' -and
            $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
        })
        if (-not $appElements.Count) { throw 'No app controls were audited; adjust selectors rather than passing.' }
        $missingId = @($appElements | Where-Object { -not $_.automationId })
        if ($missingId.Count) {
            throw "Missing AutomationIds: $(($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ')"
        }
    }
    Test-UI 'Final screenshot delivered' { Save-Screenshot '02-settings.png' }
} catch {
    $results.Add(@{ name = 'Target setup / test execution'; status = 'FAIL'; detail = "$_" })
} finally {
    try {
        if ($uiStarted) { Invoke-WinAppChecked (@('ui', 'yield') + $ScopeArgs) | Out-Null }
    } catch {
        $results.Add(@{ name = 'Release UI workflow'; status = 'FAIL'; detail = "$_" })
    } finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
    }
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    target = $Target; appPid = $AppPid; workflowId = $WorkflowId
    passed = $results.Count - $failed; failed = $failed
    results = @($results.ToArray()); screenshots = @($screenshots.ToArray())
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath '.\test-results.json' -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: .\test-results.json"
if ($failed) { exit 1 }
exit 0
```

Within a test, **throw**, not `exit`; the script fails on CLI errors, empty inspections, and undelivered evidence. Screenshots target the known main HWND so each lands at one exact host path; use `--capture-screen` when popups must be included.

### Step 3: Run, read, and visually verify

```powershell
# Preferred when Windows Sandbox is available:
.\ui-tests.ps1 -Target sandbox
if ($LASTEXITCODE -ne 0) { throw 'UI batch failed; read test-results.json.' }
$report = Get-Content -LiteralPath '.\test-results.json' -Raw | ConvertFrom-Json
$report.screenshots
```

For a captured, still-live guest PID, use `.\ui-tests.ps1 -Target sandbox -AppPid 1234 -AppScope sandbox`. For a local run (see Step 1), use `.\ui-tests.ps1 -Target local`, optionally with a local PID and `-AppScope local`. Never reuse a guest PID locally or switch targets to make failing tests pass.

View **each host PNG** listed in `test-results.json` with the image-viewing tool. UIA PASS cannot detect clipping, overlap, incorrect theming, or content bleeding past its container. Fail visual review for unintended scrollbars, unintended ellipses, clipped hero/right-edge controls, overlapping rows, unbalanced whitespace, cramped/vast spacing, incorrect Light/Dark/High Contrast, or missing focus/hover/error states. Capture meaningful states immediately after their interactions, not just at the end.

If fixes are requested, batch-fix failures, rebuild/relaunch with the same target using the template (omit `-AppPid` to launch again), and capture the new PID. Maximum **two** fix-and-rerun cycles; then report remaining failures. Do not declare completion without the structured results **and** visual review.

### Assertions and coverage

Write tests for every requested requirement. Use `wait-for --value` for TextBox/NumberBox values, RichEditBox text, ComboBox selection, toggle/checkbox `On`/`Off`, and TextBlock/label text. `--contains` matches a substring; `--gone` waits for disappearance. Use `-p IsEnabled --value True` only for a specific property. Invoke buttons/nav items, wait for page-specific elements, and test default/error/empty states.

The following examples are additions to the template, not independent shells. `Invoke-Ui` always supplies the chosen scope plus `-a $AppPid`, or scope plus `-w $Window`. For raw reads, pass `--json` and parse it; plain stdout can include advisory messages. Use `scroll-into-view` before asserting a virtualized item below the fold.

```powershell
Test-UI 'Status and enabled state' {
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '3000')
    Invoke-Ui @('wait-for', 'BtnSave', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
    $value = Invoke-Ui @('get-value', 'TxtUserName', '--json') | ConvertFrom-Json
    if ($value.text -ne 'TestUser') { throw 'Wrong username.' }
}
```

### File pickers, flyouts, and ContentDialogs

Pickers run in a separate `PickerHost` process. Discover their HWND in the **same target**; `-w` changes the window selector, never the target. Replace the filename with a file that exists **in the guest** for Sandbox testing.

```powershell
Test-UI 'Open file picker' {
    Invoke-Ui @('invoke', 'BtnOpenFile')
    Start-Sleep -Seconds 1
    $allWindows = @(Invoke-WinAppChecked (@('ui', 'list-windows', '--json') + $ScopeArgs) | ConvertFrom-Json)
    $pickers = @($allWindows | Where-Object { $_.hwnd -and [string]$_.ownerHwnd -eq $hwnd -and $_.title -match 'Open|Save' })
    if ($pickers.Count -ne 1) { throw 'Expected one picker; inspect the selected target again.' }
    $pickerHwnd = [string]$pickers[0].hwnd
    Invoke-Ui @('inspect', '--interactive', '--json') -Window $pickerHwnd
    Invoke-Ui @('set-value', 'FileNameControlHost', 'test.txt') -Window $pickerHwnd
    Invoke-Ui @('invoke', 'Open') -Window $pickerHwnd
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'opened', '--contains', '-t', '3000')
}

Test-UI 'Copy context menu' {
    Invoke-Ui @('click', 'LstItems', '--right')
    Invoke-Ui @('wait-for', 'MnuCopy', '-t', '2000')
    Invoke-Ui @('invoke', 'MnuCopy')
    Invoke-Ui @('wait-for', 'StatusText', '--value', 'Copied', '-t', '2000')
}

Test-UI 'Confirm deletion' {
    Invoke-Ui @('invoke', 'BtnDelete')
    Invoke-Ui @('wait-for', 'Primary', '-t', '2000')
    Invoke-Ui @('invoke', 'Primary')
    Invoke-Ui @('wait-for', 'Primary', '--gone', '-t', '3000')
}
```

For Save/Cancel pickers, substitute the appropriate action and assertion. MenuBar headers and flyout items use `invoke` and `wait-for` similarly. ContentDialogs live in the app window; `Primary`, `Secondary`, and `Close` are common selectors, but inspect the actual open dialog because custom AutomationIds are not guaranteed.

### Keyboard, hover, drag, touch, and pen

`send-keys --via send-input` is required for accelerators and reliable per-character input in WinUI/WPF text controls. `post-message` is HWND-targeted but does not produce per-character `KeyDown`. `--target` focuses first; `--verbatim` types literal key names. System shortcuts require the CLI's explicit permission flags; do not use them as a workaround for desktop-readiness errors.

```powershell
Test-UI 'Keyboard save' {
    Invoke-Ui @('send-keys', 'hello world', '--target', 'TxtName', '--via', 'send-input')
    Invoke-Ui @('send-keys', 'ctrl+s', '--via', 'send-input')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '2000')
}
Test-UI 'Tooltip' {
    Invoke-Ui @('hover', 'BtnInfo')
    Invoke-Ui @('wait-for', 'InfoTooltip', '-t', '2000')
}
Test-UI 'Reorder items' {
    Invoke-Ui @('drag', 'ItemA', 'ItemB')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'reordered', '--contains', '-t', '2000')
}
Test-UI 'Touch and ink input' {
    Invoke-Ui @('touch', 'LstFeed', '-g', 'swipe', '--direction', 'up', '--distance', '400')
    Invoke-Ui @('pen', 'InkCanvas', '--path', '50,50 120,80 200,60', '--pressure', '0.8')
    Save-Screenshot '03-ink.png'
    # Add the app-specific state assertion; successful injection alone is not functional proof.
}
```

Use `winapp ui drag --help`, `winapp ui touch --help`, and `winapp ui pen --help` for coordinates, long-press/dwell, pinch/stretch, pressure/tilt/eraser support. Read coordinates in the guest tree; host coordinates are not guest coordinates.

### Recordings and workflow coordination

```powershell
Test-UI 'Video delivered' {
    $video = Join-Path $ArtifactDirectory 'flow.mp4'
    if (Test-Path -LiteralPath $video) { throw 'Choose a new video evidence path.' }
    Invoke-Ui @('record', '--duration-sec', '6', '--fps', '30', '--output', $video)
    if (-not (Test-Path -LiteralPath $video -PathType Leaf) -or (Get-Item -LiteralPath $video).Length -eq 0) {
        throw "Recording was not delivered to the host: $video"
    }
}
```

Sandbox screenshot/record **`--output` paths are host paths**, automatically delivered when capture completes; do not pull them from guessed guest directories. Use fresh output paths and check the exit status and actual host files. `--capture-screen` can include popups outside the app window. For an interrupted recording, preserve `stopReason`, `partialOutput`, `recoveryHint`, and the reported partial/recovery paths; a nonempty partial file is not necessarily playable.

WinApp CLI arbitrates desktop-changing commands automatically. Without a workflow ID, each command releases its turn immediately; with one, the four-second grace covers tight script bursts, not agent reasoning. Use the **same `WINAPP_UI_WORKFLOW_ID`** for cooperating commands, including concurrent recording and interaction; independent flows need distinct IDs. The template accepts `-WorkflowId` for this purpose, sets it in its own process, restores the previous value, and calls `winapp ui yield --on sandbox` in `finally`. Fresh shell tool calls do not inherit changes from an earlier shell: set the same ID on **every** cooperating invocation. A separate concurrent recording process must also check its exit code/delivery, use the same scoped PID, and finish before the test runner yields. Do not start an independent-ID recording that blocks the interactions it is meant to capture. After a pause, inspect again and reopen menus/dialogs another workflow may have changed.

### Guest persistence and arbitrary file transfer

Do not inspect **host** `$env:LOCALAPPDATA` to prove **guest** persistence. Check the actual app data file in the selected target. Replace the sample package family/path with the app's real storage location; unpackaged apps usually use an app-specific LocalAppData directory.

```powershell
Test-UI 'Username persisted in selected target' {
    $readSettings = @'
$ErrorActionPreference = 'Stop'
$file = Join-Path $env:LOCALAPPDATA 'Packages\YourPackageFamily\LocalState\settings.json'
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing settings: $file" }
Get-Content -LiteralPath $file -Raw
'@
    $json = if ($Target -eq 'sandbox') {
        Invoke-WinAppChecked @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-Command', $readSettings)
    } else {
        & ([scriptblock]::Create($readSettings))
    }
    $settings = $json | ConvertFrom-Json
    if ($settings.UserName -ne 'TestUser') { throw 'Saved username does not match.' }
}
```

For arbitrary setup/results files, use `target exec` / `push` / `pull`, not host filesystem guesses. Transfer guest paths are **relative to snapshot `workRoot`**, normally `C:\WinApp\work`, not absolute guest paths. The following optional additions assume a host `setup.ps1` that creates `Results` under the guest work root:

```powershell
Test-UI 'Guest setup and result transfer' {
    if ($Target -ne 'sandbox') { throw 'This transfer test requires Sandbox.' }
    $snapshot = Invoke-WinAppChecked @('target', 'snapshot', 'sandbox', '--json') | ConvertFrom-Json
    if (-not $snapshot.workRoot) { throw 'No live guest workRoot; fix readiness first.' }
    Invoke-WinAppChecked @('target', 'push', 'sandbox', '.\setup.ps1', 'Setup\setup.ps1')
    $guestSetup = $snapshot.workRoot.TrimEnd('\') + '\Setup\setup.ps1'
    Invoke-WinAppChecked @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-File', $guestSetup)
    Invoke-WinAppChecked @('target', 'pull', 'sandbox', 'Results', (Join-Path $ArtifactDirectory 'results'))
}
```

Keep the guest alive while recovering failed delivery. Preserve evidence/recovery paths and app data needed for diagnosis before any **user-consented** shutdown; never stop all Sandboxes as routine cleanup.

### Binding and selector gotchas

- `set-value` does not commit default WinUI `x:Bind TwoWay` TextBox bindings until focus changes. Prefer `UpdateSourceTrigger=PropertyChanged` in the app; otherwise `invoke` Save or `focus` another control before checking persistence.
- RichEditBox/RichTextBox generally need `send-keys --via send-input`, not UIA `set-value`.
- Use `$AppPid`, never PowerShell's read-only automatic `$Pid`.
- A picker HWND needs `-w` **and the same target scope**. A fresh guest invalidates both HWNDs and PIDs; do not recycle either.
