// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Standardized diagnostic category names. These appear in IDE diagnostic lists,
/// editorconfig severity overrides (<c>dotnet_diagnostic.WUIxxxx.severity = ...</c>),
/// and rule documentation. Keep stable — third-party suppression files reference these.
/// </summary>
internal static class DiagnosticCategories
{
    /// <summary>UWP → WinUI 3 API compatibility. IDs WUI0001–WUI0999.</summary>
    public const string Compatibility = "WinUI.Compatibility";

    /// <summary>Migration suggestions sourced from the WinAppSDK migration tables. IDs WUI1000–WUI1999.</summary>
    public const string Migration = "WinUI.Migration";

    /// <summary>Runtime / layout / XAML pitfalls. IDs WUI2000–WUI2999.</summary>
    public const string Runtime = "WinUI.Runtime";

    /// <summary>MVVM and CommunityToolkit.Mvvm patterns. IDs WUI3000–WUI3999.</summary>
    public const string Mvvm = "WinUI.Mvvm";

    /// <summary>Interop (WebView2, COM, AI). IDs WUI4000–WUI4999.</summary>
    public const string Interop = "WinUI.Interop";
}
