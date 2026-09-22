// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class XamlAnalyzerTests
{
    private const string MinimalCs = "namespace Sample { class C {} }";

    [Fact]
    public async Task Wui2010FlagsNestedXBindWithoutFallback()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind ViewModel.User.Name}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindNestedNoFallback)
            .ExpectDiagnostic(DiagnosticIds.XBindMissingMode)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011FlagsXBindWithoutMode()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind ViewModel.Name}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindMissingMode)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagXBindWithMode()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind ViewModel.Name, Mode=OneWay}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagXBindWithSpacedMode()
    {
        // Explicit Mode must be recognized regardless of whitespace around '='.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind ViewModel.Counter, Mode = OneWay}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagModeBeforeNamedPath()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind Mode=OneWay, Path=ViewModel.Counter}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2010FlagsNamedNestedPathWithoutFallback()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind Mode=OneWay, Path=ViewModel.Profile.Name}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindNestedNoFallback)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagInheritedDefaultBindMode()
    {
        // A binding under x:DefaultBindMode=""OneWay"" inherits that default; no missing-mode warning.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Grid x:DefaultBindMode=""OneWay"">
    <TextBlock Text=""{x:Bind ViewModel.Counter}"" />
  </Grid>
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011IgnoresUnrelatedDefaultBindModeProperty()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       xmlns:local=""using:Sample"">
  <local:Host DefaultBindMode=""Custom"">
    <TextBlock Text=""{x:Bind Counter}"" />
  </local:Host>
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindMissingMode)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011FlagsSimplePathBinding()
    {
        // A single-segment property path gets the same missing-mode policy as a dotted path,
        // and must not be misclassified as an event handler.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind Counter}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindMissingMode)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagEventHandlerBinding()
    {
        // Binding Mode is meaningless for events; an event handler reference must stay clean.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Button Content=""Save"" Click=""{x:Bind ViewModel.Save}""
          AutomationProperties.AutomationId=""save"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagFrameworkEventOutsideOriginalSet()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <DatePicker DateChanged=""{x:Bind ViewModel.OnDateChanged}""
              AutomationProperties.AutomationId=""date"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagCustomControlEvent()
    {
        const string source = @"namespace Sample
{
    public class Calendar
    {
        public event System.EventHandler DayTapped { add { } remove { } }
    }
}";
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:local=""using:Sample"">
  <local:Calendar DayTapped=""{x:Bind OnDayTapped}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(source)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011FlagsCustomPropertyNamedLikeFrameworkEvent()
    {
        const string source = @"namespace Sample
{
    public class Panel
    {
        public bool Opened { get; set; }
    }
}";
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:local=""using:Sample"">
  <local:Panel Opened=""{x:Bind ViewModel.IsOpen}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(source)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.XBindMissingMode)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2010DoesNotFlagNestedBindingWithExplicitModeAndFallback()
    {
        // FP guard: a nested binding with an explicit mode still needs a fallback to stay clean;
        // with FallbackValue provided, neither WUI2010 nor WUI2011 fires.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <TextBlock Text=""{x:Bind ViewModel.Profile.Name, Mode=OneWay, FallbackValue=''}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2011DoesNotFlagCommandBinding()
    {
        // FP guard: command bindings are correctly OneTime.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Button Command=""{x:Bind ViewModel.SaveCommand}"" AutomationProperties.AutomationId=""save"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2012FlagsNullConverter()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <TextBlock Text=""{Binding Name, Converter={x:Null}}"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.NullConverter)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2020FlagsButtonWithoutAutomationId()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Button Content=""Click"" />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.MissingAutomationId, DiagnosticSeverity.Info)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2020DoesNotFlagAppXaml()
    {
        // App.xaml is intentionally skipped.
        var xaml = @"<Application xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Application.Resources><Button /></Application.Resources>
</Application>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("App.xaml", xaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2003FlagsPivot()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Pivot />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.UwpOnlyXamlControl)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2003FlagsVirtualizingStackPanel()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <VirtualizingStackPanel />
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.UwpOnlyXamlControl)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2003FlagsHubAndSection()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Hub>
    <HubSection />
  </Hub>
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.UwpOnlyXamlControl)
            .ExpectDiagnostic(DiagnosticIds.UwpOnlyXamlControl)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2003DoesNotFlagWinUiControls()
    {
        // FP guard: WinUI 3 controls that survive migration must stay clean.
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Grid>
    <ItemsRepeater />
    <TabView />
  </Grid>
</Page>";
        await new AnalyzerTest<XamlAnalyzer>()
            .WithSource(MinimalCs)
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }
}
