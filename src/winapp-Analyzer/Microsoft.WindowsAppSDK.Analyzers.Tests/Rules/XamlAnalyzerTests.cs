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
