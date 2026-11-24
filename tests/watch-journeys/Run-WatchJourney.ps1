# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Runs 'winapp run --watch' journeys and scores every save against its expected outcome.
.DESCRIPTION
  For each journey: copies its app to a fresh folder, starts 'winapp run <app> --watch', makes each step's edits as one
  save, reads what the save did from the watch output (applied, unchanged, not-applied, waiting, error, restart, ...), runs the step's
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
    # A folder with a Microsoft.WindowsAppSDK.WinUI .nupkg built with XAML hot reload support (a WinUI fork build). The
    # app then uses it (self-contained), and steps are scored against expect.today; without it, against expect.stock.
    [string]$WinUI,
    # Where packages restore; a fresh folder per WinUI build, so a package of the same version isn't mixed up with another.
    [string]$NuGetPackages,
    [int]$SaveTimeoutSeconds = 90,
    [switch]$NoParity,
    # Apps that aren't in apps\ (a real app's checkout, set up to build), by the name journeys use: @{ 'winui-gallery' = 'C:\src\WinUI-Gallery' }.
    [hashtable]$AppSource
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WatchJourney.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParityGate.psm1') -Force
$Winapp = (Resolve-Path $Winapp).Path
if ($DotnetRoot) {
    $env:PATH = "$DotnetRoot;$env:PATH"; $env:DOTNET_ROOT = $DotnetRoot; $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
# A reused MSBuild node keeps the XAML compiler it loaded first, so a build could run an older WinUI package's compiler.
$env:MSBUILDDISABLENODEREUSE = '1'
# dotnet watch's messages are read in English.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# The SDK winapp's 'dotnet' resolves to from a folder: the version and the dotnet.exe it comes from.
function Get-DotnetSdk([string]$Directory) {
    Push-Location $Directory
    try { $version = @(& dotnet --version | Where-Object { $_ -match '^\d+\.\d+\.\d+' })[0] } finally { Pop-Location }
    return [pscustomobject]@{ Version = $version; Dotnet = (Get-Command dotnet).Source }
}
# Where the apps build: outside this repository (whose global.json pins its own SDK), so a global.json in -OutputRoot
# picks the SDK (for example 10.0.404 when SDK 11 is installed too).
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$dotnet = Get-DotnetSdk $OutputRoot
if ($DotnetRoot -and -not $dotnet.Dotnet.StartsWith($DotnetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "dotnet resolves to $($dotnet.Dotnet), not $DotnetRoot." }
$sdk = $dotnet.Version
if ($WinUI) {
    $WinUI = (Resolve-Path $WinUI).Path
    $env:NUGET_PACKAGES = if ($NuGetPackages) { $NuGetPackages } else { Join-Path $OutputRoot ('nuget-' + (Split-Path $WinUI -Leaf)) }
    $winuiVersion = ([IO.Path]::GetFileNameWithoutExtension((Get-ChildItem $WinUI -Filter 'Microsoft.WindowsAppSDK.WinUI.*.nupkg' | Select-Object -First 1).Name)) -replace '^Microsoft\.WindowsAppSDK\.WinUI\.', ''
}
if (-not $Journey) { $Journey = @(Get-ChildItem (Join-Path $PSScriptRoot 'journeys') -Filter *.json | ForEach-Object BaseName) }
$runRoot = Join-Path $OutputRoot "$(Get-Date -Format 'yyyyMMdd-HHmmss')-sdk$sdk"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Write-Host "SDK $sdk ($($dotnet.Dotnet)), WinUI $(if ($WinUI) { $WinUI } else { 'stock (the app''s own package)' }), winapp $Winapp, results in $runRoot"

function Invoke-Ui([string[]]$Arguments) {
    $output = & $Winapp ui @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = (Remove-Ansi $output).Trim() }
}

function Test-UiCheck($Check, [int]$ProcessId) {
    $target = [string]$Check.target; $value = [string]$Check.value; $app = @('-a', "$ProcessId")
    switch ([string]$Check.assert) {
        'invoke' { return Invoke-Ui (@('invoke', $target) + $app) }
        'set-value' { return Invoke-Ui (@('set-value', $target, $value) + $app) }
        # The element's text moves on (a ticking counter): the UI thread is alive.
        'text-changes' {
            $first = Invoke-Ui (@('get-value', $target) + $app); Start-Sleep -Seconds 2; $second = Invoke-Ui (@('get-value', $target) + $app)
            return [pscustomobject]@{ Ok = ($first.Ok -and $second.Ok -and $first.Output -ne $second.Output); Output = "'$($first.Output)' then '$($second.Output)'" }
        }
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
        'xaml-text' {
            # Text in the XAML tree, such as a list item's template content that UI Automation may not expose.
            $output = Remove-Ansi (& $Winapp devtools search $target @app 2>&1 | Out-String)
            return [pscustomobject]@{ Ok = $LASTEXITCODE -eq 0; Output = $output.Trim() }
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
    # Give other sessions a turn: wait 60 s after this machine user's runs last released the lock.
    $releasedFile = Join-Path ([IO.Path]::GetTempPath()) 'winapp-watch-journeys-lock-released.txt'
    if (Test-Path $releasedFile) {
        $since = (Get-Date) - [datetime](Get-Content $releasedFile -Raw).Trim()
        if ($since.TotalSeconds -lt 60) { Start-Sleep -Seconds ([int][Math]::Ceiling(60 - $since.TotalSeconds)) }
    }
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
    try { & $Script } finally { Remove-Item $DesktopLock -Force -ErrorAction SilentlyContinue; Set-Content $releasedFile (Get-Date -Format o) }
}

function Invoke-Journey([string]$Name) {
    $journey = Get-Content (Join-Path $PSScriptRoot "journeys\$Name.json") -Raw | ConvertFrom-Json
    $dir = Join-Path $runRoot $Name
    $src = Join-Path $dir 'src'
    $appFolder = if ($AppSource -and $AppSource[$journey.app]) { $AppSource[$journey.app] } else { Join-Path $PSScriptRoot "apps\$($journey.app)" }
    if (-not (Test-Path $appFolder)) { throw "The app '$($journey.app)' isn't in apps\: pass -AppSource @{ '$($journey.app)' = '<checkout>' }." }
    $null = robocopy $appFolder $src /E /XD bin obj .git /NFL /NDL /NJH /NJS /NP
    $global:LASTEXITCODE = 0
    $projectDir = Join-Path $src $journey.project
    if ($journey.app -eq 'winui') {
        Copy-Item (Join-Path $PSScriptRoot '..\..\samples\winui-app\Assets') (Join-Path $projectDir 'Assets') -Recurse
        $props = @(if (-not $journey.packaged) { '<WindowsPackageType>None</WindowsPackageType>' })
        if ($WinUI) {
            # The WinUI build carries its runtime: self-contained, and only for the app (class libraries reject it).
            $props += "<WindowsAppSDKSelfContained Condition=`"'`$(MSBuildProjectFile)' == '$((Get-ChildItem $projectDir -Filter *.csproj | Select-Object -First 1).Name)'`">true</WindowsAppSDKSelfContained>"
            Set-Content (Join-Path $src 'Directory.Build.targets') "<Project><ItemGroup Condition=`"'`$(UseWinUI)' == 'true'`"><PackageReference Update=`"Microsoft.WindowsAppSDK.WinUI`" Version=`"$winuiVersion`" /><PackageReference Remove=`"Microsoft.WindowsAppSDK.Runtime`" /></ItemGroup></Project>"
            Set-Content (Join-Path $src 'nuget.config') "<?xml version=`"1.0`" encoding=`"utf-8`"?><configuration><packageSources><add key=`"winui-local`" value=`"$WinUI`" /></packageSources></configuration>"
        }
        if ($props) { Set-Content (Join-Path $src 'Directory.Build.props') "<Project><PropertyGroup>$($props -join '')</PropertyGroup></Project>" }
    }
    foreach ($edit in @($journey.setup | Where-Object { $_ })) { Invoke-JourneyEdit -Root $src -Step $edit }
    $log = Join-Path $dir 'watch.log'
    $arguments = @('run', "`"$projectDir`"", '--watch') + $(if ($journey.packaged) { @('--unique-identity') } else { @() }) + @($journey.runArguments | Where-Object { $_ })
    # A journey that presses keys ("op": "key") gives winapp its input through a pipe: Ctrl+R is the 0x12 character.
    $keys = $null; $stdin = ''
    if (@($journey.steps | Where-Object op -eq 'key').Count) {
        $keys = [System.IO.Pipes.NamedPipeServerStream]::new("winapp-journey-$PID", [System.IO.Pipes.PipeDirection]::Out)
        $stdin = " < \\.\pipe\winapp-journey-$PID"
    }
    # The SDK this journey's builds use: winapp runs 'dotnet' from PATH in the project folder.
    $journeySdk = Get-DotnetSdk $projectDir
    Write-Host "[$Name] SDK $($journeySdk.Version) ($($journeySdk.Dotnet))"
    # One file for winapp's and dotnet watch's output, in the order they wrote it.
    $process = Start-Process cmd.exe -ArgumentList '/d', '/c', "`"`"$Winapp`" $($arguments -join ' ') > `"$log`" 2>&1$stdin`"" -WorkingDirectory $projectDir -PassThru -WindowStyle Hidden
    if ($keys -and -not $keys.WaitForConnectionAsync().Wait(30000)) { throw 'winapp never opened its input pipe.' }
    $script:offset = 0
    # Whole lines only: a line still being written is read once it ends.
    function Read-NewLines {
        if (-not (Test-Path $log)) { return @() }
        $raw = Get-Content $log -Raw -Encoding utf8
        $end = if ($raw) { $raw.LastIndexOf("`n") } else { -1 }
        if ($end -lt $script:offset) { return @() }
        $chunk = $raw.Substring($script:offset, $end + 1 - $script:offset)
        $script:offset = $end + 1
        return @($chunk -split "`r?`n" | Select-Object -SkipLast 1)
    }
    function Get-AppPid { (Get-Process $journey.process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($src, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object StartTime -Descending | Select-Object -First 1).Id }
    function Invoke-Before([int]$ProcessId) { foreach ($check in @($journey.before | Where-Object { $_ })) { $null = Wait-UiCheck $check $ProcessId } }
    # The build an editor runs for IntelliSense: the XAML compiler's first pass only, into the app's obj folder.
    function Invoke-DesignTimeBuild([switch]$Background) {
        $csproj = (Get-ChildItem $projectDir -Filter *.csproj | Select-Object -First 1).FullName
        $msbuild = @('msbuild', "`"$csproj`"", '-nologo', '-v:q', '-t:Compile', '-p:DesignTimeBuild=true', '-p:BuildingProject=false',
            '-p:SkipCompilerExecution=true', '-p:ProvideCommandLineArgs=true', '-p:BuildingInsideVisualStudio=true')
        $started = Start-Process dotnet -ArgumentList $msbuild -WorkingDirectory $projectDir -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $dir 'design-time.log')
        if (-not $Background) { $started.WaitForExit() }
        return $started
    }

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
        if ($WinUI) {
            # Fork packages share one version, so a reused NUGET_PACKAGES could hold an older one.
            $source = (Get-Content (Join-Path $env:NUGET_PACKAGES "microsoft.windowsappsdk.winui\$winuiVersion\.nupkg.metadata") -Raw | ConvertFrom-Json).source
            if ($source.TrimEnd('\') -ne $WinUI.TrimEnd('\')) { throw "NUGET_PACKAGES holds WinUI $winuiVersion from $source, not $WinUI; use a fresh folder." }
            Write-Host "[$Name] WinUI $winuiVersion restored from $source"
        }
        Invoke-Before $appPid
        $launchPid = $appPid
        foreach ($step in @($journey.steps)) {
            $row = [ordered]@{ Row = $step.row; Step = $step.id; Title = $step.title; V1 = $step.expect.v1; Today = $step.expect.today; Observed = ''; Reason = ''; Status = ''; Checks = @(); Ms = $null; SameProcess = $null; Note = $step.note }
            $pending = if ($step.pending) { $step.pending } elseif ($WinUI -and $step.stockOnly) { 'checks stock WinUI; run without -WinUI' }
            if ($pending) {
                $row.Status = 'pending'; $row.Reason = $pending
                $rows.Add([pscustomobject]$row); Write-Host ("[{0}] {1,-28} pending: {2}" -f $Name, $step.id, $pending); continue
            }
            $null = Read-NewLines
            # An editor's design-time build (VS Code C# Dev Kit, Visual Studio) into the same obj folder.
            $designTime = $null
            if ($step.designTimeBuild -eq 'before') { $null = Invoke-DesignTimeBuild; $null = Read-NewLines }
            $saveStart = Get-Date
            if ($step.op -eq 'key') {
                if ($step.key -ne 'ctrl+r') { throw "Unknown key '$($step.key)'." }
                $keys.WriteByte(0x12); $keys.Flush()
            }
            else { Invoke-JourneyEdit -Root $src -Step $step }
            if ($step.designTimeBuild -eq 'during') { $designTime = Invoke-DesignTimeBuild -Background }
            $lines = [System.Collections.Generic.List[string]]::new()
            $deadline = (Get-Date).AddSeconds($SaveTimeoutSeconds)
            $restartDeadline = $null
            $lastLine = Get-Date
            while ($true) {
                Start-Sleep -Milliseconds 250
                foreach ($line in Read-NewLines) { $lines.Add($line); $lastLine = Get-Date }
                $result = Get-WatchOutcome -Lines $lines -Exited $process.HasExited
                # An outcome counts once the output has been quiet for a moment: one save can arrive in several batches.
                $settled = ((Get-Date) - $lastLine).TotalSeconds -ge 1.5
                if ($result -and $settled -and ($result.Outcome -notin 'restart', 'restart-no-reason', 'stuck' -or $result.Ready)) { break }
                # A restart is decided once the app is back, or when no app came back in the time a build takes. A
                # failed rebuild can still recover when a later file in the same save fixes it.
                if ($result -and -not $restartDeadline) { $restartDeadline = (Get-Date).AddSeconds(300) }
                if ((Get-Date) -ge $(if ($restartDeadline) { $restartDeadline } else { $deadline })) { break }
            }
            if ($designTime) { $designTime.WaitForExit() }
            if ($result -and $result.Outcome -in 'restart', 'restart-no-reason' -and -not $result.Ready) {
                $result = [pscustomobject]@{ Outcome = 'stuck'; Reason = 'the app did not come back'; Note = $false; Ready = $false }
            }
            # Save to the last line it printed (the time a developer waited), not counting the quiet period.
            $row.Ms = [int](($(if ($lines.Count) { $lastLine } else { Get-Date }) - $saveStart).TotalMilliseconds)
            $row.Observed = if ($result) { $result.Outcome } elseif (@($lines | Where-Object { $_.Trim() }).Count -eq 0) { 'ignored' } else { 'timeout' }
            $row.Reason = if ($result) { $result.Reason } else { '' }
            if ($row.Observed -in 'restart', 'restart-no-reason') { Start-Sleep -Seconds 2; $appPid = Get-AppPid; if ($appPid) { Invoke-Before $appPid } }
            $row.SameProcess = ($appPid -eq $launchPid)
            # The checks show whether the change is on screen (after a save that reached the app), or that the app is
            # untouched (after one that didn't).
            $checksOk = $true
            if ($row.Observed -notin 'crash', 'ignored', 'stuck' -and $appPid) {
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
            $row.Status = Get-StepStatus -Step $step -Stock:(-not $WinUI) -Observed $row.Observed -Note ([bool]$result.Note) -ChecksOk $checksOk -Reason $row.Reason
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
        if ($keys) { $keys.Dispose() }
        $rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'results.json')
    }
    return [pscustomobject]@{ Journey = $journey; Rows = $rows; Sdk = "$($journeySdk.Version) ($($journeySdk.Dotnet))" }
}

$results = foreach ($name in $Journey) {
    try { Use-DesktopLock "Run-WatchJourney $name (pid $PID)" { Invoke-Journey $name } }
    catch { Write-Warning "[$name] $($_.Exception.Message)`n$($_.ScriptStackTrace)"; [pscustomobject]@{ Journey = [pscustomobject]@{ id = $name; title = "did not run: $($_.Exception.Message)" }; Rows = @() } }
}

# The scoreboard: every step, its v1 expectation, today's recorded outcome and what this run observed.
$md = [System.Collections.Generic.List[string]]::new()
$all = @($results | ForEach-Object { $_.Rows })
$md.Add("# Watch journeys: SDK $sdk, WinUI $(if ($WinUI) { Split-Path $WinUI -Leaf } else { 'stock' }), $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$md.Add('')
$md.Add("SDK used by the journeys: $((@($results | ForEach-Object Sdk | Where-Object { $_ }) | Sort-Object -Unique) -join ' and ')")
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
