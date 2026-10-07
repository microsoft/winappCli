$ErrorActionPreference = 'Stop'
$analyzer = Join-Path $PSScriptRoot '..\..\plugins\winui\agent-plugin\skills\winui-session-report\Analyze-Session.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $analyzer), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join "`n") }

# Import only pure classification helpers; never open a user's session logs.
foreach ($name in 'Test-NoBuildEnabled', 'Test-BuildCapableCommand', 'Get-TurnCategory') {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }.GetNewClosure(), $true)
    if (-not $function) { throw "Missing helper: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}

$cases = @(
    @{ Command = '.\BuildAndRun.ps1'; Build = $true }
    @{ Command = '.\BuildAndRun.ps1 --no-build'; Build = $false }
    @{ Command = 'dotnet build .\App.csproj'; Build = $true }
    @{ Command = 'dotnet publish .\App.csproj -p:PublishAot=true'; Build = $true }
    @{ Command = 'dotnet publish .\App.csproj --no-build'; Build = $false }
    @{ Command = 'dotnet publish .\App.csproj --no-build=false'; Build = $true }
    @{ Command = 'msbuild .\App.csproj'; Build = $true }
    @{ Command = 'winapp run . --on sandbox --detach --json'; Build = $true }
    @{ Command = 'winapp run .\App.csproj --aot --on sandbox'; Build = $true }
    @{ Command = 'winapp run ".\My App\App.csproj" --aot'; Build = $true }
    @{ Command = 'winapp run .\App.slnx --project App'; Build = $true }
    @{ Command = 'winapp run .\App.cs'; Build = $true }
    @{ Command = 'winapp run --detach'; Build = $true }
    @{ Command = 'winapp run . --no-build'; Build = $false }
    @{ Command = 'winapp run . --no-build=true'; Build = $false }
    @{ Command = 'winapp run . --no-build true'; Build = $false }
    @{ Command = 'winapp run . --no-build=false'; Build = $true }
    @{ Command = 'winapp run . --no-build false'; Build = $true }
    @{ Command = 'winapp run . -- --no-build'; Build = $true }
    @{ Command = 'winapp run . --help'; Build = $false }
    @{ Command = 'winapp run .\publish'; Build = $false }
    @{ Command = 'winapp run ".\publish output"'; Build = $false }
    @{ Command = 'winapp run --arch x64 .\dist'; Build = $false }
    @{ Command = 'winapp run --arch=x64 ".\dist output"'; Build = $false }
    @{ Command = 'winapp run --on sandbox --detach .\dist'; Build = $false }
    @{ Command = 'winapp run --detach false --arch x64 .\dist'; Build = $false }
    @{ Command = 'winapp run --aot true --arch x64 .\App.csproj'; Build = $true }
    @{ Command = 'winapp run --no-build false --arch x64 .\App.csproj'; Build = $true }
    @{ Command = 'winapp run --arch x64'; Build = $true }
    @{ Command = 'winapp run --symbols --debug-output .\App.csproj'; Build = $true }
    @{ Command = 'winapp run --arch'; Build = $false }
    @{ Command = 'winapp run --manifest .\misleading.csproj .\dist'; Build = $false }
    @{ Command = 'winapp run --property .\misleading.csproj .\dist'; Build = $false }
    @{ Command = 'winapp package .\App.csproj -c Release'; Build = $true }
    @{ Command = 'winapp package --arch x64 .\App.csproj'; Build = $true }
    @{ Command = 'winapp package --self-contained .\App.csproj --no-sign'; Build = $true }
    @{ Command = 'winapp package --arch=x64 ".\My App\App.csproj"'; Build = $true }
    @{ Command = 'winapp package -c Release --arch x64 -p "Setting=My App.csproj" .\App.csproj'; Build = $true }
    @{ Command = 'winapp package --no-sign .\App.csproj'; Build = $true }
    @{ Command = 'winapp package --no-sign true .\App.csproj'; Build = $true }
    @{ Command = 'winapp package --no-build false --arch x64 .\App.csproj'; Build = $true }
    @{ Command = 'winapp package --no-build true --arch x64 .\App.csproj'; Build = $false }
    @{ Command = 'winapp package --manifest .\misleading.csproj .\publish'; Build = $false }
    @{ Command = 'winapp package --manifest .\misleading.csproj'; Build = $false }
    @{ Command = 'winapp package --property .\misleading.csproj .\publish'; Build = $false }
    @{ Command = 'winapp package --property=Setting=App.csproj .\publish'; Build = $false }
    @{ Command = 'winapp package --output ".\My App.csproj" .\publish'; Build = $false }
    @{ Command = 'winapp package --unknown .\App.csproj'; Build = $false }
    @{ Command = 'winapp pack ".\My App\App.csproj"'; Build = $true }
    @{ Command = 'winapp package .\App.csproj --no-build'; Build = $false }
    @{ Command = 'winapp package .\App.csproj --no-build false'; Build = $true }
    @{ Command = 'winapp package .\App.csproj --help'; Build = $false }
    @{ Command = 'winapp package .\publish'; Build = $false }
    @{ Command = 'winapp package .'; Build = $false }
    @{ Command = 'winapp package .\publish\x64 .\publish\arm64'; Build = $false }
    @{ Command = 'winapp package .\AppxManifest.xml'; Build = $false }
    @{ Command = 'winapp package .\publish --output .\misleading.csproj'; Build = $false }
    @{ Command = 'winapp package --help'; Build = $false }
    @{ Command = 'winapp ui inspect --on sandbox -a 123 --json'; Build = $false }
    @{ Command = 'dotnet publish .\App.csproj; winapp run . --no-build'; Build = $true }
    @{ Command = 'winapp run . --no-build; dotnet publish .\App.csproj'; Build = $true }
    @{ Command = 'winapp run . --no-build && winapp package .\App.csproj'; Build = $true }
)
$failures = @()
foreach ($case in $cases) {
    $actual = Test-BuildCapableCommand $case.Command
    if ($actual -ne $case.Build) {
        $failures += "Expected build=$($case.Build), got ${actual}: $($case.Command)"
    }
    $turn = [pscustomobject]@{
        Tools = @([pscustomobject]@{
            Name = 'powershell'; Args = @{ command = $case.Command }; HasError = $false
        })
        SkillInvocations = @()
    }
    $category = Get-TurnCategory $turn
    if (($category -eq 'build-ok') -ne $case.Build) {
        $failures += "Unexpected category ${category}: $($case.Command)"
    }
    $turn.Tools[0].HasError = $true
    $category = Get-TurnCategory $turn
    if (($category -eq 'build-fix') -ne $case.Build) {
        $failures += "Unexpected failure category ${category}: $($case.Command)"
    }
}
if ($failures.Count) { throw ($failures -join "`n") }

# Evaluate only the aggregate-analysis statements over synthetic turns. This
# verifies report labels without invoking harness detection or reading logs.
$allTurns = @(
    [pscustomobject]@{
        TurnNum = 1
        Tools = @([pscustomobject]@{
            Name = 'powershell'
            Args = @{ command = 'dotnet publish .\App.csproj; winapp run . --no-build --on sandbox' }
            HasError = $false
            ErrorSummary = @()
        })
    }
    [pscustomobject]@{
        TurnNum = 2
        Tools = @([pscustomobject]@{
            Name = 'powershell'
            Args = @{ command = 'winapp package .\publish --no-build' }
            HasError = $true
            ErrorSummary = @('CLI rejected flags on a folder')
        })
    }
)
$start = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('$buildAttemptTurns =') } | Select-Object -First 1
$end = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('$sandboxFailures =') } | Select-Object -First 1
if (-not $start -or -not $end) { throw 'Missing aggregate analysis boundaries.' }
foreach ($statement in $ast.EndBlock.Statements | Where-Object {
    $_.Extent.StartOffset -ge $start.Extent.StartOffset -and $_.Extent.EndOffset -le $end.Extent.EndOffset
}) {
    . ([scriptblock]::Create($statement.Extent.Text))
}
if ($buildWorkflowAttempts -ne 1 -or $buildWorkflowFailures -ne 0 -or $buildErrors.Count -ne 0) {
    throw 'Non-build packaging failure polluted build statistics.'
}
if ($projectBuildStatus -notmatch 'dotnet build/publish: 1' -or $projectBuildStatus -match 'winapp project') {
    throw "No-build run polluted workflow labels: $projectBuildStatus"
}
if ($sandboxCommands.Count -ne 1 -or $sandboxFailures -ne 0) { throw 'Sandbox usage count incorrect.' }

$skillPath = Join-Path (Split-Path $analyzer) 'SKILL.md'
foreach ($block in [regex]::Matches((Get-Content -LiteralPath $skillPath -Raw), '(?ms)^```powershell\r?\n(.*?)^```')) {
    $null = [System.Management.Automation.Language.Parser]::ParseInput(
        $block.Groups[1].Value, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors.Message -join "`n") }
}
Write-Host "PASS: $($cases.Count) classification/category cases, synthetic report statistics, and PowerShell parsing."
