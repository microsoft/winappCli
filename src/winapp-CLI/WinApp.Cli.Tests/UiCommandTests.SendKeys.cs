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
    // send-keys (#562) — synthetic keyboard input
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task SendKeys_Json_EmitsEnvelope()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["ctrl+a", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("ctrl+a", result.GetProperty("keys").GetString());
        Assert.AreEqual("post-message", result.GetProperty("via").GetString());
        Assert.AreEqual(1, result.GetProperty("actionCount").GetInt32());
    }

    [TestMethod]
    public async Task SendKeys_DefaultTransport_IsPostMessage()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(KeyTransport.PostMessage, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_SetsTransport()
    {
        // send-input now requires a resolvable target window (M9); give the session one.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_SequenceAndText_ParsesMultipleActions()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        // "down down enter" -> 3 key chords; "hello" -> 1 text action
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["down down enter hello", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(4, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[3]);
    }

    [TestMethod]
    public async Task SendKeys_WithTarget_FocusesElementAndUsesElementHwnd()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Edit", Selector = "txt-name-1234", WindowHandle = 4242
        };

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["hello", "-a", "TestApp", "--target", "txt-name-1234", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(4242, _fakeKeyboard.SendCalls[0].Hwnd);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("txt-name-1234", result.GetProperty("target").GetString());
        Assert.AreEqual(4242, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task SendKeys_TargetNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["hello", "-a", "TestApp", "--target", "missing-0000", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_MissingKeys_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SendKeys_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SendKeys_InvalidVia_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "bogus", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_InvalidKeyToken_ReturnsError()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["vk=0xZZ", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_IsRejected()
    {
        // send-input is OS-wide, so a system-reserved combo (win+l) acts on the shell, not just the
        // target — the command must refuse it and never reach the keyboard transport.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242; // a resolvable target so we reach the system-combo guard (M9)
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaPostMessage_StillSends()
    {
        // post-message is window-scoped (posts straight to the target HWND's queue), so it isn't
        // OS-wide and is not blocked — an alt+f4 posted to the window just closes that window.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["alt+f4", "-a", "TestApp", "--via", "post-message", "--json"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public void SendKeys_VerbatimOption_DocumentedInDescription()
    {
        // The --verbatim flag must be discoverable via --help / cli-schema and explain it types literally.
        StringAssert.Contains(UiSendKeysCommand.VerbatimOption.Description, "literal");
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_TypesEntireArgumentAsLiteralText()
    {
        // Without --verbatim this presses Down, Down, Enter; with it, the words are typed verbatim as
        // a single literal text action.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["down down enter", "-a", "TestApp", "--verbatim", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("down down enter", ((TextInput)actions[0]).Text);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(1, result.GetProperty("actionCount").GetInt32());
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_PreservesExactWhitespace()
    {
        // The normal path collapses internal whitespace to a single space; --verbatim keeps it exact.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["a  b", "-a", "TestApp", "--verbatim"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("a  b", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_SystemComboTextViaSendInput_IsSentAsText()
    {
        // With --verbatim, "win+l" is literal text (a TextInput), not a chord — the system-combo guard
        // only inspects key chords, so the text is typed rather than refused even via send-input.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242; // a resolvable target for send-input (M9)
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--verbatim", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.IsInstanceOfType<TextInput>(_fakeKeyboard.SendCalls[0].Actions[0]);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_MissingKeys_ReturnsError()
    {
        // --verbatim still requires a value; an empty keys argument is rejected before injection.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--verbatim", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_Verbatim_WhitespaceOnly_IsTypedLiterally()
    {
        // --verbatim promises exact whitespace preservation, so a whitespace-only argument is legitimate
        // content to type (three spaces), not a "missing keys" error — unlike the normal path (M8).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["   ", "-a", "TestApp", "--verbatim"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var actions = _fakeKeyboard.SendCalls[0].Actions;
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("   ", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public async Task SendKeys_WhitespaceOnly_WithoutVerbatim_ReturnsError()
    {
        // Without --verbatim a whitespace-only argument has no tokens to interpret and stays an error (M8).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["   ", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_NoResolvableTarget_ReturnsError()
    {
        // send-input is OS-wide; without a resolvable target window the command refuses rather than
        // injecting blindly into whatever has focus (M9). The default fake session has no window handle,
        // so the no-target guard fires before the foreground check ever runs.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "no keys should be sent when there is no resolvable target");
        Assert.AreEqual(0, _fakeForeground.Calls.Count, "the no-target guard fires before the foreground check");
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_ForegroundGuardDenies_AbortsWithoutSending()
    {
        // send-input verifies the foreground before injecting OS-wide; if the guard refuses (e.g. a locked
        // desktop or a wrong-window foreground), no keys are sent (M5).
        _fakeTargetResolver.TargetResult.WindowHandle = 4242; // resolvable target → reach the foreground gate
        _fakeForeground.Allow = false;
        _fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "no keys should be sent when the foreground guard refuses");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "refusing to --via send-input",
            "the refusal must name the action the command was attempting");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error to capture the JSON error envelope
    public async Task SendKeys_ViaSendInput_MidInjectionForegroundLoss_MapsToForegroundNotTarget()
    {
        // H1 (issue #657 follow-up): the pre-send foreground gate is a single check, but a long payload is
        // now paced across many SendInput calls, so focus can drift away mid-injection. KeyboardInput aborts
        // that with a ForegroundLostException; the command must map it to the SAME foreground_not_target
        // contract as the pre-send gate (not a generic error) so callers get a precise, actionable code.
        // The command writes the JSON error straight to Console.Error, so capture that stream directly.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242; // resolvable target → reach the send path
        _fakeKeyboard.SendException = new ForegroundLostException("focus drifted mid-injection");

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousErr = Console.Error;
        var errCapture = new StringWriter();
        Console.SetError(errCapture);
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--via", "send-input", "--json"]);
        }
        finally
        {
            Console.SetError(previousErr);
        }

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCodeIn(errCapture.ToString(), WinApp.Cli.Helpers.UiJsonError.CodeForegroundNotTarget);
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_LongText_WarnsAutoThrottledAndSuggestsSetValue()
    {
        // M1 (issue #657 follow-up): a literal-text payload larger than one chunk is auto-throttled (paced)
        // so it lands reliably, which blocks synchronously for a while. The command surfaces an advisory that
        // the throttling is intentional and that set-value is faster for bulk text.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var longText = new string('a', KeyboardInput.DefaultTextChunkChars + 1);

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [longText, "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var warnings = ReadWarnings(TestAnsiConsole.Output);
        Assert.IsTrue(
            warnings.Any(w => w.Contains("throttl", StringComparison.OrdinalIgnoreCase) && w.Contains("set-value", StringComparison.OrdinalIgnoreCase)),
            $"expected a throttling advisory mentioning set-value; got: {string.Join(" | ", warnings)}");
    }

    [TestMethod]
    public async Task SendKeys_ViaSendInput_TextAtChunkBoundary_DoesNotWarn()
    {
        // M1 boundary: a payload of exactly one chunk is a single SendInput call with no added pacing, so the
        // throttle advisory must NOT fire — it is scoped to payloads that actually get chunked, to avoid
        // warning on every short send-input.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var boundaryText = new string('a', KeyboardInput.DefaultTextChunkChars);

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [boundaryText, "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        var warnings = ReadWarnings(TestAnsiConsole.Output);
        Assert.IsFalse(
            warnings.Any(w => w.Contains("throttl", StringComparison.OrdinalIgnoreCase)),
            $"a single-chunk payload must not emit the throttle advisory; got: {string.Join(" | ", warnings)}");
    }

    private static List<string> ReadWarnings(string json)
    {
        var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        if (envelope.TryGetProperty("warnings", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return [.. w.EnumerateArray().Select(x => x.GetString() ?? "")];
        }

        return [];
    }

    [TestMethod]
    public async Task SendKeys_ViaPostMessage_NoTarget_StillSends()
    {
        // post-message posts straight to the target HWND's message queue and is not OS-wide, so it does
        // not require the foreground gate or a resolvable window the way send-input does (M9 is scoped to
        // send-input). A zero handle still posts (the OS routes a 0 hwnd to the focused window).
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "post-message"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count, "post-message does not consult the foreground guard");
    }

    [TestMethod]
    public async Task SendKeys_ComException_ReturnsStaleErrorWithoutSending()
    {
        // A COMException surfacing from session/element resolution is a stale-element signal: the command
        // maps it to the stale error envelope and never reaches the keyboard transport.
        _fakeTargetResolver.ResolveThrow = FakeComException;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_GenericException_ReturnsGenericErrorWithoutSending()
    {
        // A non-COM exception during resolution falls through to the catch-all → generic-error envelope,
        // and no keys are sent (mirrors the COMException stale-element path above).
        _fakeTargetResolver.ResolveThrow = FakeGenericException;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task SendKeys_PostMessageLiteralText_XamlWindow_WarnsTextMayNotBeDelivered()
    {
        // Literal text via post-message to a XAML (WinUI 3) window: the class name — read through
        // ISystemUiQuery — classifies as XAML, so the command warns that a posted WM_CHAR may be dropped
        // by the XAML input pipeline. The warning is advisory: the keys are still posted. Non-error
        // logger output routes through the static ambient AnsiConsole (TextWriterLogger), so we swap it
        // to a capturing console for the invoke; [DoNotParallelize] keeps that global swap isolated.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "WinUIDesktopWin32WindowClass";

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--via", "post-message"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "the warning is advisory — the literal text is still posted");
        StringAssert.Contains(ambient.Output, "WM_CHAR", "a XAML target must trigger the dropped-WM_CHAR warning");
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task SendKeys_PostMessageLiteralText_NonXamlWindow_DoesNotWarn()
    {
        // Same literal-text-via-post-message shape, but a Win32 window (non-XAML class) consumes WM_CHAR,
        // so the XAML warning must NOT fire — it is scoped to XAML to avoid false-alarming other stacks.
        // We capture the ambient console the same way so the negative assertion is real, not vacuous.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "Notepad";

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["hello", "-a", "TestApp", "--via", "post-message"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.IsFalse(ambient.Output.Contains("WM_CHAR", StringComparison.Ordinal),
            "a non-XAML window class must not trigger the XAML dropped-WM_CHAR warning");
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task SendKeys_PostMessageNamedKey_XamlWindow_Warns()
    {
        // #655: a named key (Enter) — not just literal text — is also dropped by the XAML input
        // pipeline when posted, because WinUI/UWP controls are windowless. The broadened warning must
        // fire for any post-message payload to a XAML target, not only text=.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "Windows.UI.Core.CoreWindow";

        var command = GetRequiredService<UiSendKeysCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, ["enter", "-a", "TestApp", "--via", "post-message"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "the warning is advisory — the key is still posted");
        StringAssert.Contains(ambient.Output, "windowless",
            "a named key to a XAML target must trigger the may-not-deliver warning");
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_XamlWindow_Json_IncludesWarning()
    {
        // The honest-reporting warning must also surface in the structured --json warnings[] so
        // automation (not just a human reading the console) can see the delivery caveat.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "WinUIDesktopWin32WindowClass";

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["text=hello", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(), "XAML post-message emits exactly the delivery caveat");
        StringAssert.Contains(warnings[0].GetString(), "post-message");
        StringAssert.Contains(warnings[0].GetString(), "windowless");
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_RetargetsToFocusedChildHwnd()
    {
        // #655 root cause: the resolved target is the top-level window, but a top-level window does not
        // route posted keyboard messages to its focused child control. The command must retarget to the
        // thread's focused child HWND (resolved via ISystemUiQuery) so the keys reach the real control.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;          // top-level window
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x789;      // focused child edit control
        _fakeSystemQuery.RootWindowByHwnd[0x789] = 0xABC;         // child's top-level root is the target

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["text=hello", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0x789, _fakeKeyboard.SendCalls[0].Hwnd, "post-message must target the focused child HWND");

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0x789, result.GetProperty("hwnd").GetInt64(), "JSON hwnd reflects the effective post target");
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_DoesNotRetargetToFocusOnDifferentTopLevelWindow()
    {
        // GetGUIThreadInfo reports focus for the whole GUI thread, and one thread can own several
        // top-level windows. If SetForegroundWindow was denied, the reported focus may belong to a
        // *different* top-level window on that thread. Retargeting there would post the keys to the wrong
        // window despite an explicit target, so the command must keep the resolved target when the focused
        // HWND's top-level root differs.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;          // target top-level window (its own root)
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x999;      // focus is on the thread, but...
        _fakeSystemQuery.RootWindowByHwnd[0x999] = 0xDDD;         // ...it belongs to a different top-level window

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0xABC, _fakeKeyboard.SendCalls[0].Hwnd,
            "post-message must keep the target HWND when the focused window belongs to a different top-level window");

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0xABC, result.GetProperty("hwnd").GetInt64(), "JSON hwnd reflects the un-retargeted target");
    }

    [TestMethod]
    public async Task SendKeys_SendInput_DoesNotRetargetToFocusedChild()
    {
        // Focused-child retargeting is a PostMessage-only concern (a top-level window doesn't forward
        // posted messages). send-input is OS-wide and lands on the foreground window, so it must keep
        // the resolved target HWND even when a focused child is available.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x789;      // present, but must be ignored for send-input

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(0xABC, _fakeKeyboard.SendCalls[0].Hwnd, "send-input keeps the resolved target HWND");
    }

    [TestMethod]
    public async Task SendKeys_SendInput_XamlWindow_DoesNotWarnAboutPostMessage()
    {
        // The may-not-deliver caveat is specific to post-message. send-input delivers real input to
        // XAML apps, so it must not carry the post-message warning even when the target looks like XAML.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "WinUIDesktopWin32WindowClass";

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["text=hello", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0, result.GetProperty("warnings").GetArrayLength(),
            "send-input must not emit the post-message delivery caveat");
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_NonXamlTarget_WithXamlFocusedChild_Json_WarnsAndTargetsChild()
    {
        // Second clause of the XAML-delivery check (UiSendKeysCommand: targetLooksXaml): the top-level
        // target is NOT XAML, but post-message retargets to the thread's focused child and *that child*
        // is a windowless XAML control. Classifying only the top-level target would miss it, so the
        // command must also inspect the effective (retargeted) HWND, warn, and post to the child.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;                                // top-level window (its own root)
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "Notepad";                      // non-XAML top-level
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x789;                            // focused child...
        _fakeSystemQuery.RootWindowByHwnd[0x789] = 0xABC;                               // ...whose root is the target -> retarget
        _fakeSystemQuery.WindowClassNameByHwnd[0x789] = "WinUIDesktopWin32WindowClass"; // ...and is itself XAML

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["text=hello", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0x789, _fakeKeyboard.SendCalls[0].Hwnd, "post-message must target the focused XAML child HWND");

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0x789, result.GetProperty("hwnd").GetInt64(), "JSON hwnd reflects the effective (child) post target");
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(),
            "a XAML focused child must raise the delivery caveat even when the top-level target is not XAML");
        StringAssert.Contains(warnings[0].GetString(), "windowless");
    }

    [TestMethod]
    [DoNotParallelize] // swaps the process-wide ambient AnsiConsole to capture the logger warning
    public async Task SendKeys_PostMessage_NonXamlTarget_WithXamlFocusedChild_WarnsOnConsole()
    {
        // Console parity for the second-clause warning: non-XAML top-level, XAML retargeted child. The
        // advisory must surface on the console (not only --json), and the keys are still posted to the child.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.WindowClassNameByHwnd[0xABC] = "Notepad";                      // non-XAML top-level
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x789;                            // focused child...
        _fakeSystemQuery.RootWindowByHwnd[0x789] = 0xABC;                               // ...whose root is the target -> retarget
        _fakeSystemQuery.WindowClassNameByHwnd[0x789] = "WinUIDesktopWin32WindowClass"; // ...and is itself XAML

        var command = GetRequiredService<UiSendKeysCommand>();
        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command,
            ["hello", "-a", "TestApp", "--via", "post-message"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "the warning is advisory — the text is still posted");
        Assert.AreEqual(0x789, _fakeKeyboard.SendCalls[0].Hwnd, "post-message retargets to the focused XAML child");
        StringAssert.Contains(ambientOutput, "windowless",
            "a XAML focused child must trigger the may-not-deliver warning on the console");
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_KeepsTarget_WhenTargetRootUnresolved()
    {
        // Retarget guard fall-through #1: a different HWND is focused, but the target's own GA_ROOT can't
        // be resolved (returns 0). With no root to compare against, the command must NOT adopt the focused
        // HWND — it keeps the resolved target.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0x789;   // a different focused HWND...
        _fakeSystemQuery.RootWindowByHwnd[0xABC] = 0;          // ...but the target's own root is unresolvable (0)

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0xABC, _fakeKeyboard.SendCalls[0].Hwnd, "an unresolved target root must keep the target HWND");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0xABC, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task SendKeys_PostMessage_KeepsTarget_WhenFocusedEqualsTarget()
    {
        // Retarget guard fall-through #2: GetGUIThreadInfo reports the target window itself as focused
        // (focused == target). There is no distinct child to adopt, so the command keeps the target.
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        _fakeSystemQuery.FocusedWindowByHwnd[0xABC] = 0xABC;   // focus resolves to the target itself

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0xABC, _fakeKeyboard.SendCalls[0].Hwnd, "focused == target must keep the target HWND (no retarget)");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0xABC, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    [DoNotParallelize] // swaps the process-wide ambient AnsiConsole to capture the success log line
    public async Task SendKeys_SuccessLog_SaysPostedForPostMessage_AndSentForSendInput()
    {
        // The human success line reports the transport honestly: post-message is fire-and-forget (it only
        // queues the message and can't confirm delivery) so it reads "Posted", while send-input is real
        // synthesized input and reads "Sent".
        _fakeTargetResolver.TargetResult.WindowHandle = 0xABC;
        var command = GetRequiredService<UiSendKeysCommand>();

        var (postExit, postOutput) = await InvokeWithAmbientConsoleCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "post-message"]);
        Assert.AreEqual(0, postExit);
        StringAssert.Contains(postOutput, "Posted", "post-message must report the queued 'Posted' verb");
        Assert.IsFalse(postOutput.Contains("Sent"), "post-message must not overstate delivery with 'Sent'");

        var (sendExit, sendOutput) = await InvokeWithAmbientConsoleCaptureAsync(command,
            ["enter", "-a", "TestApp", "--via", "send-input"]);
        Assert.AreEqual(0, sendExit);
        StringAssert.Contains(sendOutput, "Sent", "send-input must report the 'Sent' verb");
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_WithAllowSystemKeys_Sends()
    {
        // --allow-system-keys opts in to OS/shell-wide combos (e.g. driving a global hotkey such as
        // PowerToys' win+shift+v): the guard is bypassed and the combo reaches the keyboard transport.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242; // resolvable target + default foreground allow → reach the guard
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+v", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    [TestMethod]
    public async Task SendKeys_NonSystemCombo_ViaSendInput_WithAllowSystemKeys_Unaffected()
    {
        // The flag only relaxes system-reserved combos; an ordinary combo behaves identically with or
        // without it (no accidental change to the normal path).
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "send-input", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemCombo_ViaSendInput_WithoutAllow_StaysRejected()
    {
        // Default (no flag) still refuses system combos on send-input — the opt-in must not weaken the
        // default safety posture.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public void SendKeys_AllowSystemKeysOption_DocumentedInDescription()
    {
        // Discoverable via --help / cli-schema and explains it applies to send-input.
        StringAssert.Contains(UiSendKeysCommand.AllowSystemKeysOption.Description, "send-input");
        StringAssert.Contains(UiSendKeysCommand.AllowSystemKeysOption.Description, "system");
    }

    // COR-01 — SEC-01: win+l must be refused even when --allow-system-keys is set
    [TestMethod]
    public async Task SendKeys_WinL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // win+l triggers LockWorkStation() via the shell hook and is unrecoverable from automation.
        // It must be blocked EVEN when --allow-system-keys is passed — the never-bypassable guard
        // must fire before the soft-combo/allow path and prevent injection.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "win+l must never reach the keyboard transport");
    }

    // COR-01 — SEC-01: a benign win+<key> combo IS allowed with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_WinR_ViaSendInput_WithAllowSystemKeys_IsAllowed()
    {
        // win+r (Run dialog) is a soft-blocked system combo that the caller can opt into with
        // --allow-system-keys. It must pass the never-bypassable guard (only win+l is hard-blocked)
        // and reach the keyboard transport.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+r", "-a", "TestApp", "--via", "send-input", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    // COR-01 — SEC-02: --allow-system-keys with post-message is a no-op (exit 0, warning emitted)
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_WithPostMessage_IsNoOpAndWarns()
    {
        // post-message is already window-scoped and never blocks system combos, so --allow-system-keys
        // has no effect with it. The command must succeed (exit 0) and still deliver the keystrokes;
        // a warning is logged but the exit code stays 0.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "post-message", "--allow-system-keys"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "keys should still be sent via post-message");
        Assert.AreEqual(KeyTransport.PostMessage, _fakeKeyboard.SendCalls[0].Transport);
    }

    // M1: --allow-system-keys + --json + post-message → no-op warning visible in JSON warnings array
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_PostMessage_Json_WarningInResult()
    {
        // --json consumers see the no-op warning even though the global logger is suppressed in JSON mode.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--via", "post-message", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(), "one no-op warning expected in JSON");
        StringAssert.Contains(warnings[0].GetString(), "--allow-system-keys");
        StringAssert.Contains(warnings[0].GetString(), "post-message");
    }

    // M1: --allow-system-keys + --json + send-input + system combo → audit warning visible in JSON
    [TestMethod]
    public async Task SendKeys_AllowSystemKeys_SendInput_Json_AuditWarningInResult()
    {
        // --json consumers see the injection audit trail even though the global logger is suppressed.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+v", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        var warnings = result.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength(), "one audit warning expected in JSON");
        StringAssert.Contains(warnings[0].GetString(), "--allow-system-keys");
        StringAssert.Contains(warnings[0].GetString(), "win+<key>");
    }

    // M1: no --allow-system-keys flag → warnings array is empty in JSON
    [TestMethod]
    public async Task SendKeys_NoAllowFlag_Json_WarningsIsEmpty()
    {
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+a", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0, result.GetProperty("warnings").GetArrayLength(), "no warnings when flag is absent");
    }

    // LOW: cmd alias for win — cmd+l stays hard-blocked even with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_CmdL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // cmd is an alias for win in the key grammar; cmd+l must be blocked unconditionally.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["cmd+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "cmd+l must never reach the keyboard transport");
    }

    // LOW: win+shift+l (extra modifier alongside never-bypassable) stays hard-blocked
    [TestMethod]
    public async Task SendKeys_WinShiftL_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // Extra modifiers do not defeat the win+l hard block — the guard sees win modifier + VkL.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+shift+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "win+shift+l must never reach the keyboard transport");
    }

    // #656 — ctrl+alt+del (SAS) must be hard-blocked like win+l, even with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_CtrlAltDel_ViaSendInput_WithAllowSystemKeys_IsStillRefused()
    {
        // ctrl+alt+del is a Secure Attention Sequence: Windows drops synthesized SAS input regardless of
        // privilege or flag, so it can never take effect. It must be refused (exit 1) rather than report a
        // misleading success — even when --allow-system-keys is passed — and never reach the transport.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+alt+del", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "ctrl+alt+del must never reach the keyboard transport");
        // Asserting only the exit code would still pass if the JSON envelope were dropped or carried the
        // wrong code/reason. The change promises invalid_arguments + a SAS explanation, so inspect both:
        // --json consumers must get an honest, actionable error instead of silence or a misleading success.
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        var errorInfo = error.GetProperty("error");
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            errorInfo.GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for the ctrl+alt+del SAS refusal");
        var message = errorInfo.GetProperty("message").GetString();
        StringAssert.Contains(message, "ctrl+alt+del",
            "JSON error.message must name the refused combo");
        StringAssert.Contains(message, "SAS",
            "JSON error.message must explain the Secure Attention Sequence block, not just refuse");
    }

    // #656 — ctrl+alt+del without the flag is refused too, with a message explaining it can't be synthesized
    [TestMethod]
    public async Task SendKeys_CtrlAltDel_ViaSendInput_WithoutAllow_IsRefusedWithSasReason()
    {
        // Without --allow-system-keys the never-bypassable guard still fires first, and the error must
        // explain the SAS block (not just "pass --allow-system-keys", which would never help here).
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+alt+del", "-a", "TestApp", "--via", "send-input"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "ctrl+alt+del");
        StringAssert.Contains(ConsoleStdErr.ToString(), "SAS");
    }

    // #656 — post-message is window-scoped, so ctrl+alt+del is not hard-blocked there (it just posts)
    [TestMethod]
    public async Task SendKeys_CtrlAltDel_ViaPostMessage_StillSends()
    {
        // The never-bypassable guard only applies to OS-wide send-input; post-message posts straight to the
        // target HWND's queue (a posted ctrl+alt+del is harmless), so it is unaffected and still sends.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+alt+del", "-a", "TestApp", "--via", "post-message", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
    }

    // #656 — a single send-input invocation containing BOTH hard-blocked combos must compose both
    // names AND both distinct reasons. Guard-level ordering is covered in SystemKeyGuardTests; this
    // asserts the command-level message actually stitches each combo to its own reason (SAS vs. lock),
    // not just the first one.
    [TestMethod]
    public async Task SendKeys_CtrlAltDelAndWinL_ViaSendInput_ComposesBothNamesAndReasons()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["ctrl+alt+del win+l", "-a", "TestApp", "--via", "send-input", "--allow-system-keys"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "neither hard-blocked combo may reach the transport");
        var stderr = ConsoleStdErr.ToString();
        // Both combo names must appear...
        StringAssert.Contains(stderr, "ctrl+alt+del", "message must name ctrl+alt+del");
        StringAssert.Contains(stderr, "win+l", "message must name win+l");
        // ...each stitched to its OWN distinct reason: SAS for ctrl+alt+del, workstation-lock for win+l.
        StringAssert.Contains(stderr, "SAS", "message must carry the ctrl+alt+del SAS reason");
        StringAssert.Contains(stderr, "locks the workstation", "message must carry the win+l lock reason");
    }
    [TestMethod]
    public async Task SendKeys_LoneRWin_ViaSendInput_WithoutAllow_IsBlocked()
    {
        // The right-Win key (VkRWin = 0x5c) opens Start — refused without --allow-system-keys.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["vk=0x5c", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count, "lone right-Win key must be blocked without --allow-system-keys");
    }

    // LOW: lone right-Win key (vk=0x5c) IS allowed with --allow-system-keys
    [TestMethod]
    public async Task SendKeys_LoneRWin_ViaSendInput_WithAllow_Sends()
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["vk=0x5c", "-a", "TestApp", "--via", "send-input", "--allow-system-keys", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count, "right-Win key should be sent when opted in");
        Assert.AreEqual(KeyTransport.SendInput, _fakeKeyboard.SendCalls[0].Transport);
    }

    // LOW: system combo refused without flag → error text contains --allow-system-keys
    [TestMethod]
    public async Task SendKeys_SystemCombo_Refused_ErrorMentionsAllowFlag()
    {
        // The logged error message must guide the caller to --allow-system-keys so the fix is actionable.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["win+r", "-a", "TestApp", "--via", "send-input"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--allow-system-keys");
    }

}
