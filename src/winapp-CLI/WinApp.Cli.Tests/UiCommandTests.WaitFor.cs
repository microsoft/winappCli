// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <c>winapp ui wait-for</c>: missing-app / missing-selector, the --gone branch (JSON and
/// non-JSON), --value matching via an explicit --property and via the smart get-text fallback
/// (with --contains), the JSON timeout envelope, cancellation, and the COMException / generic
/// error branches. All fast and deterministic — tiny timeouts or first-poll resolution.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task WaitFor_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Gone_NonJson_Succeeds()
    {
        _fakeUia.FindSingleResult = null; // absent → disappeared on first poll
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--gone", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Gone_Json_Succeeds()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--gone", "--json", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"found\": false");
    }

    [TestMethod]
    public async Task WaitFor_Value_WithProperty_NonJson_Matches()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Edit", Name = "Field", Selector = "edit-1" };
        _fakeUia.PropertiesResult = new Dictionary<string, object?> { ["Name"] = "Ready" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["edit-1", "-a", "TestApp", "--value", "Ready", "--property", "Name", "--timeout", "1000"]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Value_SmartFallback_Contains_Json_Matches()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Document", Name = "Doc", Selector = "doc-1" };
        _fakeUia.GetTextResult = "hello world";

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["doc-1", "-a", "TestApp", "--value", "ELLO", "--contains", "--json", "--timeout", "1000"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"found\": true");
    }

    [TestMethod]
    public async Task WaitFor_Timeout_Json_ReturnsError()
    {
        _fakeUia.FindSingleResult = null; // never appears
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--json", "--timeout", "150"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"timedOut\": true");
    }

    [TestMethod]
    public async Task WaitFor_Cancelled_ReturnsError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled → ThrowIfCancellationRequested trips on the first poll
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--timeout", "1000"], cts.Token);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Com_ReturnsError()
    {
        // FindSingle exceptions are swallowed by the inner catch, so drive the COM path via GetText
        // (the smart-fallback value read), which is inside the outer try.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Document", Name = "Doc" };
        _fakeUia.GetTextThrow = FakeComException;

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--value", "x", "--timeout", "1000"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#el", "-a", "TestApp", "--timeout", "1000"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---- Retry-loop continuations (the poll-again paths), driven deterministically through the
    //      IPollDelay seam + stateful fakes so no wall-clock timing is involved. -----------------

    [TestMethod]
    public async Task WaitFor_Appear_TransientFindFailure_KeepsPollingThenSucceeds()
    {
        // The first poll's FindSingleElementAsync throws (element not ready yet); the loop's per-poll
        // catch swallows it (element = null) and keeps polling. The element resolves on the next poll,
        // so wait-for succeeds — covers the catch → keep-polling continuation.
        _fakeUia.FindSingleThrowCount = 1; // throw once, then behave normally
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK", Selector = "btn-ok" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok", "-a", "TestApp", "--timeout", "2000"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePollDelay.CallCount >= 1,
            "the transient failure must drive at least one keep-polling continuation");
    }

    [TestMethod]
    public async Task WaitFor_Gone_PresentThenAbsent_KeepsPollingUntilGone()
    {
        // --gone with the element still present on the first poll: the loop must NOT return yet (present
        // ≠ gone) and keep polling; when it disappears on the next poll wait-for succeeds — covers the
        // gone-branch present → keep-polling continuation.
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "Window", Selector = "panel-1" }); // present on poll 1
        seq.Enqueue(null);                                                               // gone on poll 2
        _fakeUia.MovingResults["panel-1"] = seq;

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["panel-1", "-a", "TestApp", "--gone", "--timeout", "2000"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePollDelay.CallCount >= 1,
            "the still-present element must drive a keep-polling continuation before it disappears");
    }

    [TestMethod]
    public async Task WaitFor_Value_ChangesAcrossPolls_KeepsPollingUntilMatch()
    {
        // --value with the smart get-text fallback: the value is "old" on the first poll (no match →
        // keep polling) and becomes "target" on the next poll → match → success. Covers the
        // value-not-yet-matched continuation the first-poll-match tests never reach.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Edit", Name = "Field", Selector = "edit-1" };
        _fakeUia.GetTextResults.Enqueue("old");    // poll 1 — no match
        _fakeUia.GetTextResults.Enqueue("target"); // poll 2 — match

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["edit-1", "-a", "TestApp", "--value", "target", "--json", "--timeout", "2000"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"found\": true");
        Assert.IsTrue(_fakePollDelay.CallCount >= 1,
            "the value-not-yet-matched poll must drive a keep-polling continuation");
    }
}
