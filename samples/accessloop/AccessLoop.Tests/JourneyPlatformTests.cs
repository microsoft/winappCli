using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class JourneyPlatformTests
{
    private string directory = null!;
    private static JourneyDefinition Definition => new()
    {
        Name = "approved fixture", Project = "JourneyDemo.csproj", NavigationRootId = "MainNavigation",
        InitialPageId = "home", TimeoutSeconds = 1, MaxTransitions = 20, MaxRepairs = 4,
        Pages =
        [
            new()
            {
                Id = "home", Title = "Home", NavigationId = "NavHome", ReadyId = "HomePageRoot",
                RequiredControlIds = ["HomePageRoot", "HomeHeading"]
            },
            new()
            {
                Id = "documents", Title = "Documents", NavigationId = "NavDocuments", ReadyId = "DocumentsPageRoot",
                RequiredControlIds = ["DocumentsPageRoot", "DocumentTitleInput", "SaveDocumentButton", "SaveStatus"],
                Exercise = new("SaveDocumentButton", "Save document", "DocumentTitleInput", "SaveStatus",
                    "AccessLoop", "Not saved yet", "Saved: AccessLoop", 1)
            },
            new()
            {
                Id = "export", Title = "Export", NavigationId = "NavExport", ReadyId = "ExportPageRoot",
                RequiredControlIds = ["ExportPageRoot", "ExportTitleInput", "ExportDocumentButton", "ExportStatus"],
                Exercise = new("ExportDocumentButton", "Export document", "ExportTitleInput", "ExportStatus",
                    "AccessLoop", "Not exported yet", "Exported: AccessLoop", 1)
            }
        ]
    };

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".journey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public async Task DiscoveryReturnsOnlyExactDeclaredDescendantsWithoutActivatingAnything()
    {
        var fake = new PlatformFake
        {
            EditTree = tree => WindowChildren(tree).Add(Element("OutsideButton", "Button", "NavDocuments preview"))
        };
        await using var session = Session(fake);
        var candidates = await session.DiscoverAsync(Definition, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "home", "documents", "export" }, candidates.Select(item => item.PageId).ToArray());
        CollectionAssert.AreEqual(new[] { "NavHome", "NavDocuments", "NavExport" },
            candidates.Select(item => item.NavigationId).ToArray());
        Assert.IsTrue(candidates.All(item => item.ControlType == "Button"));
        Assert.HasCount(0, fake.Invoked);
        Assert.HasCount(0, fake.Keys);
        Assert.AreEqual(1, fake.AttachCalls);
        Assert.AreEqual(fake.Commands.Count, fake.App.UiCalls);
    }

    [TestMethod]
    public async Task AnApprovedIdOutsideTheNavigationRootIsNotAUsableCandidate()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                var navigation = Find(tree, "MainNavigation")!["children"]!.AsArray();
                var item = Find(tree, "NavDocuments")!;
                navigation.Remove(item);
                WindowChildren(tree).Add(item);
            }
        };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "not a descendant");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    [DataRow("NavDocuments")]
    [DataRow("MainNavigation")]
    public async Task DuplicateNavigationIdentitiesAnywhereInTheWindowAreRejected(string id)
    {
        var fake = new PlatformFake
        {
            EditTree = tree => WindowChildren(tree).Add(Find(tree, id)!.DeepClone())
        };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "found 2");
    }

    [TestMethod]
    public async Task ANameContainingAnApprovedIdDoesNotAuthorizeNavigation()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                var node = Find(tree, "NavDocuments")!;
                node["automationId"] = "UnapprovedButton";
                node["name"] = "NavDocuments";
            }
        };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "Undeclared navigation");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task UnapprovedButtonsInsideNavigationFailRatherThanExpandingPermission()
    {
        var fake = new PlatformFake
        {
            EditTree = tree => Find(tree, "MainNavigation")!["children"]!.AsArray().Add(Element("DeleteAll", "Button"))
        };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "DeleteAll");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    [DataRow("Text", true)]
    [DataRow("Button", false)]
    public async Task AnIdAndTypeAloneAreNotProofOfSupportedNavigation(string type, bool invokable)
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                var node = Find(tree, "NavDocuments")!;
                node["type"] = type;
                node["isInvokable"] = invokable;
            }
        };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "invokable UIA pattern");
    }

    [TestMethod]
    public async Task AnInvokableToggleIsNotAnApprovedNavigationPattern()
    {
        var fake = new PlatformFake { EditTree = tree => Find(tree, "NavDocuments")!["toggleState"] = "off" };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "Invoke or SelectionItem only");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task ATruncatedTreeCannotProveGlobalNavigationUniqueness()
    {
        var fake = new PlatformFake { EditTree = tree => Find(tree, "MainNavigation")!["hasMoreChildren"] = true };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "truncated");
    }

    [TestMethod]
    public async Task ATruncatedSearchCannotProveLiveNavigationUniqueness()
    {
        var fake = new PlatformFake { TruncatedSearch = true };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "truncated");
    }

    [TestMethod]
    public async Task AdditionalOwnedWindowsAreRefusedBeforeAnyUiRequest()
    {
        var fake = new PlatformFake { Windows = [42, 99] };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "Additional windows");
        Assert.HasCount(0, fake.Commands);
    }

    [TestMethod]
    public async Task ForeignPidFromAttachCannotClaimTheUserProcess()
    {
        var fake = new PlatformFake { AttachedTarget = AuditFixture.Target with { ProcessId = 999 } };
        await using var session = Session(fake);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        Assert.HasCount(0, fake.Commands);
    }

    [TestMethod]
    public async Task AForeignWindowInTheInspectResponseIsRefused()
    {
        var fake = new PlatformFake { EditTree = tree => tree["windows"]![0]!["hwnd"] = 99 };
        await using var session = Session(fake);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task IdentityIsVerifiedAfterNavigationBeforeAnyFurtherRequests()
    {
        var fake = new PlatformFake { IdentityChangesOnInvoke = true };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "start time changed");
        Assert.AreEqual("invoke", fake.Commands.Last()[0]);
        Assert.HasCount(0, fake.Keys);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "home-reset-request.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "page-result.json")));
    }

    [TestMethod]
    public async Task NewWindowAfterNavigationStopsTheVisitWithoutRetryingTheAction()
    {
        var fake = new PlatformFake { AdditionalWindowOnInvoke = true };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Additional windows");
        Assert.HasCount(1, fake.Invoked);
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task MissingNameRetainsTheRealAxeFindingAndRunsOnlyTabThenEnter()
    {
        var fake = new PlatformFake();
        var result = await Visit(fake);
        Assert.AreEqual("failed", result.Status);
        Assert.AreEqual("page", result.EvidencePath);
        Assert.IsNotNull(result.Exercise);
        Assert.AreSame(result.Audit!.Axe, result.Exercise.Scan);
        Assert.IsTrue(ScenarioRunner.CanRepair(result.Exercise, "SaveDocumentButton"));
        CollectionAssert.AreEqual(
            new[] { "accessible-name", "target-enabled", "target-visible", "keyboard-reachable", "keyboard-activation" },
            result.Exercise.Checks.Select(check => check.RuleId).ToArray());
        Assert.IsFalse(result.Exercise.Checks[0].Passed);
        Assert.IsTrue(result.Exercise.Checks.Skip(1).All(check => check.Passed));
        CollectionAssert.AreEqual(new[] { "home", "tab", "enter" }, fake.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "DocumentTitleInput" }, fake.DirectFocus.ToArray());
        CollectionAssert.AreEqual(new[] { "NavHome", "NavDocuments" }, fake.Invoked.ToArray());
        CollectionAssert.AreEqual(new[] { "DocumentTitleInput", "SaveDocumentButton" },
            result.Exercise.FocusTrace.Select(item => item.AutomationId).ToArray());
        Assert.IsTrue(result.Checks.All(check => check.Passed));
        Assert.AreEqual("HomePageRoot", result.Checks.Single(check => check.Id == "initial-page-ready").Actual);
        Assert.AreEqual(1, fake.AxeCalls);
        CollectionAssert.AreEquivalent(Definition.Pages[1].RequiredControlIds.ToArray(), result.ObservedControlIds.ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "scan.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "report.html")));
        Assert.IsTrue(Directory.GetFiles(Path.Combine(directory, "page"), "*-inspect.json").Length >= 3);
        using var navigation = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "page", "navigation-result.json")));
        Assert.AreEqual(JsonValueKind.String, navigation.RootElement.GetProperty("invokedAtUtc").ValueKind);
        Assert.AreEqual(JsonValueKind.String, navigation.RootElement.GetProperty("readyAtUtc").ValueKind);
    }

    [TestMethod]
    public async Task NamedButtonPassesTheSameFiveChecksUsingRuntimeSelectorsFromProperties()
    {
        var fake = new PlatformFake { ButtonName = "Save document" };
        var result = await Visit(fake);
        Assert.AreEqual("passed", result.Status);
        Assert.IsTrue(result.Exercise!.Checks.All(check => check.Passed));
        Assert.HasCount(4, result.Exercise.VerifiedTargets);
        Assert.AreEqual("before", result.Exercise.Phase);
        Assert.AreEqual(1234, result.Exercise.ProcessId);
        CollectionAssert.AreEqual(new[] { "runtime-NavHome", "runtime-NavDocuments" },
            fake.Commands.Where(command => command[0] == "invoke").Select(command => command[1]).ToArray());
        Assert.AreEqual("runtime-DocumentTitleInput", fake.Commands.Single(command => command[0] == "set-value")[1]);
        Assert.IsFalse(fake.Commands.Any(command => command[0] is "focus" or "click" or "screenshot"));
    }

    [TestMethod]
    public async Task HomeHasAnAuditButNoFabricatedExercise()
    {
        var fake = new PlatformFake();
        var result = await Visit(fake, "home");
        Assert.AreEqual("passed", result.Status);
        Assert.IsNull(result.Exercise);
        Assert.IsNotNull(result.Audit);
        CollectionAssert.AreEqual(new[] { "NavHome" }, fake.Invoked.ToArray());
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task TheExactReadyMarkerMustAppearEvenIfOtherUiChanges()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (Find(tree, "DocumentsPageRoot") is { } page)
                    page["automationId"] = "AChangedButUnapprovedPage";
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "DocumentsPageRoot");
        Assert.IsFalse(result.Checks.Single(check => check.Id == "page-ready").Passed);
        Assert.IsNull(result.Audit);
        Assert.HasCount(0, fake.Keys);
        Assert.IsTrue(Directory.GetFiles(Path.Combine(directory, "page"), "*-inspect.json").Length >= 2);
    }

    [TestMethod]
    public async Task DuplicateReadyMarkersAreInconclusive()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (Find(tree, "DocumentsPageRoot") is { } page)
                    WindowChildren(tree).Add(page.DeepClone());
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Page-ready AutomationId");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task TwoActiveDeclaredPagesCannotCountAsReady()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (Find(tree, "DocumentsPageRoot") is not null)
                    WindowChildren(tree).Add(Element("HomePageRoot", "Pane"));
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "exactly one active");
    }

    [TestMethod]
    public async Task NavigationToADifferentDeclaredPageIsNotTheRequestedVisit()
    {
        var fake = new PlatformFake { RedirectToPage = "export" };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "unexpected page 'export'");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task NewSessionsMustStartAtTheDeclaredHomePage()
    {
        var fake = new PlatformFake { ActivePage = "documents" };
        await using var session = Session(fake);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
        StringAssert.Contains(error.Message, "Expected active page 'home'");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    [DataRow("export")]
    [DataRow("home")]
    public async Task FrozenRoutesAlwaysDeclareHomeAsTheirOrigin(string pageId)
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        var page = Definition.Pages.Single(item => item.Id == pageId);
        var result = await session.VisitAsync(Definition, page,
            new(page.Id, "documents", page.NavigationId, page.ReadyId), "before", "wrong-route", CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "must start at 'home'");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task EveryVisitResetsHomeWithinTheTwoTransitionsPerPageBudget()
    {
        var fake = new PlatformFake { ButtonName = "Save document" };
        await using var session = Session(fake);
        var documents = await Visit(session, "documents", "documents");
        var export = await Visit(session, "export", "export");
        var home = await Visit(session, "home", "home");
        Assert.IsNull(documents.Error);
        Assert.IsNull(export.Error);
        Assert.IsNull(home.Error);
        Assert.AreEqual("home", documents.Route.FromPageId);
        Assert.AreEqual("home", export.Route.FromPageId);
        Assert.AreEqual("home", home.Route.FromPageId);
        Assert.AreEqual(1, fake.AttachCalls);
        CollectionAssert.AreEqual(new[] { "NavHome", "NavDocuments", "NavHome", "NavExport", "NavHome" }, fake.Invoked.ToArray());
        Assert.IsTrue(fake.Invoked.Count <= 2 * Definition.Pages.Count);
        Assert.AreEqual(Definition.Pages.Count, fake.AxeCalls);
        Assert.IsTrue(fake.Verifications > fake.Commands.Count * 2);
        using var reset = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "export", "home-reset-result.json")));
        Assert.AreEqual("documents", reset.RootElement.GetProperty("sourcePageId").GetString());
        Assert.AreEqual("ready", reset.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.String, reset.RootElement.GetProperty("readyAtUtc").ValueKind);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "home", "home-reset-request.json")));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("incorrect")]
    public async Task FailedHomeReadinessPreventsTheRequestedPageInvocation(string state)
    {
        var resetInvoked = false;
        var fake = new PlatformFake
        {
            OnInvoke = id => resetInvoked |= id == "NavHome",
            EditTree = tree =>
            {
                if (!resetInvoked)
                    return;
                var home = Find(tree, "HomePageRoot")!;
                if (state == "duplicate")
                    WindowChildren(tree).Add(home.DeepClone());
                else
                    home["automationId"] = state == "missing" ? "UnapprovedReadyMarker" : "ExportPageRoot";
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "initial-page-ready").Passed);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "page-ready").Passed);
        CollectionAssert.AreEqual(new[] { "NavHome" }, fake.Invoked.ToArray());
        Assert.HasCount(0, fake.Keys);
        Assert.AreEqual(0, fake.AxeCalls);
        Assert.IsNull(result.Audit);
        Assert.AreEqual(Route(Definition.Pages[1]), result.Route);
        using var reset = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "page", "home-reset-result.json")));
        Assert.AreEqual("inconclusive", reset.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(File.Exists(Path.Combine(directory, "page", "navigation-request.json")));
    }

    [TestMethod]
    public async Task HomeResetRechecksItsNavigationScopeAfterDiscovery()
    {
        var moved = false;
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (!moved)
                    return;
                var home = Find(tree, "NavHome")!;
                home.Parent!.AsArray().Remove(home);
                WindowChildren(tree).Add(home);
            }
        };
        await using var session = Session(fake);
        await session.DiscoverAsync(Definition, CancellationToken.None);
        moved = true;
        var result = await Visit(session, "documents", "moved-home");
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Navigation 'NavHome' is not a descendant");
        Assert.HasCount(0, fake.Invoked);
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task RequestedNavigationIsRecheckedUnderTheRootAfterHomeReset()
    {
        var resetInvoked = false;
        var fake = new PlatformFake
        {
            OnInvoke = id => resetInvoked |= id == "NavHome",
            EditTree = tree =>
            {
                if (!resetInvoked)
                    return;
                var documents = Find(tree, "NavDocuments")!;
                documents.Parent!.AsArray().Remove(documents);
                WindowChildren(tree).Add(documents);
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Navigation 'NavDocuments' is not a descendant");
        Assert.IsTrue(result.Checks.Single(check => check.Id == "initial-page-ready").Passed);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "page-ready").Passed);
        CollectionAssert.AreEqual(new[] { "NavHome" }, fake.Invoked.ToArray());
        Assert.HasCount(0, fake.Keys);
        Assert.AreEqual(0, fake.AxeCalls);
    }

    [TestMethod]
    [DataRow("navigation")]
    [DataRow("ready")]
    [DataRow("exercise")]
    public async Task ACallerCannotPassAnotherAllowedIdToBypassTheApprovedPage(string changed)
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        var declared = Definition.Pages[1];
        var page = changed switch
        {
            "navigation" => declared with { NavigationId = "NavExport" },
            "ready" => declared with { ReadyId = "ExportPageRoot" },
            _ => declared with { Exercise = declared.Exercise! with { TargetId = "ExportDocumentButton" } }
        };
        var result = await session.VisitAsync(Definition, page, Route(page), "before", "altered", CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "approved page definition");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task ARouteCannotSubstituteAnUnrelatedReadyMarker()
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        var page = Definition.Pages[1];
        var result = await session.VisitAsync(Definition, page, Route(page) with { ReadyId = "HomePageRoot" },
            "before", "altered-route", CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task ApprovedDefinitionCannotChangeAfterDiscovery()
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        await session.DiscoverAsync(Definition, CancellationToken.None);
        var changed = Definition with { NavigationRootId = "OtherNavigation" };
        var result = await session.VisitAsync(changed, changed.Pages[1], Route(changed.Pages[1]),
            "before", "changed-contract", CancellationToken.None);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "definition changed");
        Assert.HasCount(0, fake.Invoked);
    }

    [TestMethod]
    public async Task AnUnreachableButtonIsNeverInvokedOrDirectlyFocused()
    {
        var fake = new PlatformFake { Reachable = false };
        var result = await Visit(fake);
        Assert.AreEqual("failed", result.Status);
        Assert.IsFalse(result.Exercise!.Checks.Single(check => check.RuleId == "keyboard-reachable").Passed);
        Assert.IsFalse(result.Exercise.Checks.Single(check => check.RuleId == "keyboard-activation").Passed);
        Assert.IsFalse(ScenarioRunner.CanRepair(result.Exercise, "SaveDocumentButton"));
        CollectionAssert.AreEqual(new[] { "home", "tab" }, fake.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "DocumentTitleInput" }, fake.DirectFocus.ToArray());
        CollectionAssert.AreEqual(new[] { "NavHome", "NavDocuments" }, fake.Invoked.ToArray());
    }

    [TestMethod]
    public async Task TheFocusedIdAloneCannotReplaceLiveFocusConfirmation()
    {
        var fake = new PlatformFake { Reachable = false, ReportTargetFocusAfterTab = true };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Actual keyboard focus");
        CollectionAssert.AreEqual(new[] { "home", "tab" }, fake.Keys.ToArray());
        Assert.HasCount(0, result.Exercise!.VerifiedTargets);
    }

    [TestMethod]
    public async Task APreexistingSuccessfulResultRejectsTheExerciseBeforeInput()
    {
        var fake = new PlatformFake { InitialResult = "Saved: AccessLoop" };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "already present");
        Assert.HasCount(5, result.Exercise!.Checks);
        Assert.HasCount(0, fake.Keys);
        Assert.IsFalse(fake.Commands.Any(command => command[0] == "set-value"));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "scan.json")));
    }

    [TestMethod]
    public async Task AnUnexpectedInitialResultCannotMasqueradeAsCleanState()
    {
        var fake = new PlatformFake { InitialResult = "A previous attempt failed" };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "Expected initial result");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task AResultProducedByEditingTheInputIsNotKeyboardActivation()
    {
        var fake = new PlatformFake { ActivateOnSetValue = true };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "before Enter");
        CollectionAssert.AreEqual(new[] { "home", "tab" }, fake.Keys.ToArray());
        Assert.IsFalse(result.Exercise!.Checks.Single(check => check.RuleId == "keyboard-activation").Passed);
    }

    [TestMethod]
    public async Task EnterDeliveryWithoutTheExpectedResultFailsTheBehaviorGuard()
    {
        var fake = new PlatformFake { Activates = false, ButtonName = "Save document" };
        var result = await Visit(fake);
        Assert.AreEqual("failed", result.Status);
        Assert.IsFalse(result.Exercise!.Checks.Single(check => check.RuleId == "keyboard-activation").Passed);
        Assert.AreEqual("Not saved yet", result.Exercise.Checks.Last().Actual);
    }

    [TestMethod]
    public async Task MissingRequiredTargetCannotPassOrAuthorizeInput()
    {
        var fake = new PlatformFake
        {
            EditTree = tree => Remove(tree, "SaveDocumentButton")
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "SaveDocumentButton: found 0");
        Assert.IsFalse(result.ObservedControlIds.Contains("SaveDocumentButton"));
        Assert.IsFalse(result.Checks.Single(check => check.Id == "required-controls-before").Passed);
        Assert.HasCount(0, fake.Keys);
        Assert.IsNull(result.Audit);
    }

    [TestMethod]
    public async Task ARequiredIdOutsideThePageIsNotPageCoverage()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (Find(tree, "SaveDocumentButton") is { } button)
                {
                    Remove(tree, "SaveDocumentButton");
                    WindowChildren(tree).Add(button);
                }
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsFalse(result.ObservedControlIds.Contains("SaveDocumentButton"));
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task DuplicateRequiredIdsAreExplicitlyInconclusive()
    {
        var fake = new PlatformFake
        {
            EditTree = tree =>
            {
                if (Find(tree, "SaveDocumentButton") is { } button)
                    Find(tree, "DocumentsPageRoot")!["children"]!.AsArray().Add(button.DeepClone());
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "SaveDocumentButton: found 2");
        Assert.HasCount(0, fake.Keys);
    }

    [TestMethod]
    public async Task DisappearingTargetAfterEnterCannotCountAsARepair()
    {
        var fake = new PlatformFake { LoseTargetAfterEnter = true, ButtonName = "Save document" };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "SaveDocumentButton");
        Assert.HasCount(0, result.Exercise!.VerifiedTargets);
        Assert.IsFalse(result.ObservedControlIds.Contains("SaveDocumentButton"));
        Assert.IsFalse(result.Checks.Single(check => check.Id == "required-controls-after").Passed);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "exercise.json")));
    }

    [TestMethod]
    public async Task UnrecognizedPartialCaptureIsPreservedButCannotAuthorizeTheExercise()
    {
        var fake = new PlatformFake { PartialSnapshot = true, ButtonName = "Save document" };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.AreEqual("partial", result.Audit!.Status);
        Assert.AreEqual("inconclusive", result.Audit.Results.Single(check => check.CheckId == "capture.tree").Status);
        StringAssert.Contains(result.Audit.Snapshot!.Value.GetRawText(), "privacy_unknown");
        Assert.AreEqual("inconclusive", result.Exercise!.Status);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "required-audit-evidence").Passed);
        Assert.HasCount(0, result.Exercise.VerifiedTargets);
        Assert.HasCount(0, fake.Keys);
        using var scan = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "page", "scan.json")));
        Assert.AreEqual("partial", scan.RootElement.GetProperty("status").GetString());
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "page", "snapshot.json")), "privacy_unknown");
        Assert.IsFalse(fake.Commands.Any(command => command[0] is "screenshot" or "audit-screenshot"));
    }

    [TestMethod]
    [DataRow("documents", "", "failed")]
    [DataRow("documents", "Save document", "passed")]
    [DataRow("export", "", "failed")]
    [DataRow("export", "Export document", "passed")]
    public async Task InputSitePrivacyWithholdingPreservesEvidenceAndStillExecutesTheFixedGuards(
        string pageId, string buttonName, string exerciseStatus)
    {
        var fake = new PlatformFake
        {
            PartialSnapshot = true, WithheldPrivacyAncestor = true, ButtonName = buttonName
        };
        var result = await Visit(fake, pageId);
        var page = Definition.Pages.Single(item => item.Id == pageId);
        Assert.IsNull(result.Error);
        Assert.AreEqual(exerciseStatus, result.Status);
        Assert.AreEqual("partial", result.Audit!.Status);
        Assert.IsNull(result.Audit.Error);
        Assert.IsTrue(result.Checks.All(check => check.Passed));
        Assert.AreEqual(exerciseStatus, result.Exercise!.Status);
        Assert.HasCount(5, result.Exercise.Checks);
        Assert.IsTrue(result.Exercise.Checks.Skip(1).All(check => check.Passed));
        Assert.AreSame(result.Audit.Axe, result.Exercise.Scan);
        Assert.AreEqual(buttonName.Length == 0 ? 1 : 0, result.Exercise.Scan!.Findings.Count);
        Assert.AreEqual(buttonName.Length == 0, ScenarioRunner.CanRepair(result.Exercise, page.Exercise!.TargetId));
        CollectionAssert.AreEquivalent(page.RequiredControlIds.ToArray(), result.Exercise.VerifiedTargets.ToArray());
        CollectionAssert.AreEqual(new[] { "home", "tab", "enter" }, fake.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { page.Exercise.AnchorId }, fake.DirectFocus.ToArray());

        var snapshot = result.Audit.Snapshot!.Value;
        Assert.IsTrue(JsonElement.DeepEquals(fake.Snapshots.Single(), snapshot));
        Assert.IsFalse(snapshot.GetProperty("depthLimitReached").GetBoolean());
        Assert.IsFalse(snapshot.GetProperty("elementLimitReached").GetBoolean());
        var nodes = snapshot.GetProperty("nodes").EnumerateArray().ToArray();
        var site = nodes.Single(node => node.GetProperty("id").GetString() == "n3");
        Assert.AreEqual("InputSiteWindowClass", site.GetProperty("className").GetString());
        Assert.IsFalse(site.TryGetProperty("isPassword", out _));
        Assert.IsTrue(site.GetProperty("issues").EnumerateArray()
            .Any(issue => issue.GetProperty("code").GetString() == "privacy_unavailable"));
        var target = nodes.Single(node => node.GetProperty("automationId").GetString() == page.Exercise.TargetId);
        Assert.IsFalse(target.TryGetProperty("name", out _));
        Assert.IsTrue(target.GetProperty("issues").EnumerateArray()
            .Any(issue => issue.GetProperty("code").GetString() == "privacy_withheld"));
        using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "page", "snapshot.json")));
        Assert.IsTrue(JsonElement.DeepEquals(snapshot, recorded.RootElement));
        Assert.IsFalse(fake.Commands.Any(command => command[0] is "screenshot" or "audit-screenshot"));
    }

    [TestMethod]
    public async Task PrivacyWithholdingDoesNotSkipAnyOfTheThreePageScans()
    {
        var fake = new PlatformFake { PartialSnapshot = true, WithheldPrivacyAncestor = true };
        await using var session = Session(fake);
        var pages = new List<JourneyPageResult>();
        foreach (var page in Definition.Pages)
            pages.Add(await Visit(session, page.Id, page.Id));
        CollectionAssert.AreEqual(new[] { 0, 1, 1 }, pages.Select(page => page.Audit!.Axe!.Findings.Count).ToArray());
        Assert.AreEqual(3, fake.AxeCalls);
        Assert.IsTrue(pages.All(page => page.Error is null && page.Audit!.Status == "partial"));
        CollectionAssert.AreEqual(new[] { "passed", "failed", "failed" }, pages.Select(page => page.Status).ToArray());
        Assert.IsTrue(pages.All(page => page.Checks.All(check => check.Passed)));
        Assert.IsNull(pages[0].Exercise);
        Assert.IsTrue(pages.Skip(1).All(page => page.Exercise!.Checks.Count == 5
            && page.Exercise.Checks.Skip(1).All(check => check.Passed)));
    }

    [TestMethod]
    [DataRow("truncation")]
    [DataRow("read-error")]
    [DataRow("missing-root")]
    [DataRow("timeout")]
    public async Task PrivacyWithholdingCannotHideMissingRequiredCaptureEvidence(string failure)
    {
        var fake = new PlatformFake
        {
            PartialSnapshot = true, WithheldPrivacyAncestor = true,
            EditSnapshot = snapshot =>
            {
                switch (failure)
                {
                    case "truncation":
                        snapshot["depthLimitReached"] = true;
                        break;
                    case "read-error":
                        snapshot["issues"]!.AsArray().Add(new JsonObject
                        {
                            ["code"] = "node_read_error", ["message"] = "A provider property read failed."
                        });
                        break;
                    case "missing-root":
                        snapshot["nodes"] = new JsonArray();
                        break;
                    case "timeout":
                        throw new TimeoutException("Snapshot deadline exceeded.");
                    default:
                        Assert.Fail("Unknown capture failure.");
                        break;
                }
            }
        };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        StringAssert.Contains(result.Error!, "required audit evidence");
        Assert.AreEqual("partial", result.Audit!.Status);
        Assert.IsNotNull(result.Audit.Axe);
        Assert.IsFalse(JourneyPolicy.HasRequiredAuditEvidence(result.Audit));
        Assert.IsFalse(result.Checks.Single(check => check.Id == "required-audit-evidence").Passed);
        Assert.AreEqual("inconclusive", result.Exercise!.Status);
        Assert.HasCount(5, result.Exercise.Checks);
        Assert.HasCount(0, result.Exercise.VerifiedTargets);
        Assert.HasCount(0, fake.Keys);
        Assert.IsFalse(ScenarioRunner.CanRepair(result.Exercise, "SaveDocumentButton"));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "page", "scan.json")));
    }

    [TestMethod]
    public async Task FailedPassiveCaptureRemainsPartialAndNeverUsesRawScreenshotFallback()
    {
        var fake = new PlatformFake { ScreenshotFails = true };
        await using var session = Session(fake, screenshot: true);
        var result = await Visit(session, "documents", "capture-failed");
        Assert.AreEqual("inconclusive", result.Status);
        Assert.AreEqual("partial", result.Audit!.Status);
        Assert.AreEqual("blocked", result.Audit.Results.Single(check => check.CheckId == "capture.screenshot").Status);
        Assert.HasCount(1, fake.Commands.Where(command => command[0] == "audit-screenshot").ToArray());
        Assert.HasCount(0, fake.Keys);
        Assert.AreEqual("inconclusive", result.Exercise!.Status);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "required-audit-evidence").Passed);
        Assert.IsFalse(fake.Commands.Any(command => command[0] == "screenshot"));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "capture-failed", "scan.json")));
    }

    [TestMethod]
    public async Task MissingAxeCoverageIsNotAFabricatedPassingExercise()
    {
        var fake = new PlatformFake { AxeFails = true };
        var result = await Visit(fake);
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsNull(result.Exercise!.Scan);
        Assert.HasCount(5, result.Exercise.Checks);
        Assert.HasCount(0, result.Exercise.VerifiedTargets);
        Assert.HasCount(0, fake.Keys);
        Assert.IsNotNull(result.Audit);
    }

    [TestMethod]
    public async Task EvidenceMustBeRunRelativeAndMayNotOverwriteAnExistingVisit()
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        var page = Definition.Pages[0];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            session.VisitAsync(Definition, page, Route(page), "before", Path.Combine(directory, "absolute"), CancellationToken.None));
        await Visit(session, "home", "once");
        await Assert.ThrowsExactlyAsync<IOException>(() => Visit(session, "home", "once"));
        Assert.HasCount(1, fake.Invoked);
    }

    [TestMethod]
    public async Task RelativeEvidenceCreatesItsMissingPhaseDirectory()
    {
        var fake = new PlatformFake();
        await using var session = Session(fake);
        var result = await Visit(session, "home", Path.Combine("phase", "home"));
        Assert.AreEqual("passed", result.Status);
        Assert.AreEqual(Path.Combine("phase", "home"), result.EvidencePath);
        Assert.IsTrue(File.Exists(Path.Combine(directory, result.EvidencePath, "scan.json")));
        CollectionAssert.AreEqual(new[] { "NavHome" }, fake.Invoked.ToArray());
    }

    [TestMethod]
    public async Task DisposingTheAdapterDisposesOnlyItsOwnedSessionOnce()
    {
        var fake = new PlatformFake();
        var session = Session(fake);
        await session.DiscoverAsync(Definition, CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.AreEqual(1, fake.App.DisposeCalls);
        Assert.HasCount(0, fake.Invoked);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            session.DiscoverAsync(Definition, CancellationToken.None));
    }

    private JourneyPageSession Session(PlatformFake fake, bool screenshot = false) =>
        new(fake.App, fake, "fixture-cli", directory, screenshot, _ => fake.Windows);

    private async Task<JourneyPageResult> Visit(PlatformFake fake, string id = "documents")
    {
        await using var session = Session(fake);
        return await Visit(session, id, "page");
    }

    private static Task<JourneyPageResult> Visit(JourneyPageSession session, string id, string evidencePath)
    {
        var definition = Definition;
        var page = definition.Pages.Single(item => item.Id == id);
        return session.VisitAsync(definition, page, Route(page), "before", evidencePath, CancellationToken.None);
    }

    private static JourneyRoute Route(JourneyPageDefinition page) => new(page.Id, "home", page.NavigationId, page.ReadyId);

    private static JsonArray WindowChildren(JsonObject tree) =>
        tree["windows"]![0]!["elements"]![0]!["children"]!.AsArray();

    private static IEnumerable<JsonObject> Nodes(JsonObject tree)
    {
        IEnumerable<JsonObject> Descendants(JsonObject node)
        {
            yield return node;
            if (node["children"] is JsonArray children)
                foreach (var child in children.OfType<JsonObject>().SelectMany(Descendants))
                    yield return child;
        }
        return tree["windows"]![0]!["elements"]!.AsArray().OfType<JsonObject>().SelectMany(Descendants);
    }

    private static JsonObject? Find(JsonObject tree, string id) =>
        Nodes(tree).FirstOrDefault(node => node["automationId"]?.GetValue<string>() == id);

    private static void Remove(JsonObject tree, string id)
    {
        var node = Find(tree, id);
        if (node?.Parent is JsonArray parent)
            parent.Remove(node);
    }

    private static JsonObject Element(string id, string type, string? name = null, params JsonObject[] children) => new()
    {
        ["automationId"] = id, ["selector"] = id, ["type"] = type, ["name"] = name ?? id,
        ["isEnabled"] = true, ["isOffscreen"] = false, ["isInvokable"] = type is "Button" or "TabItem" or "ListItem",
        ["children"] = new JsonArray(children.Cast<JsonNode?>().ToArray())
    };

    private sealed class OwnedApp(PlatformFake transport) : IAppSession
    {
        public int ProcessId => 1234;
        public int DisposeCalls { get; private set; }
        public int UiCalls { get; private set; }
        public async Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Assert.IsTrue(arguments[0] is "inspect" or "search" or "get-property");
            UiCalls++;
            var response = await transport.UiAsync(transport.AttachedTarget, arguments, TimeSpan.FromSeconds(1), cancellationToken);
            Assert.AreEqual(0, response.ExitCode);
            using var document = JsonDocument.Parse(response.Stdout);
            return document.RootElement.Clone();
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlatformFake : IAuditPlatform
    {
        public OwnedApp App { get; }
        public AuditTarget AttachedTarget { get; init; } = AuditFixture.Target;
        public IReadOnlyList<long> Windows { get; set; } = [42];
        public Action<JsonObject>? EditTree { get; init; }
        public Action<JsonObject>? EditSnapshot { get; init; }
        public Action<string>? OnInvoke { get; init; }
        public string ActivePage { get; set; } = "home";
        public string? RedirectToPage { get; init; }
        public string ButtonName { get; init; } = "";
        public string? InitialResult { get; init; }
        public bool TruncatedSearch { get; init; }
        public bool IdentityChangesOnInvoke { get; init; }
        public bool AdditionalWindowOnInvoke { get; init; }
        public bool Reachable { get; init; } = true;
        public bool Activates { get; init; } = true;
        public bool ActivateOnSetValue { get; init; }
        public bool ReportTargetFocusAfterTab { get; init; }
        public bool LoseTargetAfterEnter { get; init; }
        public bool PartialSnapshot { get; init; }
        public bool WithheldPrivacyAncestor { get; init; }
        public bool ScreenshotFails { get; init; }
        public bool AxeFails { get; init; }
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<string> Invoked { get; } = [];
        public List<string> Keys { get; } = [];
        public List<string> DirectFocus { get; } = [];
        public List<JsonElement> Snapshots { get; } = [];
        public int AttachCalls { get; private set; }
        public int Verifications { get; private set; }
        public int AxeCalls { get; private set; }
        private string? focused;
        private string input = "Untitled";
        private bool activated;
        private bool entered;

        public PlatformFake() => App = new(this);

        private JourneyPageDefinition Page => Definition.Pages.Single(page => page.Id == ActivePage);

        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("fixture-cli");

        public Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token)
        {
            AttachCalls++;
            Assert.AreEqual(App.ProcessId, options.ProcessId);
            Assert.IsNull(options.WindowHandle);
            return Task.FromResult(AttachedTarget);
        }

        public void VerifyTarget(AuditTarget target)
        {
            Verifications++;
            if (IdentityChangesOnInvoke && Invoked.Count > 0)
                throw new InvalidOperationException("The owned process start time changed.");
            Assert.AreEqual(AttachedTarget.ProcessId, target.ProcessId);
            Assert.AreEqual(AttachedTarget.WindowHandle, target.WindowHandle);
            Assert.AreEqual(AttachedTarget.ProcessStartedAtUtc, target.ProcessStartedAtUtc);
        }

        public Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
        {
            VerifyTarget(target);
            AxeCalls++;
            if (AxeFails)
                throw new InvalidOperationException("Axe coverage was unavailable.");
            var missingName = Page.Exercise is not null && ButtonName.Length == 0;
            return Task.FromResult(missingName
                ? ScanFixture.Create(ScanFixture.MissingName(Page.Exercise!.TargetId))
                : ScanFixture.Create());
        }

        public Task<CommandResult> UiAsync(
            AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(42L, target.WindowHandle);
            Assert.AreEqual(App.ProcessId, target.ProcessId);
            Commands.Add(arguments.ToArray());
            object output;
            switch (arguments[0])
            {
                case "inspect":
                    Assert.AreEqual("--depth", arguments[1]);
                    output = Tree();
                    break;
                case "search":
                    output = new
                    {
                        hasMore = TruncatedSearch,
                        matches = Nodes(Tree()).Where(node => node["automationId"]!.GetValue<string>() == arguments[1])
                            .Select(node =>
                            {
                                var match = (JsonObject)node.DeepClone();
                                match["windowHandle"] = 42;
                                return match;
                            }).ToArray()
                    };
                    break;
                case "get-property":
                {
                    var id = Unwrap(arguments[1]);
                    var node = Find(Tree(), id) ?? throw new InvalidOperationException("A fake live property target disappeared.");
                    output = new
                    {
                        elementId = "runtime-" + id,
                        properties = new Dictionary<string, string?>
                        {
                            ["AutomationId"] = id, ["ControlType"] = node["type"]!.GetValue<string>(),
                            ["Name"] = node["name"]?.GetValue<string>(),
                            ["IsEnabled"] = node["isEnabled"]!.GetValue<bool>().ToString(),
                            ["IsOffscreen"] = node["isOffscreen"]!.GetValue<bool>().ToString(),
                            ["HasKeyboardFocus"] = (focused == id).ToString(),
                            ["Value"] = id == Page.Exercise?.AnchorId ? input : null
                        }
                    };
                    break;
                }
                case "invoke":
                {
                    var id = Unwrap(arguments[1]);
                    var destination = Definition.Pages.SingleOrDefault(page => page.NavigationId == id)
                        ?? throw new InvalidOperationException("Invoking an exercise target or arbitrary element is forbidden.");
                    Invoked.Add(id);
                    ActivePage = destination.Id == Definition.InitialPageId ? destination.Id : RedirectToPage ?? destination.Id;
                    input = "Untitled";
                    activated = false;
                    entered = false;
                    focused = null;
                    if (AdditionalWindowOnInvoke)
                        Windows = [42, 99];
                    OnInvoke?.Invoke(id);
                    output = new { hwnd = 42, elementId = arguments[1], pattern = "InvokePattern" };
                    break;
                }
                case "set-value":
                    Assert.AreEqual(Page.Exercise!.AnchorId, Unwrap(arguments[1]));
                    input = arguments[2];
                    activated |= ActivateOnSetValue;
                    output = new { };
                    break;
                case "send-keys":
                    Assert.AreEqual("--via", arguments[2]);
                    Assert.AreEqual("send-input", arguments[3]);
                    Keys.Add(arguments[1]);
                    if (arguments[1] == "home")
                    {
                        Assert.AreEqual("--target", arguments[4]);
                        Assert.AreEqual(Page.Exercise!.AnchorId, Unwrap(arguments[5]));
                        focused = Unwrap(arguments[5]);
                        DirectFocus.Add(focused);
                    }
                    else
                    {
                        Assert.AreEqual(4, arguments.Count);
                        if (arguments[1] == "tab")
                            focused = Reachable ? Page.Exercise!.TargetId : Page.Exercise!.AnchorId;
                        else if (arguments[1] == "enter")
                        {
                            Assert.AreEqual(Page.Exercise!.TargetId, focused);
                            activated |= Activates;
                            entered = true;
                        }
                        else
                            Assert.Fail("Unexpected input key.");
                    }
                    output = new { };
                    break;
                case "get-focused":
                {
                    var id = ReportTargetFocusAfterTab && Keys.LastOrDefault() == "tab" ? Page.Exercise!.TargetId : focused;
                    output = new
                    {
                        hasFocus = id is not null,
                        element = new { automationId = id, selector = "runtime-" + id, name = id }
                    };
                    break;
                }
                case "audit-snapshot":
                {
                    var snapshot = Snapshot();
                    Snapshots.Add(snapshot);
                    return Task.FromResult(new CommandResult(PartialSnapshot ? 1 : 0, snapshot.GetRawText(), ""));
                }
                case "audit-screenshot":
                    if (ScreenshotFails)
                        throw new InvalidOperationException("Passive exact-window capture is unavailable.");
                    var path = arguments[2];
                    File.WriteAllBytes(path, Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0f0AAAAASUVORK5CYII="));
                    output = new
                    {
                        schemaVersion = 1, processId = 1234, windowHandle = 42, status = "captured",
                        captureMethod = "windows-graphics-capture", activationRequested = false,
                        filePath = path, width = 1, height = 1
                    };
                    break;
                default:
                    throw new InvalidOperationException("Unapproved test command: " + arguments[0]);
            }
            return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(output, Json.Options), ""));
        }

        private JsonObject Tree()
        {
            var navigation = Element("MainNavigation", "Pane", null,
                Element("NavHome", "Button", "Home"), Element("NavDocuments", "Button", "Documents"),
                Element("NavExport", "Button", "Export"));
            var children = Page.Exercise is { } guard
                ? new[]
                {
                    Element(guard.AnchorId, "Edit", "Title"), Element(guard.TargetId, "Button", ButtonName),
                    Element(guard.ResultId, "Text", activated ? guard.ExpectedResult : InitialResult ?? guard.InitialResult)
                }
                : [Element("HomeHeading", "Text", "Welcome")];
            var page = Element(Page.ReadyId, "Pane", Page.Title, children);
            var tree = new JsonObject
            {
                ["depth"] = 30, ["interactive"] = false, ["hideDisabled"] = false, ["hideOffscreen"] = false,
                ["windows"] = new JsonArray(new JsonObject
                {
                    ["hwnd"] = 42, ["title"] = "Journey fixture",
                    ["elements"] = new JsonArray(Element("FixtureWindow", "Window", "Journey fixture", navigation, page))
                })
            };
            if (LoseTargetAfterEnter && entered)
                Remove(tree, Page.Exercise!.TargetId);
            EditTree?.Invoke(tree);
            return tree;
        }

        private JsonElement Snapshot()
        {
            var nodes = new List<object>();
            void Add(JsonObject node, string? parentId, int depth)
            {
                var id = "n" + nodes.Count;
                var automationId = node["automationId"]!.GetValue<string>();
                var runtimeId = parentId is null ? "[2A,2A]"
                    : automationId == Page.Exercise?.TargetId ? "[1,2,3]" : $"[9,{nodes.Count}]";
                nodes.Add(AuditFixture.Node(id, parentId, depth, node["type"]!.GetValue<string>(), runtimeId,
                    automationId: automationId));
                foreach (var child in node["children"]!.AsArray().OfType<JsonObject>())
                    Add(child, id, depth + 1);
            }
            var root = Tree()["windows"]![0]!["elements"]![0]!.AsObject();
            if (WithheldPrivacyAncestor)
            {
                var contents = root["children"]!.AsArray().OfType<JsonObject>()
                    .Select(child => (JsonObject)child.DeepClone()).ToArray();
                root["children"] = new JsonArray(Element("", "Pane", null,
                    Element("", "Pane", null, Element("", "Pane", null, contents))));
            }
            Add(root, null, 0);
            var snapshot = AuditFixture.Snapshot(nodes: nodes.ToArray());
            if (PartialSnapshot)
            {
                snapshot = AuditTestFixture.Edit(snapshot, json =>
                {
                    json["status"] = "partial";
                    if (WithheldPrivacyAncestor)
                    {
                        var captured = json["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
                        foreach (var node in captured.Skip(3))
                        {
                            node.Remove("name");
                            node["issues"] = new JsonArray(new JsonObject
                            {
                                ["code"] = "privacy_withheld",
                                ["message"] = "Accessible name and text details were withheld because this node or an ancestor has indeterminate privacy."
                            });
                        }
                        var site = captured[3];
                        site["className"] = "InputSiteWindowClass";
                        site.Remove("isPassword");
                        site["issues"]!.AsArray().Insert(0, new JsonObject
                        {
                            ["code"] = "privacy_unavailable",
                            ["message"] = "The provider did not supply an explicit IsPassword Boolean; sensitive details were withheld."
                        });
                    }
                    else
                    {
                        json["issues"] = new JsonArray(new JsonObject
                        {
                            ["code"] = "privacy_unknown", ["message"] = "The provider did not establish privacy."
                        });
                    }
                });
            }
            return EditSnapshot is null ? snapshot : AuditTestFixture.Edit(snapshot, EditSnapshot);
        }

        private static string Unwrap(string selector) =>
            selector.StartsWith("runtime-", StringComparison.Ordinal) ? selector["runtime-".Length..] : selector;
    }
}
