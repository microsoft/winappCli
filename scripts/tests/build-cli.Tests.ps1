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
            $pe = [byte[]]::new(128)
            $pe[0] = 0x4D
            $pe[1] = 0x5A
            $pe[0x3C] = 0x40
            $pe[0x40] = 0x50
            $pe[0x41] = 0x45
            $pe[0x44] = 0x64
            $pe[0x45] = if ($arch -eq 'win-x64') { 0x86 } else { 0xAA }
            $cliPath = Join-Path $root "artifacts\cli\$arch\winapp.exe"
            [System.IO.File]::WriteAllBytes($cliPath, $pe)
            Copy-Item $cliPath (Join-Path $root "fake-$arch.exe")
            Set-Content (Join-Path $root "artifacts\cli\$arch\winapp.pdb") 'normal PDB output'
            @{
                schemaVersion = 1; runtimeId = $arch; sourceCommit = ('1' * 40)
                fullVersion = '1.2.3-fixture.17'; assemblyVersion = '1.2.3.17'
                cliSha256 = (Get-FileHash $cliPath).Hash
            } | ConvertTo-Json | Set-Content (Join-Path $root "artifacts\cli\$arch.build.json")
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
        Set-Content (Join-Path $root 'scripts\test-cli-shard.ps1') @'
param([int]$Shard, [string]$TestProjectPath, [string]$ResultsDirectory, [string]$CoverageSettings)
Add-Trace 'cli-shard' @($Shard, $TestProjectPath, $ResultsDirectory, $CoverageSettings)
New-Item -ItemType Directory $ResultsDirectory -Force | Out-Null
if ($fixture.Fail -ne 'shard-no-reports') {
    Set-Content (Join-Path $ResultsDirectory "WinApp.Cli.Tests.shard-$Shard.trx") '<TestRun/>'
    Set-Content (Join-Path $ResultsDirectory "WinApp.Cli.Tests.shard-$Shard.cobertura.xml") '<coverage/>'
    Set-Content (Join-Path $ResultsDirectory "cli-shard-$Shard.json") '{"expectedTests":1}'
}
if ($fixture.Fail -eq 'shard-throw') { throw 'Shard report assertion failed' }
$global:LASTEXITCODE = if ($fixture.Fail -eq 'cli-shard') { 29 } else { 0 }
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
function git {
    if (($args -join ' ') -ne 'rev-parse HEAD') { throw "Unexpected git operation: $args" }
    Add-Trace 'source-commit'
    $global:LASTEXITCODE = if ($fixture.Fail -eq 'git') { 1 } else { 0 }
    return ('1' * 40)
}
foreach ($arch in @('win-x64', 'win-arm64')) {
    $path = Join-Path $PSScriptRoot "artifacts\cli\$arch\winapp.exe"
    Set-Item -LiteralPath "Function:\$path" -Value {
        Add-Trace 'published-schema' (@($MyInvocation.MyCommand.Name) + @($args))
        if ($fixture.Fail -eq 'invalid-schema') { '{invalid' } else { '{"source":"current-cli"}' }
        $global:LASTEXITCODE = if ($fixture.Fail -eq 'schema') { 17 } else { 0 }
    }
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
        $runtimeId = $arguments[[array]::IndexOf($arguments, '-r') + 1]
        Copy-Item (Join-Path $PSScriptRoot "fake-$runtimeId.exe") (Join-Path $output 'winapp.exe')
        if ($fixture.Fail -eq 'publish-placeholder') { Set-Content (Join-Path $output 'winapp.exe') 'placeholder' }
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
        if ($arguments -contains '--report-trx-filename' -and $fixture.Fail -ne 'no-reports') {
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
        $tracePath = Join-Path $Root 'trace.jsonl'
        if (Test-Path $tracePath) { Remove-Item $tracePath }
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
        $result.Trace | Should -Match 'npm run generate-commands --schema .*artifacts\\TestResults\\cli-schema-All.json'
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

    It 'publishes only <Architecture> with the existing AOT version arguments and provenance' -ForEach @(
        @{ Architecture = 'x64' }
        @{ Architecture = 'arm64' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ SkipAll = $true; Architecture = $Architecture }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $publishes = @($result.Calls | Where-Object { $_.Name -eq 'dotnet' -and $_.Arguments[0] -eq 'publish' })
        $publishes.Count | Should -Be 1
        $result.Trace | Should -Match "-c Release -r win-$Architecture --self-contained"
        $result.Trace | Should -Match '/p:Version=1.2.3.17 /p:AssemblyVersion=1.2.3.17 /p:FileVersion=1.2.3.17 /p:InformationalVersion=1.2.3-fixture.17 /p:IncludeSourceRevisionInInformationalVersion=false'
        $result.Trace | Should -Not -Match 'dotnet build|dotnet run|dotnet test|(?m)^npm |package-npm|package-nuget|package-msix|pester|generate-llm-docs'
        $other = if ($Architecture -eq 'x64') { 'arm64' } else { 'x64' }
        Join-Path $root "artifacts\cli\win-$other" | Should -Not -Exist
        $provenance = Get-Content (Join-Path $root "artifacts\cli\win-$Architecture.build.json") -Raw | ConvertFrom-Json
        $provenance.runtimeId | Should -BeExactly "win-$Architecture"
        $provenance.sourceCommit | Should -BeExactly ('1' * 40)
        $provenance.fullVersion | Should -BeExactly '1.2.3-fixture.17'
        $provenance.assemblyVersion | Should -BeExactly '1.2.3.17'
        $provenance.cliSha256 | Should -BeExactly (Get-FileHash (Join-Path $root "artifacts\cli\win-$Architecture\winapp.exe")).Hash
    }

    It 'refuses a successful publish that leaves a placeholder instead of a PE executable' {
        $result = Invoke-BuildFixture $root -Flags @{ SkipAll = $true; Architecture = 'arm64' } -Fail 'publish-placeholder'

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'not a PE executable'
        Join-Path $root 'artifacts\cli\win-arm64.build.json' | Should -Not -Exist
    }

    It 'packages merged architecture publish outputs without changing downloaded files or stale results' {
        $mergeRoot = New-BuildFixture
        foreach ($id in $script:PackageIds) {
            Remove-Item (Join-Path $mergeRoot "artifacts\nuget\$id.9.8.7-prerelease.42.nupkg")
        }
        foreach ($architecture in @('x64', 'arm64')) {
            $publishRoot = New-BuildFixture
            $published = Invoke-BuildFixture $publishRoot -Flags @{ SkipAll = $true; Architecture = $architecture }
            $published.ExitCode | Should -Be 0 -Because $published.Output
            Copy-Item (Join-Path $publishRoot "artifacts\cli\win-$architecture\winapp.exe") (Join-Path $mergeRoot "artifacts\cli\win-$architecture\winapp.exe") -Force
            Copy-Item (Join-Path $publishRoot "artifacts\cli\win-$architecture.build.json") (Join-Path $mergeRoot "artifacts\cli\win-$architecture.build.json") -Force
        }
        $before = @(Get-ChildItem (Join-Path $mergeRoot 'artifacts') -File -Recurse | Get-FileHash)
        $result = Invoke-BuildFixture $mergeRoot -Flags @{ OnlyPackage = $true; UseExistingArtifacts = $true } -PesterVersion ''

        $result.ExitCode | Should -Be 0 -Because $result.Output
        foreach ($file in $before) {
            (Get-FileHash $file.Path).Hash | Should -BeExactly $file.Hash
        }
        $result.Trace | Should -Not -Match 'dotnet |(?m)^npm |pester|stand-down|generate-llm-docs'
        $result.Trace | Should -Match 'package-npm 1.2.3-fixture.17 Stable=False'
        $result.Trace | Should -Match 'package-nuget 1.2.3-fixture.17 Stable=False'
        $result.Trace | Should -Match 'package-msix 1.2.3.17 Stable=False fixture'
        foreach ($id in $script:PackageIds) {
            Join-Path $mergeRoot "artifacts\nuget\$id.1.2.3-fixture.17.nupkg" | Should -Exist
        }
        Join-Path $mergeRoot 'src\winapp-CLI\TestResults\stale.trx' | Should -Exist
        Join-Path $mergeRoot 'TestResults\stale.trx' | Should -Exist
        $result.Output | Should -Match 'awaiting validation'
    }

    It 'packages stable provenance without rebaking or republishing' {
        foreach ($id in $script:PackageIds) {
            Remove-Item (Join-Path $root "artifacts\nuget\$id.9.8.7-prerelease.42.nupkg")
        }
        foreach ($architecture in @('x64', 'arm64')) {
            $path = Join-Path $root "artifacts\cli\win-$architecture.build.json"
            $provenance = Get-Content $path -Raw | ConvertFrom-Json
            $provenance.fullVersion = '1.2.3'
            $provenance | ConvertTo-Json | Set-Content $path
        }
        $result = Invoke-BuildFixture $root -Flags @{ OnlyPackage = $true; UseExistingArtifacts = $true; Stable = $true }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Trace | Should -Not -Match 'dotnet |(?m)^npm |generate-llm-docs'
        $result.Trace | Should -Match 'package-npm 1.2.3 Stable=True'
        $result.Trace | Should -Match 'package-nuget 1.2.3 Stable=True'
        $result.Trace | Should -Match 'package-msix 1.2.3.17 Stable=True'
    }

    It 'rejects packaging with missing <Path> before any package creation or cleanup' -ForEach @(
        @{ Path = 'win-x64\winapp.exe' }
        @{ Path = 'win-arm64\winapp.exe' }
        @{ Path = 'win-x64.build.json' }
        @{ Path = 'win-arm64.build.json' }
    ) {
        Remove-Item (Join-Path $root "artifacts\cli\$Path")
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyPackage = $true; UseExistingArtifacts = $true }

        $result.ExitCode | Should -Not -Be 0
        $result.Trace | Should -Not -Match 'package-npm|package-nuget|package-msix|dotnet |(?m)^npm '
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'rejects <Case> packaging inputs with no mutations' -ForEach @(
        @{ Case = 'x64 binary in ARM64 folder'; RuntimeId = 'win-arm64'; Change = 'machine' }
        @{ Case = 'ARM64 binary in x64 folder'; RuntimeId = 'win-x64'; Change = 'machine' }
        @{ Case = 'placeholder executable'; RuntimeId = 'win-arm64'; Change = 'placeholder' }
        @{ Case = 'invalid PE signature'; RuntimeId = 'win-arm64'; Change = 'signature' }
        @{ Case = 'invalid PE offset'; RuntimeId = 'win-arm64'; Change = 'offset' }
        @{ Case = 'truncated PE header'; RuntimeId = 'win-arm64'; Change = 'truncated' }
        @{ Case = 'modified executable'; RuntimeId = 'win-arm64'; Change = 'hash' }
        @{ Case = 'different source commit'; RuntimeId = 'win-arm64'; Change = 'sourceCommit' }
        @{ Case = 'different package version'; RuntimeId = 'win-arm64'; Change = 'fullVersion' }
        @{ Case = 'different assembly version'; RuntimeId = 'win-x64'; Change = 'assemblyVersion' }
        @{ Case = 'different provenance architecture'; RuntimeId = 'win-arm64'; Change = 'runtimeId' }
        @{ Case = 'unsupported provenance schema'; RuntimeId = 'win-arm64'; Change = 'schemaVersion' }
        @{ Case = 'invalid provenance JSON'; RuntimeId = 'win-arm64'; Change = 'json' }
    ) {
        $cli = Join-Path $root "artifacts\cli\$RuntimeId\winapp.exe"
        $metadata = Join-Path $root "artifacts\cli\$RuntimeId.build.json"
        if ($Change -in @('machine', 'placeholder', 'signature', 'offset', 'truncated', 'hash')) {
            $bytes = [System.IO.File]::ReadAllBytes($cli)
            switch ($Change) {
                'machine' { $bytes[0x45] = if ($RuntimeId -eq 'win-x64') { 0xAA } else { 0x86 } }
                'placeholder' { $bytes = [System.Text.Encoding]::UTF8.GetBytes('placeholder') }
                'signature' { $bytes[0x40] = 0 }
                'offset' { $bytes[0x3C] = 0xFF }
                'truncated' { $bytes = $bytes[0..65] }
                'hash' { $bytes[127] = 1 }
            }
            [System.IO.File]::WriteAllBytes($cli, $bytes)
        } elseif ($Change -eq 'json') {
            Set-Content $metadata '{invalid'
        } else {
            $provenance = Get-Content $metadata -Raw | ConvertFrom-Json
            $provenance.$Change = 'different'
            $provenance | ConvertTo-Json | Set-Content $metadata
        }
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyPackage = $true; UseExistingArtifacts = $true }

        $result.ExitCode | Should -Not -Be 0
        $result.Trace | Should -Not -Match 'package-npm|package-nuget|package-msix|dotnet |(?m)^npm '
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
        if ($Change -eq 'machine') { $result.Output | Should -Match 'PE Machine' }
        if ($Change -eq 'hash') { $result.Output | Should -Match 'hash does not match' }
    }

    It 'runs only the UI Automation lane without Node, analyzer or Pester setup' {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'UIAutomation' } -PesterVersion ''

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        $result.Trace | Should -Match 'dotnet build src\\winapp-CLI\\WinApp.UIAutomation.Tests\\WinApp.UIAutomation.Tests.csproj -c Debug -p:\s*TreatWarningsAsErrors=true'
        $result.Trace | Should -Match 'dotnet run --project src\\winapp-CLI\\WinApp.UIAutomation.Tests'
        $result.Trace | Should -Not -Match 'dotnet publish|winapp.sln|(?m)^npm |schema|WinApp.Cli.Tests.csproj|dotnet test|stand-down|nuget-pester|scripts-pester'
        Join-Path $root 'artifacts\TestResults\WinApp.UIAutomation.Tests.trx' | Should -Exist
        Join-Path $root 'artifacts\TestResults\WinApp.Cli.Tests.trx' | Should -Not -Exist
        $result.Output | Should -Match 'all other required suites and shards must also pass'
    }

    It 'runs every other suite in the Core lane without rerunning UI Automation tests' {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Core' }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        $result.Trace | Should -Match 'WinApp.Cli.Tests.csproj -c Debug --no-build'
        $result.Trace | Should -Match 'npm test'
        $result.Trace | Should -Match 'dotnet test .*Microsoft.WindowsAppSDK.Analyzers.Tests.csproj'
        foreach ($name in @('stand-down', 'nuget-pester', 'scripts-pester')) {
            $result.Calls.Name | Should -Contain $name
        }
        $result.Trace | Should -Not -Match 'dotnet publish|WinApp.UIAutomation.Tests.csproj'
        Join-Path $root 'artifacts\TestResults\WinApp.Cli.Tests.trx' | Should -Exist
        Join-Path $root 'artifacts\TestResults\WinApp.UIAutomation.Tests.trx' | Should -Not -Exist
    }

    It 'covers All exactly once across Cli, Auxiliary and UIAutomation and the legacy Core partition' {
        $suiteCalls = @{}
        $suiteReports = @{}
        foreach ($suite in @('All', 'Core', 'UIAutomation', 'Cli', 'Auxiliary')) {
            $fixtureRoot = New-BuildFixture
            $result = Invoke-BuildFixture $fixtureRoot -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = $suite }
            $result.ExitCode | Should -Be 0 -Because $result.Output
            $suiteCalls[$suite] = @($result.Calls | Where-Object {
                $_.Name -in @('stand-down', 'nuget-pester', 'scripts-pester') -or
                ($_.Name -eq 'npm' -and $_.Arguments[0] -eq 'test') -or
                ($_.Name -eq 'dotnet' -and (
                    $_.Arguments[0] -eq 'test' -or
                    ($_.Arguments -contains '--report-trx-filename')))
            } | ForEach-Object {
                # Fixture paths differ; keep just the suite identity.
                if ($_.Name -eq 'dotnet') {
                    if ($_.Arguments[0] -eq 'test') { 'analyzer' } else {
                        $_.Arguments[[array]::IndexOf($_.Arguments, '--report-trx-filename') + 1]
                    }
                } else { $_.Name }
            } | Sort-Object)
            $suiteReports[$suite] = @(Get-ChildItem (Join-Path $fixtureRoot 'artifacts\TestResults') -File |
                Where-Object Extension -In @('.trx', '.xml') |
                Select-Object -ExpandProperty Name | Sort-Object)
        }
        (($suiteCalls.Core + $suiteCalls.UIAutomation | Sort-Object) -join ',') |
            Should -BeExactly ($suiteCalls.All -join ',')
        (($suiteReports.Core + $suiteReports.UIAutomation | Sort-Object) -join ',') |
            Should -BeExactly ($suiteReports.All -join ',')
        (($suiteCalls.Cli + $suiteCalls.Auxiliary + $suiteCalls.UIAutomation | Sort-Object) -join ',') |
            Should -BeExactly ($suiteCalls.All -join ',')
        (($suiteReports.Cli + $suiteReports.Auxiliary + $suiteReports.UIAutomation | Sort-Object) -join ',') |
            Should -BeExactly ($suiteReports.All -join ',')
    }

    It 'builds just the CLI test project and Node in the Cli lane without auxiliary gates' {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Cli' } -PesterVersion ''

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Trace | Should -Match 'dotnet build src\\winapp-CLI\\WinApp.Cli.Tests\\WinApp.Cli.Tests.csproj -c Debug -p:\s*TreatWarningsAsErrors=true'
        $result.Trace | Should -Match 'npm run generate-commands --schema '
        $result.Trace | Should -Match 'npm run compile'
        $result.Trace | Should -Match 'WinApp.Cli.Tests.csproj -c Debug --no-build'
        $result.Trace | Should -Not -Match 'winapp.sln|npm test|dotnet test|stand-down|nuget-pester|scripts-pester|WinApp.UIAutomation.Tests'
    }

    It 'runs auxiliary gates once using published schema without a Debug CLI or test-solution build' {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Auxiliary' }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Trace | Should -Not -Match 'dotnet build|dotnet run|winapp.sln|WinApp.Cli.Tests|WinApp.UIAutomation.Tests'
        $result.Trace | Should -Match 'published-schema .*artifacts\\cli\\win-(x64|arm64)\\winapp.exe --cli-schema'
        $result.Trace | Should -Match 'dotnet test .*Analyzers.Tests.csproj -c Debug -p:\s*TreatWarningsAsErrors=true'
        $result.Trace | Should -Match 'npm run generate-commands --schema '
        $result.Trace | Should -Match 'npm run compile'
        $result.Trace | Should -Match 'npm test'
        foreach ($name in @('stand-down', 'nuget-pester', 'scripts-pester')) {
            @($result.Calls | Where-Object Name -EQ $name).Count | Should -Be 1
        }
        $reports = @(Get-ChildItem (Join-Path $root 'artifacts\TestResults') -File)
        @($reports | Where-Object Extension -EQ '.trx').Count | Should -Be 0
        (($reports | Where-Object Extension -EQ '.xml' | Sort-Object Name).Name -join ',') |
            Should -BeExactly 'nuget-pester.xml,scripts-pester.xml'
        $reports.Name | Should -Contain 'cli-schema-Auxiliary.json'
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
    }

    It 'runs CLI shard <Shard> through the helper and collects uniquely named reports' -ForEach @(
        @{ Shard = 1 }
        @{ Shard = 2 }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Cli'; CliShard = $Shard }

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Trace | Should -Match "cli-shard $Shard .*WinApp.Cli.Tests.csproj .*src\\winapp-CLI\\TestResults .*coverage.runsettings"
        @($result.Calls | Where-Object Name -EQ 'cli-shard').Count | Should -Be 1
        $result.Trace | Should -Not -Match 'WinApp.Cli.Tests.csproj -c Debug --no-build|npm test|dotnet test|stand-down|pester|winapp.sln'
        foreach ($name in @("WinApp.Cli.Tests.shard-$Shard.trx", "WinApp.Cli.Tests.shard-$Shard.cobertura.xml", "cli-shard-$Shard.json")) {
            Join-Path $root "artifacts\TestResults\$name" | Should -Exist
        }
        Join-Path $root "artifacts\TestResults\cli-schema-Cli-$Shard.json" | Should -Exist
        Join-Path $root 'artifacts\TestResults\WinApp.Cli.Tests.trx' | Should -Not -Exist
    }

    It 'merges the four validation lanes without filename collisions and with exactly three TRX reports' {
        $files = @(
            foreach ($lane in @(
                @{ TestSuite = 'Cli'; CliShard = 1 },
                @{ TestSuite = 'Cli'; CliShard = 2 },
                @{ TestSuite = 'Auxiliary' },
                @{ TestSuite = 'UIAutomation' }
            )) {
                $laneRoot = New-BuildFixture
                $flags = @{ OnlyTests = $true; UseExistingArtifacts = $true } + $lane
                $result = Invoke-BuildFixture $laneRoot -Flags $flags
                $result.ExitCode | Should -Be 0 -Because $result.Output
                Get-ChildItem (Join-Path $laneRoot 'artifacts\TestResults') -File
            }
        )
        @($files | Group-Object Name | Where-Object Count -GT 1).Count | Should -Be 0
        (($files | Where-Object Extension -EQ '.trx' | Sort-Object Name).Name -join ',') |
            Should -BeExactly 'WinApp.Cli.Tests.shard-1.trx,WinApp.Cli.Tests.shard-2.trx,WinApp.UIAutomation.Tests.trx'
        (($files | Where-Object Name -Like 'cli-schema-*' | Sort-Object Name).Name -join ',') |
            Should -BeExactly 'cli-schema-Auxiliary.json,cli-schema-Cli-1.json,cli-schema-Cli-2.json'
    }

    It 'fails shard validation for <Step> and preserves any produced reports' -ForEach @(
        @{ Step = 'cli-shard' }
        @{ Step = 'shard-throw' }
        @{ Step = 'shard-no-reports' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Cli'; CliShard = 1 } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Not -Match '\[SUCCESS\]|validation passed'
        if ($Step -ne 'shard-no-reports') {
            Join-Path $root 'artifacts\TestResults\WinApp.Cli.Tests.shard-1.trx' | Should -Exist
        } else {
            $result.Output | Should -Match 'Required test report missing or empty'
        }
    }

    It 'rejects no-report success in the <Suite> lane' -ForEach @(
        @{ Suite = 'All' }
        @{ Suite = 'Core' }
        @{ Suite = 'Cli' }
        @{ Suite = 'UIAutomation' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = $Suite } -Fail 'no-reports'

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Required test report missing or empty'
    }

    It 'keeps <Suite> lane failure nonzero and preserves its reports' -ForEach @(
        @{ Suite = 'Core'; Step = 'cli-tests'; Report = 'WinApp.Cli.Tests.trx' }
        @{ Suite = 'UIAutomation'; Step = 'uia-tests'; Report = 'WinApp.UIAutomation.Tests.trx' }
    ) {
        $before = Get-ArtifactSnapshot $root
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = $Suite } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        (Get-ArtifactSnapshot $root) | Should -BeExactly $before
        Join-Path $root "artifacts\TestResults\$Report" | Should -Exist
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
        @{ Case = 'architecture without SkipAll'; Flags = @{ Architecture = 'x64' } }
        @{ Case = 'architecture while packaging'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; Architecture = 'x64' } }
        @{ Case = 'unknown architecture'; Flags = @{ SkipAll = $true; Architecture = 'x86' } }
        @{ Case = 'OnlyPackage without reuse'; Flags = @{ OnlyPackage = $true } }
        @{ Case = 'OnlyPackage plus OnlyTests'; Flags = @{ OnlyPackage = $true; OnlyTests = $true; UseExistingArtifacts = $true } }
        @{ Case = 'OnlyPackage with Clean'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; Clean = $true } }
        @{ Case = 'OnlyPackage with Bake'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; Bake = $true } }
        @{ Case = 'OnlyPackage skipping npm'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; SkipNpm = $true } }
        @{ Case = 'OnlyPackage skipping NuGet'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; SkipNuGet = $true } }
        @{ Case = 'OnlyPackage skipping MSIX'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; SkipMsix = $true } }
        @{ Case = 'OnlyPackage with a test suite'; Flags = @{ OnlyPackage = $true; UseExistingArtifacts = $true; TestSuite = 'Cli' } }
        @{ Case = 'shard outside Cli'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; CliShard = 1 } }
        @{ Case = 'shard without reuse'; Flags = @{ OnlyTests = $true; TestSuite = 'Cli'; CliShard = 1 } }
        @{ Case = 'unknown shard'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Cli'; CliShard = 3 } }
        @{ Case = 'suite without reuse'; Flags = @{ OnlyTests = $true; TestSuite = 'Core' } }
        @{ Case = 'unknown suite'; Flags = @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Typo' } }
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
        @{ Path = 'src\winapp-Analyzer\tests\Test-StandDownContract.ps1' }
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

    It 'requires the shard helper before clearing any test reports' {
        Remove-Item (Join-Path $root 'scripts\test-cli-shard.ps1')
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Cli'; CliShard = 1 }

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Required CLI shard helper missing'
        $result.Calls.Count | Should -Be 0
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'requires Pester for Auxiliary rather than silently skipping its gates' {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Auxiliary' } -PesterVersion ''

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Pester 5\+ is required'
        $result.Calls.Count | Should -Be 0
        Join-Path $root 'artifacts\TestResults\stale.trx' | Should -Exist
    }

    It 'propagates <Step> in the new Auxiliary lane' -ForEach @(
        @{ Step = 'schema' }
        @{ Step = 'invalid-schema' }
        @{ Step = 'npm-ci' }
        @{ Step = 'npm-generate-commands' }
        @{ Step = 'npm-compile' }
        @{ Step = 'npm-test' }
        @{ Step = 'analyzer-tests' }
        @{ Step = 'stand-down' }
        @{ Step = 'nuget-pester' }
        @{ Step = 'scripts-pester' }
    ) {
        $result = Invoke-BuildFixture $root -Flags @{ OnlyTests = $true; UseExistingArtifacts = $true; TestSuite = 'Auxiliary' } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Not -Match '\[SUCCESS\]|validation passed'
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

    It 'propagates <Step> failure in packaging-only mode without mutating CLI inputs' -ForEach @(
        @{ Step = 'package-npm' }
        @{ Step = 'package-nuget' }
        @{ Step = 'package-msix' }
    ) {
        foreach ($id in $script:PackageIds) {
            Remove-Item (Join-Path $root "artifacts\nuget\$id.9.8.7-prerelease.42.nupkg")
        }
        $before = @(Get-ChildItem (Join-Path $root 'artifacts\cli') -File -Recurse | Get-FileHash)
        $result = Invoke-BuildFixture $root -Flags @{ OnlyPackage = $true; UseExistingArtifacts = $true } -Fail $Step

        $result.ExitCode | Should -Not -Be 0
        $result.Calls.Name | Should -Contain $Step
        $result.Output | Should -Not -Match '\[SUCCESS\]|awaiting validation|Ready for distribution'
        foreach ($file in $before) {
            (Get-FileHash $file.Path).Hash | Should -BeExactly $file.Hash
        }
    }
}
