param(
    [string]$WinappPath,
    [switch]$SkipCleanup,
    # Phase 1 installs the Windows App Runtime MSIX system-wide and launches the app. That is safe on a
    # throwaway CI runner but is a footgun on a developer's own machine, so it is OFF by default locally.
    # Pass -AllowRuntimeInstall to opt in when you accept that machine-global side effect.
    [switch]$AllowRuntimeInstall
)

# Whether Phase 1 (runtime install + app launch) may run: Windows only, and only on a CI ephemeral runner
# or when the caller explicitly opts in. On CI the install is discarded with the runner VM; locally we skip
# it so a casual `Invoke-Pester` never mutates the machine. The expression is inlined at both discovery time
# and run time (a top-level function would not survive into Pester's run phase, same reason $script:skip is
# recomputed in BeforeAll).

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
    # Phase 1 is additionally gated so it never installs a runtime / launches an app on a dev box by default.
    $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $onCI = ($env:CI -eq 'true') -or ($env:GITHUB_ACTIONS -eq 'true') -or (-not [string]::IsNullOrEmpty($env:TF_BUILD))
    $script:skipRuntimeInstall = $script:skip -or -not ($onWindows -and ($onCI -or $AllowRuntimeInstall))
}

Describe 'winui-unpackaged-app sample' {

    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
        $onCI = ($env:CI -eq 'true') -or ($env:GITHUB_ACTIONS -eq 'true') -or (-not [string]::IsNullOrEmpty($env:TF_BUILD))
        $script:skipRuntimeInstall = $script:skip -or -not ($onWindows -and ($onCI -or $AllowRuntimeInstall))

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:launchedPid = $null
        $script:appProcessName = 'winui-unpackaged-app'
        $script:originalLocation = Get-Location

        # The globally-installed winapp CLI is only used by Phase 1 (project-mode launch). Gate the
        # global npm install on the same condition so a local run performs no global install either.
        if (-not $script:skipRuntimeInstall) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }

        # Resolves how to invoke the winapp CLI as an external process:
        # prefer a globally-installed `winapp`, otherwise fall back to `dotnet run`
        # against the repo CLI project. Returns @{ File; Args }.
        function Resolve-WinappInvocation {
            param([string[]]$Arguments)
            $pathWinapp = Get-Command winapp -ErrorAction SilentlyContinue
            $cliProject = Join-Path $PSScriptRoot '..\..\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj'
            if ($pathWinapp) {
                # `winapp` on PATH is typically the npm package's batch shim (winapp.cmd).
                # Start-Process -NoNewWindow launches via CreateProcess, which cannot execute
                # a .cmd directly (Win32 error 193: "%1 is not a valid Win32 application").
                # Route through cmd.exe (a real Win32 host) so the shim runs; `cmd /d /c`
                # propagates winapp's exit code back to $proc.ExitCode.
                return @{ File = 'cmd.exe'; Args = @('/d', '/c', 'winapp') + $Arguments }
            }
            if (Test-Path $cliProject) {
                return @{ File = 'dotnet'; Args = @('run', '--project', (Resolve-Path $cliProject).Path, '--') + $Arguments }
            }
            return @{ File = 'cmd.exe'; Args = @('/d', '/c', 'winapp') + $Arguments }
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        if ($script:launchedPid) {
            Stop-Process -Id $script:launchedPid -Force -ErrorAction SilentlyContinue
        }
        # Belt-and-suspenders: clean up any lingering sample process by name — but ONLY when the
        # launch phase actually ran. Otherwise a local run without -AllowRuntimeInstall would kill
        # unrelated processes that happen to share the sample's name. (PID cleanup above is always
        # safe — it only targets what we launched.)
        if (-not $script:skipRuntimeInstall) {
            Get-Process -Name $script:appProcessName -ErrorAction SilentlyContinue |
                ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        }

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Phase 1 exercises `winapp run` PROJECT MODE end-to-end against an unpackaged
    # WinUI app from a clean directory: SDK probe -> build + property resolution ->
    # unpackaged detection -> reused Windows App Runtime install -> direct .exe launch.
    # The "C2" must-have: an unpackaged app boots off the reused runtime.
    #
    # SIDE EFFECT: the launch path installs the Windows App Runtime MSIX system-wide (a
    # machine-global change) and starts the sample app. That is fine on a throwaway CI
    # runner but a footgun on a developer box, so this whole Context is gated behind
    # $script:skipRuntimeInstall (Windows + CI, or -AllowRuntimeInstall). The launched app
    # is always torn down again in the top-level AfterAll (by PID, then by name).
    Context 'Phase 1: Project-mode run (from scratch)' -Skip:$script:skipRuntimeInstall {

        BeforeAll {
            if (-not $script:skipRuntimeInstall) {
                $script:tempDir = New-TempTestDirectory -Prefix 'winui-unpackaged'

                # Copy the sample sources (never bin/obj) into a clean temp project dir.
                Get-ChildItem -Path $script:sampleDir -File | Where-Object { $_.Name -ne 'test.Tests.ps1' } |
                    ForEach-Object { Copy-Item -Path $_.FullName -Destination $script:tempDir }

                # Make sure no stale instance is running before we launch.
                Get-Process -Name $script:appProcessName -ErrorAction SilentlyContinue |
                    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            }
        }

        It 'Builds and launches the unpackaged app with winapp run .' -Skip:$script:skipRuntimeInstall {
            # Launch via Start-Process (NOT a captured pipe): the unpackaged app inherits
            # the console stdout handle, so capturing it in a pipeline would block until the
            # app exits. --detach makes winapp return as soon as the app is launched.
            #
            # Do NOT use Start-Process -Wait: -Wait blocks until the launched process AND ALL
            # ITS DESCENDANTS exit. The unpackaged app is a descendant of winapp and runs
            # indefinitely, so -Wait would hang. Instead wait only on winapp's own process, and
            # bound that wait: a winapp that never returns fails here with the process tree it
            # left behind, instead of holding the job until CI cancels it.
            $invocation = Resolve-WinappInvocation -Arguments @('run', '.', '--detach')
            $proc = Start-Process -FilePath $invocation.File -ArgumentList $invocation.Args `
                -WorkingDirectory $script:tempDir -NoNewWindow -PassThru
            Wait-ProcessExitOrFail -Process $proc -Timeout ([TimeSpan]::FromMinutes(10)) `
                -Description 'winapp run . --detach'
            $proc.ExitCode | Should -Be 0 -Because 'winapp run --detach should build and launch, then return 0'

            $app = Get-Process -Name $script:appProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
            $app | Should -Not -BeNullOrEmpty -Because 'the unpackaged app process should be running after launch'
            $script:launchedPid = $app.Id
        }

        It 'Boots off the reused Windows App Runtime (process stays alive)' -Skip:$script:skipRuntimeInstall {
            $script:launchedPid | Should -Not -BeNullOrEmpty
            # A framework-dependent unpackaged WinUI app that could not find its Windows App
            # Runtime would fail in the bootstrapper almost immediately after launch.
            Start-Sleep -Seconds 3
            $proc = Get-Process -Id $script:launchedPid -ErrorAction SilentlyContinue
            $proc | Should -Not -BeNullOrEmpty -Because 'the app should still be running after booting off the installed runtime'
        }

        It 'Detects the project as unpackaged (WindowsPackageType=None)' -Skip:$script:skipRuntimeInstall {
            # Evaluate WindowsPackageType WITHOUT compiling: `dotnet build --getProperty` with no
            # explicit target restores + evaluates the project and prints the property, but never
            # runs the Build target (verified: no output assembly is produced). This assertion is
            # deliberately ordered AFTER the `winapp run .` test above so it can never populate
            # bin/obj ahead of that cold first-invocation path, which must exercise project mode's
            # restore and CsWinRT metadata-shim logic from scratch.
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
            Push-Location $script:tempDir
            try {
                $wpt = dotnet build 'winui-unpackaged-app.csproj' -c Debug -r $rid --getProperty:WindowsPackageType
                $LASTEXITCODE | Should -Be 0
            } finally {
                Pop-Location
            }
            "$wpt".Trim() | Should -Be 'None'
        }
    }

    Context 'Phase 2: Sample Build Check' {

        BeforeAll {
            if (-not $script:skip) {
                Push-Location $script:sampleDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It 'Restores NuGet packages' -Skip:$script:skip {
            dotnet restore
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds existing sample in Debug mode' -Skip:$script:skip {
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
            dotnet build -c Debug -r $rid
            $LASTEXITCODE | Should -Be 0
        }
    }
}
