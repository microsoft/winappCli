// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// <c>--capture-screen</c> against an app with more than one window.
/// </summary>
/// <remarks>
/// Live-screen capture reads pixels off the screen, so it records whatever is actually in front.
/// Only one window can be in front, which makes "capture the screen for each of these five windows
/// and composite them" unsatisfiable: at best it stitches together moments where each window covered
/// the others, and in practice Windows refuses the second activation and the command dies with a bare
/// <c>foreground_not_target</c> that says nothing about the real problem. The command has to name the
/// ambiguity instead, before it foregrounds or captures anything.
/// </remarks>
public partial class UiCommandTests
{
    private const int MultiWindowPid = 7788;
    private const nint FirstWindow = 0x200;
    private const nint SecondWindow = 0x201;

    /// <summary>
    /// Points the command at an app whose process has two top-level windows.
    /// </summary>
    /// <remarks>
    /// Targeted by PID rather than by name: name lookup goes through the real process table, while
    /// <c>-a &lt;pid&gt;</c> routes through <c>FindWindowsByPid</c>, which the fake drives.
    /// </remarks>
    private void ArrangeTwoTopLevelWindows()
    {
        _fakeSystemQuery.ProcessIdForWindowResult = MultiWindowPid;
        _fakeSystemQuery.ProcessIdByHwnd[FirstWindow] = MultiWindowPid;
        _fakeSystemQuery.ProcessIdByHwnd[SecondWindow] = MultiWindowPid;
        _fakeSystemQuery.WindowTextResult = "Main Window";
        _fakeUia.WindowsByPidResult =
        [
            (FirstWindow, MultiWindowPid, "Main Window"),
            (SecondWindow, MultiWindowPid, "Second Window"),
        ];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
    }

    private static string App => MultiWindowPid.ToString();

    [TestMethod]
    public async Task Screenshot_CaptureScreenWithSeveralWindows_IsRejectedBeforeAnyCapture()
    {
        ArrangeTwoTopLevelWindows();
        var command = GetRequiredService<UiScreenshotCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", App, "--capture-screen", "--json", "-o", ShotPath()]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("invalid_arguments");
        Assert.AreEqual(0, _fakeUia.ScreenshotCalls.Count,
            "the failure must land before any window is foregrounded or captured");
    }

    [TestMethod]
    public async Task Screenshot_CaptureScreenAmbiguity_PointsAtListWindowsAndTheWindowFlag()
    {
        // The reviewer's report was that the generic foreground error left no way to work out what to
        // do next. The recovery hint has to name both halves of the fix.
        ArrangeTwoTopLevelWindows();
        var command = GetRequiredService<UiScreenshotCommand>();

        await ParseAndInvokeWithCaptureAsync(
            command, ["-a", App, "--capture-screen", "--json", "-o", ShotPath()]);

        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "list-windows");
        StringAssert.Contains(stderr, "-w <hwnd>");
    }

    [TestMethod]
    public async Task Screenshot_CaptureScreenForOneExplicitWindow_StillWorks()
    {
        // -w is the documented fix, so it must not be caught by the same guard.
        ArrangeTwoTopLevelWindows();
        var command = GetRequiredService<UiScreenshotCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-w", SecondWindow.ToString(), "--capture-screen", "--json", "-o", ShotPath()]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeUia.ScreenshotCalls.Count);
        Assert.IsTrue(_fakeUia.ScreenshotCalls[0].CaptureScreen);
    }

    [TestMethod]
    public async Task Screenshot_SeveralWindowsWithoutCaptureScreen_StillComposites()
    {
        // Window-content capture does not read the screen, so several windows compose fine. Rejecting
        // this too would have broken the feature the multi-window path exists for.
        ArrangeTwoTopLevelWindows();
        var command = GetRequiredService<UiScreenshotCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", App, "--json", "-o", ShotPath()]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(2, _fakeUia.ScreenshotCalls.Count, "both windows must still be captured");
    }

    [TestMethod]
    public async Task Screenshot_CaptureScreenWithAnOwnedDialog_IsAlsoRejected()
    {
        // The other way a capture becomes multi-window: one app window plus a dialog it owns. Same
        // ambiguity, same guard — the app window and its file picker cannot both be in front.
        ArrangeOwnedDialog(ownerOfDialog: AppWindow);
        var command = GetRequiredService<UiScreenshotCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-w", AppWindow.ToString(), "--capture-screen", "--json", "-o", ShotPath()]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("invalid_arguments");
        Assert.AreEqual(0, _fakeUia.ScreenshotCalls.Count);
    }
}
