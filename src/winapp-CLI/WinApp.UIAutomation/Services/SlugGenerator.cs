// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Generates deterministic, shell-safe, token-efficient semantic slugs for UIA elements.
/// Format: prefix-normalizedname-hash (e.g., btn-minimize-c4b9)
/// </summary>
internal static partial class SlugGenerator
{
    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumericRegex();

    /// <summary>3-letter type prefix map for token efficiency.</summary>
    private static readonly Dictionary<string, string> TypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] = "btn",
        ["Edit"] = "txt",
        ["TextBox"] = "txt",
        ["CheckBox"] = "chk",
        ["ComboBox"] = "cmb",
        ["ListItem"] = "itm",
        ["TabItem"] = "tab",
        ["Tab"] = "tab",
        ["Image"] = "img",
        ["Text"] = "lbl",
        ["TextBlock"] = "lbl",
        ["Pane"] = "pn",
        ["Window"] = "win",
        ["Group"] = "grp",
        ["Hyperlink"] = "lnk",
        ["MenuItem"] = "mnu",
        ["MenuBar"] = "mnb",
        ["Menu"] = "mnu",
        ["List"] = "lst",
        ["Tree"] = "tre",
        ["TreeItem"] = "tri",
        ["DataGrid"] = "grd",
        ["DataItem"] = "dat",
        ["RadioButton"] = "rdo",
        ["Slider"] = "sld",
        ["ProgressBar"] = "prg",
        ["ScrollBar"] = "scr",
        ["ToolBar"] = "tlb",
        ["StatusBar"] = "stb",
        ["TitleBar"] = "ttl",
        ["SplitButton"] = "spl",
        ["Separator"] = "sep",
        ["Document"] = "doc",
        ["Header"] = "hdr",
        ["HeaderItem"] = "hdi",
        ["ToolTip"] = "tip",
        ["Thumb"] = "thm",
        ["Table"] = "tbl",
    };

    /// <summary>Maps a UIA ControlType name to a 3-letter prefix.</summary>
    public static string GetPrefix(string controlType)
    {
        return TypePrefixes.GetValueOrDefault(controlType, "elm");
    }

    /// <summary>Maps a 3-letter prefix back to UIA ControlType names (for resolution).</summary>
    public static string[] GetTypesForPrefix(string prefix)
    {
        var types = new List<string>();
        foreach (var kvp in TypePrefixes)
        {
            if (kvp.Value == prefix)
            {
                types.Add(kvp.Key);
            }
        }
        return types.Count > 0 ? types.ToArray() : ["Unknown"];
    }

    /// <summary>Normalizes a string to lowercase alphanumeric, max 15 chars.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var cleaned = NonAlphanumericRegex().Replace(input.ToLowerInvariant(), "");
        return cleaned.Length > 15 ? cleaned[..15] : cleaned.Length > 0 ? cleaned : null;
    }

    /// <summary>Computes a 4-char hex hash from a UIA RuntimeId array.</summary>
    public static string ComputeHash(int[] runtimeId)
    {
        unchecked
        {
            var hash = 17;
            foreach (var id in runtimeId)
            {
                hash = hash * 31 + id;
            }

            return ((uint)hash).ToString("x8")[4..8];
        }
    }

    /// <summary>Computes hash directly from a SAFEARRAY* RuntimeId (CsWin32 COM interop).</summary>
    public static unsafe string ComputeHashFromSafeArray(global::Windows.Win32.System.Com.SAFEARRAY* safeArray)
    {
        if (safeArray == null)
        {
            return "0000";
        }

        var bound = safeArray->rgsabound[0];
        var count = (int)bound.cElements;
        var data = (int*)safeArray->pvData;

        unchecked
        {
            var hash = 17;
            for (var i = 0; i < count; i++)
            {
                hash = hash * 31 + data[i];
            }

            return ((uint)hash).ToString("x8")[4..8];
        }
    }

    /// <summary>
    /// Generates the full semantic slug from a SAFEARRAY RuntimeId (CsWin32 COM interop).
    /// </summary>
    public static unsafe string GenerateSlugFromSafeArray(string controlType, string? automationId, string? name, global::Windows.Win32.System.Com.SAFEARRAY* runtimeId)
    {
        var prefix = GetPrefix(controlType);
        var hash = ComputeHashFromSafeArray(runtimeId);
        var normalized = Normalize(automationId) ?? Normalize(name);

        return normalized is not null
            ? $"{prefix}-{normalized}-{hash}"
            : $"{prefix}-{hash}";
    }

    /// <summary>
    /// Parses a slug string into its components.
    /// Returns (prefix, nameSlug, hash) or null if not a valid slug format.
    /// </summary>
    public static (string Prefix, string? NameSlug, string Hash)? ParseSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // Must be lowercase, contain dashes, end with 4 hex chars
        // Format: prefix-hash or prefix-name-hash
        var parts = input.Split('-');
        if (parts.Length < 2)
        {
            return null;
        }

        var lastPart = parts[^1];
        if (lastPart.Length != 4 || !IsHex(lastPart))
        {
            return null;
        }

        var prefix = parts[0];
        if (!TypePrefixes.ContainsValue(prefix) && prefix != "elm")
        {
            return null;
        }

        var hash = lastPart;
        string? nameSlug = parts.Length > 2
            ? string.Join("-", parts[1..^1])  // Handle multi-part names (shouldn't happen with normalization, but safe)
            : null;

        return (prefix, nameSlug, hash);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
}
