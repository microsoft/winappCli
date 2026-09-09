// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Where a captured window came from, and why that is not the same question as who owns it now.
/// </summary>
/// <remarks>
/// A screenshot deliberately includes the dialogs an app owns — common-item file pickers, print and
/// security dialogs — and those run in <em>another</em> process. So a captured window's current PID
/// is not evidence of anything: for an owned dialog it is a shared system host that also runs
/// dialogs for unrelated applications. Checking the handle against that PID accepts any live window
/// in the host, including one whose handle was recycled after the real dialog closed. The check has
/// to be against the application the window was discovered for, so the owner chain has to still
/// reach it.
/// </remarks>
public partial class UiCommandTests
{
    private const int AppPid = 4321;
    private const int SystemHostPid = 9100;
    private const nint AppWindow = 0x100;
    private const nint OwnedDialog = 0xD1A;

    /// <summary>Points the command at a single app window that owns one cross-process dialog.</summary>
    private void ArrangeOwnedDialog(nint ownerOfDialog)
    {
        _fakeSystemQuery.ProcessIdForWindowResult = AppPid;
        _fakeSystemQuery.WindowTextResult = "Main Window";
        _fakeSystemQuery.ProcessIdByHwnd[AppWindow] = AppPid;

        // The dialog belongs to a shared system host, not to the app.
        _fakeSystemQuery.ProcessIdByHwnd[OwnedDialog] = SystemHostPid;
        _fakeSystemQuery.WindowOwnerByHwnd[OwnedDialog] = ownerOfDialog;

        _fakeWindowFinder.OwnedWindowsResult = [(OwnedDialog, SystemHostPid, "Save As")];
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);
    }

    private async Task<System.Text.Json.JsonElement> CaptureOwnedDialogAsync()
    {
        var path = ShotPath();
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-w", AppWindow.ToString(), "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
    }

    [TestMethod]
    public async Task Screenshot_LiveCrossProcessOwnedDialog_IsCaptured()
    {
        // The dialog runs in another process and is still owned by the app window, which is exactly the
        // case the validation must not break: a file picker is part of the app's UI.
        ArrangeOwnedDialog(ownerOfDialog: AppWindow);

        var output = await CaptureOwnedDialogAsync();
        var windows = output.GetProperty("windows").EnumerateArray().ToList();

        var dialog = windows.Single(w => w.GetProperty("hwnd").GetInt64() == OwnedDialog);
        Assert.IsTrue(dialog.GetProperty("captured").GetBoolean(),
            "an owned dialog in another process is legitimately part of this app's UI");
    }

    [TestMethod]
    public async Task Screenshot_OwnedDialogHandleReusedInsideTheSameHost_IsNotCaptured()
    {
        // The regression. The real dialog closed and its handle was reused by an unrelated window in
        // the SAME system host, so the handle still resolves to the host's PID and a check against
        // that PID passes — while the owner link back to the app is gone. Under the old check this
        // window was composited into an image labelled as this application's.
        ArrangeOwnedDialog(ownerOfDialog: 0);   // no owner: the relationship to the app no longer exists

        var output = await CaptureOwnedDialogAsync();
        var windows = output.GetProperty("windows").EnumerateArray().ToList();

        Assert.IsFalse(
            windows.Any(w => w.GetProperty("hwnd").GetInt64() == OwnedDialog),
            "a handle that can no longer be traced back to the app must not be captured as its window");
    }

    [TestMethod]
    public async Task Screenshot_OwnedDialogNowOwnedByAnUnrelatedWindow_IsNotCaptured()
    {
        // Same shape, but the recycled handle has acquired a different owner rather than none. It still
        // cannot be attributed to this application.
        ArrangeOwnedDialog(ownerOfDialog: 0x999);
        _fakeSystemQuery.ProcessIdByHwnd[0x999] = SystemHostPid;

        var output = await CaptureOwnedDialogAsync();
        var windows = output.GetProperty("windows").EnumerateArray().ToList();

        Assert.IsFalse(
            windows.Any(w => w.GetProperty("hwnd").GetInt64() == OwnedDialog),
            "an owner outside the app's own windows proves nothing about provenance");
    }

    [TestMethod]
    public async Task Screenshot_DirectSamePidWindows_AreStillCaptured()
    {
        // The app's own windows expect their own process, so the stricter owned-window rule must not
        // catch them.
        _fakeUia.WindowsByPidResult = [((nint)41, AppPid, "Main"), ((nint)42, AppPid, "Tool")];
        _fakeSystemQuery.ProcessIdByHwnd[41] = AppPid;
        _fakeSystemQuery.ProcessIdByHwnd[42] = AppPid;
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);

        var path = ShotPath();
        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", AppPid.ToString(), "--json", "-o", path]);

        Assert.AreEqual(0, exitCode);
        var output = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var windows = output.GetProperty("windows").EnumerateArray().ToList();

        Assert.HasCount(2, windows);
        Assert.IsTrue(windows.All(w => w.GetProperty("captured").GetBoolean()),
            "an app's own windows are validated against their own process and must still be captured");
    }
}
