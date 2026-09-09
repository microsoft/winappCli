// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Per-rule allowlists for known-good APIs that look like a violation but aren't.
/// Generalizes the original ConnectedAnimationService.GetForCurrentView() carve-out so
/// every rule can declaratively exempt named symbols without sprinkling string literals
/// in analyzer logic.
///
/// Add entries here only after verifying with a real-world false positive. New entries
/// require an associated test in <c>Allowlist*</c> tests proving the carve-out works.
/// </summary>
internal static class Allowlists
{
    /// <summary>
    /// Containing-type names that legitimately expose <c>GetForCurrentView()</c> in
    /// WinUI 3 (i.e. callers should NOT be flagged by WUI0004).
    /// </summary>
    public static readonly ImmutableHashSet<string> GetForCurrentViewSafeTypes =
        ImmutableHashSet.Create(
            "ConnectedAnimationService",
            // Reserved: add Microsoft.UI.* types here as Microsoft surfaces new GetForCurrentView()
            // shapes that remain valid post-migration.
            "Microsoft.UI.Xaml.Controls.ConnectedAnimationService"
        );

    /// <summary>
    /// Type names whose <c>Window.Current</c>-shaped API is intentionally still valid
    /// in WinUI 3 (e.g. user types named <c>Window</c> in unrelated namespaces).
    /// We exempt by full type display string.
    /// </summary>
    public static readonly ImmutableHashSet<string> WindowCurrentSafeTypes =
        ImmutableHashSet<string>.Empty
            // (none today — every Window.Current in WinUI 3 must be migrated)
            ;

    /// <summary>
    /// User namespaces that legitimately START WITH "Windows.UI.Xaml" but aren't the UWP
    /// XAML namespace. Used to avoid flagging contoso/3rd-party namespaces. Treated as a
    /// prefix denylist (substring matches return as not-a-violation).
    /// </summary>
    public static readonly ImmutableHashSet<string> UwpXamlNamespaceFalseFriends =
        ImmutableHashSet.Create(
            // Any namespace fragment that doesn't belong to the actual UWP XAML namespace
            // but whose name starts with "Windows.UI.Xaml" (extremely rare in practice).
            "Contoso.Windows.UI.Xaml"
        );

    /// <summary>
    /// Member names that, when called on an unrelated container type, must NOT trigger
    /// WUI4001 (WebView2 NavigateToString). Ensures we don't fire on
    /// <c>Router.Navigate(...)</c>-style APIs.
    /// </summary>
    public static readonly ImmutableHashSet<string> WebView2RequiredContainingTypes =
        ImmutableHashSet.Create("WebView2", "Microsoft.UI.Xaml.Controls.WebView2");
}
