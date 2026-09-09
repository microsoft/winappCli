// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

/// <summary>
/// Suppression regression tests. Every shipping rule must honor
/// <c>#pragma warning disable WUIxxxx</c>. A rule that doesn't honor pragma suppression
/// is unsuppressible and therefore unshippable — this file is the gate.
///
/// Pattern: take the smallest source/XAML that triggers each rule, wrap it in a pragma
/// disable, and assert <c>ExpectClean()</c>. If a future refactor breaks suppression
/// (e.g. by registering a SymbolAnalyzer at compilation-end without honoring filters)
/// these tests will turn red.
/// </summary>
public sealed class SuppressionTests
{
    // ─── WUI0001 — UWP XAML namespace ────────────────────────────────────────
    [Fact]
    public async Task SuppressWui0001()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
#pragma warning disable WUI0001
using Windows.UI.Xaml;
#pragma warning restore WUI0001
namespace Sample { class C {} }")
            .RunAsync();
    }

    // ─── WUI0002 — Window.Current ────────────────────────────────────────────
    [Fact]
    public async Task SuppressWui0002()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class Window { public static object? Current; }
class App { void M() {
#pragma warning disable WUI0002
    var w = Window.Current;
#pragma warning restore WUI0002
} }")
            .RunAsync();
    }

    // ─── WUI0004 — GetForCurrentView ─────────────────────────────────────────
    [Fact]
    public async Task SuppressWui0004()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class StatusBar { public static StatusBar GetForCurrentView() => new(); }
class App { void M() {
#pragma warning disable WUI0004
    var s = StatusBar.GetForCurrentView();
#pragma warning restore WUI0004
} }")
            .RunAsync();
    }

    // ─── WUI2001 — TabView raw content ───────────────────────────────────────
    [Fact]
    public async Task SuppressWui2001()
    {
        await new AnalyzerTest<TabViewContentAnalyzer>()
            .WithSource(@"
class TextBox {}
class TabViewItem { public object? Content { get; set; } }
class C { void M() {
    var tabItem = new TabViewItem();
#pragma warning disable WUI2001
    tabItem.Content = new TextBox();
#pragma warning restore WUI2001
} }")
            .RunAsync();
    }

    // ─── WUI3001 — Old MVVM syntax ───────────────────────────────────────────
    [Fact]
    public async Task SuppressWui3001()
    {
        await new AnalyzerTest<MvvmPatternAnalyzer>()
            .WithSource(@"
using System;
class ObservablePropertyAttribute : Attribute {}
partial class VM {
#pragma warning disable WUI3001
    [ObservableProperty] private string _name = """";
#pragma warning restore WUI3001
}")
            .RunAsync();
    }

    // ─── WUI4001 — WebView2 NavigateToString without init ────────────────────
    [Fact]
    public async Task SuppressWui4001()
    {
        await new AnalyzerTest<WebView2InitAnalyzer>()
            .WithSource(@"
class WebView2 { public void NavigateToString(string s) {} }
class Page { WebView2 webView = new(); void Load() {
#pragma warning disable WUI4001
    webView.NavigateToString(""<html/>"");
#pragma warning restore WUI4001
} }")
            .RunAsync();
    }

    // ─── WUI4101 — GenAI SetInputSequences ───────────────────────────────────
    [Fact]
    public async Task SuppressWui4101()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class GeneratorParams { public void SetInputSequences(object s) {} }
class C { void M() { var p = new GeneratorParams();
#pragma warning disable WUI4101
    p.SetInputSequences(null!);
#pragma warning restore WUI4101
} }")
            .RunAsync();
    }

    // ─── WUI4102 — GenAI ComputeLogits ───────────────────────────────────────
    [Fact]
    public async Task SuppressWui4102()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class Generator { public void ComputeLogits() {} }
class C { void M() { var g = new Generator();
#pragma warning disable WUI4102
    g.ComputeLogits();
#pragma warning restore WUI4102
} }")
            .RunAsync();
    }

    // ─── WUI4103 — GenAI TokenizerStream ctor ────────────────────────────────
    [Fact]
    public async Task SuppressWui4103()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class Tokenizer {}
class TokenizerStream { public TokenizerStream(Tokenizer t) {} }
class C { void M() { var t = new Tokenizer();
#pragma warning disable WUI4103
    var s = new TokenizerStream(t);
#pragma warning restore WUI4103
} }")
            .RunAsync();
    }

    // ─── WUI2030 — Attached property nested initializer ──────────────────────
    [Fact]
    public async Task SuppressWui2030()
    {
        await new AnalyzerTest<AttachedPropertyAnalyzer>()
            .WithSource(@"
class Button { public object? AutomationProperties { get; set; } }
class C { void M() {
#pragma warning disable WUI2030
    var b = new Button { AutomationProperties = { AutomationId = ""ok"" } };
#pragma warning restore WUI2030
} }")
            .RunAsync();
    }

    // ─── WUI1001 — API mapping (data-driven) ─────────────────────────────────
    [Fact]
    public async Task SuppressWui1001()
    {
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
#pragma warning disable WUI1001, WUI1010
using Windows.UI.Core;
#pragma warning restore WUI1001, WUI1010
class C {}")
            .WithXaml("Package.appxmanifest", @"<?xml version=""1.0""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"" />")
            .RunAsync();
    }
}
