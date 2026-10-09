// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private void ConfigureVerifiedFocus(long elementHwnd = 4242)
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", WindowHandle = elementHwnd,
        };
        _fakeSystemQuery.ForegroundWindowResult = 4242;
        _fakeDesktopForeground.CheckForeground = hwnd => _fakeSystemQuery.ForegroundWindowResult == hwnd;
        _fakeForeground.CheckResult = hwnd => _fakeSystemQuery.ForegroundWindowResult == hwnd
            ? ForegroundCheck.Proceed : ForegroundCheck.ForegroundNotTarget;
        _fakeUia.PropertiesResult["HasKeyboardFocus"] = false;
        _fakeUia.OnFocus = () =>
        {
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
            Assert.AreEqual(_fakeSystemQuery.GetRootWindow(elementHwnd), (long)_fakeSystemQuery.ForegroundWindowResult);
            _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
        };
        _fakeUia.OnGetProperties = (element, property) =>
        {
            Assert.AreSame(_fakeUia.LastFocusedElement, element);
            Assert.AreEqual("HasKeyboardFocus", property);
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
        };
    }

    private Task<int> RunVerifiedFocusAsync() => ParseAndInvokeWithCaptureAsync(
        GetRequiredService<UiFocusCommand>(), ["box", "-a", "TestApp", "--json"]);

    [TestMethod]
    public async Task Focus_AlreadyActive_VerifiesWithoutActivation()
    {
        ConfigureVerifiedFocus();
        Assert.AreEqual(0, await RunVerifiedFocusAsync());
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
        Assert.IsTrue(_fakeForeground.Calls.Count >= 2);
    }

    [TestMethod]
    public async Task Focus_Inactive_ActivatesRootThenVerifiesControl()
    {
        ConfigureVerifiedFocus(4243);
        _fakeSystemQuery.RootWindowByHwnd[4243] = 4242;
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        _fakeDesktopForeground.OnRequestForeground = hwnd =>
        {
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
            Assert.IsNull(_fakeUia.LastFocusedElement);
            _fakeSystemQuery.ForegroundWindowResult = (nint)hwnd;
        };

        Assert.AreEqual(0, await RunVerifiedFocusAsync());
        CollectionAssert.AreEqual(new long[] { 4242 }, _fakeDesktopForeground.ForegroundRequests);
        Assert.IsTrue((bool)_fakeUia.PropertiesResult["HasKeyboardFocus"]!);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 4242");
    }

    [TestMethod]
    public async Task Focus_ActivationRefused_DoesNotFocus()
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsNull(_fakeUia.LastFocusedElement);
        AssertJsonErrorCode("foreground_not_target");
    }

    [TestMethod]
    public async Task Focus_LockedDesktop_ReportsUnlockAndRetryWithoutInjectionAdvice()
    {
        ConfigureVerifiedFocus();
        _fakeForeground.CheckResult = _ => ForegroundCheck.NoInteractiveDesktop;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsNull(_fakeUia.LastFocusedElement);
        AssertJsonErrorCode("no_interactive_desktop");
        var error = ConsoleStdErr.ToString();
        StringAssert.Contains(error, "Unlock the session and retry");
        Assert.IsFalse(error.Contains("inject", StringComparison.OrdinalIgnoreCase), error);
        Assert.IsFalse(error.Contains("UIA-pattern", StringComparison.OrdinalIgnoreCase), error);
    }

    [TestMethod]
    public async Task Focus_ForegroundLostDuringFocus_FailsDespiteKeyboardFocus()
    {
        ConfigureVerifiedFocus();
        _fakeUia.OnFocus = () =>
        {
            _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
            _fakeSystemQuery.ForegroundWindowResult = 9000;
        };
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("foreground_not_target");
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    public async Task Focus_ForegroundLostDuringVerification_Fails()
    {
        ConfigureVerifiedFocus();
        _fakeUia.OnGetProperties = (_, _) => _fakeSystemQuery.ForegroundWindowResult = 9000;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("foreground_not_target");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(null)]
    [DataRow("true")]
    public async Task Focus_UnconfirmedKeyboardFocus_Fails(object? value)
    {
        ConfigureVerifiedFocus();
        _fakeUia.OnFocus = () => _fakeUia.PropertiesResult["HasKeyboardFocus"] = value;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("focus_not_acquired");
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(9999u)]
    public async Task Focus_TargetLostDuringActivation_DoesNotFocus(uint newPid)
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        _fakeDesktopForeground.OnRequestForeground = hwnd =>
        {
            _fakeSystemQuery.ForegroundWindowResult = (nint)hwnd;
            _fakeSystemQuery.ProcessIdForWindowResult = newPid;
        };
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsNull(_fakeUia.LastFocusedElement);
        AssertJsonErrorCode("stale_element");
    }

    [TestMethod]
    public async Task Focus_TargetReusedDuringVerification_Fails()
    {
        ConfigureVerifiedFocus();
        _fakeUia.OnGetProperties = (_, _) => _fakeSystemQuery.ProcessIdForWindowResult = 9999;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("stale_element");
    }

    [TestMethod]
    public async Task Focus_OwnedPopupInFrontOfMainControl_IsNotSuccess()
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 5000;
        _fakeSystemQuery.WindowOwnerByHwnd[5000] = 4242;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsNull(_fakeUia.LastFocusedElement);
        AssertJsonErrorCode("foreground_not_target");
    }

    [TestMethod]
    public async Task Focus_ControlInOwnedPopup_VerifiesPopupNotMainWindow()
    {
        ConfigureVerifiedFocus(5000);
        _fakeSystemQuery.ForegroundWindowResult = 5000;
        _fakeSystemQuery.WindowOwnerByHwnd[5000] = 4242;
        _fakeSystemQuery.ProcessIdByHwnd[5000] = 8888;
        Assert.AreEqual(0, await RunVerifiedFocusAsync());
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 5000");
    }

    [TestMethod]
    public async Task Focus_ProviderVerificationFailure_DoesNotReportSuccess()
    {
        ConfigureVerifiedFocus();
        _fakeUia.PropertiesThrow = new System.Runtime.InteropServices.COMException("provider unavailable");
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        AssertJsonErrorCode("stale_element");
    }

    [TestMethod]
    public void Focus_SelectorIsOptionalForWindowFocus()
    {
        var focus = GetRequiredService<UiFocusCommand>();
        Assert.AreEqual(0, focus.Arguments.Single().Arity.MinimumNumberOfValues);
        Assert.AreEqual(1, focus.Arguments.Single().Arity.MaximumNumberOfValues);
    }

    [TestMethod]
    public async Task Focus_MinimizedWindow_RestoresThenFindsElement()
    {
        // Packaged apps such as Calculator expose no content while minimized: the element only
        // appears once the window is restored.
        ConfigureVerifiedFocus();
        var element = _fakeUia.FindSingleResult;
        _fakeUia.FindSingleResult = null;
        var lookups = 0;
        _fakeUia.OnFindSingle = () =>
        {
            if (++lookups == 3)
            {
                _fakeUia.FindSingleResult = element;
            }
        };
        _fakeDesktopForeground.MinimizedWindows.Add(4242);

        Assert.AreEqual(0, await RunVerifiedFocusAsync(), ConsoleStdErr.ToString());
        CollectionAssert.AreEqual(new long[] { 4242 }, _fakeDesktopForeground.RestoreRequests);
        Assert.AreEqual(1, _fakePollDelay.CallCount);
        Assert.AreSame(element, _fakeUia.LastFocusedElement);
    }

    [TestMethod]
    public async Task Focus_MinimizedWindow_ElementNeverAppears_FailsWithoutFocusing()
    {
        ConfigureVerifiedFocus();
        _fakeUia.FindSingleResult = null;
        _fakeDesktopForeground.MinimizedWindows.Add(4242);

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        CollectionAssert.AreEqual(new long[] { 4242 }, _fakeDesktopForeground.RestoreRequests);
        Assert.IsNull(_fakeUia.LastFocusedElement);
        AssertJsonErrorCode("element_not_found");
    }

    [TestMethod]
    public async Task Focus_ElementMissingInVisibleWindow_DoesNotRestoreOrWait()
    {
        ConfigureVerifiedFocus();
        _fakeUia.FindSingleResult = null;

        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsEmpty(_fakeDesktopForeground.RestoreRequests);
        Assert.AreEqual(0, _fakeDesktopLock.OpenDesktopSections);
        AssertJsonErrorCode("element_not_found");
    }

    [TestMethod]
    public async Task Focus_WindowElement_IsConfirmedByForegroundNotKeyboardFocus()
    {
        // A window's root element never reports HasKeyboardFocus; focus lands on a descendant.
        ConfigureVerifiedFocus();
        _fakeUia.FindSingleResult = new UiElement { Id = "win", Selector = "win-app-1234", Type = "Window", WindowHandle = 4242 };
        _fakeUia.OnFocus = () => { };
        _fakeUia.OnGetProperties = (_, _) => Assert.Fail("A window element must not wait for HasKeyboardFocus.");

        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["win-app-1234", "-a", "TestApp", "--json"]));
    }

    [TestMethod]
    public async Task Focus_NoSelector_RestoresAndActivatesTargetWindow()
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        _fakeDesktopForeground.MinimizedWindows.Add(4242);
        _fakeDesktopForeground.OnRequestForeground = hwnd =>
        {
            Assert.AreEqual(1, _fakeDesktopLock.OpenDesktopSections);
            Assert.HasCount(1, _fakeDesktopForeground.RestoreRequests);
            _fakeSystemQuery.ForegroundWindowResult = (nint)hwnd;
        };

        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["-a", "TestApp", "--json"]));
        CollectionAssert.AreEqual(new long[] { 4242 }, _fakeDesktopForeground.RestoreRequests);
        CollectionAssert.AreEqual(new long[] { 4242 }, _fakeDesktopForeground.ForegroundRequests);
        Assert.IsNull(_fakeUia.LastFocusedElement);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 4242");
    }

    [TestMethod]
    public async Task Focus_NoSelector_ActivationRefused_Fails()
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ForegroundWindowResult = 9000;

        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["-a", "TestApp", "--json"]));
        AssertJsonErrorCode("foreground_not_target");
    }

    [TestMethod]
    public async Task Focus_NoSelector_TargetWithoutWindow_Fails()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 0;

        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["-a", "TestApp", "--json"]));
        AssertJsonErrorCode("no_target");
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    public async Task Focus_FilterWithoutSelector_IsRejected()
    {
        ConfigureVerifiedFocus();

        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["-a", "TestApp", "--type", "Edit", "--json"]));
        AssertJsonErrorCode("invalid_arguments");
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    public async Task Focus_ExplicitWindow_DoesNotActivateSiblingOrPopup()
    {
        ConfigureVerifiedFocus(5000);
        _fakeTargetResolver.TargetResult.IsExplicitWindow = true;
        _fakeSystemQuery.WindowOwnerByHwnd[5000] = 4242;
        var exit = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["box", "-w", "4242", "--json"]);
        Assert.AreEqual(1, exit);
        Assert.IsNull(_fakeUia.LastFocusedElement);
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
        AssertJsonErrorCode("stale_element");
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(9999u)]
    public async Task Focus_StaleTarget_DoesNotActivate(uint pid)
    {
        ConfigureVerifiedFocus();
        _fakeSystemQuery.ProcessIdForWindowResult = pid;
        Assert.AreEqual(1, await RunVerifiedFocusAsync());
        Assert.IsNull(_fakeUia.LastFocusedElement);
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
        AssertJsonErrorCode("stale_element");
    }

    [TestMethod]
    public async Task Focus_CancelledDuringActivation_DoesNotFocusOrRetry()
    {
        ConfigureVerifiedFocus();
        using var cts = new CancellationTokenSource();
        _fakeSystemQuery.ForegroundWindowResult = 9000;
        _fakeDesktopForeground.OnRequestForeground = _ => cts.Cancel();
        var exit = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["box", "-a", "TestApp", "--json"], cts.Token);
        Assert.AreEqual(1, exit);
        Assert.IsNull(_fakeUia.LastFocusedElement);
        Assert.IsInstanceOfType<OperationCanceledException>(_fakeDesktopLock.LastBodyException);
        Assert.HasCount(1, _fakeDesktopForeground.ForegroundRequests);
    }

    [TestMethod]
    public async Task Focus_CancelledDuringVerification_DoesNotSucceed()
    {
        ConfigureVerifiedFocus();
        using var cts = new CancellationTokenSource();
        _fakeUia.OnGetProperties = (_, _) => cts.Cancel();
        var exit = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiFocusCommand>(), ["box", "-a", "TestApp", "--json"], cts.Token);
        Assert.AreEqual(1, exit);
        Assert.IsInstanceOfType<OperationCanceledException>(_fakeDesktopLock.LastBodyException);
    }
}
