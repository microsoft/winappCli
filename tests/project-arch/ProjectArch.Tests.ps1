#Requires -Version 7
<#
.SYNOPSIS
    Runs `winapp run` against generated .NET projects that cover how the target architecture reaches
    MSBuild, and checks that each app builds, runs, and runs as the requested architecture.

.DESCRIPTION
    Each fixture is generated from ProjectArch.psm1 into -WorkRoot, run with the real CLI one at a time,
    then unregistered and deleted. See README.md.

.PARAMETER WinappPath
    winapp.exe, or a folder containing it. Default: artifacts\cli\win-<host arch>\winapp.exe, then winapp on PATH.

.PARAMETER Architecture
    Target architecture passed to --arch. Default: the host architecture.

.PARAMETER Fixture
    Wildcard patterns selecting fixture IDs. Default: all.

.PARAMETER Shard
    'N/M' runs every M-th fixture starting at N (1-based), for splitting the suite across CI runners.

.PARAMETER DumpDirectory
    When set, a winapp run that times out is dumped here (full memory) before it is stopped.
#>
param(
    [string]$WinappPath,
    [ValidateSet('x64', 'arm64', 'x86')]
    [string]$Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant(),
    [string[]]$Fixture = @('*'),
    [string]$Shard,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-project-arch'),
    [int]$TimeoutMinutes = 15,
    [string]$DumpDirectory,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    Import-Module "$PSScriptRoot\ProjectArch.psm1" -Force

    $fixtures = @(Get-ProjectArchFixtures | Where-Object {
            $id = $_.Id
            @($Fixture | Where-Object { $id -like $_ }).Count -gt 0
        } | Sort-Object { $_.Id })

    if ($Shard) {
        if ($Shard -notmatch '^(\d+)/(\d+)$' -or [int]$Matches[1] -lt 1 -or [int]$Matches[1] -gt [int]$Matches[2]) {
            throw "-Shard must be N/M with 1 <= N <= M (got '$Shard')."
        }
        $index = [int]$Matches[1] - 1
        $count = [int]$Matches[2]
        $fixtures = @(for ($i = $index; $i -lt $fixtures.Count; $i += $count) { $fixtures[$i] })
    }

    $script:skip = -not $IsWindows -or -not (Get-Command dotnet -ErrorAction SilentlyContinue)
}

BeforeAll {
    Import-Module "$PSScriptRoot\ProjectArch.psm1" -Force

    $script:winapp = $null
    if ($WinappPath) {
        $script:winapp = if (Test-Path $WinappPath -PathType Container) { Join-Path $WinappPath 'winapp.exe' } else { $WinappPath }
    }
    else {
        $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $built = Join-Path $PSScriptRoot "..\..\artifacts\cli\win-$hostArch\winapp.exe"
        $script:winapp = if (Test-Path $built) { (Resolve-Path $built).Path } else { (Get-Command winapp -ErrorAction SilentlyContinue)?.Source }
    }
    if (-not $script:winapp -or -not (Test-Path $script:winapp)) {
        throw "winapp.exe not found. Pass -WinappPath, or build it with .\scripts\build-cli.ps1."
    }
    $script:winapp = (Resolve-Path $script:winapp).Path
    Write-Host "Using $script:winapp for --arch $Architecture"

    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

    function Get-OutputTail([string]$Text, [int]$Lines = 60) {
        (($Text -split "`r?`n") | Select-Object -Last $Lines) -join "`n"
    }
}

Describe 'winapp run architecture matrix' {
    It '<Id>: <Why>' -ForEach $fixtures -Skip:$script:skip {
        $fixture = $_
        $arch = if ($fixture.ContainsKey('Arch')) { $fixture.Arch } else { $Architecture }
        $root = Join-Path $WorkRoot $fixture.Id
        $run = $null
        try {
            New-ProjectArchFixture -Fixture $fixture -Root $root
            $run = Invoke-ProjectArchRun -Fixture $fixture -Root $root -Winapp $script:winapp -Architecture $arch -TimeoutMinutes $TimeoutMinutes -DumpDirectory $DumpDirectory
            $tail = Get-OutputTail $run.Output

            $run.TimedOut | Should -BeFalse -Because "winapp run must finish within $TimeoutMinutes minutes:`n$tail"
            $run.ExitCode | Should -Be 0 -Because "winapp run must succeed:`n$tail"

            if ($fixture.ContainsKey('ExpectRid') -and $fixture.ExpectRid) {
                $run.Output | Should -Match "(?<!\S)-r\s+win-$arch\b" -Because "this project can't honor a Platform-only build, so winapp must pass the RID"
            }
            else {
                $run.Output | Should -Not -Match '(?<!\S)-r\s+win-' -Because 'winapp run conveys the architecture with Platform, not a global RID'
            }

            $exe = $null
            $windowed = $fixture.App.Kind -ne 'console'
            if ($run.Launched -and $run.ProcessId) {
                $process = Get-Process -Id $run.ProcessId -ErrorAction SilentlyContinue
                if ($windowed) {
                    $process | Should -Not -BeNullOrEmpty -Because "the app must still be running after launch:`n$tail"
                    # Hold the handle so the exit code stays readable if the app exits.
                    $null = $process.Handle
                    $deadline = [DateTime]::UtcNow.AddSeconds(30)
                    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero) {
                        Start-Sleep -Milliseconds 250
                        $process.Refresh()
                    }
                    $exitNote = if ($process.HasExited) { "exit code 0x{0:X8}" -f $process.ExitCode } else { '' }
                    $process.HasExited | Should -BeFalse -Because "the app must survive startup ($exitNote)"
                    $process.MainWindowHandle | Should -Not -Be ([IntPtr]::Zero) -Because 'the app must show a window within 30 seconds'
                    # A missing runtime makes the app host show an error dialog instead of the app's window.
                    $class = Get-WindowClassName $process.MainWindowHandle
                    $class | Should -Not -Be '#32770' -Because "the window must be the app's, not an error dialog ('$($process.MainWindowTitle)')"
                }
                if ($process -and -not $process.HasExited) { $exe = $process.Path }
            }

            if (-not $exe) {
                # Not launched, or already exited (console): check the newest built executable.
                $exe = Get-ChildItem (Join-Path $root 'App\bin') -Recurse -Filter 'FixtureApp.exe' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
            }

            $exe | Should -Not -BeNullOrEmpty -Because "winapp must produce FixtureApp.exe:`n$tail"
            Get-PeArchitecture $exe | Should -Be $arch -Because "$exe must be built for --arch $arch"
        }
        finally {
            if ($run) { Remove-ProjectArchRun -Fixture $fixture -Run $run }
            if (-not $SkipCleanup -and (Test-Path $root)) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
