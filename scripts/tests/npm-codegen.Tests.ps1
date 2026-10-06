#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:npm = (Get-Command npm.cmd -ErrorAction Stop).Source

    function New-NpmFixture {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $npmRoot = Join-Path $root 'src\winapp-npm'
        New-Item -ItemType Directory -Path "$npmRoot\src", "$npmRoot\scripts", "$root\docs" -Force | Out-Null
        Copy-Item "$repoRoot\src\winapp-npm\scripts\generate-commands.mjs" "$npmRoot\scripts"
        Copy-Item "$repoRoot\docs\cli-schema.json" "$root\docs"

        $package = Get-Content "$repoRoot\src\winapp-npm\package.json" -Raw | ConvertFrom-Json
        foreach ($entryPoint in @('compile', 'compile:watch', 'test', 'generate-docs', 'generate-docs:check')) {
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
        try {
            $output = & $npm run $EntryPoint --ignore-scripts=false 2>&1
            return @{
                ExitCode = $LASTEXITCODE
                Output = $output -join "`n"
            }
        } finally {
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
        @{ EntryPoint = 'generate-docs' }
        @{ EntryPoint = 'generate-docs:check' }
        @{ EntryPoint = 'prepublishOnly' }
    ) {
        Join-Path $root 'src\winapp-commands.ts' | Should -Not -Exist
        $result = Invoke-NpmFixture $root $EntryPoint
        $result.ExitCode | Should -Be 0 -Because $result.Output
        Join-Path $root 'src\winapp-commands.ts' | Should -Exist
        Join-Path $root 'action-ran.txt' | Should -Exist
    }

    It '<EntryPoint> stops before consuming commands when generation fails' -ForEach @(
        @{ EntryPoint = 'compile' }
        @{ EntryPoint = 'compile:watch' }
        @{ EntryPoint = 'test' }
        @{ EntryPoint = 'generate-docs' }
        @{ EntryPoint = 'generate-docs:check' }
        @{ EntryPoint = 'prepublishOnly' }
    ) {
        Set-Content (Join-Path $root '..\..\docs\cli-schema.json') '{invalid'
        $result = Invoke-NpmFixture $root $EntryPoint
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'SyntaxError'
        Join-Path $root 'src\winapp-commands.ts' | Should -Not -Exist
        Join-Path $root 'action-ran.txt' | Should -Not -Exist
    }
}
