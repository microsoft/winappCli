using System.Diagnostics;
using System.Text.Json;

namespace AccessLoop;

public sealed class ScenarioRunner(IAppLauncher launcher, IAccessibilityScanner scanner)
{
    public async Task<Observation> RunAsync(
        Scenario scenario, string phase, string evidenceDirectory, CancellationToken cancellationToken,
        IReadOnlyList<string>? requiredTargetIds = null)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var checks = new List<CheckResult>();
        var trace = new List<FocusObservation>();
        int? pid = null;
        string? error = null;
        AccessibilityScan? scan = null;
        var required = new[] { scenario.TargetId }.Concat(requiredTargetIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var verified = false;
        IAppSession? session = null;
        try
        {
            session = await launcher.LaunchAsync(scenario, evidenceDirectory, cancellationToken);
            pid = session.ProcessId;
            var wait = await session.UiAsync(
                ["wait-for", scenario.AnchorId, "--timeout", (scenario.TimeoutSeconds * 1000).ToString()],
                cancellationToken);
            if (!wait.GetProperty("found").GetBoolean())
                throw new InvalidOperationException("The scenario anchor did not become ready.");

            var tree = await session.UiAsync(["inspect", "--depth", "20"], cancellationToken);
            File.WriteAllText(Path.Combine(evidenceDirectory, "tree.json"), tree.GetRawText());
            await session.UiAsync(["screenshot", "-o", Path.Combine(evidenceDirectory, "window.png")], cancellationToken);
            var windows = tree.GetProperty("windows");
            if (windows.GetArrayLength() != 1)
                throw new InvalidDataException("This scenario requires exactly one application window to define the scan scope.");
            var windowHandle = windows[0].GetProperty("hwnd").GetInt64();

            var target = await FindExactAsync(session, scenario.TargetId, cancellationToken);
            foreach (var id in required.Where(id => id != scenario.TargetId))
                await FindExactAsync(session, id, cancellationToken);
            var anchor = await FindExactAsync(session, scenario.AnchorId, cancellationToken);
            var initialResult = await FindExactAsync(session, scenario.ResultId, cancellationToken);
            if (Text(initialResult, "name") == scenario.ExpectedResult)
                throw new InvalidOperationException("The success result is already present before activation. Reset application state before replay.");
            if (target.GetProperty("type").GetString() != "Button")
                throw new InvalidOperationException("The configured repair target must be a Button.");
            var targetSelector = RequiredText(target, "selector");
            var properties = await PropertiesAsync(session, targetSelector, cancellationToken);
            var actualName = Text(properties, "Name");
            checks.Add(new("accessible-name", actualName == scenario.ExpectedName, scenario.ExpectedName, actualName));
            checks.Add(new("target-enabled", IsTrue(properties, "IsEnabled"), "True", Text(properties, "IsEnabled")));
            checks.Add(new("target-visible", IsFalse(properties, "IsOffscreen"), "False", Text(properties, "IsOffscreen")));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(scenario.TimeoutSeconds));
                try
                {
                    scan = await scanner.ScanAsync(session.ProcessId, windowHandle, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Axe.Windows exceeded the scenario scan deadline.");
                }
            }
            if (scan.ProcessId != session.ProcessId || scan.WindowHandle != windowHandle
                || scan.ResultGroupErrorCounts.Count == 0 || string.IsNullOrWhiteSpace(scan.EngineVersion))
                throw new InvalidDataException("The scan did not cover the current scenario process and window.");
            Json.Write(Path.Combine(evidenceDirectory, "axe-scan.json"), scan);

            var anchorSelector = RequiredText(anchor, "selector");
            await session.UiAsync(["set-value", anchorSelector, scenario.InputText], cancellationToken);
            await session.UiAsync(
                ["send-keys", "home", "--via", "send-input", "--target", anchorSelector], cancellationToken);
            var focused = await ReadFocusAsync(session, 0, cancellationToken);
            trace.Add(focused);
            if (focused.AutomationId != scenario.AnchorId)
                throw new InvalidOperationException("Could not establish the scenario's starting focus.");

            // Only the starting anchor is focused directly. The target must be reached using Tab.
            for (var tab = 1; tab <= scenario.MaxTabs; tab++)
            {
                await session.UiAsync(["send-keys", "tab", "--via", "send-input"], cancellationToken);
                focused = await ObserveFocusChangeAsync(session, focused, tab, cancellationToken);
                trace.Add(focused);
                if (focused.AutomationId == scenario.TargetId)
                    break;
            }
            var reachable = focused.AutomationId == scenario.TargetId;
            checks.Add(new("keyboard-reachable", reachable, scenario.TargetId, focused.AutomationId));
            if (reachable)
            {
                var beforeEnter = await FindExactAsync(session, scenario.ResultId, cancellationToken);
                if (Text(beforeEnter, "name") == scenario.ExpectedResult)
                    throw new InvalidOperationException("The success result appeared before Enter. Keyboard activation cannot be established.");
                await session.UiAsync(["send-keys", "enter", "--via", "send-input"], cancellationToken);
                var result = await WaitForResultAsync(session, scenario, cancellationToken);
                checks.Add(new("keyboard-activation", result == scenario.ExpectedResult, scenario.ExpectedResult, result));
            }
            else
            {
                checks.Add(new("keyboard-activation", false, scenario.ExpectedResult, "Target not reached; activation not attempted."));
            }

            // Re-query identity after interaction; a disappearing target is never a repair.
            foreach (var id in required)
                await FindExactAsync(session, id, cancellationToken);
            verified = true;
            await session.UiAsync(["screenshot", "-o", Path.Combine(evidenceDirectory, "after-action.png")], cancellationToken);
        }
        catch (Exception ex) when (IsExecutionFailure(ex))
        {
            error = ex is OperationCanceledException ? "Execution cancelled." : ex.Message;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex) when (IsExecutionFailure(ex))
                {
                    error = $"{error} Cleanup failed: {ex.Message}".Trim();
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
            error ??= "Execution cancelled.";
        var observation = new Observation
        {
            Phase = phase,
            Status = error is not null ? "inconclusive"
                : scan is not null && scan.Findings.Count == 0 && checks.Count == 5 && checks.All(c => c.Passed) ? "passed" : "failed",
            ProcessId = pid,
            Error = error,
            Scan = scan,
            Checks = checks,
            FocusTrace = trace,
            VerifiedTargets = verified ? required : []
        };
        Json.Write(Path.Combine(evidenceDirectory, "observation.json"), observation);
        return observation;
    }

    public static bool IsExecutionFailure(Exception ex) =>
        ex is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException
            or ArgumentException or TimeoutException or OperationCanceledException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Xml.XmlException;

    public static bool CanRepair(Observation before, string targetId) =>
        before.Status == "failed"
        && before.Checks.Count == 5
        && before.Checks.Select(c => c.RuleId).Distinct(StringComparer.Ordinal).Count() == 5
        && before.Checks.Count(c => c.RuleId == "accessible-name" && !c.Passed) == 1
        && before.Checks.Where(c => c.RuleId != "accessible-name").All(c => c.Passed)
        && before.Scan is { } scan
        && scan.Findings.Count(f => f.AutomationId == targetId) == 1
        && scan.Findings.Any(f => f.AutomationId == targetId
            && f.RuleId is "NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace");

    private static async Task<JsonElement> FindExactAsync(IAppSession session, string id, CancellationToken token)
    {
        var found = await session.UiAsync(["search", id], token);
        if (found.GetProperty("hasMore").GetBoolean())
            throw new InvalidOperationException($"Search for {id} was truncated; use a unique AutomationId.");
        var matches = found.GetProperty("matches").EnumerateArray()
            .Where(e => Text(e, "automationId") == id).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected exactly one visible-scenario element with AutomationId {id}; found {matches.Length}.");
        return matches[0].Clone();
    }

    private static async Task<JsonElement> PropertiesAsync(IAppSession session, string selector, CancellationToken token)
    {
        var result = await session.UiAsync(["get-property", selector], token);
        return result.GetProperty("properties").Clone();
    }

    private static async Task<FocusObservation> ReadFocusAsync(IAppSession session, int tab, CancellationToken token)
    {
        var result = await session.UiAsync(["get-focused"], token);
        if (!result.GetProperty("hasFocus").GetBoolean())
            return new(tab, null, null);
        var element = result.GetProperty("element");
        return new(tab, Text(element, "automationId"), Text(element, "name"));
    }

    private static async Task<FocusObservation> ObserveFocusChangeAsync(
        IAppSession session, FocusObservation previous, int tab, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        FocusObservation current;
        do
        {
            await Task.Delay(100, token);
            current = await ReadFocusAsync(session, tab, token);
        } while (current.AutomationId == previous.AutomationId && clock.Elapsed < TimeSpan.FromMilliseconds(700));
        if (current.AutomationId is null)
            throw new InvalidOperationException("The application lost observable keyboard focus; replay is inconclusive.");
        return current;
    }

    private static async Task<string?> WaitForResultAsync(IAppSession session, Scenario scenario, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        string? result;
        do
        {
            var element = await FindExactAsync(session, scenario.ResultId, token);
            var properties = await PropertiesAsync(session, RequiredText(element, "selector"), token);
            result = Text(properties, "Name");
            if (result == scenario.ExpectedResult)
                return result;
            await Task.Delay(100, token);
        } while (clock.Elapsed < TimeSpan.FromSeconds(scenario.TimeoutSeconds));
        return result;
    }

    private static bool IsTrue(JsonElement element, string key) =>
        bool.TryParse(Text(element, key), out var value) && value;

    private static bool IsFalse(JsonElement element, string key) =>
        bool.TryParse(Text(element, key), out var value) && !value;

    private static string RequiredText(JsonElement element, string key) =>
        Text(element, key) ?? throw new InvalidDataException($"Missing {key} in winapp output.");

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
