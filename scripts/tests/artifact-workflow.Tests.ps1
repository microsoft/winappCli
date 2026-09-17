#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:buildWorkflow = Get-Content (Join-Path $repoRoot '.github\workflows\build-package.yml') -Raw
    $script:sampleWorkflow = Get-Content (Join-Path $repoRoot '.github\workflows\test-samples.yml') -Raw
    $script:collectAction = Get-Content (Join-Path $repoRoot '.github\actions\collect-metrics\action.yml') -Raw
    $script:npmPackaging = Get-Content (Join-Path $repoRoot 'scripts\package-npm.ps1') -Raw
    $script:testReportWorkflow = Get-Content (Join-Path $repoRoot '.github\workflows\test-report.yml') -Raw

    # Extract the real inline PowerShell rather than testing a copy of the gates.
    function Get-JobText([string]$Workflow, [string]$Name) {
        $match = [regex]::Match($Workflow, "(?ms)^  $([regex]::Escape($Name)):\r?\n(?<body>.*?)(?=^  [\w-]+:|\z)")
        if (-not $match.Success) { throw "Job not found: $Name" }
        $match.Groups['body'].Value
    }

    function Get-RunScript([string]$Text, [string]$Step) {
        $header = [regex]::Match($Text, "(?m)^(?<indent> *)- name: $([regex]::Escape($Step))\r?$")
        if (-not $header.Success) { throw "Step not found: $Step" }
        $indent = $header.Groups['indent'].Value.Length
        $tail = $Text.Substring($header.Index + $header.Length)
        $match = [regex]::Match($tail, "(?m)^ {$($indent + 2)}run: \|\r?\n(?<body>(?:^ {$($indent + 4)}.*(?:\r?\n|\z)|^\r?\n)+)")
        if (-not $match.Success) { throw "Inline script not found: $Step" }
        $match.Groups['body'].Value -replace "(?m)^ {$($indent + 4)}", ''
    }

    $script:buildGate = [scriptblock]::Create((Get-RunScript (Get-JobText $buildWorkflow 'build-and-package') 'Require all build and validation jobs'))
    $script:sampleGate = [scriptblock]::Create((Get-RunScript (Get-JobText $sampleWorkflow 'test-samples-result') 'Check results'))
    $script:sampleWrapperGate = [scriptblock]::Create((Get-RunScript (Get-JobText $buildWorkflow 'test-samples-result') 'Check results'))
    $script:environmentNames = @('NEEDS_JSON', 'IS_PR', 'SAMPLE_RESULT', 'BUILD_RESULT', 'REUSE_ARTIFACTS')
    $script:savedEnvironment = @{}
    foreach ($name in $environmentNames) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
}

AfterAll {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}

Describe 'Artifact-first workflow dependencies' {
    It 'publishes packages without tests and keeps the original check as a strict aggregate' {
        $producer = Get-JobText $buildWorkflow 'build-artifacts'
        $producer | Should -Match 'build-cli\.ps1 -SkipTests -SkipDocs'
        $producer | Should -Not -Match '(?m)^\s+needs:'
        $producer | Should -Not -Match 'collect-metrics|test-results'
        $producer | Should -Match 'Downloads - awaiting validation'
        foreach ($artifact in @('cli-binaries', 'npm-package', 'msix-packages', 'nuget-packages')) {
            $producer | Should -Match "(?m)^\s+name: $artifact\r?$"
        }

        $gate = Get-JobText $buildWorkflow 'build-and-package'
        $gate | Should -Match '(?m)^\s+if: always\(\)'
        $gate | Should -Match 'needs: \[build-artifacts, validate-tests, validate-docs, e2e-test-ui, samples, metrics\]'
    }

    It 'builds both architectures in one producer without intermediate publish artifacts' {
        $producer = Get-JobText $buildWorkflow 'build-artifacts'
        $producer | Should -Match 'fetch-depth: 0'
        $producer | Should -Match 'path: artifacts/cli/'
        $buildWorkflow | Should -Not -Match 'cli-publish-|OnlyPackage| -Architecture '
    }

    It 'keeps formatting, lint and compilation gates on freshly generated npm commands' {
        $codegen = $npmPackaging.IndexOf('npm run generate-commands')
        $format = $npmPackaging.IndexOf('npm run format:check')
        $lint = $npmPackaging.IndexOf('npm run lint')
        $compile = $npmPackaging.IndexOf('npm run compile')
        $codegen | Should -BeGreaterThan -1
        $codegen | Should -BeLessThan $format
        $format | Should -BeLessThan $lint
        $lint | Should -BeLessThan $compile
    }

    It 'starts validation, docs, UI E2E and samples from early artifacts, not the final gate' {
        foreach ($job in @('validate-tests', 'validate-docs', 'e2e-test-ui', 'samples')) {
            $text = Get-JobText $buildWorkflow $job
            $text | Should -Match '(?m)^\s+needs: build-artifacts\r?$'
            $text | Should -Not -Match 'dotnet publish|needs: build-and-package'
        }
        (Get-JobText $buildWorkflow 'validate-tests') |
            Should -Match ([regex]::Escape('$testArgs.CliShard = [int]$env:CLI_SHARD'))
        (Get-JobText $buildWorkflow 'e2e-test-ui') | Should -Match 'test-e2e-winui-ui\.ps1'
        (Get-JobText $buildWorkflow 'e2e-test-ui') | Should -Match 'test-ui-coordination\.ps1'
    }

    It 'runs both CLI shards, auxiliary suites once, and UI Automation on isolated runners' {
        $validation = Get-JobText $buildWorkflow 'validate-tests'
        foreach ($lane in @('Cli-1', 'Cli-2', 'Auxiliary', 'UIAutomation')) {
            ([regex]::Matches($validation, "(?m)^\s+- lane: $lane\r?$")).Count | Should -Be 1
        }
        ([regex]::Matches($validation, '(?m)^\s+suite: Cli\r?$')).Count | Should -Be 2
        ([regex]::Matches($validation, '(?m)^\s+suite: Auxiliary\r?$')).Count | Should -Be 1
        $validation | Should -Match 'shard: 1'
        $validation | Should -Match 'shard: 2'
        $validation | Should -Match 'fail-fast: false'
        $validation | Should -Match 'runs-on: windows-latest'
        $validation | Should -Match 'name: validation-results-\$\{\{ matrix.lane \}\}'
    }

    It 'reuses same-run packages safely on PRs and still builds for manual sample runs' {
        $sampleWorkflow | Should -Match '(?m)^  workflow_call:'
        $sampleWorkflow | Should -Match '(?m)^  workflow_dispatch:'
        $sampleWorkflow | Should -Not -Match '(?m)^  (pull_request|workflow_run|pull_request_target):'
        $sampleWorkflow | Should -Not -Match 'run-id:|github-token:'
        (Get-JobText $buildWorkflow 'samples') | Should -Match 'use-existing-artifacts: true'
        (Get-JobText $sampleWorkflow 'build') | Should -Match 'if: \$\{\{ !inputs.use-existing-artifacts \}\}'
        (Get-JobText $sampleWorkflow 'build') | Should -Match 'fetch-depth: 0'
        (Get-JobText $sampleWorkflow 'test-sample') | Should -Match 'needs.build.result == ''skipped'''
        (Get-JobText $sampleWorkflow 'test-sample') | Should -Match 'name: nuget-packages'
        (Get-JobText $sampleWorkflow 'test-sample') | Should -Not -Match 'continue-on-error: true'
    }

    It 'retains all samples and preserves the main doc drift warning behavior' {
        $expected = 'cpp-app, dotnet-app, electron, flutter-app, maui-app, node-winui, packaging-cli, rust-app, sparse-app, tauri-app, wpf-app, winui-app, winui-solution, winui-unpackaged-app'
        $sampleWorkflow | Should -Match ([regex]::Escape("sample: [$expected]"))
        (Get-JobText $buildWorkflow 'validate-docs') |
            Should -Match ([regex]::Escape("-FailOnDrift:(`$env:IS_PR -eq 'true')"))
    }

    It 'cancels only superseded PR runs' {
        $buildWorkflow | Should -Match ([regex]::Escape('group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.run_id }}'))
        $buildWorkflow | Should -Match ([regex]::Escape("cancel-in-progress: `${{ github.event_name == 'pull_request' }}"))
    }

    It 'does not report missing test artifacts for canceled runs but still reports failures' {
        $report = Get-JobText $testReportWorkflow 'report'
        $report | Should -Match ([regex]::Escape("if: github.event.workflow_run.conclusion != 'cancelled'"))
        $report | Should -Not -Match "conclusion == 'success'"
        $report | Should -Match 'artifact: test-results'
    }

    It 'joins package and test artifacts before collecting and reporting metrics' {
        $metrics = Get-JobText $buildWorkflow 'metrics'
        $metrics | Should -Match 'needs: \[build-artifacts, validate-tests\]'
        $metrics | Should -Match ([regex]::Escape("needs.validate-tests.result == 'success' || (github.event_name == 'pull_request' && needs.validate-tests.result == 'failure')"))
        foreach ($artifact in @('cli-binaries', 'npm-package', 'msix-packages', 'nuget-packages', 'validation-results-Cli-1', 'validation-results-Cli-2', 'validation-results-Auxiliary', 'validation-results-UIAutomation', 'test-results')) {
            $metrics | Should -Match "(?m)^\s+name: $artifact\r?$"
        }
        $metrics.IndexOf('name: test-results') | Should -BeLessThan $metrics.IndexOf('uses: ./.github/actions/collect-metrics')
        $metrics | Should -Match 'path: artifacts/TestResults'
    }
}

Describe 'Required build check outcomes' {
    BeforeEach {
        $script:results = @{}
        foreach ($job in @('build-artifacts', 'validate-tests', 'validate-docs', 'e2e-test-ui', 'samples', 'metrics')) {
            $results[$job] = @{ result = 'success' }
        }
        $env:IS_PR = 'true'
    }

    It 'passes a PR only when every dependency succeeds' {
        $env:NEEDS_JSON = $results | ConvertTo-Json -Compress
        { & $buildGate } | Should -Not -Throw
    }

    It 'rejects every failed, canceled, or skipped dependency' {
        foreach ($job in @($results.Keys)) {
            foreach ($outcome in @('failure', 'cancelled', 'skipped')) {
                $results[$job].result = $outcome
                $env:NEEDS_JSON = $results | ConvertTo-Json -Compress
                { & $buildGate } | Should -Throw -ExpectedMessage "*$job did not meet*"
            }
            $results[$job].result = 'success'
        }
    }

    It 'allows only the deliberate sample skip on main and manual runs' {
        $env:IS_PR = 'false'
        $results.samples.result = 'skipped'
        $env:NEEDS_JSON = $results | ConvertTo-Json -Compress
        { & $buildGate } | Should -Not -Throw
        $results.'validate-tests'.result = 'skipped'
        $env:NEEDS_JSON = $results | ConvertTo-Json -Compress
        { & $buildGate } | Should -Throw
    }
}

Describe 'Required sample check outcomes' {
    It 'keeps the sample report in the workspace when cleanup changes the working directory' {
        $workspace = Join-Path $TestDrive 'sample-report-workspace'
        $fixtureDir = Join-Path $workspace 'samples\fixture'
        $null = New-Item -ItemType Directory -Path $fixtureDir -Force
        @'
param([string]$WinappPath)
Describe 'Fixture sample' {
    AfterAll { Set-Location $PSScriptRoot }
    It 'runs successfully' { 1 | Should -Be 1 }
}
'@ | Set-Content (Join-Path $fixtureDir 'test.Tests.ps1')
        $runScript = Get-RunScript (Get-JobText $sampleWorkflow 'test-sample') 'Run ${{ matrix.sample }} test'
        $runScript = $runScript.Replace('${{ matrix.sample }}', 'fixture')
        $runner = Join-Path $workspace 'run.ps1'
        @"
`$env:GITHUB_WORKSPACE = '$workspace'
Set-Location '$workspace'
Import-Module Pester -MinimumVersion 5.0
$runScript
"@ | Set-Content $runner
        $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $runner 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join "`n")
        Join-Path $workspace 'test-results-fixture.xml' | Should -Exist
        Join-Path $fixtureDir 'test-results-fixture.xml' | Should -Not -Exist
        (Get-JobText $sampleWorkflow 'test-sample') | Should -Match 'if-no-files-found: error'
    }

    It 'requires the expected build result and successful sample jobs for both entry points' {
        foreach ($reuse in @('true', 'false')) {
            $env:REUSE_ARTIFACTS = $reuse
            foreach ($build in @('success', 'failure', 'cancelled', 'skipped')) {
                $env:BUILD_RESULT = $build
                foreach ($tests in @('success', 'failure', 'cancelled', 'skipped')) {
                    $env:SAMPLE_RESULT = $tests
                    $expectedBuild = if ($reuse -eq 'true') { 'skipped' } else { 'success' }
                    if ($build -eq $expectedBuild -and $tests -eq 'success') {
                        { & $sampleGate } | Should -Not -Throw
                    } else {
                        { & $sampleGate } | Should -Throw
                    }
                }
            }
        }
    }

    It 'preserves the top-level required check and rejects skipped or canceled reusable workflows' {
        foreach ($outcome in @('success', 'failure', 'cancelled', 'skipped')) {
            $env:SAMPLE_RESULT = $outcome
            if ($outcome -eq 'success') {
                { & $sampleWrapperGate } | Should -Not -Throw
            } else {
                { & $sampleWrapperGate } | Should -Throw
            }
        }
    }
}

Describe 'Metrics consume actual validation reports' {
    BeforeEach {
        $script:metricsRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path (Join-Path $metricsRoot 'TestResults') -Force
        $source = Get-RunScript $collectAction 'Parse test results'
        $script:parseMetrics = [scriptblock]::Create($source.Replace('${{ inputs.artifacts-path }}', $metricsRoot))
    }

    It 'refuses to publish a zero-test result before the reports arrive' {
        { & $parseMetrics } | Should -Throw -ExpectedMessage '*Missing test report*'
        Join-Path $metricsRoot 'test-summary.json' | Should -Not -Exist
    }

    It 'combines all three reports, including failures, without double-counting' {
        foreach ($name in @('WinApp.Cli.Tests.shard-1', 'WinApp.Cli.Tests.shard-2', 'WinApp.UIAutomation.Tests')) {
            @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="2026-09-17T00:00:00Z" finish="2026-09-17T00:00:01Z" />
  <ResultSummary><Counters total="5" executed="4" passed="3" failed="1" /></ResultSummary>
</TestRun>
'@ | Set-Content (Join-Path $metricsRoot "TestResults\$name.trx")
        }
        & $parseMetrics
        $summary = Get-Content (Join-Path $metricsRoot 'test-summary.json') -Raw | ConvertFrom-Json
        $summary.total | Should -Be 15
        $summary.passed | Should -Be 9
        $summary.failed | Should -Be 3
        $summary.skipped | Should -Be 3
        $summary.durationMs | Should -Be 3000
    }

    It 'rejects partial or malformed reports instead of declaring success' {
        '<TestRun />' | Set-Content (Join-Path $metricsRoot 'TestResults\WinApp.Cli.Tests.shard-1.trx')
        { & $parseMetrics } | Should -Throw -ExpectedMessage '*Missing test report*'
        '<TestRun />' | Set-Content (Join-Path $metricsRoot 'TestResults\WinApp.Cli.Tests.shard-2.trx')
        '<TestRun />' | Set-Content (Join-Path $metricsRoot 'TestResults\WinApp.UIAutomation.Tests.trx')
        { & $parseMetrics } | Should -Throw -ExpectedMessage '*Missing or empty test counters*'
        Join-Path $metricsRoot 'test-summary.json' | Should -Not -Exist
    }

    It 'rejects an extra unsharded report rather than double-counting the CLI suite' {
        foreach ($name in @('WinApp.Cli.Tests.shard-1', 'WinApp.Cli.Tests.shard-2', 'WinApp.UIAutomation.Tests', 'WinApp.Cli.Tests')) {
            '<TestRun />' | Set-Content (Join-Path $metricsRoot "TestResults\$name.trx")
        }
        { & $parseMetrics } | Should -Throw -ExpectedMessage '*Expected exactly three test reports*'
    }

    It 'unions overlapping CLI shard coverage instead of counting shared source lines twice' {
        foreach ($shard in @(1, 2)) {
            $firstHits = if ($shard -eq 1) { 1 } else { 0 }
            $secondHits = if ($shard -eq 2) { 1 } else { 0 }
            $firstCondition = $firstHits * 100
            $secondCondition = $secondHits * 100
            @"
<coverage><packages><package name="CLI"><classes><class filename="shared.cs"><lines>
<line number="1" hits="$firstHits" condition-coverage="50% (1/2)"><conditions>
<condition number="0" coverage="$firstCondition%" />
<condition number="1" coverage="$secondCondition%" />
</conditions></line>
<line number="2" hits="$secondHits" />
</lines></class></classes></package></packages></coverage>
"@ | Set-Content (Join-Path $metricsRoot "TestResults\WinApp.Cli.Tests.shard-$shard.cobertura.xml")
        }
        @'
<coverage><packages><package name="UI"><classes><class filename="ui.cs"><lines>
<line number="1" hits="1" />
<line number="2" hits="0" />
</lines></class></classes></package></packages></coverage>
'@ | Set-Content (Join-Path $metricsRoot 'TestResults\WinApp.UIAutomation.Tests.cobertura.xml')
        $source = (Get-RunScript $collectAction 'Parse coverage results').Replace('${{ inputs.artifacts-path }}', $metricsRoot)
        & ([scriptblock]::Create($source))
        $coverage = Get-Content (Join-Path $metricsRoot 'coverage-summary.json') -Raw | ConvertFrom-Json
        $coverage.lineCoverage | Should -Be 75
        $coverage.branchCoverage | Should -Be 100
    }
}
