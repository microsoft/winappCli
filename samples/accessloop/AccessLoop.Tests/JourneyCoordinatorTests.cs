using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class JourneyCoordinatorTests
{
    private JourneyWorkspace workspace = null!;
    private JourneyFake platform = null!;

    [TestInitialize]
    public void Initialize()
    {
        workspace = new JourneyWorkspace();
        platform = new JourneyFake(workspace);
    }

    [TestCleanup]
    public void Cleanup() => workspace.Dispose();

    [TestMethod]
    public async Task CompleteLoopVisitsThreePagesAndRepairsTwoThenOneThenZero()
    {
        var report = await Run(apply: true);
        Assert.AreEqual("passed", report.Status, report.Error + string.Join("; ", report.Errors));
        Assert.AreEqual(3, report.Rounds.Count);
        CollectionAssert.AreEqual(new[] { 2, 1, 0 }, report.Rounds.Select(round => round.Findings.Count).ToArray());
        Assert.IsTrue(report.Rounds.All(round => round.Pages.Count == 3));
        Assert.AreEqual(2, report.Repairs.Count(repair => repair.Status == "fixed"));
        Assert.AreEqual(3, platform.Disposals);
        Assert.AreEqual("Save document", workspace.Name("SaveDocumentButton"));
        Assert.AreEqual("Export document", workspace.Name("ExportDocumentButton"));
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Output, "routes.json")));
        Assert.AreEqual(workspace.InitialInputHash, AuditFiles.Hash(workspace.InputPath));
        Assert.IsTrue(report.FrozenRoutes.All(route => route.FromPageId == "home"));
    }

    [TestMethod]
    public async Task BaselineOnlyDiscoversAndReportsWithoutEditing()
    {
        var report = await Run(apply: false);
        Assert.AreEqual("awaiting-approval", report.Status);
        Assert.AreEqual(2, report.RemainingFindings.Count);
        Assert.AreEqual(0, platform.Proposals);
        Assert.IsNull(workspace.Name("SaveDocumentButton"));
        Assert.IsNull(workspace.Name("ExportDocumentButton"));
        Assert.AreEqual("declared-order", report.PlanSource);
    }

    [TestMethod]
    public async Task NoNavigationConsentMeansNoLaunchOrSourceEdit()
    {
        var report = await new JourneyRunner(platform).RunAsync(JourneyInput.Load(workspace.InputPath),
            workspace.Options with { AcceptNavigation = false, Apply = true }, "fixture", workspace.Output, CancellationToken.None);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(0, platform.Launches);
        Assert.AreEqual(0, platform.Proposals);
    }

    [TestMethod]
    public async Task CopilotPlanIsValidatedAndItsOrderFrozenForEveryRound()
    {
        platform.Plan = new(["home", "export", "documents"], "Inspect export first.");
        var report = await Run(true, useCopilotPlan: true);
        Assert.AreEqual("passed", report.Status);
        Assert.AreEqual("copilot", report.PlanSource);
        Assert.AreEqual(1, platform.Plans);
        foreach (var round in report.Rounds)
            CollectionAssert.AreEqual(new[] { "home", "export", "documents" }, round.Pages.Select(page => page.PageId).ToArray());
    }

    [TestMethod]
    public async Task UnapprovedPlanCannotAddOrOmitPages()
    {
        platform.Plan = new(["home", "delete-data", "documents"], "Unsafe plan.");
        var report = await Run(true, useCopilotPlan: true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(0, platform.Visits);
        Assert.AreEqual(0, platform.Proposals);
        Assert.AreEqual(1, platform.Disposals);
    }

    [TestMethod]
    public async Task PlannerReturningControlIdsInsteadOfPageIdsIsRejected()
    {
        platform.Plan = new(["NavHome", "NavDocuments", "NavExport"], "Control IDs are not page IDs.");
        var report = await Run(true, useCopilotPlan: true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(0, platform.Visits);
        Assert.AreEqual(0, platform.Proposals);
    }

    [TestMethod]
    public async Task PartialPageStopsTheFixLoopWithoutGreenCompletion()
    {
        platform.PartialPage = "documents";
        var report = await Run(true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(0, platform.Proposals);
        Assert.IsTrue(report.Errors.Any(error => error.Contains("incomplete", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApprovedPrivacyOnlyGapAllowsFixedChecksButKeepsCapturePartial()
    {
        platform.PrivacyOnlyPage = "documents";
        var report = await Run(true);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("passed", report.VerificationStatus);
        Assert.AreEqual("partial", report.CaptureStatus);
        Assert.AreEqual(2, report.Repairs.Count(repair => repair.Status == "fixed"));
        Assert.AreEqual(0, report.RemainingFindings.Count);
        Assert.IsTrue(report.Rounds.All(round => round.Pages.Single(page => page.PageId == "documents").Audit!.Status == "partial"));
    }

    [TestMethod]
    public async Task TruncationAlongsidePrivacyGapStillBlocksRepair()
    {
        platform.PrivacyOnlyPage = "documents";
        platform.TruncatedPage = true;
        var report = await Run(true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(0, platform.Proposals);
    }

    [TestMethod]
    public async Task MissingPageReadinessRemainsVisibleAndOtherPagesAreNotAssumedVisited()
    {
        platform.ThrowOnPage = "documents";
        var report = await Run(true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.AreEqual(2, report.Rounds[0].Pages.Count);
        Assert.IsTrue(report.Errors.Any(error => error.Contains("export", StringComparison.Ordinal)));
        Assert.AreEqual(0, platform.Proposals);
    }

    [TestMethod]
    public async Task RemovingANontargetControlRejectsAndRollsBackTheSelectedPatch()
    {
        platform.LoseControlAfterPatch = "HomeHeading";
        var report = await Run(true);
        Assert.AreEqual("regression", report.Status);
        Assert.AreEqual(1, report.Repairs.Count);
        Assert.IsTrue(report.Repairs[0].RolledBack);
        Assert.IsTrue(report.Errors.Any(error => error.Contains("lost", StringComparison.Ordinal)));
        Assert.IsNull(workspace.Name(report.Repairs[0].AutomationId!));
        Assert.AreEqual(2, report.RemainingFindings.Count);
    }

    [TestMethod]
    public async Task RegressingAnOriginallyPassingFunctionRejectsThePatch()
    {
        platform.FailActivationAfterPatch = true;
        var report = await Run(true);
        Assert.AreEqual("regression", report.Status);
        Assert.IsTrue(report.Repairs[0].RolledBack);
        Assert.IsTrue(report.Errors.Any(error => error.Contains("keyboard-activation", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BuildFailureAfterPatchRollsBackWithoutLosingBaseline()
    {
        platform.FailSecondLaunch = true;
        var report = await Run(true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.IsTrue(report.Repairs[0].RolledBack);
        Assert.AreEqual(2, report.RemainingFindings.Count);
        Assert.IsNull(workspace.Name(report.Repairs[0].AutomationId!));
        Assert.AreEqual(1, platform.Disposals);
    }

    [TestMethod]
    public async Task ChangingAcceptanceInAdvisorIsRejectedBeforeAnyPatch()
    {
        platform.ChangeAcceptance = true;
        var report = await Run(true);
        Assert.AreEqual("inconclusive", report.Status);
        Assert.IsNull(workspace.Name("SaveDocumentButton"));
        Assert.IsNull(workspace.Name("ExportDocumentButton"));
        Assert.IsTrue(report.Repairs[0].Errors.Any(error => error.Contains("frozen journey", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnsupportedStableFindingRemainsWhileSupportedFixesProceed()
    {
        platform.AddUnsupportedFinding = true;
        var report = await Run(true);
        Assert.AreEqual("needs-review", report.Status);
        Assert.AreEqual(2, report.Repairs.Count(repair => repair.Status == "fixed"));
        Assert.AreEqual(1, report.RemainingFindings.Count);
        Assert.AreEqual("UnsupportedFixtureRule", report.RemainingFindings[0].RuleId);
        Assert.AreEqual(3, report.RemainingFindings[0].Occurrences.Count);
    }

    [TestMethod]
    public async Task FindingWithoutStableIdentityDoesNotGetSilentlyDropped()
    {
        platform.AddUnidentifiedFinding = true;
        var report = await Run(true);
        Assert.AreEqual("needs-review", report.Status);
        Assert.AreEqual(0, platform.Proposals);
        Assert.IsTrue(report.RemainingFindings.Any(finding => !finding.StableIdentity));
    }

    [TestMethod]
    public async Task RepairBudgetIsNotReportedAsAllFixed()
    {
        workspace.UpdateInput(json => json["maxRepairs"] = 1);
        var report = await Run(true);
        Assert.AreEqual("needs-review", report.Status);
        Assert.AreEqual(1, report.Repairs.Count);
        Assert.AreEqual(1, report.RemainingFindings.Count);
    }

    [TestMethod]
    public void OptionsValidateConsentAndPlanningBeforeExecution()
    {
        Assert.ThrowsExactly<ArgumentException>(() => JourneyCommand.Parse(["journey", "journey.json", "--apply"]));
        Assert.ThrowsExactly<ArgumentException>(() => JourneyCommand.Parse(
            ["journey", "journey.json", "--accept-navigation", "--plan", "p.json", "--copilot-plan"]));
        Assert.ThrowsExactly<ArgumentException>(() => JourneyCommand.Parse(
            ["journey", "journey.json", "--accept-navigation", "--proposals", "proposals"]));
        var options = JourneyCommand.Parse(["journey", "journey.json", "--accept-navigation", "--apply", "--copilot-plan", "--json"]);
        Assert.IsTrue(options.Apply && options.UseCopilotPlan && options.Json);
    }

    [TestMethod]
    public void ContractRejectsDuplicateReadyMarkersAndPathEscape()
    {
        workspace.UpdateInput(json => json["pages"]![1]!["readyId"] = "HomePageRoot");
        Assert.ThrowsExactly<InvalidDataException>(() => JourneyInput.Load(workspace.InputPath));
        workspace.UpdateInput(json => json["project"] = "..\\outside.csproj");
        Assert.ThrowsExactly<InvalidDataException>(() => JourneyInput.Load(workspace.InputPath));
    }

    [TestMethod]
    public void SourceInventoryDetectsOtherEditsAndAddedFiles()
    {
        var input = JourneyInput.Load(workspace.InputPath);
        var source = JourneyInput.CaptureSources(input.Definition.Project);
        File.WriteAllText(Path.Combine(workspace.App, "new.cs"), "unapproved");
        Assert.ThrowsExactly<InvalidDataException>(() => input.EnsureSources(source));
    }

    private Task<JourneyReport> Run(bool apply, bool useCopilotPlan = false) =>
        new JourneyRunner(platform).RunAsync(JourneyInput.Load(workspace.InputPath),
            workspace.Options with { Apply = apply, UseCopilotPlan = useCopilotPlan }, "fixture-cli",
            workspace.Output, CancellationToken.None);

    private sealed class JourneyWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "accessloop-journey-" + Guid.NewGuid().ToString("N"));
        public string App => Path.Combine(Root, "App");
        public string InputPath => Path.Combine(Root, "journey.json");
        public string Output => Path.Combine(Root, "output");
        public string InitialInputHash { get; }
        public JourneyOptions Options => new(InputPath, "fixture-winapp", "fixture-copilot", Output, true, false, false, null, null, true, false);

        public JourneyWorkspace()
        {
            Directory.CreateDirectory(App);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(App, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            foreach (var id in new[] { "SaveDocumentButton", "ExportDocumentButton" })
                File.WriteAllText(Path.Combine(App, id + ".xaml"),
                    $"<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button AutomationProperties.AutomationId=\"{id}\" /></Page>");
            AuditFiles.WriteNew(InputPath, new JourneyDefinition
            {
                Name = "Test journey", Project = "App\\App.csproj", NavigationRootId = "MainNavigation", InitialPageId = "home",
                Pages =
                [
                    new() { Id = "home", Title = "Home", NavigationId = "NavHome", ReadyId = "HomePageRoot", RequiredControlIds = ["HomePageRoot", "HomeHeading"] },
                    Page("documents", "Save", "Document", "Not saved yet", "Saved: AccessLoop"),
                    Page("export", "Export", "Export", "Not exported yet", "Exported: AccessLoop")
                ]
            });
            InitialInputHash = AuditFiles.Hash(InputPath);
        }

        private static JourneyPageDefinition Page(string id, string targetPrefix, string inputPrefix, string initial, string result)
        {
            var root = id == "documents" ? "DocumentsPageRoot" : "ExportPageRoot";
            var target = targetPrefix + "DocumentButton";
            var anchor = inputPrefix + "TitleInput";
            var resultId = targetPrefix + "Status";
            return new()
            {
                Id = id, Title = id, NavigationId = id == "documents" ? "NavDocuments" : "NavExport",
                ReadyId = root, RequiredControlIds = [root, target, anchor, resultId],
                Exercise = new(target, targetPrefix + " document", anchor, resultId, "AccessLoop", initial, result)
            };
        }

        public string? Name(string id) => (string?)XDocument.Load(Path.Combine(App, id + ".xaml"))
            .Descendants().Single(element => element.Name.LocalName == "Button").Attribute("AutomationProperties.Name");

        public void UpdateInput(Action<JsonObject> edit)
        {
            var json = JsonNode.Parse(File.ReadAllText(InputPath))!.AsObject();
            edit(json);
            File.WriteAllText(InputPath, json.ToJsonString(Json.Options));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class JourneyFake(JourneyWorkspace workspace) : IJourneyPlatform
    {
        private JourneyWorkspace Workspace => workspace;
        public int Launches { get; private set; }
        public int Disposals { get; private set; }
        public int Proposals { get; private set; }
        public int Plans { get; private set; }
        public int Visits { get; private set; }
        public string? PartialPage { get; set; }
        public string? PrivacyOnlyPage { get; set; }
        public bool TruncatedPage { get; set; }
        public string? ThrowOnPage { get; set; }
        public string? LoseControlAfterPatch { get; set; }
        public bool FailActivationAfterPatch { get; set; }
        public bool FailSecondLaunch { get; set; }
        public bool ChangeAcceptance { get; set; }
        public bool AddUnsupportedFinding { get; set; }
        public bool AddUnidentifiedFinding { get; set; }
        public JourneyPlan? Plan { get; set; }

        public Task<IJourneySession> LaunchAsync(JourneyDefinition definition, string evidencePath, CancellationToken token)
        {
            Launches++;
            if (Launches == 2 && FailSecondLaunch)
                throw new InvalidOperationException("Fixture build failed.");
            return Task.FromResult<IJourneySession>(new Session(this));
        }

        public Task<JourneyPlan> PlanAsync(JourneyDefinition definition, IReadOnlyList<JourneyCandidate> candidates,
            string evidencePath, CancellationToken token)
        {
            Plans++;
            return Task.FromResult(Plan!);
        }

        public Task<RepairProposal> ProposeAsync(SourceCandidate candidate, Scenario scenario, Observation observation,
            string evidencePath, CancellationToken token)
        {
            Proposals++;
            if (ChangeAcceptance)
                workspace.UpdateInput(json => json["name"] = "Changed after freeze");
            return Task.FromResult(new RepairProposal(candidate.RelativePath, candidate.Sha256, candidate.AutomationId,
                scenario.ExpectedName, "Provide the reviewed accessible name."));
        }

        private sealed class Session(JourneyFake owner) : IJourneySession
        {
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return ValueTask.CompletedTask;
            }

            public Task<IReadOnlyList<JourneyCandidate>> DiscoverAsync(JourneyDefinition input, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<JourneyCandidate>>(input.Pages
                    .Select(page => new JourneyCandidate(page.Id, page.NavigationId, page.Title, "Button")).ToArray());

            public Task<JourneyPageResult> VisitAsync(JourneyDefinition input, JourneyPageDefinition page,
                JourneyRoute route, string phase, string evidencePath, CancellationToken token)
            {
                owner.Visits++;
                if (owner.ThrowOnPage == page.Id)
                    throw new InvalidOperationException("Ready marker missing.");
                var findings = new List<AccessibilityFinding>();
                string? actualName = null;
                if (page.Exercise is { } target)
                {
                    actualName = owner.Workspace.Name(target.TargetId);
                    if (actualName is null)
                        findings.Add(ScanFixture.MissingName(target.TargetId));
                }
                if (owner.AddUnsupportedFinding)
                    findings.Add(ScanFixture.MissingName("SharedNav") with { RuleId = "UnsupportedFixtureRule" });
                if (owner.AddUnidentifiedFinding && page.Id == "home")
                    findings.Add(ScanFixture.MissingName("") with { RuleId = "UnidentifiedFixtureRule" });
                var scan = AccessibilityScan.Create(1234, 42, "2.4.2", [new(findings.Count, findings)]);
                Observation? exercise = null;
                if (page.Exercise is { } action)
                {
                    exercise = new()
                    {
                        Phase = phase, ProcessId = 1234, Scan = scan, Status = actualName is null ? "failed" : "passed",
                        Checks =
                        [
                            new("accessible-name", actualName == action.ExpectedName, action.ExpectedName, actualName),
                            new("target-enabled", true, "True", "True"), new("target-visible", true, "False", "False"),
                            new("keyboard-reachable", true, action.TargetId, action.TargetId),
                            new("keyboard-activation", !(owner.Launches > 1 && owner.FailActivationAfterPatch), action.ExpectedResult, action.ExpectedResult)
                        ],
                        VerifiedTargets = page.RequiredControlIds
                    };
                }
                var audit = new AuditReport
                {
                    RunId = Guid.NewGuid().ToString("N"), StartedAtUtc = DateTimeOffset.UtcNow, FinishedAtUtc = DateTimeOffset.UtcNow,
                    Status = owner.PartialPage == page.Id || owner.PrivacyOnlyPage == page.Id ? "partial" : "completed", Mode = "snapshot",
                    Target = new(1234, 42, "Fixture", DateTimeOffset.UtcNow.AddMinutes(-1), "Fixture", "[1]"),
                    WinappVersion = "fixture-cli", Axe = scan,
                    Snapshot = owner.PrivacyOnlyPage == page.Id ? JsonSerializer.SerializeToElement(new
                    {
                        status = "partial", depthLimitReached = owner.TruncatedPage, elementLimitReached = false,
                        issues = Array.Empty<object>(),
                        nodes = new[] { new { issues = new[] { new { code = "privacy_unavailable", message = "Withheld" } } } }
                    }) : null
                };
                var ids = page.RequiredControlIds.Concat(input.Pages.Select(item => item.NavigationId))
                    .Where(id => owner.Launches <= 1 || id != owner.LoseControlAfterPatch).ToArray();
                return Task.FromResult(new JourneyPageResult
                {
                    PageId = page.Id, Status = "passed", EvidencePath = evidencePath, Route = route, Audit = audit,
                    Exercise = exercise, ObservedControlIds = ids,
                    Checks = [new("page-ready", true, page.ReadyId, page.ReadyId)]
                });
            }
        }
    }
}
