using System.Security.Cryptography;

namespace AccessLoop;

public sealed record ExploreProfileFile(string Path, ExploreProfile Profile, byte[] Bytes, string Sha256, string? ProjectPath)
{
    public static ExploreProfileFile Read(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = AuditFiles.ReadBytes(fullPath, 1024 * 1024);
        using var json = AuditFiles.ParseStrict(bytes);
        AuditFiles.RequireMembers(json.RootElement, "exploration profile",
            "schemaVersion", "name", "processName", "navigationRootId", "initialPageId", "pages");
        var profile = AuditFiles.Deserialize<ExploreProfile>(json.RootElement);
        Validate(profile);
        string? project = null;
        if (profile.Project is not null)
        {
            project = AuditFiles.ResolveArtifact(System.IO.Path.GetDirectoryName(fullPath)!, profile.Project);
            if (!System.IO.Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The owned project must be a local .csproj below the profile directory.");
        }
        return new(fullPath, profile, bytes, Convert.ToHexString(SHA256.HashData(bytes)), project);
    }

    public static void Validate(ExploreProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Pages is null || profile.Pages.Count is < 1 or > 10
            || profile.TimeoutSeconds is < 1 or > 120 || profile.MaxDurationSeconds is < 30 or > 1800
            || profile.MaxStates is < 1 or > 64 || profile.TreeDepth is < 2 or > 40
            || profile.MaxElements is < 10 or > 5000)
            throw new InvalidDataException("Exploration requires schema 1, 1..10 pages, timeout 1..120 seconds, duration 30..1800 seconds, 1..64 states, depth 2..40 and 10..5000 elements.");
        AuditFiles.Text(profile.Name, "profile name", 128);
        SurveyProfileFile.Id(profile.ProcessName, "processName");
        SurveyProfileFile.Id(profile.NavigationRootId, "navigationRootId");
        SurveyProfileFile.Id(profile.InitialPageId, "initialPageId");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var controlIds = new HashSet<string>(StringComparer.Ordinal) { profile.NavigationRootId };
        var minimumStates = profile.Pages.Count;
        foreach (var page in profile.Pages)
        {
            if (page is null)
                throw new InvalidDataException("Page definitions cannot be null.");
            SurveyProfileFile.Id(page.Id, "page ID");
            if (!pageIds.Add(page.Id))
                throw new InvalidDataException("Page IDs must be unique.");
            AuditFiles.Text(page.Name, "page name", 128);
            Control(page.NavigationId, "navigation ID");
            Control(page.ReadyId, "page ready marker");
            if (page.Scroll is { } scroll)
            {
                Control(scroll.ContainerId, "scroll container");
                Control(scroll.TopAnchorId, "scroll top anchor");
                Control(scroll.BottomAnchorId, "scroll bottom anchor");
                if (scroll.MaxPositions is < 2 or > 12 || !double.IsFinite(scroll.OverlapFraction)
                    || scroll.OverlapFraction is < 0.1 or > 0.75)
                    throw new InvalidDataException("Scroll policies require 2..12 positions and 0.1..0.75 viewport overlap.");
                minimumStates++;
            }
            if (page.Dropdowns is null || page.Dropdowns.Count > 5 || page.Dialogs is null || page.Dialogs.Count > 5)
                throw new InvalidDataException("Each page may declare at most five dropdowns and five dialogs.");
            foreach (var dropdown in page.Dropdowns)
            {
                if (dropdown is null || dropdown.ItemIds is null || dropdown.ItemIds.Count is < 1 or > 50)
                    throw new InvalidDataException("A dropdown needs 1..50 explicit item IDs.");
                Control(dropdown.ControlId, "dropdown control");
                foreach (var id in dropdown.ItemIds)
                    Control(id, "dropdown item");
                minimumStates++;
            }
            foreach (var dialog in page.Dialogs)
            {
                if (dialog is null || dialog.Scope is not ("same-window" or "owned-window"))
                    throw new InvalidDataException("Dialog scope must be same-window or owned-window.");
                Control(dialog.OpenerId, "dialog opener");
                Control(dialog.RootId, "dialog marker");
                Control(dialog.DismissId, "dialog safe dismiss action");
                minimumStates++;
            }
        }
        if (!pageIds.Contains(profile.InitialPageId))
            throw new InvalidDataException("The initial page must be declared.");
        if (profile.MaxStates < minimumStates)
            throw new InvalidDataException($"maxStates must permit at least the {minimumStates} declared base/interaction states.");
        if (profile.ScopeProbe is { } probe)
        {
            if (!pageIds.Contains(probe.PageId))
                throw new InvalidDataException("The optional scope probe must refer to a declared page.");
            Control(probe.OpenerId, "scope probe opener");
            AuditFiles.Text(probe.WindowTitle, "scope probe title", 128);
            if (probe.WindowTitle.Any(char.IsControl))
                throw new InvalidDataException("The scope probe title must be literal text.");
        }

        void Control(string id, string name)
        {
            SurveyProfileFile.Id(id, name);
            if (!controlIds.Add(id))
                throw new InvalidDataException($"Control '{id}' is assigned to multiple interaction roles.");
        }
    }
}

public static class ExploreOptionsParser
{
    public static ExploreOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != "explore" || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Use explore <profile.json> --accept-interactions with an explicit window/process or --launch-owned.");
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index++)
        {
            var key = args[index];
            if (key is "--accept-interactions" or "--launch-owned" or "--no-screenshot" or "--probe-scope-guard" or "--json")
            {
                if (!flags.Add(key))
                    throw new ArgumentException($"Duplicate exploration option: {key}.");
                continue;
            }
            if (key is not ("--winapp" or "--output" or "--pid" or "--window") || ++index >= args.Count
                || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, args[index]))
                throw new ArgumentException($"Unknown, duplicate or incomplete exploration option: {key}.");
        }
        if (!flags.Contains("--accept-interactions"))
            throw new ArgumentException("--accept-interactions is required for declared navigation, scrolling, disclosure and safe dialog dismissal. It never authorizes changing selected options or feature settings.");
        var targets = (flags.Contains("--launch-owned") ? 1 : 0) + (values.ContainsKey("--pid") ? 1 : 0) + (values.ContainsKey("--window") ? 1 : 0);
        if (targets != 1)
            throw new ArgumentException("Choose exactly one target: --pid, --window, or --launch-owned.");
        if (!values.ContainsKey("--output"))
            throw new ArgumentException("Choose a new --output directory for exploration evidence.");
        if (flags.Contains("--probe-scope-guard") && !flags.Contains("--launch-owned"))
            throw new ArgumentException("--probe-scope-guard deliberately opens the declared test window and is allowed only for an owned source demo.");
        return new(args[1], values.GetValueOrDefault("--winapp", "winapp"), values.GetValueOrDefault("--output"),
            values.TryGetValue("--pid", out var pid) ? checked((int)Positive(pid, "--pid", int.MaxValue)) : null,
            values.TryGetValue("--window", out var window) ? Positive(window, "--window", long.MaxValue) : null,
            flags.Contains("--launch-owned"), true, flags.Contains("--no-screenshot"),
            flags.Contains("--probe-scope-guard"), flags.Contains("--json"));
    }

    private static long Positive(string value, string name, long maximum) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed is > 0 && parsed <= maximum ? parsed : throw new ArgumentException($"{name} must be a positive integer up to {maximum}.");
}
