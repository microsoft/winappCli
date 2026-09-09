// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Command-level coverage of cooperative desktop turns (issue #764): coordination is entered only
/// after local validation, each command declares the right mode, desktop-sensitive work happens inside
/// a section, and no command acts on a target it resolved before waiting.
/// </summary>
public partial class UiCommandTests
{
    // ---------------------------------------------- validation must precede coordination (spec §10)

    [TestMethod]
    public async Task Click_MissingApp_NeverEntersCoordination()
    {
        // A malformed command must not open a participant lease, take an arrival ticket, or join an
        // indefinite queue behind another workflow.
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count, "preflight must reject before coordination");
    }

    [TestMethod]
    public async Task Click_MissingSelector_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemReservedCombo_IsRefusedBeforeCoordination()
    {
        // win+l can never be driven from automation, so it must never wait for the desktop first.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task Record_InvalidDuration_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "-1", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task Touch_InvalidGesture_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["Button", "-a", "TestApp", "--gesture", "wiggle", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    // ------------------------------------------------------------------- mode classification (§6.1)

    [TestMethod]
    public async Task Inspect_IsAnObservation()
    {
        var command = GetRequiredService<UiInspectCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, _fakeDesktopLock.Runs.Count);
        Assert.AreEqual(UiTurnMode.Observe, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual("ui inspect", _fakeDesktopLock.Runs[0].Operation);
    }

    [TestMethod]
    public async Task SetValue_TakesASharedTurnBecauseItChangesWhatTheAppShows()
    {
        // Background-safe: it drives ValuePattern and never takes active.lock, so it stays usable on a
        // locked or headless session. But it does mutate the app, so it must wait behind a foreign
        // workflow rather than editing a control underneath somebody else's click.
        _fakeUia.FindSingleResult = new UiElement { Id = "box", Selector = "box", Name = "Box" };
        var command = GetRequiredService<UiSetValueCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["box", "hello", "-a", "TestApp", "--json"]);

        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters,
            "a background-safe mutation must never take active.lock");
    }

    [TestMethod]
    public async Task ScrollIntoView_TakesASharedTurnWithoutTakingTheDesktop()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "item", Selector = "item", Name = "Item" };
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["item", "-a", "TestApp", "--json"]);

        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters);
    }

    [TestMethod]
    public async Task Record_IsTurnSharedSoSameOwnerInputCanInterleave()
    {
        var command = GetRequiredService<UiRecordCommand>();
        await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "--output", Path.Combine(_tempDirectory.FullName, "r.mp4"), "--json"]);

        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Invoke_IsDesktopExclusive()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "btn", Selector = "btn", Name = "Button" };
        var command = GetRequiredService<UiInvokeCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Scroll_ClassifiesByTransport()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "list", Selector = "list", Name = "List", X = 10, Y = 10, Width = 100, Height = 100,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        // --direction uses the UIA ScrollPattern, which moves the container without touching the
        // desktop: a shared turn, and no active.lock.
        var command = GetRequiredService<UiScrollCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["list", "-a", "TestApp", "--direction", "down", "--json"]);
        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters,
            "ScrollPattern must not take active.lock");

        // --to is the same transport, so it classifies the same way.
        _fakeDesktopLock.Runs.Clear();
        await ParseAndInvokeWithCaptureAsync(command, ["list", "-a", "TestApp", "--to", "bottom", "--json"]);
        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters);

        // --wheel injects OS-wide mouse input at the cursor.
        _fakeDesktopLock.Runs.Clear();
        await ParseAndInvokeWithCaptureAsync(command, ["list", "-a", "TestApp", "--wheel", "3", "--json"]);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "wheel input must run inside a desktop section");
    }

    [TestMethod]
    public async Task Screenshot_IsAlwaysDesktopExclusive()
    {
        _fakeUia.ScreenshotResult = (new byte[4 * 4 * 4], 4, 4);
        var output = Path.Combine(_tempDirectory.FullName, "shot.png");
        var command = GetRequiredService<UiScreenshotCommand>();

        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--output", output, "--json"]);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);

        _fakeDesktopLock.Runs.Clear();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--focus", "--output", output, "--json"]);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
    }

    // ------------------------------------------------- desktop-section placement and revalidation

    [TestMethod]
    public async Task Click_ForegroundsAndInjectsInsideADesktopSection()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "the foreground request and the click must run inside one desktop section");
        Assert.AreEqual(0, _fakeDesktopLock.OpenDesktopSections,
            "the section must be released before the result is formatted");
        CollectionAssert.Contains(_fakeDesktopForeground.ForegroundRequests, 4242L);
    }

    [TestMethod]
    public async Task Click_RefreshesTheTargetWindowFromTheReResolvedElement()
    {
        // The window a queued command acts on must come from the read taken inside the section, not
        // from the advisory read taken before the wait (spec §10.5).
        var stale = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 1111,
        };
        var current = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 2222,
        };
        // A per-selector read sequence: the advisory read taken before the section sees the old window,
        // every read inside the section sees the current one.
        _fakeUia.MovingResults["btn"] = new Queue<UiElement?>([stale, current, current, current, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.Contains(_fakeDesktopForeground.ForegroundRequests, 2222L);
        CollectionAssert.DoesNotContain(_fakeDesktopForeground.ForegroundRequests, 1111L,
            "the pre-wait window handle must never be foregrounded");
    }

    [TestMethod]
    public async Task SendKeys_RefusesWhenTheTargetWindowClosedWhileQueued()
    {
        // Keystrokes are irreversible, so send-keys must revalidate its window after the queue wait just
        // as click and invoke do — otherwise it types into whatever now owns a recycled handle.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "txt", Selector = "txt", Name = "Value",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "txt", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopForeground.ForegroundRequests.Count,
            "a closed target must be refused before anything touches the desktop");
    }

    [TestMethod]
    public async Task SendKeys_RefusesWhenTheWindowHandleWasRecycledByAnotherProcess()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "txt", Selector = "txt", Name = "Value",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        // The handle now belongs to a different process: Windows reused it while this command queued.
        _fakeSystemQuery.ProcessIdForWindowResult = 9999;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "txt", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopForeground.ForegroundRequests.Count,
            "a recycled handle must be refused before anything touches the desktop");
    }

    [TestMethod]
    public async Task SendKeys_WithoutATargetStillValidatesTheSessionWindow()
    {
        // With no --target the session HWND was captured before the queue wait, so it needs the same
        // check: nothing re-resolves it on the way in.
        _fakeTargetResolver.TargetResult = new UiTarget
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopForeground.ForegroundRequests.Count,
            "a stale session window must be refused before anything touches the desktop");
    }

    [TestMethod]
    public async Task Click_RefusesWhenTheTargetWindowClosedWhileQueued()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        // 0 means "no such window": the target closed while this command waited for the desktop.
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "no input may be injected at a dead target");
    }

    [TestMethod]
    public async Task Click_RefusesWhenTheWindowHandleWasRecycledByAnotherProcess()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        // The session resolves PID 1234; a different PID means Windows reused the handle.
        _fakeSystemQuery.ProcessIdForWindowResult = 9999;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count);
    }

    [TestMethod]
    public async Task Invoke_ReResolvesTheElementInsideTheDesktopSection()
    {
        var stale = new UiElement { Id = "btn", Selector = "btn", Name = "Stale", WindowHandle = 4242 };
        var current = new UiElement { Id = "btn", Selector = "btn", Name = "Current", WindowHandle = 4242 };
        _fakeUia.MovingResults["btn"] = new Queue<UiElement?>([stale, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters);
        Assert.AreSame(current, _fakeUia.LastInvokedElement,
            "invoke must act on the element resolved after the queue wait, not before it");
    }

    [TestMethod]
    public async Task Invoke_RefusesWhenTheTargetWindowClosedWhileQueued()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "btn", Selector = "btn", Name = "Button", WindowHandle = 4242 };
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.IsNull(_fakeUia.LastInvokedElement, "no pattern may be invoked on a dead target");
    }

    [TestMethod]
    public async Task Focus_ReResolvesTheElementInsideTheDesktopSection()
    {
        var stale = new UiElement { Id = "box", Selector = "box", Name = "Stale", WindowHandle = 4242 };
        var current = new UiElement { Id = "box", Selector = "box", Name = "Current", WindowHandle = 4242 };
        _fakeUia.MovingResults["box"] = new Queue<UiElement?>([stale, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["box", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters);
        Assert.AreSame(current, _fakeUia.LastFocusedElement);
    }

    // --------------------------------------------------------------- send-keys ordering fix (§13)

    [TestMethod]
    public async Task SendKeys_DoesNotFocusTheTargetWhenForegroundValidationFails()
    {
        // Spec §13: a command that fails foreground validation must not first apply avoidable focus to
        // its target — that alone would dismiss another workflow's transient UI.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = false;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsNull(_fakeUia.LastFocusedElement,
            "focus must be applied only after the foreground has been verified");
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_FocusesTheTargetOnlyAfterForegroundIsVerified()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = true;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(_fakeUia.LastFocusedElement);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "revalidate, foreground, focus and send belong to one desktop section");
    }

    // --------------------------------------------- coordination faults must not become internal_error

    [TestMethod]
    public async Task Record_CoordinationFailureInsideTheBody_SurfacesTheCoordinationError()
    {
        // `ui record` opens its desktop section from inside the handler's broad catch-all. Without the
        // IsCoordinationFault filter, an active.lock failure was reported as `internal_error` and — worse
        // — looked to the coordinator like a normal body return, renewing the owner's idle grace.
        _fakeRecording.RecordException = new UiCoordinationException(
            UiCoordinationErrorCodes.Unavailable, "The UI desktop lock could not be opened.");

        var outputPath = Path.Combine(_tempDirectory.FullName, "coordination-fault.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiCoordinationErrorCodes.Unavailable);
    }

    [TestMethod]
    public async Task Record_OrdinaryFailureInsideTheBody_StaysWithTheHandler()
    {
        // The filter must be narrow: only coordination faults escape. An ordinary capture failure keeps
        // its existing handler-owned envelope and never turns into a coordination error.
        _fakeRecording.RecordException = new InvalidOperationException("encoder blew up");

        var outputPath = Path.Combine(_tempDirectory.FullName, "ordinary-fault.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        // Escaping to UiCoordinatedAction would not be caught there either, so an unhandled throw here
        // is itself the failure signal.
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "encoder blew up");
        Assert.IsFalse(stderr.Contains(UiCoordinationErrorCodes.Unavailable, StringComparison.Ordinal),
            $"an ordinary failure must not be reported as a coordination error; got: {stderr}");
    }

    // ------------------------------------------- pre-start recording cancellation must not renew grace

    [TestMethod]
    public async Task WaitFor_Cancelled_DoesNotReportItselfAsACompletedCommand()
    {
        // wait-for is the one polling command, so it is the one most likely to be interrupted: an agent
        // that gives up on a slow app presses Ctrl+C mid-wait. Catching the cancellation and returning
        // an exit code told the coordinator the command had completed, which renewed the owner's idle
        // grace and kept foreign waiters queued behind a workflow that had already stopped. Worse, with
        // --gone the swallowed cancellation could surface as "the element is gone" — a success.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["Button1", "-a", "TestApp", "--json"], cts.Token);

        Assert.IsTrue(_fakeDesktopLock.LastBodyThrew,
            "the body must propagate the cancellation; returning any exit code makes the coordinator treat "
                + "an interrupted wait as a completed command and renew the owner's grace");
        Assert.IsInstanceOfType<OperationCanceledException>(_fakeDesktopLock.LastBodyException);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("internal_error", StringComparison.Ordinal),
            $"a cancellation is not an internal error (exit {exitCode})");
    }

    [TestMethod]
    public async Task Record_CancelledBeforeCaptureStarted_DoesNotReportItselfAsACompletedCommand()
    {
        // The coordinator decides whether to renew the owner's idle grace from whether the body RETURNED
        // or THREW. Swallowing a native cancellation here and returning 1 made a recording that produced
        // nothing look like a completed command, so the workflow kept the desktop reserved for four more
        // seconds on the strength of work it never did.
        //
        // System.CommandLine flattens a propagating OperationCanceledException to exit code 1 — the same
        // code the old swallow path returned — so the exit code cannot distinguish them. The observable
        // difference, and the one the coordinator actually acts on, is that the body THREW rather than
        // returned. The coordinator half — 130, the `cancelled` envelope and no grace renewal — is
        // asserted against the real coordinator in InteractiveDesktopLockTests.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _fakeRecording.RecordException = new OperationCanceledException(cts.Token);

        var outputPath = Path.Combine(_tempDirectory.FullName, "cancelled-pre-start.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"],
            cts.Token);

        Assert.IsTrue(_fakeDesktopLock.LastBodyThrew,
            "the body must propagate the cancellation; returning any exit code makes the coordinator treat "
                + "a recording that produced nothing as a completed command and renew the owner's grace");
        Assert.IsInstanceOfType<OperationCanceledException>(_fakeDesktopLock.LastBodyException);
        Assert.IsFalse(File.Exists(outputPath), "a recording cancelled before capture produces no artifact");
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("internal_error", StringComparison.Ordinal),
            $"a cancellation is not an internal error (exit {exitCode})");
    }

    [TestMethod]
    public async Task Record_ThatFinalizesOnCancellationAndReturnsSuccess_StillCompletesNormally()
    {
        // The positive half of the same contract, kept explicit so the fix above cannot be "simplified"
        // into propagating every cancellation: an ACTIVE recording observes Ctrl+C, finalizes its MP4 and
        // returns success. That is a completed command and must keep renewing the owner's grace.
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 3, Width = 64, Height = 64, Mode = "wgc" };
        _fakeRecording.RecordShouldWaitForCancellation = true;

        using var stdin = new StringReader("stop");
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = stdin;
        try
        {
            var outputPath = Path.Combine(_tempDirectory.FullName, "finalized.mp4");
            var command = GetRequiredService<UiRecordCommand>();

            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "0", "-o", outputPath, "--json"]);

            Assert.AreEqual(0, exitCode, "a finalized recording reports success rather than cancellation");
            Assert.IsTrue(File.Exists(outputPath));
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
            _fakeRecording.RecordShouldWaitForCancellation = false;
        }
    }

    // --------------------------------------------- send-input must re-verify foreground after focusing

    [TestMethod]
    public async Task SendKeys_ForegroundLostDuringFocus_RefusesToSend()
    {
        // FocusAsync is an awaited round-trip into the target's UI thread, and setting focus can itself
        // activate another window. A published repro exited 0 while HELLO landed in a decoy window that
        // the target's own focus event had activated. The check before focusing is therefore not enough —
        // the last check before injection is the one that protects the user.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = true;

        // First gate (before focus) passes; the second (after focus) denies, modelling the drift.
        _fakeForeground.DenyOnCallNumber = 2;
        _fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;
        _fakeUia.OnFocus = () => { /* a focus handler activates a decoy window */ };

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count,
            "no keystrokes may be injected once the foreground drifted away from the target");
        Assert.AreEqual(2, _fakeForeground.Calls.Count,
            "the foreground must be verified again after focus, not only before it");
    }

    [TestMethod]
    public async Task SendKeys_ForegroundHeldThroughFocus_Sends()
    {
        // The recheck must not become a false refusal on the ordinary path.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = true;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_IsUnaffectedByTheForegroundRecheck()
    {
        // post-message posts straight to the target HWND's queue, so it is not foreground-sensitive and
        // must not acquire a new refusal path.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = false;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count, "post-message never consults the foreground guard");
    }
}
