<#
.SYNOPSIS
Shared PowerShell helpers for sample & guide Pester tests.

.DESCRIPTION
This module provides setup and CLI helper functions used by each sample's
test.Tests.ps1 Pester test file. Assertion and reporting functions are handled
by Pester's built-in Should assertions — this module only provides:
  - CLI path resolution and installation
  - Prerequisite checks
  - Temp directory management
  - winapp invocation helpers

Import with: Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
#>

# ============================================================================
# Winapp CLI Helpers
# ============================================================================

function Resolve-WinappCliPath {
    <#
    .SYNOPSIS
    Resolves the winapp CLI path from artifacts or local build.
    Returns the resolved absolute path to a .tgz or package directory.
    #>
    param(
        [string]$WinappPath
    )

    $repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

    if (-not $WinappPath) {
        # Default search order: CI artifact dir, local package-npm.ps1 output dir, then source dir.
        $defaultCandidates = @(
            (Join-Path $repoRoot "artifacts\npm"),
            (Join-Path $repoRoot "artifacts"),
            (Join-Path $repoRoot "src\winapp-npm")
        )
        $WinappPath = $defaultCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $WinappPath -or -not (Test-Path $WinappPath)) {
        throw "Winapp path not found: $WinappPath"
    }

    $resolved = (Resolve-Path $WinappPath).Path

    if (Test-Path $resolved -PathType Container) {
        $tgz = Get-ChildItem -Path $resolved -Filter "*.tgz" -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending |
            Select-Object -First 1
        if ($tgz) { return $tgz.FullName }
        if (Test-Path (Join-Path $resolved "package.json")) { return $resolved }
        throw "No .tgz or package.json found in $resolved"
    }

    return $resolved
}

function ConvertTo-ArgumentList {
    <#
    .SYNOPSIS
    Splits a command-line argument string into discrete arguments, honoring
    single and double quotes. Used so callers can keep passing a single
    argument string while the command itself is invoked without Invoke-Expression.
    #>
    param(
        [string]$Arguments
    )

    if ([string]::IsNullOrWhiteSpace($Arguments)) { return ,@() }

    $result = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $quote = [char]0
    $hasContent = $false

    foreach ($ch in $Arguments.ToCharArray()) {
        if ($quote -ne [char]0) {
            if ($ch -eq $quote) { $quote = [char]0 } else { [void]$current.Append($ch) }
        } elseif ($ch -eq '"' -or $ch -eq "'") {
            $quote = $ch
            $hasContent = $true
        } elseif ([char]::IsWhiteSpace($ch)) {
            if ($hasContent) {
                $result.Add($current.ToString())
                [void]$current.Clear()
                $hasContent = $false
            }
        } else {
            [void]$current.Append($ch)
            $hasContent = $true
        }
    }

    if ($hasContent) { $result.Add($current.ToString()) }

    # Unary comma stops PowerShell unrolling a one-element array to a string, which would splat per character.
    return ,$result.ToArray()
}

function Invoke-WinappCommand {
    <#
    .SYNOPSIS
    Invokes the winapp CLI with the given arguments and returns stdout lines.
    Resolution order: local node_modules/.bin/winapp -> winapp on PATH ->
    dotnet run against the repo CLI project (only when WINAPP_TEST_USE_DOTNET=1
    or no other winapp is available). Throws on non-zero exit code.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Arguments,
        [string]$FailMessage = "winapp $Arguments failed"
    )

    $npxWinapp = Join-Path (Get-Location) "node_modules\.bin\winapp.cmd"
    $pathWinapp = Get-Command winapp -ErrorAction SilentlyContinue
    $cliProject = Join-Path $PSScriptRoot "..\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj"
    $useDotnet = $env:WINAPP_TEST_USE_DOTNET -eq '1'

    $argList = ConvertTo-ArgumentList -Arguments $Arguments

    if (Test-Path $npxWinapp) {
        $exe = 'npx'
        $argList = @('winapp') + $argList
    } elseif ($pathWinapp -and -not $useDotnet) {
        $exe = 'winapp'
    } elseif (Test-Path $cliProject) {
        # Fall back to dotnet run when no installed winapp is on PATH, or when explicitly requested.
        $exe = 'dotnet'
        $argList = @('run', '--project', $cliProject, '--') + $argList
    } else {
        $exe = 'winapp'
    }

    Write-Verbose "Running: $exe $($argList -join ' ')"
    $output = & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw $FailMessage }
    return $output
}

function Install-WinappNpmPackage {
    <#
    .SYNOPSIS
    Installs the winapp npm package into the current project as a devDependency.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )
    Write-Verbose "Installing winapp from: $PackagePath"
    npm install $PackagePath --save-dev
    if ($LASTEXITCODE -ne 0) { throw "Failed to install winapp npm package" }
}

function Install-WinappGlobal {
    <#
    .SYNOPSIS
    Installs the winapp npm package globally so 'winapp' is available on PATH.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )
    Write-Verbose "Installing winapp globally from: $PackagePath"
    npm install -g $PackagePath
    if ($LASTEXITCODE -ne 0) { throw "Failed to install winapp globally" }
}

# ============================================================================
# Prerequisite Checks
# ============================================================================

function Test-Prerequisite {
    <#
    .SYNOPSIS
    Tests whether a command-line tool is available on PATH. Returns $true/$false.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Command
    )
    $null = Get-Command $Command -ErrorAction SilentlyContinue
    return $?
}

# ============================================================================
# Network Retry Helpers
# ============================================================================

function Invoke-WithRetry {
    <#
    .SYNOPSIS
    Runs a script block that shells out to a network-dependent tool, retrying
    while it reports failure.

    .DESCRIPTION
    Sample tests download from npm and from GitHub releases, and both fail
    intermittently in CI for reasons that have nothing to do with winapp — a
    dropped connection surfaces as 'TypeError: fetch failed' and a nonzero exit
    code. Treating the first failure as a test failure reports a winapp
    regression that is not one.

    The script block is responsible for running the command; this returns $true
    as soon as the block leaves $LASTEXITCODE at 0, and $false if every attempt
    failed. Waits longer between each attempt, since an immediate retry tends to
    hit whatever transient condition failed the first one.

    .PARAMETER ScriptBlock
    The command to run. Must set $LASTEXITCODE, i.e. call a native executable.
    Anything it writes is sent straight to the host, so a caller can invoke a
    native command bare without its stdout ending up in this function's return
    value.

    .PARAMETER MaxAttempts
    How many times to run it before giving up.

    .PARAMETER OperationName
    Used in the progress messages so a CI log says which download is retrying.

    .PARAMETER OnRetry
    Optional cleanup run before each retry, e.g. removing a partial download.
    #>
    param(
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock,

        [int]$MaxAttempts = 3,

        [string]$OperationName = "command",

        [scriptblock]$OnRetry
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if ($attempt -gt 1) {
            $delay = 5 * ($attempt - 1)
            Write-Host "$OperationName failed (attempt $($attempt - 1) of $MaxAttempts). Retrying in ${delay}s..."
            # Out-Host for the same reason as below: cleanup here often shells out
            # (npm cache clean), and its stdout must not become part of the result.
            if ($OnRetry) { & $OnRetry | Out-Host }
            Start-Sleep -Seconds $delay
        }
        # Out-Host, not a bare call: a PowerShell function returns everything written
        # to the output stream, so a bare 'npx ...' inside the block would make this
        # return @('...npx output...', $true) instead of $true, and the caller's
        # 'Should -Be $true' would fail on a command that actually succeeded.
        & $ScriptBlock | Out-Host
        if ($LASTEXITCODE -eq 0) {
            return $true
        }
    }
    Write-Host "$OperationName failed after $MaxAttempts attempts."
    return $false
}

# ============================================================================
# Temp Directory Helpers
# ============================================================================

function New-TempTestDirectory {
    <#
    .SYNOPSIS
    Creates a temporary directory for from-scratch guide workflow tests.
    Returns the absolute path.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Prefix
    )
    $tempBase = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-test"
    $null = New-Item -ItemType Directory -Path $tempBase -Force
    $tempDir = Join-Path $tempBase "$Prefix-$([System.IO.Path]::GetRandomFileName())"
    $null = New-Item -ItemType Directory -Path $tempDir -Force
    return $tempDir
}

function Remove-TempTestDirectory {
    <#
    .SYNOPSIS
    Removes a temporary test directory created by New-TempTestDirectory.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )
    if (Test-Path $Path) {
        Remove-Item -Path $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================================
# Exports
# ============================================================================

Export-ModuleMember -Function @(
    'Resolve-WinappCliPath'
    'Invoke-WinappCommand'
    'Install-WinappNpmPackage'
    'Install-WinappGlobal'
    'Test-Prerequisite'
    'Invoke-WithRetry'
    'New-TempTestDirectory'
    'Remove-TempTestDirectory'
)
