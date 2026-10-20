using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ScenarioRunnerTests
{
    private string directory = null!;
    private static Scenario Example => new()
    {
        Name = "save", Project = "Demo.csproj", TargetId = "SaveButton", ExpectedName = "Save document",
        AnchorId = "TitleInput", ResultId = "SaveStatus", InputText = "AccessLoop",
        ExpectedResult = "Saved: AccessLoop", MaxTabs = 1, TimeoutSeconds = 1
    };

    [TestInitialize]
    public void Initialize() => directory = Directory.CreateTempSubdirectory("accessloop-tests-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public async Task MissingNameFailsButDoesNotBypassKeyboardTraversal()
    {
        var app = new FakeApp { Name = "" };
        var result = await Run(app);
        Assert.AreEqual("failed", result.Status);
        Assert.IsTrue(ScenarioRunner.CanRepair(result, Example.TargetId));
        Assert.AreEqual("NameNotNull", result.Scan!.Findings.Single().RuleId);
        Assert.IsFalse(result.Checks.Single(c => c.RuleId == "accessible-name").Passed);
        Assert.IsTrue(result.Checks.Single(c => c.RuleId == "keyboard-activation").Passed);
        CollectionAssert.AreEqual(new[] { "home", "tab", "enter" }, app.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "TitleInput" }, app.DirectFocus.ToArray());
        Assert.IsTrue(app.Disposed);
    }

    [TestMethod]
    public async Task OriginalChecksPassAfterCorrectNameIsPresent()
    {
        var result = await Run(new FakeApp { Name = "Save document" });
        Assert.AreEqual("passed", result.Status);
        Assert.AreEqual(5, result.Checks.Count);
        Assert.IsFalse(ScenarioRunner.CanRepair(result, Example.TargetId));
    }

    [TestMethod]
    public async Task MissingTargetIsInconclusiveNotPassing()
    {
        var app = new FakeApp { MissingTarget = true };
        var result = await Run(app);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "found 0");
        Assert.IsTrue(app.Disposed);
    }

    [TestMethod]
    public async Task DuplicateRuntimeIdentityCannotBeAccepted()
    {
        var result = await Run(new FakeApp { DuplicateTarget = true });
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "found 2");
    }

    [TestMethod]
    public async Task TruncatedSearchIsNotProofOfUniqueness()
    {
        var result = await Run(new FakeApp { TruncatedSearch = true });
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "truncated");
    }

    [TestMethod]
    public async Task UnreachableButtonCannotBeRepairedByChangingName()
    {
        var app = new FakeApp { Name = "", Reachable = false };
        var result = await Run(app);
        Assert.AreEqual("failed", result.Status);
        Assert.IsFalse(ScenarioRunner.CanRepair(result, Example.TargetId));
        Assert.IsFalse(result.Checks.Single(c => c.RuleId == "keyboard-reachable").Passed);
        CollectionAssert.AreEqual(new[] { "home", "tab" }, app.Keys.ToArray());
    }

    [TestMethod]
    public async Task SuccessfulEnterSubmissionDoesNotProveActivation()
    {
        var result = await Run(new FakeApp { Name = "Save document", Activates = false });
        Assert.AreEqual("failed", result.Status);
        Assert.IsFalse(result.Checks.Single(c => c.RuleId == "keyboard-activation").Passed);
    }

    [TestMethod]
    public async Task PreexistingSuccessResultRejectsDirtyStartingState()
    {
        var result = await Run(new FakeApp { AlreadySaved = true });
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "already present");
    }

    [TestMethod]
    public async Task SuccessCausedByEditingInputIsNotKeyboardActivation()
    {
        var app = new FakeApp { SaveOnValueChange = true };
        var result = await Run(app);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "before Enter");
        CollectionAssert.AreEqual(new[] { "home", "tab" }, app.Keys.ToArray());
    }

    [TestMethod]
    public void InvalidDataIsAnExecutionFailure()
    {
        Assert.IsTrue(ScenarioRunner.IsExecutionFailure(new InvalidDataException("bad proposal")));
    }

    [TestMethod]
    public async Task InputFailureIsInconclusiveAndClosesOwnedApp()
    {
        var app = new FakeApp { FailInput = true };
        var result = await Run(app);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsTrue(app.Disposed);
    }

    [TestMethod]
    public async Task CleanupFailureInvalidatesOtherwisePassingObservation()
    {
        var result = await Run(new FakeApp { Name = "Save document", FailCleanup = true });
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Cleanup failed");
    }

    [TestMethod]
    public async Task CancellationIsRecordedAndAppIsClosed()
    {
        var app = new FakeApp { CancelInput = true };
        var result = await Run(app);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsTrue(app.Disposed);
    }

    [TestMethod]
    public async Task CorrectScenarioNameCannotMaskAnIndependentAxeFailure()
    {
        var finding = ScanFixture.MissingName("OtherControl");
        var result = await Run(new FakeApp { Name = Example.ExpectedName }, ScanFixture.Create(finding));
        Assert.IsTrue(result.Checks.All(c => c.Passed));
        Assert.AreEqual("failed", result.Status);
        Assert.IsFalse(ScenarioRunner.CanRepair(result, Example.TargetId));
    }

    [TestMethod]
    public async Task NameExpectationMismatchAloneDoesNotTriggerAnAccessibilityRepair()
    {
        var result = await Run(new FakeApp { Name = "Save this document" });
        Assert.AreEqual("failed", result.Status);
        Assert.HasCount(0, result.Scan!.Findings);
        Assert.IsFalse(ScenarioRunner.CanRepair(result, Example.TargetId));
    }

    [TestMethod]
    public async Task OtherFindingsDoNotInvalidateTheSelectedTargetsBehaviorGuards()
    {
        var result = await Run(new FakeApp(),
            ScanFixture.Create(ScanFixture.MissingName(), ScanFixture.MissingName("OtherButton", "[4,5,6]")));
        Assert.AreEqual("failed", result.Status);
        Assert.IsTrue(ScenarioRunner.CanRepair(result, Example.TargetId));
    }

    [TestMethod]
    public async Task UnsupportedRuleOnTheSameTargetDoesNotAuthorizeANameEdit()
    {
        var result = await Run(new FakeApp(), ScanFixture.Create(ScanFixture.MissingName() with { RuleId = "IsKeyboardFocusableShouldBeTrue" }));
        Assert.IsFalse(ScenarioRunner.CanRepair(result, Example.TargetId));
    }

    [TestMethod]
    public async Task ScannerFailureIsInconclusiveAndClosesTheApp()
    {
        var app = new FakeApp();
        var result = await new ScenarioRunner(new FakeLauncher(app), new FakeScanner(app, error: new InvalidOperationException("Scan failed.")))
            .RunAsync(Example, "before", directory, CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsNull(result.Scan);
        Assert.IsTrue(app.Disposed);
    }

    [TestMethod]
    public async Task ScanFromAnotherProcessCannotPass()
    {
        var result = await Run(new FakeApp { Name = Example.ExpectedName }, ScanFixture.Create() with { ProcessId = 999 });
        Assert.AreEqual("inconclusive", result.Status);
    }

    [TestMethod]
    public async Task ScannerTimeoutIsNotAnAccessibilityFailure()
    {
        var app = new FakeApp();
        var result = await new ScenarioRunner(new FakeLauncher(app), new FakeScanner(app, error: new OperationCanceledException()))
            .RunAsync(Example, "before", directory, CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "scan deadline");
    }

    [TestMethod]
    public void ScenarioRejectsUnknownFieldsAndUnboundedReplay()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => (Example with { MaxTabs = 999 }).Validate());
        Assert.ThrowsExactly<InvalidDataException>(() => (Example with { TargetId = Example.AnchorId }).Validate());
        var path = Path.Combine(directory, "scenario.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Example, Json.Options).Replace("\"schemaVersion\": 1", "\"unknown\": 1"));
        Assert.ThrowsExactly<JsonException>(() => Scenario.Load(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScenarioUsesTheSameSnapshotThatIsRecordedAsEvidence(bool bom)
    {
        var path = Path.Combine(directory, "scenario.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(Example, Json.Options);
        if (bom)
            original = [0xEF, 0xBB, 0xBF, .. original];
        File.WriteAllText(path, JsonSerializer.Serialize(Example with { ExpectedName = "Changed later" }, Json.Options));
        var scenario = Scenario.Load(path, original);
        Assert.AreEqual("Save document", scenario.ExpectedName);
        Assert.AreEqual(Path.Combine(directory, "Demo.csproj"), scenario.Project);
    }

    [TestMethod]
    public void ReportEncodesUntrustedUiContent()
    {
        var report = new RunReport
        {
            SuiteName = "<script>alert(1)</script>", InputPath = "scenario.json",
            Inputs = [new("scenario.json", "123", "inputs\\01.json")],
            WinappVersion = "0.6.0", Status = "failed",
            Before = [new(Example, "before\\01", new Observation { Phase = "before", Status = "failed",
                Checks = [new("accessible-name", false, "Save", "<img src=x onerror=alert(1)>")],
                Scan = ScanFixture.Create(ScanFixture.MissingName() with { Description = "<script>from scanner</script>" }) })]
        };
        var path = Path.Combine(directory, "report.html");
        ReportRenderer.Write(path, report);
        var html = File.ReadAllText(path);
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SelectedFindingTargetMustRemainPresentInEveryAffectedCase()
    {
        var app = new FakeApp { Name = Example.ExpectedName, MissingAdditionalAfterActivation = true };
        var result = await new ScenarioRunner(new FakeLauncher(app), new FakeScanner(app))
            .RunAsync(Example, "after", directory, CancellationToken.None, ["OtherButton"]);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.HasCount(0, result.VerifiedTargets);
        Assert.IsTrue(app.Disposed);
    }

    private Task<Observation> Run(FakeApp app, AccessibilityScan? scan = null) =>
        new ScenarioRunner(new FakeLauncher(app), new FakeScanner(app, scan))
            .RunAsync(Example, "before", directory, CancellationToken.None);

    private sealed class FakeScanner(FakeApp app, AccessibilityScan? scan = null, Exception? error = null) : IAccessibilityScanner
    {
        public Task<AccessibilityScan> ScanAsync(int processId, long windowHandle, CancellationToken cancellationToken)
        {
            Assert.AreEqual(app.ProcessId, processId);
            Assert.AreEqual(42, windowHandle);
            if (error is not null)
                throw error;
            return Task.FromResult(scan ?? ScanFixture.Create(string.IsNullOrEmpty(app.Name) ? [ScanFixture.MissingName()] : []));
        }
    }

    private sealed class FakeLauncher(FakeApp app) : IAppLauncher
    {
        public Task<IAppSession> LaunchAsync(Scenario scenario, string evidenceDirectory, CancellationToken cancellationToken)
            => Task.FromResult<IAppSession>(app);
    }

    private sealed class FakeApp : IAppSession
    {
        public int ProcessId => 1234;
        public string Name { get; init; } = "";
        public bool Reachable { get; init; } = true;
        public bool Activates { get; init; } = true;
        public bool MissingTarget { get; init; }
        public bool DuplicateTarget { get; init; }
        public bool TruncatedSearch { get; init; }
        public bool AlreadySaved { get; init; }
        public bool SaveOnValueChange { get; init; }
        public bool FailInput { get; init; }
        public bool CancelInput { get; init; }
        public bool FailCleanup { get; init; }
        public bool MissingAdditionalAfterActivation { get; init; }
        public bool Disposed { get; private set; }
        public List<string> Keys { get; } = [];
        public List<string> DirectFocus { get; } = [];
        private string focused = "TitleInput";
        private bool activated;

        public Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            object output;
            switch (arguments[0])
            {
                case "wait-for": output = new { found = true }; break;
                case "inspect": output = new { windows = new[] { new { hwnd = 42 } } }; break;
                case "screenshot": output = new { }; break;
                case "set-value":
                    if (SaveOnValueChange) activated = true;
                    output = new { }; break;
                case "search":
                    var id = arguments[1];
                    var element = new
                    {
                        automationId = id, selector = id, type = id == "SaveButton" ? "Button" : "Edit",
                        name = id == "SaveButton" ? Name : id == "SaveStatus" && (AlreadySaved || activated)
                            ? "Saved: AccessLoop" : ""
                    };
                    var matches = id == "SaveButton" && MissingTarget
                        || id == "OtherButton" && activated && MissingAdditionalAfterActivation ? [] :
                        id == "SaveButton" && DuplicateTarget ? new[] { element, element } : [element];
                    output = new { hasMore = TruncatedSearch, matches };
                    break;
                case "get-property":
                    output = new { properties = new Dictionary<string, string>
                    {
                        ["Name"] = arguments[1] == "SaveButton" ? Name : activated ? "Saved: AccessLoop" : "",
                        ["IsEnabled"] = "True", ["IsOffscreen"] = "False"
                    } };
                    break;
                case "focus":
                    DirectFocus.Add(arguments[1]);
                    focused = arguments[1];
                    output = new { };
                    break;
                case "get-focused":
                    output = new { hasFocus = true, element = new { automationId = focused, name = Name } };
                    break;
                case "send-keys":
                    if (FailInput) throw new InvalidOperationException("Foreground lost.");
                    if (CancelInput) throw new OperationCanceledException();
                    Assert.AreEqual("--via", arguments[2]);
                    Assert.AreEqual("send-input", arguments[3]);
                    if (arguments.Count > 4)
                    {
                        Assert.AreEqual("--target", arguments[4]);
                        Assert.AreEqual("TitleInput", arguments[5]);
                        DirectFocus.Add(arguments[5]);
                        focused = arguments[5];
                    }
                    Keys.Add(arguments[1]);
                    if (arguments[1] == "tab") focused = Reachable ? "SaveButton" : "TitleInput";
                    if (arguments[1] == "enter" && Activates) activated = true;
                    output = new { };
                    break;
                default: throw new InvalidOperationException($"Unexpected command {arguments[0]}");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(output));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (FailCleanup) throw new InvalidOperationException("Application did not exit.");
            return ValueTask.CompletedTask;
        }
    }
}
