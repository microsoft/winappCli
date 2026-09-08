#Requires -Modules Pester

<#
.SYNOPSIS
Pester tests for Microsoft.Windows.SDK.BuildTools.WinApp.

.DESCRIPTION
Two suites:

  1. Gate matrix - synthesizes throwaway .csproj files and queries
     $(_WinAppRunSupportActive) via `dotnet msbuild -getProperty:`. Locks in
     the gating logic that keeps the targets safe for transitive consumption
     so they only activate for packaged Windows apps.

  2. Package layout - opens the produced .nupkg and asserts every file in
     build\ has a matching entry in buildTransitive\ (and vice versa).
     WindowsAppSDK uses the same dual-pack pattern; we mirror it.

Run from repo root:

    Invoke-Pester -Path src\winapp-NuGet\tests\NuGet.Tests.ps1
#>

param(
    [string]$NupkgPath
)

BeforeDiscovery {
    $hasDotnet = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
    # These tests are Windows-specific (manifest gating, MSBuild platform identifiers).
    # On non-Windows hosts (e.g. Linux/macOS CI), skip rather than emit noisy failures.
    $isWindowsHost = if ($null -ne (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue)) { $IsWindows } else { $true }
    $script:skip = (-not $hasDotnet) -or (-not $isWindowsHost)
}

Describe "Microsoft.Windows.SDK.BuildTools.WinApp gating" -Skip:$script:skip {
    BeforeAll {
        $script:repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
        $script:propsPath = Join-Path $script:repoRoot "src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.props"
        $script:targetsPath = Join-Path $script:repoRoot "src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.targets"
        $script:tempRoot = Join-Path ([IO.Path]::GetTempPath()) "winapp-nuget-tests-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:tempRoot -Force | Out-Null

        function script:Get-GateValue {
            param(
                [string]$CaseName,
                [string]$TargetFramework,
                [string]$OutputType,
                [string]$ProjectDirManifestName = "",  # 'appxmanifest.xml' | 'Package.appxmanifest' | 'AppxManifest.xml'
                [bool]$ProjectDirManifest = $false,    # convenience: same as -ProjectDirManifestName 'appxmanifest.xml'
                [string]$OutputDirManifestName = "",   # places file at <OutputPath><name>; OutputPath forced to bin\
                [string]$WindowsPackageType = "",
                [string]$CustomManifestPath = "",
                [string]$EnableWinAppRunSupport = "",
                [string]$TargetPlatformIdentifier = ""
            )
            $dir = Join-Path $script:tempRoot $CaseName
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            if ($ProjectDirManifestName) {
                Set-Content -Path (Join-Path $dir $ProjectDirManifestName) -Value '<x/>'
            } elseif ($ProjectDirManifest) {
                Set-Content -Path (Join-Path $dir "appxmanifest.xml") -Value '<x/>'
            }
            if ($CustomManifestPath) {
                $customParent = Join-Path $dir (Split-Path $CustomManifestPath -Parent)
                if ($customParent -and -not (Test-Path $customParent)) {
                    New-Item -ItemType Directory -Path $customParent -Force | Out-Null
                }
                Set-Content -Path (Join-Path $dir $CustomManifestPath) -Value '<x/>'
            }
            if ($OutputDirManifestName) {
                $outDir = Join-Path $dir 'bin'
                New-Item -ItemType Directory -Path $outDir -Force | Out-Null
                Set-Content -Path (Join-Path $outDir $OutputDirManifestName) -Value '<x/>'
            }
            $extraProps = ""
            if ($WindowsPackageType) { $extraProps += "    <WindowsPackageType>$WindowsPackageType</WindowsPackageType>`n" }
            if ($CustomManifestPath) { $extraProps += "    <WinAppManifestPath>`$(MSBuildProjectDirectory)\$CustomManifestPath</WinAppManifestPath>`n" }
            if ($EnableWinAppRunSupport) { $extraProps += "    <EnableWinAppRunSupport>$EnableWinAppRunSupport</EnableWinAppRunSupport>`n" }
            if ($TargetPlatformIdentifier) { $extraProps += "    <TargetPlatformIdentifier>$TargetPlatformIdentifier</TargetPlatformIdentifier>`n" }
            if ($OutputDirManifestName) { $extraProps += "    <OutputPath>bin\</OutputPath>`n" }
            $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$TargetFramework</TargetFramework>
    <OutputType>$OutputType</OutputType>
$extraProps  </PropertyGroup>
  <Import Project="$($script:propsPath)" />
  <Import Project="$($script:targetsPath)" />
</Project>
"@
            Set-Content -Path (Join-Path $dir "test.csproj") -Value $csproj
            $out = & dotnet msbuild (Join-Path $dir "test.csproj") -getProperty:_WinAppRunSupportActive -nologo 2>&1
            ($out | Select-Object -Last 1).ToString().Trim()
        }

        # Builds a project that activates run support, runs _WinAppBuildRunArgs, and returns the
        # constructed winapp command line. Targets _WinAppRunArgs rather than the final
        # RunArguments so the assertion does not depend on a restore/build of the fake project.
        function script:Get-ComputedRunArgs {
            param(
                [string]$CaseName,
                [string]$WinAppLaunchArgs = "",
                [string]$WinAppRunArgs = "",
                [switch]$WinAppRunDetach,
                [switch]$WinAppRunUnregisterOnExit,
                [switch]$WinAppRunClean,
                [switch]$WinAppRunSymbols,
                [string]$WinAppRunExecutable = "",
                [switch]$UseWinUI,
                [switch]$UseWPF,
                [switch]$UseWindowsForms,
                [switch]$UseMaui
            )
            $dir = Join-Path $script:tempRoot $CaseName
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -Path (Join-Path $dir "appxmanifest.xml") -Value '<x/>'

            # _WinAppValidateRunSupport hard-errors when WinAppCliPath does not exist. These tests
            # run against the source build\ folder rather than an installed package, so there is no
            # ..\tools\win-x64\winapp.exe; point the property at a stub instead. Nothing is executed
            # here -- only the argument string is evaluated.
            $fakeCli = Join-Path $dir "winapp.exe"
            Set-Content -Path $fakeCli -Value 'stub'

            $extraProps = "    <WinAppCliPath>$fakeCli</WinAppCliPath>`n"
            if ($WinAppLaunchArgs) { $extraProps += "    <WinAppLaunchArgs>$WinAppLaunchArgs</WinAppLaunchArgs>`n" }
            if ($WinAppRunArgs) { $extraProps += "    <WinAppRunArgs>$WinAppRunArgs</WinAppRunArgs>`n" }
            if ($WinAppRunDetach) { $extraProps += "    <WinAppRunDetach>true</WinAppRunDetach>`n" }
            if ($WinAppRunUnregisterOnExit) { $extraProps += "    <WinAppRunUnregisterOnExit>true</WinAppRunUnregisterOnExit>`n" }
            if ($WinAppRunClean) { $extraProps += "    <WinAppRunClean>true</WinAppRunClean>`n" }
            if ($WinAppRunSymbols) { $extraProps += "    <WinAppRunSymbols>true</WinAppRunSymbols>`n" }
            if ($WinAppRunExecutable) { $extraProps += "    <WinAppRunExecutable>$WinAppRunExecutable</WinAppRunExecutable>`n" }
            if ($UseWinUI) { $extraProps += "    <UseWinUI>true</UseWinUI>`n" }
            if ($UseWPF) { $extraProps += "    <UseWPF>true</UseWPF>`n" }
            if ($UseWindowsForms) { $extraProps += "    <UseWindowsForms>true</UseWindowsForms>`n" }
            if ($UseMaui) { $extraProps += "    <UseMaui>true</UseMaui>`n" }

            $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <OutputType>WinExe</OutputType>
$extraProps  </PropertyGroup>
  <Import Project="$($script:propsPath)" />
  <Import Project="$($script:targetsPath)" />
</Project>
"@
            Set-Content -Path (Join-Path $dir "test.csproj") -Value $csproj
            $out = & dotnet msbuild (Join-Path $dir "test.csproj") -t:_WinAppBuildRunArgs -getProperty:_WinAppRunArgs -nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to compute _WinAppRunArgs:`n$($out -join [Environment]::NewLine)"
            }
            ($out | Select-Object -Last 1).ToString().Trim()
        }
    }

    AfterAll {
        if ($script:tempRoot -and (Test-Path $script:tempRoot)) {
            Remove-Item -Recurse -Force $script:tempRoot -ErrorAction SilentlyContinue
        }
    }

    Context "Active scenarios - packaged Windows apps" {
        It "Activates for WinUI app (OutputType=WinExe, manifest in project dir)" {
            Get-GateValue -CaseName 'winui' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates for packaged console app (OutputType=Exe + manifest) - protects the dotnet-app sample" {
            Get-GateValue -CaseName 'console-pkg' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates with project-dir Package.appxmanifest (VS convention)" {
            Get-GateValue -CaseName 'package-appxmanifest' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifestName 'Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates with project-dir AppxManifest.xml (MSBuild output convention)" {
            Get-GateValue -CaseName 'appxmanifest-xml-cap' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifestName 'AppxManifest.xml' | Should -Be 'true'
        }

        It "Activates with explicit TargetPlatformIdentifier=windows on a non-windows TFM (non-SDK-style projects)" {
            Get-GateValue -CaseName 'explicit-tpi-windows' -TargetFramework 'net8.0' -OutputType 'Exe' -ProjectDirManifest $true -TargetPlatformIdentifier 'windows' | Should -Be 'true'
        }

        It "Activates with custom WinAppManifestPath pointing to a manifest in a sub-directory" {
            Get-GateValue -CaseName 'custom-mfst-path' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -CustomManifestPath 'platforms\windows\Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates for the Windows TFM of a multi-targeted MAUI-style project" {
            Get-GateValue -CaseName 'maui-windows' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates for MAUI-style head app whose manifest is generated into `$(OutputPath) (Package.appxmanifest)" {
            # MAUI generates the AppxManifest at build time based on platform / msbuild props,
            # so the only manifest that exists lives under bin\ — not in the project directory.
            # The auto-detection in WinAppManifestPath checks $(OutputPath) first; the gate must
            # accept that resolved path, otherwise transitive consumption from MAUI Windows libs
            # never activates and `dotnet run` falls back to the SDK default.
            Get-GateValue -CaseName 'maui-genmfst-pkg' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'WinExe' -OutputDirManifestName 'Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates when the manifest is generated into `$(OutputPath) as AppxManifest.xml" {
            Get-GateValue -CaseName 'output-appxmanifest-xml' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -OutputDirManifestName 'AppxManifest.xml' | Should -Be 'true'
        }

        It "Activates when the manifest is generated into `$(OutputPath) as appxmanifest.xml" {
            Get-GateValue -CaseName 'output-appxmanifest-lower' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -OutputDirManifestName 'appxmanifest.xml' | Should -Be 'true'
        }
    }

    Context "Inactive scenarios - must not fire in transitive consumers" {
        It "Inactive for class libraries (OutputType=Library)" {
            Get-GateValue -CaseName 'lib' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Library' | Should -Be 'false'
        }

        It "Inactive for console apps without a manifest" {
            Get-GateValue -CaseName 'console-no-mfst' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive when WindowsPackageType=MSIX is set but no manifest exists (downstream targets need a real manifest)" {
            # Regression guard: previously the gate accepted WindowsPackageType=MSIX as a
            # standalone activation signal, which let downstream targets proceed without a
            # discoverable manifest and produce hard-to-diagnose failures.
            Get-GateValue -CaseName 'msix-no-mfst' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -WindowsPackageType 'MSIX' | Should -Be 'false'
        }

        It "Inactive when WindowsPackageType=None (explicit opt-out, even with manifest)" {
            Get-GateValue -CaseName 'unpkg' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true -WindowsPackageType 'None' | Should -Be 'false'
        }

        It "Inactive when EnableWinAppRunSupport=false (explicit opt-out)" {
            Get-GateValue -CaseName 'opt-out' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true -EnableWinAppRunSupport 'false' | Should -Be 'false'
        }

        It "Inactive for the Android TFM of a MAUI-style project" {
            Get-GateValue -CaseName 'maui-android' -TargetFramework 'net8.0-android' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive for the iOS TFM of a MAUI-style project" {
            Get-GateValue -CaseName 'maui-ios' -TargetFramework 'net8.0-ios' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive when explicit TargetPlatformIdentifier=android overrides a windows-style TFM" {
            # If a non-SDK-style project explicitly sets TargetPlatformIdentifier, the
            # explicit value must win over what would be derived from the TFM string.
            Get-GateValue -CaseName 'explicit-tpi-android' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true -TargetPlatformIdentifier 'android' | Should -Be 'false'
        }

        It "Inactive for plain net8.0 (no Windows platform)" {
            Get-GateValue -CaseName 'plain-net8' -TargetFramework 'net8.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'false'
        }
    }

    Context "Run option properties" {
        It "Emits no optional switches when every property is left at its default" {
            $args = Get-ComputedRunArgs -CaseName 'run-defaults'

            $args | Should -Match ' --project-framework other-dotnet '
            $args | Should -Match ' --caller nuget-package$'
            $args | Should -Not -Match ' --detach'
            $args | Should -Not -Match ' --unregister-on-exit'
            $args | Should -Not -Match ' --clean'
            $args | Should -Not -Match ' --symbols'
            $args | Should -Not -Match ' --executable'
        }

        It "Maps WinAppLaunchArgs to --args" {
            $args = Get-ComputedRunArgs -CaseName 'run-launch-args' -WinAppLaunchArgs '--from-property value'

            $args | Should -Match ' --args "--from-property value"'
        }

        It "Passes the evaluated WinUI framework category" {
            Get-ComputedRunArgs -CaseName 'run-winui' -UseWinUI |
                Should -Match ' --project-framework winui '
        }

        It "Passes the evaluated WPF framework category" {
            Get-ComputedRunArgs -CaseName 'run-wpf' -UseWPF |
                Should -Match ' --project-framework wpf '
        }

        It "Passes the evaluated WinForms framework category" {
            Get-ComputedRunArgs -CaseName 'run-winforms' -UseWindowsForms |
                Should -Match ' --project-framework winforms '
        }

        It "Prefers MAUI when multiple framework properties are present" {
            Get-ComputedRunArgs -CaseName 'run-maui' -UseMaui -UseWinUI |
                Should -Match ' --project-framework maui '
        }

        It "Maps each boolean run property to its CLI switch" {
            $args = Get-ComputedRunArgs -CaseName 'run-bools' `
                -WinAppRunDetach -WinAppRunUnregisterOnExit -WinAppRunClean -WinAppRunSymbols

            $args | Should -Match ' --detach '
            $args | Should -Match ' --unregister-on-exit '
            $args | Should -Match ' --clean '
            $args | Should -Match ' --symbols '
        }

        It "Quotes WinAppRunExecutable so a path with spaces survives" {
            $args = Get-ComputedRunArgs -CaseName 'run-exe' -WinAppRunExecutable 'tools\My App.exe'

            $args | Should -Match ' --executable "tools\\My App\.exe"'
        }

        It "Appends WinAppRunArgs after the property-derived switches" {
            # WinAppRunArgs is the escape hatch for options with no dedicated property, so it must
            # land last -- the same position AdditionalOptions occupies in other toolsets.
            $args = Get-ComputedRunArgs -CaseName 'run-raw-args' -WinAppRunDetach -WinAppRunArgs '--verbose'

            $args | Should -Match ' --detach .*--caller nuget-package --verbose$'
        }

        It "Omits WinAppRunArgs entirely when it is empty" {
            $args = Get-ComputedRunArgs -CaseName 'run-raw-empty'

            $args | Should -Match ' --caller nuget-package$'
        }
    }

    Context "dotnet run argument routing" {
        BeforeAll {
            # RunArguments is what `dotnet run` actually launches, so this Context reads that rather
            # than the intermediate _WinAppRunArgs the other tests assert on. The distinction matters:
            # the trailing separator is added only on the dotnet run path, not to the shared argument
            # list that RunPackagedApp also uses.
            function script:Get-ComputedRunArguments {
                param([string]$CaseName)
                $dir = Join-Path $script:tempRoot $CaseName
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Set-Content -Path (Join-Path $dir "appxmanifest.xml") -Value '<x/>'
                $fakeCli = Join-Path $dir "winapp.exe"
                Set-Content -Path $fakeCli -Value 'stub'

                $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <OutputType>WinExe</OutputType>
    <WinAppCliPath>$fakeCli</WinAppCliPath>
  </PropertyGroup>
  <Import Project="$($script:propsPath)" />
  <Import Project="$($script:targetsPath)" />
  <!--
    Override _WinAppPrepareRunArguments' copy dependency with a no-op. It is declared after the
    import, so it wins. Only the computed RunArguments string is under test here; leaving the real
    copy in place would drag in the SDK build targets and require a restored, fully built project
    just to read one property.
  -->
  <Target Name="_WinAppCopyContentToLooseLayout" />
</Project>
"@
                Set-Content -Path (Join-Path $dir "test.csproj") -Value $csproj
                $out = & dotnet msbuild (Join-Path $dir "test.csproj") -t:_WinAppPrepareRunArguments -getProperty:RunArguments -nologo 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to compute RunArguments:`n$($out -join [Environment]::NewLine)"
                }
                ($out | Select-Object -Last 1).ToString().Trim()
            }
        }

        It "Ends RunArguments with a separator so dotnet run arguments reach the application" {
            # The .NET SDK appends the user's application arguments to RunArguments verbatim and drops
            # any standalone separator they typed, so `dotnet run X` and `dotnet run -- X` arrive
            # identically. Ending RunArguments with a separator puts everything appended after it in
            # winapp's passthrough region, which is what makes `dotnet run` behave the same way for a
            # project that references this package as for one that does not.
            $runArguments = Get-ComputedRunArguments -CaseName 'run-routing'

            $runArguments | Should -Match ' --caller nuget-package --$'
        }

        It "Keeps the shared argument list free of the separator (RunPackagedApp is unaffected)" {
            # RunPackagedApp invokes the CLI directly and appends nothing, so the separator belongs
            # only on the dotnet run path. A stray trailing separator there would be harmless but
            # misleading, and it would show up in the logged command line.
            $sharedArgs = Get-ComputedRunArgs -CaseName 'run-shared-no-sep'

            $sharedArgs | Should -Match ' --caller nuget-package$'
            $sharedArgs | Should -Not -Match ' --$'
        }
    }
}

Describe "Microsoft.Windows.SDK.BuildTools.WinApp package layout" -Skip:$script:skip {
    BeforeAll {
        $script:repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
        if (-not $NupkgPath) {
            $artifactsDir = Join-Path $script:repoRoot "artifacts\nuget"
            if (Test-Path $artifactsDir) {
                # The UI Automation package ids are prefixed by this one, so the glob alone also
                # matches them. Require a digit straight after the id so only the tools package
                # (id followed by its version) is picked up.
                $NupkgPath = Get-ChildItem -Path $artifactsDir -Filter "Microsoft.Windows.SDK.BuildTools.WinApp.*.nupkg" -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '^Microsoft\.Windows\.SDK\.BuildTools\.WinApp\.\d' } |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
        }
        $script:nupkg = $NupkgPath
    }

    It "Has been built (artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.*.nupkg exists)" {
        $script:nupkg | Should -Not -BeNullOrEmpty -Because "Run scripts\build-cli.ps1 to produce the package, or pass -NupkgPath."
        Test-Path $script:nupkg | Should -BeTrue
    }

    It "Mirrors build\ to buildTransitive\ exactly (parity required for transitive flow)" {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($script:nupkg)
        try {
            $build = $z.Entries | Where-Object { $_.FullName -match '^build/' -and -not $_.FullName.EndsWith('/') } |
                ForEach-Object { $_.FullName.Substring('build/'.Length) } | Sort-Object
            $buildTransitive = $z.Entries | Where-Object { $_.FullName -match '^buildTransitive/' -and -not $_.FullName.EndsWith('/') } |
                ForEach-Object { $_.FullName.Substring('buildTransitive/'.Length) } | Sort-Object
        } finally {
            $z.Dispose()
        }
        $build.Count | Should -BeGreaterThan 0
        $buildTransitive.Count | Should -BeGreaterThan 0
        Compare-Object $build $buildTransitive | Should -BeNullOrEmpty -Because "Files in build\ and buildTransitive\ must match exactly so direct and transitive consumers see the same MSBuild logic."
    }
}

Describe "UI Automation library packages" -Skip:$script:skip {
    BeforeAll {
        $script:repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
        $script:nugetDir = Join-Path $script:repoRoot "artifacts\nuget"
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        function Get-LibraryPackage([string]$Id) {
            if (-not (Test-Path $script:nugetDir)) { return $null }
            # Ids share a prefix, so require the version digit straight after the id.
            Get-ChildItem -Path $script:nugetDir -Filter "$Id.*.nupkg" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match ('^' + [regex]::Escape($Id) + '\.\d') } |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        }

        function Read-Nuspec([string]$NupkgPath) {
            $zip = [IO.Compression.ZipFile]::OpenRead($NupkgPath)
            try {
                $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
                $reader = New-Object IO.StreamReader($entry.Open())
                try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
            } finally { $zip.Dispose() }
        }

        function Get-NupkgEntries([string]$NupkgPath) {
            $zip = [IO.Compression.ZipFile]::OpenRead($NupkgPath)
            try { return @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
        }

        $script:baseId = "Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation"
        $script:recordingId = "Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording"
        $script:basePkg = Get-LibraryPackage $script:baseId
        $script:recordingPkg = Get-LibraryPackage $script:recordingId
    }

    Context "Package contents" {
        It "Both library packages have been built" {
            $script:basePkg | Should -Not -BeNullOrEmpty -Because "Run scripts\build-cli.ps1 (or scripts\package-nuget.ps1 -SkipCliPackage) first."
            $script:recordingPkg | Should -Not -BeNullOrEmpty
        }

        It "Ships an assembly for both target frameworks the base package advertises" {
            $entries = Get-NupkgEntries $script:basePkg.FullName
            $libs = @($entries | Where-Object { $_ -like 'lib/*/*.dll' } | ForEach-Object { ($_ -split '/')[1] } | Sort-Object -Unique)
            # PACKAGE.md tells consumers they can target either; a missing folder silently downgrades
            # them to the other one's capabilities instead of failing.
            $libs | Should -Contain 'net10.0-windows7.0' -Because "the lean net10.0-windows target must ship."
            $libs | Should -Contain 'net10.0-windows10.0.19041' -Because "the Graphics Capture target must ship."
        }

        It "Keeps SkiaSharp out of the base package (the reason recording ships separately)" {
            $nuspec = Read-Nuspec $script:basePkg.FullName
            $ids = @($nuspec.package.metadata.dependencies.group.dependency | ForEach-Object { $_.id })
            $ids | Should -Not -Contain 'SkiaSharp'
            @($nuspec.package.metadata.dependencies.group).Count | Should -Be 2 -Because "each advertised target framework needs its own dependency group."
        }

        It "Declares the recording package's dependency on the base package at exactly the same version" {
            $nuspec = Read-Nuspec $script:recordingPkg.FullName
            $deps = @($nuspec.package.metadata.dependencies.group.dependency)
            $baseDep = $deps | Where-Object { $_.id -eq $script:baseId }
            $baseDep | Should -Not -BeNullOrEmpty
            # Bracketed, not bare: a bare version means "[x,)", which would let a consumer pair this
            # package with a newer base package it was never built against.
            $baseDep.version | Should -BeExactly "[$($nuspec.package.metadata.version)]"
            @($deps | ForEach-Object { $_.id }) | Should -Contain 'SkiaSharp'
        }

        It "Embeds the readme that nuget.org renders" {
            foreach ($pkg in @($script:basePkg, $script:recordingPkg)) {
                $nuspec = Read-Nuspec $pkg.FullName
                $nuspec.package.metadata.readme | Should -Not -BeNullOrEmpty
                Get-NupkgEntries $pkg.FullName | Should -Contain $nuspec.package.metadata.readme
            }
        }
    }

    Context "Consuming the packages as a real project" {
        BeforeAll {
            $script:consumerRoot = Join-Path ([IO.Path]::GetTempPath()) "winapp-uia-consumer-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $script:consumerRoot -Force | Out-Null

            # Add the freshly built packages as a source. No <clear/>: the host's existing sources
            # still supply Microsoft.Extensions.* and SkiaSharp.
            @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="winapp-local" value="$($script:nugetDir)" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $script:consumerRoot "NuGet.config") -Encoding UTF8

            $script:packageVersion = (Read-Nuspec $script:basePkg.FullName).package.metadata.version

            # Builds a consumer that resolves exactly the services PACKAGE.md documents, so broken
            # dependency metadata or target-framework asset selection fails here rather than for a
            # consumer after publish. Returns the dotnet output plus its exit code.
            function Invoke-Consumer([string]$Name, [string]$TargetFramework, [bool]$WithRecording) {
                $dir = Join-Path $script:consumerRoot $Name
                New-Item -ItemType Directory -Path $dir -Force | Out-Null

                $recordingRef = if ($WithRecording) {
                    "<PackageReference Include=`"$($script:recordingId)`" Version=`"$($script:packageVersion)`" />"
                } else { "" }

                @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$TargetFramework</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>false</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$($script:baseId)" Version="$($script:packageVersion)" />
    $recordingRef
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $dir "$Name.csproj") -Encoding UTF8

                $recordingUsing = if ($WithRecording) { "using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;" } else { "" }
                $recordingRegister = if ($WithRecording) { ".AddWinAppUiRecording()" } else { "" }
                $recordingResolve = if ($WithRecording) { "_ = services.GetRequiredService<IUiRecordingService>();" } else { "" }

                @"
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
$recordingUsing

var services = new ServiceCollection()
    .AddLogging()
    .AddWinAppUiAutomation()
    $recordingRegister
    .BuildServiceProvider();

_ = services.GetRequiredService<IUiAutomation>();
_ = services.GetRequiredService<IUiTargetResolver>();
_ = services.GetRequiredService<IWindowCapture>();
$recordingResolve

Console.WriteLine("CONSUMER_OK");
"@ | Set-Content (Join-Path $dir "Program.cs") -Encoding UTF8

                Push-Location $dir
                try {
                    $output = & dotnet run --nologo 2>&1 | Out-String
                    return @{ Output = $output; ExitCode = $LASTEXITCODE }
                } finally { Pop-Location }
            }
        }

        AfterAll {
            if ($script:consumerRoot -and (Test-Path $script:consumerRoot)) {
                Remove-Item $script:consumerRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Resolves the documented services on <_>" -ForEach @('net10.0-windows', 'net10.0-windows10.0.19041.0') {
            $name = "consumer" + ($_ -replace '[^A-Za-z0-9]', '')
            $result = Invoke-Consumer -Name $name -TargetFramework $_ -WithRecording:$false
            if ($result.Output -match 'Unable to load the service index|NU1301|No such host is known|SSL connection') {
                Set-ItResult -Skipped -Because "restoring the consumer could not reach a NuGet feed for the framework dependencies."
                return
            }
            $result.Output | Should -Match 'CONSUMER_OK' -Because "a consumer must be able to install the package and resolve its documented services.`n$($result.Output)"
            $result.ExitCode | Should -Be 0
        }

        It "Resolves recording services when the recording package is added" {
            $result = Invoke-Consumer -Name "consumerrecording" -TargetFramework 'net10.0-windows10.0.19041.0' -WithRecording:$true
            if ($result.Output -match 'Unable to load the service index|NU1301|No such host is known|SSL connection') {
                Set-ItResult -Skipped -Because "restoring the consumer could not reach a NuGet feed for the framework dependencies."
                return
            }
            $result.Output | Should -Match 'CONSUMER_OK' -Because "the recording package must compose with the base package.`n$($result.Output)"
            $result.ExitCode | Should -Be 0
        }
    }
}
