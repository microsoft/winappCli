param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command cargo -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Rust App Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command cargo -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg

        $script:tempDir = New-TempTestDirectory -Prefix "rust-guide"
    }

    AfterAll {
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir "target") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Prerequisites" {
        It "Should have cargo available" -Skip:$script:skip {
            Test-Prerequisite 'cargo' | Should -Be $true
        }

        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }
    }

    Context "Rust Guide Workflow (from scratch)" {
        It "Should create a new Rust project" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                cargo new test-rust-app
                $LASTEXITCODE | Should -Be 0
                $script:rustProjectDir = Join-Path $script:tempDir "test-rust-app"
                $script:rustProjectDir | Should -Exist
            } finally { Pop-Location }
        }

        It "Should run winapp init" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                Invoke-WinappCommand -Arguments "init --use-defaults --setup-sdks=none"
            } finally { Pop-Location }
        }

        It "Should have created Package.appxmanifest" -Skip:$script:skip {
            Join-Path $script:rustProjectDir "Package.appxmanifest" | Should -Exist
        }

        It "Should add execution alias to manifest" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                Invoke-WinappCommand -Arguments "manifest add-alias"
            } finally { Pop-Location }
        }

        It "Should build Rust app in debug mode" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                cargo build
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should run app with identity via winapp run" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                # No --with-alias: a console app is aliased by inference, which is what the guide shows.
                Invoke-WinappCommand -Arguments "run .\target\debug --unregister-on-exit"
            } finally { Pop-Location }
        }

        It "Should reject hostile execution alias in manifest" -Skip:$script:skip {
            # Regression test for MSRC RCE fix: a manifest containing a non-bare /
            # path-traversal ExecutionAlias must be rejected by `winapp run --with-alias`
            # before any process is launched.
            Push-Location $script:rustProjectDir
            try {
                $manifestPath = Join-Path $script:rustProjectDir "Package.appxmanifest"
                $original = Get-Content -Raw -Path $manifestPath
                try {
                    $hostile = $original -replace 'Alias="[^"]+"', 'Alias="..\evil.exe"'
                    Set-Content -Path $manifestPath -Value $hostile -NoNewline

                    $output = & winapp run .\target\debug --with-alias --unregister-on-exit 2>&1
                    $LASTEXITCODE | Should -Not -Be 0 -Because "hostile alias must be rejected"
                    ($output -join "`n") | Should -Match '(?i)alias' -Because "error should mention alias validation. Got: $output"
                } finally {
                    Set-Content -Path $manifestPath -Value $original -NoNewline
                }
            } finally { Pop-Location }
        }

        It "Should build Rust app in release mode" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                cargo build --release
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have produced release executable" -Skip:$script:skip {
            Join-Path $script:rustProjectDir "target\release\test-rust-app.exe" | Should -Exist
        }

        It "Should prepare MSIX layout" -Skip:$script:skip {
            $distDir = Join-Path $script:rustProjectDir "dist"
            $null = New-Item -ItemType Directory -Path $distDir -Force
            Copy-Item (Join-Path $script:rustProjectDir "target\release\test-rust-app.exe") -Destination $distDir
            Join-Path $distDir "test-rust-app.exe" | Should -Exist
        }

        It "Should generate dev certificate" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                Invoke-WinappCommand -Arguments "cert generate --if-exists skip"
            } finally { Pop-Location }
        }

        It "Should have created devcert.pfx" -Skip:$script:skip {
            Join-Path $script:rustProjectDir "devcert.pfx" | Should -Exist
        }

        It "Should report certificate info" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                $output = Invoke-WinappCommand -Arguments "cert info devcert.pfx"
                $output | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }

        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:rustProjectDir
            try {
                Invoke-WinappCommand -Arguments "pack dist --manifest Package.appxmanifest --cert devcert.pfx"
            } finally { Pop-Location }
        }

        It "Should have created an MSIX file" -Skip:$script:skip {
            $msix = Get-ChildItem -Path $script:rustProjectDir -Filter "*.msix" |
                Select-Object -First 1
            $msix | Should -Not -BeNullOrEmpty
        }
    }

    Context "Sample Build Check" {
        It "Should build existing sample with cargo" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                cargo build
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have produced debug executable" -Skip:$script:skip {
            Join-Path $script:sampleDir "target\debug\rust-app.exe" | Should -Exist
        }
    }
}
