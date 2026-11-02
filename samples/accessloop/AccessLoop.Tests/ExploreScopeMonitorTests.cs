using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fake = AccessLoop.Tests.NativeExploreSessionTestFixture;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreScopeMonitorTests
{
    [TestMethod]
    public async Task UnknownForegroundStopsWithoutClaimingAnotherWindowAndCannotResume()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        fake.Windows.Foreground = 0;
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual("foreground-unavailable", monitor.Violation?.Kind);
        Assert.AreEqual(0L, monitor.Violation?.WindowHandle);
        fake.Windows.Foreground = fake.Target.WindowHandle;
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(0, fake.Windows.ForegroundRequests);
        Assert.IsTrue(monitor.StopToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task ZeroForegroundEventIsRetainedWhenTheCurrentReadHasRecovered()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        fake.Windows.Signal("foreground", 0);
        Assert.AreEqual("foreground-unavailable", monitor.Violation?.Kind);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
    }

    [TestMethod]
    public async Task NativeMonitorFailurePreservesItsReasonInsteadOfPretendingFocusChanged()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        fake.Windows.Foreground = 0;
        fake.Windows.Signal("monitor-failed", 0, "GetMessage failed with Win32 error 5.");
        Assert.AreEqual("monitor-failed", monitor.Violation?.Kind);
        Assert.AreEqual("GetMessage failed with Win32 error 5.", monitor.Violation?.Message);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(0, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public async Task ForegroundExcursionIsLatchedEvenWhenTheCurrentSnapshotHasReturned()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        fake.Windows.Signal("foreground", 999);
        Assert.IsNotNull(monitor.Violation);
        Assert.IsTrue(monitor.StopToken.IsCancellationRequested);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(0, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public async Task ProvisionalPopupAllowsOnlyScopedObservationUntilExplicitApproval()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
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
    public async Task SameProcessAloneDoesNotAuthorizeAnAdditionalWindow()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        fake.Windows.AddOwned(100, owner: 0);
        Assert.AreEqual("window-boundary-changed", monitor.Violation?.Kind);
        fake.Windows.Remove(100);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target));
    }

    [TestMethod]
    public async Task InitialForegroundRequestIsOneTimeAndNotRepeatedAfterUserSwitch()
    {
        var fake = new Fake();
        fake.Windows.Foreground = 999;
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        Assert.AreEqual(1, fake.Windows.ForegroundRequests);
        fake.Windows.Foreground = 999;
        fake.Windows.Signal("foreground", 999);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.AreEqual(1, fake.Windows.ForegroundRequests);
    }

    [TestMethod]
    public async Task IdentityFailureDuringProvisionalInputStillLatchesTheStop()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        monitor.BeginPopupTransition(TimeSpan.FromSeconds(5));
        fake.IdentityValid = false;
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
        Assert.IsNotNull(monitor.Violation);
    }

    [TestMethod]
    public async Task PopupDisappearanceWithoutAuthorizedCloseLatchesTheBoundary()
    {
        var fake = new Fake();
        using var monitor = new ExploreScopeMonitor(fake, fake.Native, fake.Windows);
        await monitor.StartAsync(fake.Target, CancellationToken.None);
        monitor.BeginPopupTransition(TimeSpan.FromSeconds(5));
        fake.Windows.AddOwned(100);
        monitor.ApprovePopup(100);
        fake.Windows.Remove(100);
        Assert.AreEqual("window-boundary-changed", monitor.Violation?.Kind);
        Assert.ThrowsExactly<ExploreStoppedException>(() => monitor.Check(fake.Target, input: true));
    }
}
