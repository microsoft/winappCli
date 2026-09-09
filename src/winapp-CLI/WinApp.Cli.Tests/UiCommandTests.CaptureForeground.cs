// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Command-level mapping of a failed screen-capture foreground verification (issue #764 re-review H6).
/// </summary>
/// <remarks>
/// <c>--capture-screen</c> BitBlts the live screen rather than a specific window, so it records
/// whatever is actually in front. <c>SetForegroundWindow</c> is only a request and Windows refuses it
/// under focus-stealing prevention, a UAC prompt, a locked session, or when another app activates
/// itself in the same instant. A published repro pointed at a green target while a magenta decoy held
/// the foreground: the command exited 0 and the PNG's centre pixel was magenta. Silently handing back
/// an image of the wrong app is worse than failing, because the caller cannot tell.
/// <para>
/// The verification itself lives in <c>UiAutomationService</c> and is covered in
/// <see cref="UiAutomationServicePureTests"/>; these tests pin the contract callers see — the existing
/// <c>foreground_not_target</c> code rather than <c>internal_error</c>, and no artifact written.
/// </para>
/// </remarks>
public partial class UiCommandTests
{
    private static ForegroundLostException CaptureRefusal()
        => new("Target window is not in the foreground — refusing to capture the screen.");

    [TestMethod]
    public async Task Screenshot_CaptureScreenForegroundRefused_ReportsForegroundNotTarget()
    {
        _fakeUia.ScreenshotThrow = CaptureRefusal();

        var outputPath = Path.Combine(_tempDirectory.FullName, "decoy.png");
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--capture-screen", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeForegroundNotTarget);
        Assert.IsFalse(File.Exists(outputPath),
            "no image may be published when the capture would have recorded the wrong window");
    }

    [TestMethod]
    public async Task Record_CaptureScreenForegroundRefused_ReportsForegroundNotTarget()
    {
        _fakeRecording.RecordException = CaptureRefusal();

        var outputPath = Path.Combine(_tempDirectory.FullName, "decoy.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--capture-screen", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeForegroundNotTarget);
        Assert.IsFalse(File.Exists(outputPath), "no MP4 may be published for a refused capture");
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowCaptureScreenForegroundRefused_ReportsForegroundNotTarget()
    {
        // The multi-window path captures each window in a loop whose catch-all records per-window
        // failures and then reports "No windows could be captured" as internal_error. A refused
        // activation is a property of the desktop, not of one window, so it must escape that loop —
        // otherwise --capture-screen on any app with an owned dialog still buries the real cause.
        _fakeWindowFinder.OwnedWindowsResult = [((nint)99, 4321, "Owned Dialog")];
        _fakeUia.ScreenshotThrow = CaptureRefusal();

        var outputPath = Path.Combine(_tempDirectory.FullName, "decoy-multi.png");
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--capture-screen", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeForegroundNotTarget);
        Assert.IsFalse(File.Exists(outputPath));
    }
}
