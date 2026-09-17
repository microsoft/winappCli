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
        $script:originalLocation = Get-Location

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        # Phase 1 registers a loose-layout dev package (winapp run . --no-launch). Unregister it
        # before the backing files are deleted so we don't leave a dangling registration on the
        # machine / CI runner. Best-effort: read the identity Name from the manifest and
        # remove any matching registered package.
        try {
            $manifestPath = Join-Path $script:sampleDir 'Package.appxmanifest'
            if (Test-Path $manifestPath) {
                [xml]$manifestXml = Get-Content -Path $manifestPath -Raw
                $identityName = $manifestXml.Package.Identity.Name
                if ($identityName) {
                    Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue |
                        ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                }
            }
        } catch {
            Write-Warning "winui-app cleanup: failed to unregister dev package: $_"
        }

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Phase 1 exercises `winapp run` PROJECT MODE against a packaged WinUI app from a
    # clean directory: build + property resolution -> packaged detection -> loose-layout
    # registration (identity), plus a Native AOT launch and window-liveness check.
    Context 'Phase 1: Project-mode run (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'winui-packaged'

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
            $output = Invoke-WinappCommand -Arguments 'run . --no-launch'
            "$output" | Should -Match ([regex]::Escape("-p:PublishProfile=$($script:profileName)"))
            # Scoped to the profile argument rather than the whole console output: winapp prints its
            # version banner, which carries the branch name, so a bare 'release-' match fails on any
            # branch whose name happens to contain it.
            "$output" | Should -Not -Match ([regex]::Escape('PublishProfile=release-'))
            "$output" | Should -Match 'Registering packaged application'
            "$output" | Should -Match 'registered'
        }

        It 'Finds the inferred-profile output with --no-build' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'run . --no-build --no-launch'
        }

        It 'Publishes Native AOT and launches the staged recipe executable with a responsive window' -Skip:$script:skip {
            # Keep PublishAot local to the app: a global -p would also reach the netstandard library.
            $projectPath = Join-Path $script:tempDir 'winui-app.csproj'
            $project = [System.Xml.Linq.XDocument]::Load($projectPath)
            $project.Root.Add([System.Xml.Linq.XElement]::Parse(
                '<PropertyGroup><PublishAot>true</PublishAot></PropertyGroup>'))
            $project.Save($projectPath)

            # run has no --no-register option. Use a unique identity and always unregister it.
            $manifestPath = Join-Path $script:tempDir 'Package.appxmanifest'
            $manifest = [System.Xml.Linq.XDocument]::Load($manifestPath)
            $identityName = "winui-aot-test-$([guid]::NewGuid().ToString('N'))"
            $manifest.Root.Element($manifest.Root.Name.Namespace + 'Identity').SetAttributeValue('Name', $identityName)
            $manifest.Save($manifestPath)
            $layoutDir = Join-Path $script:tempDir 'AotLayout'
            $stagedExe = Join-Path $layoutDir 'winui-app.exe'
            $appProcess = $null

            try {
                $output = Invoke-WinappCommand -Arguments "run . --aot --arch $($script:platform) --detach --json --output-appx-directory AotLayout"
                $result = ($output -join "`n") | ConvertFrom-Json -ErrorAction Stop
                $result.ProcessId | Should -BeGreaterThan 0
                $candidate = Get-Process -Id $result.ProcessId -ErrorAction Stop
                try {
                    $candidate.Path | Should -Be $stagedExe
                    # Retain the process handle so cleanup cannot target a reused PID.
                    $null = $candidate.Handle
                    $appProcess = $candidate
                } finally {
                    if ($null -eq $appProcess) { $candidate.Dispose() }
                }

                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                do {
                    $appProcess.Refresh()
                    $appProcess.HasExited | Should -BeFalse -Because 'the Native AOT app must survive startup'
                    if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero -and $appProcess.Responding) { break }
                    Start-Sleep -Milliseconds 200
                } while ([DateTime]::UtcNow -lt $deadline)
                $appProcess.MainWindowHandle | Should -Not -Be ([IntPtr]::Zero) -Because 'a visible main window must appear within 30 seconds'
                $appProcess.Responding | Should -BeTrue -Because 'the main window must respond within 30 seconds'
                $appProcess.WaitForExit(5000) | Should -BeFalse -Because 'the Native AOT app must stay alive for five seconds after its window appears'
                $appProcess.Refresh()
                $appProcess.MainWindowHandle | Should -Not -Be ([IntPtr]::Zero)
                $appProcess.Responding | Should -BeTrue

                [xml]$stagedManifest = Get-Content (Join-Path $layoutDir 'appxmanifest.xml') -Raw
                $stagedManifest.Package.Identity.Name | Should -Be $identityName
                $stagedManifest.Package.Identity.ProcessorArchitecture | Should -Be $script:platform.ToLowerInvariant()
                $stagedManifest.Package.Applications.Application.EntryPoint | Should -Be 'Windows.FullTrustApplication'
                $executable = $stagedManifest.Package.Applications.Application.Executable
                $executable | Should -Be 'winui-app.exe'
                $stagedExe = Join-Path $layoutDir $executable
                $stagedExe | Should -Exist

                $recipes = @(Get-ChildItem (Join-Path $script:tempDir 'bin') -Recurse -Filter '*.build.appxrecipe')
                $recipes.Count | Should -Be 1
                $recipe = [System.Xml.Linq.XDocument]::Load($recipes[0].FullName)
                $entries = @($recipe.Descendants() | Where-Object {
                    $_.Name.LocalName -eq 'AppxPackagedFile' -and
                    $_.Element($_.Name.Namespace + 'PackagePath').Value -eq $executable
                })
                $entries.Count | Should -Be 1
                $nativeExe = [System.IO.Path]::GetFullPath($entries[0].Attribute('Include').Value, $recipes[0].DirectoryName)
                $nativeExe | Should -Exist
                $published = @(Get-ChildItem (Join-Path $script:tempDir 'bin') -Recurse -Filter $executable |
                    Where-Object { $_.Directory.Name -eq 'publish' })
                $published.Count | Should -Be 1
                (Get-FileHash $stagedExe).Hash | Should -Be (Get-FileHash $nativeExe).Hash
                (Get-FileHash $stagedExe).Hash | Should -Be (Get-FileHash $published[0].FullName).Hash

                $stream = [System.IO.File]::OpenRead($stagedExe)
                $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    $pe.PEHeaders.CorHeader | Should -BeNullOrEmpty -Because 'the staged executable must be native, not a managed assembly'
                    $expectedMachine = if ($script:rid -eq 'win-arm64') { 'Arm64' } else { 'Amd64' }
                    $pe.PEHeaders.CoffHeader.Machine.ToString() | Should -Be $expectedMachine
                } finally {
                    $pe.Dispose()
                    $stream.Dispose()
                }
            } finally {
                try {
                    # If launch succeeded but its JSON was invalid, find only this fixture's executable.
                    $processes = if ($null -ne $appProcess) { @($appProcess) } else {
                        @(Get-Process | Where-Object { $_.Path -eq $stagedExe })
                    }
                    foreach ($process in $processes) {
                        try {
                            if (-not $process.HasExited) {
                                $process.Kill()
                                $process.WaitForExit(10000) | Should -BeTrue -Because 'the test process must exit before unregistering its package'
                            }
                        } finally {
                            $process.Dispose()
                        }
                    }
                } finally {
                    Get-AppxPackage -Name $identityName -ErrorAction Stop |
                        ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop }
                }
            }
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
