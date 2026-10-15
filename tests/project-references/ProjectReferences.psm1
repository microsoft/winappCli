Set-StrictMode -Version Latest

# Generates small packaged WinUI apps with project references, for the tests in ProjectReferences.Tests.ps1.

$script:AssetsSource = Join-Path $PSScriptRoot '..\..\samples\winui-app\Assets'

$script:SdkMatrix = @{
    '1.6' = @{ WinAppSdk = '1.6.250205002'; BuildTools = '10.0.26100.1742'; Tfm = 'net8.0-windows10.0.22621.0' }
    '1.8' = @{ WinAppSdk = '1.8.260317003'; BuildTools = '10.0.26100.7175'; Tfm = 'net10.0-windows10.0.26100.0' }
    '2.5' = @{ WinAppSdk = '2.5.1'; BuildTools = '10.0.26100.7175'; Tfm = 'net10.0-windows10.0.26100.0' }
}

<#
.SYNOPSIS
    Returns the test cases. Each is a hashtable: Id, Sdk ('1.6'|'1.8'|'2.5'), Why, Refs (the app's project
    references), Libs (referenced projects: Name, Kind 'winui'|'plain'|'multi', optional Platforms, Rids, Refs,
    Extra properties and raw Xml), optional Files (extra files by relative path), and Pack (also check
    `winapp package` on a clean copy).
#>
function Get-ProjectReferenceCases {
    $msixLib = @{ EnableMsixTooling = 'true' }
    @(
        @{ Id = 'msix-lib'; Sdk = '1.8'; Pack = $true; Refs = @('LibW'); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Extra = $msixLib }); Why = 'WinUI library with MSIX tooling' }
        @{ Id = 'msix-lib-wasdk25'; Sdk = '2.5'; Refs = @('LibW'); Libs = @(@{ Name = 'LibW'; Kind = 'winui'; Extra = $msixLib }); Why = 'WinUI library with MSIX tooling on Windows App SDK 2.x' }
        @{ Id = 'winui-lib-wasdk16'; Sdk = '1.6'; Refs = @('LibW'); Libs = @(@{ Name = 'LibW'; Kind = 'winui' }); Why = 'WinUI library on Windows App SDK 1.6' }
        @{ Id = 'fluentstore'; Sdk = '1.6'; Pack = $true; Refs = @('LibW', 'LibR'); Libs = @(
                @{ Name = 'LibW'; Kind = 'winui'; Platforms = 'AnyCPU;x64;x86;ARM64'; Refs = @('LibP') },
                @{ Name = 'LibR'; Kind = 'winui'; Platforms = 'x86;x64;ARM64'; Rids = 'win-x86;win-x64;win-arm64'; Refs = @('LibP') },
                @{ Name = 'LibP'; Kind = 'multi'; Platforms = 'AnyCPU;x64;x86;ARM64' }); Why = 'FluentStore graph: libraries with and without RuntimeIdentifiers' }
        @{ Id = 'lib-own-hook'; Sdk = '1.8'; Refs = @('LibP'); Libs = @(@{ Name = 'LibP'; Kind = 'plain'; Xml = '  <Import Project="..\eng\hooks.props" />' }); Files = @{
                'eng\hooks.props' = '<Project><PropertyGroup><CustomAfterDirectoryBuildTargets>$(MSBuildThisFileDirectory)gen.targets</CustomAfterDirectoryBuildTargets></PropertyGroup></Project>'
                'eng\gen.targets' = @'
<Project>
  <Target Name="FixtureGenerate" BeforeTargets="CoreCompile">
    <WriteLinesToFile File="$(IntermediateOutputPath)Gen.g.cs" Lines="namespace LibP { internal static class Gen { public const string Value = &quot;generated&quot;%3B } }" Overwrite="true" />
    <ItemGroup><Compile Include="$(IntermediateOutputPath)Gen.g.cs" /></ItemGroup>
  </Target>
</Project>
'@
                'LibP\UsesGen.cs' = 'namespace LibP { public static class UsesGen { public static string Value() { return Gen.Value; } } }'
            }; Why = 'Library whose own import sets CustomAfterDirectoryBuildTargets (its targets must still run)' }
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

function Format-References([string[]]$Names) {
    if (-not $Names) { return '' }
    $items = $Names | ForEach-Object { "    <ProjectReference Include=`"..\$_\$_.csproj`" />" }
    "  <ItemGroup>`n$($items -join "`n")`n  </ItemGroup>"
}

function Format-Packages($sdk) {
    @"
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="$($sdk.BuildTools)" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="$($sdk.WinAppSdk)" />
  </ItemGroup>
"@
}

function Get-CaseIdentityName([hashtable]$Case) {
    "projectreferences.$($Case.Id)".ToLowerInvariant()
}

<#
.SYNOPSIS
    Writes a case to $Root: a packaged WinUI app (App\App.csproj) plus its referenced projects.
#>
function New-ProjectReferenceFixture([hashtable]$Case, [string]$Root) {
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $sdk = $script:SdkMatrix[$Case.Sdk]

    # Isolate the fixture from any Directory.Build.* above it.
    Write-FixtureFile "$Root\Directory.Build.props" '<Project><PropertyGroup><Nullable>disable</Nullable><NoWarn>$(NoWarn);NU1603;NU1701</NoWarn></PropertyGroup></Project>'
    Write-FixtureFile "$Root\Directory.Build.targets" '<Project />'

    foreach ($library in @($Case['Libs'])) {
        $name = $library.Name
        $properties = [ordered]@{ RootNamespace = $name; Platforms = $library['Platforms']; RuntimeIdentifiers = $library['Rids'] }
        $winui = $library.Kind -eq 'winui'
        switch ($library.Kind) {
            'plain' { $properties.TargetFramework = 'netstandard2.0' }
            'multi' { $properties.TargetFrameworks = "netstandard2.0;$($sdk.Tfm)" }
            'winui' {
                $properties.TargetFramework = $sdk.Tfm
                $properties.TargetPlatformMinVersion = '10.0.17763.0'
                $properties.UseWinUI = 'true'
                $properties.WinUISDKReferences = 'false'
                # Windows App SDK 1.6 WinUI libraries only build under the dotnet CLI with MSIX tooling enabled.
                if ($Case.Sdk -eq '1.6') { $properties.EnableMsixTooling = 'true' }
            }
        }
        if ($library.ContainsKey('Extra')) { foreach ($key in $library.Extra.Keys) { $properties[$key] = $library.Extra[$key] } }
        Write-FixtureFile "$Root\$name\$name.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
$(Format-Properties $properties)
  </PropertyGroup>
$(if ($winui) { Format-Packages $sdk })
$(Format-References $library['Refs'])
$($library['Xml'])
</Project>
"@
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

    $properties = [ordered]@{
        OutputType = 'WinExe'
        RootNamespace = 'FixtureApp'
        AssemblyName = 'FixtureApp'
        Platforms = 'x86;x64;ARM64'
        RuntimeIdentifiers = 'win-x86;win-x64;win-arm64'
        TargetFramework = $sdk.Tfm
        TargetPlatformMinVersion = '10.0.17763.0'
        UseWinUI = 'true'
        WinUISDKReferences = 'false'
        ApplicationManifest = 'app.manifest'
        EnableMsixTooling = 'true'
    }
    Copy-Item $script:AssetsSource "$Root\App\Assets" -Recurse
    Write-FixtureFile "$Root\App\App.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
$(Format-Properties $properties)
  </PropertyGroup>
  <ItemGroup>
    <Content Include="Assets\**" />
    <ProjectCapability Include="Msix" />
  </ItemGroup>
$(Format-Packages $sdk)
$(Format-References $Case['Refs'])
</Project>
"@
    Write-FixtureFile "$Root\App\Package.appxmanifest" @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="$(Get-CaseIdentityName $Case)" Publisher="CN=ProjectReferences" Version="1.0.0.0" />
  <Properties><DisplayName>ProjectReferences</DisplayName><PublisherDisplayName>ProjectReferences</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26226.0" /></Dependencies>
  <Resources><Resource Language="x-generate"/></Resources>
  <Applications>
    <Application Id="App" Executable="`$targetnametoken`$.exe" EntryPoint="`$targetentrypoint`$">
      <uap:VisualElements DisplayName="ProjectReferences" Description="ProjectReferences" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
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
  <TextBlock Text="ProjectReferences" />
</Window>
"@
    Write-FixtureFile "$Root\App\MainWindow.xaml.cs" "namespace FixtureApp { public sealed partial class MainWindow : Microsoft.UI.Xaml.Window { public MainWindow() { InitializeComponent(); } } }"

    if ($Case.ContainsKey('Files')) {
        foreach ($relative in $Case.Files.Keys) { Write-FixtureFile (Join-Path $Root $relative) $Case.Files[$relative] }
    }
}

<#
.SYNOPSIS
    Runs winapp with a hard timeout, so a hang fails one test instead of the whole job. Returns the exit code
    (null on timeout) and the combined output.
#>
function Invoke-WinappWithTimeout([string]$Winapp, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMinutes = 15) {
    $log = Join-Path $WorkingDirectory "winapp-$([guid]::NewGuid().ToString('N')).log"
    $errors = "$log.err"
    $quoted = $Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
    $process = Start-Process -FilePath $Winapp -ArgumentList $quoted -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errors
    $null = $process.Handle
    $exited = $process.WaitForExit($TimeoutMinutes * 60 * 1000)
    if (-not $exited) { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null }
    else { $process.WaitForExit() }
    [pscustomobject]@{
        ExitCode = if ($exited) { $process.ExitCode } else { $null }
        Output = "$([string](Get-Content $log -Raw -ErrorAction SilentlyContinue))`n$([string](Get-Content $errors -Raw -ErrorAction SilentlyContinue))"
    }
}

function Remove-CaseRegistration([hashtable]$Case) {
    Get-AppxPackage -Name (Get-CaseIdentityName $Case) -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue
}

Export-ModuleMember -Function Get-ProjectReferenceCases, New-ProjectReferenceFixture, Invoke-WinappWithTimeout, Remove-CaseRegistration
