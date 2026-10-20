using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class TabAuditTests
{
    private string directory = null!;
    private TestClock clock = null!;
    private static AuditOptions Options(params string[] extra) => AuditOptions.Parse(
        ["scan", "--window", "42", "--navigation", "standard-tabs", "--accept-navigation", "--no-screenshot", .. extra]);

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "accessloop-tab-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        clock = new();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void PageNavigationAndKeyboardTabHaveSeparateConsent()
    {
        var options = Options();
        Assert.AreEqual("standard-tabs", options.Navigation);
        Assert.AreEqual(20, options.MaxPages);
        Assert.AreEqual(0, options.TabSteps);
        Assert.IsFalse(options.AcceptInput);
        Assert.IsNull(AuditFixture.Options().Navigation);
        foreach (string[] extra in new string[][]
        {
            ["--navigation", "standard-tabs"], ["--accept-navigation"], ["--max-pages", "3"],
            ["--navigation", "all-controls", "--accept-navigation"],
            ["--navigation", "standard-tabs", "--accept-navigation", "--max-pages", "0"],
            ["--navigation", "standard-tabs", "--accept-navigation", "--max-pages", "101"],
            ["--navigation", "standard-tabs", "--accept-navigation", "--tab-steps", "2", "--accept-input"]
        })
            Assert.ThrowsExactly<ArgumentException>(() => AuditOptions.Parse(["scan", "--window", "42", .. extra]));
    }

    [TestMethod]
    public async Task NoConsentDoesNotReadOrNavigate()
    {
        var client = new NavigationFake();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Run(client, Options() with { AcceptNavigation = false }));
        Assert.AreEqual(0, client.ReadCount);
        Assert.AreEqual(0, client.Actions.Count);
    }

    [TestMethod]
    public async Task VisitsEveryTabWithoutAutomationIdsAndRestoresOriginal()
    {
        var client = new NavigationFake();
        var report = await Run(client);
        Assert.AreEqual("completed", report.Status);
        Assert.AreEqual("completed", report.NavigationStatus);
        Assert.AreEqual(3, report.CapturedPageCount);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" },
            report.Pages.Select(page => page.SelectionPath.Single().ItemRuntimeId).ToArray());
        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, client.Actions.Select(item => item.ItemRuntimeId).ToArray());
        Assert.IsTrue(report.Pages.SelectMany(page => page.SelectionPath).All(item => item.AutomationId is null));
        Assert.AreEqual("restored", report.Restorations.Single().Status);
        foreach (var page in report.Pages)
        {
            var scan = Path.Combine(directory, page.Directory, "scan.json");
            Assert.AreEqual(page.ScanSha256, AuditFiles.Hash(scan));
            Assert.IsNotNull(AuditFiles.ReadVerified(scan).Snapshot);
            Assert.IsTrue(File.Exists(Path.Combine(directory, page.Directory, "navigation.json")));
        }
        Assert.AreEqual(0, TabAuditRunner.ExitCode(report));
    }

    [TestMethod]
    public async Task UnselectedDisabledAndOffscreenTabsAreExplicitCoverageGaps()
    {
        var client = new NavigationFake { Disabled = "b", Offscreen = "c" };
        var report = await Run(client);
        Assert.AreEqual(1, report.CapturedPageCount);
        Assert.AreEqual(2, report.Skipped.Count);
        Assert.AreEqual(0, client.Actions.Count);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(2, TabAuditRunner.ExitCode(report));
    }

    [TestMethod]
    public async Task PageBudgetIsExactAndDoesNotDisableRestoration()
    {
        var client = new NavigationFake();
        var report = await Run(client, Options("--max-pages", "2"));
        Assert.AreEqual(2, report.CapturedPageCount);
        Assert.AreEqual("partial", report.NavigationStatus);
        Assert.AreEqual("c", report.Skipped.Single().ItemRuntimeId);
        CollectionAssert.AreEqual(new[] { "b", "a" }, client.Actions.Select(item => item.ItemRuntimeId).ToArray());
        Assert.AreEqual("restored", report.Restorations.Single().Status);
    }

    [TestMethod]
    public async Task NestedGroupsAreVisitedOnlyWithinTheirParentPage()
    {
        var client = new NavigationFake { Nested = true };
        var report = await Run(client);
        Assert.AreEqual("completed", report.NavigationStatus);
        Assert.AreEqual(5, report.CapturedPageCount);
        var nestedPages = report.Pages.Where(page => page.SelectionPath.Count == 2).ToArray();
        Assert.AreEqual(2, nestedPages.Length);
        Assert.IsTrue(nestedPages.All(page => page.SelectionPath[0].ItemRuntimeId == "b"));
        CollectionAssert.AreEqual(new[] { "n1", "n2" }, nestedPages.Select(page => page.SelectionPath[1].ItemRuntimeId).ToArray());
        Assert.AreEqual(2, report.Restorations.Count);
        Assert.IsTrue(report.Restorations.All(item => item.Status == "restored"));
    }

    [TestMethod]
    public async Task SelectionReceiptsAreFollowedByFreshDiscoveryBeforeUsingNestedInventory()
    {
        var client = new NavigationFake { Nested = true, ReturnPreSelectionInventory = true };
        var report = await Run(client);
        Assert.AreEqual("completed", report.NavigationStatus);
        Assert.AreEqual(5, report.CapturedPageCount);
        Assert.AreEqual(2, report.Pages.Count(page => page.SelectionPath.Count == 2));
        Assert.AreEqual("a", client.Selected);
    }

    [TestMethod]
    public async Task IndependentGroupsAreNotExpandedIntoAllCombinations()
    {
        var client = new NavigationFake { Independent = true };
        var report = await Run(client);
        Assert.AreEqual("completed", report.NavigationStatus);
        Assert.AreEqual(5, report.CapturedPageCount);
        Assert.IsTrue(report.Pages.All(page => page.SelectionPath.Count == 1));
        Assert.AreEqual("a", client.Selected);
        Assert.AreEqual("n1", client.NestedSelected);
    }

    [TestMethod]
    public async Task PartialAuditEvidenceNeverBecomesACompletedAggregate()
    {
        var client = new NavigationFake();
        var report = await Run(client, partial: true, failures: true);
        Assert.AreEqual("completed", report.NavigationStatus);
        Assert.AreEqual("partial", report.CaptureStatus);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(3, report.RuleFailureCount);
        Assert.AreEqual(2, TabAuditRunner.ExitCode(report));
        TabAuditReportRenderer.Write(Path.Combine(directory, "report.html"), report);
        var html = File.ReadAllText(Path.Combine(directory, "report.html"));
        StringAssert.Contains(html, "NameNotNull");
        StringAssert.Contains(html, "rule-failure occurrences");
        StringAssert.Contains(html, "not a deduplicated");
    }

    [TestMethod]
    public async Task NoStandardTabsStillCapturesCurrentPageWithoutAnyAction()
    {
        var client = new NavigationFake { NoTabs = true };
        var report = await Run(client);
        Assert.AreEqual(1, report.CapturedPageCount);
        Assert.AreEqual(0, report.Pages.Single().SelectionPath.Count);
        Assert.AreEqual("not-applicable", report.NavigationStatus);
        Assert.AreEqual("completed", report.Status);
        Assert.AreEqual(0, report.Skipped.Count);
        Assert.AreEqual(0, TabAuditRunner.ExitCode(report));
        Assert.AreEqual(0, client.Actions.Count);
        Assert.AreEqual(0, report.Restorations.Count);
        TabAuditReportRenderer.Write(Path.Combine(directory, "report.html"), report);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "report.html")), "No standard tab groups were discovered");
    }

    [TestMethod]
    public async Task NoTabsDoesNotHideCurrentPageRuleFailuresOrCaptureGaps()
    {
        var client = new NavigationFake { NoTabs = true };
        var report = await Run(client, partial: true, failures: true);
        Assert.AreEqual("not-applicable", report.NavigationStatus);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(1, report.RuleFailureCount);
        Assert.AreEqual(2, TabAuditRunner.ExitCode(report));
    }

    [TestMethod]
    public async Task IncompleteDiscoveryNeverAuthorizesSelection()
    {
        var client = new NavigationFake { Status = "partial" };
        var report = await Run(client);
        Assert.AreEqual(1, report.CapturedPageCount);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(0, client.Actions.Count);
        StringAssert.Contains(report.Skipped.Single().Reason, "incomplete");
    }

    [TestMethod]
    public async Task ModalAfterSelectionStopsWithoutFurtherInputOrRestoration()
    {
        var client = new NavigationFake { BlockOnSelection = true };
        var report = await Run(client);
        Assert.AreEqual("partial", report.NavigationStatus);
        Assert.AreEqual(1, report.CapturedPageCount);
        CollectionAssert.AreEqual(new[] { "b" }, client.Actions.Select(item => item.ItemRuntimeId).ToArray());
        Assert.AreEqual("not-attempted", report.Restorations.Single().Status);
        StringAssert.Contains(report.Error!, "modal");
    }

    [TestMethod]
    public async Task ChangedSelectionDuringAuditKeepsEvidenceButBlocksPageAttribution()
    {
        var client = new NavigationFake();
        var platform = new AuditPlatformFake { AfterScan = () => client.Selected = "c" };
        var report = await new TabAuditRunner(platform, client, NoDelay, clock).RunAsync(
            Options(), AuditFixture.Target, "fixture-cli", directory, CancellationToken.None);
        Assert.AreEqual("blocked", report.Pages.Single().Status);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(0, client.Actions.Count);
        var scan = AuditFiles.ReadVerified(Path.Combine(directory, report.Pages.Single().Directory, "scan.json"));
        Assert.AreEqual("partial", scan.Status);
        Assert.IsNotNull(scan.Error);
    }

    [TestMethod]
    public async Task UnstableStructureCannotBeAcceptedAsAReadyPage()
    {
        var client = new NavigationFake { Unstable = true };
        var report = await Run(client, Options("--timeout-ms", "1000"));
        Assert.AreEqual("blocked", report.Status);
        Assert.AreEqual(0, report.Pages.Count);
        Assert.IsFalse(report.HasAuditResults);
        Assert.IsNull(report.RuleFailureCount);
        StringAssert.Contains(report.Error!, "did not settle");
        TabAuditReportRenderer.Write(Path.Combine(directory, "report.html"), report);
        var html = File.ReadAllText(Path.Combine(directory, "report.html"));
        StringAssert.Contains(html, "Not evaluated");
        Assert.IsFalse(html.Contains("0 rule-failure occurrences", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, Json.Options));
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("ruleFailureCount").ValueKind);
    }

    [TestMethod]
    public async Task SlowMatchingSamplesUseElapsedStabilityAndAcceptTheFinalBoundedRead()
    {
        // Preserve the timing and signature sequence from the failed real run.
        int[] milliseconds = [17796, 4794, 5149, 5956, 4261];
        var client = new NavigationFake
        {
            OnRead = read => clock.Advance(TimeSpan.FromMilliseconds(read <= milliseconds.Length ? milliseconds[read - 1] : 1000)),
            SignatureAtRead = read => read <= 2 ? 'A' : read == 3 ? 'B' : 'C'
        };
        var report = await Run(client, Options("--max-pages", "1"));
        Assert.AreEqual(1, report.CapturedPageCount);
        Assert.IsTrue(report.HasAuditResults);
        Assert.IsNull(report.Error);
        Assert.AreEqual(0, client.Actions.Count);
        Assert.AreEqual("completed", report.CaptureStatus);
    }

    [TestMethod]
    public async Task FastSamplesStillRequireHalfASecondOfUnchangedStructure()
    {
        var client = new NavigationFake();
        long? firstCapture = null;
        var platform = new AuditPlatformFake { AfterScan = () => firstCapture ??= clock.GetTimestamp() };
        _ = await new TabAuditRunner(platform, client, NoDelay, clock).RunAsync(
            Options("--max-pages", "1"), AuditFixture.Target, "fixture-cli", directory, CancellationToken.None);
        Assert.IsNotNull(firstCapture);
        Assert.IsTrue(TimeSpan.FromTicks(firstCapture.Value) >= TimeSpan.FromMilliseconds(500));
    }

    [TestMethod]
    public async Task SlowButChangingSamplesStillBlockWithoutAnyAuditResults()
    {
        var client = new NavigationFake
        {
            OnRead = _ => clock.Advance(TimeSpan.FromSeconds(6)),
            Unstable = true
        };
        var report = await Run(client);
        Assert.AreEqual("blocked", report.Status);
        Assert.IsFalse(report.HasAuditResults);
        Assert.IsNull(report.RuleFailureCount);
        Assert.IsEmpty(client.Actions);
        Assert.IsTrue(client.ReadCount <= 5, "Do not start another provider call after exhausting the settling budget.");
    }

    [TestMethod]
    public async Task CancellationDuringCaptureKeepsReportAndNeverRestoresBySendingMoreInput()
    {
        using var cancelled = new CancellationTokenSource();
        var client = new NavigationFake();
        var calls = 0;
        var platform = new AuditPlatformFake { AfterScan = () => { if (++calls == 2) cancelled.Cancel(); } };
        var report = await new TabAuditRunner(platform, client, NoDelay, clock).RunAsync(
            Options(), AuditFixture.Target, "fixture-cli", directory, cancelled.Token);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(2, report.Pages.Count);
        Assert.AreEqual("not-attempted", report.Restorations.Single().Status);
        CollectionAssert.AreEqual(new[] { "b" }, client.Actions.Select(item => item.ItemRuntimeId).ToArray());
        StringAssert.Contains(report.Error!, "cancelled");
        Assert.IsTrue(File.Exists(Path.Combine(directory, report.Pages[1].Directory, "scan.json")));
    }

    [TestMethod]
    public async Task RestorationFailureRemainsVisibleAfterAllPagesWereCaptured()
    {
        var client = new NavigationFake { RejectRestore = true };
        var report = await Run(client);
        Assert.AreEqual(3, report.CapturedPageCount);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("blocked", report.Restorations.Single().Status);
        StringAssert.Contains(report.Error!, "restore");
        Assert.AreEqual(2, TabAuditRunner.ExitCode(report));
    }

    [TestMethod]
    public async Task OriginalTabMovedOffscreenIsNotRestoredThroughAnUnapprovedFallback()
    {
        var client = new NavigationFake { OriginalMovesOffscreen = true };
        var report = await Run(client);
        Assert.AreEqual(3, report.CapturedPageCount);
        Assert.AreEqual("blocked", report.Restorations.Single().Status);
        Assert.AreEqual("partial", report.NavigationStatus);
        StringAssert.Contains(report.Error!, "(offscreen)");
        StringAssert.Contains(report.Error!, "manually");
        CollectionAssert.AreEqual(new[] { "b", "c" }, client.Actions.Select(item => item.ItemRuntimeId).ToArray());
    }

    [TestMethod]
    public async Task AChangedItemInventoryIsNotSilentlyExtendedDuringTraversal()
    {
        var client = new NavigationFake { ChangeInventoryOnSelection = true };
        var report = await Run(client);
        Assert.AreEqual("partial", report.NavigationStatus);
        Assert.IsTrue(report.CapturedPageCount < 3);
        Assert.AreEqual("not-attempted", report.Restorations.Single().Status);
        Assert.IsFalse(client.Actions.Any(action => action.ItemRuntimeId == "d"));
    }

    [TestMethod]
    public async Task NativeClientRejectsSelectionWithoutConsentBeforeLaunchingCommand()
    {
        var platform = new AuditPlatformFake { Navigation = new NavigationFake() };
        var client = new TabNavigationClient(platform, AuditFixture.Target, Options() with { AcceptNavigation = false },
            directory, []);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            client.ReadAsync(CancellationToken.None, new("tabs", "b", null), "root"));
        Assert.AreEqual(0, platform.Commands.Count);
    }

    [TestMethod]
    public void NativeDocumentsRejectAmbiguousRuntimeIdsAndCyclicAncestry()
    {
        var client = new NavigationFake();
        var snapshot = client.Snapshot();
        TabNavigationClient.Validate(snapshot, AuditFixture.Target, Options());
        var group = snapshot.Containers.Single();
        Assert.ThrowsExactly<InvalidDataException>(() => TabNavigationClient.Validate(snapshot with
        {
            Containers = [group with { Items = [group.Items[0], group.Items[0]] }]
        }, AuditFixture.Target, Options()));
        Assert.ThrowsExactly<InvalidDataException>(() => TabNavigationClient.Validate(snapshot with
        {
            Containers = [group with { ParentContainerRuntimeId = group.RuntimeId }]
        }, AuditFixture.Target, Options()));
        Assert.ThrowsExactly<InvalidDataException>(() => TabNavigationClient.Validate(snapshot with
        {
            ProcessId = snapshot.ProcessId + 1
        }, AuditFixture.Target, Options()));
    }

    [TestMethod]
    public async Task NativeClientSendsOnlyExactSelectionAndPreservesReceipt()
    {
        var client = new NavigationFake();
        var platform = new AuditPlatformFake { Navigation = client };
        var artifacts = new List<AuditArtifact>();
        var native = new TabNavigationClient(platform, AuditFixture.Target, Options(), directory, artifacts);
        await native.ReadAsync(CancellationToken.None);
        var selected = await native.ReadAsync(CancellationToken.None, new("tabs", "b", null), "root");
        Assert.AreEqual("selected", selected.Selection!.Status);
        var action = platform.Commands.Last();
        CollectionAssert.AreEqual(new[]
        {
            "audit-tabs", "--depth", "20", "--max-elements", "2000", "--timeout", "15000",
            "--select", "b", "--container", "tabs", "--root-runtime-id", "root", "--accept-navigation"
        }, action.ToArray());
        Assert.AreEqual(2, artifacts.Count);
        Assert.IsTrue(artifacts.All(artifact => File.Exists(Path.Combine(directory, artifact.Path))));
    }

    private Task<TabAuditReport> Run(NavigationFake client, AuditOptions? options = null, bool partial = false, bool failures = false) =>
        new TabAuditRunner(new AuditPlatformFake { Partial = partial, Failures = failures }, client, NoDelay, clock)
            .RunAsync(options ?? Options(), AuditFixture.Target, "fixture-cli", directory, CancellationToken.None);

    private Task NoDelay(TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        clock.Advance(duration);
        return Task.CompletedTask;
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class NavigationFake : ITabNavigationClient
    {
        public string Selected { get; set; } = "a";
        public string NestedSelected { get; private set; } = "n1";
        public string? Disabled { get; init; }
        public string? Offscreen { get; init; }
        public bool Nested { get; init; }
        public bool Independent { get; init; }
        public bool NoTabs { get; init; }
        public bool BlockOnSelection { get; init; }
        public bool RejectRestore { get; init; }
        public bool ChangeInventoryOnSelection { get; init; }
        public bool ReturnPreSelectionInventory { get; init; }
        public bool OriginalMovesOffscreen { get; init; }
        public bool Unstable { get; init; }
        public string Status { get; init; } = "captured";
        public int ReadCount { get; private set; }
        public Action<int>? OnRead { get; init; }
        public Func<int, char>? SignatureAtRead { get; init; }
        public List<TabSelection> Actions { get; } = [];

        public Task<TabNavigationSnapshot> ReadAsync(CancellationToken token, TabSelection? selection = null, string? rootRuntimeId = null)
        {
            token.ThrowIfCancellationRequested();
            ReadCount++;
            OnRead?.Invoke(ReadCount);
            var before = Snapshot();
            if (selection is not null)
            {
                Assert.AreEqual("root", rootRuntimeId);
                if (RejectRestore && selection.ItemRuntimeId == "a" && Actions.Count > 0)
                    throw new InvalidOperationException("Cannot restore the original tab.");
                Actions.Add(selection);
                if (selection.ContainerRuntimeId == "tabs")
                    Selected = selection.ItemRuntimeId;
                else
                    NestedSelected = selection.ItemRuntimeId;
            }
            var snapshot = ReturnPreSelectionInventory && selection is not null ? before : Snapshot();
            if (selection is not null)
                snapshot = snapshot with { Selection = new(selection.ContainerRuntimeId, selection.ItemRuntimeId, "selected") };
            return Task.FromResult(snapshot);
        }

        public TabNavigationSnapshot Snapshot()
        {
            var groups = new List<TabNavigationContainer>();
            if (!NoTabs)
                groups.Add(Group("tabs", null, ChangeInventoryOnSelection && Actions.Count > 0 ? ["a", "b", "d"] : ["a", "b", "c"], Selected));
            if (Independent || Nested && Selected == "b")
                groups.Add(Group("nested", Independent ? null : "tabs", ["n1", "n2"], NestedSelected));
            return new()
            {
                SchemaVersion = 1, ProcessId = AuditFixture.Target.ProcessId, WindowHandle = AuditFixture.Target.WindowHandle,
                RootRuntimeId = "root", TreeSignature = new string(SignatureAtRead?.Invoke(ReadCount)
                    ?? (Unstable && ReadCount % 2 == 1 ? 'B' : 'A'), 64),
                Status = BlockOnSelection && Actions.Count > 0 ? "blocked" : Status,
                MaxDepth = 20, MaxElements = 2000, DepthLimitReached = false, ElementLimitReached = false,
                Containers = groups, Issues = BlockOnSelection && Actions.Count > 0 ? ["Unexpected modal dialog."] : []
            };
        }

        private TabNavigationContainer Group(string id, string? parent, string[] items, string selected) => new()
        {
            RuntimeId = id, ParentContainerRuntimeId = parent, IsEnabled = true, IsOffscreen = false, Supported = true,
            Items = items.Select(item => new TabNavigationItem
            {
                RuntimeId = item, IsEnabled = item != Disabled,
                IsOffscreen = item == Offscreen || OriginalMovesOffscreen && Selected == "c" && item == "a",
                IsSelected = item == selected, Supported = true,
                Reason = item == Offscreen || OriginalMovesOffscreen && Selected == "c" && item == "a" ? "offscreen"
                    : item == Disabled ? "disabled" : null
            }).ToArray()
        };
    }

    private sealed class AuditPlatformFake : IAuditPlatform
    {
        public bool Partial { get; init; }
        public bool Failures { get; init; }
        public Action? AfterScan { get; init; }
        public NavigationFake? Navigation { get; init; }
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("fixture-cli");
        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token) => Task.FromResult(AuditFixture.Target);
        public void VerifyTarget(AuditTarget target) { }
        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
        {
            AfterScan?.Invoke();
            return Task.FromResult(AuditFixture.Axe(Failures));
        }
        public async Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            Commands.Add(arguments);
            if (arguments[0] == "audit-tabs" && Navigation is not null)
            {
                var args = arguments.ToList();
                var index = args.IndexOf("--select");
                var selection = index < 0 ? null : new TabSelection(args[args.IndexOf("--container") + 1], args[index + 1], null);
                var snapshot = await Navigation.ReadAsync(token, selection, selection is null ? null : args[args.IndexOf("--root-runtime-id") + 1]);
                return new(0, JsonSerializer.Serialize(snapshot, Json.Options), "");
            }
            if (arguments[0] != "audit-snapshot")
                throw new InvalidOperationException("A standard-tab page audit attempted an unexpected operation: " + arguments[0]);
            return new(Partial ? 1 : 0, AuditFixture.Snapshot(partial: Partial).GetRawText(), "");
        }
    }
}
