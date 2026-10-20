using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fake = AccessLoop.Tests.NativeExploreSessionTestFixture;

namespace AccessLoop.Tests;

[TestClass]
public sealed class NativeExploreSessionTests
{
    private string directory = null!;
    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".explore-test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    private async Task<ExploreReport> RunAsync(Fake fake, ExploreProfile? profile = null, bool probe = false, CancellationToken token = default)
    {
        profile ??= Fake.Profile;
        var report = fake.Report(profile);
        await fake.Session().ExecuteAsync(profile, report, directory, probe, null, token);
        Assert.IsTrue(fake.Windows.Disposed);
        return report;
    }

    [TestMethod]
    public async Task ThreePagesCaptureRealTransitionsAndImmutableAuditEvidence()
    {
        var fake = new Fake { PostDialogFocus = "AppChoseAnotherControl" };
        var report = await RunAsync(fake);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.AreEqual(7, report.States.Count);
        Assert.IsTrue(report.States.All(state => state.Status == "captured" && state.Before?.PageReady == true && state.After?.PageReady == true));
        foreach (var state in report.States)
        {
            var root = Path.Combine(directory, state.Directory);
            Assert.AreEqual(state.ScanSha256, AuditFiles.Hash(Path.Combine(root, "scan.json")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "report.html")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "state-checks.json")));
            _ = AuditFiles.ReadVerified(Path.Combine(root, "scan.json"));
        }
        Assert.AreEqual("states\\001", report.States[0].Directory);
        CollectionAssert.AreEqual(new[] { 75d, 100d, 0d }, fake.ScrollRequests);
        Assert.AreEqual("end-observed", report.Pages[1].ScrollStatus);
        Assert.AreEqual("restored-navigation", report.Restoration?.Status);
        Assert.AreEqual("basics", fake.Page);
        Assert.AreEqual("DensityStandard", fake.Selected);
        Assert.IsFalse(fake.Expanded);
        Assert.IsFalse(fake.Dialog);
        var dialog = report.Pages[2].Interactions.Single();
        Assert.AreEqual("InfoDialog", dialog.ControlId);
        Assert.AreEqual("InfoDialog", dialog.Before?.ControlId);
        Assert.AreEqual("InfoDialog", dialog.After?.ControlId);
        Assert.AreEqual("NavDialogs", dialog.Before?.FocusAutomationId);
        Assert.AreEqual("CloseInfoDialog", report.States.Single(state => state.Kind == "dialog").Before?.FocusAutomationId);
        Assert.AreEqual("AppChoseAnotherControl", dialog.After?.FocusAutomationId);
        Assert.IsFalse(fake.Calls.Any(call => call.Arguments[0] is "focus" or "send-keys" or "set-value"));
        Assert.IsTrue(fake.Calls.Where(call => call.Input).All(call => call.Arguments[1].StartsWith("control-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RealTabsUseSelectionItemPatternAndRestoreTheOriginalPage()
    {
        var fake = new Fake { TabNavigation = true };
        var report = await RunAsync(fake);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.AreEqual(7, report.States.Count);
        Assert.AreEqual("basics", fake.Page);
        Assert.AreEqual("restored-navigation", report.Restoration?.Status);
    }

    [TestMethod]
    public async Task ATabWithSettingSemanticsIsNotUsedAsNavigation()
    {
        var fake = new Fake { TabNavigation = true };
        fake.EditProperties = (id, properties) =>
        {
            if (id == "NavBasics")
                properties["ToggleState"] = "Off";
        };
        var report = await RunAsync(fake);
        Assert.AreNotEqual("finished", report.Status);
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
    }

    [TestMethod]
    public async Task TabNavigationDoesNotAcceptAnUnverifiedActivationPattern()
    {
        var fake = new Fake { TabNavigation = true };
        fake.EditResponse = (call, response) =>
        {
            if (call.Arguments is ["invoke", "control-NavScrolling"])
                response["pattern"] = "InvokePattern";
        };
        var report = await RunAsync(fake);
        Assert.AreNotEqual("finished", report.Status);
        Assert.IsFalse(report.States.Any(state => state.PageId == "scrolling"));
    }

    [TestMethod]
    [DataRow("type")]
    [DataRow("parent")]
    [DataRow("duplicate")]
    [DataRow("property")]
    [DataRow("selection")]
    public async Task NavigationRequiresLiteralUniqueButtonUnderTheDeclaredRoot(string invalid)
    {
        var fake = new Fake();
        fake.EditTree = tree =>
        {
            var button = Fake.Find(tree, "NavBasics")!;
            if (invalid == "type") button["type"] = "ListItem";
            if (invalid == "parent") Fake.Find(tree, "MainNavigation")!["automationId"] = "UnexpectedRoot";
            if (invalid == "duplicate") button["children"]!.AsArray().Add(Fake.Element("NavBasics", "Button"));
        };
        fake.EditProperties = (id, properties) =>
        {
            if (id != "NavBasics") return;
            if (invalid == "property") properties["AutomationId"] = "OtherControl";
            if (invalid == "selection") properties["IsSelected"] = false;
        };
        var report = await RunAsync(fake);
        Assert.AreNotEqual("finished", report.Status);
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
        Assert.AreEqual(0, report.States.Count);
    }

    [TestMethod]
    public async Task InitialPageIsObservedBeforeAnyInput()
    {
        var fake = new Fake { Page = "dialogs" };
        var report = await RunAsync(fake);
        Assert.AreNotEqual("finished", report.Status);
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
        Assert.AreEqual("not-attempted", report.Restoration?.Status);
    }

    [TestMethod]
    public async Task ForegroundBoundaryBetweenPropertyAndInvokeLatchesWithoutRestoration()
    {
        var fake = new Fake();
        var atBoundary = 0;
        fake.AfterCommand = call =>
        {
            if (call.Arguments is ["get-property", "DensityCombo"] && reportCount() > 0)
            {
                fake.AfterCommand = null;
                atBoundary = fake.Calls.Count;
                fake.Windows.Foreground = 9999;
                fake.Windows.Signal("foreground", 9999);
            }
        };
        int reportCount() => fake.Calls.Count(call => call.Arguments[0] == "audit-snapshot");
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual(atBoundary, fake.Calls.Count);
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
        Assert.AreEqual("not-attempted", report.Restoration?.Status);
        Assert.IsTrue(report.Events.Any(item => item.Kind == "foreground-changed"));
    }

    [TestMethod]
    public async Task UnrelatedBackgroundSignalsDoNotInterruptTheRun()
    {
        var fake = new Fake();
        fake.AfterCommand = _ => fake.Windows.Signal("background", 9999);
        var report = await RunAsync(fake);
        Assert.AreEqual("finished", report.Status, report.Error);
    }

    [TestMethod]
    [DataRow("dpi")]
    [DataRow("desktop")]
    [DataRow("size")]
    [DataRow("identity")]
    public async Task EnvironmentAndIdentityChangesStopCommandsAndPreservePartialCapture(string change)
    {
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (call.Arguments[0] != "audit-snapshot") return;
            fake.AfterCommand = null;
            fake.Native.Window = change switch
            {
                "dpi" => fake.Native.Window with { Dpi = 144 },
                "desktop" => fake.Native.Window with { InputDesktop = "Winlogon" },
                "size" => fake.Native.Window with { Width = 1200 },
                _ => fake.Native.Window
            };
            if (change == "identity") fake.IdentityValid = false;
            fake.Windows.Signal();
        };
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual("blocked", report.States.Single().Status);
        Assert.IsTrue(File.Exists(Path.Combine(directory, report.States[0].Directory, "scan.json")));
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
        Assert.AreEqual("not-attempted", report.Restoration?.Status);
    }

    [TestMethod]
    public async Task ChangedPageDuringCaptureDoesNotCountAsVerifiedCoverage()
    {
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (call.Arguments[0] == "audit-snapshot") fake.Page = "dialogs";
        };
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual("blocked", report.States.Single().Status);
        Assert.IsNotNull(report.States[0].ScanSha256);
        Assert.AreEqual("dialogs", report.States[0].After?.PageId);
        Assert.IsFalse(report.States[0].After?.PageReady);
        Assert.IsFalse(fake.Calls.Any(call => call.Input));
    }

    [TestMethod]
    public async Task ValidPartialAuditStatusIsRetainedWithoutInventingCompliance()
    {
        var fake = new Fake { PartialSnapshot = true };
        var report = await RunAsync(fake);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.IsTrue(report.States.All(state => state.Status == "captured" && state.CaptureStatus == "partial"));
    }

    [TestMethod]
    public async Task AuditExecutionFailurePreventsCapturedState()
    {
        var report = await RunAsync(new Fake { AxeFailure = true });
        Assert.AreEqual("partial", report.Status);
        Assert.IsTrue(report.States.All(state => state.Status == "blocked" && state.Error!.Contains("Axe fixture", StringComparison.Ordinal)));
        Assert.IsTrue(report.Pages.All(page => page.NavigationStatus == "blocked"));
    }

    [TestMethod]
    public async Task ScrollStepPreservesViewportOverlapRatherThanSkippingContent()
    {
        var fake = new Fake { ViewSize = 25 };
        var profile = WithScroll(Fake.Profile.Pages[1].Scroll! with { MaxPositions = 5 });
        var report = await RunAsync(fake, profile);
        Assert.AreEqual("finished", report.Status, report.Error);
        CollectionAssert.AreEqual(new[] { 25d, 50d, 75d, 100d, 0d }, fake.ScrollRequests);
        Assert.AreEqual("end-observed", report.Pages[1].ScrollStatus);
    }

    [TestMethod]
    public async Task ScrollPositionLimitPreservesOriginalPercentAndHonestIncompleteCoverage()
    {
        var fake = new Fake { Percent = 40 };
        var report = await RunAsync(fake, WithScroll(Fake.Profile.Pages[1].Scroll! with { MaxPositions = 2 }));
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("limit-reached", report.Pages[1].ScrollStatus);
        var interaction = report.Pages[1].Interactions.Single();
        Assert.AreEqual(40d, interaction.Before?.ScrollPercent);
        Assert.AreEqual(40d, interaction.After?.ScrollPercent);
        Assert.AreEqual("restored-scroll-position", interaction.Restoration?.Status);
        Assert.AreEqual(40d, fake.Percent);
    }

    [TestMethod]
    [DataRow("no-progress")]
    [DataRow("extent")]
    [DataRow("bottom")]
    public async Task UnsupportedScrollTransitionsNeverBecomeEndObserved(string failure)
    {
        var fake = new Fake { IgnoreScroll = failure == "no-progress", HideBottom = failure == "bottom" };
        if (failure == "extent")
            fake.AfterCommand = call => { if (call.Arguments[0] == "scroll") fake.ViewSize = 40; };
        var report = await RunAsync(fake);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("blocked", report.Pages[1].ScrollStatus);
        Assert.IsFalse(fake.Calls.Any(call => call.Arguments.Contains("--wheel") || call.Arguments[0] == "scroll-into-view"));
    }

    [TestMethod]
    public async Task SelectionChangeIsNotRolledBackBySelectingAnOption()
    {
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (call.Arguments is ["expand-collapse", _, "--state", "expanded"])
                fake.Selected = "DensityCompact";
        };
        var report = await RunAsync(fake);
        Assert.AreNotEqual("finished", report.Status);
        Assert.AreEqual("blocked", report.Pages[0].Interactions.Single().Status);
        Assert.AreEqual("incomplete", report.Pages[0].Interactions.Single().Restoration?.Status);
        Assert.IsTrue(fake.Calls.Where(call => call.Input).All(call => call.Arguments[0] == "expand-collapse"));
        Assert.AreEqual("DensityCompact", fake.Selected);
    }

    [TestMethod]
    public async Task SeparateComboPopupDoesNotExpandTheInWindowProfileScope()
    {
        var fake = new Fake { ExternalCombo = true };
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.IsFalse(report.States.Any(state => state.Kind == "dropdown"));
        Assert.IsFalse(fake.AxeWindows.Contains(100));
        Assert.IsFalse(fake.Calls.Any(call => call.Window == 100));
        Assert.AreEqual(1, fake.Calls.Count(call => call.Input));
        Assert.IsTrue(report.Events.Any(item => item.Kind == "window-boundary-changed" && item.WindowHandle == 100));
        Assert.AreEqual("not-attempted", report.Pages[0].Interactions.Single().Restoration?.Status);
    }

    [TestMethod]
    public async Task WrongPopupOwnershipStopsWithoutReadingTheOtherWindow()
    {
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (call.Arguments is ["expand-collapse", _, "--state", "expanded"])
                fake.Windows.AddOwned(100, owner: 999);
        };
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.IsFalse(fake.Calls.Any(call => call.Window == 100));
        Assert.AreEqual(1, fake.Calls.Count(call => call.Input));
    }

    [TestMethod]
    public async Task ScopeProbePassRequiresObservedBoundaryAndNoFurtherCommands()
    {
        var fake = new Fake();
        var report = await RunAsync(fake, probe: true);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.AreEqual("stopped", report.GuardProbe.Status);
        Assert.AreEqual(0, report.GuardProbe.ActionsAfterBoundaryViolation);
        Assert.AreEqual("not-attempted", report.Restoration?.Status);
        Assert.IsTrue(report.Events.Any(item => item.Kind == "ordinary-restoration"
            && item.Message.StartsWith("restored-navigation:", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "invoke", "control-OpenScopeProbe" }, fake.Calls.Last().Arguments);
        Assert.AreEqual(1, fake.Calls.Count(call => call.Arguments.Contains("control-OpenScopeProbe")));
    }

    [TestMethod]
    public async Task MissingScopeProbeEventCannotPass()
    {
        var report = await RunAsync(new Fake { IgnoreProbe = true }, probe: true);
        Assert.AreEqual("not-tested", report.GuardProbe.Status);
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual("incomplete", report.Restoration?.Status);
    }

    [TestMethod]
    public async Task ExistingPartialStateDirectoriesAreNeverReused()
    {
        Directory.CreateDirectory(Path.Combine(directory, "states", "001"));
        var sentinel = Path.Combine(directory, "states", "001", "scan.json");
        File.WriteAllText(sentinel, "older partial evidence");
        var report = await RunAsync(new Fake());
        Assert.AreEqual("states\\002", report.States[0].Directory);
        Assert.AreEqual("older partial evidence", File.ReadAllText(sentinel));
        Assert.AreEqual("finished", report.Status, report.Error);
    }

    [TestMethod]
    public async Task CancellationOnlyPerformsBoundedCleanupInTheIntactOriginalScope()
    {
        using var cancel = new CancellationTokenSource();
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (call.Arguments is ["expand-collapse", _, "--state", "expanded"]) cancel.Cancel();
        };
        var report = await RunAsync(fake, token: cancel.Token);
        Assert.AreEqual("paused", report.Status);
        Assert.IsFalse(fake.Expanded);
        Assert.AreEqual("restored-control", report.Pages[0].Interactions.Single().Restoration?.Status);
        Assert.IsTrue(fake.Calls.Where(call => call.Input).All(call => call.Arguments[0] == "expand-collapse"));
    }

    [TestMethod]
    public async Task ChangedSelectionDuringCaptureRetainsActualAfterObservation()
    {
        var fake = new Fake();
        fake.AfterCommand = call =>
        {
            if (fake.Expanded && call.Arguments[0] == "audit-snapshot") fake.Selected = "DensityCompact";
        };
        var report = await RunAsync(fake);
        var expanded = report.States.Single(state => state.Kind == "dropdown");
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual("blocked", expanded.Status);
        Assert.AreEqual("DensityStandard", expanded.Before?.SelectionAutomationIds.Single());
        Assert.AreEqual("DensityCompact", expanded.After?.SelectionAutomationIds.Single());
        Assert.AreEqual(1, fake.Calls.Count(call => call.Input));
    }

    [TestMethod]
    public async Task OwnedWindowDialogIsExplicitlyUnsupportedBeforeAnyOpener()
    {
        var profile = Fake.Profile with
        {
            Pages = Fake.Profile.Pages.Select(page => page.Id == "dialogs"
                ? page with { Dialogs = [page.Dialogs[0] with { Scope = "owned-window" }] } : page).ToArray()
        };
        var fake = new Fake();
        var report = await RunAsync(fake, profile);
        Assert.AreEqual("partial", report.Status);
        var dialog = report.Pages[2].Interactions.Single();
        Assert.AreEqual("blocked", dialog.Status);
        StringAssert.Contains(dialog.Reason, "Owned-window dialogs are unsupported");
        Assert.IsFalse(fake.Calls.Any(call => call.Input && call.Arguments.Contains("control-OpenInfoDialog")));
    }

    [TestMethod]
    public async Task AWindowWithADifferentTitleDoesNotProveTheExplicitProbe()
    {
        var fake = new Fake { IgnoreProbe = true };
        fake.AfterCommand = call =>
        {
            if (call.Arguments is ["invoke", "control-OpenScopeProbe"])
                fake.Windows.AddOwned(201, title: "Unexpected additional window");
        };
        var report = await RunAsync(fake, probe: true);
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual("not-tested", report.GuardProbe.Status);
        Assert.AreEqual("not-attempted", report.Restoration?.Status);
        CollectionAssert.AreEqual(new[] { "invoke", "control-OpenScopeProbe" }, fake.Calls.Last().Arguments);
    }

    [TestMethod]
    public async Task StateBudgetStopsBeforeTheNextInteractionOpener()
    {
        var fake = new Fake();
        var report = await RunAsync(fake, Fake.Profile with { MaxStates = 6 });
        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(6, report.States.Count);
        Assert.AreEqual("not-tested", report.Pages[2].Interactions.Single().Status);
        Assert.IsFalse(fake.Calls.Any(call => call.Input && call.Arguments.Contains("control-OpenInfoDialog")));
        Assert.IsTrue(report.Pages.All(page => page.ScrollStatus != "pending" && page.NavigationStatus != "pending"));
    }

    [TestMethod]
    public async Task LockedDesktopPausesBeforeAnyNativeCommandOrForegroundRequest()
    {
        var fake = new Fake();
        fake.Native.Window = fake.Native.Window with { InputDesktop = "Winlogon" };
        var report = await RunAsync(fake);
        Assert.AreEqual("paused", report.Status);
        Assert.AreEqual(0, fake.Calls.Count);
        Assert.AreEqual(0, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public async Task PassiveScreenshotsStayBoundToTheExactVerifiedState()
    {
        var fake = new Fake();
        var report = fake.Report() with { Screenshot = true };
        await fake.Session(screenshot: true).ExecuteAsync(Fake.Profile, report, directory, false, null, CancellationToken.None);
        Assert.AreEqual("finished", report.Status, report.Error);
        foreach (var state in report.States)
        {
            var scan = AuditFiles.ReadVerified(Path.Combine(directory, state.Directory, "scan.json"));
            Assert.IsTrue(scan.Artifacts.Any(artifact => artifact.Path == "screenshot.png"));
            Assert.AreEqual(state.ScanSha256, AuditFiles.Hash(Path.Combine(directory, state.Directory, "scan.json")));
        }
    }

    [TestMethod]
    public async Task NativeInvariantStringPropertiesSupportTheSameStatePath()
    {
        var fake = new Fake();
        fake.EditProperties = (_, properties) =>
        {
            foreach (var name in new[] { "IsEnabled", "IsOffscreen", "HasKeyboardFocus", "HorizontallyScrollable", "VerticallyScrollable" })
                if (properties[name] is { } flag)
                    properties[name] = flag.GetValue<bool>().ToString();
            foreach (var name in new[] { "ScrollVerticalPercent", "ScrollVerticalViewSize" })
                if (properties[name] is { } number)
                    properties[name] = number.GetValue<double>().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        };
        var report = await RunAsync(fake);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.AreEqual("end-observed", report.Pages[1].ScrollStatus);
        Assert.AreEqual(7, report.States.Count);
    }

    [TestMethod]
    public async Task InteractionsAreNotMarkedCapturedUntilCleanupHasBeenVerified()
    {
        var fake = new Fake();
        var report = fake.Report();
        var cleanupChecks = 0;
        fake.AfterCommand = call =>
        {
            var kind = call.Arguments switch
            {
                ["expand-collapse", _, "--state", "collapsed"] => "dropdown",
                ["scroll", _, "--vertical-percent", "0"] => "scroll",
                ["invoke", "control-CloseInfoDialog"] => "dialog",
                _ => null
            };
            if (kind is null) return;
            var interaction = report.Pages.SelectMany(page => page.Interactions).Single(item => item.Kind == kind);
            Assert.AreNotEqual("captured", interaction.Status);
            cleanupChecks++;
        };
        await fake.Session().ExecuteAsync(Fake.Profile, report, directory, false, null, CancellationToken.None);
        Assert.AreEqual(3, cleanupChecks);
        Assert.AreEqual("finished", report.Status, report.Error);
        Assert.IsTrue(report.Pages.SelectMany(page => page.Interactions).All(item => item.Status == "captured"));
    }

    private static ExploreProfile WithScroll(ExploreScroll scroll) =>
        Fake.Profile with { Pages = Fake.Profile.Pages.Select(page => page.Id == "scrolling" ? page with { Scroll = scroll } : page).ToArray() };
}
