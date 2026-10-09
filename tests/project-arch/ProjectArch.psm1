Set-StrictMode -Version Latest

# Generates small .NET projects that cover how `winapp run` conveys the target architecture, and runs the
# real CLI against them. See README.md for the axes the fixtures cover.

$script:AssetsSource = Join-Path $PSScriptRoot '..\..\samples\winui-app\Assets'

$script:SdkMatrix = @{
    '1.6' = @{ WinAppSdk = '1.6.250205002'; BuildTools = '10.0.26100.1742'; Tfm = 'net8.0-windows10.0.22621.0' }
    '1.8' = @{ WinAppSdk = '1.8.260317003'; BuildTools = '10.0.26100.7175'; Tfm = 'net10.0-windows10.0.26100.0' }
    '2.5' = @{ WinAppSdk = '2.5.1'; BuildTools = '10.0.26100.7175'; Tfm = 'net10.0-windows10.0.26100.0' }
}

$script:AllPlatforms = 'x86;x64;ARM64'
$script:AllRids = 'win-x86;win-x64;win-arm64'

function New-AppSpec([hashtable]$Overrides = @{}) {
    $app = @{ Kind = 'winui'; Packaged = $true; Platforms = $script:AllPlatforms; Rids = $script:AllRids; Refs = @() }
    foreach ($key in $Overrides.Keys) { $app[$key] = $Overrides[$key] }
    $app
}

<#
.SYNOPSIS
    Returns the fixture catalog. Each fixture is a hashtable:
    Id, Sdk ('1.6'|'1.8'|'2.5'), Why (what it guards), App (kind/packaging/Platforms/RIDs/refs/extra
    properties), Libs (referenced projects), and optional PlatformsInProps, Sln, PubXml, Arch (overrides
    the suite's -Architecture, for cross-architecture builds), and ExpectRid
    (the project can't honor a Platform-only build, so winapp must pass -r win-<arch>).
#>
function Get-ProjectArchFixtures {
    $msixLib = @{ EnableMsixTooling = 'true' }
    @(
        # App shapes
        @{ Id = 'A01-packaged'; Sdk = '1.8'; App = (New-AppSpec); Why = 'Packaged WinUI app' }
        @{ Id = 'A01-packaged-wasdk16'; Sdk = '1.6'; App = (New-AppSpec); Why = 'Packaged WinUI app on WinAppSDK 1.6' }
        @{ Id = 'A01-packaged-wasdk25'; Sdk = '2.5'; App = (New-AppSpec); Why = 'Packaged WinUI app on WinAppSDK 2.x' }
        @{ Id = 'A02-no-platforms'; Sdk = '1.8'; App = (New-AppSpec @{ Platforms = $null }); Why = 'App without <Platforms>' }
        @{ Id = 'A02-no-platforms-wasdk16'; Sdk = '1.6'; App = (New-AppSpec @{ Platforms = $null }); Why = 'App without <Platforms> on WinAppSDK 1.6' }
        @{ Id = 'A03-platforms-in-props'; Sdk = '1.8'; PlatformsInProps = $true; App = (New-AppSpec @{ Platforms = $null; Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = '<Platforms> from Directory.Build.props' }
        @{ Id = 'A04-wasdk-selfcontained'; Sdk = '1.8'; App = (New-AppSpec @{ SelfContained = $true }); Why = 'WindowsAppSDKSelfContained' }
        @{ Id = 'A04-wasdk-selfcontained-16'; Sdk = '1.6'; App = (New-AppSpec @{ SelfContained = $true }); Why = 'WindowsAppSDKSelfContained on 1.6 (rejects Platform=AnyCPU)' }
        @{ Id = 'A05-unpackaged'; Sdk = '1.8'; App = (New-AppSpec @{ Packaged = $false }); Why = 'Unpackaged WinUI app' }
        @{ Id = 'A05-unpackaged-wasdk16'; Sdk = '1.6'; App = (New-AppSpec @{ Packaged = $false }); Why = 'Unpackaged WinUI app on 1.6' }
        @{ Id = 'A06-wpf-plain-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Kind = 'wpf'; Packaged = $false; Platforms = $null; Rids = $null; Refs = @('LibP') }); Libs = @(@{ Name = 'LibP'; Kind = 'plain' }); Why = 'WPF app without WinAppSDK' }
        @{ Id = 'A07-wpf-wasdk'; Sdk = '1.8'; App = (New-AppSpec @{ Kind = 'wpf'; Packaged = $false; WinAppSdk = $true; Platforms = $null; Rids = $null }); Why = 'WPF app using WinAppSDK' }
        @{ Id = 'A08-console-wasdk'; Sdk = '1.8'; App = (New-AppSpec @{ Kind = 'console'; Packaged = $false; WinAppSdk = $true; Platforms = $null; Rids = $null }); Why = 'Console app using WinAppSDK' }

        # Reference graphs
        @{ Id = 'R01-plain-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibP') }); Libs = @(@{ Name = 'LibP'; Kind = 'plain' }); Why = 'netstandard2.0 reference' }
        @{ Id = 'R02-winui-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'WinUI library without <Platforms>' }
        @{ Id = 'R02-winui-lib-wasdk16'; Sdk = '1.6'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'RID-agnostic WinUI library with MSIX tooling (1.6): built twice under a global RID' }
        @{ Id = 'R02-winui-lib-wasdk25'; Sdk = '2.5'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'WinUI library on 2.x' }
        @{ Id = 'R03-winui-lib-platforms'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU;x64;x86;ARM64' }); Why = 'WinUI library with <Platforms>' }
        @{ Id = 'R03-winui-lib-platforms-wasdk16'; Sdk = '1.6'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU;x64;x86;ARM64' }); Why = 'FluentStore shape: library with <Platforms> and MSIX tooling' }
        @{ Id = 'R04-winui-lib-rids'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Rids = $script:AllRids }); Why = 'WinUI library with <RuntimeIdentifiers>' }
        @{ Id = 'R05-winui-lib-rids-platforms'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Rids = $script:AllRids; Platforms = $script:AllPlatforms }); Why = 'WinUI library with both' }
        @{ Id = 'R06-multitarget-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibM') }); Libs = @(@{ Name = 'LibM'; Kind = 'multi'; Platforms = 'AnyCPU;x64;x86;ARM64' }); Why = 'Multi-targeted reference' }
        @{ Id = 'R07-diamond'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW', 'LibP') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Refs = @('LibP') }, @{ Name = 'LibP'; Kind = 'plain' }); Why = 'Diamond reference graph' }
        @{ Id = 'R08-globalpropertiestoremove'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @(@{ Name = 'LibW'; Meta = @{ GlobalPropertiesToRemove = 'RuntimeIdentifier' } }) }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Platforms = $script:AllPlatforms }); Why = 'Reference that removes RuntimeIdentifier' }
        @{ Id = 'R09-anycpu-only-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU' }); Why = 'Library declaring only AnyCPU' }
        @{ Id = 'R10-fluentstore'; Sdk = '1.6'; App = (New-AppSpec @{ Refs = @('LibW', 'LibR') }); Libs = @(
                @{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU;x64;x86;ARM64'; Refs = @('LibP') },
                @{ Name = 'LibR'; Kind = 'winui'; Platforms = $script:AllPlatforms; Rids = $script:AllRids; Refs = @('LibP') },
                @{ Name = 'LibP'; Kind = 'multi'; Platforms = 'AnyCPU;x64;x86;ARM64' }); Why = 'FluentStore graph: mixed RID-agnostic and RID-specific WinUI libraries' }
        @{ Id = 'R11-unpackaged-winui-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Packaged = $false; Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'Unpackaged app with WinUI library' }
        @{ Id = 'R12-solution'; Sdk = '1.8'; Sln = $true; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'App inside a solution' }
        @{ Id = 'R13-selfcontained-winui-lib'; Sdk = '1.8'; App = (New-AppSpec @{ SelfContained = $true; Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'Self-contained app with WinUI library' }
        @{ Id = 'R14-msix-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Extra = $msixLib }); Why = 'RID-agnostic library with MSIX tooling (1.8)' }
        @{ Id = 'R14-msix-lib-platforms'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU;x64;x86;ARM64'; Extra = $msixLib }); Why = 'Library with <Platforms> and MSIX tooling (1.8)' }
        @{ Id = 'R14-msix-lib-wasdk25'; Sdk = '2.5'; App = (New-AppSpec @{ Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Extra = $msixLib }); Why = 'Library with MSIX tooling (2.x)' }
        @{ Id = 'R15-unpackaged-msix-lib'; Sdk = '1.8'; App = (New-AppSpec @{ Packaged = $false; Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Extra = $msixLib }); Why = 'Unpackaged app with MSIX-tooling library' }
        @{ Id = 'R15-unpackaged-msix-lib-wasdk16'; Sdk = '1.6'; App = (New-AppSpec @{ Packaged = $false; Refs = @('LibW') }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'Unpackaged app with MSIX-tooling library (1.6)' }
        @{ Id = 'R16-dynamic-platform-resolution'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @('LibW'); Extra = @{ EnableDynamicPlatformResolution = 'true' } }); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Rids = $script:AllRids; Extra = @{ EnableDynamicPlatformResolution = 'true' } }); ExpectRid = $true; Why = 'EnableDynamicPlatformResolution (needs the RID)' }
        @{ Id = 'R17-edpr-rid-split'; Sdk = '1.8'; App = (New-AppSpec @{ Refs = @(@{ Name = 'LibW'; Meta = @{ GlobalPropertiesToRemove = 'RuntimeIdentifier' } }, 'LibP'); Extra = @{ EnableDynamicPlatformResolution = 'true' } }); Libs = @(
                @{ Name = 'LibW'; Kind = 'winui'; Platforms = $script:AllPlatforms; Refs = @('LibP') },
                @{ Name = 'LibP'; Kind = 'plain'; Platforms = $script:AllPlatforms }); Why = 'EnableDynamicPlatformResolution with a reference that removes RuntimeIdentifier (must not get the RID back)' }

        # Project settings that interact with Platform/RID
        @{ Id = 'P01-platforms-lack-arch'; Sdk = '1.8'; App = (New-AppSpec @{ Platforms = 'x86;x64' }); Why = '<Platforms> without the target arch' }
        @{ Id = 'P02-net-selfcontained'; Sdk = '1.8'; App = (New-AppSpec @{ Extra = @{ SelfContained = 'true' } }); Why = '.NET SelfContained without a RID' }
        @{ Id = 'P03-publishaot-build'; Sdk = '1.8'; App = (New-AppSpec @{ Extra = @{ PublishAot = 'true' } }); Why = 'PublishAot project built without --aot' }
        @{ Id = 'P04-hardcoded-rid'; Sdk = '1.8'; App = (New-AppSpec @{ Extra = @{ RuntimeIdentifier = 'win-x86' } }); ExpectRid = $true; Why = 'Hard-coded RuntimeIdentifier for another arch (needs the RID)' }
        @{ Id = 'P05-rid-from-platform'; Sdk = '1.8'; App = (New-AppSpec @{ Platforms = 'X86;X64;ARM64'; Extra = @{ RuntimeIdentifier = 'win-$(Platform)' } }); ExpectRid = $true; Why = 'RuntimeIdentifier=win-$(Platform) with upper-case platforms (needs the RID)' }
        @{ Id = 'P06-publish-profiles'; Sdk = '1.8'; PubXml = @{ Name = 'win-{0}.pubxml'; Template = $true }; App = (New-AppSpec @{ Refs = @('LibW'); Extra = @{ PublishProfile = 'win-$(Platform).pubxml' } }); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'Visual Studio template publish profiles' }
        @{ Id = 'P06-publish-profiles-wasdk16'; Sdk = '1.6'; PubXml = @{ Name = 'win10-{0}.pubxml'; Template = $true }; App = (New-AppSpec @{ Extra = @{ PublishProfile = 'win10-$(Platform).pubxml' } }); Why = 'WinAppSDK 1.6 template publish profiles' }
        @{ Id = 'P07-unpackaged-selfcontained'; Sdk = '1.8'; App = (New-AppSpec @{ Packaged = $false; Extra = @{ SelfContained = 'true'; WindowsAppSDKSelfContained = 'true' } }); Why = 'Unpackaged fully self-contained app' }
        @{ Id = 'P08-trimmed-profile-anycpu-lib'; Sdk = '1.8'; PubXml = @{ Name = 'debug-{0}.pubxml'; Template = $false }; App = (New-AppSpec @{ Refs = @('LibP'); Extra = @{ PublishTrimmed = 'true'; PublishProfile = 'debug-$(Platform).pubxml' } }); Libs = @(@{ Name = 'LibP'; Kind = 'plain' }); Why = 'Trimmed build whose $(Platform) profile makes it self-contained' }

        # Cross-architecture builds (Arch differs from x64 and arm64 hosts; x86 runs on both)
        @{ Id = 'X01-generator-cross-arch'; Sdk = '1.8'; Arch = 'x86'; ExpectRid = $true; App = (New-AppSpec @{ Kind = 'console'; Packaged = $false; Platforms = $null; Rids = $null; Refs = @(@{ Name = 'Gen'; Meta = @{ OutputItemType = 'Analyzer'; ReferenceOutputAssembly = 'false' } }) }); Libs = @(@{ Name = 'Gen'; Kind = 'generator' }); Why = 'Project-referenced source generator built for another architecture (the compiler must still load it)' }
    )
}

function Write-FixtureFile([string]$Path, [string]$Text) {
    $directory = Split-Path $Path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text)
}

function Format-Properties($properties) {
    ($properties.GetEnumerator() | Where-Object { $null -ne $_.Value } |
        ForEach-Object { "    <$($_.Name)>$($_.Value)</$($_.Name)>" }) -join "`n"
}

function Format-References($references) {
    if (-not $references) { return '' }
    $items = foreach ($reference in $references) {
        if ($reference -is [string]) { $reference = @{ Name = $reference } }
        $metadata = ''
        if ($reference.ContainsKey('Meta')) {
            $metadata = ($reference.Meta.GetEnumerator() | ForEach-Object { " $($_.Name)=`"$($_.Value)`"" }) -join ''
        }
        "    <ProjectReference Include=`"..\$($reference.Name)\$($reference.Name).csproj`"$metadata />"
    }
    "  <ItemGroup>`n$($items -join "`n")`n  </ItemGroup>"
}

function Format-Packages($sdk, [bool]$include) {
    if (-not $include) { return '' }
    @"
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="$($sdk.BuildTools)" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="$($sdk.WinAppSdk)" />
  </ItemGroup>
"@
}

function Get-FixtureIdentityName([hashtable]$Fixture) {
    "projectarch.$($Fixture.Id)".ToLowerInvariant() -replace '[^a-z0-9.\-]', '-'
}

<#
.SYNOPSIS
    Writes a fixture to $Root: App\App.csproj plus any referenced libraries.
#>
function New-ProjectArchFixture([hashtable]$Fixture, [string]$Root) {
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $sdk = $script:SdkMatrix[$Fixture.Sdk]
    $app = $Fixture.App

    # Isolate the fixture from any Directory.Build.* above the work root.
    $props = '<Project><PropertyGroup><Nullable>disable</Nullable><NoWarn>$(NoWarn);NU1603;NU1701</NoWarn>'
    if ($Fixture.ContainsKey('PlatformsInProps') -and $Fixture.PlatformsInProps) { $props += "<Platforms>$script:AllPlatforms</Platforms>" }
    $props += '</PropertyGroup></Project>'
    Write-FixtureFile "$Root\Directory.Build.props" $props
    Write-FixtureFile "$Root\Directory.Build.targets" '<Project />'

    foreach ($library in @($Fixture['Libs'])) {
        if (-not $library) { continue }
        $name = $library.Name
        $properties = [ordered]@{ RootNamespace = $name; Platforms = $library['Platforms']; RuntimeIdentifiers = $library['Rids'] }
        $winui = $library.Kind -eq 'winui'
        switch ($library.Kind) {
            'plain' { $properties.TargetFramework = 'netstandard2.0' }
            'generator' {
                $properties.TargetFramework = 'netstandard2.0'
                $properties.IsRoslynComponent = 'true'
                $properties.EnforceExtendedAnalyzerRules = 'true'
                $properties.LangVersion = 'latest'
            }
            'multi' { $properties.TargetFrameworks = "netstandard2.0;$($sdk.Tfm)" }
            'winui' {
                $properties.TargetFramework = $sdk.Tfm
                $properties.TargetPlatformMinVersion = '10.0.17763.0'
                $properties.UseWinUI = 'true'
                $properties.WinUISDKReferences = 'false'
                # WinAppSDK 1.6 WinUI libraries only build under the dotnet CLI with MSIX tooling enabled.
                if ($Fixture.Sdk -eq '1.6') { $properties.EnableMsixTooling = 'true' }
            }
        }
        if ($library.ContainsKey('Extra')) { foreach ($key in $library.Extra.Keys) { $properties[$key] = $library.Extra[$key] } }
        $generatorPackages = if ($library.Kind -eq 'generator') {
            "  <ItemGroup>`n    <PackageReference Include=`"Microsoft.CodeAnalysis.CSharp`" Version=`"4.8.0`" PrivateAssets=`"all`" />`n  </ItemGroup>"
        }
        else { '' }
        Write-FixtureFile "$Root\$name\$name.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
$(Format-Properties $properties)
  </PropertyGroup>
$(Format-Packages $sdk $winui)
$generatorPackages
$(Format-References $library['Refs'])
</Project>
"@
        if ($library.Kind -eq 'generator') {
            Write-FixtureFile "$Root\$name\Generator.cs" @"
using Microsoft.CodeAnalysis;

namespace $name
{
    [Generator]
    public sealed class Generator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterPostInitializationOutput(output => output.AddSource(
                "Generated.g.cs",
                "namespace FixtureApp { internal static class Generated { public const string Value = \"ProjectArch\"; } }"));
    }
}
"@
            continue
        }
        Write-FixtureFile "$Root\$name\Class1.cs" "namespace $name { public static class Class1 { public static string Hello() { return `"$name`"; } } }"
        if ($winui) {
            Write-FixtureFile "$Root\$name\Control1.xaml" @"
<UserControl x:Class="$name.Control1" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TextBlock Text="$name" />
</UserControl>
"@
            Write-FixtureFile "$Root\$name\Control1.xaml.cs" "namespace $name { public sealed partial class Control1 : Microsoft.UI.Xaml.Controls.UserControl { public Control1() { InitializeComponent(); } } }"
        }
    }

    $winAppSdk = $app.Kind -eq 'winui' -or ($app.ContainsKey('WinAppSdk') -and $app.WinAppSdk)
    $properties = [ordered]@{
        OutputType = if ($app.Kind -eq 'console') { 'Exe' } else { 'WinExe' }
        RootNamespace = 'FixtureApp'
        AssemblyName = 'FixtureApp'
        Platforms = $app['Platforms']
        RuntimeIdentifiers = $app['Rids']
        TargetFramework = $sdk.Tfm
    }
    switch ($app.Kind) {
        'winui' {
            $properties.TargetPlatformMinVersion = '10.0.17763.0'
            $properties.UseWinUI = 'true'
            $properties.WinUISDKReferences = 'false'
            $properties.ApplicationManifest = 'app.manifest'
        }
        'wpf' { $properties.UseWPF = 'true' }
    }
    if ($winAppSdk -and ($app.Packaged -or ($Fixture.Sdk -eq '1.6' -and $app.Kind -eq 'winui'))) { $properties.EnableMsixTooling = 'true' }
    if ($winAppSdk -and -not $app.Packaged) { $properties.WindowsPackageType = 'None' }
    if ($app.ContainsKey('SelfContained') -and $app.SelfContained) { $properties.WindowsAppSDKSelfContained = 'true' }
    if ($app.ContainsKey('Extra')) { foreach ($key in $app.Extra.Keys) { $properties[$key] = $app.Extra[$key] } }

    $packaging = ''
    if ($app.Packaged) {
        Copy-Item $script:AssetsSource "$Root\App\Assets" -Recurse
        $packaging = "  <ItemGroup>`n    <Content Include=`"Assets\**`" />`n  </ItemGroup>"
        if ($winAppSdk) { $packaging += "`n  <ItemGroup>`n    <ProjectCapability Include=`"Msix`" />`n  </ItemGroup>" }
        Write-FixtureFile "$Root\App\Package.appxmanifest" @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="$(Get-FixtureIdentityName $Fixture)" Publisher="CN=ProjectArch" Version="1.0.0.0" />
  <Properties><DisplayName>ProjectArch</DisplayName><PublisherDisplayName>ProjectArch</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26226.0" /></Dependencies>
  <Resources><Resource Language="x-generate"/></Resources>
  <Applications>
    <Application Id="App" Executable="`$targetnametoken`$.exe" EntryPoint="`$targetentrypoint`$">
      <uap:VisualElements DisplayName="ProjectArch" Description="ProjectArch" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
    }

    Write-FixtureFile "$Root\App\App.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
$(Format-Properties $properties)
  </PropertyGroup>
$packaging
$(Format-Packages $sdk $winAppSdk)
$(Format-References $app['Refs'])
</Project>
"@

    switch ($app.Kind) {
        'winui' {
            Write-FixtureFile "$Root\App\app.manifest" @"
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"><assemblyIdentity version="1.0.0.0" name="FixtureApp.app"/></assembly>
"@
            Write-FixtureFile "$Root\App\App.xaml" @"
<Application x:Class="FixtureApp.App" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
"@
            Write-FixtureFile "$Root\App\App.xaml.cs" @"
using Microsoft.UI.Xaml;
namespace FixtureApp
{
    public partial class App : Application
    {
        private Window _window;
        public App() { InitializeComponent(); }
        protected override void OnLaunched(LaunchActivatedEventArgs args) { _window = new MainWindow(); _window.Activate(); }
    }
}
"@
            Write-FixtureFile "$Root\App\MainWindow.xaml" @"
<Window x:Class="FixtureApp.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TextBlock Text="ProjectArch" />
</Window>
"@
            Write-FixtureFile "$Root\App\MainWindow.xaml.cs" "namespace FixtureApp { public sealed partial class MainWindow : Microsoft.UI.Xaml.Window { public MainWindow() { InitializeComponent(); } } }"
        }
        'wpf' {
            Write-FixtureFile "$Root\App\Program.cs" "namespace FixtureApp { public static class Program { [System.STAThread] public static void Main() { new System.Windows.Application().Run(new System.Windows.Window { Title = `"ProjectArch`" }); } } }"
        }
        'console' {
            # A console app that references a source generator prints the generated value, so the build fails
            # (CS0103) when the compiler can't load the generator.
            $value = if (@($Fixture['Libs'] | Where-Object { $_ -and $_.Kind -eq 'generator' }).Count -gt 0) { 'Generated.Value' } else { '"ProjectArch"' }
            Write-FixtureFile "$Root\App\Program.cs" "namespace FixtureApp { public static class Program { public static void Main() { System.Console.WriteLine($value); } } }"
        }
    }

    if ($Fixture.ContainsKey('PubXml')) {
        foreach ($profileArch in 'x86', 'x64', 'arm64') {
            $publishSettings = if ($Fixture.PubXml.Template) {
                @"
    <PublishSingleFile>False</PublishSingleFile>
    <PublishReadyToRun Condition="'`$(Configuration)' == 'Debug'">False</PublishReadyToRun>
    <PublishReadyToRun Condition="'`$(Configuration)' != 'Debug'">True</PublishReadyToRun>
    <PublishTrimmed Condition="'`$(Configuration)' == 'Debug'">False</PublishTrimmed>
    <PublishTrimmed Condition="'`$(Configuration)' != 'Debug'">True</PublishTrimmed>

"@
            }
            else { '' }
            Write-FixtureFile "$Root\App\Properties\PublishProfiles\$($Fixture.PubXml.Name -f $profileArch)" @"
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <PublishProtocol>FileSystem</PublishProtocol>
    <Platform>$profileArch</Platform>
    <RuntimeIdentifier>win-$profileArch</RuntimeIdentifier>
    <PublishDir>bin\`$(Configuration)\`$(TargetFramework)\`$(RuntimeIdentifier)\publish\</PublishDir>
    <SelfContained>true</SelfContained>
$publishSettings  </PropertyGroup>
</Project>
"@
        }
    }

    if ($Fixture.ContainsKey('Sln') -and $Fixture.Sln) {
        $projects = @('App\App.csproj') + @($Fixture['Libs'] | Where-Object { $_ } | ForEach-Object { "$($_.Name)\$($_.Name).csproj" })
        & dotnet new sln -n Fixture --format sln -o $Root 2>&1 | Out-Null
        & dotnet sln "$Root\Fixture.sln" add @($projects | ForEach-Object { Join-Path $Root $_ }) 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create the solution for fixture $($Fixture.Id)." }
    }
}

<#
.SYNOPSIS
    Returns the PE machine of an executable: arm64, x64, x86, other, or $null when missing.
#>
function Get-PeArchitecture([string]$Path) {
    if (-not $Path -or -not (Test-Path $Path)) { return $null }
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32() + 4
        switch ($reader.ReadUInt16()) { 0xAA64 { 'arm64' } 0x8664 { 'x64' } 0x14c { 'x86' } default { 'other' } }
    }
    finally { $stream.Dispose() }
}

<#
.SYNOPSIS
    True when this machine can run an app built for $Architecture.
#>
function Test-CanLaunchArchitecture([string]$Architecture) {
    $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    switch ($hostArch) {
        'arm64' { $true }
        'x64' { $Architecture -in 'x64', 'x86' }
        default { $Architecture -eq $hostArch }
    }
}

<#
.SYNOPSIS
    Runs `winapp run` for a generated fixture with a hard timeout, so a hang fails one fixture instead of
    the whole job. Launches the app (--detach) when this machine can run the architecture.
#>
function Invoke-ProjectArchRun {
    param(
        [Parameter(Mandatory)][hashtable]$Fixture,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Winapp,
        [Parameter(Mandatory)][string]$Architecture,
        [int]$TimeoutMinutes = 15
    )

    $launch = Test-CanLaunchArchitecture $Architecture
    $project = Join-Path $Root 'App\App.csproj'
    $mode = if ($launch) { '--detach' } else { '--no-launch' }
    $logPath = Join-Path $Root 'winapp-run.log'
    $errorPath = Join-Path $Root 'winapp-run.err.log'

    $process = Start-Process -FilePath $Winapp -ArgumentList @('run', "`"$project`"", $mode, '--arch', $Architecture, '--json') `
        -WorkingDirectory $Root -NoNewWindow -PassThru -RedirectStandardOutput $logPath -RedirectStandardError $errorPath
    $null = $process.Handle
    $timedOut = -not $process.WaitForExit($TimeoutMinutes * 60 * 1000)
    if ($timedOut) {
        & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null
    }
    else {
        $process.WaitForExit()
    }

    $stdout = [string](Get-Content $logPath -Raw -ErrorAction SilentlyContinue)
    $stderr = [string](Get-Content $errorPath -Raw -ErrorAction SilentlyContinue)
    $processId = $null
    $json = [regex]::Match($stdout, '\{[^{}]*"ProcessId"\s*:\s*(\d+)[^{}]*\}')
    if ($json.Success) { $processId = [int]$json.Groups[1].Value }

    [pscustomobject]@{
        ExitCode = if ($timedOut) { $null } else { $process.ExitCode }
        TimedOut = $timedOut
        Launched = $launch
        ProcessId = $processId
        Output = "$stdout`n$stderr"
    }
}

<#
.SYNOPSIS
    Stops the launched app and removes the fixture's development package registration.
#>
function Remove-ProjectArchRun([hashtable]$Fixture, $Run) {
    if ($Run -and $Run.ProcessId) { Stop-Process -Id $Run.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-AppxPackage -Name (Get-FixtureIdentityName $Fixture) -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue
}

Export-ModuleMember -Function Get-ProjectArchFixtures, New-ProjectArchFixture, Get-PeArchitecture, Test-CanLaunchArchitecture, Invoke-ProjectArchRun, Remove-ProjectArchRun, Get-FixtureIdentityName
