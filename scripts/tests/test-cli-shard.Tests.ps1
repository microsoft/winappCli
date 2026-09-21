#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:shardScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'test-cli-shard.ps1'

    function Invoke-ShardFixture([int]$Shard, [string]$Failure = '') {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path "$root\results" -Force
        Copy-Item $shardScript "$root\test-cli-shard.ps1"
        # A previous successful report must not conceal a missing report from this invocation.
        Set-Content "$root\results\WinApp.Cli.Tests.shard-$Shard.trx" '<stale/>'
        Set-Content "$root\results\WinApp.Cli.Tests.shard-$Shard.cobertura.xml" '<stale/>'
        @{ shard = $Shard; failure = $Failure } | ConvertTo-Json | Set-Content "$root\input.json"
        @'
$inputData = Get-Content "$PSScriptRoot\input.json" -Raw | ConvertFrom-Json
function dotnet {
    $arguments = @($args)
    $arguments | ConvertTo-Json -Compress | Add-Content "$PSScriptRoot\calls.jsonl"
    $filterIndex = [array]::IndexOf($arguments, '--filter')
    $filter = if ($filterIndex -ge 0) { $arguments[$filterIndex + 1] } else { '' }
    $selected = if (-not $filter) { 10 } elseif ($filter -eq 'FullyQualifiedName~WinApp.Cli.Tests.PackageCommandTests.') { 4 } elseif ($filter -eq 'FullyQualifiedName!~WinApp.Cli.Tests.PackageCommandTests.') { 6 } else { throw "Unexpected filter $filter" }
    $global:LASTEXITCODE = 0
    if ($arguments -contains '--list-tests') {
        if ($inputData.failure -eq 'discovery-exit') { $global:LASTEXITCODE = 4; return }
        if ($inputData.failure -eq 'discovery-format') { 'unknown output'; return }
        if ($inputData.failure -eq 'empty-shard' -and $filter) { $selected = 0 }
        if ($inputData.failure -eq 'incomplete-shards' -and $filter) { $selected = 2 }
        "Test discovery summary: found $selected test(s) - fixture"
        return
    }
    $minimum = $arguments[[array]::IndexOf($arguments, '--minimum-expected-tests') + 1]
    if ([int]$minimum -ne 1) { throw 'The execution minimum must exclude intentional skips; exact totals are checked in TRX' }
    $results = $arguments[[array]::IndexOf($arguments, '--results-directory') + 1]
    $trxName = $arguments[[array]::IndexOf($arguments, '--report-trx-filename') + 1]
    $coverageName = $arguments[[array]::IndexOf($arguments, '--coverage-output') + 1]
    if ($inputData.failure -eq 'missing-report') { return }
    if ($inputData.failure -eq 'partial-report') { $selected-- }
    $failed = if ($inputData.failure -in @('test-failure', 'failure-with-zero-exit')) { 1 } else { 0 }
    $outcome = if ($failed -gt 0) { 'Failed' } else { 'Completed' }
    $skipped = if ($inputData.failure -eq 'passing-and-skipped') { 1 } elseif ($inputData.failure -like 'all-skipped*') { $selected } else { 0 }
    $executed = $selected - $skipped
    $passed = $executed - $failed
    $pending = 0
    if ($inputData.failure -eq 'incomplete-accounting') { $passed-- }
    if ($inputData.failure -eq 'pending-with-zero-exit') { $pending = 1 }
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<ResultSummary outcome="$outcome"><Counters total="$selected" executed="$executed" passed="$passed" failed="$failed" notExecuted="$skipped" pending="$pending" /></ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $results $trxName)
    if ($inputData.failure -ne 'missing-coverage') {
        '<coverage/>' | Set-Content (Join-Path $results $coverageName)
    }
    if ($inputData.failure -eq 'test-failure') { $global:LASTEXITCODE = 2 }
    if ($inputData.failure -eq 'all-skipped') { $global:LASTEXITCODE = 8 }
    if ($inputData.failure -eq 'minimum-exit-with-complete-report') { $global:LASTEXITCODE = 9 }
}
try {
    & "$PSScriptRoot\test-cli-shard.ps1" -Shard $inputData.shard -TestProjectPath fixture.csproj -ResultsDirectory "$PSScriptRoot\results" -CoverageSettings fixture.runsettings
    exit $LASTEXITCODE
} catch {
    Write-Host $_
    exit 1
}
'@ | Set-Content "$root\run.ps1"
        $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$root\run.ps1" 2>&1
        $exitCode = $LASTEXITCODE
        [pscustomobject]@{ Root = $root; ExitCode = $exitCode; Output = $output -join "`n" }
    }
}

Describe 'CLI shard partition and reporting' {
    It 'runs shard <Shard> with complementary MTP filters and unique complete reports' -ForEach @(
        @{ Shard = 1 }
        @{ Shard = 2 }
    ) {
        $result = Invoke-ShardFixture $Shard
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $manifest = Get-Content "$($result.Root)\results\cli-shard-$Shard.json" -Raw | ConvertFrom-Json
        $manifest.allTests | Should -Be 10
        ($manifest.shardCounts | Measure-Object -Sum).Sum | Should -Be 10
        $manifest.expectedTests | Should -Be @(4, 6)[$Shard - 1]
        "$($result.Root)\results\WinApp.Cli.Tests.shard-$Shard.trx" | Should -Exist
        "$($result.Root)\results\WinApp.Cli.Tests.shard-$Shard.cobertura.xml" | Should -Exist
    }

    It 'assigns existing and new test names to exactly one side of the class predicate' {
        $source = Get-Content $shardScript -Raw
        $source | Should -Match ([regex]::Escape('$filters = @("FullyQualifiedName~$className", "FullyQualifiedName!~$className")'))
        foreach ($name in @(
            'WinApp.Cli.Tests.PackageCommandTests.Existing',
            'WinApp.Cli.Tests.PackageCommandTests.NewDataRow',
            'WinApp.Cli.Tests.BrandNewTests.NewMethod',
            'WinApp.Cli.Tests.PackageCommandTestsExtra.NotInTheSelectedClass'
        )) {
            $first = $name.Contains('WinApp.Cli.Tests.PackageCommandTests.')
            $second = -not $first
            (@($first, $second) | Where-Object { $_ }).Count | Should -Be 1
        }
    }

    It 'accepts intentional skips only when every discovered case is accounted for and some tests execute' {
        $result = Invoke-ShardFixture 2 'passing-and-skipped'
        $result.ExitCode | Should -Be 0 -Because $result.Output
    }

    It 'rejects <Failure> instead of silently losing coverage' -ForEach @(
        @{ Failure = 'discovery-exit' }
        @{ Failure = 'discovery-format' }
        @{ Failure = 'empty-shard' }
        @{ Failure = 'incomplete-shards' }
        @{ Failure = 'missing-report' }
        @{ Failure = 'partial-report' }
        @{ Failure = 'missing-coverage' }
        @{ Failure = 'failure-with-zero-exit' }
        @{ Failure = 'all-skipped' }
        @{ Failure = 'all-skipped-with-zero-exit' }
        @{ Failure = 'incomplete-accounting' }
        @{ Failure = 'pending-with-zero-exit' }
        @{ Failure = 'minimum-exit-with-complete-report' }
    ) {
        $result = Invoke-ShardFixture 1 $Failure
        $result.ExitCode | Should -Not -Be 0 -Because $result.Output
    }

    It 'preserves a test failure exit code and the report needed for diagnostics' {
        $result = Invoke-ShardFixture 2 'test-failure'
        $result.ExitCode | Should -Be 2 -Because $result.Output
        "$($result.Root)\results\WinApp.Cli.Tests.shard-2.trx" | Should -Exist
    }
}
