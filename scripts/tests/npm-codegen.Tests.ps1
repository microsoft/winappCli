#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:npm = (Get-Command npm.cmd -ErrorAction Stop).Source

    function New-NpmFixture {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $npmRoot = Join-Path $root 'src\winapp-npm'
        New-Item -ItemType Directory -Path "$npmRoot\src", "$npmRoot\scripts", "$root\docs", "$root\src\winapp-CLI\WinApp.Cli" -Force | Out-Null
        Copy-Item "$repoRoot\src\winapp-npm\scripts\generate-commands.mjs" "$npmRoot\scripts"
        Set-Content "$root\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj" '<Project />'
        Set-Content "$npmRoot\schema.json" '{"name":"winapp","version":"1.2.3","subcommands":{"init":{"description":"Initialize a project"}}}'
        Set-Content "$npmRoot\fake-cli.cjs" @'
const fs = require('node:fs');
const cp = require('node:child_process');
const original = cp.execFileSync;
cp.execFileSync = (file, args, options) => {
  if (file !== 'dotnet' && !file.endsWith('winapp.exe')) return original(file, args, options);
  fs.appendFileSync('cli-trace.jsonl', JSON.stringify({file, args}) + '\n');
  if (process.env.FIXTURE_FAIL_BUILD && args[0] === 'build') throw new Error('CLI build failed');
  return args[0] === 'build' ? '' : fs.readFileSync('schema.json', 'utf8');
};
require('node:module').syncBuiltinESMExports();
'@

        $package = Get-Content "$repoRoot\src\winapp-npm\package.json" -Raw | ConvertFrom-Json
        foreach ($entryPoint in @('compile', 'compile:watch', 'test')) {
            $package.scripts.$entryPoint = 'node verify-generated.mjs'
        }
        $package | ConvertTo-Json -Depth 10 | Set-Content "$npmRoot\package.json"
        Set-Content "$npmRoot\verify-generated.mjs" @'
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
const source = readFileSync('src/winapp-commands.ts', 'utf8');
assert.ok(source.includes('AUTO-GENERATED'));
assert.ok(source.includes('export interface CommonOptions'));
writeFileSync('action-ran.txt', source);
'@
        return $npmRoot
    }

    function Invoke-NpmFixture {
        param([string]$Root, [string]$EntryPoint)
        Push-Location $Root
        $savedNodeOptions = $env:NODE_OPTIONS
        try {
            $preload = (Join-Path $Root 'fake-cli.cjs').Replace('\', '\\')
            $env:NODE_OPTIONS = "--require `"$preload`""
            $output = & $npm run $EntryPoint --ignore-scripts=false 2>&1
            return @{
                ExitCode = $LASTEXITCODE
                Output = $output -join "`n"
            }
        } finally {
            $env:NODE_OPTIONS = $savedNodeOptions
            Pop-Location
        }
    }
}

Describe 'Standalone npm entry points' {
    BeforeEach {
        $root = New-NpmFixture
    }

    It 'generates commands without import.meta.dirname on older supported Node versions' {
        $generator = Join-Path $root 'scripts\generate-commands.mjs'
        $source = [System.IO.File]::ReadAllText($generator)
        [System.IO.File]::WriteAllText($generator, $source.Replace('import.meta.dirname', 'undefined'))
        $result = Invoke-NpmFixture $root 'compile'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        Join-Path $root 'action-ran.txt' | Should -Exist
    }

    It '<EntryPoint> generates commands before consuming them on a fresh checkout' -ForEach @(
        @{ EntryPoint = 'compile' }
        @{ EntryPoint = 'compile:watch' }
        @{ EntryPoint = 'test' }
        @{ EntryPoint = 'prepublishOnly' }
    ) {
        Join-Path $root 'src\winapp-commands.ts' | Should -Not -Exist
        $result = Invoke-NpmFixture $root $EntryPoint
        $result.ExitCode | Should -Be 0 -Because $result.Output
        Join-Path $root 'src\winapp-commands.ts' | Should -Exist
        Join-Path $root 'action-ran.txt' | Should -Exist
        $trace = Get-Content (Join-Path $root 'cli-trace.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
        $trace.Count | Should -Be 2
        $trace[0].args[0] | Should -Be 'build'
        $trace[1].args | Should -Contain '--no-build'
        $trace[1].args | Should -Contain '--cli-schema'
    }

    It '<EntryPoint> stops before consuming commands when generation fails' -ForEach @(
        @{ EntryPoint = 'compile' }
        @{ EntryPoint = 'compile:watch' }
        @{ EntryPoint = 'test' }
        @{ EntryPoint = 'prepublishOnly' }
    ) {
        Set-Content (Join-Path $root 'schema.json') '{invalid'
        $result = Invoke-NpmFixture $root $EntryPoint
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'SyntaxError'
        Join-Path $root 'src\winapp-commands.ts' | Should -Not -Exist
        Join-Path $root 'action-ran.txt' | Should -Not -Exist
    }

    It 'does not use an obsolete documentation snapshot when bootstrapping' {
        Set-Content (Join-Path $root '..\..\docs\cli-schema.json') '{invalid'
        $result = Invoke-NpmFixture $root 'compile'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content (Join-Path $root 'src\winapp-commands.ts') -Raw | Should -Match 'export async function init'
    }

    It 'uses a built CLI without rebuilding it' {
        $arch = if ((node -p 'process.arch') -eq 'arm64') { 'win-arm64' } else { 'win-x64' }
        New-Item -ItemType Directory -Path "$root\bin\$arch" -Force | Out-Null
        Set-Content "$root\bin\$arch\winapp.exe" 'fixture'
        $result = Invoke-NpmFixture $root 'compile'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $trace = @(Get-Content (Join-Path $root 'cli-trace.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
        $trace.Count | Should -Be 1
        $trace[0].file | Should -BeLike '*winapp.exe'
    }

    It 'does not consume stale generated commands after a CLI build failure' {
        Set-Content (Join-Path $root 'src\winapp-commands.ts') '// AUTO-GENERATED: export interface CommonOptions'
        $saved = $env:FIXTURE_FAIL_BUILD
        try {
            $env:FIXTURE_FAIL_BUILD = '1'
            $result = Invoke-NpmFixture $root 'compile'
        } finally {
            $env:FIXTURE_FAIL_BUILD = $saved
        }
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'CLI build failed'
        Join-Path $root 'action-ran.txt' | Should -Not -Exist
    }
}
