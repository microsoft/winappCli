// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

/// <summary>
/// Tests for the data-driven UWP→WinAppSDK mapping analyzer (WUI1001/WUI1002/WUI1010).
/// The analyzer is gated by <c>ProjectContext</c>: it only fires when the compilation
/// looks like a UWP-migration project. We trigger that by either (a) including
/// <c>using Windows.UI.Xaml;</c> in the source, or (b) adding a Package.appxmanifest
/// AdditionalFile with a UWP-style xmlns:uap.
/// </summary>
public sealed class ApiMappingAnalyzerTests
{
    private const string UwpManifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""contoso"" Publisher=""CN=Contoso"" Version=""1.0.0.0"" />
</Package>";

    [Fact]
    public async Task Wui1001FlagsCompositionNamespaceUsingInMigratingProject()
    {
        // Windows.UI.Composition IS a mapping entry → WUI1001 fires; namespace prefix
        // also matches a feature mapping → WUI1010 fires alongside.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.UI.Composition; class C {}")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingMatch)
            .ExpectDiagnostic(DiagnosticIds.FeatureMappingHint)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1002FlagsPrintManagerNoEquivalent()
    {
        // PrintManager has no WinAppSDK equivalent — should produce WUI1002.
        // Trigger UWP-context detection via Package.appxmanifest.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
namespace Windows.Graphics.Printing { public class PrintManager { public static PrintManager GetForCurrentView() => new(); } }
namespace Sample { class C { void M() { var p = global::Windows.Graphics.Printing.PrintManager.GetForCurrentView(); } } }")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingNoEquiv)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1xxxDoesNotFireInGreenfieldProject()
    {
        // No Windows.UI.* using and no UWP manifest → context = greenfield → no diagnostics.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
using Microsoft.UI.Xaml;
namespace Microsoft.UI.Xaml { public class Window {} }
namespace Sample { class C {} }")
            .ExpectClean()
            .RunAsync();
    }

    [Fact]
    public async Task Wui1010FeatureHintFiresOnFeatureNamespace()
    {
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.ApplicationModel.Background; class C {}")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.FeatureMappingHint)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1001FlagsToastNotificationManager()
    {
        // B1: ToastNotificationManager has a WinAppSDK equivalent → WUI1001.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
namespace Windows.UI.Notifications { public class ToastNotificationManager { public static object CreateToastNotifier() => new(); } }
namespace Sample { class C { void M() { var n = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(); } } }")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingMatch)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1002FlagsRadialControllerNoEquivalent()
    {
        // B1: RadialController has no WinAppSDK equivalent → WUI1002.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
namespace Windows.UI.Input { public class RadialController { public static RadialController CreateForCurrentView() => new(); } }
namespace Sample { class C { void M() { var r = global::Windows.UI.Input.RadialController.CreateForCurrentView(); } } }")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingNoEquiv)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1001FlagsApplicationDataLocalSettings()
    {
        // B1: ApplicationData.LocalSettings maps to Microsoft.Windows.Storage.ApplicationData → WUI1001.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
namespace Windows.Storage { public class ApplicationData { public static ApplicationData Current => new(); public object LocalSettings => new(); } }
namespace Sample { class C { void M() { var s = global::Windows.Storage.ApplicationData.Current.LocalSettings; } } }")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingMatch)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1002DisplayRequestCarriesStartupCrashTier()
    {
        // B2: DisplayRequest is a runtime crasher → WUI1002 + startup-crash tier property.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource(@"
namespace Windows.System.Display { public class DisplayRequest { public void RequestActive() {} } }
namespace Sample { class C { void M() { var d = new global::Windows.System.Display.DisplayRequest(); d.RequestActive(); } } }")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.ApiMappingNoEquiv)
            .ExpectProperty(DiagnosticIds.ApiMappingNoEquiv, MigrationTiers.PropertyKey, MigrationTiers.StartupCrash)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1010FeatureHintFiresOnSensorsNamespace()
    {
        // B1: sensitive sensor family is a feature area → WUI1010 carrying the sensitive tier.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.Devices.Sensors; class C {}")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.FeatureMappingHint)
            .ExpectProperty(DiagnosticIds.FeatureMappingHint, MigrationTiers.PropertyKey, MigrationTiers.Sensitive)
            .RunAsync();
    }

    [Fact]
    public async Task Wui1010NonSensitiveFeatureHintCarriesNoSensitiveTier()
    {
        // Gap #2: a plain Windows.UI.Xaml namespace hint must NOT be flagged sensitive,
        // so it does not wrongly force sequential-manual pacing downstream.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.UI.Xaml.Controls; class C {}")
            .WithXaml("Package.appxmanifest", UwpManifest)
            .ExpectDiagnostic(DiagnosticIds.FeatureMappingHint)
            .ExpectPropertyAbsent(DiagnosticIds.FeatureMappingHint, MigrationTiers.PropertyKey)
            .RunAsync();
    }

    [Fact]
    public async Task GatedRulesStaySilentWithoutUwpMarkersOrForceFlag()
    {
        // No Package.appxmanifest and no Windows.UI.Xaml marker → context is not MigratingFromUwp,
        // so the gated ApiMappingAnalyzer stays silent (false-positive guard on greenfield/unknown).
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.Devices.Sensors; class C {}")
            .ExpectClean()
            .RunAsync();
    }

    [Fact]
    public async Task B4ForceMigrationFiresGatedRulesWithoutUwpMarkers()
    {
        // B4: the analyze/validate driver's --from-uwp sets the global option; gated rules then fire
        // even on mostly-migrated target source (no manifest, no Windows.UI.Xaml) so validate can
        // catch API residue.
        await new AnalyzerTest<ApiMappingAnalyzer>()
            .WithSource("using Windows.Devices.Sensors; class C {}")
            .ForceMigration()
            .ExpectDiagnostic(DiagnosticIds.FeatureMappingHint)
            .ExpectProperty(DiagnosticIds.FeatureMappingHint, MigrationTiers.PropertyKey, MigrationTiers.Sensitive)
            .RunAsync();
    }
}
