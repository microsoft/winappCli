using System.Globalization;

namespace AccessLoop;

public sealed record AuditOptions(
    int? ProcessId, long? WindowHandle, string Winapp, string? Output,
    int Depth, int MaxElements, int TimeoutMs, int ObserveSeconds,
    int TabSteps, bool AcceptInput, bool Screenshot, bool Json = false,
    string? Navigation = null, bool AcceptNavigation = false, int MaxPages = 20, string? App = null)
{
    public static AuditOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "scan")
            throw new ArgumentException("Use scan --pid <PID> or scan --window <HWND>.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index++)
        {
            var key = args[index];
            if (key is "--accept-input" or "--no-screenshot" or "--json" or "--accept-navigation")
            {
                if (!flags.Add(key))
                    throw new ArgumentException($"Duplicate option: {key}.");
                continue;
            }
            if (key is not ("--app" or "--pid" or "--window" or "--winapp" or "--output" or "--depth"
                or "--max-elements" or "--timeout-ms" or "--observe-seconds" or "--tab-steps"
                or "--navigation" or "--max-pages")
                || ++index >= args.Count || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, args[index]))
                throw new ArgumentException($"Unknown, duplicate, or incomplete scan option: {key}.");
        }

        if (new[] { "--app", "--pid", "--window" }.Count(values.ContainsKey) != 1)
            throw new ArgumentException("Supply exactly one of --pid <PID>, --app <name|title|PID> or --window <HWND>.");
        int? pid = values.TryGetValue("--pid", out var pidText) ? Integer("--pid", pidText, 1, int.MaxValue) : null;
        long? hwnd = null;
        if (values.TryGetValue("--window", out var hwndText))
        {
            if (!long.TryParse(hwndText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new ArgumentException("--window must be a positive decimal HWND.");
            hwnd = parsed;
        }
        var tabSteps = Integer("--tab-steps", values.GetValueOrDefault("--tab-steps", "0"), 0, 30);
        var accepted = flags.Contains("--accept-input");
        if (tabSteps > 0 && !accepted)
            throw new ArgumentException("--tab-steps requires --accept-input. Even Tab can trigger application side effects.");
        if (tabSteps == 0 && accepted)
            throw new ArgumentException("--accept-input requires a positive --tab-steps value.");
        var seconds = Integer("--observe-seconds",
            values.GetValueOrDefault("--observe-seconds", tabSteps > 0 ? "10" : "0"), 0, 60);
        if (tabSteps > 0 && seconds == 0)
            throw new ArgumentException("Keyboard probes require a positive --observe-seconds budget.");
        var navigation = values.GetValueOrDefault("--navigation");
        var acceptNavigation = flags.Contains("--accept-navigation");
        if (navigation is not (null or "standard-tabs"))
            throw new ArgumentException("--navigation must be standard-tabs.");
        if (navigation is not null && !acceptNavigation)
            throw new ArgumentException("--navigation standard-tabs requires --accept-navigation. Selecting tabs can cause application side effects.");
        if (navigation is null && (acceptNavigation || values.ContainsKey("--max-pages")))
            throw new ArgumentException("--accept-navigation and --max-pages require --navigation standard-tabs.");
        if (navigation is not null && tabSteps > 0)
            throw new ArgumentException("Automatic tab-page navigation cannot be combined with --tab-steps. Run a separate keyboard probe on the intended page.");
        return new(pid, hwnd, values.GetValueOrDefault("--winapp", "winapp"), values.GetValueOrDefault("--output"),
            Integer("--depth", values.GetValueOrDefault("--depth", "20"), 1, 50),
            Integer("--max-elements", values.GetValueOrDefault("--max-elements", "2000"), 1, 10000),
            Integer("--timeout-ms", values.GetValueOrDefault("--timeout-ms", "15000"), 1000, 120000),
            seconds, tabSteps, accepted, !flags.Contains("--no-screenshot"), flags.Contains("--json"),
            navigation, acceptNavigation, Integer("--max-pages", values.GetValueOrDefault("--max-pages", "20"), 1, 100),
            values.GetValueOrDefault("--app"));
    }

    private static int Integer(string option, string text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
            throw new ArgumentException($"{option} must be {minimum}..{maximum}.");
        return value;
    }
}
