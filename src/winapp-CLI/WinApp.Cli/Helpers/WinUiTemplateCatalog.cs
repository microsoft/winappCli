// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Whether a <c>dotnet new update --check-only</c> parse authoritatively determined the pack's
/// update state. <see cref="Unrecognized"/> must not be treated as up-to-date: it means the output
/// couldn't be interpreted (empty, truncated, or an unexpected format), so the check should retry
/// rather than be cached.
/// </summary>
internal enum UpdateCheckOutcome
{
    /// <summary>Output couldn't be interpreted; the result is unknown and the check should retry.</summary>
    Unrecognized,

    /// <summary>Output was understood and the pack is current (no newer version offered).</summary>
    UpToDate,

    /// <summary>Output was understood and a newer version is available.</summary>
    UpdateAvailable,
}

/// <summary>
/// A single template row parsed from <c>dotnet new list</c>. <see cref="ShortNames"/> holds every
/// alias dotnet accepts for the template (comma-separated in the source table); <see cref="ShortName"/>
/// is the first (canonical) one that <c>dotnet new &lt;short&gt;</c> is invoked with. <see cref="Type"/>
/// is <c>project</c> or <c>item</c>; <see cref="Tags"/> is the raw <c>Windows/WinUI/...</c> tag path
/// used to derive next-step guidance.
/// </summary>
internal sealed record WinUiTemplateEntry(
    string DisplayName,
    IReadOnlyList<string> ShortNames,
    string Language,
    string Type,
    string Tags)
{
    /// <summary>Canonical short name passed to <c>dotnet new</c> (the first listed alias).</summary>
    public string ShortName => ShortNames.Count > 0 ? ShortNames[0] : string.Empty;

    /// <summary>True when this is a project template (creates a new project), not an item template.</summary>
    public bool IsProject => string.Equals(Type, "project", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this is an item template (added into an existing project).</summary>
    public bool IsItem => string.Equals(Type, "item", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> matches any of this template's short names (case-insensitive).</summary>
    public bool MatchesShortName(string candidate)
        => ShortNames.Any(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when any '/'-separated segment of <see cref="Tags"/> equals <paramref name="segment"/>.</summary>
    public bool HasTag(string segment)
        => Tags.Split('/').Any(s => s.Trim().Equals(segment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the pack flags this template as prerelease/experimental — the Reactor templates carry
    /// an <c>Experimental</c> tag segment (<c>Windows/WinUI/Desktop/Reactor/Experimental</c>) and an
    /// "(Experimental)" suffix in their display name. Both signals are checked because
    /// <c>dotnet new list</c> truncates over-wide columns, so either one alone can be cut off.
    /// </summary>
    public bool IsExperimental
        => HasTag("Experimental") || DisplayName.Contains("(Experimental)", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A template row parsed from a pack's "Templates:" block in <c>dotnet new uninstall</c> output.
/// Unlike <c>dotnet new list</c>, that listing is not column-formatted, so <see cref="DisplayName"/>
/// is never truncated and <see cref="Aliases"/> is authoritative for what the pack actually owns.
/// </summary>
internal sealed record PackTemplateRow(string DisplayName, IReadOnlyList<string> Aliases);

/// <summary>
/// Pure parsing of <c>dotnet</c> template subcommand output. Enumerating a pack's templates and
/// detecting a stale installed pack both require scraping localized, human-oriented tables (there is
/// no machine-readable <c>dotnet new</c> output), so callers must force <c>DOTNET_CLI_UI_LANGUAGE=en</c>
/// before invoking dotnet. Parsing lives here (not in the command) so the brittle table formats can be
/// unit-tested and evolved independently of the command orchestration.
/// </summary>
internal static class WinUiTemplateCatalog
{
    /// <summary>
    /// Parses the fixed-width table emitted by <c>dotnet new list ... --columns-all</c> into template
    /// entries. Column boundaries are taken from the separator row of dashes (the only reliable
    /// delimiter, since several columns — Template Name, Tags — contain spaces), so the parser is
    /// resilient to differing column widths across dotnet versions. Rows that don't line up with the
    /// header (banners, blank lines, the "These templates matched" preamble) are skipped. Returns an
    /// empty list when no table is present.
    /// </summary>
    internal static IReadOnlyList<WinUiTemplateEntry> ParseList(string output)
    {
        var entries = new List<WinUiTemplateEntry>();
        if (string.IsNullOrEmpty(output))
        {
            return entries;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');

        // Locate the dashes separator row; the header is the non-blank line immediately above it.
        var separatorIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsSeparatorRow(lines[i]))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex <= 0)
        {
            return entries;
        }

        var header = lines[separatorIndex - 1];
        var columns = GetColumnRanges(lines[separatorIndex]);
        if (columns.Count == 0)
        {
            return entries;
        }

        // Map each column to a role by its header label, so column order/presence changes across
        // dotnet versions don't silently shift fields (e.g. reading Tags out of the Author column).
        int shortNameCol = -1, languageCol = -1, typeCol = -1, tagsCol = -1, nameCol = -1;
        for (var c = 0; c < columns.Count; c++)
        {
            var label = Slice(header, columns[c]).Trim();
            if (label.Equals("Short Name", StringComparison.OrdinalIgnoreCase))
            {
                shortNameCol = c;
            }
            else if (label.Equals("Template Name", StringComparison.OrdinalIgnoreCase))
            {
                nameCol = c;
            }
            else if (label.Equals("Language", StringComparison.OrdinalIgnoreCase))
            {
                languageCol = c;
            }
            else if (label.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                typeCol = c;
            }
            else if (label.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            {
                tagsCol = c;
            }
        }

        // Short Name is the only field we cannot function without; the rest degrade gracefully.
        if (shortNameCol < 0)
        {
            return entries;
        }

        for (var i = separatorIndex + 1; i < lines.Length; i++)
        {
            var row = lines[i];
            if (string.IsNullOrWhiteSpace(row) || IsSeparatorRow(row))
            {
                continue;
            }

            var shortNamesRaw = Slice(row, columns[shortNameCol]).Trim();
            if (shortNamesRaw.Length == 0)
            {
                continue;
            }

            var shortNames = shortNamesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (shortNames.Length == 0)
            {
                continue;
            }

            entries.Add(new WinUiTemplateEntry(
                DisplayName: nameCol >= 0 ? Slice(row, columns[nameCol]).Trim() : string.Empty,
                ShortNames: shortNames,
                Language: languageCol >= 0 ? Slice(row, columns[languageCol]).Trim() : string.Empty,
                Type: typeCol >= 0 ? Slice(row, columns[typeCol]).Trim() : string.Empty,
                Tags: tagsCol >= 0 ? Slice(row, columns[tagsCol]).Trim() : string.Empty));
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>dotnet new update --check-only</c> output for the given package id. The update table
    /// has three columns (Package, Current, Latest); the Package id contains no spaces, so a simple
    /// whitespace split is sufficient and avoids depending on exact column widths.
    /// <para>
    /// <c>Outcome</c> separates an authoritatively understood result from output we couldn't interpret:
    /// <see cref="UpdateCheckOutcome.UpdateAvailable"/> (our package appears in the table),
    /// <see cref="UpdateCheckOutcome.UpToDate"/> (a recognizable table or the "up-to-date" notice, with
    /// our package absent), or <see cref="UpdateCheckOutcome.Unrecognized"/> (empty, truncated, or an
    /// unexpected format). Callers must not treat <c>Unrecognized</c> as "up-to-date", otherwise an SDK
    /// output-format change would be cached as a successful check and suppress retries.
    /// </para>
    /// </summary>
    internal static (UpdateCheckOutcome Outcome, string? Current, string? Latest) ParseUpdateCheck(string output, string packageId)
    {
        if (string.IsNullOrEmpty(output))
        {
            return (UpdateCheckOutcome.Unrecognized, null, null);
        }

        var recognized = false;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // A dashes separator row (always present under the Package/Current/Latest header) marks a
            // well-formed update table, even when it lists only other packages.
            if (IsSeparatorRow(raw))
            {
                recognized = true;
                continue;
            }

            // "All template packages are up-to-date." is the authoritative "nothing to do" notice when
            // no table is printed. English UI is forced upstream, so this phrase is stable.
            if (line.Contains("up-to-date", StringComparison.OrdinalIgnoreCase))
            {
                recognized = true;
                continue;
            }

            // Data row for our package: "<id>  <current>  <latest>". Header row starts with
            // "Package" and is skipped by the id comparison.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                return (UpdateCheckOutcome.UpdateAvailable, parts[1], parts[2]);
            }
        }

        return recognized
            ? (UpdateCheckOutcome.UpToDate, null, null)
            : (UpdateCheckOutcome.Unrecognized, null, null);
    }

    /// <summary>
    /// Extracts the templates that belong to <paramref name="packageId"/> from a
    /// <c>dotnet new uninstall</c> listing. Each pack nests a "Templates:" block under its package-id
    /// header, one line per template ending in its aliases: e.g.
    /// <c>WinUI Blank Page (Item) (winui-page,winui3-page) C#</c>. The aliases are the last
    /// parenthesised group on the line (the display name may itself contain "(Item)" or
    /// "(Experimental)"), and everything before that group is the template's untruncated display name.
    /// This is the authoritative record of what the exact package owns and how its templates are really
    /// named. Returns an empty list when the package or its Templates block isn't present, so callers
    /// can fall back to the unfiltered <c>dotnet new list</c> results.
    /// </summary>
    internal static IReadOnlyList<PackTemplateRow> ParsePackTemplates(string uninstallListOutput, string packageId)
    {
        var rows = new List<PackTemplateRow>();
        if (string.IsNullOrEmpty(uninstallListOutput) || string.IsNullOrEmpty(packageId))
        {
            return rows;
        }

        var lines = uninstallListOutput.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Scan this package's block (lines indented deeper than the header) for its "Templates:"
            // sub-header, then collect the template rows nested under it (deeper still), stopping when
            // the indent returns to the "Templates:" level (the sibling "Uninstall Command:" block).
            var headerIndent = IndentWidth(lines[i]);
            var templatesIndent = -1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var raw = lines[j];
                if (raw.Trim().Length == 0)
                {
                    continue;
                }

                var indent = IndentWidth(raw);
                if (indent <= headerIndent)
                {
                    break; // next package block
                }

                if (templatesIndent < 0)
                {
                    if (raw.Trim().Equals("Templates:", StringComparison.OrdinalIgnoreCase))
                    {
                        templatesIndent = indent;
                    }

                    continue;
                }

                if (indent <= templatesIndent)
                {
                    break; // left the Templates block (e.g. "Uninstall Command:")
                }

                var aliases = ExtractAliases(raw);
                if (aliases.Length > 0)
                {
                    rows.Add(new PackTemplateRow(ExtractDisplayName(raw), aliases));
                }
            }

            break;
        }

        return rows;
    }

    /// <summary>
    /// Restricts <paramref name="listed"/> (parsed from <c>dotnet new list</c>) to the templates
    /// <paramref name="packRows"/> says the resolved Microsoft pack owns, and replaces each survivor's
    /// display name and aliases with the pack row's authoritative ones.
    /// <para>
    /// All of this is needed because <c>dotnet new list</c> formats into fixed-width columns — it
    /// truncates long names (<c>Reactor NavigationView App (Experim...</c>) and can clip a long alias
    /// list — and because it matches <c>winui</c> by prefix across <em>every</em> installed pack, with
    /// no column saying which pack a row came from. A listed row belongs to the pack only when the
    /// <em>canonical</em> short name we would hand to <c>dotnet new</c> is one the pack owns
    /// <em>and</em> the row's (possibly truncated) name is a prefix of that pack row's real name.
    /// Matching on any alias is not enough: an unrelated pack whose template is
    /// <c>evil,winui</c> shares <c>winui</c>, so it would be offered as an official template and
    /// scaffolded as <c>dotnet new evil</c>.
    /// </para>
    /// This fails closed. Empty <paramref name="packRows"/> means no template's ownership could be
    /// established, so nothing is returned; the caller reports that as an enumeration failure rather
    /// than offering rows of unknown provenance.
    /// </summary>
    internal static IReadOnlyList<WinUiTemplateEntry> RestrictToPack(
        IReadOnlyList<WinUiTemplateEntry> listed, IReadOnlyList<PackTemplateRow> packRows)
    {
        var kept = new List<WinUiTemplateEntry>();
        foreach (var entry in listed)
        {
            var owner = packRows.FirstOrDefault(row =>
                row.Aliases.Contains(entry.ShortName, StringComparer.OrdinalIgnoreCase)
                && NameMatches(entry.DisplayName, row.DisplayName));
            if (owner is not null)
            {
                kept.Add(entry with { DisplayName = owner.DisplayName, ShortNames = owner.Aliases });
            }
        }

        return kept;
    }

    /// <summary>
    /// True when <paramref name="listedName"/> — which <c>dotnet new list</c> may have cut short with a
    /// trailing ellipsis to fit its column — is the start of <paramref name="packName"/>. An empty
    /// listed name matches anything, since the short-name check already established ownership.
    /// </summary>
    private static bool NameMatches(string listedName, string packName)
    {
        var prefix = listedName.Trim();
        if (prefix.EndsWith("...", StringComparison.Ordinal))
        {
            prefix = prefix[..^3].TrimEnd();
        }

        return prefix.Length == 0 || packName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the comma-separated aliases from a <c>dotnet new uninstall</c> template row. The aliases are
    /// the last parenthesised group on the line (a display name may contain earlier parentheses such as
    /// "(Item)"). Returns an empty sequence when no parenthesised group is present.
    /// </summary>
    private static string[] ExtractAliases(string templateRow)
    {
        var open = templateRow.LastIndexOf('(');
        if (open < 0)
        {
            return [];
        }

        var close = templateRow.IndexOf(')', open + 1);
        if (close < 0)
        {
            return [];
        }

        return templateRow[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Pulls the display name from a <c>dotnet new uninstall</c> template row: everything before the
    /// trailing alias group, so <c>Reactor Blank App (Experimental) (reactor,reactor-blank) C#</c>
    /// yields <c>Reactor Blank App (Experimental)</c>. Returns an empty string when no alias group is
    /// present.
    /// </summary>
    private static string ExtractDisplayName(string templateRow)
    {
        var open = templateRow.LastIndexOf('(');
        return open < 0 ? string.Empty : templateRow[..open].Trim();
    }

    /// <summary>Number of leading whitespace characters on <paramref name="line"/>.</summary>
    private static int IndentWidth(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Derives the correct <c>dotnet new</c> target-framework option and value for a template from the
    /// template engine's <c>templatecache.json</c>, so the scaffold pins a TFM the installed SDK can
    /// build without hard-coding either the option name or the supported framework band.
    /// <para>
    /// Returns <c>Found = false</c> when <paramref name="cacheJson"/> does not describe the requested
    /// template (belonging to <paramref name="packageId"/>), so the caller can try another cache file or
    /// fall back to a heuristic. When the template is found, <c>OptionName</c> is the CLI option to pass
    /// (the host <c>longName</c> such as <c>dotnet-version</c>, or the raw symbol name — e.g.
    /// <c>dotnetVersion</c> on older packs — when no host mapping exists, which is why the option name
    /// must not be hard-coded) and <c>Tfm</c> is the chosen framework: <c>net{sdkMajor}.0</c> when the
    /// template offers it, otherwise the highest offered framework not newer than the SDK. Both are
    /// <c>null</c> when the template declares no TFM choice (nothing to pin) or every choice is newer
    /// than the SDK. <c>Choices</c> carries every framework the template offers, so a caller that gets
    /// no <c>Tfm</c> can tell "this template has no framework knob" (empty) apart from "the installed
    /// SDK is too old for every framework it supports" (non-empty).
    /// </para>
    /// </summary>
    internal static (bool Found, string? OptionName, string? Tfm, IReadOnlyList<string> Choices) DeriveTfmOption(
        string cacheJson, string packageId, string shortName, int sdkMajor)
    {
        if (string.IsNullOrEmpty(cacheJson) || string.IsNullOrEmpty(shortName))
        {
            return (false, null, null, []);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cacheJson);
        }
        catch (JsonException)
        {
            return (false, null, null, []);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("TemplateInfo", out var templates)
                || templates.ValueKind != JsonValueKind.Array)
            {
                return (false, null, null, []);
            }

            foreach (var template in templates.EnumerateArray())
            {
                if (!TemplateBelongsToPackage(template, packageId) || !TemplateHasShortName(template, shortName))
                {
                    continue;
                }

                // The requested template lives in this cache; from here on the answer is authoritative
                // (never fall through to another cache file) even when there's no TFM knob to pin.
                if (!TryGetTfmParameter(template, out var symbolName, out var choices))
                {
                    return (true, null, null, []);
                }

                var optionName = ResolveOptionName(template, symbolName);
                var tfm = PickTfm(choices, sdkMajor);
                return (true, optionName, tfm, choices);
            }
        }

        return (false, null, null, []);
    }

    /// <summary>
    /// Lowest .NET major version among <paramref name="choices"/> — the oldest SDK that can build any
    /// framework a template offers. Returns <c>null</c> when no choice parses as a
    /// <c>net&lt;major&gt;.0</c> moniker.
    /// </summary>
    internal static int? MinimumSdkMajor(IReadOnlyList<string> choices)
    {
        int? lowest = null;
        foreach (var choice in choices)
        {
            if (TryGetNetMajor(choice, out var major) && (lowest is null || major < lowest))
            {
                lowest = major;
            }
        }

        return lowest;
    }

    /// <summary>True when the template's mount point is the nupkg of <paramref name="packageId"/>.</summary>
    private static bool TemplateBelongsToPackage(JsonElement template, string packageId)
        => !string.IsNullOrEmpty(packageId)
            && template.TryGetProperty("MountPointUri", out var mount)
            && mount.ValueKind == JsonValueKind.String
            && (mount.GetString() ?? string.Empty).Contains(packageId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="shortName"/> is one of the template's short names.</summary>
    private static bool TemplateHasShortName(JsonElement template, string shortName)
    {
        if (!template.TryGetProperty("ShortNameList", out var shorts) || shorts.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return shorts.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.String)
            .Any(s => string.Equals(s.GetString(), shortName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the template parameter that selects the base target framework: a choice parameter whose
    /// options look like TFMs (<c>net8.0</c>, <c>net9.0</c>, ...). Returns its symbol name and offered
    /// framework values.
    /// </summary>
    private static bool TryGetTfmParameter(JsonElement template, out string symbolName, out IReadOnlyList<string> choices)
    {
        symbolName = string.Empty;
        choices = [];
        if (!template.TryGetProperty("Parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var p in parameters.EnumerateArray())
        {
            if (!p.TryGetProperty("Choices", out var choiceObj) || choiceObj.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var keys = choiceObj.EnumerateObject().Select(c => c.Name).ToList();
            if (keys.Count > 0 && keys.All(k => TryGetNetMajor(k, out _)))
            {
                symbolName = p.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? string.Empty
                    : string.Empty;
                choices = keys;
                return symbolName.Length > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the CLI option name for <paramref name="symbolName"/> from the template's host data
    /// (<c>symbolInfo.&lt;symbol&gt;.longName</c>), falling back to the raw symbol name when there is no
    /// host mapping — matching how <c>dotnet new</c> surfaces an unmapped symbol.
    /// </summary>
    private static string ResolveOptionName(JsonElement template, string symbolName)
    {
        if (!template.TryGetProperty("HostData", out var hostData))
        {
            return symbolName;
        }

        // HostData is stored either as an embedded JSON string or (defensively) as an inline object.
        try
        {
            if (hostData.ValueKind == JsonValueKind.String)
            {
                var raw = hostData.GetString();
                if (string.IsNullOrEmpty(raw))
                {
                    return symbolName;
                }

                using var parsed = JsonDocument.Parse(raw);
                return ExtractLongName(parsed.RootElement, symbolName);
            }

            if (hostData.ValueKind == JsonValueKind.Object)
            {
                return ExtractLongName(hostData, symbolName);
            }
        }
        catch (JsonException)
        {
            // Malformed host data — fall back to the raw symbol name.
        }

        return symbolName;
    }

    /// <summary>Reads <c>symbolInfo.&lt;symbol&gt;.longName</c> from host data, or returns the raw symbol name.</summary>
    private static string ExtractLongName(JsonElement host, string symbolName)
    {
        if (host.TryGetProperty("symbolInfo", out var symbolInfo)
            && symbolInfo.ValueKind == JsonValueKind.Object
            && symbolInfo.TryGetProperty(symbolName, out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("longName", out var longName)
            && longName.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(longName.GetString()))
        {
            return longName.GetString()!;
        }

        return symbolName;
    }

    /// <summary>
    /// Picks <c>net{sdkMajor}.0</c> when the template offers it, otherwise the highest offered framework
    /// whose major version is not newer than the SDK. Returns <c>null</c> when every choice is newer than
    /// the SDK (nothing buildable to pin).
    /// </summary>
    private static string? PickTfm(IReadOnlyList<string> choices, int sdkMajor)
    {
        var target = $"net{sdkMajor.ToString(CultureInfo.InvariantCulture)}.0";
        var exact = choices.FirstOrDefault(c => string.Equals(c, target, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return choices
            .Select(c => (Choice: c, IsValid: TryGetNetMajor(c, out var major), Major: major))
            .Where(x => x.IsValid && x.Major <= sdkMajor)
            .OrderByDescending(x => x.Major)
            .Select(x => x.Choice)
            .FirstOrDefault();
    }

    /// <summary>Parses the major version from a <c>net&lt;major&gt;.&lt;minor&gt;</c> TFM (e.g. <c>net10.0</c> → 10).</summary>
    private static bool TryGetNetMajor(string tfm, out int major)
    {
        major = 0;
        if (string.IsNullOrEmpty(tfm) || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = tfm[3..];
        var dot = rest.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        return int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
    }

    /// <summary>True when the line consists solely of a dashes separator (with optional surrounding whitespace).</summary>
    private static bool IsSeparatorRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '-' || ch == ' ');
    }

    /// <summary>
    /// Derives column [start, end) ranges from a dashes separator row: each contiguous run of dashes
    /// is one column, and the range extends to the start of the next column's dashes so trailing
    /// spaces in a wide field are captured. The final column extends to end-of-line.
    /// </summary>
    private static List<(int Start, int End)> GetColumnRanges(string separator)
    {
        var ranges = new List<(int Start, int End)>();
        var i = 0;
        while (i < separator.Length)
        {
            if (separator[i] == '-')
            {
                var start = i;
                while (i < separator.Length && separator[i] == '-')
                {
                    i++;
                }

                ranges.Add((start, i));
            }
            else
            {
                i++;
            }
        }

        // Extend each column's end to just before the next column's start (absorbing the gap), and the
        // last column to end-of-line, so values slightly wider than their dash run aren't truncated.
        for (var c = 0; c < ranges.Count; c++)
        {
            var end = c + 1 < ranges.Count ? ranges[c + 1].Start : int.MaxValue;
            ranges[c] = (ranges[c].Start, end);
        }

        return ranges;
    }

    /// <summary>Returns the substring of <paramref name="line"/> within [Start, End), clamped to the line length.</summary>
    private static string Slice(string line, (int Start, int End) range)
    {
        if (range.Start >= line.Length)
        {
            return string.Empty;
        }

        var end = Math.Min(range.End, line.Length);
        return line[range.Start..end];
    }
}
