// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
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
    public async Task ListWindows_Generic_ReturnsError()
    {
        _fakeUia.FindWindowsThrow = FakeGenericException;
        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }
}
