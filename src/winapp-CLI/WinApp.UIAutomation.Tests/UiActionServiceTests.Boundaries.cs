// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class UiActionServiceTests
{
    [TestMethod]
    public void Record_ObservationAgeUsesOneClockAndTheExactLimit()
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        var start = frequency * 2;
        UiActionService.CheckObservationTimestamp(start, start + frequency / 2);
        Assert.AreEqual("input_backlog", Assert.ThrowsExactly<UiActionException>(
            () => UiActionService.CheckObservationTimestamp(start, start + frequency / 2 + 1)).Code);
        Assert.AreEqual("input_backlog", Assert.ThrowsExactly<UiActionException>(
            () => UiActionService.CheckObservationTimestamp(start, start + frequency)).Code);
        Assert.AreEqual("input_timestamp", Assert.ThrowsExactly<UiActionException>(
            () => UiActionService.CheckObservationTimestamp(0, start)).Code);
        Assert.AreEqual("input_timestamp", Assert.ThrowsExactly<UiActionException>(
            () => UiActionService.CheckObservationTimestamp(start + 1, start)).Code);
    }

    [TestMethod]
    public async Task Record_ElapsedTimeCannotHideAnOldProducerTimestamp()
    {
        using var h = new Harness();
        h.Source.Events.ObservationTimestamp = _ =>
            System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency;
        h.Source.Add(Key(0x20) with { ElapsedMs = 3600000 });

        AssertStopped(await h.RecordAsync(), "input_backlog");
    }

    [TestMethod]
    public async Task Record_EditBatchValidatesEveryProducerTimestamp()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        var events = 0;
        h.Source.Events.ObservationTimestamp = _ => ++events == 1
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        h.Source.Add(Key(0x41), Key(0x42));

        AssertStopped(await h.RecordAsync(), "input_timestamp");
    }

    [TestMethod]
    public async Task Record_QueuedScopedInputCannotDisappearAfterForegroundLoss()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));
        h.Source.Add(() => h.Environment.IsForeground = false, Key(0x09, UiActionModifiers.Alt));

        var result = await h.RecordAsync();

        AssertStopped(result, "input_scope_changed");
        Assert.HasCount(1, result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_EditingScopeLossCannotDiscardQueuedNavigation()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () =>
        {
            if (h.Environment.FocusedCalls == 2)
            {
                Assert.IsTrue(h.Source.Events.Channel.Writer.TryWrite(Key(0x09, UiActionModifiers.Alt)));
                h.Environment.IsForeground = false;
            }
        };
        h.Source.Add(Key(0x41));

        var result = await h.RecordAsync();

        AssertStopped(result, "input_scope_changed");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_SettlingScopeLossCannotDiscardQueuedNavigation()
    {
        using var h = new Harness();
        h.Environment.OnFocused = () =>
        {
            if (h.Environment.FocusedCalls == 3)
            {
                Assert.IsTrue(h.Source.Events.Channel.Writer.TryWrite(Key(0x09, UiActionModifiers.Alt)));
                h.Environment.IsForeground = false;
            }
        };
        h.Source.Add(Key(0x20));

        var result = await h.RecordAsync();

        Assert.AreEqual("incomplete", result.Flow.Status);
        Assert.HasCount(1, result.Flow.Steps);
    }

    [TestMethod]
    public async Task Replay_CancelsAfterTheFinalTextFocusCheckBeforeTyping()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () =>
        {
            if (h.Environment.FocusedCalls == 6)
            {
                h.Cancellation.Cancel();
            }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.ReplayAsync(Flow(TextStep("do not deliver"))));

        Assert.HasCount(1, h.Environment.SentKeys);
        AssertChord(h.Environment.SentKeys[0], 0x41, 0x11);
    }

    [TestMethod]
    public async Task Replay_PassesCancellationToTheNativeInputEnvironment()
    {
        using var h = new Harness();

        await h.ReplayAsync(Flow(KeyStep()));

        Assert.AreEqual(h.Cancellation.Token, h.Environments.Tokens.Single());
    }

    [TestMethod]
    public async Task Replay_MalformedLaterControlTypeIsRejectedBeforeTheFirstAction()
    {
        using var h = new Harness();
        var flow = Flow(ClickStep(), KeyStep(target: Button with { ControlType = "button" }));

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(flow));

        Assert.AreEqual("invalid_flow", error.Code);
        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Environment.Clicks);
    }
}
