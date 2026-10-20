using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessLoop.Tests;

internal sealed class AuditTestFixture : IDisposable
{
    public static readonly DateTimeOffset Time = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    public const string SnapshotPath = @"evidence\snapshot.json";
    public const string AxePath = @"evidence\axe.json";
    public const string ObservationPath = @"evidence\observation.json";
    public const string ScreenshotPath = @"evidence\window #&%.png";
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "audit-tests-data", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public string ScanPath => Path.Combine(Source, "scan.json");
    public string Output => Path.Combine(Root, "combined");
    public AuditReport Report { get; set; }

    public AuditTestFixture()
    {
        Directory.CreateDirectory(Path.Combine(Source, "evidence"));
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, capturedAtUtc = Time, processId = 42, windowHandle = 73,
            rootRuntimeId = "runtime-root", title = "Audit window", status = "captured",
            maxDepth = 4, maxElements = 20, depthLimitReached = false, elementLimitReached = false,
            nodes = new object[]
            {
                new
                {
                    id = "n0", parentId = (string?)null, depth = 0, runtimeId = "runtime-root",
                    automationId = "MainWindow", name = "Audit window", controlType = "Window",
                    frameworkId = "XAML", className = "WindowClass",
                    bounds = new { left = 100, top = 50, width = 600, height = 400 }, patterns = Array.Empty<string>(),
                    textRuns = Array.Empty<object>(), issues = Array.Empty<string>()
                },
                new
                {
                    id = "n1", parentId = "n0", depth = 1, runtimeId = "runtime-button",
                    automationId = "", name = "Save", controlType = "Button", frameworkId = "XAML", className = "ButtonClass",
                    isPassword = false, isEnabled = true, isKeyboardFocusable = true, hasKeyboardFocus = false, isOffscreen = false,
                    bounds = new { left = 125, top = 100, width = 90, height = 32 }, patterns = new[] { "Invoke" },
                    value = "DO-NOT-RENDER-INPUT-VALUE",
                    textRuns = new[]
                    {
                        new
                        {
                            id = "r0", bounds = new[] { new { left = 130, top = 105, width = 60, height = 18 } },
                            text = "DO-NOT-RENDER-TEXT-CONTENT",
                            foregroundColor = new { status = "value", number = 0 },
                            backgroundColor = new { status = "value", number = 16777215 },
                            fontSize = new { status = "unsupported" },
                            fontWeight = new { status = "mixed" },
                            isHidden = new { status = "withheld", number = 918273645 }
                        }
                    },
                    issues = new[] { "AutomationId missing" }
                }
            },
            issues = Array.Empty<string>()
        }, Json.Options);
        var observation = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, processId = 42, windowHandle = 73, startedAtUtc = Time, endedAtUtc = Time.AddSeconds(3),
            status = "completed", durationSec = 3, requestedTabSteps = 1, completedTabSteps = 1, maxEvents = 100,
            eventLimitReached = false, stopReason = "budget-complete",
            focusTrace = new[]
            {
                new { sequence = 0, direction = "initial", observedAtUtc = Time, runtimeId = "runtime-button",
                    automationId = "", name = "Save", controlType = "Button", isPassword = false, isInTarget = true, repeat = false },
                new { sequence = 1, direction = "forward", observedAtUtc = Time.AddSeconds(1), runtimeId = "runtime-button",
                    automationId = "", name = "Save", controlType = "Button", isPassword = false, isInTarget = true, repeat = true }
            },
            events = new[]
            {
                new { sequence = 0, receivedAtUtc = Time.AddSeconds(2), kind = "notification",
                    runtimeId = "runtime-button", automationId = "", name = "Save", controlType = "Button",
                    propertyId = 30005, notificationKind = 2, notificationProcessing = 0, displayString = "Saved", activityId = "save-1" }
            },
            issues = Array.Empty<string>()
        }, Json.Options);
        var axe = AccessibilityScan.Create(42, 73, "2.4.2",
        [
            new(1, [new("NameNotNull", "Accessible name is missing.", "Provide an accessible name.", "WCAG",
                new Dictionary<string, string> { ["RuntimeId"] = "runtime-button", ["AutomationId"] = "", ["ControlType"] = "Button" },
                ["Invoke"])])
        ]);
        AuditFiles.WriteNew(Path.Combine(Source, SnapshotPath), snapshot);
        AuditFiles.WriteNew(Path.Combine(Source, AxePath), axe);
        AuditFiles.WriteNew(Path.Combine(Source, ObservationPath), observation);
        File.WriteAllBytes(Path.Combine(Source, ScreenshotPath),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0f0AAAAASUVORK5CYII="));
        Report = new()
        {
            RunId = "audit-fixture",
            StartedAtUtc = Time,
            FinishedAtUtc = Time.AddSeconds(3),
            Status = "partial",
            Mode = "snapshot+keyboard+events",
            Target = new(42, 73, "Demo", Time.AddMinutes(-1), "Audit window", "runtime-root"),
            WinappVersion = "0.6.0",
            Snapshot = snapshot,
            Observation = observation,
            Axe = axe,
            Results =
            [
                new()
                {
                    CheckId = "axe.scan", Status = "rule-failure", Summary = "Accessible name is missing.",
                    SourceRuleId = "NameNotNull", ElementId = "n1", ElementName = "Save",
                    Evidence = [AxePath, SnapshotPath]
                },
                new() { CheckId = "capture.tree", Status = "observed", Summary = "Two nodes captured.", Evidence = [SnapshotPath] },
                new()
                {
                    CheckId = "uia.identifiers", Status = "observed", Summary = "One node has no AutomationId.",
                    ElementId = "n1", Details = new Dictionary<string, string> { ["Missing identifiers"] = "1" },
                    Evidence = [SnapshotPath]
                },
                new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Provider text attributes are incomplete.", Evidence = [SnapshotPath] },
                new() { CheckId = "keyboard.path", Status = "observed", Summary = "One bounded focus step recorded.", Evidence = [ObservationPath] },
                new() { CheckId = "visual.estimates", Status = "unsupported", Summary = "No pixel rule is implemented." },
                new() { CheckId = "content.meaning", Status = "not-tested", Summary = "Meaning requires independent review." }
            ],
            Artifacts =
            [
                AuditFiles.Describe(Source, SnapshotPath, "application/json", "WinAppCLI"),
                AuditFiles.Describe(Source, AxePath, "application/json", "Axe.Windows"),
                AuditFiles.Describe(Source, ObservationPath, "application/json", "WinAppCLI"),
                AuditFiles.Describe(Source, ScreenshotPath, "image/png", "WinAppCLI")
            ]
        };
        SaveScan();
    }

    public void SaveScan() => File.WriteAllBytes(ScanPath, JsonSerializer.SerializeToUtf8Bytes(Report, Json.Options));

    public void ChangeScan(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllBytes(ScanPath))!.AsObject();
        edit(json);
        File.WriteAllText(ScanPath, json.ToJsonString(Json.Options));
    }

    public AuditAssessmentDocument Assessment(string kind = "human", string status = "confirmed") => new()
    {
        RunId = Report.RunId,
        ScanSha256 = AuditFiles.Hash(ScanPath),
        CreatedAtUtc = Time.AddMinutes(1),
        Author = new(kind, "Local reviewer", kind == "ai" ? "declared-local-model" : null),
        Entries =
        [
            new()
            {
                CheckId = "axe.scan", ElementId = "n1", Status = status,
                Explanation = "The recorded node and native rule evidence need a name.",
                Evidence = [SnapshotPath, AxePath]
            }
        ]
    };

    public string SaveAssessment(AuditAssessmentDocument document, string name = "input-assessment.json")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document, Json.Options));
        return path;
    }

    public string Render(AuditReport? report = null, AuditAssessmentDocument? assessment = null, string name = "report.html")
    {
        var path = Path.Combine(Source, name);
        AuditReportRenderer.Write(path, report ?? Report, assessment);
        return File.ReadAllText(path);
    }

    public static JsonElement Edit(JsonElement element, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(element.GetRawText())!.AsObject();
        edit(json);
        return JsonSerializer.SerializeToElement(json, Json.Options);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
