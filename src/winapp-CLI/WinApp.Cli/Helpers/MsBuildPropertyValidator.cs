// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Validates repeatable <c>-p Name=Value</c> command-line properties before they become MSBuild
/// arguments. Pure and side-effect free.
/// </summary>
/// <remarks>
/// Shared by <c>run</c> and <c>unregister</c> so both reject the same malformed input with the same
/// message. <c>unregister</c> accepts <c>-p</c> because a command-line property overrides a file-based
/// app's own <c>#:property</c> directives and can therefore change the identity it registers under, so
/// removing that registration needs the same properties the run used.
/// </remarks>
internal static class MsBuildPropertyValidator
{
    /// <summary>
    /// Returns an actionable error message for the first malformed property, or <see langword="null"/>
    /// when every property is well-formed.
    /// </summary>
    /// <remarks>
    /// Error text names the PROPERTY ONLY, never its value, because a value can hold a secret.
    /// </remarks>
    public static string? Validate(IReadOnlyList<string> properties)
    {
        foreach (var property in properties)
        {
            // MSBuild splits a -p token on ';' or ',' into MULTIPLE properties, which would smuggle a
            // dedicated-flag property (e.g. RuntimeIdentifier) past the name-only ForwardableProperties
            // filter and override the arch winapp conveys via the RID. Reject packing; '%3B' and '%2C'
            // escape a literal separator inside one value.
            if (property.IndexOfAny([';', ',']) >= 0)
            {
                var name = Describe(property[..property.IndexOfAny(['=', ';', ','])]);
                return $"Invalid --property {name}. A single -p cannot pack multiple properties with ';' or ','. " +
                       "Pass one property per repeatable -p (for example: -p A=1 -p B=2), or escape a literal separator in a value as '%3B' or '%2C'.";
            }

            var separator = property.IndexOf('=');
            if (separator <= 0 || string.IsNullOrWhiteSpace(property[..separator]))
            {
                // No '=' at all: echo what was typed, which is the whole token. Otherwise show the name
                // part, which Describe renders as a sentinel when it is blank.
                var shown = separator >= 0 ? property[..separator] : property;
                return $"Invalid --property {Describe(shown)}. Expected Name=Value (for example: -p WindowsPackageType=None).";
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a property name for an error message, as <c>(empty)</c> when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// A leading <c>;</c> or <c>=</c> leaves the name empty, and <c>Invalid --property ''</c> gives the
    /// user nothing to act on.
    /// </remarks>
    private static string Describe(string name) =>
        string.IsNullOrWhiteSpace(name) ? "(empty)" : $"'{name}'";
}
