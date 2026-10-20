using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fake = AccessLoop.Tests.NativeExploreSessionTestFixture;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreScopeMonitorTests
{
    [TestMethod]
    public void ForegroundExcursionIsLatchedEvenWhenTheCurrentSnapshotHasReturned()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        fake.Windows.Signal("foreground", 999);
        Assert.IsNotNull(monitor.Violation);
        Assert.IsTrue(monitor.StopToken.IsCancellationRequested);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(0, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public void ProvisionalPopupAllowsOnlyScopedObservationUntilExplicitApproval()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        monitor.BeginPopupTransition(TimeSpan.FromSeconds(5));
        fake.Windows.AddOwned(100);
        fake.Windows.Foreground = 100;
        fake.Windows.Signal("foreground", 100);
        var popup = fake.Target with { WindowHandle = 100 };
        monitor.Check(popup);
        Assert.ThrowsExactly<InvalidOperationException>(() => monitor.Check(fake.Target, input: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => monitor.Check(popup, audit: true));
        monitor.ApprovePopup(100);
        monitor.Check(popup, audit: true);
        monitor.Check(fake.Target, input: true);
        Assert.ThrowsExactly<InvalidOperationException>(() => monitor.Check(fake.Target, audit: true));
        monitor.BeginPopupClose();
        fake.Windows.Remove(100);
        monitor.EndPopup();
        monitor.Check(fake.Target, audit: true);
    }

    [TestMethod]
    public void SameProcessAloneDoesNotAuthorizeAnAdditionalWindow()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        fake.Windows.AddOwned(100, owner: 0);
        Assert.AreEqual("window-boundary-changed", monitor.Violation?.Kind);
        fake.Windows.Remove(100);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target));
    }

    [TestMethod]
    public void InitialForegroundRequestIsOneTimeAndNotRepeatedAfterUserSwitch()
    {
        var fake = new Fake();
        fake.Windows.Foreground = 999;
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        Assert.AreEqual(1, fake.Windows.ForegroundRequests);
        fake.Windows.Foreground = 999;
        fake.Windows.Signal("foreground", 999);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(1, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public void IdentityFailureDuringProvisionalInputStillLatchesTheStop()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        monitor.BeginPopupTransition(TimeSpan.FromSeconds(5));
        fake.IdentityValid = false;
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.IsNotNull(monitor.Violation);
    }

    [TestMethod]
    public void PopupDisappearanceWithoutAuthorizedCloseLatchesTheBoundary()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        monitor.Start(fake.Target);
        monitor.BeginPopupTransition(TimeSpan.FromSeconds(5));
        fake.Windows.AddOwned(100);
        monitor.ApprovePopup(100);
        fake.Windows.Remove(100);
        Assert.AreEqual("window-boundary-changed", monitor.Violation?.Kind);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
    }
}
