using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public sealed class JourneyPageSession : IJourneySession
{
    private readonly IAppSession app;
    private readonly PinnedPlatform platform;
    private readonly string winappVersion;
    private readonly string outputRoot;
    private readonly bool screenshot;
    private AuditTarget? target;
    private string? acceptedDefinition;
    private int sequence;
    private int timeoutSeconds;
    private bool discovered;
    private bool disposed;

    public JourneyPageSession(
        IAppSession app, IAuditPlatform platform, string winappVersion, string outputRoot, bool screenshot = true,
        Func<AuditTarget, IReadOnlyList<long>>? visibleWindows = null)
    {
        this.app = app;
        this.platform = new(platform, app, visibleWindows ?? NativeVisibleWindows);
        this.winappVersion = winappVersion;
        this.outputRoot = Path.GetFullPath(outputRoot);
        this.screenshot = screenshot;
    }

    public async Task<IReadOnlyList<JourneyCandidate>> DiscoverAsync(
        JourneyDefinition definition, CancellationToken token)
    {
        AcceptDefinition(definition);
        target ??= await platform.AttachAsync(Options(processId: app.ProcessId), token);
        var tree = await InspectAsync(null, token);
        var navigation = Navigation(tree, definition);
        if (!discovered)
            RequireActivePage(tree, definition, definition.InitialPageId);
        foreach (var node in navigation)
            await ConfirmAsync(node, null, token);
        var candidates = definition.Pages.Zip(navigation,
            (page, node) => new JourneyCandidate(page.Id, page.NavigationId, node.Name, node.Type)).ToArray();
        discovered = true;
        return candidates;
    }

    public async Task<JourneyPageResult> VisitAsync(
        JourneyDefinition definition, JourneyPageDefinition page, JourneyRoute route,
        string phase, string evidencePath, CancellationToken token)
    {
        var directory = NewDirectory(evidencePath);
        var checks = new List<JourneyCheck>();
        IReadOnlyList<string> observed = [];
        AuditReport? audit = null;
        var exercise = page.Exercise is { } guard ? new ExerciseState(guard) : null;
        string? error = null;
        string? initialReadyId = null;
        NavigationEvent? reset = null;
        NavigationEvent? navigation = null;
        try
        {
            AcceptDefinition(definition);
            ValidateRoute(definition, page, route);
            var initial = definition.Pages.Single(item => item.Id == definition.InitialPageId);
            initialReadyId = initial.ReadyId;
            AuditFiles.WriteNew(Path.Combine(directory, "visit-request.json"), new { phase, route, page });
            if (!discovered)
                await DiscoverAsync(definition, token);
            var before = await InspectAsync(directory, token);
            var sourcePage = ActivePage(before, definition)
                ?? throw new InvalidOperationException("No declared page is active before the initial-page reset.");
            var isHomeVisit = page.Id == initial.Id;
            var home = new NavigationEvent(
                isHomeVisit ? route : new(initial.Id, initial.Id, initial.NavigationId, initial.ReadyId), sourcePage);
            if (isHomeVisit)
                navigation = home;
            else
                reset = home;
            var ready = await NavigateAsync(
                definition, before, home, isHomeVisit ? "navigation" : "home-reset", directory, token);
            checks.Add(new("initial-page-ready", true, initial.ReadyId, initial.ReadyId));

            if (!isHomeVisit)
            {
                navigation = new(route, initial.Id);
                ready = await NavigateAsync(definition, ready, navigation, "navigation", directory, token);
            }
            checks.Add(new("page-ready", true, page.ReadyId, page.ReadyId));
            observed = ObserveControls(ready, page, "before", checks);
            RequireControls(checks);

            audit = await new AuditRunner(platform).RunAsync(
                Options(windowHandle: target!.WindowHandle), target, winappVersion, directory, token);
            AuditFiles.WriteNew(Path.Combine(directory, "scan.json"), audit);
            AuditReportRenderer.Write(Path.Combine(directory, "report.html"), audit);
            token.ThrowIfCancellationRequested();
            var requiredEvidence = JourneyPolicy.HasRequiredAuditEvidence(audit);
            checks.Add(new("required-audit-evidence", requiredEvidence,
                "Scoped Axe and complete required capture, allowing only explicit privacy withholding.",
                audit.Error ?? $"Capture status: {audit.Status}."));
            if (!requiredEvidence)
                throw new InvalidOperationException(
                    "The page's required audit evidence is incomplete. " +
                    (audit.Error ?? $"Capture status: {audit.Status}; see scan.json for the reported gaps."));

            if (exercise is not null)
                await ExerciseAsync(definition, page, exercise, directory, token);

            var after = await InspectAsync(directory, token);
            RequireActivePage(after, definition, page.Id);
            observed = observed.Intersect(ObserveControls(after, page, "after", checks), StringComparer.Ordinal).ToArray();
            RequireControls(checks);
            if (exercise is not null)
                exercise.VerifiedTargets = page.RequiredControlIds
                    .Prepend(exercise.Guard.TargetId).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
        {
            error = ex is OperationCanceledException ? "Execution cancelled." : ex.Message;
            if (!checks.Any(check => check.Id == "initial-page-ready"))
                checks.Add(new("initial-page-ready", false, initialReadyId ?? definition.InitialPageId, error));
            if (!checks.Any(check => check.Id == "page-ready"))
                checks.Add(new("page-ready", false, page.ReadyId, error));
        }

        if (token.IsCancellationRequested)
            error ??= "Execution cancelled.";
        var observation = exercise is null ? null : new Observation
        {
            Phase = phase,
            ProcessId = target?.ProcessId,
            Scan = audit?.Axe,
            Checks = exercise.Checks,
            FocusTrace = exercise.Trace,
            VerifiedTargets = error is null ? exercise.VerifiedTargets : [],
            Error = error,
            Status = error is not null || audit?.Axe is null ? "inconclusive"
                : exercise.Checks.All(check => check.Passed) && audit.Axe.Findings.Count == 0 ? "passed" : "failed"
        };
        if (observation is not null)
            AuditFiles.WriteNew(Path.Combine(directory, "exercise.json"), observation);
        var result = new JourneyPageResult
        {
            PageId = page.Id,
            Route = route,
            EvidencePath = evidencePath,
            Checks = checks,
            ObservedControlIds = observed,
            Audit = audit,
            Exercise = observation,
            Error = error,
            Status = error is not null || audit is null || observation?.Status == "inconclusive" ? "inconclusive"
                : audit.RuleFailureCount > 0 || observation?.Status == "failed" ? "failed" : "passed"
        };
        if (reset is not null)
        {
            AuditFiles.WriteNew(Path.Combine(directory, "home-reset-result.json"), new
            {
                reset.Route, reset.SourcePageId, reset.RequestedAtUtc, reset.InvokedAtUtc, reset.ReadyAtUtc,
                status = reset.ReadyAtUtc is null ? "inconclusive" : "ready",
                error = reset.ReadyAtUtc is null ? error : null
            });
        }
        AuditFiles.WriteNew(Path.Combine(directory, "navigation-result.json"), new
        {
            route, sourcePageId = navigation?.SourcePageId, requestedAtUtc = navigation?.RequestedAtUtc,
            invokedAtUtc = navigation?.InvokedAtUtc, readyAtUtc = navigation?.ReadyAtUtc,
            completedAtUtc = DateTimeOffset.UtcNow, result.Status, error
        });
        AuditFiles.WriteNew(Path.Combine(directory, "page-result.json"), result);
        return result;
    }

    private void AcceptDefinition(JourneyDefinition definition)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var serialized = JsonSerializer.Serialize(definition, Json.Options);
        if (acceptedDefinition is not null && acceptedDefinition != serialized)
            throw new InvalidDataException("The approved journey definition changed during this owned session.");
        if (definition.TimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("Journey timeoutSeconds must be 1..120.");
        acceptedDefinition ??= serialized;
        timeoutSeconds = definition.TimeoutSeconds;
    }

    private static void ValidateRoute(
        JourneyDefinition definition, JourneyPageDefinition page, JourneyRoute route)
    {
        var declared = definition.Pages.Where(item => item.Id == page.Id).ToArray();
        if (declared.Length != 1
            || JsonSerializer.Serialize(declared[0], Json.Options) != JsonSerializer.Serialize(page, Json.Options))
            throw new InvalidDataException($"Page '{page.Id}' does not match the approved page definition.");
        if (route.PageId != page.Id || route.NavigationId != page.NavigationId || route.ReadyId != page.ReadyId
            || definition.Pages.Count(item => item.Id == route.FromPageId) != 1)
            throw new InvalidDataException("The route's page, navigation, ready marker or origin does not match the approved definition.");
        if (route.FromPageId != definition.InitialPageId)
            throw new InvalidDataException($"Recorded page routes must start at '{definition.InitialPageId}'; the initial-page reset is part of each visit.");
    }

    private async Task<Tree> NavigateAsync(
        JourneyDefinition definition, Tree before, NavigationEvent navigation,
        string evidencePrefix, string directory, CancellationToken token)
    {
        var page = definition.Pages.Single(item => item.Id == navigation.Route.PageId);
        RequireActivePage(before, definition, navigation.SourcePageId);
        var node = Navigation(before, definition).Single(item => item.Id == page.NavigationId);
        var control = await ConfirmAsync(node, directory, token);
        if (!PropertyIs(control.Properties, "IsEnabled", true)
            || !PropertyIs(control.Properties, "IsOffscreen", false))
            throw new InvalidOperationException($"Approved navigation '{page.NavigationId}' must be enabled and visible.");
        if (Text(control.Properties, "ToggleState") is not null
            || Text(control.Properties, "ExpandCollapseState") is not null)
            throw new InvalidOperationException(
                $"Navigation '{page.NavigationId}' exposes a toggle or expansion pattern rather than the approved navigation action.");

        navigation.RequestedAtUtc = DateTimeOffset.UtcNow;
        AuditFiles.WriteNew(Path.Combine(directory, evidencePrefix + "-request.json"),
            new { target, navigation.Route, navigation.SourcePageId, navigation.RequestedAtUtc, control.Selector });
        var invoked = await UiAsync(["invoke", control.Selector], directory, token);
        navigation.InvokedAtUtc = DateTimeOffset.UtcNow;
        if (AuditJson.Long(invoked, "hwnd") != target!.WindowHandle
            || AuditJson.String(invoked, "elementId") != control.Selector
            || AuditJson.String(invoked, "pattern") is not ("InvokePattern" or "SelectionItemPattern"))
            throw new InvalidDataException("Navigation did not invoke the exact approved element through a supported pattern.");

        var ready = await WaitForPageAsync(definition, page, navigation.SourcePageId, directory, token);
        navigation.ReadyAtUtc = DateTimeOffset.UtcNow;
        return ready;
    }

    private static IReadOnlyList<Node> Navigation(Tree tree, JourneyDefinition definition)
    {
        var root = tree.Exact(definition.NavigationRootId);
        var approved = definition.Pages.Select(page => page.NavigationId).ToHashSet(StringComparer.Ordinal);
        foreach (var node in tree.Nodes.Where(node => node.IsDescendantOf(root)
            && node.Type is "Button" or "TabItem" or "ListItem"))
        {
            if (node.Id is null || !approved.Contains(node.Id))
                throw new InvalidOperationException(
                    $"Undeclared navigation entry '{node.Id ?? "(no AutomationId)"}' is under '{definition.NavigationRootId}'. " +
                    "Declare it in the approved contract or remove it from the navigation container.");
        }
        return definition.Pages.Select(page =>
        {
            var node = tree.Exact(page.NavigationId);
            if (!node.IsDescendantOf(root))
                throw new InvalidOperationException(
                    $"Navigation '{page.NavigationId}' is not a descendant of '{definition.NavigationRootId}'.");
            if (node.Type is not ("Button" or "TabItem" or "ListItem") || !node.Invokable
                || node.ToggleState is not null || node.ExpandState is not null)
                throw new InvalidOperationException(
                    $"Navigation '{page.NavigationId}' must be an approved Button, TabItem or ListItem with an invokable UIA pattern (Invoke or SelectionItem only).");
            if (node.Offscreen)
                throw new InvalidOperationException($"Navigation '{page.NavigationId}' is not visible.");
            return node;
        }).ToArray();
    }

    private static string? ActivePage(Tree tree, JourneyDefinition definition)
    {
        var active = new List<string>();
        foreach (var page in definition.Pages)
        {
            var matches = tree.Matches(page.ReadyId);
            if (matches.Length > 1)
                throw new InvalidOperationException($"Page-ready AutomationId '{page.ReadyId}' is ambiguous; found {matches.Length} elements.");
            if (matches.Length == 1 && !matches[0].Offscreen)
                active.Add(page.Id);
        }
        if (active.Count > 1)
            throw new InvalidOperationException($"Expected exactly one active declared page anchor; found {string.Join(", ", active)}.");
        return active.SingleOrDefault();
    }

    private static Node RequireActivePage(Tree tree, JourneyDefinition definition, string expectedPage)
    {
        var actual = ActivePage(tree, definition);
        if (actual != expectedPage)
            throw new InvalidOperationException(
                $"Expected active page '{expectedPage}', but found '{actual ?? "no declared ready marker"}'.");
        return tree.Exact(definition.Pages.Single(page => page.Id == expectedPage).ReadyId);
    }

    private async Task<Tree> WaitForPageAsync(
        JourneyDefinition definition, JourneyPageDefinition page, string fromPage,
        string directory, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        string? actual;
        do
        {
            var tree = await InspectAsync(directory, token);
            actual = ActivePage(tree, definition);
            if (actual == page.Id)
                return tree;
            if (actual is not null && actual != fromPage)
                throw new InvalidOperationException($"Navigation to '{page.Id}' activated the unexpected page '{actual}'.");
            await Task.Delay(100, token);
        } while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds));
        throw new TimeoutException(
            $"Page '{page.Id}' did not expose its unique visible ready marker '{page.ReadyId}' before the deadline; " +
            $"active page: '{actual ?? "none"}'.");
    }

    private static IReadOnlyList<string> ObserveControls(
        Tree tree, JourneyPageDefinition page, string stage, List<JourneyCheck> checks)
    {
        var root = tree.Exact(page.ReadyId);
        var present = new List<string>();
        var failures = new List<string>();
        foreach (var id in page.RequiredControlIds)
        {
            var matches = tree.Matches(id);
            if (matches.Length == 1 && (matches[0] == root || matches[0].IsDescendantOf(root)))
                present.Add(id);
            else
                failures.Add($"{id}: found {matches.Length} window matches; exactly one must belong to '{page.ReadyId}'");
        }
        checks.Add(new("required-controls-" + stage, failures.Count == 0,
            string.Join(", ", page.RequiredControlIds), failures.Count == 0 ? string.Join(", ", present) : string.Join("; ", failures)));
        return present;
    }

    private static void RequireControls(List<JourneyCheck> checks)
    {
        if (checks.LastOrDefault(check => check.Id.StartsWith("required-controls-", StringComparison.Ordinal)) is { Passed: false } failed)
            throw new InvalidOperationException("Required page controls are missing, ambiguous or outside the page: " + failed.Actual);
    }

    private async Task ExerciseAsync(
        JourneyDefinition definition, JourneyPageDefinition page, ExerciseState state,
        string directory, CancellationToken token)
    {
        var guard = state.Guard;
        var tree = await InspectAsync(directory, token);
        var root = RequireActivePage(tree, definition, page.Id);
        Node InPage(string id)
        {
            var node = tree.Exact(id);
            if (!node.IsDescendantOf(root))
                throw new InvalidOperationException($"Exercise control '{id}' is not under '{page.ReadyId}'.");
            return node;
        }
        var targetNode = InPage(guard.TargetId);
        var anchorNode = InPage(guard.AnchorId);
        var resultNode = InPage(guard.ResultId);
        if (targetNode.Type != "Button")
            throw new InvalidOperationException($"Exercise target '{guard.TargetId}' must be a Button.");
        var targetControl = await ConfirmAsync(targetNode, directory, token);
        var anchor = await ConfirmAsync(anchorNode, directory, token);
        var initial = await ConfirmAsync(resultNode, directory, token);
        state.Checks[0] = new("accessible-name", Text(targetControl.Properties, "Name") == guard.ExpectedName,
            guard.ExpectedName, Text(targetControl.Properties, "Name"));
        state.Checks[1] = new("target-enabled", PropertyIs(targetControl.Properties, "IsEnabled", true),
            "True", Text(targetControl.Properties, "IsEnabled"));
        state.Checks[2] = new("target-visible", PropertyIs(targetControl.Properties, "IsOffscreen", false),
            "False", Text(targetControl.Properties, "IsOffscreen"));
        RequireInitialResult(guard, Text(initial.Properties, "Name"), beforeEnter: false);

        await UiAsync(["set-value", anchor.Selector, guard.InputText], directory, token);
        var value = await ConfirmAsync(anchorNode, directory, token);
        if (Text(value.Properties, "Value") != guard.InputText)
            throw new InvalidOperationException($"The approved input '{guard.AnchorId}' did not retain the fixture text.");
        await UiAsync(["send-keys", "home", "--via", "send-input", "--target", value.Selector], directory, token);
        var focused = await ReadFocusAsync(0, directory, token);
        state.Trace.Add(focused.Observation);
        await RequireFocusAsync(anchorNode, focused, directory, token);

        // The anchor is the only directly focused control. No target-focus or Invoke fallback is allowed.
        for (var tab = 1; tab <= guard.MaxTabs; tab++)
        {
            await UiAsync(["send-keys", "tab", "--via", "send-input"], directory, token);
            focused = await ObserveFocusChangeAsync(focused, tab, directory, token);
            state.Trace.Add(focused.Observation);
            if (focused.Observation.AutomationId == guard.TargetId)
                break;
        }
        var reachable = focused.Observation.AutomationId == guard.TargetId;
        state.Checks[3] = new("keyboard-reachable", reachable, guard.TargetId, focused.Observation.AutomationId);
        if (!reachable)
        {
            state.Checks[4] = new("keyboard-activation", false, guard.ExpectedResult, "Target not reached; activation not attempted.");
            return;
        }

        await RequireFocusAsync(targetNode, focused, directory, token);
        var beforeEnter = await ConfirmAsync(resultNode, directory, token);
        RequireInitialResult(guard, Text(beforeEnter.Properties, "Name"), beforeEnter: true);
        var finalFocus = await ReadFocusAsync(focused.Observation.Tab, directory, token);
        await RequireFocusAsync(targetNode, finalFocus, directory, token);
        await UiAsync(["send-keys", "enter", "--via", "send-input"], directory, token);
        var result = await WaitForResultAsync(definition, page, directory, token);
        state.Checks[4] = new("keyboard-activation", result == guard.ExpectedResult, guard.ExpectedResult, result);
    }

    private static void RequireInitialResult(JourneyExercise guard, string? actual, bool beforeEnter)
    {
        if (actual == guard.ExpectedResult)
            throw new InvalidOperationException(beforeEnter
                ? "The successful result appeared before Enter. Keyboard activation cannot be established."
                : "The successful result is already present before the exercise. Reset application state before replay.");
        if (actual != guard.InitialResult)
            throw new InvalidOperationException(
                $"Expected initial result '{guard.InitialResult}'{(beforeEnter ? " before Enter" : "")}, but found '{actual ?? "(missing)"}'.");
    }

    private async Task<string?> WaitForResultAsync(
        JourneyDefinition definition, JourneyPageDefinition page, string directory, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        string? text;
        do
        {
            var tree = await InspectAsync(directory, token);
            var root = RequireActivePage(tree, definition, page.Id);
            var result = tree.Exact(page.Exercise!.ResultId);
            if (!result.IsDescendantOf(root))
                throw new InvalidOperationException($"Exercise result '{result.Id}' moved outside the active page.");
            text = Text((await ConfirmAsync(result, directory, token)).Properties, "Name");
            if (text == page.Exercise.ExpectedResult)
                return text;
            await Task.Delay(100, token);
        } while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds));
        return text;
    }

    private async Task<Control> ConfirmAsync(Node node, string? directory, CancellationToken token)
    {
        var search = await UiAsync(["search", node.Id!], directory, token);
        if (AuditJson.Boolean(search, "hasMore") != false)
            throw new InvalidOperationException($"Search for '{node.Id}' was truncated; uniqueness cannot be established.");
        var matches = AuditJson.Array(search, "matches").EnumerateArray()
            .Where(match => Text(match, "automationId") == node.Id).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected exactly one AutomationId '{node.Id}' in the owned window; found {matches.Length}.");
        var match = matches[0];
        if (Text(match, "type") != node.Type
            || Text(match, "selector") != node.Selector
            || (AuditJson.Boolean(match, "isInvokable") == true) != node.Invokable
            || Text(match, "toggleState") != node.ToggleState || Text(match, "expandState") != node.ExpandState
            || (match.TryGetProperty("windowHandle", out var window) && window.ValueKind != JsonValueKind.Null
                && window.GetInt64() != target!.WindowHandle))
            throw new InvalidOperationException($"Control '{node.Id}' changed identity or belongs to a different window.");
        var response = await UiAsync(["get-property", node.Selector], directory, token);
        var properties = response.GetProperty("properties");
        if (Text(properties, "AutomationId") != node.Id || Text(properties, "ControlType") != node.Type)
            throw new InvalidOperationException($"Property lookup no longer resolves the exact approved control '{node.Id}'.");
        // Native get-property returns the current runtime selector rather than a name-search fallback.
        return new(AuditJson.String(response, "elementId"), properties.Clone());
    }

    private async Task<FocusState> ReadFocusAsync(int tab, string directory, CancellationToken token)
    {
        var response = await UiAsync(["get-focused"], directory, token);
        if (AuditJson.Boolean(response, "hasFocus") != true)
            throw new InvalidOperationException("The owned application lost observable keyboard focus; no more input will be sent.");
        var element = response.GetProperty("element");
        return new(AuditJson.String(element, "selector"),
            new(tab, Text(element, "automationId"), Text(element, "name")));
    }

    private async Task RequireFocusAsync(Node expected, FocusState focused, string directory, CancellationToken token)
    {
        var actual = await ConfirmAsync(expected, directory, token);
        if (focused.Observation.AutomationId != expected.Id || focused.Selector != actual.Selector
            || !PropertyIs(actual.Properties, "HasKeyboardFocus", true))
            throw new InvalidOperationException($"Actual keyboard focus is not on the exact approved control '{expected.Id}'.");
    }

    private async Task<FocusState> ObserveFocusChangeAsync(
        FocusState previous, int tab, string directory, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        FocusState current;
        do
        {
            await Task.Delay(100, token);
            current = await ReadFocusAsync(tab, directory, token);
        } while (current.Selector == previous.Selector && clock.Elapsed < TimeSpan.FromMilliseconds(700));
        return current;
    }

    private async Task<Tree> InspectAsync(string? directory, CancellationToken token) =>
        new(await UiAsync(["inspect", "--depth", "30"], directory, token), target!);

    private async Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, string? directory, CancellationToken token)
    {
        if (directory is null)
        {
            // Discovery precedes page directories; the owned session records these read-only requests.
            platform.VerifyTarget(target!);
            var discovered = await app.UiAsync(arguments, token);
            platform.VerifyTarget(target!);
            return discovered;
        }
        var prefix = Path.Combine(directory, $"{++sequence:D4}-{arguments[0]}");
        AuditFiles.WriteNew(prefix + ".request.json", new { target, requestedAtUtc = DateTimeOffset.UtcNow, arguments });
        var response = await platform.UiAsync(target!, arguments, TimeSpan.FromSeconds(timeoutSeconds + 5), token);
        using (var writer = new StreamWriter(AuditFiles.OpenNew(prefix + ".json"), new UTF8Encoding(false)))
            writer.Write(response.Stdout);
        if (!string.IsNullOrEmpty(response.Stderr))
        {
            using var writer = new StreamWriter(AuditFiles.OpenNew(prefix + ".stderr.txt"), new UTF8Encoding(false));
            writer.Write(response.Stderr);
        }
        return WinappLauncher.ParseResult(response, arguments[0]);
    }

    private AuditOptions Options(int? processId = null, long? windowHandle = null) =>
        new(processId, windowHandle, "winapp", null, 30, 2000, checked(timeoutSeconds * 1000), 0, 0, false, screenshot);

    private string NewDirectory(string relativePath)
    {
        var parts = AuditFiles.NormalizeArtifactPath(relativePath).Split(Path.DirectorySeparatorChar);
        AuditFiles.EnsureDirectory(outputRoot);
        var parent = outputRoot;
        foreach (var part in parts[..^1])
        {
            parent = Path.Combine(parent, part);
            if (!Directory.Exists(parent))
                AuditFiles.CreateNewDirectory(parent);
            AuditFiles.EnsureDirectory(parent);
        }
        return AuditFiles.CreateNewDirectory(Path.Combine(parent, parts[^1]));
    }

    private static string? Text(JsonElement value, string name) => AuditJson.OptionalString(value, name);

    private static bool PropertyIs(JsonElement properties, string name, bool expected) =>
        bool.TryParse(Text(properties, name), out var actual) && actual == expected;

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await app.DisposeAsync();
    }

    private sealed record Control(string Selector, JsonElement Properties);
    private sealed record FocusState(string Selector, FocusObservation Observation);

    private sealed class NavigationEvent(JourneyRoute route, string sourcePageId)
    {
        public JourneyRoute Route { get; } = route;
        public string SourcePageId { get; } = sourcePageId;
        public DateTimeOffset? RequestedAtUtc { get; set; }
        public DateTimeOffset? InvokedAtUtc { get; set; }
        public DateTimeOffset? ReadyAtUtc { get; set; }
    }

    private sealed class ExerciseState(JourneyExercise guard)
    {
        public JourneyExercise Guard { get; } = guard;
        public List<CheckResult> Checks { get; } =
        [
            new("accessible-name", false, guard.ExpectedName, "Not evaluated."),
            new("target-enabled", false, "True", "Not evaluated."),
            new("target-visible", false, "False", "Not evaluated."),
            new("keyboard-reachable", false, guard.TargetId, "Not evaluated."),
            new("keyboard-activation", false, guard.ExpectedResult, "Not evaluated.")
        ];
        public List<FocusObservation> Trace { get; } = [];
        public IReadOnlyList<string> VerifiedTargets { get; set; } = [];
    }

    private sealed class Node(JsonElement value, Node? parent)
    {
        public Node? Parent { get; } = parent;
        public string? Id { get; } = Text(value, "automationId");
        public string? Name { get; } = Text(value, "name");
        public string Type { get; } = AuditJson.String(value, "type");
        public string Selector { get; } = AuditJson.String(value, "selector");
        public bool Offscreen { get; } = AuditJson.Boolean(value, "isOffscreen") != false;
        public bool Invokable { get; } = AuditJson.Boolean(value, "isInvokable") == true;
        public string? ToggleState { get; } = Text(value, "toggleState");
        public string? ExpandState { get; } = Text(value, "expandState");

        public bool IsDescendantOf(Node ancestor)
        {
            for (var current = Parent; current is not null; current = current.Parent)
                if (current == ancestor)
                    return true;
            return false;
        }
    }

    private sealed class Tree
    {
        public List<Node> Nodes { get; } = [];

        public Tree(JsonElement response, AuditTarget target)
        {
            var windows = AuditJson.Array(response, "windows");
            if (windows.GetArrayLength() != 1 || AuditJson.Long(windows[0], "hwnd") != target.WindowHandle)
                throw new InvalidDataException("Journey inspection requires exactly the owned top-level window; additional or foreign windows are refused.");
            if (AuditJson.Boolean(response, "interactive") == true || AuditJson.Boolean(response, "hideDisabled") == true
                || AuditJson.Boolean(response, "hideOffscreen") == true)
                throw new InvalidDataException("Filtered inspection cannot prove exact navigation scope or unique AutomationIds.");
            var roots = AuditJson.Array(windows[0], "elements");
            if (roots.GetArrayLength() != 1)
                throw new InvalidDataException("The owned window must expose exactly one UIA root.");
            Add(roots[0], null, 0);
            if (windows[0].TryGetProperty("elementCount", out var count) && count.GetInt32() != Nodes.Count)
                throw new InvalidDataException("The inspected tree's node count is inconsistent.");
        }

        private void Add(JsonElement value, Node? parent, int depth)
        {
            if (depth > 30 || Nodes.Count >= 2000 || AuditJson.Boolean(value, "hasMoreChildren") == true)
                throw new InvalidDataException("The navigation tree is truncated or exceeds its budget; uniqueness is not established.");
            var node = new Node(value, parent);
            if (parent is not null && node.Type == "Window")
                throw new InvalidDataException("An additional UIA window was found beneath the owned window.");
            Nodes.Add(node);
            if (value.TryGetProperty("children", out var children) && children.ValueKind != JsonValueKind.Null)
            {
                if (children.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The inspected UIA children must be an array.");
                foreach (var child in children.EnumerateArray())
                    Add(child, node, depth + 1);
            }
        }

        public Node[] Matches(string id) => Nodes.Where(node => node.Id == id).ToArray();

        public Node Exact(string id)
        {
            var matches = Matches(id);
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected exactly one AutomationId '{id}' in the owned window; found {matches.Length}.");
            if (Nodes.Count(node => node.Selector == matches[0].Selector) != 1)
                throw new InvalidOperationException($"The selector for AutomationId '{id}' is ambiguous.");
            return matches[0];
        }
    }

    private sealed class PinnedPlatform(
        IAuditPlatform inner, IAppSession app, Func<AuditTarget, IReadOnlyList<long>> visibleWindows) : IAuditPlatform
    {
        private AuditTarget? pinned;
        public Task<string> VersionAsync(CancellationToken token) => inner.VersionAsync(token);

        public async Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token)
        {
            if (pinned is not null || options.ProcessId != app.ProcessId || options.WindowHandle is not null)
                throw new InvalidOperationException("An owned journey resolves its PID to one HWND exactly once.");
            pinned = await inner.AttachAsync(options, token);
            VerifyTarget(pinned);
            return pinned;
        }

        public void VerifyTarget(AuditTarget target)
        {
            if (pinned is null || target.ProcessId != app.ProcessId || target.ProcessId != pinned.ProcessId
                || target.WindowHandle <= 0 || target.WindowHandle != pinned.WindowHandle
                || target.ProcessStartedAtUtc != pinned.ProcessStartedAtUtc
                || target.ProcessName != pinned.ProcessName)
                throw new InvalidOperationException("The journey process/window identity changed.");
            inner.VerifyTarget(target);
            var windows = visibleWindows(target);
            if (windows.Count != 1 || windows[0] != target.WindowHandle)
                throw new InvalidOperationException(
                    "The owned fixture must have exactly one visible top-level window. Additional windows are not approved.");
        }

        public async Task<CommandResult> UiAsync(
            AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            VerifyTarget(target);
            var result = await inner.UiAsync(target, arguments, timeout, token);
            VerifyTarget(target);
            return result;
        }

        public async Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
        {
            VerifyTarget(target);
            var result = await inner.AxeAsync(target, timeout, token);
            VerifyTarget(target);
            return result;
        }
    }

    private static IReadOnlyList<long> NativeVisibleWindows(AuditTarget target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Owned journey window verification requires Windows.");
        var windows = new List<long>();
        var visited = new HashSet<nint>();
        for (var window = GetTopWindow(0); window != 0; window = GetWindow(window, 2))
        {
            if (!visited.Add(window) || visited.Count > 10000)
                throw new InvalidOperationException("The native window list changed or exceeded the journey scope-check budget.");
            if (!IsWindowVisible(window))
                continue;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == target.ProcessId
                || AuditWindowIdentity.HasOwner(window, (nint)target.WindowHandle, handle => GetWindow(handle, 4)))
                windows.Add((long)window);
        }
        return windows;
    }

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
