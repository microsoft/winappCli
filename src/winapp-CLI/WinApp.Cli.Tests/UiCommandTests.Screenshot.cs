// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Screenshot verb coverage: single-window capture (JSON + non-JSON), multi-window discovery and
/// compositing (by-PID, by-name, owned-dialog), capture-failure, window-handle edge cases, and the
/// COM/generic error branches. All file-writing paths target a temp path so no PNG lands in the cwd.
/// Multi-window discovery is driven entirely through the fakes (FindWindowsByPid / IOwnedWindowFinder)
/// so no real desktop window is required.
/// </summary>
public partial class UiCommandTests
{
    private string ShotPath() => Path.Combine(_tempDirectory.FullName, "shot.png");

    [TestMethod]
    public async Task Screenshot_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_SingleWindow_NonJson_WritesFile()
    {
        // Selector present → multi-window discovery is skipped; single element crop path (logs, no JSON).
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path), "expected a PNG to be written to the output path");
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowByPid_Json_EmitsWindowsArray()
    {
        // Integer --app → FindWindowsByPid; two windows → composite multi-window capture.
        _fakeUia.WindowsByPidResult = [((nint)11, 4321, "Main"), ((nint)12, 4321, "Dialog")];
        // Both handles really do belong to the discovered process, which is what the OS reports when
        // the command revalidates them before capturing.
        _fakeSystemQuery.ProcessIdByHwnd[11] = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[12] = 4321;
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"captured\": true");
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "the whole multi-window capture pass must run inside one desktop section");
        Assert.AreEqual(0, _fakeDesktopLock.OpenDesktopSections,
            "the section must be closed before the command reports the written file");
    }

    [TestMethod]
    public async Task Screenshot_MultiWindow_RecycledHandle_IsSkippedNotComposited()
    {
        // Every handle in the composite was discovered before the command queued for the desktop. If
        // one of those windows closed while waiting and Windows reused its handle, capturing it would
        // splice an unrelated application's pixels into an image reported as this target's — and the
        // per-window "captured" flag would claim it succeeded.
        _fakeUia.WindowsByPidResult = [((nint)31, 4321, "Main"), ((nint)32, 4321, "Doomed")];
        _fakeSystemQuery.ProcessIdByHwnd[31] = 4321;
        _fakeSystemQuery.ProcessIdByHwnd[32] = 9999;   // recycled onto an unrelated process
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode, "the surviving window is still captured");

        var output = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var windows = output.GetProperty("windows").EnumerateArray().ToList();
        Assert.HasCount(2, windows, "both windows are reported, so the omission is visible");

        var recycled = windows.Single(w => w.GetProperty("hwnd").GetInt64() == 32);
        Assert.IsFalse(recycled.GetProperty("captured").GetBoolean(),
            "a recycled handle must never be reported as captured");
        StringAssert.Contains(recycled.GetProperty("error").GetString()!, "different process");

        var survivor = windows.Single(w => w.GetProperty("hwnd").GetInt64() == 31);
        Assert.IsTrue(survivor.GetProperty("captured").GetBoolean());

        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "the whole multi-window capture pass must run inside one desktop section");
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowByName_NonJson_Composites()
    {
        // Exact current-process-name match → real process enumeration hits FindWindowsByPid per process.
        var procName = Process.GetCurrentProcess().ProcessName;
        _fakeUia.WindowsByPidResult = [((nint)21, 100, "A"), ((nint)22, 100, "B")];
        _fakeSystemQuery.ProcessIdByHwnd[21] = 100;
        _fakeSystemQuery.ProcessIdByHwnd[22] = 100;
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", procName, "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "windows detected");
        StringAssert.Contains(TestAnsiConsole.Output, "Saved composite");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_MultiWindowAllCapturesFail_ReturnsError()
    {
        // Two windows discovered but every ScreenshotAsync throws → captures.Count == 0 → error.
        _fakeUia.WindowsByPidResult = [((nint)31, 4321, "Main"), ((nint)32, 4321, "Dialog")];
        _fakeUia.ScreenshotThrow = FakeGenericException;
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4321", "-o", path]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "✗");
    }

    [TestMethod]
    public async Task Screenshot_ForegroundLost_WritesNoArtifact()
    {
        _fakeUia.ScreenshotThrow = new ForegroundLostException("target lost foreground");
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--capture-screen", "--json", "-o", path]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCodeIn(ConsoleStdErr.ToString(), UiJsonError.CodeForegroundNotTarget);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_SingleWindowWithOwnedDialog_Composites()
    {
        // No selector, no multi-window from discovery, but a cross-process owned dialog exists →
        // the single-session path detects it and composites session + owned window.
        _fakeWindowFinder.OwnedWindowsResult = [((nint)99, 4321, "Owned Dialog")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_SingleWindow_NoSelector_NoDialog_WritesFile()
    {
        // No selector and no owned dialog: DiscoverAllWindows finds nothing to composite (returns null),
        // ResolveAsync yields the single session, and the plain single-window capture runs
        // (non-JSON → LogInformation, writes the PNG). This is the common "screenshot an app by name"
        // path that the two invalid-handle tests below no longer reach.
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path), "expected a PNG to be written for a single-window capture");
    }

    [TestMethod]
    public async Task Screenshot_WindowZero_NoApp_ReturnsErrorWithoutCapturing()
    {
        // --window 0 with no --app: the missing-app guard passes (window is not null) and
        // DiscoverAllWindows returns null (0 is not > 0, no app). Production then rejects it at
        // ResolveAsync — hwnd is not > 0 and --app is blank, so UiTargetResolver throws
        // "Specify --app..." (proven directly by UiSessionServiceTests.ResolveSession_WhitespaceApp_
        // ZeroHwnd_Throws). The command maps that to exit 1 and writes NO screenshot. The fake models
        // the same throw so the assertion reflects real behavior instead of the fake accepting hwnd 0.
        _fakeTargetResolver.ResolveThrow = new InvalidOperationException(
            "Specify --app (process name, title, or PID) or --window (HWND).");
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "0", "-o", path]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(path), "no screenshot should be written when the target is rejected");
    }

    [TestMethod]
    public async Task Screenshot_WindowInvalidHandle_ReturnsErrorWithoutCapturing()
    {
        // --window <dead hwnd>: DiscoverAllWindows calls GetProcessIdForWindow, which yields 0 for an
        // invalid handle (fake default) → the pid==0 guard bails → single-window path. Production then
        // rejects it at ResolveByHwnd because the PID is still 0, throwing "Window HWND ... not found or
        // not accessible" (proven by UiSessionServiceTests.ResolveByHwnd_WindowNotFound_Throws). The
        // command maps that to exit 1 and writes NO screenshot. The fake models the same throw so the
        // test asserts real rejection rather than the fake accepting any handle.
        _fakeTargetResolver.ResolveThrow = new InvalidOperationException(
            "Window HWND 999999 not found or not accessible.");
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "999999", "-o", path]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(path), "no screenshot should be written for an invalid window handle");
    }

    [TestMethod]
    public async Task Screenshot_ComException_ReturnsStaleError()
    {
        _fakeUia.ScreenshotThrow = FakeComException;

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_GenericException_ReturnsGenericError()
    {
        // A non-COM exception from the single-window capture falls through to the catch-all, which maps
        // it to the generic-error envelope (mirrors the COMException stale-element path above).
        _fakeUia.ScreenshotThrow = FakeGenericException;

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Screenshot_WindowHandleValid_WithOwnedDialog_CompositesThroughSeam()
    {
        // Valid --window <hwnd>: ISystemUiQuery resolves a non-zero PID + title (what a live handle
        // yields on a real desktop) and a cross-process owned dialog exists → DiscoverAllWindows
        // returns two windows through the seam → multi-window composite. Exercises the direct-HWND
        // GetProcessIdForWindow / GetWindowText reads that were previously a native ceiling.
        _fakeSystemQuery.ProcessIdForWindowResult = 4321;
        _fakeSystemQuery.WindowTextResult = "Main Window";
        _fakeWindowFinder.OwnedWindowsResult = [((nint)0xD1A, 4321, "Owned Dialog")];
        // A real owned dialog reports the app window as its GW_OWNER; that link is what attributes it
        // to this application rather than to whatever process happens to host it.
        _fakeSystemQuery.WindowOwnerByHwnd[0xD1A] = (nint)2748;
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "2748", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        StringAssert.Contains(TestAnsiConsole.Output, "Main Window");   // title read through GetWindowText seam
        StringAssert.Contains(TestAnsiConsole.Output, "Owned Dialog");  // the second, owned window
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task Screenshot_WindowHandleValid_NoOwnedDialog_CapturesSingle()
    {
        // Valid --window <hwnd> but no owned windows → DiscoverAllWindows finds a single window and
        // returns null (Count is not > 1), so the single-window capture path runs. Still exercises the
        // seam's GetProcessIdForWindow / GetWindowText reads before the single-window fall-through.
        _fakeSystemQuery.ProcessIdForWindowResult = 4321;
        _fakeSystemQuery.WindowTextResult = "Solo Window";
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
        var path = ShotPath();

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-w", "2748", "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(path));
    }
}
