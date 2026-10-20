using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreCommandTests
{
    [TestMethod]
    public void CompletedCapturedStatesWithFailuresAreNotSuccess()
    {
        var report = Complete();
        report.States[0].RuleFailures = 1;
        Assert.AreEqual(1, ExploreCommand.ExitCode(report));
    }

    [TestMethod]
    public void CompletedStatesWithoutFailuresDoNotErasePartialCapture()
    {
        var report = Complete();
        report.States[0].CaptureStatus = "partial";
        Assert.AreEqual(2, ExploreCommand.ExitCode(report));
    }

    [TestMethod]
    [DataRow("missing-page")]
    [DataRow("pending-page")]
    [DataRow("missing-base")]
    [DataRow("scroll-unfinished")]
    [DataRow("missing-dropdown")]
    [DataRow("missing-dialog")]
    [DataRow("no-guard-event")]
    [DataRow("input-after-guard")]
    [DataRow("restore-failed")]
    public void MissingDeclaredCoverageAndUnsafeOutcomesCannotBecomeExitZero(string kind)
    {
        var report = Complete();
        var declared = report.Profile.Pages[0];
        switch (kind)
        {
            case "missing-page": report.Pages.Clear(); break;
            case "pending-page": report.Pages[0].NavigationStatus = "pending"; break;
            case "missing-base": report.States[0] = report.States[0] with { Kind = "dialog" }; break;
            case "scroll-unfinished":
                report = report with { Profile = report.Profile with { Pages = [declared with
                {
                    Scroll = new() { ContainerId = "Scroll", TopAnchorId = "Top", BottomAnchorId = "Bottom" }
                }] } };
                report.Pages[0].ScrollStatus = "limit-reached";
                break;
            case "missing-dropdown":
                report = report with { Profile = report.Profile with { Pages = [declared with
                {
                    Dropdowns = [new() { ControlId = "Dropdown", ItemIds = ["Option"] }]
                }] } };
                break;
            case "missing-dialog":
                report = report with { Profile = report.Profile with { Pages = [declared with
                {
                    Dialogs = [new() { OpenerId = "Open", RootId = "Dialog", DismissId = "Close" }]
                }] } };
                break;
            case "no-guard-event": report.GuardProbe.Status = "not-observed"; break;
            case "input-after-guard": report.GuardProbe.Status = "stopped"; report.GuardProbe.ActionsAfterBoundaryViolation = 1; break;
            case "restore-failed": report.Restoration = new("incomplete", "Original page unconfirmed."); break;
        }
        Assert.AreEqual(2, ExploreCommand.ExitCode(report));
    }

    [TestMethod]
    public void PausedRunHasDistinctExitCode()
    {
        var report = Complete();
        report.Status = "paused";
        Assert.AreEqual(3, ExploreCommand.ExitCode(report));
    }

    [TestMethod]
    public void CompleteDeclaredRunCanReturnZeroWithoutAComplianceClaim() =>
        Assert.AreEqual(0, ExploreCommand.ExitCode(Complete()));

    private static ExploreReport Complete()
    {
        var profile = new ExploreProfile
        {
            Name = "Fixture", ProcessName = "Fixture", InitialPageId = "home", NavigationRootId = "Navigation",
            Pages = [new() { Id = "home", Name = "Home", NavigationId = "NavHome", ReadyId = "HomeReady" }]
        };
        return new()
        {
            RunId = "fixture", Name = "Fixture", Target = new(1, 2, "Fixture", DateTimeOffset.UtcNow, "Fixture", null),
            ProfileSha256 = new string('A', 64), Profile = profile,
            Tools = new("fixture", new string('B', 64), new string('C', 64)), Status = "finished",
            Pages = [new() { PageId = "home", Name = "Home", NavigationStatus = "captured" }],
            States = [new()
            {
                Id = "home-base", PageId = "home", Name = "Home", Kind = "base", Directory = @"states\001",
                Status = "captured", CaptureStatus = "completed"
            }],
            Restoration = new("restored-navigation", "Original navigation observed.")
        };
    }
}
