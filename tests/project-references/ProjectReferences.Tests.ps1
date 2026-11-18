#Requires -Version 7
<#
.SYNOPSIS
    Builds packaged WinUI apps that reference other projects with the real winapp CLI, covering the
    "two or more files with the same destination path" (APPX1101) and PRI175/PRI252 packaging failures.

.PARAMETER WinappPath
    winapp.exe, or a folder containing it. Default: artifacts\cli\win-<host arch>\winapp.exe, then winapp on PATH.
#>
param(
    [string]$WinappPath,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-project-references'),
    [switch]$SkipCleanup
)

BeforeDiscovery {
    Import-Module "$PSScriptRoot\ProjectReferences.psm1" -Force
    $cases = @(Get-ProjectReferenceCases)
    $packCases = @($cases | Where-Object { $_['Pack'] })
    # `winapp run --no-launch` registers the package, which needs Developer Mode.
    $devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
    $script:skip = -not $IsWindows -or -not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not $devMode
}

BeforeAll {
    Import-Module "$PSScriptRoot\ProjectReferences.psm1" -Force

    $script:winapp = if ($WinappPath) {
        if (Test-Path $WinappPath -PathType Container) { Join-Path $WinappPath 'winapp.exe' } else { $WinappPath }
    }
    else {
        $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $built = Join-Path $PSScriptRoot "..\..\artifacts\cli\win-$hostArch\winapp.exe"
        if (Test-Path $built) { $built } else { (Get-Command winapp -ErrorAction SilentlyContinue)?.Source }
    }
    if (-not $script:skip -and (-not $script:winapp -or -not (Test-Path $script:winapp))) {
        throw 'winapp.exe not found. Pass -WinappPath, or build it with .\scripts\build-cli.ps1.'
    }
    if ($script:winapp) { $script:winapp = (Resolve-Path $script:winapp).Path }
    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

    function Get-Tail([string]$Text) { (($Text -split "`r?`n") | Select-Object -Last 40) -join "`n" }

    function Assert-NothingWrittenIntoProject([string]$Root) {
        @(Get-ChildItem $Root -Recurse -File -Filter '*.targets' | Where-Object { $_.Name -like 'winapp*' -or $_.Name -like '*.winapp.*' }) |
            Should -BeNullOrEmpty -Because 'winapp passes its targets through the environment, not into the project'
    }
}

Describe 'winapp with project references' -Skip:$script:skip {
    It 'run builds and registers: <Id> (<Why>)' -ForEach $cases {
        $case = $_
        $root = Join-Path $WorkRoot "run-$($case.Id)"
        try {
            New-ProjectReferenceFixture -Case $case -Root $root
            $run = Invoke-WinappWithTimeout $script:winapp @('run', "$root\App\App.csproj", '--no-launch') $root
            $run.ExitCode | Should -Be 0 -Because "winapp run must succeed:`n$(Get-Tail $run.Output)"
            Assert-NothingWrittenIntoProject $root
        }
        finally {
            Remove-CaseRegistration $case
            if (-not $SkipCleanup) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    It 'package succeeds on a clean checkout: <Id>' -ForEach $packCases {
        $case = $_
        $root = Join-Path $WorkRoot "pack-$($case.Id)"
        try {
            New-ProjectReferenceFixture -Case $case -Root $root
            $pack = Invoke-WinappWithTimeout $script:winapp @('package', "$root\App\App.csproj", '--output', "$root\out.msix") $root
            $pack.ExitCode | Should -Be 0 -Because "winapp package must succeed without an earlier winapp run:`n$(Get-Tail $pack.Output)"
            Assert-NothingWrittenIntoProject $root
        }
        finally {
            if (-not $SkipCleanup) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    It 'run recovers after a build that failed with the duplicate-library error' {
        # A plain RID build of this graph fails and leaves a cached PRI input list naming the missing file.
        $case = Get-ProjectReferenceCases | Where-Object Id -eq 'msix-lib'
        $root = Join-Path $WorkRoot 'recovery'
        try {
            New-ProjectReferenceFixture -Case $case -Root $root
            $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
            $failed = & dotnet build "$root\App\App.csproj" -c Debug -r "win-$hostArch" -v minimal -tl:off 2>&1
            $LASTEXITCODE | Should -Not -Be 0 -Because "a plain RID build of this graph fails:`n$(Get-Tail ($failed | Out-String))"

            $run = Invoke-WinappWithTimeout $script:winapp @('run', "$root\App\App.csproj", '--no-launch') $root
            $run.ExitCode | Should -Be 0 -Because "winapp run must recover without a clean:`n$(Get-Tail $run.Output)"
        }
        finally {
            Remove-CaseRegistration $case
            if (-not $SkipCleanup) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
