param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $hasDotnet = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
    $hasNpm = $null -ne (Get-Command npm -ErrorAction SilentlyContinue)
    $hasMauiTemplate = $false

    if ($hasDotnet) {
        $templateList = dotnet new list maui 2>$null
        $hasMauiTemplate = ($LASTEXITCODE -eq 0) -and ($templateList -match 'maui')
    }

    $script:skip = -not ($hasDotnet -and $hasNpm -and $hasMauiTemplate)
}

Describe "maui-app sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:projectDir = $null
        $script:projectName = "testmauiapp"
        $script:rid = "win-x64"
        $script:tfm = "net10.0-windows10.0.19041.0"
        $script:manifestPath = $null

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg
        $script:tempDir = New-TempTestDirectory -Prefix "maui-guide"
        $script:projectDir = Join-Path $script:tempDir $script:projectName

        # Workloads are installed per SDK band. CI installs maui-windows from the repo root, where
        # global.json selects the SDK, so the temp project must resolve that same SDK.
        $repoGlobalJson = Join-Path $script:sampleDir "..\..\global.json"
        if (Test-Path $repoGlobalJson) { Copy-Item $repoGlobalJson $script:tempDir }
    }

    AfterAll {
        Set-Location $script:sampleDir
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            if ($script:sampleDir) {
                Remove-Item -Path (Join-Path $script:sampleDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir "publish") -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
                Get-ChildItem -Path $script:sampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "Phase 1: MAUI Guide Workflow (from scratch)" -Skip:$script:skip {
        It "Should create a new MAUI project" {
            Set-Location $script:tempDir
            dotnet new maui -n $script:projectName --no-restore
            $LASTEXITCODE | Should -Be 0
            $script:projectDir | Should -Exist

            # Strip to Windows-only TFM using XML-aware editing to handle
            # all conditional TargetFrameworks nodes from the template
            $csprojPath = Join-Path $script:projectDir "$($script:projectName).csproj"
            [xml]$xml = Get-Content $csprojPath
            $ns = $xml.DocumentElement.NamespaceURI
            $propertyGroup = $xml.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup'][1]")
            $propertyGroup | Should -Not -BeNullOrEmpty

            # Remove all existing TargetFrameworks elements
            $tfNodes = $xml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='TargetFrameworks']")
            foreach ($node in $tfNodes) {
                $node.ParentNode.RemoveChild($node) | Out-Null
            }

            # Add a single Windows-only TargetFrameworks element
            $newTf = $xml.CreateElement("TargetFrameworks", $ns)
            $newTf.InnerText = $script:tfm
            $propertyGroup.PrependChild($newTf) | Out-Null

            $xml.Save($csprojPath)

            # Restore after rewriting the project to Windows-only
            dotnet restore $csprojPath
            $LASTEXITCODE | Should -Be 0
        }

        It "Should publish the Windows head output" {
            Set-Location $script:projectDir
            dotnet publish ".\$($script:projectName).csproj" `
                -c Release `
                -f $script:tfm `
                -r $script:rid `
                -p:WindowsPackageType=None `
                -p:SelfContained=true `
                -p:WindowsAppSDKSelfContained=true `
                --output ".\publish\$($script:rid)"
            $LASTEXITCODE | Should -Be 0
            Join-Path $script:projectDir "publish\$($script:rid)" | Should -Exist
        }

        It "Should produce a resizetizer manifest in obj" {
            $script:manifestPath = Join-Path $script:projectDir "obj\Release\$($script:tfm)\$($script:rid)\resizetizer\m\Package.appxmanifest"
            $script:manifestPath | Should -Exist
        }

        It "Should generate a certificate from the resolved manifest" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "cert generate --manifest `"$($script:manifestPath)`" --if-exists skip"
            Join-Path $script:projectDir "devcert.pfx" | Should -Exist
        }

        It "Should package MAUI publish output with explicit manifest and executable" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "package .\publish\$($script:rid) --manifest `"$($script:manifestPath)`" --executable $($script:projectName).exe --cert .\devcert.pfx"
            Get-ChildItem -Path $script:projectDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
        }

        It "Should sign the unpackaged MAUI executable" {
            Set-Location $script:projectDir
            $exePath = Join-Path $script:projectDir "publish\$($script:rid)\$($script:projectName).exe"
            $exePath | Should -Exist
            Invoke-WinappCommand -Arguments "sign `"$exePath`" .\devcert.pfx --password password"
        }
    }

    Context "Phase 2: Sample Sanity Check" -Skip:$script:skip {
        It "Should publish the checked-in MAUI sample project" {
            Set-Location $script:sampleDir
            dotnet publish ".\maui-app.csproj" `
                -c Release `
                -f $script:tfm `
                -r $script:rid `
                -p:WindowsPackageType=None `
                -p:SelfContained=true `
                -p:WindowsAppSDKSelfContained=true `
                --output ".\publish\$($script:rid)"
            $LASTEXITCODE | Should -Be 0
        }

        It "Should package the checked-in MAUI sample with the generated manifest" {
            Set-Location $script:sampleDir
            $sampleManifest = Join-Path $script:sampleDir "obj\Release\$($script:tfm)\$($script:rid)\resizetizer\m\Package.appxmanifest"
            $sampleManifest | Should -Exist
            Invoke-WinappCommand -Arguments "cert generate --manifest `"$sampleManifest`" --if-exists skip"
            Invoke-WinappCommand -Arguments "package .\publish\$($script:rid) --manifest `"$sampleManifest`" --executable maui-app.exe --cert .\devcert.pfx"
            Get-ChildItem -Path $script:sampleDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
        }
    }
}
