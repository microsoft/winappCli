# Writing the host-side batch

### Step 2: Write the host-side batch

Create `ui-tests.ps1`, replace the sample AutomationIds/expected values with the app's requirements, and add the relevant examples from `SKILL.md` and `references/advanced-interactions.md` **inside the outer `try`**, before its `finally`. The two small command helpers only route and check CLI calls; they do not implement a target manager. Use them for **every** added command so an earlier native failure cannot be hidden by a later success.

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
