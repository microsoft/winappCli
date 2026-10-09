<#
.SYNOPSIS
Pester 5.x tests for the cpp-winui-app sample (C++/WinRT WinUI 3, Visual Studio .vcxproj).

.DESCRIPTION
Phase 1: Runs the sample from a clean copy with `winapp run` project mode — MSBuild restore + build,
  packaged registration, detached launch, --no-build, and the missing-toolchain error.
Phase 2: Quick MSBuild build of the existing sample to verify it is not stale.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    # MSBuild from the newest Visual Studio/Build Tools that can build this sample for the current machine's
    # architecture: the MSVC tools for that architecture plus the "Windows Store" (WinUI) C++ application type.
    $script:findMSBuild = {
        $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path $vswhere)) { return $null }
        $component = if ($arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
        foreach ($install in & $vswhere -prerelease -products * -version '[17.8,' -requires $component -sort -property installationPath) {
            $msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
            $winUi = Get-ChildItem (Join-Path $install 'MSBuild\Microsoft\VC') -Directory -Filter 'v*' -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'Application Type\Windows Store') }
            if ((Test-Path $msbuild) -and $winUi) { return $msbuild }
        }
        return $null
    }
    $script:skip = $null -eq (Get-Command npm -ErrorAction SilentlyContinue) -or -not (& $script:findMSBuild)
}

Describe 'cpp-winui-app sample' {

    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        # MSBuild from the newest Visual Studio/Build Tools that can build this sample for the current machine's
        # architecture: the MSVC tools for that architecture plus the "Windows Store" (WinUI) C++ application type.
        $script:findMSBuild = {
            $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            if (-not (Test-Path $vswhere)) { return $null }
            $component = if ($arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
            foreach ($install in & $vswhere -prerelease -products * -version '[17.8,' -requires $component -sort -property installationPath) {
                $msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
                $winUi = Get-ChildItem (Join-Path $install 'MSBuild\Microsoft\VC') -Directory -Filter 'v*' -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path (Join-Path $_.FullName 'Application Type\Windows Store') }
                if ((Test-Path $msbuild) -and $winUi) { return $msbuild }
            }
            return $null
        }
        $script:msbuild = & $script:findMSBuild
        $script:skip = $null -eq (Get-Command npm -ErrorAction SilentlyContinue) -or -not $script:msbuild

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:buildCheckDir = $null
        $script:originalLocation = Get-Location
        $script:arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        Set-Location $script:originalLocation
        if ($script:identityName) {
            Get-AppxPackage -Name $script:identityName -ErrorAction SilentlyContinue |
                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
        }
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            if ($script:buildCheckDir) { Remove-TempTestDirectory -Path $script:buildCheckDir }
        }
    }

    Context 'Phase 1: winapp run on the .vcxproj (from a clean copy)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'cpp-winui'
                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(packages|x64|ARM64|CppWinUIApp|Generated Files)\\' } |
                    ForEach-Object {
                        $target = Join-Path $script:tempDir $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                # A unique identity so this run never collides with a registration from elsewhere.
                $script:identityName = "cpp-winui-test-$([guid]::NewGuid().ToString('N'))"
                $manifestPath = Join-Path $script:tempDir 'Package.appxmanifest'
                $manifest = [System.Xml.Linq.XDocument]::Load($manifestPath)
                $manifest.Root.Element($manifest.Root.Name.Namespace + 'Identity').SetAttributeValue('Name', $script:identityName)
                $manifest.Save($manifestPath)

                Push-Location $script:tempDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It 'Builds with MSBuild, registers, and launches the packaged app with a responsive window' -Skip:$script:skip {
            $appProcess = $null
            try {
                $output = Invoke-WinappCommand -Arguments "run . --arch $($script:arch) --detach --json"
                $result = ($output -join "`n") | ConvertFrom-Json -ErrorAction Stop
                $result.AUMID | Should -BeLike "$($script:identityName)_*!App"
                $appProcess = Get-Process -Id $result.ProcessId -ErrorAction Stop
                $appProcess.Path | Should -BeLike (Join-Path $script:tempDir '*\AppX\CppWinUIApp.exe')

                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                do {
                    $appProcess.Refresh()
                    $appProcess.HasExited | Should -BeFalse -Because 'the app must survive startup'
                    if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero -and $appProcess.Responding) { break }
                    Start-Sleep -Milliseconds 200
                } while ([DateTime]::UtcNow -lt $deadline)
                $appProcess.MainWindowHandle | Should -Not -Be ([IntPtr]::Zero) -Because 'the XAML window must appear within 30 seconds'
            } finally {
                if ($appProcess -and -not $appProcess.HasExited) {
                    $appProcess.Kill()
                    $appProcess.WaitForExit(10000) | Out-Null
                }
            }
        }

        It 'Registers the existing build with --no-build' -Skip:$script:skip {
            $output = Invoke-WinappCommand -Arguments "run . --arch $($script:arch) --no-build --no-launch"
            "$output" | Should -Not -Match 'Building'
            Get-AppxPackage -Name $script:identityName | Should -Not -BeNullOrEmpty
        }

        It 'Explains what to install when Visual Studio is missing' -Skip:$script:skip {
            # vswhere is located through %ProgramFiles(x86)%, so pointing it elsewhere simulates a machine
            # without Visual Studio or Build Tools.
            $original = ${env:ProgramFiles(x86)}
            try {
                ${env:ProgramFiles(x86)} = Join-Path $script:tempDir 'no-visual-studio'
                $output = & winapp run . --json 2>$null
                $LASTEXITCODE | Should -Not -Be 0
            } finally {
                ${env:ProgramFiles(x86)} = $original
            }
            $result = ($output -join "`n") | ConvertFrom-Json -ErrorAction Stop
            $result.Error | Should -Match 'Desktop development with C\+\+'
            $result.Error | Should -Match 'winget install Microsoft\.VisualStudio\.BuildTools'
        }
    }

    Context 'Phase 2: Sample Build Check' {

        It 'Builds the existing sample with MSBuild' -Skip:$script:skip {
            # Build a copy in a short temp folder: restored NuGet packages under a deep checkout exceed MAX_PATH.
            $script:buildCheckDir = New-TempTestDirectory -Prefix 'cpp-winui-build'
            Get-ChildItem -Path $script:sampleDir -File | Where-Object Name -ne 'test.Tests.ps1' | Copy-Item -Destination $script:buildCheckDir
            Copy-Item -Path (Join-Path $script:sampleDir 'Assets') -Destination $script:buildCheckDir -Recurse
            $platform = if ($script:arch -eq 'arm64') { 'ARM64' } else { 'x64' }
            $output = & $script:msbuild (Join-Path $script:buildCheckDir 'CppWinUIApp.vcxproj') -nologo -restore -p:RestorePackagesConfig=true -p:Configuration=Debug "-p:Platform=$platform" -verbosity:minimal 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "MSBuild failed: $($output | Select-Object -Last 20 | Out-String)"
        }
    }
}
