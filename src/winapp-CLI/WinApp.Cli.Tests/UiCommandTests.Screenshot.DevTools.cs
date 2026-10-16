// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// With WinUI DevTools on by default, a screenshot hides its chrome (toolbar, markers, flyout) for the capture and
/// leaves out DevTools' own windows, so the image is the one the app shows without DevTools.
/// </summary>
public partial class UiCommandTests
{
    private (UiScreenshotCommand Command, UiScreenshotCommand.Handler Handler) CreateDevToolsScreenshotCommand(
        List<(int Pid, bool Hidden, int CapturesSoFar)> calls, params int[] injected)
    {
        var command = new UiScreenshotCommand();
        var handler = new UiScreenshotCommand.Handler(
            _fakeTargetResolver, _fakeUia, GetRequiredService<IUiSelectorParser>(), _fakeWindowFinder, _fakeSystemQuery,
            TestAnsiConsole, _fakeDesktopLock, NullLogger<UiScreenshotCommand>.Instance)
        {
            InjectedDevToolsPids = () => injected,
            SetDevToolsChromeHidden = (pid, hidden) => { calls.Add((pid, hidden, _fakeUia.ScreenshotCalls.Count)); return true; },
            DevToolsChromeSettle = TimeSpan.Zero,
        };
        command.SetAction((result, ct) => handler.InvokeAsync(result, ct));
        return (command, handler);
    }

    [TestMethod]
    public async Task Screenshot_OfAnAppWithDevTools_HidesItsChromeForTheCaptureOnly()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 11;
        _fakeTargetResolver.TargetResult.ProcessId = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[11] = 4321;
        var calls = new List<(int Pid, bool Hidden, int CapturesSoFar)>();
        var (command, _) = CreateDevToolsScreenshotCommand(calls, 4321, 999);

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", ShotPath()]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeUia.ScreenshotCalls);
        CollectionAssert.AreEqual(new[] { (4321, true, 0), (4321, false, 1) }, calls,
            "hidden before the capture and shown after it, and only in the captured app");
    }

    [TestMethod]
    public async Task Screenshot_ThatFails_StillShowsTheChromeAgain()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 11;
        _fakeTargetResolver.TargetResult.ProcessId = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[11] = 4321;
        _fakeUia.ScreenshotThrow = new InvalidOperationException("capture failed");
        var calls = new List<(int Pid, bool Hidden, int CapturesSoFar)>();
        var (command, _) = CreateDevToolsScreenshotCommand(calls, 4321);

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", ShotPath()]);

        Assert.AreEqual(1, exitCode);
        CollectionAssert.AreEqual(new[] { (4321, true, 0), (4321, false, 1) }, calls);
    }

    [TestMethod]
    public async Task Screenshot_OfAnAppWithoutDevTools_DoesNotTouchIt()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 11;
        _fakeTargetResolver.TargetResult.ProcessId = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[11] = 4321;
        var calls = new List<(int Pid, bool Hidden, int CapturesSoFar)>();
        var (command, _) = CreateDevToolsScreenshotCommand(calls, 999);

        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", ShotPath()]));
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Screenshot_LeavesOutTheInspectorAndTheCommentFlyout_OnlyWhenDevToolsIsAttached(bool devTools)
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 11;
        _fakeTargetResolver.TargetResult.ProcessId = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[11] = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[12] = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[13] = 4321;
        // The flyout is one of the app's own top-level windows, owned by the window it opened over.
        _fakeUia.WindowsByPidResult =
            [((nint)11, 4321, "Main"), ((nint)12, 4321, "PopupHost"), ((nint)13, 4321, "WinApp DevTools \u00B7 PID 4321")];
        _fakeSystemQuery.WindowOwnerByHwnd[12] = 11;
        _fakeUia.InspectResult = [new UiElement { Id = "e0", Type = "Window", AutomationId = "DevToolsSelPopup" }];
        var calls = new List<(int Pid, bool Hidden, int CapturesSoFar)>();
        var (command, _) = CreateDevToolsScreenshotCommand(calls, devTools ? [4321] : []);

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", ShotPath()]);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.AreEquivalent(devTools ? new long[] { 11 } : new long[] { 11, 12, 13 },
            _fakeUia.ScreenshotCalls.Select(c => c.Hwnd).ToArray());
    }
}
