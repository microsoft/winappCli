// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class RealOwnedWindowFinderTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => RealOwnedWindowFinder.ResetNativeSeams();

    [TestMethod]
    public void FindOwnedWindows_FiltersInvisibleAppWindowsUnownedAndForeignOwnedWindows()
    {
        var windows = new[] { (nint)100, (nint)200, (nint)300, (nint)400, (nint)500, (nint)600 };
        var index = -1;
        RealOwnedWindowFinder.s_findNextTopLevelWindow = after =>
        {
            if (index >= 0)
            {
                Assert.AreEqual(windows[index], (nint)after);
            }

            index++;
            return index < windows.Length ? new HWND(windows[index]) : HWND.Null;
        };
        RealOwnedWindowFinder.s_isWindowVisible = hwnd => (nint)hwnd != 200;
        RealOwnedWindowFinder.s_getWindowOwner = hwnd => (nint)hwnd switch
        {
            400 => HWND.Null,
            500 => new HWND(999),
            600 => new HWND(100),
            _ => new HWND(100)
        };
        RealOwnedWindowFinder.s_getWindowProcessId = hwnd => (int)(nint)hwnd + 1;
        RealOwnedWindowFinder.s_getWindowText = hwnd => $"title-{(nint)hwnd}";

        var result = new RealOwnedWindowFinder().FindOwnedWindows([(100, 10, "app")]);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(((nint)300, 301, "title-300"), result[0]);
        Assert.AreEqual(((nint)600, 601, "title-600"), result[1]);
    }

    [TestMethod]
    public void FindOwnedWindows_EmptyEnumerationReturnsEmptyListAndDoesNotQueryOtherSeams()
    {
        var visibleCalls = 0;
        RealOwnedWindowFinder.s_findNextTopLevelWindow = _ => HWND.Null;
        RealOwnedWindowFinder.s_isWindowVisible = _ => { visibleCalls++; return true; };

        var result = new RealOwnedWindowFinder().FindOwnedWindows([(100, 10, "app")]);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, visibleCalls);
    }

    [TestMethod]
    public void FindOwnedWindows_ExcludesEveryApplicationWindowHandle()
    {
        var windows = new[] { (nint)100, (nint)101, (nint)102 };
        var index = -1;
        var ownerCalls = 0;
        RealOwnedWindowFinder.s_findNextTopLevelWindow = _ =>
        {
            index++;
            return index < windows.Length ? new HWND(windows[index]) : HWND.Null;
        };
        RealOwnedWindowFinder.s_isWindowVisible = _ => true;
        RealOwnedWindowFinder.s_getWindowOwner = _ => { ownerCalls++; return new HWND(100); };

        var result = new RealOwnedWindowFinder().FindOwnedWindows([(100, 10, "a"), (101, 10, "b"), (102, 10, "c")]);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, ownerCalls);
    }

    [TestMethod]
    public void NativeSeams_HandleNullWindowWithoutMutation()
    {
        RealOwnedWindowFinder.ResetNativeSeams();

        // Invoke the production enumeration delegate once for coverage. We must NOT assert the
        // returned handle or compare two live calls: the topmost window is Z-order-dependent and can
        // change between calls, which would make this test flaky. The deterministic Null-handle reads
        // below are the stable, asserted invariants.
        _ = RealOwnedWindowFinder.s_findNextTopLevelWindow(HWND.Null);
        Assert.IsFalse(RealOwnedWindowFinder.s_isWindowVisible(HWND.Null));
        Assert.IsTrue(RealOwnedWindowFinder.s_getWindowOwner(HWND.Null).IsNull);
        Assert.AreEqual(0, RealOwnedWindowFinder.s_getWindowProcessId(HWND.Null));
        Assert.AreEqual(string.Empty, RealOwnedWindowFinder.s_getWindowText(HWND.Null));
    }
    private static void ResetSeams()
    {
        RealOwnedWindowFinder.s_findNextTopLevelWindow = _ => HWND.Null;
        RealOwnedWindowFinder.s_isWindowVisible = _ => true;
        RealOwnedWindowFinder.s_getWindowOwner = _ => HWND.Null;
        RealOwnedWindowFinder.s_getWindowProcessId = hwnd => (int)(nint)hwnd;
        RealOwnedWindowFinder.s_getWindowText = hwnd => string.Empty;
    }
}

