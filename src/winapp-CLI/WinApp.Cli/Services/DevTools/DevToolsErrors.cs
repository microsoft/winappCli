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
        "not-found" => "That element is not in the live visual tree. Re-run `winapp devtools inspect` — handles change when the tree rebuilds.",
        "stale-handle" => "That handle no longer resolves; the element was removed or the tree rebuilt. Re-run `winapp devtools inspect` for current handles.",
        "no-tree" => "The DevTools agent has no visual tree yet (the app's XAML has not come up, or the subscription did not bind).",
        "not-ready" => "The DevTools agent is still warming up its visual-tree census. Try again in a moment.",
        "refused-unsafe" =>
            $"{error.Message} The posture is fixed by the FIRST injection for the process lifetime — restart the app and let winapp attach it.",
        "unauthorized" => $"The DevTools pipe refused this connection: {error.Message}",
        "source-unavailable" => "The app was not built with XAML source information, so there is nothing to report here.",
        "no-response" => error.Message,
        "capability-unsupported" => $"The app's DevTools agent does not support that: {error.Message}",
        "unknown" when error.Code == -32601 => error.Message,
        _ => string.IsNullOrWhiteSpace(error.Message)
            ? $"The DevTools agent failed with '{error.Token}'."
            : $"{error.Message} ({error.Token})",
    };
}
