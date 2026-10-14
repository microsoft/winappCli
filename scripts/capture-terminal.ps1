<#
.SYNOPSIS
    Runs commands in a new Windows Terminal window and saves a PNG screenshot or an MP4 video.

.DESCRIPTION
    Used to add before/after evidence to pull requests. Each command is shown as
    "> <command>" followed by its output, with no user profile or custom prompt.
    The window closes afterwards, unless Windows Terminal is set to keep windows
    open when a shell exits.

    Use -Video for multi-step flows or output that changes over time (progress,
    prompts). For long but static output, a screenshot or pasted text is easier to read.

    Requires Windows Terminal (wt.exe) and winapp on PATH. The window takes focus
    while it runs.

.EXAMPLE
    .\scripts\capture-terminal.ps1 -Command 'winapp cert info .\devcert.pfx' -Output after.png

.EXAMPLE
    .\scripts\capture-terminal.ps1 -Video -Output flow.mp4 -Command 'winapp init --use-defaults', 'winapp cert generate'
#>
param(
    [Parameter(Mandatory)] [string[]]$Command,
    [Parameter(Mandatory)] [string]$Output,
    [switch]$Video,
    [string]$WorkingDirectory = (Get-Location).Path,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command wt.exe -ErrorAction SilentlyContinue)) { throw 'Windows Terminal (wt.exe) is required.' }
$winapp = (Get-Command winapp -ErrorAction SilentlyContinue).Source
if (-not $winapp) { throw 'winapp must be on PATH.' }
$WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) { throw "Not a directory: $WorkingDirectory" }

$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$title = "winapp-capture-$id"
$goMarker = Join-Path $env:TEMP "$title.go"
$doneMarker = Join-Path $env:TEMP "$title.done"
$releaseMarker = Join-Path $env:TEMP "$title.release"
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

function Quote([string]$s) { "'" + ($s -replace "'", "''") + "'" }

$pause = if ($Video) { 'Start-Sleep -Milliseconds 1200' } else { '' }
$steps = ($Command | ForEach-Object { "Write-Host $(Quote "> $_")`n$_`nWrite-Host ''`n$pause" }) -join "`n"

$inner = @"
Set-Location -LiteralPath $(Quote $WorkingDirectory)
Clear-Host
while (-not (Test-Path $(Quote $goMarker))) {
    if (Test-Path $(Quote $releaseMarker)) { Remove-Item $(Quote $releaseMarker); exit 1 }
    Start-Sleep -Milliseconds 100
}
$steps
New-Item -ItemType File -Path $(Quote $doneMarker) -Force | Out-Null
while (-not (Test-Path $(Quote $releaseMarker))) { Start-Sleep -Milliseconds 200 }
Remove-Item $(Quote $goMarker), $(Quote $doneMarker), $(Quote $releaseMarker) -ErrorAction SilentlyContinue
exit 0
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

Start-Process wt.exe -ArgumentList "-w new --title $title --suppressApplicationTitle powershell.exe -NoLogo -NoProfile -EncodedCommand $encoded"

$recorder = $null
try {
    $hwnd = $null
    $deadline = (Get-Date).AddSeconds(30)
    while (-not $hwnd -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $match = (& $winapp ui list-windows -a $title 2>&1 | Out-String) | Select-String 'HWND (\d+)'
        if ($match) { $hwnd = $match.Matches[0].Groups[1].Value }
    }
    if (-not $hwnd) { throw "Could not find the terminal window '$title'." }

    if ($Video) {
        # winapp ui record stops when it reads a newline on redirected stdin.
        $psi = [Diagnostics.ProcessStartInfo]::new($winapp, "ui record -w $hwnd -o `"$Output`" --overwrite")
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $recorder = [Diagnostics.Process]::Start($psi)
        Start-Sleep -Seconds 2
        if ($recorder.HasExited) { throw "winapp ui record failed: $($recorder.StandardError.ReadToEnd())" }
    }

    New-Item -ItemType File -Path $goMarker -Force | Out-Null

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $doneMarker)) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $doneMarker)) { throw "Commands did not finish within $TimeoutSeconds seconds." }

    if ($Video) {
        Start-Sleep -Seconds 1
        $recorder.StandardInput.WriteLine()
        $recorder.StandardInput.Close()
        if (-not $recorder.WaitForExit(60000)) { throw 'winapp ui record did not stop.' }
        if ($recorder.ExitCode -ne 0) { throw "winapp ui record failed: $($recorder.StandardError.ReadToEnd())" }
    }
    else {
        Start-Sleep -Milliseconds 500
        & $winapp ui screenshot -w $hwnd -o $Output | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'winapp ui screenshot failed.' }
    }
    Write-Host "Saved $Output"
}
finally {
    if ($recorder -and -not $recorder.HasExited) { $recorder.Kill() }
    # The shell exits (closing the window) and deletes the markers once it sees this,
    # even if it's still running a command after a timeout.
    New-Item -ItemType File -Path $releaseMarker -Force | Out-Null
}
