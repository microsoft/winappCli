# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    Measure what WinUI DevTools costs an app: startup, idle CPU, memory, UI responsiveness,
    interaction cost and stress, with and without `winapp run --devtools`.
.DESCRIPTION
    Each app is launched in each mode on the same build, at the same window size (1280x800):
      off       winapp run
      devtools  winapp run --devtools              (engine + overlay)
      engine    winapp run --devtools --no-overlay (engine only)
    Modes rotate across repetitions so drift on a shared machine does not favour one mode.
    One warm-up launch per app and mode is discarded, so all numbers are warm starts.

    Apps:
      bench    a copy of samples/winui-app with a unique package identity and an in-app probe
               (first frame, frame gaps, UI-thread dispatch lag, memory per navigation).
      gallery  WinUI Gallery (-GalleryPath). Read-only use; any .winapp folder this run creates is deleted.
      aidev    AI Dev Gallery (-AiDevGalleryPath). Its .winapp folder is restored byte-identical.

    Writes results.json and summary.md to -OutDir and prints the summary. Budgets come from
    scripts/devtools-perf/budgets.json (override with -Budgets). Windows show without taking focus where
    the app allows it; real input is never sent.

    -PrepareOnly stages and builds the bench app without measuring (used by devtools-perf\retention.ps1).
    -StartupOnly launches and closes each mode (startup and build numbers only). -SoakOnly runs only the
    bench app's soak (-SoakMinutes per mode). Merge result files with devtools-perf\summarize.ps1 -Results a,b.
.EXAMPLE
    .\scripts\devtools-perf.ps1
.EXAMPLE
    .\scripts\devtools-perf.ps1 -Apps bench,gallery -GalleryPath C:\src\WinUI-Gallery -Repetitions 5
#>
[CmdletBinding()]
param(
    [string]$Winapp,
    [ValidateSet('bench', 'gallery', 'aidev')][string[]]$Apps = @('bench'),
    [ValidateSet('off', 'devtools', 'engine', 'alias', 'sourceinfo')][string[]]$Modes = @('off', 'devtools', 'engine'),
    [int]$Repetitions = 3,
    [int]$IdleSeconds = 60,
    [int]$SettleSeconds = 5,
    [int]$ScrollSeconds = 8,
    [int]$NavCycles = 30,
    [int[]]$BigTreeItems = @(1000, 5000),
    [int]$ChurnElements = 2000,
    [int]$ChurnCycles = 10,
    [int]$ChurnRounds = 4,
    [int]$StressComments = 500,
    [int]$SoakMinutes = 0,
    [switch]$SoakOnly,
    [switch]$StartupOnly,
    [switch]$PrepareOnly,
    [int]$BuildRepetitions = 2,
    [string]$GalleryPath,
    [string[]]$GalleryProperty = @(),
    [int]$GalleryPages = 120,
    [string]$AiDevGalleryPath,
    [string[]]$AiDevGalleryProperty = @(),
    [int]$AwayAndBackLoops = 10,
    [string[]]$NavExclude = @('Capture Element / Camera Preview'),
    [string]$Budgets = (Join-Path $PSScriptRoot 'devtools-perf\budgets.json'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts\devtools-perf')
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Winapp) {
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $Winapp = Join-Path $repoRoot "artifacts\cli\win-$arch\winapp.exe"
}
$Winapp = (Resolve-Path $Winapp).Path
$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$work = Join-Path ([IO.Path]::GetTempPath()) 'winapp-devtools-perf'
New-Item -ItemType Directory -Path $work -Force | Out-Null
Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'devtools-perf\PerfNative.cs'))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$budget = Get-Content -Raw $Budgets | ConvertFrom-Json
$cores = [Environment]::ProcessorCount

function Write-Step([string]$Message) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $Message" }

function Get-Stats([double[]]$Values) {
    $v = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    $pick = { param($p) $v[[Math]::Min($v.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($p * $v.Count) - 1))] }
    [ordered]@{
        n = $v.Count
        median = [Math]::Round((& $pick 0.5), 2)
        p95 = [Math]::Round((& $pick 0.95), 2)
        min = [Math]::Round($v[0], 2)
        max = [Math]::Round($v[-1], 2)
    }
}

function Get-ModeArgs([string]$Mode) {
    switch ($Mode) {
        'off' { @() }
        'devtools' { @('--devtools') }
        'engine' { @('--devtools', '--no-overlay') }
    }
}

#region Apps

function New-BenchApp {
    $stage = Join-Path $work 'bench-app'
    $control = Join-Path $work 'bench-control'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage, $control -Force | Out-Null
    # A .git marker makes the stage its own comment-store root.
    New-Item -ItemType Directory -Path (Join-Path $stage '.git') | Out-Null
    $sample = Join-Path $repoRoot 'samples\winui-app'
    Get-ChildItem $sample -Exclude bin, obj, 'test.Tests.ps1', 'README.md', 'MainWindow.xaml', 'MainWindow.xaml.cs', 'App.xaml.cs' |
        Copy-Item -Destination $stage -Recurse
    Copy-Item (Join-Path $PSScriptRoot 'devtools-perf\bench-app\*') $stage
    Set-Content (Join-Path $stage 'BenchConfig.cs') @"
namespace winui_app;
internal static class BenchConfig { public const string ControlDirectory = @"$control"; }
"@
    # A XAML-authored page with many controls, so XAML parsing and source info are exercised.
    $cards = foreach ($i in 1..120) {
        @"
            <Border Padding="12" CornerRadius="8" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <Grid ColumnSpacing="12">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
                    <StackPanel>
                        <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" Text="Card $i" />
                        <TextBlock Foreground="{ThemeResource TextFillColorSecondaryBrush}" Text="Description for card $i" />
                    </StackPanel>
                    <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8">
                        <CheckBox Content="Option" />
                        <Button Content="Action $i" />
                    </StackPanel>
                </Grid>
            </Border>
"@
    }
    Set-Content (Join-Path $stage 'HeavyPage.xaml') @"
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="winui_app.HeavyPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="8">
$($cards -join "`n")
        </StackPanel>
    </ScrollViewer>
</Page>
"@
    # Control pages for the retention experiments, each isolating one kind of XAML content:
    # Plain (panels and text only), Resource (plain plus theme-resource references), Styled
    # (text blocks with an explicit Style), Button and CheckBox (templated controls).
    $variants = [ordered]@{
        Plain    = { param($i) "<Border Padding=`"12`"><Grid><StackPanel><TextBlock Text=`"Row $i`" /><TextBlock Text=`"Detail $i`" /></StackPanel></Grid></Border>" }
        Resource = { param($i) "<Border Padding=`"12`" Background=`"{ThemeResource CardBackgroundFillColorDefaultBrush}`" BorderBrush=`"{ThemeResource CardStrokeColorDefaultBrush}`"><Grid><StackPanel><TextBlock Text=`"Row $i`" Foreground=`"{ThemeResource TextFillColorSecondaryBrush}`" /><TextBlock Text=`"Detail $i`" Foreground=`"{ThemeResource TextFillColorSecondaryBrush}`" /></StackPanel></Grid></Border>" }
        Styled   = { param($i) "<StackPanel><TextBlock Style=`"{StaticResource BodyStrongTextBlockStyle}`" Text=`"Row $i`" /><TextBlock Style=`"{StaticResource CaptionTextBlockStyle}`" Text=`"Detail $i`" /></StackPanel>" }
        Button   = { param($i) "<Button Content=`"Action $i`" />" }
        CheckBox = { param($i) "<CheckBox Content=`"Option $i`" />" }
    }
    foreach ($variant in $variants.Keys) {
        $rows = foreach ($i in 1..240) { '            ' + (& $variants[$variant] $i) }
        Set-Content (Join-Path $stage "${variant}Page.xaml") @"
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="winui_app.${variant}Page"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="8">
$($rows -join "`n")
        </StackPanel>
    </ScrollViewer>
</Page>
"@
    }
    $project = Join-Path $stage 'winui-app.csproj'
    $xml = [Xml.Linq.XDocument]::Load($project)
    $xml.Root.Element('PropertyGroup').Add([Xml.Linq.XElement]::new('AssemblyName', 'DevToolsPerfBench'))
    $xml.Save($project)
    $manifest = Join-Path $stage 'Package.appxmanifest'
    $xml = [Xml.Linq.XDocument]::Load($manifest)
    $ns = $xml.Root.Name.Namespace
    $xml.Root.Element($ns + 'Identity').SetAttributeValue('Name', 'WinApp.DevToolsPerf.Bench')
    $xml.Save($manifest)
    [pscustomobject]@{
        Name = 'bench'; Target = $stage; ProcessName = 'DevToolsPerfBench'; Control = $control
        Properties = @(); ExtraArgs = @(); Probe = $true
    }
}

function Get-ExternalApp([string]$Name) {
    switch ($Name) {
        'gallery' {
            if (-not $GalleryPath) { throw '-GalleryPath is required for the gallery app.' }
            [pscustomobject]@{
                Name = 'gallery'; Target = (Join-Path $GalleryPath 'WinUIGallery'); ProcessName = 'WinUIGallery'
                Properties = $GalleryProperty; Probe = $false; Root = $GalleryPath
            }
        }
        'aidev' {
            if (-not $AiDevGalleryPath) { throw '-AiDevGalleryPath is required for the aidev app.' }
            [pscustomobject]@{
                Name = 'aidev'; Target = (Join-Path $AiDevGalleryPath 'AIDevGallery'); ProcessName = 'AIDevGallery'
                Properties = $AiDevGalleryProperty; Probe = $false; Root = $AiDevGalleryPath
            }
        }
    }
}

# Snapshot .winapp folders under an external repo so the run leaves them exactly as found.
function Save-WinappState($App) {
    $state = @{}
    Get-ChildItem $App.Root -Recurse -Directory -Force -Filter '.winapp' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } | ForEach-Object {
            $backup = Join-Path $work ("winapp-state-" + [Guid]::NewGuid().ToString('N'))
            Copy-Item $_.FullName $backup -Recurse
            $state[$_.FullName] = @{
                Backup = $backup
                Hashes = @(Get-ChildItem $_.FullName -Recurse -File -Force | ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" })
            }
        }
    $state
}

function Restore-WinappState($App, $State) {
    Get-ChildItem $App.Root -Recurse -Directory -Force -Filter '.winapp' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } | ForEach-Object {
            if (-not $State.ContainsKey($_.FullName)) {
                Write-Step "Removing $($_.FullName) created by this run"
                Remove-Item $_.FullName -Recurse -Force
            }
        }
    foreach ($path in $State.Keys) {
        $entry = $State[$path]
        $now = @(Get-ChildItem $path -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" })
        if (Compare-Object $entry.Hashes $now) {
            Write-Step "Restoring $path to its original contents"
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item $entry.Backup $path -Recurse
        }
        Remove-Item $entry.Backup -Recurse -Force
    }
}

#endregion

#region Launch and probes

function Invoke-Winapp([string[]]$Arguments) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Winapp @Arguments 2>&1
    $clock.Stop()
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Ms = $clock.Elapsed.TotalMilliseconds; Output = ($out | Out-String) }
}

function Start-App($App, [string]$Mode, [switch]$Build) {
    if ($App.Probe) { Get-ChildItem $App.Control -File | Remove-Item -Force }
    $aliasLaunch = $Mode -in 'alias', 'sourceinfo'
    $arguments = @('run', $App.Target) + (Get-ModeArgs $Mode)
    if (-not $aliasLaunch) { $arguments += @('--detach', '--json') }
    if (-not $Build) { $arguments += '--no-build' }
    foreach ($p in $App.Properties) { $arguments += @('-p', $p) }
    $t0 = [DateTime]::UtcNow
    $watch = [DtpNative]::WatchForWindow($App.ProcessName, $t0.AddSeconds(-1), 180000, 10000)
    $runMs = $null
    $runner = $null
    if ($aliasLaunch) {
        # --with-alias cannot detach: keep winapp attached in the background until the app exits.
        # The child inherits this environment, which is how 'sourceinfo' sets the XAML flag alone.
        if ($Mode -eq 'sourceinfo') { $env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO = '1' }
        try {
            $runner = Start-Process -FilePath $Winapp -ArgumentList $arguments -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $work 'alias-run.out') -RedirectStandardError (Join-Path $work 'alias-run.err')
        }
        finally { Remove-Item env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO -ErrorAction SilentlyContinue }
    }
    else {
        $run = Invoke-Winapp $arguments
        if ($run.ExitCode -ne 0) { throw "winapp $($arguments -join ' ') failed ($($run.ExitCode)):`n$($run.Output)" }
        $runMs = [Math]::Round($run.Ms, 1)
    }
    $hit = $watch.GetAwaiter().GetResult()
    if (-not $hit) { throw "No window for $($App.ProcessName) appeared after launch." }
    $process = Get-Process -Id $hit.Pid
    $start = $process.StartTime.ToUniversalTime()
    [DtpNative]::Place($hit.Hwnd, 40, 40, 1280, 800)
    $settled = $hit.SettledUtc
    $launch = [ordered]@{
        pid = $hit.Pid; hwnd = $hit.Hwnd; mode = $Mode; runnerPid = if ($runner) { $runner.Id } else { $null }
        runMs = $runMs
        launchToProcessMs = [Math]::Round(($start - $t0).TotalMilliseconds, 1)
        processToWindowMs = [Math]::Round(($hit.VisibleUtc - $start).TotalMilliseconds, 1)
        launchToWindowMs = [Math]::Round(($hit.VisibleUtc - $t0).TotalMilliseconds, 1)
        launchToSettledMs = if ($settled) { [Math]::Round(([DateTime]$settled - $t0).TotalMilliseconds, 1) } else { $null }
        windowToSettledMs = if ($settled) { [Math]::Round(([DateTime]$settled - $hit.VisibleUtc).TotalMilliseconds, 1) } else { $null }
        startupMaxPingMs = [Math]::Round($hit.MaxPingMs, 1)
        startupPingsOver50ms = $hit.PingsOver50Ms
    }
    if ($App.Probe) {
        $startup = Wait-BenchFile $App 'startup.json' 60
        $launch.processToFirstFrameMs = [Math]::Round($startup.firstFrameMs, 1)
        $launch.refreshMs = $startup.refreshMs
    }
    $launch
}

function Stop-App($App, $Launch) {
    if (-not $Launch) { return }
    $process = Get-Process -Id $Launch.pid -ErrorAction SilentlyContinue
    if (-not $process) { return }
    if ($App.Probe) {
        try { Invoke-Bench $App @{ name = 'exit' } 15 | Out-Null } catch { Write-Verbose "exit command: $_" }
    }
    else { $null = $process.CloseMainWindow() }
    if (-not $process.WaitForExit(15000)) { Stop-Process -Id $Launch.pid -Force -ErrorAction SilentlyContinue }
    if ($Launch.runnerPid) {
        $runner = Get-Process -Id $Launch.runnerPid -ErrorAction SilentlyContinue
        if ($runner -and -not $runner.WaitForExit(15000)) { Stop-Process -Id $Launch.runnerPid -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 2
}

function Wait-BenchFile($App, [string]$Name, [int]$TimeoutSeconds) {
    $path = Join-Path $App.Control $Name
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path $path)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $path" }
        Start-Sleep -Milliseconds 50
    }
    Get-Content -Raw $path | ConvertFrom-Json
}

$script:benchId = 0
function Invoke-Bench($App, [hashtable]$Command, [int]$TimeoutSeconds = 300) {
    $script:benchId++
    $Command.id = $script:benchId
    $path = Join-Path $App.Control 'cmd.json'
    Set-Content "$path.tmp" ($Command | ConvertTo-Json -Compress)
    Move-Item "$path.tmp" $path -Force
    $result = Wait-BenchFile $App "res-$($script:benchId).json" $TimeoutSeconds
    if ($result.error) { throw "Bench command $($Command.name) failed: $($result.error)" }
    $result
}

function Get-Memory([int]$ProcessId) {
    $p = Get-Process -Id $ProcessId
    $p.Refresh()
    [ordered]@{ privateMB = [Math]::Round($p.PrivateMemorySize64 / 1MB, 1); workingSetMB = [Math]::Round($p.WorkingSet64 / 1MB, 1) }
}

function Measure-Idle([int]$ProcessId, [int]$Seconds) {
    $p = Get-Process -Id $ProcessId
    $cpu0 = $p.TotalProcessorTime
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $p.Refresh()
    $cpu = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds / $clock.Elapsed.TotalMilliseconds * 100
    $m = Get-Memory $ProcessId
    [ordered]@{ cpuPercentOfOneCore = [Math]::Round($cpu, 3); privateMB = $m.privateMB; workingSetMB = $m.workingSetMB }
}

#endregion

#region DevTools protocol

class TapClient {
    [IO.Pipes.NamedPipeClientStream]$Pipe
    [IO.StreamReader]$Reader
    [IO.StreamWriter]$Writer
    [int]$Id = 0

    TapClient([int]$ProcessId) {
        $this.Pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "winapp-devtools-$ProcessId", [IO.Pipes.PipeDirection]::InOut)
        $this.Pipe.Connect(10000)
        $utf8 = [Text.UTF8Encoding]::new($false)
        $this.Reader = [IO.StreamReader]::new($this.Pipe, $utf8)
        $this.Writer = [IO.StreamWriter]::new($this.Pipe, $utf8)
        $this.Writer.AutoFlush = $true
        $null = $this.Call('DevTools.negotiate', $null)
    }

    [object] Call([string]$Method, [hashtable]$Params) {
        $this.Id++
        $message = [ordered]@{ jsonrpc = '2.0'; id = $this.Id; method = $Method }
        if ($Params) { $message.params = $Params }
        $this.Writer.WriteLine(($message | ConvertTo-Json -Compress -Depth 10))
        while ($true) {
            $line = $this.Reader.ReadLine()
            if ($null -eq $line) { throw "DevTools pipe closed during $Method" }
            $reply = $line | ConvertFrom-Json
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $this.Id) {
                if ($reply.PSObject.Properties['error']) { throw "$Method failed: $($reply.error | ConvertTo-Json -Compress -Depth 5)" }
                return $reply.result
            }
        }
        return $null
    }

    [double] Time([string]$Method, [hashtable]$Params) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $null = $this.Call($Method, $Params)
        return $clock.Elapsed.TotalMilliseconds
    }

    [void] Dispose() { $this.Pipe.Dispose() }
}

function Get-PerfSites($Report) {
    $sites = [ordered]@{}
    foreach ($s in @($Report.sites | Where-Object { $_.calls -gt 0 } | Sort-Object totalUs -Descending)) {
        $sites[$s.site] = [ordered]@{ calls = $s.calls; totalMs = [Math]::Round($s.totalUs / 1000, 2); meanMs = [Math]::Round($s.meanUs / 1000, 3); maxMs = [Math]::Round($s.maxUs / 1000, 2) }
    }
    [ordered]@{ jank = $Report.jank; sites = $sites }
}

function Find-Handles([int]$ProcessId, [string[]]$Queries, [int]$Count) {
    $handles = @()
    foreach ($q in $Queries) {
        $r = Invoke-Winapp @('devtools', 'search', $q, '-a', "$ProcessId", '--json')
        if ($r.ExitCode -ne 0) { continue }
        try { $json = $r.Output | ConvertFrom-Json } catch { continue }
        $handles += @($json.matches | ForEach-Object { $_.handle })
        if ($handles.Count -ge $Count) { break }
    }
    @($handles | Select-Object -First $Count)
}

# Cost of the things a developer does in DevTools, measured on the app's UI thread by the engine's
# own instrumentation (Internal.perf) and as protocol round trips from this script.
function Measure-Interaction($App, $Launch) {
    $appPid = $Launch.pid
    $queries = if ($App.Probe) { @('SubmitButton', 'CounterButton', 'PageTitle') } else { @('Button', 'TextBlock') }
    $handles = Find-Handles $appPid $queries 3
    if ($handles.Count -eq 0) { return [ordered]@{ error = 'No elements found to interact with.' } }
    $tap = [TapClient]::new($appPid)
    try {
        $result = [ordered]@{}
        $null = $tap.Call('Internal.perf', @{ action = 'reset' })

        # Hover highlight
        $null = $tap.Call('Internal.perf', @{ action = 'start' })
        $rt = foreach ($i in 1..10) { foreach ($h in $handles) { $tap.Time('Overlay.highlight', @{ handle = $h }); Start-Sleep -Milliseconds 50 } }
        $null = $tap.Call('Internal.perf', @{ action = 'stop' })
        $result.highlight = [ordered]@{ roundTripMs = Get-Stats $rt; ui = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' })) }

        # Pick (hit test at the window centre)
        $rect = [DtpNative]::WindowRect($Launch.hwnd)
        $cx = [int](($rect[0] + $rect[2]) / 2); $cy = [int](($rect[1] + $rect[3]) / 2)
        $null = $tap.Call('Internal.perf', @{ action = 'reset' })
        $null = $tap.Call('Internal.perf', @{ action = 'start' })
        try {
            $rt = foreach ($i in 1..20) { $tap.Time('Internal.pick', @{ x = $cx + $i; y = $cy }); Start-Sleep -Milliseconds 20 }
            $null = $tap.Call('Internal.perf', @{ action = 'stop' })
            $result.pick = [ordered]@{ roundTripMs = Get-Stats $rt; ui = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' })) }
        }
        catch { $null = $tap.Call('Internal.perf', @{ action = 'stop' }); $result.pick = [ordered]@{ error = "$_" } }

        # Open the inspector and select an element
        $before = Get-Memory $appPid
        $null = $tap.Call('Internal.perf', @{ action = 'reset' })
        $null = $tap.Call('Internal.perf', @{ action = 'start' })
        $open = $tap.Time('Window.open', @{})
        Start-Sleep -Seconds 2
        $select = $tap.Time('Selection.select', @{ handle = $handles[0] })
        Start-Sleep -Seconds 2
        $null = $tap.Call('Internal.perf', @{ action = 'stop' })
        $ui = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' }))
        Start-Sleep -Seconds 3
        $after = Get-Memory $appPid
        $result.inspector = [ordered]@{
            openRoundTripMs = [Math]::Round($open, 1); selectRoundTripMs = [Math]::Round($select, 1)
            privateDeltaMB = [Math]::Round($after.privateMB - $before.privateMB, 1)
            workingSetDeltaMB = [Math]::Round($after.workingSetMB - $before.workingSetMB, 1)
            ui = $ui
        }
        $result.inspectorIdle = Measure-Idle $appPid ([Math]::Min(30, $IdleSeconds))

        # Live property edits with the inspector open on the edited element
        $null = $tap.Call('Internal.perf', @{ action = 'reset' })
        $null = $tap.Call('Internal.perf', @{ action = 'start' })
        $rt = foreach ($i in 1..20) {
            $tap.Time('HotReload.setProperty', @{ handle = $handles[0]; prop = 'Opacity'; type = 'Double'; value = "$(0.5 + ($i % 2) * 0.4)" })
            Start-Sleep -Milliseconds 50
        }
        try { $null = $tap.Call('HotReload.clearProperty', @{ handle = $handles[0]; prop = 'Opacity' }) } catch { Write-Verbose "$_" }
        $null = $tap.Call('Internal.perf', @{ action = 'stop' })
        $result.propertyEdit = [ordered]@{ roundTripMs = Get-Stats $rt; ui = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' })) }
        $null = $tap.Call('Window.close', @{})
        $result
    }
    finally { $tap.Dispose() }
}

# Comment save end to end (CLI capture + store write + overlay refresh). Bench app only: it owns its store.
function Measure-CommentSave($App, $Launch, [int]$Count = 5) {
    $tap = [TapClient]::new($Launch.pid)
    try {
        $null = $tap.Call('Internal.perf', @{ action = 'reset' })
        $null = $tap.Call('Internal.perf', @{ action = 'start' })
        $names = @('SubmitButton', 'CounterButton', 'PageTitle', 'InputTextBox', 'FeatureCheckBox')
        $ms = foreach ($i in 1..$Count) {
            $r = Invoke-Winapp @('devtools', 'comments', 'add', '--from-element', $names[($i - 1) % $names.Count], '-a', "$($Launch.pid)",
                '--text', "perf comment $i", '--source-root', $App.Target, '--json')
            if ($r.ExitCode -ne 0) { throw "comments add failed: $($r.Output)" }
            $r.Ms
        }
        Start-Sleep -Seconds 1
        $null = $tap.Call('Internal.perf', @{ action = 'stop' })
        [ordered]@{ cliMs = Get-Stats $ms; ui = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' })) }
    }
    finally { $tap.Dispose() }
}

# Wraps scenarios in the engine's UI-thread instrumentation. What the engine's own sites account
# for is DevTools code; the rest of the measured delta is WinUI's diagnostics work on its behalf.
function Measure-Attribution($App, $Launch) {
    $tap = [TapClient]::new($Launch.pid)
    try {
        $measure = {
            param([scriptblock]$Body)
            $null = $tap.Call('Internal.perf', @{ action = 'reset' })
            $null = $tap.Call('Internal.perf', @{ action = 'start' })
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $inner = & $Body
            $wall = $clock.Elapsed.TotalMilliseconds
            $null = $tap.Call('Internal.perf', @{ action = 'stop' })
            [ordered]@{ wallMs = [Math]::Round($wall, 1); scenario = $inner; engine = Get-PerfSites ($tap.Call('Internal.perf', @{ action = 'report' })) }
        }
        $result = [ordered]@{}
        $result.idle = & $measure { Start-Sleep -Seconds 10; $null }
        if ($App.Probe) {
            $result.scroll = & $measure { Invoke-Bench $App @{ name = 'scroll'; seconds = $ScrollSeconds; px = 12 } }
            $result.churn = & $measure { Invoke-Bench $App @{ name = 'churn'; count = $ChurnElements; cycles = $ChurnCycles; rounds = 1; visible = $false } }
            $result.nav = & $measure { Invoke-Bench $App @{ name = 'nav'; cycles = 10; warmup = 1 } }
        }
        else {
            $result.navWalk = & $measure { Measure-NavWalk $App $Launch ([Math]::Min(30, $GalleryPages)) 3 }
        }
        $result
    }
    finally { $tap.Dispose() }
}

function Set-CommentStore($App, [int]$Count) {
    $store = Join-Path $App.Target '.winapp\ui-comments.json'
    if ($Count -eq 0) { Remove-Item $store -ErrorAction SilentlyContinue; return }
    $seed = Get-Content -Raw $store | ConvertFrom-Json
    $templates = @($seed.comments)
    $comments = foreach ($i in 0..($Count - 1)) {
        $c = $templates[$i % $templates.Count] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $c.id = 'cmt_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $c.text = "stress comment $i"
        $c
    }
    $seed.comments = @($comments)
    Set-Content $store ($seed | ConvertTo-Json -Depth 20)
}

#endregion

#region Real-app navigation (UI Automation, no input)

$navItemClass = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, 'Microsoft.UI.Xaml.Controls.NavigationViewItem')

function Find-NavItem($Scope, [string]$Name) {
    $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new($navItemClass,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name)))
}

function Get-ExpandPattern($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern) -and
        $pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::LeafNode) { return $pattern }
    $null
}

# Navigation targets in NavigationView order: top-level leaves, then each category's children.
# Categories are expanded one at a time because the menu virtualizes off-screen items.
function Get-NavTargets($Root, [int]$Max) {
    $targets = [Collections.Generic.List[object]]::new()
    $top = @($Root.FindAll([Windows.Automation.TreeScope]::Descendants, $navItemClass) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    foreach ($name in $top) {
        if ($targets.Count -ge $Max) { break }
        $item = Find-NavItem $Root $name
        if (-not $item) { continue }
        $expand = Get-ExpandPattern $item
        if (-not $expand) { if ($name -notin $NavExclude) { $targets.Add([pscustomobject]@{ Category = $null; Name = $name }) }; continue }
        $expand.Expand(); Start-Sleep -Milliseconds 400
        foreach ($child in @($item.FindAll([Windows.Automation.TreeScope]::Descendants, $navItemClass))) {
            if ($targets.Count -ge $Max) { break }
            if ($child.Current.Name -and $child.Current.Name -notin $NavExclude) { $targets.Add([pscustomobject]@{ Category = $name; Name = $child.Current.Name }) }
        }
        $expand.Collapse(); Start-Sleep -Milliseconds 200
    }
    @($targets)
}

$script:openCategory = $null
function Invoke-NavTarget($Root, $Target) {
    $scope = $Root
    if ($Target.Category) {
        $category = Find-NavItem $Root $Target.Category
        if (-not $category) { return $false }
        if ($script:openCategory -and $script:openCategory -ne $Target.Category) {
            $previous = Find-NavItem $Root $script:openCategory
            if ($previous -and ($p = Get-ExpandPattern $previous)) { $p.Collapse(); Start-Sleep -Milliseconds 150 }
        }
        if ($p = Get-ExpandPattern $category) { $p.Expand(); Start-Sleep -Milliseconds 300 }
        $script:openCategory = $Target.Category
        $scope = $category
    }
    $item = Find-NavItem $scope $Target.Name
    if (-not $item) { return $false }
    $pattern = $null
    if ($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return $true }
    if ($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return $true }
    $false
}

# Visits up to $Pages distinct pages, then loops between the first page and one from the middle of
# the walk. Memory is sampled after the walk and after each away-and-back loop; the UI thread is
# pinged throughout.
function Measure-NavWalk($App, $Launch, [int]$Pages, [int]$Loops) {
    $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Launch.hwnd)
    $script:openCategory = $null
    $targets = Get-NavTargets $root $Pages
    if ($targets.Count -lt 2) { return [ordered]@{ error = "Found only $($targets.Count) navigation items." } }
    $start = Get-Memory $Launch.pid
    $recorder = [DtpNative+PingRecorder]::new($Launch.hwnd, 16, 2000)
    $visited = 0
    foreach ($target in $targets) {
        if (Invoke-NavTarget $root $target) { $visited++ }
        Start-Sleep -Milliseconds 600
    }
    $walkPings = $recorder.Stop()
    Start-Sleep -Seconds 3
    $afterWalk = Get-Memory $Launch.pid
    $homePage = $targets[0]
    $heavy = $targets[[Math]::Min($targets.Count - 1, [int]($targets.Count / 2))]
    $loopMem = @()
    $recorder = [DtpNative+PingRecorder]::new($Launch.hwnd, 16, 2000)
    foreach ($i in 1..$Loops) {
        $null = Invoke-NavTarget $root $heavy; Start-Sleep -Milliseconds 800
        $null = Invoke-NavTarget $root $homePage; Start-Sleep -Milliseconds 800
        $loopMem += (Get-Memory $Launch.pid).privateMB
    }
    $loopPings = $recorder.Stop()
    $slope = if ($loopMem.Count -gt 2) { ($loopMem[-1] - $loopMem[1]) / ($loopMem.Count - 2) } else { $null }
    [ordered]@{
        pagesVisited = $visited
        start = $start; afterWalk = $afterWalk
        walkGrowthMB = [Math]::Round($afterWalk.privateMB - $start.privateMB, 1)
        walkPingMs = Get-Stats $walkPings
        walkPingsOver50ms = @($walkPings | Where-Object { $_ -gt 50 }).Count
        loopPage = "$($heavy.Category)/$($heavy.Name)"
        loopPrivateMB = $loopMem
        loopGrowthMBPerCycle = if ($null -ne $slope) { [Math]::Round($slope, 2) } else { $null }
        loopPingMs = Get-Stats $loopPings
        loopPingsOver50ms = @($loopPings | Where-Object { $_ -gt 50 }).Count
    }
}

#endregion

#region Run

$sha = (git -C $repoRoot rev-parse HEAD).Trim()
$winappVersion = (& $Winapp --version | Where-Object { $_ -match '^\d+\.\d+' } | Select-Object -First 1)
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$results = [ordered]@{
    schema = 1
    startedUtc = [DateTime]::UtcNow.ToString('o')
    environment = [ordered]@{
        commit = $sha; winapp = $winappVersion; machine = $env:COMPUTERNAME; os = "$($os.Caption) $($os.Version)"
        cpu = $cpu; logicalCores = $cores; memoryGB = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        ci = [bool]$env:GITHUB_ACTIONS
    }
    parameters = [ordered]@{
        apps = $Apps; modes = $Modes; repetitions = $Repetitions; idleSeconds = $IdleSeconds; settleSeconds = $SettleSeconds
        scrollSeconds = $ScrollSeconds; navCycles = $NavCycles; bigTreeItems = $BigTreeItems; stressComments = $StressComments; churnElements = $ChurnElements; churnCycles = $ChurnCycles; churnRounds = $ChurnRounds
        soakMinutes = $SoakMinutes; galleryPages = $GalleryPages; awayAndBackLoops = $AwayAndBackLoops; window = '1280x800'; start = 'warm'
    }
    apps = [ordered]@{}
}
$resultsPath = Join-Path $OutDir 'results.json'
function Save-Results { Set-Content $resultsPath ($results | ConvertTo-Json -Depth 30) }

foreach ($appName in $Apps) {
    $app = if ($appName -eq 'bench') { New-BenchApp } else { Get-ExternalApp $appName }
    if ($PrepareOnly) {
        # Stage and build the bench app (one plain launch registers its package), then stop.
        if ($app.Probe) { Stop-App $app (Start-App $app 'off' -Build) }
        continue
    }
    $state = if ($app.Root) { Save-WinappState $app } else { $null }
    $appResult = [ordered]@{ target = $app.Target; build = [ordered]@{}; runs = [ordered]@{}; stress = [ordered]@{}; errors = @() }
    foreach ($m in $Modes) { $appResult.runs[$m] = @() }
    $results.apps[$appName] = $appResult
    try {
      if (-not $SoakOnly) {
        # Build cost: the first run in each mode builds; later warm runs measure the incremental
        # build plus, with --devtools, the source inventory capture. Alias modes only warm up.
        foreach ($m in $Modes) {
            $times = @()
            $count = if ($m -in 'alias', 'sourceinfo') { 0 } else { $BuildRepetitions }
            foreach ($i in 0..$count) {
                Write-Step "$appName/$m build run $i"
                $launch = Start-App $app $m -Build
                Stop-App $app $launch
                if ($i -gt 0) { $times += $launch.runMs }
            }
            if ($count -gt 0) { $appResult.build[$m] = [ordered]@{ runWithBuildMs = Get-Stats $times } }
        }
        Save-Results

        for ($rep = 0; $rep -lt $Repetitions; $rep++) {
            $order = for ($k = 0; $k -lt $Modes.Count; $k++) { $Modes[($k + $rep) % $Modes.Count] }
            foreach ($m in $order) {
                Write-Step "$appName/$m repetition $($rep + 1)/$Repetitions"
                $launch = $null
                try {
                    $launch = Start-App $app $m
                    $run = [ordered]@{ startup = $launch }
                    if ($StartupOnly) { $appResult.runs[$m] += $run; continue }
                    Start-Sleep -Seconds $SettleSeconds
                    $run.idle = Measure-Idle $launch.pid $IdleSeconds
                    if ($app.Probe) {
                        $run.scroll = Invoke-Bench $app @{ name = 'scroll'; seconds = $ScrollSeconds; px = 12 }
                        $run.churn = Invoke-Bench $app @{ name = 'churn'; count = $ChurnElements; cycles = $ChurnCycles; rounds = $ChurnRounds; visible = $false }
                        $run.churnVisible = Invoke-Bench $app @{ name = 'churn'; count = $ChurnElements; cycles = $ChurnCycles; rounds = $ChurnRounds; visible = $true }
                        $run.nav = Invoke-Bench $app @{ name = 'nav'; cycles = $NavCycles; warmup = 3 }
                        $run.bigtree = @(foreach ($n in $BigTreeItems) { Invoke-Bench $app @{ name = 'bigtree'; count = $n } })
                        $run.afterScenarios = Invoke-Bench $app @{ name = 'home' }
                    }
                    else {
                        $run.navWalk = Measure-NavWalk $app $launch $GalleryPages $AwayAndBackLoops
                    }
                    $appResult.runs[$m] += $run
                }
                catch {
                    # One bad launch (for example the app crashing on a page) must not end the whole benchmark.
                    Write-Step "$appName/$m repetition $($rep + 1) failed: $_"
                    $appResult.errors += "$m repetition $($rep + 1): $_"
                }
                finally { Stop-App $app $launch }
                Save-Results
            }
        }

        # Interaction cost and attribution on fresh launches, so memory deltas are not muddied by the scenarios above.
        foreach ($m in @('devtools', 'engine')) {
            if ($StartupOnly -or $Modes -notcontains $m) { continue }
            Write-Step "$appName/$m interaction and attribution"
            $launch = $null
            try {
                $launch = Start-App $app $m
                Start-Sleep -Seconds $SettleSeconds
                $entry = [ordered]@{ startup = $launch }
                if ($m -eq 'devtools') {
                    $entry.interaction = Measure-Interaction $app $launch
                    if ($app.Probe) { $entry.commentSave = Measure-CommentSave $app $launch }
                }
                $entry.attribution = Measure-Attribution $app $launch
                $appResult["interaction-$m"] = $entry
            }
            catch {
                Write-Step "$appName/$m interaction failed: $_"
                $appResult.errors += "$m interaction: $_"
            }
            finally { Stop-App $app $launch }
            Save-Results
        }

        if ($app.Probe -and $StressComments -gt 0 -and $Modes -contains 'devtools' -and -not $StartupOnly) {
            Write-Step "$appName comment store stress ($StressComments comments)"
            if (-not (Test-Path (Join-Path $app.Target '.winapp\ui-comments.json'))) {
                $launch = Start-App $app 'devtools'; try { $null = Measure-CommentSave $app $launch } finally { Stop-App $app $launch }
            }
            Set-CommentStore $app $StressComments
            $stress = @()
            foreach ($i in 1..$Repetitions) {
                $launch = Start-App $app 'devtools'
                try {
                    Start-Sleep -Seconds $SettleSeconds
                    $entry = [ordered]@{ startup = $launch; idle = Measure-Idle $launch.pid ([Math]::Min(30, $IdleSeconds)) }
                    if ($i -eq 1) {
                        $entry.commentSave = Measure-CommentSave $app $launch 3
                        $clock = [Diagnostics.Stopwatch]::StartNew()
                        $null = Invoke-Winapp @('devtools', 'comments', 'list', '--source-root', $app.Target, '--json')
                        $entry.listMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 1)
                        $entry.nav = Invoke-Bench $app @{ name = 'nav'; cycles = 10; warmup = 2 }
                    }
                    $stress += $entry
                }
                finally { Stop-App $app $launch }
            }
            $appResult.stress.comments = [ordered]@{ count = $StressComments; runs = $stress }
            Set-CommentStore $app 0
            Save-Results
        }

      }
        if ($app.Probe -and $SoakMinutes -gt 0 -and -not $StartupOnly) {
            if ($SoakOnly) { Stop-App $app (Start-App $app 'off' -Build) }
            foreach ($m in @('off', 'devtools')) {
                if ($Modes -notcontains $m) { continue }
                Write-Step "$appName/$m soak for $SoakMinutes minutes"
                $launch = Start-App $app $m
                $samples = @()
                try {
                    $p = Get-Process -Id $launch.pid
                    $cpu0 = $p.TotalProcessorTime
                    $end = (Get-Date).AddMinutes($SoakMinutes)
                    $minute = 0
                    while ((Get-Date) -lt $end) {
                        $nav = Invoke-Bench $app @{ name = 'nav'; cycles = 5; warmup = 0 }
                        $scroll = Invoke-Bench $app @{ name = 'scroll'; seconds = 3; px = 12 }
                        $minute++
                        $samples += [ordered]@{
                            cycle = $minute; privateMB = $nav.after.privateMB; workingSetMB = $nav.after.workingSetMB; managedMB = $nav.after.managedMB
                            navP95Ms = $nav.navMs.p95; scrollMissedFrames = $scroll.missedFrames
                        }
                        Start-Sleep -Seconds 20
                    }
                    $p.Refresh()
                    $cpuSeconds = ($p.TotalProcessorTime - $cpu0).TotalSeconds
                }
                finally { Stop-App $app $launch }
                $appResult.stress["soak-$m"] = [ordered]@{ minutes = $SoakMinutes; cpuSeconds = [Math]::Round($cpuSeconds, 1); samples = $samples }
                Save-Results
            }
        }
    }
    finally {
        if ($state) { Restore-WinappState $app $state }
    }
}
if ($PrepareOnly) { return }
$results.finishedUtc = [DateTime]::UtcNow.ToString('o')
Save-Results

& (Join-Path $PSScriptRoot 'devtools-perf\summarize.ps1') -Results $resultsPath -Budgets $Budgets -Out (Join-Path $OutDir 'summary.md')
Get-Content (Join-Path $OutDir 'summary.md')

#endregion
