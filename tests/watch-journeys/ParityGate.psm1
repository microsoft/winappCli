# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# The parity gate: does the hot-reloaded app look and behave like a clean build of the same source?
#
# At a checkpoint the gate reads the live app's authored XAML tree through DevTools (VisualTree.query, a fixed set of
# property values per element), runs the journey's probes (click, type) and reads again after each. Then it copies the
# source as it is now, builds and launches it from scratch, does the same, and compares every reading element by
# element and property by property. Any difference means hot reload left the app different from what the developer
# gets after a restart.
#
# Journey fields (all optional): "parity": { "root": "ContactsPage" (a type or x:Name to read from; default the window),
#   "probes": [ { "assert": "invoke" | "set-value", "target", "value" } ], "ignore": [ "*#CountText.Text" ],
#   "properties": [ "Text", ... ] }

Import-Module (Join-Path $PSScriptRoot 'WatchJourney.psm1')

$script:DefaultProperties = @('Text', 'Content', 'Header', 'PlaceholderText', 'Visibility', 'IsEnabled', 'IsChecked', 'IsOn',
    'SelectedIndex', 'FontSize', 'Opacity', 'Width', 'Height', 'Margin', 'Padding')

function Invoke-TreeQuery([string]$Winapp, [int]$ProcessId, [string[]]$Parameters) {
    # A handle can be briefly unreadable while the UI thread is busy: one retry.
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $raw = & $Winapp devtools call VisualTree.query @Parameters -a "$ProcessId" --json 2>&1 | Out-String
        try { $result = (Remove-Ansi $raw | ConvertFrom-Json).result } catch { $result = $null }
        if ($result -and $result.ok) { return $result }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

<#
.SYNOPSIS
  One reading of the app's authored XAML tree: an ordered map of element path to its type and property values, or $null.
  A path is each ancestor's type, its x:Name when it has one, and an index among same-keyed siblings
  ("Grid#Root/StackPanel#HeaderPanel/TextBlock#Header"). List items are ordered by what they show, not by container.
#>
function Get-ParitySnapshot {
    param([string]$Winapp, [int]$ProcessId, [string]$Root, [string[]]$Properties)
    $fields = ConvertTo-Json -Compress -InputObject @($Properties)
    $scope = @('authored:=true', "fields:=$fields")
    if ($Root) {
        $found = Invoke-TreeQuery $Winapp $ProcessId @("ofType=$Root", 'authored:=false')
        $rootRow = @($found.matches | Select-Object -First 1)
        if (-not $rootRow) {
            $found = Invoke-TreeQuery $Winapp $ProcessId @("text=$Root", 'authored:=false')
            $rootRow = @($found.matches | Where-Object name -eq $Root | Select-Object -First 1)
        }
        if (-not $rootRow) { return $null }
        $scope += "rootHandle=$($rootRow[0].handle)"
    }
    $result = Invoke-TreeQuery $Winapp $ProcessId $scope
    if (-not $result) { return $null }
    if (-not $result.complete) { throw "The tree query stopped at its $($result.truncatedBy) budget: set the journey's parity root to read less." }
    $rows = @($result.matches)
    $children = @{}
    $handles = [System.Collections.Generic.HashSet[string]]::new([string[]]@($rows | ForEach-Object handle))
    foreach ($row in $rows) {
        $parent = if ($row.PSObject.Properties['parentHandle'] -and $handles.Contains([string]$row.parentHandle)) { [string]$row.parentHandle } else { '' }
        if (-not $children.ContainsKey($parent)) { $children[$parent] = [System.Collections.Generic.List[object]]::new() }
        $children[$parent].Add($row)
    }
    $snapshot = [ordered]@{}
    function Get-Values($Row) {
        $values = [ordered]@{}
        foreach ($field in @($Row.fields)) {
            if ($field.valueState -in 'value', 'binding') { $values[$field.name] = [string]$field.value }
            elseif ($field.valueState -eq 'unset') { $values[$field.name] = '(unset)' }
            elseif ($field.valueType) { $values[$field.name] = "<$($field.valueType)>" }
        }
        return $values
    }
    function Get-Text($Row) {
        return (@((Get-Values $Row).Values) + @($children[[string]$Row.handle] | Where-Object { $_ } | ForEach-Object { Get-Text $_ })) -join '|'
    }
    function Add-Node($Row, [string]$Path) {
        $snapshot[$Path] = [pscustomobject]@{ Path = $Path; Type = $Row.type; Values = (Get-Values $Row) }
        $kids = @($children[[string]$Row.handle] | Where-Object { $_ })
        if ($Row.type -match '\.(ListView|GridView|ItemsControl|ItemsRepeater|ComboBox)$') { $kids = @($kids | Sort-Object { Get-Text $_ }) }
        $counts = @{}
        foreach ($child in $kids) {
            $key = ($child.type -replace '^Microsoft\.UI\.Xaml\.(Controls\.Primitives|Controls|Shapes|Documents)\.', '') + $(if ($child.name) { "#$($child.name)" } else { '' })
            $index = [int]$counts[$key]; $counts[$key] = $index + 1
            Add-Node $child "$Path/$key$(if ($index) { "[$index]" })"
        }
    }
    foreach ($top in @($children[''] | Where-Object { $_ })) { Add-Node $top ($top.type + $(if ($top.name) { "#$($top.name)" } else { '' })) }
    return $snapshot
}

# Reads until two readings in a row agree (the UI has settled).
function Wait-ParitySnapshot([string]$Winapp, [int]$ProcessId, [string]$Root, [string[]]$Properties, [int]$TimeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previous = $null
    do {
        $snapshot = Get-ParitySnapshot $Winapp $ProcessId $Root $Properties
        $text = if ($snapshot) { ($snapshot.Values | ForEach-Object { "$($_.Path) $(($_.Values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';')" }) -join "`n" }
        if ($snapshot -and $text -eq $previous) { return $snapshot }
        $previous = $text
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $snapshot
}

# Every difference between two readings, as "path.Property: hot reload 'x' vs clean build 'y'".
function Compare-ParitySnapshot([string]$Phase, $HotReload, $Clean, [string[]]$Ignore) {
    if ($null -eq $HotReload -or $null -eq $Clean) { return @("[$Phase] the tree could not be read: hot reload $(if ($HotReload) { 'ok' } else { 'unreadable' }), clean build $(if ($Clean) { 'ok' } else { 'unreadable' })") }
    $diffs = [System.Collections.Generic.List[string]]::new()
    $ignored = { param([string]$Key) @($Ignore | Where-Object { $Key -like $_ }).Count -gt 0 }
    foreach ($path in @($HotReload.Keys) + @($Clean.Keys | Where-Object { -not $HotReload.Contains($_) })) {
        if (& $ignored $path) { continue }
        $hotNode = $HotReload[$path]; $cleanNode = $Clean[$path]
        if (-not $hotNode -or -not $cleanNode) {
            # Only the topmost missing element: its children are missing because it is.
            $parent = $path.Substring(0, [Math]::Max(0, $path.LastIndexOf('/')))
            if (-not $parent -or ($HotReload.Contains($parent) -and $Clean.Contains($parent))) { $diffs.Add("[$Phase] $path`: $(if ($hotNode) { 'only in hot reload' } else { 'only in the clean build' })") }
            continue
        }
        foreach ($name in @($hotNode.Values.Keys) + @($cleanNode.Values.Keys | Where-Object { -not $hotNode.Values.Contains($_) })) {
            if (& $ignored "$path.$name") { continue }
            if ([string]$hotNode.Values[$name] -cne [string]$cleanNode.Values[$name]) { $diffs.Add("[$Phase] $path.$name`: hot reload '$($hotNode.Values[$name])' vs clean build '$($cleanNode.Values[$name])'") }
        }
    }
    return $diffs.ToArray()
}

function Read-ParityPhases([string]$Winapp, [int]$ProcessId, $Config, [string[]]$Properties, [scriptblock]$InvokeProbe) {
    $phases = [ordered]@{ initial = (Wait-ParitySnapshot $Winapp $ProcessId $Config.root $Properties) }
    $i = 0
    foreach ($probe in @($Config.probes | Where-Object { $_ })) {
        $i++
        $null = & $InvokeProbe $probe $ProcessId
        $phases["after probe $i ($($probe.assert) $($probe.target))"] = Wait-ParitySnapshot $Winapp $ProcessId $Config.root $Properties
    }
    return $phases
}

<#
.SYNOPSIS
  The parity check for one checkpoint. Returns { Ok, Output } like a journey check. A clean build that fails is a failed
  check: there is nothing to compare with.
#>
function Invoke-ParityCheck {
    param(
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][int]$ProcessId,
        # The journey's source root (the app project and the libraries beside it) and the app project under it.
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$WorkDir,
        [Parameter(Mandatory)][scriptblock]$InvokeProbe,
        $Config,
        [switch]$Packaged
    )
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $Config) { $Config = [pscustomobject]@{} }
    $properties = if ($Config.properties) { @($Config.properties) } else { $script:DefaultProperties }
    $hot = Read-ParityPhases $Winapp $ProcessId $Config $properties $InvokeProbe

    $copy = Join-Path $WorkDir 'src'
    $null = robocopy $SourceRoot $copy /E /XD bin obj AppX .vs /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    $global:LASTEXITCODE = 0
    $runArguments = @('run', (Join-Path $copy $Project), '--devtools', 'headless', '--detach', '--json') + $(if ($Packaged) { @('--unique-identity') } else { @() })
    $log = Join-Path $WorkDir 'clean-run.log'
    $runner = Start-Process -FilePath $Winapp -ArgumentList $runArguments -WorkingDirectory $copy -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
    if (-not $runner.WaitForExit(900000)) { Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue }
    $text = Remove-Ansi ((Get-Content $log -Raw -ErrorAction SilentlyContinue) + (Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue))
    if ($text -notmatch '"ProcessId":\s*(\d+)') {
        $firstError = @($text -split "`r?`n" | Where-Object { $_ -match '\berror\b' } | Select-Object -First 1)
        return [pscustomobject]@{ Ok = $false; Output = "parity: the clean build failed: $firstError (log: $log)" }
    }
    $cleanPid = [int]$Matches[1]
    try {
        foreach ($step in @($Config.navigate | Where-Object { $_ })) { $null = & $InvokeProbe $step $cleanPid }
        $clean = Read-ParityPhases $Winapp $cleanPid $Config $properties $InvokeProbe
    }
    finally {
        Stop-Process -Id $cleanPid -Force -ErrorAction SilentlyContinue
        if ($Packaged) { Start-Sleep -Milliseconds 500; $null = & $Winapp unregister (Join-Path $copy $Project) --quiet 2>&1 }
    }
    $diffs = @(foreach ($phase in $hot.Keys) { Compare-ParitySnapshot $phase $hot[$phase] $clean[$phase] @($Config.ignore) })
    [pscustomobject]@{ HotReload = $hot; Clean = $clean; Diffs = $diffs } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $WorkDir 'parity.json')
    $elements = @($hot['initial'].Keys).Count
    $header = if ($diffs) { "parity: $($diffs.Count) difference(s) from a clean build" } else { "parity: matches a clean build ($elements elements, $($hot.Count) readings)" }
    return [pscustomobject]@{ Ok = (-not $diffs); Output = ((@("$header, $([int]$clock.Elapsed.TotalSeconds) s") + @($diffs | ForEach-Object { "  $_" })) -join "`n") }
}

Export-ModuleMember -Function Get-ParitySnapshot, Compare-ParitySnapshot, Invoke-ParityCheck
