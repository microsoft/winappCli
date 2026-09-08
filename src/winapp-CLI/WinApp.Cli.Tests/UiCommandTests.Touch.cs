// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // touch — synthetic touch gestures (tap/swipe/pinch/…) at a selector
    // center or explicit screen x,y coordinates. Exercised via FakePointerInput
    // (records contacts), FakeForegroundGuard (proceeds) and
    // FakeUiAutomationService (supplies the bounds rect via TryGetWindowRect).
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_Tap_SelectorCenter_InjectsTouch()
    {
        // Element center = (100 + 40/2, 100 + 20/2) = (120, 110). Window handle non-zero so the
        // command has a real target to bounds-check and foreground-verify against.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Button", Selector = "btn-ok-1234",
            X = 100, Y = 100, Width = 40, Height = 20, WindowHandle = 4242
        };

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Tap, call.Gesture);
        Assert.AreEqual(1, call.ContactPaths.Count);
        Assert.AreEqual(new PointerPoint(120, 110), call.ContactPaths[0][0]);

        // Bounds-check + foreground gate both ran against the resolved window handle.
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.IsTrue(_fakeForeground.Calls.Count >= 1);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("tap", result.GetProperty("gesture").GetString());
        Assert.AreEqual(4242, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task Touch_Swipe_ExplicitPoints_InjectsGlidePath()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5150;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "100,100", "--to-point", "300,120", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Swipe, call.Gesture);
        Assert.AreEqual(new PointerPoint(100, 100), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(300, 120), call.ContactPaths[0][^1]);
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Right_ComputesEndPoint()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5151;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "100,200", "--direction", "right", "--distance", "150", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Swipe, call.Gesture);
        Assert.AreEqual(new PointerPoint(100, 200), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 200), call.ContactPaths[0][^1]); // +150 on X
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Left_ComputesEndPoint()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5152;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "400,200", "--direction", "left", "--distance", "150", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(400, 200), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 200), call.ContactPaths[0][^1]); // -150 on X
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Up_ComputesEndPoint()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5153;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "200,300", "--direction", "up", "--distance", "100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(200, 300), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(200, 200), call.ContactPaths[0][^1]); // -100 on Y
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Down_ComputesEndPoint()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5154;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "200,100", "--direction", "down", "--distance", "100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(200, 100), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(200, 200), call.ContactPaths[0][^1]); // +100 on Y
    }

    [TestMethod]
    public async Task Touch_Swipe_DistanceOnly_DefaultsToRightDirection()
    {
        // Backward-compat: --distance without --direction still moves right (the old behavior).
        _fakeTargetResolver.TargetResult.WindowHandle = 5155;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "50,50", "--distance", "200", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(50, 50), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 50), call.ContactPaths[0][^1]); // right = +X
    }

    [TestMethod]
    public async Task Touch_LongPress_NoHoldMs_DefaultsTo500ms()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5156;

        var command = GetRequiredService<UiTouchCommand>();
        // No --hold-ms specified → should default to 500 ms for long-press.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.LongPress, call.Gesture);
        Assert.AreEqual(500, call.HoldMs); // long-press default
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMs_UsesProvidedValue()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5157;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "1200", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(1200, call.HoldMs); // explicit value preserved
    }

    [TestMethod]
    public async Task Touch_LongPress_JsonOutputIncludesEffectiveHoldMs()
    {
        // Verify that the JSON result carries the effective holdMs so agents can observe it.
        _fakeTargetResolver.TargetResult.WindowHandle = 5158;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        // Default long-press holdMs = 500.
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp),
            "JSON output must contain 'holdMs' property");
        Assert.AreEqual(500, holdMsProp.GetInt32(), "holdMs must equal the effective long-press default (500)");
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMs_JsonOutputIncludesExplicitValue()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5159;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "1500", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp));
        Assert.AreEqual(1500, holdMsProp.GetInt32(), "holdMs must reflect the explicit --hold-ms value");
    }

    [TestMethod]
    public async Task Touch_Tap_JsonOutput_HoldMsIsZero()
    {
        // For a plain tap, holdMs=0 and must appear in JSON as 0 (not missing).
        _fakeTargetResolver.TargetResult.WindowHandle = 5160;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "tap", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp));
        Assert.AreEqual(0, holdMsProp.GetInt32(), "Tap gesture should report holdMs=0");
    }

    [TestMethod]
    public async Task Touch_InvalidGesture_Rejected_NoInjection()
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "frobnicate", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
    }

    [TestMethod]
    public async Task Touch_FingersAboveMax_Rejected_NoInjection()
    {
        // 11 > MaxContacts (10) must be rejected up front, before planning/injecting.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--fingers", "11", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // Rejected before any window resolution.
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
    }

    [TestMethod]
    [DataRow("--hold-ms")]
    [DataRow("--duration-ms")]
    public async Task Touch_DelayAboveMax_RejectedWithInvalidArguments(string option)
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", option, "60001", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        StringAssert.Contains(ConsoleStdErr.ToString(), option);
        StringAssert.Contains(ConsoleStdErr.ToString(), "60000");
    }

    [TestMethod]
    [DataRow("tap", "--to-point", "200,200")]
    [DataRow("double-tap", "--to-point", "200,200")]
    [DataRow("long-press", "--to-point", "200,200")]
    [DataRow("pinch", "--to-point", "200,200")]
    [DataRow("stretch", "--to-point", "200,200")]
    [DataRow("tap", "--direction", "left")]
    [DataRow("double-tap", "--direction", "left")]
    [DataRow("long-press", "--direction", "left")]
    [DataRow("pinch", "--direction", "left")]
    [DataRow("stretch", "--direction", "left")]
    [DataRow("tap", "--distance", "50")]
    [DataRow("double-tap", "--distance", "50")]
    [DataRow("long-press", "--distance", "50")]
    [DataRow("tap", "--duration-ms", "50")]
    [DataRow("double-tap", "--duration-ms", "50")]
    [DataRow("long-press", "--duration-ms", "50")]
    [DataRow("pinch", "--fingers", "3")]
    [DataRow("stretch", "--fingers", "3")]
    public async Task Touch_IncompatibleExplicitGestureOption_RejectedWithInvalidArguments(
        string gesture, string option, string value)
    {
        var args = new List<string>
        {
            "-a", "TestApp",
            "--gesture", gesture,
            "--at", "100,100",
            option, value,
            "--json"
        };

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [.. args]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, option);
        StringAssert.Contains(stderr, gesture);
    }

    [TestMethod]
    public async Task Touch_Swipe_ExplicitMovingOptions_Succeeds()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5163;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "100,100",
             "--direction", "right", "--distance", "50", "--duration-ms", "250",
             "--fingers", "3", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Swipe, call.Gesture);
        Assert.AreEqual(3, call.ContactPaths.Count);
        Assert.AreEqual(250, call.DurationMs);
        Assert.AreEqual(new PointerPoint(150, 100), call.ContactPaths[0][^1]);
    }

    [TestMethod]
    public async Task Touch_Pinch_ExplicitTwoFingers_Succeeds()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 5164;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "pinch", "--at", "100,100",
             "--distance", "80", "--fingers", "2", "--duration-ms", "250", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Pinch, call.Gesture);
        Assert.AreEqual(2, call.ContactPaths.Count);
        Assert.AreEqual(250, call.DurationMs);
    }

    [TestMethod]
    public async Task Touch_ExplicitPointOutsideWindow_WarnsAndInjects()
    {
        // #661: an out-of-window point is a non-fatal advisory, not a hard failure. Touch injects
        // anyway (consistent with click/drag/hover/scroll, which already inject at out-of-window
        // coordinates) and surfaces a warning, rather than rejecting with invalid_arguments.
        _fakeTargetResolver.TargetResult.WindowHandle = 7000;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "5000,5000", "--json"]);

        Assert.AreEqual(0, exitCode, "Out-of-window point is advisory, not fatal — injection proceeds");
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count, "Injection must still happen");
        // The bounds check ran (rect consulted) AND the foreground gate was reached (no early reject).
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.IsTrue(_fakeForeground.Calls.Count >= 1);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings),
            "Out-of-window injection must surface a warnings[] advisory");
        Assert.AreEqual(1, warnings.GetArrayLength());
        StringAssert.Contains(warnings[0].GetString(), "outside the target window",
            "Warning must name the out-of-window condition");
    }

    [TestMethod]
    public async Task Touch_OutsideWindowAndRemoteSession_EmitsBothWarnings()
    {
        // The out-of-window advisory (#661) and the remote-session delivery advisory (id27/id28) are
        // independent; when both apply they must both appear in warnings[].
        _fakeTargetResolver.TargetResult.WindowHandle = 7050;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);
        _fakeForeground.IsRemoteSessionResult = true;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "5000,5000", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings));
        Assert.AreEqual(2, warnings.GetArrayLength(),
            "Both the out-of-window and remote-delivery advisories must be present");
        var joined = warnings[0].GetString() + " | " + warnings[1].GetString();
        StringAssert.Contains(joined, "outside the target window");
        StringAssert.Contains(joined, "remote");
    }

    [TestMethod]
    public async Task Touch_GestureWaypointOutsideWindow_WarnsAndInjects()
    {
        // #661: FirstOutOfBounds walks EVERY planned point, including gesture-generated waypoints — not
        // just the explicit --at/--path anchor. A swipe whose START is in-bounds but whose generated END
        // waypoint lands outside the window must still warn (and inject), proving the bounds check covers
        // the whole gesture path rather than only the anchor.
        _fakeTargetResolver.TargetResult.WindowHandle = 7100;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiTouchCommand>();
        // Start (400,300) is inside the window; a 5000px rightward swipe generates an end waypoint at
        // (5400,300), which is outside it.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "400,300", "--distance", "5000", "--json"]);

        Assert.AreEqual(0, exitCode, "Out-of-window waypoint is advisory, not fatal — injection proceeds");
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count, "Injection must still happen");

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings),
            "A gesture waypoint outside the window must surface a warnings[] advisory");
        Assert.AreEqual(1, warnings.GetArrayLength());
        // The advisory must name the out-of-bounds WAYPOINT (5400,300), not the in-bounds start (400,300),
        // proving FirstOutOfBounds walked past the anchor into the generated path.
        StringAssert.Contains(warnings[0].GetString(), "(5400,300)",
            "Warning must name the out-of-bounds waypoint, not the in-bounds start");
        StringAssert.Contains(warnings[0].GetString(), "outside the target window");
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task Touch_ExplicitPointOutsideWindow_TextMode_LogsWarning()
    {
        // Coverage for the text-mode (non-json) branch: every other out-of-window test passes --json, so
        // the logger.LogWarning foreach path is otherwise unexercised. Non-error logger output routes
        // through the static ambient AnsiConsole (TextWriterLogger), so we swap it to a capturing console
        // for the invoke; [DoNotParallelize] keeps that global swap isolated. The advisory must land on
        // that (stdout) console and NOT on stderr, proving it is a non-fatal warning.
        _fakeTargetResolver.TargetResult.WindowHandle = 7200;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiTouchCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--at", "5000,5000"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode, "Out-of-window point is advisory, not fatal — injection proceeds");
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count, "Injection must still happen");
        StringAssert.Contains(ambient.Output, "outside the target window",
            "Text mode must log the out-of-window advisory, not only emit it in the --json warnings[] envelope");
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("outside the target window"),
            "The advisory is a non-error warning — it must route to stdout, not stderr");
    }

    [TestMethod]
    public async Task Touch_ZeroTargetHwnd_Rejected_NoInjection()
    {
        // Session has no window handle (0) and an explicit --at (no element resolves a handle),
        // so there is no verifiable target — the command must refuse to inject.
        _fakeTargetResolver.TargetResult.WindowHandle = 0;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // Refused before the rect lookup / foreground gate.
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Touch_WindowRectUnreadable_Rejected_NoInjection()
    {
        // A nonzero handle resolves, but the window rect can't be read → no verifiable
        // target, so the command must refuse (no_target) before the foreground gate.
        _fakeTargetResolver.TargetResult.WindowHandle = 7100;
        _fakeUia.WindowRectAllow = false;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // The rect lookup WAS attempted, but injection/foreground never happened.
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMsZero_RejectedWithInvalidArguments()
    {
        // Explicit --hold-ms 0 with long-press is a degenerate combination that must be
        // rejected with a structured invalid_arguments error, NOT silently rewritten to 500.
        _fakeTargetResolver.TargetResult.WindowHandle = 5161;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count,
            "No injection should occur when --hold-ms 0 is explicitly given with long-press");
    }

    [TestMethod]
    public async Task Touch_InjectThrowsInvalidOperation_ReturnsNonZeroWithStructuredError()
    {
        // If the pointer-injection path throws InvalidOperationException (e.g. UP-frame failure
        // now surfacing on the normal path), the command must catch it and return non-zero with a
        // structured JSON error (injection_unsupported), not crash or report success.
        _fakeTargetResolver.TargetResult.WindowHandle = 5162;
        _fakePointer.ThrowException = new InvalidOperationException("UP frame injection failed — pointer stuck");

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode,
            "Command must return non-zero when the inject call throws InvalidOperationException");
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count,
            "No successful injection was recorded (the fake threw before recording)");
        // Stdout must be empty — no success envelope must be emitted.
        Assert.AreEqual(string.Empty, TestAnsiConsole.Output.Trim(),
            "Stdout must be empty on injection failure — success envelope must not be emitted");
        // Stderr must contain a structured JSON error with code == injection_unsupported.
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInjectionUnsupported,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'injection_unsupported' when injection throws InvalidOperationException");
    }

    // -------------------------------------------------------------------------
    // M1 (round-11) — typed AppNotFoundException: app-not-found → missing_app;
    // selector-ambiguity (plain IOE) → invalid_arguments, NOT missing_app.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_SessionService_ThrowsAppNotFound_ReturnsMissingApp()
    {
        // When the session service throws AppNotFoundException (app not running), the outer
        // catch must map it to missing_app, not internal_error.
        _fakeTargetResolver.ThrowException = new AppNotFoundException(
            "No running app found matching '__test_nonexistent__'.");

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "__test_nonexistent__", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "App-not-found must exit 1");
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "AppNotFoundException from the target resolver must map to missing_app");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInternalError),
            $"AppNotFoundException must NOT produce internal_error; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task Touch_UiaService_ThrowsSelectorAmbiguity_ReturnsInvalidArguments_NotMissingApp()
    {
        // When FindSingleElementAsync throws plain InvalidOperationException (selector matched
        // multiple elements), the outer catch must map it to invalid_arguments, NOT missing_app.
        _fakeTargetResolver.TargetResult = new UiTarget
        {
            ProcessId = 1, ProcessName = "TestApp", WindowHandle = 1234
        };
        _fakeUia.FindSingleElementThrowException = new InvalidOperationException(
            "Selector matched 3 elements: ...");

        var command = GetRequiredService<UiTouchCommand>();
        // Use a bare selector (no --at) so FindSingleElementAsync is invoked.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "btn-ok", "--json"]);

        Assert.AreEqual(1, exitCode, "Ambiguous selector must exit 1");
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Selector-ambiguity IOE must map to invalid_arguments, not missing_app");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Selector-ambiguity must NOT produce missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInternalError),
            $"Selector-ambiguity must NOT produce internal_error; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // id27/id28 — remote-session (RDP) delivery-uncertainty advisory. Synthetic
    // pointer injection can report success without actually reaching the target
    // over Remote Desktop, so the result must carry an honest warnings[] advisory
    // rather than a bare success. Driven by the fake guard's remote-session flag.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_RemoteSession_EmitsDeliveryWarning()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 8200;
        _fakeForeground.IsRemoteSessionResult = true;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(0, exitCode, "Injection still succeeds; the warning is advisory, not fatal");
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings),
            "Remote session must surface a warnings[] advisory");
        Assert.AreEqual(1, warnings.GetArrayLength());
        StringAssert.Contains(warnings[0].GetString(), "remote",
            "Warning must name the remote/RDP delivery caveat");
    }

    [TestMethod]
    public async Task Touch_LocalSession_NoDeliveryWarning()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 8201;
        _fakeForeground.IsRemoteSessionResult = false; // default; explicit for intent

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsFalse(result.TryGetProperty("warnings", out _),
            "A local console session must not emit the remote-delivery warning");
    }

    [TestMethod]
    public void RemoteInjectionWarning_PureComposition_NullWhenLocal_MessageWhenRemote()
    {
        Assert.IsNull(ForegroundGuard.RemoteInjectionWarning(false, "touch"),
            "No warning on a local session");
        var msg = ForegroundGuard.RemoteInjectionWarning(true, "touch");
        Assert.IsNotNull(msg);
        StringAssert.Contains(msg, "touch", "Warning must name the input kind");
        StringAssert.Contains(msg, "remote", "Warning must name the remote caveat");
    }

    // -------------------------------------------------------------------------
    // Bucket 3 (issue #630) — coverage top-up: the remaining argument-validation
    // branches, selector-resolution failures (via PointerCommandSupport), the
    // non-JSON success/warning render path, and the COM / generic catch arms.
    // All driven through the command handler with the existing fakes — no live
    // injection. LogError output routes to the captured stderr; non-error logger
    // output (Info/Warning) routes through the static ambient console, so those
    // tests swap it to capture and are marked [DoNotParallelize].
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Touch_ShortDescription_NamesTouchGestures()
    {
        var description = GetRequiredService<UiTouchCommand>().ShortDescription;
        Assert.IsFalse(string.IsNullOrWhiteSpace(description));
        StringAssert.Contains(description, "touch");
    }

    [TestMethod]
    public async Task Touch_FingersZero_RejectedWithInvalidArguments_NoInjection()
    {
        // --fingers 0 trips the `fingers < 1` lower-bound guard (distinct from the >MaxContacts
        // upper-bound already covered). Rejected up front, before any window resolution.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--fingers", "0", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--fingers");
    }

    [TestMethod]
    public async Task Touch_InvalidDirectionValue_RejectedWithInvalidArguments()
    {
        // A --direction value outside {right,left,up,down} is rejected by the up-front value check.
        // Using a swipe means the ONLY reason to reject is the invalid direction value itself.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--gesture", "swipe", "--direction", "diagonal", "--distance", "50", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--direction");
    }

    [TestMethod]
    public async Task Touch_MalformedAtPoint_RejectedWithInvalidArguments()
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "not-a-point", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--at");
    }

    [TestMethod]
    public async Task Touch_MalformedToPoint_OnSwipe_RejectedWithInvalidArguments()
    {
        // --to-point is only parsed on a swipe (the non-swipe guard fires earlier otherwise), so a
        // malformed value must reach the point-parse failure branch.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--to-point", "junk", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--to-point");
    }

    [TestMethod]
    public async Task Touch_NoTarget_NoSelectorNoAt_RejectedWithInvalidArguments()
    {
        // Neither a selector nor --at: the command cannot resolve a point to touch.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Provide a target");
    }

    [TestMethod]
    public async Task Touch_Pinch_WithoutDistance_RejectedWithInvalidArguments()
    {
        // Pinch/stretch require an explicit --distance (finger spread). Without it, distance defaults
        // to 0 and the gesture is rejected before injection.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--gesture", "pinch", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "distance");
    }

    [TestMethod]
    public async Task Touch_Swipe_WithoutToPointOrDistance_RejectedWithInvalidArguments()
    {
        // A swipe needs either --to-point or --distance to compute an end point; with neither it is
        // rejected rather than degenerating into a zero-length swipe.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--gesture", "swipe", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "swipe requires");
    }

    [TestMethod]
    public async Task Touch_MissingApp_NoAppNoWindow_ReturnsMissingApp()
    {
        // With valid arguments but no --app/--window, the missing-app guard fires AFTER all argument
        // validation (so a valid selector + no app yields missing_app, not invalid_arguments).
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Target app required");
    }

    [TestMethod]
    public async Task Touch_SelectorNotFound_ReturnsElementNotFound_NoInjection()
    {
        // A bare selector that resolves to no element: ResolvePointAsync reports element_not_found and
        // returns not-Ok, so the command aborts before injection (covers the null-element branch of
        // PointerCommandSupport and the !target.Ok guard). FindSingleResult stays null by default.
        _fakeTargetResolver.TargetResult.WindowHandle = 6100;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["ghost-element", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "No element found");
    }

    [TestMethod]
    public async Task Touch_SelectorZeroSizeElement_ReturnsZeroSize_NoInjection()
    {
        // A resolved element with zero width/height cannot supply a usable center point.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Button", Selector = "flat-btn",
            X = 100, Y = 100, Width = 0, Height = 20, WindowHandle = 6200
        };

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["flat-btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "zero size");
    }

    [TestMethod]
    public async Task Touch_SelectorTargetVanishesBeforeInjection_ReturnsTargetMoved_NoInjection()
    {
        // The selector resolves initially, but the pre-injection stable re-resolve finds it gone — the
        // command refuses rather than injecting at a stale coordinate (covers the TryReport-false arm of
        // PointerCommandSupport.ResolvePointAsync).
        const string sel = "vanishing-btn";
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 100, Y = 100, Width = 40, Height = 20, WindowHandle = 6300 });
        seq.Enqueue(null); // gone on the stability re-read
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "could not be re-resolved");
    }

    [TestMethod]
    [DoNotParallelize] // swaps the process-wide ambient AnsiConsole to capture non-error logger output
    public async Task Touch_NonJson_LocalSession_LogsSuccessWithoutWarning()
    {
        // The non-JSON render path logs a human success line and, for a local session, no delivery
        // warning. Info/Warning route through the static ambient console, so we swap it to capture.
        _fakeTargetResolver.TargetResult.WindowHandle = 8300;
        _fakeForeground.IsRemoteSessionResult = false;

        var command = GetRequiredService<UiTouchCommand>();
        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, ["-a", "TestApp", "--at", "150,160"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        Assert.IsFalse(TestAnsiConsole.Output.Contains('{'), "Non-JSON path must not emit a JSON envelope");
        StringAssert.Contains(ambientOutput, "tap", "Success line must name the gesture");
        Assert.IsFalse(ambientOutput.Contains("remote", StringComparison.OrdinalIgnoreCase),
            "A local session must not emit the remote-delivery warning");
    }

    [TestMethod]
    [DoNotParallelize] // swaps the process-wide ambient AnsiConsole to capture non-error logger output
    public async Task Touch_NonJson_RemoteSession_LogsSuccessAndDeliveryWarning()
    {
        // Remote (RDP) session: the non-JSON path still injects (exit 0) but appends an advisory
        // delivery-uncertainty warning to the human output.
        _fakeTargetResolver.TargetResult.WindowHandle = 8301;
        _fakeForeground.IsRemoteSessionResult = true;

        var command = GetRequiredService<UiTouchCommand>();
        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, ["-a", "TestApp", "--at", "150,160"]);

        Assert.AreEqual(0, exitCode, "The remote warning is advisory; injection still succeeds");
        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ambientOutput, "remote", "Remote session must surface the delivery-uncertainty warning");
    }

    [TestMethod]
    public async Task Touch_ComException_ReturnsStaleElement_NoInjection()
    {
        // A COMException surfacing during resolution is a stale-element signal (the element vanished
        // mid-flight); the command maps it to the stale envelope and never injects.
        _fakeUia.FindSingleElementThrowException = new System.Runtime.InteropServices.COMException("UIA stale (test)");

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "no longer accessible");
        AssertJsonErrorCode(UiJsonError.CodeStaleElement);
    }

    [TestMethod]
    public async Task Touch_GenericException_ReturnsInternalError_NoInjection()
    {
        // A non-COM, non-app-not-found, non-IOE exception during session resolution falls through to
        // the catch-all generic handler and is reported (not swallowed or crashed).
        _fakeTargetResolver.ResolveThrow = new TimeoutException("boom (test)");

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "boom (test)");
        AssertJsonErrorCode(UiJsonError.CodeInternalError);
    }
}
