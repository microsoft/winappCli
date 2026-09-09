// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Stable URLs used as <see cref="Microsoft.CodeAnalysis.DiagnosticDescriptor.HelpLinkUri"/>
/// values. Centralized so rule docs and the README link to the same locations.
/// </summary>
internal static class HelpLinks
{
    private const string Base = "https://github.com/microsoft/winappCli/blob/main/src/winapp-Analyzer/RULES.md";

    public static string For(string diagnosticId) =>
        Base + "#" + diagnosticId.ToLowerInvariant();
}
