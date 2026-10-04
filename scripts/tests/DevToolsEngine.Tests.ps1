# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

BeforeAll {
    $script:repo = (Resolve-Path "$PSScriptRoot\..\..").Path
    Import-Module "$script:repo\scripts\DevToolsEngine.psm1" -Force

    function New-EngineInputs([string]$Root) {
        foreach ($arch in 'x64', 'arm64') {
            $dir = New-Item "$Root\win-$arch" -ItemType Directory -Force
            foreach ($name in 'winapp.exe', 'WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll', 'winapp-devtools-schema.json', 'WinApp.DevTools.Native.pdb') {
                [IO.File]::WriteAllText((Join-Path $dir $name), "$arch/$name synthetic, not a PE")
            }
        }
    }

    function New-EngineArchive([string]$Root, [string]$Format, [string]$Architecture, [string]$Fault) {
        $inputRoot = Join-Path $Root 'input'
        New-EngineInputs $inputRoot
        $members = [Collections.Generic.List[object]]::new()
        $arches = @(if ($Format -in 'Npm', 'NuGet') { 'x64'; 'arm64' } else { $Architecture })
        foreach ($arch in $arches) {
            $prefix = switch ($Format) {
                Npm { "package/bin/win-$arch/" }
                NuGet { "tools/win-$arch/" }
                default { '' }
            }
            $names = @('winapp.exe', 'WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll')
            if ($Format -ne 'Npm') { $names += 'winapp-devtools-schema.json' }
            if ($Format -eq 'Portable') { $names += 'WinApp.DevTools.Native.pdb' }
            foreach ($name in $names) {
                $bytes = [IO.File]::ReadAllBytes("$inputRoot\win-$arch\$name")
                $member = "$prefix$name"
                if ($name -eq 'WinApp.DevTools.Native.dll' -and $arch -eq $arches[0]) {
                    if ($Fault -eq 'Missing') { continue }
                    switch ($Fault) {
                        Empty { $bytes = [byte[]]@() }
                        Stale { $bytes = [Text.Encoding]::UTF8.GetBytes('stale') }
                        WrongArch { $bytes = [IO.File]::ReadAllBytes("$inputRoot\win-arm64\$name") }
                        WrongPath { $member = "nested/$member" }
                        Duplicate { $members.Add(@{ Name = $member; Bytes = $bytes }) }
                    }
                }
                $members.Add(@{ Name = $member; Bytes = $bytes })
            }
        }
        if ($Fault -eq 'ForbiddenPdb') {
            $members.Add(@{ Name = 'WinApp.DevTools.Native.pdb'; Bytes = [byte[]]@(1) })
        }
        if ($Fault -eq 'ForbiddenSchema') {
            $members.Add(@{ Name = 'package/bin/win-x64/winapp-devtools-schema.json'; Bytes = [byte[]]@(1) })
        }
        $path = Join-Path $Root 'actual-package.archive'
        $file = [IO.File]::Create($path)
        try {
            if ($Format -eq 'Npm') {
                $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Compress, $true)
                $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
                try {
                    foreach ($member in $members) {
                        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $member.Name)
                        $entry.DataStream = [IO.MemoryStream]::new([byte[]]$member.Bytes)
                        try { $tar.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
                    }
                } finally { $tar.Dispose(); $gzip.Dispose() }
            } else {
                $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
                try {
                    foreach ($member in $members) {
                        $stream = $zip.CreateEntry($member.Name).Open()
                        try { $stream.Write($member.Bytes) } finally { $stream.Dispose() }
                    }
                } finally { $zip.Dispose() }
            }
        } finally { $file.Dispose() }
        @{ ArchivePath = $path; Format = $Format; CliBinariesPath = $inputRoot; Architecture = $Architecture }
    }

    function New-PackagingCopy([string]$Root, [string]$Script) {
        New-Item "$Root\scripts" -ItemType Directory -Force | Out-Null
        Copy-Item "$script:repo\scripts\DevToolsEngine.psm1" "$Root\scripts"
        $text = Get-Content "$script:repo\scripts\$Script" -Raw
        # Only the owned copy redirects CLI calls; a synthetic exe must never be executed.
        $text = $text.Replace('& $CliExe ', 'Invoke-ForbiddenCli ')
        [IO.File]::WriteAllText("$Root\scripts\$Script", $text)
        New-EngineInputs "$Root\artifacts\cli"
        New-Item "$Root\src\winapp-npm" -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllText("$Root\src\winapp-npm\package.json", '{"name":"test","version":"original"}')
    }
    function npm {
        $command = $args -join ' '
        Add-Content .external-actions "npm $command"
        if (-not (Test-Path .npm-mode)) { throw 'FORBIDDEN npm action reached' }
        $mode = Get-Content .npm-mode
        $global:LASTEXITCODE = 0
        if (($mode -eq 'CleanFail' -and $command -eq 'run clean') -or
            ($mode -eq 'CodegenFail' -and $command -like 'run generate-commands*') -or
            ($mode -eq 'CompileFail' -and $command -eq 'run compile') -or
            ($mode -eq 'PackFail' -and $args[0] -eq 'pack')) {
            $global:LASTEXITCODE = 19
            return
        }
        if ($command -eq 'run generate-commands') {
            foreach ($arch in 'x64', 'arm64') {
                foreach ($name in 'winapp.exe', 'WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll') {
                    if ((Get-FileHash "bin\win-$arch\$name").Hash -ne
                        (Get-FileHash "..\..\artifacts\cli\win-$arch\$name").Hash) {
                        throw "Stale generation input: $arch/$name"
                    }
                }
            }
        }
        if ($args[0] -eq 'pack') {
            (Get-Content package.json -Raw | ConvertFrom-Json).version | Set-Content .packed-version
            if ($mode -eq 'InvalidJson') { return 'not json' }
            if ($mode -eq 'MultipleResults') { return '[{"filename":"chosen.tgz"},{"filename":"newer.tgz"}]' }
            return '[{"filename":"chosen.tgz"}]'
        }
    }
    function dotnet {
        Add-Content .external-actions "dotnet $args"
        if (-not (Test-Path .dotnet-mode)) { throw 'FORBIDDEN dotnet action reached' }
        $global:LASTEXITCODE = 0
    }
    function Invoke-ForbiddenCli {
        Add-Content .external-actions "CLI $args"
        if (-not (Test-Path .cli-mode)) { throw 'FORBIDDEN CLI action reached' }
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'package') {
            if ($args -contains '--cert' -or $args -contains '--generate-cert' -or $args -contains '--install-cert') {
                throw 'FORBIDDEN certificate/signing action reached'
            }
            $mode = Get-Content .cli-mode
            if ($mode -eq 'MissingTool') { throw "Could not find 'makepri.exe' in the Windows SDK Build Tools." }
            if ($mode -eq 'PackageFail') {
                $global:LASTEXITCODE = 19
                return
            }
            $arch = Split-Path $args[1] -Leaf
            $outputIndex = [array]::IndexOf($args, '--output')
            if ($outputIndex -lt 0) { throw 'Missing controlled package output path' }
            Copy-Item "seed-$arch.msix" $args[$outputIndex + 1]
        } else {
            throw "FORBIDDEN CLI command reached: $args"
        }
    }
}

Describe 'Npm freshness and restoration with controlled stubs' -Tag 'DevToolsShipping' {
    It 'stops after <Mode> and preserves original package bytes' -ForEach @(
        @{ Mode = 'CleanFail'; Message = '*clean failed*'; Last = 'npm run clean' }
        @{ Mode = 'CodegenFail'; Message = '*generation failed*'; Last = 'npm run generate-commands' }
        @{ Mode = 'CompileFail'; Message = '*compilation failed*'; Last = 'npm run compile' }
        @{ Mode = 'PackFail'; Message = '*create npm package*'; Last = 'npm pack --json*' }
        @{ Mode = 'InvalidJson'; Message = '*JSON*'; Last = 'npm pack --json*' }
        @{ Mode = 'MultipleResults'; Message = '*exactly one*'; Last = 'npm pack --json*' }
        @{ Mode = 'BadArchive'; Message = '*WinApp.DevTools.Native.dll*'; Last = 'npm pack --json*' }
    ) {
        $root = Join-Path $TestDrive "npm-$Mode"
        New-PackagingCopy $root package-npm.ps1
        $npmRoot = "$root\src\winapp-npm"
        Set-Content "$npmRoot\.npm-mode" $Mode
        $a = New-EngineArchive "$root\archive" Npm x64 Missing
        Copy-Item $a.ArchivePath "$root\artifacts\chosen.tgz"
        $before = (Get-FileHash "$npmRoot\package.json").Hash
        $location = Get-Location
        { & "$root\scripts\package-npm.ps1" -Version 1.2.3 } | Should -Throw $Message
        @(Get-Content "$npmRoot\.external-actions")[-1] | Should -BeLike $Last
        (Get-FileHash "$npmRoot\package.json").Hash | Should -Be $before
        (Get-Location).Path | Should -Be $location.Path
        if ($Mode -in 'BadArchive', 'InvalidJson', 'MultipleResults') {
            Get-Content "$npmRoot\.packed-version" | Should -Be '1.2.3'
        }
    }
    It 'verifies the named tarball, not a newer stale archive' {
        $root = Join-Path $TestDrive 'npm-exact'
        New-PackagingCopy $root package-npm.ps1
        $npmRoot = "$root\src\winapp-npm"
        Set-Content "$npmRoot\.npm-mode" Success
        $a = New-EngineArchive "$root\archive" Npm x64
        Copy-Item $a.ArchivePath "$root\artifacts\chosen.tgz"
        [IO.File]::WriteAllText("$root\artifacts\newer.tgz", 'not a tarball')
        (Get-Item "$root\artifacts\newer.tgz").LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
        $before = (Get-FileHash "$npmRoot\package.json").Hash
        { & "$root\scripts\package-npm.ps1" -Version 1.2.3 } | Should -Not -Throw
        (Get-FileHash "$npmRoot\package.json").Hash | Should -Be $before
        Get-Content "$npmRoot\.packed-version" | Should -Be '1.2.3'
    }
    It 'restores bytes and location after a controlled copy failure' {
        $root = Join-Path $TestDrive 'npm-copy'
        New-PackagingCopy $root package-npm.ps1
        $npmRoot = "$root\src\winapp-npm"
        Set-Content "$npmRoot\.npm-mode" Success
        $before = (Get-FileHash "$npmRoot\package.json").Hash
        Mock Copy-Item { throw 'controlled copy failure' } -ParameterFilter { $Destination -like '*\bin\*' }
        $location = Get-Location
        { & "$root\scripts\package-npm.ps1" -Version 1.2.3 } | Should -Throw '*controlled copy failure*'
        (Get-FileHash "$npmRoot\package.json").Hash | Should -Be $before
        (Get-Location).Path | Should -Be $location.Path
        @(Get-Content "$npmRoot\.external-actions").Count | Should -Be 1
    }
    It 'guards archive API availability before cleaning or external actions' {
        $root = Join-Path $TestDrive 'npm-host'
        New-PackagingCopy $root package-npm.ps1
        $module = "$root\scripts\DevToolsEngine.psm1"
        $text = Get-Content $module -Raw
        $condition = '-not (''System.Formats.Tar.TarReader'' -as [type])'
        $text.Contains($condition) | Should -BeTrue
        [IO.File]::WriteAllText($module, $text.Replace($condition, '$true'))
        { & "$root\scripts\package-npm.ps1" -Version 1.2.3 } | Should -Throw '*PowerShell 7.3*'
        @(Get-ChildItem $root -Filter .external-actions -Recurse -Force).Count | Should -Be 0
    }
}

Describe 'Root and npm orchestration boundaries' -Tag 'DevToolsShipping' {
    It 'checks codegen failure before compiling in the actual root Node block' {
        $root = Join-Path $TestDrive 'root-node'
        New-PackagingCopy $root package-npm.ps1
        $npmRoot = "$root\src\winapp-npm"
        New-EngineInputs "$npmRoot\bin"
        Set-Content "$npmRoot\.npm-mode" CodegenFail
        $ast = [Management.Automation.Language.Parser]::ParseFile("$script:repo\scripts\build-cli.ps1", [ref]$null, [ref]$null)
        $block = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.StartsWith('if (-not $SkipTests -and ($RunCliTests -or $RunAuxiliaryTests))')
        }, $true)
        $block | Should -Not -BeNullOrEmpty
        $ProjectRoot = $root
        $SkipTests = $false
        $RunCliTests = $true
        $RunAuxiliaryTests = $false
        $TestSuite = 'Cli'
        $CliShard = 1
        $ArtifactsPath = 'artifacts'
        $CliProjectPath = 'src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj'
        $ErrorActionPreference = 'Stop'
        Mock dotnet { $global:LASTEXITCODE = 0; '{"commands":[]}' }
        { & ([scriptblock]::Create($block.Extent.Text)) } | Should -Throw '*command generation failed*'
        $actions = @(Get-Content "$npmRoot\.external-actions")
        $actions | Should -Contain 'npm run build-copy-only'
        $actions[-1] | Should -Match '^npm run generate-commands (?:-- )?--schema '
        $actions | Should -Not -Contain 'npm run compile'
    }
    It 'executes the actual serial engine stage with <Mode> stub outputs' -ForEach @(
        @{ Mode = 'Success' }; @{ Mode = 'BuildFail' }; @{ Mode = 'MissingTap' }
    ) {
        $ProjectRoot = Join-Path $TestDrive "root-engine-$Mode"
        $ArtifactsPath = 'artifacts'
        $SkipTests = $true
        New-Item "$ProjectRoot\src\winapp-devtools" -ItemType Directory -Force | Out-Null
        New-EngineInputs "$ProjectRoot\seed"
        New-Item "$ProjectRoot\artifacts\cli\win-x64", "$ProjectRoot\artifacts\cli\win-arm64" -ItemType Directory -Force | Out-Null
        Set-Content "$ProjectRoot\mode" $Mode
        @'
param($Configuration, $Arch, $EngineOut, [switch]$SkipNativeUnitTests)
$root = Split-Path (Split-Path $PSScriptRoot)
Add-Content "$root\build-order" "$Arch/$SkipNativeUnitTests"
if ((Get-Content "$root\mode") -eq 'BuildFail') { exit 19 }
New-Item $EngineOut -ItemType Directory -Force | Out-Null
Get-ChildItem "$root\seed\win-$Arch" -File | ForEach-Object {
    if ($_.Name -ne 'WinApp.DevTools.Native.dll' -or (Get-Content "$root\mode") -ne 'MissingTap') {
        Copy-Item $_.FullName $EngineOut
    }
}
exit 0
'@ | Set-Content "$ProjectRoot\src\winapp-devtools\build-devtools.ps1"
        Import-Module "$script:repo\scripts\DevToolsEngine.psm1" -Force
        $ast = [Management.Automation.Language.Parser]::ParseFile("$script:repo\scripts\build-cli.ps1", [ref]$null, [ref]$null)
        $loop = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.ForEachStatementAst] -and
            $node.Extent.Text.Contains('-SkipNativeUnitTests:$SkipTests')
        }, $true)
        $loop | Should -Not -BeNullOrEmpty
        if ($Mode -eq 'Success') {
            { & ([scriptblock]::Create($loop.Extent.Text)) } | Should -Not -Throw
            (Get-Content "$ProjectRoot\build-order") -join ',' | Should -Be 'x64/True,arm64/True'
            foreach ($arch in 'x64', 'arm64') {
                (Get-FileHash "$ProjectRoot\artifacts\cli\win-$arch\WinApp.DevTools.Native.dll").Hash |
                    Should -Be (Get-FileHash "$ProjectRoot\seed\win-$arch\WinApp.DevTools.Native.dll").Hash
            }
        } else {
            { & ([scriptblock]::Create($loop.Extent.Text)) } | Should -Throw
            @(Get-Content "$ProjectRoot\build-order").Count | Should -Be 1
        }
    }
}

Describe 'DevTools shipping payload' -Tag 'DevToolsShipping' {
    It 'owns exactly the two runtime DLLs' {
        (Get-DevToolsEnginePayload) -join ',' | Should -Be 'WinApp.DevTools.Native.dll,WinApp.DevTools.Managed.dll'
    }
    It 'rejects a zero-length runtime file' {
        $root = Join-Path $TestDrive 'empty'
        New-EngineInputs $root
        [IO.File]::WriteAllBytes("$root\win-x64\WinApp.DevTools.Native.dll", [byte[]]@())
        Get-MissingDevToolsEnginePayload "$root\win-x64" | Should -Contain 'WinApp.DevTools.Native.dll'
    }
    It 'reports the exact missing runtime path' {
        { Assert-DevToolsEnginePayload -Directory "$TestDrive\absent\win-x64" } |
            Should -Throw '*win-x64*WinApp.DevTools.Native.dll*'
    }
}

Describe 'DevTools actual archive contracts with synthetic bytes' -Tag 'DevToolsShipping' {
    It 'accepts <Format>/<Architecture> with the declared reference policy' -ForEach @(
        @{ Format = 'Npm'; Architecture = 'x64' }
        @{ Format = 'NuGet'; Architecture = 'x64' }
        @{ Format = 'Msix'; Architecture = 'x64' }
        @{ Format = 'Msix'; Architecture = 'arm64' }
        @{ Format = 'Portable'; Architecture = 'x64' }
        @{ Format = 'Portable'; Architecture = 'arm64' }
    ) {
        $a = New-EngineArchive "$TestDrive\positive-$Format-$Architecture" $Format $Architecture
        { Assert-DevToolsEngineArchive @a } | Should -Not -Throw
    }
    It 'rejects <Fault> in the archive despite complete staging (<Format>)' -ForEach @(
        foreach ($format in 'Npm', 'NuGet', 'Msix', 'Portable') {
            foreach ($fault in 'Missing', 'Empty', 'Stale', 'WrongArch', 'WrongPath', 'Duplicate') {
                @{ Format = $format; Fault = $fault }
            }
        }
    ) {
        $a = New-EngineArchive "$TestDrive\negative-$Format-$Fault" $Format x64 $Fault
        { Assert-DevToolsEngineArchive @a } | Should -Throw '*WinApp.DevTools.Native.dll*'
        Test-Path "$($a.CliBinariesPath)\win-x64\WinApp.DevTools.Native.dll" | Should -BeTrue
    }
    It 'rejects diagnostic symbols in <Format>' -ForEach @(
        @{ Format = 'Npm' }; @{ Format = 'NuGet' }; @{ Format = 'Msix' }
    ) {
        $a = New-EngineArchive "$TestDrive\pdb-$Format" $Format x64 ForbiddenPdb
        { Assert-DevToolsEngineArchive @a } | Should -Throw '*pdb*'
    }
    It 'does not add the standalone schema to npm' {
        $a = New-EngineArchive "$TestDrive\npm-schema" Npm x64 ForbiddenSchema
        { Assert-DevToolsEngineArchive @a } | Should -Throw '*winapp-devtools-schema.json*'
    }
}

Describe 'Package preflight before external actions' -Tag 'DevToolsShipping' {
    It '<Script> refuses missing <Arch>/<Name> before external actions' -ForEach @(
        foreach ($script in 'package-npm.ps1', 'package-nuget.ps1', 'package-msix.ps1') {
            foreach ($arch in 'x64', 'arm64') {
                foreach ($name in 'WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll') {
                    @{ Script = $script; Arch = $arch; Name = $name }
                }

            }
        }
    ) {
        $root = Join-Path $TestDrive "$Script-$Arch-$Name"
        New-PackagingCopy $root $Script
        Remove-Item -LiteralPath "$root\artifacts\cli\win-$Arch\$Name"
        { & "$root\scripts\$Script" -Version 1.2.3 } | Should -Throw "*$Name*"
        @(Get-ChildItem $root -Filter .external-actions -Recurse -Force).Count | Should -Be 0
        Test-Path "$root\src\winapp-NuGet\tools" | Should -BeFalse
        Test-Path "$root\artifacts\msix-layout" | Should -BeFalse
    }
}

Describe 'NuGet and MSIX package boundary archive failures' -Tag 'DevToolsShipping' {
    It 'refuses a broken NuGet archive after controlled pack, before any library build' {
        $root = Join-Path $TestDrive 'nuget-bad-archive'
        New-PackagingCopy $root package-nuget.ps1
        Set-Content "$root\.dotnet-mode" Success
        $a = New-EngineArchive "$root\archive" NuGet x64 Missing
        New-Item "$root\artifacts\nuget" -ItemType Directory -Force | Out-Null
        Copy-Item $a.ArchivePath "$root\artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.1.2.3.nupkg"
        { & "$root\scripts\package-nuget.ps1" -Version 1.2.3 } | Should -Throw '*WinApp.DevTools.Native.dll*'
        @(Get-Content "$root\.external-actions").Count | Should -Be 1
    }
    It 'keeps SkipCliPackage independent of all published files and preserves SkipBuild' {
        $root = Join-Path $TestDrive 'nuget-libraries'
        New-PackagingCopy $root package-nuget.ps1
        Remove-Item -LiteralPath "$root\artifacts\cli" -Recurse
        Set-Content "$root\.dotnet-mode" Success
        { & "$root\scripts\package-nuget.ps1" -Version 1.2.3 -SkipCliPackage -SkipBuild } | Should -Not -Throw
        $actions = @(Get-Content "$root\.external-actions")
        $actions.Count | Should -Be 3
        foreach ($action in $actions) {
            $action | Should -Match '--no-build'
            $action | Should -Not -Match 'src\\winapp-NuGet\\'
        }
        Test-Path "$root\src\winapp-NuGet\tools" | Should -BeFalse
    }
    It 'packages and validates both Stable MSIX archives without update or signing' {
        $root = Join-Path $TestDrive 'msix-stable-success'
        New-PackagingCopy $root package-msix.ps1
        Set-Content "$root\.cli-mode" Success
        New-Item "$root\msix\Assets" -ItemType Directory -Force | Out-Null
        Copy-Item "$script:repo\msix\appxmanifest.xml" "$root\msix\appxmanifest.xml"
        Copy-Item "$script:repo\scripts\msix-assets" "$root\scripts\msix-assets" -Recurse
        foreach ($arch in 'x64', 'arm64') {
            $a = New-EngineArchive "$root\archive-$arch" Msix $arch ''
            Copy-Item $a.ArchivePath "$root\seed-$arch.msix"
        }
        $output = & "$root\scripts\package-msix.ps1" -Version 1.2.3 -Stable 6>&1
        $actions = @(Get-Content "$root\.external-actions")
        $actions.Count | Should -Be 2
        $actions[0] | Should -Match '^CLI package .*\\msix-layout\\x64 --name '
        $actions[1] | Should -Match '^CLI package .*\\msix-layout\\arm64 --name '
        @($output | Where-Object { "$_" -match '^\[VALIDATE\] Msix archive: 4 matching' }).Count | Should -Be 2
        foreach ($arch in 'x64', 'arm64') {
            Test-Path "$root\artifacts\msix-packages\winappcli_1.2.3.0_$arch.msix" | Should -BeTrue
        }
        Test-Path "$root\artifacts\msix-packages\install.ps1" | Should -BeTrue
        Test-Path "$root\devcert.pfx" | Should -BeFalse
    }
    It 'stops Stable MSIX packaging on <Mode> before ARM64 or success' -ForEach @(
        @{ Mode = 'MissingTool'; Message = '*makepri.exe*' }
        @{ Mode = 'PackageFail'; Message = '*Failed to create x64 MSIX package*' }
    ) {
        $root = Join-Path $TestDrive "msix-$Mode"
        New-PackagingCopy $root package-msix.ps1
        Set-Content "$root\.cli-mode" $Mode
        New-Item "$root\msix\Assets" -ItemType Directory -Force | Out-Null
        Copy-Item "$script:repo\msix\appxmanifest.xml" "$root\msix\appxmanifest.xml"
        $output = [Collections.Generic.List[string]]::new()
        { & "$root\scripts\package-msix.ps1" -Version 1.2.3 -Stable 6>&1 |
            ForEach-Object { $output.Add("$_") } } | Should -Throw $Message
        $actions = @(Get-Content "$root\.external-actions")
        $actions.Count | Should -Be 1
        $actions[0] | Should -Match '^CLI package .*\\msix-layout\\x64 --name '
        @($output | Where-Object { $_ -match '\[SUCCESS\] MSIX (packages created|packaging complete)' }).Count | Should -Be 0
        @(Get-ChildItem "$root\artifacts\msix-packages" -Filter '*.msix').Count | Should -Be 0
        Test-Path "$root\artifacts\msix-packages\install.ps1" | Should -BeFalse
        Test-Path "$root\devcert.pfx" | Should -BeFalse
    }
    It 'rejects a broken <BadArch> MSIX from controlled package stubs' -ForEach @(
        @{ BadArch = 'x64'; Calls = 1 }; @{ BadArch = 'arm64'; Calls = 2 }
    ) {
        $root = Join-Path $TestDrive "msix-bad-$BadArch"
        New-PackagingCopy $root package-msix.ps1
        Set-Content "$root\.cli-mode" Success
        New-Item "$root\msix\Assets" -ItemType Directory -Force | Out-Null
        Copy-Item "$script:repo\msix\appxmanifest.xml" "$root\msix\appxmanifest.xml"
        foreach ($arch in 'x64', 'arm64') {
            $fault = if ($arch -eq $BadArch) { 'Missing' } else { '' }
            $a = New-EngineArchive "$root\archive-$arch" Msix $arch $fault
            Copy-Item $a.ArchivePath "$root\seed-$arch.msix"
        }
        { & "$root\scripts\package-msix.ps1" -Version 1.2.3 -Stable } | Should -Throw '*WinApp.DevTools.Native.dll*'
        @(Get-Content "$root\.external-actions").Count | Should -Be $Calls
        Test-Path "$root\devcert.pfx" | Should -BeFalse
    }
}

Describe 'Release-job inline portable archive gate' -Tag 'DevToolsShipping' {
    It 'checks actual ZIP bytes with <Fault> input' -ForEach @(
        @{ Fault = '' }; @{ Fault = 'Missing' }; @{ Fault = 'Stale' }; @{ Fault = 'Duplicate' }
    ) {
        $root = Join-Path $TestDrive "release-$Fault"
        New-EngineInputs "$root\cli-binaries"
        foreach ($arch in 'x64', 'arm64') {
            $a = New-EngineArchive "$root\archive-$arch" Portable $arch $Fault
            Copy-Item $a.ArchivePath "$root\winappcli-$arch.zip"
        }
        $yaml = Get-Content "$script:repo\.pipelines\release.yml" -Raw
        $match = [regex]::Match($yaml, '(?ms)displayName: Verify portable archive names and payloads.*?script: \|\r?\n(?<body>(?: {16}[^\r\n]*\r?\n)+)')
        $match.Success | Should -BeTrue
        $body = $match.Groups['body'].Value.Replace('$(Pipeline.Workspace)', $root)
        if ($Fault) {
            { & ([scriptblock]::Create($body)) } | Should -Throw '*WinApp.DevTools.Native.dll*'
        } else {
            { & ([scriptblock]::Create($body)) } | Should -Not -Throw
        }
    }
}
