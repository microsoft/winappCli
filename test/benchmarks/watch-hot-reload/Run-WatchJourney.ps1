# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Runs the 40-save authored-edit Todo journey against a live 'winapp run --watch' session.
.DESCRIPTION
  Materializes the Todo app, starts 'winapp run --watch', applies each authored edit as one save, records what
  the watch reported for it and which process was running, and runs the journey's uiChecks against the live app
  at each checkpoint. The truth is: did every save land in the same process, and do the checks pass.

  The parity gate (ParityGate.psm1) also checks that the hot-reloaded app matches a clean build of the same source: at
  checkpoints marked "parity": true, and after the last save, it reads the app's XAML tree and property values through
  DevTools, runs the journey's parity probes (clicks, typing) and reads again, then builds the source from scratch,
  launches it, does the same, and fails on any difference. -Parity Auto (the default) runs it for journeys that
  opt in ("parity": { "auto": true }); On runs it for every journey (after the last save at least); Off skips it.
.EXAMPLE
  .\Run-WatchJourney.ps1 -Winapp ..\..\..\artifacts\cli\win-x64\winapp.exe -WinUIPackageSource <folder with a locally built Microsoft.WindowsAppSDK.WinUI .nupkg>
#>
[CmdletBinding()]
param(
    [string]$JourneyName = 'todo-app',
    # Defaults to the journey's primary variants.
    [string[]]$Variant,
    [string]$Winapp = (Join-Path $PSScriptRoot '..\..\..\artifacts\cli\win-x64\winapp.exe'),
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-watch-journey'),
    # A local feed with Microsoft.WindowsAppSDK.WinUI (a WinUI build with the hot reload service). Optional.
    [string]$WinUIPackageSource,
    [string]$WinUIPackageVersion = '3.0.0-dev',
    [ValidateSet('Unpackaged', 'Packaged')][string]$Packaging = 'Unpackaged',
    [int]$SaveTimeoutSeconds = 180,
    # Stop after this many saves (0 = all).
    [int]$MaxActions = 0,
    # Leave the app and the watch running after the last save (to inspect it).
    [switch]$KeepRunning,
    # Compare the hot-reloaded app with a clean build: Auto = journeys with "parity": { "auto": true }, On = every journey.
    [ValidateSet('Auto', 'On', 'Off')][string]$Parity = 'Auto',
    # The lock shared by every session that launches apps on this desktop (see Use-HrDesktop in it).
    [string]$DesktopLockScript = 'C:\hr-shared\DesktopLock.ps1',
    # Run without the desktop lock: for CI agents and machines no other run shares (CI, TF_BUILD and GITHUB_ACTIONS imply it).
    [switch]$NoDesktopLock,
    # MSBuild properties (Name=Value) every build of the app gets, the clean parity build included (for example
    # EnableHotReloadStableConnectionIds=true for one approach on a scoreboard).
    [string[]]$BuildProperty = @()
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WatchJourney.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParityGate.psm1') -Force
$Winapp = (Resolve-Path $Winapp).Path
$journey = Get-Content (Join-Path $PSScriptRoot "journeys\$JourneyName.json") -Raw | ConvertFrom-Json
if (-not $Variant) { $Variant = @($journey.variants | Where-Object primary | ForEach-Object id) }
$runRoot = Join-Path $OutputRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function New-JourneyApp([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'app-template\*') $Directory
    $assets = Join-Path $PSScriptRoot "..\..\..\$($journey.app.assetsFrom)"
    if (Test-Path $assets) { Copy-Item $assets (Join-Path $Directory 'Assets') -Recurse }
    $properties = @()
    if ($Packaging -eq 'Unpackaged') { $properties += '<WindowsPackageType>None</WindowsPackageType>' }
    foreach ($property in $BuildProperty) {
        $name, $value = $property -split '=', 2
        $properties += "<$name>$([System.Security.SecurityElement]::Escape($value))</$name>"
    }
    # The local WinUI package carries the runtime; self-contained keeps a third-party package's transitive
    # Microsoft.WindowsAppSDK from pulling in a different framework. It has to be set before the package targets
    # are evaluated (so here, not in .targets), and only for the app: class libraries reject it.
    if ($WinUIPackageSource) { $properties += "<WindowsAppSDKSelfContained Condition=`"'`$(MSBuildProjectFile)' == '$($journey.app.projectFile)'`">true</WindowsAppSDKSelfContained>" }
    # Next to the app folder rather than in it, so projects beside the app (a solution's libraries) share them.
    $shared = Split-Path $Directory
    if ($properties) {
        Set-Content (Join-Path $shared 'Directory.Build.props') "<Project><PropertyGroup>$($properties -join '')</PropertyGroup></Project>"
    }
    if ($WinUIPackageSource) {
        # Update, not Remove: a package added during the journey can bring Microsoft.WindowsAppSDK back transitively.
        Set-Content (Join-Path $shared 'Directory.Build.targets') @"
<Project>
  <!-- WinUI projects only: a source generator or other netstandard project next to the app takes no WinUI. -->
  <ItemGroup Condition="'`$(UseWinUI)' == 'true'">
    <PackageReference Update="Microsoft.WindowsAppSDK" ExcludeAssets="all" />
    <PackageReference Include="Microsoft.WindowsAppSDK.WinUI" Version="$WinUIPackageVersion" />
  </ItemGroup>
</Project>
"@
        Set-Content (Join-Path $shared 'nuget.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="winui-local" value="$WinUIPackageSource" />
  </packageSources>
</configuration>
"@
    }
}

function Invoke-Ui([string[]]$Arguments) {
    $output = & $Winapp ui @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = $output.Trim() }
}

function Get-ChildCount([string]$Target, [int]$ProcessId) {
    $result = Invoke-Ui @('inspect', $Target, '-a', "$ProcessId", '--depth', '4', '--json')
    if (-not $result.Ok) { return -1 }
    $root = @(($result.Output | ConvertFrom-Json).windows[0].elements)[0]
    $items = 0
    function Walk($node) {
        foreach ($child in @($node.children)) {
            if ($null -eq $child) { continue }
            if ($child.type -eq 'ListItem') { $script:items++ }
            Walk $child
        }
    }
    $script:items = 0
    Walk $root
    if ($script:items -gt 0) { return $script:items }
    return @($root.children).Where({ $null -ne $_ }).Count
}

$script:baselines = @{}

function Get-TextNumber([string]$Target, [int]$ProcessId) {
    $output = Invoke-Ui @('get-property', $Target, '-p', 'Name', '-a', "$ProcessId")
    if ($output.Ok -and $output.Output -match 'Name:\D*(\d+)') { return [int]$Matches[1] }
    return $null
}

# Values the step's checks compare against, read before its save.
function Read-Baselines($Checks, [int]$ProcessId) {
    $script:baselines = @{}
    foreach ($check in @($Checks | Where-Object { $_.assert -eq 'number-increased' })) {
        $script:baselines[[string]$check.target] = Get-TextNumber ([string]$check.target) $ProcessId
    }
}

function Test-UiCheck($Check, [int]$ProcessId) {
    $target = [string]$Check.target
    $value = [string]$Check.value
    $app = @('-a', "$ProcessId")
    switch ([string]$Check.assert) {
        'exists' {
            $result = Invoke-Ui (@('wait-for', $target) + $app + @('-t', '5000'))
            if ($result.Ok) { return $result }
            # Panels and frames have no UI Automation peer even in a normal build; look in the XAML tree instead.
            $output = & $Winapp devtools search $target --all --json @app 2>&1 | Out-String
            return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0 -and $output -match "`"$target`""); Output = "devtools: $($output.Trim())" }
        }
        'invoke' { return Invoke-Ui (@('invoke', $target) + $app) }
        'focus' { return Invoke-Ui (@('focus', $target) + $app) }
        'set-value' { return Invoke-Ui (@('set-value', $target, $value) + $app) }
        'text-equals' { return Invoke-Ui (@('wait-for', $target, '--value', $value) + $app + @('-t', '5000')) }
        'text-contains' { return Invoke-Ui (@('wait-for', $target, '--value', $value, '--contains') + $app + @('-t', '5000')) }
        'absent' { return Invoke-Ui (@('wait-for', $target, '--gone') + $app + @('-t', '5000')) }
        'toggle-state-equals' { return Invoke-Ui (@('wait-for', $target, '-p', 'ToggleState', '--value', $value) + $app + @('-t', '5000')) }
        'enabled-equals' { return Invoke-Ui (@('wait-for', $target, '-p', 'IsEnabled', '--value', $value) + $app + @('-t', '5000')) }
        'property-equals' {
            # value is "Property=Value", read from the XAML tree (properties UI Automation doesn't expose, like FontSize).
            # "Property>N" and "Property<N" compare numbers (a scroll offset that only has to be past a point).
            if ($value -notmatch '^(?<p>[^=<>]+)(?<op>[=<>])(?<v>.*)$') { throw "property-equals value '$value' is not Property=Value, Property>N or Property<N." }
            $property, $op, $expected = $Matches.p, $Matches.op, $Matches.v
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $output = & $Winapp devtools get-property $target $property @app 2>&1 | Out-String
                if ($output -match "(?m)^\s*$([regex]::Escape($property)):\s*(\S+)") {
                    $actual = $Matches[1]
                    $ok = switch ($op) {
                        '=' { $actual -eq $expected }
                        '>' { $actual -as [double] -gt [double]$expected }
                        '<' { $actual -as [double] -lt [double]$expected }
                    }
                    if ($ok) { return [pscustomobject]@{ Ok = $true; Output = $output.Trim() } }
                }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "$($output.Trim()), expected $property$op$expected" }
        }
        'scroll-to-bottom' { return Invoke-Ui (@('scroll', $target, '--to', 'bottom') + $app) }
        'set-property' {
            # value is "Property=Value", set through DevTools (a list's SelectedIndex, as if the user picked an item).
            $property, $newValue = $value -split '=', 2
            $output = & $Winapp devtools set-property $target $property $newValue @app 2>&1 | Out-String
            return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = $output.Trim() }
        }
        'toggle-on' {
            $state = Invoke-Ui (@('get-property', $target, '-p', 'ToggleState') + $app)
            if ($state.Output -cnotmatch 'ToggleState: On') { $null = Invoke-Ui (@('invoke', $target) + $app) }
            return Invoke-Ui (@('wait-for', $target, '-p', 'ToggleState', '--value', 'On') + $app + @('-t', '5000'))
        }
        'layout-width-equals' {
            # The element's laid-out width in XAML units (an x:Name), e.g. an Image showing its file at natural size. Within
            # 1 unit: at a display scale other than 100% the layout snaps to device pixels (50 px at 125% lays out as 50.4).
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $output = & $Winapp devtools get-layout $target @app 2>&1 | Out-String
                if ($output -match 'actual:\s*([\d.]+)\s*x' -and [math]::Abs([double]$Matches[1] - [double]$value) -le 1) { return [pscustomobject]@{ Ok = $true; Output = $output.Trim() } }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "$($output.Trim()), expected width $value" }
        }
        'number-increased' {
            # A number in the element's text grew since before the save (read by Read-Baselines), e.g. an event counter whose
            # absolute value depends on how often the window resized at this display scale.
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $now = Get-TextNumber $target $ProcessId
                if ($null -ne $now -and $null -ne $script:baselines[$target] -and $now -gt $script:baselines[$target]) {
                    return [pscustomobject]@{ Ok = $true; Output = "$($script:baselines[$target]) -> $now" }
                }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "$($script:baselines[$target]) -> $now, expected an increase" }
        }
        'child-count-equals' {
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $count = Get-ChildCount $target $ProcessId
                if ("$count" -eq $value) { return [pscustomobject]@{ Ok = $true; Output = "count $count" } }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "count $count, expected $value" }
        }
        default { throw "No mapping for uiCheck assert '$($Check.assert)'." }
    }
}

# Only this run's app: other runs of the same app may be on the machine.
function Get-AppPid { (Get-Process TodoBench -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object StartTime -Descending | Select-Object -First 1).Id }

$parityOn = $journey.parity.enabled -ne $false -and ($Parity -eq 'On' -or ($Parity -eq 'Auto' -and $journey.parity.auto))
$parityConfig = if ($journey.parity) { $journey.parity } else { [pscustomobject]@{} }
$parityProbe = { param($Probe, [int]$ProcessId) Test-UiCheck $Probe $ProcessId }

$summary = [System.Collections.Generic.List[object]]::new()
# Everything that launches an app or drives UI runs under the desktop lock several hot reload sessions share on one
# desktop (UI Automation and input from two runs at once give false failures). CI agents have the desktop to themselves.
$runVariants = {
    foreach ($variantId in $Variant) {
        $appDir = Join-Path $runRoot "$variantId\app"
        New-JourneyApp $appDir
        # Setup edits are part of the app the first build compiles.
        foreach ($setup in @($journey.setup)) { if ($setup) { Invoke-JourneyEdit -AppDir $appDir -Action $setup } }
        $log = Join-Path $runRoot "$variantId\watch.log"
        # A journey can pin the build configuration (for example Release, where XAML hot reload is not enabled).
        $runArguments = @('run', $appDir, '--watch') + $(if ($journey.app.configuration) { @('-c', $journey.app.configuration) } else { @() })
        $process = Start-Process -FilePath $Winapp -ArgumentList $runArguments -WorkingDirectory $appDir `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
        $script:consumed = 0
        function Read-NewLines {
            if (-not (Test-Path $log)) { return @() }
            $all = @(Get-Content $log)
            $new = @($all | Select-Object -Skip $script:consumed)
            $script:consumed = $all.Count
            return $new
        }
        function Wait-Line([string]$Pattern, [int]$TimeoutSeconds, [System.Collections.Generic.List[string]]$Seen) {
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
                $found = $null
                foreach ($line in Read-NewLines) {
                    $Seen.Add($line)
                    if (-not $found -and $line -match $Pattern) { $found = $line }
                }
                if ($found) { return $found }
                Start-Sleep -Milliseconds 100
            }
            return $null
        }
        $seen = [System.Collections.Generic.List[string]]::new()
        $ready = Wait-Line '^\[watch\] (ready|error)' 600 $seen
        $appPid = Get-AppPid
        Write-Host "[$variantId] $ready"
        $launchPid = $appPid
        # What winapp must say once the app is up (for example why XAML hot reload is unavailable): checked with the first save.
        $launchChecks = @()
        if ($journey.app.launchExpect) {
            Start-Sleep -Milliseconds 1000
            foreach ($extra in Read-NewLines) { $seen.Add($extra) }
            $launchText = (($seen | Where-Object { $_ -match '^\[watch\]' }) -join "`n")
            $launchChecks += [pscustomobject]@{ Check = "launch $($journey.app.launchExpect)"; Ok = ($launchText -match $journey.app.launchExpect); Output = $launchText }
        }
        $rows = [System.Collections.Generic.List[object]]::new()
        # Text typed and boxes toggled by the journey's checks in the running app, latest per element (for the parity gate).
        $inputsSoFar = [System.Collections.Generic.List[object]]::new()
        $plan = Get-JourneyPlan $journey $variantId
        $lastIndex = if ($MaxActions -gt 0) { [Math]::Min($MaxActions, $plan.Count) } else { $plan.Count }
        foreach ($step in $plan) {
            if ($MaxActions -gt 0 -and $step.Index -gt $MaxActions) { break }
            $seen = [System.Collections.Generic.List[string]]::new()
            $null = Read-NewLines
            if ($appPid) { Read-Baselines $step.Checkpoint.uiChecks ([int]$appPid) }
            $saveClock = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-JourneyEdit -AppDir $appDir -Action $step.Action
            $line = Wait-Line '^\[watch\] (applied|waiting|error|restart|unchanged|ready)' $SaveTimeoutSeconds $seen
            $outcome = if ($line -match '^\[watch\] (\w+)') { $Matches[1] } else { 'timeout' }
            # After a failed rebuild or a crash, the save that fixes it rebuilds and relaunches: that's a restart.
            $relaunched = $outcome -eq 'ready'
            if ($relaunched) { $outcome = 'restart' }
            # How long the developer waited: until the save's outcome, or until the restarted app is up.
            $wallMs = [int]$saveClock.Elapsed.TotalMilliseconds
            Start-Sleep -Milliseconds 300
            foreach ($extra in Read-NewLines) { $seen.Add($extra) }
            $timed = @($seen | Where-Object { $_ -match '\((\d+) ms\)\s*$' } | Select-Object -First 1)
            $ms = if ($timed -and $timed[0] -match '\((\d+) ms\)\s*$') { [int]$Matches[1] } else { $null }
            if ($outcome -eq 'restart') {
                if (-not $relaunched) { $null = Wait-Line '^\[watch\] ready' 600 $seen }
                $wallMs = [int]$saveClock.Elapsed.TotalMilliseconds
                $appPid = Get-AppPid
                $inputsSoFar.Clear()
            }
            $checks = @($launchChecks)
            $launchChecks = @()
            if ($step.Checkpoint.uiChecks) {
                Start-Sleep -Milliseconds 700
                foreach ($check in $step.Checkpoint.uiChecks) {
                    $result = Test-UiCheck $check ([int]$appPid)
                    # A check with "state": "<what>" asks whether something the user or the app built up survived the save.
                    $checks += [pscustomobject]@{ Check = "$($check.assert) $($check.target) $($check.value)".Trim(); Ok = $result.Ok; Output = $result.Output; State = $check.state }
                    if ($check.assert -in 'set-value', 'toggle-on') {
                        $target = $check.target
                        $null = $inputsSoFar.RemoveAll([Predicate[object]] { param($i) $i.target -eq $target })
                        $inputsSoFar.Add($check)
                    }
                }
            }
            $detail = (($seen | Where-Object { $_ -match '^\[watch\]|^  ' }) -join "`n")
            # What the status line itself must say (an error the compiler would report, a note), checked like the UI.
            if ($step.Action.expect.outcome) {
                $checks += [pscustomobject]@{ Check = "outcome $($step.Action.expect.outcome)"; Ok = ($outcome -eq $step.Action.expect.outcome); Output = $detail }
            }
            if ($step.Action.expect.detail) {
                $checks += [pscustomobject]@{ Check = "detail $($step.Action.expect.detail)"; Ok = ($detail -match $step.Action.expect.detail); Output = $detail }
            }
            $paritySeconds = $null
            if ($parityOn -and ($step.Checkpoint.parity -or $step.Index -eq $lastIndex)) {
                $label = if ($step.Checkpoint.id) { [string]$step.Checkpoint.id } else { "save$($step.Index)" }
                if ($outcome -eq 'error' -or -not $appPid) {
                    # The source doesn't build (or no app is running), so there is no clean build to compare with.
                    $checks += [pscustomobject]@{ Check = "parity $label"; Ok = $true; Output = "parity: skipped, the save left the app $(if ($outcome -eq 'error') { 'with a build error' } else { 'not running' })" }
                } else {
                    # A checkpoint can carry its own parity settings ({ "probes": [...] }) over the journey's.
                    $config = $parityConfig.PSObject.Copy()
                    if ($step.Checkpoint.parity -is [pscustomobject]) {
                        foreach ($p in $step.Checkpoint.parity.PSObject.Properties) { $config | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force }
                    }
                    # What the user typed or toggled earlier in the journey is state, not markup: the clean build gets the same
                    # input before it is read (where the hot-reloaded app still shows it). Clicks are not replayed: their effect
                    # depends on the code they ran against.
                    if ($config.replayInputs -ne $false) {
                        $replay = @($inputsSoFar | Where-Object { $_.target -notin @($config.probes | ForEach-Object target) })
                        $config | Add-Member -NotePropertyName replay -NotePropertyValue $replay -Force
                    }
                    $parityResult = Invoke-ParityCheck -Winapp $Winapp -ProcessId ([int]$appPid) -AppDir $appDir -WorkDir (Join-Path $runRoot "$variantId\parity\$label") `
                        -InvokeProbe $parityProbe -Config $config -RootNamespace $journey.app.rootNamespace -Configuration $journey.app.configuration -Packaging $Packaging
                    $paritySeconds = $parityResult.Seconds
                    $checks += [pscustomobject]@{ Check = "parity $label"; Ok = $parityResult.Ok; Output = $parityResult.Output; Diffs = @($parityResult.Diffs).Count }
                    Write-Host $parityResult.Output
                }
            }
            $row = [pscustomobject]@{
                Index = $step.Index; Action = $step.Action.id; Kind = (Get-JourneySaveKind $step.Action); Outcome = $outcome; Ms = $ms; WallMs = $wallMs; Pid = $appPid
                Reason = $(if ($outcome -in 'restart', 'error', 'waiting', 'timeout') { Get-JourneyOutcomeReason $detail $outcome })
                SameProcess = ($appPid -eq $launchPid); Checkpoint = $step.Checkpoint.id
                Checks = $checks; Detail = $detail; ParitySeconds = $paritySeconds
            }
            $rows.Add($row)
            $checkText = if ($checks) { ' checks ' + (($checks | ForEach-Object { if ($_.Ok) { 'ok' } else { "FAIL($($_.Check))" } }) -join ',') } else { '' }
            Write-Host ("[{0}] {1,2} {2,-30} {3,-9} {4,6} ms pid {5}{6}" -f $variantId, $step.Index, $step.Action.id, $outcome, $ms, $appPid, $checkText)
        }
        if (-not $KeepRunning -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        if (-not $KeepRunning -and $appPid) { Stop-Process -Id ([int]$appPid) -Force -ErrorAction SilentlyContinue }
        $rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $runRoot "$variantId\results.json")
        $summary.Add([pscustomobject]@{
            Variant = $variantId; Saves = $rows.Count
            Live = @($rows | Where-Object { $_.Outcome -ne 'restart' -and $_.Outcome -ne 'timeout' }).Count
            Restarts = @($rows | Where-Object Outcome -eq 'restart').Count
            Errors = @($rows | Where-Object Outcome -eq 'error').Count
            ChecksPassed = @($rows.Checks | Where-Object Ok).Count; Checks = @($rows.Checks).Count
            Parity = $(if ($parityOn) { $p = @($rows.Checks | Where-Object { $_.Check -like 'parity *' }); "$(@($p | Where-Object Ok).Count)/$($p.Count)" } else { 'off' })
            SameProcess = -not ($rows | Where-Object { -not $_.SameProcess })
        })
    }
}
if ($NoDesktopLock -or $env:CI -or $env:TF_BUILD -or $env:GITHUB_ACTIONS) {
    & $runVariants
} elseif (Test-Path $DesktopLockScript) {
    . $DesktopLockScript
    Use-HrDesktop -Owner "Run-WatchJourney $JourneyName $($Variant -join '+') (pid $PID)" -Script $runVariants
} else {
    throw "The desktop lock script $DesktopLockScript was not found. Pass -DesktopLockScript, or -NoDesktopLock on a machine no other run shares."
}

$summary | Format-Table -AutoSize | Out-String | Write-Host
$summary | ConvertTo-Json | Set-Content (Join-Path $runRoot 'summary.json')
Write-Host "Results: $runRoot"
