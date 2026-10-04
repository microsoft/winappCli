// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsSlug
{
    /// <summary>
    /// Width of the tap's identity token. A UI Automation slug carries 4 hex digits over a tree of hundreds
    /// of elements; a DevTools census reaches 20,000, where 16 bits collides essentially always. The tap mints 40
    /// bits (<c>DevToolsTreeLayout::kIdentityHexDigits</c>) and the two widths must agree — a slug the CLI cannot
    /// parse would silently degrade to a text query and resolve the wrong element.
    /// </summary>
    public const int IdentityHexDigits = 10;

    public static string? For(string type, string? name, string? id)
    {
        if (!IsIdentity(id))
        {
            return null;
        }

        var typeToken = NormalizeType(type);
        var nameToken = SlugGenerator.Normalize(name);
        return nameToken is null ? $"{typeToken}-{id}" : $"{typeToken}-{nameToken}-{id}";
    }

    internal readonly record struct Parsed(string TypeToken, string? NameToken, string Identity);

    /// <summary>
    /// Parses a selector as a slug, or returns <c>null</c> when it is not one. The identity suffix is what
    /// makes this unambiguous: a bare <c>x:Name</c> like <c>submit-button</c> has no trailing 10 hex digits,
    /// so it can never be mistaken for a slug and silently validated against the wrong thing.
    /// </summary>
    public static Parsed? Parse(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        var parts = selector.Trim().Split('-');
        if (parts.Length < 2)
        {
            return null;
        }

        var identity = parts[^1];
        if (!IsIdentity(identity))
        {
            return null;
        }

        var typeToken = parts[0];
        if (typeToken.Length == 0 || !typeToken.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        // A normalized name never contains '-' (SlugGenerator.Normalize strips everything but [a-z0-9]), so a
        // middle section is the name whole. Joining rather than rejecting keeps a hand-typed selector with an
        // unexpected dash resolvable-or-refused rather than silently reinterpreted as a text query.
        var nameToken = parts.Length > 2 ? string.Join('-', parts[1..^1]) : null;
        return new Parsed(typeToken.ToLowerInvariant(), nameToken, identity);
    }

    /// <summary>
    /// Whether a live element matches a parsed slug. Type and name narrow; the identity DECIDES. A match on
    /// type and name with a different identity is the stale case and must be reported as such rather than
    /// accepted — that element is at a different place in the tree than the one the selector named.
    /// </summary>
    public static bool Matches(Parsed slug, string type, string? name, string? id) =>
        string.Equals(id, slug.Identity, StringComparison.OrdinalIgnoreCase)
        && NormalizeType(type) == slug.TypeToken
        && SlugGenerator.Normalize(name) == slug.NameToken;

    public static bool IsNearMiss(Parsed slug, string type, string? name, string? id) =>
        !string.Equals(id, slug.Identity, StringComparison.OrdinalIgnoreCase)
        && NormalizeType(type) == slug.TypeToken
        && SlugGenerator.Normalize(name) == slug.NameToken;

    public static string NormalizeType(string? type)
    {
        var shortType = DevToolsFormat.ShortTypeName(type);
        var builder = new StringBuilder(shortType.Length);
        foreach (var c in shortType)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.Length == 0 ? "element" : builder.ToString();
    }

    private static bool IsIdentity(string? token) =>
        token is { Length: IdentityHexDigits } && token.All(char.IsAsciiHexDigitLower);
}
