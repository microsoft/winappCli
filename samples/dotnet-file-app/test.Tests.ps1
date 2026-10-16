param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe ".NET File-Based App Workflow" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $script:tempDir = $null
        $script:sampleDir = $PSScriptRoot
        $script:sampleApp = Join-Path $PSScriptRoot "app.cs"
        # Only unregister the sample if this run registered it, so a developer's own registration survives skipped runs.
        $script:sampleRegistered = $false

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg

        $script:tempDir = New-TempTestDirectory -Prefix "dotnet-file-app"
        $script:scratchApp = Join-Path $script:tempDir "hello.cs"
    }

    AfterAll {
        # Registrations outlive the run, so remove them even when a test failed midway.
        $apps = @($script:scratchApp)
        if ($script:sampleRegistered) { $apps += $script:sampleApp }
        foreach ($app in $apps) {
            if ($app -and (Test-Path $app)) {
                try { Invoke-WinappCommand -Arguments "unregister `"$app`"" | Out-Null } catch { }
            }
        }

        if (-not $SkipCleanup -and $script:tempDir) {
            Remove-TempTestDirectory -Path $script:tempDir
        }
    }

    Context "Prerequisites" {
        It "Should have dotnet available" -Skip:$script:skip {
            Test-Prerequisite 'dotnet' | Should -Be $true
        }

        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }
    }

    Context "Phase 1: File-based app from scratch" {
        It "Should write a single-file app" -Skip:$script:skip {
            @(
                '#:property OutputType=Exe'
                '#:property TargetFramework=net10.0-windows10.0.19041.0'
                ''
                'using Windows.ApplicationModel;'
                ''
                'try'
                '{'
                '    Console.WriteLine($"Package Family Name: {Package.Current.Id.FamilyName}");'
                '}'
                'catch (InvalidOperationException)'
                '{'
                '    Console.WriteLine("Not packaged");'
                '}'
            ) | Set-Content -Path $script:scratchApp -Encoding utf8

            $script:scratchApp | Should -Exist
        }

        It "Should run without identity via dotnet run" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                $output = & dotnet run hello.cs
                $LASTEXITCODE | Should -Be 0
                ($output -join "`n") | Should -Match 'Not packaged'
            } finally { Pop-Location }
        }

        It "Should run with package identity via winapp run" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                $output = Invoke-WinappCommand -Arguments "run hello.cs"
                ($output -join "`n") | Should -Match 'Package Family Name: hello-'
            } finally { Pop-Location }
        }

        It "Should unregister the app by its .cs file" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                Invoke-WinappCommand -Arguments "unregister hello.cs"
            } finally { Pop-Location }
        }
    }

    Context "Phase 2: Sample app" {
        It "Should run the sample via winapp run" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                $script:sampleRegistered = $true
                $output = Invoke-WinappCommand -Arguments "run app.cs"
                ($output -join "`n") | Should -Match 'Package Family Name: winapp\.samples\.fileapp_'
            } finally { Pop-Location }
        }

        It "Should unregister the sample after winapp run" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-WinappCommand -Arguments "unregister app.cs"
                $script:sampleRegistered = $false
            } finally { Pop-Location }
        }

        It "Should run the sample via dotnet run" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                $script:sampleRegistered = $true
                $output = & dotnet run app.cs
                $LASTEXITCODE | Should -Be 0
                ($output -join "`n") | Should -Match 'Package Family Name: winapp\.samples\.fileapp_'
            } finally { Pop-Location }
        }
    }
}
