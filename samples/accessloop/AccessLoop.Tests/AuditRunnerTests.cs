using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class AuditOptionsTests
{
    [TestMethod]
    public void DefaultsAreReadOnly()
    {
        var options = AuditOptions.Parse(["scan", "--window", "42"]);
        Assert.AreEqual(42L, options.WindowHandle);
        Assert.AreEqual(0, options.TabSteps);
        Assert.AreEqual(0, options.ObserveSeconds);
        Assert.IsFalse(options.AcceptInput);
        Assert.IsTrue(options.Screenshot);
    }

    [TestMethod]
    public void AppTargetsAcceptNamesTitlesAndPidsWithoutConflictingTargets()
    {
        foreach (var target in new[] { "Demo", "Demo window title", "1234" })
        {
            var options = AuditOptions.Parse(["scan", "--app", target]);
            Assert.AreEqual(target, options.App);
            Assert.IsNull(options.ProcessId);
            Assert.IsNull(options.WindowHandle);
        }
        Assert.ThrowsExactly<ArgumentException>(() => AuditOptions.Parse(["scan", "--app", "Demo", "--window", "42"]));
        Assert.ThrowsExactly<ArgumentException>(() => AuditOptions.Parse(["scan", "--app", "Demo", "--pid", "1"]));
        Assert.ThrowsExactly<ArgumentException>(() => AuditOptions.Parse(["scan", "--app", " "]));
    }

    [TestMethod]
    public void InputRequiresExplicitAcceptanceBeforeExecution()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AuditFixture.Options("--tab-steps", "4"));
        Assert.ThrowsExactly<ArgumentException>(() => AuditFixture.Options("--accept-input"));
        var options = AuditFixture.Options("--tab-steps", "4", "--accept-input");
        Assert.AreEqual(10, options.ObserveSeconds);
    }

    [TestMethod]
    public void InvalidAndAmbiguousInputsAreRejected()
    {
        foreach (string[] arguments in new string[][]
        {
            ["scan"], ["scan", "--pid", "0"], ["scan", "--window", "-1"],
            ["scan", "--pid", "1", "--window", "42"], ["scan", "--pid", "1", "--pid", "2"],
            ["scan", "--pid", "1", "--max-elements", "10001"], ["scan", "--pid", "1", "--unknown"],
            ["scan", "--pid", "1", "--tab-steps", "1", "--accept-input", "--observe-seconds", "0"]
        })
            Assert.ThrowsExactly<ArgumentException>(() => AuditOptions.Parse(arguments));
    }
}

[TestClass]
public sealed class AuditRunnerTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "accessloop-audit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public async Task ReadOnlyScanWithoutIdsDoesNotRunInputOrCloseTheApp()
    {
        var platform = new PlatformFake();
        var report = await Run(platform, AuditFixture.Options());
        Assert.AreEqual("completed", report.Status);
        CollectionAssert.AreEqual(new[] { "audit-snapshot" }, platform.Commands.ToArray());
        Assert.AreEqual(1, platform.AxeCalls);
        Assert.AreEqual(0, AuditRunner.ExitCode(report));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "snapshot.json")));
        Assert.IsTrue(report.Results.Any(result => result.Status == "unsupported"));
        Assert.IsFalse(report.Results.Any(result => result.Status == "passed"));
    }

    [TestMethod]
    public async Task ActualAxeFailuresArePreservedWithoutAutomationIds()
    {
        var platform = new PlatformFake { Scan = AuditFixture.Axe(true) };
        var report = await Run(platform, AuditFixture.Options());
        Assert.AreEqual("completed", report.Status);
        Assert.AreEqual(1, report.RuleFailureCount);
        Assert.AreEqual(1, AuditRunner.ExitCode(report));
        Assert.AreEqual("n2", report.Results.Single(result => result.Status == "rule-failure").ElementId);
    }

    [TestMethod]
    public async Task EmptySnapshotCannotBeHiddenByZeroAxeFailures()
    {
        var report = await Run(new PlatformFake { Snapshot = AuditFixture.Snapshot(empty: true) }, AuditFixture.Options());
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(0, report.RuleFailureCount);
        Assert.AreEqual(2, AuditRunner.ExitCode(report));
        Assert.IsTrue(report.Results.Any(result => result.CheckId == "capture.tree" && result.Status == "blocked"));
    }

    [TestMethod]
    public async Task PartialNativeCaptureIsRetainedWithItsCoverageState()
    {
        var report = await Run(new PlatformFake { Snapshot = AuditFixture.Snapshot(partial: true), SnapshotExit = 1 }, AuditFixture.Options());
        Assert.AreEqual("partial", report.Status);
        Assert.IsNotNull(report.Snapshot);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "snapshot.json")));
    }

    [TestMethod]
    public async Task MismatchedNativeTargetIsNotPersistedAsScopedEvidence()
    {
        var report = await Run(new PlatformFake { Snapshot = AuditFixture.Snapshot(pid: 5678) }, AuditFixture.Options());
        Assert.AreEqual("partial", report.Status);
        Assert.IsNull(report.Snapshot);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "snapshot.json")));
    }

    [TestMethod]
    public async Task BoundedProbeRecordsNormalLoopsWithoutFailingThem()
    {
        var options = AuditFixture.Options("--tab-steps", "1", "--accept-input", "--observe-seconds", "1");
        var report = await Run(new PlatformFake(), options);
        Assert.AreEqual("completed", report.Status);
        Assert.AreEqual(0, report.RuleFailureCount);
        Assert.IsNotNull(report.Observation);
        Assert.IsTrue(report.Results.Any(result => result.CheckId == "keyboard.repeats" && result.Status == "observed"));
    }

    [TestMethod]
    public async Task ScreenshotIssuesKeepBaselineAndSeparatePostCaptureSnapshot()
    {
        var platform = new PlatformFake { Scan = AuditFixture.Axe(true) };
        var report = await Run(platform, AuditFixture.Options() with { Screenshot = true });
        CollectionAssert.AreEqual(new[] { "audit-snapshot", "audit-screenshot", "audit-snapshot" }, platform.Commands.ToArray());
        Assert.IsTrue(report.Artifacts.Any(artifact => artifact.Path == AuditVisuals.VerificationPath));
        using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "snapshot.json")));
        Assert.IsTrue(JsonElement.DeepEquals(original.RootElement, report.Snapshot!.Value));
        Assert.AreEqual(1, report.RuleFailureCount);
        Assert.IsFalse(platform.Commands.Any(command => command is "click" or "focus" or "scroll" or "send-keys"));
    }

    [TestMethod]
    public async Task ScreenshotWithoutIssueCandidatesDoesNotRepeatTheTreeWalk()
    {
        var platform = new PlatformFake();
        var report = await Run(platform, AuditFixture.Options() with { Screenshot = true });
        CollectionAssert.AreEqual(new[] { "audit-snapshot", "audit-screenshot" }, platform.Commands.ToArray());
        Assert.IsFalse(report.Artifacts.Any(artifact => artifact.Path == AuditVisuals.VerificationPath));
    }

    [TestMethod]
    public void SensitiveAxeValuesAreNotKeptInTheNewReportPath()
    {
        var finding = ScanFixture.MissingName() with
        {
            ElementProperties = new Dictionary<string, string>
            {
                ["ProcessId"] = "1234", ["IsPassword"] = "True",
                ["Name"] = "secret", ["Value.Value"] = "secret", ["Text"] = "secret", ["AutomationId"] = "Password"
            }
        };
        var result = AuditPrivacy.Redact(ScanFixture.Create(finding)).Findings.Single();
        Assert.IsFalse(result.ElementProperties.ContainsKey("Name"));
        Assert.IsFalse(result.ElementProperties.ContainsKey("Value.Value"));
        Assert.IsFalse(result.ElementProperties.ContainsKey("Text"));
        Assert.AreEqual("Password", result.AutomationId);
    }

    private Task<AuditReport> Run(PlatformFake platform, AuditOptions options) =>
        new AuditRunner(platform).RunAsync(options, AuditFixture.Target, "fixture-cli", directory, CancellationToken.None);

    private sealed class PlatformFake : IAuditPlatform
    {
        public JsonElement Snapshot { get; init; } = AuditFixture.Snapshot();
        public AccessibilityScan Scan { get; init; } = AuditFixture.Axe();
        public int SnapshotExit { get; init; }
        public int AxeCalls { get; private set; }
        public List<string> Commands { get; } = [];
        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("fixture-cli");
        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) => Task.FromResult(AuditFixture.Target);
        public void VerifyTarget(AuditTarget target) { }
        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
        {
            AxeCalls++;
            return Task.FromResult(Scan);
        }
        public Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            Commands.Add(arguments[0]);
            if (arguments[0] == "audit-screenshot")
            {
                var path = arguments[2];
                File.WriteAllBytes(path, Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0f0AAAAASUVORK5CYII="));
                return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, processId = target.ProcessId, windowHandle = target.WindowHandle,
                    status = "captured", captureMethod = "windows-graphics-capture",
                    activationRequested = false, filePath = path, width = 1, height = 1
                }), ""));
            }
            return Task.FromResult(arguments[0] switch
            {
                "audit-snapshot" => new CommandResult(SnapshotExit, Snapshot.GetRawText(), ""),
                "audit-observe" => new CommandResult(0, AuditFixture.Observation().GetRawText(), ""),
                _ => throw new InvalidOperationException("Unexpected command in read-only audit test.")
            });
        }
    }
}
