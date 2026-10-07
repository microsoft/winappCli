#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

    function New-SchemaFixture {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        foreach ($directory in @('scripts', 'docs', 'plugins\winapp\skills\fixture', 'plugins\winapp\com.github.copilot\agents',
            'plugins\winapp\.claude-plugin', '.github\plugin', '.claude-plugin', 'src\winapp-npm\src', 'artifacts\cli\win-x64')) {
            New-Item -ItemType Directory -Path (Join-Path $root $directory) -Force | Out-Null
        }

        foreach ($name in @('generate-llm-docs', 'validate-llm-docs', 'validate-plugin-package')) {
            Copy-Item "$repoRoot\scripts\$name.ps1" "$root\scripts"
        }
        Set-Content "$root\plugin.json" '{"version":"1.2.3","skills":["plugins/winapp/skills"],"agents":["plugins/winapp/com.github.copilot/agents/winapp.agent.md"]}'
        Set-Content "$root\plugins\winapp\plugin.json" '{"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"winapp","version":"1.2.3"}'
        Set-Content "$root\plugins\winapp\.claude-plugin\plugin.json" '{"version":"1.2.3","agents":["com.github.copilot/agents/winapp.agent.md"]}'
        Set-Content "$root\.github\plugin\marketplace.json", "$root\.claude-plugin\marketplace.json" '{"version":"1.2.3"}'
        Set-Content "$root\version.json" '{"version":"1.2.3"}'
        Set-Content "$root\src\winapp-npm\src\cli.ts" "const NODE_SUBCOMMANDS = ['create-addon'];"
        $content = @'
---
name: fixture
description: Fixture skill for validating command examples.
---
```powershell
winapp init
```
'@
        Set-Content "$root\plugins\winapp\skills\fixture\SKILL.md" $content
        Set-Content "$root\plugins\winapp\com.github.copilot\agents\winapp.agent.md" $content
        Set-Content "$root\artifacts\cli\win-x64\winapp.exe" 'fixture'
        Set-Content "$root\schema.json" '{"name":"winapp","version":"9.9.9","schemaVersion":"1.0","subcommands":{"init":{}}}'
        Set-Content "$root\run.ps1" @'
param([string]$Operation, [string]$SchemaPath = '')
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot 'artifacts\cli\win-x64\winapp.exe'
Set-Item -LiteralPath "Function:\$cli" -Value {
    Get-Content (Join-Path $PSScriptRoot 'schema.json') -Raw
    $global:LASTEXITCODE = 0
}
try {
    if ($Operation -eq 'generate') {
        & "$PSScriptRoot\scripts\generate-llm-docs.ps1" -CliPath $cli -CalledFromBuildScript
    } elseif ($Operation -eq 'validate') {
        & "$PSScriptRoot\scripts\validate-llm-docs.ps1" -CliPath $cli
    } else {
        $options = if ($SchemaPath) { @{ CliSchemaPath = $SchemaPath } } else { @{} }
        & "$PSScriptRoot\scripts\validate-plugin-package.ps1" @options
    }
    exit $LASTEXITCODE
} catch {
    Write-Host $_
    exit 1
}
'@
        return $root
    }

    function Invoke-SchemaFixture {
        param([string]$Root, [string]$Operation, [string]$SchemaPath = '')
        $output = & pwsh -NoProfile -File "$Root\run.ps1" -Operation $Operation -SchemaPath $SchemaPath 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = $output -join "`n" }
    }
}

Describe 'Live CLI schema consumers' {
    BeforeEach {
        $root = New-SchemaFixture
    }

    It 'generates the schema only under ignored artifacts without modifying hand-written docs' {
        Set-Content "$root\docs\npm-usage.md" 'Hand-written guide'
        $result = Invoke-SchemaFixture $root 'generate'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        "$root\artifacts\docs\cli-schema.json" | Should -Exist
        "$root\docs\cli-schema.json" | Should -Not -Exist
        Get-Content "$root\docs\npm-usage.md" -Raw | Should -BeLike '*Hand-written guide*'
    }

    It 'runs build-free structural plugin checks without a schema snapshot' {
        $result = Invoke-SchemaFixture $root 'plugin'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'Command examples.*not checked'
    }

    It 'validates plugins using the built CLI without a documentation snapshot' {
        $result = Invoke-SchemaFixture $root 'validate'
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'command examples'
        "$root\docs\cli-schema.json" | Should -Not -Exist
    }

    It 'rejects unknown skill commands against the current CLI schema' {
        (Get-Content "$root\plugins\winapp\skills\fixture\SKILL.md" -Raw).Replace('winapp init', 'winapp nonexistent') |
            Set-Content "$root\plugins\winapp\skills\fixture\SKILL.md"
        $result = Invoke-SchemaFixture $root 'validate'
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match "unknown command 'winapp nonexistent'"
    }

    It 'rejects an explicitly missing schema rather than skipping command validation' {
        $result = Invoke-SchemaFixture $root 'plugin' "$root\missing.json"
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'schema.*not found'
    }

    It 'rejects invalid schema data returned by the CLI' -ForEach @('{invalid', '{}') {
        Set-Content "$root\schema.json" $_
        $result = Invoke-SchemaFixture $root 'validate'
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'invalid.*schema|schema.*invalid'
    }
}

Describe 'AI documentation command reference' {
    It 'uses the live CLI schema instead of linking to the retired snapshot' {
        $content = Get-Content "$repoRoot\llms.txt" -Raw
        $content | Should -Not -Match 'https?://\S+/docs/cli-schema\.json'
        $content | Should -Match 'winapp --cli-schema'
    }
}
