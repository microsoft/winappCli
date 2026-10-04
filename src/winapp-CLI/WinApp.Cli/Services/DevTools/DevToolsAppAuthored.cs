// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// The tap's app-authored classification (<c>VisualTree.getAppAuthored</c>): which live elements the
/// classifier attributes to the app's own XAML, and — the part that matters when the answer is "none" —
/// whether the build/launch carried the XAML source information the classification needs at all.
/// </summary>
/// <param name="Handles">The handles classified as app-authored.</param>
/// <param name="SourceInstrumented">
/// Whether the runtime reported source for any element. When <c>false</c>, an empty result means "we could
/// not look", not "the app authored nothing" — and those two must never render the same way.
/// </param>
/// <param name="Truncated">Whether the classifier's own node walk was cut short.</param>
internal sealed record DevToolsAppAuthored(
    HashSet<string> Handles,
    bool SourceInstrumented,
    bool Truncated)
{
    public static DevToolsAppAuthored? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("appAuthored", out var handles)
                || handles.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in handles.EnumerateArray())
            {
                if (handle.ValueKind == JsonValueKind.String && handle.GetString() is string value)
                {
                    set.Add(value);
                }
                else
                {
                    return null;
                }
            }

            return new DevToolsAppAuthored(
                set,
                root.TryGetProperty("sourceInstrumented", out var s) && s.ValueKind == JsonValueKind.True,
                root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? ExplainIfEmpty() =>
        Handles.Count > 0
            ? null
            : SourceInstrumented
                ? "The agent classified no element as app-authored: every live element came from framework or template XAML."
                : "This app reports no XAML source information, so nothing can be classified as app-authored. " +
                  "Launch it with `winapp run --devtools`, which turns source information on.";
}
