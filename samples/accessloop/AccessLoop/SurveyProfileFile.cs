using System.Security.Cryptography;
using System.Text.Json;

namespace AccessLoop;

public sealed record SurveyProfileFile(SurveyProfile Profile, byte[] Bytes, string Sha256)
{
    public static SurveyProfileFile Read(string path)
    {
        var bytes = AuditFiles.ReadBytes(path, 1024 * 1024);
        using var json = AuditFiles.ParseStrict(bytes);
        AuditFiles.RequireMembers(json.RootElement, "survey profile", "schemaVersion", "name", "processName",
            "navigationPaneId", "menuRootId", "itemClassName", "headingAutomationId", "groupIds", "excludedIds");
        var profile = AuditFiles.Deserialize<SurveyProfile>(json.RootElement);
        Validate(profile);
        return new(profile, bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static void Validate(SurveyProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.MaxPages is < 1 or > 100
            || profile.TreeDepth is < 2 or > 50 || profile.TimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("Profile limits require schemaVersion 1, maxPages 1..100, treeDepth 2..50 and timeoutSeconds 1..120.");
        Literal(profile.Name, "profile name");
        Id(profile.ProcessName, "processName");
        Id(profile.NavigationPaneId, "navigationPaneId");
        Id(profile.MenuRootId, "menuRootId");
        Id(profile.HeadingAutomationId, "headingAutomationId");
        Literal(profile.ItemClassName, "itemClassName");
        if (profile.ItemClassName != "Microsoft.UI.Xaml.Controls.NavigationViewItem")
            throw new InvalidDataException("This survey version supports explicitly profiled WinUI NavigationViewItem menus, not arbitrary clickable controls.");
        if (profile.NavigationPaneId == profile.MenuRootId)
            throw new InvalidDataException("The approved menu root must be a distinct descendant of the navigation pane.");
        ValidateIds(profile.GroupIds, "groupIds", 20);
        ValidateIds(profile.ExcludedIds, "excludedIds", 100);
        ValidateIds(profile.HeadingWithoutIdPageIds, "headingWithoutIdPageIds", 100);
        if (!double.IsFinite(profile.HeadingWithoutIdTopFraction) || profile.HeadingWithoutIdTopFraction is < 0.05 or > 0.5)
            throw new InvalidDataException("headingWithoutIdTopFraction must be 0.05..0.5 of the navigation pane's height.");
        if (profile.GroupIds.Intersect(profile.ExcludedIds, StringComparer.Ordinal).Any())
            throw new InvalidDataException("A navigation group cannot also be an excluded item.");
        if (profile.HeadingWithoutIdPageIds.Intersect(profile.ExcludedIds.Concat(profile.GroupIds), StringComparer.Ordinal).Any())
            throw new InvalidDataException("Name-and-position heading checks apply only to approved page leaves.");
        if (profile.HeadingAliases is null || profile.HeadingAliases.Count > 100)
            throw new InvalidDataException("headingAliases must be a bounded object.");
        foreach (var (id, aliases) in profile.HeadingAliases)
        {
            Id(id, "heading alias navigation ID");
            if (aliases is null || aliases.Length is < 1 or > 10
                || aliases.Select(alias => alias?.Trim()).Distinct(StringComparer.Ordinal).Count() != aliases.Length)
                throw new InvalidDataException("Each heading alias list requires 1..10 distinct literal names.");
            foreach (var alias in aliases)
                Literal(alias, "heading alias");
        }
    }

    internal static bool MatchesHeading(SurveyProfile profile, SurveyPageDefinition page, string? heading) =>
        heading is not null && (heading.Trim() == page.Name.Trim() ||
            (profile.HeadingAliases.TryGetValue(page.Id, out var aliases)
                && aliases.Any(alias => alias.Trim() == heading.Trim())));

    internal static void ValidateIds(IReadOnlyList<string>? ids, string name, int maximum)
    {
        if (ids is null || ids.Count > maximum || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new InvalidDataException($"{name} must contain at most {maximum} unique IDs.");
        foreach (var id in ids)
            Id(id, name);
    }

    internal static void Id(string? value, string name)
    {
        Literal(value, name);
        if (value!.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            || value is "." or "..")
            throw new InvalidDataException($"{name} must be a literal ASCII identifier, not a path or selector expression.");
    }

    private static void Literal(string? value, string name)
    {
        AuditFiles.Text(value, name, 256);
        if (value!.Any(char.IsControl))
            throw new InvalidDataException($"{name} cannot contain control characters.");
    }
}

public static class SurveyOptionsParser
{
    public static SurveyOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "survey")
            throw new ArgumentException("Use survey <profile.json> or survey --resume <run-directory>.");
        string? profile = null;
        var start = 1;
        if (args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            profile = args[1];
            start = 2;
        }
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var retry = new List<string>();
        for (var index = start; index < args.Count; index++)
        {
            var key = args[index];
            if (key is "--accept-navigation" or "--confirm-unchanged" or "--no-screenshot" or "--json")
            {
                if (!flags.Add(key))
                    throw new ArgumentException($"Duplicate survey option: {key}.");
                continue;
            }
            if (key is not ("--resume" or "--pid" or "--window" or "--winapp" or "--output" or "--stop-after" or "--retry-page")
                || ++index >= args.Count || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown or incomplete survey option: {key}.");
            if (key == "--retry-page")
            {
                SurveyProfileFile.Id(args[index], "retry page");
                if (retry.Contains(args[index], StringComparer.Ordinal))
                    throw new ArgumentException("Retry page IDs must be unique.");
                retry.Add(args[index]);
            }
            else if (!values.TryAdd(key, args[index]))
                throw new ArgumentException($"Duplicate survey option: {key}.");
        }
        if (!flags.Contains("--accept-navigation"))
            throw new ArgumentException("--accept-navigation is required before changing navigation or keyboard focus in an existing app.");
        var resume = values.GetValueOrDefault("--resume");
        if ((profile is null) == (resume is null))
            throw new ArgumentException("Supply either a profile file for a new survey or --resume, not both.");
        if (resume is not null)
        {
            if (!flags.Contains("--confirm-unchanged"))
                throw new ArgumentException("Resume requires --confirm-unchanged: confirm application settings and test data were not changed while paused. Automatic identity checks cannot prove that.");
            if (values.ContainsKey("--pid") || values.ContainsKey("--window") || values.ContainsKey("--output") || flags.Contains("--no-screenshot"))
                throw new ArgumentException("Resume uses the frozen target, output and capture settings; start a new survey to change them.");
        }
        else
        {
            if (values.ContainsKey("--pid") == values.ContainsKey("--window") || !values.ContainsKey("--output"))
                throw new ArgumentException("A new survey requires exactly one --pid or --window and a new --output directory.");
            if (retry.Count > 0 || flags.Contains("--confirm-unchanged"))
                throw new ArgumentException("--retry-page and --confirm-unchanged are resume-only options.");
        }
        int? pid = values.TryGetValue("--pid", out var pidText) ? checked((int)Positive(pidText, "--pid", int.MaxValue)) : null;
        long? hwnd = values.TryGetValue("--window", out var windowText) ? Positive(windowText, "--window", long.MaxValue) : null;
        int? stop = values.TryGetValue("--stop-after", out var stopText) ? (int)Positive(stopText, "--stop-after", 100) : null;
        return new(profile, resume, pid, hwnd, values.GetValueOrDefault("--winapp", "winapp"), values.GetValueOrDefault("--output"),
            true, flags.Contains("--confirm-unchanged"), stop, retry, flags.Contains("--no-screenshot"), flags.Contains("--json"));
    }

    private static long Positive(string value, string name, long maximum) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result)
            && result > 0 && result <= maximum ? result : throw new ArgumentException($"{name} must be a positive integer up to {maximum}.");
}
