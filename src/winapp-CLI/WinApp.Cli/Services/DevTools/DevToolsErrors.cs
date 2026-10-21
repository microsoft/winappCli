// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Turns a structured DevTools failure into the one sentence a user (or an agent) can act on. The tap's stable
/// token is the branch key; the tap's own message is kept whenever it says more than the generic advice, so
/// the app is never paraphrased into something it did not say.
/// </summary>
internal static class DevToolsErrors
{
    public static string Describe(DevToolsProtocolError error) => error.Token switch
    {
        "not-found" => "That element is not in the live visual tree. Re-run `winapp devtools inspect`: handles change when the tree rebuilds.",
        "stale-handle" => "That handle no longer resolves; the element was removed or the tree rebuilt. Re-run `winapp devtools inspect` for current handles.",
        "no-tree" => "The DevTools agent has no visual tree yet (the app's XAML has not come up, or the subscription did not bind).",
        "not-ready" => "The DevTools agent is still warming up its visual-tree census. Try again in a moment.",
        "refused-unsafe" =>
            $"{error.Message} The posture is fixed by the FIRST injection for the process lifetime. Restart the app and let winapp attach it.",
        "unauthorized" => $"The DevTools pipe refused this connection: {error.Message}",
        "source-unavailable" => "The app was not built with XAML source information, so there is nothing to report here.",
        "no-response" => error.Message,
        "capability-unsupported" => $"The app's DevTools agent does not support that: {error.Message}",
        "unknown" when error.Code == -32601 => error.Message,
        _ => !string.IsNullOrWhiteSpace(error.Message) && error.Message != error.Token
            ? DescribeHresults(error.Message)
            : DescribeToken(error.Token) ?? $"The DevTools agent failed with '{error.Token}'.",
    };

    /// <summary>A plain sentence for a query or command failure token, or null when the token is not known.</summary>
    public static string? DescribeToken(string? token) => token switch
    {
        "no-match" => "No element matched.",
        "incomplete" => "The search did not finish, so the result may be missing elements. Try again, or narrow it with --root or --of-type.",
        "ambiguous" => "More than one element matched. Narrow the selector so it matches one element.",
        "ambiguous-type" => "That short type name matches more than one type. Use the full type name.",
        "unreadable" => "The property could not be read.",
        "target-changed" => "The element changed while it was being read or written. Try again.",
        "property-not-writable" => "That property cannot be set.",
        "write-unconfirmed" => "The value was written, but reading it back did not return the new value.",
        "stale-root" or "missing-root" => "The --root element is no longer in the visual tree. Re-run `winapp devtools inspect` for current handles.",
        "not-found" => "That element is not in the live visual tree. Re-run `winapp devtools inspect`: handles change when the tree rebuilds.",
        "stale-handle" => "That handle no longer resolves; the element was removed or the tree rebuilt. Re-run `winapp devtools inspect` for current handles.",
        "outside-window" => "That element belongs to a different window than the one targeted. Target its window with -w, or use a handle from this window.",
        _ when token is not null && token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && DescribeHresults(token) != token
            => $"The app reported an error: {DescribeHresults(token)}.",
        _ => null,
    };

    /// <summary>Why a query stopped early, in plain words. Unknown reasons are returned as they are.</summary>
    public static string DescribeTruncation(string reason) => reason switch
    {
        "census-cap" => "the window has more elements than DevTools tracks",
        "not-ready" => "DevTools is still reading the visual tree; try again in a moment",
        "no-tree" => "DevTools has no visual tree for this app yet",
        "tree-changed" => "the visual tree changed during the search; try again",
        "time-budget" => "the search ran out of time; narrow it with --root or --of-type",
        "value-budget" => "the property values were too large to read in one search; narrow it with --root or --of-type",
        "classifying" => "DevTools is still matching elements to their XAML source; try again in a moment",
        _ => reason,
    };

    private static readonly (string Code, string Meaning)[] KnownHresults =
    [
        ("0x80004005", "general failure"),
        ("0x8000FFFF", "unexpected failure"),
        ("0x80070057", "invalid argument"),
        ("0x80004002", "interface not supported"),
        ("0x802B000A", "XAML could not be parsed"),
    ];

    /// <summary>Adds a plain meaning after each well-known HRESULT in a message, keeping the code for searching.</summary>
    public static string DescribeHresults(string message)
    {
        foreach (var (code, meaning) in KnownHresults)
        {
            var index = message.IndexOf(code, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && !message.Contains(meaning, StringComparison.OrdinalIgnoreCase))
            {
                var end = index + code.Length;
                message = message[..end] + $" ({meaning})" + message[end..];
            }
        }
        return message;
    }
}
