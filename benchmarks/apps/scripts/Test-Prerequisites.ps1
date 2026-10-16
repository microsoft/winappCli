[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$failures = 0

function Write-Check {
    param(
        [bool] $Passed,
        [string] $Name,
        [string] $Detail,
        [string] $Guidance
    )

    if ($Passed) {
        Write-Host "[PASS] $Name" -ForegroundColor Green -NoNewline
        if ($Detail) {
            Write-Host " - $Detail"
        } else {
            Write-Host ""
        }
        return
    }

    $script:failures++
    Write-Host "[FAIL] $Name" -ForegroundColor Red
    Write-Host "       $Guidance" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "App benchmark - prerequisite check" -ForegroundColor Cyan
Write-Host ""

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    $nodeVersion = (& node --version).Trim()
    $nodeMajor = [int]($nodeVersion.TrimStart('v').Split('.')[0])
    Write-Check ($nodeMajor -ge 20) "Node.js 20+" $nodeVersion `
        "Install Node.js 20 or later: winget install OpenJS.NodeJS.LTS"
} else {
    Write-Check $false "Node.js 20+" "" `
        "Install Node.js 20 or later: winget install OpenJS.NodeJS.LTS"
}

$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    Write-Check $true "Git" ((& git --version).Trim()) ""
} else {
    Write-Check $false "Git" "" "Install Git: winget install Git.Git"
}

$copilot = Get-Command copilot -ErrorAction SilentlyContinue
if ($copilot) {
    $copilotVersion = (& copilot --version | Select-Object -First 1).Trim()
    Write-Check $true "GitHub Copilot CLI" $copilotVersion `
        "If model discovery fails in the dashboard, authenticate with: copilot login"
} else {
    Write-Check $false "GitHub Copilot CLI" "" `
        "Install and authenticate: winget install GitHub.Copilot; copilot login"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $dotnetVersion = (& dotnet --version).Trim()
    $dotnetMajor = [int]($dotnetVersion.Split('.')[0])
    Write-Check ($dotnetMajor -ge 10) ".NET SDK 10+" $dotnetVersion `
        "Install .NET SDK 10 or later: winget install Microsoft.DotNet.SDK.10"
} else {
    Write-Check $false ".NET SDK 10+" "" `
        "Install .NET SDK 10 or later: winget install Microsoft.DotNet.SDK.10"
}

$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if ($winapp) {
    Write-Check $true "WinApp CLI" ((& winapp --version).Trim()) ""
} else {
    Write-Check $false "WinApp CLI" "" `
        "Install WinApp CLI: winget install Microsoft.winappcli --source winget (see https://github.com/microsoft/winappCli)."
}

$developerModePath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
$developerMode = Get-ItemPropertyValue `
    -Path $developerModePath `
    -Name "AllowDevelopmentWithoutDevLicense" `
    -ErrorAction SilentlyContinue
Write-Check ($developerMode -eq 1) "Windows Developer Mode" `
    $(if ($developerMode -eq 1) { "enabled" } else { "" }) `
    "Enable Settings > System > For developers > Developer Mode."

# A project/solution file above the results folder (e.g. a stray .csproj in the
# user profile) makes every `dotnet new` walk up and glob that whole tree.
# Keep this in sync with `resultsRoot` in src/runner/config.ts.
$resultsDir = if ($env:BENCH_RESULTS_DIR) { $env:BENCH_RESULTS_DIR } else { Join-Path $env:LOCALAPPDATA 'winapp-benchmarks\apps\results' }
$dir = Split-Path $resultsDir -Parent
while ($dir -and -not (Test-Path -LiteralPath $dir)) { $dir = Split-Path $dir -Parent }
if ($dir) { $dir = (Resolve-Path -LiteralPath $dir).Path }
$stray = @()
while ($dir) {
    $stray += Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '\.(csproj|vbproj|fsproj|vcxproj|sln|slnx)$' -or $_.Name -in 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props' } |
        ForEach-Object FullName
    $parent = Split-Path $dir -Parent
    if (-not $parent -or $parent -eq $dir) { break }
    $dir = $parent
}
Write-Check ($stray.Count -eq 0) "No stray project files above the results folder" `
    $(if ($stray.Count -eq 0) { "none found ($resultsDir)" } else { "" }) `
    "Move or delete: $($stray -join ', '). They make 'dotnet new' scan the whole parent folder, adding minutes to every trial."

# DOTNET_ROOT pointing at a different .NET install than the `dotnet` on PATH:
# apps build against one install and run on another. Trials override it for
# processes the harness starts, but packaged apps are activated by Windows with
# the saved user environment, so a mismatch fails their launch ("You must
# install .NET") and costs the trial its score.
$dotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')
if (-not $dotnetRoot) { $dotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Machine') }
if ($dotnet -and $dotnetRoot) {
    $pathRoot = (Resolve-Path (Split-Path $dotnet.Source -Parent)).Path
    $rootPath = (Resolve-Path $dotnetRoot -ErrorAction SilentlyContinue).Path
    Write-Check ($rootPath -eq $pathRoot) "DOTNET_ROOT matches the dotnet on PATH" $rootPath `
        "DOTNET_ROOT=$dotnetRoot differs from $pathRoot. Packaged apps launched by the benchmark would use it and fail with 'You must install .NET'. Remove it: [Environment]::SetEnvironmentVariable('DOTNET_ROOT', `$null, 'User')"
}

# The user PATH must stay well below Windows' 32,767-character limit: above it,
# sign-in breaks (no shell, no packaged apps). Older harness versions leaked one
# `win-dev-bench\trial-env\...\.dotnet\tools` entry per trial.
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
$machinePath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
$trialEntries = @(($userPath -split ';') | Where-Object { $_ -match '[\\/]win-dev-bench[\\/]trial-env[\\/]' })
Write-Check ($trialEntries.Count -eq 0) "No benchmark trial folders in the user PATH" "none" `
    "$($trialEntries.Count) leftover trial folder(s) in the user PATH. The next benchmark run removes them, or remove them in Environment Variables."
Write-Check ($userPath.Length -le 4000 -and ($userPath.Length + $machinePath.Length) -le 16000) "PATH length" `
    "user $($userPath.Length), machine $($machinePath.Length) characters" `
    "User PATH is $($userPath.Length) and machine PATH $($machinePath.Length) characters (limits 4000 user / 16000 combined). Remove stale entries before benchmarking; Windows breaks above 32,767."

# Trial builds and Copilot session folders go past 260 characters.
$longPaths = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction SilentlyContinue).LongPathsEnabled
Write-Check ($longPaths -eq 1) "Windows long paths enabled" "LongPathsEnabled=1" `
    "Paths over 260 characters fail. From an elevated PowerShell: New-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force"

Write-Host ""
if ($failures -gt 0) {
    Write-Host "$failures prerequisite check(s) failed. Fix them before starting a benchmark." `
        -ForegroundColor Red
    exit 1
}

Write-Host "All detectable prerequisites passed." -ForegroundColor Green
Write-Host "The dashboard verifies Copilot model access and agent-specific dependencies before a run."
