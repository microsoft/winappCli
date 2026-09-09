// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class WebView2InitAnalyzerTests
{
    [Fact]
    public async Task Wui4001FlagsNavigateToStringWithoutInit()
    {
        await new AnalyzerTest<WebView2InitAnalyzer>()
            .WithSource(@"
class WebView2 { public void NavigateToString(string s) {} }
class Page { WebView2 webView = new(); void Load() { webView.NavigateToString(""<html/>""); } }")
            .ExpectDiagnostic(DiagnosticIds.WebView2NoInit)
            .RunAsync();
    }

    [Fact]
    public async Task Wui4001DoesNotFlagWhenEnsureCoreWebView2AsyncPresent()
    {
        await new AnalyzerTest<WebView2InitAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class WebView2 { public Task EnsureCoreWebView2Async() => Task.CompletedTask; public void NavigateToString(string s) {} }
class Page { WebView2 webView = new();
    async Task Load() { await webView.EnsureCoreWebView2Async(); webView.NavigateToString(""<html/>""); } }")
            .RunAsync();
    }

    [Fact]
    public async Task Wui4001DoesNotFlagUnrelatedNavigateOnNonWebViewType()
    {
        // FP guard: an unrelated class with a Navigate() method should not flag.
        await new AnalyzerTest<WebView2InitAnalyzer>()
            .WithSource(@"
class Router { public void Navigate(string url) {} }
class Page { Router router = new(); void Go() { router.Navigate(""/""); } }")
            .RunAsync();
    }
}
