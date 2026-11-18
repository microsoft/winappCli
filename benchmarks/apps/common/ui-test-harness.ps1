param(
    [Parameter(Mandatory)][string]$WinAppPath,
    [Parameter(Mandatory)][int]$AppPid,
    [string]$ArtifactsDir = ".",
    [ValidateRange(5, 300)][int]$UiCommandTimeoutSeconds = 30
)

$ErrorActionPreference = "Continue"
if ([string]::IsNullOrWhiteSpace($env:WINAPP_UI_WORKFLOW_ID)) {
    throw "WINAPP_UI_WORKFLOW_ID is required for cooperative concurrent UI testing."
}
$script:UiWinAppPath = $WinAppPath
$script:UiCommandTimeoutSeconds = $UiCommandTimeoutSeconds
$script:UiResults = @()
$script:UiScreenshotIndex = 0
$script:UiHwnd = $null

function Invoke-WinAppProcess {
    param(
        [Parameter(Mandatory)][object[]]$Arguments,
        [int]$TimeoutSeconds = $script:UiCommandTimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:UiWinAppPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    # Flatten nested arrays so callers can pass a target directly, e.g.
    # `Invoke-WinAppUi click X (Get-UiTarget)`. Stringifying @("-w", hwnd) as
    # one item would send the single token "-w <hwnd>", which winapp rejects.
    $flatArguments = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[object]]::new()
    for ($i = $Arguments.Count - 1; $i -ge 0; $i--) { $pending.Push($Arguments[$i]) }
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        if ($item -is [System.Collections.IEnumerable] -and $item -isnot [string]) {
            $nested = @($item)
            for ($i = $nested.Count - 1; $i -ge 0; $i--) { $pending.Push($nested[$i]) }
        } elseif ($null -ne $item) {
            $flatArguments.Add("$item")
        }
    }
    foreach ($argument in $flatArguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start WinApp CLI."
    }

    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch [System.InvalidOperationException] {}
        }
        $process.WaitForExit()
        $stdout.GetAwaiter().GetResult() | Write-Output
        $stderrText = $stderr.GetAwaiter().GetResult()
        if ($stderrText) { [Console]::Error.Write($stderrText) }
        $operation = (@($Arguments | Select-Object -First 2) -join " ")
        [Console]::Error.WriteLine((
            "benchmark_ui_timeout: winapp {0} exceeded the {1}s per-command limit and was terminated." -f
                $operation, $TimeoutSeconds))
        $global:LASTEXITCODE = 124
        return
    }

    $stdout.GetAwaiter().GetResult() | Write-Output
    $stderrText = $stderr.GetAwaiter().GetResult()
    if ($stderrText) { [Console]::Error.Write($stderrText) }
    $global:LASTEXITCODE = $process.ExitCode
}

function global:winapp {
    param(
        [Parameter(Position = 0)][string]$Command,
        [Parameter(Position = 1, ValueFromRemainingArguments = $true)][object[]]$RemainingArguments
    )
    $Arguments = @($Command) + $RemainingArguments
    if ($Command -eq "ui") {
        Invoke-WinAppProcess -Arguments $Arguments
        return
    }
    & $script:UiWinAppPath @Arguments
}

function Resolve-UiHwnd {
    $windows = Invoke-WinAppProcess -Arguments @(
        "ui", "list-windows", "-a", $AppPid, "--quiet", "--json"
    ) 2>$null |
        ConvertFrom-Json
    $script:UiHwnd = ($windows |
        Where-Object { $_.title -and $_.title -ne "PopupHost" } |
        Select-Object -First 1).hwnd
    return $script:UiHwnd
}

function Get-UiTarget {
    if (-not $script:UiHwnd) { Resolve-UiHwnd | Out-Null }
    if ($script:UiHwnd) { return @("-w", "$($script:UiHwnd)") }
    return @("-a", "$AppPid")
}

function Invoke-WinAppUi {
    param([Parameter(ValueFromRemainingArguments)][object[]]$Arguments)
    Invoke-WinAppProcess -Arguments (@("ui") + $Arguments)
}

function Get-UiElements {
    param(
        [switch]$Interactive,
        [string]$App = $null
    )
    $target = if ($App) { @("-a", $App) } else { Get-UiTarget }
    $args = @("inspect") + $target + @("--json")
    if ($Interactive) { $args += "--interactive" }
    $envelope = Invoke-WinAppUi @args 2>$null | ConvertFrom-Json
    return @($envelope.windows | ForEach-Object { $_.elements })
}

function Test-UiRequirement {
    param(
        [Parameter(Mandatory)][int]$Requirement,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Test,
        [string]$Selector
    )
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $global:LASTEXITCODE = 0
            $output = & $Test 2>&1
            if ($LASTEXITCODE -ne 0) { throw "$output" }
            $script:UiResults += [pscustomobject]@{
                requirement = $Requirement
                name = $Name
                selector = $Selector
                status = "pass"
                detail = $null
            }
            return
        } catch {
            $detail = "$_"
            if ($attempt -eq 1 -and $detail -match "foreground_not_target|Target window is not in the foreground") {
                $script:UiHwnd = $null
                Start-Sleep -Milliseconds 800
                continue
            }
            $script:UiResults += [pscustomobject]@{
                requirement = $Requirement
                name = $Name
                selector = $Selector
                status = "fail"
                detail = $detail
            }
            return
        }
    }
}

function Save-UiScreenshot {
    param([Parameter(Mandatory)][string]$Label, [switch]$CaptureScreen)
    $script:UiScreenshotIndex++
    $safe = ($Label -replace "[^a-zA-Z0-9_-]", "-").Trim("-")
    $screenshots = Join-Path $ArtifactsDir "screenshots"
    New-Item -ItemType Directory -Force -Path $screenshots | Out-Null
    $path = Join-Path $screenshots ("{0:D2}-{1}.png" -f $script:UiScreenshotIndex, $safe)
    $args = @("screenshot") + (Get-UiTarget) + @("--quiet", "--json", "--output", $path)
    if ($CaptureScreen) { $args += "--capture-screen" }
    Invoke-WinAppUi @args 2>$null | Set-Content (Join-Path $screenshots "$safe.metadata.json")
    return $path
}

function Save-UiResults {
    param([string]$Path = (Join-Path $ArtifactsDir "test-results.json"))
    $summary = [pscustomobject]@{
        passed = @($script:UiResults | Where-Object status -eq "pass").Count
        failed = @($script:UiResults | Where-Object status -eq "fail").Count
        tests = $script:UiResults
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content $Path
    return $summary
}
