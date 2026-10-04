#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\Benchmark.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '..\lib\ScenarioLint.psm1') -Force
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

    $plugin = Join-Path $TestDrive 'plugin'
    foreach ($s in @(
            @{ n = 'pkg'; d = 'Package a Windows app as an MSIX installer for distribution. Use when creating a Windows installer.' }
            @{ n = 'ident'; d = 'Enable package identity for desktop apps to use share target and startup tasks.' }
            @{ n = 'ui'; d = 'Inspect running Windows app UIs from the command line: click buttons, read text, take screenshots.' }
        )) {
        New-Item -ItemType Directory -Path (Join-Path $plugin "skills\$($s.n)") -Force | Out-Null
        "---`nname: $($s.n)`ndescription: `"$($s.d)`"`n---`n# body" | Set-Content (Join-Path $plugin "skills\$($s.n)\SKILL.md")
    }
    New-Item -ItemType Directory -Path (Join-Path $plugin 'agents') -Force | Out-Null
    "---`nname: helper`ndescription: >`n  Expert in Windows packaging`n  and code signing.`n---" | Set-Content (Join-Path $plugin 'agents\helper.agent.md')
    $script:corpus = Get-LintCorpus -PluginPaths $plugin

    function New-LintScenario($prompt, $set = 'heldout', $fixture = $null, [switch]$Snapshot, $allow = @()) {
        [pscustomobject]@{ Id = 's'; Set = $set; Prompt = $prompt; FixturePath = $fixture; RoutingSnapshot = [bool]$Snapshot; LeakAllow = @($allow) }
    }
}

Describe 'Lint tokenization' {
    It 'keeps file-name shapes, strips plurals, and drops stop words' {
        Get-LintTokens "Main.js and the packages' net8.0 C++" | Should -Be @('main.js', 'and', 'the', 'package', 'net8.0', 'c++')
        Get-ContentWords 'Package the apps for the testers' | Should -Be @('package', 'app', 'tester')
    }

    It 'breaks bigrams at stop words and punctuation, and ignores neutral platform pairs' {
        Get-ContentBigrams 'signing certificate for WinUI app, Electron desktop' | Should -Be @('signing certificate')
        Get-ContentBigrams 'XAML/code enumerate' | Should -Be @('code enumerate')
        @(Get-ContentBigrams 'XAML, code enumerate').Count | Should -Be 1
        @(Get-ContentBigrams 'XAML/code, enumerate').Count | Should -Be 0
    }

    It 'computes Jaccard overlap' {
        Get-Jaccard @('a', 'b') @('b', 'c') | Should -Be (1 / 3)
        Get-Jaccard @() @() | Should -Be 0
    }

    It 'reads single-line and block frontmatter descriptions' {
        @($corpus.Documents.Name) | Should -Contain 'agent helper'
        ($corpus.Documents | Where-Object Name -eq 'agent helper').Words | Should -Contain 'signing'
    }
}

Describe 'Invoke-ScenarioLint' {
    It 'fails held-out prompts that share a distinctive phrase and only warns for dev' {
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Turn this into an MSIX installer for QA') -Corpus $corpus)
        $f.Rule | Should -Contain 'leak-bigram'
        ($f | Where-Object Rule -eq 'leak-bigram').Level | Should -Be 'error'
        (@(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Turn this into an MSIX installer for QA' -set dev) -Corpus $corpus) | Where-Object Rule -eq 'leak-bigram').Level | Should -Be 'warning'
    }

    It 'allows phrases quoted from a real error' {
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario "Throws 'The process has no package identity' in the debugger" -allow 'package identity') -Corpus $corpus)
        $f.Rule | Should -Not -Contain 'leak-bigram'
    }

    It 'fails on high content-word overlap' {
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'click buttons, read text, take screenshots of running UIs') -Corpus $corpus -MaxJaccard 0.1)
        $f.Rule | Should -Contain 'leak-jaccard'
    }

    It 'fails held-out prompts that use Contoso or name the tooling' {
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Contoso build: run winapp on it') -Corpus $corpus)
        $f.Rule | Should -Contain 'fixed-name'
        $f.Rule | Should -Contain 'names-tooling'
    }

    It 'lints fixture file names as prompt text' {
        $fx = Join-Path $TestDrive 'fx'
        New-Item -ItemType Directory -Path $fx -Force | Out-Null
        Set-Content (Join-Path $fx 'msix-installer-notes.txt') 'notes'
        (@(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Look at the notes' -fixture $fx) -Corpus $corpus)).Rule | Should -Contain 'leak-bigram'
    }

    It 'flags empty fixture files the prompt references unless the scenario is a routing snapshot' {
        $fx = Join-Path $TestDrive 'empty'
        New-Item -ItemType Directory -Path $fx -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $fx 'app.msix') | Out-Null
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Install app.msix please' -set dev -fixture $fx) -Corpus $corpus)
        ($f | Where-Object Rule -eq 'fixture-empty').Level | Should -Be 'error'
        @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Install app.msix please' -set dev -fixture $fx -Snapshot) -Corpus $corpus).Rule | Should -Not -Contain 'fixture-empty'
    }

    It 'warns when the prompt names a file the fixture lacks' {
        $f = @(Invoke-ScenarioLint -Scenarios (New-LintScenario 'Open dist\Tool.msix' -set dev) -Corpus $corpus)
        ($f | Where-Object Rule -eq 'fixture-missing').Level | Should -Be 'warning'
    }
}

Describe 'Repository scenarios' {
    It 'pass the leakage and fixture lint (errors only; dev scenarios may warn)' {
        $scenarios = Get-ScenarioDefinitions -ScenariosRoot (Join-Path $PSScriptRoot '..\scenarios')
        $corpus = Get-LintCorpus -PluginPaths @((Join-Path $repoRoot 'plugins\winapp'), (Join-Path $repoRoot 'plugins\winui\agent-plugin'))
        $errors = @(Invoke-ScenarioLint -Scenarios $scenarios -Corpus $corpus -CapabilityMap (Read-CapabilityMap -Path (Join-Path $PSScriptRoot '..\capabilities.json')) | Where-Object Level -eq 'error')
        $errors | ForEach-Object { "$($_.Scenario): $($_.Rule) $($_.Message)" } | Should -BeNullOrEmpty
    }

    It 'run.ps1 -Lint reports counts and exits 0 without errors' {
        $run = Join-Path $PSScriptRoot '..\run.ps1'
        $out = & pwsh -NoProfile -File $run -Lint -Set dev 2>&1
        $LASTEXITCODE | Should -Be 0
        ($out -join "`n") | Should -Match 'Linted \d+ prompts: 0 errors'
    }
}
