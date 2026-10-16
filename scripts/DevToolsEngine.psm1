# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

function Get-DevToolsEnginePayload {
    @('WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll')
}

function Get-MissingDevToolsEnginePayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)
    @(Get-DevToolsEnginePayload | Where-Object {
        $path = Join-Path $Directory $_
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -ErrorAction Stop).Length -eq 0
    })
}

function Assert-DevToolsEnginePayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)
    $missing = @(Get-MissingDevToolsEnginePayload -Directory $Directory)
    if ($missing.Count) {
        throw "Missing or empty DevTools engine files: $(($missing | ForEach-Object { Join-Path $Directory $_ }) -join ', '). Run scripts\build-cli.ps1 to build the matching engines."
    }
}

function Assert-DevToolsArchiveSupport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('Npm', 'NuGet', 'Msix', 'Portable')][string]$Format)
    if ($Format -eq 'Npm' -and -not ('System.Formats.Tar.TarReader' -as [type])) {
        throw 'npm archive verification requires PowerShell 7.3 or newer with System.Formats.Tar. Use a supported pwsh host before packaging.'
    }
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    }
}

function Assert-DevToolsEngineArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][ValidateSet('Npm', 'NuGet', 'Msix', 'Portable')][string]$Format,
        [Parameter(Mandatory)][string]$CliBinariesPath,
        [ValidateSet('x64', 'arm64')][string]$Architecture
    )
    Assert-DevToolsArchiveSupport -Format $Format
    if ($Format -in 'Msix', 'Portable' -and -not $Architecture) {
        throw "Architecture is required for $Format archive verification."
    }
    $ArchivePath = (Get-Item -LiteralPath $ArchivePath -ErrorAction Stop).FullName
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $arches = if ($Format -in 'Npm', 'NuGet') { @('x64', 'arm64') } else { @($Architecture) }
    foreach ($arch in $arches) {
        $directory = Join-Path $CliBinariesPath "win-$arch"
        Assert-DevToolsEnginePayload -Directory $directory
        $prefix = switch ($Format) {
            Npm { "package/bin/win-$arch/" }
            NuGet { "tools/win-$arch/" }
            default { '' }
        }
        $names = @('winapp.exe') + @(Get-DevToolsEnginePayload)
        if ($Format -ne 'Npm') { $names += 'winapp-devtools-schema.json' }
        if ($Format -eq 'Portable') { $names += 'WinApp.DevTools.Native.pdb' }
        foreach ($name in $names) {
            $source = Join-Path $directory $name
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
                throw "Missing or empty archive input: $source"
            }
            $expected.Add("$prefix$name", (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash)
        }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    function Test-Entry([string]$Name, [long]$Length, [IO.Stream]$Stream) {
        if (-not $seen.Add($Name)) { throw "Duplicate archive entry '$Name' in $ArchivePath" }
        if ($Format -ne 'Portable' -and $Name.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unexpected PDB entry '$Name' in $ArchivePath"
        }
        if ($Format -eq 'Npm' -and ($Name -split '/')[-1] -ieq 'winapp-devtools-schema.json') {
            throw "Unexpected winapp-devtools-schema.json entry '$Name' in $ArchivePath"
        }
        if ($expected.ContainsKey($Name)) {
            if ($Length -le 0 -or $null -eq $Stream) { throw "Empty or non-file archive entry '$Name' in $ArchivePath" }
            if ((Get-FileHash -InputStream $Stream -Algorithm SHA256).Hash -ne $expected[$Name]) {
                throw "Archive entry '$Name' does not match its current CLI input: $ArchivePath"
            }
            $null = $verified.Add($Name)
        }
    }
    if ($Format -eq 'Npm') {
        $file = [IO.File]::OpenRead($ArchivePath)
        try {
            $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress, $true)
            try {
                $tar = [System.Formats.Tar.TarReader]::new($gzip, $true)
                try {
                    while ($entry = $tar.GetNextEntry()) {
                        Test-Entry $entry.Name $entry.Length $entry.DataStream
                    }
                } finally { $tar.Dispose() }
            } finally { $gzip.Dispose() }
        } finally { $file.Dispose() }
    } else {
        $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        try {
            foreach ($entry in $zip.Entries) {
                $stream = $entry.Open()
                try { Test-Entry $entry.FullName $entry.Length $stream } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
    foreach ($name in $expected.Keys) {
        if (-not $verified.Contains($name)) { throw "Missing archive entry '$name' in $ArchivePath" }
    }
    Write-Host "[VALIDATE] $Format archive: $($verified.Count) matching CLI/engine/reference files ($ArchivePath)"
}

Export-ModuleMember -Function Get-DevToolsEnginePayload, Get-MissingDevToolsEnginePayload,
    Assert-DevToolsEnginePayload, Assert-DevToolsArchiveSupport, Assert-DevToolsEngineArchive
