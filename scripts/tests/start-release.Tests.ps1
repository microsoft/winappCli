#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# start-release.ps1 writes version.json, then runs build-cli.ps1, whose docs step
# (generate-llm-docs.ps1) stamps that version on every plugin manifest. These tests keep the
# release path from skipping that step; build-cli.Tests.ps1 checks that these flags run it.

BeforeAll {
    $script:path = Join-Path (Split-Path $PSScriptRoot -Parent) 'start-release.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:path, [ref]$null, [ref]$null)
    $script:builds = @($ast.FindAll({
                param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.InvocationOperator -eq 'Ampersand' -and
                $n.CommandElements[0].Extent.Text -eq '$buildScript'
            }, $true))
}

Describe 'start-release.ps1 version sync' {
    It 'runs build-cli.ps1 after each version.json write: the release override and the bump branch' {
        $builds.Count | Should -Be 2
    }

    It 'never skips the docs step that stamps plugin versions' {
        foreach ($b in $builds) {
            $flags = @($b.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object ParameterName)
            $flags | Should -Be @('SkipTests', 'SkipNpm', 'SkipMsix') -Because $b.Extent.Text
        }
    }

    It 'stages everything the build regenerated in the version commits' {
        $text = Get-Content $path -Raw
        ([regex]::Matches($text, '"add",\s*"--all"')).Count | Should -BeGreaterOrEqual 2
    }
}
