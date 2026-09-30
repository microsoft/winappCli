# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
E2E: verify compiler-generated binding owners in a unique, no-activate WinUI fixture.
.DESCRIPTION
PrepareOnly builds without registration or an app actor. Run the prepared receipt only
with an approved exclusive runtime lane. Default modes use no input, overlay, property
writes or existing app access. Closing one owned window tests lifetime scoping; cleanup requests normal exit.
VerifyComments also writes comments and edits only the unique staged fixture's XAML,
then reads historical/current source matches without rebuilding the running fixture.
ProbeOverlay uses one window and records current-head UIA and screenshot observations.
Its runtime phase is restricted to a disposable GitHub-hosted runner.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$PrepareOnly,
    [switch]$ExpectWindowUnavailable,
    [switch]$VerifyComments,
    [switch]$LargeCommentTree,
    [switch]$ProbeOverlay
)
$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$evidence = (New-Item -ItemType Directory -Path $EvidenceDirectory -Force).FullName
$receipt = Join-Path $evidence 'prepared.json'
if ($VerifyComments -and $ExpectWindowUnavailable) { throw 'Comment acceptance requires the corrected current payload.' }
if ($LargeCommentTree -and -not $VerifyComments) { throw 'LargeCommentTree requires VerifyComments.' }
if ($ProbeOverlay -and ($VerifyComments -or $LargeCommentTree -or $ExpectWindowUnavailable)) {
    throw 'ProbeOverlay is a separate one-window acceptance mode.'
}
if ($PrepareOnly) {
    if (Test-Path -LiteralPath $receipt) { throw 'Use a fresh evidence directory for preparation.' }
    $name = 'WinApp.DevTools.Bind.' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('wb-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    $fixture = (New-Item -ItemType Directory -Path (Join-Path $stage 'fixture')).FullName
    New-Item -ItemType Directory -Path (Join-Path $fixture '.git') | Out-Null
    $source = Join-Path $PSScriptRoot '..\src\winapp-CLI\WinApp.Cli.Tests\TestApps\devtools-binding-owners'
    Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $fixture
    $project = Join-Path $fixture 'BindingOwnersFixture.csproj'
    $xml = [System.Xml.Linq.XDocument]::Load($project)
    $xml.Root.Element('PropertyGroup').Element('AssemblyName').Value = $name
    $xml.Save($project)
    $manifest = Join-Path $fixture 'Package.appxmanifest'
    $xml = [System.Xml.Linq.XDocument]::Load($manifest)
    $xml.Root.Element($xml.Root.Name.Namespace + 'Identity').SetAttributeValue('Name', $name)
    $xml.Save($manifest)
    $assets = (New-Item -ItemType Directory -Path (Join-Path $stage 'winui-crash-app\Assets')).FullName
    foreach ($file in @('Square150x150Logo.scale-200.png', 'Square44x44Logo.scale-200.png', 'StoreLogo.png')) {
        Copy-Item -LiteralPath (Join-Path $source "..\winui-crash-app\Assets\$file") -Destination $assets
    }
    dotnet build $project -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --verbosity minimal *> (Join-Path $evidence 'fixture-build.log')
    if ($LASTEXITCODE) { throw "Fixture build failed; see $evidence\fixture-build.log. Stage retained: $stage" }
    [ordered]@{ package = $name; stage = $stage; project = $project; cli = $Cli;
        verifyComments = [bool]$VerifyComments;
        largeCommentTree = [bool]$LargeCommentTree;
        probeOverlay = [bool]$ProbeOverlay;
        hashes = @(Get-FileHash -LiteralPath $Cli, (Join-Path (Split-Path $Cli) 'WinApp.DevTools.Native.dll'),
            (Join-Path (Split-Path $Cli) 'WinApp.DevTools.Managed.dll') | Select-Object Path, Hash) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt
    Write-Host "Prepared without launching: $receipt"
    return
}

if ($ProbeOverlay -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Overlay probing is restricted to the disposable GitHub-hosted UI job, not a shared desktop.'
}
$prepared = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
$stage = (Resolve-Path -LiteralPath $prepared.stage).Path
$project = (Resolve-Path -LiteralPath $prepared.project).Path
$name = $prepared.package
if ($name -notmatch '^WinApp\.DevTools\.Bind\.[a-f0-9]{12}$' -or
    (Split-Path -Leaf $stage) -notmatch '^wb-[a-f0-9]{12}$' -or
    (Split-Path -Parent $stage) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or
    -not $project.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $prepared.cli -ne $Cli -or [bool]$prepared.verifyComments -ne [bool]$VerifyComments -or
    [bool]$prepared.probeOverlay -ne [bool]$ProbeOverlay -or
    [bool]$prepared.largeCommentTree -ne [bool]$LargeCommentTree) { throw 'Invalid prepared fixture identity.' }
foreach ($file in $prepared.hashes) {
    if ((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw "Prepared payload changed: $($file.Path)" }
}
if (@(Get-AppxPackage -Name $name).Count -or @(Get-Process | Where-Object ProcessName -eq $name).Count) {
    throw 'Fixture identity is already in use. Refusing to adopt an existing actor/package.'
}
$report = Join-Path $evidence 'windows.json'
if (Test-Path -LiteralPath $report) { throw 'This fixture receipt has already been run.' }
Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class BindingOwnersGuard {
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint size, tick; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref Input input);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    public static uint InputTick() {
        var input = new Input { size = 8 };
        if (!GetLastInputInfo(ref input)) throw new Win32Exception();
        return input.tick;
    }
}
'@
$foreground = [BindingOwnersGuard]::GetForegroundWindow()
$inputTick = [BindingOwnersGuard]::InputTick()
$owned = $null
$sequence = 0
$checks = 0
$started = $null
$executable = $null
$launchStarted = [DateTime]::UtcNow
function Guard([switch]$AllowOwnedExit) {
    $currentForeground = [BindingOwnersGuard]::GetForegroundWindow()
    [uint32]$foregroundPid = 0
    if ($ProbeOverlay -and $owned -and $currentForeground -ne $foreground) {
        $null = [BindingOwnersGuard]::GetWindowThreadProcessId($currentForeground, [ref]$foregroundPid)
    }
    if (($currentForeground -ne $foreground -and (-not $ProbeOverlay -or -not $owned -or $foregroundPid -ne $owned.Id)) -or
        (-not $ProbeOverlay -and [BindingOwnersGuard]::InputTick() -ne $inputTick)) {
        throw 'Desktop activity changed; aborting reads and cleaning only the owned fixture.'
    }
    if ($owned -and -not ($AllowOwnedExit -and $owned.HasExited)) {
        $current = Get-Process -Id $owned.Id -ErrorAction Stop
        if ($current.StartTime.ToUniversalTime() -ne $started -or $current.Path -ne $executable) {
            throw 'Fixture lifetime changed; refusing further calls.'
        }
    }
}
function Invoke-Cli([string[]]$Arguments, [bool]$Success = $true, [bool]$MayExit = $false, [string]$AllowedError = '') {
    Guard
    $script:sequence++
    $stdout = Join-Path $evidence "$script:sequence-stdout.json"
    $stderr = Join-Path $evidence "$script:sequence-stderr.txt"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $Cli @Arguments --json 1> $stdout 2> $stderr
    $exit = $LASTEXITCODE
    $clock.Stop()
    [ordered]@{ arguments = $Arguments; milliseconds = $clock.ElapsedMilliseconds; exitCode = $exit } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $evidence "$script:sequence-call.json")
    $result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
    $allowed = $AllowedError -and $exit -ne 0 -and (Get-Content -LiteralPath $stderr -Raw) -match "`"code`":`"$AllowedError`""
    if (($exit -eq 0) -ne $Success -and -not $allowed) {
        throw "Unexpected exit $exit from $($Arguments -join ' '): $(Get-Content -LiteralPath $stdout -Raw)"
    }
    Guard -AllowOwnedExit:$MayExit
    return $result
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Host "PASS $Message"
}
function Wait-CommentStatus([string]$App, [string]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $status = Invoke-Cli @('ui', 'get-property', 'DevToolsGuestCommentStatus', '-a', $App, '-p', 'Name')
        if ($status.properties.Name -like $Expected) { return $status.properties.Name }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Comment status did not become '$Expected': $($status.properties.Name)"
}
function Nodes($Items) { foreach ($item in $Items) { $item; Nodes $item.children } }
function Control([string]$Command) {
    Set-Content -LiteralPath ($report + '.pending') -Value $Command -NoNewline
    Move-Item -LiteralPath ($report + '.pending') -Destination ($report + '.command') -Force
}
function Window-Guard([string]$Window) {
    Guard
    [uint32]$ownerPid = 0
    $null = [BindingOwnersGuard]::GetWindowThreadProcessId([IntPtr][long]$Window, [ref]$ownerPid)
    Check ($ownerPid -eq $owned.Id) 'HWND belongs to the owned lifetime'
}
function Diagnose([string]$Handle, [string]$Window, [string]$Expected) {
    Window-Guard $Window
    $baselineWindow = $ExpectWindowUnavailable -and $Expected -in @('Alpha', 'Beta')
    $diagnosis = Invoke-Cli @('devtools', 'diagnose-binding', $Handle, 'Text', '-w', $Window) (-not $baselineWindow)
    if ($baselineWindow) {
        Check ($diagnosis.error.token -eq 'binding-unavailable' -and $diagnosis.result.state -eq 'unavailable') 'baseline cannot establish Window source'
    }
    else { Check ($diagnosis.ok -eq $true -and $diagnosis.result.sourceValue -ceq $Expected) "exact source value: $Expected" }
}
try {
    $launchArguments = '"' + $report + '"' + $(if ($LargeCommentTree) { ' large' } elseif ($ProbeOverlay) { ' single' } else { '' })
    if ($ProbeOverlay) { $env:WINAPP_DEVTOOLS_LOG = '1' }
    $launch = Invoke-Cli @('run', $project, '--devtools', '--no-overlay', '--detach', '--args', $launchArguments)
    $candidate = Get-Process -Id $launch.processId -ErrorAction Stop
    $executable = $candidate.Path
    $started = $candidate.StartTime.ToUniversalTime()
    if ($candidate.ProcessName -ne $name -or $started -lt $launchStarted -or
        -not $executable.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Launch did not identify the newly staged fixture.'
    }
    $owned = $candidate
    $null = $owned.Handle
    [ordered]@{ pid = $owned.Id; startTimeUtc = $started; executable = $executable; package = $name } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'target-identity.json')
    Check (-not $launch.sourceError -and @($launch.sourceWarnings | Where-Object { $null -ne $_ }).Count -eq 0) 'all fixture source files admitted'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $report) -and [DateTime]::UtcNow -lt $deadline) {
        Guard
        Start-Sleep -Milliseconds 100
    }
    $windows = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Check ($windows.pid -eq $owned.Id) 'fixture self-report matches launch'
    if ($ProbeOverlay) {
        Window-Guard ([string]$windows.alpha)
        Check ($windows.beta -eq 0 -and $windows.unsupported -eq 0) 'probe fixture owns one application window'
        $app = [string]$owned.Id
        $window = [string]$windows.alpha
        $baseline = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $baseline | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $evidence 'overlay-baseline-uia.json')
        $sameNames = Invoke-Cli @('ui', 'search', 'Alpha', '-a', $app)
        Check (-not $sameNames.hasMore -and @($sameNames.matches | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Alpha' }).Count -eq 2) 'distinct same-named fixture controls remain separate'
        $null = Invoke-Cli @('devtools', 'call', 'Overlay.show', '-a', $app)
        $overlayState = Invoke-Cli @('devtools', 'call', 'Overlay.getState', '-a', $app)
        Check ($overlayState.result.host -eq 'uiLayer') 'overlay chrome is hosted in the XAML diagnostics UI layer'
        $tree = Invoke-Cli @('devtools', 'inspect', '-w', $window, '--all', '--depth', '30')
        Check (-not $tree.truncated -and $tree.depthLimitedElements -eq 0) 'overlay probe has a complete owned source tree'
        $heading = @(Nodes $tree.elements | Where-Object name -eq 'WindowHeading')
        Check ($heading.Count -eq 1) 'overlay probe selects one exact authored heading'
        $null = Invoke-Cli @('devtools', 'call', 'Selection.arm', '-w', $window)
        $null = Invoke-Cli @('ui', 'click', 'WindowHeading', '-w', $window)
        $null = Invoke-Cli @('ui', 'wait-for', 'DevToolsSelComment', '-a', $app, '-t', '5000')
        $selection = Invoke-Cli @('devtools', 'call', 'Selection.poll', '-a', $app)
        Check ($selection.result.handle -eq [string]$heading[0].handle) 'pick settled on the exact owned authored heading'
        $peek = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $peek | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $evidence 'overlay-peek-uia.json')
        $all = @($peek.windows | ForEach-Object { Nodes $_.elements })
        Check (@($all | Where-Object hasMoreChildren -eq $true).Count -eq 0) 'quick peek UIA observation is not depth-limited'
        Check (@($all | Where-Object { $_.automationId -eq 'DevToolsSelComment' -and -not $_.isOffscreen }).Count -gt 0) 'quick peek comment editor is visible'
        $rows = Invoke-Cli @('ui', 'search', 'DevToolsSelRow', '-a', $app)
        Check (-not $rows.hasMore) 'quick-property search is not result-limited'
        $duplicates = @($rows.matches | Where-Object { $_.selector } | Group-Object selector | Where-Object Count -gt 1 |
            ForEach-Object { [ordered]@{ selector = $_.Name; count = $_.Count; elements = $_.Group } })
        $inspectedRows = @($all | Where-Object { $_.automationId -match '^DevToolsSelRow[0-4]$' })
        Check ($rows.matchCount -eq 5 -and $duplicates.Count -eq 0) 'search emits each of the five quick-property peers once'
        Check ($inspectedRows.Count -eq 5 -and @($inspectedRows | Group-Object automationId | Where-Object Count -ne 1).Count -eq 0) 'inspect emits each quick-property peer once across HWND roots'
        $saveCandidates = @($all | Where-Object {
            -not $_.isOffscreen -and $_.type -eq 'Button' -and ($_.name -match '(?i)save|commit' -or $_.automationId -match '(?i)save|commit')
        })
        Check ($saveCandidates.Count -eq 1 -and $saveCandidates[0].automationId -eq 'DevToolsSelCommentSave') 'one visible inline Save action is available'
        $popupBounds = @($all | Where-Object { $_.type -eq 'Window' -or $_.className -match 'Popup' } |
            Select-Object selector, name, className, isOffscreen, x, y, width, height)
        $null = Invoke-Cli @('ui', 'screenshot', '-w', $window, '--capture-screen', '-o', (Join-Path $evidence 'overlay-peek.png'))
        $popupWindows = @($peek.windows | Where-Object className -eq 'Microsoft.UI.Content.PopupWindowSiteBridge')
        Check ($popupWindows.Count -eq 1) 'one owned quick-peek popup is available for bounded capture'
        $popupWindow = [string]$popupWindows[0].hwnd
        Window-Guard $popupWindow
        $null = Invoke-Cli @('ui', 'screenshot', '-w', $popupWindow, '--capture-screen', '-o', (Join-Path $evidence 'overlay-comment-panel.png'))
        $windowRoots = @($peek.windows | ForEach-Object { $_.elements[0] })
        $maximumWidth = ($windowRoots | Measure-Object width -Maximum).Maximum
        $maximumHeight = ($windowRoots | Measure-Object height -Maximum).Maximum
        Check ($maximumWidth -gt 0 -and $maximumHeight -gt 0 -and
            @($popupBounds | Where-Object { $_.width -gt $maximumWidth -or $_.height -gt $maximumHeight }).Count -eq 0) 'popup peers stay within measured owned-window extents without negative parking'
        Check (@($all | Where-Object { $_.automationId -eq 'DevToolsProtoRailL' -and -not $_.isOffscreen }).Count -eq 1 -and
            @($all | Where-Object { $_.automationId -eq 'DevToolsProtoPill' -and -not $_.isOffscreen }).Count -eq 0) 'collapsed toolbar exposes only the reachable pill'
        # The rail hands keyboard focus to the toolbar's first action, so `ui focus` may observe the handoff
        # instead of focus on the rail itself; where focus lands is asserted next.
        $null = Invoke-Cli @('ui', 'focus', 'DevToolsProtoRailL', '-a', $app) -AllowedError 'focus_not_acquired'
        $focused = Invoke-Cli @('ui', 'get-focused', '-a', $app)
        Check ($focused.hasFocus -and $focused.element.automationId -eq 'DevToolsProtoPick') 'keyboard focus opens the toolbar and reaches its first action'
        $expanded = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $expanded | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $evidence 'overlay-expanded-uia.json')
        $expandedNodes = @($expanded.windows | ForEach-Object { Nodes $_.elements })
        Check (@($expandedNodes | Where-Object { $_.automationId -eq 'DevToolsProtoPill' -and -not $_.isOffscreen }).Count -eq 1 -and
            @($expandedNodes | Where-Object { $_.automationId -eq 'DevToolsProtoRailL' -and -not $_.isOffscreen }).Count -eq 0) 'keyboard expansion exposes the bar without a second visible pill'
        $null = Invoke-Cli @('devtools', 'call', 'Overlay.hide', '-a', $app)
        $hidden = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $hiddenNodes = @($hidden.windows | ForEach-Object { Nodes $_.elements })
        Check (@($hiddenNodes | Where-Object {
            -not $_.isOffscreen -and $_.automationId -match '^DevTools(ProtoPill|ProtoRailL|Snap)'
        }).Count -eq 0) 'explicit hide removes toolbar halves and snap targets'
        $null = Invoke-Cli @('devtools', 'call', 'Overlay.show', '-a', $app)
        $restored = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $restoredNodes = @($restored.windows | ForEach-Object { Nodes $_.elements })
        Check (@($restoredNodes | Where-Object { $_.automationId -eq 'DevToolsProtoPill' -and -not $_.isOffscreen }).Count -eq 1 -and
            @($restoredNodes | Where-Object { $_.automationId -eq 'DevToolsProtoRailL' -and -not $_.isOffscreen }).Count -eq 0) 'show restores the previously expanded toolbar state'
        $blockedStore = Join-Path (Split-Path -Parent $project) '.winapp\ui-comments.json'
        Check (-not (Test-Path -LiteralPath $blockedStore)) 'comment failure probe has no existing store to overwrite'
        New-Item -ItemType Directory -Path $blockedStore -Force | Out-Null
        try {
            $storeFailure = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'blocked-store-control',
                '--text', 'Owned storage failure control', '--file', 'MainWindow.xaml',
                '--source-root', (Split-Path -Parent $project)) $false
            # The panel is a non-activating popup, so `ui focus` (which activates the control's own window)
            # correctly refuses it; the explicit Save action exercises the same writer path.
            $null = Invoke-Cli @('ui', 'set-value', 'DevToolsSelComment', 'Owned comment failure probe', '-a', $app)
            $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelCommentSave', '-a', $app)
            $failureStatus = Wait-CommentStatus $app 'Save not confirmed (stage: writer-exit, code: 1)*'
            $writerDeadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                Guard
                $writers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($owned.Id)")
                if ($writers.Count -eq 0) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $writerDeadline)
            Check ($writers.Count -eq 0) 'owned comment writer finished before failure observation and normal cleanup'
            $failedSave = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
            $failedSave | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $evidence 'comment-failure-uia.json')
            $failedSaveNodes = @($failedSave.windows | ForEach-Object { Nodes $_.elements })
            $draft = Invoke-Cli @('ui', 'get-property', 'DevToolsSelComment', '-a', $app, '-p', 'Value')
            Check ($draft.properties.Value -ceq 'Owned comment failure probe') 'failed write preserves the exact editor draft'
            Check (@($failedSaveNodes | Where-Object {
                -not $_.isOffscreen -and $_.automationId -eq 'DevToolsSelCommentDelete'
            }).Count -eq 0) 'failed write does not claim a persisted comment through the delete affordance'
            [ordered]@{
                failureVerified = $true
                status = $failureStatus
                storeFailure = $storeFailure
                storeIsDirectory = Test-Path -LiteralPath $blockedStore -PathType Container
                storeIsFile = Test-Path -LiteralPath $blockedStore -PathType Leaf
                commentEditor = @($failedSaveNodes | Where-Object automationId -eq 'DevToolsSelComment')
                visibleStatus = @($failedSaveNodes | Where-Object {
                    -not $_.isOffscreen -and $_.automationId -in @('DevToolsSelOperationStatus', 'DevToolsGuestCommentStatus')
                })
                visibleDeleteControls = @($failedSaveNodes | Where-Object {
                    -not $_.isOffscreen -and $_.automationId -eq 'DevToolsSelCommentDelete'
                }).Count
            } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $evidence 'comment-failure-observation.json')
        }
        finally {
            Remove-Item -LiteralPath $blockedStore
        }
        $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelCommentSave', '-a', $app)
        $null = Wait-CommentStatus $app 'Saved.'
        $stored = Get-Content -LiteralPath $blockedStore -Raw | ConvertFrom-Json
        $savedComment = @($stored.comments | Where-Object text -CEQ 'Owned comment failure probe')
        Check ($savedComment.Count -eq 1) 'retry through Save actually persists the retained draft to the owned store'

        $mutexHash = [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($blockedStore).ToLowerInvariant()))
        $mutex = [Threading.Mutex]::new($false, 'winapp-ui-comments-' + [Convert]::ToHexString($mutexHash, 0, 8))
        $held = $false
        try {
            $held = $mutex.WaitOne(0)
            Check $held 'owned store lock is available for the bounded active-draft control'
            $null = Invoke-Cli @('ui', 'set-value', 'DevToolsSelComment', 'Owned submitted text', '-a', $app)
            $dirty = Invoke-Cli @('ui', 'get-property', 'DevToolsGuestCommentStatus', '-a', $app, '-p', 'Name')
            Check ($dirty.properties.Name -ceq 'Unsaved changes.') 'editing after acknowledgement removes the stale Saved status'
            $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelCommentSave', '-a', $app)
            $pending = Invoke-Cli @('ui', 'get-property', 'DevToolsGuestCommentStatus', '-a', $app, '-p', 'Name')
            Check ($pending.properties.Name -ceq 'Saving...') 'blocked writer reports pending rather than persisted'
            $null = Invoke-Cli @('ui', 'set-value', 'DevToolsSelComment', 'Owned newer draft', '-a', $app)
        }
        finally {
            if ($held) { $mutex.ReleaseMutex() }
            $mutex.Dispose()
        }
        $null = Wait-CommentStatus $app 'Submitted text was saved. Your newer draft is not saved yet.'
        $draft = Invoke-Cli @('ui', 'get-property', 'DevToolsSelComment', '-a', $app, '-p', 'Value')
        $stored = Get-Content -LiteralPath $blockedStore -Raw | ConvertFrom-Json
        Check ($draft.properties.Value -ceq 'Owned newer draft' -and
            @($stored.comments | Where-Object text -CEQ 'Owned submitted text').Count -eq 1) 'actual store push and completion preserve newer editor text'
        $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelCommentSave', '-a', $app)
        $null = Wait-CommentStatus $app 'Saved.'
        $stored = Get-Content -LiteralPath $blockedStore -Raw | ConvertFrom-Json
        Check (@($stored.comments | Where-Object text -CEQ 'Owned newer draft').Count -eq 1) 'second explicit Save persists the newer draft'
        $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelClose', '-a', $app)

        # Comment status must also show for an element whose quick peek has no binding rows.
        $null = Invoke-Cli @('devtools', 'call', 'Selection.arm', '-w', $window)
        $null = Invoke-Cli @('ui', 'click', 'NarrowCommentProbe', '-w', $window)
        $null = Invoke-Cli @('ui', 'wait-for', 'DevToolsSelComment', '-a', $app, '-t', '5000')
        $null = Invoke-Cli @('ui', 'set-value', 'DevToolsSelComment', 'Owned unbound draft', '-a', $app)
        $null = Wait-CommentStatus $app 'Unsaved changes.'
        $null = Invoke-Cli @('ui', 'set-value', 'DevToolsSelComment', '', '-a', $app)
        $null = Invoke-Cli @('ui', 'invoke', 'DevToolsSelClose', '-a', $app)
        $probeName = 'CommentAndTreeProbeWithAnIntentionallyLongAuthoredNameToExerciseHorizontalScrollingAtTheNormalInspectorPaneWidth'
        $narrow = @(Nodes $tree.elements | Where-Object name -EQ $probeName)
        Check ($narrow.Count -eq 1) 'one authored narrow control is available for conditional observations'
        $null = Invoke-Cli @('devtools', 'comments', 'add', '--app', $app, '--from-element',
            [string]$narrow[0].handle, '--id', 'owned-narrow-pin', '--text', 'Owned narrow pin observation')
        $pins = Invoke-Cli @('ui', 'inspect', '-a', $app, '--depth', '40')
        $pins | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $evidence 'narrow-pin-uia.json')
        $pinNodes = @($pins.windows | ForEach-Object { Nodes $_.elements })
        $narrowBox = @($pinNodes | Where-Object { $_.automationId -eq 'NarrowCommentProbe' -and -not $_.isOffscreen })
        Check ($narrowBox.Count -eq 1 -and $narrowBox[0].width -gt 0) 'narrow control has actual visible UIA bounds'
        $overlaps = @($pinNodes | Where-Object { $_.automationId -eq 'WinAppDevToolsCommentPin' -and -not $_.isOffscreen } |
            ForEach-Object {
                $width = [Math]::Max(0, [Math]::Min($_.x + $_.width, $narrowBox[0].x + $narrowBox[0].width) - [Math]::Max($_.x, $narrowBox[0].x))
                $height = [Math]::Max(0, [Math]::Min($_.y + $_.height, $narrowBox[0].y + $narrowBox[0].height) - [Math]::Max($_.y, $narrowBox[0].y))
                [ordered]@{ pin = $_; intersectionArea = $width * $height }
            })
        [ordered]@{ observationOnly = $false; dpi = [BindingOwnersGuard]::GetDpiForWindow([IntPtr][long]$window);
            target = $narrowBox[0]; pins = $overlaps } | ConvertTo-Json -Depth 20 |
            Set-Content (Join-Path $evidence 'narrow-pin-observation.json')
        $null = Invoke-Cli @('ui', 'screenshot', '-w', $window, '--capture-screen', '-o', (Join-Path $evidence 'narrow-pin.png'))
        Check ($overlaps.Count -eq 2 -and @($overlaps | Where-Object intersectionArea -GT 0).Count -eq 0) 'both visible comment pins leave the narrow label unobscured'

        $null = Invoke-Cli @('devtools', 'call', 'Window.open', '-a', $app)
        try {
            $null = Invoke-Cli @('ui', 'wait-for', 'TreeScroll', '-a', $app, '-t', '5000')
            $null = Invoke-Cli @('devtools', 'call', 'Selection.select', "handle=$($heading[0].handle)", '-a', $app)
            $baseline = Invoke-Cli @('devtools', 'call', 'Selection.poll', '-a', $app)
            Check ($baseline.result.handle -eq [string]$heading[0].handle -and
                $heading[0].handle -ne $narrow[0].handle) 'tree-scroll baseline selects a different exact element'
            $before = Invoke-Cli @('ui', 'get-property', 'TreeScroll', '-a', $app)
            for ($scrollAttempt = 0; $scrollAttempt -lt 20 -and [double]$before.properties.ScrollHorizontalPercent -gt 0; $scrollAttempt++) {
                $null = Invoke-Cli @('ui', 'scroll', 'TreeScroll', '-a', $app, '--direction', 'left')
                $before = Invoke-Cli @('ui', 'get-property', 'TreeScroll', '-a', $app)
            }
            Check ([double]$before.properties.ScrollHorizontalPercent -eq 0) 'tree-scroll observation starts at a proven zero horizontal offset'
            $null = Invoke-Cli @('devtools', 'call', 'Selection.select', "handle=$($narrow[0].handle)", '-a', $app)
            $selected = Invoke-Cli @('devtools', 'call', 'Selection.poll', '-a', $app)
            Check ($selected.result.handle -eq [string]$narrow[0].handle) 'tree-scroll observation selected the exact owned narrow element'
            Start-Sleep -Milliseconds 250
            $after = Invoke-Cli @('ui', 'get-property', 'TreeScroll', '-a', $app)
            Check ([double]$after.properties.ScrollHorizontalPercent -eq 0) 'selection preserves the zero horizontal tree offset'
            $null = Invoke-Cli @('ui', 'scroll', 'TreeScroll', '-a', $app, '--direction', 'right')
            Start-Sleep -Milliseconds 250
            $manualBefore = Invoke-Cli @('ui', 'get-property', 'TreeScroll', '-a', $app)
            Check ([double]$manualBefore.properties.ScrollHorizontalPercent -gt 0) 'manual horizontal scrolling remains available'
            $null = Invoke-Cli @('devtools', 'call', 'Selection.select', "handle=$($heading[0].handle)", '-a', $app)
            $manualSelected = Invoke-Cli @('devtools', 'call', 'Selection.poll', '-a', $app)
            Check ($manualSelected.result.handle -eq [string]$heading[0].handle) 'manual-scroll control selects a distinct exact row'
            Start-Sleep -Milliseconds 250
            $manualAfter = Invoke-Cli @('ui', 'get-property', 'TreeScroll', '-a', $app)
            Check ([double]$manualAfter.properties.ScrollHorizontalPercent -eq
                [double]$manualBefore.properties.ScrollHorizontalPercent) 'selection preserves the user-chosen nonzero horizontal offset'
            [ordered]@{ observationOnly = $false; before = $before.properties; after = $after.properties;
                manualBefore = $manualBefore.properties; manualAfter = $manualAfter.properties;
                baselineHandle = [string]$heading[0].handle; selectedHandle = [string]$narrow[0].handle } | ConvertTo-Json -Depth 20 |
                Set-Content (Join-Path $evidence 'tree-scroll-observation.json')
        }
        finally {
            $null = Invoke-Cli @('devtools', 'call', 'Window.close', '-a', $app)
        }

        # Ctrl+Shift+F12 moves keyboard focus to the toolbar; Esc returns it to the app element that had it.
        function Wait-Focused([string]$Expected) {
            $deadline = [DateTime]::UtcNow.AddSeconds(3)
            do {
                $current = Invoke-Cli @('ui', 'get-focused', '-a', $app)
                if ($current.element.automationId -eq $Expected) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            return [string]$current.element.automationId
        }
        $null = Invoke-Cli @('ui', 'focus', 'ShortcutReturn', '-a', $app)
        $null = Invoke-Cli @('ui', 'send-keys', 'ctrl+shift+f12', '-a', $app, '--via', 'send-input')
        Check ((Wait-Focused 'DevToolsProtoPick') -eq 'DevToolsProtoPick') 'Ctrl+Shift+F12 moves keyboard focus to the toolbar'
        $null = Invoke-Cli @('ui', 'send-keys', 'esc', '-a', $app, '--via', 'send-input')
        Check ((Wait-Focused 'ShortcutReturn') -eq 'ShortcutReturn') 'Esc from the toolbar returns keyboard focus to the app'
        [ordered]@{
            processId = $owned.Id; startTicksUtc = $started.Ticks; executable = $executable
            sourceHandle = [string]$heading[0].handle
            rowMatchCount = $rows.matchCount; rowSearchHasMore = $rows.hasMore
            duplicateRuntimeDerivedSelectors = $duplicates
            inspectedRowCount = $inspectedRows.Count; deduplicationVerified = $true
            visibleSaveCandidates = $saveCandidates; popupBounds = $popupBounds
            commentPanelHwnd = $popupWindow
            toolbarVisibilityVerified = $true; keyboardFocusId = $focused.element.automationId
            maximumOwnedWidth = $maximumWidth; maximumOwnedHeight = $maximumHeight
            commentSaveVerified = $true; newerDraftVerified = $true
            pinPlacementVerified = $true
            remaining = 'Tree scrolling is observation-only.'
        } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $evidence 'overlay-observations.json')
        [ordered]@{ checks = $checks; result = 'passed' } | ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
        return
    }
    foreach ($which in @('alpha', 'beta')) {
        $window = [string]$windows.$which
        Window-Guard $window
        $tree = Invoke-Cli @('devtools', 'inspect', '-w', $window, '--all', '--depth', '30')
        Check ($tree.truncated -eq $false -and $tree.depthLimitedElements -eq 0) 'complete fixture tree'
        $nodes = @(Nodes $tree.elements)
        $label = if ($which -eq 'alpha') { 'Alpha' } else { 'Beta' }
        foreach ($pair in @(@('WindowHeading', $label), @('PageHeading', 'Page source'), @('ControlHeading', 'Control source'))) {
            $target = @($nodes | Where-Object name -eq $pair[0])
            Check ($target.Count -eq 1) "unique $($pair[0]) in $which"
            if ($which -eq 'alpha' -and $pair[0] -eq 'WindowHeading') { $closedHandle = [string]$target[0].handle }
            Diagnose ([string]$target[0].handle) $window $pair[1]
        }
        $rows = @($nodes | Where-Object name -eq 'TemplateHeading')
        Check ($rows.Count -eq 2) 'two same-named typed-template instances realized'
        $values = foreach ($row in $rows) {
            Window-Guard $window
            $result = Invoke-Cli @('devtools', 'diagnose-binding', ([string]$row.handle), 'Text', '-w', $window)
            Check ($result.ok -eq $true) 'typed-template instance diagnosed'
            $result.result.sourceValue
        }
        Check (@(Compare-Object ($values | Sort-Object) @("$label row one", "$label row two")).Count -eq 0) 'templates use distinct dataRoot instances'
    }
    $window = [string]$windows.unsupported
    Window-Guard $window
    # A Window held only in a static App field (App.MainWindow { get; private set; }) is still proven by its generated bindings.
    $static = Invoke-Cli @('devtools', 'diagnose-binding', 'WindowHeading', 'Text', '-w', $window)
    Check ($static.ok -eq $true -and $static.result.sourceValue -ceq 'Static-only') 'static-held Window owner is diagnosed from its own bindings'
    if ($VerifyComments) {
        $sourceRoot = Split-Path -Parent $project
        $window = [string]$windows.beta
        $tree = Invoke-Cli @('devtools', 'inspect', '-w', $window, '--all', '--depth', '30')
        $nodes = @(Nodes $tree.elements)
        $named = @($nodes | Where-Object name -eq 'WindowHeading')
        $templates = @($nodes | Where-Object name -eq 'TemplateHeading')
        Check ($named.Count -eq 1 -and $templates.Count -eq 2) 'comment targets are exact owned-window handles'
        $repeated = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'repeated', '--text', 'A unique x:Name is one place to edit',
            '--from-element', [string]$named[0].handle, '--app', [string]$owned.Id, '--source-root', $sourceRoot)
        Check ($repeated.comment.anchorConfirmed -and -not $repeated.comment.requiresConfirmation) 'a unique x:Name confirms even when its window is open twice'
        $templated = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'template', '--text', 'Template instance requires confirmation',
            '--from-element', [string]$templates[0].handle, '--app', [string]$owned.Id, '--source-root', $sourceRoot)
        Check (-not $templated.comment.anchorConfirmed -and $templated.comment.requiresConfirmation) 'typed-template comment cannot auto-confirm'
        Guard
        Control 'close-duplicates'
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath ($report + '.single')) -and [DateTime]::UtcNow -lt $deadline) {
            Guard
            Start-Sleep -Milliseconds 100
        }
        Check (Test-Path -LiteralPath ($report + '.single')) 'duplicate owned windows closed normally'
        Window-Guard $window
        $tree = Invoke-Cli @('devtools', 'inspect', '-w', $window, '--all', '--depth', '30')
        Check ($tree.truncated -eq $false -and $tree.depthLimitedElements -eq 0) 'comment handles come from a complete fixture tree without warming source classification'
        $nodes = @(Nodes $tree.elements)
        $named = @($nodes | Where-Object name -eq 'WindowHeading')
        $unnamed = @($nodes | Where-Object {
            $_.type -match '(^|\.)TextBlock$' -and -not $_.name -and
            @($_.preview | Where-Object { $_.name -eq 'Text' -and $_.value -eq 'Beta' }).Count -eq 1
        })
        Check ($named.Count -eq 1 -and $unnamed.Count -eq 1) 'unique named and unnamed fixture declarations'
        if ($LargeCommentTree) {
            Check ($nodes.Count -ge 400) 'larger fixture retains at least 400 live nodes'
            $cold = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'cold-large', '--text', 'Bounded cold capture',
                '--from-element', [string]$named[0].handle, '--app', [string]$owned.Id, '--source-root', $sourceRoot)
            Check ($cold.comment.anchorConfirmed -and -not $cold.comment.requiresConfirmation) 'a unique x:Name confirms without waiting for the large census'
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                $classification = (Invoke-Cli @('devtools', 'call', 'VisualTree.getAppAuthored', '-a', [string]$owned.Id)).result
                if ($classification.truncated -eq $false) { break }
            }
            Check ($classification.truncated -eq $false -and $classification.censusNodes -ge 400) 'explicit bounded classification completes the larger census'
        }
        $captured = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'named', '--text', 'Review the heading',
            '--from-element', [string]$named[0].handle, '--app', [string]$owned.Id, '--source-root', $sourceRoot)
        $plain = Invoke-Cli @('devtools', 'comments', 'add', '--id', 'unnamed', '--text', 'Review the unnamed heading',
            '--from-element', [string]$unnamed[0].handle, '--app', [string]$owned.Id, '--source-root', $sourceRoot)
        Check ($captured.comment.anchorConfirmed -and $plain.comment.anchorConfirmed) 'unique authored declarations confirm despite bound runtime text'
        $historical = $captured.comment.anchor.line
        $declaration = $captured.comment.anchor.authored.declaration
        $path = Join-Path $sourceRoot 'MainWindow.xaml'
        $original = [IO.File]::ReadAllText($path)
        Guard
        [IO.File]::WriteAllText($path, "`n`n" + $original)
        $moved = Invoke-Cli @('devtools', 'comments', 'get', 'named', '--source-root', $sourceRoot)
        Check ($moved.comment.anchorConfirmed -and $moved.comment.hits[0].line -eq $historical + 2) 'line movement preserves strong current identity'
        Check ($moved.comment.anchor.line -eq $historical -and $moved.comment.anchor.authored.declaration -ceq $declaration) 'creation location and declaration are immutable'
        Guard
        [IO.File]::WriteAllText($path, ("`n`n" + $original).Replace('Text="{x:Bind HeadingText, Mode=OneWay}"', 'Text="Implemented heading"'))
        $edited = Invoke-Cli @('devtools', 'comments', 'get', 'named', '--source-root', $sourceRoot)
        Check ($edited.comment.anchorConfirmed -and $edited.comment.hits[0].text.Contains('Implemented heading')) 'named declaration survives successful authored edit'
        $weak = Invoke-Cli @('devtools', 'comments', 'get', 'unnamed', '--source-root', $sourceRoot)
        Check (-not $weak.comment.anchorConfirmed -and $weak.comment.requiresConfirmation -and $weak.comment.hits.Count -gt 0) 'changed unnamed declaration remains a ranked suggestion'
        $saved = Invoke-Cli @('devtools', 'comments', 'update', 'named', '--status', 'resolved', '--note', 'Verified staged source', '--source-root', $sourceRoot)
        Check ($saved.comment.status -eq 'resolved' -and $saved.comment.anchor.authored.declaration -ceq $declaration) 'status update preserves creation evidence'
        Guard
        [IO.File]::WriteAllText($path, $original)
    }
    Guard
    Control 'close-alpha'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath ($report + '.closed')) -and [DateTime]::UtcNow -lt $deadline) {
        Guard
        Start-Sleep -Milliseconds 100
    }
    Check (Test-Path -LiteralPath ($report + '.closed')) 'first owned Window closed normally'
    Diagnose 'WindowHeading' ([string]$windows.beta) 'Beta'
    Window-Guard ([string]$windows.beta)
    $closed = Invoke-Cli @('devtools', 'diagnose-binding', $closedHandle, 'Text', '-w', [string]$windows.beta) $false
    Check ($closed.ok -eq $false) 'closed target is not rebound to the surviving Window'
    $overlay = (Invoke-Cli @('devtools', 'call', 'Overlay.getState', '-a', [string]$owned.Id)).result
    Check ($overlay.toolbarVisible -eq $false) 'overlay remains hidden'
    [ordered]@{ checks = $checks; result = 'passed' } | ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
}
finally {
    # A failed launch may have created the unique app before returning its PID.
    if (-not $owned) {
        $candidates = @(Get-Process | Where-Object ProcessName -eq $name)
        if ($candidates.Count -eq 1 -and $candidates[0].StartTime.ToUniversalTime() -ge $launchStarted -and
            $candidates[0].Path.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $owned = $candidates[0]
            $null = $owned.Handle
            $started = $owned.StartTime.ToUniversalTime()
            $executable = $owned.Path
        }
    }
    if ($owned -and -not $owned.HasExited) {
        Control 'exit'
        if (-not $owned.WaitForExit(30000)) {
            throw "Owned PID $($owned.Id) did not exit normally. Retaining its package/stage; no kill attempted."
        }
    }
    $package = @(Get-AppxPackage -Name $name)
    if ($ProbeOverlay -and $owned) {
        Copy-Item -LiteralPath (Join-Path $env:TEMP "winapp-devtools-$($owned.Id).log") -Destination $evidence -ErrorAction SilentlyContinue
    }
    if ($owned -and $owned.HasExited) {
        [ordered]@{ pid = $owned.Id; exitCode = $owned.ExitCode } | ConvertTo-Json |
            Set-Content (Join-Path $evidence 'normal-shutdown.json')
        foreach ($item in $package) {
            if (-not $item.InstallLocation.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Package install location does not match the owned stage; cleanup refused.'
            }
            Remove-AppxPackage -Package $item.PackageFullName
        }
        if ($owned.ExitCode -ne 0) { throw "Owned fixture exit was abnormal: $($owned.ExitCode)" }
    }
    if (@(Get-AppxPackage -Name $name).Count -eq 0 -and @(Get-Process | Where-Object ProcessName -eq $name).Count -eq 0) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    else { throw 'Owned fixture process or package remains; retaining its stage.' }
    if (Test-Path -LiteralPath $stage) { throw 'Owned fixture stage remains after cleanup.' }
    [ordered]@{ package = $name; processAbsent = $true; packageAbsent = $true; stageAbsent = $true } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'cleanup.json')
}
