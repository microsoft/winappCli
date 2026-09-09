// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Centralized diagnostic IDs. See <c>RULES.md</c> for the full catalog and the
/// documented ID-range methodology. IDs are immutable: once a number is assigned
/// it is never reused, even if the rule is removed.
/// </summary>
internal static class DiagnosticIds
{
    // ─── WUI0xxx — UWP → WinUI 3 API compatibility ───────────────────────────
    public const string UwpXamlNamespace   = "WUI0001"; // ex-WUI003
    public const string WindowCurrent      = "WUI0002"; // ex-WUI004
    public const string CoreDispatcher     = "WUI0003"; // ex-WUI005
    public const string GetForCurrentView  = "WUI0004"; // ex-WUI006

    // ─── WUI1xxx — Migration-table data-driven rules (Microsoft Learn) ───────
    // Single data-driven analyzer; payload comes from ApiMappings.g.cs / FeatureMappings.g.cs.
    public const string ApiMappingMatch     = "WUI1001"; // UWP API has WinAppSDK equivalent (Warning, gated by context)
    public const string ApiMappingNoEquiv   = "WUI1002"; // UWP API has no WinAppSDK equivalent (Warning, gated by context)
    public const string FeatureMappingHint  = "WUI1010"; // Migration feature-area hint (Info, gated by context)

    // ─── WUI2xxx — Runtime / layout / XAML pitfalls ──────────────────────────
    // 200x = layout/control-content
    public const string TabViewRawContent          = "WUI2001"; // ex-WUI001
    public const string TabViewRawContentXaml      = "WUI2002"; // ex-WUI021 (cross-file variant)
    public const string UwpOnlyXamlControl         = "WUI2003"; // UWP-only XAML control with no WinUI 3 equivalent
    // 201x = XAML binding (x:Bind)
    public const string XBindNestedNoFallback      = "WUI2010"; // ex-WUI007
    public const string XBindMissingMode           = "WUI2011"; // ex-WUI011
    public const string NullConverter              = "WUI2012"; // ex-WUI016
    // 202x = accessibility
    public const string MissingAutomationId        = "WUI2020"; // ex-WUI010
    // 203x = code-behind / attached-property syntax
    public const string AttachedPropertyInitializer = "WUI2030"; // ex-WUI012

    // ─── WUI3xxx — MVVM patterns ─────────────────────────────────────────────
    public const string OldMvvmSyntax = "WUI3001"; // ex-WUI008

    // ─── WUI4xxx — Interop (WebView2, COM, AI) ───────────────────────────────
    // 400x = WebView2
    public const string WebView2NoInit     = "WUI4001"; // ex-WUI002
    public const string WebView2NoInitXaml = "WUI4002"; // ex-WUI020 (cross-file variant)
    // 410x = ONNX Runtime GenAI (subgroup)
    public const string GenAiSetInputSequences   = "WUI4101"; // ex-WUI013
    public const string GenAiComputeLogits       = "WUI4102"; // ex-WUI014
    public const string GenAiTokenizerStreamCtor = "WUI4103"; // ex-WUI015
}
