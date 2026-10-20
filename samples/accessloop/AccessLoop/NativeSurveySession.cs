using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeSurveySession : ISurveySession
{
    private readonly SurveyProfile profile;
    private readonly IAuditPlatform inner;
    private readonly CountingPlatform platform;
    private readonly ISurveyNativeContext native;
    private readonly string winappVersion;
    private readonly bool screenshot;
    private readonly int commandLimit;
    private SurveyContext? context;
    private SurveyInventory? inventory;
    private SurveyNavigationState? originalNavigation;
    private SurveyPageDefinition? originalPage;
    private Dictionary<string, SurveyPageDefinition> approvedPages = new(StringComparer.Ordinal);
    private bool navigationChanged;
    private bool restoring;
    private int restorationCommands;
    private int nativeCommands;
    private long nativeMilliseconds;
    private long auditMilliseconds;

    public NativeSurveySession(
        SurveyProfile profile, IAuditPlatform platform, AuditTarget target, string winappVersion, bool screenshot)
        : this(profile, platform, target, winappVersion, screenshot, new SurveyNativeContext()) { }

    internal NativeSurveySession(
        SurveyProfile profile, IAuditPlatform platform, AuditTarget target, string winappVersion, bool screenshot,
        ISurveyNativeContext nativeContext)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(nativeContext);
        try { SurveyProfileFile.Validate(profile); }
        catch (InvalidDataException ex) { throw Blocked("profile-mismatch", ex.Message, false); }
        this.profile = profile with
        {
            GroupIds = profile.GroupIds.ToArray(),
            ExcludedIds = profile.ExcludedIds.ToArray(),
            HeadingWithoutIdPageIds = profile.HeadingWithoutIdPageIds.ToArray(),
            HeadingAliases = profile.HeadingAliases.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal)
        };
        inner = platform;
        native = nativeContext;
        this.platform = new(this);
        Target = target;
        this.winappVersion = winappVersion;
        this.screenshot = screenshot;
        commandLimit = 256 + profile.MaxPages * 48 + profile.GroupIds.Count * 32;
    }

    public AuditTarget Target { get; }
    public SurveyPerformance Performance => new(nativeCommands, nativeMilliseconds, auditMilliseconds);

    public void Preflight() => CheckTarget(Target);

    public SurveyContext GetContext()
    {
        var window = CheckTarget(Target);
        SurveyContext current;
        try { current = native.ReadContext(Target, window); }
        catch (Exception ex) when (ex is not SurveyBlockedException && IsFailure(ex))
        {
            throw Blocked("application-identity-unavailable", "Installed-app identity or environment metadata could not be read.", false);
        }
        _ = CheckTarget(Target);
        var app = current.Application;
        var environment = current.Environment;
        Require(string.Equals(app.ProcessName, Target.ProcessName, StringComparison.OrdinalIgnoreCase)
            && Path.IsPathFullyQualified(app.ExecutablePath) && IsHash(app.ExecutableSha256)
            && !string.IsNullOrWhiteSpace(app.ProductVersion) && (app.CompanionSha256 is null || IsHash(app.CompanionSha256)),
            "The selected application's path, binary fingerprint or ProductVersion is unknown.");
        Require(environment.WindowWidth == window.Width && environment.WindowHeight == window.Height
            && environment.Dpi == window.Dpi && !string.IsNullOrWhiteSpace(environment.OsVersion)
            && !string.IsNullOrWhiteSpace(environment.UiCulture), "Critical survey environment metadata is unknown.");
        if (context is not null && context.Application != app)
            throw Blocked("application-changed", "The selected installed application's binary identity changed. Start a new survey.", false);
        if (context is not null && context.Environment != environment)
            throw Blocked("environment-changed", "The survey environment changed. Restore the original environment before resuming.", true);
        context ??= current;
        return current;
    }

    public async Task<SurveyNavigationState> ReadNavigationStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(!navigationChanged, "Original navigation must be observed before any navigation changes.");
        _ = GetContext();
        var tree = await ReadTreeAsync(token);
        var selected = await ReadSelectionAsync(tree, visibleOnly: true, token);
        var state = new SurveyNavigationState(selected, new Dictionary<string, bool>(tree.Groups, StringComparer.Ordinal));
        AcceptOriginal(state);
        originalPage = tree.Pages.SingleOrDefault(page => page.Id == selected);
        return Copy(state);
    }

    public async Task<SurveyInventory> DiscoverAsync(SurveyNavigationState original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = GetContext();
        AcceptOriginal(original);
        Require(inventory is null, "The navigation inventory is already frozen; validate it rather than rediscovering.");
        var tree = await ExpandGroupsAsync(await ReadTreeAsync(token), profile.GroupIds, token);
        Require(tree.Pages.Count > 0, "No eligible internal settings pages were found.");
        var selected = originalNavigation!.SelectedPageId;
        if (selected is null)
            selected = await ReadSelectionAsync(tree, visibleOnly: false, token);
        Require(selected is null || tree.Pages.Any(page => page.Id == selected), "The original selection is not an eligible internal page.");
        originalNavigation = new(selected, originalNavigation.ExpandedGroups);
        originalPage = tree.Pages.SingleOrDefault(page => page.Id == selected);
        var discovered = new SurveyInventory(tree.Pages, tree.Excluded, tree.Items.Select(item => item.Id!).ToArray(), Copy(originalNavigation));
        Freeze(discovered);
        return CopyInventory(inventory!);
    }

    public async Task ValidateInventoryAsync(SurveyInventory inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = GetContext();
        AcceptOriginal(inventory.OriginalNavigation);
        Freeze(inventory);
        originalPage = inventory.Pages.SingleOrDefault(page => page.Id == originalNavigation!.SelectedPageId);
        var tree = await ExpandGroupsAsync(await ReadTreeAsync(token), profile.GroupIds, token);
        MatchInventory(tree, complete: true);
    }

    public async Task<SurveyPageReady> NavigateAsync(SurveyPageDefinition page, CancellationToken token)
    {
        var approved = ApprovedPage(page);
        token.ThrowIfCancellationRequested();
        _ = GetContext();
        return await NavigateCoreAsync(approved, token);
    }

    public async Task<SurveyPageReady> VerifyPageAsync(SurveyPageDefinition page, CancellationToken token)
    {
        var approved = ApprovedPage(page);
        token.ThrowIfCancellationRequested();
        _ = GetContext();
        var tree = await ReadTreeAsync(token);
        return await ReadReadyAsync(approved, tree.Leaf(approved), token);
    }

    public async Task<AuditReport> ScanAsync(string outputDirectory, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("The scan directory must be an absolute, already-created empty directory.", nameof(outputDirectory));
        var output = Path.GetFullPath(outputDirectory);
        AuditFiles.EnsureDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("The scan directory must be empty; existing evidence will not be overwritten.");
        _ = GetContext();
        var clock = Stopwatch.StartNew();
        try
        {
            var options = new AuditOptions(null, Target.WindowHandle, "winapp", output, profile.TreeDepth,
                2000, checked(profile.TimeoutSeconds * 1000), 0, 0, false, screenshot);
            var report = await new AuditRunner(platform).RunAsync(options, Target, winappVersion, output, token);
            AuditFiles.WriteNew(Path.Combine(output, "scan.json"), report);
            AuditReportRenderer.Write(Path.Combine(output, "report.html"), report);
            return report;
        }
        finally { auditMilliseconds += clock.ElapsedMilliseconds; }
    }

    public async Task<SurveyRestoration> RestoreAsync(SurveyNavigationState original, CancellationToken token)
    {
        var restoredGroups = 0;
        var selectionRestored = false;
        restoring = true;
        restorationCommands = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            _ = GetContext();
            AcceptOriginal(original);
            var selected = originalNavigation!.SelectedPageId;
            if (selected is not null)
            {
                if (originalPage is null && !approvedPages.ContainsKey(selected))
                    _ = await ReadTreeAsync(token);
                var page = originalPage ?? (approvedPages.TryGetValue(selected, out var approved) ? approved : null);
                Require(page is not null, "The original selected page was not established as an internal navigation leaf.");
                var ready = await NavigateCoreAsync(page!, token);
                if (!ready.Selected)
                    throw Blocked("restoration-not-ready", "The original internal navigation selection could not be confirmed.", true);
                selectionRestored = true;
            }
            foreach (var group in profile.GroupIds.Reverse())
            {
                await SetGroupAsync(await ReadTreeAsync(token), group, originalNavigation.ExpandedGroups[group], token);
                restoredGroups++;
            }
            var final = await ReadTreeAsync(token);
            Require(profile.GroupIds.All(id => final.Groups[id] == originalNavigation.ExpandedGroups[id]),
                "The final navigation group states do not match the original states.");
            return new(selectionRestored ? "restored-navigation" : "incomplete",
                (selectionRestored ? "Restored the original selected page and navigation group states."
                    : "Restored the original navigation group states; the original selected page was unobservable and was not restored.")
                + " Scroll position is not guaranteed. No application-data rollback was attempted.");
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return new("incomplete",
                $"Navigation restoration is incomplete (selected page restored: {selectionRestored}; groups verified: {restoredGroups}/{profile.GroupIds.Count}). "
                + (ex is OperationCanceledException ? "The bounded cleanup was cancelled." : ex.Message)
                + " No scope or environment guard was bypassed. Scroll position is not guaranteed; no application-data rollback was attempted.");
        }
        finally { restoring = false; }
    }

    private async Task<SurveyPageReady> NavigateCoreAsync(SurveyPageDefinition page, CancellationToken token)
    {
        var tree = await ExpandGroupsAsync(await ReadTreeAsync(token), page.GroupIds, token);
        var node = tree.Leaf(page);
        var control = await PrepareAsync(node, token);
        var invoked = await UiAsync(["invoke", control.Selector], token);
        ExactAction(invoked, control.Selector);
        Require(Text(invoked, "pattern") is "InvokePattern" or "SelectionItemPattern",
            "Navigation did not use InvokePattern or SelectionItemPattern on the exact approved leaf.");
        var clock = Stopwatch.StartNew();
        SurveyPageReady ready;
        var polls = 0;
        do
        {
            ready = await ReadReadyAsync(page, node, token);
            if (ready.Selected && ready.HeadingMatched)
                return ready;
            if (++polls >= 20 || clock.Elapsed >= TimeSpan.FromSeconds(Math.Min(10, profile.TimeoutSeconds)))
                break;
            await Task.Delay(150, token);
        } while (true);
        return ready;
    }

    private async Task<NavigationTree> ExpandGroupsAsync(
        NavigationTree tree, IEnumerable<string> groups, CancellationToken token)
    {
        foreach (var id in groups)
        {
            Require(tree.Groups.ContainsKey(id), $"'{id}' is not a declared navigation group.");
            if (tree.Groups[id])
                continue;
            await SetGroupAsync(tree, id, expanded: true, token);
            tree = await ReadTreeAsync(token);
        }
        return tree;
    }

    private async Task SetGroupAsync(NavigationTree tree, string id, bool expanded, CancellationToken token)
    {
        var node = tree.Group(id);
        var control = await ConfirmAsync(node, token);
        if (control.Expanded == expanded)
            return;
        control = await PrepareAsync(node, token, control);
        if (control.Expanded == expanded)
            return;
        // PowerToys NavigationView groups toggle on real Enter, not tree-view arrows or a bounds-center click.
        var response = await UiAsync(["send-keys", "enter", "--via", "send-input", "--target", control.Selector], token);
        Require(Integer(response, "hwnd") == Target.WindowHandle && Text(response, "target") == control.Selector
            && Text(response, "keys") == "enter" && Text(response, "via") == "send-input" && Integer(response, "actionCount") == 1,
            "Group expansion did not send exactly Enter to the approved group in the original window.");
        var clock = Stopwatch.StartNew();
        for (var poll = 0; poll < 20; poll++)
        {
            var after = await ConfirmAsync(node, token, settling: true);
            if (after.Expanded == expanded)
                return;
            if (clock.Elapsed >= TimeSpan.FromMilliseconds(Math.Min(2500, profile.TimeoutSeconds * 1000)))
                break;
            await Task.Delay(150, token);
        }
        throw Blocked("group-not-settled", $"Navigation group '{id}' did not reach its requested expansion state; Enter will not be retried blindly.", true);
    }

    private async Task<Control> PrepareAsync(NavigationNode node, CancellationToken token, Control? control = null)
    {
        control ??= await ConfirmAsync(node, token);
        if (!control.Enabled)
            throw Blocked("navigation-disabled", $"Navigation '{node.Id}' is disabled.", true);
        if (control.Offscreen)
        {
            navigationChanged = true;
            ExactAction(await UiAsync(["scroll-into-view", control.Selector], token), control.Selector);
            control = await ConfirmAsync(node, token);
        }
        if (control.Offscreen || !control.Enabled)
            throw Blocked("navigation-not-visible", $"Navigation '{node.Id}' is not enabled and on screen after scrolling.", true);
        navigationChanged = true;
        ExactAction(await UiAsync(["focus", control.Selector], token), control.Selector);
        control = await ConfirmAsync(node, token);
        if (!control.Focused || control.Offscreen || !control.Enabled)
            throw Blocked("navigation-focus-lost", $"Focus was not confirmed on the approved navigation '{node.Id}'. No activation was sent.", true);
        return control;
    }

    private async Task<Control> ConfirmAsync(NavigationNode node, CancellationToken token, bool settling = false)
    {
        var response = await UiAsync(["get-property", node.Selector!], token);
        var properties = Member(response, "properties", JsonValueKind.Object);
        var resolvedSelector = Text(response, "elementId");
        Require(SafeSelector(resolvedSelector) && (node.Selector == node.Id || resolvedSelector == node.Selector)
            && Text(properties, "AutomationId") == node.Id
            && Text(properties, "ClassName") == profile.ItemClassName && Text(properties, "ControlType") == "ListItem"
            && Text(properties, "Name") == node.Name, $"Property lookup no longer resolves the exact approved navigation '{node.Id}'.");
        if (Present(response, "hwnd"))
            Require(Integer(response, "hwnd") == Target.WindowHandle, "Navigation properties belong to a different window.");
        Require(!Present(properties, "ToggleState") && !Present(properties, "Value")
            && !Present(properties, "Value.Value") && !Present(properties, "IsReadOnly"),
            $"Navigation '{node.Id}' exposes a setting toggle/value pattern.");
        var expansion = Text(properties, "ExpandCollapseState");
        bool? expanded = null;
        if (node.Expanded is not null)
        {
            Require(profile.GroupIds.Contains(node.Id!, StringComparer.Ordinal)
                && (expansion is "Expanded" or "Collapsed" || (settling && expansion == "PartiallyExpanded")),
                "A declared navigation group no longer exposes a known expansion state.");
            expanded = expansion == "PartiallyExpanded" ? null : expansion == "Expanded";
        }
        else
            Require(expansion is null, "A leaf navigation entry now exposes an expansion pattern.");
        return new(resolvedSelector!, PropertyBoolean(properties, "IsEnabled"), PropertyBoolean(properties, "IsOffscreen"),
            node.Expanded is null ? PropertyBoolean(properties, "IsSelected") : null,
            expanded, bool.TryParse(Text(properties, "HasKeyboardFocus"), out var focus) && focus);
    }

    private async Task<string?> ReadSelectionAsync(NavigationTree tree, bool visibleOnly, CancellationToken token)
    {
        string? selected = null;
        foreach (var page in tree.Pages)
        {
            var node = tree.Leaf(page);
            if (tree.HiddenByGroup(page) || (visibleOnly && node.Offscreen))
                continue;
            if ((await ConfirmAsync(node, token)).Selected != true)
                continue;
            Require(selected is null, "Multiple internal navigation leaves claim to be selected.");
            selected = page.Id;
        }
        return selected;
    }

    private async Task<SurveyPageReady> ReadReadyAsync(SurveyPageDefinition page, NavigationNode node, CancellationToken token)
    {
        var control = await ConfirmAsync(node, token);
        var response = await UiAsync(["search", profile.HeadingAutomationId, "--max", Number(MaxSearchResults)], token, allowEmptySearch: true);
        var matches = Array(response, "matches");
        Require(Bool(response, "hasMore") == false && matches.GetArrayLength() <= MaxSearchResults
            && Integer(response, "matchCount") == matches.GetArrayLength(), "A truncated or malformed heading search cannot prove page readiness.");
        var headings = new List<string?>();
        foreach (var match in matches.EnumerateArray())
        {
            if (Text(match, "automationId") != profile.HeadingAutomationId || Text(match, "type") != "Text"
                || Bool(match, "isOffscreen") != false)
                continue;
            if (Present(match, "windowHandle"))
                Require(Integer(match, "windowHandle") == Target.WindowHandle, "A heading belongs to a different window.");
            // Keep only the approved heading label; never retain search/property Value fields or input text.
            headings.Add(Text(match, "name"));
        }
        var matched = headings.Where(heading => SurveyProfileFile.MatchesHeading(profile, page, heading)).ToArray();
        if (matched.Length == 1)
            return new(control.Selected == true, true, matched[0], DateTimeOffset.UtcNow);
        if (matched.Length == 0 && profile.HeadingWithoutIdPageIds.Contains(page.Id, StringComparer.Ordinal))
        {
            var before = (await ReadTreeAsync(token)).PaneBounds;
            if (before is null)
                return new(control.Selected == true, false, null, DateTimeOffset.UtcNow);
            var positioned = new List<string>();
            foreach (var name in new[] { page.Name }.Concat(profile.HeadingAliases.GetValueOrDefault(page.Id) ?? []).Distinct(StringComparer.Ordinal))
            {
                var found = await UiAsync(["search", name, "--max", Number(MaxSearchResults)], token, allowEmptySearch: true);
                Require(Bool(found, "hasMore") == false, "A truncated title search cannot prove page readiness.");
                foreach (var match in Array(found, "matches").EnumerateArray())
                {
                    if (Text(match, "type") != "Text" || Bool(match, "isOffscreen") != false
                        || Text(match, "name")?.Trim() != name.Trim() || !string.IsNullOrEmpty(Text(match, "automationId")))
                        continue;
                    if (Present(match, "windowHandle"))
                        Require(Integer(match, "windowHandle") == Target.WindowHandle, "A heading belongs to a different window.");
                    if (ElementBounds(match) is { } bounds && bounds.Left >= before.Right
                        && bounds.Top >= before.Top
                        && bounds.Bottom <= before.Top + before.Height * profile.HeadingWithoutIdTopFraction)
                        positioned.Add(Text(match, "name")!);
                }
            }
            var after = (await ReadTreeAsync(token)).PaneBounds;
            var confirmed = positioned.Count == 1 && before == after;
            return new(control.Selected == true, confirmed, confirmed ? positioned[0] : null, DateTimeOffset.UtcNow)
            {
                EvidenceKind = "visible-name-outside-navigation"
            };
        }
        return new(control.Selected == true, false, headings.Count == 1 ? headings[0] : null, DateTimeOffset.UtcNow);
    }

    private async Task<NavigationTree> ReadTreeAsync(CancellationToken token)
    {
        var response = await UiAsync(["inspect", profile.NavigationPaneId, "--depth", Number(profile.TreeDepth)], token);
        NavigationTree tree;
        try { tree = new(response, profile, Target); }
        catch (Exception ex) when (ex is not SurveyBlockedException && ex is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            throw Blocked("profile-mismatch", "Navigation inspection returned malformed scope metadata.", false);
        }
        MatchInventory(tree, complete: false);
        if (originalPage is null && originalNavigation?.SelectedPageId is { } selected)
            originalPage = tree.Pages.SingleOrDefault(page => page.Id == selected);
        return tree;
    }

    private async Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken token, bool allowEmptySearch = false)
    {
        CommandResult response;
        try { response = await platform.UiAsync(Target, arguments, TimeSpan.FromSeconds(profile.TimeoutSeconds), token); }
        catch (Exception ex) when (ex is not (SurveyBlockedException or OperationCanceledException) && IsFailure(ex))
        {
            throw Blocked("native-command-failed", $"Native '{arguments[0]}' could not complete. No fallback action was attempted; raw output was not recorded.", true);
        }
        Require(response.Stdout.Length <= AuditFiles.MaxJsonBytes, "Native navigation output exceeded its size budget.");
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(response.Stdout, new JsonDocumentOptions { MaxDepth = 128 });
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Blocked("native-response-invalid", $"Native '{arguments[0]}' did not return navigation JSON; raw output was not recorded.", true);
        }
        Require(root.ValueKind == JsonValueKind.Object, "Native navigation output is not an object.");
        if (Present(root, "error") || (response.ExitCode != 0 && !(allowEmptySearch && response.ExitCode == 1
            && Bool(root, "hasMore") == false && Array(root, "matches").GetArrayLength() == 0)))
            throw Blocked("native-command-failed", $"Native '{arguments[0]}' failed (exit {response.ExitCode}). No fallback action was attempted.", true);
        return root;
    }

    private void ExactAction(JsonElement response, string selector) =>
        Require(Integer(response, "hwnd") == Target.WindowHandle && Text(response, "elementId") == selector,
            "The action response does not identify the exact approved element and original window.");

    private SurveyPageDefinition ApprovedPage(SurveyPageDefinition page)
    {
        Require(approvedPages.TryGetValue(page.Id, out var approved) && SamePage(approved, page),
            $"Page '{page.Id}' is not in the frozen internal-page inventory.");
        return approved!;
    }

    private void Freeze(SurveyInventory frozen)
    {
        try { SurveyStore.ValidateInventory(frozen, profile); }
        catch (InvalidDataException ex) { throw Blocked("profile-mismatch", ex.Message, false); }
        Require(frozen.NavigationIds.All(IsIdentifier) && profile.GroupIds.All(id => frozen.NavigationIds.Contains(id, StringComparer.Ordinal)),
            "The frozen navigation ID inventory is inconsistent.");
        if (inventory is not null)
        {
            Require(frozen.Pages.Count == inventory.Pages.Count && frozen.Pages.All(page =>
                approvedPages.TryGetValue(page.Id, out var approved) && SamePage(page, approved))
                && frozen.NavigationIds.ToHashSet(StringComparer.Ordinal).SetEquals(inventory.NavigationIds),
                "The already-frozen navigation inventory cannot be replaced.");
            return;
        }
        inventory = CopyInventory(frozen);
        approvedPages = inventory.Pages.ToDictionary(page => page.Id, StringComparer.Ordinal);
    }

    private void AcceptOriginal(SurveyNavigationState original)
    {
        Require(original.ExpandedGroups.Count == profile.GroupIds.Count
            && profile.GroupIds.All(original.ExpandedGroups.ContainsKey)
            && (original.SelectedPageId is null || IsIdentifier(original.SelectedPageId)),
            "Original navigation must include the observed state of every declared group.");
        if (originalNavigation is not null)
        {
            Require(profile.GroupIds.All(id => original.ExpandedGroups[id] == originalNavigation.ExpandedGroups[id])
                && (original.SelectedPageId is null || originalNavigation.SelectedPageId is null
                    || original.SelectedPageId == originalNavigation.SelectedPageId),
                "The original navigation restoration state changed.");
            if (originalNavigation.SelectedPageId is null && original.SelectedPageId is not null)
                originalNavigation = new(original.SelectedPageId, originalNavigation.ExpandedGroups);
        }
        else
            originalNavigation = Copy(original);
    }

    private SurveyNativeWindow CheckTarget(AuditTarget candidate)
    {
        if (candidate.ProcessId <= 0 || candidate.WindowHandle <= 0 || candidate.ProcessStartedAtUtc == default
            || candidate.ProcessId != Target.ProcessId || candidate.WindowHandle != Target.WindowHandle
            || candidate.ProcessStartedAtUtc != Target.ProcessStartedAtUtc
            || !string.Equals(candidate.ProcessName, Target.ProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Target.ProcessName, profile.ProcessName, StringComparison.OrdinalIgnoreCase))
            throw Blocked("target-changed", "The survey requires the exact originally selected process, start time and window.", false);
        VerifyIdentity(candidate);
        SurveyNativeWindow window;
        try { window = native.ReadWindow(candidate); }
        catch (Exception ex) when (ex is not SurveyBlockedException && IsFailure(ex))
        {
            VerifyIdentity(candidate);
            throw Blocked("environment-unavailable", "The selected window's desktop and physical bounds could not be read.", true);
        }
        VerifyIdentity(candidate);
        SurveyNativeContext.ValidateWindow(window);
        if (context is not null && (window.Width != context.Environment.WindowWidth
            || window.Height != context.Environment.WindowHeight || window.Dpi != context.Environment.Dpi))
            throw Blocked("environment-changed", "The selected window's physical dimensions or DPI changed. Restore them before resuming.", true);
        return window;
    }

    private void VerifyIdentity(AuditTarget target)
    {
        try { inner.VerifyTarget(target); }
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Blocked("target-changed", "The original process/window identity can no longer be verified. Start a new survey after selecting a target manually.", false);
        }
    }

    private void CountCommand()
    {
        if (restoring ? ++restorationCommands > 128 : nativeCommands >= commandLimit)
            throw Blocked("command-budget-exceeded", "The bounded native command budget was exhausted.", true);
        nativeCommands++;
    }

    private static SurveyNavigationState Copy(SurveyNavigationState state) =>
        new(state.SelectedPageId, new Dictionary<string, bool>(state.ExpandedGroups, StringComparer.Ordinal));
    private static SurveyInventory CopyInventory(SurveyInventory value) =>
        new(value.Pages.Select(page => page with { GroupIds = page.GroupIds.ToArray() }).ToArray(),
            value.Excluded.ToArray(), value.NavigationIds.ToArray(), Copy(value.OriginalNavigation));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsFailure(Exception ex) => AuditRunner.IsExecutionFailure(ex) || ex is Win32Exception or SecurityException;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed record Control(string Selector, bool Enabled, bool Offscreen, bool? Selected, bool? Expanded, bool Focused);

    private sealed class CountingPlatform(NativeSurveySession owner) : IAuditPlatform
    {
        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult(owner.winappVersion);
        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) =>
            throw new InvalidOperationException("A survey never attaches to a replacement target.");
        public void VerifyTarget(AuditTarget target) => owner.CheckTarget(target);
        public Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
            ExecuteAsync(target, () => owner.inner.UiAsync(target, arguments, timeout, token), token);
        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token) =>
            ExecuteAsync(target, () => owner.inner.AxeAsync(target, timeout, token), token);
        private async Task<T> ExecuteAsync<T>(AuditTarget target, Func<Task<T>> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            VerifyTarget(target);
            owner.CountCommand();
            var clock = Stopwatch.StartNew();
            try
            {
                return await operation();
            }
            finally
            {
                owner.nativeMilliseconds += clock.ElapsedMilliseconds;
                VerifyTarget(target);
            }
        }
    }
}
