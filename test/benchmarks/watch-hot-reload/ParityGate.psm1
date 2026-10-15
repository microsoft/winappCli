# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Parity gate for the watch journeys: does the hot-reloaded app look and behave like a clean build of the same source?
#
# At a checkpoint the gate reads the live app's XAML tree through the DevTools protocol (VisualTree.query, authored
# elements only, with a fixed set of property values), runs the journey's probes (click a button, type into a box)
# and reads it again after each. Then it copies the app's source as it is now, builds it from scratch, launches that
# build, runs the same probes on it, and compares every reading: element by element, property by property. A value
# that differs means hot reload made the app behave differently from what the developer gets after a restart.
#
# Journey fields (all optional):
#   "parity": {
#     "auto": true,                 run with the runner's default -Parity Auto (otherwise only with -Parity On)
#     "root": "BindPage",           read from this element (a type or x:Name) down; default: the whole window
#     "probes": [ { "id", "target", "assert": "invoke" | "set-value" | "toggle-on" | "focus", "value" } ],
#                                   run on both apps, in order, with a reading after each
#     "navigate": [ ...probes ],    run on the clean build only, before reading (to reach the page the journey is on)
#     "prepare": [ ...probes ],     run on both apps before the first reading (a Reset button that undoes what the
#                                   journey's clicks built up, which the clean build never saw)
#     "enabled": false,             never run the gate for this journey (its point is state a clean launch can't have)
#     "reportedNotesCount": true,   a difference one of the save's notes reports ("note: <Element>[.<Property>] shows
#                                   '<hot>' here but '<clean>' after a restart: ...") doesn't fail the check; it is still
#                                   printed and counted as reported (the runner's -ParityAcceptReported does the same)
#     "properties": [ "Text", ... ],what to read on each element (default below)
#     "ignore": [ "*#CountText.Text" ], element paths or path.Property wildcards that may differ (event counters)
#     "replayInputs": false         don't give the clean build the text the journey typed (default: replay what is still shown)
#   }
# A checkpoint runs the gate with "parity": true, or { "probes": [...] } to override the journey's probes there. The
# runner also runs it after the last save whenever the gate is on.

$script:Ansi = [regex]'\x1B\[[0-?]*[ -/]*[@-~]'

# What the gate reads on every element. A property the element doesn't have is skipped.
$script:DefaultProperties = @(
    'Text', 'Content', 'Header', 'PlaceholderText', 'Visibility', 'IsEnabled', 'IsChecked', 'IsOn', 'Value',
    'SelectedIndex', 'FontSize', 'Opacity', 'Width', 'Height'
)

function Remove-Ansi([string]$Text) { if ($null -eq $Text) { return $null }; return $script:Ansi.Replace($Text, '') }

function ConvertTo-ShortType([string]$Type) {
    return $Type -replace '^Microsoft\.UI\.Xaml\.Controls\.Primitives\.', 'Primitives.' -replace '^Microsoft\.UI\.Xaml\.(Controls|Shapes|Documents)\.', ''
}

# Whether an element whose Visibility is bound (and unreadable) is laid out: a collapsed element measures to 0 x 0.
# Its actual and render sizes keep their last values after it collapses, so only the desired size tells.
function Get-LaidOutVisibility([string]$Winapp, [int]$ProcessId, [string]$Handle) {
    $raw = & $Winapp devtools call Layout.get "handle=$Handle" -a "$ProcessId" --json 2>&1 | Out-String
    try { $desired = (Remove-Ansi $raw | ConvertFrom-Json).result.desired } catch { return '(unreadable)' }
    if ($null -eq $desired) { return '(unreadable)' }
    return $(if ($desired.w -gt 0 -or $desired.h -gt 0) { 'Visible' } else { 'Collapsed' })
}

<#
.SYNOPSIS
  One reading of the app's authored XAML tree: an ordered map of element path to its type and property values.
.DESCRIPTION
  Elements are keyed by their path from the window: each step is the type, the x:Name when the author gave one, and an
  index among same-keyed siblings ("StackPanel#Root/ListView#ItemList/StackPanel/TextBlock[1]"). Names hot reload
  invents for its own bookkeeping (__hr_*) are not the author's and are left out. Returns $null when the app can't
  be read or the root isn't there yet.
#>
function Get-ParitySnapshot {
    param(
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$Root,
        [string]$RootNamespace,
        [string[]]$Properties = $script:DefaultProperties
    )
    $fields = ConvertTo-Json -Compress -InputObject @($Properties)
    $raw = & $Winapp devtools call VisualTree.query 'authored:=true' "fields:=$fields" -a "$ProcessId" --json 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { return $null }
    try { $result = (Remove-Ansi $raw | ConvertFrom-Json).result } catch { return $null }
    if (-not $result -or -not $result.ok) { return $null }
    $rows = @($result.matches)
    # The inside of the app's own controls (a UserControl, a templated control) counts whether or not DevTools could
    # attribute it to a source file: hot reload may load that markup from text, a clean build from compiled .xbf.
    if ($RootNamespace) {
        $known = [System.Collections.Generic.HashSet[string]]::new([string[]]@($rows | ForEach-Object handle))
        $parentOf = @{}; $typeOf = @{}
        foreach ($row in $rows) { $typeOf[$row.handle] = $row.type; if ($row.PSObject.Properties['parentHandle']) { $parentOf[$row.handle] = $row.parentHandle } }
        $nested = @($rows | Where-Object {
            if (-not $_.type.StartsWith("$RootNamespace.")) { return $false }
            # Inside another of the app's types: a control on a page. A page itself, under its Frame, is read as authored.
            for ($h = $parentOf[$_.handle]; $h; $h = $parentOf[$h]) { if ([string]$typeOf[$h] -like "$RootNamespace.*") { return $true } }
            return $false
        })
        foreach ($control in $nested) {
            $raw = & $Winapp devtools call VisualTree.query 'authored:=false' "rootHandle=$($control.handle)" "fields:=$fields" -a "$ProcessId" --json 2>&1 | Out-String
            try { $inner = (Remove-Ansi $raw | ConvertFrom-Json).result } catch { continue }
            foreach ($row in @($inner.matches)) { if ($row.PSObject.Properties['parentHandle'] -and $known.Add($row.handle)) { $rows += $row } }
        }
    }
    $byHandle = @{}
    $children = @{}
    foreach ($row in $rows) {
        $byHandle[$row.handle] = $row
        $parent = if ($row.PSObject.Properties['parentHandle'] -and $byHandle.ContainsKey($row.parentHandle)) { $row.parentHandle } else { '' }
        if (-not $children.ContainsKey($parent)) { $children[$parent] = [System.Collections.Generic.List[object]]::new() }
        $children[$parent].Add($row)
    }
    $starts = @($children[''])
    if ($Root) {
        $match = @($rows | Where-Object { $_.name -eq $Root -or $_.type -eq $Root -or $_.type.EndsWith(".$Root") } | Select-Object -First 1)
        if (-not $match) { return $null }
        $starts = $match
    }
    $snapshot = [ordered]@{}
    function Add-Node($Node, [string]$ParentPath, [string]$Page, [string]$PagePath, [string]$Segment) {
        $path = if ($ParentPath) { "$ParentPath/$Segment" } else { $Segment }
        if ($RootNamespace -and $Node.type.StartsWith("$RootNamespace.")) {
            $Page = $Node.type.Substring($RootNamespace.Length + 1)
            $PagePath = $path
        }
        $values = [ordered]@{}
        $unreadable = [System.Collections.Generic.List[string]]::new()
        foreach ($field in @($Node.fields)) {
            switch ([string]$field.valueState) {
                # 'binding' is a bound property's current value.
                { $_ -in 'value', 'binding' } { $values[$field.name] = [string]$field.value; break }
                'unset' { $values[$field.name] = '(unset)'; break }
                default {
                    if ($field.valueSource -eq 'Binding') {
                        # Bound, but the runtime holds no value it can show (a Binding that couldn't convert, say).
                        if ($field.name -eq 'Visibility') { $values['Visibility'] = Get-LaidOutVisibility $Winapp $ProcessId $Node.handle }
                        else { $unreadable.Add($field.name) }
                    }
                    # An object reference (a Frame's page, a Content element) is compared by its type.
                    elseif ($field.valueState -eq 'unresolved' -and $field.valueType) { $values[$field.name] = "<$($field.valueType)>" }
                }
            }
        }
        $element = if ($PagePath -and $path -ne $PagePath) { $path.Substring($PagePath.Length + 1) } elseif ($PagePath) { '(root)' } else { $path }
        $snapshot[$path] = [pscustomobject]@{ Path = $path; Page = $Page; Element = $element; Type = $Node.type; Values = $values; Unreadable = $unreadable }
        $counts = @{}
        $kids = @($children[$Node.handle] | Where-Object { $null -ne $_ })
        if ($Node.type -match '\.(ListView|GridView|ListBox|ItemsControl|ItemsRepeater|FlipView|ComboBox|TreeView)$') {
            # Item containers are realized and recycled by the list: a collapsed one holds no item, and their order in the
            # tree isn't the order on screen. Compare the items that are shown, ordered by what they show.
            $kids = @($kids | Where-Object { -not (Test-Collapsed $_) } | Sort-Object { Get-SubtreeText $_ })
        }
        foreach ($child in $kids) {
            $name = if ($child.name -and -not $child.name.StartsWith('__hr_')) { "#$($child.name)" } else { '' }
            $key = (ConvertTo-ShortType $child.type) + $name
            $index = [int]$counts[$key]
            $counts[$key] = $index + 1
            Add-Node $child $path $Page $PagePath ($key + $(if ($index -gt 0) { "[$index]" } else { '' }))
        }
    }
    function Test-Collapsed($Row) { return @($Row.fields | Where-Object { $_.name -eq 'Visibility' -and [string]$_.value -eq 'Collapsed' }).Count -gt 0 }
    function Get-SubtreeText($Row) {
        $own = @($Row.fields | Where-Object { $_.name -in 'Text', 'Content' -and $_.valueState -in 'value', 'binding' } | ForEach-Object { [string]$_.value })
        return (@($own) + @($children[$Row.handle] | Where-Object { $null -ne $_ } | ForEach-Object { Get-SubtreeText $_ })) -join '|'
    }
    foreach ($start in $starts) {
        $name = if ($start.name -and -not $start.name.StartsWith('__hr_')) { "#$($start.name)" } else { '' }
        Add-Node $start '' '' '' ((ConvertTo-ShortType $start.type) + $name)
    }
    return $snapshot
}

function ConvertTo-SnapshotText($Snapshot) {
    if ($null -eq $Snapshot) { return '' }
    return (@($Snapshot.Values | ForEach-Object { "$($_.Path) " + (($_.Values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';') }) -join "`n")
}

<#
.SYNOPSIS
  Reads the app until two readings in a row agree (the UI has settled), so a probe's effect is read once it's done
  rather than after a fixed delay. Returns the last reading; Stable is false if it never settled before the timeout.
#>
function Wait-ParitySnapshot {
    param(
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$Root,
        [string]$RootNamespace,
        [string[]]$Properties = $script:DefaultProperties,
        [int]$TimeoutSeconds = 20
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previous = $null
    $last = $null
    do {
        $last = Get-ParitySnapshot -Winapp $Winapp -ProcessId $ProcessId -Root $Root -RootNamespace $RootNamespace -Properties $Properties
        if ($null -ne $last) {
            $text = ConvertTo-SnapshotText $last
            if ($null -ne $previous -and $text -eq $previous) { return [pscustomobject]@{ Snapshot = $last; Stable = $true } }
            $previous = $text
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return [pscustomobject]@{ Snapshot = $last; Stable = $false }
}

<#
.SYNOPSIS
  Every difference between the hot-reloaded reading and the clean-build reading, as page / element / property:
  hot reload value vs clean-build value. An element only one side has is reported once, as "(element)".
#>
function Compare-ParitySnapshot {
    param([Parameter(Mandatory)][string]$Phase, $HotReload, $Clean, [string[]]$Ignore = @())
    $diffs = [System.Collections.Generic.List[object]]::new()
    function Add($Node, [string]$Property, $HotValue, $CleanValue, [switch]$Unread) {
        foreach ($pattern in $Ignore) { if ("$($Node.Path).$Property" -like $pattern -or $Node.Path -like $pattern) { return } }
        $diffs.Add([pscustomobject]@{
            Phase = $Phase; Page = $(if ($Node.Page) { $Node.Page } else { '(window)' }); Element = $Node.Element; Path = $Node.Path
            Property = $Property; HotReload = $HotValue; Clean = $CleanValue; Unreadable = [bool]$Unread
        })
    }
    if ($null -eq $HotReload -or $null -eq $Clean) {
        Add ([pscustomobject]@{ Path = '(root)'; Element = '(root)' }) '(readable)' $(if ($null -eq $HotReload) { 'not found' } else { 'found' }) $(if ($null -eq $Clean) { 'not found' } else { 'found' })
        return , $diffs
    }
    foreach ($path in $HotReload.Keys) {
        $hotNode = $HotReload[$path]
        if (-not $Clean.Contains($path)) {
            # Report the topmost missing element only: its children are missing because it is.
            $parent = $path.Substring(0, [Math]::Max(0, $path.LastIndexOf('/')))
            if ($parent -and -not $Clean.Contains($parent)) { continue }
            Add $hotNode '(element)' 'present' 'missing'
            continue
        }
        $cleanNode = $Clean[$path]
        if ($hotNode.Type -ne $cleanNode.Type) { Add $hotNode '(type)' $hotNode.Type $cleanNode.Type }
        foreach ($name in @($hotNode.Values.Keys) + @($cleanNode.Values.Keys | Where-Object { -not $hotNode.Values.Contains($_) })) {
            if ($hotNode.Unreadable -contains $name -or $cleanNode.Unreadable -contains $name) {
                # Bound on one side and unreadable there: not comparable. Kept as a note.
                $h = if ($hotNode.Values.Contains($name)) { $hotNode.Values[$name] } else { '(bound, unreadable)' }
                $c = if ($cleanNode.Values.Contains($name)) { $cleanNode.Values[$name] } else { '(bound, unreadable)' }
                Add $hotNode $name $h $c -Unread
                continue
            }
            $h = if ($hotNode.Values.Contains($name)) { $hotNode.Values[$name] } else { '(not set)' }
            $c = if ($cleanNode.Values.Contains($name)) { $cleanNode.Values[$name] } else { '(not set)' }
            if ($h -cne $c) { Add $hotNode $name $h $c }
        }
    }
    foreach ($path in $Clean.Keys) {
        if ($HotReload.Contains($path)) { continue }
        $parent = $path.Substring(0, [Math]::Max(0, $path.LastIndexOf('/')))
        if ($parent -and -not $HotReload.Contains($parent)) { continue }
        Add $Clean[$path] '(element)' 'missing' 'present'
    }
    return , $diffs
}

function Format-ParityDiff($Diffs) {
    return (@($Diffs | ForEach-Object {
        "  [$($_.Phase)] $($_.Page) / $($_.Element) / $($_.Property): hot reload '$($_.HotReload)' vs clean build '$($_.Clean)'"
    }) -join "`n")
}

<#
.SYNOPSIS
  Reads the app before and after each probe. A probe is a uiCheck-shaped action ({ target, assert: invoke | set-value |
  toggle-on, value }) run through the journey runner's own mapping, so it is driven exactly like the journey's checks.
#>
function Read-ParityPhases {
    param(
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][scriptblock]$InvokeProbe,
        $Probes,
        $Prepare,
        [string]$Root,
        [string]$RootNamespace,
        [string[]]$Properties,
        [int]$TimeoutSeconds
    )
    $phases = [ordered]@{}
    $notes = [System.Collections.Generic.List[string]]::new()
    foreach ($step in @($Prepare)) {
        if ($null -eq $step) { continue }
        $outcome = & $InvokeProbe $step $ProcessId
        if (-not $outcome.Ok) { $notes.Add("prepare $($step.assert) $($step.target): the step failed: $(Remove-Ansi $outcome.Output)") }
    }
    $read = Wait-ParitySnapshot -Winapp $Winapp -ProcessId $ProcessId -Root $Root -RootNamespace $RootNamespace -Properties $Properties -TimeoutSeconds $TimeoutSeconds
    if (-not $read.Stable) { $notes.Add('initial: the UI did not settle') }
    $phases['initial'] = $read.Snapshot
    $i = 0
    foreach ($probe in @($Probes)) {
        if ($null -eq $probe) { continue }
        $i++
        $label = if ($probe.id) { [string]$probe.id } else { "probe$i $($probe.assert) $($probe.target)" }
        $outcome = & $InvokeProbe $probe $ProcessId
        if (-not $outcome.Ok) { $notes.Add("${label}: the probe failed: $(Remove-Ansi $outcome.Output)") }
        $read = Wait-ParitySnapshot -Winapp $Winapp -ProcessId $ProcessId -Root $Root -RootNamespace $RootNamespace -Properties $Properties -TimeoutSeconds $TimeoutSeconds
        if (-not $read.Stable) { $notes.Add("${label}: the UI did not settle") }
        $phases["after $label"] = $read.Snapshot
    }
    return [pscustomobject]@{ Phases = $phases; Notes = $notes }
}

<#
.SYNOPSIS
  Copies the app's source as it is now (no bin/obj) next to the run, with the shared build files from its parent
  folder, so the clean build compiles exactly what the hot-reloaded app was edited into.
#>
function Copy-ParitySource {
    param([Parameter(Mandatory)][string]$AppDir, [Parameter(Mandatory)][string]$Destination)
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    $target = Join-Path $Destination (Split-Path $AppDir -Leaf)
    $null = New-Item -ItemType Directory -Path $target -Force
    $null = robocopy $AppDir $target /E /XD bin obj AppX .vs /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    if ($LASTEXITCODE -ge 8) { throw "Copying $AppDir failed (robocopy $LASTEXITCODE)." }
    foreach ($shared in 'Directory.Build.props', 'Directory.Build.targets', 'nuget.config', 'global.json') {
        $file = Join-Path (Split-Path $AppDir) $shared
        if (Test-Path $file) { Copy-Item $file $Destination }
    }
    # A solution next to the app (class libraries the app references) builds with it.
    foreach ($sibling in Get-ChildItem (Split-Path $AppDir) -Directory | Where-Object { $_.FullName -ne $AppDir -and (Get-ChildItem $_.FullName -Filter *.csproj -ErrorAction SilentlyContinue) }) {
        $null = robocopy $sibling.FullName (Join-Path $Destination $sibling.Name) /E /XD bin obj /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    }
    $global:LASTEXITCODE = 0
    return $target
}

<#
.SYNOPSIS
  The journey's earlier inputs (set-value, toggle-on) the hot-reloaded app still shows, to give the clean build the same
  input. One it no longer shows (a later markup edit replaced the typed text, the element is gone) is not replayed.
#>
function Select-ShownInputs($Inputs, $Snapshot) {
    if ($null -eq $Snapshot) { return }
    foreach ($entry in @($Inputs)) {
        if ($null -eq $entry) { continue }
        $node = @($Snapshot.Values | Where-Object { $_.Path.EndsWith("#$($entry.target)") } | Select-Object -First 1)
        if (-not $node) { continue }
        $shown = switch ([string]$entry.assert) {
            'set-value' { $node[0].Values['Text'] -ceq [string]$entry.value }
            'toggle-on' { $node[0].Values['IsChecked'] -eq 'True' -or $node[0].Values['IsOn'] -eq 'True' }
            default { $false }
        }
        if ($shown) { $entry }
    }
}

# The first build error as "WMC9999: message" (the compiler's code and text, without the path in front), else winapp's
# own error, else that no app process was reported.
function Get-CleanBuildError([string[]]$Lines) {
    foreach ($line in $Lines) {
        if ($line -match '\berror\s+([A-Z]+\d+)\s*:\s*(.+?)(\s+\[[^\]]+\])?\s*$') { $text = "$($Matches[1]): $($Matches[2])"; return $(if ($text.Length -gt 220) { $text.Substring(0, 217) + '...' } else { $text }) }
    }
    $winapp = @($Lines | Where-Object { $_ -match '"Error":\s*"([^"]+)"' } | Select-Object -First 1)
    if ($winapp -and $winapp[0] -match '"Error":\s*"([^"]+)"') { return $Matches[1] }
    return 'winapp run reported no app process'
}

<#
.SYNOPSIS
  The parity check for one checkpoint: reads the hot-reloaded app, builds and launches the same source from scratch,
  reads that too, and compares. Returns { Ok, Output, Diffs, Seconds } shaped like a journey check.
#>
function Invoke-ParityCheck {
    param(
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$AppDir,
        [Parameter(Mandatory)][string]$WorkDir,
        [Parameter(Mandatory)][scriptblock]$InvokeProbe,
        $Config,
        [string]$RootNamespace,
        [string]$Configuration,
        [ValidateSet('Unpackaged', 'Packaged')][string]$Packaging = 'Unpackaged',
        [int]$TimeoutSeconds = 20,
        [int]$BuildTimeoutSeconds = 900,
        # What winapp said with the save ("note: <Element>[.<Property>] shows '<hot>' here but '<clean>' after a restart: ...").
        [string[]]$ReportedNotes = @(),
        # A difference a note reported doesn't fail the check (it is still printed, and counted as reported).
        [switch]$AcceptReported
    )
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $root = if ($Config.root) { [string]$Config.root } else { $null }
    $properties = if ($Config.properties) { @($Config.properties) } else { $script:DefaultProperties }
    $ignore = @($Config.ignore | Where-Object { $_ })
    $common = @{ Winapp = $Winapp; InvokeProbe = $InvokeProbe; Probes = $Config.probes; Prepare = $Config.prepare; Root = $root; RootNamespace = $RootNamespace; Properties = $properties; TimeoutSeconds = $TimeoutSeconds }

    $hot = Read-ParityPhases @common -ProcessId $ProcessId
    $hotSeconds = $clock.Elapsed.TotalSeconds
    $replay = @(Select-ShownInputs -Inputs $Config.replay -Snapshot $hot.Phases['initial'])

    $source = Copy-ParitySource -AppDir $AppDir -Destination $WorkDir
    # Test hook: break the clean build on purpose, to see the gate fail when there is no clean build to compare with.
    if ($env:WINAPP_PARITY_TEST_BREAK_CLEAN_BUILD -eq '1') { Add-Content (Join-Path $source 'ParityGateBrokenOnPurpose.cs') 'this is not C# (WINAPP_PARITY_TEST_BREAK_CLEAN_BUILD)' }
    $runArguments = @('run', $source, '--devtools', 'headless', '--detach', '--json')
    if ($Configuration) { $runArguments += @('-c', $Configuration) }
    # Its own identity, so registering the clean build doesn't replace the hot-reloaded app's package.
    if ($Packaging -eq 'Packaged') { $runArguments += '--unique-identity' }
    $log = Join-Path $WorkDir 'clean-run.log'
    $runner = Start-Process -FilePath $Winapp -ArgumentList $runArguments -WorkingDirectory $source -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
    if (-not $runner.WaitForExit($BuildTimeoutSeconds * 1000)) {
        Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Ok = $false; CleanBuildFailed = $true; Output = "parity: clean build failed: it did not launch within $BuildTimeoutSeconds s (log: $log)"; Diffs = @(); Seconds = [int]$clock.Elapsed.TotalSeconds }
    }
    $buildSeconds = $clock.Elapsed.TotalSeconds - $hotSeconds
    $runText = Remove-Ansi ((Get-Content $log -Raw -ErrorAction SilentlyContinue) + (Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue))
    $cleanPid = if ($runText -match '"ProcessId":\s*(\d+)') { [int]$Matches[1] } else { $null }
    if (-not $cleanPid) {
        # Without a clean build there is nothing to compare with: the check fails, never passes for lack of differences.
        $lines = @($runText -split "`r?`n" | Where-Object { $_.Trim() })
        $reason = Get-CleanBuildError $lines
        $tail = ($lines | Select-Object -Last 15) -join "`n"
        return [pscustomobject]@{ Ok = $false; CleanBuildFailed = $true; Output = "parity: clean build failed: $reason`n  (log: $log)`n$tail"; Diffs = @(); Seconds = [int]$clock.Elapsed.TotalSeconds }
    }
    try {
        foreach ($step in @($Config.navigate) + $replay) {
            if ($null -eq $step) { continue }
            $null = & $InvokeProbe $step $cleanPid
        }
        $clean = Read-ParityPhases @common -ProcessId $cleanPid
    }
    finally {
        Stop-Process -Id $cleanPid -Force -ErrorAction SilentlyContinue
        if ($Packaging -eq 'Packaged') {
            # The clean build's identity is unique to its folder, so each check would leave one more registration behind.
            Start-Sleep -Milliseconds 500
            $null = & $Winapp unregister $source --quiet 2>&1
        }
    }

    $diffs = [System.Collections.Generic.List[object]]::new()
    foreach ($phase in $hot.Phases.Keys) {
        foreach ($d in (Compare-ParitySnapshot -Phase $phase -HotReload $hot.Phases[$phase] -Clean $clean.Phases[$phase] -Ignore $ignore)) { $diffs.Add($d) }
    }
    $unread = @($diffs | Where-Object Unreadable)
    $diffs = [System.Collections.Generic.List[object]]@($diffs | Where-Object { -not $_.Unreadable })
    $notes = @($hot.Notes | ForEach-Object { "hot reload $_" }) + @($clean.Notes | ForEach-Object { "clean build $_" }) +
        @($replay | ForEach-Object { "clean build given the journey's earlier input: $($_.assert) $($_.target) $($_.value)".TrimEnd() }) +
        @($unread | Group-Object Page, Element, Property | ForEach-Object {
            $first = $_.Group[0]
            "not compared (bound, value unreadable): $($first.Page) / $($first.Element) / $($first.Property): hot reload '$($first.HotReload)' vs clean build '$($first.Clean)'"
        })
    $elements = @($hot.Phases['initial'].Keys).Count
    $timing = "hot reload read {0:n1} s, clean build+launch {1:n0} s, total {2:n0} s" -f $hotSeconds, $buildSeconds, $clock.Elapsed.TotalSeconds
    # Differences hot reload told the developer about, matched by element (x:Name), property and both values.
    $reported = [System.Collections.Generic.List[object]]::new()
    if ($AcceptReported -or $Config.reportedNotesCount) {
        $split = Split-ReportedDifferences $diffs $ReportedNotes
        $diffs = $split.Silent
        $reported = $split.Reported
    }
    $header = if ($diffs.Count -eq 0 -and $reported.Count -eq 0) {
        "parity: hot reload matches a clean build ($elements elements, $($hot.Phases.Count) readings; $timing)"
    } elseif ($diffs.Count -eq 0) {
        "parity: $($reported.Count) difference(s) from a clean build, all reported by hot reload ($elements elements, $($hot.Phases.Count) readings; $timing)"
    } else {
        "parity: $($diffs.Count) difference(s) between hot reload and a clean build$(if ($reported.Count) { ", and $($reported.Count) reported by hot reload" }) ($elements elements, $($hot.Phases.Count) readings; $timing)"
    }
    $text = @($header) + @($notes | ForEach-Object { "  note: $_" })
    if ($diffs.Count) { $text += Format-ParityDiff $diffs }
    if ($reported.Count) { $text += '  reported by hot reload:'; $text += Format-ParityDiff $reported }
    [pscustomobject]@{ HotReload = $hot.Phases; Clean = $clean.Phases; Diffs = $diffs; Reported = $reported; Notes = $notes } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $WorkDir 'parity.json')
    return [pscustomobject]@{ Ok = ($diffs.Count -eq 0); Output = ($text -join "`n"); Diffs = $diffs; Reported = $reported; Seconds = [int]$clock.Elapsed.TotalSeconds }
}

<#
.SYNOPSIS
  The differences winapp's notes say a save leaves ("note: <Element>[.<Property>] shows '<hot>' here but '<clean>' after a
  restart: <cause>"), as { Element, Property, Hot, Clean }.
#>
function Get-ReportedDifferences([string[]]$Notes) {
    foreach ($note in @($Notes)) {
        $text = Remove-Ansi $note
        if ($text -match "^\s*(note:\s*)?(?<element>[A-Za-z_][\w]*)(\.(?<property>[A-Za-z_]\w*))?\s+shows\s+'(?<hot>.*?)'\s+here\s+but\s+'(?<clean>.*?)'\s+after\s+a\s+restart") {
            [pscustomobject]@{ Kind = 'value'; Element = $Matches.element; Property = $Matches.property; Hot = $Matches.hot; Clean = $Matches.clean }
        }
        elseif ($text -match "^\s*(note:\s*)?\d+\s+elements?\s+under\s+(?<parent>[A-Za-z_]\w*)\b.*?\s+(is|are)\s+missing\s+here\s+but\s+shown\s+after\s+a\s+restart") {
            [pscustomobject]@{ Kind = 'missing-under'; Element = $Matches.parent }
        }
        elseif ($text -match "^\s*(note:\s*)?(?<element>[A-Za-z_]\w*)\s+is\s+missing\s+here\s+but\s+shown\s+after\s+a\s+restart") {
            [pscustomobject]@{ Kind = 'missing'; Element = $Matches.element }
        }
    }
}

# How a note and the gate each write "no value": the note says null, the gate (not set) or (unset).
function ConvertTo-ComparableValue([string]$Value) { if ($Value -in 'null', '(not set)', '(unset)') { return '<no value>' }; return $Value }

<#
.SYNOPSIS
  Splits parity differences into those a note reported and silent ones. Returns { Silent, Reported }. A note covers:
  - "<X>[.<Property>] shows '<hot>' here but '<clean>' after a restart": the difference on the element named X (or on
    the page itself, written <PageType>.<Property>), that property if named, with both values;
  - "<X>.Items shows 'n' here but 'm' after a restart": item elements missing under X (n < m) or extra there (n > m);
  - "N elements under <Parent> (...) are missing here but shown after a restart": elements missing under Parent;
  - "<X> is missing here but shown after a restart": the element named X, missing.
#>
function Split-ReportedDifferences($Diffs, [string[]]$Notes) {
    $said = @(Get-ReportedDifferences $Notes)
    $silent = [System.Collections.Generic.List[object]]::new()
    $reported = [System.Collections.Generic.List[object]]::new()
    foreach ($d in @($Diffs)) {
        if ($null -eq $d) { continue }
        $path = [string]$d.Element
        $name = if ($path -match '#([^/#\[]+)(\[\d+\])?$') { $Matches[1] } else { $null }
        $isElement = $d.Property -eq '(element)'
        $match = @($said | Where-Object {
            $note = $_
            switch ($note.Kind) {
                'value' {
                    if ($note.Property -eq 'Items') {
                        # The list's item elements: missing here when it shows fewer, extra when it shows more.
                        $fewer = [int]("0$($note.Hot)" -replace '\D', '') -lt [int]("0$($note.Clean)" -replace '\D', '')
                        $isElement -and $path -match "#$([regex]::Escape($note.Element))(\[\d+\])?/" -and
                            $d.HotReload -eq $(if ($fewer) { 'missing' } else { 'present' })
                    } else {
                        $onElement = ($name -and $note.Element -eq $name) -or ($path -eq '(root)' -and $note.Element -eq $d.Page)
                        $onElement -and (-not $note.Property -or $note.Property -eq $d.Property) -and
                            (ConvertTo-ComparableValue $note.Hot) -ceq (ConvertTo-ComparableValue ([string]$d.HotReload)) -and
                            (ConvertTo-ComparableValue $note.Clean) -ceq (ConvertTo-ComparableValue ([string]$d.Clean))
                    }
                }
                'missing-under' { $isElement -and $d.HotReload -eq 'missing' -and $path -match "#$([regex]::Escape($note.Element))(\[\d+\])?/" }
                'missing' { $isElement -and $d.HotReload -eq 'missing' -and $name -eq $note.Element }
            }
        }).Count
        if ($match) { $reported.Add($d) } else { $silent.Add($d) }
    }
    return [pscustomobject]@{ Silent = $silent; Reported = $reported }
}

Export-ModuleMember -Function Get-CleanBuildError, Get-ReportedDifferences, Split-ReportedDifferences, Get-ParitySnapshot, Wait-ParitySnapshot, Compare-ParitySnapshot, Format-ParityDiff, Invoke-ParityCheck, Remove-Ansi
