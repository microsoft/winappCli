#nullable enable annotations
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Data-driven UWP → Windows App SDK API mappings.
/// Source: https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table
///
/// This file is intentionally checked in (not generated at build time) so additions are
/// reviewable in PR. The naming suffix `.g.cs` reflects that future versions may switch
/// to a build-time scraper; the public shape is stable so callers are unaffected.
///
/// To add a new mapping:
///   1. Find the UWP type/member on the Learn page.
///   2. Append a new <see cref="ApiMapping"/> entry with the fully-qualified UWP name,
///      the canonical Windows App SDK replacement (or <c>null</c> if not supported),
///      and a link to the Learn anchor or feature-area guide.
///   3. Add a test in <c>ApiMappingAnalyzerTests</c> exercising the new entry.
/// </summary>
internal static class ApiMappings
{
    public static readonly ImmutableArray<ApiMapping> All = ImmutableArray.Create(
        // ─── Windowing ────────────────────────────────────────────────────────
        new ApiMapping("Windows.UI.Core.CoreWindow",                       "Microsoft.UI.Windowing.AppWindow", "guides/windowing"),
        new ApiMapping("Windows.UI.Core.CoreWindow.GetForCurrentThread",   null,                               "guides/windowing"),
        new ApiMapping("Windows.UI.Core.CoreWindow.Bounds",                "Microsoft.UI.Windowing.AppWindow.Size", "guides/windowing"),
        new ApiMapping("Windows.UI.Core.CoreWindow.Activate",              "Microsoft.UI.Windowing.AppWindow.Show", "guides/windowing"),
        new ApiMapping("Windows.UI.Core.CoreWindow.Dispatcher",            "Microsoft.UI.Xaml.Window.DispatcherQueue", "guides/windowing"),
        new ApiMapping("Windows.UI.ViewManagement.ApplicationView",        "Microsoft.UI.Windowing.AppWindow", "guides/windowing"),
        new ApiMapping("Windows.UI.ViewManagement.ApplicationViewTitleBar","Microsoft.UI.Windowing.AppWindowTitleBar", "guides/windowing"),
        new ApiMapping("Windows.UI.WindowManagement.AppWindow",            "Microsoft.UI.Windowing.AppWindow", "guides/windowing"),
        new ApiMapping("Windows.ApplicationModel.Core.CoreApplication.CreateNewView", "Microsoft.UI.Windowing.AppWindow.Create", "guides/windowing"),

        // ─── Threading / Dispatcher ───────────────────────────────────────────
        new ApiMapping("Windows.UI.Core.CoreDispatcher",                   "Microsoft.UI.Dispatching.DispatcherQueue", "guides/threading"),
        new ApiMapping("Windows.UI.Core.CoreDispatcher.RunAsync",          "Microsoft.UI.Dispatching.DispatcherQueue.TryEnqueue", "guides/threading"),

        // ─── Composition ──────────────────────────────────────────────────────
        new ApiMapping("Windows.UI.Composition",                           "Microsoft.UI.Composition", null),
        new ApiMapping("Windows.UI.Xaml",                                  "Microsoft.UI.Xaml", null),

        // ─── Resources / MRT ──────────────────────────────────────────────────
        new ApiMapping("Windows.ApplicationModel.Resources.Core",          "Microsoft.Windows.ApplicationModel.Resources", "guides/mrtcore"),
        new ApiMapping("Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView",
                                                                           "Microsoft.Windows.ApplicationModel.Resources.ResourceManager.CreateResourceContext",
                                                                           "guides/mrtcore#resourcecontextgetforcurrentview-and-resourcecontextgetforviewindependentuse"),
        new ApiMapping("Windows.ApplicationModel.Resources.Core.ResourceManager.Current",
                                                                           "new Microsoft.Windows.ApplicationModel.Resources.ResourceManager()", "guides/mrtcore#resourcemanager-class"),

        // ─── Background tasks ─────────────────────────────────────────────────
        new ApiMapping("Windows.ApplicationModel.Background.BackgroundTaskBuilder",
                                                                           "Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder",
                                                                           "../applifecycle/background-tasks"),

        // ─── Activation ───────────────────────────────────────────────────────
        new ApiMapping("Windows.ApplicationModel.Activation.LaunchActivatedEventArgs",
                                                                           "Microsoft.UI.Xaml.LaunchActivatedEventArgs", null),

        // ─── System UI / Title bar ────────────────────────────────────────────
        new ApiMapping("Windows.ApplicationModel.Core.CoreApplicationViewTitleBar",
                                                                           "Microsoft.UI.Windowing.AppWindowTitleBar", null),
        new ApiMapping("Windows.UI.Core.SystemNavigationManager",          null, "case-study-1"),

        // ─── View management ──────────────────────────────────────────────────
        new ApiMapping("Windows.UI.ViewManagement.AccessibilitySettings.HighContrastChanged",
                                                                           "Microsoft.UI.System.ThemeSettings.Changed", null),

        // ─── Pickers / dialogs ────────────────────────────────────────────────
        // On Windows App SDK 1.8+, the WinRT pickers are SUPERSEDED by
        // Microsoft.Windows.Storage.Pickers.{FileOpenPicker, FileSavePicker, FolderPicker}.
        // The legacy WinRT types still compile but silently fail to display a
        // dialog in packaged (MSIX/Store) apps even when IInitializeWithWindow
        // succeeds — see core-pattern `file-picker-desktop` for the migration.
        new ApiMapping("Windows.Storage.Pickers.FileOpenPicker",           "Microsoft.Windows.Storage.Pickers.FileOpenPicker", "guides/winui#messagedialog-and-pickers"),
        new ApiMapping("Windows.Storage.Pickers.FileSavePicker",           "Microsoft.Windows.Storage.Pickers.FileSavePicker", "guides/winui#messagedialog-and-pickers"),
        new ApiMapping("Windows.Storage.Pickers.FolderPicker",             "Microsoft.Windows.Storage.Pickers.FolderPicker",   "guides/winui#messagedialog-and-pickers"),
        new ApiMapping("Windows.UI.Popups.MessageDialog",                  "Microsoft.UI.Xaml.Controls.ContentDialog + IInitializeWithWindow", "guides/winui#messagedialog-and-pickers"),

        // ─── Authentication ───────────────────────────────────────────────────
        new ApiMapping("Windows.Security.Authentication.Web.WebAuthenticationBroker",
                                                                           "Microsoft.Security.Authentication.OAuth.OAuth2Manager",
                                                                           "/develop/security/oauth2"),

        // ─── Capture ──────────────────────────────────────────────────────────
        new ApiMapping("Windows.Media.Capture.CameraCaptureUI",            "Microsoft.Windows.Media.Capture.CameraCaptureUI", null),

        // ─── No-equiv (currently unsupported) ─────────────────────────────────
        new ApiMapping("Windows.Graphics.Printing.PrintManager",           null, null),
        new ApiMapping("Windows.System.Display.DisplayRequest",            null, null, startupCrash: true),
        new ApiMapping("Windows.UI.Text.Core.CoreTextServicesManager",     null, "Windows 11 only"),
        new ApiMapping("Windows.UI.Core.SystemNavigationManager.GetForCurrentView", null, null),

        // ─── B1: additional migration-table coverage (UWP → WinAppSDK) ───────────
        // Adaptable (WinAppSDK equivalent exists → WUI1001):
        new ApiMapping("Windows.UI.Xaml.Controls.MediaElement",            "Microsoft.UI.Xaml.Controls.MediaPlayerElement", "guides/winui"),
        new ApiMapping("Windows.UI.Xaml.Controls.CaptureElement",          "Microsoft.UI.Xaml.Controls.MediaPlayerElement", "guides/winui"),
        new ApiMapping("Windows.UI.Notifications.ToastNotificationManager", "Microsoft.Windows.AppNotifications.AppNotificationManager", "guides/notifications"),
        new ApiMapping("Windows.Networking.PushNotifications.PushNotificationChannelManager", "Microsoft.Windows.PushNotifications.PushNotificationManager", "guides/notifications"),
        new ApiMapping("Windows.Storage.ApplicationData.LocalSettings",     "Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalSettings", "guides/applicationdata"),
        new ApiMapping("Windows.Storage.ApplicationData.LocalFolder",       "Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder", "guides/applicationdata"),

        // Unsupported (no WinAppSDK desktop equivalent → WUI1002):
        new ApiMapping("Windows.UI.Xaml.Controls.InkCanvas",               null, "no WinUI 3 desktop equivalent"),
        new ApiMapping("Windows.UI.Input.RadialController",                null, "UWP-only Surface Dial input"),
        new ApiMapping("Windows.UI.Text.Core.CoreTextEditContext",         null, "UWP-only custom IME / text-input integration"),
        new ApiMapping("Windows.ApplicationModel.Contacts.ContactManager", null, "UWP-only system contact UI"),
        new ApiMapping("Windows.ApplicationModel.Contacts.ContactPicker",  null, "UWP-only system contact UI"),
        new ApiMapping("Windows.System.Profile.AnalyticsInfo",             null, "device-family branching has no desktop analog"),
        new ApiMapping("Windows.Storage.ApplicationData.RoamingSettings",  null, "roaming app data removed in WinAppSDK"),
        new ApiMapping("Windows.Storage.ApplicationData.RoamingFolder",    null, "roaming app data removed in WinAppSDK")
    );

    /// <summary>Lookup by exact symbol display string (namespace-qualified).</summary>
    public static readonly ImmutableDictionary<string, ApiMapping> ByQualifiedName =
        BuildLookup();

    private static ImmutableDictionary<string, ApiMapping> BuildLookup()
    {
        var b = ImmutableDictionary.CreateBuilder<string, ApiMapping>();
        foreach (var m in All)
        {
            // last-wins is fine; entries are unique by construction.
            b[m.UwpQualifiedName] = m;
        }
        return b.ToImmutable();
    }
}

/// <summary>
/// One row of the API mapping table.
/// </summary>
internal sealed class ApiMapping
{
    public ApiMapping(string uwpQualifiedName, string? winAppSdkReplacement, string? learnAnchor, bool startupCrash = false)
    {
        UwpQualifiedName = uwpQualifiedName;
        WinAppSdkReplacement = winAppSdkReplacement;
        LearnAnchor = learnAnchor;
        StartupCrash = startupCrash;
    }
    public string UwpQualifiedName { get; }
    /// <summary>Replacement guidance text. <c>null</c> if no equivalent yet.</summary>
    public string? WinAppSdkReplacement { get; }
    public string? LearnAnchor { get; }
    /// <summary>True if leaving this API unaddressed throws at runtime (blank-window crash).</summary>
    public bool StartupCrash { get; }
}
