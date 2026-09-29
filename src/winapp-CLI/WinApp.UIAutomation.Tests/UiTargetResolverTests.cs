// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Direct unit tests for <see cref="UiTargetResolver"/>. All OS boundaries (process enumeration and
/// Win32 window queries) are driven through <see cref="FakeSystemUiQuery"/>, and window discovery
/// through <see cref="FakeUiAutomationService"/>, so every resolver branch is exercised
/// deterministically without a live desktop or specific running processes.
/// </summary>
[TestClass]
public class UiSessionServiceTests
{
    private static (UiTargetResolver Service, FakeUiAutomationService Uia, FakeSystemUiQuery Sys) NewService()
    {
        var uia = new FakeUiAutomationService();
        var sys = new FakeSystemUiQuery();
        var service = new UiTargetResolver(uia, sys, NullLogger<UiTargetResolver>.Instance);
        return (service, uia, sys);
    }

    [TestMethod]
    [DataRow("500")]
    [DataRow("myapp")]
    public async Task ResolveProcess_DoesNotChoosePopupOrMainWindow(string app)
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[500] = new UiProcessInfo(500, "myapp", 100, "Main");
        sys.ByNameResult = [sys.ProcessesById[500]!.Value];
        uia.WindowsByPidResult = [(100, 500, "Main"), (200, 500, "Popup")];
        uia.FindWindowsThrow = new AssertFailedException("Process-only targeting must not discover or choose an HWND.");
        sys.ForegroundWindowResult = 200;
        var target = await service.ResolveProcessAsync(app, CancellationToken.None);
        Assert.AreEqual(500, target.ProcessId);
        Assert.AreEqual(0L, target.WindowHandle);
        Assert.IsFalse(target.IsExplicitWindow);
        Assert.IsNull(target.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveProcess_SameProcessTitleMatchesNeedNoWindowChoice()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[500] = new UiProcessInfo(500, "myapp", 100, "Main");
        uia.WindowsByTitleResult = [(100, 500, "Shared main"), (200, 500, "Shared popup")];
        var target = await service.ResolveProcessAsync("Shared", CancellationToken.None);
        Assert.AreEqual(500, target.ProcessId);
        Assert.AreEqual(0L, target.WindowHandle);
    }

    [TestMethod]
    public async Task ResolveProcess_SharedTitleAcrossProcessesRefusesForegroundGuess()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [(100, 500, "Shared"), (200, 600, "Shared")];
        sys.ForegroundWindowResult = 200;
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ResolveProcessAsync("Shared", CancellationToken.None));
        StringAssert.Contains(error.Message, "500, 600");
        StringAssert.Contains(error.Message, "--app with a PID");
        Assert.IsFalse(error.Message.Contains("--window", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UiTarget_IsExplicitWindow_DefaultsToFalse()
    {
        var info = new UiTarget();
        Assert.IsFalse(info.IsExplicitWindow);
    }

    // ---- HWND resolution -------------------------------------------------

    [TestMethod]
    public async Task ResolveByHwnd_WindowNotFound_Throws()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 0; // window not found / not accessible

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveAsync(app: null, hwnd: 0x9999, CancellationToken.None));

        StringAssert.Contains(ex.Message, "not found or not accessible");
    }

    [TestMethod]
    public async Task ResolveByHwnd_Found_SetsExplicitAndTitleFromWindowText()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 4321;
        sys.ProcessesById[4321] = new UiProcessInfo(4321, "notepad", 0, null);
        sys.WindowTextResult = "Untitled - Notepad";

        var uiTarget = await service.ResolveAsync(app: null, hwnd: 0x1234, CancellationToken.None);

        Assert.IsTrue(uiTarget.IsExplicitWindow,
            "Sessions resolved via --window must be marked explicit so inspect/search/find don't expand to other windows (#472).");
        Assert.AreEqual((long)0x1234, uiTarget.WindowHandle);
        Assert.AreEqual(4321, uiTarget.ProcessId);
        Assert.AreEqual("notepad", uiTarget.ProcessName);
        Assert.AreEqual("Untitled - Notepad", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByHwnd_Found_NoWindowText_LeavesTitleNull()
    {
        var (service, _, sys) = NewService();
        sys.ProcessIdForWindowResult = 10;
        sys.DefaultProcessById = new UiProcessInfo(0, "proc", 0, null);
        sys.WindowTextResult = null; // empty/unavailable title

        var uiTarget = await service.ResolveAsync(app: null, hwnd: 0x1000, CancellationToken.None);

        Assert.IsTrue(uiTarget.IsExplicitWindow);
        Assert.AreEqual("proc", uiTarget.ProcessName);
        Assert.IsNull(uiTarget.WindowTitle);
    }

    // ---- Missing selector ------------------------------------------------

    [TestMethod]
    public async Task ResolveSession_NoAppNoWindow_Throws()
    {
        var (service, _, _) = NewService();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveAsync(app: null, hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Specify --app");
    }

    [TestMethod]
    public async Task ResolveSession_WhitespaceApp_ZeroHwnd_Throws()
    {
        var (service, _, _) = NewService();

        // hwnd 0 is not "> 0", so it falls through to the app check.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveAsync(app: "   ", hwnd: 0, CancellationToken.None));
    }

    // ---- PID resolution --------------------------------------------------

    [TestMethod]
    public async Task ResolveByPid_ProcessNotFound_Throws()
    {
        var (service, _, _) = NewService();

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveAsync(app: "999999", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No process found with PID 999999");
    }

    [TestMethod]
    public async Task ResolveByPid_Found_NoWindows_UsesMainWindowTitle()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[500] = new UiProcessInfo(500, "myapp", 0x50, "Main Title");
        uia.WindowsByPidResult = []; // no discoverable windows

        var uiTarget = await service.ResolveAsync(app: "500", hwnd: null, CancellationToken.None);

        Assert.IsFalse(uiTarget.IsExplicitWindow,
            "Only --window should mark a target as explicit; --app/PID resolution must leave it false.");
        Assert.AreEqual(500, uiTarget.ProcessId);
        Assert.AreEqual("myapp", uiTarget.ProcessName);
        Assert.AreEqual("Main Title", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_NoWindows_EmptyMainTitle_YieldsNullTitle()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[501] = new UiProcessInfo(501, "myapp", 0x51, "");
        uia.WindowsByPidResult = [];

        var uiTarget = await service.ResolveAsync(app: "501", hwnd: null, CancellationToken.None);

        Assert.IsNull(uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_NoWindowYet_KeepsProcessTarget()
    {
        // An app that is still starting has no window yet; wait-for polls the process until one appears.
        var (service, uia, sys) = NewService();
        sys.ProcessesById[502] = new UiProcessInfo(502, "CalculatorApp", 0, "");
        uia.WindowsByPidResult = [];
        uia.WindowsByTitleResult = [((nint)0x77, 999, "502 unrelated")];
        sys.WindowClassNameByHwnd[0x77] = "ApplicationFrameWindow";

        var uiTarget = await service.ResolveAsync(app: "502", hwnd: null, CancellationToken.None);

        Assert.AreEqual(502, uiTarget.ProcessId, "A PID never falls back to a title match.");
        Assert.AreEqual(0L, (long)uiTarget.WindowHandle);
    }

    // ---- Hosted (ApplicationFrameHost) apps -------------------------------

    [TestMethod]
    public async Task ResolveByName_ProcessWithoutWindow_FallsBackToHostedFrameByTitle()
    {
        var (service, uia, sys) = NewService();
        sys.MatchingResult = [new UiProcessInfo(880, "CalculatorApp", 0, "")];
        uia.WindowsByPidResult = [];
        uia.WindowsByTitleResult =
        [
            ((nint)0x301, 881, "calculator.cs - Editor"),
            ((nint)0x302, 882, "Calculator"),
        ];
        sys.WindowClassNameByHwnd[0x301] = "Chrome_WidgetWin_1";
        sys.WindowClassNameByHwnd[0x302] = "ApplicationFrameWindow";
        sys.ProcessesById[882] = new UiProcessInfo(882, "ApplicationFrameHost", 0x302, "Calculator");

        var uiTarget = await service.ResolveAsync(app: "calculator", hwnd: null, CancellationToken.None);

        Assert.AreEqual(0x302L, uiTarget.WindowHandle, "The ApplicationFrameWindow must win over other title matches.");
        Assert.AreEqual(882, uiTarget.ProcessId);
        Assert.AreEqual("Calculator", uiTarget.WindowTitle);
        Assert.IsTrue(uiTarget.IsExplicitWindow,
            "A frame target must stay in its window: ApplicationFrameHost also owns every other packaged app's frame.");
    }

    [TestMethod]
    public async Task ResolveByName_ProcessWithoutWindow_IgnoresNonFrameTitleMatches()
    {
        // While "myapp" starts, an editor titled "myapp - Visual Studio Code" is a different app.
        var (service, uia, sys) = NewService();
        sys.ByNameResult = [new UiProcessInfo(890, "myapp", 0, null)];
        uia.WindowsByPidResult = [];
        uia.WindowsByTitleResult = [((nint)0x501, 999, "myapp - Visual Studio Code")];
        sys.WindowClassNameByHwnd[0x501] = "Chrome_WidgetWin_1";

        var uiTarget = await service.ResolveAsync(app: "myapp", hwnd: null, CancellationToken.None);

        Assert.AreEqual(890, uiTarget.ProcessId);
        Assert.AreEqual(0L, (long)uiTarget.WindowHandle);
    }

    [TestMethod]
    public async Task ResolveByTitle_PlainWindows_AreNotMarkedExplicit()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [((nint)0x401, 910, "Doc")];
        sys.WindowClassNameByHwnd[0x401] = "Notepad";
        sys.DefaultProcessById = new UiProcessInfo(0, "notepad", 0, null);

        var uiTarget = await service.ResolveAsync(app: "Doc", hwnd: null, CancellationToken.None);

        Assert.IsFalse(uiTarget.IsExplicitWindow);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_SingleWindow_CreatesSession()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[600] = new UiProcessInfo(600, "app6", 0, null);
        uia.WindowsByPidResult = [((nint)0xAAA, 600, "Win6")];

        var uiTarget = await service.ResolveAsync(app: "600", hwnd: null, CancellationToken.None);

        Assert.AreEqual(600, uiTarget.ProcessId);
        Assert.AreEqual("app6", uiTarget.ProcessName);
        Assert.AreEqual((long)0xAAA, uiTarget.WindowHandle);
        Assert.AreEqual("Win6", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_MultipleWindows_AutoSelectsForeground()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[700] = new UiProcessInfo(700, "app7", 0, null);
        uia.WindowsByPidResult = [((nint)0x100, 700, "A"), ((nint)0x200, 700, "B")];
        sys.ForegroundWindowResult = 0x200; // the second window is foreground

        var uiTarget = await service.ResolveAsync(app: "700", hwnd: null, CancellationToken.None);

        Assert.AreEqual((long)0x200, uiTarget.WindowHandle);
        Assert.AreEqual("B", uiTarget.WindowTitle);
        Assert.AreEqual("app7", uiTarget.ProcessName);
    }

    [TestMethod]
    public async Task ResolveByPid_Found_MultipleWindows_AutoSelectsLargest_WhenNoForeground()
    {
        var (service, uia, sys) = NewService();
        sys.ProcessesById[701] = new UiProcessInfo(701, "app7b", 0, null);
        uia.WindowsByPidResult = [((nint)0x111, 701, "AA"), ((nint)0x222, 701, "BB")];
        sys.ForegroundWindowResult = 0; // no foreground → "largest" heuristic
        // Distinct areas so "largest" has a single correct answer: 0x222 (300×300) dwarfs 0x111 (100×100).
        sys.WindowSizeByHwnd[0x111] = (100, 100);
        sys.WindowSizeByHwnd[0x222] = (300, 300);

        var uiTarget = await service.ResolveAsync(app: "701", hwnd: null, CancellationToken.None);

        Assert.AreEqual(701, uiTarget.ProcessId);
        Assert.AreEqual("app7b", uiTarget.ProcessName);
        Assert.AreEqual(0x222L, uiTarget.WindowHandle,
            "Auto-select must pick the largest-area window (0x222), not just any candidate.");
    }

    // ---- Exact process-name resolution ----------------------------------

    [TestMethod]
    public async Task ResolveByName_ExactSingle_ReturnsProcess()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult = [new UiProcessInfo(800, "calc", 0x80, null)];
        uia.WindowsByPidResult = [];

        var uiTarget = await service.ResolveAsync(app: "calc", hwnd: null, CancellationToken.None);

        Assert.AreEqual(800, uiTarget.ProcessId);
        Assert.AreEqual("calc", uiTarget.ProcessName);
        Assert.IsNull(uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_OneWithWindow_ReturnsThatOne()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(810, "dup", 0, null),          // no window
            new UiProcessInfo(811, "dup", 0x900, "Real"),    // has a window
        ];
        uia.WindowsByPidResult = [];

        var uiTarget = await service.ResolveAsync(app: "dup", hwnd: null, CancellationToken.None);

        Assert.AreEqual(811, uiTarget.ProcessId);
        Assert.AreEqual("Real", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_MultipleWithWindow_Throws()
    {
        var (service, _, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(820, "ambig", 0x1, "W1"),
            new UiProcessInfo(821, "ambig", 0x2, "W2"),
        ];

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveAsync(app: "ambig", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Multiple 'ambig' windows found");
        StringAssert.Contains(ex.Message, "PID 820");
        StringAssert.Contains(ex.Message, "PID 821");
    }

    [TestMethod]
    public async Task ResolveByName_ExactMultiple_NoneWithWindow_FallsToPartialMatch()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult =
        [
            new UiProcessInfo(830, "xy", 0, null),
            new UiProcessInfo(831, "xy", 0, ""),
        ];
        sys.MatchingResult = [new UiProcessInfo(832, "xyz", 0x83, null)];
        uia.WindowsByPidResult = [];

        var uiTarget = await service.ResolveAsync(app: "xy", hwnd: null, CancellationToken.None);

        Assert.AreEqual(832, uiTarget.ProcessId);
        Assert.AreEqual("xyz", uiTarget.ProcessName);
    }

    // ---- Partial process-name resolution --------------------------------

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_OneWithWindow_ReturnsWithLog()
    {
        var (service, uia, sys) = NewService();
        sys.ByNameResult = []; // no exact match
        sys.MatchingResult =
        [
            new UiProcessInfo(850, "pob", 0, null),
            new UiProcessInfo(851, "poc", 0xA, "Poc Win"),
        ];
        uia.WindowsByPidResult = [];

        var uiTarget = await service.ResolveAsync(app: "po", hwnd: null, CancellationToken.None);

        Assert.AreEqual(851, uiTarget.ProcessId);
        Assert.AreEqual("poc", uiTarget.ProcessName);
        Assert.AreEqual("Poc Win", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_MultipleWithWindow_Throws()
    {
        var (service, _, sys) = NewService();
        sys.MatchingResult =
        [
            new UiProcessInfo(860, "mpa", 0x1, "MW1"),
            new UiProcessInfo(861, "mpb", 0x2, "MW2"),
        ];

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResolveAsync(app: "mp", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Multiple processes matching 'mp' found");
        StringAssert.Contains(ex.Message, "PID 860 (mpa)");
        StringAssert.Contains(ex.Message, "PID 861 (mpb)");
    }

    [TestMethod]
    public async Task ResolveByName_PartialMultiple_NoneWithWindow_FallsToTitleSearch()
    {
        var (service, uia, sys) = NewService();
        sys.MatchingResult =
        [
            new UiProcessInfo(870, "pwa", 0, null),
            new UiProcessInfo(871, "pwb", 0, ""),
        ];
        uia.WindowsByTitleResult = []; // title search also finds nothing → throws

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveAsync(app: "pw", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No running app found matching 'pw'");
    }

    // ---- Title-search fallback ------------------------------------------

    [TestMethod]
    public async Task ResolveByTitle_NoWindows_Throws()
    {
        var (service, uia, _) = NewService();
        uia.WindowsByTitleResult = [];

        var ex = await Assert.ThrowsExactlyAsync<AppNotFoundException>(
            () => service.ResolveAsync(app: "ghost", hwnd: null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "No running app found matching 'ghost'");
    }

    [TestMethod]
    public async Task ResolveByTitle_SingleWindow_KnownProcessName()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [((nint)0xB, 900, "Ghost Win")];
        sys.DefaultProcessById = new UiProcessInfo(0, "ghostapp", 0, null);

        var uiTarget = await service.ResolveAsync(app: "ghost", hwnd: null, CancellationToken.None);

        Assert.AreEqual(900, uiTarget.ProcessId);
        Assert.AreEqual((long)0xB, uiTarget.WindowHandle);
        Assert.AreEqual("Ghost Win", uiTarget.WindowTitle);
        Assert.AreEqual("ghostapp", uiTarget.ProcessName);
    }

    [TestMethod]
    public async Task ResolveByTitle_SingleWindow_UnknownProcessName_WhenLookupFails()
    {
        var (service, uia, _) = NewService();
        uia.WindowsByTitleResult = [((nint)0xE, 960, "GW")];
        // No default and no seed for PID 960 → CreateTarget's name lookup returns null → "Unknown".

        var uiTarget = await service.ResolveAsync(app: "ghost2", hwnd: null, CancellationToken.None);

        Assert.AreEqual(960, uiTarget.ProcessId);
        Assert.AreEqual("Unknown", uiTarget.ProcessName);
        Assert.AreEqual("GW", uiTarget.WindowTitle);
    }

    [TestMethod]
    public async Task ResolveByTitle_MultipleWindows_AutoSelects()
    {
        var (service, uia, sys) = NewService();
        uia.WindowsByTitleResult = [((nint)0xC1, 910, "G1"), ((nint)0xC2, 910, "G2")];
        sys.ForegroundWindowResult = 0; // → largest heuristic
        sys.DefaultProcessById = new UiProcessInfo(0, "multi", 0, null);
        // 0xC1 (400×400) is unambiguously larger than 0xC2 (50×50) → it must win.
        sys.WindowSizeByHwnd[0xC1] = (400, 400);
        sys.WindowSizeByHwnd[0xC2] = (50, 50);

        var uiTarget = await service.ResolveAsync(app: "ghosts", hwnd: null, CancellationToken.None);

        Assert.AreEqual(910, uiTarget.ProcessId);
        Assert.AreEqual("multi", uiTarget.ProcessName);
        Assert.AreEqual(0xC1L, uiTarget.WindowHandle,
            "Auto-select must pick the largest-area window (0xC1).");
    }

    // ---- ClassifyWindow (pure) ------------------------------------------

    [TestMethod]
    public void ClassifyWindow_NullClassName_ReturnsWindow()
        => Assert.AreEqual("window", UiTargetResolver.ClassifyWindow(null));

    [TestMethod]
    public void ClassifyWindow_PopupClass_ReturnsPopup()
        => Assert.AreEqual("popup", UiTargetResolver.ClassifyWindow("SomePopupClass"));

    [TestMethod]
    public void ClassifyWindow_DialogClass_ReturnsDialog()
        => Assert.AreEqual("dialog", UiTargetResolver.ClassifyWindow("#32770"));

    [TestMethod]
    public void ClassifyWindow_OrdinaryClass_ReturnsWindow()
        => Assert.AreEqual("window", UiTargetResolver.ClassifyWindow("Button"));
}
