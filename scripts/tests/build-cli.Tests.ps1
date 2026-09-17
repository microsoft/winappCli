#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $script:BuildScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'build-cli.ps1'
    $script:PackageIds = @(
        'Microsoft.Windows.SDK.BuildTools.WinApp',
        'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation',
        'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording',
        'Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer'
    )

    function New-BuildFixture {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        foreach ($dir in @(
            'scripts\tests', 'src\winapp-npm\bin\win-x64', 'src\winapp-npm\bin\win-arm64',
            'src\winapp-NuGet\tests', 'src\winapp-Analyzer\tests', 'src\winapp-CLI\TestResults',
            'src\winapp-CLI\WinApp.Cli\Services\Controls\Data', 'docs', 'scratch',
            'artifacts\cli\win-x64', 'artifacts\cli\win-arm64', 'artifacts\nuget',
            'artifacts\TestResults', 'TestResults'
        )) {
            New-Item -ItemType Directory -Path (Join-Path $root $dir) -Force | Out-Null
        }
        Copy-Item $script:BuildScript (Join-Path $root 'scripts\build-cli.ps1')
        Set-Content (Join-Path $root 'version.json') '{"version":"1.2.3"}'
        Set-Content (Join-Path $root 'docs\cli-schema.json') '{"source":"stale-docs"}'
        Set-Content (Join-Path $root 'src\winapp-CLI\coverage.runsettings') '<RunSettings/>'
        Set-Content (Join-Path $root 'src\winapp-NuGet\tests\NuGet.Tests.ps1') '# Fixture suite'
        Set-Content (Join-Path $root 'scripts\setup-winapprun.ps1') '# Fixture installer'
        Set-Content (Join-Path $root 'artifacts\keep.txt') 'unrelated artifact'
        foreach ($arch in @('win-x64', 'win-arm64')) {
            Set-Content (Join-Path $root "artifacts\cli\$arch\winapp.exe") "downloaded $arch"
            Set-Content (Join-Path $root "src\winapp-npm\bin\$arch\winapp.exe") 'stale npm binary'
        }
        foreach ($id in $script:PackageIds) {
            Set-Content (Join-Path $root "artifacts\nuget\$id.9.8.7-prerelease.42.nupkg") "downloaded $id"
        }
        foreach ($dir in @('TestResults', 'artifacts\TestResults', 'src\winapp-CLI\TestResults')) {
            Set-Content (Join-Path $root "$dir\stale.trx") 'stale test output'
            Set-Content (Join-Path $root "$dir\stale.cobertura.xml") 'stale coverage'
        }

        $corpusDir = Join-Path $root 'src\winapp-CLI\WinApp.Cli\Services\Controls\Data'
        Set-Content (Join-Path (Split-Path $corpusDir -Parent) 'CacheVersion.cs') 'const string Current = "fixture-v1";'
        Set-Content (Join-Path $corpusDir 'snapshot-manifest.json') '{"cacheVersion":"fixture-v1","scenarioCounts":{"gallery":1,"toolkit":1,"reactor":1}}'
        foreach ($provider in @('gallery', 'toolkit', 'reactor')) {
            $stream = [System.IO.MemoryStream]::new()
            $brotli = [System.IO.Compression.BrotliStream]::new($stream, [System.IO.Compression.CompressionMode]::Compress, $true)
            try {
                $brotli.Write([System.Text.Encoding]::UTF8.GetBytes('{"scenarios":[{"name":"fixture"}]}'))
                $brotli.Dispose()
                [System.IO.File]::WriteAllBytes((Join-Path $corpusDir "snapshot-$provider.json.br"), $stream.ToArray())
            } finally {
                $brotli.Dispose()
                $stream.Dispose()
            }
        }

        Set-Content (Join-Path $root 'scripts\get-build-number.ps1') @'
Add-Trace 'build-number'
$global:LASTEXITCODE = 0
return 17
'@
        Set-Content (Join-Path $root 'scripts\get-prerelease-label.ps1') @'
Add-Trace 'prerelease-label'
$global:LASTEXITCODE = 0
return 'fixture'
'@
        foreach ($name in @('package-npm', 'package-nuget', 'package-msix', 'generate-llm-docs')) {
            Set-Content (Join-Path $root "scripts\$name.ps1") @'
param($Version, [switch]$Stable, $CliBinariesPath, $Tag, $CliPath, [switch]$CalledFromBuildScript)
$name = [System.IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
Add-Trace $name @($Version, "Stable=$Stable", $Tag)
if ($name -eq 'package-nuget') {
    New-Item -ItemType Directory -Path "$PSScriptRoot\..\artifacts\nuget" -Force | Out-Null
    foreach ($id in $fixture.PackageIds) {
        Set-Content "$PSScriptRoot\..\artifacts\nuget\$id.$Version.nupkg" "packaged $id"
    }
}
$global:LASTEXITCODE = if ($fixture.Fail -eq $name) { 23 } else { 0 }
'@
        }
        Set-Content (Join-Path $root 'src\winapp-Analyzer\tests\Test-StandDownContract.ps1') @'
Add-Trace 'stand-down'
$global:LASTEXITCODE = if ($fixture.Fail -eq 'stand-down') { 19 } else { 0 }
'@

        # A separate PowerShell process preserves exit semantics. All build/package/test
        # entry points are fakes; even the nested Pester calls cannot recurse into this suite.
        Set-Content (Join-Path $root 'run.ps1') @'
$ErrorActionPreference = 'Stop'
$fixture = Get-Content "$PSScriptRoot\fixture.json" -Raw | ConvertFrom-Json -AsHashtable
$null = [System.Environment]::CurrentDirectory = $PSScriptRoot
$env:TEMP = $env:TMP = Join-Path $PSScriptRoot 'scratch'
$global:LASTEXITCODE = 0
function Add-Trace {
    param([string]$Name, [object[]]$Arguments = @())
    [pscustomobject]@{ Name = $Name; Arguments = @($Arguments) } |
        ConvertTo-Json -Depth 10 -Compress | Add-Content "$PSScriptRoot\trace.jsonl"
}
function dotnet {
    $arguments = @($args)
    Add-Trace 'dotnet' $arguments
    $step = $arguments[0]
    if ($arguments -contains '--cli-schema') {
        $step = 'schema'
        if ($fixture.Fail -eq 'invalid-schema') { '{invalid' } else { '{"source":"current-cli"}' }
    } elseif ($step -eq 'publish') {
        $output = $arguments[[array]::IndexOf($arguments, '-o') + 1]
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Set-Content (Join-Path $output 'winapp.exe') 'published'
    } elseif ($step -eq 'run' -or $step -eq 'test') {
        $project = $arguments[[array]::IndexOf($arguments, '--project') + 1]
        if ($step -eq 'test') {
            $step = 'analyzer-tests'
        } elseif ($project -match 'WinApp\.UIAutomation\.Tests') {
            $step = 'uia-tests'
        } elseif ($project -match 'WinApp\.Cli\.Tests') {
            $step = 'cli-tests'
        } elseif ($project -match 'SnapshotBaker') {
            $step = 'bake'
        }
        if ($arguments -contains '--report-trx-filename') {
            $output = $arguments[[array]::IndexOf($arguments, '--results-directory') + 1]
            New-Item -ItemType Directory -Path $output -Force | Out-Null
            $trx = $arguments[[array]::IndexOf($arguments, '--report-trx-filename') + 1]
            $coverage = $arguments[[array]::IndexOf($arguments, '--coverage-output') + 1]
            Set-Content (Join-Path $output $trx) '<TestRun/>'
            Set-Content (Join-Path $output $coverage) '<coverage/>'
        }
    }
    $global:LASTEXITCODE = if ($fixture.Fail -eq $step) { 17 } else { 0 }
}
function npm {
    $arguments = @($args)
    Add-Trace 'npm' $arguments
    $step = if ($arguments[0] -eq 'run') { $arguments[1] } else { $arguments[0] }
    if ($step -eq 'generate-commands') {
        if ($arguments -notcontains '--schema') { throw 'Codegen did not receive an explicit live schema' }
        $path = $arguments[[array]::IndexOf($arguments, '--schema') + 1]
        if ((Get-Content $path -Raw | ConvertFrom-Json).source -ne 'current-cli') {
            throw 'Codegen used a stale schema'
        }
    }
    $global:LASTEXITCODE = if ($fixture.Fail -eq "npm-$step") { 13 } else { 0 }
}
function Get-Module {
    param($Name, [switch]$ListAvailable)
    if ($fixture.PesterVersion) {
        [pscustomobject]@{ Name = 'Pester'; Version = [version]$fixture.PesterVersion }
    }
}
function Import-Module {
    [CmdletBinding()]
    param($Name, [switch]$Force)
    if ($fixture.Fail -eq 'pester-import') { throw 'Pester could not be imported' }
}
function New-PesterConfiguration {
    @{ Run = @{}; Output = @{}; TestResult = @{} }
}
function Invoke-Pester {
    param($Configuration)
    $name = if ($Configuration.Run.Path -like '*NuGet.Tests.ps1') { 'nuget-pester' } else { 'scripts-pester' }
    Add-Trace $name @($Configuration.Run.Path, $Configuration.TestResult.OutputPath)
    if ($Configuration.TestResult.Enabled) {
        Set-Content $Configuration.TestResult.OutputPath '<test-results/>'
    }
    $result = [pscustomobject]@{
        FailedCount = 0; FailedBlocksCount = 0; FailedContainersCount = 0
        PassedCount = 1; SkippedCount = 0
    }
    if ($fixture.Fail -eq $name) { $result.($fixture.FailureKind) = 1 }
    return $result
}
try {
    $flags = $fixture.Flags
    & "$PSScriptRoot\scripts\build-cli.ps1" @flags
    exit $LASTEXITCODE
} catch {
    Write-Host $_
    exit 1
}
'@
        return $root
    }

    function Invoke-BuildFixture {
        param(
            [string]$Root,
            [hashtable]$Flags = @{},
            [string]$Fail = '',
            [string]$PesterVersion = '5.7.1',
            [string]$FailureKind = 'FailedCount'
        )
        @{
            Flags = $Flags; Fail = $Fail; PesterVersion = $PesterVersion
            FailureKind = $FailureKind; PackageIds = $script:PackageIds
        } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Root 'fixture.json')
        $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $Root 'run.ps1') 2>&1
        $exitCode = $LASTEXITCODE
        $calls = @(if (Test-Path (Join-Path $Root 'trace.jsonl')) {
            Get-Content (Join-Path $Root 'trace.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
        })
        [pscustomobject]@{
            ExitCode = $exitCode
            Output = $output -join "`n"
            Calls = $calls
            Trace = @($calls | ForEach-Object { "$($_.Name) $($_.Arguments -join ' ')" }) -join "`n"
        }
    }

    function Get-ArtifactSnapshot {
        param([string]$Root)
        @(Get-ChildItem (Join-Path $Root 'artifacts') -File -Recurse |
            Where-Object FullName -NotLike '*\TestResults\*' |
            Sort-Object FullName | ForEach-Object {
                "$([System.IO.Path]::GetRelativePath($Root, $_.FullName))=$((Get-FileHash $_.FullName).Hash)"
            }) -join "`n"
    }
}

Describe 'build-cli.ps1 control flow' {
    BeforeEach {
        $root = New-BuildFixture
    }

    It 'reuses immutable artifacts and runs every test suite with fresh schema and results' {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        $result.Trace | Should -Not -Match 'dotnet publish|package-npm|package-nuget|package-msix|build-number|prerelease-label|generate-llm-docs|SnapshotBaker|generate-docs'
        # PowerShell function binding strips bare -- and separates a -p: value.
        $result.Trace | Should -Match 'dotnet build src\\winapp-CLI\\winapp.sln -c Debug -p:\s*TreatWarningsAsErrors=true'
        $result.Trace | Should -Match 'WinApp.Cli.csproj -c Debug --no-build --cli-schema'
        $result.Trace | Should -Match 'npm ci --ignore-scripts'
        $result.Trace | Should -Match 'npm run generate-commands --schema .*artifacts\\TestResults\\cli-schema.json'
        $result.Trace | Should -Match 'npm run compile'
        $result.Trace | Should -Match 'npm test'
        $result.Trace | Should -Match 'WinApp.Cli.Tests.csproj -c Debug --no-build'
        $result.Trace | Should -Match 'WinApp.UIAutomation.Tests.csproj -c Debug --no-build'
        $result.Trace | Should -Match 'dotnet test .*Microsoft.WindowsAppSDK.Analyzers.Tests.csproj -c Debug'
        $result.Calls.Name | Should -Contain 'stand-down'
        $result.Calls.Name | Should -Contain 'nuget-pester'
        $result.Calls.Name | Should -Contain 'scripts-pester'
        $result.Output | Should -Match '9.8.7-prerelease.42'
        $result.Output | Should -Match 'Existing artifacts validated successfully'
        (Get-Content (Join-Path $root 'docs\cli-schema.json') -Raw) | Should -Match 'stale-docs'
        foreach ($file in @(
            'WinApp.Cli.Tests.trx', 'WinApp.Cli.Tests.cobertura.xml',
            'WinApp.UIAutomation.Tests.trx', 'WinApp.UIAutomation.Tests.cobertura.xml',
            'nuget-pester.xml', 'scripts-pester.xml'
        )) {
            Join-Path $root "artifacts\TestResults\$file" | Should -Exist
        }
        foreach ($dir in @('TestResults', 'artifacts\TestResults', 'src\winapp-CLI\TestResults')) {
            Join-Path $root "$dir\stale.trx" | Should -Not -Exist
            Join-Path $root "$dir\stale.cobertura.xml" | Should -Not -Exist
        }
    }

    It 'skips Debug and redundant npm preparation but still packages with SkipTests' {
        $result = Invoke-BuildFixture $root -Flags @{ SkipTests = $true; SkipDocs = $true }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        @($result.Calls | Where-Object { $_.Name -eq 'dotnet' -and $_.Arguments[0] -eq 'publish' }).Count | Should -Be 2
        $result.Trace | Should -Not -Match 'dotnet build|dotnet run|dotnet test|(?m)^npm |stand-down|nuget-pester|scripts-pester|generate-llm-docs'
        foreach ($name in @('package-npm', 'package-nuget', 'package-msix')) {
            @($result.Calls | Where-Object Name -EQ $name).Count | Should -Be 1
        }
        $result.Trace | Should -Match 'package-npm 1.2.3-fixture.17 Stable=False'
        $result.Trace | Should -Match 'package-nuget 1.2.3-fixture.17 Stable=False'
        $result.Trace | Should -Match 'package-msix 1.2.3.17 Stable=False fixture'
        $result.Output | Should -Match 'awaiting validation'
        $result.Output | Should -Not -Match 'Ready for distribution'
    }

    It 'keeps the default publish, test, docs, and package pipeline' {
        $result = Invoke-BuildFixture $root

        $result.ExitCode | Should -Be 0 -Because $result.Output
        @($result.Calls | Where-Object { $_.Name -eq 'dotnet' -and $_.Arguments[0] -eq 'publish' }).Count | Should -Be 2
        foreach ($name in @('build-number', 'prerelease-label', 'stand-down', 'generate-llm-docs', 'package-npm', 'package-nuget', 'nuget-pester', 'scripts-pester', 'package-msix')) {
            $result.Calls.Name | Should -Contain $name
        }
        $result.Trace | Should -Match 'npm run generate-docs'
        $result.Output | Should -Match 'Ready for distribution'
        Join-Path $root 'artifacts\keep.txt' | Should -Not -Exist
        Join-Path $root 'artifacts\setup-winapprun.ps1' | Should -Exist
    }

    It 'keeps Stable baking and stable package versions' {
        $result = Invoke-BuildFixture $root -Flags @{ Stable = $true; SkipTests = $true }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Trace | Should -Match 'dotnet run --project .*SnapshotBaker'
        $result.Trace | Should -Match '/p:InformationalVersion=1.2.3 '
        $result.Trace | Should -Match 'package-npm 1.2.3 Stable=True'
        $result.Trace | Should -Match 'package-nuget 1.2.3 Stable=True'
        $result.Trace | Should -Match 'package-msix 1.2.3.17 Stable=True'
    }

    It 'keeps legacy OnlyTests publishing without packaging or NuGet tests' {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        @($result.Calls | Where-Object { $_.Name -eq 'dotnet' -and $_.Arguments[0] -eq 'publish' }).Count | Should -Be 2
        $result.Trace | Should -Match 'dotnet build'
        $result.Calls.Name | Should -Contain 'scripts-pester'
        $result.Trace | Should -Not -Match 'package-npm|package-nuget|package-msix|nuget-pester|generate-llm-docs'
    }

    It 'rejects <Case> before changing inputs' -ForEach @(
        @{ Case = 'reuse without OnlyTests'; Flags = @{ UseExistingArtifacts = $true } }
        @{ Case = 'Clean'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; Clean = $true } }
        @{ Case = 'Stable'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; Stable = $true } }
        @{ Case = 'Bake'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; Bake = $true } }
        @{ Case = 'SkipTests'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; SkipTests = $true } }
        @{ Case = 'OnlyDocs'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; OnlyDocs = $true } }
        @{ Case = 'SkipAll'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; SkipAll = $true } }
        @{ Case = 'test suppression'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; FailOnTestFailure = $false } }
    ) {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags $Flags

        $result.ExitCode | Should -Not -Be 0
        $result.Calls.Count | Should -Be 0
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'rejects missing <Path> before changing inputs' -ForEach @(
        @{ Path = 'artifacts\cli\win-x64\winapp.exe' }
        @{ Path = 'artifacts\cli\win-arm64\winapp.exe' }
        @{ Path = 'artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.9.8.7-prerelease.42.nupkg' }
        @{ Path = 'artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.9.8.7-prerelease.42.nupkg' }
        @{ Path = 'artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording.9.8.7-prerelease.42.nupkg' }
        @{ Path = 'artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer.9.8.7-prerelease.42.nupkg' }
        @{ Path = 'src\winapp-NuGet\tests\NuGet.Tests.ps1' }
        @{ Path = 'scripts\tests' }
    ) {
        Remove-Item (Join-Path $root $Path) -Recurse -Force
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true }

        $result.ExitCode | Should -Not -Be 0
        $result.Calls.Count | Should -Be 0
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'rejects <Case> package inputs without rebuilding' -ForEach @(
        @{ Case = 'mismatched'; Replacement = 'Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer.1.2.3.nupkg'; RemoveOriginal = $true }
        @{ Case = 'ambiguous'; Replacement = 'Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer.1.2.3.nupkg'; RemoveOriginal = $false }
        @{ Case = 'empty'; Replacement = 'Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer.9.8.7-prerelease.42.nupkg'; RemoveOriginal = $true }
    ) {
        if ($RemoveOriginal) {
            Remove-Item (Join-Path $root 'artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer.9.8.7-prerelease.42.nupkg')
        }
        $content = if ($Case -eq 'empty') { '' } else { 'other version' }
        Set-Content (Join-Path $root "artifacts\nuget\$Replacement") $content -NoNewline
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true }

        $result.ExitCode | Should -Not -Be 0
        $result.Calls.Count | Should -Be 0
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'requires Pester 5 before cleanup when installed version is <Version>' -ForEach @(
        @{ Version = '' }
        @{ Version = '3.4.0' }
    ) {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true } -PesterVersion $Version

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Pester 5\+ is required'
        $result.Calls.Count | Should -Be 0
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'fails artifact validation on <Step> failure' -ForEach @(
        @{ Step = 'build' }
        @{ Step = 'schema' }
        @{ Step = 'invalid-schema' }
        @{ Step = 'npm-ci' }
        @{ Step = 'npm-generate-commands' }
        @{ Step = 'npm-compile' }
        @{ Step = 'npm-test' }
        @{ Step = 'cli-tests' }
        @{ Step = 'uia-tests' }
        @{ Step = 'analyzer-tests' }
        @{ Step = 'stand-down' }
        @{ Step = 'pester-import' }
    ) {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Not -Match '\[SUCCESS\]|validated successfully|Ready for distribution'
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        if ($Step -in @('cli-tests', 'uia-tests', 'analyzer-tests', 'stand-down')) {
            $result.Calls.Name | Should -Contain 'stand-down'
            Join-Path $root 'artifacts\TestResults\WinApp.Cli.Tests.trx' | Should -Exist
            Join-Path $root 'artifacts\TestResults\WinApp.UIAutomation.Tests.trx' | Should -Exist
            Join-Path $root 'artifacts\TestResults\WinApp.UIAutomation.Tests.cobertura.xml' | Should -Exist
        }
    }

    It 'propagates <Suite> <Kind> rather than reporting success' -ForEach @(
        @{ Suite = 'nuget-pester'; Kind = 'FailedCount' }
        @{ Suite = 'nuget-pester'; Kind = 'FailedBlocksCount' }
        @{ Suite = 'nuget-pester'; Kind = 'FailedContainersCount' }
        @{ Suite = 'scripts-pester'; Kind = 'FailedCount' }
        @{ Suite = 'scripts-pester'; Kind = 'FailedBlocksCount' }
        @{ Suite = 'scripts-pester'; Kind = 'FailedContainersCount' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true } -Fail $Suite -FailureKind $Kind

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Not -Match '\[SUCCESS\]|validated successfully|Ready for distribution'
        Join-Path $root "artifacts\TestResults\$Suite.xml" | Should -Exist
    }

    It 'preserves explicit FailOnTestFailure false in the default pipeline for <Step>' -ForEach @(
        @{ Step = 'npm-test' }
        @{ Step = 'cli-tests' }
        @{ Step = 'analyzer-tests' }
        @{ Step = 'stand-down' }
        @{ Step = 'nuget-pester' }
        @{ Step = 'scripts-pester' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ FailOnTestFailure = $false } -Fail $Step

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Calls.Name | Should -Contain 'package-msix'
        $result.Output | Should -Match 'continuing'
    }

    It 'fails rather than success-shaping a <Step> packaging error' -ForEach @(
        @{ Step = 'package-npm' }
        @{ Step = 'package-nuget' }
        @{ Step = 'package-msix' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ SkipTests = $true; SkipDocs = $true } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Not -Match '\[SUCCESS\]|awaiting validation|Ready for distribution'
    }
}
