#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Asserts every shipped binary carries a valid Authenticode signature, including the binaries
    nested inside the MSIX, NuGet and npm containers the release publishes.

.DESCRIPTION
    Guardian's Code Sign Validation is the pipeline's normal signing gate, but it leaves two gaps
    that this script closes.

    It does not open every container. It expands .nupkg - the scan output shows gdn-*.nupkg
    folders - but a folder holding only .msix or .tgz yields zero targets, so the binaries users
    actually install are never inspected. And it runs only in the release jobs, over downloaded
    artifacts, in audit mode, so a regression surfaces as a warning on a job that still reports
    success.

    That combination already shipped a defect. Release 0.6.0 published
    Microsoft.Windows.SDK.BuildTools.WinApp.0.6.0.nupkg with an unsigned tools/win-x64/winapp.exe,
    because build-cli.ps1 packs the .nupkg before any signing runs. CSV logged it as a warning on
    a green build and the package went out.

    This runs on the build agent immediately after the ESRP steps and throws, so the same
    regression fails the build instead of shipping. .pipelines/templates/build.yaml gates it on
    DoEsrp, which is ANDed with the rel/v* branch - a weekly rehearsal on main signs nothing, so
    asserting signatures there would fail every Monday for no reason.

    Containers are expanded recursively, so a binary inside an .msix inside an .msixbundle is
    still reached. Only executable images are checked: an unsigned .ps1 inside a signed .msix or
    .nupkg is still covered by that container's signature, but a binary extracted from the
    portable winappcli-<arch>.zip carries no container signature at all and must stand on its own.

    For the same reason the signature has to be embedded. A catalog-signed binary reports Valid on
    whichever machine holds the matching .cat file and carries nothing once it is copied anywhere
    else, so catalog signatures are rejected rather than accepted.

.PARAMETER Path
    One or more files or directories to scan. Wildcards are supported, so the versioned npm
    tarball can be passed as artifacts\*.tgz. Every path must resolve, and every resolved root
    must contain at least one binary - an empty or mistyped root is a failure, not a pass.

.PARAMETER ExpectedSignerPattern
    Optional wildcard matched against each signer certificate's subject. Use it to assert the
    release was signed by the expected identity rather than merely signed by someone, for example
    '*O=Microsoft Corporation*'. Omitted by default because a valid signature is the contract this
    gate exists to enforce.

.EXAMPLE
    .\scripts\verify-signatures.ps1 -Path .\artifacts\cli

.EXAMPLE
    .\scripts\verify-signatures.ps1 -Path .\artifacts\cli,.\artifacts\msix-packages,.\artifacts\nuget,.\artifacts\*.tgz
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    [string]$ExpectedSignerPattern
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Executable images. Everything here is loaded or launched directly by a user or by Windows, so
# each one has to carry its own signature even when it also sits inside a signed container.
$BinaryExtensions = @('.exe', '.dll', '.sys', '.node')

# Archive formats the release ships binaries inside of. Expanded recursively.
$ContainerExtensions = @('.msix', '.msixbundle', '.appx', '.appxbundle', '.nupkg', '.zip', '.tgz')

# Windows PowerShell 5.1 does not load this by default; PowerShell 7 already has it.
if (-not ('System.IO.Compression.ZipFile' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

$script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sigverify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:ContainerCounter = 0

function Expand-Container {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    if ([System.IO.Path]::GetExtension($File).ToLowerInvariant() -eq '.tgz') {
        # bsdtar ships in System32 on Windows 10 1803+ and on every hosted ADO Windows image.
        & tar -xzf $File -C $Destination
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to expand '$File' (tar exit code $LASTEXITCODE)."
        }
    }
    else {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($File, $Destination)
    }
}

function Get-BinaryInventory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label,
        # Not Mandatory: the accumulator is empty on the first call, and Mandatory rejects an
        # empty collection.
        [AllowEmptyCollection()][System.Collections.Generic.List[object]]$Results
    )

    # .NET enumeration rather than Get-ChildItem: package payloads contain literal names like
    # [Content_Types].xml, and those brackets are wildcards to PowerShell's path parser.
    foreach ($file in [System.IO.Directory]::EnumerateFiles($Root, '*', [System.IO.SearchOption]::AllDirectories)) {
        $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
        $relative = $file.Substring($Root.Length).TrimStart('\', '/')
        $display = "$Label/$relative" -replace '\\', '/'

        if ($BinaryExtensions -contains $extension) {
            $Results.Add([pscustomobject]@{ Display = $display; FullName = $file })
        }
        elseif ($ContainerExtensions -contains $extension) {
            $script:ContainerCounter++
            $destination = Join-Path $script:TempRoot "c$($script:ContainerCounter)"
            Expand-Container -File $file -Destination $destination
            # '!' marks a container boundary, so a failure reads as package.msix!/winapp.exe.
            Get-BinaryInventory -Root $destination -Label "$display!" -Results $Results
        }
    }
}

$binaries = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[string]]::new()

try {
    foreach ($pattern in $Path) {
        $roots = @(Get-Item -Path $pattern -ErrorAction SilentlyContinue)
        if (-not $roots) {
            throw "Path '$pattern' did not match any file or directory."
        }

        foreach ($root in $roots) {
            $before = $binaries.Count

            if ($root.PSIsContainer) {
                Get-BinaryInventory -Root $root.FullName -Label $root.Name -Results $binaries
            }
            else {
                $extension = $root.Extension.ToLowerInvariant()
                if ($ContainerExtensions -contains $extension) {
                    $script:ContainerCounter++
                    $destination = Join-Path $script:TempRoot "c$($script:ContainerCounter)"
                    Expand-Container -File $root.FullName -Destination $destination
                    Get-BinaryInventory -Root $destination -Label "$($root.Name)!" -Results $binaries
                }
                elseif ($BinaryExtensions -contains $extension) {
                    $binaries.Add([pscustomobject]@{ Display = $root.Name; FullName = $root.FullName })
                }
                else {
                    throw "Path '$($root.FullName)' is neither a binary nor a supported container."
                }
            }

            # A root that yields nothing means a mistyped path, an empty artifact directory or a
            # packaging step that silently stopped producing output. Passing on zero binaries
            # would make this gate report success precisely when it has verified nothing.
            if ($binaries.Count -eq $before) {
                $errors.Add("Root '$($root.FullName)' contains no binaries to verify.")
            }
        }
    }

    Write-Host "[SIGN] Verifying $($binaries.Count) binaries..."

    foreach ($binary in $binaries) {
        $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName

        $subject = ''
        if ($signature.SignerCertificate) {
            $subject = $signature.SignerCertificate.Subject
        }

        $signer = '<unsigned>'
        if ($subject -match 'CN=([^,]+)') {
            $signer = $Matches[1].Trim()
        }

        if ($signature.Status -ne 'Valid') {
            $errors.Add("$($binary.Display): $($signature.Status)")
            Write-Host "[SIGN] FAIL  $($binary.Display) -> $($signature.Status)" -ForegroundColor Red
            continue
        }

        # Status alone is not enough. A catalog signature lives in a system .cat file, not in the
        # binary, so a catalog-signed file reports Valid on the machine holding that catalog and
        # carries nothing at all once a user extracts it from the portable zip somewhere else.
        # Accepting it would green-light precisely the unsigned-at-rest state this gate exists to
        # prevent, so require the signature to be embedded.
        if ($signature.SignatureType -ne 'Authenticode') {
            $errors.Add("$($binary.Display): signature is $($signature.SignatureType), not an embedded Authenticode signature.")
            Write-Host "[SIGN] FAIL  $($binary.Display) -> $($signature.SignatureType) signature, not embedded" -ForegroundColor Red
            continue
        }

        if ($ExpectedSignerPattern -and $subject -notlike $ExpectedSignerPattern) {
            $errors.Add("$($binary.Display): signed by '$subject', expected '$ExpectedSignerPattern'.")
            Write-Host "[SIGN] FAIL  $($binary.Display) -> unexpected signer '$signer'" -ForegroundColor Red
            continue
        }

        Write-Host "[SIGN] OK    $($binary.Display) -> $signer"
    }

    if ($errors.Count -gt 0) {
        Write-Host ''
        foreach ($message in $errors) {
            Write-Host "[SIGN] ERROR: $message" -ForegroundColor Red
        }
        # The detail goes in the exception too, not just the transcript: Azure DevOps surfaces the
        # terminating error as the step's failure line, and "failed for 3 item(s)" on its own sends
        # whoever is triaging back into the raw log.
        throw "Signature verification failed for $($errors.Count) item(s): $($errors -join '; ')"
    }

    Write-Host "[SIGN] All $($binaries.Count) binaries carry a valid Authenticode signature." -ForegroundColor Green
}
finally {
    if (Test-Path $script:TempRoot) {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
