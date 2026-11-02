# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

Describe 'Accessibility worker MSIX layout' {
    BeforeAll {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script = Join-Path $repo 'scripts\package-msix.ps1'
        $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $layout = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-MsixPackageLayout'
        }, $true)
        Invoke-Expression $layout.Extent.Text
        $MsixAssetsPath = Join-Path $repo 'msix\Assets'
        $MsixManifestPath = Join-Path $repo 'msix\appxmanifest.xml'
        $Stable = $true
    }

    It 'preserves runtime metadata and attribution next to the correct native executable' -ForEach @('x64', 'arm64') {
        $source = Join-Path $TestDrive "source-$_"
        $output = Join-Path $TestDrive "layout-$_"
        New-Item -ItemType Directory -Path "$source\accessibility\licenses\Axe.Windows", $output -Force | Out-Null
        Set-Content "$source\winapp.exe" "native $_"
        Set-Content "$source\accessibility\winapp-accessibility-worker.exe" "worker $_"
        Set-Content "$source\accessibility\winapp-accessibility-worker.runtimeconfig.json" '{"runtimeOptions":{"includedFrameworks":[]}}'
        Set-Content "$source\accessibility\licenses\Axe.Windows\axe.windows.nuspec.txt" 'attribution'
        New-MsixPackageLayout -LayoutPath $output -SourceBinPath $source -Architecture $_ -Version '1.2.3.4' | Out-Null
        Get-Content "$output\winapp.exe" | Should -Be "native $_"
        Get-Content "$output\accessibility\winapp-accessibility-worker.exe" | Should -Be "worker $_"
        Get-Content "$output\accessibility\winapp-accessibility-worker.runtimeconfig.json" |
            Should -Be '{"runtimeOptions":{"includedFrameworks":[]}}'
        Get-Content "$output\accessibility\licenses\Axe.Windows\axe.windows.nuspec.txt" | Should -Be 'attribution'
        ([xml](Get-Content "$output\AppxManifest.xml")).Package.Identity.ProcessorArchitecture | Should -Be $_
    }

    It 'rejects a CLI layout without its accessibility worker' {
        $source = Join-Path $TestDrive 'missing-worker'
        $output = Join-Path $TestDrive 'missing-worker-layout'
        New-Item -ItemType Directory -Path $source, $output -Force | Out-Null
        Set-Content "$source\winapp.exe" 'native'
        { New-MsixPackageLayout -LayoutPath $output -SourceBinPath $source -Architecture x64 -Version '1.2.3.4' } |
            Should -Throw '*Accessibility worker is missing*'
    }
}
