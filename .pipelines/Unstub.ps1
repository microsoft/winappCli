param(
    [string]$TelemetryProviderGuid = ""
)

# This script unstubs the telemetry at build time and replaces the stubbed file with a reference internal nuget package

$ErrorActionPreference = "Stop"

if ($TelemetryProviderGuid -eq "") {
    Write-Error "TelemetryProviderGuid is required"
    exit 1
}

#
# Unstub managed telemetry
#

Remove-Item "$($PSScriptRoot)\..\src\winapp-CLI\WinApp.Cli\Telemetry\TelemetryEventSource.cs"

$projFile = "$($PSScriptRoot)\..\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj"
$projFileContent = Get-Content $projFile -Encoding UTF8 -Raw

$xml = [xml]$projFileContent
$xml.PreserveWhitespace = $true

$defineConstantsNode = $xml.SelectSingleNode("//DefineConstants")
if ($defineConstantsNode -ne $null) {
    $defineConstantsNode.ParentNode.RemoveChild($defineConstantsNode)
    $xml.Save($projFile)
}

if ($projFileContent.Contains('Microsoft.Telemetry.Inbox.Managed')) {
    Write-Output "Project file already contains a reference to the internal package."
    return;
}

$packageReferenceNode = $xml.CreateElement("PackageReference");
$packageReferenceNode.SetAttribute("Include", "Microsoft.Telemetry.Inbox.Managed")
$itemGroupNode = $xml.CreateElement("ItemGroup")
$itemGroupNode.AppendChild($packageReferenceNode)
$xml.DocumentElement.AppendChild($itemGroupNode)
$xml.Save($projFile)

$telemetryFile = Resolve-Path "$($PSScriptRoot)\..\src\winapp-CLI\WinApp.Cli\Telemetry\Telemetry.cs"
$oldLine = "EventSource TelemetryEventSourceInstance = new "
$newLine = "private static readonly EventSource TelemetryEventSourceInstance = new EventSource(ProviderName, EventSourceSettings.EtwManifestEventFormat, new[] {""ETW_GROUP"", ""{$TelemetryProviderGuid}"" });"

Write-Host "Modifying telemetry file: $telemetryFile"
Write-Host "Looking for line containing: $oldLine"
Write-Host "Replacing with: $newLine"

if (-not (Test-Path $telemetryFile)) {
    Write-Error "Telemetry file not found: $telemetryFile"
    exit 1
}

$telemetryContent = Get-Content $telemetryFile -Encoding UTF8
$lineFound = $false
$newContent = @()

foreach ($line in $telemetryContent) {
    if ($line -like "*$oldLine*") {
        $lineFound = $true
        $newContent += $newLine
        Write-Host "Found and replaced line"
    } else {
        $newContent += $line
    }
}

if (-not $lineFound) {
    Write-Error "Could not find line containing: $oldLine"
    Write-Error "Please verify the telemetry file format has not changed"
    exit 1
}

$newContent | Set-Content $telemetryFile -Encoding UTF8
Write-Host "Successfully updated telemetry file"
