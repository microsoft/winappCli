# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Lean runner helpers for the authored-edit Todo journey against 'winapp run --watch'.

$script:Utf8 = [System.Text.UTF8Encoding]::new($false)

function ConvertTo-JourneyText($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value -replace "`r`n", "`n" }
    return (@($Value) | ForEach-Object { [string]$_ }) -join "`n"
}

function Get-JourneyPlan {
    param([Parameter(Mandatory)]$Journey, [Parameter(Mandatory)][string]$VariantId)
    $variant = $Journey.variants | Where-Object id -eq $VariantId
    if (-not $variant) { throw "Unknown variant '$VariantId'." }
    $plan = [System.Collections.Generic.List[object]]::new()
    foreach ($segment in $Journey.segments) {
        $actions = if ($segment.kind -eq 'swap') {
            $choice = [string]$variant.choices.($segment.dimension)
            @($segment.orders.$choice | ForEach-Object { $id = $_; $segment.actions | Where-Object id -eq $id })
        } else { @($segment.actions) }
        for ($i = 0; $i -lt $actions.Count; $i++) {
            $checkpoint = $actions[$i].checkpoint
            if (-not $checkpoint -and $i -eq $actions.Count - 1) { $checkpoint = $segment.checkpoint }
            $plan.Add([pscustomobject]@{ Index = $plan.Count + 1; Action = $actions[$i]; Checkpoint = $checkpoint })
        }
    }
    return , $plan
}

function Set-JourneyFile([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    for ($attempt = 0; ; $attempt++) {
        try { [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8); return }
        catch [System.IO.IOException] { if ($attempt -ge 20) { throw }; Start-Sleep -Milliseconds 100 }
    }
}

function Assert-OneMatch([string]$Content, [string]$Needle, $Action) {
    $count = ([regex]::Matches($Content, [regex]::Escape($Needle))).Count
    if ($count -ne 1) { throw "Action '$($Action.id)': expected one match, found $count." }
}

<#
.SYNOPSIS
  What kind of save an action is, for latency per kind: "xaml", "cs", "both" (XAML and C# in one save), "new type" (a
  file created), "package" (a PackageReference added or changed), "project" or "other". An action's "kind" wins.
#>
function Get-JourneySaveKind($Action) {
    if ($Action.kind) { return [string]$Action.kind }
    $edits = if ($Action.edits) { @($Action.edits) } else { @($Action) }
    $files = @($edits | ForEach-Object { [string]$_.file })
    if (@($edits | Where-Object { $_.file -like '*.*proj' -and ((ConvertTo-JourneyText $_.edit.text) -match 'PackageReference') }).Count) { return 'package' }
    if (@($edits | Where-Object { $_.edit.op -eq 'create-file' -and $_.file -match '\.(cs|xaml)$' }).Count) { return 'new type' }
    $xaml = @($files | Where-Object { $_ -like '*.xaml' }).Count
    $cs = @($files | Where-Object { $_ -like '*.cs' }).Count
    if ($xaml -and $cs) { return 'both' }
    if ($xaml) { return 'xaml' }
    if ($cs) { return 'cs' }
    if (@($files | Where-Object { $_ -like '*.*proj' }).Count) { return 'project' }
    return 'other'
}

<#
.SYNOPSIS
  Why a save restarted, failed or waited, in one line: the diagnostic codes and their counts ("ENC0020 x5, ENC0033"),
  or the first line winapp printed for it.
#>
function Get-JourneyOutcomeReason([string]$Detail, [string]$Outcome) {
    if ($Outcome -eq 'timeout') { return 'no outcome before the timeout' }
    $lines = @($Detail -split "`n")
    $start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match "^\[watch\] $Outcome\b" })
    if ($start -lt 0) { $start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^\[watch\] (restart|error|waiting)\b' }) }
    # A crash between saves: the app exited (with the exception it died of), and the next save relaunched it.
    $exited = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^\[watch\] (the app exited|lost the connection)' })
    if ($exited -ge 0 -and ($start -lt 0 -or $lines[$start] -match 'lost the connection')) {
        $code = if ($lines[$exited] -match 'exited with code (-?\d+)') { " $($Matches[1])" } else { '' }
        $exception = @($lines | Select-Object -Skip ($exited + 1) | Where-Object { $_ -match '^\s+[\w.]+(Exception|Error)\b' } | Select-Object -First 1)
        $what = if ($exception) { ': ' + (($exception[0].Trim() -replace '\s+', ' ') -replace '^(.{0,110}).*$', '$1') } else { '' }
        return "the app crashed (exit$code)$what"
    }
    if ($start -lt 0) { return '' }
    $block = [System.Collections.Generic.List[string]]::new()
    $block.Add($lines[$start])
    for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -match '^  '; $i++) { $block.Add($lines[$i]) }
    $codes = [regex]::Matches(($block -join "`n"), '\b(ENC\d{4}|CS\d{4}|WMC\d{4}|XLS\d{4})\b') | ForEach-Object { $_.Value }
    if ($codes) {
        return (@($codes | Group-Object | Sort-Object Count -Descending | ForEach-Object { if ($_.Count -gt 1) { "$($_.Name) x$($_.Count)" } else { $_.Name } }) -join ', ')
    }
    $text = ($block[0] -replace "^\[watch\] $Outcome\s*", '' -replace '\s*\(\d+ ms\)\s*$', '' -replace '\s+', ' ').Trim(' ', ':')
    if ($text.Length -gt 140) { $text = $text.Substring(0, 137) + '...' }
    return $text
}

function Invoke-JourneyEdit {
    param([Parameter(Mandatory)][string]$AppDir, [Parameter(Mandatory)]$Action)
    # One save that touches several files ("edits": [ { "file", "edit" } ]), written back to back.
    if ($Action.edits) {
        foreach ($part in @($Action.edits)) {
            Invoke-JourneyEdit -AppDir $AppDir -Action ([pscustomobject]@{ id = $Action.id; file = $part.file; edit = $part.edit })
        }
        return
    }
    $edit = $Action.edit
    $path = Join-Path $AppDir $Action.file
    $content = if (Test-Path -LiteralPath $path -PathType Leaf) { [System.IO.File]::ReadAllText($path) -replace "`r`n", "`n" } else { $null }
    switch ([string]$edit.op) {
        'create-file' { $text = ConvertTo-JourneyText $edit.text; if (-not $text.EndsWith("`n")) { $text += "`n" }; Set-JourneyFile $path $text }
        'delete-file' { Remove-Item -LiteralPath $path -Force }
        'rename-folder' { Rename-Item -LiteralPath $path -NewName ([string]$edit.to) }
        'write-base64' { $null = New-Item -ItemType Directory -Force -Path (Split-Path $path); [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String([string]$edit.base64)) }
        'insert-after' { $anchor = ConvertTo-JourneyText $edit.anchor; Assert-OneMatch $content $anchor $Action; Set-JourneyFile $path $content.Replace($anchor, $anchor + "`n" + (ConvertTo-JourneyText $edit.text)) }
        'insert-before' { $anchor = ConvertTo-JourneyText $edit.anchor; Assert-OneMatch $content $anchor $Action; Set-JourneyFile $path $content.Replace($anchor, (ConvertTo-JourneyText $edit.text) + "`n" + $anchor) }
        'replace' { $find = ConvertTo-JourneyText $edit.find; Assert-OneMatch $content $find $Action; Set-JourneyFile $path $content.Replace($find, (ConvertTo-JourneyText $edit.text)) }
        'delete' { $find = ConvertTo-JourneyText $edit.find; Assert-OneMatch $content $find $Action; Set-JourneyFile $path $content.Replace($find, '') }
        'move' {
            $block = ConvertTo-JourneyText $edit.block
            Assert-OneMatch $content $block $Action
            $content = [regex]::Replace($content.Replace($block + "`n", ''), "`n{3,}", "`n`n")
            $to = ConvertTo-JourneyText $edit.to
            Assert-OneMatch $content $to $Action
            $content = if ($edit.position -eq 'before') { $content.Replace($to, $block + "`n`n" + $to) } else { $content.Replace($to, $to + "`n`n" + $block) }
            Set-JourneyFile $path ([regex]::Replace($content, "`n{3,}", "`n`n"))
        }
        default { throw "Unknown edit op '$($edit.op)'." }
    }
}

Export-ModuleMember -Function Get-JourneyPlan, Invoke-JourneyEdit, Get-JourneySaveKind, Get-JourneyOutcomeReason
