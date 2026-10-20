#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [switch]$LayoutOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$assemblyPath = Join-Path $publishPath 'winapp-accessibility-worker.dll'
$executablePath = [System.IO.Path]::ChangeExtension($assemblyPath, '.exe')
$noticeFiles = @(
    'LICENSE.TXT',
    'THIRD-PARTY-NOTICES.TXT',
    'licenses\WinAppCLI\LICENSE.txt',
    'licenses\Axe.Windows\axe.windows.nuspec',
    'licenses\Microsoft.Win32.SystemEvents\microsoft.win32.systemevents.nuspec',
    'licenses\Microsoft.Win32.SystemEvents\LICENSE.TXT',
    'licenses\Microsoft.Win32.SystemEvents\THIRD-PARTY-NOTICES.TXT',
    'licenses\Newtonsoft.Json\newtonsoft.json.nuspec',
    'licenses\Newtonsoft.Json\LICENSE.md',
    'licenses\System.Drawing.Common\system.drawing.common.nuspec',
    'licenses\System.Drawing.Common\LICENSE.TXT',
    'licenses\System.Drawing.Common\THIRD-PARTY-NOTICES.TXT',
    'licenses\System.IO.Packaging\system.io.packaging.nuspec',
    'licenses\System.IO.Packaging\LICENSE.TXT',
    'licenses\System.IO.Packaging\THIRD-PARTY-NOTICES.TXT'
)
$requiredFiles = @(
    'winapp-accessibility-worker.exe',
    'winapp-accessibility-worker.dll',
    'winapp-accessibility-worker.deps.json',
    'winapp-accessibility-worker.runtimeconfig.json',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'clrjit.dll',
    'System.Private.CoreLib.dll',
    'Axe.Windows.Automation.dll',
    'Axe.Windows.Core.dll',
    'Axe.Windows.Desktop.dll',
    'Axe.Windows.Rules.dll'
) + $noticeFiles
foreach ($file in $requiredFiles) {
    $path = Join-Path $publishPath $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "The self-contained worker has a missing or empty $file."
    }
}
if ([System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Name -ne 'winapp-accessibility-worker') {
    throw 'The worker assembly name does not match its sibling Axe subprocess apphost.'
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'winapp-accessibility-worker.runtimeconfig.json') -Raw |
    ConvertFrom-Json -AsHashtable
$runtimeOptions = $runtimeConfig.runtimeOptions
if ($runtimeOptions.ContainsKey('framework') -or $runtimeOptions.ContainsKey('frameworks') -or
    -not $runtimeOptions.ContainsKey('includedFrameworks')) {
    throw 'The worker requires an external runtime instead of including its own.'
}
$deps = Get-Content -LiteralPath (Join-Path $publishPath 'winapp-accessibility-worker.deps.json') -Raw |
    ConvertFrom-Json -AsHashtable
if (-not $deps.libraries.ContainsKey('Axe.Windows/2.4.2')) {
    throw 'The worker must ship the real Axe.Windows 2.4.2 package.'
}
foreach ($package in $deps.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' }) {
    $id, $version = $package.Key -split '/'
    $nuspecPath = Join-Path $publishPath "licenses\$id\$($id.ToLowerInvariant()).nuspec"
    $nuspec = [System.Xml.Linq.XDocument]::Load($nuspecPath)
    $namespace = $nuspec.Root.GetDefaultNamespace()
    $metadata = $nuspec.Root.Element($namespace + 'metadata')
    if ($metadata.Element($namespace + 'id').Value -ne $id -or
        $metadata.Element($namespace + 'version').Value -ne $version -or
        $metadata.Element($namespace + 'license').Value -ne 'MIT') {
        throw "The published attribution does not match the bundled $($package.Key) package."
    }
    if ($id -eq 'Axe.Windows' -and
        ([string]::IsNullOrWhiteSpace($metadata.Element($namespace + 'copyright').Value) -or
         [string]::IsNullOrWhiteSpace($metadata.Element($namespace + 'authors').Value))) {
        throw 'The original Axe.Windows package copyright and author attribution must be preserved.'
    }
}
$runtimeIdentifier = ($deps.runtimeTarget.name -split '/')[-1]
$expectedMachine = switch ($runtimeIdentifier) {
    'win-x64' { 0x8664 }
    'win-arm64' { 0xAA64 }
    default { throw "Unexpected worker runtime identifier: $runtimeIdentifier." }
}
foreach ($file in @('winapp-accessibility-worker.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll')) {
    $stream = [System.IO.File]::OpenRead((Join-Path $publishPath $file))
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $expectedMachine) {
            throw "$file does not match $runtimeIdentifier."
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Invoke-IsolatedWorker {
    param(
        [string[]]$WorkerArguments,
        [switch]$TraceRuntime
    )

    $start = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
    $start.WorkingDirectory = $publishPath
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -like 'DOTNET_*' -or $name -like 'COREHOST_*') {
            $null = $start.Environment.Remove($name)
        }
    }
    # Only the child's environment changes; neither global dotnet nor Copilot is discoverable.
    $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    foreach ($directory in ($start.Environment['PATH'] -split ';')) {
        if (Test-Path -LiteralPath (Join-Path $directory 'dotnet.exe')) {
            throw 'The isolated child PATH unexpectedly contains dotnet.exe.'
        }
    }
    if ($TraceRuntime) {
        $start.Environment['DOTNET_HOST_TRACE'] = '1'
        $start.Environment['DOTNET_HOST_TRACE_VERBOSITY'] = '4'
    }
    foreach ($argument in $WorkerArguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'Could not start the published worker.'
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "The published worker timed out: $($WorkerArguments -join ' ')."
        }
        [pscustomobject]@{
            Arguments = $WorkerArguments
            ExitCode = $process.ExitCode
            Stdout = $stdout.GetAwaiter().GetResult()
            Stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

$checks = [System.Collections.Generic.List[object]]::new()
$runtimeTrace = @()
if (-not $LayoutOnly) {
    $help = Invoke-IsolatedWorker -WorkerArguments @('--help') -TraceRuntime
    if ($help.ExitCode -ne 0) {
        throw "Worker help failed ($($help.ExitCode)): $($help.Stderr)"
    }
    foreach ($verb in @('scan', 'survey', 'explore', 'journey', 'loop', 'check', 'repair', 'assessment-template', 'assess')) {
        if ($help.Stdout -notmatch "(?m)^\s*accessloop\s+$verb(?:\s|$)") {
            throw "The real worker help does not advertise $verb."
        }
    }
    foreach ($library in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
        if (-not $help.Stderr.Contains("Loaded library from $(Join-Path $publishPath $library)", [StringComparison]::OrdinalIgnoreCase)) {
            throw "The host trace does not confirm loading the bundled $library."
        }
    }
    if (-not $help.Stderr.Contains("App path: [$assemblyPath]", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The host is not using the assembly beside the Axe subprocess apphost.'
    }
    $runtimeTrace = @($help.Stderr -split '\r?\n' | Where-Object {
        $_.StartsWith('App path:') -or $_.StartsWith('Loaded library from ') -or
        $_.StartsWith('Executing as a self-contained app')
    })
    $checks.Add([pscustomobject]@{ Name = 'help-and-bundled-runtime'; ExitCode = $help.ExitCode })

    $missingInput = Join-Path $publishPath "missing-smoke-input-$([Guid]::NewGuid().ToString('N')).json"
    $cases = @(
        @{ Name = 'missing-scan-target'; Arguments = @('scan'); Error = 'Supply exactly one of --pid' },
        @{ Name = 'invalid-scan-pid'; Arguments = @('scan', '--pid', '0'); Error = '--pid must be 1..2147483647' },
        @{ Name = 'keyboard-approval-required'; Arguments = @('scan', '--pid', '1', '--tab-steps', '1'); Error = '--tab-steps requires --accept-input' },
        @{ Name = 'tab-navigation-approval-required'; Arguments = @('scan', '--pid', '1', '--navigation', 'standard-tabs'); Error = '--navigation standard-tabs requires --accept-navigation' },
        @{ Name = 'tab-budget-requires-navigation'; Arguments = @('scan', '--pid', '1', '--max-pages', '2'); Error = '--accept-navigation and --max-pages require --navigation standard-tabs' },
        @{ Name = 'page-navigation-not-keyboard-probe'; Arguments = @('scan', '--pid', '1', '--navigation', 'standard-tabs', '--accept-navigation', '--tab-steps', '1', '--accept-input'); Error = 'Run a separate keyboard probe' },
        @{ Name = 'missing-check-input'; Arguments = @('check', $missingInput); Error = 'Inconclusive:' },
        @{ Name = 'unknown-command'; Arguments = @('not-a-command'); Error = 'Use check <input.json> or repair <input.json>' },
        @{ Name = 'private-axe-missing-arguments'; Arguments = @('_axe-worker'); Error = 'Invalid internal Axe worker target' },
        @{ Name = 'private-axe-invalid-pid'; Arguments = @('_axe-worker', '0', '1', '1'); Error = 'Invalid internal Axe worker target' },
        # A nonexistent PID exercises private dispatch and process validation without touching any UI.
        @{ Name = 'private-axe-missing-process'; Arguments = @('_axe-worker', '2147483647', '1', '1'); Error = 'Inconclusive:' }
    )
    foreach ($case in $cases) {
        $result = Invoke-IsolatedWorker -WorkerArguments $case.Arguments
        if ($result.ExitCode -ne 2 -or -not $result.Stderr.Contains($case.Error, [StringComparison]::Ordinal) -or
            -not [string]::IsNullOrWhiteSpace($result.Stdout)) {
            throw "$($case.Name) did not preserve the real failure: exit=$($result.ExitCode); stdout=$($result.Stdout); stderr=$($result.Stderr)"
        }
        $checks.Add([pscustomobject]@{
            Name = $case.Name
            Arguments = $result.Arguments
            ExitCode = $result.ExitCode
            Stderr = $result.Stderr.Trim()
        })
    }
}

$files = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File)
[pscustomobject]@{
    PublishDirectory = $publishPath
    RuntimeIdentifier = $runtimeIdentifier
    RuntimeVersion = ($runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
    AxePackage = 'Axe.Windows/2.4.2'
    LayoutOnly = [bool]$LayoutOnly
    FileCount = $files.Count
    TotalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    AppHostBytes = (Get-Item -LiteralPath $executablePath).Length
    AssemblyBytes = (Get-Item -LiteralPath $assemblyPath).Length
    RedistributableNotices = $noticeFiles
    RuntimeTrace = $runtimeTrace
    Checks = $checks.ToArray()
}
