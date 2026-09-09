#nullable enable annotations
using System.Collections.Immutable;

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// UWP → Windows App SDK feature-area hints.
/// Source: https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/feature-mapping-table
///
/// Reported as <c>WUI1010</c> (Info severity, gated to <c>MigratingFromUwp</c> projects only).
/// These hints are advisory: they say "you used X — here's the WinAppSDK story for the
/// whole feature area" rather than flagging an exact API replacement. Pair with WUI1001/1002
/// for precise per-API guidance.
/// </summary>
internal static class FeatureMappings
{
    public static readonly ImmutableArray<FeatureMapping> All = ImmutableArray.Create(
        new FeatureMapping("Windows.ApplicationModel.Background", "Background tasks",
            "WinAppSDK uses a Full-trust COM background-task model. See guides/background-task-migration-strategy."),
        new FeatureMapping("Windows.UI.Core",                     "Windowing & dispatching",
            "CoreWindow → AppWindow, CoreDispatcher → DispatcherQueue. See guides/windowing and guides/threading."),
        new FeatureMapping("Windows.UI.WindowManagement",         "Windowing",
            "AppWindow has been redesigned in WinAppSDK. See guides/windowing."),
        new FeatureMapping("Windows.UI.Xaml",                     "WinUI 3",
            "All Windows.UI.Xaml namespaces move to Microsoft.UI.Xaml. See guides/winui."),
        new FeatureMapping("Windows.UI.Composition",              "Composition",
            "Windows.UI.Composition → Microsoft.UI.Composition (all subnamespaces)."),
        new FeatureMapping("Windows.ApplicationModel.Resources",  "Resources / MRT",
            "MRT → MRT Core. See guides/mrtcore."),
        new FeatureMapping("Windows.Web.UI",                      "WebView",
            "WebView (UWP) → WebView2 (Microsoft.Web.WebView2). EnsureCoreWebView2Async required."),
        new FeatureMapping("Windows.Security.Authentication.Web", "OAuth",
            "WebAuthenticationBroker → OAuth2Manager (WinAppSDK 1.7+). See develop/security/oauth2."),

        // ─── B1: sensitive feature families (drive SEQUENTIAL pacing via featureArea) ───
        new FeatureMapping("Windows.Media.Capture",           "Media capture",
            "Camera/media capture family. Preview via MediaPlayerElement; see guides/winui.", sensitive: true),
        new FeatureMapping("Windows.Media.SpeechRecognition", "Speech",
            "Speech recognition family — review WinAppSDK/Windows.Media support before migrating.", sensitive: true),
        new FeatureMapping("Windows.Media.SpeechSynthesis",   "Speech",
            "Speech synthesis family — review WinAppSDK/Windows.Media support before migrating.", sensitive: true),
        new FeatureMapping("Windows.Media.Audio",             "Audio",
            "Audio graph/playback family — validate device access under desktop identity.", sensitive: true),
        new FeatureMapping("Windows.Devices.Sensors",         "Sensors",
            "Sensor family — validate capabilities and device access under desktop identity.", sensitive: true),
        new FeatureMapping("Windows.Devices.Geolocation",     "Sensors",
            "Geolocation family — requires the location capability and consent prompt.", sensitive: true),
        new FeatureMapping("Windows.Devices.Bluetooth",       "Sensors",
            "Bluetooth family — validate radio/device access under desktop identity.", sensitive: true),
        new FeatureMapping("Windows.Devices.PointOfService",  "Sensors",
            "Point-of-service device family — validate device access under desktop identity.", sensitive: true),
        new FeatureMapping("Windows.Networking.Proximity",    "Sensors",
            "Proximity/NFC family — validate capability support before migrating.", sensitive: true),

        // ─── B1: phone-only families (no desktop equivalent → defer) ───
        new FeatureMapping("Windows.Phone",                   "Phone-only",
            "Phone-only API surface — no desktop equivalent; defer or redesign."),
        new FeatureMapping("Windows.ApplicationModel.Calls",  "Phone-only",
            "Phone-only calls API surface — no desktop equivalent; defer or redesign."),
        new FeatureMapping("Windows.Gaming.Input",            "Gamepad input",
            "Gamepad virtual-key paths are not in WinAppSDK — defer or redesign.")
    );

    public static readonly ImmutableDictionary<string, FeatureMapping> ByNamespacePrefix =
        BuildLookup();

    private static ImmutableDictionary<string, FeatureMapping> BuildLookup()
    {
        var b = ImmutableDictionary.CreateBuilder<string, FeatureMapping>();
        foreach (var f in All) b[f.UwpNamespacePrefix] = f;
        return b.ToImmutable();
    }
}

internal sealed class FeatureMapping
{
    public FeatureMapping(string uwpNamespacePrefix, string area, string note, bool sensitive = false)
    {
        UwpNamespacePrefix = uwpNamespacePrefix;
        Area = area;
        Note = note;
        Sensitive = sensitive;
    }
    public string UwpNamespacePrefix { get; }
    public string Area { get; }
    public string Note { get; }

    /// <summary>
    /// True for sensitive feature families (media capture, speech, audio, sensors, …) that must be
    /// migrated SEQUENTIALLY. Carried to the analyze driver as <c>severity: sensitive</c>. Ordinary
    /// namespace-rename hints (e.g. Windows.UI.Xaml) leave this false.
    /// </summary>
    public bool Sensitive { get; }
}
