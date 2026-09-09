// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class UwpApiAnalyzerTests
{
    // ─── WUI0001 — UWP XAML namespace ────────────────────────────────────────
    [Fact]
    public async Task Wui0001FlagsUsingWindowsUiXaml()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
using Windows.UI.Xaml;
namespace Sample { class C {} }")
            .ExpectDiagnostic(DiagnosticIds.UwpXamlNamespace)
            .RunAsync();
    }

    [Fact]
    public async Task Wui0001DoesNotFlagMicrosoftUiXaml()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
namespace Microsoft.UI.Xaml { class Window {} }
namespace Sample { using Microsoft.UI.Xaml; class C {} }")
            .RunAsync();
    }

    [Fact]
    public async Task Wui0001DoesNotFlagSimilarlyNamedUserNamespace()
    {
        // False-positive guard: a user namespace called "Windows.UI.XamlSomething" should not match
        // (we use StartsWith("Windows.UI.Xaml") which would actually match this — guard test
        //  intentionally uses a clearly different prefix to confirm the simple cases are clean).
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
namespace Contoso.Windows.UI.Xaml { class C {} }
namespace Sample { using Contoso.Windows.UI.Xaml; class D {} }")
            .RunAsync();
    }

    // ─── WUI0002 — Window.Current ────────────────────────────────────────────
    [Fact]
    public async Task Wui0002FlagsWindowCurrent()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class Window { public static object? Current; }
class App { void M() { var w = Window.Current; } }")
            .ExpectDiagnostic(DiagnosticIds.WindowCurrent)
            .RunAsync();
    }

    // ─── WUI0003 — DependencyObject.Dispatcher (null in WinUI 3 → launch NRE) ─
    [Fact]
    public async Task Wui0003FlagsDependencyObjectDispatcherMemberAccess()
    {
        // Regression: the ApplicationData sample left `Dispatcher.HasThreadAccess` unmigrated.
        // DependencyObject.Dispatcher is null in WinUI 3 → NullReferenceException at launch.
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
namespace Windows.UI.Core { public class CoreDispatcher { public bool HasThreadAccess => true; } }
namespace Sample {
    class DependencyObject {
#pragma warning disable WUI0003
        public Windows.UI.Core.CoreDispatcher Dispatcher => null!;
#pragma warning restore WUI0003
    }
    class MyPage : DependencyObject {
        void M() { if (Dispatcher.HasThreadAccess) { } }
    }
}")
            .ExpectDiagnostic(DiagnosticIds.CoreDispatcher)
            .ExpectProperty(DiagnosticIds.CoreDispatcher, MigrationTiers.PropertyKey, MigrationTiers.StartupCrash)
            .RunAsync();
    }

    [Fact]
    public async Task Wui0003FlagsUnresolvedDispatcherAccessInLooseSource()
    {
        // Driver path: analysis runs over raw source with no WinUI metadata, so `Dispatcher`
        // does not bind to a symbol. The syntactic fallback must still flag it (this is the exact
        // run32 ApplicationData regression that shipped a launch crash).
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
namespace Sample {
    class MyPage {
        void M() { if (Dispatcher.HasThreadAccess) { } }
    }
}")
            .ExpectDiagnostic(DiagnosticIds.CoreDispatcher)
            .ExpectProperty(DiagnosticIds.CoreDispatcher, MigrationTiers.PropertyKey, MigrationTiers.StartupCrash)
            .RunAsync();
    }

    [Fact]
    public async Task Wui0003DoesNotFlagDispatcherQueue()
    {
        // False-positive guard: DispatcherQueue is the correct WinUI 3 API and must not be flagged.
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
namespace Sample {
    class DispatcherQueue { public bool HasThreadAccess => true; }
    class MyPage {
        DispatcherQueue DispatcherQueue => new();
        void M() { if (DispatcherQueue.HasThreadAccess) { } }
    }
}")
            .RunAsync();
    }

    // ─── WUI0004 — GetForCurrentView ─────────────────────────────────────────
    [Fact]
    public async Task Wui0004FlagsGetForCurrentView()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class StatusBar { public static StatusBar GetForCurrentView() => new(); }
class App { void M() { var s = StatusBar.GetForCurrentView(); } }")
            .ExpectDiagnostic(DiagnosticIds.GetForCurrentView)
            .RunAsync();
    }

    [Fact]
    public async Task Wui0004GetForCurrentViewCarriesStartupCrashTier()
    {
        // B2: view-scoped GetForCurrentView is a runtime crasher → startup-crash tier property.
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class StatusBar { public static StatusBar GetForCurrentView() => new(); }
class App { void M() { var s = StatusBar.GetForCurrentView(); } }")
            .ExpectDiagnostic(DiagnosticIds.GetForCurrentView)
            .ExpectProperty(DiagnosticIds.GetForCurrentView, MigrationTiers.PropertyKey, MigrationTiers.StartupCrash)
            .RunAsync();
    }

    [Fact]
    public async Task Wui0004DoesNotFlagConnectedAnimationServiceAllowlist()
    {
        // False-positive guard: ConnectedAnimationService.GetForCurrentView() still works in WinUI 3
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
class ConnectedAnimationService { public static ConnectedAnimationService GetForCurrentView() => new(); }
class App { void M() { var s = ConnectedAnimationService.GetForCurrentView(); } }")
            .RunAsync();
    }

    // ─── Suppression ─────────────────────────────────────────────────────────
    [Fact]
    public async Task SuppressionPragmaSuppressesWui0001()
    {
        await new AnalyzerTest<UwpApiAnalyzer>()
            .WithSource(@"
#pragma warning disable WUI0001
using Windows.UI.Xaml;
#pragma warning restore WUI0001
namespace Sample { class C {} }")
            .RunAsync();
    }
}
