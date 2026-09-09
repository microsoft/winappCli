// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Services;

/// <summary>
/// Sanitizes NuGet error text before it reaches the user. NuGet embeds the full source URL in its messages
/// (e.g. "Unable to load the service index for source https://host/v3/index.json?sig=..."), and feeds
/// commonly authenticate with a signed query string or embedded user-info, so forwarding that text verbatim
/// can publish a credential to the console and into CI logs. The source NAME and the failure reason are what
/// makes these messages actionable, and both are preserved.
/// </summary>
internal static partial class NugetErrorMessage
{
    // Matches an absolute URI up to the first whitespace. Deliberately permissive about the tail: the exact
    // extent is decided below after trimming sentence punctuation, so it never has to encode which characters
    // a query value may contain.
    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s]+", RegexOptions.ExplicitCapture)]
    private static partial Regex UriPattern { get; }

    /// <summary>
    /// Returns <paramref name="message"/> with any embedded URI's user-info and query string removed. URIs
    /// carrying neither are left byte-for-byte intact, so the common case reads exactly as NuGet wrote it.
    /// Safe to apply more than once: a message that has already been redacted has no secrets left to remove.
    /// </summary>
    internal static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        return UriPattern.Replace(message, static match =>
        {
            var raw = match.Value;

            // NuGet ends a sentence directly after the URL ("...index.json.") and callers may wrap it in
            // quotes or parentheses. Those characters are legal in a URI, so they must be split off rather
            // than parsed as part of it — otherwise the trailing period lands inside the redacted output.
            // '>' is deliberately NOT trimmed: it terminates the "?<redacted>" marker written below, and
            // trimming it would let a second pass eat into the marker and append another one.
            var trimmed = raw.TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '"');
            var punctuation = raw[trimmed.Length..];

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return raw;
            }

            var hasUserInfo = !string.IsNullOrEmpty(uri.UserInfo);
            var hasQuery = !string.IsNullOrEmpty(uri.Query) && uri.Query != "?";
            if (!hasUserInfo && !hasQuery)
            {
                return raw;
            }

            // Rebuilt from components rather than via GetLeftPart/UriBuilder because those keep user-info in
            // the authority. Scheme, host, non-default port and path are all non-secret and worth keeping so
            // the user can still tell which feed failed.
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            var sanitized = $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
            if (hasQuery)
            {
                sanitized += "?<redacted>";
            }

            return sanitized + punctuation;
        });
    }
}
