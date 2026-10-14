# Generates journeys\real-compiler-new-types.json: new types while the app runs, on the real-compiler path.
#   1 a new UserControl, used on a page    2 a new Page, navigated to (and back)
#   3 a templated control + Themes\Generic.xaml    4 a NuGet package with controls (SettingsCard)
#   5 a project reference to a class library with a UserControl and a templated control
$ns = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:TodoBench"'
function Lines($t) { , @(($t -replace "`r`n", "`n").TrimEnd("`n") -split "`n") }
function Step($id, $title, $file, $edit, $checks, $expect) {
    $a = [ordered]@{ id = $id; title = $title; file = $file; edit = $edit }
    if ($checks) { $a.checkpoint = [ordered]@{ id = "c-$id"; kind = 'live'; uiChecks = @($checks) } }
    if ($expect) { $a.expect = $expect }
    $a
}
function Create($id, $title, $file, $text, $checks = $null, $expect = $null) { Step $id $title $file ([ordered]@{ op = 'create-file'; text = (Lines $text) }) $checks $expect }
function Replace($id, $title, $file, $find, $text, $checks = $null, $expect = $null) { Step $id $title $file ([ordered]@{ op = 'replace'; find = (Lines $find); text = (Lines $text) }) $checks $expect }
function C($assert, $target, $value = $null) { $c = [ordered]@{ target = $target; assert = $assert }; if ($null -ne $value) { $c.value = $value }; $c }
function Expect($outcome, $detail = $null) { $e = [ordered]@{ outcome = $outcome }; if ($detail) { $e.detail = $detail }; $e }

$window = @"
<?xml version="1.0" encoding="utf-8" ?>
<Window x:Class="TodoBench.MainWindow" $ns Title="TodoBench">
    <Frame x:Name="Root" />
</Window>
"@
$windowCs = @'
using Microsoft.UI.Xaml;

namespace TodoBench;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Root.Navigate(typeof(MainPage));
    }
}
'@
$page = @"
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="TodoBench.MainPage" $ns>
    <StackPanel x:Name="Panel" Spacing="6" Margin="16">
        <TextBlock x:Name="Status" AutomationProperties.AutomationId="Status" Text="ready" />
        <Button x:Name="NavBtn" AutomationProperties.AutomationId="NavBtn" Content="Navigate" Click="Nav_Click" />
        <TextBlock AutomationProperties.AutomationId="Plain" Text="plain v1" />
    </StackPanel>
</Page>
"@
$pageCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => Status.Text = "nav: none";
}
'@
$badge = @"
<?xml version="1.0" encoding="utf-8" ?>
<UserControl x:Class="TodoBench.Badge" $ns>
    <StackPanel>
        <TextBlock AutomationProperties.AutomationId="BadgeText" Text="{x:Bind Label, Mode=OneWay}" />
    </StackPanel>
</UserControl>
"@
$badgeCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class Badge : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(Badge), new PropertyMetadata(""));

    public Badge()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
'@
$second = @"
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="TodoBench.SecondPage" $ns>
    <StackPanel Spacing="6" Margin="16">
        <TextBlock AutomationProperties.AutomationId="SecondTitle" Text="second v1" />
        <Button AutomationProperties.AutomationId="BackBtn" Content="Back" Click="Back_Click" />
    </StackPanel>
</Page>
"@
$secondCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class SecondPage : Page
{
    public SecondPage()
    {
        InitializeComponent();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Frame.GoBack();
}
'@
$pill = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed class Pill : Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(Pill), new PropertyMetadata(""));

    public Pill()
    {
        DefaultStyleKey = typeof(Pill);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
'@
$generic = @"
<?xml version="1.0" encoding="utf-8" ?>
<ResourceDictionary $ns>
    <Style TargetType="local:Pill">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="local:Pill">
                    <Border Padding="6" CornerRadius="12" BorderThickness="1" BorderBrush="Gray">
                        <StackPanel Orientation="Horizontal" Spacing="4">
                            <TextBlock AutomationProperties.AutomationId="PillLabel" Text="{TemplateBinding Label}" />
                        </StackPanel>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
"@
$sideProj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <Platforms>x86;x64;ARM64</Platforms>
    <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI>
    <WinUISDKReferences>false</WinUISDKReferences>
    <EnableMsixTooling>true</EnableMsixTooling>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.2526" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.3.1" />
  </ItemGroup>
</Project>
'@
$sideBanner = @'
<?xml version="1.0" encoding="utf-8" ?>
<UserControl x:Class="SideKit.SideBanner"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <TextBlock AutomationProperties.AutomationId="SideBannerText" Text="{x:Bind Message, Mode=OneWay}" />
</UserControl>
'@
$sideBannerCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideKit;

public sealed partial class SideBanner : UserControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(SideBanner), new PropertyMetadata(""));

    public SideBanner()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
'@
$sidePill = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideKit;

public sealed class SidePill : Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SidePill), new PropertyMetadata(""));

    public SidePill()
    {
        DefaultStyleKey = typeof(SidePill);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
'@
$sideGeneric = @'
<?xml version="1.0" encoding="utf-8" ?>
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:side="using:SideKit">
    <Style TargetType="side:SidePill">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="side:SidePill">
                    <Border Padding="6" CornerRadius="12" BorderThickness="1" BorderBrush="Gray">
                        <TextBlock AutomationProperties.AutomationId="SidePillLabel" Text="{TemplateBinding Label}" />
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
'@

$P = 'MainPage.xaml'
$anchor = '        <TextBlock AutomationProperties.AutomationId="Plain"'
function Insert($id, $title, $element, $checks) { Replace $id $title $P $anchor "$element`n$anchor" $checks (Expect 'applied') }
$csprojAnchor = '    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.3.1" />'

$setup = @(
    (Create 'setup-window' 'A window hosting a Frame' 'MainWindow.xaml' $window),
    (Create 'setup-window-code' 'It navigates to MainPage' 'MainWindow.xaml.cs' $windowCs),
    (Create 'setup-page' 'A page' $P $page),
    (Create 'setup-page-code' 'Its code-behind' 'MainPage.xaml.cs' $pageCs),
    (Replace 'setup-stable-ids' 'Prototype A: stable, append-only connection IDs' 'TodoBench.csproj' '    <UseWinUI>true</UseWinUI>' "    <UseWinUI>true</UseWinUI>`n    <EnableHotReloadStableConnectionIds>true</EnableHotReloadStableConnectionIds>"),
    # A library beside the app, not referenced yet (scenario 5 adds the reference).
    (Create 'setup-sidekit-project' 'A class library with XAML controls' '..\SideKit\SideKit.csproj' $sideProj),
    (Create 'setup-sidekit-banner' 'Its UserControl' '..\SideKit\SideBanner.xaml' $sideBanner),
    (Create 'setup-sidekit-banner-code' 'Its code' '..\SideKit\SideBanner.xaml.cs' $sideBannerCs),
    (Create 'setup-sidekit-pill' 'Its templated control' '..\SideKit\SidePill.cs' $sidePill),
    (Create 'setup-sidekit-generic' 'Its default style' '..\SideKit\Themes\Generic.xaml' $sideGeneric)
)
$actions = @(
    # 1. A new UserControl.
    (Create 'n1a-usercontrol-markup' 'New UserControl: markup first (x:Bind to a property its code adds next: the compiler rejects it, as a build would)' 'Badge.xaml' $badge $null (Expect 'error' 'Label')),
    (Create 'n1b-usercontrol-code' 'New UserControl: its code' 'Badge.xaml.cs' $badgeCs $null (Expect 'applied')),
    (Insert 'n1c-usercontrol-used' 'The new UserControl used on the page' '        <local:Badge x:Name="Badge1" Label="badge v1" />' @((C 'text-equals' 'BadgeText' 'badge v1'))),
    (Replace 'n1d-usercontrol-edit' 'The new UserControl''s markup edited (its live instance re-initializes)' 'Badge.xaml' '    <StackPanel>' "    <StackPanel>`n        <TextBlock AutomationProperties.AutomationId=`"BadgeStamp`" Text=`"badge markup v2`" />" @((C 'text-equals' 'BadgeStamp' 'badge markup v2'), (C 'text-equals' 'BadgeText' 'badge v1')) (Expect 'applied')),
    # 2. A new Page, navigated to; back re-creates MainPage through WinUI's (cached) type information.
    (Create 'n2a-page-markup' 'New Page: markup first (Click to a method its code adds next: the compiler rejects it)' 'SecondPage.xaml' $second $null (Expect 'error' 'Back_Click')),
    (Create 'n2b-page-code' 'New Page: its code' 'SecondPage.xaml.cs' $secondCs $null (Expect 'applied')),
    (Replace 'n2c-navigate' 'MainPage navigates to it; back re-creates MainPage' 'MainPage.xaml.cs' 'Status.Text = "nav: none";' 'Frame.Navigate(typeof(SecondPage));' @((C 'invoke' 'NavBtn'), (C 'text-equals' 'SecondTitle' 'second v1'), (C 'invoke' 'BackBtn'), (C 'exists' 'NavBtn'), (C 'text-equals' 'BadgeText' 'badge v1')) (Expect 'applied')),
    (Replace 'n2d-page-edit' 'The new page edited while not shown; the next navigation shows it' 'SecondPage.xaml' 'Text="second v1"' 'Text="second v2"' @((C 'invoke' 'NavBtn'), (C 'text-equals' 'SecondTitle' 'second v2'), (C 'invoke' 'BackBtn'), (C 'exists' 'NavBtn')) (Expect 'applied')),
    # 3. A templated control and its default style.
    (Create 'n3a-templated-class' 'New templated control: its class' 'Pill.cs' $pill $null (Expect 'applied')),
    (Create 'n3b-generic-xaml' 'Its default style: a new Themes\Generic.xaml' 'Themes\Generic.xaml' $generic $null (Expect 'applied')),
    (Insert 'n3c-templated-used' 'The templated control used on the page' '        <local:Pill Label="pill v1" />' @((C 'text-equals' 'PillLabel' 'pill v1'))),
    (Replace 'n3d-generic-edit' 'Its default template edited' 'Themes\Generic.xaml' '                            <TextBlock AutomationProperties.AutomationId="PillLabel" Text="{TemplateBinding Label}" />' "                            <TextBlock AutomationProperties.AutomationId=`"PillLabel`" Text=`"{TemplateBinding Label}`" />`n                            <TextBlock AutomationProperties.AutomationId=`"PillStamp`" Text=`"pill template v2`" />" @((C 'text-equals' 'PillStamp' 'pill template v2'), (C 'text-equals' 'PillLabel' 'pill v1')) (Expect 'applied')),
    # 4. A NuGet package with controls.
    (Replace 'n4a-nuget-reference' 'A NuGet package with controls: CommunityToolkit.WinUI.Controls.SettingsControls' 'TodoBench.csproj' $csprojAnchor "$csprojAnchor`n    <PackageReference Include=`"CommunityToolkit.WinUI.Controls.SettingsControls`" Version=`"8.2.251219`" />" $null (Expect 'applied')),
    (Replace 'n4b-nuget-xmlns' 'The package''s namespace on the page' $P "    xmlns:local=`"using:TodoBench`">" "    xmlns:local=`"using:TodoBench`"`n    xmlns:toolkit=`"using:CommunityToolkit.WinUI.Controls`">" $null $null),
    (Insert 'n4c-nuget-card' 'A SettingsCard used on the page (its default style is in the package)' '        <toolkit:SettingsCard x:Name="Card" AutomationProperties.AutomationId="Card" Header="card v1" Description="from nuget" />' @((C 'exists' 'Card'), (C 'exists' 'card v1'))),
    (Replace 'n4d-nuget-edit' 'Its header edited' $P 'Header="card v1"' 'Header="card v2"' @((C 'exists' 'card v2')) (Expect 'applied')),
    # 5. A project reference to a library with XAML controls.
    (Replace 'n5a-project-reference' 'A ProjectReference to SideKit (a UserControl and a templated control)' 'TodoBench.csproj' $csprojAnchor "$csprojAnchor`n  </ItemGroup>`n  <ItemGroup>`n    <ProjectReference Include=`"..\SideKit\SideKit.csproj`" />" $null (Expect 'applied')),
    (Replace 'n5b-library-xmlns' 'The library''s namespace on the page' $P "    xmlns:local=`"using:TodoBench`"" "    xmlns:local=`"using:TodoBench`"`n    xmlns:side=`"using:SideKit`"" $null $null),
    (Insert 'n5c-library-used' 'Its controls used on the page' "        <side:SideBanner Message=`"side v1`" />`n        <side:SidePill Label=`"sidepill v1`" />" @((C 'text-equals' 'SideBannerText' 'side v1'), (C 'text-equals' 'SidePillLabel' 'sidepill v1'))),
    # Everything again, after a navigation round trip (MainPage re-created from WinUI's type information).
    (Replace 'n6-final' 'A final edit; navigate away and back; everything still works' $P 'Text="plain v1"' 'Text="plain v2"' @((C 'text-equals' 'Plain' 'plain v2'), (C 'invoke' 'NavBtn'), (C 'text-equals' 'SecondTitle' 'second v2'), (C 'invoke' 'BackBtn'), (C 'text-equals' 'BadgeText' 'badge v1'), (C 'text-equals' 'BadgeStamp' 'badge markup v2'), (C 'text-equals' 'PillStamp' 'pill template v2'), (C 'exists' 'card v2'), (C 'text-equals' 'SideBannerText' 'side v1'), (C 'text-equals' 'SidePillLabel' 'sidepill v1')) (Expect 'applied'))
)
$journey = [ordered]@{
    schemaVersion = '1.0'; id = 'real-compiler-new-types'
    title = 'New types while the app runs, on the real XAML compiler path: a UserControl, a Page, a templated control, a NuGet package, a project reference'
    description = @('Run with WINAPP_HOT_RELOAD_REAL_COMPILER=1. Each scenario adds a type the running app has never seen, then uses it.')
    app = [ordered]@{ template = 'app-template'; assetsFrom = 'samples/winui-app/Assets'; projectFile = 'TodoBench.csproj'; rootNamespace = 'TodoBench' }
    setup = $setup
    dimensions = @(); variants = @([ordered]@{ id = 'default'; primary = $true; summary = 'default'; choices = [ordered]@{} })
    segments = @([ordered]@{ kind = 'common'; phase = 'new-types'; title = 'new types'; actions = $actions })
}
$journey | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $PSScriptRoot '..\journeys\real-compiler-new-types.json')
