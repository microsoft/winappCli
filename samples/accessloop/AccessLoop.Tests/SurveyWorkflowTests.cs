using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class SurveyWorkflowTests
{
    private AuditTestFixture fixture = null!;
    private SurveyProfileFile profile = null!;
    private SurveyStore store = null!;
    private FakeSession session = null!;
    private SurveyState state = null!;

    [TestInitialize]
    public void Initialize()
    {
        fixture = new AuditTestFixture();
        var definition = new SurveyProfile
        {
            Name = "Installed app", ProcessName = "Demo", NavigationPaneId = "PaneRoot", MenuRootId = "MenuItemsHost",
            ItemClassName = "Microsoft.UI.Xaml.Controls.NavigationViewItem", HeadingAutomationId = "Header",
            GroupIds = ["SystemToolsNavItem"], ExcludedIds = ["FeedbackNavItem"],
            HeadingAliases = new Dictionary<string, string[]> { ["AwakeNavItem"] = ["唤醒"] }
        };
        var inputPath = Path.Combine(fixture.Root, "input-profile.json");
        AuditFiles.WriteNew(inputPath, definition);
        profile = SurveyProfileFile.Read(inputPath);
        var root = Path.Combine(fixture.Root, "survey");
        Directory.CreateDirectory(root);
        store = new SurveyStore(root);
        session = new FakeSession(fixture);
        state = new()
        {
            RunId = "survey-test", Name = profile.Profile.Name, StartedAtUtc = AuditTestFixture.Time,
            ProfileSha256 = profile.Sha256, Target = session.Target, Context = session.GetContext(),
            Tools = new("fixture-cli", new string('A', 64), new string('B', 64))
        };
        store.Initialize(profile, state);
    }

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public async Task PauseThenResumeSkipsPreviouslyCapturedPageAndKeepsItsBytes()
    {
        await Run(stopAfter: 1);
        Assert.AreEqual("paused", state.Status);
        Assert.AreEqual(3, SurveyRunner.ExitCode(state));
        Assert.AreEqual("captured", state.Pages[0].Status);
        Assert.AreEqual("pending", state.Pages[1].Status);
        var oldPath = Path.Combine(store.Root, state.Pages[0].Attempts[0].Directory, "scan.json");
        var hash = AuditFiles.Hash(oldPath);
        (state, profile) = store.Load();
        session.Navigated.Clear();

        await Run(resume: true);

        Assert.AreEqual("finished", state.Status);
        Assert.AreEqual(2, SurveyRunner.ExitCode(state), "Privacy-only partial capture must not become a compliance pass.");
        CollectionAssert.AreEqual(new[] { "AwakeNavItem" }, session.Navigated.ToArray());
        Assert.AreEqual(hash, AuditFiles.Hash(oldPath));
        Assert.AreEqual(1, state.Pages[0].Attempts.Count);
        Assert.AreEqual(2, state.Invocations.Count);
        Assert.IsTrue(state.Invocations[1].OperatorConfirmedUnchangedState);
    }

    [TestMethod]
    public async Task TargetedRetryCreatesNewAttemptWithoutErasingEarlierEvidence()
    {
        await Run();
        var page = state.Pages[0];
        var oldPath = Path.Combine(store.Root, page.Attempts[0].Directory, "scan.json");
        var hash = AuditFiles.Hash(oldPath);

        await Run(resume: true, retry: ["GeneralNavItem"]);

        Assert.AreEqual(2, page.Attempts.Count);
        Assert.AreNotEqual(page.Attempts[0].Directory, page.Attempts[1].Directory);
        Assert.AreEqual(hash, AuditFiles.Hash(oldPath));
        Assert.AreEqual(1, state.Pages[1].Attempts.Count);
        _ = store.Load();
    }

    [TestMethod]
    public async Task BlockedRetryDoesNotSilentlyCountAnOldSuccessAsCurrent()
    {
        await Run();
        session.BadHeadingFor = "GeneralNavItem";

        await Run(resume: true, retry: ["GeneralNavItem"]);

        Assert.AreEqual("paused", state.Status);
        Assert.AreEqual("blocked", state.Pages[0].Status);
        Assert.AreEqual("captured", state.Pages[0].Attempts[0].Status);
        Assert.AreEqual("blocked", state.Pages[0].Attempts[1].Status);
        Assert.IsTrue(File.Exists(Path.Combine(store.Root, state.Pages[0].Attempts[0].Directory, "scan.json")));
    }

    [TestMethod]
    public async Task ChangedEnvironmentOrApplicationCannotResume()
    {
        await Run(stopAfter: 1);
        Assert.ThrowsExactly<SurveyBlockedException>(() => SurveyRunner.EnsureCompatible(state, session.Target,
            session.Context with { Environment = session.Context.Environment with { Dpi = 144 } }, state.Tools));
        Assert.ThrowsExactly<SurveyBlockedException>(() => SurveyRunner.EnsureCompatible(state, session.Target,
            session.Context with { Application = session.Context.Application with { ProductVersion = "changed" } }, state.Tools));
        Assert.ThrowsExactly<SurveyBlockedException>(() => SurveyRunner.EnsureCompatible(state,
            session.Target with { ProcessStartedAtUtc = session.Target.ProcessStartedAtUtc.AddSeconds(1) },
            session.Context, state.Tools));
        Assert.ThrowsExactly<SurveyBlockedException>(() => SurveyRunner.EnsureCompatible(state,
            session.Target, session.Context, state.Tools with { RunnerSha256 = new string('C', 64) }));
    }

    [TestMethod]
    public async Task TamperedPageEvidenceIsRejectedBeforeResume()
    {
        await Run(stopAfter: 1);
        var file = Path.Combine(store.Root, state.Pages[0].Attempts[0].Directory, AuditTestFixture.SnapshotPath);
        File.AppendAllText(file, "changed");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
    }

    [TestMethod]
    public async Task SavedProfileAndInventoryCannotBeReplaced()
    {
        await Run(stopAfter: 1);
        var inventory = Path.Combine(store.Root, "inventory.json");
        File.AppendAllText(inventory, " ");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
    }

    [TestMethod]
    public async Task DeletedPageInRunStateCannotShrinkTheFrozenScope()
    {
        await Run(stopAfter: 1);
        var path = Path.Combine(store.Root, "survey-state.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["pages"]!.AsArray().RemoveAt(1);
        File.WriteAllText(path, json.ToJsonString(Json.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
    }

    [TestMethod]
    public async Task LockPreventsTwoSimultaneousJobRunners()
    {
        using var held = store.Lock();
        Assert.ThrowsExactly<IOException>(() => store.Lock());
        await Run(stopAfter: 1);
    }

    [TestMethod]
    public async Task MinimizedWindowPausesInsteadOfSendingInput()
    {
        session.PreflightFailure = new SurveyBlockedException("window-minimized", "Restore the window.", true);
        await Run();
        Assert.AreEqual("paused", state.Status);
        Assert.AreEqual(0, session.Navigated.Count);
        Assert.AreEqual(0, session.Scans);
        StringAssert.Contains(state.Error!, "window-minimized");
    }

    [TestMethod]
    public async Task RestorationFailureIsRecordedWithoutClosingTheApp()
    {
        session.RestoreResult = new("incomplete", "The input desktop is unavailable.");
        await Run(stopAfter: 1);
        Assert.AreEqual("incomplete", state.Invocations[0].Restoration!.Status);
        Assert.AreEqual(1, session.RestoreCalls);
    }

    [TestMethod]
    public async Task CancellationKeepsCheckpointAndRestoresNavigation()
    {
        using var cancellation = new CancellationTokenSource();
        session.AfterFirstScan = cancellation.Cancel;
        var options = Options(stopAfter: null, resume: false, retry: []);
        await new SurveyRunner(store, session, profile.Profile).RunAsync(state, options, cancellation.Token);
        Assert.AreEqual("paused", state.Status);
        Assert.IsTrue(state.Pages.Any(page => page.Attempts.Count > 0));
        Assert.AreEqual(1, session.RestoreCalls);
        _ = store.Load();
    }

    [TestMethod]
    public void OptionsRequireNavigationAndResumeStateAcknowledgement()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SurveyOptionsParser.Parse(
            ["survey", "p.json", "--window", "42", "--output", "out"]));
        Assert.ThrowsExactly<ArgumentException>(() => SurveyOptionsParser.Parse(
            ["survey", "--resume", "job", "--accept-navigation"]));
        Assert.ThrowsExactly<ArgumentException>(() => SurveyOptionsParser.Parse(
            ["survey", "--resume", "job", "--accept-navigation", "--confirm-unchanged", "--window", "42"]));
        Assert.ThrowsExactly<ArgumentException>(() => SurveyOptionsParser.Parse(
            ["survey", "p.json", "--pid", "42", "--accept-navigation", "--output", "out", "--apply"]));
        var parsed = SurveyOptionsParser.Parse(
            ["survey", "--resume", "job", "--accept-navigation", "--confirm-unchanged", "--retry-page", "GeneralNavItem", "--stop-after", "1"]);
        Assert.AreEqual(1, parsed.StopAfter);
        CollectionAssert.AreEqual(new[] { "GeneralNavItem" }, parsed.RetryPages.ToArray());
    }

    [TestMethod]
    public void ProfileRejectsArbitraryControlClassesAndAmbiguousPolicy()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SurveyProfileFile.Validate(profile.Profile with { ItemClassName = "Button" }));
        Assert.ThrowsExactly<InvalidDataException>(() => SurveyProfileFile.Validate(profile.Profile with { GroupIds = ["same", "same"] }));
        Assert.ThrowsExactly<InvalidDataException>(() => SurveyProfileFile.Validate(profile.Profile with { GroupIds = ["FeedbackNavItem"] }));
        Assert.ThrowsExactly<InvalidDataException>(() => SurveyProfileFile.Validate(profile.Profile with { NavigationPaneId = "..\\outside" }));
    }

    [TestMethod]
    public void ConfirmedHeadingAliasesAreExactAndNotGuessed()
    {
        var page = new SurveyPageDefinition("AwakeNavItem", "Awake", ["SystemToolsNavItem"]);
        Assert.IsTrue(SurveyProfileFile.MatchesHeading(profile.Profile, page, "唤醒"));
        Assert.IsTrue(SurveyProfileFile.MatchesHeading(profile.Profile, page, "Awake"));
        Assert.IsTrue(SurveyProfileFile.MatchesHeading(profile.Profile, page, "Awake "));
        Assert.IsFalse(SurveyProfileFile.MatchesHeading(profile.Profile, page, "A different page"));
    }

    private SurveyOptions Options(int? stopAfter, bool resume, IReadOnlyList<string> retry) =>
        new(resume ? null : "profile.json", resume ? store.Root : null, null, resume ? null : 73,
            "winapp", resume ? null : store.Root, true, resume, stopAfter, retry, false, false);

    private Task Run(int? stopAfter = null, bool resume = false, IReadOnlyList<string>? retry = null) =>
        new SurveyRunner(store, session, profile.Profile).RunAsync(state, Options(stopAfter, resume, retry ?? []), CancellationToken.None);

    private sealed class FakeSession(AuditTestFixture fixture) : ISurveySession
    {
        public AuditTarget Target => fixture.Report.Target;
        public SurveyContext Context { get; } = new(
            new("Demo", @"C:\fixture\Demo.exe", new string('1', 64), "1.0"),
            new(1000, 700, 96, "Windows", "zh-CN", false, true));
        public List<string> Navigated { get; } = [];
        public string? BadHeadingFor { get; set; }
        public int Scans { get; private set; }
        public int RestoreCalls { get; private set; }
        public SurveyBlockedException? PreflightFailure { get; set; }
        public SurveyRestoration RestoreResult { get; set; } = new("restored", "Restored navigation.");
        public Action? AfterFirstScan { get; set; }
        public SurveyPerformance Performance => new(5, 10, 20);
        public SurveyContext GetContext() => Context;
        public void Preflight()
        {
            if (PreflightFailure is not null)
                throw PreflightFailure;
        }
        public Task<SurveyNavigationState> ReadNavigationStateAsync(CancellationToken token) =>
            Task.FromResult(new SurveyNavigationState("GeneralNavItem", new Dictionary<string, bool> { ["SystemToolsNavItem"] = false }));
        public Task<SurveyInventory> DiscoverAsync(SurveyNavigationState original, CancellationToken token) =>
            Task.FromResult(new SurveyInventory(
                [new("GeneralNavItem", "General", []), new("AwakeNavItem", "Awake", ["SystemToolsNavItem"])],
                [new("FeedbackNavItem", "Feedback", "External")],
                ["GeneralNavItem", "AwakeNavItem", "FeedbackNavItem", "SystemToolsNavItem"], original));
        public Task ValidateInventoryAsync(SurveyInventory inventory, CancellationToken token) => Task.CompletedTask;
        public Task<SurveyPageReady> NavigateAsync(SurveyPageDefinition page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Navigated.Add(page.Id);
            return Task.FromResult(new SurveyPageReady(true, page.Id != BadHeadingFor,
                page.Id == BadHeadingFor ? "Unexpected" : page.Id == "AwakeNavItem" ? "唤醒" : page.Name, DateTimeOffset.UtcNow));
        }
        public Task<SurveyPageReady> VerifyPageAsync(SurveyPageDefinition page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new SurveyPageReady(true, true, page.Name, DateTimeOffset.UtcNow));
        }
        public Task<AuditReport> ScanAsync(string outputDirectory, CancellationToken token)
        {
            Scans++;
            foreach (var artifact in fixture.Report.Artifacts)
                AuditFiles.CopyVerifiedArtifact(fixture.Source, artifact, outputDirectory);
            var report = fixture.Report with { RunId = Guid.NewGuid().ToString("N"), Mode = "snapshot", Observation = null };
            AuditFiles.WriteNew(Path.Combine(outputDirectory, "scan.json"), report);
            AuditReportRenderer.Write(Path.Combine(outputDirectory, "report.html"), report);
            if (Scans == 1)
                AfterFirstScan?.Invoke();
            return Task.FromResult(report);
        }
        public Task<SurveyRestoration> RestoreAsync(SurveyNavigationState original, CancellationToken token)
        {
            RestoreCalls++;
            return Task.FromResult(RestoreResult);
        }
    }
}
