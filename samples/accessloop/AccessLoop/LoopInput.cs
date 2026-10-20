using System.Security.Cryptography;
using System.Text.Json;

namespace AccessLoop;

public sealed record LoopInput(string Path, LoopProfile Profile, byte[] OriginalBytes)
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "Name", "IsEnabled", "IsOffscreen", "IsSelected", "HasKeyboardFocus", "ToggleState",
        "ExpandCollapseState", "ScrollVerticalPercent", "SelectionAutomationIds"
    };

    public static LoopInput Read(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var bytes = AuditFiles.ReadBytes(full, 1024 * 1024);
        var profile = ReadProfile(bytes);
        var project = AuditFiles.ResolveArtifact(System.IO.Path.GetDirectoryName(full)!, profile.Project);
        RequireProject(project);
        return new(full, profile with { Project = project }, bytes);
    }

    public static LoopProfile ReadProfile(byte[] bytes)
    {
        using var document = AuditFiles.ParseStrict(bytes);
        AuditFiles.RequireMembers(document.RootElement, "loop profile",
            "schemaVersion", "name", "project", "processName", "pages", "steps", "repairTargets");
        var profile = AuditFiles.Deserialize<LoopProfile>(document.RootElement);
        Validate(profile);
        return profile;
    }

    public static byte[] PlanBytes(LoopProfile profile) =>
        JsonSerializer.SerializeToUtf8Bytes(profile with { Project = System.IO.Path.GetFileName(profile.Project) }, Json.Options);

    public static string PlanHash(LoopProfile profile) => Convert.ToHexString(SHA256.HashData(PlanBytes(profile)));

    public void EnsureUnchanged()
    {
        if (!AuditFiles.ReadBytes(Path, 1024 * 1024).AsSpan().SequenceEqual(OriginalBytes))
            throw new InvalidDataException("The preset input changed during execution; the original acceptance route cannot be replaced.");
    }

    public static void Validate(LoopProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Pages is null || profile.Pages.Count is < 1 or > 8
            || profile.Steps is null || profile.Steps.Count is < 1 or > 160
            || profile.RepairTargets is null || profile.RepairTargets.Count is < 1 or > 8
            || profile.TimeoutSeconds is < 1 or > 60 || profile.MaxDurationSeconds is < 30 or > 1800
            || profile.TreeDepth is < 2 or > 40 || profile.MaxElements is < 20 or > 5000)
            throw new InvalidDataException("Loop schema 1 requires bounded pages, steps, repair targets, time and snapshot sizes.");
        AuditFiles.Text(profile.Name, "loop name", 128);
        AuditFiles.Text(profile.Project, "owned project", 1024);
        SurveyProfileFile.Id(profile.ProcessName, "process name");
        if (!profile.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A loop must declare a source-owned .csproj.");
        var pages = new Dictionary<string, LoopPage>(StringComparer.Ordinal);
        var navigation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in profile.Pages)
        {
            if (page is null)
                throw new InvalidDataException("Loop pages cannot be null.");
            SurveyProfileFile.Id(page.Id, "page ID");
            AuditFiles.Text(page.Name, "page name", 128);
            SurveyProfileFile.Id(page.NavigationId, "navigation ID");
            SurveyProfileFile.Id(page.ReadyId, "ready marker");
            if (!pages.TryAdd(page.Id, page) || !navigation.Add(page.NavigationId) || !navigation.Add(page.ReadyId))
                throw new InvalidDataException("Page IDs and their navigation/ready controls must be unique.");
        }
        var steps = new Dictionary<string, LoopStep>(StringComparer.Ordinal);
        string? activePage = null;
        foreach (var step in profile.Steps)
        {
            if (step is null || step.Expectations is null || step.RequiredIds is null
                || step.Expectations.Count > 24 || step.RequiredIds.Count > 24)
                throw new InvalidDataException("Steps and bounded expectation/control lists are required.");
            SurveyProfileFile.Id(step.Id, "step ID");
            SurveyProfileFile.Id(step.PageId, "step page ID");
            AuditFiles.Text(step.Name, "step name", 160);
            if (!steps.TryAdd(step.Id, step) || !pages.TryGetValue(step.PageId, out var page))
                throw new InvalidDataException("Each step must have a unique ID and refer to a declared page.");
            if (step.Kind is not ("navigate" or "key" or "scroll" or "expand-collapse" or "invoke" or "assert" or "scan"))
                throw new InvalidDataException($"Unsupported loop step kind '{step.Kind}'.");
            if (step.Kind == "navigate")
            {
                if (step.TargetId != page.NavigationId || step.ControlType is not ("TabItem" or "Button"))
                    throw new InvalidDataException("Navigation may select only that page's declared tab/header.");
                activePage = step.PageId;
            }
            if (activePage != step.PageId)
                throw new InvalidDataException("Begin each page's steps with its explicit navigation preparation.");
            if (step.Kind is not ("assert" or "scan"))
            {
                SurveyProfileFile.Id(step.TargetId!, "action target ID");
                SurveyProfileFile.Id(step.ControlType!, "action target control type");
            }
            else if (step.TargetId is not null || step.ControlType is not null)
                throw new InvalidDataException("Read-only assert/scan steps use expectations and requiredIds, not an action target.");
            if (step.Kind == "key")
            {
                if (step.Keys is not ("Tab" or "Shift+Tab" or "Space" or "Enter" or "Alt+Down" or "Escape"
                    or "Up" or "Down" or "Left" or "Right" or "Home" or "End" or "PageUp" or "PageDown"))
                    throw new InvalidDataException("Only the reviewed non-system navigation/activation keys are allowed in this preset prototype.");
            }
            else if (step.Keys is not null)
                throw new InvalidDataException("Only key steps may specify keys.");
            if (step.Kind == "scroll")
            {
                if (step.VerticalPercent is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100)
                    throw new InvalidDataException("Scroll steps require a finite verticalPercent from 0 to 100.");
            }
            else if (step.VerticalPercent is not null)
                throw new InvalidDataException("Only scroll steps may specify a vertical percentage.");
            if (step.Kind == "expand-collapse")
            {
                if (step.ExpansionState is not ("expanded" or "collapsed"))
                    throw new InvalidDataException("Disclosure steps require expanded or collapsed.");
            }
            else if (step.ExpansionState is not null)
                throw new InvalidDataException("Only disclosure steps may specify an expansion state.");
            if (step.Kind == "invoke" && step.ControlType != "Button")
                throw new InvalidDataException("Invoke steps are restricted to declared ordinary Buttons.");
            if (step.Kind == "scan")
            {
                if (step.RequiredIds.Count == 0)
                    throw new InvalidDataException("Each checkpoint must name its required controls.");
            }
            else if (step.RequiredIds.Count != 0)
                throw new InvalidDataException("Only scan steps declare required snapshot controls.");
            if (step.Kind == "assert" && step.Expectations.Count == 0)
                throw new InvalidDataException("An assert step must contain at least one explicit expectation.");
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in step.RequiredIds)
            {
                SurveyProfileFile.Id(id, "required control ID");
                if (!required.Add(id))
                    throw new InvalidDataException("Required controls must not be repeated in a checkpoint.");
            }
            var checks = new HashSet<(string, string)>();
            foreach (var expectation in step.Expectations)
            {
                if (expectation is null || !checks.Add((expectation.TargetId, expectation.Property)))
                    throw new InvalidDataException("Expectations cannot be null or repeated within a step.");
                ValidateExpectation(expectation);
            }
        }
        if (profile.Steps.Count(step => step.Kind == "scan") > 24
            || pages.Keys.Any(id => !profile.Steps.Any(step => step.PageId == id && step.Kind == "scan")))
            throw new InvalidDataException("The route must audit every declared page, with at most 24 checkpoints.");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in profile.RepairTargets)
        {
            if (target is null)
                throw new InvalidDataException("Repair targets cannot be null.");
            SurveyProfileFile.Id(target.AutomationId, "repair target");
            AuditFiles.Text(target.ExpectedName, "reviewed accessible name", 200);
            if (target.ExpectedName.Any(char.IsControl) || target.ExpectedName.TrimStart().StartsWith('{')
                || !targets.Add(target.AutomationId) || navigation.Contains(target.AutomationId)
                || !steps.TryGetValue(target.CheckpointId, out var checkpoint) || checkpoint.Kind != "scan"
                || checkpoint.PageId != target.PageId || !checkpoint.RequiredIds.Contains(target.AutomationId, StringComparer.Ordinal))
                throw new InvalidDataException("Every repair target needs a unique non-navigation ID and an exact required checkpoint with a reviewed literal name.");
        }
    }

    private static void ValidateExpectation(LoopExpectation expectation)
    {
        SurveyProfileFile.Id(expectation.TargetId, "expectation target");
        if (!Properties.Contains(expectation.Property))
            throw new InvalidDataException($"Unsupported expectation property '{expectation.Property}'.");
        var value = expectation.Expected;
        if (expectation.Property == "SelectionAutomationIds")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 50)
                throw new InvalidDataException("SelectionAutomationIds expects a bounded string array.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                SurveyProfileFile.Id(id!, "selected item ID");
                if (!ids.Add(id!))
                    throw new InvalidDataException("Selected item IDs must be unique.");
            }
            return;
        }
        if (expectation.Property == "ScrollVerticalPercent")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
                || !double.IsFinite(number) || number is < 0 or > 100)
                throw new InvalidDataException("ScrollVerticalPercent expects a numeric percentage from 0 to 100.");
            return;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Native scalar properties require explicit string expectations.");
        var text = value.GetString()!;
        AuditFiles.Text(text, "expected property value", 2000, allowBlank: expectation.Property == "Name");
        if (expectation.Property is "IsEnabled" or "IsOffscreen" or "IsSelected" or "HasKeyboardFocus"
            && text is not ("True" or "False"))
            throw new InvalidDataException("Boolean native properties expect True or False strings.");
        if (expectation.Property == "ToggleState" && text is not ("On" or "Off" or "Indeterminate"))
            throw new InvalidDataException("ToggleState expects On, Off or Indeterminate.");
        if (expectation.Property == "ExpandCollapseState" && text is not ("Expanded" or "Collapsed" or "PartiallyExpanded" or "LeafNode"))
            throw new InvalidDataException("Invalid expected expansion state.");
    }

    internal static void RequireProject(string project)
    {
        if (!System.IO.Path.IsPathFullyQualified(project) || project.StartsWith(@"\\", StringComparison.Ordinal)
            || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source project must be a fully resolved local .csproj.");
        _ = AuditFiles.ReadBytes(project, 4 * 1024 * 1024);
    }
}

public sealed record LoopOptions(
    string? Input, string? Replay, string Round, string Output, string Winapp, string Copilot,
    bool Apply, string? Proposals, bool NoScreenshot, bool Json)
{
    public static LoopOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != "loop")
            throw new ArgumentException("Use loop <preset.json> --accept-actions --output <new-directory>.");
        var position = 1;
        string? input = args[position].StartsWith("--", StringComparison.Ordinal) ? null : args[position++];
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (; position < args.Count; position++)
        {
            var key = args[position];
            if (key is "--accept-actions" or "--apply" or "--no-screenshot" or "--json")
            {
                if (!flags.Add(key))
                    throw new ArgumentException($"Duplicate loop flag: {key}.");
            }
            else if (key is not ("--replay" or "--round" or "--output" or "--winapp" or "--copilot" or "--proposals")
                || ++position >= args.Count || string.IsNullOrWhiteSpace(args[position])
                || args[position].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, args[position]))
                throw new ArgumentException($"Unknown, duplicate or incomplete loop option: {key}.");
        }
        if (!flags.Contains("--accept-actions") || !values.ContainsKey("--output"))
            throw new ArgumentException("--accept-actions and a new --output directory are required. This builds and launches an owned demo and performs its reviewed test-state changes and real keyboard input.");
        if ((input is null) == !values.ContainsKey("--replay"))
            throw new ArgumentException("Choose a preset input OR --replay <run-directory>, not both.");
        if (values.ContainsKey("--replay") && (flags.Contains("--apply") || flags.Contains("--no-screenshot")
                || values.ContainsKey("--copilot") || values.ContainsKey("--proposals")))
            throw new ArgumentException("Replay inherits the recorded checks/capture policy and cannot request source repairs.");
        if (values.ContainsKey("--round") && !values.ContainsKey("--replay"))
            throw new ArgumentException("--round is only valid with --replay.");
        if ((values.ContainsKey("--proposals") || values.ContainsKey("--copilot")) && !flags.Contains("--apply"))
            throw new ArgumentException("Repair proposal options require --apply.");
        if (values.ContainsKey("--proposals") && values.ContainsKey("--copilot"))
            throw new ArgumentException("Choose reviewed proposal files or a Copilot advisor, not both.");
        var round = values.GetValueOrDefault("--round", "baseline");
        SurveyProfileFile.Id(round, "replay round");
        return new(input, values.GetValueOrDefault("--replay"), round, values["--output"],
            values.GetValueOrDefault("--winapp", "winapp"), values.GetValueOrDefault("--copilot", "copilot"),
            flags.Contains("--apply"), values.GetValueOrDefault("--proposals"),
            flags.Contains("--no-screenshot"), flags.Contains("--json"));
    }
}
