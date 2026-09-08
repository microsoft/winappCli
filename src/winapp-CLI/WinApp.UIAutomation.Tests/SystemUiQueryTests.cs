// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class SystemUiQueryTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => SystemUiQuery.ResetNativeSeams();

    [TestMethod]
    public void Capture_ReturnsSnapshotWhenAllAccessorsSucceed()
    {
        var info = SystemUiQuery.Capture(
            42,
            () => "proc",
            () => 123,
            () => "title");

        Assert.AreEqual(42, info.Id);
        Assert.AreEqual("proc", info.ProcessName);
        Assert.AreEqual((nint)123, info.MainWindowHandle);
        Assert.AreEqual("title", info.MainWindowTitle);
    }

    [TestMethod]
    public void Capture_UsesSafeDefaultsWhenAccessorsThrow()
    {
        var info = SystemUiQuery.Capture(
            42,
            () => throw new InvalidOperationException("name denied"),
            () => throw new InvalidOperationException("handle denied"),
            () => throw new InvalidOperationException("title denied"));

        Assert.AreEqual(42, info.Id);
        Assert.AreEqual(string.Empty, info.ProcessName);
        Assert.AreEqual(0, info.MainWindowHandle);
        Assert.IsNull(info.MainWindowTitle);
    }

    [TestMethod]
    public void GetProcessById_ReturnsNullForMissingProcess()
    {
        var result = new SystemUiQuery().GetProcessById(-12345);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetProcessById_CapturesCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();

        var result = new SystemUiQuery().GetProcessById(current.Id);

        Assert.IsTrue(result.HasValue);
        Assert.AreEqual(current.Id, result.Value.Id);
        Assert.AreEqual(current.ProcessName, result.Value.ProcessName);
    }

    [TestMethod]
    public void GetProcessesByName_CapturesAndDisposesMatchingProcesses()
    {
        using var current = Process.GetCurrentProcess();

        var results = new SystemUiQuery().GetProcessesByName(current.ProcessName);

        Assert.IsTrue(results.Any(p => p.Id == current.Id && p.ProcessName == current.ProcessName));
    }

    [TestMethod]
    public void GetProcessesMatching_ReturnsCaseInsensitiveSubstringMatches()
    {
        using var current = Process.GetCurrentProcess();
        var substring = current.ProcessName[..Math.Max(1, current.ProcessName.Length / 2)].ToUpperInvariant();

        var results = new SystemUiQuery().GetProcessesMatching(substring);

        Assert.IsTrue(results.Any(p => p.Id == current.Id));
    }

    [TestMethod]
    public void WindowAdapters_ReturnValuesFromInjectedNativeSeams()
    {
        SystemUiQuery.s_getForegroundWindow = () => 101;
        SystemUiQuery.s_getProcessIdForWindow = hwnd => (uint)(hwnd + 1);
        SystemUiQuery.s_getWindowText = hwnd => $"title-{hwnd}";
        SystemUiQuery.s_getWindowClassName = hwnd => $"class-{hwnd}";
        SystemUiQuery.s_getWindowSize = hwnd => ((int)hwnd, (int)hwnd + 2);
        SystemUiQuery.s_getWindowOwner = hwnd => (nint)(hwnd + 3);
        SystemUiQuery.s_getFocusedWindow = hwnd => hwnd + 4;
        SystemUiQuery.s_getRootWindow = hwnd => hwnd + 5;
        var query = new SystemUiQuery();

        Assert.AreEqual((nint)101, query.GetForegroundWindow());
        Assert.AreEqual(202u, query.GetProcessIdForWindow(201));
        Assert.AreEqual("title-301", query.GetWindowText(301));
        Assert.AreEqual("class-401", query.GetWindowClassName(401));
        Assert.AreEqual((501, 503), query.GetWindowSize(501));
        Assert.AreEqual((nint)604, query.GetWindowOwner(601));
        Assert.AreEqual(705, query.GetFocusedWindow(701));
        Assert.AreEqual(806, query.GetRootWindow(801));
    }

    [TestMethod]
    public void WindowAdapters_ReturnSafeDefaultsWhenInjectedSeamsThrow()
    {
        SystemUiQuery.s_getWindowText = _ => throw new InvalidOperationException("text failed");
        SystemUiQuery.s_getWindowClassName = _ => throw new InvalidOperationException("class failed");
        SystemUiQuery.s_getWindowSize = _ => throw new InvalidOperationException("rect failed");
        SystemUiQuery.s_getWindowOwner = _ => throw new InvalidOperationException("owner failed");
        SystemUiQuery.s_getFocusedWindow = _ => throw new InvalidOperationException("focus failed");
        SystemUiQuery.s_getRootWindow = _ => throw new InvalidOperationException("root failed");
        var query = new SystemUiQuery();

        Assert.IsNull(query.GetWindowText(1));
        Assert.IsNull(query.GetWindowClassName(1));
        Assert.AreEqual((0, 0), query.GetWindowSize(1));
        Assert.AreEqual((nint)0, query.GetWindowOwner(1));
        Assert.AreEqual(0, query.GetFocusedWindow(1));
        Assert.AreEqual(0, query.GetRootWindow(1));
    }

    [TestMethod]
    public void NativeWindowSeams_HandleNullWindowWithoutMutation()
    {
        SystemUiQuery.ResetNativeSeams();
        var query = new SystemUiQuery();

        _ = query.GetForegroundWindow();
        Assert.AreEqual(0u, query.GetProcessIdForWindow(0));
        Assert.IsNull(query.GetWindowText(0));
        Assert.IsNull(query.GetWindowClassName(0));
        Assert.AreEqual((0, 0), query.GetWindowSize(0));
        Assert.AreEqual((nint)0, query.GetWindowOwner(0));
        Assert.AreEqual(0, query.GetFocusedWindow(0));
        Assert.AreEqual(0, query.GetRootWindow(0));
    }
    private static void ResetSeams()
    {
        SystemUiQuery.s_getForegroundWindow = () => 0;
        SystemUiQuery.s_getProcessIdForWindow = _ => 0;
        SystemUiQuery.s_getWindowText = _ => null;
        SystemUiQuery.s_getWindowClassName = _ => null;
        SystemUiQuery.s_getWindowSize = _ => (0, 0);
        SystemUiQuery.s_getWindowOwner = _ => 0;
        SystemUiQuery.s_getFocusedWindow = _ => 0;
        SystemUiQuery.s_getRootWindow = _ => 0;
    }
}

