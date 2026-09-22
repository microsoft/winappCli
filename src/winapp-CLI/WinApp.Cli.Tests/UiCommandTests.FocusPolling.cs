// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Focus_ActivationSettle_UsesInjectedDelayBeforeFocusing()
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        _fakeDesktopForeground.OnRequestForeground = hwnd =>
            _fakeSystemQuery.ForegroundWindowResult = (nint)hwnd;
        _fakePollDelay.OnDelay = () =>
        {
            Assert.IsNull(_fakeUia.LastFocusedElement);
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
        };

        Assert.AreEqual(0, await RunVerifiedFocusAsync());
        Assert.HasCount(1, _fakePollDelay.RequestedDelays);
        Assert.AreEqual(100, _fakePollDelay.RequestedDelays[0]);
    }

    [TestMethod]
    public async Task Focus_DelayedKeyboardFocus_PollsOriginalControlWithoutRefocusing()
    {
        ConfigureVerifiedFocus();
        var original = _fakeUia.FindSingleResult;
        var focusCalls = 0;
        var reads = 0;
        _fakeUia.OnFocus = () => focusCalls++;
        _fakeUia.OnGetProperties = (element, property) =>
        {
            Assert.AreSame(original, element);
            Assert.AreEqual("HasKeyboardFocus", property);
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
            _fakeUia.PropertiesResult["HasKeyboardFocus"] = ++reads == 3;
        };
        _fakePollDelay.OnDelay = () =>
        {
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
            // A new selector match must not replace the control whose focus was requested.
            _fakeUia.FindSingleResult = new UiElement { Id = "replacement", WindowHandle = 4242 };
        };

        Assert.AreEqual(0, await RunVerifiedFocusAsync());
        Assert.AreEqual(3, reads);
        Assert.AreEqual(2, _fakePollDelay.CallCount);
        Assert.AreEqual(1, focusCalls);
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    public async Task Focus_NeverAcquiresKeyboardFocus_BoundsObservationsAndDelay()
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        var focusCalls = 0;
        _fakeUia.OnFocus = () => focusCalls++;
        _fakeUia.OnGetProperties = (_, _) => reads++;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("focus_not_acquired");
        Assert.IsTrue(reads is >= 2 and <= 11, $"Unexpected observation count: {reads}");
        Assert.IsTrue(_fakePollDelay.CallCount is >= 1 and <= 10);
        Assert.IsTrue(_fakePollDelay.RequestedDelays.All(delay => delay is > 0 and <= 50));
        Assert.IsTrue(_fakePollDelay.RequestedDelays.Sum() <= 500);
        Assert.AreEqual(1, focusCalls);
    }

    [TestMethod]
    public async Task Focus_ObservationAfterDeadline_DoesNotReportLateSuccess()
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) =>
        {
            reads++;
            Thread.Sleep(550);
            _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
        };

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("focus_not_acquired");
        Assert.AreEqual(1, reads);
        Assert.AreEqual(0, _fakePollDelay.CallCount);
    }

    [TestMethod]
    public async Task Focus_CancelledBetweenObservations_StopsBeforeNextRead()
    {
        ConfigureVerifiedFocus();
        using var cts = new CancellationTokenSource();
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) => reads++;
        _fakePollDelay.OnDelay = () => cts.Cancel();

        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["box", "-a", "TestApp", "--json"], cts.Token));
        Assert.IsInstanceOfType<OperationCanceledException>(_fakeDesktopLock.LastBodyException);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task Focus_ForegroundLostBetweenObservations_StopsWithoutReactivation()
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        var focusCalls = 0;
        _fakeUia.OnFocus = () => focusCalls++;
        _fakeUia.OnGetProperties = (_, _) => reads++;
        _fakePollDelay.OnDelay = () => _fakeSystemQuery.ForegroundWindowResult = 9000;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("foreground_not_target");
        Assert.AreEqual(1, reads);
        Assert.AreEqual(1, focusCalls);
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(9999u)]
    public async Task Focus_WindowLostBetweenObservations_StopsBeforeNextRead(uint pid)
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) => reads++;
        _fakePollDelay.OnDelay = () => _fakeSystemQuery.ProcessIdForWindowResult = pid;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task Focus_RootChangesBetweenObservations_StopsBeforeNextRead()
    {
        ConfigureVerifiedFocus(4243);
        _fakeSystemQuery.RootWindowByHwnd[4243] = 4242;
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) => reads++;
        _fakePollDelay.OnDelay = () => _fakeSystemQuery.RootWindowByHwnd[4243] = 9000;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task Focus_ProviderDisappearsOnLaterObservation_FailsImmediately()
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) =>
        {
            if (++reads == 2) { throw new COMException("Element unavailable", unchecked((int)0x80040201)); }
        };

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(2, reads);
        Assert.AreEqual(1, _fakePollDelay.CallCount);
    }

    [TestMethod]
    public async Task Focus_ForegroundLostDuringLaterSuccessfulRead_RejectsSuccess()
    {
        ConfigureVerifiedFocus();
        var reads = 0;
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) =>
        {
            if (++reads == 2)
            {
                _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
                _fakeSystemQuery.ForegroundWindowResult = 9000;
            }
        };

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("foreground_not_target");
        Assert.AreEqual(2, reads);
    }
}
