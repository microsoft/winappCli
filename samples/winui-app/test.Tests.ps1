param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe 'winui-app sample' {

    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:cliDir = $null
        $script:phaseOneLayout = $null
        $script:sampleBuildStarted = $false
        $script:originalLocation = Get-Location

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            $script:cliDir = New-TempTestDirectory -Prefix 'winui-cli'
            Push-Location $script:cliDir
            try {
                # Anchor npm to this fixture instead of an ancestor's package.json/node_modules.
                npm init --yes | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "Failed to initialize the local winapp test package" }
                Install-WinappNpmPackage -PackagePath $resolvedPkg
            } finally {
                Pop-Location
            }
            $script:winappCli = Join-Path $script:cliDir 'node_modules\.bin\winapp.cmd'
            if (-not (Test-Path $script:winappCli)) {
                throw "The configured winapp package did not install a CLI shim at $script:winappCli"
            }
        }

        function Invoke-SampleWinapp {
            param([string[]]$Arguments)
            $output = & $script:winappCli @Arguments
            if ($LASTEXITCODE -ne 0) {
                throw "Configured winapp failed ($LASTEXITCODE): $($Arguments -join ' ')"
            }
            return $output
        }

        function Get-ComparableFixturePath {
            param([string]$Path)
            if ($Path.StartsWith('\\?\', [System.StringComparison]::Ordinal)) {
                $Path = $Path.Substring(4)
            }
            return [System.IO.Path]::GetFullPath($Path)
        }

        function Copy-SampleSources {
            param([string]$Destination)
            Get-ChildItem -Path $script:sampleDir -Recurse -File |
                Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj|AppX|node_modules|\.git)\\' } |
                ForEach-Object {
                    $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                    $target = Join-Path $Destination $relative
                    $targetDir = Split-Path $target -Parent
                    if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                    Copy-Item -Path $_.FullName -Destination $target
                }
        }
    }

    AfterAll {
        Set-Location $script:originalLocation
        $phaseOneCleaned = $true
        if ($script:phaseOneLayout -and (Test-Path $script:phaseOneLayout)) {
            try {
                Invoke-SampleWinapp -Arguments @('unregister', '--output-appx-directory', $script:phaseOneLayout) | Out-Host
            } catch {
                $phaseOneCleaned = $false
                Write-Warning "winui-app cleanup: keeping $($script:tempDir) because exact-layout unregister failed: $_"
            }
        }

        if (-not $SkipCleanup) {
            if ($script:tempDir -and $phaseOneCleaned) { Remove-TempTestDirectory -Path $script:tempDir }
            if ($script:cliDir) { Remove-TempTestDirectory -Path $script:cliDir }
            if ($script:sampleBuildStarted) {
                Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Phase 1 exercises `winapp run` PROJECT MODE against a packaged WinUI app from a
    # clean directory: build + property resolution -> packaged detection -> loose-layout
    # registration (identity) WITHOUT launching (deterministic, no GUI required).
    Context 'Phase 1: Project-mode run (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'winui-packaged'
                $script:phaseOneLayout = Join-Path $script:tempDir 'TestAppX'

                # Copy the sample sources (never bin/obj) into a clean temp project dir.
                Copy-SampleSources -Destination $script:tempDir

                $script:rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
                $script:platform = if ($script:rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
                $script:profileName = "debug-$($script:platform).pubxml"

                # Exercise the real .NET SDK boundary behind architecture-profile inference. Keep this
                # test-only configuration out of the sample itself so its normal workflow stays lightweight.
                $projectPath = Join-Path $script:tempDir 'winui-app.csproj'
                $project = [System.Xml.Linq.XDocument]::Load($projectPath)
                @($project.Descendants() | Where-Object { $_.Name.LocalName -eq 'PublishProfile' }) |
                    ForEach-Object { $_.Remove() }
                $itemGroup = [System.Xml.Linq.XElement]::Parse(
                    '<ItemGroup><ProjectReference Include="AnyCpuLibrary\AnyCpuLibrary.csproj" /></ItemGroup>')
                $project.Root.Add($itemGroup)
                $project.Save($projectPath)

                @"
<Project>
  <PropertyGroup Condition="'`$(MSBuildProjectName)' == 'winui-app'">
    <PublishTrimmed>true</PublishTrimmed>
    <PublishProfile Condition="'`$(Configuration)' == 'Debug'">debug-`$(Platform).pubxml</PublishProfile>
    <PublishProfile Condition="'`$(Configuration)' == 'Release'">release-`$(Platform).pubxml</PublishProfile>
    <DefaultItemExcludes>`$(DefaultItemExcludes);AnyCpuLibrary\**\*</DefaultItemExcludes>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path (Join-Path $script:tempDir 'Directory.Build.props')

                $libraryDir = Join-Path $script:tempDir 'AnyCpuLibrary'
                New-Item -ItemType Directory -Path $libraryDir -Force | Out-Null
                @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <Target Name="RejectAppPublishProfile" BeforeTargets="Build" Condition="'$(PublishProfileImported)' == 'true'">
    <Error Text="The app publish profile leaked into the referenced library." />
  </Target>
</Project>
'@ | Set-Content -Path (Join-Path $libraryDir 'AnyCpuLibrary.csproj')

                $profileDir = Join-Path $script:tempDir 'Properties\PublishProfiles'
                New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
                @"
<Project>
  <PropertyGroup>
    <Platform>$($script:platform)</Platform>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <RuntimeIdentifier>$($script:rid)</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>false</PublishSingleFile>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path (Join-Path $profileDir $script:profileName)

                # A same-named library profile would be imported by a plain global PublishProfile property.
                # winapp scopes the inferred profile to the root app, so this file must remain inactive.
                $libraryProfileDir = Join-Path $libraryDir 'Properties\PublishProfiles'
                New-Item -ItemType Directory -Path $libraryProfileDir -Force | Out-Null
                Copy-Item -Path (Join-Path $profileDir $script:profileName) -Destination $libraryProfileDir

                Push-Location $script:tempDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It 'Proves the trimmed RID-only build requires a self-contained profile' -Skip:$script:skip {
            $output = dotnet build 'winui-app.csproj' -c Debug -r $script:rid 2>&1
            $LASTEXITCODE | Should -Not -Be 0
            "$output" | Should -Match 'NETSDK1102'
        }

        It 'Selects the effective profile, builds, and registers the packaged app' -Skip:$script:skip {
            # --no-launch builds the loose layout and registers a debug identity
            # without launching the app (no GUI, deterministic in CI).
            $output = Invoke-SampleWinapp -Arguments @('run', '.', '--no-launch', '--output-appx-directory', $script:phaseOneLayout)
            "$output" | Should -Match ([regex]::Escape("-p:PublishProfile=$($script:profileName)"))
            # Scoped to the profile argument rather than the whole console output: winapp prints its
            # version banner, which carries the branch name, so a bare 'release-' match fails on any
            # branch whose name happens to contain it.
            "$output" | Should -Not -Match ([regex]::Escape('PublishProfile=release-'))
            "$output" | Should -Match 'Registering packaged application'
            "$output" | Should -Match 'registered'
        }

        It 'Finds the inferred-profile output with --no-build' -Skip:$script:skip {
            Invoke-SampleWinapp -Arguments @('run', '.', '--no-build', '--no-launch', '--output-appx-directory', $script:phaseOneLayout)
        }
    }

    Context 'Phase 2: Sample Build Check' {

        BeforeAll {
            if (-not $script:skip) {
                $script:sampleBuildStarted = $true
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

    Context 'Phase 3: Unique identity for parallel checkouts' -Tag 'UniqueIdentity' {

        It 'Keeps two copies registered independently without changing source manifests' -Skip:$script:skip {
            $root = New-TempTestDirectory -Prefix 'winui-unique'
            $activeLayouts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $deployments = [System.Collections.Generic.List[object]]::new()
            $originalManifestHash = (Get-FileHash -LiteralPath (Join-Path $script:sampleDir 'Package.appxmanifest')).Hash
            $cleanupFailed = $false

            try {
                foreach ($name in @('worktree-a', 'worktree-b')) {
                    $copy = Join-Path $root $name
                    Copy-SampleSources -Destination $copy
                    $project = Join-Path $copy 'winui-app.csproj'
                    $layout = Join-Path $root "layouts\$name"
                    [void]$activeLayouts.Add($layout)

                    $output = Invoke-SampleWinapp -Arguments @(
                        'run', $project, '--unique-identity', '--no-launch', '--json',
                        '--output-appx-directory', $layout
                    )
                    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json
                    $identity = $result.Identity
                    $identity.Mode | Should -Be 'Unique'
                    Get-ComparableFixturePath $identity.OwnerPath | Should -Be (Get-ComparableFixturePath $project)
                    Get-ComparableFixturePath $identity.LayoutPath | Should -Be (Get-ComparableFixturePath $layout)
                    $identity.OriginalPackageName | Should -Be 'winui-app-sample'
                    $identity.EffectivePackageName | Should -Not -Be $identity.OriginalPackageName
                    $identity.PackageFamilyName | Should -Not -BeNullOrEmpty
                    $identity.PackageFullName | Should -Not -BeNullOrEmpty
                    $deployments.Add([pscustomobject]@{
                        Project = $project
                        Folder = $copy
                        Layout = $layout
                        Identity = $identity
                    })
                }

                $deployments[0].Identity.PackageFamilyName | Should -Not -Be $deployments[1].Identity.PackageFamilyName
                $deployments[0].Identity.EffectivePackageName | Should -Not -Be $deployments[1].Identity.EffectivePackageName

                foreach ($deployment in $deployments) {
                    $output = Invoke-SampleWinapp -Arguments @(
                        'run', $deployment.Project, '--no-build', '--unique-identity', '--no-launch', '--json',
                        '--output-appx-directory', $deployment.Layout
                    )
                    $repeat = ($output -join [Environment]::NewLine) | ConvertFrom-Json
                    $repeat.Identity.PackageFamilyName | Should -Be $deployment.Identity.PackageFamilyName
                    $repeat.Identity.EffectivePackageName | Should -Be $deployment.Identity.EffectivePackageName
                    $repeat.Identity.OwnerPath | Should -Be $deployment.Identity.OwnerPath

                    $registered = @(Get-AppxPackage -Name $repeat.Identity.EffectivePackageName |
                        Where-Object PackageFamilyName -EQ $repeat.Identity.PackageFamilyName)
                    $registered.Count | Should -Be 1
                    $registered[0].PackageFullName | Should -Be $repeat.Identity.PackageFullName
                    Get-ComparableFixturePath $registered[0].InstallLocation | Should -Be (Get-ComparableFixturePath $deployment.Layout)
                    $registered[0].IsDevelopmentMode | Should -BeTrue
                    (Get-FileHash -LiteralPath (Join-Path $deployment.Folder 'Package.appxmanifest')).Hash |
                        Should -Be $originalManifestHash
                }

                # No mode flag or manifest is needed: each source discovers its effective registration.
                Invoke-SampleWinapp -Arguments @('unregister', $deployments[0].Project) | Out-Host
                @(Get-AppxPackage -Name $deployments[0].Identity.EffectivePackageName).Count | Should -Be 0
                [void]$activeLayouts.Remove($deployments[0].Layout)
                @(Get-AppxPackage -Name $deployments[1].Identity.EffectivePackageName).Count | Should -Be 1

                Invoke-SampleWinapp -Arguments @('unregister', $deployments[1].Folder) | Out-Host
                @(Get-AppxPackage -Name $deployments[1].Identity.EffectivePackageName).Count | Should -Be 0
                [void]$activeLayouts.Remove($deployments[1].Layout)
                (Get-FileHash -LiteralPath (Join-Path $script:sampleDir 'Package.appxmanifest')).Hash |
                    Should -Be $originalManifestHash
            } finally {
                foreach ($layout in $activeLayouts) {
                    if (-not (Test-Path $layout)) { continue }
                    try {
                        Invoke-SampleWinapp -Arguments @('unregister', '--output-appx-directory', $layout) | Out-Host
                    } catch {
                        $cleanupFailed = $true
                        Write-Warning "winui-app cleanup: keeping $root because exact-layout unregister failed for ${layout}: $_"
                    }
                }
                if (-not $SkipCleanup -and -not $cleanupFailed) {
                    Remove-TempTestDirectory -Path $root
                }
            }
        }
    }
}
