# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Helpers for the 'winapp run --watch' journeys: apply a journey's edits, read what a save did from the watch log, and
# score it against the journey's expectations. Run-WatchJourney.ps1 drives them.

$script:Utf8 = [System.Text.UTF8Encoding]::new($false)
$script:Ansi = [regex]'\x1B\[[0-?]*[ -/]*[@-~]'

function Remove-Ansi([string]$Text) { if ($null -eq $Text) { return $null }; return $script:Ansi.Replace($Text, '') }

function ConvertTo-JourneyText($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value -replace "`r`n", "`n" }
    return (@($Value) | ForEach-Object { [string]$_ }) -join "`n"
}

<#
.SYNOPSIS
  Applies one save: an edit ({ file, op, find, text, to }) or several ("edits", written back to back, or "delayMs" apart
  to imitate an agent writing files one by one). Files are written the way an editor saves them: their own line endings
  and byte order mark.
#>
function Invoke-JourneyEdit {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)]$Step)
    if ($Step.edits) {
        $first = $true
        foreach ($part in @($Step.edits)) {
            if (-not $first -and $Step.delayMs) { Start-Sleep -Milliseconds ([int]$Step.delayMs) }
            Invoke-JourneyEdit -Root $Root -Step $part
            $first = $false
        }
        return
    }
    $path = Join-Path $Root $Step.file
    $raw = if (Test-Path -LiteralPath $path -PathType Leaf) { [System.IO.File]::ReadAllText($path) } else { $null }
    $content = if ($null -ne $raw) { $raw -replace "`r`n", "`n" } else { $null }
    $crlf = $raw -and $raw.Contains("`r`n")
    $bom = $raw -and ((@([System.IO.File]::ReadAllBytes($path) | Select-Object -First 3) -join ',') -eq '239,187,191')
    $write = {
        param([string]$Text)
        if ($crlf) { $Text = $Text -replace "`r?`n", "`r`n" }
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path)
        for ($attempt = 0; ; $attempt++) {
            try { [System.IO.File]::WriteAllText($path, $Text, $(if ($bom) { [System.Text.UTF8Encoding]::new($true) } else { $script:Utf8 })); return }
            catch [System.IO.IOException] { if ($attempt -ge 20) { throw }; Start-Sleep -Milliseconds 100 }
        }
    }
    $find = ConvertTo-JourneyText $Step.find
    $text = ConvertTo-JourneyText $Step.text
    if ($Step.op -in 'replace', 'insert-after', 'insert-before') {
        if ($null -eq $content) { throw "Edit '$($Step.op)': $($Step.file) does not exist." }
        $count = ([regex]::Matches($content, [regex]::Escape($find))).Count
        if ($count -ne 1) { throw "Edit '$($Step.op)' in $($Step.file): expected one match of '$find', found $count." }
    }
    switch ([string]$Step.op) {
        'create-file' { & $write ($(if ($text.EndsWith("`n")) { $text } else { "$text`n" })) }
        'delete-file' { Remove-Item -LiteralPath $path -Force }
        'rename-file' { Rename-Item -LiteralPath $path -NewName ([string]$Step.to) }
        'replace' { & $write $content.Replace($find, $text) }
        'insert-after' { & $write $content.Replace($find, "$find`n$text") }
        'insert-before' { & $write $content.Replace($find, "$text`n$find") }
        default { throw "Unknown edit op '$($Step.op)'." }
    }
}

<#
.SYNOPSIS
  What a save did, read from the watch log lines printed since the save, or $null while it's still undecided.
.DESCRIPTION
  Outcomes: applied, unchanged, waiting, error, restart (with a reason), restart-no-reason, stuck (the restart's rebuild
  failed and no app came back) and crash (the session ended). winapp's own "[watch] <outcome> <reason>" lines win; on the
  dotnet watch path (#1197) dotnet watch's lines are read too. "Ready" is $true once a restarted app is attached again.
#>
function Get-WatchOutcome {
    param([string[]]$Lines, [bool]$Exited)
    $outcome = $null; $reason = ''; $ready = $false
    $notes = @($Lines | Where-Object { $_ -match '^\s+note:' } | ForEach-Object { $_.Trim() })
    foreach ($line in $Lines) {
        $text = (Remove-Ansi $line).Trim()
        if ($outcome -in 'restart', 'restart-no-reason') {
            if ($text -match 'The app restarted \(PID \d+\)|DevTools attached to the app \(PID \d+\)|^\[watch\] ready') { $ready = $true; break }
            if ($text -match '\berror (MSB\d+|WMC\d+|CS\d+)\b' -or $text -match 'Build FAILED') {
                $outcome = 'stuck'; $reason = if ($Matches[1]) { $Matches[1] } else { 'the rebuild failed' }; continue
            }
        }
        if ($outcome -eq 'stuck') { if ($text -match 'The app restarted \(PID \d+\)') { $outcome = 'restart-no-reason'; $ready = $true; break }; continue }
        if ($text -match '^\[watch\] (applied|unchanged|waiting|error|restart)\b:?\s*(.*)$') {
            $outcome = $Matches[1]; $reason = $Matches[2]
            if ($outcome -eq 'restart' -and -not $reason) { $outcome = 'restart-no-reason' }
            if ($outcome -notin 'restart', 'restart-no-reason') { break }
            continue
        }
        if ($outcome) { continue }
        if ($text -match '^dotnet watch .*(changes applied|Hot reload succeeded)') { $outcome = 'applied'; break }
        if ($text -match '^dotnet watch .*No (managed code|C#) changes to apply') { $outcome = 'unchanged'; break }
        if ($text -match '^dotnet watch .*Restart is needed') { $outcome = 'restart-no-reason'; continue }
        if ($text -match '^dotnet watch .*(error (CS\d+)|compilation errors)') { $outcome = 'error'; $reason = $Matches[2]; break }
        if ($text -match '^dotnet watch .*Failed to build project') { $outcome = 'error'; $reason = 'dotnet watch could not reload the project'; break }
        if ($text -match '^dotnet watch .*An unexpected error occurred') { $reason = 'dotnet watch crashed' }
    }
    if (-not $outcome -and $Exited) { $outcome = 'crash'; if (-not $reason) { $reason = 'winapp run --watch ended' } }
    if (-not $outcome) { return $null }
    if ($outcome -eq 'applied' -and $notes) { $reason = $notes -join '; ' }
    return [pscustomobject]@{ Outcome = $outcome; Reason = ([string]$reason).Trim(); Note = [bool]$notes; Ready = $ready }
}

<#
.SYNOPSIS
  Scores a step. pass: the v1 expectation is met (and the step's checks passed where the change should show).
  gap: today's recorded outcome, not yet v1 (an expected failure). fail: neither, so something changed: a regression,
  or an improvement whose "today" should be updated. untested: no v1 decision yet; only recorded. pending: not run.
#>
function Get-StepStatus {
    param($Step, [string]$Observed, [bool]$Note, [bool]$ChecksOk, [string]$Reason)
    if ($Step.pending) { return 'pending' }
    $v1 = [string]$Step.expect.v1
    $meetsV1 = switch ($v1) {
        'live' { $Observed -eq 'applied' -and $ChecksOk }
        'live+note' { $Observed -eq 'applied' -and $Note -and $ChecksOk }
        'restart' { $Observed -eq 'restart' -and $ChecksOk }
        default { $Observed -eq $v1 }
    }
    # "reason" (a regular expression) names the cause v1 expects, so an outcome with another cause doesn't count.
    if ($meetsV1 -and $Step.expect.reason -and $Reason -notmatch $Step.expect.reason) { $meetsV1 = $false }
    if ($v1 -eq 'untested') { return 'untested' }
    if ($meetsV1) { return 'pass' }
    # A new step whose outcome today hasn't been recorded yet: copy the observed outcome into "today".
    if ($Step.expect.today -eq '?') { return 'record' }
    if ($Observed -in @(([string]$Step.expect.today) -split '\|')) { return 'gap' }
    return 'fail'
}

Export-ModuleMember -Function Remove-Ansi, ConvertTo-JourneyText, Invoke-JourneyEdit, Get-WatchOutcome, Get-StepStatus
