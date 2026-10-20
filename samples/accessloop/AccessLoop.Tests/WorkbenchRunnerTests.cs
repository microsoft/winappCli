using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class WorkbenchRunnerTests
{
    private string directory = null!;
    private string source = null!;
    private FakeEnvironment environment = null!;
    private int run;

    [TestInitialize]
    public void Initialize()
    {
        directory = Directory.CreateTempSubdirectory("accessloop-workbench-").FullName;
        File.WriteAllText(Path.Combine(directory, "Demo.csproj"), "<Project />");
        source = Path.Combine(directory, "MainWindow.xaml");
        File.WriteAllText(source, """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <Button AutomationProperties.AutomationId="SaveButton" />
                <Button AutomationProperties.AutomationId="ExportButton" />
              </StackPanel>
            </Window>
            """);
        foreach (var action in new[] { "Save", "Export" })
            Json.Write(Path.Combine(directory, action + ".json"), new Scenario
            {
                Name = action, Project = "Demo.csproj", TargetId = action + "Button",
                ExpectedName = action + " document", AnchorId = "Input", ResultId = "Status",
                InputText = "AccessLoop", ExpectedResult = action == "Save" ? "Saved: AccessLoop" : "Exported: AccessLoop",
                MaxTabs = 5, TimeoutSeconds = 1
            });
        Json.Write(Path.Combine(directory, "suite.json"),
            new { schemaVersion = 1, name = "documents", scenarios = new[] { "Save.json", "Export.json" } });
        environment = new FakeEnvironment(source);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public async Task MultipleFindingsRequireSelectionWithoutInvokingAnAdvisor()
    {
        var original = File.ReadAllBytes(source);
        var report = await Run("repair");
        Assert.AreEqual("awaiting-selection", report.Status);
        Assert.HasCount(2, report.Findings);
        Assert.IsTrue(report.Findings.All(f => f.Occurrences.Count == 2));
        Assert.IsNull(report.RepairSource);
        Assert.IsFalse(report.PatchApplied);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.AreEqual(2, environment.DisposedCount);
    }

    [TestMethod]
    public async Task RepairsOneIssueAtATimeWhileRerunningEveryScenario()
    {
        var audit = await Run("check");
        var save = audit.Findings.Single(f => f.AutomationId == "SaveButton");
        var export = audit.Findings.Single(f => f.AutomationId == "ExportButton");
        var first = await Run("repair", save.Id, true, WriteProposal("Save"));
        Assert.AreEqual("fixed", first.Status);
        Assert.AreEqual("failed", first.SuiteStatus);
        Assert.HasCount(2, first.Before);
        Assert.HasCount(2, first.After);
        Assert.AreEqual(export.Id, first.RemainingFindings.Single().Id);
        Assert.IsTrue(first.PatchApplied);
        Assert.IsFalse(first.PatchRolledBack);
        Assert.IsTrue(first.After.All(c => c.Observation.VerifiedTargets.Contains("SaveButton")));

        var second = await Run("repair", export.Id, true, WriteProposal("Export"));
        Assert.AreEqual("fixed", second.Status);
        Assert.AreEqual("passed", second.SuiteStatus);
        Assert.HasCount(0, second.RemainingFindings);
        Assert.IsTrue(second.After.All(c => c.Observation.Checks.All(check => check.Passed)));
        var final = await Run("check");
        Assert.AreEqual("passed", final.Status);
        Assert.HasCount(0, final.Findings);
        Assert.AreEqual(environment.LaunchCount, environment.DisposedCount);
    }

    [TestMethod]
    public async Task NewRegressionRollsBackOnlyTheAttemptedPatch()
    {
        var audit = await Run("check");
        var original = File.ReadAllBytes(source);
        var selected = audit.Findings.Single(f => f.AutomationId == "SaveButton");
        environment.InjectRegression = true;
        var result = await Run("repair", selected.Id, true, WriteProposal("Save"));
        Assert.AreEqual("regression", result.Status);
        Assert.IsTrue(result.PatchRolledBack);
        Assert.IsFalse(result.PatchApplied);
        Assert.IsTrue(result.VerificationErrors.Count > 0);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.AreEqual(environment.LaunchCount, environment.DisposedCount);
    }

    [TestMethod]
    public async Task ProposalAndStaleSelectionNeverEditSource()
    {
        var audit = await Run("check");
        var original = File.ReadAllBytes(source);
        var selected = audit.Findings.Single(f => f.AutomationId == "SaveButton");
        var proposed = await Run("repair", selected.Id, false, WriteProposal("Save"));
        Assert.AreEqual("awaiting-approval", proposed.Status);
        Assert.HasCount(0, proposed.After);
        Assert.IsFalse(proposed.PatchApplied);
        var stale = await Run("repair", "AL-NOT-PRESENT");
        Assert.AreEqual("needs-review", stale.Status);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task CancellationDuringLastVerificationCleanupRollsBack()
    {
        var audit = await Run("check");
        var original = File.ReadAllBytes(source);
        var selected = audit.Findings.Single(f => f.AutomationId == "SaveButton");
        using var cancellation = new CancellationTokenSource();
        environment.CancelOnDispose = (6, cancellation);
        var result = await Run("repair", selected.Id, true, WriteProposal("Save"), cancellation.Token);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.HasCount(2, result.After);
        Assert.IsTrue(result.PatchRolledBack);
        Assert.IsFalse(result.PatchApplied);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.AreEqual(6, environment.DisposedCount);
    }

    private Task<RunReport> Run(string command, string? finding = null, bool apply = false,
        string? proposal = null, CancellationToken cancellationToken = default)
    {
        var input = SuiteInput.Load(Path.Combine(directory, "suite.json"));
        var output = Path.Combine(directory, "run-" + ++run);
        Directory.CreateDirectory(output);
        var options = new WorkbenchOptions(command, input.InputPath, "not-called", "must-not-call-ai",
            output, proposal, finding, apply);
        return new WorkbenchRunner(new ScenarioRunner(environment, environment))
            .RunAsync(input, options, "0.6.3", output, cancellationToken);
    }

    private string WriteProposal(string action)
    {
        var candidate = SourceRepair.Locate(Path.Combine(directory, "Demo.csproj"), action + "Button").Single();
        var path = Path.Combine(directory, $"proposal-{run}.json");
        Json.Write(path, new RepairProposal(candidate.RelativePath, candidate.Sha256,
            candidate.AutomationId, action + " document", "Provide the action's accessible name."));
        return path;
    }

    private sealed class FakeEnvironment(string source) : IAppLauncher, IAccessibilityScanner
    {
        public int LaunchCount { get; private set; }
        public int DisposedCount { get; private set; }
        public bool InjectRegression { get; set; }
        public (int Number, CancellationTokenSource Source)? CancelOnDispose { get; set; }

        public string? Name(string id) => XDocument.Load(source).Descendants()
            .Where(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id)
            .Select(e => (string?)e.Attribute("AutomationProperties.Name")).Single();

        public Task<IAppSession> LaunchAsync(Scenario scenario, string evidenceDirectory, CancellationToken cancellationToken)
            => Task.FromResult<IAppSession>(new FakeApp(this, 1000 + ++LaunchCount));

        public Task<AccessibilityScan> ScanAsync(int processId, long windowHandle, CancellationToken cancellationToken)
        {
            var findings = new List<AccessibilityFinding>();
            foreach (var id in new[] { "SaveButton", "ExportButton" })
            {
                if (Name(id) is null)
                    findings.Add(ScanFixture.MissingName(id, $"{processId}-{id}"));
            }
            if (InjectRegression && Name("SaveButton") is not null)
                findings.Add(ScanFixture.MissingName("UnexpectedControl", $"{processId}-new") with { RuleId = "NameNotEmpty" });
            return Task.FromResult(AccessibilityScan.Create(processId, windowHandle, "2.4.2",
                [new(findings.Count, findings)]));
        }

        private sealed class FakeApp(FakeEnvironment environment, int processId) : IAppSession
        {
            public int ProcessId => processId;
            private string focused = "Input";
            private string result = "Not saved";

            public Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            {
                object output;
                string? Name(string id) => id is "SaveButton" or "ExportButton"
                    ? environment.Name(id) : id == "Status" ? result : "Document title";
                switch (arguments[0])
                {
                    case "wait-for": output = new { found = true }; break;
                    case "inspect": output = new { windows = new[] { new { hwnd = 42 } } }; break;
                    case "screenshot":
                    case "set-value": output = new { }; break;
                    case "search":
                        var id = arguments[1];
                        output = new
                        {
                            hasMore = false, matches = new[]
                            {
                                new { automationId = id, selector = id, name = Name(id),
                                    type = id.EndsWith("Button", StringComparison.Ordinal) ? "Button" : "Edit" }
                            }
                        };
                        break;
                    case "get-property":
                        output = new
                        {
                            properties = new Dictionary<string, string?>
                            {
                                ["Name"] = Name(arguments[1]), ["IsEnabled"] = "True", ["IsOffscreen"] = "False"
                            }
                        };
                        break;
                    case "send-keys":
                        if (arguments[1] == "home") focused = "Input";
                        if (arguments[1] == "tab") focused = focused == "Input" ? "SaveButton" : "ExportButton";
                        if (arguments[1] == "enter") result = focused == "SaveButton" ? "Saved: AccessLoop" : "Exported: AccessLoop";
                        output = new { }; break;
                    case "get-focused":
                        output = new { hasFocus = true, element = new { automationId = focused, name = Name(focused) } }; break;
                    default: throw new InvalidOperationException(arguments[0]);
                }
                return Task.FromResult(JsonSerializer.SerializeToElement(output));
            }

            public ValueTask DisposeAsync()
            {
                environment.DisposedCount++;
                if (environment.CancelOnDispose is { } cancel && environment.DisposedCount == cancel.Number)
                    cancel.Source.Cancel();
                return ValueTask.CompletedTask;
            }
        }
    }
}
