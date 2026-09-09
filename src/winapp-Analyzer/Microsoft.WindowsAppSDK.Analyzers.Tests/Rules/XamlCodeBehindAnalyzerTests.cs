// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class XamlCodeBehindAnalyzerTests
{
    [Fact]
    public async Task Wui4002FlagsWebView2InXamlWithoutInitInCodeBehind()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <WebView2 x:Name=""web"" />
</Page>";
        var cs = @"namespace Sample { partial class MainPage { public void Load() { /* no EnsureCoreWebView2Async */ } } }";
        await new AnalyzerTest<XamlCodeBehindAnalyzer>()
            .WithSource(cs, "MainPage.xaml.cs")
            .WithXaml("MainPage.xaml", xaml)
            .ExpectDiagnostic(DiagnosticIds.WebView2NoInitXaml)
            .RunAsync();
    }

    [Fact]
    public async Task Wui4002DoesNotFlagWhenInitPresent()
    {
        var xaml = @"<Page xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <WebView2 x:Name=""web"" />
</Page>";
        var cs = @"using System.Threading.Tasks;
namespace Sample { partial class MainPage {
    class WebView2 { public Task EnsureCoreWebView2Async() => Task.CompletedTask; }
    WebView2 web = new();
    public async Task Load() { await web.EnsureCoreWebView2Async(); }
} }";
        await new AnalyzerTest<XamlCodeBehindAnalyzer>()
            .WithSource(cs, "MainPage.xaml.cs")
            .WithXaml("MainPage.xaml", xaml)
            .RunAsync();
    }
}
