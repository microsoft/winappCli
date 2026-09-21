// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure helper for publisher distinguished name (DN) operations:
/// validation, normalization, display-name extraction, and XML-safe formatting.
/// </summary>
internal static class PublisherDnHelper
{
    /// <summary>
    /// Returns true if the input is already a valid X.500 distinguished name
    /// (e.g., "CN=Name", "OU=Finance, DC=corp, DC=com").
    /// </summary>
    public static bool IsDistinguishedName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        try
        {
            _ = new X500DistinguishedName(input);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes publisher input to a valid X.500 distinguished name.
    /// If the input is already a valid DN, it is returned as-is.
    /// Bare names (without an attribute type prefix) are wrapped with CN=.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the input is empty, contains an empty-valued DN component (e.g. "CN="),
    /// or looks like a distinguished name but cannot be parsed as one (e.g. "CN=A,,O=B").
    /// </exception>
    public static string Normalize(string publisher)
    {
        if (!TryNormalize(publisher, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(publisher));
        }
        return normalized;
    }

    /// <summary>
    /// Attempts to normalize publisher input to a valid X.500 distinguished name without throwing.
    /// Returns false with a user-facing <paramref name="error"/> message (naming the offending
    /// component where applicable) when the input cannot be turned into a usable publisher DN.
    /// </summary>
    public static bool TryNormalize(
        string? publisher,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(publisher))
        {
            error = "Publisher name cannot be empty.";
            return false;
        }

        // Strip wrapper quotes only if the entire value is enclosed in matching quotes.
        // Do NOT strip individual quote characters — they may be part of a valid DN value
        // (e.g., CN="Company, Inc." has intentional internal quotes).
        var trimmed = publisher.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Publisher name cannot be empty.";
            return false;
        }

        // The MSIX package manifest publisher type (ST_Publisher_2010_v2) has no escape sequences,
        // so a backslash — whether a literal character or an X.500 escape (e.g. "CN=Contoso\Bar" or
        // "CN=Contoso\, Inc") — can never match Identity/@Publisher. Reject it before both the
        // parsed-DN and bare-name paths rather than emit a certificate that silently will not match.
        if (trimmed.Contains('\\'))
        {
            error = $"Publisher '{trimmed}' contains a backslash, which the MSIX package manifest " +
                    "publisher cannot represent. Remove the backslash so the certificate can match " +
                    "the manifest Identity/@Publisher.";
            return false;
        }

        if (IsDistinguishedName(trimmed))
        {
            // .NET's X500DistinguishedName accepts ';' as an RDN separator equivalent to ',', but the
            // raw string we return keeps the literal ';'. The certificate re-parses it into
            // comma-separated RDNs while the manifest keeps the literal ';', so the two artifacts
            // silently diverge and never match. Reject an unquoted separator ';'; a ';' inside a
            // quoted value (e.g. CN="A;B") is data, not a separator, and is kept.
            if (ContainsUnquotedSemicolon(trimmed))
            {
                error = $"Publisher '{trimmed}' uses ';' to separate components, which the MSIX package " +
                        "manifest publisher cannot represent. Separate components with commas, for example " +
                        "'CN=Contoso, O=Contoso, C=US'.";
                return false;
            }

            // A trailing ',' or '+' parses as a valid DN, but X500DistinguishedName silently drops the
            // resulting empty RDN when it encodes the certificate (so "CN=A," becomes subject "CN=A").
            // The manifest keeps the literal trailing separator, so the two artifacts diverge and never
            // match. EnumerateRelativeDistinguishedNames() also omits that empty element, so the
            // component check below cannot see it — reject it here from the raw string instead.
            if (EndsWithUnquotedSeparator(trimmed))
            {
                error = $"Publisher '{trimmed}' ends with a stray ',' or '+' separator, which the " +
                        "certificate drops but the manifest keeps, so the two cannot match. Remove the " +
                        "trailing separator.";
                return false;
            }

            // A DN can parse yet still carry an empty value (e.g. "CN=" or "CN=A, O="), or use a
            // multi-valued RDN (e.g. "CN=A+O="). Such a publisher can never match a manifest
            // Identity/@Publisher, so reject it rather than accept it silently.
            if (!TryValidateDnComponents(trimmed, out var componentError))
            {
                error = componentError;
                return false;
            }

            normalized = trimmed;
            return true;
        }

        // Not a valid DN. If the input begins with a distinguished-name attribute assignment
        // (e.g. "CN=..." or a leading "="), it is a malformed DN attempt — reject it instead of
        // silently wrapping the whole string as a literal CN value (which produced a certificate
        // matching nothing the user asked for). A plain name that merely contains '=' further in
        // (e.g. "R&D = Team") is treated as a bare name and wrapped.
        if (LooksLikeDistinguishedNameAttempt(trimmed))
        {
            error = $"Publisher '{trimmed}' is not a valid distinguished name (DN). " +
                    "Use a plain name (wrapped as CN=<name>) or a valid DN such as 'CN=Contoso, O=Contoso, C=US'.";
            return false;
        }

        // Bare name: wrap with CN= using the builder to properly escape commas, special chars, etc.
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(trimmed);
        normalized = builder.Build().Name;
        return true;
    }

    /// <summary>
    /// Validates that every component of an already-parsed distinguished name carries a value and
    /// is single-valued. Returns false with a user-facing <paramref name="error"/> naming the
    /// offending component (or the multi-valued RDN) otherwise.
    /// </summary>
    private static bool TryValidateDnComponents(string distinguishedName, [NotNullWhen(false)] out string? error)
    {
        error = null;
        try
        {
            var x500 = new X500DistinguishedName(distinguishedName);
            foreach (var rdn in x500.EnumerateRelativeDistinguishedNames())
            {
                // Multi-valued RDNs (a+b) are never used for a certificate publisher and can hide an
                // empty value inside them, so reject them outright rather than trying to match one.
                if (rdn.HasMultipleElements)
                {
                    error = $"Publisher '{distinguishedName}' uses a multi-valued relative distinguished " +
                            "name (RDN), which is not supported for a certificate publisher. " +
                            "Use single-valued components such as 'CN=Contoso, O=Contoso'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(rdn.GetSingleElementValue()))
                {
                    var type = rdn.GetSingleElementType();
                    error = $"Publisher '{distinguishedName}' has an empty '{type.FriendlyName ?? type.Value ?? "?"}' value. " +
                            $"Provide a value, for example '{type.FriendlyName ?? "CN"}=Contoso'.";
                    return false;
                }
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // IsDistinguishedName accepted the string but decoding an individual component failed
            // (e.g. an ill-formed character). Treat it as an unusable DN rather than throwing out of
            // this non-throwing API.
            error = $"Publisher '{distinguishedName}' is not a valid distinguished name (DN). " +
                    "Use a plain name (wrapped as CN=<name>) or a valid DN such as 'CN=Contoso, O=Contoso, C=US'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when the input begins like an X.500 distinguished name — a leading attribute
    /// type (e.g. "CN", "O", a numeric OID, or an "OID."-prefixed OID) immediately followed by '=',
    /// or a leading '=' with an empty attribute type. A plain name whose first '=' is preceded by
    /// non-attribute text (e.g. "R&amp;D = Team") returns false so it is wrapped as a bare CN.
    /// </summary>
    private static bool LooksLikeDistinguishedNameAttempt(string value)
    {
        var equalsIndex = value.IndexOf('=');
        if (equalsIndex < 0)
        {
            return false;
        }

        var attributeType = value[..equalsIndex].Trim();
        if (attributeType.Length == 0)
        {
            return true; // e.g. "=Contoso"
        }

        // X.500 attribute types are short alphabetic keywords (CN, O, OU, DC, ...) or an object
        // identifier, which .NET accepts either bare (2.5.4.3) or with the standard "OID." prefix
        // (OID.2.5.4.3). Anything else before the first '=' is part of a bare name, not a DN attempt.
        var isAlphaKeyword = attributeType.All(char.IsAsciiLetter);
        var oidBody = attributeType.StartsWith("OID.", StringComparison.OrdinalIgnoreCase)
            ? attributeType[4..]
            : attributeType;
        var isOid = oidBody.Length > 0 && oidBody.All(c => char.IsAsciiDigit(c) || c == '.') && oidBody.Any(char.IsAsciiDigit);
        return isAlphaKeyword || isOid;
    }

    /// <summary>
    /// Returns true when the value contains a ';' outside of a quoted segment. .NET parses an
    /// unquoted ';' as an X.500 RDN separator (like ','), but a caller that keeps the raw string
    /// preserves the literal ';', so an unquoted separator semicolon is rejected. A ';' inside a
    /// quoted value is data and returns false. Backslash escapes are rejected earlier, so they are
    /// not considered here.
    /// </summary>
    private static bool ContainsUnquotedSemicolon(string value)
    {
        var inQuotes = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ';' && !inQuotes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the value's last significant (non-whitespace) character is an unquoted ','
    /// or '+' RDN separator. Such a trailing separator parses as a valid DN but is dropped when the
    /// certificate is encoded, so it is rejected. A separator inside a quoted value is data and does
    /// not count. Backslash escapes are rejected earlier, so they are not considered here.
    /// </summary>
    private static bool EndsWithUnquotedSeparator(string value)
    {
        var inQuotes = false;
        var lastChar = '\0';
        var lastCharQuoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                lastChar = c;
                lastCharQuoted = false;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                continue; // trailing or structural whitespace is not significant
            }
            else
            {
                lastChar = c;
                lastCharQuoted = inQuotes;
            }
        }

        return !lastCharQuoted && (lastChar == ',' || lastChar == '+');
    }

    /// <summary>
    /// Extracts a human-friendly display name from a distinguished name.
    /// For simple CN-only DNs, returns the CN value (e.g., "CN=Foo" → "Foo").
    /// For multi-component or non-CN DNs, returns the full DN unchanged.
    /// </summary>
    public static string GetDisplayName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return distinguishedName;
        }

        // If it contains a comma, it's multi-component — return full DN
        // But first check if the comma is inside quotes (escaped)
        if (HasMultipleComponents(distinguishedName))
        {
            return distinguishedName;
        }

        // Single-component: strip the attribute type prefix if it's CN=
        if (distinguishedName.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            var value = distinguishedName[3..];
            // Strip surrounding quotes from the value if present
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            return value;
        }

        // Non-CN single-component (e.g., "OU=Finance") — return full DN
        return distinguishedName;
    }

    /// <summary>
    /// Returns true if the DN has multiple RDN components (unquoted commas).
    /// </summary>
    private static bool HasMultipleComponents(string dn)
    {
        bool inQuotes = false;
        for (int i = 0; i < dn.Length; i++)
        {
            char c = dn[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\\' && i + 1 < dn.Length)
            {
                i++; // skip escaped character
            }
            else if (c == ',' && !inQuotes)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// XML-escapes a DN value so it is safe to embed inside an XML attribute.
    /// Handles quotes, ampersands, angle brackets, etc.
    /// </summary>
    public static string XmlEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
