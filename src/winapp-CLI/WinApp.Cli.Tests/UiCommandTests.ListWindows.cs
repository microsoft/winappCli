// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <c>winapp ui list-windows</c> beyond the existing JSON/ShouldIncludeWindow tests: the
/// non-JSON render path, the app-resolution branches (PID, exact process name, partial process
/// name, title fallback), safe process-name resolution, and the generic error branch. Window
/// handles are synthetic; <c>UiTargetResolver.GetWindowInfo</c> returns safe defaults for them.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task ListWindows_NonJson_RendersRows()
    {
        var me = Process.GetCurrentProcess();
        _fakeUia.WindowsByTitleResult =
        [
            ((nint)0x1111, me.Id, "Alpha Window"),   // real PID → GetProcessNameSafe success path
            ((nint)0x2222, 999_999, "Beta Window"),  // bogus PID → GetProcessNameSafe catch → "Unknown"
        ];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Alpha Window");
        StringAssert.Contains(TestAnsiConsole.Output, "Beta Window");
        StringAssert.Contains(TestAnsiConsole.Output, "HWND");
    }

    [TestMethod]
    public async Task ListWindows_AppAsPid_UsesFindWindowsByPid()
    {
        _fakeUia.WindowsByPidResult = [((nint)0x3333, 4242, "By PID")];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "4242"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "By PID");
    }

    [TestMethod]
    public async Task ListWindows_AppAsExactProcessName_UsesFindWindowsByPid()
    {
        // Using the current process's exact name guarantees GetProcessesByName returns >= 1 match,
        // driving the exact-name branch. FindWindowsByPid (fake) returns an empty list per process.
        var me = Process.GetCurrentProcess();
        _fakeUia.WindowsByPidResult = [];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", me.ProcessName]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task ListWindows_AppAsPartialProcessName_UsesFindWindowsByPid()
    {
        // A middle substring of the current process name won't match any process name exactly, so the
        // exact-name lookup returns 0 and the partial (Contains) branch runs and matches this process.
        var name = Process.GetCurrentProcess().ProcessName;
        var partial = name.Length > 2 ? name.Substring(1, name.Length - 2) : name;
        _fakeUia.WindowsByPidResult = [];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", partial]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task ListWindows_AppUnmatched_FallsBackToTitleSearch()
    {
        // A name that matches no process (exact or partial) falls through to FindWindowsByTitle.
        _fakeUia.WindowsByTitleResult = [((nint)0x4444, 4242, "Title Match")];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "zz-no-such-process-name-zz-9x8y7z"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Title Match");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ListWindows_MatchedProcessWithoutWindows_ListsHostingFrameOnly(bool byPid)
    {
        // A packaged app's process (e.g. CalculatorApp) owns no window; ApplicationFrameHost owns the
        // frame hosting its content. List that frame, never another app's title match.
        var me = Process.GetCurrentProcess();
        _fakeUia.WindowsByPidResult = [];
        _fakeUia.WindowsByTitleResult = [((nint)0x6666, 4343, "Editor Match - Visual Studio Code")];
        SystemUiQuery.s_findHostedAppFrames = pid => pid == me.Id ? [0x5555L] : [];
        SystemUiQuery.s_getProcessIdForWindow = _ => 4242;
        SystemUiQuery.s_getWindowText = hwnd => hwnd == 0x5555 ? "Hosting Frame" : null;
        try
        {
            var command = GetRequiredService<UiListWindowsCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(command,
                ["-a", byPid ? me.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : me.ProcessName]);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(TestAnsiConsole.Output, "Hosting Frame");
            Assert.IsFalse(TestAnsiConsole.Output.Contains("Editor Match"), "Title matches belong to another app.");
        }
        finally
        {
            SystemUiQuery.ResetNativeSeams();
        }
    }

    [TestMethod]
    public async Task ListWindows_Generic_ReturnsError()
    {
        _fakeUia.FindWindowsThrow = FakeGenericException;
        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }
}
