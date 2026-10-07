# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    Isolate where long-session DevTools cost comes from, using a minimal XAML diagnostics TAP.
.DESCRIPTION
    Launches the devtools-perf bench app once per configuration and runs rounds of element churn
    (collapsed, so nothing renders) and heavy-page navigation, recording per round: churn time,
    settled private-bytes floor and navigation p50. Configurations:

      off           no diagnostics
      init          probe TAP loaded, never subscribes (InitializeXamlDiagnosticsEx only)
      advise        probe TAP subscribed to visual-tree changes for the whole run (no-op callback)
      advise-cb1    as advise, but exposes only IVisualTreeServiceCallback (no element-state callback)
      advise-src    as advise, with ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1 (what --devtools sets)
      cycle         as advise-src, unsubscribing and resubscribing after every round
      unadvise      as advise-src, unsubscribing for good after half the rounds
      late          as advise-src, but subscribing only after half the rounds
      release       as advise-src, then after half the rounds unsubscribing and releasing every reference
                    the TAP holds to XAML diagnostics; three rounds later the TAP is injected again
      engine        winapp run --devtools --no-overlay (the real engine), for reference
      src-only      no TAP, only ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1 (the source-info flag Visual Studio sets)
      external      the harness does not launch the app: it runs -LaunchCommand (or waits up to 10 minutes for
                    you to start it, for example with F5 in Visual Studio) and measures whatever started it

    The probe is built from devtools-perf\probe-tap with MSVC and loaded with winapp's injector. Writes retention.json and
    retention.md to -OutDir. -Page picks the navigation target: heavy (120 cards with templated controls,
    styles and theme resources), or 240 rows of one kind of content: plain (panels and text), resource
    (plain plus theme-resource references), styled (text blocks with an explicit Style), button or checkbox. Windows show without activation; no input is sent.
.EXAMPLE
    .\scripts\devtools-perf\retention.ps1 -Rounds 12
#>
[CmdletBinding()]
param(
    [string]$Winapp,
    [ValidateSet('off', 'init', 'advise', 'advise-cb1', 'advise-src', 'cycle', 'unadvise', 'late', 'release', 'engine', 'src-only', 'external')]
    [string[]]$Configs = @('off', 'init', 'advise', 'advise-cb1', 'advise-src', 'cycle', 'unadvise', 'late', 'engine'),
    [int]$Rounds = 12,
    [int]$NavCycles = 5,
    [ValidateSet('heavy', 'plain', 'resource', 'styled', 'button', 'checkbox')][string]$Page = 'heavy',
    [int]$ChurnElements = 2000,
    [int]$ChurnCycles = 10,
    [string]$LaunchCommand,
    [switch]$SkipPrepare,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\devtools-perf\retention')
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Winapp) { $Winapp = Join-Path $repoRoot 'artifacts\cli\win-x64\winapp.exe' }
$Winapp = (Resolve-Path $Winapp).Path
$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$work = Join-Path ([IO.Path]::GetTempPath()) 'winapp-devtools-perf'
$stage = Join-Path $work 'bench-app'
$control = Join-Path $work 'bench-control'
$bin = Join-Path $work 'probe-bin'

function Write-Step([string]$Message) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $Message" }

# Build the probe TAP. It is loaded with winapp's own injector (the internal __devtools-inject verb), so it answers to the DevTools CLSID.
New-Item -ItemType Directory -Path $bin -Force | Out-Null
$src = Join-Path $PSScriptRoot 'probe-tap'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = Join-Path (& $vswhere -latest -products * -property installationPath) 'VC\Auxiliary\Build\vcvars64.bat'
& $env:ComSpec /c "call `"$vcvars`" >nul && cd /d `"$bin`" && cl /nologo /std:c++20 /O2 /MT /EHsc /W3 /WX /DUNICODE /D_UNICODE /LD `"$src\ProbeTap.cpp`" /Fe:ProbeTap.dll /link ole32.lib oleaut32.lib /DEF:`"$src\ProbeTap.def`" >nul"
if ($LASTEXITCODE) { throw 'Probe build failed.' }

if (-not $SkipPrepare) {
    Write-Step 'Preparing bench app'
    & (Join-Path $PSScriptRoot '..\devtools-perf.ps1') -Winapp $Winapp -Apps bench -PrepareOnly | Out-Null
}

$script:benchId = 0
function Wait-File([string]$Path, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path $Path)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $Path" }
        if ($script:appPid -and -not (Get-Process -Id $script:appPid -ErrorAction SilentlyContinue)) { throw "The bench app (PID $script:appPid) exited while waiting for $Path." }
        Start-Sleep -Milliseconds 50
    }
    # The app renames each result into place; a read can still race the rename, so retry briefly.
    for ($i = 0; ; $i++) {
        try { return Get-Content -Raw $Path -ErrorAction Stop | ConvertFrom-Json }
        catch [IO.IOException] { if ($i -ge 20) { throw }; Start-Sleep -Milliseconds 50 }
    }
}
function Invoke-Bench([hashtable]$Command) {
    $script:benchId++
    $Command.id = $script:benchId
    $path = Join-Path $control 'cmd.json'
    Set-Content "$path.tmp" ($Command | ConvertTo-Json -Compress)
    Move-Item "$path.tmp" $path -Force
    $r = Wait-File (Join-Path $control "res-$($script:benchId).json") 600
    if ($r.error) { throw "Bench $($Command.name): $($r.error)" }
    $r
}
function Invoke-Probe([int]$ProcessId, [string]$Command) {
    $status = Join-Path $control "probe-$ProcessId.json"
    $seq = (Get-Content -Raw $status | ConvertFrom-Json).seq
    Set-Content (Join-Path $control "probe-$ProcessId.cmd") $Command -NoNewline
    $deadline = (Get-Date).AddMinutes(2)
    while ($true) {
        Start-Sleep -Milliseconds 50
        try { $s = Get-Content -Raw $status | ConvertFrom-Json; if ($s.seq -gt $seq) { return $s } } catch { }
        if ((Get-Date) -gt $deadline) { throw "Probe did not answer '$Command'." }
    }
}

function Start-Bench([string]$Config) {
    $script:appPid = $null
    Get-ChildItem $control -File | Remove-Item -Force
    $probe = $Config -notin 'off', 'engine', 'src-only', 'external'
    $sourceInfo = $Config -in 'advise-src', 'cycle', 'unadvise', 'late', 'release', 'src-only'
    $arguments = if ($Config -eq 'engine') { @('run', $stage, '--no-build', '--devtools', '--no-overlay') } else { @('run', $stage, '--no-build', '--with-alias') }
    $runner = $null
    if ($Config -eq 'external') {
        if ($LaunchCommand) { Invoke-Expression $LaunchCommand } else { Write-Step 'Start the bench app now (for example F5 in Visual Studio); waiting up to 10 minutes' }
    }
    else {
    if ($sourceInfo) { $env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO = '1' }
    try {
        $runner = Start-Process -FilePath $Winapp -ArgumentList $arguments -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $work 'retention-run.out') -RedirectStandardError (Join-Path $work 'retention-run.err')
    }
    finally { Remove-Item env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO -ErrorAction SilentlyContinue }
    }
    $startup = Wait-File (Join-Path $control 'startup.json') $(if ($Config -eq 'external') { 600 } else { 120 })
    $appPid = [int]$startup.pid
    $script:appPid = $appPid
    Start-Sleep -Seconds 3
    # Evidence of what is attached: the app's own view of the source-info flag and debugger, and any
    # XAML diagnostics modules loaded in it (DevTools, the probe, or Visual Studio's XamlDiagnostics TAP).
    $modules = @((Get-Process -Id $appPid).Modules | Where-Object { $_.ModuleName -match '^(WinApp\.DevTools|ProbeTap)|Tap\.dll$' -or $_.FileName -match 'XamlDiagnostics|Visual Studio' } | ForEach-Object { $_.FileName })
    $launch = [ordered]@{ pid = $appPid; runner = if ($runner) { $runner.Id } else { $null }; probe = $null
        sourceInfoEnv = $startup.sourceInfoEnv; debuggerAttached = $startup.debuggerAttached; diagnosticsModules = $modules }
    if ($probe) {
        $advise = if ($Config -in 'init', 'late') { 0 } else { 1 }
        $cb2 = if ($Config -eq 'advise-cb1') { 0 } else { 1 }
        $launch.probe = Add-Probe $appPid "dir=$control;advise=$advise;cb2=$cb2"
    }
    $launch
}

function Add-Probe([int]$ProcessId, [string]$InitializationData) {
    $udk = (Get-Process -Id $ProcessId).Modules | Where-Object ModuleName -eq 'Microsoft.Internal.FrameworkUdk.dll' | Select-Object -First 1 -ExpandProperty FileName
    if (-not $udk) { throw 'The bench app has not loaded Microsoft.Internal.FrameworkUdk.dll.' }
    $status = Join-Path $control "probe-$ProcessId.json"
    Remove-Item $status -ErrorAction SilentlyContinue
    $hr = & $Winapp __devtools-inject $ProcessId (Join-Path $bin 'ProbeTap.dll') $udk $InitializationData
    if ([int]$hr -ne 0) { throw "InitializeXamlDiagnosticsEx failed: $hr" }
    Wait-File $status 60
}

function Stop-Bench($Launch) {
    try { Invoke-Bench @{ name = 'exit' } | Out-Null } catch { }
    foreach ($id in @($Launch.pid, $Launch.runner) | Where-Object { $_ }) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p -and -not $p.WaitForExit(15000)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 2
}

$results = [ordered]@{
    commit = (git -C $repoRoot rev-parse HEAD).Trim(); startedUtc = [DateTime]::UtcNow.ToString('o')
    rounds = $Rounds; navCycles = $NavCycles; page = $Page; churnElements = $ChurnElements; churnCycles = $ChurnCycles; configs = [ordered]@{}
}
$half = [int][Math]::Floor($Rounds / 2)
foreach ($config in $Configs) {
    Write-Step "$config"
    $launch = Start-Bench $config
    $rows = @()
    $events = @()
    try {
        $null = Invoke-Bench @{ name = 'nav'; cycles = 2; warmup = 0; page = $Page }
        for ($round = 1; $round -le $Rounds; $round++) {
            $churn = if ($ChurnElements -gt 0) {
                Invoke-Bench @{ name = 'churn'; count = $ChurnElements; cycles = $ChurnCycles; rounds = 1; visible = $false }
            } else { $null }
            $nav = Invoke-Bench @{ name = 'nav'; cycles = $NavCycles; warmup = 0; page = $Page }
            $pages = Invoke-Bench @{ name = 'pages' }
            $row = [ordered]@{
                round = $round; churnMs = $churn.ms
                floorMB = if ($churn) { @($churn.floorsPrivateMB)[-1] } else { (Invoke-Bench @{ name = 'mem' }).privateMB }
                navP50Ms = [Math]::Round($nav.navMs.p50, 1); navUiLagP95Ms = [Math]::Round($nav.frames.uiLagMs.p95, 1)
                heavyPagesAlive = $pages.heavyPagesAlive; heavyPagesCreated = $pages.heavyPagesCreated
            }
            $action = $null
            if ($config -eq 'cycle') { $action = 'cycle' }
            elseif ($config -eq 'unadvise' -and $round -eq $half) { $action = 'unadvise' }
            elseif ($config -eq 'late' -and $round -eq $half) { $action = 'advise' }
            elseif ($config -eq 'release' -and $round -eq $half) { $action = 'release' }
            elseif ($config -eq 'release' -and $round -eq $half + 3) { $action = 'reinject' }
            if ($action) {
                $before = (Invoke-Bench @{ name = 'mem' }).privateMB
                $s = if ($action -eq 'reinject') { Add-Probe $launch.pid "dir=$control;advise=1;cb2=1" } else { Invoke-Probe $launch.pid $action }
                Start-Sleep -Seconds 1
                $after = (Invoke-Bench @{ name = 'mem' }).privateMB
                $events += [ordered]@{ round = $round; action = $action; adviseMs = $s.lastAdviseMs; adviseAdds = $s.lastAdviseAdds
                    unadviseMs = $s.lastUnadviseMs; privateBeforeMB = $before; privateAfterMB = $after; hr = $s.hr }
            }
            $rows += $row
            Write-Verbose ($row | ConvertTo-Json -Compress)
        }
        $status = if ($launch.probe) { Invoke-Probe $launch.pid 'status' } else { $null }
    }
    catch {
        Write-Step "$config failed: $_"
        $events += [ordered]@{ round = $round; action = 'error'; message = "$_" }
        $status = $null
    }
    finally { Stop-Bench $launch }
    $results.configs[$config] = [ordered]@{ launch = $launch; rounds = $rows; events = $events; probe = $status }
    Set-Content (Join-Path $OutDir 'retention.json') ($results | ConvertTo-Json -Depth 10)
}

# Summary: first vs last round, plus the slope of the private-bytes floor per created element.
$created = 2.0 * $ChurnElements * $ChurnCycles
$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine("# Long-session retention experiments")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Commit ``$($results.commit.Substring(0, 12))``. $Rounds rounds; each round creates and removes $created collapsed elements (none if -ChurnElements 0), then navigates $NavCycles times to the '$Page' page (see -Page). Floor = settled private bytes after the churn. Floor growth is per round from round 2 on (round 1 includes warm-up).")
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Config | Churn ms, round 1 → last | Nav p50 ms, round 1 → last | Floor MB, round 1 → last | Floor growth / round, MB | Heavy pages alive / created | Events |')
[void]$sb.AppendLine('|---|---|---|---|---:|---|---|')
foreach ($config in $results.configs.Keys) {
    $r = @($results.configs[$config].rounds)
    if ($r.Count -lt 2) { continue }
    # Slope from round 2: round 1 includes one-time warm-up allocations.
    $slope = ($r[-1].floorMB - $r[1].floorMB) / [Math]::Max(1, $r.Count - 2)
    $ev = (@($results.configs[$config].events) | Select-Object -First 3 | ForEach-Object {
        "r$($_.round) $($_.action): advise $($_.adviseMs) ms / $($_.adviseAdds) adds, private $($_.privateBeforeMB)→$($_.privateAfterMB) MB" }) -join '; '
    if (@($results.configs[$config].events).Count -gt 3) { $ev += '; …' }
    [void]$sb.AppendLine("| $config | $($r[0].churnMs) → $($r[-1].churnMs) | $($r[0].navP50Ms) → $($r[-1].navP50Ms) | $($r[0].floorMB) → $($r[-1].floorMB) | $([Math]::Round($slope, 2)) | $($r[-1].heavyPagesAlive) / $($r[-1].heavyPagesCreated) | $ev |")
}
Set-Content (Join-Path $OutDir 'retention.md') $sb.ToString()
Get-Content (Join-Path $OutDir 'retention.md')
