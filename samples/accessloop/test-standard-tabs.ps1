# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WinappPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('standard', 'nameless', 'nested', 'edges', 'no-tabs')][string]$Variant = 'standard'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$winapp = (Resolve-Path -LiteralPath $WinappPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new output directory: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$source = Join-Path $output 'app'

function Copy-DemoSource([string]$From, [string]$To) {
    New-Item -ItemType Directory -Path $To | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $From) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Fixture source must not contain links: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('bin', 'obj')) {
                Copy-DemoSource $item.FullName (Join-Path $To $item.Name)
            }
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $To $item.Name)
        }
    }
}

Copy-DemoSource (Join-Path $PSScriptRoot 'StateDemo') $source
$xamlPath = Join-Path $source 'MainWindow.xaml'
$document = [Xml.Linq.XDocument]::Load($xamlPath)
$testTitle = 'AccessLoop audit fixture ' + [Guid]::NewGuid().ToString('N')
$document.Root.SetAttributeValue('Title', $testTitle)
$ns = [Xml.Linq.XNamespace]'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
$groups = @($document.Descendants($ns + 'TabView'))
if ($groups.Count -ne 1) { throw 'The fixture must start with exactly one TabView.' }
$tabs = $groups[0]
$items = $tabs.Element($ns + 'TabView.TabItems')

if ($Variant -eq 'no-tabs') {
    $appCode = Join-Path $source 'App.xaml.cs'
    $text = [IO.File]::ReadAllText($appCode)
    if (-not $text.Contains('window = new MainWindow();')) { throw 'Unexpected fixture launch code.' }
    [IO.File]::WriteAllText($appCode, $text.Replace('window = new MainWindow();', 'window = new ScopeProbeWindow();'))
    $probePath = Join-Path $source 'ScopeProbeWindow.xaml'
    $probe = [Xml.Linq.XDocument]::Load($probePath)
    $probe.Root.SetAttributeValue('Title', $testTitle)
    $probe.Save($probePath)
}

if ($Variant -eq 'nameless') {
    foreach ($element in @($tabs) + @($items.Elements($ns + 'TabViewItem'))) {
        foreach ($name in @('AutomationProperties.AutomationId', 'AutomationProperties.Name')) {
            $attribute = $element.Attribute($name)
            if ($attribute) { $attribute.Remove() }
        }
    }
}
if ($Variant -in @('nested', 'edges')) {
    $nested = [Xml.Linq.XElement]::Parse(@'
<TabViewItem xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    Header="Nested" IsClosable="False" AutomationProperties.AutomationId="TestNestedPage">
  <TabView AutomationProperties.AutomationId="TestNestedGroup" IsAddTabButtonVisible="False"
      CanDragTabs="False" CanReorderTabs="False" Height="250" VerticalAlignment="Top"
      Loaded="TestNestedTabs_Loaded">
    <TabView.TabItems>
      <TabViewItem Header="First" IsClosable="False" AutomationProperties.AutomationId="TestNestedFirst">
        <TextBlock Text="First nested page" AutomationProperties.AutomationId="TestNestedFirstReady" />
      </TabViewItem>
      <TabViewItem Header="Second" IsClosable="False" AutomationProperties.AutomationId="TestNestedSecond">
        <TextBlock Text="Second nested page" AutomationProperties.AutomationId="TestNestedSecondReady" />
      </TabViewItem>
      <TabViewItem Header="Disabled" IsClosable="False" IsEnabled="False"
          AutomationProperties.AutomationId="TestNestedDisabled">
        <TextBlock Text="This disabled page must not be selected." />
      </TabViewItem>
    </TabView.TabItems>
  </TabView>
</TabViewItem>
'@)
    $items.Add($nested)
    [IO.File]::WriteAllText((Join-Path $source 'TabFixture.cs'), @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.StateDemo;

public sealed partial class MainWindow
{
    private void TestNestedTabs_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (TabViewItem tab in ((TabView)sender).TabItems)
        {
            ToolTipService.SetToolTip(tab, null);
        }
    }
}
'@)
    $overflowTabs = if ($Variant -eq 'edges') { 1..6 } else { @() }
    foreach ($index in $overflowTabs) {
        $overflow = [Xml.Linq.XElement]::new($ns + 'TabViewItem')
        $overflow.SetAttributeValue('Header', "Overflow page $index with a long visible label")
        $overflow.SetAttributeValue('IsClosable', 'False')
        $overflow.SetAttributeValue('AutomationProperties.AutomationId', "TestOverflow$index")
        $content = [Xml.Linq.XElement]::new($ns + 'TextBlock')
        $content.SetAttributeValue('Text', "Overflow content $index")
        $overflow.Add($content)
        $items.Add($overflow)
    }
}
$document.Save($xamlPath)

& dotnet build (Join-Path $source 'AccessLoop.StateDemo.csproj') -c Debug -r win-x64 -v quiet *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Fixture build failed. Read $output\build.log" }
$exe = Join-Path $source 'bin\Debug\net10.0-windows10.0.26100.0\win-x64\AccessLoop.StateDemo.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Fixture executable not found: $exe" }

function Read-Tabs([long]$Window, [string]$Name) {
    $stdout = Join-Path $output "$Name.stdout.json"
    $stderr = Join-Path $output "$Name.stderr.txt"
    & $winapp ui audit-tabs --window $Window --json > $stdout 2> $stderr
    $code = $LASTEXITCODE
    $result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
    if ($code -ne 0 -or $result.status -ne 'captured') {
        throw "Tab discovery failed ($code). Read $stdout and $stderr"
    }
    return $result
}

$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw "Owned fixture exited with code $($process.ExitCode)." }
    } while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Owned fixture did not open a window.' }
    Start-Sleep -Milliseconds 1000
    $window = $process.MainWindowHandle.ToInt64()
    $before = Read-Tabs $window 'before'
    $initial = @($before.containers | Where-Object { -not $_.parentContainerRuntimeId })
    $tabless = $Variant -eq 'no-tabs'
    if ($initial.Count -ne $(if ($tabless) { 0 } else { 1 })) { throw 'Unexpected fixture tab-group count.' }
    $initialSelection = if (-not $tabless) { @($initial[0].items | Where-Object isSelected)[0].runtimeId } else { $null }
    if ($Variant -eq 'nameless' -and @($initial[0].items | Where-Object automationId).Count -gt 0) {
        throw 'The no-ID fixture unexpectedly exposed an application tab AutomationId.'
    }

    & $winapp ui audit --output (Join-Path $output 'no-target') *> (Join-Path $output 'no-target.txt')
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath (Join-Path $output 'no-target'))) {
        throw 'An audit without an explicit target was not rejected before creating output.'
    }
    $afterDenied = Read-Tabs $window 'after-no-target'
    if (-not $tabless -and @($afterDenied.containers[0].items | Where-Object isSelected)[0].runtimeId -ne $initialSelection) {
        throw 'The selected tab changed during the invalid command.'
    }

    $passiveDirectory = Join-Path $output 'passive'
    & $winapp ui audit -w $window --current-page --no-screenshot --output $passiveDirectory --json `
        > (Join-Path $output 'passive.stdout.json') 2> (Join-Path $output 'passive.stderr.txt')
    if ($LASTEXITCODE -notin @(0, 1, 2) -or -not (Test-Path -LiteralPath (Join-Path $passiveDirectory 'scan.json'))) {
        throw 'The current-page audit did not produce a report.'
    }
    $passive = Get-Content -LiteralPath (Join-Path $passiveDirectory 'scan.json') -Raw | ConvertFrom-Json
    $afterPassive = Read-Tabs $window 'after-passive'
    if (-not $passive.axe -or $passive.mode -ne 'snapshot' `
        -or (-not $tabless -and @($afterPassive.containers[0].items | Where-Object isSelected)[0].runtimeId -ne $initialSelection)) {
        throw '--current-page did not preserve read-only single-page behavior.'
    }

    $reportDirectory = Join-Path $output 'scan'
    & $winapp ui audit -a $testTitle `
        --max-pages 20 --timeout-ms 20000 --output $reportDirectory --json `
        > (Join-Path $output 'scan.stdout.json') 2> (Join-Path $output 'scan.stderr.txt')
    $exitCode = $LASTEXITCODE
    if ($exitCode -notin @(0, 1, 2)) { throw "Unexpected scan exit code: $exitCode" }
    $report = Get-Content -LiteralPath (Join-Path $reportDirectory 'scan.json') -Raw | ConvertFrom-Json
    if ($report.target.processId -ne $process.Id -or $report.target.windowHandle -ne $window) {
        throw 'App-title resolution did not retain the exact owned process and window.'
    }
    $rootRestoration = if ($tabless) { @() } else {
        @($report.restorations | Where-Object { $_.containerRuntimeId -eq $initial[0].runtimeId })
    }
    $offscreenRestoration = $Variant -eq 'edges' -and $rootRestoration.Count -eq 1 `
        -and $rootRestoration[0].status -eq 'blocked' `
        -and $report.error -like 'The original tab cannot be restored automatically (offscreen)*'
    if ($report.kind -ne 'standard-tabs' -or ($report.error -and -not $offscreenRestoration)) {
        & $winapp ui list-windows --app $process.Id --json `
            > (Join-Path $output 'stopped-windows.json') 2> (Join-Path $output 'stopped-windows.stderr.txt')
        & $winapp ui audit-snapshot --window $window --depth 30 --max-elements 3000 --json `
            > (Join-Path $output 'stopped-snapshot.json') 2> (Join-Path $output 'stopped-snapshot.stderr.txt')
        throw "Automatic tab scan stopped: $($report.error)"
    }
    if ($Variant -in @('standard', 'nameless') -and ($report.capturedPageCount -ne 3 -or $report.navigationStatus -ne 'completed')) {
        throw "Expected all three actual page captures, not $($report.capturedPageCount) / $($report.navigationStatus)."
    }
    if ($tabless -and ($report.capturedPageCount -ne 1 -or $report.navigationStatus -ne 'not-applicable' `
        -or $report.skipped.Count -ne 0 -or $report.restorations.Count -ne 0)) {
        throw 'An app without tabs must receive a current-page audit without a navigation failure.'
    }
    $markers = @()
    $paths = @()
    foreach ($page in $report.pages) {
        if ($page.status -ne 'captured') { throw "Page $($page.id) was not navigation-verified." }
        $pageRoot = Join-Path $reportDirectory $page.directory
        $scanPath = Join-Path $pageRoot 'scan.json'
        if ((Get-FileHash -LiteralPath $scanPath -Algorithm SHA256).Hash -ne $page.scanSha256) {
            throw "Page $($page.id) evidence hash changed."
        }
        $scan = Get-Content -LiteralPath $scanPath -Raw | ConvertFrom-Json
        if (-not $scan.axe -or -not (Test-Path -LiteralPath (Join-Path $pageRoot 'screenshot.png'))) {
            throw "Page $($page.id) is missing a real Axe result or screenshot."
        }
        $markers += @($scan.snapshot.nodes | ForEach-Object automationId)
        $paths += @($page.selectionPath | ForEach-Object automationId)
    }
    $expectedMarkers = if ($tabless) { @('ScopeProbeRoot') } else { @('BasicsReady', 'ScrollingReady', 'DialogsReady') }
    foreach ($marker in $expectedMarkers) {
        if ($marker -notin $markers) { throw "No page capture contains actual runtime marker $marker." }
    }
    if ($Variant -in @('nested', 'edges')) {
        foreach ($marker in @('TestNestedFirstReady', 'TestNestedSecondReady')) {
            if ($marker -notin $markers) { throw "Nested page was not captured: $marker" }
        }
        if ('TestNestedDisabled' -in $paths) { throw 'A disabled tab was selected.' }
        if (@($report.skipped | Where-Object reason -eq 'disabled').Count -ne 1 `
            -or $report.navigationStatus -ne 'partial' -or $exitCode -ne 2) {
            throw 'The disabled tab must remain explicit partial coverage.'
        }
        if ($Variant -eq 'nested' -and $report.capturedPageCount -ne 6) {
            throw 'Expected four top-level states and two nested states.'
        }
        if ($Variant -eq 'edges' -and @($initial[0].items | Where-Object isOffscreen).Count -eq 0) {
            throw 'The overflow fixture must actually contain initially offscreen tabs.'
        }
    }
    $after = Read-Tabs $window 'after'
    $originalSelectionRestored = $null
    if ($tabless) {
        if ($after.containers.Count -ne 0) { throw 'The tabless window unexpectedly changed its navigation inventory.' }
    }
    else {
        $restored = @($after.containers | Where-Object { $_.runtimeId -eq $initial[0].runtimeId })
        if ($restored.Count -ne 1) { throw 'The original tab group disappeared.' }
        $originalItem = @($restored[0].items | Where-Object runtimeId -eq $initialSelection)[0]
        $originalSelectionRestored = [bool]$originalItem.isSelected
        if (-not $originalSelectionRestored -and (-not $offscreenRestoration -or -not $originalItem.isOffscreen)) {
            throw 'Original selection was not restored without an explicit, observed offscreen boundary.'
        }
        if ($offscreenRestoration -and $originalSelectionRestored) {
            throw 'A blocked restoration must not be reported for an actually restored original tab.'
        }
    }
    [pscustomobject]@{
        variant = $Variant
        processId = $process.Id
        windowHandle = $window
        exitCode = $exitCode
        navigationStatus = $report.navigationStatus
        captureStatus = $report.captureStatus
        capturedPages = $report.capturedPageCount
        ruleFailureOccurrences = $report.ruleFailureCount
        skipped = $report.skipped.Count
        originalSelectionRestored = $originalSelectionRestored
        restorationBoundary = if ($offscreenRestoration) { 'original-tab-offscreen' } else { $null }
        report = Join-Path $reportDirectory 'report.html'
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $output 'verification.json')
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
    $process.Dispose()
}
