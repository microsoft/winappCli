#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for Windows App Development CLI, npm package, NuGet packages, and MSIX packages
.DESCRIPTION
    This script builds the Windows App Development CLI for both x64 and arm64 architectures,
    creates the npm package, the NuGet packages (the BuildTools.WinApp CLI tools package plus the
    UI Automation library packages), creates MSIX packages
    with distribution package, and places all artifacts in an artifacts folder. 
    Run this script from the root of the project.
.PARAMETER SkipTests
    Skip running unit tests
.PARAMETER FailOnTestFailure
    Exit with error code if tests fail (default: true, stops build on test failures)
.PARAMETER SkipNpm
    Skip npm package creation
.PARAMETER SkipNuGet
    Skip NuGet package creation (BuildTools.WinApp and the UI Automation libraries)
.PARAMETER SkipMsix
    Skip MSIX packages creation
.PARAMETER SkipDocs
    Skip CLI schema generation and plugin manifest version synchronization
.PARAMETER SkipAll
    Skip NuGet, MSIX, npm, tests, and docs (only builds the CLI)
.PARAMETER OnlyDocs
    Skip NuGet, MSIX, npm, and tests (builds the CLI and generates docs). Alias: DocsOnly
.PARAMETER OnlyTests
    Skip NuGet, MSIX, docs, and npm package creation (builds the CLI and runs tests). Alias: TestsOnly
.PARAMETER Stable
    Use stable build configuration (default: false, uses prerelease config)
.PARAMETER SkipBake
    Skip refreshing the find-ui corpus baked into the CLI. The bake only runs on -Stable
    builds; use this to opt out of it on a release build (the committed snapshot is shipped
    as-is). This is the deliberate override for a known-transient upstream outage: without
    it, a bake that fails on a stable build fails the build.
.PARAMETER Bake
    Refresh the find-ui corpus even on a prerelease build. Requires network access to GitHub.
.EXAMPLE
    .\scripts\build-cli.ps1
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipTests
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipNpm
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipNuGet
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipMsix
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipAll
.EXAMPLE
    .\scripts\build-cli.ps1 -OnlyDocs
.EXAMPLE
    .\scripts\build-cli.ps1 -DocsOnly
.EXAMPLE
    .\scripts\build-cli.ps1 -OnlyTests
.EXAMPLE
    .\scripts\build-cli.ps1 -TestsOnly
.EXAMPLE
    .\scripts\build-cli.ps1 -Stable
.EXAMPLE
    .\scripts\build-cli.ps1 -Bake
#>

param(
    [switch]$Clean = $false,
    [switch]$SkipTests = $false,
    [switch]$FailOnTestFailure = $true,
    [switch]$SkipNpm = $false,
    [switch]$SkipNuGet = $false,
    [switch]$SkipMsix = $false,
    [switch]$SkipDocs = $false,
    [switch]$SkipAll = $false,
    [Alias("DocsOnly")]
    [switch]$OnlyDocs = $false,
    [Alias("TestsOnly")]
    [switch]$OnlyTests = $false,
    [switch]$Stable = $false,
    [switch]$SkipBake = $false,
    [switch]$Bake = $false
)

# Validate compound flag usage
$CompoundFlagsCount = @($SkipAll, $OnlyDocs, $OnlyTests) | Where-Object { $_ } | Measure-Object | Select-Object -ExpandProperty Count
if ($CompoundFlagsCount -gt 1) {
    Write-Error "Only one of -SkipAll, -OnlyDocs/-DocsOnly, or -OnlyTests/-TestsOnly can be specified."
    exit 1
}

# Apply compound skip flags
if ($SkipAll) {
    $SkipNuGet = $true
    $SkipMsix = $true
    $SkipNpm = $true
    $SkipTests = $true
    $SkipDocs = $true
} elseif ($OnlyDocs) {
    $SkipNuGet = $true
    $SkipMsix = $true
    $SkipNpm = $true
    $SkipTests = $true
} elseif ($OnlyTests) {
    $SkipNuGet = $true
    $SkipMsix = $true
    $SkipNpm = $true
    $SkipDocs = $true
}

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Push-Location $ProjectRoot
try
{
    # Define paths
    $CliSolutionDir = "src\winapp-CLI"
    $CliSolutionPath = "$CliSolutionDir\winapp.sln"
    $CliProjectPath = "$CliSolutionDir\WinApp.Cli\WinApp.Cli.csproj"
    $CliTestsProjectPath = "$CliSolutionDir\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj"
    $UiAutomationTestsProjectPath = "$CliSolutionDir\WinApp.UIAutomation.Tests\WinApp.UIAutomation.Tests.csproj"
    # Build-time only, never shipped: regenerates the embedded find-ui corpus.
    $SnapshotBakerProjectPath = "$CliSolutionDir\WinApp.Cli.SnapshotBaker\WinApp.Cli.SnapshotBaker.csproj"
    $ArtifactsPath = "artifacts"
    $TestResultsPath = "TestResults"

    Write-Host "[*] Starting Windows SDK build process..." -ForegroundColor Green
    Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray
    if ($Stable) {
        Write-Host "Build mode: STABLE (no prerelease suffix)" -ForegroundColor Cyan
    } else {
        Write-Host "Build mode: PRERELEASE (with prerelease suffix)" -ForegroundColor Cyan
    }

    Write-Host "[CLEAN] Cleaning artifacts and test results..." -ForegroundColor Yellow
    if (Test-Path $ArtifactsPath) {
        Remove-Item $ArtifactsPath -Recurse -Force
    }
    if (Test-Path $TestResultsPath) {
        Remove-Item $TestResultsPath -Recurse -Force
    }

    # Create artifacts directory
    Write-Host "[SETUP] Creating artifacts directory..." -ForegroundColor Blue
    New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null

    # Step 1: Calculate version
    Write-Host "[VERSION] Calculating package version..." -ForegroundColor Blue

    # Read base version from version.json
    $VersionJsonPath = "$ProjectRoot\version.json"
    if (-not (Test-Path $VersionJsonPath)) {
        Write-Error "version.json not found at $VersionJsonPath"
        exit 1
    }

    $VersionJson = Get-Content $VersionJsonPath | ConvertFrom-Json
    $BaseVersion = $VersionJson.version

    # Get build number
    $BuildNumber = & "$PSScriptRoot\get-build-number.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to get build number"
        exit 1
    }

    # Determine prerelease label based on current branch
    # - main and rel/* branches use "prerelease" (default)
    # - all other branches use a sanitized branch name (e.g., dev/my-feature -> dev-my-feature)
    $PrereleaseLabel = & "$PSScriptRoot\get-prerelease-label.ps1"
    Write-Host "[VERSION] Prerelease label: $PrereleaseLabel" -ForegroundColor Gray

    # Construct full version based on Stable flag
    if ($Stable) {
        # Stable build: use semantic version without prerelease suffix (e.g., "0.1.0")
        $FullVersion = $BaseVersion
        Write-Host "[VERSION] Using stable version (no prerelease suffix)" -ForegroundColor Cyan
    } else {
        # Prerelease build: add prerelease label suffix (e.g., "0.1.0-prerelease.73" or "0.1.0-dev-my-feature.73")
        $FullVersion = "$BaseVersion-$PrereleaseLabel.$BuildNumber"
        Write-Host "[VERSION] Using prerelease version (with $PrereleaseLabel suffix)" -ForegroundColor Cyan
    }
    Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan

    # Extract semantic version components for assembly versioning
    # BaseVersion should be in format major.minor.patch (e.g., "0.1.0")
    $VersionParts = $BaseVersion -split '\.'
    $MajorVersion = $VersionParts[0]
    $MinorVersion = $VersionParts[1]
    $PatchVersion = $VersionParts[2]

    # Assembly version uses format: major.minor.patch.buildnumber (e.g., "0.1.0.73")
    $AssemblyVersion = "$MajorVersion.$MinorVersion.$PatchVersion.$BuildNumber"
    Write-Host "[VERSION] Assembly version: $AssemblyVersion" -ForegroundColor Cyan

    # InformationalVersion shows in --version output (e.g., "0.1.0-prerelease.73")
    $InformationalVersion = $FullVersion

    # Step 1b: Refresh the find-ui corpus baked into the binary.
    #
    # The snapshot is an EmbeddedResource, so it must be regenerated BEFORE the publish
    # below or the release ships the previous corpus. It only runs for stable builds
    # (or explicit -Bake): every prerelease re-baking would churn a ~930 KB committed diff
    # for no benefit, since the snapshot only reaches users when they upgrade the CLI.
    #
    # A bake failure fails a stable build. The baker fetches through the same providers
    # `find-ui --refresh` uses at runtime and accepts nothing but CorpusOrigin.Network, so a
    # bake that cannot complete is direct evidence that the refresh path is broken for users
    # too. Shipping the previously committed corpus in that state would hide the breakage
    # behind data that is already stale and that nobody in the field can update -- the
    # release has to stop and a human has to look. -SkipBake is the deliberate override for
    # an upstream outage known to be transient.
    #
    # Equally fatal is having no corpus at all on a stable build: that ships a CLI whose
    # find-ui is non-functional offline, the exact regression the embedded snapshot exists
    # to prevent (issue #704).
    $SnapshotDataPath = "$CliSolutionDir\WinApp.Cli\Services\Controls\Data"
    $ShouldBake = ($Stable -or $Bake) -and (-not $SkipBake)

    if ($ShouldBake) {
        Write-Host "[BAKE] Refreshing find-ui corpus from GitHub..." -ForegroundColor Blue

        # Keep the last known good set aside. The baker stages a bake and only publishes it
        # once every source and the manifest have succeeded, so a failed run should leave the
        # committed corpus untouched -- this backup covers the residue of a crash or a kill
        # partway through that publish, where restoring wholesale is the only way to get a
        # coherent corpus back.
        $BakeBackup = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-bake-backup-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $BakeBackup -Force | Out-Null
        $PreviousSnapshots = @(Get-ChildItem -Path $SnapshotDataPath -Filter "snapshot-*" -ErrorAction SilentlyContinue)
        $PreviousSnapshots | Copy-Item -Destination $BakeBackup -Force

        dotnet run --project $SnapshotBakerProjectPath -c Debug -- $SnapshotDataPath
        $BakeExitCode = $LASTEXITCODE

        if ($BakeExitCode -ne 0) {
            # Restore before deciding what to do about it, so a bake that died partway
            # through its publish never leaves a half-written corpus in the working tree.
            Get-ChildItem -Path $SnapshotDataPath -Filter "snapshot-*" -ErrorAction SilentlyContinue | Remove-Item -Force
            Get-ChildItem -Path $BakeBackup -ErrorAction SilentlyContinue | Copy-Item -Destination $SnapshotDataPath -Force
            Remove-Item $BakeBackup -Recurse -Force -ErrorAction SilentlyContinue

            if ($Stable) {
                Write-Error "Stable build aborted: the find-ui corpus refresh failed (exit $BakeExitCode). The baker fetches through the same providers 'winapp find-ui --refresh' uses, so this failure means users cannot refresh either -- shipping the previously committed corpus would hide that behind data nobody can update. Investigate upstream, then re-run. To ship the committed corpus anyway for a known-transient outage, re-run with -SkipBake."
                exit 1
            }

            Write-Warning "[BAKE] Corpus refresh failed (exit $BakeExitCode). Restored the previously committed snapshot; not a stable build, so continuing."
        } else {
            Write-Host "[BAKE] Corpus refreshed." -ForegroundColor Green
            Remove-Item $BakeBackup -Recurse -Force -ErrorAction SilentlyContinue
        }
    } elseif ($Stable) {
        Write-Warning "[BAKE] Skipped (-SkipBake); shipping the committed find-ui corpus as-is. It may be several releases behind upstream."
    }

    # Whether or not we just baked, a stable build must carry a complete, working corpus.
    # This catches the restored-from-backup case as well as a repo that never had one.
    #
    # Presence is not enough now that only the compressed blob is committed: a truncated or
    # half-written blob is still a file of non-zero length, and it would fail at runtime as
    # "no embedded corpus" -- silently reinstating issue #704 in a shipped build. So the
    # blobs are actually decompressed and parsed here, and cross-checked against the
    # manifest that the CLI, the drift job and the release all read.
    if ($Stable) {
        $RequiredSnapshots = @("snapshot-manifest.json", "snapshot-gallery.json.br", "snapshot-toolkit.json.br", "snapshot-reactor.json.br")
        $MissingSnapshots = @(
            $RequiredSnapshots | Where-Object {
                $p = Join-Path $SnapshotDataPath $_
                (-not (Test-Path $p)) -or ((Get-Item $p).Length -eq 0)
            }
        )
        if ($MissingSnapshots.Count -gt 0) {
            Write-Error "Stable build is missing the find-ui corpus: $($MissingSnapshots -join ', '). Shipping without it leaves find-ui non-functional offline (issue #704). Run: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
            exit 1
        }

        $ManifestPath = Join-Path $SnapshotDataPath "snapshot-manifest.json"
        try {
            $Manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
        } catch {
            Write-Error "Stable build aborted: snapshot-manifest.json does not parse ($($_.Exception.Message)). Re-bake: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
            exit 1
        }

        # A CacheVersion bump without a matching re-bake makes EmbeddedSnapshot reject the
        # corpus at runtime, which removes the offline floor without failing anything else.
        $CacheVersionSource = Join-Path $PSScriptRoot "..\src\winapp-CLI\WinApp.Cli\Services\Controls\CacheVersion.cs"
        $CurrentCacheVersion = (Select-String -Path $CacheVersionSource -Pattern 'Current\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
        if ($Manifest.cacheVersion -ne $CurrentCacheVersion) {
            Write-Error "Stable build aborted: the find-ui corpus was baked at CacheVersion '$($Manifest.cacheVersion)' but the code is at '$CurrentCacheVersion'. EmbeddedSnapshot rejects a version mismatch, so this would ship with no offline corpus. Re-bake: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
            exit 1
        }

        foreach ($Provider in @("gallery", "toolkit", "reactor")) {
            $BlobPath = Join-Path $SnapshotDataPath "snapshot-$Provider.json.br"
            try {
                $Raw = [System.IO.File]::ReadAllBytes($BlobPath)
                $InStream = New-Object System.IO.MemoryStream(, $Raw)
                $OutStream = New-Object System.IO.MemoryStream
                $Brotli = New-Object System.IO.Compression.BrotliStream($InStream, [System.IO.Compression.CompressionMode]::Decompress)
                $Brotli.CopyTo($OutStream)
                $Brotli.Dispose()
                $Snapshot = [System.Text.Encoding]::UTF8.GetString($OutStream.ToArray()) | ConvertFrom-Json
            } catch {
                Write-Error "Stable build aborted: snapshot-$Provider.json.br is corrupt or truncated ($($_.Exception.Message)). find-ui would start with no offline corpus for '$Provider'. Re-bake: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
                exit 1
            }

            $ActualCount = @($Snapshot.scenarios).Count
            if ($ActualCount -eq 0) {
                Write-Error "Stable build aborted: snapshot-$Provider.json.br contains no scenarios. The '$Provider' source would silently vanish from find-ui offline. Re-bake: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
                exit 1
            }

            $DeclaredCount = $Manifest.scenarioCounts.$Provider
            if ($ActualCount -ne $DeclaredCount) {
                Write-Error "Stable build aborted: snapshot-manifest.json declares $DeclaredCount scenarios for '$Provider' but the blob carries $ActualCount. The manifest and the blobs are written by one bake, so they have been edited or partially re-baked. Re-bake: dotnet run --project $SnapshotBakerProjectPath -- $SnapshotDataPath"
                exit 1
            }

            Write-Host "[BAKE] Verified $Provider corpus: $ActualCount scenarios." -ForegroundColor Green
        }
    }

    # Step 2: Publish CLI for x64 and arm64 (implicitly builds the CLI project)
    Write-Host "[PUBLISH] Publishing CLI for x64..." -ForegroundColor Blue
    dotnet publish $CliProjectPath -c Release -r win-x64 --self-contained -o "$ArtifactsPath\cli\win-x64" `
        /p:Version=$AssemblyVersion `
        /p:AssemblyVersion=$AssemblyVersion `
        /p:FileVersion=$AssemblyVersion `
        /p:InformationalVersion=$InformationalVersion `
        /p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish CLI for x64"
        exit 1
    }

    Write-Host "[PUBLISH] Publishing CLI for arm64..." -ForegroundColor Blue
    dotnet publish $CliProjectPath -c Release -r win-arm64 --self-contained -o "$ArtifactsPath\cli\win-arm64" `
        /p:Version=$AssemblyVersion `
        /p:AssemblyVersion=$AssemblyVersion `
        /p:FileVersion=$AssemblyVersion `
        /p:InformationalVersion=$InformationalVersion `
        /p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish CLI for arm64"
        exit 1
    }

    # Step 3: Build the solution in Debug to compile the tests. Coverage is collected on
    # this Debug build (Step 5) -- optimized Release builds under-count line coverage (many
    # block-brace lines report hits=0). The shipped CLI artifact is the Release `dotnet publish`
    # above; this Debug build exists only to run the test suite. See issue #630.
    # TreatWarningsAsErrors is Release-only (Directory.Build.props), so pass it explicitly here
    # to keep the warning-as-error quality gate the previous Release test build provided.
    Write-Host "[BUILD] Building CLI solution (Debug, for tests + coverage)..." -ForegroundColor Blue
    dotnet build $CliSolutionPath -c Debug -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build CLI solution"
        exit 1
    }

    # Step 4: Build Node CLI so E2E tests that invoke node cli.js can run
    if ((-not $SkipNpm) -or (-not $SkipTests)) {
        Write-Host "[BUILD] Building Node CLI (for tests)..." -ForegroundColor Blue
        Push-Location (Join-Path $ProjectRoot "src\winapp-npm")
        try {
            npm ci --ignore-scripts
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "npm ci failed, Node E2E tests will be skipped"
            } else {
                npm run generate-commands
                npm run compile
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Node CLI compile failed, Node E2E tests will be skipped"
                } else {
                    Write-Host "[BUILD] Node CLI built successfully" -ForegroundColor Green

                    # Run npm-side TypeScript unit tests (pure-logic jsbindings modules
                    # + CLI arg parser). Gated on -not $SkipTests like the C# suite.
                    if (-not $SkipTests) {
                        Write-Host "[TEST] Running npm unit tests..." -ForegroundColor Blue
                        npm test
                        if ($LASTEXITCODE -ne 0) {
                            Write-Warning "npm unit tests failed with exit code $LASTEXITCODE"
                            if ($FailOnTestFailure) {
                                Pop-Location
                                Write-Error "Stopping build due to npm unit test failures (FailOnTestFailure flag set)"
                                exit 1
                            } else {
                                Write-Host "[TEST] Continuing build despite npm unit test failures..." -ForegroundColor Yellow
                            }
                        } else {
                            Write-Host "[TEST] npm unit tests passed!" -ForegroundColor Green
                        }
                    }
                }
            }
        } finally {
            Pop-Location
        }
    }

    # Step 5: Run tests (unless skipped)
    if (-not $SkipTests) {
        Write-Host "[TEST] Running tests..." -ForegroundColor Blue
        # Measure coverage honestly. Two things distort the raw number: (1) auto-generated
        # interop (CsWin32/COM/Regex generators in obj\**) inflates the denominator -- excluded
        # via coverage.runsettings; (2) optimized Release builds report many block-brace lines as
        # hits=0, so line coverage is under-counted -- so we collect on the Debug build from
        # Step 3. Hand-written services (incl. the hardware/COM/GPU interop) are NOT excluded;
        # they are covered by real tests. See issue #630.
        $CoverageSettings = (Resolve-Path "$CliSolutionDir\coverage.runsettings").Path
        # Every test project runs. WinApp.UIAutomation.Tests covers the automation engine, which now
        # lives in its own assembly -- running only WinApp.Cli.Tests would leave it untested in CI.
        $TestExitCode = 0
        foreach ($TestProject in @($CliTestsProjectPath, $UiAutomationTestsProjectPath)) {
            $TestProjectName = [System.IO.Path]::GetFileNameWithoutExtension($TestProject)
            Write-Host "[TEST] Running $TestProjectName..." -ForegroundColor Blue
            dotnet run --project $TestProject -c Debug --no-build --results-directory $CliSolutionDir\TestResults --report-trx --report-trx-filename "$TestProjectName.trx" --coverage --coverage-settings $CoverageSettings --coverage-output-format cobertura --coverage-output "$TestProjectName.cobertura.xml"
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "$TestProjectName failed with exit code $LASTEXITCODE"
                $TestExitCode = $LASTEXITCODE
            }
        }
    
        # Copy test results to artifacts BEFORE checking for failure - find all TRX files
        Write-Host "[TEST] Collecting test results..." -ForegroundColor Blue
        New-Item -ItemType Directory -Path "$ArtifactsPath\TestResults" -Force | Out-Null
        $TrxFiles = Get-ChildItem -Path $CliSolutionDir -Filter "*.trx" -Recurse -File
        if ($TrxFiles) {
            foreach ($trxFile in $TrxFiles) {
                Copy-Item $trxFile.FullName "$ArtifactsPath\TestResults\" -Force
                Write-Host "[TEST] Copied: $($trxFile.Name)" -ForegroundColor Gray
            }
            Write-Host "[TEST] Test results copied successfully ($($TrxFiles.Count) file(s))" -ForegroundColor Green
        } else {
            Write-Warning "No TRX test result files found in $CliSolutionDir"
        }

        # Copy coverage XML files to artifacts
        $CoverageFiles = Get-ChildItem -Path $CliSolutionDir -Filter "*.cobertura.xml" -Recurse -File
        if ($CoverageFiles) {
            foreach ($coverageFile in $CoverageFiles) {
                Copy-Item $coverageFile.FullName "$ArtifactsPath\TestResults\" -Force
                Write-Host "[TEST] Copied coverage: $($coverageFile.Name)" -ForegroundColor Gray
            }
            Write-Host "[TEST] Coverage results copied successfully ($($CoverageFiles.Count) file(s))" -ForegroundColor Green
        } else {
            Write-Warning "No coverage XML files found in $CliSolutionDir"
        }

        # Now check test results and decide whether to exit
        if ($TestExitCode -ne 0) {
            Write-Warning "Tests failed with exit code $TestExitCode"
            if ($FailOnTestFailure) {
                Write-Error "Stopping build due to test failures (FailOnTestFailure flag set)"
                exit 1
            } else {
                Write-Host "[TEST] Continuing build despite test failures..." -ForegroundColor Yellow
            }
        } else {
            Write-Host "[TEST] Tests passed!" -ForegroundColor Green
        }
    } else {
        Write-Host "[TEST] Skipping tests (SkipTests flag set)" -ForegroundColor Yellow
    }

    # Step 6: Generate CLI schema and synchronize plugin versions (optional)
    if (-not $SkipDocs) {
        Write-Host ""
        Write-Host "[DOCS] Generating CLI schema and synchronizing plugin versions..." -ForegroundColor Blue
        
        $GenerateLlmDocsScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
        $CliExePath = Join-Path $ProjectRoot "$ArtifactsPath\cli\win-x64\winapp.exe"
        
        & $GenerateLlmDocsScript -CliPath $CliExePath -CalledFromBuildScript
        
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "CLI schema generation failed, but continuing..."
        } else {
            Write-Host "[DOCS] CLI schema generated successfully!" -ForegroundColor Green
        }
    } else {
        Write-Host ""
        Write-Host "[DOCS] Skipping CLI schema generation (-SkipDocs)" -ForegroundColor Yellow
    }

    # Step 7: Create npm package (optional)
    if (-not $SkipNpm) {
        Write-Host ""
        Write-Host "[NPM] Creating npm package..." -ForegroundColor Blue
    
        $PackageNpmScript = Join-Path $PSScriptRoot "package-npm.ps1"

        & $PackageNpmScript -Version $FullVersion -Stable:$Stable

        if ($LASTEXITCODE -ne 0) {
            Write-Error "npm package creation failed"
            exit 1
        }

        # Generate npm API documentation from TypeScript source (after npm build so codegen is fresh)
        Write-Host "[NPM] Generating npm API documentation..." -ForegroundColor Blue
        Push-Location (Join-Path $ProjectRoot "src\winapp-npm")
        try {
            npm run generate-docs
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "npm API documentation generation failed, but continuing..."
            } else {
                Write-Host "[NPM] npm API documentation generated successfully!" -ForegroundColor Green
            }
        } finally {
            Pop-Location
        }
    } else {
        Write-Host ""
        Write-Host "[NPM] Skipping npm package creation (use -SkipNpm:`$false to enable)" -ForegroundColor Gray
    }

    # Step 8: Create NuGet packages (optional)
    if (-not $SkipNuGet) {
        Write-Host ""
        Write-Host "[NUGET] Creating NuGet packages..." -ForegroundColor Blue

        # package-nuget.ps1 builds all three: the CLI tools package plus the two UI Automation
        # library packages.
        $PackageNuGetScript = Join-Path $PSScriptRoot "package-nuget.ps1"

        & $PackageNuGetScript -Version $FullVersion -Stable:$Stable

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "NuGet packages creation failed, but continuing..."
        } else {
            Write-Host "[NUGET] NuGet packages created successfully!" -ForegroundColor Green
        }

        # Assert the full set landed. Packaging failures above are warnings so a build can still
        # produce partial output locally, but CI uploads this folder wholesale — without this
        # check a missing package would ship as a green build with an incomplete artifact.
        $ExpectedPackages = @(
            'Microsoft.Windows.SDK.BuildTools.WinApp',
            'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation',
            'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording'
        )
        $NuGetOutput = Join-Path $ProjectRoot "artifacts\nuget"
        $BuiltPackages = @(Get-ChildItem $NuGetOutput -Filter '*.nupkg' -ErrorAction SilentlyContinue |
            ForEach-Object {
                # <id>.<version>.nupkg — the ids share a prefix, so split on the version instead.
                if ($_.Name -match '^(?<id>.+?)\.(?<version>\d+\.\d+\.\d+.*)\.nupkg$') { $Matches.id }
            })
        $MissingPackages = @($ExpectedPackages | Where-Object { $BuiltPackages -notcontains $_ })
        if ($MissingPackages.Count -gt 0) {
            Write-Error "Expected NuGet package(s) missing from artifacts/nuget: $($MissingPackages -join ', ')"
            exit 1
        }
        Write-Host "[NUGET] Verified all $($ExpectedPackages.Count) packages are present" -ForegroundColor Green

        # Run NuGet Pester tests (gate matrix + dual-pack layout parity).
        # Skipped if -SkipTests was passed.
        if (-not $SkipTests) {
            $NuGetTestsPath = Join-Path $ProjectRoot "src\winapp-NuGet\tests\NuGet.Tests.ps1"
            if (Test-Path $NuGetTestsPath) {
                $pesterMod = Get-Module -Name Pester -ListAvailable | Where-Object { $_.Version.Major -ge 5 } | Select-Object -First 1
                if ($pesterMod) {
                    Write-Host "[TEST] Running NuGet Pester tests..." -ForegroundColor Blue
                    $pesterConfig = New-PesterConfiguration
                    $pesterConfig.Run.Path = $NuGetTestsPath
                    $pesterConfig.Run.Exit = $false
                    $pesterConfig.Run.PassThru = $true
                    $pesterConfig.Output.Verbosity = 'Normal'
                    $pesterResult = Invoke-Pester -Configuration $pesterConfig
                    if (($pesterResult.FailedCount + $pesterResult.FailedBlocksCount + $pesterResult.FailedContainersCount) -gt 0) {
                        if ($FailOnTestFailure) {
                            Write-Error "Stopping build due to NuGet Pester test failures (FailOnTestFailure flag set): $($pesterResult.FailedCount) failed test(s), $($pesterResult.FailedBlocksCount) failed block(s), $($pesterResult.FailedContainersCount) failed container(s)"
                            exit 1
                        } else {
                            Write-Warning "NuGet Pester tests had $($pesterResult.FailedCount) failed test(s), $($pesterResult.FailedBlocksCount) failed block(s), $($pesterResult.FailedContainersCount) failed container(s) — continuing"
                        }
                    } else {
                        Write-Host "[TEST] NuGet Pester tests passed: $($pesterResult.PassedCount) passed, $($pesterResult.SkippedCount) skipped" -ForegroundColor Green
                    }
                } else {
                    Write-Warning "Pester 5.x not installed — skipping NuGet Pester tests. Install with: Install-Module Pester -Force -MinimumVersion 5.0"
                }
            }
        }
    } else {
        Write-Host ""
        Write-Host "[NUGET] Skipping NuGet packages creation (use -SkipNuGet:`$false to enable)" -ForegroundColor Gray
    }

    # Run the scripts/tests Pester suite (MS Learn docs validator + shared test helpers).
    # These gate the release doc-porting job and the sample test harness, so keep them green.
    # Skipped with -SkipTests.
    if (-not $SkipTests) {
        $ScriptsTestsPath = Join-Path $ProjectRoot "scripts\tests"
        if (Test-Path $ScriptsTestsPath) {
            $pesterMod = Get-Module -Name Pester -ListAvailable | Where-Object { $_.Version.Major -ge 5 } | Select-Object -First 1
            if ($pesterMod) {
                Write-Host "[TEST] Running scripts Pester tests..." -ForegroundColor Blue
                # Import the selected v5+ module explicitly so the v5 config APIs
                # don't bind to a different Pester version already in the session.
                Import-Module $pesterMod -Force
                $pesterConfig = New-PesterConfiguration
                $pesterConfig.Run.Path = $ScriptsTestsPath
                $pesterConfig.Run.Exit = $false
                $pesterConfig.Run.PassThru = $true
                $pesterConfig.Output.Verbosity = 'Normal'
                $pesterResult = Invoke-Pester -Configuration $pesterConfig
                if (($pesterResult.FailedCount + $pesterResult.FailedBlocksCount + $pesterResult.FailedContainersCount) -gt 0) {
                    if ($FailOnTestFailure) {
                        Write-Error "Stopping build due to scripts Pester test failures (FailOnTestFailure flag set): $($pesterResult.FailedCount) failed test(s), $($pesterResult.FailedBlocksCount) failed block(s), $($pesterResult.FailedContainersCount) failed container(s)"
                        exit 1
                    } else {
                        Write-Warning "Scripts Pester tests had $($pesterResult.FailedCount) failed test(s), $($pesterResult.FailedBlocksCount) failed block(s), $($pesterResult.FailedContainersCount) failed container(s) — continuing"
                    }
                } else {
                    Write-Host "[TEST] Scripts Pester tests passed: $($pesterResult.PassedCount) passed, $($pesterResult.SkippedCount) skipped" -ForegroundColor Green
                }
            } else {
                Write-Warning "Pester 5.x not installed — skipping scripts Pester tests. Install with: Install-Module Pester -Force -MinimumVersion 5.0"
            }
        }
    }

    # Step 9: Create MSIX packages (optional)
    if (-not $SkipMsix) {
        Write-Host ""
        Write-Host "[MSIX] Creating MSIX packages..." -ForegroundColor Blue
    
        # MSIX version is always 4-part numeric: major.minor.patch.buildNumber
        $MsixVersion = "$BaseVersion.$BuildNumber"
    
        # Pass branch tag so MSIX filename reflects the branch (e.g., winappcli-dev-my-feature_0.2.0.73_x64.msix)
        $MsixTag = if (-not $Stable -and $PrereleaseLabel -ne 'prerelease') { $PrereleaseLabel } else { $null }
    
        $PackageMsixScript = Join-Path $PSScriptRoot "package-msix.ps1"
        $CliBinariesPath = Join-Path (Join-Path $ProjectRoot $ArtifactsPath) "cli"

        $MsixArgs = @{
            CliBinariesPath = $CliBinariesPath
            Version = $MsixVersion
            Stable = $Stable
        }
        if ($MsixTag) { $MsixArgs['Tag'] = $MsixTag }
        & $PackageMsixScript @MsixArgs

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "MSIX packages creation failed, but continuing..."
        } else {
            Write-Host "[MSIX] MSIX packages created successfully!" -ForegroundColor Green
        }
    } else {
        Write-Host ""
        Write-Host "[MSIX] Skipping MSIX packages creation (use -SkipMsix:`$false to enable)" -ForegroundColor Gray
    }

    # Build process complete - all artifacts are ready

    # Copy install-dev script into artifacts so the folder is self-contained
    Write-Host ""
    Write-Host "[INSTALL] Copying setup-winapprun.ps1 to artifacts..." -ForegroundColor Blue
    $InstallDevScript = Join-Path $PSScriptRoot "setup-winapprun.ps1"
    if (Test-Path $InstallDevScript) {
        Copy-Item $InstallDevScript -Destination $ArtifactsPath -Force
        Write-Host "[INSTALL] setup-winapprun.ps1 copied to artifacts" -ForegroundColor Green
    } else {
        Write-Warning "setup-winapprun.ps1 not found at $InstallDevScript"
    }

    # Display results
    Write-Host ""
    Write-Host "[SUCCESS] Build completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan
    Write-Host "[INFO] Artifacts created in: $ArtifactsPath" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Contents:" -ForegroundColor White
    Get-ChildItem $ArtifactsPath | ForEach-Object {
        $size = if ($_.PSIsContainer) { "(folder)" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
        Write-Host "  * $($_.Name) $size" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "[DONE] Ready for distribution!" -ForegroundColor Green
}
finally
{
    # Restore original working directory
    Pop-Location
}
