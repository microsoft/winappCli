<#
.SYNOPSIS
End-to-end test for WinApp CLI UI automation commands with a WinUI 3 app.

.DESCRIPTION
This script tests all winapp ui commands against the WinUI 3 sample app:
1. Builds the WinUI sample app
2. Launches it with winapp run --detach
3. Tests all winapp ui commands (inspect, search, invoke, set-value, etc.)
4. Uses wait-for assertions to verify UI state
5. Takes screenshots at each stage
Requires MSVC and a Windows SDK for the direct UI Automation parent-navigation test.

.PARAMETER WinAppPath
Path to the winapp CLI executable. Default: auto-detect from repo artifacts.

.PARAMETER SkipBuild
Skip building the sample app (useful if already built).

.PARAMETER ScreenshotDir
Directory to save screenshots. Default: repo-root/test-screenshots

.EXAMPLE
.\test-e2e-winui-ui.ps1
Run with defaults (builds app, auto-detects winapp).

.EXAMPLE
.\test-e2e-winui-ui.ps1 -WinAppPath "C:\path\to\winapp.exe" -SkipBuild
Run against a specific winapp binary without rebuilding.
#>

param(
    [string]$WinAppPath,
    [switch]$SkipBuild,
    [string]$ScreenshotDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ============================================================================
# Configuration
# ============================================================================

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$sampleDir = Join-Path $repoRoot "samples\winui-app"
$Platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
$rid = $Platform.ToLower()
$buildOutput = Join-Path $sampleDir "bin\$Platform\Debug\net10.0-windows10.0.26100.0"

if (-not $ScreenshotDir) {
    $ScreenshotDir = Join-Path $repoRoot "artifacts\screenshots"
}
New-Item -ItemType Directory -Path $ScreenshotDir -Force | Out-Null

# Auto-detect winapp path
if (-not $WinAppPath) {
    $candidates = @(
        (Join-Path $repoRoot "artifacts\cli\win-$rid\winapp.exe"),
        (Join-Path $repoRoot "src\winapp-CLI\WinApp.Cli\bin\Release\net10.0-windows10.0.19041.0\win-$rid\winapp.exe"),
        (Join-Path $repoRoot "src\winapp-CLI\WinApp.Cli\bin\Debug\net10.0-windows\win-$rid\winapp.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $WinAppPath = $c; break }
    }
    if (-not $WinAppPath) {
        throw "winapp.exe not found. Build with .\scripts\build-cli.ps1 or pass -WinAppPath."
    }
}

# Resolve to absolute path so it works after Push-Location
$WinAppPath = (Resolve-Path $WinAppPath).Path

# ============================================================================
# Helpers
# ============================================================================

$script:testsPassed = 0
$script:testsFailed = 0
$script:testResults = @()

function Write-TestHeader {
    param([string]$Message)
    Write-Host "`n$('='*70)" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "$('='*70)" -ForegroundColor Cyan
}

function Write-TestPass {
    param([string]$Name, [string]$Detail = "")
    $script:testsPassed++
    $script:testResults += @{ Name = $Name; Status = "PASS"; Detail = $Detail }
    $msg = if ($Detail) { "PASS  $Name — $Detail" } else { "PASS  $Name" }
    Write-Host $msg -ForegroundColor Green
}

function Write-TestFail {
    param([string]$Name, [string]$Detail = "")
    $script:testsFailed++
    $script:testResults += @{ Name = $Name; Status = "FAIL"; Detail = $Detail }
    $msg = if ($Detail) { "FAIL  $Name — $Detail" } else { "FAIL  $Name" }
    Write-Host $msg -ForegroundColor Red
}

function Invoke-Winapp {
    param([string[]]$WinappArgs)
    $env:WINAPP_CLI_TELEMETRY_OPTOUT = "1"
    $output = & $WinAppPath @WinappArgs 2>$null
    $exitCode = $LASTEXITCODE
    return @{ Output = ($output -join "`n"); ExitCode = $exitCode }
}

function Assert-WinappSuccess {
    param([string]$TestName, [string[]]$WinappArgs)
    $result = Invoke-Winapp $WinappArgs
    if ($result.ExitCode -eq 0) {
        Write-TestPass $TestName
    } else {
        Write-TestFail $TestName "Exit code $($result.ExitCode): $($result.Output)"
    }
    return $result
}

function Assert-WinappOutputContains {
    param([string]$TestName, [string[]]$WinappArgs, [string]$Expected)
    $result = Invoke-Winapp $WinappArgs
    if ($result.ExitCode -eq 0 -and $result.Output -match [regex]::Escape($Expected)) {
        Write-TestPass $TestName $Expected
    } elseif ($result.ExitCode -ne 0) {
        Write-TestFail $TestName "Exit code $($result.ExitCode): $($result.Output)"
    } else {
        Write-TestFail $TestName "Expected '$Expected' in output but got: $($result.Output.Substring(0, [Math]::Min(200, $result.Output.Length)))"
    }
    return $result
}

# Asserts the command fails (non-zero exit) AND its combined stdout+stderr contains an expected
# substring. Used for graceful-failure paths whose message is written to stderr (which Invoke-Winapp
# discards), e.g. set-value on a control that supports no settable UIA pattern.
function Assert-WinappFailureContains {
    param([string]$TestName, [string[]]$WinappArgs, [string]$Expected)
    $env:WINAPP_CLI_TELEMETRY_OPTOUT = "1"
    $stderrFile = New-TemporaryFile
    try {
        $stdout = & $WinAppPath @WinappArgs 2>$stderrFile
        $exitCode = $LASTEXITCODE
        $stderr = Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue
    } finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }
    $combined = (@($stdout) -join "`n") + "`n" + [string]$stderr
    if ($exitCode -ne 0 -and $combined -match [regex]::Escape($Expected)) {
        Write-TestPass $TestName "exit $exitCode, matched '$Expected'"
    } elseif ($exitCode -eq 0) {
        Write-TestFail $TestName "Expected non-zero exit but got 0"
    } else {
        Write-TestFail $TestName "Exit $exitCode but '$Expected' not found in: $($combined.Substring(0, [Math]::Min(300, $combined.Length)))"
    }
    return @{ ExitCode = $exitCode; Output = $combined }
}

function Assert-WinappJsonField {
    param([string]$TestName, [string[]]$WinappArgs, [string]$Field, [string]$Expected)
    $result = Invoke-Winapp $WinappArgs
    if ($result.ExitCode -ne 0) {
        Write-TestFail $TestName "Exit code $($result.ExitCode)"
        return $result
    }
    try {
        $json = $result.Output | ConvertFrom-Json
        $actual = $json
        foreach ($part in $Field.Split('.')) {
            if ($part -match '^\d+$') { $actual = $actual[[int]$part] }
            else { $actual = $actual.$part }
        }
        if ("$actual" -match [regex]::Escape($Expected)) {
            Write-TestPass $TestName "$Field contains '$Expected'"
        } else {
            Write-TestFail $TestName "$Field = '$actual', expected '$Expected'"
        }
    } catch {
        Write-TestFail $TestName "JSON parse error: $_"
    }
    return $result
}

function Assert-ScreenshotValid {
    param([string]$TestName, [string]$Path, [int]$MinSizeKB = 2)
    if (-not (Test-Path $Path)) {
        Write-TestFail $TestName "File not found: $Path"
        return
    }
    $file = Get-Item $Path
    $sizeKB = [Math]::Round($file.Length / 1024, 1)

    if ($file.Length -lt ($MinSizeKB * 1024)) {
        Write-TestFail $TestName "Too small (${sizeKB}KB < ${MinSizeKB}KB) — likely blank"
        return
    }

    # Check for single-color images by looking at PNG compressed size vs dimensions.
    # A single-color PNG compresses extremely well — a 768x519 solid color is ~1-2KB.
    # Real UI content with text/buttons produces 10KB+.
    # Use a heuristic: if file is less than 5KB for a window-sized image, it's suspicious.
    if ($file.Length -lt 5120) {
        Write-TestFail $TestName "Suspiciously small (${sizeKB}KB) — may be single color"
        return
    }

    Write-TestPass $TestName "${sizeKB}KB"
}

# ============================================================================
# Build
# ============================================================================

Write-TestHeader "Phase 1: Build WinUI Sample App"

if (-not $SkipBuild) {
    Write-Host "Building samples\winui-app ($Platform)..."
    Push-Location $sampleDir
    try {
        dotnet build -c Debug -p:Platform=$Platform 2>&1 | ForEach-Object { Write-Host "  $_" }
        if ($LASTEXITCODE -ne 0) { throw "Build failed" }
        Write-TestPass "Build" "$Platform"
    } finally {
        Pop-Location
    }
} else {
    Write-Host "Skipping build (-SkipBuild)"
}

if (-not (Test-Path $buildOutput)) {
    throw "Build output not found at $buildOutput"
}

# ============================================================================
# Launch
# ============================================================================

Write-TestHeader "Phase 2: Launch App"

Write-Host "Using winapp: $WinAppPath"
Write-Host "Build output: $buildOutput"

# Trigger first-run notice (creates marker file) before any JSON-parsed command
Write-Host "Running --version warmup..."
$versionOut = & $WinAppPath --version 2>&1
Write-Host "  --version stdout: $versionOut"

$markerFile = Join-Path $env:USERPROFILE ".winapp\.first-run-complete"
Write-Host "  First-run marker exists: $(Test-Path $markerFile)"

Write-Host "Running --detach --json..."
Push-Location $sampleDir
$launchStdout = & $WinAppPath run $buildOutput --detach --json 2>"$ScreenshotDir\winapp-run-stderr.txt"
Pop-Location
$launchStderr = Get-Content "$ScreenshotDir\winapp-run-stderr.txt" -ErrorAction SilentlyContinue

Write-Host "  stdout lines: $($launchStdout.Count)"
for ($i = 0; $i -lt $launchStdout.Count; $i++) {
    Write-Host "  stdout[$i]: [$($launchStdout[$i])]"
}
if ($launchStderr) {
    Write-Host "  stderr lines: $($launchStderr.Count)"
    $launchStderr | Select-Object -First 5 | ForEach-Object { Write-Host "  stderr: [$_]" }
}

# Handle potential multi-line or multi-object output: take only the last JSON object
$jsonStr = ($launchStdout -join "`n")
if ($jsonStr -match '(?s).*(\{[^{}]*"ProcessId"[^{}]*\})') {
    $launchResult = $Matches[1] | ConvertFrom-Json
} else {
    throw "Failed to parse launch JSON. Raw output: $jsonStr"
}
$appPid = $launchResult.ProcessId

if (-not $appPid) {
    throw "Failed to launch app — no PID returned. Output: $launchJson"
}

Write-TestPass "winapp run --detach" "PID=$appPid, AUMID=$($launchResult.AUMID)"

# Wait for window
$timeout = 60; $elapsed = 0
while ($elapsed -lt $timeout) {
    $proc = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $proc) { throw "App process $appPid exited unexpectedly" }
    if ($proc.MainWindowHandle -ne 0) { break }
    Start-Sleep -Seconds 2; $elapsed += 2
}
if ($elapsed -ge $timeout) { throw "Window did not appear within ${timeout}s" }

Write-TestPass "Window ready" "after ${elapsed}s"

# Short pause for UI to settle
Start-Sleep -Seconds 2

# ============================================================================
# Test all winapp ui commands
# ============================================================================

Write-TestHeader "Phase 3: Test winapp ui commands"

$sw = [Diagnostics.Stopwatch]::StartNew()

# --- list-windows (JSON) ---
Assert-WinappJsonField "list-windows" -WinappArgs @("ui", "list-windows", "-a", "$appPid", "--json") -Field "0.title" -Expected "WinUI Sample"

# --- status (JSON) ---
Assert-WinappJsonField "status" -WinappArgs @("ui", "status", "-a", "$appPid", "--json") -Field "processName" -Expected "winui-app"

# --- inspect (JSON) ---
Assert-WinappJsonField "inspect --json" -WinappArgs @("ui", "inspect", "-a", "$appPid", "--json", "-d", "8") -Field "windows.0.elements.0.type" -Expected "Window"

# --- inspect interactive (exit code) ---
Assert-WinappSuccess "inspect --interactive" -WinappArgs @("ui", "inspect", "-a", "$appPid", "-i")

# --- search (JSON) ---
Assert-WinappJsonField "search Counter Button" -WinappArgs @("ui", "search", "Counter Button", "-a", "$appPid", "--json") -Field "matchCount" -Expected "1"
Assert-WinappJsonField "search SubmitButton" -WinappArgs @("ui", "search", "SubmitButton", "-a", "$appPid", "--json") -Field "matchCount" -Expected "1"

# --- screenshot ---
$ssInitial = Join-Path $ScreenshotDir "01-initial.png"
Assert-WinappSuccess "screenshot (initial)" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssInitial)

# --- invoke counter button x3 (exit code) ---
Assert-WinappSuccess "invoke Counter Button (1)" -WinappArgs @("ui", "invoke", "Counter Button", "-a", "$appPid")
Assert-WinappSuccess "invoke Counter Button (2)" -WinappArgs @("ui", "invoke", "Counter Button", "-a", "$appPid")
Assert-WinappSuccess "invoke Counter Button (3)" -WinappArgs @("ui", "invoke", "Counter Button", "-a", "$appPid")

# --- wait-for assertion: counter = Count: 3 ---
Assert-WinappSuccess "wait-for: counter = Count: 3" -WinappArgs @("ui", "wait-for", "CounterDisplay", "-a", "$appPid", "--property", "Name", "--value", "Count: 3", "-t", "5000")

# --- screenshot after counter ---
$ssCounter = Join-Path $ScreenshotDir "02-after-counter.png"
Assert-WinappSuccess "screenshot (after counter)" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssCounter)

# --- get-property (human-readable, check actual counter value) ---
Assert-WinappOutputContains "get-property CounterDisplay" -WinappArgs @("ui", "get-property", "CounterDisplay", "-a", "$appPid") -Expected "Count: 3"

# --- get-property (JSON) ---
Assert-WinappJsonField "get-property --json" -WinappArgs @("ui", "get-property", "CounterDisplay", "-a", "$appPid", "--json") -Field "properties.Name" -Expected "Count: 3"

# --- set-value (exit code) ---
Assert-WinappSuccess "set-value Text Input" -WinappArgs @("ui", "set-value", "Text Input", "Hello from e2e test!", "-a", "$appPid")

# --- wait-for text value ---
Assert-WinappSuccess "wait-for: text value set" -WinappArgs @("ui", "wait-for", "Text Input", "-a", "$appPid", "--property", "Value", "--value", "Hello from e2e test!", "-t", "5000")

# --- RichEditBox (TextPattern-only, no ValuePattern): read works, set falls back to
#     LegacyIAccessible. WinUI 3 RichEditBox returns E_NOTIMPL for put_accValue, so set-value
#     must fail *gracefully* (non-zero exit) with an actionable message that points at send-keys,
#     rather than crash or hang. This guards the fallback chain + error path for issue #620.
#     Coverage boundary: the *successful* put_accValue path can't be exercised from this WinUI 3
#     sample — no WinUI 3 control implements put_accValue without also exposing ValuePattern (which
#     set-value tries first). The fallback *ordering*, including the successful LegacyIAccessible
#     (put_accValue) branch, is now unit-tested in WinApp.Cli.Tests/ValueSetterTests.cs via the
#     IValueSetStrategy seam (issue #623); the live COM call against a real put_accValue-only control
#     is still validated manually (Win32 rich-edit, Electron/WebView2 compose boxes). ---
Assert-WinappOutputContains "get-value RichEditBox (TextPattern read path)" -WinappArgs @("ui", "get-value", "Rich Text Editor", "-a", "$appPid") -Expected "Rich text read path OK"
Assert-WinappFailureContains "set-value RichEditBox fails gracefully with send-keys hint" -WinappArgs @("ui", "set-value", "Rich Text Editor", "hello richedit", "-a", "$appPid") -Expected "send-keys"

# --- invoke checkbox toggle (exit code) ---
Assert-WinappSuccess "invoke FeatureToggle" -WinappArgs @("ui", "invoke", "Feature Toggle", "-a", "$appPid")

# --- screenshot after input ---
$ssInput = Join-Path $ScreenshotDir "03-after-input.png"
Assert-WinappSuccess "screenshot (after input)" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssInput)

# --- invoke submit (exit code) ---
Assert-WinappSuccess "invoke Submit Button" -WinappArgs @("ui", "invoke", "Submit Button", "-a", "$appPid")

# --- wait-for result text ---
Assert-WinappSuccess "wait-for: result shows submission" -WinappArgs @("ui", "wait-for", "ResultDisplay", "-a", "$appPid", "--property", "Name", "--value", "Submitted: Hello from e2e test! (Feature: On)", "-t", "5000")

# --- screenshot after submit ---
$ssSubmit = Join-Path $ScreenshotDir "04-after-submit.png"
Assert-WinappSuccess "screenshot (after submit)" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssSubmit)

# --- focus (exit code) ---
Assert-WinappSuccess "focus Text Input" -WinappArgs @("ui", "focus", "Text Input", "-a", "$appPid")

# --- get-focused (exit code — focus may not persist after click) ---
Assert-WinappSuccess "get-focused" -WinappArgs @("ui", "get-focused", "-a", "$appPid")

# --- click mouse fallback (exit code) ---
Assert-WinappSuccess "click Counter Button" -WinappArgs @("ui", "click", "Counter Button", "-a", "$appPid")

# --- wait-for counter = Count: 4 (click may use different coordinates on different screens) ---
Assert-WinappSuccess "wait-for: counter after click" -WinappArgs @("ui", "wait-for", "CounterDisplay", "-a", "$appPid", "-t", "3000")

# --- hover (exercises mouse hover + dwell) ---
Assert-WinappSuccess "hover Counter Button" -WinappArgs @("ui", "hover", "Counter Button", "-a", "$appPid", "--json", "--dwell-time", "200")

# --- hover + screenshot --capture-screen (tooltip workflow from docs) ---
$ssHover = Join-Path $ScreenshotDir "05-hover-screen.png"
Assert-WinappSuccess "screenshot after hover" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssHover, "--capture-screen")

# --- screenshot --capture-screen ---
$ssScreen = Join-Path $ScreenshotDir "06-capture-screen.png"
Assert-WinappSuccess "screenshot --capture-screen" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssScreen, "--capture-screen")

# --- screenshot --focus (exercises new WGC + foreground path) ---
$ssFocus = Join-Path $ScreenshotDir "07-focus.png"
Assert-WinappSuccess "screenshot --focus" -WinappArgs @("ui", "screenshot", "-a", "$appPid", "-o", $ssFocus, "--focus")

# --- send-keys: synthetic keyboard input into the focused Text Input (#562) ---
# Literal text with spaces exercises the whitespace-preserving parser path.
Assert-WinappSuccess "focus Text Input (for send-keys)" -WinappArgs @("ui", "focus", "Text Input", "-a", "$appPid")
Assert-WinappSuccess "send-keys: literal text" -WinappArgs @("ui", "send-keys", "Typed via send-keys", "-a", "$appPid", "--target", "Text Input", "--json")
Assert-WinappSuccess "send-keys: named key" -WinappArgs @("ui", "send-keys", "end", "-a", "$appPid", "--target", "Text Input")

# --- scroll --wheel against the main ScrollViewer (exercises mouse wheel path) ---
Assert-WinappSuccess "scroll --wheel MainScroller" -WinappArgs @("ui", "scroll", "MainScroller", "-a", "$appPid", "--wheel", "-1", "--json")

# --- wait-for: element exists ---
Assert-WinappSuccess "wait-for: element exists" -WinappArgs @("ui", "wait-for", "Submit Button", "-a", "$appPid", "-t", "3000")

# --- inspect --json (verify structure) ---
$jsonResult = Invoke-Winapp @("ui", "inspect", "-a", "$appPid", "--json", "-d", "5")
if ($jsonResult.ExitCode -eq 0) {
    try {
        $parsed = $jsonResult.Output | ConvertFrom-Json
        $totalElements = 0
        foreach ($w in $parsed.windows) { $totalElements += $w.elementCount }
        if ($parsed.windows.Count -gt 0 -and $totalElements -gt 0) {
            Write-TestPass "inspect --json structure" "$($parsed.windows.Count) window(s), $totalElements element(s)"
        } else {
            Write-TestFail "inspect --json structure" "No windows/elements in JSON"
        }
    } catch {
        Write-TestFail "inspect --json structure" "Invalid JSON: $_"
    }
} else {
    Write-TestFail "inspect --json structure" "Exit code $($jsonResult.ExitCode)"
}

$sw.Stop()

# ============================================================================
# Validate Screenshots
# ============================================================================

Write-TestHeader "Phase 4: Validate Screenshots"

Assert-ScreenshotValid "screenshot: 01-initial.png" $ssInitial
Assert-ScreenshotValid "screenshot: 02-after-counter.png" $ssCounter
Assert-ScreenshotValid "screenshot: 03-after-input.png" $ssInput
Assert-ScreenshotValid "screenshot: 04-after-submit.png" $ssSubmit
Assert-ScreenshotValid "screenshot: 05-hover-screen.png" $ssHover
Assert-ScreenshotValid "screenshot: 06-capture-screen.png" $ssScreen
Assert-ScreenshotValid "screenshot: 07-focus.png" $ssFocus

# ============================================================================
# DevTools: a write the control rejects
# ============================================================================

Write-TestHeader "Phase 5: DevTools write a control rejects"

# TitleBar.Content rejects a string but used to keep it, and the TitleBar then crashed the app at the next window
# activation. The write must fail, leave the value as it was, and the app must keep running.
$devLaunch = & $WinAppPath run $sampleDir --devtools on --detach --json 2>$null
$devPid = $null
try { $devPid = ($devLaunch | Out-String | ConvertFrom-Json).ProcessId } catch { }
if (-not $devPid) {
    Write-TestFail "devtools launch" "No process id: $($devLaunch | Out-String)"
} else {
    Write-TestPass "devtools launch" "PID $devPid"
    $null = Invoke-Winapp @("ui", "wait-for", "AppTitleBar", "-a", "$devPid", "-t", "20000")
    $rejected = Invoke-Winapp @("devtools", "set-property", "AppTitleBar", "Content", "x", "-a", "$devPid", "--json")
    if ($rejected.ExitCode -ne 0 -and $rejected.Output -match "TitleBar rejected") {
        Write-TestPass "set-property TitleBar.Content is refused"
    } else {
        Write-TestFail "set-property TitleBar.Content is refused" "Exit $($rejected.ExitCode): $($rejected.Output)"
    }
    $after = Invoke-Winapp @("devtools", "get-property", "AppTitleBar", "Content", "-a", "$devPid")
    if ($after.Output -notmatch "Content: x") { Write-TestPass "TitleBar.Content keeps its value" }
    else { Write-TestFail "TitleBar.Content keeps its value" $after.Output }

    # Opening the DevTools window and switching between it and the app changes window activation.
    $null = Invoke-Winapp @("devtools", "call", "Window.open", "-a", "$devPid")
    $null = Invoke-Winapp @("ui", "wait-for", "TreeScroll", "-a", "$devPid", "-t", "10000")
    $windows = (Invoke-Winapp @("ui", "list-windows", "-a", "$devPid", "--json")).Output | ConvertFrom-Json
    $inspectorHwnd = [string](@($windows | Where-Object title -like "WinApp DevTools*")[0].hwnd)
    $mainHwnd = [string](@($windows | Where-Object { $_.title -notlike "WinApp DevTools*" })[0].hwnd)
    foreach ($toggleId in @("PickElementToggle", "FocusTrackingToggle")) {
        Assert-WinappSuccess "$toggleId starts off" @("ui", "wait-for", $toggleId, "-w", $inspectorHwnd, "-p", "ToggleState", "--value", "Off", "-t", "3000") | Out-Null
        foreach ($state in @("On", "Off")) {
            Assert-WinappSuccess "$toggleId UI Automation activation ($state)" @("ui", "invoke", $toggleId, "-w", $inspectorHwnd) | Out-Null
            Assert-WinappSuccess "$toggleId exposes $state" @("ui", "wait-for", $toggleId, "-w", $inspectorHwnd, "-p", "ToggleState", "--value", $state, "-t", "3000") | Out-Null
        }
    }
    Assert-WinappSuccess "visual tree exposes Tree control type" @("ui", "wait-for", "WinAppDevToolsTree", "-w", $inspectorHwnd, "-p", "ControlType", "--value", "Tree", "-t", "3000") | Out-Null
    Assert-WinappSuccess "visual tree row exposes TreeItem control type" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "ControlType", "--value", "TreeItem", "-t", "3000") | Out-Null
    $parentTestDir = Join-Path ([IO.Path]::GetTempPath()) ("winapp-tree-uia-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $parentTestDir | Out-Null
    try {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        $component = if ($Platform -eq "ARM64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" } else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
        $vsPath = & $vswhere -latest -products * -requires $component -property installationPath
        if (-not $vsPath) { throw "MSVC with $component is required for the tree parent-navigation test." }
        $vcvarsName = if ($Platform -eq "ARM64") { "vcvarsamd64_arm64.bat" } else { "vcvars64.bat" }
        $vcvars = Join-Path $vsPath "VC\Auxiliary\Build\$vcvarsName"
        $parentTest = Join-Path $parentTestDir "tree-automation-ui-tests.exe"
        $parentSource = Join-Path $repoRoot "src\winapp-devtools\test\tree-automation-ui-tests.cpp"
        $compile = & $env:ComSpec /c "call `"$vcvars`" >nul && cl /nologo /std:c++20 /W3 /WX /EHsc /MT `"$parentSource`" /Fo:`"$parentTestDir\test.obj`" /Fe:`"$parentTest`" /link ole32.lib oleaut32.lib uiautomationcore.lib" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Tree parent-navigation test did not compile: $compile" }
        $parentResult = & $parentTest $inspectorHwnd 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Tree parent-navigation regression: $parentResult" }
        Write-TestPass "tree parents update after filtering without downward enumeration" "$parentResult"
    } catch {
        Write-TestFail "tree parents update after filtering without downward enumeration" "$_"
    } finally {
        Remove-Item -LiteralPath $parentTestDir -Recurse -Force
    }
    $treeResult = Invoke-Winapp @("ui", "inspect", "WinAppDevToolsTree", "-w", $inspectorHwnd, "-d", "2", "--json")
    try {
        $tree = ($treeResult.Output | ConvertFrom-Json).windows[0].elements[0]
        if ($treeResult.ExitCode -eq 0 -and $tree.children[0].automationId -eq "TreeNode0" -and
            $tree.children[0].children[0].automationId -eq "TreeNode1") {
            Write-TestPass "visual tree exposes nested TreeItem children"
        } else { Write-TestFail "visual tree exposes nested TreeItem children" $treeResult.Output }
    } catch { Write-TestFail "visual tree exposes nested TreeItem children" "$_" }
    foreach ($selectedId in @("TreeNode0", "TreeNode1")) {
        $otherId = if ($selectedId -eq "TreeNode0") { "TreeNode1" } else { "TreeNode0" }
        Assert-WinappSuccess "$selectedId supports SelectionItem.Select" @("ui", "invoke", $selectedId, "-w", $inspectorHwnd, "--action", "select") | Out-Null
        Assert-WinappSuccess "$selectedId reports selected" @("ui", "wait-for", $selectedId, "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "True", "-t", "3000") | Out-Null
        Assert-WinappSuccess "$otherId reports unselected" @("ui", "wait-for", $otherId, "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "False", "-t", "3000") | Out-Null
        Assert-WinappSuccess "reopen inspector with $selectedId selected" @("devtools", "call", "Window.open", "-a", "$devPid") | Out-Null
        Assert-WinappSuccess "$selectedId stays selected after reopening" @("ui", "wait-for", $selectedId, "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "True", "-t", "3000") | Out-Null
        Assert-WinappSuccess "hide inspector with $selectedId selected" @("devtools", "call", "Window.close", "-a", "$devPid") | Out-Null
        Assert-WinappSuccess "reopen hidden inspector with $selectedId selected" @("devtools", "call", "Window.open", "-a", "$devPid") | Out-Null
        Assert-WinappSuccess "$selectedId stays selected after hiding and reopening" @("ui", "wait-for", $selectedId, "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "True", "-t", "3000") | Out-Null
    }
    Assert-WinappSuccess "focus can move to an unselected tree row" @("ui", "focus", "TreeNode0", "-w", $inspectorHwnd) | Out-Null
    Assert-WinappSuccess "focused tree row has keyboard focus" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "HasKeyboardFocus", "--value", "True", "-t", "3000") | Out-Null
    Assert-WinappSuccess "keyboard focus does not select the tree row" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "False", "-t", "3000") | Out-Null
    Assert-WinappSuccess "selection stays on the other tree row" @("ui", "wait-for", "TreeNode1", "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "True", "-t", "3000") | Out-Null
    Assert-WinappSuccess "tree Selection.GetSelection returns the selected row" @("ui", "wait-for", "WinAppDevToolsTree", "-w", $inspectorHwnd, "--value", "TitleBar #AppTitleBar", "-t", "3000") | Out-Null
    foreach ($action in @("collapse", "expand")) {
        $state = if ($action -eq "collapse") { "Collapsed" } else { "Expanded" }
        Assert-WinappSuccess "tree row supports ExpandCollapse.$action" @("ui", "invoke", "TreeNode0", "-w", $inspectorHwnd, "--action", $action) | Out-Null
        Assert-WinappSuccess "tree row reports $state" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "ExpandCollapseState", "--value", $state, "-t", "3000") | Out-Null
        $childArgs = @("ui", "wait-for", "TreeNode1", "-w", $inspectorHwnd, "-t", "3000")
        if ($action -eq "collapse") { $childArgs += "--gone" }
        Assert-WinappSuccess "tree child visibility follows $action" $childArgs | Out-Null
    }
    Assert-WinappSuccess "filter the accessible tree" @("ui", "set-value", "TreeFilter", "AppTitleBar", "-w", $inspectorHwnd) | Out-Null
    $filteredResult = Invoke-Winapp @("ui", "inspect", "WinAppDevToolsTree", "-w", $inspectorHwnd, "-d", "2", "--json")
    try {
        $filteredTree = ($filteredResult.Output | ConvertFrom-Json).windows[0].elements[0]
        if ($filteredResult.ExitCode -eq 0 -and @($filteredTree.children).Count -eq 1 -and
            $filteredTree.children[0].automationId -eq "TreeNode1") {
            Write-TestPass "filter promotes the remaining row to an accessible root"
        } else { Write-TestFail "filter promotes the remaining row to an accessible root" $filteredResult.Output }
    } catch { Write-TestFail "filter promotes the remaining row to an accessible root" "$_" }
    Assert-WinappSuccess "clear the accessible tree filter" @("ui", "set-value", "TreeFilter", "", "-w", $inspectorHwnd) | Out-Null
    Assert-WinappSuccess "clearing filter restores the tree root" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-t", "3000") | Out-Null
    Assert-WinappSuccess "filter tree before requesting expansion" @("ui", "set-value", "TreeFilter", "Grid", "-w", $inspectorHwnd) | Out-Null
    $filteredExpand = Invoke-Winapp @("ui", "invoke", "TreeNode0", "-w", $inspectorHwnd, "--action", "expand")
    if ($filteredExpand.ExitCode -ne 0) { Write-TestPass "filtered tree refuses expansion" }
    else { Write-TestFail "filtered tree refuses expansion" $filteredExpand.Output }
    Assert-WinappSuccess "expansion leaves filter text intact" @("ui", "wait-for", "TreeFilter", "-w", $inspectorHwnd, "--value", "Grid", "-t", "3000") | Out-Null
    Assert-WinappSuccess "expansion does not reveal a nonmatching title bar" @("ui", "wait-for", "TreeNode1", "-w", $inspectorHwnd, "--gone", "-t", "3000") | Out-Null
    Assert-WinappSuccess "expansion does not reveal a nonmatching scroller" @("ui", "wait-for", "TreeNode2", "-w", $inspectorHwnd, "--gone", "-t", "3000") | Out-Null
    Assert-WinappSuccess "clear filter after refused expansion" @("ui", "set-value", "TreeFilter", "", "-w", $inspectorHwnd) | Out-Null
    Assert-WinappSuccess "clearing filter restores descendants before expansion" @("ui", "wait-for", "TreeNode1", "-w", $inspectorHwnd, "-t", "3000") | Out-Null
    Assert-WinappSuccess "expansion works after clearing filter" @("ui", "invoke", "TreeNode0", "-w", $inspectorHwnd, "--action", "expand") | Out-Null
    Assert-WinappSuccess "filter tree before requesting collapse" @("ui", "set-value", "TreeFilter", "StackPanel", "-w", $inspectorHwnd) | Out-Null
    $filteredCollapse = Invoke-Winapp @("ui", "invoke", "TreeNode3", "-w", $inspectorHwnd, "--action", "collapse")
    if ($filteredCollapse.ExitCode -ne 0) { Write-TestPass "filtered tree refuses collapse" }
    else { Write-TestFail "filtered tree refuses collapse" $filteredCollapse.Output }
    Assert-WinappSuccess "collapse leaves matching descendants visible" @("ui", "wait-for", "TreeNode6", "-w", $inspectorHwnd, "-t", "3000") | Out-Null
    Assert-WinappSuccess "collapse does not reveal a nonmatching button" @("ui", "wait-for", "TreeNode7", "-w", $inspectorHwnd, "--gone", "-t", "3000") | Out-Null
    Assert-WinappSuccess "clear filter before keyboard navigation" @("ui", "set-value", "TreeFilter", "", "-w", $inspectorHwnd) | Out-Null
    Assert-WinappSuccess "Left arrow collapses the focused tree row" @("ui", "send-keys", "left", "--target", "TreeNode0", "-w", $inspectorHwnd, "--via", "send-input") | Out-Null
    Assert-WinappSuccess "keyboard collapse updates the accessible state" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "ExpandCollapseState", "--value", "Collapsed", "-t", "3000") | Out-Null
    Assert-WinappSuccess "Right arrow expands the focused tree row" @("ui", "send-keys", "right", "--target", "TreeNode0", "-w", $inspectorHwnd, "--via", "send-input") | Out-Null
    Assert-WinappSuccess "keyboard expansion updates the accessible state" @("ui", "wait-for", "TreeNode0", "-w", $inspectorHwnd, "-p", "ExpandCollapseState", "--value", "Expanded", "-t", "3000") | Out-Null
    Assert-WinappSuccess "Tab from a leaf skips the next row's expand arrow" @("ui", "send-keys", "tab", "--target", "TreeNode1", "-w", $inspectorHwnd, "--via", "send-input") | Out-Null
    Assert-WinappSuccess "Tab lands on the next tree row" @("ui", "wait-for", "TreeNode2", "-w", $inspectorHwnd, "-p", "HasKeyboardFocus", "--value", "True", "-t", "3000") | Out-Null
    Assert-WinappSuccess "Tab does not change tree selection" @("ui", "wait-for", "TreeNode1", "-w", $inspectorHwnd, "-p", "IsSelected", "--value", "True", "-t", "3000") | Out-Null
    Assert-WinappSuccess "inspector keyboard focus and selection screenshot" @("ui", "screenshot", "-w", $inspectorHwnd, "-o", (Join-Path $ScreenshotDir "08-inspector-tree.png")) | Out-Null
    foreach ($textId in @("PageTitle", "PageSubtitle")) {
        Assert-WinappSuccess "$textId gets the duplicate-name fixture text" @("devtools", "set-property", $textId, "Text", "Shared label", "-a", "$devPid") | Out-Null
    }
    Assert-WinappSuccess "refresh inspector after changing sibling labels" @("ui", "send-keys", "f5", "--target", "TreeNode0", "-w", $inspectorHwnd, "--via", "send-input") | Out-Null
    foreach ($row in @(@("TreeNode4", "1"), @("TreeNode5", "2"))) {
        Assert-WinappSuccess "$($row[0]) has a distinct sibling name" @("ui", "wait-for", $row[0], "-w", $inspectorHwnd, "-p", "Name", "--value", "TextBlock Shared label ($($row[1]) of 2)", "-t", "3000") | Out-Null
    }
    Assert-WinappSuccess "filter matching duplicate sibling labels" @("ui", "set-value", "TreeFilter", "Shared label", "-w", $inspectorHwnd) | Out-Null
    foreach ($row in @(@("TreeNode4", "1"), @("TreeNode5", "2"))) {
        Assert-WinappSuccess "$($row[0]) keeps a distinct name when promoted to the tree root" @("ui", "wait-for", $row[0], "-w", $inspectorHwnd, "-p", "Name", "--value", "TextBlock Shared label ($($row[1]) of 2)", "-t", "3000") | Out-Null
    }
    Assert-WinappSuccess "filtered inspector screenshot" @("ui", "screenshot", "-w", $inspectorHwnd, "-o", (Join-Path $ScreenshotDir "09-inspector-filter.png")) | Out-Null
    Assert-WinappSuccess "clear duplicate-name fixture filter" @("ui", "set-value", "TreeFilter", "", "-w", $inspectorHwnd) | Out-Null
    try {
        $titleBar = (Invoke-Winapp @("devtools", "search", "AppTitleBar", "-a", "$devPid", "--json")).Output | ConvertFrom-Json
        Assert-WinappSuccess "select TitleBar properties" @(
            "devtools", "call", "Selection.select", "handle=$($titleBar.matches[0].handle)", "-a", "$devPid") | Out-Null
        foreach ($value in @("Auto", "120", "Auto")) {
            Assert-WinappSuccess "set TitleBar.Height to $value" @(
                "devtools", "set-property", "AppTitleBar", "Height", $value, "-a", "$devPid") | Out-Null
            Assert-WinappSuccess "Height header announces its current value $value" @(
                "ui", "wait-for", "Height value", "-w", $inspectorHwnd,
                "-p", "Name", "--value", "Height, $value", "-t", "5000") | Out-Null
        }
    } catch {
        Write-TestFail "property accessible names include the current value" $_.Exception.Message
    }
    for ($i = 0; $i -lt 10; $i++) {
        $null = Invoke-Winapp @("ui", "focus", "-w", $inspectorHwnd)
        $null = Invoke-Winapp @("ui", "focus", "-w", $mainHwnd)
    }
    if (Get-Process -Id $devPid -ErrorAction SilentlyContinue) { Write-TestPass "app survives window activation after the refused write" }
    else { Write-TestFail "app survives window activation after the refused write" "Process $devPid exited" }

    Assert-WinappSuccess "set-property Button.Content still accepts text" @("devtools", "set-property", "CounterButton", "Content", "Hello", "-a", "$devPid") | Out-Null
    Stop-Process -Id $devPid -ErrorAction SilentlyContinue
}

# ============================================================================
# Summary
# ============================================================================

Write-TestHeader "Results"

$total = $script:testsPassed + $script:testsFailed
Write-Host "  Total:  $total tests"
Write-Host "  Passed: $($script:testsPassed)" -ForegroundColor Green
if ($script:testsFailed -gt 0) {
    Write-Host "  Failed: $($script:testsFailed)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Failed tests:" -ForegroundColor Red
    $script:testResults | Where-Object { $_.Status -eq "FAIL" } | ForEach-Object {
        Write-Host "    - $($_.Name): $($_.Detail)" -ForegroundColor Red
    }
}
Write-Host "  Time:   $([Math]::Round($sw.Elapsed.TotalSeconds, 1))s"
Write-Host "  Screenshots: $ScreenshotDir"
Write-Host ""

# Exit with failure if any tests failed
if ($script:testsFailed -gt 0) {
    exit 1
}
