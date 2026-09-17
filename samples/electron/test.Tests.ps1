<#
.SYNOPSIS
Pester 5.x tests for the Electron sample and guide workflow.

.DESCRIPTION
Phase 1: Follows the Electron guide from scratch — scaffolds an Electron app,
  installs winapp, initializes workspace, creates and builds C#/C++ addons,
  packages the app, and creates a signed MSIX package.
Phase 2: Quick install of the existing sample to verify it is not stale.

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
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Electron Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:appDir = $null
        $script:resolvedPkg = $null

        if (-not $script:skip) {
            $script:resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Phase 1: Electron Guide Workflow (from scratch)" {
        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix "electron-guide"

                # Use a dedicated npm cache to avoid ECOMPROMISED errors in CI
                $npmCacheDir = Join-Path $script:tempDir ".npm-cache"
                $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
                $env:npm_config_cache = $npmCacheDir
            }
        }

        It "Should create a new Electron app" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                $created = Invoke-WithRetry -OperationName "create-electron-app" -MaxAttempts 3 -OnRetry {
                    Remove-Item -Path (Join-Path $script:tempDir "electron-app") -Recurse -Force -ErrorAction SilentlyContinue
                    npm cache clean --force 2>$null
                } -ScriptBlock {
                    npx -y create-electron-app@latest electron-app
                }
                $created | Should -Be $true -Because "Electron app creation should succeed within 3 attempts"
                $script:appDir = Join-Path $script:tempDir "electron-app"
                $script:appDir | Should -Exist

                # Electron 42+ no longer auto-downloads its binary during npm install.
                # Use `npx install-electron` (the supported mechanism) to fetch it.
                $electronExe = Join-Path $script:appDir "node_modules\electron\dist\electron.exe"
                if (-not (Test-Path $electronExe)) {
                    Write-Host "Electron binary not found after scaffold — running npx install-electron..."
                    Push-Location $script:appDir
                    try {
                        # Retried: this pulls ~100 MB from GitHub releases, and a dropped
                        # connection surfaces as 'TypeError: fetch failed' with exit code 1.
                        $installed = Invoke-WithRetry -OperationName "install-electron" -MaxAttempts 3 -ScriptBlock {
                            & npx --yes install-electron 2>&1 | ForEach-Object { Write-Host $_ }
                        }
                        $installed | Should -Be $true -Because "install-electron must exit cleanly"
                        $electronExe | Should -Exist -Because "Electron binary should be present after install-electron"
                    } finally { Pop-Location }
                }
            } finally { Pop-Location }
        }

        It "Should configure package.json for MSIX" -Skip:$script:skip {
            $pkgPath = Join-Path $script:appDir "package.json"
            $pkg = Get-Content $pkgPath | ConvertFrom-Json
            $pkg | Add-Member -MemberType NoteProperty -Name "displayName" -Value "WinApp Electron Test" -Force
            $pkg | Add-Member -MemberType NoteProperty -Name "description" -Value "Test app for winapp CLI" -Force
            if ([string]::IsNullOrEmpty($pkg.version)) { $pkg.version = "1.0.0" }
            $pkg | ConvertTo-Json -Depth 10 | Set-Content $pkgPath
            $pkgPath | Should -Exist
        }

        It "Should install winapp as a local devDependency" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Install-WinappNpmPackage -PackagePath $script:resolvedPkg
                Join-Path $script:appDir "node_modules\.bin\winapp.cmd" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should initialize winapp workspace with JS bindings and C++ projections" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "init . --use-defaults --add-js-bindings --setup-sdks=stable"
            } finally { Pop-Location }
        }

        It "Should create workspace files" -Skip:$script:skip {
            Join-Path $script:appDir ".winapp" | Should -Exist
            Join-Path $script:appDir "winapp.yaml" | Should -Exist
            Join-Path $script:appDir "Package.appxmanifest" | Should -Exist
        }

        # ── JS bindings smoke (v2.x) ─────────────────────────────────────

        It "Should have generated .winapp/bindings with the managed marker" -Skip:$script:skip {
            $bindingsDir = Join-Path $script:appDir ".winapp\bindings"
            $bindingsDir | Should -Exist
            (Join-Path $bindingsDir ".dynwinrt-managed") | Should -Exist
            # 50 is a generous lower bound for the full WinAppSDK scope; catches
            # "0 files generated" regressions without being brittle to SDK updates.
            # -Recurse because codegen emits namespace subdirectories (dynwinrt-codegen
            # 0.1.0-preview.20 moved output from flat to windows/foundation/-style
            # nesting); without it only the 3 root scaffolding files are counted.
            $jsCount = (Get-ChildItem -Path $bindingsDir -Filter '*.js' -Recurse -ErrorAction SilentlyContinue).Count
            $jsCount | Should -BeGreaterThan 50 -Because "Default jsBindings (full WinAppSDK) should generate many JS files"
        }

        It "Should inject @microsoft/dynwinrt as a runtime dep in package.json" -Skip:$script:skip {
            $pkgPath = Join-Path $script:appDir "package.json"
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $pkg.dependencies.'@microsoft/dynwinrt' | Should -Not -BeNullOrEmpty `
                -Because "init via npm shim with JS bindings must auto-inject the runtime dep"
        }

        It "Should write a winmds.lock.json under .winapp/" -Skip:$script:skip {
            $lockfilePath = Join-Path $script:appDir ".winapp\winmds.lock.json"
            $lockfilePath | Should -Exist
            $lockfile = Get-Content $lockfilePath -Raw | ConvertFrom-Json
            $lockfile.schema | Should -BeGreaterThan 0 -Because "Lockfile should have schema versioning"
            $lockfile.packages | Should -Not -BeNullOrEmpty -Because "Lockfile should record discovered packages"
        }

        It "Should re-run codegen via 'winapp restore' without mutating winapp.yaml or jsBindings" -Skip:$script:skip {
            $yamlPath = Join-Path $script:appDir "winapp.yaml"
            $pkgPath = Join-Path $script:appDir "package.json"
            $bindingsDir = Join-Path $script:appDir ".winapp\bindings"
            $yamlHashBefore = (Get-FileHash -Path $yamlPath -Algorithm SHA256).Hash
            $pkgHashBefore = (Get-FileHash -Path $pkgPath -Algorithm SHA256).Hash

            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "restore"
            } finally { Pop-Location }

            $yamlHashAfter = (Get-FileHash -Path $yamlPath -Algorithm SHA256).Hash
            $pkgHashAfter = (Get-FileHash -Path $pkgPath -Algorithm SHA256).Hash
            $yamlHashAfter | Should -Be $yamlHashBefore -Because "restore must not mutate winapp.yaml"
            $pkgHashAfter | Should -Be $pkgHashBefore -Because "restore must not mutate package.json (including winapp.jsBindings)"
            (Join-Path $bindingsDir ".dynwinrt-managed") | Should -Exist `
                -Because "restore should leave the managed marker in place after regen"
        }

        It "Should regenerate bindings via 'winapp node generate-bindings' (codegen-only path)" -Skip:$script:skip {
            $bindingsDir = Join-Path $script:appDir ".winapp\bindings"
            # Wipe bindings to prove generate-bindings re-creates from the cached lockfile.
            if (Test-Path $bindingsDir) {
                Remove-Item -Recurse -Force $bindingsDir
            }
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node generate-bindings"
            } finally { Pop-Location }
            (Join-Path $bindingsDir ".dynwinrt-managed") | Should -Exist `
                -Because "generate-bindings must re-emit the managed marker"
            (Join-Path $bindingsDir "index.js") | Should -Exist `
                -Because "generate-bindings must re-emit the bindings index"
        }

        It "Should resolve '#winapp/bindings' via Node's package imports" -Skip:$script:skip {
            # End-to-end smoke test: verifies the "imports" map in package.json,
            # the codegen output paths, and Node's resolver all agree. Catches
            # regressions where the map points at a file that doesn't exist.
            # Sample is CommonJS, so we only verify the require condition.
            Push-Location $script:appDir
            try {
                & node -e "require('#winapp/bindings')" 2>&1 | ForEach-Object { Write-Host $_ }
                $LASTEXITCODE | Should -Be 0 -Because "Node must resolve #winapp/bindings via package.json imports"
            } finally { Pop-Location }
        }

        It "Should detect winapp.yaml drift and refuse generate-bindings (cross-language hash parity)" -Skip:$script:skip {
            # End-to-end check that the TS yaml-packages-hash matches the C#
            # YamlPackagesHasher used by `winapp restore`. If they drift, this
            # test silently passes generate-bindings even though winmds are
            # stale — exactly the regression the parity test guards against.
            $yamlPath = Join-Path $script:appDir "winapp.yaml"
            $original = Get-Content $yamlPath -Raw
            try {
                $modified = $original -replace '(?m)^(\s*version:\s*)["'']?([\d\.]+)["'']?', '${1}999.999.999'
                $modified | Should -Not -Be $original -Because "must actually modify winapp.yaml for the test to be meaningful"
                Set-Content -Path $yamlPath -Value $modified -NoNewline
                Push-Location $script:appDir
                try {
                    # generate-bindings exits non-zero with a stale-lockfile message;
                    # capture both streams. Invoke-WinappCommand throws on non-zero,
                    # so use the raw helper that captures output.
                    $exitCode = 0
                    $output = & npx --no-install winapp node generate-bindings 2>&1
                    $exitCode = $LASTEXITCODE
                    ($output -join "`n") | Should -Match "stale|drift|restore" `
                        -Because "generate-bindings must surface the stale-lockfile reason"
                    $exitCode | Should -Not -Be 0 -Because "stale-lockfile path must exit non-zero"
                } finally { Pop-Location }
            } finally {
                Set-Content -Path $yamlPath -Value $original -NoNewline
            }
        }

        It "Should create a C++ native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cpp --name testCppAddon"
                Join-Path $script:appDir "testCppAddon" | Should -Exist
                Join-Path $script:appDir "testCppAddon\binding.gyp" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should create a C# native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cs --name testCsAddon"
                Join-Path $script:appDir "testCsAddon" | Should -Exist
                Join-Path $script:appDir "testCsAddon\testCsAddon.csproj" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should build the C++ addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $output = npx node-gyp clean configure build --directory=testCppAddon --verbose 2>&1
                $output | ForEach-Object { Write-Host $_ }
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should build the C# addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                npm run build-testCsAddon
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should download the Electron binary" -Skip:$script:skip {
            # Electron 42+ no longer downloads its binary during `npm install`.
            # Use `npx --yes install-electron` to fetch it reliably in CI.
            $exe = Join-Path $script:appDir "node_modules\electron\dist\electron.exe"
            if (Test-Path $exe) {
                Write-Host "Electron binary already present — skipping download."
            } else {
                Push-Location $script:appDir
                try {
                    Write-Host "Running npx install-electron to fetch Electron binary..."
                    $installed = Invoke-WithRetry -OperationName "install-electron" -MaxAttempts 3 -ScriptBlock {
                        & npx --yes install-electron 2>&1 | ForEach-Object { Write-Host $_ }
                    }
                    $installed | Should -Be $true -Because "install-electron must succeed"
                } finally { Pop-Location }
            }
            $exe | Should -Exist -Because "Electron binary is required for add-electron-debug-identity"
        }

        It "Should add Electron debug identity" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node add-electron-debug-identity --no-install"
            } finally { Pop-Location }
        }

        It "Should package the Electron app" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                npm run package
                $LASTEXITCODE | Should -Be 0
                $script:outDir = Join-Path $script:appDir "out"
                $script:outDir | Should -Exist
                $script:appPackageDir = (Get-ChildItem -Path $script:outDir -Directory | Select-Object -First 1).FullName
                $script:appPackageDir | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }

        It "Should register app with winapp run --no-launch" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "run `"$($script:appPackageDir)`" --no-launch"
            } finally { Pop-Location }
        }

        It "Should generate a development certificate" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "cert generate"
                Join-Path $script:appDir "devcert.pfx" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $certPath = Join-Path $script:appDir "devcert.pfx"
                Invoke-WinappCommand -Arguments "pack `"$($script:appPackageDir)`" --cert `"$certPath`""
                Get-ChildItem -Path $script:appDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }
    }

    Context "Phase 2: Sample Build Check" {
        It "Should install sample dependencies" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                npm install --ignore-scripts
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have node_modules" -Skip:$script:skip {
            Join-Path $script:sampleDir 'node_modules' | Should -Exist
        }

        It "Should have package.json" -Skip:$script:skip {
            Join-Path $script:sampleDir 'package.json' | Should -Exist
        }

        It "Should have forge.config.js" -Skip:$script:skip {
            Join-Path $script:sampleDir 'forge.config.js' | Should -Exist
        }

        It "Should have appxmanifest.xml" -Skip:$script:skip {
            Join-Path $script:sampleDir 'appxmanifest.xml' | Should -Exist
        }

        It "Should build the C# addon" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                npm run build-csAddon
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        # The full JS-bindings pipeline (init → codegen → restore → generate-bindings)
        # is exercised end-to-end in Phase 1. Phase 2 only smoke-checks the committed
        # sample, so assert its JS-bindings wiring isn't silently dropped.
        It "Should declare winapp.jsBindings in the committed sample" -Skip:$script:skip {
            $pkgPath = Join-Path $script:sampleDir 'package.json'
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $pkg.winapp | Should -Not -BeNullOrEmpty -Because "sample must declare a winapp namespace"
            $pkg.winapp.PSObject.Properties.Name | Should -Contain 'jsBindings' `
                -Because "sample opts into JS bindings"
        }

        It "Should wire 'winapp restore' into the sample postinstall" -Skip:$script:skip {
            $pkgPath = Join-Path $script:sampleDir 'package.json'
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $pkg.scripts.postinstall | Should -Match 'winapp restore' `
                -Because "JS bindings are (re)generated by 'winapp restore' on install"
        }

        It "Should wire '#winapp/bindings' package imports in the committed sample" -Skip:$script:skip {
            $pkgPath = Join-Path $script:sampleDir 'package.json'
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $imports = $pkg.imports
            $imports | Should -Not -BeNullOrEmpty -Because "sample must declare the #winapp/bindings imports map"
            $imports.'#winapp/bindings'.require | Should -Be './.winapp/bindings/index.js'
            $imports.'#winapp/bindings'.import | Should -Be './.winapp/bindings/index.mjs'
            $imports.'#winapp/bindings/*'.default | Should -Be './.winapp/bindings/*.js'
        }

        It "Should import from '#winapp/bindings' in the sample entry point" -Skip:$script:skip {
            $indexPath = Join-Path $script:sampleDir 'src/index.js'
            Get-Content $indexPath -Raw | Should -Match "require\(\s*['\`"]#winapp/bindings['\`"]\s*\)" `
                -Because "sample must use the package import alias, not a relative path"
        }
    }
}
