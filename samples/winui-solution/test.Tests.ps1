param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe 'winui-solution sample' {

    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:originalLocation = Get-Location

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        # Phase 1 registers a loose-layout dev package for the App project
        # (winapp run <sln> --no-launch). Unregister it before the backing files are
        # deleted so we don't leave a dangling registration on the machine / CI runner.
        # Best-effort: read the identity Name from the App manifest and remove any match.
        try {
            $manifestPath = Join-Path $script:sampleDir 'App\Package.appxmanifest'
            if (Test-Path $manifestPath) {
                [xml]$manifestXml = Get-Content -Path $manifestPath -Raw
                $identityName = $manifestXml.Package.Identity.Name
                if ($identityName) {
                    Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue |
                        ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                }
            }
        } catch {
            Write-Warning "winui-solution cleanup: failed to unregister dev package: $_"
        }

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            foreach ($proj in @('App', 'App.Core', 'App.Tests')) {
                Remove-Item -Path (Join-Path $script:sampleDir "$proj\bin") -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir "$proj\obj") -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Phase 1 exercises `winapp run` SOLUTION MODE against a multi-project .slnx from a
    # clean directory. The solution contains a packaged WinUI app (App) and a
    # test-shaped project (App.Tests: WinExe + TestContainer capability + MSTest, no
    # IsTestProject). winapp run must auto-select the runnable App and skip the test
    # project WITHOUT an ambiguity error, then build + register it (no launch, no GUI).
    Context 'Phase 1: Solution-mode auto-selection (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'winui-solution'

                # Copy the sample sources (never bin/obj) into a clean temp project dir.
                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        $target = Join-Path $script:tempDir $relative
                        $targetDir = Split-Path $target -Parent
                        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                Push-Location $script:tempDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It 'Auto-selects the runnable app from the solution and builds + registers it (no ambiguity)' -Skip:$script:skip {
            # No --project: solution mode must pick App (the only runnable app) and skip
            # App.Tests. --no-launch builds the loose layout + registers a debug identity
            # without launching (deterministic in CI). Invoke-WinappCommand throws on a
            # non-zero exit, so an ambiguity error (which is non-zero) would fail here.
            Invoke-WinappCommand -Arguments 'run WinUISolution.slnx --no-launch'

            # Prove App (not App.Tests) is the project that got built.
            (Get-ChildItem -Path 'App\bin' -Recurse -Filter 'App.dll' -ErrorAction SilentlyContinue) |
                Should -Not -BeNullOrEmpty
        }

        It 'Resolves an explicitly selected test project via --project' -Skip:$script:skip {
            # --project App.Tests must reach the test project. App.Tests is an unpackaged
            # WinExe, so --no-launch is rejected (non-zero) -> Invoke-WinappCommand throws.
            # If --project were ignored and the packaged App were selected instead,
            # --no-launch would be accepted (exit 0) and this would NOT throw -- so the
            # throw proves explicit selection reached App.Tests.
            { Invoke-WinappCommand -Arguments 'run WinUISolution.sln --project App.Tests --no-launch' } |
                Should -Throw
        }

        It 'Restores a configuration-free slnx without requesting a solution Platform' -Skip:$script:skip {
            # App.Tests declares the host architecture, so winapp resolves a project Platform. That
            # Platform must stay on project-targeted passes: passing it to this configuration-free .slnx
            # makes real MSBuild print MSB4126 before winapp falls back to individual restores.
            $output = Invoke-WinappCommand -Arguments 'run WinUISolution.slnx --project App.Tests --detach'
            ($output -join [Environment]::NewLine) | Should -Not -Match 'MSB4126'
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

        It 'Restores NuGet packages for the solution' -Skip:$script:skip {
            dotnet restore WinUISolution.sln
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds the existing solution in Debug mode' -Skip:$script:skip {
            # Building a .sln with an explicit -r/RID is unsupported (NETSDK1134); the RID
            # is resolved per-project. Drive the arch via -p:Platform only.
            $plat = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'ARM64' } else { 'x64' }
            dotnet build WinUISolution.sln -c Debug -p:Platform=$plat
            $LASTEXITCODE | Should -Be 0
        }
    }
}
