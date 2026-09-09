// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Parses the stdout of <c>dotnet build/msbuild --getProperty:...</c> into a property dictionary.
/// </summary>
/// <remarks>
/// The dotnet SDK returns a raw scalar for a <b>single</b> <c>--getProperty</c>, or JSON
/// (<c>{ "Properties": { "Name": "Value", ... } }</c>) for <b>multiple</b>. This helper accepts either
/// shape. Pure and side-effect free.
/// </remarks>
internal static class MsBuildPropertyReader
{
    /// <summary>
    /// Parses an MSBuild boolean property that may be undeclared or malformed.
    /// </summary>
    /// <param name="value">The evaluated property value; an unset property evaluates to an empty string.</param>
    /// <param name="malformed">
    /// <see langword="true"/> when the value is neither empty nor a valid boolean, so the caller can report
    /// the typo. The return is <see langword="null"/> in that case.
    /// </param>
    /// <returns>The declared preference, or <see langword="null"/> for "no preference".</returns>
    /// <remarks>
    /// Accepts exactly <c>true</c>/<c>false</c>, case-insensitively, because that is what the NuGet targets'
    /// <c>'$(Prop)' == 'true'</c> conditions match. Anything else has to read as UNSET rather than false:
    /// the targets forward no switch at all for a value they do not recognize, so treating
    /// <c>WinAppRunUseExecutionAlias=ture</c> as an explicit <c>false</c> here would make the same typo mean
    /// opposite things through <c>dotnet run</c> and through a direct <c>winapp run</c>.
    /// </remarks>
    public static bool? ParseOptionalBoolean(string? value, out bool malformed)
    {
        malformed = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        malformed = true;
        return null;
    }

    /// <summary>
    /// Parses <paramref name="stdout"/> for the requested properties into a case-insensitive map (values
    /// may be empty; missing properties are absent). When exactly one name is requested and the output
    /// isn't JSON, the whole trimmed output is that property's value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string stdout, IReadOnlyList<string> requestedNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stdout is null)
        {
            return result;
        }

        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return result;
        }

        // JSON shape: { "Properties": { "Name": "Value", ... } }. Tolerant of a diagnostic preamble/trailer;
        // only an object that actually carries a "Properties" object is accepted, so a scalar containing '{'
        // is never misread.
        if (TryReadPropertiesObject(trimmed, result))
        {
            return result;
        }

        // Scalar shape: a single requested property whose raw value is the whole output.
        if (requestedNames.Count == 1)
        {
            result[requestedNames[0]] = trimmed;
        }

        return result;
    }

    /// <summary>
    /// Parses the stdout of <c>dotnet build/msbuild --getItem:...</c> into a map of item name to its
    /// <c>Include</c> identities. The SDK emits <c>{ "Items": { "ItemName": [ { "Identity": "…" }, … ] } }</c>.
    /// Tolerant of a diagnostic preamble/trailer like <see cref="Parse"/>; pure and side-effect free.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseItems(string stdout)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return result;
        }

        TryReadItemsObject(stdout.Trim(), result);
        return result;
    }

    /// <summary>
    /// Scans <paramref name="text"/> for the first JSON object carrying an <c>"Items"</c> object and fills
    /// <paramref name="result"/> with each group's identities.
    /// </summary>
    private static void TryReadItemsObject(string text, Dictionary<string, IReadOnlyList<string>> result)
    {
        TryScanJsonObject(text, root =>
        {
            if (!root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var group in items.EnumerateObject().Where(g => g.Value.ValueKind == JsonValueKind.Array))
            {
                var identities = new List<string>();
                foreach (var item in group.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("Identity", out var id) &&
                        id.ValueKind == JsonValueKind.String)
                    {
                        var value = id.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            identities.Add(value);
                        }
                    }
                }

                result[group.Name] = identities;
            }

            return true;
        });
    }

    /// <summary>
    /// Scans <paramref name="text"/> for the first <c>{ "Properties": {...} }</c> envelope and, if found,
    /// fills <paramref name="result"/> and returns <c>true</c>.
    /// </summary>
    private static bool TryReadPropertiesObject(string text, Dictionary<string, string> result)
    {
        return TryScanJsonObject(text, root =>
        {
            if (!root.TryGetProperty("Properties", out var props) || props.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in props.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();
            }

            return true;
        });
    }

    /// <summary>
    /// Scans <paramref name="text"/> for the first <c>'{'</c> that begins a valid JSON object which
    /// <paramref name="tryHandle"/> accepts, skipping non-JSON preamble braces and ignoring trailing
    /// diagnostics. <paramref name="tryHandle"/> must copy out any values before returning, as the backing
    /// <see cref="JsonDocument"/> is disposed immediately after. Returns whether an object was handled.
    /// </summary>
    private static bool TryScanJsonObject(string text, Func<JsonElement, bool> tryHandle)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var braceIndex = text.IndexOf('{', searchStart);
            if (braceIndex < 0)
            {
                return false;
            }

            var candidate = text[braceIndex..];
            var bytes = System.Text.Encoding.UTF8.GetBytes(candidate);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            try
            {
                if (JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object && tryHandle(doc.RootElement))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // This '{' did not begin a valid JSON value — try the next one (preamble brace).
            }

            searchStart = braceIndex + 1;
        }

        return false;
    }
}
