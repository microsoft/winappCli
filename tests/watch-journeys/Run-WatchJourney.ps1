# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Runs 'winapp run --watch' journeys and scores every save against its expected outcome.
.DESCRIPTION
  For each journey: copies its app to a fresh folder, starts 'winapp run <app> --watch', makes each step's edits as one
  save, reads what the save did from the watch output (applied, unchanged, waiting, error, restart, ...), runs the step's
  UI checks against the live app and, at steps marked "parity", compares the app with a clean build of the same source
  (ParityGate.psm1). Every step is scored against its v1 expectation and today's recorded outcome (see README.md), and
  the run writes scoreboard.md.
.EXAMPLE
  .\Run-WatchJourney.ps1 -Journey e2-libs,plain-rude2 -DotnetRoot C:\dotnet11 -DesktopLock C:\hr-shared\desktop.lock
#>
[CmdletBinding()]
param(
    # Journey names (files in journeys\); default: all of them.
    [string[]]$Journey,
    [string]$Winapp = (Join-Path $PSScriptRoot '..\..\artifacts\cli\win-x64\winapp.exe'),
    # A .NET install to run with instead of the one on PATH (for example a side-by-side SDK 11 in C:\dotnet11).
    [string]$DotnetRoot,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-watch-journeys'),
    # A lock file shared by sessions that drive UI on this desktop; each journey runs while holding it.
    [string]$DesktopLock,
    [int]$SaveTimeoutSeconds = 90,
    [switch]$NoParity
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WatchJourney.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParityGate.psm1') -Force
$Winapp = (Resolve-Path $Winapp).Path
if ($DotnetRoot) {
    $env:PATH = "$DotnetRoot;$env:PATH"; $env:DOTNET_ROOT = $DotnetRoot; $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
# dotnet watch's messages are read in English.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# Outside this repository, whose global.json pins its own SDK.
Push-Location ([IO.Path]::GetTempPath())
$sdk = @(& dotnet --version | Where-Object { $_ -match '^\d+\.\d+\.\d+' })[0]
Pop-Location
if (-not $Journey) { $Journey = @(Get-ChildItem (Join-Path $PSScriptRoot 'journeys') -Filter *.json | ForEach-Object BaseName) }
$runRoot = Join-Path $OutputRoot "$(Get-Date -Format 'yyyyMMdd-HHmmss')-sdk$sdk"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Write-Host "SDK $sdk, winapp $Winapp, results in $runRoot"

function Invoke-Ui([string[]]$Arguments) {
    $output = & $Winapp ui @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = (Remove-Ansi $output).Trim() }
}

function Test-UiCheck($Check, [int]$ProcessId) {
    $target = [string]$Check.target; $value = [string]$Check.value; $app = @('-a', "$ProcessId")
    switch ([string]$Check.assert) {
        'invoke' { return Invoke-Ui (@('invoke', $target) + $app) }
        'set-value' { return Invoke-Ui (@('set-value', $target, $value) + $app) }
        'exists' { return Invoke-Ui (@('wait-for', $target) + $app + @('-t', '5000')) }
        'absent' { return Invoke-Ui (@('wait-for', $target, '--gone') + $app + @('-t', '5000')) }
        'text-equals' { return Invoke-Ui (@('wait-for', $target, '--value', $value) + $app + @('-t', '5000')) }
        'text-contains' { return Invoke-Ui (@('wait-for', $target, '--value', $value, '--contains') + $app + @('-t', '5000')) }
        'property-equals' {
            # "Property=Value", read from the XAML tree (properties UI Automation doesn't expose).
            $property, $expected = $value -split '=', 2
            $output = Remove-Ansi (& $Winapp devtools get-property $target $property @app 2>&1 | Out-String)
            $ok = $output -match "(?m)^\s*$([regex]::Escape($property)):\s*(.*?)\s*(\[\w+\])?\s*$" -and $Matches[1] -eq $expected
            return [pscustomobject]@{ Ok = $ok; Output = $output.Trim() }
        }
        default { throw "Unknown check '$($Check.assert)'." }
    }
}

# A check that the save's change is on screen, retried until it passes or the time is up.
function Wait-UiCheck($Check, [int]$ProcessId, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $result = Test-UiCheck $Check $ProcessId
        if ($result.Ok -or $Check.assert -in 'invoke', 'set-value') { return $result }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $result
}

function Use-DesktopLock([string]$Owner, [scriptblock]$Script) {
    if (-not $DesktopLock) { return & $Script }
    $said = $false
    while ($true) {
        if (Test-Path $DesktopLock) {
            $age = (Get-Date) - (Get-Item $DesktopLock).LastWriteTime
            if ($age.TotalMinutes -lt 45) {
                if (-not $said) { Write-Host "[lock] held by $((Get-Content $DesktopLock -Raw).Trim()); waiting"; $said = $true }
                Start-Sleep -Seconds 2; continue
            }
            Write-Host "[lock] stale ($([int]$age.TotalMinutes) min); taking it"
            Remove-Item $DesktopLock -Force -ErrorAction SilentlyContinue
        }
        try { $stream = [IO.File]::Open($DesktopLock, 'CreateNew'); $bytes = [Text.Encoding]::UTF8.GetBytes("$Owner $(Get-Date -Format s)"); $stream.Write($bytes, 0, $bytes.Length); $stream.Dispose(); break }
        catch [IO.IOException] { Start-Sleep -Milliseconds 500 }
    }
    try { & $Script } finally { Remove-Item $DesktopLock -Force -ErrorAction SilentlyContinue }
}

function Invoke-Journey([string]$Name) {
    $journey = Get-Content (Join-Path $PSScriptRoot "journeys\$Name.json") -Raw | ConvertFrom-Json
    $dir = Join-Path $runRoot $Name
    $src = Join-Path $dir 'src'
    $null = robocopy (Join-Path $PSScriptRoot "apps\$($journey.app)") $src /E /XD bin obj /NFL /NDL /NJH /NJS /NP
    $global:LASTEXITCODE = 0
    $projectDir = Join-Path $src $journey.project
    if ($journey.app -eq 'winui') {
        Copy-Item (Join-Path $PSScriptRoot '..\..\samples\winui-app\Assets') (Join-Path $projectDir 'Assets') -Recurse
        if (-not $journey.packaged) { Set-Content (Join-Path $src 'Directory.Build.props') '<Project><PropertyGroup><WindowsPackageType>None</WindowsPackageType></PropertyGroup></Project>' }
    }
    foreach ($edit in @($journey.setup | Where-Object { $_ })) { Invoke-JourneyEdit -Root $src -Step $edit }
    $log = Join-Path $dir 'watch.log'
    $arguments = @('run', "`"$projectDir`"", '--watch') + $(if ($journey.packaged) { @('--unique-identity') } else { @() }) + @($journey.runArguments | Where-Object { $_ })
    # One file for winapp's and dotnet watch's output, in the order they wrote it.
    $process = Start-Process cmd.exe -ArgumentList '/d', '/c', "`"`"$Winapp`" $($arguments -join ' ') > `"$log`" 2>&1`"" -WorkingDirectory $projectDir -PassThru -WindowStyle Hidden
    $script:consumed = 0
    function Read-NewLines {
        if (-not (Test-Path $log)) { return @() }
        $all = @(Get-Content $log -Encoding utf8)
        $new = @($all | Select-Object -Skip $script:consumed)
        $script:consumed = $all.Count
        return $new
    }
    function Get-AppPid { (Get-Process $journey.process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($src, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object StartTime -Descending | Select-Object -First 1).Id }
    function Invoke-Before([int]$ProcessId) { foreach ($check in @($journey.before | Where-Object { $_ })) { $null = Wait-UiCheck $check $ProcessId } }

    $rows = [System.Collections.Generic.List[object]]::new()
    try {
        $deadline = (Get-Date).AddMinutes(10)
        $seen = [System.Collections.Generic.List[string]]::new()
        while ((Get-Date) -lt $deadline -and -not $process.HasExited -and -not ($seen -match 'DevTools attached to the app \(PID')) {
            foreach ($line in Read-NewLines) { $seen.Add($line) }
            Start-Sleep -Milliseconds 250
        }
        $appPid = Get-AppPid
        if (-not $appPid) { throw "The app never started (see $log)." }
        Write-Host "[$Name] app ready (PID $appPid)"
        Invoke-Before $appPid
        $launchPid = $appPid
        foreach ($step in @($journey.steps)) {
            $row = [ordered]@{ Row = $step.row; Step = $step.id; Title = $step.title; V1 = $step.expect.v1; Today = $step.expect.today; Observed = ''; Reason = ''; Status = ''; Checks = @(); Ms = $null; SameProcess = $null; Note = $step.note }
            if ($step.pending) {
                $row.Status = 'pending'; $row.Reason = $step.pending
                $rows.Add([pscustomobject]$row); Write-Host ("[{0}] {1,-28} pending: {2}" -f $Name, $step.id, $step.pending); continue
            }
            $null = Read-NewLines
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-JourneyEdit -Root $src -Step $step
            $lines = [System.Collections.Generic.List[string]]::new()
            $deadline = (Get-Date).AddSeconds($SaveTimeoutSeconds)
            $restartDeadline = $null
            while ($true) {
                Start-Sleep -Milliseconds 250
                foreach ($line in Read-NewLines) { $lines.Add($line) }
                $result = Get-WatchOutcome -Lines $lines -Exited $process.HasExited
                if ($result -and ($result.Outcome -notin 'restart', 'restart-no-reason', 'stuck' -or $result.Ready)) { break }
                # A restart is decided once the app is back, or when no app came back in the time a build takes. A
                # failed rebuild can still recover when a later file in the same save fixes it.
                if ($result -and -not $restartDeadline) { $restartDeadline = (Get-Date).AddSeconds(300) }
                if ((Get-Date) -ge $(if ($restartDeadline) { $restartDeadline } else { $deadline })) { break }
            }
            if ($result -and $result.Outcome -in 'restart', 'restart-no-reason' -and -not $result.Ready) {
                $result = [pscustomobject]@{ Outcome = 'stuck'; Reason = 'the app did not come back'; Note = $false; Ready = $false }
            }
            $row.Ms = [int]$clock.Elapsed.TotalMilliseconds
            $row.Observed = if ($result) { $result.Outcome } elseif (@($lines | Where-Object { $_.Trim() }).Count -eq 0) { 'ignored' } else { 'timeout' }
            $row.Reason = if ($result) { $result.Reason } else { '' }
            if ($row.Observed -in 'restart', 'restart-no-reason') { Start-Sleep -Seconds 2; $appPid = Get-AppPid; if ($appPid) { Invoke-Before $appPid } }
            $row.SameProcess = ($appPid -eq $launchPid)
            # The checks show whether the change is on screen; they only matter when the save says it reached the app.
            $checksOk = $true
            if ($row.Observed -in 'applied', 'restart', 'restart-no-reason' -and $appPid) {
                $row.Checks = @(foreach ($check in @($step.checks | Where-Object { $_ })) {
                    $r = Wait-UiCheck $check $appPid
                    if (-not $r.Ok) { $checksOk = $false }
                    [pscustomobject]@{ Check = "$($check.assert) $($check.target) $($check.value)".Trim(); Ok = $r.Ok; Output = $r.Output }
                })
                if ($step.parity -and -not $NoParity) {
                    $parity = Invoke-ParityCheck -Winapp $Winapp -ProcessId $appPid -SourceRoot $src -Project $journey.project -WorkDir (Join-Path $dir "parity-$($step.id)") `
                        -InvokeProbe { param($c, [int]$p) Test-UiCheck $c $p } -Config $journey.parity -Packaged:([bool]$journey.packaged)
                    $row.Checks += [pscustomobject]@{ Check = 'parity'; Ok = $parity.Ok; Output = $parity.Output }
                    if (-not $parity.Ok) { $checksOk = $false }
                }
            }
            $row.Status = Get-StepStatus -Step $step -Observed $row.Observed -Note ([bool]$result.Note) -ChecksOk $checksOk -Reason $row.Reason
            $row.Detail = (@($lines | Where-Object { $_ -match '^\[watch\]|^\s+note:|^dotnet watch|DevTools attached|app restarted|\berror\b' }) | Select-Object -First 15) -join "`n"
            $rows.Add([pscustomobject]$row)
            $failed = @($row.Checks | Where-Object { -not $_.Ok } | ForEach-Object Check)
            Write-Host ("[{0}] {1,-28} {2,-17} v1 {3,-9} today {4,-17} {5,-8} {6,6} ms{7}{8}" -f $Name, $step.id, $row.Observed, $row.V1, $row.Today, $row.Status, $row.Ms,
                $(if ($row.Reason) { " - $($row.Reason)" }), $(if ($failed) { " FAILED: $($failed -join ', ')" }))
            if ($process.HasExited) { break }
        }
    }
    finally {
        Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($dir) -and $_.ProcessId -ne $PID } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Get-Process $journey.process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($src, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
        if ($journey.packaged) { Start-Sleep -Milliseconds 500; $null = & $Winapp unregister $projectDir --quiet 2>&1 }
        $rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'results.json')
    }
    return [pscustomobject]@{ Journey = $journey; Rows = $rows }
}

$results = foreach ($name in $Journey) {
    try { Use-DesktopLock "Run-WatchJourney $name (pid $PID)" { Invoke-Journey $name } }
    catch { Write-Warning "[$name] $($_.Exception.Message)`n$($_.ScriptStackTrace)"; [pscustomobject]@{ Journey = [pscustomobject]@{ id = $name; title = "did not run: $($_.Exception.Message)" }; Rows = @() } }
}

# The scoreboard: every step, its v1 expectation, today's recorded outcome and what this run observed.
$md = [System.Collections.Generic.List[string]]::new()
$all = @($results | ForEach-Object { $_.Rows })
$md.Add("# Watch journeys: SDK $sdk, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$md.Add('')
$md.Add("pass $(@($all | Where-Object Status -eq 'pass').Count) · gap $(@($all | Where-Object Status -eq 'gap').Count) · fail $(@($all | Where-Object Status -eq 'fail').Count) · untested $(@($all | Where-Object Status -eq 'untested').Count) · pending $(@($all | Where-Object Status -eq 'pending').Count)")
foreach ($result in $results) {
    $md.Add(''); $md.Add("## $($result.Journey.id): $($result.Journey.title)"); $md.Add('')
    $md.Add('| Row | Step | v1 | Today | Observed | Status | Reason or failed checks |'); $md.Add('|---|---|---|---|---|---|---|')
    foreach ($row in $result.Rows) {
        $why = (@($row.Reason) + @($row.Checks | Where-Object { -not $_.Ok } | ForEach-Object { "check failed: $($_.Check)" }) + @($row.Note) | Where-Object { $_ }) -join '; '
        $md.Add("| $($row.Row) | $($row.Step) | $($row.V1) | $($row.Today) | $($row.Observed) | $($row.Status) | $($why -replace '\|', '/') |")
    }
}
$md | Set-Content (Join-Path $runRoot 'scoreboard.md')
Write-Host ($md -join "`n")
Write-Host "Results: $runRoot"
