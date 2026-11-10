BeforeAll {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $script:Script = Join-Path $PSScriptRoot '..\export-results.ps1'
    function script:New-FakeRun([string]$Root) {
        $files = @(
            'harness.log', 'runs.jsonl', 'run-info.json', 'run-info-20261105-010203.json', 'summary.md',
            'trials\calc__cli-only__m__1.json', 'runs\001-t\check.json', 'runs\001-t\agent-run.json', 'runs\001-t\agent-run.log',
            'runs\001-t\events.jsonl', 'apps\run1\run-meta.json', 'apps\run1\upstream-pins.json',
            'apps\run1\000_calc\results.json', 'apps\run1\000_calc\trial-result.json', 'apps\run1\000_calc\state.json',
            'apps\run1\000_calc\test-results.json', 'apps\run1\000_calc\live-output.txt',
            'apps\run1\000_calc\session-logs-dir\build-events.jsonl',
            'apps\run1\000_calc\app\Calc\results.json', 'apps\run1\000_calc\app\Calc\obj\project.assets.json',
            'apps\run1\000_calc\app\Calc\MainWindow.xaml', 'apps\run1\000_calc\build-output.txt', 'apps\run1\nested.log',
            'node_modules\x\package.json')
        foreach ($f in $files) {
            $p = Join-Path $Root $f
            New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
            Set-Content -LiteralPath $p -Value '{}'
        }
    }
    function script:Get-ZipEntries([string]$Zip) {
        $z = [IO.Compression.ZipFile]::OpenRead($Zip)
        try { @($z.Entries.FullName | Sort-Object) } finally { $z.Dispose() }
    }
}

Describe 'export-results.ps1' {
    It 'keeps result files and top-level logs, and leaves out app sources, build output and events' {
        $root = Join-Path $TestDrive 'out'
        New-FakeRun $root
        $zip = Join-Path $TestDrive 'results.zip'
        & $script:Script -Path $root -Out $zip *> $null
        $expected = @(
            'apps/run1/000_calc/live-output.txt', 'apps/run1/000_calc/results.json', 'apps/run1/000_calc/state.json',
            'apps/run1/000_calc/test-results.json', 'apps/run1/000_calc/trial-result.json',
            'apps/run1/run-meta.json', 'apps/run1/upstream-pins.json',
            'harness.log', 'run-info-20261105-010203.json', 'run-info.json',
            'runs.jsonl', 'runs/001-t/agent-run.json', 'runs/001-t/agent-run.log', 'runs/001-t/check.json',
            'summary.md', 'trials/calc__cli-only__m__1.json') | Sort-Object
        Get-ZipEntries $zip | Should -Be $expected
    }
    It 'adds session-logs-dir folders and events.jsonl with -IncludeLogs' {
        $root = Join-Path $TestDrive 'out2'
        New-FakeRun $root
        $zip = Join-Path $TestDrive 'logs.zip'
        & $script:Script -Path $root -Out $zip -IncludeLogs *> $null
        $entries = Get-ZipEntries $zip
        $entries | Should -Contain 'apps/run1/000_calc/session-logs-dir/build-events.jsonl'
        $entries | Should -Contain 'runs/001-t/events.jsonl'
        $entries | Should -Not -Contain 'apps/run1/000_calc/app/Calc/results.json'
    }
}
