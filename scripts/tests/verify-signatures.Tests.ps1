#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/verify-signatures.ps1.

    This gate exists because Guardian's Code Sign Validation does not open .msix or .tgz, and runs
    in audit mode over downloaded artifacts after the build has already reported success. Release
    0.6.0 shipped an unsigned winapp.exe inside the CLI tools NuGet package that way.

    The cases that matter most here are the ones CSV cannot express: a binary nested inside a
    container, and a root that yields nothing at all. A gate that silently passes on zero inputs
    is worse than no gate, because it reports success precisely when it has verified nothing.

    Offline only, like every other suite under scripts/tests - these run during a real release
    build, so a test that reached the network would let an outage block a release.
#>

BeforeDiscovery {
    # The fixture probe is deliberately repeated in BeforeAll below, and it has to be.
    # Pester evaluates -Skip: during discovery, which runs before BeforeAll - but neither
    # BeforeDiscovery variables nor file-body definitions survive into the run phase, where the
    # resolved paths are actually needed. Discovery therefore resolves the skip flags, and the run
    # phase resolves the paths.
    #
    # The "good" fixture must carry an EMBEDDED Authenticode signature, not a catalog one.
    # Catalog-signed system files like notepad.exe and kernel32.dll report Valid but are exactly
    # what the verifier rejects, so using one here would assert the opposite of the contract.
    function Find-SignedFixture {
        param([string[]]$Candidate, [string]$Type)

        foreach ($path in $Candidate) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($signature.Status -eq 'Valid' -and $signature.SignatureType -eq $Type) { return $path }
        }
        return $null
    }

    $embeddedCandidates = @(
        (Join-Path $PSHOME 'pwsh.exe'),
        (Join-Path $PSHOME 'powershell.exe'),
        "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe")

    # Probed separately because a machine with no intact catalog store would report NotSigned, and
    # the catalog tests would then pass for the wrong reason.
    $catalogCandidates = @(
        "$env:SystemRoot\System32\notepad.exe",
        "$env:SystemRoot\System32\kernel32.dll",
        "$env:SystemRoot\System32\where.exe")

    $script:NoSignedBinary = -not (Find-SignedFixture -Candidate $embeddedCandidates -Type 'Authenticode')
    $script:NoCatalogBinary = -not (Find-SignedFixture -Candidate $catalogCandidates -Type 'Catalog')
}

BeforeAll {
    $script:VerifyScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'verify-signatures.ps1'

    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }

    # See BeforeDiscovery: this repeats that probe because discovery-phase state does not reach
    # the run phase.
    function Find-SignedFixture {
        param([string[]]$Candidate, [string]$Type)

        foreach ($path in $Candidate) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($signature.Status -eq 'Valid' -and $signature.SignatureType -eq $Type) { return $path }
        }
        return $null
    }

    $script:SignedBinary = Find-SignedFixture -Type 'Authenticode' -Candidate @(
        (Join-Path $PSHOME 'pwsh.exe'),
        (Join-Path $PSHOME 'powershell.exe'),
        "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe")

    $script:CatalogBinary = Find-SignedFixture -Type 'Catalog' -Candidate @(
        "$env:SystemRoot\System32\notepad.exe",
        "$env:SystemRoot\System32\kernel32.dll",
        "$env:SystemRoot\System32\where.exe")

    function New-FixtureRoot {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("sigtest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        return $root
    }

    function Add-SignedBinary {
        param([string]$Directory, [string]$Name = 'signed.exe')

        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        Copy-Item -LiteralPath $script:SignedBinary -Destination (Join-Path $Directory $Name) -Force
    }

    function Add-UnsignedBinary {
        param([string]$Directory, [string]$Name = 'unsigned.exe')

        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $Directory $Name), [byte[]](1..64))
    }

    function Add-CatalogSignedBinary {
        param([string]$Directory, [string]$Name = 'catalog.exe')

        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        Copy-Item -LiteralPath $script:CatalogBinary -Destination (Join-Path $Directory $Name) -Force
    }

    function New-ZipContainer {
        param([string]$SourceDirectory, [string]$Destination)

        New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
        if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($SourceDirectory, $Destination)
    }
}

Describe 'verify-signatures.ps1' -Skip:$script:NoSignedBinary {

    AfterEach {
        if ($script:root -and (Test-Path -LiteralPath $script:root)) {
            Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'loose binaries' {

        It 'passes when every binary carries a valid signature' {
            $script:root = New-FixtureRoot
            Add-SignedBinary -Directory (Join-Path $script:root 'cli')

            { & $script:VerifyScript -Path (Join-Path $script:root 'cli') } | Should -Not -Throw
        }

        It 'fails when a binary is unsigned' {
            $script:root = New-FixtureRoot
            Add-SignedBinary -Directory (Join-Path $script:root 'cli')
            Add-UnsignedBinary -Directory (Join-Path $script:root 'cli')

            { & $script:VerifyScript -Path (Join-Path $script:root 'cli') } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'fails when a signed binary has been modified after signing' {
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-SignedBinary -Directory $dir
            $target = Join-Path $dir 'signed.exe'
            $bytes = [System.IO.File]::ReadAllBytes($target)
            [System.IO.File]::WriteAllBytes($target, $bytes + [byte[]](1..16))

            { & $script:VerifyScript -Path $dir } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'ignores files that are not executable images' {
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-SignedBinary -Directory $dir
            # Unsigned, and deliberately so: inside a signed container these are covered by the
            # container signature, and the release does not sign them individually.
            Set-Content -LiteralPath (Join-Path $dir 'install.ps1') -Value 'Write-Host hi'
            Set-Content -LiteralPath (Join-Path $dir 'AppxManifest.xml') -Value '<Package/>'

            { & $script:VerifyScript -Path $dir } | Should -Not -Throw
        }
    }

    Context 'guard against verifying nothing' {

        It 'fails when a root contains no binaries at all' {
            $script:root = New-FixtureRoot
            New-Item -ItemType Directory -Path (Join-Path $script:root 'empty') -Force | Out-Null

            { & $script:VerifyScript -Path (Join-Path $script:root 'empty') } |
                Should -Throw -ExpectedMessage '*no binaries to verify*'
        }

        It 'fails when one of several roots is empty, even though the others pass' {
            $script:root = New-FixtureRoot
            Add-SignedBinary -Directory (Join-Path $script:root 'cli')
            New-Item -ItemType Directory -Path (Join-Path $script:root 'msix') -Force | Out-Null

            { & $script:VerifyScript -Path (Join-Path $script:root 'cli'), (Join-Path $script:root 'msix') } |
                Should -Throw -ExpectedMessage '*no binaries to verify*'
        }

        It 'fails when a path matches nothing, rather than passing vacuously' {
            $script:root = New-FixtureRoot

            { & $script:VerifyScript -Path (Join-Path $script:root 'does-not-exist') } |
                Should -Throw -ExpectedMessage '*did not match any file or directory*'
        }
    }

    Context 'containers' {

        It 'expands a .nupkg and verifies the binaries inside it' {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'layout'
            Add-UnsignedBinary -Directory (Join-Path $layout 'tools\win-x64') -Name 'winapp.exe'
            New-ZipContainer -SourceDirectory $layout -Destination (Join-Path $script:root 'nuget\BuildTools.WinApp.nupkg')

            { & $script:VerifyScript -Path (Join-Path $script:root 'nuget') } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'expands a .msix, which Guardian CodeSign Validation does not' {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'layout'
            Add-UnsignedBinary -Directory $layout -Name 'winapp.exe'
            New-ZipContainer -SourceDirectory $layout -Destination (Join-Path $script:root 'msix\winappcli_x64.msix')

            { & $script:VerifyScript -Path (Join-Path $script:root 'msix') } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'expands an npm .tgz, which Guardian CodeSign Validation does not' {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'package\bin\win-x64'
            Add-UnsignedBinary -Directory $layout -Name 'winapp.exe'
            $tgz = Join-Path $script:root 'npm\microsoft-winappcli-0.0.1.tgz'
            New-Item -ItemType Directory -Path (Split-Path $tgz -Parent) -Force | Out-Null
            & tar -czf $tgz -C (Join-Path $script:root 'package') 'bin'
            $LASTEXITCODE | Should -Be 0

            { & $script:VerifyScript -Path (Join-Path $script:root 'npm') } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'expands containers nested inside other containers' {
            $script:root = New-FixtureRoot
            $inner = Join-Path $script:root 'inner'
            Add-UnsignedBinary -Directory $inner -Name 'winapp.exe'
            $outerLayout = Join-Path $script:root 'outer'
            New-Item -ItemType Directory -Path $outerLayout -Force | Out-Null
            New-ZipContainer -SourceDirectory $inner -Destination (Join-Path $outerLayout 'winappcli_x64.msix')
            New-ZipContainer -SourceDirectory $outerLayout -Destination (Join-Path $script:root 'dist\winappcli.msixbundle')

            { & $script:VerifyScript -Path (Join-Path $script:root 'dist') } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }

        It 'passes a container whose binaries are all validly signed' {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'layout'
            Add-SignedBinary -Directory (Join-Path $layout 'tools\win-x64') -Name 'winapp.exe'
            New-ZipContainer -SourceDirectory $layout -Destination (Join-Path $script:root 'nuget\BuildTools.WinApp.nupkg')

            { & $script:VerifyScript -Path (Join-Path $script:root 'nuget') } | Should -Not -Throw
        }

        It 'handles the literal [Content_Types].xml entry every OPC package carries' {
            # The brackets are wildcards to PowerShell's path parser, so enumerating a package
            # payload with Get-ChildItem would silently skip or throw on this entry.
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'layout'
            Add-SignedBinary -Directory (Join-Path $layout 'tools\win-x64') -Name 'winapp.exe'
            [System.IO.File]::WriteAllText((Join-Path $layout '[Content_Types].xml'), '<Types/>')
            New-ZipContainer -SourceDirectory $layout -Destination (Join-Path $script:root 'nuget\BuildTools.WinApp.nupkg')

            { & $script:VerifyScript -Path (Join-Path $script:root 'nuget') } | Should -Not -Throw
        }

        It 'accepts a wildcard path, so the versioned npm tarball can be named as artifacts\*.tgz' {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'package\bin'
            Add-SignedBinary -Directory $layout -Name 'winapp.exe'
            & tar -czf (Join-Path $script:root 'microsoft-winappcli-0.0.1.tgz') -C (Join-Path $script:root 'package') 'bin'
            $LASTEXITCODE | Should -Be 0

            { & $script:VerifyScript -Path (Join-Path $script:root '*.tgz') } | Should -Not -Throw
        }
    }

    Context 'embedded signatures' {

        It 'rejects a catalog-signed binary, which carries no signature once it is copied elsewhere' -Skip:$script:NoCatalogBinary {
            # Get-AuthenticodeSignature reports Valid for these because this machine holds the
            # matching catalog. Extracted from the portable zip on a user's machine, the same file
            # has nothing to validate against - so accepting it would defeat the whole gate.
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-CatalogSignedBinary -Directory $dir

            { & $script:VerifyScript -Path $dir } |
                Should -Throw -ExpectedMessage '*not an embedded Authenticode signature*'
        }

        It 'rejects a catalog-signed binary nested inside a container' -Skip:$script:NoCatalogBinary {
            $script:root = New-FixtureRoot
            $layout = Join-Path $script:root 'layout'
            Add-CatalogSignedBinary -Directory (Join-Path $layout 'tools\win-x64') -Name 'winapp.exe'
            New-ZipContainer -SourceDirectory $layout -Destination (Join-Path $script:root 'nuget\BuildTools.WinApp.nupkg')

            { & $script:VerifyScript -Path (Join-Path $script:root 'nuget') } |
                Should -Throw -ExpectedMessage '*not an embedded Authenticode signature*'
        }

        It 'fails a catalog-signed binary even when an embedded-signed one passes alongside it' -Skip:$script:NoCatalogBinary {
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-SignedBinary -Directory $dir
            Add-CatalogSignedBinary -Directory $dir

            { & $script:VerifyScript -Path $dir } |
                Should -Throw -ExpectedMessage '*not an embedded Authenticode signature*'
        }
    }

    Context 'signer identity' {

        It 'passes when the signer matches the expected pattern' {
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-SignedBinary -Directory $dir

            { & $script:VerifyScript -Path $dir -ExpectedSignerPattern '*O=Microsoft Corporation*' } |
                Should -Not -Throw
        }

        It 'fails a validly signed binary whose signer is not the expected one' {
            $script:root = New-FixtureRoot
            $dir = Join-Path $script:root 'cli'
            Add-SignedBinary -Directory $dir

            { & $script:VerifyScript -Path $dir -ExpectedSignerPattern '*O=Contoso Ltd*' } |
                Should -Throw -ExpectedMessage '*Signature verification failed*'
        }
    }
}
